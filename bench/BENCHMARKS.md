# BjoML StressTest benchmarks

Run with:

```
dotnet run -c Release --project StressTest
```

Machine: AMD Ryzen 9 5900X (12C/24T), .NET 10.0.104, Linux 6.18 (Fedora 43).

> **A note on the `Task`+`SyncAsync` rows below.** They measure the plain-C#
> `ValueTask` façade — `Cml.SyncAsync` over a pooled `CmlValueTaskSource` — which
> has since been deleted; see `docs/design.md` §"Scope". The numbers are kept
> because they are what motivated the `Fiber`+`await` path and they price the
> interop boundary that a future reintroduction would have to pay again. They are
> not reproducible against the current tree: the harness rows that produced them
> are gone with the surface.

---

## Baseline — before the IThreadPoolWorkItem / Fiber migration

Commit state: original `Scheduler.cs` with dedicated `Worker` threads +
`BlockingCollection<Action>`, `ProposedChanges/` excluded from compilation.

4 consecutive runs, Release build, milliseconds:

| Benchmark | Messages | run 1 | run 2 | run 3 | run 4 | median |
|---|---|---|---|---|---|---|
| CML Ring (1000 workers x 1000 trips) | 1,000,000 | 403 | 408 | 398 | 402 | **402** |
| SimpleChannel Ring (1000 x 1000) | 1,000,000 | 195 | 196 | 199 | 203 | **198** |
| CML Fan-In/Fan-Out (100 prod x 100 cons x 5000) | 500,000 | 126 | 128 | 128 | 125 | **127** |
| SimpleChannel Fan-In/Fan-Out (100 x 100 x 5000) | 500,000 | 242 | 257 | 217 | 188 | **230** |

Correctness checks in the same run:

- Combinators test result: `HELLO (from chan2)` — correct.
- `Nack fired for chan1?` → `True` — correct.

Notes on noise:

- The three CML numbers are stable to within ~3%.
- `SimpleChannel Fan-In/Fan-Out` is the noisy one (188–257 ms, ~30% spread); it
  is lock-based and sensitive to scheduling. Treat a change there as significant
  only if it moves the median by more than ~30%.

---

## After the IThreadPoolWorkItem / Fiber migration

Dedicated `Worker` threads and `BlockingCollection` replaced by
`ThreadPool.UnsafeQueueUserWorkItem(IThreadPoolWorkItem, preferLocal: true)`, plus
the `Fiber` / `AsyncMethodBuilder` layer. 4 consecutive runs, Release, ms:

| Benchmark | run 1 | run 2 | run 3 | run 4 | median | baseline | change |
|---|---|---|---|---|---|---|---|
| CML Ring (ValueTask path) | 448 | 437 | 484 | 422 | **442** | 402 | +10% slower |
| SimpleChannel Ring | 126 | 142 | 139 | 125 | **132** | 198 | −33% faster |
| CML Fan-In/Fan-Out | 125 | 128 | 126 | 137 | **127** | 127 | unchanged |
| SimpleChannel Fan-In/Fan-Out | 208 | 208 | 216 | 203 | **208** | 230 | −10% (within noise) |
| **Fiber Ring** (new) | 326 | 314 | 336 | 351 | **331** | — | new |
| **Fan-Out** (new) | 121 | 120 | 123 | 119 | **121** | — | new |

### Analysis

**The fiber path is the one that matters, and it is faster than the old baseline.**
`Fiber Ring` (331 ms) does the same 1 000 000 message ring as `CML Ring`, but with
each node an `async Fiber` awaiting `IEvent` directly rather than a `Task` awaiting a
pooled `ValueTask` source. It beats both the new ValueTask ring (442 ms) and the
*old* baseline ring (402 ms), because awaiting an event skips
`CmlValueTaskSource` entirely — no pooled source, no `ValueTask` wrapper, and the
inline-rendezvous case allocates no continuation at all.

**The ValueTask ring regressed ~10%, and this is the expected trade.** The ring is a
single hot chain: exactly one message is in flight, so the old design's
"always enqueue to my own worker's queue" was optimal for it — the whole ring
effectively ran on one thread with perfect cache locality and no contention. The .NET
pool deliberately makes that work *stealable*, so the chain migrates between threads
and pays cache-miss costs. We bought fan-out scalability with ring locality. Note the
ring only touches the scheduler at all when the inline-dispatch budget
(`Scheduler.MaxInlineDepth`, 50) is exhausted; the rest runs inline either way.

**Fan-Out is what the old scheduler could not do at all.** 480 children spawned from
inside a single fiber, 3 000 000 iterations of CPU work each: 121 ms against a
measured serial reference of ~1 550 ms, a **12.3–13.6x speedup** on 12 physical /
24 logical cores. Under the old scheduler `Scheduler.Enqueue` from a worker always
targeted *that same worker's* queue with no stealing, so all 480 children would have
serialised onto one core while the rest sat blocked in `GetConsumingEnumerable()`.
This is bug B6 in the review, and it is the reason for the migration.

**SimpleChannel Ring improved 33%** — it has no CML machinery, so it mostly measures
raw handoff, and the pool's local queues beat a `BlockingCollection` per worker.

Both fan-in/fan-out numbers are unchanged within noise; they were already spread
across many threads, so neither scheduler was the bottleneck.

---

## Experiment: is `MaxInlineDepth` leaving performance on the table?

**Answer: no. More inlining is worse. Do not raise it, and do not build a trampoline
without re-running this.**

Instrumenting `Scheduler.Dispatch` during the fiber ring shows **2 002 000 dispatches
of which only 39 254 (1.96%) bounce to the pool** — almost exactly 1/51, which is what
a depth-50 limit predicts on a linear chain. 98% of continuations already run inline.

Sweeping the limit (2 runs each; counters perturb absolute timings slightly, so read
the trend, not the absolutes):

| `MaxInlineDepth` | bounced | Fiber Ring | CML Ring (ValueTask) |
|---|---|---|---|
| 50 (shipped) | 1.96% | 364, 369 ms | 483, 475 ms |
| 200 | 0.50% | 351, 356 ms | 566, 555 ms |
| 1000 | 0.00% | 454, 429 ms | 688, 647 ms |
| 5000 | 0.00% | 437, 457 ms | 671, 663 ms |

Eliminating the bounce **entirely** (depth ≥ 1000 — the whole 1000-node ring trip in
one recursive chain) makes the fiber ring ~20% *slower* and the ValueTask ring ~40%
slower. The cost of a deep stack — cache and TLB pressure, stack page growth, nested
`try/finally` frames — overwhelms the saving from skipping a queue push/pop long
before the bounces run out.

