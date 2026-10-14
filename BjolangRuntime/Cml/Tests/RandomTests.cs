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

// ChaChaRng: the RFC 8439 block, the three block paths against each other, the
// buffer, and `RandomString`.

using System;
using System.Linq;
using static Bjoml.Tests.Harness;
using Bjolang.Runtime;

namespace Bjoml.Tests;

public static class RandomTests
{
    public static void RunAll()
    {
        Section("Random");
        Run("one block matches the RFC 8439 test vector", Rfc8439Block);
        Run("the vector path gives the scalar path's stream", VectorAgainstScalar);
        Run("eight blocks at once give eight single blocks' stream", EightWayAgainstSingle);
        Run("FillBytes gives the same stream as repeated draws", FillAgainstDraws);
        Run("RandomString draws as Range(0, 63) per symbol", RandomStringAgainstRange);
    }

    private static void Rfc8439Block()
    {
        uint[] key =
        {
            0x03020100, 0x07060504, 0x0b0a0908, 0x0f0e0d0c,
            0x13121110, 0x17161514, 0x1b1a1918, 0x1f1e1d1c,
        };

        var rng = new ChaChaRng(key);

        // The RFC's block counter and nonce, put in words 12..15.
        rng._state[12] = 1;
        rng._state[13] = 0x09000000;
        rng._state[14] = 0x4a000000;
        rng._state[15] = 0x00000000;

        byte[] block = new byte[64];
        rng.FillBytes(block);

        byte[] expected =
        {
            0x10, 0xf1, 0xe7, 0xe4, 0xd1, 0x3b, 0x59, 0x15, 0x50, 0x0f, 0xdd, 0x1f, 0xa3, 0x20, 0x71, 0xc4,
            0xc7, 0xd1, 0xf4, 0xc7, 0x33, 0xc0, 0x68, 0x03, 0x04, 0x22, 0xaa, 0x9a, 0xc3, 0xd4, 0x6c, 0x4e,
            0xd2, 0x82, 0x64, 0x46, 0x07, 0x9f, 0xaa, 0x09, 0x14, 0xc2, 0xd7, 0x05, 0xd9, 0x8b, 0x02, 0xa2,
            0xb5, 0x12, 0x9c, 0xd1, 0xde, 0x16, 0x4e, 0xb9, 0xcb, 0xd0, 0x83, 0xe8, 0xa2, 0x50, 0x3c, 0x4e,
        };

        Assert(block.SequenceEqual(expected),
            $"expected {BitConverter.ToString(expected)}, got {BitConverter.ToString(block)}");
    }

    /// The counter starts just below the carry from word 12 to word 13. That is
    /// the only place where a block is not simply the next in sequence, and
    /// where a wrong lane or a lost carry would show.
    private static void VectorAgainstScalar()
    {
        byte[] fromVector = new byte[64];
        byte[] fromScalar = new byte[64];

        for (int trial = 0; trial < 64; trial++)
        {
            var vectorRng = new ChaChaRng((ulong)trial);
            var scalarRng = new ChaChaRng((ulong)trial);

            vectorRng._state[12] = 0xFFFFFFFDu;
            vectorRng._state[13] = 7u;
            scalarRng._state[12] = 0xFFFFFFFDu;
            scalarRng._state[13] = 7u;

            for (int block = 0; block < 8; block++)
            {
                vectorRng.GenerateBlockVector(fromVector);
                scalarRng.GenerateBlockScalar(fromScalar);

                Assert(fromVector.SequenceEqual(fromScalar),
                    $"seed {trial}, block {block}: scalar {BitConverter.ToString(fromScalar)}, " +
                    $"vector {BitConverter.ToString(fromVector)}");
            }
        }
    }

    /// If this fails, the cause is the transpose or the per-lane block counter.
    private static void EightWayAgainstSingle()
    {
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported) return;

        byte[] wide = new byte[512];
        byte[] one = new byte[64];

        for (int trial = 0; trial < 64; trial++)
        {
            var wideRng = new ChaChaRng((ulong)trial);
            var singleRng = new ChaChaRng((ulong)trial);

            // Just below the carry, so that the eight blocks go across it.
            wideRng._state[12] = 0xFFFFFFFCu;
            wideRng._state[13] = 3u;
            singleRng._state[12] = 0xFFFFFFFCu;
            singleRng._state[13] = 3u;

            wideRng.GenerateBlocks8(wide);

            for (int block = 0; block < 8; block++)
            {
                singleRng.GenerateBlockVector(one);
                Assert(wide.AsSpan(block * 64, 64).SequenceEqual(one),
                    $"seed {trial}, block {block}: the wide path is different");
            }

            AssertEqual(singleRng._state[12], wideRng._state[12], $"counter word 12, seed {trial}");
            AssertEqual(singleRng._state[13], wideRng._state[13], $"counter word 13, seed {trial}");
        }
    }

    /// The buffer holds eight blocks but is filled with one on machines without
    /// AVX2, so its limit is a field and not its length. An error there drops
    /// or repeats bytes, and that is visible only when the same stream is read
    /// in two ways. The lengths are not multiples of 64 or 512.
    private static void FillAgainstDraws()
    {
        foreach (int length in new[] { 1, 7, 63, 64, 65, 100, 511, 512, 513, 1000, 4096 })
        {
            var filled = new ChaChaRng(42UL);
            var drawn = new ChaChaRng(42UL);

            byte[] fromFill = new byte[length];
            filled.FillBytes(fromFill);

            byte[] fromDraws = new byte[(length + 3) / 4 * 4];
            for (int i = 0; i < fromDraws.Length; i += 4)
            {
                BitConverter.TryWriteBytes(fromDraws.AsSpan(i, 4), drawn.NextU32());
            }

            Assert(fromFill.AsSpan().SequenceEqual(fromDraws.AsSpan(0, length)),
                $"{length} bytes: FillBytes and NextU32 are different");
        }
    }

    /// A seeded `random-string` must give the same text as before it wrote bytes.
    private static void RandomStringAgainstRange()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

        foreach (int length in new[] { 0, 1, 17, 200, 1000 })
        {
            var fast = new ChaChaRng(7UL);
            var reference = new ChaChaRng(7UL);

            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = alphabet[reference.Range(0, alphabet.Length - 1)];

            AssertEqual(new string(chars), fast.RandomString(length).ToString(), $"length {length}");
            AssertEqual(reference.NextU32(), fast.NextU32(), $"next draw after length {length}");
        }
    }
}
