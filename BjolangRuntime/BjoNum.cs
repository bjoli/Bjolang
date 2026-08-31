using System.Globalization;

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
}
