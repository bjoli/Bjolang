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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bjoml;

/// <summary>
/// The door .NET code knocks on.
///
/// An inbox is a bounded queue with a CML receive event: a thread that is not a
/// fiber — Kestrel's, a timer's, a `FileSystemWatcher`'s — hands an item over
/// and returns, and a fiber with its own dynamic environment picks the item up.
/// Nothing on the posting side runs hosted-language code, which is the rule
/// <see cref="FiberContext"/>'s limitation note states: a callback from outside
/// may only wake a fiber.
///
/// <see cref="TimeoutEvent"/> is the model for the completing half. A timer
/// callback there arrives on a foreign thread and commits a published event with
/// <see cref="SyncState.TryCommit"/>; a post here does exactly that to whichever
/// receiver it finds, and the only difference is that a post also has a value to
/// carry and a queue to leave it in.
///
/// WAKING, AND WHY IT IS NOT <see cref="Scheduler.Dispatch"/> FROM OUTSIDE
///
/// A continuation resumed inline runs on the posting thread, and that thread
/// belongs to a .NET library which may well be inside a lock. The fiber would
/// then run arbitrary code there, call back into the library, want the same lock
/// and deadlock. So a post from a thread with no fiber context enqueues — one
/// pool hop, around a microsecond — while a post from inside a fiber keeps the
/// inline fast path. See <see cref="InboxWake"/>.
///
/// ONE QUEUE TYPE
///
/// A request/reply inbox is an <c>Inbox&lt;Call&lt;Q, R&gt;&gt;</c>. Capacity,
/// the full policy, closing and receiving are therefore written once and mean
/// the same thing for both uses; <see cref="Call{TQ,TR}"/> only adds what a
/// caller waiting for an answer needs.
/// </summary>
public sealed class Inbox<T>
{
    // One lock. An inbox is a boundary, not the ring buffer of a hot channel:
    // every operation on it is already paying for a cross-thread handover or a
    // commit protocol, and a lock held for a handful of list operations is not
    // what it costs. What the lock must never cover is user code, so every
    // resume, every task completion and every commit happens after it is
    // released — see the comment on Pump.
    private readonly object _gate = new();

    private readonly int _capacity;
    private readonly bool _dropOldest;

    /// <summary>
    /// The queued items. A linked list rather than a <see cref="Queue{T}"/>
    /// because a receiver that loses its <c>choose</c> has to put the item back
    /// where it took it from — see <see cref="GiveBack"/>. Dropping it at the
    /// tail would reorder the queue behind everyone's back.
    /// </summary>
    private readonly LinkedList<T> _items = new();

    /// <summary>
    /// Slots promised to senders that are committing right now.
    ///
    /// A sender has to know there is room *before* it commits, and committing
    /// cannot be done under the lock. The slot is held here in between, so two
    /// senders cannot both be told the same slot is theirs.
    /// </summary>
    private int _reserved;

    private RecvNode? _recvHead;
    private RecvNode? _recvTail;
    private SendNode? _sendHead;
    private SendNode? _sendTail;

    private bool _closed;
    private Exception? _error;
    private int _dropped;

    /// <summary>
    /// Whether some thread is already matching queued items with waiting
    /// receivers and senders — see <see cref="Settle"/>.
    /// </summary>
    private int _settleState;

    private readonly Gate<global::BjolangRuntime.Option<Exception>> _closedGate = new();
    private readonly InboxRecvEvent<T> _recvEvent;

