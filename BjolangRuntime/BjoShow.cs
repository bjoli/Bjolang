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
using System.Globalization;
using BjoString;

namespace Bjolang.Runtime;

/// What `written` and `displayed` need from the runtime: a builder to write a
/// value into, and the written forms of the values whose form is more than
/// their text.
public static class BjoShow
{
    /// A builder that held more than this many bytes is not kept: a thread
    /// keeps its builder for as long as it lives, and one long text would
    /// otherwise hold its buffer that long.
    const int KeptBuilderBytes = 4096;

    [ThreadStatic] static Utf8StringBuilder? kept;

    /// The thread's builder, or a new one when it is in use. It is taken out
    /// while in use, so a `written` called while another writes gets a builder
    /// of its own and not the one being written.
    public static Utf8StringBuilder Rent()
    {
        var b = kept;
        if (b is null) return new Utf8StringBuilder();
        kept = null;
        return b;
    }

    /// The text written into a rented builder. The builder is kept for the
    /// next `Rent` on this thread, unless it grew large.
    public static Utf8String Finish(Utf8StringBuilder b)
    {
        var text = b.ToUtf8String();
        if (b.ByteLength <= KeptBuilderBytes)
        {
            b.Clear();
            kept = b;
        }
        return text;
    }

    // --- Written forms ------------------------------------------------------

    /// A string as the reader reads it: in quotes, with the escapes a string
    /// literal has.
    public static void WriteStringInto(Utf8StringBuilder b, Utf8String s)
    {
        b.AppendByte((byte)'"');
        var bytes = s.AsSpan();
        int from = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            // Only ASCII is escaped, so a byte of a longer UTF-8 sequence is
            // never one of these and the text is copied as it is.
            string? escape = bytes[i] switch
            {
                (byte)'"' => "\\\"",
                (byte)'\\' => "\\\\",
                (byte)'\n' => "\\n",
                (byte)'\t' => "\\t",
                (byte)'\r' => "\\r",
                _ => null,
            };
            if (escape is null) continue;
            b.AppendUtf8(bytes[from..i]);
            b.Append(escape);
            from = i + 1;
        }
        b.AppendUtf8(bytes[from..]);
        b.AppendByte((byte)'"');
    }

    /// A character as the reader reads it: `#\a`, by its name where the
    /// reader has one, and in hexadecimal where it is not printable.
    public static Utf8String WrittenChar(BjoChar c)
    {
        string? name = c.Value switch
        {
            0x20 => "space",
            0x0A => "newline",
            0x09 => "tab",
            0x0D => "return",
            0x00 => "nul",
            0x07 => "alarm",
            0x08 => "backspace",
            0x7F => "delete",
            0x1B => "escape",
            _ => null,
        };
        if (name is not null) return Utf8String.FromUtf16("#\\" + name);
        var rune = new System.Text.Rune(c.Value);
        if (System.Text.Rune.IsControl(rune))
            return Utf8String.FromUtf16("#\\x" + c.Value.ToString("x", CultureInfo.InvariantCulture));
        return Utf8String.FromUtf16("#\\" + rune.ToString());
    }

    /// A double as the reader reads it back as one: with a point or an
    /// exponent, so that `2.0` is not read as the integer 2. Infinities and
    /// NaN, which have no literal, are written as Scheme writes them.
    public static Utf8String WrittenDouble(double d)
    {
        if (double.IsNaN(d)) return Utf8String.FromUtf16("+nan.0");
        if (double.IsPositiveInfinity(d)) return Utf8String.FromUtf16("+inf.0");
        if (double.IsNegativeInfinity(d)) return Utf8String.FromUtf16("-inf.0");
        var text = BjoNum.DoubleToString(d);
        foreach (var b in text.AsSpan())
        {
            if (b == (byte)'.' || b == (byte)'e' || b == (byte)'E') return text;
        }
        return Utf8String.FromUtf16(text.ToString() + ".0");
    }

    /// A float as `WrittenDouble` writes a double. No suffix gives a float its
    /// type, so the caller writes the type around it.
    public static Utf8String WrittenSingle(float f)
    {
        if (float.IsNaN(f)) return Utf8String.FromUtf16("+nan.0");
        if (float.IsPositiveInfinity(f)) return Utf8String.FromUtf16("+inf.0");
        if (float.IsNegativeInfinity(f)) return Utf8String.FromUtf16("-inf.0");
        var text = f.ToString(CultureInfo.InvariantCulture);
        return Utf8String.FromUtf16(text.IndexOfAny(['.', 'e', 'E']) >= 0 ? text : text + ".0");
    }

    /// A number in the invariant culture with the suffix the reader reads
    /// its type from, `21L` or `2.5M`.
    public static Utf8String WrittenNumber<T>(T n, Utf8String suffix) where T : System.Numerics.INumber<T> =>
        Utf8String.FromUtf16(n.ToString(null, CultureInfo.InvariantCulture) + suffix.ToString());
}
