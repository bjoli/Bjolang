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
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Bjoml;

/// <summary>
/// Run queues of our own, in Go's shape, worked by .NET pool threads.
///
/// The default. With <c>BJO_SCHEDULER=pool</c> in the environment,
/// <see cref="Scheduler"/> hands work straight to the .NET pool instead, as it
/// did before this existed: kept for a while as a way back, and to compare.
///
/// # Why
///
/// The pool's per-thread queues are LIFO for their owner, and a thread looks
/// elsewhere only once its own queue is empty. Under saturation no queue
/// empties, so an item pushed early sits under everything pushed after it. On
/// `bench/service` on four cores that is a shard's continuation buried under
/// client work while sixty clients wait on the shard: p99.9 of 40 ms, against
/// 4.4 ms with every item on the pool's global FIFO queue, which in turn cost a
/// third of the throughput. The pool has no setting for the order.
///
/// # The shape
///
/// <see cref="Worker"/>s are Go's Ps: one per processor, each with a FIFO run
/// queue of 256 items that its owner takes from the head and others steal half
/// of, and a <c>runnext</c> slot for the item woken most recently, so that a
/// fiber woken by a rendezvous runs next and on the same core. A worker takes
/// from <c>runnext</c> at most <see cref="NextBudget"/> times in a row before it
/// goes to its queue, so two fibers waking each other cannot starve it. Work
/// from outside any worker — a timer, an I/O completion, the main thread — goes
/// to one global FIFO queue, which a worker looks at first every 61st item, as
/// Go's does, so that it is not starved by local work either.
///
/// FIFO queues are half of it. The other half is that a resume run inline is
/// the newest work first, and one work item could go on resuming fibers inline
/// for as long as they kept waking each other. With these queues, each item may
/// run <see cref="Scheduler.WorkerInlineBudget"/> resumes inline in all, and
/// past that a resume goes to <c>runnext</c>, so the queue gets its turn.
///
/// A <see cref="Pump"/> is a pool work item that binds a worker to the thread
/// it runs on and runs items until there are none, then gives the worker and
/// the thread back. The pool keeps managing threads: if an item blocks its
/// thread, the pool notices and adds threads as it always has.
///
/// # Waking, and the spinner cap
///
/// A push wakes another pump only when a worker is idle and no pump is already
/// spinning (looking for work), and at most half the busy workers spin at once:
/// Go's rules, which keep a single chain from waking every core. The lost
/// wake-up between a pump giving up and a push deciding nobody needs waking is
/// closed as Go closes it. A push to the global queue is an interlocked store
/// and then reads the counts; a pump giving up publishes its worker idle and
/// stops spinning, both interlocked, and then looks at every queue once more.
/// One of the two always sees the other. A push to a worker's own queue needs
/// no wake-up for correctness: that worker is running and will get to it.
///
/// # A blocked worker
///
/// An item that blocks its thread holds its worker, and with it whatever is in
/// that worker's queue. <see cref="Monitor"/> looks every
/// <see cref="MonitorPeriodMs"/> ms: a bound worker that has run nothing since
/// the last look but has work queued gets a pump woken to steal it, and if no
/// worker is idle to bind that pump to, one more worker is made, up to four per
/// processor.
/// </summary>
internal static class Workers
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("BJO_SCHEDULER") != "pool";

    private const int NextBudget = 16;
    private const int GlobalEvery = 61;
    private const int MonitorPeriodMs = 10;
    /// Rounds of looking for work before a pump gives its worker back. Ten
    /// rounds, or no cap on spinners, measured no better on `bench/service`.
    private const int SpinRounds = 3;

    /// About 3 µs (the runtime normalises a spin to roughly 35 ns): how long a
    /// thief waits before taking a running worker's runnext. Go waits 3 µs.
    private const int RunnextGraceSpins = 100;

    private static readonly int s_procs = Math.Max(1, Environment.ProcessorCount);
    private static readonly Worker[] s_workers = CreateWorkers(s_procs * 4);
    private static readonly ConcurrentQueue<IThreadPoolWorkItem> s_global = new();

    /// Workers that may be bound: the first <c>s_limit</c> of <see cref="s_workers"/>.
    private static int s_limit = s_procs;

    /// Workers below the limit that no pump holds.
    private static int s_idle = s_procs;

    /// Pumps holding a worker and looking for work.
    private static int s_spinning;

    private static readonly Pump s_spinningPump = new(spinning: true);
    private static readonly Pump s_plainPump = new(spinning: false);

    private static Timer? s_monitor;
    private static int s_monitorStarted;

    [ThreadStatic] private static Worker? t_worker;

    private static Worker[] CreateWorkers(int n)
    {
        var all = new Worker[n];
        for (int i = 0; i < n; i++) all[i] = new Worker(i);
        return all;
    }

    // ---- pushing -------------------------------------------------------------

    /// <summary>
    /// Queue <paramref name="item"/>. On a worker's thread it becomes that
    /// worker's <c>runnext</c>, and what was there goes to the tail of its
    /// queue; anywhere else it goes to the global queue.
    /// </summary>
    internal static void Push(IThreadPoolWorkItem item, bool spawn = false)
    {
        var w = t_worker;
        if (w is not null)
        {
            // An exchange, not a read and a write: a thief may take what is in
            // the slot at any moment, and whichever of us gets it runs it.
            var old = Interlocked.Exchange(ref w.Next, item);
            if (old is not null) w.PushTail(old);

            // A resume that only hands this chain's next step to runnext stays
            // on this core, and wakes nobody to come and take it: a single
            // chain would otherwise be carried from core to core. An item
            // pushed back into the queue is work for another core, and so is a
            // spawn, which is new work the spawner may not get to for a while:
            // Go's newproc wakes a spinner for the same reason.
            if (old is not null || spawn) Wake();
            return;
        }

        StartMonitor();
        s_global.Enqueue(item);
        Wake();
    }

    /// <summary>
    /// Start a spinning pump if a worker is idle and nobody is looking for work
    /// already. The spinning count is taken here, before the pump exists, so
    /// that a burst of pushes starts one pump rather than one each.
    /// </summary>
    private static void Wake()
    {
        if (Volatile.Read(ref s_idle) <= 0) return;
        if (Volatile.Read(ref s_spinning) != 0) return;
        if (Interlocked.CompareExchange(ref s_spinning, 1, 0) != 0) return;
        ThreadPool.UnsafeQueueUserWorkItem(s_spinningPump, preferLocal: false);
    }

    // ---- the pump ------------------------------------------------------------

    private sealed class Pump : IThreadPoolWorkItem
    {
        private readonly bool _spinning;

        public Pump(bool spinning) => _spinning = spinning;

        public void Execute() => Run(_spinning);
    }

    private static void Run(bool spinning)
    {
        var w = AcquireIdle();
        if (w is null)
        {
            if (spinning) Interlocked.Decrement(ref s_spinning);
            return;
        }

        t_worker = w;
        try
        {
            while (true)
            {
                var item = FindRunnable(w);
                if (item is null)
                {
                    if (!spinning) spinning = TryStartSpinning();
                    if (spinning) item = Spin(w);
                }

                if (item is not null)
                {
                    if (spinning)
                    {
                        // Found work: stop counting as a spinner, and if that
                        // left nobody looking, wake one, so that a burst finds
                        // its way to every idle worker one pump at a time.
                        spinning = false;
                        if (Interlocked.Decrement(ref s_spinning) == 0) Wake();
                    }

                    Volatile.Write(ref w.Ticks, w.Ticks + 1);
                    RunItem(item);
                    continue;
                }

                // Nothing anywhere. Give the worker back, stop spinning, and
                // look once more: a push that saw this pump still spinning, or
                // this worker still busy, did not wake anybody.
                t_worker = null;
                Release(w);
                if (spinning)
                {
                    spinning = false;
                    Interlocked.Decrement(ref s_spinning);
                }

                if (!AnyWork()) return;

                w = AcquireIdle();
                if (w is null) return;
                t_worker = w;
            }
        }
        finally
        {
            t_worker = null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RunItem(IThreadPoolWorkItem item)
    {
        Scheduler.InlineBudget = Scheduler.WorkerInlineBudget;
        try { item.Execute(); }
        catch (Exception ex) { Scheduler.ReportUnhandled(ex); }
    }

    /// <summary>
    /// The next item for <paramref name="w"/>: the global queue first every
    /// 61st time, then <c>runnext</c> within its budget, the worker's own
    /// queue, the global queue, and half of somebody else's queue.
    /// </summary>
    private static IThreadPoolWorkItem? FindRunnable(Worker w)
    {
        if (++w.SchedTick % GlobalEvery == 0 && s_global.TryDequeue(out var fair))
        {
            w.NextRuns = 0;
            return fair;
        }

        var next = w.Next;
        if (next is not null)
        {
            if (w.NextRuns < NextBudget && Interlocked.CompareExchange(ref w.Next, null, next) == next)
            {
                w.NextRuns++;
                return next;
            }

            // Over budget: to the back of the queue, behind what has waited.
            if (Interlocked.CompareExchange(ref w.Next, null, next) == next) w.PushTail(next);
        }

        w.NextRuns = 0;
        return w.PopHead() ?? (s_global.TryDequeue(out var g) ? g : null) ?? Steal(w);
    }

    /// <summary>
    /// Half of somebody's queue; failing that, somebody's <c>runnext</c>.
    ///
    /// A running worker's <c>runnext</c> is what it is about to run, hot in its
    /// cache, so it is taken only from a worker that is idle, or that has run
    /// nothing for a moment — stuck in a long item, or blocked. The moment is
    /// waited once for every candidate together, after the queues came up
    /// empty, as Go does: waiting per worker made a failed search over 24
    /// workers cost tens of microseconds of spinning.
    /// </summary>
    private static IThreadPoolWorkItem? Steal(Worker me)
    {
        var all = s_workers;
        int n = Volatile.Read(ref s_limit);
        int start = me.Index + 1;
        for (int k = 0; k < all.Length; k++)
        {
            var v = all[(start + k) % all.Length];
            if (ReferenceEquals(v, me)) continue;
            if (v.Index >= n && v.IsEmpty) continue;
            var got = me.StealHalf(v);
            if (got is not null) return got;
        }

        Span<long> seen = stackalloc long[all.Length];
        bool candidates = false;
        for (int i = 0; i < all.Length; i++)
        {
            var v = all[i];
            if (ReferenceEquals(v, me) || Volatile.Read(ref v.Next) is null) continue;
            seen[i] = Volatile.Read(ref v.Ticks);
            candidates = true;
        }
        if (!candidates) return null;

        Thread.SpinWait(RunnextGraceSpins);

        for (int k = 0; k < all.Length; k++)
        {
            int i = (start + k) % all.Length;
            var v = all[i];
            if (ReferenceEquals(v, me)) continue;
            var next = Volatile.Read(ref v.Next);
            if (next is null) continue;
            if (Volatile.Read(ref v.Owned) == 1 && Volatile.Read(ref v.Ticks) != seen[i]) continue;
            if (Interlocked.CompareExchange(ref v.Next, null, next) == next) return next;
        }
        return null;
    }

    /// Go's cap: a pump may spin only while spinners are fewer than half the
    /// busy workers.
    private static bool TryStartSpinning()
    {
        int spinning = Volatile.Read(ref s_spinning);
        int busy = Volatile.Read(ref s_limit) - Volatile.Read(ref s_idle);
        if (2 * spinning >= busy) return false;
        return Interlocked.CompareExchange(ref s_spinning, spinning + 1, spinning) == spinning;
    }

    private static IThreadPoolWorkItem? Spin(Worker w)
    {
        for (int round = 0; round < SpinRounds; round++)
        {
            Thread.SpinWait(50 << Math.Min(round, 2));
            var item = FindRunnable(w);
            if (item is not null) return item;
        }
        return null;
    }

    private static bool AnyWork()
    {
        if (!s_global.IsEmpty) return true;
        foreach (var w in s_workers)
            if (!w.IsEmpty) return true;
        return false;
    }

    private static Worker? AcquireIdle()
    {
        int n = Volatile.Read(ref s_limit);
        var all = s_workers;
        for (int i = 0; i < n; i++)
        {
            var w = all[i];
            if (Volatile.Read(ref w.Owned) == 0 && Interlocked.CompareExchange(ref w.Owned, 1, 0) == 0)
            {
                Interlocked.Decrement(ref s_idle);
                return w;
            }
        }
        return null;
    }

    private static void Release(Worker w)
    {
        Volatile.Write(ref w.Owned, 0);
        if (w.Index < Volatile.Read(ref s_limit)) Interlocked.Increment(ref s_idle);
    }

    // ---- the monitor ---------------------------------------------------------

    private static void StartMonitor()
    {
        if (Volatile.Read(ref s_monitorStarted) != 0) return;
        if (Interlocked.Exchange(ref s_monitorStarted, 1) != 0) return;
        s_monitor = new Timer(static _ => Monitor(), null, MonitorPeriodMs, MonitorPeriodMs);
    }

    /// <summary>
    /// Find workers held by a thread that has run nothing since the last look
    /// while work waits in their queues, and wake a pump to steal it, making
    /// one more worker to bind it to when every worker is held.
    /// </summary>
    private static void Monitor()
    {
        bool stuckWithWork = false;
        int n = Volatile.Read(ref s_limit);
        var all = s_workers;
        for (int i = 0; i < all.Length; i++)
        {
            var w = all[i];
            long ticks = Volatile.Read(ref w.Ticks);
            bool stalled = ticks == w.SeenTicks && Volatile.Read(ref w.Owned) == 1;
            w.SeenTicks = ticks;
            if (stalled && !w.IsEmpty) stuckWithWork = true;
        }

        if (!stuckWithWork && s_global.IsEmpty) return;

        if (Volatile.Read(ref s_idle) <= 0)
        {
            if (!stuckWithWork) return;
            if (n >= all.Length) return;
            if (Interlocked.CompareExchange(ref s_limit, n + 1, n) != n) return;
            Interlocked.Increment(ref s_idle);
        }

        ThreadPool.UnsafeQueueUserWorkItem(s_plainPump, preferLocal: false);
    }

    // ---- a worker's queue ----------------------------------------------------

    /// <summary>
    /// One P: a ring of <see cref="Size"/> items with Go's protocol. The owner
    /// writes slots at the tail and publishes the tail with a release store;
    /// the owner and thieves both take from the head with a compare-exchange,
    /// having read the slot first. A slot between head and tail is never
    /// written, so a slot read before a successful exchange was the right one.
    /// </summary>
    internal sealed class Worker
    {
        internal const int Size = 256;
        private const int Mask = Size - 1;

        internal readonly int Index;
        private readonly IThreadPoolWorkItem?[] _ring = new IThreadPoolWorkItem?[Size];
        private int _head;
        private int _tail;

        internal IThreadPoolWorkItem? Next;
        internal int NextRuns;
        internal int SchedTick;
        internal int Owned;
        internal long Ticks;
        internal long SeenTicks;

        internal Worker(int index) => Index = index;

        internal bool IsEmpty =>
            Volatile.Read(ref _head) == Volatile.Read(ref _tail) && Volatile.Read(ref Next) is null;

        /// Owner only.
        internal void PushTail(IThreadPoolWorkItem item)
        {
            while (true)
            {
                int h = Volatile.Read(ref _head);
                int t = _tail;
                if (t - h < Size)
                {
                    _ring[t & Mask] = item;
                    Volatile.Write(ref _tail, t + 1);
                    return;
                }

                // Full: half of it to the global queue, oldest first, then retry.
                int n = (t - h) / 2;
                var moved = new IThreadPoolWorkItem[n];
                for (int i = 0; i < n; i++) moved[i] = _ring[(h + i) & Mask]!;
                if (Interlocked.CompareExchange(ref _head, h + n, h) != h) continue;
                foreach (var m in moved) s_global.Enqueue(m);
            }
        }

        /// Owner only: the oldest item in the queue.
        internal IThreadPoolWorkItem? PopHead()
        {
            while (true)
            {
                int h = Volatile.Read(ref _head);
                int t = Volatile.Read(ref _tail);
                if (t == h) return null;
                var item = _ring[h & Mask];
                if (Interlocked.CompareExchange(ref _head, h + 1, h) == h) return item;
            }
        }

        /// <summary>
        /// Take half of <paramref name="victim"/>'s queue, rounded up, into this
        /// worker's empty queue, and answer the first of it. Called by this
        /// worker's owner. A victim's <c>runnext</c> is taken in
        /// <see cref="Steal"/>, not here.
        /// </summary>
        internal IThreadPoolWorkItem? StealHalf(Worker victim)
        {
            while (true)
            {
                int h = Volatile.Read(ref victim._head);
                int t = Volatile.Read(ref victim._tail);
                int n = t - h;
                if (n <= 0) return null;
                if (n > Size) continue;   // torn read of head and tail; look again

                n -= n / 2;
                int mt = _tail;
                for (int i = 0; i < n; i++) _ring[(mt + i) & Mask] = victim._ring[(h + i) & Mask];
                if (Interlocked.CompareExchange(ref victim._head, h + n, h) != h) continue;

                var first = _ring[mt & Mask];
                if (n > 1)
                {
                    // The rest are this worker's queue now: it was empty, so its
                    // head is mt, and the first is handed back rather than queued.
                    Volatile.Write(ref _head, mt + 1);
                    Volatile.Write(ref _tail, mt + n);
                }
                return first;
            }
        }
    }
}
