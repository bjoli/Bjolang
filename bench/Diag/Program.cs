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

// Diagnostic harness for the BjoML spawn path.
//
// The question this answers: in `Bjo.Spawn`-heavy workloads, where does the time
// actually go? The two hypotheses are
//
//   (a) GC     — one FiberCore allocation per spawn, ~80 B, dies quickly;
//                1e6 spawns = ~80 MB of gen0 garbage.
//   (b) queue  — ThreadPool.UnsafeQueueUserWorkItem + the consumer side waking up
//                and stealing the item.
//
// The strategy is subtractive. Every row is the same 1e6-unit workload with one
// ingredient removed, so differences between rows attribute cost to ingredients.
//
// MEASUREMENT HAZARD, handled below: the completion signal is itself a contended
// atomic, and with 24 threads that can easily cost more than the thing under test.
// So there is exactly ONE shared atomic per unit, it sits alone on its own cache
// line, and rows 1-2 measure what that atomic costs on its own with no queue at
// all. Anything a queued row costs above row 2 is genuinely the queue.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Bjoml;

namespace Bjoml.Diag;

/// <summary>The one shared atomic, alone on its own 64-byte line.</summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
struct PaddedCounter
{
    [FieldOffset(64)] public int Remaining;
}

public static class Program
{
    const int N = 1_000_000;

    static PaddedCounter _c;
    static readonly ManualResetEventSlim _done = new(false);

    // Distinct-thread counter. Uses a generation stamp rather than a bool so it
    // actually resets between rows (a bool stays true on every pool thread after
    // the first row and reports 0 for everything after).
    [ThreadStatic] private static int _seenGen;
    static int _gen;
    static int _threadsSeen;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Work()
    {
        if (_seenGen != _gen) { _seenGen = _gen; Interlocked.Increment(ref _threadsSeen); }
        if (Interlocked.Decrement(ref _c.Remaining) == 0) _done.Set();
    }

    record Row(int Id, string Name, Func<long> Body);

    static Row[] AllRows() => new[]
    {
        new Row(1, "serial inline (1 thread)",   SerialInline),
        new Row(2, "Parallel.For chunked",       ParallelChunked),
        new Row(3, "pool prealloc  local=true",  () => PoolPrealloc(true)),
        new Row(4, "pool prealloc  local=false", () => PoolPrealloc(false)),
        new Row(5, "pool alloc     local=true",  () => PoolAlloc(true)),
        new Row(6, "pool alloc     local=false", () => PoolAlloc(false)),
        new Row(7, "pool batch=32  local=true",  () => PoolBatch(32, true)),
        new Row(8, "pool batch=128 local=true",  () => PoolBatch(128, true)),
        new Row(9,  "Bjo.Spawn UNbatched",       () => BjoSpawn(false)),
        new Row(10, "Bjo.Spawn batch floor=8",   () => BjoSpawn(true, floor: 8)),
        new Row(11, "Bjo.Spawn batch floor=2",   () => BjoSpawn(true, floor: 2)),
        new Row(12, "Bjo.Spawn batch floor=1",   () => BjoSpawn(true, floor: 1)),
        new Row(13, "Bjo.Spawn ADAPTIVE",        () => BjoSpawn(true, adaptive: true)),
    };

    public static void Main(string[] args)
    {
        Scheduler.Start();

        // --rows 5,7,9   --reps 5
        var rows = AllRows();
        int reps = 5;
        string mode = "throughput";
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--reps") reps = int.Parse(args[i + 1]);
            if (args[i] == "--mode") mode = args[i + 1];
            if (args[i] == "--rows")
            {
                var want = Array.ConvertAll(args[i + 1].Split(','), int.Parse);
                rows = Array.FindAll(rows, r => Array.IndexOf(want, r.Id) >= 0);
            }
        }

        if (mode == "fanout") { FanoutHarness.Run(reps); return; }
        if (mode == "select") { SelectHarness.Run(reps); return; }
        if (mode == "timeout") { TimeoutHarness.Run(reps); return; }
        if (mode == "promise") { PromiseHarness.Run(reps); return; }

        Console.WriteLine($".NET {Environment.Version}  ProcessorCount={Environment.ProcessorCount}  " +
                          $"ServerGC={System.Runtime.GCSettings.IsServerGC}  {System.Runtime.GCSettings.LatencyMode}");
        Console.WriteLine($"DOTNET_GCgen0size={Environment.GetEnvironmentVariable("DOTNET_GCgen0size") ?? "(default)"}  " +
                          $"reps={reps}  N={N}");
        Console.WriteLine();
        Console.WriteLine($"{"benchmark",-30} {"ns/op per rep",-34} {"median",7} {"B/op",6} {"gen0",5} {"GCpause",8} {"thr",4}");
        Console.WriteLine(new string('-', 104));

