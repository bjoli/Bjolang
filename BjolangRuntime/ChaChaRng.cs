/* Copyright (c) 2026 Linus Björnstam <linus.internet@fastmail.se>
 *
 * Permission to use, copy, modify, and/or distribute this software for any
 * purpose with or without fee is hereby granted, provided that the above
 * copyright notice and this permission notice appear in all copies.
 *
 * THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
 * WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
 * ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
 * WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
 * ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
 * OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
 */

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Utf8String = BjoString.Utf8String;

namespace Bjolang.Runtime;

[InlineArray(16)]
[NonUserCode]
internal struct StateArray
{
    private uint _element0;
}

/// <summary>
/// Eight blocks. The wide path computes this many at one time.
/// </summary>
///
/// <remarks>
/// Only the wide path fills all of the buffer. The narrow paths fill one block
/// and set <c>_bufferEnd</c> to match, so a machine without AVX2 does the same
/// work per draw as with a one-block buffer.
/// </remarks>
[InlineArray(512)]
[NonUserCode]
internal struct BufferArray
{
    private byte _element0;
}

/// <summary>
/// A fast pseudo-random number generator (PRNG) that can split, based on the
/// ChaCha20 stream cipher.
/// </summary>
///
/// <remarks>
/// A class and not a struct, so that the state is not copied by accident when
/// it is passed by value. The 16-word state and the buffer are inline arrays,
/// so they are stored in the object and need no heap allocations of their own.
/// </remarks>
[NonUserCode]
public class ChaChaRng
{
    internal StateArray _state;
    internal BufferArray _buffer;

    /// Where in the buffer the next draw starts.
    internal int _bufferIndex;

    /// How many bytes of the buffer are valid. The buffer holds eight blocks,
    /// but only the wide path fills all of it, so the limit is kept here and
    /// is not the length of the buffer.
    internal int _bufferEnd;

    /// Eight blocks at one time need AVX2 for the transpose, and stores of
    /// whole registers need little-endian. Both are JIT constants.
    private static bool EightWayAvailable => Avx2.IsSupported && BitConverter.IsLittleEndian;

    private ulong _boolCache;
    private int _boolCacheBitsLeft;

    public ChaChaRng(uint[] seed256)
    {
        if (seed256 == null || seed256.Length != 8)
            throw new ArgumentException("The seed must be exactly 8 uints (256 bits).");

        // Constants: "expand 32-byte k"
        _state[0] = 0x61707865;
        _state[1] = 0x3320646e;
        _state[2] = 0x79622d32;
        _state[3] = 0x6b206574;

        // Key
        for (int i = 0; i < 8; i++)
            _state[4 + i] = seed256[i];

        // Block counter (12, 13) and stream id (14, 15) start at 0.
        _state[12] = 0;
        _state[13] = 0;
        _state[14] = 0;
        _state[15] = 0;

        // An empty buffer makes the first draw generate.
        _bufferIndex = 0;
        _bufferEnd = 0;
        _boolCacheBitsLeft = 0;
    }

    public ChaChaRng(ulong seed64) : this(ExpandSeed(seed64)) { }

    /// <summary>
    /// Makes a generator with a seed from the OS CSPRNG.
    /// </summary>
    public ChaChaRng() : this(GenerateSecureSeed()) { }

