module Bjolang.Unification

open Bjolang.TypedAST

// --- UNIFICATION ENGINE ---
let mutable nextMetaId = 0

/// Hur många generaliserande bindningar som står öppna just nu.
///
/// Noll är toppnivån. `enterLevel` höjer den innan högerledet i en bindning som
/// ska generaliseras infereras, så att varje cell som skapas där föds med en
/// nivå som är högre än omgivningens.
let mutable private currentLevel = 0

let freshMeta () = 
    let id = nextMetaId
    nextMetaId <- nextMetaId + 1
    TMeta { Id = id; Value = None; Level = currentLevel }

/// Nivån just nu. Läses av `generalizeWith` och av `demote`.
let level () : int = currentLevel

let enterLevel () : unit = currentLevel <- currentLevel + 1
let exitLevel () : unit = currentLevel <- currentLevel - 1

/// Kör `f` en nivå in, och tar sig tillbaka även om `f` kastar.
///
/// Ett typfel avbryter hela kompileringen, men en REPL-session och en
/// beroendekompilering fortsätter i samma process — och en nivå som blivit
/// kvar uppe gör att nästa deklaration generaliserar för lite.
let atLevel (f: unit -> 'a) : 'a =
    enterLevel ()

    try
        f ()
    finally
        exitLevel ()

/// En cell som hör hemma en nivå in.
///
/// För en signatur som ställs i ordning *utanför* den deklaration som ska
/// generalisera den. En `impl`-metods egna typvariabler — `fold`s `%acc` —
/// instantieras innan `checkDecl` går in en nivå, och en cell född här ute
/// ligger på generaliseringens egen nivå och blir aldrig kvantifierad: metoden
/// tappar då en typparameter som gränssnittet kräver av den.
let freshMetaInner () : HMType = atLevel freshMeta

let mutable private nextEffectId = 0

/// A fresh, unsolved effect: what an `EPoly` becomes at a use site.
///
/// A counter of its own rather than sharing the type one, because the two are
/// never compared with each other — an effect cell and a metavariable are
/// different kinds of unknown, and one counter would only make each of them
/// depend on how many of the other happened to be made first.
let freshEffect () : Effect =
    let id = nextEffectId
    nextEffectId <- nextEffectId + 1
    EMeta { EId = id; EValue = None }

/// The counter, and a way to put it back.
///
/// A metavariable's id has to be unique within one inference run and means
/// nothing outside it — `heldMetaIds` compares ids belonging to the same run —
/// so this is per compilation. It is bracketed rather than left alone because
/// an id is reachable from a diagnostic, and a number that depends on what was
/// compiled first is a message that changes for no reason the reader can see.
let snapshotMetaCounter () : int = nextMetaId

/// Both counters, since an effect cell's id is per-run in exactly the same way.
let restoreMetaCounter (n: int) : unit =
    nextMetaId <- n
    nextEffectId <- n

/// Nivåräknaren, bracketerad för sig.
///
/// Ett beroende som byggs i samma process gör det mitt i den yttre
/// kompileringen. Sub-kompileringen börjar på noll och den yttre ska hitta
/// tillbaka till sin egen nivå.
let snapshotLevel () : int = currentLevel

let restoreLevel (n: int) : unit = currentLevel <- n

/// A binding, or the reason there is none.
///
/// The opaque arms are the whole of what an `#:opaque` type needs on the
/// expression side. A hidden constructor is simply never bound, so calling one
/// arrives here as an unbound variable like any typo. Saying which type it
/// belongs to is the difference between "this name does not exist" and "this
/// name is not yours to write".
///
/// Two arms rather than one because a record is constructed by its own type
/// name, and a type name — unlike a hidden case — does have a spelling
/// registered, so it reaches this point already resolved to its key.
let lookup (env: Env) (name: string) : Binding =
    match Map.tryFind name env.Bindings with
    | Some scheme -> scheme
    | None ->
        // Namnet som det skrevs. En nyckel bär sin namnrymd, och den är inget
        // en läsare har skrivit eller kan rätta.
        let shown = Naming.emittedTypeName name

        if Set.contains name env.Registry.OpaqueTypes then
            failwithf
                $"Unbound variable: %s{shown}. %s{Naming.showTypeName name} is exported #:opaque, so its fields did not cross the module boundary and it cannot be constructed here. The module that declares it exports the functions that build one."
        else
            match Map.tryFind name env.Registry.HiddenMembers with
            | Some typeKey ->
                failwithf
                    $"Unbound variable: %s{shown}. It belongs to %s{Naming.showTypeName typeKey}, which is exported #:opaque: the type's name crosses the module boundary and its representation does not, so a value of it can be held and passed on but not built here."
            | None -> failwithf $"Unbound variable: %s{shown}"

/// Introduce `name`, shadowing whatever it named before.
///
/// The `FunMetas` removal is the shadowing half. `FunMeta` records the *shape*
/// of a call — how many mandatory parameters, which keywords, whether there is
/// a rest parameter — and `infer` looks it up by name alone, so an entry left
/// behind belongs to a function the name no longer refers to. Binding `list`
/// or a parameter named `path-combine` locally then made the call site try to
/// spread arguments into an array the local binding has no parameter for, and
/// fail with `Cannot unify Int32 with Array<Int32>` — an error naming a type
/// the program never mentions.
///
/// Callers that introduce a binding which *is* variadic add its `FunMeta` back
/// immediately after calling this; `DDefun` and `DDef` both do.
let addBinding (name: string) (binding: Binding) (env: Env) : Env =
    { env with
        Bindings = Map.add name binding env.Bindings
        FunMetas = Map.remove name env.FunMetas
        // Whatever this name meant, it now means this. A trait method is bound
        // like anything else, so binding over one is what shadowing it *is*,
        // and the call site has to stop dispatching on the trait.
        TraitMethodNames = Set.remove name env.TraitMethodNames }

let rec prune (registry: TraitRegistry) (t: HMType) : HMType =
    match t with
    | TMeta m ->
        match m.Value with
        | Some innerT ->
            let pruned = prune registry innerT
            m.Value <- Some pruned
            pruned
        | None -> t
    | TCon(name, args) -> TCon(name, List.map (prune registry) args)
    | TFun(args, ret, eff) -> TFun(List.map (prune registry) args, prune registry ret, eff)
    | TTuple args -> TTuple(List.map (prune registry) args)
    | TAssoc(traitName, assocName, implementor) ->
        let prunedImpl = prune registry implementor

        match prunedImpl with
        // If the implementor is concrete, attempt resolution
        | TCon _
        | TTuple _
        | TFun _ ->
            match registry.ResolveAssociatedType traitName assocName prunedImpl with
            | Some resolved -> prune registry resolved
            | None ->
                failwithf $"Missing implementation of %s{traitName} for %s{DotNetInterop.showType prunedImpl}"
        // If still generic, keep deferred
        | _ -> TAssoc(traitName, assocName, prunedImpl)
    | _ -> t

/// The implementation `traitName` selects at `t`, with the substitution that
/// carries the impl's own type variables to `t`'s arguments.
///
/// The two levels resolution uses, in the same order: an exact head, then the
/// trait's blanket. A blanket's target is the bare implementor, so the
/// substitution binds its one variable to the whole type.
let implFor (registry: TraitRegistry) (traitName: string) (t: HMType) : (ImplTarget * Map<string, HMType>) option =
    // A tuple answers under its synthetic arity key, and is otherwise an
    // ordinary constructor applied to its element types.
    let headed =
        match prune registry t with
        | TTuple args -> Some(tupleCtor args.Length, args)
        | TCon(ctor, args) -> Some(ctor, args)
        | _ -> None

    match headed with
    | Some(ctor, args) ->
        let target =
            match Map.tryFind (traitName, ctor) registry.ImplTargets with
            | Some target -> Some target
            | None -> Map.tryFind traitName registry.BlanketImpls

        target
        |> Option.map (fun target ->
            let bound =
                if target.Ctor = BlanketCtor then
                    [ implTargetType ctor args ]
                else
                    args |> List.truncate target.FixedPrefix.Length

            let subst =
                List.zip (target.FixedPrefix |> List.truncate bound.Length) bound
                |> List.choose (fun (pattern, actual) ->
                    match pattern with
                    | TVar v -> Some(v, actual)
                    | _ -> None)
                |> Map.ofList

            target, subst)
    | None -> None

/// What a use of `traitName` at `t` still needs from the enclosing function.
///
/// A ground type answers nothing: the whole dictionary can be built where the
/// type is known. A type variable answers itself. Everything between is a
/// conditional impl, whose `(where ...)` is discharged one level down — so
/// `(->str (List %a))` needs whatever `(->str %a)` needs, which is a dictionary
/// parameter for `'a`.
///
/// The recursion terminates because a constraint's target is one of the impl's
/// own target variables, so the type it is asked at is always a proper subterm
/// of `t`.
///
/// One function, used by `Inference` to decide which constraints a generic
/// function carries and by `Lowering` to build the dictionaries that discharge
/// them. Were the two to disagree, a function would take a dictionary nobody
/// passes, or be passed one it does not declare.
let rec leafConstraints (registry: TraitRegistry) (traitName: string) (t: HMType) : (string * string) list =
    match prune registry t with
    | TVar v -> [ traitName, v ]
    | TCon _ as ground ->
        match implFor registry traitName ground with
        | Some(target, subst) ->
            target.Constraints
            |> List.collect (fun c -> leafConstraints registry c.TraitName (substTypeVars subst c.TargetType))
        | None -> []
    | _ -> []

let instantiate
    (registry: TraitRegistry)
    (Scheme(boundVars, constraints, t))
    : HMType * HMType list * TraitConstraint list =
    let boundVars = List.distinct boundVars

    let boundSubst =
        boundVars |> List.map (fun name -> name, freshMeta ()) |> Map.ofList

    // Positionally aligned with the scheme's own variable list, which is what a
    // caller needs to answer "what was `'c` instantiated to here?" — the
    // dictionary a trait constraint requires is chosen by that answer. Taking
    // them out of the map instead ordered them alphabetically, so `fold`'s
    // ['col; 'acc] came back as ['acc; 'col].
    let boundFreshTypes = boundVars |> List.map (fun name -> Map.find name boundSubst)

    /// The one cell every `EPoly` in this signature becomes, made on demand.
    ///
    /// **One**, shared by every occurrence — which is the whole of what makes
    /// `-?->` cheap. A signature therefore has exactly two instantiations
    /// however many function parameters it has, so a use either wants all of
    /// them suspending or none, there is no 2^k of emitted copies, and the
    /// mixed case is written by declaring the ones that should stay ordinary
    /// with a plain `->`.
    ///
    /// Lazy so that the overwhelming majority of signatures, which contain no
    /// `EPoly` at all, allocate nothing.
    let mutable polyCell : Effect option = None

    let effectFor (eff: Effect) =
        match eff with
        | EPoly ->
            match polyCell with
            | Some cell -> cell
            | None ->
                let cell = freshEffect ()
                polyCell <- Some cell
                cell
        | other -> other

    let rec walk node =
        match prune registry node with
        | TVar name ->
            match Map.tryFind name boundSubst with
            | Some fresh -> fresh
            | None -> node
        | TFun(args, ret, eff) -> TFun(List.map walk args, walk ret, effectFor eff)
        | TCon(name, args) -> TCon(name, List.map walk args)
        | TTuple args -> TTuple(List.map walk args)
        | TAssoc(tName, aName, impl) -> TAssoc(tName, aName, walk impl)
        | _ -> node

    let instantiatedType = walk t

    let instantiatedConstraints =
        constraints
        |> List.map (fun c ->
            { c with
                TargetType = walk c.TargetType })

    instantiatedType, boundFreshTypes, instantiatedConstraints

/// Occurs-kontrollen och nivåsänkningen i samma vandring.
///
/// De vill se exakt samma noder, och `bindMeta` är den varmaste vägen genom
/// unifieringen — två vandringar över samma typ vore en för mycket.
///
/// Sänkningen är vad som håller nivåerna sanna: efter `m.Value <- Some t` är
/// varje cell i `t` nåbar överallt där `m` är nåbar, så ingen av dem får längre
/// påstå sig vara mer inkapslad än `m`. Utan detta skulle en bindning på yttre
/// nivå kunna generalisera en cell som en inre bindning delar med den.
///
/// Ingen kortslutning: `List.exists` hade hoppat över resten av argumenten så
/// fort träffen hittats, och de cellerna hade blivit kvar på fel nivå.
let rec private occursAndLower (registry: TraitRegistry) (m: MetaVar) (t: HMType) : bool =
    let anyOf (ts: HMType list) =
        ts |> List.fold (fun found node -> occursAndLower registry m node || found) false

    match prune registry t with
    | TMeta m2 ->
        if m2.Level > m.Level then
            m2.Level <- m.Level

        m.Id = m2.Id
    | TCon(_, args) -> anyOf args
    | TFun(args, ret, _) -> anyOf (ret :: args)
    | TTuple args -> anyOf args
    | TAssoc(_, _, impl) -> occursAndLower registry m impl
    | TVar _ -> false

let bindMeta (registry: TraitRegistry) (m: MetaVar) (t: HMType) =
    match t with
    | TMeta m2 when m.Id = m2.Id -> ()
    | _ ->
        if occursAndLower registry m t then
            failwith "Type error: Infinite type (occurs check failed)"
        else
            m.Value <- Some t

/// Does this type still hide an associated-type projection whose implementor is
/// unknown? `prune` resolves a projection as soon as the implementor is
/// concrete, so what is left is a projection waiting on a meta variable —
/// something else in the same call has to pin it down first.
let rec private awaitsImplementor (registry: TraitRegistry) (t: HMType) : bool =
    match prune registry t with
    | TAssoc(_, _, impl) ->
        match prune registry impl with
        | TMeta _ -> true
        | _ -> false
    | TCon(_, args)
    | TTuple args -> List.exists (awaitsImplementor registry) args
    | TFun(args, ret, _) ->
        List.exists (awaitsImplementor registry) args
        || awaitsImplementor registry ret
    | _ -> false

/// Two arrows meet only at the same effect.
///
/// The type checker strictly prevents passing a bjoroutine (coroutine) into a
/// parameter that expects a regular function. We enforce this because they
/// compile to fundamentally different things in C#: a bjoroutine returns a
/// `Fiber<T>`, whereas a regular function returns `T`. Letting one flow into a
/// position expecting the other is the higher-order restriction, and it is
/// what monomorphisation exists to lift — by emitting a second body, not by
/// making one body serve both.
///
/// A cell binds to whatever it meets, and there is no occurs check to do: an
/// effect cannot contain another effect, so the cycle a type unifier has to
/// guard against has no shape here.
let unifyEffect (e1: Effect) (e2: Effect) =
    match pruneEffect e1, pruneEffect e2 with
    | EMeta a, EMeta b when a.EId = b.EId -> ()
    // A cell binds to either colour now. Binding it to `EAsync` is what asks
    // for the suspending copy of the procedure it came from, and that copy
    // exists: `checkDeclGroup` generates one for every signature declaring a
    // `-?->`, and `EffectGraph.selectDoubles` reads this cell back to decide
    // which of the two a call site meant.
    //
    // This branch used to refuse, because emitting one ordinary body and then
    // handing it a `Func<..., Fiber<R>>` is a Roslyn error in a file nobody
    // wrote. What made it safe to lift is that there are now two bodies rather
    // than one guess.
    | EMeta a, other -> a.EValue <- Some other
    | other, EMeta a -> a.EValue <- Some other
    | ESync, ESync
    | EAsync, EAsync -> ()
    | EEffVar a, EEffVar b when a = b -> ()
    // `EPoly` is a *quantified* variable and instantiation is what removes it,
    // so meeting one here means a signature was used without being
    // instantiated. `EEffVar` is never constructed at all.
    | EPoly, _
    | _, EPoly -> failwith "Internal error: an -?-> arrow reached unification without being instantiated"
    | EEffVar _, _
    | _, EEffVar _ -> failwith "Internal error: named effect variables have no solver"
    // The two directions are different mistakes with different answers, and one
    // message served both — so the commoner of the two, an ordinary function
    // handed to a suspending parameter, was reported as its own opposite.
    //
    // `e1` is the expectation and `e2` is what was supplied. That is not a
    // convention `unify`'s callers state anywhere, so it is asserted by the two
    // fixtures named below rather than by reading them.
    | ESync, EAsync ->
        failwith
            "Type error: a bjoroutine cannot be used where an ordinary function is expected. Its C# counterpart returns Fiber<T> rather than T, so the two are different calling conventions.\n  If the parameter is yours to change, declaring it -?-> makes it accept either colour, and both copies are generated.\n  If it is not, the suspending work has to happen before the call rather than inside the callback."
    // Subeffecting, and the only direction of it there is: a procedure that
    // never suspends is usable wherever one that may is expected. Accepted
    // here; `Codegen` bridges the representation with `Colour.Lift`, since
    // `Func<A,B>` and `Func<A,Fiber<B>>` are still different C# types.
    //
    // Only *ground* colours reach this. A `-?->` arrives as a cell and binds in
    // the `EMeta` branches above, so nothing here can settle a colour that
    // something else was still entitled to decide — which is what keeps
    // subeffecting and effect defaulting out of each other's way.
    //
    // The asymmetry is the point, and it is why splitting this branch in two
    // was worth doing before lifting either half. `ESync, EAsync` above stays
    // an error because there is no un-awaiting: a caller that does not await
    // cannot run a state machine to completion, and no wrapper can invent that.
    | EAsync, ESync -> ()

/// Diagnostic hint appended when a raw value is unified against a trait box.
///
/// Packing is always explicit (values are never boxed automatically), so the
/// missing step is an explicit `(dyn Trait <expr>)` packing expression in user
/// code.
let dynPackHintFor (t: HMType) : string =
    match t with
    | TCon(name, _) when Naming.isDynType name ->
        let traitName = (Naming.dynTraitOf name).Value

        $"\n  A (dyn %s{traitName} ...) holds a value with its type hidden, and nothing is packed into one by itself: write (dyn %s{traitName} <expr>) to pack the value."
    | _ -> ""

/// Same hint check for a pair of types: returns a hint if exactly one of the
/// types is a dyn box. If both are dyn boxes, the mismatch is between boxes
/// rather than missing packing.
let dynPackHint (t1: HMType) (t2: HMType) : string =
    match dynPackHintFor t1, dynPackHintFor t2 with
    | "", hint
    | hint, "" -> hint
    | _ -> ""

let rec unify (registry: TraitRegistry) (t1: HMType) (t2: HMType) =
    let t1, t2 = prune registry t1, prune registry t2

    match t1, t2 with
    | _ when t1 = t2 -> ()
    | TMeta m, _ -> bindMeta registry m t2
    | _, TMeta m -> bindMeta registry m t1
    // The interop void meets unit.
    //
    // The two are one idea seen from either side of the boundary: `void` is how
    // C# spells "no value", and `Unit` is the value Bjolang has to hand back
    // because C# generics cannot abstract over the absence of one. Keeping them
    // as separate constructors is what lets code generation know which of the
    // two it is looking at — a void expression is a statement and cannot be
    // returned or assigned, a `Unit` one is an ordinary value — while letting a
    // function declared `(-> ... void)` end in a `.Dispose` call.
    //
    // Nothing is bound here, so this is not a subtyping rule leaking into
    // inference: neither side is a variable, and the pair either matches or does
    // not. The conversion it licenses happens once, in `emitTerminal`.
    | TCon(TypeConstants.VoidName, []), TCon(TypeConstants.UnitName, [])
    | TCon(TypeConstants.UnitName, []), TCon(TypeConstants.VoidName, []) -> ()
    // Arguments of a `dyn` type constructor are its pinned associated types.
    // Unifying two `dyn` types compares their associated types positionally.
    // Decorates any unification error with the specific associated type keyword
    // (`#:<name>`) so the error message clearly identifies which parameter failed.
    | TCon(name1, args1), TCon(name2, args2) when
        name1 = name2 && args1.Length = args2.Length && Naming.isDynType name1
        ->
        List.iteri2
            (fun i a b ->
                try
                    unify registry a b
                with ex ->
                    let assocName = Naming.dynAssocNamesOf name1 |> List.item i

                    failwithf
                        $"%s{ex.Message}\n  They are what #:%s{assocName} is pinned to on either side of a (dyn %s{(Naming.dynTraitOf name1).Value} ...). The value's own implementation decides it, and the annotation has to agree.")
            args1
            args2
    | TCon(name1, args1), TCon(name2, args2) when name1 = name2 && args1.Length = args2.Length ->
        List.iter2 (unify registry) args1 args2
    | TFun(args1, ret1, eff1), TFun(args2, ret2, eff2) when args1.Length = args2.Length ->
        unifyEffect eff1 eff2

        // An argument whose type waits on an implementor is checked last. In
        // `(fold + 0 v)` the folding function's type mentions `%item`, which is
        // `Foldable`'s associated type: it cannot be compared against `+`'s
        // `int` until `v` has said which implementation is in play. Parameter
        // order should not decide whether a program type-checks.
        let ready, waiting =
            List.zip args1 args2
            |> List.partition (fun (a, b) -> not (awaitsImplementor registry a || awaitsImplementor registry b))

        for (a, b) in ready do
            unify registry a b

        for (a, b) in waiting do
            unify registry a b

        unify registry ret1 ret2
    | TTuple args1, TTuple args2 when args1.Length = args2.Length -> List.iter2 (unify registry) args1 args2
    | TAssoc(tn1, an1, impl1), TAssoc(tn2, an2, impl2) when tn1 = tn2 && an1 = an2 -> unify registry impl1 impl2
    | _ ->
        let shown = DotNetInterop.showTypesTogether [ t1; t2 ]

        // Arity is called out because it is the common case and the hardest to
        // read off two arrows printed one above the other.
        let note =
            let args n = if n = 1 then "1 argument" else $"%d{n} arguments"

            match t1, t2 with
            | TFun(a1, _, _), TFun(a2, _, _) when a1.Length <> a2.Length ->
                $"\n  The first takes %s{args a1.Length}, the second %s{args a2.Length}."
            // Two types of one name. The pair above then differs only by the
            // module in front of it, which is a lot to expect a reader to spot
            // — and the thing they have to know is not that the spellings
            // differ but that the types do.
            | TCon(n1, _), TCon(n2, _) when n1 <> n2 ->
                match Naming.typeKeyParts n1, Naming.typeKeyParts n2 with
                | Some(m1, bare1), Some(m2, bare2) when bare1 = bare2 ->
                    // Samma modulnamn också: två filer som heter lika. Då är
                    // katalogen det enda som skiljer dem.
                    let where1, where2 =
                        if m1 = m2 then
                            Naming.showQualifiedTypeName n1, Naming.showQualifiedTypeName n2
                        else
                            m1, m2

                    $"\n  Both are called %s{bare1}, and a type belongs to the module that declared it: %s{where1} and %s{where2} each declared one. Import a module with (prefix-types ...) to give its types a spelling of their own."
                | _ -> ""
            | _ -> ""

        failwithf
            $"Type error: these types do not match.\n  %s{shown[0]}\n  %s{shown[1]}%s{note}%s{dynPackHint t1 t2}"

/// `prune` is deep and leaves no bound metavariable behind, so pruning once at
/// the top is what makes the survivors exactly the free ones.
let freeVars (registry: TraitRegistry) (t: HMType) : MetaVar list =
    prune registry t
    |> foldType (function
        | TMeta m -> [ m ]
        | _ -> [])

/// Sänker varje obunden cell i `t` till den aktuella nivån.
///
/// För en bindning som *inte* generaliseras. Dess högerled inferras en nivå in
/// ändå — om den ska generaliseras avgörs ibland först efteråt — och cellerna
/// som blir kvar hamnar då i omgivningen med en nivå som påstår att de är
/// oåtkomliga därifrån. Nästa syskonbindning hade kvantifierat dem.
let demote (registry: TraitRegistry) (t: HMType) : unit =
    for m in freeVars registry t do
        if m.Level > currentLevel then
            m.Level <- currentLevel

let freeTVars (registry: TraitRegistry) (t: HMType) : string list =
    prune registry t
    |> foldType (function
        | TVar name -> [ name ]
        | _ -> [])

/// Metavariables that a deferred, *un-abstractable* obligation is still waiting
/// on.
///
/// Generalizing one of these would replace the very metavariable resolution is
/// watching with a rigid type variable, and the answer could then never arrive:
/// an inline trait has no dictionary, so there is nothing a quantified
/// constraint over it could mean. Such a binding stays monomorphic instead, and
/// its use site pins the constructor.
///
/// We skip this specific optimization (monomorphization) for standard .NET interfaces.
/// .NET is already designed to handle generic interfaces via virtual dispatch, so there's
/// no need for us to unroll them like we do with Bjolang traits.
///
/// A hook rather than a parameter, because `generalize` is called from a dozen
/// places that have no business knowing a constraint queue exists.
let mutable heldMetaIds: unit -> Set<int> = fun () -> Set.empty

/// Metavariables held back for a binding that is *local* to a function body.
///
/// An unresolved interface-trait obligation may be generalized at the top
/// level: the variable becomes a type parameter, the obligation becomes a
/// dictionary parameter, and `Lowering` injects both. None of that machinery
/// exists for a local binding. A C# local function gets no dictionary
/// parameter from any pass, so quantifying the variable emitted a type
/// parameter the *enclosing* method was then expected to declare — and it
/// never did, because the constraint had been attributed to the wrong
/// function entirely.
///
/// Held back, such a binding stays monomorphic, its use site pins the type,
/// and the obligation resolves to a direct static call. That is the same
/// treatment an inline trait already gets above, and for the same reason: it
/// is the only thing the binding can honestly be.
let mutable heldLocalMetaIds: unit -> Set<int> = fun () -> Set.empty

/// Kvantifierar de celler i `t` som ligger djupare än den nivå bindningen
/// hamnar på.
///
/// Nivåjämförelsen ersätter en genomgång av hela omgivningen. De två svarar
/// lika: en cell som är nåbar från omgivningen har vid något tillfälle bundits
/// in i något som lever där, och `occursAndLower` sänkte den då till den
/// omgivningens nivå. Det som är kvar över `currentLevel` kan ingen annan se.
///
/// Skillnaden är kostnaden. Den gamla varianten vandrade varje typ i varje
/// binding — prelude och alla importer inräknade — en gång per bindning som
/// generaliserades, alltså O(omgivningen) per bindning och kvadratiskt över en
/// modul. Det här rör bara `t`.
let private generalizeWith (held: Set<int>) (env: Env) (t: HMType) : Scheme =
    let tFv = freeVars env.Registry t |> List.distinct

    let generalizable =
        tFv
        |> List.filter (fun m -> m.Level > currentLevel && not (Set.contains m.Id held))
    
    // Find all explicitly named TVars that are already in the type
    let explicitTVars = freeTVars env.Registry t |> List.distinct
    
    // Generated names have to be unique across the *whole* program, not just
    // within this type. The code generator maps `'a` to `T_a`, so two
    // independent generalizations that both chose `'a` would produce a nested
    // `T_a` that shadows the enclosing one instead of referring to it — and a
    // value typed at the outer parameter is not assignable to the inner one.
    let generatedNames = generalizable |> List.map (fun _ -> Gensym.fresh "'t")
    
    List.iter2 (fun (m: MetaVar) name -> m.Value <- Some(TVar name)) generalizable generatedNames

    let allVars = (explicitTVars @ generatedNames) |> List.distinct

    // Default to empty constraints for now; gathering happens during inference
    Scheme(allVars, [], t)

/// Generalizes a top-level binding: anything an unresolved *inline*-trait
/// obligation is watching stays monomorphic, and everything else is quantified.
let generalize (env: Env) (t: HMType) : Scheme = generalizeWith (heldMetaIds ()) env t

/// Generalizes a binding local to a function body.
///
/// Everything `generalize` holds back, plus the interface-trait obligations —
/// see `heldLocalMetaIds`. A local function may still be polymorphic; it may
/// just not be polymorphic in a variable that a trait call has to dispatch on.
let generalizeLocal (env: Env) (t: HMType) : Scheme =
    generalizeWith (Set.union (heldMetaIds ()) (heldLocalMetaIds ())) env t
