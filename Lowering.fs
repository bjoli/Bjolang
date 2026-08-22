module Bjolang.Lowering

open Bjolang.TypedAST
open Bjolang.Unification

/// Resolves trait dispatch after type inference has run.
///
/// Concrete receivers are devirtualized into direct static calls; generic
/// receivers are dispatched through explicit dictionary parameters that are
/// injected into the enclosing function's signature.
///
/// `TMatch` nodes are passed through untouched - pattern matching is emitted
/// directly as C# patterns by the code generator.
/// A name an inlined body was qualified with — `core_Module::helper` — pointing
/// at the module that actually defines it.
///
/// `Lowering` looks a callee up in `env.Bindings` to decide whether it has to
/// forward dictionaries to it, and a qualified name is not a key there. Left
/// alone, an inlined body that calls a constrained generic function would
/// silently lose its dictionary arguments.
let unqualify (name: string) =
    match name.LastIndexOf "::" with
    | -1 -> name
    | i when name.Substring(0, i).EndsWith "_Module" -> name.Substring(i + 2)
    | _ -> name

/// The type of a dictionary for `traitName` at `implType`.
///
/// A trait is emitted as an interface parameterized by its implementor *and*
/// every associated type — `Foldable<T_col, T_item>` — so a dictionary has to
/// name them all. For a concrete implementor `prune` resolves each projection
/// through the registry; for a type variable it leaves a `TAssoc` standing,
/// which the code generator spells as a synthesized type parameter.
let dictionaryType (env: Env) (traitName: string) (implType: HMType) : HMType =
    let assocArgs =
        match Map.tryFind traitName env.Registry.Traits with
        | Some info ->
            info.AssociatedTypes
            |> List.map (fun assocName -> prune env.Registry (TAssoc(traitName, assocName, implType)))
        | None -> []

    TCon(traitName, implType :: assocArgs)

/// Does this implementation stand on a `(where ...)`?
///
/// The one question every dispatch site has to ask before naming a class: a
/// conditional impl is emitted without an `Instance`, so the landing pad that
/// routes through one does not exist for it.
let isConditional (env: Env) (traitName: string) (ctor: string) =
    match Map.tryFind (traitName, ctor) env.Registry.ImplTargets with
    | Some target -> not target.Constraints.IsEmpty
    | None -> false

/// What the code being lowered has to prove things with.
///
/// `Dicts` is what arrived by name: a constrained function's parameters, or a
/// conditional impl's fields. `Self` is the enclosing implementation, if there
/// is one — the dictionary for its own trait at its own target is the object
/// the method is already running on, so a self-call must not build a second
/// one per step.
/// `Within` is diagnostic only: which implementation a body belongs to, for the
/// failures that surface here rather than during inference. It is separate from
/// `Self`, which is only set when there is a dictionary to reuse.
type Scope =
    { Dicts: Map<string, string>
      Self: (string * HMType) option
      Within: string option }

    static member Empty = { Dicts = Map.empty; Self = None; Within = None }

/// The name a conditional impl reads its own dictionary under. Emitted by
/// `Codegen` as a property returning `this`, so it costs no storage and the
/// call through it is a call on a sealed type of exactly known identity.
let selfDictName (traitName: string) = dictParamName traitName "self"

