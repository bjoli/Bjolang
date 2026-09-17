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

module Bjolang.CheckDecl

open Bjolang.Lexer
open Bjolang.Ast
open Bjolang.TypedAST
open Bjolang.Unification
open Bjolang.TypeEnv
open Bjolang.Annotations
open Bjolang.Traits
open Bjolang.ForeignTyping
open Bjolang.InferExpr

/// Registers a `type` or `type-rec` group, and hands back the declarations as
/// everything downstream will see them: named by their keys.
///
/// A declared type's identity is the module that declared it plus its name
/// (`Naming.typeKey`), so two modules may each declare a `Banana` and mean two
/// types. Nothing else has to change to make that work — `Implementations`,
/// `Unions`, `InlineMethods` and the impl class C# is emitted as are all keyed
/// on a type's *name*, and the name is now unique.
///
/// The bare name is what source goes on writing, here and in anything that
/// imports this module plainly. It is filed as a spelling in the same table an
/// import modifier's spellings go in, which `originalName` resolves before any
/// registry is consulted.
let registerTypeDefs (isRec: bool) (typeDefs: TypeDef list) (env: Env) : Env * TypeDef list =
    let key (name: string) = Naming.typeKey env.CurrentModule name

    let withSpelling (kind: AliasKind) (name: string) (registry: TraitRegistry) =
        let keyed = key name

        if keyed = name then
            registry
        else
            { registry with
                ImportAliases =
                    Map.add
                        name
                        { OriginModule = env.CurrentModule
                          OriginalName = keyed
                          Kind = kind }
                        registry.ImportAliases }

    // The whole group's type names before any of its bodies: a `type-rec` names
    // its siblings, and a payload written with a bare name has to reach the
    // sibling's key rather than a same-named type declared elsewhere.
    let preRegistry =
        typeDefs
        |> List.fold
            (fun (reg: TraitRegistry) td ->
                withSpelling AliasType td.Name { reg with LocalTypes = Set.add (key td.Name) reg.LocalTypes })
            env.Registry

    let mutable finalRegistry = preRegistry
    let mutable finalBindings = env.Bindings
    let keyedDefs = ResizeArray<TypeDef>()

    for td in typeDefs do
        // A constructor follows its type, in expression and in pattern
        // position alike, so its bare name is a spelling in exactly the same
        // sense. Registered before the payloads are resolved, because a case
        // may carry a value of the union it belongs to.
        match td.Kind with
        | Union cases ->
            for case in cases do
                let caseName =
                    match case with
                    | SimpleCase(n, _) -> n
                    | DataCase(n, _, _, _) -> n

                finalRegistry <- withSpelling AliasConstructor caseName finalRegistry
        | _ -> ()

        let name = key td.Name

        let keyedKind =
            match td.Kind with
            | Alias ftype -> Alias(qualifyFTypeNames finalRegistry ftype)
            | Record(fields, isStruct) ->
                Record(fields |> List.map (fun f -> { f with Type = qualifyFTypeNames finalRegistry f.Type }), isStruct)
            | Union cases ->
                cases
                |> List.map (function
                    | SimpleCase(n, r) -> SimpleCase(key n, r)
                    | DataCase(n, types, marked, r) ->
                        DataCase(key n, types |> List.map (qualifyFTypeNames finalRegistry), marked, r))
                |> Union
            // Left as written. A hidden member is a name in a diagnostic and
            // nothing else, so keying it would only make the message harder to
            // read than the source the reader is looking at.
            | Opaque members -> Opaque members

        let td = { td with Name = name; Kind = keyedKind }
        keyedDefs.Add td

        let tArgs = td.TypeArgs |> List.map (fun a -> if a.StartsWith("'") then a else "'" + a)
        let hmArgs = tArgs |> List.map TVar
        let parentType = TCon(td.Name, hmArgs)

        match td.Kind with
        | Alias ftype ->
            let resolved = resolveTypeAnnotation finalRegistry ftype
            finalRegistry <- { finalRegistry with Aliases = Map.add td.Name (tArgs, resolved) finalRegistry.Aliases }
        | Record(fields, _) ->
            let resolvedFields = fields |> List.map (fun f -> f.Name, resolveTypeAnnotation finalRegistry f.Type)
            finalRegistry <- { finalRegistry with Records = Map.add td.Name (tArgs, resolvedFields) finalRegistry.Records }

            // Only for a record that has one, so that `Map.tryFind` answering
            // `None` means "nothing here is mutable" rather than "not a record".
            match fields |> List.filter (fun f -> f.Mutable) |> List.map (fun f -> f.Name) with
            | [] -> ()
            | mutableNames ->
                finalRegistry <-
                    { finalRegistry with
                        MutableRecordFields = Map.add td.Name mutableNames finalRegistry.MutableRecordFields }
            for (fName, _) in resolvedFields do
                let owners =
                    Map.tryFind fName finalRegistry.RecordFields |> Option.defaultValue []

                finalRegistry <-
                    { finalRegistry with
                        RecordFields = Map.add fName (owners @ [ td.Name ]) finalRegistry.RecordFields }
        | Union cases ->
            // Collected alongside the constructor bindings so that literal
            // elaboration can ask the inverse question the bindings cannot
            // answer: not "what is this constructor's type?" but "which case of
            // this union could carry this payload?".
            let mutable caseTable = []

            for case in cases do
                let caseName, resolvedArgs, isLiteral =
                    match case with
                    | SimpleCase(n, _) -> n, [], false
                    | DataCase(n, types, marked, _) ->
                        n, types |> List.map (resolveTypeAnnotation finalRegistry), marked
                let schemeArgs = tArgs
                let consScheme =
                    if resolvedArgs.IsEmpty then
                        Scheme(schemeArgs, [], parentType)
                    else
                        Scheme(schemeArgs, [], tfun resolvedArgs parentType)
                finalBindings <- Map.add caseName { Scheme = consScheme; IsMutable = false } finalBindings
                caseTable <- caseTable @ [ (caseName, resolvedArgs, isLiteral) ]

            finalRegistry <- { finalRegistry with Unions = Map.add td.Name (tArgs, caseTable) finalRegistry.Unions }

        // A head and nothing else, which is what an `#:opaque` export arrives
        // as. `LocalTypes` and the name's spelling were registered by the
        // pre-pass above, so the type is nameable, unifiable and a legal impl
        // target; deliberately absent are the `Records`, `Unions` and
        // constructor bindings that would let anything take it apart.
        //
        // Note what this costs the importer nothing to know: the member names
        // go into `HiddenMembers` so that a use of one reports the type it
        // belongs to rather than "no such constructor".
        | Opaque members ->
            finalRegistry <-
                { finalRegistry with
                    OpaqueTypes = Set.add td.Name finalRegistry.OpaqueTypes
                    HiddenMembers =
                        members
                        |> List.fold (fun acc m -> Map.add m td.Name acc) finalRegistry.HiddenMembers }


    { env with Registry = finalRegistry; Bindings = finalBindings }, List.ofSeq keyedDefs

/// What a `(: name ...)` said about a name, before the definition is read.
///
/// `Written` is the annotation as the source spelled it, kept beside the
/// resolved type because several checks ask about the *shape* that was written
/// — whether it had keyword or rest parameters, whether its arrow was painted
/// `-bjo->` — which the `HMType` no longer distinguishes. `None` for a name
/// whose signature was manufactured rather than written, which is `main`.
type Signature =
    { Type: HMType
      Written: FType option
      Constraints: (string * string) list }

/// The signatures a declaration group has read so far.
///
/// Threaded through `checkDecl` rather than held in `Env`, because an entry
/// lives only until the definition it describes is checked: `checkDef` and
/// `checkDefun` remove their own on the way out.
type Sigs = Map<string, Signature>

/// Checks a single declaration and ensures any errors raised during type checking
/// have a source location attached.
/// 
/// This acts as a fallback for errors that occur outside of expression inference 
/// (such as parsing signatures or checking trait implementations). If an error 
/// doesn't already have a specific source location (`needsLocation`), we attach 
/// the location of the entire declaration to provide context to the user.
let rec checkDecl (env: Env) (sigs: Sigs) (decl: Decl) : Env * Sigs * TDecl list =
    try
        checkDeclNode env sigs decl
    with ex when Diagnostics.needsLocation ex ->
        raise (Diagnostics.withLocation (declRange decl) ex)

and private checkDeclNode (env: Env) (sigs: Sigs) (decl: Decl) : Env * Sigs * TDecl list =
    // What module level looks like from inside this declaration: the imports,
    // the prelude and whatever this module has defined so far. Nothing a body
    // binds gets in, because every binder inside one goes through `addBinding`,
    // which does not touch this.
    //
    // It is what an `EResolved` resolves against — a name the compiler wrote,
    // which has to mean what it meant where it was written.
    let env =
        { env with
            Resolved = env.Bindings
            ResolvedFunMetas = env.FunMetas }

    match decl with
    | DSignature(name, ftype, constraints, _) ->
        let signature =
            { Type = resolveTypeAnnotation env.Registry ftype
              Written = Some ftype
              Constraints = constraints }

        env, Map.add name signature sigs, []

    | DDef(name, expr, r) ->
        checkDef env sigs name expr r


    | DDefDouble(name, defunArgs, syncBody, bjoBody, r) ->
        checkDefDouble env sigs name defunArgs syncBody bjoBody r


    | DDefun(name, defunArgs, body, colour, r) ->
        checkDefun env sigs decl name defunArgs body colour r


    | DDefTuple(names, expr, r) ->
        // The same level elevation as `DDef`, and for the same reason: each component
        // is generalized individually below.
        let exprType, typedExpr, elementMetas =
            atLevel (fun () ->
                let exprType, typedExpr = infer env expr
                let elementMetas = names |> List.map (fun _ -> freshMeta ())
                unify env.Registry exprType (TTuple elementMetas)
                solvePending env
                exprType, typedExpr, elementMetas)

        let newEnv =
            List.zip names elementMetas
            |> List.fold
                (fun acc (n, t) ->
                    addBinding
                        n
                        { Scheme = generalize env t
                          IsMutable = false }
                        acc)
                env

        newEnv, sigs, [ TDefTuple(names, typedExpr, exprType, r) ]

    | DDefMutable(name, expr, r) ->
        let exprType, typedExpr = infer env expr

        match Map.tryFind name sigs with
        | Some signature -> unify env.Registry exprType signature.Type
        | None -> ()

        solvePending env

        // Not generalized, for the same reason `ELetMutable` is not: a cell that
        // can be assigned has to have a settled type. At module level it is also
        // what makes the binding emittable at all — a generalized one would want
        // a static field at an open type, which C# has nowhere to declare.
        let newEnv =
            addBinding
                name
                { Scheme = Scheme([], [], exprType)
                  IsMutable = true }
                env

        newEnv, Map.remove name sigs, [ TDefMutable(name, typedExpr, exprType, r) ]

    | DModule(moduleName, decls, r) ->
        checkModule env sigs moduleName decls r


    | DImport(paths, r) -> env, sigs, [ TImport(paths, r) ]

    | DAlias(newName, oldName, r) ->
        checkAlias env sigs newName oldName r


    // A macro is checked as the `defun` it also produced. This carries no body
    // and contributes nothing to the program's runtime shape, so it stops here;
    // what an importing compilation reads is the assembly's macro list.
    // Neither survives type checking. A macro is already an ordinary `defun`
    // beside this, and `#:sync` is read off the declaration list by
    // `checkDeclGroup` before any body is looked at.
    | DMacro _
    | DPatternMacro _
    | DHashMacro _
    | DSyncOnly _ -> env, sigs, []
    | DExport(names, r) -> env, sigs, [ TExport(names, r) ]

    | DImportClass(specs, r) ->
        checkImportClass env sigs specs r


    | DImportExtern(specs, r) ->
        checkImportExtern env sigs specs r


    | DReExport(names, r) ->
        checkReExport env sigs names r

    | DType(typeDefs, r) ->
        let newEnv, keyed = registerTypeDefs false typeDefs env
        newEnv, sigs, [ TType(keyed, r) ]
    | DExtern(name, declaredOrigin, ftype, constraintPairs, r) ->
        checkExtern env sigs name declaredOrigin ftype constraintPairs r


    | DTrait(traitName, implementorVar, holeArity, assocTypes, signatures, defaults, clrSpec, r) ->
        checkTrait env sigs traitName implementorVar holeArity assocTypes signatures defaults clrSpec r

    | DTypeRec(typeDefs, r) ->
        let newEnv, keyed = registerTypeDefs true typeDefs env
        newEnv, sigs, [ TTypeRec(keyed, r) ]
    | DImpl(traitName, targetTypeExpr, assocBindings, whereClause, implMethodWheres, methods, r) ->
        checkImpl env sigs traitName targetTypeExpr assocBindings whereClause implMethodWheres methods r


    | DInlineImpl(traitName, methodName, ctor, originModule, parameters, body, qualification, r) ->
        // An inline template read back from a compiled module's metadata. Like
        // `DImplExtern` there is nothing to check and nothing to emit: the
        // landing pad is already compiled into the assembly that declared it,
        // and this is only the body to splice instead of calling it.
        let env =
            addInlineTemplate
                traitName
                methodName
                ctor
                { Params = parameters
                  Body = body
                  Qualification = Map.ofList qualification
                  OriginModule = originModule }
                env

        env, sigs, []

    | DImplExtern(traitName, targetTypeExpr, assocBindings, whereClause, r) ->
        checkImplExtern env sigs traitName targetTypeExpr assocBindings whereClause r


    // A spelling an import modifier produced for a type, a constructor, a trait
    // or a trait method. It binds nothing and emits nothing: what it does is
    // let `originalName` resolve the spelling away before any registry keyed on
    // the declaring module's own name is consulted.
    | DImportAlias(visible, original, kind, _) ->
        { env with
            Registry =
                { env.Registry with
                    ImportAliases =
                        Map.add
                            visible
                            { OriginModule = env.CurrentModule
                              OriginalName = original
                              Kind = kind }
                            env.Registry.ImportAliases } },
        sigs,
        []

/// Type-checks a group of declarations that share a signature scope: a module
/// body, or a whole program.
///
/// Signatures are collected up front so that declarations may refer to each
/// other out of order, which is why this cannot simply be a fold over
/// `checkDecl`. Signatures inherited from an enclosing group stay visible, with
/// the group's own taking precedence.

