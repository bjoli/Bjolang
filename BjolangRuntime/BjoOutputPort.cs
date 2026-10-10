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

// The output port: bytes, and text written as UTF-8 bytes, as a Racket port
// is. The mirror of `BjoInputPort`.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BjoString;

namespace Bjolang.Runtime;

/// <summary>
/// When a port's buffer goes out on its own, as Racket's
/// `file-stream-buffer-mode` says it.
/// </summary>
public enum BufferMode {
    /// After every write.
    None = 0,
    /// After every write that holds a newline, and when the buffer is full.
    Line = 1,
    /// When the buffer is full, and on a flush or a close.
    Block = 2,
}

/// <summary>
/// An output port: a buffered byte sink that text is written to as UTF-8.
///
/// A `string` is UTF-8 already, so writing one is a copy of its bytes into the
/// buffer; `write-char` encodes one scalar; `write-bytes!` copies the bytes as
/// they are. All three mix on one port, in any order. The port is also a
/// <see cref="TextWriter"/>, so a .NET API that takes one takes a port: its
/// UTF-16 writes are encoded as UTF-8 on the way in, a surrogate pair split
/// across two writes included.
///
/// A port with no stream underneath is a string port: the buffer grows rather
/// than draining, and <see cref="GetBytes"/> and <see cref="GetString"/> read
/// what was written.
///
/// # Threads
///
/// Standard output is written by fibers on any thread, so every write holds
/// the port's gate for as long as it takes: a line written by one fiber is
/// never interleaved with another's. The gate is a <see cref="BjoGate"/>
/// rather than a lock, because a write that has to drain the buffer may
/// suspend.
///
/// # Closing
///
/// A close writes only what is pending: bytes in the buffer, or bytes the
/// stream has been given since its last flush. Some streams refuse blocking
/// I/O (Kestrel's do), and a close is blocking, because a scope's release
/// cannot suspend. After a flush a close does no I/O at all, and
/// <see cref="DisposeAsync"/> is the close that suspends instead.
/// </summary>
public sealed class BjoOutputPort : TextWriter {
    /// The buffer of a port over a file or a stream.
    public const int DefaultBufferSize = 16 * 1024;

    /// The buffer of a connection half or a pipe.
    public const int SmallBufferSize = 4096;

    private static readonly UTF8Encoding Utf8NoMark = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly ReadOnlyMemory<byte> Newline = new byte[] { (byte)'\n' };

    private readonly Stream? _inner;
    private readonly bool _ownsInner;
    private byte[] _buf;
    private int _len;
    private bool _disposed;
    private bool _writeClosed;

    /// Bytes have reached `_inner` since it was last flushed. Set before the
    /// write rather than after, because a write that fails partway may already
    /// have handed some of them over.
    private bool _unflushed;

    /// Held for the whole of every operation; see the class doc.
    private readonly BjoGate _gate = new();

    /// For the UTF-16 a .NET caller writes: keeps a high surrogate that arrived
    /// without its low one until the next write.
    private Encoder? _utf16;

    /// <summary>See <see cref="BjoInputPort.Owner"/>.</summary>
    public BjolangRuntime.Owned? Owner;

    /// See <see cref="BjoInputPort"/>'s field of the same name.
    private readonly BjoConnection? _connection;

    /// <summary>When the buffer goes out on its own. Block unless said otherwise.</summary>
    public BufferMode Mode { get; set; } = BufferMode.Block;

    /// <summary>A port over a stream it owns.</summary>
    public BjoOutputPort(Stream inner) : this(inner, DefaultBufferSize, true, null) { }

    public BjoOutputPort(Stream inner, bool ownsInner) : this(inner, DefaultBufferSize, ownsInner, null) { }

    public BjoOutputPort(Stream inner, int bufferSize, bool ownsInner = true)
        : this(inner, bufferSize, ownsInner, null) { }

    /// The writing half of a connection. See <see cref="BjoConnection"/>.
    internal BjoOutputPort(BjoConnection connection)
        : this(connection.Stream, SmallBufferSize, false, connection) { }

