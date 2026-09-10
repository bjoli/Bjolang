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
using System.Runtime.CompilerServices;
using System.Threading;

namespace Bjoml;

// ---------------------------------------------------------------------------
// The boxed state machine. This is where the dynamic-context shim lives.
// ---------------------------------------------------------------------------

/// <summary>
/// Marker for "this work item's <c>Execute</c> is equivalent to invoking its
/// resume <c>Action</c>". Implemented ONLY by <see cref="FiberStateMachineBox{T}"/>.
///
/// <c>Promise.Complete</c> uses it to wake a parked fiber by enqueuing the box
/// itself instead of routing the fiber's resume delegate through a pooled
/// <c>ActionWorkItem</c>. The marker is what makes the target-sniff sound: a
/// delegate can wrap ANY method of an object that happens to implement
/// <see cref="IThreadPoolWorkItem"/>, and enqueuing such an object would run the
/// wrong code. Only mark a type with this if invoking <c>Execute</c> and
/// invoking the only <c>Action</c> the type ever hands out are the same thing.
/// </summary>
internal interface IFiberResume : IThreadPoolWorkItem { }

/// <summary>
/// Heap home for a fiber's state machine, allocated once at the first suspension
/// (the same trick the BCL's <c>AsyncTaskMethodBuilder</c> uses).
///
/// After the first box, the compiler keeps calling <c>MoveNext</c> on THIS copy, so
/// later <c>AwaitOnCompleted</c> calls hand us a reference to this very field and we
/// must not copy the state machine again.
///
/// It implements <see cref="IThreadPoolWorkItem"/> directly, so resuming a fiber
/// from the scheduler costs no wrapper allocation at all.
/// </summary>
internal sealed class FiberStateMachineBox<TStateMachine> : IFiberResume
    where TStateMachine : IAsyncStateMachine
{
    public TStateMachine StateMachine = default!;

    /// <summary>
    /// The fiber's dynamic environment, re-captured at every suspension so that
    /// changes made between two awaits are carried forward.
    /// </summary>
    public object? Context;

    private readonly Action _moveNext;
    public Action MoveNextAction => _moveNext;

    public FiberStateMachineBox() => _moveNext = Run;

    /// <summary>
    /// Resume the fiber with its own dynamic environment installed.
    ///
    /// Save-and-restore rather than plain assignment: continuations run inline on
    /// whichever thread completed the rendezvous, and that thread may be several
    /// frames deep inside a DIFFERENT fiber. We are borrowing it, so we hand it back
    /// exactly as we found it.
    ///
    /// Note we do NOT touch ExecutionContext. That is the whole point: the hosted
    /// language keeps its dynamic state here, and reinstating C#'s ambient context
    /// on top would be both wasted work and wrong.
    /// </summary>
    private void Run()
    {
        var prev = FiberContext.Current;
        FiberContext.Current = Context;
        try
        {
            StateMachine.MoveNext();
        }
        finally
        {
            FiberContext.Current = prev;
        }
    }

    /// <summary>Scheduler entry point when the resume was queued rather than inline.</summary>
    public void Execute()
    {
        Scheduler.InlineDepth = 0;
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            // MoveNext normally routes exceptions to SetException; reaching here
            // means the state machine itself failed, which must not kill the process.
            Scheduler.ReportUnhandled(ex);
        }
        finally
        {
            // The fiber has run to its next suspension and this thread is about to
            // go back to the pool, so publish anything it spawned along the way.
            Scheduler.OnWorkItemComplete();
        }
    }
}

// ---------------------------------------------------------------------------
// Core: the shared mutable half of a fiber. The builder is a struct and gets
// copied around; this reference is what actually persists.
// ---------------------------------------------------------------------------

public class FiberCore<T> : Promise<T>, IThreadPoolWorkItem
{
    [ThreadStatic] internal static FiberCore<T>? CurrentSpawning;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static FiberCore<T>? TakeSpawning()
    {
        var item = CurrentSpawning;
        if (item is not null)
        {
            CurrentSpawning = null;
            return item;
        }
        return null;
    }

    private object? _box;
    internal Action<FiberCore<T>>? _runner;
    internal object? _spawnBody;
    internal object? _spawnInherited;

    public FiberCore() { }

    internal FiberCore(Action<FiberCore<T>> runner, object body, object? inherited)
    {
        _runner = runner;
        _spawnBody = body;
        _spawnInherited = inherited;
    }

    public void SetResult(T value) => TrySetResult(value);
    public void SetException(Exception e) => TrySetException(e);

    /// <summary>
    /// Get the delegate that resumes this fiber, boxing the state machine on first
    /// use and re-capturing the dynamic context on every subsequent suspension.
    /// </summary>
    internal Action GetMoveNextAction<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        if (_box is FiberStateMachineBox<TStateMachine> existing)
        {
            // Re-capture: the fiber may have changed its own context since the last
            // suspension, e.g. by entering a (parameterize ...).
            existing.Context = FiberContext.Current;
            return existing.MoveNextAction;
        }

        var box = new FiberStateMachineBox<TStateMachine>
        {
            StateMachine = stateMachine,   // the one and only copy from the stack
            Context = FiberContext.Current,
        };
        _box = box;
        return box.MoveNextAction;
    }

    /// <summary>The boxed resume, usable as a work item with no wrapper allocation.</summary>
    internal IThreadPoolWorkItem? WorkItem => _box as IThreadPoolWorkItem;

    void IThreadPoolWorkItem.Execute()
    {
        var runner = _runner;
        var inherited = _spawnInherited;

        _runner = null;
        _spawnInherited = null;

        var prev = FiberContext.Current;
        bool hasContextChange = !ReferenceEquals(prev, inherited);
        if (hasContextChange) FiberContext.Current = inherited;

        CurrentSpawning = this;
        try
        {
            runner?.Invoke(this);
        }
        catch (Exception e)
        {
            TrySetException(e);
        }
        finally
        {
            _spawnBody = null;
            CurrentSpawning = null;
            if (hasContextChange) FiberContext.Current = prev;
            Scheduler.OnWorkItemComplete();
        }
    }
}

