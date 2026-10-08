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

namespace Bjoml;

using System;
using System.Threading;
using System.Runtime.CompilerServices;

/// <summary>
/// Work dispatch for BjoML.
///
/// BjoML runs on .NET thread pool threads rather than on dedicated worker
/// threads, so that the pool keeps doing what it is good at: adding threads
/// when one blocks. The order work runs in is ours. Every enqueue goes to
/// <see cref="Workers"/>, Go-shaped run queues — FIFO per core, a
/// <c>runnext</c> slot, stealing, a spinner cap — worked by pumps on pool
/// threads; see there for why the pool's own LIFO queues were not enough.
///
/// With <c>BJO_SCHEDULER=pool</c> every enqueue goes to the pool instead,
/// through <see cref="ThreadPool.UnsafeQueueUserWorkItem(IThreadPoolWorkItem, bool)"/>
/// with <c>preferLocal: true</c>, and spawns are batched (see
/// <see cref="SpawnBatch"/>), as before the queues were ours. That way back is
/// kept for a while.
///
/// EXECUTION CONTEXT: nothing here ever captures or restores an
/// <see cref="ExecutionContext"/>. That is deliberate and load-bearing. The hosted
/// language carries its own dynamic environment (see <see cref="FiberContext"/>),
/// so flowing EC would be pure overhead *and* would reinstate ambient C# state on
/// top of the fiber's own. "Unsafe" in <c>UnsafeQueueUserWorkItem</c> means
/// precisely "do not flow EC", which is what we want on every path.
/// </summary>
public static class Scheduler
{
    /// <summary>
    /// Maximum number of nested inline dispatches before we bounce to the pool.
    ///
    /// This is the only thing standing between a long chain of rendezvous
    /// continuations and a stack overflow, since continuations run inline on the
    /// thread that completed the match. Treat it as load-bearing.
    /// </summary>
    public const int MaxInlineDepth = 50;

    [ThreadStatic]
    internal static int InlineDepth;

    /// <summary>
    /// With <see cref="Workers"/>: how many resumes one work item may run
    /// inline in all, nested or not. Past it a resume goes to the worker's
    /// <c>runnext</c>, and the worker's queue gets its turn.
    ///
    /// The depth limit alone bounds how deep a chain of inline resumes goes,
    /// not how long: each time the stack unwinds the chain can go on, and work
    /// that has waited in the queue waits on. Inline resumes are the newest
    /// work first; on four saturated cores they left a shard's continuation
    /// behind client work for tens of milliseconds. 32 kept the median at a
    /// microsecond and brought `bench/service`'s p99.9 there from about 40 to
    /// about 6 ms; 8 cost throughput and 128 lost most of the gain. On the
    /// pool, whose queues are LIFO, a budget would not help, and none applies.
    /// See BjolangRuntime/Cml/design.md.
    /// </summary>
    internal const int WorkerInlineBudget = 32;

    /// What is left of <see cref="WorkerInlineBudget"/> for the work item this
    /// thread is running. Refilled by <see cref="Workers"/> per item.
    [ThreadStatic]
    internal static int InlineBudget;

    /// <summary>
    /// Where exceptions escaping a scheduled work item go. Defaults to stderr.
    ///
    /// Unlike the old dedicated workers, an unhandled exception on a thread-pool
    /// thread tears down the process, so every work item body is wrapped.
    /// </summary>
    public static Action<Exception> UnhandledException { get; set; } =
        static ex => Console.Error.WriteLine($"BjoML unhandled exception: {ex}");

    /// <summary>
    /// Optional tuning hook, kept for source compatibility with the old
    /// dedicated-worker API. There are no workers to start any more; this only
    /// raises the pool's minimum thread count so a burst of fibers does not wait
    /// on the pool's thread-injection heuristic.
    /// </summary>
    public static void Start(int numWorkers = 0)
    {
        if (numWorkers <= 0) numWorkers = Environment.ProcessorCount;

        ThreadPool.GetMinThreads(out int workerThreads, out int completionPortThreads);
        if (workerThreads < numWorkers)
            ThreadPool.SetMinThreads(numWorkers, completionPortThreads);
    }

    // ---- enqueue -----------------------------------------------------------

