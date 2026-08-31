/// The effect fixpoint over the call graph, and its first consumer.
///
/// One traversal, three eventual purposes: forward it is colour inference,
/// over declared-sync functions it is the blocking lint, and its per-export
/// answer is the bit the export boundary publishes. Only the second and third
/// are built here — the first needs effect metavariables, which do not exist
/// yet.
///
/// # The shape
///
/// - **Nodes** are function bodies.
/// - **Edges** are calls: if A calls B then `effect(B) ≤ effect(A)`.
/// - **Solve** by propagating forward to a fixpoint.
///
/// The constraint is `≤` and deliberately not equality. Equality would be
/// wrong in a way that matters later: if `map`'s body contained a yield point,
/// unifying its effect with its callback's would force every caller of `map` to
/// supply a suspending callback.
///
/// The lattice has two points, so a round that changes nothing is the fixpoint
/// and no SCC decomposition is needed to get there — a cycle contributes only
/// what its own members contribute directly, and reaches it the round after
/// they do.
///
/// # What "blocking" means here
///
/// Not "does IO". **Parks the thread it runs on.** A bjoroutine that reaches
/// one of these does not suspend when it waits; it holds a pool thread for the
/// duration, and the scheduler has one fewer to run every other fiber on. That
/// is invisible until the pool is empty, at which point the program stops for
/// reasons nothing in it explains — which is why it is worth a warning while
/// still being perfectly legal.
module Bjolang.EffectGraph

open Bjolang.Lexer
open Bjolang.TypedAST

/// Why a definition parks: the name whose call does it, and how it is reached.
type Witness =
    { /// The leaf itself — a blocking builtin, a `#:blocking` import, or an
      /// imported definition a dependency published as reaching one.
      Leaf: string
      /// Where the call to the leaf is written.
      Where: Range
      /// From the definition being reported down to the leaf, in written
      /// names. A path naming something the programmer cannot find in a source
      /// file is worse than no path at all.
      Path: string list }

/// What one body does, as far as this analysis cares: the leaves it calls
/// itself, and the definitions it calls that might reach one.
type private Scan =
    { Leaves: (string * Range) list
      Calls: (string * Range) list }

type private Node =
    { Name: string
      Effect: Effect
      Scan: Scan }

let private isLambda (e: TypedExpr) =
    match e.Node with
    | TLambda _ -> true
    | _ -> false

/// Whether calling this name parks the thread.
///
/// The origin is tried as well as the visible name, because a dependency
/// publishes what it called the definition and an importer may have renamed it.
/// Without this, `(import (std prelude) #:prefix io/)` would turn the whole port
/// surface back into names the lint has never heard of.
let private isBlocking (registry: TraitRegistry) (name: string) =
    Set.contains name registry.BlockingNames
    || (match Map.tryFind name registry.ImportAliases with
        | Some alias -> Set.contains alias.OriginalName registry.BlockingNames
        | None -> false)

