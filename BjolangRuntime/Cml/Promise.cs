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
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Bjoml;

/// <summary>
/// Success or failure as a VALUE.
///
/// Events must never throw: an exception raised inside an event continuation
/// escapes into a channel's matching loop, where it is swallowed by the
/// scheduler's catch-all and the sync block simply never completes. So a failure
/// travels as a <see cref="Result{T}"/> and is only turned back into an exception
/// inside a fiber, where the state machine can catch it.
/// </summary>
[NonUserCode]
public readonly struct Result<T>
{
    public readonly T Value;
    public readonly ExceptionDispatchInfo? Error;

    private Result(T value, ExceptionDispatchInfo? error)
    {
        Value = value;
        Error = error;
    }

    public static Result<T> Ok(T value) => new Result<T>(value, null);
    public static Result<T> Fail(ExceptionDispatchInfo e) => new Result<T>(default!, e);
    public static Result<T> Fail(Exception e) => new Result<T>(default!, ExceptionDispatchInfo.Capture(e));

    public bool IsError => Error != null;

    /// <summary>
    /// Rethrow the failure with its original stack trace, or return the value.
    ///
    /// Only call this from inside a fiber — i.e. from an awaiter's
    /// <c>GetResult()</c>, which runs on the state machine's stack. Calling it from
    /// an event continuation throws into the channel matching loop instead.
    /// </summary>
    public T Unwrap()
    {
        Error?.Throw();
        return Value;
    }
}

/// <summary>
/// Something waiting for a promise to land.
///
/// A waiter can be ABANDONED: a promise branch inside a <c>choose</c> that another
/// branch won is dead, but the promise itself may never complete, so nothing would
/// ever walk the list and drop it. Without an abandonment test, every losing
/// <c>choose</c> over a long-lived promise leaks its whole sync block.
/// </summary>
internal interface IPromiseWaiter
{
    void Signal();
    bool IsAbandoned { get; }
}

/// <summary>
/// Base for the concrete waiters: a waiter IS its own thread-pool work item.
///
/// <c>Complete</c> used to wake a waiter with <c>Scheduler.Enqueue(w.Signal)</c>,
/// and that method-group conversion allocated a fresh 64 B Action per wake — plus
/// the pooled ActionWorkItem behind Enqueue(Action), whose thread-static free
/// list starves under producer/consumer thread drift. Enqueuing the waiter object
/// itself costs nothing: it is already on the heap.
///
/// <see cref="Execute"/> carries the same obligations as ActionWorkItem's: a
/// fresh inline budget, a catch-all (an unhandled exception on a pool thread
/// kills the process), and the end-of-work-item flush that keeps batched spawns
/// from being stranded.
/// </summary>
[NonUserCode]
internal abstract class PromiseWaiter : IPromiseWaiter, IThreadPoolWorkItem
{
    public abstract void Signal();
    public abstract bool IsAbandoned { get; }

    /// <summary>
    /// Run by the completing thread on every waiter before any is woken. A
    /// waiter that commits a sync does it here; see <see cref="CommitWaiter"/>.
    /// Must not run user code.
    /// </summary>
    internal virtual void Precommit() { }

    public void Execute()
    {
        Scheduler.InlineDepth = 0;
        try
        {
            Signal();
        }
        catch (Exception ex)
        {
            Scheduler.ReportUnhandled(ex);
        }
        finally
        {
            Scheduler.OnWorkItemComplete();
        }
    }
}

/// <summary>
/// A waiter for a promise branch of a sync: it commits the sync under its
/// event id, then dispatches the value to the sync's continuation.
///
/// - **Commit order.** The commit is done in <see cref="Precommit"/>, on the
///   completing thread, before the promise wakes the waiters near it: all of
///   them for a list, those of its stripe for <see cref="StripedWaiters"/>.
///   A cancellation watch (<c>FiberWatch</c>), and a forward, waits for every
///   precommit before it commits anything (<c>Promise.WaitDrained</c>). So
///   of the syncs waiting on one promise, each commits before a watch on the
///   same promise can. A sync on <c>(cancelled tok)</c> under the token
///   <c>tok</c> returns the token's value, and does not raise.
/// - **Cost.** A list or stripe that never held one of these is not read by
///   the precommit pass, and a single waiter has no other waiter to race and
///   is not precommitted.
/// - **Dispatch.** Only the commit is inline. The continuation is dispatched
///   by the queued work item, as before, so no user code runs on the
///   completing thread.
/// - **A busy state.** A state transiently claimed by another branch is not
///   waited for here: the completing thread may hold that claim itself. Such
///   a waiter commits from its work item with <see cref="SyncState.TryCommit"/>,
///   which spins past the claim, and the order above is not guaranteed for it.
/// </summary>
[NonUserCode]
internal abstract class CommitWaiter : PromiseWaiter
{
    private const int Pending = 0, Committed = 1, Lost = 2;

