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
// Bodies of functions that take a function
// ---------------------------------------------------------------------------

/// The largest body, in untyped nodes, that is published for inlining. A call
/// is inlined only where it is given a function, and the copy is what removes
/// the call through it; past this size the copy costs more than the call.
/// The walks of the standard library — `list-map`, `vec-map`, `vec-filter`,
/// `vec-fold`, `map-fold` — are below it.
[<Literal>]
let private maxInlineBodySize = 100

let rec private exprSize (expr: Ast.Expr) : int =
    1 + (Ast.exprChildren expr |> List.sumBy exprSize)

let rec private solvedType (t: HMType) : HMType =
    match t with
    | TMeta { Value = Some inner } -> solvedType inner
    | _ -> t

let private isFunctionType (t: HMType) =
    match solvedType t with
    | TFun _ -> true
    | _ -> false

/// Does `expr` have a yield point outside the lambdas in it? The same calls
/// `ColourCheck` reads as one: a call whose callee suspends or is given a
/// suspending callback, an `#:async` import, and a trait method that
/// suspends. A lambda in `expr` has its own colour, so it is not looked in.
let rec private hasYieldPoint (expr: TypedExpr) : bool =
    let here =
        match expr.Node with
        | TApply(target, _, _) -> callSuspends target.Type || wantsSuspendingCopy target.Type
        | TForeignStaticCall(_, _, _, Some meta) -> meta.Await
        | TDotMethodCall(_, _, _, Some meta) -> meta.Await
        | TTraitCall(tref, _, _) -> callSuspends tref.MethodType || wantsSuspendingCopy tref.MethodType
        | TInterfaceCall(_, _, methodType, _, _) -> callSuspends methodType
        | _ -> false

    match expr.Node with
    | TLambda _ -> false
    | _ -> here || TypeVisitor.children expr |> List.exists hasYieldPoint

/// Does `expr` call a `defbjouble` whose copy the colour around the call
/// chooses? Such a call in a lambda or in a function's body takes the parking
/// copy; spliced into a bjoroutine, it would take the suspending one. Inlining
/// must not change which copy runs, so a body with one is not spliced.
let rec private callsColourChosenDouble (env: Env) (expr: TypedExpr) : bool =
    match expr.Node with
    | TApply({ Node = TIdent(name, _) }, _, _) when Map.containsKey name env.Registry.DoubleDefs -> true
    | _ -> TypeVisitor.children expr |> List.exists (callsColourChosenDouble env)

/// Publishes the body of every function of this module that takes a function,
/// so that a call given a known function can be inlined, here and in each
/// module that imports it. See `inlineFunctionCall`.
///
/// The bodies go in `ConstrainedBodies`, which `Exports` and the metadata
/// already carry. `Monomorphise` copies only the bodies whose function has a
/// constraint that costs a dictionary, so it does not see these.
///
/// A function is published if it has a parameter of function type, has only
/// mandatory parameters, has no `(where ...)`, is not a `defbjouble` whose
/// copy the colour around a call chooses, is not written `#:no-inline`, and
/// its body is at most `maxInlineBodySize` nodes.
let publishBodies
    (env: Env)
    (moduleOf: Map<string, string * string>)
    (decls: Ast.Decl list)
    (typed: TDecl list)
    : Env =
    let ownModuleName, ownDecls =
        match List.tryLast decls with
        | Some(Ast.DModule(name, inner, _)) -> name, inner
        | _ -> "", decls

    let typedOwn =
        match List.tryLast typed with
        | Some(TModule(_, inner, _)) -> inner
        | _ -> typed

    let takesFunction =
        typedOwn
        |> List.choose (function
            | TDefun(name, _, args, [], None, _, _, _, _) when args |> List.exists (snd >> isFunctionType) -> Some name
            | _ -> None)
        |> Set.ofList

    let unconstrained (name: string) =
        match Map.tryFind name env.Bindings with
        | Some { Scheme = Scheme(_, [], _) } -> true
        | _ -> false

    // A `defbjouble` that takes a `-?->` gives its `#:bjo` body: it is the
    // Bjolang loop, where the `#:sync` body calls a .NET method with a
    // delegate, and inlining that gains nothing. Re-inferred with an ordinary
    // callback the `#:bjo` body is ordinary, and does what the `#:sync` body
    // does. Such a function is not in `DoubleDefs`, which holds the ones
    // whose copy the colour around the call chooses.
    //
    // A definition written `#:no-inline` is not published.
    let noInline =
        ownDecls
        |> List.choose (function
            | Ast.DNoInline(name, _) -> Some name
            | _ -> None)
        |> Set.ofList

    let candidates =
        ownDecls
        |> List.choose (function
            | Ast.DDefun(name, args, body, Ast.Ordinary, _) -> Some(name, args, body)
            | Ast.DDefDouble(name, args, _, bjoBody, _) -> Some(name, args, bjoBody)
            | _ -> None)
        |> List.filter (fun (name, _, _) -> not (Set.contains name noInline))

    let bodies =
        candidates
        |> List.choose (function
            | (name, args, body) when
                Set.contains name takesFunction
                && name <> "main"
                && unconstrained name
                && not (Map.containsKey name env.Registry.ConstrainedBodies)
                && not (Map.containsKey name env.Registry.DoubleDefs)
                && not (Set.contains name env.Registry.GeneratedCopies)
                && not (Set.contains name env.Registry.InferredCopies)
                && args |> List.forall (function Ast.MandatoryArg _ -> true | _ -> false)
                && exprSize body <= maxInlineBodySize
                ->
                let parameters = Ast.mandatoryNames args

                let qualification =
                    AlphaRename.freeNames (Set.ofList parameters) body
                    |> Seq.choose (fun n ->
                        match Map.tryFind n moduleOf with
                        | Some(m, original) -> Some(n, Naming.qualifiedBinding m original)
                        | None -> None)
                    |> Map.ofSeq

                Some(
                    name,
                    ({ Params = parameters
                       Body = body
                       Qualification = qualification
                       OriginModule = ownModuleName }
                    : InlineTemplate)
                )
            | _ -> None)

    { env with
        Registry =
            { env.Registry with
                ConstrainedBodies =
                    bodies |> List.fold (fun acc (k, v) -> Map.add k v acc) env.Registry.ConstrainedBodies } }

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

