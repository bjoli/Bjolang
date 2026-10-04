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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using BjoString;

namespace Bjolang.Runtime;

/// <summary>
/// What <c>(std rx)</c> binds: a compiled regular expression, and the matches
/// it produces.
/// </summary>
///
/// <remarks>
/// <para>
/// The pattern string is written by <c>lib/std/rx.bjo</c> at compile time and
/// never by a user, which is what every choice below depends on. That compiler
/// emits no <c>.</c>, no <c>^</c>, no <c>$</c>, no <c>\d</c>, <c>\w</c>,
/// <c>\s</c> or <c>\p{...}</c>, and no character class containing a surrogate
/// code point — so the meaning of a pattern is fixed by Bjolang's source rather
/// than by the .NET runtime's Unicode tables, which change between releases.
/// </para>
/// <para>
/// Options are not configurable. <c>NonBacktracking</c> gives linear time in
/// the length of the input and, more importantly here, refuses backreferences,
/// lookarounds, atomic groups and conditionals — the constructs that would tie
/// Bjolang to a backtracking engine forever. <c>ExplicitCapture</c> makes every
/// unnamed group non-capturing, which removes .NET's rule that unnamed groups
/// are numbered before named ones. <c>CultureInvariant</c> keeps the ambient
/// culture out of a compiled program's meaning.
/// </para>
/// <para>
/// Every position handed to Bjolang becomes a <see cref="StringCursor"/>, whose
/// invariant is that it sits on a character boundary. A .NET match index is an
/// offset into UTF-16 storage and carries no such promise, so
/// <see cref="Cursor"/> checks it. The check can only fire on a pattern this
/// assembly did not write, and it throws rather than returning a cursor that
/// would decode as U+FFFD.
/// </para>
/// </remarks>
public sealed class BjoRegex
{
    private const RegexOptions Options =
        RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant;

    internal readonly Regex Re;

    /// <summary>The Bjolang name of group <c>g0</c>, <c>g1</c>, … by index.</summary>
    ///
    /// <remarks>
    /// .NET group names must be word characters, and Bjolang names are not —
    /// <c>area-code</c> and <c>ok?</c> are both legal there and neither is here.
    /// So the emitted pattern names its groups <c>g0</c> upwards and the names
    /// the user wrote live in this array. An unnamed group holds "".
    /// </remarks>
    internal readonly string[] GroupNames;

    internal readonly string Pattern;

    /// <summary>
    /// The same pattern anchored at both ends, built on first use.
    /// </summary>
    ///
    /// <remarks>
    /// A second <c>Regex</c> rather than a flag, because .NET has no
    /// "match the whole input" mode: anchoring is part of the pattern. Lazy,
    /// because a program that only ever searches should not pay to compile it.
    /// </remarks>
    private Regex? _whole;

    private BjoRegex(Regex re, string[] groupNames, string pattern)
    {
        Re = re;
        GroupNames = groupNames;
        Pattern = pattern;
    }

    // A `#rx(...)` reaches here once per program, the first time it is
    // evaluated, because the macro hoists the compile. The same pattern written
    // in two places, and `rx-compile` called with a pattern built at run time,
    // still come here more than once. Keyed on both strings, since the group
    // names are part of what the value means.
    private static readonly ConcurrentDictionary<(string, string), BjoRegex> Cache = new();

    internal Regex Whole => _whole ??= new Regex(@"\A(?:" + Pattern + @")\z", Options);

    internal static BjoRegex Compile(string pattern, string names) =>
        Cache.GetOrAdd((pattern, names), key =>
            new BjoRegex(new Regex(key.Item1, Options), SplitNames(key.Item2), key.Item1));

    private static string[] SplitNames(string names) =>
        names.Length == 0 ? Array.Empty<string>() : names.Split(',');

    internal int IndexOfName(string name)
    {
        for (int i = 0; i < GroupNames.Length; i++)
        {
            if (GroupNames[i] == name) return i;
        }
        return -1;
    }

    public override string ToString() => "#<regex " + Pattern + ">";
}

/// <summary>
/// A Bjolang string searched as the UTF-16 text .NET's regex engine reads, and
/// the UTF-8 byte offsets its match positions correspond to.
/// </summary>
///
/// <remarks>
/// The bridge until <c>(std rx)</c> runs on BjoRx, which searches UTF-8
/// directly. The offsets are built on the first position asked for, once per
/// input, and every match found in that input shares them.
/// </remarks>
internal sealed class Searched
{
    internal readonly Utf8String Input;
    internal readonly string Text;
    private int[]? _offsets;

    internal Searched(Utf8String input)
    {
        Input = input;
        Text = input.ToString();
    }

