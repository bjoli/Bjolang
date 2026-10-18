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

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Bjolang.Runtime;

/// <summary>
/// Whole-array kernels over numbers, for <c>(std array)</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// Each kernel uses <c>Vector&lt;T&gt;</c> when the hardware accelerates it and
/// <c>T</c> is a primitive number. A vector holds as many lanes as the CPU
/// register has room for: 4 doubles or 8 ints with AVX2. The elements that do
/// not fill a whole vector go through the scalar loop at the end. Any other
/// <c>INumber</c> type, such as <c>BigInteger</c> or <c>decimal</c>, goes
/// through the scalar loop only.
/// </para>
/// <para>
/// The JIT compiles one copy of each kernel per value type, so the test of
/// <c>Vector&lt;T&gt;.IsSupported</c> is a constant and the dead branch goes.
/// </para>
/// <para>
/// A sum and a dot product add lane by lane, and add the lanes together at the
/// end. At an integer type the result is the same as a left fold, because
/// integer addition wraps and is associative. At <c>double</c> the order of
/// the additions is different, so the last bits of the result can differ from
/// a left fold.
/// </para>
/// <para>
/// Comparisons use the operators of <c>T</c>, as Bjolang's <c>=</c> and
/// <c>min</c> do. So NaN equals nothing, and <c>-0.0</c> equals <c>0.0</c>.
/// <c>Equals</c> would say otherwise for both.
/// </para>
/// </remarks>
public static class BjoArray {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Vectorised<T>(int length) =>
        Vector.IsHardwareAccelerated && Vector<T>.IsSupported && length >= Vector<T>.Count;

    static void CheckSameLength<T>(string who, T[] a, T[] b) {
        if (a.Length != b.Length)
            throw new ArgumentException($"{who}: the arrays have lengths {a.Length} and {b.Length}, but they must have the same length.");
    }

    static void CheckNotEmpty<T>(string who, T[] a) {
        if (a.Length == 0)
            throw new InvalidOperationException($"{who}: the array is empty, so there is no element to start from.");
    }

    /// <summary>The sum of the elements. Zero for an empty array.</summary>
    public static T Sum<T>(T[] a) where T : INumber<T> {
        T acc = T.Zero;
        int i = 0;
        if (Vectorised<T>(a.Length)) {
            int w = Vector<T>.Count;
            ref T r = ref MemoryArrayRef(a);
            // Four accumulators, so that one addition does not wait for the
            // one before it to finish.
            var s0 = Vector<T>.Zero;
            var s1 = Vector<T>.Zero;
            var s2 = Vector<T>.Zero;
            var s3 = Vector<T>.Zero;
            for (; i <= a.Length - 4 * w; i += 4 * w) {
                s0 += Vector.LoadUnsafe(ref r, (nuint)i);
                s1 += Vector.LoadUnsafe(ref r, (nuint)(i + w));
                s2 += Vector.LoadUnsafe(ref r, (nuint)(i + 2 * w));
                s3 += Vector.LoadUnsafe(ref r, (nuint)(i + 3 * w));
            }
            for (; i <= a.Length - w; i += w)
                s0 += Vector.LoadUnsafe(ref r, (nuint)i);
            acc = Vector.Sum((s0 + s1) + (s2 + s3));
        }
        for (; i < a.Length; i++)
            acc += a[i];
        return acc;
    }

    /// <summary>The sum of the products of the elements at the same index.</summary>
    public static T Dot<T>(T[] a, T[] b) where T : INumber<T> {
        CheckSameLength("array-dot", a, b);
        T acc = T.Zero;
        int i = 0;
        if (Vectorised<T>(a.Length)) {
            int w = Vector<T>.Count;
            ref T ra = ref MemoryArrayRef(a);
            ref T rb = ref MemoryArrayRef(b);
            var s0 = Vector<T>.Zero;
            var s1 = Vector<T>.Zero;
            var s2 = Vector<T>.Zero;
            var s3 = Vector<T>.Zero;
            for (; i <= a.Length - 4 * w; i += 4 * w) {
                s0 += Vector.LoadUnsafe(ref ra, (nuint)i) * Vector.LoadUnsafe(ref rb, (nuint)i);
                s1 += Vector.LoadUnsafe(ref ra, (nuint)(i + w)) * Vector.LoadUnsafe(ref rb, (nuint)(i + w));
                s2 += Vector.LoadUnsafe(ref ra, (nuint)(i + 2 * w)) * Vector.LoadUnsafe(ref rb, (nuint)(i + 2 * w));
                s3 += Vector.LoadUnsafe(ref ra, (nuint)(i + 3 * w)) * Vector.LoadUnsafe(ref rb, (nuint)(i + 3 * w));
            }
            for (; i <= a.Length - w; i += w)
                s0 += Vector.LoadUnsafe(ref ra, (nuint)i) * Vector.LoadUnsafe(ref rb, (nuint)i);
            acc = Vector.Sum((s0 + s1) + (s2 + s3));
        }
        for (; i < a.Length; i++)
            acc += a[i] * b[i];
        return acc;
    }

