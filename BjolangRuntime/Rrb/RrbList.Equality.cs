using System.Collections.Generic;

namespace Bjolang.Runtime.Rrb;

/**
 * <summary>
 *     Structural equality: two lists are equal when they hold equal elements in
 *     the same order.
 * </summary>
 * <remarks>
 *     <para>
 *         Without this a list is compared by reference, which is what
 *         <see cref="EqualityComparer{T}.Default" /> falls back to — so a list used as a
 *         dictionary key never finds its own entry, and one built twice from the same
 *         elements compares unequal.
 *     </para>
 *     <para>
 *         Elements are compared and hashed through <see cref="EqualityComparer{T}.Default" />,
 *         so equality is structural exactly as deep as the elements' own is.
 *     </para>
 * </remarks>
 */
public sealed partial class RrbList<T> : IEquatable<RrbList<T>>
{
    /// Zero means "not computed yet". A hash that lands on zero is stored as one
    /// instead, so the sentinel costs a collision rather than a recomputation.
    private int _hash;

    public bool Equals(RrbList<T>? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        if (Count != other.Count) return false;
        if (Count == 0) return true;

        // Cheap rejection before the walk: two lists with computed and differing
        // hashes cannot be equal.
        if (_hash != 0 && other._hash != 0 && _hash != other._hash) return false;

        var comparer = EqualityComparer<T>.Default;
        var a = GetEnumerator();
        var b = other.GetEnumerator();

        while (a.MoveNext() && b.MoveNext())
            if (!comparer.Equals(a.Current, b.Current))
                return false;

        return true;
    }

    public override bool Equals(object? obj)
    {
        return obj is RrbList<T> other && Equals(other);
    }

    /**
     * <summary>
     *     Order-sensitive, and cached: a list is immutable, and a list used as a key is
     *     hashed on every lookup.
     * </summary>
     */
    public override int GetHashCode()
    {
        var cached = _hash;
        if (cached != 0) return cached;

        var hash = new HashCode();
        hash.Add(Count);

        foreach (var item in this)
            hash.Add(item);

        var computed = hash.ToHashCode();
        if (computed == 0) computed = 1;

        // Benign race: two threads compute the same answer.
        _hash = computed;
        return computed;
    }
}
