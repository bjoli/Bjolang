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
using static Bjoml.Tests.Harness;

namespace Bjoml.Tests;

/// <summary>
/// <c>Cml.WrapChoose</c>: a <c>choose</c> of <c>wrap</c>s as one event, whose
/// sink the pooled sync state keeps. These tests check that it means what the
/// <c>choose</c> of <c>wrap</c>s means, and that a kept sink is not reached by
/// a branch of an earlier sync.
/// </summary>
public static class WrapChooseTests
{
    public static void RunAll()
    {
        Section("WrapChoose");
        Run("each branch maps its own value", EachBranchMaps);
        Run("the first ready branch wins", FirstReadyWins);
        Run("a wrap inside a branch applies both functions", NestedWrap);
        Run("a throwing function raises in the syncing fiber", ThrowsOnTheSyncingFiber);
        Run("a losing with-nack branch is nacked", LoserIsNacked);
        Run("published below the root, it makes its own sink", BelowTheRoot);
        Run("a warm sync allocates only the event", AllocatesOnlyTheEvent);
        Run("stale branches do not take values", StaleBranches, 60_000);
        Run("a once event: no object per warm sync", OnceAllocatesNothing);
        Run("a once event that is not published is dropped", OnceNotPublished);
        Run("once events: stale branches do not take values", OnceStaleBranches, 60_000);
        Run("once events: a ping-pong keeps every value", OncePingPong, 60_000);
    }

    private static void EachBranchMaps()
    {
        var r = Bjo.RunToCompletion(async () =>
        {
            var a = await global::BjolangRuntime.sync(Cml.WrapChoose<int, string, string>(
                Cml.Always(1), v => $"int {v}", Cml.Never<string>(), s => $"string {s}"));
            var b = await global::BjolangRuntime.sync(Cml.WrapChoose<int, string, int, string>(
                Cml.Never<int>(), v => $"int {v}", Cml.Never<string>(), s => s, Cml.Always(3), v => $"third {v}"));
            var c = await global::BjolangRuntime.sync(Cml.WrapChoose<int, int, int, string, string>(
                Cml.Never<int>(), v => "0", Cml.Never<int>(), v => "1", Cml.Never<int>(), v => "2",
                Cml.Always("x"), s => $"fourth {s}"));
            return $"{a}|{b}|{c}";
        });
        AssertEqual("int 1|third 3|fourth x", r, "the mapped values");
    }

    private static void FirstReadyWins()
    {
        var r = Bjo.RunToCompletion(async () =>
            await global::BjolangRuntime.sync(Cml.WrapChoose<int, int, int>(
                Cml.Always(1), v => v, Cml.Always(2), v => v)));
        AssertEqual(1, r, "publish order is priority");
    }

    private static void NestedWrap()
    {
        var ch = new Channel<int>();
        var p = Bjo.Spawn(async () =>
            await global::BjolangRuntime.sync(Cml.WrapChoose<int, int, int>(
                Cml.Wrap<int, int>(ch, v => v + 1), v => v * 10, Cml.Never<int>(), v => -1)));

        AwaitPark(() => ch.RawPendingReceiveCount, "the receive to park");
        var sent = new ManualResetEventSlim(false);
        Cml.Sync(new ChannelSendEvent<int>(ch, 4), _ => sent.Set());
        Await(sent, "the sender to complete");
        AssertEqual(50, p.ToTask().GetAwaiter().GetResult(), "inner wrap, then outer function");
    }

    private static void ThrowsOnTheSyncingFiber()
    {
        var ch = new Channel<int>();
        var caught = new ManualResetEventSlim(false);
        string? message = null;

        _ = Bjo.Spawn<Unit>(async () =>
        {
            try
            {
                await global::BjolangRuntime.sync(Cml.WrapChoose<int, int, int>(
                    Cml.Never<int>(), v => v,
                    ch, v => throw new InvalidOperationException($"function got {v}")));
            }
            catch (InvalidOperationException e)
            {
                message = e.Message;
                caught.Set();
            }

            return default;
        });

        AwaitPark(() => ch.RawPendingReceiveCount, "the receive to park");
        var sent = new ManualResetEventSlim(false);
        Cml.Sync(new ChannelSendEvent<int>(ch, 7), _ => sent.Set());

        Await(sent, "the sender to complete");
        Await(caught, "the receiver to raise the function's exception");
        AssertEqual("function got 7", message, "the receiver raised something else");
    }

