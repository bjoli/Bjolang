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

using System.Text;
using System.Text.RegularExpressions;

namespace Bjolang.Runtime;

/// <summary>
/// A canonical text form of read syntax, for comparing two readers.
/// </summary>
/// <remarks>
/// - `bjolang --dump-read FILE` writes the compiler's reader output with this.
///   `(std read)` writes its own with this. Both use one printer, so a
///   difference in the text is a difference in what was read.
/// - Each node is on its own line, indented by its depth, with its kind, its
///   payload, its range as `line:column-line:column`, and `resolved` when its
///   origin is <see cref="SyntaxOrigin.Resolved"/>.
/// - The file of a range is not written. The module of a range is not written.
/// - A fresh name of the shape `&amp;N__M`, which `#(...)` makes, is written as
///   `&amp;N__gK`, where K counts the distinct fresh names in order of first
///   appearance. Two readers with different counters then write the same text.
/// </remarks>
[NonUserCode]
public static class SyntaxDump
{
    private static readonly Regex FreshName = new(@"^&\d+__\d+$", RegexOptions.Compiled);

    /// Returns the dump of `forms`, one line per node.
    public static BjoString.Utf8String Dump(global::SchemeList.SchemeList<Syntax> forms)
    {
        var sb = new StringBuilder();
        var fresh = new Dictionary<string, string>();
        foreach (var form in forms)
            Write(sb, form, 0, fresh);
        return BjoString.Utf8String.FromUtf16(sb.ToString());
    }

    private static void Write(StringBuilder sb, Syntax s, int depth, Dictionary<string, string> fresh)
    {
        sb.Append(' ', depth * 2);
        switch (s)
        {
            case Syntax.SSym sym:
                sb.Append("sym ").Append(Quote(Rename(sym.Item1.Name, fresh)));
                break;
            case Syntax.SDatum d:
                sb.Append("datum ").Append(Quote(d.Item1.Name));
                break;
            case Syntax.SInt n:
                sb.Append("num ").Append(Quote(n.Item1.ToString()));
                break;
            case Syntax.SStr str:
                sb.Append("str ").Append(Quote(str.Item1.ToString()));
                break;
            case Syntax.SChar c:
                sb.Append("char ").Append(c.Item1.Value);
                break;
            case Syntax.SKey k:
                sb.Append("key ").Append(Quote(k.Item1.Name));
                break;
            case Syntax.SPunct p:
                sb.Append("punct ").Append(Quote(p.Item1.ToString()));
                break;
            case Syntax.SList:
                sb.Append("list");
                break;
        }

        var r = s.Range;
        sb.Append(' ').Append(r.StartLine).Append(':').Append(r.StartColumn)
          .Append('-').Append(r.EndLine).Append(':').Append(r.EndColumn);
        if (s.Origin == SyntaxOrigin.Resolved)
            sb.Append(" resolved");
        sb.Append('\n');

        if (s is Syntax.SList list)
            foreach (var item in list.Item1)
                Write(sb, item, depth + 1, fresh);
    }

    private static string Rename(string name, Dictionary<string, string> fresh)
    {
        if (!FreshName.IsMatch(name))
            return name;
        if (!fresh.TryGetValue(name, out var renamed))
        {
            renamed = $"{name[..name.IndexOf("__", StringComparison.Ordinal)]}__g{fresh.Count + 1}";
            fresh[name] = renamed;
        }
        return renamed;
    }

    /// Returns `text` in double quotes, with `\`, `"` and every character below
    /// U+0020 written as an escape.
    private static string Quote(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < ' ') sb.Append($"\\u{{{(int)c:X}}}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
