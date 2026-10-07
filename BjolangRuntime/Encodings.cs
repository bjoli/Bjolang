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

// Encodings: the values `(text encodings)` exports, finding one by name or
// code page, and the conversions between bytes and strings.
//
// An encoding is a .NET `Encoding` object and nothing more. What happens to
// bytes that do not decode, or characters that do not encode, is decided by
// the operation and not by the object: `bytes->string` and `string->bytes`
// are strict unless given a replacement, as Racket's conversions are, and a
// port decodes leniently, as Racket's ports do. Whatever fallback the object
// carries is replaced by a copy with the fallback the operation wants.
//
// A string is UTF-8 already, so UTF-8 is a check and a copy in either
// direction. Every other encoding goes through UTF-16, which is what .NET's
// encoders speak.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;
using BjoString;

namespace Bjolang.Runtime;

public static class Encodings {
    private const int Utf8CodePage = 65001;

    // The code pages beyond the handful built into .NET (windows-1252,
    // dos-862, shift_jis and the rest) exist only once this provider is
    // registered. Every encoding the language hands out comes through this
    // class, so registering here, the first time one is asked for, is enough.
    //
    // The lookups are built here rather than by field initializers, which run
    // in the order they are written, before the table at the end of the class.
    static Encodings() {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        s_codePageByName = new Dictionary<string, int>(s_table.Length, StringComparer.OrdinalIgnoreCase);
        s_nameByCodePage = new Dictionary<int, string>(s_table.Length);
        foreach (var (name, cp) in s_table) {
            s_codePageByName.Add(name, cp);
            s_nameByCodePage.Add(cp, name);
        }
    }

    // The Unicode encodings, without a byte order mark: a .NET writer over
    // `Encoding.UTF8` or `Encoding.Unicode` starts with one, and nothing in
    // the language writes or skips marks.
    private static readonly Encoding s_utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding s_utf16le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
    private static readonly Encoding s_utf16be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
    private static readonly Encoding s_utf32le = new UTF32Encoding(bigEndian: false, byteOrderMark: false);
    private static readonly Encoding s_utf32be = new UTF32Encoding(bigEndian: true, byteOrderMark: false);

    private static readonly Dictionary<string, int> s_codePageByName;
    private static readonly Dictionary<int, string> s_nameByCodePage;

    // One instance per code page, so that the property, `encoding-named` and
    // `encoding-of-code-page` all hand out the same object.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Encoding> s_instances = new();

    private static Encoding Make(int codePage) => codePage switch {
        Utf8CodePage => s_utf8,
        1200 => s_utf16le,
        1201 => s_utf16be,
        12000 => s_utf32le,
        12001 => s_utf32be,
        _ => Encoding.GetEncoding(codePage),
    };

    private static Encoding OfCodePageOrThrow(int codePage) => s_instances.GetOrAdd(codePage, Make);

    // --- Finding one --------------------------------------------------------

    /// <summary>
    /// `encoding-named`: the encoding a name means, ignoring case. The names
    /// `(text encodings)` exports come first; then every name and alias .NET
    /// knows (`latin1`, `cp1252`, `Shift-JIS`), which is what a charset
    /// declared by a web page or a mail header needs. None for a name nothing
    /// knows, and for UTF-7, which .NET refuses to provide.
    /// </summary>
    public static BjolangRuntime.Option<Encoding> Named(Utf8String name) {
        string n = name.ToString().Trim();
        if (s_codePageByName.TryGetValue(n, out int cp)) return BjolangRuntime.Some(OfCodePageOrThrow(cp));
        try {
            var e = Encoding.GetEncoding(n);
            return BjolangRuntime.Some(s_nameByCodePage.ContainsKey(e.CodePage) ? OfCodePageOrThrow(e.CodePage) : e);
        } catch (ArgumentException) {
            return BjolangRuntime.None<Encoding>();
        } catch (NotSupportedException) {
            return BjolangRuntime.None<Encoding>();
        }
    }

    /// <summary>`encoding-of-code-page`: the encoding of a Windows code page number.</summary>
    public static BjolangRuntime.Option<Encoding> OfCodePage(int codePage) =>
        s_nameByCodePage.ContainsKey(codePage)
            ? BjolangRuntime.Some(OfCodePageOrThrow(codePage))
            : BjolangRuntime.None<Encoding>();

    /// <summary>
    /// `encoding-name`: the name `(text encodings)` exports the encoding
    /// under, or .NET's name for it, lower-cased, for one made elsewhere.
    /// </summary>
    public static Utf8String NameOf(Encoding encoding) {
        ArgumentNullException.ThrowIfNull(encoding);
        return Utf8String.FromUtf16(
            s_nameByCodePage.TryGetValue(encoding.CodePage, out var name) ? name : encoding.WebName.ToLowerInvariant());
    }

    // --- The same encoding, with another fallback ----------------------------
    //
    // A copy of an encoding with the fallbacks an operation wants. Copies are
    // kept per object: a code page encoding loads its tables once, and a copy
    // made per call would make each conversion pay for that again.

    private static readonly ConditionalWeakTable<Encoding, Encoding> s_strict = new();
    private static readonly ConditionalWeakTable<Encoding, Encoding> s_lenient = new();
    private static readonly ConditionalWeakTable<Encoding, System.Collections.Concurrent.ConcurrentDictionary<int, Encoding>> s_replacing = new();

    private static Encoding WithFallbacks(Encoding encoding, EncoderFallback encoder, DecoderFallback decoder) {
        var copy = (Encoding)encoding.Clone();
        copy.EncoderFallback = encoder;
        copy.DecoderFallback = decoder;
        return copy;
    }

    /// <summary>The encoding, throwing on what does not decode or encode.</summary>
    public static Encoding Strict(Encoding encoding) =>
        s_strict.GetValue(encoding, e => WithFallbacks(e, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback));

