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

/// Typed holes: `_`, or a `_name` nothing binds, written where an expression
/// goes.
///
/// A hole is inferred as a fresh metavariable, so the code around it decides
/// what it has to be. It is always an error: `Traits.solvePending` settles the
/// declaration as far as it goes and then refuses it with `describe` for each
/// hole, which says what the hole stands for and which bindings in scope would
/// do there.
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
      /// The scope at the hole, which is where what fits it is looked for.
      Env: Env }

/// The holes of the declaration being checked. Emptied by the refusal that
/// reports them.
let internal pending = ResizeArray<Hole>()

let clear () : unit = pending.Clear()

/// The names the declaration being checked binds, set by `checkDeclGroup`.
/// Its own name is bound in its body for recursion, and is listed among the
/// module's bindings rather than the hole's locals.
let mutable declaring: Set<string> = Set.empty

/// Whether `name`, written as an expression, is a hole.
///
/// `_` binds nothing anywhere, so it always is. `_name` is also an ordinary
/// name, so it is a hole only where nothing binds it or spells something by
/// it. One with a `.` in it is a member access, and one with a `/` or a `:` is
/// a qualified name.
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

/// The metavariables of the holes' types, held from generalization. A local
/// function with a hole in it then stays monomorphic, so that its uses decide
/// the hole's type.
let heldMetaIds () : Set<int> =
    pending |> Seq.collect (fun h -> metaIdsOf h.Type) |> Set.ofSeq

// --- What fits a hole ---
//
// A binding fits when its type and the hole's unify and what its where clause
// asks holds there. Unification here binds nothing: it answers a
// substitution, so trying every binding in scope leaves the declaration's
// types as they were.

/// The prefix a candidate's own type variables carry while it is tried: they
/// may become anything, and the hole's own variables are rigid.
let private flexMark = "\u0001"

/// Compared by character: `StartsWith` with a string compares by culture,
/// which ignores a control character, so every name would start with this one.
let private isFlex (v: string) = v.Length > 0 && v[0] = flexMark[0]

/// What may still be chosen: a candidate's own variable by its name, or an
/// open metavariable of either side by its id.
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

/// `t` with everything `s` chose put in.
let rec private applied (s: Map<string, HMType>) (t: HMType) : HMType =
    match walk s t with
    | TCon(n, xs) -> TCon(n, List.map (applied s) xs)
    | TTuple xs -> TTuple(List.map (applied s) xs)
    | TFun(xs, r, e) -> TFun(List.map (applied s) xs, applied s r, e)
    | TAssoc(tn, an, impl) -> TAssoc(tn, an, applied s impl)
    | other -> other

let rec private mentions (key: string) (t: HMType) : bool =
    match t with
    | _ when flexKey t = Some key -> true
    | TCon(_, xs)
    | TTuple xs -> List.exists (mentions key) xs
    | TFun(xs, r, _) -> List.exists (mentions key) xs || mentions key r
    | TAssoc(_, _, impl) -> mentions key impl
    | _ -> false

let rec private settledEffect (e: Effect) : Effect =
    match e with
    | EMeta { EValue = Some inner } -> settledEffect inner
    | other -> other

/// Only an ordinary function against a bjoroutine is refused: every other
/// pair can still meet, as `unifyEffect` lets it.
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
    | _ -> false

/// An associated type whose implementor `s` has made known, as that
/// implementation says it is. One still undecided is left as it is.
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
            // A part that is an associated type last, once the others may have
            // said what its implementor is, as `unify` does.
            | TFun(xs, r, e), TFun(ys, q, f) when xs.Length = ys.Length && effectsMeet e f ->
                let ready, waiting =
                    List.zip (xs @ [ r ]) (ys @ [ q ])
                    |> List.partition (fun (x, y) -> not (hasAssoc x || hasAssoc y))

                (ready @ waiting) |> List.fold (fun s (x, y) -> meet s x y) s
            | TVar v, TVar w when v = w -> s
            | TAssoc(tn, an, i), TAssoc(tm, am, j) when tn = tm && an = am -> meet s i j
            | _ -> None