    /// <summary>A string port: written to memory, read back with <see cref="GetString"/>.</summary>
    public BjoOutputPort() : base(System.Globalization.CultureInfo.InvariantCulture) {
        _inner = null;
        _ownsInner = false;
        _buf = new byte[256];
        CoreNewLine = ['\n'];
    }

    private BjoOutputPort(Stream inner, int bufferSize, bool ownsInner, BjoConnection? connection)
        : base(System.Globalization.CultureInfo.InvariantCulture) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        if (!inner.CanWrite)
            throw new ArgumentException("an output port needs a writable stream.", nameof(inner));

        _inner = inner;
        _ownsInner = ownsInner;
        _connection = connection;
        _buf = new byte[bufferSize];
        CoreNewLine = ['\n'];
    }

    /// <summary>UTF-8, without a byte order mark: what every text write is encoded as.</summary>
    public override Encoding Encoding => Utf8NoMark;

    /// <summary>See <see cref="BjoInputPort.Peer"/>.</summary>
    public string? Peer => _connection?.Peer;

    /// <summary>Whether this is a string port.</summary>
    public bool IsMemory => _inner is null;

    /// The stream underneath, for the runtime's own checks. Null for a string port.
    internal Stream? Inner => _inner;

    private bool HasPending => _len > 0 || _unflushed;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfShutDown() {
        if (_writeClosed)
            throw new InvalidOperationException(
                "write to an output port whose write half was ended by shutdown!.");
    }

    private void Enter() => _gate.Enter();

    private ValueTask EnterAsync(CancellationToken cancel) {
        return _gate.EnterAsync(cancel);
    }

    private void Leave() => _gate.Leave();

    // --- The buffer, under the gate -------------------------------------------

    /// Bytes into the buffer, draining it as it fills. A write larger than the
    /// buffer goes to the stream directly after what was already held.
    private void PutLocked(ReadOnlySpan<byte> bytes) {
        if (_inner is null) {
            GrowLocked(bytes.Length);
            bytes.CopyTo(_buf.AsSpan(_len));
            _len += bytes.Length;
            return;
        }

        if (bytes.Length > _buf.Length - _len) {
            DrainLocked();
            if (bytes.Length >= _buf.Length) {
                _unflushed = true;
                _inner.Write(bytes);
                return;
            }
        }
        bytes.CopyTo(_buf.AsSpan(_len));
        _len += bytes.Length;
    }

    private async ValueTask PutLockedAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancel) {
        if (_inner is null) {
            PutLocked(bytes.Span);
            return;
        }

        if (bytes.Length > _buf.Length - _len) {
            await DrainLockedAsync(cancel).ConfigureAwait(false);
            if (bytes.Length >= _buf.Length) {
                _unflushed = true;
                await _inner.WriteAsync(bytes, cancel).ConfigureAwait(false);
                return;
            }
        }
        bytes.Span.CopyTo(_buf.AsSpan(_len));
        _len += bytes.Length;
    }

    /// Whether `bytes` fit in the buffer as it is: the write that needs no I/O.
    private bool FitsLocked(int count) => _inner is null || count <= _buf.Length - _len;

    private void GrowLocked(int more) {
        if (_buf.Length - _len >= more) return;
        int size = Math.Max(_buf.Length * 2, _len + more);
        Array.Resize(ref _buf, size);
    }

    private void DrainLocked() {
        if (_len == 0 || _inner is null) return;
        int n = _len;
        _len = 0;
        _unflushed = true;
        _inner.Write(_buf, 0, n);
    }

    private async ValueTask DrainLockedAsync(CancellationToken cancel) {
        if (_len == 0 || _inner is null) return;
        int n = _len;
        _len = 0;
        _unflushed = true;
        await _inner.WriteAsync(_buf.AsMemory(0, n), cancel).ConfigureAwait(false);
    }

    private void FlushLocked() {
        if (_inner is null) return;
        DrainLocked();
        _inner.Flush();
        _unflushed = false;
    }

    private async ValueTask FlushLockedAsync(CancellationToken cancel) {
        if (_inner is null) return;
        await DrainLockedAsync(cancel).ConfigureAwait(false);
        await _inner.FlushAsync(cancel).ConfigureAwait(false);
        _unflushed = false;
    }

    /// Whether what was just written has to go out now, by the buffer mode.
    private bool MustFlush(ReadOnlySpan<byte> written) =>
        _inner is not null && Mode switch {
            BufferMode.None => true,
            BufferMode.Line => written.IndexOf((byte)'\n') >= 0,
            _ => false,
        };

    /// A high surrogate held from a .NET write, written out as U+FFFD before
    /// anything else is: bytes cannot be put between the halves of a pair.
    private void SettleSurrogateLocked() {
        if (_utf16 is null) return;
        Span<byte> tail = stackalloc byte[8];
        _utf16.Convert(ReadOnlySpan<char>.Empty, tail, flush: true, out _, out int n, out _);
        if (n > 0) PutLocked(tail[..n]);
    }

    // --- Writing --------------------------------------------------------------

    /// <summary>`write-bytes!`.</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes) {
        Enter();
        try {
            ThrowIfDisposed();
            ThrowIfShutDown();
            SettleSurrogateLocked();
            PutLocked(bytes);
            if (MustFlush(bytes)) FlushLocked();
        } finally {
            Leave();
        }
    }

    /// <summary>
    /// The suspending twin. A write that fits in the buffer and need not go out
    /// costs no state machine.
    /// </summary>
    public ValueTask WriteBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancel = default) {
        if (_gate.TryEnter()) {
            bool handedOff = false;
            try {
                ThrowIfDisposed();
                ThrowIfShutDown();
                SettleSurrogateLocked();
                if (FitsLocked(bytes.Length) && !MustFlush(bytes.Span)) {
                    PutLocked(bytes.Span);
                    return default;
                }
                handedOff = true;
                return HeldWriteAsync(bytes, cancel);
            } finally {
                if (!handedOff) Leave();
            }
        }
        return WaitedWriteAsync(bytes, cancel);
    }

    private async ValueTask HeldWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancel) {
        try {
            await PutLockedAsync(bytes, cancel).ConfigureAwait(false);
            if (MustFlush(bytes.Span)) await FlushLockedAsync(cancel).ConfigureAwait(false);
        } finally {
            Leave();
        }
    }

    private async ValueTask WaitedWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancel) {
        await _gate.EnterAsync(cancel).ConfigureAwait(false);
        try {
            ThrowIfDisposed();
            ThrowIfShutDown();
            SettleSurrogateLocked();
            await PutLockedAsync(bytes, cancel).ConfigureAwait(false);
            if (MustFlush(bytes.Span)) await FlushLockedAsync(cancel).ConfigureAwait(false);
        } finally {
            Leave();
        }
    }

    /// <summary>`write-string`: the string's own bytes.</summary>
    public void WriteText(Utf8String s) => WriteBytes(s.AsSpan());

    public ValueTask WriteTextAsync(Utf8String s, CancellationToken cancel = default) =>
        WriteBytesAsync(s.AsMemory(), cancel);

    /// <summary>`writeln`: the string and a newline, as one write.</summary>
    public void WriteTextLine(Utf8String s) {
        var bytes = s.AsSpan();
        Enter();
        try {
            ThrowIfDisposed();
            ThrowIfShutDown();
            SettleSurrogateLocked();
            PutLocked(bytes);
            PutLocked("\n"u8);
            if (MustFlush("\n"u8)) FlushLocked();
        } finally {
            Leave();
        }
    }

    public async ValueTask WriteTextLineAsync(Utf8String s, CancellationToken cancel = default) {
        await EnterAsync(cancel).ConfigureAwait(false);
        try {
            ThrowIfDisposed();
            ThrowIfShutDown();
            SettleSurrogateLocked();
            await PutLockedAsync(s.AsMemory(), cancel).ConfigureAwait(false);
            await PutLockedAsync(Newline, cancel).ConfigureAwait(false);
            if (MustFlush(Newline.Span)) await FlushLockedAsync(cancel).ConfigureAwait(false);
        } finally {
            Leave();
        }
    }

    /// <summary>`write-char`: one scalar, encoded.</summary>
    public void WriteScalar(BjoChar c) {
        Span<byte> bytes = stackalloc byte[4];
        int n = c.ToRune().EncodeToUtf8(bytes);
        WriteBytes(bytes[..n]);
    }

    public ValueTask WriteScalarAsync(BjoChar c, CancellationToken cancel = default) {
        var bytes = new byte[4];
        int n = c.ToRune().EncodeToUtf8(bytes);
        return WriteBytesAsync(bytes.AsMemory(0, n), cancel);
    }

    // --- UTF-16, for .NET callers ---------------------------------------------

    /// UTF-16 encoded into the buffer, a piece at a time so that no array is
    /// made for it. A lone surrogate becomes U+FFFD, as it does when a .NET
    /// string becomes a Bjolang one.
    private void PutCharsLocked(ReadOnlySpan<char> chars) {
        _utf16 ??= Utf8NoMark.GetEncoder();
        Span<byte> piece = stackalloc byte[1024];
        while (!chars.IsEmpty) {
            int take = Math.Min(chars.Length, 256);
            _utf16.Convert(chars[..take], piece, flush: false, out int used, out int n, out _);
            PutLocked(piece[..n]);
            chars = chars[used..];
        }
    }

    private void WriteChars(ReadOnlySpan<char> chars) {
        Enter();
        try {
            ThrowIfDisposed();
            ThrowIfShutDown();
            PutCharsLocked(chars);
            if (_inner is not null
                && (Mode == BufferMode.None || (Mode == BufferMode.Line && chars.IndexOf('\n') >= 0)))
                FlushLocked();
        } finally {
            Leave();
        }
    }

    public override void Write(char value) => WriteChars([value]);

    public override void Write(char[] buffer, int index, int count) =>
        WriteChars(buffer.AsSpan(index, count));

    public override void Write(ReadOnlySpan<char> buffer) => WriteChars(buffer);

    public override void Write(string? value) {
        if (value is not null) WriteChars(value.AsSpan());
    }

    public override void WriteLine() => WriteChars(['\n']);

    public override void WriteLine(string? value) {
        Enter();
        try {
            ThrowIfDisposed();
            ThrowIfShutDown();
            if (value is not null) PutCharsLocked(value.AsSpan());
            PutCharsLocked(['\n']);
            if (_inner is not null && Mode != BufferMode.Block) FlushLocked();
        } finally {
            Leave();
        }
    }

    // A .NET caller's asynchronous text writes. Encoding is CPU work, so they
    // write under the gate and flush, if the mode says so, asynchronously.
    public override Task WriteAsync(char value) => WriteCharsAsync(new[] { value }, default);

    public override Task WriteAsync(string? value) =>
        value is null ? Task.CompletedTask : WriteCharsAsync(value.AsMemory(), default);

    public override Task WriteAsync(char[] buffer, int index, int count) =>
        WriteCharsAsync(buffer.AsMemory(index, count), default);

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
        WriteCharsAsync(buffer, cancellationToken);

    public override Task WriteLineAsync(string? value) =>
        WriteCharsAsync(((value ?? "") + "\n").AsMemory(), default);

    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
        WriteCharsAsync((buffer.ToString() + "\n").AsMemory(), cancellationToken);

    private async Task WriteCharsAsync(ReadOnlyMemory<char> chars, CancellationToken cancel) {
        await EnterAsync(cancel).ConfigureAwait(false);
        try {
            ThrowIfDisposed();
            ThrowIfShutDown();
            if (_inner is not null && chars.Length * 3 > _buf.Length - _len)
                await DrainLockedAsync(cancel).ConfigureAwait(false);
            PutCharsLocked(chars.Span);
            if (_inner is not null
                && (Mode == BufferMode.None || (Mode == BufferMode.Line && chars.Span.IndexOf('\n') >= 0)))
                await FlushLockedAsync(cancel).ConfigureAwait(false);
        } finally {
            Leave();
        }
    }

    // --- Flushing -------------------------------------------------------------

    public override void Flush() {
        Enter();
        try {
            ThrowIfDisposed();
            FlushLocked();
        } finally {
            Leave();
        }
    }

    public override Task FlushAsync() => FlushValueAsync().AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        FlushValueAsync(cancellationToken).AsTask();

    public async ValueTask FlushValueAsync(CancellationToken cancel = default) {
        await EnterAsync(cancel).ConfigureAwait(false);
        try {
            ThrowIfDisposed();
            await FlushLockedAsync(cancel).ConfigureAwait(false);
        } finally {
            Leave();
        }
    }

    /// A flush that does no I/O when nothing is pending, for a view over this
    /// port whose own close flushes it.
    internal void FlushPending() {
        Enter();
        try {
            if (!_disposed && HasPending) FlushLocked();
        } finally {
            Leave();
        }
    }

    /// <summary>
    /// A flush for the end of the process: gives up rather than waits when
    /// another thread is in the middle of a write, since that thread may be the
    /// one ending the process.
    /// </summary>
    public void FlushAtExit() {
        if (!_gate.Enter(TimeSpan.FromSeconds(1))) return;
        try {
            if (!_disposed && !_writeClosed && HasPending) FlushLocked();
        } catch (IOException) {
        } catch (ObjectDisposedException) {
        } finally {
            Leave();
        }
    }

    // --- String ports -----------------------------------------------------------

    /// <summary>`get-output-bytes`: a copy of what a string port holds.</summary>
    public byte[] GetBytes() {
        Enter();
        try {
            if (_inner is not null)
                throw new InvalidOperationException("get-output-bytes: the port is not a string port.");
            return _buf.AsSpan(0, _len).ToArray();
        } finally {
            Leave();
        }
    }

    /// <summary>
    /// `get-output-string`: what a string port holds, as text. Bytes written
    /// with `write-bytes!` that are not UTF-8 read as U+FFFD, as Racket's do.
    /// </summary>
    public Utf8String GetString() {
        Enter();
        try {
            if (_inner is not null)
                throw new InvalidOperationException("get-output-string: the port is not a string port.");
            var bytes = _buf.AsSpan(0, _len);
            return Utf8String.TryFromUtf8(bytes, out var s) ? s : Utf8String.FromUtf8Lossy(bytes);
        } finally {
            Leave();
        }
    }

    public override string ToString() =>
        _inner is null ? GetString().ToString() : base.ToString() ?? nameof(BjoOutputPort);

    // --- The half-close -----------------------------------------------------------

    /// <summary>
    /// End the write half and leave the read half alone: a TCP FIN in one
    /// direction, not a closed connection. Everything buffered goes out first,
    /// and a write after it is refused rather than silently dropped. A stream
    /// with no half-close gets the flush and nothing else.
    /// </summary>
    public void Shutdown() {
        Enter();
        try {
            ThrowIfDisposed();
            if (_writeClosed) return;
            SettleSurrogateLocked();
            FlushLocked();
            _writeClosed = true;
            CloseWriteHalf();
        } finally {
            Leave();
        }
    }

    public async ValueTask ShutdownAsync(CancellationToken cancel = default) {
        await EnterAsync(cancel).ConfigureAwait(false);
        try {
            ThrowIfDisposed();
            if (_writeClosed) return;
            SettleSurrogateLocked();
            await FlushLockedAsync(cancel).ConfigureAwait(false);
            _writeClosed = true;
            CloseWriteHalf();
        } finally {
            Leave();
        }
    }

    private void CloseWriteHalf() {
        switch (_inner) {
            case IHalfClosable h:
                h.CloseWrite();
                break;
            case System.Net.Sockets.NetworkStream ns:
                // `Socket` has been public on `NetworkStream` since .NET Core 3.0.
                ns.Socket.Shutdown(System.Net.Sockets.SocketShutdown.Send);
                break;
        }
    }

    // --- Closing --------------------------------------------------------------

    /// <summary>
    /// Everything pending goes out, suspending rather than blocking, so that
    /// the <see cref="Dispose(bool)"/> after it has nothing to write.
    ///
    /// A failure or a cancellation drops what was pending before it is
    /// rethrown. Otherwise the close would retry it with a blocking write, on a
    /// stream that has just failed or for a fiber that was told to stop.
    /// </summary>
    internal async ValueTask SettleAsync(CancellationToken cancel) {
        await EnterAsync(cancel).ConfigureAwait(false);
        try {
            if (_disposed || _writeClosed || _inner is null) return;
            SettleSurrogateLocked();
            if (!HasPending) return;
            try {
                await FlushLockedAsync(cancel).ConfigureAwait(false);
            } catch {
                _len = 0;
                _unflushed = false;
                throw;
            }
        } finally {
            Leave();
        }
    }

    /// The close that suspends. The handle is released even when the flush
    /// fails. A port its scope owns is closed with
    /// <see cref="BjolangRuntime.CloseOutputAsync"/> instead, which releases
    /// the registration too.
    public override async ValueTask DisposeAsync() {
        try {
            await SettleAsync(CancellationToken.None).ConfigureAwait(false);
        } finally {
            Dispose();
        }
    }

    protected override void Dispose(bool disposing) {
        if (!disposing) {
            base.Dispose(disposing);
            return;
        }

        Enter();
        try {
            if (_disposed) return;
            try {
                // Held bytes go out before the handle does. With nothing
                // pending there is no I/O at all, which is what makes a close
                // after a flush safe over a stream that refuses blocking I/O.
                if (!_writeClosed && _inner is not null) {
                    SettleSurrogateLocked();
                    if (HasPending) FlushLocked();
                }
            } finally {
                _disposed = true;
                // Drained first, and only then is the half given up: if the
                // reading half has already gone, this is the call that takes
                // the handle with it.
                if (_connection is not null) _connection.HalfClosed();
                else if (_ownsInner) _inner?.Dispose();
            }
        } finally {
            Leave();
            base.Dispose(disposing);
        }
    }

    // --- As a stream ------------------------------------------------------------

    /// <summary>
    /// The port as a write-only <see cref="Stream"/>, for a .NET API that takes
    /// one. Writes go through the port; disposing the view leaves the port
    /// open, and flushing it flushes the port.
    /// </summary>
    public Stream AsStream() => new PortStream(this);

    private sealed class PortStream(BjoOutputPort port) : Stream {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            port.WriteBytes(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => port.WriteBytes(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            port.WriteBytesAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            port.WriteBytesAsync(buffer, cancellationToken);

        public override void Flush() => port.FlushPending();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            port.FlushValueAsync(cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>
/// Making output ports, and the writes the prelude and `(std ports)` import.
///
/// Every write answers <see cref="Bjoml.Unit"/>, not C# `void`, for the reason
/// `BjolangRuntime.unit` gives: no `void` can stand for a type argument. The
/// suspending halves take a trailing <see cref="CancellationToken"/> and do not
/// name it in the Bjolang signature: an `#:async` import fills in the ambient
/// token at the call site.
/// </summary>
public static class OutputPorts {
    // --- Making ports ---------------------------------------------------------

    /// <summary>A port over a stream an opener answered, registered on the ambient scope.</summary>
    public static BjoOutputPort FromStream(Stream stream) =>
        BjolangRuntime.OwnOutput(new BjoOutputPort(stream));

    /// <summary>`open-output-string`: memory only, so nothing registers it.</summary>
    public static BjoOutputPort OpenString() => new();

    /// <summary>
    /// A .NET <see cref="TextWriter"/> as a port: its bytes decoded as UTF-8
    /// and written to the writer as they come. A port is already one and is
    /// handed back. Unbuffered, so that what a program writes is in the writer
    /// when it looks; closing the port leaves the writer open.
    /// </summary>
    public static BjoOutputPort FromTextWriter(TextWriter writer) {
        ArgumentNullException.ThrowIfNull(writer);
        return writer as BjoOutputPort
            ?? new BjoOutputPort(new WriterStream(writer), BjoOutputPort.SmallBufferSize, ownsInner: false) {
                Mode = BufferMode.None,
            };
    }

    /// <summary>
    /// `reencode-output-port`: a port whose UTF-8 is re-encoded as
    /// <paramref name="encoding"/> on its way into <paramref name="port"/>, as
    /// Racket's `reencode-output-port` makes one. A character the encoding
    /// lacks is written as its replacement. Closing the new port flushes it
    /// into the old one and leaves the old one open.
    /// </summary>
    public static BjoOutputPort Reencode(BjoOutputPort port, Encoding encoding) {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(encoding);
        var transcoded = Encoding.CreateTranscodingStream(
            port.AsStream(), encoding, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: false);
        return new BjoOutputPort(transcoded, BjoOutputPort.SmallBufferSize, ownsInner: true) { Mode = port.Mode };
    }

    public static Utf8String GetString(BjoOutputPort port) => port.GetString();

    public static byte[] GetBytes(BjoOutputPort port) => port.GetBytes();

    // --- Writing --------------------------------------------------------------

    public static Bjoml.Unit Write(BjoOutputPort port, Utf8String s) {
        port.WriteText(s);
        return default;
    }

    public static async ValueTask<Bjoml.Unit> WriteAsync(
        BjoOutputPort port, Utf8String s, CancellationToken cancel = default) {
        await port.WriteTextAsync(s, cancel).ConfigureAwait(false);
        return default;
    }

    public static Bjoml.Unit WriteLine(BjoOutputPort port, Utf8String s) {
        port.WriteTextLine(s);
        return default;
    }

    public static async ValueTask<Bjoml.Unit> WriteLineAsync(
        BjoOutputPort port, Utf8String s, CancellationToken cancel = default) {
        await port.WriteTextLineAsync(s, cancel).ConfigureAwait(false);
        return default;
    }

    public static Bjoml.Unit WriteChar(BjoOutputPort port, BjoChar c) {
        port.WriteScalar(c);
        return default;
    }

    public static async ValueTask<Bjoml.Unit> WriteCharAsync(
        BjoOutputPort port, BjoChar c, CancellationToken cancel = default) {
        await port.WriteScalarAsync(c, cancel).ConfigureAwait(false);
        return default;
    }

    public static Bjoml.Unit WriteBytes(BjoOutputPort port, byte[] bytes) {
        ArgumentNullException.ThrowIfNull(bytes);
        port.WriteBytes(bytes);
        return default;
    }

    public static async ValueTask<Bjoml.Unit> WriteBytesAsync(
        BjoOutputPort port, byte[] bytes, CancellationToken cancel = default) {
        ArgumentNullException.ThrowIfNull(bytes);
        await port.WriteBytesAsync(bytes, cancel).ConfigureAwait(false);
        return default;
    }

    // --- Flushing, the half-close, closing --------------------------------------

    public static Bjoml.Unit Flush(BjoOutputPort port) {
        port.Flush();
        return default;
    }

    public static async ValueTask<Bjoml.Unit> FlushAsync(BjoOutputPort port, CancellationToken cancel = default) {
        await port.FlushValueAsync(cancel).ConfigureAwait(false);
        return default;
    }

    public static Bjoml.Unit Shutdown(BjoOutputPort port) {
        port.Shutdown();
        return default;
    }

    public static async ValueTask<Bjoml.Unit> ShutdownAsync(BjoOutputPort port, CancellationToken cancel = default) {
        await port.ShutdownAsync(cancel).ConfigureAwait(false);
        return default;
    }

    public static Bjoml.Unit Close(BjoOutputPort port) => BjolangRuntime.CloseOutput(port);

    public static ValueTask<Bjoml.Unit> CloseAsync(BjoOutputPort port, CancellationToken cancel = default) =>
        BjolangRuntime.CloseOutputAsync(port, cancel);

    /// <summary>
    /// A <see cref="TextWriter"/>'s side of <see cref="FromTextWriter"/>: UTF-8
    /// bytes decoded and written as characters. A character split between two
    /// writes waits in the decoder for its other bytes.
    /// </summary>
    private sealed class WriterStream(TextWriter writer) : Stream {
        private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) {
            Span<char> chars = stackalloc char[512];
            while (!buffer.IsEmpty) {
                int take = Math.Min(buffer.Length, 256);
                _decoder.Convert(buffer[..take], chars, flush: false, out int used, out int n, out _);
                writer.Write(chars[..n]);
                buffer = buffer[used..];
            }
        }

        public override void Flush() => writer.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
