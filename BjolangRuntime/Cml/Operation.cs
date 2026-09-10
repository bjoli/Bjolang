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

namespace Bjoml;

public abstract class Operation
{
    public SyncState? State;
    public int EventId;

    public bool IsSynchronized => State != null && State.IsSynchronized;

    public bool TrySync() => State != null && State.TrySync();
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
        op.EventId = 0;
        op.Value = value;
        return op;
    }

    public void Recycle()
    {
        State = null;
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
        op.EventId = 0;
        op.ResumeGet = null;
        op.DirectResume = null;
        op.DirectValue = default!;
        return op;
    }

    public void Recycle()
    {
        State = null;
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