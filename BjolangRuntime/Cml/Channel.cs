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
/// A synchronous, unbuffered channel.
///
/// It IS its own receive event: <see cref="Publish"/> is <see cref="PublishReceive"/>
/// and <see cref="TryNow"/> is <see cref="TryDirectReceive"/>, so `(chan-recv ch)`
/// hands back the channel rather than wrapping it. A send has to carry the value,
/// so it still needs an object of its own.
/// </summary>
public class Channel<T> : IEvent<T>, INowable<T>, IDirectSyncable<T>
{
    private readonly object _lock = new();
    private PutOp<T>? _giversHead;
    private PutOp<T>? _giversTail;
    private GetOp<T>? _takersHead;
    private GetOp<T>? _takersTail;

    public void Publish(SyncState state, int eventId, Action<T> onSync)
    {
        PublishReceive(state, eventId, onSync);
    }

    bool INowable<T>.TryNow(out T value) => TryDirectReceive(out value);

    void IDirectSyncable<T>.SyncDirect(Action<T> onSync) => SyncDirectReceive(onSync);

    /// <summary>
    /// Receive with no <see cref="SyncState"/>: commit against a parked giver if
    /// there is one, otherwise park uncontested.
    ///
    /// This is the whole of a <c>sync</c> on a bare receive. The general path
    /// costs a `SyncState` and, on every commit, two `MarkSynchronized` calls
    /// that each take a lock to walk a nack list — for a block that offered one
    /// thing and so has no branch to withdraw and no nack to fire.
    ///
    /// Only reachable from the top of a sync. A `choose` publishes through
    /// <see cref="Publish"/> and keeps the general protocol, which is what makes
    /// the unconditional commit sound: an op parked here cannot be inside a
    /// choose, so nothing can ever need to withdraw it.
    /// </summary>
    internal void SyncDirectReceive(Action<T> onSync)
    {
        Action? putResume = null;
        Action<Unit>? putResumeGive = null;
        SyncState? putState = null;
        int putEventId = 0;
        T putValue = default!;
        bool matched = false;

        lock (_lock)
        {
            PutOp<T>? prev = null;
            PutOp<T>? curr = _giversHead;

            while (curr != null)
            {
                var next = curr.Next;

                if (curr.State != null && curr.IsSynchronized)
                {
                    if (prev == null) _giversHead = next;
                    else prev.Next = next;

                    if (curr == _giversTail) _giversTail = prev;

                    curr.Recycle();
                    curr = next;
                    continue;
                }

                if (curr.State != null)
                {
                    // A choose giver still has to be claimed: it may be offering
                    // this send somewhere else as well.
                    if (curr.TrySync())
                    {
                        if (prev == null) _giversHead = next;
                        else prev.Next = next;

                        if (curr == _giversTail) _giversTail = prev;

                        putValue = curr.Value;
                        putResumeGive = curr.ResumeGive;
                        putState = curr.State;
                        putEventId = curr.EventId;
                        curr.Recycle();

                        matched = true;
                        break;
                    }

                    if (curr.IsSynchronized)
                    {
                        if (prev == null) _giversHead = next;
                        else prev.Next = next;

                        if (curr == _giversTail) _giversTail = prev;

                        curr.Recycle();
                        curr = next;
                        continue;
                    }
                }
                else
                {
                    if (prev == null) _giversHead = next;
                    else prev.Next = next;

                    if (curr == _giversTail) _giversTail = prev;

                    putValue = curr.Value;
                    if (curr.ResumeGive != null)
                    {
                        putResumeGive = curr.ResumeGive;
                        curr.Recycle();
                    }
                    else
                    {
                        putResume = curr.ResumePut;
                    }

                    matched = true;
                    break;
                }

                prev = curr;
                curr = next;
            }

            if (!matched)
            {
                NotePark();

                // State null: uncontested. The sweep leaves it alone, which is
                // right — a direct op is never dead, because it cannot lose.
                var myOp = GetOp<T>.Rent(null, 0, onSync);
                if (_takersTail == null)
                {
                    _takersHead = _takersTail = myOp;
                }
                else
                {
                    _takersTail.Next = myOp;
                    _takersTail = myOp;
                }
                return;
            }
        }

        putState?.MarkSynchronized(putEventId);

        if (putResumeGive != null) Scheduler.Dispatch(putResumeGive, Unit.Value);
        else Scheduler.Dispatch(putResume!);

        Scheduler.Dispatch(onSync, putValue);
    }

