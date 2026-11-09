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
using System.Runtime.ExceptionServices;

namespace Bjolang.Runtime;

/// <summary>
/// The sort under the <c>-unstable</c> sorts of <c>std/eq</c>, and the stable
/// sort of <c>mutablevec-sort-by!</c>. The stable sorts by <c>Ord</c> are
/// written in Bjolang, in <c>array-sort!</c>.
/// </summary>
[NonUserCode]
public static class SortModule
{
    /// <summary>
    /// Sorts <paramref name="items"/> in place with .NET's introsort. Not
    /// stable: equal elements can change places.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>Array.Sort</c> wraps an exception from the comparer in an
    /// <c>InvalidOperationException</c>. It is raised again here as itself, so
    /// that the caller can catch what <c>compare</c> raised.</item>
    /// <item>When <c>compare</c> raises, the contents of the array are
    /// unspecified: an element can be lost or be there twice.</item>
    /// </list>
    /// </remarks>
    public static void SortUnstable<T>(T[] items, IComparer<T> order)
    {
        try
        {
            Array.Sort(items, order);
        }
        catch (InvalidOperationException e) when (e.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Throw(inner);
        }
    }

    /// <summary>A stable sort in place, in the order of <paramref name="order"/>.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Insertion sort over runs of 16, then bottom-up merges between the
    /// items and one buffer. A merge takes from the left run on a tie, which
    /// keeps the sort stable.</item>
    /// <item>Generic over the comparer, so that a struct comparer is called
    /// directly.</item>
    /// </list>
    /// </remarks>
    internal static void StableSort<T, TOrder>(Span<T> items, TOrder order) where TOrder : IComparer<T>
    {
        const int run = 16;
        int n = items.Length;

        for (int lo = 0; lo < n; lo += run)
        {
            int hi = Math.Min(lo + run, n);
            for (int i = lo + 1; i < hi; i++)
            {
                T x = items[i];
                int j = i - 1;
                while (j >= lo && order.Compare(items[j], x) > 0)
                {
                    items[j + 1] = items[j];
                    j--;
                }
                items[j + 1] = x;
            }
        }

        if (n <= run) return;

        var buffer = new T[n];
        Span<T> src = items;
        Span<T> dst = buffer;
        bool inBuffer = false;

        for (int width = run; width < n; width *= 2)
        {
            for (int lo = 0; lo < n; lo += 2 * width)
            {
                int mid = Math.Min(lo + width, n);
                int hi = Math.Min(lo + 2 * width, n);
                int l = lo, r = mid, k = lo;
                while (l < mid && r < hi)
                    dst[k++] = order.Compare(src[l], src[r]) <= 0 ? src[l++] : src[r++];
                while (l < mid) dst[k++] = src[l++];
                while (r < hi) dst[k++] = src[r++];
            }

            Span<T> swap = src;
            src = dst;
            dst = swap;
            inBuffer = !inBuffer;
        }

        if (inBuffer) src.CopyTo(items);
    }
}

/// <summary>A comparison function as a comparer, for <see cref="SortModule.StableSort{T, TOrder}"/>.</summary>
[NonUserCode]
internal readonly struct FunctionOrder<T>(Func<T, T, int> compare) : IComparer<T>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(T? x, T? y) => compare(x!, y!);
}
