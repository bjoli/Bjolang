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

// The output port: bytes, and text written to it as UTF-8. Text, characters
// and bytes mixed on one port; .NET's UTF-16 writes, a surrogate pair split
// between two of them included; the three buffer modes; many threads writing
// to one port; re-encoding; a .NET TextWriter made a port.
//
// `BytePortTests.cs` has the claims about closing over a stream that refuses
// blocking I/O.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Bjoml.Tests.Harness;
using Bjolang.Runtime;
using BjoString;

namespace Bjoml.Tests;

public static class OutputPortTests
{
    public static void RunAll()
    {
        Section("Output ports");
        Run("text, characters and bytes mix on one port", MixedWrites);
        Run("a string port answers its bytes and its text", StringPort);
        Run(".NET's UTF-16 writes are UTF-8 on the port, pairs split or not", Utf16Writes);
        Run("a lone surrogate is U+FFFD, also when bytes come after it", LoneSurrogate);
        Run("block mode writes when the buffer fills or on a flush", BlockMode);
        Run("line mode writes at a newline", LineMode);
        Run("no buffering writes every write", NoBuffering);
        Run("a write larger than the buffer keeps its order", LargeWrite);
        Run("eight threads writing lines never tear one", ThreadsWriteWholeLines);
        Run("blocking and suspending writers that wait for the gate tear no line", WaitersWriteWholeLines);
        Run("a re-encoded port writes its encoding into the port under it", Reencoded);
        Run("a .NET TextWriter made a port gets whole characters", OverTextWriter);
        Run("a write after shutdown is refused", WriteAfterShutdown);
    }

    private static void AssertEqual<T>(T expected, T actual, string what) =>
        Assert(Equals(expected, actual), $"{what}: expected {expected}, got {actual}");

    private static Utf8String U(string s) => Utf8String.FromUtf16(s);

    private static void MixedWrites()
    {
        var port = new BjoOutputPort();
        port.WriteText(U("räk"));
        port.WriteScalar(new BjoChar(0x1F600));
        port.WriteBytes(new byte[] { (byte)'!' });
        port.WriteTextLine(U("ok"));
        port.Write("ж");
        AssertEqual("räk😀!ok\nж", port.GetString().ToString(), "the text");
        AssertEqual(Convert.ToHexString(Encoding.UTF8.GetBytes("räk😀!ok\nж")),
                    Convert.ToHexString(port.GetBytes()), "the bytes");
    }

    private static void StringPort()
    {
        var port = new BjoOutputPort();
        AssertEqual("", port.GetString().ToString(), "empty");
        var big = new string('x', 100_000);
        port.WriteText(U(big));
        AssertEqual(100_000, port.GetBytes().Length, "grows as it is written");
        port.WriteBytes(new byte[] { 0xFF });
        AssertEqual(big + "\uFFFD", port.GetString().ToString(), "a byte that is not UTF-8 reads as U+FFFD");
        AssertEqual(true, port.IsMemory, "is a string port");
    }

    private static void Utf16Writes()
    {
        var port = new BjoOutputPort();
        const string text = "a😀b";
        foreach (char c in text) port.Write(c);
        port.Write("ö😀".AsSpan());
        AssertEqual("a😀bö😀", port.GetString().ToString(), "unit by unit and as a span");
    }

    private static void LoneSurrogate()
    {
        var port = new BjoOutputPort();
        port.Write('\uD83D');
        port.WriteBytes(new byte[] { (byte)'x' });
        port.Write('\uDE00');
        AssertEqual("\uFFFDx\uFFFD", port.GetString().ToString(), "neither half is a character on its own");
    }

    private static void BlockMode()
    {
        var stream = new MemoryStream();
        var port = new BjoOutputPort(stream, 16, ownsInner: false) { Mode = BufferMode.Block };
        port.WriteText(U("hello\n"));
        AssertEqual(0L, stream.Length, "a newline does not flush");
        port.WriteText(U("0123456789abcdef"));
        Assert(stream.Length > 0, "a full buffer drains");
        port.Flush();
        AssertEqual("hello\n0123456789abcdef", Encoding.UTF8.GetString(stream.ToArray()), "a flush writes the rest");
    }

    private static void LineMode()
    {
        var stream = new MemoryStream();
        var port = new BjoOutputPort(stream, 1024, ownsInner: false) { Mode = BufferMode.Line };
        port.WriteText(U("prompt> "));
        AssertEqual(0L, stream.Length, "no newline, nothing written");
        port.WriteTextLine(U("answer"));
        AssertEqual("prompt> answer\n", Encoding.UTF8.GetString(stream.ToArray()), "the newline writes the line");
        port.Write("half");
        AssertEqual(15L, stream.Length, "a .NET write without a newline waits too");
        port.WriteLine();
        AssertEqual("prompt> answer\nhalf\n", Encoding.UTF8.GetString(stream.ToArray()), "and goes with its newline");
    }

