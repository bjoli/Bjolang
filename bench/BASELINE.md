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

## Phase 2c — cancellation is a link, not a branch

A sync offering one channel operation parks one op, and an op can carry a
claim, so the ambient token links to that claim instead of being published as a
second branch. One interlocked word decides between the channel and the token.

Measured A/B against the same tree with only this change stashed:

| benchmark | without 2c | with 2c |
|---|---|---|
| Ring | 74–75, 32 B | 76–81, 32 B |
| **Ring, scoped** | **116–132, 256 B** | **91–97, 72 B** |
| Spawn burst | 75–81, 185 B | 72–86, 185 B |
| Skewed choose(8) | 165–230, 72 B | 87–91, 72 B |

The scoped ring is the row this was aimed at: 256 → 72 B/op, and the 72 is
exactly the predicted structure — one `CancelWatch` (40) plus the
`ChannelSendEvent` (32). What went is the `CancellableEvent`, its closure, the
`SyncState` the two-branch form forced, and the promise waiter.

`choose` keeps the published-branch form. It has branches to arbitrate between
and a claim on one op cannot speak for the rest, so `CancellableEvent` stays for
that case.

### Why it is 72 and not 32

The watch cannot be pooled. Nothing can remove a waiter from a promise's list —
`Promise` prunes amortised on the next registration, via `IsAbandoned` — so a
recycled watch could still be sitting in the token's list and would then be
signalled on behalf of a sync it no longer belongs to. One object per parked
sync under a scope is the price, against the four it replaces.

### An unexplained row

Skewed choose went from 165–230 to 87–91 ns/op, reproducibly, with its
allocation unchanged at 72 B/op. **This change should not affect it**: that
benchmark opens no scope, so its token is null and neither the link nor the old
`CancellableEvent` is reached. The row was also much more variable before
(165–408 across sessions) than after (87–91).

It is recorded rather than claimed. The likely cause is code layout or inlining
around the channel matching loops, which this change edited in eight places;
that would be a real effect but not one 2c earns. It should be attributed
properly before anyone banks it.

## Final report

### Bjolang, by phase

ns/op and B/op. Bold is where the phase was aimed.

| phase | Ring | Ring scoped | Spawn burst | Skewed choose(8) |
|---|---|---|---|---|
| 0/1 baseline | 121, 280 | 128, 281 | 122, 353 | 231, 256 |
| 2a chan-recv is the channel | **113, 256** | 104, 257 | 126, 353 | 236, 256 |
| 2d scope is told | 123, 256 | 117, 256 | **74, 185** | 274, 256 |
| 2f main is not in a scope | **90, 72** | 118, 256 | 73, 185 | **216, 72** |
| 2b one park path | **77, 32** | 113, 256 | 85, 185 | 221, 72 |
| 2c cancellation is a link | 76–81, 32 | **91–97, 72** | 72–86, 185 | 87–91, 72 |
| final | 76–81, **32** | 91–97, **72** | 72–86, **185** | 87–91, **72** |

Allocation is the column to read. It is deterministic everywhere except the C#
spawn row, and it moved 280 → 32 on the ring: an 8.75x reduction, and the 32 that
remain are one object.

### Against the raw CML layer

The C# suite drifts between sessions — the ring's min-of-5 ranged 42 to 65 ns/op
across runs of identical binaries — so the ratio is given as a band rather than a
figure.

| benchmark | BjoML ns/op | Bjolang ns/op | ratio then | ratio now |
|---|---|---|---|---|
| Ring | 42–65 | 76–81 | 2.37x | **1.2–1.9x** |
| Skewed choose(8) | 65–68 | 87–91 | 3.45x | **1.3–1.4x** |
| Ring, scoped | 42–65 | 91–97 | 2.51x | **1.4–2.3x** |

