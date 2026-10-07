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

// A text input port that reads UTF-8 and stays UTF-8.
//
// A Bjolang string is UTF-8 and so is nearly every file. A `StreamReader` in
// between decodes the bytes to UTF-16, the line becomes a `System.String`, and
// the string is encoded back to UTF-8 for the program: two transcodes and a
// throwaway string per line, all to arrive where the bytes started. This port
// keeps the bytes. A line is found by searching the bytes for `\r` and `\n`
// (neither byte can occur inside a multi-byte sequence), checked to be valid
// UTF-8, and copied once into the `Utf8String` the program gets.
//
// Everything else is `BjoPort`'s, guarantee for guarantee, and the code has the
// same shape so that the two can be read side by side: a buffer the port owns,
// one fill in flight that belongs to the port rather than to a reader, a spin
// lock over the cursor, sticky end and failure, and a read that takes nothing
// until all of it is in the buffer. See the comments in `BjoPort.cs` for why
// each of those is the way it is; they are not repeated here.
//
// What differs is the unit. The buffer holds bytes, so the "never split"
// promise is about a multi-byte sequence rather than a surrogate pair: a
// character whose bytes straddle two fills waits for the second.
//
// Invalid input is replaced, never refused: each maximal invalid subsequence
// becomes one U+FFFD, which is what `StreamReader` with `Encoding.UTF8` does,
// so moving a port onto this class changes no program's output. A leading
// UTF-8 byte order mark is skipped, as `StreamReader` skips it. A UTF-16 or
// UTF-32 one is where the two part: `StreamReader` switches encoding on it,
// and this port raises, because it reads UTF-8 and nothing else. A file in
// another encoding is read through a byte port and `byte->text-input-port`,
// which is where a program says what the encoding is.
//
// It is still a `TextReader`, for .NET callers and for `(.Peek p)`. Those
// reads decode to UTF-16 as they go; a character above the BMP is handed out
// as its high surrogate, and the low one is kept in `pendingLow` and served
// first by whichever read comes next.

using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Unicode;
using BjoString;

namespace Bjolang.Runtime;

/// <summary>
/// A buffered UTF-8 <see cref="TextReader" /> over a <see cref="Stream"/>,
/// whose Bjolang-facing reads answer <see cref="Utf8String"/> without passing
/// through UTF-16.
///
/// Every virtual read is overridden, as in <see cref="BjoPort"/> and for its
/// reason: an inherited read that reached <c>inner</c> past the buffer would
/// skip what the buffer holds.
/// </summary>
public sealed class BjoUtf8Port : TextReader {
    private const int DefaultBufferSize = 16384;

    private readonly Stream inner;

    /// The lock over the cursor: 1 while held. See <c>BjoPort.held</c>.
    private int held;

    private readonly ref struct Held {
        private readonly BjoUtf8Port port;
        public Held(BjoUtf8Port port) => this.port = port;
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

    // The buffer and the window of it that holds unread bytes. As in
    // `BjoPort`: `buf` and `len` move only while the one fill is prepared or
    // committed, and readers only move `pos`, forwards, never past `len`.
    private byte[] buf;
    private int pos;
    private int len;

    /// The stream has answered zero once. Sticky.
    private bool ended;

    /// The stream failed once. Sticky, thrown with its original stack.
    private ExceptionDispatchInfo? failure;

    /// The fill in flight, or null.
    private Task? fill;

    private bool disposed;

    /// Whether the start of the input has been looked at for a byte order
    /// mark. Until it has, no read can say what the first character is.
    private bool bomDone;

    /// The low surrogate of a character above the BMP whose high surrogate a
    /// UTF-16 read handed out, or -1. Only the <see cref="TextReader"/> reads
    /// ever set it; every read serves it before anything in the buffer.
    private int pendingLow = -1;

    /// <summary>See <see cref="BjoPort.Owner"/>.</summary>
    public BjolangRuntime.Owned? Owner;

    public BjoUtf8Port(Stream inner) : this(inner, DefaultBufferSize) { }

    /// Settable so that a test can put a buffer boundary inside a multi-byte
    /// character, a `\r\n` or a byte order mark.
    public BjoUtf8Port(Stream inner, int bufferSize) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 2);
        this.inner = inner;
        buf = new byte[bufferSize];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    // --- Filling ------------------------------------------------------------

