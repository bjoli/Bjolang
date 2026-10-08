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

The rows above have one chain runnable at a time on an otherwise idle machine,
the one shape where pool workers spinning for work cost nothing anybody else
wanted. Two more families look at scheduling under load:

    bench/perf/drive.py --rings 1,12,1000   # K rings at once, ~1M hops in all
    bench/perf/drive.py --mixed 0,12        # one ring beside C crunching fibers

`--mixed` reports the ring's wall time per hop, how long C fibers of pure
computation took with the ring beside them and without it, and the CPU the ring
cost: the task-clock of the run with the ring less that of the run without it,
per hop, so that it includes whatever the crunchers lost to it.

Either can run inside a constraint: `--cpus 0,2` pins to those CPUs (on this
part 2k and 2k+1 share a core), `--quota 200` runs in a systemd scope with
`CPUQuota=200%`, `--procs 2` sets DOTNET_PROCESSOR_COUNT, and `--env K=V`
passes anything else, such as a scheduler switch.
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
    """Run under `perf stat`; the counters, wall milliseconds and stdout."""
    out = os.path.join(HERE, ".stat.csv")
    t0 = time.perf_counter()
    run = subprocess.run(prefix + ["perf", "stat", "-x,", "-o", out, "-e", ",".join(EVENTS), "--"]
                         + cmd + [str(reps)],
                         cwd=cwd, env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL, text=True, check=True, timeout=600)
    vals = {"wall": (time.perf_counter() - t0) * 1000}
    for line in open(out):
        parts = line.strip().split(",")
        if len(parts) > 2:
            name = parts[2] if parts[2] in EVENTS else parts[2].removesuffix(":u")
            if name in EVENTS:
                vals[name] = float(parts[0])
    return vals, run.stdout


RING_WIDTH = 10  # rows.bjo's ring-width


