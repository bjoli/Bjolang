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
    ///     (:finish-let (Some url) (sync (until-cancelled (chan-recv jobs))))
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
    /// `RunMainFiber` opens a scope around `main` and installs it, and `bjo`
    /// and `spawn` hand the environment to their children, so the ambient token
    /// is a live promise in every fiber of every program. The reference
    /// comparison against `RootCancel` below therefore skips the race only for
    /// code that runs outside any scope, which in practice means the REPL's
    /// `propagate: false` session and hand-built environments.
    ///
    /// A single channel operation has one commit point, so it parks a pooled op
    /// with no `SyncState` (see `IDirectSyncable`), and the op carries the
    /// fiber's own resume: no awaiter object, no delegate hop, no handover. The
    /// token rides along as a claim on that op, through the fiber's
    /// registration below. A rendezvous allocates only the send event, 32 bytes,
    /// which a receive does not pay either.
    ///
    /// A `choose` has branches to arbitrate between, so it keeps its
    /// `SyncState`, and the token is one more branch of it: an event id
    /// reserved once the real branches are published, committed through
    /// `TryCommit` like any other.
    ///
    /// The registration is not made per park. It is one per fiber and scope,
    /// armed for each park — see `FiberWatch` — so a fiber in a loop pays for
    /// it once.
    ///
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
    public static SyncOp<T> sync<T>(IEvent<T> ev) => new SyncOp<T>(ev, AmbientRace());

    /// The token a `sync` here races, or null when there is nothing to lose
    /// to. The comparison against `RootCancel` is what makes a program that
    /// binds `(current-cancel)` to its own default free as well: that token has
    /// no other half and can never fire.
    private static Promise<CancelReason>? AmbientRace() {
        var token = Dyn.Current.Cancel;
        return ReferenceEquals(token, RootCancel) ? null : token;
    }

    /// `(chan-put ch v)` — hand `v` over, and return when it has been taken.
    ///
    /// What `(sync (chan-send ch v))` does, without the event: a send that is
    /// synced where it is written has no use for a value describing it, and
    /// that value was the one object a rendezvous allocated. Everything else is
    /// the same path — a waiting receiver commits inline, a parked send is a
    /// pooled op, and the ambient token is raced exactly as `sync` races it.
    public static ChanPut<T> chansubput<T>(Channel<T> ch, T value) =>
        new ChanPut<T>(ch, value, AmbientRace());

    /// `(chan-get ch)` — take one message. `(sync (chan-recv ch))`, which
    /// allocates nothing either, since a receive event is the channel itself.
    public static SyncOp<T> chansubget<T>(Channel<T> ch) => new SyncOp<T>(ch, AmbientRace());

    /// What `(chan-put ch v)` evaluates to. As with `SyncOp`, nothing happens
    /// until `GetAwaiter`.
    public readonly struct ChanPut<T> {
        private readonly Channel<T> _ch;
        private readonly T _value;
        private readonly Promise<CancelReason>? _token;

        internal ChanPut(Channel<T> ch, T value, Promise<CancelReason>? token) {
            _ch = ch; _value = value; _token = token;
        }

        public SyncAwaiter<Unit> GetAwaiter() {
            if (_ch.TryDirectSend(_value)) return SyncAwaiter<Unit>.Ready(default);

            var side = _ch.SendSide;
            if (_token is null)
                return new SyncAwaiter<Unit>(side, PutOp<T>.RentDirect(_value), 0, null);

            if (_token.IsCompleted)
                return SyncAwaiter<Unit>.Cancelled(_token.GetAwaiter().GetResult());

            return SyncOp<Unit>.Watched(side, PutOp<T>.RentDirect(_value), _token);
        }
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

            if (_token is null) {
                // Nothing to race: a single channel operation parks with the
                // fiber's resume in it and no claim at all.
                if (_ev is IDirectSyncable<T> unwatched)
                    return new SyncAwaiter<T>(unwatched, unwatched.RentPark(), 0, null);

                return new SyncAwaiter<T>(_ev.GetAwaiter(), null);
            }

            // A single channel operation parks one op, and one op can carry a
            // claim, so cancellation is a claim on that op rather than a second
            // published branch: no `SyncState`, no closure, no awaiter.
            if (_ev is IDirectSyncable<T> direct) return Direct(direct, _token);

            // A `choose` has branches to arbitrate between, so the token is one
            // more branch of its `SyncState` rather than a claim on one op.
            return Chosen(_ev, _token);
        }

        /// The fiber's registration on `token`, made the first time this fiber
        /// syncs in this scope.
        private static FiberWatch Cell(Promise<CancelReason> token) {
            var env = Dyn.Current;
            var cell = env.Park;

            // A scope entered since the last sync brings a different token with
            // it, and a registration belongs to one token.
            if (cell is null || !ReferenceEquals(cell.Token, token)) {
                cell = new FiberWatch(token);
                token.Register(cell);

                // Installed before the suspension, because the state-machine box
                // re-captures the environment when it suspends and this must be
                // in the copy it keeps.
                Dyn.Current = env.WithPark(cell);
            }

            return cell;
        }

        /// A `choose`, raced against the token through the fiber's
        /// registration: the token's branch is an event id reserved on the
        /// sync's own state once the real branches are published, so it commits
        /// through the same protocol they do and can only win if none has.
        ///
        /// Armed after the branches are published and before this returns.
        /// Until the fiber registers its continuation nothing can resume it,
        /// so the arm cannot land on a later park of the same fiber.
        private static SyncAwaiter<T> Chosen(IEvent<T> ev, Promise<CancelReason> token) {
            var cell = Cell(token);

            // The branches first: publish order is priority, so one that is
            // available commits here and the cell is never armed.
            var aw = EventAwaiter<T>.RentPublished(ev, out var state);
            if (state.IsSynchronized) return new SyncAwaiter<T>(aw, cell);

            int tokenBranch = state.NextEventId();
            cell.AttachChoose(aw, state, tokenBranch);
            int gen = cell.Arm();

            // The token may have fired before the arm, and its walk of its
            // waiters then found the cell idle. Arming is interlocked and this
            // read follows it, so one of the two sides always sees the other.
            if (token.IsCompleted && cell.TryTake(gen) && state.TryCommit(tokenBranch))
                ((ICancellableAwaiter)aw).OnCancelled(token.GetAwaiter().GetResult());

            return new SyncAwaiter<T>(aw, cell);
        }

        /// One channel operation, watched by this fiber's registration.
        ///
        /// The cell is armed here, before the op is handed to the channel,
        /// because nothing can resume this fiber yet. A token firing from here
        /// on finds the park through the cell and can withdraw the op whether
        /// or not it is on the channel's list (see `Withdrawal`). Arming after
        /// the op was published would race the fiber: a partner could resume
        /// it on another thread, and the arm would land on its next park.
        private static SyncAwaiter<T> Direct(IDirectSyncable<T> ev, Promise<CancelReason> token) {
            // The token may have fired while this fiber was running, in which
            // case its registration has already been walked. Nothing is rented
            // and nothing is published.
            if (token.IsCompleted)
                return SyncAwaiter<T>.Cancelled(token.GetAwaiter().GetResult());

            return Watched(ev, ev.RentPark(), token);
        }

        /// The watched park of an op already rented, which is `Direct` once
        /// it has its op, and all of `chan-put`, whose op the caller rents
        /// because it holds the value. The token has been checked once.
        internal static SyncAwaiter<T> Watched(IParkable<T> site, Operation op, Promise<CancelReason> token) {
            var cell = Cell(token);
            int opGen = op.Watch();
            cell.AttachDirect(site, op, opGen);
            int gen = cell.Arm();

            // Fired between the caller's check and the arm, as in `Chosen`. The
            // op has not been handed over, so taking the cell back is all there
            // is to cancelling. If the token's own walk takes the cell first, it
            // withdraws the op and leaves the resuming to `Park`.
            if (token.IsCompleted && cell.TryTake(gen)) {
                site.TakeParked(op);
                cell.End();
                return SyncAwaiter<T>.Cancelled(token.GetAwaiter().GetResult());
            }

            return new SyncAwaiter<T>(site, op, opGen, cell);
        }
    }

    /// One registration on one token, reused by every `sync` of one fiber.
    ///
    /// # What it replaces
    ///
    /// A watch allocated and registered on the token for every sync that
    /// parks, which is never removed — `Promise` prunes amortised. Measured on
    /// `bench/bjolang/cmlbench.bjo`, that pair was what a parked sync cost
    /// beyond the rendezvous itself. This is the same watch, registered once and
    /// armed per park, so a fiber in a loop pays for it on its first park and
    /// not again.
    ///
    /// # Where it lives, and why that is enough of an identity
    ///
    /// On the dynamic environment. The environment is inherited through nested
    /// bjoroutine calls, re-captured at every suspension, and replaced when a
    /// scope is entered — so an environment carrying one of these describes
    /// exactly "this fiber, in this scope", which is the pair a registration on
    /// a scope's token belongs to. Nothing else in the runtime has that shape:
    /// a fiber is a stack of state-machine boxes with no identity of its own.
    ///
    /// # It belongs to one fiber
    ///
    /// Nothing claims the cell before a park, so the owner writes its fields
    /// with plain stores and arms with one compare-exchange. That is only sound
    /// if no other fiber holds the same cell, and that holds by construction:
    /// `Bjo.Spawn` starts a child with `DynEnv.ForChild`, which leaves the cell
    /// out, and `blocking` hands its thread the same copy. A park that finds
    /// the cell already armed means that rule was broken somewhere, and it
    /// throws rather than overwrite another fiber's park.
    ///
    /// # The three hazards
    ///
    /// **A lost wake-up.** The token can fire between the check that it has not
    /// and the moment the park becomes visible to it. So arming is an
    /// interlocked write and the token is re-read afterwards: one of the two
    /// sides always sees the other.
    ///
    /// **A stale reference.** The token reads which op the park is and then
    /// withdraws it, and in between the op can be taken, recycled and parked
    /// again by another fiber. So each watched park gets a generation of its
    /// own on the op, and the withdrawal names it (see `Operation.ParkedGen`).
    /// The cell's generation does the same for the cell: the token's take names
    /// the park it read, and fails once that park is over.
    ///
    /// **A registration that outlives its fiber.** A fiber that ends leaves this
    /// idle, and nothing would ever drop it. So an idle cell reports itself
    /// abandoned when the token's prune asks, and marks itself unregistered as
    /// it does; the next park registers again. An armed cell answers no.
    ///
    /// # Who touches it
    ///
    /// Only the owning fiber and the token. A partner never does: it takes a
    /// single channel operation by its op, under the channel's lock, and a
    /// `choose` through its `SyncState`. That keeps the cell out of the
    /// partner's cache, and keeps interlocked instructions off the partner's
    /// side of a rendezvous.
    internal sealed class FiberWatch : PromiseWaiter {
        /// Not in a sync, or in one that has not armed. The prune may drop it.
        private const int Idle = 0;

        /// A park the token may take.
        private const int Armed = 2;

        /// Taken, by the token or by the owner seeing the token. Nobody may take
        /// it again before the owner ends the park.
        private const int Done = 3;

        /// Dropped by the token's prune. The next park registers again.
        private const int Gone = 4;

        private const int StatusBits = 3;
        private const int StatusMask = 7;

        private readonly Promise<CancelReason> _token;

        /// (generation &lt;&lt; 3) | status. The generation counts arms.
        private int _state;

        /// For a parked channel operation: where it is parked, which op it is,
        /// and the generation the op was watched as. Written before arming.
        private IParkSite? _site;
        private Operation? _op;
        private int _opGen;

        /// The parked fiber's resume. Written before the op is handed to the
        /// channel, and read by the token only after a withdrawal under the
        /// channel's lock found the op on the list, so the lock publishes it.
        private System.Action? _resume;

        /// For a parked `choose`: its awaiter, its state, and the event id the
        /// token commits under. Written before arming.
        private ICancellableAwaiter? _aw;
        private SyncState? _choice;
        private int _tokenBranch;

        /// Why the park in progress was cancelled. Here rather than on the op,
        /// because a withdrawn op stays on the channel's list until the sweep
        /// recycles it, and by then it may be somebody else's.
        private object? _reason;

        internal FiberWatch(Promise<CancelReason> token) { _token = token; }

        internal Promise<CancelReason> Token => _token;

        /// The channel operation this park is about to hand over.
        internal void AttachDirect(IParkSite site, Operation op, int opGen) {
            _site = site;
            _op = op;
            _opGen = opGen;
            _aw = null;
            _choice = null;
        }

        /// The resume the token runs if it withdraws the op from the list.
        internal void AttachResume(System.Action resume) => _resume = resume;

        /// A `choose`, whose token branch commits through its state.
        internal void AttachChoose(ICancellableAwaiter aw, SyncState state, int tokenBranch) {
            _aw = aw;
            _choice = state;
            _tokenBranch = tokenBranch;
            _site = null;
            _op = null;
        }

        /// Publish the park to the token, and answer the generation it is
        /// armed as.
        ///
        /// Interlocked because the caller re-reads the token straight after,
        /// and a store that may sink past that read is exactly the lost
        /// wake-up. It also races the prune, which may mark an idle cell
        /// unregistered at any moment.
        ///
        /// A cell found unregistered is armed first and registered after.
        /// Registering an idle one would not stick: a token that has already
        /// fired asks the cell whether it is abandoned before signalling it,
        /// and an idle cell says yes and marks itself unregistered again. An
        /// armed one says no and is signalled on the spot, which takes the park
        /// before this returns; the caller's own check then loses the take,
        /// as it should.
        internal int Arm() {
            while (true) {
                int s = System.Threading.Volatile.Read(ref _state);
                int status = s & StatusMask;
                int gen = s >> StatusBits;

                if (status != Idle && status != Gone)
                    throw new System.InvalidOperationException(
                        "A fiber's cancellation watch is already armed by another park. Two fibers are sharing one dynamic environment, which every path that starts a fiber or hands its environment to a thread is meant to prevent.");

                int armed = ((gen + 1) << StatusBits) | Armed;
                if (System.Threading.Interlocked.CompareExchange(ref _state, armed, s) != s) continue;

                // Not on the list, so no prune can reach it before this.
                if (status == Gone) _token.Register(this);
                return gen + 1;
            }
        }

        /// Take an armed park on the token's behalf: the owner's own check,
        /// made after arming, that the token had already fired. The token takes
        /// it in <see cref="Signal"/>. Whoever wins still has to withdraw the op
        /// or commit the token's branch, and can lose that to a partner.
        internal bool TryTake(int gen) {
            int armed = (gen << StatusBits) | Armed;
            return System.Threading.Interlocked.CompareExchange(
                ref _state, (gen << StatusBits) | Done, armed) == armed;
        }

        /// The park was cancelled, and its owner is the one resuming the fiber.
        internal void CancelledHere(object reason) => _reason = reason;

        /// Why the park that just ended was cancelled, or null. Read before
        /// <see cref="End"/>.
        internal object? TakeReason() {
            var r = _reason;
            _reason = null;
            return r;
        }

        /// The park is over, whichever side ended it. Only the owner calls this.
        ///
        /// Plain stores. An armed or taken cell is left alone by the prune, the
        /// token only moves it from armed to taken, and a park that never armed
        /// leaves the state as it is, since the prune may have marked it gone.
        /// The fields are cleared after the state, and the token tolerates
        /// finding them cleared: it only reaches them for a park it has taken,
        /// and a park that is ending has nothing left to cancel.
        internal void End() {
            int s = System.Threading.Volatile.Read(ref _state);
            int status = s & StatusMask;
            if (status == Armed || status == Done)
                System.Threading.Volatile.Write(ref _state, (s & ~StatusMask) | Idle);

            _resume = null;
            _site = null;
            _op = null;
            _aw = null;
            _choice = null;
        }

        /// An idle cell is droppable and says so, marking itself unregistered in
        /// the same breath so that its next park registers again. Asked under
        /// the token's list lock, and by `Wake` before it enqueues.
        public override bool IsAbandoned {
            get {
                while (true) {
                    int s = System.Threading.Volatile.Read(ref _state);
                    int status = s & StatusMask;
                    if (status == Gone) return true;
                    if (status != Idle) return false;

                    int next = (s & ~StatusMask) | Gone;
                    if (System.Threading.Interlocked.CompareExchange(ref _state, next, s) == s)
                        return true;
                }
            }
        }

        /// The token fired. Cancel the park in progress, if there is one.
        ///
        /// A fiber that is between syncs is not woken: there is nothing to wake.
        /// Its next `sync` reads the token before it parks and raises there,
        /// which is the same answer one instruction later.
        public override void Signal() {
            int s = System.Threading.Volatile.Read(ref _state);
            if ((s & StatusMask) != Armed) return;

            // Read before the take and used only if it succeeds: the state was
            // this park, armed, both before and after the reads, and the fields
            // are written before arming.
            var site = _site;
            var op = _op;
            int opGen = _opGen;
            var aw = _aw;
            var choice = _choice;
            int tokenBranch = _tokenBranch;

            if (System.Threading.Interlocked.CompareExchange(
                    ref _state, (s & ~StatusMask) | Done, s) != s) return;

            if (aw is not null && choice is not null) {
                // `TryCommit`, not `TryClaim`: the state may be transiently
                // claimed by a branch being paired, and treating that as a loss
                // would drop a cancellation that did happen. False means a
                // branch won, and the value it delivered stands.
                if (choice.TryCommit(tokenBranch))
                    aw.OnCancelled(_token.GetAwaiter().GetResult());
                return;
            }

            // Cleared by the owner ending the park as the reads were made.
            if (site is null || op is null) return;

            // Refused: a partner took the op, and the value it delivered
            // stands. Unpublished: the owner has not parked it yet, and its
            // `Park` finds it withdrawn and resumes the fiber itself.
            if (site.CancelParked(op, opGen) != Withdrawal.Published) return;

            // Stored before the resume runs, which is what publishes it to the
            // fiber. The op stays where it is; the channel's sweep drops it.
            _reason = _token.GetAwaiter().GetResult();
            Scheduler.Dispatch(_resume!);
        }
    }

    /// Forwards to whichever awaiter the sync ended up with, or to nothing at
    /// all when the rendezvous already happened.
    public readonly struct SyncAwaiter<T> : System.Runtime.CompilerServices.ICriticalNotifyCompletion {
        private readonly EventAwaiter<T>? _aw;
        private readonly FiberWatch? _cell;
        private readonly T _ready;
        private readonly CancelReason? _why;
        private readonly bool _isReady;

        /// The direct form: one channel operation, parked with the fiber's own
        /// resume in it, under the fiber's registration when there is a token.
        private readonly IParkable<T>? _direct;
        private readonly Operation? _op;

        /// The generation the op was watched as, or 0 when no token watches it.
        private readonly int _opGen;

        /// A sync through an `EventAwaiter`: an event with no token, or a
        /// `choose` under the fiber's registration, whose park has to be given
        /// back when it ends.
        internal SyncAwaiter(EventAwaiter<T> aw, FiberWatch? cell) {
            _aw = aw; _cell = cell;
            _ready = default!; _why = null; _isReady = false;
            _direct = null; _op = null; _opGen = 0;
        }

        /// A single channel operation. The op is rented, and armed under the
        /// cell when there is one, but not yet handed to the channel; that
        /// happens in <see cref="UnsafeOnCompleted"/>.
        internal SyncAwaiter(IParkable<T> direct, Operation op, int opGen, FiberWatch? cell) {
            _aw = null; _cell = cell;
            _ready = default!; _why = null; _isReady = false;
            _direct = direct; _op = op; _opGen = opGen;
        }

        private SyncAwaiter(T ready, CancelReason? why) {
            _aw = null; _cell = null;
            _ready = ready; _why = why; _isReady = true;
            _direct = null; _op = null; _opGen = 0;
        }

        internal static SyncAwaiter<T> Ready(T value) => new SyncAwaiter<T>(value, null);

        /// The token had already fired, so nothing was published at all.
        internal static SyncAwaiter<T> Cancelled(CancelReason why) => new SyncAwaiter<T>(default!, why);

        /// The direct form is never complete here: `INowable` has already
        /// answered for a partner that was waiting, and the park itself happens
        /// on suspension.
        public bool IsCompleted => _isReady || (_aw is not null && _aw.IsCompleted);

        /// The raise happens here rather than in the event continuation. That
        /// continuation runs on whichever thread completed the rendezvous, and
        /// an exception there lands in a channel's matching loop and wedges the
        /// whole sync block. `GetResult` runs on the resuming fiber's own stack,
        /// which is where a raise belongs.
        public T GetResult() {
            if (_isReady) {
                if (_why is { } already) throw new Bjolang.Runtime.Cancelled(already);
                return _ready;
            }

            if (_op is not null) {
                // A withdrawn op belongs to the channel's sweep now and is not
                // touched again; the reason is on the cell.
                if (_cell is { } cell) {
                    var cancelled = cell.TakeReason();
                    cell.End();
                    if (cancelled is CancelReason stopped) throw new Bjolang.Runtime.Cancelled(stopped);
                }
                return _direct!.TakeParked(_op);
            }

            var v = _aw!.TakeResult(out var cancelReason);

            // Back to idle before anything can throw, so that a cancelled sync
            // still leaves the cell usable by the next one.
            _cell?.End();

            if (cancelReason is CancelReason reason) throw new Bjolang.Runtime.Cancelled(reason);
            return v;
        }

        public void OnCompleted(System.Action k) => UnsafeOnCompleted(k);

        /// Never reached in the ready case: `IsCompleted` was true, and the
        /// await contract does not ask for a continuation then.
        public void UnsafeOnCompleted(System.Action k) {
            if (_op is null) {
                _aw!.UnsafeOnCompleted(k);
                return;
            }

            // Copied out first. From the moment the op is parked a partner may
            // resume this fiber on another thread, and the state machine then
            // overwrites the field this awaiter lives in with its next one.
            var cell = _cell;
            var direct = _direct!;
            var op = _op;

            if (cell is null) {
                // No token: nothing can withdraw the op.
                if (direct.Park(op, k) != ParkResult.Parked) Scheduler.Enqueue(k);
                return;
            }

            cell.AttachResume(k);

            switch (direct.Park(op, k)) {
                case ParkResult.Parked:
                    // A partner or the token resumes the fiber from here.
                    return;

                case ParkResult.Matched:
                    // A partner was waiting, and took nothing from the cell: the
                    // op's claim was cleared under the channel's lock, so a token
                    // firing now is refused.
                    Scheduler.Enqueue(k);
                    return;

                default:
                    // The token withdrew the op before it could be parked, and
                    // left the resuming to this side.
                    cell.CancelledHere(cell.Token.GetAwaiter().GetResult());
                    Scheduler.Enqueue(k);
                    return;
            }
        }
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
    /// This is what `(ignore p)` means: `std/prelude` implements `Discard` for
    /// `(Promise %a)` as `(defun (ignore p) (detach p))`, so the blanket
    /// implementation that answers `unit` is not the one a promise takes.
    /// Naming it is still how you say it about an expression that is not a
    /// promise handle.
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
    ///
    /// The dynamic environment travels with the thunk; see
    /// <see cref="InTheCallersEnvironment{T}"/>.
    public static IEvent<Result<Exception, T>> blocking<T>(Func<T> work) =>
        Cml.Guard(() =>
            Cml.Wrap(
                TaskInterop.FromTask(Task.Run(InTheCallersEnvironment(work))).Join(),
                static r =>
                    r.IsError
                        ? Result<Exception, T>.Err(r.Error!.SourceException)
                        : Result<Exception, T>.Ok(r.Value)));

    /// <summary>
    /// Wrap a thunk so that it runs against the environment of whoever is about
    /// to hand it off, rather than against <c>Dyn.Root</c>.
    ///
    /// <c>FiberContext</c> is thread-static and is not flowed, so a thunk given
    /// to <c>Task.Run</c> or to a <c>LongRunning</c> task starts on a thread
    /// with no environment at all. Without this a `parameterize` — and so a
    /// handler, and so a faked `open-input-file` — is invisible inside
    /// `(blocking ...)`.
    ///
    /// Called from inside the <see cref="Cml.Guard{T}"/>, so the environment
    /// read is the one in force at the `sync`, not the one in force when the
    /// event value was built.
    ///
    /// Save and restore rather than assign: the thread is borrowed, and pool
    /// threads are reused.
    ///
    /// The thunk gets the environment a spawned child would, without the
    /// caller's park cell: the caller goes on syncing while the thunk runs,
    /// and the cell belongs to one fiber at a time.
    /// </summary>
    private static Func<T> InTheCallersEnvironment<T>(Func<T> work) {
        var env = (DynEnv)Dyn.Current.ForChild();
        return () => {
            using var _ = Bjoml.FiberContext.Push(env);
            return work();
        };
    }

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
    ///
    /// The dynamic environment travels with the thunk; see
    /// <see cref="InTheCallersEnvironment{T}"/>.
    public static IEvent<Result<Exception, T>> spawndivthread<T>(Func<T> work) =>
        Cml.Guard(() =>
            Cml.Wrap(
                TaskInterop.FromTask(
                    Task.Factory.StartNew(
                        InTheCallersEnvironment(work),
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
                new CancelReason.Requested(BjoString.Utf8String.FromUtf16("spawn-evt: the branch lost its choose"))));

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
    /// The other direction — the token firing while the call is in flight — is
    /// why the ambient token is *not* linked into the call's own source.
    ///
    /// Linking it made the token's firing wake two branches at once. The task's
    /// source was cancelled, so the call faulted and the task branch became
    /// ready; and the ambient branch `sync` publishes became ready, from the
    /// same cause, down a different path. Two ready branches race, first commit
    /// wins, and a task branch that won handed the cancellation back as
    /// `(Err TaskCanceledException)` — the representation this method exists to
    /// be rid of. It was rare, around one sync in fifty, and a single-shot test
    /// cannot see it.
    ///
    /// So the call is given the nack's token and nothing else, and cancelling a
    /// scope now runs in a fixed order rather than a race:
    ///
    ///   1. the token fires;
    ///   2. the ambient branch is the only one that can become ready — the call
    ///      is still running, knowing nothing about any of this;
    ///   3. it commits, and `sync` raises `Cancelled`;
    ///   4. committing it withdraws this branch, which fires the nack;
    ///   5. the nack cancels the call.
    ///
    /// The call still stops, and stops because the sync was abandoned rather
    /// than alongside it. The cost is a few in-process continuations of latency
    /// on work that is already logically over at step 3; the saving is the
    /// linked source and the continuation that disposed it, per sync.
    ///
    /// Nothing is lost by dropping the link, because the two paths covered the
    /// same ground: `sync` omits the ambient branch only when the token is
    /// `RootCancel`, and `AmbientCancellation` returns `None` for that same
    /// token, so the call that had no ambient branch never had a linked source
    /// either. `sync/blocking` publishes the branch as well.
    ///
    /// **The gap:** `sync/blocking` from outside any scope has no ambient token,
    /// so nothing there can be cancelled and nothing here changes.
    ///
    /// `TestFiles/221_task_event_cancel.bjo` is the regression test, and it is a
    /// loop because one trial proves nothing about a race.
    public static IEvent<Result<Exception, T>> TaskEvent<T>(Func<CancellationToken, Task<T>> start) =>
        Cml.Guard(() => {
            var scope = Dyn.Current.Cancel;

            // Already cancelled at the moment of the sync, so there is nothing
            // to start. Read inside the `Guard`, which is what makes it the
            // *syncing* fiber's token: an event is a value and may be built in
            // one scope and synced in another.
            if (scope is not null && !ReferenceEquals(scope, RootCancel) && scope.IsCompleted)
                return Cml.Never<Result<Exception, T>>();

            // The nack's token and nothing else — see the note above on why the
            // ambient one is not linked in. `Cancellable` makes that token and
            // fires it when this branch loses, which is now the single path by
            // which the call is stopped.
            return Cml.Wrap(
                TaskInterop.Cancellable(start),
                static r =>
                    r.IsError
                        ? Result<Exception, T>.Err(r.Error!.SourceException)
                        : Result<Exception, T>.Ok(r.Value));
        });
}
