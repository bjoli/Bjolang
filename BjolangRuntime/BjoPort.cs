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

// A port that owns its buffer, so that "is this port finished?" is answerable
// without a syscall.
//
// `port-eof?` is `(= (.Peek p) -1)`, and `TextReader` has no async peek. That
// is not an oversight in .NET: **eof on a stream is a read**. You cannot know
// whether a socket is finished without waiting for a byte or a FIN, and peek is
// "read one and put it back" — the read being the part that waits. So a peek
// that suspends is the honest shape, and the only way to make the common case
// not suspend is to hold the character that was already read.
//
// Hence a buffer we own rather than `StreamReader`'s, which is private. With
// one, `EofAsync` completes synchronously whenever anything is left, and
// `ReadLineValueAsync` can return `ValueTask<string?>` — where
// `TextReader.ReadLineAsync` must allocate a `Task`, because it has nothing
// that lets it finish without one.
//
// `BjoPort` *is* a `TextReader`, which is what keeps the type surface still:
// `TextInputPort` stays `System.IO.TextReader`, so `with-open`,
// `(cast TextInputPort ...)`, `(.Peek p)` and handing a port to a .NET API all
// go on working.

using System.Runtime.ExceptionServices;
using System.Text;
using Unit = Bjoml.Unit;

namespace Bjolang.Runtime;

/// A buffered <see cref="TextReader" /> with an eof question that usually costs
/// nothing, and reads that can be cancelled.
///
/// **Every virtual read is overridden, and that is load bearing.** If any
/// inherited path reached <c>inner</c> while the buffer still held characters,
/// those characters would be skipped — silently, as wrong output rather than as
/// an exception. The rule for anything added here: read through the buffer, or
/// drain the buffer first.
///
/// # The port owns the buffer
///
/// The guarantees are the byte port's (<see cref="BjoByteInputPort"/>), and
/// for its reasons:
///
///   * **A read takes nothing until it is complete.** A line, a character or
///     the rest of the input is handed over by moving the cursor once, under
///     the lock, when all of it is in the buffer. Until then everything read so
///     far stays where it is, in the buffer, which grows when one line is longer
///     than it.
///   * **A fill is the port's, not a reader's.** It is started with no
///     cancellation token, at most one at a time, and it appends to the buffer
///     whatever happens to whoever asked for it. A reader whose deadline fires,
///     or whose scope is cancelled, stops waiting for the fill and leaves; the
///     fill finishes anyway and its text is there for the next read. So a
///     cancelled read consumes nothing.
///   * **Two fibers, one port.** Allowed: they serialize on the single fill and
///     on the lock, and every line is handed out exactly once. Which of them
///     gets which line is theirs to arrange.
///   * **A failure is sticky,** as end of input is, and is reported only once
///     the lines that arrived before it have been handed out.
///
/// The lock is held while the cursor moves and never across a read of
/// <c>inner</c> or an await.
public sealed class BjoPort : TextReader {
    private const int DefaultBufferSize = 4096;

    private readonly TextReader inner;

    /// The lock over the cursor: 1 while held.
    ///
    /// A spin lock and not a monitor, because of what it guards. Every section
    /// under it moves a cursor, copies characters out or allocates the string
    /// handed back, and none waits on anything; so it is never held long, never
    /// reentered and never contended in the common case of one reader. Taking
    /// it is one interlocked instruction, where a monitor's enter and exit cost
    /// a `read-char` about a third of its time.
    private int held;

    /// Holding <see cref="held"/>, released by <c>Dispose</c> at the end of a
    /// <c>using</c>, which releases it however the section is left.
    private readonly ref struct Held {
        private readonly BjoPort port;
        public Held(BjoPort port) => this.port = port;
        public void Dispose() => port.Release();
    }

    private void Release() => Volatile.Write(ref held, 0);

    private Held Hold() {
        if (Interlocked.CompareExchange(ref held, 1, 0) != 0) HoldSlow();
        return new Held(this);
    }

    private void HoldSlow() {
        var spin = new SpinWait();
        while (Interlocked.CompareExchange(ref held, 1, 0) != 0) spin.SpinOnce();
    }

    // The buffer, and the window of it that holds unread characters.
    //
    // `buf` and `len` move ONLY while a fill is being prepared or committed,
    // and there is at most one fill; a reader only ever moves `pos`, forwards,
    // and never past `len`. That pair of facts is what lets a fill write into
    // `buf[len..]` outside the lock while readers take from `buf[pos..len]`.
    private char[] buf;
    private int pos;
    private int len;

    /// The inner reader has answered zero once. Sticky: a stream does not
    /// un-end, and asking again after that costs a syscall for an answer we
    /// have.
    private bool ended;

    /// The inner reader failed once. Sticky, for the same reason, and thrown
    /// with its original stack.
    private ExceptionDispatchInfo? failure;

    /// The fill in flight, or null. Holding it in a field is the whole of "at
    /// most one": a second reader waits for this rather than starting another.
    private Task? fill;

    private bool disposed;