let private scanBody (registry: TraitRegistry) (body: TypedExpr) : Scan =
    let leaves = ResizeArray<string * Range>()
    let calls = ResizeArray<string * Range>()

    let rec walk (expr: TypedExpr) =
        match expr.Node with
        | TApply(target, args, kwArgs) ->
            let named =
                match target.Node with
                | TIdent(name, _) -> Some name
                | _ -> None

            match named with
            | Some name when isBlocking registry name -> leaves.Add(name, expr.Range)
            | Some name -> calls.Add(name, expr.Range)
            // A call through a value — a parameter, a field — names nothing
            // this graph has a node for. Layer 4's monomorphisation is what
            // makes those answerable; until then they are simply not edges.
            | None -> ()

            // An ordinary function lifted into a suspending slot, which *is* a
            // name and so is an edge.
            //
            // `Colour.Lift` does not make a function polite. The lifted body
            // still runs to completion on the fiber that calls it, so lifting
            // one that parks parks that fiber — silently, since the call is
            // made through the delegate and the case above has nothing to
            // record. The lift is the one place where a fiber reaches ordinary
            // code through a value and the compiler still knows its name, so it
            // is where the edge has to be added.
            //
            // Without this, subeffecting would have quietly restored the class
            // of bug that deciding a name's colour at the name removed.
            let paramTypes =
                match target.Type with
                | TFun(ps, _, _) -> ps
                | _ -> []

            args
            |> List.iteri (fun i a ->
                match List.tryItem i paramTypes, a.Node with
                | Some wanted, TIdent(liftedName, _) when TypeVisitor.liftsToSuspending wanted a.Type ->
                    if isBlocking registry liftedName then
                        leaves.Add(liftedName, a.Range)
                    else
                        calls.Add(liftedName, a.Range)
                | _ -> ())

            // `(blocking (fun () ...))` and `(spawn-thunk (fun () ...))` run
            // the lambda somewhere else, so what is inside it is not what this
            // body does. Walking in anyway would report the *recommended* way
            // to call blocking code from a bjoroutine, and a lint that fires on
            // its own advice is one nobody leaves switched on.
            let elsewhere =
                match named with
                | Some name -> Set.contains name Prelude.elsewhereBuiltins
                | None -> false

            walk target

            for a in args do
                if not (elsewhere && isLambda a) then walk a

            for (_, v) in kwArgs do
                walk v

        // A foreign call carries the claim on its own metadata rather than in
        // the registry, for the reason `ColourCheck` reads `Await` there: by
        // the time this runs, the import table that knew is several passes
        // behind.
        | TForeignStaticCall(clrType, methodName, args, Some meta) when meta.Blocking ->
            leaves.Add($"%s{clrType}.%s{methodName}", expr.Range)
            args |> List.iter walk

        | TDotMethodCall(receiver, methodName, args, Some meta) when meta.Blocking ->
            leaves.Add($"%s{meta.DeclaringType}.%s{methodName}", expr.Range)
            walk receiver
            args |> List.iter walk

        // A dispatched trait method is deliberately not an edge, for the same
        // reason `TImpl` is not entered: one node per method name would merge
        // every implementation's body, and the lint would report the union of
        // what they all do. So a path that runs through a dictionary stops
        // there, and the lint under-reports — which is the right way for a lint
        // to be wrong.
        | TInterfaceCall(_, _, _, dict, args) ->
            walk dict
            args |> List.iter walk

        | _ -> TypeVisitor.children expr |> List.iter walk

    walk body

    { Leaves = List.ofSeq leaves
      Calls = List.ofSeq calls }

/// Every top-level definition, as a node.
///
/// A lambda's body and a body-local function's are folded into the definition
/// they are written in rather than being nodes of their own. For this consumer
/// that is what is wanted: the question is which *bjoroutine* ends up holding a
/// thread, and a lambda written inside one runs on that one's thread unless it
/// was handed to something that moves it — which is what `elsewhereBuiltins`
/// covers.
///
/// `TImpl` is deliberately not entered. A trait method name is not unique — one
/// `fold` per implementation — so an edge to one would merge every
/// implementation's body into a single node and report the union of what they
/// all do. Under-reporting is the right way for a lint to be wrong.
let rec private collectNodes (registry: TraitRegistry) (decl: TDecl) : Node list =
    match decl with
    | TDefun(name, _, _, kwDefaults, _, _, effect, body, _) ->
        // A keyword default is emitted into the method's prologue, so it runs
        // on this body's thread and belongs to this node.
        let scans = scanBody registry body :: (kwDefaults |> List.map (fun (_, _, e) -> scanBody registry e))

        [ { Name = name
            Effect = effect
            Scan =
              { Leaves = scans |> List.collect (fun s -> s.Leaves)
                Calls = scans |> List.collect (fun s -> s.Calls) } } ]

    | TModule(_, decls, _) -> decls |> List.collect (collectNodes registry)
    | _ -> []

