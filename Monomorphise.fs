module Bjolang.Monomorphise

open Bjolang.Parser
open Bjolang.TypedAST
open Bjolang.Unification

/// Replaces a call to a constrained generic function with a call to a copy of
/// it made at the call's own type arguments.
///
/// A `(where (Eq %a))` is discharged by passing a dictionary: `Lowering` turns
/// it into a leading parameter and every trait call in the body into
/// `_dict_Eq_a.eq(a, b)`, which is an interface call the JIT cannot devirtualise
/// and — the larger cost — a call `TraitInline` cannot splice, because the
/// receiver is a type variable and `TraitRef.Resolved` is therefore `None`. A
/// copy checked at `int` resolves the same call to `Eq_System_Int32`, which
/// `TraitInline` then splices into `a == b`.
///
/// That is why this runs *before* `TraitInline` rather than after: run later it
/// would remove the virtual call and leave the body it was called from intact,
/// which is the smaller half.
///
/// The copy is generated as **source** and re-checked, never by substituting
/// into the checked tree. See `Inference.checkAddendum`.
///
/// Which is also why the body of an exported constrained function is published
/// in the module's metadata, and why the copy is made in whichever module calls
/// it rather than ready-made by the one that wrote it. Which instantiations
/// exist is not knowable where the function is defined — and a copy made in the
/// caller is checked against the caller's registry, so it resolves against
/// implementations the defining module never saw.
///
/// Depth is one, and structurally so: the sweep runs once over the written
/// program and never over what it generates, so a call inside a copy still
/// passes evidence. It is a concrete singleton rather than a dictionary
/// parameter, which is what the copy bought; removing the call as well means
/// iterating the sweep, which is deliberately not what this does yet.

// ---------------------------------------------------------------------------
// Naming a copy after the types it was made at
// ---------------------------------------------------------------------------

/// FNV-1a.
///
/// `String.GetHashCode` is randomised per process, and a copy's name has to be
/// the same on every run for an incremental build to be able to reuse one.
let private stableHash (s: string) : string =
    let mutable h = 2166136261u

    for ch in s do
        h <- (h ^^^ uint32 ch) * 16777619u

    h.ToString("x8")

let private sanitize (s: string) =
    s
    |> Seq.map (fun c -> if System.Char.IsLetterOrDigit c || c = '_' then c else '_')
    |> Seq.toArray
    |> System.String

/// How a primitive is spelled in a copy's name.
///
/// `same?__at_int` rather than `same?__at_System_Int32`. Cosmetic, and only
/// that: the name is derived from this table on both the definition and every
/// call site in the same compilation, so nothing depends on the spelling beyond
/// being able to read a stack trace.
let private shortName (name: string) =
    match name with
    | _ when name = TypeConstants.Int32Name -> "int"
    | _ when name = TypeConstants.StringName -> "string"
    | _ when name = TypeConstants.BooleanName -> "bool"
    | _ when name = TypeConstants.DoubleName -> "double"
    | _ when name = TypeConstants.Int64Name -> "long"
    | _ when name = TypeConstants.UnitName -> "unit"
    | other -> other

