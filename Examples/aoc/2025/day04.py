"""Day 4: Printing Department. A roll (@) is reachable with fewer than four
rolls among its eight neighbours. Part 1 counts them; part 2 removes
reachable rolls until none are left to reach."""
import sys

rolls = {(x, y) for y, l in enumerate(open(sys.argv[1])) for x, c in enumerate(l) if c == "@"}


def reachable():
    return [(x, y) for (x, y) in rolls
            if sum((x + dx, y + dy) in rolls
                   for dx in (-1, 0, 1) for dy in (-1, 0, 1) if dx or dy) < 4]


print(len(reachable()))
removed = 0
while cells := reachable():
    rolls.difference_update(cells)
    removed += len(cells)
print(removed)
