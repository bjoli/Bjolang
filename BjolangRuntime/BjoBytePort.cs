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

// Everything around the input port (`BjoInputPort.cs`): connections, the byte
// output port, pipes, `limited`, the read events, and the dispatchers
// `(std ports)` imports.

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
/// One stream shared by the two halves of a connection.
///
/// A connection is a pair of byte ports and there is no third value — that is
/// what lets a protocol written against `(std ports)` run over TCP without
/// knowing it is TCP — so this is where the handle lives, behind both of them.
///
/// **The handle goes when both halves have been closed, or when whatever owns
/// the connection releases it, whichever comes first.** A count of two, and it
/// is the only rule under which `(with-open ((i in) (o out)) ...)` means what it
/// looks like it means. The two rules it is not:
///
///   * *released when either half is closed* would make the pair a lie — the
///     read half could never be closed while the write half still answered, and
///     `shutdown!` would lose most of its point;
///   * *released only when the scope ends* needs no counting and fails under
///     load: a server accepting in a loop inside one long-lived scope would
///     hold a descriptor per connection until that scope returned.
///
/// The peer address lives here for the same reason the stream does: there is
/// nowhere else, and a server that cannot log who connected is not finished.
/// </summary>
public sealed class BjoConnection : IDisposable {
    private readonly Stream _stream;
    private int _halvesOpen = 2;
    private int _disposed;

    public BjoConnection(Stream stream, string? peer) {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        Peer = peer;
    }

    /// <summary>
    /// The scope registration that will close this, for a connection a fiber
    /// made, or null for one a listener owns.
    ///
    /// Released rather than disposed when the second half closes, for the reason
    /// <see cref="BjolangRuntime.CloseInput"/> gives: disposing directly would
    /// release the handle and leave a spent node on the scope's list.
    /// </summary>
    public BjolangRuntime.Owned? Owner;

    /// <summary>What `peer-address` answers. Null for a stream with no peer.</summary>
    public string? Peer { get; }

    public Stream Stream => _stream;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Whoever else has to be told when this connection is over — a listener
    /// forgetting it from its registry of live connections. Runs exactly once.
    /// </summary>
    internal Action<BjoConnection>? Forget;

    /// One of the two ports was closed. The second one takes the handle with it.
    internal void HalfClosed() {
        if (Interlocked.Decrement(ref _halvesOpen) > 0) return;
        if (Owner is { } owned) owned.Release();
        else Dispose();
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Forget?.Invoke(this);
        _stream.Dispose();
    }

    /// <summary>The reading half. Owns nothing on its own; see the class doc.</summary>
    public BjoInputPort Input() => new(this);

    /// <summary>The writing half.</summary>
    public BjoByteOutputPort Output() => new(this);
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
///
/// # Closing
///
/// A close writes only what is pending: bytes in the buffer, or bytes the
/// stream has been given since its last flush. Some streams refuse blocking
/// I/O (Kestrel's do), and a close is blocking, because a scope's release
/// cannot suspend. After `flush!` a close does no I/O at all, and
/// <see cref="DisposeAsync"/> is the close that suspends instead.
/// </summary>
public sealed class BjoByteOutputPort : IDisposable, IAsyncDisposable {
    private const int DefaultBufferSize = 4096;

    private readonly Stream _inner;
    private readonly bool _ownsInner;
    private readonly byte[] _buf;
    private int _len;
    private bool _disposed;
    private bool _writeClosed;

    /// Bytes have reached `_inner` since it was last flushed. Set before the
    /// write rather than after, because a write that fails partway may already
    /// have handed some of them over.
    private bool _unflushed;

    private bool HasPending => _len > 0 || _unflushed;

    /// <summary>See <see cref="BjoInputPort.Owner"/>.</summary>
    public BjolangRuntime.Owned? Owner;

    /// See <see cref="BjoInputPort"/>'s field of the same name.
    private readonly BjoConnection? _connection;

    public BjoByteOutputPort(Stream inner) : this(inner, DefaultBufferSize, true, null) { }

    public BjoByteOutputPort(Stream inner, bool ownsInner) : this(inner, DefaultBufferSize, ownsInner, null) { }

    public BjoByteOutputPort(Stream inner, int bufferSize, bool ownsInner = true)
        : this(inner, bufferSize, ownsInner, null) { }

