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

using System.Runtime.ExceptionServices;
using System.Text;
using Unit = Bjoml.Unit;

namespace Bjolang.Runtime;

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

    /// <summary>See <see cref="BjoInputPort.Owner"/>.</summary>
    public BjolangRuntime.Owned? Owner;

    public BjoWriter(TextWriter inner) : this(inner, DefaultBufferSize) { }

    /// Settable for the reason the input port's is: the interesting case is text
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
