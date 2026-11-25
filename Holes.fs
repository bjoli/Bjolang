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

/// Typed holes: `_`, or a `_name` in expression position.
///
/// A hole is inferred as a fresh metavariable, so the surrounding code decides
/// its type. Holes always produce a compile error: `Traits.solvePending` solves
/// as much of the declaration as it can, then uses `describe` to report what
/// the compiler expects.
module Bjolang.Holes

open Bjolang.Lexer
open Bjolang.Ast
open Bjolang.TypedAST
open Bjolang.Unification
open Bjolang.TypeEnv

type Hole =
    { Written: string
      Type: HMType
      Range: Range
      /// The environment used to try to figure out what fits the hole.
      Env: Env }

/// Pending holes for the declaration currently being checked. Cleared when
/// reporting errors.
let internal pending = Compilation.PerList<Hole>()

let clear () : unit = pending.Clear()

/// Names bound by the declaration currently being checked (set by
/// `checkDeclGroup`). A recursive function's own name is in scope in its body,
/// so we track it here to classify it as a module-level binding rather than a
/// local.
let declaring = Compilation.Per<Set<string>>((fun () -> Set.empty))

/// Returns true if `name` in expression position should be treated as a hole.
///
/// `_` is always a hole in expression position. `_name` is only a hole if it's
/// a simple, unbound identifier (not `__...`, not a member access or qualified
/// name, and not already bound in scope).
let isHoleName (env: Env) (name: string) : bool =
    name = "_"
    || (name.Length > 1
        && name[0] = '_'
        && name[1] <> '_'
        && name.IndexOfAny([| '.'; '/'; ':' |]) < 0
        && not (Map.containsKey name env.Bindings)
        && not (Map.containsKey name env.Registry.ImportAliases)
        && not (Map.containsKey name env.Registry.ClrExterns)
        && not (Map.containsKey name env.Escapes))

/// Metavariable IDs referenced by pending holes. These are excluded from
/// generalization so local functions containing holes stay monomorphic and
/// their call sites can constrain the hole's type.
let heldMetaIds () : Set<int> =
    pending |> Seq.collect (fun h -> metaIdsOf h.Type) |> Set.ofSeq

// --- Finding bindings that fit a hole ---
//
// A binding fits if its type unifies with the hole's type and its trait
// constraints are satisfied. Unification here is pure (returns a substitution
// map instead of mutating metavariables), so testing candidates doesn't affect
// the declaration's types.

/// Prefix used to mark a candidate binding's quantified type variables as
/// flexible during matching, while keeping the hole's own type variables rigid.
let private flexMark = "\u0001"

/// Check the first char directly: `string.StartsWith` uses culture-sensitive
/// comparison, which ignores control characters and would match every string.
let private isFlex (v: string) = v.Length > 0 && v[0] = flexMark[0]

/// Returns the substitution key if `t` is flexible (an unsolved metavariable or
/// a candidate's flexible type variable).
let private flexKey (t: HMType) : string option =
    match t with
    | TMeta { Value = None; Id = id } -> Some $"#%d{id}"
    | TVar v when isFlex v -> Some v
    | _ -> None

let rec private walk (s: Map<string, HMType>) (t: HMType) : HMType =
    match t with
    | TMeta { Value = Some inner } -> walk s inner
    | _ ->
        match flexKey t |> Option.bind (fun k -> Map.tryFind k s) with
        | Some chosen -> walk s chosen
        | None -> t

/// Recursively applies substitution `s` to `t`.
let rec private applied (s: Map<string, HMType>) (t: HMType) : HMType =
    match walk s t with
    | TCon(n, xs) -> TCon(n, List.map (applied s) xs)
    | TTuple xs -> TTuple(List.map (applied s) xs)
    | TFun(xs, r, e) -> TFun(List.map (applied s) xs, applied s r, e)
    | TAssoc(tn, an, impl) -> TAssoc(tn, an, applied s impl)
    | TNum(carrier, unit) -> TNum(applied s carrier, unit)
    | other -> other

let rec private mentions (key: string) (t: HMType) : bool =
    match t with
    | _ when flexKey t = Some key -> true
    | TCon(_, xs)
    | TTuple xs -> List.exists (mentions key) xs
    | TFun(xs, r, _) -> List.exists (mentions key) xs || mentions key r
    | TAssoc(_, _, impl) -> mentions key impl
    | TNum(carrier, _) -> mentions key carrier
    | _ -> false

let rec private settledEffect (e: Effect) : Effect =
    match e with
    | EMeta { EValue = Some inner } -> settledEffect inner
    | other -> other

/// Checks whether two effects are compatible (only sync vs. async conflicts).
let private effectsMeet (a: Effect) (b: Effect) : bool =
    match settledEffect a, settledEffect b with
    | ESync, EAsync
    | EAsync, ESync -> false
    | _ -> true

