using Bjoml;

// A partial of its own so that this file can have `using Bjoml;` at the top
// without it reaching the rest of the runtime, where `Bjoml.Result<T>` — a
// different type from `BjolangRuntime.Result<TErr, TOk>`, carrying an
// `ExceptionDispatchInfo` rather than an error value — would be one name too
// many. The `using` is not optional here: `IEvent<T>.GetAwaiter` is an
// extension method, so `await ev` below does not compile without it.
public static partial class BjolangRuntime {

    /// `(sync ev)` — the yield point, and the only one besides calling a
    /// bjoroutine.
    ///
    /// A bjoroutine as far as the compiler is concerned: its Bjolang type is a
    /// suspending arrow, so `ColourCheck` treats a call to it as a yield point
    /// and code generation wraps that call in an `await`, both without knowing
    /// anything about this method in particular. Building an event is pure and
    /// syncing it suspends, which is the split that makes `choose` possible —
    /// see concurrency-design.md §5.
    public static async Fiber<T> sync<T>(IEvent<T> ev) => await ev;

    /// `(promise-join p)` — the joinable event. Pure: it allocates nothing that
    /// suspends, and hands back a description of a synchronisation that has not
    /// happened. `(sync (promise-join p))` is what waits.
    ///
    /// Failure arrives as a value rather than as a throw, because an exception
    /// raised inside an event continuation lands in a channel's matching loop
    /// and wedges the whole sync block. The conversion here is between two
    /// unrelated `Result`s: BjoML's carries an `ExceptionDispatchInfo` so that a
    /// rethrow keeps the original stack, and Bjolang's carries a plain error
    /// value because that is what `match` on `(Err e)` binds.
    ///
    /// `SourceException` rather than `Throw()`: this runs at sync time, not on
    /// the joining fiber's stack, so it must not raise.
    public static IEvent<Result<Exception, T>> promisesubjoin<T>(Promise<T> p) =>
        Cml.Wrap(
            p.Join(),
            static r =>
                r.IsError
                    ? Result<Exception, T>.Err(r.Error!.SourceException)
                    : Result<Exception, T>.Ok(r.Value));

    /// `(spawn-thunk f)` — the first-class counterpart of the `bjo` special
    /// form, for the higher-order case `(map spawn-thunk thunks)`.
    ///
    /// A `(-> %a)` is an ordinary function and so cannot suspend, which is the
    /// whole difference from `bjo`: this spawns work that runs to completion
    /// without ever yielding. It is still genuinely concurrent — the body starts
    /// on the pool, not on the caller's stack.
    public static Promise<T> spawnsubthunk<T>(Func<T> f) =>
        Bjo.Spawn<Func<T>, T>(static g => RunThunk(g), f);

    /// The state-taking shape of `Spawn` exists to avoid a closure, so the
    /// lambda above must not capture: the thunk travels as the state argument
    /// and this is what unpacks it.
    private static async Fiber<T> RunThunk<T>(Func<T> f) => f();

    /// `(promise-done? p)` — has it landed? Answered without suspending, for
    /// code that wants to look rather than wait.
    public static bool promisesubdone_QMARK<T>(Promise<T> p) => p.IsCompleted;

    // -----------------------------------------------------------------------
    // Channels and the event combinators
    // -----------------------------------------------------------------------
    //
    // Every one of these is an ordinary function, and that is the discipline
    // rather than an implementation detail: *building* an event allocates and
    // returns, *syncing* it is the yield point. Nothing below can suspend, so
    // nothing below needs a suspending arrow, and an event may therefore be
    // built anywhere — stored in a record, returned from a `defun`, assembled
    // inside a `choose` that then discards it. That split is what makes a
    // composite event withdrawable, and withdrawable is what makes `choose`
    // sound.

    /// `(make-chan)` — a synchronous, unbuffered channel. A send and a receive
    /// rendezvous: neither completes until the other arrives, which is where
    /// backpressure comes from for free.
    public static Channel<T> makesubchan<T>() => new Channel<T>();

