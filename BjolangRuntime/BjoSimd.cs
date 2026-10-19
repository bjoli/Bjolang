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
using System.Runtime.InteropServices;
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

    /// <summary>One bit per lane, the lowest for lane 0: set where the mask is.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MaskBits128<T>(Vector128<T> mask) => (int)mask.ExtractMostSignificantBits();

    /// <summary>One bit per lane, the lowest for lane 0. Bit 31 is the sign of the int.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MaskBits256<T>(Vector256<T> mask) => (int)mask.ExtractMostSignificantBits();

    // The shifts are operators of the generic struct, which an import can not
    // name. `>>` is arithmetic for a signed element type and logical for an
    // unsigned one, as it is on a scalar. `>>>` is always logical.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> ShiftLeft128<T>(Vector128<T> v, int n) => v << n;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> ShiftRight128<T>(Vector128<T> v, int n) => v >> n;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> ShiftRightLogical128<T>(Vector128<T> v, int n) => v >>> n;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> ShiftLeft256<T>(Vector256<T> v, int n) => v << n;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> ShiftRight256<T>(Vector256<T> v, int n) => v >> n;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> ShiftRightLogical256<T>(Vector256<T> v, int n) => v >>> n;

    // The float operations whose .NET methods are one overload per element
    // type, not one generic method. The impl that calls them is generic, so
    // these are too. The test of `T` is a constant to the JIT, which keeps
    // only the branch for the type it compiles.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> Floor128<T>(Vector128<T> v) =>
        typeof(T) == typeof(double) ? Vector128.Floor(v.AsDouble()).As<double, T>() : Vector128.Floor(v.AsSingle()).As<float, T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> Floor256<T>(Vector256<T> v) =>
        typeof(T) == typeof(double) ? Vector256.Floor(v.AsDouble()).As<double, T>() : Vector256.Floor(v.AsSingle()).As<float, T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> Ceiling128<T>(Vector128<T> v) =>
        typeof(T) == typeof(double) ? Vector128.Ceiling(v.AsDouble()).As<double, T>() : Vector128.Ceiling(v.AsSingle()).As<float, T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> Ceiling256<T>(Vector256<T> v) =>
        typeof(T) == typeof(double) ? Vector256.Ceiling(v.AsDouble()).As<double, T>() : Vector256.Ceiling(v.AsSingle()).As<float, T>();

    /// <summary>To the nearest integer, and to the even one from halfway.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> Round128<T>(Vector128<T> v) =>
        typeof(T) == typeof(double) ? Vector128.Round(v.AsDouble()).As<double, T>() : Vector128.Round(v.AsSingle()).As<float, T>();

    /// <summary>To the nearest integer, and to the even one from halfway.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> Round256<T>(Vector256<T> v) =>
        typeof(T) == typeof(double) ? Vector256.Round(v.AsDouble()).As<double, T>() : Vector256.Round(v.AsSingle()).As<float, T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> Truncate128<T>(Vector128<T> v) =>
        typeof(T) == typeof(double) ? Vector128.Truncate(v.AsDouble()).As<double, T>() : Vector128.Truncate(v.AsSingle()).As<float, T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> Truncate256<T>(Vector256<T> v) =>
        typeof(T) == typeof(double) ? Vector256.Truncate(v.AsDouble()).As<double, T>() : Vector256.Truncate(v.AsSingle()).As<float, T>();

    /// <summary><c>a * b + c</c>, rounded once.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> MulAdd128<T>(Vector128<T> a, Vector128<T> b, Vector128<T> c) =>
        typeof(T) == typeof(double)
            ? Vector128.FusedMultiplyAdd(a.AsDouble(), b.AsDouble(), c.AsDouble()).As<double, T>()
            : Vector128.FusedMultiplyAdd(a.AsSingle(), b.AsSingle(), c.AsSingle()).As<float, T>();

    /// <summary><c>a * b + c</c>, rounded once.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> MulAdd256<T>(Vector256<T> a, Vector256<T> b, Vector256<T> c) =>
        typeof(T) == typeof(double)
            ? Vector256.FusedMultiplyAdd(a.AsDouble(), b.AsDouble(), c.AsDouble()).As<double, T>()
            : Vector256.FusedMultiplyAdd(a.AsSingle(), b.AsSingle(), c.AsSingle()).As<float, T>();

    // Loads from a list. Each takes a span of exactly a vector,
    // so the slice is the range check: it throws
    // `ArgumentOutOfRangeException` when fewer elements are left, as the
    // array load does.

    /// <summary>A 128-bit vector of a list's elements, from an index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<T> LoadList128<T>(List<T> l, int i) =>
        Vector128.Create((ReadOnlySpan<T>)CollectionsMarshal.AsSpan(l).Slice(i, Vector128<T>.Count));

    /// <summary>A 256-bit vector of a list's elements, from an index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> LoadList256<T>(List<T> l, int i) =>
        Vector256.Create((ReadOnlySpan<T>)CollectionsMarshal.AsSpan(l).Slice(i, Vector256<T>.Count));

    /// <summary>How many elements of a list one 128-bit vector holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ListLanes128<T>(List<T> _) => Vector128<T>.Count;

    /// <summary>How many elements of a list one 256-bit vector holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ListLanes256<T>(List<T> _) => Vector256<T>.Count;

    /// <summary>How many elements of an array one 128-bit vector holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ArrayLanes128<T>(T[] _) => Vector128<T>.Count;

    /// <summary>How many elements of an array one 256-bit vector holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ArrayLanes256<T>(T[] _) => Vector256<T>.Count;
}
