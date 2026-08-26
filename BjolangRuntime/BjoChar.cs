using System;
using System.Numerics;
using System.Text;

namespace Bjolang.Runtime;

/// <summary>
/// Represents a 32-bit Unicode scalar value (Scheme-style character).
/// </summary>
// `IComparisonOperators` is what Bjolang's `<` asks of an operand type: the
// operators below already exist, and declaring the interface is what lets a
// constrained generic function reach them. `IComparable` is the other half,
// and is what `Ord`'s `compare` — and so sorting — goes through.
public readonly record struct BjoChar : IComparable<BjoChar>, IComparisonOperators<BjoChar, BjoChar, bool>
{
    public uint Value { get; }

    public BjoChar(uint codePoint)
    {
        if (!Rune.IsValid(codePoint))
        {
            throw new ArgumentOutOfRangeException(nameof(codePoint), $"Invalid Unicode scalar value: 0x{codePoint:X}");
        }
        Value = codePoint;
    }

    /// <summary>
    /// Helper for string literal building during string interpolation or concatenation.
    /// </summary>
    public override string ToString() => new Rune(Value).ToString();

    // Ordering by codepoint, which is what R6RS's `char<?` is defined as.
    //
    // These exist so that Bjolang's `<` works on characters directly: the
    // operator is typed `(-> %a %a bool)` and emitted as the C# operator, so a
    // type that has one needs no builtin and chains n-arily for free. `char<?`
    // and friends are then ordinary library aliases rather than compiler
    // knowledge.
    //
    // Comparing by `Value` rather than by `ToString` also keeps the ordering
    // over astral characters right: a codepoint comparison puts U+1F600 after
    // U+FFFD, while comparing UTF-16 text would sort it by its high surrogate.
    public int CompareTo(BjoChar other) => Value.CompareTo(other.Value);

    public static bool operator <(BjoChar a, BjoChar b) => a.Value < b.Value;
    public static bool operator >(BjoChar a, BjoChar b) => a.Value > b.Value;
    public static bool operator <=(BjoChar a, BjoChar b) => a.Value <= b.Value;
    public static bool operator >=(BjoChar a, BjoChar b) => a.Value >= b.Value;

    /// <summary>
    /// This scalar as UTF-16, written into <paramref name="dest" /> — which must hold two
    /// units — and answering how many it took: one inside the BMP, two above it.
    ///
    /// The surrogate arithmetic is written once here because there is more than one sink
    /// for it: a <see cref="StringBuilder" /> and a <see cref="System.IO.TextWriter" />
    /// both want the units without a string in between.
    /// </summary>
    public int EncodeUtf16(Span<char> dest)
    {
        if (Value <= 0xFFFF)
        {
            // Single UTF-16 code unit fit
            dest[0] = (char)Value;
            return 1;
        }

        // High/Low surrogate pair calculation
        uint scalar = Value - 0x10000;
        dest[0] = (char)((scalar >> 10) + 0xD800);
        dest[1] = (char)((scalar & 0x3FF) + 0xDC00);
        return 2;
    }

    /// <summary>
    /// Zero-allocation append directly into a C# StringBuilder.
    /// </summary>
    public void AppendTo(StringBuilder sb)
    {
        Span<char> buf = stackalloc char[2];
        sb.Append(buf[..EncodeUtf16(buf)]);
    }

    /// <summary>
    /// Zero-allocation write directly to a port, for `write-char`.
    ///
    /// Not <c>Write((char)Value)</c>: that is a UTF-16 code unit and this is a scalar, so
    /// an astral character has to go out as its two halves or the port receives a lone
    /// surrogate.
    /// </summary>
    public void WriteTo(System.IO.TextWriter writer)
    {
        Span<char> buf = stackalloc char[2];
        writer.Write(buf[..EncodeUtf16(buf)]);
    }
}