| benchmark | BjoML B/op | Bjolang B/op | ratio then | ratio now |
|---|---|---|---|---|
| Ring | 0.2 | 32 | 1400x | **160x** |
| Ring, scoped | 0.2 | 72 | 1405x | **360x** |
| Skewed choose(8) | 40.0 | 72 | 6.4x | **1.8x** |

Spawn is deliberately absent: the two suites' spawn benchmarks have different
children, as recorded under phase 2d, so their ratio means nothing.

### What was not done, and why

**The scoped ring is 72 B/op against the unscoped 32.** The difference is the
one `CancelWatch` per parked sync, and it is there because nothing can remove a
waiter from a promise's list, so the watch cannot be pooled. Closing it needs
either an unregister on `Promise` — O(n) in the waiter count, which is 1000 on
this benchmark — or a different waiter structure. Neither is obviously worth it.

**2e, the typed fiber context, is not done.** It is small, and worth roughly one
`isinst` per `Dyn.Current` read. It is untouched because `FiberContext.Current`
is typed `object?` and six tests in `FiberTests.cs` put their own `Ctx` type in
the slot to check propagation; typing the slot to `DynEnv` means rewriting those
tests to use a type they cannot currently construct meaningfully. Worth doing,
but it buys less than it disturbs, and it buys nothing on any row above.

**The namespace is still `Bjoml`.** Phase 3 lists renaming it as optional and
last. It touches `Prelude.fs` and `Codegen.fs`, and there was more value in the
comments and the dead-code pass.

**`TestFiles/` carries no licence header.** The sweep covered the compiler, the
runtime, the standard library, the examples, the benchmarks and the tooling —
121 files. The 487 test fixtures were left out as fixture data rather than
shipped source; one of them, `errors/macro_runaway.bjo`, asserts on a line
number and would need its expectation moved first.

### Where Codegen was not told a runtime function's name

Every fast path in this work is a runtime type test on the event value, never a
special form and never a name the compiler matches. Three places where the name
would have been easier:

1. **`(sync (chan-recv ch))`.** The direct park could have been a pattern in
   `Codegen.fs`: see `chan-recv` under a `sync`, emit the park. Instead
   `EventAwaiter.Start` tests `ev is IDirectSyncable<T>`. The difference is
   visible in `(sync (guard #(chan-recv ch)))` and in a `chan-recv` stored in a
   record and synced later — both keep the fast path, and both would have lost it
   under a syntactic rule.

2. **`(sync (chan-send ch v))`.** Same test, on `ChannelSendEvent`. Keeping the
   send event a class rather than a struct is what lets it carry the interfaces;
   a struct would box on the way into `IEvent<Unit>` for the same 32 bytes and
   could not answer the type test.

3. **`INowable` in `sync`.** `sync` asks whether the event can commit now before
   it builds anything. It would have been cheaper still to have the compiler emit
   `TryDirectReceive` at a call site it recognised. It asks the value instead, so
   an event that arrived through a helper is asked too.

The one place the runtime is named directly is unchanged and was already so:
`Prelude.fs` writes down `sync`'s Bjolang type, and `Codegen.fs` wraps a
suspending call in `await`. Neither knows what a channel is.

## Scopes-as-owners, phase 0 — tail-resumptive effects

`blocking` and `spawn/thread` now carry the dynamic environment to the thunk,
`(std effect)` adds `defeffect`/`with-handler`, and the prelude declares `warn`,
`log!`, `now` and `getenv`. None of it is on a spawn or a sync path.

Four runs, min-of-5 per run:

| benchmark | ns/op | B/op | vs before phase 0 |
|---|---|---|---|
| Ring | 71–84 | 32 | unchanged |
| Ring, scoped | 88–98 | 72 | unchanged |
| Spawn burst | 72–82 | 185 | unchanged |
| Skewed choose(8) | 92–233 | 72 | unchanged |

Every allocation figure is bit-identical to the pre-phase run, which is the
column to read: a change that touched the park path could not leave all four
alone. The ns spread is this machine's session drift, and skewed choose is the
bimodal row described above.