    /// <summary>Send with no <see cref="SyncState"/>. See <see cref="SyncDirectReceive"/>.</summary>
    internal void SyncDirectSend(T value, Action<Unit> onSync)
    {
        Action<T>? getResume = null;
        Action? directTakerResume = null;
        SyncState? getState = null;
        int getEventId = 0;
        bool matched = false;

        lock (_lock)
        {
            GetOp<T>? prev = null;
            GetOp<T>? curr = _takersHead;

            while (curr != null)
            {
                var next = curr.Next;

                if (curr.State != null && curr.IsSynchronized)
                {
                    if (prev == null) _takersHead = next;
                    else prev.Next = next;

                    if (curr == _takersTail) _takersTail = prev;

                    curr.Recycle();
                    curr = next;
                    continue;
                }

                if (curr.State != null)
                {
                    if (curr.TrySync())
                    {
                        if (prev == null) _takersHead = next;
                        else prev.Next = next;

                        if (curr == _takersTail) _takersTail = prev;

                        getResume = curr.ResumeGet;
                        getState = curr.State;
                        getEventId = curr.EventId;
                        curr.Recycle();

                        matched = true;
                        break;
                    }

                    if (curr.IsSynchronized)
                    {
                        if (prev == null) _takersHead = next;
                        else prev.Next = next;

                        if (curr == _takersTail) _takersTail = prev;

                        curr.Recycle();
                        curr = next;
                        continue;
                    }
                }
                else
                {
                    if (prev == null) _takersHead = next;
                    else prev.Next = next;

                    if (curr == _takersTail) _takersTail = prev;

                    if (curr.ResumeGet != null)
                    {
                        getResume = curr.ResumeGet;
                        curr.Recycle();
                    }
                    else
                    {
                        curr.DirectValue = value;
                        directTakerResume = curr.DirectResume;
                    }

                    matched = true;
                    break;
                }

                prev = curr;
                curr = next;
            }

            if (!matched)
            {
                NotePark();

                var myOp = PutOp<T>.Rent(null, 0, value, onSync);
                if (_giversTail == null)
                {
                    _giversHead = _giversTail = myOp;
                }
                else
                {
                    _giversTail.Next = myOp;
                    _giversTail = myOp;
                }
                return;
            }
        }

        getState?.MarkSynchronized(getEventId);

        if (getResume != null) Scheduler.Dispatch(getResume, value);
        else if (directTakerResume != null) Scheduler.Dispatch(directTakerResume);

        Scheduler.Dispatch(onSync, Unit.Value);
    }

    public ChannelReceiveAwaiter<T> GetAwaiter() => new(this);

    public ChannelReceiveOperation<T> Receive() => new(this);

    public ChannelSendOperation<T> Send(T value) => new(this, value);

    /// <summary>
    /// Number of parked receive operations. Cleans stale entries and returns active count.
    /// </summary>
    internal int PendingReceiveCount
    {
        get
        {
            lock (_lock)
            {
                CleanTakers();
                int count = 0;
                var curr = _takersHead;
                while (curr != null)
                {
                    count++;
                    curr = curr.Next;
                }
                return count;
            }
        }
    }