    /// <summary>
    /// Queue a work item. Prefer this overload: a caller that can implement
    /// <see cref="IThreadPoolWorkItem"/> on an object it already owns (as
    /// <c>FiberStateMachineBox</c> does) enqueues with zero allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Enqueue(IThreadPoolWorkItem item)
    {
        if (Workers.Enabled) Workers.Push(item);
        else ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: true);
    }

    /// A fiber's resume delegate targets its state-machine box, which is a work
    /// item whose `Execute` is the resume (see <see cref="IFiberResume"/>), so
    /// that is queued as it stands, as `Promise.Wake` does, with nothing rented.
    public static void Enqueue(Action work) =>
        Enqueue(work.Target is IFiberResume box ? box : ActionWorkItem.Rent(work));

    public static void Enqueue<T>(Action<T> work, T state) => Enqueue(ActionWorkItem<T>.Rent(work, state));

    // ---- spawn -------------------------------------------------------------

    /// <summary>
    /// Set false to bypass <see cref="SpawnBatch"/> entirely and give every spawn
    /// its own queue operation, as before. Kept so the batching can be A/B'd
    /// against the old path without rebuilding.
    /// </summary>
    public static bool BatchSpawns { get; set; } = true;

    /// <summary>How a published batch is spread over worker threads.</summary>
    public enum SpawnBatchMode
    {
        /// <summary>
        /// Recursive halving: each batch hands half its range back to the pool until
        /// the range reaches <see cref="SpawnSplitFloor"/>. Cheap and stealable, but
        /// the parallelism it can reach is fixed by the floor before any thread runs.
        /// </summary>
        Tree,

        /// <summary>
        /// One shared range drained by an atomic cursor, with threads recruiting more
        /// threads while work remains. Parallelism is decided at run time by how many
        /// workers actually turn up, not by a constant.
        /// </summary>
        Adaptive,
    }

    /// <summary>
    /// Defaults to <see cref="SpawnBatchMode.Adaptive"/>, on measurement rather than
    /// taste. Sweeping N CPU-bound fibers (bench/Diag <c>--mode fanout</c>) against a
    /// serial reference, and 1e6 trivial fibers for throughput:
    ///
    /// <code>
    ///                 speedup at N=16   speedup at N=24   throughput   B/op
    ///   unbatched         19.8              19.5            289 ns      80
    ///   tree floor=8       4.3               6.3             34 ns      92
    ///   tree floor=2      11.9              14.2             33 ns     104
    ///   tree floor=1      17.5              20.5             35 ns     120
    ///   adaptive          17.3              24.8             30 ns      89
    /// </code>
    ///
    /// Every batched policy has the same throughput, so the fixed floor was buying
    /// nothing and costing up to 4.6x in parallelism. Adaptive is best on all four
    /// columns: it allocates no split items (it re-enqueues itself) and it is the
    /// only policy whose parallelism is not decided before any thread has run.
    /// </summary>
    public static SpawnBatchMode BatchMode { get; set; } = SpawnBatchMode.Adaptive;

    /// <summary>
    /// Smallest range <see cref="SpawnBatchMode.Tree"/> will still split. Only
    /// consulted in <see cref="SpawnBatchMode.Tree"/> mode, which is no longer the
    /// default; kept so the comparison in <see cref="BatchMode"/> stays reproducible.
    ///
    /// Beware: this constant decides how much parallelism a batch can reach BEFORE
    /// any thread has run. A batch of N fibers becomes ceil(N / floor) chunks and
    /// each chunk runs SEQUENTIALLY on one thread, so a floor of 8 caps a 16-fiber
    /// fan-out at two-way parallelism no matter how many cores are idle. That is
    /// precisely the bug that motivated the adaptive mode.
    /// </summary>
    public static int SpawnSplitFloor { get; set; } = 1;

    /// <summary>
    /// Queue a newly spawned fiber.
    ///
    /// Distinct from <see cref="Enqueue(IThreadPoolWorkItem)"/> on purpose: a spawn
    /// and a rendezvous continuation want opposite things. A continuation wants to
    /// run on the thread that just completed the match, for cache locality, and
    /// there is exactly one of it. A spawn is "go run this somewhere else", it has
    /// no locality to preserve, and spawns arrive in bursts — which is precisely
    /// the shape that can be batched. See <see cref="SpawnBatch"/> for the numbers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnqueueSpawn(IThreadPoolWorkItem item)
    {
        // A push onto our own queues costs no thread request, which is the
        // cost the batch amortises.
        if (Workers.Enabled) { Workers.Push(item, spawn: true); return; }
        if (!BatchSpawns) { ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: true); return; }
        SpawnBatch.Current.Add(item);
    }

    /// <summary>
    /// Called at every work-item boundary — i.e. every point where this thread is
    /// about to go back to the pool and could block or idle. Publishes anything the
    /// fiber spawned but that batching has not handed over yet.
    ///
    /// This is what keeps batched spawns from being stranded on a thread that stops
    /// making progress; see the hazard note on <see cref="SpawnBatch"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void OnWorkItemComplete()
    {
        if (!BatchSpawns) return;
        SpawnBatch.Current.FlushAndEndRun();
    }

    // ---- dispatch ----------------------------------------------------------

    /// <summary>
    /// Run a continuation now if the inline budget allows, otherwise queue it.
    ///
    /// Running inline is the fast path and the common case: the thread that
    /// completed a rendezvous simply keeps going into the continuation instead of
    /// paying a queue round trip.
    /// </summary>
    public static void Dispatch(Action action)
    {
        int depth = InlineDepth;
        if (depth < MaxInlineDepth && (!Workers.Enabled || --InlineBudget >= 0))
        {
            InlineDepth = depth + 1;
            try { action(); }
            finally { InlineDepth = depth; }
        }
        else
        {
            Enqueue(action);
        }
    }

    public static void Dispatch<T>(Action<T> action, T state)
    {
        int depth = InlineDepth;
        if (depth < MaxInlineDepth && (!Workers.Enabled || --InlineBudget >= 0))
        {
            InlineDepth = depth + 1;
            try { action(state); }
            finally { InlineDepth = depth; }
        }
        else
        {
            Enqueue(action, state);
        }
    }

    /// <summary>
    /// Report a failure nobody is going to look at.
    ///
    /// Public so that a hosted language can route a detached fiber's exception
    /// here rather than invoking <see cref="UnhandledException"/> directly —
    /// the property has no guard, and a handler that throws while reporting a
    /// failure would take the process down over a diagnostic.
    /// </summary>
    public static void ReportUnhandled(Exception ex)
    {
        try { UnhandledException(ex); }
        catch { /* a throwing handler must not take the process down */ }
    }
}

