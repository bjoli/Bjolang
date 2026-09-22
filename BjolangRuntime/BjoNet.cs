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

// TCP, as two byte ports.
//
// THE ONE INVARIANT, which is `BjoBytePort`'s one layer out:
//
//   A connection the operating system has handed over belongs to the listener,
//   not to the event that asked for it. An accept never takes a connection from
//   the kernel — it takes one from the listener's slot, and only when it wins.
//
// Commitment is taking a connection out of a slot and nothing else. That is
// what makes an accept safe inside a `choose`, and it is why an accept that
// completes after its branch has already lost is harmless rather than a leaked
// descriptor and a client that thinks it is connected.
//
// This file is deliberately the same shape as `BjoBytePort.cs`, member for
// member, because it is the same problem with a connection where the buffer is:
//
//   a refill  <->  an accept, at most one in flight, never cancelled;
//   the buffer <->  the slot, which holds the one connection nobody has taken;
//   the three-step take — `BeginLocked` under the lock, `TryCommit` outside it
//     because committing fires the losers' nacks, then `Handover` or
//     `UndoLocked` — identically;
//   `Deliver`, one runner and not recursion, identically.
//
// WHAT IS DELIBERATELY NOT HERE
//
//   * A cancellable accept. An `AcceptAsync` that has already completed cannot
//     be un-accepted: the connection is established and the client already
//     believes it. Cancelling would leave it with no owner.
//   * Two accepts at once. One slot, one in flight; the kernel's backlog is the
//     queue, and it is much better at being one than this would be.
//   * A connection built before the commit. `Handover` is what turns the
//     accepted `Socket` into a `BjoConnection`, so a branch that loses has
//     allocated nothing, registered nothing, and put the socket back.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Bjoml;
using Unit = Bjoml.Unit;
using AcceptResult = BjolangRuntime.Result<System.Exception, Bjolang.Runtime.BjoConnection>;

namespace Bjolang.Runtime;

/// <summary>
/// A listening socket whose accepts are CML events.
///
/// # Closing, in two steps rather than one
///
/// <see cref="Close"/> is `close-listener!` and is GRACEFUL: it stops accepting,
/// closes the listening socket and fires <see cref="Closed"/>. Connections
/// already accepted are **not** touched — they belong to whoever is handling
/// them, and killing a request half-answered is not what "stop listening"
/// means. That is `inbox-close!`'s meaning, one layer out.
///
/// <see cref="Dispose"/> is what the scope runs, and is the hard stop: the
/// above, and then every connection still live. A server drains by closing the
/// listener, letting its handlers finish, and leaving the scope.
///
/// The registry of live connections exists for that second step. It is also
/// what a future websocket shutdown needs, which is why it is here rather than
/// in whatever is built on this.
/// </summary>
public sealed class BjoTcpListener : IDisposable {
    private readonly object _lock = new();
    private readonly Socket _socket;

    /// The one connection the operating system has handed over and no accept
    /// has taken. The kernel's backlog is the queue behind it.
    private Socket? _slot;

    /// The accept in flight, or null. Holding it in a field is the whole of
    /// "at most one": a second acceptor joins this rather than starting another.
    private Task? _accepting;

    /// A take is tentatively holding the slot: it has emptied it and is out at
    /// <c>TryCommit</c>, which may yet fail and put it back. See
    /// `BjoByteInputPort._taking`, which is the same field for the same reason.
    private bool _taking;

    private TaskCompletionSource? _quiet;

    /// The socket failed once. Sticky, as a byte port's is.
    private Exception? _error;

    /// Set before the listening socket is disposed, so that the failure the
    /// pending accept is about to see can be told from a real one.
    private int _closing;

    private bool _closed;
    private bool _disposed;

    private Waiter? _waiters;

    private const int Idle = 0;
    private const int Running = 1;
    private const int RunningAgain = 2;
    private int _deliverState;

    private int _published;

    private readonly HashSet<BjoConnection> _live = new();
    private readonly Gate<Unit> _closedGate = new();

    /// <summary>See <see cref="BjoPort.Owner"/>.</summary>
    public BjolangRuntime.Owned? Owner;