    /// <summary>The cursor at UTF-16 index <paramref name="index"/>.</summary>
    ///
    /// <remarks>
    /// A pattern emitted by <c>lib/std/rx.bjo</c> cannot begin or end a match
    /// inside a surrogate pair: no class it writes contains a surrogate, an
    /// astral literal is emitted as its whole pair, and the iteration below
    /// advances a scalar at a time. So this throws rather than clamping — a
    /// cursor that is off a boundary is a bug in the emitter.
    /// </remarks>
    internal StringCursor Cursor(int index)
    {
        if (index > 0 && index < Text.Length && char.IsLowSurrogate(Text[index]))
        {
            throw new InvalidOperationException(
                $"rx: a match boundary at {index} falls inside a surrogate pair.");
        }
        return new StringCursor(Offsets()[index]);
    }

    // The byte offset of every UTF-16 index: a pair is four bytes, counted at
    // its high surrogate, and its low surrogate's entry is never asked for.
    private int[] Offsets()
    {
        if (_offsets is not null) return _offsets;
        var offsets = new int[Text.Length + 1];
        int bytes = 0;
        for (int i = 0; i < Text.Length; i++)
        {
            offsets[i] = bytes;
            char c = Text[i];
            bytes += c < 0x80 ? 1
                : c < 0x800 ? 2
                : char.IsHighSurrogate(c) && i + 1 < Text.Length && char.IsLowSurrogate(Text[i + 1]) ? 4
                : char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(Text[i - 1]) ? 0
                : 3;
        }
        offsets[Text.Length] = bytes;
        return _offsets = offsets;
    }
}

/// <summary>One match: the input it was found in, and where.</summary>
///
/// <remarks>
/// The input is carried because a <see cref="StringCursor"/> does not carry the
/// string it indexes, and every span this hands out is a pair of cursors into
/// this particular input.
/// </remarks>
public sealed class BjoMatch
{
    internal readonly BjoRegex Owner;
    internal readonly Searched Input;
    internal readonly Match M;

    internal BjoMatch(BjoRegex owner, Searched input, Match m)
    {
        Owner = owner;
        Input = input;
        M = m;
    }

    public override string ToString() => "#<rx-match " + M.Value + ">";
}

/// <summary>What <c>(std rx)</c> imports.</summary>
public static class BjoRegexModule
{
    // --- Compiling ---------------------------------------------------------

    public static BjoRegex Compile(string pattern, string names) => BjoRegex.Compile(pattern, names);

    public static string Pattern(BjoRegex rx) => rx.Pattern;

    public static int GroupCount(BjoRegex rx) => rx.GroupNames.Length;

    // --- Positions ---------------------------------------------------------

    /// <summary>The offset after the scalar at <paramref name="index"/>.</summary>
    ///
    /// <remarks>
    /// How an empty match advances. Stepping one UTF-16 unit would put the next
    /// search inside a surrogate pair, and an empty match found there would be
    /// a cursor off a character boundary.
    /// </remarks>
    private static int NextScalar(string input, int index)
    {
        if (index + 1 < input.Length && char.IsHighSurrogate(input[index]) && char.IsLowSurrogate(input[index + 1]))
        {
            return index + 2;
        }
        return index + 1;
    }

    // --- Searching ---------------------------------------------------------

    public static global::BjolangRuntime.Option<BjoMatch> Search(BjoRegex rx, Utf8String input)
    {
        var searched = new Searched(input);
        Match m = rx.Re.Match(searched.Text);
        return m.Success
            ? global::BjolangRuntime.Some(new BjoMatch(rx, searched, m))
            : global::BjolangRuntime.None<BjoMatch>();
    }

    public static bool IsSearchMatch(BjoRegex rx, Utf8String input) => rx.Re.IsMatch(input.ToString());

    public static global::BjolangRuntime.Option<BjoMatch> MatchWhole(BjoRegex rx, Utf8String input)
    {
        var searched = new Searched(input);
        Match m = rx.Whole.Match(searched.Text);
        return m.Success
            ? global::BjolangRuntime.Some(new BjoMatch(rx, searched, m))
            : global::BjolangRuntime.None<BjoMatch>();
    }

    public static bool IsWholeMatch(BjoRegex rx, Utf8String input) => rx.Whole.IsMatch(input.ToString());

    /// <summary>Every non-overlapping match, left to right.</summary>
    private static List<Match> AllMatches(BjoRegex rx, string input)
    {
        var found = new List<Match>();
        int pos = 0;
        while (pos <= input.Length)
        {
            Match m = rx.Re.Match(input, pos);
            if (!m.Success) break;
            found.Add(m);
            // An empty match would otherwise be found at the same place
            // forever. Advancing by a scalar rather than a unit is what keeps
            // the next search on a character boundary.
            pos = m.Length == 0 ? NextScalar(input, m.Index) : m.Index + m.Length;
        }
        return found;
    }

