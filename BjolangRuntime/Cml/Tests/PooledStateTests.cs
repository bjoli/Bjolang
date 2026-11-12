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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static Bjoml.Tests.Harness;

namespace Bjoml.Tests;

/// <summary>
/// A <see cref="SyncState"/> is pooled with its <see cref="EventAwaiter{T}"/>
/// and gets a new generation at each sync. These tests check that a branch left
/// behind by an earlier sync (a parked op, a promise waiter, a timer) can not
/// commit a later sync of the same state.
/// </summary>
public static class PooledStateTests
{
    public static void RunAll()
    {
        Section("Pooled sync states");
        Run("an old generation loses every transition", OldGenerationLoses);
        Run("a choose reuses its awaiter's state", ChooseReusesState);
        Run("a choose allocates nothing once warm", ChooseAllocatesNothing);
        Run("stale channel branches do not take values", StaleChannelBranches, 60_000);
        Run("stale promise waiters do not commit a later sync", StalePromiseWaiters, 30_000);
        Run("stale timers do not commit a later sync", StaleTimers, 30_000);
    }

    private static void OldGenerationLoses()
    {
        var state = new SyncState();
        long old = state.Gen;
        state.Renew();
        long now = state.Gen;
        Assert(now == old + 1, "Renew did not advance the generation");

        Assert(state.IsSynchronizedAt(old), "an old generation reads as live");
        Assert(!state.IsSynchronizedAt(now), "the new generation reads as over");
        Assert(!state.TryCommit(old, 1), "TryCommit at an old generation succeeded");
        Assert(!state.TrySyncWaiting(old), "TrySyncWaiting at an old generation succeeded");
        Assert(!state.TryClaim(old), "TryClaim at an old generation succeeded");

        var other = new SyncState();
        Assert(other.TryClaim(), "could not claim the other state");
        AssertEqual(SyncState.Pairing.Lost, other.TryPair(state, old), "TryPair at an old generation");
        other.ResetClaim();

        // The new generation still works, and was not touched by the attempts.
        Assert(state.TryCommit(now, 3), "TryCommit at the current generation failed");
        AssertEqual(3, state.WinningEventId, "winner");
        Assert(state.IsSynchronized, "state did not reach S");
    }

    private static void ChooseReusesState()
    {
        var a = new Channel<int>();
        var b = new Channel<int>();
        var ev = Cml.Choose<int>(a, b);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        Bjo.RunToCompletion(() => Loop(ev, a, seen));
        Assert(seen.Count <= 2, $"100 chooses on one fiber used {seen.Count} states");
    }

    private static async Fiber<int> Loop(IEvent<int> ev, Channel<int> a, HashSet<object> seen)
    {
        for (int i = 0; i < 100; i++)
        {
            // A sender parks first, so the choose commits while it publishes.
            _ = Bjo.Spawn(() => SendOne(a, i));
            await Cml.Timeout(0);
            var aw = EventAwaitExtensions.GetAwaiter<int>(ev);
            seen.Add(StateOf(aw));
            await new Started<int>(aw);
        }
        return 0;
    }

    /// <summary>Awaits an awaiter that was taken by hand.</summary>
    private readonly struct Started<T>(EventAwaiter<T> aw)
    {
        public EventAwaiter<T> GetAwaiter() => aw;
    }

    private static object StateOf<T>(EventAwaiter<T> aw) =>
        typeof(EventAwaiter<T>).GetField("_state",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(aw)!;

    private static async Fiber SendOne(Channel<int> ch, int v) => await ch.Send(v);

    private static void ChooseAllocatesNothing()
    {
        long perSync = Bjo.RunToCompletion(() => AllocLoop());
        Assert(perSync == 0, $"a warm choose allocated {perSync} B per sync");
    }

    private static async Fiber<long> AllocLoop()
    {
        var ev = Cml.Choose(Cml.Always(1), Cml.Never<int>());
        for (int i = 0; i < 1000; i++) await ev;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) await ev;
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 10_000;
    }

