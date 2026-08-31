using System.Globalization;
using System.Text;

namespace Bjolang.Runtime;

/// <summary>
/// Numbers read and written the same way wherever the program runs.
/// </summary>
///
/// <remarks>
/// <para>
/// <c>double.Parse</c> and <c>double.ToString</c> both take the ambient
/// culture, so <c>1.5</c> is unreadable and unwritable where the decimal
/// separator is a comma. Every conversion Bjolang offers goes through here
/// instead, which means a document written by one program is readable by the
/// next.
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
