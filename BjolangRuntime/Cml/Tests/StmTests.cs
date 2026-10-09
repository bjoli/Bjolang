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
using Bjolang.Runtime;

namespace Bjoml.Tests;

/// <summary>
/// Tests for the transactions of <c>BjoStm.cs</c>. The MCAS has no model
/// checker, so the stress tests here check invariants under real contention:
/// a total that transfers keep, a constraint that write skew would break, and
/// a count of items that a lost wakeup would make short.
/// </summary>
public static class StmTests
{
    public static void RunAll()
    {
        Harness.Section("Transactions (std stm)");

        Harness.Run("transfers keep the total, and every read sees it", TransfersKeepTotal, 30_000);
        Harness.Run("two transactions can not both break a constraint (write skew)", NoWriteSkew, 30_000);
        Harness.Run("or-else undoes the writes of a branch that retries", OrElseUndoes);
        Harness.Run("a transaction that retries waits for a write", RetryWaitsForWrite);
        Harness.Run("a write wakes every waiter", WriteWakesAll);
        Harness.Run("a transaction that loses a choose writes nothing", LoserWritesNothing);
        Harness.Run("an exception is raised by the sync, also after a wait", ExceptionsReachSync);
        Harness.Run("waiters whose sync was lost do not pile up", LostWaitersArePurged);
        Harness.Run("a one-slot buffer passes every item", OneSlotBuffer, 30_000);
        Harness.Run("a log can not be used after its transaction", LogIsClosed);
    }

    private static T Sync<T>(IEvent<T> ev) => global::BjolangRuntime.syncdivblocking(ev);

    private static T Atomically<T>(Func<Tx, T> body) => Sync(StmModule.AtomicallyEvt(body));

