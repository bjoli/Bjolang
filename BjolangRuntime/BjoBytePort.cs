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

// Bytes, under the text. The layer `BjoPort` is a layer over.
//
// THE ONE INVARIANT, which everything here follows from:
//
//   Every byte that leaves the operating system lands in the port's buffer. A
//   read never takes bytes from the underlying stream — it takes them from the
//   buffer, and only when it wins.
//
// Commitment is therefore a cursor move inside the port and nothing else. That
// is what makes a read safe inside a `choose`; it is why `peek` is the same
// mechanism with the last step removed rather than a second one; and it is why
// a refill that lands after its branch has already lost is harmless rather than
// data loss.
//
// It is also `BjoPort`'s own rule one layer down. That file's header says:
//
//   Every virtual read is overridden, and that is load bearing. If any
//   inherited path reached `inner` while the buffer still held characters,
//   those characters would be skipped — silently, as wrong output rather than
//   as an exception. The rule for anything added here: read through the buffer,
//   or drain the buffer first.
//
// `AsStream` below is where that rule bites here: the text layer is built over
// a `Stream` that serves the port's buffer first and refills *through* the
// port, never over the port's own `Stream`. Reaching past the port would skip
// whatever a peek or an overshooting refill had already pulled in, silently.
//
// WHAT IS DELIBERATELY NOT HERE
//
//   * A cancellable refill. `Stream.ReadAsync` that has already taken bytes
//     from the kernel cannot put them back, so a cancelled refill is lost data.
//     A refill belongs to the PORT and runs to completion; the thing that gets
//     cancelled is the fiber's WAIT for it, which loses nothing because the
//     bytes land in the buffer either way.
//   * Two refills at once. Two concurrent `ReadAsync` on one `Stream` is
//     undefined behaviour in .NET, so a second reader joins the one in flight
//     rather than starting another.
//   * Interleaving byte reads and text reads on one port. `byte->text-input-port`
//     is one-way; see `AsStream`.

using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bjoml;
using Unit = Bjoml.Unit;
using ByteRead = BjolangRuntime.Result<System.Exception, BjolangRuntime.Option<byte[]>>;

namespace Bjolang.Runtime;

/// <summary>
/// A stream that can end its write half without ending its read half, which is
/// what `shutdown!` needs and what `Stream` has no word for.
///
/// Implemented by <see cref="BjoBytePipe"/> here; a `NetworkStream` is handled
/// by <see cref="BjoByteOutputPort.Shutdown"/> directly, since it is .NET's and
/// cannot be made to implement this.
/// </summary>
public interface IHalfClosable {
    /// No more bytes will be written. A reader on the other end drains what is
    /// already there and then sees end of input.
    void CloseWrite();
}

/// <summary>
/// A buffered byte source whose reads are CML events.
///
/// **The port owns the buffer, and the buffer is the only place bytes live.**
/// A read is a cursor move; a peek is the same look with the cursor left alone;
/// a refill is the port's own business and is never cancelled. See the file
/// header.
///
/// # Two fibers, one port
///
/// Allowed, and not guarded against: two fibers reading one port serialize on
/// the single in-flight refill and on the buffer lock. What is *not* promised
/// is which of them gets which bytes — that is theirs to arrange. Every byte is
/// handed out exactly once.
///
/// # Errors
///
/// A failure from the underlying stream is sticky, exactly as end of input is.
/// It reaches a read in one of two shapes, and which one depends on where the
/// reader was standing when it landed:
///
///   * As a value, <c>Err e</c>, out of the read events. The CML resume path
///     carries a value and cannot carry a raise — which is why
///     <c>promise-join</c>, <c>blocking</c> and <c>task-&gt;event</c> all yield a
///     <c>Result</c> too. The language's `read-some`/`read-bytes` unwrap it and
///     raise on the fiber's own stack.
///   * As a throw, out of <see cref="Eof"/>, <see cref="Peek"/> and the
///     <see cref="AsStream"/> reads, all of which are ordinary awaits and so
///     already stand on the caller's stack.
///
/// Bytes that arrived before the failure are handed out first. The failure is
/// only reported once the buffer is empty, because those bytes are data.
/// </summary>
public sealed class BjoByteInputPort : IDisposable {
    private const int DefaultBufferSize = 4096;

    private readonly object _lock = new();
    private readonly Stream _inner;

    /// Whether disposing this port disposes the stream under it. False for the
    /// read half of a pipe and for `limited`, where the stream is a view of
    /// something the caller still owns.
    private readonly bool _ownsInner;

    // The buffer, and the window of it that holds unread bytes.
    //
    // `_buf` and `_len` are moved ONLY while preparing a refill, and there is at
    // most one refill; a taker only ever moves `_pos`, forwards, and never past
    // `_len`. That pair of facts is what lets the pump write into
    // `_buf[at.._buf.Length]` outside the lock without a copy.
    private byte[] _buf;
    private int _pos;
    private int _len;

    /// The stream answered zero once. Sticky: a stream does not un-end.
    private bool _ended;

    /// The stream failed once. Sticky, for the same reason, and reported only
    /// after whatever had already arrived has been handed out.
    private Exception? _error;

    private bool _disposed;

    /// The refill in flight, or null. Holding it in a field is the whole of
    /// "at most one": a second reader awaits this rather than starting another.
    private Task? _refill;

    /// A take is tentatively holding the cursor: it has moved <c>_pos</c> and is
    /// out at <c>TryCommit</c>, which may yet fail and put it back. Nothing else
    /// may take while this is set.
    ///
    /// It cannot be held for long — <c>TryCommit</c> spins past another branch's
    /// transient claim and runs no I/O — and it is cleared on both paths.
    private bool _taking;

    /// Completed and replaced whenever <c>_taking</c> clears, for the async
    /// readers that cannot sit on the monitor.
    private TaskCompletionSource? _quiet;

    /// Whoever is waiting for bytes, oldest first. Singly linked and appended by
    /// walking: a port has one or two waiters, not a queue.
    private Waiter? _waiters;