    private static void LoserIsNacked()
    {
        var nacked = new ManualResetEventSlim(false);
        var r = Bjo.RunToCompletion(async () =>
            await global::BjolangRuntime.sync(Cml.WrapChoose<int, int, string>(
                Cml.WithNack<int>(nack =>
                {
                    Cml.Sync(nack, _ => nacked.Set());
                    return Cml.Never<int>();
                }), v => "loser",
                Cml.Always(2), v => "winner")));
        AssertEqual("winner", r, "the ready branch");
        Await(nacked, "the losing branch's nack");
    }

    /// <summary>
    /// Two of the same form in one sync, below a <c>choose</c>. Neither is at
    /// the root, so neither uses the kept sink, and each delivers its own
    /// function's value.
    /// </summary>
    private static void BelowTheRoot()
    {
        var r = Bjo.RunToCompletion(async () =>
        {
            var first = Cml.WrapChoose<int, int, string>(Cml.Never<int>(), v => "a", Cml.Never<int>(), v => "b");
            var second = Cml.WrapChoose<int, int, string>(Cml.Never<int>(), v => "c", Cml.Always(0), v => "d");
            return await global::BjolangRuntime.sync(Cml.Choose(first, second));
        });
        AssertEqual("d", r, "the branch of the second event");
    }

    private static void AllocatesOnlyTheEvent()
    {
        long perSync = Bjo.RunToCompletion(() => AllocLoop());
        // The event, a WrapChoose2 of four references, is 48 B on x64. A sink
        // or a delegate made per sync would add 64 B or more.
        Assert(perSync < 64, $"a warm sync allocated {perSync} B");
    }

    private static readonly Func<int, int> Same = v => v;

