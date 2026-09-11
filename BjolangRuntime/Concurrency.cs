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
    ///
    /// # It watches the ambient token by itself
    ///
    /// The event is raced against the scope's cancellation token. If the token
    /// wins, this raises `Cancelled` rather than returning. So a worker loop is
    /// written as if cancellation did not exist:
    ///
    ///     (def url (sync (chan-recv jobs)))
    ///
    /// and not, as it had to be written before:
    ///
    ///     (:break-let (Some url) (sync (until-cancelled (chan-recv jobs))))
    ///
    /// The second spelling still exists and still means what it meant, but the
    /// token it watches is now written down — see `until-cancelled`. The
    /// ambient one is this method's business.
    ///
    /// # Cancellation loses a race; it never undoes a commit
    ///
    /// The token is offered as one more branch of a `choose`, and it is
    /// published *second*. Publish order is priority, so an event that is
    /// already available — a channel with a sender parked in it — wins even
    /// though the token has fired. That is deliberate: the sender has handed the
    /// value over, the rendezvous happened, and throwing the value away would
    /// lose a message that was successfully delivered.
    ///
    /// Once a branch has committed, `SyncState` is in its terminal state and the
    /// token cannot take it back. Cancellation competes in the commit protocol
    /// rather than reaching around it, and that is the only place the rule can
    /// live: any check outside the protocol is a check with a window after it.
    ///
    /// # What it costs
    ///
    /// A scope's token is a live promise, so a program that opens any scope at
    /// all pays for two `Wrap`s and a `Choose` on every `sync`. A program with
    /// no scope — nothing has been parameterized and no `with-cancel` is open —
    /// pays a reference comparison and takes the old path unchanged.
    /// EXPERIMENT: not an `async` method.
    ///
    /// It used to be `async Fiber<T>`, and that cost a whole extra promise and
    /// scheduler hop per rendezvous: the event completed, that woke `sync`'s own
    /// state machine, which completed `sync`'s promise, which woke the caller.
    /// Measured on a 1,000,000-message ring, 40 ns/op became 870.
    ///
    /// So `sync` builds the event and hands back something awaitable that
    /// forwards to the event's own awaiter. One suspension, one resume. The
    /// language does not notice: `sync`'s Bjolang type is written down in
    /// `Prelude.fs`, not read off this signature, so all the call site needs is
    /// that `await` compiles.
    public static SyncOp<T> sync<T>(IEvent<T> ev) {
        // Nothing to lose to. The comparison against `RootCancel` is what makes
        // a program that binds `(current-cancel)` to its own default free as
        // well: that token has no other half and can never fire.
        var token = Dyn.Current.Cancel;
        return new SyncOp<T>(ev, ReferenceEquals(token, RootCancel) ? null : token);
    }

    /// What `(sync ev)` evaluates to: the event, and the token racing it.
    ///
    /// Both fields are copied, nothing is built. `GetAwaiter` is where the
    /// synchronisation starts, which is the same moment `await ev` started it
    /// when `sync` was an ordinary async method.
    public readonly struct SyncOp<T> {
        private readonly IEvent<T> _ev;
        private readonly Promise<CancelReason>? _token;

        internal SyncOp(IEvent<T> ev, Promise<CancelReason>? token) { _ev = ev; _token = token; }

        public SyncAwaiter<T> GetAwaiter() {
            // The partner is already here, so this commits without publishing
            // anything, without a `SyncState`, and without the race. See
            // `INowable` for why skipping the race is not a change of meaning.
            if (_ev is INowable<T> now && now.TryNow(out var ready))
                return SyncAwaiter<T>.Ready(ready);

            if (_token is null) return new SyncAwaiter<T>(_ev.GetAwaiter(), null);

            var race = new CancellableEvent<T>(_ev, _token);
            return new SyncAwaiter<T>(((IEvent<T>)race).GetAwaiter(), race);
        }
    }

    /// The event, racing the ambient token, as one event rather than as a
    /// `choose` over two `wrap`s.
    ///
    /// Written out because the combinator spelling allocated seven objects per
    /// parked rendezvous — a choose, three wraps, their mapper closures and the
    /// join — and a `sync` is the most frequent thing a program does. Here the
    /// event branch hands `onSync` straight through with no mapper at all, and
    /// only the token branch needs a closure.
    ///
    /// The reason lands in <see cref="Why"/> rather than in the payload, so
    /// that the payload can stay `T` and no wrapper struct is needed. Safe
    /// because one of these is built per sync and exactly one branch commits:
    /// the write happens before `onSync`, and the awaiter's own full fence
    /// publishes it to whoever reads the result.
    internal sealed class CancellableEvent<T> : IEvent<T> {
        private readonly IEvent<T> _ev;
        private readonly Promise<CancelReason> _token;

        internal CancelReason? Why;

        internal CancellableEvent(IEvent<T> ev, Promise<CancelReason> token) {
            _ev = ev;
            _token = token;
        }

        /// The token is published *second*, and that ordering is the semantics:
        /// publish order is priority, so an event that is already available wins
        /// even though the token has fired. The rendezvous happened and throwing
        /// the value away would lose a delivered message.
        public void Publish(SyncState state, int eventId, System.Action<T> onSync) {
            _ev.Publish(state, state.NextEventId(), onSync);
            if (state.IsSynchronized) return;
            _token.Join().Publish(state, state.NextEventId(), r => { Why = r.Value; onSync(default!); });
        }
    }

    /// Forwards to whichever awaiter the sync ended up with, or to nothing at
    /// all when the rendezvous already happened.
    public readonly struct SyncAwaiter<T> : System.Runtime.CompilerServices.ICriticalNotifyCompletion {
        private readonly EventAwaiter<T>? _aw;
        private readonly CancellableEvent<T>? _race;
        private readonly T _ready;
        private readonly bool _isReady;

        internal SyncAwaiter(EventAwaiter<T> aw, CancellableEvent<T>? race) {
            _aw = aw; _race = race; _ready = default!; _isReady = false;
        }

        private SyncAwaiter(T ready) { _aw = null; _race = null; _ready = ready; _isReady = true; }

        internal static SyncAwaiter<T> Ready(T value) => new SyncAwaiter<T>(value);

        public bool IsCompleted => _isReady || _aw!.IsCompleted;

        /// The raise happens here rather than in the event continuation. That
        /// continuation runs on whichever thread completed the rendezvous, and
        /// an exception there lands in a channel's matching loop and wedges the
        /// whole sync block. `GetResult` runs on the resuming fiber's own stack,
        /// which is where a raise belongs.
        public T GetResult() {
            if (_isReady) return _ready;

            var v = _aw!.GetResult();
            if (_race?.Why is { } why) throw new Bjolang.Runtime.Cancelled(why);
            return v;
        }

        public void OnCompleted(System.Action k) => UnsafeOnCompleted(k);

        /// Never reached in the ready case: `IsCompleted` was true, and the
        /// await contract does not ask for a continuation then.
        public void UnsafeOnCompleted(System.Action k) => _aw!.UnsafeOnCompleted(k);
    }

    /// The outcome of one `sync`: either the value the event carried, or the
    /// reason the scope was cancelled.
    ///
    /// A struct with a factory per case rather than two constructors, because
    /// `T` can be instantiated at `CancelReason` and two constructors would then
    /// be the same constructor.
    internal readonly struct Synced<T> {
        private readonly T _value;
        private readonly CancelReason? _why;

        private Synced(T value, CancelReason? why) { _value = value; _why = why; }

        internal static Synced<T> Value(T v) => new(v, null);
        internal static Synced<T> Cancelled(CancelReason why) => new(default!, why);

        internal T Unwrap() =>
            _why is null ? _value : throw new Bjolang.Runtime.Cancelled(_why);
    }

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
    ///
    /// It goes into the current scope, exactly as `bjo` does. It is `bjo`'s
    /// first-class counterpart, so a fiber started through it must be owned the
    /// same way — otherwise `(map spawn-thunk thunks)` would be the one spelling
    /// of "start some work" that nothing waits for, and it would be the spelling
    /// that looks the most ordinary.
    ///
    /// **A thunk cannot be cancelled.** It has no yield point, so nothing can
    /// interrupt it and the scope simply waits for it to finish. That is the
    /// same cooperative limit `cancelled?` exists for, and it means a long thunk
    /// holds its scope open for its whole duration.
    public static Promise<T> spawnsubthunk<T>(Func<T> f) =>
        ScopeSpawn<T>(() => RunThunk(f));

    /// The state-taking shape of `Spawn` used to be reachable from here, which
    /// is why this is a separate method rather than an inline lambda: the
    /// runner must not capture, so the thunk travelled as the state argument
    /// and this unpacked it.
    ///
    /// The saving is gone now that the spawn goes through the scope, which
    /// takes a `Func&lt;Fiber&lt;T&gt;&gt;` and so costs one closure. That is
    /// the price of a thunk being owned like every other fiber, and it is one
    /// allocation against a spawn that already makes several.
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
    ///
    /// A send cannot be the channel, the way a receive can, because it has to
    /// carry the value. `ChannelSendEvent` carries both the `INowable` and the
    /// `IDirectSyncable` fast paths, so there is nothing left for this layer to
    /// add.
    public static IEvent<Unit> chansubsend<T>(Channel<T> ch, T value) =>
        new ChannelSendEvent<T>(ch, value);

    /// `(chan-recv ch)` — the event of taking one message.
    ///
    /// The channel itself. `Channel<T>` implements `IEvent<T>` with
    /// `Publish = PublishReceive` and `INowable<T>` with `TryNow =
    /// TryDirectReceive`, which is everything a receive event has to be, so
    /// wrapping it only allocated an object to forward through.
    ///
    /// Bjolang cannot tell: `(Chan a)` and `(Event a)` are different types in
    /// the language whatever the runtime representation is, and this returns
    /// the static type an event has to have.
    public static IEvent<T> chansubrecv<T>(Channel<T> ch) => ch;

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
    ///
    /// A fiber that stopped because it was cancelled is not reported. Being
    /// cancelled is how a worker normally ends, so reporting it would print an
    /// unhandled-exception line every time a scope closes cleanly — and the
    /// record that a cancellation happened is the scope's token, not this.
    public static Unit detach<T>(Promise<T> p) {
        // The ambient token, read here rather than at completion: this is the
        // scope the caller is in, and it is the one whose cancellation would
        // explain the fiber stopping.
        ReportUnlessCancelled(p, Dyn.Current.Cancel);
        return default;
    }

    // -----------------------------------------------------------------------
    // Cancellation
    // -----------------------------------------------------------------------
    //
    // A cancellation token is a `Promise<CancelReason>` and nothing more. That
    // is the whole of §6.1: a token is "a persistent event that fires once", a
    // promise is already exactly that, and so cancellation needs no scheduler
    // support, no flag to poll and no callback registry. Bjolang sees a distinct
    // type — `CancelToken` is nominal, so `promise-join` and `detach` cannot be
    // aimed at one — but nothing here has to enforce that, because the type
    // system already has.
    //
    // The payload is the *reason*. A token that carried a `Unit` said only that
    // you had been cancelled, never why — so a worker could not tell a deadline
    // from a shutdown from a sibling's failure, and every one of those wants a
    // different amount of cleanup.
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
    ///
    /// The consequence of that for the payload is worth stating: the reason a
    /// token carries is *the first one raised*, not the most specific. A scope
    /// that hits its deadline and then throws on the way out reports
    /// `(Deadline)`, because the deadline fired first and `TrySetResult` lost
    /// the second race. First-wins is the only rule a write-once cell can have,
    /// and it is the right one — the first reason is the cause and the rest are
    /// consequences.
    public static ValueTuple<Func<CancelReason, Unit>, Promise<CancelReason>> makesubcancel() {
        var token = new Promise<CancelReason>();
        return new ValueTuple<Func<CancelReason, Unit>, Promise<CancelReason>>(
            reason => { token.TrySetResult(reason); return default; },
            token);
    }

    /// `(cancelled ct)` — the event of this token having fired, carrying why.
    ///
    /// An event rather than a callback, so cancellation composes with
    /// everything else a fiber might be waiting for: `(choose (chan-recv jobs)
    /// (cancelled ct))` is a worker that is interruptible while parked, which
    /// is the only kind of interruptible that costs nothing.
    ///
    /// The outcome is unwrapped to its value because a token cannot fail: the
    /// only writer Bjolang can reach is `make-cancel`'s thunk, which calls
    /// `TrySetResult`, and `link-cancel` only ever forwards from another token.
    public static IEvent<CancelReason> cancelled(Promise<CancelReason> ct) =>
        Cml.Wrap(ct.Join(), static r => r.Value);

    /// `(until-cancelled token ev)` — `ev`, or `None` if `token` fires first.
    ///
    /// # Why it takes the token now
    ///
    /// It used to read the ambient one, and that job belongs to `sync`. Leaving
    /// it here as well would mean two mechanisms watching the same token and
    /// disagreeing about what a cancellation looks like: one raising, one
    /// returning `None`.
    ///
    /// So it keeps the *other* half of its job, which nothing else does. Any
    /// token that is not the ambient one — a per-request deadline, a sub-scope's
    /// token, one received from somewhere else — is an ordinary event and still
    /// composes under `choose`:
    ///
    ///     (match (sync (until-cancelled deadline (chan-recv jobs)))
    ///       ((Some job) (handle job))
    ///       (None       (give-up)))
    ///
    /// That is what protects the CML layer. Cancellation being automatic in
    /// `sync` must not mean that a token can only ever be watched automatically.
    ///
    /// # Do not hand it the ambient token
    ///
    /// `sync` is already watching that one, so the block would hold two waiters
    /// on one promise: this branch, and the branch `sync` adds. If the token
    /// fires while the sync is parked, `Promise.Complete` wakes both by
    /// *enqueuing* them, and two pool work items have no order relative to each
    /// other. Whichever commits first decides whether the sync answers `None` or
    /// raises `Cancelled` — both correct, and the caller cannot tell which it
    /// will get, so a `None` arm stops running some of the time.
    ///
    /// It is deterministic in the other case, which is what makes the bug easy
    /// to miss: a token that has *already* fired is committed inline during
    /// publish, and this branch is published first, so `None` wins every time
    /// in the test that was written to check it.
    ///
    /// Nothing here can detect the mistake. `sync` is handed an event and cannot
    /// see which promises are inside it. Watching a token that is not the
    /// ambient one has one waiter and no race.
    ///
    /// # Why `None` rather than a raise
    ///
    /// A fired token is persistent, so a loop that races one directly wins on it
    /// every iteration thereafter and spins a core at 100%. `None` makes leaving
    /// the natural spelling, because not recursing is what you write anyway.
    ///
    /// `ev` is published first, and that is the ordering `choose` gives meaning
    /// to: a token that has already fired must not take an iteration in which
    /// there was still a job waiting.
    public static IEvent<Option<T>> untilsubcancelled<T>(Promise<CancelReason> token, IEvent<T> ev) =>
        Cml.Choose(
            Cml.Wrap(ev, static v => Some(v)),
            Cml.Wrap(cancelled(token), static _ => None<T>()));

    /// `(cancelled? ct)` — has it fired, right now?
    ///
    /// For the compute loop with no yield point to hang a `choose` on. §6.1's
    /// cooperative limitation is exactly this: a loop that never syncs and
    /// never checks runs to completion no matter who cancelled what.
    public static bool cancelled_QMARK(Promise<CancelReason> ct) => ct.IsCompleted;

    /// `(cancel-reason ct)` — why, if it has fired at all.
    ///
    /// The poll that `cancelled?` is the boolean of. Two answers rather than
    /// one because "not cancelled" and "cancelled, and here is why" are
    /// different facts, and a loop that has just broken out on `cancelled?`
    /// still has to find out which cleanup it owes.
    ///
    /// Reading the outcome cannot throw here: a token completes only through
    /// `TrySetResult`, so the awaiter's rethrow path is unreachable, and this
    /// is guarded on `IsCompleted` anyway.
    public static Option<CancelReason> cancelsubreason(Promise<CancelReason> ct) =>
        ct.IsCompleted ? Some(ct.GetAwaiter().GetResult()) : None<CancelReason>();

    // `deadline-watch!` was here: the fiber `(with-deadline ms ...)` spawned to
    // race a timer against the scope's own token.
    //
    // It cannot work now that a scope waits for its children. The watcher waited
    // for the scope's token, and the scope's token does not fire until the
    // children are done — so the scope would wait for the watcher and the
    // watcher would wait for the scope. A deadline is a `System.Threading.Timer`
    // owned by the `Scope` object instead; see `Scope.cs`.

    /// `(link-cancel parent child)` — cancelling the parent cancels the child.
    ///
    /// One-directional on purpose: a child that cancels itself must not take
    /// its parent's other children down with it. Building a tree is repeated
    /// linking, and the leaf that fails cancels only what hangs below it.
    ///
    /// `Forward` is safe as a bare completion callback in a way user code never
    /// is — it stores a value into another promise and returns — so this is one
    /// of the few things §5.4 allows on a borrowed thread.
    ///
    /// It forwards the *value*, so a child inherits its parent's reason
    /// unaltered. Nothing here had to change for the reason payload, and that
    /// is the right answer rather than a coincidence: a linked child was
    /// cancelled because its parent was, so the parent's reason is its own.
    public static Unit linksubcancel(Promise<CancelReason> parent, Promise<CancelReason> child) {
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

    /// `(spawn/thread thunk)` — run something on a thread of its own.
    ///
    /// **This is a function, not one of the `spawn` special forms.** It takes a
    /// thunk and answers an *event*, exactly as `blocking` above does, so it is
    /// written `(sync (spawn/thread #(crunch data)))` and not
    /// `(spawn/thread (crunch data))`. The second is an ordinary application and
    /// a type error, which is the failure the name invites — the two are
    /// siblings by what they do, not by how they are spelled.
    ///
    /// # What it is for, and why `blocking` is not it
    ///
    /// `blocking` moves work that *parks a thread* to `Task.Run`. This one moves
    /// work that *uses a core* to a thread of its own. The thread pool cannot
    /// tell those apart — both are "a worker that is not coming back soon" — but
    /// the programs are different, and only one of them is helped by `Task.Run`.
    ///
    /// For compute, `Task.Run` buys nothing at all: it is the same pool, so the
    /// core is occupied either way and the only thing that changed is which pool
    /// thread is holding it. `LongRunning` is what changes the answer, because
    /// the default scheduler reads it as "do not take a pool thread" and starts a
    /// dedicated one.
    ///
    /// So this is the form for work that is long and CPU-bound: an image resize,
    /// a compression pass, a search over a big structure. A handful of such
    /// fibers on the pool is fine and is what a pool is for; the case this exists
    /// for is when there are more of them than there are cores, and fibers that
    /// care about latency are queued behind them.
    ///
    /// # A dedicated thread is not free
    ///
    /// Roughly a megabyte of stack, and no reuse — the thread is created and
    /// destroyed per call. That is the trade against a pool thread, and it is the
    /// reason this is not simply the default for everything: it is worth it for
    /// work measured in tens of milliseconds and upwards, and a loss for work
    /// measured in microseconds.
    ///
    /// # Cancellation
    ///
    /// Not cancellable, and it cannot be, for the same reason `blocking` is not:
    /// the thunk is arbitrary code with no yield point to interrupt. What *is*
    /// cancellable is the wait. `(sync (spawn/thread ...))` consults the ambient
    /// token like every other sync, so a cancelled scope stops waiting for the
    /// result — while the thread carries on to the end. The scope will not wait
    /// for it either; it waits for fibers, and this is not one.
    ///
    /// Work that must outlive its scope goes under `spawn/detached`, which is
    /// the other axis: this form decides *where* the work runs, the four spawn
    /// forms decide *who owns* it.
    ///
    /// Guarded, so each sync starts a fresh thread rather than replaying the
    /// first one's answer. Failure is a value, as everywhere else that resolves
    /// at sync time.
    public static IEvent<Result<Exception, T>> spawndivthread<T>(Func<T> work) =>
        Cml.Guard(() =>
            Cml.Wrap(
                TaskInterop.FromTask(
                    Task.Factory.StartNew(
                        work,
                        CancellationToken.None,
                        // The whole content of this method. `DenyChildAttach`
                        // beside it so that a task started *inside* the thunk
                        // cannot attach to this one and silently make the wait
                        // longer than the thunk.
                        TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default)).Join(),
                static r =>
                    r.IsError
                        ? Result<Exception, T>.Err(r.Error!.SourceException)
                        : Result<Exception, T>.Ok(r.Value)));

    /// `(sync/blocking ev)` — wait for an event from an ordinary function.
    ///
    /// `sync` is a yield point, so only a bjoroutine may write it, and a program
    /// whose `main` is a plain `defun` cannot take a value off a channel at all.
    /// This is the way in, and the mirror of `blocking` above: that one carries
    /// a thread-parking call into fiber-land, this one carries an event out.
    ///
    /// `Cml.Sync` takes a continuation rather than suspending a fiber, so no
    /// fiber is involved here at all. That is the whole reason this exists as a
    /// primitive rather than as `(await-promise (bjo (syncing ev)))` written in
    /// Bjolang: the spawning version costs a fiber per value, which on a channel
    /// in a loop is a fiber per element.
    ///
    /// The continuation runs on whatever thread completes the event and does
    /// nothing but hand the value over and wake this one — the "may wake a
    /// fiber, may not *be* the work" rule that every nack callback follows.
    ///
    /// A monitor rather than a `ManualResetEventSlim`, as in `Bjo.RunToCompletion`:
    /// the completing thread must not be able to touch a disposed handle after
    /// we wake.
    ///
    /// **Do not call this from inside a bjoroutine.** It parks the thread it is
    /// called on, and inside a fiber that thread belongs to the pool — which is
    /// what the events being waited for need in order to fire. From fiber-land
    /// the answer is `sync`, which is the whole point of there being two.
    ///
    /// Nothing withdraws this once it is parked, so a wait that might not end
    /// needs its own way out built in before it gets here:
    /// `(sync/blocking (choose (chan-recv c) (wrap (timeout 1000) ...)))`.
    ///
    /// The ambient token is offered as a branch here for the same reason it is
    /// in `sync`, and it matters more: this parks a real thread, so a wait that
    /// cancellation could not reach would be a thread the program cannot get
    /// back. `Cancelled` is raised on the calling thread, which is an ordinary
    /// throw from an ordinary function.
    public static T syncdivblocking<T>(IEvent<T> ev) {
        var token = Dyn.Current.Cancel;

        var offered =
            token is null || ReferenceEquals(token, RootCancel)
                ? Cml.Wrap(ev, static v => Synced<T>.Value(v))
                : Cml.Choose(
                    Cml.Wrap(ev, static v => Synced<T>.Value(v)),
                    Cml.Wrap(cancelled(token), static why => Synced<T>.Cancelled(why)));

        var gate = new object();
        bool landed = false;
        Synced<T> value = default!;

        Cml.Sync(offered, v => {
            lock (gate) {
                value = v;
                landed = true;
                Monitor.Pulse(gate);
            }
        });

        lock (gate) {
            while (!landed) { Monitor.Wait(gate); }
        }

        // Raised here rather than in the continuation above, which runs on the
        // thread that completed the event and must not throw.
        return value.Unwrap();
    }

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
            var token = new Promise<CancelReason>();

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

            // The reason names the mechanism rather than the loser: nothing
            // here knows what the branch was for. A child that wants to tell
            // "my choose branch lost" from "the whole scope is going down" has
            // it in the string.
            Cml.Sync(nack, _ => token.TrySetResult(
                new CancelReason.Requested("spawn-evt: the branch lost its choose")));

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
    private static readonly Promise<CancelReason> RootCancel = new();

    /// Slot 2 on the environment — a field, not a champ entry, which is why it
    /// is built here rather than by `make-parameter`.
    ///
    /// It earns the field by how it is read rather than by how often it is
    /// bound: `(:until-cancelled)` hoists a read to the head of a loop and
    /// `#:async` calls take one per call, so it is read on paths where nothing
    /// else is happening. It took the slot the error port used to hold.
    ///
    /// The root token stays the parameter's initial value rather than being
    /// seeded into the root environment, so an unparameterized program still
    /// gets *this* promise — the one <see cref="AmbientCancellation"/> tests
    /// for — out of an environment that holds null.
    public static readonly Param<Promise<CancelReason>> currentsubcancel = new(2, -1, RootCancel);

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
        Promise<CancelReason>, CancellationTokenSource> CancelBridges = new();

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
    ///
    /// **The reason does not cross.** A .NET `CancellationToken` carries no
    /// payload and never will, so a cancelled `#:async` call surfaces as a bare
    /// `TaskCanceledException`: the reason exists only on the Bjolang side of
    /// the bridge, and a caller that wants it reads `cancel-reason` off its own
    /// token rather than off the exception.
    ///
    /// Off the environment rather than through `parameter-ref`: the same three
    /// cases without its slot switch and cast back from `object`. Null there
    /// means nothing bound one, which is the root scope — so is `RootCancel`,
    /// for a program that bound `(current-cancel)` to its own default.
    public static CancellationToken AmbientCancellation() {
        var token = Dyn.Current.Cancel;

        if (token is null || ReferenceEquals(token, RootCancel)) return CancellationToken.None;
        if (token.IsCompleted) return new CancellationToken(true);

        return AmbientBridge(token).Token;
    }

    // The ambient token does not change inside a scope, so one entry catches
    // every call a loop makes and the table is probed once per scope instead of
    // once per call. It pins one token and one source per thread, which is the
    // one place the weak keying above does not hold.
    [ThreadStatic] private static Promise<CancelReason>? LastToken;
    [ThreadStatic] private static CancellationTokenSource? LastBridge;

    /// The source mirroring one token, created once.
    private static CancellationTokenSource AmbientBridge(Promise<CancelReason> token) {
        if (ReferenceEquals(token, LastToken)) return LastBridge!;

        var cts = Bridge(token);

        // Key published last, so a pair is never half written.
        LastBridge = cts;
        LastToken = token;
        return cts;
    }

    private static CancellationTokenSource Bridge(Promise<CancelReason> token) =>
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
    /// it must not raise.
    ///
    /// # Cancellation is not one of those failures
    ///
    /// It used to be: a call stopped by the ambient token came back as
    /// `(Err TaskCanceledException)`, a value nobody ever read. So an
    /// `(Result Exception string)` from a fetch meant either "the request
    /// failed" or "we were cancelled", and the caller had to tell them apart by
    /// looking at the exception type.
    ///
    /// Now a scope that has already been cancelled makes this event offer
    /// *nothing at all*. Look at what that leaves: `sync` publishes this branch
    /// and then publishes the ambient token, the token has already fired, so the
    /// token is the only branch that can commit — and `sync` raises `Cancelled`.
    /// The unification costs one test, because the answer was already there in
    /// the branch `sync` adds.
    ///
    /// Offering nothing is also the honest description. A call that must not be
    /// made is not a call that failed, and `never` is CML's word for a branch
    /// with nothing to offer this time round.
    ///
    /// The other direction — the token firing while the call is in flight —
    /// needs nothing here. The token firing is what cancels the task, so the
    /// token's branch in `sync` commits first and the task's late result is
    /// dropped by the commit protocol.
    ///
    /// **The gap:** `sync/blocking` from outside any scope has no ambient token,
    /// so nothing there can be cancelled and nothing here changes. A call made
    /// under a token that fires later behaves as described above.
    public static IEvent<Result<Exception, T>> TaskEvent<T>(Func<CancellationToken, Task<T>> start) =>
        Cml.Guard(() => {
            var scope = Dyn.Current.Cancel;

            // Already cancelled at the moment of the sync, so there is nothing
            // to start. Read inside the `Guard`, which is what makes it the
            // *syncing* fiber's token: an event is a value and may be built in
            // one scope and synced in another.
            if (scope is not null && !ReferenceEquals(scope, RootCancel) && scope.IsCompleted)
                return Cml.Never<Result<Exception, T>>();

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
