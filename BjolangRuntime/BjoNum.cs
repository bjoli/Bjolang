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

using System.Globalization;
using System.Text;
using BjoString;

namespace Bjolang.Runtime;

/// <summary>
/// Numbers read and written the same way wherever the program runs.
/// </summary>
///
/// <remarks>
/// <para>
/// <c>Parse</c> and <c>ToString</c> both take the ambient culture, so
/// <c>1.5</c> is unreadable and unwritable where the decimal separator is a
/// comma, and <c>-100</c> comes back as <c>−100</c> where the minus is U+2212.
/// Every conversion Bjolang offers goes through here instead, which means a
/// document written by one program is readable by the next.
/// </para>
/// <para>
/// <c>NumberStyles.Float</c> is a sign, digits, a point, and an exponent —
/// what a JSON number is, and what <c>double-&gt;string</c> writes back.
/// </para>
/// </remarks>
/// <summary>
/// Marks a union whose case with no fields is represented as <c>null</c>, and
/// names that case, for printing.
/// </summary>
///
/// <remarks>
/// A union with exactly one case that carries nothing, and at least one that
/// carries something, represents the empty case as <c>null</c>: a value that
/// holds it stores no reference, which needs no GC write barrier, and a test
/// for it is a compare with zero. The compiler writes this attribute on the
/// union's base class, as <c>null</c> itself says nothing of its type.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
[NonUserCode]
public sealed class NullCaseAttribute(string name) : Attribute {
    public string Name { get; } = name;
}

/// <summary>The name of <typeparamref name="T"/>'s null case, read once.</summary>
[NonUserCode]
public static class NullCase<T> {
    public static readonly string? Name =
        (Attribute.GetCustomAttribute(typeof(T), typeof(NullCaseAttribute), false) as NullCaseAttribute)?.Name;
}

/// The largest and the smallest value of a number type, which is where
/// `up-from` and `down-from` stop when no `#:to` says otherwise.
///
/// One `typeof` test per type, not a lookup of `MaxValue` by reflection: a
/// Native AOT build has no metadata for those fields, so the lookup fails
/// there. Each test is a constant in the copy of the method for one type, under
/// the JIT and under Native AOT, so a walk pays nothing for it.
///
/// A `double` runs to its infinities, past its `MaxValue`, which is where a
/// walk with no end is going anyway. A `BigInteger` has no end, so it gets one
/// no count can reach: 2^(2^20), a number of 128 KiB, made once.
/// `BigInteger` compares lengths before digits, so a walk's test against it
/// is as cheap as against a small one.
[NonUserCode]
public static class NumBounds {
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static T Bound<T>(bool top) {
        if (typeof(T) == typeof(int)) return (T)(object)(top ? int.MaxValue : int.MinValue);
        if (typeof(T) == typeof(long)) return (T)(object)(top ? long.MaxValue : long.MinValue);
        if (typeof(T) == typeof(double)) return (T)(object)(top ? double.PositiveInfinity : double.NegativeInfinity);
        if (typeof(T) == typeof(float)) return (T)(object)(top ? float.PositiveInfinity : float.NegativeInfinity);
        if (typeof(T) == typeof(short)) return (T)(object)(top ? short.MaxValue : short.MinValue);
        if (typeof(T) == typeof(sbyte)) return (T)(object)(top ? sbyte.MaxValue : sbyte.MinValue);
        if (typeof(T) == typeof(byte)) return (T)(object)(top ? byte.MaxValue : byte.MinValue);
        if (typeof(T) == typeof(ushort)) return (T)(object)(top ? ushort.MaxValue : ushort.MinValue);
        if (typeof(T) == typeof(uint)) return (T)(object)(top ? uint.MaxValue : uint.MinValue);
        if (typeof(T) == typeof(ulong)) return (T)(object)(top ? ulong.MaxValue : ulong.MinValue);
        if (typeof(T) == typeof(nint)) return (T)(object)(top ? nint.MaxValue : nint.MinValue);
        if (typeof(T) == typeof(nuint)) return (T)(object)(top ? nuint.MaxValue : nuint.MinValue);
        if (typeof(T) == typeof(Int128)) return (T)(object)(top ? Int128.MaxValue : Int128.MinValue);
        if (typeof(T) == typeof(UInt128)) return (T)(object)(top ? UInt128.MaxValue : UInt128.MinValue);
        if (typeof(T) == typeof(decimal)) return (T)(object)(top ? decimal.MaxValue : decimal.MinValue);
        if (typeof(T) == typeof(Half)) return (T)(object)(top ? Half.PositiveInfinity : Half.NegativeInfinity);
        if (typeof(T) == typeof(System.Numerics.BigInteger)) return (T)(object)(top ? FarBigInteger.Top : FarBigInteger.Bottom);
        throw new InvalidOperationException($"{typeof(T)} has no {(top ? "largest" : "smallest")} value to count to.");
    }

