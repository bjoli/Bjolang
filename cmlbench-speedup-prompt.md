# Task: close the gap between `bench/bjolang/cmlbench.bjo` and `bench/Cml/Program.cs`

You are working in the Bjolang repository (F# compiler, C# runtime `BjolangRuntime` on top of the BjoML CML layer). The Bjolang twin of the CML benchmark is 3–4× slower than the raw C# twin on the **Skewed choose(8)** row, and somewhat slower on the ring rows. Your job is to (1) measure where the gap comes from, subtractively, (2) fix the inconsistencies listed below, and (3) implement the speed-ups that the measurements justify, without changing cancellation semantics.

Work on a branch. Never commit a measurement hack; they are listed separately from fixes.

## Starting hypothesis — verify it before doing anything else

Read these and confirm or refute each claim by reading the code, not from memory:

- `Scope.cs` → `RunMainFiber` opens a scope with `scopesubopen_BANG(0)` and installs it with `scopesubpush_BANG`, which sets `Dyn.Current.Cancel = scope.Token`. So inside `main`, and in every `bjo`/`spawn` child (they inherit the environment), the ambient token is a live `Promise<CancelReason>`, **not** `RootCancel`.
- `Concurrency.cs` → `sync` only skips the race when the token *is* `RootCancel`. `SyncOp.GetAwaiter` tries `INowable`, then `IDirectSyncable`; `ChooseEvent<T>` (Event.cs) implements neither, so a `choose` always falls through to `new CancellableEvent(ev, token)`.
- `CancellableEvent.Publish` publishes `token.Join()` as one more branch. That costs a closure object + delegate + a `Promise<T>.Waiter`, registered on the token via `Promise.RegisterAny` under `lock(list)`, and pruned only every `_pruneAt` registrations. The token never fires in the benchmark, so all of it is dead weight.
- Single channel ops take the `CancelWatch` link instead (one object, no `SyncState`); `choose` was deliberately left on the branch path — see the comment in `SyncOp.GetAwaiter`.
- `chan-send` allocates a `ChannelSendEvent<T>` and goes through `IDirectSyncable` + `EventAwaiter.Rent`; the C# twin's `ch.Send(i)` is the struct `ChannelSendOperation<T>` with a struct awaiter that rents only a `PutOp`. `EventAwaiter`'s pool is `[ThreadStatic]`, rented on one thread and recycled on the resuming thread.
- The Bjolang harness does not `GC.Collect()` before each rep; the C# `Measured` does.

If any of this is wrong, say so in the report and adjust the plan.

## Part 1 — Measure (subtractive, like design.md §B7)

Protocol for every measurement in this task:
- Build both twins in Release. Run them **interleaved** in the same session (C#, Bjolang, C#, Bjolang…) so the bimodal cross-chiplet mode hits both equally.
- 5 reps per row, record **min** ns/op and B/op, and also the median (the C# twin already prints it; add it to the Bjolang harness — see Part 2).
- Record `GC.CollectionCount(0)` across each rep.
- Keep a single results table in the report, one column per experiment, all four Bjolang rows plus the three C# rows. A change that speeds up the skewed row and regresses a ring row is not a win.

Measurement hacks (branch-only, never committed, one at a time, revert between them):

1. **Whole token cost.** In `sync`, `return new SyncOp<T>(ev, null);`. If the skewed row lands near the C# number, the rest of this task is about the token and nothing else.
2. **Lock + list vs. allocations.** Keep the token but comment out `Register(new Waiter(...))` in `Promise<T>.Publish` (the main token never fires in the benchmark, so this is safe *for measurement only*). The difference between (1) and (2) is the cost of `CancellableEvent` + closure; the difference between (2) and baseline is the registration, its lock, and the pruning.
3. **Send event.** Temporarily make the sender in `cmlbench.bjo` call a hand-written `await ch.Send(i)` through a small `import/extern` or a test-only runtime helper, bypassing `chan-send`. This measures the `ChannelSendEvent` + `EventAwaiter` cost on the sender side.
4. **Awaiter pool misses.** Add a temporary counter in `EventAwaiter<T>.Rent` for the `new` fallback and print it per rep. High counts mean the thread-static pool is thrashing under ping-pong.
5. **Harness.** Add `GC.Collect()` + `WaitForPendingFinalizers` + `GC.Collect()` before each timed rep in the Bjolang harness (this one becomes a real fix in Part 2) and compare.

Report the table before starting Part 3.

## Part 2 — Fix inconsistencies (these are real commits)

1. **Stale docstring.** `sync` in `Concurrency.cs` says "`main` is not in a scope, which is what makes the first paragraph the common case rather than an unreachable one." `RunMainFiber` makes that false. Rewrite the "What it costs" section so it describes what actually happens under the main scope, for a single channel op and for a `choose` separately.
2. **Row names and comments in `cmlbench.bjo`.** `ring-unscoped` runs under the main scope's token, so its nodes *do* race a token; the `ring-scoped` comment ("every `sync` in every node now races the scope's live token, which is the row's whole point") describes something both rows do. Make the comments true and rename the rows so the difference they measure — a nested `with-cancel` and its per-child bookkeeping, not "token vs no token" — is what the name says.
3. **Make the twins twins.** The C# skewed receiver is a spawned fiber joined from outside; the Bjolang one runs on the main fiber. Spawn it with `bjo` and join both handles, as the C# twin does. Check the ring kick-off send the same way and note any remaining differences in the file header.
4. **Harness parity.** In the Bjolang harness: settle the heap before each rep (see hack 5), print the median beside the min, print `GC.CollectionCount(0)` per rep, and print the `.NET` version / `ProcessorCount` / `ServerGC` header the C# twin prints. Use `import/extern`; check `DotNetInterop.fs` for how overloads such as `System.GC.Collect` are resolved before assuming a signature.
5. **`ignore` docs disagree.** One docstring in `Concurrency.cs` says `(ignore p)` already goes through `Discard`'s implementation for `Promise`; another says that is what it *will* mean once the `Discard` trait exists. Find out which is true and fix the other.
6. Any other comment you find that the code contradicts while reading — list it in the report, fix it if it is local.

## Part 3 — Speed-ups, in order; measure after each; stop when the number says to

### 3a. Collapse the token branch for `choose` (low risk, do first)

`CancellableEvent.Publish` allocates three objects for the token branch (closure, delegate, `Promise.Waiter`). Replace them with **one**: a small `sealed class` deriving from `PromiseWaiter` that holds the `SyncState`, the event id, the `onSync` delegate and a back-reference to the `CancellableEvent` (or folds `Why` into itself), with `Signal()` doing what the closure did and `IsAbandoned => state.IsSynchronized`. Register it directly on the token. Same semantics, ~2 allocations fewer per parked sync.

### 3b. Registration cost (only if hack 2 showed it matters)

If the lock and list dominate, consider replacing `List<object>` + `Monitor` in `Promise.RegisterAny` with an intrusive lock-free push (a `_next` field on `PromiseWaiter`), pruning by rebuilding. Keep the amortised prune guarantee. Bare `Action` waiters still exist (`OnCompleted`), so keep a path for them.

### 3c. Persistent registration per fiber (bigger; only if 3a + 3b leave a large token cost)

Design constraints, all of which must be respected or the change is rejected:
- The runtime has no logical-fiber identity today: a fiber is a stack of nested `async Fiber` boxes, and `FiberContext.Current` is a `DynEnv` **shared** with spawned children (`Fiber.cs`: `Context = FiberContext.Current`), so a mutable "parked here" cell cannot live in it. You will need a second per-thread slot that every box's `Run()` saves and restores next to `FiberContext`, set to a fresh object at spawn and inherited through nested calls. Say in the report what this commits BjoML to.
- Lost wake-up: a persistent registration can be signalled while the fiber is between syncs. Each park must publish its parked state with a full fence, re-check `token.IsCompleted`, and race the signaller for the claim with an interlocked generation word. Reuse the two-contender word `CancelWatch` already has; the generation number is what rejects a stale signal ("signalled on behalf of a sync it no longer belongs to" — the hazard the "why it is not pooled" comment describes).
- `Signal` for a parked `choose` must go through `SyncState.TryCommit` with an event id reserved at publish time. Never bypass the commit protocol.
- A fiber's completion must flip the registration's `IsAbandoned`, or a long-lived scope keeps dead entries until the prune.
- One registration per (fiber, token). `sync/blocking` keeps the branch path.

### 3d. Send-event allocation (only if hack 3 showed it matters)

The fast paths are deliberately attached to the *value*, not the syntax (see the `INowable` docstring). If you add a syntactic fast path for `(sync (chan-send ch v))`, it must (a) keep the value path as fallback with identical semantics, (b) still watch the ambient token via a `CancelWatch`-style link, and (c) be an intrinsic the emitter recognises, not a change to `chan-send`'s type. Document the limitation this introduces: an event that reaches `sync` through a variable or a `guard` takes the slow path.

### 3e. Awaiter pool (only if hack 4 showed high miss counts)

Try a larger per-thread cap first; if drift is the problem, try returning to a small global free list on the recycle side. Measure the ring rows too — they are the case the pool was built for.

## Semantic invariants — every change must keep these, and add tests for them

Add to `Tests/CmlTests.cs` (or the Bjolang test suite, whichever covers the path) if not already present:
- A parked `choose` under a scope whose token fires raises `Cancelled` on the resuming fiber's own stack, not inside a promise completion walk.
- A `choose` with an available branch wins over a token that has already fired ("a delivered value beats a fired token"; publish order is priority).
- A token firing after a branch committed does not undo the commit and does not lose the value.
- A token that fired *before* the sync cancels without parking anything.
- Cancellation never runs user code on the signalling thread.
- Dead-taker residue in the 7 idle channels stays bounded (the existing B7 tests must still pass).
- No `sync` under a scope leaks a waiter on the token per rendezvous after the scope has run for a while (assert the token's waiter count stays bounded across 4000 iterations).

Also keep: "store the channel, not the op" and "clean outside the state lock" from design.md.

## Deliverables

1. The results table (Part 1), then a second table with before/after for each change in Part 3, all rows, both twins.
2. The Part 2 fixes as separate commits.
3. Each Part 3 change as its own commit with the numbers in the message.
4. A short section appended to `design.md` in the same style as §B7: what was tried, what it cost, what was kept, and what was removed and why — including any candidate you measured and rejected.
5. A list of every claim in the "starting hypothesis" you found to be wrong.
