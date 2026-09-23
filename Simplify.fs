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

module Bjolang.Simplify

open Bjolang.TypedAST

/// Cleans up what trait inlining leaves behind around a local bound to a union
/// case, as in the collector of a `(loop ... (:acc ...))`:
///
///   * A `match` on the local picks its arm at compile time.
///   * A local nothing reads, whose value has no effects, is dropped.
///
/// Runs on the typed tree before `Lowering`, which turns matches into type
/// tests that no longer say which case they test for.
///
/// Names are not unique yet at this point, so a binding is only touched when
/// nothing inside its scope binds the same name again.

// ---------------------------------------------------------------------------
// Names
// ---------------------------------------------------------------------------

/// Keyword defaults of a local function sit outside `TypeVisitor.children`.
let private kwDefaults (expr: TypedExpr) : TypedExpr list =
    let ofFun (fn: LocalFun) = fn.KeywordArgs |> List.map (fun (_, _, d) -> d)

    match expr.Node with
    | TLet(_, _, fn, _, _) -> ofFun fn
    | TLetRec(bindings, _) -> bindings |> List.collect (fun (_, _, fn, _) -> ofFun fn)
    | _ -> []

let private allChildren (expr: TypedExpr) : TypedExpr list =
    TypeVisitor.children expr @ kwDefaults expr

let private funBinders (fn: LocalFun) : string list =
    fn.Params
    @ (fn.KeywordArgs |> List.map (fun (n, _, _) -> n))
    @ (fn.RestArg |> Option.map fst |> Option.toList)

/// The names `expr` binds at its own node.
let private bindersHere (expr: TypedExpr) : string list =
    match expr.Node with
    | TLet(n, _, fn, _, _) -> n :: funBinders fn
    | TLetRec(bindings, _) -> bindings |> List.collect (fun (n, _, fn, _) -> n :: funBinders fn)
    | TLetTuple(names, _, _) -> names
    | TLetMutable(n, _, _) -> [ n ]
    | TLambda(args, _) -> args
    | TMatch(_, clauses) -> clauses |> List.collect (fun c -> AlphaRename.patternNames c.Pattern)
    | TDefMatch(binder, _, _, arms) ->
        AlphaRename.patternNames binder
        @ (arms |> List.collect (fun a -> AlphaRename.patternNames a.Pattern))
    | TLoop(members, _) ->
        members
        |> List.collect (fun m -> m.LoopName :: (m.Slots |> List.map fst) @ m.Locals)
    | _ -> []

let rec private rebinds (name: string) (expr: TypedExpr) : bool =
    List.contains name (bindersHere expr) || allChildren expr |> List.exists (rebinds name)

let rec private mentions (name: string) (expr: TypedExpr) : bool =
    match expr.Node with
    | TIdent(n, _)
    | TSet(n, _)
    | TRecordUpdate(n, _)
    | TRecordSet(n, _) when n = name -> true
    | _ -> allChildren expr |> List.exists (mentions name)

// ---------------------------------------------------------------------------
// Values
// ---------------------------------------------------------------------------

type private Ctx = { Cases: Set<string> }

/// `(K a ...)` or a bare nullary `K`, as the case and its arguments.
let private asCase (ctx: Ctx) (expr: TypedExpr) : (string * TypedExpr list) option =
    match expr.Node with
    | TIdent(k, _) when Set.contains k ctx.Cases -> Some(k, [])
    | TApply({ Node = TIdent(k, _) }, args, []) when Set.contains k ctx.Cases -> Some(k, args)
    | _ -> None

let private isLiteral (expr: TypedExpr) : bool =
    match expr.Node with
    | TInt _
    | TBool _
    | TString _
    | TChar _
    | TKeyword _
    | TSymbol _ -> true
    | _ -> false

/// Evaluating it has no effect, so it may be left out.
let rec private isPure (ctx: Ctx) (expr: TypedExpr) : bool =
    isLiteral expr
    || (match expr.Node with
        | TIdent _ -> true
        | TTupleMake items -> List.forall (isPure ctx) items
        | _ ->
            match asCase ctx expr with
            | Some(_, args) -> List.forall (isPure ctx) args
            | None -> false)

let private letIn (name: string) (value: TypedExpr) (body: TypedExpr) : TypedExpr =
    { Type = body.Type
      Range = value.Range
      Node = TLet(name, false, noParams, value, body) }

// ---------------------------------------------------------------------------
// Case of a known case
// ---------------------------------------------------------------------------

