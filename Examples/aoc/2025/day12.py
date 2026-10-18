"""Day 12: Christmas Tree Farm. Count the regions that all their presents fit
in, turned or flipped, without overlap. Most regions are settled by area; the
rest are searched square by square."""
import sys

blocks = open(sys.argv[1]).read().strip().split("\n\n")
shapes = []
for b in blocks[:-1]:
    rows = b.split("\n")[1:]
    shapes.append({(x, y) for y, r in enumerate(rows) for x, c in enumerate(r) if c == "#"})


def normalise(cells):
    x0 = min(x for x, _ in cells)
    y0 = min(y for _, y in cells)
    return tuple(sorted(((x - x0, y - y0) for x, y in cells), key=lambda c: (c[1], c[0])))


def orientations(cells):
    out = set()
    for _ in range(4):
        cells = {(-y, x) for x, y in cells}
        out.add(normalise(cells))
        out.add(normalise({(-x, y) for x, y in cells}))
    return list(out)


orients = [orientations(s) for s in shapes]


def search(w, h, counts, slack):
    grid = [False] * (w * h)

    def fits(o, x0, y0):
        return all(0 <= x0 + dx < w and 0 <= y0 + dy < h and not grid[x0 + dx + (y0 + dy) * w]
                   for dx, dy in o)

    def mark(o, x0, y0, v):
        for dx, dy in o:
            grid[x0 + dx + (y0 + dy) * w] = v

    def go(at, slack):
        if not any(counts):
            return True
        if at >= w * h:
            return False
        if grid[at]:
            return go(at + 1, slack)
        x, y = at % w, at // w
        for i, os in enumerate(orients):
            if counts[i] == 0:
                continue
            for o in os:
                ax = x - o[0][0]  # the shape's first cell goes on (x, y)
                if fits(o, ax, y):
                    mark(o, ax, y, True)
                    counts[i] -= 1
                    ok = go(at + 1, slack)
                    counts[i] += 1
                    mark(o, ax, y, False)
                    if ok:
                        return True
        if slack > 0:
            grid[at] = True
            ok = go(at + 1, slack - 1)
            grid[at] = False
            return ok
        return False

    return go(0, slack)


def fits_region(line):
    dims, rest = line.split(": ")
    w, h = map(int, dims.split("x"))
    counts = list(map(int, rest.split()))
    needed = sum(c * len(s) for c, s in zip(counts, shapes))
    if needed > w * h:
        return False
    if sum(counts) <= (w // 3) * (h // 3):
        return True
    return search(w, h, counts, w * h - needed)


print(sum(fits_region(l) for l in blocks[-1].split("\n")))
