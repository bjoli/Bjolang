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

/// Per-compilation state, so that several compilations can run in one process
/// without sharing mutable globals.
///
/// Compiler passes keep counters, tables and settings in module-level values.
/// Process-wide values stay plain `let` bindings. Anything that belongs to a
/// single compilation is declared as a `Per`, `PerDictionary`, `PerSet` or
/// `PerList` instead. Those look like ordinary module-level values but store
/// their data in the current `Context`.
///
/// - If nothing ever creates a context, everything lives in the root context
///   and behaves exactly like a `let mutable` global.
/// - `inNew` runs a function in a fresh context. Compilations running in
///   different contexts (e.g. on different threads) don't see each other's
///   state.
/// - The current context is ambient and flows with async control flow: tasks
///   and threads started by a compilation inherit its context.
///
/// `Session` saves and restores some of these values around a nested
/// compilation (an import compiled on demand). It works on the current
/// context, so it behaves the same inside any context.
///
/// Process-wide caches (loaded assemblies, files read from disk) and hooks
/// that one module installs for another are deliberately not per-context.
/// `Session` has the full list.
module Compilation =
    open System
    open System.Collections.Generic
    open System.Runtime.CompilerServices
    open System.Runtime.InteropServices
    open System.Threading

    /// Holds the state of one compilation: an array with one slot per `Per` value.
    [<AllowNullLiteral>]
    type Context internal (parent: Context) =
        let mutable slots: obj[] = Array.zeroCreate 128

        /// The context that was current when this one was created, or null for
        /// the root context.
        member _.Parent = parent

        member internal _.Slots
            with get () = slots
            and set (v: obj[]) = slots <- v

    /// A mutable cell holding the value of one `Per` in one context.
    [<AllowNullLiteral>]
    type internal Holder<'T>(initial: 'T) =
        let mutable value = initial

        member _.Value
            with get () = value
            and set (v: 'T) = value <- v

    let private root = Context(null)

    /// Thread-static cache of the current context.
    ///
    /// The source of truth is the `AsyncLocal` below, which flows into tasks
    /// and new threads. Reading an `AsyncLocal` is slow (a lookup in the
    /// execution context), and `Per` values are read on hot paths, so we mirror
    /// it into a thread-static field, which is a single load. The
    /// `AsyncLocal`'s change callback keeps the field up to date on every
    /// thread.
    type private Ambient() =
        [<ThreadStatic; DefaultValue>]
        static val mutable private current: Context

        static member Current
            with get () = Ambient.current
            and set (c: Context) = Ambient.current <- c

    let private flow =
        AsyncLocal<Context>(fun (change: AsyncLocalValueChangedArgs<Context>) -> Ambient.Current <- change.CurrentValue)

    /// Returns the current context, or the root context if none has been set.
    let current () : Context =
        match Ambient.Current with
        | null -> root
        | c -> c

    let mutable private count = 0

    let internal nextIndex () = Interlocked.Increment(&count) - 1

    /// Returns the holder in slot `index` of `context`, creating it with `make`
    /// on first access. Grows the slot array if needed.
    let internal holderIn<'T> (context: Context) (index: int) (make: Context -> 'T) : Holder<'T> =
        let slots = context.Slots

        if index < slots.Length && not (isNull slots[index]) then
            Unsafe.As<Holder<'T>> slots[index]
        else
            // Slow path, taken once per slot per context. Lock because a
            // compilation may use helper threads that share its context.
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

    /// A per-compilation mutable value. Use it instead of a module-level
    /// `let mutable`, and read or write it through `.Value`.
    ///
    /// - `Per(make)`: every context starts with its own `make ()`.
    /// - `Per(make, true)`: a setting, which child contexts inherit. A context
    ///   created by `inNew` starts with its parent's value, copied the first
    ///   time the child accesses the setting. The root context starts with
    ///   `make ()`.
    ///
    /// Values are initialized lazily, on a context's first read or write.
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

    /// A per-compilation `Dictionary`. Forwards the `Dictionary` members the
    /// compiler uses to the current context's instance, so existing call sites
    /// compile unchanged. `Current` returns the underlying dictionary.
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

    /// A per-compilation `HashSet`, forwarding to the current context's instance.
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

    /// A per-compilation `ResizeArray`, forwarding to the current context's
    /// instance.
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

    /// Runs `f` in a fresh child context and returns its result. The caller's
    /// context is restored afterwards, even if `f` throws.
    ///
    /// Inside `f`, ordinary `Per` values start from their initial value and
    /// settings start from the caller's current value (see `Per`).
    ///
    /// Use this for compilations that run concurrently with others, such as
    /// the language server's background checks. A nested compilation on the
    /// same thread (e.g. compiling an import) should use `Session.isolated`
    /// instead, which saves and restores state within the current context.
    let inNew (f: unit -> 'a) : 'a =
        let outer = flow.Value
        flow.Value <- Context(current ())

        try
            f ()
        finally
            flow.Value <- outer