/// <summary>
/// Pooled <see cref="IThreadPoolWorkItem"/> wrapper for a bare <see cref="Action"/>.
///
/// The free list is <c>[ThreadStatic]</c> and therefore needs no synchronisation at
/// all. Items are rented on the producing thread and recycled on the consuming
/// thread, so a given thread's list drifts in length; that is harmless, it is only
/// a cache, and both ends fall back to allocation.
/// </summary>
internal sealed class ActionWorkItem : IThreadPoolWorkItem
{
    private const int MaxCached = 32;

    [ThreadStatic] private static ActionWorkItem? _free;
    // The free list's depth from this item down, while it is on the list: a
    // thread-static count would be a second __tls_get_addr per rent and return.
    private int _freeDepth;

    private ActionWorkItem? _next;
    private Action _action = null!;

    public static ActionWorkItem Rent(Action action)
    {
        var item = _free;
        if (item is null) return new ActionWorkItem { _action = action };

        _free = item._next;
        item._next = null;
        item._action = action;
        return item;
    }

    public void Execute()
    {
        var action = _action;

        // Clear and recycle BEFORE running the body: the body may not return
        // normally, and we must never touch our own fields once we are back on a
        // free list.
        _action = null!;
        var head = _free;
        int depth = head is null ? 0 : head._freeDepth;
        if (depth < MaxCached)
        {
            _next = head;
            _freeDepth = depth + 1;
            _free = this;
        }

        // A pool thread is reused across work items, and a previous item may have
        // unwound mid-dispatch, so the inline budget is reset per item.
        Scheduler.InlineDepth = 0;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Scheduler.ReportUnhandled(ex);
        }
        finally
        {
            Scheduler.OnWorkItemComplete();
        }
    }
}

/// <summary>
/// Pooled <see cref="IThreadPoolWorkItem"/> wrapper for an <see cref="Action{T}"/>
/// plus its state, so a continuation carrying a value needs no closure allocation.
/// </summary>
internal sealed class ActionWorkItem<T> : IThreadPoolWorkItem
{
    private const int MaxCached = 32;

    [ThreadStatic] private static ActionWorkItem<T>? _free;
    // The free list's depth from this item down, while it is on the list: a
    // thread-static count would be a second __tls_get_addr per rent and return.
    private int _freeDepth;

    private ActionWorkItem<T>? _next;
    private Action<T> _action = null!;
    private T _state = default!;

    public static ActionWorkItem<T> Rent(Action<T> action, T state)
    {
        var item = _free;
        if (item is null) return new ActionWorkItem<T> { _action = action, _state = state };

        _free = item._next;
        item._next = null;
        item._action = action;
        item._state = state;
        return item;
    }

    public void Execute()
    {
        var action = _action;
        var state = _state;

        _action = null!;
        _state = default!;   // drop the reference so a cached item never pins a value
        var head = _free;
        int depth = head is null ? 0 : head._freeDepth;
        if (depth < MaxCached)
        {
            _next = head;
            _freeDepth = depth + 1;
            _free = this;
        }

        Scheduler.InlineDepth = 0;
        try
        {
            action(state);
        }
        catch (Exception ex)
        {
            Scheduler.ReportUnhandled(ex);
        }
        finally
        {
            Scheduler.OnWorkItemComplete();
        }
    }
}
