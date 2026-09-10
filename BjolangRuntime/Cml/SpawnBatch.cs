/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 *
 * As a special exception to the Mozilla Public License, version 2.0, if you
 * compile your application source code and portions of this software are
 * embedded into the generated object code or executable form as a normal
 * consequence of the compilation process (such as inline functions,
 * templates, generics, or macros), you may redistribute such embedded portions
 * in such object code or executable form without complying with the source code
 * availability requirements or notice obligations of Section 3 of the MPL 2.0.
 */

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Bjoml;

/// <summary>
/// Amortises the .NET thread pool's per-work-item cost across a burst of spawns.
///
/// WHY THIS EXISTS. Measurement (bench/Diag) on a 12C/24T Zen 4 box, 1e6 units:
///
///   Parallel.For chunked, no queue at all ............  11 ns/unit
///   one ThreadPool work item per unit ................ 185 ns/unit
///   one ThreadPool work item per 32 units ............  31 ns/unit
///   Bjo.Spawn (one work item per fiber) .............. 216 ns/unit
///   Bjo.Spawn with GC *entirely* disabled ............ 196 ns/unit
///
/// So a spawn costs ~216 ns of which ~20 ns is GC and ~185 ns is the queue. The
/// allocation is nearly free — a zero-allocation pre-allocated work item costs the
/// same 185 ns. What costs is the *number of queue operations*: 24 threads
/// contending on the pool's thread-request counter and stealing one item at a time.
/// Go is faster at spawning for exactly this reason: its run queues move work in
/// batches (half a queue per steal, 128 goroutines per global-queue spill) whereas
/// .NET's work-stealing queue moves one item per steal.
///
/// So we hand the pool BATCHES instead of items. One queue operation per
/// <see cref="Capacity"/> fibers instead of one per fiber.
///
/// THE HAZARD, AND WHY THE WATCHDOG IS NOT OPTIONAL. Parking fibers in a
/// thread-local buffer means they are not stealable, and — much worse — a thread
/// that spawns and then blocks on something *outside* BjoML (a Monitor, a
/// ManualResetEventSlim, Console.ReadLine) would strand them forever. That is a
/// deadlock, not a slowdown. Three things prevent it:
///
///   1. BURST DETECTION. The first <see cref="BurstThreshold"/> spawns in a run go
///      straight to the pool, so the overwhelmingly common "spawn one or two
///      children and await them" shape never touches the buffer at all and is
///      byte-for-byte the old behaviour.
///   2. FLUSH ON YIELD. Every work-item boundary flushes (see
///      <see cref="Scheduler.OnWorkItemComplete"/>), so a fiber that suspends or
///      returns publishes everything it queued.
///   3. WATCHDOG. Batches are registered globally and a 1 ms timer flushes any
///      that a thread is sitting on. This is the backstop that makes 1 and 2
///      optimisations rather than correctness requirements: even a thread that
///      spawns 5 fibers and then spins forever on a raw lock cannot strand them.
/// </summary>
internal sealed class SpawnBatch
{
    /// <summary>Fibers per queue operation. 64 puts us at the flat part of the curve.</summary>
    internal const int Capacity = 64;

    /// <summary>
    /// Spawns that go straight to the pool before batching engages. Keeps small
    /// fan-outs on exactly the old code path, so they cannot regress in latency.
    /// </summary>
    internal const int BurstThreshold = 8;

    private const int WatchdogPeriodMs = 1;

    [ThreadStatic] private static SpawnBatch? t_current;

    // Every batch ever created, so the watchdog can reach a buffer whose owning
    // thread has stopped cooperating. Pool threads are long-lived, so this list is
    // bounded by "threads that have ever spawned" in practice.
    private static readonly List<SpawnBatch> s_all = new();

    // Created stopped and never replaced. Creating it lazily inside EnsureWatchdog
    // meant two threads could race on the `??=` and build two timers, orphaning one.
    private static readonly Timer s_watchdog =
        new Timer(static _ => Sweep(), null, Timeout.Infinite, Timeout.Infinite);

