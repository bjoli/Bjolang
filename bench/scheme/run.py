#!/usr/bin/env python3
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.
"""The classic Scheme benchmarks: Chez Scheme against Bjolang.

The Chez programs are the sources of ecraven's r7rs-benchmarks, unchanged.
They are put together as that suite's `bench` script does (source, then
`common.scm`, then the postlude) and read the suite's own inputs on stdin.
The suite is not copied into this repository; it is cloned at a fixed
commit into `.r7rs-benchmarks` on the first run.

The Bjolang programs are ports in `bjolang/`. They take the same inputs as
command-line arguments (see BENCHMARKS) and print the same `+!CSVLINE!+`
line, so both are timed the same way: inside the program, around the runs
of the benchmark only.

`bjolang-idiomatic/` holds a second Bjolang program for the benchmarks where
a Bjolang programmer would use vecs, comprehensions or `loop` rather than
lists and named lets. It takes the same arguments, and its column is empty
for a benchmark that has none.

Usage:
  ./run.py                    every benchmark, 3 repetitions, minimum taken
  ./run.py fib tak            only these
  ./run.py --reps 5 --no-o3
"""

import argparse
import math
import os
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
SUITE = HERE / ".r7rs-benchmarks"
BUILD = HERE / ".build"
SHIM = HERE / "chez" / "r7rs"
SUITE_URL = "https://github.com/ecraven/r7rs-benchmarks.git"
SUITE_COMMIT = "85f6acdc4cc4e2b857f307ba56bd0ba931dcccd1"

# Each benchmark's Bjolang arguments. They are the values in the suite's
# `inputs/<name>.input`, with a list input given as its length and an
# S-expression or vector input written into the port itself.
BENCHMARKS = {
    "fib":       ["5", "40", "102334155"],
    "fibfp":     ["10", "35.0", "9227465.0"],
    "tak":       ["1", "40", "20", "11", "12"],
    "takl":      ["1", "40", "20", "12", "13"],
    "ack":       ["2", "3", "12", "32765"],
    "cpstak":    ["1", "40", "20", "11", "12"],
    "nqueens":   ["10", "13", "73712"],
    "mbrot":     ["1000", "75", "5"],
    "primes":    ["10000", "1000", "168"],
    "sum":       ["200000", "10000", "50005000"],
    "sumfp":     ["500", "1e6", "5.000005e11"],
    "diviter":   ["1000000", "1000", "500"],
    "divrec":    ["1000000", "1000", "500"],
    "deriv":     ["10000000"],
    "array1":    ["500", "1000000", "1000000"],
    "triangl":   ["50", "22", "1"],
    "quicksort": ["2500", "10000", "1000000"],
    "fft":       ["100", "65536", "0.0", "0.0"],
    "pnpoly":    ["1000000", "6"],
    "puzzle":    ["1000", "511", "2005"],
    "paraffins": ["10", "23", "5731580"],
    "gcbench":   ["1", "20"],
}

TIMEOUT = 300
CSV = re.compile(r"^\+!CSVLINE!\+[^,]*,[^,]*,(.*)$", re.M)


def run(cmd, **kw):
    return subprocess.run(cmd, check=True, **kw)


def fetch_suite():
    if (SUITE / "src").is_dir():
        return
    print(f"Cloning {SUITE_URL} into {SUITE} ...", file=sys.stderr)
    run(["git", "clone", "--quiet", SUITE_URL, str(SUITE)])
    run(["git", "-C", str(SUITE), "checkout", "--quiet", SUITE_COMMIT])


def build_chez(name, level):
    """Assembles the program as the suite does and compiles it ahead of time."""
    out = BUILD / f"chez-O{level}"
    out.mkdir(parents=True, exist_ok=True)
    src = out / f"{name}.scm"
    parts = [SUITE / "src" / f"{name}.scm",
             SUITE / "src" / "common.scm",
             HERE / "chez" / "postlude.scm",
             SUITE / "src" / "common-postlude.scm"]
    src.write_text("".join(p.read_text() for p in parts))
    so = out / f"{name}.so"
    expr = f'(compile-program "{src}" "{so}")'
    run(["scheme", "-q", "--libdirs", str(SHIM), "--compile-imported-libraries",
         "--optimize-level", str(level)],
        input=expr, text=True, stdout=subprocess.DEVNULL)
    return so


def build_bjolang(name, compiler, variant="bjolang"):
    """Builds `variant/name.bjo`, or answers None when the variant has no such
    benchmark."""
    src = HERE / variant / f"{name}.bjo"
    if not src.exists():
        return None
    exe = src.with_suffix(".exe")
    inputs = [src, HERE / "bjolang" / "common.bjo", Path(compiler)]
    if exe.exists() and exe.stat().st_mtime > max(p.stat().st_mtime for p in inputs):
        return exe
    r = subprocess.run(["dotnet", compiler, src.name], cwd=src.parent,
                       capture_output=True, text=True)
    if r.returncode != 0 or not exe.exists():
        print(r.stdout[-2000:], r.stderr[-2000:], file=sys.stderr)
        raise RuntimeError(f"Bjolang build of {name} failed")
    return exe


