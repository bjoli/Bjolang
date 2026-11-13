/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 *
 * Copyright (c) 2025 Linus Björnstam
 */

namespace Bjolang.Runtime.Rrb;

// A merge of two lists whose leaves line up, as the two halves of a split
// do, and as they still do after a Map, which keeps a tree's shape: the left
// list's tree ends on a leaf, and its tail and the right list's head make
// one leaf or none. The merge then makes a dense tree without moving an
// item. Each node of either tree that lands on a block of its own size in
// the joined tree is kept as it is, and the nodes above the leaves that do
// not are made again from their children. Concat would give the nodes at the
// seam size tables instead.
//
// For the halves of one split, only the nodes the seam runs through are new.
// A split gives each half's root no more height than it needs and cuts off
// the Gap it has no use for, so the positions of a half, and of what is
// merged from such halves, can be moved by whole leaves against the other's.
// Then the moved tree's nodes above the leaves are made again: at most its
// leaves over 32 of them, and the smaller tree is the one moved.
public sealed partial class RrbList<T>
{
    /// <summary>
    ///     This list and <paramref name="other" /> as one list with a dense
    ///     tree, or null when their positions do not line up.
    /// </summary>
    private RrbList<T>? TryAlignedMerge(RrbList<T> other)
    {
        var right = other.Root;
        if (right == null || !right.IsDense()) return null;
        var left = Root;
        if (left != null && !left.IsDense()) return null;

        // Little on the left, as when a split falls in the head or just after
        // it: no tree, or a tree of one leaf and no head. Those items and the
        // right list's head are at most three leaves, and are laid out again
        // in front of the right tree: what is over whole leaves is the head,
        // and each full leaf goes in front of the tree, which keeps it dense
        // (PushInFront).
        if (left == null || (Shift == 0 && HeadLen == 0))
        {
            var leafLen = left == null ? 0 : left.Len;
            var front = leafLen + TailLen + other.HeadLen;
            var items = new T[front];
            if (leafLen > 0) Array.Copy(RrbAlgorithm.AsLeaf(left!).Items, 0, items, 0, leafLen);
            Array.Copy(Tail, 0, items, leafLen, TailLen);
            if (other.HeadLen > 0) Array.Copy(other.Head!, 0, items, leafLen + TailLen, other.HeadLen);

            var headLen = front % Constants.RRB_BRANCHING;
            var pushed = right;
            var shiftAfter = other.Shift;
            var gapAfter = other.Gap;
            for (var at = front - Constants.RRB_BRANCHING; at >= headLen; at -= Constants.RRB_BRANCHING)
            {
                var leaf = new T[Constants.RRB_BRANCHING];
                Array.Copy(items, at, leaf, 0, Constants.RRB_BRANCHING);
                var treeSize = other.TreeCount + (front - Constants.RRB_BRANCHING - at);
                pushed = PushInFront(pushed, treeSize, ref gapAfter, leaf, Constants.RRB_BRANCHING, ref shiftAfter);
            }

            T[]? head = null;
            if (headLen > 0)
            {
                head = new T[headLen];
                Array.Copy(items, 0, head, 0, headLen);
            }

            return new RrbList<T>(pushed, other.Tail, Count + other.Count, shiftAfter, other.TailLen,
                head, headLen, gapAfter);
        }

        // Where the left tree ends, in its positions: on a leaf.
        long p = left == null ? 0 : Gap + TreeCount;
        if ((p & Constants.RRB_MASK) != 0) return null;

        // Little on the right, as when a split falls in the last leaf: a tree
        // of one leaf and no head. Its items and the left tail are at most
        // three leaves, and go after the left tree as full leaves, the rest
        // as the tail.
        if (other.Shift == 0 && other.HeadLen == 0)
        {
            var rightLeaf = RrbAlgorithm.AsLeaf(right);
            var back = TailLen + rightLeaf.Len + other.TailLen;
            var items = new T[back];
            Array.Copy(Tail, 0, items, 0, TailLen);
            Array.Copy(rightLeaf.Items, 0, items, TailLen, rightLeaf.Len);
            Array.Copy(other.Tail, 0, items, TailLen + rightLeaf.Len, other.TailLen);

            var appended = left;
            var shiftAfter = Shift;
            var at = 0;
            for (; back - at > Constants.RRB_BRANCHING; at += Constants.RRB_BRANCHING)
            {
                var leaf = new T[Constants.RRB_BRANCHING];
                Array.Copy(items, at, leaf, 0, Constants.RRB_BRANCHING);
                appended = RrbAlgorithm.AppendLeafToTree(appended, new LeafNode<T>(leaf, Constants.RRB_BRANCHING, OwnerId.None),
                    ref shiftAfter, OwnerId.None);
            }

            var tailLen = back - at;
            var tail = new T[tailLen];
            Array.Copy(items, at, tail, 0, tailLen);

            var gapAfter = Gap;
            var appendedItems = TreeCount + at;
            if (gapAfter > 0) appended = NormalizeGap(appended, ref shiftAfter, ref gapAfter, appendedItems);
            return new RrbList<T>(appended, tail, Count + other.Count, shiftAfter, tailLen, Head, HeadLen, gapAfter);
        }

        var seam = TailLen + other.HeadLen;
        if (seam != 0 && seam != Constants.RRB_BRANCHING) return null;

        // Where the right tree starts, given where the left one is: `d` puts
        // its first item after the seam. The leaves have to line up; a node
        // above them that does not land on a block of its own size is made
        // again from its children, down to nodes that do. One of the trees
        // stays where it is, and the other is moved: the smaller one, as the
        // nodes made again are at most its leaves over 32. The left tree is
        // moved far enough that the right root lands whole, and gets phantoms
        // in front of it.
        var d = p + seam - other.Gap;
        if (d % Constants.RRB_BRANCHING != 0) return null;
        long leftMove, rightMove;
        if (d >= 0 && other.TreeCount <= TreeCount)
        {
            leftMove = 0;
            rightMove = d;
        }
        else
        {
            var rightSpan = 32L << other.Shift;
            leftMove = Math.Max(0, -d);
            var over = (d + leftMove) % rightSpan;
            if (over != 0) leftMove += rightSpan - over;
            rightMove = d + leftMove;
        }

        var seamAt = p + leftMove;
        var end = seamAt + seam + other.TreeCount;
        var shift = Math.Max(Constants.RRB_BITS, Math.Max(Shift, other.Shift));
        while ((32L << shift) < end) shift += Constants.RRB_BITS;
        if (shift > 25) return null;

        LeafNode<T>? seamLeaf = null;
        if (seam > 0)
        {
            var items = new T[Constants.RRB_BRANCHING];
            Array.Copy(Tail, 0, items, 0, TailLen);
            if (other.HeadLen > 0) Array.Copy(other.Head!, 0, items, TailLen, other.HeadLen);
            seamLeaf = new LeafNode<T>(items, Constants.RRB_BRANCHING, OwnerId.None);
        }

        var leftGap = left == null ? 0 : Gap;
        var join = new AlignedJoin(
            new Placed(left, Shift, leftMove), leftMove + leftGap,
            new Placed(right, other.Shift, rightMove),
            seamAt, seam, end, seamLeaf);
        Node<T> root = join.Build(shift, 0);

        var gap = (int)(leftMove + leftGap);
        var treeItems = (left == null ? 0 : TreeCount) + seam + other.TreeCount;
        var normalized = NormalizeGap(root, ref shift, ref gap, treeItems)!;

        return new RrbList<T>(normalized, other.Tail, Count + other.Count, shift, other.TailLen,
            left == null ? null : Head, left == null ? 0 : HeadLen, gap);
    }