    /// `(chan-send ch v)` — the event of handing `v` over. Not the handing
    /// over: that happens at the `sync`.
    public static IEvent<Unit> chansubsend<T>(Channel<T> ch, T value) => ch.Send(value);

    /// `(chan-recv ch)` — the event of taking one message.
    public static IEvent<T> chansubrecv<T>(Channel<T> ch) => ch.Receive();

    /// `(choose ev ...)` — offer several, commit to exactly one.
    ///
    /// The losers are *withdrawn*, not merely ignored: a losing `chan-recv` is
    /// removed from the channel's taker queue, so nothing is left parked
    /// waiting for a message that will never be read.
    public static IEvent<T> choose<T>(params IEvent<T>[] events) => Cml.Choose(events);

    /// `(wrap ev f)` — `f` runs *after* the commit, on the syncing fiber, so it
    /// is safe to do real work in it. This is `map` for events, and it is the
    /// only lawful piece of a monad they have: a `bind` would give a composite
    /// two commit points, and a two-commit event cannot be withdrawn.
    public static IEvent<U> wrap<T, U>(IEvent<T> ev, Func<T, U> mapper) => Cml.Wrap(ev, mapper);

    /// `(guard thunk)` — build the event fresh at each `sync`.
    ///
    /// What you need whenever the event depends on the moment of
    /// synchronisation. A timeout is the canonical case: built once and reused,
    /// its deadline is in the past after the first iteration and its branch
    /// wins every time thereafter.
    public static IEvent<T> guard<T>(Func<IEvent<T>> generator) => Cml.Guard(generator);

    /// `(with-nack gen)` — `gen` is handed an event that fires when this branch
    /// *loses*, and returns the branch itself.
    ///
    /// For a branch that acquires something which must be released if it does
    /// not win: a timer, a cancellation source, a tentative reservation on a
    /// server. Without it, every losing `choose` leaks whatever the branch
    /// took.
    ///
    /// The nack is carried on a promise rather than a channel, deliberately: a
    /// nack is a *fact*, so every listener must see it and a late listener must
    /// still see it. A channel-based nack would park an operation only one
    /// receiver could consume.
    ///
    /// A nack callback must never run user code. It runs on a borrowed thread
    /// with whatever context that thread happened to have; it may wake a fiber,
    /// it may not *be* the work.
    public static IEvent<T> withsubnack<T>(Func<IEvent<Unit>, IEvent<T>> generator) =>
        Cml.WithNack(generator);

    /// `(always v)` — already available, and therefore *persistent*: it wins
    /// every iteration of a `choose` loop it appears in. That is the definition
    /// rather than a defect, and it is the same trap a completed promise sets.
    public static IEvent<T> always<T>(T value) => Cml.Always(value);

    /// `(never)` — never available. The identity of `choose`, and what a branch
    /// that has nothing to offer this time around returns.
    public static IEvent<T> never<T>() => Cml.Never<T>();

    // -----------------------------------------------------------------------
    // Timers
    // -----------------------------------------------------------------------

    /// `(timeout ms)` — available `ms` after each *sync*, not after it is built.
    ///
    /// Relative, and rebuilt at every sync, which is what "five seconds from
    /// now" has to mean inside a loop. Built once and reused without that, the
    /// deadline would be in the past after the first iteration and this branch
    /// would win every time round thereafter — silently, because winning a race
    /// is not an error.
    public static IEvent<Unit> timeout(int ms) => Cml.Timeout(ms);

    /// `(at-time deadline)` — available at a fixed instant.
    ///
    /// Absolute, and therefore not interchangeable with `timeout`: the deadline
    /// is decided when this is built and only the remaining interval is
    /// recomputed, which is what makes it usable as a budget for a whole loop
    /// rather than for one iteration of it.
    public static IEvent<Unit> atsubtime(DateTime utcDeadline) => Cml.At(utcDeadline);

    // -----------------------------------------------------------------------
    // Detaching
    // -----------------------------------------------------------------------

