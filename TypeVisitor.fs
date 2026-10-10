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

module Bjolang.TypeVisitor

open Bjolang.TypedAST

/// Generic traversal helpers over the typed AST.
///
/// This module owns the *single* exhaustive match over `TExprNode`. Every other
/// pass (trait-constraint collection, dictionary lowering, tail-call analysis)
/// should delegate its boring structural cases here so that adding a new node
/// only requires updating this file.

// ---------------------------------------------------------------------------
// Shallow (one level) mapping
// ---------------------------------------------------------------------------

/// Rebuilds `pat` applying `f` to every expression held *directly* inside it and
/// `fp` to every directly nested sub-pattern.
let mapPatternChildrenWith (f: TypedExpr -> TypedExpr) (fp: TypedPattern -> TypedPattern) (pat: TypedPattern) : TypedPattern =
    let node =
        match pat.Node with
        | TPWildcard
        | TPInt _
        | TPString _
        | TPChar _
        | TPBool _
        | TPKeyword _
        | TPSymbol _
        | TPIdent _ as leaf -> leaf
        | TPList(items, tailOpt) -> TPList(List.map fp items, Option.map fp tailOpt)
        | TPVec(items, tailOpt) -> TPVec(List.map fp items, Option.map fp tailOpt)
        | TPArray(items, tailOpt) -> TPArray(List.map fp items, Option.map fp tailOpt)
        | TPTuple items -> TPTuple(List.map fp items)
        | TPConstruct(name, args) -> TPConstruct(name, List.map fp args)
        | TPRecord(name, fields) -> TPRecord(name, fields |> List.map (fun (f, p) -> f, fp p))
        | TPTypeTest _ as leaf -> leaf
        | TPApp(expr, inner) -> TPApp(f expr, fp inner)
        | TPAs(inner, name) -> TPAs(fp inner, name)
        | TPOr alts -> TPOr(List.map fp alts)
        | TPAnd alts -> TPAnd(List.map fp alts)

    { pat with Node = node }

/// A local function's keyword defaults, each mapped by `f`.
let private mapLocalFun (f: TypedExpr -> TypedExpr) (fn: LocalFun) : LocalFun =
    if fn.KeywordArgs.IsEmpty then
        fn
    else
        { fn with KeywordArgs = fn.KeywordArgs |> List.map (fun (n, t, d) -> n, t, f d) }

