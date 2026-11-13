using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bjolang.Runtime.OrderedTrees.SetNodes;

[Flags]
public enum NodeFlags : byte
{
    None = 0,
    IsLeaf = 1 << 0,
    IsRoot = 1 << 1
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[NonUserCode]
public struct NodeHeader
{
    // 6 Bytes: OwnerId for Copy-on-Write (CoW)
    public OwnerId Owner;

    // 1 Byte: Number of items currently used
    public byte Count;

    // 1 Byte: Type flags (Leaf, Root, etc.)
    public NodeFlags Flags;

    public NodeHeader(OwnerId owner, byte count, NodeFlags flags)
    {
        Owner = owner;
        Count = count;
        Flags = flags;
    }
}

[InlineArray(32)]
[NonUserCode]
public struct KeyBuffer<T>
{
    private T _element0;
}

// Constraint: Internal Nodes fixed at 32 children.
// This removes the need for a separate array allocation for children references.
[InlineArray(32)]
[NonUserCode]
public struct NodeBuffer<T>
{
    private Node<T>? _element0;
}

[NonUserCode]
public abstract class Node<T>
{
    public NodeHeader Header;

    protected Node(OwnerId owner, NodeFlags flags)
    {
        Header = new NodeHeader(owner, 0, flags);
    }

    public abstract Span<T> GetKeys(); 
    
    public bool IsLeaf => (Header.Flags & NodeFlags.IsLeaf) != 0;

    public abstract Node<T> EnsureEditable(OwnerId transactionId);
        
    public void SetCount(int newCount) => Header.Count = (byte)newCount;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LeafNode<T> AsLeaf()
    {
        // Zero-overhead cast. Assumes you checked IsLeaf or know logic flow.
        return Unsafe.As<LeafNode<T>>(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public InternalNode<T> AsInternal()
    {
        // Zero-overhead cast. Assumes you checked !IsLeaf or know logic flow.
        return Unsafe.As<InternalNode<T>>(this);
    }
}

[NonUserCode]
public sealed class LeafNode<T> : Node<T>
{
    public const int Capacity = 64;
    public const int MergeThreshold = 8;
    
    public T[]? Keys;

    public LeafNode(OwnerId owner) : base(owner, NodeFlags.IsLeaf)
    {
        Keys = new T[Capacity];
    }

    // Copy Constructor for CoW
    private LeafNode(LeafNode<T> original, OwnerId newOwner)
        : base(newOwner, original.Header.Flags)
    {
        Keys = new T[Capacity];
        Header.Count = original.Header.Count;

        // Copy data
        Array.Copy(original.Keys!, Keys, original.Header.Count);
    }

    public override Node<T> EnsureEditable(OwnerId transactionId)
    {
        // CASE 1: Persistent Mode (transactionId is None).
        // We MUST create a copy, because we cannot distinguish "Shared Immutable Node (0)"
        // from "New Mutable Node (0)" based on ID alone.
        if (transactionId == OwnerId.None)
        {
            return new LeafNode<T>(this, OwnerId.None);
        }

        // CASE 2: Transient Mode.
        // If we own the node, return it.
        if (Header.Owner == transactionId)
        {
            return this;
        }

        // CASE 3: CoW needed (Ownership mismatch).
        return new LeafNode<T>(this, transactionId);
    }

    public override Span<T> GetKeys()
    {
        return Keys.AsSpan(0, Header.Count);
    }
}

[NonUserCode]
public class InternalNode<T> : Node<T>
{
    public const int Capacity = 32;

    public KeyBuffer<T> Keys;
    public NodeBuffer<T> Children;

    public InternalNode(OwnerId owner, NodeFlags flags = NodeFlags.None) 
        : base(owner, flags)
    {
    }

    // Fixed CoW Constructor
    protected InternalNode(InternalNode<T> original, OwnerId newOwner, NodeFlags flags)
        : base(newOwner, flags)
    {
        Header.Count = original.Header.Count;
        
        // Fast struct blit for both Keys and Children.
        // No loop required for InlineArrays!
        this.Keys = original.Keys;
        this.Children = original.Children;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<Node<T>> GetChildren() 
    {
        // An internal node always has (Count + 1) children
        return MemoryMarshal.CreateSpan(ref Children[0]!, Header.Count + 1);
    }

    public override Span<T> GetKeys() => MemoryMarshal.CreateSpan(ref Keys[0], Header.Count);

    public override Node<T> EnsureEditable(OwnerId transactionId)
    {
        if (transactionId == OwnerId.None) return new InternalNode<T>(this, OwnerId.None, Header.Flags);
        if (Header.Owner == transactionId) return this;
        return new InternalNode<T>(this, transactionId, Header.Flags);
    }
}

[StructLayout(LayoutKind.Auto, Pack = 1)]
[NonUserCode]
public readonly struct OwnerId(uint id, ushort gen) : IEquatable<OwnerId>
{
    private const int BatchSize = 100;

    // The max of allocated IDs globally.
    // Starts at 0, so the first batch reserves IDs 1 to 100.
    private static long _globalHighWaterMark;


    // These fields are unique to each thread. They initialize to 0/default.
    // The current ID value this thread is handing out.
    [ThreadStatic] private static long _localCurrentId;

    // How many IDs are left in this thread's current batch.
    [ThreadStatic] private static int _localRemaining;

    // ---------------------------------------------------------
    // Instance Data (6 Bytes)
    // ---------------------------------------------------------
    private readonly uint Id = id; // 4 bytes
    private readonly ushort Gen = gen; // 2 bytes

    /// <summary>
    ///     Generates the next unique OwnerId.
    ///     mostly non-blocking (thread-local), hits Interlocked only once per 100 IDs.
    /// </summary>
    public static OwnerId Next()
    {
        // We have IDs remaining in our local batch.
        // This executes with zero locking overhead.
        if (_localRemaining > 0)
        {
            _localRemaining--;
            var val = ++_localCurrentId;
            return new OwnerId((uint)val, (ushort)(val >> 32));
        }

        // SLOW PATH: We ran out (or this is the thread's first call).
        return NextBatch();
    }

    private static OwnerId NextBatch()
    {
        // Atomically reserve a new block of IDs from the global counter.
        // Only one thread contends for this cache line at a time.
        var reservedEnd = Interlocked.Add(ref _globalHighWaterMark, BatchSize);

        // Calculate the start of our new range.
        var reservedStart = reservedEnd - BatchSize + 1;

        // Reset the local cache.
        // We set _localCurrentId to (start - 1) so that the first increment
        // inside the logic below lands exactly on 'reservedStart'.
        _localCurrentId = reservedStart - 1;
        _localRemaining = BatchSize;

        // Perform the generation logic (same as Fast Path)
        _localRemaining--;
        var val = ++_localCurrentId;
        return new OwnerId((uint)val, (ushort)(val >> 32));
    }

    public static readonly OwnerId None = new(0, 0);

    public bool IsNone => Id == 0 && Gen == 0;

    public bool Equals(OwnerId other)
    {
        return Id == other.Id && Gen == other.Gen;
    }

    public override bool Equals(object? obj)
    {
        return obj is OwnerId other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Id, Gen);
    }

    public static bool operator ==(OwnerId left, OwnerId right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(OwnerId left, OwnerId right)
    {
        return !left.Equals(right);
    }
}
