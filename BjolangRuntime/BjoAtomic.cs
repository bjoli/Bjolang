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

using System.Reflection;
using System.Runtime.CompilerServices;

namespace Bjolang.Runtime;

// The atomics of (std atomic).
//
// A Bjolang `(Atomic T)` is an `Atomic<T>`. Generic code holds it through this
// base class, so the operations for any T are virtual calls. `AtomicModule.Make`
// chooses the class for T:
//
//   int, uint, long, ulong   AtomicInt32, AtomicUInt32, AtomicInt64, AtomicUInt64
//   other reference types    AtomicBox<T>
//   other value types        AtomicStructBox<T>
//
// Every class is a class and not a struct. An atomic that is copied must still
// be the same atomic.
//
// The integer operations of the trait `AtomicInteger` call the `AtomicModule`
// overloads for one integer type. Each overload casts to the sealed class and
// calls a method that is not virtual, so a call at a known integer type is a
// direct call to `Interlocked`.
//
// Equality in compare-and-swap: a reference type compares by identity, as
// `eq?` does. A value type, the four integers included, compares by value.

/// <summary>An atomic cell. Make one with <see cref="AtomicModule.Make{T}"/>.</summary>
public abstract class Atomic<T>
{
    // Only this assembly makes subclasses. `AtomicModule` depends on this: it
    // casts every Atomic<int> to AtomicInt32, and so on.
    private protected Atomic() { }

    public abstract T Ref();

    public abstract void Set(T value);

    /// Stores `value` and returns the value before.
    public abstract T Swap(T value);

    /// Stores `desired` if the value is `expected`. Returns the value before
    /// the call; the store happened if and only if that value equals
    /// `expected`.
    public abstract T CompareAndSwap(T expected, T desired);

    /// Stores `f` of the value, and retries if another thread stored a value
    /// in between. Returns the value stored. `f` can run more than once.
    public abstract T Update(Func<T, T> f);
}

public sealed class AtomicInt32 : Atomic<int>
{
    private int _value;

    internal AtomicInt32(int value) => _value = value;

    public override int Ref() => Volatile.Read(ref _value);
    public override void Set(int value) => Interlocked.Exchange(ref _value, value);
    public override int Swap(int value) => Interlocked.Exchange(ref _value, value);
    public override int CompareAndSwap(int expected, int desired) =>
        Interlocked.CompareExchange(ref _value, desired, expected);

    public override int Update(Func<int, int> f)
    {
        var seen = Volatile.Read(ref _value);
        while (true)
        {
            var next = f(seen);
            var found = Interlocked.CompareExchange(ref _value, next, seen);
            if (found == seen) return next;
            seen = found;
        }
    }

    public int Increment() => Interlocked.Increment(ref _value);
    public int Decrement() => Interlocked.Decrement(ref _value);
    public int Add(int n) => Interlocked.Add(ref _value, n);
    public int Sub(int n) => Interlocked.Add(ref _value, unchecked(-n));
}

public sealed class AtomicUInt32 : Atomic<uint>
{
    private uint _value;

    internal AtomicUInt32(uint value) => _value = value;

    public override uint Ref() => Volatile.Read(ref _value);
    public override void Set(uint value) => Interlocked.Exchange(ref _value, value);
    public override uint Swap(uint value) => Interlocked.Exchange(ref _value, value);
    public override uint CompareAndSwap(uint expected, uint desired) =>
        Interlocked.CompareExchange(ref _value, desired, expected);

    public override uint Update(Func<uint, uint> f)
    {
        var seen = Volatile.Read(ref _value);
        while (true)
        {
            var next = f(seen);
            var found = Interlocked.CompareExchange(ref _value, next, seen);
            if (found == seen) return next;
            seen = found;
        }
    }

    public uint Increment() => Interlocked.Increment(ref _value);
    public uint Decrement() => Interlocked.Decrement(ref _value);
    public uint Add(uint n) => Interlocked.Add(ref _value, n);
    public uint Sub(uint n) => Interlocked.Add(ref _value, unchecked(0u - n));
}

public sealed class AtomicInt64 : Atomic<long>
{
    private long _value;

    internal AtomicInt64(long value) => _value = value;