    /// <summary>
    /// The encoding as a port decodes with: what does not decode becomes
    /// U+FFFD. Encoding keeps the object's own fallback.
    /// </summary>
    public static Encoding Lenient(Encoding encoding) =>
        s_lenient.GetValue(encoding, e => WithFallbacks(e, e.EncoderFallback, new DecoderReplacementFallback("\uFFFD")));

    /// <summary>The encoding, putting <paramref name="replacement"/> where something does not decode or encode.</summary>
    private static Encoding Replacing(Encoding encoding, BjoChar replacement) {
        var byChar = s_replacing.GetValue(encoding, _ => new());
        return byChar.GetOrAdd((int)replacement.Value, (r, e) => {
            string s = new Rune(r).ToString();
            return WithFallbacks(e, new ScalarReplacementFallback(s), new DecoderReplacementFallback(s));
        }, encoding);
    }

    /// <summary>
    /// The replacement once per character that does not encode. .NET's
    /// <see cref="EncoderReplacementFallback"/> writes it once per UTF-16
    /// unit, which is twice for a character outside the Basic Multilingual
    /// Plane.
    /// </summary>
    private sealed class ScalarReplacementFallback(string replacement) : EncoderFallback {
        public override int MaxCharCount => replacement.Length;
        public override EncoderFallbackBuffer CreateFallbackBuffer() => new Buffer(replacement);

        private sealed class Buffer(string replacement) : EncoderFallbackBuffer {
            private int _next = replacement.Length;

            public override int Remaining => replacement.Length - _next;

            public override bool Fallback(char charUnknown, int index) {
                _next = 0;
                return true;
            }

            public override bool Fallback(char charUnknownHigh, char charUnknownLow, int index) {
                _next = 0;
                return true;
            }

            public override char GetNextChar() => _next < replacement.Length ? replacement[_next++] : '\0';

            public override bool MovePrevious() {
                if (_next == 0) return false;
                _next--;
                return true;
            }

            public override void Reset() => _next = replacement.Length;
        }
    }

    // --- bytes->string --------------------------------------------------------

    private static ReadOnlySpan<byte> Range(byte[] bytes, int start, int end) {
        ArgumentNullException.ThrowIfNull(bytes);
        if (start < 0 || end > bytes.Length || start > end) {
            throw new ArgumentOutOfRangeException(nameof(start),
                $"bytes->string: the range {start}..{end} is not within the {bytes.Length} bytes.");
        }
        return bytes.AsSpan(start, end - start);
    }

    /// <summary>
    /// `bytes->string`: the bytes from <paramref name="start"/> to
    /// <paramref name="end"/>, decoded strictly. What does not decode is an
    /// error naming its offset.
    /// </summary>
    public static Utf8String Decode(byte[] bytes, int start, int end, Encoding encoding) {
        ArgumentNullException.ThrowIfNull(encoding);
        var span = Range(bytes, start, end);
        if (encoding.CodePage == Utf8CodePage) {
            if (Utf8String.TryFromUtf8(span, out var s)) return s;
            var (at, length) = FirstInvalidUtf8(span);
            throw Undecodable(span.Slice(at, length), start + at, encoding);
        }
        try {
            return FromChars(Strict(encoding), span);
        } catch (DecoderFallbackException e) {
            int at = FailureOffset(Strict(encoding), span);
            var unknown = e.BytesUnknown is { Length: > 0 } b ? b : span.Slice(at, Math.Min(1, span.Length - at)).ToArray();
            throw Undecodable(unknown, start + at, encoding, e);
        }
    }

    /// <summary>
    /// Where the bytes that fail to decode begin: just past the last byte that
    /// came out as characters. The exception's own index cannot be trusted
    /// for this: for a lone UTF-16 surrogate it points at the character after.
    /// Only reached on failure, so feeding the decoder a byte at a time costs
    /// nothing that matters.
    /// </summary>
    private static int FailureOffset(Encoding strict, ReadOnlySpan<byte> bytes) {
        var decoder = strict.GetDecoder();
        Span<char> chars = stackalloc char[16];
        int decoded = 0;
        for (int i = 0; i < bytes.Length; i++) {
            int n;
            try {
                n = decoder.GetChars(bytes.Slice(i, 1), chars, flush: i == bytes.Length - 1);
            } catch (DecoderFallbackException) {
                return decoded;
            }
            if (n > 0) decoded = i + 1;
        }
        return decoded;
    }

    /// <summary>
    /// `bytes->string` with `#:replace`: what does not decode becomes
    /// <paramref name="replacement"/>, once per invalid sequence.
    /// </summary>
    public static Utf8String Decode(byte[] bytes, int start, int end, Encoding encoding, BjoChar replacement) {
        ArgumentNullException.ThrowIfNull(encoding);
        var span = Range(bytes, start, end);
        if (encoding.CodePage == Utf8CodePage) {
            if (Utf8String.TryFromUtf8(span, out var s)) return s;
            if (replacement.Value == 0xFFFD) return Utf8String.FromUtf8Lossy(span);
        }
        return FromChars(Replacing(encoding, replacement), span);
    }

