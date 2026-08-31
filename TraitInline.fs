module Bjolang.TraitInline

open Bjolang.TypedAST
open Bjolang.Unification

/// Splices statically resolved trait method bodies into their call sites.
///
/// This is a pass of its own rather than part of dictionary lowering, and it
/// runs *before* it: the dictionary pass then sees the inlined result and
/// handles any interface-trait dispatch inside it with no changes at all.
///
/// It also runs before `LoopLowering`, and must. A `TRecur` carries an *index*
/// into the innermost enclosing `TLoop`, so splicing a body containing one into
/// a different function afterwards produces a silently wrong jump. Running first
/// also means the tail-recursive groups that inlining *creates* still become
/// loops.

// ---------------------------------------------------------------------------
// Warnings
// ---------------------------------------------------------------------------

let private warn = Diagnostics.warn

// ---------------------------------------------------------------------------
// Substitution and beta reduction
// ---------------------------------------------------------------------------

/// How many times `name` is referenced.
let rec private occurrences (name: string) (expr: TypedExpr) : int =
    let here =
        match expr.Node with
        | TIdent(n, _) when n = name -> 1
        | TSet(n, _) when n = name -> 1
        | TRecordUpdate(n, _) when n = name -> 1
        | TRecordSet(n, _) when n = name -> 1
        | _ -> 0

    TypeVisitor.children expr |> List.sumBy (occurrences name) |> (+) here

/// Replaces every reference to `name` with `replacement`.
///
/// Safe without a scope check because the body was freshened at the splice:
/// every binder in it is a name nothing else in the program can mention, so no
/// binder can capture a free variable of `replacement` and no binder can shadow
/// `name`.
let rec private substitute (name: string) (replacement: TypedExpr) (expr: TypedExpr) : TypedExpr =
    match expr.Node with
    | TIdent(n, _) when n = name -> replacement
    | _ -> TypeVisitor.mapChildren (substitute name replacement) expr

/// An argument that may be duplicated or reordered without changing what the
/// program does or how long it takes.
let rec private isTrivial (expr: TypedExpr) : bool =
    match expr.Node with
    | TIdent _
    | TInt _
    | TString _
    | TKeyword _
    | TSymbol _ -> true
    | TGetField(target, _) -> isTrivial target
    | _ -> false

/// Binds `parameters` to `args` inside `body`.
///
/// This one decision is what makes monadic code compile to a loop rather than
/// grow the stack, so the two cases are worth spelling out:
///
///   * A **lambda** argument used at most once is *substituted*. Given beta
///     reduction on a direct application, that turns `(k x)` inside `bind`'s
///     body into `((fun (x) ...) x)`, which reduces, which puts the recursive
///     call back in tail position where `LoopLowering` can see it. Let-binding
///     it leaves `(k x)` against a variable, beta never fires, and the monadic
///     loop becomes stack growth.
///   * The occurrence check is not optional. Substituting a lambda used twice
///     duplicates it, and in a `(do ...)` block the continuation is the entire
///     rest of the block — so a `bind` that mentions its continuation twice
///     would give code size exponential in nesting depth.
///
/// Everything else is bound with a `TLet`, in argument order with the body
/// innermost, which preserves left-to-right evaluation and evaluates each
/// argument exactly once.
let rec private bindArguments
    (describe: string)
    (parameters: (string * TypedExpr) list)
    (body: TypedExpr)
    : TypedExpr =

    match parameters with
    | [] -> body
    | (name, arg) :: rest ->
        let substitutable =
            if isTrivial arg then
                true
            else
                match arg.Node with
                | TLambda _ ->
                    let n = occurrences name body

                    if n <= 1 then
                        true
                    else
                        warn
                            $"%s{describe} mentions its parameter '%s{Gensym.baseName name}' %d{n} times and is given a function there, so it cannot be substituted. It is bound to a local instead — correct, but the call it wraps will not be a tail call, and a loop written through it will grow the stack."

                        false
                | _ -> false

        if substitutable then
            bindArguments describe rest (substitute name arg body)
        else
            let inner = bindArguments describe rest body

            { Type = inner.Type
              Range = arg.Range
              Node = TLet(name, false, noParams, arg, inner) }

