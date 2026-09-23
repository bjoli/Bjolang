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

// The four claims about `BjoByteInputPort` that Bjolang cannot check from the
// outside, each of them about WHICH PATH was taken rather than about what came
// back.
//
// `TestFiles/231_byte_ports.bjo` covers the behaviour: reads, peeks, the
// half-close, the text layer, and the racy form of "a losing branch consumes
// nothing" over three hundred rounds. What is here is the part that needs to
// see inside — the publish counter, the stream's own concurrency, and a loser
// arranged to lose deterministically rather than by luck.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using static Bjoml.Tests.Harness;
using Bjolang.Runtime;
using ByteRead = BjolangRuntime.Result<System.Exception, BjolangRuntime.Option<byte[]>>;

namespace Bjoml.Tests;

public static class BytePortTests
{
    public static void RunAll()
    {
        Section("Byte ports");
        Run("a buffered read commits without publishing", TryNowIsTheFastPath);
        Run("a losing branch consumes nothing", LoserConsumesNothing);
        Run("at most one refill is ever in flight", OneRefillAtATime);
        Run("a cancelled wait does not cancel the refill", CancelledWaitKeepsTheBytes);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static byte[] Bytes(ByteRead r, string what)
    {
        if (!r.IsOk) throw new AssertionException($"{what}: the read failed with {r.ErrValue}");
        if (!r.OkValue.IsSome) throw new AssertionException($"{what}: the read was at end of input");
        return r.OkValue.Value;
    }

    private static string Show(byte[] bytes) => string.Join(".", bytes);

    /// A stream that hands nothing over until it is told to, so that a reader
    /// can be parked on a refill at a known moment.
    private sealed class GatedStream : Stream
    {
        private readonly byte[] _data;
        private readonly TaskCompletionSource _open =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _pos;

        public GatedStream(byte[] data) => _data = data;

        public void Open() => _open.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            await _open.Task.ConfigureAwait(false);

            int n = Math.Min(buffer.Length, _data.Length - _pos);
            _data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) =>
            ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();

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

    /// A stream that records how many reads were ever in flight at once, and
    /// that always takes a moment, so that a second reader arriving has
    /// somewhere to arrive.
    private sealed class CountingStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _pos;
        private int _inFlight;
        private int _max;

        public CountingStream(byte[] data, int chunk)
        {
            _data = data;
            _chunk = chunk;
        }

        public int MaxConcurrent => Volatile.Read(ref _max);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        {
            int now = Interlocked.Increment(ref _inFlight);

            int seen;
            while (now > (seen = Volatile.Read(ref _max)))
                if (Interlocked.CompareExchange(ref _max, now, seen) == seen)
                    break;

            try
            {
                await Task.Delay(1, cancel).ConfigureAwait(false);

                int n = Math.Min(Math.Min(buffer.Length, _chunk), _data.Length - _pos);
                _data.AsSpan(_pos, n).CopyTo(buffer.Span);
                _pos += n;
                return n;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) =>
            ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();

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

    // -----------------------------------------------------------------------
    // The fast path
    // -----------------------------------------------------------------------

    /// <summary>
    /// `INowable.TryNow` answering true PERFORMS the rendezvous, so a read the
    /// buffer can already answer must never reach `Publish` at all.
    ///
    /// Nothing visible from Bjolang distinguishes the two paths — both hand
    /// back the same bytes — so the port counts its publishes and this looks at
    /// the count. The `sync` used here is the language's own
    /// (`BjolangRuntime.sync`), because that is where `INowable` is consulted;
    /// `Cml.Sync` always publishes, by design.
    /// </summary>
    private static void TryNowIsTheFastPath()
    {
        var port = new BjoByteInputPort(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6 }));

        // A peek forces the refill and leaves the cursor alone, so the buffer
        // now holds everything and no read after this needs the stream.
        var peeked = port.Peek(0, 1);
        Assert(peeked.IsSome, "the peek brought nothing in");
        AssertEqual(0, port.PublishCount, "a peek published something");

        var ev = BytePorts.ReadBytesEvent(port, 2);

        var first = BjolangRuntime.sync(ev).GetAwaiter();
        Assert(first.IsCompleted, "a read the buffer could answer did not complete inline");
        AssertEqual("1.2", Show(Bytes(first.GetResult(), "the first read")), "the first read");
        AssertEqual(0, port.PublishCount, "a buffered read published instead of taking the fast path");

        // Answering true committed, so the same event asked again is at the
        // next two bytes rather than at the same two.
        var second = BjolangRuntime.sync(ev).GetAwaiter();
        Assert(second.IsCompleted, "the second buffered read did not complete inline");
        AssertEqual("3.4", Show(Bytes(second.GetResult(), "the second read")), "the second read");
        AssertEqual(0, port.PublishCount, "the second buffered read published");

        // And a read the buffer CANNOT answer declines the fast path and does
        // publish, which is what makes the three zeroes above mean anything.
        var big = BytePorts.ReadBytesEvent(port, 64);
        Assert(!((INowable<ByteRead>)big).TryNow(out _),
            "a read the buffer could not answer claimed the fast path");

        var landed = new ManualResetEventSlim(false);
        ByteRead last = default;
        Cml.Sync(big, v => { last = v; landed.Set(); });
        Await(landed, "the short read to land");

        AssertEqual("5.6", Show(Bytes(last, "the short read")), "the short read");
        Assert(port.PublishCount > 0, "a read that had to wait never published");

        port.Dispose();
    }

