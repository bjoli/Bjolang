namespace Bjolang.Runtime.Rrb;

public sealed partial class RrbList<T>
{
    /// <summary>
    ///     The pieces' items in order, as one list.
    /// </summary>
    /// <remarks>
    ///     A merge costs about the same whatever the size of the pieces, and
    ///     shares their trees: for a big piece it is much cheaper than copying
    ///     it. For a piece of tens of items, copying is cheaper, and gives full
    ///     leaves where merges give part-full ones under size tables. So a
    ///     piece of at least <see cref="ConcatMergeAtLeast"/> items is merged,
    ///     and a smaller one is copied into the leaves of the result.
    ///     <para>
    ///     Measured on 1M items: 50 000 pieces of 20 took 15-18 ms by a merge
    ///     at a time, and a run of 1000 pieces of 1000 took 2-3.6 ms; the
    ///     copies are faster on the first, the merges on the second.
    ///     </para>
    /// </remarks>
    public static RrbList<T> Concat(ReadOnlySpan<RrbList<T>> pieces)
    {
        // The result so far: `done`, with the copied pieces after it in
        // `builder`, which is loaded from `done` so that they go into the
        // same tree.
        var done = Empty;
        RrbBuilder<T>? builder = null;
        foreach (var piece in pieces)
        {
            if (piece.Count == 0) continue;
            if (piece.Count >= ConcatMergeAtLeast)
            {
                if (builder != null)
                {
                    done = builder.ToImmutable();
                    builder = null;
                }

                done = done.Merge(piece);
            }
            else
            {
                builder ??= new RrbBuilder<T>(done);
                for (var index = 0; index < piece.Count;)
                {
                    var items = piece.LeafAt(index, out var position, out var length);
                    builder.AppendRange(items.AsSpan(position, length - position));
                    index += length - position;
                }
            }
        }

        return builder?.ToImmutable() ?? done;
    }

    /// <summary>
    ///     The size from which <see cref="Concat"/> merges a piece rather than
    ///     copy it.
    /// </summary>
    public const int ConcatMergeAtLeast = 256;
}
