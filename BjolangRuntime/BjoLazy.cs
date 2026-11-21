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

using System.Runtime.CompilerServices;
using Bjoml;

namespace Bjolang.Runtime;

// The lazy values of (std lazy).
//
// A Bjolang `(Lazy T)` is a `BjoLazy<T>`. It does not use System.Lazy<T>:
// - System.Lazy<T> keeps an exception from the thunk and raises it again at
//   each later access. Here a failed thunk does not change the value: the
//   next force runs the thunk again.
// - The thunk runs in the dynamic environment of the thread that forces it.
//
// States:
//   Unstarted  the thunk has not run, or it raised.
//   Running    one thread runs the thunk. Other threads wait.
//   Done       _value is set and does not change. _thunk is null.

/// <summary>A value that is computed one time, when it is first forced.</summary>
[NonUserCode]
public sealed class BjoLazy<T>
{
    private const int Unstarted = 0;
    private const int Running = 1;
    private const int Done = 2;

    private int _state;
    private T _value = default!;
    private Func<T>? _thunk;

    // The managed thread ID of the thread that runs the thunk, or 0.
    private int _runner;

    // The number of threads in Monitor.Wait, or about to be.
    private int _waiters;

    internal BjoLazy(Func<T> thunk) => _thunk = thunk;

    internal BjoLazy(T value)
    {
        _value = value;
        _state = Done;
    }

    public bool IsForced => Volatile.Read(ref _state) == Done;

    /// <summary>
    /// Returns the value. Runs the thunk if no force has completed.
    /// - Exactly one thread runs the thunk at a time. A second thread waits
    ///   for it, and blocks its thread.
    /// - If the thunk raises, the exception goes to the thread that ran it.
    ///   The lazy value is unstarted again, and a waiting thread runs the
    ///   thunk itself.
    /// - A force on the thread that runs the thunk raises
    ///   InvalidOperationException. Waiting there can not complete.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Force() => Volatile.Read(ref _state) == Done ? _value : ForceSlow();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T ForceSlow()
    {
        var me = Environment.CurrentManagedThreadId;
        while (true)
        {
            switch (Interlocked.CompareExchange(ref _state, Running, Unstarted))
            {
                case Done:
                    return _value;
                case Unstarted:
                    return Run(me);
                default:
                    if (Volatile.Read(ref _runner) == me)
                        throw new InvalidOperationException(
                            "force: the lazy value was forced by its own thunk");
                    Wait();
                    break;
            }
        }
    }

    private T Run(int me)
    {
        Volatile.Write(ref _runner, me);
        T value;
        try
        {
            value = _thunk!();
        }
        catch
        {
            Volatile.Write(ref _runner, 0);
            Interlocked.Exchange(ref _state, Unstarted);
            Wake();
            throw;
        }
        _value = value;
        _thunk = null;
        Volatile.Write(ref _runner, 0);
        // Publishes _value: a reader that sees Done also sees _value. A full
        // fence, so that Wake reads _waiters after the state changed.
        Interlocked.Exchange(ref _state, Done);
        Wake();
        return value;
    }

    // Monitor.Wait and Monitor.PulseAll make the lock a sync block, which is
    // slow to make. So the runner takes the lock only when a waiter counted
    // itself in _waiters.
    //
    // A waiter cannot miss the wake:
    // - The waiter increments _waiters, then reads the state.
    // - The runner changes the state, then reads _waiters.
    // Both writes are full fences. So the waiter sees the new state, or the
    // runner sees the waiter and pulses. The waiter holds the lock from its
    // read of the state until Monitor.Wait releases it, so the pulse comes
    // after the wait starts.
    private void Wait()
    {
        var spin = new SpinWait();
        while (!spin.NextSpinWillYield)
        {
            if (Volatile.Read(ref _state) != Running) return;
            spin.SpinOnce();
        }
        lock (this)
        {
            Interlocked.Increment(ref _waiters);
            while (Volatile.Read(ref _state) == Running) Monitor.Wait(this);
            Interlocked.Decrement(ref _waiters);
        }
    }

    private void Wake()
    {
        if (Volatile.Read(ref _waiters) != 0)
            lock (this) Monitor.PulseAll(this);
    }

    public override string ToString() =>
        IsForced ? $"#<lazy {_value}>" : "#<lazy>";
}

