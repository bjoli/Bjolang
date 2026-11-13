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


using System.Runtime.CompilerServices;

namespace Bjolang.Runtime.Rrb;

public sealed partial class RrbList<T>
{
    /**
     * <summary>
     *     Applies an accumulator function over a sequence.
     * </summary>
     * <typeparam name="TState">The type of the accumulator value.</typeparam>
     * <param name="seed">The initial accumulator value.</param>
     * <param name="func">An accumulator function to be invoked on each element with the arguments (value, state)</param>
     * <returns>The final accumulator value.</returns>
     */
    public TState Fold<TState>(TState seed, Func<T, TState, TState> func)
    {
        var state = seed;

        // Fold over the head part
        if (HeadLen > 0)
        {
            var items = Head!;
            for (var i = 0; i < HeadLen; i++) state = func(items[i], state);
        }

        // Fold over the tree part
        if (Root != null) state = FoldNode(Root, Shift, state, func);

        // Fold over the tail part
        if (TailLen > 0)
        {
            var items = Tail;
            for (var i = 0; i < TailLen; i++) state = func(items[i], state);
        }

        return state;
    }

    public T Reduce(Func<T, T, T> func)
    {
        if (this.Count == 0)
        {
            throw new InvalidOperationException("Cannot reduce an empty list.");
        }

        // From index 1: the walk skips to it in O(log N).
        var visitor = new ReduceVisitor { Func = func, State = this[0] };
        Visit(ref visitor, 1, Count - 1);
        return visitor.State;
    }

    // Calls `func` with each item and the state so far, as `Reduce` folds.
    [NonUserCode]
    private struct ReduceVisitor : IItemVisitor<T>
    {
        public Func<T, T, T> Func;
        public T State;

        // The state in a local: a field behind `ref` is stored and loaded per item.
        public void Visit(ReadOnlySpan<T> items)
        {
            var func = Func;
            var state = State;
            foreach (var item in items) state = func(item, state);
            State = state;
        }
    }

    // Calls an `Action`, for `ForEach`.
    [NonUserCode]
    private struct ActionVisitor : IItemVisitor<T>
    {
        public Action<T> Action;

        public void Visit(ReadOnlySpan<T> items)
        {
            var action = Action;
            foreach (var item in items) action(item);
        }
    }

    private TState FoldNode<TState>(Node<T> node, int shift, TState state, Func<T, TState, TState> func)
    {
        // Base case: We are at a leaf node
        if (shift == 0)
        {
            var leaf = (LeafNode<T>)node;
            var items = leaf.Items;
            var len = leaf.Len;

            // Iterate directly over the array - extremely fast
            for (var i = 0; i < len; i++) state = func(items[i], state);
            return state;
        }

        // Recursive step: We are at an internal node
        var internalNode = (InternalNode<T>)node;
        var lenInternal = internalNode.Len;

        for (var i = 0; i < lenInternal; i++)
        {
            // Recurse down to children, past the phantoms of a Gap
            var child = internalNode.Children[i]!;
            if (child.IsPhantom()) continue;
            state = FoldNode(child, shift - Constants.RRB_BITS, state, func);
        }

        return state;
    }

    
    /**
     * <summary>
     *     High-performance internal iterator.
     *     Returns false if the iteration was terminated early by the predicate.
     * </summary>
     * <param name="action">A function that returns true to continue, or false to break.</param>
     * <example>
     *     list.Iter(item => {
     *     if (item > 100)
     *     {
     *     Console.WriteLine("Found it!");
     *     return false; // Break
     *     }
     *     return true; // Continue
     *     });
     * </example>
     */
    public bool Iter(Func<T, bool> action)
    {
        // 0. Iterate Head
        if (HeadLen > 0)
        {
            var items = Head!;
            var len = HeadLen;
            for (var i = 0; i < len; i++)
                if (!action(items[i]))
                    return false;
        }

        // 1. Iterate Tree
        if (Root != null)
            // If the tree part returns false, we stop immediately and return false.
            if (!IterNode(Root, Shift, action))
                return false;

        // 2. Iterate Tail
        if (TailLen > 0)
        {
            var items = Tail;
            // Hoist the length check for speed
            var len = TailLen;
            for (var i = 0; i < len; i++)
                if (!action(items[i]))
                    return false;
        }

        return true; // Completed successfully
    }

