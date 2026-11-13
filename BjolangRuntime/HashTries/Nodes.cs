using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bjolang.Runtime.HashTries;

// The nodes of the hash tries behind (std hashset) and the prelude's Map.
// lib/std/hashset.bjo and lib/std/hashmap.protobjo hold the trie code;
// NodeOps does the operations on one node.
//
// - A map's element is a `(K, V)` value tuple, compared and hashed by key.
// - Meta holds the node's size (bits 0-7), its kind (bits 8-15) and the
//   owner that may edit it in place (bits 16-63, 0 for none).
// - Map holds the hash fragments that hold an element (bits 0-31) and those
//   that hold a child (bits 32-63). A collision node uses neither.
// - A leaf holds only elements, inline: LeafN holds N. A branch holds its
//   elements in an array and its N children inline. A collision node holds
//   the elements whose full hashes are all `Hash`.
// - Every size of a kind has the same field layout, so NodeOps reads any
//   LeafN as a Leaf1 and any BranchN as a Branch1, with the size from Meta.

[StructLayout(LayoutKind.Sequential)]
[NonUserCode]
public abstract class Node<T>
{
    public ulong Meta;
    public ulong Map;
}

[NonUserCode]
internal static class Kinds
{
    public const byte Leaf = 0;
    public const byte Branch = 1;
    public const byte Collision = 2;
}

[NonUserCode]
internal sealed class CollisionNode<T> : Node<T>
{
    public T[] Data = null!;
    public int Hash;
}

// Owners of transients. 0 is no owner: a node with owner 0 is never edited
// in place. Each thread takes ids in batches of 100 from a global counter.
[NonUserCode]
internal static class OwnerIds
{
    private const int BatchSize = 100;
    private static long _reserved;
    [ThreadStatic] private static long _next;
    [ThreadStatic] private static int _left;

    public static long Next()
    {
        if (_left == 0)
        {
            _next = Interlocked.Add(ref _reserved, BatchSize) - BatchSize;
            _left = BatchSize;
        }
        _left--;
        return ++_next;
    }
}

// The sizes. Elements and children are inline arrays, so a node is one object.