    // -----------------------------------------------------------------------
    // The loser
    // -----------------------------------------------------------------------

    /// <summary>
    /// The deterministic form of the property the whole design is for.
    ///
    /// The read is published first and parks, because the stream is gated; the
    /// sibling is an `Always` and commits at once; only then is the gate opened,
    /// so the refill lands for a branch that has already lost. Nothing may have
    /// been consumed: every byte is still there for the next reader.
    ///
    /// The racy form of this — where the refill and the sibling land together —
    /// is `TestFiles/231_byte_ports.bjo`, three hundred rounds of it.
    /// </summary>
    private static void LoserConsumesNothing()
    {
        var gate = new GatedStream(new byte[] { 10, 20, 30, 40 });
        var port = new BjoByteInputPort(gate);

        var read = Cml.Wrap(BytePorts.ReadBytesEvent(port, 4), static _ => "read");
        var other = Cml.Always("other");

        string? won = null;
        var landed = new ManualResetEventSlim(false);

        Cml.Sync(Cml.Choose(read, other), w => { won = w; landed.Set(); });

        Await(landed, "the choose to settle");
        AssertEqual("other", won, "the gated read won a choose it could not have answered");

        // The refill was never cancelled, and it lands now — for a branch that
        // is long gone.
        gate.Open();

        var buf = new byte[4];
        int n = port.ReadInto(buf);
        while (n < 4)
        {
            int more = port.ReadInto(buf.AsSpan(n));
            if (more == 0) break;
            n += more;
        }

        AssertEqual(4, n, "the losing branch swallowed bytes");
        AssertEqual("10.20.30.40", Show(buf), "the bytes after the loser");

        port.Dispose();
    }

    // -----------------------------------------------------------------------
    // One refill
    // -----------------------------------------------------------------------

    /// <summary>
    /// Two concurrent `ReadAsync` calls on one `Stream` is undefined behaviour
    /// in .NET, so a second reader has to join the refill in flight rather than
    /// start another. The stream counts, which is the only place the answer is
    /// visible.
    ///
    /// The tally beside it is the other half of the same claim: serializing on
    /// one refill must not lose or duplicate a byte.
    /// </summary>
    private static void OneRefillAtATime()
    {
        const int total = 256;
        var data = new byte[total];
        for (int i = 0; i < total; i++) data[i] = (byte)i;

        var stream = new CountingStream(data, chunk: 8);
        var port = new BjoByteInputPort(stream, bufferSize: 8);

        var seen = new int[total];
        var readers = new Thread[4];
        Exception? failure = null;

        for (int t = 0; t < readers.Length; t++)
        {
            readers[t] = new Thread(() =>
            {
                try
                {
                    var one = new byte[1];
                    while (port.ReadInto(one) == 1) Interlocked.Increment(ref seen[one[0]]);
                }
                catch (Exception e) { failure = e; }
            }) { IsBackground = true };
        }

        foreach (var r in readers) r.Start();
        foreach (var r in readers) Assert(r.Join(8000), "a reader never finished");

        if (failure is not null) throw new AssertionException($"a reader failed: {failure}");

        AssertEqual(1, stream.MaxConcurrent, "more than one refill was in flight at once");

        for (int i = 0; i < total; i++)
            AssertEqual(1, seen[i], $"byte {i} was handed out the wrong number of times");

        port.Dispose();
    }

    // -----------------------------------------------------------------------
    // The refill outlives the wait
    // -----------------------------------------------------------------------

    /// <summary>
    /// A cancellation token abandons the WAIT, not the refill. Bytes
    /// `ReadAsync` has already taken from the kernel cannot be put back, so a
    /// cancelled refill would be lost data — which under a `choose` is silent
    /// corruption visible only under load.
    /// </summary>
    private static void CancelledWaitKeepsTheBytes()
    {
        var gate = new GatedStream(new byte[] { 7, 8, 9 });
        var port = new BjoByteInputPort(gate);

        var cancel = new CancellationTokenSource();
        var pending = port.ReadIntoAsync(new byte[3], cancel.Token).AsTask();

        Assert(!pending.Wait(100), "a read of a gated stream completed");
        cancel.Cancel();

        try
        {
            pending.GetAwaiter().GetResult();
            throw new AssertionException("the cancelled wait did not throw");
        }
        catch (OperationCanceledException) { /* as it should */ }

        // The refill was never told about the token, so it is still coming.
        gate.Open();

        var buf = new byte[3];
        int n = port.ReadInto(buf);
        AssertEqual(3, n, "the cancelled wait took bytes with it");
        AssertEqual("7.8.9", Show(buf), "the bytes after a cancelled wait");

        port.Dispose();
    }
}
