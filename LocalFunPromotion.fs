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

module Bjolang.LocalFunPromotion

open Bjolang.TypedAST

/// Makes a local that holds a lambda into a local function, when the scope
/// only calls it.
///
/// `(def f (fun (x) ...))` is a value binding, so `Codegen` emits it as a
/// delegate: `Func<int, int> f = (x) => ...`. A delegate costs an allocation
/// for the delegate and one for its closure each time the binding runs, and
/// each call through it is indirect. A local function that is only called
/// costs neither: C# keeps what it captures in a struct on the stack, and the
/// JIT can inline a call to it. So a binding whose name the scope uses only as
/// the head of a call is marked as a function binding, which `Codegen` emits as
/// a local function. A name used as a value keeps the delegate: each use would
/// make a new one.
///
/// Inference has already run, so the binding keeps its monomorphic type. Its
/// type variables are all in scope in the enclosing C# member, or the delegate
/// could not have been emitted either, so the local function is not generic.
///
/// The pass runs after inlining. An inlined call can leave a lambda as the
/// value, under the locals that hold the call's arguments:
///
///   (def add2 (twice h))  ~>  (def add2 (let ((g h)) (fun (x) (g (g x)))))
///
/// Such locals are moved out in front of the binding, so that the lambda is
/// the value. They run at the same point as before. A local is moved only when
/// the scope of the binding does not mention its name, so it captures nothing.
/// The moved locals are then promoted in turn: `g` above is only called too.

/// Is every mention of `name` in `expr` the head of a call with `arity`
/// arguments and no keyword arguments?
///
/// Shadowing is ignored. A mention of an inner binding of the same name is
/// counted as well, so the answer can be no where yes was correct, but never
/// the reverse.
let rec private onlyCalled (name: string) (arity: int) (expr: TypedExpr) : bool =
    match expr.Node with
    | TIdent(n, _) -> n <> name
    | TSet(n, _)
    | TRecordUpdate(n, _)
    | TRecordSet(n, _) when n = name -> false
    | TApply({ Node = TIdent(n, _) }, args, kwArgs) when n = name ->
        args.Length = arity
        && kwArgs.IsEmpty
        && args |> List.forall (onlyCalled name arity)
    | _ -> TypeVisitor.children expr |> List.forall (onlyCalled name arity)

/// The parameters of a lambda that can be emitted as a local function.
let private lambdaParams (value: TypedExpr) : string list option =
    match value.Node, value.Type with
    | TLambda(args, _), TFun(argTypes, _, _) when args.Length = argTypes.Length -> Some args
    | _ -> None

/// Splits `value` into the locals in front of a lambda and the lambda.
///
/// The locals come back outermost first, each as the name it binds and a
/// function that puts it back around a new scope.
let rec private peel (value: TypedExpr) : ((string * (TypedExpr -> TypedExpr)) list * TypedExpr) option =
    let around (n: string) (rest: TypedExpr) (rebuild: TypedExpr -> TExprNode) =
        peel rest
        |> Option.map (fun (outer, lambda) ->
            (n, (fun (inner: TypedExpr) -> { value with Type = inner.Type; Node = rebuild inner })) :: outer, lambda)

    match value.Node with
    | TLambda _ -> Some([], value)
    | TLet(n, isFun, fn, v, rest) -> around n rest (fun inner -> TLet(n, isFun, fn, v, inner))
    | TLetMutable(n, v, rest) -> around n rest (fun inner -> TLetMutable(n, v, inner))
    | _ -> None

/// Promotes the binding at the top of `expr`, if it is one that qualifies.
/// The children have already been visited.
let rec private promoteNode (expr: TypedExpr) : TypedExpr =
    match expr.Node with
    | TLet(name, false, fn, value, body) when fn = noParams && name <> "_" ->
        let promoted =
            peel value
            |> Option.bind (fun (outer, lambda) ->
                match lambdaParams lambda with
                | Some args when onlyCalled name args.Length body ->
                    let mentioned = LoopLowering.referencedNames body

                    if outer |> List.exists (fun (n, _) -> n <> "_" && (n = name || mentioned.Contains n)) then
                        None
                    else
                        let binding =
                            { expr with Node = TLet(name, true, onlyParams args, lambda, body) }

                        // Innermost first, so each moved local sees the scope
                        // that now holds the binding.
                        Some(List.foldBack (fun (_, wrap) inner -> promoteNode (wrap inner)) outer binding)
                | _ -> None)

        defaultArg promoted expr

    // A group that `LoopLowering` did not make a loop. A member that holds a
    // lambda becomes a local function when the body and every member only call
    // it.
    | TLetRec(bindings, body) ->
        let scope = body :: (bindings |> List.map (fun (_, _, _, v) -> v))

        let promote (n: string, isFun, fn: LocalFun, v: TypedExpr) =
            match isFun, lambdaParams v with
            | false, Some args when fn = noParams && scope |> List.forall (onlyCalled n args.Length) ->
                n, true, onlyParams args, v
            | _ -> n, isFun, fn, v

        { expr with Node = TLetRec(List.map promote bindings, body) }

    | _ -> expr

let run (decls: TDecl list) : TDecl list =
    decls |> List.map (TypeVisitor.mapDecl (TypeVisitor.mapExpr promoteNode))