    /// `(detach p)` — stop listening, deliberately.
    ///
    /// Dropping a promise instead loses any exception inside it silently:
    /// nothing else is watching, so a fiber that died is simply never
    /// mentioned. This says "I know, and I still do not want the result",
    /// routing a failure to the scheduler's unhandled-exception hook.
    ///
    /// This is what `(ignore p)` will mean once §8.2's `Discard` trait exists.
    /// Until then it has to be named.
    public static Unit detach<T>(Promise<T> p) {
        p.Detach();
        return default;
    }

    // -----------------------------------------------------------------------
    // Cancellation
    // -----------------------------------------------------------------------
    //
    // A cancellation token is a `Promise<Unit>` and nothing more. That is the
    // whole of §6.1: a token is "a persistent event that fires once", a promise
    // is already exactly that, and so cancellation needs no scheduler support,
    // no flag to poll and no callback registry. Bjolang sees a distinct type —
    // `CancelToken` is nominal, so `promise-join` and `detach` cannot be aimed
    // at one — but nothing here has to enforce that, because the type system
    // already has.
    //
    // Persistence is the design and also the cost. Every listener sees a
    // cancellation and a late listener still sees it, which is what "cancelled"
    // has to mean; the price is that a cancelled scope is *finished*, and
    // carrying on needs a fresh token rather than a reset. It is also the trap
    // §4.4 describes: a cancelled token in a `choose` wins every iteration
    // thereafter, so a loop that races one must leave rather than go round.

    /// `(make-cancel)` — a cancel thunk and the token it fires, as a pair.
    ///
    /// Two values rather than one because they are two different rights. The
    /// thunk is the capability to cancel; the token is only the ability to
    /// notice. Handing a worker a token lets it watch its own deadline without
    /// also letting it cancel its siblings, and that separation disappears the
    /// moment both live behind one handle.
    ///
    /// Cancelling twice is a no-op — `TrySetResult` loses the race and says so
    /// — so the thunk needs no guard at its call sites and can be handed to a
    /// nack, a finaliser and a supervisor at once.
    public static ValueTuple<Func<Unit>, Promise<Unit>> makesubcancel() {
        var token = new Promise<Unit>();
        return new ValueTuple<Func<Unit>, Promise<Unit>>(
            () => { token.TrySetResult(default); return default; },
            token);
    }

    /// `(cancelled ct)` — the event of this token having fired.
    ///
    /// An event rather than a callback, so cancellation composes with
    /// everything else a fiber might be waiting for: `(choose (chan-recv jobs)
    /// (cancelled ct))` is a worker that is interruptible while parked, which
    /// is the only kind of interruptible that costs nothing.
    ///
    /// The outcome is discarded down to its value because a token cannot fail:
    /// the only writer Bjolang can reach is `make-cancel`'s thunk, which calls
    /// `TrySetResult`, and `link-cancel` only ever forwards from another token.
    public static IEvent<Unit> cancelled(Promise<Unit> ct) =>
        Cml.Wrap(ct.Join(), static r => r.Value);

    /// `(cancelled? ct)` — has it fired, right now?
    ///
    /// For the compute loop with no yield point to hang a `choose` on. §6.1's
    /// cooperative limitation is exactly this: a loop that never syncs and
    /// never checks runs to completion no matter who cancelled what.
    public static bool cancelled_QMARK(Promise<Unit> ct) => ct.IsCompleted;

    /// `(link-cancel parent child)` — cancelling the parent cancels the child.
    ///
    /// One-directional on purpose: a child that cancels itself must not take
    /// its parent's other children down with it. Building a tree is repeated
    /// linking, and the leaf that fails cancels only what hangs below it.
    ///
    /// `Forward` is safe as a bare completion callback in a way user code never
    /// is — it stores a value into another promise and returns — so this is one
    /// of the few things §5.4 allows on a borrowed thread.
    public static Unit linksubcancel(Promise<Unit> parent, Promise<Unit> child) {
        parent.Forward(child);
        return default;
    }

    // -----------------------------------------------------------------------
    // Escaping the synchronous BCL
    // -----------------------------------------------------------------------

