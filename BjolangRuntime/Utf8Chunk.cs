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

// Utf8Chunk: bytes checked to be UTF-8 once, that strings are then taken
// from by byte offset without being checked again.
//
// It is for a reader that finds its fields by looking at bytes, as (text csv)
// does, and makes many small strings from one buffer.
//
// The bytes are a copy that the chunk owns and never hands out, so nothing
// can change them after the check. A string is taken only between two
// character boundaries inside the part that was found valid, and a part of
// valid UTF-8 cut at character boundaries is itself valid UTF-8. So no string
// made here can be invalid, whatever offsets the caller gives: an offset that
// is not a boundary raises.

using System.Buffers;
using System.Text;
using System.Text.Unicode;
using BjoString;

namespace Bjolang.Runtime;

[NonUserCode]
public sealed class Utf8Chunk {
    private readonly byte[] _bytes;

    /// <summary>
    /// Where the valid UTF-8 from the start ends: the length, or where the
    /// first sequence starts that is not valid or not whole. Always a
    /// character boundary.
    /// </summary>
    public int ValidEnd { get; }

    public int Length => _bytes.Length;

    private Utf8Chunk(byte[] bytes) {
        _bytes = bytes;
        ValidEnd = ValidPrefix(bytes);
    }

    /// <summary>A chunk of a copy of the bytes from <paramref name="start"/> to <paramref name="end"/>.</summary>
    public static Utf8Chunk FromBytes(byte[] bytes, int start, int end) {
        ArgumentNullException.ThrowIfNull(bytes);
        if (start < 0 || end > bytes.Length || start > end) {
            throw new ArgumentOutOfRangeException(nameof(start),
                $"bytes->utf8-chunk: the range {start}..{end} is not within the {bytes.Length} bytes.");
        }
        return new Utf8Chunk(bytes.AsSpan(start, end - start).ToArray());
    }

    /// <summary>
    /// The text from offset <paramref name="start"/> to <paramref name="end"/>
    /// of the chunk. Both have to be character boundaries, at most
    /// <see cref="ValidEnd"/>.
    /// </summary>
    public Utf8String Substring(int start, int end) {
        if (start < 0 || end > ValidEnd || start > end) {
            throw new ArgumentOutOfRangeException(nameof(start),
                $"utf8-chunk-substring: the range {start}..{end} is not within the {ValidEnd} valid bytes.");
        }
        if (start == end) return default;
        if (!IsBoundary(start) || !IsBoundary(end)) {
            throw new ArgumentException(
                $"utf8-chunk-substring: the range {start}..{end} does not start and end between characters.");
        }
        return new Utf8String(_bytes.AsSpan(start, end - start).ToArray());
    }

    // Inside the valid part, a byte that is not a continuation byte starts a
    // character. ValidEnd is a boundary by how it is found.
    private bool IsBoundary(int i) => i == ValidEnd || (_bytes[i] & 0xC0) != 0x80;

    private static int ValidPrefix(ReadOnlySpan<byte> bytes) {
        if (Utf8.IsValid(bytes)) return bytes.Length;
        // A buffer read from a stream is most often cut inside its last
        // character, and is valid before it.
        int whole = WholeEnd(bytes);
        if (whole < bytes.Length && Utf8.IsValid(bytes[..whole])) return whole;
        int at = 0;
        while (at < bytes.Length) {
            if (Rune.DecodeFromUtf8(bytes[at..], out _, out int used) != OperationStatus.Done) return at;
            at += used;
        }
        return at;
    }

    // Where the last character starts when its lead byte says it has more
    // bytes than are left; else the length.
    private static int WholeEnd(ReadOnlySpan<byte> bytes) {
        for (int i = bytes.Length - 1, seen = 1; i >= 0 && seen <= 4; i--, seen++) {
            byte b = bytes[i];
            if (b < 0x80) return bytes.Length;
            if (b < 0xC0) continue;
            int need = b < 0xE0 ? 2 : b < 0xF0 ? 3 : 4;
            return seen < need ? i : bytes.Length;
        }
        return bytes.Length;
    }
}
