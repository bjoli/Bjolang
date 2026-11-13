using System.Collections;

namespace Bjolang.Runtime.Rrb;

public sealed partial class RrbList<T> : ICollection<T>, IReadOnlyList<T>
{
// Explicit Interface Implementation:
    IEnumerator<T> IEnumerable<T>.GetEnumerator()
    {
        return new RrbEnumerator<T>(this);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public RrbList<T> AddRange(IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));

        // Optimization: If items is already an RrbList, use O(log N) Merge
        if (items is RrbList<T> otherList) return Merge(otherList);

        // Optimization: Use Builder to create a tree in O(M) then Merge in O(log N).
        // This is strictly faster than repeatedly calling Add (O(M log N)).
        var other = Create(items);
        if (other.Count == 0) return this;

        return Merge(other);
    }
    public RrbList<T> AddRange(ReadOnlySpan<T> items)
    {
        var other = Create(items);
        if (other.Count == 0) return this;
        return Merge(other);
    }
    
    /**
  * <summary>
  *     Returns an enumerator that iterates through the list.
  * </summary>
  * <returns>An <see cref="IEnumerator{T}" /> for the list.</returns>
  */
    public RrbEnumerator<T> GetEnumerator()
    {
        return new RrbEnumerator<T>(this);
    }

    public RrbEnumerator<T> RangeEnumerator(int index, int count)
    {
        // Bounds checks handled by constructor
        return new RrbEnumerator<T>(this, index, count);
    }

    // Helper to expose the reverse range
    public RrbReverseEnumerator<T> ReverseEnumerator(int index, int count)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        // Constructor does bounds check for 'count'
        return new RrbReverseEnumerator<T>(this, index, count);
    }

    public RrbReverseEnumerator<T> ReverseEnumerator()
    {
        return new RrbReverseEnumerator<T>(this);
    }
}