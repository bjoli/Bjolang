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
public sealed class BjoPort : TextReader {
    private const int DefaultBufferSize = 4096;

    private readonly TextReader inner;
    private readonly char[] buf;
    private int pos;
    private int len;

    /// The inner reader has answered zero once. Sticky: a stream does not
    /// un-end, and asking again after that costs a syscall for an answer we
    /// have.
    private bool ended;

    private bool disposed;

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

    private bool Buffered => pos < len;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    // --- Filling ------------------------------------------------------------
    //
    // Only ever called with the buffer empty, and `pos`/`len` are assigned only
    // *after* the read returns. That ordering is the whole of how the three
    // endings stay apart: a fill that throws — because the token fired, or
    // because the port was disposed under it — leaves the port exactly as it
    // was, rather than half-reset with a stale `len` that would re-serve
    // characters already handed out.
    //
    // And a cancelled fill must not set `ended`. If it did,
    // `(loop (:break (port-eof? p)) ...)` would end normally on cancellation and
    // return a partial result as though it were the whole thing.

    private int FillSync() {
        if (ended) return 0;

        int n = inner.Read(buf, 0, buf.Length);
        if (n <= 0) {
            ended = true;
            pos = len = 0;
            return 0;
        }

        pos = 0;
        len = n;
        return n;
    }

    private async ValueTask<int> FillAsync(CancellationToken cancel) {
        if (ended) return 0;

        int n = await inner.ReadAsync(buf.AsMemory(), cancel).ConfigureAwait(false);
        if (n <= 0) {
            ended = true;
            pos = len = 0;
            return 0;
        }

        pos = 0;
        len = n;
        return n;
    }

    // --- The eof question ---------------------------------------------------