    // Interlocked.Read and not a plain read: a 64-bit read can tear on a
    // 32-bit platform.
    public override long Ref() => Interlocked.Read(ref _value);
    public override void Set(long value) => Interlocked.Exchange(ref _value, value);
    public override long Swap(long value) => Interlocked.Exchange(ref _value, value);
    public override long CompareAndSwap(long expected, long desired) =>
        Interlocked.CompareExchange(ref _value, desired, expected);

    public override long Update(Func<long, long> f)
    {
        var seen = Interlocked.Read(ref _value);
        while (true)
        {
            var next = f(seen);
            var found = Interlocked.CompareExchange(ref _value, next, seen);
            if (found == seen) return next;
            seen = found;
        }
    }

    public long Increment() => Interlocked.Increment(ref _value);
    public long Decrement() => Interlocked.Decrement(ref _value);
    public long Add(long n) => Interlocked.Add(ref _value, n);
    public long Sub(long n) => Interlocked.Add(ref _value, unchecked(-n));
}

public sealed class AtomicUInt64 : Atomic<ulong>
{
    private ulong _value;

    internal AtomicUInt64(ulong value) => _value = value;

    // Interlocked.Read and not a plain read: a 64-bit read can tear on a
    // 32-bit platform.
    public override ulong Ref() => Interlocked.Read(ref _value);
    public override void Set(ulong value) => Interlocked.Exchange(ref _value, value);
    public override ulong Swap(ulong value) => Interlocked.Exchange(ref _value, value);
    public override ulong CompareAndSwap(ulong expected, ulong desired) =>
        Interlocked.CompareExchange(ref _value, desired, expected);

    public override ulong Update(Func<ulong, ulong> f)
    {
        var seen = Interlocked.Read(ref _value);
        while (true)
        {
            var next = f(seen);
            var found = Interlocked.CompareExchange(ref _value, next, seen);
            if (found == seen) return next;
            seen = found;
        }
    }

    public ulong Increment() => Interlocked.Increment(ref _value);
    public ulong Decrement() => Interlocked.Decrement(ref _value);
    public ulong Add(ulong n) => Interlocked.Add(ref _value, n);
    public ulong Sub(ulong n) => Interlocked.Add(ref _value, unchecked(0UL - n));
}

/// A reference type. Compare-and-swap compares by identity.
public sealed class AtomicBox<T> : Atomic<T> where T : class
{
    private T _value;

    private AtomicBox(T value) => _value = value;

    // Called through reflection by AtomicModule.Factory, which cannot name
    // `where T : class` from an unconstrained T.
    internal static Atomic<T> Create(T value) => new AtomicBox<T>(value);

    public override T Ref() => Volatile.Read(ref _value);

    // Interlocked.Exchange and not Volatile.Write: a full fence, so a store is
    // ordered with the loads and stores after it too.
    public override void Set(T value) => Interlocked.Exchange(ref _value, value);
    public override T Swap(T value) => Interlocked.Exchange(ref _value, value);
    public override T CompareAndSwap(T expected, T desired) =>
        Interlocked.CompareExchange(ref _value, desired, expected);

    public override T Update(Func<T, T> f)
    {
        var seen = Volatile.Read(ref _value);
        while (true)
        {
            var next = f(seen);
            var found = Interlocked.CompareExchange(ref _value, next, seen);
            if (ReferenceEquals(found, seen)) return next;
            seen = found;
        }
    }
}

/// A value type. The value is in an immutable cell, and a store swaps the
/// cell. Compare-and-swap compares the values with
/// `EqualityComparer<T>.Default`.
public sealed class AtomicStructBox<T> : Atomic<T> where T : struct
{
    private sealed class Cell
    {
        internal readonly T V;
        internal Cell(T v) => V = v;
    }

    private Cell _cell;

    private AtomicStructBox(T value) => _cell = new Cell(value);

    // Called through reflection by AtomicModule.Factory, which cannot name
    // `where T : struct` from an unconstrained T.
    internal static Atomic<T> Create(T value) => new AtomicStructBox<T>(value);

    public override T Ref() => Volatile.Read(ref _cell).V;
    public override void Set(T value) => Interlocked.Exchange(ref _cell, new Cell(value));
    public override T Swap(T value) => Interlocked.Exchange(ref _cell, new Cell(value)).V;

