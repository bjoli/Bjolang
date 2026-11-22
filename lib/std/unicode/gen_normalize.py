#!/usr/bin/env python3
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.

"""Generates the normalization tables of (std unicode normalize) from the UCD.

Usage: gen_normalize.py UCD_DIR

UCD_DIR holds, from https://www.unicode.org/Public/UCD/latest/ucd/:
  UnicodeData.txt
  CompositionExclusions.txt
  DerivedNormalizationProps.txt
  NormalizationTest.txt

Writes:
  lib/std/unicode/normalize-table.protobjo          the tables
  TestFiles/assets/unicode/normalize-cases.protobjo  the conformance cases

The property of a code point is 14 bits:
  bits 0-7    Canonical_Combining_Class
  bit  8      NFD_Quick_Check is No: it has a canonical decomposition
  bit  9      NFKD_Quick_Check is No: it has a decomposition of either kind
  bits 10-11  NFC_Quick_Check: 0 Yes, 1 Maybe, 2 No
  bits 12-13  NFKC_Quick_Check: 0 Yes, 1 Maybe, 2 No

The property table has two stages of 256-entry blocks, as in
gen_grapheme.py: stage 1 maps `cp >> 8` to a block, and stage 2 holds the
blocks, one character per entry: the index of its property in a list of
the properties that occur, plus '0'.

The decompositions are full: applied again until nothing decomposes. There
are two tables, canonical and compatibility. Each entry is the code point in
6 hex digits, the length in 2, and the code points in 6 each, in code point
order. Hangul syllables are not in them: they decompose by arithmetic.

The compositions are the primary composites: the canonical decompositions
of two code points that are not in Full_Composition_Exclusion. Each entry
is the first code point, the second and the composite, in 6 hex digits
each, ordered by the first and then the second.
"""

import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

N = 0x110000
BLOCK = 256
QC = {'Y': 0, 'M': 1, 'N': 2}

# Hangul syllables, as in the Unicode Standard, section 3.12.
S_BASE, S_COUNT = 0xAC00, 11172


def find(ucd, name):
    p = os.path.join(ucd, name)
    if not os.path.exists(p):
        sys.exit(f'gen_normalize.py: no {name} in {ucd}')
    return p


def version(path):
    with open(path, encoding='utf-8') as f:
        first = f.readline()
    # "# DerivedNormalizationProps-18.0.0.txt"
    return first.split('-')[-1].replace('.txt', '').strip()


def ranges(path):
    with open(path, encoding='utf-8') as f:
        for line in f:
            line = line.split('#')[0].strip()
            if not line:
                continue
            fields = [x.strip() for x in line.split(';')]
            lo, _, hi = fields[0].partition('..')
            yield int(lo, 16), int(hi or lo, 16), fields[1:]


def unicode_data(path):
    """The combining classes, and the decompositions: cp -> (compat?, cps)."""
    ccc = [0] * N
    decomp = {}
    first = None
    with open(path, encoding='utf-8') as f:
        for line in f:
            fields = line.rstrip('\n').split(';')
            cp = int(fields[0], 16)
            name = fields[1]
            # A range, as <CJK Ideograph, First> ... <..., Last>: all class 0, no
            # decompositions. Only the two ends are listed.
            if name.endswith(', First>'):
                first = cp
                continue
            if name.endswith(', Last>'):
                first = None
                continue
            ccc[cp] = int(fields[3])
            d = fields[5]
            if d:
                compat = d.startswith('<')
                if compat:
                    d = d.split('>', 1)[1]
                decomp[cp] = (compat, [int(x, 16) for x in d.split()])
    return ccc, decomp


def full(cp, decomp, compat):
    """The full decomposition of `cp`, without Hangul syllables."""
    if cp not in decomp:
        return [cp]
    kind, parts = decomp[cp]
    if kind and not compat:
        return [cp]
    out = []
    for p in parts:
        out.extend(full(p, decomp, compat))
    return out


