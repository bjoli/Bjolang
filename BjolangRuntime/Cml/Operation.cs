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

/// <summary>What became of an op handed to a channel to park.</summary>
internal enum ParkResult
{
    /// <summary>It is on the channel's list, waiting for a partner.</summary>
    Parked,

    /// <summary>A partner was already waiting, and the two met.</summary>
    Matched,

    /// <summary>
    /// The token withdrew it before it could be parked. Nothing was published,
    /// and resuming the fiber is up to whoever called <c>Park</c>.
    /// </summary>
    Withdrawn
}

/// <summary>What a withdrawal found.</summary>
internal enum Withdrawal
{
    /// <summary>The park is over: a partner took the op, or it was never this park.</summary>
    Refused,

    /// <summary>Withdrawn from the channel's list. The withdrawer resumes the fiber.</summary>
    Published,

    /// <summary>
    /// Withdrawn before its owner parked it. The owner's <c>Park</c> sees that
    /// and resumes the fiber itself.
    /// </summary>
    Unpublished
}

/// <summary>
/// The other end of a watched park: whatever can withdraw a parked op on behalf
/// of the token watching it. Non-generic, because the fiber's registration
/// watches parks on channels of every element type.
/// </summary>
internal interface IParkSite
{
    /// <summary>Withdraw <paramref name="op"/> if it is still live as <paramref name="gen"/>.</summary>
    Withdrawal CancelParked(Operation op, int gen);
}

public abstract class Operation
{
    public SyncState? State;
    public int EventId;

    /// <summary>
    /// The claim that decides whether a partner or the token gets a watched
    /// op. Positive while the park is live, zero once a partner (or, on an
    /// inline match, its owner) has taken it, or when no token watches it,
    /// <see cref="Withdrawn"/> once the token has taken it.
    ///
    /// The claim lives on the op because a partner already holds the channel's
    /// lock and is already writing to this op when it takes it, so taking it
    /// costs one plain store and no interlocked instruction. The token, which
    /// fires at most once, pays for the lock instead: it withdraws the op
    /// through <see cref="IParkSite.CancelParked"/>.
    ///
    /// Only read and written under the lock of the channel the op is parked in,
    /// except by the renter before the op is handed to the channel.
    /// </summary>
    internal int ParkedGen;

    /// <summary>
    /// Whether the op has been put on a channel's list. Set under the channel's
    /// lock, so that a withdrawal under the same lock can tell who resumes the
    /// fiber: the withdrawer if the op was waiting there, or the owner, whose
    /// <c>Park</c> has not run yet and will find the op withdrawn.
    /// </summary>
    internal bool Published;

    /// <summary>
    /// The last generation handed out for this op. It is kept when the op is
    /// recycled, so a generation identifies one park of one op. A token still
    /// holding a reference to an op that has since been recycled and parked
    /// again by another fiber names an old generation, and its withdrawal is
    /// refused.
    /// </summary>
    private int _gen;

    internal const int Withdrawn = -1;

    /// <summary>Mark this op as a watched park, and name it.</summary>
    internal int Watch()
    {
        int g = _gen + 1;
        if (g <= 0) g = 1;
        _gen = g;
        ParkedGen = g;
        return g;
    }

    public bool IsSynchronized => State != null && State.IsSynchronized;

    /// <summary>
    /// Takes this op for a caller holding no claim of its own: a direct send or
    /// receive. Waits out a transient claim on the op's state rather than
    /// skipping it; see <see cref="SyncState.TrySyncWaiting"/>.
    /// </summary>
    public bool TrySync() => State != null && State.TrySyncWaiting();

    /// <summary>
    /// Win a direct op. Caller holds the channel's lock. Unwatched ops cannot be
    /// contested, so they always win; a watched one is taken by clearing its
    /// generation, which is what turns the token's withdrawal away.
    /// </summary>
    internal bool TryTakeDirect()
    {
        if (ParkedGen < 0) return false;
        ParkedGen = 0;
        return true;
    }

    /// <summary>A direct op the token withdrew, which the channel may drop.</summary>
    internal bool IsCancelled => ParkedGen < 0;
}