/// The key of a function's body in `Active`, in the namespace of the trait keys
/// but never equal to one: those hold two `::`, this one starts with `fn`.
let private functionKey (originModule: string) (originalName: string) =
    $"fn::%s{originModule}::%s{originalName}"

type private Ctx =
    { Env: Env
      /// Threaded functionally, so it pops on backtracking: a sibling call to
      /// the same method elsewhere in the tree still inlines, and only cycles on
      /// the *current path* fall back to a call.
      Active: Set<string>
      /// Top-level name -> the module and name it was written as. A call names a
      /// function by what this module calls it, and a published body is found
      /// by what its own module called it.
      ModuleOf: Map<string, string * string>
      /// The same, by the qualified name a spliced body calls it by.
      Qualified: Map<string, string * string>
      /// Every name the declaration being inlined binds anywhere. A name in it
      /// may mean the local and not the top-level function of that name, so
      /// it is not taken for the function.
      Shadowed: Set<string>
      /// In a `match` guard. A guard is emitted as `case ... when`, which has
      /// no place for the statements a spliced loop needs, so a function call
      /// there is not inlined.
      InGuard: bool
      /// In the body of a loop: a member of a `TLetRec`, which is what a named
      /// `let` and a `(loop ...)` are until `LoopLowering`.
      InLoop: bool
      /// In a body spliced by this traversal.
      InSplice: bool
      /// The second of the two passes over a declaration. See `inlineDecl`.
      SecondPass: bool
      /// The typed nodes function splices may still add to the declaration
      /// being inlined. Shared by the two passes and by nested splices.
      Budget: int ref }

/// How much a declaration may grow by function splices: `GrowthFactor` times
/// its own size, and at least `GrowthFloor` nodes, so that a small function
/// can take a few calls. One node is about 1.5 to 2 bytes of IL.
[<Literal>]
let private GrowthFactor = 2

[<Literal>]
let private GrowthFloor = 300

let rec private typedSize (expr: TypedExpr) : int =
    1 + (TypeVisitor.children expr |> List.sumBy typedSize)

/// The module and name of the top-level function `name` means here, if it
/// means one.
let private topLevelFunction (ctx: Ctx) (name: string) : (string * string) option =
    match Map.tryFind name ctx.Qualified with
    | Some origin -> Some origin
    | None when Set.contains name ctx.Shadowed -> None
    | None -> Map.tryFind name ctx.ModuleOf