let private isOpen (t: HMType) : bool =
    not (List.isEmpty (metaIdsOf t)) || typeVarsOf t |> List.exists isFlex

/// Several answers as one: refused if any refuses, held if all hold, and
/// otherwise not known yet.
let private allOf (answers: bool option list) : bool option =
    if List.contains (Some false) answers then Some false
    elif List.forall ((=) (Some true)) answers then Some true
    else None

/// Whether `traitName` holds at `t`, a type with a candidate's choices put
/// in: `None` while `t` is open where the answer depends on it.
///
/// At a variable of the enclosing signature a constraint is taken into the
/// function's own where clause, so it holds, unless it is a .NET interface:
/// such a constraint has to be written in the signature, and whether it is
/// is not known here.
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

/// Binding key -> the name a program writes it as, where the two differ: a
/// constructor and a trait method are bound under their keys and spelled
/// through `ImportAliases`. A def is bound under the name it is written as.
let private spellingsOf (registry: TraitRegistry) : Map<string, string> =
    registry.ImportAliases
    |> Map.toSeq
    |> Seq.filter (fun (visible, alias) ->
        visible <> alias.OriginalName && alias.Kind <> AliasDef && alias.Kind <> AliasMacro)
    |> Seq.groupBy (fun (_, alias) -> alias.OriginalName)
    |> Seq.map (fun (key, spellings) -> key, spellings |> Seq.map fst |> Seq.minBy (fun v -> v.Length, v))
    |> Map.ofSeq

/// How many fits are named; the rest are counted.
let private fitsShown = 6

/// The bindings in scope that fit a hole of type `t`, as the program writes
/// them: the hole's own scope first, then the module's and the builtins, then
/// the imports, and within each the least generic first. `waiting` is what the
/// code around the hole asks of its type.
///
/// What the compiler made rather than the program is left out: a generated
/// copy, a qualified name and anything else with no spelling of its own, as
/// are what is deprecated or failed to check. So is a binding of every type,
/// such as the stand-in for a declaration that failed: it fits anything and
/// says nothing.
///
/// With `localOnly`, only the hole's own scope is searched.
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
        // A spelling a binding in scope has taken means that binding here.
        | Some visible when Map.containsKey visible env.Bindings -> None
        | Some visible -> Some visible
        | None when key.Contains "." || key.Contains "::" || key.Contains "__" || key.StartsWith "#" -> None
        | None -> Some key

    let isLocal (key: string) (binding: Binding) =
        not (Set.contains key declaring)
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

    // A trait method is bound with no constraint: a use of it dispatches,
    // and that is what asks for the trait, at the scheme's first variable,
    // and for the member's own where clause.
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

            // What the binding asks has to be known to hold; what the hole is
            // asked may still be open, as it is for any value of the type.
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

/// A hole, for a tool that offers what could fill it: its type as shown, the
/// bindings in scope that fit, ranked as the refusal ranks them, the
/// constructors of its type written with holes for what they take, and for a
/// function type how many parameters it takes.
type Fillers =
    { Range: Range
      Type: string
      Fits: string list
      Constructors: string list
      Parameters: int option }

/// Called with each hole as it is refused.
let mutable observe: (Fillers -> unit) option = None

/// The values of type `t` the code at `h` may build directly, each with a hole
/// for what it takes: a union's cases, a record with each field, a newtype
/// around its value. Nothing for a type whose representation is hidden there.
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

/// The refusal of a hole: where it is, what it stands for, and what in scope
/// would do there. `waiting` is every constraint the declaration raised, with
/// the type it is asked at; those that mention the hole's type are what the
/// code around it asks of it.
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

    // A type that names no type, such as `(-> ?a ?b)`, is fitted by nearly
    // everything at module level, so only the hole's own scope is searched.
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

    observe
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
