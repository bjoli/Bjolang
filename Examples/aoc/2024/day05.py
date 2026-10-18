"""Day 5: Print Queue. Part 1 sums the middle page of each update that obeys
the rules. Part 2 puts the others in order and sums their middle pages."""
import sys
from functools import cmp_to_key

head, tail = open(sys.argv[1]).read().strip().split("\n\n")
rules = set(head.split())
updates = [l.split(",") for l in tail.split()]


def compare(a, b):
    return -1 if f"{a}|{b}" in rules else 1 if f"{b}|{a}" in rules else 0


part1 = part2 = 0
for u in updates:
    ordered = sorted(u, key=cmp_to_key(compare))
    if ordered == u:
        part1 += int(u[len(u) // 2])
    else:
        part2 += int(ordered[len(u) // 2])
print(part1)
print(part2)
