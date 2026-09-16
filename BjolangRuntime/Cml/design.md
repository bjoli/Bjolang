# The CML layer: design

A Concurrent ML implementation for a Scheme-like language that compiles to C#.
This document describes the runtime after the `IThreadPoolWorkItem` + `Fiber`
migration, and records what is deliberately *not* done yet.

## It is one project with Bjolang, not two

This was a separate project, `Bjoml.csproj`, referenced by `BjolangRuntime`. It is
now compiled into `BjolangRuntime` directly. The namespace is still `Bjoml`.

Being two projects had a cost beyond the build graph: the two layers had to agree
through a public interface, and the agreement was what was slow. Bjolang wrapped
every channel operation in a class of its own so it could carry an interface
`IEvent<T>` did not have; every `sync` took the general commit protocol even for a
bare channel operation, where a C# caller would have used the direct awaiter; and
a scope learned that a child had finished by *joining* it, which cost a whole sync
block per fiber.

Those are gone. What replaced them lives *under* `IEvent<T>` rather than beside it:

- `Channel<T>` is its own receive event. `(chan-recv ch)` is `ch`.
- `INowable<T>` — commit without publishing at all, when a partner is already
  parked.
- `IDirectSyncable<T>` — park with no `SyncState`, for a sync block with one
  branch and so nothing to withdraw. Reachable only from the top of a sync, never
  from `Publish`, which is what keeps a `choose` branch withdrawable.
- `IFiberLanding` — a fiber tells its owner it has finished, instead of the owner
  joining it.

Each is a type test at the point of use, never a name the code generator knows, so
a channel operation that reaches `sync` through a helper, a record field or a
`guard` keeps every fast path.

`IEvent<T>` is still the specification. The general path is still there, still
used by `choose`, and still the thing the fast paths are tested against.

## Scope: a compiler backend, not a CML library for C#

There used to be a second surface here — `Cml.SyncAsync`/`SyncAsyncVoid`,
`Channel<T>.GetMessage`/`PutMessage`, and the pooled `CmlValueTaskSource` behind
them — letting a plain `async Task` sync on an event. It is gone.

Everything the language emits goes through `Fiber` and awaits an `IEvent<T>`
directly, so that façade had no callers outside the benchmarks measuring it, and
its own doc comment recorded a leak it could not fix: a `ValueTask` created and
never awaited is never recycled. Deleting it also drops the
`Microsoft.Extensions.ObjectPool` package — its only user — and with it the
`CopyLocalLockFileAssemblies` workaround that existed to get that DLL next to the
probing path of a compiled program.

What remains of the interop surface is deliberate and small: `TaskInterop.FromTask`
(a `Task` coming in), `TaskInterop.Cancellable` (the withdrawable form the language
emits for `task->event`), and `TaskInterop.ToTask` — which has no callers, and is
kept because it is the only path for **.NET calling into Bjolang**.

This is reversible, but it means rewriting the pooled completion source if the
decision changes.

---

## 1. Scheduling

All work goes through
`ThreadPool.UnsafeQueueUserWorkItem(IThreadPoolWorkItem, preferLocal: true)`.

There are no dedicated worker threads. The previous design gave each worker a
`BlockingCollection` and had `Enqueue` always target the *current* worker's queue,
which meant a fiber spawning N children pinned all N to one core with no way for idle
workers to steal them. The .NET pool already implements local queues with stealing,
so `preferLocal: true` keeps the producer-side locality while leaving the work
stealable.

Measured effect: a 480-child fan-out now runs 12–13x faster than serial on 12
physical cores. See `BENCHMARKS.md`. The cost is ~10% on the single-hot-chain ring
benchmark, where the old "never steal" behaviour was accidentally optimal.

### Inline dispatch

`Scheduler.Dispatch` runs a continuation on the completing thread when
`InlineDepth < Scheduler.MaxInlineDepth` (50), otherwise it queues. This is the fast
path and the common case: the thread that completes a rendezvous simply keeps going.

