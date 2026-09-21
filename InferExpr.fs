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

module Bjolang.InferExpr

open Bjolang.Lexer
open Bjolang.Ast
open Bjolang.TypedAST
open Bjolang.Unification
open Bjolang.TypeEnv
open Bjolang.Annotations
open Bjolang.Traits
open Bjolang.ForeignTyping

/// The payload head constructors a literal of this shape could be injected
/// into, or `None` for an expression that has no literal shape.
///
/// `None` is the ordinary case and keeps the type-directed path: an unquoted
/// `,my-rot-13` is a `(-> string string)` and is selected by that type, having
/// no shape a reader could select it by.
let private literalPayloadHeads (expr: Expr) : string list option =
    match expr with
    | EList _ -> Some [ "List" ]
    | EVec _ -> Some [ "Vec" ]
    | EArray _ -> Some [ "Array" ]
    | EString _ -> Some [ TypeConstants.StringName ]
    | EQuotedSymbol _ -> Some [ TypeConstants.SymbolName ]
    // A numeric literal's own type rather than "int or double": `1` and `1.0`
    // are different shapes to a reader, and a union carrying both would
    // otherwise be ambiguous for every number written in it.
    | EInt(value, _) ->
        match inferNumericType value with
        | TCon(name, _) -> Some [ name ]
        | _ -> None
    | _ -> None

/// How a literal is named in a diagnostic about it.
let private literalShapeName (expr: Expr) : string =
    match expr with
    | EList _ -> "list"
    | EVec _ -> "vec"
    | EArray _ -> "array"
    | EString _ -> "string"
    | EQuotedSymbol _ -> "symbol"
    | EInt _ -> "number"
    | _ -> "value"

/// `a`, `a or b`, `a, b or c`.
let private orList (names: string list) : string =
    match List.rev names with
    | [] -> ""
    | [ one ] -> one
    | last :: earlier -> String.concat ", " (List.rev earlier) + " or " + last

/// A union's cases as they were declared, for a diagnostic that has to say what
/// was on offer.
let private describeUnionCases (registry: TraitRegistry) (unionName: string) : string =
    match Map.tryFind unionName registry.Unions with
    | None -> ""
    | Some(_, cases) ->
        cases
        |> List.map (fun (caseName, payloads, _) ->
            let caseName = Naming.showTypeName caseName

            if payloads.IsEmpty then
                caseName
            else
                let types = payloads |> List.map DotNetInterop.showType |> String.concat " "
                $"(%s{caseName} %s{types})")
        |> String.concat ", "

/// Whether any element of a literal is itself a list or vec literal.
///
/// The question a failed element join has to answer: is this a list of things
/// that happen not to agree, or a tree that was never meant to have one element
/// type at all? Only the second has an answer beyond "these two types differ",
/// and a nested literal is what tells them apart.
let private hasNestedSequenceLiteral (elements: Expr list) : bool =
    elements
    |> List.exists (function
        | EList _
        | EVec _
        | EArray _ -> true
        | _ -> false)

/// Join one element of a literal to the element type its siblings share.
///
/// Plain unification, except for the report when it fails. A literal reaches
/// here only when nothing pushed an expected type into it, and for a tree —
/// `'(pipe (ls "-l"))` — that is itself the error: its elements are a symbol
/// and a list because it describes a union, and no union was named. Saying that
/// `Symbol` does not unify with `(List Symbol)` names two types the program
/// never wrote, and no way out of it.
let private joinLiteralElement
    (env: Env)
    (r: Range)
    (kind: string)
    (elements: Expr list)
    (elementType: HMType)
    (elemTy: HMType)
    : unit =
    try
        unify env.Registry elemTy elementType
    with ex when Diagnostics.isDiagnostic ex && hasNestedSequenceLiteral elements ->
        let shown =
            DotNetInterop.showTypesTogether [ prune env.Registry elemTy; prune env.Registry elementType ]

        failwithf
            $"Type Error at %s{Lexer.formatPos r}: nothing here says what this %s{kind} literal holds, and its elements do not agree on a type by themselves:\n  %s{shown[0]}\n  %s{shown[1]}\nA literal with a nested list among its elements stands for a union, and which union it stands for comes from the type expected where it is written. A generic parameter expects nothing in particular, so there is none: annotate the value and pass that, as in (def (: procs (List ProcList)) (list '(...)))."

// ---------------------------------------------------------------------------
// `def` propagation
// ---------------------------------------------------------------------------

/// The cases of the union `t` is, as name and payload count, or `None` when `t`
/// is not a union at all.
///
/// A declared union wins over a builtin of the same name, as it does in
/// `Exhaustiveness`: a module that defines its own `Result` propagates its own
/// cases.
let private unionCasesOf (registry: TraitRegistry) (t: HMType) : (string * int) list option =
    match t with
    | TCon(name, args) ->
        match Map.tryFind name registry.Unions with
        | Some(_, cases) -> Some(cases |> List.map (fun (caseName, payloads, _) -> caseName, payloads.Length))
        | None ->
            match name, args with
            | "Option", [ _ ] -> Some [ "None", 0; "Some", 1 ]
            | "Result", [ _; _ ] -> Some [ "Err", 1; "Ok", 1 ]
            | "List", [ _ ] -> Some [ "Nil", 0; "Cons", 2 ]
            | _ -> None
    | _ -> None

/// Whether a pattern matches every value of its type.
let rec private alwaysMatches (pat: TypedPattern) : bool =
    match pat.Node with
    | TPWildcard
    | TPIdent _ -> true
    | TPAs(inner, _) -> alwaysMatches inner
    | TPTuple items -> List.forall alwaysMatches items
    | TPAnd alts -> List.forall alwaysMatches alts
    | _ -> false

/// The constructors of `t` that `binder` leaves out, as name and payload count.
///
/// `None` where the leftovers are not a set of constructors: a literal, a
/// sequence pattern, a view, or a constructor whose fields are themselves
/// refutable. `:propagate` rebuilds one constructor per leftover case, so it
/// has nothing to build from a `None` and refuses it.
let private uncoveredCases
    (registry: TraitRegistry)
    (t: HMType)
    (binder: TypedPattern)
    : (string * int) list option =
    let rec covered (pat: TypedPattern) =
        match pat.Node with
        | TPAs(inner, _) -> covered inner
        | TPConstruct(name, args) when List.forall alwaysMatches args -> Some name
        | _ -> None

    if alwaysMatches binder then
        Some []
    else
        match covered binder, unionCasesOf registry t with
        | Some name, Some cases -> Some(cases |> List.filter (fun (c, _) -> c <> name))
        // A constructor pattern on a type with no cases to list is a record or
        // a CLR class: there is one way to build one, so the pattern matches
        // every value of it.
        | Some _, None -> Some []
        | None, _ -> None

/// What a function-shaped local binding declares, before its body is looked at.
///
/// Built first so that a recursive call *inside* the body — one that passes a
/// keyword argument, or leaves an optional one out — has metadata to resolve
/// against. A top-level `defun` establishes its `FunMeta` before its body for
/// exactly the same reason.
type private LocalFunShape =
    { Mandatory: (string * HMType) list
      Keywords: (string * HMType * Expr) list
      /// Name and *element* type; the parameter itself is an array of it.
      Rest: (string * HMType) option
      RetType: HMType
      /// The flat arrow a call unifies against: mandatory, then keyword, then
      /// the rest array. The layout a top-level `defun` builds, because the
      /// same application rule reads it.
      FunType: HMType
      Meta: FunMeta }

/// Reads a local `defun`'s argument list as types.
///
/// A local function has no declared signature to take them from, so a
/// parameter's type is a metavariable unless `(: x type)` said otherwise, and
/// the return type is whatever the body turns out to have unless `: type` did.
let private localFunShape (env: Env) (args: DefunArg list) (retAnn: FType option) : LocalFunShape =
    let annotated (ann: FType option) =
        match ann with
        | Some t -> resolveTypeAnnotation env.Registry t
        | None -> freshMeta ()

    let mandatory =
        args |> List.choose (function MandatoryArg(n, t) -> Some(n, annotated t) | _ -> None)

    let keywords =
        args |> List.choose (function KeywordArg(n, d) -> Some(n, freshMeta (), d) | _ -> None)

    let rest = args |> List.tryPick (function RestArg n -> Some(n, freshMeta ()) | _ -> None)
    let retType = annotated retAnn

    let argTypes =
        (mandatory |> List.map snd)
        @ (keywords |> List.map (fun (_, t, _) -> t))
        @ (match rest with
           | Some(_, t) -> [ TCon("Array", [ t ]) ]
           | None -> [])

    { Mandatory = mandatory
      Keywords = keywords
      Rest = rest
      RetType = retType
      // A *cell*, not `ESync`, because a body-local function has no signature
      // and so nothing to declare a colour in — which by this language's own
      // rule means the colour is inferred. `EffectGraph` binds it once it has
      // seen the body: a local function that reaches a yield point becomes an
      // `async` C# local function, and one that does not stays ordinary.
      //
      // The cell is shared by every reference, which is what makes this work
      // with no repainting anywhere. `generalizeLocal` quantifies `MetaVar`s
      // and an `EffectCell` is not one, and `instantiate` freshens `EPoly` and
      // passes every other effect through — so each use of the name, including
      // a recursive one, reads back the same cell the binding decided.
      FunType = TFun(argTypes, retType, freshEffect ())
      Meta =
        { MandatoryCount = mandatory.Length
          KeywordParams = keywords |> List.map (fun (n, t, _) -> n, t)
          RestParam = rest |> Option.map snd } }

/// Infers a type, attaching a source location to any diagnostic that lacks one.
///
/// `unify` is where most type errors are raised and it is given two types and
/// nothing else — no range reaches it, and threading one to every call site
/// would mean inventing a location at the several that have no natural one.
/// Catching here instead costs nothing on the path where nothing throws, and
/// names the innermost expression whose inference failed, which is the smallest
/// piece of source that can be blamed.
///
/// The `when` is doing real work: a filter runs before the stack unwinds, so an
/// exception that is not a diagnostic is never caught here and keeps its trace.
let rec infer (env: Env) (expr: Expr) : HMType * TypedExpr =
    try
        inferNode env (resolveAliasedHead env expr)
    with ex when Diagnostics.needsLocation ex ->
        raise (Diagnostics.withLocation (exprRange expr) ex)

/// Rewrites a head identifier that is a prefixed spelling of a constructor, a
/// record type or a trait method back to the name it stands for.
///
/// Once, here, rather than at each of the guards in `inferNode`: that function
/// dispatches on `EIdent` and on `EApp(EIdent ...)` in a dozen places, and a
/// spelling is not meant to be visible to any of them.
///
/// A *binding* of that name is not a spelling of anything, and wins. Without
/// this the rewrite ran before scope was consulted, so a lowercase union case
/// beat every binder: `(let ((counting 7)) (int->string counting))` reported
/// `int` against `(prelude/Counting ?a)` — the reference had already become the
/// constructor's key, and the local was never asked about. That made `mapping`,
/// `counting`, `folding` and their four siblings unusable as variable names
/// anywhere in a program that imports the prelude, which is every program.
///
/// The test is `Bindings` rather than a kind, because that is what a spelling
/// resolves *past*. A constructor's key is what holds the binding, so a bare
/// name is in `Bindings` only when something else put it there.
and private resolveAliasedHead (env: Env) (expr: Expr) : Expr =
    let resolve (name: string) =
        // A binder inside this declaration wins; a name that still means what it
        // meant at module level does not. The comparison is against `Resolved`
        // rather than a mere `containsKey` because a declaration's own name can
        // be bound under its bare spelling too, and that binding *is* what the
        // spelling stands for rather than something in its way.
        if Map.tryFind name env.Bindings <> Map.tryFind name env.Resolved then
            name
        else
            originalName env.Registry name

    match expr with
    | EIdent(name, r) ->
        let original = resolve name
        if original = name then expr else EIdent(original, r)
    | EApp(EIdent(name, ir), args, r) ->
        let original = resolve name
        if original = name then expr else EApp(EIdent(original, ir), args, r)
    | _ -> expr