    private readonly SyncState _state;
    private readonly long _gen;   // the generation published at; see SyncState
    private readonly int _eventId;
    private int _pre;

    protected CommitWaiter(SyncState state, int eventId)
    {
        _state = state;
        _gen = state.Gen;
        _eventId = eventId;
    }

    /// <summary>The sync has committed under this branch: pass the value on.</summary>
    protected abstract void Dispatch();

    internal sealed override void Precommit()
    {
        _pre = _state.TryCommitNow(_gen, _eventId) switch
        {
            SyncState.CommitNow.Committed => Committed,
            SyncState.CommitNow.Lost => Lost,
            _ => Pending,
        };
    }

    public sealed override void Signal()
    {
        switch (Volatile.Read(ref _pre))
        {
            case Committed: Dispatch(); return;
            case Lost: return;
        }

        // TryCommit, not TryClaim. This can run on another thread long after
        // Publish returned, and find the state transiently claimed by a sibling
        // branch still being published. Treating that as "someone else won"
        // would drop a completion that happened, and the sync would wait
        // forever on an event that already fired.
        if (_state.TryCommit(_gen, _eventId)) Dispatch();
    }

    /// <summary>
    /// Another branch won the sync, so the waiter can be dropped. A waiter that
    /// committed in <see cref="Precommit"/> still has its value to dispatch.
    /// </summary>
    public sealed override bool IsAbandoned =>
        Volatile.Read(ref _pre) != Committed && _state.IsSynchronizedAt(_gen);
}

/// <summary>
/// A promise's waiters, split by the core that registered them.
///
/// For a promise that many fibers wait on at once: a scope's cancellation
/// token, on which every fiber that parks in the scope registers. Behind one
/// lock, every park on every core met at that lock, and a service whose fibers
/// all lived under `main` ran on half its cores. Here a registration takes only
/// the lock of the stripe for the core it is running on.
///
/// A promise moves its waiters here once it has enough of them (see
/// <c>Promise.StripeAt</c>) and never moves back. Completion closes each stripe
/// under its lock and wakes what it held; a registration that finds its stripe
/// closed was too late for the walk and signals its waiter itself.
///
/// Each stripe prunes its own abandoned waiters, as the single list does, when
/// it grows to twice what survived the last prune.
/// </summary>
[NonUserCode]
internal sealed class StripedWaiters
{
#pragma warning disable CS0169 // the padding is never read
    [NonUserCode]
    private sealed class Stripe
    {
        // The two fields every registration on this core writes, kept off the
        // cache lines of the stripes either side, which other cores write.
        private long _pad0, _pad1, _pad2, _pad3, _pad4, _pad5, _pad6;

        /// 1 while held. A spin lock: nothing done under it waits on anything.
        public int Held;
        public bool Closed;

        /// Holds, or held, a <see cref="CommitWaiter"/>. A stripe without one
        /// is skipped by the precommit pass.
        public bool HasCommit;
        public int PruneAt = 8;
        public readonly List<object> Waiters = new();

        private long _pad7, _pad8, _pad9, _pad10, _pad11, _pad12, _pad13;

        public void Enter()
        {
            if (Interlocked.CompareExchange(ref Held, 1, 0) == 0) return;
            var spin = new SpinWait();
            do spin.SpinOnce(sleep1Threshold: -1);
            while (Volatile.Read(ref Held) != 0 || Interlocked.CompareExchange(ref Held, 1, 0) != 0);
        }

        public void Exit() => Volatile.Write(ref Held, 0);
    }
#pragma warning restore CS0169

