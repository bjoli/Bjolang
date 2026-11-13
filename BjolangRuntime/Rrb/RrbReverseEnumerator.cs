using System.Collections;
using System.Runtime.CompilerServices;

namespace Bjolang.Runtime.Rrb;

[NonUserCode]
public struct RrbReverseEnumerator<T> : IEnumerator<T>
{
    private readonly RrbList<T> _list;

    // --- Hoisted Hot Path Fields ---
    private T[]? _currentItems;
    private int _leafIndex;
    private int _leafFloor; // Lowest index in this leaf the range allows
    private int _totalIndex;
    // -------------------------------

    private readonly int _startIndex;
    private readonly int _count; // Limit how many items to yield
    private readonly int _stopIndex; // Exclusive lower bound: _startIndex - _count

    /// Whether the walk has entered the tree. Not `_currentItems == null`: the
    /// tail is items too, and a walk that leaves it for the tree has to *set*
    /// the stack rather than advance one that was never built.
    private bool _inTree;

    // Inline buffers, as in RrbEnumerator, so that constructing a walk
    // allocates nothing. A walk of a short vec never enters the tree, and two
    // heap arrays per walk were most of what such a walk cost.
    private EnumeratorPathBuffer<T> _path;
    private EnumeratorIndexBuffer _pathIndexes;
    private int _depth;

    public RrbReverseEnumerator<T> GetEnumerator()
    {
        return this;
    }

    // Overload 1: Reverse the WHOLE list (End -> 0)
    public RrbReverseEnumerator(RrbList<T> list)
        : this(list, list.Count - 1, list.Count)
    {
    }

    // Overload 2: Reverse from a specific index down to 0
    public RrbReverseEnumerator(RrbList<T> list, int startIndex)
        : this(list, startIndex, startIndex + 1)
    {
    }

    // Iterates backwards starting FROM 'startIndex' (inclusive).
    public RrbReverseEnumerator(RrbList<T> list, int startIndex, int count)
    {
        if (startIndex < -1 || startIndex >= list.Count)
            throw new ArgumentOutOfRangeException(nameof(startIndex));

        if (count < 0 || startIndex - count + 1 < -1) // Bounds check logic
            throw new ArgumentOutOfRangeException(nameof(count));

        _list = list;
        _startIndex = startIndex;
        _count = count;
        _stopIndex = startIndex - count;

        // Initialize state so first MoveNext() triggers MoveNextRare()
        _totalIndex = startIndex + 1; // Start "after" the item
        _currentItems = null;
        _leafIndex = -2; // Sentinel
        _leafFloor = 0;
        _inTree = false;
        _depth = 0;
    }

    public T Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _currentItems![_leafIndex];
    }

    object? IEnumerator.Current => Current;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        // Hot Path: Decrement and check bounds (down to the floor)
        //
        // The floor is the range's, not the leaf's: a walk of part of the list
        // ends inside a leaf, and testing only `>= 0` here ran on to the leaf's
        // start and yielded elements the range does not contain.
        if (--_leafIndex >= _leafFloor)
        {
            _totalIndex--;
            return true;
        }

        return MoveNextRare();
    }

    private bool MoveNextRare()
    {
        _totalIndex--;

        // Check Limit (Range Iteration)
        // If we have yielded '_count' items, stop.
        // Stop condition: _totalIndex < (_startIndex - _count + 1)
        if (_totalIndex <= _stopIndex) return false;

        // 1. Check Tail
        var tailOffset = _list.Count - _list.TailLen;
        if (_totalIndex >= tailOffset)
        {
            var tail = _list.Tail;
            _currentItems = tail;
            // In reverse, leafIndex is simply the offset
            _leafIndex = _totalIndex - tailOffset;
            _leafFloor = Floor();
            return true;
        }

        // 2. Check Head. A walk reaches it last, so the tree's stack is never
        // advanced past the tree's first leaf.
        var headLen = _list.HeadLen;
        if (_totalIndex < headLen)
        {
            _currentItems = _list.Head;
            _leafIndex = _totalIndex;
            _leafFloor = Floor();
            return true;
        }

        // 3. Traverse Tree
        if (!_inTree)
        {
            // First time entering the tree, from the start or from the tail.
            SetupStack(_list.Root!, _list.Shift, _totalIndex - headLen + _list.Gap);
            _inTree = true;
        }
        else
        {
            AdvanceStack();
        }

        return true;
    }

    /// The lowest index in the current leaf the range still allows.
    ///
    /// Called once per leaf, with `_totalIndex` and `_leafIndex` both set to
    /// the element about to be returned. Zero unless the range ends inside
    /// this leaf, which is the only case the hot path cannot see for itself.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Floor()
    {
        var remaining = _totalIndex - _stopIndex; // Counting the current one
        var floor = _leafIndex + 1 - remaining;
        return floor > 0 ? floor : 0;
    }

    private void SetupStack(Node<T> root, int shift, int targetIndex)
    {
        // Logic identical to Forward Enumerator (drill down to specific index)
        _depth = 0;
        var current = root;
        var currentShift = shift;

        while (currentShift > 0)
        {
            var internalNode = (InternalNode<T>)current;
            var (childIndex, relativeIndex) = GetChildIndex(internalNode, targetIndex, currentShift);

            _path[_depth] = current;
            _pathIndexes[_depth] = childIndex;

            current = internalNode.Children[childIndex]!;
            _depth++;
            currentShift -= Constants.RRB_BITS;
            targetIndex = relativeIndex;
        }

        var leaf = (LeafNode<T>)current;
        _currentItems = leaf.Items;
        _leafIndex = targetIndex; // Exact index within leaf
        _leafFloor = Floor();
    }

    private void AdvanceStack()
    {
        var d = _depth - 1;
        while (d >= 0)
        {
            var parent = (InternalNode<T>)_path[d]!;

            // REVERSE LOGIC: Move Left
            var prevIndex = _pathIndexes[d] - 1;

            if (prevIndex >= 0)
            {
                _pathIndexes[d] = prevIndex;
                var current = parent.Children[prevIndex]!;

                d++;
                while (d < _depth)
                {
                    _path[d] = current;
                    var internalNode = (InternalNode<T>)current;

                    // REVERSE LOGIC: Drill down LAST child
                    var lastChildIdx = internalNode.Len - 1;
                    _pathIndexes[d] = lastChildIdx;

                    current = internalNode.Children[lastChildIdx]!;
                    d++;
                }

                var leaf = (LeafNode<T>)current;
                _currentItems = leaf.Items;
                _leafIndex = leaf.Len - 1; // Start at END of new leaf
                _leafFloor = Floor();
                return;
            }

            d--;
        }

        throw new InvalidOperationException("Iterator state corrupt.");
    }

    // Reuse the helper we discussed earlier
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int, int) GetChildIndex(InternalNode<T> node, int index, int shift)
    {
        if (node.SizeTable != null)
        {
            var childIndex = 0;
            while (childIndex < node.Len && node.SizeTable[childIndex] <= index) childIndex++;
            var prevCount = childIndex > 0 ? node.SizeTable[childIndex - 1] : 0;
            return (childIndex, index - prevCount);
        }
        else
        {
            var childIndex = (index >> shift) & Constants.RRB_MASK;
            var childStart = childIndex << shift;
            return (childIndex, index - childStart);
        }
    }

    public void Reset()
    {
        _totalIndex = _startIndex + 1;
        _currentItems = null;
        _leafIndex = -2;
        _leafFloor = 0;
        _inTree = false;
        _depth = 0;
    }

    public void Dispose()
    {
        _currentItems = null;
    }
}