[InlineArray(1), NonUserCode] internal struct Elements1<T> { private T _e; }
[NonUserCode] internal sealed class Leaf1<T> : Node<T> { public Elements1<T> Data; }
[InlineArray(2), NonUserCode] internal struct Elements2<T> { private T _e; }
[NonUserCode] internal sealed class Leaf2<T> : Node<T> { public Elements2<T> Data; }
[InlineArray(3), NonUserCode] internal struct Elements3<T> { private T _e; }
[NonUserCode] internal sealed class Leaf3<T> : Node<T> { public Elements3<T> Data; }
[InlineArray(4), NonUserCode] internal struct Elements4<T> { private T _e; }
[NonUserCode] internal sealed class Leaf4<T> : Node<T> { public Elements4<T> Data; }
[InlineArray(5), NonUserCode] internal struct Elements5<T> { private T _e; }
[NonUserCode] internal sealed class Leaf5<T> : Node<T> { public Elements5<T> Data; }
[InlineArray(6), NonUserCode] internal struct Elements6<T> { private T _e; }
[NonUserCode] internal sealed class Leaf6<T> : Node<T> { public Elements6<T> Data; }
[InlineArray(7), NonUserCode] internal struct Elements7<T> { private T _e; }
[NonUserCode] internal sealed class Leaf7<T> : Node<T> { public Elements7<T> Data; }
[InlineArray(8), NonUserCode] internal struct Elements8<T> { private T _e; }
[NonUserCode] internal sealed class Leaf8<T> : Node<T> { public Elements8<T> Data; }
[InlineArray(9), NonUserCode] internal struct Elements9<T> { private T _e; }
[NonUserCode] internal sealed class Leaf9<T> : Node<T> { public Elements9<T> Data; }
[InlineArray(10), NonUserCode] internal struct Elements10<T> { private T _e; }
[NonUserCode] internal sealed class Leaf10<T> : Node<T> { public Elements10<T> Data; }
[InlineArray(11), NonUserCode] internal struct Elements11<T> { private T _e; }
[NonUserCode] internal sealed class Leaf11<T> : Node<T> { public Elements11<T> Data; }
[InlineArray(12), NonUserCode] internal struct Elements12<T> { private T _e; }
[NonUserCode] internal sealed class Leaf12<T> : Node<T> { public Elements12<T> Data; }
[InlineArray(13), NonUserCode] internal struct Elements13<T> { private T _e; }
[NonUserCode] internal sealed class Leaf13<T> : Node<T> { public Elements13<T> Data; }
[InlineArray(14), NonUserCode] internal struct Elements14<T> { private T _e; }
[NonUserCode] internal sealed class Leaf14<T> : Node<T> { public Elements14<T> Data; }
[InlineArray(15), NonUserCode] internal struct Elements15<T> { private T _e; }
[NonUserCode] internal sealed class Leaf15<T> : Node<T> { public Elements15<T> Data; }
[InlineArray(16), NonUserCode] internal struct Elements16<T> { private T _e; }
[NonUserCode] internal sealed class Leaf16<T> : Node<T> { public Elements16<T> Data; }
[InlineArray(17), NonUserCode] internal struct Elements17<T> { private T _e; }
[NonUserCode] internal sealed class Leaf17<T> : Node<T> { public Elements17<T> Data; }
[InlineArray(18), NonUserCode] internal struct Elements18<T> { private T _e; }
[NonUserCode] internal sealed class Leaf18<T> : Node<T> { public Elements18<T> Data; }
[InlineArray(19), NonUserCode] internal struct Elements19<T> { private T _e; }
[NonUserCode] internal sealed class Leaf19<T> : Node<T> { public Elements19<T> Data; }
[InlineArray(20), NonUserCode] internal struct Elements20<T> { private T _e; }
[NonUserCode] internal sealed class Leaf20<T> : Node<T> { public Elements20<T> Data; }
[InlineArray(21), NonUserCode] internal struct Elements21<T> { private T _e; }
[NonUserCode] internal sealed class Leaf21<T> : Node<T> { public Elements21<T> Data; }
[InlineArray(22), NonUserCode] internal struct Elements22<T> { private T _e; }
[NonUserCode] internal sealed class Leaf22<T> : Node<T> { public Elements22<T> Data; }
[InlineArray(23), NonUserCode] internal struct Elements23<T> { private T _e; }
[NonUserCode] internal sealed class Leaf23<T> : Node<T> { public Elements23<T> Data; }
[InlineArray(24), NonUserCode] internal struct Elements24<T> { private T _e; }
[NonUserCode] internal sealed class Leaf24<T> : Node<T> { public Elements24<T> Data; }
[InlineArray(25), NonUserCode] internal struct Elements25<T> { private T _e; }
[NonUserCode] internal sealed class Leaf25<T> : Node<T> { public Elements25<T> Data; }
[InlineArray(26), NonUserCode] internal struct Elements26<T> { private T _e; }
[NonUserCode] internal sealed class Leaf26<T> : Node<T> { public Elements26<T> Data; }
[InlineArray(27), NonUserCode] internal struct Elements27<T> { private T _e; }
[NonUserCode] internal sealed class Leaf27<T> : Node<T> { public Elements27<T> Data; }
[InlineArray(28), NonUserCode] internal struct Elements28<T> { private T _e; }
[NonUserCode] internal sealed class Leaf28<T> : Node<T> { public Elements28<T> Data; }
[InlineArray(29), NonUserCode] internal struct Elements29<T> { private T _e; }
[NonUserCode] internal sealed class Leaf29<T> : Node<T> { public Elements29<T> Data; }
[InlineArray(30), NonUserCode] internal struct Elements30<T> { private T _e; }
[NonUserCode] internal sealed class Leaf30<T> : Node<T> { public Elements30<T> Data; }
[InlineArray(31), NonUserCode] internal struct Elements31<T> { private T _e; }
[NonUserCode] internal sealed class Leaf31<T> : Node<T> { public Elements31<T> Data; }
[InlineArray(32), NonUserCode] internal struct Elements32<T> { private T _e; }
[NonUserCode] internal sealed class Leaf32<T> : Node<T> { public Elements32<T> Data; }