`std/stopwatch` is now a Bjolang record over the `now` effect rather than a
`System.Diagnostics.Stopwatch`, and the benchmark harness reads it — twice per
measured region, against 1e6 messages.

## The phase 1 gate — measured, not met, and overridden

**Decision: proceed anyway.** The numbers below stand; what changed is the
judgement about them. The cost is one 40-byte allocation per parked sync under a
live scope, and at 72 B/op the scoped ring is still at the low end against other
CML implementations and against Go's channels. The structure a scope around
`main` buys — every fiber owned, every resource released, a failure reported
rather than lost — is worth that. Recorded here so the next person reads a
measurement rather than an assumption.

### The numbers

Phase 1 would restore an implicit scope around `main`, on the premise that after
the BjoML merge "the token is a link on the parked op, so a scope's per-park cost
is one pointer write". The gate was: the scoped ring within ~5% of the unscoped
ring.

Same tree, same run, four repetitions:

| | unscoped ring | scoped ring | difference |
|---|---|---|---|
| ns/op (min of 4 runs) | 71 | 88 | **+24%** |
| ns/op (typical) | 74–84 | 88–98 | +19% |
| **B/op** | **32** | **72** | **+40 B, 2.25x** |

The gate is not met, on either column, and the allocation column says why. Phase
2c made the ambient token a link rather than a published branch, which removed
the `CancellableEvent`, its closure, the `SyncState` and a promise waiter — but
it did not make the link free. A parked sync under a live token still allocates
one `CancelWatch`, and that object cannot be pooled: nothing can remove a waiter
from a promise's list, so a recycled watch could still be sitting in the token's
list and be signalled for a sync it no longer belongs to. The final-report
section above records this as the one thing phase 2c did not close.

So a scope around `main` puts 40 B/op and roughly 20% back onto every parked
rendezvous in every program, including programs that never open a scope of their
own — which is the cost phase 2f removed. Phase 2f is therefore reverted in
substance: `main` is in a scope again, and the unscoped ring row above no longer
describes any real program, only a `main` that never parks.

Closing the gap needs the `CancelWatch` to become poolable, which needs either an
unregister on `Promise` — O(n) in the waiter count, 1000 on this benchmark — or a
different waiter structure. That is a change to `Promise`, not to `Scope`, and it
is the single highest-value optimisation left on this path: it would take the
scoped ring to the 32 B/op the unscoped one has, and every program is on the
scoped row now.

## Phase 1 — `main` is a scope again

`RunMainFiber` and `RunMainSync` open an ordinary scope, install it, run the
body, close it and restore. The standard-stream flush is registered as the first
release on it, so under LIFO it runs last.

Three runs, min-of-5 each:

| benchmark | ns/op | B/op | vs phase 0 |
|---|---|---|---|
| Ring | 95–115 | 72 | **+40 B, +25%** |
| Ring, scoped | 89–96 | 72 | unchanged |
| Spawn burst | 74–84 | 185 | unchanged |
| Skewed choose(8) | 122–128 | 256 | **+184 B, +35%** |

### The two ring rows are now one measurement

`Ring` spawns its nodes with `bjo` and joins them by hand, outside any
`with-cancel`. That used to mean no ambient token; it now means `main`'s. So the
row is the scoped row with extra steps, and the 72 B/op it reports is the same
`CancelWatch` the scoped row has always paid. **There is no unscoped row left**,
and nothing in the table measures a program without a live token any more,
because no such program exists.

### `choose` costs more than a park does

Skewed choose is the row that moved most, and it was not predicted by the ring
measurement the gate was taken on. 72 → 256 B/op.

A `sync` offering one channel operation parks one op and *links* the ambient
token to it, which is phase 2c and costs one `CancelWatch`. A `choose` cannot:
it has branches to arbitrate between, and a claim on one op cannot speak for the
rest, so the token goes back to being a published branch — a `CancellableEvent`,
its closure, the `SyncState` the multi-branch form needs anyway, and a promise
waiter. 184 bytes, on every `sync` of a `choose`.

