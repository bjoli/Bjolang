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
using System.Runtime.CompilerServices;
using System.Threading;

namespace Bjoml;

/// <summary>
/// Makes <c>IEvent&lt;T&gt;</c> awaitable, so <c>(sync ev)</c> in the hosted language
/// compiles to <c>await ev</c> and the suspension points of the language are exactly
/// its sync points. That equivalence is what makes CML reasoning work: between two
/// syncs a fiber is atomic with respect to every other fiber.
/// </summary>
public static class EventAwaitExtensions
{
    public static EventAwaiter<T> GetAwaiter<T>(this IEvent<T> ev) => EventAwaiter<T>.Rent(ev);
}

/// <summary>
/// The synchronisation is STARTED in <see cref="Rent"/> (or the constructor), i.e.
/// when <c>await</c> evaluates its operand.
///
/// If the rendezvous matches inline — the common case, because
/// <see cref="Scheduler.Dispatch"/> runs continuations on the matching thread —
/// <see cref="IsCompleted"/> is already true and the compiler never asks for a
/// continuation, so no resume delegate is created and the fiber never suspends.
///
/// POOLED, like <see cref="GetOp{T}"/>/<see cref="PutOp{T}"/>, and for the same
/// reason: one of these per await was 2 of the 3 allocations on the fiber choose
/// path (the object, plus the method-group conversion of <c>OnSync</c> minting a
/// fresh <c>Action&lt;T&gt;</c> per sync — a pooled instance carries its delegate
/// for life). The third, <see cref="SyncState"/>, must NOT be pooled: losing
/// choose branches linger in channels and are reclaimed lazily (see the B7 notes
/// in <c>Channel.cs</c>), so a recycled state back in W would make a stale parked
/// op look live again.
///
/// The recycle point is <see cref="GetResult"/>, which the await contract calls
/// exactly once, after completion. Losing branches keep dead references to the
/// <c>OnSync</c> delegate in parked ops, but never invoke it — every channel path
/// checks <c>TrySync</c>/<c>IsSynchronized</c> before resuming — so they only pin
/// the pooled object, which is exactly what a pool wants pinned.
///
/// Field resets happen at recycle time, NOT at rent: they must be complete before
/// <see cref="Cml.Sync"/> publishes, because the instant an op is parked, a thread
/// on the other side of the channel can call <see cref="OnSync"/> concurrently.
/// </summary>
public sealed class EventAwaiter<T> : ICriticalNotifyCompletion
{
    /// <summary>Marks "already completed" so a late continuation runs immediately.</summary>
    private static readonly Action Sentinel = () => { };

    private const int MaxCached = 64;
    [ThreadStatic] private static EventAwaiter<T>? _free;
    [ThreadStatic] private static int _freeCount;

    private EventAwaiter<T>? _next;
    private readonly Action<T> _onSync;
    private T _result = default!;
    private Action? _continuation;

    private EventAwaiter() => _onSync = OnSync;

    public EventAwaiter(IEvent<T> ev) : this()
    {
        Start(ev, _onSync);
    }

    public static EventAwaiter<T> Rent(IEvent<T> ev)
    {
        var aw = _free;
        if (aw is null) return new EventAwaiter<T>(ev);

        _free = aw._next;
        _freeCount--;
        aw._next = null;

        // _result/_continuation were cleared when this instance was recycled.
        Start(ev, aw._onSync);
        return aw;
    }

    /// <summary>
    /// Begin the synchronisation, without a <see cref="SyncState"/> where the
    /// event does not need one.
    ///
    /// A single channel operation has one commit point and no branches to
    /// withdraw, so the commit protocol has nothing to arbitrate. It parks one
    /// pooled op instead. <c>_onSync</c> is the awaiter's own cached delegate,
    /// so carrying the value as an argument costs no closure.
    ///
    /// Everything else — including a <c>choose</c> over channels — goes the
    /// general way, which is what keeps a losing branch withdrawable.
    /// </summary>
    private static void Start(IEvent<T> ev, Action<T> onSync)
    {
        if (ev is IDirectSyncable<T> direct) direct.SyncDirect(onSync);
        else Cml.Sync(ev, onSync);
    }

    private void OnSync(T value)
    {
        // Written before the Exchange below, which is a full fence, so any thread
        // that observes the Sentinel also observes the result.
        _result = value;

        var c = Interlocked.Exchange(ref _continuation, Sentinel);

        // null  -> completed inline, before anyone asked to be resumed.
        // Sentinel -> impossible, we only ever complete once.
        // anything else -> the fiber parked; resume it.
        if (c != null && !ReferenceEquals(c, Sentinel)) c();
    }

    public bool IsCompleted => ReferenceEquals(Volatile.Read(ref _continuation), Sentinel);

    /// <summary>
    /// Return the result and recycle this awaiter.
    ///
    /// Safe because everything <see cref="OnSync"/> does to this object
    /// happens-before the continuation runs (the Exchange is a full fence, and the
    /// inline-completion path finishes OnSync before IsCompleted can observe the
    /// Sentinel), and the await contract calls GetResult exactly once, afterwards.
    /// Clearing <c>_result</c> here rather than at rent keeps a pooled awaiter from
    /// pinning a stale T, mirroring <see cref="GetOp{T}.Recycle"/>.
    /// </summary>
    public T GetResult()
    {
        var r = _result;
        _result = default!;
        _continuation = null;

        if (_freeCount < MaxCached)
        {
            _next = _free;
            _free = this;
            _freeCount++;
        }

        return r;
    }

    public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

    /// <summary>
    /// "Unsafe" here means "does not flow ExecutionContext", which is precisely what
    /// BjoML wants: the fiber carries its own dynamic environment and the builder
    /// reinstates it on resume.
    /// </summary>
    public void UnsafeOnCompleted(Action continuation)
    {
        var prev = Interlocked.CompareExchange(ref _continuation, continuation, null);

        // The event completed in the gap between IsCompleted returning false and us
        // registering. Nobody will call us, so run it here.
        if (ReferenceEquals(prev, Sentinel)) continuation();
    }
}

/// <summary>Convenience constructors for the common channel operations.</summary>
public static class ChannelEvents
{
    public static ChannelReceiveOperation<T> Receive<T>(this Channel<T> ch) => ch.Receive();

    public static ChannelSendOperation<T> Send<T>(this Channel<T> ch, T value) => ch.Send(value);
}
