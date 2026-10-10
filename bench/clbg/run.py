#!/usr/bin/env python3
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at http://mozilla.org/MPL/2.0/.
"""Benchmarks Game programs: Bjolang against C#, Go and Chez Scheme.

Every program is single-threaded and follows the same algorithm in each
language. A run is timed as a whole process, start-up included, as the
Benchmarks Game does, with the output going to /dev/null. Before the timed
runs, each program runs at a small size, and its output must be the same as
the output of the Go program.

Usage:
  ./run.py                    every benchmark, 3 runs each, the minimum taken
  ./run.py nbody fasta        only these
  ./run.py --reps 5 --small   the small sizes, for a quick look
"""

import argparse
import hashlib
import math
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
BUILD = HERE / ".build"

# name: (argument at full size, argument at the check size). For the two
# that read standard input, the argument is the n of the fasta file they read.
BENCHMARKS = {
    "nbody":         (50_000_000, 1000),
    "spectralnorm":  (5500, 100),
    "fannkuchredux": (12, 7),
    "binarytrees":   (21, 10),
    "mandelbrot":    (16000, 200),
    "fasta":         (25_000_000, 1000),
    "knucleotide":   (25_000_000, 25000),
    "revcomp":       (25_000_000, 25000),
    "pidigits":      (10000, 27),
}
STDIN = {"knucleotide", "revcomp"}
LANGS = ["bjolang", "csharp", "go", "chez"]
TIMEOUT = 900


def run(cmd, **kw):
    return subprocess.run(cmd, check=True, **kw)


def build_all(names):
    compiler = subprocess.run([str(ROOT / "build_compiler.sh"), "--path"],
                              capture_output=True, text=True, check=True).stdout.strip()
    run([str(ROOT / "build_compiler.sh")], stdout=subprocess.DEVNULL)
    for name in names:
        src = HERE / "bjolang" / f"{name}.bjo"
        exe = src.with_suffix(".exe")
        if not exe.exists() or exe.stat().st_mtime < max(src.stat().st_mtime,
                                                         Path(compiler).stat().st_mtime):
            r = subprocess.run(["dotnet", compiler, src.name], cwd=src.parent,
                               capture_output=True, text=True)
            if r.returncode != 0 or not exe.exists():
                print(r.stdout[-2000:], r.stderr[-2000:], file=sys.stderr)
                raise RuntimeError(f"Bjolang build of {name} failed")
    run(["dotnet", "build", "-c", "Release", "-o", str(BUILD / "csharp"), "--nologo", "-v", "q"],
        cwd=HERE / "csharp", stdout=subprocess.DEVNULL)
    run(["go", "build", "-o", str(BUILD / "go-clbg"), "."], cwd=HERE / "go")
    (BUILD / "chez").mkdir(parents=True, exist_ok=True)
    for name in names:
        so = BUILD / "chez" / f"{name}.so"
        src = HERE / "chez" / f"{name}.ss"
        if not so.exists() or so.stat().st_mtime < src.stat().st_mtime:
            run(["scheme", "-q", "--optimize-level", "2"],
                input=f'(compile-program "{src}" "{so}")', text=True,
                stdout=subprocess.DEVNULL)


def command(lang, name, arg):
    if lang == "bjolang":
        cmd = ["dotnet", str(HERE / "bjolang" / f"{name}.exe")]
    elif lang == "csharp":
        cmd = ["dotnet", str(BUILD / "csharp" / "Clbg.dll"), name]
    elif lang == "go":
        cmd = [str(BUILD / "go-clbg"), name]
    else:
        cmd = ["scheme", "--program", str(BUILD / "chez" / f"{name}.so")]
    return cmd if name in STDIN else cmd + [str(arg)]


def fasta_input(n):
    """The fasta output for n, made by the Go program once."""
    path = BUILD / f"fasta-{n}.txt"
    if not path.exists():
        with open(path, "wb") as f:
            run([str(BUILD / "go-clbg"), "fasta", str(n)], stdout=f)
    return path


