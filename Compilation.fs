(* This Source Code Form is subject to the terms of the Mozilla Public
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
 *)

namespace Bjolang

/// Where the state of a compilation is kept.
///
/// The compiler's passes keep counters, tables and settings in module-level
/// values. A value that is the same for a whole process is a plain `let`. A
/// value of one compilation is a `Per`, a `PerDictionary`, a `PerSet` or a
/// `PerList` of this module: its storage is in a `Context`, and each thread
/// of control has a current context.
///
/// - A process that never makes a context has one, the root. Each value then
///   has one storage, as a module-level `let mutable` has.
/// - `inNew` runs a function in a context of its own. Two threads that each
///   run in a context of their own share none of these values.
/// - The current context goes with the thread of control: a task or a thread
///   that a compilation starts has the compilation's context.
///
/// `Session` saves and restores some of these values around a compilation
/// that another compilation starts. It reads and writes the current context,
/// so it works the same in every context.
///
/// Not in a context: a cache of what the process has loaded or read from
/// disk, and a hook that one module sets once for another. See `Session` for
/// the list.
module Compilation =
    open System
    open System.Collections.Generic
    open System.Runtime.CompilerServices
    open System.Runtime.InteropServices
    open System.Threading

    /// The state of one compilation: a slot for each `Per`.
    [<AllowNullLiteral>]
    type Context internal (parent: Context) =
        let mutable slots: obj[] = Array.zeroCreate 128

        /// The context that made this one. Null for the root.
        member _.Parent = parent

        member internal _.Slots
            with get () = slots
            and set (v: obj[]) = slots <- v

    /// The storage of one `Per` in one context.
    [<AllowNullLiteral>]
    type internal Holder<'T>(initial: 'T) =
        let mutable value = initial

        member _.Value
            with get () = value
            and set (v: 'T) = value <- v

    let private root = Context(null)

    /// The current context of each thread.
    ///
    /// An `AsyncLocal` goes with the thread of control into tasks and new
    /// threads, but to read one costs a search. So the value is kept in a
    /// thread-static field too, which costs one load, and the `AsyncLocal`
    /// writes that field on each thread where its value changes.
    type private Ambient() =
        [<ThreadStatic; DefaultValue>]
        static val mutable private current: Context

        static member Current
            with get () = Ambient.current
            and set (c: Context) = Ambient.current <- c

    let private flow =
        AsyncLocal<Context>(fun (change: AsyncLocalValueChangedArgs<Context>) -> Ambient.Current <- change.CurrentValue)

    /// The context of the code that runs now.
    let current () : Context =
        match Ambient.Current with
        | null -> root
        | c -> c

    let mutable private count = 0

    let internal nextIndex () = Interlocked.Increment(&count) - 1

    /// The holder of slot `index` in `context`, made by `make` when the
    /// context does not have it yet.
    let internal holderIn<'T> (context: Context) (index: int) (make: Context -> 'T) : Holder<'T> =
        let slots = context.Slots

        if index < slots.Length && not (isNull slots[index]) then
            Unsafe.As<Holder<'T>> slots[index]
        else
            // A context is used by one compilation, but that compilation can
            // have a helper thread.
            lock context (fun () ->
                if index >= context.Slots.Length then
                    let bigger = Array.zeroCreate (max (index + 1) (context.Slots.Length * 2))
                    Array.blit context.Slots 0 bigger 0 context.Slots.Length
                    context.Slots <- bigger

                match context.Slots[index] with
                | null ->
                    let holder = Holder<'T>(make context)
                    context.Slots[index] <- holder
                    holder
                | made -> Unsafe.As<Holder<'T>> made)

    /// A value of a compilation, in place of a module-level `let mutable`.
    ///
    /// - `Per(make)`: each context starts with `make ()`.
    /// - `Per(make, true)`: a setting. A context that `inNew` makes starts
    ///   with the value that the context which made it has when the new one
    ///   first reads the setting. The root starts with `make ()`.
    ///
    /// The value is made when a context first reads or writes it.
    [<Sealed>]
    type Per<'T>(make: unit -> 'T, isSetting: bool) =
        let index = nextIndex ()

        let rec first (context: Context) : 'T =
            if isSetting && not (isNull context.Parent) then
                (holderIn context.Parent index first).Value
            else
                make ()

        new(make: unit -> 'T) = Per(make, false)

        member _.Value
            with get () : 'T = (holderIn (current ()) index first).Value
            and set (v: 'T) = (holderIn (current ()) index first).Value <- v

    /// A `Dictionary` of a compilation, with the members that the compiler
    /// uses. `Current` is the dictionary itself.
    [<Sealed>]
    type PerDictionary<'K, 'V when 'K: equality>(make: unit -> Dictionary<'K, 'V>) =
        let cell = Per<Dictionary<'K, 'V>>(make)

        new() = PerDictionary(fun () -> Dictionary<'K, 'V>())

        member _.Current = cell.Value
        member d.Count = d.Current.Count
        member d.Keys = d.Current.Keys
        member d.Values = d.Current.Values

        member d.Item
            with get (key: 'K) : 'V = d.Current[key]
            and set (key: 'K) (value: 'V) = d.Current[key] <- value

        member d.Add(key: 'K, value: 'V) = d.Current.Add(key, value)
        member d.TryAdd(key: 'K, value: 'V) = d.Current.TryAdd(key, value)
        member d.ContainsKey(key: 'K) = d.Current.ContainsKey key
        member d.Remove(key: 'K) = d.Current.Remove key
        member d.Clear() = d.Current.Clear()
        member d.TryGetValue(key: 'K, [<Out>] value: byref<'V>) = d.Current.TryGetValue(key, &value)

        interface IEnumerable<KeyValuePair<'K, 'V>> with
            member d.GetEnumerator() : IEnumerator<KeyValuePair<'K, 'V>> = d.Current.GetEnumerator()

            member d.GetEnumerator() : Collections.IEnumerator = d.Current.GetEnumerator()

    /// A `HashSet` of a compilation.
    [<Sealed>]
    type PerSet<'T>(make: unit -> HashSet<'T>) =
        let cell = Per<HashSet<'T>>(make)

        new() = PerSet(fun () -> HashSet<'T>())

        member _.Current = cell.Value
        member s.Count = s.Current.Count
        member s.Add(item: 'T) = s.Current.Add item
        member s.Contains(item: 'T) = s.Current.Contains item
        member s.Remove(item: 'T) = s.Current.Remove item
        member s.Clear() = s.Current.Clear()
        member s.UnionWith(items: seq<'T>) = s.Current.UnionWith items

        interface IEnumerable<'T> with
            member s.GetEnumerator() : IEnumerator<'T> = s.Current.GetEnumerator()
            member s.GetEnumerator() : Collections.IEnumerator = s.Current.GetEnumerator()

    /// A `ResizeArray` of a compilation.
    [<Sealed>]
    type PerList<'T>() =
        let cell = Per<List<'T>>(fun () -> List<'T>())

        member _.Current = cell.Value
        member l.Count = l.Current.Count

        member l.Item
            with get (i: int) : 'T = l.Current[i]
            and set (i: int) (value: 'T) = l.Current[i] <- value

        member l.Add(item: 'T) = l.Current.Add item
        member l.AddRange(items: seq<'T>) = l.Current.AddRange items
        member l.Clear() = l.Current.Clear()
        member l.RemoveAt(i: int) = l.Current.RemoveAt i
        member l.Remove(item: 'T) = l.Current.Remove item
        member l.RemoveRange(index: int, count: int) = l.Current.RemoveRange(index, count)
        member l.Insert(index: int, item: 'T) = l.Current.Insert(index, item)
        member l.Contains(item: 'T) = l.Current.Contains item
        member l.RemoveAll(test: 'T -> bool) = l.Current.RemoveAll(Predicate test)
        member l.ToArray() = l.Current.ToArray()

        interface IEnumerable<'T> with
            member l.GetEnumerator() : IEnumerator<'T> = l.Current.GetEnumerator()
            member l.GetEnumerator() : Collections.IEnumerator = l.Current.GetEnumerator()

    /// Runs `f` in a new context, and gives its result. The code before and
    /// after the call has the context that it had.
    ///
    /// - Each `Per` starts in the new context as `Per` says: a value with
    ///   what its `make` gives, and a setting with what the calling context
    ///   has.
    /// - For a compilation that runs at the same time as another one. A
    ///   compilation that one thread starts inside another, such as that of
    ///   an import, uses `Session.isolated`, which stays in the context.
    let inNew (f: unit -> 'a) : 'a =
        let outer = flow.Value
        flow.Value <- Context(current ())

        try
            f ()
        finally
            flow.Value <- outer