    private BjoTcpListener(Socket socket) {
        _socket = socket;
        // Read once and kept: after the socket is disposed there is no endpoint
        // to ask, and `listener-port` is most useful precisely for a listener
        // bound to port 0, which the program has to be able to ask afterwards.
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>The port actually bound, which is the interesting question when
    /// 0 was asked for.</summary>
    public int Port { get; }

    /// <summary>Fires once the listener has stopped accepting, and from then on
    /// commits immediately on every sync — `inbox-closed`'s shape.</summary>
    public IEvent<Unit> Closed => _closedGate;

    /// <summary>
    /// How many accepts have gone the published way rather than taking the
    /// <see cref="INowable{T}"/> fast path. For the tests, and only for them.
    /// </summary>
    internal int PublishCount => Volatile.Read(ref _published);

    private int _accepts;
    private int _maxAccepts;

    /// <summary>
    /// The most accepts that were ever in flight at once, which has to be one.
    /// For the tests, and only for them.
    /// </summary>
    internal int MaxConcurrentAccepts => Volatile.Read(ref _maxAccepts);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    // --- Binding ------------------------------------------------------------

    /// <summary>
    /// Bind and listen. Whatever this throws becomes a `Result` on the Bjolang
    /// side, which is why nothing is caught.
    /// </summary>
    public static BjoTcpListener Bind(string host, int port, int backlog) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(backlog, 1);

        var address = ResolveOne(host);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        try {
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen(backlog);
        } catch {
            socket.Dispose();
            throw;
        }

        return new BjoTcpListener(socket);
    }

    /// The first address a host name has, or the literal it already is.
    ///
    /// No happy eyeballs and no parallel attempts: an address that answers is
    /// used and the rest are not consulted. A literal — `127.0.0.1`, `::1` — is
    /// parsed rather than resolved, so the common case asks nothing of DNS.
    internal static IPAddress ResolveOne(string host) =>
        IPAddress.TryParse(host, out var literal) ? literal : ResolveAll(host)[0];

