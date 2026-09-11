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

/// Every value that is computed and then dropped has to say so.
///
/// F#'s rule, and universal: no opt-in, no per-type attribute, no per-module
/// flag. A value in statement position must have type `Unit`, or be handed to
/// `ignore`, which is an ordinary function whose whole job is to have the right
/// type.
///
/// # Why universal
///
/// The cost is real — every fluent .NET call needs ceremony — and it is
/// accepted for two reasons. A silently discarded `HashSet.Add` returning
/// `false` is a genuine bug class, and it is invisible: nothing about the source
/// says a value went missing. And a rule with exceptions is a rule people have
/// to learn the shape of, which is worse than one that is merely strict.
///
/// §8.1 already removed the largest source of noise by making the builder
/// procedures return `Unit` in the first place.
///
/// # Why this runs before `TraitInline`
///
/// Determinism, not necessity. `spliceTemplate` is best-effort: it falls back to
/// a landing pad when re-inference throws, when the key is `Active` on the
/// current path, or when arity does not match. A check after inlining would
/// report on spliced bodies and not on the same body reached through a landing
/// pad — the same program, different errors depending on inliner luck — and it
/// would report once per call site rather than once.
///
/// That is also the second argument for the rule being universal rather than a
/// per-module flag: under a flag, a library compiled without it could have its
/// template spliced into a module with it, and the error would fire on a body
/// from another file that the user may not even have the source for.
module Bjolang.MustUse

open Bjolang.Lexer
open Bjolang.TypedAST

/// May a value of this type be dropped without saying so?
///
/// Three spellings of "nothing", and all three are reachable. `Unit` is what a
/// Bjolang `void` signature means; `System.Void` is the interop void, which a
/// `.Dispose()` call in tail position has; and the empty tuple is what `()` and
/// an empty body produce.
let private carriesNothing (registry: TraitRegistry) (t: HMType) =
    match Unification.prune registry t with
    | TCon(TypeConstants.UnitName, [])
    | TCon(TypeConstants.VoidName, [])
    | TTuple [] -> true
    | _ -> false

let private describe = DotNetInterop.showType

/// Checks if this call diverges (never returns).
///
/// Calls to functions tracked in `ReturnOnlyGenerics` produce no value, so 
/// they do not require `ignore` handling. Only explicit calls are checked; 
/// dropping the function value itself is still considered discarding a value.
let private neverReturns (registry: TraitRegistry) (expr: TypedExpr) =
    match expr.Node with
    | TApply({ Node = TIdent(name, _) }, _, _) ->
        Set.contains (Naming.writtenName name) registry.ReturnOnlyGenerics
    | _ -> false

/// `what` names the shape that is doing the discarding, so that the message can
/// say where the value went rather than only that it went.
let private checkDiscard (registry: TraitRegistry) (what: string) (expr: TypedExpr) : unit =
    if neverReturns registry expr then
        ()
    elif not (carriesNothing registry expr.Type) then
        failwithf
            $"Type Error at %s{formatPos expr.Range}: this value has type %s{describe expr.Type} and is discarded.\n  %s{what}\n  Every value that is computed and then dropped has to say so: write `(ignore ...)` around it. A value that goes missing without a word is the bug this rule exists to catch."

let rec private checkExpr (registry: TraitRegistry) (expr: TypedExpr) : unit =
    let descend = checkExpr registry

    match expr.Node with
    // A body of several forms is `TLet("_", …, first, rest)`: the parser
    // sequences with a binding nobody can name. That binder is the whole of
    // statement position in this language — a loop's `:do` clauses, a `seq`
    // body's leading forms and a `do` block's `:then`s all arrive here.
    //
    // A *user-written* `_` reaches the same node and is treated the same way,
    // which is right: `(def _ (f x))` is a discard spelled unusually, and the
    // rule is that a discard is spelled `ignore`.
    | TLet("_", false, _, value, body) ->
        checkDiscard registry "It is a form in the middle of a body, so its value is dropped." value
        descend value
        descend body

    // `when` and `unless` have no else branch, so their body's value has
    // nowhere to go by construction.
    | TWhen(cond, body, _) ->
        descend cond
        checkDiscard registry "A (when ...) or (unless ...) body has no branch to return to, so its value is dropped." body
        descend body

    // The cleanup of a `try/finally` runs for its effect; the form's value is
    // the body's.
    | TTryFinally(body, cleanup) ->
        descend body
        checkDiscard registry "A #:finally clause runs for its effect, so its value is dropped." cleanup
        descend cleanup

    | _ -> TypeVisitor.children expr |> List.iter descend

let private checkDecl (registry: TraitRegistry) (decl: TDecl) : unit =
    decl
    |> TypeVisitor.mapDecl (fun e ->
        checkExpr registry e
        e)
    |> ignore

let run (registry: TraitRegistry) (decls: TDecl list) : unit =
    decls |> List.iter (checkDecl registry)