and private checkDef (env: Env) (sigs: Sigs) (name: string) (expr: Expr) (r: Range) : Env * Sigs * TDecl list =
    // A declared signature is an expected type, so it is pushed into the
    // value rather than only checked against it afterwards — a list literal
    // needs it while its elements are being inferred, not after. The
    // annotated branch of `let` does the same.
    let declaredType = Map.tryFind name sigs |> Option.map (fun s -> s.Type)

    // One level in, so that the cells born here are visibly deeper than everything
    // already in the environment — imports, prelude and the module's
    // previous declarations. `solvePending` lies inside: the cells it
    // creates belong to this right-hand side, and created at the outer level they
    // would have dragged the rest down with them.
    let exprType, typedExpr =
        atLevel (fun () ->
            let exprType, typedExpr =
                match declaredType with
                | Some sigType -> inferChecked sigType env expr
                | None -> infer env expr

            match declaredType with
            | Some sigType -> unify env.Registry exprType sigType
            | None -> ()

            // Trait obligations are discharged before generalization: a scheme must
            // not be built over a constructor that resolution would still have
            // pinned down.
            solvePending env
            exprType, typedExpr)

    // The same value restriction `let` applies. It was missing here, so a
    // module-level `(def c (make-array 1))` was generalized over the element
    // type of a cell that only ever exists once — and could then be written
    // at one type and read at another, with nothing but the code generator's
    // inability to declare a generic static field standing in the way.
    let newEnv =
        addBinding
            name
            { Scheme =
                if isSyntacticValue env.Registry typedExpr then
                    generalize env exprType
                else
                    // Monomorphic, but inferred one level in — the cells must
                    // go down again before they become part of the environment.
                    demote env.Registry exprType
                    Scheme([], [], exprType)
              IsMutable = false }
            env

    // Keyword and rest metadata travels with a `def`'s signature too, for
    // the same reason it travels with `extern`'s: `FunMeta` is what carries
    // the *shape* of a call, and the flat function type alone cannot spread
    // arguments. Without this, aliasing a variadic function bound its
    // array form and nothing else — `(def f list)` typechecked, and then
    // `(f 1 2 3)` failed on arity because `f` had no metadata to spread by.
    //
    // Only `defun`, `extern` and imported signatures used to register it,
    // so `def` was the one declaration form where a `#:rest` signature was
    // accepted and then silently meant something narrower.
    let newEnv =
        match Map.tryFind name sigs with
        | Some { Written = Some(TArrow(mandatory, keywords, restOpt, _, _, _)) } when
            restOpt.IsSome || not keywords.IsEmpty
            ->
            let funMeta =
                { MandatoryCount = mandatory.Length
                  KeywordParams =
                    keywords |> List.map (fun (n, ft) -> n, resolveTypeAnnotation env.Registry ft)
                  RestParam = restOpt |> Option.map (resolveTypeAnnotation env.Registry) }

            { newEnv with FunMetas = Map.add name funMeta newEnv.FunMetas }
        | _ -> newEnv

    newEnv, Map.remove name sigs, [ TDef(name, typedExpr, exprType, r) ]

// One name, one signature, two hand-written bodies.
//
// The split happens here rather than in the parser because the second body
// needs a signature to be checked against, and the signature is a separate
// `(: name ...)` form the parser has not read yet. Here `sigs` holds it, so
// the suspending copy can be given the *same* one — which is most of what
// this form buys: arity and type drift between two hand-written bodies is
// what rots, and neither body gets to disagree with the declaration.
and private checkDefDouble (env: Env) (sigs: Sigs) (name: string) (defunArgs: DefunArg list) (syncBody: Expr) (bjoBody: Expr) (r: Range) : Env * Sigs * TDecl list =
    let bjoName = Naming.suspendingCopy name

    match Map.tryFind name sigs with
    | None ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{name}' requires a type signature (: %s{name} ...). A defbjouble is two bodies checked against one declaration, so the declaration is the part that cannot be left out."
    | Some signature ->
        // Both halves are checked as ordinary definitions, one per colour,
        // against the one signature — which `recolour` repaints for each.
        let env, sigs, syncDecls = checkDecl env sigs (DDefun(name, defunArgs, syncBody, Ordinary, r))

        // The suspending half is checked against the signature *repainted*,
        // exactly as a generated twin is. Without it a `-?->` parameter
        // here would be emitted `Func<A,B>` while the body awaited it, and
        // the mismatch would land in generated C#.
        //
        // Re-resolved from the repainted annotation rather than patched in
        // the `HMType`, so there is one place that knows what repainting
        // means.
        let bjoSignature =
            match signature.Written with
            | Some ftype ->
                let repainted = suspendingSignature ftype

                { signature with
                    Type = resolveTypeAnnotation env.Registry repainted
                    Written = Some repainted }
            | None -> signature

        let env, sigs, bjoDecls =
            checkDecl env (Map.add bjoName bjoSignature sigs) (DDefun(bjoName, defunArgs, bjoBody, Suspending, r))

        // **Leaves only**, and the check is mechanical: the `#:bjo` body
        // has to contain a yield point the `#:sync` body does not.
        //
        // Two bodies differing only in which ordinary functions they call
        // is a `defun` — one body, and a copy generated per colour. Writing
        // it by hand instead means maintaining two things that will drift,
        // for no gain the compiler could not have given for free.
        let syncPoints = syncDecls |> List.collect yieldPointsOfDecl |> Set.ofList
        let bjoPoints = bjoDecls |> List.collect yieldPointsOfDecl |> Set.ofList
        let added = Set.difference bjoPoints syncPoints

        if Set.isEmpty added then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: the two bodies of '%s{name}' contain the same yield points, so there is nothing a defbjouble is doing here that a defun would not do better.\n  A defbjouble is for a *leaf*: a procedure whose two colours call different .NET methods, which is the one thing no inference can derive. Anything above a leaf is written once with defun, and the copy for each colour is generated.\n  If the suspending half was meant to await something, it does not yet."

        // `DoubleDefs` is "a call from a bjoroutine means the other body",
        // and that is only true when the two bodies take the *same*
        // arguments — a leaf like `read-line`, where the caller's colour is
        // the whole of the question.
        //
        // A `-?->` parameter makes it false. There the copy is chosen by
        // the callback that was handed over, not by where the call sits, so
        // `vec-map` called from a bjoroutine with an ordinary lambda still
        // means the ordinary body. Registering it here would rewrite that
        // call to a twin whose parameter is a `Func<A,Fiber<B>>` and hand
        // it a `Func<A,B>`, and it would also give every caller a copy of
        // its own for a call that never suspends.
        //
        // `wantsSuspendingCopy` selects it instead, under the same name, so
        // nothing is lost by staying out of this map.
        let declaresPoly =
            match signature.Written with
            | Some(TArrow(mandatory, keywords, restOpt, _, _, _)) ->
                mandatory @ (keywords |> List.map snd) @ (restOpt |> Option.toList)
                |> List.exists (function
                    | TApp("-?->", _, _) -> true
                    | _ -> false)
            | _ -> false

        let registry =
            if declaresPoly then
                env.Registry
            else
                { env.Registry with
                    DoubleDefs = Map.add name bjoName env.Registry.DoubleDefs }

        { env with Registry = registry }, Map.remove name sigs, syncDecls @ bjoDecls

and private checkDefun (env: Env) (sigs: Sigs) (decl: Decl) (name: string) (defunArgs: DefunArg list) (body: Expr) (colour: Colour) (r: Range) : Env * Sigs * TDecl list =
    // `defbjo` is the only thing that says a function may suspend. The
    // signature does not: `(: fetch (-> string string))` is what you write
    // for either colour, because an arrow says what a function takes and
    // returns and the definer says how it is called. So the declared type
    // arrives `ESync` and is repainted before anything is unified with it.
    let effect = colourEffect colour

    // Enforce mandatory signature for all top-level defuns except 'main'
    let sigOpt = Map.tryFind name sigs
    if name <> "main" && sigOpt.IsNone then
        failwithf $"Type Error: Function '%s{name}' requires a type signature (: %s{name} ...) at %s{Lexer.formatPos r}"

    // Extract structured keyword/rest info from the raw FType (if available)
    let mandatoryFTypes, keywordFTypes, restFTypeOpt, retFType =
        match sigOpt with
        | Some { Written = Some(TArrow(m, kw, rest, ret, _, _)) } -> m, kw, rest, Some ret
        | _ -> [], [], None, None

    // A plain `->` says nothing about the colour, so it agrees with either
    // definer and `recolour` supplies the answer — that is the normal case
    // and the one the design asks for.
    //
    // `-bjo->` does say something. It is what module metadata publishes,
    // and nothing stops a program writing one by hand, so it has to be
    // held to: a signature that claims a function suspends, over a `defun`
    // that cannot, is a contradiction rather than a decoration.
    match sigOpt with
    | Some { Written = Some(TArrow(_, _, _, _, Suspending, sr)) } when colour = Ordinary ->
        failwithf
            $"Type Error at %s{Lexer.formatPos sr}: the signature of '%s{name}' is written -bjo->, which says calling it is a yield point, but it is defined with defun. Define it with defbjo, or write the signature with ->."
    | _ -> ()

    // Whether the signature itself demanded the colour, alias and all.
    // Read from the *resolved* type rather than the annotation, which is
    // what makes `(: broken Filter)` answer the same as a written `-bjo->`.
    //
    // `main` is the exception, and has to be: it is the one definition
    // allowed no signature, and the one it is given is manufactured from
    // the definer — so reading it back would be asking the definition
    // about itself.
    let colourDeclared =
        name <> "main"
        && (match sigOpt with
            | Some { Type = TFun(_, _, declared) } -> groundEffect declared = EAsync
            | _ -> false)

    let sigHMType = sigOpt |> Option.map (fun s -> recolour effect s.Type)

    // Extract explicit trait constraints from the signature
    let explicitConstraints =
        match sigOpt with
        | Some signature ->
            signature.Constraints |> List.map (fun (traitName, varName) ->
                { TraitName = originalName env.Registry traitName; TargetType = TVar varName; Pins = [] })
        | None -> []

    // From here up to `exitLevel` below we are one level in: parameters
    // without a signature, the return type and everything the body creates are born deeper than
    // the environment, and are thus what `generalize` gets to quantify.
    //
    // `atLevel` cannot be used — the block is hundreds of lines long
    // and yields a dozen names. A thrown type error leaves
    // the counter elevated, and `Session.restore` is the one that takes it down.
    enterLevel ()

    // Match defun args with the signature types
    let mandatoryArgNames = mandatoryNames defunArgs
    let keywordArgDefs =
        defunArgs |> List.choose (function KeywordArg(n, defaultExpr) -> Some(n, defaultExpr) | _ -> None)
    let restArgName =
        defunArgs |> List.tryPick (function RestArg n -> Some n | _ -> None)

    // Resolve mandatory arg types from signature
    let mandatoryTypes =
        if mandatoryFTypes.Length > 0 then
            if mandatoryArgNames.Length <> mandatoryFTypes.Length then
                failwithf $"Type Error: Function '%s{name}' has %d{mandatoryArgNames.Length} mandatory args but signature specifies %d{mandatoryFTypes.Length} at %s{Lexer.formatPos r}"
            List.zip mandatoryArgNames (mandatoryFTypes |> List.map (resolveTypeAnnotation env.Registry))
        else
            // For main or functions without TArrow signature, use fresh metas
            mandatoryArgNames |> List.map (fun n -> n, freshMeta())

    // A type written at the parameter, `(: x int)`, has to agree with the
    // one the signature gives it. The body-local form has no signature and
    // takes its parameter types from exactly here, so the annotation means
    // the same thing in both places rather than being decoration in one.
    List.iter2
        (fun ann (_, t) ->
            match ann with
            | Some ft -> unify env.Registry t (resolveTypeAnnotation env.Registry ft)
            | None -> ())
        (defunArgs |> List.choose (function MandatoryArg(_, a) -> Some a | _ -> None))
        mandatoryTypes

    // Resolve keyword arg types from signature and type-check defaults
    let keywordTypes =
        keywordArgDefs |> List.map (fun (kwName, _defaultExpr) ->
            let kwType =
                match keywordFTypes |> List.tryFind (fun (n, _) -> n = kwName) with
                | Some (_, ft) -> resolveTypeAnnotation env.Registry ft
                | None ->
                    if sigOpt.IsSome then
                        failwithf $"Type Error: Keyword argument '#:%s{kwName}' not found in signature for '%s{name}' at %s{Lexer.formatPos r}"
                    else freshMeta()
            kwName, kwType)

    // Resolve rest arg type from signature
    let restArgType =
        match restArgName, restFTypeOpt with
        | Some _, Some ft -> Some (resolveTypeAnnotation env.Registry ft)
        | Some _, None ->
            if sigOpt.IsSome then
                failwithf $"Type Error: Function '%s{name}' has a rest arg but signature has no #:rest at %s{Lexer.formatPos r}"
            else Some (freshMeta())
        | None, _ -> None

    let expectedRetType =
        match retFType with
        | Some ft -> resolveTypeAnnotation env.Registry ft
        | None -> freshMeta()

    // Build the flat function type for unification
    let allArgTypes =
        (mandatoryTypes |> List.map snd) @
        (keywordTypes |> List.map snd) @
        (match restArgType with Some rt -> [TCon("Array", [rt])] | None -> [])
    let funType = TFun(allArgTypes, expectedRetType, effect)

    match sigHMType with
    | Some st -> unify env.Registry funType st
    | None -> ()

    // Keyword/rest metadata has to exist *before* the body is inferred, or a
    // recursive call that passes a keyword argument, or omits an optional
    // one, has no metadata to resolve against: the keyword-application rule
    // would reject it and the flat `funType` would refuse to unify with the
    // shorter argument list.
    let funMeta = {
        MandatoryCount = mandatoryTypes.Length
        KeywordParams = keywordTypes
        RestParam = restArgType
    }

    let recEnv =
        let bound =
            addBinding
                name
                { Scheme = Scheme([], [], funType)
                  IsMutable = false }
                env

        let bound =
            // Implementing a method is not shadowing it. `addBinding` drops
            // the name from `TraitMethodNames`, which is right for a program
            // that binds over a method and wrong for the `impl` that
            // supplies one: the body of `(defun (= xs ys) ...)` for lists
            // compares the elements, and that call has to dispatch.
            if bound.ImplMethod = Some name then
                { bound with TraitMethodNames = Set.add name bound.TraitMethodNames }
            else
                bound

        { bound with FunMetas = Map.add name funMeta bound.FunMetas }

    /// The colour a `-?->` parameter has *inside this body*.
    ///
    /// Whichever copy this body is. A generated pair covers both colours
    /// between them — `expandPolymorphicDefuns` builds the other one from a
    /// signature where the same arrow reads `-bjo->` — so neither has a
    /// choice left to make. The `EPoly` stays in `mandatoryTypes` and in the
    /// published scheme, which is where a caller and an importing module
    /// read that a second copy exists.
    ///
    /// It matters because `EPoly` reaching a use site becomes a fresh cell,
    /// and a cell nothing constrains is answered by *defaulting* — with the
    /// enclosing member's colour. Inside a `defbjo` that made the ordinary
    /// copy await its own `Func<A,B>` parameter, which Roslyn rejects in a
    /// file nobody wrote. The parameter's colour is not the enclosing
    /// member's business; it is decided by which copy this is.
    ///
    /// `ESync` and not the definition's own colour, which is a distinction
    /// worth stating because they look interchangeable and are not: a
    /// `defbjo` declaring a `-?->` parameter is `Suspending` and is still
    /// the *ordinary-callback* copy. What tells the two apart is the
    /// signature, not the definer — a suspending copy is checked against a
    /// repainted one where the arrow already reads `-bjo->`, so `EPoly`
    /// never reaches here for it and this case cannot fire.
    let bodyParamType (t: HMType) =
        match t with
        | TFun(args, ret, EPoly) -> TFun(args, ret, ESync)
        | other -> other

    // Bind mandatory args
    let envWithMandatory =
        mandatoryTypes
        |> List.fold
            (fun acc (n, t) ->
                addBinding n { Scheme = Scheme([], [], bodyParamType t); IsMutable = false } acc)
            recEnv

    // Bind keyword args
    let bodyEnv =
        keywordTypes
        |> List.fold
            (fun acc (n, t) ->
                addBinding n { Scheme = Scheme([], [], bodyParamType t); IsMutable = false } acc)
            envWithMandatory

    // Bind rest arg as Array type
    let bodyEnv =
        match restArgName, restArgType with
        | Some rn, Some rt ->
            addBinding rn { Scheme = Scheme([], [], TCon("Array", [ bodyParamType rt ])); IsMutable = false } bodyEnv
        | _ -> bodyEnv

    let bodyType, typedBody = infer bodyEnv body
    unify env.Registry bodyType expectedRetType

    // Type-check keyword default expressions
    let typedKeywordArgs, _ =
        List.zip keywordArgDefs keywordTypes
        |> List.fold (fun (typedArgs, currentEnv) ((kwName, defaultExpr), (_, kwType)) ->
            let defaultType, typedDefault = infer currentEnv defaultExpr
            unify env.Registry defaultType kwType
            let nextEnv = addBinding kwName { Scheme = Scheme([], [], kwType); IsMutable = false } currentEnv
            (typedArgs @ [kwName, kwType, typedDefault], nextEnv)
        ) ([], envWithMandatory)

    solvePending env

    exitLevel ()

    let scheme = generalize env funType
    let (Scheme(vars, _, schemeType)) = scheme

    // Collect trait constraints from the body and merge with explicit ones
    let inferredConstraints = collectTraitConstraints env typedBody
    let allConstraints =
        let seen = System.Collections.Generic.HashSet<string * string>()
        [ for c in explicitConstraints @ inferredConstraints do
            let key = (c.TraitName, match c.TargetType with TVar v -> v | _ -> "")
            if seen.Add(key) then yield c ]
    // A constraint may only land on a type variable this signature
    // quantifies.
    //
    // A local binding is generalized over variables of its own, and a
    // constraint collected from the body can land on one of *those*:
    // `(defun (inner y) (+ y y))` inside a generic function acquires `Num`
    // at `inner`'s variable rather than at the enclosing function's.
    // Attached to this scheme anyway it became a C# `where` clause naming a
    // type parameter the method does not declare — `CS0699`, in generated
    // code — and a published signature constraining a variable it never
    // mentions. Nothing compared the two, because nobody wrote either down.
    //
    // Checked before the declaration rule below, which would otherwise ask
    // for a clause naming that same unmentionable variable.
    for c in allConstraints do
        match c.TargetType with
        | TVar v when not (List.contains v vars) ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: '%s{name}' would carry the constraint '%s{c.TraitName}' at a type variable its signature does not mention. A local binding in the body was generalized on its own and picked the constraint up there. Annotate that local's parameter with one of this signature's variables — (defun (helper (: y %%a)) ...) — so the constraint lands where it can be declared."
        | _ -> ()

    // A CLR constraint has to be written down.
    //
    // `Eq` or `->str` at a type variable is a *capability*: an open world,
    // anyone may implement one, and inferring it from the body says no more
    // than the body already said. `Num` is not that. Nothing declared in
    // Bjolang can ever satisfy it, so the constraint is an enumeration of
    // the types .NET ships — a statement about what the type *is*, and this
    // language writes types down. Every top-level `defun` needs a signature
    // for the same reason.
    //
    // Inferred silently it also made a published contract depend on a body:
    // adding a `/` widened a signature with no diff in it, and a partial
    // `(where (Ordered %a))` was completed to include `Num` without a word.
    let undeclared =
        inferredConstraints
        |> List.filter (fun c ->
            (match Map.tryFind c.TraitName env.Registry.Traits with
             | Some info -> info.ClrConstraint.IsSome
             | None -> false)
            && not (
                explicitConstraints
                |> List.exists (fun d -> d.TraitName = c.TraitName && d.TargetType = c.TargetType)
            ))

    if not undeclared.IsEmpty then
        // The whole clause, explicit ones included, so the message is
        // something to paste rather than something to merge by hand.
        let shown (c: TraitConstraint) =
            let written =
                match prune env.Registry c.TargetType with
                | TVar v -> "%" + v.TrimStart('\'')
                | t -> DotNetInterop.showType t

            $"(%s{c.TraitName} %s{written})"

        let clause =
            (explicitConstraints @ undeclared) |> List.map shown |> String.concat " "

        let needs = undeclared |> List.map shown |> String.concat " and "

        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{name}' needs %s{needs}, which its signature does not declare. A constraint on a .NET interface says which types this *is* rather than what they can do, and nothing written in Bjolang can ever satisfy one — so it belongs in the signature. Write:\n  (: %s{name} ... (where %s{clause}))"

    let schemeWithConstraints = Scheme(vars, allConstraints, schemeType)

    let finalEnv =
        addBinding
            name
            { Scheme = schemeWithConstraints
              IsMutable = false }
            env
    let finalEnv = { finalEnv with FunMetas = Map.add name funMeta finalEnv.FunMetas }

    let finalEnv =
        if colourDeclared then
            { finalEnv with
                Registry =
                    { finalEnv.Registry with
                        ColourDeclared = Set.add name finalEnv.Registry.ColourDeclared } }
        else
            finalEnv

    let restArgInfo =
        match restArgName, restArgType with
        | Some rn, Some rt -> Some(rn, rt)
        | _ -> None

    // A `-?->` parameter whose colour the body never uses.
    //
    // The two copies would come out byte-identical. What makes them differ
    // is the *call* — one emits `f(x)`, the other `await f(x)` — so a
    // parameter that is only stored gives the second copy nothing to do.
    // Either it should be written `->`, or the body meant to use it and
    // does not.
    //
    // Generated copies only. A `defbjouble`'s `#:sync` half has the same
    // `EPoly` parameter and hands it on, and its twin is written by hand.
    if Set.contains (Naming.suspendingCopy name) env.Registry.GeneratedCopies then
        for (paramName, paramType) in mandatoryTypes do
            match prune env.Registry paramType with
            | TFun(_, _, EPoly) when not (usesColour env paramName typedBody) ->
                Diagnostics.warn
                    $"'%s{name}' declares its parameter '%s{paramName}' as -?->, which says it will be given a function of either colour — but the body neither calls it nor hands it to another -?->. Both copies of '%s{name}' would be identical. Write the parameter -> if it is an ordinary function, or use it if the body meant to.\n  at %s{Lexer.formatPos r}"
            | _ -> ()

    let decl = TDefun(name, vars, mandatoryTypes, typedKeywordArgs, restArgInfo, expectedRetType, effect, typedBody, r)
    finalEnv, Map.remove name sigs, [ decl ]

