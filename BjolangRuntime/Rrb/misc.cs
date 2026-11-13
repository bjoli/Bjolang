using System.Buffers;
using System.Runtime.CompilerServices;

namespace Bjolang.Runtime.Rrb;

#pragma warning disable CS8981
[NonUserCode]
public static class misc
{
    
    // This should return a pretty unbalanced RrbList.
    public static RrbList<int> MakeUnbalanced(int length)
    {
        var list = new RrbList<int>(Enumerable.Range(0, length));

        for (int i = 0; i < list.Count; i += Constants.RRB_BRANCHING)
        {
            list = list.RemoveAt(i);
        }

        return list;
    }
}

[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
[NonUserCode]
internal ref struct ArrayPoolScope<T>(int length)
{
    private T[]? _array = ArrayPool<T>.Shared.Rent(length);
    // Returns a Span of the requested size, ignoring the extra buffer slack
    private Span<T> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _array.AsSpan(0, length);
    }

    public T[]? Array => _array;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        T[]? arr = _array;
        if (arr != null)
        {
            _array = null; // Poison the struct to prevent double-return
            
            bool needsClear = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
            ArrayPool<T>.Shared.Return(arr, clearArray: needsClear);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator Span<T>(ArrayPoolScope<T> scope) => scope.Span;
}