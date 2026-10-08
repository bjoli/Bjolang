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

- **at suspend** — `FiberBuilder.GetMoveNextAction` re-captures `FiberContext.Current`
  into the state machine box. Re-capturing every time (not just at box creation) is
  what makes a `(parameterize ...)` that spans an await work.
- **at resume** — `FiberResume.Run` saves the ambient context, installs the
  fiber's, calls `MoveNext`, and restores in a `finally`.

The box is made at a fiber's first suspension. For a *called* bjoroutine it is a
`CalledFiber`, which is the call's promise as well; a call that never suspends
allocates nothing, and its result travels back inside the `Fiber<T>`, as with
`ValueTask<T>`. A *spawned* fiber's promise, a `FiberCore`, exists before its body
runs, so its box is a separate `FiberStateMachineBox`.

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
syncs nothing interrupts a fiber, and every exchange with another fiber is a sync.
Fibers still run in parallel on the pool, so state two of them share outside a
channel needs a lock, as it would between threads.

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
| inside a `Wrap` mapper, under a language `sync` | correct (applied on the syncing fiber; see B13) |
| inside a `Wrap` mapper, under `Cml.Sync` | **wrong** (mappers run in the continuation) |
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
- **B12** — a channel match skipped a partner whose state was in `C`, the same
  mistake as B4 on the pairing side. `(choose (send a) (recv b))` against
  `(choose (send b) (recv a))`, started together: each side claims itself to
  publish its second branch, finds the other claimed, skips it and parks, and
  all four ops sit in their channels with nobody left to walk them. A compiled
  program hit it on its first such rendezvous, since the cold JIT holds the
  claim for milliseconds. A claim holder now resolves a claimed partner through
  `SyncState.TryPair`: of two claimed states, the one with the lower order (a
  rank taken on first contention) waits with its claim, and the other gives its
  claim back, waits for the partner to leave `C` and looks again. A claim holder
  never waits on a lock and only ever waits for a higher order, so every chain
  of waiting ends at a thread making progress. A direct send or receive, which
  holds no claim, waits out the partner's `C` (`TrySyncWaiting`), as `TryCommit`
  does.
- **B13** — a `wrap` function ran on whichever thread committed: the partner
  inside its own sync, a timer thread, a pool thread completing a promise. It
  saw that thread's dynamic environment, and an exception from it unwound into
  the partner's sync, whose fiber died, while the syncing fiber was never
  resumed. A sync started by the language (`EventAwaiter`, and
  `sync/blocking`) now gives its `SyncState` a `WrapHost`. The winning
  branch's innermost `WrapSink` hands itself and its value to the host, which
  wakes the syncing side with no value yet. `TakeResult` then runs it on the
  fiber: it applies its mapper and calls the next wrap out, which finds the
  host already holding one and applies its own directly, and so on to the
  awaiter. A sync through `Cml.Sync` has no host and maps where it commits, as
  before; the runtime's own wraps there are pure conversions. Cost: 8 bytes on
  every `SyncState`, and about 9 ns on a wrapped cross-fiber rendezvous of
  250 ns.

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
since been done, and the direct park is back — see "The direct park, again" below.

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

**What the remaining gap was.** The ring was 112 against the C# twin's 58 and the
skewed row 151 against 88, with the direct-park rewrite above unblocked.

#### The direct park, again — kept

With one registration per fiber, the op-carries-the-resume shape no longer
collides with anything. A single channel operation under the fiber's registration
rents its op in `GetAwaiter` and parks it in `SyncAwaiter.UnsafeOnCompleted`, with
the state machine's own resume in the op's slot — the shape of the C# twin's
`ChannelSendAwaiter`. The partner resumes the fiber straight from the op; there is
no `EventAwaiter`, no `onSync` hop and no handover.

Three things the park had to get right, since it now happens *after* the fiber
decided to suspend:

- A cancelled op stays on the channel's list until the sweep recycles it, so the
  reason is stored on the cell, not the op, and a cancelled awaiter never touches
  its op again. A linked park counts toward the sweep (`NotePark`), as the old
  direct park did.