type private Pick =
    | Take of TypedExpr
    | Skip
    | GiveUp

/// What clause `c` does with a value known to be `(k args ...)`, where `local`
/// names that value.
let private pick (ctx: Ctx) (local: TypedExpr) (k: string) (args: TypedExpr list) (c: TMatchClause) : Pick =
    match c.Pattern.Node with
    | TPConstruct(n, _) when n <> k && Set.contains n ctx.Cases -> Skip
    | _ when c.Guard.IsSome -> GiveUp
    | TPWildcard -> Take c.Body
    | TPIdent n -> Take(letIn n local c.Body)
    | TPConstruct(n, subs) when n = k && subs.Length = args.Length ->
        let bind (sub: TypedPattern) (arg: TypedExpr) (body: TypedExpr option) =
            body
            |> Option.bind (fun body ->
                match sub.Node with
                | TPWildcard -> Some body
                | TPIdent s -> Some(letIn s arg body)
                | _ -> None)

        match List.foldBack2 bind subs args (Some c.Body) with
        | Some body -> Take body
        | None -> GiveUp
    | _ -> GiveUp

/// Rewrites every `match` on `name` inside `expr` that can be decided, and
/// counts them in `decided`.
let rec private decide
    (ctx: Ctx)
    (decided: int ref)
    (name: string)
    (k: string)
    (args: TypedExpr list)
    (expr: TypedExpr)
    : TypedExpr =
    let expr = TypeVisitor.mapChildren (decide ctx decided name k args) expr

    match expr.Node with
    | TMatch({ Node = TIdent(n, _) } as local, clauses) when n = name ->
        let rec first clauses =
            match clauses with
            | [] -> None
            | c :: rest ->
                match pick ctx local k args c with
                | Take body -> Some body
                | Skip -> first rest
                | GiveUp -> None

        match first clauses with
        | Some body ->
            decided.Value <- decided.Value + 1
            { body with Type = expr.Type; Range = expr.Range }
        | None -> expr
    | _ -> expr

/// Binds each `(fresh, arg)` around `inner`, first outermost, so the arguments
/// are evaluated in the order they were written. One nothing reads is dropped
/// when it has no effect.
let private wrapInOrder (ctx: Ctx) (named: (string * TypedExpr) list) (inner: TypedExpr) : TypedExpr =
    List.foldBack
        (fun (fresh, arg) (acc: TypedExpr) ->
            if not (mentions fresh acc) && isPure ctx arg then acc else letIn fresh arg acc)
        named
        inner

let rec private simplify (ctx: Ctx) (expr: TypedExpr) : TypedExpr =
    let expr = TypeVisitor.mapChildren (simplify ctx) expr

    match expr.Node with
    | TLet(name, false, fn, value, body) when fn = noParams && not (rebinds name body) ->
        match asCase ctx value with
        | Some(k, args) ->
            // Every argument that is not a literal goes into a fresh local
            // ahead of the case. A fresh name cannot be shadowed, so it can be
            // copied into any arm.
            let named =
                args
                |> List.map (fun arg ->
                    if isLiteral arg then
                        None, arg
                    else
                        let fresh = Gensym.fresh "case"
                        Some(fresh, arg), { arg with Node = TIdent(fresh, []) })

            let argRefs = named |> List.map snd
            let count = ref 0
            let decided = decide ctx count name k argRefs body

            if count.Value = 0 then
                if mentions name body || not (isPure ctx value) then expr else body
            else
                let caseValue =
                    match value.Node with
                    | TApply(target, _, kw) -> { value with Node = TApply(target, argRefs, kw) }
                    | _ -> value

                let inner =
                    if mentions name decided then
                        { expr with Node = TLet(name, false, noParams, caseValue, decided) }
                    else
                        decided

                { wrapInOrder ctx (named |> List.choose fst) inner with Type = expr.Type }

        | None when not (mentions name body) && isPure ctx value -> body
        | None -> expr

    | _ -> expr

// ---------------------------------------------------------------------------
// The pass
// ---------------------------------------------------------------------------

let run (env: Env) (decls: TDecl list) : TDecl list =
    let cases =
        env.Registry.Unions
        |> Map.toSeq
        |> Seq.collect (fun (_, (_, cases)) -> cases |> Seq.map (fun (n, _, _) -> n))
        |> Set.ofSeq

    let ctx = { Cases = cases }
    decls |> List.map (TypeVisitor.mapDecl (simplify ctx))