and private inferNode (env: Env) (expr: Expr) : HMType * TypedExpr =
    match expr with
    | EInt(value, r) ->
        let inferredType = numericLiteralType value r

        inferredType,
        { Type = inferredType
          Range = r
          Node = TInt value }
    | EString(value, r) ->
        TypeConstants.stringType,
        { Type = TypeConstants.stringType
          Range = r
          Node = TString value }

    | EApp(EIdent(name, _), args, r) when Map.containsKey name env.Escapes ->
        inferEscapeCall env name args r


    | EIdent(name, r) when Map.containsKey name env.Escapes ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: `%s{name}` is an escape, not a value: it can only be applied inside its own `with-return`."

    | EWithReturn(name, body, r) ->
        // Every rule about where `name` may be applied is a question about
        // shape, so all of them are decided here, over the body as written,
        // before a single form in it is typed.
        Hygiene.checkEscapeUses name body

        let resultType = freshMeta ()
        let label = Gensym.fresh "__ret"

        let bodyEnv =
            { env with
                Escapes = Map.add name { Label = label; Result = resultType } env.Escapes }

        let bodyType, typedBody = infer bodyEnv body
        unify env.Registry bodyType resultType

        resultType,
        { Type = resultType
          Range = r
          Node = TWithReturn(label, typedBody) }

    | EDefMatch(binder, scrutinee, failure, sequel, r) ->
        inferDefMatch env binder scrutinee failure sequel r


    // `std/eq`'s own equality primitives, refused everywhere else. See
    // `Naming.eqPrivateBindings` for why they are shut away at all.
    //
    // This refusal is also the recursion diagnostic for materialization: an
    // `Eq` impl written as `(clr-equals a b)` would emit an `Equals` that
    // calls the impl that calls `Equals`, forever. Refusing the *name* is
    // deliberately blunter than scanning impl bodies — a shallow body check
    // misses the same call one helper function away, and there is no use of
    // these primitives outside std/eq that is not that loop waiting to happen.
    | EIdent(name, r) when
        Set.contains name Naming.eqPrivateBindings
        // Extract the simple module name from the fully-qualified `CurrentModule` key.
        && Naming.moduleNameOfPath env.CurrentModule <> Naming.eqModuleName
        ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{name}' is private to std/eq. It is .NET's equality, and a type's own `Eq` implementation is what .NET equality is made *of* — writing one in terms of the other is a loop. Use structural-equals and structural-hash for the field-by-field comparison, compare the fields yourself, or derive."

    // A trait method written where a value is wanted, rewritten into the lambda
    // it stands for: `even?` becomes `(fun (x) (even? x))`.
    //
    // C# has no method group to hand over — an interface trait's method is a
    // slot on an interface and an inline trait's is not emitted at all — so a
    // bare reference used to reach `Codegen` as a name with nothing behind it.
    // The call inside the lambda is in call position like any other, so it
    // dispatches normally and the obligation it raises is discharged at
    // whatever type the lambda is used at.
    //
    // This is the rewrite the parser makes for operators, made here because
    // only the registry knows which names are methods. `TraitMethodNames`
    // leaves out a name that something has bound over, so a local of the same
    // name still wins; and the head of an application never arrives here,
    // because `EApp` matches its own callee first.
    | EIdent(name, r) when
        Set.contains name env.TraitMethodNames
        && (match traitMethodArity env name with
            | Some n -> n > 0
            | None -> false)
        ->
        let arity = Option.defaultValue 0 (traitMethodArity env name)
        let ps = List.init arity (fun _ -> Gensym.fresh "eta")

        infer env (EFun(ps, EApp(EIdent(name, r), ps |> List.map (fun p -> EIdent(p, r)), r), Ordinary, r))

    // An inline trait's methods are never bound as values: there is no single
    // scheme they could be bound under, which is the whole reason the trait is
    // inline-only.
    | EIdent(name, r) when
        Map.containsKey name env.Registry.TraitMethods
        && not (Map.containsKey name env.Bindings)
        ->
        let traitName = env.Registry.TraitMethods[name]

        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{name}' is a method of the inline-only trait '%s{traitName}' and has no value form. Apply it directly, or wrap it in a lambda at a known type."

    | EIdent(name, r) when
        not (Map.containsKey name env.Bindings)
        && not (name.EndsWith ".")
        && name.Contains "."
        && Map.containsKey (name.Substring(0, name.LastIndexOf ".")) env.Registry.ClrClasses
        ->
        inferStaticMember env name r


    | EIdent(name, r) when Map.containsKey name env.Registry.ClrExterns && not (Map.containsKey name env.Bindings) ->
        inferExternValue env name r


    // `apply` is a form, not a function, so it has no value form either.
    //
    // There is no `HMType` to bind it to: the arity it checks, the parameters
    // it fills and the effect the call takes on are all read off whichever `f`
    // it is applied to, and a bare `apply` has no `f` to read them from. Said
    // here rather than left to `lookup`, which would report it as unbound and
    // send the reader looking for a missing import.
    | EIdent("apply", r) when not (Map.containsKey "apply" env.Bindings) ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: 'apply' is a form, not a value, so it has no value form. Write the call out — (apply f xs) — or wrap it in a lambda over a function you name there."

    | EIdent(name, r) ->
        inferIdent env name r


    // A name the compiler wrote, in value position.
    //
    // Handled by putting module level's binding for it back and then taking the
    // ordinary path, rather than by resolving it here. A name is not always a
    // binding: `folding` is a union case and reaches its meaning through the
    // registry, as record constructors and trait methods do, and those paths
    // are not worth reproducing.
    //
    // Nothing to put back means nothing at module level had that name, so there
    // is nothing a local could be shadowing and the registries decide.
    | EResolved(name, r) ->
        let t, te = infer (unshadow name env) (EIdent(name, r))
        t, requalifyResolved name env te

    | EFun(args, body, colour, r) -> inferLambda None env args body colour r

    // A trait method in application position.
    //
    // The call is typed immediately — every position in the template gets a
    // fresh meta and the arguments and result are unified against them — while
    // *which* implementation runs is left blank for the solver. That is what
    // lets `pure`, whose constructor appears only in its result, be resolved at
    // all: the metas are shared with the surrounding expression, so an enclosing
    // `bind` or a declared return type pins them.
    // Ahead of every specialised application below, so that a call the compiler
    // wrote reaches whichever of them it should — trait method, record
    // constructor, union case or ordinary function — with its head meaning what
    // it meant at module level.
    | EApp(EResolved(name, mr), args, r) ->
        let t, te = infer (unshadow name env) (EApp(EIdent(name, mr), args, r))
        t, requalifyResolved name env te

    | EApp(EIdent(methodName, _), args, r) when Set.contains methodName env.TraitMethodNames ->
        inferTraitMethodCall env methodName args r


    | EApp(EIdent(recordTypeName, _), args, r) when Map.containsKey recordTypeName env.Registry.Records ->
        inferRecordConstruct env recordTypeName args r


    // --- Foreign .NET interop ---
    //
    // All four forms resolve the member they name against real .NET metadata
    // right here, from the types of the arguments as inference has them. The
    // selected overload's parameter types are then unified *back into* the
    // arguments, so reflection does not merely check the call — it informs it.

    // `(.-Property target)` — an instance property or field read.
    | EApp(EIdent(name, _), args, r) when name.StartsWith ".-" && name.Length > 2 ->
        let propName = name.Substring 2
        let where = Lexer.formatPos r

        match args with
        | [ target ] ->
            let targetType, typedTarget = infer env target
            let clrTarget = receiverClrType where name targetType
            let propType = DotNetInterop.resolveMemberRead where clrTarget propName false

            propType,
            { Type = propType
              Range = r
              Node = TDotPropertyGet(typedTarget, propName, propType) }
        | _ ->
            failwithf
                $"Type Error at %s{where}: '%s{name}' reads a property, so it takes exactly one argument — the object to read it from — but was given %d{args.Length}."

    | EApp(EIdent(name, _), args, r) when name.StartsWith "." && name.Length > 1 ->
        inferDotMethod env name args r


    | EApp(EIdent(name, _), args, r) when
        name.EndsWith "."
        && name.Length > 1
        && Map.containsKey (name.Substring(0, name.Length - 1)) env.Registry.ClrClasses
        ->
        inferClassConstruct env name args r


    | EApp(EIdent(name, _), args, r) when
        Map.containsKey name env.Registry.ClrExterns && not (Map.containsKey name env.Bindings)
        ->
        inferExternCall env name args r


    | EApp(EIdent("apply", _), args, r) when not (Map.containsKey "apply" env.Bindings) ->
        inferApply env args r


    | EApp(target, args, r) ->
        inferGeneralApp env target args r


    // Deliberately not generalized — see `ELetMono`. The value is inferred
    // first, exactly as `let` does, so the binding keeps its concrete head; only
    // the quantification is dropped.
    | ELetMono(name, value, body, r) ->
        let valType, typedVal = infer env value

        let localEnv =
            addBinding
                name
                { Scheme = Scheme([], [], valType)
                  IsMutable = false }
                env

        let bodyType, typedBody = infer localEnv body

        bodyType,
        { Type = bodyType
          Range = r
          Node = TLet(name, false, noParams, typedVal, typedBody) }

    | ELet(name, isFun, args, typeAnn, value, body, r) ->
        inferLet env name isFun args typeAnn value body r


    | ELetRec(bindings, body, r) ->
        inferLetRec env bindings body r


    | ELetMutable(name, typeAnn, value, body, r) ->
        inferLetMutable env name typeAnn value body r


    | ESet(name, value, r) ->
        let valType, typedVal = infer env value
        let binding = lookup env name

        if not binding.IsMutable then
            failwithf $"Type Error: Cannot mutate immutable variable '%s{name}' at %s{Lexer.formatPos r}"

        let targetType, _, _ = instantiate env.Registry binding.Scheme
        unify env.Registry valType targetType

        TypeConstants.unitType,
        { Type = TypeConstants.unitType
          Range = r
          Node = TSet(name, typedVal) }

    | EIf(cond, trueBranch, falseBranch, r) ->
        let condType, tCond = infer env cond
        unify env.Registry condType TypeConstants.boolType
        let trueType, tTrue = infer env trueBranch
        let falseType, tFalse = infer env falseBranch
        unify env.Registry trueType falseType

        trueType,
        { Type = trueType
          Range = r
          Node = TIf(tCond, tTrue, tFalse) }

    | EWhen(cond, body, negated, r) ->
        let condType, tCond = infer env cond
        unify env.Registry condType TypeConstants.boolType

        // The body is evaluated for its effect and its value thrown away, so it
        // constrains nothing: there is no other arm for it to agree with, and
        // the form itself yields nothing.
        let _, tBody = infer env body

        TypeConstants.unitType,
        { Type = TypeConstants.unitType
          Range = r
          Node = TWhen(tCond, tBody, negated) }

    | EQuotedSymbol(sym, r) ->
        let t = TypeConstants.symbolType

        t,
        { Type = t
          Range = r
          Node = TSymbol sym }

    | EChar(c, r) ->
        let t = TypeConstants.charType

        t,
        { Type = t
          Range = r
          Node = TChar c }

    | EBool(b, r) ->
        let t = TypeConstants.boolType

        t,
        { Type = t
          Range = r
          Node = TBool b }

    | EKeyword(kw, r) ->
        let t = TypeConstants.keywordType

        t,
        { Type = t
          Range = r
          Node = TKeyword kw }

    | ETuple(exprs, r) ->
        let typedExprs = exprs |> List.map (infer env)
        let tupleType = TTuple(typedExprs |> List.map fst)

        tupleType,
        { Type = tupleType
          Range = r
          Node = TTupleMake(typedExprs |> List.map snd) }

    | ELetTuple(names, value, body, r) ->
        let valType, typedVal = infer env value
        let elementMetas = names |> List.map (fun _ -> freshMeta ())
        unify env.Registry valType (TTuple elementMetas)

        let localEnv =
            List.zip names elementMetas
            |> List.fold
                (fun acc (n, t) ->
                    addBinding
                        n
                        { Scheme = Scheme([], [], t)
                          IsMutable = false }
                        acc)
                env

        let bodyType, typedBody = infer localEnv body

        bodyType,
        { Type = bodyType
          Range = r
          Node = TLetTuple(names, typedVal, typedBody) }

    | EList(exprs, r) -> inferCollection env "List" "list" TListMake exprs r

    | EVec(exprs, r) -> inferCollection env "Vec" "vec" TVecMake exprs r

    | EArray(exprs, r) -> inferCollection env "Array" "array" TArrayMake exprs r

    | ETryFinally(body, cleanup, r) ->
        let bodyType, tBody = infer env body
        let _, tCleanup = infer env cleanup

        bodyType,
        { Type = bodyType
          Range = r
          Node = TTryFinally(tBody, tCleanup) }

    // `(try body #:catch (E1 E2 ...))`. The listed failures become values; the
    // rest keep propagating, exactly as `#:exceptions` does at an import — this
    // is the same facility with the list written at the use rather than at the
    // declaration.
    | ETryCatch(body, exceptions, r) ->
        let where = Lexer.formatPos r
        checkExceptionTypes where exceptions

        let bodyType, tBody = infer env body
        let resultType = wrapForeignExceptions exceptions bodyType

        resultType,
        { Type = resultType
          Range = r
          Node = TTryCatch(tBody, exceptions) }

    | ESeq(body, r) ->
        inferSeq env body r


    | EBjo(call, kind, r) ->
        inferBjo env call kind r


    | ETaskEvent(call, r) ->
        inferTaskEvent env call r


    | EYield(value, r) ->
        let elemType = currentSeqElement env "yield" r
        let valueType, tValue = infer env value
        unify env.Registry valueType elemType

        TypeConstants.unitType,
        { Type = TypeConstants.unitType
          Range = r
          Node = TYield tValue }

    | EYieldFrom(source, r) ->
        let elemType = currentSeqElement env "yield-from" r
        let sourceType, tSource = infer env source
        unify env.Registry sourceType (TCon("Seq", [ elemType ]))

        TypeConstants.unitType,
        { Type = TypeConstants.unitType
          Range = r
          Node = TYieldFrom tSource }


    | EMatch(target, clauses, r) ->
        inferMatch env target clauses r


    | EGetField(targetExpr, field, r) ->
        let targetType, typedTarget = infer env targetExpr
        let recordTypeName = recordTypeOfField env.Registry targetType field r

        let instantiatedRecordType, _, expectedFieldsInstantiated =
            instantiateRecord env.Registry recordTypeName

        unify env.Registry targetType instantiatedRecordType

        let fieldType =
            match Map.tryFind field expectedFieldsInstantiated with
            | Some t -> t
            | None -> failwithf $"Type Error: Field '%s{field}' does not belong to record '%s{recordTypeName}' at %s{Lexer.formatPos r}"

        fieldType,
        { Type = fieldType
          Range = r
          Node = TGetField(typedTarget, field) }

    | ERecordUpdate(targetName, fields, r) ->
        inferRecordUpdate env targetName fields r


    | ERecordSet(targetName, fields, r) ->
        inferRecordSet env targetName fields r


    | ECast(targetTypeAnnotation, expr, r) ->
        let targetType = resolveTypeAnnotation env.Registry targetTypeAnnotation
        let exprType, typedExpr = infer env expr
        targetType,
        { Type = targetType
          Range = r
          Node = TCast(typedExpr, targetType) }

    | EDynPack(writtenTrait, valueExpr, r) ->
        inferDynPack env writtenTrait valueExpr r


