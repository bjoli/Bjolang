using System.Runtime.CompilerServices;

namespace Bjolang.Runtime.Rrb;

/// A transient RRB-List. Most vecs a program builds are small, so nothing is
/// allocated until it is needed: the tail, a leaf's worth, comes with the
/// first item, and the array that collects full leaves with the first leaf,
/// growing 4, 8, 16 and on up to a chunk.
[NonUserCode]
public class RrbBuilder<T>
{
    private Node<T>? _root;
    private int _shift;
    private int _rootCount;

    // The head of the lists the builder makes: items in front of the tree.
    // The first leaf the builder fills becomes the head when the tree is made,
    // so that a list from a builder reads its front in one array and the tree
    // behind it is laid out from its first slot, dense. Null when empty.
    //
    // The items are at _head[_headStart .. _headStart + _headLen). A head the
    // builder owns is a leaf's worth with its items at the end, so that
    // Prepend writes in front of them and PopFirst steps past one. A head
    // shared with a list starts at 0, as a list's does, and is copied before
    // it is changed. A head exists only with a tree, as in a list.
    private T[]? _head;
    private int _headStart;
    private int _headLen;
    private bool _headShared;
    
    // Full leaves wait here until ToImmutable puts them in the tree. Chunks of
    // a fixed size rather than one array that doubles, so that a big vec never
    // makes an array large enough for the large object heap. Every chunk in
    // `_chunks` is full and ChunkSize long; `_currentChunk` grows up to that.
    private const int ChunkSize = 512; // 512 * 8 bytes = 4KB (Well within Gen 0)
    private const int FirstChunkSize = 4;
    
    private List<LeafNode<T>[]>? _chunks;
    private LeafNode<T>[]? _currentChunk;
    // Index in the current chunk
    private int _chunkIndex;
    // Total leaves stored across all chunks
    private int _totalLeaves;
    
    // The tail is handed to the list ToImmutable makes when its length is its
    // count. It is shared from then on, and the builder copies it before
    // changing it. A shared tail is always full to its length, so Add's fast
    // path never writes to one.
    private T[] _currentTail;
    private int _currentTailLen;
    private bool _tailShared;
    
    private OwnerId _token;

    // A small builder starts its tail at FirstSmallTail items and doubles it up
    // to a leaf, where a builder by default takes a leaf's worth at the first
    // item. A vec of 4, 8, 16 or 32 is then handed its tail with no copy, and a
    // smaller one copies from a short tail rather than from a leaf.
    private readonly bool _small;
    private const int FirstSmallTail = 4;

