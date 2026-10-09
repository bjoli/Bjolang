#!/bin/bash
# Compiles the given benchmarks (default: every .bjo here but common.bjo).
set -e
cd "$(dirname "$0")"
compiler="$(../../../build_compiler.sh --path)"
if [ $# -eq 0 ]; then
    set -- $(ls *.bjo | grep -v '^common.bjo$' | sed 's/\.bjo$//')
fi
for b in "$@"; do
    dotnet "$compiler" "$b.bjo" > "$b.log" 2>&1 && echo "ok   $b" || { echo "FAIL $b"; grep -iE 'error' "$b.log" | head -5; }
done
