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

// Ports made from Bjolang functions: `make-input-port` and `make-output-port`
// in `(std ports)`.
//
// - Each is a Stream whose operations call bjoroutines, under an ordinary
//   port. The port does the buffering, the UTF-8 and the events.
// - A call that completes without suspending costs no task. One that suspends
//   is awaited through its promise, so a fiber that reads or writes the port
//   suspends rather than parks a thread.
// - The sync paths (a read or write from a sync caller, and a close from a
//   scope's release) block on the promise, as a sync read of any async stream
//   does.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Bjoml;
using Unit = Bjoml.Unit;
using ByteChunk = BjolangRuntime.Option<byte[]>;

namespace Bjolang.Runtime;

/// <summary>What `(std ports)` imports for `make-input-port` and `make-output-port`.</summary>
[NonUserCode]
public static class FnPorts {
    /// <summary>
    /// A stream whose bytes are the chunks <paramref name="read"/> returns.
    /// - <c>None</c> is the end of input; an empty chunk is skipped.
    /// - <paramref name="close"/> is called once, when the stream is disposed.
    /// </summary>
    public static Stream InputStream(Func<Fiber<ByteChunk>> read, Func<Unit> close) =>
        new FnInputStream(read, close);

    /// <summary>
    /// A stream whose writes, flushes and close call the functions.
    /// - <paramref name="write"/> is given a fresh array each time, which it may keep.
    /// - <paramref name="close"/> is called once. A port over this stream calls
    ///   it in place of a last flush.
    /// </summary>
    public static Stream OutputStream(Func<byte[], Fiber<Unit>> write, Func<Fiber<Unit>> flush, Func<Fiber<Unit>> close) =>
        new FnOutputStream(write, flush, close);

    /// The value of a call, waited for on this thread if it suspended.
    internal static T Wait<T>(Fiber<T> f) =>
        f.Core is { } p ? p.ToTask().GetAwaiter().GetResult() : f.Result;

    /// The value of a call, suspending if it suspended.
    internal static ValueTask<T> Await<T>(Fiber<T> f) =>
        f.Core is { } p ? new ValueTask<T>(p.ToTask()) : new ValueTask<T>(f.Result);
}

/// <summary>See <see cref="FnPorts.InputStream"/>.</summary>
[NonUserCode]
internal sealed class FnInputStream(Func<Fiber<ByteChunk>> read, Func<Unit> close) : Stream {
    // The chunk being read from, and how far. Touched only by the port's
    // refill, of which there is one at a time.
    private byte[]? _chunk;
    private int _at;
    private bool _ended;
    private bool _closed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// Copies from the chunk in hand. Zero once the chunk is used up.
    private int Take(Span<byte> into) {
        if (_chunk is null) return 0;
        int n = Math.Min(into.Length, _chunk.Length - _at);
        _chunk.AsSpan(_at, n).CopyTo(into);
        _at += n;
        if (_at == _chunk.Length) _chunk = null;
        return n;
    }

    /// Takes a chunk; false at the end of input.
    private bool Accept(ByteChunk chunk) {
        if (!chunk.IsSome) {
            _ended = true;
            return false;
        }
        if (chunk.Value is { Length: > 0 } bytes) {
            _chunk = bytes;
            _at = 0;
        }
        return true;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) {
        if (buffer.IsEmpty) return new ValueTask<int>(0);
        while (_chunk is null && !_ended) {
            var f = read();
            if (f.Core is not null) return ReadSuspendedAsync(f, buffer);
            Accept(f.Result);
        }
        return new ValueTask<int>(Take(buffer.Span));
    }

    private async ValueTask<int> ReadSuspendedAsync(Fiber<ByteChunk> f, Memory<byte> buffer) {
        Accept(await FnPorts.Await(f).ConfigureAwait(false));
        while (_chunk is null && !_ended)
            Accept(await FnPorts.Await(read()).ConfigureAwait(false));
        return Take(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) =>
        ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();

    public override int Read(Span<byte> buffer) {
        if (buffer.IsEmpty) return 0;
        while (_chunk is null && !_ended) Accept(FnPorts.Wait(read()));
        return Take(buffer);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
        if (disposing && !_closed) {
            _closed = true;
            close();
        }
        base.Dispose(disposing);
    }
}

/// <summary>See <see cref="FnPorts.OutputStream"/>.</summary>
[NonUserCode]
internal sealed class FnOutputStream(
    Func<byte[], Fiber<Unit>> write, Func<Fiber<Unit>> flush, Func<Fiber<Unit>> close) : Stream {
    private bool _closed;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_closed, this);

    public override void Write(ReadOnlySpan<byte> buffer) {
        ThrowIfClosed();
        FnPorts.Wait(write(buffer.ToArray()));
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    // The token is not passed on: the function runs in the writer's dynamic
    // environment and reads the writer's ambient token itself.
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default) {
        ThrowIfClosed();
        await FnPorts.Await(write(buffer.ToArray())).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancel) =>
        WriteAsync(buffer.AsMemory(offset, count), cancel).AsTask();

    public override void Flush() {
        ThrowIfClosed();
        FnPorts.Wait(flush());
    }

    public override async Task FlushAsync(CancellationToken cancel) {
        ThrowIfClosed();
        await FnPorts.Await(flush()).ConfigureAwait(false);
    }

    /// The close, suspending. Called once; a second call does nothing.
    internal async ValueTask CloseAsync() {
        if (_closed) return;
        _closed = true;
        await FnPorts.Await(close()).ConfigureAwait(false);
    }

    /// Marks the stream closed without calling the close function: for a port
    /// whose last write failed.
    internal void Abandon() => _closed = true;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
        if (disposing && !_closed) {
            _closed = true;
            FnPorts.Wait(close());
        }
        base.Dispose(disposing);
    }
}
