#!/bin/bash
# Usage: ./run.sh [day ...]   e.g. ./run.sh 01 02
# Builds each dayNN.bjo and dayNN.ml, and runs them and dayNN.py on every
# examples/dayNN*.txt. Each output is compared with the .expected file beside it.
# A day with a dayNN-avx.bjo, a solution with (std simd), is checked too.
# With a real input: dotnet dayNN.exe input.txt, ./dayNN.native input.txt, or
# python3 dayNN.py input.txt.
cd "$(dirname "$0")"
COMPILER=../../../bin/compiler/Bjolang.dll
days=("$@"); [ ${#days[@]} -eq 0 ] && days=($(ls day??.bjo | sed 's/day\(..\).bjo/\1/'))
pass=0; fail=0
check() {  # label, expected file, command...
  local label=$1 want=$(cat "$2"); shift 2
  local got=$(timeout 60 "$@" 2>&1)
  if [ "$got" = "$want" ]; then echo "$label: ok ($(echo $got))"; pass=$((pass+1))
  else echo "$label: WRONG"; echo "  want: $(echo $want)"; echo "  got:  $(echo "$got" | head -20)"; fail=$((fail+1)); fi
}
for d in "${days[@]}"; do
  if [ day$d.exe -ot day$d.bjo ] || [ day$d.exe -ot common.bjo ]; then
    out=$(timeout 300 dotnet $COMPILER day$d.bjo 2>&1) || { echo "day$d: BUILD FAILED"; echo "$out" | tail -15; fail=$((fail+1)); continue; }
  fi
  avx=""
  if [ -f day$d-avx.bjo ]; then
    avx=day$d-avx.exe
    if [ $avx -ot day$d-avx.bjo ] || [ $avx -ot common.bjo ]; then
      out=$(timeout 300 dotnet $COMPILER day$d-avx.bjo 2>&1) || { echo "day$d-avx: BUILD FAILED"; echo "$out" | tail -15; fail=$((fail+1)); avx=""; }
    fi
  fi
  if [ day$d.native -ot day$d.ml ]; then
    out=$(ocamlopt -I +str str.cmxa day$d.ml -o day$d.native 2>&1) || { echo "day$d.ml: BUILD FAILED"; echo "$out" | tail -15; fail=$((fail+1)); continue; }
  fi
  for ex in examples/day$d*.txt; do
    name=$(basename $ex .txt)
    check "$name bjolang" examples/$name.expected dotnet day$d.exe $ex
    [ -n "$avx" ] && check "$name avx    " examples/$name.expected dotnet $avx $ex
    check "$name ocaml  " examples/$name.expected ./day$d.native $ex
    check "$name python " examples/$name.expected python3 day$d.py $ex
  done
done
echo "$pass passed, $fail failed"
