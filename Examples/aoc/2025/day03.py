"""Day 3: Lobby. Turn on k batteries of each bank, in order, to make the
largest number: k = 2 in part 1, 12 in part 2. Sums over the banks."""
import sys


def joltage(ds, k):
    acc, start = 0, 0
    for j in range(k):
        # Leave room for the k - j - 1 digits still to pick.
        end = len(ds) - (k - j - 1)
        i = max(range(start, end), key=lambda i: (ds[i], -i))
        acc = acc * 10 + ds[i]
        start = i + 1
    return acc


banks = [[int(c) for c in l.strip()] for l in open(sys.argv[1]) if l.strip()]
print(sum(joltage(b, 2) for b in banks))
print(sum(joltage(b, 12) for b in banks))
