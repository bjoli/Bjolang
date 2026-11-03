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

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using BjoString;

namespace Bjolang.Runtime;

/// <summary>
/// Bjolang's <c>rational</c>: an exact fraction over <c>BigInteger</c>.
/// </summary>
///
/// <remarks>
/// <list type="bullet">
/// <item>Always normalised: the denominator is positive and shares no factor
/// with the numerator. <c>default</c> is 0.</item>
/// <item>Implements <c>INumber</c>, so it is a <c>Num</c> and a carrier of
/// units, and <c>+ - * / %</c> and the comparisons are its operators.</item>
/// <item>Division by zero throws <c>DivideByZeroException</c>.</item>
/// <item>Text: <c>5</c>, <c>5/18</c>. <c>Parse</c> also reads <c>5|18</c>,
/// <c>0.3048</c> and <c>1e-3</c>, exactly.</item>
/// </list>
/// </remarks>
[NonUserCode]
public readonly struct BjoRational : INumber<BjoRational>, ISignedNumber<BjoRational>
{
    private readonly BigInteger _num;
    // 0 only in `default`, which is 0/1.
    private readonly BigInteger _den;

    public BigInteger Numerator => _num;
    public BigInteger Denominator => _den.IsZero ? BigInteger.One : _den;

    public static BigInteger NumeratorOf(BjoRational r) => r.Numerator;
    public static BigInteger DenominatorOf(BjoRational r) => r.Denominator;

    public BjoRational(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
            throw new DivideByZeroException("a rational with denominator 0");

        if (denominator.Sign < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        var g = BigInteger.GreatestCommonDivisor(numerator, denominator);

        if (!g.IsOne && !g.IsZero)
        {
            numerator /= g;
            denominator /= g;
        }

        _num = numerator;
        _den = numerator.IsZero ? BigInteger.One : denominator;
    }

    public BjoRational(long numerator, long denominator)
        : this(new BigInteger(numerator), new BigInteger(denominator)) { }

    public BjoRational(BigInteger value)
    {
        _num = value;
        _den = BigInteger.One;
    }

    public static implicit operator BjoRational(int value) => new(new BigInteger(value));
    public static implicit operator BjoRational(long value) => new(new BigInteger(value));
    public static implicit operator BjoRational(BigInteger value) => new(value);

    /// <summary>Rounded once, to the nearest double.</summary>
    public static explicit operator double(BjoRational r) => ToDouble(r);

    /// <summary>Rounded once, to the nearest decimal.</summary>
    public static explicit operator decimal(BjoRational r) => (decimal)r.Numerator / (decimal)r.Denominator;

    private static double ToDouble(BjoRational r)
    {
        var d = (double)r.Numerator / (double)r.Denominator;

        if (double.IsFinite(d) && (d != 0 || r.Numerator.IsZero))
            return d;

        // Both sides too large or too small for a double on their own.
        var exponent = BigInteger.Log(BigInteger.Abs(r.Numerator)) - BigInteger.Log(r.Denominator);
        return r.Numerator.Sign * Math.Exp(exponent);
    }

    // --- Identities ------------------------------------------------------

    public static BjoRational Zero => default;
    public static BjoRational One => new(BigInteger.One);
    public static BjoRational NegativeOne => new(BigInteger.MinusOne);
    public static int Radix => 10;
    public static BjoRational AdditiveIdentity => Zero;
    public static BjoRational MultiplicativeIdentity => One;

    // --- Arithmetic ------------------------------------------------------

    public static BjoRational operator +(BjoRational a, BjoRational b) =>
        new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);

    public static BjoRational operator -(BjoRational a, BjoRational b) =>
        new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);

    public static BjoRational operator *(BjoRational a, BjoRational b) =>
        new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);

    public static BjoRational operator /(BjoRational a, BjoRational b)
    {
        if (b.Numerator.IsZero)
            throw new DivideByZeroException();

        return new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);
    }

    /// <summary>a - b × truncate(a / b), as for the other numbers.</summary>
    public static BjoRational operator %(BjoRational a, BjoRational b)
    {
        var q = a / b;
        var whole = BigInteger.Divide(q.Numerator, q.Denominator);
        return a - b * new BjoRational(whole);
    }

    public static BjoRational operator -(BjoRational a) => new(-a.Numerator, a.Denominator);
    public static BjoRational operator +(BjoRational a) => a;
    public static BjoRational operator ++(BjoRational a) => a + One;
    public static BjoRational operator --(BjoRational a) => a - One;

    // --- Comparison ------------------------------------------------------

    public int CompareTo(BjoRational other) =>
        (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        BjoRational r => CompareTo(r),
        _ => throw new ArgumentException("not a rational", nameof(obj)),
    };

    public bool Equals(BjoRational other) => Numerator == other.Numerator && Denominator == other.Denominator;
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is BjoRational r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    public static bool operator ==(BjoRational a, BjoRational b) => a.Equals(b);
    public static bool operator !=(BjoRational a, BjoRational b) => !a.Equals(b);
    public static bool operator <(BjoRational a, BjoRational b) => a.CompareTo(b) < 0;
    public static bool operator >(BjoRational a, BjoRational b) => a.CompareTo(b) > 0;
    public static bool operator <=(BjoRational a, BjoRational b) => a.CompareTo(b) <= 0;
    public static bool operator >=(BjoRational a, BjoRational b) => a.CompareTo(b) >= 0;

    // --- Classification --------------------------------------------------

    public static BjoRational Abs(BjoRational value) => value.Numerator.Sign < 0 ? -value : value;
    public static bool IsCanonical(BjoRational value) => true;
    public static bool IsComplexNumber(BjoRational value) => false;
    public static bool IsInteger(BjoRational value) => value.Denominator.IsOne;
    public static bool IsEvenInteger(BjoRational value) => IsInteger(value) && value.Numerator.IsEven;
    public static bool IsOddInteger(BjoRational value) => IsInteger(value) && !value.Numerator.IsEven;
    public static bool IsFinite(BjoRational value) => true;
    public static bool IsImaginaryNumber(BjoRational value) => false;
    public static bool IsInfinity(BjoRational value) => false;
    public static bool IsNaN(BjoRational value) => false;
    public static bool IsNegative(BjoRational value) => value.Numerator.Sign < 0;
    public static bool IsNegativeInfinity(BjoRational value) => false;
    public static bool IsPositiveInfinity(BjoRational value) => false;
    public static bool IsNormal(BjoRational value) => !value.Numerator.IsZero;
    public static bool IsPositive(BjoRational value) => value.Numerator.Sign >= 0;
    public static bool IsRealNumber(BjoRational value) => true;
    public static bool IsSubnormal(BjoRational value) => false;
    public static bool IsZero(BjoRational value) => value.Numerator.IsZero;

    public static BjoRational MaxMagnitude(BjoRational x, BjoRational y) => Abs(x) >= Abs(y) ? x : y;
    public static BjoRational MaxMagnitudeNumber(BjoRational x, BjoRational y) => MaxMagnitude(x, y);
    public static BjoRational MinMagnitude(BjoRational x, BjoRational y) => Abs(x) <= Abs(y) ? x : y;
    public static BjoRational MinMagnitudeNumber(BjoRational x, BjoRational y) => MinMagnitude(x, y);

    // --- Conversions -----------------------------------------------------

    private static bool TryFrom<TOther>(TOther value, out BjoRational result)
    {
        switch (value)
        {
            case BjoRational r: result = r; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case short s: result = (int)s; return true;
            case sbyte sb: result = (int)sb; return true;
            case byte b: result = (int)b; return true;
            case ushort us: result = (int)us; return true;
            case uint ui: result = (long)ui; return true;
            case ulong ul: result = new BigInteger(ul); return true;
            case Int128 i128: result = (BigInteger)i128; return true;
            case UInt128 u128: result = (BigInteger)u128; return true;
            case BigInteger bi: result = bi; return true;
            case decimal m: result = FromDecimal(m); return true;
            case double d when double.IsFinite(d): result = FromDouble(d); return true;
            case float f when float.IsFinite(f): result = FromDouble(f); return true;
            default: result = default; return false;
        }
    }

    /// <summary>The decimal exactly: its digits over a power of ten.</summary>
    public static BjoRational FromDecimal(decimal m)
    {
        var bits = decimal.GetBits(m);
        var scale = (bits[3] >> 16) & 0xFF;
        var negative = (bits[3] & unchecked((int)0x80000000)) != 0;
        var mantissa = new BigInteger((uint)bits[0]) | (new BigInteger((uint)bits[1]) << 32) | (new BigInteger((uint)bits[2]) << 64);
        return new(negative ? -mantissa : mantissa, BigInteger.Pow(10, scale));
    }

    /// <summary>The double exactly: its mantissa over a power of two.</summary>
    public static BjoRational FromDouble(double d)
    {
        if (!double.IsFinite(d))
            throw new OverflowException($"{d} is not a rational");

        var bits = BitConverter.DoubleToInt64Bits(d);
        var negative = bits < 0;
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;

        if (exponent == 0)
            exponent++;
        else
            mantissa |= 1L << 52;

        exponent -= 1075;
        var m = new BigInteger(negative ? -mantissa : mantissa);

        return exponent >= 0
            ? new(m * BigInteger.Pow(2, exponent))
            : new(m, BigInteger.Pow(2, -exponent));
    }

    private static bool TryTo<TOther>(BjoRational value, bool truncate, out TOther result)
    {
        object? boxed = null;
        var whole = BigInteger.Divide(value.Numerator, value.Denominator);

        if (typeof(TOther) == typeof(BjoRational)) boxed = value;
        else if (typeof(TOther) == typeof(double)) boxed = ToDouble(value);
        else if (typeof(TOther) == typeof(float)) boxed = (float)ToDouble(value);
        else if (typeof(TOther) == typeof(decimal)) boxed = (decimal)value;
        else if (IsInteger(value) || truncate)
        {
            if (typeof(TOther) == typeof(BigInteger)) boxed = whole;
            else if (typeof(TOther) == typeof(int)) boxed = (int)whole;
            else if (typeof(TOther) == typeof(long)) boxed = (long)whole;
            else if (typeof(TOther) == typeof(short)) boxed = (short)whole;
            else if (typeof(TOther) == typeof(byte)) boxed = (byte)whole;
            else if (typeof(TOther) == typeof(uint)) boxed = (uint)whole;
            else if (typeof(TOther) == typeof(ulong)) boxed = (ulong)whole;
            else if (typeof(TOther) == typeof(Int128)) boxed = (Int128)whole;
            else if (typeof(TOther) == typeof(UInt128)) boxed = (UInt128)whole;
        }

        result = boxed is null ? default! : (TOther)boxed;
        return boxed is not null;
    }

    static bool INumberBase<BjoRational>.TryConvertFromChecked<TOther>(TOther value, out BjoRational result) => TryFrom(value, out result);
    static bool INumberBase<BjoRational>.TryConvertFromSaturating<TOther>(TOther value, out BjoRational result) => TryFrom(value, out result);
    static bool INumberBase<BjoRational>.TryConvertFromTruncating<TOther>(TOther value, out BjoRational result) => TryFrom(value, out result);
    static bool INumberBase<BjoRational>.TryConvertToChecked<TOther>(BjoRational value, [MaybeNullWhen(false)] out TOther result) => TryTo(value, false, out result);
    static bool INumberBase<BjoRational>.TryConvertToSaturating<TOther>(BjoRational value, [MaybeNullWhen(false)] out TOther result) => TryTo(value, true, out result);
    static bool INumberBase<BjoRational>.TryConvertToTruncating<TOther>(BjoRational value, [MaybeNullWhen(false)] out TOther result) => TryTo(value, true, out result);

    // --- Text ------------------------------------------------------------

    /// <summary>"5", "-5/18".</summary>
    public override string ToString() =>
        Denominator.IsOne
            ? Numerator.ToString(CultureInfo.InvariantCulture)
            : Numerator.ToString(CultureInfo.InvariantCulture) + "/" + Denominator.ToString(CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <summary>As the reader reads it back: "(: 5 rational)",
    /// "(: &lt;5|18&gt; rational)".</summary>
    public static Utf8String Written(BjoRational r) =>
        Utf8String.FromUtf16(
            r.Denominator.IsOne
                ? $"(: {r.Numerator.ToString(CultureInfo.InvariantCulture)} rational)"
                : $"(: <{r.Numerator.ToString(CultureInfo.InvariantCulture)}|{r.Denominator.ToString(CultureInfo.InvariantCulture)}> rational)");

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        var text = ToString();

        if (text.Length > destination.Length)
        {
            charsWritten = 0;
            return false;
        }

        text.AsSpan().CopyTo(destination);
        charsWritten = text.Length;
        return true;
    }

    private static BigInteger? Integer(ReadOnlySpan<char> s) =>
        BigInteger.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>"5", "-2", "5/18", "5|18", "0.3048", "-1.5e-3".</summary>
    public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out BjoRational result)
    {
        result = default;
        s = s.Trim();
        var bar = s.IndexOfAny('/', '|');

        if (bar >= 0)
        {
            var n = Integer(s[..bar]);
            var d = Integer(s[(bar + 1)..]);

            if (n is null || d is null || d.Value.IsZero)
                return false;

            result = new(n.Value, d.Value);
            return true;
        }

        var e = s.IndexOfAny('e', 'E');
        var exponent = 0;

        if (e >= 0)
        {
            if (!int.TryParse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
                return false;

            s = s[..e];
        }

        var negative = s.StartsWith("-");
        if (negative || s.StartsWith("+"))
            s = s[1..];

        var point = s.IndexOf('.');
        var whole = point >= 0 ? s[..point] : s;
        var fraction = point >= 0 ? s[(point + 1)..] : ReadOnlySpan<char>.Empty;

        if (whole.Length == 0 && fraction.Length == 0)
            return false;

        foreach (var c in whole)
            if (!char.IsAsciiDigit(c)) return false;

        foreach (var c in fraction)
            if (!char.IsAsciiDigit(c)) return false;

        var digits = BigInteger.Parse(string.Concat(whole.Length == 0 ? "0" : whole, fraction), CultureInfo.InvariantCulture);
        var scale = exponent - fraction.Length;
        var value = scale >= 0
            ? new BjoRational(digits * BigInteger.Pow(10, scale))
            : new BjoRational(digits, BigInteger.Pow(10, -scale));

        result = negative ? -value : value;
        return true;
    }

    public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out BjoRational result)
    {
        result = default;
        return s is not null && TryParse(s.AsSpan(), style, provider, out result);
    }

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out BjoRational result) =>
        TryParse(s, NumberStyles.Float, provider, out result);

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out BjoRational result) =>
        TryParse(s, NumberStyles.Float, provider, out result);

    public static BjoRational Parse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider) =>
        TryParse(s, style, provider, out var r) ? r : throw new FormatException($"'{s}' is not a rational");

    public static BjoRational Parse(string s, NumberStyles style, IFormatProvider? provider) =>
        Parse(s.AsSpan(), style, provider);

    public static BjoRational Parse(ReadOnlySpan<char> s, IFormatProvider? provider) =>
        Parse(s, NumberStyles.Float, provider);

    public static BjoRational Parse(string s, IFormatProvider? provider) =>
        Parse(s.AsSpan(), NumberStyles.Float, provider);
}