    private const int Idle = 0;
    private const int Running = 1;
    private const int RunningAgain = 2;
    private int _deliverState;

    /// <summary>See <see cref="BjoPort.Owner"/>. Null for a port nothing owns —
    /// a pipe, a `limited` view — and set by whatever opened a real handle.</summary>
    public BjolangRuntime.Owned? Owner;

    public BjoByteInputPort(Stream inner) : this(inner, DefaultBufferSize, true) { }

    public BjoByteInputPort(Stream inner, bool ownsInner) : this(inner, DefaultBufferSize, ownsInner) { }

    /// The buffer size is settable for the reason `BjoPort`'s is: every
    /// interesting bug in a buffered reader lives at a buffer boundary, and a
    /// test that cannot put the boundary where it wants cannot reach them.
    public BjoByteInputPort(Stream inner, int bufferSize, bool ownsInner = true) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        if (!inner.CanRead)
            throw new ArgumentException("a byte input port needs a readable stream.", nameof(inner));

        _inner = inner;
        _ownsInner = ownsInner;
        _buf = new byte[bufferSize];
    }

    /// The stream this port reads from, for a caller that has to hand it to a
    /// .NET API. **Not** the thing to build a text reader over — see
    /// <see cref="AsStream"/>.
    internal Stream Inner => _inner;

    private bool Finished => _ended || _error is not null;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// Rethrow with the original stack rather than a fresh one. Written as a
    /// function returning `Exception` so a call site can be `throw Rethrow(e)`
    /// and the compiler can see the flow end there.
    private static Exception Rethrow(Exception e) {
        ExceptionDispatchInfo.Capture(e).Throw();
        return e;
    }

    // --- The buffer ---------------------------------------------------------

    /// <summary>
    /// Room for <paramref name="want"/> unread bytes, compacting and growing as
    /// needed. Under the lock, and only ever from <see cref="EnsureRefill"/>,
    /// which is what keeps the array still while a pump is writing into it.
    ///
    /// Growing is how a read of more than one bufferful stays a cursor move: a
    /// `(read-bytes p 100000)` is asking for an array of that size anyway, so
    /// holding it in the buffer costs nothing that was not already going to be
    /// paid.
    /// </summary>
    private void MakeRoomLocked(int want) {
        int have = _len - _pos;
        int need = Math.Max(want, have + 1);

        if (_buf.Length < need) {
            var bigger = new byte[Math.Max(need, _buf.Length * 2)];
            if (have > 0) Buffer.BlockCopy(_buf, _pos, bigger, 0, have);
            _buf = bigger;
        } else if (have + (_buf.Length - _len) < need) {
            if (have > 0) Buffer.BlockCopy(_buf, _pos, _buf, 0, have);
        } else {
            return; // there is already room after `_len`
        }

        _pos = 0;
        _len = have;
    }

    /// <summary>
    /// How many bytes a request for <paramref name="count"/> may take right
    /// now: -1 for "not yet, wait", 0 for "there will never be any more", and
    /// otherwise the count to hand over.
    ///
    /// <paramref name="count"/> of 0 means `read-some`: whatever is there, and
    /// never an empty array. A positive count is exactly that many, except at
    /// end of input, where the short remainder is handed over and the call
    /// after it answers 0.
    ///
    /// Buffered bytes are served before a sticky failure is reported, because
    /// bytes that arrived are data.
    /// </summary>
    private int TakeableLocked(int count) {
        int have = _len - _pos;

        if (count <= 0) return have > 0 ? have : (Finished ? 0 : -1);
        if (have >= count) return count;
        return Finished ? have : -1;
    }

    /// <summary>
    /// Move the cursor and hand the bytes over. Under the lock, and the only
    /// place the cursor ever moves forward.
    /// </summary>
    private ByteRead TakeLocked(int n) {
        if (n <= 0) {
            return _error is not null
                ? ByteRead.Err(_error)
                : ByteRead.Ok(BjolangRuntime.None<byte[]>());
        }

        var bytes = new byte[n];
        Buffer.BlockCopy(_buf, _pos, bytes, 0, n);
        _pos += n;
        return ByteRead.Ok(BjolangRuntime.Some(bytes));
    }

    // --- Refilling ----------------------------------------------------------
    //
    // A refill is the port's, not a reader's. It is started with no
    // cancellation token at all, it completes whatever happens to whoever asked
    // for it, and it deposits into the buffer. A reader that has gone away —
    // lost its `choose`, hit its deadline — loses nothing by it: the bytes are
    // in the buffer for the next reader.
    //
    // The task is completed rather than faulted even when the read threw. A
    // failure is recorded on the port, where it is sticky and where a later
    // reader can find it; a faulted task would have to be observed by every
    // waiter or reported as unhandled.

    /// <summary>
    /// The refill in flight, starting one if there is none. Never faults, and
    /// never cancels.
    /// </summary>
    private Task EnsureRefill(int want) {
        TaskCompletionSource tcs;
        byte[] buf;
        int at, room;

        lock (_lock) {
            if (_refill is not null) return _refill;
            if (_disposed || Finished) return Task.CompletedTask;

            MakeRoomLocked(want);
            buf = _buf;
            at = _len;
            room = _buf.Length - _len;

            // Published into the field BEFORE the read starts, so that a read
            // completing synchronously cannot find `_refill` still null and let
            // a second one through.
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _refill = tcs.Task;
        }

        // Started outside the lock. A synchronous completion runs `Pump` to its
        // end right here, and its end is `Deliver`, which commits waiters and
        // resumes fibers — none of which may happen with the buffer lock held.
        _ = Pump(tcs, buf, at, room);
        return tcs.Task;
    }

    private async Task Pump(TaskCompletionSource tcs, byte[] buf, int at, int room) {
        Exception? failure = null;
        int n = 0;

        try {
            n = await _inner.ReadAsync(buf.AsMemory(at, room), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception e) {
            failure = e;
        }

        lock (_lock) {
            // `_len` is assigned only after the read has returned, which is what
            // keeps a failed refill from leaving a stale length behind that
            // would re-serve bytes already handed out.
            if (failure is not null) _error ??= failure;
            else if (n <= 0) _ended = true;
            else _len = at + n;

            // Cleared before the task is completed, so a waiter woken by it that
            // still cannot be satisfied asks for a FRESH refill rather than
            // finding this spent one.
            _refill = null;
        }

        tcs.TrySetResult();
        Deliver();
    }

    // --- Waiting ------------------------------------------------------------

    /// <summary>
    /// One published read, parked until the buffer can answer it.
    ///
    /// The three steps are apart on purpose. <c>BeginLocked</c> runs under the
    /// buffer lock and moves the cursor tentatively; <c>TryCommit</c> runs
    /// outside it, because committing fires the losing branches' nacks and a
    /// nack can resume a fiber; and then either <c>Handover</c> or
    /// <c>UndoLocked</c>. Nothing is consumed by a branch that loses.
    /// </summary>
    private abstract class Waiter {
        public SyncState State = null!;
        public int EventId;
        public Waiter? Next;

        /// What a refill on this waiter's behalf has to make room for.
        public abstract int Want { get; }

        /// Under the lock: can this be answered now, and if so take it.
        public abstract bool BeginLocked(BjoByteInputPort p);

        /// Outside the lock, the commit having succeeded.
        public abstract void Handover();

        /// Under the lock, the commit having failed. Put the cursor back.
        public abstract void UndoLocked(BjoByteInputPort p);
    }

    private sealed class ReadWaiter : Waiter {
        /// 0 is `read-some`; a positive count is `read-bytes`.
        public int Count;
        public Action<ByteRead> OnSync = null!;

        private ByteRead _value;
        private int _oldPos;

        public override int Want => Count <= 0 ? 1 : Count;

        public override bool BeginLocked(BjoByteInputPort p) {
            int n = p.TakeableLocked(Count);
            if (n < 0) return false;

            _oldPos = p._pos;
            _value = p.TakeLocked(n);
            return true;
        }

        public override void Handover() {
            var v = _value;
            _value = default;
            InboxWake.Resume(OnSync, v);
        }

        public override void UndoLocked(BjoByteInputPort p) {
            p._pos = _oldPos;
            _value = default;
        }
    }

    /// The port is finished — cleanly or because it failed. It says only that,
    /// which is what makes it the right thing to wait on for "the other end has
    /// gone away"; a read beside it is what says which of the two happened.
    private sealed class EofWaiter : Waiter {
        public Action<Unit> OnSync = null!;

        public override int Want => 1;

        public override bool BeginLocked(BjoByteInputPort p) => p._pos >= p._len && p.Finished;

        public override void Handover() => InboxWake.Resume(OnSync, Unit.Value);

        public override void UndoLocked(BjoByteInputPort p) { }
    }

    private void AppendLocked(Waiter w) {
        if (_waiters is null) {
            _waiters = w;
            return;
        }

        var last = _waiters;
        while (last.Next is not null) last = last.Next;
        last.Next = w;
    }

    /// Unlink <paramref name="cur"/> and answer what follows it. Under the lock.
    private Waiter? UnlinkLocked(Waiter? prev, Waiter cur) {
        var next = cur.Next;
        if (prev is null) _waiters = next;
        else prev.Next = next;
        cur.Next = null;
        return next;
    }

    /// <summary>
    /// Match what is buffered with who is waiting until nothing more can be
    /// matched, then start a refill if anyone is still waiting.
    ///
    /// ONE RUNNER, NOT RECURSION, for the reason `Inbox.Settle` gives: serving a
    /// waiter can complete a refill inline, which re-enters this, and writing
    /// that as recursion makes the stack as deep as the port is busy. A second
    /// arrival leaves a note instead and whoever is already here goes round
    /// again.
    /// </summary>
    private void Deliver() {
        if (Interlocked.CompareExchange(ref _deliverState, Running, Idle) != Idle) {
            Volatile.Write(ref _deliverState, RunningAgain);
            return;
        }

        do {
            Volatile.Write(ref _deliverState, Running);

            while (ServeStep()) { }

            RefillForWaiters();
        }
        while (Interlocked.CompareExchange(ref _deliverState, Idle, Running) != Running);
    }

    /// <summary>
    /// One waiter served, or one that had already lost swept away. False when
    /// there is nothing left to do.
    /// </summary>
    private bool ServeStep() {
        Waiter? chosen = null;

        lock (_lock) {
            // Another take is tentatively holding the cursor. Whoever it is will
            // clear it and call back here.
            if (_taking) return false;

            Waiter? prev = null;
            var cur = _waiters;

            while (cur is not null) {
                // Lost elsewhere in its own sync block, and swept the next time
                // anything walks the list — the way a channel reclaims the ops
                // of a choose that lost.
                if (cur.State.IsSynchronized) {
                    cur = UnlinkLocked(prev, cur);
                    continue;
                }

                if (cur.BeginLocked(this)) {
                    chosen = cur;
                    UnlinkLocked(prev, cur);
                    break;
                }

                prev = cur;
                cur = cur.Next;
            }

            if (chosen is null) return false;
            _taking = true;
        }

        if (chosen.State.TryCommit(chosen.EventId)) {
            EndTake();
            chosen.Handover();
            return true;
        }

        // Another branch of that sync won in the window above. Nothing is
        // consumed: the cursor goes back exactly where it was, and the bytes
        // stay for the next reader.
        lock (_lock) { chosen.UndoLocked(this); }
        EndTake();
        return true;
    }

    private void EndTake() {
        TaskCompletionSource? quiet;

        lock (_lock) {
            _taking = false;
            quiet = _quiet;
            _quiet = null;
            Monitor.PulseAll(_lock);
        }

        quiet?.TrySetResult();
    }

    /// Under the lock: the signal that the tentative take has finished.
    private Task QuietLocked() {
        _quiet ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _quiet.Task;
    }

    private void RefillForWaiters() {
        int want = 0;

        lock (_lock) {
            if (_refill is not null || _disposed || Finished) return;

            for (var w = _waiters; w is not null; w = w.Next)
                if (!w.State.IsSynchronized && w.Want > want)
                    want = w.Want;
        }

        if (want > 0) EnsureRefill(want);
    }

    // --- The events' two halves ---------------------------------------------

    /// <summary>
    /// The <see cref="INowable{T}"/> fast path: the buffer can answer, so the
    /// read commits without publishing anything at all.
    ///
    /// Answering true PERFORMS the rendezvous, so this takes outright and there
    /// is nothing to undo. It is one locked step, which is why it needs no
    /// tentative hold — and it refuses while someone else has one, because a
    /// take it cannot undo must not be interleaved with one that may be.
    /// </summary>
    internal bool TryReadNow(int count, out ByteRead value) {
        lock (_lock) {
            ThrowIfDisposed();

            if (_taking) {
                value = default;
                return false;
            }

            int n = TakeableLocked(count);
            if (n < 0) {
                value = default;
                return false;
            }

            value = TakeLocked(n);
            return true;
        }
    }

    internal bool TryEofNow(out Unit value) {
        value = default;

        lock (_lock) {
            ThrowIfDisposed();
            return !_taking && _pos >= _len && Finished;
        }
    }

    /// <summary>
    /// The published form. Parks unconditionally and lets <see cref="Deliver"/>
    /// decide, so there is one path through the buffer rather than two — and a
    /// read that could be answered at once still is, synchronously, before this
    /// returns.
    /// </summary>
    internal void PublishRead(int count, SyncState state, int eventId, Action<ByteRead> onSync) {
        ThrowIfDisposed();
        Interlocked.Increment(ref _published);

        var w = new ReadWaiter { State = state, EventId = eventId, OnSync = onSync, Count = count };
        lock (_lock) { AppendLocked(w); }
        Deliver();
    }

    internal void PublishEof(SyncState state, int eventId, Action<Unit> onSync) {
        ThrowIfDisposed();
        Interlocked.Increment(ref _published);

        var w = new EofWaiter { State = state, EventId = eventId, OnSync = onSync };
        lock (_lock) { AppendLocked(w); }
        Deliver();
    }

    private int _published;

    /// <summary>
    /// How many reads have gone the published way rather than taking the
    /// <see cref="INowable{T}"/> fast path.
    ///
    /// For the tests, and only for them: "a buffered read commits without
    /// publishing" is a claim about which path was taken, and nothing else
    /// about the port can be looked at to see which one it was.
    /// </summary>
    internal int PublishCount => Volatile.Read(ref _published);

    // --- The plain reads ----------------------------------------------------
    //
    // What `AsStream`, `peek` and the eof question are built on. These are
    // ordinary waits and not events: nothing about them can lose a `choose`, so
    // they take outright, and a failure reaches the caller as a throw on the
    // stack the caller is already standing on.
    //
    // They still go through the buffer, and they still respect a tentative take
    // — `_taking` — because the cursor has one owner at a time whoever is
    // moving it.

    /// <summary>
    /// Up to <c>buffer.Length</c> bytes, 0 at end of input. Parks the calling
    /// thread while a refill is in flight.
    /// </summary>
    public int ReadInto(Span<byte> buffer) {
        if (buffer.IsEmpty) return 0;

        while (true) {
            lock (_lock) {
                ThrowIfDisposed();

                if (_taking) {
                    Monitor.Wait(_lock);
                    continue;
                }

                int have = _len - _pos;
                if (have > 0) {
                    int n = Math.Min(buffer.Length, have);
                    _buf.AsSpan(_pos, n).CopyTo(buffer);
                    _pos += n;
                    return n;
                }

                if (_error is not null) throw Rethrow(_error);
                if (_ended) return 0;
            }

            EnsureRefill(1).GetAwaiter().GetResult();
        }
    }

    /// <summary>The suspending twin of <see cref="ReadInto"/>.</summary>
    public async ValueTask<int> ReadIntoAsync(Memory<byte> buffer, CancellationToken cancel = default) {
        if (buffer.IsEmpty) return 0;

        while (true) {
            Task? quiet = null;

            lock (_lock) {
                ThrowIfDisposed();

                if (_taking) {
                    quiet = QuietLocked();
                } else {
                    int have = _len - _pos;
                    if (have > 0) {
                        int n = Math.Min(buffer.Length, have);
                        _buf.AsSpan(_pos, n).CopyTo(buffer.Span);
                        _pos += n;
                        return n;
                    }

                    if (_error is not null) throw Rethrow(_error);
                    if (_ended) return 0;
                }
            }

            // `WaitAsync` rather than a cancellable read: the token abandons the
            // WAIT, not the refill. Whatever the refill brings still lands in
            // the buffer, for this reader or the next one.
            if (quiet is not null) await quiet.WaitAsync(cancel).ConfigureAwait(false);
            else await EnsureRefill(1).WaitAsync(cancel).ConfigureAwait(false);
        }
    }

    // --- The eof question ---------------------------------------------------

    /// <summary>
    /// Whether the port is at end of input. No syscall and no suspension
    /// whenever the buffer holds anything.
    ///
    /// A port that FAILED is not at end of input, and this raises rather than
    /// answering true — or `(loop (:break (byte-port-eof? p)) ...)` would end
    /// normally on a broken socket and return a truncated result as though it
    /// were the whole thing.
    /// </summary>
    public bool Eof() {
        while (true) {
            lock (_lock) {
                ThrowIfDisposed();

                if (_taking) {
                    Monitor.Wait(_lock);
                    continue;
                }

                if (_pos < _len) return false;
                if (_error is not null) throw Rethrow(_error);
                if (_ended) return true;
            }

            EnsureRefill(1).GetAwaiter().GetResult();
        }
    }

    public async ValueTask<bool> EofAsync(CancellationToken cancel = default) {
        while (true) {
            Task? quiet = null;

            lock (_lock) {
                ThrowIfDisposed();

                if (_taking) {
                    quiet = QuietLocked();
                } else {
                    if (_pos < _len) return false;
                    if (_error is not null) throw Rethrow(_error);
                    if (_ended) return true;
                }
            }

            if (quiet is not null) await quiet.WaitAsync(cancel).ConfigureAwait(false);
            else await EnsureRefill(1).WaitAsync(cancel).ConfigureAwait(false);
        }
    }

    // --- Peeking ------------------------------------------------------------
    //
    // The same mechanism as a read with the last step — the cursor move — left
    // out. That is the whole of it, and it is why `peek, decide, convert` works:
    // the sniffed bytes are still in the buffer, and `AsStream` serves the
    // buffer first.

    private BjolangRuntime.Option<byte[]> PeekLocked(int skip, int count) {
        int have = _len - _pos - skip;
        if (have <= 0) return BjolangRuntime.None<byte[]>();

        int n = Math.Min(count, have);
        var bytes = new byte[n];
        Buffer.BlockCopy(_buf, _pos + skip, bytes, 0, n);
        return BjolangRuntime.Some(bytes);
    }

    private static void CheckPeek(int skip, int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
    }

    /// <summary>
    /// <paramref name="skip"/> bytes past the cursor, then <paramref name="count"/>
    /// of them, without moving it. Short at end of input; `None` when there is
    /// nothing at all beyond the skip.
    /// </summary>
    public BjolangRuntime.Option<byte[]> Peek(int skip, int count) {
        CheckPeek(skip, count);
        if (count == 0) return BjolangRuntime.Some(Array.Empty<byte>());

        while (true) {
            lock (_lock) {
                ThrowIfDisposed();

                if (_taking) {
                    Monitor.Wait(_lock);
                    continue;
                }

                if (_len - _pos >= skip + count) return PeekLocked(skip, count);
                if (_error is not null && _len - _pos == 0) throw Rethrow(_error);
                if (Finished) return PeekLocked(skip, count);
            }

            EnsureRefill(skip + count).GetAwaiter().GetResult();
        }
    }

    public async ValueTask<BjolangRuntime.Option<byte[]>> PeekAsync(
        int skip, int count, CancellationToken cancel = default) {

        CheckPeek(skip, count);
        if (count == 0) return BjolangRuntime.Some(Array.Empty<byte>());

        while (true) {
            Task? quiet = null;

            lock (_lock) {
                ThrowIfDisposed();

                if (_taking) {
                    quiet = QuietLocked();
                } else {
                    if (_len - _pos >= skip + count) return PeekLocked(skip, count);
                    if (_error is not null && _len - _pos == 0) throw Rethrow(_error);
                    if (Finished) return PeekLocked(skip, count);
                }
            }

            if (quiet is not null) await quiet.WaitAsync(cancel).ConfigureAwait(false);
            else await EnsureRefill(skip + count).WaitAsync(cancel).ConfigureAwait(false);
        }
    }

    // --- The stream view ----------------------------------------------------

    /// <summary>
    /// The port as a <see cref="Stream"/>: the buffer first, then refills
    /// through the port.
    ///
    /// **This, and never `_inner`, is what a text reader goes over.** A
    /// `StreamReader` built over the port's own stream would skip whatever the
    /// buffer held from a peek or from a refill that overshot — silently, as
    /// wrong output. Over this, nothing is skipped, which is exactly what makes
    /// "peek, decide, then convert" work.
    ///
    /// Reading BYTES again after building a text reader over this does not work
    /// and is not meant to: a `StreamReader` reads ahead, so by the time it has
    /// handed out its first line the port is somewhere past it. The conversion
    /// is one-way.
    ///
    /// Disposing the view does not dispose the port. The port is owned by the
    /// scope that opened it.
    /// </summary>
    public Stream AsStream() => new PortStream(this);

    private sealed class PortStream : Stream {
        private readonly BjoByteInputPort _port;

        public PortStream(BjoByteInputPort port) => _port = port;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            ArgumentNullException.ThrowIfNull(buffer);
            return _port.ReadInto(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer) => _port.ReadInto(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) {
            ArgumentNullException.ThrowIfNull(buffer);
            return _port.ReadIntoAsync(buffer.AsMemory(offset, count), cancel).AsTask();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) =>
            _port.ReadIntoAsync(buffer, cancel);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("this is the read half of a byte port.");

        /// Deliberately empty. A `StreamReader` disposes the stream it was given,
        /// and the port is not the text reader's to close.
        protected override void Dispose(bool disposing) { }
    }

    // --- Closing ------------------------------------------------------------

    /// <summary>
    /// Releasing the handle, and nothing else.
    ///
    /// A refill in flight is NOT the wakeup: disposing a stream under a read is
    /// racy across stream types, so a reader parked here is woken by the
    /// ambient cancellation token, exactly as `BjoPort` arranges. What a dispose
    /// does to a pending refill is turn it into a failure, which is sticky and
    /// which the next reader sees.
    /// </summary>
    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            Monitor.PulseAll(_lock);
        }

        if (_ownsInner) _inner.Dispose();
    }
}

