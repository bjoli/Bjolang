using System;
using System.Runtime.CompilerServices;

namespace Bjolang.Runtime.Rrb;

// Pattern matching from the front. The items come from the head when they
// are there, which is the usual case, and the rest drops them from the head
// (see DropFront), so a loop of `(a, rest)` matches costs about a PopFirst
// per step.
[NonUserCode]
public sealed partial class RrbList<T>
{
    /// <summary>
    /// Deconstructs the list into 1 head element and a rest list.
    /// Supports C# pattern matching like: <c>list is (var a, { } rest)</c>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deconstruct(out T item1, out RrbList<T>? rest)
    {
        if (Count < 1)
        {
            item1 = default!;
            rest = null;
            return;
        }

        item1 = FrontItem(0);
        rest = DropFront(1);
    }

    /// <summary>
    /// Deconstructs the list into 2 head elements and a rest list.
    /// Supports C# pattern matching like: <c>list is (var a, var b, { } rest)</c>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deconstruct(out T item1, out T item2, out RrbList<T>? rest)
    {
        if (Count < 2)
        {
            item1 = default!;
            item2 = default!;
            rest = null;
            return;
        }

        item1 = FrontItem(0);
        item2 = FrontItem(1);
        rest = DropFront(2);
    }

    /// <summary>
    /// Deconstructs the list into 3 head elements and a rest list.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deconstruct(out T item1, out T item2, out T item3, out RrbList<T>? rest)
    {
        if (Count < 3)
        {
            item1 = default!;
            item2 = default!;
            item3 = default!;
            rest = null;
            return;
        }

        item1 = FrontItem(0);
        item2 = FrontItem(1);
        item3 = FrontItem(2);
        rest = DropFront(3);
    }

    /// <summary>
    /// Deconstructs the list into 4 head elements and a rest list.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deconstruct(out T item1, out T item2, out T item3, out T item4, out RrbList<T>? rest)
    {
        if (Count < 4)
        {
            item1 = default!;
            item2 = default!;
            item3 = default!;
            item4 = default!;
            rest = null;
            return;
        }

        item1 = FrontItem(0);
        item2 = FrontItem(1);
        item3 = FrontItem(2);
        item4 = FrontItem(3);
        rest = DropFront(4);
    }

    /// <summary>
    /// Deconstructs the list into 5 head elements and a rest list.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deconstruct(out T item1, out T item2, out T item3, out T item4, out T item5, out RrbList<T>? rest)
    {
        if (Count < 5)
        {
            item1 = default!;
            item2 = default!;
            item3 = default!;
            item4 = default!;
            item5 = default!;
            rest = null;
            return;
        }

        item1 = FrontItem(0);
        item2 = FrontItem(1);
        item3 = FrontItem(2);
        item4 = FrontItem(3);
        item5 = FrontItem(4);
        rest = DropFront(5);
    }

    /// <summary>
    /// Deconstructs the list into 6 head elements and a rest list.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deconstruct(out T item1, out T item2, out T item3, out T item4, out T item5, out T item6, out RrbList<T>? rest)
    {
        if (Count < 6)
        {
            item1 = default!;
            item2 = default!;
            item3 = default!;
            item4 = default!;
            item5 = default!;
            item6 = default!;
            rest = null;
            return;
        }

        item1 = FrontItem(0);
        item2 = FrontItem(1);
        item3 = FrontItem(2);
        item4 = FrontItem(3);
        item5 = FrontItem(4);
        item6 = FrontItem(5);
        rest = DropFront(6);
    }
}