    /// `(blocking thunk)` — run something that parks a thread, somewhere else.
    ///
    /// `File.ReadAllText`, `Console.ReadLine`, `Thread.Sleep`, `Monitor.Enter`:
    /// each of these parks the thread it is called on, and a fiber calling one
    /// parks a *pool* thread. `SpawnBatch`'s watchdog stops that from stranding
    /// the fibers queued behind it, but it does not get the thread back, and
    /// enough of them starves the pool. §7.5.
    ///
    /// `Task.Run` moves the parking somewhere the pool can grow to cover, and
    /// the fiber suspends on the result rather than on the work — so the pool
    /// thread the *fiber* was on goes back immediately. That is the whole
    /// trade: one thread is still parked, but it is not one that other fibers
    /// are queued behind.
    ///
    /// Guarded, so each sync starts a fresh run rather than replaying the first
    /// one's answer. Failure is a value, as everywhere else that resolves at
    /// sync time.
    ///
    /// Not cancellable, and it cannot be: the thunk is arbitrary synchronous
    /// code with no token to hand it and no yield point to interrupt. Losing a
    /// `choose` on this stops you listening and nothing else — which is exactly
    /// what `task->event` exists to avoid, and is why this is the last resort
    /// rather than the way to call .NET.
    public static IEvent<Result<Exception, T>> blocking<T>(Func<T> work) =>
        Cml.Guard(() =>
            Cml.Wrap(
                TaskInterop.FromTask(Task.Run(work)).Join(),
                static r =>
                    r.IsError
                        ? Result<Exception, T>.Err(r.Error!.SourceException)
                        : Result<Exception, T>.Ok(r.Value)));

    /// `(async-seq->chan s)` — a .NET async stream as a channel, plus a promise
    /// that says when it is over.
    ///
    /// An `IAsyncEnumerable<T>` is not composable: you can `await foreach` it
    /// and nothing else. It cannot go into a `choose`, cannot be raced against
    /// a timeout, and cannot be withdrawn. A channel can do all three, so a
    /// fiber pumps one into the other and the stream becomes an ordinary CML
    /// citizen. §7.6.
    ///
    /// Backpressure is free, and it is the reason this is a *synchronous*
    /// channel: the pump blocks in the rendezvous until a receiver takes the
    /// item, so a slow consumer stops the producer rather than growing a
    /// buffer behind it.
    ///
    /// The second half of the pair exists because `Channel` has no close — and
    /// would be the wrong answer even if it had one, since a closed channel
    /// cannot say *why* it closed. A promise carries the end and the failure
    /// both. The rendezvous is what makes the pair exact rather than racy: the
    /// pump cannot complete until its last send was taken, so a fired promise
    /// means a genuinely empty channel and the consumer's `choose` between the
    /// two branches never has to guess.
    ///
    /// **Limitation:** the pump only notices cancellation *between* items. A
    /// stream that stalls in the middle of producing one stalls the pump with
    /// it, since the token is the enumerator's to honour and nothing here can
    /// interrupt a `MoveNextAsync` that has stopped answering.
    public static ValueTuple<Channel<T>, Promise<Unit>> asyncsubseqsubgtchan<T>(
        IAsyncEnumerable<T> source) {

        var channel = new Channel<T>();

        var finished = Bjo.Spawn<Unit>(async () => {
            // Read inside the fiber rather than at the spawn: `Bjo.Spawn`
            // installs the captured environment before the body runs, so this
            // is the token of the scope that asked for the stream.
            await foreach (var item in source
                               .WithCancellation(AmbientCancellation())
                               .ConfigureAwait(false)) {
                await channel.Send(item);
            }

            return default;
        });

        return new ValueTuple<Channel<T>, Promise<Unit>>(channel, finished);
    }