/// <summary>
/// The state of a <c>FiberLazy</c> of (std lazy). The thunk and the calls to
/// it are in Bjolang: <c>force/fiber</c> is a <c>defbjo</c> that calls these
/// methods, none of which suspends.
/// </summary>
///
/// <remarks>
/// Protocol for one force:
/// - <see cref="TryStart"/> returns true for exactly one caller while no run
///   is in progress. That caller runs the thunk, then calls
///   <see cref="Succeed"/> or <see cref="Fail"/>.
/// - Every other caller syncs on <see cref="Gate"/>, then starts again.
///
/// <c>_gate</c> is the run in progress: a promise that completes when the run
/// ends, with success or failure. It does not hold the value. <c>Fail</c> sets
/// <c>_gate</c> to null before it completes the gate, so a woken waiter finds
/// no run in progress and can start one.
///
/// <c>_done</c> is made only for <c>lazy-evt</c>. It completes with the value
/// when a run succeeds, and never fails.
/// </remarks>
[NonUserCode]
public sealed class FiberLazyCell<T>
{
    private const int Unstarted = 0;
    private const int Done = 2;

    private static readonly Promise<Unit> s_ended = Ended();

    private int _state;
    private T _value = default!;
    private Promise<Unit>? _gate;
    private Promise<T>? _done;

    public bool IsForced => Volatile.Read(ref _state) == Done;

    /// <summary>The value. Only after <see cref="IsForced"/> returns true.</summary>
    public T Value => _value;

    /// <summary>
    /// Whether the caller must run the thunk. False when the value is forced or
    /// another caller runs the thunk.
    /// </summary>
    public bool TryStart()
    {
        if (Volatile.Read(ref _state) == Done || Volatile.Read(ref _gate) is not null) return false;
        if (Interlocked.CompareExchange(ref _gate, new Promise<Unit>(), null) is not null) return false;
        // A run that succeeded between the two reads must not run again.
        if (Volatile.Read(ref _state) == Done)
        {
            Volatile.Read(ref _gate)!.TrySetResult(default);
            return false;
        }
        return true;
    }

    /// <summary>
    /// A promise that completes when the run in progress ends. An ended
    /// promise when no run is in progress.
    /// </summary>
    public Promise<Unit> Gate() => Volatile.Read(ref _gate) ?? s_ended;

    /// <summary>The runner stores the value and ends its run.</summary>
    public void Succeed(T value)
    {
        _value = value;
        // A full fence: DonePromise writes _done and then reads _state.
        Interlocked.Exchange(ref _state, Done);
        Volatile.Read(ref _done)?.TrySetResult(value);
        Volatile.Read(ref _gate)?.TrySetResult(default);
    }

    /// <summary>The runner ends its run without a value.</summary>
    public void Fail()
    {
        var gate = Interlocked.Exchange(ref _gate, null);
        gate?.TrySetResult(default);
    }

    /// <summary>A promise that completes with the value when a run succeeds.</summary>
    public Promise<T> DonePromise()
    {
        var done = Volatile.Read(ref _done);
        if (done is null)
        {
            var mine = new Promise<T>();
            done = Interlocked.CompareExchange(ref _done, mine, null) ?? mine;
        }
        // A run that succeeded before _done was set did not complete it.
        if (Volatile.Read(ref _state) == Done) done.TrySetResult(_value);
        return done;
    }

    private static Promise<Unit> Ended()
    {
        var p = new Promise<Unit>();
        p.TrySetResult(default);
        return p;
    }

    public override string ToString() =>
        IsForced ? $"#<fiber-lazy {_value}>" : "#<fiber-lazy>";
}

/// <summary>The functions of (std lazy) for async thunks.</summary>
[NonUserCode]
public static class FiberLazyModule
{
    public static FiberLazyCell<T> Make<T>() => new();
    public static bool IsForced<T>(FiberLazyCell<T> cell) => cell.IsForced;
    public static T Value<T>(FiberLazyCell<T> cell) => cell.Value;
    public static bool TryStart<T>(FiberLazyCell<T> cell) => cell.TryStart();
    public static Promise<Unit> Gate<T>(FiberLazyCell<T> cell) => cell.Gate();
    public static void Succeed<T>(FiberLazyCell<T> cell, T value) => cell.Succeed(value);
    public static void Fail<T>(FiberLazyCell<T> cell) => cell.Fail();
    public static Promise<T> Done<T>(FiberLazyCell<T> cell) => cell.DonePromise();
}

/// <summary>The functions of (std lazy).</summary>
[NonUserCode]
public static class LazyModule
{
    public static BjoLazy<T> Make<T>(Func<T> thunk) => new(thunk);

    public static BjoLazy<T> Ready<T>(T value) => new(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Force<T>(BjoLazy<T> lazy) => lazy.Force();

    public static bool IsForced<T>(BjoLazy<T> lazy) => lazy.IsForced;
}
