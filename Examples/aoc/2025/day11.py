"""Day 11: Reactor. Part 1 counts the paths from "you" to "out". Part 2 counts
the paths from "svr" to "out" through both "dac" and "fft"."""
import sys
from functools import cache

graph = {}
for line in open(sys.argv[1]):
    if line.strip():
        name, outs = line.split(": ")
        graph[name] = outs.split()


def count(source, target):
    @cache
    def paths(node):
        return 1 if node == target else sum(paths(n) for n in graph.get(node, []))
    return paths(source)


print(count("you", "out") if "you" in graph else 0)
print(count("svr", "dac") * count("dac", "fft") * count("fft", "out")
      + count("svr", "fft") * count("fft", "dac") * count("dac", "out")
      if "svr" in graph else 0)