    // The token a builder owns its nodes by. Taken at the first node rather than
    // when the builder is made: a builder that only ever fills its tail, which
    // is what most builders are, never needs one, and taking one reads
    // thread-local state.
    private OwnerId Token
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_token == OwnerId.None) _token = OwnerId.Next();
            return _token;
        }
    }

    public RrbBuilder()
    {
        _currentTail = Array.Empty<T>();
    }

    private RrbBuilder(bool small) : this()
    {
        _small = small;
    }

    /// A builder for a vec that is likely small. See `_small`.
    public static RrbBuilder<T> Small() => new RrbBuilder<T>(small: true);

    internal RrbBuilder(RrbList<T> list)
    {
        _currentTail = Array.Empty<T>();
        Load(list);
    }

    // The builder's state set to `list`, with no pending leaves. The tree is
    // shared, and the builder owns none of it.
    private void Load(RrbList<T> list)
    {
        // The builder appends to a tree that starts at 0.
        list = list.Materialized();

        if (_totalLeaves > 0)
        {
            _chunks?.Clear();
            Array.Clear(_currentChunk!, 0, _chunkIndex);
            _chunkIndex = 0;
            _totalLeaves = 0;
        }

        _token = OwnerId.None;
        _root = list.Root;
        _rootCount = list.TreeCount;
        _shift = list.Shift;
        _head = list.Head;
        _headStart = 0;
        _headLen = list.HeadLen;
        _headShared = list.HeadLen > 0;
        
        if (list.TailLen == list.Tail.Length)
        {
            _currentTail = list.Tail;
            _tailShared = list.TailLen > 0;
        }
        else
        {
            _currentTail = new T[list.TailLen];
            Array.Copy(list.Tail, _currentTail, list.TailLen);
            _tailShared = false;
        }
        _currentTailLen = list.TailLen;
    }

    /// <summary>
    /// Creates a new RRB-List from an array directly by chunking it.
    /// </summary>
    /// <param name="items">The array of items.</param>
    /// <param name="reuseArrayIfShorterThan32">If true and the array is small enough to fit in the tail, it will be used directly instead of copied.</param>
    /// <returns>A new RRB-List.</returns>
    public static RrbList<T> FromArray(T[] items, bool reuseArrayIfShorterThan32 = false)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (items.Length == 0) return RrbList<T>.Empty;

        if (items.Length <= Constants.RRB_BRANCHING)
        {
            var tail = reuseArrayIfShorterThan32 ? items : (T[])items.Clone();
            return new RrbList<T>(null, tail, items.Length, 0, items.Length);
        }

        var builder = new RrbBuilder<T>();
        builder.AddRange(items);
        return builder.ToImmutable();
    }

    /// <summary>
    /// Creates a new RRB-List from a ReadOnlySpan directly by chunking it.
    /// </summary>
    /// <param name="items">The span of items.</param>
    /// <returns>A new RRB-List.</returns>
    public static RrbList<T> FromSpan(ReadOnlySpan<T> items)
    {
        if (items.Length == 0) return RrbList<T>.Empty;

        if (items.Length <= Constants.RRB_BRANCHING)
        {
            var tail = items.ToArray();
            return new RrbList<T>(null, tail, items.Length, 0, items.Length);
        }

        var builder = new RrbBuilder<T>();
        builder.AddRange(items);
        return builder.ToImmutable();
    }

    public int Count => _headLen + _rootCount + (_totalLeaves * Constants.RRB_BRANCHING) + _currentTailLen;

    public T this[int index]
    {
        get
        {
            if (index < _headLen) return _head![_headStart + index];
            index -= _headLen;

            if (index < _rootCount)
            {
                return GetFromTree(_root!, index, _shift);
            }

            var pendingIndex = index - _rootCount;
            var pendingTotal = _totalLeaves * Constants.RRB_BRANCHING;
            
            if (pendingIndex < pendingTotal)
            {
                var globalLeafIdx = pendingIndex >> Constants.RRB_BITS; // / 32
                var itemIdx = pendingIndex & Constants.RRB_MASK;       // % 32
                
                return GetLeaf(globalLeafIdx).Items[itemIdx];
            }

            return _currentTail[pendingIndex - pendingTotal];
        }
        set => SetItem(index, value);
    }
    
    private int FullChunks => _chunks?.Count ?? 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LeafNode<T>[] ChunkAt(int chunkIdx) =>
        chunkIdx < FullChunks ? _chunks![chunkIdx] : _currentChunk!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LeafNode<T> GetLeaf(int globalIndex) =>
        ChunkAt(globalIndex / ChunkSize)[globalIndex % ChunkSize];
    
    private T GetFromTree(Node<T> node, int index, int shift)
    {
        while (shift > 0)
        {
            var inode = RrbAlgorithm.AsInternal(node);
            if (inode.IsRelaxed())
            {
                var (childIdx, relIdx) = RrbAlgorithm.GetRelaxedIndex(inode, index, shift);
                node = inode.Children[childIdx]!;
                index = relIdx;
            }
            else
            {
                var childIdx = (index >> shift) & Constants.RRB_MASK;
                node = inode.Children[childIdx]!;
                // A relaxed node further down takes an index relative to itself.
                index -= childIdx << shift;
            }
            shift -= Constants.RRB_BITS;
        }
        return RrbAlgorithm.AsLeaf(node).Items[index & Constants.RRB_MASK];
    }

    public void SetItem(int index, T value)
    {
         if (index < 0 || index >= Count) throw new IndexOutOfRangeException();

         if (index < _headLen)
         {
             if (_headShared) OwnHead();
             _head![_headStart + index] = value;
             return;
         }

         index -= _headLen;

         if (index < _rootCount)
         {
             _root = RrbAlgorithm.Update(_root!, index, value, _shift, Token);
             return;
         }

         var pendingIndex = index - _rootCount;
         var pendingTotal = _totalLeaves * Constants.RRB_BRANCHING;

         if (pendingIndex < pendingTotal)
         {
             var globalLeafIdx = pendingIndex >> Constants.RRB_BITS;
             var itemIdx = pendingIndex & Constants.RRB_MASK;
             
             var targetChunk = ChunkAt(globalLeafIdx / ChunkSize);
             int idxInChunk = globalLeafIdx % ChunkSize;
             var leaf = targetChunk[idxInChunk];

             if (leaf.Owner != Token)
             {
                 leaf = leaf.CloneAndSet(itemIdx, value);
                 targetChunk[idxInChunk] = leaf;
             }
             else
             {
                 leaf.Items[itemIdx] = value;
             }
             return;
         }

         if (_tailShared) UnshareTail(_currentTail.Length);
         _currentTail[pendingIndex - pendingTotal] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(T item)
    {
        var tail = _currentTail;
        var len = _currentTailLen;
        if ((uint)len < (uint)tail.Length)
        {
            tail[len] = item;
            _currentTailLen = len + 1;
            return;
        }

        AddSlow(item);
    }
        
    // The tail is full to its length: it is a leaf's worth and becomes one, or
    // it grows.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddSlow(T item)
    {
        if (_currentTailLen == Constants.RRB_BRANCHING)
        {
            PushTailAsLeaf();
            var fresh = new T[Constants.RRB_BRANCHING];
            fresh[0] = item;
            _currentTail = fresh;
            _currentTailLen = 1;
            return;
        }

        // There is no tail yet, it is a list's, exactly as long as it is, or it
        // is a small builder's, which grows.
        var capacity = _small
            ? Math.Min(Math.Max(FirstSmallTail, _currentTailLen * 2), Constants.RRB_BRANCHING)
            : Constants.RRB_BRANCHING;
        UnshareTail(capacity);
        _currentTail[_currentTailLen++] = item;
    }

    // Replaces the tail with a copy of `capacity`, which is the builder's own.
    private void UnshareTail(int capacity)
    {
        var copy = new T[capacity];
        Array.Copy(_currentTail, copy, _currentTailLen);
        _currentTail = copy;
        _tailShared = false;
    }

    // A full tail becomes a leaf. One shared with a list is frozen, so that the
    // builder clones it before setting an item in it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PushTailAsLeaf()
    {
        var owner = _tailShared ? OwnerId.None : Token;
        AddLeaf(new LeafNode<T>(_currentTail, Constants.RRB_BRANCHING, owner));
        _tailShared = false;
    }

    public void AddRange(ReadOnlySpan<T> items)
    {
        if (items.Length == 0) return;

        int offset = 0;
        int remaining = items.Length;

        // Fill the tail up to a leaf. When nothing will be left over, the tail
        // is made exactly as long as it needs to be, so that ToImmutable can
        // hand it over without copying.
        if (_currentTailLen < Constants.RRB_BRANCHING)
        {
            int toTail = Math.Min(Constants.RRB_BRANCHING - _currentTailLen, remaining);
            int needed = _currentTailLen + toTail;
            if (_tailShared || needed > _currentTail.Length)
                UnshareTail(remaining > toTail ? Constants.RRB_BRANCHING : needed);
            
            items.Slice(0, toTail).CopyTo(_currentTail.AsSpan(_currentTailLen));
            _currentTailLen = needed;
            offset = toTail;
            remaining -= toTail;
        }

        if (remaining == 0) return;

        // The tail is full, and more follows: room for it and for every whole
        // leaf after it.
        ReserveLeaves(1 + ((remaining - 1) >> Constants.RRB_BITS));
        PushTailAsLeaf();

        // The chunk, its index and the token are kept in locals, and put back
        // only around the call that finds a new chunk.
        var token = Token;
        var chunk = _currentChunk!;
        var index = _chunkIndex;
        var added = 0;
        while (remaining > Constants.RRB_BRANCHING)
        {
            var leafItems = new T[Constants.RRB_BRANCHING];
            items.Slice(offset, Constants.RRB_BRANCHING).CopyTo(leafItems);
            var leaf = new LeafNode<T>(leafItems, Constants.RRB_BRANCHING, token);
            
            if ((uint)index < (uint)chunk.Length)
            {
                chunk[index++] = leaf;
                added++;
            }
            else
            {
                _chunkIndex = index;
                _totalLeaves += added;
                added = 0;
                AddLeafToNewRoom(leaf);
                chunk = _currentChunk!;
                index = _chunkIndex;
            }
            
            offset += Constants.RRB_BRANCHING;
            remaining -= Constants.RRB_BRANCHING;
        }
        _chunkIndex = index;
        _totalLeaves += added;

        _currentTail = items.Slice(offset, remaining).ToArray();
        _currentTailLen = remaining;
    }

    // Like AddRange, for many short ranges in turn. AddRange leaves a tail
    // exactly as long as what is in it, so that ToImmutable can hand it over,
    // and the next range then copies it into a tail of a leaf's worth. Here
    // the tail is always a leaf's worth: each item is copied once, and
    // ToImmutable copies the last tail once.
    internal void AppendRange(ReadOnlySpan<T> items)
    {
        if (items.Length == 0) return;
        if (_tailShared || _currentTail.Length < Constants.RRB_BRANCHING) UnshareTail(Constants.RRB_BRANCHING);

        while (true)
        {
            int take = Math.Min(Constants.RRB_BRANCHING - _currentTailLen, items.Length);
            items.Slice(0, take).CopyTo(_currentTail.AsSpan(_currentTailLen));
            _currentTailLen += take;
            items = items.Slice(take);
            if (items.Length == 0) return;

            // The tail is full, and more follows.
            PushTailAsLeaf();
            _currentTail = new T[Constants.RRB_BRANCHING];
            _currentTailLen = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddLeaf(LeafNode<T> leaf)
    {
        var chunk = _currentChunk;
        var index = _chunkIndex;
        if (chunk != null && (uint)index < (uint)chunk.Length)
        {
            chunk[index] = leaf;
            _chunkIndex = index + 1;
            _totalLeaves++;
            return;
        }
        
        AddLeafToNewRoom(leaf);
    }

    // The current chunk is full, or there is none: it grows up to a chunk, or
    // is put with the full ones and a new chunk begins.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddLeafToNewRoom(LeafNode<T> leaf)
    {
        var chunk = _currentChunk;
        if (chunk == null)
        {
            chunk = _currentChunk = new LeafNode<T>[FirstChunkSize];
        }
        else
        {
            if (chunk.Length < ChunkSize)
            {
                Array.Resize(ref _currentChunk, Math.Min(chunk.Length * 2, ChunkSize));
                chunk = _currentChunk;
            }
            else
            {
                (_chunks ??= new List<LeafNode<T>[]>()).Add(chunk);
                chunk = _currentChunk = new LeafNode<T>[ChunkSize];
                _chunkIndex = 0;
            }
        }

        chunk[_chunkIndex++] = leaf;
        _totalLeaves++;
    }

    // Room for `count` more leaves in the current chunk, as far as a chunk
    // goes, so that a range added at once is not copied as the chunk grows.
    private void ReserveLeaves(int count)
    {
        if (count <= 0) return;
        if (_currentChunk == null)
        {
            _currentChunk = new LeafNode<T>[Math.Clamp(count, FirstChunkSize, ChunkSize)];
            return;
        }

        int wanted = _chunkIndex + count;
        if (wanted > _currentChunk.Length && _currentChunk.Length < ChunkSize)
            Array.Resize(ref _currentChunk, Math.Min(Math.Max(wanted, _currentChunk.Length * 2), ChunkSize));
    }

    public RrbList<T> ToImmutable()
    {
        if (_totalLeaves > 0)
        {
            FlushLeavesToTree();
            
            // Reset state, keeping the current chunk for the leaves to come.
            _chunks?.Clear();
            Array.Clear(_currentChunk!, 0, _chunkIndex);
            _chunkIndex = 0;
            _totalLeaves = 0;
        }

        if (_root == null)
        {
            if (_currentTailLen == 0) return RrbList<T>.Empty;
            var tail = TakeTail();
            return new RrbList<T>(null, tail, _currentTailLen, 0, _currentTailLen);
        }

        var frozenRoot = _root;
        if (frozenRoot is InternalNode<T> inode) frozenRoot = inode.Freeze(Token);
        else if (frozenRoot is LeafNode<T> lnode) frozenRoot = lnode.Freeze(Token);
        
        var finalTail = TakeTail();
        var totalCount = _headLen + _rootCount + _currentTailLen;
        
        _token = OwnerId.None;
        _root = frozenRoot;
        
        return new RrbList<T>(frozenRoot, finalTail, totalCount, _shift, finalTail.Length, TakeHead(), _headLen, 0);
    }

    // The head for a list: the builder's own when it starts at 0, which the
    // builder then shares, and otherwise a copy.
    private T[]? TakeHead()
    {
        if (_headLen == 0) return null;
        if (_headStart == 0)
        {
            _headShared = true;
            return _head;
        }

        var head = new T[_headLen];
        Array.Copy(_head!, _headStart, head, 0, _headLen);
        return head;
    }

    // The head copied into a leaf's worth that the builder owns, its items at
    // the end.
    private void OwnHead()
    {
        var own = new T[Constants.RRB_BRANCHING];
        var start = Constants.RRB_BRANCHING - _headLen;
        if (_headLen > 0) Array.Copy(_head!, _headStart, own, start, _headLen);
        _head = own;
        _headStart = start;
        _headShared = false;
    }

    // ---------------------------------------------------------------------
    // The ends
    // ---------------------------------------------------------------------
    //
    // Each works in the tail or the head, or for Pop in the leaves waiting for
    // the tree, without touching the tree. When those have nothing to give,
    // or the head is full, the builder becomes a list, the list's own
    // operation is done, and the builder is loaded from the result: at most
    // once in a leaf's worth of operations at that end.

    /// <summary>Removes the last item, and answers it.</summary>
    public T Pop()
    {
        if (_currentTailLen == 0)
        {
            if (_totalLeaves == 0) return PopSlow();
            TakeLastLeafAsTail();
        }

        if (_tailShared) UnshareTail(_currentTail.Length);
        var i = --_currentTailLen;
        var item = _currentTail[i];
        _currentTail[i] = default!;
        return item;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T PopSlow()
    {
        if (Count == 0) throw new InvalidOperationException("The builder is empty");
        var list = ToImmutable();
        var item = list[list.Count - 1];
        Load(list.Pop());
        return item;
    }

    // The last leaf waiting for the tree becomes the tail. The tail is empty.
    private void TakeLastLeafAsTail()
    {
        if (_chunkIndex == 0)
        {
            var last = _chunks!.Count - 1;
            _currentChunk = _chunks[last];
            _chunks.RemoveAt(last);
            _chunkIndex = ChunkSize;
        }

        var leaf = _currentChunk![--_chunkIndex];
        _currentChunk[_chunkIndex] = null!;
        _totalLeaves--;

        _currentTail = leaf.Items;
        _currentTailLen = leaf.Len;
        _tailShared = _token == OwnerId.None || leaf.Owner != _token;
    }

    /// <summary>Adds an item in front of the first.</summary>
    public void Prepend(T item)
    {
        if (_root != null && _headLen < Constants.RRB_BRANCHING)
        {
            if (_headShared || _headStart == 0) OwnHead();
            _head![--_headStart] = item;
            _headLen++;
            return;
        }

        PrependSlow(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PrependSlow(T item) => Load(ToImmutable().Prepend(item));

    /// <summary>Removes the first item, and answers it.</summary>
    public T PopFirst()
    {
        if (_headLen > 0)
        {
            var item = _head![_headStart];
            if (!_headShared) _head[_headStart] = default!;
            _headStart++;
            _headLen--;
            return item;
        }

        return PopFirstSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T PopFirstSlow()
    {
        if (Count == 0) throw new InvalidOperationException("The builder is empty");
        var list = ToImmutable();
        var item = list[0];
        Load(list.PopFirst());
        return item;
    }

    // The tail for a list: the builder's own when it is exactly as long as its
    // count, which the builder then shares, and otherwise a copy.
    private T[] TakeTail()
    {
        if (_currentTailLen == 0) return Array.Empty<T>();
        if (_currentTailLen == _currentTail.Length)
        {
            _tailShared = true;
            return _currentTail;
        }

        var tail = new T[_currentTailLen];
        Array.Copy(_currentTail, tail, _currentTailLen);
        return tail;
    }
    
    private void FlushLeavesToTree()
    {
        // Tracks global progress across all chunks
        int globalLeafCursor = 0; 
        
        if (_root == null)
        {
            // The first leaf becomes the head when a tree follows it. The tree
            // then starts at the second leaf, and is dense. A lone leaf stays
            // the tree: a list's head needs a tree behind it.
            if (_totalLeaves >= 2)
            {
                var first = GetLeaf(globalLeafCursor++);
                _head = first.Items.Length == first.Len ? first.Items : first.Items[..first.Len];
                _headStart = 0;
                _headLen = first.Len;
                _headShared = true;
            }

            // Optimization: Directly grab the first leaf
            _root = GetLeaf(globalLeafCursor++);
            _shift = 0;
            _rootCount += Constants.RRB_BRANCHING;
        }
        
        if (_shift == 0)
        {
            _root = NewParent(_root, _rootCount);
            _shift = Constants.RRB_BITS;
        }
        
        while (globalLeafCursor < _totalLeaves)
        {
            var rootInternal = RrbAlgorithm.AsInternal(_root).EnsureEditable(Token);
            _root = FillRightSpine(rootInternal, _shift, ref globalLeafCursor);

            if (globalLeafCursor < _totalLeaves)
            {
                _root = NewParent(_root, _rootCount);
                _shift += Constants.RRB_BITS;
            }
        }
    }

    // A one-child parent for `child`, which holds `size` items. Siblings will
    // follow it, so it needs a size table unless it is dense and full.
    private InternalNode<T> NewParent(Node<T> child, int size)
    {
        var children = new Node<T>?[Constants.RRB_BRANCHING];
        children[0] = child;

        int[]? table = null;
        if (child.IsRelaxed() || size != 1 << (_shift + Constants.RRB_BITS))
        {
            table = new int[Constants.RRB_BRANCHING];
            table[0] = size;
        }

        return new InternalNode<T>(children, table, 1, Token);
    }

    // Appends pending leaves under `node`, which must be editable. Returns the
    // node, or a relaxed copy of it when a dense node would otherwise get a
    // part-full child before another child or a relaxed child.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private InternalNode<T> FillRightSpine(InternalNode<T> node, int shift, ref int globalCursor)
    {
        // 1. FILL EXISTING LAST CHILD
        if (node.Len > 0 && shift > Constants.RRB_BITS)
        {
            int lastIdx = node.Len - 1;
            InternalNode<T> lastChild = RrbAlgorithm.AsInternal(node.Children[lastIdx]!);
            
            //  Manual Owner Check to skip EnsureEditable call overhead
            if (lastChild.Owner != Token)
                lastChild = lastChild.EnsureEditable(Token, expand: true);
            
            int cursorBefore = globalCursor;
            lastChild = FillRightSpine(lastChild, shift - Constants.RRB_BITS, ref globalCursor);
            node.Children[lastIdx] = lastChild;
            int leavesConsumed = globalCursor - cursorBefore;
            
            if (node.SizeTable != null)
            {
                node.SizeTable[lastIdx] += leavesConsumed * Constants.RRB_BRANCHING;
            }
            else if (lastChild.IsRelaxed() ||
                     (globalCursor < _totalLeaves &&
                      RrbAlgorithm.CountTree(lastChild, shift - Constants.RRB_BITS) != 1 << shift))
            {
                // Dense indexing does not look at size tables below it, and
                // assumes every child but the last is full.
                node = RrbAlgorithm.CreateRelaxedNodeFromDense(node, Token, shift);
            }
        }
        
        if (globalCursor >= _totalLeaves) return node;

        // 2. FILL NEW SIBLINGS
        
        // A. BOTTOM LEVEL (Shift 5) - Array.Copy Optimization
        if (shift == Constants.RRB_BITS)
        {
            // Full leaves after a part-full one need a size table.
            if (node.IsDense() && node.Len > 0 && node.Len < Constants.RRB_BRANCHING &&
                node.Children[node.Len - 1]!.Len < Constants.RRB_BRANCHING)
                node = RrbAlgorithm.CreateRelaxedNodeFromDense(node, Token, shift);

            int spaceRemaining = Constants.RRB_BRANCHING - node.Len;
            int leavesAvailable = _totalLeaves - globalCursor;
            int countToCopy = Math.Min(spaceRemaining, leavesAvailable);

            int startIdx = node.Len;
            var children = node.Children;

            // 1. Resolve starting chunk coordinates ONCE
            int cIdx = globalCursor / ChunkSize;
            int iIdx = globalCursor % ChunkSize;
    
            // 2. Determine source chunk
            var chunk = ChunkAt(cIdx);
    
            // 3. Check if the copy spans across a chunk boundary. Only a full
            // chunk can be crossed: the current one holds every leaf left.
            int availableInChunk = ChunkSize - iIdx;
    
            if (countToCopy <= availableInChunk)
            {
                // FAST PATH: All items in one chunk
                Array.Copy(chunk, iIdx, children, startIdx, countToCopy);
            }
            else
            {
                // STRADDLE PATH: Items cross into the next chunk (Rare: 1 in 16 calls)
        
                // Copy Part 1 (End of current chunk)
                Array.Copy(chunk, iIdx, children, startIdx, availableInChunk);
        
                // Copy Part 2 (Start of next chunk)
                int remaining = countToCopy - availableInChunk;
                chunk = ChunkAt(cIdx + 1);
        
                Array.Copy(chunk, 0, children, startIdx + availableInChunk, remaining);
            }

            // Update SizeTable (Standard loop is fine here, simple integer add)
            if (node.IsRelaxed())
            {
                int currentTotal = node.Len > 0 ? node.SizeTable![node.Len - 1] : 0;
                var table = node.SizeTable;
                for (int i = 0; i < countToCopy; i++)
                {
                    currentTotal += Constants.RRB_BRANCHING;
                    table![startIdx + i] = currentTotal;
                }
            }

            node.Len += (byte)countToCopy;
            globalCursor += countToCopy;
            _rootCount += countToCopy * Constants.RRB_BRANCHING;
            return node;
        }

        // B. INTERNAL LEVELS (Shift > 5)
        int childShift = shift - Constants.RRB_BITS;
        
        while (node.Len < Constants.RRB_BRANCHING && globalCursor < _totalLeaves)
        {
            var newSibling = new InternalNode<T>(Constants.RRB_BRANCHING, Token);
            newSibling.Len = 0;
            
            int cursorBefore = globalCursor;
            newSibling = FillRightSpine(newSibling, childShift, ref globalCursor);
            int leavesConsumed = globalCursor - cursorBefore;
            
            int addedSize = leavesConsumed * Constants.RRB_BRANCHING;

            int idx = node.Len;
            node.Children[idx] = newSibling;
            
            if (node.IsRelaxed())
            {
                int prev = idx > 0 ? node.SizeTable![idx - 1] : 0;
                node.SizeTable![idx] = prev + addedSize;
            }

            node.Len++;
        }

        return node;
    }
}