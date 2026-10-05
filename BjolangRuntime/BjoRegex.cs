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
using BjoRx.Search;
using BjoRx.Syntax;
using BjoString;
using RxRegex = BjoRx.Meta.Regex;

namespace Bjolang.Runtime;

/// <summary>
/// What <c>(std rx)</c> binds: a compiled regular expression, and the matches
/// it produces.
/// </summary>
///
/// <remarks>
/// <para>
/// The pattern is a <see cref="HirText"/> tree, written by
/// <c>lib/std/rx.bjo</c> at compile time and never by a user, and the engine
/// is BjoRx: linear time, leftmost-first, over the string's UTF-8 as it is.
/// Its byte offsets are the string's cursors, so a match position needs no
/// conversion, and a match can only begin and end on a character boundary.
/// </para>
/// <para>
/// Only <c>($ ...)</c> and <c>(=> :name ...)</c> capture. They are numbered
/// in the order they open, from 1, which is BjoRx's group number; the
/// runtime's group index is one less, since group 0 is the whole match.
/// </para>
/// </remarks>
public sealed class BjoRegex
{
    internal readonly RxRegex Re;

    /// <summary>The Bjolang name of each group, by index. An unnamed group holds "".</summary>
    internal readonly string[] GroupNames;

    internal readonly string Pattern;

    private readonly Hir _hir;

    /// <summary>
    /// The same pattern anchored at both ends, built on first use.
    /// </summary>
    ///
    /// <remarks>
    /// A second regex rather than "search, then check the end": under
    /// leftmost-first, <c>(or "a" "ab")</c> searched in "ab" finds "a", which
    /// fails the check, while the anchored pattern finds "ab". Lazy, because a
    /// program that only ever searches should not pay to compile it.
    /// </remarks>
    private RxRegex? _whole;

    private BjoRegex(Hir hir, string names, string pattern)
    {
        _hir = hir;
        Re = RxRegex.Create(hir);
        GroupNames = SplitNames(names, Re.GroupInfo.GroupLength - 1);
        Pattern = pattern;
    }

    // A `#rx(...)` reaches here once per program, the first time it is
    // evaluated, because the macro hoists the compile. The same pattern written
    // in two places, and `rx-compile` called with a pattern built at run time,
    // still come here more than once. Keyed on both strings, since the group
    // names are part of what the value means.
    private static readonly ConcurrentDictionary<(string, string), BjoRegex> Cache = new();

    internal RxRegex Whole =>
        _whole ??= RxRegex.Create(Hir.Concat(Hir.Look(Look.Start), _hir, Hir.Look(Look.End)));

    internal static BjoRegex Compile(string pattern, string names) =>
        Cache.GetOrAdd((pattern, names), key =>
        {
            Hir hir;
            try
            {
                hir = HirText.Parse(key.Item1);
            }
            catch (FormatException e)
            {
                throw new ArgumentException(e.Message, nameof(pattern), e);
            }
            return new BjoRegex(hir, key.Item2, key.Item1);
        });

    // One name per group the pattern has. The list alone cannot say: "" is
    // no groups, and also one unnamed group.
    private static string[] SplitNames(string names, int groups)
    {
        var split = groups == 0 ? [] : names.Split(',');
        if (split.Length < groups) Array.Resize(ref split, groups);
        for (int i = 0; i < split.Length; i++) split[i] ??= "";
        return split;
    }

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

/// <summary>One match: the input it was found in, and its group spans.</summary>
///
/// <remarks>
/// The input is carried because a <see cref="StringCursor"/> does not carry the
/// string it indexes, and every span this hands out is a pair of cursors into
/// this particular input. <c>Slots</c> holds start and end byte offsets, group
/// 0 first, -1 for a group that took no part. They count from the start of the
/// input, which a slice does not share with its array, so they become cursors
/// through <c>CursorAt</c>.
/// </remarks>
public sealed class BjoMatch
{
    internal readonly BjoRegex Owner;
    internal readonly Utf8String Input;
    internal readonly int[] Slots;