let private analyse (registry: TraitRegistry) (decls: TDecl list) : Node list * Map<string, Witness> =
    let nodes = decls |> List.collect (collectNodes registry)

    let mutable blocked =
        nodes
        |> List.choose (fun n ->
            match n.Scan.Leaves with
            | (leaf, where) :: _ ->
                Some(
                    n.Name,
                    { Leaf = leaf
                      Where = where
                      Path = [ n.Name; leaf ] }
                )
            | [] -> None)
        |> Map.ofList

    // The fixpoint. `Where` is carried from the witness rather than replaced by
    // the call site that reached it: the leaf is where the fix goes, and the
    // path below says how this definition got there.
    let mutable changed = true

    while changed do
        changed <- false

        for n in nodes do
            if not (Map.containsKey n.Name blocked) then
                let reached =
                    n.Scan.Calls
                    |> List.tryPick (fun (callee, _) -> Map.tryFind callee blocked)

                match reached with
                | Some w ->
                    blocked <- Map.add n.Name { w with Path = n.Name :: w.Path } blocked
                    changed <- true
                | None -> ()

    nodes, blocked

/// Every top-level definition in this module whose call can park a thread.
///
/// Published for the importing module, which sees this one as signatures and
/// would otherwise have to stop its own graph at the module boundary — at
/// exactly the calls worth reporting, since `read-line` and its neighbours all
/// live behind one.
let blockingDefinitions (registry: TraitRegistry) (decls: TDecl list) : Set<string> =
    let _, blocked = analyse registry decls
    blocked |> Map.toSeq |> Seq.map fst |> Set.ofSeq

// ---------------------------------------------------------------------------
// Call-site selection
// ---------------------------------------------------------------------------

/// Effect defaulting: an effect cell nothing has constrained becomes the colour
/// of the member it is written in.
///
/// This is the rule `groundEffect` has always described and never got to apply,
/// because until copies were generated a cell had only one colour it could
/// legally take. It is what makes a *name* colourless in the same way a call
/// already is: `(port->list read-line p)` leaves the reader's arrow open —
/// nothing about `read-line` or about `-?->` decides it — and what settles it
/// is whether the reading happens inside a bjoroutine.
///
/// Done here, in place, rather than as a pass of its own, because the context
/// it defaults to is precisely `selectIn`'s `allowed`: the same walk that knows
/// which C# member an expression lands in is the one that has to answer this,
/// and two walks agreeing by construction beats two walks agreeing by
/// inspection.
///
/// Whole types rather than outer arrows, because the cell a call has to read
/// back sits in a *parameter's* arrow — the `-?->` — while the cell that
/// decided it came from an argument's outer one, and unification chained them.
/// Binding the root reaches both from either side.
///
/// Grounding is idempotent and monotone: a cell already solved is left alone,
/// so an argument that pinned a colour keeps it and only the genuinely
/// undecided ones are decided here.
let rec private ground (allowed: bool) (t: HMType) : unit =
    let colour = if allowed then EAsync else ESync

    match t with
    | TFun(args, ret, eff) ->
        (match pruneEffect eff with
         | EMeta cell -> cell.EValue <- Some colour
         | _ -> ())

        args |> List.iter (ground allowed)
        ground allowed ret
    | TCon(_, args)
    | TTuple args -> args |> List.iter (ground allowed)
    | TAssoc(_, _, implementor) -> ground allowed implementor
    | TMeta { Value = Some inner } -> ground allowed inner
    | _ -> ()

/// A name with two copies, referenced as a value and grounded suspending.
///
/// The counterpart to the call-site rewrite below, and the harder half: a bare
/// `read-line` is not a call, so nothing about where it sits says which copy it
/// means, and the arrow it was grounded to is the only record of the decision.
/// Reading it back here is what lets `(port->list read-line p)` and
/// `(port->list (bjoroutine (q) (read-line q)) p)` compile to the same thing.
///
/// A cell that grounded to `ESync` is not one of these, so the test is exact
/// without needing to know whether grounding or unification bound it.
let private valueCopy (registry: TraitRegistry) (expr: TypedExpr) : TypedExpr option =
    match expr.Node, expr.Type with
    | TIdent(name, tyArgs), TFun(_, _, eff) when pruneEffect eff = EAsync ->
        match Map.tryFind name registry.DoubleDefs with
        | Some bjoName ->
            Some
                { expr with
                    Node = TIdent(bjoName, tyArgs)
                    Type = recolour EAsync expr.Type }
        | None -> None
    | _ -> None