    public Inbox(int capacity, bool dropOldest)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                "An inbox holds at least one item. There is no unbounded inbox: an event source faster than its consumer must not grow memory without limit.");

        _capacity = capacity;
        _dropOldest = dropOldest;
        _recvEvent = new InboxRecvEvent<T>(this);
    }

    /// <summary>How many items the two dropping policies have thrown away.</summary>
    public int DroppedCount => Volatile.Read(ref _dropped);

    /// <summary>`(inbox-recv ib)`. One instance; the event carries no per-sync state.</summary>
    public IEvent<T> Receive => _recvEvent;

    /// <summary>`(inbox-send-evt ib x)`.</summary>
    public IEvent<Unit> SendEvent(T item) => new InboxSendEvent<T>(this, item);

    /// <summary>`(inbox-closed ib)` — closed *and* drained, with the failure if there was one.</summary>
    public IEvent<global::BjolangRuntime.Option<Exception>> Closed => _closedGate;

    // -----------------------------------------------------------------------
    // Putting something in
    // -----------------------------------------------------------------------

    /// <summary>
    /// The half of every put that neither drops nor waits: hand the item to a
    /// waiting receiver, or queue it if there is room.
    /// </summary>
    private enum Deposit
    {
        Done,
        Full,
        Closed,
    }

    private Deposit TryDeposit(T item)
    {
        while (true)
        {
            RecvNode? r;

            lock (_gate)
            {
                if (_closed) return Deposit.Closed;

                r = TakeReceiverLocked();

                if (r is null)
                {
                    if (FullLocked) return Deposit.Full;
                    _items.AddLast(item);
                    return Deposit.Done;
                }
            }

            // Outside the lock: TryCommit fires the losing branches' nacks
            // inline, and the resume below runs the fiber itself.
            if (r.State.TryCommit(r.EventId))
            {
                InboxWake.Resume(r.OnSync, item);
                return Deposit.Done;
            }

            // That receiver's block was won elsewhere. Nothing was consumed;
            // try the next one.
        }
    }

    /// <summary>
    /// `(inbox-post! ib x)`. Safe from any thread, never waits, and when the
    /// inbox is full the policy decides.
    /// </summary>
    public bool Post(T item)
    {
        while (true)
        {
            RecvNode? r;
            T evicted = default!;
            bool didEvict = false;
            bool refused = false;

            lock (_gate)
            {
                if (_closed) return false;

                r = TakeReceiverLocked();

                if (r is null)
                {
                    if (!FullLocked)
                    {
                        _items.AddLast(item);
                        return true;
                    }

                    // Full. Both policies count what they lose, so that a
                    // program can say "dropped 37 progress updates" rather than
                    // quietly being wrong.
                    _dropped++;

                    if (_dropOldest && _items.First is { } oldest)
                    {
                        evicted = oldest.Value;
                        didEvict = true;
                        _items.RemoveFirst();
                        _items.AddLast(item);
                    }
                    else
                    {
                        // DropNewest, or DropOldest with nothing of its own to
                        // evict (every slot is promised to a committing sender).
                        refused = true;
                    }
                }
            }

            if (r is null)
            {
                // A dropped `Call` has a caller waiting on a task, and this is
                // where it is told. Outside the lock: completing a task can run
                // its continuations.
                if (didEvict) Discard(evicted, Full());
                if (refused) { Discard(item, Full()); return false; }
                return true;
            }

            if (r.State.TryCommit(r.EventId))
            {
                InboxWake.Resume(r.OnSync, item);
                return true;
            }
        }
    }

    /// <summary>
    /// `(inbox-send ib x)` and `(inbox-send/token ib x token)`. Safe from any
    /// thread; the task completes once the item is in, and stays pending while
    /// the inbox is full. That is the backpressure: a waiting producer holds
    /// exactly one item.
    /// </summary>
    public Task<Unit> Send(T item, CancellationToken token)
    {
        switch (TryDeposit(item))
        {
            case Deposit.Done:
                return Task.FromResult(Unit.Value);
            case Deposit.Closed:
                return CancelledTask();
        }

        var node = new SendNode(item);

        lock (_gate)
        {
            // Closed between the deposit attempt and here.
            if (_closed) return CancelledTask();
            ParkSenderLocked(node);
        }

        if (token.CanBeCanceled)
        {
            // Registered after parking: the other order leaves a window where
            // the token fires, finds nothing to cancel, and the item is parked
            // afterwards anyway.
            node.Registration = token.Register(static s => ((SendNode)s!).CancelByToken(), node);
        }

        // Room may have appeared while we were parking.
        Settle();

        return node.Task;
    }

    /// <summary>
    /// The published form of a send, for a fiber. It commits when there is
    /// room; losing a <c>choose</c> leaves the item unqueued, which is the whole
    /// reason a fiber gets an event here rather than the task above.
    /// </summary>
    internal void PublishSend(T item, SyncState state, int eventId, Action<Unit> onSync)
    {
        bool room;

        lock (_gate)
        {
            // A closed inbox never commits this event. A fiber that may meet one
            // chooses it together with `inbox-closed`.
            if (_closed) return;

            room = !FullLocked;
            if (room) _reserved++;
        }

        if (room)
        {
            if (state.TryCommit(eventId))
            {
                lock (_gate)
                {
                    _reserved--;
                    _items.AddLast(item);
                }

                Settle();
                InboxWake.Resume(onSync, Unit.Value);
            }
            else
            {
                // Another branch won; the item is not queued, which is exactly
                // what a task could not have promised.
                lock (_gate) { _reserved--; }
            }

            return;
        }

        var node = new SendNode(item, state, eventId, onSync);

        lock (_gate)
        {
            if (_closed) return;
            ParkSenderLocked(node);
        }

        Settle();
    }

    // -----------------------------------------------------------------------
    // Taking something out
    // -----------------------------------------------------------------------

    /// <summary>
    /// The <see cref="INowable{T}"/> fast path: an item is already here, so the
    /// receive commits without publishing anything at all.
    /// </summary>
    internal bool TryReceiveNow(out T value)
    {
        bool got;

        lock (_gate)
        {
            got = TryDequeueLiveLocked(out value);
        }

        if (got) Settle();
        return got;
    }

    internal void PublishReceive(SyncState state, int eventId, Action<T> onSync)
    {
        T value;
        bool got;

        lock (_gate)
        {
            got = TryDequeueLiveLocked(out value);

            if (!got)
            {
                // Parked. A node is withdrawn by its own SyncState going
                // terminal — a losing branch, a fired cancellation token — and
                // swept from this list the next time something walks it, the
                // way a channel reclaims the ops of a choose that lost.
                ParkReceiverLocked(new RecvNode(state, eventId, onSync));
                return;
            }
        }

        Settle();

        if (state.TryCommit(eventId))
        {
            Scheduler.Dispatch(onSync, value);
            return;
        }

        // Another branch of this sync won. Nothing may be consumed: the item
        // goes back where it was, for the next receiver.
        GiveBack(value);
        Settle();
    }

    // -----------------------------------------------------------------------
    // Settling
    // -----------------------------------------------------------------------

    /// <summary>
    /// Match what is queued with who is waiting, until nothing more can be
    /// matched, and then answer whether the inbox has drained.
    ///
    /// ONE RUNNER, NOT RECURSION. Admitting a sender fills a slot a receiver may
    /// want; waking that receiver frees a slot the next sender may want. Writing
    /// that as two functions calling each other makes the stack as deep as there
    /// are waiting producers, so it is one loop instead, and a second thread
    /// arriving mid-loop leaves a note rather than running its own: whoever is
    /// already here goes round again for it.
    ///
    /// Every commit, every resume and every task completion happens with the
    /// lock released, because all three run code this layer does not own.
    /// </summary>
    private void Settle()
    {
        if (Interlocked.CompareExchange(ref _settleState, Running, Idle) != Idle)
        {
            // Someone is already settling. Telling them there is more to do is
            // enough, and is what keeps this from being a nested call.
            Volatile.Write(ref _settleState, RunningAgain);
            return;
        }

        do
        {
            Volatile.Write(ref _settleState, Running);

            while (HandOverStep() || AdmitStep())
            {
            }

            SignalClosedIfDrained();
        }
        while (Interlocked.CompareExchange(ref _settleState, Idle, Running) != Running);
    }

    private const int Idle = 0;
    private const int Running = 1;
    private const int RunningAgain = 2;

    /// <summary>One queued item to one waiting receiver. False when there is no such pair.</summary>
    private bool HandOverStep()
    {
        RecvNode? r;
        T value;

        lock (_gate)
        {
            if (_recvHead is null || !HasLiveItemLocked()) return false;

            r = TakeReceiverLocked();
            if (r is null) return false;

            if (!TryDequeueLiveLocked(out value))
            {
                PushReceiverFrontLocked(r);
                return false;
            }
        }

        if (r.State.TryCommit(r.EventId)) InboxWake.Resume(r.OnSync, value);
        else GiveBack(value);

        return true;
    }

    /// <summary>
    /// One waiting sender into one free slot, oldest first.
    ///
    /// The slot is reserved before the sender is committed and released again if
    /// the sender turns out to have lost its <c>choose</c> or been cancelled, so
    /// a sender that cannot commit never costs the next one its place.
    /// </summary>
    private bool AdmitStep()
    {
        SendNode? s;

        lock (_gate)
        {
            if (FullLocked) return false;

            s = TakeSenderLocked();
            if (s is null) return false;

            _reserved++;
        }

        if (s.TryWin())
        {
            lock (_gate)
            {
                _reserved--;
                _items.AddLast(s.Value);
            }

            s.Complete();
        }
        else
        {
            lock (_gate) { _reserved--; }
        }

        return true;
    }

    /// <summary>
    /// Put an item back at the head, for a receiver whose sync was won
    /// elsewhere. An item that died in the meantime — a call whose caller went
    /// away while this receive was being decided — is not requeued: no fiber
    /// ever saw it, so it is cancelled rather than handed on.
    /// </summary>
    private void GiveBack(T item)
    {
        if (item is IInboxItem d && !d.Untake()) return;

        lock (_gate) { _items.AddFirst(item); }
    }

    // -----------------------------------------------------------------------
    // Ending it
    // -----------------------------------------------------------------------

    /// <summary>`(inbox-close! ib)` — a graceful end. What is queued stays.</summary>
    public void Close() => Shutdown(null, abort: false);

    /// <summary>
    /// `(inbox-fail! ib e)`. The error is stored beside the queue rather than in
    /// it, so no full policy can drop the one item that says what went wrong.
    /// </summary>
    public void Fail(Exception error) => Shutdown(error, abort: false);

    /// <summary>`(inbox-abort! ib)` — a hard stop: everything queued is dropped.</summary>
    public void Abort() => Shutdown(null, abort: true);

    private void Shutdown(Exception? error, bool abort)
    {
        List<T>? discarded = null;
        List<SendNode>? stranded = null;

        lock (_gate)
        {
            if (!_closed)
            {
                _closed = true;
                _error = error;
            }
            else if (_error is null && error is not null)
            {
                _error = error;
            }

            if (abort)
            {
                if (_items.Count > 0)
                {
                    discarded = new List<T>(_items);
                    _items.Clear();
                }

                for (var s = _sendHead; s is not null; s = s.Next) (stranded ??= new List<SendNode>()).Add(s);
                _sendHead = null;
                _sendTail = null;
            }
        }

        if (discarded is not null)
            foreach (var item in discarded)
                // Cancelled, not failed: an abort is the program stopping, and
                // a queued call's caller never had an answer to fail.
                Discard(item, null);

        if (stranded is not null)
            foreach (var s in stranded)
                s.CancelByAbort();

        Settle();
    }

    /// <summary>
    /// `inbox-closed` fires once there is nothing left to receive: the queue is
    /// empty *and* no sender that was already waiting is still owed its place.
    /// Firing earlier would end a receive loop with items still to come.
    /// </summary>
    private void SignalClosedIfDrained()
    {
        Exception? error;

        lock (_gate)
        {
            if (!_closed || _items.Count > 0 || _reserved > 0 || HasLiveSenderLocked()) return;
            error = _error;
        }

        _closedGate.Signal(
            error is null
                ? global::BjolangRuntime.None<Exception>()
                : global::BjolangRuntime.Some(error));
    }

    // -----------------------------------------------------------------------
    // The list plumbing, all of it under _gate
    // -----------------------------------------------------------------------

    private bool FullLocked => _items.Count + _reserved >= _capacity;

    private static InboxFullException Full() => new("The inbox is full.");

    private static void Discard(T item, Exception? reason)
    {
        if (item is IInboxItem d) d.Discard(reason);
    }

    private RecvNode? TakeReceiverLocked()
    {
        while (_recvHead is { } n)
        {
            _recvHead = n.Next;
            if (_recvHead is null) _recvTail = null;
            n.Next = null;

            // Withdrawn: its sync block was won by another branch, or by the
            // ambient cancellation token.
            if (!n.State.IsSynchronized) return n;
        }

        return null;
    }

    private void ParkReceiverLocked(RecvNode n)
    {
        if (_recvTail is null) _recvHead = _recvTail = n;
        else { _recvTail.Next = n; _recvTail = n; }
    }

    private void PushReceiverFrontLocked(RecvNode n)
    {
        n.Next = _recvHead;
        _recvHead = n;
        _recvTail ??= n;
    }

    private void ParkSenderLocked(SendNode n)
    {
        if (_sendTail is null) _sendHead = _sendTail = n;
        else { _sendTail.Next = n; _sendTail = n; }
    }

    private SendNode? TakeSenderLocked()
    {
        while (_sendHead is { } n)
        {
            _sendHead = n.Next;
            if (_sendHead is null) _sendTail = null;
            n.Next = null;

            if (!n.IsDead) return n;
        }

        return null;
    }

    private bool HasLiveSenderLocked()
    {
        for (var s = _sendHead; s is not null; s = s.Next)
            if (!s.IsDead)
                return true;

        return false;
    }

    /// <summary>
    /// Is there an item a receiver could have? A call abandoned while it sat in
    /// the queue is not one, and is dropped here rather than offered.
    /// </summary>
    private bool HasLiveItemLocked()
    {
        while (_items.First is { } node)
        {
            if (node.Value is IInboxItem d && d.IsDead)
            {
                _items.RemoveFirst();
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Take the oldest item a receiver may have, and mark a call as taken while
    /// the lock is held — the moment that decides the race between a receive and
    /// the caller's cancellation token.
    /// </summary>
    private bool TryDequeueLiveLocked(out T item)
    {
        while (_items.First is { } node)
        {
            var x = node.Value;
            _items.RemoveFirst();

            if (x is IInboxItem d && !d.TryTake()) continue;

            item = x;
            return true;
        }

        item = default!;
        return false;
    }

    private static Task<Unit> CancelledTask()
    {
        var tcs = new TaskCompletionSource<Unit>(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetCanceled();
        return tcs.Task;
    }

    // -----------------------------------------------------------------------
    // Nodes
    // -----------------------------------------------------------------------

    /// <summary>One parked <c>(inbox-recv ib)</c>.</summary>
    private sealed class RecvNode
    {
        public readonly SyncState State;
        public readonly int EventId;
        public readonly Action<T> OnSync;
        public RecvNode? Next;

        public RecvNode(SyncState state, int eventId, Action<T> onSync)
        {
            State = state;
            EventId = eventId;
            OnSync = onSync;
        }
    }

    /// <summary>
    /// One producer waiting for room, either half of the pair: a task sender
    /// (<c>inbox-send</c>) or a published event sender (<c>inbox-send-evt</c>).
    ///
    /// They are one list because order is one order — "waiting senders are
    /// served first-in, first-out" is a promise across both forms — and they
    /// differ only in how winning is decided and what winning does.
    /// </summary>
    private sealed class SendNode
    {
        public readonly T Value;
        public SendNode? Next;
        public CancellationTokenRegistration Registration;

        private readonly TaskCompletionSource<Unit>? _tcs;
        private readonly SyncState? _state;
        private readonly int _eventId;
        private readonly Action<Unit>? _onSync;

        /// <summary>For the task form: 0 while waiting, 1 once settled either way.</summary>
        private int _settled;

        public SendNode(T value)
        {
            Value = value;
            _tcs = new TaskCompletionSource<Unit>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public SendNode(T value, SyncState state, int eventId, Action<Unit> onSync)
        {
            Value = value;
            _state = state;
            _eventId = eventId;
            _onSync = onSync;
        }

        public Task<Unit> Task => _tcs!.Task;

        public bool IsDead =>
            _state is not null ? _state.IsSynchronized : Volatile.Read(ref _settled) != 0;

        /// <summary>Claim this park for the inbox. Exactly one claimant can.</summary>
        public bool TryWin() =>
            _state is not null
                ? _state.TryCommit(_eventId)
                : Interlocked.Exchange(ref _settled, 1) == 0;

        /// <summary>The item is in; tell whoever was waiting for it.</summary>
        public void Complete()
        {
            Registration.Dispose();

            if (_tcs is not null) _tcs.TrySetResult(Unit.Value);
            else InboxWake.Resume(_onSync!, Unit.Value);
        }

        /// <summary>
        /// The caller's token fired while we waited. The item never enters the
        /// queue, and the task is cancelled.
        /// </summary>
        public void CancelByToken()
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0) return;
            _tcs!.TrySetCanceled();
        }

        /// <summary>
        /// `inbox-abort!`. A waiting task sender is cancelled; a waiting event
        /// sender is simply never committed, so its <c>choose</c> stays open for
        /// whatever else it offered.
        /// </summary>
        public void CancelByAbort()
        {
            if (_tcs is null) return;
            if (Interlocked.Exchange(ref _settled, 1) != 0) return;

            Registration.Dispose();
            _tcs.TrySetCanceled();
        }
    }
}

/// <summary>
/// Refused because the inbox was full, under the <c>DropNewest</c> policy or as
/// the oldest item evicted under <c>DropOldest</c>.
///
/// An <see cref="InvalidOperationException"/> because that is what it is: the
/// call was made when it could not be served. An HTTP server turns it into 503.
/// </summary>
public sealed class InboxFullException : InvalidOperationException
{
    public InboxFullException(string message) : base(message) { }
}

/// <summary>
/// A queued item that has a caller behind it, and therefore something to be told
/// when it is dropped, evicted, aborted or abandoned.
///
/// Implemented by <see cref="Call{TQ,TR}"/> and by nothing else: a plain item
/// is a value, and dropping one is only a number in
/// <see cref="Inbox{T}.DroppedCount"/>.
/// </summary>
internal interface IInboxItem
{
    /// <summary>Abandoned while queued; receivers must skip it.</summary>
    bool IsDead { get; }

    /// <summary>
    /// A receiver is taking this item. Fails if the caller's token got here
    /// first, in which case the item is already cancelled and is skipped.
    /// </summary>
    bool TryTake();

    /// <summary>
    /// The receive that took this item lost its <c>choose</c> after all. True
    /// when the item is live again and may be requeued; false when the caller
    /// went away in that window, which no fiber ever saw.
    /// </summary>
    bool Untake();

    /// <summary>
    /// Dropped without ever being received: a full inbox, an eviction, or an
    /// abort. <paramref name="reason"/> is null for an abort, which cancels.
    /// </summary>
    void Discard(Exception? reason);
}

/// <summary>
/// Wake a fiber the way the waking thread allows.
///
/// The rule from <see cref="FiberContext"/>: a thread with no fiber context is
/// someone else's — it may be holding a lock, and running a continuation inline
/// there would run hosted-language code inside that lock. So it enqueues, at the
/// cost of one pool hop. A post from inside a fiber keeps the inline path, which
/// is what every rendezvous in this runtime does.
/// </summary>
internal static class InboxWake
{
    public static void Resume<TValue>(Action<TValue> k, TValue value)
    {
        if (FiberContext.Current is null) Scheduler.Enqueue(k, value);
        else Scheduler.Dispatch(k, value);
    }
}

/// <summary>
/// A one-shot, persistent event with a value: it never fires until it is
/// signalled, and from then on every <c>sync</c> on it commits immediately.
///
/// <see cref="Promise{T}"/> is the same shape and is deliberately not used:
/// it resumes with <see cref="Scheduler.Dispatch"/>, and both of the things
/// carried this way — an inbox that has drained, a call whose caller has gone —
/// can be signalled from a thread that is not a fiber.
/// </summary>
internal sealed class Gate<T> : IEvent<T>, INowable<T>
{
    private readonly object _lock = new();
    private Node? _waiters;
    private T _value = default!;
    private int _set;

    public bool IsSet => Volatile.Read(ref _set) != 0;

    public void Signal(T value)
    {
        Node? woken;

        lock (_lock)
        {
            if (_set != 0) return;

            _value = value;
            Volatile.Write(ref _set, 1);

            woken = _waiters;
            _waiters = null;
        }

        while (woken is not null)
        {
            var next = woken.Next;
            if (woken.State.TryCommit(woken.EventId)) InboxWake.Resume(woken.OnSync, value);
            woken = next;
        }
    }

    public void Publish(SyncState state, int eventId, Action<T> onSync)
    {
        T value;

        lock (_lock)
        {
            if (_set == 0)
            {
                _waiters = new Node(state, eventId, onSync, _waiters);
                return;
            }

            value = _value;
        }

        if (state.TryCommit(eventId)) Scheduler.Dispatch(onSync, value);
    }

    bool INowable<T>.TryNow(out T value)
    {
        if (IsSet)
        {
            value = _value;
            return true;
        }

        value = default!;
        return false;
    }

    private sealed class Node
    {
        public readonly SyncState State;
        public readonly int EventId;
        public readonly Action<T> OnSync;
        public readonly Node? Next;

        public Node(SyncState state, int eventId, Action<T> onSync, Node? next)
        {
            State = state;
            EventId = eventId;
            OnSync = onSync;
            Next = next;
        }
    }
}

internal sealed class InboxRecvEvent<T> : IEvent<T>, INowable<T>
{
    private readonly Inbox<T> _inbox;

    public InboxRecvEvent(Inbox<T> inbox) => _inbox = inbox;

    public void Publish(SyncState state, int eventId, Action<T> onSync) =>
        _inbox.PublishReceive(state, eventId, onSync);

    bool INowable<T>.TryNow(out T value) => _inbox.TryReceiveNow(out value);
}

internal sealed class InboxSendEvent<T> : IEvent<Unit>
{
    private readonly Inbox<T> _inbox;
    private readonly T _item;

    public InboxSendEvent(Inbox<T> inbox, T item)
    {
        _inbox = inbox;
        _item = item;
    }

    public void Publish(SyncState state, int eventId, Action<Unit> onSync) =>
        _inbox.PublishSend(_item, state, eventId, onSync);
}

/// <summary>
/// One request, and the answer its caller is waiting for.
///
/// THE RACE THIS TYPE EXISTS TO SETTLE
///
/// A caller's cancellation token — Kestrel's <c>RequestAborted</c> — can fire at
/// the same instant a fiber receives the call, and the two outcomes are
/// different:
///
/// <list type="bullet">
/// <item>Still queued: the call is removed and the task is cancelled. No fiber
/// ever sees a call nobody is waiting for.</item>
/// <item>Already taken: the task is left pending, and
/// <see cref="Abandoned"/> fires instead. Only the receiver may complete it —
/// because completing it hands the .NET caller its object back, and in an HTTP
/// server that object is recycled into the next request while the handler is
/// still writing to it.</item>
/// </list>
///
/// One word decides, with a compare-and-swap on each side, so it comes out as
/// exactly one of the two and never both.
/// </summary>
public sealed class Call<TQ, TR> : IInboxItem
{
    private const int Queued = 0;
    private const int Taken = 1;
    private const int Dead = 2;
    private const int AbandonedTaken = 3;

    private readonly TaskCompletionSource<TR> _tcs;
    private readonly Gate<Unit> _abandoned = new();
    private CancellationTokenRegistration _registration;
    private int _phase;

    internal Call(TQ request)
    {
        Request = request;

        // RunContinuationsAsynchronously, or the caller's continuation runs on
        // the replying fiber's thread: .NET code re-entered there, on a deeper
        // stack, in a fiber that has moved on to its next call.
        _tcs = new TaskCompletionSource<TR>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>What the caller asked.</summary>
    public TQ Request { get; }

    /// <summary>The task the .NET caller waits on.</summary>
    public Task<TR> Task => _tcs.Task;

    /// <summary>
    /// Fires once the caller's token has fired *after* a receiver took this
    /// call. It never fires for a call made without a token, and never for one
    /// cancelled while it was still queued — nobody had it to be told.
    /// </summary>
    public IEvent<Unit> Abandoned => _abandoned;

    internal void Watch(CancellationToken token)
    {
        if (!token.CanBeCanceled) return;
        _registration = token.Register(static s => ((Call<TQ, TR>)s!).OnToken(), this);
    }

    /// <summary>
    /// `(call-reply! c v)`. The first completion wins and answers true; a later
    /// one answers false and does nothing, so a timeout and a handler can race
    /// to answer safely.
    /// </summary>
    public bool Reply(TR value)
    {
        var won = _tcs.TrySetResult(value);
        if (won) _registration.Dispose();
        return won;
    }

    /// <summary>`(call-fail! c e)`. The same, with an exception for the caller.</summary>
    public bool Fail(Exception error)
    {
        var won = _tcs.TrySetException(error);
        if (won) _registration.Dispose();
        return won;
    }

    private void OnToken()
    {
        if (Interlocked.CompareExchange(ref _phase, Dead, Queued) == Queued)
        {
            // Still in the queue. Cancel it; receivers skip it from here on.
            _tcs.TrySetCanceled();
            return;
        }

        if (Interlocked.CompareExchange(ref _phase, AbandonedTaken, Taken) == Taken)
            _abandoned.Signal(Unit.Value);
    }

    bool IInboxItem.IsDead => Volatile.Read(ref _phase) == Dead;

    bool IInboxItem.TryTake() => Interlocked.CompareExchange(ref _phase, Taken, Queued) == Queued;

    bool IInboxItem.Untake()
    {
        if (Interlocked.CompareExchange(ref _phase, Queued, Taken) == Taken) return true;

        // The token fired in the window between this receive taking the call
        // and losing its choose. It was reported as abandoned to a receiver
        // that turned out not to exist, so from the caller's side this is the
        // queued case after all: cancel it, and do not put it back.
        _tcs.TrySetCanceled();
        return false;
    }

    void IInboxItem.Discard(Exception? reason)
    {
        if (Interlocked.CompareExchange(ref _phase, Dead, Queued) != Queued) return;

        _registration.Dispose();

        if (reason is null) _tcs.TrySetCanceled();
        else _tcs.TrySetException(reason);
    }
}

/// <summary>
/// <see cref="IObserver{T}"/> made of three delegates, so that a hosted language
/// can subscribe to an <see cref="IObservable{T}"/> at all: subscribing needs an
/// object implementing the interface, which Bjolang cannot write, and base .NET
/// has no <c>Subscribe(Action&lt;T&gt;)</c>.
///
/// The three run on the observable's thread, so they follow the rule every
/// foreign callback does: post, close or fail, and nothing else.
/// </summary>
public sealed class DelegateObserver<T> : IObserver<T>
{
    // `Func<..., Unit>` rather than `Action<...>`, for the reason
    // `BjolangRuntime.RrbForEach` gives: a Bjolang `(-> %a void)` is
    // `(-> %a Unit)`, and only one delegate shape can also be an instantiation
    // of a generic arrow.
    private readonly Func<T, Unit> _onNext;
    private readonly Func<Exception, Unit> _onError;
    private readonly Func<Unit> _onCompleted;

    public DelegateObserver(Func<T, Unit> onNext, Func<Exception, Unit> onError, Func<Unit> onCompleted)
    {
        _onNext = onNext;
        _onError = onError;
        _onCompleted = onCompleted;
    }

    public void OnNext(T value) => _onNext(value);

    public void OnError(Exception error) => _onError(error);

    public void OnCompleted() => _onCompleted();
}

/// <summary>
/// The static surface `(std inbox)` imports, in the shape
/// <c>import/extern</c> reads: one function per operation, the inbox first.
/// </summary>
public static class InboxModule
{
    public static Inbox<T> Make<T>(int capacity, bool dropOldest) => new(capacity, dropOldest);

    public static bool Post<T>(Inbox<T> inbox, T item) => inbox.Post(item);

    public static Task<Unit> Send<T>(Inbox<T> inbox, T item) => inbox.Send(item, CancellationToken.None);

    public static Task<Unit> SendWithToken<T>(Inbox<T> inbox, T item, CancellationToken token) =>
        inbox.Send(item, token);

    public static IEvent<Unit> SendEvent<T>(Inbox<T> inbox, T item) => inbox.SendEvent(item);

    public static IEvent<T> Receive<T>(Inbox<T> inbox) => inbox.Receive;

    public static IEvent<global::BjolangRuntime.Option<Exception>> Closed<T>(Inbox<T> inbox) => inbox.Closed;

    public static void Close<T>(Inbox<T> inbox) => inbox.Close();

    public static void Fail<T>(Inbox<T> inbox, Exception error) => inbox.Fail(error);

    public static void Abort<T>(Inbox<T> inbox) => inbox.Abort();

    public static int DroppedCount<T>(Inbox<T> inbox) => inbox.DroppedCount;

    public static IObserver<T> MakeObserver<T>(
        Func<T, Unit> onNext, Func<Exception, Unit> onError, Func<Unit> onCompleted) =>
        new DelegateObserver<T>(onNext, onError, onCompleted);

    // ---- request and reply -------------------------------------------------

    public static Task<TR> CallInbox<TQ, TR>(Inbox<Call<TQ, TR>> inbox, TQ request) =>
        StartCall(inbox, request, CancellationToken.None);

    public static Task<TR> CallInboxWithToken<TQ, TR>(
        Inbox<Call<TQ, TR>> inbox, TQ request, CancellationToken token) =>
        StartCall(inbox, request, token);

    private static Task<TR> StartCall<TQ, TR>(
        Inbox<Call<TQ, TR>> inbox, TQ request, CancellationToken token)
    {
        var call = new Call<TQ, TR>(request);

        // Watched before it is posted: a receiver can have it the instant the
        // post returns, and the token must already be able to find it.
        call.Watch(token);

        if (!inbox.Post(call))
        {
            // Refused by the policy, or closed. `Post` has already faulted the
            // call where a policy refused it; a closed inbox cancels instead.
            if (!call.Task.IsCompleted) ((IInboxItem)call).Discard(null);
        }

        return call.Task;
    }

    public static TQ CallRequest<TQ, TR>(Call<TQ, TR> call) => call.Request;

    public static bool CallReply<TQ, TR>(Call<TQ, TR> call, TR value) => call.Reply(value);

    public static bool CallFail<TQ, TR>(Call<TQ, TR> call, Exception error) => call.Fail(error);

    public static IEvent<Unit> CallAbandoned<TQ, TR>(Call<TQ, TR> call) => call.Abandoned;
}