let private traverse (f: 'a -> 'b option) (xs: 'a list) : 'b list option =
    let rec go acc rest =
        match rest with
        | [] -> Some(List.rev acc)
        | x :: tail ->
            match f x with
            | Some y -> go (y :: acc) tail
            | None -> None

    go [] xs

/// The identifier fragment a type argument contributes to a copy's name.
///
/// A function type answers `None`, which declines the whole specialisation. It
/// has no short spelling, and a type argument that is one is both rare and the
/// case where the traits that motivate this — `Eq`, `Ord`, `->str` — have no
/// implementation to resolve to anyway.
let rec private typeKey (t: HMType) : string option =
    match t with
    | TCon(name, []) -> Some(sanitize (shortName name))
    | TCon(name, args) ->
        traverse typeKey args
        |> Option.map (fun keys -> sanitize (shortName name) + "_" + String.concat "_" keys)
    | TTuple args -> traverse typeKey args |> Option.map (fun keys -> "tup_" + String.concat "_" keys)
    | TFun _
    | TVar _
    | TMeta _
    | TAssoc _ -> None

/// The name of the copy `name` takes at these type arguments.
///
/// Hashed past a length that keeps a stack trace readable. The bound is reached
/// by ordinary types rather than by pathological ones: a type this module did
/// not declare is keyed by the module that did, so one user type is already
/// most of the budget.
///
/// The hash is over the whole key and the readable part is only a hint, so two
/// instantiations that agree on their last segments still get distinct names.
let private copyName (name: string) (keys: string list) =
    let joined = String.concat "__" keys

    if joined.Length <= 40 then
        Naming.specializedCopy name joined
    else

    let lastSegment (k: string) =
        match k.LastIndexOf '_' with
        | i when i >= 0 && i < k.Length - 1 -> k.Substring(i + 1)
        | _ -> k

    let hint = keys |> List.map lastSegment |> String.concat "_"
    let hint = if hint.Length > 24 then hint.Substring(0, 24) else hint

    Naming.specializedCopy name (hint + "_" + stableHash joined)

// ---------------------------------------------------------------------------
// Spelling a ground type back as a surface type
// ---------------------------------------------------------------------------

/// The inverse of `Inference.resolveTypeAnnotation`, over ground types only.
///
/// The copy's signature is a `DSignature`, so the substituted type has to be an
/// `FType`; nothing else in the compiler goes in this direction, because
/// nothing else generates a signature from a type it inferred.
let rec private hmToFType (r: Lexer.Range) (t: HMType) : FType option =
    match t with
    | TCon(name, []) -> Some(TName(name, r))
    | TCon(name, args) -> traverse (hmToFType r) args |> Option.map (fun a -> TApp(name, a, r))
    | TTuple args -> traverse (hmToFType r) args |> Option.map (fun a -> TApp("Tuple", a, r))
    | TFun(args, ret, eff) ->
        let head =
            match groundEffect eff with
            | ESync -> Some "->"
            | EAsync -> Some "-bjo->"
            | _ -> None

        match head, traverse (hmToFType r) (args @ [ ret ]) with
        | Some h, Some parts -> Some(TApp(h, parts, r))
        | _ -> None
    | TAssoc(traitName, assocName, implementor) ->
        hmToFType r implementor
        |> Option.map (fun i -> TApp("assoc", [ TName(traitName, r); TName(assocName, r); i ], r))
    | TVar _
    | TMeta _ -> None

/// `t` as a surface type, or `None` if it cannot be spelled as one faithfully.
///
/// The answer is checked by resolving it again and comparing: a type
/// constructor whose name is also an alias key, a primitive under a second
/// spelling, and the internal name of a `dyn` type — which is not surface
/// syntax at all — all resolve back to something else, and this is what turns
/// each of them into a declined specialisation rather than a copy with the
/// wrong signature.
let private spellGround (registry: TraitRegistry) (r: Lexer.Range) (t: HMType) : FType option =
    // An arrow's effect may be a solved cell, and `hmToFType` writes the arrow
    // head the cell resolved to. The comparison below is between a type that
    // has been through the parser and one that has not, so both sides are put
    // in the same terms first.
    let rec settle (t: HMType) =
        match t with
        | TCon(n, args) -> TCon(n, List.map settle args)
        | TFun(args, ret, eff) -> TFun(List.map settle args, settle ret, groundEffect eff)
        | TTuple args -> TTuple(List.map settle args)
        | TAssoc(traitName, assocName, implementor) -> TAssoc(traitName, assocName, settle implementor)
        | other -> other

    // `prune` raises when an associated type is projected out of a type with no
    // implementation. That is a declined specialisation rather than a failure:
    // the generic original will report it at the call site if it is real.
    match (try Some(settle (prune registry t)) with _ -> None) with
    | None -> None
    | Some pruned ->

    match hmToFType r pruned with
    | Some ft when (try settle (Inference.resolveTypeAnnotation registry ft) = pruned with _ -> false) -> Some ft
    | _ -> None

// ---------------------------------------------------------------------------
// Substituting into a written signature
// ---------------------------------------------------------------------------

let private bareVar (v: string) = v.TrimStart '\''

/// Replaces the type variables of a type with what a call site gave them.
let rec private substHM (subst: Map<string, HMType>) (t: HMType) : HMType =
    match t with
    | TVar n -> Map.tryFind (bareVar n) subst |> Option.defaultValue t
    | TCon(n, args) -> TCon(n, args |> List.map (substHM subst))
    | TFun(args, ret, eff) -> TFun(args |> List.map (substHM subst), substHM subst ret, eff)
    | TTuple args -> TTuple(args |> List.map (substHM subst))
    | TAssoc(traitName, assocName, implementor) -> TAssoc(traitName, assocName, substHM subst implementor)
    | TMeta _ -> t

/// One component of a signature, at this instantiation, spelled back as surface
/// syntax.
///
/// The substitution happens on the *resolved* type rather than on the written
/// one, because a signature may project an associated type —
/// `(assoc Over elem %c)` — and putting the implementor into that leaves a
/// projection standing. Only `prune` discharges one, and only once the
/// implementor is concrete, which it is exactly here. Substituting on the
/// `FType` instead left the copy with a parameter Codegen spelled `object`.
let private specialiseType
    (registry: TraitRegistry)
    (r: Lexer.Range)
    (subst: Map<string, HMType>)
    (ft: FType)
    : FType option =
    let resolved = Inference.resolveTypeAnnotation registry ft |> substHM subst
    spellGround registry r resolved

// ---------------------------------------------------------------------------
// What may be specialised
// ---------------------------------------------------------------------------

/// A definition this pass is allowed to copy.
///
/// `Origin` is the module the body was *written* in, and is `None` when that is
/// the module being compiled. For an imported one it decides two things: the
/// module the body is re-checked under, and the qualification its free names
/// are rewritten with afterwards.
type private Candidate =
    { Name: string
      Signature: FType
      Args: DefunArg list
      Body: Expr
      Origin: string option
      Qualification: Map<string, string>
      SigRange: Lexer.Range
      DefRange: Lexer.Range }

/// Does a signature declare a parameter that may take either colour?
let rec private mentionsPolyArrow (ft: FType) : bool =
    match ft with
    | TName _ -> false
    | TApp("-?->", _, _) -> true
    | TApp(_, args, _) -> args |> List.exists mentionsPolyArrow
    | TArrow(mandatory, keywords, restOpt, ret, _, _) ->
        (mandatory @ (keywords |> List.map snd) @ Option.toList restOpt @ [ ret ])
        |> List.exists mentionsPolyArrow

/// Is this trait discharged by a C# `where` clause rather than by a dictionary?
///
/// Such a constraint costs nothing to leave in place — the runtime already
/// specialises a generic method over a value type and devirtualises through the
/// bound — so a function constrained only by these has nothing to gain here.
let private isClrConstraint (registry: TraitRegistry) (traitName: string) =
    match Map.tryFind traitName registry.Traits with
    | Some info -> info.ClrConstraint.IsSome
    | None -> false

/// Whether a definition of this name and shape may be copied at all.
///
/// Asked of a local definition and of an imported one alike, so that a library
/// publishes exactly the bodies an importer would have been allowed to copy had
/// it written them itself.
let private eligible
    (env: Env)
    (name: string)
    (ftype: FType)
    (constraints: (string * string) list)
    (args: DefunArg list)
    =
    // A copy is only worth making for a constraint that costs a dictionary.
    constraints
    |> List.exists (fun (traitName, _) ->
        not (isClrConstraint env.Registry (Inference.originalName env.Registry traitName)))
    && name <> "main"
    // A `-?->` promises a second body, and the twin is generated inside
    // `checkDeclGroup` — so it is not among the declarations read here, and a
    // copy of the ordinary half alone would be a name `selectDoubles` cannot
    // find a twin for. Both halves want specialising together, which is a
    // second feature.
    && not (mentionsPolyArrow ftype)
    && not (Map.containsKey name env.Registry.DoubleDefs)
    && not (Set.contains name env.Registry.GeneratedCopies)
    && not (Set.contains name env.Registry.InferredCopies)
    // Keyword and rest parameters are a calling convention, and a default value
    // is an expression that would have to be re-checked in the copy alongside
    // the body. `addInlineTemplate` declines them for the same reason.
    && args |> List.forall (function MandatoryArg _ -> true | _ -> false)

/// The declarations of the module being compiled, and its name.
let private ownModule (decls: Decl list) : string * Decl list =
    match List.tryLast decls with
    | Some(DModule(name, inner, _)) -> name, inner
    | _ -> "", decls

/// The definitions written in this module that a copy may be made from.
let private localCandidates (env: Env) (decls: Decl list) : Map<string, Candidate> =
    let _, moduleDecls = ownModule decls

    let signatures =
        moduleDecls
        |> List.choose (function
            | DSignature(name, ftype, constraints, r) -> Some(name, (ftype, constraints, r))
            | _ -> None)
        |> Map.ofList

    let found =
        moduleDecls
        |> List.choose (function
            // Ordinary definers only. A `defbjo` has a twin for the same reason
            // a `-?->` does.
            | DDefun(name, args, body, Ordinary, r) ->
                match Map.tryFind name signatures with
                | Some(ftype, constraints, sigRange) when eligible env name ftype constraints args ->
                    Some(
                        name,
                        { Name = name
                          Signature = ftype
                          Args = args
                          Body = body
                          Origin = None
                          Qualification = Map.empty
                          SigRange = sigRange
                          DefRange = r }
                    )
                | _ -> None
            | _ -> None)
        |> Map.ofList

    // Two constrained functions that call each other are each other's callee
    // rather than their own, so a copy of one would still call the generic
    // other — which is the `CS7036` that direct recursion used to give. The
    // group wants specialising together; until it is, neither member is.
    let calls =
        found
        |> Map.map (fun name c ->
            AlphaRename.freeNames (Set.ofList (mandatoryNames c.Args)) c.Body
            |> Set.filter (fun n -> n <> name && Map.containsKey n found))

    let inCycle (start: string) =
        let rec walk seen frontier =
            if Set.contains start frontier then
                true
            else
                let next =
                    frontier
                    |> Set.filter (fun n -> not (Set.contains n seen))
                    |> Set.fold (fun acc n -> Set.union acc (Map.tryFind n calls |> Option.defaultValue Set.empty)) Set.empty

                if Set.isEmpty next then false else walk (Set.union seen frontier) next

        walk Set.empty (Map.tryFind start calls |> Option.defaultValue Set.empty)

    found |> Map.filter (fun name _ -> not (inCycle name))

/// The imported definitions a copy may be made from.
///
/// The body comes from the dependency's metadata and the signature from the
/// `DExtern` the importer parsed out of the same metadata, so the two are
/// matched by the name the *origin* published: an import modifier changes what
/// this module calls the function, and nothing about the body.
///
/// A local definition of the same name wins. Shadowing an import is what a
/// module is entitled to do, and the local body is the one its calls mean.
let private importedCandidates (env: Env) (local: Map<string, Candidate>) (decls: Decl list) : Map<string, Candidate> =
    if Map.isEmpty env.Registry.ConstrainedBodies then
        Map.empty
    else

    decls
    |> List.collect (function
        | DModule(_, inner, _) -> inner
        | other -> [ other ])
    |> List.choose (function
        | DExtern(visible, origin, ftype, constraints, r) when not (Map.containsKey visible local) ->
            match Map.tryFind origin.OriginalName env.Registry.ConstrainedBodies with
            | Some tpl ->
                let args = tpl.Params |> List.map (fun p -> MandatoryArg(p, None))

                if eligible env visible ftype constraints args then
                    Some(
                        visible,
                        { Name = visible
                          Signature = ftype
                          Args = args
                          Body = tpl.Body
                          Origin = Some tpl.OriginModule
                          Qualification = tpl.Qualification
                          SigRange = r
                          DefRange = r }
                    )
                else
                    None
            | None -> None
        | _ -> None)
    |> Map.ofList

let private candidates (env: Env) (decls: Decl list) : Map<string, Candidate> =
    let local = localCandidates env decls

    importedCandidates env local decls
    |> Map.fold (fun acc name c -> Map.add name c acc) local

// ---------------------------------------------------------------------------
// Demand
// ---------------------------------------------------------------------------

/// One instantiation a call site asked for.
type private Demand =
    { Callee: string
      CopyName: string
      /// The scheme's variables, in order, and what this call gave them.
      Bindings: (string * HMType) list }

/// What copy, if any, this callee at these type arguments asks for.
///
/// One definition, asked twice: once to find out which copies to make and once
/// to point the calls at them. Two walks that decided this differently would
/// mean a call to a copy that was never generated, or a copy nothing calls.
let private demandOf (env: Env) (cands: Map<string, Candidate>) (name: string) (tArgs: HMType list) : Demand option =
    let ground (t: HMType) =
        let pruned = prune env.Registry t
        List.isEmpty (freeVars env.Registry pruned) && List.isEmpty (freeTVars env.Registry pruned)

    // Unqualified only: a qualified name is another module's function, and this
    // pass has no body for one.
    if Naming.writtenName name <> name || not (Map.containsKey name cands) then
        None
    else

    match Map.tryFind name env.Bindings with
    | Some binding ->
        let (Scheme(schemeVars, constraints, _)) = binding.Scheme

        // Every type argument, not only the constrained ones: a copy with no
        // type parameters left needs no type arguments at the call site, and so
        // no agreement about the order `generalize` put them in.
        if
            constraints.IsEmpty
            || schemeVars.Length <> tArgs.Length
            || tArgs.IsEmpty
            || not (List.forall ground tArgs)
        then
            None
        else

        let resolved = tArgs |> List.map (prune env.Registry)

        match traverse typeKey resolved with
        | Some keys ->
            Some
                { Callee = name
                  CopyName = copyName name keys
                  Bindings = List.zip schemeVars resolved }
        | None -> None
    | None -> None

/// The instantiations the written program calls for.
///
/// A mention that is not a call is passed over. `Lowering` eta-expands one back
/// into call position and supplies its dictionary there, so it is correct as it
/// stands; specialising it is a separate rewrite at a different node.
let private collect (env: Env) (cands: Map<string, Candidate>) (decls: TDecl list) : Map<string, Demand> =
    let step (acc: Map<string, Demand list>) (expr: TypedExpr) =
        match expr.Node with
        | TApply({ Node = TIdent(name, tArgs) }, _, _) ->
            match demandOf env cands name tArgs with
            | Some d ->
                let seen = Map.tryFind d.CopyName acc |> Option.defaultValue []

                if seen |> List.exists (fun other -> other.Bindings = d.Bindings) then
                    acc
                else
                    Map.add d.CopyName (d :: seen) acc
            | None -> acc
        | _ -> acc

    // A name claimed by two instantiations is dropped rather than given to
    // either. Reachable only through the hash a long key falls back to, and the
    // alternative is not a missed copy but a wrong one: both call sites would
    // be pointed at whichever of the two was generated.
    decls
    |> List.fold (TypeVisitor.foldDecl step) Map.empty
    |> Map.toList
    |> List.choose (function
        | copyName, [ single ] -> Some(copyName, single)
        | _ -> None)
    |> Map.ofList

// ---------------------------------------------------------------------------
// Generation
// ---------------------------------------------------------------------------

/// The copy's declarations, checked, or `None` if it could not be made.
///
/// Best-effort throughout, and deliberately: a body that will not check at a
/// concrete type is a bug in this pass, and the generic function it was copied
/// from is always a correct answer. Failing the build over an optimisation
/// would report a line the reader did not write.
let private generate (env: Env) (cand: Candidate) (demand: Demand) : (Env * TDecl list) option =
    try
        let subst = demand.Bindings |> List.map (fun (v, t) -> bareVar v, t) |> Map.ofList
        let specialise = specialiseType env.Registry cand.SigRange subst

        // The arrow is rebuilt rather than resolved whole, so that the copy's
        // signature keeps the shape `checkDecl` reads parameter types off. A
        // signature that is not one — a `defun` given a type alias to an arrow
        // — is declined; the copy has no parameter list to take from it.
        //
        // Keyword and rest positions are empty by construction: a definition
        // with either is not a candidate.
        let signature =
            match cand.Signature with
            | TArrow(mandatory, [], None, ret, colour, r) ->
                match traverse specialise mandatory, specialise ret with
                | Some m, Some ret -> Some(TArrow(m, [], None, ret, colour, r))
                | _ -> None
            | _ -> None

        match signature with
        | None -> None
        | Some signature ->

        // The recursive call. Without this the copy calls the generic original,
        // which specialises the first iteration and dispatches every one after
        // it — and a constrained function that recurses is exactly the shape
        // this pass exists for.
        //
        // Sound because a recursive occurrence is bound monomorphically, so it
        // is at the enclosing function's own type variables and therefore at
        // precisely this instantiation.
        let body = AlphaRename.renameFree (Map.ofList [ cand.Name, demand.CopyName ]) cand.Body

        // An imported body is checked under the module it was *written* in, not
        // the one it is landing in: it may name something its own module is
        // allowed to name and this one is not. `TraitInline` does the same at a
        // splice, and for the same reason.
        let checkEnv =
            match cand.Origin with
            | Some origin -> { env with CurrentModule = origin }
            | None -> env

        let checked', typed =
            Inference.checkAddendum
                checkEnv
                [ DSignature(demand.CopyName, signature, [], cand.SigRange)
                  DDefun(demand.CopyName, cand.Args, body, Ordinary, cand.DefRange) ]

        // Free names now say which module they came from. The copy is emitted
        // in *this* module's class, so a bare `helper` in it would bind to
        // whatever this module calls `helper`.
        let typed =
            typed
            |> List.map (TypeVisitor.mapDecl (AlphaRename.applyQualification cand.Qualification))

        Some({ checked' with CurrentModule = env.CurrentModule }, typed)
    with ex ->
        Diagnostics.warn
            $"could not specialise '%s{cand.Name}' at %s{Lexer.formatPos cand.DefRange}: %s{ex.Message}. Calls to it keep passing a dictionary."

        None

// ---------------------------------------------------------------------------
// Redirection
// ---------------------------------------------------------------------------

/// Points every call that asked for a copy at the copy that was made.
///
/// The callee's node keeps the type it already had: instantiation left it as
/// the arrow at exactly these type arguments, which is the copy's own type. The
/// type arguments themselves go, the copy having no parameters to take them.
let private redirect (env: Env) (cands: Map<string, Candidate>) (made: Map<string, Demand>) (expr: TypedExpr) : TypedExpr =
    match expr.Node with
    | TApply(({ Node = TIdent(name, tArgs) } as target), args, kwArgs) ->
        match demandOf env cands name tArgs with
        // The instantiation is compared as well as the name: a copy whose name
        // was claimed by two of them is generated for neither, and this call
        // may be the other one.
        | Some d when (match Map.tryFind d.CopyName made with
                       | Some kept -> kept.Bindings = d.Bindings
                       | None -> false) ->
            { expr with Node = TApply({ target with Node = TIdent(d.CopyName, []) }, args, kwArgs) }
        | _ -> expr
    | _ -> expr

/// Puts the generated declarations where the module being compiled will emit
/// them.
let rec private appendToLastModule (generated: TDecl list) (decls: TDecl list) : TDecl list =
    match List.tryLast decls with
    | Some(TModule(name, inner, r)) ->
        (decls |> List.take (decls.Length - 1)) @ [ TModule(name, inner @ generated, r) ]
    | _ -> decls @ generated

// ---------------------------------------------------------------------------
// The pass
// ---------------------------------------------------------------------------

/// Records this module's own candidates, so that `Exports` can publish their
/// bodies and an importer can make the copies *it* needs.
///
/// The qualification is computed exactly as `Pipeline.qualifyInlineTemplates`
/// computes a template's, off the same `moduleOf` map, and for the same reason:
/// a body spliced into another module has to keep meaning what it meant here.
///
/// The recursive occurrence is qualified along with everything else and is
/// never used: a copy renames it to the copy's own name before checking, on
/// both sides of the boundary. It costs one map entry to leave it in and a case
/// to take it out.
let private publish
    (moduleOf: Map<string, string * string>)
    (ownModuleName: string)
    (cands: Map<string, Candidate>)
    (env: Env)
    : Env =
    let bodies =
        cands
        |> Map.filter (fun _ c -> c.Origin.IsNone)
        |> Map.map (fun _ c ->
            let params' = mandatoryNames c.Args

            let qualification =
                AlphaRename.freeNames (Set.ofList params') c.Body
                |> Seq.choose (fun n ->
                    // A name with no module class of its own — a data
                    // constructor, a `Prelude` binding, a trait method — is
                    // left as written. There is nothing to qualify it to.
                    match Map.tryFind n moduleOf with
                    | Some(m, original) -> Some(n, Naming.qualifiedBinding m original)
                    | None -> None)
                |> Map.ofSeq

            { Params = params'
              Body = c.Body
              Qualification = qualification
              OriginModule = ownModuleName }
            : InlineTemplate)

    { env with
        Registry =
            { env.Registry with
                ConstrainedBodies =
                    bodies |> Map.fold (fun acc k v -> Map.add k v acc) env.Registry.ConstrainedBodies } }

/// Specialises every constrained generic the written program calls at a ground
/// instantiation.
///
/// `decls` is the untyped program — the copies are made from its bodies — and
/// `typed` is the checked one, which is where the demand and the call sites
/// are. `moduleOf` says which module each top-level name belongs to, which is
/// what a published body's free names are qualified with.
let run
    (env: Env)
    (moduleOf: Map<string, string * string>)
    (decls: Decl list)
    (typed: TDecl list)
    : Env * TDecl list =
    let ownModuleName, _ = ownModule decls
    let cands = candidates env decls

    // Before the early exits below: what this module publishes is what an
    // importer may copy, and that does not depend on whether this module
    // happened to call any of it itself.
    let env = publish moduleOf ownModuleName cands env

    if Map.isEmpty cands then
        env, typed
    else

    let demands = collect env cands typed

    if Map.isEmpty demands then
        env, typed
    else

    // Every copy is made before any call is pointed at one: a demand that
    // cannot be met leaves its call sites alone, so redirection has to know
    // which copies exist rather than which were wanted.
    let env, made, generated =
        demands
        |> Map.toList
        |> List.fold
            (fun (env, made, generated) (copyName, demand) ->
                match generate env (Map.find demand.Callee cands) demand with
                | Some(env, decls) -> env, Map.add copyName demand made, generated @ decls
                | None -> env, made, generated)
            (env, Map.empty, [])

    if Map.isEmpty made then
        env, typed
    else

    // The generated declarations are added *after* the rewrite and are not
    // themselves rewritten. That is the whole of "depth one": a call inside a
    // copy is a call to whatever the original called, dictionary and all.
    let rewritten =
        typed
        |> List.map (TypeVisitor.mapDecl (TypeVisitor.mapExpr (redirect env cands made)))

    env, appendToLastModule generated rewritten