/// <summary>
/// A buffered byte sink.
///
/// Most writes do no I/O and copy into the buffer; a write that fills it drains
/// immediately, which is a syscall — so writes have suspending twins as well as
/// flushes, for the reason <see cref="BjoWriter"/>'s do.
///
/// Not thread-safe, and neither is <see cref="BjoWriter"/>. A port two fibers
/// write to concurrently is a program that has not said what it means; the
/// reading side is where the design is, because that is where a `choose` can
/// take a read away.
/// </summary>
public sealed class BjoByteOutputPort : IDisposable {
    private const int DefaultBufferSize = 4096;

    private readonly Stream _inner;
    private readonly bool _ownsInner;
    private readonly byte[] _buf;
    private int _len;
    private bool _disposed;
    private bool _writeClosed;

    /// <summary>See <see cref="BjoPort.Owner"/>.</summary>
    public BjolangRuntime.Owned? Owner;

    public BjoByteOutputPort(Stream inner) : this(inner, DefaultBufferSize, true) { }

    public BjoByteOutputPort(Stream inner, bool ownsInner) : this(inner, DefaultBufferSize, ownsInner) { }

    public BjoByteOutputPort(Stream inner, int bufferSize, bool ownsInner = true) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        if (!inner.CanWrite)
            throw new ArgumentException("a byte output port needs a writable stream.", nameof(inner));

