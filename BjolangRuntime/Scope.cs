using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using Bjoml;

// A partial of its own, for the reason `Concurrency.cs` is one: `using Bjoml;`
// brings `Bjoml.Result<T>` into scope, and that is a different type from
// `BjolangRuntime.Result<TErr, TOk>`. Keeping the `using` inside one file keeps
// the two apart everywhere else. The `using` is needed here because the drain
// writes `await someEvent`, and `IEvent<T>.GetAwaiter` is an extension method.
public static partial class BjolangRuntime {

    // -----------------------------------------------------------------------
    // Scopes
    // -----------------------------------------------------------------------
    //
    // A scope is a cancellation token plus the fibers that were started under
    // it. `with-cancel` opens one, `spawn` and `bjo` put fibers into it, and the
    // scope does not return until every one of those fibers has finished.
    //
    // # What this replaces
    //
    // Before this, a scope held a token and nothing else. A program that wanted
    // to know its workers had stopped had to keep the promises itself, cancel by
    // hand, and join them one at a time:
    //
    //     (def crew (hire jobs answers 8))
    //     (def feed (bjo (feeder jobs urls)))
    //     ...
    //     (cancel (Requested "enough"))
    //     (ignore (sync (promise-join feed)))
    //     (reap crew)
    //
    // Three mechanisms, all of them bookkeeping, and all of them easy to forget.
    // Forgetting them was invisible: a fiber nobody joins looks exactly like a
    // fiber that was never started.
    //
    // # The one rule
    //
    // When `with-cancel` returns, the work it started is over. Everything below
    // exists to make that sentence true.

    /// <summary>
    /// One fiber the scope is waiting for.
    ///
    /// # Why a second promise per child
    ///
    /// The scope holds children of many different result types — a
    /// `(Promise string)` next to a `(Promise Unit)` — and it has to wait on all
    /// of them at once, so it needs one list of one type. `Done` is that type: a
    /// `Promise&lt;Unit&gt;` that is completed when the child finishes, whichever
    /// way it finished.
    ///
    /// The alternative was a non-generic child interface wrapping each child's
    /// own promise. That reads better but cannot get at the *outcome*: BjoML
    /// keeps `Promise.Outcome` internal, so the only public way to read a failed
    /// promise is `GetResult()`, which throws — and a drain that has to catch an
    /// exception per failed child in order to collect it is worse than an extra
    /// object per child.
    ///
    /// # Why the error is a field and not the promise's own failure
    ///
    /// `Done` always completes *successfully*. The child's failure is put in
    /// <see cref="Error"/> first, so the drain can read it with a field load
    /// rather than by catching a rethrow. Setting the field before completing
    /// the promise is what makes it visible: `TrySetResult` is a full fence, and
    /// the drain only reads `Error` after it has seen `Done.IsCompleted`.
    /// </summary>
    private sealed class ScopeChild {
        internal readonly Promise<Unit> Done = new();
        internal volatile ExceptionDispatchInfo? Error;

        /// <summary>
        /// Does this child's failure belong to the scope?
        ///
        /// True for `(spawn ...)`, which hands nothing back: nobody else can be
        /// watching, so if the scope does not report the failure then nothing
        /// does and a fiber that died is never mentioned.
        ///
        /// False for `(bjo ...)`, which hands back the promise. The failure is
        /// already on that promise, and the caller asked for it — the whole
        /// reason to write `bjo` rather than `spawn` is to read the outcome:
        ///
        ///     (match (sync (promise-join p))
        ///       ((Ok v)  (use v))
        ///       ((Err e) (recover e)))
        ///
        /// A scope that reported it as well would raise past a program that had
        /// just handled it.
        ///
        /// The scope still *waits* for a `bjo` child either way. Waiting is
        /// about when the scope is over; reporting is about who owns the
        /// failure, and they are different questions.
        /// </summary>
        internal bool Reports;
    }

