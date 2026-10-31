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

/// Warnings for definitions nothing uses: a top-level one the module neither
/// exports nor uses, and a local binding its scope never reads.
///
/// Read off the module as parsed, macros expanded and before any pass rewrites
/// it, so that a binding is the one the program wrote. What a macro wrote is
/// passed over: its nodes carry the call site's range
/// (`Macro.expansionSites`), and the program did not write them.
module Bjolang.Unused

open Bjolang.Lexer
open Bjolang.Ast

/// A name this check says nothing about: one the program keeps on purpose,
/// written with a leading `_`, and one the compiler made (`x__3`, `f__bjo`,
/// `#name` for a hash macro's transformer).
let private exempt (name: string) =
    name.StartsWith "_" || name.Contains "__" || name.StartsWith "#" || name.StartsWith "%"

/// Every expression a declaration holds, keyword defaults included.
let private exprsOf (d: Decl) : Expr list =
    let found = ResizeArray<Expr>()

    mapDeclExprs
        (fun e ->
            found.Add e
            e)
        d
    |> ignore

    List.ofSeq found

/// A local binding: its name, the binding form's range, the value's, and
/// whether its scope reads it.
type private Local =
    { Name: string
      Form: Range
      Value: Range
      Read: bool }

/// Every local binding in `e`.
let rec private locals (e: Expr) : Local list =
    let readIn (name: string) (scope: Expr) = Set.contains name (freeNames Set.empty scope)

    let local name (form: Range) value read =
        { Name = name
          Form = form
          Value = exprRange value
          Read = read }

    let here =
        match e with
        | ELet(name, _, _, _, value, body, r)
        | ELetMono(name, value, body, r)
        | ELetMutable(name, _, value, body, r) -> [ local name r value (readIn name body) ]
        | ELetTuple(names, value, body, r) ->
            let read = freeNames Set.empty body
            names |> List.map (fun name -> local name r value (Set.contains name read))
        | ELetRec(bindings, body, r) ->
            bindings
            |> List.map (fun (name, _, _, _, value) ->
                let byOthers =
                    bindings |> List.exists (fun (other, _, _, _, v) -> other <> name && readIn name v)

                local name r value (readIn name body || byOthers))
        | _ -> []

    here @ (exprChildren e |> List.collect locals)

/// Warns about each local binding in `exprs`, one declaration's, that nothing
/// reads.
///
/// A loop clause's variable is bound once for each place the loop reads it,
/// and every one of those bindings has the clause's value. A name is reported
/// only when no binding of it to that value is read.
///
/// The binding form's range is what says whether a macro wrote it. The value's
/// is what the warning names: a `def` in a body is a binding whose form is the
/// rest of the body, and the value is next to the name either way.
let private warnLocals (written: Range -> bool) (exprs: Expr list) : unit =
    exprs
    |> List.collect locals
    |> List.filter (fun l -> not (exempt l.Name) && written l.Form)
    |> List.groupBy (fun l -> l.Name, l.Value)
    |> List.iter (fun ((name, value), bindings) ->
        if bindings |> List.forall (fun l -> not l.Read) then
            Diagnostics.warn
                $"'%s{name}' is bound to the value at %s{formatPos value} and nothing reads it. Remove it, or start its name with _ if it is kept on purpose.")

/// The names a top-level declaration defines, each with where.
let private defines (d: Decl) : (string * Range) list =
    match d with
    | DDef(name, _, r)
    | DDefMutable(name, _, r)
    | DDefun(name, _, _, _, r)
    | DDefDouble(name, _, _, _, r) -> [ name, r ]
    | DDefTuple(names, _, r) -> names |> List.map (fun n -> n, r)
    | DDefPattern(pattern, _, r) -> patternBinders pattern |> List.map (fun n -> n, r)
    | _ -> []

/// Warns about the top-level definitions of `decls`, one module's, that it
/// does not export and nothing in it uses, when `topLevel`, and about the
/// local bindings in it that nothing reads.
///
/// A REPL entry's definitions are there for the entries after it, so it asks
/// for the locals only.
let rec warn (topLevel: bool) (decls: Decl list) : unit =
    let sites = Macro.expansionSites ()
    let written (r: Range) = not (Set.contains (r.File, r.Start, r.End) sites)

    // Named in an export, or kept alive by a second name for it.
    let kept =
        decls
        |> List.collect (function
            | DExport(names, _)
            | DReExport(names, _) -> names
            | DAlias(_, original, _) -> [ original ]
            | DMacro(name, _)
            | DClauseMacro(name, _)
            | DPatternMacro(name, _)
            | DHashMacro(name, _) -> [ name ]
            | _ -> [])
        |> Set.ofList

    // What each declaration reads at the top level. A definition's use of
    // itself, a recursive call, does not keep it.
    let reads =
        decls
        |> List.map (fun d ->
            let own = defines d |> List.map fst |> Set.ofList
            let read = exprsOf d |> List.map (freeNames Set.empty) |> Set.unionMany
            own, read)

    let usedElsewhere (name: string) =
        reads |> List.exists (fun (own, read) -> not (Set.contains name own) && Set.contains name read)

    for d in decls do
        for (name, r) in defines d do
            if
                topLevel
                && name <> "main"
                && not (exempt name)
                && not (Set.contains name kept)
                && written r
                && not (usedElsewhere name)
            then
                Diagnostics.warn
                    $"'%s{name}' is defined at %s{formatPos r} and nothing uses it: this module does not export it, and none of its code reads it. Remove it, export it, or start its name with _ if it is kept on purpose."

        match d with
        | DModule(_, inner, _) -> warn topLevel inner
        | DDerived _
        | DLinked _
        | DInlineImpl _ -> ()
        | _ -> warnLocals written (exprsOf d)
