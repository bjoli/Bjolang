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

using System.Runtime.InteropServices;
using Bjoml;

namespace Bjolang.Runtime;

/// <summary>
/// How a program's `main` ends when it is interrupted, killed, or fails while
/// fibers still run. Used by `RunMainSync` and `RunMainFiber` only: not by a
/// REPL session or a hosted `main`.
///
/// Signals:
/// - SIGINT, SIGTERM and SIGHUP flush standard output first. .NET raises no
///   `ProcessExit` for them, so without this a redirected program that is
///   killed loses what it had written.
/// - The first SIGINT cancels main's scope with `(Requested "SIGINT")` and
///   does not end the process. The program then exits with 130. The second
///   SIGINT ends the process at once.
/// - SIGTERM and SIGHUP end the process as before. When main has returned
///   and its scope still waits for fibers, a line on standard error says so.
/// - A signal that the program handles itself, with `signal-subscribe` or
///   `with-signal-cancel`, is the program's: then only the flush happens here.
///
/// The grace period:
/// - After main's scope is cancelled (main failed, a fiber of main failed, or
///   the first SIGINT), the fibers get `BJO_EXIT_GRACE` seconds, 3 by
///   default, to stop.
/// - A fiber that does not stop in that time, such as one in an
///   uncancellable wait, would make the program hang. Instead the cause and
///   the number of fibers go to standard error, and the process exits: 1
///   after a failure, 130 after SIGINT.
/// </summary>
[NonUserCode]
internal static class MainExit
{
    private const int Running = 0;
    private const int Draining = 1;
    private const int Done = 2;

    private static global::BjolangRuntime.Scope? s_main;
    private static int s_phase;

    // Every SIGINT counts, also one that the program handles itself, so that
    // the second one after a `with-signal-cancel` still ends the process.
    private static int s_interrupts;
    private static int s_interrupted;
    private static int s_armed;

    // Kept so the registrations are not collected, which would remove them.
    private static PosixSignalRegistration? s_int, s_term, s_hup;

    /// The reason main's scope is cancelled with at the first SIGINT. Compared
    /// by reference, so that a `Cancelled` from it is told apart from any other.
    internal static readonly global::BjolangRuntime.CancelReason Interrupt =
        new global::BjolangRuntime.CancelReason.Requested(BjoString.Utf8String.FromUtf16("SIGINT"));

    internal static bool Interrupted => System.Threading.Volatile.Read(ref s_interrupted) != 0;