// `(ret e)` — an application of an escape some enclosing `with-return` put
// in scope. Taken before the general application case, and before the
// general `EIdent` below it, because an escape is not a value and there is
// nothing in `Bindings` for either of them to find.
//
// Where it may *stand* was settled syntactically by `checkEscapeUses` when
// the block was entered, so nothing here has to ask.
/// A collection literal: `(list ...)`, `[...]` or `#[...]`.
///
/// One element type, joined across every element so that the diagnostic names
/// the literal rather than the pair of elements that disagreed. `ctor` names
/// the type, `literalName` is how that diagnostic spells the form, and
/// `mkNode` builds the node — which is all the three literals differ by.
and private inferCollection
    (env: Env)
    (ctor: string)
    (literalName: string)
    (mkNode: TypedExpr list -> TExprNode)
    (exprs: Expr list)
    (r: Range)
    : HMType * TypedExpr =
    let elementType = freshMeta ()

    let typedExprs =
        exprs
        |> List.map (fun e ->
            let t, te = infer env e
            joinLiteralElement env r literalName exprs elementType t
            te)

    let collectionType = TCon(ctor, [ elementType ])

    collectionType,
    { Type = collectionType
      Range = r
      Node = mkNode typedExprs }

and private inferEscapeCall (env: Env) (name: string) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let info = env.Escapes[name]

    let typedValue =
        match args with
        | [] ->
            // `(ret)` says the block produces nothing, which only a
            // void-typed block can agree with.
            unify env.Registry info.Result TypeConstants.unitType
            None
        | [ value ] ->
            let valueType, typedValue = infer env value
            unify env.Registry valueType info.Result
            Some typedValue
        | _ ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: `%s{name}` leaves its block with one value or with none, and here it was given %d{List.length args}."

    // Never yields a value, so it is given a fresh metavariable: it stands
    // wherever a value of any type was wanted and constrains nothing there.
    let resultType = freshMeta ()

    resultType,
    { Type = resultType
      Range = r
      Node = TReturn(info.Label, typedValue) }

and private inferDefMatch
    (env: Env)
    (binder: Pattern)
    (scrutinee: Expr)
    (failure: DefFailure)
    (sequel: Expr)
    (r: Range)
    : HMType * TypedExpr =
    // One scrutinee, matched by the binder and by every arm, so it is inferred
    // once and every pattern is checked against the type it has here.
    let scrutineeType, typedScrutinee = infer env scrutinee
    let typedBinder, boundVars = checkPattern inferChecked env scrutineeType binder

    let withVars vars inner =
        Map.fold
            (fun acc n t ->
                addBinding
                    n
                    { Scheme = Scheme([], [], t)
                      IsMutable = false }
                    acc)
            inner
            vars

    // The sequel is the rest of the body, so the form's own type is the
    // sequel's — and so is the failure part's. Whichever runs produces the
    // whole form's value, exactly as an `if`'s two branches do. A body that
    // means to leave an enclosing block says so with a `(ret ...)`, which is an
    // ordinary tail-position form here.
    let sequelType, typedSequel = infer (withVars boundVars env) sequel

    // `:propagate` rebuilds every case its pattern leaves out, each written as
    // the arm that rebuilds it and checked as any other arm is. The case name
    // rides along for the report: a propagation that does not fit the body is a
    // mistake about this form rather than about the arm's type.
    let failureArms =
        match failure with
        | FailNone -> []
        | FailArms arms -> arms |> List.map (fun (pat, body) -> None, pat, body)
        | FailValue value -> [ None, PWildcard(exprRange value), value ]
        | FailPropagate ->
            propagatedArms env scrutineeType typedBinder r
            |> List.map (fun (caseName, (pat, body)) -> Some caseName, pat, body)

    // The failure part is checked in `env` and not under what the binder binds:
    // it runs because the binder did not match, so nothing the binder would
    // have bound exists. What an arm's own pattern binds is in scope in that
    // arm and nowhere else.
    let typedArms =
        failureArms
        |> List.map (fun (propagated, armPattern, armBody) ->
            let typedPattern, armVars = checkPattern inferChecked env scrutineeType armPattern
            let armType, typedBody = infer (withVars armVars env) armBody

            try
                unify env.Registry armType sequelType
            with ex when Diagnostics.isDiagnostic ex ->
                let shown =
                    DotNetInterop.showTypesTogether [ prune env.Registry armType; prune env.Registry sequelType ]

                match propagated with
                | Some caseName ->
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: `:propagate` would return %s{Naming.showTypeName caseName} here, but this body has type %s{shown[1]}."
                | None ->
                    // The `void` sequel is the mistake this form invites, and
                    // it is worth naming: it is what a body written for its
                    // effects leaves behind, and the failure that "returned a
                    // value" from it was relying on an escape this form does
                    // not perform.
                    let hint =
                        if shown[1] = "void" then
                            "\nThe rest of the body produces nothing, so the failure may not produce anything either. If it was meant to leave the enclosing block, say so: `(ret ...)` naming an enclosing `with-return`, or `(panic! ...)`."
                        else
                            "\nA failure produces the form's value, as an `if`'s else does — it does not leave the enclosing block by itself. To leave one, write it: `(ret ...)` naming an enclosing `with-return`, or `(panic! ...)`."

                    failwithf
                        $"Type Error at %s{Lexer.formatPos typedBody.Range}: what a `def` produces when its pattern does not match is the value of the whole form, and here it disagrees with the rest of the body:\n  the failure:          %s{shown[0]}\n  the rest of the body: %s{shown[1]}%s{hint}"

            ({ Pattern = typedPattern; Body = typedBody }: TDefMatchArm))

    sequelType,
    { Type = sequelType
      Range = r
      Node = TDefMatch(typedBinder, typedScrutinee, typedSequel, typedArms) }

/// The arms `:propagate` stands for: every case the pattern leaves out, matched
/// and rebuilt, one arm each.
///
/// Each case is rebuilt rather than the scrutinee handed back, which is what
/// lets the type arguments change: `None` is constructed fresh at the body's
/// type, and an `Err`'s payload is carried across into a new one. Whether the
/// body admits that is decided by unifying the arm with the sequel, as for any
/// other arm — so a type parameter the leftover case mentions is forced to be
/// the same in scrutinee and body, and one it does not mention is free.
and private propagatedArms
    (env: Env)
    (scrutineeType: HMType)
    (binder: TypedPattern)
    (r: Range)
    : (string * (Pattern * Expr)) list =
    let settled =
        try
            prune env.Registry scrutineeType
        with _ ->
            scrutineeType

    let shown = DotNetInterop.showType settled

    let rebuild (caseName: string, arity: int) =
        if not (Map.containsKey caseName env.Bindings) then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: `:propagate` rebuilds the cases this pattern leaves out, and %s{Naming.showTypeName caseName} of %s{shown} is not in scope here. Import it, or give a failure value or a :fail clause."

        let carried = List.init arity (fun _ -> Gensym.fresh "carried")

        let rebuilt =
            match carried with
            | [] -> EIdent(caseName, r)
            | _ -> EApp(EIdent(caseName, r), carried |> List.map (fun n -> EIdent(n, r)), r)

        (caseName, (PConstruct(caseName, carried |> List.map (fun n -> PIdent(n, r)), r), rebuilt))

    match uncoveredCases env.Registry settled binder with
    | Some [] ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: this pattern always matches, so the failure part of this def can never run. `:propagate` has no case to rebuild — drop it."
    | Some cases -> cases |> List.map rebuild
    | None ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: `:propagate` rebuilds whole cases of the scrutinee's type, and what this pattern leaves of %s{shown} is not a set of cases. Give a failure value or a :fail clause."

// `Class.Member` — a static field or property. This is how an enum value
// such as `FileMode.Open` is written, and it is why `import/class` is
// useful for a type that has no constructor at all.
and private inferStaticMember (env: Env) (name: string) (r: Range) : HMType * TypedExpr =
    let split = name.LastIndexOf "."
    let alias = name.Substring(0, split)
    let memberName = name.Substring(split + 1)
    let info = env.Registry.ClrClasses[alias]
    let where = Lexer.formatPos r
    let clrType = DotNetInterop.resolveType $" at %s{where}" info.ClrName
    let memberType = DotNetInterop.resolveMemberRead where clrType memberName true

    memberType,
    { Type = memberType
      Range = r
      Node = TForeignStaticGet(info.ClrName, memberName, memberType) }

