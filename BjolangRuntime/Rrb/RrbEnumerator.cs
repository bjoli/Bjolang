using System.Collections;
using System.Runtime.CompilerServices;

namespace Bjolang.Runtime.Rrb;

[InlineArray(8)]
[NonUserCode]
internal struct EnumeratorPathBuffer<T> { private Node<T>? _element0; }

[InlineArray(8)]
[NonUserCode]
internal struct EnumeratorIndexBuffer { private int _element0; }


[NonUserCode]
public struct RrbEnumerator<T> : IEnumerator<T>
{
    private readonly RrbList<T> _list;
    private readonly int _endIndex; // The exclusive upper bound

    // --- Hoisted Hot Path Fields (Unchanged!) ---
    private T[]? _currentItems;
    private int _leafIndex;
    private int _leafLen; // Now clamped to the range!
    private int _totalIndex;
    // --------------------------------------------

    private readonly int _startIndex;

    /// Whether the walk has entered the tree. Not `_currentItems == null`: the
    /// head is items too, and a walk that leaves it for the tree has to *set*
    /// the stack rather than advance one that was never built.
    private bool _inTree;

    private EnumeratorPathBuffer<T> _path;
    private EnumeratorIndexBuffer _pathIndexes;
    private int _depth;

    // Default constructor (Full list)
    public RrbEnumerator(RrbList<T> list)
        : this(list, 0, list.Count)
    {
    }

    // Start Index constructor
    public RrbEnumerator(RrbList<T> list, int startIndex)
        : this(list, startIndex, list.Count - startIndex)
    {
    }

    // Range constructor
    public RrbEnumerator(RrbList<T> list, int startIndex, int count)
    {
        if (startIndex < 0 || startIndex > list.Count)
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (count < 0 || startIndex + count > list.Count)
            throw new ArgumentOutOfRangeException(nameof(count));

        _list = list;
        _startIndex = startIndex;
        _endIndex = startIndex + count; // Store exclusive end

        _totalIndex = startIndex - 1;
        _currentItems = null;
        _leafIndex = -1;
        _leafLen = 0;
        _inTree = false;
        _depth = 0;
    }

    public T Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _currentItems![_leafIndex];
    }

    object? IEnumerator.Current => Current;

    // Hot Path is 100% IDENTICAL to the previous version
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (++_leafIndex < _leafLen)
        {
            _totalIndex++;
            return true;
        }

        return MoveNextRare();
    }

    private bool MoveNextRare()
    {
        // Check if we hit the range limit
        // (Use +1 because _totalIndex tracks the *previous* item)
        if (_totalIndex + 1 >= _endIndex) return false;

        _totalIndex++;

        // 0. Check Head
        var headLen = _list.HeadLen;
        if (_totalIndex < headLen)
        {
            _currentItems = _list.Head;
            _leafIndex = _totalIndex;
            _leafLen = Math.Min(headLen, _endIndex);
            return true;
        }

        // 1. Check Tail
        var tailOffset = _list.Count - _list.TailLen;
        if (_totalIndex >= tailOffset)
        {
            var tail = _list.Tail;
            _currentItems = tail;

            // Calculate where we are in the tail
            _leafIndex = _totalIndex - tailOffset;

            // CAP THE LENGTH: Stop at tail end OR range end
            var tailEnd = _endIndex - tailOffset;
            _leafLen = Math.Min(tail.Length, tailEnd);

            return true;
        }

        // 2. Traverse Tree
        if (!_inTree)
        {
            SetupStack(_list.Root!, _list.Shift, _totalIndex - headLen + _list.Gap);
            _inTree = true;
        }
        else
        {
            AdvanceStack();
        }

        return true;
    }

    private void SetupStack(Node<T> root, int shift, int targetIndex)
    {
        // ... (Path finding logic is identical to before) ...
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
        _leafIndex = targetIndex & Constants.RRB_MASK;

        // CAP THE LENGTH:
        // How many items can we take from this leaf?
        // Allowed: (_endIndex) - (Current Global Index)
        // Current Global Index = _totalIndex (which is targetIndex passed in)
        // Actually simpler: 
        // We know _leafIndex. We know leaf.Len.
        // We know we want to yield N items total.
        // Remaining items in range = _endIndex - _totalIndex.
        // End bound for this leaf = _leafIndex + remaining.

        var remainingInRange = _endIndex - _totalIndex;
        _leafLen = Math.Min(leaf.Len, _leafIndex + remainingInRange);
    }

    private void AdvanceStack()
    {
        // ... (Tree traversal identical to before) ...
        var d = _depth - 1;
        while (d >= 0)
        {
            var parent = (InternalNode<T>)_path[d]!;
            var nextIndex = _pathIndexes[d] + 1;

            if (nextIndex < parent.Len)
            {
                _pathIndexes[d] = nextIndex;
                var current = parent.Children[nextIndex]!;

                d++;
                while (d < _depth)
                {
                    _path[d] = current;
                    _pathIndexes[d] = 0;
                    current = ((InternalNode<T>)current).Children[0]!;
                    d++;
                }

                var leaf = (LeafNode<T>)current;
                _currentItems = leaf.Items;
                _leafIndex = 0;

                // CAP THE LENGTH:
                // We are at the start of a new leaf.
                // We are at global position _totalIndex.
                // Remaining range = _endIndex - _totalIndex.
                var remainingInRange = _endIndex - _totalIndex;
                _leafLen = Math.Min(leaf.Len, remainingInRange);

                return;
            }

            d--;
        }

        throw new InvalidOperationException("Iterator state corrupt.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int childIndex, int relativeIndex) GetChildIndex(InternalNode<T> node, int index, int shift)
    {
        if (node.IsRelaxed())
        {
            // Route relaxed nodes straight to your AVX engine
            return RrbAlgorithm.GetRelaxedIndex(node, index, shift);
        }
    
        int childIndex = (index >> shift) & Constants.RRB_MASK;
        int childStart = childIndex << shift;
        return (childIndex, index - childStart);
    }

    // Duck typing for foreach
    public RrbEnumerator<T> GetEnumerator()
    {
        return this;
    }

    public void Reset()
    {
        _totalIndex = _startIndex - 1;
        _currentItems = null;
        _leafIndex = -1;
        _leafLen = 0;
        _inTree = false;
        _depth = 0;
    }

    public void Dispose()
    {
        _currentItems = null;
    }
}