    internal BjoMatch(BjoRegex owner, Utf8String input, int[] slots)
    {
        Owner = owner;
        Input = input;
        Slots = slots;
    }

    internal Utf8String TextOf(int start, int end) =>
        Input.Substring(Input.CursorAt(start), Input.CursorAt(end));

    public override string ToString() => "#<rx-match " + TextOf(Slots[0], Slots[1]) + ">";
}

/// <summary>What <c>(std rx)</c> imports.</summary>
public static class BjoRegexModule
{
    // --- Compiling ---------------------------------------------------------

    public static BjoRegex Compile(string pattern, string names) => BjoRegex.Compile(pattern, names);

    public static string Pattern(BjoRegex rx) => rx.Pattern;

    public static int GroupCount(BjoRegex rx) => rx.GroupNames.Length;

    // --- Searching ---------------------------------------------------------

    private static Input InputOf(Utf8String s) => new(s.AsMemory());

    /// <summary>The leftmost match at or after <paramref name="from"/>, with its groups, or null.</summary>
    private static BjoMatch? Captured(BjoRegex rx, RxRegex re, Utf8String s, int from)
    {
        var caps = re.CreateCaptures();
        return re.Captures(InputOf(s) with { Start = from }, caps) ? new BjoMatch(rx, s, caps.Slots) : null;
    }

    public static global::BjolangRuntime.Option<BjoMatch> Search(BjoRegex rx, Utf8String input) =>
        Captured(rx, rx.Re, input, 0) is { } m
            ? global::BjolangRuntime.Some(m)
            : global::BjolangRuntime.None<BjoMatch>();

    public static bool IsSearchMatch(BjoRegex rx, Utf8String input) => rx.Re.IsMatch(InputOf(input));

    public static global::BjolangRuntime.Option<BjoMatch> MatchWhole(BjoRegex rx, Utf8String input) =>
        Captured(rx, rx.Whole, input, 0) is { } m
            ? global::BjolangRuntime.Some(m)
            : global::BjolangRuntime.None<BjoMatch>();

    public static bool IsWholeMatch(BjoRegex rx, Utf8String input) => rx.Whole.IsMatch(InputOf(input));

    /// <summary>The offset after the character at <paramref name="index"/>, or past the end.</summary>
    private static int NextScalar(ReadOnlySpan<byte> s, int index)
    {
        if (index >= s.Length) return s.Length + 1;
        byte lead = s[index];
        return index + (lead < 0x80 ? 1 : lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4);
    }

    /// <summary>
    /// Every non-overlapping match, left to right. An empty match moves the
    /// next search one character on, so it cannot be found again, and an
    /// empty match right after another match is found as well.
    /// </summary>
    private static IEnumerable<Match> AllMatches(BjoRegex rx, Utf8String s)
    {
        var input = InputOf(s);
        int pos = 0;
        while (pos <= s.ByteLength)
        {
            if (rx.Re.Find(input with { Start = pos }) is not { } m) yield break;
            yield return m;
            pos = m.IsEmpty ? NextScalar(s.AsSpan(), m.Start) : m.End;
        }
    }

    public static BjoMatch[] Matches(BjoRegex rx, Utf8String input)
    {
        var found = new List<BjoMatch>();
        int pos = 0;
        while (pos <= input.ByteLength && Captured(rx, rx.Re, input, pos) is { } m)
        {
            found.Add(m);
            pos = m.Slots[0] == m.Slots[1] ? NextScalar(input.AsSpan(), m.Slots[0]) : m.Slots[1];
        }
        return found.ToArray();
    }

    // --- Rewriting ---------------------------------------------------------
    //
    // The replacement is literal: no `$1`, which is a substitution syntax
    // Bjolang would then have inherited without deciding to. A split returns
    // the pieces only, never the groups.