    /// <summary>The least element, by <c>T.Min</c>. Throws for an empty array.</summary>
    public static T Min<T>(T[] a) where T : INumber<T> {
        CheckNotEmpty("array-min", a);
        T acc = a[0];
        int i = 1;
        if (Vectorised<T>(a.Length)) {
            int w = Vector<T>.Count;
            ref T r = ref MemoryArrayRef(a);
            var m = Vector.LoadUnsafe(ref r);
            for (i = w; i <= a.Length - w; i += w)
                m = Vector.Min(m, Vector.LoadUnsafe(ref r, (nuint)i));
            acc = m[0];
            for (int j = 1; j < w; j++)
                acc = T.Min(acc, m[j]);
        }
        for (; i < a.Length; i++)
            acc = T.Min(acc, a[i]);
        return acc;
    }

    /// <summary>The greatest element, by <c>T.Max</c>. Throws for an empty array.</summary>
    public static T Max<T>(T[] a) where T : INumber<T> {
        CheckNotEmpty("array-max", a);
        T acc = a[0];
        int i = 1;
        if (Vectorised<T>(a.Length)) {
            int w = Vector<T>.Count;
            ref T r = ref MemoryArrayRef(a);
            var m = Vector.LoadUnsafe(ref r);
            for (i = w; i <= a.Length - w; i += w)
                m = Vector.Max(m, Vector.LoadUnsafe(ref r, (nuint)i));
            acc = m[0];
            for (int j = 1; j < w; j++)
                acc = T.Max(acc, m[j]);
        }
        for (; i < a.Length; i++)
            acc = T.Max(acc, a[i]);
        return acc;
    }

    /// <summary>Adds each element of <paramref name="src"/> to the element of
    /// <paramref name="dst"/> at the same index.</summary>
    public static void AddInPlace<T>(T[] dst, T[] src) where T : INumber<T> {
        CheckSameLength("array-add!", dst, src);
        int i = 0;
        if (Vectorised<T>(dst.Length)) {
            int w = Vector<T>.Count;
            ref T rd = ref MemoryArrayRef(dst);
            ref T rs = ref MemoryArrayRef(src);
            for (; i <= dst.Length - w; i += w)
                (Vector.LoadUnsafe(ref rd, (nuint)i) + Vector.LoadUnsafe(ref rs, (nuint)i)).StoreUnsafe(ref rd, (nuint)i);
        }
        for (; i < dst.Length; i++)
            dst[i] += src[i];
    }

    /// <summary>Multiplies each element by <paramref name="k"/>.</summary>
    public static void ScaleInPlace<T>(T[] a, T k) where T : INumber<T> {
        int i = 0;
        if (Vectorised<T>(a.Length)) {
            int w = Vector<T>.Count;
            ref T r = ref MemoryArrayRef(a);
            var vk = new Vector<T>(k);
            for (; i <= a.Length - w; i += w)
                (Vector.LoadUnsafe(ref r, (nuint)i) * vk).StoreUnsafe(ref r, (nuint)i);
        }
        for (; i < a.Length; i++)
            a[i] *= k;
    }

    /// <summary>How many elements are <c>==</c> to <paramref name="x"/>.</summary>
    public static int Count<T>(T[] a, T x) where T : INumber<T> {
        int n = 0;
        int i = 0;
        if (Vectorised<T>(a.Length)) {
            int w = Vector<T>.Count;
            ref T r = ref MemoryArrayRef(a);
            var vx = new Vector<T>(x);
            for (; i <= a.Length - w; i += w) {
                var eq = Vector.Equals(Vector.LoadUnsafe(ref r, (nuint)i), vx);
                // A lane that matched has every bit set. The mask has one bit
                // per lane, so its population count is the number of matches.
                n += BitOperations.PopCount(MaskBits(eq));
            }
        }
        for (; i < a.Length; i++)
            if (a[i] == x)
                n++;
        return n;
    }

    /// <summary>The index of the first element <c>==</c> to <paramref name="x"/>,
    /// or -1.</summary>
    public static int IndexOf<T>(T[] a, T x) where T : INumber<T> {
        int i = 0;
        if (Vectorised<T>(a.Length)) {
            int w = Vector<T>.Count;
            ref T r = ref MemoryArrayRef(a);
            var vx = new Vector<T>(x);
            for (; i <= a.Length - w; i += w) {
                var eq = Vector.Equals(Vector.LoadUnsafe(ref r, (nuint)i), vx);
                if (eq != Vector<T>.Zero)
                    return i + BitOperations.TrailingZeroCount(MaskBits(eq));
            }
        }
        for (; i < a.Length; i++)
            if (a[i] == x)
                return i;
        return -1;
    }

    /// <summary>Writes <paramref name="x"/> to every element.</summary>
    public static void Fill<T>(T[] a, T x) => Array.Fill(a, x);

    // One bit per lane, from the lane's top bit. `Vector<T>` has no such
    // method, so the vector is read as the fixed-size type of its width. The
    // width is a constant to the JIT, so two of the branches go.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong MaskBits<T>(Vector<T> v) {
        if (Vector<byte>.Count == 64) return v.AsVector512().ExtractMostSignificantBits();
        if (Vector<byte>.Count == 32) return v.AsVector256().ExtractMostSignificantBits();
        return v.AsVector128().ExtractMostSignificantBits();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ref T MemoryArrayRef<T>(T[] a) => ref MemoryMarshal.GetArrayDataReference(a);
}
