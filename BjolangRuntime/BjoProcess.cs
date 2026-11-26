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

// The three things `std/run` needs that the language cannot say for itself.
//
// **A pipe.** A command stage already has both ends — a child's `StandardInput`
// is a writer and its `StandardOutput` is a reader — but a stage written in
// Bjolang has neither, so `run` has to make them.
//
// `System.IO.Pipelines` rather than `AnonymousPipeServerStream`: an anonymous
// pipe is a pair of file descriptors, and a descriptor is inherited by every
// child started after it exists. .NET exposes no way to mark one close-on-exec
// or to filter what a child inherits, so a real pipe would leak into unrelated
// children — the same class of bug `run_tests.sh` works around with `9>&-`.
// This one is memory, and is nobody's business but ours.
//
// **Two read loops.** `ReadLineAsync` returns `null` at end of stream, and
// Bjolang has no null: the value would cross the boundary as a `string` that is
// not one. Keeping both loops on this side means the question never arises.
// The copy is a block at a time rather than a line, which matters — a
// line-oriented pump invents a trailing newline for a stream that ended without
// one.
//
// **`ArgumentList`.** It is a `Collection<string>`, and a constructed generic
// is type-only across the interop boundary, so its `Add` cannot be reached.

using System.IO.Pipelines;
using System.Text;

/// An output port and an input port joined end to end, for a pipeline stage
/// that is a Bjolang procedure rather than a child process.
[NonUserCode]
public sealed class BjoPipe {
    private readonly Bjolang.Runtime.BjoInputPort reader;
    private readonly Bjolang.Runtime.BjoOutputPort writer;

    private BjoPipe() {
        var pipe = new Pipe();
        reader = new Bjolang.Runtime.BjoInputPort(pipe.Reader.AsStream());
        // Unbuffered, because whatever reads the other end is a different
        // fiber and there is no later moment at which we would know to flush.
        //
        // Disposing either of these completes the underlying `Pipe` end —
        // `AsStream` defaults to `leaveOpen: false` — so `close-output-port` on
        // the writer is what makes the reader see end of stream. `std/run`
        // depends on that: it is how a closed stdin reaches the far end of a
        // filter.
        writer = new Bjolang.Runtime.BjoOutputPort(
            pipe.Writer.AsStream(), Bjolang.Runtime.BjoOutputPort.SmallBufferSize, ownsInner: true) {
            Mode = Bjolang.Runtime.BufferMode.None,
        };
    }

    public static BjoPipe Create() => new BjoPipe();

    public Bjolang.Runtime.BjoInputPort Reader => reader;
    public Bjolang.Runtime.BjoOutputPort Writer => writer;
}

/// The parts of running a pipeline that have no Bjolang spelling.
[NonUserCode]
public static class BjoProc {
    /// Worth the three lines: the alternative is `ProcessStartInfo.Arguments`,
    /// a single string that .NET re-splits with its own quoting rules, which
    /// would leave `std/run` *generating* those rules. Getting that subtly
    /// wrong is how an argument containing a space becomes two arguments.
    /// Here an argument is an argument.
    public static System.Diagnostics.ProcessStartInfo AddArgument(
            System.Diagnostics.ProcessStartInfo settings, string argument) {
        settings.ArgumentList.Add(argument);
        return settings;
    }

    /// Copy `from` into `to` until `from` ends, then close `to`.
    ///
    /// Closing is not optional, and that is the whole point of the loop being
    /// here: a junction that does not close leaves the process downstream
    /// waiting on a stdin that will never end, and the pipeline hangs with no
    /// indication of which stage is at fault. The one place a stream must *not*
    /// be closed on exhaustion is the pipeline's last reader, which belongs to
    /// whoever called `run` — and nothing pumps that.
    ///
    /// `from` is closed too, however the copy ends. Every caller hands over a
    /// stream nothing else reads: an upstream stage's output or a file opened
    /// for the redirect.
    ///
    /// A process that stops reading early (`head -n 1`) is an ordinary end of
    /// the copy, as it is in a shell, not a failure: the write into its input
    /// fails, the copy stops, and closing `from` lets the upstream process see
    /// a broken pipe and exit rather than block on a full one. A write that
    /// fails into anything else, such as a file, is still an error.
    public static Task PumpAsync(
            Bjolang.Runtime.BjoInputPort from, Bjolang.Runtime.BjoOutputPort to, CancellationToken cancel) =>
        Pump(from, to, null, cancel);

    /// `PumpAsync` between two stages. When the next stage stops reading,
    /// `link` records it, so that `std/run` does not count the upstream
    /// command's failure from the broken pipe as an error.
    public static Task PumpLinkAsync(
            Bjolang.Runtime.BjoInputPort from, Bjolang.Runtime.BjoOutputPort to, PipeLink link,
            CancellationToken cancel) =>
        Pump(from, to, link, cancel);

    private static async Task Pump(
            Bjolang.Runtime.BjoInputPort from, Bjolang.Runtime.BjoOutputPort to, PipeLink? link,
            CancellationToken cancel) {
        var buffer = new byte[8192];
        var source = from.AsStream();
        bool readerGone = false;
        try {
            while (true) {
                int n = await source.ReadAsync(buffer.AsMemory(), cancel).ConfigureAwait(false);
                if (n == 0) { break; }
                try {
                    await to.WriteBytesAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                } catch (IOException) when (IsProcessInput(to)) {
                    readerGone = true;
                    break;
                }
            }
            if (!readerGone) {
                try {
                    await to.FlushValueAsync(cancel).ConfigureAwait(false);
                } catch (IOException) when (IsProcessInput(to)) {
                    readerGone = true;
                }
            }
        } finally {
            if (readerGone) link?.MarkReaderGone();
            try {
                // Closing flushes what is still buffered, which fails again
                // once the reader is gone. The stream is closed either way.
                try { to.Dispose(); } catch (IOException) when (readerGone) { }
            } finally {
                from.Dispose();
            }
        }
    }