        _inner = inner;
        _ownsInner = ownsInner;
        _buf = new byte[bufferSize];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfShutDown() {
        if (_writeClosed)
            throw new InvalidOperationException(
                "write to a byte output port whose write half was ended by shutdown!.");
    }

    // --- Writing ------------------------------------------------------------

    public void Write(ReadOnlySpan<byte> bytes) {
        ThrowIfDisposed();
        ThrowIfShutDown();

        while (!bytes.IsEmpty) {
            if (_len == _buf.Length) DrainSync();

            int n = Math.Min(bytes.Length, _buf.Length - _len);
            bytes[..n].CopyTo(_buf.AsSpan(_len));
            _len += n;
            bytes = bytes[n..];
        }
    }

    /// The suspending twin. Avoids the `async` keyword in the common case, the
    /// way <see cref="BjoWriter.WriteValueAsync"/> does: text that fits in the
    /// buffer must not cost a state machine.
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancel = default) {
        ThrowIfDisposed();
        ThrowIfShutDown();

        if (bytes.Length <= _buf.Length - _len) {
            bytes.Span.CopyTo(_buf.AsSpan(_len));
            _len += bytes.Length;
            return default;
        }

        return SpillAsync(bytes, cancel);
    }

    private async ValueTask SpillAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancel) {
        while (!bytes.IsEmpty) {
            if (_len == _buf.Length) await DrainAsync(cancel).ConfigureAwait(false);

            int n = Math.Min(bytes.Length, _buf.Length - _len);
            bytes.Span[..n].CopyTo(_buf.AsSpan(_len));
            _len += n;
            bytes = bytes[n..];
        }
    }

    private void DrainSync() {
        if (_len == 0) return;
        int n = _len;
        _len = 0;
        _inner.Write(_buf, 0, n);
    }

    private async ValueTask DrainAsync(CancellationToken cancel) {
        if (_len == 0) return;
        int n = _len;
        _len = 0;
        await _inner.WriteAsync(_buf.AsMemory(0, n), cancel).ConfigureAwait(false);
    }

    // --- Flushing -----------------------------------------------------------

    public void Flush() {
        ThrowIfDisposed();
        DrainSync();
        _inner.Flush();
    }

    public async ValueTask FlushValueAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        await DrainAsync(cancel).ConfigureAwait(false);
        await _inner.FlushAsync(cancel).ConfigureAwait(false);
    }

    // --- The half-close -----------------------------------------------------

    /// <summary>
    /// End the write half and leave the read half alone.
    ///
    /// A protocol needs this and `close` cannot say it: "I have finished
    /// speaking, now answer me" is a TCP FIN in one direction, not a closed
    /// connection. Everything buffered goes out first, and a write after it is
    /// refused rather than silently dropped.
    ///
    /// A stream with no half-close gets the flush and nothing else, which is the
    /// most that can honestly be done for a file.
    /// </summary>
    public void Shutdown() {
        ThrowIfDisposed();
        if (_writeClosed) return;

        DrainSync();
        _inner.Flush();
        _writeClosed = true;

        switch (_inner) {
            case IHalfClosable h:
                h.CloseWrite();
                break;
            case System.Net.Sockets.NetworkStream ns:
                // Here rather than in `(std net)` so that the module above can be
                // written without reopening this one. `Socket` has been public
                // on `NetworkStream` since .NET Core 3.0.
                ns.Socket.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                break;
        }
    }

    public async ValueTask ShutdownAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        if (_writeClosed) return;

        await DrainAsync(cancel).ConfigureAwait(false);
        await _inner.FlushAsync(cancel).ConfigureAwait(false);
        _writeClosed = true;

        switch (_inner) {
            case IHalfClosable h:
                h.CloseWrite();
                break;
            case System.Net.Sockets.NetworkStream ns:
                ns.Socket.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                break;
        }
    }

    // --- Closing ------------------------------------------------------------

    public void Dispose() {
        if (_disposed) return;

        try {
            // Held bytes go out before the handle does, and `_disposed` is set
            // only afterwards so that the drain is not refused by its own guard.
            if (!_writeClosed) {
                DrainSync();
                _inner.Flush();
            }
        } finally {
            _disposed = true;
            if (_ownsInner) _inner.Dispose();
        }
    }
}

