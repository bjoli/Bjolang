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
using System.Diagnostics;
using System.Threading;

namespace Bjoml;

/// <summary>
/// The deadlines of armed timeouts, without a <see cref="Timer"/> each.
///
/// A <see cref="Timer"/> per armed timeout was three objects and a lock on the
/// runtime's timer queue to arm it and another to cancel it, and a `choose`
/// with a deadline arms one every time it parks. Here an armed
/// <see cref="TimeoutNode"/> is itself the entry: linked into a wheel of 1 ms
/// slots, one wheel per core so that arming and cancelling on different cores
/// do not meet, each wheel under its own spin lock. Cancelling unlinks the node
/// at once, so a service whose deadlines nearly all lose keeps no dead entries
/// for a second each.
///
/// One shared 1 ms tick walks every wheel from where it last stopped to now and
/// fires what is due, outside the wheel's lock. It runs only while something is
/// armed. A deadline more than a lap (1024 ms) away stays in its slot until the
/// lap it is due in. Precision is that of the tick, about a millisecond, which
/// is what a <see cref="Timer"/> gave too.
/// </summary>
internal static class TimerWheel
{
    private const int Slots = 1024;
    private const int SlotMask = Slots - 1;

#pragma warning disable CS0169 // the padding is never read
    internal sealed class Wheel
    {
        private long _pad0, _pad1, _pad2, _pad3, _pad4, _pad5, _pad6;

        /// 1 while held. Nothing done under it waits on anything.
        public int Held;

        /// Linked nodes, which is what the tick asks before taking the lock.
        public int Count;

        /// The next millisecond the tick has not walked.
        public long Cursor;

        public readonly TimeoutNode?[] Heads = new TimeoutNode?[Slots];

        private long _pad7, _pad8, _pad9, _pad10, _pad11, _pad12, _pad13;

        public void Enter()
        {
            if (Interlocked.CompareExchange(ref Held, 1, 0) == 0) return;
            var spin = new SpinWait();
            do spin.SpinOnce(sleep1Threshold: -1);
            while (Volatile.Read(ref Held) != 0 || Interlocked.CompareExchange(ref Held, 1, 0) != 0);
        }

        public void Exit() => Volatile.Write(ref Held, 0);
    }
#pragma warning restore CS0169

    private static readonly Wheel[] s_wheels = CreateWheels();
    private static readonly long s_origin = Stopwatch.GetTimestamp();

    private static readonly Timer s_tick =
        new(static _ => Tick(), null, Timeout.Infinite, Timeout.Infinite);

    private static int s_ticking;   // 0 stopped, 1 running

    private static Wheel[] CreateWheels()
    {
        int n = (int)System.Numerics.BitOperations.RoundUpToPowerOf2(
            (uint)Math.Clamp(Environment.ProcessorCount, 1, 64));
        var all = new Wheel[n];
        for (int i = 0; i < n; i++) all[i] = new Wheel();
        return all;
    }

    private static long NowMs() =>
        (Stopwatch.GetTimestamp() - s_origin) * 1000 / Stopwatch.Frequency;

    /// Nodes linked into every wheel. Test-only.
    internal static int LinkedCount
    {
        get
        {
            int n = 0;
            foreach (var w in s_wheels) n += Volatile.Read(ref w.Count);
            return n;
        }
    }

    /// <summary>Link <paramref name="node"/> to fire in <paramref name="ms"/> ms.</summary>
    internal static void Add(TimeoutNode node, int ms)
    {
        long now = NowMs();
        var w = s_wheels[Thread.GetCurrentProcessorId() & (s_wheels.Length - 1)];

        w.Enter();
        if (w.Count == 0) w.Cursor = now;
        long due = now + ms;
        // A clock read on this core a moment behind the tick's: the slot it
        // names may have been walked already, and would not come round for a
        // lap. The cursor's slot is the earliest that will still be walked.
        if (due < w.Cursor) due = w.Cursor;
        node.Due = due;
        node.Wheel = w;
        ref var head = ref w.Heads[(int)(due & SlotMask)];
        node.WheelNext = head;
        node.WheelPrev = null;
        if (head is not null) head.WheelPrev = node;
        head = node;
        w.Count++;
        w.Exit();

        // The count above, then whether the tick is running: against the
        // tick's stop, then its look at every count (see Tick).
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref s_ticking) == 0) StartTicking();
    }

    /// <summary>Unlink <paramref name="node"/> if it is still linked.</summary>
    internal static void Remove(TimeoutNode node)
    {
        var w = Volatile.Read(ref node.Wheel);
        if (w is null) return;

        w.Enter();
        if (ReferenceEquals(node.Wheel, w)) Unlink(w, node);
        w.Exit();
    }

    private static void Unlink(Wheel w, TimeoutNode node)
    {
        if (node.WheelPrev is { } prev) prev.WheelNext = node.WheelNext;
        else w.Heads[(int)(node.Due & SlotMask)] = node.WheelNext;
        if (node.WheelNext is { } next) next.WheelPrev = node.WheelPrev;
        node.WheelPrev = node.WheelNext = null;
        Volatile.Write(ref node.Wheel, null);
        w.Count--;
    }

    private static void StartTicking()
    {
        if (Interlocked.CompareExchange(ref s_ticking, 1, 0) != 0) return;
        s_tick.Change(1, 1);
    }

    private static void Tick()
    {
        if (FireDue()) return;

        // Nothing linked anywhere: stop, then look once more. An Add that saw
        // the tick running did not start it; the exchange orders this stop
        // before the look, and Add's fence orders its count before its read of
        // this flag, so one of the two sees the other.
        s_tick.Change(Timeout.Infinite, Timeout.Infinite);
        Interlocked.Exchange(ref s_ticking, 0);
        foreach (var w in s_wheels)
        {
            if (Volatile.Read(ref w.Count) != 0)
            {
                StartTicking();
                return;
            }
        }
    }

    /// Fire what is due on every wheel; answer whether anything is still linked.
    private static bool FireDue()
    {
        long now = NowMs();
        bool linked = false;

        foreach (var w in s_wheels)
        {
            if (Volatile.Read(ref w.Count) == 0) continue;

            TimeoutNode? due = null;
            w.Enter();
            long from = w.Cursor;
            long to = now - from >= Slots ? from + Slots - 1 : now;
            for (long t = from; t <= to; t++)
            {
                var node = w.Heads[(int)(t & SlotMask)];
                while (node is not null)
                {
                    var next = node.WheelNext;
                    if (node.Due <= now)
                    {
                        Unlink(w, node);
                        node.FireNext = due;
                        due = node;
                    }
                    node = next;
                }
            }
            if (now >= w.Cursor) w.Cursor = now + 1;
            linked |= w.Count != 0;
            w.Exit();

            while (due is not null)
            {
                var next = due.FireNext;
                due.FireNext = null;
                due.FireFromWheel();
                due = next;
            }
        }

        return linked;
    }
}
