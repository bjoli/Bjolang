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

using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Bjoml;

namespace Bjolang.Runtime;

/// <summary>
/// The fiber trace as a person reads it: every fiber and blocked thread that
/// waits now, under the scope it waits in, with where it waits, on what, and
/// since when.
///
/// Only with <c>BJO_FIBER_TRACE</c> set (see <see cref="FiberTrace"/>). The
/// report is written on SIGQUIT (Ctrl-\ in a terminal) and by
/// <c>(fiber-trace)</c>. A watcher thread writes it once when the program
/// looks deadlocked; see <see cref="Watch"/>.
/// </summary>
[NonUserCode]
internal static class FiberReport
{
    private static int s_installed;

    /// Kept so the registration is not collected, which would remove it.
    private static System.Runtime.InteropServices.PosixSignalRegistration? s_quit;

    /// <summary>
    /// A scope was made. Notes which scope was ambient and where in the source
    /// the scope was opened. The first scope of the program also installs the
    /// signal handler and starts the watcher.
    /// </summary>
    internal static void Opened(global::BjolangRuntime.Scope scope, string kind)
    {
        Install();
        scope.TraceKind = kind;
        scope.TraceParent = global::BjolangRuntime.Dyn.Current.Scope;
        var user = WaitSites.UserFrame(new StackTrace(1, fNeedFileInfo: true));
        if (user is not null)
            scope.TraceOpenedAt = WaitSites.Describe(user.GetFileName()!, user.GetFileLineNumber(), new());
    }

    private static void Install()
    {
        if (System.Threading.Volatile.Read(ref s_installed) != 0) return;
        if (System.Threading.Interlocked.Exchange(ref s_installed, 1) != 0) return;

        try {
            s_quit = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGQUIT,
                [NonUserCode] static (ctx) => {
                    ctx.Cancel = true;
                    Write(Text());
                });
        } catch (System.Exception) {
            // No SIGQUIT on this platform. (fiber-trace) still works.
        }