and private checkModule (env: Env) (sigs: Sigs) (moduleName: string) (decls: Decl list) (r: Range) : Env * Sigs * TDecl list =
    // Rule 5 for aliases, decided over the module as written rather than as
    // checked: an alias may appear above the definition it would collide
    // with, and by the time the fold reached it the collision would look
    // like ordinary shadowing.
    let aliasesHere =
        decls |> List.choose (function DAlias(n, _, ar) -> Some(n, ar) | _ -> None)

    if not aliasesHere.IsEmpty then
        let definedHere =
            decls
            |> List.collect (function
                | DDef(n, _, _)
                | DDefMutable(n, _, _)
                | DDefun(n, _, _, _, _)
                | DMacro(n, _)
                | DPatternMacro(n, _) -> [ n ]
                | DDefTuple(ns, _, _) -> ns
                | _ -> [])
            |> Set.ofList

        for (name, ar) in aliasesHere do
            if Set.contains name definedHere then
                failwithf
                    $"Alias Error: '%s{name}' at %s{Lexer.formatPos ar} is also defined in this module. An alias is a top-level binding, so it may not take a name the module defines for itself."

        for (name, group) in aliasesHere |> List.groupBy fst do
            if group.Length > 1 then
                let positions = group |> List.map (snd >> Lexer.formatPos) |> String.concat " and "

                failwithf
                    $"Alias Error: '%s{name}' is aliased more than once, at %s{positions}. Two aliases producing one name is an error, not a shadowing."

    let finalEnv, finalSigs, typedDecls =
        checkDeclGroup { env with CurrentModule = moduleName } sigs decls

    // Every name this module defines is also reachable as
    // `Module_Module::name`.
    //
    // That spelling is what a macro expansion uses for a binding of the
    // macro's own module: the expansion lands somewhere else, where a local
    // of the same name would otherwise take it over. `Codegen` already
    // emits `::` as a class-qualified reference and `AlphaRename` already
    // refuses to touch one, so the only thing missing was somewhere to look
    // it up.
    //
    // Restricted to what the module actually defines rather than to
    // everything in scope at its end, so `Foo_Module::print` is not a name
    // just because `Foo` imported the prelude.
    //
    // The name to look the binding up under and the name to qualify it as
    // are two things: they differ for an import brought in under a
    // modifier, where the qualified spelling has to name the member the
    // origin's class actually defines.
    let definedHere =
        decls
        |> List.collect (function
            | DDef(n, _, _)
            | DDefMutable(n, _, _) -> [ n, Naming.qualifiedBinding moduleName n ]
            | DExtern(visible, origin, _, _, _) ->
                // A facade's re-export lives in another module's class, so
                // its qualified spelling names that one.
                let m = if origin.OriginModule = "" then moduleName else origin.OriginModule
                [ visible, Naming.qualifiedBinding m origin.OriginalName ]
            | DDefun(n, _, _, _, _) -> [ n, Naming.qualifiedBinding moduleName n ]
            | DDefTuple(ns, _, _) -> ns |> List.map (fun n -> n, Naming.qualifiedBinding moduleName n)
            | _ -> [])

    let qualified =
        definedHere
        |> List.fold
            (fun acc (visible, qualifiedSpelling) ->
                match Map.tryFind visible finalEnv.Bindings with
                | Some binding -> Map.add qualifiedSpelling binding acc
                | None -> acc)
            finalEnv.Bindings

    { finalEnv with
        Bindings = qualified
        CurrentModule = env.CurrentModule },
    finalSigs,
    [ TModule(moduleName, typedDecls, r) ]

// `(:alias new old)`. A second spelling of a binding or a macro already in
// scope, sharing the original's scheme, keyword and rest metadata, and
// mutability — `set!` through one writes to the original's cell, because
// codegen resolves the alias to it rather than emitting a copy.
//
// Types, traits and constructors are refused. A type has `type` aliases of
// its own, and a constructor follows its type: neither is a binding, so
// neither could share one.
and private checkAlias (env: Env) (sigs: Sigs) (newName: string) (oldName: string) (r: Range) : Env * Sigs * TDecl list =
    let where = Lexer.formatPos r

    // Through the table first, so that aliasing a *prefixed* type or trait
    // is refused for what it is rather than reported as unbound.
    let target = originalName env.Registry oldName

    let isConstructor =
        env.Registry.Unions
        |> Map.exists (fun _ (_, cases) -> cases |> List.exists (fun (c, _, _) -> c = target))

    if Map.containsKey target env.Registry.Traits then
        failwithf
            $"Alias Error: '%s{oldName}' at %s{where} is a trait, and (:alias ...) makes a second spelling of a def or a macro. Import the module that declares it with (prefix-types ...) to change what its traits are called."

    if
        Set.contains target env.Registry.LocalTypes
        || Map.containsKey target env.Registry.Aliases
        || Map.containsKey target env.Registry.Records
    then
        failwithf
            $"Alias Error: '%s{oldName}' at %s{where} is a type, and (:alias ...) makes a second spelling of a def or a macro. Write (type (: %s{newName} %s{oldName})) for a type, or import its module with (prefix-types ...)."

    if isConstructor then
        failwithf
            $"Alias Error: '%s{oldName}' at %s{where} is a constructor, and (:alias ...) makes a second spelling of a def or a macro. A constructor follows its type: import its module with (prefix-types ...) to change what it is called."

    match Map.tryFind oldName env.Bindings with
    | Some binding ->
        // Resolved through the table, so a chain of facades flattens here
        // rather than at every use. An origin module of `""` means "look it
        // up where the whole program is known": a name defined in this
        // module, or a compiler builtin with no module class at all.
        let resolution =
            match Map.tryFind oldName env.Registry.ImportAliases with
            | Some a -> { a with Kind = AliasDef }
            | None -> { OriginModule = ""; OriginalName = oldName; Kind = AliasDef }

        // `addBinding` for the reason `DExtern` uses it — a second spelling
        // is a binder, and one that lands on a trait method's name shadows
        // it. The `FunMeta` goes on *after*, since `addBinding` drops the
        // one the new name had and this is the case that wants a new one.
        let newEnv =
            addBinding
                newName
                binding
                { env with
                    Registry =
                        { env.Registry with
                            ImportAliases = Map.add newName resolution env.Registry.ImportAliases } }

        let newEnv =
            match Map.tryFind oldName env.FunMetas with
            | Some meta -> { newEnv with FunMetas = Map.add newName meta newEnv.FunMetas }
            | None -> newEnv

        newEnv, sigs, [ TAlias(newName, Some resolution, r) ]

    // A macro is not a binding. It was registered under the new name before
    // this module was parsed — it had to be, since the parser decides what a
    // head symbol means when it meets it — so there is nothing left to do.
    | None when Macro.isMacro oldName || Macro.isPatternMacro oldName || Macro.isHashMacro oldName ->
        env, sigs, [ TAlias(newName, None, r) ]

    | None ->
        failwithf
            $"Alias Error: '%s{oldName}' is not in scope at %s{where}. (:alias ...) needs a binding or a macro to make a second spelling of."

// `(import/class (Alias (: Clr.Class type #:exceptions (E ...))) ...)`
//
// Two passes, because a constructor signature is written in terms of the
// alias it is declaring — `(-> string StreamWriter)` — so the alias has to
// be a type before that signature can be resolved.
and private checkImportClass (env: Env) (sigs: Sigs) (specs: ClassImportSpec list) (r: Range) : Env * Sigs * TDecl list =
    let baseInfos = specs |> List.map classInfoOfSpec

    // The alias becomes a type alias as well as a class: that is what lets
    // an ordinary signature say `StreamWriter` and mean
    // `System.IO.StreamWriter`, with no second spelling to keep in sync.
    let registryWithAliases =
        baseInfos
        |> List.fold
            (fun (reg: TraitRegistry) info ->
                let aliasTarget = classAliasTarget info

                { reg with
                    ClrClasses = Map.add info.Alias info reg.ClrClasses
                    Aliases = Map.add info.Alias (info.TypeParams, aliasTarget) reg.Aliases
                    // Both spellings are this module's: the alias, and the
                    // .NET name it expands to. The second is what an impl's
                    // target resolves to, and without it the orphan rule
                    // would refuse a module the right to implement a trait
                    // for a type it went to the trouble of importing.
                    LocalTypes = reg.LocalTypes |> Set.add info.Alias |> Set.add info.ClrName })
            env.Registry

    let infos =
        List.map2
            (fun (info: ClrClassInfo) (spec: ClassImportSpec) ->
                { info with
                    CtorType = spec.ConstructorType |> Option.map (resolveTypeAnnotation registryWithAliases) })
            baseInfos
            specs

    let finalRegistry =
        infos
        |> List.fold (fun (reg: TraitRegistry) info -> { reg with ClrClasses = Map.add info.Alias info reg.ClrClasses }) registryWithAliases

    { env with Registry = finalRegistry }, sigs, [ TImportClass(infos, r) ]