    private static int s_watchdogState;   // 0 = stopped, 1 = running

    /// <summary>
    /// Guards <see cref="_items"/>/<see cref="_count"/> against the watchdog. The
    /// owner thread is the only writer in the normal case, so this CAS is
    /// uncontended and costs a few ns against the ~185 ns it saves.
    /// </summary>
    private int _gate;

    private IThreadPoolWorkItem[] _items = new IThreadPoolWorkItem[Capacity];
    private int _count;

    /// <summary>Consecutive spawns since the last yield point; drives burst detection.</summary>
    internal int Run;

    internal static SpawnBatch Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var b = t_current;
            return b ?? Create();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SpawnBatch Create()
    {
        var b = new SpawnBatch();
        t_current = b;
        lock (s_all) s_all.Add(b);
        return b;
    }

    // ---- hot path ----------------------------------------------------------

    /// <summary>
    /// Queue a freshly spawned fiber. Publishes immediately during the first
    /// <see cref="BurstThreshold"/> spawns of a run, then starts batching.
    /// </summary>
    internal void Add(IThreadPoolWorkItem item)
    {
        int run = Run;
        if (run < BurstThreshold)
        {
            Run = run + 1;
            ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: true);
            return;
        }

        Lock();
        bool wasEmpty = _count == 0;
        _items[_count++] = item;
        bool full = _count == Capacity;
        IThreadPoolWorkItem[]? arr = null;
        int n = 0;
        if (full) arr = Detach(out n);
        Unlock();

        if (arr != null) Publish(arr, n);

        // Arm only on the empty -> non-empty transition, not on every Add. The
        // watchdog only needs waking when a buffer STARTS holding work, and
        // EnsureWatchdog carries a full fence, so paying it once per batch instead
        // of once per spawn keeps it off the hot path.
        else if (wasEmpty) EnsureWatchdog();
    }

    /// <summary>
    /// Publish anything buffered and end the current run. Called at every work-item
    /// boundary, which is every point at which this thread might go idle or block.
    /// </summary>
    internal void FlushAndEndRun()
    {
        Run = 0;
        if (Volatile.Read(ref _count) == 0) return;

        Lock();
        var arr = Detach(out int n);
        Unlock();

        if (arr != null) Publish(arr, n);
    }

    // ---- internals ---------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Lock()
    {
        if (Interlocked.CompareExchange(ref _gate, 1, 0) == 0) return;
        LockSlow();
    }

    private void LockSlow()
    {
        var sw = new SpinWait();
        while (Interlocked.CompareExchange(ref _gate, 1, 0) != 0) sw.SpinOnce();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Unlock() => Volatile.Write(ref _gate, 0);

    /// <summary>
    /// Take ownership of the buffered items and install a fresh buffer. Caller must
    /// hold the gate. Handing the array over instead of copying keeps this at one
    /// array allocation per <see cref="Capacity"/> fibers (~8 B/fiber).
    /// </summary>
    private IThreadPoolWorkItem[]? Detach(out int n)
    {
        n = _count;
        if (n == 0) return null;
        var arr = _items;
        _items = new IThreadPoolWorkItem[Capacity];
        _count = 0;
        return arr;
    }

    private static void Publish(IThreadPoolWorkItem[] arr, int n)
    {
        IThreadPoolWorkItem batch = Scheduler.BatchMode == Scheduler.SpawnBatchMode.Adaptive
            ? new AdaptiveSpawnBatchItem(arr, n)
            : new SpawnBatchItem(arr, 0, n);
        ThreadPool.UnsafeQueueUserWorkItem(batch, preferLocal: true);
    }

    // ---- watchdog ----------------------------------------------------------

    /// <summary>
    /// Start the watchdog if it is not already ticking.
    ///
    /// The CAS is unconditional, with no <c>Volatile.Read</c> fast path in front of
    /// it, and that is load-bearing rather than sloppy. This is one half of a
    /// store/load pair against <see cref="Sweep"/>:
    ///
    /// <code>
    ///   Add:   store _count = 1   ...  load s_watchdogState
    ///   Sweep: store s_watchdogState = 0  ...  load _count
    /// </code>
    ///
    /// x86 permits StoreLoad reordering, and neither <c>Volatile.Write</c> (release)
    /// nor <c>Volatile.Read</c> (acquire) prevents it. With plain volatile accesses
    /// both sides can read stale values, each conclude "the other party will handle
    /// it", and drop the buffered fibers on the floor — permanently, if the owning
    /// thread never spawns again. A full fence is required on BOTH sides;
    /// <c>Interlocked.CompareExchange</c> provides it here and
    /// <c>Interlocked.Exchange</c> provides it in <see cref="Sweep"/>.
    /// </summary>
    private static void EnsureWatchdog()
    {
        if (Interlocked.CompareExchange(ref s_watchdogState, 1, 0) != 0) return;
        s_watchdog.Change(WatchdogPeriodMs, WatchdogPeriodMs);
    }

    private static void Sweep()
    {
        if (FlushAll()) return;   // still busy, keep ticking

        // Nothing outstanding: stop rather than burn a wakeup every millisecond
        // forever. Interlocked.Exchange, not Volatile.Write — see EnsureWatchdog for
        // why a release store is not enough here.
        s_watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        Interlocked.Exchange(ref s_watchdogState, 0);

        // Re-check AFTER publishing the stop. An Add that landed while we were
        // sweeping would have seen state == 1 and declined to arm the timer,
        // trusting us to catch its item — but we had already passed its batch. The
        // full fence above orders our stop before this re-scan, so any such Add is
        // now visible, and any Add that happens after it will see state == 0 and arm
        // the timer itself. One of the two paths always fires.
        if (FlushAll()) EnsureWatchdog();
    }

    /// <summary>Publish every non-empty batch. Returns true if anything was found.</summary>
    private static bool FlushAll()
    {
        SpawnBatch[] snapshot;
        lock (s_all) snapshot = s_all.ToArray();

        bool anyPending = false;
        foreach (var b in snapshot)
        {
            if (Volatile.Read(ref b._count) == 0) continue;

            b.Lock();
            var arr = b.Detach(out int n);
            b.Unlock();

            if (arr != null) { Publish(arr, n); anyPending = true; }
        }

        return anyPending;
    }
}