**`MaxInlineDepth` is load-bearing.** It is the only bound on stack growth for a
chain of continuations that each complete another rendezvous.

### ExecutionContext is never flowed

Nothing in the runtime captures or restores an `ExecutionContext`:

| Path | Mechanism |
|---|---|
| scheduler enqueue | `UnsafeQueueUserWorkItem` |
| fiber suspension | builder calls `awaiter.UnsafeOnCompleted` |
| `Task` bridging | `ConfigureAwait(false)` + `UnsafeOnCompleted`, never `ContinueWith` |

This is what makes it safe to enqueue work unsafely everywhere. The hosted language
carries its own dynamic environment (below), so flowing EC would cost an allocation
per await *and* layer C#'s ambient state on top of the fiber's own.

**Consequence:** `AsyncLocal` values set by C# libraries — `Activity`/OpenTelemetry
spans, some logging scopes — do not survive a BjoML await. If you want tracing, put
the span inside your own context object. This is asserted by the
`AsyncLocal does not survive a C# Task await` regression test.

---

## 2. The dynamic environment shim

`FiberContext.Current` is a single `[ThreadStatic] object?`. BjoML knows nothing
about the payload; the language owns it, so installing a context is one pointer
store.

The shim lives in the *builder*, because that is the only place that sees every
suspension point of a fiber:

- **at suspend** — `FiberCore.GetMoveNextAction` re-captures `FiberContext.Current`
  into the state machine box. Re-capturing every time (not just at box creation) is
  what makes a `(parameterize ...)` that spans an await work.
- **at resume** — `FiberStateMachineBox.Run` saves the ambient context, installs the
  fiber's, calls `MoveNext`, and restores in a `finally`.

The save/restore is not optional. Continuations run inline on whichever thread
completed the rendezvous, and that thread may be several frames deep inside a
*different* fiber. A resuming fiber borrows the thread and must hand it back exactly
as it found it.

`(parameterize ...)` compiles to `FiberContext.Push(...)` / `Dispose`.

**Limitation:** the context is only reinstated at *fiber* suspension points. A bare
callback handed to the scheduler from outside a fiber — a nack action, a promise
waiter — runs with whatever context the borrowed thread had. Such callbacks must
therefore never run user code; they should only wake a fiber. `TaskInterop.Cancellable`
respects this (its nack callback only cancels a token).

---

## 3. Fibers, promises and events

| Type | What it is | Language surface |
|---|---|---|
| `Fiber` / `Fiber<T>` | return type of a compiled bjoroutine; a compiler artifact | not first-class |
| `Promise<T>` | write-once cell that is **also** a persistent `IEvent` | first-class; what `spawn` returns |
| `IEvent<T>` | a CML event | first-class; what `sync` takes |

`Bjo.Spawn` returns a `Promise<T>`, not a `Fiber<T>`. That is the whole point: a
promise is composable with `choose`, which a `Task` can never be.

```csharp
var r = await Cml.Choose(
    Cml.Wrap(p.Join(),        x => "done"),
    Cml.Wrap(abort.Receive(), x => "aborted"));
```

`(sync ev)` compiles to `await ev` via `EventAwaitExtensions.GetAwaiter`. Nothing
else in the language suspends, which is what makes CML reasoning work: between two
syncs a fiber is atomic with respect to every other fiber.

### Errors are values

Events must never throw. An exception raised in an event continuation escapes into a
channel's matching loop and is swallowed by the scheduler's catch-all, leaving the
sync block hung forever. So failures travel as `Result<T>` and are only converted
back to exceptions inside a fiber, in an awaiter's `GetResult()`, where the state
machine turns them into `SetException`.

| Location | Throwing here is |
|---|---|
| inside a bjoroutine body | correct |
| inside an awaiter's `GetResult()` | correct |
| inside an `IEvent` continuation | **wrong** |
| inside a `Wrap` mapper | **wrong** (mappers run in the continuation) |
| inside a nack action | **wrong** |

### Task interop

