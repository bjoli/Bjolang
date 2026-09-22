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

module Bjolang.Annotations

open Bjolang.Lexer
open Bjolang.Ast
open Bjolang.TypedAST
open Bjolang.Unification
open Bjolang.TypeEnv

let private typeNameMap =
    Map.ofList [
        "int", TypeConstants.intType
        "byte", TypeConstants.byteType
        "short", TypeConstants.shortType
        "ushort", TypeConstants.ushortType
        "uint", TypeConstants.uintType
        "long", TypeConstants.longType
        "ulong", TypeConstants.ulongType
        "double", TypeConstants.doubleType
        "string", TypeConstants.stringType
        "bool", TypeConstants.boolType
        // `void` in a Bjolang signature is the *unit* type, not C#'s `void`.
        // `System.Void` is spelled here too because programs already write it,
        // and there is nothing else it could reasonably mean: the interop void
        // is not a type a program can hold, pass or return.
        "void", TypeConstants.unitType
        "Unit", TypeConstants.unitType
        "System.Void", TypeConstants.unitType
        // Lowercase, like the other primitives. `Char` is the canonical name
        // the type carries internally, but a signature spells it `char`.
        "char", TypeConstants.charType
        // The non-generic task is `Task`, and the long spelling is the same
        // type rather than a second one — a signature that writes it out must
        // still match what a .NET method returning a bare `Task` comes back as.
        // See `DotNetInterop.clrToNullary`.
        "System.Threading.Tasks.Task", TCon("Task", [])
    ]

