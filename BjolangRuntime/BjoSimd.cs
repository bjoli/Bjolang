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
using System.Runtime.Intrinsics;

namespace Bjolang.Runtime;

/// <summary>
/// What <c>(std simd)</c> needs from <c>Vector128&lt;T&gt;</c> and
/// <c>Vector256&lt;T&gt;</c> that <c>import/extern</c> cannot reach.
/// </summary>
///
/// <remarks>
/// <c>Count</c> and <c>Zero</c> are static properties of the generic struct,
/// not methods of the static <c>Vector128</c> and <c>Vector256</c> classes.
/// An import can not name a static member of a generic type, so these
/// methods read them. Each one is inlined, and <c>Count</c> is a constant to
/// the JIT.
/// </remarks>
public static class BjoSimd {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> Zero128<T>() => Vector128<T>.Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> Zero256<T>() => Vector256<T>.Zero;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Lanes128<T>(Vector128<T> _) => Vector128<T>.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Lanes256<T>(Vector256<T> _) => Vector256<T>.Count;

    // A mask is what a comparison answers: every bit of a lane is set where
    // the comparison holds, and none where it does not. So the top bit of a
    // lane says it, and `ExtractMostSignificantBits` has one bit per lane.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Any128<T>(Vector128<T> mask) => mask.ExtractMostSignificantBits() != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Any256<T>(Vector256<T> mask) => mask.ExtractMostSignificantBits() != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool All128<T>(Vector128<T> mask) =>
        mask.ExtractMostSignificantBits() == (uint)((1UL << Vector128<T>.Count) - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool All256<T>(Vector256<T> mask) =>
        mask.ExtractMostSignificantBits() == (uint)((1UL << Vector256<T>.Count) - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CountTrue128<T>(Vector128<T> mask) =>
        System.Numerics.BitOperations.PopCount(mask.ExtractMostSignificantBits());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CountTrue256<T>(Vector256<T> mask) =>
        System.Numerics.BitOperations.PopCount(mask.ExtractMostSignificantBits());

    /// <summary>The first lane of the mask that is set, or -1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FirstTrue128<T>(Vector128<T> mask) {
        uint bits = mask.ExtractMostSignificantBits();
        return bits == 0 ? -1 : System.Numerics.BitOperations.TrailingZeroCount(bits);
    }

    /// <summary>The first lane of the mask that is set, or -1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FirstTrue256<T>(Vector256<T> mask) {
        uint bits = mask.ExtractMostSignificantBits();
        return bits == 0 ? -1 : System.Numerics.BitOperations.TrailingZeroCount(bits);
    }

    /// <summary>How many elements of an array one 128-bit vector holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ArrayLanes128<T>(T[] _) => Vector128<T>.Count;

    /// <summary>How many elements of an array one 256-bit vector holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ArrayLanes256<T>(T[] _) => Vector256<T>.Count;
}