/// The dictionary that proves `traitName` at `implType`, as an expression.
///
/// Four shapes, and the recursion is what conditional impls buy:
///
///     ->str int             ToStr_Int::Instance
///     ->str (List int)      ToStr_List<int>::Make(ToStr_Int::Instance)
///     ->str 'a              _dict_->str_'a, from the enclosing scope
///     the enclosing impl     _dict_->str_self, which is `this`
///
/// `scope` is what the enclosing function or impl class has to offer, so a
/// variable bottoms out there. It terminates on everything else because a
/// conditional impl constrains only its own target variables, and those stand
/// for proper subterms of the type being proved.
let rec buildEvidence
    (env: Env)
    (scope: Scope)
    (traitName: string)
    (implType: HMType)
    (range: Lexer.Range)
    (describe: string)
    : TypedExpr =

    let resolved = prune env.Registry implType
    let dictType = dictionaryType env traitName resolved

    let isSelf =
        match scope.Self with
        | Some(selfTrait, selfTarget) -> selfTrait = traitName && prune env.Registry selfTarget = resolved
        | None -> false

    match resolved with
    | _ when isSelf ->
        { Type = dictType
          Range = range
          Node = TIdent(selfDictName traitName, []) }

    | TVar varName ->
        let name = dictParamName traitName varName

        if not (Map.containsKey name scope.Dicts) then
            failwithf $"Missing dictionary '%s{name}' %s{describe} at %s{Lexer.formatPos range}"

        { Type = dictType
          Range = range
          Node = TIdent(name, []) }

    // A tuple dispatches under its synthetic arity key and is otherwise an
    // ordinary head applied to its element types.
    | TCon _
    | TTuple _ ->
        let typeName, tconArgs =
            match resolved with
            | TTuple args -> tupleCtor args.Length, args
            | TCon(n, args) -> n, args
            | _ -> failwith "unreachable"

        // The same two levels resolution uses — exact head, then the trait's
        // blanket. This is the site that makes the §0.2 guarantee concrete: a
        // value routed through a constrained generic function gets the impl
        // chosen *here*, at the concrete instantiation, so it is the specific
        // one whenever there is one.
        let hasSpecific = Map.containsKey (traitName, typeName) env.Registry.ImplTargets

        let ctor, classTyArgs =
            if hasSpecific then
                typeName, tconArgs
            else
                // The blanket's one class type parameter is the implementor
                // itself, not the head's arguments.
                BlanketCtor, [ resolved ]

        let constraints =
            match implFor env.Registry traitName resolved with
            | Some(target, subst) ->
                target.Constraints
                |> List.map (fun c -> c.TraitName, substTypeVars subst c.TargetType)
            | None ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos range}: no implementation of trait '%s{traitName}' for '%s{Naming.showTypeName typeName}', needed %s{describe}."

        if constraints.IsEmpty then
            { Type = dictType
              Range = range
              Node = TIdent(implSingletonName traitName ctor, classTyArgs) }
        else
            // A conditional impl has no singleton — it does not exist until the
            // dictionaries it stands on do — so the evidence is built by its
            // factory out of the evidence for its where clause.
            let subEvidence =
                constraints
                |> List.map (fun (cTrait, cType) -> buildEvidence env scope cTrait cType range describe)

            let calleeType =
                tfun (subEvidence |> List.map (fun e -> e.Type)) dictType

            let callee =
                { Type = calleeType
                  Range = range
                  Node = TIdent(implFactoryName traitName ctor, classTyArgs) }
                : TypedExpr

            { Type = dictType
              Range = range
              Node = TApply(callee, subEvidence, []) }

    // A structural type with no head constructor: a function, or a tuple of an
    // arity nothing implements. There is nothing to key an implementation by,
    // so not even a blanket is reachable — resolution needs a head to look up
    // before it can fall back to one.
    | other ->
        let within =
            match scope.Within with
            | Some w -> $"\n  in %s{w}"
            | None -> ""

        failwithf
            $"Type Error at %s{Lexer.formatPos range}: no implementation of trait '%s{traitName}' for '%s{DotNetInterop.showType other}', needed %s{describe}. That type has no head constructor, so it cannot have one — and a blanket implementation is only reached through a head.%s{within}"

