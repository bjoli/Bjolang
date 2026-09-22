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
using System.Runtime.ExceptionServices;
using Bjoml;

// A partial of its own, for the reason `Concurrency.cs` is one: `using Bjoml;`
// brings `Bjoml.Result<T>` into scope, and that is a different type from
// `BjolangRuntime.Result<TErr, TOk>`. Keeping the `using` inside one file keeps
// the two apart everywhere else. The `using` is needed here because a scope is
// built out of `Promise`, `Cml` and `Bjo`.
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
    ///
    /// # `ObjectDisposedException` is read the same way
    ///
    /// A scope does not wait for a daemon, so a daemon can still be parked in
    /// `read-line` on an owned port when the release walk disposes that port.
    /// The daemon then unwinds with `ObjectDisposedException` because the scope
    /// ended, which is the same fact `OperationCanceledException` carries, and
    /// it is read by the same test: a fired token makes it cancellation, and no
    /// token makes it a genuine use-after-close.
    /// </summary>
    private static bool IsCancellation(ExceptionDispatchInfo e, Promise<CancelReason>? token) {
        var ex = e.SourceException;
        if (ex is Bjolang.Runtime.Cancelled) return true;
        return ex is System.OperationCanceledException or System.ObjectDisposedException
            && token is { IsCompleted: true };
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
    /// <see cref="ReportUnlessCancelled{T}"/> as a landing, for the spawns that
    /// are starting the fiber and can therefore hand it an owner rather than
    /// join it afterwards.
    ///
    /// One per spawn, where joining cost a `SyncState`, a join event and a
    /// closure. `detach` cannot use this — it is handed a promise that has
    /// already been started, and may not be a fiber at all — so the joining
    /// version above stays for it.
    /// </summary>
    private sealed class UnhandledReporter : IFiberLanding {
        private readonly Promise<CancelReason>? _token;

        internal UnhandledReporter(Promise<CancelReason>? token) => _token = token;

        public void Landed(ExceptionDispatchInfo? error) {
            if (error is not null && !IsCancellation(error, _token))
                Scheduler.ReportUnhandled(error.SourceException);
        }
    }

    /// <summary>
    /// One resource a scope owns, and the handle `own!` hands back.
    ///
    /// The node is the handle: there is no separate registration record, so
    /// releasing early is an unlink rather than a lookup. Doubly linked, so a
    /// scope that owns a hundred thousand handles can drop any one of them
    /// without walking.
    ///
    /// <see cref="_state"/> is flipped with an interlocked exchange, and
    /// whoever flips it owns the release. That is the whole of the race between
    /// an early <see cref="Release"/> and the scope's closing pass: exactly one
    /// of them wins, so the thunk runs exactly once.
    /// </summary>
    public sealed class Owned {
        private const int Registered = 0;
        private const int Released = 1;

        private readonly Scope _scope;
        private readonly System.Action _release;
        private int _state = Registered;

        /// Guarded by the owning scope's `_gate`, both of them.
        internal Owned? Prev;
        internal Owned? Next;

        internal Owned(Scope scope, System.Action release) {
            _scope = scope;
            _release = release;
        }

        /// True for whoever flips the state, false for everyone after.
        internal bool Claim() =>
            System.Threading.Interlocked.Exchange(ref _state, Released) == Registered;

        internal void Run() => _release();

        /// <summary>
        /// `(release! owned)` — run the release now and take the node off the
        /// scope's list. A second call does nothing, and neither does a call
        /// that lost the race with the scope's closing pass.
        ///
        /// The thunk runs on the caller's stack and outside the lock, so its
        /// exception reaches the caller and no user code runs under `_gate`.
        /// </summary>
        public Unit Release() {
            if (!Claim()) return default;
            _scope.Unlink(this);
            _release();
            return default;
        }
    }

    /// <summary>
    /// A cancellation scope: the fibers started inside it, and the resources
    /// registered on it.
    ///
    /// Opaque to Bjolang: the only things that touch one are the four `spawn`
    /// forms, `own!`, and the `with-scope` / `with-cancel` / `with-deadline` /
    /// `with-shield` macros, which open it, install it and close it again.
    ///
    /// # Scopes that own nothing pay nothing
    ///
    /// <see cref="_head"/> is null until the first <see cref="Own"/>, and
    /// neither a spawn nor a sync reads it. A program that owns no resources
    /// runs the code it ran before this existed.
    /// </summary>
    public sealed class Scope {

        /// <summary>
        /// The scope's cancellation token. Fires at most once, and everything
        /// started under the scope inherits it as the ambient token, so firing
        /// it stops the whole subtree.
        /// </summary>
        public readonly Promise<CancelReason> Token = new();

        /// <summary>
        /// Guards <see cref="_failures"/> only, and the moments where
        /// <see cref="_outstanding"/> has to be read together with it. A spawn
        /// never takes it at all, and no user code runs while it is held.
        ///
        /// Nothing completes a promise while holding it either — not
        /// <see cref="Token"/> and not <see cref="_allDone"/>. Completing a
        /// promise can run a woken fiber inline, on this very thread, and that
        /// fiber is free to spawn into this scope. A C# lock is re-entrant, so
        /// the spawn would not deadlock; it would walk straight into the middle
        /// of a half-finished update, which is worse. Everything below therefore
        /// reads and writes in one lock section and fires whatever it has to
        /// fire after releasing it.
        /// </summary>
        private readonly object _gate = new();

        /// <summary>
        /// How many things the scope is still waiting for, and whether it has
        /// started closing, in one word: the low bits count, and the sign bit is
        /// closed. One per started child, plus one for the body itself, which
        /// <see cref="Close"/> gives up — the counter section on
        /// <see cref="Close"/> says why the body holds one.
        ///
        /// The two live together because enlisting has to test one and change
        /// the other *atomically*, or a spawn could read "still open", lose the
        /// thread, and count itself in after the scope had already decided it
        /// was finished — which is a fiber nothing waits for. Packed, that test
        /// and increment is one compare-exchange and a spawn touches no lock at
        /// all. In two fields it takes a lock, which is what it used to do.
        ///
        /// Counting down never disturbs the sign bit, because the count can
        /// only reach zero after <see cref="Close"/> has given up the body's
        /// reference, and Close sets the bit before it does. So "the scope is
        /// empty" is exactly `int.MinValue`, and there is never a borrow.
        /// </summary>
        private int _outstanding = 1;

        /// The sign bit of <see cref="_outstanding"/>. After it is set, a spawn
        /// into this scope does nothing — see <see cref="TryEnlist"/>.
        private const int ClosedBit = int.MinValue;

        /// <summary>
        /// The failures the scope has to raise, in the order the children
        /// landed. Null until the first one, because almost no scope has any.
        ///
        /// Capped at <see cref="MaxKept"/>. A million failing children must not
        /// hold a million exceptions alive, so past the cap only
        /// <see cref="_failureCount"/> moves and the report says how many were
        /// dropped.
        /// </summary>
        private List<ExceptionDispatchInfo>? _failures;

        /// How many children failed, which is not `_failures.Count` once the
        /// cap is reached.
        private int _failureCount;

        private const int MaxKept = 32;

        /// <summary>
        /// Does a child's failure belong to this scope?
        ///
        /// True everywhere except the REPL session scope and the scope around a
        /// detached fiber. Those two have nobody to raise to — the REPL has a
        /// prompt to return to, and a detached subtree is outside every scope
        /// by construction — so a failure there is reported to stderr as it
        /// lands and does not fire the token. Without that, one failed spawn at
        /// the prompt would cancel the session and every later `sync` would
        /// raise.
        ///
        /// A flag rather than a second scope type: everything else about the
        /// two is the same, and the drain has to behave identically.
        /// </summary>
        private readonly bool _propagate;

        /// The head of the owned list, newest first. Null until the first
        /// <see cref="Own"/>, and guarded by <see cref="_gate"/>.
        private Owned? _head;

        /// How many handles are on the list. For the test hook only; nothing in
        /// the runtime branches on it.
        private int _ownedCount;

        /// <summary>
        /// The release walk has taken the list. Set under <see cref="_gate"/>
        /// by <see cref="ClaimAll"/>, which is what makes it the exact point
        /// after which an <see cref="Own"/> would register on a list nobody
        /// will read.
        ///
        /// Deliberately later than the closed bit. Closing starts with the
        /// drain, and a child is still running during the drain — so
        ///
        ///     (with-scope (spawn (fetch-into "a.txt")))
        ///
        /// opens its file after the body has ended and before the releases run.
        /// That handle has somebody to release it, so it is registered. Only a
        /// fiber the scope does not wait for — a daemon, or a detached one —
        /// can still be running past the walk, and that is the case where
        /// refusing is the right answer.
        /// </summary>
        private bool _releasing;

        /// <summary>
        /// Completed by whoever takes <see cref="_outstanding"/> to zero. This
        /// is the only thing <see cref="Close"/> waits on.
        /// </summary>
        private readonly Promise<Unit> _allDone = new();

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

        internal Scope(int deadlineMs, Promise<CancelReason>? parent, bool propagate = true) {
            _propagate = propagate;
            _reporting = new Landing(this, reports: true);
            _silent = new Landing(this, reports: false);

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
        internal bool IsClosed => System.Threading.Volatile.Read(ref _outstanding) < 0;

        /// <summary>How many handles the scope is still holding. Test hook.</summary>
        internal int OwnedCount { get { lock (_gate) return _ownedCount; } }

        /// <summary>
        /// Register a release on this scope, and hand back the handle that runs
        /// it early.
        ///
        /// # A finished scope refuses this, where a closing one ignores a spawn
        ///
        /// The asymmetry is deliberate, and it is about what the two leave
        /// behind. A fiber that was never started leaks nothing, so `spawn`
        /// into a closing scope starts nothing and says nothing. A resource
        /// that was opened and then not registered is a leak with nobody left
        /// to release it, so the only safe answer is to refuse before the
        /// caller lets go of it — which is why this throws and the caller
        /// disposes what it had just opened.
        ///
        /// The two also differ in *when* they begin refusing, for the reason on
        /// <see cref="_releasing"/>: a spawn is refused as soon as closing
        /// starts, because the drain has been declared complete, and an `own!`
        /// is refused only once the releases have run, because until then there
        /// is still somebody to release it.
        /// </summary>
        internal Owned Own(System.Action release) {
            lock (_gate) {
                // Under the gate, so that the test and the link are one step
                // against the release walk, which takes the same lock to detach
                // the list.
                if (_releasing)
                    throw new Bjolang.Runtime.ScopeEnded(
                        "own! on a scope that has already released what it held: nothing would release this. The caller is a daemon or a detached fiber outliving its scope; give it a scope of its own.");

                var node = new Owned(this, release) { Next = _head };
                if (_head is not null) _head.Prev = node;
                _head = node;
                _ownedCount++;
                return node;
            }
        }

        /// <summary>
        /// Take a node off the list. Only ever called by whoever won the node's
        /// claim, so it cannot race the closing pass over the same node.
        ///
        /// A node the closing pass has already detached has null links and is
        /// not the head, so this finds nothing to do — which is the case where
        /// an early `release!` lost the race and its `Claim` told it so.
        /// </summary>
        internal void Unlink(Owned node) {
            lock (_gate) {
                if (node.Prev is not null) node.Prev.Next = node.Next;
                else if (ReferenceEquals(_head, node)) _head = node.Next;

                if (node.Next is not null) node.Next.Prev = node.Prev;

                node.Prev = null;
                node.Next = null;
                _ownedCount--;
            }
        }

        /// <summary>
        /// Take the whole list, claim every node on it, and hand back the ones
        /// this pass won as a chain in release order — newest first.
        ///
        /// Claiming happens under the lock so that it cannot interleave with an
        /// <see cref="Unlink"/>, which takes the same lock. Running happens
        /// afterwards and outside it, because a release is user code.
        ///
        /// Every node visited has its links cleared, won or not. A node an
        /// early `release!` won is then invisible to the <see cref="Unlink"/>
        /// that is still on its way in, so it cannot write into the private
        /// chain built here.
        /// </summary>
        private Owned? ClaimAll() {
            Owned? first = null;
            Owned? last = null;

            lock (_gate) {
                _releasing = true;
                var node = _head;
                _head = null;
                _ownedCount = 0;

                while (node is not null) {
                    var next = node.Next;
                    node.Prev = null;
                    node.Next = null;

                    if (node.Claim()) {
                        if (last is null) first = node;
                        else last.Next = node;
                        last = node;
                    }

                    node = next;
                }
            }

            return first;
        }

        /// <summary>
        /// Count in a fiber that is about to be started, or answer false if this
        /// scope is already closing.
        ///
        /// # Why the child is counted before the fiber is started
        ///
        /// The count says the child exists before the child does. So there is no
        /// window in which the drain can find nothing outstanding and declare
        /// the scope over while a fiber is on its way to the run queue. Counting
        /// after the spawn would leave exactly that window, and it is the window
        /// in which a scope returns with work still running — which is the one
        /// thing this whole file exists to prevent.
        ///
        /// # Why not simply hold a lock across the spawn
        ///
        /// Because starting a fiber reaches into the scheduler, and the
        /// scheduler is allowed to run work inline. Holding a lock across it
        /// would mean holding a lock across arbitrary other fibers' code.
        ///
        /// There is no lock here at all now. The closed flag is the sign bit of
        /// the count, so counting in *is* the test: increment, and read the
        /// answer off the result.
        ///
        /// Deliberately an unconditional increment and not a compare-exchange
        /// loop. Every landing decrements this same word from every pool
        /// thread at once, so a CAS that has to match a value it read a moment
        /// ago spends the storm retrying — measured as occasional 2-4x spikes
        /// on a million spawns, where an increment, which always completes,
        /// has none. The cost is that a spawn arriving after the scope closed
        /// has to put the count back, and that is the rare case.
        /// </summary>
        private bool TryEnlist() {
            // Both this and the `Or` in `Close` are atomic read-modify-writes on
            // one word, so they are totally ordered against each other: either
            // this child is counted before closing began, or it sees the bit.
            if (System.Threading.Interlocked.Increment(ref _outstanding) >= 0) return true;

            // Closed after all. Give the count back — and if that empties the
            // scope, say so, exactly as a landing would.
            if (System.Threading.Interlocked.Decrement(ref _outstanding) == ClosedBit)
                _allDone.TrySetResult(default);

            return false;
        }

        /// <summary>
        /// The two ways a child can land, as two objects per scope rather than
        /// one per child.
        ///
        /// A child is wired to the count by <see cref="IFiberLanding"/>, which is
        /// a field on the fiber. Joining the child said the same thing and cost a
        /// `SyncState`, a join event and a closure each. Which of the two an
        /// individual child gets is the only thing that varies, so the flag lives
        /// in the adapter and the adapter is shared.
        /// </summary>
        private sealed class Landing : IFiberLanding {
            private readonly Scope _scope;
            private readonly bool _reports;

            internal Landing(Scope scope, bool reports) { _scope = scope; _reports = reports; }

            public void Landed(ExceptionDispatchInfo? error) => _scope.Landed(_reports, error);
        }

        private readonly Landing _reporting;
        private readonly Landing _silent;

        /// <summary>
        /// One child has finished. Keep its failure if the failure is the
        /// scope's, then take the child off the count.
        ///
        /// This runs on whatever thread happened to complete the child, so it
        /// must not suspend, must not run user code, and should allocate as
        /// little as it can. It never suspends, it runs no user code, and the
        /// only thing it ever allocates is the failure list, on the first
        /// failure.
        ///
        /// # A child that was cancelled has not failed
        ///
        /// Without that rule cancellation is unusable, because the ordinary way
        /// a worker ends is that a `sync` raised `Cancelled` at it, and the
        /// scope would then report its own cancellation back to its caller as an
        /// error every single time. Nothing is lost by staying quiet: the record
        /// that a cancellation happened is the scope's token, which has fired
        /// and carries the reason.
        ///
        /// # Why that is decided here and not at the end
        ///
        /// Behaviour change, and a deliberate one. The test asks whether the
        /// token had fired, so its answer depends on *when* it is asked. It used
        /// to be asked after the drain, by which time the token had almost
        /// always fired, and a child that died of a genuine
        /// `OperationCanceledException` — an `HttpClient` timeout, say — before
        /// the body called `cancel` was then quietly classified as cancelled and
        /// dropped. Asking at the moment the child lands asks about the state of
        /// the world the child actually died in, so that failure is now
        /// reported.
        ///
        /// # Why an ordinary landing takes no lock
        ///
        /// Because it has nothing to say. A child that finished, or that
        /// finished by being cancelled, only has to come off the count, and the
        /// count is interlocked. The lock is for the failure list and the
        /// failure list, which has to be read and written together with the
        /// count, and a failure is the rare case.
        ///
        /// # Why the token is fired here at all
        ///
        /// Because after <see cref="Close"/> has started, this is the only place
        /// left that can. A child failing during the drain has to stop its
        /// siblings, or the drain waits for workers that nothing will ever wake.
        /// While the body is still running the token is deliberately left alone;
        /// see <see cref="Close"/>.
        ///
        /// # Why the token is fired before <see cref="_allDone"/>
        ///
        /// Completing `_allDone` can resume <see cref="Close"/> inline on this
        /// thread, and the next thing Close does is fire the token with
        /// `ScopesubEnded`. If the last child to land is the one that failed,
        /// doing these two in the other order would leave the scope's recorded
        /// reason as "the scope ended" when it was really "a child failed".
        /// </summary>
        private void Landed(bool reports, ExceptionDispatchInfo? error) {
            // Outside the lock: `IsCancellation` reads the token, and the token
            // is not ours to read while holding the gate.
            bool failed = reports && error is not null && !IsCancellation(error, Token);

            // A scope that does not propagate says it here and now, because
            // there is nobody it could say it to later: it will not store the
            // failure and will not fire its token for it. From this point the
            // child counts as an ordinary landing.
            if (failed && !_propagate) {
                Scheduler.ReportUnhandled(error!.SourceException);
                failed = false;
            }

            bool closed = false;
            bool none;
            if (failed) {
                lock (_gate) {
                    _failureCount++;
                    _failures ??= new List<ExceptionDispatchInfo>();
                    if (_failures.Count < MaxKept) _failures.Add(error!);

                    // One read answers both questions, and answers them for the
                    // state this child left behind rather than for some later
                    // one.
                    int left = System.Threading.Interlocked.Decrement(ref _outstanding);
                    closed = left < 0;
                    none = left == ClosedBit;
                }
            } else {
                none = System.Threading.Interlocked.Decrement(ref _outstanding) == ClosedBit;
            }

            // Only once closing has begun. Close reads the same list under the
            // same lock, so a failure recorded before it got there is fired by
            // Close instead, and one recorded after is fired here. Whichever of
            // the two runs second sees the other's work, so none is missed and
            // none is fired twice.
            if (failed && closed)
                Token.TrySetResult(new CancelReason.Failed(error!.SourceException));

            if (none) _allDone.TrySetResult(default);
        }

        /// <summary>
        /// Start <paramref name="body"/> as a child of this scope, or do nothing
        /// at all if the scope is already closing.
        ///
        /// Returns null in the second case, and the caller decides what that
        /// means for its own result type.
        /// </summary>
        /// <param name="reports">
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
        /// </param>
        internal Promise<T>? Start<T>(System.Func<Fiber<T>> body, bool reports) {
            if (!TryEnlist()) return null;

            // No `try` around the spawn. `Bjo.Spawn` does not call the body — it
            // allocates a fiber object and queues it — so nothing the program
            // wrote can throw here, and the count cannot be left one too high.
            var landing = reports ? _reporting : _silent;

            var env = Dyn.Current;
            if (env.Park is null) return Bjo.Spawn(body, landing);

            // The child does not inherit the parent's registration on the token:
            // two fibers cannot park on one claim, so it would fall back to a
            // watch per park for its whole life. It builds its own at its first
            // sync. Only a parent that has parked at least once pays the copy.
            Dyn.Current = env.WithPark(null);
            try {
                return Bjo.Spawn(body, landing);
            } finally {
                Dyn.Current = env;
            }
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
        /// # Why a counter and not a list of children
        ///
        /// The obvious way to wait for several children is to join them one at a
        /// time, and that is wrong here: if child 3 fails while we are parked on
        /// child 1, we do not find out until children 1 and 2 have finished on
        /// their own — which may be never, because nothing has told them to
        /// stop. So the scope used to keep one entry per child and wait on every
        /// entry still running at once, rebuilding that wait each time one of
        /// them landed.
        ///
        /// The rebuilding is what had to go. Waiting on a child is not free: it
        /// publishes a fresh waiter on that child, and a round that waits on
        /// everything still running pays one waiter per child. Children usually
        /// land one at a time, so n children meant n rounds and about n²/2
        /// waiter publications — 4,000 children measured 8,002,000 of them, and
        /// 100,000 children would be five billion.
        ///
        /// Nobody waits on the children individually now. A child that lands
        /// decrements a counter, the one that takes it to zero completes a
        /// single promise, and this waits on that promise. One wait, and O(1)
        /// per child.
        ///
        /// # Why the body holds a count of its own
        ///
        /// <see cref="_outstanding"/> starts at one, and the one is the body.
        /// Otherwise zero would mean "no child is running just now", which is
        /// not the same thing as "the scope is over":
        ///
        ///     (with-cancel (cancel)
        ///       (spawn (quick))        ; finishes at once
        ///       (sync (timeout 100))
        ///       (spawn (slow)))        ; started after quick landed
        ///
        /// `quick` lands while the body is still in the timeout, the count hits
        /// zero and the promise is completed — for good, because a promise
        /// completes once. Close would then return with `slow` still running.
        /// Holding one for the body makes zero reachable only after Close has
        /// given it up, which is the fact worth waiting for.
        ///
        /// # A child that failed while the body was still running
        ///
        /// The token is not fired for it at the time; whether it should be is a
        /// separate question, and today the answer is that a child's failure
        /// does not interrupt the body. But something has to fire it eventually,
        /// or the siblings that failure was supposed to stop are never told. The
        /// old code never did, so a body that ended normally, with one failed
        /// child and one child parked on a channel nobody writes to, hung here
        /// forever. Closing therefore reads the failure list in the same lock
        /// section that sets the closed flag, and fires the token for whatever
        /// is already in it.
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

            // From here on a spawn into this scope starts nothing, and every
            // child that lands from now on fires the token itself if it failed.
            // The scope is already unwinding, so a fiber added now would either
            // be waited for in a group that has been declared complete, or be
            // left running with nobody watching.
            ExceptionDispatchInfo? firstFailure = null;
            bool none;
            lock (_gate) {
                // Closed first, then the body's own reference is given up. In
                // that order the count cannot reach zero between the two: the
                // body still holds one while the bit is being set.
                System.Threading.Interlocked.Or(ref _outstanding, ClosedBit);
                if (_failures is { Count: > 0 }) firstFailure = _failures[0];
                none = System.Threading.Interlocked.Decrement(ref _outstanding) == ClosedBit;
            }

            // A child failed while the body was still running, so nothing has
            // told the siblings to stop yet. Without this the wait below only
            // ends when they finish of their own accord, and a worker parked on
            // a channel nobody will write to never does.
            if (firstFailure is not null)
                Token.TrySetResult(new CancelReason.Failed(firstFailure.SourceException));

            // Deliberately not `sync`: `sync` consults the ambient token, and
            // the ambient token here is this scope's own — very possibly already
            // fired. A drain that gave up on cancellation would be a drain that
            // never drains.
            if (!none) await _allDone;

            // Everything the scope waits for has finished. The token fires now
            // for the things it does not wait for: daemons, and any child that
            // was handed the token explicitly. A no-op if it has already fired.
            Token.TrySetResult(new CancelReason.ScopesubEnded());

            // The deadline has nothing left to interrupt.
            _deadline?.Dispose();
            _deadline = null;

            // Every fiber has landed and the token has fired, so nothing the
            // scope owns is still in use. Releases run newest-first, which is
            // the order the resources were opened in reversed — a port wrapped
            // around a stream is closed before the stream.
            ReleaseAll();

            List<ExceptionDispatchInfo>? failures;
            int total;
            lock (_gate) { failures = _failures; total = _failureCount; }

            if (failures is null) return default;

            // The body's own failure is the first cause, so it comes first and
            // the children's are attached to it. The body's failure is *not*
            // dropped here — the caller re-raises it when this returns without
            // throwing, which is the case where no child failed as well.
            if (bodyFailure.IsSome)
                throw Aggregate(bodyFailure.Value, failures, total);

            // One failure travels as itself, with the stack it was raised with.
            // Wrapping a single exception in an aggregate would make every
            // `#:catch` in the language have to unwrap before it could match.
            // `total` and not `failures.Count`, so that a run which hit the cap
            // is never mistaken for a single failure.
            if (total == 1) failures[0].Throw();

            throw Aggregate(null, failures, total);
        }

        /// <summary>
        /// Run every release the scope still holds, newest first.
        ///
        /// # Under a shield
        ///
        /// The scope's own token has fired by now, so anything a release calls
        /// that consults the ambient token would refuse before doing its work —
        /// which is the cleanup-after-cancellation problem `with-shield` exists
        /// for, arriving here by default. The environment is pushed with a
        /// fresh token for the walk and put back afterwards. The scope field is
        /// left alone: it is closed, so a `spawn` inside a release starts
        /// nothing and an `own!` refuses, which is what should happen.
        ///
        /// # No deadline
        ///
        /// A release is a plain thunk. It cannot suspend, so there is no point
        /// at which it could be abandoned, and interrupting one would mean
        /// running every release on a pool thread and walking away from it —
        /// a leaked thread per stuck release and a thread hop per handle. So a
        /// release that blocks blocks the close, which is the trade
        /// `with-cancel` already makes for a child that never lands: a hang
        /// says where it is. `own-bjo!` is where a deadline would live, and it
        /// is additive from here — the shield is already in place, only the
        /// wait and the timer would be new.
        ///
        /// # A release that throws
        ///
        /// Caught, recorded and the walk goes on. One failing handle must not
        /// leave the rest of them open.
        /// </summary>
        private void ReleaseAll() {
            var node = ClaimAll();
            if (node is null) return;

            var saved = Dyn.Current;
            Dyn.Current = saved.WithCancel(new Promise<CancelReason>());
            try {
                while (node is not null) {
                    var next = node.Next;
                    try {
                        node.Run();
                    } catch (System.Exception e) {
                        lock (_gate) {
                            _failureCount++;
                            _failures ??= new List<ExceptionDispatchInfo>();
                            if (_failures.Count < MaxKept)
                                _failures.Add(ExceptionDispatchInfo.Capture(e));
                        }
                    }
                    node = next;
                }
            } finally {
                Dyn.Current = saved;
            }
        }

        /// <summary>
        /// Several children failed at once, so all of them are reported. Losing
        /// the second one hides the fact that the group failed as a group,
        /// which is usually the more interesting fact.
        ///
        /// They come out in the order the children landed, which is not the
        /// order the children were started in. There is no cheaper order that
        /// means anything: a child's position in the group says nothing about
        /// when it died, and landing order at least says which failure came
        /// first.
        /// </summary>
        /// <paramref name="total"/> is how many failed, which is more than
        /// <paramref name="failures"/> holds once the cap is reached. The count
        /// is what the message reports, so a run that dropped some says so
        /// rather than quietly under-reporting.
        private static System.AggregateException Aggregate(
            System.Exception? first, List<ExceptionDispatchInfo> failures, int total) {

            var all = new List<System.Exception>(failures.Count + 1);
            if (first is not null) all.Add(first);
            foreach (var f in failures) all.Add(f.SourceException);

            int counted = total + (first is not null ? 1 : 0);
            int dropped = counted - all.Count;
            var message = dropped > 0
                ? $"{counted} failures inside one cancellation scope, and {dropped} more not kept"
                : $"{counted} failures inside one cancellation scope";

            return new System.AggregateException(message, all);
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
        var scope = RequireScope("bjo");

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
        // Null means the scope is closing and nothing was started. Nothing to
        // report, and nothing to hand back.
        _ = RequireScope("spawn").Start(body, reports: true);
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
    /// # Why it is not counted
    ///
    /// Because the count is exactly "what the scope waits for", and this is not
    /// that. Cancellation reaches a daemon through the ambient token, which it
    /// inherits from the dynamic environment and not from being enlisted, so
    /// counting it in would hold the scope open for nothing.
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
        var scope = RequireScope("spawn/daemon");

        // A scope that is closing starts nothing, daemons included: it is about
        // to fire its token, so the fiber's first act would be to stop.
        if (scope.IsClosed) return default;

        // The scope's token, so that a daemon unwinding on the cancellation the
        // scope just fired is not printed as an unhandled exception.
        //
        // Started with its own park cell, for the reason `Scope.Start` does the
        // same: a child that inherits its parent's cannot use it.
        var env = Dyn.Current;
        if (env.Park is not null) Dyn.Current = env.WithPark(null);
        try {
            _ = Bjo.Spawn(body, new UnhandledReporter(scope.Token));
        } finally {
            Dyn.Current = env;
        }

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
            _ = Bjo.Spawn(() => DetachedSubtree(body), new UnhandledReporter(null));
        } finally {
            Dyn.Current = saved;
        }
        return default;
    }

    /// <summary>
    /// A detached fiber's body, inside a scope of its own.
    ///
    /// Detaching clears the ambient scope, and `spawn` and `own!` both refuse
    /// when there is none. Without this, every spawn and every file opened
    /// anywhere under a detached fiber would raise — which is a semantic change
    /// nobody asked for, and would make the escape hatch unusable.
    ///
    /// So the subtree gets a scope: unlinked, so nothing outside can cancel it
    /// as before; no deadline, because there is no outer deadline to inherit;
    /// and `propagate: false`, because there is nobody to raise to. A child that
    /// fails is reported as it lands, which is what an unowned spawn did before
    /// there was a scope here at all.
    ///
    /// The fiber therefore waits for its own children and releases its own
    /// resources before it ends. That is new, and it is the point: detached
    /// means outside *this* program's scopes, not unstructured.
    /// </summary>
    private static async Fiber<T> DetachedSubtree<T>(System.Func<Fiber<T>> body) {
        var scope = new Scope(0, null, propagate: false);
        var saved = scopesubpush_BANG(scope);
        try {
            T answer;
            try {
                answer = await body();
            } catch (System.Exception e) {
                await scope.Close(new Option<System.Exception>(e));
                throw;
            }

            await scope.Close(default);
            return answer;
        } finally {
            _ = dynsubrestore_BANG(saved);
        }
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

    /// `(scope-cancel! sc reason)` — fire the token of a scope held as a value.
    public static Unit scopesubcancel_BANG(Scope scope, CancelReason reason) {
        scope.Token.TrySetResult(reason);
        return default;
    }

    /// `(scope-owned-count sc)` — the test hook. Not part of the surface.
    public static int scopesubownedsubcount(Scope scope) => scope.OwnedCount;

    // -----------------------------------------------------------------------
    // Owned resources
    // -----------------------------------------------------------------------

    /// <summary>
    /// The ambient scope, or a raise if there is none.
    ///
    /// The same error `own!` gives, because they are the same fact: there is
    /// nowhere to put this.
    /// </summary>
    internal static Scope RequireScope(string what) =>
        Dyn.Current.Scope ?? throw new Bjolang.Runtime.ScopeEnded(
            $"{what} outside a scope: there is nothing here to own it or wait for it. Open one with (with-scope ...), or capture one with (current-scope) and re-enter it with (in-scope ...).");

    /// `(current-scope)` — the ambient scope as a value.
    public static Scope currentsubscope() => RequireScope("(current-scope)");

    /// `(own! thunk)` — register a release on the ambient scope.
    ///
    /// The scope is read before anything else happens, so a caller that holds a
    /// freshly opened handle finds out it has nowhere to put it before it lets
    /// go of it.
    public static Owned own_BANG(System.Func<Unit> release) =>
        RequireScope("own!").Own(() => release());

    /// `(release! owned)` — run the release now. See <see cref="Owned.Release"/>.
    public static Unit release_BANG(Owned owned) => owned.Release();

    // -----------------------------------------------------------------------
    // Ports the scope owns
    // -----------------------------------------------------------------------

    /// <summary>
    /// Register a freshly opened port on the ambient scope, and hand it back.
    ///
    /// Called by the file-port constructors after they have wrapped the stream.
    /// The scope is read *before* the stream is opened — see the Bjolang side —
    /// so reaching here means there is a scope, and the only way `Own` fails is
    /// that the scope began closing in between. In that case the port is
    /// disposed here rather than leaked, and the raise reaches the caller that
    /// asked for it.
    /// </summary>
    public static Bjolang.Runtime.BjoPort OwnReader(Bjolang.Runtime.BjoPort port) {
        port.Owner = RegisterPort(port);
        return port;
    }

    public static Bjolang.Runtime.BjoWriter OwnWriter(Bjolang.Runtime.BjoWriter port) {
        port.Owner = RegisterPort(port);
        return port;
    }

    /// <summary>
    /// The same, one layer down. A byte port holds a `Stream` and so holds a
    /// handle, which is the whole of what a scope owns things for.
    ///
    /// The two that are deliberately NOT registered are a pipe's halves and a
    /// `limited` view: neither opened anything, and a scope that released them
    /// would be releasing something it did not acquire. See `BjoBytePipe`.
    /// </summary>
    public static Bjolang.Runtime.BjoByteInputPort OwnByteReader(Bjolang.Runtime.BjoByteInputPort port) {
        port.Owner = RegisterPort(port);
        return port;
    }

    /// <summary>See <see cref="OwnByteReader"/>.</summary>
    public static Bjolang.Runtime.BjoByteOutputPort OwnByteWriter(Bjolang.Runtime.BjoByteOutputPort port) {
        port.Owner = RegisterPort(port);
        return port;
    }

    /// <summary>
    /// Take ownership of whatever a handler answered `open-input-file` with.
    ///
    /// A real port is already owned, by the constructor that opened it and
    /// before the caller could lose it, so this hands it straight back. Anything
    /// else — a `StringReader` a fake filesystem built with
    /// `open-input-string` — is wrapped and registered on the scope the
    /// *perform* happened in.
    ///
    /// That is the whole point of the phase: a leak test over a fake filesystem
    /// means the same thing as over a real one, because both kinds of port are
    /// on the same list. Ports made directly with `open-input-string` are still
    /// unowned; only ports that arrive through the effect are adopted.
    /// </summary>
    public static System.IO.TextReader AdoptReader(System.IO.TextReader port) {
        if (port is Bjolang.Runtime.BjoPort { Owner: not null }) return port;
        return OwnReader(Bjolang.Runtime.BjoPort.Wrap(port));
    }

    /// <summary>See <see cref="AdoptReader"/>.</summary>
    public static System.IO.TextWriter AdoptWriter(System.IO.TextWriter port) {
        if (port is Bjolang.Runtime.BjoWriter { Owner: not null }) return port;
        return OwnWriter(Bjolang.Runtime.BjoWriter.Wrap(port));
    }

    private static Owned RegisterPort(System.IDisposable port) {
        var scope = Dyn.Current.Scope;
        if (scope is null) {
            port.Dispose();
            throw new Bjolang.Runtime.ScopeEnded(
                "a port was opened outside a scope: there is nothing here to close it.");
        }

        try {
            return scope.Own(port.Dispose);
        } catch {
            port.Dispose();
            throw;
        }
    }

    /// <summary>
    /// `close-input-port`, `close-output-port`, and the way out of a
    /// `with-open`.
    ///
    /// A port its scope owns is *released*, not disposed: a direct `.Dispose`
    /// would close the stream and leave the node on the scope's list, so a
    /// long-lived scope would accumulate one spent entry per file it had
    /// already finished with.
    ///
    /// Anything else is disposed, which is what a string port wants and what an
    /// `IDisposable` from interop wants. The three standard ports have no owner
    /// and are never disposed — closing one flushes it and stops there, because
    /// nothing in the language should be able to take stdout away from the rest
    /// of the program.
    /// </summary>
    public static Unit CloseInput(System.IO.TextReader? port) {
        if (port is null) return default;
        if (port is Bjolang.Runtime.BjoPort { Owner: { } owned }) return owned.Release();
        if (ReferenceEquals(port, StdIn) || ReferenceEquals(port, Console.In)) return default;
        port.Dispose();
        return default;
    }

    public static Unit CloseOutput(System.IO.TextWriter? port) {
        if (port is null) return default;
        if (port is Bjolang.Runtime.BjoWriter { Owner: { } owned }) return owned.Release();
        if (IsStandard(port)) { port.Flush(); return default; }
        port.Dispose();
        return default;
    }

    /// <summary>
    /// `close-byte-input-port!` and `close-byte-output-port!`, and the way out
    /// of a `with-open` over either.
    ///
    /// Through the owner handle for the same reason the text closers are: a
    /// direct `.Dispose` would release the handle and leave the node on the
    /// scope's list, so a long-lived scope would collect one spent entry per
    /// port it had already finished with. A port nothing owns — a pipe half, a
    /// `limited` view — is disposed, which for both of those is a no-op on
    /// anything shared.
    /// </summary>
    public static Unit CloseByteInput(Bjolang.Runtime.BjoByteInputPort? port) {
        if (port is null) return default;
        if (port.Owner is { } owned) return owned.Release();
        port.Dispose();
        return default;
    }

    /// <summary>See <see cref="CloseByteInput"/>.</summary>
    public static Unit CloseByteOutput(Bjolang.Runtime.BjoByteOutputPort? port) {
        if (port is null) return default;
        if (port.Owner is { } owned) return owned.Release();
        port.Dispose();
        return default;
    }

    /// `(close-owned-or-dispose x)` — the `with-open` exit, which is handed
    /// whatever the binding held: a port, or any `IDisposable` that came out of
    /// interop.
    public static Unit closesubownedsuborsubdispose<T>(T thing) => CloseOwnedOrDispose(thing);

    public static Unit CloseOwnedOrDispose(object? thing) {
        switch (thing) {
            case null: return default;
            case System.IO.TextReader r: return CloseInput(r);
            case System.IO.TextWriter w: return CloseOutput(w);
            // Before the `IDisposable` arm, and that ordering is the point: a
            // byte port IS disposable, and the general arm would dispose it
            // behind its scope's back and leave the registration standing.
            case Bjolang.Runtime.BjoByteInputPort bi: return CloseByteInput(bi);
            case Bjolang.Runtime.BjoByteOutputPort bo: return CloseByteOutput(bo);
            case System.IDisposable d: d.Dispose(); return default;
            default: return default;
        }
    }

    /// The writers a program must not be able to close. `Console.Out` and
    /// `Console.Error` as .NET hands them over, and a `BjoWriter` wrapped round
    /// either.
    private static bool IsStandard(System.IO.TextWriter w) =>
        ReferenceEquals(w, Console.Out) || ReferenceEquals(w, Console.Error);

    // -----------------------------------------------------------------------
    // `main`
    // -----------------------------------------------------------------------
    //
    // `main` runs in a scope, and it is an ordinary one: no special-cased
    // token, no separate code path, the same `scope-open!` that `with-cancel`
    // uses. So the rule is the same rule, one level up — when `main` returns,
    // the work it started is over: every fiber landed, every resource released.
    //
    // A sibling failure cancels the rest, `main` does not return until every
    // owned fiber has landed, and a top-level failure is reported through the
    // aggregated report like any other.
    //
    // # What this costs, and why it is paid
    //
    // A live ambient token puts one `CancelWatch` on every parked sync: 40
    // bytes and about 20% on the ring benchmark, measured in
    // `bench/BASELINE.md`. `main` was taken out of a scope to avoid exactly
    // that. It is back because the alternative is that a top-level `spawn` is
    // owned by nothing, a top-level `open-input-file` is released by nothing,
    // and `own!` has no answer at all outside a written-down `with-cancel`.
    //
    // The watch is the thing to make cheaper, and it is not a scope problem:
    // nothing can remove a waiter from a promise's list, so it cannot be
    // pooled. See the baseline.

    /// A Bjolang program runs in the invariant culture.
    ///
    /// Bjolang's own conversions are invariant already — `BjoNum` is nothing
    /// but that. A borrowed one is not: `import/extern` on
    /// `StringBuilder.Append(double)` or `DateTime.ToString()` reaches straight
    /// past `BjoNum` into whatever `LANG` says, and writes `2,5` or `−100` with
    /// a U+2212. Setting the culture once here covers every such call, present
    /// and future, rather than one binding at a time.
    ///
    /// Both are set: `DefaultThreadCurrentCulture` is what a thread with no
    /// culture of its own falls back to, which is every scheduler thread, and
    /// the assignment to `CurrentCulture` pins the thread `main` starts on.
    ///
    /// Formatting only. `CurrentUICulture` decides what language an exception
    /// message is in and is left alone.
    ///
    /// A program that wants the user's locale asks for it by name, the same
    /// way a C# program that wants invariant formatting has to.
    private static void RunInvariant() {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture =
            System.Globalization.CultureInfo.InvariantCulture;
    }

    /// <summary>
    /// The scope around `main`, with the standard streams registered on it.
    ///
    /// The flush is the *first* release, so under LIFO it is the last to run:
    /// every resource the program owns has written its final lines by then.
    /// Registered rather than done in a `finally` so that it is subject to the
    /// same ordering as everything else, and so that a release which throws
    /// cannot skip it.
    ///
    /// The standard streams themselves are never owned — nothing releases them,
    /// and `close-output-port` on one flushes and does nothing else.
    /// </summary>
    private static Scope OpenMainScope() {
        var scope = scopesubopen_BANG(0);
        _ = scope.Own(static () => {
            Console.Out.Flush();
            Console.Error.Flush();
        });
        return scope;
    }

    /// <summary>
    /// The REPL's session scope: opened when the session starts, installed for
    /// every entry, closed on the way out.
    ///
    /// Without one, `(def p (open-input-file "x"))` at the prompt would be
    /// closed before the next line was typed, and a `spawn` would have nowhere
    /// to enlist.
    ///
    /// `propagate: false`, and that is the whole of what makes a prompt
    /// survivable. One `(spawn (fn () (raise ...)))` would otherwise fire the
    /// session token, and every later `sync` at the prompt would raise
    /// `Cancelled` — a session poisoned by one typo. Instead the failure is
    /// printed as it lands and the session goes on, which is what a scopeless
    /// spawn did before there was a session scope.
    ///
    /// Pushed and not restored, like `currently-in-repl` beside it: the session
    /// is the process.
    /// </summary>
    public static Scope OpenReplSession() {
        var scope = new Scope(0, null, propagate: false);
        _ = scopesubpush_BANG(scope);
        return scope;
    }

    /// <summary>
    /// End the session.
    ///
    /// The token is fired *before* the drain, which is the one place this
    /// departs from what `with-cancel` does. A scope waits first so that
    /// "run both and wait for both" is writable; a prompt that has been left
    /// has nothing left to wait for, and a `(spawn (forever))` typed an hour
    /// ago must not be able to hold Ctrl-D.
    /// </summary>
    public static Unit CloseReplSession(Scope scope) {
        scope.Token.TrySetResult(new CancelReason.Requested("the REPL session ended"));
        _ = Bjo.RunToCompletion(() => scope.Close(default));
        return default;
    }

    /// The entry point for a bjoroutine `main`.
    ///
    /// The shape is the `with-scope` macro's, written out: install, run, close
    /// with whatever the body was leaving with, restore. The body's failure is
    /// rethrown after the close so that the children are cancelled and waited
    /// for first, and so that a child that also failed is reported with it.
    public static async Fiber<T> RunMainFiber<T>(System.Func<Fiber<T>> body) {
        RunInvariant();

        var scope = OpenMainScope();
        var saved = scopesubpush_BANG(scope);
        try {
            T answer;
            try {
                answer = await body();
            } catch (System.Exception e) {
                await scope.Close(new Option<System.Exception>(e));
                throw;
            }

            await scope.Close(default);
            return answer;
        } finally {
            _ = dynsubrestore_BANG(saved);
        }
    }

    /// <summary>
    /// The entry point for an ordinary `main`.
    ///
    /// A plain `defun` cannot suspend, so the body runs on the calling thread.
    /// The *close* still has to wait — `spawn` is colourless, so a `defun` main
    /// can start fibers even though it cannot join one — and waiting is a
    /// fiber's business, so the drain is driven by `RunToCompletion`.
    ///
    /// Parking this thread is allowed where parking a pool thread would not be:
    /// it is the process's entry thread, and nothing is queued behind it.
    /// </summary>
    public static T RunMainSync<T>(System.Func<T> body) {
        RunInvariant();

        var scope = OpenMainScope();
        var saved = scopesubpush_BANG(scope);
        try {
            T answer;
            try {
                answer = body();
            } catch (System.Exception e) {
                _ = Bjo.RunToCompletion(() => scope.Close(new Option<System.Exception>(e)));
                throw;
            }

            _ = Bjo.RunToCompletion(() => scope.Close(default));
            return answer;
        } finally {
            _ = dynsubrestore_BANG(saved);
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

    /// <summary>
    /// Raised by `own!`, either because there is no scope to own the thing or
    /// because the scope there is has begun closing.
    ///
    /// An ordinary exception rather than a `Cancelled`: nothing was cancelled,
    /// and a program is free to catch this and dispose what it was holding.
    /// `#:catch` may name it, unlike `Cancelled`.
    /// </summary>
    public sealed class ScopeEnded : System.Exception {
        public ScopeEnded(string message) : base(message) { }
    }
}