/// <summary>
/// A published batch of spawned fibers.
///
/// Executing it does NOT simply run all of them on this thread — that would trade
/// the queue cost for a loss of parallelism, which is bug B6 in a new hat. Instead
/// it repeatedly splits: half the range goes back to the pool as another batch (so
/// it is stealable and other threads can pick it up) and this thread keeps the
/// other half. The result is a tree fan-out — 64 fibers reach 24 cores in ~6 queue
/// operations instead of 64.
/// </summary>
internal sealed class SpawnBatchItem : IThreadPoolWorkItem
{
    private readonly IThreadPoolWorkItem[] _items;
    private readonly int _start;
    private readonly int _end;

    internal SpawnBatchItem(IThreadPoolWorkItem[] items, int start, int end)
    {
        _items = items;
        _start = start;
        _end = end;
    }

    public void Execute()
    {
        int start = _start, end = _end;
        int floor = Scheduler.SpawnSplitFloor;
        if (floor < 1) floor = 1;

        // Give the tail away first, so a stealing thread can start on it while we
        // are still working through the head.
        while (end - start > floor)
        {
            int mid = start + (end - start) / 2;
            ThreadPool.UnsafeQueueUserWorkItem(new SpawnBatchItem(_items, mid, end), preferLocal: true);
            end = mid;
        }

        var items = _items;
        for (int i = start; i < end; i++)
        {
            var item = items[i];
            items[i] = null!;   // do not pin a finished fiber for the rest of the batch

            // Each fiber gets a fresh inline budget; one deep rendezvous chain must
            // not push the next fiber in the batch closer to a stack overflow.
            Scheduler.InlineDepth = 0;
            try
            {
                item.Execute();
            }
            catch (Exception ex)
            {
                Scheduler.ReportUnhandled(ex);
            }
        }

        Scheduler.OnWorkItemComplete();
    }
}

