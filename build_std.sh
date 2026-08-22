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
# `stopwatch` imports both `prelude` and `syntax-match`, the latter because its
# `time-it` is written with it.
./bjor --lib lib/std/stopwatch.bjo
# `fmt` imports `prelude` and nothing else.
./bjor --lib lib/std/fmt.bjo

# The collections. Each imports `prelude` and nothing else, and each is
# independent of the other two, so the order between them does not matter.
./bjor --lib lib/std/set.bjo
./bjor --lib lib/std/orderedset.bjo
./bjor --lib lib/std/orderedmap.bjo

echo "Standard library built successfully!"