50 → 200 is within run-to-run noise for the fiber ring (its spread at depth 50 across
all sessions is 314–369 ms) while clearly hurting the ValueTask ring, which pays more
stack per hop because its `ValueTask` continuations also run inline
(`RunContinuationsAsynchronously` is false).

Two conclusions:

- The bounce is not a bottleneck, so the obvious "trampoline" idea — park the
  continuation in a thread-local list at depth 50, unwind, and drain it on the same
  thread, giving 0% bounce with a bounded stack — is chasing at most a few percent.
  That is a poor trade against its real cost: work parked in a thread-local list is
  **not stealable**, which reintroduces B6 in miniature, and it needs careful drain-on-
  exception handling or work is silently stranded on an idle thread.
- `preferLocal: true` is already doing most of the job the trampoline would do. The
  local queue is LIFO, so a bounced continuation is usually popped straight back by
  the same thread; the enqueue is not a thread migration in the common case.

---

# Spawn throughput vs Go

Machine: AMD Ryzen 9 7900 (12C/24T), .NET 10.0.7 (ServerGC), Go 1.26.5, Linux 7.0.

`bench/Bench` and `bench/go` are shaped identically: same counts, same topology,
same work per unit. `bench/Diag` is the subtractive harness used to attribute cost.

## The gap, as measured

Medians of 3 runs, ns/op:

| Benchmark | BjoML before | Go | note |
|---|---|---|---|
| Spawn storm | 321 | 201 | **the real gap, 1.6x** |
| Ping-pong | 236 | 162 | |
| Ring | 128 | 90 | |
| Select/Choose | 214 | 134 | Go's `select`; not composable |
| Fan-out | 113 ms | 120 ms | equal |
| Spawn+send | 661 | 1778 | **not evidence, see below** |

`Spawn+send` is 200 000 senders on ONE unbuffered channel. Go handles that
particularly badly — every blocked sender allocates a `sudog`, links onto
`hchan.sendq` under the channel mutex, and the receiver hands off to exactly one
sender at a time. It is a lock convoy, not a rendezvous measurement, and it should
not be read as a BjoML win.

## Which cost dominates: GC or the queue?

**The queue, overwhelmingly. GC is ~9%.**

The decisive experiment is subtractive: run the same 1e6-unit workload with one
ingredient removed at a time. All rows use one shared completion atomic, padded onto
its own cache line, so the benchmark's own bookkeeping is constant across rows.
Medians of 7 reps:

| row | default GC | GC *entirely* disabled (`DOTNET_GCgen0size=0x20000000`) |
|---|---|---|
| `Parallel.For` chunked, no queue at all | 11 | 10 |
| one pool work item per unit, **zero allocation** | 185 | 204 |
| one pool work item per **32** units | 31 | 30 |
| `Bjo.Spawn` | 216 | 196 |

Three readings, and they all say the same thing:

1. **GC is ~9%.** With gen0 raised to 512 MB the spawn storm does *zero* gen0
   collections and reports `GCpause = 0.0 ms`, and `Bjo.Spawn` improves only
   216 → 196 ns. The GC pause in the default configuration is ~12–38 ms out of
   ~216 ms.
2. **Allocation itself is ~free.** A pre-allocated, zero-allocation work item costs
   the *same* 185 ns as one allocated per unit. The .NET allocator is a pointer bump;
   80 bytes of gen0 garbage is not what is expensive.
3. **How the queue operations are DISTRIBUTED is ~85% of the cost.** Same allocation,
   same atomics, same work — batching 32 units per queue operation takes 185 → 31 ns.

   Note carefully what this is *not* saying. The obvious reading is "fewer queue
   operations = faster", and that reading is wrong: the batch rows above split all
   the way down to leaves of ONE item, so they perform roughly the same number of
   queue operations as the unbatched row, and they are still 6x faster. What changes
   is *who* performs them. Unbatched, one thread pushes a million items into its own
   local queue and 23 threads contend to steal them one at a time under the victim
   queue's foreign lock. Batched, the tree spreads the pushes across every thread's
   own local queue, where push/pop is uncontended and nearly free.

   This distinction matters, because "fewer queue ops" motivates a coarse split floor
   and that turns out to be actively harmful — see the parallelism sweep below.

BjoML's own machinery is only ~30 ns of the 216 (`Bjo.Spawn` 216 vs raw pool 185).
The fiber layer was never the problem.

### Why the .NET pool costs ~185 ns per item here

One producer, 24 consumers, a million tiny items. Every enqueue touches the pool's
thread-request counter; every consumer steals **one item at a time** under the
victim queue's foreign lock. The pool also over-injects threads on a deep backlog of
tiny items — the harness observed 24 → 44 live worker threads on a 24-thread box.