    private readonly Stripe[] _stripes;
    private readonly int _mask;

    /// The waiters of <paramref name="from"/> go to the first stripe; the
    /// prune thins them out as registrations on that core arrive.
    /// <paramref name="hasCommit"/>: whether they include a <see cref="CommitWaiter"/>.
    public StripedWaiters(List<object> from, bool hasCommit)
    {
        int n = (int)System.Numerics.BitOperations.RoundUpToPowerOf2(
            (uint)Math.Clamp(Environment.ProcessorCount, 1, 64));
        _stripes = new Stripe[n];
        for (int i = 0; i < n; i++) _stripes[i] = new Stripe();
        _mask = n - 1;
        _stripes[0].Waiters.AddRange(from);
        _stripes[0].HasCommit = hasCommit;
    }

    /// <summary>
    /// Add <paramref name="waiter"/> to this core's stripe, or answer false
    /// when the promise has completed and its walk has passed that stripe, in
    /// which case nobody will wake it and the caller signals it.
    /// </summary>
    public bool TryAdd(object waiter)
    {
        var s = _stripes[Thread.GetCurrentProcessorId() & _mask];
        s.Enter();
        try
        {
            if (s.Closed) return false;

            if (s.Waiters.Count >= s.PruneAt)
            {
                s.Waiters.RemoveAll([NonUserCode] static (w) => w is PromiseWaiter pw && pw.IsAbandoned);
                s.PruneAt = Math.Max(8, s.Waiters.Count * 2);
            }

            if (waiter is CommitWaiter) s.HasCommit = true;
            s.Waiters.Add(waiter);
            return true;
        }
        finally { s.Exit(); }
    }

    /// <summary>
    /// Close every stripe and wake what it held, one stripe at a time. Run
    /// once, by the completion that won, after the promise reads as completed.
    /// A registration that finds its stripe closed signals its waiter itself.
    ///
    /// - A stripe's syncs are precommitted before its waiters are woken. The
    ///   caller sets the promise drained after the last stripe.
    /// - A stripe that never held a <see cref="CommitWaiter"/> skips the
    ///   precommit pass: reading every waiter of a token with a watch per
    ///   parked fiber cost a cache miss each.
    /// - The precommits and wakes are done outside the stripe's lock.
    /// </summary>
    public void Drain<T>()
    {
        foreach (var s in _stripes)
        {
            object[] held;
            bool hasCommit;
            s.Enter();
            try
            {
                s.Closed = true;
                held = s.Waiters.ToArray();
                hasCommit = s.HasCommit;
                s.Waiters.Clear();
            }
            finally { s.Exit(); }

            if (hasCommit)
                foreach (var w in held) Promise<T>.Precommit(w);
            foreach (var w in held) Promise<T>.Wake(w);
        }
    }

    /// Registered waiters, counted without pruning. Test-only.
    public int Count
    {
        get
        {
            int n = 0;
            foreach (var s in _stripes)
            {
                s.Enter();
                n += s.Waiters.Count;
                s.Exit();
            }
            return n;
        }
    }
}

