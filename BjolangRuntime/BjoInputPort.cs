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

// The input port: a source of bytes, and text read from those bytes.
//
// One port type, as in Racket. A port is bytes; `read-char` and `read-line`
// decode UTF-8 from it as they go, so byte reads and text reads mix freely on
// one port, and a string port is a port over the string's own UTF-8. Text in
// another encoding is a port over a stream that transcodes to UTF-8.
//
// THE ONE INVARIANT, which everything here follows from:
//
//   Every byte that leaves the operating system lands in the port's buffer. A
//   read never takes bytes from the underlying stream — it takes them from the
//   buffer, and only when it wins.
//
// Commitment is therefore a cursor move inside the port and nothing else. That
// is what makes a read safe inside a `choose`; it is why a peek is the same
// mechanism with the last step removed rather than a second one; it is why a
// refill that lands after its branch has already lost is harmless rather than
// data loss; and it is why a cancelled `read-line` consumes nothing: the part
// of the line that had arrived is still in the buffer for the next read.
//
// DECODING is lenient, as Racket's ports are: an invalid sequence reads as
// U+FFFD, one per maximal invalid subsequence, which is what .NET's decoder
// and OCaml's `String.get_utf_8_uchar` do. A strict reading is a conversion,
// not a port. A byte order mark is not special: U+FEFF is a character like
// any other, as it is in Racket and OCaml.
//
// WHAT IS DELIBERATELY NOT HERE
//
//   * A cancellable refill. `Stream.ReadAsync` that has already taken bytes
//     from the kernel cannot put them back, so a cancelled refill is lost data.
//     A refill belongs to the PORT and runs to completion; the thing that gets
//     cancelled is the fiber's WAIT for it, which loses nothing because the
//     bytes land in the buffer either way.
//   * Two refills at once. Two concurrent reads on one `Stream` is undefined
//     behaviour in .NET, so a second reader joins the one in flight rather than
//     starting another.

using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Text;
using Bjoml;
using BjoString;
using Unit = Bjoml.Unit;
using ByteRead = BjolangRuntime.Result<System.Exception, BjolangRuntime.Option<byte[]>>;

namespace Bjolang.Runtime;

/// <summary>
/// A stream whose synchronous <c>Read</c> is as good as its asynchronous one.
/// A port over such a stream fills a blocking caller with a plain read, rather
/// than starting an asynchronous one and blocking on it.
///
/// Opt-in, because not every stream allows it: Kestrel's request bodies refuse
/// synchronous I/O outright.
/// </summary>
internal interface ISyncReadable { }

/// <summary>
/// What ends a line: Racket's `read-line` modes, and the prelude's
/// `LineMode`, whose cases `line-mode-code` numbers as these are numbered.
/// </summary>
public enum LineMode {
    /// `\n`, `\r` or `\r\n`, whichever is longest. The default.
    Any = 0,
    /// `\n` or `\r`, each on its own: `\r\n` ends a line and then an empty one.
    AnyOne = 1,
    /// `\n` alone; a `\r` is part of the line.
    Linefeed = 2,
    /// `\r` alone; a `\n` is part of the line.
    Return = 3,
    /// `\r\n` alone; a `\r` or `\n` on its own is part of the line.
    ReturnLinefeed = 4,
}

/// <summary>
/// A buffered input port: bytes, read directly or as CML events, and text,
/// decoded from them as UTF-8.
///
/// **The port owns the buffer, and the buffer is the only place bytes live.**
/// A read is a cursor move; a peek is the same look with the cursor left alone;
/// a refill is the port's own business and is never cancelled. See the file
/// header.
///
/// # Every virtual read is overridden
///
/// It is a <see cref="TextReader"/>, so that a .NET API taking one takes a
/// port. If any inherited read reached the stream while the buffer still held
/// bytes, those bytes would be skipped — silently, as wrong output. The rule
/// for anything added here: read through the buffer.
///
/// # Two fibers, one port
///
/// Allowed: two fibers reading one port serialize on the single in-flight
/// refill and on the lock, and every byte, character or line is handed out
/// exactly once. Which of them gets which is theirs to arrange.
///
/// # Errors
///
/// A failure from the underlying stream is sticky, exactly as end of input is,
/// and is reported only once whatever arrived before it has been handed out,
/// because those bytes are data. It reaches a read in one of two shapes:
///
///   * As a value, <c>Err e</c>, out of the read events. The CML resume path
///     carries a value and cannot carry a raise. The language's
///     `read-some`/`read-bytes` unwrap it and raise on the fiber's own stack.
///   * As a throw, out of every other read, all of which are ordinary waits
///     and so already stand on the caller's stack.
///
/// A line or character cut short by a failure reports the failure, not the
/// fragment: the rest of it was lost.
/// </summary>
public sealed class BjoInputPort : TextReader {
    private const int DefaultBufferSize = 16384;

    /// For a connection's read half and a pipe: many of them may be open at
    /// once, and each is read a message at a time rather than streamed.
    internal const int SmallBufferSize = 4096;

    private readonly Stream _inner;

    /// Whether disposing this port disposes the stream under it. False for the
    /// read half of a pipe and for `limited`, where the stream is a view of
    /// something the caller still owns.
    private readonly bool _ownsInner;

    /// Whether a blocking caller may fill with a plain synchronous read. See
    /// <see cref="ISyncReadable"/>.
    private readonly bool _syncReads;

    /// The connection this is one half of, or null for a port that stands
    /// alone. When it is set, closing this port closes a half rather than the
    /// stream — see <see cref="BjoConnection"/>.
    private readonly BjoConnection? _connection;

    // --- The lock -----------------------------------------------------------
    //
    // A spin lock, not a monitor. Every section under it moves a cursor, copies
    // bytes out or allocates the string handed back, and none waits on
    // anything, so it is never held long and almost never contended. Taking it
    // is one interlocked instruction, where a monitor's enter and exit cost a
    // `read-char` about a third of its time.
    //
    // Nothing waits while holding it. A reader that has to wait for a
    // tentative take (`_taking`) to finish lets go and spins, or awaits
    // `_quiet`, and looks again.