This is precisely where Go's scheduler differs, and it is not a micro-optimisation
detail: Go moves runnable work in **batches** (a local runq spills 128 goroutines to
the global queue at once; a steal takes *half* the victim's queue). .NET moves one
work item per operation. Nothing about `preferLocal` changes this — `true` and
`false` measured identically (185 vs 185).

## The fix: batch the spawns, not the continuations

`SpawnBatch` parks freshly spawned fibers in a thread-local buffer and hands the pool
a *batch* work item every 64 fibers. `Scheduler.EnqueueSpawn` is deliberately
separate from `Scheduler.Enqueue`: a rendezvous continuation wants to run on the
thread that completed the match (locality, and there is only ever one of it), while a
spawn has no locality to preserve and arrives in bursts.

Three properties make it safe, and all three are load-bearing:

- **Burst detection.** The first 8 spawns of a run go straight to the pool, so
  "spawn a child and await it" is byte-for-byte the old path and cannot regress.
- **Flush on yield.** Every work-item boundary flushes, so a fiber that suspends or
  returns publishes whatever it queued.
- **Watchdog.** A 1 ms timer flushes buffers whose owning thread has stopped
  cooperating. This is *not* belt-and-braces: without it, a thread that spawns >8
  fibers and then blocks on a non-BjoML primitive strands them **forever**. Deleting
  the `EnsureWatchdog()` call turns 6 tests in `SpawnBatchTests` into hangs.

The batch does not run inline as a block — that would trade queue cost for lost
parallelism, which is bug B6 in a new hat.

## Does the split policy have to be a fixed heuristic? No, and it must not be

The first version of `SpawnBatchItem` split recursively down to a fixed floor of 8
and ran the leaf inline, justified by "splitting to 1 would reintroduce the per-item
queue cost". **That justification was wrong, and the constant was a 4.6x bug.**

A fixed floor decides how much parallelism a batch can reach *before any thread has
run*: a batch of N becomes ceil(N / floor) chunks and each chunk runs sequentially on
one thread. With floor 8, sixteen CPU-bound fibers become two chunks — two-way
parallelism on a 24-core box.

The throughput harness cannot see this. With a million trivial fibers there is so
much work that every policy fills every core. It only shows up when N is small but
above the burst threshold and each fiber is expensive, which is why the original
480-child fan-out benchmark missed it entirely, and why the original test — which
asserted only that more than one thread was touched — passed while it was happening.

`bench/Diag --mode fanout` sweeps N CPU-bound fibers against a calibrated serial
reference and measures achieved speedup (ideal = `min(N, cores)`):

| N | ideal | unbatched | floor=8 | floor=4 | floor=2 | floor=1 | adaptive |
|---|---|---|---|---|---|---|---|
| 12 | 12.0 | 14.9 | **6.8** | 7.0 | 7.5 | 10.9 | 13.0 |
| 16 | 16.0 | 19.8 | **4.3** | 8.8 | 11.9 | 17.5 | 17.3 |
| 20 | 20.0 | 19.7 | **6.9** | 11.1 | 14.6 | 20.5 | 20.0 |
| 24 | 24.0 | 19.5 | **6.3** | 11.1 | 14.2 | 20.5 | 24.8 |
| 48 | 24.0 | 25.6 | 14.6 | 18.5 | 22.3 | 22.6 | 21.9 |
| 256 | 24.0 | 29.2 | 24.5 | 27.4 | 28.3 | 28.7 | 28.9 |

And the throughput cross-check on 1e6 trivial fibers — the thing the floor was
supposed to protect:

| policy | ns/op | B/op | GC pause |
|---|---|---|---|
| unbatched | 289 | 80 | 5.6 ms |
| tree floor=8 | 34 | 92 | 2.3 ms |
| tree floor=2 | 33 | 104 | 0.9 ms |
| tree floor=1 | 35 | 120 | 42.4 ms |
| **adaptive** | **30** | **89** | **0.5 ms** |

**Every batched policy has identical throughput.** The floor was protecting nothing
and costing up to 4.6x in parallelism.

`SpawnBatchMode.Adaptive` is therefore the default. It publishes the batch as one
shared range drained by an atomic cursor: threads take chunks until it is empty, and
a thread that finds work remaining recruits one more thread (capped at one per core,
self-limiting once the cursor is exhausted). Chunk size follows guided
self-scheduling — `remaining / (2 * workers)`, clamped — so it takes big bites while
there is plenty left and small bites near the end, which is what stops the tail
landing on a single thread. Achieved parallelism is then simply however many threads
turned up: one thread drains it correctly, twenty-four split it twenty-four ways, and
no constant had to predict which.

It is also the cheapest policy on allocation (89 B/op) because it re-enqueues
*itself* rather than allocating a new split item per level.

Ranges handed out by the cursor are disjoint, so `RunRange` can null out array slots
without synchronisation.

The regression test is `"a batch of 16 CPU-bound fibers actually runs in parallel"`,
which asserts achieved speedup rather than thread count, and fails at ~4.3x if the
tree/floor=8 policy is restored.

Note the adaptive chunk arithmetic is inert at the shipped capacity on a large box:
`remaining / (2 * procs)` is `64 / 48 = 1` on 24 cores, so adaptive degenerates to a
shared cursor handing out one fiber at a time. That is why it allocates least and
performs best, but it also means the chunking logic is **not exercised by any of the
numbers above**. On a 4-core machine the same expression gives 8, and chunks of 8
reintroduce head-of-line blocking *within* a chunk: a claimed chunk cannot be taken
by another thread, so a fiber that blocks its thread holds up its chunk-mates. That
path is untested.

## Watchdog fences

The watchdog stops itself when a sweep finds nothing pending and re-arms on the next
buffered spawn. That handshake is a store/load pair:

```
Add:   store _count = 1            ...  load  s_watchdogState
Sweep: store s_watchdogState = 0   ...  load  _count
```

x86 permits StoreLoad reordering, and neither `Volatile.Write` (release) nor
`Volatile.Read` (acquire) prevents it. With plain volatile accesses both sides can
read stale values, both conclude the other party will handle it, and the buffered
fibers are dropped — permanently, if the owning thread never spawns again. This is
the deadlock the watchdog exists to prevent, reappearing inside the watchdog.

Both sides therefore use a full fence: `Interlocked.CompareExchange` in
`EnsureWatchdog` (which is why there is no `Volatile.Read` fast path in front of it)
and `Interlocked.Exchange` in `Sweep`, followed by a re-scan after the stop is
published. `Add` arms only on the empty-to-non-empty transition, so the fence is paid
once per batch rather than once per spawn.

**These fences are reasoned, not test-covered.** Removing the re-scan in `Sweep`
still passes the stop/re-arm stress test three runs out of three; the window is a few
instructions wide. Detecting it would need fault injection — a stall hook inside
`Sweep` between the scan and the stop — which is not built.

This is also why the earlier "do not build a trampoline" conclusion is *not*
contradicted. That analysis was about rendezvous **continuations**, where the
argument still holds: there is one continuation, it has locality worth keeping, and
parking it is pure loss. Spawns have the opposite economics.

## Result

`bench/Diag`, medians of 7 reps:

| | ns/op |
|---|---|
| `Bjo.Spawn` unbatched | 278 |
| `Bjo.Spawn` batched | **37** |

End-to-end, `bench/Bench` vs Go, medians of 3:

| Benchmark | before | after | Go | vs Go |
|---|---|---|---|---|
| **Spawn storm** | 321 | **92** | 201 | **2.2x faster** |
| Ping-pong | 236 | 206 | 162 | 1.3x slower |
| Ring | 128 | 102 | 90 | 1.1x slower |
| Select/Choose | 214 | 197 | 134 | 1.5x slower |
| Fan-out | 113 ms | 118 ms | 120 ms | equal |

Fan-out is unchanged (11.3x speedup on 12 physical cores), confirming the split
preserves parallelism. Cost: +12 B/op (80 → 92) for the batch arrays and split items,
and GC pause *drops* (12 ms → 0.4 ms) because fibers no longer pile up as an 80 MB
live backlog waiting to be dequeued one at a time.

## SimpleChannel: the like-for-like comparison against a Go `chan`

`Channel<T>` is a composable CML event — it pays for `choose`, `withNack` and the
`SyncState` protocol. A Go `chan` has no equivalent capability, so those rows flatter
Go. `SimpleChannel` is the honest comparison: plain unbuffered point-to-point
rendezvous, no composition.

| Benchmark | `Channel<T>` | `SimpleChannel` | Go `chan` |
|---|---|---|---|
| Ping-pong | 206 | **178** | 162 |
| Ring | 102 | 140 | 90 |
| Spawn+send | 630 | **491** | 1778 |

There is deliberately no `SimpleChannel` row for Select/Choose: a `SimpleChannel`
cannot be an argument to `choose`. That is exactly the capability `Channel<T>` is
charging ~60 ns for.

`Ring simple` is the noisy one (69–144 ms across runs) and is *not* reliably faster
than the CML ring; the ring is a single hot chain where the CML path's inline
dispatch already avoids most scheduler traffic.

## Hopac comparison

`bench/hopac` is the same six benchmarks written against Hopac 0.5.1, shaped
identically. Hopac is the reference point that matters most: it is also CML, also on
.NET, also on a work-stealing scheduler, and it has had years of tuning. Go tells us
what a mature runtime with language support achieves; **Hopac tells us what the same
idea costs on the same platform**, which is what BjoML should actually be judged
against.

Medians of 3, ns/op, with bytes allocated per operation:

| Benchmark | BjoML | B/op | Hopac | B/op | Go |
|---|---|---|---|---|---|
| Spawn storm (from a foreign thread) | **110** | 89 | 2163 | 72 | — |
| Spawn storm (from inside the runtime) | **100** | 89 | 212 | 184 | 201 |
| Spawn+send | **590** | 377 | 1076 | 304 | 1778 |
| Ping-pong | 193 | **0** | 191 | 672 | 162 |
| Ring | **62** | **0** | 76 | 296 | 90 |
| Select/Choose | 211 | 40 | **141** | 528 | 134 |
| Fan-out | 113 ms | — | **112 ms** | — | 120 ms |

### A fairness bug in the earlier suite, now fixed

The spawn storm had been measuring different things in different languages. In Go,
`main` *is* a goroutine, so `go func()` pushes onto a local run queue and never
touches a shared structure. In the C# and F# versions the producer was an ordinary
thread, so every spawn crossed a shared queue. Those are not the same measurement,
and Go's number was quietly getting the easy path.

`Spawn storm inside` is the comparable row, and adding it changed the conclusion.

### Hopac has a 10x cliff on foreign-thread spawns; BjoML does not

**2163 ns/op from a foreign thread against 212 from inside a job.** Hopac's `queue`
from outside the runtime takes the global work stack's `SpinlockTTAS`, once per
spawn, while 24 workers spin on the same lock trying to pull work out. Hopac's own
source comments on that spinlock predict this exactly: "on every change of owner this
spinlock implementation requires Omega(n) cache line transfers ... and is thus
inherently unscalable". It is fine in normal Hopac use, because Hopac code spawns
from inside jobs onto an unsynchronised worker-local stack.

BjoML has no such cliff — 110 foreign against 100 inside — and the reason is
`SpawnBatch`. This is the strongest evidence yet for keeping it. It had been
described in this document as a workaround for the .NET thread pool; it is better
understood as **the fix for a cliff that the mature comparable runtime still has**.
An embedded language runtime is called from foreign threads constantly (the entry
point, `Task` continuations, host callbacks), so this is not a benchmark artifact.

Independent confirmation: the `trial/own-workers` branch reproduced Hopac's cliff
almost exactly. Hand-written workers with a global queue measured 299 ns/op for the
same storm without batching, and 109 with it.

### Where Hopac is better — much less than the table above suggests

**The `Select/Choose` row is not trustworthy, and neither is any other single-shot
row.** The suites run each benchmark exactly once, which on .NET measures a
partially tiered-JIT'd method rather than steady-state code.

`bench/Diag --mode select` and `bench/hopac select` run the same benchmark in
isolation with repetitions. The warm-up curves, ns/op per repetition:

```
BjoML   176  187  190  112   72   91   92   93   93   93   94   93  100   93   94
Hopac   161   88   95   91   86   82   81   83   83   91   85   83   81   90   82
```

BjoML needs **four** repetitions to reach steady state; Hopac needs **one**. The
single-shot suite therefore penalises BjoML far more than Hopac, and the "141 vs 211"
in the table above is mostly that artifact.

Steady state, medians of 15 repetitions across 4 separate process invocations:

| | single-shot (suite) | steady state |
|---|---|---|
| BjoML | 211 | **94** (91, 95, 94, 95) |
| Hopac | 141 | **85** |

So Hopac's `Alt` is about **10% faster, not 33%**, and BjoML's `SyncState` is not the
weak point it appeared to be.

### Two attempts to explain the remaining 9 ns, both falsified

Reading the two implementations suggested BjoML pays more per rendezvous:
`SyncState.MarkSynchronized` takes `lock (this)` unconditionally — twice per
rendezvous, once for each side — to walk a nack list that is empty in this benchmark,
where Hopac's `Pick.SetNacks` is a null check because the Pick's `Claimed` state
doubles as the mutex. And `NextEventId()` is an `Interlocked.Increment` where Hopac
threads a plain `int` down the `TryAlt` chain.

Both were tested by deliberately breaking them and re-measuring:

| variant | median ns/op |
|---|---|
| baseline | 94 (91, 95, 94, 95) |
| `MarkSynchronized` lock removed entirely | 93 |
| `NextEventId` as a plain increment | 96 (97, 96, 96, 94) |

**Neither changes anything measurable.** An uncontended `Monitor` on a hot cache line
costs far less than the ~20 ns assumed, and one `Interlocked.Increment` on a
just-allocated object is close to free. The remaining ~9 ns is unattributed; it wants
a profiler, not more code reading.

(An intermediate run showed 113 and another 89. Both were noise — repeated invocations
put every variant in the 91–97 band. Single invocations cannot resolve differences of
this size, which is worth remembering before acting on any 5–10 ns "win".)

### The remaining ~9 ns found: it was the harness, and the sign flips

The profiler turned out to be unnecessary. The two select benchmarks were not
measuring the same thing on the two runtimes:

- Hopac's receiver is `run (selectReceiver choice rounds)` — a **job inside
  Hopac's scheduler**, its native execution path.
- BjoML's receiver was `async Task Receive(...)` looping `await Cml.SyncAsync(choose)`
  — the **ValueTask interop path**, explicitly documented in `CmlValueTaskSource` as
  "the interop path, not the fiber path".

So Hopac's number was its native path and BjoML's number was its foreign-caller
path. Per op, the interop receiver pays for a pooled `CmlValueTaskSource` rent and
return (two interlocked exchanges on shared pool state),
`ManualResetValueTaskSourceCore` set/reset, the `ValueTask` wrapper, and the Task
method builder's ExecutionContext handling on every resume. The native path — an
`async Fiber` awaiting the event through `EventAwaiter<T>` — has none of that.

`bench/Diag --mode select` now measures both receivers. Five interleaved
process invocations, medians of 9–15 warmed reps, same session (absolute numbers
are higher than the 94/85 recorded above; the machine was in a slower
frequency state, so read the columns against each other, not against history):

| session | Hopac | BjoML Task+`SyncAsync` | BjoML Fiber+`await` |
|---|---|---|---|
| 1 | 126 | 134 | **111** |
| 2 | 128 | 123 | **123** |
| 3 | 126 | 145 | **108** |
| 4 | 122 | 137 | **113** |
| 5 | 125 | 141 | **109** |

Two observations:

- **Measured like-for-like, BjoML's choose is ~10% faster than Hopac's Alt**, not
  10% slower. The entire residual gap — and a bit more — was the harness.
- **The interop path is bimodal, which explains this document's noise complaints.**
  Within a single Task+`SyncAsync` invocation reps flip between a ~104 mode and a
  ~140 mode and then stick (e.g. `104 104 240 104 131 134 138 134`), consistent
  with the receiver's continuation either landing inline on the sender's thread
  and staying there, or settling into a cross-thread handoff per op. The fiber
  receiver has no such modes; its spread within a run is a few ns.

The fiber receiver allocates 136 B/op against the interop path's 40 (an
`EventAwaiter<T>` is a class allocated per await, plus the per-sync `SyncState`),
and is faster anyway — consistent with the earlier finding that allocation is
~free and scheduling distribution is what matters.

