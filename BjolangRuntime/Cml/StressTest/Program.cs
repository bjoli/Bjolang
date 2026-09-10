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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Bjoml;

namespace StressTest;

class Program
{
    static async Task Main(string[] args)
    {
        Scheduler.Start();

        Console.WriteLine("Starting Bjoml CML Stress Tests...\n");

        await RunSimpleRingBenchmark();
        Console.WriteLine();
        await RunProducerConsumerBenchmark();
        Console.WriteLine();
        await RunSimpleProducerConsumerBenchmark();
        Console.WriteLine();
        await RunFiberRingBenchmark();
        Console.WriteLine();
        await RunFiberSimpleRingBenchmark();
        Console.WriteLine();
        await RunFanOutBenchmark();
        Console.WriteLine();
        await RunCombinatorTest();

        Console.WriteLine("\nAll tests completed.");
    }

    /// <summary>
    /// One message into a channel, as a fiber. A `Channel&lt;T&gt;` rendezvous is a
    /// suspension, so injecting the ring's first message needs something that can
    /// suspend — which since the ValueTask facade went is a fiber and nothing else.
    /// </summary>
    static async Fiber SendOne(Channel<int> channel, int value) => await channel.Send(value);

    static async Task RunProducerConsumerBenchmark()
    {
        const int numProducers = 100;
        const int numConsumers = 100;
        const int messagesPerProducer = 5000;

        Console.WriteLine($"--- Fan-In / Fan-Out: {numProducers} Producers, {numConsumers} Consumers, {messagesPerProducer} msgs each ---");

        var sharedChannel = new Channel<int>();

        var producers = new Promise<Unit>[numProducers];
        var consumers = new Promise<Unit>[numConsumers];

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < numConsumers; i++)
            consumers[i] = Bjo.Spawn(() => Consume(sharedChannel));

        for (int i = 0; i < numProducers; i++)
            producers[i] = Bjo.Spawn(() => Produce(sharedChannel, messagesPerProducer));

        foreach (var p in producers) await p.ToTask();

        // Poison pills, one per consumer. Each is a rendezvous, so by the time
        // the last one returns every consumer has taken one and is on its way out.
        for (int i = 0; i < numConsumers; i++)
            await Bjo.Spawn(() => SendOne(sharedChannel, -1)).ToTask();

        foreach (var c in consumers) await c.ToTask();

