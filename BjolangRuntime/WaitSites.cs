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

using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Bjolang.Runtime;

/// <summary>
/// The place in Bjolang source that a stack points at, written for a person.
///
/// Used only when a report is made: when a deadline report is read, and when
/// the fiber trace is dumped. Nothing here runs on a path that a program
/// takes when it works.
/// </summary>
[NonUserCode]
internal static class WaitSites
{
    /// <summary>
    /// The namespace prefix of the standard library's modules. A frame there
    /// is shown only if no frame of the program's own modules is on the stack,
    /// because "prelude.bjo:4400" does not tell a user which of their own
    /// calls was waiting.
    /// </summary>
    private const string StdPrefix = "BjoMod.std.";

    /// <summary>
    /// The innermost frame of <paramref name="trace"/> in a .bjo file, and of
    /// the program's own modules if there is one. Null when no frame has a
    /// .bjo file, which is the case when the program has no pdb.
    /// </summary>
    internal static StackFrame? UserFrame(StackTrace trace)
    {
        StackFrame? firstBjo = null;
        foreach (var frame in trace.GetFrames())
        {
            var file = frame.GetFileName();
            if (file is null || !file.EndsWith(".bjo", System.StringComparison.Ordinal)) continue;
            firstBjo ??= frame;
            var ns = frame.GetMethod()?.DeclaringType?.Namespace;
            if (ns is null || !ns.StartsWith(StdPrefix, System.StringComparison.Ordinal)) return frame;
        }
        return firstBjo;
    }

    /// <summary>
    /// "file.bjo:12", with the file relative to the working directory when it
    /// is below it.
    /// </summary>
    internal static string Location(string file, int line)
    {
        var shown = file;
        try
        {
            var relative = System.IO.Path.GetRelativePath(System.Environment.CurrentDirectory, file);
            if (!relative.StartsWith("..", System.StringComparison.Ordinal)) shown = relative;
        }
        catch (System.Exception) { }
        return $"{shown}:{line}";
    }

    /// <summary>
    /// The text of line <paramref name="line"/> of <paramref name="file"/>,
    /// trimmed and shortened, or null when the file cannot be read.
    /// <paramref name="cache"/> keeps the files read during one report.
    /// </summary>
    internal static string? SourceLine(string file, int line, Dictionary<string, string[]?> cache)
    {
        if (!cache.TryGetValue(file, out var lines))
        {
            try { lines = System.IO.File.ReadAllLines(file); }
            catch (System.Exception) { lines = null; }
            cache[file] = lines;
        }
        if (lines is null || line < 1 || line > lines.Length) return null;

        var text = lines[line - 1].Trim();
        const int Max = 72;
        return text.Length > Max ? text.Substring(0, Max - 3) + "..." : text;
    }

    /// <summary>
    /// One site as it is shown: the location, and the source line after it.
    /// </summary>
    internal static string Describe(string file, int line, Dictionary<string, string[]?> cache)
    {
        var location = Location(file, line);
        var text = SourceLine(file, line, cache);
        return text is null ? location : $"{location}  {text}";
    }

    /// <summary>
    /// Where the fiber that raised <paramref name="e"/> was waiting, read off
    /// the stack the exception was raised with.
    /// </summary>
    internal static string Of(System.Exception e, Dictionary<string, string[]?> cache)
    {
        var frame = UserFrame(new StackTrace(e, fNeedFileInfo: true));
        if (frame is null) return "(no Bjolang source on its stack)";
        return Describe(frame.GetFileName()!, frame.GetFileLineNumber(), cache);
    }

    /// <summary>
    /// Sites, each once and with how many fibers wait there, in the order they
    /// were first seen. Writes nothing for an empty list.
    /// </summary>
    internal static void AppendCounted(StringBuilder sb, IEnumerable<string> sites, string indent)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>();
        foreach (var site in sites)
        {
            if (counts.TryGetValue(site, out var n)) counts[site] = n + 1;
            else { counts[site] = 1; order.Add(site); }
        }

        foreach (var site in order)
        {
            sb.Append('\n').Append(indent);
            int n = counts[site];
            sb.Append(n == 1 ? "1 fiber   at " : $"{n} fibers  at ");
            sb.Append(site);
        }
    }
}
