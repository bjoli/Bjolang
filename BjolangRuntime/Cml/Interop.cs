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
using System.Threading;
using System.Threading.Tasks;

namespace Bjoml;

public static class Bjo
{
    // -----------------------------------------------------------------------
    // spawn
    // -----------------------------------------------------------------------

    /// <summary>
    /// <c>(spawn bjoroutine)</c>. Returns a <see cref="Promise{T}"/>, NOT a
    /// <see cref="Fiber{T}"/>: a fiber is a compiler artifact, while a promise is a
    /// first-class value that is both joinable and composable with <c>choose</c>.
    /// That composability is the whole point — you cannot write
    /// <c>(sync (choose (promise-join p) (channel-get cancel)))</c> if spawn hands
    /// back a task.
    ///
    /// The body starts on the pool rather than on the caller's stack, so spawn is
    /// genuinely concurrent instead of "run until the first suspension".
    ///
    /// The child INHERITS the spawning fiber's dynamic environment.
    ///
    /// Note for callers: C# cannot infer <typeparamref name="T"/> from an async
    /// lambda, so write <c>Bjo.Spawn&lt;int&gt;(async () =&gt; ...)</c>.
    /// </summary>
    public static Promise<T> Spawn<T>(Func<Fiber<T>> body)
    {
        var inherited = FiberContext.Current;
        var core = new FiberCore<T>(SpawnRunners<T>.FuncRunner, body, inherited);
        Scheduler.EnqueueSpawn(core);
        return core;
    }

    /// <summary>
    /// Spawn a bjoroutine with state and a result. Enables zero-closure static
    /// lambdas. The state is stored typed and inline (see
    /// <see cref="StatefulFiberCore{TState, T}"/>), so a value-typed state — the
    /// idiomatic tuple — is not boxed.
    /// </summary>
    public static Promise<TResult> Spawn<TState, TResult>(Func<TState, Fiber<TResult>> body, TState state)
    {
        var inherited = FiberContext.Current;
        var core = new StatefulFiberCore<TState, TResult>(SpawnStateRunners<TState, TResult>.StateRunner, body, state, inherited);
        Scheduler.EnqueueSpawn(core);
        return core;
    }

    /// <summary>Spawn a bjoroutine with no useful result.</summary>
    public static Promise<Unit> Spawn(Func<Fiber> body)
    {
        var inherited = FiberContext.Current;
        var core = new FiberCore<Unit>(SpawnUnitRunners.FuncRunner, body, inherited);
        Scheduler.EnqueueSpawn(core);
        return core;
    }

    /// <summary>
    /// Spawn a bjoroutine with state and no useful result. Enables zero-closure
    /// static lambdas; the state is stored typed and inline, unboxed.
    /// </summary>
    public static Promise<Unit> Spawn<TState>(Func<TState, Fiber> body, TState state)
    {
        var inherited = FiberContext.Current;
        var core = new StatefulFiberCore<TState, Unit>(SpawnStateRunners<TState, Unit>.UnitStateRunner, body, state, inherited);
        Scheduler.EnqueueSpawn(core);
        return core;
    }

    // -----------------------------------------------------------------------
    // entry point
    // -----------------------------------------------------------------------

    /// <summary>
    /// Run a bjoroutine on the current thread and block until it finishes. For the
    /// program entry point.
    ///
    /// DO NOT call this from a thread-pool thread: the fiber needs pool threads to
    /// make progress and you are holding one hostage.
    /// </summary>
    public static T RunToCompletion<T>(Func<Fiber<T>> body)
    {
        var p = body().AsPromise();
        WaitFor(p);
        return p.GetAwaiter().GetResult();
    }

    public static void RunToCompletion(Func<Fiber> body)
    {
        var p = body().AsPromise();
        WaitFor(p);
        p.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Block until a promise lands.
    ///
    /// Uses a monitor rather than a ManualResetEventSlim because the completing
    /// thread must not be able to touch a disposed handle after we wake.
    /// </summary>
    private static void WaitFor<T>(Promise<T> p)
    {
        var gate = new object();
        bool completed = false;

        p.GetAwaiter().OnCompleted(() =>
        {
            lock (gate)
            {
                completed = true;
                Monitor.Pulse(gate);
            }
        });

        lock (gate)
        {
            while (!completed) Monitor.Wait(gate);
        }
    }
}

internal static class SpawnRunners<T>
{
    public static readonly Action<FiberCore<T>> FuncRunner = static core =>
    {
        var func = (Func<Fiber<T>>)core._spawnBody!;
        var fiber = func();
        if (!ReferenceEquals(fiber.Core, core)) fiber.AsPromise().Forward(core);
    };
}

internal static class SpawnUnitRunners
{
    public static readonly Action<FiberCore<Unit>> FuncRunner = static core =>
    {
        var func = (Func<Fiber>)core._spawnBody!;
        var fiber = func();
        if (!ReferenceEquals(fiber.Core, core)) fiber.AsPromise().Forward(core);
    };
}

internal static class SpawnStateRunners<TState, TResult>
{
    public static readonly Action<FiberCore<TResult>> StateRunner = static core =>
    {
        var stateful = (StatefulFiberCore<TState, TResult>)core;
        var func = (Func<TState, Fiber<TResult>>)core._spawnBody!;
        var state = stateful.SpawnState;
        stateful.SpawnState = default!;   // the promise handle must not pin the args
        var fiber = func(state);
        if (!ReferenceEquals(fiber.Core, core)) fiber.AsPromise().Forward(core);
    };