    /// <summary>
    /// Number of parked send operations. Cleans stale entries and returns active count.
    /// </summary>
    internal int PendingSendCount
    {
        get
        {
            lock (_lock)
            {
                CleanGivers();
                int count = 0;
                var curr = _giversHead;
                while (curr != null)
                {
                    count++;
                    curr = curr.Next;
                }
                return count;
            }
        }
    }

    /// <summary>
    /// Parked receive entries, counted WITHOUT cleaning first. Test-only.
    ///
    /// <see cref="PendingReceiveCount"/> calls <c>CleanTakers</c> before counting, so it
    /// can never observe a leak — reading it performs exactly the reclamation that a B7
    /// test is trying to prove happens on its own, and the test passes whether or not
    /// the bug is fixed. Proving entries do not accumulate requires the raw list.
    /// </summary>
    internal int RawPendingReceiveCount
    {
        get
        {
            lock (_lock)
            {
                int count = 0;
                for (var curr = _takersHead; curr != null; curr = curr.Next) count++;
                return count;
            }
        }
    }

    /// <summary>
    /// Parked receive entries with no <see cref="SyncState"/>. Test-only.
    ///
    /// A direct op commits unconditionally, which is only sound while no such op
    /// can be a branch of a <c>choose</c>. Nothing in the type system says so —
    /// it follows from <see cref="SyncDirectReceive"/> being reachable only from
    /// the top of a sync — so the tests assert it against the parked list.
    /// </summary>
    internal int RawDirectReceiveCount
    {
        get
        {
            lock (_lock)
            {
                int count = 0;
                for (var curr = _takersHead; curr != null; curr = curr.Next)
                    if (curr.State == null) count++;
                return count;
            }
        }
    }

    /// <summary>Parked send entries with no <see cref="SyncState"/>. Test-only.</summary>
    internal int RawDirectSendCount
    {
        get
        {
            lock (_lock)
            {
                int count = 0;
                for (var curr = _giversHead; curr != null; curr = curr.Next)
                    if (curr.State == null) count++;
                return count;
            }
        }
    }

    /// <summary>Parked send entries, counted without cleaning first. Test-only.</summary>
    internal int RawPendingSendCount
    {
        get
        {
            lock (_lock)
            {
                int count = 0;
                for (var curr = _giversHead; curr != null; curr = curr.Next) count++;
                return count;
            }
        }
    }

    // ---- B7: bounding the dead set ------------------------------------------
    //
    // A losing choose branch leaves its op parked here. Otherwise it is reclaimed only
    // if some LATER operation happens to walk past it, so a channel offered in a choose
    // that then goes quiet grows without bound.
    //
    // The trigger is a park counter, not the committing block. Every dead entry is
    // necessarily preceded by a PARK in this same channel, so counting parks bounds the
    // dead set without anyone having to remember which channels a block touched. The
    // count is plain, non-atomic, and read under _lock, which the park sites already
    // hold — so the fast path pays one increment and nothing else.
    //
    // The alternative, having SyncState record each channel and drive cleanup on
    // commit, gives a strictly stronger guarantee (zero stranded, not merely bounded)
    // and was measured at 36 ns/op on Select/Choose. See docs/design.md; do not
    // reintroduce it without re-measuring.
    private int _parksSinceSweep;
    private int _sweepThreshold = MinSweepThreshold;

    private const int MinSweepThreshold = 32;

    /// <summary>
    /// Called from the park sites with <c>_lock</c> already held. Sweeps once parks
    /// since the last sweep reach a threshold that scales with the live set, which
    /// amortises the O(n) walk to O(1) per park.
    /// </summary>
    private void NotePark()
    {
        if (++_parksSinceSweep < _sweepThreshold) return;

        _parksSinceSweep = 0;

        int live = CleanTakers() + CleanGivers();

        // Next sweep only once the dead set could again rival the live set. Without
        // this a channel with many genuine waiters would rescan them on every 32nd
        // park and never reclaim anything.
        _sweepThreshold = Math.Max(MinSweepThreshold, live * 2);
    }

