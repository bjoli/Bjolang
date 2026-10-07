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

// Encodings: finding one by name or code page, and `bytes->string` /
// `string->bytes` - strict by default, with a replacement on request, and the
// offset of what failed in the error.

using System;
using System.Linq;
using System.Text;
using static Bjoml.Tests.Harness;
using Bjolang.Runtime;
using BjoString;

namespace Bjoml.Tests;

public static class EncodingsTests
{
    public static void RunAll()
    {
        Section("Encodings");
        Run("every name finds its encoding, and is its name", NamesRoundTrip);
        Run("names ignore case and take .NET's aliases; unknown is None", NamedLookups);
        Run("a code page finds the same object as the name", CodePages);
        Run("UTF-8 is checked and copied, within a range", Utf8Decode);
        Run("invalid UTF-8 is an error at its offset, or replaced", Utf8Invalid);
        Run("the Unicode encodings agree with .NET's and write no mark", UnicodeRoundTrips);
        Run("a byte another encoding cannot decode is an error at its offset", StrictDecodeOtherEncodings);
        Run("a character an encoding lacks is an error, or replaced", StrictEncode);
        Run("code pages beyond .NET's built-in few exist", CodePagesProvider);
        Run("a port's copy of an encoding decodes leniently", LenientForPorts);
    }

    private static Utf8String U(string s) => Utf8String.FromUtf16(s);

    private static string Error(Action a)
    {
        try { a(); }
        catch (ArgumentException e) { return e.Message; }
        throw new Exception("expected an ArgumentException");
    }

    private static Encoding Some(BjolangRuntime.Option<Encoding> o, string what)
    {
        Assert(o.IsSome, $"{what}: expected an encoding");
        return o.Value;
    }

    private static void NamesRoundTrip()
    {
        var props = typeof(Encodings).GetProperties()
            .Where(p => p.PropertyType == typeof(Encoding) && p.Name.StartsWith("Cp"))
            .ToArray();
        Assert(props.Length == 139, $"139 encodings, got {props.Length}");
        foreach (var p in props)
        {
            var e = (Encoding)p.GetValue(null)!;
            Assert(e.CodePage == int.Parse(p.Name[2..]), $"{p.Name} is code page {e.CodePage}");
            string name = Encodings.NameOf(e).ToString();
            Assert(name == name.ToLowerInvariant(), $"{name} is lower case");
            Assert(ReferenceEquals(Some(Encodings.Named(U(name)), name), e), $"{name} names {p.Name}");
            Assert(ReferenceEquals(p.GetValue(null), e), $"{p.Name} is cached");
        }
    }

    private static void NamedLookups()
    {
        Assert(ReferenceEquals(Some(Encodings.Named(U("Windows-1252")), "Windows-1252"), Encodings.Cp1252), "case");
        Assert(ReferenceEquals(Some(Encodings.Named(U(" UTF-8 ")), "UTF-8"), Encodings.Cp65001), "trimmed");
        Assert(ReferenceEquals(Some(Encodings.Named(U("latin1")), "latin1"), Encodings.Cp28591), "alias latin1");
        Assert(ReferenceEquals(Some(Encodings.Named(U("cp1252")), "cp1252"), Encodings.Cp1252), "alias cp1252");
        Assert(ReferenceEquals(Some(Encodings.Named(U("utf-16")), "utf-16"), Encodings.Cp1200), ".NET's utf-16 is LE");
        Assert(ReferenceEquals(Some(Encodings.Named(U("euc-jp")), "euc-jp"), Encodings.Cp51932), "euc-jp is 51932");
        Assert(Encodings.NameOf(Encodings.Cp20932).ToString() == "cp20932", "the other euc-jp");
        Assert(Encodings.NameOf(Encodings.Cp1201).ToString() == "utf-16be", "utf-16be");
        Assert(Encodings.NameOf(Encodings.Cp12000).ToString() == "utf-32le", "utf-32le");
        Assert(!Encodings.Named(U("no-such-encoding")).IsSome, "unknown");
        Assert(!Encodings.Named(U("utf-7")).IsSome, "UTF-7 is refused");
        Assert(!Encodings.Named(U("")).IsSome, "empty");
    }

    private static void CodePages()
    {
        Assert(ReferenceEquals(Some(Encodings.OfCodePage(862), "862"), Encodings.Cp862), "862");
        Assert(ReferenceEquals(Some(Encodings.OfCodePage(65001), "65001"), Encodings.Cp65001), "65001");
        Assert(!Encodings.OfCodePage(65000).IsSome, "UTF-7");
        Assert(!Encodings.OfCodePage(0).IsSome, "0");
        Assert(!Encodings.OfCodePage(-1).IsSome, "-1");
    }