/// The names `expr` binds anywhere in it: the binders `AlphaRename` renames.
let private localBinders (expr: TypedExpr) : Set<string> =
    let here (e: TypedExpr) : string list =
        match e.Node with
        | TLet(n, _, fn, _, _) -> n :: fn.Params
        | TLetRec(bindings, _) -> bindings |> List.collect (fun (n, _, fn, _) -> n :: fn.Params)
        | TLetTuple(names, _, _) -> names
        | TLetMutable(n, _, _) -> [ n ]
        | TLambda(ps, _) -> ps
        | TMatch(_, clauses) -> clauses |> List.collect (fun c -> AlphaRename.patternNames c.Pattern)
        | TDefMatch(binder, _, _, arms) ->
            AlphaRename.patternNames binder
            @ (arms |> List.collect (fun a -> AlphaRename.patternNames a.Pattern))
        | TWithReturn(n, _) -> [ n ]
        | _ -> []

    let rec go (acc: Set<string>) (e: TypedExpr) =
        let acc = here e |> List.fold (fun s n -> Set.add n s) acc
        TypeVisitor.children e |> List.fold go acc

    go Set.empty expr

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
    // the node still says which implementation was chosen. An impl with only a
    // `(where (Num %a))` is handed back too, because `Lowering` checks that
    // constraint at the type the call is made at.
    let conditional =
        match Map.tryFind (tref.Trait, ctor) env.Registry.ImplTargets with
        | Some target -> not target.Constraints.IsEmpty
        | None -> false

    // A member-level `(where ...)` is the same story as a conditional impl:
    // the call takes a dictionary argument built out of evidence, and that is
    // `Lowering`'s job. (A *splice* of such a member never needs one — the
    // body is re-checked source, and its trait calls resolve at the concrete
    // types — but a landing pad is a call to the compiled method, which
    // declares the parameter.)
    if conditional || not tref.MemberConstraints.IsEmpty then
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

        // The first pass decided this call already. Only a body the second
        // pass spliced has trait calls it has not seen.
        if ctx.SecondPass && not ctx.InSplice then
            { expr with Node = TTraitCall(tref, args, kwArgs) }
        else

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
                spliceTemplate
                    ctx
                    key
                    $"the '%s{tref.Method}' implementation of '%s{tref.Trait}' for '%s{ctor}'"
                    tpl
                    args
                    expr
                    false
                    (fun () -> landingPad ctx.Env tref ctor tyArgs args [] expr)
            | _ -> landingPad ctx.Env tref ctor tyArgs args kwArgs expr

    | TApply({ Node = TIdent(name, _) } as callee, args, []) ->
        let args = args |> List.map (inlineExpr ctx)
        let call = { expr with Node = TApply(callee, args, []) }
        // Calls in loops in the first pass, the others in the second: what the
        // budget allows goes to the calls that run most. A body the second
        // pass splices is new, so all its calls are its own to decide.
        let thisPass =
            if ctx.SecondPass then not ctx.InLoop || ctx.InSplice else ctx.InLoop

        if ctx.InGuard || not thisPass then call else inlineFunctionCall ctx name callee args call

    // The members of a group are loops: a named `let` or a `(loop ...)`.
    | TLetRec(bindings, body) ->
        let loopCtx = { ctx with InLoop = true }

        let bindings =
            bindings
            |> List.map (fun (n, isFun, fn, value) ->
                let fn =
                    { fn with KeywordArgs = fn.KeywordArgs |> List.map (fun (k, t, d) -> k, t, inlineExpr ctx d) }

                n, isFun, fn, inlineExpr loopCtx value)

        { expr with Node = TLetRec(bindings, inlineExpr ctx body) }

    // A pattern's view step is emitted in the same place as a guard.
    | TMatch(target, clauses) ->
        let guardCtx = { ctx with InGuard = true }

        let rec mapPattern (p: TypedPattern) =
            TypeVisitor.mapPatternChildrenWith (inlineExpr guardCtx) mapPattern p

        let clauses =
            clauses
            |> List.map (fun c ->
                { Pattern = mapPattern c.Pattern
                  Guard = c.Guard |> Option.map (inlineExpr guardCtx)
                  Body = inlineExpr ctx c.Body })

        { expr with Node = TMatch(inlineExpr ctx target, clauses) }

    | _ -> TypeVisitor.mapChildren (inlineExpr ctx) expr