let rec private hasAssoc (t: HMType) : bool =
    match t with
    | TMeta { Value = Some inner } -> hasAssoc inner
    | TAssoc _ -> true
    | TCon(_, xs)
    | TTuple xs -> List.exists hasAssoc xs
    | TFun(xs, r, _) -> List.exists hasAssoc xs || hasAssoc r
    | TNum(carrier, _) -> hasAssoc carrier
    | _ -> false

/// Tries to resolve an associated type once `s` has determined its implementing
/// type; leaves it unchanged if it's still unresolved.
let private reduced (registry: TraitRegistry) (s: Map<string, HMType>) (t: HMType) : HMType =
    match t with
    | TAssoc _ ->
        try
            prune registry (applied s t)
        with ex when Diagnostics.isDiagnostic ex ->
            t
    | _ -> t

let rec private meet (registry: TraitRegistry) (s: Map<string, HMType> option) (a: HMType) (b: HMType) : Map<string, HMType> option =
    match s with
    | None -> None
    | Some subst ->
        let a = walk subst a |> reduced registry subst
        let b = walk subst b |> reduced registry subst
        let meet = meet registry

        match flexKey a, flexKey b with
        | Some ka, Some kb when ka = kb -> s
        | Some k, _ -> if mentions k (applied subst b) then None else Some(Map.add k b subst)
        | _, Some k -> if mentions k (applied subst a) then None else Some(Map.add k a subst)
        | None, None ->
            match a, b with
            | TCon(TypeConstants.VoidName, []), TCon(TypeConstants.UnitName, [])
            | TCon(TypeConstants.UnitName, []), TCon(TypeConstants.VoidName, []) -> s
            | TCon(n, xs), TCon(m, ys) when n = m && xs.Length = ys.Length -> List.fold2 meet s xs ys
            | TTuple xs, TTuple ys when xs.Length = ys.Length -> List.fold2 meet s xs ys
            // Unify parameter/return types that contain associated types last,
            // so earlier parameters can determine the implementing type first.
            | TFun(xs, r, e), TFun(ys, q, f) when xs.Length = ys.Length && effectsMeet e f ->
                let ready, waiting =
                    List.zip (xs @ [ r ]) (ys @ [ q ])
                    |> List.partition (fun (x, y) -> not (hasAssoc x || hasAssoc y))

                (ready @ waiting) |> List.fold (fun s (x, y) -> meet s x y) s
            | TVar v, TVar w when v = w -> s
            | TAssoc(tn, an, i), TAssoc(tm, am, j) when tn = tm && an = am -> meet s i j
            | TNum(c, u), TNum(d, v) when Units.prune u = Units.prune v -> meet s c d
            | _ -> None

let private isOpen (t: HMType) : bool =
    not (List.isEmpty (metaIdsOf t)) || typeVarsOf t |> List.exists isFlex

/// Combines tri-state constraint results: `Some false` if any failed,
/// `Some true` if all passed, otherwise `None` (undetermined).
let private allOf (answers: bool option list) : bool option =
    if List.contains (Some false) answers then Some false
    elif List.forall ((=) (Some true)) answers then Some true
    else None

/// Checks whether `traitName` holds for `t` (after applying a candidate's
/// substitution). Returns `None` if `t` is still too open to decide.
///
/// For a rigid type variable from the enclosing signature, normal trait
/// constraints are assumed to hold (they can be added to the signature's
/// `where` clause), except for .NET interfaces, which must be written
/// explicitly and can't be verified here.
let rec private holdsAt (clrHolds: string -> HMType -> bool) (registry: TraitRegistry) (traitName: string) (t: HMType) : bool option =
    match t with
    | TMeta _ -> None
    | TVar v when isFlex v -> None
    | TVar _ when Map.containsKey traitName registry.BlanketImpls -> Some true
    | TVar _ -> if isClrTrait registry traitName then None else Some true
    | ground when isClrTrait registry traitName -> if isOpen ground then None else Some(clrHolds traitName ground)
    | ground ->
        match implFor registry traitName ground with
        | Some(target, subst) ->
            target.Constraints
            |> List.map (fun c -> holdsAt clrHolds registry c.TraitName (applied Map.empty (substTypeVars subst c.TargetType)))
            |> allOf
        | None when
            Map.containsKey traitName registry.BlanketImpls
            && (match ground with
                | TFun _ -> false
                | _ -> true)
            ->
            Some true
        | None when isOpen ground -> None
        | None -> Some false