- From the moment the op is parked, a partner may resume the fiber on another
  thread. Every later step names its generation and fails harmlessly once the
  park is over: `Arm` is a CAS from (gen, Held).
- A park that found its partner waiting returns without scheduling its own
  resume, so the claim is settled before the fiber can run `End`.

Same suite, same protocol, two runs:

    Ring                  117 -> 88-92 ns/op    32 B/op either way
    Ring (nested scope)   100 -> 78-79 ns/op
    Skewed choose(8)      160 -> 148-152 ns/op  112 B/op either way
    Spawn burst             unchanged

The C# twin is 54-56 on the ring and 89-98 on the skewed row. A sync that cannot
claim the fiber's registration, and a `choose`, still go through `EventAwaiter`.

A sync with no token at all — the REPL, a hand-built environment — parks the
same way with no claim, as the C# twin does.

#### A `choose`'s token branch on the same registration — kept

The receiver of the skewed row syncs a `choose`, and each of its parks still
built a `CancellableEvent` and a `TokenWatch` registered under the token's lock.
Now the fiber's registration is the token branch: the branches are published
against the sync's own `SyncState`, and only if none committed inline is one more
event id reserved and the cell armed with it. The token commits through
`TryCommit(id)` like any branch, so an available branch still wins, and a branch
committing after the token fired still keeps its value.

    Skewed choose(8)      148-152 -> 144-149 ns/op, 112 -> 72 B/op, gc0 3 -> 1/rep

The 32 B/op left over the C# twin is the send event.

#### Where the token's cost is now — measured, not yet acted on

With every per-park object gone, making `sync` ignore the token still takes the
ring from ~88 to ~73 and the skewed row from ~150 to ~118. Removing it from one
side at a time says which side:

    token removed from     ring     skewed
    nothing               82-87    145-162
    `choose` only         93-95    142-151
    direct ops only       72-81    133-138
    both                  69-77    114-130

So it is the claim protocol on a direct park — `TryBegin` and `Arm` on the
fiber's cell, and the partner's `TryTake`, which drags the cell's cache line to
the partner's core and back every rendezvous. Allocation is not involved.

#### The partner's half of the claim, on the op — kept

The partner now takes a watched op by clearing `Operation.ParkedGen`, a plain
store under the channel lock it already holds, to a line it is already
writing. It never touches the fiber's cell, and its side of a rendezvous has
no interlocked instruction and no interface call. The token, which fires at
most once, takes the same lock to withdraw the op (`IParkSite.CancelParked`).

The generation that tells a stale reference from a live park moved with it.
The op mints one each time it is watched and keeps the counter across
recycling, so a token holding an op that was taken, recycled and parked again
by another fiber names an old generation and is refused. The cell keeps a
generation of its own for the owner's and the token's steps, but nothing on a
channel's list points at the cell any more. `ITakeable` lost its generation:
only the one-shot `CancelWatch` uses it.

Old and new runtimes published side by side and run interleaved, eleven runs
each over two sessions (min, median):

    Ring                  85, 89-94 -> 80-81, 86-89 ns/op
    Ring (nested scope)   74-78, 80 -> 70-74, 75-81 ns/op
    Spawn burst             unchanged
    Skewed choose(8)        unchanged (a choose does not take this path)

About half of what removing the token from direct ops was worth. The rest is
the owner's two compare-exchanges on its own cell, `TryBegin` and `Arm`. `Arm`
is the fence that keeps a token firing during the park from being lost.
`TryBegin` is only there because two fibers can share a cell (a child started
straight from `Bjo.Spawn` inherits its parent's environment). It could be a
plain store if every spawn path gave the child an environment without the
cell, as `Scope.Start` already does.

A deterministic test drives each order of the race by hand: a partner first
(the token is refused), the token first (once only, and a sender passes the
op by), and a stale generation on a reused op. The fiber-level race test cannot
reach the first order reliably, because a partner resumes the parked fiber
inline: with the check sabotaged to ignore the generation it still passed,
and the deterministic one failed.

#### A token that cannot unregister — measured, not a cost

`Promise` prunes waiters amortised and has no unregister. An intrusive node the
fiber unlinks would remove `FiberWatch`'s abandonment protocol (the `Gone`
state) and nothing else. The cell's generation guards against stale parks on
channel lists, and the fallback is for two fibers sharing one environment, and
neither depends on how the registration leaves the token. Stubbing out the
fiber's registration altogether moved no row of the suite (ring 81-87,
skewed 141-154), because a fiber registers once per scope, not per park.