    /// <summary>
    /// Did this fiber stop because it was told to, rather than because
    /// something went wrong?
    ///
    /// Used in two places that must agree: a scope does not report a cancelled
    /// child as a failure, and `detach` does not print one to stderr. Both are
    /// the same fact — the cancellation was asked for, so it is not news.
    ///
    /// # Two spellings, and why the token decides between them
    ///
    /// A `Cancelled` is unambiguous: only a fired token can produce one.
    ///
    /// `OperationCanceledException` is not. It is how .NET says cancellation,
    /// and a fiber parked in a bare `#:async` call unwinds with one when the
    /// ambient token reaches the .NET side — so it means "we were cancelled"
    /// there. But `HttpClient` also raises it when a request times out, and that
    /// is a genuine failure nobody asked for.
    ///
    /// The token is what tells them apart. If <paramref name="token"/> has not
    /// fired then nothing asked this fiber to stop, so whatever .NET was
    /// complaining about is a failure and must propagate. Without that test the
    /// rule would swallow every request timeout in the program.
    ///
    /// <paramref name="token"/> is null where there is no token to consult — a
    /// detached fiber has neither scope nor token by construction, so its
    /// `OperationCanceledException` is always a failure.
    /// </summary>
    private static bool IsCancellation(ExceptionDispatchInfo e, Promise<CancelReason>? token) {
        var ex = e.SourceException;
        if (ex is Bjolang.Runtime.Cancelled) return true;
        return ex is System.OperationCanceledException && token is { IsCompleted: true };
    }

    /// <summary>
    /// Stop listening to a promise, but still say something if it failed for a
    /// reason nobody asked for.
    ///
    /// What `Promise.Detach` does, minus the cancellations. A daemon ends by
    /// being cancelled — that is what a daemon is for — and printing
    /// "unhandled exception" every time a scope closes would be noise on every
    /// well-behaved program.
    ///
    /// The callback stores nothing and runs no user code, so it is safe on the
    /// borrowed thread that delivers it.
    /// </summary>
    internal static void ReportUnlessCancelled<T>(Promise<T> p, Promise<CancelReason>? token) {
        Cml.Sync(p.Join(), r => {
            if (r.IsError && !IsCancellation(r.Error!, token))
                Scheduler.ReportUnhandled(r.Error!.SourceException);
        });
    }

    /// <summary>
    /// A cancellation scope, and the fibers started inside it.
    ///
    /// Opaque to Bjolang: the only things that touch one are the four `spawn`
    /// forms and the `with-cancel` / `with-deadline` / `with-shield` macros,
    /// which open it, install it and close it again.
    ///
    /// # Room left on purpose
    ///
    /// A scope owns fibers and nothing else today. Eio's switches also own file
    /// handles, and `with-response` in `std/http` does the same job for one
    /// type. Unifying them is worth doing later, and nothing here is in the way
    /// of it: a release action would be a second list beside
    /// <see cref="_children"/>, run at the end of <see cref="Close"/> where the
    /// deadline timer is disposed today. It is deliberately not added yet,
    /// because a list nothing writes to is a list nobody maintains.
    /// </summary>
    public sealed class Scope {

        /// <summary>
        /// The scope's cancellation token. Fires at most once, and everything
        /// started under the scope inherits it as the ambient token, so firing
        /// it stops the whole subtree.
        /// </summary>
        public readonly Promise<CancelReason> Token = new();

        /// <summary>
        /// Guards <see cref="_children"/> and <see cref="_closed"/> only. No
        /// user code and no spawn ever runs while this is held: a spawn takes
        /// the lock to add a child, drops it, and only then starts the fiber.
        /// </summary>
        private readonly object _gate = new();

        private readonly List<ScopeChild> _children = new();

        /// <summary>
        /// Set the moment closing begins. After this, a spawn into this scope
        /// does nothing — see <see cref="TryEnlist"/>.
        /// </summary>
        private bool _closed;

