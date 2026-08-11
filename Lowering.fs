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
/// The name a devirtualized trait method call is emitted under. `Codegen`
/// rewrites `::` to `.`, so this names the singleton's instance method.
let implInstanceMethod (traitName: string) (targetTypeName: string) (methodName: string) =
    implInstanceMethodName traitName targetTypeName methodName

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

module DictionaryLowering =

    let rec lowerExpr (env: Env) (activeDicts: Map<string, string>) (expr: TypedExpr) : TypedExpr =
        let recurse e = lowerExpr env activeDicts e

        match expr.Node with
        // A trait call the inliner did not splice. The node says which trait it
        // belongs to and, if the solver got there, which implementation — so
        // nothing here is derived from the method name.
        | TTraitCall(tref, args, kwArgs) ->
            let loweredArgs = args |> List.map recurse
            let loweredKwArgs = kwArgs |> List.map (fun (n, e) -> n, recurse e)

            let node =
                match tref.Resolved with
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

                    match hole with
                    | TVar varName ->
                        let expectedDictName = $"_dict_%s{tref.Trait}_%s{varName}"

                        if not (Map.containsKey expectedDictName activeDicts) then
                            failwithf
                                $"Missing dictionary '%s{expectedDictName}' for trait dispatch at %s{Lexer.formatPos expr.Range}"

                        let dictIdent =
                            { Type = dictionaryType env tref.Trait hole
                              Range = expr.Range
                              Node = TIdent(expectedDictName, []) }
                            : TypedExpr

                        TInterfaceCall(dictIdent.Type, tref.Method, dictIdent, loweredArgs)

                    | other ->
                        failwithf
                            $"Unsupported receiver type %s{DotNetInterop.showType other} for trait dispatch at %s{Lexer.formatPos expr.Range}"

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

                                        match resolvedType with
                                        | TCon(typeName, tconArgs) ->
                                            // Static dispatch: pass the singleton Instance.
                                            //
                                            // The same two levels resolution
                                            // uses — exact head, then the
                                            // trait's blanket. This is the site
                                            // that makes the §0.2 guarantee
                                            // concrete: a value routed through
                                            // a constrained generic function
                                            // gets the impl chosen *here*, at
                                            // the concrete instantiation, so it
                                            // is the specific one whenever there
                                            // is one.
                                            let hasSpecific =
                                                Map.containsKey (c.TraitName, typeName) env.Registry.ImplTargets

                                            let instanceName, classTyArgs =
                                                if hasSpecific then
                                                    implSingletonName c.TraitName typeName, tconArgs
                                                else
                                                    // The blanket's one class type parameter is the
                                                    // implementor itself, not the head's arguments.
                                                    implSingletonName c.TraitName BlanketCtor, [ resolvedType ]

                                            { Type = dictionaryType env c.TraitName resolvedType
                                              Range = expr.Range
                                              Node = TIdent(instanceName, classTyArgs) }
                                            : TypedExpr
                                        | TVar varName ->
                                            // Forward the dictionary from our own parameters
                                            let expectedDictName = $"_dict_%s{c.TraitName}_%s{varName}"

                                            if not (Map.containsKey expectedDictName activeDicts) then
                                                failwithf
                                                    $"Missing dictionary '%s{expectedDictName}' to forward for call to '%s{calleeName}' at %s{Lexer.formatPos expr.Range}"

                                            { Type = dictionaryType env c.TraitName resolvedType
                                              Range = expr.Range
                                              Node = TIdent(expectedDictName, []) }
                                            : TypedExpr
                                        | _ ->
                                            failwithf
                                                $"Cannot resolve dictionary for type %s{DotNetInterop.showType resolvedType} at %s{Lexer.formatPos expr.Range}")

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
        | TDef(name, value, t, r) -> TDef(name, lowerExpr env Map.empty value, t, r)

        | TDefTuple(names, value, t, r) -> TDefTuple(names, lowerExpr env Map.empty value, t, r)

        | TDefMutable(name, value, t, r) -> TDefMutable(name, lowerExpr env Map.empty value, t, r)

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

                let activeDicts =
                    dictParams
                    |> List.fold (fun acc (dName, _) -> Map.add dName dName acc) Map.empty

                let loweredBody = lowerExpr env activeDicts body

                let loweredKwArgs =
                    kwArgs |> List.map (fun (n, t, e) -> n, t, lowerExpr env activeDicts e)

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

        | TImpl(traitName, kind, holeArity, targetType, assoc, methods, r) ->
            TImpl(traitName, kind, holeArity, targetType, assoc, methods |> List.map (lowerDecl env), r)

        | TModule(name, decls, r) -> TModule(name, decls |> List.map (lowerDecl env), r)

        | _ -> decl // TTrait, TImport, TExport, TType, TTypeRec, TExtern

/// Runs every post-inference lowering stage over a checked program.
let lowerProgram (env: Env) (decls: TDecl list) : TDecl list =
    decls |> List.map (DictionaryLowering.lowerDecl env)
