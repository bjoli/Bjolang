# The classic Scheme benchmarks: Chez Scheme and Bjolang

```sh
./run.py                 # all benchmarks, 3 runs each, the minimum is reported
./run.py fib nqueens     # only these
./run.py --reps 5 --no-o3
```

Run it on a machine that does nothing else: a test suite in parallel changes
the times by up to 40%.

Requirements: `scheme` (Chez Scheme) on the `PATH`, `git`, and the repository
built as `Docs/Compiling.org` says.

## What is compared

**Chez Scheme** runs the sources of
[ecraven's r7rs-benchmarks](https://github.com/ecraven/r7rs-benchmarks),
without changes, at the fixed commit in `run.py`. `run.py` clones the suite
into `.r7rs-benchmarks` (it is not copied into this repository). Each
program is put together as the suite's `bench` script does: source,
`common.scm`, postlude, `common-postlude.scm`. It reads the suite's own input
file on stdin and is compiled ahead of time with `compile-program`. The
suite's published Chez results use `--optimize-level 2`. That is the column
used for the ratios. `-O3` (unsafe) is shown next to it.

Chez does not ship the R7RS libraries, so `chez/r7rs/` holds a small
`(scheme base)`, `(scheme time)` and others. They re-export what
`(chezscheme)` already has and define the few names it lacks. Records use
R7RS syntax. `chez/postlude.scm` replaces the suite's Chez postlude, because
that postlude has an `import` in the middle of the program.

**Bjolang** runs the ports in `bjolang/`. Each port follows the algorithm of
the original form for form: the same loops, the same recursion, the same
allocation. A port takes the same inputs as command-line arguments
(`BENCHMARKS` in `run.py`). Both sides time the same thing: the loop around
the benchmark thunk, inside the program. Process start-up is not included.
JIT compilation of the benchmark code is included for Bjolang, as compile time
is not for Chez.

The benchmarks that use first-class continuations (`ctak`, `fibc`) are not
included. `puzzle` uses `call/cc` only to leave a loop early, so it is
included and its port returns from the loop instead.

## Where the ports differ

Bjolang is statically typed, so some data takes a different shape:

| benchmark   | original                                  | port                                                |
|-------------|-------------------------------------------|-----------------------------------------------------|
| all         | generic arithmetic, fixnums and flonums   | `int` (32-bit) or `double`, fixed by type           |
| `deriv`     | S-expressions                             | a union `Atom \| Int \| Lst`. Quoted literals elaborate into it. |
| `paraffins` | `'(H)`, `#(C r r r)`, `#(BCP …)`          | unions `Radical` and `Paraffin`                     |
| `gcbench`   | children are `0` until set                | `(Option Node)`, which is a struct                  |
| `diviter`, `divrec` | lists of `'()`                    | `(List (List int))` of `Nil`                        |
| `puzzle`    | `call/cc` as an early exit                | return from the loop                                |
| `quicksort` | `random` from the source's MRG32k3a       | the same generator, ported                          |

The type difference is the largest effect. The Scheme sources use generic
`+`, `<` and `vector-ref`, and Chez has to box every flonum that leaves a
register. The Bjolang ports compile to `double` arithmetic on unboxed arrays.
The numeric results below show mostly this. They do not show that
one compiler is better than the other.

## Idiomatic variants

`bjolang-idiomatic/` has a second program for the benchmarks where a Bjolang
programmer would not write lists and named lets. It uses `Vec`, comprehensions
and `loop`, and keeps the algorithm and the result of the original:

| benchmark | idiomatic form |
|-----------|----------------|
| `sum`, `sumfp` | `summing` over a `range`; `:with` and `folding` for doubles |
| `primes` | the same sieve by repeated filtering, with `vecing` |
| `diviter`, `divrec` | every other element of a vec |
| `deriv` | the S-expression keeps its arguments in a `(Vec Sexp)`, rebuilt with `vec-map` |
| `nqueens` | the same search tree, with the rows and the placed queens in vecs |
| `paraffins` | nested comprehensions over vecs of radicals |
| `array1` | vecs built by comprehensions |
| `mbrot` | `:with` state for the iteration, a vec of vecs for the matrix |
| `pnpoly` | a vec of points, walked backwards with the previous vertex in `:with` |

The recursive benchmarks (`fib`, `tak`, `ack`, `takl`, `cpstak`) and the
benchmarks that change arrays in place (`quicksort`, `triangl`, `puzzle`,
`fft`, `gcbench`) are already idiomatic, and have no variant.

## Results

Linux 6.18, AMD Ryzen 9 5900X (24 threads), 16 GB, Chez Scheme 10.3.0,
.NET SDK 10.0.104. Seconds, minimum of 3 runs. A ratio is Bjolang time divided
by Chez `-O2` time: below 1 means Bjolang is faster.

| benchmark  | chez -O2 | chez -O3 | bjolang | / chez | idiomatic | / chez |
|------------|---------:|---------:|--------:|-------:|----------:|-------:|
| fib        |    2.578 |    2.704 |   1.250 |   0.48 |           |        |
| fibfp      |    1.656 |    1.557 |   0.308 |   0.19 |           |        |
| tak        |    1.029 |    1.219 |   1.213 |   1.18 |           |        |
| takl       |    2.500 |    2.453 |   1.716 |   0.69 |           |        |
| ack        |    1.721 |    1.725 |   0.691 |   0.40 |           |        |
| cpstak     |    2.020 |    1.906 | stack overflow | — |         |        |
| nqueens    |    3.203 |    2.956 |   4.711 |   1.47 |     5.538 |   1.73 |
| mbrot      |    4.142 |    3.936 |   0.448 |   0.11 |     0.488 |   0.12 |
| primes     |    0.598 |    0.573 |   1.273 |   2.13 |     0.670 |   1.12 |
| sum        |    1.660 |    1.453 |   0.455 |   0.27 |     0.498 |   0.30 |
| sumfp      |    2.407 |    2.590 |   0.364 |   0.15 |     0.391 |   0.16 |
| diviter    |    0.947 |    0.919 |   2.836 |   2.99 |     1.352 |   1.43 |
| divrec     |    1.517 |    1.326 |   4.069 |   2.68 |     1.921 |   1.27 |
| deriv      |    0.729 |    0.646 |   3.977 |   5.45 |     5.335 |   7.32 |
| array1     |    9.779 |    9.156 |   0.819 |   0.08 |     2.694 |   0.28 |
| triangl    |    1.325 |    1.046 |   1.072 |   0.81 |           |        |
| quicksort  |    2.562 |    2.387 |   1.207 |   0.47 |           |        |
| fft        |    1.358 |    1.277 |   0.169 |   0.12 |           |        |
| pnpoly     |    2.832 |    2.767 |   0.526 |   0.19 |     0.584 |   0.21 |
| puzzle     |    1.337 |    0.906 |   0.646 |   0.48 |           |        |
| paraffins  |    4.218 |    4.203 |   7.812 |   1.85 |     5.720 |   1.36 |
| gcbench    |    0.555 |    0.559 |   1.368 |   2.46 |           |        |

Geometric means of the ratio:

| programs | benchmarks | geometric mean |
|----------|-----------:|---------------:|
| ports | 21 | **0.61** |
| idiomatic variants | 11 | 0.67 |
| the faster Bjolang program of each benchmark | 21 | **0.54** |

The Chez figures are about 1.5 times faster than the suite's published
Chez 10.3.0 results, uniformly, which is the difference in machines.

### Reading the results

- **Numeric code and vectors** (`fibfp`, `sumfp`, `mbrot`, `fft`, `pnpoly`,
  `array1`): Bjolang is 5 to 12 times faster. This comes from the static
  types, see above.
- **Integer recursion** (`fib`, `tak`, `ack`, `takl`): about equal, or
  Bjolang is faster.
- **List allocation** (`deriv`, `divrec`, `diviter`, `primes`, `nqueens`,
  `paraffins`, `gcbench`): the ports are 1.5 to 5 times slower. Measured
  causes, in `divrec` and `deriv`:
  - Most of the time is in allocation: the allocation helper, the
    thread-local lookup it makes on Linux, and zeroing. Garbage collection
    itself costs little; fewer or larger collections do not help.
  - A cons cell is 32 bytes (object header, type, `car`, `cdr`), against 16
    in Chez, so twice the memory is written.
  - Each reference stored into a new cell goes through a GC write barrier,
    which the .NET JIT does not leave out for new objects. Chez needs none.
  - Nothing in Bjolang can remove these. NativeAOT removes the thread-local
    lookup, but its write barrier is slower, and the total was 18% slower.
- **Vecs where lists were** (`primes`, `diviter`, `divrec`, `paraffins`): the
  idiomatic variants are 1.4 to 2.1 times faster than the list ports. A vec
  is built in leaf arrays, so it makes far fewer objects than a list.
- **Short vec walks** (`nqueens`, `pnpoly`): a vec walk is a struct cursor
  over the leaf arrays now, and allocates nothing (see below). Before that,
  each walk allocated a cursor and started an enumerator, and the idiomatic
  `nqueens` took 10.5 s and `pnpoly` 0.985 s. What is left is the start of a
  walk: `in-vec`, `in-reverse-vec` and `up-from` are calls that check their
  arguments. The check `ok?` in `nqueens` starts two walks hundreds of
  millions of times; with `(:for k (range 0 n))` and `vec-ref` the same
  program runs in 3.35 s.
- **Small persistent vecs** (`deriv`): the idiomatic variant rebuilds each
  expression with `vec-insert` and `vec-map` on vecs of two to four elements,
  and that costs more than consing the same lists.
- **`cpstak`** overflows the stack. Each continuation call `(k z)` is a tail
  call to a closure. .NET does not do general tail calls, and Bjolang only
  turns self tail calls into loops, so the stack grows with the depth of the
  continuation chain. `(cpstak 18 12 6)`, the older input of the suite, runs.

## Runtime and compiler changes made for these benchmarks

- **Non-virtual list cells** (`SchemeList.cs`). `car` and `cdr` are fields of
  `SchemeList<T>`, and the empty list is the node with a null `cdr`. `Car`,
  `Cdr` and `IsEmpty` are field reads, not virtual calls. `Cons<T>` and
  `Nil<T>` stay as classes, so type patterns on them do not change.
- **The list builder** (`SchemeListBuilder.cs`). A new cell has a null `cdr`
  until the next one is added, and the last cell gets the empty list when the
  list is made. Each `Add` thus stores one reference less and does not read
  `Nil<T>.Instance`, which needs a runtime lookup in shared generic code.
- **The reverse vec enumerator** (`RrbReverseEnumerator.cs`) kept its path in
  two heap arrays, made for every walk. It uses inline buffers now, as the
  forward enumerator does.
- **Vec walks one leaf at a time** (`VecCursor`, `VecBackCursor`,
  `RrbList.LeafAt`). `(:for x v)`, `in-vec` and `in-reverse-vec` walk with a
  struct cursor that holds the current leaf array and an index into it. An
  element costs an array read, the next leaf is looked up once per 32
  elements, and a walk allocates nothing. Before, a walk allocated a class
  holding an enumerator. A walk of 6 elements went from 4.0 to 1.1 ns per
  element, and a walk of a million from 1.2 to 0.66 ns, against 2.7 ns for
  `vec-ref` per index. The slow path of the cursor takes and answers values,
  so that the JIT keeps the cursor in registers: with a method that changes
  the cursor in place, the same walk took 7.9 ns per element.
- **Builtins written as C# expressions** (`Codegen.inlineBuiltins`). A call to
  a builtin that only forwards to a member, such as `list-head` or `vec-ref`,
  is emitted as that member, `l.Car` or `v[i]`, not as a call to the runtime
  wrapper. In concrete code the JIT already inlined the wrappers, and the
  benchmarks do not change. The gain is in generic code and in code the JIT
  has not optimized yet.

Before and after these changes, for the ports (seconds):

| benchmark | before | after |
|-----------|-------:|------:|
| takl      |  2.473 | 1.716 |
| divrec    |  4.881 | 4.069 |
| nqueens   |  5.083 | 4.711 |
| deriv     |  4.200 | 3.977 |
| paraffins |  7.343 | 7.812 |

And for the idiomatic variants, before and after the leaf cursors:

| benchmark | before | after |
|-----------|-------:|------:|
| nqueens   | 10.485 | 5.538 |
| pnpoly    |  0.985 | 0.584 |
| primes    |  0.750 | 0.670 |
| array1    |  3.018 | 2.694 |

`paraffins` is slower in all builds made after the first run, also with the
inline builtins turned off. The cause is not known.

## Compiler bugs found while porting

Both are fixed, and `TestFiles/329_loop_lambda_args_and_punctuated_names.bjo`
covers them.

- A self tail call with a lambda as an argument emitted
  `var __next = (v) => …`, which C# refuses (CS8917). The temporary is now
  declared with the delegate type (`Codegen.generateRecur`).
- A name with ASCII punctuation that has no C# spelling, such as `radius^2`,
  was emitted unchanged. Such characters are now escaped as `_u5E_`
  (`Naming.escapeUnidentifiable`).
