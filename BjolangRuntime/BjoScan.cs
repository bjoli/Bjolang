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

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// step that reads a number or a character answers it with the position, as
/// a value tuple, so nothing is allocated. A step that reads text answers
/// only the position, and <see cref="Slice"/> makes the text from the line's
/// bytes when the pattern keeps it.
/// </para>
/// <para>
/// A step that takes a width reads no more than that many characters, and
/// reads to the end of the line when the width is negative.
/// </para>
/// <para>
/// A step never throws on text that does not match. The macro decides what a
/// failure does: <c>scan</c> calls <see cref="Fail{T}"/>, and <c>try-scan</c>
/// makes an <c>Err</c> with <see cref="Message"/>.
/// </para>
/// </remarks>
[NonUserCode]
public static class BjoScan {
    // Whitespace, as a space in a pattern matches it: the ASCII blanks.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsSpace(byte b) => b == (byte)' ' || (uint)(b - 9) <= 4;

    /// <summary>The text of <paramref name="literal"/>, exactly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Lit(Utf8String s, int pos, Utf8String literal) {
        var rest = s.AsSpan()[pos..];
        var want = literal.AsSpan();
        return rest.StartsWith(want) ? pos + want.Length : -1;
    }

    /// <summary>One ASCII byte, exactly. What a one-character literal compiles to.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Byte(Utf8String s, int pos, int b) {
        var span = s.AsSpan();
        return (uint)pos < (uint)span.Length && span[pos] == b ? pos + 1 : -1;
    }

    /// <summary>
    /// A literal of 2 to 8 bytes, exactly. <paramref name="bytes"/> holds the
    /// literal's bytes in little-endian order, so that one 64-bit read and
    /// compare matches it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Lit8(Utf8String s, int pos, long bytes, int length) {
        var b = s.AsSpan();
        int left = b.Length - pos;
        if (left < length) return -1;
        ref byte at = ref Unsafe.Add(ref MemoryMarshal.GetReference(b), pos);
        ulong v;
        if (left >= 8) {
            v = BinaryPrimitives.ReadUInt64LittleEndian(MemoryMarshal.CreateReadOnlySpan(ref at, 8));
        } else {
            v = 0;
            for (int i = 0; i < length; i++) v |= (ulong)Unsafe.Add(ref at, i) << (8 * i);
        }
        ulong mask = length == 8 ? ulong.MaxValue : (1UL << (8 * length)) - 1;
        return (v & mask) == (ulong)bytes ? pos + length : -1;
    }

    /// <summary>Any amount of whitespace, none included. Never fails.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Ws(Utf8String s, int pos) {
        var b = s.AsSpan();
        while (pos < b.Length && IsSpace(b[pos])) pos++;
        return pos;
    }

    // The bytes of the line that a step with a width can read: all of them
    // when the width is negative, else no more than `width` characters from
    // `pos`. The macro passes a constant width, so after inlining the test
    // goes away.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ReadOnlySpan<byte> Within(Utf8String s, int pos, int width) {
        var b = s.AsSpan();
        return width < 0 ? b : b[..WidthEnd(b, pos, width)];
    }

    static int WidthEnd(ReadOnlySpan<byte> b, int pos, int width) {
        int i = pos;
        for (int n = 0; n < width && i < b.Length; n++) {
            byte c = b[i];
            i += c < 0x80 ? 1 : c >= 0xF0 ? 4 : c >= 0xE0 ? 3 : 2;
        }
        return i;
    }

    /// <summary>The text from <paramref name="from"/> to <paramref name="to"/>,
    /// which shares the line's bytes. What a text item's value is.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Utf8String Slice(Utf8String s, int from, int to) {
        // A line is most often all of its array, which one exact type test
        // finds, where `Bytes` and `Start` would each test for a part.
        var data = s.Data;
        if (Utf8String.IsWhole(data)) return new(Unsafe.As<byte[]>(data), from, to - from);
        return new(s.Bytes, s.Start + from, to - from);
    }

    /// <summary>An int: an optional sign and decimal digits, in at most
    /// <paramref name="width"/> characters when it is not negative. A number
    /// too big for an int does not match.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int, int) Int(Utf8String s, int pos, int width) {
        var (magnitude, negative, end) = Digits(Within(s, pos, width), pos, int.MaxValue);
        if (end < 0) return (0, -1);
        return ((int)(negative ? 0UL - magnitude : magnitude), end);
    }

    /// <summary>A long, as <see cref="Int"/> reads an int.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (long, int) Long(Utf8String s, int pos, int width) {
        var (magnitude, negative, end) = Digits(Within(s, pos, width), pos, long.MaxValue);
        if (end < 0) return (0, -1);
        return ((long)(negative ? 0UL - magnitude : magnitude), end);
    }

    // An optional sign and one or more decimal digits, as a magnitude of at
    // most `max`, or `max + 1` when the sign is a minus. The end is -1 when
    // there is no digit or the magnitude is too big.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static (ulong, bool, int) Digits(ReadOnlySpan<byte> b, int pos, ulong max) {
        int i = pos;
        bool negative = false;
        if ((uint)i < (uint)b.Length) {
            byte c = b[i];
            if (c == (byte)'-') { negative = true; i++; }
            else if (c == (byte)'+') i++;
        }
        int first = i;
        ulong acc = 0;
        while ((uint)i < (uint)b.Length) {
            uint d = (uint)(b[i] - '0');
            if (d > 9) break;
            acc = acc * 10 + d;
            i++;
        }
        // Up to 19 digits cannot wrap a ulong, so the check comes after.
        if (i - first > 19) return LongDigits(b, first, max, negative);
        ulong limit = negative ? max + 1 : max;
        return i == first || acc > limit
            ? (0, false, -1)
            : (acc, negative, i);
    }

    // More than 19 digits: too big, unless most of them are leading zeros.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (ulong, bool, int) LongDigits(ReadOnlySpan<byte> b, int first, ulong max, bool negative) {
        ulong limit = negative ? max + 1 : max;
        ulong acc = 0;
        int i = first;
        while ((uint)i < (uint)b.Length) {
            uint d = (uint)(b[i] - '0');
            if (d > 9) break;
            if (acc > (limit - d) / 10) return (0, false, -1);
            acc = acc * 10 + d;
            i++;
        }
        return (acc, negative, i);
    }

    /// <summary>A double, as <c>string-&gt;double</c> reads one: <c>1.5</c>,
    /// <c>-2</c>, <c>3e8</c>. A width is as for <see cref="Int"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (double, int) Double(Utf8String s, int pos, int width) =>
        Utf8Parser.TryParse(Within(s, pos, width)[pos..], out double value, out int used)
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

    // The text steps below answer only the position after the text, and
    // `Slice` makes the value. An item whose value is not kept therefore
    // allocates nothing.

    /// <summary>One or more characters up to whitespace or the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Word(Utf8String s, int pos, int width) {
        var b = Within(s, pos, width);
        int end = pos;
        while (end < b.Length && !IsSpace(b[end])) end++;
        return end == pos ? -1 : end;
    }

    /// <summary>The rest of the line, which may be empty. Never fails.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Rest(Utf8String s, int pos, int width) =>
        width < 0 ? s.ByteLength : WidthEnd(s.AsSpan(), pos, width);

    // Whether an ASCII set holds a byte. The set is a 128-bit mask in two
    // halves: bit c of `low` is character c, and bit c - 64 of `high` is
    // character c from 64. A byte from 128 up is in no set.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool InSet(byte c, long low, long high) =>
        c < 64 ? ((ulong)low >> c & 1) != 0
               : c < 128 && ((ulong)high >> (c - 64) & 1) != 0;

    /// <summary>One or more characters of an ASCII set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Chars(Utf8String s, int pos, long low, long high, int width) {
        var b = Within(s, pos, width);
        int end = pos;
        while (end < b.Length && InSet(b[end], low, high)) end++;
        return end == pos ? -1 : end;
    }

    /// <summary>Any characters up to one of an ASCII set or the end, none
    /// included. Never fails. A non-ASCII character is never in the set, so
    /// the end is never inside one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Until(Utf8String s, int pos, long low, long high, int width) {
        var b = Within(s, pos, width);
        int end = pos;
        while (end < b.Length && !InSet(b[end], low, high)) end++;
        return end;
    }

    /// <summary>Any characters up to one ASCII byte or the end, none
    /// included. Never fails. A vectorized search, which is what
    /// <see cref="Until"/> with one character compiles to.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int UntilByte(Utf8String s, int pos, int stop, int width) {
        var b = Within(s, pos, width);
        int found = b[pos..].IndexOf((byte)stop);
        return found < 0 ? b.Length : pos + found;
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
