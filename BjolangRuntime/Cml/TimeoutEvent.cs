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
/// did indirectly, for one object per armed sync: the node is its own nack and
/// its own entry in <see cref="TimerWheel"/>. A separate cancel delegate and
/// <see cref="NackNode"/> were 104 B more per armed sync. A
/// <see cref="System.Threading.Timer"/> each was three more objects and a lock on
/// the runtime's timer queue to arm and another to cancel.
///
/// Arming happens in <see cref="Publish"/>, so "relative to sync" needs no Guard:
/// the deadline starts when the event is published, which is what a timeout
/// inside a choose loop has to mean.
/// </summary>
internal sealed class TimeoutEvent : IEvent<Unit>, ISleepEvent
{
    private readonly int _ms;

    public TimeoutEvent(int ms) => _ms = ms;

    public void Publish(SyncState state, int eventId, Action<Unit> onSync)
        => TimeoutNode.Arm(state, eventId, onSync, _ms);

    int ISleepEvent.SleepMs() => _ms;
}

/// <summary>
/// A timer event that a lone `sync` can wait for as a sleep: see
/// <see cref="SleepNode"/>.
/// </summary>
internal interface ISleepEvent
{
    /// <summary>
    /// How long a sync started now waits. Zero or less means that the event is
    /// available now, and the sync then goes the general way, which commits it
    /// at once.
    /// </summary>
    int SleepMs();
}

/// <summary>
/// The direct implementation of <see cref="Cml.At"/>: an ABSOLUTE deadline,
/// deliberately not the same thing as <see cref="Cml.Timeout"/>. The deadline is
/// fixed at construction and only the remaining interval is computed per publish,
/// which is what makes it usable as an overall budget for a loop.
/// </summary>
internal sealed class AtEvent : IEvent<Unit>, ISleepEvent
{
    private readonly DateTime _utcDeadline;

    public AtEvent(DateTime utcDeadline) => _utcDeadline = utcDeadline;

    public void Publish(SyncState state, int eventId, Action<Unit> onSync)
        => TimeoutNode.Arm(state, eventId, onSync, Remaining());

    int ISleepEvent.SleepMs() => Remaining();

    private int Remaining()
    {
        var remaining = (_utcDeadline - DateTime.UtcNow).TotalMilliseconds;
        return remaining <= 0 ? 0 : (remaining > int.MaxValue ? int.MaxValue : (int)remaining);
    }
}

/// <summary>
/// An entry in <see cref="TimerWheel"/>: when it is due, where it is linked,
/// and what to do when it is due.
///
/// A <see cref="NackNode"/>, because a <see cref="TimeoutNode"/> is its own
/// nack and C# has one base class. A <see cref="SleepNode"/> does not use the
/// nack fields, and pays 24 B for them.
/// </summary>
internal abstract class WheelNode : NackNode
{
    // Where this node is in the timer wheel: the millisecond it is due, the
    // wheel it is linked into (null once unlinked), its neighbours in its slot,
    // and its place in the list of nodes a tick is about to fire. All but
    // FireNext are written only under the wheel's lock. See TimerWheel.
    internal long Due;
    internal TimerWheel.Wheel? Wheel;
    internal WheelNode? WheelPrev, WheelNext, FireNext;

    /// <summary>
    /// The deadline arrived. Called by the wheel's tick, which has already
    /// unlinked this node, outside the wheel's lock.
    /// </summary>
    internal abstract void FireFromWheel();
}

/// <summary>
/// One armed timeout: its place in the timer wheel, the sync block it is trying
/// to commit, and a gate.
///
/// NOT POOLED, deliberately, and the reason is the same ABA argument that allows
/// <see cref="EventAwaiter{T}"/> to be pooled — inverted. A pooled object is safe
/// only if every stale reference to it is dead, and channel resume delegates are
/// dead because they are gated by a CAS on their own op's <see cref="SyncState"/>.
/// A nack action has no such gate: <c>MarkSynchronized</c> fires it
/// unconditionally when the branch loses. A recycled node's cancel delegate could
/// therefore be invoked by a PREVIOUS sync's losing nack and take whatever the
/// node is doing NOW out of the wheel. A fresh node per armed sync makes a late
/// cancel harmless: it hits this node's gate and finds it already Done.
///
/// The gate (<see cref="_gate"/>) is the single owner-election point: whichever
/// of Fire and Cancel exchanges it first owns the node's way out of the wheel; the
/// loser does nothing. Fire may also lose the COMMIT (the block was won between
/// the deadline and the CAS), in which case it does nothing more, the tick having
/// unlinked the node already; the nack that made it lose has already run or will
/// run, and both find the gate Done.
/// </summary>
internal sealed class TimeoutNode : WheelNode
{
    private const int Pending = 0;
    private const int Done = 1;

    private readonly SyncState _state;
    private readonly int _eventId;
    private readonly Action<Unit> _onSync;
    private int _gate;

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
        // of taking a pointless trip through the wheel.
        if (ms <= 0)
        {
            if (state.TryCommit(eventId)) Scheduler.DispatchOwn(onSync, default);
            return;
        }

        // An earlier branch already won; don't build anything. Racy, but only as
        // an optimisation — the nack path below is what is load-bearing.
        if (state.IsSynchronized) return;

        var node = new TimeoutNode(state, eventId, onSync);

        // A leaf's subtree is exactly one branch index wide. Set before the
        // node is registered, so that no commit can read it unset.
        node.I1 = eventId + 1;

        // From this call on, Cancel can run concurrently on another thread.
        if (!state.RegisterNack(node, eventId))
        {
            // The block synchronised before we registered. RegisterNack enqueued
            // Cancel iff the winner is an earlier branch — always true here, since
            // later branches have not published yet. Cancel on this unarmed node
            // is a no-op: it wins the gate and finds nothing linked. Nothing to undo.
            return;
        }

