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
using System.Threading.Tasks;
using static Bjoml.Tests.Harness;

namespace Bjoml.Tests;

public static class FiberTests
{
    public static void RunAll()
    {
        Section("Fiber basics");
        Run("a fiber with no await completes synchronously", NoAwaitCompletes);
        Run("a fiber suspends on a channel and resumes", SuspendAndResume);
        Run("two fibers ping-pong over channels", PingPong);
        Run("an exception surfaces at the await, not the spawn", ExceptionSurfacesAtAwait);

        Section("Dynamic context shim");
        Run("context survives a suspension resumed on another thread", ContextSurvivesSuspension);
        Run("a resuming fiber restores the borrowed thread's context", BorrowedThreadIsRestored);
        Run("a spawned fiber inherits the spawner's context", SpawnInheritsContext);
        Run("context changed between awaits is carried forward", ContextRecapturedAtEachSuspend);

        Section("ExecutionContext must NOT flow");
        Run("AsyncLocal does not survive a channel await", AsyncLocalDoesNotFlow);
        Run("AsyncLocal does not survive a C# Task await", AsyncLocalDoesNotFlowAcrossTask);

        Section("Promise");
        Run("promise join composes with choose", PromiseJoinInChoose);
        Run("TryCommit spins past a transient claim (B4)", TryCommitSpinsPastClaim);
        Run("a promise completed on a foreign thread wakes a choose", ForeignThreadCompletion);
        Run("losing choose branches do not accumulate waiters", WaitersArePruned);
        Run("the first completion wins the value, not the last", FirstCompletionWins);

        Section("Task interop");
        Run("FromTask bridges a completed task", FromTaskCompleted);
        Run("FromTask bridges a faulted task", FromTaskFaulted);
        Run("Cancellable cancels the token when it loses", CancellableCancelsOnLoss);
        Run("Cancellable does not cancel when it wins", CancellableSurvivesOnWin);
    }

    // -----------------------------------------------------------------------
    // Basics
    // -----------------------------------------------------------------------

    private static async Fiber<int> Constant(int v) => v;

    private static void NoAwaitCompletes()
    {
        int result = Bjo.RunToCompletion(() => Constant(7));
        AssertEqual(7, result, "fiber returned the wrong value");
    }

    private static async Fiber<int> ReceiveOnce(Channel<int> ch) => await ch.Receive();

    private static void SuspendAndResume()
    {
        var ch = new Channel<int>();
        var p = Bjo.Spawn(() => ReceiveOnce(ch));

        Cml.Sync(new ChannelSendEvent<int>(ch, 99), _ => { });

        AssertEqual(99, WaitFor(p), "fiber did not observe the sent value");
    }

