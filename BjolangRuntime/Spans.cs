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

// Spans: what the prelude's span-* and ro-span-* functions call.
//
// A range is given as from and to, the end excluded, because the prelude
// takes #:from and #:to. .NET takes a start and a length, so each converts.
// Span<T> and ReadOnlySpan<T> have methods of their own here, because
// Bjolang converts neither to the other implicitly.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bjolang.Runtime;

[NonUserCode]
public static class Spans {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<T> FromArray<T>(T[] array, int from, int to) =>
        new(array, from, to - from);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<T> ReadOnlyFromArray<T>(T[] array, int from, int to) =>
        new(array, from, to - from);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Length<T>(Span<T> span) => span.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Length<T>(ReadOnlySpan<T> span) => span.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Ref<T>(Span<T> span, int i) => span[i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Ref<T>(ReadOnlySpan<T> span, int i) => span[i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set<T>(Span<T> span, int i, T value) => span[i] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<T> Slice<T>(Span<T> span, int from, int to) =>
        span.Slice(from, to - from);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<T> Slice<T>(ReadOnlySpan<T> span, int from, int to) =>
        span.Slice(from, to - from);

    public static T[] ToArray<T>(Span<T> span) => span.ToArray();

    public static T[] ToArray<T>(ReadOnlySpan<T> span) => span.ToArray();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<T> ReadOnly<T>(Span<T> span) => span;

    // span-> and ro-span->: the same memory as numbers of another type.
    //
    // - Only plain numbers: no references inside, and every bit pattern is
    //   a value. Not decimal, which has bit patterns that are not values,
    //   and not BigInteger, which holds an array.
    // - A read-only span gives a read-only span. So the bytes of a string,
    //   which are only given read-only, stay read-only.
    // - The length in bytes must be a whole number of the new elements.
    //
    // Both raise a Bjolang panic. The tests of typeof are decided when the
    // JIT compiles each instantiation, so a call of plain types tests
    // nothing at run time. There are no constraints on the type parameters:
    // a generic Bjolang function over (Num %a) is a C# method over
    // INumber<T>, which is not `struct`.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<TTo> As<TFrom, TTo>(Span<TFrom> span) {
        int length = CastLength<TFrom, TTo>(span.Length);
        return MemoryMarshal.CreateSpan(
            ref Unsafe.As<TFrom, TTo>(ref MemoryMarshal.GetReference(span)), length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<TTo> As<TFrom, TTo>(ReadOnlySpan<TFrom> span) {
        int length = CastLength<TFrom, TTo>(span.Length);
        return MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<TFrom, TTo>(ref MemoryMarshal.GetReference(span)), length);
    }

    /// <summary>The number of TTo in `length` TFrom, or a panic.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CastLength<TFrom, TTo>(int length) {
        if (!IsPlain<TFrom>() || !IsPlain<TTo>())
            NotPlain<TFrom, TTo>();
        long bytes = (long)length * Unsafe.SizeOf<TFrom>();
        int size = Unsafe.SizeOf<TTo>();
        if (bytes % size != 0)
            NotWhole<TFrom, TTo>(length);
        return (int)(bytes / size);
    }

    /// <summary>Whether T is a plain number: no references, and every bit pattern a value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPlain<T>() =>
        typeof(T) == typeof(byte) || typeof(T) == typeof(sbyte)
        || typeof(T) == typeof(short) || typeof(T) == typeof(ushort)
        || typeof(T) == typeof(int) || typeof(T) == typeof(uint)
        || typeof(T) == typeof(long) || typeof(T) == typeof(ulong)
        || typeof(T) == typeof(nint) || typeof(T) == typeof(nuint)
        || typeof(T) == typeof(Int128) || typeof(T) == typeof(UInt128)
        || typeof(T) == typeof(Half) || typeof(T) == typeof(float) || typeof(T) == typeof(double);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NotPlain<TFrom, TTo>() {
        var bad = IsPlain<TFrom>() ? typeof(TTo) : typeof(TFrom);
        global::BjolangRuntime.Panic<int>(BjoString.Utf8String.FromUtf16(
            $"span->: a span of {bad.Name} cannot be seen as numbers of another type. Only the plain number types can: the integers, float and double."), 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void NotWhole<TFrom, TTo>(int length) {
        long bytes = (long)length * Unsafe.SizeOf<TFrom>();
        global::BjolangRuntime.Panic<int>(BjoString.Utf8String.FromUtf16(
            $"span->: {length} {typeof(TFrom).Name} are {bytes} bytes, which is not a whole number of {typeof(TTo).Name} of {Unsafe.SizeOf<TTo>()} bytes. Slice the span first."), 1);
    }
}