`TaskInterop.Cancellable` is the only form that should be exposed for use in
`choose`. A plain `Task` is already running and cannot be withdrawn, so if a
sibling branch wins, an uncancellable task keeps burning a socket. `Cancellable`
wires a `withNack` to a `CancellationTokenSource` so losing the choose actually tears
the work down.

Both `Promise` and `Cancellable` events are **persistent**: once completed, syncing
again succeeds immediately with the same value. A completed promise in a `choose`
loop therefore wins every iteration and starves its siblings — the same trap as
`Cml.Always`. Document this for language users.

---

## 4. Bugs fixed in this pass

Each has a regression test in `Tests/`, and each test has been verified to fail when
the corresponding fix is reverted.

- **B1** — nack fired when the winner was a nested `choose` under the same
  `withNack`. Event ids are now a *tree*: `choose` mints children under the incoming
  id, `wrap`/`guard` pass it through, and a nack fires only if its id does not cover
  the winner.
- **B2** — two `withNack`s could share an id and the inner one silently overwrote the
  outer one's nack. Nacks are now a list, not a dictionary keyed by id.
- **B3** — a sync block could match *itself*, livelocking a whole thread forever with
  no exception and no diagnostic. `(choose (send ch v) (recv ch))` is legal CML and
  hung the process. Ops belonging to our own `SyncState` are now skipped and
  re-queued in a `finally` on every exit path.
- **B4** — `TryClaim` treated a transient `C` as "already lost", silently dropping
  completions that arrive from another thread. Added `SyncState.TryCommit`, which
  spins past `C` (safe: `C` is never held across user code).
- **B5** — `Scheduler.Start()` raced and could NRE on a partially-published array.
  Gone with the dedicated workers.
- **B6** — no work stealing. Gone with the dedicated workers.
- **B7** — a losing `choose` branch left its `PutOp`/`GetOp` parked in the channel,
  reclaimed only if some *later* operation happened to walk past it. A channel offered
  in a `choose` that then went quiet grew without bound: measured at exactly one
  stranded op per losing branch, 500 out of 500. Now bounded rather than driven to
  zero, by a park counter on the channel, at no measurable cost. See below.
- **B8** — `MarkSynchronized` was a public method that would stomp another thread's
  claim. Precondition now documented; self-committing events go through `TryCommit`.
- **B9** — the `ValueTask` path flowed `ExecutionContext`. Flag stripped at the
  time; the path itself has since been deleted along with the rest of the
  plain-C# façade.
- **B10** — root event id is now `0` and reserved; dead `Operation` helpers removed;
  operation pools are per-`T` statics rather than per-`Channel` instances;
  `withNack` no longer allocates a `Channel<Unit>` per publish.
- **B11** — `Promise.Complete` stored its value *before* claiming the cell, so a
  second, losing `TrySetResult` returned `false` — correctly — having already
  overwritten the winner's value on the way to finding out. Unobservable while
  every payload was a `Unit`. The hosted language's cancellation token carries a
  reason, and "cancelling twice is a no-op" has to mean the first reason is the
  one kept, so the claim now precedes the store.

Two bugs in the *proposed* code were also fixed: `TaskInterop.Cancellable` could not
compile (generic inference through an async lambda) and leaked its
`CancellationTokenSource` whenever the branch **won**; and `PromiseEvent` registered
waiters that were never removed when their branch lost.

### B7 in detail — count parks, not commits

The half of the fix that reads as a rewrite was already done, for unrelated reasons:
the channel had by then moved from `ConcurrentQueue` to intrusive lists under a
per-channel lock, and already had `CleanTakers`/`CleanGivers` to unlink and recycle
synchronized entries. Those were only ever called from the test-only `Pending*Count`
properties, so nothing in production ever ran them.

All that was missing was a trigger, and the cheap one is a **park counter on the
channel**. Every dead entry is necessarily preceded by a park in that same channel, so
counting parks bounds the dead set. `Channel<T>.NotePark` is called from all four park
sites — direct and published, send and receive — with `_lock` already held, and sweeps once parks since the last sweep reach
`max(32, live * 2)` — which amortises the O(n) walk to O(1) per park. The fast path
pays one non-atomic increment.