    /// <summary>Unlink and recycle synchronized takers. Returns the surviving count.</summary>
    private int CleanTakers()
    {
        int live = 0;
        GetOp<T>? prev = null;
        GetOp<T>? curr = _takersHead;
        while (curr != null)
        {
            var next = curr.Next;
            if (curr.State != null && curr.IsSynchronized)
            {
                if (prev == null) _takersHead = next;
                else prev.Next = next;

                if (curr == _takersTail) _takersTail = prev;

                curr.Recycle();
            }
            else
            {
                prev = curr;
                live++;
            }
            curr = next;
        }
        return live;
    }

    /// <summary>Unlink and recycle synchronized givers. Returns the surviving count.</summary>
    private int CleanGivers()
    {
        int live = 0;
        PutOp<T>? prev = null;
        PutOp<T>? curr = _giversHead;
        while (curr != null)
        {
            var next = curr.Next;
            if (curr.State != null && curr.IsSynchronized)
            {
                if (prev == null) _giversHead = next;
                else prev.Next = next;

                if (curr == _giversTail) _giversTail = prev;

                curr.Recycle();
            }
            else
            {
                prev = curr;
                live++;
            }
            curr = next;
        }
        return live;
    }

    public void PublishSend(SyncState state, int eventId, T value, Action<Unit> onSync)
    {
        Action<T>? getResume = null;
        Action? directTakerResume = null;
        SyncState? getState = null;
        int getEventId = 0;
        bool matched = false;

        lock (_lock)
        {
            GetOp<T>? prev = null;
            GetOp<T>? curr = _takersHead;

            while (curr != null)
            {
                var next = curr.Next;

                if (curr.State != null && curr.IsSynchronized)
                {
                    if (prev == null) _takersHead = next;
                    else prev.Next = next;

                    if (curr == _takersTail) _takersTail = prev;

                    curr.Recycle();
                    curr = next;
                    continue;
                }

                if (curr.State != null && ReferenceEquals(curr.State, state))
                {
                    prev = curr;
                    curr = next;
                    continue;
                }

                if (state.TryClaim())
                {
                    if (curr.State != null)
                    {
                        if (curr.TrySync())
                        {
                            if (prev == null) _takersHead = next;
                            else prev.Next = next;

                            if (curr == _takersTail) _takersTail = prev;

                            getResume = curr.ResumeGet;
                            getState = curr.State;
                            getEventId = curr.EventId;
                            curr.Recycle();

                            matched = true;
                            break;
                        }

                        state.ResetClaim();
                        if (curr.IsSynchronized)
                        {
                            if (prev == null) _takersHead = next;
                            else prev.Next = next;

                            if (curr == _takersTail) _takersTail = prev;

                            curr.Recycle();
                            curr = next;
                            continue;
                        }
                    }
                    else
                    {
                        // A taker with no state is uncontested: it belongs to
                        // one sync that offered nothing else, so there is
                        // nothing to arbitrate and it commits unconditionally.
                        //
                        // Two resume shapes. `ResumeGet` takes the value as an
                        // argument and is a pooled awaiter's cached delegate,
                        // so the op is finished the moment it is unlinked and
                        // is recycled here. `DirectResume` is a bare `Action`
                        // belonging to a struct awaiter, which reads the value
                        // off the op afterwards and recycles it itself.
                        if (prev == null) _takersHead = next;
                        else prev.Next = next;

                        if (curr == _takersTail) _takersTail = prev;

                        if (curr.ResumeGet != null)
                        {
                            getResume = curr.ResumeGet;
                            curr.Recycle();
                        }
                        else
                        {
                            curr.DirectValue = value;
                            directTakerResume = curr.DirectResume;
                        }

                        matched = true;
                        break;
                    }
                }
                else
                {
                    return;
                }

                prev = curr;
                curr = next;
            }

            if (!matched)
            {
                if (state.IsSynchronized) return;
                NotePark();

                var myOp = PutOp<T>.Rent(state, eventId, value, onSync);
                if (_giversTail == null)
                {
                    _giversHead = _giversTail = myOp;
                }
                else
                {
                    _giversTail.Next = myOp;
                    _giversTail = myOp;
                }
                return;
            }
        }

        // Outside lock:
        state.MarkSynchronized(eventId);

        // Null for a taker that had no state to synchronize.
        getState?.MarkSynchronized(getEventId);

        if (getResume != null) Scheduler.Dispatch(getResume, value);
        else if (directTakerResume != null) Scheduler.Dispatch(directTakerResume);

        Scheduler.Dispatch(onSync, Unit.Value);
    }

