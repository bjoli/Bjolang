using System.Runtime.CompilerServices;

namespace Bjolang.Runtime.Rrb;

[NonUserCode]
public static class RrbList
{
    /// <summary>
    /// Creates a new RRB-List from a ReadOnlySpan. This enables C# 12+ collection expressions.
    /// </summary>
    public static RrbList<T> Create<T>(ReadOnlySpan<T> items)
    {
        return RrbList<T>.Create(items);
    }
}
