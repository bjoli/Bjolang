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

// BjoML counterpart to bench/go/main.go.
//
// Every benchmark is shaped identically to its Go twin: same counts, same channel
// topology, same work per unit. Allocation per operation is reported alongside time,
// because that is the diagnostic that actually explains a gap against goroutines.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bjoml;

namespace Bjoml.Bench;

public static class Program
{
    /// <summary>
    /// Run <paramref name="body"/> back-to-back so the reader can see the warm-up curve.
    ///
    /// Repetitions are CONSECUTIVE per benchmark rather than looping the whole suite,
    /// because what needs to reach steady state is this benchmark's own code path: its
    /// generic instantiations, its async state machines, and the tier-1 recompilation
    /// with PGO that only happens after enough invocations. Interleaving other
    /// benchmarks between reps would let those decay.
    /// </summary>
    static async Task Repeat(int reps, Func<Task> body)
    {
        for (int i = 0; i < reps; i++) await body();
    }

    public static async Task Main(string[] args)
    {
        Scheduler.Start();

        // --reps N. Default 1 keeps the historical single-shot behaviour, which every
        // number in BENCHMARKS.md was produced with.
        int reps = 1;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--reps") reps = int.Parse(args[i + 1]);

        // `varied` runs the dark-spot suite (see Varied.cs) instead of the main one.
        if (Array.IndexOf(args, "varied") >= 0)
        {
            await Varied.Run(reps);
            return;
        }

        Console.WriteLine($".NET {Environment.Version}, ProcessorCount={Environment.ProcessorCount}, " +
                          $"ServerGC={System.Runtime.GCSettings.IsServerGC}, reps={reps}");
        Console.WriteLine();

        // Warm up the JIT so the first benchmark is not paying for everything.
        await Warmup();

        await Repeat(reps, SpawnStorm);
        await Repeat(reps, SpawnStormInside);
        await Repeat(reps, SpawnAndSend);
        await Repeat(reps, PingPong);
        await Repeat(reps, Ring);
        await Repeat(reps, SelectChooseFiber);
        await Repeat(reps, FanOut);

        // SimpleChannel is the like-for-like comparison against a Go `chan`: both
        // are plain unbuffered point-to-point rendezvous with no composition. The
        // Channel<T> rows above are paying for `choose`/`withNack` machinery that
        // Go has no equivalent of, so they flatter Go.
        //
        // There is deliberately no SimpleChannel row for Select/Choose: a
        // SimpleChannel cannot be an argument to `choose`. That is exactly the
        // capability the CML channel is charging for.
        Console.WriteLine();
        Console.WriteLine("--- SimpleChannel (non-composable, like a Go chan) ---");
        await Repeat(reps, SpawnAndSendSimple);
        await Repeat(reps, PingPongSimple);
        await Repeat(reps, RingSimple);
    }

    static async Task Warmup()
    {
        for (int i = 0; i < 200; i++)
        {
            var ch = new Channel<int>();
            var p = Bjo.Spawn(() => Trivial(ch));
            await Bjo.Spawn(() => SendOne(ch, 1)).ToTask();
            await p.ToTask();
        }
    }

    static async Fiber Trivial(Channel<int> ch) => await ch.Receive();

    // ---------------------------------------------------------------------
    // 1. Spawn storm
    // ---------------------------------------------------------------------

    static long _counter;
    static int _remaining;
    static readonly ManualResetEventSlim _allDone = new(false);

    // Matches Go's `go func(){ atomic.AddInt64(...); wg.Done() }()` exactly: an
    // atomic bump and a completion signal, no rendezvous. An async method with no
    // await is legal and the compiler emits a synchronous fast path, which is the
    // honest equivalent of a goroutine that never blocks.
#pragma warning disable CS1998
    static async Fiber Bump()
    {
        Interlocked.Increment(ref _counter);
        if (Interlocked.Decrement(ref _remaining) == 0) _allDone.Set();
    }
#pragma warning restore CS1998

    static Task SpawnStorm()
    {
        const int n = 1_000_000;
        _counter = 0;
        _remaining = n;
        _allDone.Reset();

        // Hoisted so we allocate one delegate, not one per spawn — Go's closure is
        // likewise loop-invariant here because it captures nothing.
        Func<Fiber> body = () => Bump();

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < n; i++) Bjo.Spawn(body);
        _allDone.Wait();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;

