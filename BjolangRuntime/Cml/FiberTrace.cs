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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Bjoml;

/// <summary>
/// An awaiter that can say what a fiber parked on it waits for: a channel, a
/// timer node, a promise. Read only when the trace is on.
/// </summary>
internal interface ITraceAwaiter
{
    object? TraceTarget { get; }
}

/// <summary>
/// The fiber trace: every frame of every fiber that is suspended now, with
/// where it waits and on what.
///
/// Off unless <c>BJO_FIBER_TRACE</c> is set in the environment. Every hook
/// tests <see cref="Enabled"/>, which reads two <c>static readonly</c> fields.
/// When the JIT recompiles a method at tier 1, it reads such a field as a
/// constant, so with the trace off the test and the call after it are removed
/// from the hot paths.
///
/// Native AOT has no tier 1, and reads a field set from the environment at
/// every test: 5 to 8 % of a channel rendezvous. So <see cref="IsSupported"/>
/// is a feature switch, and the Native AOT build sets it to false. ILCompiler
/// then reads <see cref="Enabled"/> as false and removes the hooks. A native
/// executable has no fiber trace; the <c>.exe</c> built beside it has.
///
/// # What is recorded
///
/// A fiber is a chain of boxed state machines, one per bjoroutine frame that
/// is suspended. Each box is recorded when its frame suspends, and removed
/// when the box is resumed. The innermost frame of a fiber waits on an event,
/// a timer or a .NET task; each frame further out waits on the frame inside
/// it, through that frame's promise. The report puts the chains back together
/// from that link.
///
/// The place in the source is the line of the await, read off the stack the
/// first time a frame suspends at that await. It is kept per state machine
/// type and await number, so a loop that waits at one line pays for the
/// stack walk once.
/// </summary>
[NonUserCode]
public static class FiberTrace
{
    /// The feature switch that a Native AOT build sets to false.
    public const string SupportedSwitch = "Bjolang.FiberTrace.IsSupported";

    private static readonly bool s_supported =
        !AppContext.TryGetSwitch(SupportedSwitch, out bool supported) || supported;

    private static readonly bool s_enabled = IsOn(Environment.GetEnvironmentVariable("BJO_FIBER_TRACE"));

    /// <summary>
    /// Can the trace be turned on in this build? A constant false where the
    /// switch is set to false at the build, as a Native AOT build sets it.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.FeatureSwitchDefinition(SupportedSwitch)]
    public static bool IsSupported => s_supported;

