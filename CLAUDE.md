Hi Claude

Please also have a look at the file "CLAUDE-local.md"

Just some small infos to get you started:

To build and run all tests, ./run_tests.py . This runs all the files in TestFiles that start with 3 digits. Tests are reported as failed if compilation fails, or a test outputs "FAILURE: ..." or "FAIL_COMPILE"

Smaller runs, for a change to the standard library:

- `./run_tests.py --only programs`: the numbered, error, warning and codegen
  tests, without the toolchain checks (packages, graph builds, staleness,
  debug symbols, signals, REPL). About 50 seconds.
- `./run_tests.py --touching std/compress`: only the tests that import the
  module, directly or through another module. Takes `std/compress`,
  `'(std compress)'` or a path to a `.bjo` file; repeat it or separate
  modules with commas. A module few tests use takes seconds. Everything
  imports the prelude, so for a prelude change use `--only programs`.
- Run the full suite before committing.

A full test run takes about a minute. Run it in the foreground with a timeout of about 300 seconds, and read the result when it finishes. Do not start it in the background and then wait for it with sleep loops. If a run takes much longer than 2 minutes, something is wrong: stop and find out why.

If you change anything in bjolangruntime you need to rebuild it. It is a different c# project in BjolangRuntime

To rebuild the standard library, please use ./build_std.sh

## Running and waiting for commands

- Tool calls in one batch run at the same time, not in order. Put only
  independent calls in one batch.
  - Steps that depend on each other go in one shell command, joined with
    `&&` or under `set -e`.
  - One git command per message.
  - One edit per message to the same file. Parallel edits to one file have
    left it truncated.
- To wait for a background job, use one command that polls every 10
  seconds until the job is done, for example:

      while ! grep -q '^done' /tmp/job.txt; do sleep 10; done; cat /tmp/job.txt

  Have the job write a last line (here `done`) when it ends, or poll for
  the file it writes last. A command is stopped after 600 seconds whatever
  timeout it asks for, and a job started with `&` inside it is stopped with
  it. Start a long job as a background job, and poll it with commands that
  each end within 600 seconds (for example `for i in $(seq 1 50)`). Do not run a long `sleep` and then check: the
  job may have finished minutes before. Do not send several `sleep`
  commands in one batch: they all start at once.

## Standard library

is in lib/std and is documented in .bjodoc files

## Comments

Write clear, direct engineering comments. Keep them succinct without being cryptic:

- **Standard technical vocabulary:** Say "returns" (never "answers"), "iterates/traverses" (never "walks"), "rejects/raises" (never "refuses"), and "suspends/async" (never poetic metaphors like "twin" or "colours").
- **No essays or design philosophy:** Do not philosophize (e.g., "Own things the GC cannot price"). State the contract, invariants, and mechanisms directly.
- **Structure over narrative:** Avoid dense, run-on sentences stitched together with dashes and semicolons. For non-trivial functions or macros, use short bulleted lists:
  - Summary / Purpose
  - Options / Modes (e.g., `#:mode`)
  - Lifecycle & Cleanup (e.g., `with-open` vs `with-scope`)
  - Error handling (what raises and when)
- **Contracts, not line-by-line narration:** Document what the caller needs to know (invariants, aliasing protections, resource ownership). Do not write stream-of-consciousness narratives of how the code runs.

## Benchmarks

are written using either (std stopwatch) for quick one-off measurements, or (std bench) for proper benchmarks.