    public void PublishReceive(SyncState state, int eventId, Action<T> resumeGet)
    {
        Action? putResume = null;           // a direct giver's continuation
        Action<Unit>? putResumeGive = null; // a choose giver's onSync
        SyncState? putState = null;
        int putEventId = 0;
        T putValue = default!;
        bool matched = false;

        lock (_lock)
        {
            PutOp<T>? prev = null;
            PutOp<T>? curr = _giversHead;

            while (curr != null)
            {
                var next = curr.Next;

                if (curr.State != null && curr.IsSynchronized)
                {
                    if (prev == null) _giversHead = next;
                    else prev.Next = next;

                    if (curr == _giversTail) _giversTail = prev;

                    curr.Recycle();
                    curr = next;
                    continue;
                }

                if (curr.State != null && ReferenceEquals(curr.State, state))
                {
                    prev = curr;
                    curr = next;
                    continue;
                }

                if (state.TryClaim())
                {
                    if (curr.State != null)
                    {
                        if (curr.TrySync())
                        {
                            if (prev == null) _giversHead = next;
                            else prev.Next = next;

                            if (curr == _giversTail) _giversTail = prev;

                            putValue = curr.Value;
                            putResumeGive = curr.ResumeGive;
                            putState = curr.State;
                            putEventId = curr.EventId;
                            curr.Recycle();

                            matched = true;
                            break;
                        }

                        state.ResetClaim();
                        if (curr.IsSynchronized)
                        {
                            if (prev == null) _giversHead = next;
                            else prev.Next = next;

                            if (curr == _giversTail) _giversTail = prev;

                            curr.Recycle();
                            curr = next;
                            continue;
                        }
                    }
                    else
                    {
                        // A giver with no state is uncontested; see PublishSend
                        // for why, and for the two resume shapes. The value is
                        // on the op either way — a send has to carry it — so
                        // only the resume differs.
                        if (prev == null) _giversHead = next;
                        else prev.Next = next;

                        if (curr == _giversTail) _giversTail = prev;

                        putValue = curr.Value;
                        if (curr.ResumeGive != null)
                        {
                            putResumeGive = curr.ResumeGive;
                            curr.Recycle();
                        }
                        else
                        {
                            putResume = curr.ResumePut;
                        }

                        matched = true;
                        break;
                    }
                }
                else
                {
                    return;
                }

                prev = curr;
                curr = next;
            }

            if (!matched)
            {
                if (state.IsSynchronized) return;
                NotePark();

                var myOp = GetOp<T>.Rent(state, eventId, resumeGet);
                if (_takersTail == null)
                {
                    _takersHead = _takersTail = myOp;
                }
                else
                {
                    _takersTail.Next = myOp;
                    _takersTail = myOp;
                }
                return;
            }
        }

        // Outside lock:
        state.MarkSynchronized(eventId);

        // Null for a giver that had no state to synchronize.
        putState?.MarkSynchronized(putEventId);

        if (putResumeGive != null) Scheduler.Dispatch(putResumeGive, Unit.Value);
        else Scheduler.Dispatch(putResume!);

        Scheduler.Dispatch(resumeGet, putValue);
    }