module DictionaryLowering =

    let rec lowerExpr (env: Env) (scope: Scope) (expr: TypedExpr) : TypedExpr =
        let recurse e = lowerExpr env scope e

        match expr.Node with
        // A trait call the inliner did not splice. The node says which trait it
        // belongs to and, if the solver got there, which implementation — so
        // nothing here is derived from the method name.
        | TTraitCall(tref, args, kwArgs) ->
            let loweredArgs = args |> List.map recurse
            let loweredKwArgs = kwArgs |> List.map (fun (n, e) -> n, recurse e)

            let node =
                match tref.Resolved with
                | Some(ctor, tyArgs) when isConditional env tref.Trait ctor ->
                    // A conditional impl, whose class has no `Instance` for a
                    // landing pad to route through. The dictionary is built for
                    // this one concrete type and dispatched on directly: still
                    // static, since the type is exact and the class is sealed.
                    let hole =
                        match tref.Holes with
                        | h :: _ -> prune env.Registry h
                        | [] ->
                            failwithf
                                $"Trait method '%s{tref.Method}' has no implementor to dispatch on at %s{Lexer.formatPos expr.Range}"

                    let dict =
                        buildEvidence
                            env
                            scope
                            tref.Trait
                            hole
                            expr.Range
                            $"to call '%s{tref.Method}'"

                    TInterfaceCall(dict.Type, tref.Method, dict, loweredArgs)

                | Some(ctor, tyArgs) ->
                    // Static dispatch: the landing pad, named directly.
                    let kind =
                        match Map.tryFind tref.Trait env.Registry.Traits with
                        | Some info -> info.Kind
                        | None -> InterfaceTrait

                    let calleeType =
                        tfun
                            ((loweredArgs |> List.map (fun a -> a.Type))
                             @ (loweredKwArgs |> List.map (fun (_, e) -> e.Type)))
                            expr.Type

                    let callee =
                        { Type = calleeType
                          Range = expr.Range
                          Node = TIdent(landingPadName kind tref.Trait ctor tref.Method, tyArgs) }
                        : TypedExpr

                    TApply(callee, loweredArgs, loweredKwArgs)

                | None ->
                    // Generic dispatch, through the dictionary the enclosing
                    // function was given. Only an interface trait ever gets
                    // here: an unresolved inline-trait call was rejected during
                    // inference, because there is no dictionary to pass.
                    let hole =
                        match tref.Holes with
                        | h :: _ -> prune env.Registry h
                        | [] ->
                            failwithf
                                $"Trait method '%s{tref.Method}' has no implementor to dispatch on at %s{Lexer.formatPos expr.Range}"

                    let dictIdent =
                        buildEvidence env scope tref.Trait hole expr.Range "for trait dispatch"

                    TInterfaceCall(dictIdent.Type, tref.Method, dictIdent, loweredArgs)

            { expr with Node = node }

        | TApply(target, args, kwArgs) ->
            // The callee may carry trait constraints that require us to pass
            // dictionaries explicitly.
            let node =
                    let standardCall () =
                        TApply(
                            recurse target,
                            args |> List.map recurse,
                            kwArgs |> List.map (fun (n, e) -> n, recurse e)
                        )

                    match target.Node with
                    | TIdent(calleeName, tArgs) ->
                        match Map.tryFind (unqualify calleeName) env.Bindings with
                        | Some binding ->
                            let (Scheme(schemeVars, constraints, _)) = binding.Scheme

                            if not constraints.IsEmpty && not tArgs.IsEmpty then
                                // Build a substitution from scheme vars to instantiated types
                                let varSubst =
                                    List.zip schemeVars (tArgs |> List.map (prune env.Registry))
                                    |> Map.ofList

                                // Build dictionary arguments for each constraint
                                let dictArgs =
                                    constraints
                                    |> List.map (fun c ->
                                        let resolvedType =
                                            match c.TargetType with
                                            | TVar varName ->
                                                match Map.tryFind varName varSubst with
                                                | Some t -> prune env.Registry t
                                                | None -> c.TargetType
                                            | _ -> prune env.Registry c.TargetType

                                        buildEvidence
                                            env
                                            scope
                                            c.TraitName
                                            resolvedType
                                            expr.Range
                                            $"to call '%s{calleeName}'")

                                TApply(
                                    recurse target,
                                    dictArgs @ (args |> List.map recurse),
                                    kwArgs |> List.map (fun (n, e) -> n, recurse e)
                                )
                            else
                                standardCall ()
                        | None -> standardCall ()
                    | _ -> standardCall ()

            { expr with Node = node }

        // Everything else recurses structurally.
        | _ -> TypeVisitor.mapChildren recurse expr

    let rec lowerDecl (env: Env) (decl: TDecl) : TDecl =
        match decl with
        | TDef(name, value, t, r) -> TDef(name, lowerExpr env Scope.Empty value, t, r)

        | TDefTuple(names, value, t, r) -> TDefTuple(names, lowerExpr env Scope.Empty value, t, r)

        | TDefMutable(name, value, t, r) -> TDefMutable(name, lowerExpr env Scope.Empty value, t, r)

        | TDefun(name, tyArgs, args, kwArgs, restArg, retType, effect, body, r) ->
            // An inline trait's methods are never bound as values — there is no
            // scheme they could be bound under — so an impl of one has nothing
            // to look up here. It also has nothing to look up *for*: an inline
            // trait carries no dictionaries.
            let constraints =
                match Map.tryFind name env.Bindings with
                | Some binding ->
                    let (Scheme(_, cs, _)) = binding.Scheme
                    cs
                | None -> []

            match Scheme([], constraints, retType) with
            | Scheme(_, constraints, _) ->
                // Inject dictionary parameters into generic functions at the declaration level
                let dictParams =
                    constraints
                    |> List.map (fun c ->
                        let typeVarName =
                            match prune env.Registry c.TargetType with
                            | TVar n -> n
                            | _ -> "unknown"

                        let paramName = $"_dict_%s{c.TraitName}_%s{typeVarName}"
                        paramName, dictionaryType env c.TraitName c.TargetType)

                // Each associated type of a constrained trait becomes a type
                // parameter of the function itself. The caller never writes it:
                // C# infers it from the dictionary argument, whose impl class
                // fixes the association (`Foldable_Vec<int>` is a
                // `Foldable<Vec<int>, int>`).
                let assocTyArgs =
                    constraints
                    |> List.collect (fun c ->
                        match prune env.Registry c.TargetType, Map.tryFind c.TraitName env.Registry.Traits with
                        | TVar typeVarName, Some info ->
                            info.AssociatedTypes
                            |> List.map (assocTypeVar typeVarName)
                        | _ -> [])

                // A function's dictionaries all arrive as parameters: there is
                // no enclosing implementation for a call to be a self-call of.
                let scope =
                    { Scope.Empty with
                        Dicts =
                            dictParams
                            |> List.fold (fun acc (dName, _) -> Map.add dName dName acc) Map.empty }

                let loweredBody = lowerExpr env scope body

                let loweredKwArgs =
                    kwArgs |> List.map (fun (n, t, e) -> n, t, lowerExpr env scope e)

                TDefun(
                    name,
                    (tyArgs @ assocTyArgs) |> List.distinct,
                    dictParams @ args,
                    loweredKwArgs,
                    restArg,
                    retType,
                    effect,
                    loweredBody,
                    r
                )

        | TImpl(traitName, kind, holeArity, targetType, assoc, dicts, methods, r) ->
            // A conditional impl's methods dispatch through the class's fields
            // rather than through parameters of their own. The names are the
            // same either way, so a body reads identically whether it ended up
            // in a constrained function or in an impl that carries the same
            // evidence — and `lowerDecl`'s ordinary `TDefun` path, which would
            // look the constraints up under the *trait method's* name and find
            // none, is bypassed for exactly that reason.
            let scope =
                { Dicts = dicts |> List.fold (fun acc (name, _) -> Map.add name name acc) Map.empty
                  // What this class is a dictionary *of*. A method dispatching
                  // its own trait at its own target — the recursive step of
                  // `size` over a list — is already holding the answer, and
                  // rebuilding it would allocate one dictionary per element.
                  Self = if dicts.IsEmpty then None else Some(traitName, targetType)
                  Within =
                    Some
                        $"the implementation of '%s{traitName}' for '%s{DotNetInterop.showType targetType}'" }

            let lowerMethod (m: TDecl) =
                match m with
                | TDefun(name, tyArgs, args, kwArgs, restArg, retType, effect, body, mr) ->
                    TDefun(
                        name,
                        tyArgs,
                        args,
                        kwArgs |> List.map (fun (n, t, e) -> n, t, lowerExpr env scope e),
                        restArg,
                        retType,
                        effect,
                        lowerExpr env scope body,
                        mr
                    )
                | other -> lowerDecl env other

            TImpl(traitName, kind, holeArity, targetType, assoc, dicts, methods |> List.map lowerMethod, r)

        | TModule(name, decls, r) -> TModule(name, decls |> List.map (lowerDecl env), r)

        | _ -> decl // TTrait, TImport, TExport, TType, TTypeRec, TExtern

/// Runs every post-inference lowering stage over a checked program.
let lowerProgram (env: Env) (decls: TDecl list) : TDecl list =
    decls |> List.map (DictionaryLowering.lowerDecl env)
