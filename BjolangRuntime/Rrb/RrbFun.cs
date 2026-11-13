using System;
using System.Collections.Generic;

namespace Bjolang.Runtime.Rrb;

/// <summary>
/// Provides static functional-style helpers for <see cref="RrbList{T}"/>,
/// where the first argument is the list.
/// </summary>
[NonUserCode]
public static class RrbFun
{
    // --- Creation ---
    public static RrbList<T> Empty<T>() => RrbList<T>.Empty;

    public static RrbList<T> Create<T>(IEnumerable<T> items) => RrbList<T>.Create(items);

    // --- Element access ---
    public static T Get<T>(RrbList<T> list, int index) => list[index];

    public static RrbList<T> SetItem<T>(RrbList<T> list, int index, T value) => list.SetItem(index, value);

    // --- Addition / Insertion / Removal ---
    public static RrbList<T> Add<T>(RrbList<T> list, T item) => list.Add(item);

    public static RrbList<T> Prepend<T>(RrbList<T> list, T item) => list.Prepend(item);

    public static RrbList<T> Pop<T>(RrbList<T> list) => list.Pop();

    public static RrbList<T> PopFirst<T>(RrbList<T> list) => list.PopFirst();

    // --- Merging ---
    public static RrbList<T> Merge<T>(RrbList<T> list, RrbList<T> other, bool pure = false) => list.Merge(other, pure);

    // --- Higher-order functions ---
    public static RrbList<TResult> Map<T, TResult>(RrbList<T> list, Func<T, TResult> mapper) => list.Map(mapper);

    public static RrbList<TResult> MapShape<T, TResult>(RrbList<T> list, TResult[] items) => list.MapShape(items);

    public static RrbList<T> Filter<T>(RrbList<T> list, Func<T, bool> predicate) => list.Filter(predicate);

    public static TState Fold<T, TState>(RrbList<T> list, TState seed, Func<T, TState, TState> func) => list.Fold(seed, func);

    public static T Reduce<T>(RrbList<T> list, Func<T, T, T> func) => list.Reduce(func);

    public static T? Find<T>(RrbList<T> list, Func<T, bool> predicate) => list.Find(predicate);

    public static void ForEach<T>(RrbList<T> list, Action<T> action, int index = 0, int count = -1) =>
        list.ForEach(action, index, count);

    public static bool Iter<T>(RrbList<T> list, Func<T, bool> action) => list.Iter(action);

    // --- Copying / Counting / Searching ---
    public static int Count<T>(RrbList<T> list) => list.Count;

    public static bool Contains<T>(RrbList<T> list, T item) => list.Contains(item);

    public static void CopyTo<T>(RrbList<T> list, T[] array, int arrayIndex) => list.CopyTo(array, arrayIndex);

    public static void CopyRange<T>(RrbList<T> list, int sourceIndex, T[] destination, int destinationIndex, int count) =>
        list.CopyTo(sourceIndex, destination, destinationIndex, count);

    // --- Utility ---
    public static RrbBuilder<T> ToBuilder<T>(RrbList<T> list, int leafCapacity = Constants.RRB_BRANCHING) =>
        list.ToBuilder(leafCapacity);

    public static RrbList<T> Compact<T>(RrbList<T> list) => list.Compact();

    public static string ToString<T>(RrbList<T> list) => list.ToString();
}