        /// <summary>
        /// The scope's deadline, or null when it has none. A bare
        /// <see cref="System.Threading.Timer"/> rather than a fiber that races
        /// the token.
        ///
        /// A fiber was the old shape and it cannot work here. The scope waits
        /// for its children before it fires its token, and the watcher fiber
        /// was waiting for that token — so the scope would wait for the watcher
        /// and the watcher would wait for the scope. A timer is outside that
        /// cycle. Its callback only calls `TrySetResult`, which runs no user
        /// code, so it is safe on the timer's own thread.
        /// </summary>
        private System.Threading.Timer? _deadline;

        internal Scope(int deadlineMs, Promise<CancelReason>? parent) {
            // Linking is one-directional: the parent cancels the child, never
            // the other way round. A `with-cancel` inside a `with-cancel` that
            // stops early must not take its parent's other work down with it.
            //
            // `with-shield` passes null here, and that missing link is the whole
            // of what a shield is.
            if (parent is not null && !ReferenceEquals(parent, RootCancel))
                parent.Forward(Token);

            if (deadlineMs > 0)
                _deadline = new System.Threading.Timer(
                    static s => ((Scope)s!).Token.TrySetResult(new CancelReason.Deadline()),
                    this,
                    deadlineMs,
                    System.Threading.Timeout.Infinite);
        }

        /// <summary>Has closing started? Read by the daemon and detached spawns.</summary>
        internal bool IsClosed { get { lock (_gate) return _closed; } }

        /// <summary>
        /// Take a slot for a fiber that is about to be started, or answer null
        /// if this scope is already closing.
        ///
        /// # Why the slot is taken before the fiber is started
        ///
        /// The child is in the list, unfinished, before it exists. So there is
        /// no window in which the drain can look at the list, find it empty and
        /// declare the scope over while a fiber is on its way to the run queue.
        /// Registering after the spawn would leave exactly that window, and it
        /// is the window in which a scope returns with work still running —
        /// which is the one thing this whole file exists to prevent.
        ///
        /// # Why not simply hold the lock across the spawn
        ///
        /// Because starting a fiber reaches into the scheduler, and the
        /// scheduler is allowed to run work inline. Holding a lock across it
        /// would mean holding a lock across arbitrary other fibers' code.
        /// </summary>
        private ScopeChild? TryEnlist(bool reports) {
            lock (_gate) {
                if (_closed) return null;
                var child = new ScopeChild { Reports = reports };
                _children.Add(child);
                return child;
            }
        }

        /// <summary>
        /// Wire a started fiber to the slot it was given: when the fiber lands,
        /// its failure — if any — is stored and the slot is marked done.
        ///
        /// A bare completion callback rather than a fiber that joins. A joining
        /// fiber would be one more fiber per child, and it would have to be
        /// waited for too. This callback stores two fields and returns, which is
        /// the rule for anything running on a borrowed thread.
        /// </summary>
        private static void Attach<T>(Promise<T> fiber, ScopeChild slot) {
            Cml.Sync(fiber.Join(), r => {
                if (r.IsError) slot.Error = r.Error;
                slot.Done.TrySetResult(default);
            });
        }

        /// <summary>
        /// Start <paramref name="body"/> as a child of this scope, or do nothing
        /// at all if the scope is already closing.
        ///
        /// Returns null in the second case, and the caller decides what that
        /// means for its own result type.
        ///
        /// <paramref name="reports"/> says whether a failure of this child is
        /// the scope's to raise; see <see cref="ScopeChild.Reports"/>.
        /// </summary>
        internal Promise<T>? Start<T>(System.Func<Fiber<T>> body, bool reports) {
            var slot = TryEnlist(reports);
            if (slot is null) return null;

            var fiber = Bjo.Spawn(body);
            Attach(fiber, slot);
            return fiber;
        }

