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

// The "varied" suite: shapes the main suite never exercises, hunting dark spots.
//
// The main suite is all low-contention, narrow-select, no-timer shapes: one hot
// chain, two-branch receive-only chooses, and nothing that ever arms a timer or
// fires a nack. Each benchmark here isolates one of those blind spots:
//
//   Fan-in            channel contention, many senders one receiver, no spawn cost
//   Many-to-many      the channel lock convoy, 100 senders and 100 receivers
//   Wide choose (8)   select width: publish to 8, park 8 ops, 7 go stale per op
//   Skewed choose (8) dead-branch reclamation: 7 branches never fire (B7 sweeping)
//   Choose send (2)   the PublishSend path under choose, never benchmarked before
//   Choose+timeout    Guard/WithNack/Timer machinery per sync
//   Pipeline (4)      sustained multi-hop flow, several messages in flight
//   Par ping-pong     12 independent hot chains at once, scheduler contention
//
// Every receiver and sender is a native fiber (see BENCHMARKS.md: the interop
// path measures the interop boundary, not the machinery). Same counts and
// topology as bench/go/varied.go and bench/hopac/Varied.fs.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Bjoml;

namespace Bjoml.Bench;

public static class Varied
{
    public static async Task Run(int reps)
    {
        Console.WriteLine($".NET {Environment.Version}, ProcessorCount={Environment.ProcessorCount}, " +
                          $"ServerGC={System.Runtime.GCSettings.IsServerGC}, reps={reps}");
        Console.WriteLine();

        await Warmup();

        await Repeat(reps, FanIn);
        await Repeat(reps, ManyToMany);
        await Repeat(reps, WideChoose);
        await Repeat(reps, SkewedChoose);
        await Repeat(reps, ChooseSend);
        await Repeat(reps, ChooseTimeout);
        await Repeat(reps, Pipeline);
        await Repeat(reps, ParallelPingPong);
    }

    static async Task Repeat(int reps, Func<Task> body)
    {
        for (int i = 0; i < reps; i++) await body();
    }

    static async Task Warmup()
    {
        for (int i = 0; i < 200; i++)
        {
            var ch = new Channel<int>();
            var p = Bjo.Spawn(() => WarmFiber(ch));
            await Bjo.Spawn(() => WarmSend(ch)).ToTask();
            await p.ToTask();
        }
    }

    static async Fiber WarmFiber(Channel<int> ch) => await ch.Receive();
    static async Fiber WarmSend(Channel<int> ch) => await ch.Send(1);

    // ---------------------------------------------------------------------
    // 1. Fan-in: 100 long-lived producers, one channel, one consumer.
    //
    // Spawn+send already hammers one channel, but conflates it with 200k spawns
    // and 200k parked senders. Here the producers are 100 long-lived fibers, so
    // the giver list stays ~100 and what is measured is contended rendezvous.
    // ---------------------------------------------------------------------

    const int FanInProducers = 100;
    const int FanInPerProducer = 10_000;

    static async Fiber Produce(Channel<int> ch, int count)
    {
        for (int i = 0; i < count; i++) await ch.Send(1);
    }

    static async Fiber ConsumeSum(Channel<int> ch, int count, long[] sink)
    {
        long total = 0;
        for (int i = 0; i < count; i++) total += await ch.Receive();
        sink[0] = total;
    }

    static async Task FanIn()
    {
        const int n = FanInProducers * FanInPerProducer;
        var ch = new Channel<int>();
        var sink = new long[1];

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        var producers = new Promise<Unit>[FanInProducers];
        for (int p = 0; p < FanInProducers; p++)
            producers[p] = Bjo.Spawn(() => Produce(ch, FanInPerProducer));

        await Bjo.Spawn(() => ConsumeSum(ch, n, sink)).ToTask();
        foreach (var p in producers) await p.ToTask();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;
        Report("Fan-in", n, sw, alloc, $"total={sink[0]}");
    }

    // ---------------------------------------------------------------------
    // 2. Many-to-many: 100 producers, 100 consumers, ONE channel.
    //
    // Every operation takes the same Monitor. This is the convoy shape: if the
    // channel lock is the scaling limit, this is where it shows.
    // ---------------------------------------------------------------------

    const int M2MSide = 100;
    const int M2MPer = 10_000;

    static async Fiber ConsumeCount(Channel<int> ch, int count)
    {
        for (int i = 0; i < count; i++) await ch.Receive();
    }

