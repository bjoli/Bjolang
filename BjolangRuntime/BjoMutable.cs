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

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bjolang.Runtime;

// What the mutable collections need that Bjolang cannot write.
//
// `std/mutable/vec`, `set` and `heap` import the members of `List<T>`,
// `HashSet<T>` and `PriorityQueue<T,P>` themselves. What is left here:
//
// - The map, `MutableMapModule`: its `null` key is kept in a subclass of
//   `Dictionary<K,V>`, and `AddOrExisting` takes a `ref` to the entry.
// - The walks of the map and the set: a .NET struct enumerator in a heap cell,
//   which a caller can hold and step.
//
// A partial answer is `bool TryXyz(..., out ...)`, the outs marked
// `[MaybeNullWhen(false)]`, as in the rest of the runtime's interfaces.
// Bjolang imports it with `(out T)` and gets an `Option`.
//
// Equality is .NET's throughout, `EqualityComparer<T>.Default`. That is the
// same equality `Map` and `Set` file their keys under; it is not the `Eq`
// trait in `std/eq`.

// ---------------------------------------------------------------------------
// MutableMap — Dictionary<K,V>
// ---------------------------------------------------------------------------

/// The `Dictionary<K,V>` that `mutablemap` makes: the table, and a slot beside
/// it for the key `null`.
///
/// <remarks>
/// .NET's `Dictionary` takes no `null` key, and a union's empty case is `null`
/// at run time (see <see cref="NullCaseAttribute"/>), so the entry for that key
/// is kept here. The type is still a `Dictionary<K,V>`, so an extern that takes
/// one accepts it; such an extern does not see the `null` entry.
/// </remarks>
[NonUserCode]
public sealed class NullKeyDictionary<K, V> : Dictionary<K, V> where K : notnull {
    public bool HasNull;
    public V NullValue = default!;

    public NullKeyDictionary() { }

    public NullKeyDictionary(int capacity) : base(capacity) { }

    public NullKeyDictionary(Dictionary<K, V> other) : base(other) { }
}

/// <remarks>
/// Every function tests for a `null` key before it goes to the table. The test
/// is `!typeof(K).IsValueType &amp;&amp; key is null`, which the JIT removes
/// where `K` is a struct, and which is one compare where it is a class.
/// A `Dictionary` made elsewhere has no slot: it holds no `null` key, and
/// writing one there is the error .NET gives.
/// </remarks>
[NonUserCode]
public static class MutableMapModule {
    public static Dictionary<K, V> Empty<K, V>() where K : notnull => new NullKeyDictionary<K, V>();

    public static Dictionary<K, V> WithCapacity<K, V>(int capacity) where K : notnull =>
        new NullKeyDictionary<K, V>(capacity);