#### One fiber per cell, and one compare-exchange per park — kept

`TryBegin` existed because two fibers could hold the same cell: a child
started straight from `Bjo.Spawn` inherited its parent's environment, and
with it the cell. That is now closed where it opens. `Bjo.Spawn` starts every
child with `FiberContext.ForChild()`, which `DynEnv` answers without its cell,
and `blocking`/`spawn/thread` hand their thread the same copy. Compiled code
cannot start two bjoroutines on one environment any other way: every call is
awaited where it is made, and every spawn goes through `Bjo.Spawn`. So the
owner writes the cell with plain stores, and the claim before a park is gone,
with the fallback for a fiber that lost it (`CancelWatch`, the `ITakeable`
link on ops and `CancellableEvent`'s use for a `choose`).

The prune is what made a plain `TryBegin` unsound on its own: it may mark an
idle cell unregistered at any moment, and a plain store over that would leave a
cell believing it is registered. So there is no `Held` state at all. The owner
fills in the park while the cell is idle and moves it straight to armed with
one compare-exchange, which fails against the prune's mark and is also the
fence the lost-wake-up argument needs. A cell found unregistered is armed
first and registered second: registered idle, a token that has already fired
asks it whether it is abandoned, it says yes and drops itself again, and the
arm loops forever. That was the first version, and the cancel tests hung on
it.

Arming before the park is published is what makes the generation on the
cell's arm unnecessary. Armed after `Park`, as before, a partner could resume
the fiber on another thread and the arm would land on its next park. Armed in
`GetAwaiter`, nothing can resume the fiber yet. The cost is that the token can
now find a park whose op is not on the list, and the channel lock decides who
resumes the fiber: `CancelParked` answers `Published` (the token resumes it)
or `Unpublished` (the owner's `Park` finds the op withdrawn, parks nothing and
resumes it), and an owner that meets a partner inline clears its own claim so
that a late token is refused. The re-read of the token after `Park` and
`Settle` went with it. A park that finds the cell already armed throws: two
fibers on one cell is a bug somewhere else, and failing there beats corrupting
another fiber's park.

Old and new runtimes published side by side, 16 interleaved runs each (min,
first quartile, median, ns/op):

    Ring                  79 / 86 / 88    -> 77 / 80 / 85.5
    Ring (nested scope)   72 / 77 / 78.5  -> 65 / 72 / 74
    Skewed choose(8)     145 / 148 / 149.5 -> 139 / 144 / 147
    Spawn burst           76 / 80 / 81    -> 75 / 83 / 83.5

The spawn row's median is within its noise, and the one thing added to it is
the type test in `FiberContext.ForChild`; the parent there has no cell, so
nothing is copied. B/op is unchanged on every row.

#### `chan-put` and `chan-get`: an operation without its event — kept

The syntactic fast path for `(sync (chan-send ch v))` was rejected above
(hack 3b) because it would be attached to a spelling, and because keeping one
send event per thread bought 32 B/op and no time. `chan-put` is the same saving
as a function of its own, as Guile fibers has `put-message` beside
`put-operation`: what it does does not depend on how a call is written, and
the event forms stay for `choose`, `wrap` and passing an operation around.

It is faster than hack 3b predicted, because it removes more than the
allocation. `sync` reaches a channel's fast paths through type tests on the
event (`INowable`, `IDirectSyncable`) and interface calls; `chan-put` calls
`TryDirectSend` and rents its `PutOp` directly, and parks through the
channel's one cached `ChannelSendSide`. Everything after that is the same
watched park (`SyncOp.Watched`), so the token is raced exactly as `sync` races
it. `chan-get` is `sync` on the channel, which never allocated.

`bench/bjolang/cmlbench.bjo` with each `(sync (chan-send ...))` and
`(sync (chan-recv ...))` replaced, six interleaved rounds (min, first quartile,
median, ns/op, and B/op):

    Ring                  77 / 80 / 82.5, 32 B   -> 62 / 65 / 68, 0 B
    Ring (nested scope)   69 / 74 / 75,   32 B   -> 61 / 64 / 64, 0 B
    Skewed choose(8)     144 / 149 / 151.5, 72 B -> 132 / 136 / 137, 40 B
    Spawn burst             unchanged

The choose row's sender uses `chan-put`; the 40 B left is the receiver's
`SyncState`.

The suite now carries the pair as rows of its own, `Ring` and
`Ring (put/get)`, `Skewed choose(8)` and `Skewed choose(8), put`. Run side by
side in one process, six runs: the ring 80-92 against 63-73 (minimum), and the
choose 147-172 against 146-164. The choose's time is the receiver's, and a
cheaper sender does not move it; the separate-file comparison above, which
showed 151 -> 137, ran each variant in its own process and caught that row in
different modes.

What the event costs `sync` on the ring is its allocation, not the type tests
on it. `chan-put` given back a dummy `ChannelSendEvent` per send, kept alive
with `GC.KeepAlive` and otherwise unused, measured no faster than the event
row (minimum 82-117 against 79-86 over five runs). So a base class for the two
channel events, replacing the interface tests with one class test, would buy
nothing. The note under hack 3b that one cached send event per thread "bought
no time" does not hold for the runtime as it is now.

#### `promise-join` is the promise — kept

`(promise-join p)` used to be `Cml.Wrap(p.Join(), convert)`: a wrap event per
join, and a closure and its delegate every time the wrap was published, to
turn BjoML's `Result<T>` into Bjolang's `(Result Exception a)`. With the
`SyncState` a join needs because a promise had no `INowable`, joining a
promise that had already landed cost 168 B. `Promise<T>` now implements
Bjolang's join event as well, converting as it delivers through a `JoinWaiter`
of its own, and answers `INowable` once it has landed, so such a join commits
without publishing anything. A million joins of a landed promise: 74 -> 12
ns/op and 168 -> 0 B/op. Starting a fiber and joining it: 362 -> 230 B/op, the
time set by the spawn.

#### A promise's value as an event — kept

`(cancelled ct)`, the nack a `with-nack` hands its generator, and the timer
behind `TimeoutViaCombinators` were each `Cml.Wrap(p.Join(), r => r.Value)`:
the value of a promise that is never completed with a failure, at the cost of
a wrap and a closure and delegate per publish. `PromiseValue<T>`
(`Promise.ValueEvent()`) delivers the value through a waiter of its own and
answers `INowable` once the promise has landed.

    (sync (cancelled ct)) on a fired token        57 -> 11 ns/op, 168 -> 24 B/op
    a worker's choose of a receive and (cancelled ct)  180 -> 160 ns/op, 408 -> 304 B/op

The 24 B is the `PromiseValue` itself, made per `(cancelled ct)`. Caching one
per token would need a token class of its own, for 24 bytes. Most of the
worker's 304 is the `choose` and the program's own `wrap`, whose closure per
publish is the same cost this removed, in a combinator that can be published
many times at once and so has nowhere else to keep its `onSync`.

**Event ids as a plain increment — measured and rejected.** `NextEventId` is an
interlocked increment with one writer, nine per sync of an eight-way `choose`.
A plain increment measured within the noise on every row, so the interlocked one
stays; it costs nothing it would be worth reasoning away.

---

## 5. Hardware counters, against Hopac

Hopac allocates six times what this runtime does on the ring (208 B/op against
0) and was faster on it, so allocation is not what separates the two. What does
was measured with hardware counters.

### How

`bench/perf/drive.py` runs one row of `bench/perf/bjolang/rows.bjo` or its Hopac
twin `bench/perf/hopac` under `perf stat`, at 2 and at 12 reps of a million
operations, and divides the difference by ten million: startup, JIT compilation
and the warm-up are in both runs and cancel. The counters are user-space only,
which is all an unprivileged process may count at `perf_event_paranoid` 2.
`--one-core` pins the process to one CPU; `--tc0` sets
`DOTNET_TieredCompilation=0`.

`--tc0` matters. With tiering on, parts of BjolangRuntime were still running
tier-0 code after fifteen million operations, call counting
(`JIT_CountProfile32`) was 3-8% of all instructions, and Hopac's numbers carried
the same residue. Compare with it off.

To attribute, `perf record` needs `DOTNET_PerfMapEnabled=1` and
`DOTNET_EnableWriteXorExecute=0`. With W^X on, JIT-compiled code runs from a
file-backed alias, perf never consults the perf map, and every managed frame is
an unnamed address.

    DOTNET_TieredCompilation=0 DOTNET_PerfMapEnabled=1 DOTNET_EnableWriteXorExecute=0 \
      taskset -c 2 perf record -e instructions:u -c 100000 -- dotnet rows.exe ringput 12
    perf report --no-children --stdio --sort sym

### What it says

Per operation, median of three, `--tc0` (5900X, .NET 10):

    one core               instr  cycles  IPC  br-miss  L1d-miss
    Ring (put/get)          1698     630  2.69   4.88     33.7
    Hopac ring               841     277  3.03   0.16      8.7
    Skewed choose, put      4056    1291  3.14   0.86      4.6
    Hopac choose            2363     725  3.26   0.17      9.1

    all cores              instr  cycles  IPC  br-miss  cpu-ns  wall-ns
    Ring (put/get)          1894    1465  1.29   6.27     430       85
    Hopac ring              1096     373  2.94   0.52     118      105
    Skewed choose, put      2433     794  3.06   0.12     173      170
    Hopac choose            2561     800  3.20   0.41     202      195

On one core the path is twice Hopac's, and mispredicts thirty times as often. On
the choose the one-core row is the one that counts: there the sender is never
already waiting, so every sync takes the full park path, where on many cores the
receiver mostly finds an offer and commits on the poll.

**The ring's instructions, one core** (`perf record`, share of all user
instructions; mispredicts follow roughly the same spread):

    Monitor enter and exit on the channel, with the __tls_get_addr
      they cost inside libcoreclr                                   ~17%
    the token: AmbientRace (7.7% on its own), Cell, Arm, End, Watched ~14%
    the two awaiter layers: ChanPut/SyncOp GetAwaiter, GetResult,
      UnsafeOnCompleted                                              ~14%
    write barriers, and InlinedMemmoveGCRefsHelper copying an
      awaiter struct with references into the state machine box     ~8.5%
    Scheduler.Dispatch and CalledFiber.Run                            ~7%
    op rent and recycle                                               ~6%