[InlineArray(1), NonUserCode] internal struct Children1<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch1<T> : Node<T> { public T[] Data = null!; public Children1<T> Children; }
[InlineArray(2), NonUserCode] internal struct Children2<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch2<T> : Node<T> { public T[] Data = null!; public Children2<T> Children; }
[InlineArray(3), NonUserCode] internal struct Children3<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch3<T> : Node<T> { public T[] Data = null!; public Children3<T> Children; }
[InlineArray(4), NonUserCode] internal struct Children4<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch4<T> : Node<T> { public T[] Data = null!; public Children4<T> Children; }
[InlineArray(5), NonUserCode] internal struct Children5<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch5<T> : Node<T> { public T[] Data = null!; public Children5<T> Children; }
[InlineArray(6), NonUserCode] internal struct Children6<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch6<T> : Node<T> { public T[] Data = null!; public Children6<T> Children; }
[InlineArray(7), NonUserCode] internal struct Children7<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch7<T> : Node<T> { public T[] Data = null!; public Children7<T> Children; }
[InlineArray(8), NonUserCode] internal struct Children8<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch8<T> : Node<T> { public T[] Data = null!; public Children8<T> Children; }
[InlineArray(9), NonUserCode] internal struct Children9<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch9<T> : Node<T> { public T[] Data = null!; public Children9<T> Children; }
[InlineArray(10), NonUserCode] internal struct Children10<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch10<T> : Node<T> { public T[] Data = null!; public Children10<T> Children; }
[InlineArray(11), NonUserCode] internal struct Children11<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch11<T> : Node<T> { public T[] Data = null!; public Children11<T> Children; }
[InlineArray(12), NonUserCode] internal struct Children12<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch12<T> : Node<T> { public T[] Data = null!; public Children12<T> Children; }
[InlineArray(13), NonUserCode] internal struct Children13<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch13<T> : Node<T> { public T[] Data = null!; public Children13<T> Children; }
[InlineArray(14), NonUserCode] internal struct Children14<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch14<T> : Node<T> { public T[] Data = null!; public Children14<T> Children; }
[InlineArray(15), NonUserCode] internal struct Children15<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch15<T> : Node<T> { public T[] Data = null!; public Children15<T> Children; }
[InlineArray(16), NonUserCode] internal struct Children16<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch16<T> : Node<T> { public T[] Data = null!; public Children16<T> Children; }
[InlineArray(17), NonUserCode] internal struct Children17<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch17<T> : Node<T> { public T[] Data = null!; public Children17<T> Children; }
[InlineArray(18), NonUserCode] internal struct Children18<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch18<T> : Node<T> { public T[] Data = null!; public Children18<T> Children; }
[InlineArray(19), NonUserCode] internal struct Children19<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch19<T> : Node<T> { public T[] Data = null!; public Children19<T> Children; }
[InlineArray(20), NonUserCode] internal struct Children20<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch20<T> : Node<T> { public T[] Data = null!; public Children20<T> Children; }
[InlineArray(21), NonUserCode] internal struct Children21<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch21<T> : Node<T> { public T[] Data = null!; public Children21<T> Children; }
[InlineArray(22), NonUserCode] internal struct Children22<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch22<T> : Node<T> { public T[] Data = null!; public Children22<T> Children; }
[InlineArray(23), NonUserCode] internal struct Children23<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch23<T> : Node<T> { public T[] Data = null!; public Children23<T> Children; }
[InlineArray(24), NonUserCode] internal struct Children24<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch24<T> : Node<T> { public T[] Data = null!; public Children24<T> Children; }
[InlineArray(25), NonUserCode] internal struct Children25<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch25<T> : Node<T> { public T[] Data = null!; public Children25<T> Children; }
[InlineArray(26), NonUserCode] internal struct Children26<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch26<T> : Node<T> { public T[] Data = null!; public Children26<T> Children; }
[InlineArray(27), NonUserCode] internal struct Children27<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch27<T> : Node<T> { public T[] Data = null!; public Children27<T> Children; }
[InlineArray(28), NonUserCode] internal struct Children28<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch28<T> : Node<T> { public T[] Data = null!; public Children28<T> Children; }
[InlineArray(29), NonUserCode] internal struct Children29<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch29<T> : Node<T> { public T[] Data = null!; public Children29<T> Children; }
[InlineArray(30), NonUserCode] internal struct Children30<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch30<T> : Node<T> { public T[] Data = null!; public Children30<T> Children; }
[InlineArray(31), NonUserCode] internal struct Children31<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch31<T> : Node<T> { public T[] Data = null!; public Children31<T> Children; }
[InlineArray(32), NonUserCode] internal struct Children32<T> { private Node<T> _e; }
[StructLayout(LayoutKind.Sequential), NonUserCode]
internal sealed class Branch32<T> : Node<T> { public T[] Data = null!; public Children32<T> Children; }

[NonUserCode]
internal static class Alloc<T>
{
    // The empty leaf, the root of an empty set. It has size 0, so no slot of
    // it is ever read.
    public static readonly Node<T> Empty = new Leaf1<T>();

