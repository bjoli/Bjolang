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

// `BjoPort`, the text input port, makes the byte port's promises: a cancelled
// read consumes nothing, a fill is the port's and at most one is in flight,
// two readers each get whole lines and every line exactly once, and a failure
// is sticky and reported after the lines that arrived before it.
//
// The cancellation cases are the three shapes that used to lose text: a line
// longer than what was buffered, a `\r` that was the last character buffered,
// and `read-all` gathering across fills.

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

public static class TextPortTests
{
    public static void RunAll()
    {
        Section("Text ports");
        Run("a cancelled read-line keeps the partial line", CancelledLineKeepsPartial);
        Run("a cancelled read-line after a final \\r keeps the line", CancelledLineAfterCr);
        Run("a cancelled read-all keeps what it gathered", CancelledReadAllKeepsText);
        Run("a line longer than the buffer survives a cancellation", LongLineSurvivesCancel);
        Run("at most one fill is ever in flight", OneFillAtATime);
        Run("two readers get every line exactly once", TwoReadersEveryLineOnce);
        Run("a failure is sticky, after the lines before it", FailureIsStickyAfterLines);
        Run("\\r\\n split across fills is one terminator", CrLfAcrossFills);
        Run("read-char/opt across waiting fills hands out every character once", CharsAcrossWaitingFills);
        Run("a surrogate pair split across fills is one character", SurrogatePairAcrossFills);
    }

    /// A reader fed by the test: each `Feed` is what one read hands over, and a
    /// read with nothing fed waits. It ignores the token, which is the case a
    /// port must not depend on, and it counts how many reads are in flight.
    private sealed class FeedReader : TextReader
    {
        private readonly ConcurrentQueue<string?> _chunks = new();
        private readonly SemaphoreSlim _ready = new(0);
        private string _rest = "";
        private int _inFlight;
        private int _max;

        public int MaxConcurrent => Volatile.Read(ref _max);

        /// Null is end of input.
        public void Feed(string? chunk)
        {
            _chunks.Enqueue(chunk);
            _ready.Release();
        }

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancel = default)
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

