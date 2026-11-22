#!/usr/bin/env python3
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.

"""Generates the character tables of (std unicode chars) from the UCD.

Usage: gen_chars.py UCD_DIR

UCD_DIR holds, from https://www.unicode.org/Public/UCD/latest/ucd/:
  UnicodeData.txt
  DerivedCoreProperties.txt
  PropList.txt
  CaseFolding.txt

Writes:
  lib/std/unicode/chars-table.protobjo           the tables
  TestFiles/assets/unicode/chars-cases.protobjo   counts and sums, for the test

Each code point has a kind. A kind is:
  - its properties, 13 bits:
      bits 0-4   General_Category, as the index in CATEGORIES
      bit  5     Alphabetic
      bit  6     White_Space
      bit  7     Uppercase
      bit  8     Lowercase
      bits 9-12  the decimal digit value (Numeric_Type Decimal), or 15
  - four deltas: the simple uppercase, lowercase and titlecase mappings and
    the simple case fold (status C and S), each as the mapped code point
    minus the code point. 0 is no mapping.

The kind table has two stages of 256-entry blocks, as in gen_grapheme.py:
stage 1 maps `cp >> 8` to a block, and stage 2 holds the blocks, one kind
per code point. There are fewer than 256 kinds and 256 blocks, so both
stages are written as two hex digits an entry.
"""

import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))

N = 0x110000
BLOCK = 256
CATEGORIES = 'Lu Ll Lt Lm Lo Mn Mc Me Nd Nl No Pc Pd Ps Pe Pi Pf Po Sm Sc Sk So Zs Zl Zp Cc Cf Cs Co Cn'.split()
NO_DIGIT = 15
# A delta is written in 6 hex digits, plus this.
DELTA_BIAS = 0x800000


def find(ucd, name):
    p = os.path.join(ucd, name)
    if not os.path.exists(p):
        sys.exit(f'gen_chars.py: no {name} in {ucd}')
    return p


def version(path):
    with open(path, encoding='utf-8') as f:
        first = f.readline()
    # "# DerivedCoreProperties-18.0.0.txt"
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


def code_points(path, name):
    out = set()
    for lo, hi, f in ranges(path):
        if f[0] == name:
            out.update(range(lo, hi + 1))
    return out


def unicode_data(path):
    """The category, the decimal digit value and the three simple case
    mappings of every code point, as deltas."""
    cat = ['Cn'] * N
    digit = [NO_DIGIT] * N
    upper, lower, title = [0] * N, [0] * N, [0] * N
    first = None
    with open(path, encoding='utf-8') as f:
        for line in f:
            fields = line.rstrip('\n').split(';')
            cp = int(fields[0], 16)
            name = fields[1]
            # A range, as <CJK Ideograph, First> ... <..., Last>: only the
            # two ends are listed, and every code point in it is the same.
            if name.endswith(', First>'):
                first = cp
                continue
            span = range(first, cp + 1) if name.endswith(', Last>') else [cp]
            first = None
            for c in span:
                cat[c] = fields[2]
                if fields[6]:
                    digit[c] = int(fields[6])
                if fields[12]:
                    upper[c] = int(fields[12], 16) - c
                if fields[13]:
                    lower[c] = int(fields[13], 16) - c
                # An empty titlecase mapping is the uppercase one.
                title[c] = int(fields[14], 16) - c if fields[14] else upper[c]
    return cat, digit, upper, lower, title


def simple_fold(path):
    fold = [0] * N
    with open(path, encoding='utf-8') as f:
        for line in f:
            line = line.split('#')[0].strip()
            if not line:
                continue
            cp, status, mapping = [x.strip() for x in line.split(';')[:3]]
            if status in ('C', 'S'):
                fold[int(cp, 16)] = int(mapping, 16) - int(cp, 16)
    return fold