        Report("Spawn storm", n, sw, alloc, $"counter={Interlocked.Read(ref _counter)}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The same storm with the producer loop running INSIDE a fiber.
    ///
    /// The original suite was not fair on this point. In Go, <c>main</c> is itself a
    /// goroutine, so <c>go func()</c> pushes onto a local run queue and never touches
    /// anything shared. Here the producer was an <c>async Task</c> — a foreign thread
    /// as far as the runtime is concerned — so every spawn crossed a shared queue.
    /// Those are different measurements, and this is the one comparable to Go's.
    /// </summary>
    static async Fiber StormParent(int n, Func<Fiber> body)
    {
        for (int i = 0; i < n; i++) Bjo.Spawn(body);
    }

    static Task SpawnStormInside()
    {
        const int n = 1_000_000;
        _counter = 0;
        _remaining = n;
        _allDone.Reset();

        Func<Fiber> body = () => Bump();

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        Bjo.Spawn(() => StormParent(n, body));
        _allDone.Wait();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;

        Report("Spawn storm inside", n, sw, alloc, $"counter={Interlocked.Read(ref _counter)}");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------
    // 2. Spawn + one channel send each
    // ---------------------------------------------------------------------

    static async Fiber SendOne(Channel<int> ch, int v) => await ch.Send(v);

    /// <summary>
    /// The receiver is a fiber, not an async Task. Same fairness point as the
    /// spawn storm: Go's `main` IS a goroutine, so its receive loop runs native.
    /// The old Task receiver paid the interop path per op — a fresh
    /// ChannelReceiveEvent (24 B) and a SyncState (40 B) per receive — which
    /// belongs to the interop boundary, not to spawn+rendezvous.
    /// </summary>
    static async Fiber ReceiveSum(Channel<int> ch, int n, long[] sink)
    {
        long total = 0;
        for (int i = 0; i < n; i++) total += await ch.Receive();
        sink[0] = total;
    }

    static async Task SpawnAndSend()
    {
        const int n = 200_000;
        var ch = new Channel<int>();
        var sink = new long[1];

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < n; i++)
        {
            int v = i;
            Bjo.Spawn(static s => SendOne(s.ch, s.v), (ch, v));
        }

        await Bjo.Spawn(() => ReceiveSum(ch, n, sink)).ToTask();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;

        Report("Spawn+send", n, sw, alloc, $"total={sink[0]}");
    }

    // ---------------------------------------------------------------------
    // 3. Ping-pong
    // ---------------------------------------------------------------------

    static async Fiber Ponger(Channel<int> a, Channel<int> b, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            int v = await a.Receive();
            await b.Send(v * 2);
        }
    }

