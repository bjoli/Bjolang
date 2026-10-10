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

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using BjoString;
using Collections;

namespace Bjolang.Runtime;

// The cursor for walking part of a `Vec` backwards. It walks one leaf array at
// a time, as a struct that `next` answers advanced, so a walk allocates
// nothing. The forward cursor is `BjolangRuntime.VecCursor`, a builtin type,
// which `in-vec` and plain `(:for x v)` use; see there.

/// A backward walk of part of a vec, one leaf array at a time.
///
/// `Items[I]` down to `Items[Floor]` are this leaf's part of the walk.
/// `NextIndex` is the vec index below them, and `Stop` the vec index the walk
/// ends above.
public readonly struct VecBackCursor<T> {
    private readonly T[] _items;
    private readonly int _i;
    private readonly int _floor;
    private readonly RrbList<T> _list;
    private readonly int _nextIndex;
    private readonly int _stop;

    private VecBackCursor(T[] items, int i, int floor, RrbList<T> list, int nextIndex, int stop) {
        _items = items;
        _i = i;
        _floor = floor;
        _list = list;
        _nextIndex = nextIndex;
        _stop = stop;
    }

    /// The walk of `count` elements down from `last`. `last` may be -1, which
    /// with a count of 0 is the walk of an empty slice.
    public static VecBackCursor<T> Start(RrbList<T> list, int last, int count) {
        if (last < -1 || last >= list.Count || count < 0 || last - count < -1)
            NotARun(last, count, list.Count);
        if (count == 0) return new VecBackCursor<T>(Array.Empty<T>(), -1, 0, list, last, last);
        return Enter(list, last, last - count);
    }

    public bool Done {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _i < _floor;
    }

    public T Current {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _items[_i];
    }

    // The slow path takes and answers values, as in `BjolangRuntime.VecCursor`,
    // so that the JIT can keep the cursor in registers.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public VecBackCursor<T> Next() {
        var i = _i - 1;
        if (i >= _floor || _nextIndex <= _stop) return new VecBackCursor<T>(_items, i, _floor, _list, _nextIndex, _stop);
        return AtLeaf(_list, _nextIndex, _stop);
    }

    // Apart from `Start`, so that the message does not make `Start` too large
    // to inline at the start of each walk.
    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private static void NotARun(int last, int count, int length) =>
        throw new ArgumentOutOfRangeException(nameof(count), $"{count} down from {last} is not a run of a vec of {length}.");

    /// The cursor on element `index`, with the part of its leaf the walk down
    /// to above `stop` takes. Inlined where a walk starts, so that the walk of
    /// a vec that is all tail makes no call. `AtLeaf` is the same out of line,
    /// for the next leaf of a walk.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static VecBackCursor<T> Enter(RrbList<T> list, int index, int stop) {
        var items = list.LeafAt(index, out var position, out _);
        var take = Math.Min(position + 1, index - stop);
        return new VecBackCursor<T>(items, position, position - take + 1, list, index - take, stop);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static VecBackCursor<T> AtLeaf(RrbList<T> list, int index, int stop) => Enter(list, index, stop);
}

public static class VecWalk {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VecBackCursor<T> Backward<T>(RrbList<T> list, int last, int count) =>
        VecBackCursor<T>.Start(list, last, count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool BackwardDone<T>(VecBackCursor<T> cursor) => cursor.Done;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T BackwardCurrent<T>(VecBackCursor<T> cursor) => cursor.Current;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VecBackCursor<T> BackwardNext<T>(VecBackCursor<T> cursor) => cursor.Next();
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