// ---------------------------------------------------------------------------
// The pipe
// ---------------------------------------------------------------------------

/// <summary>
/// A byte port pair with nothing underneath: what is written to the output can
/// be read from the input.
///
/// Unbounded, and in memory. A pipe is what a string port is one layer up —
/// the GC prices it, nothing registers it on a scope, and a write never waits.
/// A bounded pipe would be a different thing with a different name; this one
/// exists so that everything above can be tested with no file and no network.
/// </summary>
public sealed class BjoBytePipe {
    private readonly BytePipeStream _stream = new();

    public BjoBytePipe() {
        // The asymmetry is the pipe: closing the READER must not end the
        // writer, so the input half does not own the stream — but closing the
        // WRITER is exactly what the reader sees as end of input, so the output
        // half does. Disposing this stream completes it rather than discarding
        // it, so whatever is queued is still the reader's.
        Input = new BjoByteInputPort(_stream, ownsInner: false);
        Output = new BjoByteOutputPort(_stream, ownsInner: true);
    }

    public BjoByteInputPort Input { get; }

    public BjoByteOutputPort Output { get; }

    /// <summary>
    /// A byte queue with two faces. Writes append and never wait; reads take
    /// from the front and wait for the writer when there is nothing.
    /// </summary>
    private sealed class BytePipeStream : Stream, IHalfClosable {
        private readonly object _lock = new();
        private readonly Queue<byte[]> _chunks = new();
        private int _offset;
        private bool _completed;
        private TaskCompletionSource? _arrival;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void CloseWrite() {
            TaskCompletionSource? waiting;

            lock (_lock) {
                if (_completed) return;
                _completed = true;
                waiting = _arrival;
                _arrival = null;
                Monitor.PulseAll(_lock);
            }

            waiting?.TrySetResult();
        }

