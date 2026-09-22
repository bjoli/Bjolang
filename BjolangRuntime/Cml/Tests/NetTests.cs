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

// The claims about `BjoTcpListener` that Bjolang cannot check from the outside.
//
// `TestFiles/232_net.bjo` covers the behaviour — round trips, the half-close,
// `limited` over a socket, and the racy form of "a losing accept consumes
// nothing" over two hundred rounds. What is here needs either to see inside
// (the publish counter, the accept counter) or to reach a socket option the
// language does not expose (`SO_LINGER`, which is how a reset is produced on
// purpose).

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using static Bjoml.Tests.Harness;
using Bjolang.Runtime;
using AcceptResult = BjolangRuntime.Result<System.Exception, Bjolang.Runtime.BjoConnection>;

namespace Bjoml.Tests;

public static class NetTests
{
    public static void RunAll()
    {
        Section("TCP");
        Run("an accept the slot can answer commits without publishing", TryNowIsTheFastPath);
        Run("a losing accept consumes nothing", LoserAcceptsNothing);
        Run("at most one accept is ever in flight", OneAcceptAtATime);
        Run("a reset is not a clean end of input", ResetIsNotEndOfInput);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static BjoTcpListener Listening() => BjoTcpListener.Bind("127.0.0.1", 0, 16);

    private static Socket ClientTo(BjoTcpListener listener)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(new IPEndPoint(IPAddress.Loopback, listener.Port));
        return socket;
    }

    /// One accept, the published way, with a bounded wait.
    private static BjoConnection Accept(BjoTcpListener listener, string what)
    {
        var landed = new ManualResetEventSlim(false);
        AcceptResult result = default;

        Cml.Sync(Net.AcceptEvent(listener), r => { result = r; landed.Set(); });
        Await(landed, what);

        if (!result.IsOk) throw new AssertionException($"{what}: the accept failed with {result.ErrValue}");
        return result.OkValue;
    }

    // -----------------------------------------------------------------------
    // The fast path
    // -----------------------------------------------------------------------

    /// <summary>
    /// The listener keeps one connection ready — the slot is a prefetch of
    /// exactly one — so an accept that arrives after a connection has landed
    /// must take it outright rather than publish a waiter.
    ///
    /// Nothing visible from Bjolang distinguishes the two paths, so the listener
    /// counts its publishes and this looks at the count.
    /// </summary>
    private static void TryNowIsTheFastPath()
    {
        using var listener = Listening();

        // The first accept has nothing to take and must publish. It is also what
        // starts the listener accepting at all, which is deliberate: a listener
        // nobody accepts on does not accept.
        using var first = ClientTo(listener);
        var one = Accept(listener, "the first accept");
        AssertEqual(1, listener.PublishCount, "the first accept did not publish");
        one.Dispose();

        // A second connection lands in the slot the first accept's completion
        // lined up. Taking it must not publish again.
        using var second = ClientTo(listener);

        var fast = Net.AcceptEvent(listener);
        var now = (INowable<AcceptResult>)fast;

        AcceptResult ready = default;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        bool took = false;

        while (DateTime.UtcNow < deadline)
        {
            if (now.TryNow(out ready)) { took = true; break; }
            Thread.Sleep(5);
        }

        Assert(took, "a connection sitting in the slot was never taken by the fast path");
        Assert(ready.IsOk, "the fast path answered a failure");
        AssertEqual(1, listener.PublishCount, "taking a ready connection published");

        ready.OkValue.Dispose();
    }

    // -----------------------------------------------------------------------
    // The loser
    // -----------------------------------------------------------------------

