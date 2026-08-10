using System;
using System.Text;

namespace Bjolang.Runtime;

/// <summary>
/// Represents a 32-bit Unicode scalar value (Scheme-style character).
/// </summary>
public readonly record struct BjoChar
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
