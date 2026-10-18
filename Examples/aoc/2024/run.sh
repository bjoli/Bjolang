#!/bin/bash
# Usage: ./run.sh [day ...]   e.g. ./run.sh 01 02
# Builds each dayNN.bjo and runs it, and dayNN.py beside it, on every
# examples/dayNN*.txt, comparing each output with the .expected file beside it.
# With a real input: dotnet dayNN.exe input.txt, or python3 dayNN.py input.txt.
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
  for ex in examples/day$d*.txt; do
    name=$(basename $ex .txt)
    check "$name bjolang" examples/$name.expected dotnet day$d.exe $ex
    check "$name python " examples/$name.expected python3 day$d.py $ex
  done
done
echo "$pass passed, $fail failed"