`bench/Bench` now reports both rows (`Select/Choose` and `Select/Choose(F)`).
The interop row is still worth keeping: a hosted language embedded in ordinary
C# will cross that boundary, and ~15–25 ns plus scheduling bimodality is its
real price. But it is a statement about the interop boundary, not about
`SyncState` or the choose machinery.

### EventAwaiter pooling: 136 → 40 B/op on the fiber choose path

With the harness fixed, the fiber path's own per-await costs became worth
counting. One `await choose` was three allocations:

| allocation | size | avoidable? |
|---|---|---|
| `EventAwaiter<T>` (a class, one per await) | 32 B | yes — pool it |
| `Action<T>` from the `OnSync` method-group conversion | 64 B | yes — a pooled awaiter caches its delegate for life |
| `SyncState` | 40 B | **no** |

`SyncState` must stay fresh per sync: losing choose branches linger in channels
and are reclaimed lazily (the B7 design), so a recycled state back in `W` would
make a stale parked op look live again — ABA. Pooling it would need a
generation stamp packed into the state word and generation-aware CAS everywhere;
not worth it while allocation stays this cheap.

The awaiter, unlike the state, has a safe recycle point: `GetResult()`, which the
await contract calls exactly once, after completion. Stale losing-branch ops keep
references to the cached `OnSync` delegate but can never *invoke* it — every
resume site in `Channel<T>` captures `ResumeGet`/`ResumePut` only after
`curr.TrySync()` succeeds (a CAS on that op's own `SyncState`, and `S` is
terminal), and `Promise.Waiter.Signal` routes through `TryCommit`, which fails on
a synchronized state. So a dead reference only pins the pooled object, which is
what a pool wants pinned. Same `[ThreadStatic]` freelist pattern as
`GetOp`/`PutOp`; field resets happen at recycle, before the rent-side publish,
because a parked op's other side can fire `OnSync` from another thread the
instant it is parked.

