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

/// Units removed from the checked tree, after `Monomorphise` and before
/// every pass that emits code.
///
/// - Expression types and declaration signatures: `TNum(c, _)` is `c`.
/// - A module with no unit in it is returned as it is.
///
/// Unit variables are never type arguments: `instantiate` and `checkDefun`
/// leave them out.
///
/// Types held inside expression nodes are not rewritten; the passes after
/// this one read a `TNum` there as its carrier.
module Bjolang.EraseUnits

open Bjolang.TypedAST
open Bjolang.TypeVisitor

let rec private hasUnits (t: HMType) : bool =
    match t with
    | TNum _ -> true
    | TCon(_, args)
    | TTuple args -> List.exists hasUnits args
    | TFun(args, ret, _) -> hasUnits ret || List.exists hasUnits args
    | TAssoc(_, _, implementor) -> hasUnits implementor
    | TMeta { Value = Some inner } -> hasUnits inner
    | TVar _
    | TMeta _ -> false

let private erase (t: HMType) : HMType = if hasUnits t then eraseUnits t else t

let rec private declHasUnits (decl: TDecl) : bool =
    let inExpr e = foldExpr (fun found (x: TypedExpr) -> found || hasUnits x.Type) false e

    match decl with
    | TDef(_, value, t, _)
    | TDefTuple(_, value, t, _)
    | TDefMutable(_, value, t, _) -> hasUnits t || inExpr value
    | TDefPattern(_, value, _, _) -> inExpr value
    | TDefun(_, _, args, kwArgs, restArg, retType, _, body, _) ->
        hasUnits retType
        || args |> List.exists (snd >> hasUnits)
        || kwArgs |> List.exists (fun (_, t, e) -> hasUnits t || inExpr e)
        || restArg |> Option.exists (snd >> hasUnits)
        || inExpr body
    | TModule(_, decls, _) -> List.exists declHasUnits decls
    | TImpl(_, _, _, targetType, assoc, dicts, methods, _) ->
        hasUnits targetType
        || assoc |> List.exists (snd >> hasUnits)
        || dicts |> List.exists (snd >> hasUnits)
        || List.exists declHasUnits methods
    | _ -> false

let rec private eraseDecl (decl: TDecl) : TDecl =
    let inExprs = mapDecl (mapExpr (fun e -> { e with Type = erase e.Type }))

    match decl with
    | TModule(name, decls, r) -> TModule(name, List.map eraseDecl decls, r)
    | TImpl(traitName, kind, holeArity, targetType, assoc, dicts, methods, r) ->
        TImpl(
            traitName,
            kind,
            holeArity,
            erase targetType,
            assoc |> List.map (fun (n, t) -> n, erase t),
            dicts |> List.map (fun (n, t) -> n, erase t),
            List.map eraseDecl methods,
            r
        )
    | _ ->
        match inExprs decl with
        | TDef(name, value, t, r) -> TDef(name, value, erase t, r)
        | TDefTuple(names, value, t, r) -> TDefTuple(names, value, erase t, r)
        | TDefMutable(name, value, t, r) -> TDefMutable(name, value, erase t, r)
        | TDefun(name, tyArgs, args, kwArgs, restArg, retType, effect, body, r) ->
            TDefun(
                name,
                tyArgs,
                args |> List.map (fun (n, t) -> n, erase t),
                kwArgs |> List.map (fun (n, t, e) -> n, erase t, e),
                restArg |> Option.map (fun (n, t) -> n, erase t),
                erase retType,
                effect,
                body,
                r
            )
        | other -> other

let run (decls: TDecl list) : TDecl list =
    if List.exists declHasUnits decls then List.map eraseDecl decls else decls
