#!/bin/bash
set -e

echo "Building standard library..."

# In dependency order: `maths` imports nothing, and `prelude` imports it in
# order to re-export the `Num` trait. Building `prelude` first would find a
# stale `maths.dll`, or none at all and fall back to compiling the source a
# second time into `prelude` itself.
#
# `syntax-match` comes before both. It imports nothing at all — deliberately,
# since `prelude` imports *it* to write `cond` and its neighbours, and a macro
# has to be compiled before whatever uses it is read.
# `eq` is below all of them: `=` is a trait method, so every module that
# compares two values imports the module declaring the trait.
./bjor --lib lib/std/eq.bjo
./bjor --lib lib/std/syntax-match.bjo
./bjor --lib lib/std/maths.bjo
./bjor --lib lib/std/prelude.bjo
# `ports` imports `prelude`, so it comes last for the same reason.
./bjor --lib lib/std/ports.bjo
# `monad` imports `prelude` for `list-append` and `syntax-match` to write `do`
# with. Both have to be compiled first — a macro's module is loaded into the
# compiler along with everything it imports.
./bjor --lib lib/std/monad.bjo
# `stopwatch` imports both `prelude` and `syntax-match`, the latter because its
# `time-it` is written with it.
./bjor --lib lib/std/stopwatch.bjo
# `fmt` imports `prelude` and nothing else.
./bjor --lib lib/std/fmt.bjo
# `run` imports `prelude` and `syntax-match`, the latter because `with-run` is
# written with it. It also binds `BjoPipe` and `BjoProc` out of the runtime
# assembly, so a change to `BjolangRuntime/BjoProcess.cs` has to reach the
# compiler before this line: the compiler links a copy of the runtime of its
# own, and the default load context serves whichever identity loaded first.
# Rebuild the compiler after the runtime, then run this.
./bjor --lib lib/std/run.bjo
# `simpletest` likewise. It is what the suite's assertions are written in, so
# it is built with the library rather than beside the tests: a test file is an
# ordinary program, and this is an ordinary module it imports.
./bjor --lib lib/std/simpletest.bjo

# The collections. Each imports `prelude` and nothing else, and each is
# independent of the other two, so the order between them does not matter.
./bjor --lib lib/std/set.bjo
# `clr-ord` imports `prelude` for the `Ord` trait it implements against, and
# declares nothing else.
./bjor --lib lib/std/clr-ord.bjo
./bjor --lib lib/std/orderedset.bjo
./bjor --lib lib/std/orderedmap.bjo

# The mutable collections, under `std/mutable` so that reaching for one is a
# deliberate act. `deque` imports `prelude` and nothing else.
./bjor --lib lib/std/mutable/deque.bjo

# `text/json` imports `prelude` and nothing else.
./bjor --lib lib/text/json.bjo

# After `json`, whose `Json` type it names, and after `syntax-match`, which its
# transformer is written in.
./bjor --lib lib/text/json-codec.bjo

echo "Standard library built successfully!"
