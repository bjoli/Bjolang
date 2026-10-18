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

using System.Buffers.Text;
using System.Runtime.CompilerServices;
using BjoString;

namespace Bjolang.Runtime;

/// <summary>
/// The steps that <c>(std scan)</c>'s macros compile a pattern into.
/// </summary>
///
/// <remarks>
/// <para>
/// A position is a byte offset into the string's UTF-8 bytes, from 0 to the
/// byte length. Each step takes the position it starts at and answers the
/// position after what it read, or -1 when the text there does not match. A
/// step that reads a value answers it with the position, as a value tuple, so
/// nothing is allocated. A string that a step reads is a slice of the line,
/// which shares its bytes.
/// </para>
/// <para>
/// A step never throws on text that does not match. The macro decides what a
/// failure does: <c>scan</c> calls <see cref="Fail{T}"/>, and <c>try-scan</c>
/// makes an <c>Err</c> with <see cref="Message"/>.
/// </para>
/// </remarks>
public static class BjoScan {
    // Whitespace, as a space in a pattern matches it: the ASCII blanks.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsSpace(byte b) => b == (byte)' ' || (uint)(b - 9) <= 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Utf8String Slice(Utf8String s, int from, int to) =>
        new(s.Bytes, s.Start + from, to - from);

    /// <summary>The text of <paramref name="literal"/>, exactly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Lit(Utf8String s, int pos, Utf8String literal) {
        var rest = s.AsSpan()[pos..];
        var want = literal.AsSpan();
        return rest.StartsWith(want) ? pos + want.Length : -1;
    }

    /// <summary>Any amount of whitespace, none included. Never fails.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Ws(Utf8String s, int pos) {
        var b = s.AsSpan();
        while (pos < b.Length && IsSpace(b[pos])) pos++;
        return pos;
    }

    /// <summary>An int: an optional sign and decimal digits. A number too big
    /// for an int does not match.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int, int) Int(Utf8String s, int pos) =>
        Utf8Parser.TryParse(s.AsSpan()[pos..], out int value, out int used)
            ? (value, pos + used)
            : (0, -1);

    /// <summary>A long: an optional sign and decimal digits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (long, int) Long(Utf8String s, int pos) =>
        Utf8Parser.TryParse(s.AsSpan()[pos..], out long value, out int used)
            ? (value, pos + used)
            : (0, -1);

    /// <summary>A double, as <c>string-&gt;double</c> reads one: <c>1.5</c>, <c>-2</c>, <c>3e8</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double, int) Double(Utf8String s, int pos) =>
        Utf8Parser.TryParse(s.AsSpan()[pos..], out double value, out int used)
            ? (value, pos + used)
            : (0.0, -1);

    /// <summary>One character, whatever it is.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (BjoChar, int) Char(Utf8String s, int pos) {
        var b = s.AsSpan();
        if (pos >= b.Length) return (default, -1);
        if (b[pos] < 0x80) return (new BjoChar(b[pos]), pos + 1);
        var r = Utf8Ops.Decode(b, pos, out int width);
        return (BjoChar.FromRune(r), pos + width);
    }

    /// <summary>One or more characters up to whitespace or the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Utf8String, int) Word(Utf8String s, int pos) {
        var b = s.AsSpan();
        int end = pos;
        while (end < b.Length && !IsSpace(b[end])) end++;
        return end == pos ? (default, -1) : (Slice(s, pos, end), end);
    }

    /// <summary>The rest of the line, which may be empty. Never fails.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Utf8String, int) Rest(Utf8String s, int pos) {
        int end = s.ByteLength;
        return (Slice(s, pos, end), end);
    }

    /// <summary>
    /// One or more characters of a set of ASCII characters. The set is a
    /// 128-bit mask in two halves: bit <c>c</c> of <paramref name="low"/> is
    /// character <c>c</c>, and bit <c>c - 64</c> of <paramref name="high"/> is
    /// character <c>c</c> from 64.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Utf8String, int) Chars(Utf8String s, int pos, long low, long high) {
        var b = s.AsSpan();
        int end = pos;
        while (end < b.Length) {
            byte c = b[end];
            bool member = c < 64 ? ((ulong)low >> c & 1) != 0
                        : c < 128 && ((ulong)high >> (c - 64) & 1) != 0;
            if (!member) break;
            end++;
        }
        return end == pos ? (default, -1) : (Slice(s, pos, end), end);
    }

    /// <summary>Whether <paramref name="pos"/> is the end of the line.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool End(Utf8String s, int pos) => pos == s.ByteLength;

    /// <summary>
    /// Why the line does not match: what was expected, at which column, and
    /// the line. The column counts characters from 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Utf8String Message(Utf8String s, int pos, Utf8String expected) {
        int column = Utf8Ops.CountScalars(s.AsSpan()[..pos]) + 1;
        return Utf8String.FromUtf16($"scan: expected {expected} at column {column} of \"{s}\"");
    }

    /// <summary>Raises a <c>FormatException</c> that says why the line does not match.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T Fail<T>(Utf8String s, int pos, Utf8String expected) =>
        throw new FormatException(Message(s, pos, expected).ToString());
}