def wrap(text, width=100, indent='    '):
    return '\n'.join(indent + text[i:i + width] for i in range(0, len(text), width))


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    ucd = sys.argv[1]
    ud = find(ucd, 'UnicodeData.txt')
    dcp = find(ucd, 'DerivedCoreProperties.txt')
    pl = find(ucd, 'PropList.txt')
    cf = find(ucd, 'CaseFolding.txt')
    ver = version(dcp)
    for p in (pl, cf):
        if version(p) != ver:
            sys.exit(f'gen_chars.py: {p} is version {version(p)}, not {ver}')

    cat, digit, upper, lower, title = unicode_data(ud)
    fold = simple_fold(cf)
    alphabetic = code_points(dcp, 'Alphabetic')
    uppercase = code_points(dcp, 'Uppercase')
    lowercase = code_points(dcp, 'Lowercase')
    white = code_points(pl, 'White_Space')

    # The module trusts these.
    for c in range(N):
        assert digit[c] == NO_DIGIT or cat[c] == 'Nd', hex(c)
        assert cat[c] != 'Nd' or digit[c] != NO_DIGIT, hex(c)
    deltas = [d for t in (upper, lower, title, fold) for d in t]
    assert all(-DELTA_BIAS < d < DELTA_BIAS for d in deltas)

    def props(c):
        return (CATEGORIES.index(cat[c])
                | (c in alphabetic) << 5
                | (c in white) << 6
                | (c in uppercase) << 7
                | (c in lowercase) << 8
                | digit[c] << 9)

    kind_of_cp = [(props(c), upper[c], lower[c], title[c], fold[c]) for c in range(N)]
    kinds = sorted(set(kind_of_cp))
    assert len(kinds) <= 256, len(kinds)
    index = {k: i for i, k in enumerate(kinds)}
    blocks, stage1 = {}, []
    for b in range(0, N, BLOCK):
        key = tuple(index[k] for k in kind_of_cp[b:b + BLOCK])
        stage1.append(blocks.setdefault(key, len(blocks)))
    assert len(blocks) <= 256, len(blocks)
    stage2 = [i for block in sorted(blocks, key=blocks.get) for i in block]

    s1 = ''.join(f'{i:02x}' for i in stage1)
    s2 = ''.join(f'{i:02x}' for i in stage2)
    sk = ''.join(f'{p:04x}' + ''.join(f'{d + DELTA_BIAS:06x}' for d in ds) for (p, *ds) in kinds)

    header = f''';; Generated by lib/std/unicode/gen_chars.py from the Unicode Character
;; Database {ver}: UnicodeData.txt, DerivedCoreProperties.txt, PropList.txt
;; and CaseFolding.txt. Do not edit; run the script again instead.
;;
;; The data is © Unicode, Inc., under the Unicode License v3:
;; https://www.unicode.org/license.txt
'''
    table = header + f'''
;; The Unicode version the tables are of.
(def unicode-version "{ver}")

;; The general category of an index in bits 0-4 of a kind. A match and not
;; an array of symbols, so that initializing the module calls nothing.
(: (-> int Symbol))
(defun (category-symbol i)
  (match i
{chr(10).join(f"    ({i} '{g})" for i, g in enumerate(CATEGORIES[:-1]))}
    (_ '{CATEGORIES[-1]})))

;; The kinds, 28 hex digits each:
;; - 4: the properties. Bits 0-4 the general category, bit 5 Alphabetic,
;;   bit 6 White_Space, bit 7 Uppercase, bit 8 Lowercase, bits 9-12 the
;;   decimal digit value or 15.
;; - 6 each: the deltas of the simple uppercase, lowercase and titlecase
;;   mappings and of the simple case fold, plus {DELTA_BIAS:#x}.
(def kinds-text """
{wrap(sk, 112)}
    """)

;; Stage 1: the block of each 256 code points, as two hex digits.
(def stage1-text """
{wrap(s1)}
    """)

;; Stage 2: the blocks, the kind of each code point as two hex digits.
(def stage2-text """
{wrap(s2)}
    """)
'''
    out = os.path.join(ROOT, 'lib', 'std', 'unicode', 'chars-table.protobjo')
    with open(out, 'w', encoding='utf-8') as f:
        f.write(table)

    # What the test checks the module against: for each property, the number
    # of code points that have it and the sum of those code points; for each
    # mapping, the number of code points it changes and the sum of what they
    # map to. Surrogates are left out: they are not chars.
    scalars = [c for c in range(N) if not 0xD800 <= c <= 0xDFFF]

    def count_sum(pred):
        cs = [c for c in scalars if pred(c)]
        return len(cs), sum(cs)

    def mapped(t):
        cs = [c for c in scalars if t[c]]
        return len(cs), sum(c + t[c] for c in cs)

    rows = []
    for g in CATEGORIES:
        rows.append(('category ' + g,) + count_sum(lambda c, g=g: cat[c] == g))
    rows.append(('alphabetic',) + count_sum(lambda c: c in alphabetic))
    rows.append(('white-space',) + count_sum(lambda c: c in white))
    rows.append(('uppercase',) + count_sum(lambda c: c in uppercase))
    rows.append(('lowercase',) + count_sum(lambda c: c in lowercase))
    rows.append(('title-case',) + count_sum(lambda c: cat[c] == 'Lt'))
    rows.append(('numeric',) + count_sum(lambda c: cat[c] in ('Nd', 'Nl', 'No')))
    for d in range(10):
        rows.append((f'digit {d}',) + count_sum(lambda c, d=d: digit[c] == d))
    rows.append(('upcase',) + mapped(upper))
    rows.append(('downcase',) + mapped(lower))
    rows.append(('titlecase',) + mapped(title))
    rows.append(('foldcase',) + mapped(fold))

    test = header.replace('UnicodeData.txt, DerivedCoreProperties.txt, PropList.txt\n;; and CaseFolding.txt',
                          'the same files as chars-table.protobjo') + f'''
;; One line a property: its name, the number of chars that have it and the
;; sum of their code points, separated by ';'. For a mapping: the number of
;; chars it changes, and the sum of what they map to.
(def chars-cases-version "{ver}")
(def chars-cases """
{chr(10).join(f'    {name};{n};{s}' for name, n, s in rows)}
    """)
'''
    os.makedirs(os.path.join(ROOT, 'TestFiles', 'assets', 'unicode'), exist_ok=True)
    out2 = os.path.join(ROOT, 'TestFiles', 'assets', 'unicode', 'chars-cases.protobjo')
    with open(out2, 'w', encoding='utf-8') as f:
        f.write(test)
    print(f'Unicode {ver}: {len(kinds)} kinds in {len(blocks)} blocks')


if __name__ == '__main__':
    main()
