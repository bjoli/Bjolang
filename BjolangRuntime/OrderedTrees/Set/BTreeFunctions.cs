using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bjolang.Runtime.OrderedTrees.SetNodes
{
    [NonUserCode]
    public static class BTreeFunctions
    {
        // The vector scans order NaN below every other key, as the comparer does, but cannot
        // find NaN itself: no comparison with NaN is true. A NaN key goes through the comparer.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int FindIndex<T>(LeafNode<T> node, T key, IComparer<T> comparer)
        {
            if (typeof(T) == typeof(int))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref int firstIntRef = ref Unsafe.As<T, int>(ref firstKeyRef);
                ReadOnlySpan<int> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                int intKey = Unsafe.As<T, int>(ref key);
                return Scanners.FindFirstGreaterOrEqualInt(intKeys, intKey);
            }
            if (typeof(T) == typeof(uint))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref uint firstIntRef = ref Unsafe.As<T, uint>(ref firstKeyRef);
                ReadOnlySpan<uint> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                uint intKey = Unsafe.As<T, uint>(ref key);
                return Scanners.FindFirstGreaterOrEqualInt(intKeys, intKey);
            }
            if (typeof(T) == typeof(long))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref long firstIntRef = ref Unsafe.As<T, long>(ref firstKeyRef);
                ReadOnlySpan<long> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                long intKey = Unsafe.As<T, long>(ref key);
                return Scanners.FindFirstGreaterOrEqualInt(intKeys, intKey);
            }
            if (typeof(T) == typeof(ulong))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref ulong firstIntRef = ref Unsafe.As<T, ulong>(ref firstKeyRef);
                ReadOnlySpan<ulong> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                ulong intKey = Unsafe.As<T, ulong>(ref key);
                return Scanners.FindFirstGreaterOrEqualInt(intKeys, intKey);
            }
            if (typeof(T) == typeof(short))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref short firstIntRef = ref Unsafe.As<T, short>(ref firstKeyRef);
                ReadOnlySpan<short> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                short intKey = Unsafe.As<T, short>(ref key);
                return Scanners.FindFirstGreaterOrEqualInt(intKeys, intKey);
            }
            if (typeof(T) == typeof(ushort))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref ushort firstIntRef = ref Unsafe.As<T, ushort>(ref firstKeyRef);
                ReadOnlySpan<ushort> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                ushort intKey = Unsafe.As<T, ushort>(ref key);
                return Scanners.FindFirstGreaterOrEqualInt(intKeys, intKey);
            }
            if (typeof(T) == typeof(float) && !float.IsNaN(Unsafe.As<T, float>(ref key)))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref float firstIntRef = ref Unsafe.As<T, float>(ref firstKeyRef);
                ReadOnlySpan<float> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                float intKey = Unsafe.As<T, float>(ref key);
                return Scanners.FindFirstGreaterOrEqualFloat(intKeys, intKey);
            }
            if (typeof(T) == typeof(double) && !double.IsNaN(Unsafe.As<T, double>(ref key)))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref double firstIntRef = ref Unsafe.As<T, double>(ref firstKeyRef);
                ReadOnlySpan<double> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                double intKey = Unsafe.As<T, double>(ref key);
                return Scanners.FindFirstGreaterOrEqualFloat(intKeys, intKey);
            }

            return BinarySearchKeys(node.GetKeys(), key, comparer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int FindRoutingIndex<T>(InternalNode<T> node, T key, IComparer<T> comparer)
        {   
            if (typeof(T) == typeof(int))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref int firstIntRef = ref Unsafe.As<T, int>(ref firstKeyRef);
                ReadOnlySpan<int> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                int intKey = Unsafe.As<T, int>(ref key);
                return Scanners.FindFirstGreaterInt(intKeys, intKey);
            }
            if (typeof(T) == typeof(uint))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref uint firstIntRef = ref Unsafe.As<T, uint>(ref firstKeyRef);
                ReadOnlySpan<uint> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                uint intKey = Unsafe.As<T, uint>(ref key);
                return Scanners.FindFirstGreaterInt(intKeys, intKey);
            }
            if (typeof(T) == typeof(long))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref long firstIntRef = ref Unsafe.As<T, long>(ref firstKeyRef);
                ReadOnlySpan<long> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                long intKey = Unsafe.As<T, long>(ref key);
                return Scanners.FindFirstGreaterInt(intKeys, intKey);
            }
            if (typeof(T) == typeof(ulong))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref ulong firstIntRef = ref Unsafe.As<T, ulong>(ref firstKeyRef);
                ReadOnlySpan<ulong> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                ulong intKey = Unsafe.As<T, ulong>(ref key);
                return Scanners.FindFirstGreaterInt(intKeys, intKey);
            }
            if (typeof(T) == typeof(short))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref short firstIntRef = ref Unsafe.As<T, short>(ref firstKeyRef);
                ReadOnlySpan<short> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                short intKey = Unsafe.As<T, short>(ref key);
                return Scanners.FindFirstGreaterInt(intKeys, intKey);
            }
            if (typeof(T) == typeof(ushort))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref ushort firstIntRef = ref Unsafe.As<T, ushort>(ref firstKeyRef);
                ReadOnlySpan<ushort> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                ushort intKey = Unsafe.As<T, ushort>(ref key);
                return Scanners.FindFirstGreaterInt(intKeys, intKey);
            }
            if (typeof(T) == typeof(float) && !float.IsNaN(Unsafe.As<T, float>(ref key)))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref float firstIntRef = ref Unsafe.As<T, float>(ref firstKeyRef);
                ReadOnlySpan<float> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                float intKey = Unsafe.As<T, float>(ref key);
                return Scanners.FindFirstGreaterFloat(intKeys, intKey);
            }
            if (typeof(T) == typeof(double) && !double.IsNaN(Unsafe.As<T, double>(ref key)))
            {
                Span<T> keys = node.GetKeys();
                ref T firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref double firstIntRef = ref Unsafe.As<T, double>(ref firstKeyRef);
                ReadOnlySpan<double> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                double intKey = Unsafe.As<T, double>(ref key);
                return Scanners.FindFirstGreaterFloat(intKeys, intKey);
            }

            return BinaryRoutingKeys(node.GetKeys(), key, comparer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BinarySearchKeys<T>(ReadOnlySpan<T> keys, T key, IComparer<T> comparer)
        {
            int low = 0;
            int high = keys.Length - 1;
            ref T keysRef = ref MemoryMarshal.GetReference(keys);

            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                T midKey = Unsafe.Add(ref keysRef, mid);
                int cmp = comparer.Compare(midKey, key);
        
                if (cmp == 0) return mid;
                if (cmp < 0) low = mid + 1;
                else high = mid - 1;
            }
            return low;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BinaryRoutingKeys<T>(ReadOnlySpan<T> keys, T key, IComparer<T> comparer)
        {
            int low = 0;
            int high = keys.Length - 1;
            ref T keysRef = ref MemoryMarshal.GetReference(keys);
            
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                T midKey = Unsafe.Add(ref keysRef, mid);
                int cmp = comparer.Compare(midKey, key);
                
                if (cmp <= 0) low = mid + 1;
                else high = mid - 1;
            }
            return low;
        }
    }
}
