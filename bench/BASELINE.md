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

## Phase 2f — `main` is not in a scope

`RunMainFiber` and `RunMainSync` no longer open a `Scope`. `main` runs with
`Scope == null` and no token, so `sync` takes the branch that skips the
cancellation race.

| benchmark | Bjolang ns/op | Bjolang B/op | vs phase 2d |
|---|---|---|---|
| Ring | 90 | 72 | **−33 ns, −184 B** |
| Ring, scoped | 118 | 256 | unchanged |
| Spawn burst | 73 | 185 | unchanged |
| Skewed choose(8) | 216 | 72 | **−58 ns, −184 B** |

The 184 bytes are the `CancellableEvent`, its token-branch closure, and the
promise waiter the token branch registered.

The ring is now 72 B/op, and that is exactly a `SendEvent` (32) plus a
`SyncState` (40). Nothing else is left on the rendezvous path.

**The scoped ring did not move, and that is the point.** A written-down
`with-cancel` still has a live token and still pays the race, so it still costs
256 B/op. Before this phase both rings paid it, because there was no such thing
as an unscoped program. The gap between 72 and 256 B/op is now a measurement of
what phase 2c would remove, which it was not possible to take before.

### The gate is not met

The stated gate was "the unscoped ring inside `main` is within noise of the
pure-C# ring". It is 90 against 42.1, so it is not. The remaining gap is the
general publish path: a `SyncState` per sync and the commit protocol on top of
it, which is what phase 2b removes and which this phase only made *reachable*.
2f did its own part — the cancellation race is gone from an unscoped `sync` —
but it cannot close that gap on its own.

### Programs that had to change: none

The suite is green with no edit to any `.bjo`. Grepping for a top-level `spawn`
in `main` with no enclosing scope found one, `TestFiles/113_until_cancelled.bjo`,
and it does not rely on the root drain: it spawns a feeder and then receives
every item the feeder sends, so the rendezvous is what waits, not the scope.

## Phase 2b — one park path

A sync offering a single channel operation parks an op with `State == null` and
commits unconditionally. No `SyncState`, no commit protocol, and no
`EventAwaiter` change visible to `SyncAwaiter`, which still sees one awaiter
type. `choose` publishes the general way and is untouched.

| benchmark | Bjolang ns/op | Bjolang B/op | vs phase 2f |
|---|---|---|---|
| Ring | 77 | 32 | **−13 ns, −40 B** |
| Ring, scoped | 113 | 256 | unchanged |
| Spawn burst | 85 | 185 | unchanged |
| Skewed choose(8) | 221 | 72 | unchanged |

The 40 bytes are the `SyncState`. What went with it is not only the allocation:
committing a rendezvous used to call `MarkSynchronized` on two sync states, and
each one takes a lock to walk a nack list that a single-branch block cannot
have.

The ring is now **32 B/op, and all of it is the `ChannelSendEvent`**. A receive
allocates nothing at all: `(chan-recv ch)` is the channel, it parks a pooled
`GetOp`, and the awaiter is pooled too. Phase 2a's note that a send "may become
a struct-backed pooled object if the ring benchmark shows the allocation" is now
the only thing left on this row.

Skewed choose does not move, and that is the design: a `choose` keeps the full
protocol because a losing branch has to be withdrawable.

### The invariant this rests on

An unconditional commit cannot be withdrawn, so it is only sound while a direct
op can never be a branch of a `choose`. Nothing in the type system says so — it
follows from `SyncDirect` being reachable only from the top of a sync and never
from `Publish`. Four tests in `CmlTests` assert it against the parked list:
a bare sync parks with no `SyncState`, a `choose` branch never does, and the two
protocols pair in both directions.

## Test suites

Green at every phase.

- Bjolang `./run_tests.sh`: 186 groups (one added at 2f,
  `TestFiles/205_root_is_not_a_scope.bjo`), 213 error, 8 warning, 22 codegen,
  3 REPL, 3 staleness.
- CML `dotnet run -c Release --project BjolangRuntime/Cml/Tests`: 57 passed
  (four added at 2b).

- Bjolang `./run_tests.sh`: 185 groups, 213 error tests, 8 warning tests, 22
  codegen assertions, 3 REPL transcripts, 3 staleness checks.
- CML `dotnet run -c Release --project BjolangRuntime/Cml/Tests`: 53 passed.
