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
./bjor --lib lib/std/syntax-match.bjo
./bjor --lib lib/std/maths.bjo
./bjor --lib lib/std/prelude.bjo
# `ports` imports `prelude`, so it comes last for the same reason.
./bjor --lib lib/std/ports.bjo

echo "Standard library built successfully!"
