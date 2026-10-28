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
}