    /// <summary>Is the trace on? Read once, at start-up.</summary>
    public static bool Enabled
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IsSupported && s_enabled;
    }

    private static bool IsOn(string? value) =>
        value is not null && value != "" && value != "0"
        && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>One suspended frame.</summary>
    [NonUserCode]
    public sealed class Frame
    {
        /// <summary>The box: what resumes the frame, and its promise for a called frame.</summary>
        public object Box = null!;

        /// <summary>The source place of the await, as <see cref="Site"/> writes it.</summary>
        public Site Site = null!;

        /// <summary>
        /// What the frame waits on: a channel, a channel's send side, a timer
        /// node, a promise, an event, or a .NET awaiter. Null when nothing
        /// was found.
        /// </summary>
        public object? Target;

        /// <summary>The fiber's dynamic environment when the frame suspended.</summary>
        public object? Context;

        /// <summary><see cref="Stopwatch.GetTimestamp"/> when it suspended.</summary>
        public long Since;

        /// <summary>
        /// A thread that parked itself, as a <c>defun</c> does in
        /// <c>sync/blocking</c> or at the close of a scope, rather than a
        /// fiber's frame.
        /// </summary>
        public bool IsThread;
    }

    /// <summary>
    /// A place in the source. <see cref="Text"/> is the place as a person
    /// reads it; <see cref="InBjolang"/> says whether it is in a .bjo file, so
    /// the report can leave out frames of the runtime.
    /// </summary>
    [NonUserCode]
    public sealed class Site
    {
        public readonly string Text;
        public readonly bool InBjolang;

        public Site(string text, bool inBjolang) { Text = text; InBjolang = inBjolang; }
    }

    private static readonly ConcurrentDictionary<object, Frame> s_parked = new(ReferenceEqualityComparer.Instance);

    private static readonly ConcurrentDictionary<(Type, int), Site> s_sites = new();

    private static readonly ConcurrentDictionary<Type, FieldInfo?> s_stateFields = new();

    /// <summary>
    /// How many frames have suspended or resumed since the start. Tells a
    /// watcher whether anything happened between two of its looks.
    /// </summary>
    private static long s_progress;

    public static long Progress => Interlocked.Read(ref s_progress);

    /// <summary>
    /// The event of the sync that this thread is starting, set by the sync and
    /// taken by the park after it. The awaiter of a <c>choose</c> does not keep
    /// its event, so this is how the trace learns what a choose waits on.
    /// </summary>
    [ThreadStatic] private static object? t_event;

    /// <summary>Called by a sync when the trace is on, before its awaiter parks.</summary>
    public static void NoteEvent(object ev) => t_event = ev;

    /// <summary>
    /// A frame is about to suspend on <paramref name="awaiter"/>. Called before
    /// the park, because a partner can resume the frame as soon as it is
    /// parked, and the resume must find the record to remove it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Parked<TAwaiter, TStateMachine>(object box, ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        var noted = t_event;
        t_event = null;

        object? target = awaiter is ITraceAwaiter t ? t.TraceTarget : awaiter;
        if (target is null) target = noted;

        var frame = new Frame
        {
            Box = box,
            Site = SiteOf(ref stateMachine),
            Target = target,
            Context = FiberContext.Current,
            Since = Stopwatch.GetTimestamp(),
        };
        s_parked[box] = frame;
        Interlocked.Increment(ref s_progress);
    }

    /// <summary>A box is resuming its frame.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Resumed(object box)
    {
        s_parked.TryRemove(box, out _);
        Interlocked.Increment(ref s_progress);
    }

    /// <summary>The frames suspended now, in no order.</summary>
    public static List<Frame> Snapshot() => new(s_parked.Values);

    /// <summary>
    /// Is this thread running Bjolang code outside any fiber? True on the
    /// thread of a <c>defun</c> <c>main</c>, between its start and its end.
    /// </summary>
    [ThreadStatic] private static bool t_outside;

    /// How many such threads are running now, not parked in a wait.
    private static int s_outsideRunning;

    /// <summary>
    /// Is a thread outside the fibers running Bjolang code now? While one is,
    /// fibers that wait for it are not stuck: it can still send to them.
    /// </summary>
    public static bool OutsideRunning => Volatile.Read(ref s_outsideRunning) > 0;

    /// <summary>This thread runs a <c>defun</c> <c>main</c>.</summary>
    public static void EnterOutside()
    {
        if (t_outside) return;
        t_outside = true;
        Interlocked.Increment(ref s_outsideRunning);
    }

    /// <summary>The <c>defun</c> <c>main</c> of this thread has ended.</summary>
    public static void LeaveOutside()
    {
        if (!t_outside) return;
        t_outside = false;
        Interlocked.Decrement(ref s_outsideRunning);
    }

    /// <summary>
    /// This thread is about to park until <paramref name="target"/> is done.
    /// Answers the key to give to <see cref="ThreadDone"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object ThreadWaiting(object target)
    {
        var key = new object();
        var where = Bjolang.Runtime.WaitSites.Where(new StackTrace(1, fNeedFileInfo: true), new());
        var site = where is null
            ? new Site("(no Bjolang code on its stack)", false)
            : new Site(where, true);

        s_parked[key] = new Frame
        {
            Box = key,
            Site = site,
            Target = target,
            Context = FiberContext.Current,
            Since = Stopwatch.GetTimestamp(),
            IsThread = true,
        };
        Interlocked.Increment(ref s_progress);
        if (t_outside) Interlocked.Decrement(ref s_outsideRunning);
        return key;
    }

    /// <summary>The wait that <see cref="ThreadWaiting"/> recorded is over.</summary>
    public static void ThreadDone(object key)
    {
        s_parked.TryRemove(key, out _);
        Interlocked.Increment(ref s_progress);
        if (t_outside) Interlocked.Increment(ref s_outsideRunning);
    }

    /// <summary>
    /// Is the scheduler idle: no worker held by a pump, and nothing queued
    /// for them? Answers false with <c>BJO_SCHEDULER=pool</c>, where the
    /// queues are the .NET pool's and cannot be read.
    /// </summary>
    public static bool SchedulerIdle => Workers.Enabled && Workers.IsIdle;

    /// <summary>
    /// The place of the await the state machine is suspending at. The state
    /// field says which await it is; the stack, read once per await, says
    /// where it is.
    /// </summary>
    private static Site SiteOf<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        var type = typeof(TStateMachine);
        var field = s_stateFields.GetOrAdd(type, [NonUserCode] static (t) =>
            t.GetField("<>1__state", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        int state = field?.GetValue(stateMachine) is int s ? s : int.MinValue;

        if (s_sites.TryGetValue((type, state), out var known)) return known;
        var site = Locate(type, new StackTrace(1, fNeedFileInfo: true));
        s_sites[(type, state)] = site;
        return site;
    }

    /// <summary>
    /// The frame of the state machine's <c>MoveNext</c> on the stack, as a site.
    /// It is on the stack because the park is called from inside it.
    /// </summary>
    private static Site Locate(Type stateMachine, StackTrace trace)
    {
        var wanted = stateMachine.IsGenericType ? stateMachine.GetGenericTypeDefinition() : stateMachine;
        foreach (var frame in trace.GetFrames())
        {
            var declaring = frame.GetMethod()?.DeclaringType;
            if (declaring is null) continue;
            if (declaring.IsGenericType) declaring = declaring.GetGenericTypeDefinition();
            if (declaring != wanted) continue;

            var file = frame.GetFileName();
            if (file is null) break;
            bool bjo = file.EndsWith(".bjo", StringComparison.Ordinal);
            return new Site(Bjolang.Runtime.WaitSites.Describe(file, frame.GetFileLineNumber(), new()), bjo);
        }

        // No lines: a build without a pdb, such as a Native AOT executable,
        // where `GetMethod` answers null too. The state machine's own type
        // still names the function and its module.
        var full = stateMachine.FullName ?? stateMachine.Name;
        bool program = full.StartsWith("BjoMod.", StringComparison.Ordinal);
        return new Site(program ? Bjolang.Runtime.WaitSites.Readable(full, "MoveNext") : MethodName(stateMachine), program);
    }

    /// <summary>
    /// The method a state machine was made for, from its name: the compiler
    /// calls the machine of <c>worker</c> <c>&lt;worker&gt;d__3</c>.
    /// </summary>
    internal static string MethodName(Type stateMachine)
    {
        var name = stateMachine.Name;
        int open = name.IndexOf('<'), close = name.IndexOf('>');
        var method = open >= 0 && close > open ? name.Substring(open + 1, close - open - 1) : name;
        var owner = stateMachine.DeclaringType?.Name;
        return owner is null ? method : $"{owner}.{method}";
    }
}
