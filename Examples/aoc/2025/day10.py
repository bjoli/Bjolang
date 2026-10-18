"""Day 10: Factory. Part 1: the fewest presses that light the pattern, a press
toggling its lights. Part 2: the fewest presses that bring each counter to its
target, a press adding one to its counters. Summed over the machines."""
import sys
from functools import cache
from itertools import product


def solve(line):
    toks = line.split()
    lights = [c == "#" for c in toks[0][1:-1]]
    buttons = [tuple(map(int, t[1:-1].split(","))) for t in toks[1:-1]]
    targets = tuple(map(int, toks[-1][1:-1].split(",")))
    k = len(targets)

    # Every subset of the buttons, pressed once each: what it adds, and how
    # many presses that is.
    presses = []
    for chosen in product((0, 1), repeat=len(buttons)):
        adds = [0] * k
        for on, b in zip(chosen, buttons):
            for c in b if on else ():
                adds[c] += 1
        presses.append((tuple(adds), sum(chosen)))

    toggles = min(n for adds, n in presses
                  if [a % 2 == 1 for a in adds[:len(lights)]] == lights)

    # The subsets grouped by the parity of what they add.
    by_parity = {}
    for adds, n in presses:
        by_parity.setdefault(tuple(a % 2 for a in adds), []).append((adds, n))

    # The buttons pressed an odd number of times give the targets' parity;
    # what is left is even, and is half the problem, pressed twice.
    @cache
    def fewest(t):
        if not any(t):
            return 0
        best = None
        for adds, n in by_parity.get(tuple(x % 2 for x in t), []):
            if all(a <= x for a, x in zip(adds, t)):
                rest = fewest(tuple((x - a) // 2 for a, x in zip(adds, t)))
                if rest is not None and (best is None or n + 2 * rest < best):
                    best = n + 2 * rest
        return best

    return toggles, fewest(targets)


answers = [solve(l) for l in open(sys.argv[1]) if l.strip()]
print(sum(a for a, _ in answers))
print(sum(b for _, b in answers))