        public override void Write(byte[] buffer, int offset, int count) {
            ArgumentNullException.ThrowIfNull(buffer);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer) {
            if (buffer.IsEmpty) return;

            TaskCompletionSource? waiting;

            lock (_lock) {
                if (_completed)
                    throw new InvalidOperationException("write to a pipe whose write half has been closed.");

                _chunks.Enqueue(buffer.ToArray());
                waiting = _arrival;
                _arrival = null;
                Monitor.PulseAll(_lock);
            }

            waiting?.TrySetResult();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default) {
            Write(buffer.Span);
            return default;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancel) {
            ArgumentNullException.ThrowIfNull(buffer);
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        /// Under the lock: copy from the head chunk, or -1 for "nothing yet".
        private int TakeLocked(Span<byte> buffer) {
            while (_chunks.Count > 0) {
                var head = _chunks.Peek();
                int left = head.Length - _offset;

                if (left <= 0) {
                    _chunks.Dequeue();
                    _offset = 0;
                    continue;
                }

                int n = Math.Min(buffer.Length, left);
                head.AsSpan(_offset, n).CopyTo(buffer);
                _offset += n;
                if (_offset == head.Length) {
                    _chunks.Dequeue();
                    _offset = 0;
                }

                return n;
            }

            return _completed ? 0 : -1;
        }

        public override int Read(byte[] buffer, int offset, int count) {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer) {
            if (buffer.IsEmpty) return 0;

            lock (_lock) {
                while (true) {
                    int n = TakeLocked(buffer);
                    if (n >= 0) return n;
                    Monitor.Wait(_lock);
                }
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) {
            if (buffer.IsEmpty) return 0;

            while (true) {
                Task arrival;

                lock (_lock) {
                    int n = TakeLocked(buffer.Span);
                    if (n >= 0) return n;

                    _arrival ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    arrival = _arrival.Task;
                }

                await arrival.WaitAsync(cancel).ConfigureAwait(false);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancel) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        /// Completing, not discarding. Whatever is queued is still the reader's.
        protected override void Dispose(bool disposing) {
            if (disposing) CloseWrite();
        }
    }
}

// ---------------------------------------------------------------------------
// The limit
// ---------------------------------------------------------------------------

/// <summary>
/// At most <c>n</c> bytes of what is under it, and then end of input.
///
/// Over the port's <see cref="BjoByteInputPort.AsStream"/> rather than over its
/// stream, which is the whole reason the limit is exact: everything the
/// underlying port had buffered counts towards the bound, and once the bound is
/// reached the underlying port is positioned exactly where the limit ended.
/// </summary>
internal sealed class LimitedStream : Stream {
    private readonly Stream _inner;
    private long _left;

    public LimitedStream(Stream inner, long limit) {
        _inner = inner;
        _left = limit;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer) {
        if (_left <= 0) return 0;

        int n = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
        _left -= n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) {
        if (_left <= 0) return 0;

        int n = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], cancel).ConfigureAwait(false);
        _left -= n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// The bound is a view of something the caller still owns.
    protected override void Dispose(bool disposing) { }
}

// ---------------------------------------------------------------------------
// The events
// ---------------------------------------------------------------------------

/// <summary>
/// A read, as a CML event. The same shape as <c>InboxRecvEvent</c>: an event
/// over a buffered source, <see cref="INowable{T}"/> when the buffer can
/// satisfy it and published when it cannot.
///
/// EPHEMERAL, and that is the point. Every <c>sync</c> asks the port a new
/// question and there is no memoised result, so a loop over one of these reads
/// successive bytes rather than winning with the same answer forever — which is
/// what <c>(task-&gt;event ...)</c> does, and why it is not this.
/// </summary>
internal sealed class ByteReadEvent : IEvent<ByteRead>, INowable<ByteRead> {
    private readonly BjoByteInputPort _port;
    private readonly int _count;

