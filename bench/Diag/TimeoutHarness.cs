// Focused measurement of the ARMED timeout path: publish a timeout branch into a
// sync block, then let another branch win, which fires the nack and cancels the
// timer. This is the loop body of every select-with-deadline in a real server —
// the deadline is armed and then almost always loses.
//
// The varied suite's Choose+timeout row only lands on this path stochastically
// (whenever the receive branch is not ready at publish time), which is why it
// looked bimodal. This harness forces the armed path deterministically by
// driving the SyncState protocol directly: publish, claim, mark synchronized
// with a foreign winner.
//
// Two rows: the direct TimeoutEvent, and the combinator formulation it replaced
// (Guard -> WithNack -> Promise -> nested Sync -> Wrap), retained in Cml as the
// executable specification.

using System;
using System.Diagnostics;
using System.Threading;
using Bjoml;

namespace Bjoml.Diag;

public static class TimeoutHarness
{
    public static void Run(int reps)
    {
        Console.WriteLine($".NET {Environment.Version}  ProcessorCount={Environment.ProcessorCount}  " +
                          $"ServerGC={System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine($"reps={reps}   (arm + lose + cancel, per op)");
        Console.WriteLine();

        Measure("TimeoutEvent (direct)", reps, Cml.Timeout(1000));
        Measure("Combinator spec      ", reps, Cml.TimeoutViaCombinators(1000));

        // ---- end-to-end: the varied suite's Choose+timeout, armed EVERY op ----
        //
        // The suite row is bimodal because arming depends on whether the receive
        // branch is ready at publish time. Here the sender waits until the
        // receiver's op is actually parked (visible via the internal
        // PendingReceiveCount) before sending, and the timeout branch is
        // published FIRST, so every single iteration arms a timer and then loses.
        // This is the honest steady-state shape of a real select-with-deadline
        // loop, and the number to put against Hopac's flat 268 and Go's 456,
        // both of which arm every iteration by construction.
        Console.WriteLine();
        Console.WriteLine("end-to-end choose(timeout, recv), timer armed every op:");
        MeasureArmed("TimeoutEvent (direct)", reps, combinators: false);
        MeasureArmed("Combinator spec      ", reps, combinators: true);
    }

    static void MeasureArmed(string name, int reps, bool combinators)
    {
        ArmedOnce(combinators, 10_000, out _);   // warm up

        var samples = new double[reps];
        double alloc = 0;
        for (int r = 0; r < reps; r++) samples[r] = ArmedOnce(combinators, 50_000, out alloc);

        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);

        Console.WriteLine($"{name} per rep: {string.Join(" ", Array.ConvertAll(samples, s => $"{s,6:F0}"))}");
        Console.WriteLine($"                      median : {sorted[reps / 2]:F0} ns/op   ({alloc:F0} B/op)");
    }

    static double ArmedOnce(bool combinators, int rounds, out double bytesPerOp)
    {
        var ch = new Channel<int>();
        var tmo = combinators ? Cml.TimeoutViaCombinators(1000) : Cml.Timeout(1000);

        // Timeout branch first: by the time the receive branch parks (which is
        // what releases the sender), the timer is already armed.
        var choose = Cml.Choose(Cml.Wrap(tmo, static _ => -1), (IEvent<int>)ch);

        var sender = Bjo.Spawn(() => ArmedSender(ch, rounds));

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        Bjo.Spawn(() => ArmedReceiver(choose, rounds)).ToTask().GetAwaiter().GetResult();
        sender.ToTask().GetAwaiter().GetResult();

        sw.Stop();

        Thread.Sleep(150);   // let the enqueued nack cancels finish allocating
        bytesPerOp = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)rounds;

        return sw.Elapsed.TotalMilliseconds * 1e6 / rounds;
    }

    static async Fiber ArmedReceiver(IEvent<int> choose, int rounds)
    {
        for (int i = 0; i < rounds; i++) await choose;
    }

    static async Fiber ArmedSender(Channel<int> ch, int rounds)
    {
        for (int i = 0; i < rounds; i++)
        {
            // Do not send until the receiver is genuinely parked, so the choose
            // can never commit inline during publish and skip arming the timer.
            while (ch.PendingReceiveCount == 0) Thread.SpinWait(20);
            await ch.Send(i);
        }
    }

    static void Measure(string name, int reps, IEvent<Unit> tmo)
    {
        Once(tmo, 10_000, out _);          // warm up

        var samples = new double[reps];
        double alloc = 0;
        for (int r = 0; r < reps; r++) samples[r] = Once(tmo, 50_000, out alloc);

        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);

        Console.WriteLine($"{name} per rep: {string.Join(" ", Array.ConvertAll(samples, s => $"{s,6:F0}"))}");
        Console.WriteLine($"                      median : {sorted[reps / 2]:F0} ns/op   ({alloc:F0} B/op)");
    }

    static double Once(IEvent<Unit> tmo, int rounds, out double bytesPerOp)
    {
        var sink = new Action<Unit>(static _ => { });

        long before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < rounds; i++)
        {
            var state = new SyncState();
            int id = state.NextEventId();
            tmo.Publish(state, id, sink);

            // Another branch wins: claim and commit with a winner outside the
            // timeout's [id, id+1) interval, which fires its nack.
            state.TryClaim();
            state.MarkSynchronized(id + 1);
        }

        sw.Stop();

        // Cancels are dispatched asynchronously (nacks are enqueued); give them a
        // moment to finish so their allocations land inside the measurement.
        Thread.Sleep(150);
        bytesPerOp = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)rounds;

        return sw.Elapsed.TotalMilliseconds * 1e6 / rounds;
    }
}