public sealed class PutOp<T> : Operation
{
    // 256, not 64, because recycling is BURSTY where renting is steady. A K-wide
    // choose that keeps losing parks K-1 dead ops per sync, and every dead channel
    // hits its NotePark sweep threshold on the SAME iteration (they park in
    // lockstep), so up to (K-1) * 32 ops come back in one burst. With a cap of 64
    // most of that burst was dropped and the next 32 iterations allocated fresh:
    // the skewed-choose benchmark (bench/Bench varied) measured 310 B/op at cap 64
    // and 40 B/op — the un-poolable SyncState only — at 256. Worst case memory is
    // 256 * ~64 B = 16 KB per (T, thread), which is a cache, not a leak.
    private const int MaxCached = 256;
    [ThreadStatic] private static PutOp<T>? _free;
    [ThreadStatic] private static int _freeCount;

    public T Value = default!;

    /// <summary>Resume for a DIRECT send (awaiter path); null for choose givers.</summary>
    public Action ResumePut = null!;

    /// <summary>
    /// Resume for a send under a sync block. Typed <c>Action&lt;Unit&gt;</c> so
    /// <c>PublishSend</c> can carry the choose's <c>onSync</c> straight through:
    /// adapting it to a bare <c>Action</c> allocated a closure per publish per
    /// branch (88 B of the choose-send row's 128 B/op).
    /// </summary>
    public Action<Unit>? ResumeGive;

    public PutOp<T>? Next;

    /// <summary>Rent for a choose giver (parked by <c>PublishSend</c>).</summary>
    public static PutOp<T> Rent(SyncState? state, int eventId, T value, Action<Unit> resumeGive)
    {
        var op = _free;
        if (op is null)
        {
            return new PutOp<T>
            {
                State = state,
                EventId = eventId,
                Value = value,
                ResumeGive = resumeGive
            };
        }

        _free = op.Next;
        _freeCount--;
        op.Next = null;
        op.State = state;
        op.Published = false;
        op.EventId = eventId;
        op.Value = value;
        op.ResumeGive = resumeGive;
        return op;
    }

    /// <summary>Rent for a direct send; the awaiter sets <see cref="ResumePut"/> on park.</summary>
    public static PutOp<T> RentDirect(T value)
    {
        var op = _free;
        if (op is null) return new PutOp<T> { Value = value };

        _free = op.Next;
        _freeCount--;
        op.Next = null;
        op.State = null;
        op.Published = false;
        op.EventId = 0;
        op.Value = value;
        return op;
    }

    public void Recycle()
    {
        State = null;
        Published = false;
        ParkedGen = 0;
        Value = default!;
        ResumePut = null!;
        ResumeGive = null;
        EventId = 0;

        if (_freeCount < MaxCached)
        {
            Next = _free;
            _free = this;
            _freeCount++;
        }
        else
        {
            Next = null;
        }
    }
}

public sealed class GetOp<T> : Operation
{
    // See PutOp<T>.MaxCached: sized for the sweep bursts of a wide choose.
    private const int MaxCached = 256;
    [ThreadStatic] private static GetOp<T>? _free;
    [ThreadStatic] private static int _freeCount;

    public Action<T>? ResumeGet;
    public Action? DirectResume;
    public T DirectValue = default!;
    public GetOp<T>? Next;

    public static GetOp<T> Rent(SyncState? state, int eventId, Action<T>? resumeGet)
    {
        var op = _free;
        if (op is null)
        {
            return new GetOp<T>
            {
                State = state,
                EventId = eventId,
                ResumeGet = resumeGet
            };
        }

        _free = op.Next;
        _freeCount--;
        op.Next = null;
        op.State = state;
        op.Published = false;
        op.EventId = eventId;
        op.ResumeGet = resumeGet;
        op.DirectResume = null;
        op.DirectValue = default!;
        return op;
    }

    public static GetOp<T> RentDirect()
    {
        var op = _free;
        if (op is null)
        {
            return new GetOp<T>
            {
                State = null,
                EventId = 0,
                ResumeGet = null,
                DirectResume = null,
                DirectValue = default!
            };
        }

        _free = op.Next;
        _freeCount--;
        op.Next = null;
        op.State = null;
        op.Published = false;
        op.EventId = 0;
        op.ResumeGet = null;
        op.DirectResume = null;
        op.DirectValue = default!;
        return op;
    }

    public void Recycle()
    {
        State = null;
        Published = false;
        ParkedGen = 0;
        ResumeGet = null;
        DirectResume = null;
        DirectValue = default!;
        EventId = 0;

        if (_freeCount < MaxCached)
        {
            Next = _free;
            _free = this;
            _freeCount++;
        }
        else
        {
            Next = null;
        }
    }
}