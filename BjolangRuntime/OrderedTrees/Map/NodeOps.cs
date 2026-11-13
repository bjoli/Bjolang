using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bjolang.Runtime.OrderedTrees.MapNodes;

/// <summary>
/// Operations on one node, or on two nodes' slots, for a tree written in
/// Bjolang.
/// </summary>
///
/// <remarks>
/// <list type="bullet">
/// <item>The nodes stay in C# because an internal node holds its keys and
/// children in inline arrays, which Bjolang cannot declare.</item>
/// <item>Every operation that spans more than two nodes (descent, splits,
/// merges, walks) is the caller's.</item>
/// <item>The <c>Set</c>, <c>Move</c> and <c>SetCount</c> operations write to
/// the node they are given. The caller makes it editable first.</item>
/// <item>A <c>Move</c> may overlap within one node: it copies as
/// <c>Span.CopyTo</c> does.</item>
/// <item>Nothing checks a slot against the count. A slot past the capacity
/// raises <c>IndexOutOfRangeException</c>.</item>
/// </list>
/// </remarks>
[NonUserCode]
public static class NodeOps
{
    public const int LeafCapacity = 64;
    public const int InternalCapacity = 32;

    // ---------------------------------------------------------------------
    // Owners
    // ---------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OwnerId OwnerNone() => OwnerId.None;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static OwnerId OwnerNext() => OwnerId.Next();

    /// Returns <paramref name="node"/> if <paramref name="owner"/> owns it,
    /// and otherwise a copy that it owns. With the owner <c>None</c> it always
    /// returns a copy.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Node<TK> Editable<TK>(Node<TK> node, OwnerId owner) => node.EnsureEditable(owner);

    // ---------------------------------------------------------------------
    // Creating
    // ---------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LeafNode<TK, TV> NewLeaf<TK, TV>(OwnerId owner) => new(owner);

    /// A node known to be a leaf, as one. No check.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LeafNode<TK, TV> AsLeaf<TK, TV>(Node<TK> node) => node.AsLeaf<TV>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Node<TK> LeafAsNode<TK, TV>(LeafNode<TK, TV> leaf) => leaf;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Node<TK> NewInternal<TK>(OwnerId owner) => new InternalNode<TK>(owner);

    // ---------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLeaf<TK>(Node<TK> node) => node.IsLeaf;

    /// The number of keys. An internal node has one child more.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count<TK>(Node<TK> node) => node.Header.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TK LeafKey<TK, TV>(LeafNode<TK, TV> leaf, int i) => leaf.Keys![i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TV LeafValue<TK, TV>(LeafNode<TK, TV> leaf, int i) => leaf.Values[i];

    /// The key array of a leaf, of length <see cref="LeafCapacity"/>. Only
    /// the first <see cref="Count{TK}"/> slots are keys.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TK[] LeafKeys<TK, TV>(LeafNode<TK, TV> leaf) => leaf.Keys!;

    /// The value array of a leaf, as <see cref="LeafKeys{TK, TV}"/>.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TV[] LeafValues<TK, TV>(LeafNode<TK, TV> leaf) => leaf.Values;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TK Key<TK>(Node<TK> node, int i) => node.AsInternal().Keys[i];

    /// Key slot <paramref name="i"/> of a leaf or an internal node, by a
    /// virtual call. For callers that do not know the value type.
    public static TK KeyOf<TK>(Node<TK> node, int i) => node.GetKeys()[i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Node<TK> Child<TK>(Node<TK> node, int i) => node.AsInternal().Children[i]!;

    // ---------------------------------------------------------------------
    // Searching
    // ---------------------------------------------------------------------

    /// The slot of <paramref name="key"/> in a leaf if it is there, and
    /// otherwise <c>-(slot + 1)</c> for the slot it would go in.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LeafFind<TK, TV>(LeafNode<TK, TV> leaf, TK key, IComparer<TK> comparer)
    {
        var i = BTreeFunctions.FindIndex(leaf, key, comparer);
        return i < leaf.Header.Count && comparer.Compare(leaf.Keys![i], key) == 0 ? i : -(i + 1);
    }

    /// The child of an internal node that <paramref name="key"/> belongs in:
    /// the number of keys not greater than it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Route<TK>(Node<TK> node, TK key, IComparer<TK> comparer) =>
        BTreeFunctions.FindRoutingIndex(node.AsInternal(), key, comparer);

    /// <paramref name="comparer"/>'s order of two keys: negative, zero or
    /// positive.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compare<TK>(IComparer<TK> comparer, TK a, TK b) => comparer.Compare(a, b);

    // ---------------------------------------------------------------------
    // Writing (to an editable node)
    // ---------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetCount<TK>(Node<TK> node, int count) => node.SetCount(count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LeafSet<TK, TV>(LeafNode<TK, TV> leaf, int i, TK key, TV value)
    {
        leaf.Keys![i] = key;
        leaf.Values[i] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LeafSetValue<TK, TV>(LeafNode<TK, TV> leaf, int i, TV value) => leaf.Values[i] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetKey<TK>(Node<TK> node, int i, TK key) => node.AsInternal().Keys[i] = key;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetChild<TK>(Node<TK> node, int i, Node<TK> child) => node.AsInternal().Children[i] = child;

    /// Copies <paramref name="n"/> keys and values of a leaf from slot
    /// <paramref name="from"/> of <paramref name="source"/> to slot
    /// <paramref name="to"/> of <paramref name="target"/>.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LeafMove<TK, TV>(LeafNode<TK, TV> source, int from, LeafNode<TK, TV> target, int to, int n)
    {
        source.Keys.AsSpan(from, n).CopyTo(target.Keys.AsSpan(to));
        source.Values.AsSpan(from, n).CopyTo(target.Values.AsSpan(to));
    }

    /// Copies <paramref name="n"/> keys of an internal node.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void KeyMove<TK>(Node<TK> source, int from, Node<TK> target, int to, int n)
    {
        Span<TK> s = source.AsInternal().Keys;
        Span<TK> t = target.AsInternal().Keys;
        s.Slice(from, n).CopyTo(t.Slice(to));
    }

    /// Copies <paramref name="n"/> children of an internal node.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ChildMove<TK>(Node<TK> source, int from, Node<TK> target, int to, int n)
    {
        Span<Node<TK>> s = source.AsInternal().Children!;
        Span<Node<TK>> t = target.AsInternal().Children!;
        s.Slice(from, n).CopyTo(t.Slice(to));
    }
}
