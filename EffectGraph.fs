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

/// Reports every bjoroutine that can reach a call which parks its thread.
///
/// A warning rather than an error, and that is the whole design: parking is
/// legal, sometimes deliberate, and always the programmer's call. What it is
/// not is *visible* — so this says it, names the path, and gets out of the way.
let lint (registry: TraitRegistry) (decls: TDecl list) : unit =
    let nodes, blocked = analyse registry decls

    for n in nodes do
        if n.Effect = EAsync then
            match Map.tryFind n.Name blocked with
            | Some w ->
                let path = String.concat " -> " w.Path

                Diagnostics.warn
                    $"'%s{n.Name}' is a bjoroutine, and calling '%s{w.Leaf}' at %s{formatPos w.Where} parks the thread it runs on.\n  %s{path}\n  A parked thread is one the scheduler cannot hand to another fiber: nothing else runs on it until the call returns. Move the wait off the fiber with (sync (blocking (fun () ...))), or use an operation that suspends rather than waits."
            | None -> ()