    private static void NoBuffering()
    {
        var stream = new MemoryStream();
        var port = new BjoOutputPort(stream, 1024, ownsInner: false) { Mode = BufferMode.None };
        port.WriteText(U("a"));
        AssertEqual(1L, stream.Length, "the first write");
        port.WriteScalar(new BjoChar('b'));
        port.WriteBytes(new byte[] { (byte)'c' });
        AssertEqual("abc", Encoding.UTF8.GetString(stream.ToArray()), "every write");
        port.WriteBytesAsync(new byte[] { (byte)'d' }).AsTask().GetAwaiter().GetResult();
        AssertEqual("abcd", Encoding.UTF8.GetString(stream.ToArray()), "and the asynchronous ones");
    }

    private static void LargeWrite()
    {
        var stream = new MemoryStream();
        var port = new BjoOutputPort(stream, 16, ownsInner: false);
        port.WriteText(U("head:"));
        var big = Enumerable.Range(0, 1000).Select(i => (byte)('a' + i % 26)).ToArray();
        port.WriteBytes(big);
        port.WriteText(U(":tail"));
        port.WriteBytesAsync(big).AsTask().GetAwaiter().GetResult();
        port.Flush();
        string expected = "head:" + Encoding.ASCII.GetString(big) + ":tail" + Encoding.ASCII.GetString(big);
        AssertEqual(expected, Encoding.UTF8.GetString(stream.ToArray()), "in order");
    }

    /// A stream that takes its time, so that a write which drains holds the
    /// gate across an await and the other writers have to wait for it.
    private sealed class SlowStream : Stream
    {
        public readonly MemoryStream Written = new();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(1);
            lock (Written) Written.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default)
        {
            await Task.Delay(1, cancel).ConfigureAwait(false);
            lock (Written) Written.Write(buffer.Span);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static void WaitersWriteWholeLines()
    {
        var slow = new SlowStream();
        var port = new BjoOutputPort(slow, 64, ownsInner: false);
        const int PerWriter = 200;

        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            var line = Encoding.UTF8.GetBytes($"writer {w} {new string((char)('a' + w), 40)}\n");
            for (int i = 0; i < PerWriter; i++)
            {
                if (w % 2 == 0) port.WriteBytes(line);
                else await port.WriteBytesAsync(line);
            }
        })).ToArray();

        Assert(Task.WaitAll(writers, 30000), "every writer finishes");
        port.Flush();

        var lines = Encoding.UTF8.GetString(slow.Written.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        AssertEqual(8 * PerWriter, lines.Length, "every line is written");
        foreach (var l in lines)
        {
            int w = l[7] - '0';
            AssertEqual($"writer {w} {new string((char)('a' + w), 40)}", l, "a whole line");
        }
    }

    private static void ThreadsWriteWholeLines()
    {
        var port = new BjoOutputPort();
        const int threads = 8, lines = 5000;
        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(async () =>
        {
            for (int i = 0; i < lines; i++)
            {
                var line = U($"thread {t} line {i} " + new string((char)('a' + t), 40));
                if (i % 2 == 0) port.WriteTextLine(line);
                else await port.WriteTextLineAsync(line);
            }
        })).ToArray();
        Task.WaitAll(tasks);
        var all = port.GetString().ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        AssertEqual(threads * lines, all.Length, "every line");
        foreach (var l in all)
        {
            var parts = l.Split(' ');
            int t = int.Parse(parts[1]);
            Assert(parts.Length == 5 && parts[4] == new string((char)('a' + t), 40), $"a torn line: {l}");
        }
    }

    private static void Reencoded()
    {
        var under = new BjoOutputPort();
        var utf16 = OutputPorts.Reencode(under, Encodings.Cp1201);
        utf16.WriteText(U("hé😀"));
        utf16.Dispose();
        AssertEqual(Convert.ToHexString(new UnicodeEncoding(true, false).GetBytes("hé😀")),
                    Convert.ToHexString(under.GetBytes()), "UTF-16BE bytes in the port under it");
        under.WriteText(U("!"));
        AssertEqual((byte)'!', under.GetBytes()[^1], "the port under it is still open");
    }

    private static void OverTextWriter()
    {
        var sw = new StringWriter();
        var port = OutputPorts.FromTextWriter(sw);
        var bytes = Encoding.UTF8.GetBytes("räk😀");
        // Cut inside ä and inside the emoji.
        port.WriteBytes(bytes.AsSpan(0, 2));
        port.WriteBytes(bytes.AsSpan(2, 4));
        port.WriteBytes(bytes.AsSpan(6));
        AssertEqual("räk😀", sw.ToString(), "whole characters");
        Assert(ReferenceEquals(port, OutputPorts.FromTextWriter(port)), "a port is handed back as it is");
        port.Dispose();
        sw.Write("still open");
        AssertEqual("räk😀still open", sw.ToString(), "closing the port leaves the writer open");
    }

    private static void WriteAfterShutdown()
    {
        var stream = new MemoryStream();
        var port = new BjoOutputPort(stream, 64, ownsInner: false);
        port.WriteText(U("bye"));
        port.Shutdown();
        AssertEqual("bye", Encoding.UTF8.GetString(stream.ToArray()), "the shutdown flushed");
        try
        {
            port.WriteText(U("more"));
            throw new AssertionException("a write after shutdown was taken");
        }
        catch (InvalidOperationException) { /* as it should */ }
    }
}