    /// Standard input of a started process: a pipe whose reader is the process.
    static bool IsProcessInput(Bjolang.Runtime.BjoOutputPort to) =>
        to.Inner is System.IO.Pipes.PipeStream;

    /// A number as it has to appear in an argv.
    ///
    /// Invariant, not `double.ToString()`: a command line is not a place for a
    /// locale. On a machine set to Swedish the ordinary conversion renders
    /// `0.5` as `0,5`, and `sleep 0,5` is an error rather than half a second.
    public static string Argument(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// Copy `from` into `to` until `from` ends, and leave `to` open.
    ///
    /// A separate method rather than a flag on `PumpAsync`, because the two
    /// differ in exactly the way a boolean argument hides. This one exists for
    /// one case: `errors-into-file`, where every command in a subtree pumps its
    /// standard error into a single file. The first pump to finish must not
    /// close what the others are still writing to, so closing is somebody
    /// else's job — `std/run` joins all of them and closes once.
    ///
    /// The pumps run concurrently into one port, which takes each write whole.
    public static async Task PumpIntoAsync(
            Bjolang.Runtime.BjoInputPort from, Bjolang.Runtime.BjoOutputPort to, CancellationToken cancel) {
        var buffer = new byte[8192];
        var source = from.AsStream();
        while (true) {
            int n = await source.ReadAsync(buffer.AsMemory(), cancel).ConfigureAwait(false);
            if (n == 0) { break; }
            await to.WriteBytesAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
        }
        await to.FlushValueAsync(cancel).ConfigureAwait(false);
    }

    /// `PumpIntoAsync`, and the bytes copied are also kept in `tail`.
    public static async Task PumpIntoTailAsync(
            Bjolang.Runtime.BjoInputPort from, Bjolang.Runtime.BjoOutputPort to, ErrorTail tail,
            CancellationToken cancel) {
        var buffer = new byte[8192];
        var source = from.AsStream();
        while (true) {
            int n = await source.ReadAsync(buffer.AsMemory(), cancel).ConfigureAwait(false);
            if (n == 0) { break; }
            tail.Add(buffer.AsSpan(0, n));
            await to.WriteBytesAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
        }
        await to.FlushValueAsync(cancel).ConfigureAwait(false);
    }

    /// Every line of `from`, for `run/strings`.
    ///
    /// An array rather than a Bjolang `Vec`, which has no construction path
    /// from here; `std/run` converts. Splitting the text afterwards instead
    /// would need a substring, which the language does not have, and would have
    /// to re-decide what `\r\n` and a trailing newline mean — questions
    /// `TextReader` has already answered.
    public static async Task<string[]> ReadLinesAsync(Bjolang.Runtime.BjoInputPort from, CancellationToken cancel) {
        var lines = new List<string>();
        while (await from.ReadLineAsync(cancel).ConfigureAwait(false) is string line) {
            lines.Add(line);
        }
        return lines.ToArray();
    }
}

/// The last bytes one command wrote to its standard error, for the message of
/// a `RunError`. `std/run` keeps one for each command whose standard error it
/// copies.
///
/// - Holds the last `Size` bytes. Older bytes are dropped.
/// - `Add` and `Text` can run on different threads.
[NonUserCode]
public sealed class ErrorTail {
    public const int Size = 2048;

    private readonly byte[] ring = new byte[Size];
    private long total;
    private readonly object gate = new();

    public static ErrorTail Create() => new ErrorTail();

    public void Add(ReadOnlySpan<byte> bytes) {
        lock (gate) {
            if (bytes.Length >= Size) bytes = bytes[^Size..];
            foreach (var b in bytes) {
                ring[(int)(total % Size)] = b;
                total++;
            }
        }
    }

    /// The kept bytes as UTF-8 text, without the whitespace at the end. When
    /// bytes were dropped, the text starts after the first line end, so that
    /// it does not start in the middle of a line. "" when nothing was kept.
    public string Text() {
        byte[] kept;
        lock (gate) {
            int n = (int)System.Math.Min(total, Size);
            kept = new byte[n];
            long start = total - n;
            for (int i = 0; i < n; i++) kept[i] = ring[(int)((start + i) % Size)];
        }
        int from = 0;
        if (total > Size) {
            int newline = System.Array.IndexOf(kept, (byte)'\n');
            from = newline >= 0 && newline < kept.Length - 1 ? newline + 1 : 0;
        }
        return Encoding.UTF8.GetString(kept, from, kept.Length - from).TrimEnd();
    }
}

/// The pipe from one command's output to the next stage's input. Records
/// whether the next stage stopped reading before the command stopped
/// writing.
///
/// .NET ignores SIGPIPE, and a child inherits that. So a command whose reader
/// has gone gets EPIPE and exits with an ordinary error code, not with 141.
[NonUserCode]
public sealed class PipeLink {
    private volatile bool readerGone;

    public static PipeLink Create() => new PipeLink();

    public bool ReaderGone => readerGone;

    internal void MarkReaderGone() => readerGone = true;
}