    // A dense tree, its height, and how far its positions are moved in the
    // joined tree.
    [NonUserCode]
    private readonly record struct Placed(Node<T>? Root, int Shift, long Move)
    {
        // Whether the joined tree may hold this tree's nodes at `shift` as they
        // are: they land on blocks of their size when the move is a multiple
        // of it. A leaf always does, as the leaves line up.
        public bool HasWhole(int shift) => shift <= Shift && Move % (32L << shift) == 0;

        // The node at `shift` that covers joined position `at`. Null when the
        // tree has no node there.
        public Node<T>? NodeAt(long at, int shift)
        {
            var pos = at - Move;
            if (Root == null || pos < 0 || pos >= 32L << Shift) return null;
            var node = Root;
            for (var s = Shift; s > shift; s -= Constants.RRB_BITS)
            {
                var inode = RrbAlgorithm.AsInternal(node);
                var index = (int)((pos >> s) & Constants.RRB_MASK);
                if (index >= inode.Len) return null;
                node = inode.Children[index]!;
            }

            return node;
        }
    }

    // The two trees and where they meet, in the joined tree's positions.
    // Positions before `gapEnd` are the joined tree's Gap.
    [NonUserCode]
    private readonly struct AlignedJoin(
        Placed left, long gapEnd, Placed right,
        long seamAt, int seam, long end, LeafNode<T>? seamLeaf)
    {
        // The joined node at `shift` that covers the positions from `start`:
        // 32 << shift of them, 1 << shift to a child.
        public Node<T> Build(int shift, long start)
        {
            var childShift = shift - Constants.RRB_BITS;
            var span = 1L << shift;
            var children = new Node<T>?[Constants.RRB_BRANCHING];
            var len = 0;

            for (var i = 0; i < Constants.RRB_BRANCHING; i++)
            {
                var at = start + i * span;
                if (at >= end) break;

                Node<T>? child;
                if (at + span <= gapEnd)
                    child = Phantom<T>.At(childShift);
                else if (at + span <= seamAt && left.HasWhole(childShift))
                    child = left.NodeAt(at, childShift);
                else if (at >= seamAt + seam && right.HasWhole(childShift))
                    child = right.NodeAt(at, childShift);
                else if (childShift == 0 && at == seamAt)
                    child = seamLeaf;
                else if (childShift == 0)
                    child = null;
                else
                    child = Build(childShift, at);

                if (child == null) throw new InvalidOperationException("Aligned merge: a block has no node");
                children[len++] = child;
            }

            if (len < Constants.RRB_BRANCHING) Array.Resize(ref children, len);
            return new InternalNode<T>(children, null, len, OwnerId.None);
        }
    }
}