    public static readonly Action<FiberCore<Unit>> UnitStateRunner = static core =>
    {
        var stateful = (StatefulFiberCore<TState, Unit>)core;
        var func = (Func<TState, Fiber>)core._spawnBody!;
        var state = stateful.SpawnState;
        stateful.SpawnState = default!;
        var fiber = func(state);
        if (!ReferenceEquals(fiber.Core, core)) fiber.AsPromise().Forward(core);
    };
}

public static class TaskInterop
{
    // -----------------------------------------------------------------------
    // Task -> Promise
    // -----------------------------------------------------------------------

    /// <summary>
    /// Bring a C# task into BjoML.
    ///
    /// The task is ALREADY RUNNING. Losing a <c>choose</c> does not stop it, it only
    /// drops the result: a task cannot be un-started, whereas a channel event that
    /// loses is cleanly withdrawn. Prefer <see cref="Cancellable{T}"/>, which makes
    /// the difference go away.
    /// </summary>
    public static Promise<T> FromTask<T>(Task<T> task)
    {
        var p = new Promise<T>();

        // ConfigureAwait(false) + UnsafeOnCompleted rather than ContinueWith:
        // ContinueWith captures an ExecutionContext and reinstates it around the
        // continuation, which is exactly what BjoML does not want.
        var awaiter = task.ConfigureAwait(false).GetAwaiter();
        if (awaiter.IsCompleted) Settle(task, p);
        else awaiter.UnsafeOnCompleted(() => Settle(task, p));

        return p;
    }

    private static void Settle<T>(Task<T> t, Promise<T> p)
    {
        if (t.IsCanceled) p.TrySetException(new TaskCanceledException(t));
        else if (t.IsFaulted) p.TrySetException(t.Exception!.GetBaseException());
        else p.TrySetResult(t.Result);
    }

    // -----------------------------------------------------------------------
    // Promise -> Task, for calling back into C#
    // -----------------------------------------------------------------------

    // No call sites yet. This is the .NET-calls-Bjolang direction; deleting it
    // closes that door and it is not obvious how to reopen it. The moment a
    // .NET API wants an async delegate written in Bjolang — an ASP.NET
    // `Func<HttpContext, Task>`, a `DelegatingHandler` — this is what code
    // generation emits at the boundary.
    public static Task<T> ToTask<T>(this Promise<T> p)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        p.GetAwaiter().OnCompleted(() =>
        {
            try { tcs.TrySetResult(p.GetAwaiter().GetResult()); }
            catch (Exception e) { tcs.TrySetException(e); }
        });

        return tcs.Task;
    }

    // -----------------------------------------------------------------------
    // The only form that should be exposed to the language for use in choose
    // -----------------------------------------------------------------------

    /// <summary>
    /// Wrap a cancellable async operation as a proper CML event: if this branch
    /// loses the <c>choose</c>, the nack fires, the token is cancelled and the
    /// underlying work actually stops rather than running on to no purpose.
    ///
    /// Language surface: <c>(task-&gt;event (lambda (cancel-token) ...))</c>. Making
    /// the uncancellable form hard to reach is worth the inconvenience; otherwise
    /// every choose over I/O leaks work.
    ///
    /// Note this is still a PERSISTENT event: once the task has completed, syncing
    /// on it again yields the same value immediately, so it will win every iteration
    /// of a loop and starve its siblings. Same trap as <c>Cml.Always</c>.
    /// </summary>
    public static IEvent<Result<T>> Cancellable<T>(Func<CancellationToken, Task<T>> start)
        => Cml.WithNack<Result<T>>(nack =>
        {
            var cts = new CancellationTokenSource();

            // Dispose exactly once, from whichever of the two paths gets there first.
            int disposed = 0;
            void DisposeOnce()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0) cts.Dispose();
            }

            // A bare callback rather than a spawned fiber. The proposed version
            // spawned a fiber to await the nack, which meant that whenever this
            // branch WON, that fiber parked forever holding the token source. This
            // runs no user code, so it is safe outside a fiber context.
            Cml.Sync(nack, _ =>
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* the task already finished */ }
                DisposeOnce();
            });

            Task<T> task;
            try
            {
                task = start(cts.Token);
            }
            catch (Exception e)
            {
                // A synchronous throw from the starter is a result, not a crash.
                DisposeOnce();
                return Cml.Always(Result<T>.Fail(e));
            }

            var p = FromTask(task);

            // The winning path also has to clean up; the nack will never fire there.
            p.OnCompleted(DisposeOnce);

            return p.Join();
        });
}