    internal static IPAddress[] ResolveAll(string host) {
        var found = Dns.GetHostAddresses(host);
        if (found.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        return found;
    }

    // --- Accepting ----------------------------------------------------------

    /// <summary>
    /// The accept in flight, starting one if there is none and there is room in
    /// the slot for what it brings. Never faults, and never cancels.
    /// </summary>
    private void EnsureAccept() {
        TaskCompletionSource tcs;

        lock (_lock) {
            if (_accepting is not null) return;
            if (_closed || _disposed || _error is not null || _slot is not null) return;

            // Published into the field before the accept starts, so that one
            // completing synchronously cannot find it null and let a second
            // through.
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _accepting = tcs.Task;
        }

        // Started outside the lock: a synchronous completion runs `Pump` to its
        // end right here, and its end is `Deliver`, which commits waiters and
        // resumes fibers — none of which may happen with the lock held.
        _ = Pump(tcs);
    }

    private async Task Pump(TaskCompletionSource tcs) {
        Socket? accepted = null;
        Exception? failure = null;

        // Two concurrent accepts on one listening socket is not undefined the
        // way two reads on one stream are, but it would defeat the slot: two
        // connections and one place to put them. The counters are how a test
        // sees that it never happens; the guarantee itself is that `_accepting`
        // is published under the lock before this is started.
        int inFlight = Interlocked.Increment(ref _accepts);
        int seen;
        while (inFlight > (seen = Volatile.Read(ref _maxAccepts)))
            if (Interlocked.CompareExchange(ref _maxAccepts, inFlight, seen) == seen)
                break;

        try {
            accepted = await _socket.AcceptAsync(CancellationToken.None).ConfigureAwait(false);
        } catch (ObjectDisposedException) {
            // The listener was closed under us. Not a failure: `Closed` is what
            // says so, and it has already been fired.
        } catch (SocketException e) {
            // The same thing, reported the other way on some platforms.
            if (Volatile.Read(ref _closing) == 0) failure = e;
        } catch (Exception e) {
            failure = e;
        } finally {
            Interlocked.Decrement(ref _accepts);
        }

        bool orphaned = false;

        lock (_lock) {
            if (accepted is not null) {
                if (_closed) orphaned = true;
                else _slot = accepted;
            } else if (failure is not null) {
                _error ??= failure;
            }

            // Cleared before the task is completed, so a waiter woken by it that
            // still cannot be served asks for a FRESH accept.
            _accepting = null;
        }

        // A connection that landed after the listener closed has nobody to go
        // to. Closing it is the honest answer: a client sees a connection that
        // died rather than one nothing will ever read.
        if (orphaned) accepted!.Dispose();

        tcs.TrySetResult();
        Deliver();
    }

    /// <summary>
    /// One accepted socket, turned into a connection this listener will remember
    /// until both its halves are closed.
    ///
    /// Only ever called AFTER a commit, which is the point: a branch that loses
    /// has built nothing and registered nothing.
    /// </summary>
    private BjoConnection Adopt(Socket socket) {
        var connection = NewConnection(socket);
        bool tracked;

        lock (_lock) {
            tracked = !_closed && !_disposed;
            if (tracked) _live.Add(connection);
        }

        // Accepted in the window where the listener closed. It is still a
        // perfectly good connection and the accept won it, so it is handed over
        // — but this listener is done and will not be closing it, so it is the
        // caller's alone.
        if (tracked) connection.Forget = Forget;

        return connection;
    }

    private void Forget(BjoConnection connection) {
        lock (_lock) { _live.Remove(connection); }
    }

    /// <summary>
    /// A connected socket as a connection: Nagle off, the peer remembered, and
    /// the stream owning the socket.
    ///
    /// **Nagle is off deliberately.** A byte port has a write buffer of its own
    /// and `flush!` is the program saying "send this now"; leaving Nagle on
    /// would hold a flushed small write back waiting for an ACK, which is
    /// exactly wrong for a request/response protocol.
    /// </summary>
    internal static BjoConnection NewConnection(Socket socket) {
        try { socket.NoDelay = true; } catch (SocketException) { /* not fatal */ }

        string? peer = null;
        try { peer = socket.RemoteEndPoint?.ToString(); } catch (SocketException) { }
        catch (ObjectDisposedException) { }

        return new BjoConnection(new NetworkStream(socket, ownsSocket: true), peer);
    }

    // --- Waiting ------------------------------------------------------------

    /// <summary>
    /// One published accept, parked until the slot can answer it.
    ///
    /// The three steps are apart for the reason `BjoByteInputPort.Waiter`'s are:
    /// <c>TryCommit</c> fires the losing branches' nacks and a nack can resume a
    /// fiber, so it must not run under the listener's lock.
    /// </summary>
    private sealed class Waiter {
        public SyncState State = null!;
        public int EventId;
        public Action<AcceptResult> OnSync = null!;
        public Waiter? Next;

        private Socket? _taken;
        private Exception? _failure;

        /// Under the lock: can this be answered now, and if so take it.
        public bool BeginLocked(BjoTcpListener l) {
            if (l._slot is { } ready) {
                _taken = ready;
                l._slot = null;
                return true;
            }

            if (l._error is { } e) {
                _failure = e;
                return true;
            }

            return false;
        }

        /// Outside the lock, the commit having succeeded. The connection is
        /// built HERE and nowhere earlier.
        public void Handover(BjoTcpListener l) {
            AcceptResult value;

            if (_taken is { } socket) {
                _taken = null;
                value = AcceptResult.Ok(l.Adopt(socket));
            } else {
                value = AcceptResult.Err(_failure!);
                _failure = null;
            }

            InboxWake.Resume(OnSync, value);
        }

        /// Under the lock, the commit having failed. The socket goes back in the
        /// slot, untouched and unwrapped, for the next acceptor.
        public void UndoLocked(BjoTcpListener l) {
            if (_taken is { } socket) {
                l._slot = socket;
                _taken = null;
            }

            _failure = null;
        }
    }

    private void AppendLocked(Waiter w) {
        if (_waiters is null) {
            _waiters = w;
            return;
        }

        var last = _waiters;
        while (last.Next is not null) last = last.Next;
        last.Next = w;
    }

    private Waiter? UnlinkLocked(Waiter? prev, Waiter cur) {
        var next = cur.Next;
        if (prev is null) _waiters = next;
        else prev.Next = next;
        cur.Next = null;
        return next;
    }

    /// <summary>
    /// Match what is in the slot with who is waiting, then make sure an accept
    /// is in flight.
    ///
    /// ONE RUNNER, NOT RECURSION, for the reason `Inbox.Settle` gives: serving a
    /// waiter can complete an accept inline, which re-enters this.
    /// </summary>
    private void Deliver() {
        // Take ownership, or leave a note for whoever has it. The note is a
        // compare-and-swap from `Running` and never a plain write — see
        // `BjoByteInputPort.Deliver`, where the same plain write left the flag
        // set with nobody running and killed the port for good.
        while (true) {
            int state = Volatile.Read(ref _deliverState);

            if (state == Idle) {
                if (Interlocked.CompareExchange(ref _deliverState, Running, Idle) == Idle) break;
                continue;
            }

            if (state == RunningAgain) return;
            if (Interlocked.CompareExchange(ref _deliverState, RunningAgain, Running) == Running) return;
        }

        do {
            Volatile.Write(ref _deliverState, Running);

            while (ServeStep()) { }

            // Unconditionally, and not only when somebody is waiting: the slot
            // is a prefetch of exactly one, so a listener that has just handed a
            // connection over goes straight back to having one ready.
            EnsureAccept();
        }
        while (Interlocked.CompareExchange(ref _deliverState, Idle, Running) != Running);
    }

    private bool ServeStep() {
        Waiter? chosen = null;

        lock (_lock) {
            if (_taking) return false;

            Waiter? prev = null;
            var cur = _waiters;

            while (cur is not null) {
                // Lost elsewhere in its own sync block, and swept the next time
                // anything walks the list.
                if (cur.State.IsSynchronized) {
                    cur = UnlinkLocked(prev, cur);
                    continue;
                }

                if (cur.BeginLocked(this)) {
                    chosen = cur;
                    UnlinkLocked(prev, cur);
                    break;
                }

                prev = cur;
                cur = cur.Next;
            }

            if (chosen is null) return false;
            _taking = true;
        }

        if (chosen.State.TryCommit(chosen.EventId)) {
            EndTake();
            chosen.Handover(this);
            return true;
        }

        // Another branch of that sync won in the window above. Nothing is
        // consumed: the socket goes back in the slot, still a raw socket, and
        // the next acceptor gets it.
        lock (_lock) { chosen.UndoLocked(this); }
        EndTake();
        return true;
    }

    private void EndTake() {
        TaskCompletionSource? quiet;

        lock (_lock) {
            _taking = false;
            quiet = _quiet;
            _quiet = null;
            Monitor.PulseAll(_lock);
        }

        quiet?.TrySetResult();
    }

    private Task QuietLocked() {
        _quiet ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _quiet.Task;
    }

    // --- The event's two halves ---------------------------------------------

    /// <summary>
    /// The <see cref="INowable{T}"/> fast path: the slot has a connection, so
    /// the accept commits without publishing anything at all.
    ///
    /// Answering true PERFORMS the rendezvous, so this takes outright and there
    /// is nothing to undo — and it refuses while someone else holds a tentative
    /// take, because a take it cannot undo must not interleave with one that may
    /// be.
    /// </summary>
    internal bool TryAcceptNow(out AcceptResult value) {
        Socket? ready = null;
        Exception? failure = null;

        lock (_lock) {
            ThrowIfDisposed();

            if (_taking) {
                value = default;
                return false;
            }

            if (_slot is { } waiting) {
                ready = waiting;
                _slot = null;
            } else if (_error is { } e) {
                failure = e;
            } else {
                value = default;
                return false;
            }
        }

        value = ready is not null ? AcceptResult.Ok(Adopt(ready)) : AcceptResult.Err(failure!);

        // The slot is empty now, so line the next one up.
        Deliver();
        return true;
    }

    internal void PublishAccept(SyncState state, int eventId, Action<AcceptResult> onSync) {
        ThrowIfDisposed();
        Interlocked.Increment(ref _published);

        var w = new Waiter { State = state, EventId = eventId, OnSync = onSync };
        lock (_lock) { AppendLocked(w); }
        Deliver();
    }

    // --- Closing ------------------------------------------------------------

    /// <summary>
    /// `close-listener!` — stop accepting, and say so.
    ///
    /// Graceful, and that is the whole difference from <see cref="Dispose"/>: a
    /// connection already accepted is left alone, because it belongs to whoever
    /// is handling it. An accept parked on this listener is never committed
    /// again — <see cref="Closed"/> is how a loop finds out, exactly as
    /// `inbox-closed` is for a closed inbox.
    /// </summary>
    public void Close() {
        lock (_lock) {
            if (_closed) return;
            _closed = true;
            Volatile.Write(ref _closing, 1);
        }

        // The wakeup for an accept in flight. It sees `ObjectDisposedException`
        // or a `SocketException`, and `_closing` is what tells it which of those
        // is a failure and which is this.
        try { _socket.Dispose(); } catch (Exception) { /* closing twice is not news */ }

        Socket? orphan;
        lock (_lock) {
            orphan = _slot;
            _slot = null;
        }

        // Accepted, never handed over, and now never will be.
        orphan?.Dispose();

        _closedGate.Signal(Unit.Value);
    }

    /// <summary>
    /// The hard stop, and what the scope runs: everything <see cref="Close"/>
    /// does, and then every connection this listener accepted that is still
    /// open.
    /// </summary>
    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
        }

