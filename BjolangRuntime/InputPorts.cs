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

// Making input ports, and the reads the prelude imports.
//
// Every input port is a `BjoInputPort`, whatever it reads from: a file, a
// socket, a string, a pipe, a .NET `TextReader`, or bytes in another encoding.
// The last two are bytes made on the way in — UTF-8 encoded from the reader's
// characters, or transcoded to UTF-8 — so that the port's reads, text and bytes
// alike, always see UTF-8.

using System.Text;
using BjoString;

namespace Bjolang.Runtime;

public static class InputPorts {
    // --- Making ports -------------------------------------------------------

    /// <summary>
    /// `open-input-string`: a port over the string's own UTF-8, sharing its
    /// bytes rather than copying them. A string port, as in Racket, is bytes.
    /// </summary>
    public static BjoInputPort FromString(Utf8String s) => new(s.AsMemory());

    /// <summary>
    /// A .NET <see cref="TextReader"/> as a port: its characters encoded as
    /// UTF-8 as they are read. A port is already one and is handed back.
    ///
    /// For text that only exists as characters — `Console.In`, a reader a .NET
    /// API returned. Where the bytes underneath are reachable, a port over
    /// them is the better port: nothing is decoded only to be encoded again.
    /// </summary>
    public static BjoInputPort FromTextReader(TextReader reader) {
        ArgumentNullException.ThrowIfNull(reader);
        return reader as BjoInputPort ?? new BjoInputPort(new ReaderStream(reader));
    }

    /// <summary>
    /// A port over bytes in a declared encoding: the one rule for how bytes
    /// become text, used for an HTTP body and by <see cref="Reencode"/>.
    ///
    /// UTF-8 that replaces invalid bytes, which is what `utf8` and
    /// `Encoding.UTF8` both are, needs no conversion: the port reads it as it
    /// is. Anything else, including a UTF-8 that throws on invalid bytes, is
    /// transcoded to UTF-8 on the way in, decoded strictly by the encoding's
    /// own rules. A byte order mark is not treated specially.
    /// </summary>
    public static BjoInputPort Over(Stream stream, Encoding encoding) {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(encoding);
        return IsLenientUtf8(encoding)
            ? new BjoInputPort(stream)
            : new BjoInputPort(Encoding.CreateTranscodingStream(stream, encoding, Encoding.UTF8, leaveOpen: false));
    }

    /// <summary>
    /// `reencode-input-port`: a port whose bytes are <paramref name="port"/>'s
    /// read as <paramref name="encoding"/> and re-encoded as UTF-8, as
    /// Racket's `reencode-input-port` makes one. Reads through the port, so
    /// what it had buffered is not skipped. Closing the new port does not
    /// close the old one.
    /// </summary>
    public static BjoInputPort Reencode(BjoInputPort port, Encoding encoding) {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(encoding);
        return new BjoInputPort(
            Encoding.CreateTranscodingStream(port.AsStream(), encoding, Encoding.UTF8, leaveOpen: false));
    }

    private static bool IsLenientUtf8(Encoding encoding) =>
        encoding is UTF8Encoding && encoding.DecoderFallback is DecoderReplacementFallback { DefaultString: "\uFFFD" };

    /// <summary>
    /// A <see cref="TextReader"/>'s characters as UTF-8 bytes. A lone
    /// surrogate becomes U+FFFD, as it does when a .NET string becomes a
    /// Bjolang one.
    /// </summary>
    private sealed class ReaderStream(TextReader reader) : Stream, ISyncReadable {
        private readonly Encoder _encoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetEncoder();
        private readonly char[] _chars = new char[2048];
        private readonly byte[] _bytes = new byte[2048 * 3 + 4];
        private int _at;
        private int _end;
        private bool _done;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// What one read of characters encodes to: false at the end. A read
        /// that ends on a high surrogate encodes the characters before it and
        /// keeps the surrogate in the encoder for the next.
        private bool Encode(int n) {
            bool last = n == 0;
            _encoder.Convert(_chars.AsSpan(0, n), _bytes, last, out _, out _end, out _);
            _at = 0;
            if (last) _done = true;
            return _end > 0 || !last;
        }

        private int Hand(Span<byte> into) {
            int n = Math.Min(into.Length, _end - _at);
            _bytes.AsSpan(_at, n).CopyTo(into);
            _at += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer) {
            if (buffer.IsEmpty) return 0;
            while (_at == _end) {
                if (_done || !Encode(reader.Read(_chars, 0, _chars.Length))) return 0;
            }
            return Hand(buffer);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) {
            if (buffer.IsEmpty) return 0;
            while (_at == _end) {
                if (_done) return 0;
                int n = await reader.ReadAsync(_chars.AsMemory(), cancel).ConfigureAwait(false);
                if (!Encode(n)) return 0;
            }
            return Hand(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) {
            if (disposing) reader.Dispose();
            base.Dispose(disposing);
        }
    }