    /// <summary>
    /// The deterministic form of the property the design is for.
    ///
    /// The accept is published first against a listener nothing has connected
    /// to, so it parks; the sibling is an `Always` and commits at once; only
    /// then does a client arrive, so the connection lands for a branch that has
    /// already lost. Nothing may have been consumed — the accept after it gets
    /// that same connection, working.
    ///
    /// The racy form, where the connection and the sibling land together, is
    /// `TestFiles/232_net.bjo`: two hundred rounds, and it reports the split.
    /// </summary>
    private static void LoserAcceptsNothing()
    {
        using var listener = Listening();

        var accept = Cml.Wrap(Net.AcceptEvent(listener), static _ => "accepted");
        var other = Cml.Always("other");

        string? won = null;
        var settled = new ManualResetEventSlim(false);

        Cml.Sync(Cml.Choose(accept, other), w => { won = w; settled.Set(); });

        Await(settled, "the choose to settle");
        AssertEqual("other", won, "an accept with nothing to accept won the choose");

        // The connection arrives now, for a branch that is long gone.
        using var client = ClientTo(listener);
        client.Send(new byte[] { 42, 43 });

        var connection = Accept(listener, "the accept after the loser");

        var port = connection.Input();
        var buffer = new byte[2];
        int n = port.ReadInto(buffer);

        AssertEqual(2, n, "the losing branch swallowed the connection's first bytes");
        AssertEqual(42, buffer[0], "the first byte after the loser");
        AssertEqual(43, buffer[1], "the second byte after the loser");

        port.Dispose();
        connection.Dispose();
    }

    // -----------------------------------------------------------------------
    // One accept
    // -----------------------------------------------------------------------

    /// <summary>
    /// The slot holds one connection, so there must never be two accepts in
    /// flight racing to put something in it. The guarantee is that `_accepting`
    /// is published under the lock before the accept starts; this is how a test
    /// sees that it held.
    ///
    /// The tally beside it is the other half of the claim: serializing on one
    /// accept must not lose a connection or hand one over twice.
    /// </summary>
    private static void OneAcceptAtATime()
    {
        const int howMany = 12;
        using var listener = Listening();

        var clients = new Socket[howMany];
        for (int i = 0; i < howMany; i++)
        {
            clients[i] = ClientTo(listener);
            clients[i].Send(new byte[] { (byte)i });
        }

        var seen = new int[howMany];

        for (int i = 0; i < howMany; i++)
        {
            var connection = Accept(listener, $"connection {i}");
            var port = connection.Input();

            var one = new byte[1];
            AssertEqual(1, port.ReadInto(one), $"connection {i} carried nothing");
            seen[one[0]]++;

            port.Dispose();
            connection.Dispose();
        }

        AssertEqual(1, listener.MaxConcurrentAccepts, "more than one accept was in flight at once");

        for (int i = 0; i < howMany; i++)
            AssertEqual(1, seen[i], $"client {i} was accepted the wrong number of times");

        foreach (var c in clients) c.Dispose();
    }

    // -----------------------------------------------------------------------
    // A reset is not an end of input
    // -----------------------------------------------------------------------

    /// <summary>
    /// The case a protocol depends on, and the reason a byte port's failure is
    /// sticky rather than folded into "no more bytes": HTTP/1.1 without a
    /// `Content-Length` ends at end of input, so a connection that was RESET
    /// must not look like one that finished.
    ///
    /// `SO_LINGER` with a timeout of zero is how a reset is produced on purpose,
    /// and it is the reason this test is here rather than in Bjolang — the
    /// language exposes no socket options, deliberately.
    /// </summary>
    private static void ResetIsNotEndOfInput()
    {
        using var listener = Listening();

        // --- the clean half: a shutdown and a close is an end of input -------
        using (var polite = ClientTo(listener))
        {
            var connection = Accept(listener, "the polite connection");
            polite.Shutdown(SocketShutdown.Both);
            polite.Close();

            var port = connection.Input();
            AssertEqual(0, port.ReadInto(new byte[4]), "a clean close was not an end of input");

            port.Dispose();
            connection.Dispose();
        }

        // --- the rude half: linger zero, and the close is a reset ------------
        var rude = ClientTo(listener);
        var reset = Accept(listener, "the rude connection");

        rude.LingerState = new LingerOption(true, 0);
        rude.Close();

        var broken = reset.Input();

        try
        {
            int n = broken.ReadInto(new byte[4]);
            throw new AssertionException(
                $"a reset connection answered {n} rather than failing, which is a clean end of input");
        }
        catch (IOException e) when (e.InnerException is SocketException)
        {
            // As it should: the failure is the port's, and it is sticky.
        }
        catch (SocketException)
        {
            // Some platforms surface it without the IOException wrapper.
        }

        broken.Dispose();
        reset.Dispose();
    }
}
