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

namespace Bjolang.Runtime.TextTree;

// The nodes of (std text)'s B+ tree. lib/std/text.bjo holds every operation,
// including those on one node; this file only declares the nodes and gives
// Bjolang access to their inline arrays, which it cannot declare.
//
// - A node has up to `Capacity` children and keeps, for each, its length in
//   bytes and its number of newlines.
// - A `TextLeaves` node's children are chunks: strings of valid UTF-8 that
//   start and end on character boundaries. A `TextBranch` node's children are
//   nodes, all of the same kind and height.
// - `Owner` is the transient that may edit the node in place, 0 for none.
// - Nothing checks a slot against `Count`. A slot past `Capacity` raises
//   `IndexOutOfRangeException`.

[InlineArray(TextNode.Capacity)]
[NonUserCode]
public struct TextInts
{
    private int _element0;
}

[InlineArray(TextNode.Capacity)]
[NonUserCode]
public struct TextKids
{
    private TextNode? _element0;
}

[InlineArray(TextNode.Capacity)]
[NonUserCode]
public struct TextChunks
{
    private Utf8String _element0;
}

[NonUserCode]
public abstract class TextNode
{
    public const int Capacity = 16;

    public long Owner;
    public int Count;
    public TextInts Bytes;
    public TextInts Lines;

    protected TextNode(long owner) => Owner = owner;

    protected TextNode(TextNode original, long owner)
    {
        Owner = owner;
        Count = original.Count;
        Bytes = original.Bytes;
        Lines = original.Lines;
    }
}

[NonUserCode]
public sealed class TextBranch : TextNode
{
    public TextKids Kids;

    public TextBranch(long owner) : base(owner) { }

    public TextBranch(TextBranch original, long owner) : base(original, owner) => Kids = original.Kids;
}

[NonUserCode]
public sealed class TextLeaves : TextNode
{
    public TextChunks Chunks;

    public TextLeaves(long owner) : base(owner) { }

    public TextLeaves(TextLeaves original, long owner) : base(original, owner) => Chunks = original.Chunks;
}

/// <summary>Accessors of the text nodes, for Bjolang.</summary>
[NonUserCode]
public static class TextNodes
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextBranch NewBranch(long owner) => new(owner);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextLeaves NewLeaves(long owner) => new(owner);

    /// A copy of every slot, owned by <paramref name="owner"/>.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextBranch CopyBranch(TextBranch node, long owner) => new(node, owner);

    /// A copy of every slot, owned by <paramref name="owner"/>.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextLeaves CopyLeaves(TextLeaves node, long owner) => new(node, owner);

    /// A node known to be a branch, as one. No check.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextBranch AsBranch(TextNode node) => Unsafe.As<TextBranch>(node);

    /// A node known to be a leaves node, as one. No check.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextLeaves AsLeaves(TextNode node) => Unsafe.As<TextLeaves>(node);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextNode BranchNode(TextBranch node) => node;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextNode LeavesNode(TextLeaves node) => node;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Owner(TextNode node) => node.Owner;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count(TextNode node) => node.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetCount(TextNode node, int count) => node.Count = count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Bytes(TextNode node, int i) => node.Bytes[i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetBytes(TextNode node, int i, int bytes) => node.Bytes[i] = bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Lines(TextNode node, int i) => node.Lines[i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetLines(TextNode node, int i, int lines) => node.Lines[i] = lines;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TextNode Kid(TextBranch node, int i) => node.Kids[i]!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetKid(TextBranch node, int i, TextNode kid) => node.Kids[i] = kid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Utf8String Chunk(TextLeaves node, int i) => node.Chunks[i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetChunk(TextLeaves node, int i, Utf8String chunk) => node.Chunks[i] = chunk;
}