    /// Whether the port is at end of input.
    ///
    /// No syscall, no allocation and no suspension whenever the buffer holds
    /// anything — which, reading a file a line at a time, is all but one call
    /// in a bufferful.
    public ValueTask<bool> EofAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        if (Buffered) return new ValueTask<bool>(false);
        if (ended) return new ValueTask<bool>(true);
        return FillThenEofAsync(cancel);
    }

    private async ValueTask<bool> FillThenEofAsync(CancellationToken cancel) =>
        await FillAsync(cancel).ConfigureAwait(false) == 0;

    /// The blocking twin, for an ordinary function. This is what `Peek` is
    /// already doing; it is named so that a call site says which question it is
    /// asking.
    public bool Eof() {
        ThrowIfDisposed();
        if (Buffered) return false;
        return FillSync() == 0;
    }

    // --- Character reads ----------------------------------------------------

    public override int Peek() {
        ThrowIfDisposed();
        if (!Buffered && FillSync() == 0) return -1;
        return buf[pos];
    }

    public override int Read() {
        ThrowIfDisposed();
        if (!Buffered && FillSync() == 0) return -1;
        return buf[pos++];
    }

    /// The async single-character read, for `read-char`.
    ///
    /// A code unit rather than a scalar, like `Peek` and `Read`, because that
    /// is what the contract of the type it overrides says. Putting a surrogate
    /// pair back together is `read-char`'s job and is done once, in
    /// `reader-read-char!`.
    public ValueTask<int> ReadValueAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        if (Buffered) return new ValueTask<int>(buf[pos++]);
        if (ended) return new ValueTask<int>(-1);
        return FillThenReadAsync(cancel);
    }

    private async ValueTask<int> FillThenReadAsync(CancellationToken cancel) {
        if (await FillAsync(cancel).ConfigureAwait(false) == 0) return -1;
        return buf[pos++];
    }

    // --- Block reads --------------------------------------------------------

    public override int Read(char[] buffer, int index, int count) {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(index, count));
    }

    public override int Read(Span<char> buffer) {
        ThrowIfDisposed();
        if (buffer.IsEmpty) return 0;
        if (!Buffered && FillSync() == 0) return 0;

        int n = Math.Min(buffer.Length, len - pos);
        buf.AsSpan(pos, n).CopyTo(buffer);
        pos += n;
        return n;
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
        if (Buffered) {
            int n = Math.Min(buffer.Length, len - pos);
            buf.AsSpan(pos, n).CopyTo(buffer.Span);
            pos += n;
            return new ValueTask<int>(n);
        }

        if (ended) return new ValueTask<int>(0);
        return FillThenReadAsync(buffer, cancel);
    }

    private async ValueTask<int> FillThenReadAsync(Memory<char> buffer, CancellationToken cancel) {
        if (await FillAsync(cancel).ConfigureAwait(false) == 0) return 0;

        int n = Math.Min(buffer.Length, len - pos);
        buf.AsSpan(pos, n).CopyTo(buffer.Span);
        pos += n;
        return n;
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
    // eagerly — `StreamReader`'s semantics exactly, because `read-line` is
    // documented against them and a port that disagreed would be a trap. The
    // one cost is that a `\r` landing on the last character of a buffer needs
    // one more read to find out whether an `\n` follows it.

    /// Where the current buffer's next line ends, or -1 if it does not end in it.
    private int IndexOfTerminator() {
        int rel = buf.AsSpan(pos, len - pos).IndexOfAny('\r', '\n');
        return rel < 0 ? -1 : pos + rel;
    }

    private bool StepOverTerminator(int at) {
        bool cr = buf[at] == '\r';
        pos = at + 1;
        if (!cr) return false;
        if (Buffered) {
            if (buf[pos] == '\n') pos++;
            return false;
        }
        // The `\n` may be in the next bufferful, so the caller has to fill.
        return !ended;
    }

    public override string? ReadLine() {
        ThrowIfDisposed();
        if (!Buffered && FillSync() == 0) return null;

        // The common case, and the reason to bother: the whole line is already
        // here, so the only allocation is the string itself.
        int at = IndexOfTerminator();
        if (at >= 0) {
            var line = new string(buf, pos, at - pos);
            if (StepOverTerminator(at)) {
                FillSync();
                if (Buffered && buf[pos] == '\n') pos++;
            }
            return line;
        }

        var sb = new StringBuilder();
        sb.Append(buf, pos, len - pos);
        pos = len;

        while (FillSync() > 0) {
            at = IndexOfTerminator();
            if (at < 0) {
                sb.Append(buf, pos, len - pos);
                pos = len;
                continue;
            }

            sb.Append(buf, pos, at - pos);
            if (StepOverTerminator(at)) {
                FillSync();
                if (Buffered && buf[pos] == '\n') pos++;
            }
            return sb.ToString();
        }

        // Input that ended without a terminator is still a line.
        return sb.ToString();
    }

    /// The suspending twin, and the reason this type exists rather than a
    /// helper beside `TextReader`: the buffer is what lets it complete without
    /// allocating, so it can return `ValueTask<string?>` where
    /// `TextReader.ReadLineAsync` has to return `Task<string?>`.
    ///
    /// `null` at end of input, which `read-line/opt` turns into `None`. There
    /// is no peek in it at all.
    public async ValueTask<string?> ReadLineValueAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        if (!Buffered && await FillAsync(cancel).ConfigureAwait(false) == 0) return null;

        int at = IndexOfTerminator();
        if (at >= 0) {
            var line = new string(buf, pos, at - pos);
            if (StepOverTerminator(at)) {
                await FillAsync(cancel).ConfigureAwait(false);
                if (Buffered && buf[pos] == '\n') pos++;
            }
            return line;
        }

        var sb = new StringBuilder();
        sb.Append(buf, pos, len - pos);
        pos = len;

        while (await FillAsync(cancel).ConfigureAwait(false) > 0) {
            at = IndexOfTerminator();
            if (at < 0) {
                sb.Append(buf, pos, len - pos);
                pos = len;
                continue;
            }

            sb.Append(buf, pos, at - pos);
            if (StepOverTerminator(at)) {
                await FillAsync(cancel).ConfigureAwait(false);
                if (Buffered && buf[pos] == '\n') pos++;
            }
            return sb.ToString();
        }

        return sb.ToString();
    }

    public override Task<string?> ReadLineAsync() => ReadLineValueAsync().AsTask();

    public override ValueTask<string?> ReadLineAsync(CancellationToken cancel) => ReadLineValueAsync(cancel);

    // --- Everything that is left --------------------------------------------

    public override string ReadToEnd() {
        ThrowIfDisposed();

        // The buffer first. `inner.ReadToEnd()` on its own would skip whatever
        // is held here, which is the corruption this class exists to prevent.
        if (!Buffered) {
            if (ended) return "";
            ended = true;
            return inner.ReadToEnd();
        }

        var sb = new StringBuilder(len - pos);
        sb.Append(buf, pos, len - pos);
        pos = len = 0;
        ended = true;
        sb.Append(inner.ReadToEnd());
        return sb.ToString();
    }

    public override Task<string> ReadToEndAsync() => ReadToEndAsync(CancellationToken.None);

    public override async Task<string> ReadToEndAsync(CancellationToken cancel) {
        ThrowIfDisposed();

        var sb = new StringBuilder();
        if (Buffered) sb.Append(buf, pos, len - pos);
        // Emptied before the first await: the buffer is about to be scratch
        // space, and a cancelled read must not leave it looking like content.
        pos = len = 0;

        while (!ended) {
            int n = await inner.ReadAsync(buf.AsMemory(), cancel).ConfigureAwait(false);
            if (n <= 0) {
                ended = true;
                break;
            }
            sb.Append(buf, 0, n);
        }

        return sb.ToString();
    }

    /// Releasing the handle, and nothing else.
    ///
    /// **Close is not the wakeup.** Disposing a stream with a read in flight is
    /// racy across stream types, so a reader parked on this port is woken by the
    /// ambient cancellation token instead — which is what makes `with-deadline`
    /// work on a stalled read.
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

    public static bool PortEof(TextReader reader) =>
        reader is BjoPort p ? p.Eof() : reader.Peek() == -1;

    public static ValueTask<bool> PortEofAsync(TextReader reader, CancellationToken cancel = default) =>
        reader is BjoPort p ? p.EofAsync(cancel) : new ValueTask<bool>(reader.Peek() == -1);

    public static ValueTask<string?> ReadLineOrNullAsync(TextReader reader, CancellationToken cancel = default) =>
        reader is BjoPort p ? p.ReadLineValueAsync(cancel) : reader.ReadLineAsync(cancel);

    public static ValueTask<int> ReadUnitAsync(TextReader reader, CancellationToken cancel = default) =>
        reader is BjoPort p ? p.ReadValueAsync(cancel) : new ValueTask<int>(reader.Read());

    public static Task<string> ReadToEndAsync(TextReader reader, CancellationToken cancel = default) =>
        reader.ReadToEndAsync(cancel);
}

/// The symmetric type, and half the difficulty: a writer has no eof problem.
///
/// What it buys is a flush that suspends and a write that does not. Text goes
/// into memory and stays there, so `write-string` is a copy whatever colour it
/// is called in, and only `flush-port` — where the syscall actually is — has a
/// suspending twin worth having.
public sealed class BjoWriter : TextWriter {
    private const int DefaultBufferSize = 4096;

    private readonly TextWriter inner;
    private readonly char[] buf;
    private int len;
    private bool disposed;

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

    public override Task WriteAsync(char value) {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(string? value) {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(char[] buffer, int index, int count) {
        Write(buffer, index, count);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancel = default) {
        cancel.ThrowIfCancellationRequested();
        Append(buffer.Span);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(char value) {
        WriteLine(value);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(string? value) {
        WriteLine(value);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(char[] buffer, int index, int count) {
        WriteLine(buffer, index, count);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancel = default) {
        cancel.ThrowIfCancellationRequested();
        Append(buffer.Span);
        Append(CoreNewLine);
        return Task.CompletedTask;
    }

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
}
