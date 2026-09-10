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
using System.Threading;

namespace Bjoml.Tests;

/// <summary>
/// Tests for the spawn batching in <c>SpawnBatch</c>.
///
/// Batching parks freshly spawned fibers in a thread-local buffer and hands them to
/// the pool in groups, which is a ~7x throughput win but introduces a failure mode
/// the unbatched path did not have: a fiber that is buffered but never published is
/// never run. Every test here is written so that a stranded fiber shows up as a
/// TIMEOUT rather than as a silently wrong number.
/// </summary>
public static class SpawnBatchTests
{
    private static double _sink;

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Burn(int iterations)
    {
        double acc = 0;
        for (int i = 1; i <= iterations; i++) acc += 1.0 / i;
        _sink = acc;
    }

    public static void RunAll()
    {
        Harness.Section("Spawn batching");

        Harness.Run("every spawn in a large burst runs exactly once", () =>
        {
            const int n = 200_000;
            int counter = 0;
            var done = new ManualResetEventSlim(false);
            int remaining = n;

            for (int i = 0; i < n; i++)
            {
                Bjo.Spawn(() => Count());
            }

            Harness.Await(done, "all 200k spawned fibers to run", 30_000);
            Harness.AssertEqual(n, Volatile.Read(ref counter), "fibers run");

#pragma warning disable CS1998
            async Fiber Count()
            {
                Interlocked.Increment(ref counter);
                if (Interlocked.Decrement(ref remaining) == 0) done.Set();
            }
#pragma warning restore CS1998
        });

        // THE deadlock test. A thread that spawns more than BurstThreshold fibers and
        // then blocks on something outside BjoML has buffered fibers that no
        // work-item boundary will ever flush, because it never reaches one. Only the
        // watchdog can rescue them. Without it this test hangs forever.
        Harness.Run("watchdog publishes fibers stranded by a thread that never yields", () =>
        {
            const int n = 20;   // > BurstThreshold (8), < Capacity (64), so they sit buffered
            int remaining = n;
            var done = new ManualResetEventSlim(false);

            for (int i = 0; i < n; i++) Bjo.Spawn(() => Finish());

            // This thread now blocks without ever returning to the pool. The only
            // thing that can publish the buffered fibers is the watchdog sweep.
            Harness.Await(done, "watchdog to publish stranded fibers", 5_000);

#pragma warning disable CS1998
            async Fiber Finish()
            {
                if (Interlocked.Decrement(ref remaining) == 0) done.Set();
            }
#pragma warning restore CS1998
        });

        // Exercises the watchdog's stop/re-arm cycle many times over: it stops itself
        // when a sweep finds nothing pending and must re-arm on the next buffered
        // spawn. Each iteration uses a FRESH thread, so the burst counter restarts
        // and exactly one fiber is left buffered, and the quiet period is varied
        // around the 1 ms period so spawns land at different points in the cycle.
        //
        // SCOPE, honestly: this catches gross breakage of arming (a watchdog that
        // never restarts after going idle), and it is the only test that covers the
        // cycle rather than a single shot. It does NOT reliably detect the
        // store/load fence bug that this handshake is vulnerable to — that window is
        // a few instructions wide, and removing the re-check in Sweep was measured to
        // still pass this test three runs out of three. Detecting that would need
        // fault injection (a stall hook inside Sweep between the scan and the stop),
        // which is not built. Treat the fences in SpawnBatch as reasoned, not
        // test-covered.
        Harness.Run("watchdog survives repeated stop/re-arm cycles", () =>
        {
            const int iterations = 150;
            const int perIteration = 9;   // 8 direct + 1 left in the buffer

            for (int iter = 0; iter < iterations; iter++)
            {
                int remaining = perIteration;
                var done = new ManualResetEventSlim(false);

                var t = new Thread(() =>
                {
                    for (int i = 0; i < perIteration; i++) Bjo.Spawn(() => Finish());
                })
                { IsBackground = true };

                t.Start();
                t.Join();

                if (!done.Wait(5_000))
                    throw new AssertionException(
                        $"iteration {iter}: {Volatile.Read(ref remaining)} of {perIteration} fibers " +
                        "never ran; the watchdog failed to re-arm after stopping");

                // Straddle the 1 ms watchdog period so some iterations arm from a
                // stopped timer and others race the stop decision itself.
                Thread.Sleep(iter % 3);

#pragma warning disable CS1998
                async Fiber Finish()
                {
                    if (Interlocked.Decrement(ref remaining) == 0) done.Set();
                }
#pragma warning restore CS1998
            }
        }, timeoutMs: 60_000);

        // Same shape, but the spawner burns CPU instead of blocking, so it holds the
        // thread without any suspension point at all.
        Harness.Run("watchdog publishes while the spawner spins on CPU", () =>
        {
            const int n = 30;
            int remaining = n;
            var done = new ManualResetEventSlim(false);

            for (int i = 0; i < n; i++) Bjo.Spawn(() => Finish());

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double acc = 0;
            while (sw.ElapsedMilliseconds < 300 && !done.IsSet)
                for (int i = 1; i < 10_000; i++) acc += 1.0 / i;
            GC.KeepAlive(acc);

            Harness.Await(done, "fibers to run while spawner spins", 5_000);

#pragma warning disable CS1998
            async Fiber Finish()
            {
                if (Interlocked.Decrement(ref remaining) == 0) done.Set();
            }
#pragma warning restore CS1998
        });

        // A small fan-out must stay on the direct path: below the burst threshold
        // nothing is buffered, so latency is exactly what it was before batching.
        Harness.Run("a small fan-out is published immediately", () =>
        {
            const int n = 4;   // < BurstThreshold
            int remaining = n;
            var done = new ManualResetEventSlim(false);

            for (int i = 0; i < n; i++) Bjo.Spawn(() => Finish());

            // No watchdog tick should be needed, so allow far less than its period
            // times a safety factor: this must pass on the direct path alone.
            Harness.Await(done, "4 fibers to run without batching", 2_000);

#pragma warning disable CS1998
            async Fiber Finish()
            {
                if (Interlocked.Decrement(ref remaining) == 0) done.Set();
            }
#pragma warning restore CS1998
        });

        // A batch must reach the idle cores, and this has to be asserted as achieved
        // PARALLELISM, not as a thread count. An earlier version of this test only
        // checked that more than one thread was touched, and it happily passed while
        // a fixed split floor of 8 was serialising sixteen fibers into two chunks —
        // a 4.6x slowdown that the assertion could not see.
        //
        // The interesting size is just above the burst threshold: below it nothing is
        // batched, and far above it there is so much work that any policy fills the
        // machine. Sixteen is where a bad policy shows up.
        Harness.Run("a batch of 16 CPU-bound fibers actually runs in parallel", () =>
        {
            if (Environment.ProcessorCount < 8)
            {
                // Nothing to assert about parallelism on a machine that has none.
                return;
            }

            const int n = 16;

            // Calibrate on this machine rather than hard-coding a duration, so the
            // test means the same thing on a slow box as on a fast one.
            int iterations = 3_000_000;
            var cal = System.Diagnostics.Stopwatch.StartNew();
            Burn(iterations);
            cal.Stop();
            double serialMs = cal.Elapsed.TotalMilliseconds;

            int remaining = n;
            var done = new ManualResetEventSlim(false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Bjo.Spawn(() => Parent());
            Harness.Await(done, "16 CPU-bound fibers", 60_000);
            sw.Stop();

            double speedup = n * serialMs / sw.Elapsed.TotalMilliseconds;

            // Deliberately loose. Ideal is 16x; adaptive measures ~17x and the broken
            // floor=8 policy measures ~4.3x. A threshold of 4 separates them with a
            // wide margin while tolerating a loaded or throttled machine.
            Harness.Assert(speedup >= 4.0,
                $"16 fibers achieved only {speedup:F1}x speedup (serial {serialMs:F0} ms, " +
                $"wall {sw.Elapsed.TotalMilliseconds:F0} ms); the batch is being serialised");

#pragma warning disable CS1998
            async Fiber Parent()
            {
                for (int i = 0; i < n; i++) Bjo.Spawn(() => Work());
            }

            async Fiber Work()
            {
                Burn(iterations);
                if (Interlocked.Decrement(ref remaining) == 0) done.Set();
            }
#pragma warning restore CS1998
        });

        // Exceptions must not take out the rest of the batch.
        Harness.Run("a throwing fiber does not kill its batch", () =>
        {
            const int n = 40;
            int survived = 0;
            int remaining = n;
            var done = new ManualResetEventSlim(false);
            var prev = Scheduler.UnhandledException;
            Scheduler.UnhandledException = static _ => { };

            try
            {
                for (int i = 0; i < n; i++)
                {
                    int id = i;
                    Bjo.Spawn(() => Maybe(id));
                }
                Harness.Await(done, "all fibers in a batch with throwers", 5_000);
                Harness.AssertEqual(n / 2, Volatile.Read(ref survived), "non-throwing fibers that ran");
            }
            finally
            {
                Scheduler.UnhandledException = prev;
            }

#pragma warning disable CS1998
            async Fiber Maybe(int id)
            {
                try
                {
                    if (id % 2 == 0) throw new InvalidOperationException("boom");
                    Interlocked.Increment(ref survived);
                }
                finally
                {
                    if (Interlocked.Decrement(ref remaining) == 0) done.Set();
                }
            }
#pragma warning restore CS1998
        });
    }
}