        /// <summary>
        /// End the scope: cancel the children if something went wrong, then wait
        /// for every one of them to actually finish, and only then report.
        ///
        /// <paramref name="bodyFailure"/> is the exception the scope's body was
        /// leaving with, or `None` if it returned normally.
        ///
        /// # Why the token is not fired first
        ///
        /// It would be simpler to cancel and then wait, always. But then this
        ///
        ///     (with-cancel (cancel)
        ///       (spawn (process a))
        ///       (spawn (process b)))
        ///
        /// could never mean "run both and wait for both", because reaching the
        /// end of the body would stop them. Waiting first is what makes that
        /// shape writable, and stopping early stays writable too — the body
        /// calls `cancel` itself when that is what it wants.
        ///
        /// The cost is real and is accepted: a scope whose children never end,
        /// and which never calls `cancel`, hangs here instead of returning. A
        /// hang says where it is. A scope that returned while its children ran
        /// on would say nothing at all.
        ///
        /// # Why a choose and not a loop
        ///
        /// The obvious way to wait for several children is to join them one at a
        /// time. That is wrong here. If child 3 fails while we are parked on
        /// child 1, we do not find out until children 1 and 2 have finished on
        /// their own — which may be never, because nothing has told them to
        /// stop. So we wait on all the remaining children at once and rebuild
        /// the wait each time one of them lands, which is what lets a failure
        /// anywhere in the group cancel the rest immediately.
        ///
        /// # Why report comes last
        ///
        /// Reporting the failure straight away would let the caller carry on
        /// while eight sockets are still tearing down. The scope has to be empty
        /// before its caller sees anything, or "the scope returned" stops
        /// meaning "the work is over".
        /// </summary>
        public async Fiber<Unit> Close(Option<System.Exception> bodyFailure) {
            // Leaving abnormally, so the children are told to stop before we
            // start waiting for them. `TrySetResult` loses if the body already
            // cancelled, which is correct: the first reason is the cause.
            if (bodyFailure.IsSome)
                Token.TrySetResult(new CancelReason.Failed(bodyFailure.Value));

            // From here on a spawn into this scope starts nothing. The scope is
            // already unwinding, so a fiber added now would either be waited for
            // in a group that has been declared complete, or be left running
            // with nobody watching.
            lock (_gate) _closed = true;

            var failures = new List<ExceptionDispatchInfo>();

            // Children that finished while the body was still running.
            Harvest(failures);

            while (true) {
                var pending = Pending();
                if (pending.Length == 0) break;

                // Deliberately not `sync`: `sync` consults the ambient token,
                // and the ambient token here is this scope's own — very possibly
                // already fired. A drain that gave up on cancellation would be a
                // drain that never drains.
                if (pending.Length == 1) {
                    await pending[0];
                } else {
                    var branches = new IEvent<Result<Unit>>[pending.Length];
                    for (int i = 0; i < pending.Length; i++) branches[i] = pending[i];
                    _ = await Cml.Choose(branches);
                }

                var before = failures.Count;
                Harvest(failures);

                // A child failed, so the rest are told to stop. Without this the
                // group would only shrink when its members happened to finish,
                // and a worker parked on a channel nobody will write to never
                // does.
                if (failures.Count > before)
                    Token.TrySetResult(new CancelReason.Failed(failures[before].SourceException));
            }

            // Everything the scope waits for has finished. The token fires now
            // for the things it does not wait for: daemons, and any child that
            // was handed the token explicitly. A no-op if it has already fired.
            Token.TrySetResult(new CancelReason.ScopesubEnded());

            // The deadline has nothing left to interrupt.
            _deadline?.Dispose();
            _deadline = null;

            if (failures.Count == 0) return default;

            // The body's own failure is the first cause, so it comes first and
            // the children's are attached to it. The body's failure is *not*
            // dropped here — the caller re-raises it when this returns without
            // throwing, which is the case where no child failed as well.
            if (bodyFailure.IsSome)
                throw Aggregate(bodyFailure.Value, failures);

            // One failure travels as itself, with the stack it was raised with.
            // Wrapping a single exception in an aggregate would make every
            // `#:catch` in the language have to unwrap before it could match.
            if (failures.Count == 1) failures[0].Throw();

            throw Aggregate(null, failures);
        }

