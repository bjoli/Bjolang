# Baseline

The numbers the BjoML merge is measured against. Every phase appends a section.

## Machine

| | |
|---|---|
| CPU | AMD Ryzen 9 7900, 12C/24T |
| RAM | 61 GB |
| OS | Fedora 44, kernel 7.1.8-200.fc44.x86_64 |
| .NET | 10.0.107, ServerGC on |
| Date | 2026-09-10 |

## How to reproduce

```sh
dotnet build bench/Cml/Cml.Bench.csproj -c Release
dotnet bench/Cml/bin/Release/net10.0/CmlBench.dll

dotnet bin/Release/net10.0/Bjolang.dll bench/bjolang/cmlbench.bjo
dotnet bench/bjolang/cmlbench.exe
```

Both suites report `ns/op` and `B/op`, three reps, median. The C# suite is the
raw CML layer; the Bjolang suite is the same topology through `spawn`, `sync`
and a scope. `Ring (scoped)` has no C# twin — a scope is a Bjolang construct —
so it is compared against the same C# ring.

## Phase 0 — before the merge

| benchmark | BjoML ns/op | BjoML B/op | Bjolang ns/op | Bjolang B/op | ns ratio | B ratio |
|---|---|---|---|---|---|---|
| Ring (1e6 msgs) | 94.0 | 0.2 | 127 | 280 | 1.35x | 1400x |
| Ring, scoped | 94.0 | 0.2 | 145 | 281 | 1.54x | 1405x |
| Spawn burst (1e6) | 148.2 | 89.1 | 154 | 353 | 1.04x | 3.96x |
| Skewed choose(8) | 72.6 | 40.0 | 294 | 256 | 4.05x | 6.4x |

### What the rows already say

The two ring rows are 127 and 145 ns/op and allocate the same 280 B/op. They
should not be close: only the second opens a `with-cancel`. They are close
because `main` itself runs inside a `Scope` with a live token, so
`ReferenceEquals(token, RootCancel)` in `sync` is false in both, and both build
a `CancellableEvent` plus its closure on every rendezvous. The unscoped row is
not measuring an unscoped program — there is no such thing in Bjolang today.
That is what phase 2f removes.

280 B/op against 0.2 is the seam, not the ring: per parked rendezvous the
language allocates a `RecvEvent`/`SendEvent`, a `CancellableEvent`, the token
branch's closure, a `SyncState` and an `EventAwaiter`, where the C# path parks
one pooled `GetOp` and allocates nothing.

Skewed choose is the widest gap at 4.05x. A `choose` publishes 8 branches and
the cancellation branch makes 9, so the per-branch cost is paid one extra time
per op on top of the per-sync allocations.

Spawn burst is already close on time (1.04x) and 4x on allocation. The extra
bytes are the scope's `Attach` — a `Cml.Sync` per fiber, so a `SyncState` and a
closure each — which phase 2d deletes.

## Test suites at this commit

Both green.

- Bjolang `./run_tests.sh`: 185 groups, 213 error tests, 8 warning tests, 22
  codegen assertions, 3 REPL transcripts, 3 staleness checks.
- BjoML `dotnet run -c Release --project Tests`: 53 passed, 0 failed.