        // Into the wheel, and then look at the gate. A concurrent Cancel flips
        // the gate and then unlinks: if it looked before the node was linked,
        // it found nothing to unlink, and this sees its gate and unlinks for it.
        // Both sides are fenced (an exchange there, the wheel's lock here), so
        // one of them always removes the node.
        TimerWheel.Add(node, ms);
        if (Volatile.Read(ref node._gate) == Done)
        {
            TimerWheel.Remove(node);
            return;
        }

        // Mirror of WithNackEvent's post-publish check. A MarkSynchronized that
        // raced the registration may have found no nack and skipped its list
        // (see there). If the block is synchronized now, we lost (only Fire can
        // make us win, and it has not committed), so cancel by hand. Idempotent.
        if (state.IsSynchronized) node.Cancel();
    }

    /// <summary>This node's nack: the branch lost.</summary>
    public override void Fire() => Cancel();

    /// <summary>The nack action: this branch lost, take it out of the wheel.</summary>
    private void Cancel()
    {
        if (Interlocked.Exchange(ref _gate, Done) == Done) return;
        TimerWheel.Remove(this);
    }

    /// <summary>
    /// The deadline arrived first; try to win the sync. Called by the wheel's
    /// tick, which has already unlinked this node.
    /// </summary>
    internal override void FireFromWheel()
    {
        if (Interlocked.Exchange(ref _gate, Done) == Done) return;

        // TryCommit, not TryClaim: this runs on the tick's thread long after
        // Publish returned and may find the state transiently Claimed by a
        // sibling — the same argument as Promise.Deliver.
        // Queued, not run here: the tick fires every due node in turn, and a
        // fiber run here would delay all the nodes after it.
        if (_state.TryCommit(_eventId)) Scheduler.Enqueue(_onSync, default);
    }
}

/// <summary>
/// A lone `(sync (timeout ms))`: a sleep. The fiber's resume goes into the
/// timer wheel, and nothing else is built.
///
/// The general way builds a <see cref="SyncState"/>, an
/// <see cref="EventAwaiter{T}"/> with its delegate, and a
/// <see cref="TimeoutNode"/>, and a sleeping fiber keeps all of them: 288 B.
/// A sync with one timer has no branches to arbitrate, so this is the
/// <see cref="IDirectSyncable{T}"/> park of a lone channel operation, with the
/// wheel as the channel. <see cref="BjolangRuntime.SyncOp{T}"/> parks it
/// through the same <see cref="BjolangRuntime.SyncAwaiter{T}"/> path, and the
/// fiber's cancellation watch withdraws it as it withdraws an op, through
/// <see cref="IParkSite.CancelParked"/>. The op argument of that path is null
/// here: the node is the op and the site at once.
///
/// One node per sleep, not pooled, for the reason <see cref="TimeoutNode"/>
/// is not: a late cancel or fire must find this node's gate closed, not a
/// later sleep's.
///
/// The gate decides who resumes the fiber, once:
/// <list type="bullet">
/// <item><see cref="Unpublished"/>: the fiber has not parked yet. A cancel
/// that closes the gate now leaves the resuming to <see cref="Park"/>.</item>
/// <item><see cref="Published"/>: parked, with the resume set. Whoever closes
/// the gate, the tick or the cancel, resumes the fiber.</item>
/// <item><see cref="Done"/>: closed.</item>
/// </list>
/// </summary>
internal sealed class SleepNode : WheelNode, IParkable<Unit>
{
    private const int Unpublished = 0;
    private const int Published = 1;
    private const int Done = 2;

    private readonly int _ms;
    private int _gate;
    private IResume? _resume;

    internal SleepNode(int ms) => _ms = ms;

    /// <summary>
    /// Into the wheel, with the fiber's resume. The op is null; see above.
    ///
    /// The resume is written before the gate opens, and the gate opens before
    /// the node is linked, so a tick that finds the node finds the resume.
    /// </summary>
    public ParkResult Park(Operation op, IResume resume)
    {
        _resume = resume;

        // Closed already: the token withdrew this sleep before it parked.
        if (Interlocked.CompareExchange(ref _gate, Published, Unpublished) != Unpublished)
            return ParkResult.Withdrawn;

        // A cancel can close the gate between the exchange and the link. It
        // then resumes the fiber, and its unlink found nothing, so the node is
        // unlinked here. Both sides are fenced (the exchange there, the
        // wheel's lock here), as in TimeoutNode.Arm.
        TimerWheel.Add(this, _ms);
        if (Volatile.Read(ref _gate) == Done) TimerWheel.Remove(this);
        return ParkResult.Parked;
    }

    /// <summary>A sleep carries no value.</summary>
    public Unit TakeParked(Operation op) => default;

    /// <summary>The token's withdrawal. Called once, by the fiber's watch.</summary>
    public Withdrawal CancelParked(Operation op, int gen)
    {
        switch (Interlocked.Exchange(ref _gate, Done))
        {
            case Published:
                TimerWheel.Remove(this);
                return Withdrawal.Published;
            case Unpublished:
                return Withdrawal.Unpublished;
            default:
                // The tick resumed the fiber first. The sleep ended normally.
                return Withdrawal.Refused;
        }
    }

    internal override void FireFromWheel()
    {
        // Only a published node is linked, so the gate is Published or Done.
        // Queued, as in TimeoutNode.FireFromWheel.
        if (Interlocked.CompareExchange(ref _gate, Done, Published) == Published)
            Scheduler.Enqueue(_resume!);
    }
}