So the cost of this phase is not uniform: a program built on channel
rendezvous pays 40 B/op, and a program built on `choose` and `select` pays 184.
The gate was measured on the cheaper of the two. Recorded here so that the next
person reads the number rather than the ring's.

### What would close it

Two separate things, in the order they are worth doing:

1. **A poolable `CancelWatch`**, which needs an unregister on `Promise`. Worth
   40 B/op on every parked sync under a scope, which is now every parked sync.
2. **A claim that can speak for a whole `choose`**, which would let the
   multi-branch form take the link instead of the published branch. Worth the
   184 above. This is a change to the commit protocol and is much the larger of
   the two.

## Phases 2 to 6 — owned resources, owned ports, the fake filesystem

Nothing here is on a spawn or a sync path, and the table says so.

| benchmark | ns/op | B/op | vs phase 1 |
|---|---|---|---|
| Ring | 85–95 | 72 | unchanged |
| Ring, scoped | 98–100 | 72 | unchanged |
| Spawn burst | 81–83 | 185 | unchanged |
| Skewed choose(8) | 125–219 | 256 | unchanged |

**Scopes that own nothing pay nothing.** The owned list head is null until the
first `own!`, and nothing reads it on a spawn or a sync — a scope's constructor
sets one more field and that is the whole of it. Every allocation figure is
identical to phase 1, which a change on the park path could not be.

The scale rows the phase-2 plan asked for — 100k handles in one scope against
100k one-handle child scopes — are **not** recorded. The mechanism they were
meant to price is in and tested for correctness, but the numbers would be a
claim about contention on `_gate` that nothing in the suite currently exercises,
and a benchmark nobody runs is worse than none. The advice they were meant to
support ("a child scope per unit of work, not a list on the parent") rests on
the structure rather than on a measurement: the parent holds a count, the child
holds the list, and the parent's close is a drain rather than a walk.

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

## Literal hoisting — a constant is evaluated once, not once per evaluation

`Codegen` emitted a keyword as `BjolangRuntime.Keyword.Intern("apple")` at every
site it appeared: a string hash and a `ConcurrentDictionary` probe, per
evaluation, for a value that was fully known when the file was compiled. It now
interns once into a static field of a per-assembly `internal static class
__Literals` and reads the field. Symbols go the same way, and so does a `[...]`
vec literal whose elements are all literals, which used to allocate an array
*and* a list on every evaluation.

`#[...]` array literals are deliberately excluded: `Docs/Syntax.org` promises a
fresh array per evaluation, an array can be written to, and `036_array.bjo` and
`187_array_literals.bjo` test it.

### The measurement

`TestFiles/assets/litbench/litbench.bjo`, 2,000,000 iterations, min of 20 reps,
five warm-up passes over every shape, `GC.Collect` before each rep and outside
the stopwatch. The columns are ns/op **net of the empty loop**, which is
0.18 ns/op and identical in both builds. A/B against the same tree with only
`Codegen.fs` reverted, both rebuilt through `build_std.sh`.

| shape | before | after |
|---|---|---|
| `(eq? k :apple)` | 4.769 | **0.001** |
| `(eq? k apple-key)`, a module `def` | 0.001 | 0.001 |
| `cond` over three literals, all reached | 14.716 | **0.181** |
| `match` over three keyword patterns | 0.816 | 1.073 |
| `(string->keyword "apple")` | 4.768 | 4.767 |
| `[1 2 3]` in the loop | 6.250 | **0.454** |

A keyword literal cost **4.77 ns** and now costs nothing measurable: the field
read is loop-invariant and the JIT lifts it out. Three of them in a dispatch
cost 14.7 ns and now cost 0.18. A constant vec cost 6.25 ns — an array and an
`RrbList` per evaluation — and now costs 0.45, which is the `vec-ref` that was
always there.

