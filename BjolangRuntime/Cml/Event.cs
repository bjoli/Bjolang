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

namespace Bjoml;

using System;
using System.Threading.Tasks;

public interface IEvent<T>
{
    void Publish(SyncState sharedState, int eventId, Action<T> onSync);
}

/// <summary>
/// Can this event commit immediately, with no CML machinery at all — and if so,
/// commit it.
///
/// Answering true PERFORMS the rendezvous, so an implementation must not be
/// asked unless the caller is prepared to have committed. Only the two channel
/// operations implement it: everything else either cannot commit without
/// publishing or has no partner to commit against.
///
/// It is a type test at the point of use, never a name the compiler knows. That
/// is what keeps the fast path attached to the VALUE rather than to the syntax,
/// so a channel operation that reached <c>sync</c> through a helper, a record
/// field or a <c>guard</c> still takes it.
///
/// Skipping the cancellation race when this answers true is not a change of
/// meaning: the token branch is published second and publish order is priority,
/// so an event that is available has already beaten it.
/// </summary>
internal interface INowable<T>
{
    bool TryNow(out T value);
}

/// <summary>
/// An event with exactly one commit point, which can therefore be synchronised
/// without a <see cref="SyncState"/>.
///
/// A <see cref="SyncState"/> exists to arbitrate between the branches of one
/// sync block and to fire the nacks of the losers. A block offering a single
/// channel operation has no branches and no nacks, so it pays for a commit
/// protocol it cannot use — an allocation, and two locked nack walks per
/// rendezvous.
///
/// <see cref="SyncDirect"/> is reached only from the top of a sync, never from
/// <c>Publish</c>. That is what makes the unconditional commit sound: an op
/// parked this way cannot be inside a <c>choose</c>, so nothing can ever need to
/// withdraw it. <c>choose</c> publishes its branches the general way and keeps
/// the full protocol.
/// </summary>
internal interface IDirectSyncable<T>
{
    /// <summary>
    /// Commit against a waiting partner if there is one, otherwise park.
    /// <paramref name="onSync"/> is called with the value on whichever thread
    /// commits, which may be this one.
    /// </summary>
    void SyncDirect(Action<T> onSync);

    /// <summary>
    /// The same, with a claim on the parked op so that <paramref name="link"/>
    /// can take it instead of the channel — which is how a cancellation token
    /// reaches a fiber parked on a rendezvous.
    ///
    /// Returns true when it actually parked. False means it committed inline and
    /// <paramref name="onSync"/> has already run, so there is nothing to take and
    /// the caller must not arm anything.
    ///
    /// The claim is only consulted for ops that carry a link, so an unlinked
    /// direct park keeps committing unconditionally.
    /// </summary>
    bool SyncDirect(Action<T> onSync, ITakeable link);
}

public readonly struct Unit
{
    public static readonly Unit Value = default;
}

public static class Cml
{
    // The core entrypoint for all CML operations
    public static void Sync<T>(IEvent<T> ev, Action<T> continuation)
    {
        var state = new SyncState();
        ev.Publish(state, SyncState.RootEventId, continuation);
    }

    /// <summary>
    /// When true, <c>choose</c> publishes its branches in a random rotation
    /// instead of left-to-right. Default false.
    ///
    /// WHAT THIS IS FOR: publish order is priority. When two branches are ready
    /// at publish time (a parked partner, a completed promise, an Always), the
    /// first one published wins the sync, every time. Left-to-right is therefore
    /// a real semantic — deterministic priority, useful and documented — but a
    /// server that syncs on choose(a, b) in a loop with both channels hot will
    /// starve b forever. Go randomizes select for exactly this reason.
    ///
    /// WHAT THIS IS NOT: per-channel fairness. Parked ops in a single channel
    /// are matched FIFO regardless of this flag. Randomization only decides
    /// which BRANCH gets the first chance to match inline.
    ///
    /// Measured cost on the fiber select benchmark: within noise (&lt; 2 ns/op);
    /// the xorshift and rotation are a handful of registers. It is off by
    /// default because deterministic priority is the better default for a
    /// hosted language — predictable, and the language can expose the choice.
    /// </summary>
    public static bool RandomizeChoice { get; set; } = false;

    // Combinators
    public static IEvent<T> Choose<T>(IEvent<T> ev1, IEvent<T> ev2) => new PairChooseEvent<T>(ev1, ev2);

    public static IEvent<T> Choose<T>(IEvent<T> ev1, IEvent<T> ev2, IEvent<T> ev3) => new TripleChooseEvent<T>(ev1, ev2, ev3);

    public static IEvent<T> Choose<T>(params IEvent<T>[] events) => new ChooseEvent<T>(events);
    
    public static IEvent<U> Wrap<T, U>(IEvent<T> ev, Func<T, U> mapper) => new WrapEvent<T, U>(ev, mapper);
    
