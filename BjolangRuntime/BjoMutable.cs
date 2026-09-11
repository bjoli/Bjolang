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

using System.Runtime.CompilerServices;

namespace Bjolang.Runtime;

// The mutable collections, as static modules over the .NET ones.
//
// The values these hand back are `List<T>`, `Dictionary<K,V>`, `HashSet<T>`
// and `PriorityQueue<T,P>` themselves — nothing is wrapped — so anything else
// imported from .NET that takes one of those accepts a Bjolang mutable
// collection unchanged.
//
// A module rather than `import/extern` straight onto the BCL type, because
// three things the interop cannot reach are needed to use one at all:
// `import/class` declares no constructor for a generic class, an accessor
// import takes no index so there is no indexer, and a method with an `out`
// parameter is filtered out of overload resolution. Everything below is one of
// those three or a one-line forward.
//
// A partial answer is a tuple rather than an `out`, which is the convention the
// rest of the runtime's interfaces follow: an `out` is a C# idiom, while a
// tuple is a value in any language.
//
// Equality is .NET's throughout — `EqualityComparer<T>.Default` for the
// dictionary and the set, `Comparer<P>.Default` for the heap's priorities.
// That is the same equality `Map` and `Set` file their keys under, so the four
// agree with each other; it is not the `Eq` trait in `std/eq`.

// ---------------------------------------------------------------------------
// MutableVec — List<T>
// ---------------------------------------------------------------------------

public static class MutableVecModule {
    public static List<T> Empty<T>() => new();

    /// Room for `capacity` elements before the first regrow. The count is
    /// still zero.
    public static List<T> WithCapacity<T>(int capacity) => new(capacity);

    public static List<T> FromEnumerable<T>(IEnumerable<T> items) => new(items);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IEnumerable<T> AsEnumerable<T>(List<T> xs) => xs;

    public static List<T> Copy<T>(List<T> xs) => new(xs);

    public static T[] ToArray<T>(List<T> xs) => xs.ToArray();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count<T>(List<T> xs) => xs.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEmpty<T>(List<T> xs) => xs.Count == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Get<T>(List<T> xs, int index) => xs[index];

    public static (bool found, T value) TryGet<T>(List<T> xs, int index) =>
        index >= 0 && index < xs.Count ? (true, xs[index]) : (false, default!);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set<T>(List<T> xs, int index, T value) => xs[index] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add<T>(List<T> xs, T value) => xs.Add(value);

    public static void AddRange<T>(List<T> xs, IEnumerable<T> items) => xs.AddRange(items);

    public static void Insert<T>(List<T> xs, int index, T value) => xs.Insert(index, value);

    public static void RemoveAt<T>(List<T> xs, int index) => xs.RemoveAt(index);

    /// Removes the first element equal to `value`, and answers whether there
    /// was one.
    public static bool Remove<T>(List<T> xs, T value) => xs.Remove(value);

    /// Removes every element the predicate accepts, and answers how many went.
    public static int RemoveWhere<T>(List<T> xs, Func<T, bool> predicate) =>
        xs.RemoveAll(new Predicate<T>(predicate.Invoke));

    /// The last element, removed. The pair's first field is false when the list
    /// was empty, which is the case a stack-shaped caller has to test anyway.
    public static (bool found, T value) TryPopBack<T>(List<T> xs) {
        if (xs.Count == 0) return (false, default!);

        var last = xs.Count - 1;
        var value = xs[last];
        xs.RemoveAt(last);
        return (true, value);
    }

    public static void Clear<T>(List<T> xs) => xs.Clear();

    public static bool Contains<T>(List<T> xs, T value) => xs.Contains(value);

    /// The index of the first element equal to `value`, or -1.
    public static int IndexOf<T>(List<T> xs, T value) => xs.IndexOf(value);

    public static void Reverse<T>(List<T> xs) => xs.Reverse();

    public static void SortBy<T>(List<T> xs, Func<T, T, int> compare) =>
        xs.Sort(new Comparison<T>(compare.Invoke));
}

// ---------------------------------------------------------------------------
// MutableMap — Dictionary<K,V>
// ---------------------------------------------------------------------------

public static class MutableMapModule {
    public static Dictionary<K, V> Empty<K, V>() where K : notnull => new();

    public static Dictionary<K, V> WithCapacity<K, V>(int capacity) where K : notnull => new(capacity);