    /// 1 while held.
    private int _held;

    /// Holding <see cref="_held"/>, released by <c>Dispose</c> at the end of a
    /// <c>using</c>, which releases it however the section is left.
    private readonly ref struct Held {
        private readonly BjoInputPort _port;
        public Held(BjoInputPort port) => _port = port;
        public void Dispose() => _port.Release();
    }

    private void Release() => Volatile.Write(ref _held, 0);

    private Held Hold() {
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0) HoldSlow();
        return new Held(this);
    }

    private void HoldSlow() {
        var spin = new SpinWait();
        while (Interlocked.CompareExchange(ref _held, 1, 0) != 0) spin.SpinOnce();
    }

    // --- State --------------------------------------------------------------

    // The buffer, and the window of it that holds unread bytes.
    //
    // `_buf` and `_len` are moved ONLY while preparing or committing a refill,
    // and there is at most one refill; a taker only ever moves `_pos`,
    // forwards, and never past `_len`. That pair of facts is what lets a refill
    // write into `_buf[_len..]` outside the lock without a copy.
    private byte[] _buf;
    private int _pos;
    private int _len;

    /// The stream answered zero once. Sticky: a stream does not un-end.
    private bool _ended;

    /// The stream failed once. Sticky, for the same reason.
    private Exception? _error;

    private bool _disposed;

    /// The refill in flight, or null. Holding it in a field is the whole of
    /// "at most one": a second reader waits for this rather than starting
    /// another.
    private Task? _refill;

    /// A take is tentatively holding the cursor: it has moved <c>_pos</c> and is
    /// out at <c>TryCommit</c>, which may yet fail and put it back. Nothing else
    /// may take while this is set.
    private bool _taking;

    /// Completed and replaced whenever <c>_taking</c> clears, for the readers
    /// that suspend rather than spin.
    private TaskCompletionSource? _quiet;

    /// Whoever is waiting for bytes, oldest first. Singly linked and appended by
    /// walking: a port has one or two waiters, not a queue.
    private Waiter? _waiters;

    private const int Idle = 0;
    private const int Running = 1;
    private const int RunningAgain = 2;
    private int _deliverState;

    /// The low surrogate of a character above the BMP whose high surrogate a
    /// UTF-16 read handed out, or -1. Only the <see cref="TextReader"/> reads
    /// set it; every text read serves it first, and a byte read, which starts
    /// after that character's bytes, drops it.
    private int _pendingLow = -1;

    /// <summary>
    /// The scope registration that will dispose this port, or null for a port
    /// nothing owns — the standard input, a string port, a pipe, a `limited`
    /// view. Set by whatever opened a real handle, before the caller could
    /// lose it. Read only by the closers in `Scope.cs`.
    /// </summary>
    public BjolangRuntime.Owned? Owner;

    public BjoInputPort(Stream inner) : this(inner, DefaultBufferSize, true, null) { }

    public BjoInputPort(Stream inner, bool ownsInner) : this(inner, DefaultBufferSize, ownsInner, null) { }

    /// The buffer size is settable because every interesting bug in a buffered
    /// reader lives at a buffer boundary — a `\r\n` split across two refills,
    /// a character whose bytes straddle one, a line longer than the buffer —
    /// and a test that cannot put the boundary where it wants cannot reach them.
    public BjoInputPort(Stream inner, int bufferSize, bool ownsInner = true)
        : this(inner, bufferSize, ownsInner, null) { }

    /// The reading half of a connection. Owns neither the stream nor a place on
    /// a scope: the connection owns both, and this tells it when it is closed.
    internal BjoInputPort(BjoConnection connection)
        : this(connection.Stream, SmallBufferSize, false, connection) { }

    private BjoInputPort(Stream inner, int bufferSize, bool ownsInner, BjoConnection? connection) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        if (!inner.CanRead)
            throw new ArgumentException("an input port needs a readable stream.", nameof(inner));

        _inner = inner;
        _ownsInner = ownsInner;
        _connection = connection;
        _syncReads = inner is FileStream or MemoryStream or UnmanagedMemoryStream or ISyncReadable;
        _buf = new byte[bufferSize];
    }

    /// <summary>
    /// A port over bytes already in memory that nothing may write: a string
    /// port, over the string's own UTF-8. The buffer IS those bytes, so nothing
    /// is copied until a read hands some out. The port is at end of input from
    /// the start, which is what keeps a refill from ever writing into them.
    /// </summary>
    internal BjoInputPort(ReadOnlyMemory<byte> bytes) {
        _inner = Stream.Null;
        _ownsInner = false;
        _syncReads = true;

        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out var segment)
            && segment.Array is { } array) {
            _buf = array;
            _pos = segment.Offset;
            _len = segment.Offset + segment.Count;
        } else {
            _buf = bytes.ToArray();
            _len = _buf.Length;
        }

        _ended = true;
    }

    /// <summary>
    /// Who is at the other end, when this port is one half of a connection.
    /// Null for a file, a pipe or a `limited` view, which have no peer.
    /// </summary>
    public string? Peer => _connection?.Peer;

    /// The stream this port reads from, for a caller that has to hand it to a
    /// .NET API. **Not** the thing to read through — see <see cref="AsStream"/>.
    internal Stream Inner => _inner;

    /// No more bytes will arrive, because the input ended or failed. Never
    /// throws, so a read part way through gathering can ask it.
    private bool Finished => _ended || _error is not null;

    /// Under the lock: nothing more is coming. Throws the sticky failure, and
    /// is asked only where nothing has been taken yet.
    private bool FinishedLocked() {
        if (_ended) return true;
        if (_error is not null) throw Rethrow(_error);
        return false;
    }

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
    /// Room after <c>_len</c> for the next refill: at least what
    /// <paramref name="want"/> asks for beyond what is held, and at least half
    /// the buffer, so that a line longer than the buffer is not gathered a few
    /// bytes at a time. Compacts, and grows when compacting is not enough.
    ///
    /// Under the lock, and only while no refill is in flight, which is what
    /// keeps the array still while one is writing into it. Growing is how a
    /// long line, or a `(read-bytes p 100000)`, stays a cursor move.
    /// </summary>
    private void MakeRoomLocked(int want) {
        int have = _len - _pos;
        if (have == 0) _pos = _len = 0;

        int room = Math.Max(Math.Max(want - have, _buf.Length / 2), 1);
        if (_buf.Length - _len >= room) return;

        if (_pos > 0) {
            if (have > 0) Buffer.BlockCopy(_buf, _pos, _buf, 0, have);
            _pos = 0;
            _len = have;
            if (_buf.Length - _len >= room) return;
        }

        var bigger = new byte[Math.Max(_buf.Length * 2, have + room)];
        if (have > 0) Buffer.BlockCopy(_buf, _pos, bigger, 0, have);
        _buf = bigger;
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
    /// </summary>
    private int TakeableLocked(int count) {
        int have = _len - _pos;

        if (count <= 0) return have > 0 ? have : (Finished ? 0 : -1);
        if (have >= count) return count;
        return Finished ? have : -1;
    }

    /// <summary>
    /// Move the cursor and hand the bytes over. Under the lock. A byte read
    /// starts after the character a UTF-16 read split, so it drops the half
    /// that read left pending.
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
        _pendingLow = -1;
        return ByteRead.Ok(BjolangRuntime.Some(bytes));
    }

    // --- Refilling ----------------------------------------------------------
    //
    // A refill is the port's, not a reader's. It is started with no
    // cancellation token at all, it completes whatever happens to whoever asked
    // for it, and it deposits into the buffer. A reader that has gone away —
    // lost its `choose`, hit its deadline — loses nothing by it.
    //
    // `_len` is assigned only after the read has returned, and a read that
    // throws records the failure instead, so a refill never leaves a stale
    // length behind that would re-serve bytes already handed out. The task is
    // completed rather than faulted even when the read threw: the failure is
    // the port's, sticky, and found by whoever reads next.

    private readonly record struct RefillStart(TaskCompletionSource Done, byte[] Into, int At, int Room);

    /// <summary>
    /// Under the lock: the refill to wait for, or a fresh one for this caller
    /// to perform, described by <paramref name="start"/>. Null and no start
    /// when no more input will ever come.
    /// </summary>
    private Task? RefillLocked(int want, out RefillStart? start) {
        start = null;
        if (_refill is not null) return _refill;
        if (_disposed || Finished) return null;

        MakeRoomLocked(want);

        // Published into the field BEFORE the read starts, so that a read
        // completing synchronously cannot find `_refill` still null and let a
        // second one through.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _refill = done.Task;
        start = new RefillStart(done, _buf, _len, _buf.Length - _len);
        return done.Task;
    }

    /// <summary>
    /// The refill in flight, starting an asynchronous one if there is none.
    /// Never faults, and never cancels.
    /// </summary>
    private Task EnsureRefill(int want) {
        Task? pending;
        RefillStart? start;

        using (Hold()) pending = RefillLocked(want, out start);

        // Started outside the lock. A synchronous completion runs `Pump` to its
        // end right here, and its end is `Deliver`, which commits waiters and
        // resumes fibers — none of which may happen with the lock held.
        if (start is { } s) _ = Pump(s);
        return pending ?? Task.CompletedTask;
    }

    /// <summary>
    /// One refill, for a caller that may block: a plain read on a stream that
    /// allows one, otherwise the asynchronous refill waited for. A refill
    /// already in flight is waited for either way.
    /// </summary>
    private void FillSync(int want) {
        if (!_syncReads) {
            EnsureRefill(want).GetAwaiter().GetResult();
            return;
        }

        Task? pending;
        RefillStart? start;

        using (Hold()) pending = RefillLocked(want, out start);

        if (start is { } s) {
            int n = 0;
            Exception? failure = null;
            try { n = _inner.Read(s.Into, s.At, s.Room); }
            catch (Exception e) { failure = e; }
            Commit(s, n, failure);
        } else {
            pending?.GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// One refill, for a caller that suspends. The read runs with no token;
    /// <paramref name="cancel"/> only ends this caller's wait for it.
    /// </summary>
    private ValueTask FillAsync(int want, CancellationToken cancel) {
        var pending = EnsureRefill(want);
        if (pending.IsCompleted) return default;
        return new ValueTask(cancel.CanBeCanceled ? pending.WaitAsync(cancel) : pending);
    }

    private async Task Pump(RefillStart s) {
        int n = 0;
        Exception? failure = null;

        try {
            n = await _inner.ReadAsync(s.Into.AsMemory(s.At, s.Room), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception e) {
            failure = e;
        }

        Commit(s, n, failure);
    }

    /// What a finished read leaves behind. Never throws.
    private void Commit(RefillStart s, int n, Exception? failure) {
        using (Hold()) {
            if (failure is not null) _error ??= failure;
            else if (n <= 0) _ended = true;
            else _len = s.At + n;

            // Cleared before the task is completed, so a reader woken by it
            // that still needs more starts a FRESH refill rather than finding
            // this spent one.
            _refill = null;
        }

        s.Done.TrySetResult();
        Deliver();
    }

    // --- Waiting for a tentative take ---------------------------------------
    //
    // Every read other than the events' own takes outright under the lock, and
    // must not do so while an event's tentative take holds the cursor. These
    // are the two ways of waiting it out.

    /// Under the lock: the signal that the tentative take has finished.
    private Task QuietLocked() {
        _quiet ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _quiet.Task;
    }

    /// <summary>
    /// Outside the lock, after a section that could not answer: spin while a
    /// tentative take holds the cursor, otherwise bring in more bytes.
    /// </summary>
    private void WaitSync(bool busy, int want, ref SpinWait spin) {
        if (busy) spin.SpinOnce();
        else FillSync(want);
    }

    /// The suspending twin: <paramref name="quiet"/> is the tentative take to
    /// wait out, or null to bring in more bytes.
    private ValueTask WaitAsync(Task? quiet, int want, CancellationToken cancel) =>
        quiet is not null ? new ValueTask(quiet.WaitAsync(cancel)) : FillAsync(want, cancel);

    // --- Events -------------------------------------------------------------

    /// <summary>
    /// One published read, parked until the buffer can answer it.
    ///
    /// The three steps are apart on purpose. <c>BeginLocked</c> runs under the
    /// lock and moves the cursor tentatively; <c>TryCommit</c> runs outside it,
    /// because committing fires the losing branches' nacks and a nack can
    /// resume a fiber; and then either <c>Handover</c> or <c>UndoLocked</c>.
    /// Nothing is consumed by a branch that loses.
    /// </summary>
    private abstract class Waiter {
        public SyncState State = null!;
        public int EventId;
        public Waiter? Next;

        /// What a refill on this waiter's behalf has to make room for.
        public abstract int Want { get; }

        /// Under the lock: can this be answered now, and if so take it.
        public abstract bool BeginLocked(BjoInputPort p);

        /// Outside the lock, the commit having succeeded.
        public abstract void Handover();

        /// Under the lock, the commit having failed. Put the cursor back.
        public abstract void UndoLocked(BjoInputPort p);
    }

    private sealed class ReadWaiter : Waiter {
        /// 0 is `read-some`; a positive count is `read-bytes`.
        public int Count;
        public Action<ByteRead> OnSync = null!;

        private ByteRead _value;
        private int _oldPos;
        private int _oldLow;

        public override int Want => Count <= 0 ? 1 : Count;

        public override bool BeginLocked(BjoInputPort p) {
            int n = p.TakeableLocked(Count);
            if (n < 0) return false;

            _oldPos = p._pos;
            _oldLow = p._pendingLow;
            _value = p.TakeLocked(n);
            return true;
        }

        public override void Handover() {
            var v = _value;
            _value = default;
            InboxWake.Resume(OnSync, v);
        }

        public override void UndoLocked(BjoInputPort p) {
            p._pos = _oldPos;
            p._pendingLow = _oldLow;
            _value = default;
        }
    }

    /// The port is finished — cleanly or because it failed. It says only that,
    /// which is what makes it the right thing to wait on for "the other end has
    /// gone away"; a read beside it is what says which of the two happened.
    private sealed class EofWaiter : Waiter {
        public Action<Unit> OnSync = null!;

        public override int Want => 1;

        public override bool BeginLocked(BjoInputPort p) => p._pos >= p._len && p.Finished;

        public override void Handover() => InboxWake.Resume(OnSync, Unit.Value);

        public override void UndoLocked(BjoInputPort p) { }
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
        // Take ownership, or leave a note for whoever has it.
        //
        // THE NOTE IS A COMPARE-AND-SWAP FROM `Running`, NEVER A PLAIN WRITE,
        // and that is the whole of this loop. A plain write is decided while
        // the owner is still inside and can land *after* the owner has released
        // the state to `Idle`:
        //
        //     U: CAS(Running, Idle) fails — reads Running, the owner is inside
        //     T: exit CAS succeeds — the state becomes Idle, T leaves
        //     U: writes RunningAgain — with nobody running
        //
        // From then on every call here sees a state that is not `Idle`, writes
        // the note again and returns, and the port is dead: bytes in its
        // buffer, readers parked on it, and nothing left to hand them over. A
        // CAS from `Running` cannot do that, because a release has already
        // moved the state out of `Running`.
        while (true) {
            int state = Volatile.Read(ref _deliverState);

            if (state == Idle) {
                if (Interlocked.CompareExchange(ref _deliverState, Running, Idle) == Idle) break;
                continue;                      // someone took it first; look again
            }

            if (state == RunningAgain) return; // already told
            if (Interlocked.CompareExchange(ref _deliverState, RunningAgain, Running) == Running) return;
            // It moved under us — the owner released, or another thread told
            // them first. Round again.
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

        using (Hold()) {
            // No waiters is the common case of a port read only by plain reads,
            // which is every text read: one load and out.
            if (_waiters is null) return false;

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
        using (Hold()) chosen.UndoLocked(this);
        EndTake();
        return true;
    }

    private void EndTake() {
        TaskCompletionSource? quiet;

        using (Hold()) {
            _taking = false;
            quiet = _quiet;
            _quiet = null;
        }

        quiet?.TrySetResult();
    }

    private void RefillForWaiters() {
        int want = 0;

        using (Hold()) {
            if (_waiters is null || _refill is not null || _disposed || Finished) return;

            for (var w = _waiters; w is not null; w = w.Next)
                if (!w.State.IsSynchronized && w.Want > want)
                    want = w.Want;
        }

        if (want > 0) EnsureRefill(want);
    }

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
        using (Hold()) {
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

        using (Hold()) {
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
        using (Hold()) AppendLocked(w);
        Deliver();
    }

    internal void PublishEof(SyncState state, int eventId, Action<Unit> onSync) {
        ThrowIfDisposed();
        Interlocked.Increment(ref _published);

        var w = new EofWaiter { State = state, EventId = eventId, OnSync = onSync };
        using (Hold()) AppendLocked(w);
        Deliver();
    }

    private int _published;

    /// <summary>
    /// How many reads have gone the published way rather than taking the
    /// <see cref="INowable{T}"/> fast path. For the tests: "a buffered read
    /// commits without publishing" is a claim about which path was taken.
    /// </summary>
    internal int PublishCount => Volatile.Read(ref _published);

    // --- Plain byte reads -------------------------------------------------------
    //
    // What `AsStream` and its users are built on. Ordinary waits, not events:
    // nothing about them can lose a `choose`, so they take outright, and a
    // failure reaches the caller as a throw.

    /// Under the lock: up to <c>into.Length</c> bytes, 0 at end of input, or -1
    /// when a refill has to come first.
    private int IntoLocked(Span<byte> into) {
        int have = _len - _pos;
        if (have > 0) {
            int n = Math.Min(into.Length, have);
            _buf.AsSpan(_pos, n).CopyTo(into);
            _pos += n;
            _pendingLow = -1;
            return n;
        }

        return FinishedLocked() ? 0 : -1;
    }

    /// Up to <c>buffer.Length</c> bytes, 0 at end of input.
    public int ReadInto(Span<byte> buffer) {
        if (buffer.IsEmpty) return 0;

        var spin = new SpinWait();
        while (true) {
            bool busy = false;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) busy = true;
                else if (IntoLocked(buffer) is var n and >= 0) return n;
            }
            WaitSync(busy, 1, ref spin);
        }
    }

    /// <summary>The suspending twin of <see cref="ReadInto"/>.</summary>
    public async ValueTask<int> ReadIntoAsync(Memory<byte> buffer, CancellationToken cancel = default) {
        if (buffer.IsEmpty) return 0;

        while (true) {
            Task? quiet = null;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) quiet = QuietLocked();
                else if (IntoLocked(buffer.Span) is var n and >= 0) return n;
            }
            await WaitAsync(quiet, 1, cancel).ConfigureAwait(false);
        }
    }

    // --- Peeking at bytes ---------------------------------------------------------

    private BjolangRuntime.Option<byte[]> PeekLocked(int skip, int count) {
        int have = _len - _pos - skip;
        if (have <= 0) return BjolangRuntime.None<byte[]>();

        int n = Math.Min(count, have);
        var bytes = new byte[n];
        Buffer.BlockCopy(_buf, _pos + skip, bytes, 0, n);
        return BjolangRuntime.Some(bytes);
    }

    /// Under the lock: the peek, or null when a refill has to come first.
    private BjolangRuntime.Option<byte[]>? PeekBytesLocked(int skip, int count) {
        if (_len - _pos >= skip + count) return PeekLocked(skip, count);
        if (_error is not null && _len - _pos == 0) throw Rethrow(_error);
        if (Finished) return PeekLocked(skip, count);
        return null;
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
    public BjolangRuntime.Option<byte[]> PeekBytes(int skip, int count) {
        CheckPeek(skip, count);
        if (count == 0) return BjolangRuntime.Some(Array.Empty<byte>());

        var spin = new SpinWait();
        while (true) {
            bool busy = false;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) busy = true;
                else if (PeekBytesLocked(skip, count) is { } peeked) return peeked;
            }
            WaitSync(busy, skip + count, ref spin);
        }
    }

    public async ValueTask<BjolangRuntime.Option<byte[]>> PeekBytesAsync(
        int skip, int count, CancellationToken cancel = default) {

        CheckPeek(skip, count);
        if (count == 0) return BjolangRuntime.Some(Array.Empty<byte>());

        while (true) {
            Task? quiet = null;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) quiet = QuietLocked();
                else if (PeekBytesLocked(skip, count) is { } peeked) return peeked;
            }
            await WaitAsync(quiet, skip + count, cancel).ConfigureAwait(false);
        }
    }

    // --- The eof question ---------------------------------------------------

    /// <summary>
    /// Under the lock: true or false when known, null when a refill has to say.
    ///
    /// A port that FAILED is not at end of input, and this raises rather than
    /// answering true — or `(loop (:finish (port-eof? p)) ...)` would end
    /// normally on a broken socket and return a truncated result as though it
    /// were the whole thing.
    /// </summary>
    private bool? EofLocked() {
        if (_pendingLow >= 0) return false;
        if (_pos < _len) return false;
        if (FinishedLocked()) return true;
        return null;
    }

    /// <summary>
    /// Whether the port is at end of input. No syscall and no suspension
    /// whenever the buffer holds anything — which, reading a file a line at a
    /// time, is all but one call in a bufferful.
    /// </summary>
    public bool Eof() {
        var spin = new SpinWait();
        while (true) {
            bool busy = false;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) busy = true;
                else if (EofLocked() is { } known) return known;
            }
            WaitSync(busy, 1, ref spin);
        }
    }

    public ValueTask<bool> EofAsync(CancellationToken cancel = default) {
        using (Hold()) {
            ThrowIfDisposed();
            if (!_taking && EofLocked() is { } known) return new ValueTask<bool>(known);
        }
        return EofSlowAsync(cancel);
    }

    private async ValueTask<bool> EofSlowAsync(CancellationToken cancel) {
        while (true) {
            Task? quiet = null;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) quiet = QuietLocked();
                else if (EofLocked() is { } known) return known;
            }
            await WaitAsync(quiet, 1, cancel).ConfigureAwait(false);
        }
    }

    // --- Characters -----------------------------------------------------------

    /// <summary>
    /// Under the lock: the character at the cursor and how many bytes it
    /// takes, without moving. False when a refill has to come first — a
    /// multi-byte character is only decoded once all of its bytes are here.
    /// <paramref name="scalar"/> is -1 when no more input will come; the
    /// caller asks <see cref="FinishedLocked"/> whether that is the end or a
    /// failure.
    ///
    /// An invalid sequence decodes to U+FFFD and takes the bytes of its
    /// maximal invalid prefix, as <see cref="Rune.DecodeFromUtf8"/> and
    /// <see cref="Encoding.UTF8"/> agree it should; so does a sequence cut off
    /// by the end of the input. One cut off by a failure decodes to nothing,
    /// so that the failure is what the read reports.
    /// </summary>
    private bool PeekScalarLocked(out int scalar, out int size) {
        scalar = -1;
        size = 0;

        if (_pos < _len) {
            byte b = _buf[_pos];
            if (b < 0x80) {
                scalar = b;
                size = 1;
                return true;
            }

            var status = Rune.DecodeFromUtf8(_buf.AsSpan(_pos, _len - _pos), out var rune, out size);
            if (status == OperationStatus.NeedMoreData) {
                if (!Finished) return false;
                if (_error is not null) {
                    size = 0;
                    return true;
                }
            }

            scalar = rune.Value;
            return true;
        }

        return Finished;
    }

    private static Exception UnpairedLow() =>
        new InvalidOperationException(
            "read-char: the port holds an unpaired low surrogate, which is not a character.");

    /// Under the lock: the next character, taken whole, or -1 at end of input.
    /// False when a refill has to come first.
    private bool ScalarLocked(bool take, out int scalar) {
        if (_pendingLow >= 0) {
            // Half a character a UTF-16 read left behind. Taken, so that the
            // port moves on past it, and reported.
            if (take) _pendingLow = -1;
            throw UnpairedLow();
        }

        if (!PeekScalarLocked(out scalar, out int size)) return false;
        if (scalar < 0) FinishedLocked();
        else if (take) _pos += size;
        return true;
    }

    /// One character as a Unicode scalar, or -1 at end of input.
    public int ReadScalar() => ScalarSync(take: true);

    /// The character `read-char` would answer, left where it is: `peek-char`.
    public int PeekScalar() => ScalarSync(take: false);

    private int ScalarSync(bool take) {
        var spin = new SpinWait();
        while (true) {
            bool busy = false;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) busy = true;
                else if (ScalarLocked(take, out int scalar)) return scalar;
            }
            WaitSync(busy, 1, ref spin);
        }
    }

    public ValueTask<int> ReadScalarValueAsync(CancellationToken cancel = default) =>
        ScalarAsync(take: true, cancel);

    public ValueTask<int> PeekScalarValueAsync(CancellationToken cancel = default) =>
        ScalarAsync(take: false, cancel);

    private ValueTask<int> ScalarAsync(bool take, CancellationToken cancel) {
        using (Hold()) {
            ThrowIfDisposed();
            if (!_taking && ScalarLocked(take, out int scalar)) return new ValueTask<int>(scalar);
        }
        return ScalarSlowAsync(take, cancel);
    }

    private async ValueTask<int> ScalarSlowAsync(bool take, CancellationToken cancel) {
        while (true) {
            Task? quiet = null;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) quiet = QuietLocked();
                else if (ScalarLocked(take, out int scalar)) return scalar;
            }
            await WaitAsync(quiet, 1, cancel).ConfigureAwait(false);
        }
    }

    // --- UTF-16 code units, for the TextReader contract -------------------------

    /// Under the lock: the next code unit or -1, or null when a refill has to
    /// say. Moves only when <paramref name="take"/>.
    private int? UnitLocked(bool take) {
        if (_pendingLow >= 0) {
            int low = _pendingLow;
            if (take) _pendingLow = -1;
            return low;
        }

        if (!PeekScalarLocked(out int scalar, out int size)) return null;

        if (scalar < 0) {
            FinishedLocked();
            return -1;
        }

        if (scalar <= 0xFFFF) {
            if (take) _pos += size;
            return scalar;
        }

        Span<char> pair = stackalloc char[2];
        new Rune(scalar).EncodeToUtf16(pair);
        if (take) {
            _pos += size;
            _pendingLow = pair[1];
        }
        return pair[0];
    }

    private int UnitSync(bool take) {
        var spin = new SpinWait();
        while (true) {
            bool busy = false;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) busy = true;
                else if (UnitLocked(take) is { } unit) return unit;
            }
            WaitSync(busy, 1, ref spin);
        }
    }

    public override int Peek() => UnitSync(take: false);

    public override int Read() => UnitSync(take: true);

    /// The suspending twin of <see cref="Read()"/>, for a reader that works in
    /// code units.
    public ValueTask<int> ReadUnitValueAsync(CancellationToken cancel = default) {
        using (Hold()) {
            ThrowIfDisposed();
            if (!_taking && UnitLocked(take: true) is { } unit) return new ValueTask<int>(unit);
        }
        return ReadUnitSlowAsync(cancel);
    }

    private async ValueTask<int> ReadUnitSlowAsync(CancellationToken cancel) {
        while (true) {
            Task? quiet = null;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) quiet = QuietLocked();
                else if (UnitLocked(take: true) is { } unit) return unit;
            }
            await WaitAsync(quiet, 1, cancel).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Under the lock: as many code units as fit, 0 at end of input, or -1
    /// when a refill has to come first. Stops early rather than wait, at a
    /// character whose bytes are not all here yet.
    /// </summary>
    private int CharsLocked(Span<char> into) {
        int n = 0;

        if (_pendingLow >= 0) {
            into[n++] = (char)_pendingLow;
            _pendingLow = -1;
        }

        Span<char> pair = stackalloc char[2];
        while (n < into.Length) {
            if (!PeekScalarLocked(out int scalar, out int size) || scalar < 0) break;
            _pos += size;

            if (scalar <= 0xFFFF) {
                into[n++] = (char)scalar;
                continue;
            }

            new Rune(scalar).EncodeToUtf16(pair);
            into[n++] = pair[0];
            if (n < into.Length) into[n++] = pair[1];
            else _pendingLow = pair[1];
        }

        if (n > 0) return n;

        // Nothing taken, so this is the place to report the end or a failure.
        if (PeekScalarLocked(out int next, out _) && next < 0) return FinishedLocked() ? 0 : -1;
        return -1;
    }

    public override int Read(char[] buffer, int index, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(index, count));
    }

    public override int Read(Span<char> buffer) {
        if (buffer.IsEmpty) return 0;

        var spin = new SpinWait();
        while (true) {
            bool busy = false;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) busy = true;
                else if (CharsLocked(buffer) is var n and >= 0) return n;
            }
            WaitSync(busy, 1, ref spin);
        }
    }

    public override int ReadBlock(char[] buffer, int index, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadBlock(buffer.AsSpan(index, count));
    }

    public override int ReadBlock(Span<char> buffer) {
        int total = 0;
        while (total < buffer.Length) {
            int n = Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public override Task<int> ReadAsync(char[] buffer, int index, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadAsync(buffer.AsMemory(index, count)).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancel = default) {
        if (buffer.IsEmpty) return 0;

        while (true) {
            Task? quiet = null;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) quiet = QuietLocked();
                else if (CharsLocked(buffer.Span) is var n and >= 0) return n;
            }
            await WaitAsync(quiet, 1, cancel).ConfigureAwait(false);
        }
    }

    public override Task<int> ReadBlockAsync(char[] buffer, int index, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadBlockAsync(buffer.AsMemory(index, count)).AsTask();
    }

    public override async ValueTask<int> ReadBlockAsync(Memory<char> buffer, CancellationToken cancel = default) {
        int total = 0;
        while (total < buffer.Length) {
            int n = await ReadAsync(buffer[total..], cancel).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // --- Lines --------------------------------------------------------------
    //
    // What ends a line is the reader's choice, among Racket's `read-line`
    // modes (see `LineMode`). The default, `Any`, is `\n`, `\r` or `\r\n`,
    // the longest that matches, so the `\n` of a `\r\n` is consumed with it;
    // a `\r` that is the last byte buffered then waits for one more refill to
    // learn whether an `\n` follows it. The search is over bytes: neither byte
    // can occur inside a multi-byte sequence.

    /// <summary>
    /// Under the lock: where the next line's bytes are, taken whole: true with
    /// <paramref name="some"/> and the line's bounds, true without at end of
    /// input, or false when a refill has to come first — in which case nothing
    /// has moved. The bounds stay valid only while the lock is held.
    /// </summary>
    private bool LineBoundsLocked(LineMode mode, out int start, out int count, out bool some) {
        start = 0;
        count = 0;
        some = false;

        var window = _buf.AsSpan(_pos, _len - _pos);

        // Where the terminator starts in the window, and how long it is.
        int rel;
        int ending = 1;
        switch (mode) {
            case LineMode.Any:
                rel = window.IndexOfAny((byte)'\r', (byte)'\n');
                if (rel >= 0 && window[rel] == (byte)'\r') {
                    // Whether this `\r` is a `\r\n` is not known yet.
                    if (rel + 1 == window.Length && !Finished) return false;
                    if (rel + 1 < window.Length && window[rel + 1] == (byte)'\n') ending = 2;
                }
                break;
            case LineMode.AnyOne:
                rel = window.IndexOfAny((byte)'\r', (byte)'\n');
                break;
            case LineMode.Linefeed:
                rel = window.IndexOf((byte)'\n');
                break;
            case LineMode.Return:
                rel = window.IndexOf((byte)'\r');
                break;
            case LineMode.ReturnLinefeed:
                // A `\r` last in the buffer is no terminator yet, and so no
                // reason to stop: the search simply finds none and waits.
                rel = window.IndexOf("\r\n"u8);
                ending = 2;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a line mode.");
        }

        if (rel >= 0) {
            start = _pos;
            count = rel;
            some = true;
            _pos += rel + ending;
            return true;
        }

        // Input that failed part way through a line has lost the rest of it,
        // so the failure is what this read reports, not the fragment.
        if (!FinishedLocked()) return false;

        // Input that ended without a terminator is still a line; and a pending
        // low surrogate is a line of its own if nothing follows it.
        if (_pos < _len || _pendingLow >= 0) {
            start = _pos;
            count = _len - _pos;
            some = true;
            _pos = _len;
        }

        return true;
    }

    private static readonly byte[] Replacement = [0xEF, 0xBF, 0xBD];

    /// Under the lock: the bytes as a Bjolang string, copied once when valid,
    /// which is nearly always, and with each invalid sequence replaced by
    /// U+FFFD otherwise. A pending low surrogate goes first, as U+FFFD: on its
    /// own it is not a character.
    private Utf8String TextLocked(ReadOnlySpan<byte> bytes) {
        if (_pendingLow < 0) return Utf8String.FromUtf8Lossy(bytes);

        _pendingLow = -1;
        var joined = new byte[Replacement.Length + bytes.Length];
        Replacement.CopyTo(joined, 0);
        bytes.CopyTo(joined.AsSpan(Replacement.Length));
        return Utf8String.FromUtf8Lossy(joined);
    }

    /// Under the lock: the bytes as a .NET string, after a pending low
    /// surrogate if there is one.
    private string StringLocked(ReadOnlySpan<byte> bytes) {
        var text = Encoding.UTF8.GetString(bytes);
        if (_pendingLow < 0) return text;

        var low = (char)_pendingLow;
        _pendingLow = -1;
        return string.Concat(new ReadOnlySpan<char>(in low), text);
    }

    private bool LineLocked(LineMode mode, out BjolangRuntime.Option<Utf8String> line) {
        line = default;
        if (!LineBoundsLocked(mode, out int start, out int count, out bool some)) return false;
        if (some) line = BjolangRuntime.Some(TextLocked(_buf.AsSpan(start, count)));
        return true;
    }

    /// The next line, ended as <paramref name="mode"/> says, or `None` at end
    /// of input.
    public BjolangRuntime.Option<Utf8String> ReadLineUtf8(LineMode mode = LineMode.Any) {
        var spin = new SpinWait();
        while (true) {
            bool busy = false;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) busy = true;
                else if (LineLocked(mode, out var line)) return line;
            }
            WaitSync(busy, 1, ref spin);
        }
    }

    public ValueTask<BjolangRuntime.Option<Utf8String>> ReadLineUtf8ValueAsync(CancellationToken cancel = default) =>
        ReadLineUtf8ValueAsync(LineMode.Any, cancel);

    public ValueTask<BjolangRuntime.Option<Utf8String>> ReadLineUtf8ValueAsync(
        LineMode mode, CancellationToken cancel = default) {
        using (Hold()) {
            ThrowIfDisposed();
            if (!_taking && LineLocked(mode, out var line)) return new ValueTask<BjolangRuntime.Option<Utf8String>>(line);
        }
        return ReadLineUtf8SlowAsync(mode, cancel);
    }

    private async ValueTask<BjolangRuntime.Option<Utf8String>> ReadLineUtf8SlowAsync(
        LineMode mode, CancellationToken cancel) {
        while (true) {
            Task? quiet = null;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) quiet = QuietLocked();
                else if (LineLocked(mode, out var line)) return line;
            }
            await WaitAsync(quiet, 1, cancel).ConfigureAwait(false);
        }
    }

    /// The .NET `ReadLine` contract is `StreamReader`'s, which is `Any`.
    private bool LineStringLocked(out string? line) {
        line = null;
        if (!LineBoundsLocked(LineMode.Any, out int start, out int count, out bool some)) return false;
        if (some) line = StringLocked(_buf.AsSpan(start, count));
        return true;
    }

    public override string? ReadLine() {
        var spin = new SpinWait();
        while (true) {
            bool busy = false;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) busy = true;
                else if (LineStringLocked(out var line)) return line;
            }
            WaitSync(busy, 1, ref spin);
        }
    }

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancel) {
        while (true) {
            Task? quiet = null;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) quiet = QuietLocked();
                else if (LineStringLocked(out var line)) return line;
            }
            await WaitAsync(quiet, 1, cancel).ConfigureAwait(false);
        }
    }

    // --- Everything that is left --------------------------------------------

    /// <summary>
    /// Under the lock: the bounds of the rest of the input once all of it has
    /// arrived, or false while more is coming. Until then it all stays in the
    /// buffer, so a cancelled `read-all` loses none of it.
    /// </summary>
    private bool RestBoundsLocked(out int start, out int count) {
        start = count = 0;
        if (!FinishedLocked()) return false;
        start = _pos;
        count = _len - _pos;
        _pos = _len;
        return true;
    }

    private bool RestLocked(out Utf8String rest) {
        rest = default;
        if (!RestBoundsLocked(out int start, out int count)) return false;
        rest = TextLocked(_buf.AsSpan(start, count));
        return true;
    }

    /// Everything left, as a Bjolang string.
    public Utf8String ReadToEndUtf8() {
        var spin = new SpinWait();
        while (true) {
            bool busy = false;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) busy = true;
                else if (RestLocked(out var rest)) return rest;
            }
            WaitSync(busy, 1, ref spin);
        }
    }

    public async ValueTask<Utf8String> ReadToEndUtf8Async(CancellationToken cancel = default) {
        while (true) {
            Task? quiet = null;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) quiet = QuietLocked();
                else if (RestLocked(out var rest)) return rest;
            }
            await WaitAsync(quiet, 1, cancel).ConfigureAwait(false);
        }
    }

    private bool RestStringLocked(out string? rest) {
        rest = null;
        if (!RestBoundsLocked(out int start, out int count)) return false;
        rest = StringLocked(_buf.AsSpan(start, count));
        return true;
    }

    public override string ReadToEnd() {
        var spin = new SpinWait();
        while (true) {
            bool busy = false;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) busy = true;
                else if (RestStringLocked(out var rest)) return rest!;
            }
            WaitSync(busy, 1, ref spin);
        }
    }

    public override Task<string> ReadToEndAsync() => ReadToEndAsync(CancellationToken.None);

    public override async Task<string> ReadToEndAsync(CancellationToken cancel) {
        while (true) {
            Task? quiet = null;
            using (Hold()) {
                ThrowIfDisposed();
                if (_taking) quiet = QuietLocked();
                else if (RestStringLocked(out var rest)) return rest!;
            }
            await WaitAsync(quiet, 1, cancel).ConfigureAwait(false);
        }
    }

    // --- The stream view ----------------------------------------------------

    /// <summary>
    /// The port as a <see cref="Stream"/>: the buffer first, then refills
    /// through the port.
    ///
    /// **This, and never the port's own stream, is what anything reading
    /// through the port goes over** — a `limited` view, a transcoder, a .NET
    /// API that wants a `Stream`. Reading the port's own stream would skip
    /// whatever the buffer held from a peek or from a refill that overshot,
    /// silently.
    ///
    /// Disposing the view does not dispose the port.
    /// </summary>
    public Stream AsStream() => new PortStream(this);

    private sealed class PortStream : Stream, ISyncReadable {
        private readonly BjoInputPort _port;

        public PortStream(BjoInputPort port) => _port = port;

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
            throw new NotSupportedException("this is a view of an input port.");

        /// Deliberately empty: the port is not the view's to close.
        protected override void Dispose(bool disposing) { }
    }

    // --- Closing ------------------------------------------------------------

    /// <summary>
    /// Releasing the handle, and nothing else.
    ///
    /// **Close is not the wakeup.** Disposing a stream under a read is racy
    /// across stream types, so a reader parked here is woken by the ambient
    /// cancellation token instead, which is what makes `with-deadline` work on
    /// a stalled read. A refill still in flight then fails into the port or
    /// finishes there; nobody is waiting for it.
    /// </summary>
    protected override void Dispose(bool disposing) {
        bool first;
        using (Hold()) {
            first = !_disposed;
            _disposed = true;
        }

        if (first && disposing) {
            // One half of a connection closes a half; the handle goes with the
            // second one. Anything else disposes what it owns.
            if (_connection is not null) _connection.HalfClosed();
            else if (_ownsInner) _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