    // -----------------------------------------------------------------------
    // Direct Channel Operations (Non-Selective Fast Paths)
    // -----------------------------------------------------------------------

    public bool TryDirectReceive(out T value)
    {
        Action? putResume = null;
        Action<Unit>? putResumeGive = null;
        SyncState? putState = null;
        int putEventId = 0;
        value = default!;

        lock (_lock)
        {
            PutOp<T>? prev = null;
            PutOp<T>? curr = _giversHead;

            while (curr != null)
            {
                var next = curr.Next;

                if (curr.State != null && curr.IsSynchronized)
                {
                    if (prev == null) _giversHead = next;
                    else prev.Next = next;

                    if (curr == _giversTail) _giversTail = prev;

                    curr.Recycle();
                    curr = next;
                    continue;
                }

                if (curr.State != null)
                {
                    if (curr.TrySync())
                    {
                        if (prev == null) _giversHead = next;
                        else prev.Next = next;

                        if (curr == _giversTail) _giversTail = prev;

                        value = curr.Value;
                        putResumeGive = curr.ResumeGive;
                        putState = curr.State;
                        putEventId = curr.EventId;
                        curr.Recycle();
                        break;
                    }

                    if (curr.IsSynchronized)
                    {
                        if (prev == null) _giversHead = next;
                        else prev.Next = next;

                        if (curr == _giversTail) _giversTail = prev;

                        curr.Recycle();
                        curr = next;
                        continue;
                    }
                }
                else
                {
                    // Uncontested giver; see PublishReceive for the two shapes.
                    if (prev == null) _giversHead = next;
                    else prev.Next = next;

                    if (curr == _giversTail) _giversTail = prev;

                    value = curr.Value;
                    if (curr.ResumeGive != null)
                    {
                        putResumeGive = curr.ResumeGive;
                        curr.Recycle();
                    }
                    else
                    {
                        putResume = curr.ResumePut;
                    }
                    break;
                }

                prev = curr;
                curr = next;
            }
        }

        if (putResumeGive != null)
        {
            putState?.MarkSynchronized(putEventId);
            Scheduler.Dispatch(putResumeGive, Unit.Value);
            return true;
        }

        if (putResume != null)
        {
            Scheduler.Dispatch(putResume);
            return true;
        }

        return false;
    }

    public void ParkDirectReceive(GetOp<T> op)
    {
        Action? putResume = null;
        Action<Unit>? putResumeGive = null;
        SyncState? putState = null;
        int putEventId = 0;
        bool matched = false;
        T val = default!;

        lock (_lock)
        {
            PutOp<T>? prev = null;
            PutOp<T>? curr = _giversHead;

            while (curr != null)
            {
                var next = curr.Next;

                if (curr.State != null && curr.IsSynchronized)
                {
                    if (prev == null) _giversHead = next;
                    else prev.Next = next;

                    if (curr == _giversTail) _giversTail = prev;

                    curr.Recycle();
                    curr = next;
                    continue;
                }

                if (curr.State != null)
                {
                    if (curr.TrySync())
                    {
                        if (prev == null) _giversHead = next;
                        else prev.Next = next;

                        if (curr == _giversTail) _giversTail = prev;

                        val = curr.Value;
                        putResumeGive = curr.ResumeGive;
                        putState = curr.State;
                        putEventId = curr.EventId;
                        curr.Recycle();

                        matched = true;
                        break;
                    }

                    if (curr.IsSynchronized)
                    {
                        if (prev == null) _giversHead = next;
                        else prev.Next = next;

                        if (curr == _giversTail) _giversTail = prev;

                        curr.Recycle();
                        curr = next;
                        continue;
                    }
                }
                else
                {
                    // Uncontested giver; see PublishReceive for the two shapes.
                    if (prev == null) _giversHead = next;
                    else prev.Next = next;

                    if (curr == _giversTail) _giversTail = prev;

                    val = curr.Value;
                    if (curr.ResumeGive != null)
                    {
                        putResumeGive = curr.ResumeGive;
                        curr.Recycle();
                    }
                    else
                    {
                        putResume = curr.ResumePut;
                    }

                    matched = true;
                    break;
                }

                prev = curr;
                curr = next;
            }

            if (matched)
            {
                op.DirectValue = val;
            }
            else
            {
                if (_takersTail == null)
                {
                    _takersHead = _takersTail = op;
                }
                else
                {
                    _takersTail.Next = op;
                    _takersTail = op;
                }
                return;
            }
        }

        putState?.MarkSynchronized(putEventId);

        if (putResumeGive != null) Scheduler.Dispatch(putResumeGive, Unit.Value);
        else Scheduler.Dispatch(putResume!);

        Scheduler.Enqueue(op.DirectResume!);
    }