    public static Node<T> Leaf(int size, ulong meta, ulong map) => size switch
    {
        1 => new Leaf1<T> { Meta = meta, Map = map },
        2 => new Leaf2<T> { Meta = meta, Map = map },
        3 => new Leaf3<T> { Meta = meta, Map = map },
        4 => new Leaf4<T> { Meta = meta, Map = map },
        5 => new Leaf5<T> { Meta = meta, Map = map },
        6 => new Leaf6<T> { Meta = meta, Map = map },
        7 => new Leaf7<T> { Meta = meta, Map = map },
        8 => new Leaf8<T> { Meta = meta, Map = map },
        9 => new Leaf9<T> { Meta = meta, Map = map },
        10 => new Leaf10<T> { Meta = meta, Map = map },
        11 => new Leaf11<T> { Meta = meta, Map = map },
        12 => new Leaf12<T> { Meta = meta, Map = map },
        13 => new Leaf13<T> { Meta = meta, Map = map },
        14 => new Leaf14<T> { Meta = meta, Map = map },
        15 => new Leaf15<T> { Meta = meta, Map = map },
        16 => new Leaf16<T> { Meta = meta, Map = map },
        17 => new Leaf17<T> { Meta = meta, Map = map },
        18 => new Leaf18<T> { Meta = meta, Map = map },
        19 => new Leaf19<T> { Meta = meta, Map = map },
        20 => new Leaf20<T> { Meta = meta, Map = map },
        21 => new Leaf21<T> { Meta = meta, Map = map },
        22 => new Leaf22<T> { Meta = meta, Map = map },
        23 => new Leaf23<T> { Meta = meta, Map = map },
        24 => new Leaf24<T> { Meta = meta, Map = map },
        25 => new Leaf25<T> { Meta = meta, Map = map },
        26 => new Leaf26<T> { Meta = meta, Map = map },
        27 => new Leaf27<T> { Meta = meta, Map = map },
        28 => new Leaf28<T> { Meta = meta, Map = map },
        29 => new Leaf29<T> { Meta = meta, Map = map },
        30 => new Leaf30<T> { Meta = meta, Map = map },
        31 => new Leaf31<T> { Meta = meta, Map = map },
        32 => new Leaf32<T> { Meta = meta, Map = map },
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    };

    public static Node<T> Branch(int size, ulong meta, ulong map) => size switch
    {
        1 => new Branch1<T> { Meta = meta, Map = map },
        2 => new Branch2<T> { Meta = meta, Map = map },
        3 => new Branch3<T> { Meta = meta, Map = map },
        4 => new Branch4<T> { Meta = meta, Map = map },
        5 => new Branch5<T> { Meta = meta, Map = map },
        6 => new Branch6<T> { Meta = meta, Map = map },
        7 => new Branch7<T> { Meta = meta, Map = map },
        8 => new Branch8<T> { Meta = meta, Map = map },
        9 => new Branch9<T> { Meta = meta, Map = map },
        10 => new Branch10<T> { Meta = meta, Map = map },
        11 => new Branch11<T> { Meta = meta, Map = map },
        12 => new Branch12<T> { Meta = meta, Map = map },
        13 => new Branch13<T> { Meta = meta, Map = map },
        14 => new Branch14<T> { Meta = meta, Map = map },
        15 => new Branch15<T> { Meta = meta, Map = map },
        16 => new Branch16<T> { Meta = meta, Map = map },
        17 => new Branch17<T> { Meta = meta, Map = map },
        18 => new Branch18<T> { Meta = meta, Map = map },
        19 => new Branch19<T> { Meta = meta, Map = map },
        20 => new Branch20<T> { Meta = meta, Map = map },
        21 => new Branch21<T> { Meta = meta, Map = map },
        22 => new Branch22<T> { Meta = meta, Map = map },
        23 => new Branch23<T> { Meta = meta, Map = map },
        24 => new Branch24<T> { Meta = meta, Map = map },
        25 => new Branch25<T> { Meta = meta, Map = map },
        26 => new Branch26<T> { Meta = meta, Map = map },
        27 => new Branch27<T> { Meta = meta, Map = map },
        28 => new Branch28<T> { Meta = meta, Map = map },
        29 => new Branch29<T> { Meta = meta, Map = map },
        30 => new Branch30<T> { Meta = meta, Map = map },
        31 => new Branch31<T> { Meta = meta, Map = map },
        32 => new Branch32<T> { Meta = meta, Map = map },
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    };
}