Result, interleaved with Hopac in the same session (medians of 9 warmed reps,
3 process invocations each; Hopac column doubles as the machine-state control):

| | Hopac | BjoML Fiber+`await` | B/op |
|---|---|---|---|
| before pooling | 122–128 | 108–123 | 136 |
| after pooling | 115–117 | **97–104** | **40** |

Normalised against the Hopac control the time win is a few ns/op — consistent
with "allocation is ~free" — but the garbage drops 3.4x, and the fiber choose
now sits ~14% under Hopac's Alt with only the un-poolable `SyncState` left
per op. All 47 tests pass; the choose/nack/promise suites exercise exactly the
stale-branch paths the pooling argument depends on.

### Can the JIT be told to optimise sooner? Yes, and it makes things worse

The obvious response to a four-repetition warm-up is to force the optimising JIT
earlier, either per method with
`[MethodImpl(MethodImplOptions.AggressiveOptimization)]` or globally with
`DOTNET_TieredCompilation=0`. Both were measured on `--mode select`, 12 repetitions:

| configuration | rep 1 | steady state |
|---|---|---|
| default | 175 | **96–98** |
| `TieredCompilation=0` | **95** | 118–124 |
| `TieredPGO=0` | 174 | 110–111 |
| `QuickJitForLoops=0` | 163 | 91–99 |
| `AggressiveOptimization` on the hot channel methods | 172 | 93–95 |

Two conclusions, both against the idea.

**BjoML depends heavily on dynamic PGO — about 14%.** Turning it off costs 97 → 111
ns/op. That is unsurprising in hindsight: the hot path is nothing but indirect calls —
`IEvent<T>.Publish`, `Action<T>` continuations, `IThreadPoolWorkItem.Execute`,
`IAsyncStateMachine.MoveNext` — and guarded devirtualisation from profile data is
exactly what removes them. Disabling tiered compilation disables PGO with it, which is
why it is 22% slower at steady state despite starting fast.

Hopac behaves the same way (87 → 102 with `TieredPGO=0`, a 17% loss), so this is a
property of CML-style runtimes on .NET rather than of BjoML specifically.

**`AggressiveOptimization` did nothing measurable**, neither to warm-up nor to steady
state, when applied to `PublishSend`, `PublishReceive`, `TryDirectSend` and
`TryDirectReceive`. The hot path is spread across compiler-generated async state
machines and generic instantiations that cannot be annotated, so annotating four
hand-written methods covers too little of it to matter. It would also opt those
methods out of PGO, which the table above shows is the expensive thing to lose.

So: **do not** mark BjoML for aggressive optimisation and do not disable tiering. For
benchmarking, use repetitions. If startup latency ever matters for the hosted
language, ReadyToRun is the right tool — it starts above tier-0 quality *and* still
tiers up with PGO — but that is untested here.

A useful side effect: the BjoML/Hopac gap is stable across every configuration
(10 ns default, 18 ns untiered, 9 ns without PGO), which confirms it is a real
difference in the implementations and not a tiering artifact.

### Consequence for the rest of this document

