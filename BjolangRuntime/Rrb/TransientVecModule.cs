namespace Bjolang.Runtime.Rrb;

/// Bjolang's TransientVec: a builder under a type of its own, so that it and
/// VecBuilder are different types in Bjolang. It adds nothing: the ends and
/// the sharing are the builder's.
[NonUserCode]
public sealed class RrbTransient<T> : RrbBuilder<T>
{
    public RrbTransient()
    {
    }

    internal RrbTransient(RrbList<T> list) : base(list)
    {
    }
}

/// What Bjolang's transientvec- functions call: a transient changed in place
/// at both ends and at any index, and turned into a list in O(1).
[NonUserCode]
public static class TransientVecModule
{
    public static RrbTransient<T> Empty<T>() => new RrbTransient<T>();

    public static RrbTransient<T> FromList<T>(RrbList<T> list) => new RrbTransient<T>(list);

    /// The list as it stands. The transient stays usable, and copies what it
    /// handed over before it changes it.
    public static RrbList<T> ToList<T>(RrbTransient<T> t) => t.ToImmutable();

    public static int Count<T>(RrbTransient<T> t) => t.Count;

    public static bool IsEmpty<T>(RrbTransient<T> t) => t.Count == 0;

    // Checked here because the builder's indexer is not: past the length but
    // inside the tail array it answers whatever the slot holds.
    public static T Ref<T>(RrbTransient<T> t, int index)
    {
        if ((uint)index >= (uint)t.Count) throw new IndexOutOfRangeException();
        return t[index];
    }

    public static void Set<T>(RrbTransient<T> t, int index, T item)
    {
        if ((uint)index >= (uint)t.Count) throw new IndexOutOfRangeException();
        t.SetItem(index, item);
    }

    public static void Add<T>(RrbTransient<T> t, T item) => t.Add(item);

    public static void AddRange<T>(RrbTransient<T> t, IEnumerable<T> items)
    {
        foreach (var item in items) t.Add(item);
    }

    public static void Prepend<T>(RrbTransient<T> t, T item) => t.Prepend(item);

    public static T Pop<T>(RrbTransient<T> t) => t.Pop();

    public static T PopFirst<T>(RrbTransient<T> t) => t.PopFirst();
}