Hopac's hop is mostly allocation and collection — `RhpNewFast`, `memset`, write
barriers and the GC's plan phase are about 30% of it — and its channel logic is
a few virtual calls on continuation objects. It takes no `Monitor`: its locks
are its own `Interlocked` spin locks.

**On many cores the cost is scheduling.** The ring's wall time is Hopac's or
better, but its CPU time is four to five cores' worth for a chain that has one
runnable fiber at a time. Its code ran on 68 different threads in one recording,
and 15% of all cycles were pool workers spin-waiting for work
(`ThreadNative_SpinWait`). That spinning is what keeps the wall time down:
with `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0` the ring went from 62-79 to
167-184 ns/op. A resumed fiber runs inline until `MaxInlineDepth` and then
crosses the pool, and an idle worker that is spinning picks it up on another
core, which is where the L1 misses come from. Hopac pushes a woken job onto its
worker's own stack and keeps going on one core.

---

## 6. Known issues

### Channel ordering is not guaranteed

A contended operation is re-queued at the **tail**, so FIFO is not preserved and a
sender can be starved under sustained contention. Worth stating in the language
manual so the runtime is not held to FIFO later.

### Blocking bridge

`Bjo.RunToCompletion` blocks the calling thread. Do not call it from a thread-pool
thread: the fiber needs pool threads to make progress and you are holding one hostage.
