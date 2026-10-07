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

// The input port's text reads, which decode UTF-8 from its bytes. Three kinds
// of test:
//
//   * The port's guarantees as a text reader: a cancelled read consumes
//     nothing, one refill in flight, every line to exactly one reader, a
//     sticky failure reported after the text before it.
//   * Agreement with a decoding reader: random bytes, invalid sequences
//     included, cut into random refills and read through small buffers, give
//     the lines, characters and text a `StreamReader` gives. A byte order mark
//     is a character to both.
//   * One port for bytes and text: byte reads and text reads mixed on one
//     port, string ports over a string's own bytes, a .NET `TextReader` made a
//     port, and text in another encoding re-encoded.
//
// `BytePortTests.cs` has the byte reads and events on their own.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Bjoml.Tests.Harness;
using Bjolang.Runtime;
using BjoString;

namespace Bjoml.Tests;

public static class InputPortTextTests
{
    public static void RunAll()
    {
        Section("Input ports: text");
        Run("a cancelled read-line keeps the partial line", CancelledLineKeepsPartial);
        Run("a cancelled read-line after a final \\r keeps the line", CancelledLineAfterCr);
        Run("a cancelled read-all keeps what it gathered", CancelledReadAllKeepsText);
        Run("a line longer than the buffer survives a cancellation", LongLineSurvivesCancel);
        Run("at most one refill is ever in flight", OneRefillAtATime);
        Run("four readers get every line exactly once", ReadersEveryLineOnce);
        Run("a failure is sticky, after the lines before it", FailureIsStickyAfterLines);
        Run("\\r\\n split across refills is one terminator", CrLfAcrossRefills);
        Run("every line mode, at every buffer size", LineModes);
        Run("read-char/opt across waiting refills hands out every character once", CharsAcrossWaitingRefills);
        Run("a multi-byte character split across refills is one character", MultiByteAcrossRefills);
        Run("lines, characters and text agree with a decoding reader", AgreesWithStreamReader);
        Run("a byte order mark is a character", ByteOrderMarkIsACharacter);
        Run("UTF-16 reads hand out both halves of an astral character", Utf16ReadsSplitAstral);
        Run("read-char after half a character raises and moves on", ReadCharAfterHalf);
        Run("eof on empty input, and peek-char leaves the character", EofAndPeek);

        Section("Input ports: bytes and text on one port");
        Run("byte reads and text reads mix on one port", BytesAndTextMix);
        Run("a peek at bytes leaves them for read-line", PeekThenLine);
        Run("a string port reads the string's own bytes, a slice too", StringPortSharesBytes);
        Run("a .NET TextReader becomes a port of UTF-8 bytes", TextReaderBecomesPort);
        Run("text in another encoding is re-encoded as UTF-8", ReencodedText);
    }

    /// A stream fed by the test: each `Feed` is what one read hands over, and a
    /// read with nothing fed waits. It ignores the token, which is the case a
    /// port must not depend on, and counts how many reads are in flight.
    private sealed class FeedStream : Stream
    {
        private readonly ConcurrentQueue<byte[]?> _chunks = new();
        private readonly SemaphoreSlim _ready = new(0);
        private byte[] _rest = [];
        private int _inFlight;
        private int _max;

        public int MaxConcurrent => Volatile.Read(ref _max);

        /// Null is end of input.
        public void Feed(byte[]? chunk)
        {
            _chunks.Enqueue(chunk);
            _ready.Release();
        }

