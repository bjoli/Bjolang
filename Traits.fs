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
    openLiterals |> Seq.collect (fun (t, _, _) -> metaIdsOf t) |> Set.ofSeq

do Unification.heldMetaIds <- fun () -> Set.union (heldWanteds InlineTrait ()) (heldLiterals ())
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
let clearWanteds () : unit = wantedQueue.Clear()

/// Instantiates an impl's target pattern, giving fresh metas to the impl's own
/// prefix variables.
///
/// Returns the prefix to unify the hole against, and — separately — the metas
/// standing for the class's *type parameters*. The two are not the same list:
/// `impl Show for (List int)` has a one-argument prefix and no type parameters
/// at all, and naming `Show_List<int>` for it is a type error in C#.
let private instantiateImplPrefix (target: ImplTarget) : HMType list * HMType list =
    let vars = target.FixedPrefix |> List.collect typeVarsOf |> List.distinct
    let subst = vars |> List.map (fun v -> v, freshMeta ()) |> Map.ofList
    let prefix = target.FixedPrefix |> List.map (substTypeVars subst)
    prefix, vars |> List.map (fun v -> subst[v])

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
    if (match Map.tryFind w.Trait registry.Traits with
        | Some info -> info.ClrConstraint.IsSome
        | None -> false) then
        false
    else

    let ctorOpt =
        w.HoleArgs
        |> List.tryPick (fun (m, _) ->
            match prune registry m with
            | TCon(ctor, _) -> Some ctor
            | TTuple args -> Some(tupleCtor args.Length)
            | _ -> None)

    match ctorOpt with
    // A hole that is still a metavariable, or a rigid `TVar`, never reaches a
    // lookup at all: it falls through to the dictionary path. That is what makes
    // the blanket fallback below safe — see the comment on it.
    | None -> false
    | Some ctor ->
        match Map.tryFind (w.Trait, ctor) registry.ImplTargets with
        | None ->
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
            match Map.tryFind w.Trait registry.BlanketImpls with
            | Some _ ->
                let hole =
                    match w.HoleArgs with
                    | (m, _) :: _ -> prune registry m
                    | [] ->
                        failwithf
                            $"Internal error: trait obligation for '%s{w.Trait}' has no implementor at %s{Lexer.formatPos w.Range}"

                w.Ref.Resolved <- Some(BlanketCtor, [ hole ])
                true
            | None ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos w.Range}: no implementation of trait '%s{w.Trait}' for '%s{Naming.showTypeName ctor}', required by '%s{w.Method}'."
        | Some target ->
            let prefix, classTypeArgs = instantiateImplPrefix target

            for (m, occArgs) in w.HoleArgs do
                unify registry m (implTargetType ctor (prefix @ occArgs))

            w.Ref.Resolved <- Some(ctor, classTypeArgs |> List.map (prune registry))
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
/// The check has to be here rather than left to unification: a bare literal is
/// a metavariable, so `(: greeting string) (def greeting 5)` unified rather
/// than failing, and the mismatch surfaced as C# that would not compile.
let private defaultNumericLiterals (env: Env) : unit =
    for (t, text, r) in openLiterals do
        match prune env.Registry t with
        // Nothing said what it is, so it is an `int` — which is what a literal
        // written without a suffix has always been.
        | TMeta m -> m.Value <- Some TypeConstants.intType
        // A type variable. The literal is emitted through the implementor's own
        // `CreateChecked`, and the `Num` that `collectTraitConstraints` reads
        // off it is what makes that legal.
        | TVar _ -> ()
        | numeric when NumericLiteral.isNumeric numeric -> checkLiteralFits numeric text r |> ignore
        | other ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: these types do not match. '%s{text}' is a number, and it is being used where a '%s{DotNetInterop.showType other}' is wanted.%s{dynPackHintFor other}"

    openLiterals.Clear()

/// The pin equations a constrained member's instantiation raised — one per
/// `#:assoc` in its `(where ...)`, per call. Each says "this associated-type
/// projection equals that type"; both sides may still be metavariables when
/// queued, so they wait in line until resolution has fed them, exactly as a
/// wanted does.
let private pinEquations = ResizeArray<HMType * HMType * string * Lexer.Range>()

