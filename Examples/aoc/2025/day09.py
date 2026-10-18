"""Day 9: Movie Theater. Red tiles in order around a loop of horizontal and
vertical sides. Part 1: the largest rectangle with red tiles at two opposite
corners. Part 2: the largest such rectangle inside the loop."""
import sys

ps = [tuple(map(int, l.split(","))) for l in open(sys.argv[1]) if l.strip()]
sides = list(zip(ps, ps[1:] + ps[:1]))


def crossed(x0, x1, y0, y1):
    """Whether a side passes through the open inside of the rectangle."""
    return any(min(a[0], b[0]) < x1 and max(a[0], b[0]) > x0 and
               min(a[1], b[1]) < y1 and max(a[1], b[1]) > y0 for a, b in sides)


def inside(px, py):
    """Whether (px + 1/2, py + 1/2) is inside, counting sides to its right."""
    return sum(a[0] == b[0] and a[0] > px and min(a[1], b[1]) <= py < max(a[1], b[1])
               for a, b in sides) % 2 == 1


part1 = part2 = 0
for i, a in enumerate(ps):
    for b in ps[i + 1:]:
        x0, x1 = sorted((a[0], b[0]))
        y0, y1 = sorted((a[1], b[1]))
        area = (x1 - x0 + 1) * (y1 - y0 + 1)
        part1 = max(part1, area)
        # A rectangle of one row or column has no inside to test.
        if area > part2 and x0 < x1 and y0 < y1 and not crossed(x0, x1, y0, y1) and inside(x0, y0):
            part2 = area
print(part1)
print(part2)