    /// The writing half of a connection. See <see cref="BjoConnection"/>.
    internal BjoByteOutputPort(BjoConnection connection)
        : this(connection.Stream, DefaultBufferSize, false, connection) { }

    private BjoByteOutputPort(Stream inner, int bufferSize, bool ownsInner, BjoConnection? connection) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        if (!inner.CanWrite)
            throw new ArgumentException("a byte output port needs a writable stream.", nameof(inner));

        _inner = inner;
        _ownsInner = ownsInner;
        _connection = connection;
        _buf = new byte[bufferSize];
    }

    /// <summary>See <see cref="BjoInputPort.Peer"/>.</summary>
    public string? Peer => _connection?.Peer;

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
        _unflushed = true;
        _inner.Write(_buf, 0, n);
    }

    private async ValueTask DrainAsync(CancellationToken cancel) {
        if (_len == 0) return;
        int n = _len;
        _len = 0;
        _unflushed = true;
        await _inner.WriteAsync(_buf.AsMemory(0, n), cancel).ConfigureAwait(false);
    }

    // --- Flushing -----------------------------------------------------------

    public void Flush() {
        ThrowIfDisposed();
        DrainSync();
        _inner.Flush();
        _unflushed = false;
    }

    public async ValueTask FlushValueAsync(CancellationToken cancel = default) {
        ThrowIfDisposed();
        await DrainAsync(cancel).ConfigureAwait(false);
        await _inner.FlushAsync(cancel).ConfigureAwait(false);
        _unflushed = false;
    }

    /// A flush that does no I/O when nothing is pending. A text writer over
    /// this port uses it, because `StreamWriter.Dispose` always flushes its
    /// stream, and closing the text writer after an asynchronous flush must not
    /// block.
    internal void FlushPending() {
        if (HasPending) Flush();
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
        _unflushed = false;
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
        _unflushed = false;
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

    /// <summary>
    /// Everything pending goes out, suspending rather than blocking, so that
    /// the <see cref="Dispose"/> after it has nothing to write.
    ///
    /// A failure or a cancellation drops what was pending before it is
    /// rethrown. Otherwise `Dispose` would retry it with a blocking write, on a
    /// stream that has just failed or for a fiber that was told to stop.
    /// </summary>
    internal async ValueTask SettleAsync(CancellationToken cancel) {
        if (_disposed || _writeClosed || !HasPending) return;

        try {
            await DrainAsync(cancel).ConfigureAwait(false);
            await _inner.FlushAsync(cancel).ConfigureAwait(false);
            _unflushed = false;
        } catch {
            _len = 0;
            _unflushed = false;
            throw;
        }
    }

    /// The close that suspends. The handle is released even when the flush
    /// fails. A port its scope owns is closed with
    /// <see cref="BjolangRuntime.CloseByteOutputAsync"/> instead, which
    /// releases the registration too.
    public async ValueTask DisposeAsync() {
        try {
            await SettleAsync(CancellationToken.None).ConfigureAwait(false);
        } finally {
            Dispose();
        }
    }

    public void Dispose() {
        if (_disposed) return;

        try {
            // Held bytes go out before the handle does, and `_disposed` is set
            // only afterwards so that the drain is not refused by its own guard.
            // With nothing pending there is no I/O at all, which is what makes
            // a close after `flush!` safe over a stream that refuses blocking
            // I/O.
            if (!_writeClosed && HasPending) {
                DrainSync();
                _inner.Flush();
                _unflushed = false;
            }
        } finally {
            _disposed = true;
            // Drained first, above, and only then is the half given up: if the
            // reading half has already gone, this is the call that takes the
            // handle with it.
            if (_connection is not null) _connection.HalfClosed();
            else if (_ownsInner) _inner.Dispose();
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
        Input = new BjoInputPort(_stream, BjoInputPort.SmallBufferSize, ownsInner: false);
        Output = new BjoByteOutputPort(_stream, ownsInner: true);
    }

    public BjoInputPort Input { get; }

    public BjoByteOutputPort Output { get; }

    /// <summary>
    /// A byte queue with two faces. Writes append and never wait; reads take
    /// from the front and wait for the writer when there is nothing.
    /// </summary>
    private sealed class BytePipeStream : Stream, IHalfClosable, ISyncReadable {
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
/// Over the port's <see cref="BjoInputPort.AsStream"/> rather than over its
/// stream, which is the whole reason the limit is exact: everything the
/// underlying port had buffered counts towards the bound, and once the bound is
/// reached the underlying port is positioned exactly where the limit ended.
/// </summary>
internal sealed class LimitedStream : Stream, ISyncReadable {
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
    private readonly BjoInputPort _port;
    private readonly int _count;

    internal ByteReadEvent(BjoInputPort port, int count) {
        _port = port;
        _count = count;
    }

    public void Publish(SyncState state, int eventId, Action<ByteRead> onSync) =>
        _port.PublishRead(_count, state, eventId, onSync);

    bool INowable<ByteRead>.TryNow(out ByteRead value) => _port.TryReadNow(_count, out value);
}

/// <summary>The port is finished. See <c>EofWaiter</c>.</summary>
internal sealed class ByteEofEvent : IEvent<Unit>, INowable<Unit> {
    private readonly BjoInputPort _port;

    internal ByteEofEvent(BjoInputPort port) => _port = port;

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
/// A class of its own rather than static members on the ports, because there
/// are two port types here and one module above: a single prefix is what
/// keeps the `import/extern` block in `lib/std/ports.bjo` readable. The shape
/// is `Bjoml.InboxModule`'s.
///
/// The suspending halves take a trailing <see cref="CancellationToken"/> and do
/// not name it in the Bjolang signature: an `#:async` import fills in the
/// ambient token at the call site.
/// </summary>
public static class BytePorts {
    // --- Sources ------------------------------------------------------------

    /// The `#:exceptions` on the Bjolang side turns whatever this throws into a
    /// `Result`, which is why nothing is caught here.
    public static BjoInputPort OpenInput(string path) =>
        BjolangRuntime.OwnInput(new BjoInputPort(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)));

    public static BjoByteOutputPort OpenOutput(string path) =>
        BjolangRuntime.OwnByteWriter(new BjoByteOutputPort(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)));

    public static BjoInputPort FromStream(Stream stream) =>
        BjolangRuntime.OwnInput(new BjoInputPort(stream));

    public static BjoByteOutputPort ToStream(Stream stream) =>
        BjolangRuntime.OwnByteWriter(new BjoByteOutputPort(stream));

    public static BjoBytePipe MakePipe() => new();

    public static BjoInputPort PipeInput(BjoBytePipe pipe) => pipe.Input;

    public static BjoByteOutputPort PipeOutput(BjoBytePipe pipe) => pipe.Output;

    public static BjoInputPort Limited(BjoInputPort port, int limit) {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return new BjoInputPort(new LimitedStream(port.AsStream(), limit), ownsInner: false);
    }

    // --- Reading, as events -------------------------------------------------

    public static IEvent<ByteRead> ReadSomeEvent(BjoInputPort port) => new ByteReadEvent(port, 0);

    public static IEvent<ByteRead> ReadBytesEvent(BjoInputPort port, int count) {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return new ByteReadEvent(port, count);
    }

    public static IEvent<Unit> EofEvent(BjoInputPort port) => new ByteEofEvent(port);

    // --- Reading, without a choice ------------------------------------------

    public static bool Eof(BjoInputPort port) => port.Eof();

    public static ValueTask<bool> EofAsync(BjoInputPort port, CancellationToken cancel = default) =>
        port.EofAsync(cancel);

    public static BjolangRuntime.Option<byte[]> PeekBytes(BjoInputPort port, int skip, int count) =>
        port.PeekBytes(skip, count);

    public static ValueTask<BjolangRuntime.Option<byte[]>> PeekBytesAsync(
        BjoInputPort port, int skip, int count, CancellationToken cancel = default) =>
        port.PeekBytesAsync(skip, count, cancel);

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

    public static Unit CloseInput(BjoInputPort port) => BjolangRuntime.CloseInput(port);

    public static Unit CloseOutput(BjoByteOutputPort port) => BjolangRuntime.CloseByteOutput(port);

    public static ValueTask<Unit> CloseOutputAsync(BjoByteOutputPort port, CancellationToken cancel = default) =>
        BjolangRuntime.CloseByteOutputAsync(port, cancel);

    // --- The text layer -----------------------------------------------------

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
        /// the byte port rather than stopping in its buffer. Only when something
        /// is pending, because `StreamWriter.Dispose` calls this even after an
        /// asynchronous flush has written everything.
        public override void Flush() => _port.FlushPending();

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
