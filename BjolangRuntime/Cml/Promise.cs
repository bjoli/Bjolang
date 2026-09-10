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
internal abstract class PromiseWaiter : IPromiseWaiter, IThreadPoolWorkItem
{
    public abstract void Signal();
    public abstract bool IsAbandoned { get; }

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
/// A write-once cell that is also a PERSISTENT CML event.
///
/// This is the handle type for <c>spawn</c> and the bridge type for C# tasks.
/// Unlike a channel event it is not consumed by syncing: once completed, every sync
/// on it succeeds immediately with the same value.
///
/// STARVATION WARNING: a completed promise inside a <c>choose</c> loop wins every
/// iteration, exactly like <c>Cml.Always</c>. Document this for language users.
/// </summary>
public class Promise<T> : IEvent<Result<T>>
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

        if (oldWaiters != null)
        {
            if (oldWaiters is List<object> list)
            {
                lock (list)
                {
                    foreach (var w in list) Wake(w);
                }
            }
            else
            {
                Wake(oldWaiters);
            }
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

    // ---- waiter registration ----------------------------------------------

    /// <summary>
    /// Wake one registered waiter after completion.
    ///
    /// A waiter is either a <see cref="PromiseWaiter"/> — enqueued directly, it is
    /// its own work item — or a bare <see cref="Action"/> stored unwrapped by
    /// <see cref="OnCompleted"/>. For a bare action there is one more save: a
    /// parked FIBER's resume delegate targets its state-machine box, which is
    /// itself a work item whose <c>Execute</c> is equivalent to invoking the
    /// delegate (that equivalence is what <see cref="IFiberResume"/> asserts; do
    /// not widen the test to <see cref="IThreadPoolWorkItem"/>, which any object
    /// could implement with unrelated semantics). So the common case — a fiber
    /// blocked on a promise — wakes with zero allocation end to end.
    /// </summary>
    private static void Wake(object waiter)
    {
        if (waiter is PromiseWaiter pw)
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

    /// <summary>Run a waiter inline; the already-completed registration path.</summary>
    private static void SignalInline(object waiter)
    {
        if (waiter is PromiseWaiter pw)
        {
            if (!pw.IsAbandoned) pw.Signal();
        }
        else
        {
            ((Action)waiter)();
        }
    }

    internal void Register(PromiseWaiter waiter) => RegisterAny(waiter);

    /// <summary>Run <paramref name="k"/> now if already complete, else on completion.</summary>
    internal void OnCompleted(Action k) => RegisterAny(k);

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
            else if (current is List<object> list)
            {
                lock (list)
                {
                    if (ReferenceEquals(Volatile.Read(ref _waiters), s_completedSentinel))
                    {
                        SignalInline(waiter);
                        return;
                    }

                    if (list.Count >= _pruneAt)
                    {
                        list.RemoveAll(static w => w is PromiseWaiter pw && pw.IsAbandoned);
                        _pruneAt = Math.Max(8, list.Count * 2);
                    }

                    list.Add(waiter);
                    return;
                }
            }
            else
            {
                // A single waiter (bare Action or PromiseWaiter); grow to a list.
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
            var r = Outcome;
            if (r.IsError) target.TrySetException(r.Error!);
            else target.TrySetResult(r.Value);
            return;
        }

        Register(new ForwardWaiter(this, target));
    }

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
        OnCompleted(() =>
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
            Deliver(state, eventId, onSync);
            return;
        }

        Register(new Waiter(this, state, eventId, onSync));
    }

    private void Deliver(SyncState state, int eventId, Action<Result<T>> onSync)
    {
        // TryCommit, not TryClaim. This waiter can run on a completely different
        // thread long after Publish returned, and may well find the state transiently
        // Claimed by a sibling branch still being published. Treating that as "someone
        // else won" would drop a completion that actually happened, and the choose
        // would then wait forever on an event that already fired.
        if (!state.TryCommit(eventId)) return;

        Scheduler.Dispatch(onSync, Outcome);
    }

    private sealed class Waiter : PromiseWaiter
    {
        private readonly Promise<T> _owner;
        private readonly SyncState _state;
        private readonly int _eventId;
        private readonly Action<Result<T>> _onSync;

        public Waiter(Promise<T> owner, SyncState state, int eventId, Action<Result<T>> onSync)
        {
            _owner = owner;
            _state = state;
            _eventId = eventId;
            _onSync = onSync;
        }

        public override void Signal() => _owner.Deliver(_state, _eventId, _onSync);

        /// <summary>Our sync block was won by another branch; we can be dropped.</summary>
        public override bool IsAbandoned => _state.IsSynchronized;
    }

    // ---- direct-await surface (cheaper than routing through Cml.Sync) ------

    public PromiseAwaiter<T> GetAwaiter() => new PromiseAwaiter<T>(this);
}

public readonly struct PromiseAwaiter<T> : ICriticalNotifyCompletion
{
    private readonly Promise<T> _p;
    public PromiseAwaiter(Promise<T> p) => _p = p;

    public bool IsCompleted => _p.IsCompleted;

    // Called from inside MoveNext, so throwing here is converted to SetException by
    // the state machine. This is the correct place for a failure to surface.
    public T GetResult() => _p.Outcome.Unwrap();

    public void OnCompleted(Action continuation) => _p.OnCompleted(continuation);

    // No ExecutionContext capture, by design. See FiberContext.
    public void UnsafeOnCompleted(Action continuation) => _p.OnCompleted(continuation);
}
