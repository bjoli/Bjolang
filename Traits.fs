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

module Bjolang.Traits

open Bjolang.Lexer
open Bjolang.Ast
open Bjolang.TypedAST
open Bjolang.Unification
open Bjolang.TypeEnv
open Bjolang.Annotations

/// A trait obligation raised by a call and discharged later.
///
/// `bind` resolves from its first argument, but `pure : 'a -> m 'a` mentions the
/// constructor only in its *result*, so any rule that reads argument zero cannot
/// see it at all. Rather than make `infer` bidirectional, the obligation is
/// simply recorded and revisited once the surrounding expression has had its say.
type Wanted =
    { Trait: string
      Method: string
      Kind: TraitKind
      /// One entry per occurrence of the hole: the meta standing for the
      /// constructor application, and the arguments it was applied to.
      HoleArgs: (HMType * HMType list) list
      /// The AST node that reads the answer back.
      Ref: TraitRef
      Range: Range }

let private wantedQueue = ResizeArray<Wanted>()

/// Wanteds that two or more implementations could still answer, because a
/// type in them is not known yet. `solvePending` tries them again after each
/// step that can tell it more, and reports those that are left.
let private undecidedWanteds = ResizeArray<Wanted>()

/// The constraints over .NET interfaces of the implementations that resolution
/// chose, each with the type it asks it at, where, and why: the `(Num %a)` of
/// `(Summing %a)` at the `string` a loop sums. Checked in `solvePending`, so
/// that the error names the use rather than the implementation's body.
let private clrObligations = ResizeArray<string * HMType * Range * string>()

/// The wanteds of .NET-interface traits whose constraint is already queued in
/// `clrObligations`, so that the solver's later passes over the same wanted do
/// not queue it again.
let private clrChecked = System.Collections.Generic.HashSet<Wanted>(HashIdentity.Reference)

/// What the declaration being checked requires at the types it uses, each
/// with where and why: the `(where ...)` of each constrained binding it refers
/// to, and of each conditional impl a resolved call uses, as `(Lane double)`
/// for `(impl (SimdShift (Simd256 %a)) (where (IntLane %a)))` at `Float64x4`.
///
/// `solvePending` settles them as it settles wanteds: the one implementation
/// that fits a type still open decides it, and while several fit the
/// obligation waits. `Lowering` checks them as well when it builds a
/// dictionary, but an inlined call has no dictionary, so without this such a
/// call compiled at a type its impl does not cover; and a type left open made
/// C# that did not compile.
let private obligations = ResizeArray<string * HMType * Range * string>()

/// The holes an unresolved obligation of `kind` is still watching.
///
/// For `InlineTrait`: a local helper written without a signature —
/// `(defun (bump fa) (fmap fa inc))` — is let-polymorphic, so its parameter's
/// metavariable used to be quantified the moment the binding was finished, long
/// before any call site said what it was. Resolution then found a rigid type
/// variable and reported that an inline-only trait cannot be used generically,
/// which was true of the type it had just been given and false of the program
/// that was written. Holding these back makes such a binding monomorphic, which
/// is the only thing it can honestly be: one use site, one constructor,
/// resolved and inlined.
///
/// For `InterfaceTrait`: held back for local bindings only. At the top level
/// such an obligation is exactly the generic-receiver case the dictionary path
/// handles, and holding it there would turn every constrained generic function
/// monomorphic — but a top-level binding drains the queue before it generalizes
/// anyway, so the two never meet.
let private heldWanteds (kind: TraitKind) () : Set<int> =
    wantedQueue
    |> Seq.filter (fun w -> w.Kind = kind && w.Ref.Resolved.IsNone)
    |> Seq.collect (fun w -> w.HoleArgs |> Seq.collect (fun (m, _) -> metaIdsOf m))
    |> Set.ofSeq

/// The metavariables an unsettled numeric literal is still watching.
///
/// Generalizing one would quantify a variable `defaultNumericLiterals` is about
/// to answer, and the answer would arrive at a type parameter nothing
/// instantiates: `(let ((n 5)) ...)` generalized to `forall t. t`, and codegen
/// then emitted the digits `5` for a local declared at that parameter. Held
/// back, such a binding is monomorphic — the only thing it can be, a literal
/// having exactly one type in the code that comes out.
let private heldLiterals () : Set<int> =
    Seq.append (openLiterals |> Seq.map (fun (t, _, _) -> t)) openNumbers
    |> Seq.collect metaIdsOf
    |> Set.ofSeq

/// The metavariables a deferred projection equation waits on. Generalizing one
/// would make the equation's implementor rigid, and it could then never be
/// known. Held back, the binding stays monomorphic and its use site says what
/// the implementor is.
let private heldProjections () : Set<int> =
    Unification.deferredProjections
    |> Seq.collect (fun (projection, _, _) -> metaIdsOf projection)
    |> Set.ofSeq

/// The metavariables an open case waits on. Generalized, a local function that
/// returns the case would give each use a type of its own, and none of them
/// would decide the case.
let private heldOpenCases () : Set<int> =
    openCases |> Seq.collect (fun c -> metaIdsOf c.Type) |> Set.ofSeq

do
    Unification.heldMetaIds <-
        fun () -> Set.unionMany [ heldWanteds InlineTrait (); heldLiterals (); heldProjections (); heldOpenCases () ]
do Unification.heldLocalMetaIds <- heldWanteds InterfaceTrait

let internal pushWanted (w: Wanted) = wantedQueue.Add w

/// Detaches everything raised so far. Callers solve what they take.
let takeWanteds () : Wanted list =
    let ws = List.ofSeq wantedQueue
    wantedQueue.Clear()
    ws

/// Drops whatever is still queued.
///
/// A successful `checkProgram` leaves the queue empty — `solvePending` is what
/// empties it — so this is for the compilation that *failed*. An obligation
/// raised before the exception is still in the queue, and the next compilation
/// in the same process would try to solve it against an environment it was
/// never about. That is a diagnostic pointing at another file entirely, which
/// is the worst shape a state leak can take.
let clearWanteds () : unit =
    wantedQueue.Clear()
    undecidedWanteds.Clear()
    Unification.deferredProjections.Clear()
    clrObligations.Clear()
    clrChecked.Clear()
    obligations.Clear()

/// Queues the check of a constraint over a .NET interface at the type a use
/// instantiated it to: the `(Num %a)` of `+` at the `string` of `(+ "s")`.
/// `solvePending` checks it once the declaration is inferred. `Lowering` checks
/// it too, but only in code that `Simplify` has kept: a use in `(if #f ...)`
/// is gone by then. A constraint on any other trait is passed over here.
let queueClrObligation (env: Env) (traitName: string) (t: HMType) (range: Range) (describe: string) : unit =
    match Map.tryFind traitName env.Registry.Traits with
    | Some { ClrConstraint = Some _ } -> clrObligations.Add(traitName, t, range, describe)
    | _ -> ()

/// Queues a constraint of a constrained binding at the type a use
/// instantiated it to, to be settled as `obligations` says. One over a .NET
/// interface is `queueClrObligation`'s, and one of an inline trait is decided
/// where its call is inlined.
let queueCallObligation (env: Env) (traitName: string) (t: HMType) (range: Range) (describe: string) : unit =
    match Map.tryFind traitName env.Registry.Traits with
    | Some { ClrConstraint = None; Kind = InterfaceTrait } -> obligations.Add(traitName, t, range, describe)
    | _ -> ()

