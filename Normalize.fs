module Bjolang.Normalize

open Bjolang.Parser

/// Source-to-source rewriting of the *untyped* AST, run before `LetRecify`.
///
/// So far it does one thing: an application whose callee is a literal `fun`
/// becomes a chain of `let` bindings. That shape is not something people write
/// by hand — it is what a macro expands to whenever it wants to name its
/// operands, and what several desugarings leave behind — and every later pass
/// pays for it. A lambda that survives to `Codegen` is an allocated `Func<>`
/// with a call through it; the `let` chain it should have been is a plain local
/// and a straight-line body that `LoopLowering` can still see tail calls
/// through.
///
/// Two decisions shape everything here.
///
/// **It is a guarantee, not a heuristic.** Wherever the shape matches and the
/// preconditions hold, the rewrite fires. A macro author is meant to be able to
/// rely on it, so there is no size cutoff and no "only if used once" test — the
/// argument is *bound*, never substituted, so firing can never duplicate work
/// or lose an effect and there is nothing for a heuristic to protect.
///
/// **It is type-preserving.** Exactly the same set of programs typechecks
/// before and after; see `betaReduce` on `isFun`. Whether a program compiles
/// must not depend on whether an optimization ran, so any rewrite that would
/// change generalization does not belong in this pass.
///
/// What this forecloses: constant folding, dead-binding elimination and
/// eta-reduction are all deliberately absent. The first two would need to know
/// which calls are pure, and this pass runs before type checking, so it cannot
/// ask. A general inliner needs the same information plus a cost model, and
/// would break the guarantee above by having to decline.

// ---------------------------------------------------------------------------
// Beta-reduction
// ---------------------------------------------------------------------------

/// `((fun (x y) body) a b)` => `(let ((x a)) (let ((y b)) body))`.
///
/// A nullary lambda falls out of the same fold: no parameters means no
/// bindings, so `((fun () body))` is `body`. That is not a second rule to keep
/// in step with this one, and writing it as one would mean deciding what an
/// empty `let` chain means instead.
///
/// `funRange` is the lambda's, `appRange` the application's; both are only for
/// diagnostics, because every node this builds takes the range of the argument
/// it came from.
let private betaReduce
    (parameters: string list)
    (body: Expr)
    (args: Expr list)
    (funRange: Lexer.Range)
    (appRange: Lexer.Range)
    : Expr =

    // Raised here rather than left for `Inference`. At this node both counts
    // are known and the callee is a literal lambda, so the mistake can be
    // stated as one; by the time inference sees it, it is two arrow types that
    // will not unify, and the message names neither the lambda nor the count.
    if List.length parameters <> List.length args then
        failwithf
            $"The lambda at %s{Lexer.formatPos funRange} takes %d{List.length parameters} argument(s) but is applied to %d{List.length args} at %s{Lexer.formatPos appRange}. Give it one argument per parameter, or add the missing parameters to the (fun (...) ...) list."

    // Application arguments are *simultaneous* — every one of them is evaluated
    // in the enclosing scope — and a chain of nested `ELet`s is sequential. That
    // difference is a capture:
    //
    //     (def x 1) (def y 2)
    //     ((fun (x y) (- x y)) y x)   ; (- 2 1), and naively (- 2 2)
    //
    // Which is exactly the problem `(let ((x a) (y b)) ...)` has, so it is
    // exactly the same call: `Parser.simultaneous` freshens the parameters a
    // later argument would otherwise see, and hands back the names to bind and
    // a substitution for the body. Sharing it is the point — two
    // implementations of one rule would be one implementation and one latent
    // capture, and the one that fires less often would be the wrong one.
    let boundNameLists, bodySubst =
        simultaneous (List.map2 (fun p arg -> [ p ], arg) parameters args)

    let boundNames = boundNameLists |> List.map List.head

    // The substitution goes to the body, never to the arguments: the arguments
    // are the caller's expressions and mean what they meant where they were
    // written.
    let renamedBody = renameFree bodySubst body

    List.foldBack
        (fun (name, arg) rest ->
            ELet(
                name,
                // Not function-shaped, and that is the reason this pass is safe
                // to run at all: `Inference` generalizes a function-shaped
                // `let` and never a lambda parameter, so `isFun = false` is
                // what makes the rewrite type-preserving. With `isFun = true`,
                //
                //     ((fun (id) (Tuple (id 1) (id "a"))) (fun (y) y))
                //
                // would start compiling — but only because the optimization
                // fired, which is not a property a program may depend on.
                false,
                [],
                None,
                arg,
                rest,
                // The argument's own range, not the application's. Every
                // generated node sharing one range is what makes a long chain
                // of `bind` calls — a `(do ...)` block, say — report each of
                // its errors at the same character.
                exprRange arg
            ))
        (List.zip boundNames args)
        renamedBody