    private static void Utf8Decode()
    {
        var bytes = Encoding.UTF8.GetBytes("xBlåbär😀y");
        var all = Encodings.Decode(bytes, 0, bytes.Length, Encodings.Cp65001);
        Assert(all.ToString() == "xBlåbär😀y", "whole");
        Assert(Encodings.Decode(bytes, 1, bytes.Length - 1, Encodings.Cp65001).ToString() == "Blåbär😀", "range");
        Assert(Encodings.Decode(bytes, 3, 3, Encodings.Cp65001).ToString() == "", "empty range");
        bytes[1] = (byte)'b';
        Assert(all.ToString() == "xBlåbär😀y", "a copy, not a view");
        Assert(Error(() => Encodings.Decode(bytes, 2, 1, Encodings.Cp65001)).Contains("range 2..1"), "backwards");
        Assert(Error(() => Encodings.Decode(bytes, 0, 99, Encodings.Cp65001)).Contains("range 0..99"), "past the end");
        // A range that cuts a character is invalid at the cut.
        var cut = Error(() => Encodings.Decode(bytes, 0, 4, Encodings.Cp65001));
        Assert(cut.Contains("offset 3 (0xC3)"), cut);
    }

    private static void Utf8Invalid()
    {
        byte[] bytes = [(byte)'a', (byte)'b', 0xFF, (byte)'c', 0xE2, 0x82, (byte)'d'];
        var m = Error(() => Encodings.Decode(bytes, 0, bytes.Length, Encodings.Cp65001));
        Assert(m == "bytes->string: the bytes at offset 2 (0xFF) are not valid utf-8.", m);
        var m2 = Error(() => Encodings.Decode(bytes, 3, bytes.Length, Encodings.Cp65001));
        Assert(m2.Contains("offset 4 (0xE2 0x82)"), m2);
        var q = Encodings.Decode(bytes, 0, bytes.Length, Encodings.Cp65001, new BjoChar('?'));
        Assert(q.ToString() == "ab?c?d", q.ToString());
        var r = Encodings.Decode(bytes, 0, bytes.Length, Encodings.Cp65001, new BjoChar(0xFFFD));
        Assert(r.ToString() == "ab\uFFFDc\uFFFDd", r.ToString());
        var astral = Encodings.Decode(bytes, 0, 3, Encodings.Cp65001, new BjoChar(0x1F600));
        Assert(astral.ToString() == "ab😀", astral.ToString());
        // Any UTF-8 object is UTF-8, whatever fallback it carries.
        var m3 = Error(() => Encodings.Decode(bytes, 0, 3, Encoding.UTF8));
        Assert(m3.Contains("offset 2"), m3);
    }

    private static void UnicodeRoundTrips()
    {
        const string text = "Blåbärssylt 😀 жук\u0000!";
        (Encoding ours, Encoding theirs)[] pairs = [
            (Encodings.Cp1200, new UnicodeEncoding(false, false)),
            (Encodings.Cp1201, new UnicodeEncoding(true, false)),
            (Encodings.Cp12000, new UTF32Encoding(false, false)),
            (Encodings.Cp12001, new UTF32Encoding(true, false)),
        ];
        foreach (var (ours, theirs) in pairs)
        {
            var bytes = Encodings.Encode(U(text), ours);
            Assert(bytes.SequenceEqual(theirs.GetBytes(text)), $"{Encodings.NameOf(ours)} bytes");
            Assert(Encodings.Decode(bytes, 0, bytes.Length, ours).ToString() == text, $"{Encodings.NameOf(ours)} back");
            Assert(ours.GetPreamble().Length == 0, $"{Encodings.NameOf(ours)} has no mark");
        }
        Assert(Encodings.Encode(U(text), Encodings.Cp65001).SequenceEqual(Encoding.UTF8.GetBytes(text)), "utf-8");
        Assert(Encodings.Cp65001.GetPreamble().Length == 0, "utf-8 has no mark");
        // A mark is a character like any other.
        var marked = Encodings.Decode([0xFF, 0xFE, (byte)'a', 0], 0, 4, Encodings.Cp1200);
        Assert(marked.ToString() == "\uFEFFa", "a mark is U+FEFF");
    }