    public static Utf8String Replace(BjoRegex rx, Utf8String input, Utf8String replacement)
    {
        var sb = new Utf8StringBuilder(input.ByteLength);
        int last = 0;
        foreach (Match m in AllMatches(rx, input))
        {
            sb.Append(input.Slice(input.CursorAt(last), input.CursorAt(m.Start))).Append(replacement);
            last = m.End;
        }
        return sb.Append(input.Slice(input.CursorAt(last), StringCursor.End(input))).ToUtf8String();
    }

    public static Utf8String ReplaceWith(BjoRegex rx, Utf8String input, Func<BjoMatch, Utf8String> f)
    {
        var sb = new Utf8StringBuilder(input.ByteLength);
        int last = 0;
        foreach (BjoMatch m in Matches(rx, input))
        {
            sb.Append(input.Slice(input.CursorAt(last), input.CursorAt(m.Slots[0]))).Append(f(m));
            last = m.Slots[1];
        }
        return sb.Append(input.Slice(input.CursorAt(last), StringCursor.End(input))).ToUtf8String();
    }

    public static Utf8String[] Split(BjoRegex rx, Utf8String input)
    {
        var pieces = new List<Utf8String>();
        int last = 0;
        foreach (Match m in AllMatches(rx, input))
        {
            // A separator that matched nothing would split between every pair
            // of characters and put the whole input back as single characters.
            if (m.IsEmpty) continue;
            pieces.Add(input.Substring(input.CursorAt(last), input.CursorAt(m.Start)));
            last = m.End;
        }
        pieces.Add(input.Substring(input.CursorAt(last), StringCursor.End(input)));
        return pieces.ToArray();
    }

    // --- Reading a match ---------------------------------------------------

    public static Utf8String Text(BjoMatch m) => m.TextOf(m.Slots[0], m.Slots[1]);

    public static Utf8String Input(BjoMatch m) => m.Input;

    public static StringCursor Start(BjoMatch m) => m.Input.CursorAt(m.Slots[0]);

    public static StringCursor End(BjoMatch m) => m.Input.CursorAt(m.Slots[1]);

    public static int Count(BjoMatch m) => m.Owner.GroupNames.Length;

    /// <summary>The slot of group <paramref name="index"/>'s start, or -1 when it took no part.</summary>
    private static int GroupSlot(BjoMatch m, int index)
    {
        if (index < 0 || index >= m.Owner.GroupNames.Length) return -1;
        int slot = 2 * (index + 1);
        return slot + 1 < m.Slots.Length && m.Slots[slot] >= 0 ? slot : -1;
    }

    public static global::BjolangRuntime.Option<Utf8String> GroupText(BjoMatch m, int index)
    {
        int slot = GroupSlot(m, index);
        return slot < 0
            ? global::BjolangRuntime.None<Utf8String>()
            : global::BjolangRuntime.Some(m.TextOf(m.Slots[slot], m.Slots[slot + 1]));
    }

    public static global::BjolangRuntime.Option<StringCursor> GroupStart(BjoMatch m, int index)
    {
        int slot = GroupSlot(m, index);
        return slot < 0
            ? global::BjolangRuntime.None<StringCursor>()
            : global::BjolangRuntime.Some(m.Input.CursorAt(m.Slots[slot]));
    }

    public static global::BjolangRuntime.Option<StringCursor> GroupEnd(BjoMatch m, int index)
    {
        int slot = GroupSlot(m, index);
        return slot < 0
            ? global::BjolangRuntime.None<StringCursor>()
            : global::BjolangRuntime.Some(m.Input.CursorAt(m.Slots[slot + 1]));
    }

    /// <summary>The number of the group the user named, or -1.</summary>
    public static int IndexOfName(BjoRegex rx, string name) => rx.IndexOfName(name);

    public static int MatchIndexOfName(BjoMatch m, string name) => m.Owner.IndexOfName(name);
}
