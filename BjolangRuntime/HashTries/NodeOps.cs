using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bjolang.Runtime.HashTries;

// One-node operations on the hash tries, for lib/std/hashset.bjo and
// lib/std/hashmap.protobjo.
//
// - `shift` is 5 times the depth of the node. The fragment of a hash at a
//   node is (hash >> shift) & 31, and picks the node's slot for it.
// - An operation that takes `owner` edits the node in place when the node is
//   owned by `owner` and `owner` is not 0. Otherwise, and whenever the node
//   changes size, it returns a new node owned by `owner`.
// - Elements are numbered in the node's own order: a leaf's or a branch's by
//   fragment, a collision node's as added.
[NonUserCode]
public static class NodeOps
{
    // What Probe returns besides a child's index.
    private const int Found = -1;
    private const int Vacant = -2;
    private const int Clash = -3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SizeOf<T>(Node<T> n) => (int)(n.Meta & 0xFF);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte KindOf<T>(Node<T> n) => (byte)(n.Meta >> 8);

    private static ulong MetaOf(int size, byte kind, long owner) =>
        (uint)size | ((ulong)kind << 8) | ((ulong)owner << 16);

    private static bool Editable<T>(Node<T> n, long owner) => owner != 0 && n.Meta >> 16 == (ulong)owner;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Bit(int hash, int shift) => 1u << ((hash >> shift) & 31);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int IndexOf(uint map, uint bit) => BitOperations.PopCount(map & (bit - 1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Span<T> LeafElements<T>(Node<T> n) =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<Elements1<T>, T>(ref Unsafe.As<Leaf1<T>>(n).Data), SizeOf(n));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref T[] BranchElements<T>(Node<T> n) => ref Unsafe.As<Branch1<T>>(n).Data;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Span<Node<T>> ChildSpan<T>(Node<T> n) =>
        MemoryMarshal.CreateSpan(ref Unsafe.As<Children1<T>, Node<T>>(ref Unsafe.As<Branch1<T>>(n).Children),
            SizeOf(n));

    private static Span<T> ElementSpan<T>(Node<T> n) => KindOf(n) switch
    {
        Kinds.Leaf => LeafElements(n),
        Kinds.Branch => BranchElements(n),
        _ => Unsafe.As<CollisionNode<T>>(n).Data
    };

    private static T[] Inserted<T>(ReadOnlySpan<T> xs, int i, T x)
    {
        var r = new T[xs.Length + 1];
        xs[..i].CopyTo(r);
        r[i] = x;
        xs[i..].CopyTo(r.AsSpan(i + 1));
        return r;
    }

    private static T[] Removed<T>(ReadOnlySpan<T> xs, int i)
    {
        if (xs.Length == 1) return [];
        var r = new T[xs.Length - 1];
        xs[..i].CopyTo(r);
        xs[(i + 1)..].CopyTo(r.AsSpan(i));
        return r;
    }

    // A leaf of `xs` at the fragments of `map`, or the empty leaf.
    private static Node<T> LeafOf<T>(ReadOnlySpan<T> xs, ulong map, long owner)
    {
        if (xs.Length == 0) return Alloc<T>.Empty;
        var leaf = Alloc<T>.Leaf(xs.Length, MetaOf(xs.Length, Kinds.Leaf, owner), map);
        xs.CopyTo(LeafElements(leaf));
        return leaf;
    }

    private static Node<T> BranchOf<T>(int children, ulong map, T[] elements, long owner)
    {
        var b = Alloc<T>.Branch(children, MetaOf(children, Kinds.Branch, owner), map);
        BranchElements(b) = elements;
        return b;
    }

    // ---------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------

    public static long OwnerNext() => OwnerIds.Next();

    /// <summary>The empty leaf, the root of an empty set.</summary>
    public static Node<T> Empty<T>() => Alloc<T>.Empty;

    /// <summary>0 for a leaf, 1 for a branch, 2 for a collision node.</summary>
    public static int Kind<T>(Node<T> n) => KindOf(n);

    /// <summary>The number of elements in the node itself.</summary>
    public static int Size<T>(Node<T> n) => KindOf(n) switch
    {
        Kinds.Leaf => SizeOf(n),
        Kinds.Branch => BranchElements(n).Length,
        _ => Unsafe.As<CollisionNode<T>>(n).Data.Length
    };

    public static int ChildCount<T>(Node<T> n) => KindOf(n) == Kinds.Branch ? SizeOf(n) : 0;

    /// <summary>The node's element `i`.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Element<T>(Node<T> n, int i) =>
        KindOf(n) == Kinds.Leaf ? LeafElements(n)[i] : ArrayOf(n)[i];

    private static T[] ArrayOf<T>(Node<T> n) =>
        KindOf(n) == Kinds.Branch ? BranchElements(n) : Unsafe.As<CollisionNode<T>>(n).Data;

    // Unchecked: `i` comes from Probe or from a loop below ChildCount.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Node<T> Child<T>(Node<T> n, int i) =>
        Unsafe.Add(ref Unsafe.As<Children1<T>, Node<T>>(ref Unsafe.As<Branch1<T>>(n).Children), i);

    /// <summary>The fragments that hold an element, one bit each.</summary>
    public static int ElementMap<T>(Node<T> n) => (int)(uint)n.Map;

    /// <summary>The fragments that hold a child, one bit each.</summary>
    public static int ChildMap<T>(Node<T> n) => (int)(n.Map >> 32);

    public static int CollisionHash<T>(Node<T> n) => Unsafe.As<CollisionNode<T>>(n).Hash;

    public static bool Same<T>(Node<T> a, Node<T> b) => ReferenceEquals(a, b);

    public static int Fragment(int hash, int shift) => (hash >> shift) & 31;

    public static int Hash<T>(IEqualityComparer<T> comparer, T x) => comparer.GetHashCode(x!);

    public static bool Equal<T>(IEqualityComparer<T> comparer, T a, T b) => comparer.Equals(a, b);

    public static bool SameComparer<T>(IEqualityComparer<T> a, IEqualityComparer<T> b) =>
        ReferenceEquals(a, b) || a.Equals(b);

    /// <summary>
    ///     Where `x` stands in the node.
    ///     - The index of the child to look in.
    ///     - -1: the node holds an element equal to `x`.
    ///     - -2: the node has room for `x`: its slot is empty, or it is a collision node of
    ///       `x`'s hash that does not hold `x`.
    ///     - -3: the slot holds another element, or it is a collision node of another hash.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Probe<T>(Node<T> n, T x, int hash, int shift, IEqualityComparer<T> comparer)
    {
        var kind = KindOf(n);
        if (kind == Kinds.Collision)
        {
            var c = Unsafe.As<CollisionNode<T>>(n);
            if (c.Hash != hash) return Clash;
            foreach (var y in c.Data)
                if (comparer.Equals(y, x))
                    return Found;
            return Vacant;
        }

        var bit = Bit(hash, shift);
        var map = n.Map;
        var elements = (uint)map;
        if ((elements & bit) != 0)
        {
            // Unchecked: the bit is set, so the node has element `i`.
            var i = IndexOf(elements, bit);
            var y = kind == Kinds.Leaf
                ? Unsafe.Add(ref Unsafe.As<Elements1<T>, T>(ref Unsafe.As<Leaf1<T>>(n).Data), i)
                : Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(BranchElements(n)), i);
            return comparer.Equals(y, x) ? Found : Clash;
        }

        var children = (uint)(map >> 32);
        return (children & bit) != 0 ? IndexOf(children, bit) : Vacant;
    }

