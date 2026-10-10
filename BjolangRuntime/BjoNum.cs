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
public sealed class NullCaseAttribute(string name) : Attribute {
    public string Name { get; } = name;
}

/// <summary>The name of <typeparamref name="T"/>'s null case, read once.</summary>
public static class NullCase<T> {
    public static readonly string? Name =
        (Attribute.GetCustomAttribute(typeof(T), typeof(NullCaseAttribute), false) as NullCaseAttribute)?.Name;
}

public static class BjoNum {
    public static double ParseDouble(Utf8String s) => Utf8Number.ParseDouble(s);

    public static Utf8String DoubleToString(double d) => Utf8Number.Format(d);

    // Integers too. `sv-SE` writes its minus as U+2212 MINUS SIGN, so
    // `(-100).ToString()` there is not a string any parser expects — it is not
    // even ASCII.
    public static int ParseInt(Utf8String s) => Utf8Number.ParseInt(s);

    public static Utf8String IntToString(int n) => Utf8Number.Format(n);

    public static Utf8String LongToString(long n) => Utf8Number.Format(n);

    public static Utf8String ByteToString(byte n) => Utf8Number.Format(n);

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

    /// The `->str` fallback at a known type: as <see cref="ToStringInvariant"/>,
    /// except that `null` at a union whose empty case is `null` is that case's
    /// name. The type is what says which name, as the value has none.
    public static Utf8String Show<T>(T value) => Utf8String.FromUtf16(ShowField(value));

    /// What a union case's record text writes for one of its fields.
    public static string ShowField<T>(T value) =>
        value is null ? NullCase<T>.Name ?? ""
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

    /// The `->str` fallback: whatever a type with no implementation of its own
    /// says about itself, asked in the invariant culture. Reaches the numeric
    /// types the prelude names no conversion for, and every `IFormattable`.
    public static Utf8String ToStringInvariant(object? o) =>
        Utf8String.FromUtf16(System.Convert.ToString(o, CultureInfo.InvariantCulture) ?? "");

    // A scanner that has just spelled a number into a builder wants the number,
    // not the string. These parse the builder's bytes where they are.
    public static double ParseDouble(Utf8StringBuilder b) => Utf8Number.ParseDouble(b.AsSpan());

    /// Overflows rather than saturating, so a number too wide for a long can be
    /// caught and read again as a double.
    public static long ParseLong(Utf8StringBuilder b) => Utf8Number.ParseLong(b.AsSpan());
}