Every cross-runtime row above is single-shot and therefore carries the same warm-up
bias, in BjoML's disfavour. `Ring` at 62 vs 76 and `Ping-pong` at 193 vs 191 should be
re-measured with repetitions before being relied on. The spawn-storm conclusion is the
exception: a 10x foreign/inside cliff is far too large to be a JIT artifact.

### Where BjoML is better

**Allocation, by an order of magnitude.** Ping-pong 0 B/op against 672, ring 0 against
296, select 40 against 528. Hopac's `job` computation expression allocates
continuation objects per bind; BjoML's compiled async state machine is allocated once
per fiber and then reused across every suspension. This is the structural advantage
of building on `IAsyncStateMachine`, and it shows up as lower GC pressure rather than
lower latency — the two runtimes are within noise of each other on ping-pong wall
time despite the 672:0 allocation ratio.

**Spawning, roughly 2x**: 100 vs 212 inside, and 20x foreign.

**Ring, 62 vs 76**, with zero allocation.

### Caveats

- Hopac's `Spawn+send` is as noisy as Go's (695, 3334, 1076 across three runs) and
  for the same reason: 200 000 senders convoying on one unbuffered channel. Treat
  that row as non-evidence for all three runtimes.
- Hopac 0.5.1 predates .NET 10 and is running on a runtime it was never tuned for.
- Only the C# suite reports `SimpleChannel`; there is no Hopac equivalent because
  Hopac's `Ch` is always a composable `Alt`.

## What is left on the table

- **~30 ns of BjoML overhead per spawn** (216 vs 185 raw pool, pre-batching).
  ~~`FiberCore<T>` is 80 B and carries four fields (`_runner`, `_spawnBody`,
  `_spawnState`, `_spawnInherited`) that are dead the moment the fiber starts.~~
  Partially addressed: `_spawnState` is gone — stateful spawns store their
  state TYPED AND INLINE in `StatefulFiberCore<TState, T>`, which also removed
  the 32 B box that every value-tuple state paid (the idiomatic shape for a
  static spawn lambda). Spawn storm: 89 → 81 B/op; Spawn+send: −56 B/op
  together with the receiver fix below. The three remaining dead fields
  (24 B) would need a pooled spawn-request object, ~150 lines; by the timer
  wheel's precedent (rejected at 144 B/op for the same complexity), that
  fails the bar. The Spawn+send row also now uses a FIBER receiver — the old
  async-Task receiver paid a fresh `ChannelReceiveEvent` + `SyncState` per
  receive (64 B/op of interop cost booked against a spawn benchmark), and
  Go's `main` is a goroutine, so the fiber receiver is the like-for-like
  shape. Row total: 377 → 289 B/op, at allocation parity with Hopac
  (264-304). What remains is the floor for "a suspended fiber blocked on a
  channel with a joinable handle": the state-machine box and its MoveNext
  delegate (~144 B, the price of the async protocol), the promise handle
  (~80 B), and the parked PutOp (~48 B) — the same role Go fills with a 2 KB
  goroutine stack plus a sudog.
- **Ping-pong and Ring** are handoff-latency bound, not spawn bound. Batching does
  nothing for them; they are already within 1.1–1.3x of Go.
- ~~**Select/Choose at 1.5x** is the widest remaining gap and is a `SyncState`
  question (40 B/op allocated per sync), not a scheduler one.~~ **Resolved:** the
  gap was the benchmark, not the operator. The receiver was running on the
  ValueTask interop path while Hopac's ran native; measured fiber-to-job, BjoML's
  choose is ~10% *faster* than Hopac's Alt. See "The remaining ~9 ns found" above.
  What remains is the cost of the interop boundary itself (`Cml.SyncAsync` +
  `CmlValueTaskSource`), which is ~15–25 ns/op and scheduling-bimodal.

---

# The varied suite: hunting dark spots

The main suite is all low-contention, narrow-select, no-timer shapes: one hot
chain at a time, two-branch receive-only chooses, and nothing that ever arms a
timer or fires a nack. `bench/Bench varied`, `bench/hopac varied` and
`bench/go -suite varied` are eight benchmarks aimed at exactly those blind
spots, shaped identically across the three runtimes.

Medians of warmed reps (`--reps 5`, first rep discarded for the .NET suites),
ns/op, BjoML B/op in parentheses:

| Benchmark | shape | BjoML | Hopac | Go |
|---|---|---|---|---|
| Fan-in | 100 senders, 1 receiver, 1 channel | **47** (0) | 383 | 189 |
| Many-to-many | 100 senders, 100 receivers, 1 channel | 405 (0) | 1394 | **274** |
| Wide choose (8) | choose over 8, round-robin sender | 210 (40) | **185** | 397 |
| Skewed choose (8) | choose over 8, one channel ever fires | 290 (40) | **142** | 400 |
| Choose send (2) | choose between two sends | **165** (128) | 169 | 162 |
| Choose+timeout | choose(recv, timeout 1s), recv wins | **~130** (40) | 268 | 456 |
| Pipeline (4) | 4 stages, 500k items in flight | **310** (0) | 891 | 746 |
| Par ping-pong (12) | 12 independent hot chains | **26** (0) | 97 | 37 |

Five of eight rows BjoML wins outright, three of them by 2x or more, with zero
allocation. The other three are the dark spots this suite existed to find.

### Dark spot 1, fixed: wide-choose sweep bursts overflowed the op freelists

Skewed choose initially measured **310 B/op** — versus 40 for wide choose — and
was *slower* than wide choose despite doing less. The mechanism: a K-wide choose
that keeps losing parks K−1 dead ops per sync, and the dead channels all hit
their `NotePark` sweep threshold on the SAME iteration, because they park in
lockstep. Up to (K−1) × 32 recycled ops then arrive in one burst at a freelist
whose cap was 64: most of the burst was dropped, and the next 32 iterations
allocated fresh. Renting is steady, recycling is bursty, and the cap has to be
sized for the burst.

`GetOp`/`PutOp.MaxCached` is now 256 (16 KB per type per thread, a cache not a
leak): skewed choose drops to 40 B/op — the un-poolable `SyncState` only.
Raising it further to 512 measured identically, so 256 is not sitting on a
cliff edge.

What remains on this row is a **sticky scheduling bimodality**, not allocation:
a process settles into either a ~95 ns/op mode (sender and receiver serialized
inline on one thread) or a ~285 ns/op mode (cross-thread, every op paying cache
transfers on 8 channel locks), and stays there. Hopac's 142 sits between the
two modes. The same stickiness was seen on the interop select row. This wants
a look at thread placement someday; no code change here.

### Dark spot 2, half fixed: the armed-timer path in Choose+timeout

