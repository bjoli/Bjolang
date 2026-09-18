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

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Bjolang.Runtime;

/// <summary>
/// A position in a string: an offset into that string's storage, pointing at
/// the first unit of a character.
/// </summary>
///
/// <remarks>
/// <para>
/// The offset is <b>opaque</b>, and that is the entire design. Today a string
/// is a C# <c>string</c> and the offset counts UTF-16 code units; the day it
/// becomes UTF-8 the offset counts bytes. No Bjolang program can tell, because
/// none of them can read the number — there is no <c>string-cursor-&gt;index</c>
/// and no way to build a cursor except from a string. A cursor is only ever
/// produced by <see cref="Start"/> or <see cref="End"/> and only ever moved by
/// <see cref="Next"/> or <see cref="Prev"/>, so every value that exists is on a
/// character boundary by construction.
/// </para>
/// <para>
/// That is also why <c>Offset</c> is <c>internal</c>: the opacity is enforced
/// by the assembly boundary, not merely by convention. Generated code lives in
/// another assembly and cannot reach it.
/// </para>
/// <para>
/// Only <see cref="Ref"/> decodes, and it goes through <see cref="Rune"/>.
/// Moving a cursor needs nothing but the width of a character in storage
/// units, and that is readable from the unit the character starts at: in
/// UTF-16 a high surrogate followed by a low one spans two units and
/// everything else spans one, and in UTF-8 the answer is in the leading bits
/// of the first byte. <see cref="WidthAt"/> and <see cref="WidthBefore"/> are
/// the only places that know the encoding's shape, so the migration is a
/// change to them and to <c>DecodeFromUtf16</c> in <see cref="Ref"/>.
/// </para>
/// <para>
/// The widths agree with <c>Rune.DecodeFromUtf16</c> on malformed input,
/// which settles it on .NET's terms: an unpaired surrogate is one unit wide
/// and decodes as U+FFFD, so a traversal always advances and always
/// terminates rather than throwing halfway through a string it did not write.
/// </para>
/// <para>
/// A cursor does not carry the string it indexes, which is why every operation
/// takes both. Pairing them would double the size of the struct to re-check
/// something the <c>Iterable</c> protocol already arranges, and it is the same
/// bargain the other cursors in the runtime strike.
/// </para>
/// </remarks>
// See `BjoChar`: the operators are already here, and the interface is what
// makes them reachable from a constrained generic.
public readonly record struct StringCursor : IComparable<StringCursor>, System.Numerics.IComparisonOperators<StringCursor, StringCursor, bool>
{
    internal int Offset { get; }

    internal StringCursor(int offset) => Offset = offset;

    /// <summary>A cursor on the first character.</summary>
    public static StringCursor Start(string s) => new StringCursor(0);

    /// <summary>
    /// The past-the-end cursor. Not a character position: it is the bound a
    /// traversal stops at, and the only cursor <see cref="AtEnd"/> answers true
    /// for.
    /// </summary>
    public static StringCursor End(string s) => new StringCursor(s.Length);

    public static bool AtEnd(string s, StringCursor c) => c.Offset >= s.Length;

    /// <summary>
    /// How many storage units the character starting at <paramref name="i"/>
    /// occupies. <paramref name="i"/> must be a character boundary inside the
    /// string, which every cursor below the end is.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WidthAt(string s, int i) =>
        char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;

    /// <summary>
    /// How many storage units the character ending at <paramref name="i"/>
    /// occupies, so that a step backwards is a read of the last unit rather
    /// than a rescan from the start. <paramref name="i"/> must be a character
    /// boundary above zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WidthBefore(string s, int i) =>
        char.IsLowSurrogate(s[i - 1]) && i >= 2 && char.IsHighSurrogate(s[i - 2]) ? 2 : 1;

    /// <summary>The character at the cursor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BjoChar Ref(string s, StringCursor c)
    {
        if (c.Offset >= s.Length)
        {
            ThrowPastEnd("string-cursor-ref");
        }
        // A unit that is no part of a pair is already the scalar, and that is
        // the common case; the decode is left for the surrogates it is for.
        char unit = s[c.Offset];
        if (!char.IsSurrogate(unit))
        {
            return new BjoChar(unit);
        }
        Rune.DecodeFromUtf16(s.AsSpan(c.Offset), out Rune rune, out _);
        return new BjoChar((uint)rune.Value);
    }

    /// <summary>The cursor on the next character.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringCursor Next(string s, StringCursor c)
    {
        if (c.Offset >= s.Length)
        {
            ThrowPastEnd("string-cursor-next");
        }
        return new StringCursor(c.Offset + WidthAt(s, c.Offset));
    }

    /// <summary>The cursor on the previous character.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringCursor Prev(string s, StringCursor c)
    {
        if (c.Offset <= 0)
        {
            ThrowBeforeStart();
        }
        return new StringCursor(c.Offset - WidthBefore(s, c.Offset));
    }

    /// <summary>
    /// The character at the cursor, and the cursor after it.
    /// </summary>
    ///
    /// <remarks>
    /// What a traversal asks for, and the reason it is one function: the unit
    /// at the cursor decides both answers, so asking together costs one bounds
    /// check and one read where <see cref="Ref"/> followed by <see cref="Next"/>
    /// costs two of each. The tuple is a <c>ValueTuple</c> and does not
    /// allocate.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (BjoChar, StringCursor) RefNext(string s, StringCursor c)
    {
        if (c.Offset >= s.Length)
        {
            ThrowPastEnd("string-cursor-ref+next");
        }
        char unit = s[c.Offset];
        if (!char.IsSurrogate(unit))
        {
            return (new BjoChar(unit), new StringCursor(c.Offset + 1));
        }
        Rune.DecodeFromUtf16(s.AsSpan(c.Offset), out Rune rune, out int consumed);
        return (new BjoChar((uint)rune.Value), new StringCursor(c.Offset + consumed));
    }

    // The throws are out of line, and not inlined, so that the bodies above
    // stay small enough for the JIT to inline them into the loop that calls
    // them — which is every string traversal a Bjolang program writes.
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPastEnd(string op) =>
        throw new ArgumentOutOfRangeException("c",
            $"{op}: the cursor is at the end of the string. Guard with (string-cursor-end? s c).");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowBeforeStart() =>
        throw new ArgumentOutOfRangeException("c",
            "string-cursor-prev: the cursor is at the start of the string.");

    /// <summary>The text between two cursors, the second exclusive.</summary>
    public static string Substring(string s, StringCursor start, StringCursor end)
    {
        if (start.Offset > end.Offset)
        {
            throw new ArgumentException(
                "substring/cursors: the start cursor is after the end cursor.", nameof(start));
        }
        return s.Substring(start.Offset, end.Offset - start.Offset);
    }

    /// <summary>
    /// How many characters the string holds — a walk, because the answer is not
    /// the storage length in any encoding worth using.
    /// </summary>
    public static int Count(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; n++)
        {
            i += WidthAt(s, i);
        }
        return n;
    }

    // Ordering, so that Bjolang's `<` — which is polymorphic and emits the C#
    // operator — works on cursors without a named comparison of its own.
    // Meaningful only between two cursors on the same string; comparing across
    // strings is nonsense the type system does not catch, exactly as it does
    // not for two indices into different arrays.
    public int CompareTo(StringCursor other) => Offset.CompareTo(other.Offset);

    public static bool operator <(StringCursor a, StringCursor b) => a.Offset < b.Offset;
    public static bool operator >(StringCursor a, StringCursor b) => a.Offset > b.Offset;
    public static bool operator <=(StringCursor a, StringCursor b) => a.Offset <= b.Offset;
    public static bool operator >=(StringCursor a, StringCursor b) => a.Offset >= b.Offset;

    public override string ToString() => $"#<string-cursor {Offset}>";
}
