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

// Regression tests for the correctness bugs described in the review.

using System;
using System.Threading;
using static Bjoml.Tests.Harness;

namespace Bjoml.Tests;

public static class CmlTests
{
    public static void RunAll()
    {
        Section("B3 - self-synchronisation must not livelock");
        Run("choose(send ch, recv ch) publishes without spinning", SelfChooseTerminates);
        Run("self-choose still pairs with an external partner", SelfChoosePairsExternally);
        Run("two symmetric self-chooses pair with each other", SymmetricSelfChooses);

        Section("B1 - nack must not fire when a nested choose under it wins");
        Run("nack silent when its own subtree wins", NackSilentWhenSubtreeWins);
        Run("nack fires when an outside branch wins", NackFiresWhenOutsideBranchWins);

        Section("B2 - colliding nack ids must not overwrite each other");
        Run("two nested withNacks both fire", NestedNacksBothFire);

        Section("B4 / B10 - always-enabled events");
        Run("Always wins a choose", AlwaysWinsChoose);
        Run("Always is not dropped when published after a parked branch", AlwaysAfterParkedBranch);

        Section("B7 - losing branches must stay bounded, live ones must survive");
        Run("losers do not grow with the iteration count", StaleOpsStayBounded);
        Run("a channel abandoned after one choose keeps at most one", AbandonedChannelKeepsAtMostOne);
        Run("a live parked receive is NOT cleaned away", LiveReceiveSurvivesCleanup);
        Run("withNack losers stay bounded too", WithNackLosersBounded);

        Section("The direct park");
        Run("a bare sync parks with no SyncState", BareSyncParksDirectly);
        Run("a choose branch never parks directly", ChooseNeverParksDirectly);
        Run("a directly parked receive pairs with a choose send", DirectPairsWithChooseSend);
        Run("a directly parked send pairs with a choose receive", DirectSendPairsWithChooseReceive);

        Section("Baseline combinator behaviour");
        Run("wrap maps the value", WrapMapsValue);
        Run("guard is evaluated at sync time", GuardIsDeferred);

        Section("Timers");
        Run("a timeout becomes available on its own", TimeoutFires);
        Run("a timeout loses to something already available", TimeoutLoses);
        Run("a timeout is relative to each sync, not to when it was built", TimeoutIsRebuiltPerSync);
        Run("an absolute deadline does not restart on the next sync", AtIsAbsolute);
        Run("a timeout that already passed is available at once", AtInThePastFiresNow);
        Run("a cancelled timeout never fires into a later sync", CancelledTimeoutStaysQuiet);
        Run("combinator spec: a timeout becomes available on its own", SpecTimeoutFires);
        Run("combinator spec: a timeout loses to something already available", SpecTimeoutLoses);

        Section("Choice order");
        Run("choose is deterministic left-to-right by default", ChooseIsDeterministicByDefault);
        Run("randomized choose lets every branch win", RandomizedChooseIsFair);

        Section("Detaching and linking a promise");
        Run("detach routes a failure to the scheduler", DetachReportsFailure);
        Run("detach stays quiet on success", DetachIsQuietOnSuccess);
        Run("forward pipes an outcome into another promise", ForwardPipesOutcome);

        Section("The ambient token that `sync` races");
        Run("a parked choose raises on the resuming fiber's own stack", ParkedChooseRaisesOnItsOwnStack);
        Run("cancellation runs no user code on the thread that fired the token", CancelRunsNothingOnTheSignaller);
        Run("a delivered value beats a token that has already fired", ValueBeatsFiredToken);
        Run("a token that fires after the commit loses, and the value survives", LateTokenKeepsTheValue);
        Run("a token that fired first cancels and leaves nothing live parked", FiredTokenLeavesNothingParked);
        Run("a token does not collect a waiter per rendezvous", TokenWaitersStayBounded);
        Run("a fiber reuses one registration across its syncs", RegistrationIsReused);
        Run("every parked fiber raises exactly once when the token fires", AllParkedFibersRaiseOnce);
        Run("cancelling a running ping-pong never hangs or resumes twice", CancelRacesWithParking);
        Run("a cancel that lands while a fiber is parking is not lost", CancelDuringAParkIsNotLost);
        Run("two fibers sharing one environment both park and both cancel", SharedCellStillCancelsBoth);
    }

    // -----------------------------------------------------------------------
    // The ambient token
    // -----------------------------------------------------------------------
    //
    // `sync` races the event against the token bound to `current-cancel`, which
    // in a compiled program is the scope's. These drive that binding directly,
    // since there is no scope here to open.

    private static Promise<Unit> UnderToken(
        Promise<global::BjolangRuntime.CancelReason> token, Func<Fiber<Unit>> body) =>
        Bjo.Spawn<Unit>(async () =>
        {
            var saved = global::BjolangRuntime.parametersubpush_BANG(
                global::BjolangRuntime.currentsubcancel, token);
            try { return await body(); }
            finally { global::BjolangRuntime.dynsubrestore_BANG(saved); }
        });

    /// <summary>
    /// Park a choose under <paramref name="token"/>, fire the token, and report
    /// where the raise happened.
    /// </summary>
    private static (bool caught, int thread, string stack) CancelAParkedChoose(
        Promise<global::BjolangRuntime.CancelReason> token)
    {
        var a = new Channel<int>();
        var b = new Channel<int>();
        var done = new ManualResetEventSlim(false);

        bool caught = false;
        int thread = 0;
        string stack = "";

        _ = UnderToken(token, async () =>
        {
            try
            {
                await global::BjolangRuntime.sync(Cml.Choose<int>(a, b));
            }
            catch (Bjolang.Runtime.Cancelled e)
            {
                caught = true;
                thread = Environment.CurrentManagedThreadId;
                stack = e.StackTrace ?? "";
            }

            done.Set();
            return default;
        });

        AwaitPark(() => a.RawPendingReceiveCount, "the choose to park");
        AwaitPark(() => b.RawPendingReceiveCount, "the choose to park in both channels");

        token.TrySetResult(new global::BjolangRuntime.CancelReason.Requested("test"));
        Await(done, "the cancelled choose to resume");

        return (caught, thread, stack);
    }

