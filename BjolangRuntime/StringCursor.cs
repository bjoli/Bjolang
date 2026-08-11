using System;
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
/// All decoding goes through <see cref="Rune"/>, which is what makes the
/// migration a one-word change: <c>DecodeFromUtf16</c> becomes
/// <c>DecodeFromUtf8</c> and every routine below is otherwise untouched. It
/// also settles malformed input on .NET's terms — an unpaired surrogate decodes
/// as U+FFFD and consumes one unit, so a traversal always advances and always
/// terminates rather than throwing halfway through a string it did not write.
/// </para>
/// <para>
/// A cursor does not carry the string it indexes, which is why every operation
/// takes both. Pairing them would double the size of the struct to re-check
/// something the <c>Iterable</c> protocol already arranges, and it is the same
/// bargain the other cursors in the runtime strike.
/// </para>
/// </remarks>
public readonly record struct StringCursor : IComparable<StringCursor>
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

    /// <summary>The character at the cursor.</summary>
    public static BjoChar Ref(string s, StringCursor c)
    {
        if (c.Offset >= s.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(c),
                "string-cursor-ref: the cursor is at the end of the string. Guard with (string-cursor-end? s c).");
        }
        Rune.DecodeFromUtf16(s.AsSpan(c.Offset), out Rune rune, out _);
        return new BjoChar((uint)rune.Value);
    }

    /// <summary>The cursor on the next character.</summary>
    public static StringCursor Next(string s, StringCursor c)
    {
        if (c.Offset >= s.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(c),
                "string-cursor-next: the cursor is at the end of the string. Guard with (string-cursor-end? s c).");
        }
        Rune.DecodeFromUtf16(s.AsSpan(c.Offset), out _, out int consumed);
        return new StringCursor(c.Offset + consumed);
    }

    /// <summary>
    /// The cursor on the previous character. Decoding backwards is what makes
    /// this O(1) rather than a rescan from the start.
    /// </summary>
    public static StringCursor Prev(string s, StringCursor c)
    {
        if (c.Offset <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(c),
                "string-cursor-prev: the cursor is at the start of the string.");
        }
        Rune.DecodeLastFromUtf16(s.AsSpan(0, c.Offset), out _, out int consumed);
        return new StringCursor(c.Offset - consumed);
    }

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
            Rune.DecodeFromUtf16(s.AsSpan(i), out _, out int consumed);
            i += consumed;
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