    public bool TryDirectSend(T value)
    {
        Action<T>? getResume = null;
        Action? directResume = null;
        SyncState? getState = null;
        int getEventId = 0;

        lock (_lock)
        {
            GetOp<T>? prev = null;
            GetOp<T>? curr = _takersHead;

            while (curr != null)
            {
                var next = curr.Next;

                if (curr.State != null && curr.IsSynchronized)
                {
                    if (prev == null) _takersHead = next;
                    else prev.Next = next;

                    if (curr == _takersTail) _takersTail = prev;

                    curr.Recycle();
                    curr = next;
                    continue;
                }

                if (curr.State != null)
                {
                    if (curr.TrySync())
                    {
                        if (prev == null) _takersHead = next;
                        else prev.Next = next;

                        if (curr == _takersTail) _takersTail = prev;

                        getResume = curr.ResumeGet;
                        getState = curr.State;
                        getEventId = curr.EventId;
                        curr.Recycle();
                        break;
                    }

                    if (curr.IsSynchronized)
                    {
                        if (prev == null) _takersHead = next;
                        else prev.Next = next;

                        if (curr == _takersTail) _takersTail = prev;

                        curr.Recycle();
                        curr = next;
                        continue;
                    }
                }
                else
                {
                    // Uncontested taker; see PublishSend for the two shapes.
                    if (prev == null) _takersHead = next;
                    else prev.Next = next;

                    if (curr == _takersTail) _takersTail = prev;

                    if (curr.ResumeGet != null)
                    {
                        getResume = curr.ResumeGet;
                        curr.Recycle();
                    }
                    else
                    {
                        curr.DirectValue = value;
                        directResume = curr.DirectResume;
                    }
                    break;
                }

                prev = curr;
                curr = next;
            }
        }

        if (getResume != null)
        {
            getState?.MarkSynchronized(getEventId);
            Scheduler.Dispatch(getResume, value);
            return true;
        }

        if (directResume != null)
        {
            Scheduler.Dispatch(directResume);
            return true;
        }

        return false;
    }

