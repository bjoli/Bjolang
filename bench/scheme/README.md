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
| fib        |    2.634 |    2.583 |   1.238 |   0.47 |           |        |
| fibfp      |    1.611 |    1.619 |   0.301 |   0.19 |           |        |
| tak        |    1.027 |    1.188 |   1.208 |   1.18 |           |        |
| takl       |    2.491 |    2.439 |   1.717 |   0.69 |           |        |
| ack        |    1.721 |    1.720 |   0.690 |   0.40 |           |        |
| cpstak     |    2.009 |    1.896 | stack overflow | — |         |        |
| nqueens    |    3.194 |    2.990 |   4.729 |   1.48 |     4.078 |   1.28 |
| mbrot      |    4.169 |    3.903 |   0.448 |   0.11 |     0.487 |   0.12 |
| primes     |    0.603 |    0.577 |   1.271 |   2.11 |     0.731 |   1.21 |
| sum        |    1.660 |    1.457 |   0.454 |   0.27 |     0.497 |   0.30 |
| sumfp      |    2.397 |    2.413 |   0.363 |   0.15 |     0.392 |   0.16 |
| diviter    |    0.953 |    0.918 |   2.784 |   2.92 |     1.349 |   1.42 |
| divrec     |    1.438 |    1.416 |   4.000 |   2.78 |     1.844 |   1.28 |
| deriv      |    0.765 |    0.653 |   3.992 |   5.22 |     5.227 |   6.83 |
| array1     |    9.587 |    9.342 |   0.830 |   0.09 |     2.760 |   0.29 |
| triangl    |    1.318 |    1.057 |   1.065 |   0.81 |           |        |
| quicksort  |    2.560 |    2.388 |   1.214 |   0.47 |           |        |
| fft        |    1.355 |    1.287 |   0.170 |   0.13 |           |        |
| pnpoly     |    2.823 |    2.737 |   0.527 |   0.19 |     0.493 |   0.17 |
| puzzle     |    1.339 |    0.911 |   0.645 |   0.48 |           |        |
| paraffins  |    4.184 |    4.172 |   7.694 |   1.84 |     5.597 |   1.34 |
| gcbench    |    0.547 |    0.536 |   1.369 |   2.50 |           |        |

Geometric means of the ratio:

| programs | benchmarks | geometric mean |
|----------|-----------:|---------------:|
| ports | 21 | **0.61** |
| idiomatic variants | 11 | 0.64 |
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
  over the leaf arrays now, it allocates nothing, and its start inlines into
  the loop (see below). Before that, each walk allocated a cursor and started
  an enumerator, and the idiomatic `nqueens` took 10.5 s and `pnpoly`
  0.985 s. The idiomatic `pnpoly` is now faster than the port. The check
  `ok?` in `nqueens` starts two walks hundreds of millions of times; with
  `(:for k (range 0 n))` and `vec-ref` the same program runs in 3.35 s,
  against 4.08 s with the walks.
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
- **Walks that start without a call.** `in-vec`, `in-reverse-vec` and
  `up-from` were calls at the start of each walk, because the JIT did not
  inline them. Two things stopped it:
  - `check-slice` and `check-step` built their panic messages in their own
    bodies, which made them too large. The messages are now made in
    functions of their own, `slice-panic` and `step-panic`, which run only
    when a check fails. `range-by` has the same change.
  - Each of these functions reads a string literal, and a string literal was
    a `static readonly` field of a struct type, `Utf8String`. The .NET JIT
    does not inline a method of another assembly that reads a static field of
    a struct type. The code generator now holds a struct literal in a
    `StrongBox`. This applies to every Bjolang function that uses a string
    literal: such a function in a library can now be inlined into a program.
  - The first leaf of a vec that is all tail is now found without a call
    (`RrbList.LeafAt` inlines its tail case).

  The idiomatic `nqueens` went from 5.54 s to 4.08 s, and `pnpoly` from
  0.584 s to 0.493 s.
- **Builtins written as C# expressions** (`Codegen.inlineBuiltins`). A call to
  a builtin that only forwards to a member, such as `list-head` or `vec-ref`,
  is emitted as that member, `l.Car` or `v[i]`, not as a call to the runtime
  wrapper. In concrete code the JIT already inlined the wrappers, and the
  benchmarks do not change. The gain is in generic code and in code the JIT
  has not optimized yet.

Before and after these changes, for the ports (seconds):

| benchmark | before | after |
|-----------|-------:|------:|
| takl      |  2.473 | 1.717 |
| divrec    |  4.881 | 4.000 |
| nqueens   |  5.083 | 4.729 |
| deriv     |  4.200 | 3.992 |
| paraffins |  7.343 | 7.694 |

And for the idiomatic variants, before the leaf cursors, with them, and with
walks that start without a call:

| benchmark | before | leaf cursors | inlined start |
|-----------|-------:|-------------:|--------------:|
| nqueens   | 10.485 |        5.538 |         4.078 |
| pnpoly    |  0.985 |        0.584 |         0.493 |
| primes    |  0.750 |        0.670 |         0.731 |
| array1    |  3.018 |        2.694 |         2.760 |

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
