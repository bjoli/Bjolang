// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.
//
// As a special exception to the Mozilla Public License, version 2.0, if you
// compile your application source code and portions of this software are
// embedded into the generated object code or executable form as a normal
// consequence of the compilation process (such as inline functions,
// templates, generics, or macros), you may redistribute such embedded portions
// in such object code or executable form without complying with the source code
// availability requirements or notice obligations of Section 3 of the MPL 2.0.

using System;
using System.Buffers.Binary;

namespace Bjolang.Runtime;

/// The buffer under `(std prelude bytestring)`'s `ByteStringBuilder`: bytes
/// appended at the end, and numbers appended in a byte order.
///
/// - `BigEndian` is the builder's own order, fixed when it is made. Each
///   `Add` of a number takes the order to write in; the prelude passes the
///   builder's own, or the one a `-le` or `-be` name says.
/// - `ToArray` copies, so the builder can be added to after it.
[NonUserCode]
public sealed class ByteStringBuilder {
    private byte[] buffer;
    private int length;

    public readonly bool BigEndian;

    public ByteStringBuilder(bool bigEndian) {
        buffer = Array.Empty<byte>();
        BigEndian = bigEndian;
    }

    public int Length => length;

    public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();

    public void Clear() => length = 0;

    public void AddByte(byte b) => Space(1)[0] = b;

    public void AddBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Space(bytes.Length));

    public void AddShort(short n, bool big) {
        if (big) BinaryPrimitives.WriteInt16BigEndian(Space(2), n);
        else BinaryPrimitives.WriteInt16LittleEndian(Space(2), n);
    }

    public void AddUShort(ushort n, bool big) {
        if (big) BinaryPrimitives.WriteUInt16BigEndian(Space(2), n);
        else BinaryPrimitives.WriteUInt16LittleEndian(Space(2), n);
    }

    public void AddInt(int n, bool big) {
        if (big) BinaryPrimitives.WriteInt32BigEndian(Space(4), n);
        else BinaryPrimitives.WriteInt32LittleEndian(Space(4), n);
    }

    public void AddUInt(uint n, bool big) {
        if (big) BinaryPrimitives.WriteUInt32BigEndian(Space(4), n);
        else BinaryPrimitives.WriteUInt32LittleEndian(Space(4), n);
    }

    public void AddLong(long n, bool big) {
        if (big) BinaryPrimitives.WriteInt64BigEndian(Space(8), n);
        else BinaryPrimitives.WriteInt64LittleEndian(Space(8), n);
    }

    public void AddULong(ulong n, bool big) {
        if (big) BinaryPrimitives.WriteUInt64BigEndian(Space(8), n);
        else BinaryPrimitives.WriteUInt64LittleEndian(Space(8), n);
    }

    public void AddFloat(float n, bool big) {
        if (big) BinaryPrimitives.WriteSingleBigEndian(Space(4), n);
        else BinaryPrimitives.WriteSingleLittleEndian(Space(4), n);
    }

    public void AddDouble(double n, bool big) {
        if (big) BinaryPrimitives.WriteDoubleBigEndian(Space(8), n);
        else BinaryPrimitives.WriteDoubleLittleEndian(Space(8), n);
    }

    // The next `count` bytes at the end, which the caller fills. The buffer
    // doubles when it is full, so appending is amortised O(1).
    private Span<byte> Space(int count) {
        int needed = length + count;
        if (needed > buffer.Length) {
            var bigger = new byte[Math.Max(needed, Math.Max(16, buffer.Length * 2))];
            buffer.AsSpan(0, length).CopyTo(bigger);
            buffer = bigger;
        }
        var space = buffer.AsSpan(length, count);
        length = needed;
        return space;
    }
}
