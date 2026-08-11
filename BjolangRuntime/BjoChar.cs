using System;
using System.Text;

namespace Bjolang.Runtime;

/// <summary>
/// Represents a 32-bit Unicode scalar value (Scheme-style character).
/// </summary>
public readonly record struct BjoChar : IComparable<BjoChar>
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
    /// Zero-allocation append directly into a C# StringBuilder.
    /// </summary>
    public void AppendTo(StringBuilder sb)
    {
        if (Value <= 0xFFFF)
        {
            // Single UTF-16 code unit fit
            sb.Append((char)Value);
        }
        else
        {
            // High/Low surrogate pair calculation (zero string allocation)
            uint scalar = Value - 0x10000;
            sb.Append((char)((scalar >> 10) + 0xD800));
            sb.Append((char)((scalar & 0x3FF) + 0xDC00));
        }
    }
}
