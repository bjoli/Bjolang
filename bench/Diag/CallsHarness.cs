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

// Cost of a bjoroutine CALL, as opposed to a spawn.
//
// A call to an `async Fiber<T>` method allocates nothing until it suspends,
// and then one CalledFiber, which is its promise and its state-machine box.
// Until it suspends, its result is carried in the Fiber<T> itself. These rows
// price the shapes that scheme treats differently:
//
//   sync     the callee never suspends, so the core is the only allocation
//   susp     every level suspends, so every level pays core + box and a
//            scheduler hop on the way back up
//   handler  one bjoroutine calling eight helpers that complete inline and
//            one that suspends, the shape of a typical request handler
//   lift     Colour.Lift, a plain function seen as a bjoroutine
//
// "int", "pair" and "big" are the result type: pair is a 16 B struct holding a
// reference, the size of an Option or a 2-tuple, and big is a 32 B struct, the
// size of an Option of a Result. The rows differ in what is copied through
// Fiber<T>, the awaiter and the builder.
//
// Everything is driven from one fiber and awaited in turn, so a row measures
// latency per call chain, not throughput.

using System;
using System.Diagnostics;
using Bjolang.Runtime;
using Bjoml;

namespace Bjoml.Diag;

public static class CallsHarness
{
    record struct Pair(long A, object? B);

    record struct Big(long A, long B, long C, long D);

    record Row(string Name, int Ops, Func<int, Fiber<long>> Body);

    static readonly Func<int, Fiber<int>> s_lifted = Colour.Lift<int, int>(static x => x + 1);

    static Row[] AllRows() => new[]
    {
        new Row("sync    depth 1  int ", 2_000_000, n => SyncIntLoop(n, 1)),
        new Row("sync    depth 8  int ", 1_000_000, n => SyncIntLoop(n, 8)),
        new Row("sync    depth 1  pair", 2_000_000, n => SyncPairLoop(n, 1)),
        new Row("sync    depth 8  pair", 1_000_000, n => SyncPairLoop(n, 8)),
        new Row("sync    depth 1  big ", 2_000_000, n => SyncBigLoop(n, 1)),
        new Row("sync    depth 8  big ", 1_000_000, n => SyncBigLoop(n, 8)),
        new Row("lift             int ", 2_000_000, LiftLoop),
        new Row("susp    depth 1  int ",   200_000, n => SuspIntLoop(n, 1)),
        new Row("susp    depth 8  int ",   100_000, n => SuspIntLoop(n, 8)),
        new Row("susp    depth 8  big ",   100_000, n => SuspBigLoop(n, 8)),
        new Row("handler 8 sync+1 susp",   200_000, HandlerLoop),
    };

    public static void Run(int reps)
    {
        Console.WriteLine($".NET {Environment.Version}  ProcessorCount={Environment.ProcessorCount}  " +
                          $"ServerGC={System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine($"reps={reps}   (one call chain, awaited, per op)");
        Console.WriteLine();
        Console.WriteLine($"{"benchmark",-22} {"ns/op per rep",-40} {"median",7} {"B/op",6} {"gen0",5}");
        Console.WriteLine(new string('-', 84));

        foreach (var row in AllRows())
        {
            Once(row, Math.Max(1, row.Ops / 10), out _, out _);   // warm-up, discarded

            var samples = new double[reps];
            double alloc = 0; int g0 = 0;
            for (int r = 0; r < reps; r++) samples[r] = Once(row, row.Ops, out alloc, out g0);

            var sorted = (double[])samples.Clone();
            Array.Sort(sorted);

            string perRep = string.Join(" ", Array.ConvertAll(samples, s => $"{s,6:F0}"));
            Console.WriteLine($"{row.Name,-22} {perRep,-40} {sorted[reps / 2],7:F0} {alloc,6:F0} {g0,5}");
        }
        Console.WriteLine(new string('-', 84));
    }

    static long s_sink;

    static double Once(Row row, int ops, out double bytesPerOp, out int gen0)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        int g0 = GC.CollectionCount(0);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        s_sink += Bjo.RunToCompletion(() => row.Body(ops));

        sw.Stop();
        bytesPerOp = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)ops;
        gen0 = GC.CollectionCount(0) - g0;
        return sw.Elapsed.TotalMilliseconds * 1e6 / ops;
    }

    // ---------------------------------------------------------------------
    // Callees. `depth` counts bjoroutine calls in the chain, leaf included.
    // ---------------------------------------------------------------------

#pragma warning disable CS1998
    static async Fiber<int> SyncInt(int depth, int x)
    {
        if (depth == 1) return x + 1;
        return await SyncInt(depth - 1, x) + 1;
    }

    static async Fiber<Pair> SyncPair(int depth, long x)
    {
        if (depth == 1) return new Pair(x, s_pairRef);
        var p = await SyncPair(depth - 1, x);
        return p with { A = p.A + 1 };
    }

    static readonly object s_pairRef = new();

    static async Fiber<Big> SyncBig(int depth, long x)
    {
        if (depth == 1) return new Big(x, x, x, x);
        var b = await SyncBig(depth - 1, x);
        return b with { A = b.A + 1 };
    }
#pragma warning restore CS1998

    static async Fiber<int> SuspInt(int depth, Promise<int> p)
    {
        if (depth == 1) return await p + 1;
        return await SuspInt(depth - 1, p) + 1;
    }

    static async Fiber<Big> SuspBig(int depth, Promise<int> p)
    {
        if (depth == 1)
        {
            long x = await p;
            return new Big(x, x, x, x);
        }
        var b = await SuspBig(depth - 1, p);
        return b with { A = b.A + 1 };
    }

    static async Fiber<int> Handler(int x, Promise<int> p)
    {
        int a = x;
        for (int k = 0; k < 8; k++) a = await SyncInt(1, a);
        return a + await SuspInt(1, p);
    }

    // ---------------------------------------------------------------------
    // Drivers. A suspending row completes the promise after the chain has
    // parked on it, so the wake goes through the scheduler as in real use.
    // ---------------------------------------------------------------------

    static async Fiber<long> SyncIntLoop(int n, int depth)
    {
        long sum = 0;
        for (int i = 0; i < n; i++) sum += await SyncInt(depth, i);
        return sum;
    }

    static async Fiber<long> SyncPairLoop(int n, int depth)
    {
        long sum = 0;
        for (int i = 0; i < n; i++) sum += (await SyncPair(depth, i)).A;
        return sum;
    }

    static async Fiber<long> SyncBigLoop(int n, int depth)
    {
        long sum = 0;
        for (int i = 0; i < n; i++) sum += (await SyncBig(depth, i)).A;
        return sum;
    }

    static async Fiber<long> LiftLoop(int n)
    {
        long sum = 0;
        for (int i = 0; i < n; i++) sum += await s_lifted(i);
        return sum;
    }

    static async Fiber<long> SuspIntLoop(int n, int depth)
    {
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            var p = new Promise<int>();
            var f = SuspInt(depth, p);
            p.TrySetResult(i);
            sum += await f;
        }
        return sum;
    }

    static async Fiber<long> SuspBigLoop(int n, int depth)
    {
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            var p = new Promise<int>();
            var f = SuspBig(depth, p);
            p.TrySetResult(i);
            sum += (await f).A;
        }
        return sum;
    }

    static async Fiber<long> HandlerLoop(int n)
    {
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            var p = new Promise<int>();
            var f = Handler(i, p);
            p.TrySetResult(i);
            sum += await f;
        }
        return sum;
    }
}
