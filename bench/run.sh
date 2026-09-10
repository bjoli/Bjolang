#!/bin/bash
# Both suites, and the minimum of each row.
#
# The Bjolang side prints one line per rep because it has no sorting in it; the
# reduction to a minimum is here, so that the two suites are reduced the same
# way. Rows come out as `name|ns/op|B/op`.
set -e
cd "$(dirname "$0")/.."

echo "=== raw CML (C#) ==="
dotnet build bench/Cml/Cml.Bench.csproj -c Release >/dev/null
dotnet bench/Cml/bin/Release/net10.0/CmlBench.dll

echo
echo "=== Bjolang ==="
dotnet bin/Release/net10.0/Bjolang.dll bench/bjolang/cmlbench.bjo >/dev/null
dotnet bench/bjolang/cmlbench.exe | awk -F'|' '
  { if (!($1 in ns) || $2+0 < ns[$1]) ns[$1] = $2+0; b[$1] = $3; if (!($1 in seen)) { seen[$1]=1; order[++n]=$1 } }
  END { for (i = 1; i <= n; i++) printf "%-22s %10d %10d\n", order[i], ns[order[i]], b[order[i]] }'