/// Applies `f` to each *immediate* sub-expression of `expr` and rebuilds the node.
/// `f` is responsible for any further recursion.
let mapChildren (f: TypedExpr -> TypedExpr) (expr: TypedExpr) : TypedExpr =
    let mapPat (p: TypedPattern) =
        // Patterns only ever contain expressions via TPApp; recurse structurally.
        let rec go p = mapPatternChildrenWith f go p
        go p

    let mapClause (c: TMatchClause) =
        { Pattern = mapPat c.Pattern
          Guard = Option.map f c.Guard
          Body = f c.Body }

    let node =
        match expr.Node with
        // Leaves
        | TInt _
        | TString _
        | TChar _
        | TBool _
        | TIdent _
        | TKeyword _
        | TSymbol _ as leaf -> leaf

        // A local function's keyword defaults are expressions of the program
        // like any other, and every pass built on this has to reach them.
        | TLet(name, isFun, fn, value, body) -> TLet(name, isFun, mapLocalFun f fn, f value, f body)
        | TLetRec(bindings, body) ->
            TLetRec(bindings |> List.map (fun (n, isFun, fn, e) -> n, isFun, mapLocalFun f fn, f e), f body)
        | TLetTuple(names, value, body) -> TLetTuple(names, f value, f body)
        | TLambda(args, body) -> TLambda(args, f body)
        | TApply(target, args, kwArgs) ->
            TApply(f target, List.map f args, kwArgs |> List.map (fun (n, e) -> n, f e))
        | TTupleMake items -> TTupleMake(List.map f items)
        | TListMake items -> TListMake(List.map f items)
        | TVecMake items -> TVecMake(List.map f items)
        | TRecordMake fields -> TRecordMake(fields |> List.map (fun (k, v) -> k, f v))
        | TRecordUpdate(name, fields) -> TRecordUpdate(name, fields |> List.map (fun (k, v) -> k, f v))
        | TRecordSet(name, fields) -> TRecordSet(name, fields |> List.map (fun (k, v) -> k, f v))
        | TLetMutable(name, value, body) -> TLetMutable(name, f value, f body)
        | TSet(name, value) -> TSet(name, f value)
        | TIf(c, t, e) -> TIf(f c, f t, f e)
        | TWhen(c, body, negated) -> TWhen(f c, f body, negated)
        | TTryFinally(body, cleanup) -> TTryFinally(f body, f cleanup)
        | TTryCatch(body, exceptions) -> TTryCatch(f body, exceptions)
        | TSeq body -> TSeq(f body)
        | THoist body -> THoist(f body)
        | TBjo(body, kind) -> TBjo(f body, kind)
        | TTaskEvent(receiver, clrType, name, args, payload, isVoid) ->
            TTaskEvent(Option.map f receiver, clrType, name, List.map f args, payload, isVoid)
        | TYield value -> TYield(f value)
        | TYieldFrom source -> TYieldFrom(f source)
        | TMatch(target, clauses) -> TMatch(f target, List.map mapClause clauses)
        | TWithReturn(label, body) -> TWithReturn(label, f body)
        | TReturn(label, value) -> TReturn(label, Option.map f value)
        | TDefMatch(binder, scrutinee, sequel, arms) ->
            TDefMatch(
                mapPat binder,
                f scrutinee,
                f sequel,
                arms
                |> List.map (fun (a: TDefMatchArm) ->
                    { Pattern = mapPat a.Pattern
                      Body = f a.Body })
            )
        | TInterfaceCall(iType, mName, mType, dict, args) -> TInterfaceCall(iType, mName, mType, f dict, List.map f args)
        | TTraitCall(tref, args, kwArgs) ->
            TTraitCall(tref, List.map f args, kwArgs |> List.map (fun (n, e) -> n, f e))
        | TDynPack(traitName, hole, value) -> TDynPack(traitName, hole, f value)
        | TThrow e -> TThrow(f e)
        | TIsInst(tgt, t) -> TIsInst(f tgt, t)
        | TIsInstCase(tgt, t, caseName) -> TIsInstCase(f tgt, t, caseName)
        | TCast(tgt, t) -> TCast(f tgt, t)
        | TCaseCast(tgt, t, caseName) -> TCaseCast(f tgt, t, caseName)
        | TGetField(tgt, name) -> TGetField(f tgt, name)
        | TTypeEq(a, b) -> TTypeEq(f a, f b)
        | TArrayMake items -> TArrayMake(List.map f items)
        | TLoop(members, bodyOpt) ->
            TLoop(members |> List.map (fun m -> { m with Body = f m.Body }), Option.map f bodyOpt)
        | TRecur(depth, index, args) -> TRecur(depth, index, List.map f args)

        // Foreign .NET interop. The metadata is not an expression and is
        // carried through untouched: it records what the type checker resolved
        // against .NET metadata, and no later pass may second-guess it.
        | TDotMethodCall(target, name, args, meta) -> TDotMethodCall(f target, name, List.map f args, meta)
        | TDotPropertyGet(target, name, t) -> TDotPropertyGet(f target, name, t)
        | TDotPropertySet(target, name, value) -> TDotPropertySet(f target, name, f value)
        | TNewObject(clrName, args, meta) -> TNewObject(clrName, List.map f args, meta)
        | TForeignStaticCall(clrType, name, args, meta) -> TForeignStaticCall(clrType, name, List.map f args, meta)
        | TClrMemberCall(traitName, method, implType, args) ->
            TClrMemberCall(traitName, method, implType, List.map f args)
        | TForeignStaticSet(clrType, name, value) -> TForeignStaticSet(clrType, name, f value)
        | TForeignStaticGet _ as leaf -> leaf

    { expr with Node = node }