    /// <summary>
    /// The scope registration that will dispose this port, or null for a port
    /// nothing owns.
    ///
    /// Null for the three standard ports and for a string port: nothing
    /// releases stdin, and a `StringReader` is memory. Non-null for a file, set
    /// by the constructor that opened it, before the caller can lose the
    /// handle.
    ///
    /// A C# field and not an `(owner p)` in Bjolang. The only things that read
    /// it are `close-input-port` and `with-open`, both of which go through
    /// <see cref="BjolangRuntime.CloseOwnedOrDispose"/>.
    /// </summary>
    public BjolangRuntime.Owned? Owner;

    public BjoPort(TextReader inner) : this(inner, DefaultBufferSize) { }

    /// The buffer size is settable because every interesting bug in this class
    /// lives at a buffer boundary — a `\r\n` split across two fills, a line
    /// longer than one bufferful — and a test that cannot make the boundary
    /// fall where it wants cannot reach them.
    public BjoPort(TextReader inner, int bufferSize) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 2);
        this.inner = inner;
        buf = new char[bufferSize];
    }

    /// Wrap unless it is already one of ours. Nesting two buffers would work
    /// and would double the copying for nothing.
    public static BjoPort Wrap(TextReader inner) => inner as BjoPort ?? new BjoPort(inner);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    // --- Filling ------------------------------------------------------------
    //
    // `len` is assigned only *after* the read returns, and a fill that throws
    // records the failure instead. So a fill never leaves a stale length behind
    // that would re-serve characters already handed out, and a cancelled
    // *reader* has no way to touch the port at all: it was only waiting.
    //
    // A failed or cancelled read must not set `ended`. If it did,
    // `(loop (:finish (port-eof? p)) ...)` would end normally and return a
    // partial result as though it were the whole thing.

    /// <summary>
    /// Room after <c>len</c> for the next fill. Under the lock, and only while
    /// no fill is in flight, which is what keeps the array still while one is
    /// writing into it.
    ///
    /// Growing is how a line longer than the buffer stays in the port rather
    /// than in a reader's local builder, where a cancelled read would lose it.
    /// </summary>
    private void MakeRoomLocked() {
        int have = len - pos;

        if (have == 0) {
            pos = len = 0;
            return;
        }

        // Plenty of room after what is held: leave it where it is.
        if (buf.Length - len >= buf.Length / 2) return;

        if (pos > 0) {
            Array.Copy(buf, pos, buf, 0, have);
            pos = 0;
            len = have;
            if (buf.Length - len >= buf.Length / 2) return;
        }

        var bigger = new char[buf.Length * 2];
        Array.Copy(buf, pos, bigger, 0, have);
        buf = bigger;
        pos = 0;
        len = have;
    }

    /// <summary>
    /// Under the lock: the fill to wait for, or a fresh one this caller is to
    /// perform, described by <paramref name="start"/>. Null and no start when
    /// there will never be more: the input ended, failed or was disposed.
    /// </summary>
    private Task? FillLocked(out (TaskCompletionSource Done, char[] Into, int At, int Room)? start) {
        start = null;
        if (fill is not null) return fill;
        if (disposed || ended || failure is not null) return null;

        MakeRoomLocked();

        // Published into the field BEFORE the read starts, so that a read
        // completing synchronously cannot find `fill` still null and let a
        // second one through.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fill = done.Task;
        start = (done, buf, len, buf.Length - len);
        return done.Task;
    }

    /// What a finished read leaves behind. Never throws: the failure is the
    /// port's, and whoever reads next finds it.
    private void Commit(TaskCompletionSource done, int at, int n, Exception? error) {
        using (Hold()) {
            if (error is not null) failure ??= ExceptionDispatchInfo.Capture(error);
            else if (n <= 0) ended = true;
            else len = at + n;

            // Cleared before the task completes, so a reader woken by it that
            // still needs more starts a FRESH fill rather than finding this one.
            fill = null;
        }

        done.TrySetResult();
    }

    /// One fill, for a caller that may block: performed here with a blocking
    /// read, or waited for if another reader's is already in flight.
    private void FillSync() {
        Task? pending;
        (TaskCompletionSource Done, char[] Into, int At, int Room)? start;

        using (Hold()) pending = FillLocked(out start);

        if (start is { } s) {
            int n = 0;
            Exception? error = null;
            try { n = inner.Read(s.Into, s.At, s.Room); }
            catch (Exception e) { error = e; }
            Commit(s.Done, s.At, n, error);
        } else {
            pending?.GetAwaiter().GetResult();
        }
    }

    /// One fill, for a caller that suspends. The read itself runs with no
    /// token; <paramref name="cancel"/> only ends this caller's wait for it.
    private ValueTask FillAsync(CancellationToken cancel) {
        Task? pending;
        (TaskCompletionSource Done, char[] Into, int At, int Room)? start;

        using (Hold()) pending = FillLocked(out start);

        if (start is { } s) _ = Pump(s.Done, s.Into, s.At, s.Room);

        if (pending is null || pending.IsCompleted) return default;
        return new ValueTask(cancel.CanBeCanceled ? pending.WaitAsync(cancel) : pending);
    }

    private async Task Pump(TaskCompletionSource done, char[] into, int at, int room) {
        int n = 0;
        Exception? error = null;
        try { n = await inner.ReadAsync(into.AsMemory(at, room), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) { error = e; }
        Commit(done, at, n, error);
    }

    /// Under the lock: nothing more is coming. Throws the sticky failure, which
    /// is only asked once everything that arrived before it is gone.
    private bool FinishedLocked() {
        if (ended) return true;
        failure?.Throw();
        return false;
    }

    // --- The eof question ---------------------------------------------------

    /// Under the lock: true or false when known, null when a fill has to say.
    private bool? EofLocked() {
        if (pos < len) return false;
        if (FinishedLocked()) return true;
        return null;
    }

    /// Whether the port is at end of input.
    ///
    /// No syscall, no allocation and no suspension whenever the buffer holds
    /// anything — which, reading a file a line at a time, is all but one call
    /// in a bufferful.
    public ValueTask<bool> EofAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        using (Hold()) {
            if (EofLocked() is { } known) return new ValueTask<bool>(known);
        }
        return EofSlowAsync(cancel);
    }

    private async ValueTask<bool> EofSlowAsync(CancellationToken cancel) {
        while (true) {
            await FillAsync(cancel).ConfigureAwait(false);
            ThrowIfDisposed();
            using (Hold()) {
                if (EofLocked() is { } known) return known;
            }
        }
    }

    /// The blocking twin, for an ordinary function.
    public bool Eof() {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (EofLocked() is { } known) return known;
            }
            FillSync();
        }
    }

    // --- Character reads ----------------------------------------------------

    /// Under the lock: the next code unit or -1, or null when a fill has to
    /// say. Moves the cursor only when <paramref name="take"/>.
    private int? UnitLocked(bool take) {
        if (pos < len) return take ? buf[pos++] : buf[pos];
        if (FinishedLocked()) return -1;
        return null;
    }

    private int UnitSync(bool take) {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (UnitLocked(take) is { } unit) return unit;
            }
            FillSync();
        }
    }

    public override int Peek() => UnitSync(take: false);

    public override int Read() => UnitSync(take: true);

    /// The async single-character read, for `read-char`.
    ///
    /// A code unit rather than a scalar, like `Peek` and `Read`, because that
    /// is what the contract of the type it overrides says. Putting a surrogate
    /// pair back together is `read-char`'s job and is done once, in
    /// `reader-read-char!`.
    public ValueTask<int> ReadValueAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        using (Hold()) {
            // The common case spelled out, ahead of the general one.
            if (pos < len) return new ValueTask<int>(buf[pos++]);
            if (UnitLocked(take: true) is { } unit) return new ValueTask<int>(unit);
        }
        return ReadSlowAsync(cancel);
    }

    private async ValueTask<int> ReadSlowAsync(CancellationToken cancel) {
        while (true) {
            await FillAsync(cancel).ConfigureAwait(false);
            ThrowIfDisposed();
            using (Hold()) {
                if (UnitLocked(take: true) is { } unit) return unit;
            }
        }
    }

    /// <summary>
    /// Under the lock: the next character, as its one or two code units, taken
    /// whole — a surrogate pair is never split between two readers. False when
    /// a fill has to come first. <paramref name="first"/> is -1 at end of
    /// input; <paramref name="second"/> is -1 unless the first is a high
    /// surrogate, and stays -1 for one left unpaired at the end, which
    /// `Assemble` reports.
    /// </summary>
    private bool ScalarLocked(out int first, out int second) {
        first = -1;
        second = -1;

        if (pos < len) {
            int unit = buf[pos];
            if (char.IsHighSurrogate((char)unit)) {
                if (pos + 1 < len) second = buf[pos + 1];
                else if (!FinishedLocked()) return false;
            }

            first = unit;
            pos += second < 0 ? 1 : 2;
            return true;
        }

        return FinishedLocked();
    }

    /// One character as code units, for `read-char`. See <see cref="ScalarLocked"/>.
    public (int First, int Second) ReadScalar() {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (ScalarLocked(out int first, out int second)) return (first, second);
            }
            FillSync();
        }
    }

    public ValueTask<(int First, int Second)> ReadScalarValueAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        using (Hold()) {
            if (ScalarLocked(out int first, out int second)) return new ValueTask<(int, int)>((first, second));
        }
        return ReadScalarSlowAsync(cancel);
    }

    private async ValueTask<(int First, int Second)> ReadScalarSlowAsync(CancellationToken cancel) {
        while (true) {
            await FillAsync(cancel).ConfigureAwait(false);
            ThrowIfDisposed();
            using (Hold()) {
                if (ScalarLocked(out int first, out int second)) return (first, second);
            }
        }
    }

    // --- Block reads --------------------------------------------------------

    /// Under the lock: how many were copied, 0 at end of input, or -1 when a
    /// fill has to come first.
    private int BlockLocked(Span<char> into) {
        if (pos < len) {
            int n = Math.Min(into.Length, len - pos);
            buf.AsSpan(pos, n).CopyTo(into);
            pos += n;
            return n;
        }

        return FinishedLocked() ? 0 : -1;
    }

    public override int Read(char[] buffer, int index, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(index, count));
    }

    public override int Read(Span<char> buffer) {
        if (buffer.IsEmpty) return 0;

        while (true) {
            ThrowIfDisposed();
            int n;
            using (Hold()) n = BlockLocked(buffer);
            if (n >= 0) return n;
            FillSync();
        }
    }

    public override int ReadBlock(char[] buffer, int index, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        return ReadBlock(buffer.AsSpan(index, count));
    }

    /// Unlike `Read`, this comes back short only at end of input.
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

    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancel = default) {
        ThrowIfDisposed();
        if (buffer.IsEmpty) return new ValueTask<int>(0);

        // The point of the buffer: a read that is already satisfied never
        // becomes a state machine.
        int n;
        using (Hold()) n = BlockLocked(buffer.Span);
        if (n >= 0) return new ValueTask<int>(n);

        return ReadBlockSlowAsync(buffer, cancel);
    }

    private async ValueTask<int> ReadBlockSlowAsync(Memory<char> buffer, CancellationToken cancel) {
        while (true) {
            await FillAsync(cancel).ConfigureAwait(false);
            ThrowIfDisposed();
            int n;
            using (Hold()) n = BlockLocked(buffer.Span);
            if (n >= 0) return n;
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
    // `\n`, `\r` and `\r\n` all end a line, and the `\n` of a `\r\n` is consumed
    // with it — `StreamReader`'s semantics exactly, because `read-line` is
    // documented against them and a port that disagreed would be a trap. A `\r`
    // that is the last character buffered therefore needs one more fill before
    // its line can be handed over, to find out whether an `\n` follows it.

    /// <summary>
    /// Under the lock: the next line, taken whole. True with the line, true
    /// with null at end of input, or false when a fill has to come first —
    /// in which case nothing has moved.
    /// </summary>
    private bool LineLocked(out string? line) {
        line = null;

        int rel = buf.AsSpan(pos, len - pos).IndexOfAny('\r', '\n');
        if (rel >= 0) {
            int at = pos + rel;
            bool cr = buf[at] == '\r';

            // Whether this `\r` is a `\r\n` is not known yet.
            if (cr && at + 1 == len && !FinishedLocked()) return false;

            line = new string(buf, pos, at - pos);
            pos = at + 1;
            if (cr && pos < len && buf[pos] == '\n') pos++;
            return true;
        }

        if (!FinishedLocked()) return false;

        // Input that ended without a terminator is still a line.
        if (pos < len) {
            line = new string(buf, pos, len - pos);
            pos = len;
        }

        return true;
    }

    public override string? ReadLine() {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (LineLocked(out var line)) return line;
            }
            FillSync();
        }
    }

    /// The suspending twin, and the reason this type exists rather than a
    /// helper beside `TextReader`: the buffer is what lets it complete without
    /// allocating, so it can return `ValueTask<string?>` where
    /// `TextReader.ReadLineAsync` has to return `Task<string?>`.
    ///
    /// `null` at end of input, which `read-line/opt` turns into `None`.
    public ValueTask<string?> ReadLineValueAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        using (Hold()) {
            if (LineLocked(out var line)) return new ValueTask<string?>(line);
        }
        return ReadLineSlowAsync(cancel);
    }

    private async ValueTask<string?> ReadLineSlowAsync(CancellationToken cancel) {
        while (true) {
            await FillAsync(cancel).ConfigureAwait(false);
            ThrowIfDisposed();
            using (Hold()) {
                if (LineLocked(out var line)) return line;
            }
        }
    }

    public override Task<string?> ReadLineAsync() => ReadLineValueAsync().AsTask();

    public override ValueTask<string?> ReadLineAsync(CancellationToken cancel) => ReadLineValueAsync(cancel);

    // --- Everything that is left --------------------------------------------

    /// Under the lock: the rest of the input once it has all arrived, or null
    /// while more is coming. Everything gathered so far stays in the buffer
    /// until then, so a cancelled `read-all` loses none of it.
    private string? RestLocked() {
        if (!FinishedLocked()) return null;
        var rest = new string(buf, pos, len - pos);
        pos = len;
        return rest;
    }

    public override string ReadToEnd() {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (RestLocked() is { } rest) return rest;
            }
            FillSync();
        }
    }

    public override Task<string> ReadToEndAsync() => ReadToEndAsync(CancellationToken.None);

    public override async Task<string> ReadToEndAsync(CancellationToken cancel) {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (RestLocked() is { } rest) return rest;
            }
            await FillAsync(cancel).ConfigureAwait(false);
        }
    }

    /// Releasing the handle, and nothing else.
    ///
    /// **Close is not the wakeup.** Disposing a stream with a read in flight is
    /// racy across stream types, so a reader parked on this port is woken by the
    /// ambient cancellation token instead — which is what makes `with-deadline`
    /// work on a stalled read. A fill still in flight then fails into the port
    /// or finishes there; nobody is waiting for it.
    protected override void Dispose(bool disposing) {
        if (!disposed) {
            disposed = true;
            if (disposing) inner.Dispose();
        }
        base.Dispose(disposing);
    }

    // --- Dispatchers --------------------------------------------------------
    //
    // What the suspending half of a port operation is imported as. Each tests
    // for a `BjoPort` at runtime the way `writer->string` already tests for a
    // `StringWriter`, so the three kinds of port each get the right answer:
    // `open-input-file` hands back a `BjoPort` and takes the async path;
    // `open-input-string` hands back a `StringReader` and correctly takes the
    // sync one, with no task and no allocation; and a raw `TextReader` from a
    // .NET API still works, and honestly parks.

    // In pairs, and that is the point: a `defbjouble` body names one of these
    // per colour, and the two have to be the same question asked twice. A
    // dispatcher with no twin would be half a leaf.

    public static bool PortEof(TextReader reader) =>
        reader is BjoPort p ? p.Eof() : reader.Peek() == -1;

    public static ValueTask<bool> PortEofAsync(TextReader reader, CancellationToken cancel = default) =>
        reader is BjoPort p ? p.EofAsync(cancel) : new ValueTask<bool>(reader.Peek() == -1);

    /// `null` at end of input, from both halves. `read-line/opt` turns that
    /// into `None`; `read-line` turns it into the exception its docstring
    /// promises.
    public static string? ReadLineOrNull(TextReader reader) => reader.ReadLine();

    public static ValueTask<string?> ReadLineOrNullAsync(TextReader reader, CancellationToken cancel = default) =>
        reader is BjoPort p ? p.ReadLineValueAsync(cancel) : reader.ReadLineAsync(cancel);

    /// One UTF-16 code unit, or -1. Deliberately *not* a scalar: putting a
    /// surrogate pair back together is `read-char`'s job and is done once,
    /// above this, so that both halves of the leaf answer the same shape.
    public static int ReadUnit(TextReader reader) => reader.Read();

    public static ValueTask<int> ReadUnitAsync(TextReader reader, CancellationToken cancel = default) =>
        reader is BjoPort p ? p.ReadValueAsync(cancel) : new ValueTask<int>(reader.Read());

    public static string ReadRest(TextReader reader) => reader.ReadToEnd();

    public static Task<string> ReadRestAsync(TextReader reader, CancellationToken cancel = default) =>
        reader.ReadToEndAsync(cancel);

    /// The message both halves of `read-line` fail with.
    ///
    /// One string, because the two bodies of a `defbjouble` promising different
    /// things at end of input is exactly the drift the form cannot check for.
    private static Exception EndOfLine() =>
        new EndOfStreamException(
            "read-line: the port is at end of input. Guard with (port-eof? p), or use read-line/opt.");

    public static string ReadLineOrThrow(TextReader reader) => reader.ReadLine() ?? throw EndOfLine();

    public static async ValueTask<string> ReadLineOrThrowAsync(TextReader reader, CancellationToken cancel = default) =>
        await ReadLineOrNullAsync(reader, cancel).ConfigureAwait(false) ?? throw EndOfLine();

    // --- Characters ---------------------------------------------------------
    //
    // A Bjolang `char` is a Unicode scalar and a `TextReader` deals in UTF-16
    // code units, so a character above the BMP arrives as two reads and has to
    // be put back together. Written once, here, and shared by both colours:
    // two copies of surrogate arithmetic is two chances to get it wrong, and
    // the bug it produces is half a character rather than an exception.

    private static BjoChar Assemble(int first, int second)
    {
        var unit = (char)first;
        if (!char.IsSurrogate(unit)) return new BjoChar((uint)first);

        if (!char.IsHighSurrogate(unit))
            throw new InvalidOperationException(
                "read-char: the port holds an unpaired low surrogate, which is not a character.");

        if (second < 0 || !char.IsLowSurrogate((char)second))
            throw new InvalidOperationException(
                "read-char: the port holds a high surrogate with no low surrogate after it, which is not a character.");

        return new BjoChar((uint)char.ConvertToUtf32(unit, (char)second));
    }

    /// Whether a second read is owed, without doing it. Splitting this out is
    /// what lets the suspending half await the second unit rather than
    /// blocking for it.
    private static bool NeedsPair(int first) => first >= 0 && char.IsHighSurrogate((char)first);

    private static Exception EndOfChar() =>
        new EndOfStreamException(
            "read-char: the port is at end of input. Guard with (port-eof? p), or use read-char/opt.");

    /// The next character's code units: -1 first at end of input, -1 second
    /// unless the first is a high surrogate. One read on a `BjoPort`, so that
    /// two readers cannot split a pair between them; two on anything else.
    private static (int First, int Second) Units(TextReader reader)
    {
        if (reader is BjoPort p) return p.ReadScalar();

        var first = reader.Read();
        return (first, NeedsPair(first) ? reader.Read() : -1);
    }

    private static async ValueTask<(int First, int Second)> UnitsAsync(TextReader reader, CancellationToken cancel)
    {
        if (reader is BjoPort p) return await p.ReadScalarValueAsync(cancel).ConfigureAwait(false);

        var first = await ReadUnitAsync(reader, cancel).ConfigureAwait(false);
        var second = NeedsPair(first) ? await ReadUnitAsync(reader, cancel).ConfigureAwait(false) : -1;
        return (first, second);
    }

    public static BjoChar ReadCharOrThrow(TextReader reader)
    {
        var (first, second) = Units(reader);
        if (first < 0) throw EndOfChar();
        return Assemble(first, second);
    }

    public static ValueTask<BjoChar> ReadCharOrThrowAsync(TextReader reader, CancellationToken cancel = default)
    {
        // A character already buffered completes here, with no state machine.
        // One that is not is awaited as it stands: asking again would start a
        // second read, and the first would take a character nobody receives.
        var pending = reader is BjoPort p ? p.ReadScalarValueAsync(cancel) : UnitsAsync(reader, cancel);

        if (pending.IsCompletedSuccessfully)
        {
            var (first, second) = pending.Result;
            if (first < 0) throw EndOfChar();
            return new ValueTask<BjoChar>(Assemble(first, second));
        }

        return Awaited(pending);

        static async ValueTask<BjoChar> Awaited(ValueTask<(int First, int Second)> pending)
        {
            var (first, second) = await pending.ConfigureAwait(false);
            if (first < 0) throw EndOfChar();
            return Assemble(first, second);
        }
    }

    // --- The `/opt` reads -----------------------------------------------------
    //
    // One read each, answering `None` at end of input. Not `port-eof?` and
    // then a read: between the two another fiber reading the same port can
    // take the last line, and the read then fails at an end of input the
    // check said was not there.

    private static BjolangRuntime.Option<BjoString.Utf8String> LineOption(string? line) =>
        line is null
            ? BjolangRuntime.None<BjoString.Utf8String>()
            : BjolangRuntime.Some(BjoString.Utf8String.FromUtf16(line));

    public static BjolangRuntime.Option<BjoString.Utf8String> ReadLineOpt(TextReader reader) =>
        LineOption(reader.ReadLine());

    public static ValueTask<BjolangRuntime.Option<BjoString.Utf8String>> ReadLineOptAsync(
        TextReader reader,
        CancellationToken cancel = default)
    {
        var pending = ReadLineOrNullAsync(reader, cancel);
        return pending.IsCompletedSuccessfully
            ? new ValueTask<BjolangRuntime.Option<BjoString.Utf8String>>(LineOption(pending.Result))
            : Awaited(pending);

        static async ValueTask<BjolangRuntime.Option<BjoString.Utf8String>> Awaited(ValueTask<string?> pending) =>
            LineOption(await pending.ConfigureAwait(false));
    }

    public static BjolangRuntime.Option<BjoChar> ReadCharOpt(TextReader reader)
    {
        var (first, second) = Units(reader);
        return first < 0 ? BjolangRuntime.None<BjoChar>() : BjolangRuntime.Some(Assemble(first, second));
    }

    public static ValueTask<BjolangRuntime.Option<BjoChar>> ReadCharOptAsync(
        TextReader reader,
        CancellationToken cancel = default)
    {
        // As `ReadCharOrThrowAsync`: a read not yet complete is awaited, never
        // asked for again.
        var pending = reader is BjoPort p ? p.ReadScalarValueAsync(cancel) : UnitsAsync(reader, cancel);

        if (pending.IsCompletedSuccessfully)
        {
            var (first, second) = pending.Result;
            return new ValueTask<BjolangRuntime.Option<BjoChar>>(
                first < 0 ? BjolangRuntime.None<BjoChar>() : BjolangRuntime.Some(Assemble(first, second)));
        }

        return Awaited(pending);

        static async ValueTask<BjolangRuntime.Option<BjoChar>> Awaited(ValueTask<(int First, int Second)> pending)
        {
            var (first, second) = await pending.ConfigureAwait(false);
            return first < 0 ? BjolangRuntime.None<BjoChar>() : BjolangRuntime.Some(Assemble(first, second));
        }
    }
}

