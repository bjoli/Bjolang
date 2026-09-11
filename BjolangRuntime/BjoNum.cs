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
public static class BjoNum {
    public static double ParseDouble(string s) =>
        double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static string DoubleToString(double d) =>
        d.ToString(CultureInfo.InvariantCulture);

    // Integers too. `sv-SE` writes its minus as U+2212 MINUS SIGN, so
    // `(-100).ToString()` there is not a string any parser expects — it is not
    // even ASCII.
    public static int ParseInt(string s) =>
        int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

    public static string IntToString(int n) =>
        n.ToString(CultureInfo.InvariantCulture);

    public static string LongToString(long n) =>
        n.ToString(CultureInfo.InvariantCulture);

    public static string ByteToString(byte n) =>
        n.ToString(CultureInfo.InvariantCulture);

    /// The `->str` fallback: whatever a type with no implementation of its own
    /// says about itself, asked in the invariant culture. Reaches the numeric
    /// types the prelude names no conversion for, and every `IFormattable`.
    public static string ToStringInvariant(object? o) =>
        System.Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";

    // A scanner that has just spelled a number into a builder wants the number,
    // not the string. `ToString` allocates one per number read; these copy into
    // the stack instead.
    //
    // The bound is longer than any number worth writing. Past it the digits
    // cannot change the answer, but they are still copied, because a number is
    // allowed to be as long as it likes and refusing one here would be a
    // parse error invented by an optimisation.
    private const int Stacked = 512;

    public static double ParseDouble(StringBuilder b) {
        char[]? spilled = null;
        Span<char> chars = b.Length <= Stacked ? stackalloc char[Stacked]
                                               : (spilled = new char[b.Length]);
        b.CopyTo(0, chars, b.Length);
        return double.Parse(chars[..b.Length], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// Overflows rather than saturating, so a number too wide for a long can be
    /// caught and read again as a double.
    public static long ParseLong(StringBuilder b) {
        char[]? spilled = null;
        Span<char> chars = b.Length <= Stacked ? stackalloc char[Stacked]
                                               : (spilled = new char[b.Length]);
        b.CopyTo(0, chars, b.Length);
        return long.Parse(chars[..b.Length], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }
}
