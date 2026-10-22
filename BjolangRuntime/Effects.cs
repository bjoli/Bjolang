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

public static partial class BjolangRuntime {

    /// <summary>
    /// The prelude's `system-clock` reads `monotonic-ms` here: milliseconds on
    /// a monotonic clock.
    ///
    /// Monotonic rather than wall-clock, because what reads it is
    /// `std/stopwatch`, and a wall clock can step backwards over an NTP
    /// adjustment or a leap second — which would make an elapsed time negative.
    ///
    /// The origin is arbitrary and unspecified: only differences mean anything.
    /// </summary>
    public static long ClockMs() =>
        System.Diagnostics.Stopwatch.GetTimestamp() / TicksPerMs;

    /// Fixed at startup, so the division below is against a constant the JIT
    /// can see. At least 1, since a frequency under a kilohertz would make this
    /// zero and the division throw.
    private static readonly long TicksPerMs =
        System.Math.Max(1L, System.Diagnostics.Stopwatch.Frequency / 1000L);

    /// <summary>
    /// The prelude's `system-clock` reads `monotonic-ns` here: nanoseconds on
    /// the same monotonic clock as `ClockMs`, at the resolution the timer has.
    /// The origin is arbitrary: only differences mean anything.
    /// </summary>
    /// <remarks>
    /// On Linux the timer counts nanoseconds, and its reading is returned as it
    /// is. A timer with another frequency is converted in 128 bits, because the
    /// reading times a billion overflows a long.
    /// </remarks>
    public static long ClockNs() {
        var ticks = System.Diagnostics.Stopwatch.GetTimestamp();
        if (TimerCountsNs) return ticks;
        return (long)((System.Int128)ticks * 1_000_000_000L / System.Diagnostics.Stopwatch.Frequency);
    }

    private static readonly bool TimerCountsNs =
        System.Diagnostics.Stopwatch.Frequency == 1_000_000_000L;
}

namespace Bjolang.Runtime {

    /// <summary>
    /// An effect operation was performed with no handler installed and no
    /// default to fall back on.
    ///
    /// Raised by the operation's default handler, which `defeffect` writes in
    /// Bjolang; nothing in the runtime throws this. The type lives here because
    /// `#:catch` names .NET types, and a program that wants to answer for a
    /// missing handler has to be able to name one.
    /// </summary>
    [NonUserCode]
    public sealed class Unhandled : System.Exception {

        /// <summary>The operation's name, as it was written in `defeffect`.</summary>
        public readonly string Operation;

        public Unhandled(string operation)
            : base($"no handler for the effect operation '{operation}'") =>
            Operation = operation;
    }
}