    internal ByteReadEvent(BjoByteInputPort port, int count) {
        _port = port;
        _count = count;
    }

    public void Publish(SyncState state, int eventId, Action<ByteRead> onSync) =>
        _port.PublishRead(_count, state, eventId, onSync);

    bool INowable<ByteRead>.TryNow(out ByteRead value) => _port.TryReadNow(_count, out value);
}

/// <summary>The port is finished. See <c>EofWaiter</c>.</summary>
internal sealed class ByteEofEvent : IEvent<Unit>, INowable<Unit> {
    private readonly BjoByteInputPort _port;

    internal ByteEofEvent(BjoByteInputPort port) => _port = port;

    public void Publish(SyncState state, int eventId, Action<Unit> onSync) =>
        _port.PublishEof(state, eventId, onSync);

    bool INowable<Unit>.TryNow(out Unit value) => _port.TryEofNow(out value);
}

// ---------------------------------------------------------------------------
// The dispatchers
// ---------------------------------------------------------------------------

/// <summary>
/// What `(std ports)` imports, one entry per operation.
///
/// A class of its own rather than static members on the two ports the way
/// `BjoPort` has them, because there are two port types here and one module
/// above: a single prefix is what keeps the `import/extern` block in
/// `lib/std/ports.bjo` readable. The shape is `Bjoml.InboxModule`'s.
///
/// The suspending halves take a trailing <see cref="CancellationToken"/> and do
/// not name it in the Bjolang signature: an `#:async` import fills in the
/// ambient token at the call site.
/// </summary>
public static class BytePorts {
    // --- Sources ------------------------------------------------------------