    private static uint[] GenerateSecureSeed()
    {
        Span<byte> bytes = stackalloc byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        uint[] seed = new uint[8];
        for (int i = 0; i < 8; i++)
        {
            seed[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i * 4, 4));
        }
        return seed;
    }

    /// Expands a 64-bit seed to a 256-bit key with SplitMix64.
    private static uint[] ExpandSeed(ulong seed)
    {
        ulong x = seed;
        ulong NextSplitMix()
        {
            ulong z = (x += 0x9e3779b97f4a7c15UL);
            z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
            z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
            return z ^ (z >> 31);
        }

        uint[] expanded = new uint[8];
        for (int i = 0; i < 4; i++)
        {
            ulong next = NextSplitMix();
            expanded[i * 2] = (uint)next;
            expanded[i * 2 + 1] = (uint)(next >> 32);
        }
        return expanded;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b; d ^= a; d = BitOperations.RotateLeft(d, 16);
        c += d; b ^= c; b = BitOperations.RotateLeft(b, 12);
        a += b; d ^= a; d = BitOperations.RotateLeft(d, 8);
        c += d; b ^= c; b = BitOperations.RotateLeft(b, 7);
    }

    /// <summary>
    /// Generates one block, with vectors where the machine has them.
    /// </summary>
    ///
    /// <remarks>
    /// The two paths compute the same ChaCha20 and give the same stream: 20
    /// rounds, the same state, the same block counter. The choice is not
    /// visible from outside. The conditions are JIT constants, so the branch
    /// is removed at compile time.
    ///
    /// The vector path stores whole registers and thus needs little-endian. On
    /// a big-endian machine the scalar path writes word by word instead.
    /// </remarks>
    private void GenerateBlock(Span<byte> output)
    {
        if (Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            GenerateBlockVector(output);
        }
        else
        {
            GenerateBlockScalar(output);
        }
    }

    // The lane rotations that the diagonal round needs. `Shuffle` reads an
    // index per lane: result[i] = v[index[i]].
    private static readonly Vector128<uint> Lane1 = Vector128.Create(1u, 2u, 3u, 0u);
    private static readonly Vector128<uint> Lane2 = Vector128.Create(2u, 3u, 0u, 1u);
    private static readonly Vector128<uint> Lane3 = Vector128.Create(3u, 0u, 1u, 2u);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> RotateLeft(Vector128<uint> v, int bits)
    {
        return Vector128.ShiftLeft(v, bits) | Vector128.ShiftRightLogical(v, 32 - bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QuarterRound(
        ref Vector128<uint> a, ref Vector128<uint> b, ref Vector128<uint> c, ref Vector128<uint> d)
    {
        a += b; d ^= a; d = RotateLeft(d, 16);
        c += d; b ^= c; b = RotateLeft(b, 12);
        a += b; d ^= a; d = RotateLeft(d, 8);
        c += d; b ^= c; b = RotateLeft(b, 7);
    }

    /// <summary>
    /// The low and the high word of the block counter, for the eight blocks
    /// that are computed at one time.
    /// </summary>
    ///
    /// <remarks>
    /// The counter is a 64-bit value in words 12 and 13, and the eight lanes
    /// hold the counter plus zero to seven. Thus the high word can be
    /// different *between lanes*: if the counter is just below 2^32, four of
    /// them overflow and four do not. One broadcast word 13 would be correct
    /// in all other cases, and wrong in this one without a visible sign. For
    /// this reason both words are computed per lane.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> CounterLow(ulong counter)
    {
        return Vector256.Create(
            (uint)counter,       (uint)(counter + 1), (uint)(counter + 2), (uint)(counter + 3),
            (uint)(counter + 4), (uint)(counter + 5), (uint)(counter + 6), (uint)(counter + 7));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> CounterHigh(ulong counter)
    {
        return Vector256.Create(
            (uint)(counter >> 32),       (uint)((counter + 1) >> 32),
            (uint)((counter + 2) >> 32), (uint)((counter + 3) >> 32),
            (uint)((counter + 4) >> 32), (uint)((counter + 5) >> 32),
            (uint)((counter + 6) >> 32), (uint)((counter + 7) >> 32));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> RotateLeft(Vector256<uint> v, int bits)
    {
        return Vector256.ShiftLeft(v, bits) | Vector256.ShiftRightLogical(v, 32 - bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QuarterRound(
        ref Vector256<uint> a, ref Vector256<uint> b, ref Vector256<uint> c, ref Vector256<uint> d)
    {
        a += b; d ^= a; d = RotateLeft(d, 16);
        c += d; b ^= c; b = RotateLeft(b, 12);
        a += b; d ^= a; d = RotateLeft(d, 8);
        c += d; b ^= c; b = RotateLeft(b, 7);
    }

    /// <summary>
    /// One block in four registers: one row of the state in each.
    /// </summary>
    ///
    /// <remarks>
    /// With the rows in registers, one quarter round computes all four
    /// columns at one time, one lane per column. The diagonal round is the
    /// same quarter round with the lanes rotated (row 1 by one step, row 2 by
    /// two, row 3 by three), so that each diagonal is where its column was.
    /// After the round they are rotated back.
    ///
    /// Four additions and four stores complete the block. No transpose is
    /// necessary, because the rows are already in output order.
    /// </remarks>
    internal void GenerateBlockVector(Span<byte> output)
    {
        Span<uint> state = _state;
        ref uint s = ref MemoryMarshal.GetReference(state);

        var a = Vector128.LoadUnsafe(ref s);
        var b = Vector128.LoadUnsafe(ref s, 4);
        var c = Vector128.LoadUnsafe(ref s, 8);
        var d = Vector128.LoadUnsafe(ref s, 12);

        var a0 = a;
        var b0 = b;
        var c0 = c;
        var d0 = d;

        for (int i = 0; i < 10; i++)
        {
            QuarterRound(ref a, ref b, ref c, ref d);

            b = Vector128.Shuffle(b, Lane1);
            c = Vector128.Shuffle(c, Lane2);
            d = Vector128.Shuffle(d, Lane3);

            QuarterRound(ref a, ref b, ref c, ref d);

            b = Vector128.Shuffle(b, Lane3);
            c = Vector128.Shuffle(c, Lane2);
            d = Vector128.Shuffle(d, Lane1);
        }

        a += a0;
        b += b0;
        c += c0;
        d += d0;

        ref byte o = ref MemoryMarshal.GetReference(output);
        a.AsByte().StoreUnsafe(ref o);
        b.AsByte().StoreUnsafe(ref o, 16);
        c.AsByte().StoreUnsafe(ref o, 32);
        d.AsByte().StoreUnsafe(ref o, 48);

        // Increment the block counter (a 64-bit value in words 12 and 13).
        if (++_state[12] == 0)
        {
            _state[13]++;
        }
    }

    /// <summary>
    /// Four words of eight blocks, put so that each block has its four words
    /// in sequence.
    /// </summary>
    ///
    /// <remarks>
    /// Eight unpacks are sufficient: the first two interleave words in pairs,
    /// the last two do the same at 64-bit width. The result is one register
    /// per group of four words. The lower half is from one block and the
    /// upper half is from the block four steps after it, which is the layout
    /// that the stores in <see cref="GenerateBlocks8"/> use.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4(
        Vector256<uint> a, Vector256<uint> b, Vector256<uint> c, Vector256<uint> d,
        out Vector256<uint> r0, out Vector256<uint> r1, out Vector256<uint> r2, out Vector256<uint> r3)
    {
        var t0 = Avx2.UnpackLow(a, b);
        var t1 = Avx2.UnpackHigh(a, b);
        var t2 = Avx2.UnpackLow(c, d);
        var t3 = Avx2.UnpackHigh(c, d);

        r0 = Avx2.UnpackLow(t0.AsUInt64(), t2.AsUInt64()).AsUInt32();
        r1 = Avx2.UnpackHigh(t0.AsUInt64(), t2.AsUInt64()).AsUInt32();
        r2 = Avx2.UnpackLow(t1.AsUInt64(), t3.AsUInt64()).AsUInt32();
        r3 = Avx2.UnpackHigh(t1.AsUInt64(), t3.AsUInt64()).AsUInt32();
    }

    /// <summary>
    /// Eight blocks at one time, transposed: register i holds word i of eight
    /// different blocks.
    /// </summary>
    ///
    /// <remarks>
    /// In this layout the diagonal round needs no lane rotations. It is the
    /// same quarter round on different registers. The cost is the transpose
    /// at the end, which is approximately four percent.
    ///
    /// There are sixteen named locals, and the start values are read again
    /// from the state at the end instead of being kept. There are sixteen ymm
    /// registers, and two sets would spill all of them to the stack.
    ///
    /// Writes 512 bytes and moves the block counter eight steps. Eight
    /// consecutive blocks are the same stream as eight single blocks, so the
    /// path is not visible in the output.
    /// </remarks>
    internal void GenerateBlocks8(Span<byte> output)
    {
        Span<uint> state = _state;

        // Scalar, so that the two counter vectors can be made again at the
        // end instead of being kept in registers.
        ulong counter = (((ulong)state[13]) << 32) | state[12];

        Vector256<uint> v0  = Vector256.Create(state[0]),  v1  = Vector256.Create(state[1]),
                        v2  = Vector256.Create(state[2]),  v3  = Vector256.Create(state[3]),
                        v4  = Vector256.Create(state[4]),  v5  = Vector256.Create(state[5]),
                        v6  = Vector256.Create(state[6]),  v7  = Vector256.Create(state[7]),
                        v8  = Vector256.Create(state[8]),  v9  = Vector256.Create(state[9]),
                        v10 = Vector256.Create(state[10]), v11 = Vector256.Create(state[11]),
                        v12 = CounterLow(counter),
                        v13 = CounterHigh(counter),
                        v14 = Vector256.Create(state[14]), v15 = Vector256.Create(state[15]);

        for (int i = 0; i < 10; i++)
        {
            QuarterRound(ref v0, ref v4, ref v8,  ref v12);
            QuarterRound(ref v1, ref v5, ref v9,  ref v13);
            QuarterRound(ref v2, ref v6, ref v10, ref v14);
            QuarterRound(ref v3, ref v7, ref v11, ref v15);

            QuarterRound(ref v0, ref v5, ref v10, ref v15);
            QuarterRound(ref v1, ref v6, ref v11, ref v12);
            QuarterRound(ref v2, ref v7, ref v8,  ref v13);
            QuarterRound(ref v3, ref v4, ref v9,  ref v14);
        }

        v0  += Vector256.Create(state[0]);  v1  += Vector256.Create(state[1]);
        v2  += Vector256.Create(state[2]);  v3  += Vector256.Create(state[3]);
        v4  += Vector256.Create(state[4]);  v5  += Vector256.Create(state[5]);
        v6  += Vector256.Create(state[6]);  v7  += Vector256.Create(state[7]);
        v8  += Vector256.Create(state[8]);  v9  += Vector256.Create(state[9]);
        v10 += Vector256.Create(state[10]); v11 += Vector256.Create(state[11]);
        v12 += CounterLow(counter);
        v13 += CounterHigh(counter);
        v14 += Vector256.Create(state[14]); v15 += Vector256.Create(state[15]);

        Transpose4(v0,  v1,  v2,  v3,  out var a0, out var a1, out var a2, out var a3);
        Transpose4(v4,  v5,  v6,  v7,  out var b0, out var b1, out var b2, out var b3);
        Transpose4(v8,  v9,  v10, v11, out var c0, out var c1, out var c2, out var c3);
        Transpose4(v12, v13, v14, v15, out var d0, out var d1, out var d2, out var d3);

        // The lower halves are blocks 0..3, the upper halves blocks 4..7.
        ref byte o = ref MemoryMarshal.GetReference(output);
        Vector256.Create(a0.GetLower(), b0.GetLower()).AsByte().StoreUnsafe(ref o, 0);
        Vector256.Create(c0.GetLower(), d0.GetLower()).AsByte().StoreUnsafe(ref o, 32);
        Vector256.Create(a1.GetLower(), b1.GetLower()).AsByte().StoreUnsafe(ref o, 64);
        Vector256.Create(c1.GetLower(), d1.GetLower()).AsByte().StoreUnsafe(ref o, 96);
        Vector256.Create(a2.GetLower(), b2.GetLower()).AsByte().StoreUnsafe(ref o, 128);
        Vector256.Create(c2.GetLower(), d2.GetLower()).AsByte().StoreUnsafe(ref o, 160);
        Vector256.Create(a3.GetLower(), b3.GetLower()).AsByte().StoreUnsafe(ref o, 192);
        Vector256.Create(c3.GetLower(), d3.GetLower()).AsByte().StoreUnsafe(ref o, 224);
        Vector256.Create(a0.GetUpper(), b0.GetUpper()).AsByte().StoreUnsafe(ref o, 256);
        Vector256.Create(c0.GetUpper(), d0.GetUpper()).AsByte().StoreUnsafe(ref o, 288);
        Vector256.Create(a1.GetUpper(), b1.GetUpper()).AsByte().StoreUnsafe(ref o, 320);
        Vector256.Create(c1.GetUpper(), d1.GetUpper()).AsByte().StoreUnsafe(ref o, 352);
        Vector256.Create(a2.GetUpper(), b2.GetUpper()).AsByte().StoreUnsafe(ref o, 384);
        Vector256.Create(c2.GetUpper(), d2.GetUpper()).AsByte().StoreUnsafe(ref o, 416);
        Vector256.Create(a3.GetUpper(), b3.GetUpper()).AsByte().StoreUnsafe(ref o, 448);
        Vector256.Create(c3.GetUpper(), d3.GetUpper()).AsByte().StoreUnsafe(ref o, 480);

        counter += 8;
        _state[12] = (uint)counter;
        _state[13] = (uint)(counter >> 32);
    }

    internal void GenerateBlockScalar(Span<byte> output)
    {
        Span<uint> workingState = stackalloc uint[16];
        ReadOnlySpan<uint> stateSpan = _state;
        stateSpan.CopyTo(workingState);

        for (int i = 0; i < 10; i++)
        {
            QuarterRound(ref workingState[0], ref workingState[4], ref workingState[8],  ref workingState[12]);
            QuarterRound(ref workingState[1], ref workingState[5], ref workingState[9],  ref workingState[13]);
            QuarterRound(ref workingState[2], ref workingState[6], ref workingState[10], ref workingState[14]);
            QuarterRound(ref workingState[3], ref workingState[7], ref workingState[11], ref workingState[15]);

            QuarterRound(ref workingState[0], ref workingState[5], ref workingState[10], ref workingState[15]);
            QuarterRound(ref workingState[1], ref workingState[6], ref workingState[11], ref workingState[12]);
            QuarterRound(ref workingState[2], ref workingState[7], ref workingState[8],  ref workingState[13]);
            QuarterRound(ref workingState[3], ref workingState[4], ref workingState[9],  ref workingState[14]);
        }

        for (int i = 0; i < 16; i++)
        {
            uint mixedWord = workingState[i] + stateSpan[i];
            BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4, 4), mixedWord);
        }

        // Increment the block counter (a 64-bit value in words 12 and 13).
        if (++_state[12] == 0)
        {
            _state[13]++;
        }
    }

    /// <summary>
    /// Fills the buffer: eight blocks where the machine can, otherwise one.
    /// </summary>
    ///
    /// <remarks>
    /// The narrow path fills *one* block and not eight, although the buffer
    /// holds eight. Eight single blocks would do eight times the work at the
    /// first draw, and only the wide path makes them cheap.
    /// </remarks>
    private void ReloadBuffer()
    {
        Span<byte> buffer = _buffer;

        if (EightWayAvailable)
        {
            GenerateBlocks8(buffer);
            _bufferEnd = 512;
        }
        else
        {
            GenerateBlock(buffer.Slice(0, 64));
            _bufferEnd = 64;
        }

        _bufferIndex = 0;
    }

    /// <summary>
    /// Uses state from this generator to make a new, independent generator,
    /// for example for a different thread.
    /// </summary>
    public ChaChaRng Split()
    {
        byte[] newKeyBytes = new byte[32];
        FillBytes(newKeyBytes);

        uint[] newKey = new uint[8];
        for (int i = 0; i < 8; i++)
        {
            newKey[i] = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(newKeyBytes, i * 4, 4));
        }

        return new ChaChaRng(newKey);
    }

    public uint NextU32()
    {
        if (_bufferIndex + 4 > _bufferEnd) ReloadBuffer();
        ReadOnlySpan<byte> bufferSpan = _buffer;
        uint result = BinaryPrimitives.ReadUInt32LittleEndian(bufferSpan.Slice(_bufferIndex, 4));
        _bufferIndex += 4;
        return result;
    }

    public ulong NextU64()
    {
        if (_bufferIndex + 8 > _bufferEnd)
        {
            // Across the end of the buffer, the value is made from two U32 draws.
            if (_bufferIndex < _bufferEnd)
            {
                uint low = NextU32();
                uint high = NextU32();
                return ((ulong)high << 32) | low;
            }
            ReloadBuffer();
        }
        ReadOnlySpan<byte> bufferSpan = _buffer;
        ulong result = BinaryPrimitives.ReadUInt64LittleEndian(bufferSpan.Slice(_bufferIndex, 8));
        _bufferIndex += 8;
        return result;
    }

    /// A double in [0, 1), with 53 bits of randomness.
    public double NextF64()
    {
        ulong value = NextU64() >> 11;
        return value * (1.0 / (1UL << 53));
    }

    public void FillBytes(byte[] buffer)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));

        int offset = 0;
        int remaining = buffer.Length;

        // Use the bytes left in the internal buffer first.
        if (_bufferIndex < _bufferEnd)
        {
            int available = _bufferEnd - _bufferIndex;
            int toCopy = Math.Min(available, remaining);
            ReadOnlySpan<byte> bufferSpan = _buffer;
            bufferSpan.Slice(_bufferIndex, toCopy).CopyTo(new Span<byte>(buffer, offset, toCopy));
            _bufferIndex += toCopy;
            offset += toCopy;
            remaining -= toCopy;
        }

        // Eight blocks at a time directly into the output, where available.
        if (EightWayAvailable)
        {
            while (remaining >= 512)
            {
                GenerateBlocks8(new Span<byte>(buffer, offset, 512));
                offset += 512;
                remaining -= 512;
            }
        }

        // Whole blocks directly into the output for the remainder.
        while (remaining >= 64)
        {
            GenerateBlock(new Span<byte>(buffer, offset, 64));
            offset += 64;
            remaining -= 64;
        }

        // Keep the surplus of the last block in the internal buffer.
        if (remaining > 0)
        {
            ReloadBuffer();
            ReadOnlySpan<byte> bufferSpan = _buffer;
            bufferSpan.Slice(0, remaining).CopyTo(new Span<byte>(buffer, offset, remaining));
            _bufferIndex = remaining;
        }
    }

    /// <summary>
    /// A random integer in the inclusive range [min, max]. Uses Lemire's
    /// method, so there is no modulo bias.
    /// </summary>
    public int Range(int min, int max)
    {
        if (min > max) throw new ArgumentException("min must be less than or equal to max.");
        if (min == max) return min;

        uint range = (uint)((ulong)max - (ulong)min + 1);
        if (range == 0) // The range is all of the UInt32 space.
            return (int)NextU32();

        uint x = NextU32();
        ulong m = (ulong)x * range;
        uint l = (uint)m;

        if (l < range)
        {
            uint t = (0U - range) % range; // Equal to (2^32) % range.
            while (l < t)
            {
                x = NextU32();
                m = (ulong)x * range;
                l = (uint)m;
            }
        }
        return min + (int)(m >> 32);
    }

    public bool NextBool()
    {
        if (_boolCacheBitsLeft == 0)
        {
            _boolCache = NextU64();
            _boolCacheBitsLeft = 64;
        }
        bool result = (_boolCache & 1) == 1;
        _boolCache >>= 1;
        _boolCacheBitsLeft--;
        return result;
    }

    public bool Chance(double probability)
    {
        if (probability <= 0.0) return false;
        if (probability >= 1.0) return true;
        return NextF64() < probability;
    }

    public void Shuffle<T>(T[] array)
    {
        if (array == null || array.Length < 2) return;
        for (int i = array.Length - 1; i > 0; i--)
        {
            int j = Range(0, i);
            if (i != j)
            {
                T temp = array[i];
                array[i] = array[j];
                array[j] = temp;
            }
        }
    }

    public void Shuffle<T>(IList<T> list)
    {
        if (list == null || list.Count < 2) return;
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Range(0, i);
            if (i != j)
            {
                T temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }
        }
    }

    public T Choice<T>(T[] array)
    {
        if (array == null || array.Length == 0) throw new ArgumentException("The array must not be empty.");
        return array[Range(0, array.Length - 1)];
    }

    public T Choice<T>(IList<T> list)
    {
        if (list == null || list.Count == 0) throw new ArgumentException("The list must not be empty.");
        return list[Range(0, list.Count - 1)];
    }

    private static ReadOnlySpan<byte> DefaultAlphabet =>
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"u8;

    /// <summary>
    /// A string over <c>[A-Za-z0-9-_]</c>.
    /// </summary>
    ///
    /// <remarks>
    /// The alphabet has 64 symbols, so the top six bits of a draw select a
    /// symbol without bias. For a range of 64, <see cref="Range"/> gives the
    /// same value from the same draw and never rejects one, so the output is
    /// the same as one <c>Range(0, 63)</c> per symbol.
    ///
    /// The alphabet is ASCII, so the bytes are valid UTF-8 and become the
    /// string without a copy or a validation.
    /// </remarks>
    public Utf8String RandomString(int length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length == 0) return Utf8String.Empty;

        ReadOnlySpan<byte> alphabet = DefaultAlphabet;
        byte[] result = new byte[length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = alphabet[(int)(NextU32() >> 26)];
        }
        return new Utf8String(result);
    }
}
