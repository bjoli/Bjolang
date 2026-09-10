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

namespace Bjoml;

/// <summary>
/// An intrusive singly-linked record for negative acknowledgements (NACKs).
/// Follows Hopac's interval-based choice tracking: each NACK guards an index interval [I0, I1).
/// </summary>
public sealed class NackNode
{
    public readonly Action Action;
    public readonly int I0;
    public int I1;
    public NackNode? Next;

    public NackNode(Action action, int i0, NackNode? next)
    {
        Action = action;
        I0 = i0;
        I1 = int.MaxValue;
        Next = next;
    }
}

/// <summary>
/// The lock-free state machine shared by every branch of one <c>Cml.Sync</c> block.
///
/// <list type="bullet">
/// <item><c>W</c> Waiting — pending, claimable by any matching thread.</item>
/// <item><c>C</c> Claimed — a thread is inspecting this state to pair it. Held
/// across a handful of instructions only, never across user code, which is what
/// makes spinning on it safe.</item>
/// <item><c>S</c> Synchronized — paired, terminal.</item>
/// </list>
///
/// Borrowed from Hopac Core: choice branches are indexed with sequential integers 0, 1, 2, ...
/// Subtrees are tracked as contiguous intervals [I0, I1). A nack fires only when the
/// winning branch index lies OUTSIDE [I0, I1), completely avoiding dictionary allocations,
/// tree hashing, and locks for ID generation.
/// </summary>
public class SyncState
{
    public const int W = 0;
    public const int C = 1;
    public const int S = 2;

    /// <summary>Root event index.</summary>
    public const int RootEventId = 0;

    private int _value = W;
    private int _eventIdCounter = RootEventId;
    private int _winningEventId = -1;

    // Intrusive singly-linked list of NACKs.
    // Allocated on demand only when withNack is actually used.
    private NackNode? _nacks;

    // B7 cleanup is deliberately NOT tracked here. SyncState used to remember every
    // channel a block parked in so that committing could drive cleanup, and that cost
    // 36 ns/op on Select/Choose — see the B7 section of docs/design.md. The trigger
    // lives in Channel<T> instead, where a park is already visible under a lock that
    // is already held.

    /// <summary>The next event index to be minted.</summary>
    public int CurrentEventId => Volatile.Read(ref _eventIdCounter);

    /// <summary>The winning event ID that synchronized this state.</summary>
    public int WinningEventId => Volatile.Read(ref _winningEventId);

    /// <summary>Mint a fresh sequential leaf/branch id.</summary>
    public int NextEventId() => Interlocked.Increment(ref _eventIdCounter) - 1;

    // ---- state transitions -------------------------------------------------

    /// <summary>W -&gt; C. Take exclusive ownership so we can pair against another state.</summary>
    public bool TryClaim() => Interlocked.CompareExchange(ref _value, C, W) == W;

    /// <summary>
    /// W -&gt; S. Called on the OPPOSING party's state by a thread that already holds
    /// C on its own. Does not fire nacks; the caller follows up with
    /// <see cref="MarkSynchronized"/>.
    /// </summary>
    public bool TrySync() => Interlocked.CompareExchange(ref _value, S, W) == W;

    /// <summary>C -&gt; W. Release a claim we could not turn into a pairing.</summary>
    public void ResetClaim() => Volatile.Write(ref _value, W);

    public bool IsSynchronized => Volatile.Read(ref _value) == S;

    /// <summary>
    /// Atomic W -&gt; C -&gt; S for events that commit on their own initiative rather
    /// than by pairing: <c>Always</c>, <c>Promise</c>, timers.
    ///
    /// Crucially it spins past a transient <c>C</c> instead of reporting failure.
    /// A plain <c>TryClaim</c> here silently DROPS the completion: a promise that
    /// lands on a thread-pool thread while the publishing thread happens to hold
    /// <c>C</c> for another branch would be discarded, and the choose would wait
    /// forever on an event that already fired.
    ///
    /// Returns false only when the block was genuinely won by someone else.
    /// </summary>
    public bool TryCommit(int eventId)
    {
        var sw = new SpinWait();
        while (true)
        {
            int v = Volatile.Read(ref _value);
            if (v == S) return false;                 // lost for real
            if (v == W && Interlocked.CompareExchange(ref _value, C, W) == W)
            {
                MarkSynchronized(eventId);
                return true;
            }
            sw.SpinOnce();                            // v == C: transient, retry
        }
    }

    /// <summary>
    /// Finish a synchronisation and fire the nacks of every losing subtree.
    ///
    /// PRECONDITION: the caller must already own this state, having driven it to
    /// <c>C</c> via <see cref="TryClaim"/>/<see cref="TryCommit"/> or to <c>S</c>
    /// via <see cref="TrySync"/>. Calling it on an unowned state blindly stomps
    /// another thread's claim.
    /// </summary>
    public void MarkSynchronized(int winningEventId)
    {
        _winningEventId = winningEventId;
        Volatile.Write(ref _value, S);

        NackNode? toFireHead = null;

        lock (this)
        {
            var curr = _nacks;
            _nacks = null;
            while (curr != null)
            {
                var next = curr.Next;
                if (winningEventId < curr.I0 || curr.I1 <= winningEventId)
                {
                    curr.Next = toFireHead;
                    toFireHead = curr;
                }
                curr = next;
            }
        }

        while (toFireHead != null)
        {
            // Fired INLINE, not enqueued. A nack action is required to never run
            // user code (see FiberContext's limitation note) — the built-in ones
            // close a timeout node's gate or TrySetResult a nack promise — so the
            // committing thread can run it directly and skip a pool work item,
            // which costs several times what the action does (a cross-thread pool
            // item is ~185 ns before it wakes anyone; see the queue-cost table in
            // BENCHMARKS.md).
            //
            // This enqueue — not the timer mechanism — was the dominant cost of
            // the armed choose+timeout path: firing inline took it from 791 to
            // 317 ns/op. That was established by building a timer wheel to
            // replace System.Threading.Timer and measuring no difference; the
            // wheel was then discarded. See "The timer wheel: built, measured,
            // and rejected" in BENCHMARKS.md.
            //
            // The try/catch is load-bearing: this runs inside the rendezvous
            // commit path, and a throwing nack must not unwind into a channel
            // matching loop.
            try { toFireHead.Action(); }
            catch (Exception ex) { Scheduler.ReportUnhandled(ex); }
            toFireHead = toFireHead.Next;
        }
    }

    /// <summary>
    /// Register a NACK callback for the interval starting at <paramref name="i0"/>.
    /// Returns the <see cref="NackNode"/> whose <c>I1</c> should be updated after
    /// publishing the guarded subtree.
    /// </summary>
    public NackNode? RegisterNack(int i0, Action nack)
    {
        lock (this)
        {
            if (!IsSynchronized)
            {
                var node = new NackNode(nack, i0, _nacks);
                _nacks = node;
                return node;
            }
        }

        // Another branch won before we finished publishing this one.
        // Fire it now if the winning event is outside [i0, +inf).
        if (_winningEventId < i0)
        {
            Scheduler.Enqueue(nack);
        }

        return null;
    }
}