        var watcher = new System.Threading.Thread(Watch) {
            IsBackground = true,
            Name = "Bjolang fiber trace watcher",
        };
        watcher.Start();
    }

    private static void Write(string text)
    {
        try {
            System.Console.Error.WriteLine(text);
            System.Console.Error.Flush();
        } catch (System.Exception) { }
    }

    // -----------------------------------------------------------------------
    // The stall watcher
    // -----------------------------------------------------------------------

    private const int LookEveryMs = 500;
    private const int StuckAfterMs = 2000;

    /// <summary>
    /// Writes the report once when the program looks deadlocked: for
    /// <see cref="StuckAfterMs"/> no frame has suspended or resumed, the
    /// scheduler has had nothing to run, no thread outside the fibers is
    /// running Bjolang code, and every fiber waits on something that only
    /// another fiber can give it. A wait on a timer, a .NET task, a
    /// <c>choose</c> or anything else that the runtime does not know the end
    /// of says that the program can still go on, and then nothing is written.
    /// It writes again only after something has happened.
    /// </summary>
    private static void Watch()
    {
        long seen = -1;
        long quietSince = 0;
        long reportedAt = -1;
        while (true) {
            System.Threading.Thread.Sleep(LookEveryMs);
            try {
                long progress = FiberTrace.Progress;
                long now = Stopwatch.GetTimestamp();
                if (progress != seen || !Quiet()) {
                    seen = progress;
                    quietSince = now;
                    continue;
                }

                if (reportedAt == progress) continue;
                if (Stopwatch.GetElapsedTime(quietSince, now).TotalMilliseconds < StuckAfterMs) continue;

                var frames = FiberTrace.Snapshot();
                if (frames.Count == 0 || !AllStuck(frames)) continue;

                // Asked again, after the snapshot: a frame that moved in
                // between is progress, and it is not a deadlock.
                if (FiberTrace.Progress != progress || !Quiet()) continue;

                reportedAt = progress;
                Write(
                    "Bjolang fiber trace: nothing has run for " +
                    $"{StuckAfterMs / 1000} s, and every fiber waits on a channel or on another fiber. " +
                    "This looks like a deadlock.\n" + Text(frames));
            } catch (System.Exception) {
                // A report that fails must not end the watcher, or the program.
            }
        }
    }

    private static bool Quiet() => FiberTrace.SchedulerIdle && !FiberTrace.OutsideRunning;

    private static bool AllStuck(List<FiberTrace.Frame> frames)
    {
        var boxes = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var f in frames) boxes.Add(f.Box);
        foreach (var f in frames) {
            // A frame waiting on a frame inside it is as stuck as that one.
            if (f.Target is not null && boxes.Contains(f.Target)) continue;
            if (!Describe(f.Target, Scope(f)).OnlyFibersEnd) return false;
        }
        return true;
    }

    // -----------------------------------------------------------------------
    // What a frame waits on
    // -----------------------------------------------------------------------

    [NonUserCode]
    private readonly record struct Wait(string What, object? Channel, bool OnlyFibersEnd);

    private static global::BjolangRuntime.Scope? Scope(FiberTrace.Frame f) =>
        (f.Context as global::BjolangRuntime.DynEnv)?.Scope;

    /// <summary>
    /// What <paramref name="target"/> is, in Bjolang's words, and whether only
    /// a fiber can end the wait: a channel's partner, a fiber that lands, a
    /// scope's last child. A wait that nothing in the runtime ends, such as
    /// <c>never</c>, counts as one that only a fiber ends.
    /// </summary>
    private static Wait Describe(object? target, global::BjolangRuntime.Scope? scope)
    {
        if (target is null) return new("waiting for an event", null, false);

        var type = target.GetType();
        var def = type.IsGenericType ? type.GetGenericTypeDefinition() : type;

        if (def == typeof(Channel<>)) return new("waiting in chan-recv", target, true);
        if (def == typeof(ChannelSendSide<>)) return new("waiting in chan-send", Field(target, "_channel"), true);
        if (def == typeof(ChannelSendEvent<>)) return new("waiting in chan-send", Field(target, "_channel"), true);
        if (def == typeof(ChannelReceiveEvent<>)) return new("waiting in chan-recv", Field(target, "_channel"), true);
        if (target is SleepNode || target is TimeoutEvent || target is AtEvent)
            return new("waiting for a timeout", null, false);
        if (def == typeof(NeverEvent<>)) return new("waiting on never", null, true);
        if (def == typeof(ChooseEvent<>) || def == typeof(PairChooseEvent<>) || def == typeof(TripleChooseEvent<>))
            return new("waiting in a choose", null, false);
        if (def == typeof(WrapEvent<,>)) return new("waiting for a wrapped event", null, false);
        if (def == typeof(GuardEvent<>)) return new("waiting in a guard", null, false);
        if (def == typeof(WithNackEvent<>)) return new("waiting in a with-nack", null, false);
        if (def == typeof(Gate<>) || def == typeof(InboxRecvEvent<>)) return new("waiting for an inbox", null, false);
        if (def == typeof(InboxSendEvent<>)) return new("waiting to send to an inbox", null, false);

        if (scope is not null && ReferenceEquals(target, scope.AllDone))
            return new("closing this scope, waiting for its children", null, true);
        if (def == typeof(FiberCore<>) || def == typeof(StatefulFiberCore<,>))
            return new("waiting for a fiber to land", null, true);
        // The frame it calls has not suspended, so it is running or queued to run.
        if (def == typeof(CalledFiber<,>)) return new("running, or ready to run", null, false);
        if (def == typeof(Promise<>) || def == typeof(PromiseValue<>)) return new("waiting for a promise", null, false);

        var name = def.Name;
        int tick = name.IndexOf('`');
        if (tick > 0) name = name.Substring(0, tick);
        return def.Namespace is { } ns && ns.StartsWith("System", System.StringComparison.Ordinal)
            ? new($"waiting for .NET {name}", null, false)
            : new($"waiting for {name}", null, false);
    }

    private static object? Field(object o, string name) =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(o);

    // -----------------------------------------------------------------------
    // The report
    // -----------------------------------------------------------------------

    /// <summary>The report of what waits now.</summary>
    internal static string Text() =>
        FiberTrace.Enabled
            ? Text(FiberTrace.Snapshot())
            : "The fiber trace is off. Set BJO_FIBER_TRACE=1 in the environment to turn it on.";

    /// <summary>One wait, its chain of callers and how long it has lasted.</summary>
    [NonUserCode]
    private sealed class Waiting
    {
        public string Line = "";
        public List<string> Chain = new();
        public double Seconds;
        public bool IsThread;
    }

    private static string Text(List<FiberTrace.Frame> frames)
    {
        var byBox = new Dictionary<object, FiberTrace.Frame>(ReferenceEqualityComparer.Instance);
        foreach (var f in frames) byBox[f.Box] = f;

        // The frame that waits on each frame: its caller.
        var callerOf = new Dictionary<object, FiberTrace.Frame>(ReferenceEqualityComparer.Instance);
        foreach (var f in frames)
            if (f.Target is not null && byBox.ContainsKey(f.Target)) callerOf[f.Target] = f;

        var channels = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var byScope = new Dictionary<object, List<Waiting>>(ReferenceEqualityComparer.Instance);
        var noScope = new object();
        long now = Stopwatch.GetTimestamp();
        int fibers = 0, threads = 0;

        foreach (var f in frames) {
            // Only the innermost frame of each fiber starts a line; the
            // frames further out are its chain.
            if (f.Target is not null && byBox.ContainsKey(f.Target)) continue;

            var scope = Scope(f);
            var wait = Describe(f.Target, scope);
            var what = wait.What;
            if (wait.Channel is not null) {
                if (!channels.TryGetValue(wait.Channel, out var n)) channels[wait.Channel] = n = channels.Count + 1;
                what += $" on channel #{n}";
            }

            var w = new Waiting {
                Line = what,
                Seconds = Stopwatch.GetElapsedTime(f.Since, now).TotalSeconds,
                IsThread = f.IsThread,
            };
            var sites = new List<FiberTrace.Site> { f.Site };
            var cur = f;
            var guard = new HashSet<object>(ReferenceEqualityComparer.Instance) { f.Box };
            while (callerOf.TryGetValue(cur.Box, out var outer) && guard.Add(outer.Box)) {
                sites.Add(outer.Site);
                cur = outer;
            }
            w.Chain = Shown(sites);

            if (f.IsThread) threads++; else fibers++;
            var key = (object?)scope ?? noScope;
            if (!byScope.TryGetValue(key, out var list)) byScope[key] = list = new();
            list.Add(w);
        }

        var sb = new StringBuilder();
        sb.Append("Bjolang fiber trace: ")
          .Append(fibers == 1 ? "1 fiber" : $"{fibers} fibers")
          .Append(threads == 0 ? "" : threads == 1 ? " and 1 thread" : $" and {threads} threads")
          .Append(" waiting.");

        // Each scope that has a waiting fiber, with the scopes it is in, as
        // a tree from the outermost.
        var children = new Dictionary<object, List<global::BjolangRuntime.Scope>>(ReferenceEqualityComparer.Instance);
        var roots = new List<global::BjolangRuntime.Scope>();
        var known = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var key in byScope.Keys) {
            if (key is not global::BjolangRuntime.Scope s) continue;
            for (var at = s; at is not null && known.Add(at); at = at.TraceParent) {
                if (at.TraceParent is { } parent) {
                    if (!children.TryGetValue(parent, out var kids)) children[parent] = kids = new();
                    kids.Add(at);
                } else {
                    roots.Add(at);
                }
            }
        }

        foreach (var root in roots) AppendScope(sb, root, children, byScope, "");
        if (byScope.TryGetValue(noScope, out var loose)) {
            sb.Append("\n\noutside any scope");
            AppendWaiting(sb, loose, "  ");
        }
        return sb.ToString();
    }

    private static void AppendScope(
        StringBuilder sb, global::BjolangRuntime.Scope scope,
        Dictionary<object, List<global::BjolangRuntime.Scope>> children,
        Dictionary<object, List<Waiting>> byScope, string indent)
    {
        sb.Append("\n\n").Append(indent).Append(scope.TraceKind);
        if (scope.TraceOpenedAt is { } at) sb.Append(", opened at ").Append(at);
        if (scope.Token.IsCompleted)
            sb.Append(" (cancelled: ").Append(scope.Token.Outcome.Value).Append(')');

        if (byScope.TryGetValue(scope, out var waiting)) AppendWaiting(sb, waiting, indent + "  ");
        if (children.TryGetValue(scope, out var kids))
            foreach (var kid in kids) AppendScope(sb, kid, children, byScope, indent + "  ");
    }

    /// <summary>
    /// The waits of one scope, each once with how many fibers wait so and for
    /// how long the longest of them has.
    /// </summary>
    private static void AppendWaiting(StringBuilder sb, List<Waiting> waiting, string indent)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, (Waiting First, int Count, double Longest)>();
        foreach (var w in waiting) {
            var key = (w.IsThread ? "t|" : "f|") + w.Line + "|" + string.Join("|", w.Chain);
            if (groups.TryGetValue(key, out var g))
                groups[key] = (g.First, g.Count + 1, System.Math.Max(g.Longest, w.Seconds));
            else {
                groups[key] = (w, 1, w.Seconds);
                order.Add(key);
            }
        }

        foreach (var key in order) {
            var (w, count, longest) = groups[key];
            var who = w.IsThread
                ? (count == 1 ? "1 thread" : $"{count} threads")
                : (count == 1 ? "1 fiber" : $"{count} fibers");
            sb.Append('\n').Append(indent).Append(who).Append(' ').Append(w.Line)
              .Append(", for ").Append(Seconds(longest));
            for (int i = 0; i < w.Chain.Count; i++)
                sb.Append('\n').Append(indent).Append(i == 0 ? "    at   " : "    from ").Append(w.Chain[i]);
        }
    }

    /// <summary>
    /// The sites of a chain that are in Bjolang source, without a site
    /// repeated next to itself. None when no site is in Bjolang source: such
    /// a chain is the runtime's own frames, such as the start of <c>main</c>.
    /// </summary>
    private static List<string> Shown(List<FiberTrace.Site> sites)
    {
        var shown = new List<string>();
        foreach (var s in sites) {
            if (!s.InBjolang) continue;
            if (shown.Count > 0 && shown[^1] == s.Text) continue;
            shown.Add(s.Text);
        }
        return shown;
    }

    private static string Seconds(double s) =>
        s < 1 ? $"{s * 1000:0} ms" : s < 60 ? $"{s:0.0} s" : $"{s / 60:0.0} min";
}