// An `import/extern` name used as a *value* rather than applied.
//
// A .NET method group is not a value, so the only thing this can mean is a
// lambda that calls it — which needs the parameter types before there are
// any arguments to infer them from. That is what the declared signature is
// for, and why it is required here and optional everywhere else.
//
// An accessor is the exception, and needs no signature: a property has no
// overload set, so its type is known from the member alone. A *static*
// accessor read is not even a lambda — the alias names the value, exactly as
// `FileMode.Open` does.
//
// An ordinary binding of the same name wins. The extern registry is one flat
// namespace shared by every module in the compilation, so without this an
// alias published by some imported library would silently capture calls to a
// function defined right here.
and private inferExternValue (env: Env) (name: string) (r: Range) : HMType * TypedExpr =
    let info = env.Registry.ClrExterns[name]
    let where, clrType, receiverType = externTarget info r

    // Named once: three of the four shapes below build a lambda over the
    // receiver, and all of them have to agree on what its type is.
    let identOf (n: string) (t: HMType) : TypedExpr =
        { Type = t
          Range = r
          Node = TIdent(n, []) }

    match info.Kind with
    // A static read *is* the value, re-read wherever the name stands —
    // `TForeignStaticGet` emits the member access itself, so a property like
    // `DateTime.Now` still means "now" at each mention.
    | ExternGet when not info.IsInstance ->
        let memberType = DotNetInterop.resolveMemberRead where clrType info.MemberName true

        memberType,
        { Type = memberType
          Range = r
          Node = TForeignStaticGet(info.ClrType, info.MemberName, memberType) }

    | ExternGet ->
        let memberType = DotNetInterop.resolveMemberRead where clrType info.MemberName false
        let recv = Gensym.fresh "__foreign"

        let body: TypedExpr =
            { Type = memberType
              Range = r
              Node = TDotPropertyGet(identOf recv receiverType, info.MemberName, memberType) }

        let funType = tfun [ receiverType ] memberType

        funType,
        { Type = funType
          Range = r
          Node = TLambda([ recv ], body) }

    | ExternSet ->
        let memberType = DotNetInterop.resolveMemberWrite where clrType info.MemberName (not info.IsInstance)
        let value = Gensym.fresh "__foreign"
        let valueExpr = identOf value memberType

        let paramNames, paramTypes, node =
            if info.IsInstance then
                let recv = Gensym.fresh "__foreign"

                [ recv; value ],
                [ receiverType; memberType ],
                TDotPropertySet(identOf recv receiverType, info.MemberName, valueExpr)
            else
                [ value ], [ memberType ], TForeignStaticSet(info.ClrType, info.MemberName, valueExpr)

        let body: TypedExpr =
            { Type = TypeConstants.voidType
              Range = r
              Node = node }

        let funType = tfun paramTypes TypeConstants.voidType

        funType,
        { Type = funType
          Range = r
          Node = TLambda(paramNames, body) }

    | ExternMethod ->
        // An async import is not a value either, and for a second reason on
        // top of the method-group one: the eta-expansion would be an
        // ordinary lambda whose body is a yield point, which is §3.1's
        // higher-order restriction with a worse error message. Said here
        // rather than left to `ColourCheck`, which would name a lambda the
        // user never wrote.
        if info.IsAsync then
            failwithf
                $"Type Error at %s{where}: '%s{name}' names the async .NET method '%s{info.ClrType}.%s{info.MemberName}', and calling it is a yield point, so it cannot be used as a value — the (fun ...) it would become may not suspend. Call it directly, or wrap the call in a bjoroutine of your own and pass that."

        match info.GenericTypeArgs with
        // A generic method as a value. The eta-expansion is built from the
        // declared signature rather than from reflection, which is where a
        // generic import's meaning lives anyway — and the lambda is at *one*
        // instantiation, whatever the context settles it to, because a C#
        // delegate cannot be generic.
        | Some _ ->
            let paramTypes, retType, typeArgs = instantiateGenericExtern env.Registry where info

            let methodParams =
                if info.IsInstance then List.tail paramTypes else paramTypes

            let argNames = paramTypes |> List.map (fun _ -> Gensym.fresh "__foreign")
            let argExprs: TypedExpr list = List.map2 identOf argNames paramTypes

            let meta = Some(genericExternMeta info typeArgs methodParams retType)

            let node =
                if info.IsInstance then
                    foreignCallNode info.ClrType info.MemberName (Some(List.head argExprs)) (List.tail argExprs) meta
                else
                    foreignCallNode info.ClrType info.MemberName None argExprs meta

            let resultType = wrapForeignExceptions info.Exceptions retType

            let body: TypedExpr =
                { Type = resultType
                  Range = r
                  Node = node }

            let funType = tfun paramTypes resultType

            funType,
            { Type = funType
              Range = r
              Node = TLambda(argNames, body) }

        | None ->

        match info.DeclaredType with
        | Some(TFun(declaredParams, _, _)) ->
            // The receiver of an instance member is the alias's first
            // parameter and none of the method's, so the declared type is
            // split before reflection sees it and rejoined afterwards.
            let declaredReceiver, methodParamTypes =
                if info.IsInstance then
                    match declaredParams with
                    | recv :: rest -> Some recv, rest
                    | [] ->
                        failwithf
                            $"Type Error at %s{where}: '%s{name}' names the instance method '%s{info.ClrType}.%s{info.MemberName}', whose receiver is its first argument, but its declared type takes none."
                else
                    None, declaredParams

            let resolved = resolveExternMethod where info clrType methodParamTypes
            unifyForeignArgs env.Registry methodParamTypes resolved.ParameterTypes
            declaredReceiver |> Option.iter (fun t -> unify env.Registry t receiverType)

            let retType = wrapForeignExceptions info.Exceptions resolved.ReturnType
            let argNames = resolved.ParameterTypes |> List.map (fun _ -> Gensym.fresh "__foreign")

            // Annotated because `TypedExpr` and `TypedPattern` have the same
            // three field names, and neither of these is in a position that
            // says which one is meant.
            let argExprs: TypedExpr list = List.map2 identOf argNames resolved.ParameterTypes

            let paramNames, paramTypes, node =
                if info.IsInstance then
                    let recv = Gensym.fresh "__foreign"

                    recv :: argNames,
                    receiverType :: resolved.ParameterTypes,
                    TDotMethodCall(
                        identOf recv receiverType,
                        info.MemberName,
                        argExprs,
                        Some(metadataOf resolved info.Exceptions)
                    )
                else
                    argNames,
                    resolved.ParameterTypes,
                    TForeignStaticCall(
                        resolved.DeclaringType,
                        info.MemberName,
                        argExprs,
                        Some(metadataOf resolved info.Exceptions)
                    )

            let body: TypedExpr =
                { Type = retType
                  Range = r
                  Node = node }

            let funType = tfun paramTypes retType

            funType,
            { Type = funType
              Range = r
              Node = TLambda(paramNames, body) }
        | _ ->
            failwithf
                $"Type Error at %s{where}: '%s{name}' names the .NET method '%s{info.ClrType}.%s{info.MemberName}', and a method group is not a value. To use it as one, give it a signature in its import/extern clause; otherwise call it directly."

and private inferIdent (env: Env) (name: string) (r: Range) : HMType * TypedExpr =
    let binding = lookup env name
    let t, tArgs, constraints = instantiate env.Registry binding.Scheme

    // A name with two emitted copies is colour-polymorphic, and its
    // *reference* has to say so, not just its call.
    //
    // The binding is the ordinary copy's, so its arrow is `ESync`, and
    // handing that to a `-?->` parameter bound the parameter's cell to
    // `ESync` before anything had decided anything: `(port->list read-line
    // p)` from a bjoroutine chose the ordinary reader and parked on every
    // line, silently, while `(port->list (bjoroutine (q) (read-line q)) p)`
    // suspended. Same call, and the difference was that one of them
    // mentioned a colour — which is the thing this design exists to avoid.
    //
    // So the reference gets a cell of its own instead. Meeting a parameter
    // declared `->` binds it to `ESync` exactly as before; meeting a `-?->`
    // chains the two and leaves both open, and `EffectGraph` grounds the
    // chain to the colour of the member the reference is written in.
    let t =
        match t with
        | TFun(args, ret, ESync) when Map.containsKey name env.Registry.DoubleDefs ->
            TFun(args, ret, freshEffect ())
        | other -> other

    t,
    { Type = t
      Range = r
      Node = TIdent(name, tArgs) }

// A trait method call, unless the name has been bound over.
//
// This used to dispatch on the name alone, before the environment was
// consulted at all, so nothing a program wrote could intercept it: a local
// called `next` or a parameter called `compare` was accepted, ignored, and
// dead — and the program's own calls to it failed on arity, against the
// programmer's line, naming a parameter they never wrote.
//
// `TraitMethodNames` is what distinguishes the method's own binding from a
// binding over it, which `Bindings` cannot: both sit there under one name.
// An inline trait's methods are not bound at all, hence the first half.
and private inferTraitMethodCall (env: Env) (methodName: string) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let traitName = env.Registry.TraitMethods[methodName]

    // Every argument is positional, keywords included. A trait method's
    // shape is fixed by its trait and no trait declares a keyword
    // parameter, so `#:foo` here can only be the keyword *value* — which
    // `(= k #:foo)` is, now that `Keyword` has an `Eq` implementation. A
    // call written as though it took keyword arguments fails on arity
    // instead, which is what it is.
    let typedArgs = args |> List.map (infer env)

    let methodType, tref = traitCallType env traitName methodName r
    let retType = freshMeta ()

    // The effect is the method's own, copied rather than chosen — the same
    // move the ordinary application makes with `demandedEffect`. Building
    // this arrow with `tfun` spelled it `->` unconditionally, so a trait
    // that declared `-bjo->` met its own call site and was told an ordinary
    // function cannot be used where a bjoroutine is expected.
    unify
        env.Registry
        methodType
        (TFun(typedArgs |> List.map fst, retType, demandedEffect env methodType))

    retType,
    { Type = retType
      Range = r
      Node = TTraitCall(tref, typedArgs |> List.map snd, []) }

