/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 *
 * Copyright (c) 2025 Linus Björnstam
 */

using System.Runtime.CompilerServices;

namespace Bjolang.Runtime.Rrb;

// The head: up to a leaf's worth of items in front of the tree, the mirror of
// the tail. Prepend fills it, and pushes it into the tree as a leaf when it is
// full. PopFirst empties it, and takes the tree's first leaf as the new head
// when it is empty.
//
// Invariant: a list with no tree has no head (HeadLen == 0 when Root == null).
// Everything a small list holds is in its tail, so the code paths for small
// lists never have to look for a head.
public sealed partial class RrbList<T>
{
    /**
     * <summary>
     *     Returns a new list with the specified item added to the front.
     * </summary>
     * <param name="item">The item to add.</param>
     * <returns>A new list with the item first.</returns>
     */
    public RrbList<T> Prepend(T item)
    {
        if (Root == null)
        {
            // Case 1: No tree, and the tail has room.
            if (TailLen < Constants.RRB_BRANCHING)
            {
                var newTail = new T[TailLen + 1];
                newTail[0] = item;
                Array.Copy(Tail, 0, newTail, 1, TailLen);
                return new RrbList<T>(null, newTail, Count + 1, 0, TailLen + 1);
            }

            // Case 2: No tree, and the tail is full: it becomes the tree.
            var leaf = new LeafNode<T>(ExactArray(Tail, TailLen), TailLen, OwnerId.None);
            return new RrbList<T>(leaf, Array.Empty<T>(), Count + 1, 0, 0, [item], 1, 0);
        }

        // Case 3: The head has room.
        if (HeadLen < Constants.RRB_BRANCHING)
        {
            var newHead = new T[HeadLen + 1];
            newHead[0] = item;
            if (HeadLen > 0) Array.Copy(Head!, 0, newHead, 1, HeadLen);
            return new RrbList<T>(Root, Tail, Count + 1, Shift, TailLen, newHead, HeadLen + 1, Gap);
        }

        // Case 4: The head is full, push it into the tree: into the Gap, when
        // the tree is dense.
        var shift = Shift;
        var gap = Gap;
        var root = PushInFront(Root, TreeCount, ref gap, Head!, HeadLen, ref shift);
        return new RrbList<T>(root, Tail, Count + 1, shift, TailLen, [item], 1, gap);
    }

    /**
     * <summary>
     *     Returns a new list with the specified items added to the front.
     * </summary>
     * <param name="items">The items to add.</param>
     * <returns>A new list with the items first, in the order given.</returns>
     */
    public RrbList<T> PrependRange(IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        var other = items as RrbList<T> ?? Create(items);
        return other.Merge(this);
    }

    /**
     * <summary>
     *     Removes the first element from the list.
     * </summary>
     * <returns>A new list with the first element removed.</returns>
     */
    public RrbList<T> PopFirst()
    {
        // Fast Path: Just shrink the head. Kept small enough to inline. Count
        // is at least 2 here, as a list with a head also has a tree.
        var headLen = HeadLen;
        if (headLen > 1)
        {
            var newHead = new T[headLen - 1];
            Array.Copy(Head!, 1, newHead, 0, headLen - 1);
            return new RrbList<T>(Root, Tail, Count - 1, Shift, TailLen, newHead, headLen - 1, Gap);
        }

        if (Count == 0) throw new InvalidOperationException("List is empty");
        return DropFrontSlow(1);
    }

