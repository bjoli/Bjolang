#!/usr/bin/env python3
"""Times the AoC solutions in Bjolang, OCaml and Python, without startup.

Usage: bench/bench.py INPUT_DIR [-n RUNS] [YY-NN ...]

INPUT_DIR holds the inputs as YY-NN.txt, for example 25-03.txt for day 3 of
2025. Each program measures itself: the clock starts after its runtime has
started, and stops when the program exits. So the times leave out process and
runtime startup. They include reading the input, and the .NET JIT. The table
shows the best of RUNS runs, in seconds. Each run must give the same output in
all three languages; a difference is marked with "!=". A day that has a
dayNN-avx.bjo, a solution with (std simd), gets its time in the "avx" column,
and its output must be the same too.
"""
import argparse
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
AOC = os.path.dirname(HERE)
BUILD = os.path.join(HERE, "_build")
COMPILER = os.path.join(AOC, "..", "..", "bin", "compiler", "Bjolang.dll")
HOOK = os.path.join(BUILD, "hook", "StartupHook.dll")


def stale(target, *sources):
    return not os.path.exists(target) or any(
        os.path.getmtime(s) > os.path.getmtime(target) for s in sources)


def build(year, day):
    """Builds the Bjolang and the timed OCaml program for one day."""
    base = os.path.join(AOC, year, f"day{day}")
    exe = base + ".exe"
    if stale(exe, base + ".bjo", os.path.join(AOC, year, "common.bjo")):
        subprocess.run(["dotnet", COMPILER, f"day{day}.bjo"], cwd=os.path.join(AOC, year),
                       check=True, stdout=subprocess.DEVNULL)
    native = os.path.join(BUILD, f"{year}-day{day}.native")
    if stale(native, base + ".ml", os.path.join(HERE, "bench.ml")):
        # bench.ml comes first, so its clock starts before the solution runs.
        subprocess.run(["ocamlopt", "-I", "+unix", "-I", "+str", "unix.cmxa", "str.cmxa",
                        "-I", BUILD, "-o", native,
                        os.path.join(HERE, "bench.ml"), base + ".ml"],
                       cwd=BUILD, check=True)
    avx = base + "-avx.bjo"
    avx_exe = None
    if os.path.exists(avx):
        avx_exe = base + "-avx.exe"
        if stale(avx_exe, avx, os.path.join(AOC, year, "common.bjo")):
            subprocess.run(["dotnet", COMPILER, f"day{day}-avx.bjo"], cwd=os.path.join(AOC, year),
                           check=True, stdout=subprocess.DEVNULL)
    return exe, native, base + ".py", avx_exe


def run(cmd, env=None):
    """Runs a program and returns its output and the time it measured."""
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=300,
                       env={**os.environ, **(env or {})})
    times = re.findall(r"^BENCH (\S+)$", p.stderr, re.M)
    if p.returncode != 0 or not times:
        sys.exit(f"failed: {' '.join(cmd)}\n{p.stderr}")
    return p.stdout, float(times[-1])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("inputs")
    ap.add_argument("-n", type=int, default=3)
    ap.add_argument("days", nargs="*")
    args = ap.parse_args()
    os.makedirs(BUILD, exist_ok=True)
    if stale(HOOK, os.path.join(HERE, "StartupHook", "StartupHook.cs")):
        subprocess.run(["dotnet", "build", "-c", "Release", "-o", os.path.dirname(HOOK),
                        os.path.join(HERE, "StartupHook")], check=True,
                       stdout=subprocess.DEVNULL)
    names = args.days or sorted(f[:-4] for f in os.listdir(args.inputs)
                                if re.fullmatch(r"\d\d-\d\d\.txt", f))
    print(f"{'day':8}{'bjolang':>9}{'avx':>9}{'ocaml':>9}{'python':>9}")
    for name in names:
        yy, day = name.split("-")
        year = "20" + yy
        if not os.path.exists(os.path.join(AOC, year, f"day{day}.bjo")):
            continue
        exe, native, py, avx_exe = build(year, day)
        inp = os.path.join(args.inputs, name + ".txt")
        cmds = [(["dotnet", exe, inp], {"DOTNET_STARTUP_HOOKS": HOOK}),
                ([native, inp], None),
                ([sys.executable, os.path.join(HERE, "pyrun.py"), py, inp], None)]
        best, outs = [], []
        for cmd, env in cmds:
            results = [run(cmd, env) for _ in range(args.n)]
            outs.append(results[0][0])
            best.append(min(t for _, t in results))
        avx = "        -"
        if avx_exe:
            results = [run(["dotnet", avx_exe, inp], {"DOTNET_STARTUP_HOOKS": HOOK}) for _ in range(args.n)]
            outs.append(results[0][0])
            avx = f"{min(t for _, t in results):9.3f}"
        mark = "" if all(o == outs[0] for o in outs) else "  != outputs differ"
        print(f"{name:8}{best[0]:9.3f}{avx}" + "".join(f"{t:9.3f}" for t in best[1:]) + mark)


if __name__ == "__main__":
    main()
