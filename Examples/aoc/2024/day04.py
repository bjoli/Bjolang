"""Day 4: Ceres Search. Part 1 counts XMAS in all eight directions. Part 2
counts the crosses of two MAS on the diagonals of a 3x3 square."""
import sys

rows = [l.strip() for l in open(sys.argv[1]) if l.strip()]
h, w = len(rows), len(rows[0])


def at(x, y):
    return rows[y][x] if 0 <= x < w and 0 <= y < h else "."


dirs = [(dx, dy) for dx in (-1, 0, 1) for dy in (-1, 0, 1) if dx or dy]
print(sum(all(at(x + i * dx, y + i * dy) == c for i, c in enumerate("XMAS"))
          for y in range(h) for x in range(w) for dx, dy in dirs))


def mas(x, y, dx, dy):
    return {at(x - dx, y - dy), at(x + dx, y + dy)} == {"M", "S"}


print(sum(at(x, y) == "A" and mas(x, y, 1, 1) and mas(x, y, 1, -1)
          for y in range(h) for x in range(w)))
