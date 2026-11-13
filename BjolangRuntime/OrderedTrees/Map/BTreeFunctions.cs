using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bjolang.Runtime.OrderedTrees.MapNodes
{
    [NonUserCode]
    public static class BTreeFunctions
    {
        // The vector scans order NaN below every other key, as the comparer does, but cannot
        // find NaN itself: no comparison with NaN is true. A NaN key goes through the comparer.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int FindIndex<TK,TV>(LeafNode<TK,TV> node, TK key, IComparer<TK> comparer)
        {
            if (typeof(TK) == typeof(int))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref int firstIntRef = ref Unsafe.As<TK, int>(ref firstKeyRef);
                ReadOnlySpan<int> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                int intKey = Unsafe.As<TK, int>(ref key);
                return Scanners.FindFirstGreaterOrEqualInt(intKeys, intKey);
            }
            if (typeof(TK) == typeof(uint))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref uint firstIntRef = ref Unsafe.As<TK, uint>(ref firstKeyRef);
                ReadOnlySpan<uint> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                uint intKey = Unsafe.As<TK, uint>(ref key);
                return Scanners.FindFirstGreaterOrEqualInt(intKeys, intKey);
            }
            if (typeof(TK) == typeof(long))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref long firstIntRef = ref Unsafe.As<TK, long>(ref firstKeyRef);
                ReadOnlySpan<long> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                long intKey = Unsafe.As<TK, long>(ref key);
                return Scanners.FindFirstGreaterOrEqualInt(intKeys, intKey);
            }
            if (typeof(TK) == typeof(ulong))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref ulong firstIntRef = ref Unsafe.As<TK, ulong>(ref firstKeyRef);
                ReadOnlySpan<ulong> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                ulong intKey = Unsafe.As<TK, ulong>(ref key);
                return Scanners.FindFirstGreaterOrEqualInt(intKeys, intKey);
            }
            if (typeof(TK) == typeof(short))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref short firstIntRef = ref Unsafe.As<TK, short>(ref firstKeyRef);
                ReadOnlySpan<short> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                short intKey = Unsafe.As<TK, short>(ref key);
                return Scanners.FindFirstGreaterOrEqualInt(intKeys, intKey);
            }
            if (typeof(TK) == typeof(ushort))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref ushort firstIntRef = ref Unsafe.As<TK, ushort>(ref firstKeyRef);
                ReadOnlySpan<ushort> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                ushort intKey = Unsafe.As<TK, ushort>(ref key);
                return Scanners.FindFirstGreaterOrEqualInt(intKeys, intKey);
            }
            if (typeof(TK) == typeof(float) && !float.IsNaN(Unsafe.As<TK, float>(ref key)))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref float firstIntRef = ref Unsafe.As<TK, float>(ref firstKeyRef);
                ReadOnlySpan<float> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                float intKey = Unsafe.As<TK, float>(ref key);
                return Scanners.FindFirstGreaterOrEqualFloat(intKeys, intKey);
            }
            if (typeof(TK) == typeof(double) && !double.IsNaN(Unsafe.As<TK, double>(ref key)))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref double firstIntRef = ref Unsafe.As<TK, double>(ref firstKeyRef);
                ReadOnlySpan<double> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                double intKey = Unsafe.As<TK, double>(ref key);
                return Scanners.FindFirstGreaterOrEqualFloat(intKeys, intKey);
            }

            return BinarySearchKeys(node.GetKeys(), key, comparer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int FindRoutingIndex<TK>(InternalNode<TK> node, TK key, IComparer<TK> comparer)
        {   
            if (typeof(TK) == typeof(int))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref int firstIntRef = ref Unsafe.As<TK, int>(ref firstKeyRef);
                ReadOnlySpan<int> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                int intKey = Unsafe.As<TK, int>(ref key);
                return Scanners.FindFirstGreaterInt(intKeys, intKey);
            }
            if (typeof(TK) == typeof(uint))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref uint firstIntRef = ref Unsafe.As<TK, uint>(ref firstKeyRef);
                ReadOnlySpan<uint> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                uint intKey = Unsafe.As<TK, uint>(ref key);
                return Scanners.FindFirstGreaterInt(intKeys, intKey);
            }
            if (typeof(TK) == typeof(long))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref long firstIntRef = ref Unsafe.As<TK, long>(ref firstKeyRef);
                ReadOnlySpan<long> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                long intKey = Unsafe.As<TK, long>(ref key);
                return Scanners.FindFirstGreaterInt(intKeys, intKey);
            }
            if (typeof(TK) == typeof(ulong))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref ulong firstIntRef = ref Unsafe.As<TK, ulong>(ref firstKeyRef);
                ReadOnlySpan<ulong> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                ulong intKey = Unsafe.As<TK, ulong>(ref key);
                return Scanners.FindFirstGreaterInt(intKeys, intKey);
            }
            if (typeof(TK) == typeof(short))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref short firstIntRef = ref Unsafe.As<TK, short>(ref firstKeyRef);
                ReadOnlySpan<short> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                short intKey = Unsafe.As<TK, short>(ref key);
                return Scanners.FindFirstGreaterInt(intKeys, intKey);
            }
            if (typeof(TK) == typeof(ushort))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref ushort firstIntRef = ref Unsafe.As<TK, ushort>(ref firstKeyRef);
                ReadOnlySpan<ushort> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                ushort intKey = Unsafe.As<TK, ushort>(ref key);
                return Scanners.FindFirstGreaterInt(intKeys, intKey);
            }
            if (typeof(TK) == typeof(float) && !float.IsNaN(Unsafe.As<TK, float>(ref key)))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref float firstIntRef = ref Unsafe.As<TK, float>(ref firstKeyRef);
                ReadOnlySpan<float> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                float intKey = Unsafe.As<TK, float>(ref key);
                return Scanners.FindFirstGreaterFloat(intKeys, intKey);
            }
            if (typeof(TK) == typeof(double) && !double.IsNaN(Unsafe.As<TK, double>(ref key)))
            {
                Span<TK> keys = node.GetKeys();
                ref TK firstKeyRef = ref MemoryMarshal.GetReference(keys);
                ref double firstIntRef = ref Unsafe.As<TK, double>(ref firstKeyRef);
                ReadOnlySpan<double> intKeys = MemoryMarshal.CreateReadOnlySpan(ref firstIntRef, keys.Length);
                double intKey = Unsafe.As<TK, double>(ref key);
                return Scanners.FindFirstGreaterFloat(intKeys, intKey);
            }

            return BinaryRoutingKeys(node.GetKeys(), key, comparer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BinarySearchKeys<TK>(ReadOnlySpan<TK> keys, TK key, IComparer<TK> comparer)
        {
            int low = 0;
            int high = keys.Length - 1;
            ref TK keysRef = ref MemoryMarshal.GetReference(keys);

            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                TK midKey = Unsafe.Add(ref keysRef, mid);
                int cmp = comparer.Compare(midKey, key);
        
                if (cmp == 0) return mid;
                if (cmp < 0) low = mid + 1;
                else high = mid - 1;
            }
            return low;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BinaryRoutingKeys<TK>(ReadOnlySpan<TK> keys, TK key, IComparer<TK> comparer)
        {
            int low = 0;
            int high = keys.Length - 1;
            ref TK keysRef = ref MemoryMarshal.GetReference(keys);
            
            while (low <= high)
            {
                int mid = low + ((high - low) >> 1);
                TK midKey = Unsafe.Add(ref keysRef, mid);
                int cmp = comparer.Compare(midKey, key);
                
                if (cmp <= 0) low = mid + 1;
                else high = mid - 1;
            }
            return low;
        }
    }
}