    public static BjoMatch[] Matches(BjoRegex rx, Utf8String input)
    {
        var searched = new Searched(input);
        List<Match> found = AllMatches(rx, searched.Text);
        var result = new BjoMatch[found.Count];
        for (int i = 0; i < found.Count; i++) result[i] = new BjoMatch(rx, searched, found[i]);
        return result;
    }

    // --- Rewriting ---------------------------------------------------------
    //
    // Both are written out by hand rather than through `Regex.Replace` and
    // `Regex.Split`. `Replace` would read `$1` and `$&` in the replacement,
    // which is a substitution syntax Bjolang would then have inherited without
    // deciding to, and `Split` returns captured groups interleaved with the
    // pieces. Doing the walk here keeps both meanings Bjolang's own.

    public static Utf8String Replace(BjoRegex rx, Utf8String input, Utf8String replacement)
    {
        string text = input.ToString();
        string with = replacement.ToString();
        var sb = new StringBuilder();
        int last = 0;
        foreach (Match m in AllMatches(rx, text))
        {
            sb.Append(text, last, m.Index - last).Append(with);
            last = m.Index + m.Length;
        }
        return Utf8String.FromUtf16(sb.Append(text, last, text.Length - last).ToString());
    }

    public static Utf8String ReplaceWith(BjoRegex rx, Utf8String input, Func<BjoMatch, Utf8String> f)
    {
        var searched = new Searched(input);
        string text = searched.Text;
        var sb = new StringBuilder();
        int last = 0;
        foreach (Match m in AllMatches(rx, text))
        {
            sb.Append(text, last, m.Index - last).Append(f(new BjoMatch(rx, searched, m)).ToString());
            last = m.Index + m.Length;
        }
        return Utf8String.FromUtf16(sb.Append(text, last, text.Length - last).ToString());
    }

    public static Utf8String[] Split(BjoRegex rx, Utf8String input)
    {
        string text = input.ToString();
        var pieces = new List<Utf8String>();
        int last = 0;
        foreach (Match m in AllMatches(rx, text))
        {
            // A separator that matched nothing would split between every pair
            // of characters and put the whole input back as single characters.
            if (m.Length == 0) continue;
            pieces.Add(Utf8String.FromUtf16(text.AsSpan(last, m.Index - last)));
            last = m.Index + m.Length;
        }
        pieces.Add(Utf8String.FromUtf16(text.AsSpan(last)));
        return pieces.ToArray();
    }

    // --- Reading a match ---------------------------------------------------

    public static Utf8String Text(BjoMatch m) => Utf8String.FromUtf16(m.M.Value);

    public static Utf8String Input(BjoMatch m) => m.Input.Input;

    public static StringCursor Start(BjoMatch m) => m.Input.Cursor(m.M.Index);

    public static StringCursor End(BjoMatch m) => m.Input.Cursor(m.M.Index + m.M.Length);

    public static int Count(BjoMatch m) => m.Owner.GroupNames.Length;

    private static Group? GroupAt(BjoMatch m, int index)
    {
        if (index < 0 || index >= m.Owner.GroupNames.Length) return null;
        Group g = m.M.Groups["g" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        return g.Success ? g : null;
    }

    public static global::BjolangRuntime.Option<Utf8String> GroupText(BjoMatch m, int index)
    {
        Group? g = GroupAt(m, index);
        return g is null
            ? global::BjolangRuntime.None<Utf8String>()
            : global::BjolangRuntime.Some(Utf8String.FromUtf16(g.Value));
    }

    public static global::BjolangRuntime.Option<StringCursor> GroupStart(BjoMatch m, int index)
    {
        Group? g = GroupAt(m, index);
        return g is null
            ? global::BjolangRuntime.None<StringCursor>()
            : global::BjolangRuntime.Some(m.Input.Cursor(g.Index));
    }

    public static global::BjolangRuntime.Option<StringCursor> GroupEnd(BjoMatch m, int index)
    {
        Group? g = GroupAt(m, index);
        return g is null
            ? global::BjolangRuntime.None<StringCursor>()
            : global::BjolangRuntime.Some(m.Input.Cursor(g.Index + g.Length));
    }

    /// <summary>The number of the group the user named, or -1.</summary>
    public static int IndexOfName(BjoRegex rx, string name) => rx.IndexOfName(name);

    public static int MatchIndexOfName(BjoMatch m, string name) => m.Owner.IndexOfName(name);
}