/// Is `arg` a function whose body is known here: a lambda, or a top-level
/// function? Only such an argument makes inlining the call worth it, because
/// only then does the call through it become a direct call or the lambda's
/// body.
///
/// A lambda with a yield point in its body is not: it is an error that
/// `ColourCheck` reports at the lambda, and the lambda's body put into a
/// bjoroutine would make it no error.
and private isKnownFunction (ctx: Ctx) (arg: TypedExpr) : bool =
    match arg.Node with
    | TLambda(_, body) ->
        not (callSuspends arg.Type)
        && not (hasYieldPoint body)
        && not (callsColourChosenDouble ctx.Env body)
    // Not a `defbjouble`: the colour around a call chooses its copy, so the
    // call the substitution makes would choose by a colour the reference did
    // not have.
    | TIdent(name, _) ->
        match topLevelFunction ctx name with
        | Some(_, original) ->
            isFunctionType arg.Type
            && not (Map.containsKey name ctx.Env.Registry.DoubleDefs)
            && not (Map.containsKey original ctx.Env.Registry.DoubleDefs)
        | None -> false
    | _ -> false

/// Inlines a call to a top-level function that is given a known function, if
/// the function's body is published (see `publishBodies`).
///
/// The splice is the one a trait method gets. A lambda argument is then
/// substituted where the body calls its parameter, and `betaReduce` makes the
/// call the lambda's body. So `(list-map (fun (x) (* x 2)) xs)` becomes the loop
/// of `list-map` with `(* elem 2)` in it, and no delegate is made or called.
///
/// Conservative, unlike a trait method's splice: the argument has to be a
/// lambda or a top-level function written at the call, the body has to call a
/// lambda parameter at most once, and the body has to be small. Otherwise the
/// call stays a call. A lambda called twice would only be bound to a local,
/// and the call through it would remain.
and private inlineFunctionCall
    (ctx: Ctx)
    (name: string)
    (callee: TypedExpr)
    (args: TypedExpr list)
    (call: TypedExpr)
    : TypedExpr =
    let origin =
        if args |> List.exists (isKnownFunction ctx) then
            topLevelFunction ctx name
        else
            None

    let template =
        match origin with
        | Some(originModule, original) ->
            match Map.tryFind original ctx.Env.Registry.ConstrainedBodies with
            | Some tpl when tpl.OriginModule = originModule -> Some(functionKey originModule original, tpl)
            | _ -> None
        | None -> None

    // A spliced body calls a function by its qualified name, which is not a
    // key in `Bindings`; the name the function was written as is.
    let unconstrained =
        let binding =
            match Map.tryFind name ctx.Env.Bindings, origin with
            | Some b, _ -> Some b
            | None, Some(_, original) -> Map.tryFind original ctx.Env.Bindings
            | None, None -> None

        match binding with
        | Some { Scheme = Scheme(_, [], _) } -> true
        | _ -> false

    match template with
    | Some(key, tpl) when
        not (Set.contains key ctx.Active)
        && tpl.Params.Length = args.Length
        && unconstrained
        // The same rules as for a trait method: a suspending function, or one
        // given a suspending callback, keeps its call, which is what
        // `ColourCheck` and `EffectGraph` read.
        && not (callSuspends callee.Type)
        && not (wantsSuspendingCopy callee.Type)
        ->
        spliceTemplate ctx key $"'%s{name}'" tpl args call true (fun () -> call)
    | _ -> call

