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

using System.Runtime.ExceptionServices;

namespace Bjolang.Runtime;

/// <summary>
/// The sort under the <c>-unstable</c> sorts of <c>std/eq</c>. The stable
/// sorts are written in Bjolang, in <c>array-sort!</c> and
/// <c>array-sort-by!</c>.
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
}