Choose+timeout is bimodal in a *structural* way. When the receive branch
commits inline during publish, `ChooseEvent` short-circuits and the timeout
branch is never published at all: ~130 ns/op, 40 B/op, and the timer is never
armed. That lazy branch publishing is why BjoML wins this row 2x against Hopac
and 3.5x against Go, both of which arm their timer every iteration.

But when the timeout branch DOES publish — which in a real server is the
common case, because the whole reason for a deadline is that the other side is
often not ready — the armed path originally cost **~1380 ns/op and 1360 B/op**.
Measuring the ingredient in isolation showed the `System.Threading.Timer`
arm+dispose is only ~98 ns and 144 B; the rest was the combinator sandwich
`Cml.Timeout` was built from: Guard → WithNack → a `Promise` → a second whole
`Cml.Sync` block just to LISTEN to the nack → two Wraps with a closure each.

`Cml.Timeout`/`Cml.At` are now direct events (`TimeoutEvent`/`AtEvent` +
`TimeoutNode`): one node, one cancel delegate, one leaf `NackNode`, one Timer
per armed sync. The node is deliberately NOT pooled — a nack action has no CAS
gate, so a pooled node's cancel delegate could be fired by a previous sync's
losing nack into the node's next life; a fresh node makes a late cancel hit a
closed gate instead. The combinator formulation is retained as
`Cml.TimeoutViaCombinators`, the executable specification, and both run
against the same tests (plus a fire-vs-cancel race regression test,
`CancelledTimeoutStaysQuiet`).

`bench/Diag --mode timeout` drives the armed path deterministically
(publish, then let a foreign branch win): medians of 7,

| | ns/op | B/op |
|---|---|---|
| combinator spec | 1066 | 1264 |
| `TimeoutEvent` | **782** | **368** |

Allocation fell 3.4x; time only 27%. The remaining ~700 ns is not composition
and not allocation: it is `TimerQueue` lock traffic, because the arm happens
on the syncing thread while the nack-driven dispose runs on a pool thread
(nacks are enqueued), so every op crosses a timer-queue partition twice. The
same-thread, uncontended figure for the identical Timer object is 98 ns.

The same harness also runs the full choose end to end with the timer armed on
EVERY iteration (the sender waits until the receiver's op is parked before
sending, and the timeout branch is published first). That is the steady-state
shape of a real select-with-deadline loop, and the number to put against
Hopac and Go, both of which arm every iteration by construction. Same session,
medians:

| armed every op | ns/op | B/op |
|---|---|---|
| BjoML, combinator spec | 1170 | 1360 |
| BjoML, `TimeoutEvent` | 922 | 464 |
| Hopac (`timeOutMillis`, timer wheel) | **241** | 448 |
| Go (`time.After`) | 475 | — |
| BjoML, suite row (receive ready, timer skipped) | **139** | 40 |

So after the fix: allocation is at parity with Hopac (464 vs 448), the 10x
cliff is now a 6.6x cliff against BjoML's own fast mode, and the armed path is
still 3.8x Hopac and 2x Go on wall time. What remains was ATTRIBUTED to the
`System.Threading.Timer` round trip through the partitioned global timer
queue. That attribution was a hypothesis, and building the timer wheel to test
it is what falsified it — see the next section.

### The timer wheel: built, measured, and rejected — the enqueued nack was the cost

A hashed timer wheel (`TimerWheel.cs`: 256 slots x 8 ms, CAS slot stacks,
absolute due-slots so a lagging ticker fires late-but-correct, a self-stopping
ticker using the SpawnBatch watchdog fence pattern, and cancellation as pure
gate-close with lazy drop) was built to eliminate the TimerQueue crossing.
It changed nothing:

| armed micro (arm+lose+cancel) | ns/op | B/op |
|---|---|---|
| `System.Threading.Timer` | 666 | 384 |
| timer wheel | 638 | 240 |

A wash on time. So the ~650 ns was never the timer mechanism — it was
something COMMON to both paths, and the remaining suspect was the nack
delivery: `MarkSynchronized` enqueued every nack as a pool work item, and a
cross-thread pool item costs ~185 ns before it wakes anyone (see the
queue-cost table above), plus the wake, plus the cache traffic of running the
cancel on a different core than the arm.

Nack actions are already REQUIRED to never run user code (the FiberContext
limitation note), and the built-in ones close a gate or `TrySetResult` a nack
promise. So `MarkSynchronized` now fires them inline on the committing thread,
wrapped in a try/catch so a throwing nack cannot unwind into a channel
matching loop. That one change, with 52 tests passing:

| armed end-to-end choose(timeout, recv) | ns/op | B/op |
|---|---|---|
| combinator spec, enqueued nacks (the original) | 1170 | 1360 |
| `TimeoutEvent` + enqueued nacks (Timer) | 791 | 480 |
| `TimeoutEvent` + INLINE nacks, Timer | **317** | 448 |
| `TimeoutEvent` + INLINE nacks, wheel | 402 | 304 |
| Hopac (same session) | 257 | 448 |
| Go | 475 | — |

Two verdicts, both from measurement:

- **The inline nack is the fix.** One guarded call replaces a pool hop per
  losing withNack branch, and the armed timeout path lands at 317 ns/op —
  1.2x from Hopac's timer wheel, comfortably ahead of Go, and 3.7x better
  than where this section started. It also speeds up every OTHER nack user
  (the combinator spec dropped 1170 → 970 without being touched).
- **The wheel is not worth its code, and was deleted.** With the real cost
  removed, the wheel measured ~400 ns/op with 2-3x the run-to-run variance of
  the plain Timer (its shared ticker batches fires into 8 ms pulses), and its
  one reliable win was the 144 B Timer object. Against ~150 lines of fenced
  stop/re-arm handshake, CAS slot stacks and catch-up logic, that is a bad
  trade, so it is not in the tree — this section is its record. For a future
  re-measurement, the shape that was tested: 256 slots x 8 ms; nodes carry an
  ABSOLUTE due slot so a lagging ticker fires late-but-correct with catch-up
  capped at one revolution; arm is one CAS push onto a lock-free slot stack;
  cancel is the gate-close the node already does, with the drain dropping
  closed nodes when it next passes their slot (so cancelled nodes are held at
  most ~2 s, not until deadline); one self-stopping ticker Timer for the
  whole process, using the SpawnBatch watchdog's fenced stop/re-arm
  handshake. It slots in behind `TimeoutNode.Arm` without touching anything
  else.

The methodological note worth keeping: the wheel was worth BUILDING precisely
because it isolated the timer mechanism as a variable. It answered "is it the
timer?" with a clean no, which is what pointed at the enqueue. This is the
same lesson as the MarkSynchronized-lock and NextEventId experiments earlier
in this document — attribution by deletion beats attribution by reading.