and private spliceTemplate
    (ctx: Ctx)
    (key: string)
    (describe: string)
    (tpl: InlineTemplate)
    (args: TypedExpr list)
    (expr: TypedExpr)
    (onlyIfSubstituted: bool)
    (fallback: unit -> TypedExpr)
    : TypedExpr =

    let budgetAtStart = ctx.Budget.Value

    try
        // 1. Freshen at the splice. Mandatory, and *not* something the global
        //    uniquifying pass can do afterwards: renaming preserves meaning, it
        //    cannot recover a meaning the splice already destroyed.
        //    A `(by-colour ...)` takes its `#:sync` body, because only a call
        //    that does not suspend is spliced.
        let body =
            AlphaRename.reachHidden
                (fun n -> Map.containsKey n ctx.Env.Bindings)
                tpl.Qualification
                (ColourTwins.resolveIn false tpl.Body)

        let freshBody, subst = AlphaRename.freshen tpl.Params body
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
        let bodyType, typedBody = InferExpr.infer spliceEnv freshBody
        unify ctx.Env.Registry bodyType expr.Type

        // Obligations raised by the body — a `bind` calling `bind` — are
        // discharged here, before anything looks at the result.
        Traits.solvePending spliceEnv

        // 4. Free names now say which module they came from.
        let qualified = AlphaRename.applyQualification tpl.Qualification typedBody

        let innerCtx =
            { ctx with
                Active = Set.add key ctx.Active
                InSplice = true }

        let bindings = List.zip freshParams args

        // A function splice costs the declaration's budget its body's size,
        // and its own nested splices take theirs from the same budget. If the
        // splice is given up, what it took is given back.
        let budgetBefore = ctx.Budget.Value
        let cost = if onlyIfSubstituted then typedSize typedBody else 0

        // Read before step 4: `DoubleDefs` knows a name as written, and the
        // qualification rewrites it. A yield point left in the body would be
        // moved into the caller, where `ColourCheck` would judge it at a
        // place it was not written.
        if
            onlyIfSubstituted
            && (callsColourChosenDouble ctx.Env typedBody
                || hasYieldPoint typedBody
                || cost > budgetBefore)
        then
            fallback ()
        else
            ctx.Budget.Value <- budgetBefore - cost

            // 5. Recurse, with this key held down for the current path only.
            let inlined = inlineExpr innerCtx qualified

            // A function call is inlined only for the sake of its lambdas, so
            // if one of them would only be bound to a local, the call is kept.
            let lambdaKept =
                bindings
                |> List.exists (fun (name, arg) ->
                    match arg.Node with
                    | TLambda _ -> occurrences name inlined > 1
                    | _ -> false)

            if onlyIfSubstituted && lambdaKept then
                ctx.Budget.Value <- budgetBefore
                fallback ()
            else
                // 6. Bind the arguments, then reduce whatever the substitution
                //    exposed.
                betaReduce (bindArguments describe bindings inlined)
    with ex ->
        // A template that will not re-infer here is a compile error waiting to
        // happen in generated C#, and the fallback is always a correct answer.
        // Say so rather than failing the build over an optimization.
        warn
            $"could not inline %s{describe} at %s{Lexer.formatPos expr.Range}: %s{ex.Message}. Falling back to a call."

        ctx.Budget.Value <- budgetAtStart
        fallback ()

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

    // A function holds its own key over its body, for the reason a method
    // does: a call to itself stays a call.
    | TDefun(name, _, args, kwArgs, rest, _, _, body, _) ->
        let active =
            match Map.tryFind name ctx.ModuleOf with
            | Some(m, original) -> Set.add (functionKey m original) ctx.Active
            | None -> ctx.Active

        let shadowed =
            (args |> List.map fst)
            @ (kwArgs |> List.map (fun (n, _, _) -> n))
            @ (rest |> Option.map fst |> Option.toList)
            |> List.fold (fun acc n -> Set.add n acc) (localBinders body)

        inlineInTwoPasses { ctx with Active = active; Shadowed = shadowed } decl

    | _ ->
        let shadowed = TypeVisitor.foldDecl (fun acc e -> Set.union acc (localBinders e)) Set.empty decl
        inlineInTwoPasses { ctx with Shadowed = shadowed } decl

/// Inlines in one declaration, in two passes over it with one budget.
///
/// Function splices grow the declaration, and there is a limit to how much a
/// method should grow: past some size the JIT optimizes it less, and a copy at
/// a call that runs once gains nothing. So the declaration may grow by
/// `GrowthFactor` times its size, at least `GrowthFloor` nodes. The calls in
/// loops are inlined first, as they run the most, and the other calls take
/// what is left in the second pass. Trait splices are made in the first pass
/// and do not count.
and private inlineInTwoPasses (ctx: Ctx) (decl: TDecl) : TDecl =
    let size = TypeVisitor.foldDecl (fun n _ -> n + 1) 0 decl
    let budget = ref (max GrowthFloor (GrowthFactor * size))
    let first = TypeVisitor.mapDecl (inlineExpr { ctx with Budget = budget; SecondPass = false }) decl
    TypeVisitor.mapDecl (inlineExpr { ctx with Budget = budget; SecondPass = true }) first

/// Inlines every statically resolvable trait call in the program, and every
/// call of a published function that is given a known function.
let run (env: Env) (moduleOf: Map<string, string * string>) (decls: TDecl list) : TDecl list =
    let qualified =
        moduleOf
        |> Map.toSeq
        |> Seq.map (fun (_, (m, original)) -> Naming.qualifiedBinding m original, (m, original))
        |> Map.ofSeq

    let ctx =
        { Env = env
          Active = Set.empty
          ModuleOf = moduleOf
          Qualified = qualified
          Shadowed = Set.empty
          InGuard = false
          InLoop = false
          InSplice = false
          SecondPass = false
          Budget = ref 0 }
    decls |> List.map (inlineDecl ctx)
