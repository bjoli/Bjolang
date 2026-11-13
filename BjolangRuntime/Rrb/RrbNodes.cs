/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Runtime.CompilerServices;

namespace Bjolang.Runtime.Rrb;

/// <summary>
///     What Bjolang's vec algorithms (lib/std/vec.protobjo) read and build an
///     <see cref="RrbList{T}" /> with.
///     - A missing tree or child is <see cref="NoTree{T}" />, never null.
///     - Every node made here is frozen: no transient owns it.
///     - The helpers that the runtime's own vec code also uses stay here.
/// </summary>
[NonUserCode]
public static class RrbNodes
{
    [NonUserCode]
    private static class None<T>
    {
        public static readonly Node<T> Value = MakeValue();

        private static Node<T> MakeValue()
        {
            var leaf = new LeafNode<T>([], 0, OwnerId.None);
            leaf.Flags |= NodeFlags.IsFrozen;
            return leaf;
        }
    }

    // ---- The list ---------------------------------------------------------

    /// <summary>The stand-in for no tree, or no node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Node<T> NoTree<T>() => None<T>.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNoTree<T>(Node<T> n) => ReferenceEquals(n, None<T>.Value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Node<T>? OrNull<T>(Node<T> n) => ReferenceEquals(n, None<T>.Value) ? null : n;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Node<T> OrNone<T>(Node<T>? n) => n ?? None<T>.Value;

    public static Node<T> Root<T>(RrbList<T> l) => OrNone(l.Root);
    public static int Shift<T>(RrbList<T> l) => l.Shift;
    public static int Gap<T>(RrbList<T> l) => l.Gap;
    public static int HeadLen<T>(RrbList<T> l) => l.HeadLen;
    public static int TailLen<T>(RrbList<T> l) => l.TailLen;

    /// <summary>The head's items in [0, HeadLen). An empty array when there is no head.</summary>
    public static T[] Head<T>(RrbList<T> l) => l.HeadLen == 0 ? [] : l.Head!;

    /// <summary>The tail's items in [0, TailLen).</summary>
    public static T[] Tail<T>(RrbList<T> l) => l.Tail;

    /// <summary>
    ///     A list of its parts, as <see cref="RrbList{T}" />'s fields have them. A tree of
    ///     <see cref="NoTree{T}" /> is none.
    /// </summary>
    public static RrbList<T> List<T>(Node<T> root, int shift, int gap, T[] head, int headLen, T[] tail, int tailLen,
        int count) =>
        new(OrNull(root), tail, count, shift, tailLen, head, headLen, gap);

    /// <summary>A list of a tree and a tail, with no head. A tree of <see cref="NoTree{T}" /> is none.</summary>
    public static RrbList<T> Tree<T>(Node<T> root, int shift, int gap, T[] tail, int tailLen, int count) =>
        new(OrNull(root), tail, count, shift, tailLen, null, 0, gap);

    /// <summary>`count` items from `start` in a new list of at most two leaves. Requires count &lt;= 64.</summary>
    public static RrbList<T> Copied<T>(RrbList<T> l, int start, int count) => l.Copied(start, count);

    /// <summary>The first `count` items, 0 &lt; count &lt;= length. See <see cref="RrbList{T}.TakeFront" />.</summary>
    public static RrbList<T> TakeFront<T>(RrbList<T> l, int count) => l.TakeFront(count);

    /// <summary>
    ///     The first `limit` items of the node at `shift`. Returns the node, or <see cref="NoTree{T}" />, and
    ///     its last leaf's items when that leaf is not full, with their count; then that leaf is not in the node.
    /// </summary>
    public static (Node<T> Node, T[] Tail, int TailLen) SliceRight<T>(Node<T> n, int limit, int shift)
    {
        var r = RrbAlgorithm.SliceRightAndPromote(n, limit, shift, out var tail, out var tailLen);
        return (OrNone(r), tail, tailLen);
    }

    /// <summary>The list without its head, indexed from 0.</summary>
    public static RrbList<T> Headless<T>(RrbList<T> l) => l.Headless();

