// Does a spawn batch actually reach all the cores?
//
// The throughput harness in Program.cs measures ns/op on a million trivial fibers.
// That is the wrong instrument for this question: with a million fibers there is so
// much work that ANY policy fills every core, so a policy that serialises fibers in
// groups still looks perfect.
//
// The failure mode only shows up when the number of spawned fibers is SMALL but
// above the burst threshold, and each fiber is expensive. Sixteen CPU-bound fibers
// on a 24-core box should finish in one fiber's worth of time. If the batch policy
// decides in advance that a range of 8 is "small enough to run inline", eight of
// them run one after another on a single thread and it takes eight times as long —
// while 23 cores sit idle.
//
// So this measures achieved SPEEDUP against a calibrated serial reference, sweeps N
// across the interesting region, and compares policies. Counting distinct threads is
// not enough; a policy can touch many threads and still be badly serialised.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Bjoml;

namespace Bjoml.Diag;

public static class FanoutHarness
{
    private static int _remaining;
    private static readonly ManualResetEventSlim _done = new(false);
    private static int _iterations;
    private static double _sink;

    public static void Run(int reps)
    {
        int procs = Environment.ProcessorCount;

        // One fiber should be big enough that scheduling noise (including the 1 ms
        // watchdog, which this benchmark avoids anyway) cannot dominate it.
        double serialMs = Calibrate(targetMs: 8.0);

        Console.WriteLine($".NET {Environment.Version}  ProcessorCount={procs}  " +
                          $"ServerGC={System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine($"one fiber = {serialMs:F2} ms of CPU ({_iterations:N0} iterations), " +
                          $"reps={reps}, median reported");
        Console.WriteLine();
        Console.WriteLine("Achieved speedup vs serial reference (higher is better; 'ideal' = min(N, cores))");
        Console.WriteLine();

        var policies = new (string Name, Action Apply)[]
        {
            ("unbatched",  () => { Scheduler.BatchSpawns = false; }),
            ("floor=8",    () => { Scheduler.BatchSpawns = true; Scheduler.BatchMode = Scheduler.SpawnBatchMode.Tree; Scheduler.SpawnSplitFloor = 8; }),
            ("floor=4",    () => { Scheduler.BatchSpawns = true; Scheduler.BatchMode = Scheduler.SpawnBatchMode.Tree; Scheduler.SpawnSplitFloor = 4; }),
            ("floor=2",    () => { Scheduler.BatchSpawns = true; Scheduler.BatchMode = Scheduler.SpawnBatchMode.Tree; Scheduler.SpawnSplitFloor = 2; }),
            ("floor=1",    () => { Scheduler.BatchSpawns = true; Scheduler.BatchMode = Scheduler.SpawnBatchMode.Tree; Scheduler.SpawnSplitFloor = 1; }),
            ("adaptive",   () => { Scheduler.BatchSpawns = true; Scheduler.BatchMode = Scheduler.SpawnBatchMode.Adaptive; }),
        };

        int[] sweep = { 2, 4, 8, 9, 10, 12, 16, 20, 24, 32, 48, 64, 96, 128, 256 };

        Console.Write($"{"N",5} {"ideal",7}");
        foreach (var p in policies) Console.Write($" {p.Name,10}");
        Console.WriteLine();
        Console.WriteLine(new string('-', 14 + policies.Length * 11));

        foreach (int n in sweep)
        {
            double ideal = Math.Min(n, procs);
            Console.Write($"{n,5} {ideal,7:F1}");

            foreach (var p in policies)
            {
                p.Apply();
                RunFanout(n);                       // warm-up, discarded

                var samples = new double[reps];
                for (int r = 0; r < reps; r++)
                {
                    double wallMs = RunFanout(n);
                    samples[r] = n * serialMs / wallMs;   // achieved speedup
                }
                Array.Sort(samples);
                Console.Write($" {samples[reps / 2],10:F1}");
            }

            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine("Efficiency = achieved / ideal, as a percentage");
        Console.WriteLine();
        Console.Write($"{"N",5} {"ideal",7}");
        foreach (var p in policies) Console.Write($" {p.Name,10}");
        Console.WriteLine();
        Console.WriteLine(new string('-', 14 + policies.Length * 11));

        foreach (int n in sweep)
        {
            double ideal = Math.Min(n, procs);
            Console.Write($"{n,5} {ideal,7:F1}");

            foreach (var p in policies)
            {
                p.Apply();
                RunFanout(n);

                var samples = new double[reps];
                for (int r = 0; r < reps; r++)
                    samples[r] = n * serialMs / RunFanout(n);
                Array.Sort(samples);
                Console.Write($" {100.0 * samples[reps / 2] / ideal,9:F0}%");
            }

            Console.WriteLine();
        }

        // Leave the process in the shipped configuration.
        Scheduler.BatchSpawns = true;
        Scheduler.BatchMode = Scheduler.SpawnBatchMode.Tree;
        Scheduler.SpawnSplitFloor = 8;
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// Spawn N CPU-bound fibers from INSIDE a fiber and time until all have run.
    ///
    /// Spawning from a fiber matters: the parent suspends/returns immediately after
    /// the loop, which flushes the batch at a work-item boundary. Spawning from a
    /// plain thread that then blocks would instead wait for the 1 ms watchdog, and
    /// that latency would be confounded with the split policy under test.
    /// </summary>
    private static double RunFanout(int n)
    {
        _remaining = n;
        _done.Reset();

        var sw = Stopwatch.StartNew();
        Bjo.Spawn(() => Parent(n));
        _done.Wait();
        sw.Stop();

        return sw.Elapsed.TotalMilliseconds;
    }

#pragma warning disable CS1998
    private static async Fiber Parent(int n)
    {
        for (int i = 0; i < n; i++) Bjo.Spawn(Child);
    }

    private static async Fiber Child()
    {
        Burn(_iterations);
        if (Interlocked.Decrement(ref _remaining) == 0) _done.Set();
    }
#pragma warning restore CS1998

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Burn(int iterations)
    {
        double acc = 0;
        for (int i = 1; i <= iterations; i++) acc += 1.0 / i;
        _sink = acc;
    }

    /// <summary>Pick an iteration count worth roughly <paramref name="targetMs"/>.</summary>
    private static double Calibrate(double targetMs)
    {
        int probe = 2_000_000;
        Burn(probe);                       // JIT + frequency ramp

        var sw = Stopwatch.StartNew();
        Burn(probe);
        sw.Stop();

        _iterations = (int)(probe * targetMs / sw.Elapsed.TotalMilliseconds);

        // Measure the real thing at the chosen size, best of 5 — the serial
        // reference is the denominator of every number in the table, so a serial
        // reference inflated by a stray interrupt would flatter every policy.
        double best = double.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            var s = Stopwatch.StartNew();
            Burn(_iterations);
            s.Stop();
            best = Math.Min(best, s.Elapsed.TotalMilliseconds);
        }
        return best;
    }
}
