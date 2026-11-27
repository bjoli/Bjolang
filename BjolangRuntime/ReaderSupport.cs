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

using System.Text.RegularExpressions;

namespace Bjolang.Runtime;

/// <summary>
/// The parts of the lexer that the compiler's Lexer.fs and (std read) both
/// call, so that the two readers use the same regular expressions and the
/// same case mapping.
/// </summary>
[NonUserCode]
public static class ReaderSupport
{
    /// The number at the start of a quantity literal: `9.81`, `-2`, `1e-3`,
    /// `1|3`, `.5`.
    private static readonly Regex QuantityNumberRegex =
        new(@"^-?(\d+(\.\d*)?|\.\d+)([eE][-+]?\d+)?(\|\d+)?", RegexOptions.Compiled);

    /// What a number literal may be spelled as: the shapes and suffixes
    /// `TypedAST.NumericLiteral` reads. Hexadecimal and binary digits take only
    /// the `u` and `l` suffixes. A real has a digit after its point. A real
    /// takes `d` and `M`; an integer also takes `I` for a bigint.
    private static readonly Regex NumberSpellingRegex = new(
        "^-?(?:"
        + "0[xX][0-9a-fA-F]+(?:[uU][lL]?|[lL])?"
        + "|0[bB][01]+(?:[uU][lL]?|[lL])?"
        + "|[0-9]+(?:uy|us|[uU][lL]?|s|[lL]|[dD]|[mM]|I)?"
        + "|[0-9]+(?:\\.[0-9]+(?:[eE][-+]?[0-9]+)?|[eE][-+]?[0-9]+)[dDmM]?"
        + ")$",
        RegexOptions.Compiled);

    /// Returns the number at the start of the body of a quantity literal,
    /// `9.81` of `9.81 m/s^2`, or "" when it starts with none.
    public static string QuantityNumber(string body) => QuantityNumberRegex.Match(body).Value;

    /// Returns the unit of the body of a quantity literal: what follows its
    /// number, trimmed.
    public static string QuantityUnit(string body) =>
        body.Substring(QuantityNumberRegex.Match(body).Value.Length).Trim();

    /// Returns whether `text` is spelled as a number literal.
    public static bool IsNumberSpelling(string text) => NumberSpellingRegex.IsMatch(text);

    /// Returns `text` in lower case, by the invariant culture.
    public static string LowerInvariant(string text) => text.ToLowerInvariant();
}