        public override int Read(char[] buffer, int index, int count) =>
            ReadAsync(buffer.AsMemory(index, count)).AsTask().GetAwaiter().GetResult();
    }

    /// A reader that hands over its text and then fails.
    private sealed class FailingReader : TextReader
    {
        private string _text;
        public FailingReader(string text) => _text = text;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancel = default)
        {
            if (_text.Length == 0) throw new IOException("the wire broke");
            int n = Math.Min(buffer.Length, _text.Length);
            _text.AsSpan(0, n).CopyTo(buffer.Span);
            _text = _text[n..];
            return new ValueTask<int>(n);
        }

        public override int Read(char[] buffer, int index, int count) =>
            ReadAsync(buffer.AsMemory(index, count)).AsTask().GetAwaiter().GetResult();
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

    private static string? Line(BjoPort p) =>
        p.ReadLineValueAsync().AsTask().GetAwaiter().GetResult();

    // -----------------------------------------------------------------------

    private static void CancelledLineKeepsPartial()
    {
        var feed = new FeedReader();
        var port = new BjoPort(feed);

        feed.Feed("abc");
        Cancelled(c => port.ReadLineValueAsync(c).AsTask(), "read-line with half a line");

        feed.Feed("def\n");
        AssertEqual("abcdef", Line(port), "the line after the cancelled read");
    }

    private static void CancelledLineAfterCr()
    {
        var feed = new FeedReader();
        var port = new BjoPort(feed);

        // The line is complete, but whether its `\r` is a `\r\n` is not known
        // until the next fill, and that fill is the one cancelled.
        feed.Feed("line1\r");
        Cancelled(c => port.ReadLineValueAsync(c).AsTask(), "read-line ending at a \\r");

        feed.Feed("line2\n");
        AssertEqual("line1", Line(port), "the first line");
        AssertEqual("line2", Line(port), "the second line");
    }

    private static void CancelledReadAllKeepsText()
    {
        var feed = new FeedReader();
        var port = new BjoPort(feed);

        feed.Feed("hello ");
        Cancelled(c => port.ReadToEndAsync(c), "read-all before the end");

        feed.Feed("world");
        feed.Feed(null);
        AssertEqual("hello world", port.ReadToEndAsync().GetAwaiter().GetResult(), "read-all after it");
    }

    private static void LongLineSurvivesCancel()
    {
        var feed = new FeedReader();
        var port = new BjoPort(feed, 4);

        feed.Feed("0123456789");
        feed.Feed("abcdefghij");
        Cancelled(c => port.ReadLineValueAsync(c).AsTask(), "read-line of a line five buffers long");

        feed.Feed("ABCDEFGHIJ\nnext\n");
        AssertEqual("0123456789abcdefghijABCDEFGHIJ", Line(port), "the long line");
        AssertEqual("next", Line(port), "the line after it");
    }

    private static void OneFillAtATime()
    {
        var feed = new FeedReader();
        var port = new BjoPort(feed);

        // Three readers parked on an empty port: one fill between them.
        var readers = Enumerable.Range(0, 3).Select(_ => port.ReadLineValueAsync().AsTask()).ToArray();
        Thread.Sleep(50);
        AssertEqual(1, feed.MaxConcurrent, "reads in flight on the inner reader");

        feed.Feed("a\nb\nc\n");
        var lines = readers.Select(t => t.GetAwaiter().GetResult()).OrderBy(l => l).ToArray();
        AssertEqual("a,b,c", string.Join(",", lines), "what the three readers got");
    }

    private static void TwoReadersEveryLineOnce()
    {
        const int count = 200_000;
        var text = new StringBuilder();
        for (int i = 0; i < count; i++) text.Append("line-").Append(i).Append('\n');

        // A small buffer, so that the readers meet at fills constantly.
        var port = new BjoPort(new StringReader(text.ToString()), 64);
        var seen = new ConcurrentBag<string>();

        async Task Drain()
        {
            await Task.Yield();
            while (await port.ReadLineValueAsync().ConfigureAwait(false) is { } line) seen.Add(line);
        }

        Task.WaitAll(Drain(), Drain(), Drain(), Drain());

        AssertEqual(count, seen.Count, "lines handed out");
        var distinct = new HashSet<string>(seen);
        AssertEqual(count, distinct.Count, "distinct lines");
        for (int i = 0; i < count; i += 9973)
            Assert(distinct.Contains($"line-{i}"), $"line-{i} was handed out intact");
    }

    private static void FailureIsStickyAfterLines()
    {
        var port = new BjoPort(new FailingReader("one\ntwo\nthr"), 64);

        AssertEqual("one", Line(port), "the first line");
        AssertEqual("two", Line(port), "the second line");

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
    }

    /// The `read-char/opt` dispatcher, reading while the feed hands text over a
    /// little at a time, so that most reads have to wait for a fill. A read
    /// that waited must be the read that completes: asking the port a second
    /// time would let the first take a character nobody receives.
    private static void CharsAcrossWaitingFills()
    {
        var feed = new FeedReader();
        var port = new BjoPort(feed, 8);
        var expected = string.Concat(Enumerable.Range(0, 400).Select(i => $"{i},"));

        var feeder = Task.Run(async () =>
        {
            for (int i = 0; i < expected.Length; i += 7)
            {
                await Task.Delay(1).ConfigureAwait(false);
                feed.Feed(expected.Substring(i, Math.Min(7, expected.Length - i)));
            }
            feed.Feed(null);
        });

        var got = new StringBuilder();
        while (true)
        {
            var c = BjoPort.ReadCharOptAsync(port).AsTask().GetAwaiter().GetResult();
            if (!c.IsSome) break;
            got.Append((char)c.Value.Value);
        }

        feeder.GetAwaiter().GetResult();
        AssertEqual(expected, got.ToString(), "what read-char/opt handed out");
    }

    private static void SurrogatePairAcrossFills()
    {
        var feed = new FeedReader();
        var port = new BjoPort(feed);
        var smile = char.ConvertFromUtf32(0x1F600);

        feed.Feed("a" + smile[0]);
        feed.Feed(smile[1] + "b");
        feed.Feed(null);

        var chars = new List<uint>();
        while (BjoPort.ReadCharOptAsync(port).AsTask().GetAwaiter().GetResult() is { IsSome: true } c)
            chars.Add(c.Value.Value);

        AssertEqual("61,1F600,62", string.Join(",", chars.Select(c => c.ToString("X"))), "the characters");
    }

    private static void CrLfAcrossFills()
    {
        var feed = new FeedReader();
        var port = new BjoPort(feed);

        feed.Feed("a\r");
        feed.Feed("\nb\r\n");
        feed.Feed(null);

        AssertEqual("a", Line(port), "the line before the split \\r\\n");
        AssertEqual("b", Line(port), "the line after it");
        AssertEqual(null, Line(port), "end of input");
    }
}
