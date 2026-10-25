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

using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bjolang.Runtime;

// IArray — the immutable array.
//
// A struct around .NET's `ImmutableArray<T>`, which is itself a struct around
// one `T[]` that nothing writes after it is made. Indexing is an array read,
// and the value is one reference wide. Every change copies the whole array, so
// it is the collection for values that are built once and then read, such as
// the arrays of a parsed JSON document. `Vec` is the one to change.
//
// A wrapper rather than `ImmutableArray<T>` itself, for its equality. The
// compiler takes a std `Eq` implementation for a .NET type to agree with the
// type's own `Equals`, and gives a `Map` or a `HashSet` .NET's comparer. An
// `ImmutableArray<T>` equals another only when they hold the same array. This
// one compares the elements, with the elements' own `Equals`, as `Vec` does.
//
// The default value holds no array. A field or an array slot of type `IArray`
// holds it before it is written. Every member reads it as the empty array.

[NonUserCode]
public readonly struct IArray<T> : IEquatable<IArray<T>>, IReadOnlyList<T>
{
    private readonly ImmutableArray<T> _items;

    public IArray(ImmutableArray<T> items) => _items = items;

    public static readonly IArray<T> Empty = new(ImmutableArray<T>.Empty);

    /// The elements as .NET's immutable array, for an `import/extern` that
    /// takes one. The empty array for the default value.
    public ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _items.IsDefault ? 0 : _items.Length;
    }

    /// Throws when the index is out of range.
    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Items[index];
    }

    public ReadOnlySpan<T> AsSpan() => Items.AsSpan();

    public ImmutableArray<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    public bool Equals(IArray<T> other)
    {
        var a = AsSpan();
        var b = other.AsSpan();
        if (a.Length != b.Length) return false;
        if (a == b) return true;
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < a.Length; i++)
            if (!comparer.Equals(a[i], b[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is IArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Count);
        foreach (var item in AsSpan()) hash.Add(item);
        return hash.ToHashCode();
    }

    public static bool operator ==(IArray<T> a, IArray<T> b) => a.Equals(b);

    public static bool operator !=(IArray<T> a, IArray<T> b) => !a.Equals(b);
}

// The functions the prelude imports.
[NonUserCode]
public static class IArrayModule {
    public static IArray<T> Empty<T>() => IArray<T>.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count<T>(IArray<T> xs) => xs.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEmpty<T>(IArray<T> xs) => xs.Count == 0;

    /// Throws when the index is out of range, as `Refable` asks.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Get<T>(IArray<T> xs, int index) => xs[index];

    public static bool TryGet<T>(IArray<T> xs, int index, [MaybeNullWhen(false)] out T value)
    {
        if (index >= 0 && index < xs.Count)
        {
            value = xs[index];
            return true;
        }

        value = default;
        return false;
    }

    /// A copy with the element at `index` replaced. Throws when the index is
    /// out of range.
    public static IArray<T> SetItem<T>(IArray<T> xs, int index, T value) => new(xs.Items.SetItem(index, value));

    /// A copy with `value` at the end.
    public static IArray<T> Add<T>(IArray<T> xs, T value) => new(xs.Items.Add(value));

    /// A copy of `count` elements of `items` from `start`. The array is not
    /// kept, so the caller can write it again.
    public static IArray<T> FromSubarray<T>(T[] items, int start, int count) =>
        count == 0 ? IArray<T>.Empty : new(ImmutableArray.Create(items, start, count));

    public static IArray<T> FromArray<T>(T[] items) => new(ImmutableArray.Create(items));

    /// A copy of what a `MutableVec` holds now. One copy of its elements: the
    /// list's own array is longer than its count, and the list can be written
    /// again.
    public static IArray<T> FromList<T>(List<T> items) =>
        items.Count == 0 ? IArray<T>.Empty : new(ImmutableArray.Create<T>(CollectionsMarshal.AsSpan(items)));

    public static IArray<T> FromEnumerable<T>(IEnumerable<T> items) => new(ImmutableArray.CreateRange(items));

    /// A new array, which the caller can write.
    public static T[] ToArray<T>(IArray<T> xs) => xs.AsSpan().ToArray();

    public static IEnumerable<T> AsEnumerable<T>(IArray<T> xs) => xs;

    /// The function first, as `vec-map` takes it.
    public static IArray<U> Map<T, U>(Func<T, U> f, IArray<T> xs)
    {
        var src = xs.AsSpan();
        if (src.Length == 0) return IArray<U>.Empty;
        var dst = new U[src.Length];
        for (var i = 0; i < src.Length; i++) dst[i] = f(src[i]);
        // The array is new and nothing else holds it, so it becomes the
        // immutable array without a copy.
        return new(ImmutableCollectionsMarshal.AsImmutableArray(dst));
    }
}
