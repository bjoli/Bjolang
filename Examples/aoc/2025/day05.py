"""Day 5: Cafeteria. Part 1 counts the IDs on hand that some range covers;
part 2 counts every ID the ranges cover."""
import sys

head, tail = open(sys.argv[1]).read().strip().split("\n\n")
ranges = sorted(tuple(map(int, l.split("-"))) for l in head.split())
ids = [int(l) for l in tail.split()]
print(sum(any(lo <= i <= hi for lo, hi in ranges) for i in ids))
covered, nxt = 0, 0  # nxt: the first ID not yet counted
for lo, hi in ranges:
    start = max(lo, nxt)
    covered += max(0, hi + 1 - start)
    nxt = max(nxt, hi + 1)
print(covered)