    public static IEvent<T> Guard<T>(Func<IEvent<T>> generator) => new GuardEvent<T>(generator);
    
    public static IEvent<T> WithNack<T>(Func<IEvent<Unit>, IEvent<T>> generator) => new WithNackEvent<T>(generator);
    
    public static IEvent<T> Always<T>(T value) => new AlwaysEvent<T>(value);
    
    public static IEvent<T> Never<T>() => new NeverEvent<T>();

    // ---- timers ------------------------------------------------------------

    /// <summary>
    /// The event that becomes available <paramref name="ms"/> milliseconds
    /// after it is SYNCED — not after it is built.
    ///
    /// Implemented directly by <see cref="TimeoutEvent"/>, which arms its timer
    /// in <c>Publish</c> — so "relative to each sync" needs no Guard — and
    /// registers a leaf nack to dispose the timer when the branch loses. The
    /// combinator version below is retained as the executable specification;
    /// the direct one exists because the composition was measured at ~1.4 µs
    /// and 1360 B per armed sync where the timer itself costs ~100 ns and
    /// 144 B. Both are run against the same tests.
    /// </summary>
    public static IEvent<Unit> Timeout(int ms) => new TimeoutEvent(ms);

    /// <summary>
    /// The event that becomes available at a fixed instant.
    ///
    /// Absolute, and therefore NOT the same thing as <see cref="Timeout"/>: the
    /// deadline is decided when this is built, and only the remaining interval
    /// is recomputed at each sync. That is what makes it usable as an overall
    /// budget for a loop, where a relative timeout would restart on every
    /// iteration and never expire.
    /// </summary>
    public static IEvent<Unit> At(DateTime utcDeadline) => new AtEvent(utcDeadline);

    /// <summary>
    /// The combinator formulation of <see cref="Timeout"/>, retained as the
    /// executable specification for <see cref="TimeoutEvent"/> and exercised by
    /// the same tests.
    ///
    /// The <see cref="Guard{T}"/> is the whole reason this is not just a
    /// promise and a timer. Built once and reused, a relative deadline is in
    /// the past after the first iteration, so its branch would win every time
    /// round a <c>choose</c> loop thereafter. Rebuilding at each sync is what
    /// "five seconds from now" has to mean inside a loop.
    ///
    /// The <see cref="WithNack{T}"/> is the other half. A losing branch has to
    /// dispose its timer, or every iteration of a select over a timeout leaks a
    /// live one until it fires.
    /// </summary>
    internal static IEvent<Unit> TimeoutViaCombinators(int ms) => Guard(() => TimerEvent(ms));

    /// <summary>
    /// One armed timer, disposed exactly once by whichever of the two paths
    /// gets there first: the branch lost, or the timer fired.
    ///
    /// The same shape as <c>TaskInterop.Cancellable</c>, for the same reason.
    /// </summary>
    private static IEvent<Unit> TimerEvent(int ms) =>
        WithNack<Unit>(nack =>
        {
            var p = new Promise<Unit>();

            var timer = new System.Threading.Timer(
                static s => ((Promise<Unit>)s!).TrySetResult(default),
                p,
                ms,
                System.Threading.Timeout.Infinite);

            int disposed = 0;

            void DisposeOnce()
            {
                if (System.Threading.Interlocked.Exchange(ref disposed, 1) == 0) timer.Dispose();
            }

            // Neither of these runs user code, which is the rule for anything
            // reachable from a nack: they run on a borrowed thread with
            // whatever context it happened to have.
            Sync(nack, _ => DisposeOnce());
            p.OnCompleted(DisposeOnce);

            return Wrap(p.Join(), static r => r.Value);
        });
}

// ---------------- Implementation of Combinators ----------------

/// <summary>
/// Cheap per-thread xorshift for <see cref="Cml.RandomizeChoice"/>. Quality does
/// not matter here — this decides benchmark-invisible tie-breaks, not keys — but
/// allocation and contention do, hence neither <c>Random.Shared</c> (an extra
/// indirection and defensive locking on older runtimes) nor a lock.
/// </summary>
internal static class ChoiceRng
{
    [ThreadStatic] private static uint _state;

    public static uint Next()
    {
        uint x = _state;
        // Seed lazily; keep it odd so the sequence never collapses to zero.
        if (x == 0) x = (uint)Environment.CurrentManagedThreadId * 2654435769u | 1u;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }
}

/// <summary>
/// Optimized binary choice between two events, avoiding array allocation and loop overhead.
/// </summary>
public sealed class PairChooseEvent<T> : IEvent<T>
{
    private readonly IEvent<T> _ev1;
    private readonly IEvent<T> _ev2;

    public PairChooseEvent(IEvent<T> ev1, IEvent<T> ev2)
    {
        _ev1 = ev1;
        _ev2 = ev2;
    }

