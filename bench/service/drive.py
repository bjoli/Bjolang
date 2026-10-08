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
Both .NET programs run with server GC, as bench/run.sh does.
"""
import argparse, os, statistics, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
HOPAC_DLL = os.path.join(HERE, "hopac", "bin", "Release", "net10.0", "ServiceHopac.dll")

PROGRAMS = {
    "Go":      (os.path.join(HERE, "go"), lambda a: ["./service", "-reps", a[0], "-clients", a[1], "-requests", a[2]]),
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
    ap.add_argument("--clients", type=int, default=1000)
    ap.add_argument("--requests", type=int, default=1000)
    ap.add_argument("--reps", type=int, default=5)
    ap.add_argument("--trials", type=int, default=3)
    ap.add_argument("--only", help="comma-separated subset of Go,Hopac,Bjolang")
    ap.add_argument("--env", action="append", default=[], help="K=V for every program")
    ap.add_argument("--no-build", action="store_true")
    a = ap.parse_args()
    if not a.no_build:
        build()

    env = dict(os.environ, DOTNET_gcServer="1", DOTNET_gcConcurrent="1")
    for kv in a.env:
        k, v = kv.split("=", 1)
        env[k] = v
    prefix = []
    if a.cores:
        env["GOMAXPROCS"] = env["DOTNET_PROCESSOR_COUNT"] = str(a.cores)
        prefix = ["taskset", "-c", ",".join(str(2 * i) for i in range(a.cores))]

    requests = a.clients * a.requests
    print(f"{a.clients} clients x {a.requests} requests; {'%d cores' % a.cores if a.cores else 'all cores'}; "
          f"median of {a.trials} x {a.reps} reps")
    cols = ["wall ms", "req/s", "cpu ms", "cores", "p50 us", "p99 us", "p99.9 us", "B/req"]
    print(f"{'':10}" + "".join(f"{c:>11}" for c in cols))
    checksums = set()
    for name in PROGRAMS:
        if a.only and name not in a.only.split(","):
            continue
        reps = []
        for _ in range(a.trials):
            reps += run(name, [str(a.reps), str(a.clients), str(a.requests)], env, prefix)
        checksums |= {r["checksum"] for r in reps}
        if any(r["timeouts"] for r in reps):
            print(f"{name}: {sum(r['timeouts'] for r in reps)} timeouts", file=sys.stderr)
        m = {k: statistics.median(r[k] for r in reps) for k in reps[0]}
        row = [f"{m['wall_ms']:.0f}", f"{requests / m['wall_ms'] * 1000 / 1e6:.2f}M",
               f"{m['cpu_ms']:.0f}", f"{m['cpu_ms'] / m['wall_ms']:.1f}",
               f"{m['p50_ns'] / 1000:.0f}", f"{m['p99_ns'] / 1000:.0f}", f"{m['p999_ns'] / 1000:.0f}",
               f"{m['alloc_b'] / requests:.0f}"]
        print(f"{name:10}" + "".join(v.rjust(11) for v in row), flush=True)
    if len(checksums) != 1:
        print(f"CHECKSUMS DISAGREE: {sorted(checksums)}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