/// Drops whatever pin equations are still queued; see `clearWanteds`.
let clearPinEquations () : unit = pinEquations.Clear()

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

/// Solves everything raised since the last call. Used at every point that is
/// about to generalize, since a scheme must not be built over a constructor
/// that resolution would still have pinned down.
///
/// Literals first. A trait obligation dispatches on its implementor, and one
/// that is still an open literal — `(= 1 2)` — resolves to nothing at all, so
/// the queue has to be drained after the numbers have said what they are.
/// Pin equations last, after resolution has fed them.
let solvePending (env: Env) : unit =
    defaultNumericLiterals env
    solveWanteds env (takeWanteds ())
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
let registerDynImpl (traitName: string) (info: TraitInfo) (env: Env) : Env =
    match info.DynSafe with
    | Error _ -> env
    | Ok() ->
        let key = Naming.dynTypeName traitName info.AssociatedTypes
        // One type variable per associated type, named after the associated type.
        let vars = info.AssociatedTypes |> List.map (fun a -> TVar("'" + a))

        let implTarget =
            { Ctor = key
              FixedPrefix = vars
              HoleArity = 0
              // Unconditional: the box already contains its dictionary, so
              // static `Instance` is used.
              Constraints = [] }

        addImplementation
            traitName
            key
            (TCon(key, vars))
            implTarget
            (List.zip info.AssociatedTypes vars |> Map.ofList)
            env

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
let internal traitMethodArity (env: Env) (methodName: string) : int option =
    match Map.tryFind methodName env.Registry.TraitMethods with
    | None -> None
    | Some traitName ->
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

            let vars =
                let sigVars = freeTVars env.Registry withAssoc

                let constraintVars =
                    memberCs
                    |> List.collect (fun c ->
                        freeTVars env.Registry c.TargetType
                        @ (c.Pins |> List.collect (fun (_, t) -> freeTVars env.Registry t)))

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

/// Instantiates a record type with fresh type variables.
///
/// The record type and its field types have to be instantiated under the *same*
/// substitution, or a field's type variable would be unrelated to the one in the
/// record type it came from. Returns the instantiated record type, the declared
/// fields as written, and the field types under that substitution.
let internal instantiateRecord
    (registry: TraitRegistry)
    (recordTypeName: string)
    : HMType * (string * HMType) list * Map<string, HMType> =

    let tArgs, expectedFields = Map.find recordTypeName registry.Records

    // The names are used exactly as they were registered, leading quote and
    // all. Trimming it here bound the scheme over `a` while the field types
    // resolved to `'a`, so the substitution matched nothing and a generic
    // record's fields came back still holding the declaration's own variables.
    let recordScheme = Scheme(tArgs, [], TCon(recordTypeName, tArgs |> List.map TVar))

    let instantiatedRecordType, freshVars, _ = instantiate registry recordScheme
    let fieldSubst = List.zip tArgs freshVars |> Map.ofList

    let expectedFieldsInstantiated =
        expectedFields |> List.map (fun (n, t) -> n, substTypeVars fieldSubst t) |> Map.ofList

    instantiatedRecordType, expectedFields, expectedFieldsInstantiated

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
let internal recordTypeOfField
    (registry: TraitRegistry)
    (targetType: HMType)
    (field: string)
    (r: Range)
    : string =

    match prune registry targetType with
    | TCon(name, _) when Map.containsKey name registry.Records -> name
    // Answered here rather than by the fallback below, which would go looking
    // for another owner of the field name and report that none has it. The type
    // is known; what is missing is its fields, and they are missing on purpose.
    | TCon(name, _) when Set.contains name registry.OpaqueTypes ->
        failwithf
            $"Type Error at %s{formatPos r}: '%s{field}' cannot be read here.%s{opaqueTypeNote registry name}"
    | _ ->
        match Map.tryFind field registry.RecordFields |> Option.defaultValue [] with
        | [ only ] -> only
        | [] ->
            failwithf
                $"Type Error at %s{formatPos r}: no record or struct type has a field named '%s{field}'.%s{hiddenMemberNote registry field}"
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