        sw.Stop();
        Console.WriteLine($"Producer/Consumer finished in {sw.ElapsedMilliseconds} ms. Total Messages Processed: {_totalReceived}");
    }

    private static int _totalReceived;

    static async Fiber Consume(Channel<int> channel)
    {
        while (true)
        {
            int msg = await channel.Receive();
            if (msg == -1) return;
            System.Threading.Interlocked.Increment(ref _totalReceived);
        }
    }

    static async Fiber Produce(Channel<int> channel, int count)
    {
        for (int j = 0; j < count; j++) await channel.Send(j);
    }

    static async Task RunSimpleRingBenchmark()
    {
        const int numWorkers = 1000;
        const int numTrips = 1000;

        Console.WriteLine($"--- SimpleChannel Ring Benchmark: {numWorkers} workers, {numTrips} trips around the ring ---");
        
        var channels = new SimpleChannel<int>[numWorkers];
        for (int i = 0; i < numWorkers; i++)
        {
            channels[i] = new SimpleChannel<int>();
        }

        var tasks = new Task[numWorkers];

        for (int i = 0; i < numWorkers; i++)
        {
            int workerId = i;
            var inChannel = channels[workerId];
            var outChannel = channels[(workerId + 1) % numWorkers];

            tasks[i] = Task.Run(async () =>
            {
                while (true)
                {
                    int msg = await inChannel.GetMessage();
                    if (msg == -1) 
                    {
                        if (workerId != numWorkers - 1) 
                        {
                            await outChannel.PutMessage(-1);
                        }
                        break;
                    }
                    
                    if (workerId == numWorkers - 1)
                    {
                        msg++;
                        if (msg >= numTrips)
                        {
                            await outChannel.PutMessage(-1);
                            continue;
                        }
                    }

                    await outChannel.PutMessage(msg);
                }
            });
        }

        var sw = Stopwatch.StartNew();
        await channels[0].PutMessage(0);
        await Task.WhenAll(tasks);
        sw.Stop();
        Console.WriteLine($"SimpleChannel Ring Benchmark finished in {sw.ElapsedMilliseconds} ms. Passed {numWorkers * numTrips} messages.");
    }

    static async Task RunSimpleProducerConsumerBenchmark()
    {
        const int numProducers = 100;
        const int numConsumers = 100;
        const int messagesPerProducer = 5000;

        Console.WriteLine($"--- SimpleChannel Fan-In / Fan-Out: {numProducers} Producers, {numConsumers} Consumers, {messagesPerProducer} msgs each ---");

        var sharedChannel = new SimpleChannel<int>();
        var producers = new Task[numProducers];
        var consumers = new Task[numConsumers];
        
        int totalReceived = 0;

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < numConsumers; i++)
        {
            consumers[i] = Task.Run(async () =>
            {
                while (true)
                {
                    int msg = await sharedChannel.GetMessage();
                    if (msg == -1) break;
                    System.Threading.Interlocked.Increment(ref totalReceived);
                }
            });
        }

        for (int i = 0; i < numProducers; i++)
        {
            producers[i] = Task.Run(async () =>
            {
                for (int j = 0; j < messagesPerProducer; j++)
                {
                    await sharedChannel.PutMessage(j);
                }
            });
        }

        await Task.WhenAll(producers);

        for (int i = 0; i < numConsumers; i++)
        {
            await sharedChannel.PutMessage(-1);
        }

        await Task.WhenAll(consumers);
        sw.Stop();
        Console.WriteLine($"SimpleChannel Producer/Consumer finished in {sw.ElapsedMilliseconds} ms. Total Messages Processed: {totalReceived}");
    }

    /// <summary>
    /// The same ring, but every node is a Fiber awaiting IEvent directly instead of a
    /// Task awaiting a pooled ValueTask source. This is the path the compiled language
    /// actually uses, so it is the number that matters.
    /// </summary>
    static async Task RunFiberRingBenchmark()
    {
        const int numWorkers = 1000;
        const int numTrips = 1000;

        Console.WriteLine($"--- Fiber Ring Benchmark: {numWorkers} workers, {numTrips} trips around the ring ---");

        var channels = new Channel<int>[numWorkers];
        for (int i = 0; i < numWorkers; i++) channels[i] = new Channel<int>();

        var handles = new Promise<Unit>[numWorkers];

        for (int i = 0; i < numWorkers; i++)
        {
            int workerId = i;
            var inChannel = channels[workerId];
            var outChannel = channels[(workerId + 1) % numWorkers];
            bool isLast = workerId == numWorkers - 1;

            handles[i] = Bjo.Spawn(() => RingNode(inChannel, outChannel, isLast, numTrips));
        }

        var sw = Stopwatch.StartNew();

        await Bjo.Spawn(() => SendOne(channels[0], 0)).ToTask();

        foreach (var h in handles) await h.ToTask();

        sw.Stop();
        Console.WriteLine($"Fiber Ring Benchmark finished in {sw.ElapsedMilliseconds} ms. Passed {numWorkers * numTrips} messages.");
    }

    static async Fiber RingNode(Channel<int> inChannel, Channel<int> outChannel, bool isLast, int numTrips)
    {
        while (true)
        {
            int msg = await inChannel.Receive();

            if (msg == -1)
            {
                if (!isLast) await outChannel.Send(-1);
                return;
            }

            if (isLast)
            {
                msg++;
                if (msg >= numTrips)
                {
                    await outChannel.Send(-1);
                    continue;
                }
            }

            await outChannel.Send(msg);
        }
    }

    static async Task RunFiberSimpleRingBenchmark()
    {
        const int numWorkers = 1000;
        const int numTrips = 1000;

        Console.WriteLine($"--- Fiber SimpleChannel Ring Benchmark: {numWorkers} workers, {numTrips} trips around the ring ---");

        var channels = new SimpleChannel<int>[numWorkers];
        for (int i = 0; i < numWorkers; i++) channels[i] = new SimpleChannel<int>();

        var handles = new Promise<Unit>[numWorkers];

        for (int i = 0; i < numWorkers; i++)
        {
            int workerId = i;
            var inChannel = channels[workerId];
            var outChannel = channels[(workerId + 1) % numWorkers];
            bool isLast = workerId == numWorkers - 1;

            handles[i] = Bjo.Spawn(() => SimpleRingNode(inChannel, outChannel, isLast, numTrips));
        }

        var sw = Stopwatch.StartNew();

        await channels[0].PutMessage(0);

        foreach (var h in handles) await h.ToTask();

        sw.Stop();
        Console.WriteLine($"Fiber SimpleChannel Ring Benchmark finished in {sw.ElapsedMilliseconds} ms. Passed {numWorkers * numTrips} messages.");
    }

    static async Fiber SimpleRingNode(SimpleChannel<int> inChannel, SimpleChannel<int> outChannel, bool isLast, int numTrips)
    {
        while (true)
        {
            int msg = await inChannel.GetMessage();

            if (msg == -1)
            {
                if (!isLast) await outChannel.PutMessage(-1);
                return;
            }

            if (isLast)
            {
                msg++;
                if (msg >= numTrips)
                {
                    await outChannel.PutMessage(-1);
                    continue;
                }
            }

            await outChannel.PutMessage(msg);
        }
    }

    /// <summary>
    /// The workload the old scheduler could not do: one fiber spawning many children.
    ///
    /// With per-worker queues and no stealing, a fiber running on worker 3 enqueued
    /// every child onto worker 3's own queue, so the whole fan-out ran on ONE core
    /// while the others sat blocked in GetConsumingEnumerable. On the .NET pool the
    /// children are stealable, so this should scale with core count.
    /// </summary>
    static async Task RunFanOutBenchmark()
    {
        const int numChildren = 480;
        const int iterationsPerChild = 3_000_000;

        Console.WriteLine($"--- Fan-Out: 1 fiber spawns {numChildren} children, {iterationsPerChild} iterations of CPU work each ---");

        // Serial reference, so the speedup claim is measured rather than assumed.
        var serialSw = Stopwatch.StartNew();
        BurnCore(iterationsPerChild);
        serialSw.Stop();
        double serialTotalMs = serialSw.Elapsed.TotalMilliseconds * numChildren;

        var sw = Stopwatch.StartNew();
        await Bjo.Spawn(() => FanOutParent(numChildren, iterationsPerChild)).ToTask();
        sw.Stop();

        Console.WriteLine(
            $"Fan-Out finished in {sw.ElapsedMilliseconds} ms on {Environment.ProcessorCount} logical cores " +
            $"(serial reference {serialTotalMs:F0} ms, speedup {serialTotalMs / sw.Elapsed.TotalMilliseconds:F1}x).");
    }

    static async Fiber FanOutParent(int numChildren, int iterations)
    {
        var children = new Promise<Unit>[numChildren];

        // Spawned from INSIDE a fiber, which is the case the old scheduler pinned.
        for (int i = 0; i < numChildren; i++)
            children[i] = Bjo.Spawn(() => Burn(iterations));

        var doneChannel = new Channel<int>();

        for (int i = 0; i < numChildren; i++)
        {
            var child = children[i];
            Bjo.Spawn(() => Notify(child, doneChannel));
        }

        for (int i = 0; i < numChildren; i++)
            await doneChannel.Receive();
    }

    static async Fiber Notify(Promise<Unit> child, Channel<int> done)
    {
        await child;
        await done.Send(1);
    }

    static async Fiber Burn(int iterations)
    {
        // Deliberately synchronous CPU work with no suspension, so the only thing
        // being measured is whether the scheduler spread the children across cores.
        BurnCore(iterations);
        await Cml.Always(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void BurnCore(int iterations)
    {
        double acc = 0;
        for (int i = 1; i <= iterations; i++) acc += 1.0 / i;
        if (acc < 0) throw new Exception("unreachable");
    }

    static async Task RunCombinatorTest()
    {
        Console.WriteLine("--- CML Combinators Test (Choose, Wrap, WithNack) ---");
        var chan1 = new Channel<string>();
        var chan2 = new Channel<string>();

        // We will do a choose between receiving from chan1 or chan2.
        // We will wrap both to uppercase.
        // We will add a NACK to chan1 so if we receive from chan2 instead, chan1's NACK will fire.

        bool nackFired = false;

        var ev1 = Cml.WithNack(nackChan => 
        {
            // A bare callback rather than a fiber waiting on the nack: a fiber
            // would park forever in every run where this branch WON, since the
            // nack promise never completes there. Setting a flag runs no user
            // code, so it is safe on the borrowed thread that delivers it.
            Cml.Sync(nackChan, _ => nackFired = true);

            return Cml.Wrap(new ChannelReceiveEvent<string>(chan1), s => s.ToUpper() + " (from chan1)");
        });

        var ev2 = Cml.Wrap(new ChannelReceiveEvent<string>(chan2), s => s.ToUpper() + " (from chan2)");

        var choice = Cml.Choose(ev1, ev2);

        // Send to chan2 to ensure ev2 wins
        var sender = Bjo.Spawn(() => SendString(chan2, "hello"));

        var result = await Bjo.Spawn<string>(() => SyncOne(choice)).ToTask();
        await sender.ToTask();

        Console.WriteLine($"Result: {result}");
        
        // Wait briefly to allow NACK to fire on the ThreadPool
        await Task.Delay(100);
        Console.WriteLine($"Nack fired for chan1? {nackFired}");
    }

    static async Fiber SendString(Channel<string> channel, string value) => await channel.Send(value);

    /// Syncing is a suspension, so the one place a plain `async Task` used to
    /// reach into CML is now a one-shot fiber.
    static async Fiber<string> SyncOne(IEvent<string> ev) => await ev;
}
