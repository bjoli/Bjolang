"""Day 3: Mull It Over. Part 1 sums the products of the mul(X,Y)
instructions. Part 2 obeys do() and don't()."""
import re
import sys

memory = open(sys.argv[1]).read()
total = enabled = 0
on = True
for m in re.finditer(r"mul\((\d{1,3}),(\d{1,3})\)|do\(\)|don't\(\)", memory):
    if m[0] == "do()":
        on = True
    elif m[0] == "don't()":
        on = False
    else:
        product = int(m[1]) * int(m[2])
        total += product
        enabled += product if on else 0
print(total)
print(enabled)