    /// Later pairs win over earlier ones, so this and repeated `Set` agree.
    public static Dictionary<K, V> FromEnumerable<K, V>(IEnumerable<(K key, V value)> pairs) where K : notnull {
        var map = new NullKeyDictionary<K, V>();
        foreach (var (key, value) in pairs) Set(map, key, value);
        return map;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsNull<K>(K key) => !typeof(K).IsValueType && key is null;

    /// The `null` key's entry, if the map holds one.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool HasNull<K, V>(Dictionary<K, V> map) where K : notnull =>
        !typeof(K).IsValueType && map is NullKeyDictionary<K, V> { HasNull: true };

    /// The slot to write the `null` key to.
    static NullKeyDictionary<K, V> Slot<K, V>(Dictionary<K, V> map) where K : notnull =>
        map as NullKeyDictionary<K, V>
        ?? throw new ArgumentNullException("key", "This Dictionary was not made by mutablemap, and it takes no null key.");

    /// The entries as pairs. `Dictionary` enumerates `KeyValuePair`, which has
    /// no Bjolang spelling, so the pair is rebuilt as a tuple.
    public static IEnumerable<(K, V)> AsEnumerable<K, V>(Dictionary<K, V> map) where K : notnull {
        if (HasNull(map)) yield return (default!, ((NullKeyDictionary<K, V>)map).NullValue);
        foreach (var entry in map) yield return (entry.Key, entry.Value);
    }

    public static Dictionary<K, V> Copy<K, V>(Dictionary<K, V> map) where K : notnull {
        var copy = new NullKeyDictionary<K, V>(map);
        if (map is NullKeyDictionary<K, V> source) {
            copy.HasNull = source.HasNull;
            copy.NullValue = source.NullValue;
        }
        return copy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Count<K, V>(Dictionary<K, V> map) where K : notnull =>
        HasNull(map) ? map.Count + 1 : map.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEmpty<K, V>(Dictionary<K, V> map) where K : notnull => Count(map) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContainsKey<K, V>(Dictionary<K, V> map, K key) where K : notnull =>
        IsNull(key) ? HasNull(map) : map.ContainsKey(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static V Get<K, V>(Dictionary<K, V> map, K key) where K : notnull {
        if (IsNull(key)) {
            return HasNull(map)
                ? ((NullKeyDictionary<K, V>)map).NullValue
                : throw new KeyNotFoundException("The null key was not present in the dictionary.");
        }
        return map[key];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGet<K, V>(Dictionary<K, V> map, K key, [MaybeNullWhen(false)] out V value) where K : notnull {
        if (IsNull(key)) {
            var found = HasNull(map);
            value = found ? ((NullKeyDictionary<K, V>)map).NullValue : default;
            return found;
        }
        return map.TryGetValue(key, out value);
    }

    public static V GetOr<K, V>(Dictionary<K, V> map, K key, V fallback) where K : notnull =>
        TryGet(map, key, out var value) ? value : fallback;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set<K, V>(Dictionary<K, V> map, K key, V value) where K : notnull {
        if (IsNull(key)) {
            var slot = Slot(map);
            slot.HasNull = true;
            slot.NullValue = value;
            return;
        }
        map[key] = value;
    }

    /// Adds the entry when the key is new, and answers false. When the key is
    /// there already, changes nothing, and answers true with the value it has.
    /// One lookup in the table either way.
    public static bool AddOrExisting<K, V>(Dictionary<K, V> map, K key, V value, [MaybeNullWhen(false)] out V existing) where K : notnull {
        if (IsNull(key)) {
            if (HasNull(map)) {
                existing = ((NullKeyDictionary<K, V>)map).NullValue;
                return true;
            }
            Set(map, key, value);
            existing = default;
            return false;
        }
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out bool exists);
        if (exists) {
            existing = slot!;
            return true;
        }
        slot = value;
        existing = default;
        return false;
    }

    /// Adds the entry, which must be new.
    public static void AddNew<K, V>(Dictionary<K, V> map, K key, V value) where K : notnull {
        if (AddOrExisting(map, key, value, out _))
            throw new ArgumentException($"The key is already in the map: {key}", nameof(key));
    }

    public static bool Remove<K, V>(Dictionary<K, V> map, K key) where K : notnull {
        if (IsNull(key)) {
            if (!HasNull(map)) return false;
            var slot = (NullKeyDictionary<K, V>)map;
            slot.HasNull = false;
            slot.NullValue = default!;
            return true;
        }
        return map.Remove(key);
    }

    public static void Clear<K, V>(Dictionary<K, V> map) where K : notnull {
        map.Clear();
        if (map is NullKeyDictionary<K, V> slot) {
            slot.HasNull = false;
            slot.NullValue = default!;
        }
    }

    /// A view: the `null` key is looked for when the walk starts.
    public static IEnumerable<K> Keys<K, V>(Dictionary<K, V> map) where K : notnull =>
        typeof(K).IsValueType ? map.Keys : KeysWithNull(map);

    static IEnumerable<K> KeysWithNull<K, V>(Dictionary<K, V> map) where K : notnull {
        if (HasNull(map)) yield return default!;
        foreach (var key in map.Keys) yield return key;
    }

    public static IEnumerable<V> Values<K, V>(Dictionary<K, V> map) where K : notnull =>
        typeof(K).IsValueType ? map.Values : ValuesWithNull(map);

    static IEnumerable<V> ValuesWithNull<K, V>(Dictionary<K, V> map) where K : notnull {
        if (HasNull(map)) yield return ((NullKeyDictionary<K, V>)map).NullValue;
        foreach (var value in map.Values) yield return value;
    }

    // --- Walking -----------------------------------------------------------

    public static MutableMapEnumerator<K, V> Enumerator<K, V>(Dictionary<K, V> map) where K : notnull => new(map);

    /// .NET's `MoveNext`: steps to the next entry, and answers whether there is
    /// one. The `null` key's entry comes first.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MoveNext<K, V>(MutableMapEnumerator<K, V> e) where K : notnull {
        if (!typeof(K).IsValueType && e.NullState != MutableMapEnumerator<K, V>.InTable) {
            if (e.NullState == MutableMapEnumerator<K, V>.BeforeNull) {
                e.NullState = MutableMapEnumerator<K, V>.OnNull;
                return true;
            }
            e.NullState = MutableMapEnumerator<K, V>.InTable;
        }
        return e.Enumerator.MoveNext();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (K, V) Current<K, V>(MutableMapEnumerator<K, V> e) where K : notnull {
        if (!typeof(K).IsValueType && e.NullState == MutableMapEnumerator<K, V>.OnNull)
            return (default!, e.NullValue);
        var entry = e.Enumerator.Current;
        return (entry.Key, entry.Value);
    }
}

/// One walk of a `Dictionary<K,V>`, with .NET's protocol: `MoveNext`, then
/// `Current`. Stepping it changes it for everyone who holds it, which is why
/// it is not called a cursor.
///
/// `Dictionary<K,V>.Enumerator` is a struct, so every copy advances
/// independently — useless to a caller that has to *hold* the walk. This is
/// that struct in a heap cell: one allocation for the walk, none per element.
[NonUserCode]
public sealed class MutableMapEnumerator<K, V> where K : notnull {
    public const byte InTable = 0;
    public const byte BeforeNull = 1;
    public const byte OnNull = 2;

    public Dictionary<K, V>.Enumerator Enumerator;

    /// Where the walk is relative to the `null` key's entry, which is not in
    /// the table (see <see cref="NullKeyDictionary{K,V}"/>).
    public byte NullState;
    public V NullValue = default!;

    public MutableMapEnumerator(Dictionary<K, V> map) {
        Enumerator = map.GetEnumerator();
        if (map is NullKeyDictionary<K, V> { HasNull: true } slot) {
            NullState = BeforeNull;
            NullValue = slot.NullValue;
        }
    }
}

// ---------------------------------------------------------------------------
// MutableSet — HashSet<T>
// ---------------------------------------------------------------------------

/// The walk of a `HashSet<T>`, which `std/mutable/set` imports. The rest of
/// the set is imported from `HashSet<T>` itself.
[NonUserCode]
public static class MutableSetModule {
    // --- Walking -----------------------------------------------------------

    public static MutableSetEnumerator<T> Enumerator<T>(HashSet<T> set) => new(set);

    /// .NET's `MoveNext`: steps to the next element, and answers whether there
    /// is one.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MoveNext<T>(MutableSetEnumerator<T> e) => e.Enumerator.MoveNext();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Current<T>(MutableSetEnumerator<T> e) => e.Enumerator.Current;
}

/// One walk of a `HashSet<T>`. A struct enumerator in a heap cell, for the
/// reason `MutableMapEnumerator` gives.
[NonUserCode]
public sealed class MutableSetEnumerator<T> {
    public HashSet<T>.Enumerator Enumerator;

    public MutableSetEnumerator(HashSet<T> set) {
        Enumerator = set.GetEnumerator();
    }
}
