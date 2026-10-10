# Benchmarks Game programs: Bjolang, C#, Go and Chez Scheme

```sh
./run.py                 # all benchmarks, 3 runs each, the minimum is reported
./run.py nbody fasta     # only these
./run.py --small         # the check sizes, for a quick look
./run.py --warm          # the second run in each process; see "Warm runs"
```

Run it on a machine that does nothing else.

Requirements: `dotnet`, `go` and `scheme` (Chez Scheme) on the `PATH`, and
the repository built as `Docs/Compiling.org` says.

## What is compared

Nine programs of the
[Computer Language Benchmarks Game](https://benchmarksgame-team.pages.debian.net/benchmarksgame/):
n-body, spectral-norm, fannkuch-redux, binary-trees, mandelbrot, fasta,
k-nucleotide, reverse-complement and pidigits. regex-redux is not included,
because it would measure the .NET regex engine.

- `bjolang/` holds the ports. They are plain Bjolang: no `import/extern`, no
  .NET calls, no bjoroutines. They use the standard library only, with
  `(std mutable map)` for the hash table of k-nucleotide and `(std fmt)`
  for the decimals. pidigits uses `bigint`.
- `csharp/`, `go/` and `chez/` hold one program per benchmark that follows
  the algorithm of the port step for step.

All programs are single-threaded. The fastest programs of the Benchmarks
Game use threads and SIMD; these do not, so the times are not comparable with
the times on its site.

`run.py` times a whole process, start-up included, as the Benchmarks Game
does, with the output going to `/dev/null`. Before the timed runs, each
program runs at a small size, and its output must be the same as the output of
the Go program. k-nucleotide and reverse-complement read the output of fasta
for 25,000,000 (254 MB), which `run.py` makes once in `.build/`.

The C# program runs with the default runtime settings, as a Bjolang program
does. Chez programs are compiled ahead of time with `compile-program` at
`--optimize-level 2` (safe). They use the fixnum and flonum operations
(`fx+`, `fl*`), `fxvector`, `flvector` and `bytevector`, which is what a Chez
programmer writes for speed.

## Where the programs differ

| benchmark | Bjolang | C# | Go | Chez |
|-----------|---------|----|----|------|
| n-body | a record with mutable fields | a class | a struct pointer | an `flvector` per body |
| binary-trees | a union `Empty \| (Node Tree Tree)` | a class, `null` children | a struct pointer, `nil` children | a pair, `'()` children |
| k-nucleotide | `MutableMap` (a .NET `Dictionary`), two lookups per k-mer | `Dictionary`, two lookups | `map`, one `counts[key]++` | `eqv` hashtable, `hashtable-update!` |
| fasta, revcomp output | `write-bytes!` on the output port | `BufferedStream` | `bufio.Writer` | binary port, block buffered |
| pidigits | `bigint`, which is .NET's `BigInteger` | `BigInteger` | `math/big`, a new value for each result | Chez's own bignums |

In each language a node of binary-trees is one allocation, also for a tree of
depth 0. A Chez pair is 16 bytes; an object with two references is 24 bytes
in .NET and 16 in Go.

## Results

Linux 6.18, AMD Ryzen 9 5900X, 16 GB, .NET SDK 10.0.104, Go 1.25.12,
Chez Scheme 10.3.0. Wall-clock seconds, minimum of 3 runs, and the peak
resident memory of one run. A ratio is the Bjolang time divided by the other
time: below 1 means Bjolang is faster.

| benchmark | size | Bjolang | C# | Go | Chez | / C# | / Go | / Chez |
|-----------|-----:|--------:|---:|---:|-----:|-----:|-----:|-------:|
| n-body | 50,000,000 | 2.51 | 2.47 | 2.81 | 7.89 | 1.02 | 0.89 | 0.32 |
| spectral-norm | 5,500 | 1.22 | 1.20 | 1.14 | 4.82 | 1.02 | 1.07 | 0.25 |
| fannkuch-redux | 12 | 20.51 | 21.00 | 21.67 | 58.74 | 0.98 | 0.95 | 0.35 |
| binary-trees | 21 | 12.87 | 10.46 | 14.43 | 4.23 | 1.23 | 0.89 | 3.04 |
| mandelbrot | 16,000 | 12.34 | 12.26 | 12.28 | 15.71 | 1.01 | 1.00 | 0.79 |
| fasta | 25,000,000 | 2.73 | 2.45 | 2.39 | 3.63 | 1.11 | 1.14 | 0.75 |
| k-nucleotide | 25,000,000 | 9.07 | 8.44 | 12.71 | 29.84 | 1.07 | 0.71 | 0.30 |
| reverse-complement | 25,000,000 | 1.50 | 1.29 | 0.54 | 19.79 | 1.16 | 2.76 | 0.08 |
| pidigits | 10,000 | 2.50 | 2.46 | 2.35 | 2.72 | 1.02 | 1.06 | 0.92 |

| peak memory, MB | Bjolang | C# | Go | Chez |
|-----------------|--------:|---:|---:|-----:|
| n-body | 35 | 30 | 2 | 47 |
| spectral-norm | 34 | 29 | 2 | 47 |
| fannkuch-redux | 33 | 32 | 2 | 46 |
| binary-trees | 644 | 612 | 189 | 272 |
| mandelbrot | 66 | 64 | 33 | 84 |
| fasta | 35 | 29 | 2 | 47 |
| k-nucleotide | 513 | 649 | 365 | 1244 |
| reverse-complement | 1213 | 1248 | 447 | 1961 |
| pidigits | 47 | 45 | 8 | 47 |

pidigits was measured in a later run than the others, with the same
machine and settings.

Geometric means of the ratio, over the 9 benchmarks:

| Bjolang against | geometric mean |
|-----------------|---------------:|
| C# | 1.07 |
| Go | 1.07 |
| Chez Scheme | 0.47 |

### Reading the results

- **Loops over numbers and arrays** (n-body, spectral-norm, fannkuch-redux,
  mandelbrot): Bjolang and C# are equal within the noise of the runs. The
  generated C# is the loop a C# programmer writes, and the .NET JIT makes the
  same machine code of both. Go is equal too. Chez is 1.3 to 4 times slower,
  as it boxes a flonum that is stored in a vector or passed to a procedure
  that is not inlined.
- **Allocation** (binary-trees): Bjolang is 23% slower than C#. A union case
  is a C# record, and `check` is a `switch` on the type of the case. Chez is
  3 times faster than both: its pairs are smaller and its collector is made
  for many short-lived pairs. Go uses the least memory and has the slowest
  collector here.
- **Text and output** (fasta, k-nucleotide, reverse-complement): Bjolang is 7
  to 16% slower than C#. The cause is not measured. Two differences are
  candidates: a Bjolang string is UTF-8 and a .NET string UTF-16, so a line
  read from a port is decoded and copied in other places, and `write-bytes!`
  goes through Bjolang's port, which takes its gate for each write. Go reads bytes and never decodes them, which
  is why its reverse-complement is 2.8 times faster than both .NET programs.
  The Chez programs read text with `get-line` through a UTF-8 transcoder and
  collect the lines in a string port; that is slow, and a Chez program that
  reads bytes would be faster.
- **Big integers** (pidigits): the four are within 16% of each other. The
  work is arithmetic on large numbers, and Bjolang's `bigint` is .NET's
  `BigInteger`, so it is equal to C#.
- **Start-up**: a Bjolang program that does nothing takes about 0.06 s, a C#
  program 0.03 s, a Chez program 0.05 s and a Go program less than 0.01 s.
  Bjolang loads the standard library and its runtime as more assemblies.
  This is in every time above.

## Warm runs

```sh
./run.py --warm
```

With `--warm`, each program runs its benchmark twice in one process: first
with its output thrown away, then again, and only the second run is timed,
inside the program. Start-up and most of the JIT's work are not in the time.
k-nucleotide and reverse-complement read standard input once, before either
run, and each run reads it from memory (a string port in Bjolang and Chez, a
`StringReader` in C#, a `bytes.Reader` in Go), so reading the file is not in
the time either. fasta starts each run from the same seed.

Same machine, minimum of 3 runs, seconds:

| benchmark | Bjolang | C# | Go | Chez | / C# | / Go | / Chez | cold Bjolang / C# |
|-----------|--------:|---:|---:|-----:|-----:|-----:|-------:|------------------:|
| n-body | 2.44 | 2.44 | 2.81 | 7.82 | 1.00 | 0.87 | 0.31 | 1.02 |
| spectral-norm | 1.16 | 1.17 | 1.15 | 4.90 | 1.00 | 1.01 | 0.24 | 1.02 |
| fannkuch-redux | 20.85 | 21.16 | 21.64 | 59.40 | 0.99 | 0.96 | 0.35 | 0.98 |
| binary-trees | 12.77 | 10.37 | 14.49 | 4.09 | 1.23 | 0.88 | 3.13 | 1.23 |
| mandelbrot | 12.14 | 12.07 | 12.27 | 15.52 | 1.01 | 0.99 | 0.78 | 1.01 |
| fasta | 2.68 | 2.41 | 2.39 | 3.56 | 1.11 | 1.12 | 0.75 | 1.11 |
| k-nucleotide | 8.15 | 7.89 | 12.40 | 15.45 | 1.03 | 0.66 | 0.53 | 1.07 |
| reverse-complement | 0.99 | 0.57 | 0.35 | 5.40 | 1.75 | 2.83 | 0.18 | 1.16 |
| pidigits | 2.37 | 2.33 | 2.43 | 2.67 | 1.01 | 0.97 | 0.89 | 1.02 |

Geometric means: 1.11 against C#, 1.05 against Go, 0.54 against Chez.

- **Warming up changes little.** Where the work is the same, the warm times
  are within 0.35 s of the cold ones, mostly below, and every ratio to C#
  moves by 0.04 or less. The benchmarks run for seconds, so start-up and JIT compilation were
  already a small part of them. The gaps that remain, binary-trees and fasta,
  are in the code that runs, not in warming it up.
- **Reverse-complement is the exception**, as the work is not the same:
  without reading 254 MB from standard input, what is left is converting
  the lines and writing them. Bjolang takes 0.99 s and C# 0.57 s for that,
  so the text path is where Bjolang loses, and reading a file hid most of
  it in the cold run (1.50 s against 1.29 s). The cause is not measured.
- **Chez reads text slowly.** Its k-nucleotide goes from 29.8 to 15.5 s and
  its reverse-complement from 19.8 to 5.4 s without reading standard input
  through a UTF-8 transcoder.

## What porting found

Things the ports had to go around. None stops a program, but each made the
port longer than it had to be:

- A double literal with an explicit `+` in its exponent, `4.84e+00`, was a
  syntax error. It is accepted now.
- There were no big integers, so pidigits could not be written. There are
  now `bigint`, `int128`, `uint128` and `decimal`, with F#'s literal
  suffixes, `2I` and `1.5M`.
- `summing` added `int`s only, so a sum of doubles had to be
  `(folding 0.0 (+ s x))`. It now works at every numeric type, and the ports
  use it.
- A local signature, `(: v (Array double))` before a `def` in a body, is a
  syntax error. The type of an empty `make-array` must come from its uses.
- Tuples have no `Ord`, so a list of tuples cannot be sorted by `list-sort`;
  k-nucleotide declares a record with `type/derive (Eq Ord)`.
- `(loop (:for _ (range 0 n)) (:do ...))` in the middle of a body is refused
  as an `int` that is discarded, where the same loop with `(:for i ...)` is
  accepted.
