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

"""Hardware counters per rendezvous, Bjolang against Hopac.

Each row is run under `perf stat` at 2 and at 12 reps of a million operations.
The difference over the ten extra reps is the steady-state cost of one
operation: startup, JIT compilation and the warm-up are in both runs and cancel.
Counters are user-space only (`:u`), which is what an unprivileged process may
count with perf_event_paranoid at 2; time in the kernel shows only in `cpu-ns`.

    bench/perf/drive.py                    # every row, all cores
    bench/perf/drive.py --one-core --tc0   # path length: one core, no tiering
    bench/perf/drive.py ring               # rows whose name contains "ring"

`--tc0` sets DOTNET_TieredCompilation=0, so every method is fully optimised
from its first compilation. Without it, parts of the runtime are still running
tier-0 code after the warm-up, and both sides carry tiering residue.
`--one-core` pins the process to one CPU, which separates the cost of the path
from the cost of moving work between cores.
"""
import argparse, os, statistics, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
EVENTS = ["instructions:u", "cycles:u", "branches:u", "branch-misses:u",
          "L1-dcache-load-misses:u", "task-clock"]
LO, HI = 2, 12
OPS = 1_000_000

BJO = os.path.join(HERE, "bjolang")
HOPAC = os.path.join(HERE, "hopac")
HOPAC_DLL = os.path.join(HOPAC, "bin", "Release", "net10.0", "PerfHopac.dll")
ROWS = {
    "bjo ring (sync)":  (BJO, ["dotnet", "rows.exe", "ring"]),
    "bjo ring put/get": (BJO, ["dotnet", "rows.exe", "ringput"]),
    "hopac ring":       (HOPAC, ["dotnet", HOPAC_DLL, "ring"]),
    "bjo choose":       (BJO, ["dotnet", "rows.exe", "choose"]),
    "bjo choose, put":  (BJO, ["dotnet", "rows.exe", "chooseput"]),
    "hopac choose":     (HOPAC, ["dotnet", HOPAC_DLL, "choose"]),
}


def build():
    compiler = subprocess.run([os.path.join(ROOT, "build_compiler.sh"), "--path"],
                              capture_output=True, text=True, check=True).stdout.strip()
    subprocess.run(["dotnet", compiler, "rows.bjo"], cwd=BJO, check=True,
                   stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL)
    subprocess.run(["dotnet", "build", "-c", "Release"], cwd=HOPAC, check=True,
                   stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL)


def one(cwd, cmd, reps, env, prefix):
    out = os.path.join(HERE, ".stat.csv")
    t0 = time.perf_counter()
    subprocess.run(prefix + ["perf", "stat", "-x,", "-o", out, "-e", ",".join(EVENTS), "--"]
                   + cmd + [str(reps)],
                   cwd=cwd, env=env, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                   stderr=subprocess.DEVNULL, check=True, timeout=600)
    vals = {"wall": (time.perf_counter() - t0) * 1000}
    for line in open(out):
        parts = line.strip().split(",")
        if len(parts) > 2:
            name = parts[2] if parts[2] in EVENTS else parts[2].removesuffix(":u")
            if name in EVENTS:
                vals[name] = float(parts[0])
    return vals


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("rows", nargs="*", help="substrings of row names to run")
    ap.add_argument("--one-core", action="store_true")
    ap.add_argument("--tc0", action="store_true")
    ap.add_argument("--trials", type=int, default=3)
    ap.add_argument("--no-build", action="store_true")
    a = ap.parse_args()
    if not a.no_build:
        build()

    env = dict(os.environ, DOTNET_gcServer="1", DOTNET_gcConcurrent="1")
    if a.tc0:
        env["DOTNET_TieredCompilation"] = "0"
    prefix = ["taskset", "-c", "2"] if a.one_core else []

    print(f"per operation, from {HI - LO} extra reps of {OPS:,}; median of {a.trials}"
          f"{'; one core' if a.one_core else ''}{'; TieredCompilation=0' if a.tc0 else ''}")
    cols = ["instr", "cycles", "IPC", "branches", "br-miss", "L1d-miss", "cpu-ns", "wall-ns"]
    print(f"{'':20}" + "".join(f"{c:>10}" for c in cols))
    for name, (cwd, cmd) in ROWS.items():
        if a.rows and not any(r in name for r in a.rows):
            continue
        trials = []
        for _ in range(a.trials):
            lo, hi = one(cwd, cmd, LO, env, prefix), one(cwd, cmd, HI, env, prefix)
            trials.append({k: (hi[k] - lo[k]) / ((HI - LO) * OPS) for k in hi})
        m = {k: statistics.median(t[k] for t in trials) for k in trials[0]}
        row = [m["instructions:u"], m["cycles:u"], m["instructions:u"] / m["cycles:u"],
               m["branches:u"], m["branch-misses:u"], m["L1-dcache-load-misses:u"],
               m["task-clock"] * 1e6, m["wall"] * 1e6]
        fmt = ["{:.0f}", "{:.0f}", "{:.2f}", "{:.0f}", "{:.2f}", "{:.1f}", "{:.0f}", "{:.0f}"]
        print(f"{name:20}" + "".join(f.format(v).rjust(10) for f, v in zip(fmt, row)), flush=True)


if __name__ == "__main__":
    sys.exit(main())
