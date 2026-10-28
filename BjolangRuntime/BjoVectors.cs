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
/// Helpers for the vector loops that the compiler writes. See
/// <c>Codegen.emitVectorLoop</c>.
/// </summary>
[NonUserCode]
public static class BjoVectors
{
    /// <summary>
    /// The bits of <paramref name="count"/> elements from
    /// <paramref name="index"/> in a valid bitmap: bit 0 for element
    /// <paramref name="index"/>. All bits are set when the bitmap is empty,
    /// because then no element is None.
    /// </summary>
    /// <remarks>
    /// <paramref name="count"/> is at most 8, so the bits are in two bytes at
    /// most. The caller makes sure that the bitmap holds a bit for each
    /// element.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Bits(ReadOnlySpan<byte> bitmap, int index, int count)
    {
        int all = (1 << count) - 1;
        if (bitmap.Length == 0) return all;
        int at = index >> 3;
        int word = bitmap[at];
        if (at + 1 < bitmap.Length) word |= bitmap[at + 1] << 8;
        return (word >> (index & 7)) & all;
    }

    /// <summary>
    /// A vector with all bits set in the lanes whose bit is set in
    /// <paramref name="bits"/>, and no bits set in the others: bit 0 for lane
    /// 0. A vector and'ed with it keeps the lanes of the bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<T> Lanes<T>(int bits)
    {
        if (Vector256<T>.Count == 4)
        {
            var lanes = Vector256.Create(1L, 2L, 4L, 8L);
            return Vector256.Equals(Vector256.Create((long)bits) & lanes, lanes).As<long, T>();
        }
        else
        {
            var lanes = Vector256.Create(1, 2, 4, 8, 16, 32, 64, 128);
            return Vector256.Equals(Vector256.Create(bits) & lanes, lanes).As<int, T>();
        }
    }
}