    /// <summary>The element in the slot of `hash`. Only where Probe returned -3 for a leaf or a branch.</summary>
    public static T ElementAt<T>(Node<T> n, int hash, int shift) =>
        ElementSpan(n)[IndexOf((uint)n.Map, Bit(hash, shift))];

    /// <summary>
    ///     Where the entry of `key` stands in a map's node. As Probe, except that a found
    ///     entry returns -4 - its index in the node's own order, for Element,
    ///     WithElementAt and WithoutElementAt.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ProbeKey<TK, TV>(Node<(TK, TV)> n, TK key, int hash, int shift, IEqualityComparer<TK> comparer)
    {
        var kind = KindOf(n);
        if (kind == Kinds.Collision)
        {
            var c = Unsafe.As<CollisionNode<(TK, TV)>>(n);
            if (c.Hash != hash) return Clash;
            var data = c.Data;
            for (var at = 0; at < data.Length; at++)
                if (comparer.Equals(data[at].Item1, key))
                    return -4 - at;
            return Vacant;
        }

        var bit = Bit(hash, shift);
        var map = n.Map;
        var elements = (uint)map;
        if ((elements & bit) != 0)
        {
            // Unchecked: the bit is set, so the node has element `i`.
            var i = IndexOf(elements, bit);
            var y = kind == Kinds.Leaf
                ? Unsafe.Add(ref Unsafe.As<Elements1<(TK, TV)>, (TK, TV)>(ref Unsafe.As<Leaf1<(TK, TV)>>(n).Data), i)
                : Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(BranchElements(n)), i);
            return comparer.Equals(y.Item1, key) ? -4 - i : Clash;
        }