    private static bool IterNode(Node<T> node, int shift, Func<T, bool> action)
    {
        // Base Case: Leaf Node
        // We iterate the array directly. This is the "hot loop" that beats IEnumerator.
        if (shift == 0)
        {
            var leaf = RrbAlgorithm.AsLeaf(node);
            var items = leaf.Items;
            int len = leaf.Len;

            for (var i = 0; i < len; i++)
                if (!action(items[i]))
                    return false;
            return true;
        }

        // Recursive Step: Internal Node
        var internalNode = RrbAlgorithm.AsInternal(node);
        var children = internalNode.Children;
        int childCount = internalNode.Len;
        var nextShift = shift - Constants.RRB_BITS;

        for (var i = 0; i < childCount; i++)
        {
            // Recurse, past the phantoms of a Gap. If child returns false (break), we propagate it up.
            var child = children[i]!;
            if (child.IsPhantom()) continue;
            if (!IterNode(child, nextShift, action))
                return false;
        }

        return true;
    }

    /**
     * <summary>
     *     Performs the specified action on each element of the list within the range.
     * </summary>
     * <param name="action">The delegate to perform on each element.</param>
     * <param name="index">The zero-based starting index (default 0).</param>
     * <param name="count">The number of elements to process.(default -1 means "until the end").</param>
     */
    public void ForEach(Action<T> action, int index = 0, int count = -1)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));

        // Handle default "to the end"
        if (count == -1) count = Count - index;

        if (count < 0 || index + count > Count) throw new ArgumentOutOfRangeException(nameof(count));
        var visitor = new ActionVisitor { Action = action };
        Visit(ref visitor, index, count);
    }

    /**
     * <summary>
     *     Calls <paramref name="visitor" /> with the <paramref name="count" /> items from
     *     <paramref name="index" />, in order, one span per head, leaf and tail.
     *     - A struct visitor, so that each kind of visit is its own specialised loop, with no
     *       delegate for the visit itself.
     *     - Unchecked: the caller has checked the range.
     * </summary>
     */
    internal void Visit<TV>(ref TV visitor, int index, int count) where TV : struct, IItemVisitor<T>
    {
        if (count == 0) return;

        // Traverse the Head
        if (index < HeadLen)
        {
            var items = Head!;
            var fromHead = Math.Min(count, HeadLen - index);
            visitor.Visit(new ReadOnlySpan<T>(items, index, fromHead));

            count -= fromHead;
            if (count == 0) return;
            index = HeadLen;
        }

        // From here on, index is relative to the start of the tree.
        index -= HeadLen;

        // Traverse the Tree (Root)
        var treeSize = TreeCount;

        // Check if our range overlaps with the tree
        if (index < treeSize && Root != null)
        {
            var takeFromTree = Math.Min(count, treeSize - index);
            VisitNode(Root, Shift, index + Gap, takeFromTree, ref visitor);

            // Adjust remaining count and index for the tail
            count -= takeFromTree;
            index = 0; // We have consumed the offset inside the tree
        }
        else
        {
            // Entire range is in the tail, just adjust the index relative to tail start
            index -= treeSize;
        }

        // Traverse the Tail
        if (count > 0 && TailLen > 0)
        {
            var items = Tail;

            visitor.Visit(new ReadOnlySpan<T>(items, index, count));
        }
    }

    private static void VisitNode<TV>(Node<T> node, int shift, int offset, int count, ref TV visitor)
        where TV : struct, IItemVisitor<T>
    {
        // Base case: Leaf Node
        if (shift == 0)
        {
            var leaf = RrbAlgorithm.AsLeaf(node);
            var items = leaf.Items;
            visitor.Visit(new ReadOnlySpan<T>(items, offset, count));
            return;
        }

        // Internal Node
        var internalNode = RrbAlgorithm.AsInternal(node);
        var childShift = shift - Constants.RRB_BITS;

        // Fast path for Dense nodes
        if (internalNode.SizeTable == null)
        {
            var blockSize = 1 << (childShift + Constants.RRB_BITS);

            for (var i = 0; i < internalNode.Len; i++)
            {
                if (count <= 0) break;

                // If last child, we assume it contains everything else we need. 
                // (Since we validated total bounds at the entry point).
                // Otherwise, it's a full block.
                var currentSize = i == internalNode.Len - 1 ? int.MaxValue : blockSize;

                // Skip child entirely if offset is beyond it
                if (offset >= currentSize)
                {
                    offset -= currentSize;
                }
                else
                {
                    // Overlap: We need to process this child
                    // We take either all that is requested, or all that holds in this child
                    var amount = Math.Min(count, currentSize - offset);

                    VisitNode(internalNode.Children[i]!, childShift, offset, amount, ref visitor);

                    count -= amount;
                    offset = 0; // After the first partial child, subsequent children start at 0
                }
            }
        }
        else
        {
            // Slow path for Relaxed nodes (using SizeTable)
            var prevTotal = 0;
            for (var i = 0; i < internalNode.Len; i++)
            {
                if (count <= 0) break;

                var currentTotal = internalNode.SizeTable[i];
                var currentSize = currentTotal - prevTotal;

                if (offset >= currentSize)
                {
                    offset -= currentSize;
                }
                else
                {
                    var amount = Math.Min(count, currentSize - offset);

                    VisitNode(internalNode.Children[i]!, childShift, offset, amount, ref visitor);

                    count -= amount;
                    offset = 0;
                }

                prevTotal = currentTotal;
            }
        }
    }

    /**
     * <summary>
     * Runs mapper on each value in the RrbList and returns a new list with all the mapped values
     * </summary>
     * <typeparam name="TResult">The type of the value returned by mapper.</typeparam>
     * <param name="mapper">A transform function to apply to each element.</param>
     * <returns>A new RrbList containing the mapped elements.</returns>
     */
    public RrbList<TResult> Map<TResult>(Func<T, TResult> mapper)
    {
        if (mapper == null) throw new ArgumentNullException(nameof(mapper));
        if (Count == 0) return RrbList<TResult>.Empty;

        // 1. Map the Head (first, so that mapper sees the items in order)
        TResult[] newHead = Array.Empty<TResult>();
        if (HeadLen > 0)
        {
            newHead = new TResult[HeadLen];
            for (var i = 0; i < HeadLen; i++)
            {
                newHead[i] = mapper(Head![i]);
            }
        }

        // 2. Map the Tree
        Node<TResult>? newRoot = null;
        if (Root != null)
        {
            newRoot = MapNode(Root, Shift, mapper);
        }

        // 3. Map the Tail
        TResult[] newTail = Array.Empty<TResult>();
        if (TailLen > 0)
        {
            newTail = new TResult[TailLen];
            for (var i = 0; i < TailLen; i++)
            {
                newTail[i] = mapper(Tail[i]);
            }
        }

        // The tree keeps its shape, phantoms and Gap included.
        return new RrbList<TResult>(newRoot, newTail, Count, Shift, TailLen, newHead, HeadLen, Gap);
    }

    /**
     * <summary>
     * A list of the same shape as this one, holding <paramref name="items" /> in order: what Map
     * makes, for a caller that makes the items itself. Head, leaves, phantoms, size tables and
     * Gap are as this list's, so a split's halves made this way still merge into a dense tree.
     * </summary>
     * <param name="items">The new items, as many as this list holds. They are copied.</param>
     */
    public RrbList<TResult> MapShape<TResult>(TResult[] items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (items.Length != Count) throw new ArgumentException("MapShape takes as many items as the list holds");
        if (Count == 0) return RrbList<TResult>.Empty;

        var at = 0;
        [NonUserCode]
        TResult[] Take(int n)
        {
            if (n == 0) return Array.Empty<TResult>();
            var part = new TResult[n];
            Array.Copy(items, at, part, 0, n);
            at += n;
            return part;
        }

        var newHead = Take(HeadLen);
        var newRoot = Root == null ? null : ShapeNode(Root, Shift, Take);
        var newTail = Take(TailLen);
        return new RrbList<TResult>(newRoot, newTail, Count, Shift, TailLen, newHead, HeadLen, Gap);
    }

    // The node with each leaf's items taken in order from `take`.
    private static Node<TResult> ShapeNode<TResult>(Node<T> node, int shift, Func<int, TResult[]> take)
    {
        if (node.IsPhantom()) return Phantom<TResult>.At(shift);

        if (shift == 0)
        {
            var leaf = RrbAlgorithm.AsLeaf(node);
            return new LeafNode<TResult>(take(leaf.Len), leaf.Len, OwnerId.None);
        }

        var internalNode = RrbAlgorithm.AsInternal(node);
        var newChildren = new Node<TResult>?[internalNode.Len];
        for (var i = 0; i < internalNode.Len; i++)
            newChildren[i] = ShapeNode(internalNode.Children[i]!, shift - Constants.RRB_BITS, take);

        return new InternalNode<TResult>(newChildren, internalNode.SizeTable, internalNode.Len, OwnerId.None);
    }

    // This function copies it's way down to the leaf nodes and then runs a copy of the 
    // leaf with mapper run on every element. This is by far the fastest way to run a function on each 
    // element of the RrbList.
    private static Node<TResult> MapNode<TResult>(Node<T> node, int shift, Func<T, TResult> mapper)
    {
        // A Gap's phantoms hold no items to map: they stand for the same slots.
        if (node.IsPhantom()) return Phantom<TResult>.At(shift);

        // Base case: Map values in the leaf
        if (shift == 0)
        {
            var leaf = RrbAlgorithm.AsLeaf(node);
            var newItems = new TResult[leaf.Len];
            
            for (var i = 0; i < leaf.Len; i++)
            {
                newItems[i] = mapper(leaf.Items[i]);
            }
            
            return new LeafNode<TResult>(newItems, leaf.Len, OwnerId.None);
        }

        // Recursive step: Map internal nodes
        var internalNode = RrbAlgorithm.AsInternal(node);
        var newChildren = new Node<TResult>?[internalNode.Len];

        for (var i = 0; i < internalNode.Len; i++)
        {
            newChildren[i] = MapNode(internalNode.Children[i]!, shift - Constants.RRB_BITS, mapper);
        }

        // Because the structural layout is identical, 
        // we can safely pass the exact same SizeTable reference without allocating a new one.
        return new InternalNode<TResult>(newChildren, internalNode.SizeTable, internalNode.Len, OwnerId.None);
    }
    
    
    /**
     * <summary>
     * Filters the elements of the list based on a predicate.
     * </summary>
     * <param name="filter">A function to test each element for a condition.</param>
     * <returns>A new RrbList containing the elements that satisfy the condition.</returns>
     */
    public RrbList<T> Filter(Func<T, bool> filter)
    {
        var builder = new RrbBuilder<T>();
        this.ForEach([NonUserCode] (elem) =>
        {
            if (filter(elem))
            {
                builder.Add(elem);
            }
        });
        return builder.ToImmutable();
    }

    public T? Find(Func<T, bool> predicate)
    {
        T? res = default(T);
        Iter([NonUserCode] (elem) =>
        {
            if (predicate(elem))
            {
                res = elem;
                return false;
            }

            return true;
        });

        return res;
    }
}

/// One step of <see cref="RrbList{T}.Visit{TV}" />: called with each run of items, in order.
internal interface IItemVisitor<T>
{
    void Visit(ReadOnlySpan<T> items);
}
