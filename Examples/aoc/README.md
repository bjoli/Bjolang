# Advent of Code in Bjolang, OCaml and Python

Solutions to [Advent of Code](https://adventofcode.com/) 2025 (all twelve
days) and 2024 (days 1 to 5). Each day has a Bjolang solution, `dayNN.bjo`.
Beside it are an OCaml solution, `dayNN.ml`, and a Python solution, `dayNN.py`.
All three use the same algorithm. The OCaml uses only the standard library and
`Str`.

The inputs are not here: each player gets their own. `examples/` has the
worked examples from the puzzle texts and their answers.

```sh
./2025/run.sh            # build every day and check all three languages
./2025/run.sh 03 07      # only these days
dotnet 2025/day03.exe input.txt
./2025/day03.native input.txt
python3 2025/day03.py input.txt
```

`common.bjo` holds what the Bjolang solutions share: reading the input
file and parsing numbers.

Day 8 of 2025 connects 10 pairs when there are fewer than 100 junction boxes
(the example) and 1000 otherwise (a real input).

## Solutions with vectors

Five days also have a `dayNN-avx.bjo`: the same puzzle, solved with the
lane types of `(std simd)` and the array kernels of `(std array)`. They
show where vectors help in a puzzle and where they do not. `run.sh` checks
them on the examples too.

- **2024 day 4**: 32 cells of the word search at once. Each direction is
  four loads and four byte compares.
- **2025 day 4**: the neighbours of 32 cells are eight loads at fixed
  offsets, added.
- **2025 day 7**: 4 columns of beam counts at once, with the splitters as a
  mask.
- **2025 day 9**: the areas of 4 rectangles at once, and whether any of 4
  sides of the loop crosses a rectangle.
- **2024 day 1**: the distances of 4 pairs at once, and `array-count` for
  part 2.

Each program was built with `--aot --aot-cpu native`, and the time is the
whole process, best of 15, on an AVX2 machine with one real-sized input.
A native program that does nothing takes about 4.5 ms:

| day   | scalar  | avx    |
|-------|---------|--------|
| 24-01 | 5.1 ms  | 5.1 ms |
| 24-04 | 7.6 ms  | 4.7 ms |
| 25-04 | 14.6 ms | 4.7 ms |
| 25-07 | 5.7 ms  | 5.0 ms |
| 25-09 | 19.1 ms | 6.9 ms |

Part of that gain is not the vectors. Each `-avx` solution also uses a
different layout: flat arrays with a margin of zeros, instead of a `Vec` of
records and a bounds test per access. To tell the two apart, a third version
of day 4 and day 9 of 2025 used the flat layout one element at a time:

| day                                  | original | flat, scalar | flat, vectors |
|--------------------------------------|----------|--------------|---------------|
| 25-04, a 3000x3000 grid, no parsing  | 4.5 s    | 1.66 s       | 0.18 s        |
| 25-09, the real input, no startup    | 14.5 ms  | 3.9 ms       | 2.4 ms        |

- On day 4 the layout gives 2.7x and the vectors 9.2x. A vector holds 32
  cells, so 32x is the limit. Part 2 makes 82 passes over a 9 MB grid,
  which is too big for the core's own cache, and each of the nine loads
  per vector checks its bounds.
- On day 9 the layout gives 3.7x and the vectors only 1.6x, of a possible
  4x. The test stops at the first side that crosses the rectangle, so a
  scalar loop that finds one early does little work, and its `and` stops
  at the first comparison that fails. The vector loop always tests 4 sides
  with four comparisons each, and makes 4 broadcasts per rectangle.
- On days 24-01 and 25-07 the inputs are small, and reading them takes
  almost all of the time.

So vectors pay most on dense work over a large grid that does not stop
early.

On .NET the JIT takes most of the time for an input this small, and a
vector solution has more methods to compile. `bench/bench.py` shows the
`-avx` times in a column of their own. On days 24-01 and 25-07 the vector
solution was the slower of the two there.

Day 8 of 2025 has no vector solution. Its half a million pairs are packed
into longs and sorted, and neither step works on lanes.
