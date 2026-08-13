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
        | TPKeyword _
        | TPSymbol _
        | TPIdent _ as leaf -> leaf
        | TPList(items, tailOpt) -> TPList(List.map fp items, Option.map fp tailOpt)
        | TPVec(items, tailOpt) -> TPVec(List.map fp items, Option.map fp tailOpt)
        | TPTuple items -> TPTuple(List.map fp items)
        | TPConstruct(name, args) -> TPConstruct(name, List.map fp args)
        | TPTypeTest _ as leaf -> leaf
        | TPApp(expr, inner) -> TPApp(f expr, fp inner)
        | TPAs(inner, name) -> TPAs(fp inner, name)

    { pat with Node = node }

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
        | TIdent _
        | TKeyword _
        | TSymbol _ as leaf -> leaf

        | TLet(name, isFun, args, value, body) -> TLet(name, isFun, args, f value, f body)
        | TLetRec(bindings, body) ->
            TLetRec(bindings |> List.map (fun (n, isFun, args, e) -> n, isFun, args, f e), f body)
        | TLetTuple(names, value, body) -> TLetTuple(names, f value, f body)
        | TLambda(args, body) -> TLambda(args, f body)
        | TApply(target, args, kwArgs) ->
            TApply(f target, List.map f args, kwArgs |> List.map (fun (n, e) -> n, f e))
        | TTupleMake items -> TTupleMake(List.map f items)
        | TListMake items -> TListMake(List.map f items)
        | TVecMake items -> TVecMake(List.map f items)
        | TRecordMake fields -> TRecordMake(fields |> List.map (fun (k, v) -> k, f v))
        | TRecordUpdate(name, fields) -> TRecordUpdate(name, fields |> List.map (fun (k, v) -> k, f v))
        | TLetMutable(name, value, body) -> TLetMutable(name, f value, f body)
        | TSet(name, value) -> TSet(name, f value)
        | TIf(c, t, e) -> TIf(f c, f t, f e)
        | TWhen(c, body, negated) -> TWhen(f c, f body, negated)
        | TTryFinally(body, cleanup) -> TTryFinally(f body, f cleanup)
        | TTryCatch(body, exceptions) -> TTryCatch(f body, exceptions)
        | TSeq body -> TSeq(f body)
        | TBjo body -> TBjo(f body)
        | TTaskEvent(clrType, name, args, payload, isVoid) ->
            TTaskEvent(clrType, name, List.map f args, payload, isVoid)
        | TYield value -> TYield(f value)
        | TYieldFrom source -> TYieldFrom(f source)
        | TMatch(target, clauses) -> TMatch(f target, List.map mapClause clauses)
        | TInterfaceCall(iType, mName, dict, args) -> TInterfaceCall(iType, mName, f dict, List.map f args)
        | TTraitCall(tref, args, kwArgs) ->
            TTraitCall(tref, List.map f args, kwArgs |> List.map (fun (n, e) -> n, f e))
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
        | TRecur(index, args) -> TRecur(index, List.map f args)

        // Foreign .NET interop. The metadata is not an expression and is
        // carried through untouched: it records what the type checker resolved
        // against .NET metadata, and no later pass may second-guess it.
        | TDotMethodCall(target, name, args, meta) -> TDotMethodCall(f target, name, List.map f args, meta)
        | TDotPropertyGet(target, name, t) -> TDotPropertyGet(f target, name, t)
        | TNewObject(clrName, args, meta) -> TNewObject(clrName, List.map f args, meta)
        | TForeignStaticCall(clrType, name, args, meta) -> TForeignStaticCall(clrType, name, List.map f args, meta)
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

/// Deep, top-down rewrite: `f` is applied to the node first, then to its children.
let rec mapExprTopDown (f: TypedExpr -> TypedExpr) (expr: TypedExpr) : TypedExpr =
    f expr |> mapChildren (mapExprTopDown f)

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
    | TExport _
    | TReExport _
    | TType _
    | TTypeRec _
    | TTrait _
    | TExtern _
    | TImportExtern _
    | TImportClass _ -> decl

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