### Dark spot 3, accepted: many-to-many convoys on the channel Monitor

100 senders and 100 receivers on ONE channel is 1.5x slower than Go (405 vs
274). Every operation serialises on the same `Monitor`, and .NET's Monitor
under 200-thread contention loses to Go's futex-based runtime mutex plus its
sudog handoff. BjoML is still 3.4x faster than Hopac on the same row, and the
0 B/op shows the op pooling holds up under contention. A sharded or lock-free
waiter structure could close this, but it would complicate the matching loops
that every other row depends on; not worth it for a topology whose fix at the
application level is "use more than one channel".

### Minor: Choose send allocates 128 B/op in adapter closures

`ChannelSendOperation<T>.Publish` (and `ChannelSendEvent<T>`) allocate
`() => onSync(Unit.Value)` per publish to adapt `Action<Unit>` to the
`Action resumePut` that `PublishSend` takes — two closures per 2-branch choose
op, ~88 B, plus the SyncState. BjoML wins the row anyway; threading
`Action<Unit>` through `PublishSend`/`PutOp` would remove it if it ever
matters.

### What the wins say

Fan-in (47 vs Go's 189), pipeline (310 vs 746) and parallel ping-pong (26 vs
37) are all the same story: inline dispatch turns a rendezvous into a direct
call on the matching thread, and the op/awaiter pooling means steady-state
zero allocation. These are the shapes a CML runtime lives in, and there is no
dark spot in them.

## Three "easy" optimizations, priced one at a time

Each implemented separately with a probe benchmark run between steps, kept or
rejected on its own numbers.

### 1. The promise wake path: 160 → 40 B/op

`bench/Diag --mode promise` (1M complete→wake cycles) as the probe:

| step | bare action | fiber-shaped wake |
|---|---|---|
| baseline | 259 ns / 160 B | 259 ns / 160 B |
| waiters are their own work items | 238 / 64 | 238 / 64 |
| continuations stored unwrapped | 231 / 72 | **206 / 40** |

Two changes. First, `Complete` used to wake each waiter with
`Scheduler.Enqueue(w.Signal)` — a method-group conversion allocating a 64 B
delegate per wake, plus the pooled `ActionWorkItem` behind `Enqueue(Action)`,
whose thread-static free list starves under producer/consumer drift. Waiters
are heap objects already, so they now implement `IThreadPoolWorkItem` (with
ActionWorkItem's full obligations: fresh inline budget, catch-all, end-of-item
flush) and are enqueued directly.

Second, `OnCompleted(Action)` stored its continuation wrapped in an
`ActionWaiter`. The waiter slot now holds the `Action` itself, and the wake
path has one more save: a parked FIBER's resume delegate targets its
state-machine box, which is a work item whose `Execute` is equivalent to
invoking the delegate. That equivalence is asserted by the `IFiberResume`
marker — and only that marker; any object can implement `IThreadPoolWorkItem`
with unrelated semantics, so the target sniff must not be widened. Result: a
fiber blocked on a promise wakes with zero allocation beyond the promise
itself. Bare actions (ToTask, Detach — the rare shape) pay +8 B for the
returned `ActionWorkItem`; accepted.

### 2. Choose-send adapter closures: 128 → 40 B/op

`ChannelSendEvent`/`ChannelSendOperation` adapted their `Action<Unit>` to
`PublishSend`'s bare `Action` with a fresh closure per publish per branch —
88 B/op on the choose-send row, paid even by losing branches. `PublishSend`
now takes `Action<Unit>` and `PutOp` carries it in a typed `ResumeGive` field
beside the direct path's `ResumePut`; the receive-side match sites dispatch by
the discriminator they already had (choose givers have a `SyncState`, direct
givers never do). Choose send (2): 128 → 40 B/op, ~113 → ~93 ns/op. Ring, the
direct-path control, unchanged.

### 3. GC tuning: measured, and it went the WRONG way — do not ship it

The obvious remaining knob was gen0 size: this document's own pre-batching
measurement had GC at ~9%. Post-batching, same session, medians:

| | Diag spawn row (trivial work) | Spawn storm (fibers execute) |
|---|---|---|
| default ServerGC | 47 ns, 23 gen0, 5.9 ms pause | **103-108 ns** |
| `DOTNET_GCgen0size=0x20000000` | **37 ns, 0 gen0, 0 pause** | 122-134 ns |
| workstation GC | 41 ns, 9 gen0, 17 ms pause | — |

The micro says +21%, the real benchmark says **−20%, consistently, across
alternated runs**. The difference is the working set: with a 512 MB nursery
nothing is recycled during the run, so every allocation lands on cold pages —
page faults and TLB misses instead of the cache-hot memory that frequent,
cheap gen0 collections recycle. The trivial-work micro is bump-allocation
bound and never touches its allocations again, which is exactly the shape
real fibers are not.

So the recommendation for the hosted language is: ServerGC on (already the
default here), and NO gen0 tuning. The pre-batching 9% belonged to the 80 MB
live backlog that batching eliminated. This is the third falsified-by-harness
hypothesis in this document; the micro that "confirms" a tuning is not the
workload.

## Choice randomization: free, and off by default

Publish order is priority: when two branches are ready at publish time, the
first published wins, every time. Go randomizes `select` because production
systems starve otherwise; BjoML's deterministic left-to-right is a real
semantic that a hosted language may well prefer — but it should be a choice,
not an accident.

`Cml.RandomizeChoice` (default false) makes every choose publish in a random
rotation: a coin flip for the pair form, a rotation for the triple and array
forms. Rotation rather than a full Fisher-Yates shuffle, deliberately — a
shuffle needs a scratch array per sync, a rotation is one modulo, and rotation
already delivers the guarantee that matters (every branch reaches first
position with probability 1/n, so no fixed branch can be starved). Pairwise
order bias between adjacent branches remains; Go does eliminate that, at the
price of a per-select shuffle. Note this is branch-order randomization only:
parked ops within one channel are matched FIFO regardless.

The cost, on the fiber select row (`bench/Diag --mode select`), same process:

| | median ns/op |
|---|---|
| Fiber+await | 117 |
| Fiber+await, randomized | 113 |

Indistinguishable from noise — a thread-static xorshift and a register swap do
not show up next to a channel Monitor. The flag stays off by default because
deterministic priority is the more useful semantic for a language runtime
(and it is now pinned by a test, `ChooseIsDeterministicByDefault`); turning it
on is free when starvation-freedom is the requirement instead.