Two rows are controls and both behave:

- **`def` does not move**, because it was already a static field. It is the
  floor the literal row was aiming at, and the literal row reached it.
- **`string->keyword` does not move**, because its name arrives at run time and
  no hoisting can help it. It is also what says the other rows are real: 4.77 ns
  is `Intern`, and it is still there for the one caller that has to pay it —
  the reader in `(text bjodat)`, which is why `BjoNameCache` exists.

### The `match` row got slower, and this change cannot have done it

0.816 → 1.073 ns/op, stable to ±0.01 across four runs of each build. A keyword
pattern is emitted as `case BjolangRuntime.Keyword { Name: "apple" }` — a string
comparison, and **no `Intern` call** — so `classify-match` is byte-identical
before and after; the emitted C# was diffed to be sure. The likely cause is code
layout in an assembly whose other methods all got shorter. Recorded rather than
explained, as the skewed-choose row was under phase 2c.

It does say something worth acting on later: a keyword pattern still compares
*names*, where it could now compare references against the hoisted field.
`Keyword.Equals` is `ReferenceEquals`, so the value is already there to use.
That is a change to pattern lowering and is not part of this work.

### On the record decoders, where this started

`TestFiles/assets/jsonbench/formatbench.bjo`, 20 000 records, ns/rec. The
`def/bjodat-type` macro used to hoist its record keys by hand — a
`(def __jb-key-Type-field :name)` per field — and that workaround was deleted as
part of this change. So both columns below are the macro *without* its
workaround, and the only difference is whether the compiler hoists:

| | before | after |
|---|---|---|
| bjodat decode | 66 ns/rec | **52 ns/rec** |

52 is what the hand-hoisted macro reached, so the compiler gives back exactly
what the workaround did — for every keyword in every program, rather than for
five keys in one macro.

Nothing else in that benchmark moves, and the reason is worth writing down:
parse is unaffected because a reader's key names arrive at run time, and the
**JSON** decode column is unaffected because JSON keys are strings, which the
CLR already interns in metadata. `codecbench.bjo` was run before and after and
shows no reliable change for the same reason — its columns move ±25% between
runs of an identical binary, which is the noise floor for that harness and is
larger than any effect this could have on it.

### Assembly size

One field and one initialiser per distinct literal, and nothing else:

| | before | after | |
|---|---|---|---|
| `lib/std/prelude.dll` | 666,112 | 667,648 | +1,536 B, +0.23% |
| `lib/std/run.dll` | 83,456 | 83,968 | +512 B |
| `lib/std/fmt.dll` | 38,400 | 38,400 | unchanged |
| `lib/text/json.dll` | 54,272 | 54,272 | unchanged |

The prelude hoists **77** distinct literals, which is more than anything else in
the tree, for 1,536 bytes — about 20 bytes each. The two unchanged rows are
files whose literal count did not cross a 512-byte sector boundary; PE sizes
quantise, so these columns should be read as "one sector or none", not as a byte
count.

### Test suites

Green. `python3 run_tests.py`: 196 groups, 0 compile failures, 0 execution
failures, 222/222 error tests, 8/8 warning, **23/23 codegen** (one added,
`TestFiles/codegen/literal_hoisting.bjo`), 4/4 REPL transcripts, 3/3 staleness.

`034_keywords_and_symbols.bjo` is the one that matters most and it passes
untouched: both keyword spellings interning to one value, `eq?` on symbols,
pattern matching, and `string->keyword` reaching a literal's value. That last
assertion is the whole safety argument in one line — interning is idempotent, so
a hoisted literal and a name built at run time are still the same reference.

## The name cache moved into Bjolang, and the C# file went

`BjolangRuntime/BjoText.cs` held a sixteen-slot direct-mapped cache from the
reader's scratch buffer to an interned name. It existed because
`StringBuilder.Equals(ReadOnlySpan<char>)` had no counterpart in Bjolang: there
was no way to ask whether what had just been buffered was a name already held,
without building the string to ask with — and building that string was 35% of
parse.