/// <summary>
/// A spawned fiber that carries caller state, stored INLINE and typed.
///
/// The state used to live in an <c>object?</c> field on <see cref="FiberCore{T}"/>,
/// which boxed every value-typed state — and the idiomatic state for a static
/// spawn lambda is exactly a value tuple, so the common case paid a 32 B box per
/// spawn (measured on the Spawn+send benchmark). A generic subclass keeps the
/// tuple inline and lets the runner read it without a cast-unbox.
///
/// The runner clears the slot immediately after starting the body, so a
/// long-lived promise handle does not pin the spawn arguments.
/// </summary>
internal sealed class StatefulFiberCore<TState, T> : FiberCore<T>
{
    internal TState SpawnState;

    internal StatefulFiberCore(Action<FiberCore<T>> runner, object body, TState state, object? inherited)
        : base(runner, body, inherited)
    {
        SpawnState = state;
    }
}

// ---------------------------------------------------------------------------
// The task-like types
// ---------------------------------------------------------------------------

/// <summary>
/// Return type of a compiled bjoroutine that yields a value.
///
/// Deliberately NOT a first-class language value: it is a compiler artifact, in the
/// same way <c>Task</c> is for C#. What escapes into the language is
/// <see cref="Promise{T}"/>, which unlike a fiber (or a task) is composable with
/// <c>choose</c>.
/// </summary>
[AsyncMethodBuilder(typeof(FiberMethodBuilder<>))]
public readonly struct Fiber<T>
{
    internal readonly FiberCore<T> Core;
    internal Fiber(FiberCore<T> core) => Core = core;

    /// <summary>The first-class handle. This is what <c>spawn</c> hands to the language.</summary>
    public Promise<T> AsPromise() => Core;

    public PromiseAwaiter<T> GetAwaiter() => Core.GetAwaiter();
}

/// <summary>Return type of a compiled bjoroutine with no useful value.</summary>
[AsyncMethodBuilder(typeof(FiberMethodBuilder))]
public readonly struct Fiber
{
    internal readonly FiberCore<Unit> Core;
    internal Fiber(FiberCore<Unit> core) => Core = core;

    public Promise<Unit> AsPromise() => Core;

    public PromiseAwaiter<Unit> GetAwaiter() => Core.GetAwaiter();
}

// ---------------------------------------------------------------------------
// Builders
// ---------------------------------------------------------------------------

public struct FiberMethodBuilder<T>
{
    private FiberCore<T> _core;

    public static FiberMethodBuilder<T> Create()
    {
        var b = default(FiberMethodBuilder<T>);
        b._core = FiberCore<T>.TakeSpawning() ?? new FiberCore<T>();
        return b;
    }

    // The compiler looks for a property literally named `Task`.
    public Fiber<T> Task => new Fiber<T>(_core);

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        // No ExecutionContext capture or restore. The body runs on the caller's
        // thread with the caller's dynamic environment already installed, which is
        // exactly what a direct procedure call should look like.
        //
        // The caller's context is put back when this returns, because a call that
        // *suspends* returns here with the callee's environment still installed:
        // the body ran up to its first await on this thread, and anything it
        // pushed on the way — a (parameterize ...), a cancellation scope — is
        // still in the slot. The caller would then adopt it, and take its
        // snapshot from it at its own next suspension.
        //
        // The callee loses nothing: its box captured this same environment inside
        // MoveNext, at the await, which is what reinstates it when it resumes.
        var caller = FiberContext.Current;
        try
        {
            stateMachine.MoveNext();
        }
        finally
        {
            FiberContext.Current = caller;
        }
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine) { /* legacy/debugger only */ }

    public void SetResult(T result) => _core.SetResult(result);

    public void SetException(Exception exception) => _core.SetException(exception);

    /// <summary>
    /// Routed to the unsafe path on purpose: BjoML never flows ExecutionContext.
    /// </summary>
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
        => awaiter.OnCompleted(_core.GetMoveNextAction(ref stateMachine));

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
        => awaiter.UnsafeOnCompleted(_core.GetMoveNextAction(ref stateMachine));
}

public struct FiberMethodBuilder
{
    private FiberCore<Unit> _core;

    public static FiberMethodBuilder Create()
    {
        var b = default(FiberMethodBuilder);
        b._core = FiberCore<Unit>.TakeSpawning() ?? new FiberCore<Unit>();
        return b;
    }

    public Fiber Task => new Fiber(_core);

    /// <summary>See <see cref="FiberMethodBuilder{T}.Start"/>: the caller's dynamic
    /// environment is put back when a suspended call returns here.</summary>
    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        var caller = FiberContext.Current;
        try
        {
            stateMachine.MoveNext();
        }
        finally
        {
            FiberContext.Current = caller;
        }
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    public void SetResult() => _core.SetResult(default);

    public void SetException(Exception exception) => _core.SetException(exception);

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
        => awaiter.OnCompleted(_core.GetMoveNextAction(ref stateMachine));

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
        => awaiter.UnsafeOnCompleted(_core.GetMoveNextAction(ref stateMachine));
}
