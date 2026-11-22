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
  lib/std/unicode/normalize-table.protobjo          the version and the counts
  lib/std/unicode/normalize-*.bin                   the tables, below
  TestFiles/assets/unicode/normalize-cases.protobjo  the conformance cases

The module reads the .bin files with (include-bytes ...), so they are
compiled into its assembly. Numbers are little-endian.

The property of a code point is 14 bits:
  bits 0-7    Canonical_Combining_Class
  bit  8      NFD_Quick_Check is No: it has a canonical decomposition
  bit  9      NFKD_Quick_Check is No: it has a decomposition of either kind
  bits 10-11  NFC_Quick_Check: 0 Yes, 1 Maybe, 2 No
  bits 12-13  NFKC_Quick_Check: 0 Yes, 1 Maybe, 2 No

The property table has two stages of 256-entry blocks, as in
gen_grapheme.py: stage 1 maps `cp >> 8` to a block, and stage 2 holds the
blocks, one byte an entry: the index of its property in the properties that
occur.
  normalize-stage1.bin    stage 1, a byte a block
  normalize-stage2.bin    stage 2, a byte a code point
  normalize-kinds.bin     the properties that occur, int32

The decompositions are full: applied again until nothing decomposes. There
are two tables, canonical and compatibility, each of int32 in three parts:
the code points that decompose, in order (count of them); the start of each
one's decomposition in the third part (count + 1 of them, the last the
end); and the code points of the decompositions. Hangul syllables are not in
them: they decompose by arithmetic.
  normalize-canonical.bin
  normalize-compatibility.bin

The compositions are the primary composites: the canonical decompositions
of two code points that are not in Full_Composition_Exclusion, ordered by
the first and then the second.
  normalize-composition-keys.bin   first * 0x110000 + second, int64
  normalize-composites.bin         the composite of each, int32
"""

import os
import struct
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
    def mapping_ints(table):
        """keys, then starts (one more than the keys), then targets."""
        keys, starts, targets = [], [], []
        for cp, v in sorted(table.items()):
            keys.append(cp)
            starts.append(len(targets))
            targets.extend(v)
        starts.append(len(targets))
        return keys + starts + targets

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

;; The number of code points with a full canonical decomposition, Hangul
;; syllables left out.
(def canonical-count {len(canon)})

;; The number of code points with a full compatibility decomposition, Hangul
;; syllables left out.
(def compatibility-count {len(compat)})

;; The number of primary composites.
(def composition-count {len(compositions)})
'''
    here = os.path.join(ROOT, 'lib', 'std', 'unicode')

    def write(name, fmt, values):
        with open(os.path.join(here, name), 'wb') as f:
            f.write(struct.pack(f'<{len(values)}{fmt}', *values))

    write('normalize-stage1.bin', 'B', stage1)
    write('normalize-stage2.bin', 'B', [kind_of[p] for p in stage2])
    write('normalize-kinds.bin', 'i', kinds)
    write('normalize-canonical.bin', 'i', mapping_ints(canon))
    write('normalize-compatibility.bin', 'i', mapping_ints(compat))
    write('normalize-composition-keys.bin', 'q', [a * 0x110000 + b for a, b, _ in compositions])
    write('normalize-composites.bin', 'i', [c for _, _, c in compositions])

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
