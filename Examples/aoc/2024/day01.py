"""Day 1: Historian Hysteria. Part 1 pairs the two sorted columns and sums the
distances. Part 2 sums each left ID times its count in the right column."""
import sys
from collections import Counter

pairs = [tuple(map(int, l.split())) for l in open(sys.argv[1]) if l.strip()]
left = sorted(a for a, _ in pairs)
right = sorted(b for _, b in pairs)
print(sum(abs(a - b) for a, b in zip(left, right)))
counts = Counter(right)
print(sum(a * counts[a] for a in left))