    private static Utf8String FromChars(Encoding encoding, ReadOnlySpan<byte> bytes) {
        int count = encoding.GetCharCount(bytes);
        char[]? rented = null;
        Span<char> chars = count <= 256 ? stackalloc char[256] : (rented = ArrayPool<char>.Shared.Rent(count));
        try {
            int n = encoding.GetChars(bytes, chars);
            return Utf8String.FromUtf16(chars[..n]);
        } finally {
            if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Where the first invalid sequence starts, and how many bytes it is.</summary>
    private static (int At, int Length) FirstInvalidUtf8(ReadOnlySpan<byte> bytes) {
        int at = 0;
        while (at < bytes.Length) {
            if (Rune.DecodeFromUtf8(bytes[at..], out _, out int used) != OperationStatus.Done) return (at, used);
            at += used;
        }
        return (bytes.Length, 0);
    }

    private static ArgumentException Undecodable(ReadOnlySpan<byte> unknown, int at, Encoding encoding, Exception? inner = null) =>
        new($"bytes->string: the bytes at offset {at} ({Hex(unknown)}) are not valid {NameOf(encoding)}.", inner);

    private static string Hex(ReadOnlySpan<byte> bytes) {
        var sb = new StringBuilder(bytes.Length * 5);
        foreach (byte b in bytes) {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("0x").Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    // --- string->bytes --------------------------------------------------------

    /// <summary>
    /// `string->bytes`: the string encoded strictly. A character the encoding
    /// has no bytes for is an error naming it and its offset.
    /// </summary>
    public static byte[] Encode(Utf8String s, Encoding encoding) {
        ArgumentNullException.ThrowIfNull(encoding);
        if (encoding.CodePage == Utf8CodePage) return s.AsSpan().ToArray();
        return WithChars(s, encoding, static (chars, e) => {
            try {
                return ToBytes(Strict(e), chars);
            } catch (EncoderFallbackException x) {
                throw Unencodable(chars, x, e);
            }
        });
    }

    /// <summary>
    /// `string->bytes` with `#:replace`: a character the encoding has no
    /// bytes for becomes <paramref name="replacement"/>'s, which must have
    /// some.
    /// </summary>
    public static byte[] Encode(Utf8String s, Encoding encoding, BjoChar replacement) {
        ArgumentNullException.ThrowIfNull(encoding);
        if (encoding.CodePage == Utf8CodePage) return s.AsSpan().ToArray();
        string r = replacement.ToRune().ToString();
        try {
            Strict(encoding).GetByteCount(r);
        } catch (EncoderFallbackException) {
            throw new ArgumentException(
                $"string->bytes: the replacement {Describe(replacement.ToRune())} is not in {NameOf(encoding)} either.");
        }
        var replacing = Replacing(encoding, replacement);
        return WithChars(s, replacing, static (chars, e) => ToBytes(e, chars));
    }

    private static byte[] ToBytes(Encoding encoding, ReadOnlySpan<char> chars) {
        var bytes = new byte[encoding.GetByteCount(chars)];
        encoding.GetBytes(chars, bytes);
        return bytes;
    }

    private delegate byte[] CharsFunc(ReadOnlySpan<char> chars, Encoding encoding);

    private static byte[] WithChars(Utf8String s, Encoding encoding, CharsFunc f) {
        var utf8 = s.AsSpan();
        // A UTF-8 byte is never more than one UTF-16 unit.
        char[]? rented = null;
        Span<char> chars = utf8.Length <= 256 ? stackalloc char[256] : (rented = ArrayPool<char>.Shared.Rent(utf8.Length));
        try {
            Utf8.ToUtf16(utf8, chars, out _, out int n);
            return f(chars[..n], encoding);
        } finally {
            if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static ArgumentException Unencodable(ReadOnlySpan<char> chars, EncoderFallbackException e, Encoding encoding) {
        var rune = e.IsUnknownSurrogate() ? new Rune(e.CharUnknownHigh, e.CharUnknownLow) : new Rune(e.CharUnknown);
        int at = Encoding.UTF8.GetByteCount(chars[..Math.Clamp(e.Index, 0, chars.Length)]);
        return new($"string->bytes: {Describe(rune)} at offset {at} is not in {NameOf(encoding)}.", e);
    }

    private static string Describe(Rune r) => $"U+{r.Value:X4} ({r})";

    // --- Generated: one entry and one property per encoding -----------------

    private static readonly (string Name, int CodePage)[] s_table = [
        ("asmo-708", 708),
        ("big5", 950),
        ("cp1025", 21025),
        ("cp20932", 20932),
        ("cp50222", 50222),
        ("cp866", 866),
        ("cp875", 875),
        ("csiso2022jp", 50221),
        ("dos-720", 720),
        ("dos-862", 862),
        ("euc-cn", 51936),
        ("euc-jp", 51932),
        ("euc-kr", 51949),
        ("gb18030", 54936),
        ("gb2312", 936),
        ("hz-gb-2312", 52936),
        ("ibm-thai", 20838),
        ("ibm00858", 858),
        ("ibm00924", 20924),
        ("ibm01047", 1047),
        ("ibm01140", 1140),
        ("ibm01141", 1141),
        ("ibm01142", 1142),
        ("ibm01143", 1143),
        ("ibm01144", 1144),
        ("ibm01145", 1145),
        ("ibm01146", 1146),
        ("ibm01147", 1147),
        ("ibm01148", 1148),
        ("ibm01149", 1149),
        ("ibm037", 37),
        ("ibm1026", 1026),
        ("ibm273", 20273),
        ("ibm277", 20277),
        ("ibm278", 20278),
        ("ibm280", 20280),
        ("ibm284", 20284),
        ("ibm285", 20285),
        ("ibm290", 20290),
        ("ibm297", 20297),
        ("ibm420", 20420),
        ("ibm423", 20423),
        ("ibm424", 20424),
        ("ibm437", 437),
        ("ibm500", 500),
        ("ibm737", 737),
        ("ibm775", 775),
        ("ibm850", 850),
        ("ibm852", 852),
        ("ibm855", 855),
        ("ibm857", 857),
        ("ibm860", 860),
        ("ibm861", 861),
        ("ibm863", 863),
        ("ibm864", 864),
        ("ibm865", 865),
        ("ibm869", 869),
        ("ibm870", 870),
        ("ibm871", 20871),
        ("ibm880", 20880),
        ("ibm905", 20905),
        ("iso-2022-jp", 50220),
        ("iso-2022-kr", 50225),
        ("iso-8859-1", 28591),
        ("iso-8859-13", 28603),
        ("iso-8859-15", 28605),
        ("iso-8859-2", 28592),
        ("iso-8859-3", 28593),
        ("iso-8859-4", 28594),
        ("iso-8859-5", 28595),
        ("iso-8859-6", 28596),
        ("iso-8859-7", 28597),
        ("iso-8859-8", 28598),
        ("iso-8859-8-i", 38598),
        ("iso-8859-9", 28599),
        ("johab", 1361),
        ("koi8-r", 20866),
        ("koi8-u", 21866),
        ("ks_c_5601-1987", 949),
        ("macintosh", 10000),
        ("shift_jis", 932),
        ("us-ascii", 20127),
        ("utf-16be", 1201),
        ("utf-16le", 1200),
        ("utf-32be", 12001),
        ("utf-32le", 12000),
        ("utf-8", 65001),
        ("windows-1250", 1250),
        ("windows-1251", 1251),
        ("windows-1252", 1252),
        ("windows-1253", 1253),
        ("windows-1254", 1254),
        ("windows-1255", 1255),
        ("windows-1256", 1256),
        ("windows-1257", 1257),
        ("windows-1258", 1258),
        ("windows-874", 874),
        ("x-chinese-cns", 20000),
        ("x-chinese-eten", 20002),
        ("x-cp20001", 20001),
        ("x-cp20003", 20003),
        ("x-cp20004", 20004),
        ("x-cp20005", 20005),
        ("x-cp20261", 20261),
        ("x-cp20269", 20269),
        ("x-cp20936", 20936),
        ("x-cp20949", 20949),
        ("x-cp50227", 50227),
        ("x-ebcdic-koreanextended", 20833),
        ("x-europa", 29001),
        ("x-ia5", 20105),
        ("x-ia5-german", 20106),
        ("x-ia5-norwegian", 20108),
        ("x-ia5-swedish", 20107),
        ("x-iscii-as", 57006),
        ("x-iscii-be", 57003),
        ("x-iscii-de", 57002),
        ("x-iscii-gu", 57010),
        ("x-iscii-ka", 57008),
        ("x-iscii-ma", 57009),
        ("x-iscii-or", 57007),
        ("x-iscii-pa", 57011),
        ("x-iscii-ta", 57004),
        ("x-iscii-te", 57005),
        ("x-mac-arabic", 10004),
        ("x-mac-ce", 10029),
        ("x-mac-chinesesimp", 10008),
        ("x-mac-chinesetrad", 10002),
        ("x-mac-croatian", 10082),
        ("x-mac-cyrillic", 10007),
        ("x-mac-greek", 10006),
        ("x-mac-hebrew", 10005),
        ("x-mac-icelandic", 10079),
        ("x-mac-japanese", 10001),
        ("x-mac-korean", 10003),
        ("x-mac-romanian", 10010),
        ("x-mac-thai", 10021),
        ("x-mac-turkish", 10081),
        ("x-mac-ukrainian", 10017),
    ];

    private static Encoding? s_cp37;
    /// <summary><c>ibm037</c>.</summary>
    public static Encoding Cp37 => s_cp37 ??= OfCodePageOrThrow(37);
    private static Encoding? s_cp437;
    /// <summary><c>ibm437</c>.</summary>
    public static Encoding Cp437 => s_cp437 ??= OfCodePageOrThrow(437);
    private static Encoding? s_cp500;
    /// <summary><c>ibm500</c>.</summary>
    public static Encoding Cp500 => s_cp500 ??= OfCodePageOrThrow(500);
    private static Encoding? s_cp708;
    /// <summary><c>asmo-708</c>.</summary>
    public static Encoding Cp708 => s_cp708 ??= OfCodePageOrThrow(708);
    private static Encoding? s_cp720;
    /// <summary><c>dos-720</c>.</summary>
    public static Encoding Cp720 => s_cp720 ??= OfCodePageOrThrow(720);
    private static Encoding? s_cp737;
    /// <summary><c>ibm737</c>.</summary>
    public static Encoding Cp737 => s_cp737 ??= OfCodePageOrThrow(737);
    private static Encoding? s_cp775;
    /// <summary><c>ibm775</c>.</summary>
    public static Encoding Cp775 => s_cp775 ??= OfCodePageOrThrow(775);
    private static Encoding? s_cp850;
    /// <summary><c>ibm850</c>.</summary>
    public static Encoding Cp850 => s_cp850 ??= OfCodePageOrThrow(850);
    private static Encoding? s_cp852;
    /// <summary><c>ibm852</c>.</summary>
    public static Encoding Cp852 => s_cp852 ??= OfCodePageOrThrow(852);
    private static Encoding? s_cp855;
    /// <summary><c>ibm855</c>.</summary>
    public static Encoding Cp855 => s_cp855 ??= OfCodePageOrThrow(855);
    private static Encoding? s_cp857;
    /// <summary><c>ibm857</c>.</summary>
    public static Encoding Cp857 => s_cp857 ??= OfCodePageOrThrow(857);
    private static Encoding? s_cp858;
    /// <summary><c>ibm00858</c>.</summary>
    public static Encoding Cp858 => s_cp858 ??= OfCodePageOrThrow(858);
    private static Encoding? s_cp860;
    /// <summary><c>ibm860</c>.</summary>
    public static Encoding Cp860 => s_cp860 ??= OfCodePageOrThrow(860);
    private static Encoding? s_cp861;
    /// <summary><c>ibm861</c>.</summary>
    public static Encoding Cp861 => s_cp861 ??= OfCodePageOrThrow(861);
    private static Encoding? s_cp862;
    /// <summary><c>dos-862</c>.</summary>
    public static Encoding Cp862 => s_cp862 ??= OfCodePageOrThrow(862);
    private static Encoding? s_cp863;
    /// <summary><c>ibm863</c>.</summary>
    public static Encoding Cp863 => s_cp863 ??= OfCodePageOrThrow(863);
    private static Encoding? s_cp864;
    /// <summary><c>ibm864</c>.</summary>
    public static Encoding Cp864 => s_cp864 ??= OfCodePageOrThrow(864);
    private static Encoding? s_cp865;
    /// <summary><c>ibm865</c>.</summary>
    public static Encoding Cp865 => s_cp865 ??= OfCodePageOrThrow(865);
    private static Encoding? s_cp866;
    /// <summary><c>cp866</c>.</summary>
    public static Encoding Cp866 => s_cp866 ??= OfCodePageOrThrow(866);
    private static Encoding? s_cp869;
    /// <summary><c>ibm869</c>.</summary>
    public static Encoding Cp869 => s_cp869 ??= OfCodePageOrThrow(869);
    private static Encoding? s_cp870;
    /// <summary><c>ibm870</c>.</summary>
    public static Encoding Cp870 => s_cp870 ??= OfCodePageOrThrow(870);
    private static Encoding? s_cp874;
    /// <summary><c>windows-874</c>.</summary>
    public static Encoding Cp874 => s_cp874 ??= OfCodePageOrThrow(874);
    private static Encoding? s_cp875;
    /// <summary><c>cp875</c>.</summary>
    public static Encoding Cp875 => s_cp875 ??= OfCodePageOrThrow(875);
    private static Encoding? s_cp932;
    /// <summary><c>shift_jis</c>.</summary>
    public static Encoding Cp932 => s_cp932 ??= OfCodePageOrThrow(932);
    private static Encoding? s_cp936;
    /// <summary><c>gb2312</c>.</summary>
    public static Encoding Cp936 => s_cp936 ??= OfCodePageOrThrow(936);
    private static Encoding? s_cp949;
    /// <summary><c>ks_c_5601-1987</c>.</summary>
    public static Encoding Cp949 => s_cp949 ??= OfCodePageOrThrow(949);
    private static Encoding? s_cp950;
    /// <summary><c>big5</c>.</summary>
    public static Encoding Cp950 => s_cp950 ??= OfCodePageOrThrow(950);
    private static Encoding? s_cp1026;
    /// <summary><c>ibm1026</c>.</summary>
    public static Encoding Cp1026 => s_cp1026 ??= OfCodePageOrThrow(1026);
    private static Encoding? s_cp1047;
    /// <summary><c>ibm01047</c>.</summary>
    public static Encoding Cp1047 => s_cp1047 ??= OfCodePageOrThrow(1047);
    private static Encoding? s_cp1140;
    /// <summary><c>ibm01140</c>.</summary>
    public static Encoding Cp1140 => s_cp1140 ??= OfCodePageOrThrow(1140);
    private static Encoding? s_cp1141;
    /// <summary><c>ibm01141</c>.</summary>
    public static Encoding Cp1141 => s_cp1141 ??= OfCodePageOrThrow(1141);
    private static Encoding? s_cp1142;
    /// <summary><c>ibm01142</c>.</summary>
    public static Encoding Cp1142 => s_cp1142 ??= OfCodePageOrThrow(1142);
    private static Encoding? s_cp1143;
    /// <summary><c>ibm01143</c>.</summary>
    public static Encoding Cp1143 => s_cp1143 ??= OfCodePageOrThrow(1143);
    private static Encoding? s_cp1144;
    /// <summary><c>ibm01144</c>.</summary>
    public static Encoding Cp1144 => s_cp1144 ??= OfCodePageOrThrow(1144);
    private static Encoding? s_cp1145;
    /// <summary><c>ibm01145</c>.</summary>
    public static Encoding Cp1145 => s_cp1145 ??= OfCodePageOrThrow(1145);
    private static Encoding? s_cp1146;
    /// <summary><c>ibm01146</c>.</summary>
    public static Encoding Cp1146 => s_cp1146 ??= OfCodePageOrThrow(1146);
    private static Encoding? s_cp1147;
    /// <summary><c>ibm01147</c>.</summary>
    public static Encoding Cp1147 => s_cp1147 ??= OfCodePageOrThrow(1147);
    private static Encoding? s_cp1148;
    /// <summary><c>ibm01148</c>.</summary>
    public static Encoding Cp1148 => s_cp1148 ??= OfCodePageOrThrow(1148);
    private static Encoding? s_cp1149;
    /// <summary><c>ibm01149</c>.</summary>
    public static Encoding Cp1149 => s_cp1149 ??= OfCodePageOrThrow(1149);
    private static Encoding? s_cp1200;
    /// <summary><c>utf-16le</c>.</summary>
    public static Encoding Cp1200 => s_cp1200 ??= OfCodePageOrThrow(1200);
    private static Encoding? s_cp1201;
    /// <summary><c>utf-16be</c>.</summary>
    public static Encoding Cp1201 => s_cp1201 ??= OfCodePageOrThrow(1201);
    private static Encoding? s_cp1250;
    /// <summary><c>windows-1250</c>.</summary>
    public static Encoding Cp1250 => s_cp1250 ??= OfCodePageOrThrow(1250);
    private static Encoding? s_cp1251;
    /// <summary><c>windows-1251</c>.</summary>
    public static Encoding Cp1251 => s_cp1251 ??= OfCodePageOrThrow(1251);
    private static Encoding? s_cp1252;
    /// <summary><c>windows-1252</c>.</summary>
    public static Encoding Cp1252 => s_cp1252 ??= OfCodePageOrThrow(1252);
    private static Encoding? s_cp1253;
    /// <summary><c>windows-1253</c>.</summary>
    public static Encoding Cp1253 => s_cp1253 ??= OfCodePageOrThrow(1253);
    private static Encoding? s_cp1254;
    /// <summary><c>windows-1254</c>.</summary>
    public static Encoding Cp1254 => s_cp1254 ??= OfCodePageOrThrow(1254);
    private static Encoding? s_cp1255;
    /// <summary><c>windows-1255</c>.</summary>
    public static Encoding Cp1255 => s_cp1255 ??= OfCodePageOrThrow(1255);
    private static Encoding? s_cp1256;
    /// <summary><c>windows-1256</c>.</summary>
    public static Encoding Cp1256 => s_cp1256 ??= OfCodePageOrThrow(1256);
    private static Encoding? s_cp1257;
    /// <summary><c>windows-1257</c>.</summary>
    public static Encoding Cp1257 => s_cp1257 ??= OfCodePageOrThrow(1257);
    private static Encoding? s_cp1258;
    /// <summary><c>windows-1258</c>.</summary>
    public static Encoding Cp1258 => s_cp1258 ??= OfCodePageOrThrow(1258);
    private static Encoding? s_cp1361;
    /// <summary><c>johab</c>.</summary>
    public static Encoding Cp1361 => s_cp1361 ??= OfCodePageOrThrow(1361);
    private static Encoding? s_cp10000;
    /// <summary><c>macintosh</c>.</summary>
    public static Encoding Cp10000 => s_cp10000 ??= OfCodePageOrThrow(10000);
    private static Encoding? s_cp10001;
    /// <summary><c>x-mac-japanese</c>.</summary>
    public static Encoding Cp10001 => s_cp10001 ??= OfCodePageOrThrow(10001);
    private static Encoding? s_cp10002;
    /// <summary><c>x-mac-chinesetrad</c>.</summary>
    public static Encoding Cp10002 => s_cp10002 ??= OfCodePageOrThrow(10002);
    private static Encoding? s_cp10003;
    /// <summary><c>x-mac-korean</c>.</summary>
    public static Encoding Cp10003 => s_cp10003 ??= OfCodePageOrThrow(10003);
    private static Encoding? s_cp10004;
    /// <summary><c>x-mac-arabic</c>.</summary>
    public static Encoding Cp10004 => s_cp10004 ??= OfCodePageOrThrow(10004);
    private static Encoding? s_cp10005;
    /// <summary><c>x-mac-hebrew</c>.</summary>
    public static Encoding Cp10005 => s_cp10005 ??= OfCodePageOrThrow(10005);
    private static Encoding? s_cp10006;
    /// <summary><c>x-mac-greek</c>.</summary>
    public static Encoding Cp10006 => s_cp10006 ??= OfCodePageOrThrow(10006);
    private static Encoding? s_cp10007;
    /// <summary><c>x-mac-cyrillic</c>.</summary>
    public static Encoding Cp10007 => s_cp10007 ??= OfCodePageOrThrow(10007);
    private static Encoding? s_cp10008;
    /// <summary><c>x-mac-chinesesimp</c>.</summary>
    public static Encoding Cp10008 => s_cp10008 ??= OfCodePageOrThrow(10008);
    private static Encoding? s_cp10010;
    /// <summary><c>x-mac-romanian</c>.</summary>
    public static Encoding Cp10010 => s_cp10010 ??= OfCodePageOrThrow(10010);
    private static Encoding? s_cp10017;
    /// <summary><c>x-mac-ukrainian</c>.</summary>
    public static Encoding Cp10017 => s_cp10017 ??= OfCodePageOrThrow(10017);
    private static Encoding? s_cp10021;
    /// <summary><c>x-mac-thai</c>.</summary>
    public static Encoding Cp10021 => s_cp10021 ??= OfCodePageOrThrow(10021);
    private static Encoding? s_cp10029;
    /// <summary><c>x-mac-ce</c>.</summary>
    public static Encoding Cp10029 => s_cp10029 ??= OfCodePageOrThrow(10029);
    private static Encoding? s_cp10079;
    /// <summary><c>x-mac-icelandic</c>.</summary>
    public static Encoding Cp10079 => s_cp10079 ??= OfCodePageOrThrow(10079);
    private static Encoding? s_cp10081;
    /// <summary><c>x-mac-turkish</c>.</summary>
    public static Encoding Cp10081 => s_cp10081 ??= OfCodePageOrThrow(10081);
    private static Encoding? s_cp10082;
    /// <summary><c>x-mac-croatian</c>.</summary>
    public static Encoding Cp10082 => s_cp10082 ??= OfCodePageOrThrow(10082);
    private static Encoding? s_cp12000;
    /// <summary><c>utf-32le</c>.</summary>
    public static Encoding Cp12000 => s_cp12000 ??= OfCodePageOrThrow(12000);
    private static Encoding? s_cp12001;
    /// <summary><c>utf-32be</c>.</summary>
    public static Encoding Cp12001 => s_cp12001 ??= OfCodePageOrThrow(12001);
    private static Encoding? s_cp20000;
    /// <summary><c>x-chinese-cns</c>.</summary>
    public static Encoding Cp20000 => s_cp20000 ??= OfCodePageOrThrow(20000);
    private static Encoding? s_cp20001;
    /// <summary><c>x-cp20001</c>.</summary>
    public static Encoding Cp20001 => s_cp20001 ??= OfCodePageOrThrow(20001);
    private static Encoding? s_cp20002;
    /// <summary><c>x-chinese-eten</c>.</summary>
    public static Encoding Cp20002 => s_cp20002 ??= OfCodePageOrThrow(20002);
    private static Encoding? s_cp20003;
    /// <summary><c>x-cp20003</c>.</summary>
    public static Encoding Cp20003 => s_cp20003 ??= OfCodePageOrThrow(20003);
    private static Encoding? s_cp20004;
    /// <summary><c>x-cp20004</c>.</summary>
    public static Encoding Cp20004 => s_cp20004 ??= OfCodePageOrThrow(20004);
    private static Encoding? s_cp20005;
    /// <summary><c>x-cp20005</c>.</summary>
    public static Encoding Cp20005 => s_cp20005 ??= OfCodePageOrThrow(20005);
    private static Encoding? s_cp20105;
    /// <summary><c>x-ia5</c>.</summary>
    public static Encoding Cp20105 => s_cp20105 ??= OfCodePageOrThrow(20105);
    private static Encoding? s_cp20106;
    /// <summary><c>x-ia5-german</c>.</summary>
    public static Encoding Cp20106 => s_cp20106 ??= OfCodePageOrThrow(20106);
    private static Encoding? s_cp20107;
    /// <summary><c>x-ia5-swedish</c>.</summary>
    public static Encoding Cp20107 => s_cp20107 ??= OfCodePageOrThrow(20107);
    private static Encoding? s_cp20108;
    /// <summary><c>x-ia5-norwegian</c>.</summary>
    public static Encoding Cp20108 => s_cp20108 ??= OfCodePageOrThrow(20108);
    private static Encoding? s_cp20127;
    /// <summary><c>us-ascii</c>.</summary>
    public static Encoding Cp20127 => s_cp20127 ??= OfCodePageOrThrow(20127);
    private static Encoding? s_cp20261;
    /// <summary><c>x-cp20261</c>.</summary>
    public static Encoding Cp20261 => s_cp20261 ??= OfCodePageOrThrow(20261);
    private static Encoding? s_cp20269;
    /// <summary><c>x-cp20269</c>.</summary>
    public static Encoding Cp20269 => s_cp20269 ??= OfCodePageOrThrow(20269);
    private static Encoding? s_cp20273;
    /// <summary><c>ibm273</c>.</summary>
    public static Encoding Cp20273 => s_cp20273 ??= OfCodePageOrThrow(20273);
    private static Encoding? s_cp20277;
    /// <summary><c>ibm277</c>.</summary>
    public static Encoding Cp20277 => s_cp20277 ??= OfCodePageOrThrow(20277);
    private static Encoding? s_cp20278;
    /// <summary><c>ibm278</c>.</summary>
    public static Encoding Cp20278 => s_cp20278 ??= OfCodePageOrThrow(20278);
    private static Encoding? s_cp20280;
    /// <summary><c>ibm280</c>.</summary>
    public static Encoding Cp20280 => s_cp20280 ??= OfCodePageOrThrow(20280);
    private static Encoding? s_cp20284;
    /// <summary><c>ibm284</c>.</summary>
    public static Encoding Cp20284 => s_cp20284 ??= OfCodePageOrThrow(20284);
    private static Encoding? s_cp20285;
    /// <summary><c>ibm285</c>.</summary>
    public static Encoding Cp20285 => s_cp20285 ??= OfCodePageOrThrow(20285);
    private static Encoding? s_cp20290;
    /// <summary><c>ibm290</c>.</summary>
    public static Encoding Cp20290 => s_cp20290 ??= OfCodePageOrThrow(20290);
    private static Encoding? s_cp20297;
    /// <summary><c>ibm297</c>.</summary>
    public static Encoding Cp20297 => s_cp20297 ??= OfCodePageOrThrow(20297);
    private static Encoding? s_cp20420;
    /// <summary><c>ibm420</c>.</summary>
    public static Encoding Cp20420 => s_cp20420 ??= OfCodePageOrThrow(20420);
    private static Encoding? s_cp20423;
    /// <summary><c>ibm423</c>.</summary>
    public static Encoding Cp20423 => s_cp20423 ??= OfCodePageOrThrow(20423);
    private static Encoding? s_cp20424;
    /// <summary><c>ibm424</c>.</summary>
    public static Encoding Cp20424 => s_cp20424 ??= OfCodePageOrThrow(20424);
    private static Encoding? s_cp20833;
    /// <summary><c>x-ebcdic-koreanextended</c>.</summary>
    public static Encoding Cp20833 => s_cp20833 ??= OfCodePageOrThrow(20833);
    private static Encoding? s_cp20838;
    /// <summary><c>ibm-thai</c>.</summary>
    public static Encoding Cp20838 => s_cp20838 ??= OfCodePageOrThrow(20838);
    private static Encoding? s_cp20866;
    /// <summary><c>koi8-r</c>.</summary>
    public static Encoding Cp20866 => s_cp20866 ??= OfCodePageOrThrow(20866);
    private static Encoding? s_cp20871;
    /// <summary><c>ibm871</c>.</summary>
    public static Encoding Cp20871 => s_cp20871 ??= OfCodePageOrThrow(20871);
    private static Encoding? s_cp20880;
    /// <summary><c>ibm880</c>.</summary>
    public static Encoding Cp20880 => s_cp20880 ??= OfCodePageOrThrow(20880);
    private static Encoding? s_cp20905;
    /// <summary><c>ibm905</c>.</summary>
    public static Encoding Cp20905 => s_cp20905 ??= OfCodePageOrThrow(20905);
    private static Encoding? s_cp20924;
    /// <summary><c>ibm00924</c>.</summary>
    public static Encoding Cp20924 => s_cp20924 ??= OfCodePageOrThrow(20924);
    private static Encoding? s_cp20932;
    /// <summary><c>cp20932</c>.</summary>
    public static Encoding Cp20932 => s_cp20932 ??= OfCodePageOrThrow(20932);
    private static Encoding? s_cp20936;
    /// <summary><c>x-cp20936</c>.</summary>
    public static Encoding Cp20936 => s_cp20936 ??= OfCodePageOrThrow(20936);
    private static Encoding? s_cp20949;
    /// <summary><c>x-cp20949</c>.</summary>
    public static Encoding Cp20949 => s_cp20949 ??= OfCodePageOrThrow(20949);
    private static Encoding? s_cp21025;
    /// <summary><c>cp1025</c>.</summary>
    public static Encoding Cp21025 => s_cp21025 ??= OfCodePageOrThrow(21025);
    private static Encoding? s_cp21866;
    /// <summary><c>koi8-u</c>.</summary>
    public static Encoding Cp21866 => s_cp21866 ??= OfCodePageOrThrow(21866);
    private static Encoding? s_cp28591;
    /// <summary><c>iso-8859-1</c>.</summary>
    public static Encoding Cp28591 => s_cp28591 ??= OfCodePageOrThrow(28591);
    private static Encoding? s_cp28592;
    /// <summary><c>iso-8859-2</c>.</summary>
    public static Encoding Cp28592 => s_cp28592 ??= OfCodePageOrThrow(28592);
    private static Encoding? s_cp28593;
    /// <summary><c>iso-8859-3</c>.</summary>
    public static Encoding Cp28593 => s_cp28593 ??= OfCodePageOrThrow(28593);
    private static Encoding? s_cp28594;
    /// <summary><c>iso-8859-4</c>.</summary>
    public static Encoding Cp28594 => s_cp28594 ??= OfCodePageOrThrow(28594);
    private static Encoding? s_cp28595;
    /// <summary><c>iso-8859-5</c>.</summary>
    public static Encoding Cp28595 => s_cp28595 ??= OfCodePageOrThrow(28595);
    private static Encoding? s_cp28596;
    /// <summary><c>iso-8859-6</c>.</summary>
    public static Encoding Cp28596 => s_cp28596 ??= OfCodePageOrThrow(28596);
    private static Encoding? s_cp28597;
    /// <summary><c>iso-8859-7</c>.</summary>
    public static Encoding Cp28597 => s_cp28597 ??= OfCodePageOrThrow(28597);
    private static Encoding? s_cp28598;
    /// <summary><c>iso-8859-8</c>.</summary>
    public static Encoding Cp28598 => s_cp28598 ??= OfCodePageOrThrow(28598);
    private static Encoding? s_cp28599;
    /// <summary><c>iso-8859-9</c>.</summary>
    public static Encoding Cp28599 => s_cp28599 ??= OfCodePageOrThrow(28599);
    private static Encoding? s_cp28603;
    /// <summary><c>iso-8859-13</c>.</summary>
    public static Encoding Cp28603 => s_cp28603 ??= OfCodePageOrThrow(28603);
    private static Encoding? s_cp28605;
    /// <summary><c>iso-8859-15</c>.</summary>
    public static Encoding Cp28605 => s_cp28605 ??= OfCodePageOrThrow(28605);
    private static Encoding? s_cp29001;
    /// <summary><c>x-europa</c>.</summary>
    public static Encoding Cp29001 => s_cp29001 ??= OfCodePageOrThrow(29001);
    private static Encoding? s_cp38598;
    /// <summary><c>iso-8859-8-i</c>.</summary>
    public static Encoding Cp38598 => s_cp38598 ??= OfCodePageOrThrow(38598);
    private static Encoding? s_cp50220;
    /// <summary><c>iso-2022-jp</c>.</summary>
    public static Encoding Cp50220 => s_cp50220 ??= OfCodePageOrThrow(50220);
    private static Encoding? s_cp50221;
    /// <summary><c>csiso2022jp</c>.</summary>
    public static Encoding Cp50221 => s_cp50221 ??= OfCodePageOrThrow(50221);
    private static Encoding? s_cp50222;
    /// <summary><c>cp50222</c>.</summary>
    public static Encoding Cp50222 => s_cp50222 ??= OfCodePageOrThrow(50222);
    private static Encoding? s_cp50225;
    /// <summary><c>iso-2022-kr</c>.</summary>
    public static Encoding Cp50225 => s_cp50225 ??= OfCodePageOrThrow(50225);
    private static Encoding? s_cp50227;
    /// <summary><c>x-cp50227</c>.</summary>
    public static Encoding Cp50227 => s_cp50227 ??= OfCodePageOrThrow(50227);
    private static Encoding? s_cp51932;
    /// <summary><c>euc-jp</c>.</summary>
    public static Encoding Cp51932 => s_cp51932 ??= OfCodePageOrThrow(51932);
    private static Encoding? s_cp51936;
    /// <summary><c>euc-cn</c>.</summary>
    public static Encoding Cp51936 => s_cp51936 ??= OfCodePageOrThrow(51936);
    private static Encoding? s_cp51949;
    /// <summary><c>euc-kr</c>.</summary>
    public static Encoding Cp51949 => s_cp51949 ??= OfCodePageOrThrow(51949);
    private static Encoding? s_cp52936;
    /// <summary><c>hz-gb-2312</c>.</summary>
    public static Encoding Cp52936 => s_cp52936 ??= OfCodePageOrThrow(52936);
    private static Encoding? s_cp54936;
    /// <summary><c>gb18030</c>.</summary>
    public static Encoding Cp54936 => s_cp54936 ??= OfCodePageOrThrow(54936);
    private static Encoding? s_cp57002;
    /// <summary><c>x-iscii-de</c>.</summary>
    public static Encoding Cp57002 => s_cp57002 ??= OfCodePageOrThrow(57002);
    private static Encoding? s_cp57003;
    /// <summary><c>x-iscii-be</c>.</summary>
    public static Encoding Cp57003 => s_cp57003 ??= OfCodePageOrThrow(57003);
    private static Encoding? s_cp57004;
    /// <summary><c>x-iscii-ta</c>.</summary>
    public static Encoding Cp57004 => s_cp57004 ??= OfCodePageOrThrow(57004);
    private static Encoding? s_cp57005;
    /// <summary><c>x-iscii-te</c>.</summary>
    public static Encoding Cp57005 => s_cp57005 ??= OfCodePageOrThrow(57005);
    private static Encoding? s_cp57006;
    /// <summary><c>x-iscii-as</c>.</summary>
    public static Encoding Cp57006 => s_cp57006 ??= OfCodePageOrThrow(57006);
    private static Encoding? s_cp57007;
    /// <summary><c>x-iscii-or</c>.</summary>
    public static Encoding Cp57007 => s_cp57007 ??= OfCodePageOrThrow(57007);
    private static Encoding? s_cp57008;
    /// <summary><c>x-iscii-ka</c>.</summary>
    public static Encoding Cp57008 => s_cp57008 ??= OfCodePageOrThrow(57008);
    private static Encoding? s_cp57009;
    /// <summary><c>x-iscii-ma</c>.</summary>
    public static Encoding Cp57009 => s_cp57009 ??= OfCodePageOrThrow(57009);
    private static Encoding? s_cp57010;
    /// <summary><c>x-iscii-gu</c>.</summary>
    public static Encoding Cp57010 => s_cp57010 ??= OfCodePageOrThrow(57010);
    private static Encoding? s_cp57011;
    /// <summary><c>x-iscii-pa</c>.</summary>
    public static Encoding Cp57011 => s_cp57011 ??= OfCodePageOrThrow(57011);
    private static Encoding? s_cp65001;
    /// <summary><c>utf-8</c>.</summary>
    public static Encoding Cp65001 => s_cp65001 ??= OfCodePageOrThrow(65001);
}