    /// `(spawn-evt (f x))` — start it at the sync, cancel it if the branch loses.
    ///
    /// The gap this closes is §7.4's: `(promise-join (bjo (f x)))` starts the
    /// work eagerly and losing the `choose` only stops you *listening* — the
    /// child runs on with nobody waiting. That is limitation 12, and it is
    /// invisible, since a promise nobody joins looks exactly like one that was
    /// never made.
    ///
    /// So the child is spawned under a cancellation token of its own, and the
    /// nack fires it. The token is installed on the dynamic environment around
    /// the starter rather than passed to it, which is what makes the whole
    /// subtree inherit it: `bjo` captures the environment, so the child, its
    /// children, and every `#:async` call any of them makes all see the same
    /// token without a parameter anywhere.
    ///
    /// A bare nack callback, not a fiber that waits on the nack. A fiber would
    /// park forever in every case where the branch *wins* — the nack promise
    /// never completes — which is the mistake `TaskInterop.Cancellable`'s
    /// comment records having made once already. Firing a token is
    /// `TrySetResult`: no user code, safe on a borrowed thread.
    ///
    /// Note what this does *not* do: cancellation is still cooperative. A child
    /// that never syncs and never checks `cancelled?` runs to completion no
    /// matter who lost what.
    public static IEvent<Result<Exception, T>> spawnsubevtdivstart<T>(Func<Promise<T>> start) =>
        Cml.WithNack<Result<Exception, T>>(nack => {
            var token = new Promise<Unit>();

            // Pushed and restored around the spawn exactly as `parameterize`
            // would, and for the same reason: the child inherits the
            // environment as it was when the spawn ran, and this fiber must not
            // keep the binding afterwards.
            var saved = parametersubpush_BANG(currentsubcancel, token);
            Promise<T> child;
            try {
                child = start();
            } finally {
                dynsubrestore_BANG(saved);
            }

            Cml.Sync(nack, _ => token.TrySetResult(default));

            return Cml.Wrap(
                child.Join(),
                static r =>
                    r.IsError
                        ? Result<Exception, T>.Err(r.Error!.SourceException)
                        : Result<Exception, T>.Ok(r.Value));
        });

    /// `(current-cancel)` — the ambient token.
    ///
    /// `bjo` captures the dynamic environment, so a `parameterize` around a
    /// spawn gives the token to the whole subtree beneath it without a
    /// parameter in any signature. Snapshot-at-spawn is the correct semantics
    /// here for a reason that does not hold for a mutable cell: a token is an
    /// immutable handle, so the child and the parent looking at "the same
    /// token" really are looking at the same thing.
    ///
    /// The default is a promise nobody holds the other half of. Never completed
    /// and never completable, so an unparameterized program sees a token that
    /// never fires — the root scope, which is what it is.
    ///
    /// It is a named field rather than an inline `new` so that
    /// <see cref="AmbientCancellation"/> can recognise it: a program that never
    /// parameterized anything must not pay for a `CancellationTokenSource` it
    /// could never fire. Declared *before* the parameter that holds it, because
    /// static field initializers run in declaration order and the other way
    /// round binds the default to null.
    private static readonly Promise<Unit> RootCancel = new();

    public static readonly Param<Promise<Unit>> currentsubcancel = makesubparameter(RootCancel);

    // -----------------------------------------------------------------------
    // The bridge to .NET
    // -----------------------------------------------------------------------

    /// One `CancellationTokenSource` per token that has ever been handed to a
    /// .NET call.
    ///
    /// Keyed weakly, so a source lives exactly as long as the token it
    /// mirrors and a scope that has come and gone takes its source with it.
    /// The alternative — a fresh source per call — would need a registration
    /// per call to tear down, and it would tear it down on whichever thread
    /// happened to be finishing, which is the one place §5.4 says nothing may
    /// happen.
    ///
    /// Never disposed, deliberately. A `CancellationTokenSource` with no timer
    /// and no wait handle holds nothing a finaliser would want back, and
    /// disposing one is only correct after every registration on it is gone —
    /// which is exactly the bookkeeping a per-token source avoids having.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Promise<Unit>, CancellationTokenSource> CancelBridges = new();