    /// <summary>
    /// Receivers choose over a hot and a cold channel, so the cold channel
    /// collects branches of many generations of the same states. Senders send
    /// on both. Every value must arrive exactly once.
    /// </summary>
    private static void StaleChannelBranches()
    {
        const int Receivers = 4, PerSender = 20_000;
        var hot = new Channel<int>();
        var cold = new Channel<int>();
        var ev = Cml.Choose<int>(hot, cold);
        var counts = new int[2 * PerSender];

        var senders = new[]
        {
            Bjo.Spawn(() => SendRange(hot, 0, PerSender, 1)),
            Bjo.Spawn(() => SendRange(cold, PerSender, PerSender, 50)),
        };
        var receivers = new Promise<int>[Receivers];
        for (int r = 0; r < Receivers; r++)
            receivers[r] = Bjo.Spawn(() => Receive(ev, 2 * PerSender / Receivers, counts));

        foreach (var s in senders) s.ToTask().GetAwaiter().GetResult();
        foreach (var r in receivers) r.ToTask().GetAwaiter().GetResult();

        for (int i = 0; i < counts.Length; i++)
            if (counts[i] != 1) Assert(false, $"value {i} arrived {counts[i]} times");
    }

    private static async Fiber SendRange(Channel<int> ch, int from, int n, int pauseEvery)
    {
        for (int i = 0; i < n; i++)
        {
            await ch.Send(from + i);
            if (i % pauseEvery == 0) await Cml.Timeout(0);
        }
    }

    private static async Fiber<int> Receive(IEvent<int> ev, int n, int[] counts)
    {
        for (int i = 0; i < n; i++) Interlocked.Increment(ref counts[await ev]);
        return 0;
    }

    /// <summary>
    /// A fiber chooses many times between a channel and a promise that is not
    /// set, which leaves a waiter of each generation in the promise. Then the
    /// promise is set while the fiber waits on a sync that does not include it.
    /// That sync must not complete.
    /// </summary>
    private static void StalePromiseWaiters()
    {
        var ch = new Channel<int>();
        var only = new Channel<int>();
        var p = new Promise<int>();
        var waiting = new ManualResetEventSlim(false);

        var fiber = Bjo.Spawn(() => PromiseLoop(ch, only, p, waiting));
        for (int i = 0; i < 2000; i++) Bjo.RunToCompletion(() => SendOnce(ch, i));

        Await(waiting, "the fiber to wait on the other channel");
        Thread.Sleep(20);
        p.TrySetResult(-7);
        Thread.Sleep(100);
        Assert(!fiber.IsCompleted, "a stale promise waiter completed a sync it was not in");

        Bjo.RunToCompletion(() => SendOnce(only, 5));
        AssertEqual(5, fiber.ToTask().GetAwaiter().GetResult(), "the value of the last sync");
    }

    private static async Fiber<int> SendOnce(Channel<int> ch, int v)
    {
        await ch.Send(v);
        return 0;
    }

    private static async Fiber<int> PromiseLoop(Channel<int> ch, Channel<int> only, Promise<int> p, ManualResetEventSlim waiting)
    {
        var ev = Cml.Choose<int>(ch, p.ValueEvent());
        for (int i = 0; i < 2000; i++)
        {
            int v = await ev;
            if (v < 0) throw new Exception("the promise won before it was set");
        }
        // The same awaiter pool, so the same states, now on a sync without p.
        var last = Cml.Choose<int>(only, Cml.Never<int>());
        waiting.Set();
        return await last;
    }

    /// <summary>
    /// Timeouts that lose stay in the wheel until their deadline. When they
    /// fire, the states they were armed on are at later generations, in syncs
    /// that have no timeout. Those syncs must not complete.
    /// </summary>
    private static void StaleTimers()
    {
        var ch = new Channel<int>();
        var only = new Channel<int>();
        var waiting = new ManualResetEventSlim(false);
        var fiber = Bjo.Spawn(() => TimerLoop(ch, only, waiting));
        for (int i = 0; i < 500; i++) Bjo.RunToCompletion(() => SendOnce(ch, i));

        Await(waiting, "the fiber to wait on the other channel");
        Thread.Sleep(400);   // past every deadline
        Assert(!fiber.IsCompleted, "a stale timer completed a sync it was not in");

        Bjo.RunToCompletion(() => SendOnce(only, 9));
        AssertEqual(9, fiber.ToTask().GetAwaiter().GetResult(), "the value of the last sync");
    }

    private static async Fiber<int> TimerLoop(Channel<int> ch, Channel<int> only, ManualResetEventSlim waiting)
    {
        var ev = Cml.Choose<int>(ch, Cml.Wrap(Cml.Timeout(200), _ => -1));
        for (int i = 0; i < 500; i++)
        {
            int v = await ev;
            if (v < 0) throw new Exception("a timeout won while a sender was waiting");
        }
        var last = Cml.Choose<int>(only, Cml.Never<int>());
        waiting.Set();
        return await last;
    }
}