def execute(cmd, stdin_path, stdout):
    """Runs cmd and answers (seconds, peak RSS in MB), or a string saying why
    there are none."""
    with open(stdin_path) if stdin_path else open(os.devnull) as stdin:
        start = time.perf_counter()
        p = subprocess.Popen(cmd, stdin=stdin, stdout=stdout, stderr=subprocess.PIPE)
        try:
            _, err = p.communicate(timeout=TIMEOUT)
        except subprocess.TimeoutExpired:
            p.kill()
            p.communicate()
            return "TIMEOUT"
        secs = time.perf_counter() - start
    if p.returncode != 0:
        print(err.decode(errors="replace")[-1000:], file=sys.stderr)
        return "CRASH"
    return secs


def peak_rss(cmd, stdin_path):
    """The peak resident set of one run, in MB, from GNU time."""
    with tempfile.NamedTemporaryFile("r") as tf:
        with open(stdin_path) if stdin_path else open(os.devnull) as stdin:
            subprocess.run(["/usr/bin/time", "-f", "%M", "-o", tf.name] + cmd,
                           stdin=stdin, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           timeout=TIMEOUT)
        try:
            return int(tf.read().strip().splitlines()[-1]) / 1024
        except (ValueError, IndexError):
            return None


def output_hash(lang, name, arg):
    stdin_path = fasta_input(arg) if name in STDIN else None
    with tempfile.TemporaryFile() as out:
        r = execute(command(lang, name, arg), stdin_path, out)
        if isinstance(r, str):
            return r
        out.seek(0)
        return hashlib.md5(out.read()).hexdigest()


def fmt(v):
    return f"{v:8.2f}" if isinstance(v, float) else f"{v:>8}"


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("names", nargs="*", help="benchmarks to run (default: all)")
    ap.add_argument("--reps", type=int, default=3, help="runs per program; the minimum is reported")
    ap.add_argument("--small", action="store_true", help="time the check sizes instead")
    args = ap.parse_args()
    names = args.names or list(BENCHMARKS)
    unknown = [n for n in names if n not in BENCHMARKS]
    if unknown:
        ap.error(f"unknown benchmark(s): {' '.join(unknown)}")

    BUILD.mkdir(exist_ok=True)
    build_all(names)

    header = (f"{'benchmark':<14}" + "".join(f"{l:>9}" for l in LANGS)
              + "   bjo/C#   bjo/Go bjo/Chez" + "".join(f"{'MB ' + l[:4]:>10}" for l in LANGS))
    print(f"wall-clock seconds, minimum of {args.reps} runs\n")
    print(header)
    print("-" * len(header))
    ratios = {l: [] for l in LANGS[1:]}
    for name in names:
        full, small = BENCHMARKS[name]
        arg = small if args.small else full
        expected = output_hash("go", name, small)
        row, mem = {}, {}
        for lang in LANGS:
            got = output_hash(lang, name, small)
            if got != expected:
                row[lang] = "WRONG" if len(got) == 32 else got
                continue
            stdin_path = fasta_input(arg) if name in STDIN else None
            results = [execute(command(lang, name, arg), stdin_path, subprocess.DEVNULL)
                       for _ in range(args.reps)]
            nums = [r for r in results if isinstance(r, float)]
            row[lang] = min(nums) if len(nums) == len(results) else \
                next(r for r in results if not isinstance(r, float))
            mem[lang] = peak_rss(command(lang, name, arg), stdin_path)

        line = f"{name:<14}" + "".join(f" {fmt(row[l])}" for l in LANGS)
        bjo = row["bjolang"]
        for other in LANGS[1:]:
            v = row[other]
            if isinstance(bjo, float) and isinstance(v, float):
                ratios[other].append(bjo / v)
                line += f" {bjo / v:8.2f}"
            else:
                line += f" {'':>8}"
        line += "".join(f" {mem[l]:9.0f}" if mem.get(l) else f" {'':>9}" for l in LANGS)
        print(line, flush=True)

    print()
    for other, rs in ratios.items():
        if rs:
            g = math.exp(sum(map(math.log, rs)) / len(rs))
            print(f"geometric mean of bjolang / {other}: {g:.2f} over {len(rs)} benchmarks")


if __name__ == "__main__":
    main()