def wrap(text, width=100, indent='    '):
    return '\n'.join(indent + text[i:i + width] for i in range(0, len(text), width))


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    ucd = sys.argv[1]
    ud = find(ucd, 'UnicodeData.txt')
    ce = find(ucd, 'CompositionExclusions.txt')
    dnp = find(ucd, 'DerivedNormalizationProps.txt')
    nt = find(ucd, 'NormalizationTest.txt')
    ver = version(dnp)
    for p in (ce, nt):
        if version(p) != ver:
            sys.exit(f'gen_normalize.py: {p} is version {version(p)}, not {ver}')

    ccc, decomp = unicode_data(ud)

    props = list(ccc)
    exclusions = set()
    for lo, hi, f in ranges(dnp):
        name = f[0]
        bit = {'NFD_QC': (8, None), 'NFKD_QC': (9, None), 'NFC_QC': (10, 1), 'NFKC_QC': (12, 1)}.get(name)
        if name == 'Full_Composition_Exclusion':
            exclusions.update(range(lo, hi + 1))
        if bit is None:
            continue
        shift, _ = bit
        value = QC[f[1]]
        if name in ('NFD_QC', 'NFKD_QC'):
            assert value == 2
            value = 1
        for c in range(lo, hi + 1):
            props[c] |= value << shift

    # The tables must agree with the quick checks, which the module trusts:
    # a code point is looked up in a table only when its quick check says No.
    hangul = range(S_BASE, S_BASE + S_COUNT)
    canon = {}
    compat = {}
    for cp in sorted(decomp):
        c = full(cp, decomp, False)
        k = full(cp, decomp, True)
        if c != [cp]:
            canon[cp] = c
        if k != [cp]:
            compat[cp] = k
    for cp in range(N):
        nfd_no = bool(props[cp] >> 8 & 1)
        nfkd_no = bool(props[cp] >> 9 & 1)
        assert nfd_no == (cp in canon or cp in hangul), hex(cp)
        assert nfkd_no == (cp in compat or cp in hangul), hex(cp)
    assert max(len(v) for v in compat.values()) <= 255

    compositions = []
    for cp, (kind, parts) in decomp.items():
        if not kind and len(parts) == 2 and cp not in exclusions:
            compositions.append((parts[0], parts[1], cp))
    compositions.sort()
    # The second of a pair is NFC_QC Maybe, which is what lets the module
    # skip the search for every other code point.
    for a, b, _ in compositions:
        assert props[b] >> 10 & 3 == 1, hex(b)

    kinds = sorted(set(props))
    assert len(kinds) <= 78, len(kinds)
    kind_of = {p: i for i, p in enumerate(kinds)}
    blocks, stage1 = {}, []
    for b in range(0, N, BLOCK):
        key = tuple(props[b:b + BLOCK])
        stage1.append(blocks.setdefault(key, len(blocks)))
    assert len(blocks) <= 256, len(blocks)
    stage2 = [p for block in sorted(blocks, key=blocks.get) for p in block]
    s1 = ''.join(f'{i:02x}' for i in stage1)
    s2 = ''.join(chr(ord('0') + kind_of[p]) for p in stage2)
    sk = ''.join(f'{p:04x}' for p in kinds)

    def mapping_text(table):
        return ''.join(f'{cp:06x}{len(v):02x}' + ''.join(f'{x:06x}' for x in v) for cp, v in sorted(table.items()))

    comp_text = ''.join(f'{a:06x}{b:06x}{c:06x}' for a, b, c in compositions)

    header = f''';; Generated by lib/std/unicode/gen_normalize.py from the Unicode Character
;; Database {ver}: UnicodeData.txt, CompositionExclusions.txt and
;; DerivedNormalizationProps.txt. Do not edit; run the script again instead.
;;
;; The data is © Unicode, Inc., under the Unicode License v3:
;; https://www.unicode.org/license.txt
'''
    table = header + f'''
;; The Unicode version the tables are of.
(def unicode-version "{ver}")

;; Stage 1: the block of each 256 code points, as two hex digits.
(def stage1-text """
{wrap(s1)}
    """)

;; The properties that occur, as four hex digits each: the combining class
;; in bits 0-7, NFD_QC No in bit 8, NFKD_QC No in bit 9, NFC_QC in bits
;; 10-11 and NFKC_QC in bits 12-13 (0 Yes, 1 Maybe, 2 No).
(def kinds-text "{sk}")

;; Stage 2: the blocks, one character per code point, the index of its
;; property in kinds-text plus '0'.
(def stage2-text """
{wrap(s2)}
    """)

;; The number of code points with a full canonical decomposition, Hangul
;; syllables left out.
(def canonical-count {len(canon)})

;; The full canonical decompositions, in code point order. Each is the code
;; point in 6 hex digits, the length in 2, and the code points in 6 each.
(def canonical-text """
{wrap(mapping_text(canon))}
    """)

;; The number of code points with a full compatibility decomposition, Hangul
;; syllables left out.
(def compatibility-count {len(compat)})

;; The full compatibility decompositions, in the same form.
(def compatibility-text """
{wrap(mapping_text(compat))}
    """)

;; The number of primary composites.
(def composition-count {len(compositions)})

;; The primary composites: the first code point, the second and the
;; composite, in 6 hex digits each, ordered by the first and the second.
(def composition-text """
{wrap(comp_text)}
    """)
'''
    out = os.path.join(ROOT, 'lib', 'std', 'unicode', 'normalize-table.protobjo')
    with open(out, 'w', encoding='utf-8') as f:
        f.write(table)

    # The conformance cases. A field that is the same as an earlier field of
    # its line is written as that field's number; the others as the code
    # points in hex, separated by '.'.
    cases = []
    part1 = []
    part = None
    with open(nt, encoding='utf-8') as f:
        for line in f:
            if line.startswith('@Part'):
                part = line.split()[0]
                continue
            line = line.split('#')[0].strip()
            if not line:
                continue
            fields = line.rstrip(';').split(';')[:5]
            out_fields = []
            for i, x in enumerate(fields):
                j = next((k for k in range(i) if fields[k] == x), None)
                out_fields.append(str(j) if j is not None else '.'.join(x.split()))
            cases.append(';'.join(out_fields))
            if part == '@Part1':
                part1.append(int(fields[0], 16))
    part1.sort()
    test = header.replace('UnicodeData.txt, CompositionExclusions.txt and\n;; DerivedNormalizationProps.txt',
                          'NormalizationTest.txt') + f'''
;; NormalizationTest.txt, one case a line: c1 to c5 separated by ';'. A
;; field that is the same as an earlier one of its line is that field's
;; number, from 0. The others are code points in hex, separated by '.'.
(def normalize-cases-version "{ver}")
(def normalize-cases """
{chr(10).join('    ' + c for c in cases)}
    """)

;; The code points of part 1, in hex, in order. Every other code point is
;; its own NFC, NFD, NFKC and NFKD.
(def normalize-part1 """
{chr(10).join('    ' + ' '.join(f'{c:x}' for c in part1[i:i + 16]) for i in range(0, len(part1), 16))}
    """)
'''
    os.makedirs(os.path.join(ROOT, 'TestFiles', 'assets', 'unicode'), exist_ok=True)
    out2 = os.path.join(ROOT, 'TestFiles', 'assets', 'unicode', 'normalize-cases.protobjo')
    with open(out2, 'w', encoding='utf-8') as f:
        f.write(test)
    print(f'Unicode {ver}: {len(kinds)} properties in {len(blocks)} blocks, '
          f'{len(canon)} canonical and {len(compat)} compatibility decompositions, '
          f'{len(compositions)} compositions, {len(cases)} test cases')


if __name__ == '__main__':
    main()