/// Maps internal binding keys to their user-facing names when they differ
/// (e.g. constructors and trait methods stored under internal keys but accessed
/// via `ImportAliases`). Regular defs are already keyed by their written name.
let private spellingsOf (registry: TraitRegistry) : Map<string, string> =
    registry.ImportAliases
    |> Map.toSeq
    |> Seq.filter (fun (visible, alias) ->
        visible <> alias.OriginalName && alias.Kind <> AliasDef && alias.Kind <> AliasMacro)
    |> Seq.groupBy (fun (_, alias) -> alias.OriginalName)
    |> Seq.map (fun (key, spellings) -> key, spellings |> Seq.map fst |> Seq.minBy (fun v -> v.Length, v))
    |> Map.ofSeq

/// Max number of matching bindings to list by name; the rest are summarized as a count.
let private fitsShown = 6

/// Finds bindings in scope that fit a hole of type `t`, returning their written
/// names ordered by: locals first, then current module / builtins, then imports
/// (and least generic first within each tier). `waiting` contains the trait
/// constraints imposed on the hole by surrounding code.
///
/// Filters out compiler-generated names, deprecated or poisoned bindings, and
/// completely unconstrained polymorphic bindings (like error placeholders) that
/// would match anything.
///
/// If `localOnly` is true, only searches the hole's local scope.
let private fitsFor
    (clrHolds: string -> HMType -> bool)
    (h: Hole)
    (t: HMType)
    (waiting: (string * HMType) list)
    (localOnly: bool)
    : string list =
    let env = h.Env
    let registry = env.Registry
    let spellings = spellingsOf registry

    let writtenAs (key: string) : string option =
        match Map.tryFind key spellings with
        // If another binding in scope shadows this name, it refers to that binding instead.
        | Some visible when Map.containsKey visible env.Bindings -> None
        | Some visible -> Some visible
        | None when key.Contains "." || key.Contains "::" || key.Contains "__" || key.StartsWith "#" -> None
        | None -> Some key

    let isLocal (key: string) (binding: Binding) =
        not (Set.contains key declaring.Value)
        && env.ImplMethod <> Some key
        && (match Map.tryFind key env.Resolved with
            | Some atModule -> not (obj.ReferenceEquals(binding, atModule))
            | None -> true)

    let tier (key: string) (written: string) (binding: Binding) =
        if isLocal key binding then
            0
        else
            match Map.tryFind written registry.ImportAliases with
            | Some alias when alias.OriginModule <> "" && alias.OriginModule <> env.CurrentModule -> 2
            | _ -> 1

    // Trait methods don't carry their own trait constraint in their scheme;
    // reconstruct the trait constraint (on the first type var) plus any
    // method-level `where` constraints.
    let methodConstraints (key: string) (vars: string list) : TraitConstraint list =
        match traitMethodOf registry key with
        | Some(traitName, methodName) when Set.contains key env.TraitMethodNames && not vars.IsEmpty ->
            let memberWheres =
                Map.tryFind traitName registry.Traits
                |> Option.bind (fun info -> Map.tryFind methodName info.MemberWheres)
                |> Option.defaultValue []

            { TraitName = traitName
              TargetType = TVar vars.Head
              Pins = [] }
            :: memberWheres
        | _ -> []

    let fits (key: string) (Scheme(vars, constraints, schemeType)) =
        let flex = vars |> List.map (fun v -> v, TVar(flexMark + v)) |> Map.ofList
        let own (t: HMType) = substTypeVars flex (applied Map.empty t)

        match meet registry (Some Map.empty) t (own schemeType) with
        | None -> false
        | Some subst ->
            let asked =
                (constraints @ methodConstraints key vars)
                |> List.map (fun c -> holdsAt clrHolds registry c.TraitName (applied subst (own c.TargetType)))

            let around =
                waiting
                |> List.map (fun (traitName, at) -> holdsAt clrHolds registry traitName (applied subst at))

            // The candidate's own constraints must definitely hold; the hole's
            // surrounding constraints just must not be violated.
            allOf asked = Some true && allOf around <> Some false

    env.Bindings
    |> Map.toList
    |> List.choose (fun (key, binding) ->
        match writtenAs key with
        | Some written when
            key <> "_"
            && not (Set.contains key Naming.eqPrivateBindings)
            && not (Map.containsKey key env.DeprecatedNames)
            && not (isOpenCaseName key)
            && not (Diagnostics.isPoisoned key)
            && (not localOnly || isLocal key binding)
            ->
            let (Scheme(vars, _, schemeType)) = binding.Scheme

            match applied Map.empty schemeType with
            | TVar v when List.contains v vars -> None
            | _ when fits key binding.Scheme -> Some(tier key written binding, vars.Length, written)
            | _ -> None
        | _ -> None)
    |> List.sort
    |> List.map (fun (_, _, written) -> written)
    |> List.distinct

/// Structured info about a hole for IDE/tooling completions: its formatted
/// type, ranked matching bindings, constructor templates (with `_` for args),
/// and parameter count if it's a function type.
type Fillers =
    { Range: Range
      Type: string
      Fits: string list
      Constructors: string list
      Parameters: int option }