    public void Publish(SyncState sharedState, int eventId, Action<T> onSync)
    {
        var a = _ev1;
        var b = _ev2;
        if (Cml.RandomizeChoice && (ChoiceRng.Next() & 1) != 0) (a, b) = (b, a);

        a.Publish(sharedState, sharedState.NextEventId(), onSync);
        if (sharedState.IsSynchronized) return;
        b.Publish(sharedState, sharedState.NextEventId(), onSync);
    }
}

/// <summary>
/// Optimized ternary choice between three events, avoiding array allocation and loop overhead.
/// </summary>
public sealed class TripleChooseEvent<T> : IEvent<T>
{
    private readonly IEvent<T> _ev1;
    private readonly IEvent<T> _ev2;
    private readonly IEvent<T> _ev3;

    public TripleChooseEvent(IEvent<T> ev1, IEvent<T> ev2, IEvent<T> ev3)
    {
        _ev1 = ev1;
        _ev2 = ev2;
        _ev3 = ev3;
    }

    public void Publish(SyncState sharedState, int eventId, Action<T> onSync)
    {
        var a = _ev1;
        var b = _ev2;
        var c = _ev3;
        if (Cml.RandomizeChoice)
        {
            // Random rotation. Not a full shuffle, but every branch reaches
            // first position with equal probability, which is what kills
            // starvation of a fixed branch; see ChooseEvent for the reasoning.
            switch (ChoiceRng.Next() % 3)
            {
                case 1: (a, b, c) = (b, c, a); break;
                case 2: (a, b, c) = (c, a, b); break;
            }
        }

        a.Publish(sharedState, sharedState.NextEventId(), onSync);
        if (sharedState.IsSynchronized) return;
        b.Publish(sharedState, sharedState.NextEventId(), onSync);
        if (sharedState.IsSynchronized) return;
        c.Publish(sharedState, sharedState.NextEventId(), onSync);
    }
}

/// <summary>
/// Combines multiple events into a single choice. 
/// In CML, 'choose' allows a thread to wait on multiple possible synchronizations simultaneously.
/// The first event to successfully claim the shared state wins, and all others are discarded.
/// </summary>
public class ChooseEvent<T> : IEvent<T>
{
    private readonly IEvent<T>[] _events;
    public ChooseEvent(params IEvent<T>[] events) { _events = events; }

    public void Publish(SyncState sharedState, int eventId, Action<T> onSync)
    {
        var events = _events;
        int n = events.Length;

        // Random ROTATION, not a full Fisher-Yates shuffle. A shuffle needs a
        // scratch array per sync; a rotation is one modulo. Rotation is enough
        // for the guarantee that matters — every branch reaches first position
        // with probability 1/n, so no fixed branch can be starved by two
        // always-ready earlier siblings — while pairwise order bias between
        // adjacent branches remains, which Go's select does eliminate. If that
        // ever matters, revisit; do not pay a per-sync allocation for it today.
        int start = Cml.RandomizeChoice && n > 1 ? (int)(ChoiceRng.Next() % (uint)n) : 0;

        // Each branch gets a distinct sequential event index.
        // Hopac interval indexing: subtrees are tracked by range [I0, I1) on the SyncState.
        for (int k = 0; k < n; k++)
        {
            // An earlier branch may have committed inline (Always, an already-queued
            // partner, a completed Promise). Publishing the rest would only queue
            // operations that can never win.
            if (sharedState.IsSynchronized) return;

            int idx = start + k;
            if (idx >= n) idx -= n;
            events[idx].Publish(sharedState, sharedState.NextEventId(), onSync);
        }
    }
}

/// <summary>
/// Wraps an event to map its result.
/// Because ChooseEvent requires all its branches to return the same type T, Wrap is essential.
/// It allows you to combine differently-typed events (like receiving an Int vs a String) 
/// into a unified type before passing them to Choose.
/// </summary>
public class WrapEvent<T, U> : IEvent<U>
{
    private readonly IEvent<T> _ev;
    private readonly Func<T, U> _mapper;

    public WrapEvent(IEvent<T> ev, Func<T, U> mapper)
    {
        _ev = ev;
        _mapper = mapper;
    }

    public void Publish(SyncState sharedState, int eventId, Action<U> onSync)
    {
        // Intercept the synchronization callback to apply the mapping function before resuming the user.
        _ev.Publish(sharedState, eventId, value => onSync(_mapper(value)));
    }
}

/// <summary>
/// Delays the creation of an event until it is actually synchronized.
/// This is used when the event requires side-effects or dynamic state to be allocated 
/// only at the exact moment the thread commits to the synchronization block.
/// </summary>
public class GuardEvent<T> : IEvent<T>
{
    private readonly Func<IEvent<T>> _generator;

