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
| fib        |    2.674 |    2.786 |   1.258 |   0.47 |           |        |
| fibfp      |    1.560 |    1.569 |   0.310 |   0.20 |           |        |
| tak        |    1.005 |    1.193 |   1.208 |   1.20 |           |        |
| takl       |    2.495 |    2.442 |   1.686 |   0.68 |           |        |
| ack        |    1.733 |    1.717 |   0.691 |   0.40 |           |        |
| cpstak     |    2.021 |    1.887 | stack overflow | — |         |        |
| nqueens    |    3.205 |    2.963 |   4.704 |   1.47 |    10.485 |   3.27 |
| mbrot      |    4.172 |    3.939 |   0.447 |   0.11 |     0.487 |   0.12 |
| primes     |    0.601 |    0.571 |   1.252 |   2.08 |     0.750 |   1.25 |
| sum        |    1.658 |    1.451 |   0.458 |   0.28 |     0.496 |   0.30 |
| sumfp      |    2.277 |    2.479 |   0.363 |   0.16 |     0.391 |   0.17 |
| diviter    |    0.946 |    0.919 |   2.846 |   3.01 |     1.346 |   1.42 |
| divrec     |    1.453 |    1.428 |   4.082 |   2.81 |     1.901 |   1.31 |
| deriv      |    0.761 |    0.626 |   3.949 |   5.19 |     5.293 |   6.96 |
| array1     |    9.660 |    9.319 |   0.818 |   0.08 |     3.018 |   0.31 |
| triangl    |    1.318 |    1.053 |   1.072 |   0.81 |           |        |
| quicksort  |    2.577 |    2.378 |   1.211 |   0.47 |           |        |
| fft        |    1.364 |    1.281 |   0.170 |   0.12 |           |        |
| pnpoly     |    2.821 |    2.764 |   0.525 |   0.19 |     0.985 |   0.35 |
| puzzle     |    1.334 |    0.906 |   0.647 |   0.48 |           |        |
| paraffins  |    4.222 |    4.215 |   7.664 |   1.82 |     5.628 |   1.33 |
| gcbench    |    0.552 |    0.548 |   1.365 |   2.47 |           |        |

Geometric means of the ratio:

| programs | benchmarks | geometric mean |
|----------|-----------:|---------------:|
| ports | 21 | **0.61** |
| idiomatic variants | 11 | 0.76 |
| the faster Bjolang program of each benchmark | 21 | **0.55** |

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
- **Short vec walks** (`nqueens`, `pnpoly`, `deriv`): the idiomatic variants
  are slower than the ports. Each `(:for x (in-vec v))` or `in-reverse-vec`
  allocates a cursor and starts an enumerator, and `in-vec` and `up-from`
  each make a call that checks their arguments. For a walk of a few elements,
  that start costs more than the walk. In `nqueens` the check `ok?` does this
  hundreds of millions of times: with `(:for k (range 0 n))` and `vec-ref`
  instead of `in-reverse-vec` and `up-from`, the same program runs in 3.35 s,
  against 10.5 s. `deriv` also pays for `vec-insert` on small vecs.
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
- **Builtins written as C# expressions** (`Codegen.inlineBuiltins`). A call to
  a builtin that only forwards to a member, such as `list-head` or `vec-ref`,
  is emitted as that member, `l.Car` or `v[i]`, not as a call to the runtime
  wrapper. In concrete code the JIT already inlined the wrappers, and the
  benchmarks do not change. The gain is in generic code and in code the JIT
  has not optimized yet.

Before and after these changes, for the ports (seconds):

| benchmark | before | after |
|-----------|-------:|------:|
| takl      |  2.473 | 1.686 |
| divrec    |  4.881 | 4.082 |
| nqueens   |  5.083 | 4.704 |
| deriv     |  4.200 | 3.949 |
| paraffins |  7.343 | 7.664 |

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