let rec resolveTypeAnnotation (registry: TraitRegistry) (ptype: FType) : HMType =
    match ptype with
    | TName(name, _) ->
        if name.StartsWith("'") then
            TVar name
        else

        let name = originalName registry name

        match Map.tryFind name registry.Aliases with
            | Some (args, t) when args.Length = 0 -> t
            | Some (args, _) ->
                failwithf $"Type alias {Naming.showTypeName name} expects {args.Length} arguments, but got 0"
            | None ->
                match Map.tryFind name typeNameMap with
                | Some t -> t
                | None -> TCon(name, [])
    | TApp("->", args, _) ->
        let resolvedArgs = args |> List.map (resolveTypeAnnotation registry)
        tfun (List.take (resolvedArgs.Length - 1) resolvedArgs) (List.last resolvedArgs)
    // `(-bjo-> ...)` reaching here rather than as a `TArrow` — the nested
    // positions of an arrow are read by `parseArrowTypeInner`, which builds a
    // `TApp` for every applied form. A bjoroutine-typed *parameter* is not
    // reachable from source yet, but the metadata serializer writes what the
    // type says, so it can be read.
    | TApp("-bjo->", args, _) ->
        let resolvedArgs = args |> List.map (resolveTypeAnnotation registry)
        TFun(List.take (resolvedArgs.Length - 1) resolvedArgs, List.last resolvedArgs, EAsync)
    // `(-?-> ...)`: a parameter this signature accepts at either colour.
    //
    // `EPoly` is a *quantified* variable — one per signature, shared by every
    // occurrence — so it is `instantiate` that turns it into something
    // solvable, exactly as a `TVar` becomes a `TMeta` there. The parser has
    // already refused every position where it would mean nothing, so anything
    // reaching here is a parameter arrow or came back from metadata.
    | TApp("-?->", args, _) ->
        let resolvedArgs = args |> List.map (resolveTypeAnnotation registry)
        TFun(List.take (resolvedArgs.Length - 1) resolvedArgs, List.last resolvedArgs, EPoly)
    | TArrow(mandatory, keywords, restOpt, ret, colour, _) ->
        let mandatoryTypes = mandatory |> List.map (resolveTypeAnnotation registry)
        let keywordTypes = keywords |> List.map (fun (_, t) -> resolveTypeAnnotation registry t)
        let restArrayType =
            match restOpt with
            | Some rt -> [TCon("Array", [resolveTypeAnnotation registry rt])]
            | None -> []
        let retType = resolveTypeAnnotation registry ret
        let allArgTypes = mandatoryTypes @ keywordTypes @ restArrayType
        TFun(allArgTypes, retType, colourEffect colour)
    // (assoc Trait item 'col) — an associated type projected out of an
    // implementor. Written by the export-metadata serializer rather than by
    // hand: inside a `def/trait` an associated type is named directly.
    // `(Tuple a b)` is the tuple type, not a one-off constructor named "Tuple".
    // It is also what `serializeHMType` writes for a `TTuple`, so without this
    // no exported signature mentioning a tuple could be read back.
    | TApp("Tuple", args, _) -> TTuple(args |> List.map (resolveTypeAnnotation registry))
    | TApp("assoc", [ TName(traitName, _); TName(assocName, _); implType ], _) ->
        TAssoc(traitName, assocName, resolveTypeAnnotation registry implType)
    // `(dyn ->str)`, `(dyn Foldable #:item int)` — a trait object type.
    //
    // Resolves to a standard `TCon` whose constructor name is the unique key
    // generated by `Naming.dynTypeName`, with associated types passed positionally.
    // This allows unification, `prune`, substitution, and printing to process
    // dynamic types without special-case logic.
    | TApp("dyn", TName(writtenTrait, _) :: assocItems, r) ->
        let traitName = originalName registry writtenTrait

        let info =
            match Map.tryFind traitName registry.Traits with
            | Some i -> i
            | None ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: '%s{writtenTrait}' is not a trait in scope, so (dyn %s{writtenTrait} ...) names nothing. A bare trait name is not a type; only (dyn ...) makes one."

        match info.DynSafe with
        | Error why -> failwithf $"Type Error at %s{Lexer.formatPos r}: %s{why}"
        | Ok() -> ()

        let rec pinned items =
            match items with
            | [] -> []
            | TName(kw, _) :: t :: rest when kw.StartsWith "#:" -> (kw.Substring 2, t) :: pinned rest
            | _ ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: every associated type in a dyn type is pinned by name, as in (dyn %s{writtenTrait} #:item int)."

        let given = pinned assocItems

        let listed =
            if info.AssociatedTypes.IsEmpty then
                "it has none"
            else
                "it has " + String.concat " " (info.AssociatedTypes |> List.map (fun a -> "#:" + a))

        for (name, _) in given do
            if not (List.contains name info.AssociatedTypes) then
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: '%s{traitName}' has no associated type #:%s{name} — %s{listed}."

        for (name, count) in given |> List.countBy fst do
            if count > 1 then
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: #:%s{name} is pinned %d{count} times in this dyn type, and an associated type has one binding."

        // In declaration order, matching `TraitInfo` and generated C# interface
        // type parameter order.
        let ordered =
            info.AssociatedTypes
            |> List.map (fun assocName ->
                match given |> List.tryFind (fun (n, _) -> n = assocName) with
                | Some(_, t) -> resolveTypeAnnotation registry t
                | None ->
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: the associated type #:%s{assocName} of '%s{traitName}' is not pinned. A dyn type pins every one of them, because the box hides the type that would otherwise answer them — %s{listed}.")

        TCon(Naming.dynTypeName traitName info.AssociatedTypes, ordered)
    // `(%f int)` — a type variable applied to arguments. `HMType` has no case
    // for this and deliberately never will: giving the unifier one makes it
    // higher-order. Only an inline trait's own constructor variable may be
    // written applied, and `resolveTemplate` reads those, not this function.
    //
    // Falling through to the general case turned it into a type constructor
    // literally named `'f`, which then failed much later with a confusing
    // complaint about a missing implementation for a type nobody wrote.
    // An arrow spelled with an effect this compiler does not know.
    //
    // Only reachable from module metadata written by a *newer* compiler —
    // `serializeHMType` writes `(-bjo-> ...)` for an `EAsync` arrow, and there
    // is no source syntax for one yet. Without this the name falls through to
    // the general case and becomes a type constructor literally called
    // `-bjo->`, which then fails somewhere else entirely, complaining about a
    // missing implementation for a type nobody wrote.
    | TApp(name, _, r) when name.EndsWith "->" && name <> "->" ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: the arrow %s{name} carries an effect this compiler does not understand. The module was compiled by a newer Bjolang; rebuild it, or upgrade."

    | TApp(name, _, r) when name.StartsWith "'" ->
        failwithf
            $"Kind Error at %s{Lexer.formatPos r}: the type variable %%%s{name.TrimStart('\'')} is applied to arguments here. Bjolang has no higher-kinded type variables: only the constructor variable of an inline trait may be written applied, and only inside that trait's own signatures. A function cannot be generic over a type constructor."

    | TApp(name, args, _) ->
        let name = originalName registry name
        let resolvedArgs = args |> List.map (resolveTypeAnnotation registry)
        match Map.tryFind name registry.Aliases with
        | Some (typeParams, t) ->
            if typeParams.Length <> resolvedArgs.Length then
                // Use showTypeName to format the internal name (e.g. BjoMod.std.prelude__Map)
                // back into the user-facing name (e.g. Map).
                failwithf
                    $"Type alias {Naming.showTypeName name} expects {typeParams.Length} arguments, but got {resolvedArgs.Length}"
            let normalizeParam (p: string) = if p.StartsWith("'") then p else "'" + p
            let subst = List.zip (typeParams |> List.map normalizeParam) resolvedArgs |> Map.ofList
            substTypeVars subst t
        | None -> TCon(name, resolvedArgs)


// ---------------------------------------------------------------------------
// Inline-trait signature templates
// ---------------------------------------------------------------------------

let rec private hmToTpl (t: HMType) : TplType =
    match t with
    | TCon(n, args) -> TplCon(n, List.map hmToTpl args)
    | TVar n -> TplVar n
    | TFun(args, ret, eff) -> TplFun(List.map hmToTpl args, hmToTpl ret, eff)
    | TTuple ts -> TplTuple(List.map hmToTpl ts)
    | other ->
        failwithf $"Type error: %s{DotNetInterop.showType other} may not appear in an inline trait's signature"

/// Reads a trait signature that mentions the implementor *applied*.
///
/// The result is a `TplType`, never an `HMType`: `m` occurs at two different
/// argument lists in `bind`, and giving the unifier a case for that would make
/// it higher-order. Instantiation at an impl (see `instantiateTemplate`)
/// eliminates the hole and hands inference an ordinary first-order type.
let rec resolveTemplate (registry: TraitRegistry) (holeVar: string) (ftype: FType) : TplType =
    let go = resolveTemplate registry holeVar
    let holeName = "'" + holeVar

    match ftype with
    | TName(name, r) when name = holeName ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: the constructor variable %%%s{holeVar} must be written applied, as (%%%s{holeVar} ...)."
    | TName _ -> hmToTpl (resolveTypeAnnotation registry ftype)
    | TApp("->", args, _) ->
        let resolved = args |> List.map go
        TplFun(List.take (resolved.Length - 1) resolved, List.last resolved, ESync)
    // A callback the method takes at either colour. Only a parameter reaches
    // here — `parseType` refuses `-?->` anywhere else — and the twin derived
    // from it is where the suspending one is answered.
    | TApp("-?->", args, _) ->
        let resolved = args |> List.map go
        TplFun(List.take (resolved.Length - 1) resolved, List.last resolved, EPoly)
    // The derived twin's own parameters, read back from metadata.
    | TApp("-bjo->", args, _) ->
        let resolved = args |> List.map go
        TplFun(List.take (resolved.Length - 1) resolved, List.last resolved, EAsync)
    | TArrow(mandatory, keywords, restOpt, ret, colour, r) ->
        // An *inline* trait, specifically. An interface trait's method may be
        // declared `-bjo->`: it has a dictionary, so the colour has an emitted
        // interface slot to live in and a dispatched call can read it before it
        // knows which implementation it reached. An inline trait has neither —
        // its methods are spliced at the call site — so there is nowhere to put
        // the claim and nothing that would check it.
        if colour <> Ordinary then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: an inline trait's methods cannot be bjoroutines. An inline trait is spliced rather than dispatched, so it has no interface for the colour to be declared in. A trait over a plain implementor — one that does not apply it, like (Iterable %%s) — may declare a method -bjo->."
        if not keywords.IsEmpty || restOpt.IsSome then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: an inline trait's methods may not take keyword or rest parameters."
        TplFun(mandatory |> List.map go, go ret, ESync)
    | TApp("Tuple", args, _) -> TplTuple(args |> List.map go)
    | TApp(name, args, _) when name = holeName -> TplHole(args |> List.map go)
    | TApp(name, args, _) when name.StartsWith "'" ->
        failwithf
            $"Type Error: only the trait's own constructor variable may be applied in a signature; %s{name} is an ordinary type variable."
    | TApp(name, args, _) ->
        match Map.tryFind name registry.Aliases with
        | Some _ ->
            // An alias may expand into anything, including something that hides
            // the hole. Resolving it as an ordinary type is only sound when no
            // argument mentions the hole.
            hmToTpl (resolveTypeAnnotation registry ftype)
        | None -> TplCon(name, args |> List.map go)

/// Instantiates a template for one call site: every ordinary type variable
/// becomes a fresh meta, and every *occurrence* of the hole becomes a meta of
/// its own together with the arguments it was applied to.
///
/// The hole metas are shared with the surrounding expression, which is the whole
/// point: the call node is fully typed immediately, with the constructor still
/// unknown, and whatever later pins one of them — an argument, an enclosing
/// `bind`, a declared return type — pins the constructor.
let instantiateTemplateFresh (tpl: TplType) : HMType * (HMType * HMType list) list =
    let varMap = System.Collections.Generic.Dictionary<string, HMType>()
    let holes = ResizeArray<HMType * HMType list>()

    // One cell for every `EPoly` in the template, as `instantiate` makes one
    // for every `EPoly` in a scheme. An `EPoly` left standing reaches `unify`,
    // which refuses it: it is a quantified variable and this is what removes it.
    let mutable polyCell: Effect option = None

    let effectFor eff =
        match eff with
        | EPoly ->
            match polyCell with
            | Some cell -> cell
            | None ->
                let cell = freshEffect ()
                polyCell <- Some cell
                cell
        | other -> other

    let rec go t =
        match t with
        | TplVar n ->
            match varMap.TryGetValue n with
            | true, m -> m
            | _ ->
                let m = freshMeta ()
                varMap[n] <- m
                m
        | TplCon(n, args) -> TCon(n, List.map go args)
        | TplFun(args, ret, eff) -> TFun(List.map go args, go ret, effectFor eff)
        | TplTuple ts -> TTuple(List.map go ts)
        | TplHole args ->
            let argTypes = List.map go args
            let m = freshMeta ()
            holes.Add(m, argTypes)
            m

    let t = go tpl
    t, List.ofSeq holes

// ---------------------------------------------------------------------------
// Deferred trait resolution
// ---------------------------------------------------------------------------

/// Every type name a declaration mentions, resolved to the name it stands for.
///
/// The declaration itself outlives inference: the code generator emits a field
/// or a payload by the name written in it, and the metadata publishes it as
/// source text. Both read those names literally, so a payload written as the
/// bare `Crate` becomes `kitchen__Crate` here — where the table that knows
/// which `Crate` is meant is still at hand. Type variables are left alone.
let rec internal qualifyFTypeNames (registry: TraitRegistry) (ftype: FType) : FType =
    let qualify (n: string) = if n.StartsWith "'" then n else originalName registry n
    let go = qualifyFTypeNames registry

    match ftype with
    | TName(n, r) -> TName(qualify n, r)
    | TApp(n, args, r) -> TApp(qualify n, List.map go args, r)
    | TArrow(mandatory, keywords, restOpt, ret, colour, r) ->
        TArrow(
            List.map go mandatory,
            keywords |> List.map (fun (k, t) -> k, go t),
            Option.map go restOpt,
            go ret,
            colour,
            r
        )

