/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 *
 * As a special exception to the Mozilla Public License, version 2.0, if you
 * compile your application source code and portions of this software are
 * embedded into the generated object code or executable form as a normal
 * consequence of the compilation process (such as inline functions,
 * templates, generics, or macros), you may redistribute such embedded portions
 * in such object code or executable form without complying with the source code
 * availability requirements or notice obligations of Section 3 of the MPL 2.0.
 */

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bjolang.Runtime;

/// <summary>
///     A persistent map from parameter id to value: the parameters a
///     <c>DynEnv</c> binds, other than the ports and the cancel token.
///
///     - A 32-way bitmap trie keyed by the id itself, low five bits first.
///       Ids never collide, so there is no hashing, no comparer and no
///       collision node.
///     - A node keeps entries inline (<see cref="_dataMap" />) and children
///       (<see cref="_nodeMap" />), in one array. Two ids that share a slot move into a
///       child, one level per five shared bits.
///     - <c>ParamIds</c> places hot ids in root slots 0..30, so reading one
///       is one node access however many parameters are bound. Cold ids all
///       hang under root slot 31.
///     - Immutable: <see cref="With" /> copies the path to the entry and
///       shares the rest. There is no removal; a restore replaces the whole
///       environment.
/// </summary>
[NonUserCode]
internal sealed class ParamTrie
{
    /// An inline binding, or a child in <see cref="Value" /> when the slot is
    /// one of the node's children. <see cref="Key" /> is unused for a child.
    internal struct Entry
    {
        public int Key;
        public object Value;
    }

    internal static readonly ParamTrie Empty = new(0, 0, []);

    private readonly uint _dataMap;
    private readonly uint _nodeMap;

    /// The node's entries in slot order, then its children in slot order.
    private readonly Entry[] _slots;

    private ParamTrie(uint dataMap, uint nodeMap, Entry[] slots)
    {
        _dataMap = dataMap;
        _nodeMap = nodeMap;
        _slots = slots;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Index(uint map, uint bit) => BitOperations.PopCount(map & (bit - 1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Bit(int id, int shift) => 1u << (int)(((uint)id >> shift) & 31);

    private int EntryCount => BitOperations.PopCount(_dataMap);

    /// Returns whether <paramref name="id" /> is bound, and its value in
    /// <paramref name="value" /> if so.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetValue(int id, out object value)
    {
        var node = this;
        var shift = 0;
        while (true)
        {
            var bit = Bit(id, shift);
            if ((node._dataMap & bit) != 0)
            {
                ref var e = ref node._slots[Index(node._dataMap, bit)];
                value = e.Value;
                return e.Key == id;
            }

            if ((node._nodeMap & bit) == 0)
            {
                value = null!;
                return false;
            }

            node = Unsafe.As<ParamTrie>(node._slots[node.EntryCount + Index(node._nodeMap, bit)].Value);
            shift += 5;
        }
    }

    /// Returns a trie that binds <paramref name="id" /> to
    /// <paramref name="value" />, replacing any earlier binding of it.
    internal ParamTrie With(int id, object value) => With(id, value, 0);

    private ParamTrie With(int id, object value, int shift)
    {
        var bit = Bit(id, shift);
        var slots = _slots;
        if ((_dataMap & bit) != 0)
        {
            var i = Index(_dataMap, bit);
            var old = slots[i];
            if (old.Key == id)
            {
                var copy = Copy(slots);
                copy[i].Value = value;
                return new ParamTrie(_dataMap, _nodeMap, copy);
            }

            // The slot holds another id: both move into a new child, which
            // goes from the entries to the children.
            var child = Pair(old, new Entry { Key = id, Value = value }, shift + 5);
            var j = EntryCount - 1 + Index(_nodeMap, bit);
            var r = new Entry[slots.Length];
            Array.Copy(slots, r, i);
            Array.Copy(slots, i + 1, r, i, j - i);
            r[j].Value = child;
            Array.Copy(slots, j + 1, r, j + 1, slots.Length - j - 1);
            return new ParamTrie(_dataMap & ~bit, _nodeMap | bit, r);
        }

        if ((_nodeMap & bit) != 0)
        {
            var k = EntryCount + Index(_nodeMap, bit);
            var copy = Copy(slots);
            copy[k].Value = Unsafe.As<ParamTrie>(slots[k].Value).With(id, value, shift + 5);
            return new ParamTrie(_dataMap, _nodeMap, copy);
        }

        var at = Index(_dataMap, bit);
        var grown = new Entry[slots.Length + 1];
        Array.Copy(slots, grown, at);
        grown[at] = new Entry { Key = id, Value = value };
        Array.Copy(slots, at, grown, at + 1, slots.Length - at);
        return new ParamTrie(_dataMap | bit, _nodeMap, grown);
    }

    /// A node holding two distinct ids that agree below <paramref name="shift" />.
    private static ParamTrie Pair(Entry a, Entry b, int shift)
    {
        var ba = Bit(a.Key, shift);
        var bb = Bit(b.Key, shift);
        if (ba == bb)
            return new ParamTrie(0, ba, [new Entry { Value = Pair(a, b, shift + 5) }]);
        return ba < bb
            ? new ParamTrie(ba | bb, 0, [a, b])
            : new ParamTrie(ba | bb, 0, [b, a]);
    }

    private static Entry[] Copy(Entry[] xs)
    {
        var r = new Entry[xs.Length];
        xs.AsSpan().CopyTo(r);
        return r;
    }
}
