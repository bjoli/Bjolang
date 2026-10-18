"""Runs a Python solution for bench.py: pyrun.py dayNN.py input.txt.

The clock starts after the interpreter has started, and the solution runs as
__main__. The elapsed time goes to stderr when the solution ends."""
import runpy
import sys
import time

t0 = time.perf_counter()
script = sys.argv[1]
sys.argv = sys.argv[1:]
try:
    runpy.run_path(script, run_name="__main__")
finally:
    sys.stdout.flush()
    print(f"BENCH {time.perf_counter() - t0:.4f}", file=sys.stderr)