    /// <summary>
    /// The reason is carried to the awaiter and raised in <c>GetResult</c>, so the
    /// throw unwinds the fiber's own state machine. Raising it where the token is
    /// completed would unwind a promise's completion walk instead, and be
    /// swallowed by the scheduler's catch-all.
    /// </summary>
    private static void ParkedChooseRaisesOnItsOwnStack()
    {
        var token = new Promise<global::BjolangRuntime.CancelReason>();
        var (caught, _, stack) = CancelAParkedChoose(token);

        Assert(caught, "a parked choose under a fired token did not raise Cancelled");
        Assert(stack.Contains("MoveNext"),
            $"the raise did not come from the fiber's state machine; stack was:\n{stack}");
        Assert(!stack.Contains("Promise") && !stack.Contains("Complete"),
            $"the raise came out of a promise completion walk; stack was:\n{stack}");
    }

    private static void CancelRunsNothingOnTheSignaller()
    {
        var token = new Promise<global::BjolangRuntime.CancelReason>();
        int signaller = Environment.CurrentManagedThreadId;
        var (caught, thread, _) = CancelAParkedChoose(token);

        Assert(caught, "a parked choose under a fired token did not raise Cancelled");
        Assert(thread != signaller,
            "the cancelled fiber resumed on the thread that fired the token");
    }

    /// <summary>
    /// Publish order is priority and the token is published last, so a branch
    /// that is available at publish time wins even though the token has fired.
    /// The rendezvous happened; throwing the value away would lose a delivered
    /// message.
    /// </summary>
    private static void ValueBeatsFiredToken()
    {
        var token = new Promise<global::BjolangRuntime.CancelReason>();
        var a = new Channel<int>();
        var b = new Channel<int>();

        _ = Bjo.Spawn<Unit>(async () => { await a.Send(7); return default; });
        AwaitPark(() => a.RawPendingSendCount, "the sender to park");

        token.TrySetResult(new global::BjolangRuntime.CancelReason.Requested("before the sync"));

        var done = new ManualResetEventSlim(false);
        int got = -1;
        bool cancelled = false;

        _ = UnderToken(token, async () =>
        {
            try { got = await global::BjolangRuntime.sync(Cml.Choose<int>(a, b)); }
            catch (Bjolang.Runtime.Cancelled) { cancelled = true; }
            done.Set();
            return default;
        });

        Await(done, "the sync to finish");
        Assert(!cancelled, "an available branch lost to a token that had already fired");
        AssertEqual(7, got, "the delivered value");
    }

    private static void LateTokenKeepsTheValue()
    {
        var token = new Promise<global::BjolangRuntime.CancelReason>();
        var a = new Channel<int>();
        var b = new Channel<int>();

        var done = new ManualResetEventSlim(false);
        int got = -1;
        bool cancelled = false;

        _ = UnderToken(token, async () =>
        {
            try { got = await global::BjolangRuntime.sync(Cml.Choose<int>(a, b)); }
            catch (Bjolang.Runtime.Cancelled) { cancelled = true; }
            done.Set();
            return default;
        });

        AwaitPark(() => a.RawPendingReceiveCount, "the choose to park");

        // Returns once the rendezvous has committed, so the token below is
        // strictly later than the commit.
        Cml.Sync(new ChannelSendEvent<int>(a, 11), _ => { });
        token.TrySetResult(new global::BjolangRuntime.CancelReason.Requested("after the commit"));

        Await(done, "the committed sync to resume");
        Assert(!cancelled, "a token fired after the commit took the sync back");
        AssertEqual(11, got, "the committed value");
    }

    /// <summary>
    /// The branches are published before the token, so they do park; what must
    /// not survive is a LIVE op, which would hold a message nobody will ever
    /// read.
    /// </summary>
    private static void FiredTokenLeavesNothingParked()
    {
        var token = new Promise<global::BjolangRuntime.CancelReason>();
        token.TrySetResult(new global::BjolangRuntime.CancelReason.Requested("already cancelled"));

        var a = new Channel<int>();
        var b = new Channel<int>();
        var done = new ManualResetEventSlim(false);
        bool cancelled = false;

        _ = UnderToken(token, async () =>
        {
            try { await global::BjolangRuntime.sync(Cml.Choose<int>(a, b)); }
            catch (Bjolang.Runtime.Cancelled) { cancelled = true; }
            done.Set();
            return default;
        });

        Await(done, "the sync under a fired token to finish");
        Assert(cancelled, "a sync under a token that had already fired did not raise");

        Assert(!a.TryDirectSend(1), "a live receive was left parked in the first branch");
        Assert(!b.TryDirectSend(2), "a live receive was left parked in the second branch");
    }

    /// <summary>
    /// Nothing removes a waiter from a promise: the token's list is pruned
    /// amortised, using <c>IsAbandoned</c>. Without that, a long-lived scope
    /// collects one waiter per rendezvous of every fiber under it.
    /// </summary>
    private static void TokenWaitersStayBounded()
    {
        AssertEqual(WaitersAfter(500), WaitersAfter(4000),
            "the token's waiter list grew with the number of rendezvous");
    }

