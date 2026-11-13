/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 *
 * Copyright (c) 2025 Linus Björnstam
 *
 * Portions of this code are based on a port of c-rrb (https://github.com/hypirion/c-rrb),
 * Copyright (c) 2013-2014 Jean Niklas L'orange, licensed under the MIT License.
 */

using System.Text;
using System.Runtime.CompilerServices;

namespace Bjolang.Runtime.Rrb;

[CollectionBuilder(typeof(RrbList), nameof(RrbList.Create))]
public sealed partial class RrbList<T>
{
    /**
     * <summary>
     *     Gets an empty <see cref="RrbList{T}" />.
     * </summary>
     */
    public static readonly RrbList<T> Empty = new();

    internal readonly Node<T>? Root;
    internal readonly int Shift;

    internal readonly T[] Tail;
    internal readonly int TailLen;

    // The head is the tail's mirror: up to a leaf's worth of items in front of
    // the tree, in order, at Head[0..HeadLen). It makes Prepend and PopFirst
    // cheap. A list with no tree keeps everything in its tail, so HeadLen is
    // 0 whenever Root is null. Element i is in the head when i < HeadLen, in
    // the tail when i >= Count - TailLen, and otherwise at i - HeadLen in the
    // tree.
    //
    // Head is null when HeadLen is 0, which is most lists. The constructors
    // then never store it: a new object's fields are already null, and a
    // store of a reference costs a GC write barrier, which on Add and Pop
    // was most of what the head cost. Read Head only when HeadLen > 0.
    internal readonly T[]? Head;
    internal readonly int HeadLen;

    // How many slots at the start of the tree are not the list's: the tree is
    // a dense tree of Gap + TreeCount items, and the list's item i - HeadLen
    // in the tree is at Gap + i - HeadLen. The slots are filled by phantom
    // subtrees (see Phantom<T>), so that indexing a dense tree needs nothing
    // but the add. A multiple of a leaf, and 0 unless the tree is dense:
    // Prepend fills it from its end, and PopFirst widens it, without the size
    // tables that a relaxed left spine would need. Operations in the middle
    // of the tree first turn it into those tables (Materialized).
    internal readonly int Gap;

    internal RrbList(Node<T>? root, T[] tail, int cnt, int shift, int tailLen)
    {
        Root = root;
        Tail = tail;
        Count = cnt;
        Shift = shift;
        TailLen = tailLen;
    }

    internal RrbList(Node<T>? root, T[] tail, int cnt, int shift, int tailLen, T[]? head, int headLen, int gap)
    {
        Root = root;
        Tail = tail;
        Count = cnt;
        Shift = shift;
        TailLen = tailLen;
        Gap = gap;
        if (headLen != 0)
        {
            Head = head;
            HeadLen = headLen;
        }
    }

    // How many of the list's items the tree holds.
    internal int TreeCount => Count - HeadLen - TailLen;

    /**
     * <summary>
     *     Initializes a new instance of the <see cref="RrbList{T}" /> class that is empty.
     * </summary>
     */
    public RrbList()
    {
        Root = null;
        Tail = Array.Empty<T>();
        Count = 0;
        Shift = 0;
        TailLen = 0;
    }

    /**
     * <summary>
     *     Initializes a new instance of the <see cref="RrbList{T}" /> class that contains elements copied from the specified
     *     collection.
     * </summary>
     * <param name="items">The collection whose elements are copied to the new list.</param>
     */
    public RrbList(IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (items is RrbList<T> other)
        {
            Root = other.Root;
            Tail = other.Tail;
            Count = other.Count;
            Shift = other.Shift;
            TailLen = other.TailLen;
            Head = other.Head;
            HeadLen = other.HeadLen;
            Gap = other.Gap;
            return;
        }

        var isArray = items is T[];
        var arr = isArray ? (T[])items : items.ToArray();
        
        // If it was not originally an array, we just allocated it via ToArray(), 
        // so we can safely allow the builder to reuse it for the tail if it's short enough.
        var temp = RrbBuilder<T>.FromArray(arr, reuseArrayIfShorterThan32: !isArray);

        Root = temp.Root;
        Tail = temp.Tail;
        Count = temp.Count;
        Shift = temp.Shift;
        TailLen = temp.TailLen;
        Head = temp.Head;
        HeadLen = temp.HeadLen;
    }

    /**
     * <summary>
     *     Gets a value indicating whether the <see cref="T:System.Collections.Generic.ICollection`1" /> is read-only.
     * </summary>
     */
    public bool IsReadOnly => true;

    // --- ICollection<T> Implementation ---
    void ICollection<T>.Add(T item)
    {
        throw new NotSupportedException("RrbList is immutable.");
    }

    void ICollection<T>.Clear()
    {
        throw new NotSupportedException("RrbList is immutable.");
    }

    bool ICollection<T>.Remove(T item)
    {
        throw new NotSupportedException("RrbList is immutable.");
    }

    /**
     * <summary>
     *     Determines whether the list contains a specific value.
     * </summary>
     * <param name="item">The object to locate in the list.</param>
     * <returns>true if the item is found in the list; otherwise, false.</returns>
     * <remarks>This could be made faster.</remarks>
     */
    public bool Contains(T item)
    {
        return !Iter([NonUserCode] (x) =>
        {
            if (EqualityComparer<T>.Default.Equals(x, item)) return false;

            return true;
        });
    }

    /**
     * <summary>
     *     The number of elements in the list
     * </summary>
     */
    public int Count { get; }

    /**
     * <summary>
     *     Copies the elements of the <see cref="RrbList{T}" /> to an <see cref="T:System.Array" />, starting at a particular
     *     <see cref="T:System.Array" /> index.
     * </summary>
     * <param name="array">
     *     The one-dimensional <see cref="T:System.Array" /> that is the destination of the elements copied
     *     from <see cref="RrbList{T}" />. The <see cref="T:System.Array" /> must have
     * </param>
     * <param name="arrayIndex">The zero-based index in <paramref name="array" /> at which copying begins.</param>
     */
    public void CopyTo(T[] array, int arrayIndex)
    {
        CopyTo(0, array, arrayIndex, Count);
    }


    /**
     * <summary>
     *     Gets the element at the specified index.
     * </summary>
     * <param name="index">The zero-based index of the element to get.</param>
     * <returns>The element at the specified index.</returns>
     */
    // Here we have an indexer that uses AVX for indexing into relaxed nodes. It is about 1.65x faster for a relaxed
    // operation.
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new IndexOutOfRangeException();

            var tailOffset = Count - TailLen;
            // Direct array access
            if (index >= tailOffset) return Tail[index - tailOffset];

            index -= HeadLen;
            if (index < 0) return Head![index + HeadLen];
            index += Gap;

            var node = Root!;
            var shift = Shift;

            if (shift == 0)
                return RrbAlgorithm.AsLeaf(node).Items[index];

            while (node.IsRelaxed())
            {
                var internalNode = RrbAlgorithm.AsInternal(node);
                var (childIndex, relativeIndex) = RrbAlgorithm.GetRelaxedIndex(internalNode, index, shift);

                node = internalNode.Children[childIndex]!;
                index = relativeIndex;
                shift -= Constants.RRB_BITS;
            }

            while (shift > 0)
            {
                var childIndex = (index >> shift) & Constants.RRB_MASK;
                node = RrbAlgorithm.AsInternal(node).Children[childIndex]!;
                shift -= Constants.RRB_BITS;
            }

            return RrbAlgorithm.AsLeaf(node).Items[index & Constants.RRB_MASK];
        }
    }

    /// <summary>
    ///     The leaf array that holds element <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    ///     The elements of a leaf are consecutive elements of the list, so a walk
    ///     can read a leaf as an array and look up only the next one: for
    ///     <paramref name="index"/> at <paramref name="position"/> in the array,
    ///     the elements from <c>index - position</c> to
    ///     <c>index - position + length - 1</c> are <c>items[0]</c> to
    ///     <c>items[length - 1]</c>. The array may be longer than
    ///     <paramref name="length"/>, and it is shared with the list, so the caller
    ///     must not change it.
    /// </remarks>
    // The tail case is inlined, and the descent of the tree is a call: a walk of
    // a short vec, which is all tail, then looks up its leaf with no call.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T[] LeafAt(int index, out int position, out int length)
    {
        if ((uint)index >= (uint)Count) throw new IndexOutOfRangeException();

        var tailOffset = Count - TailLen;
        if (index >= tailOffset)
        {
            position = index - tailOffset;
            length = TailLen;
            return Tail;
        }

        if (index < HeadLen)
        {
            position = index;
            length = HeadLen;
            return Head!;
        }

        return TreeLeafAt(index - HeadLen + Gap, out position, out length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T[] TreeLeafAt(int index, out int position, out int length) =>
        DescendToLeaf(index, out position, out length);

    /// <summary>
    ///     The leaf array that holds element <paramref name="index"/>, as
    ///     <see cref="LeafAt"/> returns it, with no call and no check of the
    ///     index.
    /// </summary>
    /// <remarks>
    ///     For a walk's step to its next leaf, which is inlined in the walk's
    ///     loop. On Linux .NET keeps no float register across a call, so a
    ///     loop with a call on any path keeps its float sums in memory.
    ///     <list type="bullet">
    ///         <item>The caller makes sure that <paramref name="index"/> is
    ///         an element of the list.</item>
    ///         <item>Everything it does is inlined: the tail, the head, and
    ///         the descent of the tree (<see cref="DescendToLeaf"/>).</item>
    ///     </list>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T[] LeafInWalk(int index, out int position, out int length)
    {
        var tailOffset = Count - TailLen;
        if (index >= tailOffset)
        {
            position = index - tailOffset;
            length = TailLen;
            return Tail;
        }

        if (index < HeadLen)
        {
            position = index;
            length = HeadLen;
            return Head!;
        }

        return DescendToLeaf(index - HeadLen + Gap, out position, out length);
    }

    // The descent from the root to the leaf of tree index `index`: a search of
    // the size table at each relaxed level, then a shift at each dense one.
    // Every node access is an `Unsafe.As` (RrbAlgorithm.AsInternal, AsLeaf),
    // so the descent makes no call.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T[] DescendToLeaf(int index, out int position, out int length)
    {
        var node = Root!;
        var shift = Shift;

        while (shift > 0 && node.IsRelaxed())
        {
            var internalNode = RrbAlgorithm.AsInternal(node);
            var (childIndex, relativeIndex) = RrbAlgorithm.GetRelaxedIndex(internalNode, index, shift);

            node = internalNode.Children[childIndex]!;
            index = relativeIndex;
            shift -= Constants.RRB_BITS;
        }

        while (shift > 0)
        {
            var childIndex = (index >> shift) & Constants.RRB_MASK;
            node = RrbAlgorithm.AsInternal(node).Children[childIndex]!;
            shift -= Constants.RRB_BITS;
        }

        var leaf = RrbAlgorithm.AsLeaf(node);
        position = index & Constants.RRB_MASK;
        length = leaf.Len;
        return leaf.Items;
    }

    /// <summary>
    ///     True if the root of the tree has a size table.
    /// </summary>
    /// <remarks>
    ///     Then <see cref="LeafAt"/> searches a size table at each relaxed
    ///     level, and a walk does better with a <see cref="TreePath{T}"/>. A
    ///     dense tree is found by shifts, and LeafAt is as fast as a path.
    /// </remarks>
    public bool TreeIsRelaxed => Root != null && Root.IsRelaxed() && Gap == 0;

    /// <summary>
    ///     True if element <paramref name="index"/> is in the tree, not in the
    ///     head or the tail.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool InTree(int index) => index >= HeadLen && index < Count - TailLen;

    /**
     * <summary>
     *     Copies a range of elements from the RrbList to a compatible one-dimensional array.
     * </summary>
     */
    public void CopyTo(int sourceIndex, T[] destination, int destinationIndex, int count)
    {
        if (sourceIndex < 0 || sourceIndex > Count) throw new ArgumentOutOfRangeException(nameof(sourceIndex));
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (destinationIndex < 0) throw new ArgumentOutOfRangeException(nameof(destinationIndex));
        if (destination.Length - destinationIndex < count)
            throw new ArgumentException("Destination array is too small.");
        if (count == 0) return;

        // 0. Copy from Head (if the range starts in it)
        if (sourceIndex < HeadLen)
        {
            var itemsFromHead = Math.Min(count, HeadLen - sourceIndex);
            Array.Copy(Head!, sourceIndex, destination, destinationIndex, itemsFromHead);

            destinationIndex += itemsFromHead;
            count -= itemsFromHead;
            sourceIndex += itemsFromHead;
            if (count == 0) return;
        }

        var tailOffset = Count - TailLen;

        // 1. Copy from Tree (if the range starts before the tail)
        if (sourceIndex < tailOffset)
        {
            var itemsFromTree = Math.Min(count, tailOffset - sourceIndex);

            if (Root != null)
                CopyRangeNode(Root, Shift, sourceIndex - HeadLen + Gap, destination, destinationIndex, itemsFromTree);

            destinationIndex += itemsFromTree;
            count -= itemsFromTree;
            sourceIndex += itemsFromTree; // Should equal tailOffset now
        }

        // 2. Copy from Tail (if there are remaining items to copy)
        if (count > 0)
        {
            var idxInTail = sourceIndex - tailOffset;
            Array.Copy(Tail, idxInTail, destination, destinationIndex, count);
        }
    }

    private static void CopyRangeNode(Node<T> node, int shift, int offsetInNode, T[] dest, int destIdx, int count)
    {
        // Base Case: Leaf
        if (shift == 0)
        {
            var leaf = RrbAlgorithm.AsLeaf(node);
            Array.Copy(leaf.Items, offsetInNode, dest, destIdx, count);
            return;
        }

        // Recursive Step: Internal
        var internalNode = RrbAlgorithm.AsInternal(node);

        var (childIdx, subIdx) = RrbAlgorithm.GetChildIndexAvx(internalNode, offsetInNode, shift);

        while (count > 0 && childIdx < internalNode.Len)
        {
            var child = internalNode.Children[childIdx]!;

            int childSize;
            if (internalNode.SizeTable != null)
            {
                var prev = childIdx > 0 ? internalNode.SizeTable[childIdx - 1] : 0;
                childSize = internalNode.SizeTable[childIdx] - prev;
            }
            else
            {
                if (childIdx == internalNode.Len - 1)
                    childSize = RrbAlgorithm.CountTree(child, shift - Constants.RRB_BITS);
                else
                    childSize = 1 << shift;
            }

            var availableInChild = childSize - subIdx;
            var toCopy = Math.Min(count, availableInChild);

            CopyRangeNode(child, shift - Constants.RRB_BITS, subIdx, dest, destIdx, toCopy);

            count -= toCopy;
            destIdx += toCopy;

            childIdx++;
            subIdx = 0;
        }
    }

    /**
     * <summary>
     *     Creates a new RRB-List from an <see cref="IEnumerable{T}" />.
     * </summary>
     * <param name="items">The items to create the list from.</param>
     * <returns>A new RRB-List containing the items.</returns>
     */
    public static RrbList<T> Create(IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (items is RrbList<T> rrb) return rrb;
        if (items is ICollection<T> c && c.Count == 0) return Empty;
        return new RrbList<T>(items);
    }

    /**
     * <summary>
     *     Creates a new RRB-List from a <see cref="ReadOnlySpan{T}" />.
     * </summary>
     * <param name="items">The items to create the list from.</param>
     * <returns>A new RRB-List containing the items.</returns>
     */
    public static RrbList<T> Create(ReadOnlySpan<T> items)
    {
        return RrbBuilder<T>.FromSpan(items);
    }


    /**
     * <summary>
     *     Returns a new list with the specified item added to the end.
     * </summary>
     * <param name="item">The item to add.</param>
     * <returns>A new list with the item added.</returns>
     */
    public RrbList<T> Add(T item)
    {
        // Case 1: Tail has room
        if (TailLen < Constants.RRB_BRANCHING)
        {
            var newTail = new T[TailLen + 1];
            Array.Copy(Tail, 0, newTail, 0, TailLen);
            newTail[TailLen] = item;

            return new RrbList<T>(Root, newTail, Count + 1, Shift, TailLen + 1, Head, HeadLen, Gap);
        }

        return AddPushingTail(item);
    }

    // Add when the tail is full: it goes into the tree, which keeps its Gap.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private RrbList<T> AddPushingTail(T item)
    {
        var newShift = Shift;
        var oldTailLeaf = new LeafNode<T>(Tail, TailLen, OwnerId.None); // Wrap current tail

        // Delegate to algorithm with wrapped leaf
        var newRoot = RrbAlgorithm.AppendLeafToTree(Root, oldTailLeaf, ref newShift, OwnerId.None);

        // The Gap is kept while the tree stays dense.
        var gap = Gap;
        var treeItems = TreeCount + TailLen;
        if (gap > 0) newRoot = NormalizeGap(newRoot, ref newShift, ref gap, treeItems);

        var newTailArr = new[] { item };

        return new RrbList<T>(newRoot, newTailArr, Count + 1, newShift, 1, Head, HeadLen, gap);
    }

    /**
     * <summary>
     *     Returns a new list with the element at the specified index replaced with the new value.
     * </summary>
     * <param name="index">The index of the element to replace.</param>
     * <param name="value">The new value for the element.</param>
     * <returns>A new list with the replaced item.</returns>
     */
    public RrbList<T> SetItem(int index, T value)
    {
        if (index < 0 || index >= Count) throw new IndexOutOfRangeException();

        var tailOffset = Count - TailLen;
        if (index >= tailOffset)
        {
            var newTail = new T[TailLen];
            Array.Copy(Tail, 0, newTail, 0, TailLen);
            newTail[index - tailOffset] = value;

            return new RrbList<T>(Root, newTail, Count, Shift, TailLen, Head, HeadLen, Gap);
        }

        if (index < HeadLen)
        {
            var newHead = new T[HeadLen];
            Array.Copy(Head!, 0, newHead, 0, HeadLen);
            newHead[index] = value;

            return new RrbList<T>(Root, Tail, Count, Shift, TailLen, newHead, HeadLen, Gap);
        }

        var newRoot = RrbAlgorithm.Update(Root!, index - HeadLen + Gap, value, Shift, OwnerId.None);
        return new RrbList<T>(newRoot, Tail, Count, Shift, TailLen, Head, HeadLen, Gap);
    }


    /**
     * <summary>
     *     Creates a mutable builder from the current list.
     * </summary>
     * <returns>A new <see cref="RrbBuilder{T}" />.</returns>
     */
    public RrbBuilder<T> ToBuilder(int leafCapacify = Constants.RRB_BRANCHING)
    {
        return new RrbBuilder<T>(this);
    }


    /**
     * <summary>
     *     Creates a slice of the list.
     * </summary>
     * <param name="start">The zero-based index at which to begin the slice.</param>
     * <param name="count">The number of elements in the slice.</param>
     * <returns>A new list that is a slice of the original list.</returns>
     */
    public RrbList<T> Slice(int start, int count)
    {
        if (start < 0 || count < 0 || start + count > Count)
            throw new ArgumentOutOfRangeException();
        if (count == 0) return Empty;
        if (start == 0 && count == Count) return this;

        // --- 1. Small Slice Optimization ---
        if (count <= Constants.RRB_BRANCHING * 2)
        {
            var arr = new T[count];
            CopyTo(start, arr, 0, count);

            if (count <= Constants.RRB_BRANCHING)
                // Tail Only
                return new RrbList<T>(null, arr, count, 0, count);

            // Root + Tail
            var rootLen = Constants.RRB_BRANCHING;
            var tailLen = count - rootLen;

            var rootItems = new T[rootLen];
            var tailItems = new T[tailLen];

            Array.Copy(arr, 0, rootItems, 0, rootLen);
            Array.Copy(arr, rootLen, tailItems, 0, tailLen);

            var rootNode = new LeafNode<T>(rootItems, rootLen, OwnerId.None);
            return new RrbList<T>(rootNode, tailItems, count, 0, tailLen);
        }

        // --- 2. With a head: slice the rest, and put back what is kept of the head ---
        if (HeadLen > 0) return SliceWithHead(start, count);

        // From here on there is no head, so the tree starts at 0.
        var treeSize = Count - TailLen;

        // --- 3. Slice falls into tail ---
        if (start >= treeSize)
        {
            var tTail = Tail.AsSpan().Slice(start - treeSize, count).ToArray();
            return new RrbList<T>(null, tTail, count, 0, tTail.Length);
        }

        // --- 4. Drop/skip. Start is less than TreeSize, meaning Root is not null ---
        if (start != 0 && start + count == Count)
        {
            // A dense tree widens its Gap, and what is left of the leaf the
            // drop ends in becomes the head.
            if (Root!.IsDense()) return DropTreeFront(start);

            // A relaxed tree gives up its first leaf when the drop ends in it,
            // and is cut by SliceLeftRec otherwise.
            if (start <= RrbAlgorithm.FirstLeafLen(Root, Shift)) return PromoteFirstLeaf(start);

            var dropRoot = RrbAlgorithm.SliceLeftRec(Root, start, Shift);
            int dropShift = Shift;
            dropRoot = RrbAlgorithm.LowerRoot(dropRoot, ref dropShift, count - TailLen);
            return new RrbList<T>(dropRoot, Tail, count, dropShift, TailLen);
        }

        // --- 5. Take. Start is 0 ---
        if (start == 0)
        {
            // All of the tree, and some of the tail.
            if (count > treeSize)
            {
                var keptTail = count - treeSize;
                var takeTailItems = new T[keptTail];
                Array.Copy(Tail, takeTailItems, keptTail);
                return new RrbList<T>(Root, takeTailItems, count, Shift, keptTail, null, 0, Gap);
            }

            // Inside the tree, which keeps its Gap.
            var takeRoot = RrbAlgorithm.SliceRightAndPromote(Root!, Gap + count, Shift,
                out T[] takeTail,
                out int len);
            int tempShift = Shift;
            if (Gap > 0)
            {
                var gap = Gap;
                takeRoot = NormalizeGap(takeRoot, ref tempShift, ref gap, count - len);
                return new RrbList<T>(takeRoot, takeTail, count, tempShift, len, null, 0, gap);
            }

            takeRoot = RrbAlgorithm.LowerRoot(takeRoot, ref tempShift, count - len);
            return new RrbList<T>(takeRoot, takeTail, count, tempShift, len);
        }

        // --- 6. In the middle of a dense tree: a drop, and then a take ---
        // Both keep a dense tree dense: the drop leaves a Gap, and what is
        // left of the leaf it ends in as the head, and the take cuts the right
        // edge, which a dense tree may leave part-full. A cut from the left
        // by RrbAlgorithm.Slice would give the left spine size tables.
        // (A tree with a Gap is dense, so this takes those too.)
        if (Root!.IsDense())
        {
            if (start + count <= treeSize) return SliceDenseTree(start, count);
            return DropTreeFront(start).Slice(0, count);
        }

        // --- 7. General Case: in the middle of a relaxed tree ---

        var treeSliceStart = start;
        var treeSliceEnd = Math.Min(start + count, treeSize);
        var treeSliceCount = Math.Max(0, treeSliceEnd - treeSliceStart);

        Node<T>? newRoot = null;

        // Receive raw array + length directly
        T[] promotedTail = Array.Empty<T>();
        var promotedTailLen = 0;
        var newShift = Shift;

        if (treeSliceCount > 0)
            newRoot = RrbAlgorithm.Slice(Root!, treeSliceStart, treeSliceCount, ref newShift, out promotedTail,
                out promotedTailLen);

        // --- 3. Handle Tail Merging ---
        T[] finalTail;
        var originalTailNeeded = start + count > treeSize;

        if (originalTailNeeded)
        {
            var tailStartIdx = Math.Max(0, start - treeSize);
            var tailEndIdx = start + count - treeSize;
            var tailCount = tailEndIdx - tailStartIdx;

            if (promotedTailLen > 0)
            {
                // Merge: [Tree Promoted] + [Original Tail Slice]
                if (promotedTailLen + tailCount <= Constants.RRB_BRANCHING)
                {
                    finalTail = new T[promotedTailLen + tailCount];
                    Array.Copy(promotedTail, 0, finalTail, 0, promotedTailLen);
                    Array.Copy(Tail, tailStartIdx, finalTail, promotedTailLen, tailCount);
                }
                else
                {
                    // Overflow: promotedTail goes back to Tree
                    var leafWrapper = new LeafNode<T>(promotedTail, promotedTailLen, OwnerId.None);
                    newRoot = RrbAlgorithm.AppendLeafToTree(newRoot, leafWrapper, ref newShift, OwnerId.None);

                    finalTail = new T[tailCount];
                    Array.Copy(Tail, tailStartIdx, finalTail, 0, tailCount);
                }
            }
            else
            {
                // Only old tail slice
                finalTail = new T[tailCount];
                Array.Copy(Tail, tailStartIdx, finalTail, 0, tailCount);
            }
        }
        else
        {
            // Only tree promoted tail
            if (promotedTailLen == promotedTail.Length)
            {
                finalTail = promotedTail;
            }
            else
            {
                finalTail = new T[promotedTailLen];
                if (promotedTailLen > 0)
                    Array.Copy(promotedTail, 0, finalTail, 0, promotedTailLen);
            }
        }

        newRoot = RrbAlgorithm.LowerRoot(newRoot, ref newShift, count - finalTail.Length);
        return new RrbList<T>(newRoot, finalTail, count, newShift, finalTail.Length);
    }

    /**
     * <summary>
     *     Merges two lists together.
     * </summary>
     * <param name="other">The list to merge with the current list.</param>
     * <param name="pure">
     *   Whether to preform a pure concatenation. This is about 50% slower, but leaves the tree
     *   in a cleaner state, where some operations may be slightly faster. Defaults to false.
     * </param>
     * <returns>A new list containing elements from both lists.</returns>
     */
    public RrbList<T> Merge(RrbList<T> other, bool pure = false)
    {
        if (other.Count == 0) return this;
        if (Count == 0) return other;

        // Two lists whose positions line up, as a split's halves do, meet
        // without moving an item, and the tree stays dense (RrbList.Join.cs).
        if (other.Root != null && TryAlignedMerge(other) is { } aligned) return aligned;

        // Merging a tree with just a tail (other.Root == null)
        // This avoids the Concat overhead for common "AppendAll" patterns.
        if (other.Root == null)
        {
            // Both tails fit in one buffer (Simple array copy)
            if (TailLen + other.TailLen <= Constants.RRB_BRANCHING)
            {
                var newItems = new T[TailLen + other.TailLen];
                Array.Copy(Tail, 0, newItems, 0, TailLen);
                Array.Copy(other.Tail, 0, newItems, TailLen, other.TailLen);

                return new RrbList<T>(Root, newItems, Count + other.Count, Shift, newItems.Length, Head, HeadLen, Gap);
            }

            // Overflow. Current tail must be pushed into the tree.
            // We fill the current tail buffer to 32 using items from 'other.Tail',
            // push it into the tree, and the remainder of 'other.Tail' becomes the new tail.
            
            var spaceInThis = Constants.RRB_BRANCHING - TailLen;
            var overflow = other.TailLen - spaceInThis;

            var newLeafItems = new T[Constants.RRB_BRANCHING];
            Array.Copy(Tail, 0, newLeafItems, 0, TailLen);
            Array.Copy(other.Tail, 0, newLeafItems, TailLen, spaceInThis);

            var newLeaf = new LeafNode<T>(newLeafItems, Constants.RRB_BRANCHING, OwnerId.None);

            var newTailItems = new T[overflow];
            Array.Copy(other.Tail, spaceInThis, newTailItems, 0, overflow);

            var newShift = Shift;
            // AppendLeafToTree safely handles Root being null (promotes leaf to tree logic)
            var newRoot = RrbAlgorithm.AppendLeafToTree(Root, newLeaf, ref newShift, OwnerId.None);

            // The Gap is kept while the tree stays dense.
            var gap = Gap;
            if (gap > 0) newRoot = NormalizeGap(newRoot, ref newShift, ref gap, TreeCount + Constants.RRB_BRANCHING);

            return new RrbList<T>(newRoot, newTailItems, Count + other.Count, newShift, overflow, Head, HeadLen, gap);
        }

        // Two trees go through Concat, which knows nothing of a Gap.
        if (Gap > 0 || other.Gap > 0) return Materialized().Merge(other.Materialized(), pure);
        
        // Full Merge: Tree + Tree
        // Our head stays our head, and other's tail stays the tail. What is
        // between them, our tree, our tail, other's head and other's tree,
        // becomes one tree.
        var newLeftShift = Shift;
        Node<T>? leftTree = Root;
        var leftTreeSize = TreeCount;

        // Our tail and other's head meet at the seam: they go into the left
        // tree as one leaf when they fit in one, and as a full leaf and the
        // rest when they do not.
        // If both are empty, we must not push an empty leaf, as that corrupts the tree structure.
        var seamLen = TailLen + other.HeadLen;
        if (seamLen > 0)
        {
            T[] seam;
            if (other.HeadLen == 0)
            {
                seam = Tail;
            }
            else
            {
                seam = new T[seamLen];
                Array.Copy(Tail, 0, seam, 0, TailLen);
                Array.Copy(other.Head!, 0, seam, TailLen, other.HeadLen);
            }

            var firstLen = Math.Min(seamLen, Constants.RRB_BRANCHING);
            T[] firstItems;
            if (firstLen == seamLen)
            {
                firstItems = seam;
            }
            else
            {
                firstItems = new T[firstLen];
                Array.Copy(seam, 0, firstItems, 0, firstLen);
            }

            leftTree = RrbAlgorithm.AppendLeafToTree(leftTree, new LeafNode<T>(firstItems, firstLen, OwnerId.None),
                ref newLeftShift, OwnerId.None);

            if (seamLen > firstLen)
            {
                var restLen = seamLen - firstLen;
                var restItems = new T[restLen];
                Array.Copy(seam, firstLen, restItems, 0, restLen);
                leftTree = RrbAlgorithm.AppendLeafToTree(leftTree, new LeafNode<T>(restItems, restLen, OwnerId.None),
                    ref newLeftShift, OwnerId.None);
            }

            leftTreeSize += seamLen;
        }

        // Concat
        // leftTree is not null here: if seamLen > 0 it was set by AppendLeafToTree,
        // and if TailLen == 0, Root must be non-null (otherwise Count would be 0).
        // The right tree size is other's count without its head and tail.
        var (combinedTree, _) = RrbAlgorithm.Concat(
            leftTree!, leftTreeSize, 
            other.Root!, other.TreeCount, 
            newLeftShift, other.Shift, 
            out var combinedShift
        );

        // other.Tail is preserved as the new tail
        return new RrbList<T>(combinedTree, other.Tail, Count + other.Count, combinedShift, other.TailLen, Head, HeadLen, 0);
    }


    /**
     * <summary>
     *     Splits the list at several indices: one more part than there are points.
     *     Part i holds the items from points[i-1] up to points[i], with 0 before the first point and
     *     Count after the last. The parts share the list's nodes, as <see cref="Split" /> does.
     * </summary>
     * <param name="points">The indices, in ascending order. Two equal points give an empty part.</param>
     * <exception cref="ArgumentException">When the points are not in ascending order.</exception>
     * <exception cref="ArgumentOutOfRangeException">When a point is outside 0..Count.</exception>
     */
    public RrbList<T>[] SplitAt(ReadOnlySpan<int> points)
    {
        var prev = 0;
        foreach (var p in points)
        {
            if (p < 0 || p > Count) throw new ArgumentOutOfRangeException(nameof(points), p, "a point is outside the list");
            if (p < prev) throw new ArgumentException("the points are not in ascending order", nameof(points));
            prev = p;
        }

        // From the right: each split cuts the part that is left of the points done so far,
        // so every cut is one Split of a smaller list.
        var parts = new RrbList<T>[points.Length + 1];
        var rest = this;
        for (var i = points.Length - 1; i >= 0; i--)
        {
            var (left, right) = rest.Split(points[i]);
            parts[i + 1] = right;
            rest = left;
        }

        parts[0] = rest;
        return parts;
    }

    /**
     * <summary>
     *     Splits the list into two at the specified index.
     * </summary>
     * <param name="index">The index at which to split the list.</param>
     * <returns>A tuple containing the left and right parts of the split list.</returns>
     */
    public (RrbList<T> Left, RrbList<T> Right) Split(int index)
    {
        if (index < 0 || index > Count) throw new IndexOutOfRangeException();

        if (index == 0) return (Empty, this);
        if (index == Count) return (this, Empty);

        // --- Case 0: There is a head ---
        if (HeadLen > 0)
        {
            // Split within the head (or right after it): the left is all head,
            // which has no tree and so goes in a tail.
            if (index <= HeadLen)
            {
                var leftItems = new T[index];
                Array.Copy(Head!, 0, leftItems, 0, index);

                var rightHeadLen = HeadLen - index;
                var rightHead = Array.Empty<T>();
                if (rightHeadLen > 0)
                {
                    rightHead = new T[rightHeadLen];
                    Array.Copy(Head!, index, rightHead, 0, rightHeadLen);
                }

                return (
                    new RrbList<T>(null, leftItems, index, 0, index),
                    new RrbList<T>(Root, Tail, Count - index, Shift, TailLen, rightHead, rightHeadLen, Gap));
            }

            // Split the rest, and the head goes back on the left.
            var (restLeft, restRight) = Headless().Split(index - HeadLen);
            return (WithHead(Head, HeadLen, restLeft), restRight);
        }

        var treeSize = Count - TailLen;

        // --- Case 1: Split is in the tail ---
        if (index >= treeSize)
        {
            var tailSplitIdx = index - treeSize;

            if (tailSplitIdx == 0)
            {
                // Left gets entire tree (empty tail — all nodes in a dense tree are full).
                // Right gets the original tail.
                return (
                    new RrbList<T>(Root, Array.Empty<T>(), treeSize, Shift, 0, null, 0, Gap),
                    new RrbList<T>(null, Tail, TailLen, 0, TailLen));
            }

            // Split within the tail — pure array work, no tree traversal.
            var leftTailItems = new T[tailSplitIdx];
            Array.Copy(Tail, 0, leftTailItems, 0, tailSplitIdx);

            var rightTailLen = TailLen - tailSplitIdx;
            var rightTailItems = new T[rightTailLen];
            Array.Copy(Tail, tailSplitIdx, rightTailItems, 0, rightTailLen);

            return (
                new RrbList<T>(Root, leftTailItems, index, Shift, tailSplitIdx, null, 0, Gap),
                new RrbList<T>(null, rightTailItems, Count - index, 0, rightTailLen));
        }

        // --- Case 2: Split is in the tree ---
        // A dense tree is cut into a take and a drop, which both keep it
        // dense: the left half's right edge may be part-full, and the right
        // half starts after a Gap, with what is left of the leaf the cut is
        // in as its head. (A tree with a Gap is dense, so this takes those too.)
        if (Root!.IsDense()) return SplitDenseTree(index);

        // A relaxed tree: single-pass algorithm.

        RrbAlgorithm.SplitTree(Root!, index, Shift,
            out var leftRoot, out var leftTail, out var leftTailLen,
            out var rightRoot);

        var leftShift = Shift;
        var rightShift = Shift;

        // Each half is no taller than its items need.
        leftRoot = RrbAlgorithm.LowerRoot(leftRoot, ref leftShift, index - leftTailLen);
        rightRoot = RrbAlgorithm.LowerRoot(rightRoot, ref rightShift, Count - index - TailLen);

        // Right list reuses the original tail directly.
        return (
            new RrbList<T>(leftRoot, leftTail, index, leftShift, leftTailLen),
            new RrbList<T>(rightRoot, Tail, Count - index, rightShift, TailLen));
    }

    /**
     * <summary>
     *     Returns a new, fully compacted (dense) version of this list.
     *     This operation is O(N) as it rebuilds the tree structure.
     *     Use this if the tree depth becomes excessive due to repeated relaxed operations.
     * </summary>
     */
    public RrbList<T> Compact()
    {
        if (Root == null) return this;
        var builder = new RrbBuilder<T>();
        foreach (var item in this) builder.Add(item);
        return builder.ToImmutable();
    }


    /**
     * <summary>
     *     Returns a string representation with debug information
     * </summary>
     * <returns>A string that represents the current object.</returns>
     */
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"RrbList<{typeof(T).Name}> (Cnt: {Count}, Height: {Shift / Constants.RRB_BITS}{(Gap > 0 ? $", Gap: {Gap}" : "")})");

        if (HeadLen > 0)
        {
            sb.Append("  [Head]: ");
            PrintItems(sb, Head!, HeadLen);
            sb.AppendLine();
        }

        if (TailLen > 0)
        {
            sb.Append("  [Tail]: ");
            PrintItems(sb, Tail, TailLen);
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("  [Tail]: <empty>");
        }

        if (Root != null)
        {
            sb.AppendLine("  [Tree Root]:");
            PrintNode(sb, Root, 1, Shift);
        }
        else
        {
            sb.AppendLine("  [Tree Root]: <null>");
        }

        return sb.ToString();
    }

    private void PrintNode(StringBuilder sb, Node<T> node, int indentLevel, int shift)
    {
        var indent = new string(' ', indentLevel * 2 + 2);

        if (node is LeafNode<T> leaf)
        {
            sb.Append($"{indent}Leaf (Len: {leaf.Len}): ");
            PrintItems(sb, leaf.Items, leaf.Len);
            sb.AppendLine();
        }
        else if (node is InternalNode<T> inode)
        {
            var tableInfo = inode.SizeTable != null
                ? $" [TABLE: {string.Join(", ", inode.SizeTable)}]"
                : " [Balanced]";

            sb.AppendLine($"{indent}Node (Len: {inode.Len}, Shift: {shift}){tableInfo}");

            for (var i = 0; i < inode.Len; i++)
            {
                // A run of phantoms is one line.
                var phantoms = 0;
                while (i < inode.Len && inode.Children[i]!.IsPhantom())
                {
                    phantoms++;
                    i++;
                }

                if (phantoms > 0)
                    sb.AppendLine($"{indent}  Phantom x{phantoms} (Shift: {shift - Constants.RRB_BITS})");
                if (i < inode.Len)
                    PrintNode(sb, inode.Children[i]!, indentLevel + 1, shift - Constants.RRB_BITS);
            }
        }
    }

    private void PrintItems(StringBuilder sb, T[] items, int count)
    {
        sb.Append("[");
        var limit = Math.Min(count, 10);
        for (var i = 0; i < limit; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(items[i]);
        }

        if (count > limit) sb.Append($", ... and {count - limit} more");
        sb.Append("]");
    }


    /**
     * <summary>
     *     Removes the last element from the list.
     * </summary>
     * <returns>A new list with the last element removed.</returns>
     */
    public RrbList<T> Pop()
    {
        // Fast Path: Just shrink the tail count. Kept small enough to inline:
        // the empty list and the slow path are out of line.
        var tailLen = TailLen;
        if (tailLen > 1)
        {
            var newTailItems = new T[tailLen - 1];
            Array.Copy(Tail, 0, newTailItems, 0, tailLen - 1);

            return new RrbList<T>(Root, newTailItems, Count - 1, Shift, tailLen - 1, Head, HeadLen, Gap);
        }

        return PopSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private RrbList<T> PopSlow()
    {
        if (Count == 0) throw new InvalidOperationException("List is empty");

        // Slow Path: Tail becomes empty.
        // We rely on Slice to find the new tail from the tree.
        return Slice(0, Count - 1);
    }

    /**
     * <summary>
     *     Verifies the internal structural integrity of the RRB-Tree. Throws an exception if an inconsistency is found.
     * </summary>
     */
    public void VerifyIntegrity()
    {
        if (HeadLen < 0 || HeadLen > Constants.RRB_BRANCHING)
            throw new Exception($"Integrity Error: HeadLen {HeadLen} out of range");
        if (HeadLen > 0 && (Head == null || Head.Length < HeadLen))
            throw new Exception($"Integrity Error: Head array size {Head?.Length} < HeadLen {HeadLen}");
        if (TailLen < 0 || TailLen > Constants.RRB_BRANCHING)
            throw new Exception($"Integrity Error: TailLen {TailLen} out of range");

        if (Root == null)
        {
            if (HeadLen != 0)
                throw new Exception($"Integrity Error: list without a tree has a head of {HeadLen}");
            if (Count != TailLen)
                throw new Exception($"Integrity Error: list without a tree has Count {Count} but TailLen {TailLen}");
            return;
        }

        // 0. The Gap: a multiple of a leaf, only on a dense tree with items
        // past it, and exactly the slots the phantoms take, all at the start.
        if (Gap < 0 || Gap % Constants.RRB_BRANCHING != 0)
            throw new Exception($"Integrity Error: Gap {Gap} is not a multiple of a leaf");
        if (Gap > 0)
        {
            if (Root.IsRelaxed())
                throw new Exception($"Integrity Error: Gap {Gap} on a relaxed tree");
            if (TreeCount == 0)
                throw new Exception($"Integrity Error: Gap {Gap} on a tree with no items");
        }

        if (Root.IsPhantom()) throw new Exception("Integrity Error: the root is a phantom");
        var phantomSlots = PhantomSlots(Root, Shift);
        var leading = LeadingPhantomSlots(Root, Shift);
        if (phantomSlots != Gap || leading != Gap)
            throw new Exception(
                $"Integrity Error: Gap {Gap}, but phantoms take {phantomSlots} slots, {leading} of them at the start");

        // 1. Traverse and verify structure/invariants
        VerifyNode(Root, Shift);

        // 2. Verify total count matches metadata
        var countedSize = CountNode(Root, Shift);
        if (countedSize != Gap + TreeCount)
            throw new Exception(
                $"Integrity Error: Root tracks {TreeCount} items after a Gap of {Gap}, but traversal found {countedSize} slots.");

        if (Tail.Length < TailLen)
            throw new Exception($"Integrity Error: Tail array size {Tail.Length} < TailLen {TailLen}");
    }

    /**
     * <summary>
     *     Verifies the search step invariant at every internal node, and throws when a node breaks it.
     *     A node may have at most <c>RRB_EXTRAS</c> more children than the fewest that could hold
     *     what its children hold, which is what bounds how far a relaxed lookup has to search past
     *     the slot the index points at.
     * </summary>
     */
    public void VerifySearchStep()
    {
        if (Root != null) VerifySearchStep(Root, Shift, "root");
    }

    /**
     * <summary>
     *     The furthest a lookup has to walk in any relaxed node, past the child the index points
     *     at when every child is taken to be full. A lookup in a dense tree walks 0.
     * </summary>
     */
    public int MaxSearchDistance() => Root == null ? 0 : MaxSearchDistance(Root, Shift);

    /**
     * <summary>
     *     The tree's height in levels above the leaves, how many leaves and internal nodes it has,
     *     and the fewest leaves its items would fit in. For watching a tree's shape over time.
     * </summary>
     */
    public (int Height, int Leaves, int Internal, int FewestLeaves) Shape()
    {
        if (Root == null) return (0, 0, 0, 0);
        var leaves = 0;
        var internalNodes = 0;
        CountShape(Root, Shift, ref leaves, ref internalNodes);
        var treeItems = TreeCount;
        return (Shift / Constants.RRB_BITS, leaves, internalNodes, (treeItems + Constants.RRB_BRANCHING - 1) / Constants.RRB_BRANCHING);
    }

    // How many slots the phantoms under `node` take.
    private static long PhantomSlots(Node<T> node, int shift)
    {
        if (node.IsPhantom()) return 1L << (shift + Constants.RRB_BITS);
        if (shift == 0) return 0;

        var inode = (InternalNode<T>)node;
        long sum = 0;
        for (var i = 0; i < inode.Len; i++) sum += PhantomSlots(inode.Children[i]!, shift - Constants.RRB_BITS);
        return sum;
    }

    // How many slots the phantoms at the start of `node` take, before its first item.
    private static long LeadingPhantomSlots(Node<T> node, int shift)
    {
        long sum = 0;
        while (true)
        {
            if (node.IsPhantom()) return sum + (1L << (shift + Constants.RRB_BITS));
            if (shift == 0) return sum;

            var inode = (InternalNode<T>)node;
            var i = 0;
            while (i < inode.Len && inode.Children[i]!.IsPhantom())
            {
                sum += 1L << shift;
                i++;
            }

            if (i == inode.Len) return sum;
            node = inode.Children[i]!;
            shift -= Constants.RRB_BITS;
        }
    }

    private static void CountShape(Node<T> node, int shift, ref int leaves, ref int internalNodes)
    {
        // A Gap's phantoms are shared, and hold none of the list's items.
        if (node.IsPhantom()) return;

        if (shift == 0)
        {
            leaves++;
            return;
        }

        internalNodes++;
        var inode = (InternalNode<T>)node;
        for (var i = 0; i < inode.Len; i++) CountShape(inode.Children[i]!, shift - Constants.RRB_BITS, ref leaves, ref internalNodes);
    }

    private static int MaxSearchDistance(Node<T> node, int shift)
    {
        if (shift == 0 || node.IsPhantom()) return 0;

        var inode = (InternalNode<T>)node;
        var worst = 0;
        if (inode.SizeTable != null)
            for (var i = 1; i < inode.Len; i++)
                worst = Math.Max(worst, i - (inode.SizeTable[i - 1] >> shift));

        for (var i = 0; i < inode.Len; i++)
            worst = Math.Max(worst, MaxSearchDistance(inode.Children[i]!, shift - Constants.RRB_BITS));
        return worst;
    }

    private static void VerifySearchStep(Node<T> node, int shift, string path)
    {
        if (shift == 0 || node.IsPhantom()) return;

        var inode = (InternalNode<T>)node;
        var slots = 0;
        for (var i = 0; i < inode.Len; i++) slots += inode.Children[i]!.Len;

        var allowed = RrbAlgorithm.AllowedChildren(slots);
        if (inode.Len > allowed)
            throw new Exception(
                $"Search step violated at {path} (shift {shift}): {inode.Len} children holding {slots}, at most {allowed} allowed.");

        for (var i = 0; i < inode.Len; i++)
            VerifySearchStep(inode.Children[i]!, shift - Constants.RRB_BITS, $"{path}/{i}");
    }

    // The slots under `node`: its items, and for a phantom, the slots it stands for.
    private int CountNode(Node<T> node, int shift)
    {
        if (node.IsPhantom()) return 1 << (shift + Constants.RRB_BITS);
        if (shift == 0) return node.Len;
        var inode = (InternalNode<T>)node;
        var sum = 0;

        for (var i = 0; i < inode.Len; i++)
        {
            // SAFETY: Check for null before recursion
            var child = inode.Children[i];
            if (child == null) continue;

            sum += CountNode(child, shift - Constants.RRB_BITS);
        }

        return sum;
    }

    private void VerifyNode(Node<T> node, int shift)
    {
        // Phantoms are shared and full by construction.
        if (node.IsPhantom()) return;

        if (shift == 0)
        {
            var leaf = (LeafNode<T>)node;
            // Ensure array is at least as large as Len says
            if (leaf.Items.Length < leaf.Len)
                throw new Exception($"Integrity Error: Leaf Len ({leaf.Len}) > Array Size ({leaf.Items.Length})");
            return;
        }

        var inode = (InternalNode<T>)node;
        var calculatedTotal = 0;

        for (var i = 0; i < inode.Len; i++)
        {
            var child = inode.Children[i];

            if (child == null) break;

            VerifyNode(child, shift - Constants.RRB_BITS);

            var childSize = CountNode(child, shift - Constants.RRB_BITS);
            calculatedTotal += childSize;

            // Verify SizeTable consistency if it exists
            if (inode.SizeTable != null)
            {
                if (inode.SizeTable[i] != calculatedTotal)
                    throw new Exception(
                        $"Integrity Error: SizeTable mismatch at index {i}. Table says {inode.SizeTable[i]}, actual sum is {calculatedTotal}");
            }
            else
            {
                // Indexing does not look for size tables below a balanced node.
                if (child.IsRelaxed())
                    throw new Exception($"Integrity Error: Balanced node has relaxed child at index {i}");

                // Verify Balanced Invariant
                // All children except the last must be full
                var capacity = 1 << shift;

                // Only enforce "Fullness" if it's not the very last logical child
                // and not a null gap.
                if (i < inode.Len - 1)
                    if (childSize != capacity)
                        throw new Exception(
                            $"Integrity Error: Balanced node has non-full child at index {i} (Size {childSize}/{capacity})");
            }
        }
    }
}