/// Checks a CLR constraint at a concrete implementor, and produces nothing.
///
/// There is no evidence to build — that is the point of such a trait — so this
/// is called for its failure. A type variable is passed over: it becomes a C#
/// `where` clause, and the check happens again at whatever the caller
/// instantiates it to.
///
/// The message deliberately does not read like the missing-impl one below. That
/// one invites you to write an implementation; here there is none to write.
let rec checkClrConstraint
    (env: Env)
    (clr: ClrConstraintInfo)
    (traitName: string)
    (implType: HMType)
    (range: Lexer.Range)
    (describe: string)
    : unit =

    match prune env.Registry implType with
    // A rigid variable is the constrained-generic case: it becomes a C# `where`
    // clause, and the check runs again at whatever a caller instantiates it to.
    | TVar _ -> ()
    // A metavariable still unsolved *here* is not that. Inference has run and
    // generalization has turned every variable it kept into a `TVar`, so what
    // is left is a type nothing in the program ever said. Left alone it reaches
    // C# as `object`, which fails there against the interface constraint — a
    // true report of a real problem, in the wrong language.
    | TMeta _ ->
        failwithf
            $"Type Error at %s{Lexer.formatPos range}: nothing here says what type '%s{traitName}' is needed at, %s{describe}. A constraint has to be discharged at a type, and this expression never says which one — annotate it."
    // A newtype is what it wraps to .NET, which would say yes for it. It has
    // the interface only if it was given it, `#:via` a type that has it.
    | TCon(name, _) as newtype when Map.containsKey name env.Registry.Newtypes ->
        match implFor env.Registry traitName newtype with
        | Some({ Via = Some via }, subst) -> checkClrConstraint env clr traitName (substTypeVars subst via) range describe
        | _ ->
            // In the suggested code, this module's own types by the names its
            // source writes.
            let rec written (t: HMType) =
                match t with
                | TCon(n, args) -> TCon(Naming.bareTypeName env.CurrentModule n, List.map written args)
                | TTuple args -> TTuple(List.map written args)
                | TFun(args, ret, eff) -> TFun(List.map written args, written ret, eff)
                | other -> other

            let shown = DotNetInterop.showType newtype
            let target = DotNetInterop.showType (written newtype)
            let wrapped = DotNetInterop.showType (written (eraseNewtypesIn env.Registry.Newtypes newtype))

            failwithf
                $"Type Error at %s{Lexer.formatPos range}: '%s{shown}' does not satisfy '%s{traitName}', needed %s{describe}. A newtype has only what is implemented for it, and this is not: write (impl (%s{traitName} %s{target}) #:via %s{wrapped})."
    | resolved ->
        // The interface is written over the trait's own implementor variable,
        // so putting the actual implementor in is what turns
        // `(INumber %a)` into `INumber<int>`.
        let implVar =
            match Map.tryFind traitName env.Registry.Traits with
            | Some info -> "'" + info.ImplementorVar
            | None -> "'a"

        let subst = Map.ofList [ implVar, resolved ]
        let args = clr.Args |> List.map (substTypeVars subst >> prune env.Registry)

        let clrArgs = args |> List.map DotNetInterop.tryClrTypeOf

        let refuse (why: string) =
            failwithf
                $"Type Error at %s{Lexer.formatPos range}: '%s{DotNetInterop.showType resolved}' does not satisfy '%s{traitName}', needed %s{describe}. %s{why}"

        if clrArgs |> List.exists Option.isNone then
            refuse
                $"'%s{traitName}' is the .NET interface '%s{clr.InterfaceName}', and this type has no .NET counterpart to ask about."
        else

        let clrArgs = clrArgs |> List.map Option.get

        match DotNetInterop.tryResolveGenericInterface clr.InterfaceName clrArgs.Length with
        | None ->
            refuse $"'%s{clr.InterfaceName}' could not be found. The trait naming it will not work anywhere."
        | Some definition ->
            let satisfied =
                DotNetInterop.tryConstructInterface definition clrArgs
                |> Option.map (fun constructed ->
                    DotNetInterop.implementsInterface (List.head clrArgs) constructed)
                |> Option.defaultValue false

            if not satisfied then
                refuse
                    $"'%s{traitName}' is the .NET interface '%s{clr.InterfaceName}', which '%s{DotNetInterop.showType resolved}' does not implement — and cannot be made to, since the interface belongs to .NET rather than to this program."

/// Instantiates an impl's target pattern, giving fresh metas to the impl's own
/// prefix variables.
///
/// Returns the prefix to unify the hole against, and — separately — the metas
/// standing for the class's *type parameters*. The two are not the same list:
/// `impl Show for (List int)` has a one-argument prefix and no type parameters
/// at all, and naming `Show_List<int>` for it is a type error in C#.
let private instantiateImplPrefix (target: ImplTarget) : HMType list * HMType list * Map<string, HMType> =
    let vars = target.FixedPrefix |> List.collect typeVarsOf |> List.distinct
    let subst = vars |> List.map (fun v -> v, freshMeta ()) |> Map.ofList
    let prefix = target.FixedPrefix |> List.map (substTypeVars subst)
    prefix, vars |> List.map (fun v -> subst[v]), subst

/// The pin equations a constrained member's instantiation raised — one per
/// `#:assoc` in its `(where ...)`, per call. Each says "this associated-type
/// projection equals that type"; both sides may still be metavariables when
/// queued, so they wait in line until resolution has fed them, exactly as a
/// wanted does.
let private pinEquations = ResizeArray<HMType * HMType * string * Lexer.Range>()

/// Queues the pins of a constraint in an implementation's where clause, at
/// the types `subst` gives the implementation's variables: the impl of
/// `traitName` holds there only if each does.
let private queueImplPins (traitName: string) (target: ImplTarget) (c: TraitConstraint) (subst: Map<string, HMType>) (r: Lexer.Range) : unit =
    for (assocName, pinType) in c.Pins do
        pinEquations.Add(
            TAssoc(c.TraitName, assocName, substTypeVars subst c.TargetType),
            substTypeVars subst pinType,
            $"the associated type #:%s{assocName} of '%s{c.TraitName}', which the where clause of the implementation of '%s{traitName}' for '%s{Naming.showTypeName target.Ctor}' pins",
            r
        )

