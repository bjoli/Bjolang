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

using System.Runtime.CompilerServices;
using System.Threading;

namespace Bjoml;

/// <summary>
/// A spin lock with no owner: one compare-exchange to take it, one store to
/// give it back. Not re-entrant — a holder that takes it again spins forever.
///
/// Written <c>using (gate.Hold()) { ... }</c>, which is shaped like the
/// <c>lock</c> statement it stands in for.
/// </summary>
internal sealed class SpinGate
{
    private int _held;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Holder Hold()
    {
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0) Contended();
        return new Holder(this);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Contended()
    {
        var spin = new SpinWait();
        do spin.SpinOnce(sleep1Threshold: -1);
        while (Volatile.Read(ref _held) != 0 || Interlocked.CompareExchange(ref _held, 1, 0) != 0);
    }

    public readonly ref struct Holder
    {
        private readonly SpinGate _gate;

        internal Holder(SpinGate gate) => _gate = gate;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() => Volatile.Write(ref _gate._held, 0);
    }
}
