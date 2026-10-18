"""Day 2: Red-Nosed Reports. A report is safe when it rises or falls by 1 to 3
at each step. Part 2 also counts a report that is safe with one level removed."""
import sys


def safe(levels):
    steps = [b - a for a, b in zip(levels, levels[1:])]
    return all(1 <= d <= 3 for d in steps) or all(-3 <= d <= -1 for d in steps)


def dampened(levels):
    return safe(levels) or any(safe(levels[:i] + levels[i + 1:]) for i in range(len(levels)))


reports = [list(map(int, l.split())) for l in open(sys.argv[1]) if l.strip()]
print(sum(map(safe, reports)))
print(sum(map(dampened, reports)))