/// Point calls at the suspending copy of a `defbjouble`, wherever one may be
/// awaited.
///
/// This is monomorphisation in its smallest possible form: the set of copies is
/// fixed — both were written by hand — so there is nothing to generate and only
/// the choice to make. `(read-line p)` in a `defun` is the `#:sync` body, the
/// same source in a `defbjo` is the `#:bjo` one, and the call site says
/// nothing either way. That is the whole point of the form.
///
/// The choice is made from the *enclosing member's* colour, which is a
/// deliberate limitation and not an oversight: a `defun` that a bjoroutine
/// calls keeps its ordinary copy, because giving it a second one is the
/// generating half of monomorphisation and that is a later layer. Until then a
/// procedure that must suspend is written `defbjo` and says so.
///
/// `allowed` tracks the same thing `ColourCheck`'s does, and has to: rewriting
/// a call into a copy that awaits, somewhere an await is illegal, would turn a
/// clean rejection into one that names a generated member the programmer has
/// never seen.
///
/// A `-?->` is chosen differently, and cannot use `allowed` at all — see
/// `wantsSuspendingCopy`.
///
/// # Why a generated copy always type-checks, and needs no escape hatch
///
/// A copy given to a `defun` because its call graph reaches a `defbjouble`
/// cannot fail `ColourCheck`, so nothing has to detect that and drop it.
///
/// The original is a `defun`, so it was checked with a sealed site and
/// therefore contains no yield point anywhere — otherwise it would not have
/// compiled. The copy is the same body, so the only yield points it can have
/// are the ones this pass puts there, and this pass only rewrites where
/// `allowed` holds. Every yield point in a generated copy is therefore in a
/// position where an await is legal, by construction.
///
/// What that buys is graceful degradation rather than a diagnostic: a `defun`
/// whose call to the leaf sits inside a `(seq ...)` still gets a copy, the call
/// inside the sequence is left on the ordinary body, and the copy simply awaits
/// nothing. It still parks, and the blocking lint still says so — which is the
/// right outcome for a copy nobody asked for by name.
///
/// A `-?->` copy is the case that *can* fail, and for a reason that does not
/// apply here: its parameter's own type is repainted, so calling that parameter
/// is a yield point wherever it appears, `allowed` or not. That one is an error,
/// because the arrow was written down and the author is owed an answer.
/// Paints every reference to one of `names` with `colour`.
///
/// A loop member's callers read the colour off the arrow the *binding* had —
/// `localFunShape`'s cell, which survives `LetRecify` and `LoopLowering`
/// untouched because those restructure the tree and not the types. Binding it
/// makes `callSuspends` answer at every call site at once, which is the same
/// mechanism a body-local function uses and the reason neither needs a pass to
/// go and find its callers.
///
/// Every occurrence is bound rather than the first one found. They do share a
/// cell, so one would do; relying on that would make this correct for a reason
/// that lives in three other files.
/// Has some use of one of `names` already fixed it as an ordinary function?
///
/// A loop member handed to a parameter declared `->` had its arrow unified with
/// that parameter's, so the cell is solved before anything here looks at it: the
/// member *is* a `Func<A,B>` at that call site, whatever the emitter would
/// prefer. Making the group async anyway would emit a `Fiber<T>`-returning local
/// function and hand it to a `Func<A,B>` — a Roslyn error in a file nobody
/// wrote.
///
/// A *call* does not pin, which is the distinction that makes this work: an
/// application demands the arrow's own colour rather than spelling `->`, so
/// only a value use constrains it. That is also why the group left refused here
/// is exactly the one `InEscapingLoop`'s original sentence described — passed
/// somewhere as a value — and why that sentence is finally true of everything
/// it now reaches.
let rec private pinnedOrdinary (names: Set<string>) (expr: TypedExpr) : bool =
    let here =
        match expr.Node with
        | TIdent(n, _) when Set.contains n names ->
            match expr.Type with
            | TFun(_, _, eff) -> pruneEffect eff = ESync
            | _ -> false
        | _ -> false

    here || (TypeVisitor.children expr |> List.exists (pinnedOrdinary names))