// Record and struct construction: `(Car (brand "banana") (year 3000))`.
//
// It arrives as an ordinary application because nothing before this point
// knows which names are record types — and so do the arguments, `(brand
// "banana")` being indistinguishable from a call to `brand` until the head
// is known. Both are reread here, where the registry can say so. The type
// name is the constructor: no field set is ever searched for an owner, and
// two records sharing a field name are no longer in each other's way.
and private inferRecordConstruct (env: Env) (recordTypeName: string) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let writtenFields =
        args
        |> List.map (fun arg ->
            match arg with
            | EApp(EIdent(fieldName, _), [ value ], _) -> fieldName, value
            | bad ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos (exprRange bad)}: '%s{recordTypeName}' is a record type, so each argument is one of its fields, written (field-name value).")

    let instantiatedRecordType, expectedFields, expectedFieldsInstantiated =
        instantiateRecord env.Registry recordTypeName

    let fieldList = expectedFields |> List.map fst |> String.concat ", "

    let provided =
        (Map.empty, writtenFields)
        ||> List.fold (fun acc (name, expr) ->
            if Map.containsKey name acc then
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: field '%s{name}' of '%s{recordTypeName}' is given twice."

            let exprType, typedExpr = infer env expr

            match Map.tryFind name expectedFieldsInstantiated with
            | Some expectedType -> unify env.Registry exprType expectedType
            | None ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos (exprRange expr)}: '%s{recordTypeName}' has no field '%s{name}'. Its fields are: %s{fieldList}."

            Map.add name typedExpr acc)

    // Declaration order, not the order the fields were written in: the
    // constructor a record compiles to takes them positionally, so writing
    // them out of order would otherwise silently swap two same-typed fields.
    let orderedFields =
        expectedFields
        |> List.map (fun (name, _) ->
            match Map.tryFind name provided with
            | Some typedExpr -> name, typedExpr
            | None ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: '%s{recordTypeName}' is missing field '%s{name}'. Every field has to be given.")

    instantiatedRecordType,
    { Type = instantiatedRecordType
      Range = r
      Node = TRecordMake orderedFields }

// `(.Method target args...)` — an instance method call.
and private inferDotMethod (env: Env) (name: string) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let methodName = name.Substring 1
    let where = Lexer.formatPos r

    match args with
    | [] ->
        failwithf
            $"Type Error at %s{where}: '%s{name}' calls an instance method, so its first argument is the object to call it on, but it was given none."
    | target :: rest ->
        let targetType, typedTarget = infer env target
        let clrTarget = receiverClrType where name targetType

        let typedArgs = rest |> List.map (infer env)
        let argTypes = typedArgs |> List.map fst

        settleLiterals argTypes
        let resolved = DotNetInterop.resolveMethod where false clrTarget methodName argTypes

        let coercedArgs =
            reconcileForeignArgs env.Registry (typedArgs |> List.map snd) resolved.ParameterTypes

        // Never exception-wrapped: `import/class` declares one signature —
        // the constructor's — so there is nowhere to say what a method may
        // raise, and wrapping it anyway would swallow exceptions nobody
        // listed.
        let retType = resolved.ReturnType

        retType,
        { Type = retType
          Range = r
          Node = TDotMethodCall(typedTarget, methodName, coercedArgs, Some(metadataOf resolved [])) }

// `(ClassName. args...)` — construction.
and private inferClassConstruct (env: Env) (name: string) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let alias = name.Substring(0, name.Length - 1)
    let info = env.Registry.ClrClasses[alias]
    let where = Lexer.formatPos r
    let clrType = DotNetInterop.resolveType $" at %s{where}" info.ClrName

    let typedArgs = args |> List.map (infer env)
    let argTypes = typedArgs |> List.map fst

    settleLiterals argTypes
    let resolved = DotNetInterop.resolveConstructor where clrType argTypes

    let coercedArgs =
        reconcileForeignArgs env.Registry (typedArgs |> List.map snd) resolved.ParameterTypes

    // The declared signature is enforced against the overload reflection
    // chose, rather than used in place of it. Writing one down is how a
    // reader of the source learns what the constructor takes without
    // consulting the BCL; getting it wrong is an error rather than a
    // silently ignored comment.
    match info.CtorType with
    | Some declared -> unify env.Registry declared (tfun resolved.ParameterTypes resolved.ReturnType)
    | None -> ()

    let retType = wrapForeignExceptions info.CtorExceptions resolved.ReturnType

    let meta =
        { ClrType = resolved.DeclaringType
          ParameterTypes = resolved.ParameterTypes
          Exceptions = info.CtorExceptions }

    retType,
    { Type = retType
      Range = r
      Node = TNewObject(resolved.DeclaringType, coercedArgs, Some meta) }

// A .NET member named by `import/extern`, applied. As above, a binding of
// the same name shadows the alias rather than the other way round.
//
// An instance member's receiver is the first argument and is taken off the
// front here, so that everything below — overload selection, the threaded
// token, a declared signature — works in the member's own parameters. It
// rejoins as the receiver of a `TDotMethodCall`, which is the same node
// `(.Method x ...)` produces; the difference is that this one arrived
// through a clause that could say `#:async`.
and private inferExternCall (env: Env) (name: string) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let info = env.Registry.ClrExterns[name]
    let where, clrType, receiverType = externTarget info r

    /// Splits the receiver off an instance member's argument list.
    ///
    /// The receiver is reconciled like an argument rather than unified with
    /// the declaring type, so that a subclass reaches a member declared on
    /// its base: the upcast is written into the tree exactly as a widening
    /// argument's is, which is also what keeps the C# that reads the
    /// generated call resolving it the same way.
    let takeReceiver (typedArgs: (HMType * TypedExpr) list) =
        match typedArgs with
        | (_, recv) :: rest ->
            let coerced =
                reconcileForeignArgs env.Registry [ recv ] [ receiverType ] |> List.head

            coerced, rest
        | [] ->
            failwithf
                $"Type Error at %s{where}: '%s{name}' names the instance member '%s{info.ClrType}.%s{info.MemberName}', so its first argument is the object to use it on, but it was given none."

    match info.Kind with
    // A property read. There is no overload set and no conversion to make:
    // the member has one type, and the only thing the call site supplies is
    // the receiver.
    | ExternGet ->
        let typedArgs = args |> List.map (infer env)

        if not info.IsInstance then
            failwithf
                $"Type Error at %s{where}: '%s{name}' reads the static property '%s{info.ClrType}.%s{info.MemberName}', so it is a value rather than a call. Write it bare, as '%s{name}'."

        let receiver, rest = takeReceiver typedArgs

        if not rest.IsEmpty then
            failwithf
                $"Type Error at %s{where}: '%s{name}' reads a property, so it takes exactly one argument — the object to read it from — but was given %d{args.Length}."

        let memberType = DotNetInterop.resolveMemberRead where clrType info.MemberName false
        checkDeclaredExtern env.Registry info receiverType true [] memberType

        memberType,
        { Type = memberType
          Range = r
          Node = TDotPropertyGet(receiver, info.MemberName, memberType) }

    // A property write. Void, like `set!`, and for the same reason: the
    // value assigned is not what the form is for, and handing it back would
    // make `(set-length! sb n)` usable as an expression that quietly has a
    // value.
    | ExternSet ->
        let typedArgs = args |> List.map (infer env)
        let memberType = DotNetInterop.resolveMemberWrite where clrType info.MemberName (not info.IsInstance)

        let receiver, rest =
            if info.IsInstance then
                let recv, rest = takeReceiver typedArgs
                Some recv, rest
            else
                None, typedArgs

        let value =
            match rest with
            | [ v ] -> reconcileForeignArgs env.Registry [ snd v ] [ memberType ] |> List.head
            | _ ->
                let wanted = if info.IsInstance then "the object to write it on and the value" else "the value"

                failwithf
                    $"Type Error at %s{where}: '%s{name}' writes a property, so it takes %s{wanted}, but was given %d{args.Length} argument(s)."

        checkDeclaredExtern env.Registry info receiverType receiver.IsSome [ memberType ] TypeConstants.voidType

        let node =
            match receiver with
            | Some recv -> TDotPropertySet(recv, info.MemberName, value)
            | None -> TForeignStaticSet(info.ClrType, info.MemberName, value)

        TypeConstants.voidType,
        { Type = TypeConstants.voidType
          Range = r
          Node = node }

    // A generic method, applied. There is no overload to choose from the
    // arguments here: the import already chose one, against the declared
    // signature, and solved its type arguments. So this is an ordinary
    // polymorphic call — instantiate, unify, done — and the only thing that
    // makes it foreign is where the body ends up.
    | ExternMethod when info.GenericTypeArgs.IsSome ->
        let paramTypes, retType, typeArgs = instantiateGenericExtern env.Registry where info

        if args.Length <> paramTypes.Length then
            let what =
                if info.IsInstance then
                    $"the object to use it on and %d{paramTypes.Length - 1} argument(s)"
                else
                    $"%d{paramTypes.Length} argument(s)"

            failwithf
                $"Type Error at %s{where}: '%s{name}' takes %s{what}, but was given %d{args.Length}."

        let typedArgs = args |> List.map (infer env)

        // Unified rather than scored. A declared signature is not a
        // candidate to be ranked against others — it is what the alias
        // *means* — so an argument that does not fit is a type error naming
        // the two types, exactly as it would be for a Bjolang function.
        List.iter2 (fun (argType, _) paramType -> unify env.Registry argType paramType) typedArgs paramTypes

        let callArgs = typedArgs |> List.map snd

        let receiver, methodArgs, methodParams =
            if info.IsInstance then
                Some(List.head callArgs), List.tail callArgs, List.tail paramTypes
            else
                None, callArgs, paramTypes

        let resultType = wrapForeignExceptions info.Exceptions retType

        let meta =
            Some(genericExternMeta info typeArgs methodParams retType)

        resultType,
        { Type = resultType
          Range = r
          Node = foreignCallNode info.ClrType info.MemberName receiver methodArgs meta }

    | ExternMethod ->
        let allTypedArgs = args |> List.map (infer env)

        let receiver, typedArgs =
            if info.IsInstance then
                let recv, rest = takeReceiver allTypedArgs
                Some recv, rest
            else
                None, allTypedArgs

        let argTypes = typedArgs |> List.map fst

        // An `#:async` import resolves against one more parameter than the
        // call site wrote — the ambient token — and yields the task's result
        // rather than the task, so the two paths differ in what they hand
        // back and agree on everything after it.
        let resolved, visibleParams, callResultType, threadsToken =
            if info.IsAsync then
                resolveAsyncExtern where info clrType argTypes
            elif info.Cancellable then
                // Threaded, but not awaited: the method takes a token and
                // hands back an ordinary value. `File.ReadLinesAsync` is the
                // case — a stream rather than a task, and a token in every
                // overload.
                let resolved, threads = resolveTokenThreadedExtern where info clrType argTypes

                let visible =
                    if threads then
                        resolved.ParameterTypes |> List.truncate (resolved.ParameterTypes.Length - 1)
                    else
                        resolved.ParameterTypes

                resolved, visible, resolved.ReturnType, threads
            else
                let resolved = resolveExternMethod where info clrType argTypes
                resolved, resolved.ParameterTypes, resolved.ReturnType, false

        let coercedArgs =
            reconcileForeignArgs env.Registry (typedArgs |> List.map snd) visibleParams

        checkDeclaredExtern env.Registry info receiverType receiver.IsSome visibleParams callResultType

        let retType = wrapForeignExceptions info.Exceptions callResultType

        let meta =
            Some
                { metadataOf resolved info.Exceptions with
                    ParameterTypes = visibleParams
                    ReturnType = callResultType
                    Await = info.IsAsync
                    AmbientToken = threadsToken
                    Blocking = info.IsBlocking }

        retType,
        { Type = retType
          Range = r
          Node = foreignCallNode resolved.DeclaringType info.MemberName receiver coercedArgs meta }

// `(apply f pos1 ... posN coll)` — spread a collection into `f`'s `#:rest`
// parameter.
//
// An intrinsic rather than a prelude binding because there is no `HMType`
// it could be given: how many arguments it accepts, which parameters they
// fill, and whether the resulting call suspends are all read off whatever
// `f` turns out to be.
//
// `f` has to be a bare name, and that is a real restriction rather than a
// simplification. Whether a parameter is a `#:rest` one is recorded in
// `FunMeta`, which `infer` looks up *by name*; the flat type says
// `(Array %a)` and an ordinary array parameter says exactly the same thing.
// So a computed callee — a parameter, a lambda, an element of a vec — has
// nothing that could distinguish the two, and spreading into a parameter
// that is not variadic is precisely the run-time failure this form exists
// to rule out. `addBinding` drops a shadowed name's `FunMeta` for the same
// reason, so a local `f` cannot inherit a global one's shape either.
and private inferApply (env: Env) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let where = Lexer.formatPos r

    let calleeExpr, callArgs =
        match args with
        | f :: rest -> f, rest
        | [] ->
            failwithf
                $"Type Error at %s{where}: 'apply' needs a function and a collection, as (apply f coll)."

    let calleeName =
        match calleeExpr with
        | EIdent(n, _) when Map.containsKey n env.Bindings -> n
        | _ ->
            failwithf
                $"Type Error at %s{where}: 'apply' needs a named function as its first argument. Whether a parameter is a #:rest one belongs to a function's declaration rather than to its type, so a computed function carries nothing that says how to spread into it."

    let meta =
        match Map.tryFind calleeName env.FunMetas with
        | Some m when m.RestParam.IsSome -> m
        | _ ->
            failwithf
                $"Type Error at %s{where}: '%s{calleeName}' has no #:rest parameter, so there is nothing for 'apply' to spread a collection into. A collection's length is not part of its type, so filling fixed parameters from one would need an arity check at run time. Call '%s{calleeName}' directly with positional arguments instead."

    // Keyword arguments are passed straight through. `FunMeta` is what says
    // which names are keywords, and it is the callee's own — the same list
    // an ordinary call site consults.
    let isDeclaredKw kwName =
        meta.KeywordParams |> List.exists (fun (k, _) -> k = kwName)

    let rec splitArgs positional keywords remaining =
        match remaining with
        | [] -> List.rev positional, List.rev keywords
        | EKeyword(kwName, _) :: value :: rest when isDeclaredKw kwName ->
            splitArgs positional ((kwName, value) :: keywords) rest
        | EKeyword(kwName, kr) :: [] when isDeclaredKw kwName ->
            failwithf $"Keyword argument '#:%s{kwName}' is missing a value at %s{Lexer.formatPos kr}"
        | arg :: rest -> splitArgs (arg :: positional) keywords rest

    let positionalExprs, keywordExprs = splitArgs [] [] callArgs

    // The *last* positional argument is the collection. A syntactic rule,
    // not an inferred one: which argument is spread has to be legible from
    // the call site alone, and a type-directed choice would change under a
    // signature the reader cannot see.
    let fixedExprs, collExpr =
        match List.rev positionalExprs with
        | last :: revFixed -> List.rev revFixed, last
        | [] ->
            failwithf
                $"Type Error at %s{where}: 'apply' needs a collection as its last argument, after any arguments filling '%s{calleeName}'s fixed parameters."

    if fixedExprs.Length < meta.MandatoryCount then
        failwithf
            $"Type Error at %s{where}: '%s{calleeName}' has %d{meta.MandatoryCount} fixed parameter(s) and 'apply' was given %d{fixedExprs.Length}. Every fixed parameter has to be supplied positionally, before the collection."

    if fixedExprs.Length > meta.MandatoryCount then
        failwithf
            $"Type Error at %s{where}: '%s{calleeName}' has %d{meta.MandatoryCount} fixed parameter(s) and 'apply' was given %d{fixedExprs.Length}. Everything before the collection fills a fixed parameter, one for one, and the collection is what fills the #:rest parameter."

    let targetType, typedTarget = infer env calleeExpr
    let fixedTyped = fixedExprs |> List.map (infer env)

    // A literal collection is never built. The elements go straight into
    // the rest array, which is the very node a direct call `(f a b c)`
    // produces — so the two spellings compile to the same call.
    let literalItems =
        match collExpr with
        | EList(items, _) -> Some items
        | EVec(items, _) -> Some items
        | EArray(items, _) -> Some items
        | _ -> None

    let restElem, restNode =
        match literalItems with
        | Some items ->
            let elemSlot = freshMeta ()

            let typedItems =
                items
                |> List.map (fun item ->
                    let itemType, typedItem = infer env item
                    unify env.Registry itemType elemSlot
                    typedItem)

            elemSlot,
            ({ Type = TCon("Array", [ elemSlot ])
               Range = r
               Node = TArrayMake typedItems }: TypedExpr)
        | None ->
            let collType, typedColl = infer env collExpr

            // Named so the conversion is an ordinary typed call. Emitting it
            // as C# text in `Codegen` instead would hide it from
            // `containsAwait`, which walks the typed tree to decide whether
            // the enclosing lambda has to be async.
            let convert (fn: string) (elem: HMType) : TypedExpr =
                let arrayType = TCon("Array", [ elem ])

                { Type = arrayType
                  Range = r
                  Node =
                    TApply(
                        { Type = tfun [ typedColl.Type ] arrayType
                          Range = r
                          Node = TIdent(fn, []) },
                        [ typedColl ],
                        []
                    ) }

            match prune env.Registry collType with
            // Passed through untouched: no conversion and no allocation,
            // which is the case `apply` is actually worth having for.
            | TCon("Array", [ elem ]) -> elem, typedColl
            | TCon("Vec", [ elem ]) -> elem, convert "vec->array" elem
            | TCon("List", [ elem ]) -> elem, convert "list->array" elem
            | TMeta _ ->
                failwithf
                    $"Type Error at %s{where}: 'apply' needs to know the last argument's type here to spread it, and nothing has fixed it yet. Annotate it as an Array, a Vec or a List."
            | other ->
                failwithf
                    $"Type Error at %s{where}: 'apply' can spread an Array, a Vec or a List, and the last argument is %s{DotNetInterop.showType other}."

    // Each keyword slot gets a fresh metavariable rather than the type
    // `FunMeta` recorded, for the reason the ordinary call path gives: the
    // recorded type still carries the declaration's rigid `TVar`s, and the
    // flat unification below is what gives the slot its real type.
    let keywordTyped = keywordExprs |> List.map (fun (n, e) -> n, infer env e)

    let kwSlots =
        meta.KeywordParams
        |> List.map (fun (kwName, _) ->
            let slot = freshMeta ()

            match keywordTyped |> List.tryFind (fun (n, _) -> n = kwName) with
            | Some(_, (valType, _)) -> unify env.Registry valType slot
            | None -> ()

            slot)

    let retType = freshMeta ()
    let flatTypes = (fixedTyped |> List.map fst) @ kwSlots @ [ TCon("Array", [ restElem ]) ]

    // The effect is the callee's own, copied rather than chosen. A pure `f`
    // gives a pure node and a suspending one a suspending node, which is why
    // there is one `apply` and not an `apply` plus an `apply/bjo`:
    // `ColourCheck` and `Codegen` already read the effect off the callee's
    // arrow, so neither needs to know this form exists.
    unify env.Registry targetType (TFun(flatTypes, retType, demandedEffect env targetType))

    retType,
    { Type = retType
      Range = r
      Node =
        TApply(
            typedTarget,
            (fixedTyped |> List.map snd) @ [ restNode ],
            keywordTyped |> List.map (fun (n, (_, te)) -> n, te)
        ) }

and private inferGeneralApp (env: Env) (target: Expr) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let targetType, typedTarget = infer env target

    // Look up FunMeta if the target is a known identifier
    let funMeta =
        match target with
        | EIdent(name, _) -> Map.tryFind name env.FunMetas
        | _ -> None

    let isDeclaredKw kwName =
        match funMeta with
        | Some meta -> meta.KeywordParams |> List.exists (fun (k, _) -> k = kwName)
        | None -> false

    /// The type the callee declares for positional argument `i`, when there
    /// is one worth pushing into the argument.
    ///
    /// Only a list or vec literal asks: it is the one argument shape whose
    /// *elements* need the expected type before they can be inferred at
    /// all, which is what makes `(run/lines '(pipe (ls "-l")))` work
    /// without an annotated intermediate binding.
    ///
    /// Only the mandatory prefix, because past it the flat parameter list
    /// holds the keyword parameters in declaration order and then the rest
    /// array, and neither lines up with an argument's position. And only a
    /// parameter that is already something: an unbound metavariable is a
    /// polymorphic parameter, `(map run/lines ...)`, which expects nothing
    /// in particular and so has nothing to push.
    let expectedParam (i: int) : HMType option =
        let declared =
            match prune env.Registry targetType with
            | TFun(paramTys, _, _) when i < paramTys.Length ->
                match funMeta with
                | Some meta when i >= meta.MandatoryCount -> None
                | _ -> Some(prune env.Registry paramTys[i])
            | _ -> None

        match declared with
        | Some(TMeta _)
        | None -> None
        | Some paramTy -> Some paramTy

    // Separate keyword args from positional args
    // Keyword args appear as EKeyword("name") followed by a value expr when matching a declared keyword parameter
    let rec splitArgs positional keywords remaining =
        match remaining with
        | [] -> List.rev positional, List.rev keywords
        | EKeyword(kwName, _) :: value :: rest when isDeclaredKw kwName ->
            splitArgs positional ((kwName, value) :: keywords) rest
        | EKeyword(kwName, kr) :: [] when isDeclaredKw kwName ->
            failwithf $"Keyword argument '#:%s{kwName}' is missing a value at %s{Lexer.formatPos kr}"
        | arg :: rest -> splitArgs (arg :: positional) keywords rest

    let positionalExprs, keywordExprs = splitArgs [] [] args

    // Positional arguments are inferred in two passes, lambdas last. A
    // lambda's parameter types come from what the callee declares for its
    // position, and `expectedParam` can only report that once the other
    // arguments have pinned it: in `(vec-for-each (fun (r) ...) kept)` it is
    // `kept` that says what `r` is, and the body cannot be inferred before
    // then.
    //
    // Results are written back into their source position, because codegen
    // emits the arguments in the order they are listed and they may have
    // side effects.
    let slots: (HMType * TypedExpr) option array = Array.create (List.length positionalExprs) None

    /// Pin an argument to the parameter it fills, ahead of the flat
    /// unification below, so that a later lambda argument can read its own
    /// parameter types off the callee's arrow.
    ///
    /// The parameter goes first, because `unifyEffect` reads its first
    /// argument as the expectation: the other way round an ordinary
    /// function passed to a `-bjo->` parameter is reported as its opposite
    /// and refused.
    ///
    /// A parameter still waiting on an implementor is skipped, the same
    /// exception `unify` makes for `TFun`: in `(fold + 0 v)` the folding
    /// function mentions `Foldable`'s associated type, which nothing knows
    /// until `v` has been inferred.
    let pinToParam (i: int) (argType: HMType) =
        match expectedParam i with
        | Some paramTy when not (awaitsImplementor env.Registry paramTy || awaitsImplementor env.Registry argType) ->
            unify env.Registry paramTy argType
        | _ -> ()

    positionalExprs
    |> List.iteri (fun i arg ->
        match arg with
        | EFun _ -> ()
        | _ ->
            let argType, typedArg =
                match arg, expectedParam i with
                | (EList _ | EVec _ | EArray _), Some paramTy -> inferChecked paramTy env arg
                | _ -> infer env arg

            pinToParam i argType
            slots[i] <- Some(argType, typedArg))

    let keywordArgs = keywordExprs |> List.map (fun (kwName, value) -> kwName, infer env value)

    positionalExprs
    |> List.iteri (fun i arg ->
        match arg with
        | EFun _ ->
            let inferred =
                match expectedParam i with
                | Some paramTy -> inferChecked paramTy env arg
                | None -> infer env arg

            slots[i] <- Some inferred
        | _ -> ())

    let positionalArgs = slots |> Array.toList |> List.map Option.get
    let retType = freshMeta ()

    match funMeta with
    | Some meta when not keywordArgs.IsEmpty || meta.RestParam.IsSome || not meta.KeywordParams.IsEmpty ->
        // Structured call: separate mandatory, keyword, and rest args
        let mandatoryArgs = positionalArgs |> List.take (min positionalArgs.Length meta.MandatoryCount)
        let restArgs = positionalArgs |> List.skip (min positionalArgs.Length meta.MandatoryCount)

        // Build the flat arg types for unification (mandatory + keyword in decl order + rest array)
        //
        // Each keyword and rest slot gets a *fresh* metavariable rather than
        // the type recorded in `FunMeta`. The recorded type came from the
        // declaration and still carries that declaration's rigid `TVar`s, so
        // unifying an argument against it directly is what used to make
        // `(: f (-> #:rest %a %a))` unusable: the first call tried to unify
        // `int` with `'a` itself instead of with a fresh instance of it.
        //
        // The flat unification against `targetType` below is what gives
        // these slots their real types. `targetType` came from `infer`, which
        // instantiates the scheme, so its parameters are already fresh per
        // call site. `FunMeta` is then consulted only for the call's *shape*
        // — how many mandatory parameters there are, which keywords exist,
        // and whether there is a rest parameter at all.
        let kwArgTypes =
            meta.KeywordParams |> List.map (fun (kwName, _) ->
                let slot = freshMeta ()

                match keywordArgs |> List.tryFind (fun (n, _) -> n = kwName) with
                | Some (_, (valType, _)) -> unify env.Registry valType slot
                | None -> ()  // keyword not provided, will use default

                slot)

        // The rest arguments become *one* argument: an array. That is what
        // the flat type says — `#:rest` resolves to a single `(Array %a)`
        // parameter — and the typed tree has to agree with it.
        //
        // It used to hand `TApply` the rest arguments spread flat, N of
        // them against a type with one parameter, and rely on C# `params`
        // to put them back together at the call site. That works only when
        // the callee is emitted as a real `params` method. Alias the
        // function to a value — `(def f list)` — and the callee is a
        // `Func<int[], SchemeList<int>>` field, delegates have no `params`
        // semantics, and `f(1, 2, 3)` fails to compile in C# after passing
        // the type checker. Materializing the array here makes the two
        // spellings the same call. C# still accepts an explicit array for a
        // `params` parameter, so the direct case is unaffected.
        //
        // `LoopLowering` already builds the same node for a tail call into
        // a rest parameter.
        let restArgTypes, restTypedArgs =
            match meta.RestParam with
            | Some _ ->
                let elemSlot = freshMeta ()

                for (rt, _) in restArgs do
                    unify env.Registry rt elemSlot

                let arrayType = TCon("Array", [elemSlot])

                [ arrayType ],
                [ ({ Type = arrayType
                     Range = r
                     Node = TArrayMake(restArgs |> List.map snd) }: TypedExpr) ]
            | None ->
                if not restArgs.IsEmpty then
                    failwithf $"Too many arguments at %s{Lexer.formatPos r}"
                [], []

        let allFlatTypes = (mandatoryArgs |> List.map fst) @ kwArgTypes @ restArgTypes
        unify env.Registry targetType (TFun(allFlatTypes, retType, demandedEffect env targetType))

        let typedKwArgs =
            keywordArgs |> List.map (fun (n, (_, te)) -> (n, te))

        // Positional args in TApply = mandatory + the rest array (keyword
        // args are separate)
        let positionalTypedArgs =
            (mandatoryArgs |> List.map snd) @ restTypedArgs

        retType,
        { Type = retType
          Range = r
          Node = TApply(typedTarget, positionalTypedArgs, typedKwArgs) }

    | _ ->
        // No FunMeta or no keyword args: simple positional call
        if not keywordArgs.IsEmpty then
            failwithf $"Keyword arguments used on a function without keyword parameter metadata at %s{Lexer.formatPos r}"

        unify
            env.Registry
            targetType
            (TFun(positionalArgs |> List.map fst, retType, demandedEffect env targetType))

        retType,
        { Type = retType
          Range = r
          Node = TApply(typedTarget, positionalArgs |> List.map snd, []) }

and private inferLet (env: Env) (name: string) (isFun: bool) (args: DefunArg list) (typeAnn: FType option) (value: Expr) (body: Expr) (r: Range) : HMType * TypedExpr =
    let inferBinding () =
        // For a function-shaped binding `typeAnn` is the *return* type, and is
        // already accounted for by the shape; for a value binding it is the
        // binding's own type.
        let shape = if isFun then Some(localFunShape env args typeAnn) else None

        let valType, typedVal, localFun =
            match shape with
            | Some s ->
                let lambda, lf = inferLocalFunBody env s args r value
                s.FunType, lambda, lf
            | None ->
                // When the binding has a type annotation, resolve it first and
                // pass it down as the expected type.  For list / vec literals
                // this enables per-element constructor injection before any
                // element-level unification can fail.
                let t, typed =
                    match typeAnn with
                    | Some tAnn ->
                        let expectedType = resolveTypeAnnotation env.Registry tAnn
                        inferChecked expectedType env value
                    | None -> infer env value

                match typeAnn with
                | Some tAnn -> unify env.Registry t (resolveTypeAnnotation env.Registry tAnn)
                | None -> ()

                t, typed, noParams

        shape, valType, typedVal, localFun

    // One level in just for the binding that is actually generalized. A
    // value is bound monomorphically, and its cells belong in the enclosing
    // level — if we raised it here the next sibling binding would quantify them.
    let shape, valType, typedVal, localFun =
        if isFun then atLevel inferBinding else inferBinding ()

    // Only a *function*-shaped local binding is generalized.
    //
    // The value restriction would admit more — a bare lambda is a syntactic
    // value — but C# is the limit here rather than soundness. A local
    // binding that is not a function is emitted as an ordinary local
    // variable, and neither a delegate nor a `SchemeList<T>` local can be
    // generic: there is nowhere for the type parameter to be declared.
    // Quantifying one emitted `Func<T_t__1, T_t__1> id = ...` naming a
    // parameter the enclosing method never declared.
    //
    // A local `defun` is not affected: it becomes a C# local function,
    // which may have type parameters of its own.
    let scheme =
        if isFun then generalizeLocal env valType
        else Scheme([], [], valType)

    let localEnv = addBinding name { Scheme = scheme; IsMutable = false } env

    // Keyword and rest metadata travels with the name, or a call that
    // passes a keyword argument — or omits an optional one — has nothing to
    // resolve against and is checked against the flat arrow instead.
    let localEnv =
        match shape with
        | Some s -> { localEnv with FunMetas = Map.add name s.Meta localEnv.FunMetas }
        | None -> localEnv

    let bodyType, typedBody = infer localEnv body

    bodyType,
    { Type = bodyType
      Range = r
      Node = TLet(name, isFun, localFun, typedVal, typedBody) }

and private inferLetRec (env: Env) (bindings: (string * bool * DefunArg list * FType option * Expr) list) (body: Expr) (r: Range) : HMType * TypedExpr =
    let bindingMetas = bindings |> List.map (fun (n, _, _, _, _) -> n, freshMeta ())

    // Every member's shape is read before any body is checked. Two reasons,
    // and they are the same reason at two scales: a mutually recursive
    // group's earlier member has already said what this one's arguments
    // are, and a body checked against bare metavariables is fatal rather
    // than merely imprecise for an associated type — a projection needs a
    // concrete head and cannot be deferred into a unification. A recursive
    // call that passes a keyword argument needs the `FunMeta` for the same
    // reason it does at the top level.
    let shapes =
        bindings
        |> List.map (fun (_, isFun, args, typeAnn, _) ->
            if isFun then Some(localFunShape env args typeAnn) else None)

    List.iter2
        (fun shape (_, expected) ->
            match shape with
            | Some(s: LocalFunShape) -> unify env.Registry s.FunType expected
            | None -> ())
        shapes
        bindingMetas

    let withMetas (start: Env) =
        List.fold2
            (fun (acc: Env) shape (n, _) ->
                match shape with
                | Some(s: LocalFunShape) -> { acc with FunMetas = Map.add n s.Meta acc.FunMetas }
                | None -> acc)
            start
            shapes
            bindingMetas

    let recEnv =
        bindingMetas
        |> List.fold
            (fun acc (n, t) ->
                addBinding
                    n
                    { Scheme = Scheme([], [], t)
                      IsMutable = false }
                    acc)
            env
        |> withMetas

    let typedBindings =
        List.zip3 bindings shapes bindingMetas
        |> List.map (fun ((name, isFun, args, typeAnn, expr), shape, (_, expectedType)) ->
            let valType, typedVal, localFun =
                match shape with
                | Some s ->
                    let lambda, lf = inferLocalFunBody recEnv s args r expr
                    s.FunType, lambda, lf
                | None ->
                    let t, typed = infer recEnv expr

                    // A value binding's annotation is its own type. A
                    // function's is its return type, and the shape has
                    // already unified it with the body.
                    match typeAnn with
                    | Some tAnn -> unify env.Registry t (resolveTypeAnnotation env.Registry tAnn)
                    | None -> ()

                    t, typed, noParams

            unify env.Registry valType expectedType
            name, isFun, localFun, typedVal)

    // No own level for the group, unlike `ELet`. Each member
    // is bound monomorphically in `recEnv` while the bodies are checked, so
    // the cells belong to the enclosing level and `generalizeLocal` below
    // quantifies nothing beyond what the annotations already named. A
    // recursive local function is monomorphic; see `057_local_generalization`.
    let finalEnv =
        List.zip bindings bindingMetas
        |> List.fold
            // Function-shaped members only, for the reason `ELet` gives:
            // anything else becomes a plain local and cannot carry a type
            // parameter.
            (fun acc ((_, isFun, _, _, _), (n, t)) ->
                addBinding
                    n
                    { Scheme = (if isFun then generalizeLocal recEnv t else Scheme([], [], t))
                      IsMutable = false }
                    acc)
            env
        |> withMetas

    let bodyType, typedBody = infer finalEnv body

    bodyType,
    { Type = bodyType
      Range = r
      Node = TLetRec(typedBindings, typedBody) }

and private inferLetMutable (env: Env) (name: string) (typeAnn: FType option) (value: Expr) (body: Expr) (r: Range) : HMType * TypedExpr =
    let valType, typedVal = infer env value
    
    match typeAnn with
    | Some tAnn ->
        let expectedType = resolveTypeAnnotation env.Registry tAnn
        unify env.Registry valType expectedType
    | None -> ()

    // Deliberately not generalized. A mutable binding is a cell, and a
    // *polymorphic* cell is the value restriction's classic hole: each use
    // would instantiate a fresh variable, so a `set!` at one type and a read
    // at another would both check and disagree about what is in there. If it
    // can be assigned, its type has to be settled.
    let localEnv =
        addBinding
            name
            { Scheme = Scheme([], [], valType)
              IsMutable = true }
            env

    let bodyType, typedBody = infer localEnv body

    bodyType,
    { Type = bodyType
      Range = r
      Node = TLetMutable(name, typedVal, typedBody) }

and private inferSeq (env: Env) (body: Expr) (r: Range) : HMType * TypedExpr =
    let elemType = freshMeta ()

    // The body is run for its yields; whatever its last form evaluates to is
    // discarded, exactly as in `when`. A sequence's *value* is its elements,
    // so there is nothing for the body's own type to agree with.
    let _, tBody = infer (withSeqElement elemType env) body

    let seqType = TCon("Seq", [ elemType ])

    seqType,
    { Type = seqType
      Range = r
      Node = TSeq tBody }

// `(bjo (f x y))`. The call is checked exactly as it would be if it were
// written where it stands — same arguments, same arity, same overloads —
// and only its *result* is repackaged as a promise. Nothing about spawning
// changes what the call means, which is the point of the direct style.
//
// The body may be a call to a bjoroutine or to an ordinary function; both
// are useful and both compile to the same thing. `ColourCheck` allows a
// yield point in there whatever the enclosing colour, because the spawned
// body becomes an async lambda of its own.
// Only `bjo` hands the promise back. The three `spawn` forms are `Unit`,
// because the scope is holding the child and nothing else needs a handle —
// which is what keeps them out of `MustUse`'s way without giving `bjo` an
// exemption from it.
and private inferBjo (env: Env) (call: Expr) (kind: SpawnKind) (r: Range) : HMType * TypedExpr =
    let resultType, tCall = infer env call

    let formType =
        match kind with
        | SpawnScoped -> TCon("Promise", [ resultType ])
        | SpawnUnit
        | SpawnDaemon
        | SpawnDetached -> TypeConstants.unitType

    formType,
    { Type = formType
      Range = r
      Node = TBjo(tCall, kind) }

// `(task->event (fetch url))`. The event of making an async .NET call.
//
// The operand is *not* inferred as an expression, which is the whole point
// of the special form: everywhere else `(fetch url)` means "await this",
// and here it has to mean "hand me the task, unstarted". So the call is
// taken apart and the pieces are re-resolved — same overload rules, same
// arguments, one difference in what comes out.
//
// §7.3, and the reason `Cancellable` rather than `FromTask` is the only
// form the language can reach: a task handed over already running cannot be
// withdrawn from a `choose`, so losing would drop the result and leave the
// work going. Here the branch owns a token, and losing cancels it.
and private inferTaskEvent (env: Env) (call: Expr) (r: Range) : HMType * TypedExpr =
    let where = Lexer.formatPos r

    let name, args =
        match call with
        | EApp(EIdent(n, _), a, _) -> n, a
        | _ ->
            failwithf
                $"Type Error at %s{where}: task->event takes a call to a method imported #:async. To turn a bjoroutine into an event, spawn it and join the promise — (promise-join (bjo (f x))) — though note that losing a choose on a join stops you listening without stopping the work."

    let info =
        match Map.tryFind name env.Registry.ClrExterns with
        | Some i when not (Map.containsKey name env.Bindings) -> i
        | _ ->
            failwithf
                $"Type Error at %s{where}: '%s{name}' is not a method imported by import/extern, so task->event has no task to make an event of. For a bjoroutine, use (promise-join (bjo (%s{name} ...))) instead."

    if info.Kind <> ExternMethod then
        failwithf
            $"Type Error at %s{where}: '%s{name}' reads or writes the property '%s{info.ClrType}.%s{info.MemberName}', which produces no task. task->event takes a call to a method imported #:async."

    if not info.IsAsync then
        failwithf
            $"Type Error at %s{where}: '%s{name}' names '%s{info.ClrType}.%s{info.MemberName}', which is imported without #:async, so calling it produces no task to wait for. An ordinary .NET call is made where it is written and there is nothing to race."

    // The branch's own token is what makes losing mean something. Without a
    // parameter to put it in there is no difference between this and
    // `FromTask`, which §7.3 keeps out of the language on purpose.
    if info.Uncancellable then
        failwithf
            $"Type Error at %s{where}: '%s{name}' is imported #:uncancellable, so a losing choose branch could not stop it — the work would carry on with nobody listening, which is exactly what task->event exists to prevent. Await it directly instead, or find an overload that takes a CancellationToken."

    let _, clrType, receiverType = externTarget info r
    let allTypedArgs = args |> List.map (infer env)

    // An instance member's receiver is the first argument here as it is
    // everywhere else. It is evaluated where the form stands, like the other
    // operands, and only the *call* is deferred to the sync.
    let receiver, typedArgs =
        if info.IsInstance then
            match allTypedArgs with
            | (_, recv) :: rest ->
                Some(reconcileForeignArgs env.Registry [ recv ] [ receiverType ] |> List.head), rest
            | [] ->
                failwithf
                    $"Type Error at %s{where}: '%s{name}' names the instance method '%s{info.ClrType}.%s{info.MemberName}', so its first argument is the object to call it on, but it was given none."
        else
            None, allTypedArgs

    let argTypes = typedArgs |> List.map fst

    if not (DotNetInterop.hasTokenOverload (not info.IsInstance) clrType info.MemberName (Some(argTypes.Length + 1))) then
        failwithf
            $"Type Error at %s{where}: '%s{info.ClrType}.%s{info.MemberName}' has no overload taking these %d{argTypes.Length} argument(s) and a System.Threading.CancellationToken, so this branch would have no way to stop the work it started."

    let resolved =
        resolveExternMethod where info clrType (argTypes @ [ DotNetInterop.cancellationTokenType ])

    // §7.2's third rule, enforced where it bites. A `ValueTask` may be
    // consumed exactly once, so it cannot become an event: the conversion
    // would have to call `.AsTask()` first, which allocates the thing the
    // `ValueTask` existed to avoid.
    if DotNetInterop.isValueTask resolved.RawReturnType then
        failwithf
            $"Type Error at %s{where}: '%s{info.ClrType}.%s{info.MemberName}' returns a ValueTask, which may only be consumed once and therefore cannot become an event. Call it directly — awaiting a ValueTask is fine and is what it is for."

    let awaited =
        match DotNetInterop.awaitedResultType resolved.RawReturnType with
        | Some t -> t
        | None ->
            failwithf
                $"Type Error at %s{where}: '%s{info.ClrType}.%s{info.MemberName}' returns %s{resolved.RawReturnType.Name}, which is not a task."

    let visibleParams = resolved.ParameterTypes |> List.truncate (resolved.ParameterTypes.Length - 1)
    let coercedArgs = reconcileForeignArgs env.Registry (typedArgs |> List.map snd) visibleParams

    checkDeclaredExtern env.Registry info receiverType receiver.IsSome visibleParams awaited

    // A non-generic `Task` carries no result, and `Result<E, void>` is not
    // a type C# has — so the event carries the unit, exactly as a `void`
    // call's `#:exceptions` wrapper does.
    let awaitIsVoid = awaited = TypeConstants.voidType
    let payload = if awaitIsVoid then TypeConstants.unitType else awaited

    // Failure is a value here for the same reason it is at a join: this
    // runs at sync time, on the fiber's stack rather than on the one that
    // completed the task, and a raise there would land in the wrong place.
    // Cancellation arrives as one of those values — a losing branch's
    // `Err` is a `TaskCanceledException` nobody ever looks at.
    let eventType =
        TCon("Event", [ TCon("Result", [ TCon("System.Exception", []); payload ]) ])

    eventType,
    { Type = eventType
      Range = r
      Node = TTaskEvent(receiver, resolved.DeclaringType, info.MemberName, coercedArgs, payload, awaitIsVoid) }

and private inferMatch (env: Env) (target: Expr) (clauses: (Pattern * Expr option * Expr) list) (r: Range) : HMType * TypedExpr =
    let targetType, typedTarget = infer env target
    let returnType = freshMeta ()

    let typedClauses =
        clauses
        |> List.map (fun (pat, guard, body) ->
            let typedPat, boundVars = checkPattern inferChecked env targetType pat

            let boundEnv =
                Map.fold
                    (fun acc n t ->
                        addBinding
                            n
                            { Scheme = Scheme([], [], t)
                              IsMutable = false }
                            acc)
                    env
                    boundVars

            let typedGuard =
                match guard with
                | Some g ->
                    let gType, tg = infer boundEnv g
                    unify env.Registry gType TypeConstants.boolType
                    Some tg
                | None -> None

            let bodyType, typedBody = infer boundEnv body

            unify env.Registry bodyType returnType

            { Pattern = typedPat
              Guard = typedGuard
              Body = typedBody }
            : TMatchClause)

    returnType,
    { Type = returnType
      Range = r
      Node = TMatch(typedTarget, typedClauses) }

and private inferRecordUpdate (env: Env) (targetName: string) (fields: (string * Expr) list) (r: Range) : HMType * TypedExpr =
    let targetBinding = lookup env targetName
    let targetType, _, _ = instantiate env.Registry targetBinding.Scheme
    
    let recordTypeName =
        if fields.IsEmpty then
            failwithf $"Type Error at %s{formatPos r}: a record-set has to update at least one field."

        recordTypeOfField env.Registry targetType (fst fields.Head) r


    let instantiatedRecordType, _, expectedFieldsInstantiated =
        instantiateRecord env.Registry recordTypeName

    unify env.Registry targetType instantiatedRecordType

    let typedFields =
        fields |> List.map (fun (name, expr) ->
            let exprType, typedExpr = infer env expr
            match Map.tryFind name expectedFieldsInstantiated with
            | Some expectedType -> unify env.Registry exprType expectedType
            | None -> failwithf $"Type Error: Field '%s{name}' does not belong to record '%s{recordTypeName}' at %s{Lexer.formatPos r}"
            name, typedExpr)

    targetType,
    { Type = targetType
      Range = r
      Node = TRecordUpdate(targetName, typedFields) }

// `(record-set! r (field value) ...)` — the write in place.
//
// Shaped like `ERecordUpdate` above and checked like it, plus the two
// questions a write has that a copy does not: is this field writable, and
// is this module allowed to write it.
and private inferRecordSet (env: Env) (targetName: string) (fields: (string * Expr) list) (r: Range) : HMType * TypedExpr =
    let targetBinding = lookup env targetName
    let targetType, _, _ = instantiate env.Registry targetBinding.Scheme

    // Non-empty by construction — the parser refuses a `record-set!` that
    // names no field — so the head is safe to resolve the type from.
    let recordTypeName = recordTypeOfField env.Registry targetType (fst fields.Head) r

    let instantiatedRecordType, _, expectedFieldsInstantiated =
        instantiateRecord env.Registry recordTypeName

    unify env.Registry targetType instantiatedRecordType

    // A field is writable only where it was declared. The check is on the
    // *record's* module rather than on the binding's: a value of a foreign
    // record type reaches here by every ordinary route — an argument, a
    // field of something local — and none of them may write it.
    if not (declaredHere env.CurrentModule recordTypeName) then
        let shown = Naming.showTypeName recordTypeName

        failwithf
            $"Type Error at %s{formatPos r}: '%s{shown}' was declared in another module, so this one may not write its fields. A module that means its state to be written from outside exports functions that write it."

    let mutableFields = mutableFieldsOf env.Registry recordTypeName

    let typedFields =
        fields |> List.map (fun (name, expr) ->
            let exprType, typedExpr = infer env expr

            match Map.tryFind name expectedFieldsInstantiated with
            | Some expectedType -> unify env.Registry exprType expectedType
            | None ->
                failwithf
                    $"Type Error at %s{formatPos r}: field '%s{name}' does not belong to record '%s{Naming.showTypeName recordTypeName}'."

            if not (List.contains name mutableFields) then
                let writable =
                    if mutableFields.IsEmpty then "It has no mutable fields."
                    else "Its mutable fields are: " + String.concat ", " mutableFields + "."

                failwithf
                    $"Type Error at %s{formatPos r}: field '%s{name}' of '%s{Naming.showTypeName recordTypeName}' is not mutable, so it cannot be written in place. Declare it (: %s{name} <type> #:mutable), or use record-set for a copy. %s{writable}"

            name, typedExpr)

    // Void, as every other write in the language is. The value it might
    // have handed back — the record — is the same object either way, so
    // returning it would only invite `(def r2 (record-set! r ...))` to read
    // as though it were a copy.
    TypeConstants.unitType,
    { Type = TypeConstants.unitType
      Range = r
      Node = TRecordSet(targetName, typedFields) }

// `(dyn ->str 42)` — packing a value into a trait box.
//
// The argument's type is represented by a hole metavariable, so whatever
// binds it binds the implementation. The obligation is queued just like a
// trait method call: the hole is prevented from being generalized
// prematurely, and resolving it pins the box. If it remains open,
// projections linger and the enclosing function receives the constraint as a
// `_dict_` parameter like any other trait usage.
and private inferDynPack (env: Env) (writtenTrait: string) (valueExpr: Expr) (r: Range) : HMType * TypedExpr =
    let traitName = originalName env.Registry writtenTrait

    let info =
        match Map.tryFind traitName env.Registry.Traits with
        | Some i -> i
        | None ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: '%s{writtenTrait}' is not a trait in scope, so (dyn %s{writtenTrait} ...) packs nothing."

    match info.DynSafe with
    | Error why -> failwithf $"Type Error at %s{Lexer.formatPos r}: %s{why}"
    | Ok() -> ()

    let hole, typedValue = infer env valueExpr

    let tref =
        { Trait = traitName
          Method = writtenTrait
          Holes = [ hole ]
          MethodType = tfun [ hole ] TypeConstants.unitType
          MemberConstraints = []
          Resolved = None }

    pushWanted
        { Trait = traitName
          Method = writtenTrait
          Kind = info.Kind
          HoleArgs = [ hole, [] ]
          Ref = tref
          Range = r }

    // Associated types are never specified at packing time: they are
    // resolved from the implementation chosen for the hole. `prune`
    // resolves projections as soon as the hole becomes concrete, unifying
    // with explicit annotations.
    let dynType =
        TCon(
            Naming.dynTypeName traitName info.AssociatedTypes,
            info.AssociatedTypes |> List.map (fun a -> TAssoc(traitName, a, hole))
        )

    dynType,
    { Type = dynType
      Range = r
      Node = TDynPack(traitName, hole, typedValue) }

/// Infer `expr` expecting it to have type `expected`, enabling constructor
/// injection for list and vec literals.  For any other expression shape the
/// call degrades to a plain `infer` so the annotation is unified afterwards
/// by the caller as usual.
/// Checks a local function's body and its keyword defaults against the shape
/// already read off its argument list.
///
/// The order is the one a top-level `defun` uses: the body first, in a scope
/// holding every parameter, and then each default in the *mandatory*-argument
/// scope extended by the keyword parameters before it. A default may therefore
/// name an earlier parameter and not a later one, which is the only order that
/// can be evaluated.

and private inferLocalFunBody
    (env: Env)
    (shape: LocalFunShape)
    (args: DefunArg list)
    (r: Range)
    (value: Expr)
    : TypedExpr * LocalFun =

    let bind name t acc =
        addBinding name { Scheme = Scheme([], [], t); IsMutable = false } acc

    let envWithMandatory = shape.Mandatory |> List.fold (fun acc (n, t) -> bind n t acc) env

    let bodyEnv =
        shape.Keywords |> List.fold (fun acc (n, t, _) -> bind n t acc) envWithMandatory

    let bodyEnv =
        match shape.Rest with
        | Some(n, t) -> bind n (TCon("Array", [ t ])) bodyEnv
        | None -> bodyEnv

    let bodyType, typedBody = infer bodyEnv value
    unify env.Registry bodyType shape.RetType

    let typedKeywords, _ =
        shape.Keywords
        |> List.fold
            (fun (acc, currentEnv) (n, t, defaultExpr) ->
                let defaultType, typedDefault = infer currentEnv defaultExpr
                unify env.Registry defaultType t
                acc @ [ n, t, typedDefault ], bind n t currentEnv)
            ([], envWithMandatory)

    let paramNames = allArgNames args

    { Type = shape.FunType
      Range = r
      Node = TLambda(paramNames, typedBody) },
    { Params = paramNames
      KeywordArgs = typedKeywords
      RestArg = shape.Rest }

and internal inferChecked (expected: HMType) (env: Env) (expr: Expr) : HMType * TypedExpr =
    match expr, prune env.Registry expected with
    | EList(exprs, r), TCon("List", [ elemTy ]) ->
        let typedExprs = exprs |> List.map (inferAndMaybeInject elemTy env)
        TCon("List", [ elemTy ]),
        { Type = TCon("List", [ elemTy ]); Range = r; Node = TListMake typedExprs }
    | EVec(exprs, r), TCon("Vec", [ elemTy ]) ->
        let typedExprs = exprs |> List.map (inferAndMaybeInject elemTy env)
        TCon("Vec", [ elemTy ]),
        { Type = TCon("Vec", [ elemTy ]); Range = r; Node = TVecMake typedExprs }
    | EArray(exprs, r), TCon("Array", [ elemTy ]) ->
        let typedExprs = exprs |> List.map (inferAndMaybeInject elemTy env)
        TCon("Array", [ elemTy ]),
        { Type = TCon("Array", [ elemTy ]); Range = r; Node = TArrayMake typedExprs }

    // A lambda whose parameters the expectation already names.
    | EFun(args, body, colour, r), TFun(paramTys, _, _) when List.length paramTys = List.length args ->
        inferLambda (Some paramTys) env args body colour r

    | _ ->
        infer env expr

/// A lambda. `pins` are the parameter types the context expects, when it
/// expects any: unifying them before the body is inferred is what lets the body
/// read a record field off a parameter, since `recordTypeOfField` needs the
/// record type at the moment of the access.
///
/// The effect is the lambda's own keyword and never comes from the context. A
/// `fun` is `ESync` and a `bjoroutine` is `EAsync`, `ColourCheck` reads that off
/// the node, and taking it from a `-bjo->` parameter instead would repaint the
/// lambda and make the colour diagnostics wrong.
and private inferLambda
    (pins: HMType list option)
    (env: Env)
    (args: string list)
    (body: Expr)
    (colour: Colour)
    (r: Range)
    : HMType * TypedExpr =
    let argTypes = args |> List.map (fun _ -> freshMeta ())

    match pins with
    | Some paramTys -> List.iter2 (fun argTy paramTy -> unify env.Registry argTy paramTy) argTypes paramTys
    | None -> ()

    let localEnv =
        List.zip args argTypes
        |> List.fold
            (fun acc (n, t) ->
                addBinding
                    n
                    { Scheme = Scheme([], [], t)
                      IsMutable = false }
                    acc)
            (withoutSeqElement env)

    let bodyType, typedBody = infer localEnv body
    let funType = TFun(argTypes, bodyType, colourEffect colour)

    funType,
    { Type = funType
      Range = r
      Node = TLambda(args, typedBody) }

/// Check one element of a literal against the type its position expects,
/// injecting a union constructor around it where the expectation is a union.
///
/// Two ways to pick that constructor, and which applies is decided by the
/// element:
///
/// *Shape* first. A literal — a nested list, a string, a symbol, a number —
/// selects by what it is written as, against the *head* of each case's payload.
/// This has to happen before the element is inferred, because for a nested
/// heterogeneous literal inferring is what fails. The chosen payload is then
/// pushed back down through `inferChecked`, which calls this function again for
/// each of that literal's own elements. Recursion terminates because the
/// expression strictly shrinks; mutually recursive unions need nothing extra,
/// since every step is a fresh lookup in `Registry.Unions`.
///
/// *Type* otherwise. An unquoted `,value` has no shape to be read — a
/// `(-> string string)` is a candidate for `ProcFn` because of its type and
/// nothing else — so it is inferred first and matched against the payloads
/// afterwards.
and private inferAndMaybeInject (expectedElem: HMType) (env: Env) (expr: Expr) : TypedExpr =
    let pe = prune env.Registry expectedElem

    /// The typed constructor application, around an already typed payload.
    let wrapInCtor (ctorName: string) (payloadTy: HMType) (payload: TypedExpr) : TypedExpr =
        let ctorType = tfun [ payloadTy ] pe
        let ctorExpr: TypedExpr = { Type = ctorType; Range = payload.Range; Node = TIdent(ctorName, []) }
        { Type = pe; Range = payload.Range; Node = TApply(ctorExpr, [ payload ], []) }

    match pe, literalPayloadHeads expr with
    | TCon(unionName, typeArgs), Some heads when Map.containsKey unionName env.Registry.Unions ->
        // The literal's own range, not the range of whatever list it is written
        // in: the constructor that cannot be chosen is this one's.
        let reportAt () = Lexer.formatPos (exprRange expr)
        let shape = literalShapeName expr
        // Shown by key, like every other type name in a diagnostic: which
        // module's union this is may be the whole of what the reader is
        // missing.
        let shownUnion = Naming.showTypeName unionName

        match env.Registry.CasesByPayloadShape unionName typeArgs heads with
        | [ (ctorName, payloadTy) ] ->
            let payloadType, typedPayload = inferChecked payloadTy env expr
            unify env.Registry payloadType payloadTy
            wrapInCtor ctorName payloadTy typedPayload
        | [] ->
            failwithf
                $"Type Error at %s{reportAt ()}: no case of the union %s{shownUnion} carries a %s{shape}, so this literal cannot be one. A literal written where a union is expected is elaborated into the case that holds it, and %s{shownUnion} has %s{describeUnionCases env.Registry unionName}."
        | many ->
            let names = many |> List.map (fst >> Naming.showTypeName) |> orList

            failwithf
                $"Type Error at %s{reportAt ()}: this %s{shape} literal could be injected into %s{names}, and nothing here says which. They are all cases of %s{shownUnion} that carry a %s{shape}, and only the payload's head constructor is compared against the literal — never its arguments — so the literal itself cannot tell them apart. Mark the case a literal means with #:literal where %s{shownUnion} is declared, or write the constructor around it here."
    | _ ->
        // Type-directed. The element is inferred first, and `CandidateCases`
        // then asked — speculatively, so it may not bind anything — whether one
        // constructor's payload matches what came back.
        //
        // `inferChecked` rather than `infer`, because the expectation is worth
        // pushing even when it names no union: at `(List ProcList)` the element
        // is a list of its own, and its elements are what the unions are at.
        let elemTy, te = inferChecked pe env expr
        let pg = prune env.Registry elemTy

        match pe with
        | TCon(unionName, typeArgs) when Map.containsKey unionName env.Registry.Unions ->
            match env.Registry.CandidateCases unionName typeArgs pg with
            | [ (ctorName, [ payloadTy ]) ] ->
                // Exactly one constructor's payload matches. Unify the element
                // against it — which resolves any metavariable left on either
                // side — and wrap.
                unify env.Registry elemTy payloadTy
                wrapInCtor ctorName payloadTy te
            | _ ->
                // Zero or ambiguous matches — unify directly and let the type
                // error (if any) be reported normally.
                unify env.Registry elemTy expectedElem
                te
        | _ ->
            unify env.Registry elemTy expectedElem
            te

// --- DECLARATION CHECKING ---