def measure(cmd, stdin_path=None):
    """Seconds the program reports, or a string saying why there are none."""
    try:
        with open(stdin_path) if stdin_path else open(os.devnull) as stdin:
            r = subprocess.run(cmd, stdin=stdin, capture_output=True, text=True,
                               timeout=TIMEOUT)
    except subprocess.TimeoutExpired:
        return "TIMEOUT"
    m = CSV.search(r.stdout)
    if not m:
        if "overflow" in (r.stderr + r.stdout).lower():
            return "STACKOVF"
        return "CRASH"
    try:
        return float(m.group(1))
    except ValueError:
        return m.group(1)  # INCORRECT


def best(results):
    nums = [r for r in results if isinstance(r, float)]
    return min(nums) if len(nums) == len(results) else next(r for r in results if not isinstance(r, float))


def fmt(v):
    return f"{v:8.3f}" if isinstance(v, float) else f"{v:>8}"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("names", nargs="*", help="benchmarks to run (default: all)")
    ap.add_argument("--reps", type=int, default=3, help="runs per benchmark; the minimum is reported")
    ap.add_argument("--no-o3", action="store_true", help="skip Chez at --optimize-level 3 (unsafe)")
    args = ap.parse_args()

    names = args.names or list(BENCHMARKS)
    unknown = [n for n in names if n not in BENCHMARKS]
    if unknown:
        ap.error(f"unknown benchmark(s): {' '.join(unknown)}")

    fetch_suite()
    compiler = subprocess.run([str(ROOT / "build_compiler.sh"), "--path"],
                              capture_output=True, text=True, check=True).stdout.strip()
    run([str(ROOT / "build_compiler.sh")], stdout=subprocess.DEVNULL)

    levels = [2] if args.no_o3 else [2, 3]
    version = subprocess.run(["scheme", "--version"], capture_output=True, text=True)
    print(f"Chez Scheme {(version.stdout + version.stderr).strip()}, "
          f"{args.reps} runs each, minimum in seconds\n")
    header = (f"{'benchmark':<11}" + "".join(f"  chez -O{l}" for l in levels)
              + "   bjolang   bjo/O2    idiom  idiom/O2")
    print(header)
    print("-" * len(header))

    def ratio(v, base):
        if isinstance(v, float) and isinstance(base, float) and base > 0:
            return v / base
        return None

    ratios = {"bjo": [], "idiom": [], "best": []}
    for name in names:
        row = {}
        inp = SUITE / "inputs" / f"{name}.input"
        for level in levels:
            so = build_chez(name, level)
            cmd = ["scheme", "--libdirs", str(SHIM), "--program", str(so)]
            row[level] = best([measure(cmd, inp) for _ in range(args.reps)])
        for key, variant in (("bjo", "bjolang"), ("idiom", "bjolang-idiomatic")):
            exe = build_bjolang(name, compiler, variant)
            if exe is None:
                row[key] = None
                continue
            cmd = ["dotnet", str(exe)] + BENCHMARKS[name]
            row[key] = best([measure(cmd) for _ in range(args.reps)])

        for key in ("bjo", "idiom"):
            r = ratio(row[key], row[2])
            if r is not None:
                ratios[key].append(r)
        candidates = [r for r in (ratio(row["bjo"], row[2]), ratio(row["idiom"], row[2])) if r is not None]
        if candidates:
            ratios["best"].append(min(candidates))

        bjo_r = ratio(row["bjo"], row[2])
        idiom_r = ratio(row["idiom"], row[2])
        print(f"{name:<11}" + "".join(f"  {fmt(row[l])}" for l in levels)
              + f"  {fmt(row['bjo'])}  {'' if bjo_r is None else f'{bjo_r:8.2f}':>8}"
              + f"  {'' if row['idiom'] is None else fmt(row['idiom']):>8}"
              + f"  {'' if idiom_r is None else f'{idiom_r:8.2f}':>8}", flush=True)

    def gmean(rs):
        return math.exp(sum(math.log(r) for r in rs) / len(rs))

    if ratios["bjo"]:
        print(f"\nGeometric mean, bjolang / chez -O2 over {len(ratios['bjo'])} benchmarks: "
              f"{gmean(ratios['bjo']):.2f}")
    if ratios["idiom"]:
        print(f"Geometric mean, idiomatic / chez -O2 over {len(ratios['idiom'])} benchmarks: "
              f"{gmean(ratios['idiom']):.2f}")
    if ratios["best"]:
        print(f"Geometric mean, faster Bjolang program / chez -O2 over {len(ratios['best'])} benchmarks: "
              f"{gmean(ratios['best']):.2f}")


if __name__ == "__main__":
    main()