    /// Room after <c>len</c> for the next fill: compacting, or growing when
    /// what is held does not leave half the buffer free. See
    /// <c>BjoPort.MakeRoomLocked</c>.
    private void MakeRoomLocked() {
        int have = len - pos;

        if (have == 0) {
            pos = len = 0;
            return;
        }

        if (buf.Length - len >= buf.Length / 2) return;

        if (pos > 0) {
            Array.Copy(buf, pos, buf, 0, have);
            pos = 0;
            len = have;
            if (buf.Length - len >= buf.Length / 2) return;
        }

        var bigger = new byte[buf.Length * 2];
        Array.Copy(buf, pos, bigger, 0, have);
        buf = bigger;
        pos = 0;
        len = have;
    }

    /// Under the lock: the fill to wait for, or a fresh one for this caller to
    /// perform. Null and no start when no more input will ever come.
    private Task? FillLocked(out (TaskCompletionSource Done, byte[] Into, int At, int Room)? start) {
        start = null;
        if (fill is not null) return fill;
        if (disposed || ended || failure is not null) return null;

        MakeRoomLocked();

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fill = done.Task;
        start = (done, buf, len, buf.Length - len);
        return done.Task;
    }

    private void Commit(TaskCompletionSource done, int at, int n, Exception? error) {
        using (Hold()) {
            if (error is not null) failure ??= ExceptionDispatchInfo.Capture(error);
            else if (n <= 0) ended = true;
            else len = at + n;
            fill = null;
        }

        done.TrySetResult();
    }