    private static int GraceMs {
        get {
            var text = System.Environment.GetEnvironmentVariable("BJO_EXIT_GRACE");
            if (text is not null
                && double.TryParse(text, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                && seconds >= 0)
                return (int)System.Math.Min(seconds * 1000, int.MaxValue);
            return 3000;
        }
    }

    /// Main starts. Registers the signal handlers.
    internal static void Started(global::BjolangRuntime.Scope main)
    {
        s_main = main;
        if (System.OperatingSystem.IsAndroid() || System.OperatingSystem.IsBrowser()
            || System.OperatingSystem.IsWasi())
            return;
        s_int = Register(PosixSignal.SIGINT);
        s_term = Register(PosixSignal.SIGTERM);
        s_hup = Register(PosixSignal.SIGHUP);
    }

    private static PosixSignalRegistration? Register(PosixSignal signal)
    {
        try {
            return PosixSignalRegistration.Create(signal, OnSignal);
        } catch (System.Exception) {
            // The platform cannot handle this signal. Its default stays.
            return null;
        }
    }

    /// Main returned. Its scope now waits for the fibers main started. If the
    /// scope is cancelled while it waits, the grace period starts.
    internal static void Returned()
    {
        System.Threading.Volatile.Write(ref s_phase, Draining);
        var main = s_main;
        if (main is null || main.Token.IsCompleted) return;
        // Polls, because a wait on the token would be a fiber's business.
        var watcher = new System.Threading.Thread([NonUserCode] () => {
            while (System.Threading.Volatile.Read(ref s_phase) == Draining) {
                // `ScopesubEnded` is how a close that went well fires the
                // token, before the releases run. Any other reason cancelled
                // the fibers.
                if (System.Threading.Volatile.Read(ref s_phase) == Draining
                    && global::BjolangRuntime.cancelsubreason(main.Token) is { IsSome: true } reason) {
                    if (reason.Value is not global::BjolangRuntime.CancelReason.ScopesubEnded)
                        Arm(Interrupted ? 130 : 1, Interrupted ? null : FiberFailed);
                    return;
                }
                System.Threading.Thread.Sleep(50);
            }
        }) { IsBackground = true, Name = "Bjolang main exit watcher" };
        watcher.Start();
    }

    /// Main raised `failure`, and its scope is about to be closed.
    internal static void Failed(System.Exception failure)
    {
        // The body has left. What the scope still waits for is its fibers.
        System.Threading.Volatile.Write(ref s_phase, Draining);
        if (Interrupted && failure is Cancelled { Reason: var r } && ReferenceEquals(r, Interrupt))
            return;
        Arm(1, "main raised:\n" + failure);
    }

    /// Main's scope is closed. The grace period, if one runs, does nothing now.
    internal static void Closed() => System.Threading.Volatile.Write(ref s_phase, Done);

    /// Starts the grace period once. When it ends with main's scope still open,
    /// writes why to standard error and exits with `code`.
    private static void Arm(int code, string? cause)
    {
        if (System.Threading.Interlocked.Exchange(ref s_armed, 1) != 0) return;
        int grace = GraceMs;
        var timer = new System.Threading.Thread([NonUserCode] () => {
            System.Threading.Thread.Sleep(grace);
            if (System.Threading.Volatile.Read(ref s_phase) == Done) return;
            var main = s_main;
            int running = main?.RunningChildren ?? 0;
            // Before main returns, the count holds main itself.
            bool mainRuns = System.Threading.Volatile.Read(ref s_phase) == Running;
            string stuck = mainRuns
                ? (running > 1 ? $"main and {Fibers(running - 1)}" : "main")
                : Fibers(running);
            string why = cause ?? "interrupted (SIGINT)";
            if (ReferenceEquals(cause, FiberFailed) && main?.FirstFailure is { } first) why += ":\n" + first;
            Write($"bjolang: {why}\n" +
                  $"bjolang: {stuck} did not stop within {grace / 1000.0:0.###} s, so the program exits without waiting.\n" +
                  Trace());
            global::BjolangRuntime.StdOut.FlushAtExit();
            System.Environment.Exit(code);
        }) { IsBackground = true, Name = "Bjolang main exit grace" };
        timer.Start();
    }

    private static void OnSignal(PosixSignalContext ctx)
    {
        global::BjolangRuntime.StdOut.FlushAtExit();

        bool interrupt = ctx.Signal == PosixSignal.SIGINT;
        int nth = interrupt ? System.Threading.Interlocked.Increment(ref s_interrupts) : 0;

        if (SignalGroup.HandlesNow((int)ctx.Signal)) return;
        var main = s_main;
        int phase = System.Threading.Volatile.Read(ref s_phase);
        if (main is null || phase == Done) return;

        if (interrupt && nth == 1) {
            ctx.Cancel = true;
            System.Threading.Volatile.Write(ref s_interrupted, 1);
            main.Token.TrySetResult(Interrupt);
            Arm(130, null);
            return;
        }

        // The default action follows, and ends the process.
        if (phase == Draining) {
            Write($"bjolang: main has ended, and its scope was still waiting for {Fibers(main.RunningChildren)} " +
                  $"when {ctx.Signal} ended the program.\n" + Trace());
        }
    }

    private const string FiberFailed = "a fiber that main started failed";

    private static string Fibers(int n) => n == 1 ? "1 fiber" : $"{n} fibers";

    private static string Trace() =>
        FiberTrace.Enabled
            ? FiberReport.Text() + "\n"
            : "bjolang: run with BJO_FIBER_TRACE=1 to see where the program waits.\n";

    private static void Write(string text)
    {
        try {
            global::BjolangRuntime.StdErr.FlushAtExit();
            System.Console.Error.Write(text);
            System.Console.Error.Flush();
        } catch (System.Exception) {
            // Standard error is gone. Nothing else can be said.
        }
    }
}
