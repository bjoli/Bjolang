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
    /// UTF-8 needs no conversion: the port reads it as it is. Anything else
    /// is transcoded to UTF-8 on the way in. Either way a port is lenient,
    /// as Racket's are: bytes that do not decode read as U+FFFD, whatever
    /// fallback the encoding object carries. A byte order mark is not treated
    /// specially.
    /// </summary>
    public static BjoInputPort Over(Stream stream, Encoding encoding) {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(encoding);
        return encoding.CodePage == Utf8CodePage
            ? new BjoInputPort(stream)
            : new BjoInputPort(Transcoded(stream, encoding));
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
        return new BjoInputPort(Transcoded(port.AsStream(), encoding));
    }

    private const int Utf8CodePage = 65001;

    /// <summary>The stream's bytes, read as the encoding, as UTF-8; what does not decode is U+FFFD.</summary>
    private static Stream Transcoded(Stream stream, Encoding encoding) =>
        Encoding.CreateTranscodingStream(stream, Encodings.Lenient(encoding), Encoding.UTF8, leaveOpen: false);

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

    /// A `LineMode` as the prelude numbers it, in `line-mode-code`.
    private static LineMode Mode(int code) =>
        code is >= (int)LineMode.Any and <= (int)LineMode.ReturnLinefeed
            ? (LineMode)code
            : throw new ArgumentOutOfRangeException(nameof(code), code, "not a line mode.");

    public static Utf8String ReadLineOrThrow(BjoInputPort port, int mode = 0) =>
        LineOrThrow(port.ReadLineUtf8(Mode(mode)));

    public static ValueTask<Utf8String> ReadLineOrThrowAsync(
        BjoInputPort port, int mode, CancellationToken cancel = default) {
        var pending = port.ReadLineUtf8ValueAsync(Mode(mode), cancel);
        return pending.IsCompletedSuccessfully
            ? new ValueTask<Utf8String>(LineOrThrow(pending.Result))
            : Awaited(pending);

        static async ValueTask<Utf8String> Awaited(ValueTask<BjolangRuntime.Option<Utf8String>> pending) =>
            LineOrThrow(await pending.ConfigureAwait(false));
    }

    /// One read, answering `None` at end of input. Not `port-eof?` and then a
    /// read: between the two another fiber reading the same port can take the
    /// last line.
    public static BjolangRuntime.Option<Utf8String> ReadLineOpt(BjoInputPort port, int mode) =>
        port.ReadLineUtf8(Mode(mode));

    public static ValueTask<BjolangRuntime.Option<Utf8String>> ReadLineOptAsync(
        BjoInputPort port, int mode, CancellationToken cancel = default) =>
        port.ReadLineUtf8ValueAsync(Mode(mode), cancel);

    // --- Characters ---------------------------------------------------------

    private static Exception EndOfChar(string op) =>
        new EndOfStreamException(
            $"{op}: the port is at end of input. Guard with (port-eof? p), or use {op}/opt.");

    private static BjoChar CharOrThrow(int scalar, string op) =>
        scalar < 0 ? throw EndOfChar(op) : new BjoChar((uint)scalar);

    private static BjolangRuntime.Option<BjoChar> CharOption(int scalar) =>
        scalar < 0 ? BjolangRuntime.None<BjoChar>() : BjolangRuntime.Some(new BjoChar((uint)scalar));

    private static ValueTask<BjoChar> CharOrThrowAsync(ValueTask<int> pending, string op) {
        return pending.IsCompletedSuccessfully
            ? new ValueTask<BjoChar>(CharOrThrow(pending.Result, op))
            : Awaited(pending, op);

        static async ValueTask<BjoChar> Awaited(ValueTask<int> pending, string op) =>
            CharOrThrow(await pending.ConfigureAwait(false), op);
    }

    private static ValueTask<BjolangRuntime.Option<BjoChar>> CharOptionAsync(ValueTask<int> pending) {
        return pending.IsCompletedSuccessfully
            ? new ValueTask<BjolangRuntime.Option<BjoChar>>(CharOption(pending.Result))
            : Awaited(pending);

        static async ValueTask<BjolangRuntime.Option<BjoChar>> Awaited(ValueTask<int> pending) =>
            CharOption(await pending.ConfigureAwait(false));
    }

    public static BjoChar ReadCharOrThrow(BjoInputPort port) => CharOrThrow(port.ReadScalar(), "read-char");

    public static ValueTask<BjoChar> ReadCharOrThrowAsync(BjoInputPort port, CancellationToken cancel = default) =>
        CharOrThrowAsync(port.ReadScalarValueAsync(cancel), "read-char");

    public static BjolangRuntime.Option<BjoChar> ReadCharOpt(BjoInputPort port) => CharOption(port.ReadScalar());

    public static ValueTask<BjolangRuntime.Option<BjoChar>> ReadCharOptAsync(
        BjoInputPort port, CancellationToken cancel = default) =>
        CharOptionAsync(port.ReadScalarValueAsync(cancel));

    // `peek-char`: the character `read-char` would answer, left in the port.
    // It decodes from the port's buffer, so a peek costs what a read does and
    // takes nothing; a byte read after it starts at the character's first byte.

    public static BjoChar PeekCharOrThrow(BjoInputPort port) => CharOrThrow(port.PeekScalar(), "peek-char");

    public static ValueTask<BjoChar> PeekCharOrThrowAsync(BjoInputPort port, CancellationToken cancel = default) =>
        CharOrThrowAsync(port.PeekScalarValueAsync(cancel), "peek-char");

    public static BjolangRuntime.Option<BjoChar> PeekCharOpt(BjoInputPort port) => CharOption(port.PeekScalar());

    public static ValueTask<BjolangRuntime.Option<BjoChar>> PeekCharOptAsync(
        BjoInputPort port, CancellationToken cancel = default) =>
        CharOptionAsync(port.PeekScalarValueAsync(cancel));
}