    /// Later pairs win over earlier ones, so this and repeated `Set` agree.
    public static Dictionary<K, V> FromEnumerable<K, V>(IEnumerable<(K key, V value)> pairs) where K : notnull {
        var map = new Dictionary<K, V>();
        foreach (var (key, value) in pairs) map[key] = value;
        return map;
    }

    /// The entries as pairs. `Dictionary` enumerates `KeyValuePair`, which has
    /// no Bjolang spelling, so the pair is rebuilt as a tuple.
    public static IEnumerable<(K, V)> AsEnumerable<K, V>(Dictionary<K, V> map) where K : notnull {
        foreach (var entry in map) yield return (entry.Key, entry.Value);
    }

    public static Dictionary<K, V> Copy<K, V>(Dictionary<K, V> map) where K : notnull => new(map);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count<K, V>(Dictionary<K, V> map) where K : notnull => map.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEmpty<K, V>(Dictionary<K, V> map) where K : notnull => map.Count == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContainsKey<K, V>(Dictionary<K, V> map, K key) where K : notnull => map.ContainsKey(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static V Get<K, V>(Dictionary<K, V> map, K key) where K : notnull => map[key];

    public static (bool found, V value) TryGet<K, V>(Dictionary<K, V> map, K key) where K : notnull =>
        map.TryGetValue(key, out var value) ? (true, value) : (false, default!);

    public static V GetOr<K, V>(Dictionary<K, V> map, K key, V fallback) where K : notnull =>
        map.TryGetValue(key, out var value) ? value : fallback;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set<K, V>(Dictionary<K, V> map, K key, V value) where K : notnull => map[key] = value;

    /// Sets the entry only when the key is new, and answers whether it was.
    public static bool Add<K, V>(Dictionary<K, V> map, K key, V value) where K : notnull => map.TryAdd(key, value);

    public static bool Remove<K, V>(Dictionary<K, V> map, K key) where K : notnull => map.Remove(key);

    public static void Clear<K, V>(Dictionary<K, V> map) where K : notnull => map.Clear();

    public static IEnumerable<K> Keys<K, V>(Dictionary<K, V> map) where K : notnull => map.Keys;

    public static IEnumerable<V> Values<K, V>(Dictionary<K, V> map) where K : notnull => map.Values;

    // --- Walking -----------------------------------------------------------

    public static MutableMapCursor<K, V> Cursor<K, V>(Dictionary<K, V> map) where K : notnull => new(map);

    /// Advances the cursor, and answers whether it ran off the end. The advance
    /// happens here because a walk asks "is there more?" exactly once per
    /// element, which is what lets the whole traversal allocate nothing after
    /// the cursor itself.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CursorDone<K, V>(MutableMapCursor<K, V> cursor) where K : notnull =>
        !cursor.Enumerator.MoveNext();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (K, V) CursorCurrent<K, V>(MutableMapCursor<K, V> cursor) where K : notnull {
        var entry = cursor.Enumerator.Current;
        return (entry.Key, entry.Value);
    }
}

/// A position in a walk of a `Dictionary<K,V>`.
///
/// `Dictionary<K,V>.Enumerator` is a struct, so every copy advances
/// independently — useless to a caller that has to *hold* the position. This is
/// that struct in a heap cell: one allocation for the walk, none per element.
public sealed class MutableMapCursor<K, V> where K : notnull {
    public Dictionary<K, V>.Enumerator Enumerator;

    public MutableMapCursor(Dictionary<K, V> map) {
        Enumerator = map.GetEnumerator();
    }
}

// ---------------------------------------------------------------------------
// MutableSet — HashSet<T>
// ---------------------------------------------------------------------------

public static class MutableSetModule {
    public static HashSet<T> Empty<T>() => new();

    public static HashSet<T> FromEnumerable<T>(IEnumerable<T> items) => new(items);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IEnumerable<T> AsEnumerable<T>(HashSet<T> set) => set;

    public static HashSet<T> Copy<T>(HashSet<T> set) => new(set);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count<T>(HashSet<T> set) => set.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEmpty<T>(HashSet<T> set) => set.Count == 0;

    /// Answers whether the element was new.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Add<T>(HashSet<T> set, T value) => set.Add(value);

    public static void AddRange<T>(HashSet<T> set, IEnumerable<T> items) => set.UnionWith(items);

    /// Answers whether the element was there.
    public static bool Remove<T>(HashSet<T> set, T value) => set.Remove(value);

    public static void RemoveRange<T>(HashSet<T> set, IEnumerable<T> items) => set.ExceptWith(items);