let rec private bindCallsTo (names: Set<string>) (colour: Effect) (expr: TypedExpr) : unit =
    (match expr.Node with
     | TIdent(n, _) when Set.contains n names ->
         match expr.Type with
         | TFun(_, _, eff) ->
             match pruneEffect eff with
             | EMeta cell -> cell.EValue <- Some colour
             | _ -> ()
         | _ -> ()
     | _ -> ())

    TypeVisitor.children expr |> List.iter (bindCallsTo names colour)

let rec private selectIn (registry: TraitRegistry) (allowed: bool) (expr: TypedExpr) : TypedExpr =
    let descend = selectIn registry allowed
    let sealed_ = selectIn registry false

    // Defaulting comes before every choice below, because every choice below is
    // made by reading a colour back out of a type. See `ground`.
    ground allowed expr.Type

    match expr.Node with
    | TLambda(ps, body) ->
        let inner =
            match expr.Type with
            | TFun(_, _, eff) -> pruneEffect eff = EAsync
            | _ -> false

        { expr with Node = TLambda(ps, selectIn registry inner body) }

    | TSeq body -> { expr with Node = TSeq(sealed_ body) }

    | TBjo body ->
        // The operands run in the parent and the call runs in the child, which
        // is always async — the same split `ColourCheck` makes, down to
        // emptying the argument list so that the child's colour reaches the
        // call and nothing else.
        match body.Node with
        | TApply(target, args, kwArgs) ->
            // The call first, and in the child's colour, because it is what
            // decides which copy is meant — and therefore what colour an
            // argument that is a bare name has to be. The arguments are
            // selected afterwards in the parent's, where they are evaluated: an
            // ordinary method builds a suspending delegate perfectly well, it
            // is only *calling* one that needs an await.
            let chosen = selectIn registry true { body with Node = TApply(target, [], []) }

            let target =
                match chosen.Node with
                | TApply(t, _, _) -> t
                | _ -> target

            let args = args |> List.map descend
            let kwArgs = kwArgs |> List.map (fun (n, v) -> n, descend v)
            { expr with Node = TBjo { body with Node = TApply(target, args, kwArgs) } }
        | _ -> { expr with Node = TBjo(selectIn registry true body) }

    | TLoop(members, bodyOpt) ->
        match bodyOpt with
        | None ->
            let members = members |> List.map (fun m -> { m with Body = descend m.Body })
            { expr with Node = TLoop(members, None) }
        | Some body ->
            let inlined = LoopLowering.isInlinedLoop members body

            if inlined then
                // A `while` in the enclosing member, which is the member's own
                // colour and nothing to decide.
                let members = members |> List.map (fun m -> { m with Body = selectIn registry allowed m.Body })
                { expr with Node = TLoop(members, Some(descend body)) }
            else
                // C# local functions, and so the same question a body-local
                // `defun` asks — answered the same optimistic way. See
                // `localFun`: the bodies are selected as though suspending were
                // allowed and whether any of them reached a yield point is the
                // answer.
                //
                // **One colour for the whole group.** Members jump to one
                // another, so a suspending member makes suspenders of its
                // siblings whether or not their own bodies await; and where the
                // group has cross-member jumps it is emitted as a single merged
                // method, which can only have one colour anyway.
                let selected =
                    members |> List.map (fun m -> { m with Body = selectIn registry allowed m.Body })

                let body = descend body

                let names = selected |> List.map (fun m -> m.LoopName) |> Set.ofList
                let everywhere = body :: (selected |> List.map (fun m -> m.Body))

                let colour =
                    if
                        allowed
                        && selected |> List.exists (fun m -> TypeVisitor.reachesAwait m.Body)
                        && not (everywhere |> List.exists (pinnedOrdinary names))
                    then
                        EAsync
                    else
                        ESync

                // The emitters read the member; the call sites read the *type*
                // of the name they call, which is the arrow the binding had
                // before `LoopLowering` dissolved it. Both have to be told, and
                // the cell is what tells the second: it is shared with every
                // reference, so `callSuspends` answers at each call without
                // anything walking out to find them.
                if colour = EAsync then
                    for e in everywhere do
                        bindCallsTo names colour e

                { expr with Node = TLoop(selected |> List.map (fun m -> { m with Effect = colour }), Some body) }

    | TLet(n, isFun, lf, value, body) ->
        let value = if isFun then localFun registry allowed value else descend value
        { expr with Node = TLet(n, isFun, lf, value, descend body) }

    | TLetRec(bindings, body) ->
        let bindings =
            bindings
            |> List.map (fun (n, isFun, lf, value) ->
                n, isFun, lf, (if isFun then localFun registry allowed value else descend value))

        { expr with Node = TLetRec(bindings, descend body) }

    // A method reached through a dictionary, where no implementation is known.
    // The twin is a slot of its own on the interface, so what is chosen is the
    // method *name* — where an ordinary call chooses the callee's.
    | TInterfaceCall(dictType, method, methodType, dict, args) ->
        ground allowed methodType

        let method, methodType =
            if wantsSuspendingCopy methodType then
                Naming.suspendingCopy method, recolour EAsync methodType
            else
                method, methodType

        { expr with
            Node = TInterfaceCall(dictType, method, methodType, descend dict, args |> List.map descend) }

    | TApply(target, args, kwArgs) ->
        // The callee's own type, defaulted before it is read: the cell an
        // `-?->` was instantiated to lives in one of its *parameters*, and this
        // is the last point at which anything could still constrain it. The
        // arguments below share that cell through unification, so grounding it
        // here answers for them too — which is why the call is decided before
        // its arguments are walked, and not after.
        ground allowed target.Type

        let target =
            match target.Node with
            // Chosen by what the argument *is*, not by where the call is
            // written. The two copies take different delegate types —
            // `Func<A,B>` against `Func<A,Fiber<B>>` — so exactly one of them
            // fits the argument, and the enclosing colour has no say.
            //
            // Which means this fires even where an await is illegal, unlike the
            // `defbjouble` case below. That is not an oversight: calling the
            // ordinary copy there would emit C# handing a fiber-returning
            // delegate to a parameter that takes an ordinary one, and a clean
            // rejection from `ColourCheck` is the better of the two.
            | TIdent(name, tyArgs) when wantsSuspendingCopy target.Type ->
                { target with
                    Node = TIdent(Naming.suspendingCopy name, tyArgs)
                    Type = recolour EAsync target.Type }
            | TIdent(name, tyArgs) when allowed ->
                match Map.tryFind name registry.DoubleDefs with
                // The copy's type is the declared one repainted, which is
                // exactly what `checkDecl` gave the definition itself.
                | Some bjoName ->
                    { target with
                        Node = TIdent(bjoName, tyArgs)
                        Type = recolour EAsync target.Type }
                | None -> descend target
            | _ -> descend target

        { expr with
            Node =
                TApply(
                    target,
                    args |> List.map descend,
                    kwArgs |> List.map (fun (n, v) -> n, descend v)
                ) }

    // A bare name, which is where the last colourless call site was decided by
    // something other than what the reader wrote. See `valueCopy`.
    | _ ->
        match valueCopy registry expr with
        | Some copy -> copy
        | None -> TypeVisitor.mapChildren descend expr

