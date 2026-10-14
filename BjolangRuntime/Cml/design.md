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

**How many workers spin.** Classified by stack, 13% of the ring's cycles were
pool workers waiting for work (`PortableThreadPool+WorkerThread`,
`LowLevelLifoSemaphore`, `LowLevelSpinWaiter`) and 0.1% `Monitor` contention.
In 5 ms windows a median of five threads spun at once (90th percentile six,
most nine) while one thread had the chain. Go bounds this: a spinning M is
allowed only while spinners are fewer than half the busy Ps, and `wakep` starts
one only when none is spinning, so a single chain has at most one. The .NET pool
has no such cap: every worker that dequeues an item requests another, and each
spins its budget before it sleeps.

`DOTNET_ThreadPool_UnfairSemaphoreSpinLimit` bounds how long each spins, not how
many, and it does not help (ring put/get, all cores, `--tc0`; CPU and wall ns
per hop):

    spin limit   70 (default)   30         10         3          0
    CPU / wall   448 / 80       417 / 90   409 / 95   536 / 185  — / 167-184

**A `runnext` slot — measured, not kept.** Built as an experiment: past
`MaxInlineDepth` a continuation went into a one-item thread-local slot instead of
the pool, run by the outermost `Dispatch` frame once the stack had unwound; a
second one still went to the pool. The chain then stays on its thread and wakes
nobody. With the depth as a variable (ring put/get, all cores, `--tc0`):

                       instr  cycles  br-miss  L1d-miss  CPU ns  wall ns
    no slot, depth 50   1894    1465     6.27      49      430       85
    slot, depth 1       1908     776     4.19      31      194      170
    slot, depth 4       1787     662     2.84      21      151      140
    slot, depth 16      1749     687     4.69      22      157      145
    slot, depth 50      1732     687     5.26      33      157      145

The slot takes a third of the CPU, and depth 4 halves the mispredicts: unwinding
fifty nested resumes overflows the return-address predictor. But the single
chain's wall time goes from 85 to 140 ns. The 85 depends on one thread unwinding
its fifty frames while another carries the chain on, which is overlap paid for
with four to five cores. The skewed choose moved within its noise either way.
Which of the two a program wants depends on whether anything else wants those
cores; a scheduler of its own with Go's spinner cap could in principle have
both. A continuation in the slot also waits for its thread to unwind, so a
chain that blocks its thread would strand it, the hazard the spawn batch's
watchdog exists for. Measured again after the changes below, with more
workloads: see "Under load".

**Every thread-static access is a `__tls_get_addr` call.** libcoreclr is loaded
as a shared library, so its thread-local storage is dynamic, and both a managed
`[ThreadStatic]` read and the native `Monitor` (which needs the current
thread) go through the dynamic loader's lookup. About 61% of those calls on the
ring come from the channel's lock sites (`TryDirectSend`, `ParkReceive`,
`TryDirectReceive`, `TakeParked`); the rest from `FiberContext` in
`CalledFiber.Run`, `InlineDepth` in `Dispatch`, and `AmbientRace`.

**A channel's lock is a spin gate — kept.** `Channel<T>` locked a plain object
with `lock`, which is `Monitor`: native code, a `__tls_get_addr` on enter and on
exit for the owning thread, and recursion bookkeeping. `SpinGate` is what Hopac
uses: one compare-exchange to take, one release store to give back, no owner,
written `using (_lock.Hold())` where `lock (_lock)` was. It is not re-entrant,
and need not be: every call made under a channel's lock is a compare-exchange
on an op's or a `SyncState`'s word, or a walk of that channel's own lists.
`MarkSynchronized`, which runs nacks, and every resume are called after the lock
is released. The spins inside (`TrySyncWaiting`, `TryPair`) wait for a
partner's transient claim, and `TryPair`'s argument that nothing holding a claim
waits on a lock does not depend on whether a lock blocks or spins.

    one core, --tc0         instr        cycles
    Ring (put/get)          1698 -> 1441  630 -> 550
    Skewed choose, put      4056 -> 3527  1291 -> 1121

    all cores, --tc0        instr        cycles        CPU ns     wall ns
    Ring (put/get)          1894 -> 1633  1465 -> 1290  430 -> 390  85 -> 75
    Ring (sync)             2028 -> 1760  1546 -> 1370  521 -> 479  110 -> 105

`System.Threading.Lock` was tried first, being re-entrant like `Monitor`, and
cost more than `Monitor` did (1730 instructions on the one-core ring, 4393 on
the choose): it identifies its owner by the managed thread id, which is another
thread-static. The inbox and `SimpleChannel` still lock with `Monitor`; neither
is on these paths.