// `(import/extern (alias (: Clr.Type.Member type #:exceptions (E ...))) ...)`
and private checkImportExtern (env: Env) (sigs: Sigs) (specs: ExternImportSpec list) (r: Range) : Env * Sigs * TDecl list =
    // Every target this group names, so that the note below can tell an
    // oversight from a pair.
    let importedTargets = specs |> List.map (fun s -> s.ClrTarget) |> Set.ofList

    let infos =
        specs
        |> List.map (fun spec ->
            let where = Lexer.formatPos spec.Range
            let split = spec.ClrTarget.LastIndexOf "."

            let kind =
                if spec.IsGet then ExternGet
                elif spec.IsSet then ExternSet
                else ExternMethod

            if split <= 0 || split = spec.ClrTarget.Length - 1 then
                let what =
                    match kind with
                    | ExternMethod -> "a method"
                    | _ -> "a property or field"

                failwithf
                    $"Syntax error at %s{where}: '%s{spec.ClrTarget}' does not name %s{what}. Write the declaring type and the member together, as in System.Console.WriteLine."

            let typeName = spec.ClrTarget.Substring(0, split)
            let memberName = spec.ClrTarget.Substring(split + 1)
            let clrType = DotNetInterop.resolveType $" at %s{where}" typeName

            // Whether the member is static or an instance one is read off
            // the metadata rather than written in the clause. There is
            // nothing an author could add — the name denotes one or the
            // other — and an instance member simply takes its receiver as
            // the alias's first argument.
            //
            // Checked at the import rather than at the first call: an
            // import that names nothing is wrong whether or not anybody got
            // around to using it.
            let existsStatic, existsInstance =
                match kind with
                | ExternMethod ->
                    DotNetInterop.hasStaticMethod clrType memberName,
                    DotNetInterop.hasInstanceMethod clrType memberName
                | _ ->
                    DotNetInterop.hasMember true clrType memberName,
                    DotNetInterop.hasMember false clrType memberName

            // Static first, which is how C# reads `Type.Member` too. A type
            // with both under one name is vanishingly rare and the static
            // one is what the spelling says.
            let isInstance =
                if existsStatic then false
                elif existsInstance then true
                else
                    match kind with
                    | ExternMethod ->
                        failwithf
                            $"Type Error at %s{where}: '%s{clrType.FullName}' has no public method named '%s{memberName}', static or instance."
                    | ExternGet ->
                        failwithf
                            $"Type Error at %s{where}: '%s{clrType.FullName}' has no public property or field named '%s{memberName}'."
                    | ExternSet ->
                        failwithf
                            $"Type Error at %s{where}: '%s{clrType.FullName}' has no public property or field named '%s{memberName}'."

            match kind with
            | ExternGet ->
                // Readability and writability are settled here for the same
                // reason existence is: the clause is where the claim was
                // made. The resolved type is thrown away — each use
                // re-resolves it, and there is only ever one answer.
                DotNetInterop.resolveMemberRead where clrType memberName (not isInstance) |> ignore
            | ExternSet -> DotNetInterop.resolveMemberWrite where clrType memberName (not isInstance) |> ignore
            | ExternMethod ->
                checkExceptionTypes where spec.Exceptions

                // Both of these are arity-independent, so they can be
                // answered here rather than at the first call — which is the
                // point. `#:async` on a method that returns nothing
                // awaitable is a mistake about the method, and the import is
                // where the claim was made.
                if spec.IsAsync && not (DotNetInterop.hasAwaitableOverload (not isInstance) clrType memberName) then
                    failwithf
                        $"Type Error at %s{where}: '%s{clrType.FullName}.%s{memberName}' is imported #:async, but no overload of it returns a Task or a ValueTask. An ordinary method is imported without #:async and called directly."

                if spec.IsAsync
                   && not spec.Uncancellable
                   && not (DotNetInterop.hasTokenOverload (not isInstance) clrType memberName None) then
                    failwithf
                        $"Type Error at %s{where}: '%s{clrType.FullName}.%s{memberName}' has no overload taking a System.Threading.CancellationToken, so the ambient cancellation token cannot be threaded into it.\n  Write #:uncancellable here to explicitly allow this. By default, we require all imported .NET async methods to accept a CancellationToken. If a method doesn't support cancellation, it will keep running in the background even if Bjolang has aborted the operation (e.g. if a choose block timed out). Marking it with #:uncancellable makes it obvious to developers where a background resource leak might come from."

                // §7.5's lint. A synchronous method with an `…Async` sibling
                // is almost always the wrong one to have imported: the
                // sibling does not park a pool thread, and with `#:async`
                // the call site reads identically. Said rather than
                // enforced, because there are real reasons to want the
                // synchronous one — a startup path with no fiber in sight,
                // say — and being told the name is enough to make the choice
                // deliberate.
                let hasSibling =
                    if isInstance then
                        DotNetInterop.hasInstanceMethod clrType (memberName + "Async")
                    else
                        DotNetInterop.hasStaticMethod clrType (memberName + "Async")

                // Unless the sibling is imported here too, which is what the
                // `#:sync` half of a `defbjouble` looks like: the pair is
                // the point, and the advice has already been taken two
                // clauses down. Telling someone to do the thing they have
                // visibly done is how a note stops being read.
                let siblingImported = Set.contains (spec.ClrTarget + "Async") importedTargets

                if not spec.IsAsync && hasSibling && not siblingImported then
                    Diagnostics.progress
                        $"Note at %s{where}: '%s{clrType.FullName}.%s{memberName}' has an async sibling, '%s{memberName}Async'. The synchronous one parks a thread; importing the sibling with #:async does not, and the call site reads the same either way (§7.5)."

                if spec.Uncancellable && not (spec.IsAsync || spec.Cancellable) then
                    failwithf
                        $"Syntax error at %s{where}: #:uncancellable says not to thread the ambient cancellation token into this call, but without #:async or #:cancellable there is no token to thread and nothing to cancel."

                if spec.Cancellable && spec.IsAsync then
                    failwithf
                        $"Syntax error at %s{where}: #:cancellable is what #:async already does — the ambient token is threaded into every #:async call that has an overload to take it. Write one or the other."

                // The two say opposite things about the same call. An
                // `#:async` import compiles to an await, which is a fiber
                // giving its thread back; `#:blocking` says the thread is
                // held. A call does one or the other.
                if spec.IsBlocking && spec.IsAsync then
                    failwithf
                        $"Syntax error at %s{where}: '%s{clrType.FullName}.%s{memberName}' is imported both #:async and #:blocking, and those are opposites. An #:async call compiles to an await and hands its thread back; a #:blocking one holds it. Write whichever the method actually does."

                if spec.Cancellable && not (DotNetInterop.hasTokenOverload (not isInstance) clrType memberName None) then
                    failwithf
                        $"Type Error at %s{where}: '%s{clrType.FullName}.%s{memberName}' is imported #:cancellable, but no overload of it takes a System.Threading.CancellationToken. There is nothing to thread."

            let declaredType =
                spec.ExplicitType |> Option.map (resolveTypeAnnotation env.Registry)

            // A member whose overloads are *all* generic definitions is
            // resolved here, once, against the signature — and a member with
            // even one ordinary overload keeps being resolved per call site
            // from its argument types, exactly as before. Which of the two a
            // name gets is a property of the .NET method group rather than
            // of anything written in the clause.
            let genericTypeArgs, declaredType =
                if kind <> ExternMethod
                   || not (DotNetInterop.isGenericOnlyMethod (not isInstance) clrType memberName) then
                    None, declaredType
                else
                    if spec.IsAsync || spec.Cancellable then
                        failwithf
                            $"Type Error at %s{where}: '%s{clrType.FullName}.%s{memberName}' is generic, and #:async and #:cancellable are not supported for a generic method yet. Its type arguments are solved from the declared signature, which has no room to say what a threaded token or an unwrapped task does to them."

                    match declaredType with
                    | Some(TFun(declaredParams, declaredReturn, _)) ->
                        // The receiver is the alias's first parameter and
                        // none of the method's, so it comes off before
                        // reflection sees the signature.
                        let methodParams =
                            if not isInstance then
                                declaredParams
                            else
                                match declaredParams with
                                | _ :: rest -> rest
                                | [] ->
                                    failwithf
                                        $"Type Error at %s{where}: '%s{spec.Alias}' names the instance method '%s{clrType.FullName}.%s{memberName}', whose receiver is its first argument, but its declared type takes none."

                        let resolved =
                            DotNetInterop.resolveGenericMethod
                                where
                                (not isInstance)
                                clrType
                                memberName
                                methodParams
                                declaredReturn

                        // A method that answers nothing keeps the *interop*
                        // void as its type, not the unit the signature spells
                        // it with. The two are the same thing to a reader and
                        // not to the emitter: a void call is a statement, and
                        // a unit is a value C# would have to produce.
                        let normalized =
                            if resolved.ReturnType = declaredReturn then
                                declaredType
                            else
                                Some(TFun(declaredParams, resolved.ReturnType, ESync))

                        Some resolved.TypeArguments, normalized
                    | Some _ ->
                        failwithf
                            $"Type Error at %s{where}: '%s{spec.Alias}' names a method, so its declared type has to be a function type."
                    | None ->
                        failwithf
                            $"Type Error at %s{where}: '%s{clrType.FullName}.%s{memberName}' is generic, so this import needs a declared signature — that is where its type arguments come from. Write one, as in (: %s{spec.ClrTarget} (-> (Set %%a) %%a (Set %%a)))."

            { Alias = spec.Alias
              ClrType = clrType.FullName
              MemberName = memberName
              Kind = kind
              IsInstance = isInstance
              DeclaredType = declaredType
              GenericTypeArgs = genericTypeArgs
              Exceptions = spec.Exceptions
              IsAsync = spec.IsAsync
              Uncancellable = spec.Uncancellable
              Cancellable = spec.Cancellable
              IsBlocking = spec.IsBlocking })

    let newRegistry =
        infos
        |> List.fold (fun (reg: TraitRegistry) info -> { reg with ClrExterns = Map.add info.Alias info reg.ClrExterns }) env.Registry

    { env with Registry = newRegistry }, sigs, [ TImportExtern(infos, r) ]

and private checkReExport (env: Env) (sigs: Sigs) (names: string list) (r: Range) : Env * Sigs * TDecl list =
    // A re-exported name was defined elsewhere and already carries a
    // signature from there, so the local-signature rule `export` enforces
    // cannot apply. What can be checked is that the name is actually in
    // scope here — otherwise the module would advertise something it does
    // not have.
    //
    // A *type* is here too, and it is a different thing from a binding in
    // every respect but the one that matters: it is in scope, and the
    // module wants its importers to be able to write the name. It has no
    // signature to carry and is not in `Bindings` at all, so what crosses
    // is the spelling together with the declaration it stands for — under
    // the key its own module gave it, so that there is one type and not
    // two. See `ModuleMetadata.ReExportedType`, which is where the
    // reasoning about the key lives.
    //
    // Exporting a union exports its cases, and re-exporting one does too:
    // a union that arrives without its cases is a type nothing can take
    // apart, and the cases travel inside the declaration rather than as
    // eleven more names to have written.
    let where = Lexer.formatPos r

    for name in names do
        if not (Map.containsKey name env.Bindings) then
            let keyed = originalName env.Registry name

            if Set.contains keyed env.Registry.LocalTypes then
                // A type this module declared is published by `export`,
                // which publishes the declaration. Re-exporting one would
                // be the same declaration written twice under one key.
                if keyed = Naming.typeKey env.CurrentModule name then
                    failwithf
                        "Re-export Error: '%s' at %s is a type this module declares, and (re-export ...) publishes a name this module imported. Write (export %s)."
                        name
                        where
                        name
            elif Map.containsKey name env.Registry.Traits then
                failwithf
                    "Re-export Error: '%s' at %s is a trait. A trait travels with its methods, so re-export one of them and the whole trait crosses with it."
                    name
                    where
            elif Map.containsKey name env.Registry.ClrClasses then
                failwithf
                    "Re-export Error: '%s' at %s is an (import/class ...) alias of this module's, and an alias is a name for a .NET class rather than something imported. Write (export %s), or import the class here as well."
                    name
                    where
                    name
            else
                failwithf
                    "Re-export Error: '%s' is not in scope at %s. A re-exported name must be a binding or a type this module imported."
                    name
                    where

    env, sigs, [ TReExport(names, r) ]

and private checkExtern (env: Env) (sigs: Sigs) (name: string) (declaredOrigin: ImportAlias) (ftype: FType) (constraintPairs: (string * string) list) (r: Range) : Env * Sigs * TDecl list =
    // An unfilled origin module means "the module this declaration is in",
    // which is only knowable here. A filled one is a facade's: the module
    // publishing the name generated no code for it.
    let origin =
        if declaredOrigin.OriginModule = "" then
            { declaredOrigin with OriginModule = env.CurrentModule }
        else
            declaredOrigin

    let t = resolveTypeAnnotation env.Registry ftype
    let scheme = generalize env t
    let (Scheme(vars, _, schemeType)) = scheme
    // Add constraints from DLL metadata
    let constraints = 
        constraintPairs |> List.map (fun (traitName, varName) ->
            { TraitName = originalName env.Registry traitName; TargetType = TVar varName; Pins = [] })
    let schemeWithConstraints = Scheme(vars, constraints, schemeType)
    // The same hazard as a top-level definition over a method, arriving by
    // a different route and with nothing in this file to point at — so the
    // module it came from is the location, which is where the fix is.
    match Map.tryFind name env.Registry.TraitMethods with
    | Some traitName when Set.contains name env.TraitMethodNames ->
        Diagnostics.warn
            $"'%s{name}' is imported from '%s{Naming.moduleNameOfPath origin.OriginModule}' and is a method of the trait '%s{traitName}', so the import binds over it. A call to '%s{name}' in this module reaches the imported binding rather than dispatching. Import that module with (except ... %s{name}) or (rename ... (%s{name} another-name)) if that is not what you meant."
    | _ -> ()

    // Through `addBinding`, because an import is a binder like any other.
    // Writing `Bindings` directly left the name in `TraitMethodNames`, so a
    // module that bound over `sign` and exported it published a binding the
    // importer resolved and then never called: every `(sign x)` over there
    // went on dispatching `Num`. That is the bug shadowing was supposed to
    // have fixed, surviving across a module boundary.
    let newEnv = addBinding name { Scheme = schemeWithConstraints; IsMutable = false } env

    // Every imported binding gets a table entry, whether or not a modifier
    // renamed it. The degenerate one carries no new spelling but does carry
    // the module the name came from, which is what an `(:alias ...)` of it
    // needs to resolve to a qualified reference — and what makes a facade
    // of a facade flatten, since the entry already holds the ultimate
    // origin rather than the module it was read from.
    let newEnv =
        { newEnv with
            Registry =
                { newEnv.Registry with
                    ImportAliases = Map.add name origin newEnv.Registry.ImportAliases } }

    // Keyword and rest metadata travels with an imported signature too.
    // Without it a call that passes a keyword argument, or omits an optional
    // one, has nothing to resolve against, and the flat function type
    // refuses to unify with the shorter argument list the caller wrote.
    let newEnv =
        match ftype with
        | TArrow(mandatory, keywords, restOpt, _, _, _) ->
            let funMeta =
                { MandatoryCount = mandatory.Length
                  KeywordParams =
                    keywords |> List.map (fun (n, ft) -> n, resolveTypeAnnotation env.Registry ft)
                  RestParam = restOpt |> Option.map (resolveTypeAnnotation env.Registry) }

            { newEnv with FunMetas = Map.add name funMeta newEnv.FunMetas }
        | _ -> newEnv

    newEnv, sigs, [ TExtern(name, origin, ftype, r) ]