    private void FillSync() {
        Task? pending;
        (TaskCompletionSource Done, byte[] Into, int At, int Room)? start;

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

    private ValueTask FillAsync(CancellationToken cancel) {
        Task? pending;
        (TaskCompletionSource Done, byte[] Into, int At, int Room)? start;

        using (Hold()) pending = FillLocked(out start);

        if (start is { } s) _ = Pump(s.Done, s.Into, s.At, s.Room);

        if (pending is null || pending.IsCompleted) return default;
        return new ValueTask(cancel.CanBeCanceled ? pending.WaitAsync(cancel) : pending);
    }

    private async Task Pump(TaskCompletionSource done, byte[] into, int at, int room) {
        int n = 0;
        Exception? error = null;
        try { n = await inner.ReadAsync(into.AsMemory(at, room), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) { error = e; }
        Commit(done, at, n, error);
    }

    /// Under the lock: no more bytes will arrive, whether because the input
    /// ended or because it failed. Never throws, so a read part way through
    /// gathering can ask it without losing what it has gathered.
    private bool Over => ended || failure is not null;

    /// Under the lock: nothing more is coming. Throws the sticky failure, and
    /// is asked only where nothing has been taken yet.
    private bool FinishedLocked() {
        if (ended) return true;
        failure?.Throw();
        return false;
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// The byte order marks of the encodings this port does not read, longest
    /// first where one begins with another: UTF-32's little-endian mark starts
    /// with UTF-16's.
    private static readonly (byte[] Mark, string Name)[] ForeignBoms = [
        ([0xFF, 0xFE, 0x00, 0x00], "UTF-32 (little-endian)"),
        ([0x00, 0x00, 0xFE, 0xFF], "UTF-32 (big-endian)"),
        ([0xFF, 0xFE], "UTF-16 (little-endian)"),
        ([0xFE, 0xFF], "UTF-16 (big-endian)"),
    ];

    /// Whether more bytes could still turn what is here into a byte order mark.
    private static bool CouldBecomeBom(ReadOnlySpan<byte> have) {
        if (have.Length < Utf8Bom.Length && Utf8Bom.StartsWith(have)) return true;
        foreach (var (mark, _) in ForeignBoms) {
            if (have.Length < mark.Length && mark.AsSpan().StartsWith(have)) return true;
        }
        return false;
    }

    /// <summary>
    /// Under the lock: false while the first bytes might still be a byte order
    /// mark, which only a fill can settle. Skips a UTF-8 one, once.
    ///
    /// A UTF-16 or UTF-32 mark is refused, on this read and every one after:
    /// read as UTF-8, such a file is replacement characters and NULs from
    /// start to end, and a port that handed those out as its text would be
    /// failing silently. The message says how to read the file instead.
    /// </summary>
    private bool BomLocked() {
        if (bomDone) return true;

        var have = buf.AsSpan(pos, len - pos);
        if (!Over && CouldBecomeBom(have)) return false;

        if (have.StartsWith(Utf8Bom)) {
            pos += Utf8Bom.Length;
        } else {
            foreach (var (mark, name) in ForeignBoms) {
                if (have.StartsWith(mark)) throw ForeignText(name);
            }
        }

        bomDone = true;
        return true;
    }

    private static Exception ForeignText(string encoding) =>
        new InvalidDataException(
            $"the input starts with a {encoding} byte order mark, and a text port reads UTF-8. "
            + "Open it with open-byte-input-file and read it through "
            + "(byte->text-input-port p encoding); bom-encoding in (std ports) names the encoding.");

    // --- Text out of the buffer -----------------------------------------------

    private static readonly byte[] Replacement = [0xEF, 0xBF, 0xBD];

    /// The bytes as a Bjolang string: copied once when valid, which is nearly
    /// always, and with each invalid sequence replaced by U+FFFD otherwise.
    private static Utf8String Text(ReadOnlySpan<byte> bytes) => Utf8String.FromUtf8Lossy(bytes);

    /// Under the lock: the same, preceded by a pending low surrogate if there
    /// is one, which on its own is not a character and so reads as U+FFFD — as
    /// it does when a `BjoPort` line starting with one becomes a Bjolang
    /// string.
    private Utf8String TextAfterPendingLocked(ReadOnlySpan<byte> bytes) {
        if (pendingLow < 0) return Text(bytes);
        pendingLow = -1;
        var joined = new byte[Replacement.Length + bytes.Length];
        Replacement.CopyTo(joined, 0);
        bytes.CopyTo(joined.AsSpan(Replacement.Length));
        return Text(joined);
    }

    /// Under the lock: the bytes as a .NET string, after a pending low
    /// surrogate if there is one.
    private string StringAfterPendingLocked(ReadOnlySpan<byte> bytes) {
        var text = Encoding.UTF8.GetString(bytes);
        if (pendingLow < 0) return text;
        var low = (char)pendingLow;
        pendingLow = -1;
        return string.Concat(new ReadOnlySpan<char>(in low), text);
    }

    // --- The eof question ---------------------------------------------------

    private bool? EofLocked() {
        if (pendingLow >= 0) return false;
        if (!BomLocked()) return null;
        if (pos < len) return false;
        if (FinishedLocked()) return true;
        return null;
    }

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

    public bool Eof() {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (EofLocked() is { } known) return known;
            }
            FillSync();
        }
    }

    // --- Characters -----------------------------------------------------------

    /// <summary>
    /// Under the lock: the character at the cursor and how many bytes it
    /// takes, without moving. False when a fill has to come first — a
    /// multi-byte character is only decoded once all of its bytes are here.
    /// <paramref name="scalar"/> is -1 when no more input will come; the
    /// caller asks <see cref="FinishedLocked"/> whether that is the end or a
    /// failure.
    ///
    /// An invalid sequence decodes to U+FFFD and takes the bytes of its
    /// maximal invalid prefix, as <see cref="Rune.DecodeFromUtf8"/> and
    /// <see cref="Encoding.UTF8"/> agree it should. So does a sequence cut off
    /// by the end of the input.
    /// </summary>
    private bool PeekScalarLocked(out int scalar, out int size) {
        scalar = -1;
        size = 0;
        if (!BomLocked()) return false;

        if (pos < len) {
            byte b = buf[pos];
            if (b < 0x80) {
                scalar = b;
                size = 1;
                return true;
            }

            var status = Rune.DecodeFromUtf8(buf.AsSpan(pos, len - pos), out var rune, out size);
            if (status == OperationStatus.NeedMoreData) {
                if (!Over) return false;

                // Cut short by a failure rather than by the end: the rest of
                // the character was lost, which is the failure's to report.
                if (failure is not null) {
                    size = 0;
                    return true;
                }
            }

            scalar = rune.Value;
            return true;
        }

        return Over;
    }

    private static Exception UnpairedLow() =>
        new InvalidOperationException(
            "read-char: the port holds an unpaired low surrogate, which is not a character.");

    /// Under the lock: the next character, taken whole, or -1 at end of input.
    /// False when a fill has to come first.
    private bool ScalarLocked(out int scalar) {
        if (pendingLow >= 0) {
            // Half a character a UTF-16 read left behind. Taken, so that the
            // port moves on past it, and reported, as `BjoPort` reports it.
            pendingLow = -1;
            throw UnpairedLow();
        }

        if (!PeekScalarLocked(out scalar, out int size)) return false;
        if (scalar < 0) FinishedLocked();
        else pos += size;
        return true;
    }

    /// One character as a Unicode scalar, or -1 at end of input.
    public int ReadScalar() {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (ScalarLocked(out int scalar)) return scalar;
            }
            FillSync();
        }
    }

    public ValueTask<int> ReadScalarValueAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        using (Hold()) {
            if (ScalarLocked(out int scalar)) return new ValueTask<int>(scalar);
        }
        return ReadScalarSlowAsync(cancel);
    }

    private async ValueTask<int> ReadScalarSlowAsync(CancellationToken cancel) {
        while (true) {
            await FillAsync(cancel).ConfigureAwait(false);
            ThrowIfDisposed();
            using (Hold()) {
                if (ScalarLocked(out int scalar)) return scalar;
            }
        }
    }

    // --- UTF-16 code units, for the TextReader contract -------------------------

    /// Under the lock: the next code unit or -1, or null when a fill has to
    /// say. Moves only when <paramref name="take"/>.
    private int? UnitLocked(bool take) {
        if (pendingLow >= 0) {
            int low = pendingLow;
            if (take) pendingLow = -1;
            return low;
        }

        if (!PeekScalarLocked(out int scalar, out int size)) return null;

        if (scalar < 0) {
            FinishedLocked();
            return -1;
        }

        if (scalar <= 0xFFFF) {
            if (take) pos += size;
            return scalar;
        }

        var rune = new Rune(scalar);
        Span<char> pair = stackalloc char[2];
        rune.EncodeToUtf16(pair);
        if (take) {
            pos += size;
            pendingLow = pair[1];
        }
        return pair[0];
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

    /// The suspending twin of <see cref="Read()"/>, for a reader that works in
    /// code units (the JSON and bjodat readers do).
    public ValueTask<int> ReadUnitValueAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        using (Hold()) {
            if (UnitLocked(take: true) is { } unit) return new ValueTask<int>(unit);
        }
        return ReadUnitSlowAsync(cancel);
    }

    private async ValueTask<int> ReadUnitSlowAsync(CancellationToken cancel) {
        while (true) {
            await FillAsync(cancel).ConfigureAwait(false);
            ThrowIfDisposed();
            using (Hold()) {
                if (UnitLocked(take: true) is { } unit) return unit;
            }
        }
    }

    /// <summary>
    /// Under the lock: as many code units as fit, 0 at end of input, or -1
    /// when a fill has to come first. Stops early rather than wait, at a
    /// character whose bytes are not all here yet.
    /// </summary>
    private int BlockLocked(Span<char> into) {
        int n = 0;

        if (pendingLow >= 0) {
            into[n++] = (char)pendingLow;
            pendingLow = -1;
        }

        Span<char> pair = stackalloc char[2];
        while (n < into.Length) {
            if (!PeekScalarLocked(out int scalar, out int size) || scalar < 0) break;
            pos += size;

            if (scalar <= 0xFFFF) {
                into[n++] = (char)scalar;
                continue;
            }

            new Rune(scalar).EncodeToUtf16(pair);
            into[n++] = pair[0];
            if (n < into.Length) into[n++] = pair[1];
            else pendingLow = pair[1];
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
    // `\n`, `\r` and `\r\n` end a line, as in `BjoPort` and `StreamReader`, and
    // a `\r` that is the last byte buffered waits for one more fill to learn
    // whether an `\n` follows it.

    /// <summary>
    /// Under the lock: where the next line's bytes are, taken whole: true with
    /// <paramref name="some"/> and the line's bounds, true without at end of
    /// input, or false when a fill has to come first — in which case nothing
    /// has moved. The bounds stay valid only while the lock is held.
    /// </summary>
    private bool LineBoundsLocked(out int start, out int count, out bool some) {
        start = 0;
        count = 0;
        some = false;
        if (!BomLocked()) return false;

        int rel = buf.AsSpan(pos, len - pos).IndexOfAny((byte)'\r', (byte)'\n');
        if (rel >= 0) {
            int at = pos + rel;
            bool cr = buf[at] == (byte)'\r';

            if (cr && at + 1 == len && !Over) return false;

            start = pos;
            count = rel;
            some = true;
            pos = at + 1;
            if (cr && pos < len && buf[pos] == (byte)'\n') pos++;
            return true;
        }

        // Input that failed part way through a line has lost the rest of it,
        // so the failure is what this read reports, not the fragment.
        if (!FinishedLocked()) return false;

        // Input that ended without a terminator is still a line; and a pending
        // low surrogate is a line of its own if nothing follows it.
        if (pos < len || pendingLow >= 0) {
            start = pos;
            count = len - pos;
            some = true;
            pos = len;
        }

        return true;
    }

    private bool LineLocked(out BjolangRuntime.Option<Utf8String> line) {
        line = default;
        if (!LineBoundsLocked(out int start, out int count, out bool some)) return false;
        if (some) line = BjolangRuntime.Some(TextAfterPendingLocked(buf.AsSpan(start, count)));
        return true;
    }

    /// The next line, or `None` at end of input.
    public BjolangRuntime.Option<Utf8String> ReadLineUtf8() {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (LineLocked(out var line)) return line;
            }
            FillSync();
        }
    }

    public ValueTask<BjolangRuntime.Option<Utf8String>> ReadLineUtf8ValueAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        using (Hold()) {
            if (LineLocked(out var line)) return new ValueTask<BjolangRuntime.Option<Utf8String>>(line);
        }
        return ReadLineUtf8SlowAsync(cancel);
    }

    private async ValueTask<BjolangRuntime.Option<Utf8String>> ReadLineUtf8SlowAsync(CancellationToken cancel) {
        while (true) {
            await FillAsync(cancel).ConfigureAwait(false);
            ThrowIfDisposed();
            using (Hold()) {
                if (LineLocked(out var line)) return line;
            }
        }
    }

    private bool LineStringLocked(out string? line) {
        line = null;
        if (!LineBoundsLocked(out int start, out int count, out bool some)) return false;
        if (some) line = StringAfterPendingLocked(buf.AsSpan(start, count));
        return true;
    }

    public override string? ReadLine() {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (LineStringLocked(out var line)) return line;
            }
            FillSync();
        }
    }

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancel) {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (LineStringLocked(out var line)) return line;
            }
            await FillAsync(cancel).ConfigureAwait(false);
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
        BomLocked();
        start = pos;
        count = len - pos;
        pos = len;
        return true;
    }

    private bool RestLocked(out Utf8String rest) {
        rest = default;
        if (!RestBoundsLocked(out int start, out int count)) return false;
        rest = TextAfterPendingLocked(buf.AsSpan(start, count));
        return true;
    }

    /// Everything left, as a Bjolang string.
    public Utf8String ReadToEndUtf8() {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (RestLocked(out var rest)) return rest;
            }
            FillSync();
        }
    }

    public async ValueTask<Utf8String> ReadToEndUtf8Async(CancellationToken cancel = default) {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (RestLocked(out var rest)) return rest;
            }
            await FillAsync(cancel).ConfigureAwait(false);
        }
    }

    private bool RestStringLocked(out string? rest) {
        rest = null;
        if (!RestBoundsLocked(out int start, out int count)) return false;
        rest = StringAfterPendingLocked(buf.AsSpan(start, count));
        return true;
    }

    public override string ReadToEnd() {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (RestStringLocked(out var rest)) return rest!;
            }
            FillSync();
        }
    }

    public override Task<string> ReadToEndAsync() => ReadToEndAsync(CancellationToken.None);

    public override async Task<string> ReadToEndAsync(CancellationToken cancel) {
        while (true) {
            ThrowIfDisposed();
            using (Hold()) {
                if (RestStringLocked(out var rest)) return rest!;
            }
            await FillAsync(cancel).ConfigureAwait(false);
        }
    }

    /// Releasing the stream, and nothing else. A reader parked on the port is
    /// woken by the ambient cancellation token, not by this; see
    /// <c>BjoPort.Dispose</c>.
    protected override void Dispose(bool disposing) {
        if (!disposed) {
            disposed = true;
            if (disposing) inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
