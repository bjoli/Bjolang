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

/// Returns true for names the unused-binding warnings should ignore: the
/// wildcard `_` and compiler-generated names (`x__3`, `f__bjo`, and the
/// `#name` transformer of a hash macro).
let private exempt (name: string) =
    name = "_" || name.Contains "__" || name.StartsWith "#" || name.StartsWith "%"

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
                $"'%s{name}' is bound to the value at %s{formatPos value} and nothing reads it. Remove it, or write _ in place of the name.")

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

/// Returns true if `name` is a user-written identifier containing `_`,
/// which is not allowed.
///
/// Underscores are reserved for the compiler: name mangling emits them
/// (`a?` becomes `a_QMARK` in C#) and gensyms use a `__N` suffix (`x__3`).
/// A user identifier with `_` could collide with either.
///
/// Not rejected:
/// - `_` on its own, which is the wildcard, not an identifier.
/// - Compiler-generated names (`loopcur__7`, or `&1__4` for a short-lambda
///   parameter). We tell them apart with `Gensym.isUserWritten`: a name only
///   counts if the lexer read it from `file`, the source file that the binding
///   is in. Names that merely appear in an imported module's metadata don't
///   count.
/// - The REPL's wrapper names in `Gensym.compilerWritten`. The REPL writes
///   them into the entry's source file, so they look user-written.
let private illegal (file: string) (name: string) =
    name <> "_"
    && name.Contains "_"
    && Gensym.isUserWritten file name
    && not (Gensym.compilerWritten.Contains name)

/// Returns the names of all parameters in `args`: positional, keyword and rest.
let private parameters (args: DefunArg list) : string list =
    args
    |> List.map (function
        | MandatoryArg(n, _)
        | KeywordArg(n, _)
        | RestArg n -> n)

/// Collects every identifier bound anywhere inside `e` (let bindings, lambda
/// and local-function parameters, pattern variables), paired with the source
/// range of the binding form.
let rec private binders (e: Expr) : (string * Range) list =
    let here =
        match e with
        | ELet(name, _, args, _, _, _, r) -> (name :: parameters args) |> List.map (fun n -> n, r)
        | ELetMono(name, _, _, r)
        | ELetMutable(name, _, _, _, r)
        | EWithReturn(name, _, r) -> [ name, r ]
        | ELetTuple(names, _, _, r)
        | EFun(names, _, _, r) -> names |> List.map (fun n -> n, r)
        | ELetRec(bindings, _, r) ->
            bindings
            |> List.collect (fun (name, _, args, _, _) -> name :: parameters args)
            |> List.map (fun n -> n, r)
        | EMatch(_, arms, r) ->
            arms
            |> List.collect (fun (pattern, _, _) -> patternBinders pattern)
            |> List.map (fun n -> n, r)
        | EDefMatch(pattern, _, _, _, r) -> patternBinders pattern |> List.map (fun n -> n, r)
        | _ -> []

    here @ (exprChildren e |> List.collect binders)

/// Reports a syntax error for every identifier containing `_` that the
/// module's declarations bind: top-level definitions, parameters, local
/// bindings and pattern variables.
///
/// All offending names are reported at once, each at the range of its binding
/// form. Bindings produced by macro expansion are skipped.
///
/// Only binding sites are checked. References are left alone, because an
/// unbound `_total` is a typed hole (see `Holes`) and .NET member names may
/// legitimately contain underscores.
let rejectNames (decls: Decl list) : unit =
    let sites = Macro.expansionSites ()
    let written (r: Range) = not (Set.contains (r.File, r.Start, r.End) sites)

    let rec bound (d: Decl) : (string * Range) list =
        match d with
        | DModule(_, inner, _) -> inner |> List.collect bound
        | DDerived _
        | DLinked _
        | DInlineImpl _ -> []
        | _ ->
            let own =
                match d with
                | DDefun(_, args, _, _, r)
                | DDefDouble(_, args, _, _, r) -> parameters args |> List.map (fun n -> n, r)
                | _ -> []

            defines d @ own @ (exprsOf d |> List.collect binders)

    let errors =
        decls
        |> List.collect bound
        |> List.filter (fun (name, r) -> illegal r.File name && written r)
        |> List.distinct
        |> List.sortBy (fun (_, r) -> r.File, r.Start)
        |> List.map (fun (name, r) -> $"Syntax Error at %s{formatPos r}: '%s{name}' is an illegal name.")

    if not errors.IsEmpty then
        Diagnostics.failWithAll "names" errors

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
            | DAccMacro(name, _)
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
                    $"'%s{name}' is defined at %s{formatPos r} and nothing uses it: this module does not export it, and none of its code reads it. Remove it, or export it."

        match d with
        | DModule(_, inner, _) -> warn topLevel inner
        | DDerived _
        | DLinked _
        | DInlineImpl _ -> ()
        | _ -> warnLocals written (exprsOf d)
