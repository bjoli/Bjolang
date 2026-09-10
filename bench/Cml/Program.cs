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

// The four rows the merge is measured against, on the raw CML layer.
//
// Each has a twin in bench/bjolang/cmlbench.bjo written in Bjolang. Same counts,
// same topology, same work per unit, so the gap between the two tables is what
// the language costs on top of the runtime. Rows are reported as ns/op and B/op
// because allocation is the diagnostic that explains most of the gap.
//
// The scoped ring has no counterpart here: a scope is a Bjolang construct. This
// suite's ring is the reference the Bjolang scoped ring is compared against.

using System.Diagnostics;
using Bjoml;

namespace CmlBench;

public static class Program
{
    const int Reps = 5;

    public static void Main(string[] args)
    {
        Scheduler.Start();

        var only = args.Length > 0 ? args[0] : null;

        Console.WriteLine(
            $".NET {Environment.Version}, ProcessorCount={Environment.ProcessorCount}, " +
            $"ServerGC={System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine();
        Console.WriteLine($"{"benchmark",-22} {"ns/op",10} {"B/op",10}   (min of {Reps})");

        Warmup();

        if (only is null or "ring") Bench("Ring", 1_000_000, Ring);
        if (only is null or "spawn") Bench("Spawn burst", 1_000_000, SpawnBurst);
        if (only is null or "skewed") Bench("Skewed choose(8)", 1_000_000, SkewedChoose);
    }

    /// Tier-0 code in the first measured rep is the single largest source of
    /// noise here, so every path the suite uses is run once at a small count.
    static void Warmup()
    {
        RingCore(100, 100);
        SpawnBurstCore(50_000);
        SkewedChooseCore(50_000);
    }

    // ---- harness -----------------------------------------------------------

    /// Minimum of <see cref="Reps"/>, with the median printed beside it.
    ///
    /// The skewed-choose row on a multi-CCD part is bimodal: the sender and the
    /// receiver either land on the same chiplet or they do not, and the two modes
    /// are about 70 and 150 ns/op with nothing in between. A median over an odd
    /// number of reps therefore reports whichever mode won the coin toss, which
    /// makes two phases incomparable. The minimum is the machine at its least
    /// disturbed and is stable across runs, so it is the column phases are
    /// compared on; the median is printed so that a row whose two modes have
    /// genuinely moved apart is still visible.
    ///
    /// Allocation needs neither: it is deterministic, and every rep agrees.
    static void Bench(string name, int n, Func<(TimeSpan, long)> body)
    {
        var ns = new double[Reps];
        var bytes = new double[Reps];

        for (int r = 0; r < Reps; r++)
        {
            var (elapsed, alloc) = body();
            ns[r] = elapsed.TotalMilliseconds * 1e6 / n;
            bytes[r] = (double)alloc / n;
        }

        Array.Sort(ns);
        Array.Sort(bytes);
        Console.WriteLine(
            $"{name,-22} {ns[0],10:F1} {bytes[0],10:F1}   (median {ns[Reps / 2]:F1})");
    }

    static (TimeSpan, long) Measured(Action body)
    {
        // A settled heap before the mark, so the row reports what the benchmark
        // allocated rather than what the previous one left behind.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        body();
        sw.Stop();
        return (sw.Elapsed, GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    // ---- 1. Ring -----------------------------------------------------------
    //
    // 1000 nodes, 1000 trips: a million parked rendezvous. Every message finds
    // its channel empty, so this is the park-and-resume path and nothing else.

    static async Fiber RingNode(Channel<int> inCh, Channel<int> outCh, bool isLast, int trips)
    {
        while (true)
        {
            int msg = await inCh.Receive();

            if (msg == -1)
            {
                if (!isLast) await outCh.Send(-1);
                return;
            }

            if (isLast)
            {
                msg++;
                if (msg >= trips)
                {
                    await outCh.Send(-1);
                    continue;
                }
            }

            await outCh.Send(msg);
        }
    }

    static async Fiber SendOne(Channel<int> ch, int v) => await ch.Send(v);

    static (TimeSpan, long) Ring() => RingCore(1000, 1000);

    static (TimeSpan, long) RingCore(int workers, int trips)
    {
        var channels = new Channel<int>[workers];
        for (int i = 0; i < workers; i++) channels[i] = new Channel<int>();

        // Spawned outside the mark: 1000 spawns against 1,000,000 messages is
        // noise, and including them would mix the spawn row into this one.
        var handles = new Promise<Unit>[workers];
        for (int i = 0; i < workers; i++)
        {
            var inCh = channels[i];
            var outCh = channels[(i + 1) % workers];
            bool isLast = i == workers - 1;
            handles[i] = Bjo.Spawn(() => RingNode(inCh, outCh, isLast, trips));
        }

        return Measured(() =>
        {
            Bjo.Spawn(() => SendOne(channels[0], 0)).ToTask().GetAwaiter().GetResult();
            foreach (var h in handles) h.ToTask().GetAwaiter().GetResult();
        });
    }

    // ---- 2. Spawn burst ----------------------------------------------------
    //
    // A million fibers that do an atomic bump and land. No rendezvous, so the
    // row is the cost of starting and finishing a fiber.

    static long _counter;
    static int _remaining;
    static readonly ManualResetEventSlim AllDone = new(false);

#pragma warning disable CS1998
    static async Fiber Bump()
    {
        Interlocked.Increment(ref _counter);
        if (Interlocked.Decrement(ref _remaining) == 0) AllDone.Set();
    }
#pragma warning restore CS1998

    /// The producer loop runs inside a fiber, not on a foreign thread. A spawn
    /// from a fiber pushes onto the batch its own thread already owns; one from
    /// outside crosses a shared queue, which is a different measurement.
    static async Fiber BurstParent(int n, Func<Fiber> body)
    {
        for (int i = 0; i < n; i++) _ = Bjo.Spawn(body);
    }

    static (TimeSpan, long) SpawnBurst() => SpawnBurstCore(1_000_000);

    static (TimeSpan, long) SpawnBurstCore(int n)
    {
        _counter = 0;
        _remaining = n;
        AllDone.Reset();

        // One delegate for the whole burst, not one per spawn: the body captures
        // nothing, so hoisting it keeps the row about spawning.
        Func<Fiber> body = () => Bump();

        return Measured(() =>
        {
            _ = Bjo.Spawn(() => BurstParent(n, body));
            AllDone.Wait();
        });
    }

    // ---- 3. Skewed choose --------------------------------------------------
    //
    // One receiver choosing over 8 channels while only channel 0 ever fires. The
    // other 7 accumulate dead takers that only the amortised sweep reclaims, so
    // this is the bounded-dead-set design under sustained fire.

    const int WideK = 8;

    static async Fiber SkewedSender(Channel<int> ch, int rounds)
    {
        for (int i = 0; i < rounds; i++) await ch.Send(i);
    }

    static async Fiber ChooseReceiver(IEvent<int> choose, int rounds)
    {
        for (int i = 0; i < rounds; i++) await choose;
    }

    static (TimeSpan, long) SkewedChoose() => SkewedChooseCore(1_000_000);

    static (TimeSpan, long) SkewedChooseCore(int rounds)
    {
        var evs = new IEvent<int>[WideK];
        Channel<int>? first = null;
        for (int i = 0; i < WideK; i++)
        {
            var ch = new Channel<int>();
            first ??= ch;
            evs[i] = ch;
        }

        var choose = Cml.Choose(evs);
        var sender = Bjo.Spawn(() => SkewedSender(first!, rounds));

        return Measured(() =>
        {
            Bjo.Spawn(() => ChooseReceiver(choose, rounds)).ToTask().GetAwaiter().GetResult();
            sender.ToTask().GetAwaiter().GetResult();
        });
    }
}
