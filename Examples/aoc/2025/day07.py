"""Day 7: Laboratories. A beam falls from S; a splitter (^) starts two beams,
one column to each side. Part 1 counts the splitters a beam reaches, part 2
the timelines."""
import sys

rows = [l.rstrip("\n") for l in open(sys.argv[1]) if l.strip()]
w = len(rows[0])
beams = [1 if c == "S" else 0 for c in rows[0]]
splits = 0
for row in rows:
    nxt = [0] * w
    for x, n in enumerate(beams):
        if not n:
            continue
        if row[x] == "^":
            splits += 1
            if x > 0:
                nxt[x - 1] += n
            if x + 1 < w:
                nxt[x + 1] += n
        else:
            nxt[x] += n
    beams = nxt
print(splits)
print(sum(beams))