    private static void AwaitPark(Func<int> count, string what, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (count() == 0)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new AssertionException($"timed out waiting for {what}");
            Thread.Sleep(1);
        }
    }

    private static async Fiber<long> AllocLoop()
    {
        var always = Cml.Always(1);
        var never = Cml.Never<int>();
        for (int i = 0; i < 1000; i++)
            await global::BjolangRuntime.sync(Cml.WrapChoose(always, Same, never, Same));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            await global::BjolangRuntime.sync(Cml.WrapChoose(always, Same, never, Same));
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 10_000;
    }

    /// <summary>
    /// Receivers sync on a hot and a cold channel of different types, so the
    /// cold channel collects branches of many generations that hold the kept
    /// sinks' delegates. Every value must arrive exactly once.
    /// </summary>
    private static void StaleBranches()
    {
        const int Receivers = 4, PerSender = 20_000;
        var hot = new Channel<int>();
        var cold = new Channel<long>();
        var counts = new int[2 * PerSender];

        var senders = new[]
        {
            Bjo.Spawn(() => SendInts(hot, PerSender)),
            Bjo.Spawn(() => SendLongs(cold, PerSender)),
        };
        var receivers = new Promise<int>[Receivers];
        for (int r = 0; r < Receivers; r++)
            receivers[r] = Bjo.Spawn(() => Receive(hot, cold, 2 * PerSender / Receivers, counts));

        foreach (var s in senders) s.ToTask().GetAwaiter().GetResult();
        foreach (var r in receivers) r.ToTask().GetAwaiter().GetResult();

        for (int i = 0; i < counts.Length; i++)
            if (counts[i] != 1) Assert(false, $"value {i} arrived {counts[i]} times");
    }

    private static async Fiber SendInts(Channel<int> ch, int n)
    {
        for (int i = 0; i < n; i++)
        {
            await ch.Send(i);
            await Cml.Timeout(0);
        }
    }

    private static async Fiber SendLongs(Channel<long> ch, int n)
    {
        for (int i = 0; i < n; i++)
        {
            await ch.Send(n + i);
            if (i % 50 == 0) await Cml.Timeout(0);
        }
    }

    private static async Fiber<int> Receive(Channel<int> hot, Channel<long> cold, int n, int[] counts)
    {
        for (int i = 0; i < n; i++)
        {
            int at = await global::BjolangRuntime.sync(Cml.WrapChoose<int, long, int>(
                hot, v => v, cold, v => (int)v));
            Interlocked.Increment(ref counts[at]);
        }
        return 0;
    }

    private static void OnceAllocatesNothing()
    {
        long perSync = Bjo.RunToCompletion(() => OnceAllocLoop());
        // An object per sync is 24 B or more. What is left is the fiber moving
        // to a worker whose free slot, awaiter and kept sink are still empty,
        // about 1 B a sync on average.
        Assert(perSync < 8, $"a warm sync allocated {perSync} B");
    }

    private static async Fiber<long> OnceAllocLoop()
    {
        var always = Cml.Always(1);
        var never = Cml.Never<int>();
        for (int i = 0; i < 1000; i++)
            await global::BjolangRuntime.sync(Cml.WrapChooseOnce(always, Same, never, Same));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            await global::BjolangRuntime.sync(Cml.WrapChooseOnce(always, Same, never, Same));
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 10_000;
    }

    /// <summary>
    /// A rented event that is never published leaves the slot empty. The next
    /// rent makes a new event, which is not the dropped one.
    /// </summary>
    private static void OnceNotPublished()
    {
        var r = Bjo.RunToCompletion(async () =>
        {
            var dropped = Cml.WrapChooseOnce<int, int, string>(Cml.Always(1), v => "dropped", Cml.Never<int>(), v => "x");
            var used = Cml.WrapChooseOnce<int, int, string>(Cml.Always(2), v => $"used {v}", Cml.Never<int>(), v => "x");
            Assert(!ReferenceEquals(dropped, used), "a rented event was rented again before it was published");
            return await global::BjolangRuntime.sync(used);
        });
        AssertEqual("used 2", r, "the value of the event that was synced");
    }

    private static void OnceStaleBranches()
    {
        const int Receivers = 4, PerSender = 20_000;
        var hot = new Channel<int>();
        var cold = new Channel<long>();
        var counts = new int[2 * PerSender];

        var senders = new[]
        {
            Bjo.Spawn(() => SendInts(hot, PerSender)),
            Bjo.Spawn(() => SendLongs(cold, PerSender)),
        };
        var receivers = new Promise<int>[Receivers];
        for (int r = 0; r < Receivers; r++)
            receivers[r] = Bjo.Spawn(() => OnceReceive(hot, cold, 2 * PerSender / Receivers, counts));

        foreach (var s in senders) s.ToTask().GetAwaiter().GetResult();
        foreach (var r in receivers) r.ToTask().GetAwaiter().GetResult();

        for (int i = 0; i < counts.Length; i++)
            if (counts[i] != 1) Assert(false, $"value {i} arrived {counts[i]} times");
    }

    private static async Fiber<int> OnceReceive(Channel<int> hot, Channel<long> cold, int n, int[] counts)
    {
        for (int i = 0; i < n; i++)
        {
            int at = await global::BjolangRuntime.sync(Cml.WrapChooseOnce<int, long, int>(
                hot, v => v, cold, v => (int)v));
            Interlocked.Increment(ref counts[at]);
        }
        return 0;
    }

    /// <summary>
    /// Two fibers send each other a counter, each receiving through a once
    /// event of the same types. A receive that pairs with a parked partner can
    /// resume it inline, on the thread that is still publishing, and that
    /// partner rents from the same slot. Each value must arrive once, in
    /// order.
    /// </summary>
    private static void OncePingPong()
    {
        const int Rounds = 50_000;
        var toA = new Channel<int>();
        var toB = new Channel<int>();
        var quit = new Channel<int>();
        var a = Bjo.Spawn(() => Bounce(toA, toB, quit, Rounds, true));
        var b = Bjo.Spawn(() => Bounce(toB, toA, quit, Rounds, false));
        AssertEqual(Rounds, a.ToTask().GetAwaiter().GetResult(), "rounds seen by a");
        AssertEqual(Rounds, b.ToTask().GetAwaiter().GetResult(), "rounds seen by b");
    }

    private static async Fiber<int> Bounce(Channel<int> inbox, Channel<int> outbox, Channel<int> quit, int rounds, bool starts)
    {
        int expected = starts ? 1 : 0;
        if (starts) await outbox.Send(0);
        int seen = 0;
        while (seen < rounds)
        {
            int v = await global::BjolangRuntime.sync(Cml.WrapChooseOnce<int, int, int>(
                inbox, x => x, quit, x => -1));
            if (v != expected) throw new AssertionException($"expected {expected}, got {v}");
            seen++;
            expected = v + 2;
            if (!(starts && seen == rounds)) await outbox.Send(v + 1);
        }
        return seen;
    }
}