    /// <summary>
    ///     The list without its first <paramref name="n" /> items, for
    ///     0 &lt; n &lt; Count: what PopFirst and Deconstruct leave as the rest.
    /// </summary>
    /// <remarks>
    ///     When the items are all in the head, only the head is copied. When
    ///     they use it up, the tree's first leaf becomes the next head at once,
    ///     so the next match is on the fast path too.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal RrbList<T> DropFront(int n)
    {
        var headLen = HeadLen;
        if (n < headLen)
        {
            var newLen = headLen - n;
            var newHead = new T[newLen];
            Array.Copy(Head!, n, newHead, 0, newLen);
            return new RrbList<T>(Root, Tail, Count - n, Shift, TailLen, newHead, newLen, Gap);
        }

        return DropFrontSlow(n);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private RrbList<T> DropFrontSlow(int n)
    {
        if (n >= Count) return Empty;

        // No tree: everything is in the tail.
        if (Root == null)
        {
            var restCount = Count - n;
            var newTail = new T[restCount];
            Array.Copy(Tail, n, newTail, 0, restCount);
            return new RrbList<T>(null, newTail, restCount, 0, restCount);
        }

        // The head goes, and n - HeadLen more from the tree.
        var rest = HeadLen == 0 ? this : Headless();
        var fromTree = n - HeadLen;

        if (fromTree < rest.TreeCount)
        {
            // A dense tree widens its Gap, and what is left of the leaf the
            // drop ends in becomes the head.
            if (rest.Root!.IsDense()) return rest.DropTreeFront(fromTree);

            // A relaxed one gives up its first leaf, when the drop ends in it.
            if (fromTree < RrbAlgorithm.FirstLeafLen(rest.Root, rest.Shift))
                return rest.PromoteFirstLeaf(fromTree);
        }

        var kept = rest.Count - fromTree;

        // Two leaves' worth or less, or all in the tail: copied.
        if (kept <= Constants.RRB_BRANCHING * 2 || fromTree >= rest.TreeCount)
            return rest.Copied(fromTree, kept);

        // The drop ends right after the first leaf, or further in: SliceLeftRec.
        if (fromTree == RrbAlgorithm.FirstLeafLen(rest.Root!, rest.Shift)) return rest.PromoteFirstLeaf(fromTree);
        var shift = rest.Shift;
        var root = RrbAlgorithm.LowerRoot(RrbAlgorithm.SliceLeftRec(rest.Root!, fromTree, shift), ref shift,
            kept - rest.TailLen);
        return new RrbList<T>(root, rest.Tail, kept, shift, rest.TailLen);
    }

    /// <summary>
    ///     The item at <paramref name="index" />, looked for in the head first:
    ///     the items a match takes from the front are usually there.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T FrontItem(int index) => index < HeadLen ? Head![index] : this[index];

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    // `count` items from `start`, copied into a new list of no more than two
    // leaves: a tail, or a full leaf and a tail. Requires count <= 64.
    internal RrbList<T> Copied(int start, int count)
    {
        var items = new T[count];
        CopyTo(start, items, 0, count);
        if (count <= Constants.RRB_BRANCHING) return new RrbList<T>(null, items, count, 0, count);
        var leaf = new LeafNode<T>(items[..Constants.RRB_BRANCHING], Constants.RRB_BRANCHING, OwnerId.None);
        return new RrbList<T>(leaf, items[Constants.RRB_BRANCHING..], count, 0, count - Constants.RRB_BRANCHING);
    }

    /// <summary>
    ///     `count` items from `start`, for the C# list patterns vec patterns compile to (`[a, .. rest]`):
    ///     a take, then a drop. Bjolang's `vec-slice` is in lib/std/vec.protobjo.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">When the slice is outside the list.</exception>
    public RrbList<T> Slice(int start, int count)
    {
        if (start < 0 || count < 0 || count > Count - start) throw new ArgumentOutOfRangeException();
        if (count == 0) return Empty;
        var front = TakeFront(start + count);
        return start == 0 ? front : front.DropFront(start);
    }

    // The first `count` items, 0 < count <= Count.
    // - Two leaves' worth or less is copied.
    // - The head is kept, and the rest taken.
    // - The tree is cut on the right by SliceRightAndPromote. Its last leaf
    //   becomes the tail when it is not full. A dense tree keeps its Gap.
    internal RrbList<T> TakeFront(int count)
    {
        if (count == Count) return this;
        if (count <= Constants.RRB_BRANCHING * 2) return Copied(0, count);
        if (HeadLen > 0) return WithHead(Head, HeadLen, Headless().TakeFront(count - HeadLen));

        var treeSize = Count - TailLen;
        if (count > treeSize)
        {
            var kept = count - treeSize;
            return new RrbList<T>(Root, Tail[..kept], count, Shift, kept, null, 0, Gap);
        }

        var root = RrbAlgorithm.SliceRightAndPromote(Root!, Gap + count, Shift, out var tail, out var tailLen);
        var shift = Shift;
        if (Gap > 0)
        {
            var gap = Gap;
            root = NormalizeGap(root, ref shift, ref gap, count - tailLen);
            return new RrbList<T>(root, tail, count, shift, tailLen, null, 0, gap);
        }

        root = RrbAlgorithm.LowerRoot(root, ref shift, count - tailLen);
        return new RrbList<T>(root, tail, count, shift, tailLen);
    }

    // This list without its head: the tree and the tail, indexed from 0.
    // Operations that do not touch the head work on this and put the head
    // back with WithHead.
    internal RrbList<T> Headless()
    {
        if (HeadLen == 0) return this;
        return new RrbList<T>(Root, Tail, Count - HeadLen, Shift, TailLen, null, 0, Gap);
    }

    // `rest`, which has no head, with `headLen` items of `head` in front of it.
    // Keeps the invariant: when `rest` has no tree, the head joins its tail
    // if they fit in one leaf, and otherwise becomes the tree.
    internal static RrbList<T> WithHead(T[]? head, int headLen, RrbList<T> rest)
    {
        if (headLen == 0) return rest;
        var count = rest.Count + headLen;

        if (rest.Root != null)
            return new RrbList<T>(rest.Root, rest.Tail, count, rest.Shift, rest.TailLen, head, headLen, rest.Gap);

        if (headLen + rest.TailLen <= Constants.RRB_BRANCHING)
        {
            var newTail = new T[count];
            Array.Copy(head!, 0, newTail, 0, headLen);
            Array.Copy(rest.Tail, 0, newTail, headLen, rest.TailLen);
            return new RrbList<T>(null, newTail, count, 0, count);
        }

        // Too many for one leaf: a full leaf for the tree, and the rest in the
        // tail. A part-full leaf for root would have the next leaf pushed
        // after it, and the tree would need size tables from then on.
        // A full head is that leaf as it is, and the tail stays as it is.
        if (headLen == Constants.RRB_BRANCHING)
        {
            var whole = new LeafNode<T>(ExactArray(head!, headLen), headLen, OwnerId.None);
            return new RrbList<T>(whole, rest.Tail, count, 0, rest.TailLen);
        }

        // Otherwise the leaf is the head and the start of the tail.
        var fromTail = Constants.RRB_BRANCHING - headLen;
        var rootItems = new T[Constants.RRB_BRANCHING];
        Array.Copy(head!, 0, rootItems, 0, headLen);
        Array.Copy(rest.Tail, 0, rootItems, headLen, fromTail);

        var tailLen = rest.TailLen - fromTail;
        var tail = new T[tailLen];
        Array.Copy(rest.Tail, fromTail, tail, 0, tailLen);

        var leaf = new LeafNode<T>(rootItems, Constants.RRB_BRANCHING, OwnerId.None);
        return new RrbList<T>(leaf, tail, count, 0, tailLen);
    }

    // This list, with no head and a dense tree, without its first `k` items,
    // for 0 <= k < TreeCount. Every leaf up to the one the drop ends in becomes
    // a phantom, the Gap grows to the end of that leaf, and what is left of it
    // becomes the head. The tree stays dense, where taking leaves off a dense
    // left spine without a Gap would give it size tables.
    internal RrbList<T> DropTreeFront(int k)
    {
        var pos = Gap + k;

        // The root's children left of the path are left out, not made phantoms.
        var skip = TopDigit(pos);
        var root = RrbAlgorithm.PhantomUpTo(Root!, Shift, pos, out var leaf, skip);

        var start = pos & Constants.RRB_MASK;
        var headLen = leaf.Len - start;
        var treeItems = TreeCount - k - headLen;
        var gap = (pos & ~Constants.RRB_MASK) + Constants.RRB_BRANCHING - (skip << Shift);
        var shift = Shift;
        root = NormalizeGap(root, ref shift, ref gap, treeItems);

        T[] head;
        if (start == 0)
        {
            head = ExactArray(leaf.Items, leaf.Len);
        }
        else
        {
            head = new T[headLen];
            Array.Copy(leaf.Items, start, head, 0, headLen);
        }

        if (root != null)
            return new RrbList<T>(root, Tail, headLen + treeItems + TailLen, shift, TailLen, head, headLen, gap);

        return WithHead(head, headLen, new RrbList<T>(null, Tail, TailLen, 0, TailLen));
    }

    // This list, with no head and a dense tree, split at `index`, which is in
    // the tree, in one pass (SplitDense). The leaf the split is in gives the
    // left half its tail and the right half its head. The left half keeps the
    // Gap, and the right half starts after a Gap that ends with that leaf.
    // Both stay dense.
    internal (RrbList<T> Left, RrbList<T> Right) SplitDenseTree(int index)
    {
        var pos = Gap + index;
        var shift = Shift;
        var skip = TopDigit(pos);
        Node<T>? leftRoot;
        Node<T>? rightRoot;
        LeafNode<T> leaf;
        if (shift == 0)
        {
            // A lone leaf for a tree: it is all split between the two.
            leaf = RrbAlgorithm.AsLeaf(Root!);
            leftRoot = null;
            rightRoot = null;
        }
        else
        {
            RrbAlgorithm.SplitDense(Root!, shift, pos, out leftRoot, out var right, out leaf, skip);
            rightRoot = right;
        }

        var cut = pos & Constants.RRB_MASK;

        // Left: the tree before the leaf, and the leaf up to the split as tail.
        var leftTail = cut == 0 ? Array.Empty<T>() : ExactArray(leaf.Items, cut);
        var leftTreeItems = index - cut;
        var leftShift = shift;
        var leftGap = Gap;
        leftRoot = NormalizeGap(leftRoot, ref leftShift, ref leftGap, leftTreeItems);
        var leftList = leftRoot == null
            ? new RrbList<T>(null, leftTail, cut, 0, cut)
            : new RrbList<T>(leftRoot, leftTail, index, leftShift, cut, null, 0, leftGap);

        // Right: the rest of the leaf as head, and the tree after it.
        var headLen = leaf.Len - cut;
        T[] head;
        if (cut == 0)
        {
            head = ExactArray(leaf.Items, leaf.Len);
        }
        else
        {
            head = new T[headLen];
            Array.Copy(leaf.Items, cut, head, 0, headLen);
        }

        var rightTreeItems = TreeCount - index - headLen;
        var rightShift = shift;
        var rightGap = (pos & ~Constants.RRB_MASK) + Constants.RRB_BRANCHING - (skip << Shift);
        rightRoot = NormalizeGap(rightRoot, ref rightShift, ref rightGap, rightTreeItems);
        var rightCount = Count - index;
        var rightList = rightRoot == null
            ? WithHead(head, headLen, new RrbList<T>(null, Tail, TailLen, 0, TailLen))
            : new RrbList<T>(rightRoot, Tail, rightCount, rightShift, TailLen, head, headLen, rightGap);

        return (leftList, rightList);
    }

    // This list, with no head and a dense tree, sliced to `count` items from
    // `start`, all in the tree, for a count longer than two leaves. One pass
    // (CutDense) makes phantoms of every leaf up to the one `start` is in and
    // cuts off every leaf from the one the slice ends in. Those two leaves give
    // the head and the tail, and the tree between them stays dense, after a Gap.
    internal RrbList<T> SliceDenseTree(int start, int count)
    {
        var first = Gap + start;
        var last = first + count - 1;
        var skip = TopDigit(first);
        var root = RrbAlgorithm.CutDense(Root!, Shift, first, last, out var firstLeaf, out var lastLeaf, skip);

        var headStart = first & Constants.RRB_MASK;
        var headLen = firstLeaf.Len - headStart;
        var tailLen = (last & Constants.RRB_MASK) + 1;
        var treeItems = count - headLen - tailLen;

        var gap = (first & ~Constants.RRB_MASK) + Constants.RRB_BRANCHING - (skip << Shift);
        var shift = Shift;
        root = NormalizeGap(root, ref shift, ref gap, treeItems);

        T[] head;
        if (headStart == 0)
        {
            head = ExactArray(firstLeaf.Items, firstLeaf.Len);
        }
        else
        {
            head = new T[headLen];
            Array.Copy(firstLeaf.Items, headStart, head, 0, headLen);
        }

        var tail = ExactArray(lastLeaf.Items, tailLen);
        return new RrbList<T>(root, tail, count, shift, tailLen, head, headLen, gap);
    }

    // How many of the root's first children a cut from the left at tree position
    // `pos` can leave out: those before the child that holds `pos`, and when the
    // root is above the leaves, that child too, as the leaf goes to the head.
    // 0 when the root is a leaf.
    private int TopDigit(int pos) => Shift switch
    {
        0 => 0,
        Constants.RRB_BITS => ((pos >> Shift) & Constants.RRB_MASK) + 1,
        _ => (pos >> Shift) & Constants.RRB_MASK
    };

    // The same list with Gap 0: the phantoms cut away and the left spine given
    // size tables. What an operation that does not keep a Gap starts from.
    internal RrbList<T> Materialized()
    {
        if (Gap == 0) return this;
        var shift = Shift;
        var gap = Gap;
        var root = MaterializeTree(Root!, ref shift, ref gap, TreeCount);
        return new RrbList<T>(root, Tail, Count, shift, TailLen, Head, HeadLen, 0);
    }

    // `root` with its Gap cut away: SliceLeftRec gives the left spine size
    // tables, and LowerRoot takes off the height it no longer needs.
    private static Node<T>? MaterializeTree(Node<T> root, ref int shift, ref int gap, int treeItems)
    {
        var cut = RrbAlgorithm.SliceLeftRec(root, gap, shift);
        gap = 0;
        return RrbAlgorithm.LowerRoot(cut, ref shift, treeItems);
    }

    // A tree with a Gap, holding `treeItems` of the list's items, as the Gap
    // rules have it: null when it holds none; the root's children that are all
    // Gap cut off, and a root with one child given up for the child, so that
    // the tree is no taller than its Gap and items need; and with no Gap at
    // all when the tree is not dense, or would still be more than a level
    // taller than its items need.
    internal static Node<T>? NormalizeGap(Node<T>? root, ref int shift, ref int gap, int treeItems)
    {
        if (root == null || treeItems == 0)
        {
            shift = 0;
            gap = 0;
            return null;
        }

        // A relaxed tree has no Gap. Without one, the loop below only gives up
        // a root with one child, which is all a dense tree can be too tall by.
        if (root.IsRelaxed()) return gap == 0 ? root : MaterializeTree(root, ref shift, ref gap, treeItems);

        while (shift > 0)
        {
            var node = RrbAlgorithm.AsInternal(root);

            // Children that are all Gap.
            var cut = gap >> shift;
            if (cut > 0)
            {
                var len = node.Len - cut;
                var children = new Node<T>?[len];
                Array.Copy(node.Children, cut, children, 0, len);
                node = new InternalNode<T>(children, null, len, OwnerId.None);
                root = node;
                gap -= cut << shift;
            }

            if (node.Len != 1) break;

            root = node.Children[0]!;
            shift -= Constants.RRB_BITS;
        }

        var least = 0;
        for (var capacity = (long)Constants.RRB_BRANCHING; capacity < treeItems; capacity <<= Constants.RRB_BITS)
            least++;
        if (gap > 0 && shift / Constants.RRB_BITS > least + 1)
            return MaterializeTree(root, ref shift, ref gap, treeItems);

        return root;
    }

    // This list without its first `drop` items, which are all in the tree's
    // first leaf: what is left of that leaf becomes the head. Requires an
    // empty head, a relaxed tree (so no Gap), and `drop` at most the first
    // leaf's length.
    internal RrbList<T> PromoteFirstLeaf(int drop)
    {
        var shift = Shift;
        var root = RrbAlgorithm.RemoveFirstLeaf(Root!, Shift, out var leaf);
        var treeItems = TreeCount - leaf.Len;
        root = RrbAlgorithm.LowerRoot(root, ref shift, treeItems);

        var rest = root == null
            ? new RrbList<T>(null, Tail, TailLen, 0, TailLen)
            : new RrbList<T>(root, Tail, Count - leaf.Len, shift, TailLen);

        var headLen = leaf.Len - drop;
        if (headLen == 0) return rest;

        T[] head;
        if (drop == 0)
        {
            head = leaf.Items;
        }
        else
        {
            head = new T[headLen];
            Array.Copy(leaf.Items, drop, head, 0, headLen);
        }

        return WithHead(head, headLen, rest);
    }

    // `root`, holding `treeSize` of the list's items after a Gap of `gap`,
    // with the first `len` items of `items` in front of them as a leaf.
    //
    // A dense tree takes a full leaf into its Gap, and when there is none, a
    // root above with the old root as its last child gives it one: the tree
    // stays dense. A lone leaf, or a leaf parent with room, takes the leaf in
    // front without a Gap, as both stay dense that way too. Otherwise a full
    // leaf goes down the left spine (TryPrependLeaf), and a part full one, or
    // one the left spine has no room for, goes through Concat, which repacks
    // what it has to.
    internal static Node<T> PushInFront(Node<T> root, int treeSize, ref int gap, T[] items, int len, ref int shift)
    {
        var leaf = new LeafNode<T>(ExactArray(items, len), len, OwnerId.None);

        if (len == Constants.RRB_BRANCHING && root.IsDense())
        {
            if (gap >= Constants.RRB_BRANCHING)
            {
                gap -= Constants.RRB_BRANCHING;
                return RrbAlgorithm.PlaceLeaf(root, shift, gap, leaf);
            }

            if (shift == 0 || (shift == Constants.RRB_BITS && root.Len < Constants.RRB_BRANCHING))
            {
                var denseShift = shift;
                var dense = RrbAlgorithm.TryPrependLeaf(root, leaf, ref denseShift);
                if (dense != null)
                {
                    shift = denseShift;
                    return dense;
                }
            }

            var grownShift = shift;
            var grown = RrbAlgorithm.GrowForGap(root, ref grownShift, out var grownGap);
            if (grown != null)
            {
                shift = grownShift;
                gap = grownGap - Constants.RRB_BRANCHING;
                return RrbAlgorithm.PlaceLeaf(grown, shift, gap, leaf);
            }
        }

        if (gap > 0) root = MaterializeTree(root, ref shift, ref gap, treeSize)!;

        if (len == Constants.RRB_BRANCHING)
        {
            var newShift = shift;
            var pushed = RrbAlgorithm.TryPrependLeaf(root, leaf, ref newShift);
            if (pushed != null)
            {
                shift = newShift;
                return pushed;
            }
        }

        var (node, _) = RrbAlgorithm.Concat<T>(leaf, len, root, treeSize, 0, shift, out var concatShift);
        shift = concatShift;
        return node;
    }

    // The first `len` items of `items`, in an array of exactly that length.
    private static T[] ExactArray(T[] items, int len)
    {
        if (items.Length == len) return items;
        var exact = new T[len];
        Array.Copy(items, exact, len);
        return exact;
    }
}