    /// Removes every element the predicate accepts, and answers how many went.
    public static int RemoveWhere<T>(HashSet<T> set, Func<T, bool> predicate) =>
        set.RemoveWhere(new Predicate<T>(predicate.Invoke));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Contains<T>(HashSet<T> set, T value) => set.Contains(value);

    public static void Clear<T>(HashSet<T> set) => set.Clear();

    // --- Set algebra, written into the receiver ----------------------------

    public static void Union<T>(HashSet<T> set, HashSet<T> other) => set.UnionWith(other);

    public static void Intersect<T>(HashSet<T> set, HashSet<T> other) => set.IntersectWith(other);

    public static void Except<T>(HashSet<T> set, HashSet<T> other) => set.ExceptWith(other);

    public static void SymmetricExcept<T>(HashSet<T> set, HashSet<T> other) => set.SymmetricExceptWith(other);

    public static bool IsSubsetOf<T>(HashSet<T> set, HashSet<T> other) => set.IsSubsetOf(other);

    public static bool IsSupersetOf<T>(HashSet<T> set, HashSet<T> other) => set.IsSupersetOf(other);

    public static bool Overlaps<T>(HashSet<T> set, HashSet<T> other) => set.Overlaps(other);

    // --- Walking -----------------------------------------------------------

    public static MutableSetCursor<T> Cursor<T>(HashSet<T> set) => new(set);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CursorDone<T>(MutableSetCursor<T> cursor) => !cursor.Enumerator.MoveNext();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T CursorCurrent<T>(MutableSetCursor<T> cursor) => cursor.Enumerator.Current;
}

/// A position in a walk of a `HashSet<T>`. A struct enumerator in a heap cell,
/// for the reason `MutableMapCursor` gives.
public sealed class MutableSetCursor<T> {
    public HashSet<T>.Enumerator Enumerator;

    public MutableSetCursor(HashSet<T> set) {
        Enumerator = set.GetEnumerator();
    }
}

// ---------------------------------------------------------------------------
// MutableHeap — PriorityQueue<T,P>
// ---------------------------------------------------------------------------

public static class MutableHeapModule {
    /// Ordered by `Comparer<P>.Default`, least priority first.
    public static PriorityQueue<T, P> Empty<T, P>() => new();

    /// Ordered by a comparison of the caller's, which is what lets a priority
    /// be ordered by the `Ord` trait rather than by .NET.
    public static PriorityQueue<T, P> WithComparison<T, P>(Func<P, P, int> compare) =>
        new(Comparer<P>.Create(new Comparison<P>(compare.Invoke)));

    public static PriorityQueue<T, P> FromEnumerable<T, P>(IEnumerable<(T element, P priority)> items) {
        var heap = new PriorityQueue<T, P>();
        heap.EnqueueRange(items);
        return heap;
    }

    /// A copy that keeps the comparer, so a heap built by `WithComparison`
    /// copies to one ordered the same way.
    public static PriorityQueue<T, P> Copy<T, P>(PriorityQueue<T, P> heap) =>
        new(heap.UnorderedItems, heap.Comparer);

    /// The contents in *no* order — heap order is only observable by popping.
    public static IEnumerable<(T, P)> AsEnumerable<T, P>(PriorityQueue<T, P> heap) {
        foreach (var (element, priority) in heap.UnorderedItems) yield return (element, priority);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count<T, P>(PriorityQueue<T, P> heap) => heap.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEmpty<T, P>(PriorityQueue<T, P> heap) => heap.Count == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add<T, P>(PriorityQueue<T, P> heap, T element, P priority) => heap.Enqueue(element, priority);

    public static void AddRange<T, P>(PriorityQueue<T, P> heap, IEnumerable<(T element, P priority)> items) =>
        heap.EnqueueRange(items);

    public static (bool found, T element, P priority) TryPeek<T, P>(PriorityQueue<T, P> heap) =>
        heap.TryPeek(out var element, out var priority) ? (true, element, priority) : (false, default!, default!);

    public static (bool found, T element, P priority) TryPop<T, P>(PriorityQueue<T, P> heap) =>
        heap.TryDequeue(out var element, out var priority) ? (true, element, priority) : (false, default!, default!);

    /// Pushes and pops as one operation, answering what came off — which may be
    /// the element just pushed. The count never grows, so this is how a heap is
    /// kept to a bound without two rebalances per element.
    public static T AddPop<T, P>(PriorityQueue<T, P> heap, T element, P priority) =>
        heap.EnqueueDequeue(element, priority);

    public static void Clear<T, P>(PriorityQueue<T, P> heap) => heap.Clear();
}