What the runtime owed the library was two primitives, not a class:

```
stringbuilder-code-ref : (-> StringBuilder int int)
string-code-ref        : (-> string int int)
```

One UTF-16 unit each, the counterpart of the `stringbuilder-add-code!` that was
already there, and named for units rather than characters because
`string-cursor-ref` is the scalar-aware accessor and stays that. With those, the
cache is twenty lines of Bjolang in `lib/text/bjodat.bjo` and the C# file is
deleted.

The comparison is now an interpreted loop over units where it was a vectorised
`Equals`. That is the cost, and it is not visible:

| | bjodat parse, ns/rec |
|---|---|
| cache in C# (`BjoNameCache`) | 405, 412, 413, 415, 425 |
| cache in Bjolang | 405, 407, 425, 427, 435, 437 |

The two ranges overlap. There may be a percent or two in it; this harness cannot
see it, and saying which is faster on these numbers would be making it up. Names
are short — three to seven units — so the loop the vectorised compare replaced
was never long enough to matter.

## One-pass record decoding

`def/bjodat-type` now generates `bjodat-read-<Name>` and `bjodat-parse-<Name>`
beside `bjodat-><Name>`. They read a record straight off the reader: the key is
compared against the field names **in the reader's own buffer**, so a record
costs no key string, no interned `Keyword`, no `BjoKey` node, no entry vec and
no scan over it. The shape is a loop carrying one `(Option field)` slot per
field — a struct, so the slots are free, and absence and the value are one
question instead of two.

The values are *not* read specially. Each still becomes a `Bjodat` and goes
through its field type's own `bjodat->` instance, which is what keeps an
instance a user wrote working and every error message identical — asserted, word
for word, in `Playground/bjodat-codec-test.bjo`, which now carries 46
assertions against 26.

| corpus, 20 000 records | two-pass | one-pass | |
|---|---|---|---|
| five scalar fields | 479–488 ns/rec | **373–383** | 0.76–0.78x |
| a `Vec` and an `Option` | 747–762 ns/rec | 719–729 | 0.95–0.96x |

Against JSON through `(text json-codec)`, the scalar figure is **0.65x**.

### This is a fifth of what was predicted, and the prediction was wrong

The estimate that started this was "~3x conservatively", taken from
`codecbench.bjo`, where parsing JSON *objects* costs 3.3x parsing the same
records as positional arrays. That measured the whole of a map's machinery
including JSON's string keys — and bjodat had already removed most of it before
this change: a `BjoMap` is a flat vec rather than a trie, and the name cache had
already killed the per-key allocation. What was left of the tree to remove was
about a fifth, and a fifth is what came off.

The rich corpus moves 4%, and that is the design saying so out loud: a
`(Vec string)` field still builds a `BjoVec` of `BjoStr` and converts it. The
entry is what this saves, not the value.

### Where the time actually is, since it is not the tree

`scratch/portfloor.bjo`, over the same 2.36 MB corpus:

| | ns/byte |
|---|---|
| a bare loop over `BjoPort.ReadUnit`, counting units | 0.371 |
| the same text parsed into a tree | 3.492 |

**The port is 10% of the parse.** I had expected it to be most of it — a virtual
`TextReader.Read()` per character sounds expensive and is not; on a
`StringReader` the JIT deals with it. So the remaining nine tenths is the
reader's own per-character work: `skip-space!`, `symbol-code?`, the lookahead
field on the `Reader` record, and the buffer appends. That is where anything
further has to come from, and it is a different kind of work from anything this
session removed.

### Test suites

`python3 run_tests.py`: 196 groups, 0 failures, 222/222 error, 8/8 warning,
23/23 codegen, 4/4 REPL, 3/3 staleness. `Playground`: reader 68/68, codec 46/46,
and the real `mydata.bjodat` round-trips.