        Close();

        List<BjoConnection> live;
        lock (_lock) {
            live = new List<BjoConnection>(_live);
            _live.Clear();
        }

        foreach (var connection in live) connection.Dispose();
    }
}

// ---------------------------------------------------------------------------
// The event
// ---------------------------------------------------------------------------

/// <summary>
/// An accept, as a CML event. The same shape as `ByteReadEvent` beside it.
///
/// EPHEMERAL, and that is the point. Every <c>sync</c> asks the listener a new
/// question and there is no memoised result, so a server loop accepts
/// successive connections rather than winning with the same one forever — which
/// is what <c>(task-&gt;event ...)</c> does, and one of the two reasons it is
/// not this.
/// </summary>
internal sealed class TcpAcceptEvent : IEvent<AcceptResult>, INowable<AcceptResult> {
    private readonly BjoTcpListener _listener;

    internal TcpAcceptEvent(BjoTcpListener listener) => _listener = listener;

    public void Publish(SyncState state, int eventId, Action<AcceptResult> onSync) =>
        _listener.PublishAccept(state, eventId, onSync);

    bool INowable<AcceptResult>.TryNow(out AcceptResult value) => _listener.TryAcceptNow(out value);
}

// ---------------------------------------------------------------------------
// The dispatchers
// ---------------------------------------------------------------------------