**One thread-static per pool — kept.** Each pool (`PutOp`, `GetOp`,
`EventAwaiter`, the two `ActionWorkItem`s, `SimpleChannel`'s nodes) kept its
free list and its length as two thread-statics. A reference and an `int` live in
different thread-static blocks, so the JIT could not share the lookup: every
rent and every return made two `__tls_get_addr` calls (counted in
`DOTNET_JitDisasm` output). The length now lives in the pooled objects — each
knows the depth of the list from itself down while it is on it — and a rent or
a return makes one.

    one core, --tc0         instr        cycles
    Ring (put/get)          1441 -> 1416  550 -> 534
    Skewed choose, put      3527 -> 3343  1121 -> 1042

What is left on a ring hop is one lookup in each of `AmbientRace` (twice),
`Dispatch` (`InlineDepth`), `CalledFiber.Run` (the fiber context) and the
pool's rent and return. They are read by different methods, so gathering them
into one per-thread object would save nothing unless the object were passed
along rather than looked up.

**`AmbientRace`'s read of the environment — measured, not worth acting on.**
`perf record` put 7.7% of the ring's instructions in it, which overstates it:
the samples include the helper it calls for the thread-static. Reading the
token once into a plain static instead (wrong in general, right for this one
benchmark) moved the one-core ring from 1698 to 1651 instructions and 630 to 618
cycles per hop: about 2%. Avoiding the read for real would mean carrying the
environment through every call that syncs, for that.

**A smaller awaiter — kept.** `SyncAwaiter<T>`, the struct a bjoroutine's state
machine stores across an `await`, held five references (the event awaiter, the
fiber watch, the direct parkable, the operation, the cancel reason) and a value,
a generation and two flags. Which of the first five were in use depended on the
path, so they now share one `object` field and a `byte` that says which kind it
is, read back with `Unsafe.As`: three references and a value. Fewer references
means less for `InlinedMemmoveGCRefsHelper` to copy into the box, with a write
barrier each.

    one core, --tc0         instr        cycles
    Ring (put/get)          1416 -> 1366  534 -> 515
    Ring (sync)             1557 -> 1509  592 -> 565

The skewed choose did not move outside its noise, which on that row is wide:
even pinned to one core its runs fall in two groups about 500 instructions
apart.

The rest of the write barriers are not the box. Barriers are about 9.7% of the
ring's instructions and copying the awaiter about 2.7 points of that; the others
are the protocol's own stores of references: installing and restoring the
`FiberContext` in `CalledFiber.Run`, `FiberWatch.AttachDirect` and
`AttachResume`, the park lists' links, and recycling an op. A barrier stub has
no frame, so `perf`'s frame-pointer unwinding charges it to the caller's caller;
read call graphs through barriers with that in mind. Server and workstation GC
made no difference to any of this.

### Under load

Every row above has one chain runnable at a time on an idle machine, the one
shape in which pool workers spinning for work cost nobody anything. Three more
look at scheduling:

    bench/perf/drive.py --rings 1,12,1000     # K rings of 10 at once, ~1M hops
    bench/perf/drive.py --mixed 0,12          # one ring beside C crunching fibers
    bench/perf/drive.py --quota 200 --procs 2 --rings 1,12,1000
    bench/perf/drive.py --cpus 0,2,4,6 --procs 4 --rings 1,12,1000

`--quota` runs in a systemd scope with a `CPUQuota`, as a container would, and
`--procs` tells the runtime so through `DOTNET_PROCESSOR_COUNT`; `--cpus` pins.
On the 5900X CPUs 2k and 2k+1 share a core, so `0,2,4,6` is four whole cores.

**The `runnext` slot again — measured, not kept.** Rebuilt behind an
environment switch at depth 4, after the spin gate, the pool and the awaiter
changes above. Nanoseconds per hop, all cores unless stated, default tiering
without the call-counting delay (the next paragraph), median of five:

                                  wall, off -> on   CPU, off -> on
    1000-node ring (one chain)        64 -> 77         319 -> 94
    1 / 2 rings of 10            65 -> 65 / 40 -> 37   73 -> 74 / 81 -> 76
    6 rings of 10                     18 -> 19          94 -> 98
    12 rings of 10                    16 -> 15         148 -> 131
    24 rings of 10                    14 -> 14         201 -> 219
    1000 rings of 10                  27 -> 22         375 -> 316
    ring beside 12 crunchers         100 -> 72          -
    quota 200%, 1, 2, 12, 1000 rings  equal            equal
    4 cores, 1, 2, 12, 1000 rings     equal or within 3 ns

It is the old trade again. The single chain takes 30% of the CPU it did and
is 20% slower, the overlap of one thread unwinding while another carries the
chain on being gone. Under load the slot is level or ahead: with work for
every worker, nobody is idle to spin, and keeping a woken fiber where it was
woken costs nothing. Beside crunching fibers the ring moved little; on four
cores the crunchers lost 5.6% to it without the slot and 2.8% with it. Not
better everywhere, so not kept. Measured first with the call-counting delay on,
the slot had looked 40% worse at 12 and 24 rings; that was the artefact below.

What a scheduler of its own could add is the case the slot gives up: Go lets
one spinning worker pick up a woken goroutine while the waker carries on, so a
single chain keeps its overlap at the cost of one spinner instead of five. That
is the one row it would win, on an idle machine, and nothing measured under
load or under a quota points to the pool.

**Tier-up under load — a measuring artefact.** At first 12 rings got only three
times the throughput of one and 24 got less than 12, at twice the instructions
per hop. That was tiered compilation, not scheduling. With every hardware
thread busy, methods were still running instrumented tier-0 code
(`JIT_CountProfile32` was a fifth of the cycles) well after the warm-up, though
with four workers they had tiered up; the likeliest reason is that the
background compilation got too little CPU, which was not confirmed. With
`DOTNET_TC_CallCountingDelayMs=0`, which promotes a method after its thirtieth
call instead of after a quiet 100 ms, the rows agree with
`--tc0`. Nanoseconds per hop, all cores:

                          instr   wall   CPU
    24 rings, default      2612     30   555
    24 rings, delay 0      1069     11   177
    same, 4 workers        1092     21    81

So independent chains do scale: 24 rings move a hop every 11 ns against one
ring's 63. What CPU they cost beyond the 4-worker run is mostly SMT, which
counts two threads' time on one core. With `--tc0`, one worker per whole core
ran 24 rings at 17 ns on 159 CPU-ns, and adding the sibling threads made it 15
ns on 239. Neither profile showed workers spinning (`ThreadNative_SpinWait` 18
samples of 45,000) or a migration or context switch. `drive.py` sets the delay
to 0 for every row.

**The ten-node ring does not spin.** One ring of ten costs 79 CPU-ns per hop
for 66 wall, close to one core; the thousand-node ring costs 345 for 73. Why the
shape changes how many workers wake has not been looked into.

### A service, against Go and Hopac

`bench/service` is one program written three times: a sharded key-value
service in Go, Hopac and Bjolang (the specification is in `go/main.go`). Sixteen
shard servers loop on a choice between requests and a quit channel; a thousand
client connections make a thousand requests each, with a little decoding work
before each; one request in ten is a multi-get that spawns four tasks and
collects their answers on a channel; every request makes its own reply channel
and waits for it under a one-second deadline. All three agree on a checksum.
`bench/service/drive.py [--cores N]` runs them; `--cores` pins to N whole cores
and tells each runtime so.

Wall milliseconds per rep of a million client requests (1.3 million shard
requests), median of fifteen, server GC for both .NET programs:

    cores                1     4     8    12   24 threads
    Go                1560   495   488   454   447
    Hopac              770   327   277   270   431
    Bjolang, before   1365   578   661   731   849
    Bjolang           1340   548   464   407   412

All cores, after:

                wall ms  req/s   CPU ms  cores  p50 us  p99 us  p99.9 us
    Go              445  2.25M     6183   13.9      17    5437      5893
    Hopac           433  2.31M     8428   19.5     104    3112      5697
    Bjolang         412  2.43M     8173   19.8      16    4219      8081

**Before, more cores made it slower, and the cause was the scope's token.**
Every fiber that parks in a scope registers on the scope's cancellation token,
once per fiber (`FiberWatch`), and again after the prune has dropped it while it
was between parks. Every fiber here lives under `main`'s scope, the multi-gets
start 400,000 short-lived fibers a rep, and the token kept its waiters in one
`List` behind one `lock`. On 24 threads 7% of the cycles were in `Monitor`, all
of it `Promise.RegisterAny` from `SyncOp.Watched` and `SyncOp.Cell`, and the
threads that lost the lock slept on it: nine cores busy, p50 390 us. With the
token race switched off (wrong, but it measures the watch) the same run took
374 ms on 19.7 cores, p50 14 us.

A promise with 32 waiters now moves them to `StripedWaiters`: one stripe per
core, each with its own spin lock, list and prune, chosen by
`Thread.GetCurrentProcessorId`. Completion closes each stripe under its lock and
wakes what it held; a registration that finds its stripe closed signals itself.
A fiber's join never gets near 32 waiters; a busy scope's token does at once.
The single-chain and multi-ring rows did not move (ring put/get 1350
instructions on one core, 65 ns on all), and 12, 24 and 1000 rings improved by
2-3 ns, because they register on the token too.

Ruled out on the way: the inline depth (0, 1, 4, 16 and 50 all within noise),
tiering's call-counting delay (no effect on runs this long), and workstation
GC, which is much worse for both .NET programs (Bjolang 1858 ms, Hopac 674).
Hopac's own profile spends 45% of its cycles in `Worker::Run`, its workers
looking for work; what it has over the old Bjolang is not less spinning but
more cores doing useful work.

**On four cores the scheduler is not the difference.** Profiled side by side
(four whole cores, each runtime told so), Bjolang spent twice Hopac's cycles,
and the gap divided like this:

    the program's own code (MoveNext, awaiter code inlined)   21%
    fiber plumbing (awaiters, builders, promises)              17%
    the cancellation watch (arm, end, registration)            11%
    tiering instrumentation                                    11%
    channels, events, choose                                    9%
    GC                                                          8%
    write barriers and __tls_get_addr                          12%
    scheduler (.NET pool against Hopac's Worker)                1%

So a scheduler of Hopac's would buy about 1%, and putting Hopac.Core under
Bjolang's builder would keep most of the rest, which is the code a `sync`
compiles to and the token race it carries. Tiering is not a knob: with it off
Bjolang loses its profile-guided code and runs slower (602 against 511 ms), and
the call-counting delay changes nothing at this length. `crunch` compiles to
the same C# remainder in both.

**A registration per call, found by its allocations.** `bench/service` now
prints bytes allocated per request (Go 490, Hopac 1860, Bjolang 1810 on four
cores). By type, from `dotnet-trace --profile gc-verbose`, half of Bjolang's
were the `choose` being rebuilt each time (wraps, sinks, delegates, the event
array, a `SyncState`, a timer), which Hopac's alternatives cost as well; but
`FiberWatch` and `DynEnv` were 11%, one of each per request. The registration
lived in a new environment that `Cell` installed, and that environment belonged
to the frame that built it: a bjoroutine call restores its caller's environment
when it returns, so a `sync` inside a called function (the client's `request`)
built, registered and dropped a registration on every call. It is now written
into the environment object, which every frame of the fiber shares, and
`ForChild` copies an environment that has a token so that no two fibers hold
the same one. Four cores: 548 -> 498 ms (Go 498, Hopac 336), 1810 -> 1600
bytes a request. All cores: 412 -> 401 ms, CPU 8.2 -> 7.2 s.

What is left: on four cores Bjolang is 1.5 times Hopac, in the per-sync path
above, and its p99.9 there is 40 ms against Hopac's 6 and Go's 3.5. On one core
Hopac lets a few clients starve (p99.9 660 ms) where Bjolang's is 190.

**Timeouts on a wheel.** A `choose` with a deadline arms its timeout every time
it parks, and each armed timeout was a `System.Threading.Timer`: a
`TimerQueueTimer`, a `Timer` and a `TimerHolder`, and the runtime timer queue's
lock to arm it and again to cancel it. `TimerWheel` keeps them instead: the
`TimeoutNode` is its own entry, linked into a wheel of 1024 one-millisecond
slots, one wheel per core under its own spin lock; a cancel unlinks it at once,
and one shared 1 ms tick, running only while something is linked, fires what is
due. On `bench/service`: all cores 408 -> 366 ms (2.73M requests/s; Go 2.27M,
Hopac 2.42M), four cores 511 -> 483 ms, and 160 bytes a request less.

**A suspended call's box, reused.** A called bjoroutine that suspends is one
object, `CalledFiber`, its state machine's box and its caller's promise at once,
with a resume delegate made in its constructor. The client's `request` suspends
on nearly every call, so that was two objects a request. The box now goes back
to a per-thread free list (16 deep, per state machine type) when the caller's
`FiberAwaiter.GetResult` has read the outcome, and the next suspension of the
same function on that thread takes it out, delegate and all. Only that read
releases: the compiler awaits a fiber once and reads it once, and a promise
reached another way (`SpawnSettle`'s forward, `RunToCompletion`, `AsPromise`)
is never released and so never reused. When the read happens the call has
completed, nothing holds its resume delegate any more, and the callee's thread,
if it is still returning from `TrySetResult`, touches only a virtual no-op of
the box. `bench/service`: all cores 366 -> 350 ms, 2075 -> 1692 bytes a
request.

### Run queues of our own

**The four-core tail was queue order.** With the pool, `bench/service` on four
cores had a p99.9 near 40 ms against Go's 3.5 and Hopac's 6. Not GC: the
longest pause in a traced run was 6.2 ms. Logged one by one, the slow requests
were spread over most clients but bunched in time: hundreds of clients slowed
in the same 25 ms, several times a rep. The pool's per-thread queues are LIFO
for their owner, and a thread takes from elsewhere only when its own is empty,
which under saturation it never is; a shard's continuation pushed under newer
client work waits, and the shard's sixty clients wait with it. Every item on
the pool's global FIFO queue instead gave a p99.9 of 4.4 ms and cost a third
of the throughput. The pool has no setting for the order, and putting some of
the work on its global queue does not help, since that is read only when the
local queue is empty.

**`Workers`** puts Go's shape on pool threads: one worker per processor, each
with a 256-item FIFO queue its owner takes from the head of and others steal
half of, a `runnext` slot for the item woken last (16 runs in a row at most),
and a global queue for work from outside a worker, read first every 61st item.
Pumps — pool work items — bind a worker to their thread and run items until
there are none; the pool still adds threads when an item blocks one. Spinners
are capped at half the busy workers, and the lost wake-up is closed as Go
closes it. A monitor every 10 ms wakes a pump to steal from a worker that has
run nothing while work waits in its queue, adding a worker, up to four per
processor, when all are held. The pool path stays behind `BJO_SCHEDULER=pool`
for a while.

FIFO queues halved the tail (to 17 ms) and no more: inline resume is newest
work first as well, and one work item could go on resuming fibers inline for as
long as they kept waking each other. No inline resume at all (depth 1) gave
Go's profile — p99.9 5 ms, p50 250 us — at 10-15% of the throughput, the
non-inline resumes making calls suspend and timeouts arm that inline ones
spared; the timer wheel and the box reuse above came out of finding that. What
was kept is a budget: 32 inline resumes per work item in all, and past them the
resume goes to `runnext`. Measured with budgets of 8 (tail 5 ms, slower), 32,
and 128 (tail 12 ms).

Three rules came from the ring rows. A resume that only fills an empty
`runnext` wakes nobody, so that a single chain is not carried from core to
core: before that rule one ring of ten cost four times the CPU. A spawn always
offers itself to an idle core, as Go's `newproc` does: without that a spawner
that kept running sat on its child, and `231_byte_ports.bjo`'s race between a
writer fiber and a ready inbox never raced. A busy worker's `runnext` is
stolen only after it has run nothing for about 3 us, waited once per search,
not per victim: per victim, a failed search over 24 workers spun for tens of
microseconds. Spinning more, or without the cap, measured no better.

Against the pool, after the timer wheel and box reuse, median of fifteen reps,
`DOTNET_TC_CallCountingDelayMs=0` for both:

                                     pool            Workers
    service, 4 cores: wall, p99.9    484 ms, 49 ms   505 ms, 5.8 ms
    service, all cores: wall, p99.9  354 ms, 6.8 ms  350 ms, 7.8 ms
    service, all cores: p50          13 us           89 us
    1000-node ring: wall, CPU        62 ns, 299 ns   77 ns, 94 ns
    1, 12, 1000 rings of 10          67, 15, 28 ns   65, 16, 23 ns
    one core, ring put/get           1325 instr      1334 instr

What it gives up: a single chain on an idle machine is about a quarter slower,
having lost the overlap of one core unwinding while another carries the chain
on, and under load on all cores a request waits its turn, so the median rises.
Two rows of `bench/bjolang/cmlbench.bjo` were not measured then, and cost
about twice as much (min of 5, all cores, measured later at d3ec31a):

                                  Workers   pool
    spawn burst, ns a spawn         226      105
    skewed choose(8), ns a choose   230      113
    ring, ns a message               71       59-63

Why has not been looked into. A burst of spawns from one fiber goes to its
worker's queue, and other workers have to steal it; the pool's own spawn
batching (`SpawnBatch`) is not used with Workers.
What it gets: the tail under saturation, a third of the CPU for a lone chain,
and the many-ring rows. Go, against these, is 498 ms and 440 ms on the service,
Hopac 336 and 413.

**A web server at fixed rates.** `bench/bjoweb-rate.py` loads bjoweb's
`examples/hello.exe` with oha at a fixed rate over 256 connections, timing each
request from when it should have been sent. Both schedulers top out near 185k
requests a second, at the same CPU per rate, and the median is about the same.
The tail varies a lot from run to run; over four or five runs, p99.9 at 25k a
second was 14-23 ms for Workers and 16-34 for the pool, which says nothing, and
at 100k 19-20 ms against 38-56, which does. At 2k a second both have a p99.9
near 16 ms, so some of every tail here is not the scheduler.

But with bjoweb's defaults — 16 worker fibers, 128 requests queued, the newest
refused with a 503 past that — Workers refused 0.1% to 2% of the requests from
25k a second up, and the pool none. These are not stalls. Logged one by one at
100k, the refusals came in half of all 10 ms windows, windows whose slowest
answer took about a millisecond. The queue is simply where a burst waits now.
A request comes in on a pool thread running Kestrel, which posts it to the
server's inbox, and the post from outside a fiber enqueues the worker fiber
it wakes. With the pool that went on the posting thread's own LIFO queue, run
before that thread took any more of Kestrel's work, so arrivals were paced by
the answering and a burst waited in Kestrel, unbounded. With Workers it goes
to the global queue for the workers' threads, and Kestrel's threads go on
admitting. That is what a bounded queue is for, but 128 is short of what 256
connections can have in flight. `#:workers 64` left 0.1-0.3% refused, and
`#:queue 1024` none, with Workers' p99.9 at 19 ms at 100k a second against the
pool's 38 — but for one run at 25k a second in which 0.2% of the answers were
not 200, which three more runs did not repeat. Queueing the pump that a post from outside wakes on the posting
thread's own queue, so that it ran as soon as Kestrel's item returned, changed
nothing; neither did waking a pump for every resume, as Go's `ready` does.

### A feeder and its workers

`bench/clbg/parallel` has Benchmarks Game programs with fibers, against Go
with goroutines. mandelbrot gives its rows, and fannkuch-redux its blocks of
permutations, to 24 worker fibers through one channel. Both used the same CPU
time as Go but about 10 cores of 24: mandelbrot took 1.6 s against Go's 0.70,
and fannkuch-redux 1.6 to 8.9 s against 1.41. The same mandelbrot with no
channel, each fiber taking every 24th row, took 0.76 s.

The cause was the inline resume. A worker's `chan-get` found the feeder
parked and ran it inline. The feeder's next `chan-put` found another worker
parked and ran that inline, and the worker computed a row before the feeder
could go on. Up to the budget of 32 resumes, the rows ran one after another on
one thread while the other workers waited. With `BJO_SCHEDULER=pool` both
programs ran on one core.

A send that takes a parked receiver while others are still parked on the
channel now queues it (`Scheduler.DispatchTaken`). The next such resume pushes
it from `runnext` to the queue, and that wakes a worker to steal it, by the
rules above. A ring, a `choose` over channels with one sender each and the
service's request channels have one fiber parked on a side at most, so they
resume inline as before. mandelbrot takes 0.86 s and fannkuch-redux 1.70 s.

Old and new, run in turn, minimum or median as each tool reports:

    cmlbench, 8 rounds, median ns/op    ring 93 -> 91, put/get 86.5 -> 87,
                                        skewed 258.5 -> 259, spawn 228 -> 224
    ring put/get, one core, --tc0       1337 -> 1344 instructions, 1-2 ns
    1, 12, 1000 rings, all cores        69, 15, 25 -> 67, 15, 23 ns
    the same on 4 cores                 72, 23, 33 -> 69, 23, 32 ns
    a ring beside 12 crunchers          crunchers +0.0..1.1% -> +1.6..2.7%
    service, all cores                  366-367 -> 363-372 ms
    service, 4 cores, 4 runs each       median 508 -> 512 ms, p99.9 equal

Three other rules were measured and not kept:

- Queue every resume on a worker's thread while a worker is idle. mandelbrot
  0.84 s, but a hop of a ring of ten cost 200 more instructions, and 12 rings
  went from 132-144 to 192-216 CPU-ns a hop. Queued at a send only, the
  skewed choose got faster, 256 -> 171 ns, and the rings did not.
- Mark the depth while a receive runs the sender it took, so that whatever
  that sender wakes is queued. The service on 4 cores went from 508 to 533 ms
  and its p50 from 1 to 260 us: a client's `chan-get` on its reply channel can
  take the shard that is parked to send the reply, and the shard then queued
  everything, the next clients too.
- Queue only when a receive on the same channel is below on the stack. Not
  enough alone: mandelbrot 1.48 s.

A queued resume counts against the inline budget, as an inline one does. That
was so in the version measured, and it ends a long inline chain sooner.

### GC pauses are the tail

On all cores, `bench/service`'s p99 and p99.9 were set by the GC more than by
anything in this layer. With counters added, a rep of about 340 ms had 33
collections and 80-100 ms of pauses: for a quarter of the wall time, no
thread ran. Hopac had 57 collections and 65 ms.

The cause is .NET's dynamic adaptation of the server GC (DATAS), on by default
since .NET 9. It chooses the number of heaps while the program runs, from the
share of time spent in GC. A GC trace (`dotnet-trace`, the GC keyword, read
with TraceEvent's `TraceLoadedDotNetRuntime`) showed 22 heaps for Bjolang and
12 for Hopac. The two promote about the same per collection (1.7 and 1.8 MB),
but a gen0 pause took 3.1 ms on average against 1.6, because each phase of a
collection waits for every GC thread (`t_join::join` was 11% of all samples
against 4%). Suspending the threads took 20-50 us and was not the cost.

Same session, all cores, median of 15 reps:

                                     wall   p99      p99.9    memory
    Bjolang, DATAS on (.NET default)  342   3093 us  6474 us  120 MB
    Bjolang, GCHeapCount 12           280   2354     5283     210
    Bjolang, DATAS off                266   1875     5369     370
    Hopac, GCHeapCount 12             402   3195     6570     206

So a program with server GC now gets `System.GC.DynamicAdaptationMode` 0 in
its runtimeconfig unless it says `(gc-dynamic-adaptation #t)`
(`Build.withRuntimeDefaults`). Memory is the cost. A fixed heap count is not
the default because the right count depends on the machine that runs the
program, not the one that builds it.

`bench/service/drive.py` now prints the collections and the pause time of
each program, and runs both .NET programs with DATAS off, so that they are
compared on the same GC (`--datas` turns it on for both). The Bjolang client
kept its samples in a list, which put 125,000 cells a rep through promotion
that the Go and Hopac clients, with arrays made at full size, did not have;
it makes its array at full size now. That alone took the pause time from 97
to 80 ms and p99.9 from 7.6 to 6.2 ms, with DATAS on.

`drive.py` after both changes, median of 3 x 5 reps:

                 wall ms  cpu ms  p50 us  p99 us  p99.9 us  gcs  pause ms
    all cores
      Go             437    6062      18    5373      5732  140        16
      Hopac          385    8565     118    2659      5733    7        18
      Bjolang        271    5738     118    1947      6903    7        19
    4 cores
      Go             491    1853     184    2532      3429  143         7
      Hopac          282    1098       1    2258      4137   44        32
      Bjolang        437    1722       1    3155      4496   39        36

With almost no collections at all (a 64 MB gen0 per heap, 1.6 GB of memory)
the all-core p99 was 1.28 ms for Bjolang and 2.20 for Hopac. What is left of
the all-core tail is the GC.

**Four cores are a matter of CPU.** The clients are a closed loop, so the mean
latency is clients x wall / requests: 437 us for Bjolang, 282 for Hopac.
Bjolang's latencies are less spread than Hopac's (p99 7.2 times the mean
against 8.0), so its tail is longer only because a request costs 1.6 times the
CPU. With the GC taken out of both, the difference is about 600 ns a request:
the runtime's own methods, 770 ns against Hopac's 440; runtime code inlined
into the state machines, about 200 ns more; write barriers, 113 ns against 43.
The program's own work costs the same in both. No one item is large.

Measured on four cores and not kept:

- No pooling at all (ops, awaiters, called boxes, work items). Allocation
  went from 1611 to 2399 B a request and gen1 collections doubled; slower.
  Pooled objects are old, so storing young references into them marks cards,
  but allocating is the greater cost.
- A send that takes a receiver queues it and continues, as Hopac does, where
  it ran the receiver inline. On four cores p99 went from 3.43 to 2.78 ms but
  the wall time from 472 to 598 ms; on all cores both were worse. The client
  then always parks in its `choose` and arms its timeout, which the inline
  order mostly spares it.
- The token race switched off: about 7% faster. That is what structured
  cancellation costs, and it stays.

**A timeout node is its own nack — kept.** An armed timeout was a
`TimeoutNode`, a cancel delegate and a `NackNode`. `NackNode.Fire` is virtual
now, and `TimeoutNode` derives from it, so it is one object. And
`MarkSynchronized` took the state's `Monitor` on every commit to walk the
nack list, which is empty for most syncs. It now stores S with an exchange
and reads the list without the lock; `RegisterNack` fences after it stores,
and its callers look at the state after that, so one of the two always sees
the other (the argument is in `MarkSynchronized`). Old and new runtimes in
turn, median of 30 reps on four cores and 20 on all:

    4 cores    wall 449 -> 444 ms   cpu 1756 -> 1726 ms   1611 -> 1578 B/req
    all cores  wall 266 -> 266 ms   cpu 5746 -> 5623 ms   1782 -> 1697 B/req

**A small `choose` without its array, and a written send as `chan-put` —
kept.** Two things from an allocation trace of the service. The compiler
gathers a `choose`'s branches into an array, as it does for every rest
parameter, so `PairChooseEvent` and `TripleChooseEvent` were never reached
from Bjolang: each `choose` was an array and a `ChooseEvent`. And
`(sync (chan-send ...))` written as such built the send event, which hack 3b
above found worth no time; with the tail set by the GC, bytes are worth more
than they were.

`Codegen.generateApply` now emits a `choose` of two or three branches as the
runtime's `choose(a, b)` and `choose(a, b, c)`, and `(sync (chan-send ch v))`
as `chan-put`. Both are recognised only when the names are the builtins
(`builtinNamed`), and only in that spelling: a send event in a variable is
still an event, as `chan-put` itself had decided. The pair and the triple now
also look at the state before their first branch, as `ChooseEvent` does.

`bench/service`, old and new programs in turn on the same runtime, median of
30 reps on four cores and 20 on all:

    4 cores    wall 442 -> 416 ms   cpu 1721 -> 1628 ms   p99 3237 -> 3113 us
               1579 -> 1398 B/req
    all cores  wall 261 -> 255 ms   cpu 5696 -> 5438 ms   1695 -> 1517 B/req

`bench/bjolang/cmlbench.bjo`, old and new in turn, three runs each (minimum
ns/op and B/op). `Ring` now is `Ring (put/get)`, and `Skewed choose(8)` is
its `put` row:

    Ring                  90-92, 32 B  -> 77-79, 0 B
    Ring (nested scope)   79-88, 32 B  -> 70-77, 0 B
    Skewed choose(8)      248-252, 88 B -> 221-233, 56 B
    Spawn burst             unchanged

### At a fixed rate

A closed loop at saturation mostly measures throughput: on four cores
Bjolang's latencies were less spread than Hopac's, and its p99 was higher
only because each request cost more CPU. `bench/service` therefore has an
open loop as well (`drive.py --rates`, schedule in `go/main.go`): 10000
clients send on a fixed schedule.

**Correction.** The latencies of this section, and of every open-loop table
after it up to "Latency from the due time", are not what they were said to
be. A client that was late counted from the due time, but a client that
slept until its request was due counted from when it woke, and at these
rates nearly every client sleeps. So they leave out everything that held a
client back before it ran: a late timer, a wait in a run queue, and a GC
pause while it slept. They are service times. The comparisons of one
Bjolang build against another still hold as comparisons of service time.
The comparisons with Go and Hopac do not hold as latency: the delays that
were left out are not the same in the three runtimes. "Latency from the due
time" below has the three measured from the due time.

`drive.py --rates ...`, median of 2 x 3 reps of 3 s. Only the rows below
saturation are shown; near it, all three built a backlog and the numbers
changed a lot from run to run:

                     cores   p50 us   p99 us   p99.9 us   GC pause / rep
    4 cores, 0.25M/s
      Go               1.3        9     2657      11699        10 ms
      Hopac            1.7        2    18281      26123       200
      Bjolang          1.4        2       55        324        44
    4 cores, 0.5M/s
      Go               2.7      758    66933      75456        49
      Hopac            2.4     3963    88007     100267       359
      Bjolang          2.2        2     9974      17038       131
    all cores, 0.5M/s
      Go               3.6        4     6092       8743        22
      Hopac           19.9     7538    30024      37906       200
      Bjolang          5.1        5       60        134        84
    all cores, 1M/s
      Go               8.4      377    34640      41155       102
      Hopac           18.8     8925    38664      48897       406
      Bjolang          9.2       13    19231      24242       260

Below saturation, Bjolang had the lowest service time of the three at every
rate. (This said "tail"; see the correction above.) Hopac's workers kept
every core busy and its median was in milliseconds; its GC pauses were not
the cause (with three collections a rep it was the same). Why it does badly
here was not looked into.

**Bjolang's open-loop tail is GC pauses.** All cores at 1M/s: 23 gen0
collections a rep of about 7 ms each. With 10000 clients between requests,
more is alive at each collection: 7.4 MB promoted against 1.7 MB in the
closed loop. A pause of 7 ms at 1M/s leaves 7000 requests waiting, and p99
moved from 1.2 to 22 ms from rep to rep, depending on how the pauses fell.
With a larger gen0 (`DOTNET_GCgen0size=0x4000000`, 64 MB a heap, 1.5 GB in
all) there were 3 collections a rep, and p99 was 0.17-0.21 ms and p99.9
0.46-1.1 ms. A pause is about as long either way, because what it costs
follows what is alive, not the size of gen0. So the lever is how often the GC
runs, which allocation per request and the gen0 budget set. The budget is a
trade of memory against tail, and only the program can choose it; a runtime
setting for it would be the place.

**What would cut more allocation**, from the same trace (not done):

- Delegates are about 20% of the bytes: an `Action<T>` for every published
  wrap sink. A sink class with a virtual `Commit`, in place of `Action<T>` in
  `IEvent<T>.Publish`, would remove them; every event implements `Publish`.
  Pooling showed what bytes are worth here: 790 B more per request cost 2-3%,
  so this would be worth 1-2%.
- Events built inside a loop from values that do not change in it, such as
  the shard's `choose`, about 8%.
- A spawn is emitted as `ScopeSpawn(async () => await f(...))`, one state
  machine more than `ScopeSpawn(() => f(...))`. Done for a module
  bjoroutine; see "What a parked fiber keeps" below.

### Memory, and Go with as much of it

The tables above compare Go with its default collector against two .NET
programs with the server GC and DATAS off, and they do not show memory. The
two make different trades. Go keeps the heap small and collects often: about
140 collections a rep, at a peak of about 20 MB. The server GC keeps hundreds
of megabytes and collects rarely: 6 or 7 collections a rep, at a peak of
about 380 MB on all cores and 100 MB on four. So Go was compared on a
nineteenth of the memory on all cores and a fifth on four, and its wall
time, CPU time and tail all paid for that.

`drive.py` now shows each program's peak resident memory (`peak MB`, from
`os.wait4`), and runs Go a second time with `GOGC=off` and `GOMEMLIMIT` at
the larger .NET peak (`Go, N MiB`; `--no-matched` leaves it out). A
comparison with Go should give both Go rows, and say which it means.

`drive.py`, closed loop, median of 3 x 5 reps:

                   wall ms  cpu ms  p50 us  p99 us  p99.9 us  gcs  peak MB
    all cores
      Go               444    6082      16    5437      5858  141       21
      Hopac            392    8797     109    2671      6610    7      382
      Bjolang          259    5445     107    1889      7114    6      368
      Go, 382 MiB      259    4001      14    2954      3312    1      358
    4 cores
      Go               496    1871     186    2510      3522  142       22
      Hopac            290    1137       1    2442      4290   46      102
      Bjolang          424    1650       1    3183      4994   34      106
      Go, 106 MiB      417    1647     186    2029      3322    5      103

With as much memory, Go was as fast as Bjolang on all cores, on three
quarters of its CPU time and with half its p99.9. On four cores the .NET
programs peak at about 100 MB, and Go with that much was as fast as Bjolang
and had a lower tail. Hopac stays the fastest on four cores.

**The open loop again, with more reps.** `drive.py --rates`, median of 3 x 5
reps of 3 s, so two and a half times the reps of the table above. Service
times, not latencies: see the correction in "At a fixed rate".

                          cores  p50 us  p99 us  p99.9 us  pause ms  peak MB
    4 cores, 0.25M/s
      Go                    1.0       2    1721      6265         5      160
      Hopac                 1.5       2      11        29        49      214
      Bjolang               1.2       1      35        67        36      347
      Go, 347 MiB           0.8       2     169       268         0      369
    4 cores, 0.5M/s
      Go                    1.9     116   24729     30862        27      178
      Hopac                 2.3       2       8        17        83      212
      Bjolang               1.8       1      30       169        77      381
      Go, 380 MiB           1.8     101   22824     33264         6      487
    all cores, 0.5M/s
      Go                    3.5       3    6774      9521        21      174
      Hopac                21.9    5578   20316     24968       100      399
      Bjolang               5.0       5      63       131        57      411
      Go, 410 MiB           2.8       3     138       447         1      434
    all cores, 1M/s
      Go                    8.2      43   32199     36485        80      189
      Hopac                22.5    7059   21003     27853       207      720
      Bjolang               9.7      14    2315      5201       133      493
      Go, 720 MiB           6.3      19  101409    107333         6      821

This replaces the conclusion of the table above, that Bjolang had the lowest
service time of the three at every rate below saturation. That is so on all cores,
against Go with either memory and against Hopac. On four cores it is not:
Hopac had the lowest tail there, 8-11 us at p99, where the earlier table gave
it 18-88 ms. Those rows of the earlier table do not reproduce, and the
reason is not known.

Two things are still not explained, and a comparison should not lean on
them:

- Hopac on all cores keeps all 24 threads busy at 0.5M/s, where the other
  two use 3 to 5, and its median is in milliseconds. On four cores, with as
  many threads as cores, it is the best of the three. Its spinning workers
  against the timer thread and the GC threads is a guess, not a finding.
- Go's open-loop tail does not follow its memory. On four cores at 0.5M/s it
  is 23-25 ms with either memory, on 1.9 cores, so not for want of CPU. On
  all cores at 1M/s Go with 720 MiB had a p99 of 101 ms against 32 ms with
  its default. Its timers (a `time.Sleep` per paced request and a
  `time.NewTimer` per request) are where to look first.

### What a parked fiber keeps

The open loop parks 10000 clients between requests, and a parked client
keeps its objects until it wakes. `dotnet-gcdump` at 4 cores and 0.25M/s,
halfway through a 30 s rep, gave these live objects for each client. The
client's own data (its samples and its channel) is left out.

    spawned fiber, as it was emitted
      CalledFiber<client>              232   the callee's box and promise
      FiberStateMachineBox<lambda>      80   the async lambda's box
      closure of the lambda             48
      Action of the lambda's box        64
      FiberCore<ClientOut>              88   the spawn's promise
    spawned fiber, now
      FiberStateMachineBox<client>     184   box only: FiberCore is the promise
      FiberCore<ClientOut>              88
    the same either way
      CalledFiber<pace>, 2 Actions     288   the call that waits; the resume
                                             of its box and of the client's
      EventAwaiter, Action<Unit>       128   `(sync (timeout ms))`
      SyncState, TimeoutNode           160
      FiberWatch, DynEnv               152   the cancellation watch

A spawn of a module bjoroutine is now `ScopeSpawn(() => f(...))`. The
callee's builder takes the spawn's promise (`FiberCore.CurrentSpawning`)
when it starts, so the fiber has one box and one promise. This cut 240 B and
3 objects from each parked client, from 1240 B to 1000 B: the live heap went from
192,613 to 168,619 objects. The codegen does this only when the callee is a
module function compiled as an `async` method. A builtin or a function value
can start another bjoroutine before the one it returns, and that one would
take the promise, so those keep the `async` lambda.

Wall time, alternating the two builds, 3 trials: closed loop on all cores
311 to 291 ms (CPU 6681 to 6047 ms); on 4 cores 418 ms both, after the
first rep.

The tail, at 0.25M/s on 4 cores: 30 rounds, each running both builds in
turn with the order flipped every round, 3 reps of 3 s each, so 90 reps a
build:

                 old     new    new - old, 95% interval   rounds new lower/higher
    p99 us      35.0    35.3    -0.3 .. +0.9                14 / 16
    p99.9 us    67.0    66.8    -9.4 .. +10.6               15 / 15

No difference at p99 larger than 1 us, and none found at p99.9. The tail of
the all-cores open loop at 1M/s is too noisy for 9 reps to show a
difference either way.

Not done, in order of size:

- Each box has its own resume `Action`, 64 B. An awaiter that takes the box
  (`IFiberResume`) in place of an `Action` would remove it, but every
  awaiter, op and the cancellation watch store an `Action` now. Done; see
  "A box needs no delegate" below.
- `(sync (timeout ms))` goes the general way: an awaiter, its delegate and a
  `SyncState`, 248 B with the `TimeoutNode`. A sleep that parks the fiber's
  resume in the timer wheel, and that the watch can withdraw, would need only
  the node. Done; see the next section.

### A lone sleep parks in the timer wheel

A `sync` on one `timeout` or `at` event, with a delay above 0 ms, now parks
a `SleepNode` (`TimeoutEvent.cs`) in place of the general way. The node is
the wheel's entry, the fiber's resume and the park site at once: it
implements `IParkable<Unit>`, so `SyncOp` parks it through the same
`SyncAwaiter` path as a lone channel operation, and the fiber's
`FiberWatch` withdraws it through `CancelParked`. A gate of three states
(unpublished, published, done) decides who resumes the fiber: the tick, the
token, or `Park` itself when the token came first. A delay of 0 ms or less
keeps the general way, which commits at once. The wheel takes a `WheelNode`,
the base class of `TimeoutNode` and `SleepNode`.

The gcdump at 4 cores and 0.25M/s, as in the section above:

    a sleeping client, before         a sleeping client, now
      TimeoutNode           104         SleepNode            96
      EventAwaiter           64
      Action<Unit>           64
      SyncState              56

A parked client keeps 808 B in place of 1000 B, and the live heap went from
164,795 to 130,875 objects. Allocation fell 6.5% a request.

The A/B, as above: 30 rounds at 0.25M/s on 4 cores, 90 reps a build, old
and new runtime under one `service.exe`:

                 old     new    new - old, 95% interval
    cpu ms      3388    3197    -214 .. -159
    p99 us      35.1    35.9    +0.0 .. +1.2
    p99.9 us    66.4    62.7    -10.1 .. +9.0
    gcs            9      16    +7 .. +8
    pause ms    35.5    46.0    +9 .. +12

The closed loops, which do not sleep, did not change (10 rounds each).

**Twice the collections, from less allocation.** Counters for one rep:

                 gen0   gen1   gen2   gen1 after GC   committed
    old            69     62      6         24.9 MB      193 MB
    new           213     10      4          8.6 MB      162 MB

Before, every sleeping fiber kept four objects alive, so much survived each
gen0 collection, and gen1 collections were frequent. Now less survives, and
the server GC sizes gen0 by what survives, so it picks a smaller budget and
runs more, cheaper gen0 collections: 3.2 ms each in place of 4.3. With the
budget fixed (`DOTNET_GCgen0size=0x2000000`, 10 rounds), both make 5
collections a rep, and the new runtime pauses 19 ms in place of 25 (-24%),
uses 7% less CPU, and has the same p99 and p99.9. So the change itself
costs nothing. The 0.8 us at p99 with the default budget is the GC's choice
of memory against collections, and the gen0 budget is the lever for it, as
in "Bjolang's open-loop tail is GC pauses" above.

### A box needs no delegate

Every state machine box (`FiberStateMachineBox`, `CalledFiber`) made an
`Action` for its `MoveNext` when it was made, because an awaiter takes an
`Action`. A parked fiber kept one for each frame: two a client here, 128 B.

The runtime's awaiters now take the box. `IResume` is what a parked fiber
is resumed through: `Resume()` here, or `Execute()` as a queued work item.
A box is one, and every store of a resume (a channel op, `FiberWatch`,
`SleepNode`, `EventAwaiter`, a promise's waiters) holds an `IResume`. The
fiber builders test the awaiter for `IFiberAwaiter`, as the BCL's builders
test for `IStateMachineBoxAwareAwaiter`, and hand it the box. The JIT
removes the box of that test and cast for a struct awaiter, since the
awaiter's type is known in each instantiation; allocation a request did not
rise, which a box a park would have shown as about 40 B. An awaiter from
outside, such as a `Task`'s, still gets a delegate, made at the first such
await (`MoveNextAction`). A delegate that comes back to the runtime from a
builder outside it is wrapped in an `ActionResume`, unless it is a box's
own (`Resumes.Of`).

The gcdump at 4 cores and 0.25M/s: 20,344 `System.Action` before, 7 after.
A parked client keeps 680 B in place of 808 B.

The A/B as above, old and new runtime under one `service.exe`:

                                  old      new    new - old, 95% interval
    4 cores, 0.25M/s, 90 reps
      cpu ms                     3198     3122    -97 .. -41
      p99 us                     35.6     33.0    -3.0 .. -2.1
      p99.9 us                   63.1     49.6    -20.6 .. -7.4
      gcs                          16       18    +1 .. +2
      pause ms                     46       47    -1 .. +2
    4 cores, closed loop, 50 reps
      wall ms                     423    411.5    -19 .. -6
      cpu ms                     1645     1615    -54 .. -18
      pause ms                     32       27    -5 .. -4
    all cores, closed loop, 50 reps
      wall ms                     245      247    -12 .. +11
      alloc MB                   1512     1481    -33 .. -28

With the gen0 budget fixed at 32 MB a heap (30 reps), the open loop used 2%
less CPU and had a p99 1.4 us lower. Allocation fell only a little in the
open loop, since a pooled `CalledFiber` kept its delegate across reuse. What
the cut saves there is the delegate's work: making it once per box, and the
indirect call of every resume.

### A finished fiber's watch goes to the next

A fiber builds its `FiberWatch` at its first `sync` and registers it on the
scope's token. Nothing removes a waiter from a promise: when the fiber
ended, its watch stayed on the token until a prune dropped it, and a stripe
prunes when it has grown to twice what the last prune kept. So a token
carried up to one dead watch for each live one, and every short fiber that
synced paid for a new watch and a registration under a stripe's lock. The
gcdumps of bench/service showed 10,500 to 18,200 watches for 10,000 clients,
depending on where the prune cycle was.

Now the root frame of a spawned fiber, when it ends, tells its environment
(`IFiberContext.FiberEnded`, called by the fiber builder on its own thread
with its own environment installed). `DynEnv` gives up its watch, and
`FiberWatch.Retire` keeps it, still registered, for the next fiber that
syncs under the same token. A watch already serves one fiber across park
after park, and the protocol does not care which fiber that is: each park
has a generation, so a stale take or signal names an old one and fails. It
needs one owner at a time, and the fiber that owned this one has ended. Only
an idle watch is kept.

Kept where. Each thread has four slots. But fibers do not end where the next
ones start: a fiber waiting for a reply is resumed inline by the sender, so
it ends on the sender's thread, while new fibers start on the threads of
those that spawn them. Counted on bench/service, 2.4 million watches were
retired in a rep and 49,000 overflowed a thread's slots, and each of those
was a dead registration. So a watch that a thread has no room for goes to
its scope (`Scope.Retired`, a capped stack under a lock), and a thread with
none takes one from there. Only those overflows touch the lock.

The gcdump, five times in one run of each build:

    old   16,169  12,785  10,541  15,314  13,075  watches
    new   10,320  10,320  10,320  10,320  10,320

The A/B, as above, old and new runtime under one `service.exe`:

                                  old      new    new - old, 95% interval
    4 cores, 0.25M/s, 90 reps
      alloc MB                 1062.8   1037.6    -25.4 .. -25.1
      pause ms                   46.5     43.0    -5 .. -2
      gcs                          18       19    +0.5 .. +1
      p99, p99.9, cpu         no difference
    4 cores, closed loop, 50 reps
      wall ms                   416.5    401.5    -21 .. -7.5
      cpu ms                     1635   1575.5    -84 .. -40
      alloc MB                 1376.8   1340.6    -37.7 .. -34.9
    all cores, closed loop, 50 reps
      alloc MB                 1480.2   1444.1    -38.6 .. -33.9
      wall, cpu, tail         no difference

A cell kept in a slot or in the scope is still registered on the token, so
it can be pruned while it waits; the next arm then registers it again. A
scope that ends with many fibers still running drops what does not fit
under the cap, as before, onto a token that is about to complete.

### Fields that are never used together share a slot

Four objects held fields for two uses that never overlap:

- `FiberWatch` held a channel park (site, op, generation) and a `choose`
  park (awaiter, state, branch id) side by side. A park is one or the
  other, so two object slots and one number hold either; a `SyncState` in
  the second slot is what says it is a `choose`. 88 B to 64 B.
- `FiberCore` kept a runner delegate, the body and the inherited context
  until the fiber starts, and the state machine box after. The body is
  cleared before it runs and the box is made while it runs, so the two
  share one slot, and the runner is a virtual method: the body's delegate
  type says what to call, and only a spawn with state is a subclass.
  88 B to 72 B.
- `CalledFiber` kept a free-list link that is used only while the box is
  on the list, when its `Context` is always null. The link is `Context`
  now. Both boxes also kept a field for the lazy `MoveNextAction`; the
  delegate is now made at each await outside the runtime and not kept, since
  such an await (a `Task`'s) allocates more than it. `CalledFiber<pace>`
  160 B to 144 B, `FiberStateMachineBox<client>` 184 B to 176 B.

A parked client in bench/service keeps 616 B in place of 680 B. The A/B, as
above (90 reps at 0.25M/s on 4 cores, 50 of each closed loop), found 0.5 to
0.8% less allocation and no difference in wall time, CPU, pauses or the
tail.

What a parked client keeps now, from the gcdump:

    FiberStateMachineBox<client>   176   the spawned fiber's frame
    CalledFiber<pace>              144   the call that waits
    SleepNode                       96   the sleep
    FiberCore<ClientOut>            72   the spawned fiber's promise
    FiberWatch                      64   its cancellation watch
    DynEnv                          64   its environment, which holds the watch

### Inlining a bjoroutine is the author's choice

A called bjoroutine that suspends keeps a `CalledFiber` for its state machine
until it returns; the copy of its body in the caller has none, and its locals
become fields of the caller's frame. `TraitInline` can now make that copy
(`inlineOptedIn`), but only for a function written `#:inline`.

It was tried first without asking: every bjoroutine of at most 50 nodes,
called from another bjoroutine, with a fixed budget per caller. In
bench/service that copied `pace` and `single` into `client`, and `request`
into `single` and `sub-get`. Memory fell as it should (616 B to 504 B a
parked client), but the A/B against the build without it found:

                                       old       new
    all cores, closed loop, wall ms    251.5     269.5   (+7%; CPU +7%, p99 +34%)
    all cores, 1M/s, p99 us             3837     10690   (p99.9 7514 to 14904)
    4 cores, closed loop, wall ms      397.5     388.0   (-2.4%)

The copy changes where the caller goes on. A called bjoroutine that
finishes wakes its caller through `Promise.Wake`, which queues it. The copy
has no promise: the caller goes on inline on whatever thread resumed the
copy. After `request`'s receive of a shard's reply, that is the shard's
thread, which then ran the client's whole next request before it served
anyone else. With all cores, the shards are the bottleneck. A build that
copied only `pace` (the others written `#:no-inline`) had no slowdown, and
used 4% less CPU at 1M/s.

So nothing is copied without being asked, and the measurements say which
calls to ask for: a wait whose resumer is not a busy fiber, such as a sleep.
`pace` is written `#:inline`; `request` is not. The rules a copy keeps:
only at a call where a yield point is allowed, never a spawned call, not a
call given a function (an ordinary function given where a bjoroutine is
expected is bridged where it is given), only a body that has a yield point
of its own, and not a function that names itself. See `Docs/Syntax.org`.

`pace` written `#:inline`, against the build before, as in the A/Bs above:

                                   old       new    new - old, 95% interval
    4 cores, 0.25M/s, 90 reps
      cpu ms                      3119    2989.5    -150 .. -93.5
      p99 us                      33.6       7.4    -26.5 .. -25.7
      p99.9 us                    50.4      19.1    -33.2 .. -29.9
    all cores, 1M/s, 30 reps
      cpu ms                     26458     25243    -1472 .. -879
      alloc MB                    4876      4778    -100 .. -87
    the closed loops, 50 reps each: no difference

The tail fell for the reason the slowdown above rose. A client woke in
`pace`, and `pace`'s promise then queued the client: a hop through the
scheduler inside every measured latency. The copy goes on where the timer
woke it. A parked client keeps 488 B in place of 616 B: `CalledFiber<pace>`
(144 B) is gone, and the client's frame grew by 16 B.

The same change halved the size up to which an ordinary function that takes
a function is inlined by itself, from 100 nodes to 50. The walks of the
prelude past it (`list-filter`, `list-foldl`, `list-foldr`, `vec-filter`,
`vec-fold`, `vec-iter`) are written `#:inline`, so they are still inlined.

### Three findings of a review of the runtime

A review of the runtime's code listed about thirty points. Most were small
costs on paths that `bench/service` does not use, or were not true (a
`SpinGate` is a class, so a `readonly` field of it makes no copy). Three were
changed.

**A with-nack fired for the branch that won.** A partner that pairs with a
parked op makes the state synchronized under the channel's lock, and writes
the winner's id in `MarkSynchronized`, after it lets go of the lock. A
`WithNackEvent` that looked at the state in that gap, just after it
published its subtree, read the id as -1. It took -1 for a branch before
its own and fired the nack, and the branch inside it won. The test "nack
silent when its subtree wins while it publishes" sends on the channel from
a thread without pause: before the change it failed in 3 runs of 5, with 2
to 6 false nacks in 200,000 rounds. Now an id of -1 leaves the decision to
`MarkSynchronized`, which still comes and finds the node. The node's `I1`
is written with a fence, so that `MarkSynchronized` reads it if the id
read was -1: each side writes, fences, and then reads what the other wrote.

**A timer tick could run fibers.** `Workers.RunItem` fills the inline
budget for each item, and nothing emptied it when a pump gave its thread
back to the pool. The pool then ran the timer wheel's tick on that thread,
and `FireFromWheel` resumed up to 32 fibers inline, outside any worker,
while the due nodes after them waited. With `BJO_SCHEDULER=pool` there was
no budget at all. Now `Workers.Run` empties the budget when it returns, and
`FireFromWheel` queues the resume.

**A search for work looked at four times too many workers.** `Steal`,
`AnyWork` and `Monitor` went over all `4 * procs` workers. Only the first
`s_limit` are ever bound, and the limit only grows, so the others are
always empty. Now they look at those only.

Old and new runtime, same `service.exe`, 50 reps each, order flipped each
round:

                                   old       new    new - old, 95% interval
    4 cores, 0.25M/s
      cpu ms                    3013.5    2955.5    -94 .. -16.5
      p99 us                       7.5       6.6    -1.3 .. -0.5
      p99.9 us                    18.8      16.9    -2.9 .. -1.0
    all cores, 1M/s: no difference (cpu -0.7%, p=0.10)
    all cores, closed loop: no difference

The gain at 4 cores is from the tick: `pace`'s sleep ends there, and every
request starts from it.

### A sync that commits at once still yields past the budget — rejected

When a `choose` commits as it publishes, the channel resumes the partner and
then calls the syncing fiber's own sink, both through `Scheduler.Dispatch`.
With the inline budget spent, both are queued: the partner goes to
`runnext`, the sink goes there after it and pushes the partner to the queue,
and the fiber parks although its sync is done, until its sink comes round. A
second review called that a false suspension, and both `choose`s of
`bench/service` (the shard's loop and `request`) go that way.

The sink of an awaiter whose fiber has not parked only stores the value, so
it was called directly, outside the budget (`Scheduler.DispatchOwn`, for an
`EventAwaiter` and for a wrap with a host). Against the runtime before it,
50 reps each:

                                       old       new
    4 cores, closed loop, p99 us      3141      4998   (p99.9 4690 to 6090)
    all cores, 1M/s, p99 us           1600      6152   (p99.9 4834 to 9131, CPU +2.7%)
    4 cores, closed loop, cpu ms      1551.5    1520.5
    all cores closed, 4 cores 0.25M/s: no difference

The suspension is the budget at work. A fiber that has used up the budget
of its work item has to let the queue run, and a sync is where it can.
Called directly, the sink let the fiber go on: a shard that had spent its
budget went on to the next request, and the clients in the queue behind it
waited. The CPU and allocation saved by not parking is real (2% and 2.7% at
4 cores), but the tail pays for it.

The same review found that a spinning pump that finds work on its last look
before it stops does not spin again, so the work it then finds wakes nobody.
Go's spins again. Made to spin again and measured alone, 50 reps each, on
the closed loops and at 1M/s: no difference, so it was not kept.

### Latency from the due time

The open-loop tables before this section count a request's latency from
when its client woke, if the client slept until the request was due (see
the correction in "At a fixed rate"). Bjolang's p99 at 0.25M/s on 4 cores
was 6 us by that measure, against 13 for Hopac and 1013 for Go: less than
the time a GC pause or a timer tick takes, at a rate where both happen
many times a second. The delays before a client runs were not in the
number.

All three programs now take two latencies of each sampled request. The
latency (`p50`, `p99`, `p99.9`) counts from when the request was due, as a
caller of the service would see it. The service time (`svc`) counts from
when the client ran, which is what the tables before measured. A client
that is early sleeps in whole milliseconds, rounded up, in all three
programs, so every latency has up to a millisecond of that in it, and the
medians are 0.75-1.1 ms. Below about 2 ms the latency says little.

`drive.py`, all four runs as in "Memory, and Go with as much of it", median
of 3 x 5 reps. The closed loop has no sleep, and its latency and service
time are the same thing:

                   wall ms  cpu ms  p50 us  p99 us  p99.9 us  gcs  peak MB
    all cores
      Go               400    5542      13    4487      5838  108       27
      Hopac            436    8854     113    2995     12653    7      387
      Bjolang          256    5194     103    1531      5617    6      427
      Go, 426 MiB      259    4057      16    2855      3227    1      402
    4 cores
      Go               479    1847     175    2489      3393  107       24
      Hopac            294    1146       1    2357      3790   46      109
      Bjolang          411    1587       1    3077      4659   32      186
      Go, 186 MiB      425    1680     187    2149      3052    3      179

                          cores  p99 us  p99.9 us   svc p99  svc p99.9  peak MB
    4 cores, 0.25M/s
      Go                    0.9    5024      8077      1268       6087      162
      Hopac                 1.4    2702      4725        10         31      233
      Bjolang               1.0    2507      2987         5         15      260
      Go, 259 MiB           0.9    2128      4948       255       2625      323
    4 cores, 0.5M/s
      Go                    1.9   33116     38042     32534      37568      174
      Hopac                 2.3    7288      8660         7         19      237
      Bjolang               1.5    2990      4212         6         18      283
      Go, 282 MiB           1.8   24958     31099     24957      31099      374
    all cores, 0.5M/s
      Go                    3.4    9227     12320      6741       9321      177
      Hopac                21.5   23538     30542     21112      24905      407
      Bjolang               4.2    4964      6727        58        369      425
      Go, 425 MiB           2.9    6592     11032      5905      10918      448
    all cores, 1M/s
      Go                    8.4   31095     34804     30817      34755      203
      Hopac                22.2   27145     34484     24506      30076     1033
      Bjolang               8.3    7438     11654      1769       5977      464
      Go, 1033 MiB          6.1   32234     40023     31645      40023     1084

What holds, measured from the due time:

- On 4 cores at 0.25M/s the three are close, with p99 between 2.1 and 2.7
  ms (default Go: 5.0). That is mostly the sleep's rounding and a timer
  tick. Bjolang has the lowest p99.9, 3.0 ms against 4.7-4.9.
- At 0.5M/s on 4 cores Bjolang has the lowest tail, 3.0 ms at p99 against
  7.3 for Hopac. Go's is 25-33 ms with either memory, and its service time
  is as long, so that delay is in the run of a request, not before it.
- On all cores, Bjolang has the lowest tail at both rates: 5.0 ms p99 at
  0.5M/s against 6.6 for Go with as much memory, and 7.4 ms at 1M/s
  against 27-32 for the others. An earlier run of the same, with the
  programs changed by hand, gave Bjolang 3.6 ms and Go with as much
  memory 2.1 at 0.5M/s, so that pair is not settled.

The service time and the latency differ most for the two .NET programs:
Bjolang's p99 is 5-58 us of service time in milliseconds of latency, below
1M/s. A request that has started finishes fast; the time goes before it
starts, in the sleep's rounding, a timer, the run queue and the GC pauses
that fall while clients sleep. Go's service time is close to its latency
whenever its tail is long, so Go's delays fall while requests run. Which
runtime is better depends on the latency, and the service time says where
the time goes.

### Where the cycles go, and two type checks fewer

`perf` on `bench/service`'s closed loop on all cores, with the perf map on,
ReadyToRun and tiering off so that every method has a name and its final
code (`DOTNET_PerfMapEnabled=1 DOTNET_ReadyToRun=0
DOTNET_TieredCompilation=0 DOTNET_EnableWriteXorExecute=0`; without the
last, JIT code shows as `memfd:doublemapper`):

    channels, events, sync              26%
    TLS, casts, stubs, write barriers   20%
    the program's own code              14%   (crunch 7%)
    GC                                  13%   (server GC threads joining 6%)
    fibers and scopes                   12%
    scheduler: spin, steal, find work    7%
    channel locks, contended             1%

Two of the casts were ours. `StealHalf` copied items between arrays of
`IThreadPoolWorkItem`, and a store into an array of a reference type checks
the element's type: 2.8%. A worker's ring is now an array of a struct around
the item. `Promise.Complete` asked `is List<object>` and then `is
PromiseWaiter`; neither type is sealed, so both are calls that walk the
type's parents, and the waiter is nearly always a fiber's box, which is
neither: 2.9%. It now compares the list's exact type, and asks for
`IResume` first.

A/B against the build before, 50 reps each, the open loop at 1M/s 100:

    all cores, closed loop, cpu ms    5290 to 5141   (-2.8%, p=0.04)
    the rest of wall, tail and cpu:   no difference

The first 50 reps at 1M/s put the new build's p99 10% higher with p=0.004,
and the next 50 put it 16% lower: the tail at that rate moves with where the
GC pauses fall, and one A/B of it can mislead.

### A yield does not wake a core for its partner

`cmlbench`'s skewed choose, a chain of two fibers, took 230 ns a round with
Workers and 105 with the pool, on 5.4 s of CPU against 2.0. Unlimited inline
budget brought it to 96 ns and 1.2 s, so the budget was the cause, though
the budget is wanted (see "Run queues of our own").

Counters in a copy of the runtime, over 5 million rounds, gave the
mechanism. Of 313,000 pushes to a worker, 105,000 displaced the item in
`runnext`, and each woke a pump: 85,000 wake-ups and 26,000 steals. Past
the budget, a `choose` that commits as it publishes queues two things, the
partner that the channel resumed and then its own sink. The sink, the
fiber yielding, went to `runnext` after the partner, pushed the partner to
the back of the queue, and woke a pump, which stole the partner. The chain
then ran between two cores, each handover a cross-core one, and the pumps
spun in between: the cycles were spread evenly over six threads, and
`SpinWait` was a third of them.

Three ways to queue that sink, against the build before:

                                 choose, ns   CPU, s   bench/service
    as before                       235        5.4
    sink to the back, no wake        93        1.2    p99 at 1M/s 6.1 to 12.2 ms,
                                                       4 cores closed 3.1 to 4.8
    sink to runnext, partner         188        1.7    no difference, all four
      to the back, no wake

The sink at the back of the queue keeps the chain whole, but in
`bench/service` the fiber yielding is the shard, the bottleneck, and every
client waits behind it, as when the sink ran inline. The third keeps the
sink first and only stops the partner from waking a core: a fiber that
yields is no new work for another core, and its partner is the other half
of a chain this core runs. `Scheduler.DispatchOwn` queues such a sink
through `Workers.PushYield`. The A/B against the build before, 50 reps each,
found no difference in wall, CPU, p99 or p99.9 in any of the four runs.

---

## 6. Known issues

### Channel ordering is not guaranteed

A contended operation is re-queued at the **tail**, so FIFO is not preserved and a
sender can be starved under sustained contention. Worth stating in the language
manual so the runtime is not held to FIFO later.

### Blocking bridge

`Bjo.RunToCompletion` blocks the calling thread. Do not call it from a thread-pool
thread: the fiber needs pool threads to make progress and you are holding one hostage.