and private checkTrait (env: Env) (sigs: Sigs) (traitName: string) (implementorVar: string) (holeArity: int) (assocTypes: string list) (signatures: (string * FType * MemberConstraint list) list) (defaults: Decl list) (clrSpec: (string * FType list * (string * string) list) option) (r: Range) : Env * Sigs * TDecl list =
    // The member constraints ride the signature list; split them off here
    // so the many readers of `(name, type)` pairs below keep their shape.
    let memberWheres: Map<string, Ast.MemberConstraint list> =
        signatures
        |> List.choose (fun (name, _, cs) -> if List.isEmpty cs then None else Some(name, cs))
        |> Map.ofList

    let signatures = signatures |> List.map (fun (name, fType, _) -> name, fType)
    // The kind is derived, not declared: an implementor written applied to
    // arguments cannot be an interface, because there is no C# interface
    // that abstracts over a type constructor.
    let kind = if holeArity > 0 then InlineTrait else InterfaceTrait

    // A trait that stands for a .NET interface. Everything about it is
    // checked here rather than at a use site: the interface is named in
    // this declaration, so this is the only place a diagnostic can point at
    // where the name was written. See `Docs/Numerics.org`.
    let clrConstraint =
        clrSpec
        |> Option.map (fun (ifaceName, argExprs, memberSpecs) ->
            let args = argExprs |> List.map (resolveTypeAnnotation env.Registry)

            if holeArity > 0 then
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' applies its implementor, so it is inline-only and cannot stand for a .NET interface. A C# interface cannot abstract over a type constructor, which is the same reason the trait is inline-only."

            if not assocTypes.IsEmpty then
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' stands for a .NET interface and cannot declare associated types. There is no implementation to bind one in — the interface is the implementation."

            if not defaults.IsEmpty then
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' stands for a .NET interface and cannot give default method bodies. There is no implementation for one to land in."

            // The constraint has to *say* something about the implementor,
            // or a `(where ...)` on it constrains nothing and the C# clause
            // would name a type parameter the method does not have.
            let implVar = "'" + implementorVar

            if not (args |> List.exists (fun a -> freeTVars env.Registry a |> List.contains implVar)) then
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' stands for '%s{ifaceName}' but never applies it to %%%s{implementorVar}, so a constraint on it would say nothing about the implementor. Write (#:clr-constraint (%s{ifaceName} %%%s{implementorVar}))."

            let iface =
                match DotNetInterop.tryResolveGenericInterface ifaceName args.Length with
                | Some t -> t
                | None ->
                    let applied =
                        if args.IsEmpty then ifaceName else $"%s{ifaceName} at %d{args.Length} type argument(s)"

                    failwithf
                        $"Interop Error at %s{Lexer.formatPos r}: trait '%s{traitName}' stands for '%s{applied}', which is not a .NET interface this compiler can find. Names must be fully qualified, as in System.Numerics.INumber, and the number of arguments has to be the number the interface declares."

            // Every method must say which member it is. There is no
            // implementation to fall back on and no default body to inherit,
            // so a method without one names nothing at all.
            let memberMap = Map.ofList memberSpecs

            for (mName, _) in signatures do
                if not (Map.containsKey mName memberMap) then
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: '%s{mName}' is a method of '%s{traitName}', which stands for the .NET interface '%s{ifaceName}', so it has to say which member of it to call. Write (: %s{mName} ... #:clr-member SomeMember)."

            let declared = signatures |> List.map fst |> Set.ofList

            for (mName, _) in memberSpecs do
                if not (Set.contains mName declared) then
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' binds a #:clr-member for '%s{mName}', which it does not declare. Add (: %s{mName} ...) to the trait, or remove the binding."

            // Resolved against the interface here, where the diagnostic can
            // point at the declaration. Whether the member is static is
            // read rather than written: the metadata already knows.
            let members =
                memberSpecs
                |> List.map (fun (mName, memberName) ->
                    match DotNetInterop.tryFindInterfaceMember iface memberName with
                    | Some kind ->
                        mName,
                        { MemberName = memberName
                          IsStatic = (kind = DotNetInterop.StaticMember) }
                    | None ->
                        let available =
                            DotNetInterop.interfaceMemberNames iface |> String.concat ", "

                        failwithf
                            $"Interop Error at %s{Lexer.formatPos r}: '%s{ifaceName}' has no member '%s{memberName}', named by '%s{mName}' in trait '%s{traitName}'. It offers: %s{available}.")
                |> Map.ofList

            { InterfaceName = ifaceName
              Args = args
              Members = members })

    for (name, fType) in signatures do
        match fType with
        | TArrow(mandatory, keywords, restOpt, _, colour, ar) when
            mandatory @ (keywords |> List.map snd) @ (restOpt |> Option.toList)
            |> List.exists (function
                | TApp("-?->", _, _) -> true
                | _ -> false)
            ->
            // A method declared `-?->` at a parameter may not also declare
            // a colour of its own. The twin derived below is what answers
            // the suspending case, and it is derived rather than written.
            if colour = Suspending then
                failwithf
                    $"Type Error at %s{Lexer.formatPos ar}: trait '%s{traitName}' declares '%s{name}' -bjo-> and gives it a -?-> parameter. A -?-> already says calling it suspends whenever the callback does, so the two together leave the callback no say. Write the method's own arrow ->."

            // A trait standing for a .NET interface has one member per
            // method and no second one to derive a twin into.
            if clrConstraint.IsSome then
                failwithf
                    $"Type Error at %s{Lexer.formatPos ar}: trait '%s{traitName}' stands for a .NET interface, so '%s{name}' is the member it names and there is no second member for a -?-> to be answered by. Declare the parameter ->."
        | _ -> ()

    let hmSignatures =
        match kind with
        | InterfaceTrait ->
            signatures
            |> List.map (fun (name, fType) -> name, resolveTypeAnnotation env.Registry fType)
            |> Map.ofList
        | InlineTrait -> Map.empty

    let templates =
        match kind with
        | InterfaceTrait -> Map.empty
        | InlineTrait ->
            signatures
            |> List.map (fun (name, fType) -> name, resolveTemplate env.Registry implementorVar fType)
            |> Map.ofList

    // A method taking a callback of either colour gets a second one,
    // `name__bjo`, taking the suspending callback and suspending with it.
    //
    // Derived rather than declared, and merged into the same map, so that
    // everything downstream finds two methods where the author wrote one:
    // the interface gets both slots, `DImpl` checks both bodies against
    // them, and `EffectGraph` picks between them with the name rewriting it
    // already does for a top-level `-?->`.
    //
    // Not published. `Exports` leaves a twin out and an importing module
    // derives its own from the `-?->` it reads back, so the rule lives in
    // one place and metadata never says the same thing twice.
    let hmSignatures =
        hmSignatures
        |> Map.fold
            (fun acc name t ->
                if declaresPolyParam t then
                    Map.add (Naming.suspendingCopy name) (suspendingTwin t) acc
                else
                    acc)
            hmSignatures

    let templates =
        templates
        |> Map.fold
            (fun acc name tpl ->
                if templateDeclaresPolyParam tpl then
                    Map.add (Naming.suspendingCopy name) (suspendingTwinTemplate tpl) acc
                else
                    acc)
            templates

    if kind = InlineTrait && not assocTypes.IsEmpty then
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' applies its implementor, so it is inline-only and cannot declare associated types. An inline trait's methods may be generic in their own right instead."

    // A default body is not checked here — there is nothing to check it
    // against. Its type comes from the impl it is spliced into, and until
    // there is one the implementor is an abstract variable that no `.NET`
    // overload, record field or numeric literal could be resolved at.
    //
    // What *is* checked is that it stands for a method this trait declares.
    // A defaulted name with no signature would otherwise sit in the trait
    // being silently ignored by every impl, since only declared methods are
    // ever looked up.
    let declaredMethods = signatures |> List.map fst |> Set.ofList

    let defaultBodies =
        defaults
        |> List.map (fun d ->
            match d with
            | DDefun(name, _, _, _, dr) ->
                if not (Set.contains name declaredMethods) then
                    failwithf
                        $"Type Error at %s{Lexer.formatPos dr}: trait '%s{traitName}' gives a default body for '%s{name}', which it does not declare. Add (: %s{name} ...) to the trait, or remove the body."

                name, d
            | _ ->
                failwithf
                    $"Syntax error at %s{Lexer.formatPos r}: only 'defun' declarations may appear in trait '%s{traitName}'.")

    for (name, _) in defaultBodies |> List.countBy fst |> List.filter (fun (_, n) -> n > 1) do
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' gives more than one default body for '%s{name}'."

    // ---- Member-level `(where ...)` clauses -----------------------------
    //
    // Validated once, here, against the trait declaration, and resolved
    // into the trait's own variable space: the constrained variable stays
    // a `TVar`, and a pin's type may name the trait's associated types as
    // bare variables, resolved to projections at each use the way the
    // signatures themselves are. `traitCallType` instantiates these per
    // call; the impl checker grants them as given equalities per body.
    //
    // The messages here are the member-level ones. The impl-level
    // restriction — a constraint with associated types on an *impl* —
    // stays refused with its own message: an impl is one class, and its
    // constraints' associated types would have to be class parameters
    // fixed before any call site exists. A method has no such problem: a
    // generic method takes whatever each call site brings.
    let resolvedMemberWheres: Map<string, TraitConstraint list> =
        let resolved =
            memberWheres
            |> Map.map (fun methodName cs ->
                if kind <> InterfaceTrait then
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: '%s{traitName}' is an inline trait, and only an interface trait's member may carry a where clause — an inline method has no dictionary slot to receive the evidence."

                let sigVars =
                    match Map.tryFind methodName hmSignatures with
                    | Some t -> freeTVars env.Registry t |> Set.ofList
                    | None -> Set.empty

                cs
                |> List.map (fun (mc: Ast.MemberConstraint) ->
                    let cTrait = originalName env.Registry mc.MCTrait
                    let cr = mc.MCRange

                    // Rule 1: a member's where constrains variables of its
                    // own signature, and never the implementor. What the
                    // implementor must satisfy is an impl-level matter.
                    if mc.MCVar = "'" + implementorVar then
                        failwithf
                            $"Type Error at %s{Lexer.formatPos cr}: the where clause of '%s{methodName}' constrains '%%%s{implementorVar}', the trait's own implementor. A member's where may only constrain the member's own type variables; what the implementor satisfies is written on each impl."

                    if not (Set.contains mc.MCVar sigVars) then
                        let written = "%" + mc.MCVar.TrimStart('\'')

                        failwithf
                            $"Type Error at %s{Lexer.formatPos cr}: the where clause of '%s{methodName}' constrains '%s{written}', which its signature does not mention. A member may only constrain its own type variables."

                    let cInfo =
                        match Map.tryFind cTrait env.Registry.Traits with
                        | Some i -> i
                        | None ->
                            failwithf
                                $"Unknown trait '%s{cTrait}' in the where clause of '%s{methodName}' at %s{Lexer.formatPos cr}"

                    if cInfo.Kind = InlineTrait then
                        failwithf
                            $"Type Error at %s{Lexer.formatPos cr}: '%s{cTrait}' is an inline-only trait, so it cannot appear in a member's where clause. There is no dictionary for the call site to pass."

                    // The colour decision, made rather than left: a
                    // constraint whose methods suspend would make this
                    // member a yield point its own arrow never declares.
                    // Refused, so the wrong program is an error instead.
                    let coloured =
                        cInfo.Signatures
                        |> Map.toList
                        |> List.filter (fun (n, _) -> not (Naming.isSuspendingCopy n))
                        |> List.tryPick (fun (n, t) ->
                            match t with
                            | TFun(_, _, EAsync) -> Some n
                            | _ -> None)

                    match coloured with
                    | Some n ->
                        failwithf
                            $"Type Error at %s{Lexer.formatPos cr}: '%s{cTrait}' declares '%s{n}' with -bjo->, so calling it through this constraint is a yield point — one '%s{methodName}''s own arrow does not declare. A member's where clause may only name traits whose methods are ordinary."
                    | None -> ()

                    // Rule 2: every associated type pinned, by name, the
                    // way a dyn type pins them — one rule shared between
                    // the two is worth more than a laxer one that differs.
                    for (pinName, _) in mc.MCPins do
                        if not (List.contains pinName cInfo.AssociatedTypes) then
                            let listed =
                                match cInfo.AssociatedTypes with
                                | [] -> "it has none"
                                | names -> "it has " + (names |> List.map (fun n -> "#:" + n) |> String.concat ", ")

                            failwithf
                                $"Type Error at %s{Lexer.formatPos cr}: '%s{cTrait}' has no associated type #:%s{pinName} — %s{listed}."

                    for (pinName, count) in mc.MCPins |> List.countBy fst |> List.filter (fun (_, n) -> n > 1) do
                        ignore count

                        failwithf
                            $"Type Error at %s{Lexer.formatPos cr}: #:%s{pinName} is pinned more than once in '%s{methodName}''s where clause."

                    for assocName in cInfo.AssociatedTypes do
                        if not (mc.MCPins |> List.exists (fun (n, _) -> n = assocName)) then
                            failwithf
                                $"Type Error at %s{Lexer.formatPos cr}: the associated type #:%s{assocName} of '%s{cTrait}' is not pinned in '%s{methodName}''s where clause. A member's constraint pins every one of them, the way a dyn type does — pin it to a fresh variable, #:%s{assocName} %%some-var, to say it does not matter."

                    { TraitName = cTrait
                      TargetType = TVar mc.MCVar
                      Pins = mc.MCPins |> List.map (fun (n, t) -> n, resolveTypeAnnotation env.Registry t) }))

        // A member with a `-?->` parameter has a derived suspending twin;
        // the twin inherits the wheres, being the same source.
        resolved
        |> Map.toList
        |> List.collect (fun (name, cs) ->
            let twin = Naming.suspendingCopy name

            if Map.containsKey twin hmSignatures then
                [ name, cs; twin, cs ]
            else
                [ name, cs ])
        |> Map.ofList

    let traitInfo =
        { ImplementorVar = implementorVar
          AssociatedTypes = assocTypes
          Signatures = hmSignatures
          Kind = kind
          HoleArity = holeArity
          Templates = templates
          Defaults = Map.ofList defaultBodies
          ClrConstraint = clrConstraint
          MemberWheres = resolvedMemberWheres
          // Derived from signatures and computed on declaration: an
          // imported trait reconstructs its dyn-safety verdict without
          // serializing it to metadata.
          DynSafe = dynSafety traitName implementorVar kind clrConstraint hmSignatures resolvedMemberWheres }

    let newEnv = addTrait traitName traitInfo env
    let newEnv = registerDynImpl traitName traitInfo newEnv

    // Whatever the kind, the method names are recorded so that `infer` can
    // recognize them in application position without searching every trait.
    // Read off the maps rather than the source, so a derived twin is a
    // method like any other.
    let methodNames =
        match kind with
        | InterfaceTrait -> hmSignatures |> Map.toList |> List.map fst
        | InlineTrait -> templates |> Map.toList |> List.map fst

    // A method name identifies its trait, and that is the *only* thing that
    // can: nothing at a call site says which trait `pure` came from. Two
    // traits claiming one name is therefore not ambiguity to be resolved
    // later but a program with no meaning, and it has to be rejected here
    // rather than silently dispatched to whichever was registered last.
    for m in methodNames do
        match Map.tryFind m newEnv.Registry.TraitMethods with
        | Some owner when owner <> traitName ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' declares a method '%s{m}', but '%s{owner}' already does. A call site says nothing about which trait a method name belongs to, so the two are indistinguishable. Rename one of them."
        | _ -> ()

    let newEnv =
        { newEnv with
            Registry =
                { newEnv.Registry with
                    TraitMethods =
                        methodNames
                        |> List.fold (fun acc m -> Map.add m traitName acc) newEnv.Registry.TraitMethods } }

    let assocSubst = 
        assocTypes 
        |> List.map (fun assocName -> 
            "'" + assocName, TAssoc(traitName, assocName, TVar ("'" + implementorVar)))
        |> Map.ofList

    // An inline trait's methods are deliberately *not* bound into
    // `env.Bindings`. There is no single scheme they could be bound under —
    // `m` appears applied to two different arguments in `bind` — and a
    // weaker stand-in would be worse than nothing.
    let mutable finalEnv = newEnv

    if kind = InterfaceTrait then
        for kvp in hmSignatures do
            let methodTypeWithAssoc = substTypeVars assocSubst kvp.Value
            // Collect ALL free type variables from the method signature.
            // The implementor var is always first; any additional vars (like 'acc)
            // are method-level generics that must also be quantified.
            let methodVars = freeTVars env.Registry methodTypeWithAssoc |> List.distinct
            let implVar = "'" + implementorVar
            let allVars = implVar :: (methodVars |> List.filter ((<>) implVar))
            let scheme = Scheme(allVars, [], methodTypeWithAssoc)
            finalEnv <- addBinding kvp.Key { Scheme = scheme; IsMutable = false } finalEnv

    // Last, because `addBinding` above cleared each name as it bound it.
    // Declaring a trait is the one thing that makes a name mean "dispatch on
    // the trait", and it is done here rather than beside each binding
    // because an *inline* trait's methods are never bound at all.
    //
    // A name may be a prelude function and a trait method both — `wrap` is,
    // being a CML combinator and the method of a `Wrapper` declared in a
    // module of its own. The declaration wins there, which is what
    // shadowing means.
    finalEnv <-
        { finalEnv with
            TraitMethodNames =
                methodNames |> List.fold (fun acc m -> Set.add m acc) finalEnv.TraitMethodNames }

    finalEnv, sigs, [ TTrait(traitName, implementorVar, kind, holeArity, assocTypes, hmSignatures, r) ]

and private checkImpl (env: Env) (sigs: Sigs) (traitName: string) (targetTypeExpr: FType) (assocBindings: (string * FType) list) (whereClause: (string * string) list) (implMethodWheres: (string * MemberConstraint list) list) (methods: Decl list) (r: Range) : Env * Sigs * TDecl list =
    // The trait may be written under a spelling a `prefix` produced; the
    // registries are keyed on the name the `def/trait` gave it.
    let traitName = originalName env.Registry traitName
    let whereClause = whereClause |> List.map (fun (t, v) -> originalName env.Registry t, v)
    let targetType = resolveTypeAnnotation env.Registry targetTypeExpr

    let typeKey =
        match implCtorKey targetType with
        | Some k -> k
        | None -> failwithf $"Trait implementations require concrete target types at %s{Lexer.formatPos r}"

    let isLocalTrait = env.Registry.IsTraitDefinedLocally(traitName)

    // A tuple belongs to no module, exactly as `List` and `Option` do, so
    // the "or the module defining the type" half of the orphan rule has
    // nothing to hold it to.
    let isLocalType =
        typeKey <> BlanketCtor
        && (isTupleCtor typeKey || env.Registry.IsTypeDefinedLocally(typeKey))

    // The orphan rule, and it is what keeps the blanket fallback from being
    // a source of action at a distance. Once impls can overlap, adding one
    // in a third module could change which impl a call in an unrelated
    // module selects. Restricting an impl to the module defining the trait
    // or the module defining the head constructor removes the possibility:
    // any module that can *mention* both already depends on both, so it
    // recompiles.
    //
    // A blanket has no head constructor at all, so only the trait's own
    // module may write one — which is exactly right. A blanket declared
    // elsewhere would change the meaning of every call to that trait in
    // every module that never heard of it.
    // A blanket is held to the stricter half of the rule on its own, and
    // against the module the trait was actually declared in rather than
    // against `LocalTraits` — which by this point also holds every imported
    // trait. There is no second escape hatch for it: a blanket has no head
    // constructor, so "or the module defining the type" has nothing to say.
    if typeKey = BlanketCtor then
        let declaredHere =
            Map.tryFind traitName env.Registry.TraitOrigins = Some env.CurrentModule

        if not declaredHere then
            failwithf
                $"Orphan Rule Violation at %s{Lexer.formatPos r}: a blanket implementation of '%s{traitName}' may only be written in the module that defines the trait. A blanket applies at every type that has no implementation of its own, so declaring one here would change what '%s{traitName}' means for modules that do not import this one."
    elif not (isLocalTrait || isLocalType) then
        failwithf
            $"Orphan Rule Violation at %s{Lexer.formatPos r}: Cannot implement foreign trait '%s{traitName}' for foreign type '%s{typeKey}'."

    // An `Eq` or `Ord` implementation for a type this module did not
    // declare: an `import/class` alias, or a type imported from another
    // module (in the REPL, from an earlier entry).
    //
    // Both pass the orphan rule, and both would work for `(= a b)` — but
    // not for `Map`, `Set` or the ordered collections, which ask the
    // *type* for its equality and ordering. Materialization can only put
    // the implementation there at the moment the type's class is emitted,
    // which is in the module that declares it; anywhere else the two
    // answers silently diverge, which is the exact hole materialization
    // exists to close. Refused, with the fix named.
    //
    // The standard library is carved out: `std/eq` and `std/clr-ord`
    // implement these traits for .NET and runtime types as delegations to
    // the very members .NET consults — `.CompareTo`,
    // `EqualityComparer.Default` — so their two answers cannot part ways.
    // No compiler check could establish that of an arbitrary body, so the
    // privilege stops at `lib/std`.
    if (traitName = "Eq" || traitName = "Ord")
       && typeKey <> BlanketCtor
       && not (isTupleCtor typeKey)
       && not (Naming.isStdModuleKey env.CurrentModule) then
        if not (Naming.isModuleKey typeKey) then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: '%s{Naming.showTypeName typeKey}' is a .NET type, so '%s{traitName}' cannot be implemented for it. Its equality and ordering are compiled into it already — this implementation would answer '=' and 'compare', but Map, Set and the ordered collections ask the type itself, and the two would silently disagree. Declare a wrapper type with (type ...) and implement '%s{traitName}' for that."
        // `typeKey` is idempotent on a key this module built, so equality
        // is the "declared here" test.
        elif Naming.typeKey env.CurrentModule typeKey <> typeKey then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: '%s{Naming.showTypeName typeKey}' is declared in another module, so '%s{traitName}' cannot be implemented for it here. Its Equals, GetHashCode and CompareTo were compiled with that module — this implementation would answer '=' and 'compare', but Map, Set and the ordered collections ask the type itself, and the two would silently disagree. Write the implementation in the module that declares the type — in the REPL, in the same entry as the type."

    let hmAssocBindings =
        assocBindings
        |> List.map (fun (name, fType) -> name, resolveTypeAnnotation env.Registry fType)

    let hmAssocBindingsMap = Map.ofList hmAssocBindings

    let traitInfo =
        match Map.tryFind traitName env.Registry.Traits with
        | Some info -> info
        | None -> failwithf $"Unknown trait '%s{traitName}' at %s{Lexer.formatPos r}"

    checkAssocBindings traitName traitInfo (assocBindings |> List.map fst) r

    // A trait that stands for a .NET interface has no implementations to
    // write: whether a type satisfies it is decided by the runtime, and a
    // `impl` would be a second answer to a question already answered.
    match traitInfo.ClrConstraint with
    | Some clr ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{traitName}' stands for the .NET interface '%s{clr.InterfaceName}', so it has no implementations to write — a type satisfies it by implementing the interface, which '%s{Naming.showTypeName typeKey}' either does or does not. Remove the impl."
    | None -> ()

    // The `(where ...)`, checked against what the impl can actually hold.
    //
    // Each constraint becomes a dictionary the impl class carries, so it has
    // to be phrased over a variable of the impl's own target: there is
    // nowhere else for the evidence to come from, and a variable named here
    // and nowhere in the target would be one the class has no parameter for.
    let targetVars = freeTVars env.Registry targetType |> Set.ofList

    let implConstraints =
        whereClause
        |> List.map (fun (cTrait, varName) ->
            if not (Set.contains varName targetVars) then
                let written = "%" + varName.TrimStart('\'')

                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: the where clause of this implementation constrains '%s{written}', which the implemented type does not mention. An impl may only constrain its own type variables."

            match Map.tryFind cTrait env.Registry.Traits with
            | None -> failwithf $"Unknown trait '%s{cTrait}' in the where clause at %s{Lexer.formatPos r}"
            | Some cInfo ->
                // An inline trait has no dictionary — that is what makes it
                // inline-only — so there is nothing an impl could hold to
                // discharge a constraint over one.
                if cInfo.Kind = InlineTrait then
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: '%s{cTrait}' is an inline-only trait, so it cannot appear in an implementation's where clause. There is no dictionary for the impl to carry."

                // The dictionary's C# type names the trait's associated
                // types too, and for a constraint over a type *variable*
                // those are not known here — they would have to become
                // further parameters of the impl class. Not built; say so
                // rather than emitting a class that will not compile.
                if not cInfo.AssociatedTypes.IsEmpty then
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: '%s{cTrait}' has associated types, which an implementation's where clause cannot carry yet. Constrain a function instead, where the association becomes a type parameter."

                { TraitName = cTrait; TargetType = TVar varName; Pins = [] })

    // Defaults are spliced in *here*, before anything looks at the method
    // list, so that everything below — the definition-site check, the
    // completeness check, the landing pads, the inline templates — sees an
    // impl that wrote every method out by hand. A defaulted method is
    // therefore not a second kind of method with a dispatch path of its own;
    // it is the same method, and it costs exactly what writing it would.
    //
    // Re-checking one body per impl rather than checking it once against the
    // trait is what makes a default able to say something the trait's own
    // signature cannot: `(clr-abs x)` picks `Math.Abs(int)` in the `int`
    // impl and `Math.Abs(double)` in the `double` one, from argument types
    // that only exist once the implementor is known.
    let definedMethodNames =
        methods
        |> List.choose (function
            | DDefun(name, _, _, _, _) -> Some name
            | _ -> None)
        |> Set.ofList

    let inheritedMethods =
        traitInfo.Defaults
        |> Map.toList
        |> List.filter (fun (name, _) -> not (Set.contains name definedMethodNames))
        |> List.map snd

    let methods = methods @ inheritedMethods

    // A hand-written body for a constrained member must repeat the
    // member's `(where ...)` as `(: name (where ...))` beside its defun —
    // required rather than inferred: it is more to write, and what the
    // impl reader then sees is a method that takes evidence. The trait's
    // own clause stays the authority for what is *checked*; the
    // repetition has to name the same traits.
    for name in definedMethodNames do
        let traitWheres =
            Map.tryFind name traitInfo.MemberWheres |> Option.defaultValue []

        let written =
            implMethodWheres |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd

        match traitWheres, written with
        | [], None -> ()
        | [], Some _ ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: '%s{name}' carries no where clause in trait '%s{traitName}', so this implementation may not add one. Remove the (: %s{name} (where ...)) form."
        | cs, None ->
            let spelled = cs |> List.map (fun c -> c.TraitName) |> String.concat ", "

            failwithf
                $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' declares '%s{name}' with a where clause (over %s{spelled}), and an implementation writing it by hand repeats the clause: add (: %s{name} (where ...)) beside the defun, spelled as the trait spells it."
        | cs, Some ws ->
            let want = cs |> List.map (fun c -> c.TraitName) |> List.sort |> String.concat ", "

            let got =
                ws
                |> List.map (fun (w: Ast.MemberConstraint) -> originalName env.Registry w.MCTrait)
                |> List.sort
                |> String.concat ", "

            if want <> got then
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: the where clause written for '%s{name}' does not match the trait's. The trait constrains %s{want}; this one names %s{got}."

    // A marker beside no method is a leftover.
    for (name, _) in implMethodWheres do
        if not (Set.contains name definedMethodNames) then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: (: %s{name} (where ...)) has no hand-written '%s{name}' beside it in this implementation. The clause rides the method; remove it, or write the method."

    /// Does the trait declare this method with a callback of either colour?
    let takesEitherColour (name: string) =
        match traitInfo.Kind with
        | InlineTrait ->
            Map.tryFind name traitInfo.Templates
            |> Option.map templateDeclaresPolyParam
            |> Option.defaultValue false
        | InterfaceTrait ->
            Map.tryFind name traitInfo.Signatures
            |> Option.map declaresPolyParam
            |> Option.defaultValue false

    // The second body, from the same source, for the twin the trait
    // derived. Generated here and checked below like any other method, for
    // the reason `expandPolymorphicDefuns` gives at the top level: the
    // parameter's colour is in every type on every node, so re-checking one
    // source against a repainted signature cannot miss an occurrence and a
    // substitution afterwards could.
    //
    // A default body is copied too — it arrived above as an ordinary method
    // and is one from here on.
    let methods =
        methods
        @ (methods
           |> List.choose (function
               | DDefun(name, args, body, _, mr) when takesEitherColour name ->
                   Some(DDefun(Naming.suspendingCopy name, args, body, Suspending, mr))
               | _ -> None))

    // Which of them the author did not write, so that a diagnostic about
    // one says so rather than pointing at a line where, as written, there
    // is nothing wrong.
    let env =
        { env with
            Registry =
                { env.Registry with
                    GeneratedCopies =
                        methods
                        |> List.fold
                            (fun acc m ->
                                match m with
                                | DDefun(name, _, _, _, _) when Naming.isSuspendingCopy name -> Set.add name acc
                                | _ -> acc)
                            env.Registry.GeneratedCopies } }

    let implTarget = implTargetOf traitName traitInfo targetType implConstraints r
    let regEnv = addImplementation traitName typeKey targetType implTarget hmAssocBindingsMap env

    // FIX 1: Prepend the "'" to the substitution keys so they match TVar "'c"
    let mutable substitutions = Map.add ("'" + traitInfo.ImplementorVar) targetType Map.empty

    for (k, v) in hmAssocBindings do
        substitutions <- Map.add ("'" + k) v substitutions

    let rec applySubst t =
        match prune regEnv.Registry t with
        | TVar name ->
            match Map.tryFind name substitutions with
            | Some concrete -> concrete
            | None -> t
        | TCon(n, args) -> TCon(n, args |> List.map applySubst)
        | TFun(args, ret, eff) -> TFun(args |> List.map applySubst, applySubst ret, eff)
        | TTuple args -> TTuple(args |> List.map applySubst)
        | _ -> t

    let typedMethods =
        methods
        |> List.map (checkImplMethod regEnv traitInfo implTarget implConstraints targetType traitName applySubst substitutions r)

    // Ensure all required methods from the trait are implemented
    let requiredMethods =
        match traitInfo.Kind with
        | InlineTrait -> traitInfo.Templates |> Map.toList |> List.map fst
        | InterfaceTrait -> traitInfo.Signatures |> Map.toList |> List.map fst

    for requiredMethod in requiredMethods do
        let isImplemented =
            methods
            |> List.exists (function
                | DDefun(name, _, _, _, _) -> name = requiredMethod
                | _ -> false)

        if not isImplemented then
            failwithf
                "Implementation of trait '%s' is missing required method '%s' at %s"
                traitName requiredMethod (Lexer.formatPos r)

    // Register every method as an inline template — interface traits
    // included. A statically resolvable call is inlined whatever the kind of
    // trait it belongs to; the difference is only that an interface trait
    // also keeps its dictionary path for the generic case.
    //
    // The body stored is the untyped one. Re-inferring it at the splice is
    // what lets it take a type the trait signature could not express, and a
    // typed AST is not serializable anyway: `HMType` is full of mutable
    // metavariable cells.
    let finalEnv =
        methods
        |> List.fold
            (fun acc methodDecl ->
                match methodDecl with
                | DDefun(name, defunArgs, body, _, _) ->
                    let paramNames = mandatoryNames defunArgs

                    // Keyword and rest parameters would have to survive the
                    // splice as a calling convention, which a spliced body
                    // has no call to carry. Such a method simply is not
                    // inlineable; the landing pad still is.
                    let inlineable =
                        defunArgs |> List.forall (function MandatoryArg _ -> true | _ -> false)

                    if inlineable then
                        addInlineTemplate
                            traitName
                            name
                            implTarget.Ctor
                            { Params = paramNames
                              Body = body
                              // Filled in after inference, where a
                              // name-to-module map exists.
                              Qualification = Map.empty
                              OriginModule = acc.CurrentModule }
                            acc
                    else
                        acc
                | _ -> acc)
            regEnv

    // The dictionaries the class holds, named exactly as a constrained
    // function's parameters are — `Lowering` puts them in scope for the
    // method bodies under the same names, so a body cannot tell whether the
    // dictionary it dispatches through arrived as an argument or was stored
    // by the constructor.
    //
    // A constrained trait has no associated types (checked above), so the
    // dictionary's type is the trait applied to the one variable.
    let dictFields =
        implConstraints
        |> List.map (fun c ->
            let varName =
                match c.TargetType with
                | TVar v -> v
                | other -> failwithf $"Internal error: impl constraint over %s{DotNetInterop.showType other}"

            dictParamName c.TraitName varName, TCon(c.TraitName, [ c.TargetType ]))

    finalEnv,
    sigs,
    [ TImpl(traitName, traitInfo.Kind, traitInfo.HoleArity, targetType, hmAssocBindings, dictFields, typedMethods, r) ]


/// One method of an `impl`, checked against the trait's signature instantiated
/// at this implementation.
///
/// The definition-site check. Checking each body here, once, is what keeps the
/// error out of the instantiation sites: an inline method that does not match
/// its trait is rejected at the impl rather than at every place it is spliced.
///
/// `applySubst` is `checkImpl`'s substitution of the implementor variable and
/// the associated types. It is passed in rather than rebuilt, because it closes
/// over bindings that only `checkImpl` has.
and private checkImplMethod
    (regEnv: Env)
    (traitInfo: TraitInfo)
    (implTarget: ImplTarget)
    (implConstraints: TraitConstraint list)
    (targetType: HMType)
    (traitName: string)
    (applySubst: HMType -> HMType)
    (substitutions: Map<string, HMType>)
    (r: Range)
    (methodDecl: Decl)
    : TDecl =
    match methodDecl with
    | DDefun(name, args, body, colour, methodRange) ->
        // The definition-site check. Checking each body against the
        // trait's own signature, instantiated at *this* impl, is what
        // keeps errors out of the instantiation sites: an inline
        // method that does not match its trait is rejected here,
        // once, rather than at every place it is later spliced.
        let expectedSignature =
            match traitInfo.Kind with
            | InlineTrait ->
                match Map.tryFind name traitInfo.Templates with
                | Some tpl -> instantiateTemplate implTarget tpl
                | None ->
                    failwithf
                        $"Method '%s{name}' is not a member of trait '%s{traitName}' at %s{Lexer.formatPos methodRange}"
            | InterfaceTrait ->
                match Map.tryFind name traitInfo.Signatures with
                | Some sigType -> applySubst sigType
                | None ->
                    failwithf
                        $"Method '%s{name}' is not a member of trait '%s{traitName}' at %s{Lexer.formatPos methodRange}"

        // A body is one colour or the other, never both, so the
        // `-?->` a *caller* reads is fixed here. This is the
        // ordinary half; the twin's was fixed suspending when the
        // trait derived it, and has no `EPoly` left to fix.
        let expectedSignature = ordinaryHalf expectedSignature

        // After substituting the implementor var and associated types,
        // the signature may still contain TVars from two sources:
        //   1. Class-level type params (from targetType, e.g. 'a in List %a)
        //      → These must stay as rigid TVars so they match the class params.
        //   2. Method-level generics (like 'acc in fold's signature)
        //      → These must be instantiated to fresh metas.
        //
        // An inline trait's class-level parameters are only the
        // impl's *fixed prefix*: the arguments the constructor
        // variable abstracts over belong to the method, and `bind`'s
        // own `'b` is a method-level generic that has to reach C# as
        // a generic method parameter.
        let classLevelVars =
            match traitInfo.Kind with
            | InlineTrait -> implTarget.FixedPrefix |> List.collect typeVarsOf |> Set.ofList
            | InterfaceTrait -> freeTVars regEnv.Registry targetType |> Set.ofList
        let remainingVars = freeTVars regEnv.Registry expectedSignature |> List.distinct

        // The member's own `(where ...)`, if the trait gave it one.
        // Its variables — the constrained one and any a pin
        // introduced — are method-level generics exactly like a
        // signature's own, so they join the same instantiation.
        //
        // A variable a *pin alone* introduced is renamed out of
        // the way first: the trait spelled `#:cursor %k` in its
        // own namespace, and nothing stops this impl's target
        // using the same letter — `(MapBuilder %k %v)` — which
        // would quietly weld the member's cursor to the class's
        // key. The constrained variable itself appears in the
        // signature and must stay spelled as the signature spells
        // it, or the pins would miss the parameter they are about.
        let memberCs =
            let raw = Map.tryFind name traitInfo.MemberWheres |> Option.defaultValue []

            // The signature's own method-level variables — not the
            // class's, which reach `remainingVars` through the
            // substituted implementor and are exactly the capture
            // being avoided.
            let sigVars =
                remainingVars
                |> List.filter (fun v -> not (Set.contains v classLevelVars))
                |> Set.ofList

            let rename =
                raw
                |> List.collect (fun c ->
                    freeTVars regEnv.Registry c.TargetType
                    @ (c.Pins |> List.collect (fun (_, t) -> freeTVars regEnv.Registry t)))
                |> List.distinct
                |> List.filter (fun v ->
                    not (Set.contains v sigVars) && not (Map.containsKey v substitutions))
                |> List.map (fun v -> v, TVar(v + "__mw"))
                |> Map.ofList

            raw
            |> List.map (fun c ->
                { c with
                    TargetType = substTypeVars rename c.TargetType
                    Pins = c.Pins |> List.map (fun (pn, t) -> pn, substTypeVars rename t) })

        let memberCsVars =
            memberCs
            |> List.collect (fun c ->
                freeTVars regEnv.Registry c.TargetType
                @ (c.Pins |> List.collect (fun (_, t) -> freeTVars regEnv.Registry t)))
            |> List.distinct

        //
        // `freshMetaInner`: `checkDecl` below generalizes at this
        // level, so the method's own variables must lie one
        // level in to be included in the scheme again.
        let freshSubst =
            (remainingVars @ memberCsVars)
            |> List.distinct
            |> List.filter (fun v ->
                not (Set.contains v classLevelVars)
                && not (Map.containsKey v substitutions))
            |> List.map (fun v -> v, freshMetaInner ())
            |> Map.ofList
        let instantiatedSig = substTypeVars freshSubst expectedSignature

        // Trait-space → this impl's space: the implementor and its
        // associated types by `substitutions`, the member's own
        // variables by the same fresh metas the signature got.
        let substituteAll (t: HMType) =
            substTypeVars freshSubst (applySubst t)

        // The pins, granted as *given* equalities while this body
        // is checked: here, where the implementor is known, the
        // projection at the member's variable simply is the pinned
        // type — `(assoc Iterable elem %s)` is `%added`, which
        // this impl bound. `Unification.prune` reads these where
        // it would otherwise leave the projection standing.
        let givenPins =
            memberCs
            |> List.collect (fun c ->
                let target = substituteAll c.TargetType

                c.Pins
                |> List.map (fun (assocName, pinT) ->
                    c.TraitName, assocName, target, substituteAll pinT))

        let regEnv =
            if givenPins.IsEmpty then
                regEnv
            else
                { regEnv with
                    Registry =
                        { regEnv.Registry with
                            PinnedAssocs = givenPins @ regEnv.Registry.PinnedAssocs } }

        // The impl's definer against the trait's arrow.
        //
        // This is the only place the two can meet. An impl method
        // declares no signature of its own — it inherits the
        // trait's — so the top-level check for a `-bjo->` signature
        // over a `defun` cannot fire here: `checkDecl` is handed
        // `None` in the slot that check reads. And it must not be
        // left to unification either, because `recolour` repaints
        // the inherited signature's outermost arrow with the
        // definer's colour before anything is unified, so a
        // disagreement is erased rather than reported.
        //
        // All implementations of a trait must agree on whether a method
        // is synchronous or asynchronous. We enforce this because, at
        // runtime, dynamic dispatch needs to know whether to await the
        // method call before it even figures out which specific class's
        // method it's calling.
        (match instantiatedSig with
         | TFun(_, _, declared) when declared <> colourEffect colour ->
             match declared with
             | EAsync ->
                 failwithf
                     $"Type Error at %s{Lexer.formatPos methodRange}: trait '%s{traitName}' declares '%s{name}' with -bjo->, so calling it is a yield point wherever it is dispatched, and every implementation of it has to be able to suspend. Define this one with defbjo."
             | _ ->
                 failwithf
                     $"Type Error at %s{Lexer.formatPos methodRange}: trait '%s{traitName}' declares '%s{name}' with ->, so a call to it is not a yield point and this implementation may not suspend. Define it with defun.\n  Declaring the method -bjo-> in the trait is the alternative, and it is a claim about all of them: a dispatched call cannot know which implementation it reached before deciding whether to await."
         | _ -> ())

        // Pass instantiatedSig through 'sigs'.
        // This forces DDefun to unify the expected types into the arguments
        // BEFORE inference and generalization.
        let methodSigs =
            Map.add
                name
                { Type = instantiatedSig
                  Written = None
                  Constraints = [] }
                Map.empty

        // Which method this is, so that the `defun`'s own recursion
        // binding is not taken for a shadow of it.
        let regEnv = { regEnv with ImplMethod = Some name }

        let _, _, tDecls = checkDecl regEnv methodSigs methodDecl
        let tDecl = List.head tDecls // The fully verified TDefun node

        // A member-level `(where ...)` becomes the method's own
        // leading dictionary parameters, mirrored on the interface
        // slot by `Codegen`. Injected here rather than in
        // `Lowering` — which looks constraints up under a
        // binding's name, and a trait method's binding carries
        // none — because here the pinned associated types are
        // known in this impl's own variable space: the dictionary
        // for `(Iterable %s #:elem %added)` is an
        // `Iterable<T_s, %added-as-bound-here, T_k>`.
        let tDecl =
            if memberCs.IsEmpty then
                tDecl
            else
                match tDecl with
                | TDefun(n, tyArgs, args, kwArgs, restArg, retType, effect, body, mr) ->
                    let dictParams =
                        memberCs
                        |> List.map (fun c ->
                            let target = prune regEnv.Registry (substituteAll c.TargetType)

                            let varName =
                                match target with
                                | TVar v -> v
                                | other -> DotNetInterop.showType other

                            let cInfo = Map.find c.TraitName regEnv.Registry.Traits

                            let assocArgs =
                                cInfo.AssociatedTypes
                                |> List.map (fun a ->
                                    match c.Pins |> List.tryFind (fun (pn, _) -> pn = a) with
                                    | Some(_, pinT) -> prune regEnv.Registry (substituteAll pinT)
                                    | None -> prune regEnv.Registry (TAssoc(c.TraitName, a, target)))

                            dictParamName c.TraitName varName, TCon(c.TraitName, target :: assocArgs))

                    // A pin to a fresh variable introduces a
                    // generic the method's own signature never
                    // mentions, so generalization cannot have
                    // reached it. Settle any meta still open in
                    // a dictionary's type as one more
                    // method-level generic; the body shares the
                    // cell, so every use follows.
                    let extraVars = ResizeArray<string>()

                    let rec settle t =
                        match prune regEnv.Registry t with
                        | TMeta m ->
                            let v = $"'mw%d{m.Id}"
                            m.Value <- Some(TVar v)
                            extraVars.Add v
                            TVar v
                        | TCon(n2, args2) -> TCon(n2, List.map settle args2)
                        | TTuple args2 -> TTuple(List.map settle args2)
                        | TFun(args2, ret2, eff2) -> TFun(List.map settle args2, settle ret2, eff2)
                        | TAssoc(tn, an, inner) -> TAssoc(tn, an, settle inner)
                        | other -> other

                    let dictParams = dictParams |> List.map (fun (dn, t) -> dn, settle t)

                    TDefun(
                        n,
                        (tyArgs @ List.ofSeq extraVars) |> List.distinct,
                        dictParams @ args,
                        kwArgs,
                        restArg,
                        retType,
                        effect,
                        body,
                        mr
                    )
                | other -> other

        // What the body turned out to need of the impl's own type
        // variables, against what the impl declared. A method is not
        // a generic function: there are no dictionary parameters to
        // inject, only the fields the `(where ...)` put on the
        // class, so an undeclared need has nowhere to come from.
        //
        // Caught here rather than in `Lowering`, which would
        // otherwise report a missing `_dict_` — a name the program
        // never wrote and the author has no way to supply.
        match tDecl with
        | TDefun(_, _, _, _, _, _, _, typedBody, _) ->
            for c in collectTraitConstraints regEnv typedBody do
                let varName =
                    match prune regEnv.Registry c.TargetType with
                    | TVar v -> v
                    | other -> DotNetInterop.showType other

                let declared =
                    implConstraints
                    |> List.exists (fun d -> d.TraitName = c.TraitName && d.TargetType = TVar varName)

                // A member-level `(where ...)` also supplies a
                // dictionary — as the method's own leading
                // parameter rather than a field of the class.
                let memberDeclared =
                    memberCs |> List.exists (fun d -> d.TraitName = c.TraitName)

                if not (declared || memberDeclared) then
                    // Spelled as the source spells a type variable,
                    // because the message asks for a line to be
                    // typed and `'a` is not how one is written.
                    let written = "%" + varName.TrimStart('\'')

                    failwithf
                        $"Type Error at %s{Lexer.formatPos methodRange}: '%s{name}' uses '%s{c.TraitName}' at '%s{written}', which this implementation does not require. Add (where (%s{c.TraitName} %s{written})) to the implementation, and every use of it will have to supply one."
        | _ -> ()

        tDecl

    | _ -> failwithf $"Only 'defun' declarations are allowed inside 'impl' at %s{Lexer.formatPos r}"

and private checkImplExtern (env: Env) (sigs: Sigs) (traitName: string) (targetTypeExpr: FType) (assocBindings: (string * FType) list) (whereClause: (string * string) list) (r: Range) : Env * Sigs * TDecl list =
    // A bodyless implementation, read back from a compiled module's
    // metadata. Only the registry needs to learn about it: the methods are
    // already compiled into the assembly that declared it, so there is
    // nothing to type-check and nothing to emit.
    let traitName = originalName env.Registry traitName
    let whereClause = whereClause |> List.map (fun (t, v) -> originalName env.Registry t, v)
    let targetType = resolveTypeAnnotation env.Registry targetTypeExpr

    let typeKey =
        match implCtorKey targetType with
        | Some k -> k
        | None -> failwithf $"Trait implementations require concrete target types at %s{Lexer.formatPos r}"

    let traitInfo =
        match Map.tryFind traitName env.Registry.Traits with
        | Some info -> info
        | None ->
            failwithf $"Unknown trait '%s{traitName}' in imported implementation at %s{Lexer.formatPos r}"

    checkAssocBindings traitName traitInfo (assocBindings |> List.map fst) r

    let hmAssocBindings =
        assocBindings
        |> List.map (fun (name, fType) -> name, resolveTypeAnnotation env.Registry fType)
        |> Map.ofList

    // The published `(where ...)`. An importing module builds the dictionary
    // for `(List int)` itself, so it has to know a `(->str int)` goes inside
    // — the impl class in the other assembly has no `Instance` to reach for.
    let implConstraints =
        whereClause
        |> List.map (fun (cTrait, varName) -> { TraitName = cTrait; TargetType = TVar varName; Pins = [] })

    let implTarget = implTargetOf traitName traitInfo targetType implConstraints r
    addImplementation traitName typeKey targetType implTarget hmAssocBindings env, sigs, []

and internal checkDeclGroup
    (env: Env)
    (sigs: Sigs)
    (decls: Decl list)
    : Env * Sigs * TDecl list =

    let decls = ColourTwins.expandPolymorphicDefuns decls

    /// Which of those are copies, so that a diagnostic about one can say so.
    let env =
        let generated =
            decls
            |> List.choose (function
                | DDefun(name, _, _, Suspending, _) when Naming.isSuspendingCopy name -> Some name
                | _ -> None)
            |> Set.ofList

        { env with
            Registry =
                { env.Registry with
                    GeneratedCopies = Set.union env.Registry.GeneratedCopies generated } }

    let decls, inferredPairs = ColourTwins.expandReachingDefuns env.Registry decls

    /// Registered as doubles, so that the *existing* enclosing-colour selection
    /// in `EffectGraph.selectDoubles` picks them up with no case of its own.
    /// Unlike a `-?->`, both copies here have the same parameter types, so
    /// there is no argument to choose by and the caller's colour is the only
    /// thing that could decide.
    let env =
        { env with
            Registry =
                { env.Registry with
                    DoubleDefs = inferredPairs |> Map.fold (fun acc k v -> Map.add k v acc) env.Registry.DoubleDefs
                    GeneratedCopies =
                        inferredPairs
                        |> Map.toSeq
                        |> Seq.map snd
                        |> Set.ofSeq
                        |> Set.union env.Registry.GeneratedCopies
                    InferredCopies =
                        inferredPairs
                        |> Map.toSeq
                        |> Seq.map snd
                        |> Set.ofSeq
                        |> Set.union env.Registry.InferredCopies } }

    /// The registry a signature is *read* with: this one, plus the group's own
    /// type declarations.
    ///
    /// Signatures are resolved up front, before anything in the group is
    /// checked, and a `type` declared beside them had not been registered yet.
    /// For a union that is invisible — an unknown name resolves to a
    /// constructor of that name, which is exactly what the declaration
    /// registers — but an alias resolves to nothing at all, so
    /// `(: run/lines (-> ProcList (List string)))` took a `ProcList` that no
    /// later `(List ProcItem)` could unify with.
    ///
    /// The declarations are registered again by the fold below, in order and
    /// into the environment the group really uses. This copy is thrown away
    /// after the signatures have been read off it.
    ///
    /// A module's own `import/class` aliases are folded in for the same reason
    /// — `Exception` is a type here as much as a `type` declaration is one —
    /// and only the alias, since a constructor signature is resolved later and
    /// names nothing a signature elsewhere can.
    ///
    /// Imports of other modules need no such treatment: `DImport` adds nothing
    /// here, because an imported module is a group of its own that has already
    /// been checked by the time this one starts.
    ///
    /// The spellings are folded in too. A type is registered under its key, so
    /// a signature written above the `type` it names — or above the import
    /// alias a `.dll`'s reader produced — resolves the bare name through the
    /// same table everything else does, and has to see it.
    let sigRegistry =
        decls
        |> List.fold
            (fun acc d ->
                match d with
                | DType(typeDefs, _) -> fst (registerTypeDefs false typeDefs acc)
                | DTypeRec(typeDefs, _) -> fst (registerTypeDefs true typeDefs acc)
                | DImportClass(specs, _) ->
                    specs
                    |> List.map classInfoOfSpec
                    |> List.fold
                        (fun (inner: Env) info ->
                            { inner with
                                Registry =
                                    { inner.Registry with
                                        Aliases =
                                            Map.add
                                                info.Alias
                                                (info.TypeParams, classAliasTarget info)
                                                inner.Registry.Aliases } })
                        acc
                | DImportAlias(visible, original, kind, _) ->
                    { acc with
                        Registry =
                            { acc.Registry with
                                ImportAliases =
                                    Map.add
                                        visible
                                        { OriginModule = acc.CurrentModule
                                          OriginalName = original
                                          Kind = kind }
                                        acc.Registry.ImportAliases } }
                // Provisionally register traits for associated types and dyn-safety:
                // signature annotations may reference `(dyn LocalTrait #:item int)`
                // declared later in the same file. `DTrait` performs full
                // processing when reached.
                //
                // Traits whose signatures cannot be parsed during this pre-pass
                // are skipped and reported later during `def/trait`.
                | DTrait(traitName, implementorVar, holeArity, assocTypes, signatures, _, clrSpec, _) ->
                    try
                        let kind = if holeArity > 0 then InlineTrait else InterfaceTrait

                        let hmSignatures =
                            match kind with
                            | InterfaceTrait ->
                                signatures
                                |> List.map (fun (name, fType, _) -> name, resolveTypeAnnotation acc.Registry fType)
                                |> Map.ofList
                            | InlineTrait -> Map.empty

                        // Only the interface name is needed to determine boxability;
                        // detailed constraints are validated on declaration.
                        let clr =
                            clrSpec
                            |> Option.map (fun (ifaceName, _, _) ->
                                { InterfaceName = ifaceName
                                  Args = []
                                  Members = Map.empty })

                        addTrait
                            traitName
                            { ImplementorVar = implementorVar
                              AssociatedTypes = assocTypes
                              Signatures = hmSignatures
                              Kind = kind
                              HoleArity = holeArity
                              Templates = Map.empty
                              Defaults = Map.empty
                              ClrConstraint = clr
                              // Member wheres are resolved by the full
                              // `DTrait` pass; this pre-registration only
                              // exists so signatures can be parsed.
                              MemberWheres = Map.empty
                              DynSafe = dynSafety traitName implementorVar kind clr hmSignatures Map.empty }
                            acc
                    with _ ->
                        acc
                | _ -> acc)
            env
        |> fun withTypes -> withTypes.Registry

    // One name, two signatures. Silently unanswerable before this: the map
    // below is built from a list, so whichever came last won and the other was
    // dropped without a word. It became reachable when a `defun` was allowed to
    // write its own — the two spellings say the same thing, and saying it twice
    // is a mistake rather than a refinement.
    //
    // Trait method signatures are deliberately not in this: two traits
    // declaring a method of one name is a different question, answered by its
    // own diagnostic.
    let duplicateSignature =
        decls
        |> List.choose (function
            | DSignature(name, _, _, r) -> Some(name, r)
            | _ -> None)
        |> List.groupBy fst
        |> List.tryFind (fun (_, group) -> group.Length > 1)

    match duplicateSignature with
    | Some(name, (_, r) :: _) ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{name}' has more than one type signature. A (: %s{name} ...) and a `defun` that writes its own types both declare one, so write whichever of the two you meant and not both."
    | _ -> ()

    /// Resolves a type signature and attaches the given source location `r` to any errors.
    ///
    /// We resolve signatures before checking declarations so they can refer to each other
    /// in any order. Because this happens at the module level, errors would default to 
    /// reporting the entire file's location. This helper ensures errors in the signature 
    /// point specifically to the signature's location.
    let resolveSigAt (r: Range) (ftype: FType) =
        try
            resolveTypeAnnotation sigRegistry ftype
        with ex when Diagnostics.needsLocation ex ->
            raise (Diagnostics.withLocation r ex)

    let explicitSigs =
        decls
        |> List.collect (function
            | DSignature(name, ftype, constraints, r) ->
                [ name,
                  { Type = resolveSigAt r ftype
                    Written = Some ftype
                    Constraints = constraints } ]
            // An inline trait's signatures are not `HMType`s and never can be:
            // they mention the constructor variable applied. They are read as
            // templates by `DTrait` instead, and there is nothing to inject here.
            | DTrait(_, _, holeArity, _, signatures, _, _, r) when holeArity = 0 ->
                signatures
                |> List.map (fun (name, ftype, _) ->
                    name,
                    { Type = resolveSigAt r ftype
                      Written = Some ftype
                      Constraints = [] })
            | _ -> [])
        |> Map.ofList

    /// A trait method's signature comes from its `def/trait`, whichever kind of
    /// trait that is, so exporting one is never missing a signature.
    ///
    /// The trait's own name is here too, and has to be: a trait standing for a
    /// .NET interface declares no methods, so there is no method name to
    /// publish it by, and `(export Num)` is the only way it can cross a module
    /// boundary at all.
    let traitMethodNames =
        decls
        |> List.collect (function
            | DTrait(traitName, _, _, _, signatures, _, _, _) ->
                traitName :: (signatures |> List.map (fun (n, _, _) -> n))
            | _ -> [])
        |> Set.ofList

    /// Aliases bound by `import/extern` in this group.
    ///
    /// One of these has no signature and cannot be given one: it names a .NET
    /// *overload set*, and which member of it a call means is decided from that
    /// call's argument types. So it is exported as the import itself rather than
    /// as a type — the importing module re-resolves the overloads against the
    /// same metadata, exactly as this one did.
    let externAliases =
        decls
        |> List.collect (function
            | DImportExtern(specs, _) -> specs |> List.map (fun s -> s.Alias)
            | _ -> [])
        |> Set.ofList

    /// Names that already carry a signature from somewhere else: a second
    /// spelling this group declares, and anything an import brought in under
    /// any spelling.
    ///
    /// Rule 9's facade is exactly this — `(export http-get)` where `http-get`
    /// is an alias. The local-signature rule cannot apply to one, for the same
    /// reason it does not apply to `re-export`: the name was given its type
    /// where it was defined, and writing a second one here would be a copy that
    /// could disagree.
    let aliasedNames =
        decls
        |> List.collect (function
            | DAlias(newName, _, _) -> [ newName ]
            | _ -> [])
        |> Set.ofList

    /// The type names this group declares, and the `import/class` aliases it
    /// binds.
    ///
    /// A type has to be named in an `(export ...)` to cross at all, and it has
    /// no signature to be missing: what it publishes is its declaration. So the
    /// rule below has to know a type name when it sees one, or exporting one
    /// would fail asking for a signature that cannot be written.
    let declaredTypeNames =
        decls
        |> List.collect (function
            | DType(typeDefs, _)
            | DTypeRec(typeDefs, _) -> typeDefs |> List.map (fun td -> td.Name)
            | DImportClass(specs, _) -> specs |> List.map (fun s -> s.Alias)
            | _ -> [])
        |> Set.ofList

    decls
    |> List.iter (function
        | DExport(names, exprRange) ->
            for name in names do
                if
                    not (
                        Map.containsKey name explicitSigs
                        || Set.contains name traitMethodNames
                        || Set.contains name externAliases
                        || Set.contains name aliasedNames
                        || Set.contains name declaredTypeNames
                        || Map.containsKey name env.Registry.ImportAliases
                    )
                then
                    failwithf "Export Error: Exported item '%s' is missing a mandatory type signature at %s" name (Lexer.formatPos exprRange)
        | _ -> ())

    let combinedSigs = Map.fold (fun acc k v -> Map.add k v acc) sigs explicitSigs

    // The suspending copy of a `defbjouble` shares the written signature, and
    // has to have it here as well as in `checkDecl`: the forward-declaration
    // fold below reads `explicitSigs`, so without this the copy is in
    // `declaredFunctions` with nothing to bind it to and a forward call to it
    // finds nothing.
    let explicitSigs =
        decls
        |> List.fold
            (fun acc d ->
                match d with
                | DDefDouble(name, _, _, _, _) ->
                    match Map.tryFind name acc with
                    | Some signature -> Map.add (Naming.suspendingCopy name) signature acc
                    | None -> acc
                | _ -> acc)
            explicitSigs

    // Bind every function with a signature before checking them, 
    // allowing out-of-order and mutually recursive calls within the group.
    let declaredFunctions =
        decls
        |> List.collect (function
            | DDefun(name, _, _, colour, _) -> [ name, colourEffect colour ]
            // Both halves, so that a forward call to either resolves. The
            // suspending copy is bound under the same signature it will be
            // checked against, repainted — which is the same thing `checkDecl`
            // does for it a moment later, and has to agree with.
            | DDefDouble(name, _, _, _, _) -> [ name, ESync; Naming.suspendingCopy name, EAsync ]
            | _ -> None |> Option.toList)
        |> Map.ofList

    let envWithForwardDecls =
        explicitSigs
        |> Map.fold
            (fun (acc: Env) name (signature: Signature) ->
                match Map.tryFind name declaredFunctions with
                | None -> acc
                | Some effect ->

                    let (Scheme(vars, _, schemeType)) = generalize acc (recolour effect signature.Type)

                    let constraints =
                        signature.Constraints
                        |> List.map (fun (traitName, varName) ->
                            { TraitName = originalName acc.Registry traitName; TargetType = TVar varName; Pins = [] })

                    let bound =
                        addBinding
                            name
                            { Scheme = Scheme(vars, constraints, schemeType)
                              IsMutable = false }
                            acc

                    // Keyword and rest parameters need their metadata just as
                    // early: a forward call that passes a keyword argument, or
                    // omits an optional one, has nothing to resolve against
                    // without it.
                    match signature.Written with
                    | Some(TArrow(mandatory, keywords, restOpt, _, _, _)) ->
                        let funMeta =
                            { MandatoryCount = mandatory.Length
                              KeywordParams =
                                keywords |> List.map (fun (n, ft) -> n, resolveTypeAnnotation acc.Registry ft)
                              RestParam = restOpt |> Option.map (resolveTypeAnnotation acc.Registry) }

                        { bound with FunMetas = Map.add name funMeta bound.FunMetas }
                    | _ -> bound)
            env

    let finalEnv, finalSigs, typedDecls =
        decls
        |> List.fold
            (fun (currEnv, currSigs, accDecls) d ->
                let nextEnv, nextSigs, tDecls =
                    match d with
                    // Which implementation a failure came from. A method body
                    // is short, and often nobody wrote it — `type/derive`
                    // writes one per field, and every node a macro builds
                    // carries the *call site's* range, so the line reported is
                    // the derive form rather than the field that asked for the
                    // comparison.
                    | DImpl(traitName, target, _, _, _, _, ir) ->
                        try
                            checkDecl currEnv currSigs d
                        with ex when Diagnostics.isDiagnostic ex ->
                            let targetName =
                                match target with
                                | TName(n, _) -> n
                                | TApp(n, _, _) -> n
                                | _ -> "this type"

                            failwithf
                                $"%s{ex.Message}\n  in the implementation of '%s{traitName}' for '%s{targetName}' at %s{Lexer.formatPos ir}"
                    | _ -> checkDecl currEnv currSigs d

                (nextEnv, nextSigs, tDecls @ accDecls))
            (envWithForwardDecls, combinedSigs, [])

    finalEnv, finalSigs, List.rev typedDecls

