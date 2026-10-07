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

// `BjoUtf8Port`, the text port that reads UTF-8 without passing through
// UTF-16. Two kinds of test:
//
//   * `BjoPort`'s guarantees, case for case (see `TextPortTests.cs`): a
//     cancelled read consumes nothing, one fill in flight, every line to
//     exactly one reader, a sticky failure after the lines before it.
//   * Agreement with `StreamReader`, which is what file ports were before:
//     random bytes, invalid sequences included, cut into random fills and read
//     through small buffers, must give the same lines, characters and text.
//     Plus what is new here: a multi-byte character split across fills, the
//     byte order marks, and the UTF-16 view a .NET caller gets.

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

namespace Bjoml.Tests;

public static class Utf8PortTests
{
    public static void RunAll()
    {
        Section("UTF-8 text ports");
        Run("a cancelled read-line keeps the partial line", CancelledLineKeepsPartial);
        Run("a cancelled read-line after a final \\r keeps the line", CancelledLineAfterCr);
        Run("a cancelled read-all keeps what it gathered", CancelledReadAllKeepsText);
        Run("a line longer than the buffer survives a cancellation", LongLineSurvivesCancel);
        Run("at most one fill is ever in flight", OneFillAtATime);
        Run("four readers get every line exactly once", ReadersEveryLineOnce);
        Run("a failure is sticky, after the lines before it", FailureIsStickyAfterLines);
        Run("\\r\\n split across fills is one terminator", CrLfAcrossFills);
        Run("read-char/opt across waiting fills hands out every character once", CharsAcrossWaitingFills);
        Run("a multi-byte character split across fills is one character", MultiByteAcrossFills);
        Run("lines, characters and text agree with StreamReader", AgreesWithStreamReader);
        Run("a UTF-8 byte order mark is skipped once", Utf8BomSkippedOnce);
        Run("a UTF-16 or UTF-32 byte order mark is refused", ForeignBomRefused);
        Run("bom-encoding names the encoding a mark is for", BomEncodingNames);
        Run("UTF-16 reads hand out both halves of an astral character", Utf16ReadsSplitAstral);
        Run("read-char after half a character raises and moves on", ReadCharAfterHalf);
        Run("eof on empty input and on a lone byte order mark", EofCases);
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

    private static string? Line(BjoUtf8Port p)
    {
        var line = p.ReadLineUtf8ValueAsync().AsTask().GetAwaiter().GetResult();
        return line.IsSome ? line.Value.ToString() : null;
    }

    private static BjoUtf8Port Over(byte[] bytes, int bufferSize = 64) =>
        new(new MemoryStream(bytes), bufferSize);

    private static byte[] Bytes(params int[] values) => values.Select(v => (byte)v).ToArray();

    // -----------------------------------------------------------------------

    private static void CancelledLineKeepsPartial()
    {
        var feed = new FeedStream();
        var port = new BjoUtf8Port(feed);

        feed.Feed("abc");
        Cancelled(c => port.ReadLineUtf8ValueAsync(c).AsTask(), "read-line with half a line");

        feed.Feed("def\n");
        AssertEqual("abcdef", Line(port), "the line after the cancelled read");
    }

    private static void CancelledLineAfterCr()
    {
        var feed = new FeedStream();
        var port = new BjoUtf8Port(feed);

        feed.Feed("line1\r");
        Cancelled(c => port.ReadLineUtf8ValueAsync(c).AsTask(), "read-line ending at a \\r");

        feed.Feed("line2\n");
        AssertEqual("line1", Line(port), "the first line");
        AssertEqual("line2", Line(port), "the second line");
    }

    private static void CancelledReadAllKeepsText()
    {
        var feed = new FeedStream();
        var port = new BjoUtf8Port(feed);

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
        var port = new BjoUtf8Port(feed, 4);

        feed.Feed("0123456789");
        feed.Feed("åäöÅÄÖ");
        Cancelled(c => port.ReadLineUtf8ValueAsync(c).AsTask(), "read-line of a line several buffers long");

        feed.Feed("ABCDEFGHIJ\nnext\n");
        AssertEqual("0123456789åäöÅÄÖABCDEFGHIJ", Line(port), "the long line");
        AssertEqual("next", Line(port), "the line after it");
    }

    private static void OneFillAtATime()
    {
        var feed = new FeedStream();
        var port = new BjoUtf8Port(feed);

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

        // A small buffer, so that the readers meet at fills constantly.
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
        var port = new BjoUtf8Port(new FailingStream(Encoding.UTF8.GetBytes("one\ntwå\nthr")), 64);

        AssertEqual("one", Line(port), "the first line");
        AssertEqual("twå", Line(port), "the second line");

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

        // A character cut short by the failure is not a replacement character:
        // the rest of it was lost, and the read says so.
        var chars = new BjoUtf8Port(new FailingStream(Bytes('a', 0xF0, 0x9F)), 64);
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

    private static void CrLfAcrossFills()
    {
        var feed = new FeedStream();
        var port = new BjoUtf8Port(feed);

        feed.Feed("a\r");
        feed.Feed("\nb\r\n");
        feed.Feed((byte[]?)null);

        AssertEqual("a", Line(port), "the line before the split \\r\\n");
        AssertEqual("b", Line(port), "the line after it");
        AssertEqual(null, Line(port), "end of input");
    }

    /// The `read-char/opt` dispatcher while the text arrives a few bytes at a
    /// time, so that most reads wait for a fill and many characters arrive in
    /// two pieces. A read that waited must be the read that completes.
    private static void CharsAcrossWaitingFills()
    {
        var feed = new FeedStream();
        var port = new BjoUtf8Port(feed, 8);
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
            var c = BjoPort.ReadCharOptAsync(port).AsTask().GetAwaiter().GetResult();
            if (!c.IsSome) break;
            got.Append(char.ConvertFromUtf32((int)c.Value.Value));
        }

        feeder.GetAwaiter().GetResult();
        AssertEqual(expected, got.ToString(), "what read-char/opt handed out");
    }

    private static void MultiByteAcrossFills()
    {
        // Two, three and four bytes, each handed over one byte per fill, read
        // through buffers too small to hold more than a piece at a time.
        foreach (int bufferSize in new[] { 2, 3, 4, 16 })
        {
            var feed = new FeedStream();
            var port = new BjoUtf8Port(feed, bufferSize);
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

    // --- Against StreamReader ------------------------------------------------

    /// Pieces random input is made of: ASCII, every line ending, valid
    /// sequences of each length, and the invalid ones a decoder has to make a
    /// decision about. No 0xFE, so no input begins with a UTF-16 mark.
    private static readonly byte[][] Pieces = [
        Bytes('a'), Bytes('b'), Bytes(' '), Bytes('\r'), Bytes('\n'), Bytes('\r', '\n'),
        Bytes(0xC3, 0xA5), Bytes(0xE2, 0x82, 0xAC), Bytes(0xF0, 0x9F, 0x98, 0x80),
        Bytes(0xEF, 0xBB, 0xBF),
        Bytes(0x80), Bytes(0xBF), Bytes(0xFF), Bytes(0xC0, 0xAF), Bytes(0xC3),
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

    private static StreamReader Reference(byte[] bytes) =>
        new(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

    private static BjoUtf8Port Chunked(byte[] bytes, Random random)
    {
        var sizes = Enumerable.Range(0, 5).Select(_ => random.Next(1, 6)).ToArray();
        return new BjoUtf8Port(new ChunkyStream(bytes, sizes), random.Next(2, 10));
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
            var expectedLines = string.Join("|", lines.Select(l => BjoString.Utf8String.FromUtf16(l).ToString()));

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
            AssertEqual(BjoString.Utf8String.FromUtf16(expected).ToString(),
                Chunked(bytes, random).ReadToEndUtf8().ToString(), $"{Show(bytes)}: read-all");
            AssertEqual(expected, Chunked(bytes, random).ReadToEnd(), $"{Show(bytes)}: ReadToEnd");
        }
    }

    // --- Byte order marks ------------------------------------------------------

    private static void Utf8BomSkippedOnce()
    {
        var text = Bytes(0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, 'a', '\n');
        AssertEqual("\uFEFFa", Line(Over(text)), "the line after one mark");

        // The mark arriving a byte at a time, through the smallest buffer.
        var feed = new FeedStream();
        var port = new BjoUtf8Port(feed, 2);
        foreach (var b in text) feed.Feed([b]);
        feed.Feed((byte[]?)null);
        AssertEqual(0xFEFF, port.ReadScalar(), "the second mark is a character");
        AssertEqual((int)'a', port.ReadScalar(), "the character after it");
    }

    private static void ForeignBomRefused()
    {
        var cases = new (byte[] Bytes, string Name)[] {
            (Bytes(0xFF, 0xFE, 'a', 0), "UTF-16 (little-endian)"),
            (Bytes(0xFE, 0xFF, 0, 'a'), "UTF-16 (big-endian)"),
            (Bytes(0xFF, 0xFE, 0, 0, 'a', 0, 0, 0), "UTF-32 (little-endian)"),
            (Bytes(0, 0, 0xFE, 0xFF, 0, 0, 0, 'a'), "UTF-32 (big-endian)"),
        };

        foreach (var (bytes, name) in cases)
        {
            var port = Over(bytes, 2);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var line = port.ReadLineUtf8();
                    throw new AssertionException($"{name}, attempt {attempt}: expected a refusal, got <{line}>");
                }
                catch (InvalidDataException e)
                {
                    Assert(e.Message.Contains(name), $"{name}, attempt {attempt}: the message names it: {e.Message}");
                }
            }
        }

        // A mark cut short by the end of the input is only bytes, and invalid ones.
        AssertEqual("\uFFFD", Line(Over(Bytes(0xFF))), "a lone 0xFF");
    }

    private static void BomEncodingNames()
    {
        string? Named(params int[] head) =>
            BytePorts.BomEncoding(BjolangRuntime.Some(Bytes(head))) is { IsSome: true } e ? e.Value.WebName : null;

        AssertEqual("utf-8", Named(0xEF, 0xBB, 0xBF, 'a'), "UTF-8");
        AssertEqual("utf-16", Named(0xFF, 0xFE, 'a', 0), "UTF-16 little-endian");
        AssertEqual("utf-16BE", Named(0xFE, 0xFF, 0, 'a'), "UTF-16 big-endian");
        AssertEqual("utf-32", Named(0xFF, 0xFE, 0, 0), "UTF-32 little-endian");
        AssertEqual("utf-32BE", Named(0, 0, 0xFE, 0xFF), "UTF-32 big-endian");
        AssertEqual(null, Named('a', 'b'), "no mark");
        AssertEqual(null, BytePorts.BomEncoding(BjolangRuntime.None<byte[]>()).IsSome ? "some" : null, "no bytes");
    }

    // --- The UTF-16 view ---------------------------------------------------------

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

    private static void EofCases()
    {
        Assert(Over([]).Eof(), "empty input");
        Assert(Over(Bytes(0xEF, 0xBB, 0xBF)).EofAsync().AsTask().GetAwaiter().GetResult(), "a lone mark");
        Assert(!Over(Bytes(0xEF, 0xBB)).Eof(), "two bytes of a mark are text");
        AssertEqual("\uFFFD", Line(Over(Bytes(0xEF, 0xBB))), "and read as one replacement");
        AssertEqual(null, Line(Over(Bytes(0xEF, 0xBB, 0xBF))), "no line after a lone mark");
    }
}