    public void ParkDirectSend(PutOp<T> op)
    {
        Action<T>? getResume = null;
        Action? directResume = null;
        SyncState? getState = null;
        int getEventId = 0;
        bool matched = false;

        lock (_lock)
        {
            GetOp<T>? prev = null;
            GetOp<T>? curr = _takersHead;

            while (curr != null)
            {
                var next = curr.Next;

                if (curr.State != null && curr.IsSynchronized)
                {
                    if (prev == null) _takersHead = next;
                    else prev.Next = next;

                    if (curr == _takersTail) _takersTail = prev;

                    curr.Recycle();
                    curr = next;
                    continue;
                }

                if (curr.State != null)
                {
                    if (curr.TrySync())
                    {
                        if (prev == null) _takersHead = next;
                        else prev.Next = next;

                        if (curr == _takersTail) _takersTail = prev;

                        getResume = curr.ResumeGet;
                        getState = curr.State;
                        getEventId = curr.EventId;
                        curr.Recycle();

                        matched = true;
                        break;
                    }

                    if (curr.IsSynchronized)
                    {
                        if (prev == null) _takersHead = next;
                        else prev.Next = next;

                        if (curr == _takersTail) _takersTail = prev;

                        curr.Recycle();
                        curr = next;
                        continue;
                    }
                }
                else
                {
                    // Direct taker
                    if (prev == null) _takersHead = next;
                    else prev.Next = next;

                    if (curr == _takersTail) _takersTail = prev;

                    curr.DirectValue = op.Value;
                    directResume = curr.DirectResume;
                    matched = true;
                    break;
                }

                prev = curr;
                curr = next;
            }

            if (!matched)
            {
                if (_giversTail == null)
                {
                    _giversHead = _giversTail = op;
                }
                else
                {
                    _giversTail.Next = op;
                    _giversTail = op;
                }
                return;
            }
        }

        getState?.MarkSynchronized(getEventId);

        if (getResume != null) Scheduler.Dispatch(getResume, op.Value);
        else if (directResume != null) Scheduler.Dispatch(directResume);

        Scheduler.Enqueue(op.ResumePut);
    }
}

public readonly struct ChannelReceiveAwaiter<T> : ICriticalNotifyCompletion
{
    private readonly Channel<T> _channel;
    private readonly T _result;
    private readonly bool _isCompleted;
    private readonly GetOp<T>? _op;

    public ChannelReceiveAwaiter(Channel<T> channel)
    {
        _channel = channel;
        if (channel.TryDirectReceive(out _result))
        {
            _isCompleted = true;
            _op = null;
        }
        else
        {
            _isCompleted = false;
            _op = GetOp<T>.RentDirect();
        }
    }

    public bool IsCompleted => _isCompleted;

    public T GetResult()
    {
        if (_op != null)
        {
            var res = _op.DirectValue;
            _op.Recycle();
            return res;
        }
        return _result;
    }

    public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

    public void UnsafeOnCompleted(Action continuation)
    {
        _op!.DirectResume = continuation;
        _channel.ParkDirectReceive(_op);
    }
}

public readonly struct ChannelSendAwaiter<T> : ICriticalNotifyCompletion
{
    private readonly Channel<T> _channel;
    private readonly bool _isCompleted;
    private readonly PutOp<T>? _op;

    public ChannelSendAwaiter(Channel<T> channel, T value)
    {
        _channel = channel;
        if (channel.TryDirectSend(value))
        {
            _isCompleted = true;
            _op = null;
        }
        else
        {
            _isCompleted = false;
            _op = PutOp<T>.RentDirect(value);
        }
    }

    public bool IsCompleted => _isCompleted;

    public void GetResult()
    {
        _op?.Recycle();
    }

    public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

    public void UnsafeOnCompleted(Action continuation)
    {
        _op!.ResumePut = continuation;
        _channel.ParkDirectSend(_op);
    }
}

public readonly struct ChannelReceiveOperation<T> : IEvent<T>
{
    private readonly Channel<T> _channel;

    public ChannelReceiveOperation(Channel<T> channel) => _channel = channel;

    public ChannelReceiveAwaiter<T> GetAwaiter() => new(_channel);

    public void Publish(SyncState state, int eventId, Action<T> onSync)
        => _channel.PublishReceive(state, eventId, onSync);
}

public readonly struct ChannelSendOperation<T> : IEvent<Unit>
{
    private readonly Channel<T> _channel;
    private readonly T _value;

    public ChannelSendOperation(Channel<T> channel, T value)
    {
        _channel = channel;
        _value = value;
    }

    public ChannelSendAwaiter<T> GetAwaiter() => new(_channel, _value);

    public void Publish(SyncState state, int eventId, Action<Unit> onSync)
        => _channel.PublishSend(state, eventId, _value, onSync);
}