    /// <summary>
    ///     A list with no head and a dense tree without its first `k` items, 0 &lt;= k &lt; tree count.
    ///     See <see cref="RrbList{T}.DropTreeFront" />.
    /// </summary>
    public static RrbList<T> DropTreeFront<T>(RrbList<T> l, int k) => l.DropTreeFront(k);

    /// <summary>
    ///     A list with no head and a relaxed tree without its first `drop` items, all in the first leaf.
    /// </summary>
    public static RrbList<T> PromoteFirstLeaf<T>(RrbList<T> l, int drop) => l.PromoteFirstLeaf(drop);

    /// <summary>
    ///     A slice of a list with no head and a dense tree, all in the tree and longer than two leaves.
    ///     The result stays dense.
    /// </summary>
    public static RrbList<T> SliceDense<T>(RrbList<T> l, int start, int count) => l.SliceDenseTree(start, count);

    /// <summary>A list with no head and a dense tree, split at `index` in the tree. Both halves stay dense.</summary>
    public static (RrbList<T> Left, RrbList<T> Right) SplitDense<T>(RrbList<T> l, int index) => l.SplitDenseTree(index);

    /// <summary>The same list with a gap of 0. See <see cref="RrbList{T}.Materialized" />.</summary>
    public static RrbList<T> Materialized<T>(RrbList<T> l) => l.Materialized();

    /// <summary>
    ///     `rest` with `head` in front of it. A list with no tree keeps everything in its tail, so this
    ///     may move the head there.
    /// </summary>
    public static RrbList<T> WithHead<T>(T[] head, int headLen, RrbList<T> rest) =>
        RrbList<T>.WithHead(headLen == 0 ? null : head, headLen, rest);

    /// <summary>
    ///     A full leaf of `items` in front of a tree of `treeSize` items. Returns the root, the gap and
    ///     the shift.
    /// </summary>
    public static (Node<T> Root, int Gap, int Shift) PushInFront<T>(Node<T> root, int treeSize, int gap, T[] items,
        int len, int shift)
    {
        var r = RrbList<T>.PushInFront(root, treeSize, ref gap, items, len, ref shift);
        return (r, gap, shift);
    }

    /// <summary>
    ///     A full leaf of `items` after the tree. A dense tree keeps its gap. Returns the root, the
    ///     shift and the gap.
    /// </summary>
    public static (Node<T> Root, int Shift, int Gap) AppendLeaf<T>(Node<T> root, T[] items, int gap, int treeItems,
        int shift)
    {
        var leaf = new LeafNode<T>(items, items.Length, OwnerId.None);
        var r = RrbAlgorithm.AppendLeafToTree(OrNull(root), leaf, ref shift, OwnerId.None);
        if (gap > 0) r = RrbList<T>.NormalizeGap(r, ref shift, ref gap, treeItems)!;
        return (r, shift, gap);
    }

    /// <summary>
    ///     A tree with a gap, as the gap rules have it. Returns the root, the shift and the gap.
    ///     See <see cref="RrbList{T}.NormalizeGap" />.
    /// </summary>
    public static (Node<T> Root, int Shift, int Gap) NormalizeGap<T>(Node<T> root, int shift, int gap, int treeItems)
    {
        var r = RrbList<T>.NormalizeGap(OrNull(root), ref shift, ref gap, treeItems);
        return (OrNone(r), shift, gap);
    }

    /// <summary>The node at `shift` without its first `toDrop` items. The left spine gets size tables.</summary>
    public static Node<T> SliceLeft<T>(Node<T> n, int toDrop, int shift) => RrbAlgorithm.SliceLeftRec(n, toDrop, shift);

    /// <summary>The length of the first leaf under the node at `shift`.</summary>
    public static int FirstLeafLen<T>(Node<T> n, int shift) => RrbAlgorithm.FirstLeafLen(n, shift);

