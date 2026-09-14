/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

namespace Bjolang.Runtime;

using System.Runtime.CompilerServices;
using System.Text;

/// <summary>
/// A small cache from a scratch buffer's contents to an interned name.
///
/// A reader that interns every name it reads pays twice per name: a string
/// allocated off the buffer, and then a <see cref="ConcurrentDictionary"/>
/// probe inside <c>Intern</c>. A document repeats its names — a manifest of
/// twenty thousand records writes the same five keys twenty thousand times —
/// so almost every one of those allocations is of a string that already exists.
///
/// This holds the last name seen in each slot and compares the buffer against
/// it without building a string, which <see cref="StringBuilder.Equals"/> over
/// a span does in one pass. A hit costs that comparison and nothing else. A
/// miss costs what interning always cost, plus the failed comparison.
///
/// Direct-mapped rather than associative: a probe is one comparison, and two
/// names colliding costs a miss rather than a wrong answer. Sized for the
/// number of distinct names a record has, not the number of records.
///
/// Not thread-safe, and not meant to be: a cache belongs to one reader, and a
/// reader reads one document.
/// </summary>
public sealed class BjoNameCache {
    private const int Size = 16;
    private const int Mask = Size - 1;

    private readonly string?[] _names = new string?[Size];

    /// The first character and the length. Cheap, and enough to separate the
    /// names of one record: `version` and `commit` differ by length, `git` and
    /// `name` by both.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Slot(StringBuilder sb) {
        int n = sb.Length;
        if (n == 0) return 0;
        return ((sb[0] * 31) ^ n) & Mask;
    }

    /// The buffer's contents as a string, reusing the one already handed out
    /// for those same characters.
    ///
    /// This stops short of answering the interned `Keyword` itself, which would
    /// save the probe inside `Intern` as well. A .NET method may not *return* a
    /// `Keyword` to Bjolang: `DotNetInterop.clrToNullary` maps reflected types
    /// back to builtin ones for `char` and deliberately nothing else, so the
    /// return would come back as `BjolangRuntime+Symbol` and unify with
    /// nothing. Widening that table is a compiler decision with a trade-off of
    /// its own, so this saves the allocation and leaves the probe.
    public string Name(StringBuilder sb) {
        int i = Slot(sb);
        string? name = _names[i];
        if (name is not null && sb.Equals(name.AsSpan())) return name;

        string s = sb.ToString();
        _names[i] = s;
        return s;
    }
}
