"""Day 1: Secret Entrance. A dial of 0..99 starts at 50. Part 1 counts the
turns that end at 0, part 2 every click that passes 0."""
import sys

lines = [l.strip() for l in open(sys.argv[1]) if l.strip()]
pos, zeros, clicks = 50, 0, 0
for line in lines:
    d = int(line[1:])
    if line[0] == "R":
        clicks += (pos + d) // 100
        pos = (pos + d) % 100
    else:
        if pos == 0:
            clicks += d // 100
        elif d >= pos:
            clicks += 1 + (d - pos) // 100
        pos = (pos - d) % 100
    zeros += pos == 0
print(zeros)
print(clicks)