**The guarantee is bounded, not zero.** A channel offered in exactly one choose and
then abandoned never parks again, so its single loser stays put. That is one entry per
abandoned channel, collectable with the channel itself. B7 was *unbounded* growth;
bounded is the fix. `Tests/CmlTests.cs` asserts this the only way it honestly can, by
checking that the residue does not grow with the workload: 500 and 4000 iterations
must leave the same small number stranded, and with the sweep disabled 4000 iterations
strand all 4000.

#### The expensive version, and why it is not here

The obvious design is to have the committing side drive cleanup: `SyncState` records
every channel a block parks in, and `MarkSynchronized` cleans each. It gives a strictly
stronger guarantee — zero stranded, including for the abandoned channel — and it was
implemented, measured, and removed. It cost **36 ns/op** on Select/Choose, roughly a
quarter of the whole operation.

Subtractive measurement, `bench/Diag --mode select --reps 25`, medians:

    baseline, no cleanup at all                      135 ns/op   40 B/op
    extra SyncState fields present, no registration  148         56
    registration, its lock removed                   167         56
    registration, cleanup body disabled              170         56
    full commit-driven version                       171         56
    park counter (current)                           132         40

Read that table before optimising anything here. **The lock is 4 ns and the cleanup
walk is 1 ns** — both were the author's stated suspects and both are noise, exactly as
in the `MarkSynchronized`/`NextEventId` investigation recorded above. The cost is the
registration machinery itself: two more reference fields on a per-`Cml.Sync`
allocation, the stores into them, and the interface dispatch to reach the channel.

Note also that the park counter is *faster than not fixing B7 at all* (132 against
135), because sweeping keeps the intrusive lists short and the matching loop therefore
walks fewer dead nodes.

Two hazards worth keeping, if anyone reinstates a commit-driven scheme:

- **Store the channel, not the operation.** A matcher can unlink and recycle an op at
  any moment, so a stored op reference may already belong to somebody else.
- **Clean outside the state lock.** Parking takes the channel lock and then the state
  lock, so cleaning while holding the state lock inverts that order and deadlocks.

A note on the old test. `characterise: losing branches accumulate in an idle channel`
could never have detected any of this: it read `PendingReceiveCount`, which calls
`CleanTakers()` *before* counting, so reading it performed the very reclamation the
test was meant to prove happened on its own. It passed with the bug fully present. The
replacement tests read `RawPendingReceiveCount`, which does not clean.

### The ambient token — where the skewed-choose row's time actually goes

`RunMainFiber` opens a scope around `main`, so `Dyn.Current.Cancel` is a live
promise in every fiber of every compiled program and every `sync` races it. The
Bjolang twin of `bench/Cml/Program.cs` was 2x the C# one on the skewed-choose row,
and the token was the suspect. It is about half the gap, and the measurements below
say which half.

