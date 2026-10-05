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

using System.Runtime.CompilerServices;
using BjoString;
using Collections;

namespace Bjolang.Runtime;

// Cursors for walking part of a `Vec`, forwards or backwards.
//
// `RrbList<T>` already knows how to do both: `RangeEnumerator` and
// `ReverseEnumerator` walk leaf arrays, so an element costs an index increment
// and an array read, where `vec-ref` per index costs a descent of the tree.
// That is the whole reason these exist rather than an `int` cursor and
// `vec-ref`, which is what the array iterators use and what an array makes
// cheap.
//
// Shaped like `BjolangRuntime.VecCursor`: the enumerator is a struct, and a
// struct copied into a call has its `MoveNext` advance the copy, so the cursor
// is a class holding one as a *field* — one allocation per walk, none per
// element, no boxing. `done?` is what advances, which the `Iterable` protocol
// allows: it is called once per iteration, before `current`, and nothing
// peeks. `next` is then the identity.

/// A forward walk of `count` elements from `from`.
public sealed class VecWalkCursor<T> {
    public RrbEnumerator<T> E;

    public VecWalkCursor(RrbList<T> list, int from, int count) {
        E = new RrbEnumerator<T>(list, from, count);
    }
}

/// A backward walk of `count` elements, starting at `last` and going down.
///
/// `last` is inclusive and may be -1, which with a count of 0 is the empty
/// walk — the shape a caller reaches for an empty slice, and the reason this
/// calls the constructor rather than `RrbList.ReverseEnumerator`, which
/// refuses a negative index.
public sealed class VecBackCursor<T> {
    public RrbReverseEnumerator<T> E;

    public VecBackCursor(RrbList<T> list, int last, int count) {
        E = new RrbReverseEnumerator<T>(list, last, count);
    }
}

public static class VecWalk {
    public static VecWalkCursor<T> Forward<T>(RrbList<T> list, int from, int count) =>
        new VecWalkCursor<T>(list, from, count);

    public static bool ForwardDone<T>(VecWalkCursor<T> cursor) => !cursor.E.MoveNext();

    public static T ForwardCurrent<T>(VecWalkCursor<T> cursor) => cursor.E.Current;

    public static VecBackCursor<T> Backward<T>(RrbList<T> list, int last, int count) =>
        new VecBackCursor<T>(list, last, count);

    public static bool BackwardDone<T>(VecBackCursor<T> cursor) => !cursor.E.MoveNext();

    public static T BackwardCurrent<T>(VecBackCursor<T> cursor) => cursor.E.Current;
}

// The walk `(:for ch s)` takes over a string.
//
// `string-cursor-*` take the string at every step, and each one asks again
// whether it is a whole array or a slice of one, which the JIT does not hoist
// out of a loop. This takes the array and the bounds once, and decodes each
// character as it steps onto it, so that `current` is a field.
//
// The fields are private and only `Start` and `Next` make one, so a walk is
// always on a character boundary of its own array: what lets its reads go
// unchecked, and what keeps a walk from being anything but a walk. A default
// one is at the end.

/// The character a walk is on, and where the next one begins.
public readonly struct StringWalk {
    private readonly byte[]? _bytes;
    private readonly int _next;
    private readonly int _end;
    private readonly BjoChar _current;
    private readonly bool _more;

    private StringWalk(byte[]? bytes, int next, int end, BjoChar current, bool more) {
        _bytes = bytes;
        _next = next;
        _end = end;
        _current = current;
        _more = more;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static StringWalk At(byte[]? bytes, int i, int end) {
        if (i >= end) {
            return default;
        }
        var (r, next) = BjoString.Cursors.RefNext(bytes, 0, end, i);
        return new StringWalk(bytes, next.Offset, end, BjoChar.FromRune(r), true);
    }

    public static StringWalk Start(Utf8String s) {
        var b = s.GetBounds();
        return At(b.Bytes, b.Lo, b.Hi);
    }

    public bool Done {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !_more;
    }

    /// The character; a walk at the end has none, and says so.
    public BjoChar Current {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _more ? _current : ThrowAtEnd();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public StringWalk Next() => _more ? At(_bytes, _next, _end) : this;

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static BjoChar ThrowAtEnd() =>
        throw new InvalidOperationException("string-walk-current: the walk is at the end of the string.");
}

public static class StringWalks {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringWalk Start(Utf8String s) => StringWalk.Start(s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Done(StringWalk w) => w.Done;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BjoChar Current(StringWalk w) => w.Current;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringWalk Next(StringWalk w) => w.Next();
}
