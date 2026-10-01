#!/bin/bash
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.
#
# As a special exception to the Mozilla Public License, version 2.0, if you
# compile your application source code and portions of this software are
# embedded into the generated object code or executable form as a normal
# consequence of the compilation process (such as inline functions,
# templates, generics, or macros), you may redistribute such embedded portions
# in such object code or executable form without complying with the source code
# availability requirements or notice obligations of Section 3 of the MPL 2.0.
#
# HTTP throughput, latency and allocation of bjoweb's examples/hello.exe.
#
# Build it first, against the runtime under test:
#     (cd ../bjoweb && ../Bjolang/bjo/bjo build -c examples/hello.bjo)
#
# Needs bombardier (go install github.com/codesenberg/bombardier@latest) and
# dotnet-counters (dotnet tool install -g dotnet-counters).
#
# B/req is the server's allocation-rate counter summed over the measured run
# and divided by the requests bombardier completed in it. The counter samples
# once a second and the window is a few seconds longer than the load, so it
# includes a few seconds of idle allocation, about 30 KB each.
#
# The load generator runs on the same machine as the server, so rps is a
# comparison between runs on this machine, not a capacity figure.
#
#     CONNS=64 DUR=10 REPS=3 bench/bjoweb.sh

set -e
cd "$(dirname "$0")/.."

WEB="${BJOWEB:-$(cd .. && pwd)/bjoweb}"
BOMB="${BOMBARDIER:-$HOME/go/bin/bombardier}"
COUNTERS="${DOTNET_COUNTERS:-$HOME/.dotnet/tools/dotnet-counters}"
CONNS="${CONNS:-64}"
DUR="${DUR:-10}"
REPS="${REPS:-3}"
BASE="http://127.0.0.1:8080"

dotnet "$WEB/examples/hello.exe" >/dev/null 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null' EXIT

for _ in $(seq 50); do
    curl -s -o /dev/null "$BASE/" && break
    sleep 0.2
done

# name|method|path|body
ROUTES=(
    "static  (fun, lifted)|GET|/|"
    "param   (bjoroutine) |GET|/hello/ada|"
    "header  (fun, lifted)|GET|/headers|"
    "echo    (reads body) |POST|/echo|shout"
)

bomb() {   # method path body duration -> bombardier json on stdout
    local args=(-c "$CONNS" -d "${4}s" -m "$1" -l -p r -o json)
    [ -n "$3" ] && args+=(-b "$3")
    "$BOMB" "${args[@]}" "$BASE$2"
}

echo "conns=$CONNS  dur=${DUR}s  reps=$REPS  server pid=$PID"
for r in "${ROUTES[@]}"; do
    IFS='|' read -r _ method path body <<<"$r"
    bomb "$method" "$path" "$body" 3 >/dev/null
done

printf '\n%-22s %-28s %9s %8s %8s %8s %7s\n' route "rps per rep" "median" p50us p99us "B/req" errors
printf '%s\n' "$(printf -- '-%.0s' $(seq 96))"

for r in "${ROUTES[@]}"; do
    IFS='|' read -r name method path body <<<"$r"
    files=()
    for rep in $(seq "$REPS"); do
        out=$(mktemp); ctr=$(mktemp -u).csv
        # dotnet-counters stops at end of stdin, so it is given one that stays
        # open. It starts before the load and outlasts it, because it takes
        # about a second to attach.
        (sleep $((DUR + 30)) | "$COUNTERS" collect -p "$PID" --counters System.Runtime --format csv \
            -o "$ctr" --refresh-interval 1 --duration "00:00:00:$(printf %02d $((DUR + 4)))" >/dev/null 2>&1) &
        cpid=$!
        sleep 1.5
        bomb "$method" "$path" "$body" "$DUR" >"$out"
        wait $cpid || true
        files+=("$out" "$ctr")
    done
    python3 - "$name" "${files[@]}" <<'EOF'
import json, sys, statistics
name, files = sys.argv[1], sys.argv[2:]
rps, p50, p99, bpr, errs = [], [], [], [], 0
for out, ctr in zip(files[0::2], files[1::2]):
    res = json.load(open(out))["result"]
    reqs = res["req2xx"]
    errs += res["req1xx"] + res["req3xx"] + res["req4xx"] + res["req5xx"] + res["others"]
    rps.append(res["rps"]["mean"])
    pct = res["latency"]["percentiles"]
    p50.append(pct["50"]); p99.append(pct["99"])
    alloc = sum(float(l.rsplit(",", 1)[1]) for l in open(ctr)
                if "total_allocated" in l)
    bpr.append(alloc / reqs if reqs else 0)
med = statistics.median
print(f"{name:<22} {' '.join(f'{x:8.0f}' for x in rps):<28} {med(rps):9.0f} "
      f"{med(p50):8.0f} {med(p99):8.0f} {med(bpr):8.0f} {errs:7d}")
EOF
    rm -f "${files[@]}"
done