        var children = (uint)(map >> 32);
        return (children & bit) != 0 ? IndexOf(children, bit) : Vacant;
    }

    /// <summary>
    ///     The value of the entry ProbeKey found. `code` is ProbeKey's result, -4 - index.
    ///     Unchecked: only for a code ProbeKey returned for this node.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TV ValueAt<TK, TV>(Node<(TK, TV)> n, int code)
    {
        var i = -4 - code;
        var kind = KindOf(n);
        if (kind == Kinds.Leaf)
            return Unsafe.Add(ref Unsafe.As<Elements1<(TK, TV)>, (TK, TV)>(ref Unsafe.As<Leaf1<(TK, TV)>>(n).Data), i)
                .Item2;
        var data = kind == Kinds.Branch ? BranchElements(n) : Unsafe.As<CollisionNode<(TK, TV)>>(n).Data;
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), i).Item2;
    }

    /// <summary>Whether `a` and `b` are equal by .NET's default equality, as a map compares a value it replaces.</summary>
    public static bool SameValue<TV>(TV a, TV b) => EqualityComparer<TV>.Default.Equals(a, b);

    // ---------------------------------------------------------------------------
    // Adding
    // ---------------------------------------------------------------------------

    /// <summary>
    ///     The node with `x` added, where Probe returned -2: in the empty slot of `hash`, or
    ///     at the end of a collision node.
    /// </summary>
    public static Node<T> WithElement<T>(Node<T> n, T x, int hash, int shift, long owner)
    {
        var kind = KindOf(n);
        if (kind == Kinds.Collision)
        {
            var c = Unsafe.As<CollisionNode<T>>(n);
            var data = Inserted<T>(c.Data, c.Data.Length, x);
            if (Editable(n, owner))
            {
                c.Data = data;
                return n;
            }

            return new CollisionNode<T>
                { Meta = MetaOf(0, Kinds.Collision, owner), Data = data, Hash = c.Hash };
        }

        var bit = Bit(hash, shift);
        var i = IndexOf((uint)n.Map, bit);
        if (kind == Kinds.Leaf)
        {
            var old = LeafElements(n);
            var size = old.Length + 1;
            var leaf = Alloc<T>.Leaf(size, MetaOf(size, Kinds.Leaf, owner), n.Map | bit);
            var dst = LeafElements(leaf);
            old[..i].CopyTo(dst);
            dst[i] = x;
            old[i..].CopyTo(dst[(i + 1)..]);
            return leaf;
        }

        var elements = Inserted<T>(BranchElements(n), i, x);
        if (Editable(n, owner))
        {
            BranchElements(n) = elements;
            n.Map |= bit;
            return n;
        }

        var b = BranchOf(SizeOf(n), n.Map | bit, elements, owner);
        ChildSpan(n).CopyTo(ChildSpan(b));
        return b;
    }

    /// <summary>
    ///     The leaf or branch with the element in the slot of `hash` replaced by the child
    ///     `sub`, which holds that element.
    /// </summary>
    public static Node<T> WithSubtree<T>(Node<T> n, int hash, int shift, Node<T> sub, long owner)
    {
        var bit = Bit(hash, shift);
        var elements = (uint)n.Map;
        var children = (uint)(n.Map >> 32);
        var i = IndexOf(elements, bit);
        var j = IndexOf(children, bit);
        var leaf = KindOf(n) == Kinds.Leaf;
        var old = leaf ? LeafElements(n) : BranchElements(n);
        var b = BranchOf(leaf ? 1 : SizeOf(n) + 1, ((ulong)(children | bit) << 32) | (elements & ~bit),
            Removed<T>(old, i), owner);
        var dst = ChildSpan(b);
        if (leaf)
        {
            dst[0] = sub;
            return b;
        }

        var src = ChildSpan(n);
        src[..j].CopyTo(dst);
        dst[j] = sub;
        src[j..].CopyTo(dst[(j + 1)..]);
        return b;
    }

    /// <summary>The branch with child `i` replaced by `child`.</summary>
    public static Node<T> WithChild<T>(Node<T> n, int i, Node<T> child, long owner)
    {
        if (Editable(n, owner))
        {
            ChildSpan(n)[i] = child;
            return n;
        }

        var b = BranchOf(SizeOf(n), n.Map, BranchElements(n), owner);
        var dst = ChildSpan(b);
        ChildSpan(n).CopyTo(dst);
        dst[i] = child;
        return b;
    }

    /// <summary>A leaf of `x`, at a node at `shift`.</summary>
    public static Node<T> Single<T>(T x, int hash, int shift, long owner)
    {
        var leaf = Alloc<T>.Leaf(1, MetaOf(1, Kinds.Leaf, owner), Bit(hash, shift));
        LeafElements(leaf)[0] = x;
        return leaf;
    }

    /// <summary>A leaf of `a` and `b`, whose fragments at `shift` differ.</summary>
    public static Node<T> Pair<T>(T a, int ha, T b, int hb, int shift, long owner)
    {
        var bitA = Bit(ha, shift);
        var bitB = Bit(hb, shift);
        var leaf = Alloc<T>.Leaf(2, MetaOf(2, Kinds.Leaf, owner), bitA | bitB);
        var dst = LeafElements(leaf);
        dst[bitA < bitB ? 0 : 1] = a;
        dst[bitA < bitB ? 1 : 0] = b;
        return leaf;
    }

    /// <summary>A branch whose one child is `child`, in the slot of `hash`.</summary>
    public static Node<T> Wrap<T>(Node<T> child, int hash, int shift, long owner)
    {
        var b = BranchOf<T>(1, (ulong)Bit(hash, shift) << 32, [], owner);
        ChildSpan(b)[0] = child;
        return b;
    }

    /// <summary>A branch of `x` and the child `child`, whose fragments at `shift` differ.</summary>
    public static Node<T> Fork<T>(T x, int hx, Node<T> child, int hc, int shift, long owner)
    {
        var b = BranchOf(1, ((ulong)Bit(hc, shift) << 32) | Bit(hx, shift), [x], owner);
        ChildSpan(b)[0] = child;
        return b;
    }

    /// <summary>A collision node of `a` and `b`, which both have the hash `hash`.</summary>
    public static Node<T> Collision<T>(T a, T b, int hash, long owner) =>
        new CollisionNode<T> { Meta = MetaOf(0, Kinds.Collision, owner), Data = [a, b], Hash = hash };

    /// <summary>
    ///     The node with element `i` replaced by `x`. Edits a leaf in place when the owner may;
    ///     an element array is always copied, since another node may share it.
    /// </summary>
    public static Node<T> WithElementAt<T>(Node<T> n, int i, T x, long owner)
    {
        var kind = KindOf(n);
        if (kind == Kinds.Leaf)
        {
            if (Editable(n, owner))
            {
                LeafElements(n)[i] = x;
                return n;
            }

            var old = LeafElements(n);
            var leaf = Alloc<T>.Leaf(old.Length, MetaOf(old.Length, Kinds.Leaf, owner), n.Map);
            var dst = LeafElements(leaf);
            old.CopyTo(dst);
            dst[i] = x;
            return leaf;
        }

        var src = ArrayOf(n);
        var data = new T[src.Length];
        src.AsSpan().CopyTo(data);
        data[i] = x;
        if (kind == Kinds.Collision)
        {
            var c = Unsafe.As<CollisionNode<T>>(n);
            if (Editable(n, owner))
            {
                c.Data = data;
                return n;
            }

            return new CollisionNode<T> { Meta = MetaOf(0, Kinds.Collision, owner), Data = data, Hash = c.Hash };
        }

        if (Editable(n, owner))
        {
            BranchElements(n) = data;
            return n;
        }

        var b = BranchOf(SizeOf(n), n.Map, data, owner);
        ChildSpan(n).CopyTo(ChildSpan(b));
        return b;
    }

    // ---------------------------------------------------------------------------
    // Removing
    // ---------------------------------------------------------------------------

    /// <summary>
    ///     The node without element `i`, which is in the slot of `hash`. As WithoutElement: a
    ///     collision node left with one element becomes a leaf of it, and a leaf left with none
    ///     the empty leaf.
    /// </summary>
    public static Node<T> WithoutElementAt<T>(Node<T> n, int i, int hash, int shift, long owner)
    {
        var kind = KindOf(n);
        if (kind == Kinds.Collision)
        {
            var c = Unsafe.As<CollisionNode<T>>(n);
            if (c.Data.Length == 2) return Single(c.Data[1 - i], c.Hash, shift, owner);
            var data = Removed<T>(c.Data, i);
            if (Editable(n, owner))
            {
                c.Data = data;
                return n;
            }

            return new CollisionNode<T>
                { Meta = MetaOf(0, Kinds.Collision, owner), Data = data, Hash = c.Hash };
        }

        var map = n.Map & ~(ulong)Bit(hash, shift);
        if (kind == Kinds.Leaf)
        {
            var old = LeafElements(n);
            if (old.Length == 1) return Alloc<T>.Empty;
            var leaf = Alloc<T>.Leaf(old.Length - 1, MetaOf(old.Length - 1, Kinds.Leaf, owner), map);
            var dst = LeafElements(leaf);
            old[..i].CopyTo(dst);
            old[(i + 1)..].CopyTo(dst[i..]);
            return leaf;
        }

        var elements = Removed<T>(BranchElements(n), i);
        if (Editable(n, owner))
        {
            BranchElements(n) = elements;
            n.Map = map;
            return n;
        }

        var b = BranchOf(SizeOf(n), map, elements, owner);
        ChildSpan(n).CopyTo(ChildSpan(b));
        return b;
    }

    /// <summary>
    ///     The node without its element equal to `x`, where Probe returned -1. A collision
    ///     node left with one element becomes a leaf of it, and a leaf left with none the
    ///     empty leaf.
    /// </summary>
    public static Node<T> WithoutElement<T>(Node<T> n, T x, int hash, int shift,
        IEqualityComparer<T> comparer, long owner)
    {
        var kind = KindOf(n);
        if (kind == Kinds.Collision)
        {
            var c = Unsafe.As<CollisionNode<T>>(n);
            var at = 0;
            while (!comparer.Equals(c.Data[at], x)) at++;
            if (c.Data.Length == 2) return Single(c.Data[1 - at], c.Hash, shift, owner);
            var data = Removed<T>(c.Data, at);
            if (Editable(n, owner))
            {
                c.Data = data;
                return n;
            }

            return new CollisionNode<T>
                { Meta = MetaOf(0, Kinds.Collision, owner), Data = data, Hash = c.Hash };
        }

        var bit = Bit(hash, shift);
        var i = IndexOf((uint)n.Map, bit);
        var map = n.Map & ~(ulong)bit;
        if (kind == Kinds.Leaf)
        {
            var old = LeafElements(n);
            if (old.Length == 1) return Alloc<T>.Empty;
            var leaf = Alloc<T>.Leaf(old.Length - 1, MetaOf(old.Length - 1, Kinds.Leaf, owner), map);
            var dst = LeafElements(leaf);
            old[..i].CopyTo(dst);
            old[(i + 1)..].CopyTo(dst[i..]);
            return leaf;
        }

        var elements = Removed<T>(BranchElements(n), i);
        if (Editable(n, owner))
        {
            BranchElements(n) = elements;
            n.Map = map;
            return n;
        }

        var b = BranchOf(SizeOf(n), map, elements, owner);
        ChildSpan(n).CopyTo(ChildSpan(b));
        return b;
    }

    /// <summary>The branch without child `i`, in the slot of `hash`. A branch left with no child becomes a leaf.</summary>
    public static Node<T> WithoutChild<T>(Node<T> n, int i, int hash, int shift, long owner)
    {
        var map = n.Map & ~((ulong)Bit(hash, shift) << 32);
        var size = SizeOf(n);
        if (size == 1) return LeafOf<T>(BranchElements(n), map, owner);
        var b = BranchOf(size - 1, map, BranchElements(n), owner);
        var src = ChildSpan(n);
        var dst = ChildSpan(b);
        src[..i].CopyTo(dst);
        src[(i + 1)..].CopyTo(dst[i..]);
        return b;
    }

    /// <summary>
    ///     The branch with child `i`, in the slot of `hash`, replaced by its one element `x`.
    ///     A branch left with no child becomes a leaf.
    /// </summary>
    public static Node<T> WithChildInlined<T>(Node<T> n, int i, T x, int hash, int shift, long owner)
    {
        var bit = Bit(hash, shift);
        var map = (n.Map & ~((ulong)bit << 32)) | bit;
        var elements = Inserted<T>(BranchElements(n), IndexOf((uint)map, bit), x);
        var size = SizeOf(n);
        if (size == 1) return LeafOf<T>(elements, map, owner);
        var b = BranchOf(size - 1, map, elements, owner);
        var src = ChildSpan(n);
        var dst = ChildSpan(b);
        src[..i].CopyTo(dst);
        src[(i + 1)..].CopyTo(dst[i..]);
        return b;
    }

    // ---------------------------------------------------------------------------
    // Building
    // ---------------------------------------------------------------------------

    /// <summary>
    ///     A leaf or a branch of the elements of `elements` and the children of `children`
    ///     from `start`, one for each bit of `elementMap` and of `childMap`, in the order of
    ///     the bits. A leaf if `childMap` is 0.
    /// </summary>
    public static Node<T> Assemble<T>(int elementMap, T[] elements, int childMap, Node<T>[] children, int start,
        long owner)
    {
        var e = BitOperations.PopCount((uint)elementMap);
        var map = ((ulong)(uint)childMap << 32) | (uint)elementMap;
        if (childMap == 0) return LeafOf<T>(elements.AsSpan(start, e), map, owner);
        var c = BitOperations.PopCount((uint)childMap);
        var b = BranchOf(c, map, e == 0 ? [] : elements.AsSpan(start, e).ToArray(), owner);
        children.AsSpan(start, c).CopyTo(ChildSpan(b));
        return b;
    }

    /// <summary>A collision node of the first `count` elements of `elements`, all of the hash `hash`.</summary>
    public static Node<T> CollisionOf<T>(T[] elements, int count, int hash, long owner) =>
        new CollisionNode<T>
        {
            Meta = MetaOf(0, Kinds.Collision, owner), Data = elements.AsSpan(0, count).ToArray(), Hash = hash
        };
}