    /// <summary>
    /// A sync on one channel operation borrows the fiber's own registration and
    /// gives it back, so a fiber in a loop registers once rather than once per
    /// park. Two fibers, so two registrations, and the count must not follow the
    /// rendezvous count at all.
    /// </summary>
    private static void RegistrationIsReused()
    {
        var token = new Promise<global::BjolangRuntime.CancelReason>();
        var ch = new Channel<int>();

        var receiver = UnderToken(token, async () =>
        {
            for (int i = 0; i < 4000; i++) await global::BjolangRuntime.sync((IEvent<int>)ch);
            return default;
        });

        var sender = UnderToken(token, async () =>
        {
            for (int i = 0; i < 4000; i++)
                await global::BjolangRuntime.sync(global::BjolangRuntime.chansubsend(ch, i));
            return default;
        });

        receiver.ToTask().GetAwaiter().GetResult();
        sender.ToTask().GetAwaiter().GetResult();

        int waiters = token.RawWaiterCount;
        Assert(waiters <= 4, $"4000 rendezvous left {waiters} registrations on the token");
    }

    private static void AllParkedFibersRaiseOnce()
    {
        const int N = 200;

        var token = new Promise<global::BjolangRuntime.CancelReason>();
        var channels = new Channel<int>[N];
        var done = new CountdownEvent(N);
        int raised = 0;
        int delivered = 0;

        for (int i = 0; i < N; i++)
        {
            var ch = channels[i] = new Channel<int>();
            _ = UnderToken(token, async () =>
            {
                try
                {
                    await global::BjolangRuntime.sync((IEvent<int>)ch);
                    Interlocked.Increment(ref delivered);
                }
                catch (Bjolang.Runtime.Cancelled) { Interlocked.Increment(ref raised); }

                done.Signal();
                return default;
            });
        }

        for (int i = 0; i < N; i++) AwaitPark(() => channels[i].RawPendingReceiveCount, $"fiber {i} to park");

        token.TrySetResult(new global::BjolangRuntime.CancelReason.Requested("all of you"));

        Assert(done.Wait(10_000), $"only {raised + delivered} of {N} fibers came back");
        AssertEqual(N, raised, "fibers that raised Cancelled");
        AssertEqual(0, delivered, "fibers that were handed a value");
    }