/// Whether an open metavariable can be the type `pattern`: anything can,
/// except that a number literal is only a number, and a real one only a number
/// that has fractions.
let private literalTakes (open': HMType) (pattern: HMType) : bool =
    not (isOpenLiteral open')
    || (NumericLiteral.isNumeric pattern
        && (not (isOpenRealLiteral open') || NumericLiteral.holdsFraction pattern))

/// The head a wanted's implementor has, and the implementations at that head
/// its arguments could still be an instance of. `None` while the implementor
/// is not known to have a head.
let private candidatesOf (registry: TraitRegistry) (w: Wanted) : (string * (ImplTarget * Map<string, HMType>) list) option =
    w.HoleArgs
    |> List.tryPick (fun (m, _) -> implHeadOf (prune registry m))
    |> Option.map (fun (ctor, args) -> ctor, implsMatching registry w.Trait ctor args literalTakes)

let private takeUndecided () : Wanted list =
    let ws = List.ofSeq undecidedWanteds
    undecidedWanteds.Clear()
    ws

/// Settles a requirement of `traitName` on the one implementation, `target`,
/// that fits it. Each hole, an occurrence of the implementor with the
/// arguments after the implementation's fixed prefix, is unified with the
/// implementation's target, and what the implementation requires is queued
/// at the types that gives it. `user` ends "the implementation of ..., which
/// ...". Answers the metavariables that stand for the class's type
/// parameters.
let private settleOn
    (registry: TraitRegistry)
    (traitName: string)
    (target: ImplTarget)
    (ctor: string)
    (holes: (HMType * HMType list) list)
    (r: Range)
    (user: string)
    : HMType list =
    let prefix, classTypeArgs, subst = instantiateImplPrefix target

    for (m, occArgs) in holes do
        unify registry m (implTargetType ctor (prefix @ occArgs))

    let describe =
        $"by the implementation of '%s{Naming.showTraitName traitName}' for '%s{Naming.showTypeName ctor}', which %s{user}"

    // A type that only a `(where (Num %a))` asks anything of is a number
    // nothing says the type of, as a bare literal is: the count of a
    // `(counting x)` that is only printed. It is an `int` unless something
    // else in the declaration says otherwise.
    for c in clrConstraints registry target do
        let t = substTypeVars subst c.TargetType

        match prune registry t with
        | TMeta _ as open' -> openNumbers.Add open'
        | _ -> ()

        clrObligations.Add(c.TraitName, t, r, describe)

    for c in dictionaryConstraints registry target do
        queueImplPins traitName target c subst r
        obligations.Add(c.TraitName, substTypeVars subst c.TargetType, r, describe)

    classTypeArgs

let private tryResolveWanted (env: Env) (w: Wanted) : bool =
    if w.Ref.Resolved.IsSome then
        true
    else

    let registry = env.Registry

    // A trait that stands for a .NET interface is never resolved to an
    // implementation, and the lookup below is not merely pointless for it but
    // wrong: `ImplTargets` is keyed by trait *name*, so a local `Num` would
    // otherwise pick up the impls of an imported trait that happens to share
    // the name, and dispatch to a class that knows nothing about it.
    //
    // Left unresolved on purpose. `TraitInline` passes an unresolved call
    // through untouched and `Lowering` turns it into the member call, which is
    // the same emission at a concrete implementor as at a generic one — so
    // there is nothing here for resolution to decide.
    //
    // Its constraint is still checked here, at the call's type, when that is
    // known by the end of the declaration: `solvePending` checks the queued
    // obligation. Left to `Lowering` alone, a call that `Simplify` removes,
    // such as one in `(if #f ...)`, was never checked, and `(+ "s")` compiled.
    if (match Map.tryFind w.Trait registry.Traits with
        | Some info -> info.ClrConstraint.IsSome
        | None -> false) then
        match w.Ref.Holes with
        | hole :: _ when clrChecked.Add w -> clrObligations.Add(w.Trait, hole, w.Range, $"to call '%s{w.Ref.Method}'")
        | _ -> ()

        false
    else

    let ctorOpt =
        w.HoleArgs |> List.tryPick (fun (m, _) -> implHeadOf (prune registry m) |> Option.map fst)

    match ctorOpt with
    // A hole that is still a metavariable, or a rigid `TVar`, never reaches a
    // lookup at all: it falls through to the dictionary path. That is what makes
    // the blanket fallback below safe — see the comment on it.
    | None -> false
    | Some ctor ->
        match implsAtHead registry w.Trait ctor with
        | [] ->
            // Level two: the blanket, if the trait has one.
            //
            // Nothing is unified here, and nothing should be. A blanket imposes
            // no structure on the implementor — that is what makes it a blanket
            // — so there is no prefix to match and the hole is left exactly as
            // it was found.
            //
            // The soundness argument is that this branch is only ever reached
            // with `ctor` in hand, which means the implementor is *ground*. A
            // type variable became a dictionary parameter further up and gets
            // filled in at the concrete instantiation site, where this same
            // choice is made again with the same answer. So a blanket can
            // legitimately differ in behaviour from a specific impl — dropping
            // a value and detaching a promise are not the same act — without
            // generic code ever baking in the wrong one.
            //
            // A function type is passed over: no blanket reaches it.
            match (if ctor = ProcedureCtor then None else Map.tryFind w.Trait registry.BlanketImpls) with
            | Some blanket ->
                let hole =
                    match w.HoleArgs with
                    | (m, _) :: _ -> prune registry m
                    | [] ->
                        failwithf
                            $"Internal error: trait obligation for '%s{w.Trait}' has no implementor at %s{Lexer.formatPos w.Range}"

                w.Ref.Resolved <- Some(blanket.Key, [ hole ])
                true
            | None ->
                // The type as the program writes it: `(Tuple long long)`, not
                // the key `Tuple2` the lookup used.
                let shown =
                    match w.HoleArgs with
                    | (m, _) :: _ -> DotNetInterop.showType (prune registry m)
                    | [] -> Naming.showTypeName ctor

                let hint =
                    if ctor = ProcedureCtor then
                        " A function type has the implementations written for Procedure and no others: a blanket implementation does not reach it."
                    else
                        persistentTwinHint registry w.Trait ctor shown

                failwithf
                    $"Type Error at %s{Lexer.formatPos w.Range}: no implementation of trait '%s{w.Trait}' for '%s{shown}', required by '%s{w.Method}'.%s{hint}"
        | atHead ->
        match candidatesOf registry w |> Option.map snd |> Option.defaultValue [] with
        // Two or more still fit. Choosing now would be a guess, so the
        // wanted waits for the types to say more.
        | _ :: _ :: _ -> false
        | [] ->
            let shown =
                match w.HoleArgs with
                | (m, _) :: _ -> DotNetInterop.showType (prune registry m)
                | [] -> Naming.showTypeName ctor

            let written =
                atHead
                |> List.map (fun t -> "'" + DotNetInterop.showType (implTargetType ctor t.FixedPrefix) + "'")
                |> String.concat " and "

            failwithf
                $"Type Error at %s{Lexer.formatPos w.Range}: no implementation of trait '%s{w.Trait}' for '%s{shown}', required by '%s{w.Method}'. It has implementations for %s{written} only."
        | [ target, _ ] ->
            let classTypeArgs = settleOn registry w.Trait target ctor w.HoleArgs w.Range $"'%s{w.Method}' uses"
            w.Ref.Resolved <- Some(target.Key, classTypeArgs |> List.map (prune registry))
            true

/// Runs the wanted queue to a fixpoint, then reports what is left.
///
/// An unsolved `InterfaceTrait` obligation is not an error: it is exactly the
/// generic-receiver case the dictionary path already handles, and leaving it
/// alone is what keeps the current semantics intact.
let solveWanteds (env: Env) (wanteds: Wanted list) : unit =
    let mutable pending = wanteds |> List.filter (fun w -> w.Ref.Resolved.IsNone)
    let mutable progress = true

    while progress && not pending.IsEmpty do
        progress <- false

        pending <-
            pending
            |> List.filter (fun w ->
                if tryResolveWanted env w then
                    progress <- true
                    false
                else
                    true)

    for w in pending do
        match candidatesOf env.Registry w with
        | Some(_, _ :: _ :: _) -> undecidedWanteds.Add w
        | _ ->

        match w.Kind with
        | InterfaceTrait -> ()
        | InlineTrait ->
            let holes = w.HoleArgs |> List.map (fst >> prune env.Registry)

            if holes |> List.exists (function TVar _ -> true | _ -> false) then
                failwithf
                    $"Type Error at %s{Lexer.formatPos w.Range}: '%s{w.Method}' cannot be used at a generic type; '%s{w.Trait}' is an inline-only trait, so there is no dictionary to pass. Give the call a concrete type, or make the caller monomorphic."
            else
                failwithf
                    $"Type Error at %s{Lexer.formatPos w.Range}: cannot determine which '%s{w.Trait}' instance '%s{w.Method}' uses here; add a type annotation. Nothing in this expression says what the constructor is — a `(do ...)` block with no `:bind` never mentions one."

/// Settles every numeric literal that nothing pinned down, and holds the rest
/// to being numbers.
///
/// Chooses the case of each open case whose type now says which union it is
/// (`TypeEnv.openCase`), and answers whether it chose any. A chosen case's
/// constructor type is unified with the use's, which may decide another.
///
/// With `final`, an open case the type has not decided is decided by scope, as
/// a name is, and one that is not even so is refused: the declaration is about
/// to generalize, and a case of no particular union has no class to be made
/// from.
let private settleOpenCases (env: Env) (final: bool) : bool =
    let unionNames (keys: string list) =
        keys
        |> List.choose Naming.unionOfCaseKey
        |> List.map Naming.showTypeName
        |> function
            | [ one ] -> one
            | several -> String.concat ", " (List.take (several.Length - 1) several) + " and " + List.last several

    let choose (c: OpenCase) (union: string) =
        match candidateOf c.Candidates union with
        | None ->
            failwithf
                $"Type Error at %s{Lexer.formatPos c.Range}: %s{c.Written} is used here as a %s{Naming.showTypeName union}, which has no case %s{c.Written}. It is a case of %s{unionNames c.Candidates}."
        | Some key ->
            if not (representationVisible env.Registry c.CodeModule union) then
                failwithf
                    $"Type Error at %s{Lexer.formatPos c.Range}: '%s{c.Written}' is a case of %s{Naming.showTypeName union}, which cannot be built or taken apart here. %s{opaqueTypeNote env.Registry union}"

            let consType, tArgs, _ = instantiate env.Registry (lookup env key).Scheme

            try
                unify env.Registry c.Type consType
            with ex when Diagnostics.needsLocation ex ->
                raise (Diagnostics.withLocation c.Range ex)

            settledCases[c.Placeholder] <- (key, tArgs)
            chooseCase c.Range c.Written key

    let rec pass (progress: bool) =
        let ready, waiting =
            List.ofSeq openCases
            |> List.partition (fun c -> (unionHeadOf env.Registry c.Type).IsSome)

        if ready.IsEmpty then
            progress
        else
            openCases.Clear()
            openCases.AddRange waiting

            for c in ready do
                choose c (unionHeadOf env.Registry c.Type).Value

            pass true

    let progress = pass false

    // What the type leaves open, the rules every other name follows decide: a
    // case of the module the use is in wins over an imported one (rule 1 of
    // MODULES.org), and an imported one over the standard library's (rule 2).
    let byScope (c: OpenCase) =
        let own = c.Candidates |> List.filter (fun k -> Naming.bareTypeName c.CodeModule k <> k)
        let notStd = c.Candidates |> List.filter (Naming.isStdModuleKey >> not)

        match own, notStd with
        | [ one ], _ -> Some one
        | [], [ one ] -> Some one
        | _ -> None

    let rec decideByScope (decided: bool) =
        match openCases |> Seq.tryFind (fun c -> (byScope c).IsSome) with
        | Some c ->
            openCases.Remove c |> ignore
            choose c (Naming.unionOfCaseKey (byScope c).Value).Value
            pass true |> ignore
            decideByScope true
        | None -> decided

    let progress = if final then decideByScope progress else progress

    if final && openCases.Count > 0 then
        let c = openCases[0]
        openCases.Clear()

        let advice =
            if c.InPattern then
                "Give the value matched a type, with a signature or as (: value Type)."
            else
                let union = (Naming.unionOfCaseKey c.Candidates.Head).Value

                let bare =
                    match Naming.typeKeyParts union with
                    | Some(_, name) -> name
                    | None -> union

                $"Write the type with it, as in (: %s{c.Written} %s{bare}), or give what it is passed to a signature."

        failwithf
            $"Type Error at %s{Lexer.formatPos c.Range}: %s{c.Written} is a case of %s{unionNames c.Candidates}, and nothing here says which one this is. %s{advice}"

    progress

/// The check has to be here rather than left to unification: a bare literal is
/// a metavariable, so `(: greeting string) (def greeting 5)` unified rather
/// than failing, and the mismatch surfaced as C# that would not compile.
let private defaultNumericLiterals (env: Env) : unit =
    for (t, text, r) in openLiterals do
        match prune env.Registry t with
        // Nothing said what it is, so it is an `int`, or a `double` if a real
        // literal shares it (`literalDefault`). It has to fit there too, or C#
        // refuses the literal in the generated code.
        | TMeta m ->
            let settled = literalDefault m.Id
            m.Value <- Some settled
            checkLiteralFits settled text r |> ignore
        // A real literal at a type variable: `Num` does not say the type has
        // fractions, and `CreateChecked` would cut the fraction off.
        | TVar _ as v when NumericLiteral.isReal text ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: '%s{text}' has a fraction, and it is being used at the type parameter '%s{DotNetInterop.showType v}', which may be a whole number. Give the function a concrete type, or write the number as a whole one."
        // A type variable. The literal is emitted through the implementor's own
        // `CreateChecked`, and the `Num` that `collectTraitConstraints` reads
        // off it is what makes that legal.
        | TVar _ -> ()
        | numeric when NumericLiteral.isNumeric numeric -> checkLiteralFits numeric text r |> ignore
        // A newtype that implements `Num` is a number, and a literal is one
        // of the number type it wraps.
        | newtype when (numericNewtype env.Registry newtype).IsSome ->
            checkLiteralFits (numericNewtype env.Registry newtype).Value text r |> ignore
        // The literal is the index or key of a collection that nothing says the
        // type of: a local function that takes the collection is never called.
        | TAssoc(traitName, assocName, impl) as projection when not (List.isEmpty (metaIdsOf impl)) ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: nothing says what type '%s{text}' has. Its type is '%s{DotNetInterop.showType projection}': the %%%s{assocName} of an implementation of '%s{traitName}', and nothing says which implementation. Give the value it goes with a type, for example with a parameter annotation."
        | other ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: these types do not match. '%s{text}' is a number, and it is being used where a '%s{DotNetInterop.showType other}' is wanted.%s{dynPackHintFor other}"

    openLiterals.Clear()

    for t in openNumbers do
        match prune env.Registry t with
        | TMeta m -> m.Value <- Some TypeConstants.intType
        | _ -> ()

    openNumbers.Clear()

/// Drops whatever pin equations are still queued; see `clearWanteds`.
let clearPinEquations () : unit = pinEquations.Clear()

/// Queues the pins of a function's instantiated constraints, each an equation
/// between the projection at this use and the pinned type, as
/// `traitCallType` does for a member's.
let queuePins (name: string) (constraints: TraitConstraint list) (r: Lexer.Range) : unit =
    for c in constraints do
        for (assocName, pinType) in c.Pins do
            pinEquations.Add(
                TAssoc(c.TraitName, assocName, c.TargetType),
                pinType,
                $"the associated type #:%s{assocName} of '%s{c.TraitName}', which the where clause of '%s{name}' pins",
                r
            )

/// Solves every pin equation whose two sides have said what they are.
///
/// An equation still blocked on a metavariable stays queued — the call that
/// raised it has not resolved yet. One whose sides are settled, or rigid, is
/// unified now: a projection on a rigid variable either reduces through the
/// given equalities in `Registry.PinnedAssocs`, or unifies with the same
/// projection on the other side, or genuinely does not hold — and the error
/// then points at the call, naming the pin.
let private solvePinEquations (env: Env) : unit =
    let rec blocked t =
        match t with
        | TAssoc(_, _, TMeta _) -> true
        | TAssoc(_, _, inner) -> blocked inner
        | TCon(_, args) -> List.exists blocked args
        | TTuple args -> List.exists blocked args
        | TFun(args, ret, _) -> List.exists blocked args || blocked ret
        | _ -> false

    let pending = List.ofSeq pinEquations
    pinEquations.Clear()

    for (lhs, rhs, context, r) in pending do
        let l = prune env.Registry lhs
        let r' = prune env.Registry rhs

        if blocked l || blocked r' then
            pinEquations.Add(lhs, rhs, context, r)
        else
            try
                unify env.Registry l r'
            with ex ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: %s{context}, does not hold at this call. %s{ex.Message}"

/// Checks each deferred projection equation whose implementor is now known.
/// Answers whether it checked any, since a check can bind metavariables that
/// other obligations wait on.
///
/// One that still waits stays queued. A check can defer new equations, so the
/// queue is taken before the checks run.
let private solveDeferredProjections (env: Env) : bool =
    let pending = List.ofSeq Unification.deferredProjections
    Unification.deferredProjections.Clear()
    let mutable progress = false

    for (projection, other, site) as entry in pending do
        match prune env.Registry projection with
        | TAssoc(_, _, impl) when not (List.isEmpty (metaIdsOf impl)) ->
            Unification.deferredProjections.Add entry
        | settled ->
            progress <- true

            try
                unify env.Registry settled other
            with ex when Diagnostics.needsLocation ex ->
                match site with
                | Some r -> raise (Diagnostics.withLocation r ex)
                | None -> reraise ()

    progress

/// The implementations that fit an obligation of `traitName` at `ground`.
let private obligationCandidates (registry: TraitRegistry) (traitName: string) (ground: HMType) =
    implsFor registry traitName ground literalTakes

/// Settles what `obligations` holds, as far as the types say: that
/// `traitName` has an implementation at the type, and that what that
/// implementation requires holds there too, down to the leaves. Produces
/// nothing: the evidence is built by `Lowering` where a dictionary is needed,
/// and an inlined call needs none. Answers whether it settled any, as
/// settling one can bind metavariables that others wait on.
///
/// One at a type variable is passed over, as `checkClrConstraint` passes it
/// over: it is a constraint of the enclosing function, and is checked again
/// at each type a caller gives it. One at a metavariable, or that several
/// implementations fit, stays queued. The recursion through what an
/// implementation requires ends because each constraint is at a proper
/// subterm of the type.
let private settleObligations (env: Env) : bool =
    let registry = env.Registry
    let pending = List.ofSeq obligations
    obligations.Clear()
    let mutable progress = false

    for (traitName, t, r, describe) as entry in pending do
        match prune registry t with
        | (TCon _ | TTuple _ | TFun _) as ground ->
            match Map.tryFind traitName registry.Traits with
            | Some { ClrConstraint = Some clr } ->
                progress <- true
                checkClrConstraint env clr traitName ground r describe
            | _ ->
                match obligationCandidates registry traitName ground with
                | [ target, subst ] when target.Ctor = BlanketCtor ->
                    progress <- true

                    for c in target.Constraints do
                        queueImplPins traitName target c subst r
                        obligations.Add(c.TraitName, substTypeVars subst c.TargetType, r, describe)
                | [ target, _ ] ->
                    progress <- true
                    let ctor, args = (implHeadOf ground).Value
                    let occArgs = args |> List.skip (min target.FixedPrefix.Length args.Length)
                    settleOn registry traitName target ctor [ ground, occArgs ] r $"is used %s{describe}" |> ignore
                | [] when
                    Map.containsKey traitName registry.BlanketImpls
                    && (match ground with
                        | TFun _ -> false
                        | _ -> true)
                    ->
                    progress <- true
                | [] ->
                    let ctor, _ = (implHeadOf ground).Value

                    let others =
                        match implsAtHead registry traitName ctor with
                        | [] -> ""
                        | atHead ->
                            let written =
                                atHead
                                |> List.map (fun t -> "'" + DotNetInterop.showType (implTargetType ctor t.FixedPrefix) + "'")
                                |> String.concat " and "

                            $" It has implementations for %s{written} only."

                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: no implementation of trait '%s{Naming.showTraitName traitName}' for '%s{DotNetInterop.showType ground}', needed %s{describe}.%s{others}"
                | _ -> obligations.Add entry
        | TMeta _ -> obligations.Add entry
        | _ -> ()

    progress

/// Refuses an obligation that several implementations still fit, once
/// nothing is left to decide it, and drops the rest of the queue: one at a
/// metavariable is checked by `Lowering` at the type it ends up at.
let private refuseUndecidedObligations (env: Env) : unit =
    let left = List.ofSeq obligations
    obligations.Clear()

    for (traitName, t, r, describe) in left do
        match prune env.Registry t with
        | TMeta _ -> ()
        | ground ->
            let written =
                obligationCandidates env.Registry traitName ground
                |> List.map (fun (target, _) ->
                    match Map.tryFind (traitName, target.Key) env.Registry.Implementations with
                    | Some(written, _) -> "'" + DotNetInterop.showType written + "'"
                    | None -> "'" + target.Key + "'")
                |> String.concat " and "

            failwithf
                $"Type Error at %s{Lexer.formatPos r}: which implementation of '%s{Naming.showTraitName traitName}' is needed %s{describe} is not decided. The type is '%s{DotNetInterop.showType ground}', and the implementations for %s{written} both fit it. Say what the type is, with an annotation."

/// Solves everything raised since the last call. Used at every point that is
/// about to generalize, since a scheme must not be built over a constructor
/// that resolution would still have pinned down.
///
/// Literals first. A trait obligation dispatches on its implementor, and one
/// that is still an open literal — `(= 1 2)` — resolves to nothing at all, so
/// the queue has to be drained after the numbers have said what they are.
/// Pin equations last, after resolution has fed them.
///
/// Open cases before the literals: a case's payload types are what a literal
/// written as its payload is.
let solvePending (env: Env) : unit =
    settleOpenCases env false |> ignore
    defaultNumericLiterals env
    solveWanteds env (takeWanteds () @ takeUndecided ())

    // Resolution may have opened numbers of its own, through an impl's
    // `(where (Num %a))`. They are settled now, before this declaration
    // generalizes, and what they let resolve is resolved.
    if openNumbers.Count > 0 then
        defaultNumericLiterals env
        solveWanteds env (takeWanteds () @ takeUndecided ())

    // A deferred projection that holds can tell an obligation its implementor,
    // and a resolved obligation can tell a projection its own. A settled
    // obligation decides types that wanteds and projections wait on, and may
    // open numbers as resolution does.
    let rec settle () =
        if openNumbers.Count > 0 then
            defaultNumericLiterals env
            solveWanteds env (takeWanteds () @ takeUndecided ())

        if settleOpenCases env false || solveDeferredProjections env || settleObligations env then
            solveWanteds env (takeWanteds () @ takeUndecided ())
            settle ()

    settle ()

    // A case decided by scope decides a type the queue may wait on.
    if settleOpenCases env true then
        solveWanteds env (takeWanteds () @ takeUndecided ())
        settle ()

    // What two implementations still both fit has nothing left to decide it,
    // and the declaration is about to generalize: an open type in it would
    // become a type variable, and neither implementation is written for one.
    match takeUndecided () with
    | w :: _ ->
        let shown, written =
            match candidatesOf env.Registry w with
            | Some(ctor, candidates) ->
                let shown =
                    match w.HoleArgs with
                    | (m, _) :: _ -> DotNetInterop.showType (prune env.Registry m)
                    | [] -> Naming.showTypeName ctor

                shown,
                candidates
                |> List.map (fun (t, _) -> "'" + DotNetInterop.showType (implTargetType ctor t.FixedPrefix) + "'")
                |> String.concat " and "
            | None -> "", ""

        failwithf
            $"Type Error at %s{Lexer.formatPos w.Range}: which implementation of '%s{w.Trait}' '%s{w.Method}' uses here is not decided. Its type is '%s{shown}', and the implementations for %s{written} both fit it. Say what the type is, with an annotation."
    | [] -> ()

    refuseUndecidedObligations env

    // A type still open here is not checked: a later declaration may settle
    // it, and `Lowering` checks it again at the type it ends up at.
    let pendingClr = List.ofSeq clrObligations
    clrObligations.Clear()
    clrChecked.Clear()

    for (traitName, t, r, describe) in pendingClr do
        match prune env.Registry t, Map.tryFind traitName env.Registry.Traits with
        | (TVar _ | TMeta _), _ -> ()
        | _, Some { ClrConstraint = Some clr } -> checkClrConstraint env clr traitName t r describe
        | _ -> ()

    solvePinEquations env

/// Reads an impl's target as a pattern.
///
/// The trait's constructor variable abstracts over the *trailing* `HoleArity`
/// arguments; everything before them is fixed by this impl.
let implTargetOf
    (traitName: string)
    (info: TraitInfo)
    (targetType: HMType)
    (constraints: TraitConstraint list)
    (r: Range)
    : ImplTarget =
    match targetType with
    | TCon(ctor, args) ->
        if args.Length < info.HoleArity then
            failwithf
                $"Kind Error at %s{Lexer.formatPos r}: trait '%s{traitName}' abstracts over the last %d{info.HoleArity} argument(s) of its implementor, but '%s{ctor}' is applied to only %d{args.Length}. A constructor whose abstracted argument is not last — `Either e` in the first position — needs a newtype that flips them."

        { Ctor = ctor
          Key = (implKey targetType).Value
          Via = None
          FixedPrefix = args |> List.take (args.Length - info.HoleArity)
          HoleArity = info.HoleArity
          Constraints = constraints }

    // A blanket: `(impl (Discard %a) ...)`, which applies wherever the exact
    // head has no impl of its own. The implementor is the class's one type
    // parameter, so the prefix is the variable itself.
    //
    // Only for a first-order trait. A blanket over a trait that abstracts a
    // *constructor* would have to be written `(%f %a)` — a type variable
    // applied — which `HMType` has no case for and deliberately never will.
    | TVar _ ->
        if info.HoleArity > 0 then
            failwithf
                $"Kind Error at %s{Lexer.formatPos r}: trait '%s{traitName}' abstracts over the last %d{info.HoleArity} argument(s) of its implementor, so it cannot have a blanket implementation. A blanket names a bare type variable, and there is nothing to apply it to."

        // A blanket may not be conditional, and this is not a limitation of the
        // implementation. Its target *is* the implementor, so `(where (C %a))`
        // over it would ask a type to satisfy a constraint in order to satisfy
        // the constraint — evidence with no bottom. A conditional impl is
        // written at a constructor, where the demand lands on a smaller type.
        if not constraints.IsEmpty then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: a blanket implementation of '%s{traitName}' cannot have a where clause. Its target is the implementor itself, so the constraint would be discharged at the very type it is being proved for."

        { Ctor = BlanketCtor
          Key = (implKey targetType).Value
          Via = None
          FixedPrefix = [ targetType ]
          HoleArity = 0
          Constraints = [] }

    // A tuple, under its synthetic arity key. Nothing abstracts over a tuple's
    // trailing arguments — there is no constructor to apply — so the whole of
    // it is the fixed prefix and only a first-order trait can be implemented
    // for one.
    | TTuple args ->
        if info.HoleArity > 0 then
            failwithf
                $"Kind Error at %s{Lexer.formatPos r}: trait '%s{traitName}' abstracts over the last %d{info.HoleArity} argument(s) of its implementor, and a tuple has no constructor for it to abstract over."

        { Ctor = tupleCtor args.Length
          Key = (implKey targetType).Value
          Via = None
          FixedPrefix = args
          HoleArity = 0
          Constraints = constraints }

    | _ -> failwithf $"Trait implementations require concrete target types at %s{Lexer.formatPos r}"

/// Auto-registers the trait implementation for its own dynamic (`dyn`) type.
///
/// Registered whenever a trait is registered (both declared and imported).
///
/// No explicit `TImpl` node is emitted; `Codegen` handles it during `TTrait`
/// emission and `Exports` excludes it from metadata (importers re-derive it).
///
/// Enables standard trait calls like `(->str d)` and `(println d)` on boxed
/// values without special-casing: constraint resolution finds the entry and
/// `buildEvidence` emits `DynImpl::Instance`.
///
/// Each superclass of the trait is implemented at the dyn type too
/// (`dynSuperclasses`), by a class that forwards to the boxed value's own
/// implementation, which the box holds. A superclass declares no associated
/// types (`superclassesBoxable`).
let registerDynImpl (traitName: string) (info: TraitInfo) (env: Env) : Env =
    match info.DynSafe with
    | Error _ -> env
    | Ok() ->
        let key = Naming.dynTypeName traitName info.AssociatedTypes
        // One type variable per associated type, named after the associated type.
        let vars = info.AssociatedTypes |> List.map (fun a -> TVar("'" + a))
        let dynType = TCon(key, vars)

        let implTarget =
            { Ctor = key
              Key = (implKey dynType).Value
              Via = None
              FixedPrefix = vars
              HoleArity = 0
              // Unconditional: the box already contains its dictionaries, so
              // static `Instance` is used.
              Constraints = [] }

        let env =
            addImplementation traitName dynType implTarget (List.zip info.AssociatedTypes vars |> Map.ofList) env

        dynSuperclasses env.Registry info
        |> List.fold (fun acc superName -> addImplementation superName dynType implTarget Map.empty acc) env

/// Validates that an implementation binds every associated type declared by the
/// trait, exactly once.
///
/// Catching missing associated type bindings on declaration prevents confusing
/// late projection failures and ensures trait objects can reliably pin them.
let checkAssocBindings (traitName: string) (info: TraitInfo) (bound: string list) (r: Range) : unit =
    let listed =
        if info.AssociatedTypes.IsEmpty then
            "it declares none"
        else
            "it declares " + String.concat " " (info.AssociatedTypes |> List.map (fun a -> "%" + a))

    for name in bound do
        if not (List.contains name info.AssociatedTypes) then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: this implementation of '%s{traitName}' binds (type %%%s{name} ...), which the trait does not declare — %s{listed}."

    for (name, count) in bound |> List.countBy id do
        if count > 1 then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: this implementation of '%s{traitName}' binds %%%s{name} %d{count} times, and an associated type has one binding."

    for assocName in info.AssociatedTypes do
        if not (List.contains assocName bound) then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: this implementation of '%s{traitName}' does not bind its associated type %%%s{assocName}. Write (type %%%s{assocName} <type>) in it — %s{listed}."

/// How many parameters a trait method declares, or `None` when it declares
/// something that is not an arrow.
///
/// Read out of the trait rather than out of a binding, because an inline
/// trait's methods are not bound at all. Only the count is taken, so no
/// instantiation happens and nothing is unified.
let internal traitMethodArity (env: Env) (methodKey: string) : int option =
    match traitMethodOf env.Registry methodKey with
    | None -> None
    | Some(traitName, methodName) ->
        match Map.tryFind traitName env.Registry.Traits with
        | None -> None
        | Some info ->
            match info.Kind with
            | InlineTrait ->
                match Map.tryFind methodName info.Templates with
                | Some(TplFun(args, _, _)) -> Some args.Length
                | _ -> None
            | InterfaceTrait ->
                match Map.tryFind methodName info.Signatures with
                | Some(TFun(args, _, _)) -> Some args.Length
                | _ -> None

/// Instantiates a trait method at a call site and records the obligation.
let internal traitCallType (env: Env) (traitName: string) (methodName: string) (r: Range) : HMType * TraitRef =
    let info = Map.find traitName env.Registry.Traits

    let methodType, holeArgs, memberConstraints =
        match info.Kind with
        | InlineTrait ->
            match Map.tryFind methodName info.Templates with
            | Some tpl ->
                let t, holes = instantiateTemplateFresh tpl
                t, holes, []
            | None -> failwithf $"Internal error: '%s{methodName}' is not a method of inline trait '%s{traitName}'"
        | InterfaceTrait ->
            // Instantiated from the trait's own signature rather than from
            // whatever `methodName` happens to be bound to. Inside an `impl`
            // the method is also bound monomorphically, for recursion, and that
            // binding quantifies nothing — so reading the implementor out of a
            // scheme's type arguments found no hole at all for a self-call.
            let sigType =
                match Map.tryFind methodName info.Signatures with
                | Some t -> t
                | None -> failwithf $"Internal error: '%s{methodName}' is not a method of trait '%s{traitName}'"

            let implVar = "'" + info.ImplementorVar

            // An associated type is a projection out of the implementor, so it
            // is pinned by the same meta rather than being free on its own.
            let assocSubst =
                info.AssociatedTypes
                |> List.map (fun a -> "'" + a, TAssoc(traitName, a, TVar implVar))
                |> Map.ofList

            let withAssoc = substTypeVars assocSubst sigType

            // The member's own `(where ...)`, brought into the same variable
            // space: a pin naming one of the trait's associated types becomes
            // the projection at the same implementor variable — and so, below,
            // at the same hole this very call is dispatching on.
            let memberCs =
                match Map.tryFind methodName info.MemberWheres with
                | Some cs ->
                    cs
                    |> List.map (fun c ->
                        { c with
                            Pins = c.Pins |> List.map (fun (n, t) -> n, substTypeVars assocSubst t) })
                | None -> []

            // Without the pins of the body this call is in: they speak of
            // that body's variables, and the trait's signature has its own.
            let sigRegistry = Unification.withoutPins env.Registry

            let vars =
                let sigVars = freeTVars sigRegistry withAssoc

                let constraintVars =
                    memberCs
                    |> List.collect (fun c ->
                        freeTVars sigRegistry c.TargetType
                        @ (c.Pins |> List.collect (fun (_, t) -> freeTVars sigRegistry t)))

                implVar :: ((sigVars @ constraintVars) |> List.distinct |> List.filter ((<>) implVar))

            // Through `instantiate` rather than a substitution of its own, so
            // that a `-?->` parameter becomes the one shared cell here as it
            // does at every other use site. Left `EPoly` it reaches `unify`,
            // which refuses it: instantiation is what removes it.
            let instantiated, fresh, instantiatedCs =
                instantiate env.Registry (Scheme(vars, memberCs, withAssoc))

            // Each pin is an equation between a projection at this call and
            // the type the member pinned it to. Queued rather than unified on
            // the spot: both sides are metavariables until the surrounding
            // expression has had its say.
            for c in instantiatedCs do
                for (assocName, pinType) in c.Pins do
                    pinEquations.Add(
                        TAssoc(c.TraitName, assocName, c.TargetType),
                        pinType,
                        $"the associated type #:%s{assocName} of '%s{c.TraitName}', which the where clause of '%s{methodName}' pins",
                        r)

            // An implementor of arity zero is the hole, applied to nothing.
            instantiated, [ List.head fresh, [] ], instantiatedCs

    let tref =
        { Trait = traitName
          Method = methodName
          Holes = holeArgs |> List.map fst
          MethodType = methodType
          MemberConstraints = memberConstraints
          Resolved = None }

    pushWanted
        { Trait = traitName
          Method = methodName
          Kind = info.Kind
          HoleArgs = holeArgs
          Ref = tref
          Range = r }

    methodType, tref

/// The `#:mutable` fields of a record type, or `[]` for one that has none —
/// which includes every type that is not a record at all.
let mutableFieldsOf (registry: TraitRegistry) (recordTypeName: string) : string list =
    Map.tryFind recordTypeName registry.MutableRecordFields |> Option.defaultValue []

/// Whether `moduleName` is the module that declared the record keyed
/// `recordTypeName`, and so the only one that may write its fields.
///
/// Read off the key rather than recorded beside it: a type's key *is* its
/// declaring module and its name collapsed into one string, and `Naming.typeKey`
/// is idempotent for a name that already carries this module's prefix. So a key
/// this module built comes back unchanged and any other key grows a second
/// prefix, which is exactly the question being asked. Nothing has to guess where
/// the key divides, which `typeKeyParts` would have to.
let internal declaredHere (moduleName: string) (recordTypeName: string) : bool =
    Naming.typeKey moduleName recordTypeName = recordTypeName

/// Which record type a `record-ref` or `record-set` is talking about.
///
/// The target's own type answers that whenever it is known — which is every
/// place the value was constructed, annotated, or already unified with a record
/// somewhere upstream. Only in a genuinely generic context, where the target is
/// still an unresolved meta variable, is there nothing to go on, and the field
/// name is consulted instead. That fallback is a guess, so it is only allowed to
/// stand when exactly one record type declares the name: silently picking one of
/// several is how a field name shared by two records used to make one of them
/// unreachable.
///
/// `codeModule` is the module whose code the access is, and `verb` what it does
/// to the field ("read", "updated", "written"). An `#:opaque` record whose
/// representation that code may not see is refused when the type is known, and
/// is no candidate for the fallback, so that its field names cannot make a
/// field of a visible record ambiguous.
let internal recordTypeOfField
    (registry: TraitRegistry)
    (codeModule: string)
    (verb: string)
    (targetType: HMType)
    (field: string)
    (r: Range)
    : string =
    let visible (typeKey: string) = representationVisible registry codeModule typeKey

    let refuse (typeKey: string) =
        failwithf
            $"Type Error at %s{formatPos r}: '%s{field}' cannot be %s{verb} here. %s{opaqueTypeNote registry typeKey}"

    match prune registry targetType with
    | TCon(name, _) when Map.containsKey name registry.Records ->
        if not (visible name) then refuse name
        name
    | _ ->
        let owners = Map.tryFind field registry.RecordFields |> Option.defaultValue []

        match owners |> List.filter visible with
        | [ only ] -> only
        | [] ->
            match owners with
            | hidden :: _ -> refuse hidden
            | [] -> failwithf $"Type Error at %s{formatPos r}: no record or struct type has a field named '%s{field}'."
        | many ->
            let owners = String.concat ", " many

            failwithf
                $"Type Error at %s{formatPos r}: '%s{field}' is a field of %s{owners}, and the type of the value here is not known yet. Annotate it, or give the enclosing function a signature."


/// A syntactic value, in the sense the value restriction means it.
///
/// Only these may be generalized. Generalizing anything else is unsound the
/// moment the language has a mutable cell — and it has one already, in `Array`:
///
///     (def c (make-array 1))            ;; if this were ∀a. (Array a)
///     (array-set! c 0 42)               ;; a := int
///     (string-length (array-ref c 0))   ;; a := string, same array
///
/// Both lines check, and an int is read as a string. An application is the shape
/// that can allocate such a cell, so an application is not a value however
/// innocent it looks. The recursion matters as much as the cases: a tuple or a
/// record is a value only when everything in it is, so a box nested inside one
/// is refused along with it.
///
/// A record with a `#:mutable` field is the `make-array` case arriving by a
/// second route, and is refused for the same reason — constructing one
/// allocates a cell, however syntactic the construction looks:
///
///     (type (: (Box %a) (Record (: item %a #:mutable))))
///     (def b (Box (item Nil)))         ;; if this were ∀a. (Box a)
///     (box-set! b 1)                   ;; a := int
///     (string-length (record-ref b item))  ;; a := string, same box
///
/// The registry is needed rather than the node alone because the node carries
/// the field *values*, not the declaration that says which of them are cells.
let rec isSyntacticValue (registry: TraitRegistry) (expr: TypedExpr) =
    let recur = isSyntacticValue registry

    match expr.Node with
    | TInt _
    | TString _
    | TKeyword _
    | TSymbol _
    | TLambda(_, _)
    | TIdent(_, _) -> true
    | TTupleMake es -> List.forall recur es
    | TListMake es -> List.forall recur es
    | TVecMake es -> List.forall recur es
    | TRecordMake fields ->
        let hasMutableField =
            match prune registry expr.Type with
            | TCon(name, _) -> not (mutableFieldsOf registry name).IsEmpty
            | _ -> false

        not hasMutableField && fields |> List.forall (snd >> recur)
    | _ -> false

// ---------------------------------------------------------------------------
// Foreign .NET interop
// ---------------------------------------------------------------------------

/// The effect the arrow demanded at a call site must carry.
///
/// The demand takes the callee's own effect rather than always `ESync`.
/// Otherwise every call to a bjoroutine would fail in `unifyEffect`, including
/// the legal ones — inference would be answering a question it cannot see the
/// answer to. *Whether* the yield point is somewhere it can be resumed from is
/// `ColourCheck`'s, after loop lowering has decided which bodies become members
/// of their own.
///
/// A callee whose type is still a metavariable is `ESync`: nothing has said it
/// is a bjoroutine, and unification will pin it as an ordinary arrow. That is
/// the right default, because the only way to *become* a bjoroutine is to be
/// written as one.
/// Does this body do anything with `name` that its colour changes?
///
/// Two things qualify. Calling it, where one copy awaits and the other does
/// not. Handing it to a parameter that is itself `-?->`, where the callee's
/// own two copies are selected by what it is given — which is how a wrapper
/// like `deque-fold` is written, and looks from here like merely storing it.
///
/// The callee's *declared* parameters, from the scheme rather than from the
/// node: `instantiate` freshens `EPoly` away, and in the ordinary copy the
/// fresh cell is then pinned `ESync` by the argument.
let internal usesColour (env: Env) (name: string) (body: TypedExpr) : bool =
    let declaredParams (callee: TypedExpr) =
        match callee.Node with
        | TIdent(n, _) ->
            match Map.tryFind n env.Bindings with
            | Some b ->
                let (Scheme(_, _, t)) = b.Scheme

                match t with
                | TFun(args, _, _) -> args
                | _ -> []
            | None -> []
        | _ -> []

    let handedToPoly (callee: TypedExpr) (args: TypedExpr list) =
        let declared = declaredParams callee

        args
        |> List.indexed
        |> List.exists (fun (i, a) ->
            match a.Node with
            | TIdent(n, _) when n = name ->
                match List.tryItem i declared with
                | Some(TFun(_, _, EPoly)) -> true
                | _ -> false
            | _ -> false)

    TypeVisitor.foldExpr
        (fun found e ->
            found
            || (match e.Node with
                | TApply({ Node = TIdent(n, _) }, _, _) when n = name -> true
                | TApply(callee, args, _) -> handedToPoly callee args
                | _ -> false))
        false
        body

/// Every yield point in a body, named.
///
/// Named rather than counted, because the check that reads this is a *set
/// difference*: what the suspending half of a `defbjouble` does that the
/// ordinary half does not. Two bodies that both call `read-line` and differ
/// only in a branch are not a pair of colours, however different they look.
let internal yieldPointsOfDecl (decl: TDecl) : string list =
    let found = ResizeArray<string>()

    decl
    |> TypeVisitor.foldDecl
        (fun () e ->
            match e.Node with
            | TForeignStaticCall(clrType, methodName, _, Some meta) when meta.Await ->
                found.Add $"%s{clrType}.%s{methodName}"
            | TDotMethodCall(_, methodName, _, Some meta) when meta.Await ->
                found.Add $"%s{meta.DeclaringType}.%s{methodName}"
            | TApply(target, _, _) ->
                match pruneEffect (match target.Type with
                                   | TFun(_, _, eff) -> eff
                                   | _ -> ESync) with
                | EAsync ->
                    match target.Node with
                    | TIdent(n, _) -> found.Add n
                    | _ -> found.Add "an expression"
                | _ -> ()
            // A dispatched trait method the trait declared `-bjo->`. Counted so
            // that a `defbjouble` whose suspending half reaches one is a pair of
            // colours rather than two spellings of the same body.
            | TInterfaceCall(_, mName, methodType, _, _) when callSuspends methodType -> found.Add mName
            | _ -> ())
        ()
    |> ignore

    List.ofSeq found

let internal demandedEffect (env: Env) (targetType: HMType) : Effect =
    match prune env.Registry targetType with
    | TFun(_, _, eff) -> eff
    | _ -> ESync

// ---------------------------------------------------------------------------
// Literal elaboration
//
// A literal written where a union is expected is elaborated into a constructor
// of that union: `'(pipe (ls "-l"))` at `(List ProcItem)` becomes
// `(list (ProcSym 'pipe) (ProcSub (list (ProcSym 'ls) (ProcStr "-l"))))`.
//
// Which constructor is chosen by the literal's *shape*, not by its type, and
// the difference is the whole point: a nested `(ls "-l")` has no type to be
// chosen by. Inferring it on its own allocates one element metavariable and
// unifies `Symbol` against `string`, which fails before any constructor is
// consulted. The shape is available before any of that happens.
// ---------------------------------------------------------------------------