/// Collects every *immediate* sub-expression of `expr`.
let children (expr: TypedExpr) : TypedExpr list =
    let acc = ResizeArray<TypedExpr>()

    mapChildren
        (fun e ->
            acc.Add e
            e)
        expr
    |> ignore

    List.ofSeq acc

// ---------------------------------------------------------------------------
// Deep traversals
// ---------------------------------------------------------------------------

/// Deep, bottom-up rewrite: children are rewritten before `f` is applied to the node.
let rec mapExpr (f: TypedExpr -> TypedExpr) (expr: TypedExpr) : TypedExpr =
    expr |> mapChildren (mapExpr f) |> f

/// Deep pre-order fold over `expr` and all of its sub-expressions.
let rec foldExpr (f: 'S -> TypedExpr -> 'S) (state: 'S) (expr: TypedExpr) : 'S =
    children expr |> List.fold (foldExpr f) (f state expr)

// ---------------------------------------------------------------------------
// Declarations
// ---------------------------------------------------------------------------

/// Applies `f` to every expression directly held by `decl`, recursing into
/// nested declaration groups (`TModule`, `TImpl`).
let rec mapDecl (f: TypedExpr -> TypedExpr) (decl: TDecl) : TDecl =
    match decl with
    | TDef(name, value, t, r) -> TDef(name, f value, t, r)
    | TDefTuple(names, value, t, r) -> TDefTuple(names, f value, t, r)
    | TDefPattern(pattern, value, binders, r) ->
        // A pattern holds an expression in one place, a `(:view step p)`, and
        // it is mapped for the reason a match clause's is: the step is code,
        // and a pass rewriting code has to reach it.
        let rec mapPat p = mapPatternChildrenWith f mapPat p
        TDefPattern(mapPat pattern, f value, binders, r)
    | TDefMutable(name, value, t, r) -> TDefMutable(name, f value, t, r)
    | TDefun(name, tyArgs, args, kwArgs, restArg, retType, effect, body, r) ->
        TDefun(
            name,
            tyArgs,
            args,
            kwArgs |> List.map (fun (n, t, e) -> n, t, f e),
            restArg,
            retType,
            effect,
            f body,
            r
        )
    | TModule(name, decls, r) -> TModule(name, decls |> List.map (mapDecl f), r)
    | TImpl(traitName, kind, holeArity, targetType, assoc, dicts, methods, r) ->
        TImpl(traitName, kind, holeArity, targetType, assoc, dicts, methods |> List.map (mapDecl f), r)
    | TImport _
    | TAlias _
    | TExport _
    | TReExport _
    | TType _
    | TTypeRec _
    | TTrait _
    | TExtern _
    | TImportExtern _
    | TImportClass _ -> decl

/// As `mapDecl`, but `f` is also handed the declaration that directly holds the
/// expression.
///
/// For passes whose answer depends on the enclosing definition rather than on
/// the expression alone — a body's colour is its `TDefun`'s effect, and nothing
/// in the body says so.
let rec mapDeclWithContext (f: TDecl -> TypedExpr -> TypedExpr) (decl: TDecl) : TDecl =
    match decl with
    | TModule(name, decls, r) -> TModule(name, decls |> List.map (mapDeclWithContext f), r)
    | TImpl(traitName, kind, holeArity, targetType, assoc, dicts, methods, r) ->
        TImpl(traitName, kind, holeArity, targetType, assoc, dicts, methods |> List.map (mapDeclWithContext f), r)
    | _ -> mapDecl (f decl) decl

/// Does a value of type `supplied` need lifting to sit in a `wanted` slot?
///
/// True exactly where subeffecting was allowed by `unifyEffect` and the two
/// types are still spelled differently in C#: an ordinary arrow accepted into a
/// suspending one. `Codegen` wraps such an argument in `Colour.Lift`, and the
/// blocking lint reads the same question to decide whether a fiber has reached
/// ordinary code through a value.
///
/// Two callers and one definition, for the reason `reachesAwait` has one: the
/// lint deciding differently from the emitter would mean either a wrapper with
/// no edge — a fiber parking with nothing to say so — or an edge with no
/// wrapper, which is a warning about a call that is not there.
let liftsToSuspending (wanted: HMType) (supplied: HMType) : bool =
    match wanted, supplied with
    | TFun(_, _, w), TFun(_, _, s) -> groundEffect w = EAsync && groundEffect s = ESync
    | _ -> false

/// Does evaluating this expression *in the member it is written in* reach an
/// `await`?
///
/// "In the member it is written in" is the whole content of the question, and
/// is why the sub-member cases answer `false` without looking inside: a lambda,
/// a sequence and a body-local function each open a C# member of their own, so
/// an await in one of them is that member's business and not this one's. A
/// local function that awaits still makes its *caller* await — but through the
/// call, which is the `TApply` case, not through the definition.
///
/// `bjo` is the one shape where the distinction is live: its operands are
/// evaluated here and its call is not.
///
/// `EffectGraph` uses it to decide whether a body-local function is async.
let rec reachesAwait (expr: TypedExpr) : bool = reachesAwaitExcept (fun _ -> false) expr

/// `reachesAwait`, not counting a call to a name `idle` holds: one about to be
/// pointed at a callee that does not suspend.
and reachesAwaitExcept (idle: string -> bool) (expr: TypedExpr) : bool =
    let reachesAwait = reachesAwaitExcept idle

    match expr.Node with
    // A function-shaped binding's value is a `TLambda`, so both are covered
    // here, and a `TLetRec` group's members likewise.
    | TLambda _
    | TSeq _
    // A hoisted body runs in a method of its own, and `HoistCheck` and
    // `ColourCheck` make sure it cannot await there either.
    | THoist _ -> false
    | TBjo(body, _) ->
        match body.Node with
        | TApply(target, args, kwArgs) ->
            reachesAwait target
            || List.exists reachesAwait args
            || kwArgs |> List.exists (snd >> reachesAwait)
        | _ -> false
    // A call to a member of the loop is a jump, or a call into a group whose
    // colour its bodies decide. It awaits only if some member awaits something
    // else. The call alone is no evidence: `EffectGraph` grounds the arrow of an
    // inlined loop's name to the colour of the member around it, so in a
    // bjoroutine every loop entry reads as suspending.
    | TLoop(members, bodyOpt) ->
        // Both spellings: the `TApply` case asks `idle` with the written one.
        let names =
            members
            |> List.collect (fun m -> [ m.LoopName; Naming.writtenName m.LoopName ])
            |> Set.ofList
        let exceptMembers = reachesAwaitExcept (fun n -> idle n || Set.contains n names)

        members |> List.exists (fun m -> exceptMembers m.Body)
        || bodyOpt |> Option.exists exceptMembers
    | TForeignStaticCall(_, _, _, Some meta) when meta.Await -> true
    | TDotMethodCall(_, _, _, Some meta) when meta.Await -> true
    | TApply(target, _, _) when
        callSuspends target.Type
        && not (
            match target.Node with
            | TIdent(name, _) -> idle (Naming.writtenName name)
            | _ -> false
        )
        ->
        true
    // A dispatched trait method whose trait declared `-bjo->`. The colour is on
    // the node rather than on an arrow, so the case above cannot see it.
    | TInterfaceCall(_, _, methodType, _, _) when callSuspends methodType -> true
    | _ -> children expr |> List.exists reachesAwait

/// Deep pre-order fold over every expression contained in `decl`.
let foldDecl (f: 'S -> TypedExpr -> 'S) (state: 'S) (decl: TDecl) : 'S =
    let acc = ref state

    mapDecl
        (fun e ->
            acc.Value <- foldExpr f acc.Value e
            e)
        decl
    |> ignore

    acc.Value
