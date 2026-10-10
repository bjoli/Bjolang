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

namespace Bjolang.Runtime;

/// <summary>
/// A lock that a holder may keep across an await, with an entry that costs
/// one interlocked instruction when nobody else holds it.
/// </summary>
///
/// <remarks>
/// A <see cref="SemaphoreSlim"/> takes a monitor in both <c>Wait</c> and
/// <c>Release</c>, also when nobody waits: 18 ns per pair, measured, which was
/// most of what an output port's write cost. Here the gate is one int. Only a
/// caller that finds it held goes to the semaphore, which then only wakes
/// waiters.
///
/// A caller that leaves releases a permit when someone waits. A woken waiter
/// tries the int again, and waits again if another caller took the gate
/// first. So a permit is a hint, not a hand-over: a spare permit makes one
/// waiter look once more, and the gate is not strictly first come, first
/// served.
///
/// Why no wakeup is lost: a waiter counts itself in <c>_waiting</c> and then
/// tries the int; a holder frees the int and then reads <c>_waiting</c>. Both
/// steps are interlocked, so either the holder sees the waiter and releases a
/// permit, or the waiter sees the int free and takes it.
/// </remarks>
internal sealed class BjoGate {
    private int _held;
    private int _waiting;
    private readonly SemaphoreSlim _signal = new(0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnter() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enter() {
        if (!TryEnter()) EnterSlow();
    }

    private void EnterSlow() {
        Interlocked.Increment(ref _waiting);
        try {
            while (!TryEnter()) _signal.Wait();
        } finally {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// Enters, or answers false when <paramref name="timeout"/> passes first.
    public bool Enter(TimeSpan timeout) {
        if (TryEnter()) return true;

        var deadline = DateTime.UtcNow + timeout;
        Interlocked.Increment(ref _waiting);
        try {
            while (!TryEnter()) {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !_signal.Wait(left)) return TryEnter();
            }
            return true;
        } finally {
            Interlocked.Decrement(ref _waiting);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask EnterAsync(CancellationToken cancel) =>
        TryEnter() ? default : new ValueTask(EnterSlowAsync(cancel));

    private async Task EnterSlowAsync(CancellationToken cancel) {
        Interlocked.Increment(ref _waiting);
        try {
            while (!TryEnter()) await _signal.WaitAsync(cancel).ConfigureAwait(false);
        } finally {
            Interlocked.Decrement(ref _waiting);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Leave() {
        Interlocked.Exchange(ref _held, 0);
        if (Volatile.Read(ref _waiting) > 0) _signal.Release();
    }
}