    // The loop is necessary. Another thread can store an equal value in a new
    // cell between the read and the exchange. Then the exchange fails on the
    // cell, but the value still equals `expected`, so the swap must be tried
    // again against the new cell.
    public override T CompareAndSwap(T expected, T desired)
    {
        var cell = Volatile.Read(ref _cell);
        Cell? fresh = null;
        while (true)
        {
            if (!EqualityComparer<T>.Default.Equals(cell.V, expected)) return cell.V;
            fresh ??= new Cell(desired);
            var found = Interlocked.CompareExchange(ref _cell, fresh, cell);
            if (ReferenceEquals(found, cell)) return cell.V;
            cell = found;
        }
    }

    public override T Update(Func<T, T> f)
    {
        var cell = Volatile.Read(ref _cell);
        while (true)
        {
            var next = new Cell(f(cell.V));
            var found = Interlocked.CompareExchange(ref _cell, next, cell);
            if (ReferenceEquals(found, cell)) return next.V;
            cell = found;
        }
    }
}

public static class AtomicModule
{
    /// An atomic that holds `value`, of the class for T.
    ///
    /// The JIT compiles this method once for each value type T, and in that
    /// copy the `typeof` tests are constants. So for `int` only the first
    /// branch is left, and the casts through `object` do not box.
    public static Atomic<T> Make<T>(T value)
    {
        if (typeof(T) == typeof(int)) return (Atomic<T>)(object)new AtomicInt32((int)(object)value!);
        if (typeof(T) == typeof(uint)) return (Atomic<T>)(object)new AtomicUInt32((uint)(object)value!);
        if (typeof(T) == typeof(long)) return (Atomic<T>)(object)new AtomicInt64((long)(object)value!);
        if (typeof(T) == typeof(ulong)) return (Atomic<T>)(object)new AtomicUInt64((ulong)(object)value!);
        return Factory<T>.Create(value);
    }

    // The constructor of AtomicBox<T> or AtomicStructBox<T>, found once for
    // each T.
    private static class Factory<T>
    {
        internal static readonly Func<T, Atomic<T>> Create = Find();

        private static Func<T, Atomic<T>> Find()
        {
            var open = typeof(T).IsValueType ? typeof(AtomicStructBox<>) : typeof(AtomicBox<>);
            var create = open.MakeGenericType(typeof(T))
                .GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!;
            return create.CreateDelegate<Func<T, Atomic<T>>>();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Ref<T>(Atomic<T> a) => a.Ref();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set<T>(Atomic<T> a, T value) => a.Set(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Swap<T>(Atomic<T> a, T value) => a.Swap(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T CompareAndSwap<T>(Atomic<T> a, T expected, T desired) =>
        a.CompareAndSwap(expected, desired);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Update<T>(Atomic<T> a, Func<T, T> f) => a.Update(f);

    // The integer operations, one overload for each integer type.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Increment(Atomic<int> a) => ((AtomicInt32)a).Increment();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Decrement(Atomic<int> a) => ((AtomicInt32)a).Decrement();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Add(Atomic<int> a, int n) => ((AtomicInt32)a).Add(n);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Sub(Atomic<int> a, int n) => ((AtomicInt32)a).Sub(n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Increment(Atomic<uint> a) => ((AtomicUInt32)a).Increment();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Decrement(Atomic<uint> a) => ((AtomicUInt32)a).Decrement();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Add(Atomic<uint> a, uint n) => ((AtomicUInt32)a).Add(n);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Sub(Atomic<uint> a, uint n) => ((AtomicUInt32)a).Sub(n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Increment(Atomic<long> a) => ((AtomicInt64)a).Increment();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Decrement(Atomic<long> a) => ((AtomicInt64)a).Decrement();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Add(Atomic<long> a, long n) => ((AtomicInt64)a).Add(n);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Sub(Atomic<long> a, long n) => ((AtomicInt64)a).Sub(n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Increment(Atomic<ulong> a) => ((AtomicUInt64)a).Increment();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Decrement(Atomic<ulong> a) => ((AtomicUInt64)a).Decrement();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Add(Atomic<ulong> a, ulong n) => ((AtomicUInt64)a).Add(n);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Sub(Atomic<ulong> a, ulong n) => ((AtomicUInt64)a).Sub(n);
}