    private static async Fiber Pinger(Channel<int> to, Channel<int> from, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            await to.Send(i);
            int back = await from.Receive();
            if (back != i * 2) throw new AssertionException($"ponger returned {back} for {i}");
        }
    }

    private static async Fiber Ponger(Channel<int> from, Channel<int> to, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            int v = await from.Receive();
            await to.Send(v * 2);
        }
    }

    private static void PingPong()
    {
        var a = new Channel<int>();
        var b = new Channel<int>();
        const int rounds = 1000;

        var ping = Bjo.Spawn(() => Pinger(a, b, rounds));
        var pong = Bjo.Spawn(() => Ponger(a, b, rounds));

        WaitFor(ping);
        WaitFor(pong);
    }

    private static async Fiber<int> Thrower()
    {
        await Cml.Always(0);
        throw new InvalidOperationException("boom");
    }

    private static void ExceptionSurfacesAtAwait()
    {
        var p = Bjo.Spawn(() => Thrower());

        try
        {
            WaitFor(p);
            throw new AssertionException("expected the fiber's exception to be rethrown");
        }
        catch (InvalidOperationException ex)
        {
            AssertEqual("boom", ex.Message, "wrong exception surfaced");
        }
    }

    // -----------------------------------------------------------------------
    // Dynamic context
    // -----------------------------------------------------------------------

    private sealed class Ctx
    {
        public readonly string Name;
        public Ctx(string name) => Name = name;
        public override string ToString() => Name;
    }

    /// <summary>
    /// The fiber parks on <paramref name="data"/> and is resumed by a foreign thread
    /// that has a DIFFERENT context installed. It must come back with its own.
    /// </summary>
    private static async Fiber<string> ContextProbe(Channel<int> data)
    {
        await data.Receive();
        return FiberContext.Current?.ToString() ?? "<null>";
    }

    private static void ContextSurvivesSuspension()
    {
        var data = new Channel<int>();

        FiberContext.Current = new Ctx("fiber-context");
        var p = Bjo.Spawn(() => ContextProbe(data));
        FiberContext.Current = null;

        AwaitSuspension(data);

        // Resume from a thread carrying somebody else's context entirely.
        RunOnForeignThread(new Ctx("stranger"), () =>
            Cml.Sync(new ChannelSendEvent<int>(data, 1), _ => { }));

        AssertEqual("fiber-context", WaitFor(p), "fiber resumed with the wrong dynamic context");
    }

    private static void BorrowedThreadIsRestored()
    {
        var data = new Channel<int>();

        FiberContext.Current = new Ctx("fiber-context");
        var p = Bjo.Spawn(() => ContextProbe(data));
        FiberContext.Current = null;

        AwaitSuspension(data);

        var stranger = new Ctx("stranger");
        object? afterResume = null;

        RunOnForeignThread(stranger, () =>
        {
            // Resuming the fiber happens inline inside this Sync.
            Cml.Sync(new ChannelSendEvent<int>(data, 1), _ => { });
            afterResume = FiberContext.Current;
        });

        WaitFor(p);

        Assert(ReferenceEquals(afterResume, stranger),
            $"the resuming thread's context was clobbered: expected <stranger>, got <{afterResume}>");
    }

    private static async Fiber<string> InheritProbe()
    {
        await Cml.Always(0);
        return FiberContext.Current?.ToString() ?? "<null>";
    }

    private static void SpawnInheritsContext()
    {
        FiberContext.Current = new Ctx("parent");
        var p = Bjo.Spawn(() => InheritProbe());
        FiberContext.Current = null;

        AssertEqual("parent", WaitFor(p), "spawned fiber did not inherit the dynamic context");
    }

    /// <summary>
    /// A fiber that changes its own context between two suspensions — which is what
    /// (parameterize ...) around an await compiles to — must see the NEW one after
    /// the second resume. This is why the box re-captures on every suspend rather
    /// than only when it is first created.
    /// </summary>
    private static async Fiber<string> RecaptureProbe(Channel<int> data)
    {
        await data.Receive();

        // This is what (parameterize ...) spanning an await compiles to.
        FiberContext.Current = new Ctx("second");

        await data.Receive();

        return FiberContext.Current?.ToString() ?? "<null>";
    }

    private static void ContextRecapturedAtEachSuspend()
    {
        var data = new Channel<int>();

        FiberContext.Current = new Ctx("first");
        var p = Bjo.Spawn(() => RecaptureProbe(data));
        FiberContext.Current = null;

        AwaitSuspension(data);
        RunOnForeignThread(new Ctx("stranger-a"), () =>
            Cml.Sync(new ChannelSendEvent<int>(data, 1), _ => { }));

        AwaitSuspension(data);
        RunOnForeignThread(new Ctx("stranger-b"), () =>
            Cml.Sync(new ChannelSendEvent<int>(data, 2), _ => { }));

        AssertEqual("second", WaitFor(p), "the fiber's own context change was not carried forward");
    }

    // -----------------------------------------------------------------------
    // ExecutionContext
    // -----------------------------------------------------------------------

    private static readonly AsyncLocal<string?> _ambient = new();

    private static int _ecSetThread;
    private static int _ecFinalThread;
    private static int _ecForeignThread;

    private static async Fiber<string> AsyncLocalProbe(Channel<int> data)
    {
        // Set INSIDE the fiber, so there genuinely is an ExecutionContext on this
        // thread that could be captured at the suspension point.
        _ambient.Value = "set-inside-fiber";
        _ecSetThread = Environment.CurrentManagedThreadId;

        await data.Receive();

        _ecFinalThread = Environment.CurrentManagedThreadId;
        return _ambient.Value ?? "<null>";
    }

    /// <summary>
    /// Documents the deliberate limitation: BjoML uses unsafe enqueues and unsafe
    /// awaits everywhere, so ExecutionContext — and therefore AsyncLocal, Activity
    /// spans and friends — does not flow across a BjoML suspension. Anything ambient
    /// the language needs belongs in FiberContext instead.
    /// </summary>
    private static void AsyncLocalDoesNotFlow()
    {
        // A fiber does not always suspend, even when the partner arrives "late".
        // EventAwaiter publishes the sync in its constructor and only then reports
        // IsCompleted, so a rendezvous that lands in that window completes the await
        // without ever creating a continuation, and the fiber runs straight on down
        // its own stack. That path is correct and is the common fast path — but it
        // is vacuous for this test, because a fiber that never left its thread has
        // obviously still got its own AsyncLocal.
        //
        // So: repeat until we observe a genuine cross-thread resume, and assert only
        // on those. Requiring at least one keeps the test from silently passing
        // because it never managed to suspend at all.
        const int attempts = 50;
        int genuineSuspensions = 0;

        for (int i = 0; i < attempts; i++)
        {
            var data = new Channel<int>();
            _ecSetThread = _ecFinalThread = _ecForeignThread = 0;

            var p = Bjo.Spawn(() => AsyncLocalProbe(data));

            AwaitSuspension(data);
            RunOnForeignThread(null, () =>
            {
                _ecForeignThread = Environment.CurrentManagedThreadId;
                Cml.Sync(new ChannelSendEvent<int>(data, 1), _ => { });
            });

            string observed = WaitFor(p);

            if (_ecFinalThread == _ecSetThread) continue;   // never suspended; vacuous

            genuineSuspensions++;

            Assert(observed == "<null>",
                "ExecutionContext flowed across a BjoML channel await; it must not. " +
                $"observed=<{observed}> setThread={_ecSetThread} finalThread={_ecFinalThread} " +
                $"foreignThread={_ecForeignThread}");
        }

        Assert(genuineSuspensions > 0,
            $"no fiber actually suspended in {attempts} attempts, so nothing was tested");
    }

    private static async Fiber<string> TaskAwaitProbe()
    {
        _ambient.Value = "set-inside-fiber";

        // Task.Yield's awaiter implements ICriticalNotifyCompletion, so the compiler
        // routes it through the builder's AwaitUnsafeOnCompleted. Unlike BjoML's own
        // awaiters — which never capture an ExecutionContext no matter how they are
        // invoked — this one WOULD flow EC if the builder called the safe
        // OnCompleted. So this is the test that actually pins the builder down.
        await Task.Yield();

        return _ambient.Value ?? "<null>";
    }

    private static void AsyncLocalDoesNotFlowAcrossTask()
    {
        var p = Bjo.Spawn(() => TaskAwaitProbe());

        AssertEqual("<null>", WaitFor(p),
            "the fiber builder reinstated a C# ExecutionContext across an await");
    }

    // -----------------------------------------------------------------------
    // Promise
    // -----------------------------------------------------------------------

    private static void PromiseJoinInChoose()
    {
        var idle = new Channel<int>();
        var promise = new Promise<int>();
        promise.TrySetResult(5);

        var done = new ManualResetEventSlim(false);
        string? result = null;

        Cml.Sync(
            Cml.Choose(
                Cml.Wrap(new ChannelReceiveEvent<int>(idle), v => $"chan{v}"),
                Cml.Wrap(promise.Join(), r => $"promise{r.Value}")),
            r => { result = r; done.Set(); });

        Await(done, "the choose to commit");
        AssertEqual("promise5", result, "the completed promise did not win");
    }

    /// <summary>
    /// The heart of B4. A branch that commits on its own initiative may arrive while
    /// a sibling branch transiently holds C. Treating that as "someone else won"
    /// drops the completion and the sync block waits forever on an event that fired.
    /// </summary>
    private static void TryCommitSpinsPastClaim()
    {
        var state = new SyncState();

        Assert(state.TryClaim(), "could not claim a fresh state");

        bool committed = false;
        var started = new ManualResetEventSlim(false);

        var t = new Thread(() =>
        {
            started.Set();
            committed = state.TryCommit(1);
        })
        { IsBackground = true };
        t.Start();

        Await(started, "the committing thread to start");
        Thread.Sleep(150);

        Assert(!committed, "TryCommit succeeded while the state was still claimed");

        // The sibling branch backs off; the commit must now go through.
        state.ResetClaim();

        Assert(t.Join(2000), "TryCommit never completed after the claim was released");
        Assert(committed, "TryCommit treated a transient claim as a genuine loss");
        Assert(state.IsSynchronized, "state did not reach Synchronized");
    }

    private static void ForeignThreadCompletion()
    {
        for (int i = 0; i < 200; i++)
        {
            var idle = new Channel<int>();
            var promise = new Promise<int>();
            var done = new ManualResetEventSlim(false);

            var completer = new Thread(() => promise.TrySetResult(1)) { IsBackground = true };
            completer.Start();

            Cml.Sync(
                Cml.Choose(
                    Cml.Wrap(new ChannelReceiveEvent<int>(idle), v => "chan"),
                    Cml.Wrap(promise.Join(), r => "promise")),
                _ => done.Set());

            Await(done, $"the promise to wake the choose on iteration {i}", 2000);
            completer.Join(2000);
        }
    }

    /// <summary>
    /// A long-lived promise offered in a choose that keeps losing must not accumulate
    /// one dead waiter per iteration; nothing would ever walk the list to clear them
    /// if the promise itself never completes.
    /// </summary>
    private static void WaitersArePruned()
    {
        var neverCompletes = new Promise<int>();
        var ch = new Channel<int>();

        for (int i = 0; i < 5000; i++)
        {
            var done = new ManualResetEventSlim(false);

            Cml.Sync(
                Cml.Choose(
                    Cml.Wrap(new ChannelReceiveEvent<int>(ch), v => "chan"),
                    Cml.Wrap(neverCompletes.Join(), r => "promise")),
                _ => done.Set());

            Cml.Sync(new ChannelSendEvent<int>(ch, i), _ => { });
            Await(done, $"iteration {i}", 2000);
        }

        // If waiters were never pruned this promise would now be holding 5000 dead
        // sync blocks alive. Completing it must not take a noticeable amount of time
        // or fire anything.
        neverCompletes.TrySetResult(0);
    }

    /// <summary>
    /// A write-once cell has to be write-once in the value as well as in the
    /// answer. The first version stored before it claimed, so the loser's
    /// <c>TrySetResult</c> returned false — correctly — and had already
    /// overwritten the winner's value on the way to finding that out.
    ///
    /// Unobservable while every payload was a <c>Unit</c>. A cancellation token
    /// carries a reason, and "cancelling twice is a no-op" has to mean the first
    /// reason is the one kept.
    /// </summary>
    private static void FirstCompletionWins()
    {
        var p = new Promise<int>();

        Assert(p.TrySetResult(1), "the first completion should win");
        Assert(!p.TrySetResult(2), "the second completion should lose");
        AssertEqual(1, p.GetAwaiter().GetResult(), "the loser overwrote the winner's value");

        // And the same for a failure arriving second: it must not displace the
        // value either, nor make a settled promise start throwing.
        Assert(!p.TrySetException(new InvalidOperationException("late")), "a late failure should lose");
        AssertEqual(1, p.GetAwaiter().GetResult(), "a late failure displaced the value");
    }

    // -----------------------------------------------------------------------
    // Task interop
    // -----------------------------------------------------------------------

    private static void FromTaskCompleted()
    {
        var p = TaskInterop.FromTask(Task.FromResult(11));
        AssertEqual(11, WaitFor(p), "FromTask lost the value");
    }

    private static void FromTaskFaulted()
    {
        var p = TaskInterop.FromTask(Task.FromException<int>(new InvalidOperationException("task-boom")));

        try
        {
            WaitFor(p);
            throw new AssertionException("expected the task's exception");
        }
        catch (InvalidOperationException ex)
        {
            AssertEqual("task-boom", ex.Message, "wrong exception surfaced");
        }
    }

    private static void CancellableCancelsOnLoss()
    {
        var winner = new Channel<int>();
        var cancelled = new ManualResetEventSlim(false);
        var done = new ManualResetEventSlim(false);

        Cml.Sync(
            Cml.Choose(
                TaskInterop.Cancellable<int>(token =>
                {
                    token.Register(() => cancelled.Set());
                    return new TaskCompletionSource<int>().Task;   // never completes
                }),
                Cml.Wrap(new ChannelReceiveEvent<int>(winner), v => Result<int>.Ok(v))),
            _ => done.Set());

        Cml.Sync(new ChannelSendEvent<int>(winner, 1), _ => { });

        Await(done, "the choose to commit");
        Await(cancelled, "the losing branch's cancellation token to fire");
    }

    private static void CancellableSurvivesOnWin()
    {
        var idle = new Channel<int>();
        var cancelled = new ManualResetEventSlim(false);
        var done = new ManualResetEventSlim(false);
        Result<int> result = default;

        Cml.Sync(
            Cml.Choose(
                TaskInterop.Cancellable<int>(token =>
                {
                    token.Register(() => cancelled.Set());
                    return Task.FromResult(42);
                }),
                Cml.Wrap(new ChannelReceiveEvent<int>(idle), v => Result<int>.Ok(v))),
            r => { result = r; done.Set(); });

        Await(done, "the cancellable task to commit");
        AssertEqual(42, result.Value, "the winning task's value was lost");
        AssertNoSignal(cancelled, "the winning branch was cancelled");
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Block until a fiber has genuinely PARKED a receive on <paramref name="ch"/>.
    ///
    /// A handshake over a second channel looks like it would do this job, but does
    /// not: the matching loop dispatches the partner's continuation inline from
    /// inside the awaiter's constructor, so the partner can observe the handshake
    /// while the fiber has not yet reached — let alone suspended on — its next await.
    /// A test that then "resumes" the fiber actually races it, and on the losing side
    /// the fiber never suspends at all and simply runs on in place. That silently
    /// turns every suspension test into a no-op.
    /// </summary>
    private static void AwaitSuspension<T>(Channel<T> ch, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (ch.PendingReceiveCount == 0)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new AssertionException($"no fiber parked a receive within {timeoutMs} ms");
            Thread.Yield();
        }

        // The op being queued only proves the sync was PUBLISHED. The awaiter is
        // still a few instructions from asking the builder to suspend, and if the
        // partner arrives inside that window the fiber completes inline instead.
        // This pause makes a genuine suspension overwhelmingly likely; callers that
        // depend on it must still tolerate the fast path.
        Thread.Sleep(20);
    }

    /// <summary>
    /// Run <paramref name="body"/> on a thread that shares nothing with the caller.
    ///
    /// ExecutionContext flow is suppressed around Start, because a plain
    /// <c>new Thread(...)</c> CAPTURES the creating thread's ExecutionContext — which
    /// would quietly carry AsyncLocal values onto the "foreign" thread and make the
    /// EC tests below assert nothing.
    /// </summary>
    private static void RunOnForeignThread(object? context, Action body)
    {
        Exception? failure = null;

        var t = new Thread(() =>
        {
            FiberContext.Current = context;
            try { body(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true };

        using (ExecutionContext.SuppressFlow())
        {
            t.Start();
        }

        Assert(t.Join(5000), "the foreign thread did not finish");
        if (failure != null) throw failure;
    }

    private static T WaitFor<T>(Promise<T> p, int timeoutMs = 5000)
    {
        var done = new ManualResetEventSlim(false);
        p.GetAwaiter().OnCompleted(() => done.Set());
        Await(done, "the promise to complete", timeoutMs);
        return p.GetAwaiter().GetResult();
    }
}