/// Optional hook called with each hole's `Fillers` when errors are reported.
let observe = Compilation.Per<(Fillers -> unit) option>((fun () -> None))

/// Builds constructor templates for `t` with `_` placeholders for arguments
/// (union cases, record literals, newtypes). Returns an empty list if `t` is
/// opaque at the hole's location.
let private constructorsOf (h: Hole) (t: HMType) : string list =
    let registry = h.Env.Registry
    let spellings = spellingsOf registry

    let written (key: string) =
        match Map.tryFind key spellings with
        | Some visible -> visible
        | None ->
            match Naming.typeKeyParts key with
            | Some(_, bare) -> bare
            | None -> key

    let applied (key: string) (arity: int) =
        if arity = 0 then written key else "(" + written key + String.replicate arity " _" + ")"

    let rec deref t =
        match t with
        | TMeta { Value = Some inner } -> deref inner
        | _ -> t

    match deref t with
    | TCon(name, _) when
        Map.containsKey name registry.OpaqueTypes
        && h.Range.Module <> ""
        && not (representationVisible registry h.Range.Module name)
        ->
        []
    | TCon(name, _) ->
        match Map.tryFind name registry.Unions, Map.tryFind name registry.Records, Map.tryFind name registry.Newtypes with
        | Some(_, cases), _, _ -> cases |> List.map (fun (c, payloads) -> applied c payloads.Length)
        | _, Some(_, fields), _ ->
            [ "(" + written name + (fields |> List.map (fun (f, _) -> " (" + f + " _)") |> String.concat "") + ")" ]
        | _, _, Some _ -> [ applied name 1 ]
        | _ ->
            match name with
            | "Option" -> [ "(Some _)"; "None" ]
            | "Result" -> [ "(Ok _)"; "(Err _)" ]
            | _ -> []
    | _ -> []

/// Formats the error message for a hole, including its location, expected type,
/// relevant trait constraints from `waiting`, and matching bindings in scope.
let describe (clrHolds: string -> HMType -> bool) (waiting: (string * HMType) list) (h: Hole) : string =
    let registry = h.Env.Registry

    let settled (t: HMType) =
        try
            prune registry t
        with ex when Diagnostics.isDiagnostic ex ->
            applied Map.empty t

    let t = settled h.Type
    let own = Set.ofList (metaIdsOf t)

    let waiting =
        waiting
        |> List.map (fun (traitName, at) -> traitName, settled at)
        |> List.filter (fun (_, at) -> metaIdsOf at |> List.exists own.Contains)
        |> List.distinct

    let shown = DotNetInterop.showTypesTogether (t :: List.map snd waiting)

    let asked =
        List.zip waiting shown.Tail
        |> List.map (fun ((traitName, _), at) -> $"(%s{Naming.showTraitName traitName} %s{at})")
        |> List.distinct

    let what =
        match t, asked with
        | TMeta _, [] -> "a value of any type: nothing around it says what it has to be"
        | _, [] -> $"a value of type %s{shown.Head}"
        | _, [ one ] -> $"a value of type %s{shown.Head}, where %s{one} has to hold"
        | _, several ->
            let all = String.concat " and " several
            $"a value of type %s{shown.Head}, where %s{all} have to hold"

    // If the expected type has no concrete type names (e.g. `(-> ?a ?b)`),
    // almost every module-level function would match, so only search locals.
    let rec namesAType (t: HMType) =
        match t with
        | TMeta { Value = Some inner } -> namesAType inner
        | TCon _
        | TVar _ -> true
        | TTuple xs -> List.exists namesAType xs
        | TFun(xs, r, _) -> List.exists namesAType xs || namesAType r
        | _ -> false

    let fits =
        match t, asked with
        | TMeta _, [] -> []
        | _ -> fitsFor clrHolds h t waiting (not (namesAType t))

    observe.Value
    |> Option.iter (fun f ->
        let rec parameters t =
            match t with
            | TMeta { Value = Some inner } -> parameters inner
            | TFun(args, _, _) -> Some args.Length
            | _ -> None

        f
            { Range = h.Range
              Type = shown.Head
              Fits = fits
              Constructors = constructorsOf h t
              Parameters = parameters t })

    let fitsLine =
        let named = fits |> List.truncate fitsShown |> String.concat ", "

        match fits.Length - fitsShown with
        | _ when fits.IsEmpty -> ""
        | more when more > 0 -> $"\n  Bindings in scope that fit: %s{named}, and %d{more} more."
        | _ -> $"\n  Bindings in scope that fit: %s{named}."

    $"Hole at %s{formatPos h.Range}: '%s{h.Written}' stands for %s{what}.%s{fitsLine}"