    // --- The reads the prelude imports ----------------------------------------
    //
    // In pairs, one per colour: a `defbjouble` body names one of these per
    // colour, and the two have to be the same question asked twice.
    //
    // A read that has not completed is awaited as it stands and never asked
    // for a second time: asking again would start a second read, and the
    // first would take a line or character nobody receives.

    public static bool PortEof(BjoInputPort port) => port.Eof();

    public static ValueTask<bool> PortEofAsync(BjoInputPort port, CancellationToken cancel = default) =>
        port.EofAsync(cancel);

    /// One UTF-16 code unit, or -1, for a reader that works in code units.
    public static int ReadUnit(BjoInputPort port) => port.Read();

    public static ValueTask<int> ReadUnitAsync(BjoInputPort port, CancellationToken cancel = default) =>
        port.ReadUnitValueAsync(cancel);

    public static Utf8String ReadRest(BjoInputPort port) => port.ReadToEndUtf8();

    public static ValueTask<Utf8String> ReadRestAsync(BjoInputPort port, CancellationToken cancel = default) =>
        port.ReadToEndUtf8Async(cancel);

    // --- Lines --------------------------------------------------------------

    /// The message both halves of `read-line` fail with: one string, because
    /// two bodies promising different things at end of input is exactly the
    /// drift a `defbjouble` cannot check for.
    private static Exception EndOfLine() =>
        new EndOfStreamException(
            "read-line: the port is at end of input. Guard with (port-eof? p), or use read-line/opt.");

    private static Utf8String LineOrThrow(BjolangRuntime.Option<Utf8String> line) =>
        line.IsSome ? line.Value : throw EndOfLine();

    public static Utf8String ReadLineOrThrow(BjoInputPort port) => LineOrThrow(port.ReadLineUtf8());

    public static ValueTask<Utf8String> ReadLineOrThrowAsync(BjoInputPort port, CancellationToken cancel = default) {
        var pending = port.ReadLineUtf8ValueAsync(cancel);
        return pending.IsCompletedSuccessfully
            ? new ValueTask<Utf8String>(LineOrThrow(pending.Result))
            : Awaited(pending);

        static async ValueTask<Utf8String> Awaited(ValueTask<BjolangRuntime.Option<Utf8String>> pending) =>
            LineOrThrow(await pending.ConfigureAwait(false));
    }

    /// One read, answering `None` at end of input. Not `port-eof?` and then a
    /// read: between the two another fiber reading the same port can take the
    /// last line.
    public static BjolangRuntime.Option<Utf8String> ReadLineOpt(BjoInputPort port) => port.ReadLineUtf8();

    public static ValueTask<BjolangRuntime.Option<Utf8String>> ReadLineOptAsync(
        BjoInputPort port, CancellationToken cancel = default) =>
        port.ReadLineUtf8ValueAsync(cancel);

    // --- Characters ---------------------------------------------------------

    private static Exception EndOfChar() =>
        new EndOfStreamException(
            "read-char: the port is at end of input. Guard with (port-eof? p), or use read-char/opt.");

    private static BjoChar CharOrThrow(int scalar) =>
        scalar < 0 ? throw EndOfChar() : new BjoChar((uint)scalar);

    private static BjolangRuntime.Option<BjoChar> CharOption(int scalar) =>
        scalar < 0 ? BjolangRuntime.None<BjoChar>() : BjolangRuntime.Some(new BjoChar((uint)scalar));

    public static BjoChar ReadCharOrThrow(BjoInputPort port) => CharOrThrow(port.ReadScalar());

    public static ValueTask<BjoChar> ReadCharOrThrowAsync(BjoInputPort port, CancellationToken cancel = default) {
        var pending = port.ReadScalarValueAsync(cancel);
        return pending.IsCompletedSuccessfully
            ? new ValueTask<BjoChar>(CharOrThrow(pending.Result))
            : Awaited(pending);

        static async ValueTask<BjoChar> Awaited(ValueTask<int> pending) =>
            CharOrThrow(await pending.ConfigureAwait(false));
    }

    public static BjolangRuntime.Option<BjoChar> ReadCharOpt(BjoInputPort port) => CharOption(port.ReadScalar());

    public static ValueTask<BjolangRuntime.Option<BjoChar>> ReadCharOptAsync(
        BjoInputPort port, CancellationToken cancel = default) {
        var pending = port.ReadScalarValueAsync(cancel);
        return pending.IsCompletedSuccessfully
            ? new ValueTask<BjolangRuntime.Option<BjoChar>>(CharOption(pending.Result))
            : Awaited(pending);

        static async ValueTask<BjolangRuntime.Option<BjoChar>> Awaited(ValueTask<int> pending) =>
            CharOption(await pending.ConfigureAwait(false));
    }
}