    static async Fiber Pinger(Channel<int> a, Channel<int> b, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            await a.Send(i);
            await b.Receive();
        }
    }

    static async Task PingPong()
    {
        const int rounds = 1_000_000;
        var a = new Channel<int>();
        var b = new Channel<int>();

        var pong = Bjo.Spawn(() => Ponger(a, b, rounds));

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        var ping = Bjo.Spawn(() => Pinger(a, b, rounds));
        await pong.ToTask();
        await ping.ToTask();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;

        Report("Ping-pong", rounds, sw, alloc, "round trips");
    }

    // ---------------------------------------------------------------------
    // 4. Ring
    // ---------------------------------------------------------------------

    static async Fiber RingNode(Channel<int> inCh, Channel<int> outCh, bool isLast, int numTrips)
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
                if (msg >= numTrips)
                {
                    await outCh.Send(-1);
                    continue;
                }
            }

            await outCh.Send(msg);
        }
    }

    static async Task Ring()
    {
        const int numWorkers = 1000;
        const int numTrips = 1000;

        var channels = new Channel<int>[numWorkers];
        for (int i = 0; i < numWorkers; i++) channels[i] = new Channel<int>();

        var handles = new Promise<Unit>[numWorkers];
        for (int i = 0; i < numWorkers; i++)
        {
            var inCh = channels[i];
            var outCh = channels[(i + 1) % numWorkers];
            bool isLast = i == numWorkers - 1;
            handles[i] = Bjo.Spawn(() => RingNode(inCh, outCh, isLast, numTrips));
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        await Bjo.Spawn(() => SendOne(channels[0], 0)).ToTask();
        foreach (var h in handles) await h.ToTask();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;

        Report("Ring", numWorkers * numTrips, sw, alloc, "messages");
    }

    // ---------------------------------------------------------------------
    // 5. Select / Choose
    // ---------------------------------------------------------------------

    static async Fiber SelectSender(Channel<int> a, Channel<int> b, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            if (i % 2 == 0) await a.Send(i);
            else await b.Send(i);
        }
    }

    /// <summary>
    /// Select with the receiver as a native fiber awaiting the event.
    ///
    /// This is the row comparable to Hopac, whose receiver is a job INSIDE its
    /// scheduler. Measured (bench/Diag --mode select): fiber ~100 ns/op stable
    /// after EventAwaiter pooling, Hopac ~116 ns/op.
    /// </summary>
    static async Fiber SelectReceiverFiber(IEvent<int> choose, int rounds)
    {
        for (int i = 0; i < rounds; i++) await choose;
    }

    static async Task SelectChooseFiber()
    {
        const int rounds = 1_000_000;
        var a = new Channel<int>();
        var b = new Channel<int>();

        var sender = Bjo.Spawn(() => SelectSender(a, b, rounds));

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        var choose = Cml.Choose(a, b);
        await Bjo.Spawn(() => SelectReceiverFiber(choose, rounds)).ToTask();

        await sender.ToTask();
        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;

        Report("Select/Choose", rounds, sw, alloc, "ops");
    }

    // ---------------------------------------------------------------------
    // 6. Fan-out
    // ---------------------------------------------------------------------

    static async Fiber Burn(int iterations)
    {
        BurnCore(iterations);
        await Cml.Always(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void BurnCore(int iterations)
    {
        double acc = 0;
        for (int i = 1; i <= iterations; i++) acc += 1.0 / i;
        _sink = acc;
    }

    static double _sink;

    static async Fiber FanOutParent(int numChildren, int iterations, Channel<int> done)
    {
        for (int i = 0; i < numChildren; i++)
        {
            var child = Bjo.Spawn(() => Burn(iterations));
            Bjo.Spawn(() => Notify(child, done));
        }

        for (int i = 0; i < numChildren; i++) await done.Receive();
    }

    static async Fiber Notify(Promise<Unit> child, Channel<int> done)
    {
        await child;
        await done.Send(1);
    }

    static async Task FanOut()
    {
        const int numChildren = 480;
        const int iterations = 3_000_000;

        var serialSw = Stopwatch.StartNew();
        BurnCore(iterations);
        serialSw.Stop();
        double serialTotalMs = serialSw.Elapsed.TotalMilliseconds * numChildren;

        var done = new Channel<int>();
        var sw = Stopwatch.StartNew();
        await Bjo.Spawn(() => FanOutParent(numChildren, iterations, done)).ToTask();
        sw.Stop();

        Console.WriteLine(
            $"Fan-out:          {numChildren} children in {sw.ElapsedMilliseconds} ms " +
            $"(serial reference {serialTotalMs:F0} ms, speedup {serialTotalMs / sw.Elapsed.TotalMilliseconds:F1}x)");
    }

    // ---------------------------------------------------------------------

    // ---------------------------------------------------------------------
    // SimpleChannel counterparts
    // ---------------------------------------------------------------------

    static async Fiber SendOneSimple(SimpleChannel<int> ch, int v) => await ch.PutMessage(v);

    static async Task SpawnAndSendSimple()
    {
        const int n = 200_000;
        var ch = new SimpleChannel<int>();

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < n; i++)
        {
            int v = i;
            Bjo.Spawn(static s => SendOneSimple(s.ch, s.v), (ch, v));
        }

        long total = 0;
        for (int i = 0; i < n; i++) total += await ch.GetMessage();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;

        Report("Spawn+send simple", n, sw, alloc, $"total={total}");
    }

    static async Fiber PongerSimple(SimpleChannel<int> a, SimpleChannel<int> b, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            int v = await a.GetMessage();
            await b.PutMessage(v * 2);
        }
    }

    static async Fiber PingerSimple(SimpleChannel<int> a, SimpleChannel<int> b, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            await a.PutMessage(i);
            await b.GetMessage();
        }
    }

    static async Task PingPongSimple()
    {
        const int rounds = 1_000_000;
        var a = new SimpleChannel<int>();
        var b = new SimpleChannel<int>();

        var pong = Bjo.Spawn(() => PongerSimple(a, b, rounds));

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        var ping = Bjo.Spawn(() => PingerSimple(a, b, rounds));
        await pong.ToTask();
        await ping.ToTask();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;

        Report("Ping-pong simple", rounds, sw, alloc, "round trips");
    }

    static async Fiber RingNodeSimple(SimpleChannel<int> inCh, SimpleChannel<int> outCh, bool isLast, int numTrips)
    {
        while (true)
        {
            int msg = await inCh.GetMessage();

            if (msg == -1)
            {
                if (!isLast) await outCh.PutMessage(-1);
                return;
            }

            if (isLast)
            {
                msg++;
                if (msg >= numTrips)
                {
                    await outCh.PutMessage(-1);
                    continue;
                }
            }

            await outCh.PutMessage(msg);
        }
    }

    static async Task RingSimple()
    {
        const int numWorkers = 1000;
        const int numTrips = 1000;

        var channels = new SimpleChannel<int>[numWorkers];
        for (int i = 0; i < numWorkers; i++) channels[i] = new SimpleChannel<int>();

        var handles = new Promise<Unit>[numWorkers];
        for (int i = 0; i < numWorkers; i++)
        {
            var inCh = channels[i];
            var outCh = channels[(i + 1) % numWorkers];
            bool isLast = i == numWorkers - 1;
            handles[i] = Bjo.Spawn(() => RingNodeSimple(inCh, outCh, isLast, numTrips));
        }

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        await channels[0].PutMessage(0);
        foreach (var h in handles) await h.ToTask();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;

        Report("Ring simple", numWorkers * numTrips, sw, alloc, "messages");
    }

    // ---------------------------------------------------------------------

    static void Report(string name, int n, Stopwatch sw, long allocBytes, string extra)
    {
        double nsPerOp = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / n;
        Console.WriteLine(
            $"{name,-17} {n} in {sw.ElapsedMilliseconds} ms " +
            $"({nsPerOp:F0} ns/op, {allocBytes / (double)n:F0} B/op), {extra}");
    }
}