/// <summary>
/// A published batch with NO fixed parallelism constant.
///
/// <see cref="SpawnBatchItem"/> decides how many chunks a batch becomes before any
/// thread has run, using <see cref="Scheduler.SpawnSplitFloor"/>. That is the wrong
/// shape of decision: how much parallelism is available is a run-time property (how
/// many workers are idle right now), not something a constant can know. A floor of 8
/// caps a 16-fiber fan-out at two-way parallelism on a 24-core box.
///
/// Here the batch is one shared range drained by an atomic cursor. Threads take
/// chunks until it is empty, so the achieved parallelism is simply however many
/// threads turned up — one thread drains the whole thing correctly, twenty-four
/// threads split it twenty-four ways, and nothing had to predict which.
///
/// Two details make it behave:
///
/// - RECRUITMENT is self-limiting. A thread that finds work remaining enqueues one
///   more reference to this same batch, so helpers arrive geometrically rather than
///   all at once, and they stop arriving the moment the cursor is exhausted. It is
///   capped at one helper per core so a batch cannot flood the pool.
/// - CHUNK SIZE shrinks toward the end (guided self-scheduling). Big bites while
///   there is plenty left keep the atomic off the hot path; small bites near the end
///   stop the tail from landing entirely on one thread, which is the very problem
///   the fixed floor has.
/// </summary>
internal sealed class AdaptiveSpawnBatchItem : IThreadPoolWorkItem
{
    /// <summary>Cap on a single bite, so one thread cannot swallow a whole batch.</summary>
    private const int MaxChunk = 16;

    private readonly IThreadPoolWorkItem[] _items;
    private readonly int _length;

    private int _cursor;    // next index to claim; only ever moved by Interlocked
    private int _helpers;   // extra copies of this batch handed to the pool

    internal AdaptiveSpawnBatchItem(IThreadPoolWorkItem[] items, int length)
    {
        _items = items;
        _length = length;
    }

    public void Execute()
    {
        int procs = Environment.ProcessorCount;

        try
        {
            while (true)
            {
                int claimed = Volatile.Read(ref _cursor);
                int remaining = _length - claimed;
                if (remaining <= 0) return;

                TryRecruit(remaining, procs);

                // Guided self-scheduling: remaining / (2 * workers), clamped.
                int chunk = remaining / (2 * procs);
                if (chunk < 1) chunk = 1;
                else if (chunk > MaxChunk) chunk = MaxChunk;

                int start = Interlocked.Add(ref _cursor, chunk) - chunk;
                if (start >= _length) return;
                int end = Math.Min(start + chunk, _length);

                RunRange(start, end);
            }
        }
        finally
        {
            Scheduler.OnWorkItemComplete();
        }
    }

    /// <summary>
    /// Ask for one more thread if there is enough left to be worth one. The CAS both
    /// counts helpers and serialises the decision, so N threads racing here produce
    /// N distinct helpers rather than N duplicates of the same one.
    /// </summary>
    private void TryRecruit(int remaining, int procs)
    {
        if (remaining <= 1) return;

        int helpers = Volatile.Read(ref _helpers);
        if (helpers >= procs - 1) return;

        if (Interlocked.CompareExchange(ref _helpers, helpers + 1, helpers) == helpers)
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
    }

    private void RunRange(int start, int end)
    {
        var items = _items;
        for (int i = start; i < end; i++)
        {
            var item = items[i];

            // Safe without synchronisation: the cursor hands out disjoint ranges, so
            // this slot belongs to this thread and to no other.
            items[i] = null!;

            Scheduler.InlineDepth = 0;
            try
            {
                item.Execute();
            }
            catch (Exception ex)
            {
                Scheduler.ReportUnhandled(ex);
            }
        }
    }
}