/// <summary>
/// What `(std net)` imports, one entry per operation. `BytePorts`' shape, for
/// the reason given there: one prefix keeps the `import/extern` block readable.
/// </summary>
public static class Net {
    // --- Connecting ---------------------------------------------------------

    /// <summary>
    /// Connect, trying each address the host has in turn. The `#:exceptions` on
    /// the Bjolang side turns whatever this throws into a `Result`.
    ///
    /// The token is the ambient one, filled in at the call site by the `#:async`
    /// import. Cancelling abandons the connect and disposes the socket it was
    /// using — unlike a read, an incomplete connect has taken nothing that could
    /// be lost by stopping.
    /// </summary>
    public static async ValueTask<BjoConnection> Connect(
        string host, int port, CancellationToken cancel = default) {

        ArgumentNullException.ThrowIfNull(host);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, cancel).ConfigureAwait(false);

        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        Exception? last = null;

        foreach (var address in addresses) {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            try {
                await socket.ConnectAsync(address, port, cancel).ConfigureAwait(false);
                return BjoTcpListener.NewConnection(socket);
            } catch (Exception e) {
                socket.Dispose();
                // A cancelled connect is the caller's deadline, not an address
                // that did not answer, so there is nothing to try next.
                if (e is OperationCanceledException) throw;
                last = e;
            }
        }

        throw last!;
    }

    // --- Listening ----------------------------------------------------------

    public static BjoTcpListener Listen(string host, int port, int backlog) =>
        BjoTcpListener.Bind(host, port, backlog);

    public static IEvent<AcceptResult> AcceptEvent(BjoTcpListener listener) =>
        new TcpAcceptEvent(listener);

    public static IEvent<Unit> ClosedEvent(BjoTcpListener listener) => listener.Closed;

    public static int ListenerPort(BjoTcpListener listener) => listener.Port;

    public static Unit CloseListener(BjoTcpListener listener) {
        listener.Close();
        return default;
    }

    // --- A connection's two halves ------------------------------------------

    public static BjoByteInputPort ConnectionInput(BjoConnection connection) => connection.Input();

    public static BjoByteOutputPort ConnectionOutput(BjoConnection connection) => connection.Output();

    public static BjolangRuntime.Option<string> PeerAddress(BjoByteInputPort port) =>
        port.Peer is { } peer ? BjolangRuntime.Some(peer) : BjolangRuntime.None<string>();
}
