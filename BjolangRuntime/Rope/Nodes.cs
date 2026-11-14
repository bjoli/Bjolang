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

using System.Runtime.CompilerServices;
using BjoString;

namespace Bjolang.Runtime.Rope;

// The nodes of (std rope)'s B+ tree. lib/std/rope.bjo holds every operation,
// including those on one node; this file only declares the nodes and gives
// Bjolang access to their inline arrays, which it cannot declare.
//
// - A node has up to `Capacity` children and keeps, for each, its length in
//   bytes and its number of newlines.
// - A `RopeLeaves` node's children are chunks: strings of valid UTF-8 that
//   start and end on character boundaries. A `RopeBranch` node's children are
//   nodes, all of the same kind and height.
// - `Owner` is the transient that may edit the node in place, 0 for none.
// - Nothing checks a slot against `Count`. A slot past `Capacity` raises
//   `IndexOutOfRangeException`.

[InlineArray(RopeNode.Capacity)]
[NonUserCode]
public struct RopeInts
{
    private int _element0;
}

[InlineArray(RopeNode.Capacity)]
[NonUserCode]
public struct RopeKids
{
    private RopeNode? _element0;
}

[InlineArray(RopeNode.Capacity)]
[NonUserCode]
public struct RopeChunks
{
    private Utf8String _element0;
}

[NonUserCode]
public abstract class RopeNode
{
    public const int Capacity = 16;

    public long Owner;
    public int Count;
    public RopeInts Bytes;
    public RopeInts Lines;

    protected RopeNode(long owner) => Owner = owner;

    protected RopeNode(RopeNode original, long owner)
    {
        Owner = owner;
        Count = original.Count;
        Bytes = original.Bytes;
        Lines = original.Lines;
    }
}

[NonUserCode]
public sealed class RopeBranch : RopeNode
{
    public RopeKids Kids;

    public RopeBranch(long owner) : base(owner) { }

    public RopeBranch(RopeBranch original, long owner) : base(original, owner) => Kids = original.Kids;
}

[NonUserCode]
public sealed class RopeLeaves : RopeNode
{
    public RopeChunks Chunks;

    public RopeLeaves(long owner) : base(owner) { }

    public RopeLeaves(RopeLeaves original, long owner) : base(original, owner) => Chunks = original.Chunks;
}

/// <summary>Accessors of the rope nodes, for Bjolang.</summary>
[NonUserCode]
public static class RopeNodes
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RopeBranch NewBranch(long owner) => new(owner);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RopeLeaves NewLeaves(long owner) => new(owner);

    /// A copy of every slot, owned by <paramref name="owner"/>.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RopeBranch CopyBranch(RopeBranch node, long owner) => new(node, owner);

    /// A copy of every slot, owned by <paramref name="owner"/>.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RopeLeaves CopyLeaves(RopeLeaves node, long owner) => new(node, owner);

    /// A node known to be a branch, as one. No check.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RopeBranch AsBranch(RopeNode node) => Unsafe.As<RopeBranch>(node);

    /// A node known to be a leaves node, as one. No check.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RopeLeaves AsLeaves(RopeNode node) => Unsafe.As<RopeLeaves>(node);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RopeNode BranchNode(RopeBranch node) => node;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RopeNode LeavesNode(RopeLeaves node) => node;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Owner(RopeNode node) => node.Owner;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count(RopeNode node) => node.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetCount(RopeNode node, int count) => node.Count = count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Bytes(RopeNode node, int i) => node.Bytes[i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetBytes(RopeNode node, int i, int bytes) => node.Bytes[i] = bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Lines(RopeNode node, int i) => node.Lines[i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLines(RopeNode node, int i, int lines) => node.Lines[i] = lines;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RopeNode Kid(RopeBranch node, int i) => node.Kids[i]!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetKid(RopeBranch node, int i, RopeNode kid) => node.Kids[i] = kid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Utf8String Chunk(RopeLeaves node, int i) => node.Chunks[i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetChunk(RopeLeaves node, int i, Utf8String chunk) => node.Chunks[i] = chunk;
}
