/*
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 *
 * Copyright (c) 2025 Linus Björnstam
 *
 * Portions of this code are based on a port of c-rrb (https://github.com/hypirion/c-rrb),
 * Copyright (c) 2013-2014 Jean Niklas L'orange, licensed under the MIT License.
 */

using System.Runtime.InteropServices;

namespace Bjolang.Runtime.Rrb;

[NonUserCode]
internal static class Constants
{
    public const int RRB_BITS = 5;
    public const int RRB_BRANCHING = 1 << RRB_BITS; // 32 for RRB_BITS = 5
    public const int RRB_MASK = RRB_BRANCHING - 1;

    // RRB_INVARIANT. Any value higher than zero means we allow nodes with fewer than 32
    // values when concatenating. This can lead to faster concatenation, but a loss of 
    // lookup performance and higher memory usage.
    public const int RRB_INVARIANT = 1;

    // We allow 2 incomplete nodes after a merge to prevent situations where we shift the whole tree. 
    // We should try to see what real world usage leads to and tweak this accordingly. Setting this to 0 means you
    // will hainterlocked.incrementve a mostly dense tree with fast lookups, at the cost of slower merges.
    public const int RRB_EXTRAS = 2;

    // We are limited to 2 billion elements by .net anyway, so this doesn't limit us at all.
    public const int RRB_MAX_HEIGHT = 10;
}

[StructLayout(LayoutKind.Auto, Pack = 1)]
[NonUserCode]
internal readonly struct OwnerId(uint id, ushort gen) : IEquatable<OwnerId>
{

    private const int BatchSize = 100;

    // The max of allocated IDs globally.
    // Starts at 0, so the first batch reserves IDs 1 to 100.
    private static long _globalHighWaterMark = 0;


    // These fields are unique to each thread. They initialize to 0/default.
    // The current ID value this thread is handing out.
    [ThreadStatic] 
    private static long _localCurrentId;

    // How many IDs are left in this thread's current batch.
    [ThreadStatic] 
    private static int _localRemaining;

    // ---------------------------------------------------------
    // Instance Data (6 Bytes)
    // ---------------------------------------------------------
    private readonly uint Id = id;   // 4 bytes
    private readonly ushort Gen = gen; // 2 bytes

    /// <summary>
    /// Generates the next unique OwnerId. 
    /// mostly non-blocking (thread-local), hits Interlocked only once per 100 IDs.
    /// </summary>
    public static OwnerId Next()
    {
        // We have IDs remaining in our local batch.
        // This executes with zero locking overhead.
        if (_localRemaining > 0)
        {
            _localRemaining--;
            long val = ++_localCurrentId;
            return new OwnerId((uint)val, (ushort)(val >> 32));
        }

        // SLOW PATH: We ran out (or this is the thread's first call).
        return NextBatch();
    }

    private static OwnerId NextBatch()
    {
        // Atomically reserve a new block of IDs from the global counter.
        // Only one thread contends for this cache line at a time.
        long reservedEnd = Interlocked.Add(ref _globalHighWaterMark, BatchSize);
        
        // Calculate the start of our new range.
        long reservedStart = reservedEnd - BatchSize + 1;

        // Reset the local cache.
        // We set _localCurrentId to (start - 1) so that the first increment 
        // inside the logic below lands exactly on 'reservedStart'.
        _localCurrentId = reservedStart - 1;
        _localRemaining = BatchSize;

        // Perform the generation logic (same as Fast Path)
        _localRemaining--;
        long val = ++_localCurrentId;
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