    [NonUserCode]
    private static class FarBigInteger {
        internal static readonly System.Numerics.BigInteger Top = System.Numerics.BigInteger.One << (1 << 20);
        internal static readonly System.Numerics.BigInteger Bottom = -Top;
    }
}

[NonUserCode]
public static class BjoNum {
    // The ends of a number type: see `NumBounds`.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static T Top<T>() => NumBounds.Bound<T>(true);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static T Bottom<T>() => NumBounds.Bound<T>(false);

    // In the invariant culture: `sv-SE` writes its minus as U+2212 MINUS SIGN,
    // which no parser expects. The same styles as `Utf8Number.ParseInt`: a
    // sign, digits, and white space at either end.
    public static long ParseLong(Utf8String s) =>
        long.Parse(s.AsSpan(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// The text of `n` in base `radix`: `int->string` and `long->string` with
    /// `#:radix`.
    ///
    /// - Digits are 0-9, then a-z, or A-Z when `upper` is true.
    /// - A negative number has an ASCII minus and the digits of its magnitude,
    ///   not the two's complement: -255 in base 16 is "-ff".
    /// - Raises `ArgumentOutOfRangeException` when `radix` is not 2 to 36.
    public static Utf8String FormatRadix(long n, int radix, bool upper) {
        CheckRadix(radix);
        Span<byte> digits = stackalloc byte[65];
        int at = digits.Length;
        // The magnitude as a ulong, which holds that of long.MinValue too.
        ulong magnitude = n < 0 ? (ulong)(-(n + 1)) + 1 : (ulong)n;
        byte letter = upper ? (byte)'A' : (byte)'a';
        do {
            int d = (int)(magnitude % (ulong)radix);
            magnitude /= (ulong)radix;
            digits[--at] = d < 10 ? (byte)('0' + d) : (byte)(letter + d - 10);
        } while (magnitude != 0);
        if (n < 0) digits[--at] = (byte)'-';
        return Utf8String.FromUtf8(digits[at..]);
    }

    /// The number that `s` writes in base `radix`: `string->long` with
    /// `#:radix`.
    ///
    /// - White space at either end, as `string->long` takes. Then an optional
    ///   `+` or `-`, then one or more digits, 0-9 and letters in either case.
    ///   A prefix such as `0x` is not read.
    /// - Raises `FormatException` for other text, `OverflowException` when
    ///   the number is outside `min` to `max`, and
    ///   `ArgumentOutOfRangeException` when `radix` is not 2 to 36.
    public static long ParseRadix(Utf8String s, int radix, long min, long max) {
        CheckRadix(radix);
        var text = s.AsSpan().Trim(" \t\n\v\f\r"u8);
        bool negative = false;
        if (text.Length > 0 && (text[0] == (byte)'-' || text[0] == (byte)'+')) {
            negative = text[0] == (byte)'-';
            text = text[1..];
        }
        if (text.Length == 0)
            throw new FormatException($"\"{s}\" is not a number in base {radix}");
        // Digits accumulate as a negative number, whose range includes the
        // magnitude of `min`.
        long limit = negative ? min : -max;
        long value = 0;
        foreach (byte c in text) {
            int d = c >= '0' && c <= '9' ? c - '0'
                  : c >= 'a' && c <= 'z' ? c - 'a' + 10
                  : c >= 'A' && c <= 'Z' ? c - 'A' + 10
                  : 99;
            if (d >= radix)
                throw new FormatException($"\"{s}\" is not a number in base {radix}");
            if (value < (limit + d) / radix)
                throw new OverflowException($"\"{s}\" in base {radix} is too large");
            value = value * radix - d;
            if (value < limit)
                throw new OverflowException($"\"{s}\" in base {radix} is too large");
        }
        return negative ? value : -value;
    }

    public static Utf8String FormatRadix(int n, int radix, bool upper) => FormatRadix((long)n, radix, upper);

    public static int ParseRadixInt(Utf8String s, int radix) =>
        (int)ParseRadix(s, radix, int.MinValue, int.MaxValue);

    public static long ParseRadixLong(Utf8String s, int radix) =>
        ParseRadix(s, radix, long.MinValue, long.MaxValue);

    static void CheckRadix(int radix) {
        if (radix < 2 || radix > 36)
            throw new ArgumentOutOfRangeException(nameof(radix), radix, "a radix is from 2 to 36");
    }

    // `decimal`, `bigint`, `int128` and `uint128`, which have no reader in
    // Utf8Number. Through .NET's own parsers, in the invariant culture. An
    // integer takes a sign and digits; a decimal also takes a point and an
    // exponent, as a double does.
    public static T ParseInteger<T>(Utf8String s) where T : System.Numerics.IBinaryInteger<T> =>
        T.Parse(s.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    public static decimal ParseDecimal(Utf8String s) =>
        decimal.Parse(s.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    public static Utf8String FormatNumber<T>(T n) where T : System.Numerics.INumber<T> =>
        Utf8String.FromUtf16(n.ToString(null, CultureInfo.InvariantCulture));

    /// The prelude's `clr-show`, which the blanket `Written` writes with:
    /// whatever a type with no implementation of its own says about itself,
    /// asked in the invariant culture, except that `null` at a union whose
    /// empty case is `null` is that case's name. The type is what says which
    /// name, as the value has none.
    public static Utf8String Show<T>(T value) => Utf8String.FromUtf16(ShowField(value));

    /// What a union case's record text writes for one of its fields. A
    /// boolean is written as the language writes it, `#t` or `#f`, and a
    /// function as Scheme writes one, `#&lt;procedure&gt;`.
    public static string ShowField<T>(T value) =>
        value is null ? NullCase<T>.Name ?? ""
        : value is bool b ? (b ? "#t" : "#f")
        : value is Delegate ? "#<procedure>"
        : value is System.Runtime.CompilerServices.ITuple tuple ? ShowTuple(tuple)
        : System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    /// A tuple as `ValueTuple` writes it, `(a, b)`. Written here because
    /// `ValueTuple` writes a `null` item as nothing; the item's type, read off
    /// the tuple's type, gives the name of the case it is.
    static string ShowTuple(System.Runtime.CompilerServices.ITuple tuple) {
        var itemTypes = tuple.GetType().GetGenericArguments();
        var sb = new System.Text.StringBuilder("(");
        for (int i = 0; i < tuple.Length; i++) {
            if (i > 0) sb.Append(", ");
            var item = tuple[i];
            if (item is null) {
                var type = i < itemTypes.Length ? itemTypes[i] : null;
                var attribute = type is null ? null
                    : Attribute.GetCustomAttribute(type, typeof(NullCaseAttribute), false) as NullCaseAttribute;
                sb.Append(attribute?.Name);
            } else {
                sb.Append(ShowField<object>(item));
            }
        }
        return sb.Append(')').ToString();
    }

    // A scanner that has just spelled a number into a builder wants the number,
    // not the string. These parse the builder's bytes where they are.
    public static double ParseDouble(Utf8StringBuilder b) => Utf8Number.ParseDouble(b.AsSpan());

    /// Overflows rather than saturating, so a number too wide for a long can be
    /// caught and read again as a double.
    public static long ParseLong(Utf8StringBuilder b) => Utf8Number.ParseLong(b.AsSpan());
}