/// Reduces `((fun (x ...) body) a ...)` in place.
///
/// Substituting a continuation is only half of the trick; without this the
/// result is a genuine closure call, which C# will not turn into a jump and
/// which `LoopLowering` cannot see through.
let rec betaReduce (expr: TypedExpr) : TypedExpr =
    let expr = TypeVisitor.mapChildren betaReduce expr

    match expr.Node with
    | TApply({ Node = TLambda(parameters, lambdaBody) }, args, [])
        when parameters.Length = args.Length ->
        // The lambda's own binders are freshened first: `args` are expressions
        // from the *caller's* scope and a binder inside the lambda could
        // otherwise capture one of their free variables.
        let freshBody, subst = AlphaRename.freshenTyped parameters lambdaBody
        let renamed = parameters |> List.map (fun p -> Map.tryFind p subst |> Option.defaultValue p)
        betaReduce (bindArguments "this lambda" (List.zip renamed args) freshBody)
    | _ -> expr

// ---------------------------------------------------------------------------
// Qualification
// ---------------------------------------------------------------------------

/// Rewrites the free names of a spliced body to name the module they actually
/// came from.
///
/// Applied *after* inference, never before: `infer` fails hard on unbound names
/// and `Origin_Module::helper` is not a key in `env.Bindings`.
let applyQualification (qualification: Map<string, string>) (expr: TypedExpr) : TypedExpr =
    if Map.isEmpty qualification then
        expr
    else

    let rec go (e: TypedExpr) =
        let node =
            match e.Node with
            | TIdent(n, tArgs) ->
                match Map.tryFind n qualification with
                | Some q -> TIdent(q, tArgs)
                | None -> TIdent(n, tArgs)
            | TSet(n, v) ->
                let n' = Map.tryFind n qualification |> Option.defaultValue n
                TSet(n', go v)
            // A write reaching a splice is a write to whatever the target
            // resolved to there, exactly as `set!` is.
            | TRecordSet(n, fields) ->
                let n' = Map.tryFind n qualification |> Option.defaultValue n
                TRecordSet(n', fields |> List.map (fun (k, v) -> k, go v))
            | _ -> (TypeVisitor.mapChildren go e).Node

        { e with Node = node }

    go expr

// ---------------------------------------------------------------------------
// The pass
// ---------------------------------------------------------------------------

/// Keyed on the **constructor only**, never on the full type arguments.
///
/// That is deliberate: `Vec<Vec<int>>` and `Vec<int>` share a key, so the inner
/// one falls back to a call. Never incorrect, occasionally less inlining than
/// would be theoretically possible — and a type-argument-aware key is exactly
/// what reintroduces non-termination.
let private inlineKey (traitName: string) (methodName: string) (ctor: string) =
    $"%s{traitName}::%s{methodName}::%s{ctor}"

type private Ctx =
    { Env: Env
      /// Threaded functionally, so it pops on backtracking: a sibling call to
      /// the same method elsewhere in the tree still inlines, and only cycles on
      /// the *current path* fall back to a call.
      Active: Set<string> }

/// The call that stands in for an inlined body: the impl's own method, named
/// directly. Emitted whenever inlining would recur, whenever the occurrence
/// check refuses, and whenever no template was registered at all.
let private landingPad (env: Env) (tref: TraitRef) (ctor: string) (tyArgs: HMType list) (args: TypedExpr list) (kwArgs: (string * TypedExpr) list) (expr: TypedExpr) : TypedExpr =
    let kind =
        match Map.tryFind tref.Trait env.Registry.Traits with
        | Some info -> info.Kind
        | None -> InterfaceTrait

    // A conditional impl has no singleton to route through: its dictionary has
    // to be built out of the evidence its `(where ...)` demands, and that is
    // `Lowering`'s job. Handing the call back unchanged is what asks for it —
    // the node still says which implementation was chosen.
    let conditional =
        match Map.tryFind (tref.Trait, ctor) env.Registry.ImplTargets with
        | Some target -> not target.Constraints.IsEmpty
        | None -> false

    if conditional then
        { expr with Node = TTraitCall(tref, args, kwArgs) }
    else

    // The method's own arrow, as instantiation left it. The pad names the
    // impl's method, which was emitted at the colour the trait declared, so an
    // `->` here would leave the call unawaited against an `async` member — and
    // the parameters carry the cell a `-?->` was instantiated to, which is what
    // `EffectGraph` reads to choose between this pad and the twin's.
    let calleeType =
        match tref.MethodType with
        | TFun(paramTypes, _, eff) -> TFun(paramTypes, expr.Type, eff)
        | other -> other

    let callee =
        { Type = calleeType
          Range = expr.Range
          Node = TIdent(landingPadName kind tref.Trait ctor tref.Method, tyArgs) }
        : TypedExpr

    { expr with Node = TApply(callee, args, kwArgs) }

let rec private inlineExpr (ctx: Ctx) (expr: TypedExpr) : TypedExpr =
    match expr.Node with
    | TTraitCall(tref, args, kwArgs) ->
        let args = args |> List.map (inlineExpr ctx)
        let kwArgs = kwArgs |> List.map (fun (n, e) -> n, inlineExpr ctx e)

        match tref.Resolved with
        // Unresolved: an interface trait at a generic receiver. The dictionary
        // pass owns it from here, exactly as before.
        //
        // **This is also the guard that keeps blanket impls sound.** A blanket
        // may differ in *behaviour* from a specific impl, so splicing its body
        // into a function whose implementor is still a type variable would bake
        // in the wrong answer for every caller that has a specific impl. It
        // cannot happen today because a `TVar` hole never resolves — `infer`
        // leaves `Resolved = None` and the call goes out through a dictionary,
        // which is chosen at the concrete instantiation site. An inlining
        // optimization that ever learned to splice at a `TVar` would have to
        // exclude blankets explicitly.
        | None -> { expr with Node = TTraitCall(tref, args, kwArgs) }

        | Some(ctor, tyArgs) ->
            let key = inlineKey tref.Trait tref.Method ctor
            let template = Map.tryFind (tref.Trait, tref.Method, ctor) ctx.Env.Registry.InlineMethods

            // A suspending method is never spliced, and the reason is not the
            // splice itself — it is that splicing dissolves the *call*, and the
            // call is what `ColourCheck` reads to decide whether a yield point
            // is allowed where it is written.
            //
            // Inlined, `(defun (bad n) (fetch n))` became the impl's body with
            // no call left in it, and was accepted; with a generic receiver the
            // same source kept its call and was refused. A program whose
            // acceptance depends on whether the inliner could see the
            // implementor is what this codebase avoids everywhere else, and the
            // landing pad is always a correct answer.
            //
            // The saving given up is negligible against what is left: an await
            // is a state-machine transition, and no call it replaces is cheaper
            // than that.
            //
            // A `-?->` method is spliced or not by the same rule, one step
            // later: the *callback* it was handed decides. Ordinary, nothing
            // suspends and the body is pasted as it always was. Suspending, the
            // splice would leave a yield point behind in a body written in
            // another file — `(map slow xs)` inside a `(seq ...)` reported
            // `list-map` at `prelude.dll:1`, naming a call the reader never
            // wrote — so it becomes the twin's landing pad instead, and the
            // refusal lands on the line that asked for it.
            match template with
            | Some tpl when
                not (Set.contains key ctx.Active)
                && tpl.Params.Length = args.Length
                && kwArgs.IsEmpty
                && not (callSuspends tref.MethodType)
                && not (wantsSuspendingCopy tref.MethodType)
                ->
                spliceTemplate ctx tref ctor tyArgs tpl args expr
            | _ -> landingPad ctx.Env tref ctor tyArgs args kwArgs expr

    | _ -> TypeVisitor.mapChildren (inlineExpr ctx) expr

and private spliceTemplate
    (ctx: Ctx)
    (tref: TraitRef)
    (ctor: string)
    (tyArgs: HMType list)
    (tpl: InlineTemplate)
    (args: TypedExpr list)
    (expr: TypedExpr)
    : TypedExpr =

    let key = inlineKey tref.Trait tref.Method ctor
    let describe = $"the '%s{tref.Method}' implementation of '%s{tref.Trait}' for '%s{ctor}'"

    try
        // 1. Freshen at the splice. Mandatory, and *not* something the global
        //    uniquifying pass can do afterwards: renaming preserves meaning, it
        //    cannot recover a meaning the splice already destroyed.
        let freshBody, subst = AlphaRename.freshen tpl.Params tpl.Body
        let freshParams = tpl.Params |> List.map (fun p -> Map.tryFind p subst |> Option.defaultValue p)

        // 2. Each parameter is bound at the argument's *concrete* type. Not a
        //    fresh metavariable: the whole point of re-inference is that the
        //    body gets to see what it is actually being applied to.
        //    Checked under the module the *body* came from, not the one it is
        //    landing in: a template may name something its own module is
        //    allowed to name and this one is not.
        let spliceEnv =
            List.zip freshParams args
            |> List.fold
                (fun acc (name, (arg: TypedExpr)) ->
                    addBinding
                        name
                        { Scheme = Scheme([], [], prune ctx.Env.Registry arg.Type)
                          IsMutable = false }
                        acc)
                { ctx.Env with CurrentModule = tpl.OriginModule }

        // 3. Re-infer the body expression directly. Never re-wrapped as a
        //    lambda first: `infer`'s `EFun` case binds each parameter to a fresh
        //    metavariable in a scope of its own, which would throw away the
        //    concrete argument types just supplied.
        let bodyType, typedBody = Inference.infer spliceEnv freshBody
        unify ctx.Env.Registry bodyType expr.Type

        // Obligations raised by the body — a `bind` calling `bind` — are
        // discharged here, before anything looks at the result.
        Inference.solvePending spliceEnv

        // 4. Free names now say which module they came from.
        let qualified = applyQualification tpl.Qualification typedBody

        // 5. Recurse, with this key held down for the current path only.
        let inlined = inlineExpr { ctx with Active = Set.add key ctx.Active } qualified

        // 6. Bind the arguments, then reduce whatever the substitution exposed.
        betaReduce (bindArguments describe (List.zip freshParams args) inlined)
    with ex ->
        // A template that will not re-infer here is a compile error waiting to
        // happen in generated C#, and the landing pad is always a correct
        // answer. Say so rather than failing the build over an optimization.
        warn
            $"could not inline %s{describe} at %s{Lexer.formatPos expr.Range}: %s{ex.Message}. Falling back to a call."

        landingPad ctx.Env tref ctor tyArgs args [] expr

// ---------------------------------------------------------------------------
// Declarations
// ---------------------------------------------------------------------------

let rec private inlineDecl (ctx: Ctx) (decl: TDecl) : TDecl =
    match decl with
    | TModule(name, decls, r) -> TModule(name, decls |> List.map (inlineDecl ctx), r)

    | TImpl(traitName, kind, holeArity, targetType, assoc, dicts, methods, r) ->
        // A blanket impl's methods key off the sentinel exactly as a specific
        // impl's key off their constructor, and a tuple's off its arity.
        // Getting this wrong would not be a miscompile — the key would simply
        // never match, and every self-call in such a body would inline one
        // gratuitous copy of itself before falling back.
        let ctor = implCtorKey targetType |> Option.defaultValue ""

        // A method of an impl is its own recursive edge. Holding its key down
        // over its own body means a self-call becomes the landing pad, which
        // `LoopLowering` then recognizes and turns into a loop — rather than one
        // gratuitous copy of the body inside itself.
        let methodCtx (methodName: string) =
            { ctx with Active = Set.add (inlineKey traitName methodName ctor) ctx.Active }

        let methods =
            methods
            |> List.map (function
                | TDefun(n, _, _, _, _, _, _, _, _) as m -> inlineDecl (methodCtx n) m
                | m -> inlineDecl ctx m)

        TImpl(traitName, kind, holeArity, targetType, assoc, dicts, methods, r)

    | _ -> TypeVisitor.mapDecl (inlineExpr ctx) decl

/// Inlines every statically resolvable trait call in the program.
let run (env: Env) (decls: TDecl list) : TDecl list =
    let ctx = { Env = env; Active = Set.empty }
    decls |> List.map (inlineDecl ctx)
