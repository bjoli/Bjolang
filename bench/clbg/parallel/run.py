#!/usr/bin/env python3
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.
"""Parallel Benchmarks Game programs: Bjolang fibers against Go goroutines.

Each program runs as a whole process with its output going to /dev/null.
Before the timed runs, each program runs at a small size, and its output must
be the same as the output of the single-threaded Go program in ../go. The
single-threaded Bjolang and Go programs run too, for the speed-up.

For each program the table gives the wall-clock seconds and the CPU seconds
(user and system) of the fastest run. CPU / wall is about the number of cores
that were busy.

The parallel Bjolang programs are built with server GC (`--runtime
gc-server=true`, which a project says in its manifest as `(runtime (gc-server
#t))`); the single-threaded ones keep the default.

Usage:
  ./run.py                    every benchmark, 3 runs each
  ./run.py mandelbrot         only this one
  ./run.py --reps 5 --small   the check sizes, for a quick look
"""

import argparse
import hashlib
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
CLBG = HERE.parent
ROOT = CLBG.parent.parent
BUILD = CLBG / ".build"

BENCHMARKS = {
    "mandelbrot":    (16000, 1001),
    "spectralnorm":  (5500, 100),
    "fannkuchredux": (12, 10),
    "binarytrees":   (21, 10),
}
# Single-threaded first, then parallel.
PROGRAMS = ["bjo-1", "go-1", "bjo-par", "go-par"]


def build(names):
    compiler = subprocess.run([str(ROOT / "build_compiler.sh"), "--path"],
                              capture_output=True, text=True, check=True).stdout.strip()
    subprocess.run([str(ROOT / "build_compiler.sh")], stdout=subprocess.DEVNULL, check=True)
    # The parallel programs ask for server GC, as a project that allocates on
    # many cores does in its manifest: with the default workstation GC, 24
    # threads that allocate wait for one collector.
    for d, extra, flags in ((CLBG / "bjolang", "harness.bjo", []),
                            (HERE / "bjolang", "par.bjo", ["--runtime", "gc-server=true"])):
        for name in names:
            src = d / f"{name}.bjo"
            exe = src.with_suffix(".exe")
            inputs = [src, d / extra, Path(compiler)]
            if not exe.exists() or exe.stat().st_mtime < max(p.stat().st_mtime for p in inputs):
                r = subprocess.run(["dotnet", compiler, *flags, src.name], cwd=d,
                                   capture_output=True, text=True)
                if r.returncode != 0 or not exe.exists():
                    print(r.stdout[-2000:], r.stderr[-2000:], file=sys.stderr)
                    raise RuntimeError(f"Bjolang build of {src} failed")
    BUILD.mkdir(exist_ok=True)
    subprocess.run(["go", "build", "-o", str(BUILD / "go-clbg"), "."], cwd=CLBG / "go", check=True)
    subprocess.run(["go", "build", "-o", str(BUILD / "go-par"), "."], cwd=HERE / "go", check=True)


def command(prog, name, arg):
    return {
        "bjo-1":   ["dotnet", str(CLBG / "bjolang" / f"{name}.exe")],
        "go-1":    [str(BUILD / "go-clbg"), name],
        "bjo-par": ["dotnet", str(HERE / "bjolang" / f"{name}.exe")],
        "go-par":  [str(BUILD / "go-par"), name],
    }[prog] + [str(arg)]


def execute(cmd, stdout):
    """Runs cmd and answers (wall seconds, CPU seconds)."""
    start = time.perf_counter()
    p = subprocess.Popen(cmd, stdin=subprocess.DEVNULL, stdout=stdout)
    _, status, usage = os.wait4(p.pid, 0)
    wall = time.perf_counter() - start
    if os.waitstatus_to_exitcode(status) != 0:
        raise RuntimeError(f"{' '.join(cmd)} failed")
    return wall, usage.ru_utime + usage.ru_stime


def output_hash(cmd):
    with tempfile.TemporaryFile() as out:
        execute(cmd, out)
        out.seek(0)
        return hashlib.md5(out.read()).hexdigest()


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("names", nargs="*", help="benchmarks to run (default: all)")
    ap.add_argument("--reps", type=int, default=3, help="runs per program; the fastest is reported")
    ap.add_argument("--small", action="store_true", help="time the check sizes instead")
    args = ap.parse_args()
    names = args.names or list(BENCHMARKS)
    unknown = [n for n in names if n not in BENCHMARKS]
    if unknown:
        ap.error(f"unknown benchmark(s): {' '.join(unknown)}")
    build(names)

    print(f"{os.cpu_count()} logical CPUs; wall seconds / CPU seconds of the fastest of "
          f"{args.reps} runs\n")
    header = f"{'benchmark':<14}" + "".join(f"{p:>16}" for p in PROGRAMS) + \
        "  bjo speed-up  go speed-up  bjo-par/go-par"
    print(header)
    print("-" * len(header))
    for name in names:
        full, small = BENCHMARKS[name]
        arg = small if args.small else full
        expected = output_hash(command("go-1", name, small))
        for prog in PROGRAMS:
            if output_hash(command(prog, name, small)) != expected:
                raise RuntimeError(f"{prog} {name}: the output is wrong")
        # The programs take turns, so that a slow minute of the machine falls
        # on all of them.
        runs = {p: [] for p in PROGRAMS}
        for _ in range(args.reps):
            for prog in PROGRAMS:
                runs[prog].append(execute(command(prog, name, arg), subprocess.DEVNULL))
        best = {p: min(rs) for p, rs in runs.items()}
        line = f"{name:<14}" + "".join(f"{best[p][0]:8.2f}/{best[p][1]:7.2f}" for p in PROGRAMS)
        line += f"  {best['bjo-1'][0] / best['bjo-par'][0]:11.1f}"
        line += f"  {best['go-1'][0] / best['go-par'][0]:11.1f}"
        line += f"  {best['bjo-par'][0] / best['go-par'][0]:14.2f}"
        print(line, flush=True)


if __name__ == "__main__":
    main()