    public GuardEvent(Func<IEvent<T>> generator) { _generator = generator; }

    public void Publish(SyncState sharedState, int eventId, Action<T> onSync)
    {
        var ev = _generator();
        ev.Publish(sharedState, eventId, onSync);
    }
}

/// <summary>
/// Negative Acknowledgement (NACK). 
/// When composing complex choices, a thread often needs to know if a specific branch LOST the race 
/// so it can abort tentative operations or clean up resources.
/// WithNack passes a NACK event (which fires if the branch loses) into a generator that builds the actual event.
/// </summary>
public class WithNackEvent<T> : IEvent<T>
{
    private readonly Func<IEvent<Unit>, IEvent<T>> _generator;

    public WithNackEvent(Func<IEvent<Unit>, IEvent<T>> generator)
    {
        _generator = generator;
    }

    public void Publish(SyncState sharedState, int eventId, Action<T> onSync)
    {
        // The nack is carried by a Promise, not a Channel.
        //
        // A channel-based nack is EPHEMERAL: firing it parks a PutOp that only a
        // receiver can consume, so a nack nobody listens to leaves an operation
        // stranded in a channel that is then unreachable forever. A promise is
        // PERSISTENT, which is also the better semantics: "this branch lost" is a
        // fact, not a message, so every listener should see it and a listener that
        // arrives late should still see it.
        int i0 = eventId;
        var nack = new Promise<Unit>();

        // Register Nack interval starting at i0.
        var nackNode = sharedState.RegisterNack(i0, () => nack.TrySetResult(default));

        // A nack promise is only ever completed successfully, so the Result wrapper
        // can be projected away.
        var ev = _generator(Cml.Wrap(nack.Join(), static r => r.Value));
        
        // Publish the generated event with our starting eventId.
        ev.Publish(sharedState, eventId, onSync);

        // Subtree ends after the last minted branch index.
        int i1 = Math.Max(sharedState.CurrentEventId, i0 + 1);
        if (nackNode != null)
        {
            nackNode.I1 = i1;
            if (sharedState.IsSynchronized)
            {
                int winner = sharedState.WinningEventId;
                if (winner < i0 || i1 <= winner)
                {
                    nack.TrySetResult(default);
                }
            }
        }
    }
}

public class AlwaysEvent<T> : IEvent<T>
{
    private readonly T _value;
    public AlwaysEvent(T value) { _value = value; }

    public void Publish(SyncState sharedState, int eventId, Action<T> onSync)
    {
        // Commit outright, bypassing the queues entirely.
        //
        // TryCommit rather than TryClaim + MarkSynchronized: a bare TryClaim treats
        // a transient C (some other branch mid-pairing) as "already lost" and
        // silently drops an event that is always enabled.
        if (sharedState.TryCommit(eventId))
            Scheduler.Dispatch(onSync, _value);
    }
}

public class NeverEvent<T> : IEvent<T>
{
    public void Publish(SyncState sharedState, int eventId, Action<T> onSync)
    {
        // Do nothing. It never synchronizes, mimicking an event that never occurs.
    }
}

// ---------------- Base Channel Events ----------------

/// <summary>
/// The event of handing one value over.
///
/// Unlike a receive this cannot be the channel itself, because it has to carry
/// the value. It is the object `(chan-send ch v)` evaluates to.
/// </summary>
public class ChannelSendEvent<T> : IEvent<Unit>, INowable<Unit>, IDirectSyncable<Unit>
{
    private readonly Channel<T> _channel;
    private readonly T _value;

    public ChannelSendEvent(Channel<T> channel, T value)
    {
        _channel = channel;
        _value = value;
    }

    public ChannelSendAwaiter<T> GetAwaiter() => new(_channel, _value);

    public void Publish(SyncState sharedState, int eventId, Action<Unit> onSync)
    {
        _channel.PublishSend(sharedState, eventId, _value, onSync);
    }

    bool INowable<Unit>.TryNow(out Unit value)
    {
        value = default;
        return _channel.TryDirectSend(_value);
    }

    void IDirectSyncable<Unit>.SyncDirect(Action<Unit> onSync) =>
        _channel.SyncDirectSend(_value, onSync);

    bool IDirectSyncable<Unit>.SyncDirect(Action<Unit> onSync, ITakeable link) =>
        _channel.SyncDirectSend(_value, onSync, link);
}

public class ChannelReceiveEvent<T> : IEvent<T>
{
    private readonly Channel<T> _channel;

    public ChannelReceiveEvent(Channel<T> channel)
    {
        _channel = channel;
    }

    public ChannelReceiveAwaiter<T> GetAwaiter() => new(_channel);

    public void Publish(SyncState sharedState, int eventId, Action<T> onSync)
    {
        _channel.PublishReceive(sharedState, eventId, onSync);
    }
}