Protocol: both twins built in Release and run interleaved in one session (C#,
Bjolang, C#, Bjolang, ...), five alternations of five reps, minimum reported, on a
5900X (12 cores / 24 threads, two CCDs), .NET 10. Each hack applied on its own and
reverted before the next. `ns/op` is the skewed row unless a ring row is named.

    experiment                                   skewed    B/op   ring
    baseline, before this pass                      160     184    119
    baseline, after the heap settle (below)         171     184    121
    1. `sync` ignores the token entirely            129      72     90
    2. no registration in `Promise.Publish`         168     184      —
    2b. no registration for a direct op's watch     153     152    106
    3. sender uses `await ch.Send(v)` directly      106     112      —
    3b. one send event per thread, not per send     173     120    118
    3a. one object for the token branch (kept)      171     152    114
    one registration per fiber, not per park (kept) 151     112    112
    C# twin                                          88      40     57

Hack 2 is the prescribed one and it measures nothing: a `choose`'s registration on
the token costs less than the noise. Hack 2b is the same subtraction for the
`CancelWatch` a single channel operation registers, and that one is worth 18 ns on
the skewed row and 8 on the ring — the lock, the list add and the amortised prune
together.

The awaiter pool was counted rather than subtracted: a counter on
`EventAwaiter.Rent`'s `new` fallback reports **5869 misses** over a whole suite run,
against roughly 30 million rents. The thread-static pool is not thrashing under
ping-pong, so there was nothing to fix there.

#### What was kept

**One object for a `choose`'s token branch.** `CancellableEvent.Publish` used to
publish the token through `Promise.Publish`, which allocated a closure, its
delegate and a `Promise.Waiter`. `TokenWatch` is a `PromiseWaiter` that holds the
`SyncState`, the branch id and `onSync`, and commits the sync itself. 184 -> 171
ns/op and 184 -> 152 B/op; the ring rows do not change, because a single channel
operation never builds one.

**A settled heap before each rep, and the same collector on both sides.** The
Bjolang harness did not `GC.Collect()` before a rep and the C# one did, so a
collection earned by the previous row landed inside the next row's timed region:
the skewed row's median was 409 ns/op against a minimum of 160. With the settle the
median is 190 and the minimum is unchanged, which is what the minimum was chosen
for. Separately, `Cml.Bench.csproj` asks for server GC and the compiled Bjolang
program got workstation GC; `bench/run.sh` now sets `DOTNET_gcServer` so the two
tables are comparable, and both harnesses print the collector they ran under.

#### What was measured and rejected

**A syntactic fast path for `(sync (chan-send ch v))`.** Hack 3 — a sender that
awaits `ch.Send(v)` directly, as the C# twin does — is worth 65 ns, which is what
made this look like the biggest item. It is not the event object: hack 3b keeps
every other part of the path and only stops allocating a `ChannelSendEvent` per
send, and it costs **32 B/op and no time at all**. The 65 ns is the rest of what
hack 3 removes: the `CancelWatch` and its registration (18 ns, hack 2b) and the
pooled `EventAwaiter` indirection — an extra delegate hop and an interlocked
handover per rendezvous — where the C# path stores the fiber's own resume delegate
straight into the `PutOp`. An intrinsic that only removed the event object would
buy the allocation and nothing else, at the price of a fast path attached to syntax
rather than to the value: an event reaching `sync` through a variable or a `guard`
would take the slow path. Not worth it for 32 bytes.

**Parking the fiber's own resume delegate, with no `EventAwaiter`.** Built,
measured and reverted. A direct park does not need an awaiter object: the op has
slots for a resume delegate and a value, which is exactly how the C# twin's
`ChannelSendAwaiter` works. `IDirectSyncable` grew `StartDirect` / `ParkDirect` /
`TakeDirect` — rent the op, park it with the fiber's own resume in it once the
fiber decides to suspend, read the value back out — and `SyncAwaiter` became the
struct that holds the op. That removes a pooled object, a delegate hop and the
awaiter's interlocked handover per rendezvous.

It works, all tests pass, and **with the token switched off it is 13% faster**:
ring 90 -> 78, skewed choose 129 -> 115 ns/op. With the token on — which is every
real program — the ring goes 129 -> ~200 ns/op while the skewed row barely moves.
The regression tracks the per-park `CancelWatch` registration: with the watch
allocated but not registered the ring is 88.

What it is not, each ruled out by measurement rather than by argument:

- GC. Gen-0, gen-1 and gen-2 counts per rep are the same either way (the ring's
  gen-2 count is 20 in both), and B/op moves only by the watch's 8 extra bytes.
- The op pools. 4639 `new` fallbacks against 10 million parks.
- The commit-during-park path, where `ParkDirect` has to queue the resume instead
  of completing inline: 80 times in 10 million parks.
- `NotePark` at the two new park sites: removing it again changes nothing.
- The state-machine box holding the watch alive across the suspension: building
  the watch inside `UnsafeOnCompleted` and reaching it back through the op's link
  measures the same.
- Where the registration sits. Registering in `GetAwaiter` as the old path did,
  and only arming and parking later, measures the same.

So the awaiter indirection really is worth ~13 ns, and something about combining
the op-carries-the-resume shape with a per-park registration on a shared token
costs three times that. Whatever it is did not show up in the six places it
should have, and `perf` is not available on this machine to look further. The
change is not on the branch; this paragraph and the numbers are what is left of
it. Anyone reinstating it should first make the per-park registration go away —
which is the persistent-registration item below, and which this measurement moved
from "the remaining token cost" to "the thing that has to happen first". That has
since been done, so this is worth another attempt.

**A lock-free waiter list on `Promise`.** Worth at most the 18 ns of hack 2b, and
only part of that is the lock — the rest is the list add and the prune. Removing
the `Monitor` means a Treiber push whose prune has to detach the whole stack to
filter it, and a `Complete` that runs while a prune holds the detached stack must
not lose those waiters. That is a lost-wake-up hazard in the cancellation path,
which is the path with no test coverage from ordinary programs, for 18 ns.

#### One persistent registration per (fiber, scope) — kept

This is where the rest of the token cost was, and it is now gone. A parked single
channel operation used to allocate a `CancelWatch` and register it on the scope's
token every time; `FiberWatch` is the same watch, registered once and re-armed per
park.

    Ring                  124 -> 112 ns/op, 72 -> 32 B/op
    Ring (nested scope)   110 ->  92 ns/op, 72 -> 32 B/op
    Skewed choose(8)      171 -> 151 ns/op, 152 -> 112 B/op
    Spawn burst             unchanged

**Where the identity came from.** The obvious reading is that this needs a logical
fiber identity the runtime does not have — a second per-thread slot saved and
restored by every state-machine box, set at spawn, inherited through nested calls.
It does not. `DynEnv` already has every property wanted: it is inherited through
nested bjoroutine calls, re-captured into the box at every suspension, and
replaced wholesale when a scope is entered. An environment carrying a cell
therefore means "this fiber, in this scope", which is exactly the pair a
registration on a scope's token belongs to. The one thing missing was freshness at
spawn, and `Scope.Start` supplies it by handing the child an environment without
the parent's cell.

The cell is not exclusive, and does not need to be: a fiber that cannot claim it
— a child spawned straight from `Bjo.Spawn`, a thunk on a `blocking` thread —
falls back to a `CancelWatch` of its own. Correctness never depends on winning it,
only speed does.

**The three hazards.** A lost wake-up: arming is interlocked and the token is
re-read straight after, so a cancel landing between the check and the park is seen
by one side or the other. A stale park: a cancelled sync leaves its op on the
channel's list pointing at the claim, so `ITakeable` carries a generation and the
op records the one it was parked with — the fiber's next park cannot be matched
through the last one's leftovers. A registration outliving its fiber: an idle cell
reports itself abandoned to the token's prune and marks itself unregistered as it
does, and the next park registers again.

Publishing order is what keeps "a token firing after a commit does not undo it".
The op is parked while the claim is only *held* — a state the channel may take and
the token may not — so a rendezvous that commits inline is out of reach of a token
firing in the same instant. Arming comes after, and only then is the park visible
to the token.

**What the remaining gap is.** The ring is 112 against the C# twin's 58 and the
skewed row 151 against 88. The direct-park rewrite above is worth ~13 ns of that
and is now unblocked, since the per-park registration it collided with is gone.

---

## 5. Known issues

### Channel ordering is not guaranteed

A contended operation is re-queued at the **tail**, so FIFO is not preserved and a
sender can be starved under sustained contention. Worth stating in the language
manual so the runtime is not held to FIFO later.

### Blocking bridge

`Bjo.RunToCompletion` blocks the calling thread. Do not call it from a thread-pool
thread: the fiber needs pool threads to make progress and you are holding one hostage.
