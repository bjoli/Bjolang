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

/// A `TextWriter` and a `TextReader` joined end to end, for a pipeline stage
/// that is a Bjolang procedure rather than a child process.
public sealed class BjoPipe {
    private readonly TextReader reader;
    private readonly TextWriter writer;

    private BjoPipe() {
        var pipe = new Pipe();
        // Not `Encoding.UTF8`: its preamble would be written into the stream
        // and read back out of the other end as a character.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        reader = new StreamReader(pipe.Reader.AsStream(), encoding);
        // `AutoFlush`, because whatever reads the other end is a different
        // fiber and there is no later moment at which we would know to flush.
        //
        // Disposing either of these completes the underlying `Pipe` end —
        // `AsStream` defaults to `leaveOpen: false` — so `close-output-port` on
        // the writer is what makes the reader see end of stream. `std/run`
        // depends on that: it is how a closed stdin reaches the far end of a
        // filter.
        writer = new StreamWriter(pipe.Writer.AsStream(), encoding) { AutoFlush = true };
    }

    public static BjoPipe Create() => new BjoPipe();

    public TextReader Reader => reader;
    public TextWriter Writer => writer;
}

/// The parts of running a pipeline that have no Bjolang spelling.
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
    public static async Task PumpAsync(TextReader from, TextWriter to, CancellationToken cancel) {
        var buffer = new char[8192];
        try {
            while (true) {
                int n = await from.ReadAsync(buffer.AsMemory(), cancel).ConfigureAwait(false);
                if (n == 0) { break; }
                await to.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
            }
            await to.FlushAsync(cancel).ConfigureAwait(false);
        } finally {
            to.Dispose();
        }
    }

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
    /// `to` is expected to be a `TextWriter.Synchronized` wrapper, since the
    /// pumps run concurrently.
    public static async Task PumpIntoAsync(TextReader from, TextWriter to, CancellationToken cancel) {
        var buffer = new char[8192];
        while (true) {
            int n = await from.ReadAsync(buffer.AsMemory(), cancel).ConfigureAwait(false);
            if (n == 0) { break; }
            await to.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
        }
        await to.FlushAsync(cancel).ConfigureAwait(false);
    }

    /// Block the calling thread until a spawned bjoroutine lands, rethrowing
    /// whatever it raised.
    ///
    /// This is what lets `std/run`'s waiting be ordinary functions rather than
    /// bjoroutines, so that a plain `defun` may shell out. The pumps themselves
    /// stay fibers — `Bjo.Spawn` enqueues them on the pool, which needs nobody
    /// driving it — and this waits for them from outside.
    ///
    /// The monitor, rather than a `ManualResetEventSlim`, is `Bjo.RunToCompletion`'s
    /// own shape: the completing thread must not be able to touch a disposed
    /// handle after we wake.
    ///
    /// Carries `Bjo.RunToCompletion`'s warning with it: **do not call this on a
    /// thread-pool thread.** The fibers being waited for need pool threads to
    /// finish, and this one is holding one. From inside a fiber, reach the
    /// blocking entry points through `(blocking ...)`, which moves the parking
    /// to a thread the pool can grow to cover.
    public static T Await<T>(Bjoml.Promise<T> promise) {
        var gate = new object();
        bool landed = false;

        promise.GetAwaiter().OnCompleted(() => {
            lock (gate) {
                landed = true;
                Monitor.Pulse(gate);
            }
        });

        lock (gate) {
            while (!landed) { Monitor.Wait(gate); }
        }

        return promise.GetAwaiter().GetResult();
    }

    /// Every line of `from`, blocking. The counterpart of `ReadLinesAsync`, for
    /// the same reason `Await` exists.
    public static string[] ReadLines(TextReader from) {
        var lines = new List<string>();
        while (from.ReadLine() is string line) {
            lines.Add(line);
        }
        return lines.ToArray();
    }

    /// Every line of `from`, for `run/strings`.
    ///
    /// An array rather than a Bjolang `Vec`, which has no construction path
    /// from here; `std/run` converts. Splitting the text afterwards instead
    /// would need a substring, which the language does not have, and would have
    /// to re-decide what `\r\n` and a trailing newline mean — questions
    /// `TextReader` has already answered.
    public static async Task<string[]> ReadLinesAsync(TextReader from, CancellationToken cancel) {
        var lines = new List<string>();
        while (await from.ReadLineAsync(cancel).ConfigureAwait(false) is string line) {
            lines.Add(line);
        }
        return lines.ToArray();
    }
}
