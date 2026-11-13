using System.Runtime.CompilerServices;

namespace Bjolang.Runtime.Rrb;

/// <summary>
///     The path from the root of a list's tree to one leaf, so that a walk
///     finds the next leaf with a step, not a search from the root.
/// </summary>
/// <remarks>
///     For a relaxed tree, <see cref="RrbList{T}.LeafAt"/> searches a size
///     table at each relaxed level, for each leaf. The path keeps the index of
///     the child at each level, so the next leaf is one more at the lowest
///     level that has a next child, and then the first children down. A walk
///     of a vec joined from 1000-element pieces took half the time with it.
///     For a dense tree, LeafAt is as fast, so a walk uses LeafAt there.
///     <para>
///     Only the indices are kept, not the nodes: a step finds the nodes again
///     from the root, with a read of a child array at each level. A step is
///     inlined in the loop of a walk, and a reference written to the heap is a
///     call (the GC's write barrier), which on Linux keeps the loop's float
///     sums in memory: a sum of 1M doubles took 2.64 ms with a write of a
///     reference at each new leaf, and 0.65 with a write of an int.
///     </para>
///     <para>
///     Only the tree is on the path. The head and tail are found with LeafAt.
///     </para>
/// </remarks>
[NonUserCode]
public sealed class TreePath<T>
{
    /// <summary>The list whose tree the path is in.</summary>
    public readonly RrbList<T> List;

    // The index of the child on the path at each level, the root's first.
    private readonly int[] _at;

    // The list index after the leaf at the end of the path; -1 before Seek.
    private int _leafEnd = -1;

    public TreePath(RrbList<T> list)
    {
        List = list;
        _at = new int[list.Shift / Constants.RRB_BITS];
    }

    /// <summary>
    ///     The leaf of element <paramref name="index"/>, which must be in the
    ///     tree, as <see cref="RrbList{T}.LeafAt"/> returns it, with the path
    ///     set to it.
    /// </summary>
    /// <remarks>
    ///     For a walk's step to its next leaf, which is inlined in the walk's
    ///     loop (see <see cref="RrbList{T}.LeafInWalk"/>), and so makes no
    ///     call and writes no reference.
    ///     <list type="bullet">
    ///         <item>A step when <paramref name="index"/> is the first
    ///         element after the leaf of the path.</item>
    ///         <item>Else a seek from the root, as at the first leaf of the
    ///         tree that a walk reaches.</item>
    ///     </list>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T[] LeafFrom(int index, out int position, out int length)
    {
        if (index == _leafEnd)
        {
            var next = Step(index, out position, out length);
            if (next != null) return next;
        }

        return Seek(index, out position, out length);
    }

    /// <summary>
    ///     Sets the path to the leaf of element <paramref name="index"/>, which
    ///     must be in the tree, and returns the leaf as <see cref="RrbList{T}.LeafAt"/>
    ///     does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T[] Seek(int index, out int position, out int length)
    {
        var node = List.Root!;
        var i = index - List.HeadLen + List.Gap;
        var shift = List.Shift;
        for (var level = 0; shift > 0; level++, shift -= Constants.RRB_BITS)
        {
            var inner = RrbAlgorithm.AsInternal(node);
            int child;
            if (inner.IsRelaxed())
            {
                (child, i) = RrbAlgorithm.GetRelaxedIndex(inner, i, shift);
            }
            else
            {
                child = (i >> shift) & Constants.RRB_MASK;
                i &= (1 << shift) - 1;
            }

            _at[level] = child;
            node = inner.Children[child]!;
        }

        var leaf = RrbAlgorithm.AsLeaf(node);
        position = i;
        length = leaf.Len;
        _leafEnd = index - i + length;
        return leaf.Items;
    }

    /// <summary>
    ///     Moves the path to the next leaf and returns it, with
    ///     <paramref name="position"/> 0. Returns null if
    ///     <paramref name="index"/> is not the first element of the next leaf,
    ///     or the next leaf is not in the tree; the path is then not changed.
    /// </summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item>From the root along the path, the lowest level whose node
    ///         has a child after the path's.</item>
    ///         <item>That child, and the first children below it.</item>
    ///     </list>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T[]? Step(int index, out int position, out int length)
    {
        position = length = 0;
        if (index != _leafEnd || index >= List.Count - List.TailLen) return null;

        var levels = _at.Length;
        var node = List.Root!;
        var turn = -1;
        InternalNode<T>? turnNode = null;
        for (var level = 0; level < levels; level++)
        {
            var inner = RrbAlgorithm.AsInternal(node);
            if (_at[level] + 1 < inner.Len)
            {
                turn = level;
                turnNode = inner;
            }

            node = inner.Children[_at[level]]!;
        }

        if (turn < 0) return null;

        node = turnNode!.Children[++_at[turn]]!;
        for (var level = turn + 1; level < levels; level++)
        {
            _at[level] = 0;
            node = RrbAlgorithm.AsInternal(node).Children[0]!;
        }

        var leaf = RrbAlgorithm.AsLeaf(node);
        length = leaf.Len;
        _leafEnd += length;
        return leaf.Items;
    }
}