/// A body-local function, and the colour it is emitted in.
///
/// This is the one construct whose colour nothing else can answer. A top-level
/// definition declares it, a lambda takes it from the arrow it is written
/// against, and a generated copy is told — but a local function has no
/// signature to declare anything in, which by this language's own rule leaves
/// it inferred. Here is the only place that knows both halves of the question:
/// what the body reaches, and which C# member the definition lands in.
///
/// **Optimistically, in one pass.** The body is selected as though suspending
/// were allowed and the answer is whether it then holds a yield point. That is
/// enough because both outcomes are well formed, not because the guess is
/// usually right: a suspending copy chosen for a bare name inside a function
/// that turns out ordinary is a `Func<A,Fiber<B>>` being *built*, and an
/// ordinary C# method builds one perfectly well. Only calling one needs an
/// await — and a call is the very thing that would have made the function
/// async, so the case where the guess would matter cannot arise.
///
/// Sealed as before when the enclosing member is ordinary. There is nothing for
/// a local function to be async *for* there: its caller could not await it. The
/// yield point inside is then a real error, and `ColourCheck` reports it
/// against the local function by name.
and private localFun (registry: TraitRegistry) (allowed: bool) (value: TypedExpr) : TypedExpr =
    match value.Node with
    | TLambda(ps, body) when allowed ->
        // The body directly, rather than through `selectIn`'s `TLambda` case:
        // that case reads the colour off the arrow to decide how to walk the
        // body, and the arrow is precisely what is not decided yet.
        let selected = selectIn registry true body

        let colour =
            if TypeVisitor.reachesAwait selected then EAsync else ESync

        (match value.Type with
         | TFun(_, _, eff) ->
             match pruneEffect eff with
             | EMeta cell -> cell.EValue <- Some colour
             | _ -> ()
         | _ -> ())

        // Grounded in the colour just decided rather than the enclosing one:
        // whatever is still open in this arrow belongs to *this* member, and it
        // is this member's colour that answers for it.
        ground (colour = EAsync) value.Type

        { value with Node = TLambda(ps, selected) }

    | _ -> selectIn registry false value