        foreach (var row in rows)
        {
            Measure(row.Body, out _);                       // warm-up, discarded
            var samples = new double[reps];
            double alloc = 0, pause = 0; int g0 = 0, thr = 0;

            for (int r = 0; r < reps; r++)
            {
                samples[r] = Measure(row.Body, out var s);
                alloc = s.Alloc; pause = s.Pause; g0 = s.Gen0; thr = s.Threads;
            }

            var sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            double median = sorted[reps / 2];

            string perRep = string.Join(" ", Array.ConvertAll(samples, s => $"{s,5:F0}"));
            Console.WriteLine($"{row.Id + " " + row.Name,-30} {perRep,-34} {median,7:F0} " +
                              $"{alloc,6:F0} {g0,5} {pause,8:F1} {thr,4}");
        }
        Console.WriteLine(new string('-', 104));
    }

    record struct Stats(double Alloc, int Gen0, double Pause, int Threads);

    // ---------------------------------------------------------------------

    /// <summary>Runs one repetition and returns ns/op.</summary>
    static double Measure(Func<long> body, out Stats stats)
    {
        _c.Remaining = N;
        _gen++;
        _threadsSeen = 0;
        _done.Reset();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        int g0 = GC.CollectionCount(0);
        TimeSpan pause0 = GC.GetTotalPauseDuration();
        long alloc0 = GC.GetTotalAllocatedBytes(precise: true);

        var sw = Stopwatch.StartNew();
        body();
        _done.Wait();
        sw.Stop();

        stats = new Stats(
            (GC.GetTotalAllocatedBytes(precise: true) - alloc0) / (double)N,
            GC.CollectionCount(0) - g0,
            (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds,
            _threadsSeen);

        return sw.Elapsed.TotalMilliseconds * 1e6 / N;
    }

    // ---------------------------------------------------------------------
    // Floors: no queue at all
    // ---------------------------------------------------------------------

    /// <summary>Uncontended floor: what one unit costs with no queue and no sharing.</summary>
    static long SerialInline()
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) Work();
        sw.Stop();
        return sw.ElapsedTicks;
    }

    /// <summary>
    /// Contended floor: the same units spread over all cores in big chunks, so the
    /// only shared cost is the completion atomic. Anything a queued row costs above
    /// this row is attributable to the queue rather than to the benchmark's own
    /// bookkeeping.
    /// </summary>
    static long ParallelChunked()
    {
        var sw = Stopwatch.StartNew();
        int chunks = Environment.ProcessorCount;
        int per = N / chunks;
        for (int t = 0; t < chunks; t++)
        {
            int start = t * per;
            int end = (t == chunks - 1) ? N : start + per;
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                for (int i = start; i < end; i++) Work();
            }, null);
        }
        sw.Stop();
        return sw.ElapsedTicks;
    }

    // ---------------------------------------------------------------------
    // Queue rows
    // ---------------------------------------------------------------------

    /// <summary>Queue cost with zero allocation inside the clock.</summary>
    static long PoolPrealloc(bool preferLocal)
    {
        var items = new PoolItem[N];
        for (int i = 0; i < N; i++) items[i] = new PoolItem();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) ThreadPool.UnsafeQueueUserWorkItem(items[i], preferLocal);
        sw.Stop();
        return sw.ElapsedTicks;
    }

    /// <summary>One work item allocated per unit, exactly as spawn does.</summary>
    static long PoolAlloc(bool preferLocal)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) ThreadPool.UnsafeQueueUserWorkItem(new PoolItem(), preferLocal);
        sw.Stop();
        return sw.ElapsedTicks;
    }

    /// <summary>
    /// Go's trick, transplanted: hand the pool BATCHES, not items.
    ///
    /// A batch is one work item holding K units. On execute it first re-enqueues its
    /// own second half as a fresh batch — so the work stays stealable and fans out
    /// as a tree instead of a single queue — then runs the first half inline.
    /// One queue operation per K units instead of per unit.
    /// </summary>
    static long PoolBatch(int k, bool preferLocal)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i += k)
            ThreadPool.UnsafeQueueUserWorkItem(new BatchItem(Math.Min(k, N - i)), preferLocal);
        sw.Stop();
        return sw.ElapsedTicks;
    }

    static long BjoSpawn(bool batched, int floor = 8, bool adaptive = false)
    {
        Scheduler.BatchSpawns = batched;
        Scheduler.SpawnSplitFloor = floor;
        Scheduler.BatchMode = adaptive
            ? Scheduler.SpawnBatchMode.Adaptive
            : Scheduler.SpawnBatchMode.Tree;
        Func<Fiber> body = () => Bump();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) Bjo.Spawn(body);
        sw.Stop();
        return sw.ElapsedTicks;
    }

#pragma warning disable CS1998
    static async Fiber Bump() => Work();
#pragma warning restore CS1998

    // ---------------------------------------------------------------------

    sealed class PoolItem : IThreadPoolWorkItem
    {
        public void Execute() => Work();
    }

    sealed class BatchItem : IThreadPoolWorkItem
    {
        private int _count;
        public BatchItem(int count) => _count = count;

        public void Execute()
        {
            int n = _count;
            while (n > 1)
            {
                int half = n / 2;
                // Split: the tail goes back to the pool so other threads can take it.
                ThreadPool.UnsafeQueueUserWorkItem(new BatchItem(n - half), preferLocal: true);
                n = half;
            }
            for (int i = 0; i < n; i++) Work();
        }
    }
}