    static async Task ManyToMany()
    {
        const int n = M2MSide * M2MPer;
        var ch = new Channel<int>();

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        var all = new Promise<Unit>[2 * M2MSide];
        for (int p = 0; p < M2MSide; p++)
        {
            all[2 * p] = Bjo.Spawn(() => Produce(ch, M2MPer));
            all[2 * p + 1] = Bjo.Spawn(() => ConsumeCount(ch, M2MPer));
        }
        foreach (var p in all) await p.ToTask();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;
        Report("Many-to-many", n, sw, alloc, "msgs");
    }

    // ---------------------------------------------------------------------
    // 3. Wide choose: one receiver choosing over 8 channels, sender round-robins.
    //
    // Per op the receiver publishes to 8 channels, parks 8 ops, and 7 of them go
    // stale. Measures how the per-branch publish cost and the stale-op walks
    // scale with select width.
    // ---------------------------------------------------------------------

    const int WideK = 8;
    const int WideRounds = 1_000_000;

    static async Fiber WideSender(Channel<int>[] chs, int rounds, bool skewed)
    {
        for (int i = 0; i < rounds; i++)
        {
            var ch = skewed ? chs[0] : chs[i & (WideK - 1)];
            await ch.Send(i);
        }
    }

    static async Fiber ChooseReceiver(IEvent<int> choose, int rounds)
    {
        for (int i = 0; i < rounds; i++) await choose;
    }

    static async Task WideChooseCore(string name, bool skewed)
    {
        var chs = new Channel<int>[WideK];
        var evs = new IEvent<int>[WideK];
        for (int i = 0; i < WideK; i++) evs[i] = chs[i] = new Channel<int>();

        var choose = Cml.Choose(evs);
        var sender = Bjo.Spawn(() => WideSender(chs, WideRounds, skewed));

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        await Bjo.Spawn(() => ChooseReceiver(choose, WideRounds)).ToTask();
        await sender.ToTask();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;
        Report(name, WideRounds, sw, alloc, "ops");
    }

    static Task WideChoose() => WideChooseCore("Wide choose (8)", skewed: false);

    // ---------------------------------------------------------------------
    // 4. Skewed choose: same 8 branches, but only channel 0 ever fires.
    //
    // The other 7 channels accumulate dead takers that only NotePark's amortised
    // sweep reclaims. This is the B7 bounded-dead-set design under sustained fire.
    // ---------------------------------------------------------------------

    static Task SkewedChoose() => WideChooseCore("Skewed choose(8)", skewed: true);

    // ---------------------------------------------------------------------
    // 5. Choose send: one fiber choosing between two SENDS.
    //
    // Every existing choose benchmark chooses between receives; the PublishSend
    // path under a SyncState has never been measured. Two receivers drain.
    // ---------------------------------------------------------------------

    const int ChooseSendRounds = 1_000_000;

    static async Fiber DrainUntilStop(Channel<int> ch, long[] count)
    {
        while (await ch.Receive() != -1) count[0]++;
    }

    static async Fiber ChooseSendLoop(IEvent<Unit> choose, Channel<int> a, Channel<int> b, int rounds)
    {
        for (int i = 0; i < rounds; i++) await choose;
        await a.Send(-1);
        await b.Send(-1);
    }

    static async Task ChooseSend()
    {
        var a = new Channel<int>();
        var b = new Channel<int>();
        var count = new long[1];

        // Constant payload so the event can be hoisted, exactly like the Go and
        // Hopac versions (`select { case a <- 1: case b <- 1: }`).
        var choose = Cml.Choose<Unit>(a.Send(1), b.Send(1));

        var ra = Bjo.Spawn(() => DrainUntilStop(a, count));
        var rb = Bjo.Spawn(() => DrainUntilStop(b, count));

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        await Bjo.Spawn(() => ChooseSendLoop(choose, a, b, ChooseSendRounds)).ToTask();
        await ra.ToTask();
        await rb.ToTask();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;
        Report("Choose send (2)", ChooseSendRounds, sw, alloc, $"received={count[0]}");
    }

    // ---------------------------------------------------------------------
    // 6. Choose + timeout: choose(receive, timeout 1s), receive always wins.
    //
    // The realistic select-with-deadline loop. Exercises Guard, WithNack, nack
    // firing, and Timer arm/dispose per sync — none of which any other benchmark
    // touches. Go's twin is the idiomatic `select { ...; case <-time.After(1s) }`,
    // which allocates a timer per iteration just like this does.
    // ---------------------------------------------------------------------