    private static void StrictDecodeOtherEncodings()
    {
        byte[] ascii = [(byte)'a', (byte)'b', 0x80, (byte)'c'];
        var m = Error(() => Encodings.Decode(ascii, 0, 4, Encodings.Cp20127));
        Assert(m == "bytes->string: the bytes at offset 2 (0x80) are not valid us-ascii.", m);
        var m2 = Error(() => Encodings.Decode(ascii, 1, 4, Encodings.Cp20127));
        Assert(m2.Contains("offset 2 (0x80)"), "the offset is in the whole array: " + m2);
        Assert(Encodings.Decode(ascii, 0, 4, Encodings.Cp20127, new BjoChar('*')).ToString() == "ab*c", "replaced");
        // Half a UTF-16 code unit.
        byte[] odd = [(byte)'a', 0, (byte)'b'];
        var m3 = Error(() => Encodings.Decode(odd, 0, 3, Encodings.Cp1200));
        Assert(m3.Contains("offset 2") && m3.Contains("utf-16le"), m3);
        // A lone surrogate in UTF-16.
        byte[] lone = [(byte)'a', 0, 0x00, 0xD8, (byte)'b', 0];
        var m4 = Error(() => Encodings.Decode(lone, 0, 6, Encodings.Cp1200));
        Assert(m4.Contains("offset 2"), m4);
        Assert(Encodings.Decode(lone, 0, 6, Encodings.Cp1200, new BjoChar('?')).ToString() == "a?b", "lone replaced");
        // The object's own fallback does not make the conversion lenient.
        var m5 = Error(() => Encodings.Decode(ascii, 0, 4, Encoding.ASCII));
        Assert(m5.Contains("offset 2"), m5);
    }

    private static void StrictEncode()
    {
        var m = Error(() => Encodings.Encode(U("aé"), Encodings.Cp20127));
        Assert(m == "string->bytes: U+00E9 (é) at offset 1 is not in us-ascii.", m);
        var m2 = Error(() => Encodings.Encode(U("åäö😀"), Encodings.Cp28591));
        Assert(m2 == "string->bytes: U+1F600 (😀) at offset 6 is not in iso-8859-1.", m2);
        var replaced = Encodings.Encode(U("aé😀b"), Encodings.Cp20127, new BjoChar('?'));
        Assert(replaced.SequenceEqual("a??b"u8.ToArray()), "replaced: " + Convert.ToHexString(replaced));
        var m3 = Error(() => Encodings.Encode(U("aé"), Encodings.Cp20127, new BjoChar('ü')));
        Assert(m3 == "string->bytes: the replacement U+00FC (ü) is not in us-ascii either.", m3);
        Assert(Encodings.Encode(U("åäö"), Encodings.Cp28591).SequenceEqual(new byte[] { 0xE5, 0xE4, 0xF6 }), "latin-1");
        Assert(Encodings.Encode(U(""), Encodings.Cp1252).Length == 0, "empty");
        // Long enough for the rented buffer.
        var big = new string('ж', 5000);
        var bytes = Encodings.Encode(U(big), Encodings.Cp20866);
        Assert(bytes.Length == 5000 && Encodings.Decode(bytes, 0, 5000, Encodings.Cp20866).ToString() == big, "koi8-r, long");
    }

    private static void CodePagesProvider()
    {
        var hebrew = Encodings.Encode(U("שלום"), Encodings.Cp862);
        Assert(hebrew.SequenceEqual(new byte[] { 0x99, 0x8C, 0x85, 0x8D }), "dos-862 " + Convert.ToHexString(hebrew));
        Assert(Encodings.Decode(hebrew, 0, 4, Encodings.Cp862).ToString() == "שלום", "dos-862 back");
        Assert(Encodings.Decode([0x80, 0x9F], 0, 2, Encodings.Cp1252).ToString() == "€Ÿ", "windows-1252");
        var sjis = Encodings.Encode(U("日本"), Encodings.Cp932);
        Assert(Encodings.Decode(sjis, 0, sjis.Length, Encodings.Cp932).ToString() == "日本", "shift_jis");
        var m = Error(() => Encodings.Decode([0x41, 0x82], 0, 2, Encodings.Cp932));
        Assert(m.Contains("offset 1") && m.Contains("shift_jis"), m);
    }

    private static void LenientForPorts()
    {
        var lenient = Encodings.Lenient(Encodings.Cp20127);
        Assert(lenient.GetString([(byte)'a', 0x80]) == "a\uFFFD", "U+FFFD");
        Assert(ReferenceEquals(lenient, Encodings.Lenient(Encodings.Cp20127)), "one copy per encoding");
        Assert(Encodings.Cp20127.GetString([0x80]) == "?", "the original is untouched");
    }
}
