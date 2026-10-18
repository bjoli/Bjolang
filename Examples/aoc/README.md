# Advent of Code in Bjolang and Python

Solutions to [Advent of Code](https://adventofcode.com/) 2025 (all twelve
days) and 2024 (days 1 to 5). Each day has a Bjolang solution, `dayNN.bjo`,
and a Python solution beside it, `dayNN.py`, which uses the same algorithm.

The inputs are not here: each player gets their own. `examples/` has the
worked examples from the puzzle texts and their answers.

```sh
./2025/run.sh            # build every day and check both languages
./2025/run.sh 03 07      # only these days
dotnet 2025/day03.exe input.txt
python3 2025/day03.py input.txt
```

`common.bjo` holds what the Bjolang solutions share: reading the input
file and parsing numbers.

Day 8 of 2025 connects 10 pairs when there are fewer than 100 junction boxes
(the example) and 1000 otherwise (a real input).
