#!/usr/bin/env python3
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

"""The sharded key-value service in Go, Hopac and Bjolang, side by side.

The scenario is specified in go/main.go's header. Each program runs one
warm-up rep and then REPS timed ones, and prints a line per rep; this runs
each TRIALS times, takes the median of every column over all the reps, and
checks that all three agree on the checksum.

    bench/service/drive.py                     # all cores
    bench/service/drive.py --cores 4           # 4 whole cores, each told so
    bench/service/drive.py --cores 1 --clients 200

`--cores N` pins to N whole cores (CPUs 0, 2, 4, ... on this 5900X, where 2k
and 2k+1 share a core) and sets GOMAXPROCS and DOTNET_PROCESSOR_COUNT to N.

Both .NET programs run with server GC, as bench/run.sh does, and with the
GC's dynamic adaptation (DATAS) off, which is what a Bjolang program with
server GC gets unless it asks for it (Docs/Projects.org, "Runtime settings").
The two programs are then compared on the same GC. With DATAS on, the GC
chose 22 heaps for one and 12 for the other, and the pauses that follow from
that were most of the difference in the tail. `--datas` turns it on for both.

`gcs` is the number of collections in a rep and `pause` the time the GC held
every thread, in total. A tail that moves with them is the GC's.

Without `--rates` the loop is closed: each client sends its next request as
soon as it has the answer to the last. The machine is then saturated, the
mean latency is about clients x wall / requests, and the tail mostly says
which runtime is faster. With `--rates` it is open: requests arrive on a
schedule, at each rate in turn, so the three are compared at the same load
(go/main.go's header has the schedule). Then 10000 clients each send
rate x seconds / 10000 requests, and `req/s` says whether a program kept up.

    bench/service/drive.py --rates 200000,500000,1000000 --cores 4
"""
import argparse, os, statistics, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
HOPAC_DLL = os.path.join(HERE, "hopac", "bin", "Release", "net10.0", "ServiceHopac.dll")

PROGRAMS = {
    "Go":      (os.path.join(HERE, "go"), lambda a: ["./service", "-reps", a[0], "-clients", a[1], "-requests", a[2],
                                                     "-rate", a[3]]),
    "Hopac":   (os.path.join(HERE, "hopac"), lambda a: ["dotnet", HOPAC_DLL, *a]),
    "Bjolang": (os.path.join(HERE, "bjolang"), lambda a: ["dotnet", "service.exe", *a]),
}


def build():
    subprocess.run(["go", "build", "-o", "service", "."], cwd=os.path.join(HERE, "go"), check=True)
    subprocess.run(["dotnet", "build", "-c", "Release"], cwd=os.path.join(HERE, "hopac"), check=True,
                   stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL)
    compiler = subprocess.run([os.path.join(ROOT, "build_compiler.sh"), "--path"],
                              capture_output=True, text=True, check=True).stdout.strip()
    subprocess.run(["dotnet", compiler, "service.bjo"], cwd=os.path.join(HERE, "bjolang"), check=True,
                   stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL)


def run(name, args, env, prefix):
    cwd, cmd = PROGRAMS[name]
    out = subprocess.run(prefix + cmd(args), cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                         capture_output=True, text=True, check=True, timeout=1200).stdout
    reps = []
    for line in out.splitlines():
        if line.startswith("rep "):
            f = line.split()
            reps.append({f[i]: int(f[i + 1]) for i in range(2, len(f), 2)})
    return reps


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cores", type=int, help="pin to this many whole cores, and say so")
    ap.add_argument("--clients", type=int, help="default 1000, or 10000 with --rates")
    ap.add_argument("--requests", type=int, default=1000, help="per client, in a closed loop")
    ap.add_argument("--rates", help="comma-separated requests a second: an open loop at each")
    ap.add_argument("--seconds", type=float, default=3, help="the length of a rep at a rate")
    ap.add_argument("--reps", type=int, help="default 5, or 3 with --rates")
    ap.add_argument("--trials", type=int, help="default 3, or 2 with --rates")
    ap.add_argument("--only", help="comma-separated subset of Go,Hopac,Bjolang")
    ap.add_argument("--env", action="append", default=[], help="K=V for every program")
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--datas", action="store_true", help="the GC's dynamic adaptation on, for both .NET programs")
    a = ap.parse_args()
    if not a.no_build:
        build()

    env = dict(os.environ, DOTNET_gcServer="1", DOTNET_gcConcurrent="1",
               DOTNET_GCDynamicAdaptationMode="1" if a.datas else "0")
    for kv in a.env:
        k, v = kv.split("=", 1)
        env[k] = v
    prefix = []
    if a.cores:
        env["GOMAXPROCS"] = env["DOTNET_PROCESSOR_COUNT"] = str(a.cores)
        prefix = ["taskset", "-c", ",".join(str(2 * i) for i in range(a.cores))]

    rates = [int(r) for r in a.rates.split(",")] if a.rates else [0]
    is_open = rates != [0]
    clients = a.clients or (10000 if is_open else 1000)
    reps_n = a.reps or (3 if is_open else 5)
    trials = a.trials or (2 if is_open else 3)

    where = '%d cores' % a.cores if a.cores else 'all cores'
    if is_open:
        print(f"{clients} clients, open loop, {a.seconds:g} s a rep; {where}; median of {trials} x {reps_n} reps")
    else:
        print(f"{clients} clients x {a.requests} requests; {where}; median of {trials} x {reps_n} reps")
    cols = ["wall ms", "req/s", "cpu ms", "cores", "p50 us", "p99 us", "p99.9 us", "B/req", "gcs", "pause ms"]
    print(f"{'':18}" + "".join(f"{c:>11}" for c in cols))
    ok = True
    for rate in rates:
        per_client = max(1, int(rate * a.seconds / clients)) if rate else a.requests
        requests = clients * per_client
        checksums = set()
        for name in PROGRAMS:
            if a.only and name not in a.only.split(","):
                continue
            reps = []
            for _ in range(trials):
                reps += run(name, [str(reps_n), str(clients), str(per_client), str(rate)], env, prefix)
            checksums |= {r["checksum"] for r in reps}
            if any(r["timeouts"] for r in reps):
                print(f"{name}: {sum(r['timeouts'] for r in reps)} timeouts", file=sys.stderr)
            m = {k: statistics.median(r[k] for r in reps) for k in reps[0]}
            row = [f"{m['wall_ms']:.0f}", f"{requests / m['wall_ms'] * 1000 / 1e6:.2f}M",
                   f"{m['cpu_ms']:.0f}", f"{m['cpu_ms'] / m['wall_ms']:.1f}",
                   f"{m['p50_ns'] / 1000:.0f}", f"{m['p99_ns'] / 1000:.0f}", f"{m['p999_ns'] / 1000:.0f}",
                   f"{m['alloc_b'] / requests:.0f}", f"{m['gcs']:.0f}", f"{m['pause_ms']:.0f}"]
            label = f"{name} @{rate / 1e6:g}M" if rate else name
            print(f"{label:18}" + "".join(v.rjust(11) for v in row), flush=True)
        if len(checksums) > 1:
            print(f"CHECKSUMS DISAGREE at rate {rate}: {sorted(checksums)}", file=sys.stderr)
            ok = False
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
