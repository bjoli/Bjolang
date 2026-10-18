"""Day 8: Playground. Connect junction boxes, closest pairs first. Part 1:
after K pairs (10 for the example, 1000 for a real input), the product of the
three largest circuits. Part 2: the product of the X coordinates of the last
pair that makes one circuit."""
import math
import sys

boxes = [tuple(map(int, l.split(","))) for l in open(sys.argv[1]) if l.strip()]
n = len(boxes)
k = 10 if n < 100 else 1000
pairs = sorted((math.dist(boxes[i], boxes[j]), i, j) for i in range(n) for j in range(i + 1, n))
parent = list(range(n))
size = [1] * n


def root(i):
    while parent[i] != i:
        parent[i] = parent[parent[i]]
        i = parent[i]
    return i


def join(i, j):
    a, b = root(i), root(j)
    if a == b:
        return False
    parent[a] = b
    size[b] += size[a]
    return True


for _, i, j in pairs[:k]:
    join(i, j)
print(math.prod(sorted((size[i] for i in range(n) if root(i) == i), reverse=True)[:3]))
circuits = sum(root(i) == i for i in range(n))
for _, i, j in pairs[k:]:
    if join(i, j):
        circuits -= 1
        if circuits == 1:
            print(boxes[i][0] * boxes[j][0])
            break
