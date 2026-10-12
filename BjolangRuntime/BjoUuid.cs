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

using System;
using System.Buffers.Binary;
using System.Threading;

namespace Bjolang.Runtime;

/// <summary>
/// The shim under <c>(std uuid)</c>. A uuid crosses as two <c>ulong</c>s, the
/// high and the low 64 bits in network byte order, so that comparing the two
/// numbers compares the bytes. Every method answers the high half and gives the
/// low half as an <c>out</c> parameter.
/// </summary>
public static class UuidModule
{
    const ulong VersionMask = 0xFFFF_FFFF_FFFF_0FFFUL;
    const ulong VariantMask = 0x3FFF_FFFF_FFFF_FFFFUL;
    const ulong Rfc9562Variant = 0x8000_0000_0000_0000UL;

    static ulong WithVersion(ulong hi, int version) => (hi & VersionMask) | ((ulong)version << 12);

    static ulong WithVariant(ulong lo) => (lo & VariantMask) | Rfc9562Variant;

    /// <summary>Version 4: 122 random bits from the caller.</summary>
    public static ulong V4(ulong random1, ulong random2, out ulong lo)
    {
        lo = WithVariant(random2);
        return WithVersion(random1, 4);
    }

    // The last version 7 value made in this process, as the 48 bits of the
    // time followed by the 74 random bits.
    static UInt128 lastV7;
    static readonly Lock v7Lock = new();

    /// <summary>
    /// Version 7: the Unix time in milliseconds, then 74 random bits. Values
    /// made in this process always increase. A value that would not be greater
    /// than the last one is the last one plus one, as RFC 9562 section 6.2
    /// allows, so a clock that stands still or steps back still gives values in
    /// increasing order.
    /// </summary>
    public static ulong V7(long unixMs, ulong random1, ulong random2, out ulong lo)
    {
        const int RandomBits = 74;
        var ms = (UInt128)((ulong)unixMs & 0xFFFF_FFFF_FFFFUL);
        var random = (((UInt128)random1 << 64) | random2) & ((UInt128.One << RandomBits) - 1);
        var candidate = (ms << RandomBits) | random;

        UInt128 chosen;
        lock (v7Lock)
        {
            chosen = candidate > lastV7 ? candidate : lastV7 + 1;
            lastV7 = chosen;
        }

        var time = (ulong)(chosen >> RandomBits);
        var randA = (ulong)(chosen >> 62) & 0xFFFUL;
        var randB = (ulong)chosen & VariantMask;
        lo = randB | Rfc9562Variant;
        return (time << 16) | (7UL << 12) | randA;
    }

    /// <summary>Versions 3 and 5: the first 16 bytes of a digest.</summary>
    public static ulong FromDigest(byte[] digest, int version, out ulong lo)
    {
        if (digest.Length < 16)
            throw new ArgumentException("A name-based uuid needs a digest of at least 16 bytes.");
        lo = WithVariant(BinaryPrimitives.ReadUInt64BigEndian(digest.AsSpan(8, 8)));
        return WithVersion(BinaryPrimitives.ReadUInt64BigEndian(digest.AsSpan(0, 8)), version);
    }

    public static byte[] ToBytes(ulong hi, ulong lo)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(0, 8), hi);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), lo);
        return bytes;
    }

    public static bool TryFromBytes(byte[] bytes, out ulong hi, out ulong lo)
    {
        hi = 0;
        lo = 0;
        if (bytes.Length != 16) return false;
        hi = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(0, 8));
        lo = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8, 8));
        return true;
    }

    /// <summary>The 36-character form: 8-4-4-4-12 hex digits.</summary>
    public static string Format(ulong hi, ulong lo, bool upper)
    {
        Span<char> chars = stackalloc char[36];
        var digits = upper ? "0123456789ABCDEF" : "0123456789abcdef";
        var at = 0;
        for (var i = 0; i < 32; i++)
        {
            if (i == 8 || i == 12 || i == 16 || i == 20) chars[at++] = '-';
            var word = i < 16 ? hi : lo;
            var shift = (15 - (i % 16)) * 4;
            chars[at++] = digits[(int)((word >> shift) & 0xF)];
        }
        return new string(chars);
    }

    static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>
    /// The 36-character form, in either case. Nothing else: no braces, no
    /// <c>urn:uuid:</c>, and no form without hyphens, so that one uuid has one
    /// text in each case.
    /// </summary>
    public static bool TryParse(string text, out ulong hi, out ulong lo)
    {
        hi = 0;
        lo = 0;
        if (text.Length != 36) return false;
        ulong h = 0, l = 0;
        var digit = 0;
        for (var i = 0; i < 36; i++)
        {
            var c = text[i];
            if (i == 8 || i == 13 || i == 18 || i == 23)
            {
                if (c != '-') return false;
                continue;
            }
            var v = HexValue(c);
            if (v < 0) return false;
            if (digit < 16) h = (h << 4) | (uint)v;
            else l = (l << 4) | (uint)v;
            digit++;
        }
        hi = h;
        lo = l;
        return true;
    }

    public static Guid ToGuid(ulong hi, ulong lo)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes[..8], hi);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], lo);
        return new Guid(bytes, bigEndian: true);
    }

    public static ulong FromGuid(Guid guid, out ulong lo)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes, bigEndian: true, out _);
        lo = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]);
    }
}
