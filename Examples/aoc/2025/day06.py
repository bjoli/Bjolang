"""Day 6: Trash Compactor. Problems side by side, separated by blank columns,
with the operator in the last row. Part 1 reads each row of a problem as a
number; part 2 reads each column, its digits top to bottom."""
import math
import sys

lines = [l.rstrip("\n") for l in open(sys.argv[1]) if l.strip()]
width = max(map(len, lines))
rows = [l.ljust(width) for l in lines]
*digits, ops = rows
blank = [all(r[c] == " " for r in rows) for c in range(width)]

problems, start = [], 0
for c in range(width + 1):
    if c == width or blank[c]:
        if start < c:
            problems.append((start, c))
        start = c + 1


def combine(op, xs):
    return math.prod(xs) if op == "*" else sum(xs)


part1 = part2 = 0
for lo, hi in problems:
    op = ops[lo:hi].strip()
    part1 += combine(op, [int(r[lo:hi]) for r in digits if r[lo:hi].strip()])
    # The digits of the column, top to bottom; a space between them is skipped.
    part2 += combine(op, [int("".join(r[c] for r in digits).replace(" ", "")) for c in range(lo, hi)])
print(part1)
print(part2)