/// Runs unconditionally: there used to be a short circuit when no `defbjouble`
/// was in scope, and a `-?->` needs no `defbjouble` anywhere to want its copy.
let selectDoubles (registry: TraitRegistry) (decls: TDecl list) : TDecl list =
        decls
        |> List.map (
            TypeVisitor.mapDeclWithContext (fun owner e ->
                let allowed =
                    match owner with
                    | TDefun(_, _, _, _, _, _, effect, _, _) -> pruneEffect effect = EAsync
                    | _ -> false

                selectIn registry allowed e)
        )

/// Reports every suspending body that can reach a call which parks its thread.
///
/// A warning rather than an error, and that is the whole design: parking is
/// legal, sometimes deliberate, and always the programmer's call. What it is
/// not is *visible* — so this says it, names the path, and gets out of the way.
///
/// # Two kinds of suspending body, and only one of them was written
///
/// Every node with an `EAsync` effect is a candidate, and since layer 4 that
/// includes the *generated copies*: a `defun` whose call graph reaches a
/// `defbjouble` has one, and a copy whose leaf could not be reached in its
/// suspending form parks exactly as the original does.
///
/// Reporting those is right — a bjoroutine calling that `defun` gets the copy,
/// so the parking is real — but calling the definition a bjoroutine is not. The
/// reader wrote `defun`, `humanize` strips `__bjo` before this prints, and the
/// message would be telling them their `defun` is something it is not. That is
/// worse than saying nothing: it teaches a model of the language in which
/// `defun` and `defbjo` are not the distinction they are.
///
/// The advice has to differ too, and for a reason with teeth: the fix offered to
/// a bjoroutine is `(sync (blocking ...))`, and `sync` is a yield point, so it
/// cannot be written in the `defun` body a copy is made from. Offering it would
/// be the "lint that fires on its own advice" trap with the advice unfollowable
/// as well.
let lint (registry: TraitRegistry) (decls: TDecl list) : unit =
    let nodes, blocked = analyse registry decls

    for n in nodes do
        if n.Effect = EAsync then
            match Map.tryFind n.Name blocked with
            | Some w ->
                let path = String.concat " -> " w.Path
                let parks = "A parked thread is one the scheduler cannot hand to another fiber: nothing else runs on it until the call returns."

                let message =
                    if Set.contains n.Name registry.GeneratedCopies then
                        $"'%s{n.Name}' is not a bjoroutine, but the suspending copy of it that a bjoroutine's call reaches still parks the thread it runs on: calling '%s{w.Leaf}' at %s{formatPos w.Where} does.\n  %s{path}\n  %s{parks} The copy is this same body, and nothing along that path could be given its suspending form here — either a callee has none, or the call sits somewhere an await is illegal, such as a (seq ...) body. Move the call out of whatever sealed it, or write the suspending version by hand with (defbjo ...)."
                    else
                        $"'%s{n.Name}' is a bjoroutine, and calling '%s{w.Leaf}' at %s{formatPos w.Where} parks the thread it runs on.\n  %s{path}\n  %s{parks} Move the wait off the fiber with (sync (blocking (fun () ...))), or use an operation that suspends rather than waits."

                Diagnostics.warn message
            | None -> ()