def rings_ops(k):
    return k * RING_WIDTH * (100_000 // k)


def counted(rows, a, env, prefix):
    """Rows timed by the difference between LO and HI reps, per operation."""
    print(f"per operation, from {HI - LO} extra reps; median of {a.trials}")
    cols = ["instr", "cycles", "IPC", "branches", "br-miss", "L1d-miss", "cpu-ns", "wall-ns"]
    print(f"{'':20}" + "".join(f"{c:>10}" for c in cols))
    for name, (cwd, cmd, ops) in rows.items():
        trials = []
        for _ in range(a.trials):
            lo, hi = one(cwd, cmd, LO, env, prefix)[0], one(cwd, cmd, HI, env, prefix)[0]
            trials.append({k: (hi[k] - lo[k]) / ((HI - LO) * ops) for k in hi})
        m = {k: statistics.median(t[k] for t in trials) for k in trials[0]}
        row = [m["instructions:u"], m["cycles:u"], m["instructions:u"] / m["cycles:u"],
               m["branches:u"], m["branch-misses:u"], m["L1-dcache-load-misses:u"],
               m["task-clock"] * 1e6, m["wall"] * 1e6]
        fmt = ["{:.0f}", "{:.0f}", "{:.2f}", "{:.0f}", "{:.2f}", "{:.1f}", "{:.0f}", "{:.0f}"]
        print(f"{name:20}" + "".join(f.format(v).rjust(10) for f, v in zip(fmt, row)), flush=True)


MIXED_REPS = 7
WARMUP = 3  # rows.bjo's warm-up reps, which print too


def mixed_run(row, c, env, prefix):
    stat, out = one(BJO, ["dotnet", "rows.exe", row, str(c)], MIXED_REPS, env, prefix)
    reps = [tuple(int(x) for x in l.split()[1:]) for l in out.splitlines()
            if l.startswith("mixed ")][WARMUP:]
    return stat["task-clock"], reps


def mixed(counts, a, env, prefix):
    """One ring beside C crunching fibers, against the crunchers alone."""
    print(f"one ring of {RING_WIDTH}, 1M hops, beside C fibers crunching; "
          f"median of {a.trials} x {MIXED_REPS} reps")
    cols = ["ring wall-ns", "crunch ms", "alone ms", "slowdown", "ring cpu-ns"]
    print(f"{'':10}" + "".join(f"{c:>14}" for c in cols))
    for c in counts:
        ring_ns, crunch, alone, cpu = [], [], [], []
        for _ in range(a.trials):
            cpu_with, with_ring = mixed_run("mixed", c, env, prefix)
            cpu_without, without = mixed_run("crunch", c, env, prefix)
            ring_ns += [r for r, _ in with_ring]
            crunch += [m for _, m in with_ring]
            alone += [m for _, m in without]
            cpu.append((cpu_with - cpu_without) * 1e6 / ((WARMUP + MIXED_REPS) * 1_000_000))
        med = statistics.median
        slow = f"{(med(crunch) / med(alone) - 1) * 100:+.1f}%" if c else "-"
        row = [f"{med(ring_ns)}", f"{med(crunch)}" if c else "-", f"{med(alone)}" if c else "-",
               slow, f"{med(cpu):.0f}"]
        print(f"{'C=' + str(c):10}" + "".join(v.rjust(14) for v in row), flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("rows", nargs="*", help="substrings of row names to run")
    ap.add_argument("--one-core", action="store_true")
    ap.add_argument("--cpus", help="taskset CPU list")
    ap.add_argument("--quota", type=int, help="CPUQuota percent, in a systemd scope")
    ap.add_argument("--procs", type=int, help="DOTNET_PROCESSOR_COUNT")
    ap.add_argument("--env", action="append", default=[], help="K=V for the process")
    ap.add_argument("--rings", help="comma-separated counts of rings run at once")
    ap.add_argument("--mixed", help="comma-separated counts of crunching fibers")
    ap.add_argument("--tc0", action="store_true")
    ap.add_argument("--trials", type=int, default=3)
    ap.add_argument("--no-build", action="store_true")
    a = ap.parse_args()
    if not a.no_build:
        build()

    # Without the call-counting delay a method tiers up after thirty calls. With
    # it, rows that keep every hardware thread busy were still running
    # instrumented tier-0 code long after the warm-up.
    env = dict(os.environ, DOTNET_gcServer="1", DOTNET_gcConcurrent="1",
               DOTNET_TC_CallCountingDelayMs="0")
    if a.tc0:
        env["DOTNET_TieredCompilation"] = "0"
    if a.procs:
        env["DOTNET_PROCESSOR_COUNT"] = str(a.procs)
    for kv in a.env:
        k, v = kv.split("=", 1)
        env[k] = v
    prefix = []
    if a.quota:
        prefix += ["systemd-run", "--user", "--scope", "-q", "-p", f"CPUQuota={a.quota}%", "--"]
    if a.one_core or a.cpus:
        prefix += ["taskset", "-c", a.cpus or "2"]

    print("; ".join(x for x in [
        "one core" if a.one_core else "", f"cpus {a.cpus}" if a.cpus else "",
        f"CPUQuota={a.quota}%" if a.quota else "", f"ProcessorCount={a.procs}" if a.procs else "",
        "TieredCompilation=0" if a.tc0 else "", *a.env] if x) or "all cores")

    if a.mixed:
        mixed([int(c) for c in a.mixed.split(",")], a, env, prefix)
        return
    if a.rings:
        counted({f"bjo rings K={k}": (BJO, ["dotnet", "rows.exe", "rings", str(k)], rings_ops(k))
                 for k in (int(k) for k in a.rings.split(","))}, a, env, prefix)
        return
    counted({name: (cwd, cmd, OPS) for name, (cwd, cmd) in ROWS.items()
             if not a.rows or any(r in name for r in a.rows)}, a, env, prefix)


if __name__ == "__main__":
    sys.exit(main())
