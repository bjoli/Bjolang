# Baseline

The numbers the BjoML merge is measured against. Every phase appends a section.

## Machine

| | |
|---|---|
| CPU | AMD Ryzen 9 7900, 12C/24T, 2 CCDs |
| RAM | 61 GB |
| OS | Fedora 44, kernel 7.1.8-200.fc44.x86_64 |
| .NET | 10.0.107, ServerGC on |
| Date | 2026-09-10 |

## How to reproduce

```sh
./bench/run.sh
```

Both suites report `ns/op` and `B/op` over five reps, reduced to the **minimum**.

The minimum rather than the median because the skewed-choose row is bimodal on
a multi-CCD part: the sender and the receiver either land on the same chiplet or
they do not, and the two modes are roughly 70 and 150 ns/op with nothing in
between. A median over an odd number of reps reports whichever mode won the coin
toss, which makes two phases incomparable. Allocation needs no statistic at all
— it is deterministic and every rep agrees, which is also what makes it the
reliable signal when a timing row is ambiguous.

The C# suite is the raw CML layer; the Bjolang suite is the same topology
through `spawn`, `sync` and a scope. `Ring (scoped)` has no C# twin — a scope is
a Bjolang construct — so it is compared against the same C# ring.

## Phase 0/1 — before the seam is touched

Phase 1 moved the sources and changed their licence headers and nothing else,
so it shares a row with phase 0. That it is genuinely a no-op is visible in the
allocation column: every figure is bit-identical across the move, which a
behaviour change could not be.

| benchmark | BjoML ns/op | BjoML B/op | Bjolang ns/op | Bjolang B/op | ns ratio | B ratio |
|---|---|---|---|---|---|---|
| Ring (1e6 msgs) | 51.0 | 0.2 | 121 | 280 | 2.37x | 1400x |
| Ring, scoped | 51.0 | 0.2 | 128 | 281 | 2.51x | 1405x |
| Spawn burst (1e6) | 91.0 | 89.1 | 122 | 353 | 1.34x | 3.96x |
| Skewed choose(8) | 66.9 | 40.0 | 231 | 256 | 3.45x | 6.4x |

### What the rows already say

The two ring rows are 121 and 128 ns/op and allocate the same 280 B/op. They
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

Skewed choose is the widest gap at 3.45x. A `choose` publishes 8 branches and
the cancellation branch makes 9, so the per-branch cost is paid one extra time
per op on top of the per-sync allocations.

Spawn burst is the narrowest at 1.34x, and 4x on allocation. The extra bytes are
the scope's `Attach` — a `Cml.Sync` per fiber, so a `SyncState` and a closure
each — which phase 2d deletes.

## Phase 2a — `chan-recv` returns the channel

`Channel<T>` implements `IEvent<T>` and now `INowable<T>` as well, so
`(chan-recv ch)` hands back `ch` and the `RecvEvent` wrapper is gone.

| benchmark | Bjolang ns/op | Bjolang B/op | vs phase 1 |
|---|---|---|---|
| Ring | 113 | 256 | −8 ns, −24 B |
| Ring, scoped | 104 | 257 | −24 ns, −24 B |
| Spawn burst | 126 | 353 | unchanged |
| Skewed choose(8) | 236 | 256 | unchanged |

C# reference this run: ring 44.7, spawn burst 90.8, skewed choose 67.4 ns/op.

The 24 bytes are exactly the `RecvEvent` — an object header and one reference
field. That it is 24 and not 48 on the ring, where each trip does a receive and
a send, is the point: a send still allocates, because it has to carry the value.

Skewed choose does not move and should not. Its event is built once outside the
loop, so the eight wrappers it dropped were eight allocations in total rather
than eight per op.

### A note on the noise floor

Two columns in this file cannot be compared between runs and are recorded only
so that a real change is not mistaken for one of them:

- The C# **spawn burst B/op** is nondeterministic — 38 to 89 B/op across runs of
  the same binary — because the number of `SpawnBatch` arrays a burst allocates
  depends on how the batches happened to fill. The Bjolang spawn row is steady
  at 353 because the scope's per-child bookkeeping dominates it.
- Absolute ns/op drifts a few percent between sessions on this machine.
  Phase-to-phase claims here rest on the allocation columns, which are
  deterministic everywhere except the row named above, and on differences large
  enough to survive a re-run.

## Phase 2d — the scope is told, it does not ask

A fiber carries an `IFiberLanding?`, and `Promise.Complete` calls it. The scope
hands each child one of two adapters it made in its own constructor, so joining
every child — a `SyncState`, a join event, a waiter and a closure each — is
gone. `Attach` is deleted.

Taken out of order: 2b and 2c are both gated on 2f, since until `main` is
unscoped no `sync` in a real program can reach a direct park. 2d is independent
of all of them.

| benchmark | Bjolang ns/op | Bjolang B/op | vs phase 2a |
|---|---|---|---|
| Ring | 123 | 256 | unchanged |
| Ring, scoped | 117 | 256 | unchanged |
| Spawn burst | 74 | 185 | **−52 ns, −168 B** |
| Skewed choose(8) | 274 | 256 | unchanged |

The 168 bytes are the `SyncState`, the promise waiter, the closure over
`(this, reports)` and the waiter list the promise had to grow to hold it.

The ring rows do not move and should not: a scope's bookkeeping is per fiber,
and the ring spawns 1000 fibers against 1,000,000 messages.

### The spawn row is not a ratio

Bjolang now reports 74 ns/op against the C# suite's 95.8, and that does **not**
mean the language beats the runtime it is built on. The two spawn benchmarks
have different children: the C# one does an `Interlocked.Increment` and a
countdown on a `ManualResetEventSlim`, because a pure-C# harness has no scope to
tell it when the burst is over, while the Bjolang one uses the scope as the
completion signal and its child body is empty. The C# row carries a latch the
Bjolang row does not.

Only the Bjolang column moves meaningfully on this row, and 126 → 74 is what
phase 2d is judged on. The cross-suite ratio for spawn is not reported in the
final table for this reason.

## Test suites

Green at phase 0, 1, 2a and 2d.

- Bjolang `./run_tests.sh`: 185 groups, 213 error tests, 8 warning tests, 22
  codegen assertions, 3 REPL transcripts, 3 staleness checks.
- CML `dotnet run -c Release --project BjolangRuntime/Cml/Tests`: 53 passed.