    /// <summary>
    /// The lost-wake-up hunt: fire the token while a rendezvous loop is running,
    /// so that the cancel lands in the window between a fiber checking the token
    /// and publishing its park. A miss shows up as a fiber that never comes back,
    /// which the harness reports as a timeout.
    /// </summary>
    private static void CancelRacesWithParking()
    {
        for (int trial = 0; trial < 60; trial++)
        {
            var token = new Promise<global::BjolangRuntime.CancelReason>();
            var ch = new Channel<int>();
            var receiverDone = new ManualResetEventSlim(false);
            var senderDone = new ManualResetEventSlim(false);
            int received = 0;

            _ = UnderToken(token, async () =>
            {
                try
                {
                    while (true)
                    {
                        await global::BjolangRuntime.sync((IEvent<int>)ch);
                        received++;
                    }
                }
                catch (Bjolang.Runtime.Cancelled) { }

                receiverDone.Set();
                return default;
            });

            _ = UnderToken(token, async () =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++)
                        await global::BjolangRuntime.sync(global::BjolangRuntime.chansubsend(ch, i));
                }
                catch (Bjolang.Runtime.Cancelled) { }

                senderDone.Set();
                return default;
            });

            if ((trial & 1) == 0) Thread.Sleep(trial % 4);
            token.TrySetResult(new global::BjolangRuntime.CancelReason.Requested($"trial {trial}"));

            Await(receiverDone, $"the receiver of trial {trial} ({received} received)");
            Await(senderDone, $"the sender of trial {trial}");
        }
    }

    /// <summary>
    /// The window between a fiber reading the token and publishing its park.
    ///
    /// A cancel that lands in there is delivered by neither side unless the park
    /// re-reads the token after arming: the token's walk of its waiters sees a
    /// park that is not published yet, and the fiber goes on to park for good.
    /// These fibers park on channels no one will ever send to, so a miss is a
    /// fiber that never comes back rather than one that is rescued by the next
    /// rendezvous. The spins spread the fires across the window.
    /// </summary>
    private static void CancelDuringAParkIsNotLost()
    {
        const int Trials = 300;
        const int N = 8;

        for (int trial = 0; trial < Trials; trial++)
        {
            var token = new Promise<global::BjolangRuntime.CancelReason>();
            var done = new CountdownEvent(N);

            for (int i = 0; i < N; i++)
            {
                var ch = new Channel<int>();
                int spin = (trial * 7 + i * 13) % 97;

                _ = UnderToken(token, async () =>
                {
                    for (int s = 0; s < spin; s++) Thread.SpinWait(1);

                    try { await global::BjolangRuntime.sync((IEvent<int>)ch); }
                    catch (Bjolang.Runtime.Cancelled) { }

                    done.Signal();
                    return default;
                });
            }

            Thread.SpinWait((trial * 31) % 4096);
            token.TrySetResult(new global::BjolangRuntime.CancelReason.Requested($"trial {trial}"));

            Assert(done.Wait(5000),
                $"trial {trial}: {done.CurrentCount} of {N} fibers parked through the cancel");
        }
    }

    /// <summary>
    /// A child spawned straight from `Bjo.Spawn` inherits its parent's
    /// environment, and therefore its cell. Only one of them can hold it, so the
    /// other takes a watch of its own — and cancellation has to reach both.
    /// </summary>
    private static void SharedCellStillCancelsBoth()
    {
        var token = new Promise<global::BjolangRuntime.CancelReason>();
        var warmup = new Channel<int>();
        var a = new Channel<int>();
        var b = new Channel<int>();
        var done = new CountdownEvent(2);
        int raised = 0;

        _ = UnderToken(token, async () =>
        {
            // Builds the cell on this fiber's environment, which the child below
            // then inherits.
            _ = Bjo.Spawn<Unit>(async () =>
            {
                await warmup.Send(1);
                return default;
            });
            await global::BjolangRuntime.sync((IEvent<int>)warmup);

            _ = Bjo.Spawn<Unit>(async () =>
            {
                try { await global::BjolangRuntime.sync((IEvent<int>)b); }
                catch (Bjolang.Runtime.Cancelled) { Interlocked.Increment(ref raised); }

                done.Signal();
                return default;
            });

            try { await global::BjolangRuntime.sync((IEvent<int>)a); }
            catch (Bjolang.Runtime.Cancelled) { Interlocked.Increment(ref raised); }

            done.Signal();
            return default;
        });

        AwaitPark(() => a.RawPendingReceiveCount, "the parent to park");
        AwaitPark(() => b.RawPendingReceiveCount, "the child to park");

        token.TrySetResult(new global::BjolangRuntime.CancelReason.Requested("both of you"));

        Assert(done.Wait(10_000), $"only {raised} of 2 fibers came back");
        AssertEqual(2, raised, "fibers that raised Cancelled");
    }

    private static int WaitersAfter(int rendezvous)
    {
        var token = new Promise<global::BjolangRuntime.CancelReason>();
        var ch = new Channel<int>();
        var idle = new Channel<int>();

        var receiver = UnderToken(token, async () =>
        {
            for (int i = 0; i < rendezvous; i++)
                await global::BjolangRuntime.sync(Cml.Choose<int>(ch, idle));
            return default;
        });

        var sender = UnderToken(token, async () =>
        {
            for (int i = 0; i < rendezvous; i++)
                await global::BjolangRuntime.sync(global::BjolangRuntime.chansubsend(ch, i));
            return default;
        });

        receiver.ToTask().GetAwaiter().GetResult();
        sender.ToTask().GetAwaiter().GetResult();

        // Both fibers are done, so every waiter still on the list is abandoned;
        // what is asserted is that the count does not scale with the workload.
        return token.RawWaiterCount <= 64 ? 0 : token.RawWaiterCount;
    }

    // -----------------------------------------------------------------------
    // Timers
    // -----------------------------------------------------------------------

    private static void TimeoutFires()
    {
        var done = new ManualResetEventSlim(false);
        Cml.Sync(Cml.Timeout(50), _ => done.Set());
        Await(done, "a 50 ms timeout");
    }

    /// <summary>
    /// The point of a timeout is to lose most of the time. It goes second
    /// because `choose` stops publishing at the first available branch, and a
    /// branch that is never published cannot be the one under test.
    /// </summary>
    private static void TimeoutLoses()
    {
        var result = new ManualResetEventSlim(false);
        string? got = null;

        Cml.Sync(
            Cml.Choose(
                Cml.Wrap(Cml.Timeout(5000), _ => "timeout"),
                Cml.Wrap(Cml.Always(1), _ => "value")),
            v => { got = v; result.Set(); });

        Await(result, "the choose to commit");
        AssertEqual("value", got, "the wrong branch won");
    }

    /// <summary>
    /// Built once and synced twice, a relative timeout must wait the full
    /// interval BOTH times. Without the guard the deadline would be in the past
    /// by the second sync — which inside a `choose` loop means the timeout
    /// branch wins every iteration after the first, silently.
    /// </summary>
    private static void TimeoutIsRebuiltPerSync()
    {
        var ev = Cml.Timeout(120);

        var first = new ManualResetEventSlim(false);
        Cml.Sync(ev, _ => first.Set());
        Await(first, "the first sync");

        var start = DateTime.UtcNow;
        var second = new ManualResetEventSlim(false);
        Cml.Sync(ev, _ => second.Set());
        Await(second, "the second sync");

        var waited = (DateTime.UtcNow - start).TotalMilliseconds;
        Assert(waited > 60, $"the second sync returned after {waited:F0} ms, so the deadline was not rebuilt");
    }

    /// <summary>
    /// The other half of the same distinction. An absolute deadline is fixed
    /// when it is built, so a second sync after it has passed is available at
    /// once — which is what makes it usable as a budget for a whole loop.
    /// </summary>
    private static void AtIsAbsolute()
    {
        var ev = Cml.At(DateTime.UtcNow.AddMilliseconds(120));

        var first = new ManualResetEventSlim(false);
        Cml.Sync(ev, _ => first.Set());
        Await(first, "the deadline");

        var start = DateTime.UtcNow;
        var second = new ManualResetEventSlim(false);
        Cml.Sync(ev, _ => second.Set());
        Await(second, "the second sync");

        var waited = (DateTime.UtcNow - start).TotalMilliseconds;
        Assert(waited < 60, $"the second sync waited {waited:F0} ms, so the deadline restarted");
    }

    private static void AtInThePastFiresNow()
    {
        var done = new ManualResetEventSlim(false);
        Cml.Sync(Cml.At(DateTime.UtcNow.AddSeconds(-10)), _ => done.Set());
        Await(done, "a deadline that has already passed", 1000);
    }

    /// <summary>
    /// 500 sync blocks in which a 1 ms timeout is armed and races a promise
    /// completed from another thread. The deadline and the loss race constantly,
    /// so both orders of Fire vs Cancel are exercised. Each block must commit
    /// exactly once, and no late timer callback may leak into a later block —
    /// the fresh-TimeoutNode-per-arm design is what makes the latter impossible
    /// (a pooled node's cancel delegate could be fired by a PREVIOUS sync's nack,
    /// which has no CAS gate); this is the regression test for that choice.
    /// </summary>
    private static void CancelledTimeoutStaysQuiet()
    {
        int count = 0;
        for (int i = 0; i < 500; i++)
        {
            var done = new ManualResetEventSlim(false);
            var p = new Promise<int>();
            ThreadPool.UnsafeQueueUserWorkItem(_ => p.TrySetResult(1), null);

            Cml.Sync(
                Cml.Choose(
                    Cml.Wrap(p.Join(), static _ => 1),
                    Cml.Wrap(Cml.Timeout(1), static _ => 2)),
                _ => { Interlocked.Increment(ref count); done.Set(); });

            Await(done, $"sync {i}");
        }

        Thread.Sleep(100);   // let any stray timer callbacks run
        AssertEqual(500, count, "each sync must commit exactly once");
    }

    private static void SpecTimeoutFires()
    {
        var done = new ManualResetEventSlim(false);
        Cml.Sync(Cml.TimeoutViaCombinators(50), _ => done.Set());
        Await(done, "a 50 ms combinator timeout");
    }

    private static void SpecTimeoutLoses()
    {
        var result = new ManualResetEventSlim(false);
        string? got = null;

        Cml.Sync(
            Cml.Choose(
                Cml.Wrap(Cml.TimeoutViaCombinators(5000), static _ => "timeout"),
                Cml.Wrap(Cml.Always(1), static _ => "value")),
            v => { got = v; result.Set(); });

        Await(result, "the choose to commit");
        AssertEqual("value", got, "the wrong branch won");
    }

    // -----------------------------------------------------------------------
    // Choice order
    // -----------------------------------------------------------------------

    /// <summary>
    /// Publish order is priority: with randomization off, the first ready
    /// branch wins every time. This is a documented semantic, not an accident —
    /// pin it so a change to the publish loop cannot silently alter it.
    /// </summary>
    private static void ChooseIsDeterministicByDefault()
    {
        for (int i = 0; i < 100; i++)
        {
            string? got = null;
            var done = new ManualResetEventSlim(false);
            Cml.Sync(
                Cml.Choose(Cml.Always("first"), Cml.Always("second")),
                v => { got = v; done.Set(); });
            Await(done, "the choose");
            AssertEqual("first", got, "publish order is priority; the first ready branch must win");
        }
    }

    /// <summary>
    /// With randomization on, every always-ready branch must eventually win.
    /// Four branches over up to 400 syncs: the chance of missing one by luck is
    /// (3/4)^400 per branch, i.e. zero for test purposes.
    /// </summary>
    private static void RandomizedChooseIsFair()
    {
        Cml.RandomizeChoice = true;
        try
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < 400 && seen.Count < 4; i++)
            {
                string? got = null;
                var done = new ManualResetEventSlim(false);
                Cml.Sync(
                    Cml.Choose(Cml.Always("a"), Cml.Always("b"), Cml.Always("c"), Cml.Always("d")),
                    v => { got = v; done.Set(); });
                Await(done, "the choose");
                seen.Add(got!);
            }
            AssertEqual(4, seen.Count, "with randomization every always-ready branch must eventually win");
        }
        finally
        {
            Cml.RandomizeChoice = false;
        }
    }

    // -----------------------------------------------------------------------
    // Detach / Forward
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detaching is what "throw away this handle" has to mean for a promise.
    /// Dropping the reference would lose the exception inside it silently,
    /// because nothing else is watching.
    /// </summary>
    private static void DetachReportsFailure()
    {
        var reported = new ManualResetEventSlim(false);
        Exception? seen = null;

        var previous = Scheduler.UnhandledException;
        Scheduler.UnhandledException = ex => { seen = ex; reported.Set(); };

        try
        {
            var p = new Promise<int>();
            p.Detach();
            p.TrySetException(new InvalidOperationException("boom"));

            Await(reported, "the detached failure to be reported");
            Assert(seen is InvalidOperationException, $"reported the wrong exception: {seen}");
            AssertEqual("boom", seen!.Message, "the exception lost its message");
        }
        finally
        {
            Scheduler.UnhandledException = previous;
        }
    }

    private static void DetachIsQuietOnSuccess()
    {
        var reported = new ManualResetEventSlim(false);

        var previous = Scheduler.UnhandledException;
        Scheduler.UnhandledException = _ => reported.Set();

        try
        {
            var p = new Promise<int>();
            p.Detach();
            p.TrySetResult(7);

            AssertNoSignal(reported, "a successful detached promise reporting a failure");
        }
        finally
        {
            Scheduler.UnhandledException = previous;
        }
    }

    /// <summary>
    /// What a child cancellation token is built out of: cancelling a parent
    /// scope has to cancel everything under it.
    /// </summary>
    private static void ForwardPipesOutcome()
    {
        var parent = new Promise<int>();
        var child = new Promise<int>();
        parent.Forward(child);

        Assert(!child.IsCompleted, "the child landed before the parent");

        parent.TrySetResult(42);

        var done = new ManualResetEventSlim(false);
        int got = 0;
        Cml.Sync(child.Join(), r => { got = r.Value; done.Set(); });

        Await(done, "the forwarded outcome");
        AssertEqual(42, got, "the child got the wrong value");
    }

    // -----------------------------------------------------------------------
    // B3
    // -----------------------------------------------------------------------

    /// <summary>
    /// Before the fix this never returned: the receive branch dequeued the send
    /// branch's own op, claimed the shared state W-&gt;C, then failed to drive that
    /// same state W-&gt;S, reset, re-queued and retried forever. Reaching the end of
    /// this method at all is the assertion.
    /// </summary>
    private static void SelfChooseTerminates()
    {
        var ch = new Channel<int>();
        var published = new ManualResetEventSlim(false);

        Cml.Sync(
            Cml.Choose(
                Cml.Wrap(new ChannelSendEvent<int>(ch, 42), _ => "sent"),
                Cml.Wrap(new ChannelReceiveEvent<int>(ch), v => $"got {v}")),
            _ => { });

        published.Set();
        Await(published, "publish to return", 1000);
    }

    /// <summary>
    /// The self-choose must not merely avoid spinning, it must stay live: both of
    /// its branches remain genuinely offered to other threads.
    /// </summary>
    private static void SelfChoosePairsExternally()
    {
        var ch = new Channel<int>();
        var chooserDone = new ManualResetEventSlim(false);
        var partnerDone = new ManualResetEventSlim(false);
        string? chooserResult = null;
        int received = -1;

        Cml.Sync(
            Cml.Choose(
                Cml.Wrap(new ChannelSendEvent<int>(ch, 42), _ => "sent"),
                Cml.Wrap(new ChannelReceiveEvent<int>(ch), v => $"got {v}")),
            r => { chooserResult = r; chooserDone.Set(); });

        // An outside receiver should take the send branch.
        Cml.Sync(new ChannelReceiveEvent<int>(ch), v => { received = v; partnerDone.Set(); });

        Await(chooserDone, "the self-choose to commit");
        Await(partnerDone, "the external receiver to commit");

        AssertEqual("sent", chooserResult, "self-choose picked the wrong branch");
        AssertEqual(42, received, "partner received the wrong value");
    }

    /// <summary>Two sync blocks that each offer both directions must pair with each other.</summary>
    private static void SymmetricSelfChooses()
    {
        var ch = new Channel<int>();
        var first = new ManualResetEventSlim(false);
        var second = new ManualResetEventSlim(false);

        Cml.Sync(
            Cml.Choose(
                Cml.Wrap(new ChannelSendEvent<int>(ch, 1), _ => "sent"),
                Cml.Wrap(new ChannelReceiveEvent<int>(ch), v => $"got {v}")),
            _ => first.Set());

        Cml.Sync(
            Cml.Choose(
                Cml.Wrap(new ChannelSendEvent<int>(ch, 2), _ => "sent"),
                Cml.Wrap(new ChannelReceiveEvent<int>(ch), v => $"got {v}")),
            _ => second.Set());

        Await(first, "the first symmetric choose");
        Await(second, "the second symmetric choose");
    }

    // -----------------------------------------------------------------------
    // B1
    // -----------------------------------------------------------------------

    /// <summary>
    /// choose(withNack(choose(recvA, recvB)), recvC), and A wins.
    ///
    /// The winning id is a grandchild of the withNack's id. With a flat id set the
    /// winner simply "is not my id", so the nack fired even though the withNack's
    /// own subtree committed — in a server protocol, cancelling the very request
    /// you just succeeded at.
    /// </summary>
    private static void NackSilentWhenSubtreeWins()
    {
        var a = new Channel<int>();
        var b = new Channel<int>();
        var c = new Channel<int>();

        var nackFired = new ManualResetEventSlim(false);
        var done = new ManualResetEventSlim(false);
        string? result = null;

        Cml.Sync(
            Cml.Choose(
                Cml.WithNack(nack =>
                {
                    Cml.Sync(nack, _ => nackFired.Set());
                    return Cml.Choose(
                        Cml.Wrap(new ChannelReceiveEvent<int>(a), v => $"a{v}"),
                        Cml.Wrap(new ChannelReceiveEvent<int>(b), v => $"b{v}"));
                }),
                Cml.Wrap(new ChannelReceiveEvent<int>(c), v => $"c{v}")),
            r => { result = r; done.Set(); });

        Cml.Sync(new ChannelSendEvent<int>(a, 1), _ => { });

        Await(done, "the choose to commit");
        AssertEqual("a1", result, "wrong branch won");
        AssertNoSignal(nackFired, "the nack of the winning withNack subtree fired");
    }

    private static void NackFiresWhenOutsideBranchWins()
    {
        var a = new Channel<int>();
        var b = new Channel<int>();
        var c = new Channel<int>();

        var nackFired = new ManualResetEventSlim(false);
        var done = new ManualResetEventSlim(false);
        string? result = null;

        Cml.Sync(
            Cml.Choose(
                Cml.WithNack(nack =>
                {
                    Cml.Sync(nack, _ => nackFired.Set());
                    return Cml.Choose(
                        Cml.Wrap(new ChannelReceiveEvent<int>(a), v => $"a{v}"),
                        Cml.Wrap(new ChannelReceiveEvent<int>(b), v => $"b{v}"));
                }),
                Cml.Wrap(new ChannelReceiveEvent<int>(c), v => $"c{v}")),
            r => { result = r; done.Set(); });

        Cml.Sync(new ChannelSendEvent<int>(c, 9), _ => { });

        Await(done, "the choose to commit");
        AssertEqual("c9", result, "wrong branch won");
        Await(nackFired, "the losing withNack subtree's nack");
    }

    // -----------------------------------------------------------------------
    // B2
    // -----------------------------------------------------------------------

    /// <summary>
    /// withNack publishes its generated event with its OWN id, so a withNack nested
    /// (through a wrap) inside another registers a second nack under the same id.
    /// Keying nacks by id meant the inner one overwrote the outer one and the outer
    /// nack was silently never fired.
    /// </summary>
    private static void NestedNacksBothFire()
    {
        var a = new Channel<int>();
        var b = new Channel<int>();

        var outerFired = new ManualResetEventSlim(false);
        var innerFired = new ManualResetEventSlim(false);
        var done = new ManualResetEventSlim(false);

        Cml.Sync(
            Cml.Choose(
                Cml.WithNack(outer =>
                {
                    Cml.Sync(outer, _ => outerFired.Set());
                    return Cml.Wrap(
                        Cml.WithNack(inner =>
                        {
                            Cml.Sync(inner, _ => innerFired.Set());
                            return new ChannelReceiveEvent<int>(a);
                        }),
                        v => $"a{v}");
                }),
                Cml.Wrap(new ChannelReceiveEvent<int>(b), v => $"b{v}")),
            _ => done.Set());

        Cml.Sync(new ChannelSendEvent<int>(b, 7), _ => { });

        Await(done, "the choose to commit");
        Await(innerFired, "the inner nack");
        Await(outerFired, "the outer nack (overwritten by the inner one before the fix)");
    }

    // -----------------------------------------------------------------------
    // B4 / B10
    // -----------------------------------------------------------------------

    private static void AlwaysWinsChoose()
    {
        var idle = new Channel<int>();
        var done = new ManualResetEventSlim(false);
        string? result = null;

        Cml.Sync(
            Cml.Choose(
                Cml.Wrap(new ChannelReceiveEvent<int>(idle), v => $"chan{v}"),
                Cml.Always("always")),
            r => { result = r; done.Set(); });

        Await(done, "Always to commit");
        AssertEqual("always", result, "Always did not win against an idle channel");
    }

    /// <summary>
    /// Always is published second, after a channel branch has already parked an
    /// operation. It must still commit rather than find the state busy and vanish.
    /// </summary>
    private static void AlwaysAfterParkedBranch()
    {
        var idle = new Channel<int>();

        for (int i = 0; i < 200; i++)
        {
            var done = new ManualResetEventSlim(false);
            Cml.Sync(
                Cml.Choose(
                    Cml.Wrap(new ChannelReceiveEvent<int>(idle), v => $"chan{v}"),
                    Cml.Always("always")),
                _ => done.Set());

            Await(done, $"Always to commit on iteration {i}", 2000);
        }
    }

    // -----------------------------------------------------------------------
    // B7
    // -----------------------------------------------------------------------

    /// <summary>
    /// Run <paramref name="iterations"/> chooses in which the `idle` branch always
    /// loses, and return how many dead entries it is left holding.
    ///
    /// Reads <c>RawPendingReceiveCount</c>, not <c>PendingReceiveCount</c>: the latter
    /// cleans before counting, so it reports 0 whether or not the bug is present and
    /// cannot witness a leak at all.
    /// </summary>
    private static int StrandedAfter(int iterations)
    {
        var busy = new Channel<int>();
        var idle = new Channel<int>();

        for (int i = 0; i < iterations; i++)
        {
            var done = new ManualResetEventSlim(false);

            Cml.Sync(
                Cml.Choose(
                    new ChannelReceiveEvent<int>(busy),
                    new ChannelReceiveEvent<int>(idle)),
                _ => done.Set());

            Cml.Sync(new ChannelSendEvent<int>(busy, i), _ => { });
            Await(done, $"iteration {i}", 2000);
        }

        return idle.RawPendingReceiveCount;
    }

    // ---- the direct park ---------------------------------------------------
    //
    // A sync that offers one channel operation has nothing to arbitrate and no
    // nack to fire, so it parks an op with no SyncState and commits
    // unconditionally when a partner arrives. That is only sound while such an
    // op can never be a branch of a choose, because an unconditional commit
    // cannot be withdrawn.
    //
    // The invariant is structural — SyncDirect is reachable only from the top of
    // a sync, never from Publish — so these assert it where it would break.

    /// <summary>Park, then wait for the park to be visible in the channel.</summary>
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

    private static void BareSyncParksDirectly()
    {
        var ch = new Channel<int>();

        // Through the event awaiter, which is the path `(sync ev)` compiles to.
        // `await ch` would take the struct awaiter instead, which is a different
        // entry point to the same parked shape.
        _ = Bjo.Spawn<int>(async () => await (IEvent<int>)ch);

        AwaitPark(() => ch.RawPendingReceiveCount, "the receive to park");

        AssertEqual(1, ch.RawDirectReceiveCount,
            "a sync offering one channel operation must park without a SyncState");

        // And it still pairs.
        Cml.Sync(new ChannelSendEvent<int>(ch, 7), _ => { });
    }

    private static void ChooseNeverParksDirectly()
    {
        var a = new Channel<int>();
        var b = new Channel<int>();

        _ = Bjo.Spawn<int>(async () => await Cml.Choose<int>(a, b));

        AwaitPark(() => a.RawPendingReceiveCount, "the choose to park");
        AwaitPark(() => b.RawPendingReceiveCount, "the choose to park in both");

        AssertEqual(0, a.RawDirectReceiveCount,
            "a choose branch parked directly, and a direct op cannot be withdrawn");
        AssertEqual(0, b.RawDirectReceiveCount,
            "a choose branch parked directly, and a direct op cannot be withdrawn");

        Cml.Sync(new ChannelSendEvent<int>(a, 1), _ => { });
    }

    /// <summary>
    /// The two protocols have to interoperate in both directions, because which
    /// side of a rendezvous is direct is decided independently by each side.
    /// </summary>
    private static void DirectPairsWithChooseSend()
    {
        var ch = new Channel<int>();
        var other = new Channel<int>();
        var got = new ManualResetEventSlim(false);
        int seen = 0;

        _ = Bjo.Spawn<int>(async () =>
        {
            seen = await (IEvent<int>)ch;
            got.Set();
            return seen;
        });

        AwaitPark(() => ch.RawPendingReceiveCount, "the direct receive to park");
        AssertEqual(1, ch.RawDirectReceiveCount, "the receive was not direct");

        // A send offered under a choose, so it arrives with a SyncState.
        Cml.Sync(
            Cml.Choose(
                new ChannelSendEvent<int>(ch, 99),
                new ChannelSendEvent<int>(other, 0)),
            _ => { });

        Await(got, "the directly parked receive to be resumed by a choose send");
        AssertEqual(99, seen, "the value did not survive the pairing");
    }

    private static void DirectSendPairsWithChooseReceive()
    {
        var ch = new Channel<int>();
        var other = new Channel<int>();
        var sent = new ManualResetEventSlim(false);

        _ = Bjo.Spawn<Unit>(async () =>
        {
            await (IEvent<Unit>)new ChannelSendEvent<int>(ch, 55);
            sent.Set();
            return default;
        });

        AwaitPark(() => ch.RawPendingSendCount, "the direct send to park");
        AssertEqual(1, ch.RawDirectSendCount, "the send was not direct");

        int seen = 0;
        var got = new ManualResetEventSlim(false);
        Cml.Sync(
            Cml.Choose(
                new ChannelReceiveEvent<int>(ch),
                new ChannelReceiveEvent<int>(other)),
            v => { seen = v; got.Set(); });

        Await(got, "the choose receive to take the directly parked send");
        Await(sent, "the directly parked send to be resumed");
        AssertEqual(55, seen, "the value did not survive the pairing");
    }

    /// <summary>
    /// The core B7 assertion. Cleanup is triggered by parks and only runs past a
    /// threshold, so the guarantee is BOUNDED, not zero — a handful of recent losers
    /// are legitimately still parked at any moment.
    ///
    /// Boundedness is therefore tested the only way it can honestly be tested: by
    /// asking whether the residue grows with the workload. B7 was unbounded growth, so
    /// eight times the iterations producing the same small residue is the fix.
    /// </summary>
    private static void StaleOpsStayBounded()
    {
        int few = StrandedAfter(500);
        int many = StrandedAfter(4000);

        Assert(many <= 128,
            $"4000 losing branches left {many} stranded, past the expected bound");

        Assert(many <= few + 64,
            $"stranding grows with the iteration count: 500 -> {few}, 4000 -> {many}");
    }

    /// <summary>
    /// The accepted cost of the trade, pinned so nobody widens it by accident.
    ///
    /// A channel offered in exactly one choose and then abandoned never parks again, so
    /// its park counter never reaches the sweep threshold and its single loser stays
    /// put. One entry per abandoned channel is bounded and collectable with the channel
    /// itself; what would NOT be acceptable is that number rising.
    /// </summary>
    private static void AbandonedChannelKeepsAtMostOne()
    {
        var busy = new Channel<int>();
        var idle = new Channel<int>();
        var done = new ManualResetEventSlim(false);

        Cml.Sync(
            Cml.Choose(
                new ChannelReceiveEvent<int>(busy),
                new ChannelReceiveEvent<int>(idle)),
            _ => done.Set());

        Cml.Sync(new ChannelSendEvent<int>(busy, 1), _ => { });
        Await(done, "the busy branch to win", 2000);

        Thread.Sleep(100);

        int stranded = idle.RawPendingReceiveCount;
        Assert(stranded <= 1,
            $"An abandoned channel kept {stranded} dead entries, expected at most 1");
    }

    /// <summary>
    /// The safety side of the fix, and the one that would catch an over-eager cleanup:
    /// an operation whose block has NOT committed is still live and must survive.
    /// Reclaiming it would lose a genuine waiter and hang the receiver forever.
    /// </summary>
    private static void LiveReceiveSurvivesCleanup()
    {
        var live = new Channel<int>();
        var busy = new Channel<int>();
        var received = new ManualResetEventSlim(false);
        int got = -1;

        // A plain receive that nobody will satisfy yet. This must stay parked.
        Cml.Sync(new ChannelReceiveEvent<int>(live), v => { got = v; received.Set(); });

        // Churn unrelated chooses so cleanup runs repeatedly against `live`.
        for (int i = 0; i < 50; i++)
        {
            var done = new ManualResetEventSlim(false);
            Cml.Sync(
                Cml.Choose(
                    new ChannelReceiveEvent<int>(busy),
                    new ChannelReceiveEvent<int>(live)),
                _ => done.Set());

            Cml.Sync(new ChannelSendEvent<int>(busy, i), _ => { });
            Await(done, $"iteration {i}", 2000);
        }

        // The original live receive must still be there and must still work.
        Cml.Sync(new ChannelSendEvent<int>(live, 99), _ => { });
        Await(received, "the still-parked receive to be satisfied", 2000);
        AssertEqual(99, got, "value delivered to the surviving receive");
    }

    /// <summary>
    /// withNack builds a deeper event tree, so the losing branch is published through
    /// a different path. Confirm registration still happens there.
    /// </summary>
    private static void WithNackLosersBounded()
    {
        const int iterations = 200;

        var busy = new Channel<int>();
        var idle = new Channel<int>();

        for (int i = 0; i < iterations; i++)
        {
            var done = new ManualResetEventSlim(false);

            Cml.Sync(
                Cml.Choose(
                    new ChannelReceiveEvent<int>(busy),
                    Cml.WithNack(nack =>
                    {
                        Cml.Sync(nack, _ => { });
                        return new ChannelReceiveEvent<int>(idle);
                    })),
                _ => done.Set());

            Cml.Sync(new ChannelSendEvent<int>(busy, i), _ => { });
            Await(done, $"iteration {i}", 2000);
        }

        int stranded = idle.RawPendingReceiveCount;
        Assert(stranded <= 128,
            $"withNack losing branches stranded {stranded} of {iterations}, past the expected bound");
    }

    // -----------------------------------------------------------------------
    // Sanity
    // -----------------------------------------------------------------------

    private static void WrapMapsValue()
    {
        var ch = new Channel<int>();
        var done = new ManualResetEventSlim(false);
        string? result = null;

        Cml.Sync(Cml.Wrap(new ChannelReceiveEvent<int>(ch), v => $"<{v}>"),
                 r => { result = r; done.Set(); });

        Cml.Sync(new ChannelSendEvent<int>(ch, 5), _ => { });

        Await(done, "the wrapped receive");
        AssertEqual("<5>", result, "wrap did not map the value");
    }

    private static void GuardIsDeferred()
    {
        int generated = 0;
        var guard = Cml.Guard(() => { generated++; return Cml.Always(1); });

        AssertEqual(0, generated, "guard ran before sync");

        var done = new ManualResetEventSlim(false);
        Cml.Sync(guard, _ => done.Set());

        Await(done, "the guarded event");
        AssertEqual(1, generated, "guard generator ran the wrong number of times");
    }
}
