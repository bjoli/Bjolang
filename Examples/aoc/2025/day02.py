"""Day 2: Gift Shop. An ID is invalid when its digits are one block repeated:
exactly twice in part 1, at least twice in part 2. Sums the invalid IDs."""
import sys


def repeats(s, k):
    return all(s[i] == s[i - k] for i in range(k, len(s)))


def twice(s):
    return len(s) % 2 == 0 and repeats(s, len(s) // 2)


def some_repeat(s):
    return any(len(s) % k == 0 and repeats(s, k) for k in range(1, len(s) // 2 + 1))


part1 = part2 = 0
for r in open(sys.argv[1]).read().strip().split(","):
    lo, hi = map(int, r.split("-"))
    for n in range(lo, hi + 1):
        s = str(n)
        part1 += n if twice(s) else 0
        part2 += n if some_repeat(s) else 0
print(part1)
print(part2)
