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

module Bjolang.Gensym

/// The compiler's *single* source of invented names.
///
/// A single, global counter is necessary because the inliner splices bodies
/// that already contain `LoopLowering` names into functions where `Codegen`
/// will later hoist temporaries. A shared counter guarantees these names
/// remain distinct across passes.
let private counter = ref 0

/// The names of the shape `fresh` makes that source has written.
///
/// `x__1` is an identifier a program may write, so a fresh `x__1` would be a
/// binder that captures the program's own variable of that name. The lexer
/// reserves every such name it reads, and `fresh` passes over them.
let private written = System.Collections.Generic.HashSet<string>()

/// Is `name` of the shape `fresh` makes: a name, `__`, and digits?
let private isFreshShaped (name: string) : bool =
    match name.LastIndexOf "__" with
    | -1 -> false
    | i ->
        let suffix = name.Substring(i + 2)
        suffix.Length > 0 && suffix |> Seq.forall System.Char.IsDigit

/// Every name the source of this compilation has written, of any shape.
///
/// What tells a program's own local from one the compiler invented, once both
/// are C# locals in a PDB: `loopcur__75` and `__init__65` are names nobody
/// writes, and `x__23` is the `x` somebody did (see `PdbNames`).
let private sourceNames = System.Collections.Generic.HashSet<string>()

/// Reserves a name source has written, so that `fresh` never makes it.
let reserve (name: string) : unit =
    sourceNames.Add name |> ignore

    if isFreshShaped name then
        written.Add name |> ignore

/// Whether source this compilation read has written `name`.
let isWritten (name: string) : bool = sourceNames.Contains name

/// `prefix__N`, with `N` unique across the whole compilation, and not a name
/// source has written (see `reserve`).
///
/// The `__N` suffix is not decoration. `Codegen.sanitizeIdent` is not
/// injective — `a-b` and `asubb` both become `asubb` — so a renamed binder may
/// not rely on its base name to distinguish it. The counter does.
let fresh (prefix: string) : string =
    let next () =
        counter.Value <- counter.Value + 1
        $"%s{prefix}__%d{counter.Value}"

    let mutable name = next ()

    while written.Contains name do
        name <- next ()

    name

/// The counter's value, and a way to put it back.
///
/// One compilation's invented names must not depend on what was compiled before
/// it in the same process: a module's `.dll` is judged stale by timestamp, so
/// two builds of unchanged source that differ in a `tmp__37` are a cache that
/// starts missing. A process boundary used to guarantee that for free. These
/// are what `Session` brackets an in-process module compilation with instead.
///
/// A REPL entry is the other case and uses the same pair the other way: the
/// counter is *not* put back, so two entries never invent the same name — entry
/// 4's `tmp__12` and entry 9's would otherwise be one C# member in two
/// assemblies, and the one loaded second is the one that wins.
///
/// The names source has written go with the counter, for the same reason:
/// which names `fresh` passes over depends on what was read.
type State =
    { Counter: int
      Written: Set<string>
      SourceNames: Set<string> }

let emptyState: State =
    { Counter = 0
      Written = Set.empty
      SourceNames = Set.empty }

let snapshot () : State =
    { Counter = counter.Value
      Written = Set.ofSeq written
      SourceNames = Set.ofSeq sourceNames }

let restore (state: State) : unit =
    counter.Value <- state.Counter
    written.Clear()
    written.UnionWith state.Written
    sourceNames.Clear()
    sourceNames.UnionWith state.SourceNames

/// The base name a `fresh` name was derived from, for diagnostics.
let baseName (name: string) : string =
    match name.LastIndexOf "__" with
    | -1 -> name
    | i ->
        let suffix = name.Substring(i + 2)
        if suffix.Length > 0 && suffix |> Seq.forall System.Char.IsDigit then
            name.Substring(0, i)
        else
            name