/// A buffered text writer.
///
/// Most writes do no I/O and simply copy text into the buffer. However, a write
/// that fills the buffer will drain it immediately, performing an actual syscall.
/// Because of this, writes must have asynchronous counterparts (suspending twins)
/// just like flushes do, ensuring that draining a full buffer inside a bjoroutine
/// suspends rather than blocking a thread pool thread.
public sealed class BjoWriter : TextWriter {
    private const int DefaultBufferSize = 4096;

    private readonly TextWriter inner;
    private readonly char[] buf;
    private int len;
    private bool disposed;

    /// <summary>See <see cref="BjoPort.Owner"/>.</summary>
    public BjolangRuntime.Owned? Owner;

    public BjoWriter(TextWriter inner) : this(inner, DefaultBufferSize) { }

    /// Settable for the reason `BjoPort`'s is: the interesting case is text
    /// that spans a buffer boundary.
    public BjoWriter(TextWriter inner, int bufferSize) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        this.inner = inner;
        buf = new char[bufferSize];
        // So that `WriteLine` puts out what the wrapped writer would have.
        NewLine = inner.NewLine;
    }

    public static BjoWriter Wrap(TextWriter inner) => inner as BjoWriter ?? new BjoWriter(inner);

    public override Encoding Encoding => inner.Encoding;

    public override IFormatProvider FormatProvider => inner.FormatProvider;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    // Everything below funnels through `Append`. The hazard here is the mirror
    // of the reader's: a path that wrote to `inner` directly would not skip
    // text, it would *reorder* it, arriving ahead of whatever is still held
    // here. `TextWriter`'s own overloads for the primitive types are all
    // defined in terms of `Write(string)` and `Write(char)`, so overriding
    // those four is the whole surface.

    private void Append(ReadOnlySpan<char> text) {
        ThrowIfDisposed();

        while (!text.IsEmpty) {
            if (len == buf.Length) DrainSync();

            int n = Math.Min(text.Length, buf.Length - len);
            text[..n].CopyTo(buf.AsSpan(len));
            len += n;
            text = text[n..];
        }
    }

    private void DrainSync() {
        if (len == 0) return;
        int n = len;
        len = 0;
        inner.Write(buf, 0, n);
    }

    private async ValueTask DrainAsync(CancellationToken cancel) {
        if (len == 0) return;
        int n = len;
        len = 0;
        await inner.WriteAsync(buf.AsMemory(0, n), cancel).ConfigureAwait(false);
    }

    /// Asynchronous counterpart for `write-string` and `write-char`.
    ///
    /// Deliberately avoids the `async` keyword to prevent state machine allocation 
    /// overhead in the common case where the text fits in the buffer. Instead, it 
    /// returns a completed `ValueTask` immediately, only deferring to `SpillAsync` 
    /// when the buffer is full and actually needs to suspend.
    public ValueTask WriteValueAsync(ReadOnlyMemory<char> text, CancellationToken cancel = default) {
        ThrowIfDisposed();

        if (text.Length <= buf.Length - len) {
            text.Span.CopyTo(buf.AsSpan(len));
            len += text.Length;
            return default;
        }

        return SpillAsync(text, cancel);
    }

    public ValueTask WriteValueAsync(string? value, CancellationToken cancel = default) =>
        value is null ? default : WriteValueAsync(value.AsMemory(), cancel);

    /// Asynchronously drains the buffer when it is full.
    ///
    /// Requires `ReadOnlyMemory` instead of `ReadOnlySpan` because a span 
    /// cannot be held across an `await` boundary.
    private async ValueTask SpillAsync(ReadOnlyMemory<char> text, CancellationToken cancel) {
        while (!text.IsEmpty) {
            if (len == buf.Length) await DrainAsync(cancel).ConfigureAwait(false);

            int n = Math.Min(text.Length, buf.Length - len);
            text.Span[..n].CopyTo(buf.AsSpan(len));
            len += n;
            text = text[n..];
        }
    }

    /// Writes text followed by a newline.
    ///
    /// Buffers both sequentially. Like `WriteValueAsync`, this avoids the `async` 
    /// keyword to prevent allocations when both the text and newline fit entirely 
    /// within the remaining buffer space.
    public ValueTask WriteLineValueAsync(string? value, CancellationToken cancel = default) {
        ThrowIfDisposed();

        if ((value?.Length ?? 0) + CoreNewLine.Length <= buf.Length - len) {
            if (value is not null) {
                value.AsSpan().CopyTo(buf.AsSpan(len));
                len += value.Length;
            }

            CoreNewLine.AsSpan().CopyTo(buf.AsSpan(len));
            len += CoreNewLine.Length;
            return default;
        }

        return WriteLineSpillAsync(value, cancel);
    }

    private async ValueTask WriteLineSpillAsync(string? value, CancellationToken cancel) {
        if (value is not null) await WriteValueAsync(value.AsMemory(), cancel).ConfigureAwait(false);
        await WriteValueAsync(CoreNewLine.AsMemory(), cancel).ConfigureAwait(false);
    }

    public override void Write(char value) {
        ThrowIfDisposed();
        if (len == buf.Length) DrainSync();
        buf[len++] = value;
    }

    public override void Write(string? value) {
        if (value is not null) Append(value.AsSpan());
    }

    public override void Write(char[] buffer, int index, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        Append(buffer.AsSpan(index, count));
    }

    public override void Write(ReadOnlySpan<char> buffer) => Append(buffer);

    // We explicitly route these async overrides through `WriteValueAsync`
    // instead of calling their synchronous counterparts to ensure that
    // buffer-full situations suspend asynchronously rather than blocking the
    // thread.

    public override Task WriteAsync(char value) =>
        WriteValueAsync(new[] { value }.AsMemory(), default).AsTask();

    public override Task WriteAsync(string? value) => WriteValueAsync(value).AsTask();

    public override Task WriteAsync(char[] buffer, int index, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        return WriteValueAsync(buffer.AsMemory(index, count), default).AsTask();
    }

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancel = default) =>
        WriteValueAsync(buffer, cancel).AsTask();

    public override Task WriteLineAsync(char value) =>
        WriteLineValueAsync(value.ToString()).AsTask();

    public override Task WriteLineAsync(string? value) => WriteLineValueAsync(value).AsTask();

    public override Task WriteLineAsync(char[] buffer, int index, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        return WriteLineValueAsync(new string(buffer, index, count)).AsTask();
    }

    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancel = default) =>
        WriteLineValueAsync(buffer.ToString(), cancel).AsTask();

    public override void Flush() {
        ThrowIfDisposed();
        DrainSync();
        inner.Flush();
    }

    /// The one operation here with a syscall in it, and so the only one whose
    /// suspending twin is not a formality.
    public async ValueTask FlushValueAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        await DrainAsync(cancel).ConfigureAwait(false);
        await inner.FlushAsync(cancel).ConfigureAwait(false);
    }

    public override Task FlushAsync() => FlushValueAsync().AsTask();

    public override Task FlushAsync(CancellationToken cancel) => FlushValueAsync(cancel).AsTask();

    protected override void Dispose(bool disposing) {
        if (!disposed) {
            // Held text is written before the handle goes, and `disposed` is set
            // only afterwards so that `Flush` below is not refused by its own
            // guard.
            if (disposing) {
                try {
                    DrainSync();
                    inner.Flush();
                } finally {
                    disposed = true;
                    inner.Dispose();
                }
            } else {
                disposed = true;
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync() {
        if (!disposed) {
            try {
                await FlushValueAsync().ConfigureAwait(false);
            } finally {
                disposed = true;
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
        GC.SuppressFinalize(this);
    }

    // --- Dispatchers --------------------------------------------------------

    // `Bjoml.Unit` rather than C# `void`, for the reason `BjolangRuntime.unit`
    // gives: a callback typed `(-> %a %b)` becomes `Func<T_a, T_b>` and no
    // `T_b` can stand for `void`.
    public static Unit FlushPort(TextWriter writer) {
        writer.Flush();
        return default;
    }

    public static async ValueTask<Unit> FlushPortAsync(TextWriter writer, CancellationToken cancel = default) {
        if (writer is BjoWriter w) await w.FlushValueAsync(cancel).ConfigureAwait(false);
        else await writer.FlushAsync(cancel).ConfigureAwait(false);
        return default;
    }

    // Output endpoints for `defbjouble`, providing both synchronous and 
    // asynchronous variants.
    //
    // These methods check if the given `TextWriter` is specifically a `BjoWriter`
    // to utilize its optimized buffering paths. Other writers (like `StringWriter` 
    // or standard .NET `TextWriter`s) fall back to their default `TextWriter` implementations.

    // The asynchronous dispatchers avoid the `async` keyword to prevent state machine 
    // overhead on synchronous completions.
    //
    // Since `Unit` is effectively `default` everywhere, we can safely return `default` 
    // for completed operations without allocating.

    private static async ValueTask<Unit> Awaiting(ValueTask pending) {
        await pending.ConfigureAwait(false);
        return default;
    }

    private static async ValueTask<Unit> Awaiting(Task pending) {
        await pending.ConfigureAwait(false);
        return default;
    }

    /// Consumes the result of a completed `ValueTask`. 
    /// This is necessary to prevent pooled `IValueTaskSource` instances from leaking.
    private static ValueTask<Unit> Settle(ValueTask pending) {
        if (!pending.IsCompletedSuccessfully) return Awaiting(pending);
        pending.GetAwaiter().GetResult();
        return default;
    }

    private static ValueTask<Unit> Settle(Task pending) =>
        pending.IsCompletedSuccessfully ? default : Awaiting(pending);

    public static Unit WritePort(TextWriter writer, string value) {
        writer.Write(value);
        return default;
    }

    // A Bjolang string, written without becoming a .NET string first.
    public static Unit WritePort(TextWriter writer, BjoString.Utf8String value) {
        BjoString.Utf8Text.Write(writer, value);
        return default;
    }

    public static Unit WriteLinePort(TextWriter writer, BjoString.Utf8String value) {
        BjoString.Utf8Text.Write(writer, value);
        writer.WriteLine();
        return default;
    }

    public static ValueTask<Unit> WritePortAsync(
        TextWriter writer,
        string value,
        CancellationToken cancel = default) =>
        writer is BjoWriter w
            ? Settle(w.WriteValueAsync(value, cancel))
            : Settle(writer.WriteAsync(value.AsMemory(), cancel));

    public static Unit WriteLinePort(TextWriter writer, string value) {
        writer.WriteLine(value);
        return default;
    }

    public static ValueTask<Unit> WriteLinePortAsync(
        TextWriter writer,
        string value,
        CancellationToken cancel = default) =>
        writer is BjoWriter w
            ? Settle(w.WriteLineValueAsync(value, cancel))
            : Settle(writer.WriteLineAsync(value.AsMemory(), cancel));

    /// Writes a single Bjolang character to the port.
    /// 
    /// Note: This calls `BjoChar.WriteTo` instead of `Write((char)c)` because 
    /// a Unicode scalar above the Basic Multilingual Plane requires writing 
    /// two UTF-16 code units.
    public static Unit WriteCharPort(TextWriter writer, BjoChar c) {
        c.WriteTo(writer);
        return default;
    }

    public static ValueTask<Unit> WriteCharPortAsync(
        TextWriter writer,
        BjoChar c,
        CancellationToken cancel = default) {
        // We encode to a memory-backed array up front because `EncodeUtf16`
        // requires a `Span`, which cannot be held across the await the spill
        // path has.
        var units = new char[2];
        var text = units.AsMemory(0, c.EncodeUtf16(units));

        return writer is BjoWriter w
            ? Settle(w.WriteValueAsync(text, cancel))
            : Settle(writer.WriteAsync(text, cancel));
    }
}
