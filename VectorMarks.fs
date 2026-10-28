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

/// Marks the elements of `Contiguous` drivers and the steps of
/// `BlockCollector`s, so that `Codegen` can find them in a loop.
///
/// The pass runs after `Monomorphise` and before `TraitInline`. Only here is
/// a call still a call of a trait method at a known type. After `TraitInline`
/// the call is the body of the implementation, and nothing tells that the body
/// is the element of a driver.
///
/// A mark is a call of a name that no program can write, with the original
/// call as its last argument:
///
///     (%vector-element seq cursor (iterable-current seq cursor))
///     (let x (elem) (%vector-step c acc x (collector-step c acc x)))
///
/// The passes after this one treat a mark as a call of an unknown function,
/// and change its arguments as they change all expressions. `Codegen` writes
/// the last argument where a mark is not part of a vector loop.
module Bjolang.VectorMarks

open Bjolang.TypedAST
open Bjolang.Unification

[<Literal>]
let ElementMark = "%vector-element"

[<Literal>]
let StepMark = "%vector-step"

/// Whether `t` is a type with no type variables. Code for vector lanes needs
/// the type of each lane.
let rec private isGround (t: HMType) : bool =
    match t with
    | TMeta { Value = Some inner } -> isGround inner
    | TMeta _
    | TVar _
    | TAssoc _ -> false
    | TCon(_, args)
    | TTuple args -> List.forall isGround args
    | TFun(args, ret, _) -> List.forall isGround args && isGround ret

let private hasImpl (env: Env) (traitName: string) (t: HMType) : bool =
    let t = prune env.Registry t
    isGround t && (implFor env.Registry traitName t).IsSome

let private isName (e: TypedExpr) =
    match e.Node with
    | TIdent(_, []) -> true
    | _ -> false

let private mark (name: string) (args: TypedExpr list) (original: TypedExpr) : TypedExpr =
    let all = args @ [ original ]
    let callee =
        { Type = TFun(all |> List.map (fun a -> a.Type), original.Type, ESync)
          Range = original.Range
          Node = TIdent(name, []) }
        : TypedExpr
    { original with Node = TApply(callee, all, []) }

let private markExpr (env: Env) (expr: TypedExpr) : TypedExpr =
    match expr.Node with
    | TTraitCall(tref, [ seq; cursor ], []) when
        tref.Trait = "Iterable" && tref.Method = "iterable-current"
        && isName seq && isName cursor
        && hasImpl env "Contiguous" seq.Type
        ->
        mark ElementMark [ seq; cursor ] expr
    | TTraitCall(tref, [ c; acc; x ], []) when
        tref.Trait = "Collector" && tref.Method = "collector-step"
        && isName c && isName acc
        && hasImpl env "BlockCollector" c.Type
        ->
        // The element is bound to a name, so that the mark and the step read
        // it, and it is evaluated once.
        let xName = Gensym.fresh "vectorx"
        let xIdent = { x with Node = TIdent(xName, []) }
        let step = { expr with Node = TTraitCall(tref, [ c; acc; xIdent ], []) }
        { expr with Node = TLet(xName, false, noParams, x, mark StepMark [ c; acc; xIdent ] step) }
    | _ -> expr

/// Marks every driver element and collector step that has a vector form.
let run (env: Env) (decls: TDecl list) : TDecl list =
    decls |> List.map (TypeVisitor.mapDecl (TypeVisitor.mapExpr (markExpr env)))