/// <summary>
/// A write-once cell that is also a PERSISTENT CML event.
///
/// This is the handle type for <c>spawn</c> and the bridge type for C# tasks.
/// Unlike a channel event it is not consumed by syncing: once completed, every sync
/// on it succeeds immediately with the same value.
///
/// STARVATION WARNING: a completed promise inside a <c>choose</c> loop wins every
/// iteration, exactly like <c>Cml.Always</c>. Document this for language users.
/// </summary>
[NonUserCode]
public class Promise<T> : IEvent<Result<T>>,
    IEvent<global::BjolangRuntime.Result<Exception, T>>,
    INowable<global::BjolangRuntime.Result<Exception, T>>
{
    private static readonly object s_completedSentinel = new();

    private object? _waiters;
    private T _value = default!;
    private ExceptionDispatchInfo? _error;

    /// <summary>
    /// Claimed by the writer that won, before it stores anything.
    ///
    /// A separate flag rather than <see cref="_waiters"/> reaching the sentinel,
    /// because those are two different moments: the cell has an owner from the
    /// claim, and is readable only from the publish. A loser must be turned away
    /// at the first of them.
    /// </summary>
    private int _claimed;

    /// <summary>EXPERIMENT: amortised prune threshold, guarded by lock(list).</summary>
    private int _pruneAt = 8;

    /// <summary>
    /// Waiters that, surviving a prune, move the list to <see cref="StripedWaiters"/>.
    /// A fiber's join has one or two; a scope's token has one per fiber parked
    /// in the scope, and is what this is for.
    /// </summary>
    private const int StripeAt = 32;


    /// <summary>Set by the completion once every waiter is precommitted.</summary>
    private bool _drained;

    /// <summary>
    /// The list of waiters holds, or held, a <see cref="CommitWaiter"/>.
    /// Without one the precommit pass is skipped. Written before the waiter is
    /// published: under the list's lock, or before the exchange that installs
    /// the list. The stripes keep a flag each.
    /// </summary>
    private bool _hasCommit;

    /// <summary>
    /// Wait for a completed promise to finish precommitting its waiters.
    ///
    /// - For a caller that saw the promise completed and is about to commit a
    ///   sync against it: the syncs that were waiting on the promise commit
    ///   first. See <see cref="CommitWaiter"/>.
    /// - Precondition: <see cref="IsCompleted"/>. The wait is a spin over the
    ///   precommit pass, which does no blocking work.
    /// </summary>
    internal void WaitDrained()
    {
        if (Volatile.Read(ref _drained)) return;
        // No Sleep(1): a watch woken from an early stripe waits here while the
        // completion drains the later ones, which takes microseconds.
        var sw = new SpinWait();
        while (!Volatile.Read(ref _drained)) sw.SpinOnce(sleep1Threshold: -1);
    }

    public bool IsCompleted => ReferenceEquals(Volatile.Read(ref _waiters), s_completedSentinel);

    public bool TrySetResult(T value) => Complete(value, null);

    public bool TrySetException(Exception e) => Complete(default!, ExceptionDispatchInfo.Capture(e));

    public bool TrySetException(ExceptionDispatchInfo e) => Complete(default!, e);

    private bool Complete(T value, ExceptionDispatchInfo? error)
    {
        // CLAIM BEFORE STORING. This used to store first and find out afterwards
        // whether it had won, which returned the right answer and wrote the
        // wrong value: a second, losing completion still overwrote the winner's.
        // Invisible while every payload was a Unit, and a bug the moment one
        // carries information — a cancellation token holds a CancelReason, and
        // "first reason wins" is exactly the property that was not true.
        if (Interlocked.Exchange(ref _claimed, 1) != 0) return false;

        _value = value;
        _error = error;

        // Publishes the two stores above: the exchange is a full fence, and
        // every reader tests IsCompleted — an acquiring read of this same field
        // — before it touches Outcome.
        var oldWaiters = Interlocked.Exchange(ref _waiters, s_completedSentinel);

        // The syncs waiting here are precommitted before the waiters are
        // woken, and `_drained` is set once every one is. A watch waits for it
        // (WaitDrained) before it commits. See CommitWaiter.
        if (oldWaiters != null)
        {
            // The exact type: List<T> is not sealed, so `is List<object>`
            // calls a helper that walks the type's parents, and the waiter is
            // usually a single fiber's box, which is no list. The lists here
            // are all made by this class.
            if (oldWaiters.GetType() == typeof(List<object>))
            {
                var list = Unsafe.As<List<object>>(oldWaiters);
                lock (list)
                {
                    if (_hasCommit)
                        foreach (var w in list) Precommit(w);
                    Volatile.Write(ref _drained, true);
                    foreach (var w in list) Wake(w);
                }
            }
            else if (oldWaiters is StripedWaiters striped)
            {
                // Stripe by stripe, so the first wakes are not held back by
                // the last stripes. A watch woken here may run before a later
                // stripe's syncs are precommitted, and waits for `_drained`
                // before it commits anything.
                striped.Drain<T>();
                Volatile.Write(ref _drained, true);
            }
            else
            {
                // No precommit. A single waiter has no other waiter to race,
                // and a watch registered after the completion is one that the
                // prune dropped, which only a list does.
                Volatile.Write(ref _drained, true);
                Wake(oldWaiters);
            }
        }
        else
        {
            Volatile.Write(ref _drained, true);
        }

        OnLanded(error);
        return true;
    }

    /// <summary>
    /// Run once, by the completion that won, after the value is readable and the
    /// waiters have been enqueued.
    ///
    /// For an owner that has to hear about every fiber it started and does not
    /// otherwise want the value. Registering a waiter says the same thing and
    /// costs a <see cref="SyncState"/>, a join event and a closure per fiber;
    /// this costs a null check on a field the owner already had to store.
    ///
    /// It runs on whichever thread completed the promise, so the same rules apply
    /// as to a nack: no user code, no suspending.
    /// </summary>
    protected virtual void OnLanded(ExceptionDispatchInfo? error) { }

    /// <summary>
    /// The one awaiter of a called bjoroutine has read the outcome. A promise
    /// that anything else can reach does nothing here; the box of a suspended
    /// call goes back to its pool (see <c>CalledFiber</c>).
    /// </summary>
    internal virtual void ReleaseAfterRead() { }

    /// <summary>Back to a promise that has not landed, for a pooled subclass.</summary>
    private protected void ResetForReuse()
    {
        _waiters = null;
        _value = default!;
        _error = null;
        _pruneAt = 8;
        _claimed = 0;
        _drained = false;
        _hasCommit = false;
    }

    // ---- waiter registration ----------------------------------------------

    /// <summary>
    /// Wake one registered waiter after completion.
    ///
    /// A waiter is a <see cref="PromiseWaiter"/> — enqueued directly, it is its
    /// own work item — or a parked fiber's box (<see cref="IResume"/>), which is
    /// one too, or a bare <see cref="Action"/> stored unwrapped by
    /// <see cref="OnCompleted(Action)"/>. For a bare action there is one more save: a
    /// parked FIBER's resume delegate targets its state-machine box, which is
    /// itself a work item whose <c>Execute</c> is equivalent to invoking the
    /// delegate (that equivalence is what <see cref="IFiberResume"/> asserts; do
    /// not widen the test to <see cref="IThreadPoolWorkItem"/>, which any object
    /// could implement with unrelated semantics). So the common case — a fiber
    /// blocked on a promise — wakes with zero allocation end to end.
    /// </summary>
    internal static void Wake(object waiter)
    {
        // A parked fiber's box first, registered with no delegate (see
        // IResume): it is the common waiter, and no PromiseWaiter is one, so
        // the order does not change which branch a waiter takes.
        if (waiter is IResume resume)
        {
            Scheduler.Enqueue(resume);
        }
        else if (waiter is PromiseWaiter pw)
        {
            if (!pw.IsAbandoned) Scheduler.Enqueue(pw);
        }
        else
        {
            var a = (Action)waiter;
            if (a.Target is IFiberResume box) Scheduler.Enqueue(box);
            else Scheduler.Enqueue(a);
        }
    }

    /// <summary>The first pass of a completion; see <see cref="CommitWaiter"/>.</summary>
    internal static void Precommit(object waiter)
    {
        if (waiter is PromiseWaiter pw) pw.Precommit();
    }

    /// <summary>Run a waiter inline; the already-completed registration path.</summary>
    private static void SignalInline(object waiter)
    {
        // In the order of Wake, for the same reason.
        if (waiter is IResume resume)
        {
            resume.Resume();
        }
        else if (waiter is PromiseWaiter pw)
        {
            if (!pw.IsAbandoned) pw.Signal();
        }
        else
        {
            ((Action)waiter)();
        }
    }

    /// <summary>
    /// Registered waiters, counted without pruning first. Test-only.
    ///
    /// A sync under a scope registers on the scope's token and never removes the
    /// registration; <see cref="IPromiseWaiter.IsAbandoned"/> and the amortised
    /// prune are what keep the list from growing with the number of rendezvous.
    /// Proving that needs the raw count, since every path that could report it
    /// also prunes.
    /// </summary>
    internal int RawWaiterCount
    {
        get
        {
            var w = Volatile.Read(ref _waiters);
            if (w is null || ReferenceEquals(w, s_completedSentinel)) return 0;
            if (w is List<object> list) { lock (list) return list.Count; }
            if (w is StripedWaiters striped) return striped.Count;
            return 1;
        }
    }

    internal void Register(PromiseWaiter waiter) => RegisterAny(waiter);

    /// <summary>Run <paramref name="k"/> now if already complete, else on completion.</summary>
    internal void OnCompleted(Action k) => RegisterAny(k);

    /// <summary>
    /// <see cref="OnCompleted(Action)"/> for a parked fiber's box. A box can be
    /// a promise itself (<c>CalledFiber</c>), but never a <see cref="PromiseWaiter"/>,
    /// so <see cref="Wake"/> tells the three kinds apart.
    /// </summary>
    internal void OnCompleted(IResume k) => RegisterAny(k);

    private void RegisterAny(object waiter)
    {
        SpinWait spin = default;
        while (true)
        {
            var current = Volatile.Read(ref _waiters);
            if (ReferenceEquals(current, s_completedSentinel))
            {
                SignalInline(waiter);
                return;
            }

            if (current == null)
            {
                if (Interlocked.CompareExchange(ref _waiters, waiter, null) == null)
                    return;
            }
            else if (current is StripedWaiters striped)
            {
                if (!striped.TryAdd(waiter)) SignalInline(waiter);
                return;
            }
            else if (current is List<object> list)
            {
                // Signalled after the lock is released: the completion holds
                // this lock for its precommits, and a late waiter may wait for
                // them (WaitDrained).
                bool late = false;
                lock (list)
                {
                    var now = Volatile.Read(ref _waiters);
                    if (ReferenceEquals(now, s_completedSentinel))
                    {
                        late = true;
                        goto unlocked;
                    }

                    // Moved to stripes while this waited for the lock, and
                    // nobody reads this list any more: look again.
                    if (ReferenceEquals(now, list))
                    {
                        if (list.Count >= _pruneAt)
                        {
                            list.RemoveAll([NonUserCode] static (w) => w is PromiseWaiter pw && pw.IsAbandoned);
                            _pruneAt = Math.Max(8, list.Count * 2);
                        }

                        if (list.Count >= StripeAt)
                        {
                            var stripes = new StripedWaiters(list, _hasCommit);
                            if (Interlocked.CompareExchange(ref _waiters, stripes, list) == list)
                            {
                                if (!stripes.TryAdd(waiter)) SignalInline(waiter);
                                return;
                            }

                            // Only a completion takes the field off a list whose
                            // lock is held here, and it walks this list once the
                            // lock is released: adding to it is still right.
                        }

                        if (waiter is CommitWaiter) _hasCommit = true;
                        list.Add(waiter);
                        return;
                    }
                }
            unlocked:
                if (late)
                {
                    SignalInline(waiter);
                    return;
                }
            }
            else
            {
                // A single waiter (bare Action or PromiseWaiter); grow to a list.
                if (current is CommitWaiter || waiter is CommitWaiter) _hasCommit = true;
                var grown = new List<object>(4) { current, waiter };
                if (Interlocked.CompareExchange(ref _waiters, grown, current) == current)
                    return;
            }

            spin.SpinOnce();
        }
    }

    internal Result<T> Outcome
    {
        get
        {
            // Callers must have observed IsCompleted first; that acquiring read
            // pairs with the release write in Complete.
            return _error != null ? Result<T>.Fail(_error) : Result<T>.Ok(_value);
        }
    }

    /// <summary>
    /// Pipe this promise's outcome into <paramref name="target"/> when it lands.
    ///
    /// Public because a hosted language needs it: a child cancellation token is
    /// a promise forwarded from its parent's, so that cancelling a scope
    /// cancels everything under it. Safe to expose for the reason the nack rule
    /// asks about — it runs no user code, only <c>TrySetResult</c> on the
    /// target — so a borrowed thread stays borrowed for a pointer store.
    /// </summary>
    public void Forward(Promise<T> target)
    {
        if (IsCompleted)
        {
            // The target's waiters run after this promise's syncs commit, as
            // they do when the forward is woken; see CommitWaiter.
            WaitDrained();
            var r = Outcome;
            if (r.IsError) target.TrySetException(r.Error!);
            else target.TrySetResult(r.Value);
            return;
        }

        Register(new ForwardWaiter(this, target));
    }

    [NonUserCode]
    private sealed class ForwardWaiter : PromiseWaiter
    {
        private readonly Promise<T> _source;
        private readonly Promise<T> _target;

        public ForwardWaiter(Promise<T> source, Promise<T> target)
        {
            _source = source;
            _target = target;
        }

        public override void Signal()
        {
            // Signalled at the registration, it may be during the source's
            // precommits; see Forward.
            _source.WaitDrained();
            var r = _source.Outcome;
            if (r.IsError) _target.TrySetException(r.Error!);
            else _target.TrySetResult(r.Value);
        }

        public override bool IsAbandoned => _target.IsCompleted;
    }

    /// <summary>
    /// Stop listening, deliberately, and make sure a failure is still heard.
    ///
    /// What a hosted language's "discard this handle" means for a promise.
    /// Dropping the reference instead would lose an exception inside it
    /// silently: nothing else is watching, so a fiber that died would simply
    /// never be mentioned. This registers a completion callback that routes a
    /// failure to <see cref="Scheduler.ReportUnhandled"/> and ignores success.
    ///
    /// Deliberately *not* the same thing as exposing
    /// <see cref="OnCompleted"/>. That would hand out a callback slot running
    /// on a borrowed thread with whatever context it happened to have, which is
    /// exactly where user code must never go. This one takes no callback, so
    /// there is nothing to misuse.
    /// </summary>
    public void Detach()
    {
        OnCompleted([NonUserCode] () =>
        {
            var r = Outcome;
            if (r.IsError) Scheduler.ReportUnhandled(r.Error!.SourceException);
        });
    }

    // ---- CML surface -------------------------------------------------------

    /// <summary>
    /// The joinable event. Carries failure as a value; unwrap it inside a fiber.
    /// Returns this instance directly to avoid allocating event wrapper objects.
    /// </summary>
    public IEvent<Result<T>> Join() => this;

    public void Publish(SyncState state, int eventId, Action<Result<T>> onSync)
    {
        if (IsCompleted)
        {
            // From inside the publish, so this sync's own sink; see CommitWaiter.
            if (state.TryCommit(state.Gen, eventId)) Scheduler.DispatchOwn(onSync, Outcome);
            return;
        }

        Register(new Waiter(this, state, eventId, onSync));
    }

    [NonUserCode]
    private sealed class Waiter : CommitWaiter
    {
        private readonly Promise<T> _owner;
        private readonly Action<Result<T>> _onSync;

        public Waiter(Promise<T> owner, SyncState state, int eventId, Action<Result<T>> onSync)
            : base(state, eventId)
        {
            _owner = owner;
            _onSync = onSync;
        }

        protected override void Dispatch() => Scheduler.Dispatch(_onSync, _owner.Outcome);
    }

    // ---- the hosted language's join ---------------------------------------

    /// <summary>
    /// The outcome as Bjolang's `(Result Exception a)`: the exception itself
    /// rather than the dispatch info, because that is what `(Err e)` binds.
    /// `SourceException` rather than `Throw()`, since a join must not raise.
    /// </summary>
    private static global::BjolangRuntime.Result<Exception, T> ForJoin(Result<T> r) =>
        r.IsError
            ? global::BjolangRuntime.Result<Exception, T>.Err(r.Error!.SourceException)
            : global::BjolangRuntime.Result<Exception, T>.Ok(r.Value);

    /// <summary>
    /// A landed promise is available, so `sync` takes it without publishing
    /// anything. Skipping the token race is not a change of meaning: an
    /// available event beats the token, which is published after it.
    /// </summary>
    bool INowable<global::BjolangRuntime.Result<Exception, T>>.TryNow(
        out global::BjolangRuntime.Result<Exception, T> value)
    {
        if (IsCompleted)
        {
            value = ForJoin(Outcome);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// `(promise-join p)` is this promise, as an event carrying Bjolang's
    /// result. Converting in the waiter rather than through `Cml.Wrap` saves
    /// the wrap event a join used to build, and the closure and delegate the
    /// wrap built at every publish.
    /// </summary>
    void IEvent<global::BjolangRuntime.Result<Exception, T>>.Publish(
        SyncState state, int eventId, Action<global::BjolangRuntime.Result<Exception, T>> onSync)
    {
        if (IsCompleted)
        {
            if (state.TryCommit(state.Gen, eventId)) Scheduler.DispatchOwn(onSync, ForJoin(Outcome));
            return;
        }

        Register(new JoinWaiter(this, state, eventId, onSync));
    }

    /// <summary>The join's waiter: <see cref="Waiter"/> with the conversion.</summary>
    [NonUserCode]
    private sealed class JoinWaiter : CommitWaiter
    {
        private readonly Promise<T> _owner;
        private readonly Action<global::BjolangRuntime.Result<Exception, T>> _onSync;

        public JoinWaiter(
            Promise<T> owner, SyncState state, int eventId,
            Action<global::BjolangRuntime.Result<Exception, T>> onSync)
            : base(state, eventId)
        {
            _owner = owner;
            _onSync = onSync;
        }

        protected override void Dispatch() => Scheduler.Dispatch(_onSync, ForJoin(_owner.Outcome));
    }

    /// <summary>
    /// This promise's value as an event, for a promise that is only ever
    /// completed with a value: a cancellation token, a timer, a nack. See
    /// <see cref="PromiseValue{T}"/>.
    /// </summary>
    internal IEvent<T> ValueEvent() => new PromiseValue<T>(this);

    // ---- direct-await surface (cheaper than routing through Cml.Sync) ------

    public PromiseAwaiter<T> GetAwaiter() => new PromiseAwaiter<T>(this);
}

/// <summary>
/// A promise's value as an event, for a promise that is never completed with an
/// exception.
///
/// What <c>Cml.Wrap(p.Join(), r => r.Value)</c> spelled: a wrap event, and a
/// closure and its delegate at every publish, to take the value out of the
/// <see cref="Result{T}"/>. This delivers the value itself through a waiter of
/// its own, and a promise that has landed commits on the spot through
/// <see cref="INowable{T}"/>.
///
/// A failed promise delivers <c>default</c>; nothing that builds one of these
/// completes its promise with a failure.
/// </summary>
[NonUserCode]
internal sealed class PromiseValue<T> : IEvent<T>, INowable<T>
{
    private readonly Promise<T> _promise;

    internal PromiseValue(Promise<T> promise) => _promise = promise;

    public bool TryNow(out T value)
    {
        if (_promise.IsCompleted)
        {
            value = _promise.Outcome.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public void Publish(SyncState state, int eventId, Action<T> onSync)
    {
        if (_promise.IsCompleted)
        {
            if (state.TryCommit(state.Gen, eventId)) Scheduler.DispatchOwn(onSync, _promise.Outcome.Value);
            return;
        }

        _promise.Register(new Waiter(this, state, eventId, onSync));
    }

    [NonUserCode]
    private sealed class Waiter : CommitWaiter
    {
        private readonly PromiseValue<T> _owner;
        private readonly Action<T> _onSync;

        public Waiter(PromiseValue<T> owner, SyncState state, int eventId, Action<T> onSync)
            : base(state, eventId)
        {
            _owner = owner;
            _onSync = onSync;
        }

        protected override void Dispatch() => Scheduler.Dispatch(_onSync, _owner._promise.Outcome.Value);
    }
}

[NonUserCode]
public readonly struct PromiseAwaiter<T> : ICriticalNotifyCompletion, IFiberAwaiter, ITraceAwaiter
{
    private readonly Promise<T> _p;
    public PromiseAwaiter(Promise<T> p) => _p = p;

    object? ITraceAwaiter.TraceTarget => _p;

    public bool IsCompleted => _p.IsCompleted;

    // Called from inside MoveNext, so throwing here is converted to SetException by
    // the state machine. This is the correct place for a failure to surface.
    public T GetResult() => _p.Outcome.Unwrap();

    public void OnCompleted(Action continuation) => _p.OnCompleted(continuation);

    // No ExecutionContext capture, by design. See FiberContext.
    public void UnsafeOnCompleted(Action continuation) => _p.OnCompleted(continuation);

    void IFiberAwaiter.ParkFiber(IFiberResume box) => _p.OnCompleted(box);
}
