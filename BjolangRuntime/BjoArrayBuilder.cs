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

namespace Bjolang.Runtime;

/// <summary>
/// The accumulator of the <c>arraying</c> collector: an array that grows, and
/// the number of elements in it.
/// </summary>
/// <remarks>
/// It is also a <c>BlockCollector</c>'s accumulator, so that a vector loop
/// can write a block of elements into the array at once.
/// </remarks>
[NonUserCode]
public sealed class ArrayBuilder<T>
{
    private T[] _items = System.Array.Empty<T>();
    private int _count;

    public int Count => _count;

    private void Reserve(int wanted)
    {
        if (wanted <= _items.Length) return;
        var bigger = new T[Math.Max(Math.Max(wanted, 16), _items.Length * 2)];
        _items.AsSpan(0, _count).CopyTo(bigger);
        _items = bigger;
    }

    public void Add(T x)
    {
        if (_count == _items.Length) Reserve(_count + 1);
        _items[_count++] = x;
    }

    /// <summary><paramref name="n"/> more elements at the end, written next through <see cref="Span"/>.</summary>
    public void Grow(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        Reserve(_count + n);
        _count += n;
    }

    public Span<T> Span(int start, int n)
    {
        if (start < 0 || n < 0 || start > _count - n) throw new ArgumentOutOfRangeException(nameof(start));
        return _items.AsSpan(start, n);
    }

    /// <summary>
    /// The elements, as an array of their number. The builder's own array
    /// when it has that size, and a copy when it does not.
    /// </summary>
    public T[] ToArray()
    {
        var result = _count == _items.Length ? _items : _items.AsSpan(0, _count).ToArray();
        _items = System.Array.Empty<T>();
        _count = 0;
        return result;
    }
}

/// <summary>The operations of <see cref="ArrayBuilder{T}"/>, for the prelude.</summary>
[NonUserCode]
public static class ArrayBuilderModule
{
    public static ArrayBuilder<T> Empty<T>() => new();

    public static ArrayBuilder<T> Add<T>(ArrayBuilder<T> b, T x)
    {
        b.Add(x);
        return b;
    }

    public static int Count<T>(ArrayBuilder<T> b) => b.Count;

    public static ArrayBuilder<T> Grow<T>(ArrayBuilder<T> b, int n)
    {
        b.Grow(n);
        return b;
    }

    public static Span<T> Span<T>(ArrayBuilder<T> b, int start, int n) => b.Span(start, n);

    public static T[] ToArray<T>(ArrayBuilder<T> b) => b.ToArray();

    /// <summary>
    /// An element of an array is never None. A vector loop calls this only
    /// for a collector of Options, and <c>arraying</c> of Options has no
    /// vector form, since an Option is not a lane.
    /// </summary>
    public static void NoNone<T>(ArrayBuilder<T> b, int i) =>
        throw new InvalidOperationException("an element of an array cannot be made None");
}