    /// The `#:exceptions` on the Bjolang side turns whatever this throws into a
    /// `Result`, which is why nothing is caught here.
    public static BjoByteInputPort OpenInput(string path) =>
        BjolangRuntime.OwnByteReader(new BjoByteInputPort(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)));

    public static BjoByteOutputPort OpenOutput(string path) =>
        BjolangRuntime.OwnByteWriter(new BjoByteOutputPort(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)));

    public static BjoByteInputPort FromStream(Stream stream) =>
        BjolangRuntime.OwnByteReader(new BjoByteInputPort(stream));

    public static BjoByteOutputPort ToStream(Stream stream) =>
        BjolangRuntime.OwnByteWriter(new BjoByteOutputPort(stream));

    public static BjoBytePipe MakePipe() => new();

    public static BjoByteInputPort PipeInput(BjoBytePipe pipe) => pipe.Input;

    public static BjoByteOutputPort PipeOutput(BjoBytePipe pipe) => pipe.Output;

    public static BjoByteInputPort Limited(BjoByteInputPort port, int limit) {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return new BjoByteInputPort(new LimitedStream(port.AsStream(), limit), ownsInner: false);
    }

    // --- Reading, as events -------------------------------------------------

    public static IEvent<ByteRead> ReadSomeEvent(BjoByteInputPort port) => new ByteReadEvent(port, 0);

    public static IEvent<ByteRead> ReadBytesEvent(BjoByteInputPort port, int count) {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return new ByteReadEvent(port, count);
    }

    public static IEvent<Unit> EofEvent(BjoByteInputPort port) => new ByteEofEvent(port);

    // --- Reading, without a choice ------------------------------------------

    public static bool Eof(BjoByteInputPort port) => port.Eof();

    public static ValueTask<bool> EofAsync(BjoByteInputPort port, CancellationToken cancel = default) =>
        port.EofAsync(cancel);

    public static BjolangRuntime.Option<byte[]> PeekBytes(BjoByteInputPort port, int skip, int count) =>
        port.Peek(skip, count);

    public static ValueTask<BjolangRuntime.Option<byte[]>> PeekBytesAsync(
        BjoByteInputPort port, int skip, int count, CancellationToken cancel = default) =>
        port.PeekAsync(skip, count, cancel);

    // --- Writing ------------------------------------------------------------

    // `Bjoml.Unit` and not C# `void`, for the reason `BjolangRuntime.unit`
    // gives: no `void` can stand for a type argument.

    public static Unit WriteBytes(BjoByteOutputPort port, byte[] bytes) {
        ArgumentNullException.ThrowIfNull(bytes);
        port.Write(bytes);
        return default;
    }

    public static async ValueTask<Unit> WriteBytesAsync(
        BjoByteOutputPort port, byte[] bytes, CancellationToken cancel = default) {
        ArgumentNullException.ThrowIfNull(bytes);
        await port.WriteAsync(bytes, cancel).ConfigureAwait(false);
        return default;
    }

    public static Unit Flush(BjoByteOutputPort port) {
        port.Flush();
        return default;
    }

    public static async ValueTask<Unit> FlushAsync(BjoByteOutputPort port, CancellationToken cancel = default) {
        await port.FlushValueAsync(cancel).ConfigureAwait(false);
        return default;
    }

    public static Unit Shutdown(BjoByteOutputPort port) {
        port.Shutdown();
        return default;
    }

    public static async ValueTask<Unit> ShutdownAsync(BjoByteOutputPort port, CancellationToken cancel = default) {
        await port.ShutdownAsync(cancel).ConfigureAwait(false);
        return default;
    }

    // --- Closing ------------------------------------------------------------

    public static Unit CloseInput(BjoByteInputPort port) => BjolangRuntime.CloseByteInput(port);

    public static Unit CloseOutput(BjoByteOutputPort port) => BjolangRuntime.CloseByteOutput(port);

    // --- The text layer -----------------------------------------------------

    /// <summary>
    /// A text reader over the byte port, reading THROUGH it rather than past it.
    /// See <see cref="BjoByteInputPort.AsStream"/> for why that distinction is
    /// the whole of this function.
    /// </summary>
    public static TextReader ToTextReader(BjoByteInputPort port, Encoding encoding) {
        ArgumentNullException.ThrowIfNull(encoding);
        return new BjoPort(new StreamReader(port.AsStream(), encoding, detectEncodingFromByteOrderMarks: false));
    }

    /// <summary>
    /// A text writer over the byte port. Flushing or closing it pushes its
    /// characters into the byte port AND drains the byte port, because the
    /// view's `Flush` is the port's.
    /// </summary>
    public static TextWriter ToTextWriter(BjoByteOutputPort port, Encoding encoding) {
        ArgumentNullException.ThrowIfNull(encoding);
        return new BjoWriter(new StreamWriter(new OutPortStream(port), encoding));
    }

    private sealed class OutPortStream : Stream {
        private readonly BjoByteOutputPort _port;

        public OutPortStream(BjoByteOutputPort port) => _port = port;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) {
            ArgumentNullException.ThrowIfNull(buffer);
            _port.Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer) => _port.Write(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default) =>
            _port.WriteAsync(buffer, cancel);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancel) {
            ArgumentNullException.ThrowIfNull(buffer);
            return _port.WriteAsync(buffer.AsMemory(offset, count), cancel).AsTask();
        }

        /// The port's, so that closing the text writer reaches the stream under
        /// the byte port rather than stopping in its buffer.
        public override void Flush() => _port.Flush();

        public override Task FlushAsync(CancellationToken cancel) => _port.FlushValueAsync(cancel).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("this is the write half of a byte port.");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        /// The byte port is not the text writer's to close; it is owned by the
        /// scope that opened it. Its held bytes still go out, because
        /// `StreamWriter.Dispose` flushes before it disposes.
        protected override void Dispose(bool disposing) { }
    }
}