    /// <summary>A root at most a level taller than `treeItems` need. Returns the root and the shift.</summary>
    public static (Node<T> Root, int Shift) LowerRoot<T>(Node<T> root, int shift, int treeItems)
    {
        var r = RrbAlgorithm.LowerRoot(OrNull(root), ref shift, treeItems);
        return (OrNone(r), shift);
    }

    // ---- Nodes ------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Len<T>(Node<T> n) => n.Len;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRelaxed<T>(Node<T> n) => n.IsRelaxed();

    /// <summary>A leaf's items, in [0, Len).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T[] Items<T>(Node<T> n) => Unsafe.As<LeafNode<T>>(n).Items;

    /// <summary>An internal node's children, in [0, Len).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Node<T>[] Children<T>(Node<T> n) => Unsafe.As<InternalNode<T>>(n).Children!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Node<T> Child<T>(Node<T> n, int i) => Unsafe.As<InternalNode<T>>(n).Children[i]!;

    /// <summary>The cumulative sizes of an internal node at `shift`: its table, or one worked out for a dense node.</summary>
    public static int[] Cumulative<T>(Node<T> n, int shift)
    {
        var node = Unsafe.As<InternalNode<T>>(n);
        return node.SizeTable ?? RrbAlgorithm.DenseTable(node, shift);
    }

    /// <summary>How much the child at `i` of an internal node at `shift` holds.</summary>
    public static int ChildSize<T>(Node<T> n, int i, int shift) =>
        RrbAlgorithm.ChildSize(Unsafe.As<InternalNode<T>>(n), i, shift);

    /// <summary>How many items the node at `shift` holds.</summary>
    public static int CountTree<T>(Node<T> n, int shift) => RrbAlgorithm.CountTree(n, shift);

    /// <summary>The child of an internal node at `shift` that holds `index`, and the index within it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int Child, int Index) ChildIndex<T>(Node<T> n, int index, int shift) =>
        RrbAlgorithm.GetChildIndexAvx(Unsafe.As<InternalNode<T>>(n), index, shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Node<T> Leaf<T>(T[] items, int len) => new LeafNode<T>(items, len, OwnerId.None);

    /// <summary>A relaxed node over the first `len` of `children`, with `table` as its cumulative sizes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Node<T> Relaxed<T>(Node<T>[] children, int[] table, int len) =>
        new InternalNode<T>(children!, table, len, OwnerId.None);

    /// <summary>A dense node over the first `len` of `children`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Node<T> Dense<T>(Node<T>[] children, int len) =>
        new InternalNode<T>(children!, null, len, OwnerId.None);

    // ---- The search step invariant ---------------------------------------

    /// <summary>The most children a node may have for `slots` slots below it.</summary>
    public static int AllowedChildren(int slots) => RrbAlgorithm.AllowedChildren(slots);

    /// <summary>The slots the first `count` children hold.</summary>
    public static int SlotsOfChildren<T>(Node<T>[] children, int count) =>
        RrbAlgorithm.SlotsOfChildren(children!, count);

    /// <summary>
    ///     A node at `shift` over the first `count` children, each holding sizes[i], repacked when they
    ///     break the invariant. Returns a second node, or <see cref="NoTree{T}" />, when they need two.
    /// </summary>
    public static (Node<T> Node, Node<T> Overflow) BuildChecked<T>(Node<T>[] children, int[] sizes, int count,
        int shift)
    {
        var r = RrbAlgorithm.BuildChecked(children!, sizes, count, shift);
        return (r.NewNode, OrNone(r.Overflow));
    }

    /// <summary>
    ///     The `count` nodes from `start` of `children`, holding `sizes`, repacked into plan.Length nodes
    ///     of plan[i] slots each. Returns a relaxed node over them.
    /// </summary>
    public static Node<T> Repack<T>(Node<T>[] children, int[] sizes, int start, int count, int[] plan, int shift) =>
        RrbAlgorithm.ExecuteConcatPlan(new ReadOnlySpan<Node<T>>(children, start, count),
            new ReadOnlySpan<int>(sizes, start, count), plan, plan.Length, shift);
}