        /// <summary>
        /// Several children failed at once, so all of them are reported. Losing
        /// the second one hides the fact that the group failed as a group,
        /// which is usually the more interesting fact.
        /// </summary>
        private static System.AggregateException Aggregate(
            System.Exception? first, List<ExceptionDispatchInfo> failures) {

            var all = new List<System.Exception>(failures.Count + 1);
            if (first is not null) all.Add(first);
            foreach (var f in failures) all.Add(f.SourceException);
            return new System.AggregateException(
                $"{all.Count} failures inside one cancellation scope", all);
        }

        /// <summary>
        /// The children that have not finished yet. A fresh array each round,
        /// because `choose` publishes what it is given and a child that has
        /// already landed would win every round from then on.
        /// </summary>
        private Promise<Unit>[] Pending() {
            lock (_gate) {
                if (_children.Count == 0) return System.Array.Empty<Promise<Unit>>();

                var pending = new List<Promise<Unit>>(_children.Count);
                foreach (var c in _children)
                    if (!c.Done.IsCompleted) pending.Add(c.Done);

                return pending.ToArray();
            }
        }

        /// <summary>
        /// Collect the failures of every child that has landed, and forget those
        /// children.
        ///
        /// Every landed child is read, not only the one that woke the drain.
        /// Several can land between two rounds, and a failure that is only
        /// noticed when its own branch happens to win the `choose` is a failure
        /// that is sometimes not noticed at all.
        /// </summary>
        private void Harvest(List<ExceptionDispatchInfo> failures) {
            lock (_gate) {
                for (int i = _children.Count - 1; i >= 0; i--) {
                    var c = _children[i];
                    if (!c.Done.IsCompleted) continue;

                    // A child that stopped because it was cancelled did not
                    // fail. This is not a nicety — without it cancellation is
                    // unusable, because the ordinary way a worker ends is that
                    // a `sync` raised `Cancelled` at it, and the scope would
                    // then report its own cancellation back to its caller as an
                    // error every single time.
                    //
                    // The record that a cancellation happened is the scope's
                    // token, which has fired and carries the reason. Nothing is
                    // lost by not raising here.
                    if (c.Reports && c.Error is { } e && !IsCancellation(e, Token))
                        failures.Add(e);

                    _children.RemoveAt(i);
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // The four ways to start a fiber
    // -----------------------------------------------------------------------
    //
    // All four are emitted by the code generator from a surface form, and the
    // only difference between them is what the scope does about the fiber
    // afterwards. They are separate methods rather than one method with a flag
    // so that the generated code says which one it meant.

    /// <summary>
    /// `(bjo (f x))` — start a fiber in the current scope and hand back its
    /// promise.
    ///
    /// The promise is a value like any other, so `MustUse` applies to it: the
    /// caller has to join it, race it, or say `ignore`. There is deliberately no
    /// exception for `bjo`. A rule with one exception is a rule people have to
    /// learn the shape of, and `spawn` below is a cheaper answer than a hole.
    ///
    /// # Who owns a failure
    ///
    /// The caller does. The scope waits for this fiber but does not report its
    /// failure, because the failure is on the promise that was just handed back
    /// and reading it is what `bjo` is for. A scope that reported it as well
    /// would raise past a program that had already handled it.
    ///
    /// `(spawn ...)` is the other half of that: it hands nothing back, so its
    /// failure is the scope's.
    ///
    /// # A scope that is already closing
    ///
    /// The fiber is not started, and nothing is raised: the scope is unwinding,
    /// and one more error on the way out is noise rather than information. But
    /// this form has to return a promise, so it returns one that has already
    /// failed with `Cancelled`. Joining it says what happened; ignoring it is
    /// the same as ignoring any other promise.
    /// </summary>
    public static Promise<T> ScopeSpawn<T>(System.Func<Fiber<T>> body) {
        var scope = Dyn.Current.Scope;
        if (scope is null) return Bjo.Spawn(body);

        var child = scope.Start(body, reports: false);
        if (child is not null) return child;

        var stillborn = new Promise<T>();
        stillborn.TrySetException(
            new Bjolang.Runtime.Cancelled(new CancelReason.ScopesubEnded()));
        return stillborn;
    }

    /// <summary>
    /// `(spawn (f x))` — the same fiber, with no handle to account for.
    ///
    /// This is the common form. Most spawned work is joined by the scope and by
    /// nothing else, and writing `(ignore (bjo ...))` for it would be ceremony
    /// on every line.
    ///
    /// # Who owns a failure
    ///
    /// The scope does, and that is the second difference from `bjo`. Nothing
    /// was handed back, so nobody else can be watching: a failure that the scope
    /// did not raise would be a fiber that died without being mentioned
    /// anywhere. So the scope cancels the other children, waits for them, and
    /// raises.
    ///
    /// With no scope — a REPL expression, or a bare thread — there is nobody to
    /// raise to, and the failure is reported instead.
    /// </summary>
    public static Unit ScopeSpawnUnit<T>(System.Func<Fiber<T>> body) {
        var scope = Dyn.Current.Scope;
        if (scope is null) { ReportUnlessCancelled(Bjo.Spawn(body), Dyn.Current.Cancel); return default; }

        // Null means the scope is closing and nothing was started. Nothing to
        // report, and nothing to hand back.
        _ = scope.Start(body, reports: true);
        return default;
    }

    /// <summary>
    /// `(spawn/daemon (f x))` — a fiber the scope does *not* wait for, but which
    /// it does cancel.
    ///
    /// For a heartbeat, a logger, a metrics pump: work that should not hold the
    /// scope open, and should not outlive it either. When the scope's children
    /// are done, the scope fires its token, and this fiber — which inherited
    /// that token like any other — stops at its next `sync`.
    ///
    /// # Why it is not in the child list
    ///
    /// Because the list is exactly "what the scope waits for", and this is not
    /// that. Cancellation reaches a daemon through the ambient token, which it
    /// inherits from the dynamic environment and not from being enlisted, so an
    /// entry here would be a record nothing ever reads.
    ///
    /// # What the scope does not do
    ///
    /// It does not wait for a daemon to *acknowledge* the cancellation. So a
    /// daemon can still be finishing its last write when the scope returns.
    /// That is the trade the form is for: a scope that waited for its daemons
    /// would be held open by exactly the fibers that are meant never to hold it
    /// open.
    ///
    /// The failure of a daemon is therefore reported rather than propagated —
    /// there is no longer anyone to propagate it to.
    /// </summary>
    public static Unit ScopeSpawnDaemon<T>(System.Func<Fiber<T>> body) {
        var scope = Dyn.Current.Scope;

        // A scope that is closing starts nothing, daemons included: it is about
        // to fire its token, so the fiber's first act would be to stop.
        if (scope is not null && scope.IsClosed) return default;

        // The scope's token, so that a daemon unwinding on the cancellation the
        // scope just fired is not printed as an unhandled exception.
        ReportUnlessCancelled(Bjo.Spawn(body), scope?.Token);
        return default;
    }

    /// <summary>
    /// `(spawn/detached (f x))` — a fiber that genuinely leaves. Nothing waits
    /// for it and nothing cancels it.
    ///
    /// The scope *and* the ambient token are cleared before the spawn, so the
    /// child inherits neither. That is what makes it a real escape hatch rather
    /// than a daemon by another name: a detached fiber outlives the scope that
    /// started it, and the whole subtree it goes on to start is outside every
    /// scope as well.
    ///
    /// This is the rare case, and the name is meant to look uncomfortable. If a
    /// detached fiber is holding a socket when the program decides to shut down,
    /// nothing in the language will close it.
    ///
    /// `Detach` rather than dropping the promise: a fiber that dies with nobody
    /// watching would otherwise never be mentioned anywhere.
    /// </summary>
    public static Unit ScopeSpawnDetached<T>(System.Func<Fiber<T>> body) {
        // Pushed and restored around the spawn, exactly as `parameterize` would:
        // `Bjo.Spawn` captures the dynamic environment as it stands *at the
        // spawn*, so clearing it here is what the child inherits, and this fiber
        // must not keep the cleared binding afterwards.
        var saved = Dyn.Current;
        Dyn.Current = saved.Detached();
        try {
            // No token: a detached fiber inherits none, so nothing can have
            // asked it to stop and every failure it has is a real one.
            ReportUnlessCancelled(Bjo.Spawn(body), null);
        } finally {
            Dyn.Current = saved;
        }
        return default;
    }

    // -----------------------------------------------------------------------
    // What `with-cancel`, `with-deadline` and `with-shield` expand to
    // -----------------------------------------------------------------------
    //
    // The three macros in `std/prelude` are the same three calls in a different
    // order, which is the point: a shield is a scope with one flag different,
    // and a deadline is a scope with a timer. Anything true of one is true of
    // all three.

    /// `(scope-open! ms)` — a scope linked to the ambient one, with a deadline
    /// of `ms` milliseconds, or no deadline when `ms` is zero.
    ///
    /// Linked, so cancelling an outer scope cancels this one and everything
    /// under it. The link is one-directional: this scope cancelling itself says
    /// nothing about its parent.
    ///
    /// This does not install the scope. `scope-push!` does, and the two are
    /// separate for the reason `parameter-push!` is separate from
    /// `make-parameter`: installing has to be undone by a `finally` that the
    /// macro writes, and a call that both allocated and installed would have no
    /// place to put that.
    public static Scope scopesubopen_BANG(int deadlineMs) =>
        new Scope(deadlineMs, Dyn.Current.Cancel);

    /// `(shield-open! ms)` — the same scope, with no link to the parent.
    ///
    /// This is `with-shield`. Once `sync` consults the ambient token, every
    /// `sync` after a cancellation fails immediately — including the ones in the
    /// cleanup that the cancellation is the reason for. A shield is the way out:
    /// inside it the ambient token is a fresh one that has not fired, so `sync`
    /// works and `(current-cancel)` agrees with it.
    ///
    /// # Why it still takes a deadline
    ///
    /// Because a shield with no deadline makes a program impossible to kill:
    /// nothing outside it can reach in, by construction. The default is thirty
    /// seconds, which is long enough for a flush and short enough to notice.
    public static Scope shieldsubopen_BANG(int deadlineMs) =>
        new Scope(deadlineMs, null);

    /// `(scope-push! sc)` — install the scope, and hand back the environment it
    /// displaced.
    ///
    /// Installing it binds two things at once: the scope, which is what a
    /// `spawn` enlists in, and `current-cancel`, which is what everything else
    /// reads. They are one field store apart and always agree, which is what
    /// keeps `(current-cancel)` and `sync` from disagreeing about whether the
    /// current fiber is cancelled.
    public static DynEnv scopesubpush_BANG(Scope scope) {
        var prev = Dyn.Current;
        Dyn.Current = prev.WithScope(scope);
        return prev;
    }

    /// `(scope-canceller sc)` — the thunk that fires this scope's token.
    ///
    /// Handed out separately from the scope for the reason `make-cancel` hands
    /// out two values: the thunk is the capability to cancel and the token is
    /// only the ability to notice. A worker can be given the token to watch its
    /// own deadline without also being given the power to stop its siblings.
    public static System.Func<CancelReason, Unit> scopesubcanceller(Scope scope) =>
        reason => { scope.Token.TrySetResult(reason); return default; };

    /// `(scope-token sc)` — the scope's token, for a child that wants to watch
    /// it explicitly rather than let `sync` do it.
    public static Promise<CancelReason> scopesubtoken(Scope scope) => scope.Token;

    /// `(scope-close! sc failure)` — end the scope. See <see cref="Scope.Close"/>
    /// for the ordering, which is the whole content of it.
    public static Fiber<Unit> scopesubclose_BANG(Scope scope, Option<System.Exception> failure) =>
        scope.Close(failure);

    // -----------------------------------------------------------------------
    // The root scope
    // -----------------------------------------------------------------------
    //
    // `main` runs inside a scope like any other body, so `(spawn ...)` at the
    // top level of `main` is legal and the process does not exit with fibers
    // still in flight. Without it, the outermost `spawn` in a program would be
    // the one spawn nothing waited for — the opposite of the rule everywhere
    // else.
    //
    // The root scope is unlinked (there is nothing above it) and has no
    // deadline (a program is allowed to take as long as it takes).

    /// The entry point for a bjoroutine `main`.
    public static async Fiber<T> RunMainFiber<T>(System.Func<Fiber<T>> body) {
        var scope = new Scope(0, null);
        var saved = scopesubpush_BANG(scope);
        try {
            T result;
            try {
                result = await body();
            } catch (System.Exception e) {
                await scope.Close(Some(e));
                throw;
            }

            await scope.Close(None<System.Exception>());
            return result;
        } finally {
            Dyn.Current = saved;
        }
    }

    /// The entry point for an ordinary `main`.
    ///
    /// A plain `defun` cannot suspend, so the body runs as it always did and
    /// only the *drain* needs a fiber. `RunToCompletion` parks the calling
    /// thread until that fiber lands, which is safe here and nowhere else: the
    /// rule it documents is never to call it from a pool thread, and the thread
    /// `Main` runs on is the one thread in the process that is certainly not one.
    public static T RunMainSync<T>(System.Func<T> body) {
        var scope = new Scope(0, null);
        var saved = scopesubpush_BANG(scope);
        try {
            T result;
            try {
                result = body();
            } catch (System.Exception e) {
                _ = Bjo.RunToCompletion(() => scope.Close(Some(e)));
                throw;
            }

            _ = Bjo.RunToCompletion(() => scope.Close(None<System.Exception>()));
            return result;
        } finally {
            Dyn.Current = saved;
        }
    }
}

namespace Bjolang.Runtime {

    /// <summary>
    /// The exception a cancelled `sync` raises.
    ///
    /// # Why an exception at all
    ///
    /// Because cancellation has to reach code that is not looking for it. A
    /// worker parked on a channel has one line of source at the `sync`, and a
    /// cancellation that arrived as a return value would need that line — and
    /// every line like it — to unwrap and decide. Unwinding is what a language
    /// already has for "stop what you are doing".
    ///
    /// # Why exactly one of these
    ///
    /// There used to be two spellings of the same fact: a `chan-recv` under
    /// `until-cancelled` gave back `None`, and a cancelled `#:async` call gave
    /// back `(Err TaskCanceledException)`. Neither was wrong on its own, but a
    /// caller had to know which one it was going to get. Everything now raises
    /// this, so `(Result Exception string)` coming out of a fetch means the
    /// request failed and nothing else.
    ///
    /// # Getting past it
    ///
    /// Every `sync` after a cancellation raises this, cleanup included. That is
    /// what `with-shield` is for: inside a shield the ambient token is a fresh
    /// one, so a `sync` works again.
    ///
    /// Naming this type in a `#:catch` is a compile error, and pointing at
    /// `with-shield` is why. A broad handler that swallows a cancellation and
    /// carries on is the classic structured-concurrency bug, and the shape of it
    /// is always the same: the handler catches, logs, and loops.
    /// </summary>
    public sealed class Cancelled : System.Exception {

        /// <summary>Why the scope was cancelled. A deadline and a shutdown want
        /// different amounts of cleanup, so the reason travels with the throw.</summary>
        public readonly global::BjolangRuntime.CancelReason Reason;

        public Cancelled(global::BjolangRuntime.CancelReason reason)
            : base($"cancelled: {reason}") => Reason = reason;
    }
}