/// One bottom-up pass. Children first, so that a rewrite at this node sees
/// operands that are already normalized and the driver's fixpoint is only
/// needed for rewrites that expose *new* shapes further up.
let rec private rewriteExpr (expr: Expr) : Expr =
    match expr with
    | EInt _
    | EString _
    | EChar _
    | EBool _
    | EQuotedSymbol _
    | EKeyword _
    | EIdent _ -> expr

    | ETuple(exprs, r) -> ETuple(List.map rewriteExpr exprs, r)
    | EList(exprs, r) -> EList(List.map rewriteExpr exprs, r)
    | EVec(exprs, r) -> EVec(List.map rewriteExpr exprs, r)
    | ECast(t, e, r) -> ECast(t, rewriteExpr e, r)

    | EApp(target, args, r) ->
        let target' = rewriteExpr target
        let args' = List.map rewriteExpr args

        match target' with
        | EFun(parameters, body, Ordinary, funRange) -> betaReduce parameters body args' funRange r

        // A `bjoroutine` in head position is left exactly as it is. Its body
        // may suspend, and hoisting that body into the caller would move a
        // yield point into a function that is not coloured to have one —
        // precisely the property `ColourCheck` decides later, from the shape
        // this pass would have destroyed.
        | EFun(_, _, Suspending, _) -> EApp(target', args', r)

        | _ -> EApp(target', args', r)

    | ELet(name, isFun, args, typeAnn, value, body, r) ->
        ELet(name, isFun, rewriteArgs args, typeAnn, rewriteExpr value, rewriteExpr body, r)

    | ELetMono(name, value, body, r) -> ELetMono(name, rewriteExpr value, rewriteExpr body, r)

    | ELetRec(bindings, body, r) ->
        let bindings' =
            bindings
            |> List.map (fun (n, isFun, args, t, value) -> (n, isFun, rewriteArgs args, t, rewriteExpr value))

        ELetRec(bindings', rewriteExpr body, r)

    | ELetTuple(names, value, body, r) -> ELetTuple(names, rewriteExpr value, rewriteExpr body, r)

    | ELetMutable(name, typeAnn, value, body, r) -> ELetMutable(name, typeAnn, rewriteExpr value, rewriteExpr body, r)

    | ESet(name, value, r) -> ESet(name, rewriteExpr value, r)

    | EIf(cond, t, f, r) -> EIf(rewriteExpr cond, rewriteExpr t, rewriteExpr f, r)
    | EWhen(cond, body, negated, r) -> EWhen(rewriteExpr cond, rewriteExpr body, negated, r)

    // The lambda itself is untouched whatever its colour: only a lambda in head
    // position is rewritten, and one that is a value has a caller this pass
    // cannot see.
    | EFun(args, body, colour, r) -> EFun(args, rewriteExpr body, colour, r)

    | ERecordUpdate(baseRec, fields, r) -> ERecordUpdate(baseRec, fields |> List.map (fun (k, v) -> k, rewriteExpr v), r)
    | ERecordSet(baseRec, fields, r) -> ERecordSet(baseRec, fields |> List.map (fun (k, v) -> k, rewriteExpr v), r)

    | EGetField(target, field, r) -> EGetField(rewriteExpr target, field, r)

    | EMatch(target, clauses, r) ->
        let clauses' =
            clauses
            |> List.map (fun (p, guard, b) -> (p, Option.map rewriteExpr guard, rewriteExpr b))

        EMatch(rewriteExpr target, clauses', r)

    | ETryFinally(body, cleanup, r) -> ETryFinally(rewriteExpr body, rewriteExpr cleanup, r)
    | ETryCatch(body, exceptions, r) -> ETryCatch(rewriteExpr body, exceptions, r)

    | ESeq(body, r) -> ESeq(rewriteExpr body, r)
    | EBjo(body, r) -> EBjo(rewriteExpr body, r)
    | ETaskEvent(body, r) -> ETaskEvent(rewriteExpr body, r)
    | EYield(value, r) -> EYield(rewriteExpr value, r)
    | EYieldFrom(value, r) -> EYieldFrom(rewriteExpr value, r)

/// A keyword parameter's default is an expression like any other, and is the
/// only expression an argument list holds.
and private rewriteArgs (args: DefunArg list) : DefunArg list =
    args
    |> List.map (function
        | KeywordArg(n, defaultExpr) -> KeywordArg(n, rewriteExpr defaultExpr)
        | other -> other)

// ---------------------------------------------------------------------------
// Driver
// ---------------------------------------------------------------------------

/// Enough for any nesting a program actually contains: one pass reduces every
/// applied lambda in the tree, and a further pass is only needed where reducing
/// one exposed another that was not syntactically visible before.
let private iterationCap = 20

/// Rewrites to a fixpoint.
///
/// Iterating rather than trusting a single pass is what will keep this correct
/// once there is more than one rule: rewrites feed each other, and "run
/// everything once in the right order" is an ordering constraint nobody can
/// check. The cap is an assertion, not a budget — hitting it means two rules
/// undo each other, and a silent cap would turn that into a slow compiler
/// instead of a bug report.
let normalizeExpr (expr: Expr) : Expr =
    let rec go (n: int) (current: Expr) =
        if n > iterationCap then
            failwithf
                $"Internal error: Normalize did not reach a fixpoint within %d{iterationCap} iterations on the expression at %s{Lexer.formatPos (exprRange current)}. Two rewrite rules are undoing each other; this is a compiler bug, please report it with the source that triggered it."

        let next = rewriteExpr current
        if next = current then current else go (n + 1) next

    go 1 expr

/// Walks declarations, normalizing every body.
let rec normalizeDecl (decl: Decl) : Decl =
    match decl with
    | DDef(name, expr, r) -> DDef(name, normalizeExpr expr, r)
    | DDefTuple(names, expr, r) -> DDefTuple(names, normalizeExpr expr, r)
    | DDefMutable(name, expr, r) -> DDefMutable(name, normalizeExpr expr, r)
    | DDefun(name, args, body, colour, r) ->
        let normalizedArgs =
            args
            |> List.map (function
                | KeywordArg(n, defaultExpr) -> KeywordArg(n, normalizeExpr defaultExpr)
                | other -> other)

        DDefun(name, normalizedArgs, normalizeExpr body, colour, r)
    | DModule(name, decls, r) -> DModule(name, normalizeModule decls, r)
    | DTrait(name, implementor, arity, assocTypes, signatures, defaults, clr, r) ->
        // A trait default is a `DDefun` whose body is checked once per impl, so
        // it holds real code and is normalized like any other.
        DTrait(name, implementor, arity, assocTypes, signatures, List.map normalizeDecl defaults, clr, r)
    | DImpl(traitName, target, assocTypes, constraints, methods, r) ->
        DImpl(traitName, target, assocTypes, constraints, List.map normalizeDecl methods, r)
    // An inline template's body is untyped source read back out of another
    // module's metadata, and that module was compiled by this same pipeline —
    // so it has been normalized already, at its origin.
    | DInlineImpl _
    // Types, imports, exports, aliases and signatures carry no executable body.
    | DSignature _
    | DImport _
    | DAlias _
    | DExport _
    | DReExport _
    | DType _
    | DTypeRec _
    | DExtern _
    | DImportAlias _
    | DImportExtern _
    | DImportClass _
    | DMacro _
    | DImplExtern _ -> decl

and normalizeModule (decls: Decl list) : Decl list = List.map normalizeDecl decls
