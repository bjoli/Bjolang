using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Bjolang.Runtime.OrderedTrees;

[NonUserCode]
public static class Scanners
{
    // Generic linear scan fallback
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LinearScanGreaterOrEqual<T>(ReadOnlySpan<T> keys, T target) where T : unmanaged, IComparisonOperators<T, T, bool>
    {
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i] >= target) return i;
        }
        return keys.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LinearScanGreater<T>(ReadOnlySpan<T> keys, T target) where T : unmanaged, IComparisonOperators<T, T, bool>
    {
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i] > target) return i;
        }
        return keys.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int ScanAvx2GreaterOrEqual<T>(ReadOnlySpan<T> keys, T target) where T : unmanaged, IComparisonOperators<T, T, bool>
    {
        var vTarget = Vector256.Create(target);
        var i = 0;
        var len = keys.Length;
        int step = Vector256<T>.Count;

        for (; i <= len - step; i += step)
        {
            fixed (T* ptr = keys)
            {
                var vData = Vector256.Load(ptr + i);
                var mask = Vector256.GreaterThanOrEqual(vData, vTarget);
                uint m = mask.ExtractMostSignificantBits();

                if (m != 0)
                {
                    return i + BitOperations.TrailingZeroCount(m);
                }
            }
        }
        return LinearScanGreaterOrEqual(keys.Slice(i), target) + i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int ScanAvx512GreaterOrEqual<T>(ReadOnlySpan<T> keys, T target) where T : unmanaged, IComparisonOperators<T, T, bool>
    {
        var vTarget = Vector512.Create(target);
        var i = 0;
        var len = keys.Length;
        int step = Vector512<T>.Count;

        for (; i <= len - step; i += step)
        {
            fixed (T* ptr = keys)
            {
                var vData = Vector512.Load(ptr + i);
                var mask = Vector512.GreaterThanOrEqual(vData, vTarget);

                if (mask != Vector512<T>.Zero)
                {
                    uint m = (uint)mask.ExtractMostSignificantBits();
                    return i + BitOperations.TrailingZeroCount(m);
                }
            }
        }
        return LinearScanGreaterOrEqual(keys.Slice(i), target) + i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int ScanAvx2Greater<T>(ReadOnlySpan<T> keys, T target) where T : unmanaged, IComparisonOperators<T, T, bool>
    {
        var vTarget = Vector256.Create(target);
        var i = 0;
        var len = keys.Length;
        int step = Vector256<T>.Count;

        for (; i <= len - step; i += step)
        {
            fixed (T* ptr = keys)
            {
                var vData = Vector256.Load(ptr + i);
                var mask = Vector256.GreaterThan(vData, vTarget);
                uint m = mask.ExtractMostSignificantBits();

                if (m != 0)
                {
                    return i + BitOperations.TrailingZeroCount(m);
                }
            }
        }
        return LinearScanGreater(keys.Slice(i), target) + i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int ScanAvx512Greater<T>(ReadOnlySpan<T> keys, T target) where T : unmanaged, IComparisonOperators<T, T, bool>
    {
        var vTarget = Vector512.Create(target);
        var i = 0;
        var len = keys.Length;
        int step = Vector512<T>.Count;

        for (; i <= len - step; i += step)
        {
            fixed (T* ptr = keys)
            {
                var vData = Vector512.Load(ptr + i);
                var mask = Vector512.GreaterThan(vData, vTarget);

                if (mask != Vector512<T>.Zero)
                {
                    uint m = (uint)mask.ExtractMostSignificantBits();
                    return i + BitOperations.TrailingZeroCount(m);
                }
            }
        }
        return LinearScanGreater(keys.Slice(i), target) + i;
    }

    // Public API for Integer types
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FindFirstGreaterOrEqualInt<T>(ReadOnlySpan<T> keys, T target) where T : unmanaged, IComparisonOperators<T, T, bool>
    {
        if (!Vector256.IsHardwareAccelerated || keys.Length < Vector256<T>.Count)
            return LinearScanGreaterOrEqual(keys, target);

        return Vector512.IsHardwareAccelerated
            ? ScanAvx512GreaterOrEqual(keys, target)
            : ScanAvx2GreaterOrEqual(keys, target);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FindFirstGreaterInt<T>(ReadOnlySpan<T> keys, T target) where T : unmanaged, IComparisonOperators<T, T, bool>
    {
        if (!Vector256.IsHardwareAccelerated || keys.Length < Vector256<T>.Count)
            return LinearScanGreater(keys, target);

        return Vector512.IsHardwareAccelerated
            ? ScanAvx512Greater(keys, target)
            : ScanAvx2Greater(keys, target);
    }

    // Public API for Float/Double
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FindFirstGreaterOrEqualFloat<T>(ReadOnlySpan<T> keys, T target) where T : unmanaged, IComparisonOperators<T, T, bool>
    {
        if (!Vector256.IsHardwareAccelerated || keys.Length < Vector256<T>.Count)
            return LinearScanGreaterOrEqual(keys, target);

        return Vector512.IsHardwareAccelerated
            ? ScanAvx512GreaterOrEqual(keys, target)
            : ScanAvx2GreaterOrEqual(keys, target);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FindFirstGreaterFloat<T>(ReadOnlySpan<T> keys, T target) where T : unmanaged, IComparisonOperators<T, T, bool>
    {
        if (!Vector256.IsHardwareAccelerated || keys.Length < Vector256<T>.Count)
            return LinearScanGreater(keys, target);

        return Vector512.IsHardwareAccelerated
            ? ScanAvx512Greater(keys, target)
            : ScanAvx2Greater(keys, target);
    }
}