    const int TimeoutRounds = 100_000;

    static async Fiber PlainSender(Channel<int> ch, int rounds)
    {
        for (int i = 0; i < rounds; i++) await ch.Send(i);
    }

    static async Task ChooseTimeout()
    {
        var ch = new Channel<int>();

        var choose = Cml.Choose(
            (IEvent<int>)ch,
            Cml.Wrap(Cml.Timeout(1000), static _ => -1));

        var sender = Bjo.Spawn(() => PlainSender(ch, TimeoutRounds));

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        await Bjo.Spawn(() => ChooseReceiver(choose, TimeoutRounds)).ToTask();
        await sender.ToTask();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;
        Report("Choose+timeout", TimeoutRounds, sw, alloc, "ops");
    }

    // ---------------------------------------------------------------------
    // 7. Pipeline: source -> 4 stages -> sink, 500k items, all unbuffered.
    //
    // Unlike the ring (one token, one hot chain), several items are in flight at
    // once, so the stages genuinely run in parallel and every hop is a rendezvous
    // between two running fibers.
    // ---------------------------------------------------------------------

    const int PipelineStages = 4;
    const int PipelineItems = 500_000;

    static async Fiber PipelineSource(Channel<int> outCh, int items)
    {
        for (int i = 0; i < items; i++) await outCh.Send(i);
        await outCh.Send(-1);
    }

    static async Fiber PipelineStage(Channel<int> inCh, Channel<int> outCh)
    {
        while (true)
        {
            int v = await inCh.Receive();
            if (v == -1) { await outCh.Send(-1); return; }
            await outCh.Send(v + 1);
        }
    }

    static async Fiber PipelineSink(Channel<int> inCh, long[] sink)
    {
        long total = 0;
        while (true)
        {
            int v = await inCh.Receive();
            if (v == -1) { sink[0] = total; return; }
            total += v;
        }
    }

    static async Task Pipeline()
    {
        var chs = new Channel<int>[PipelineStages + 1];
        for (int i = 0; i < chs.Length; i++) chs[i] = new Channel<int>();
        var sink = new long[1];

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        var all = new Promise<Unit>[PipelineStages + 2];
        all[0] = Bjo.Spawn(() => PipelineSource(chs[0], PipelineItems));
        for (int s = 0; s < PipelineStages; s++)
        {
            var inCh = chs[s];
            var outCh = chs[s + 1];
            all[s + 1] = Bjo.Spawn(() => PipelineStage(inCh, outCh));
        }
        all[PipelineStages + 1] = Bjo.Spawn(() => PipelineSink(chs[PipelineStages], sink));

        foreach (var p in all) await p.ToTask();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;
        Report("Pipeline (4)", PipelineItems, sw, alloc, $"total={sink[0]}");
    }

    // ---------------------------------------------------------------------
    // 8. Parallel ping-pong: 12 independent pairs, each on its own two channels.
    //
    // The main suite's ping-pong is ONE hot chain, which inline dispatch turns
    // into single-threaded execution. Twelve simultaneous chains is the shape
    // that shows scheduler contention and cache-line traffic between chains.
    // ---------------------------------------------------------------------

    const int PairCount = 12;
    const int PairRounds = 100_000;

    static async Fiber PairPonger(Channel<int> a, Channel<int> b, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            int v = await a.Receive();
            await b.Send(v * 2);
        }
    }

    static async Fiber PairPinger(Channel<int> a, Channel<int> b, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            await a.Send(i);
            await b.Receive();
        }
    }

    static async Task ParallelPingPong()
    {
        const int n = PairCount * PairRounds;

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        var all = new Promise<Unit>[2 * PairCount];
        for (int p = 0; p < PairCount; p++)
        {
            var a = new Channel<int>();
            var b = new Channel<int>();
            all[2 * p] = Bjo.Spawn(() => PairPonger(a, b, PairRounds));
            all[2 * p + 1] = Bjo.Spawn(() => PairPinger(a, b, PairRounds));
        }
        foreach (var p in all) await p.ToTask();

        sw.Stop();
        long alloc = GC.GetTotalAllocatedBytes(precise: true) - before;
        Report("Par ping-pong(12)", n, sw, alloc, "round trips");
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
