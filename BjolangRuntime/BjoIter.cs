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