    private static void RunThreads(int n, Action<int> body)
    {
        var threads = new Thread[n];
        Exception? failure = null;
        for (int i = 0; i < n; i++)
        {
            int id = i;
            threads[i] = new Thread(() =>
            {
                try { body(id); }
                catch (Exception e) { failure ??= e; }
            }) { IsBackground = true };
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        if (failure is not null) throw failure;
    }

    private static void TransfersKeepTotal()
    {
        const int accounts = 8, initial = 1000, writers = 8, transfers = 20_000;
        var locs = new Loc<int>[accounts];
        for (int i = 0; i < accounts; i++) locs[i] = StmModule.MakeLoc(initial);

        int badSums = 0;
        int done = 0;

        RunThreads(writers + 2, id =>
        {
            if (id >= writers)
            {
                // Readers: a read-only transaction over all accounts.
                while (Volatile.Read(ref done) < writers)
                {
                    int sum = Atomically(tx =>
                    {
                        int s = 0;
                        foreach (var l in locs) s += tx.Get(l);
                        return s;
                    });
                    if (sum != accounts * initial) Interlocked.Increment(ref badSums);
                }
                return;
            }

            var rng = new Random(id);
            for (int k = 0; k < transfers; k++)
            {
                int from = rng.Next(accounts), to = rng.Next(accounts), amount = rng.Next(10);
                Func<Tx, int> body = tx =>
                {
                    tx.Set(locs[from], tx.Get(locs[from]) - amount);
                    tx.Set(locs[to], tx.Get(locs[to]) + amount);
                    return 0;
                };
                // Half through the event, with a SyncState, and half on this
                // thread.
                if ((k & 1) == 0) Atomically(body);
                else Harness.Assert(StmModule.TryAtomically(body).IsSome, "a transfer retried");
            }
            Interlocked.Increment(ref done);
        });

        int total = 0;
        foreach (var l in locs) total += StmModule.Ref(l);
        Harness.AssertEqual(accounts * initial, total, "total after the transfers");
        Harness.AssertEqual(0, badSums, "reads that saw a different total");
    }

    private static void NoWriteSkew()
    {
        const int rounds = 50_000;
        var xs = new Loc<int>[rounds];
        var ys = new Loc<int>[rounds];
        for (int r = 0; r < rounds; r++)
        {
            xs[r] = StmModule.MakeLoc(0);
            ys[r] = StmModule.MakeLoc(0);
        }

        using var start = new Barrier(2);
        RunThreads(2, id =>
        {
            for (int r = 0; r < rounds; r++)
            {
                var x = xs[r];
                var y = ys[r];
                var mine = id == 0 ? x : y;
                start.SignalAndWait();
                StmModule.TryAtomically(tx =>
                {
                    if (tx.Get(x) + tx.Get(y) == 0) tx.Set(mine, 1);
                    return 0;
                });
            }
        });

        for (int r = 0; r < rounds; r++)
            Harness.AssertEqual(1, StmModule.Ref(xs[r]) + StmModule.Ref(ys[r]), $"x + y after round {r}");
    }

    private static void OrElseUndoes()
    {
        var a = StmModule.MakeLoc(0);
        var b = StmModule.MakeLoc(0);

        int got = Atomically(tx => tx.OrElse(
            t => { t.Set(a, 5); t.Set(b, 7); return t.Retry<int>(); },
            t => t.Get(a) * 10 + t.Get(b)));
        Harness.AssertEqual(0, got, "what the second branch read");
        Harness.AssertEqual(0, StmModule.Ref(a), "a after the commit");

        // Nested: the inner or-else succeeds with its second branch, then the
        // outer first branch retries and all of its writes are undone.
        got = Atomically(tx => tx.OrElse(
            t =>
            {
                t.Set(a, 1);
                t.OrElse(u => { u.Set(b, 2); return u.Retry<int>(); },
                         u => { u.Set(b, 3); return 0; });
                return t.Retry<int>();
            },
            t => { t.Set(a, t.Get(a) + 100); return t.Get(b); }));
        Harness.AssertEqual(0, got, "b in the outer second branch");
        Harness.AssertEqual(100, StmModule.Ref(a), "a after the nested commit");
        Harness.AssertEqual(0, StmModule.Ref(b), "b after the nested commit");

        // A first branch that succeeds keeps its writes.
        Atomically(tx => tx.OrElse(t => { t.Set(a, 9); return 0; }, t => t.Retry<int>()));
        Harness.AssertEqual(9, StmModule.Ref(a), "a after a first branch that succeeds");
    }

    private static void RetryWaitsForWrite()
    {
        var flag = StmModule.MakeLoc(false);
        int result = 0;
        var finished = new ManualResetEventSlim(false);

        var waiter = new Thread(() =>
        {
            result = Atomically(tx => tx.Get(flag) ? 42 : tx.Retry<int>());
            finished.Set();
        }) { IsBackground = true };
        waiter.Start();

        Harness.AssertNoSignal(finished, "the transaction committing before the write", 200);
        Harness.Assert(StmModule.TryAtomically(tx => { tx.Set(flag, true); return 0; }).IsSome, "the write retried");
        Harness.Await(finished, "the waiting transaction");
        Harness.AssertEqual(42, result, "result");
    }

    private static void WriteWakesAll()
    {
        const int n = 64;
        var gate = StmModule.MakeLoc(0);
        int woken = 0;
        var started = new CountdownEvent(n);
        var threads = new Thread[n];

        for (int i = 0; i < n; i++)
        {
            threads[i] = new Thread(() =>
            {
                started.Signal();
                Atomically(tx => tx.Get(gate) > 0 ? 0 : tx.Retry<int>());
                Interlocked.Increment(ref woken);
            }) { IsBackground = true };
            threads[i].Start();
        }

        started.Wait();
        Thread.Sleep(100);
        Harness.AssertEqual(0, Volatile.Read(ref woken), "woken before the write");
        StmModule.TryAtomically(tx => { tx.Set(gate, 1); return 0; });
        foreach (var t in threads) Harness.Assert(t.Join(5000), "a waiter did not wake");
        Harness.AssertEqual(n, Volatile.Read(ref woken), "woken");
    }

    private static void LoserWritesNothing()
    {
        var flag = StmModule.MakeLoc(false);
        var side = StmModule.MakeLoc(0);

        int got = Sync(Cml.Choose(
            StmModule.AtomicallyEvt(tx =>
            {
                tx.Set(side, tx.Get(side) + 1);
                return tx.Get(flag) ? 1 : tx.Retry<int>();
            }),
            Cml.Wrap(Cml.Timeout(50), _ => 2)));
        Harness.AssertEqual(2, got, "the branch that won");

        // The write wakes the parked transaction, which must see that its sync
        // is lost and not commit.
        StmModule.TryAtomically(tx => { tx.Set(flag, true); return 0; });
        Thread.Sleep(100);
        Harness.AssertEqual(0, StmModule.Ref(side), "side after the timeout won");
    }

    private static void ExceptionsReachSync()
    {
        var loc = StmModule.MakeLoc(0);

        try
        {
            Atomically<int>(tx => { tx.Set(loc, 1); throw new InvalidOperationException("first"); });
            throw new AssertionException("no exception");
        }
        catch (InvalidOperationException e)
        {
            Harness.AssertEqual("first", e.Message, "message");
        }
        Harness.AssertEqual(0, StmModule.Ref(loc), "loc after the exception");

        // The second run is on a pool thread. The exception must still come
        // out of the sync, on the thread that synced.
        var flag = StmModule.MakeLoc(false);
        Exception? caught = null;
        var done = new ManualResetEventSlim(false);
        var t = new Thread(() =>
        {
            try { Atomically<int>(tx => tx.Get(flag) ? throw new ArgumentException("second") : tx.Retry<int>()); }
            catch (Exception e) { caught = e; }
            done.Set();
        }) { IsBackground = true };
        t.Start();

        Thread.Sleep(50);
        StmModule.TryAtomically(tx => { tx.Set(flag, true); return 0; });
        Harness.Await(done, "the sync to raise");
        Harness.Assert(caught is ArgumentException { Message: "second" }, $"caught {caught}");
    }

    private static void LostWaitersArePurged()
    {
        var never = StmModule.MakeLoc(false);
        for (int i = 0; i < 10_000; i++)
        {
            // The transaction is published first, parks, and then loses to
            // the Always.
            int got = Sync(Cml.Choose(
                StmModule.AtomicallyEvt(tx => tx.Get(never) ? 1 : tx.Retry<int>()),
                Cml.Always(0)));
            Harness.AssertEqual(0, got, "winner");
        }
        Harness.Assert(never.WaiterCount <= 16, $"{never.WaiterCount} waiters on a Loc with no live waiter");
    }

    private static void OneSlotBuffer()
    {
        const int producers = 4, consumers = 4, items = 5_000;
        var full = StmModule.MakeLoc(false);
        var slot = StmModule.MakeLoc(0L);
        long received = 0;
        int receivedCount = 0;

        RunThreads(producers + consumers, id =>
        {
            if (id < producers)
            {
                for (int k = 1; k <= items; k++)
                {
                    long item = k;
                    Atomically(tx =>
                    {
                        if (tx.Get(full)) return tx.Retry<int>();
                        tx.Set(slot, item);
                        tx.Set(full, true);
                        return 0;
                    });
                }
                return;
            }

            for (int k = 0; k < producers * items / consumers; k++)
            {
                long item = Atomically(tx =>
                {
                    if (!tx.Get(full)) return tx.Retry<long>();
                    tx.Set(full, false);
                    return tx.Get(slot);
                });
                Interlocked.Add(ref received, item);
                Interlocked.Increment(ref receivedCount);
            }
        });

        Harness.AssertEqual(producers * items, receivedCount, "items received");
        Harness.AssertEqual((long)producers * items * (items + 1) / 2, received, "sum of the items");
    }

    private static void LogIsClosed()
    {
        var loc = StmModule.MakeLoc(0);
        Tx? kept = null;
        Atomically(tx => { kept = tx; return 0; });
        try
        {
            kept!.Set(loc, 1);
            throw new AssertionException("no exception");
        }
        catch (InvalidOperationException) { }
        Harness.AssertEqual(0, StmModule.Ref(loc), "loc");
    }
}
