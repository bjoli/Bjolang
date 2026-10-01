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
/// resume <c>Action</c>". Implemented ONLY by <see cref="FiberStateMachineBox{T}"/>
/// and <see cref="CalledFiber{TStateMachine, T}"/>.
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

/// <summary>What resuming a boxed state machine does, for both kinds of box.</summary>
internal static class FiberResume
{
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Run<TStateMachine>(ref TStateMachine stateMachine, object? context)
        where TStateMachine : IAsyncStateMachine
    {
        var prev = FiberContext.Current;
        FiberContext.Current = context;
        try
        {
            stateMachine.MoveNext();
        }
        finally
        {
            FiberContext.Current = prev;
        }
    }

    /// <summary>Scheduler entry point when the resume was queued rather than inline.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Execute<TStateMachine>(ref TStateMachine stateMachine, object? context)
        where TStateMachine : IAsyncStateMachine
    {
        Scheduler.InlineDepth = 0;
        try
        {
            Run(ref stateMachine, context);
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

/// <summary>
/// Heap home for a SPAWNED fiber's state machine, allocated once at its first
/// suspension. A called bjoroutine uses <see cref="CalledFiber{TStateMachine, T}"/>
/// instead, which is its promise and its box in one object; a spawned fiber's
/// promise is handed out before the body has run, so before the state machine's
/// type is known, and the two have to be separate.
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

    private void Run() => FiberResume.Run(ref StateMachine, Context);

    public void Execute() => FiberResume.Execute(ref StateMachine, Context);
}

/// <summary>
/// A called bjoroutine that has suspended: its promise, with its state machine
/// boxed inside it.
///
/// A call that completes without suspending allocates nothing, because its result
/// travels back inside the <see cref="Fiber{T}"/>. A call that suspends needs a heap
/// home for its state machine and something for its caller to wait on, both at the
/// same moment, so this one object is both.
///
/// The same rules as <see cref="FiberStateMachineBox{TStateMachine}"/> apply: the
/// state machine is copied in once, and <see cref="Context"/> is re-captured at
/// every suspension.
/// </summary>
internal sealed class CalledFiber<TStateMachine, T> : Promise<T>, IFiberResume
    where TStateMachine : IAsyncStateMachine
{
    public TStateMachine StateMachine = default!;
    public object? Context;

    private readonly Action _moveNext;
    public Action MoveNextAction => _moveNext;

    public CalledFiber() => _moveNext = Run;

    private void Run() => FiberResume.Run(ref StateMachine, Context);

    public void Execute() => FiberResume.Execute(ref StateMachine, Context);
}

// ---------------------------------------------------------------------------
// Core: the promise of a spawned fiber, made by the spawn before the body runs
// and handed to the body's builder through CurrentSpawning.
// ---------------------------------------------------------------------------

/// <summary>
/// Told when a fiber it started has finished, whichever way it finished.
///
/// The alternative is to join the fiber, and a scope that joins every child pays
/// a <c>Cml.Sync</c> per child — a <see cref="SyncState"/>, a join event and a
/// closure — to learn something the completion already knows. An owner is one
/// object shared by every child it started, so the per-fiber cost is the field.
///
/// <see cref="Landed"/> runs on whichever thread completed the fiber and is
/// subject to the same rules as a nack action: it must not suspend and must not
/// run user code. Throwing from it is reported, not propagated: the completion
/// path it runs on belongs to the fiber, not to the owner.
/// </summary>
public interface IFiberLanding
{
    void Landed(System.Runtime.ExceptionServices.ExceptionDispatchInfo? error);
}

public class FiberCore<T> : Promise<T>, IThreadPoolWorkItem
{
    [ThreadStatic] internal static FiberCore<T>? CurrentSpawning;

    /// <summary>
    /// Who to tell when this fiber lands, or null for a fiber nobody owns.
    ///
    /// Assigned before the core is enqueued, because the fiber can be running on
    /// another thread by the time <c>EnqueueSpawn</c> returns.
    /// </summary>
    internal IFiberLanding? Landing;

    protected override void OnLanded(System.Runtime.ExceptionServices.ExceptionDispatchInfo? error)
    {
        var landing = Landing;
        if (landing is null) return;

        // Cleared before the call so that an owner which completes this promise
        // again from inside Landed cannot be told twice.
        Landing = null;

        try { landing.Landed(error); }
        catch (Exception ex) { Scheduler.ReportUnhandled(ex); }
    }

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

    internal FiberCore(Action<FiberCore<T>> runner, object body, object? inherited)
    {
        _runner = runner;
        _spawnBody = body;
        _spawnInherited = inherited;
    }

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
///
/// A call that completed without suspending has no promise: <see cref="Core"/> is
/// null and the value is in <see cref="Result"/>. That is the same split
/// <c>ValueTask&lt;T&gt;</c> makes, and for the same reason: most calls never
/// suspend, and a promise per call was most of what they allocated.
/// <c>default(Fiber&lt;T&gt;)</c> is therefore a call that returned <c>default</c>.
/// </summary>
[AsyncMethodBuilder(typeof(FiberMethodBuilder<>))]
public readonly struct Fiber<T>
{
    internal readonly Promise<T>? Core;
    internal readonly T Result;

    internal Fiber(Promise<T> core)
    {
        Core = core;
        Result = default!;
    }

    internal Fiber(T result)
    {
        Core = null;
        Result = result;
    }

    /// <summary>
    /// The first-class handle. A call that completed without suspending has none
    /// yet, so one is made here, already completed.
    /// </summary>
    public Promise<T> AsPromise()
    {
        if (Core is not null) return Core;
        var p = new Promise<T>();
        p.TrySetResult(Result);
        return p;
    }

    public FiberAwaiter<T> GetAwaiter() => new FiberAwaiter<T>(Core, Result);
}

/// <summary>
/// Return type of a compiled bjoroutine with no useful value. <see cref="Core"/> is
/// null for a call that completed without suspending, as in <see cref="Fiber{T}"/>.
/// </summary>
[AsyncMethodBuilder(typeof(FiberMethodBuilder))]
public readonly struct Fiber
{
    internal readonly Promise<Unit>? Core;
    internal Fiber(Promise<Unit>? core) => Core = core;

    public Promise<Unit> AsPromise()
    {
        if (Core is not null) return Core;
        var p = new Promise<Unit>();
        p.TrySetResult(default);
        return p;
    }

    public FiberAwaiter<Unit> GetAwaiter() => new FiberAwaiter<Unit>(Core, default);
}

/// <summary>
/// Awaiter for both fiber types: the result itself when the call completed without
/// suspending, otherwise the call's promise.
/// </summary>
public readonly struct FiberAwaiter<T> : ICriticalNotifyCompletion
{
    private readonly Promise<T>? _p;
    private readonly T _result;

    internal FiberAwaiter(Promise<T>? p, T result)
    {
        _p = p;
        _result = result;
    }

    public bool IsCompleted => _p is null || _p.IsCompleted;

    // Called from inside MoveNext, so throwing here is converted to SetException by
    // the state machine. This is the correct place for a failure to surface.
    public T GetResult() => _p is null ? _result : _p.Outcome.Unwrap();

    public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

    // No ExecutionContext capture, by design. See FiberContext.
    public void UnsafeOnCompleted(Action continuation)
    {
        if (_p is null) continuation();
        else _p.OnCompleted(continuation);
    }
}

// ---------------------------------------------------------------------------
// Builders
// ---------------------------------------------------------------------------

/// <summary>What the two builders share: finding the delegate that resumes a fiber.</summary>
internal static class FiberBuilder
{
    /// <summary>
    /// The resume delegate for a fiber about to suspend, with the dynamic context
    /// captured for it.
    ///
    /// <paramref name="core"/> is the builder's own field, and the builder lives
    /// inside <paramref name="stateMachine"/>. The first suspension of a call
    /// stores the new <see cref="CalledFiber{TStateMachine, T}"/> there BEFORE
    /// copying the state machine into it, so the copy carries a builder that
    /// already points at its promise and later <c>SetResult</c> calls, which run
    /// on the copy, reach it. The original stays on the caller's stack, and its
    /// builder is where <c>Task</c> is read, which needs the promise too.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Action GetMoveNextAction<TStateMachine, T>(
        ref Promise<T>? core, ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        var current = core;

        if (current is null)
        {
            var called = new CalledFiber<TStateMachine, T> { Context = FiberContext.Current };
            core = called;
            called.StateMachine = stateMachine;   // the one and only copy from the stack
            return called.MoveNextAction;
        }

        if (current is CalledFiber<TStateMachine, T> existing)
        {
            // Re-capture: the fiber may have changed its own context since the last
            // suspension, e.g. by entering a (parameterize ...).
            existing.Context = FiberContext.Current;
            return existing.MoveNextAction;
        }

        return ((FiberCore<T>)current).GetMoveNextAction(ref stateMachine);
    }

    /// <summary>
    /// Run the body on the caller's thread.
    ///
    /// No ExecutionContext capture or restore. The body runs on the caller's
    /// thread with the caller's dynamic environment already installed, which is
    /// exactly what a direct procedure call should look like.
    ///
    /// The caller's context is put back when this returns, because a call that
    /// *suspends* returns here with the callee's environment still installed:
    /// the body ran up to its first await on this thread, and anything it
    /// pushed on the way — a (parameterize ...), a cancellation scope — is
    /// still in the slot. The caller would then adopt it, and take its
    /// snapshot from it at its own next suspension.
    ///
    /// The callee loses nothing: its box captured this same environment inside
    /// MoveNext, at the await, which is what reinstates it when it resumes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Start<TStateMachine>(ref TStateMachine stateMachine)
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
}

public struct FiberMethodBuilder<T>
{
    /// <summary>
    /// Null until the call suspends, and then its <see cref="CalledFiber{TStateMachine, T}"/>.
    /// A spawned fiber's body starts with the spawn's <see cref="FiberCore{T}"/>
    /// here instead, because that promise exists before the body runs.
    /// </summary>
    private Promise<T>? _core;

    /// <summary>The value of a call that completed without suspending.</summary>
    private T _result;

    public static FiberMethodBuilder<T> Create()
    {
        var b = default(FiberMethodBuilder<T>);
        b._core = FiberCore<T>.TakeSpawning();
        return b;
    }

    // The compiler looks for a property literally named `Task`.
    public Fiber<T> Task => _core is null ? new Fiber<T>(_result) : new Fiber<T>(_core);

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
        => FiberBuilder.Start(ref stateMachine);

    public void SetStateMachine(IAsyncStateMachine stateMachine) { /* legacy/debugger only */ }

    public void SetResult(T result)
    {
        if (_core is null) _result = result;
        else _core.TrySetResult(result);
    }

    /// <summary>A call that fails before it suspends gets a promise here to carry the failure.</summary>
    public void SetException(Exception exception)
        => (_core ??= new Promise<T>()).TrySetException(exception);

    /// <summary>
    /// Routed to the unsafe path on purpose: BjoML never flows ExecutionContext.
    /// </summary>
    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
        => awaiter.OnCompleted(FiberBuilder.GetMoveNextAction(ref _core, ref stateMachine));

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
        => awaiter.UnsafeOnCompleted(FiberBuilder.GetMoveNextAction(ref _core, ref stateMachine));
}

public struct FiberMethodBuilder
{
    /// <summary>See <see cref="FiberMethodBuilder{T}"/>. Null at the end means success.</summary>
    private Promise<Unit>? _core;

    public static FiberMethodBuilder Create()
    {
        var b = default(FiberMethodBuilder);
        b._core = FiberCore<Unit>.TakeSpawning();
        return b;
    }

    public Fiber Task => new Fiber(_core);

    public void Start<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
        => FiberBuilder.Start(ref stateMachine);

    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    public void SetResult() => _core?.TrySetResult(default);

    public void SetException(Exception exception)
        => (_core ??= new Promise<Unit>()).TrySetException(exception);

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
        => awaiter.OnCompleted(FiberBuilder.GetMoveNextAction(ref _core, ref stateMachine));

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
        ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
        => awaiter.UnsafeOnCompleted(FiberBuilder.GetMoveNextAction(ref _core, ref stateMachine));
}