    /// The ambient Bjolang cancellation token, as .NET wants to be given it.
    ///
    /// Emitted by the compiler as the trailing argument of every `#:async`
    /// import that has a `CancellationToken` overload — see
    /// concurrency-design.md §7.2. Nobody writes this by hand, which is the
    /// point: a token the caller has to remember is a token the caller forgets,
    /// and an HTTP request that outlives the scope that wanted it is the
    /// failure that follows.
    ///
    /// Three cases, and the first two are the common ones:
    ///
    ///   * nothing has been parameterized, so there is nothing to cancel and
    ///     `None` is both correct and free;
    ///   * the scope is already cancelled, so the call should not start —
    ///     handing .NET a token that is already cancelled is how you say that
    ///     without a check at every call site;
    ///   * otherwise, the source mirroring this token, created once.
    public static CancellationToken AmbientCancellation() {
        var token = parametersubref(currentsubcancel);

        if (ReferenceEquals(token, RootCancel)) return CancellationToken.None;
        if (token.IsCompleted) return new CancellationToken(true);

        return AmbientBridge(token).Token;
    }

    /// The source mirroring one token, created once.
    private static CancellationTokenSource AmbientBridge(Promise<Unit> token) =>
        CancelBridges.GetValue(token, static t => {
            var cts = new CancellationTokenSource();

            // A bare callback rather than a fiber that waits: cancelling a
            // source runs .NET's own registrations and nothing of the user's,
            // so it is safe on the borrowed thread that delivers it. A fiber
            // here would park forever in every scope that is never cancelled,
            // which is most of them.
            Cml.Sync(t.Join(), _ => {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* nothing left to cancel */ }
            });

            return cts;
        });

    /// `(task->event (fetch url))` — an async .NET call as a withdrawable event.
    ///
    /// Emitted by the compiler, never written: the surface form is a special
    /// form, because its operand must *not* be evaluated where it stands. What
    /// arrives here is a starter that has not been called — which is the whole
    /// difference from `FromTask`, and the reason `FromTask` is not in the
    /// language. A task handed over already running cannot be withdrawn from a
    /// `choose`: losing would drop the result and leave the work going, whereas
    /// this one is started at the sync and cancelled if its branch loses.
    ///
    /// Two tokens have to become one. The branch's, which
    /// `TaskInterop.Cancellable` fires from the nack, and the ambient one, so
    /// that cancelling the scope stops the call as it would stop any other —
    /// §7.3. `CreateLinkedTokenSource` is how .NET spells that, and the link is
    /// disposed when the task lands whichever way it went.
    ///
    /// Wrapped in a `guard` so the ambient token is read at *sync* time, on the
    /// fiber doing the syncing. Read when the event was built it would be the
    /// building fiber's, which is the same mistake `timeout` would make without
    /// its own guard: an event is a value, and a value can be built in one
    /// scope and synced in another.
    ///
    /// Failure is a value, as it is at a join, and for the same reason: this
    /// runs at sync time rather than on the thread that completed the task, so
    /// it must not raise. Cancellation arrives that way too — a losing branch's
    /// `Err` holds a `TaskCanceledException` that nobody ever looks at.
    public static IEvent<Result<Exception, T>> TaskEvent<T>(Func<CancellationToken, Task<T>> start) =>
        Cml.Guard(() => {
            var ambient = AmbientCancellation();

            Func<CancellationToken, Task<T>> scoped =
                !ambient.CanBeCanceled
                    // Nothing to link to, so nothing to allocate. The common
                    // case: a program that never parameterized a token.
                    ? start
                    : branch => {
                        var linked = CancellationTokenSource.CreateLinkedTokenSource(branch, ambient);

                        try {
                            var task = start(linked.Token);

                            task.ContinueWith(
                                static (_, s) => ((CancellationTokenSource)s!).Dispose(),
                                linked,
                                CancellationToken.None,
                                TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);

                            return task;
                        } catch {
                            // A synchronous throw from the starter is still a
                            // result — `Cancellable` turns it into one — but
                            // the link is this method's to clean up.
                            linked.Dispose();
                            throw;
                        }
                    };

            return Cml.Wrap(
                TaskInterop.Cancellable(scoped),
                static r =>
                    r.IsError
                        ? Result<Exception, T>.Err(r.Error!.SourceException)
                        : Result<Exception, T>.Ok(r.Value));
        });
}