        public void Feed(string text) => Feed(Encoding.UTF8.GetBytes(text));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)))
                if (Interlocked.CompareExchange(ref _max, now, seen) == seen) break;

            try
            {
                if (_rest.Length == 0)
                {
                    await _ready.WaitAsync().ConfigureAwait(false);
                    _chunks.TryDequeue(out var chunk);
                    if (chunk is null) return 0;
                    _rest = chunk;
                }

                int n = Math.Min(buffer.Length, _rest.Length);
                _rest.AsSpan(0, n).CopyTo(buffer.Span);
                _rest = _rest[n..];
                return n;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// Bytes handed over in reads of the given sizes, cycled, and then the
    /// end. What a socket does to a file's worth of text.
    private sealed class ChunkyStream(byte[] bytes, int[] sizes) : Stream
    {
        private int _at;
        private int _turn;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, sizes[_turn++ % sizes.Length]), bytes.Length - _at);
            Array.Copy(bytes, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            var into = new byte[buffer.Length];
            int n = Read(into, 0, into.Length);
            into.AsSpan(0, n).CopyTo(buffer.Span);
            return new ValueTask<int>(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// A stream that hands over its bytes and then fails.
    private sealed class FailingStream(byte[] bytes) : Stream
    {
        private int _at;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at == bytes.Length) throw new IOException("the wire broke");
            int n = Math.Min(count, bytes.Length - _at);
            Array.Copy(bytes, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            var into = new byte[buffer.Length];
            int n = Read(into, 0, into.Length);
            into.AsSpan(0, n).CopyTo(buffer.Span);
            return new ValueTask<int>(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// A .NET reader handing over one character per read, so that every
    /// surrogate pair arrives in two reads.
    private sealed class OneCharReader(string text) : TextReader
    {
        private int _at;

        public override int Read(char[] buffer, int index, int count) => Read(buffer.AsSpan(index, count));

        public override int Read(Span<char> buffer)
        {
            if (_at == text.Length || buffer.IsEmpty) return 0;
            buffer[0] = text[_at++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancel = default) =>
            new(Read(buffer.Span));
    }

    /// Runs `read` with a token that fires after a moment, and insists that it
    /// was cancelled rather than completed.
    private static void Cancelled<T>(Func<CancellationToken, Task<T>> read, string what)
    {
        using var cts = new CancellationTokenSource(100);
        try
        {
            var value = read(cts.Token).GetAwaiter().GetResult();
            throw new AssertionException($"{what}: expected a cancellation, got <{value}>");
        }
        catch (OperationCanceledException) { }
    }

    private static string? Line(BjoInputPort p)
    {
        var line = p.ReadLineUtf8ValueAsync().AsTask().GetAwaiter().GetResult();
        return line.IsSome ? line.Value.ToString() : null;
    }

    private static BjoInputPort Over(byte[] bytes, int bufferSize = 64) =>
        new(new MemoryStream(bytes), bufferSize);

    private static byte[] Bytes(params int[] values) => values.Select(v => (byte)v).ToArray();

    private static string Shown(BjolangRuntime.Option<byte[]> bytes) =>
        bytes.IsSome ? Convert.ToHexString(bytes.Value) : "none";

    // --- Guarantees -----------------------------------------------------------

    private static void CancelledLineKeepsPartial()
    {
        var feed = new FeedStream();
        var port = new BjoInputPort(feed);

        feed.Feed("abc");
        Cancelled(c => port.ReadLineUtf8ValueAsync(c).AsTask(), "read-line with half a line");

        feed.Feed("def\n");
        AssertEqual("abcdef", Line(port), "the line after the cancelled read");
    }

    private static void CancelledLineAfterCr()
    {
        var feed = new FeedStream();
        var port = new BjoInputPort(feed);

        feed.Feed("line1\r");
        Cancelled(c => port.ReadLineUtf8ValueAsync(c).AsTask(), "read-line ending at a \\r");

        feed.Feed("line2\n");
        AssertEqual("line1", Line(port), "the first line");
        AssertEqual("line2", Line(port), "the second line");
    }

    private static void CancelledReadAllKeepsText()
    {
        var feed = new FeedStream();
        var port = new BjoInputPort(feed);

        feed.Feed("hello ");
        Cancelled(c => port.ReadToEndUtf8Async(c).AsTask(), "read-all before the end");

        feed.Feed("wörld");
        feed.Feed((byte[]?)null);
        AssertEqual("hello wörld", port.ReadToEndUtf8Async().AsTask().GetAwaiter().GetResult().ToString(),
            "read-all after it");
    }

    private static void LongLineSurvivesCancel()
    {
        var feed = new FeedStream();
        var port = new BjoInputPort(feed, 4);

        feed.Feed("0123456789");
        feed.Feed("åäöÅÄÖ");
        Cancelled(c => port.ReadLineUtf8ValueAsync(c).AsTask(), "read-line of a line several buffers long");

        feed.Feed("ABCDEFGHIJ\nnext\n");
        AssertEqual("0123456789åäöÅÄÖABCDEFGHIJ", Line(port), "the long line");
        AssertEqual("next", Line(port), "the line after it");
    }

    private static void OneRefillAtATime()
    {
        var feed = new FeedStream();
        var port = new BjoInputPort(feed);

        var readers = Enumerable.Range(0, 3).Select(_ => port.ReadLineUtf8ValueAsync().AsTask()).ToArray();
        Thread.Sleep(50);
        AssertEqual(1, feed.MaxConcurrent, "reads in flight on the stream");

        feed.Feed("a\nb\nc\n");
        var lines = readers.Select(t => t.GetAwaiter().GetResult().Value.ToString()).OrderBy(l => l).ToArray();
        AssertEqual("a,b,c", string.Join(",", lines), "what the three readers got");
    }

    private static void ReadersEveryLineOnce()
    {
        const int count = 200_000;
        var text = new StringBuilder();
        for (int i = 0; i < count; i++) text.Append("rad-").Append(i).Append("-åäö😀\n");

        // A small buffer, so that the readers meet at refills constantly.
        var port = Over(Encoding.UTF8.GetBytes(text.ToString()));
        var seen = new ConcurrentBag<string>();

        async Task Drain()
        {
            await Task.Yield();
            while (await port.ReadLineUtf8ValueAsync().ConfigureAwait(false) is { IsSome: true } line)
                seen.Add(line.Value.ToString());
        }

        Task.WaitAll(Drain(), Drain(), Drain(), Drain());

        AssertEqual(count, seen.Count, "lines handed out");
        var distinct = new HashSet<string>(seen);
        AssertEqual(count, distinct.Count, "distinct lines");
        for (int i = 0; i < count; i += 9973)
            Assert(distinct.Contains($"rad-{i}-åäö😀"), $"line {i} was handed out intact");
    }

    private static void FailureIsStickyAfterLines()
    {
        var port = new BjoInputPort(new FailingStream(Encoding.UTF8.GetBytes("one\ntwå\nthr")), 64);

        AssertEqual("one", Line(port), "the first line");
        AssertEqual("twå", Line(port), "the second line");

        // "thr" is a line cut short by the failure, not a line: the failure
        // is what the read reports.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var line = Line(port);
                throw new AssertionException($"attempt {attempt}: expected the failure, got <{line}>");
            }
            catch (IOException e)
            {
                AssertEqual("the wire broke", e.Message, $"attempt {attempt}: the failure");
            }
        }

        // A character cut short by the failure is not a replacement character
        // either.
        var chars = new BjoInputPort(new FailingStream(Bytes('a', 0xF0, 0x9F)), 64);
        AssertEqual((int)'a', chars.ReadScalar(), "the character before the cut");
        try
        {
            var c = chars.ReadScalar();
            throw new AssertionException($"expected the failure, got <{c}>");
        }
        catch (IOException e)
        {
            AssertEqual("the wire broke", e.Message, "the failure, for the cut character");
        }
    }

    private static void CrLfAcrossRefills()
    {
        var feed = new FeedStream();
        var port = new BjoInputPort(feed);

        feed.Feed("a\r");
        feed.Feed("\nb\r\n");
        feed.Feed((byte[]?)null);

        AssertEqual("a", Line(port), "the line before the split \\r\\n");
        AssertEqual("b", Line(port), "the line after it");
        AssertEqual(null, Line(port), "end of input");
    }

    /// Racket's five modes over one input that holds every terminator: a lone
    /// `\r`, a lone `\n`, `\r\n`, and `\n\r`, which is two. The buffer sizes put
    /// a refill between the two bytes of each pair somewhere.
    private static void LineModes()
    {
        var input = Encoding.UTF8.GetBytes("a\rb\nc\r\nd\n\re");
        var expected = new (LineMode Mode, string Lines)[] {
            (LineMode.Any, "a|b|c|d||e"),
            (LineMode.AnyOne, "a|b|c||d||e"),
            (LineMode.Linefeed, "a\rb|c\r|d|\re"),
            (LineMode.Return, "a|b\nc|\nd\n|e"),
            (LineMode.ReturnLinefeed, "a\rb\nc|d\n\re"),
        };

        foreach (var (mode, lines) in expected)
        {
            foreach (int bufferSize in new[] { 1, 2, 3, 4, 64 })
            {
                var port = Over(input, bufferSize);
                var got = new List<string>();
                while (port.ReadLineUtf8(mode) is { IsSome: true } line) got.Add(line.Value.ToString());
                AssertEqual(lines, string.Join("|", got), $"{mode}, buffer {bufferSize}");

                var feed = new FeedStream();
                foreach (var b in input) feed.Feed([b]);
                feed.Feed((byte[]?)null);
                var fed = new BjoInputPort(feed, bufferSize);
                got.Clear();
                while (fed.ReadLineUtf8ValueAsync(mode).AsTask().GetAwaiter().GetResult() is { IsSome: true } line)
                    got.Add(line.Value.ToString());
                AssertEqual(lines, string.Join("|", got), $"{mode}, buffer {bufferSize}, a byte per refill");
            }
        }
    }

    /// The `read-char/opt` dispatcher while the text arrives a few bytes at a
    /// time, so that most reads wait for a refill and many characters arrive
    /// in two pieces. A read that waited must be the read that completes.
    private static void CharsAcrossWaitingRefills()
    {
        var feed = new FeedStream();
        var port = new BjoInputPort(feed, 8);
        var expected = string.Concat(Enumerable.Range(0, 300).Select(i => $"{i}å€😀,"));
        var bytes = Encoding.UTF8.GetBytes(expected);

        var feeder = Task.Run(async () =>
        {
            for (int i = 0; i < bytes.Length; i += 7)
            {
                await Task.Delay(1).ConfigureAwait(false);
                feed.Feed(bytes[i..Math.Min(i + 7, bytes.Length)]);
            }
            feed.Feed((byte[]?)null);
        });

        var got = new StringBuilder();
        while (true)
        {
            var c = InputPorts.ReadCharOptAsync(port).AsTask().GetAwaiter().GetResult();
            if (!c.IsSome) break;
            got.Append(char.ConvertFromUtf32((int)c.Value.Value));
        }

        feeder.GetAwaiter().GetResult();
        AssertEqual(expected, got.ToString(), "what read-char/opt handed out");
    }

    private static void MultiByteAcrossRefills()
    {
        // Two, three and four bytes, each handed over one byte per refill, read
        // through buffers too small to hold more than a piece at a time.
        foreach (int bufferSize in new[] { 1, 2, 3, 4, 16 })
        {
            var feed = new FeedStream();
            var port = new BjoInputPort(feed, bufferSize);
            foreach (var b in Encoding.UTF8.GetBytes("aå€😀b\nö😀\n")) feed.Feed([b]);
            feed.Feed((byte[]?)null);

            var chars = new List<int>();
            for (int i = 0; i < 6; i++) chars.Add(port.ReadScalarValueAsync().AsTask().GetAwaiter().GetResult());
            AssertEqual("61,E5,20AC,1F600,62,A", string.Join(",", chars.Select(c => c.ToString("X"))),
                $"buffer {bufferSize}: the characters");
            AssertEqual("ö😀", Line(port), $"buffer {bufferSize}: the line after them");
            AssertEqual(-1, port.ReadScalar(), $"buffer {bufferSize}: end of input");
        }
    }

    // --- Against a decoding reader ----------------------------------------------

    /// Pieces random input is made of: ASCII, every line ending, valid
    /// sequences of each length, a byte order mark, and the invalid sequences
    /// a decoder has to make a decision about.
    private static readonly byte[][] Pieces = [
        Bytes('a'), Bytes('b'), Bytes(' '), Bytes('\r'), Bytes('\n'), Bytes('\r', '\n'),
        Bytes(0xC3, 0xA5), Bytes(0xE2, 0x82, 0xAC), Bytes(0xF0, 0x9F, 0x98, 0x80),
        Bytes(0xEF, 0xBB, 0xBF),
        Bytes(0x80), Bytes(0xBF), Bytes(0xFF), Bytes(0xFE), Bytes(0xC0, 0xAF), Bytes(0xC3),
        Bytes(0xE2, 0x82), Bytes(0xF0, 0x9F, 0x98), Bytes(0xED, 0xA0, 0x80),
        Bytes(0xF4, 0x90, 0x80, 0x80), Bytes(0xF8, 0x88, 0x80, 0x80, 0x80),
    ];

    private static byte[] RandomInput(Random random)
    {
        var bytes = new List<byte>();
        if (random.Next(4) == 0) bytes.AddRange(Bytes(0xEF, 0xBB, 0xBF));
        int pieces = random.Next(0, 40);
        for (int i = 0; i < pieces; i++) bytes.AddRange(Pieces[random.Next(Pieces.Length)]);
        return bytes.ToArray();
    }

    /// The reference: a decoder held to UTF-8 that neither skips a byte order
    /// mark nor switches encoding on one. An encoding with no preamble is what
    /// keeps `StreamReader` from skipping one.
    private static StreamReader Reference(byte[] bytes) =>
        new(new MemoryStream(bytes), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false);

    private static BjoInputPort Chunked(byte[] bytes, Random random)
    {
        var sizes = Enumerable.Range(0, 5).Select(_ => random.Next(1, 6)).ToArray();
        return new BjoInputPort(new ChunkyStream(bytes, sizes), random.Next(1, 10));
    }

    private static string Show(byte[] bytes) => Convert.ToHexString(bytes);

    private static void AgreesWithStreamReader()
    {
        var random = new Random(20261007);

        for (int round = 0; round < 3000; round++)
        {
            var bytes = RandomInput(random);
            string expected = Reference(bytes).ReadToEnd();

            // Lines, as Bjolang strings and as .NET strings.
            var lines = new List<string>();
            using (var reference = Reference(bytes))
                while (reference.ReadLine() is { } line) lines.Add(line);
            var expectedLines = string.Join("|", lines.Select(l => Utf8String.FromUtf16(l).ToString()));

            var port = Chunked(bytes, random);
            var got = new List<string>();
            while (port.ReadLineUtf8() is { IsSome: true } line) got.Add(line.Value.ToString());
            AssertEqual(expectedLines, string.Join("|", got), $"{Show(bytes)}: read-line");

            port = Chunked(bytes, random);
            got.Clear();
            while (port.ReadLine() is { } line) got.Add(line);
            AssertEqual(string.Join("|", lines), string.Join("|", got), $"{Show(bytes)}: ReadLine");

            // Characters, as scalars and as UTF-16 code units.
            port = Chunked(bytes, random);
            var scalars = new List<int>();
            for (int c; (c = port.ReadScalar()) >= 0;) scalars.Add(c);
            AssertEqual(string.Join(",", expected.EnumerateRunes().Select(r => r.Value)), string.Join(",", scalars),
                $"{Show(bytes)}: read-char");

            port = Chunked(bytes, random);
            var units = new StringBuilder();
            for (int c; (c = port.Read()) >= 0;) units.Append((char)c);
            AssertEqual(expected, units.ToString(), $"{Show(bytes)}: Read");

            port = Chunked(bytes, random);
            var block = new char[random.Next(1, 5)];
            units.Clear();
            for (int n; (n = port.Read(block, 0, block.Length)) > 0;) units.Append(block, 0, n);
            AssertEqual(expected, units.ToString(), $"{Show(bytes)}: Read into a block");

            // The rest.
            AssertEqual(Utf8String.FromUtf16(expected).ToString(),
                Chunked(bytes, random).ReadToEndUtf8().ToString(), $"{Show(bytes)}: read-all");
            AssertEqual(expected, Chunked(bytes, random).ReadToEnd(), $"{Show(bytes)}: ReadToEnd");
        }
    }

    // --- Characters -------------------------------------------------------------

    private static void ByteOrderMarkIsACharacter()
    {
        AssertEqual("\uFEFFa", Line(Over(Bytes(0xEF, 0xBB, 0xBF, 'a', '\n'))), "a UTF-8 mark");
        AssertEqual(0xFEFF, Over(Bytes(0xEF, 0xBB, 0xBF)).ReadScalar(), "a mark on its own");

        // UTF-16, read as the UTF-8 it is not: each byte that cannot start a
        // character is one replacement.
        var scalars = new List<int>();
        var port = Over(Bytes(0xFF, 0xFE, 'h', 0));
        for (int c; (c = port.ReadScalar()) >= 0;) scalars.Add(c);
        AssertEqual("FFFD,FFFD,68,0", string.Join(",", scalars.Select(c => c.ToString("X"))), "UTF-16 bytes");
    }

    private static void Utf16ReadsSplitAstral()
    {
        var smile = char.ConvertFromUtf32(0x1F600);
        var port = Over(Encoding.UTF8.GetBytes("a" + smile + "b\n" + smile + "c"));

        AssertEqual((int)'a', port.Peek(), "peek a");
        AssertEqual((int)'a', port.Read(), "read a");
        AssertEqual((int)smile[0], port.Peek(), "peek the high half");
        AssertEqual((int)smile[0], port.Read(), "read the high half");
        AssertEqual((int)smile[1], port.Peek(), "peek the low half");
        AssertEqual((int)smile[1], port.Read(), "read the low half");
        AssertEqual("b", port.ReadLine(), "the rest of the line");

        // A block of one takes half a character, and the line after it starts
        // with the other half.
        var one = new char[1];
        AssertEqual(1, port.Read(one, 0, 1), "a block of one");
        AssertEqual(smile[0], one[0], "the high half in the block");
        AssertEqual(smile[1] + "c", port.ReadLine(), "the line starting with the low half");
        AssertEqual(null, port.ReadLine(), "end of input");
    }

    private static void ReadCharAfterHalf()
    {
        var smile = char.ConvertFromUtf32(0x1F600);
        var port = Over(Encoding.UTF8.GetBytes(smile + "x"));

        AssertEqual((int)smile[0], port.Read(), "the high half, by a .NET read");
        Assert(!port.Eof(), "not at end with the low half pending");
        try
        {
            var c = port.ReadScalar();
            throw new AssertionException($"expected a refusal, got <{c}>");
        }
        catch (InvalidOperationException e)
        {
            Assert(e.Message.Contains("unpaired low surrogate"), $"the message: {e.Message}");
        }
        AssertEqual((int)'x', port.ReadScalar(), "the character after the half");
    }

    private static void EofAndPeek()
    {
        Assert(Over([]).Eof(), "empty input");
        Assert(!Over(Bytes(0xEF, 0xBB)).Eof(), "two bytes of a mark are input");
        AssertEqual("\uFFFD", Line(Over(Bytes(0xEF, 0xBB))), "and read as one replacement");

        var port = Over(Encoding.UTF8.GetBytes("😀a"));
        AssertEqual(0x1F600, port.PeekScalar(), "peek-char");
        AssertEqual(0x1F600, port.PeekScalar(), "peek-char again");
        AssertEqual(0x1F600, port.ReadScalar(), "read-char gets the peeked character");
        AssertEqual((int)'a', port.ReadScalar(), "and then the next");
        AssertEqual(-1, port.PeekScalar(), "peek-char at the end");
    }

    // --- One port for bytes and text ----------------------------------------------

    private static void BytesAndTextMix()
    {
        foreach (int bufferSize in new[] { 1, 3, 64 })
        {
            var port = Over(Encoding.UTF8.GetBytes("HEAD\r\nå😀rest\nbody"), bufferSize);
            var into = new byte[4];

            AssertEqual("HEAD", Line(port), $"buffer {bufferSize}: a line");
            AssertEqual(0xE5, port.ReadScalar(), $"buffer {bufferSize}: a character");

            AssertEqual(4, port.ReadInto(into), $"buffer {bufferSize}: four bytes");
            AssertEqual("F09F9880", Convert.ToHexString(into), $"buffer {bufferSize}: the emoji's bytes");

            AssertEqual("rest", Line(port), $"buffer {bufferSize}: the line after the bytes");
            AssertEqual("body", port.ReadToEndUtf8().ToString(), $"buffer {bufferSize}: the rest");
            Assert(port.Eof(), $"buffer {bufferSize}: at end");
        }
    }

    private static void PeekThenLine()
    {
        var feed = new FeedStream();
        var port = new BjoInputPort(feed, 4);
        feed.Feed("GET / HTTP/1.1\r\n");
        feed.Feed("Host: x\r\n");
        feed.Feed((byte[]?)null);

        AssertEqual("474554", Shown(port.PeekBytes(0, 3)), "the method, peeked");
        AssertEqual("GET / HTTP/1.1", Line(port), "the request line, peeked bytes included");
        AssertEqual("Host: x", Line(port), "the header");
    }

    private static void StringPortSharesBytes()
    {
        var whole = Utf8String.FromUtf16("  åäö\n😀x  ");
        var slice = whole.TrimSlice();

        var port = InputPorts.FromString(slice);
        AssertEqual("åäö", Line(port), "the slice's first line");
        AssertEqual(0x1F600, port.ReadScalar(), "a character");
        AssertEqual("x", port.ReadToEndUtf8().ToString(), "the rest, and not the whole string's");
        Assert(port.Eof(), "at end");

        var empty = InputPorts.FromString(Utf8String.Empty);
        Assert(empty.Eof(), "an empty string port is at end");
        AssertEqual(null, Line(empty), "and has no line");
    }

    private static void TextReaderBecomesPort()
    {
        var smile = char.ConvertFromUtf32(0x1F600);
        var port = InputPorts.FromTextReader(new OneCharReader("a" + smile + "\nb" + '\uD800' + "c"));

        AssertEqual("a" + smile, Line(port), "a pair that arrived in two reads");
        AssertEqual("b\uFFFDc", port.ReadToEndUtf8().ToString(), "a lone surrogate is a replacement");

        var already = Over(Bytes('x'));
        Assert(ReferenceEquals(already, InputPorts.FromTextReader(already)), "a port is handed back as it is");
    }

    private static void ReencodedText()
    {
        // "grüße" in Latin-1: ü and ß are single bytes that are not UTF-8.
        var latin1 = Over(Bytes('g', 'r', 0xFC, 0xDF, 'e', '\n', 'x'));
        AssertEqual("FC", Shown(latin1.PeekBytes(2, 1)), "the Latin-1 byte, peeked first");
        var text = InputPorts.Reencode(latin1, Encoding.Latin1);
        AssertEqual("grüße", Line(text), "a Latin-1 line");
        AssertEqual("C3BC", Shown(InputPorts.Reencode(Over(Bytes(0xFC)), Encoding.Latin1).PeekBytes(0, 2)),
            "the re-encoded port's bytes are UTF-8");

        // UTF-16 with its mark: the mark is a character here too.
        var utf16 = InputPorts.Reencode(Over(Bytes(0xFF, 0xFE, 'h', 0, 'i', 0)), Encoding.Unicode);
        var all = utf16.ReadToEndUtf8().ToString();
        AssertEqual("hi", all.TrimStart('\uFEFF'), "UTF-16 text");
        AssertEqual(all.StartsWith('\uFEFF') ? "kept" : "dropped", ReencodeKeepsMark, "what happens to the mark");
    }

    /// Whether re-encoding keeps a byte order mark as a character. Pinned
    /// here so that a change in what .NET's transcoder does is a failing test
    /// and not a silent change of what `reencode-input-port` hands out.
    private const string ReencodeKeepsMark = "kept";
}
