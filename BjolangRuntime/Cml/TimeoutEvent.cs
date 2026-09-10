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

namespace Bjoml;

/// <summary>
/// The direct implementation of <see cref="Cml.Timeout"/>.
///
/// The combinator version (<see cref="Cml.TimeoutViaCombinators"/>, retained as
/// the executable specification) builds Guard → WithNack → Promise → a second
/// whole <c>Cml.Sync</c> block just to listen to the nack → two Wraps, and was
/// measured at ~1.4 µs and 1360 B per ARMED sync — of which the actual
/// <see cref="System.Threading.Timer"/> arm+dispose is only ~98 ns and 144 B.
/// The composition was the cost, so this class does directly what the sandwich
/// did indirectly, for one node, one cancel delegate, one NackNode and one Timer
/// per armed sync.
///
/// Arming happens in <see cref="Publish"/>, so "relative to sync" needs no Guard:
/// the deadline starts when the event is published, which is what a timeout
/// inside a choose loop has to mean.
/// </summary>
internal sealed class TimeoutEvent : IEvent<Unit>
{
    private readonly int _ms;

    public TimeoutEvent(int ms) => _ms = ms;

    public void Publish(SyncState state, int eventId, Action<Unit> onSync)
        => TimeoutNode.Arm(state, eventId, onSync, _ms);
}

/// <summary>
/// The direct implementation of <see cref="Cml.At"/>: an ABSOLUTE deadline,
/// deliberately not the same thing as <see cref="Cml.Timeout"/>. The deadline is
/// fixed at construction and only the remaining interval is computed per publish,
/// which is what makes it usable as an overall budget for a loop.
/// </summary>
internal sealed class AtEvent : IEvent<Unit>
{
    private readonly DateTime _utcDeadline;

    public AtEvent(DateTime utcDeadline) => _utcDeadline = utcDeadline;

    public void Publish(SyncState state, int eventId, Action<Unit> onSync)
    {
        var remaining = (_utcDeadline - DateTime.UtcNow).TotalMilliseconds;
        var ms = remaining <= 0 ? 0 : (remaining > int.MaxValue ? int.MaxValue : (int)remaining);
        TimeoutNode.Arm(state, eventId, onSync, ms);
    }
}

/// <summary>
/// One armed timeout: a timer, the sync block it is trying to commit, and a gate.
///
/// NOT POOLED, deliberately, and the reason is the same ABA argument that allows
/// <see cref="EventAwaiter{T}"/> to be pooled — inverted. A pooled object is safe
/// only if every stale reference to it is dead, and channel resume delegates are
/// dead because they are gated by a CAS on their own op's <see cref="SyncState"/>.
/// A nack action has no such gate: <c>MarkSynchronized</c> fires it
/// unconditionally when the branch loses. A recycled node's cancel delegate could
/// therefore be invoked by a PREVIOUS sync's losing nack and dispose the timer of
/// whatever the node is doing NOW. A fresh node per armed sync makes a late
/// cancel harmless: it hits this node's gate and finds it already Done.
///
/// The gate (<see cref="_gate"/>) is the single owner-election point: whichever
/// of Fire and Cancel exchanges it first is responsible for the timer; the loser
/// does nothing. Fire may also lose the COMMIT (the block was won between the
/// deadline and the CAS), in which case it just disposes; the nack that made it
/// lose has already run or will run, and both find the gate Done.
/// </summary>
internal sealed class TimeoutNode
{
    private const int Pending = 0;
    private const int Done = 1;

    private readonly SyncState _state;
    private readonly int _eventId;
    private readonly Action<Unit> _onSync;
    private Timer? _timer;
    private int _gate;

    private static readonly TimerCallback s_fire = static s => ((TimeoutNode)s!).Fire();

    private TimeoutNode(SyncState state, int eventId, Action<Unit> onSync)
    {
        _state = state;
        _eventId = eventId;
        _onSync = onSync;
    }

    public static void Arm(SyncState state, int eventId, Action<Unit> onSync, int ms)
    {
        // An expired deadline is an always-ready event; commit like Cml.Always
        // does (TryCommit, which spins past a sibling's transient claim) instead
        // of taking a pointless trip through the timer queue.
        if (ms <= 0)
        {
            if (state.TryCommit(eventId)) Scheduler.Dispatch(onSync, default);
            return;
        }

        // An earlier branch already won; don't build anything. Racy, but only as
        // an optimisation — the nack path below is what is load-bearing.
        if (state.IsSynchronized) return;

        var node = new TimeoutNode(state, eventId, onSync);

        // From this call on, Cancel can run concurrently on another thread.
        var nack = state.RegisterNack(eventId, node.Cancel);
        if (nack == null)
        {
            // The block synchronised before we registered. RegisterNack enqueued
            // Cancel iff the winner is an earlier branch — always true here, since
            // later branches have not published yet. Cancel on this unarmed node
            // is a no-op: it wins the gate and finds no timer. Nothing to undo.
            return;
        }

        // A leaf's subtree is exactly one branch index wide.
        nack.I1 = eventId + 1;

        // Create unarmed, publish the field, and only then start the clock, so
        // that a concurrent Cancel always has a coherent view: either it sees no
        // timer (and step (a) disposes for it), or it sees the timer and disposes
        // it itself (making Change throw ODE, caught at (b)).
        var t = new Timer(s_fire, node, Timeout.Infinite, Timeout.Infinite);
        Volatile.Write(ref node._timer, t);

        if (Volatile.Read(ref node._gate) == Done)
        {
            t.Dispose();                                    // (a)
            return;
        }

        try { t.Change(ms, Timeout.Infinite); }
        catch (ObjectDisposedException) { }                 // (b)

        // Mirror of WithNackEvent's post-publish check. nack.I1 above is written
        // without the SyncState lock, so a MarkSynchronized that raced us may have
        // read I1 = MaxValue, judged the winner inside our interval, and skipped
        // our nack. If the block is synchronized now, we lost (only Fire can make
        // us win, and it has not committed) — cancel by hand. Idempotent.
        if (state.IsSynchronized) node.Cancel();
    }

    /// <summary>The nack action: this branch lost, release the timer.</summary>
    private void Cancel()
    {
        if (Interlocked.Exchange(ref _gate, Done) == Done) return;
        Volatile.Read(ref _timer)?.Dispose();
    }

    /// <summary>Timer callback: the deadline arrived first; try to win the sync.</summary>
    private void Fire()
    {
        if (Interlocked.Exchange(ref _gate, Done) == Done) return;
        _timer!.Dispose();

        // TryCommit, not TryClaim: this runs on a timer-queue thread long after
        // Publish returned and may find the state transiently Claimed by a
        // sibling — the same argument as Promise.Deliver.
        if (_state.TryCommit(_eventId)) Scheduler.Dispatch(_onSync, default);
    }
}
