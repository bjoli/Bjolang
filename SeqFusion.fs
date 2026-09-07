module Bjolang.SeqFusion

open Bjolang.TypedAST

/// Fuses a loop that walks a `seq` literal into the literal's own body.
///
/// A `(loop (:for e S) ...)` whose `S` is a `(seq ...)` written right there —
/// or spliced there by `TraitInline`, which is how `(map f (filter p xs))`
/// arrives once both defaults are `seql`s — pulls one element at a time through
/// a `SeqCursor`: an iterator object per stage and two interface calls per
/// element. Nothing downstream can see through that. This pass can, because the
/// producer is source rather than a runtime object, and the rewrite is the old
/// one: walking a sequence once is running its body with each `yield x`
/// replaced by what the walker does with `x`.
///
/// The splice is in continuation style. At a yield the producer has a
/// continuation — what its member does after the `:do` — and the consumer's
/// body has jumps. Each jump becomes "store the slots, then the producer's
/// continuation"; the consumer's exits stay exactly what they were:
///
///     producer:  (let ((_ (yield x))) REST)
///     fused:     K(x) [ (looplevel cur slots...) := (begin (set! cells...) REST) ]
///
/// `REST` is a tail call to a producer member, so `K` sits in tail position of
/// that member. That is what makes early exit free: a consumer `:break` is a
/// tail position that calls the finish member instead of `REST`, so nothing
/// resumes the producer, and since every path in a loop is a tail call the
/// finish's value flows out of the producer's group as the result. Nothing is
/// threaded. The producer's members are retyped to return the consumer's
/// result for the same reason, and the producer's own exits — exhaustion, its
/// `:break`, its `:final` — are the sequence ending, so they become the
/// consumer's finish on the cells.
///
/// Why it is safe: the consumer pulls the producer exactly once and in order,
/// and the producer runs its clauses only when pulled, so the interleaving of
/// effects is the same fused or not: producer up to a yield, consumer for that
/// element, producer's continuation. Disposal gets simpler, not harder: there
/// is no enumerator left to abandon.
///
/// Why slots become cells: the consumer's level function carries its `:with`s
/// and accumulators as parameters and advances by tail call, but its body now
/// sits inside the producer's loop and has nothing to tail call. A cell in the
/// enclosing scope, assigned where the jump was and read into the slot's own
/// name at the top of every element, is the same fold spelled imperatively —
/// and the reads let the body keep mentioning each slot by the name it
/// already uses, with every `:acc` clause rebinding it exactly as before.
///
/// One ordering caveat the splice introduces. A `:with`'s `end` is tested where
/// the cursor's `done?` is, in clause order, and `done?` on a cursor is what
/// pulls the next element; so `(:for x s) (:with i ...)` tests after pulling
/// and `(:with i ...) (:for x s)` tests before. Fused, the producer has always
/// already produced the element when the `end` test runs. The two agree for
/// the first order and differ, by one produced-but-unconsumed element, for the
/// second — observable only through a `:do` in the producer.
///
/// What this version refuses, each of which is a correct program left as it is:
///
///   * A consumer with more than one level, or two `:for`s in lockstep. One
///     level with any number of `:with`s and `:acc`s is `map`, `filter`, `take`,
///     `fold`, `any?`, `collect` and every clause-shaped adapter, which is
///     what the pass is for. (A multi-level *producer* is fine: its yields are
///     wherever they are.)
///   * A producer that is not a loop: no `seql` shape, no finish member with
///     no parameters and the empty finish, a yield that is not a `:do`
///     statement in tail position of its member, or a `(yield-from s)`.
///   * A producer with more than `maxYieldSites` yields, and the shapes where
///     copying `REST` into every jump of the consumer would compound: several
///     yields under a consumer with several jumps, or a large `REST` under
///     several jumps. A loop-shaped producer has one yield whose continuation
///     is one tail call, and pays nothing.
///
/// Where it runs: after `TraitInline`, which is what exposes the literals, and
/// before `Lowering` and `LoopLowering`. The producer's group is still a
/// `letrec` of tail calls here, so once it stands in the consumer's function
/// `LoopLowering` recognises it as it would any other and emits it inline as a
/// `while` — which is also what lets a `yield` of the consumer's own (when *it*
/// is a `seql`) reach the enclosing iterator, and an `await` in its body reach
/// the enclosing state machine. `MustUse` has already run, so nothing here
/// changes a diagnostic.
///
/// Fusion is bottom-up, so a chain fuses from the inside out: the innermost
/// pair becomes one `seq`, which is then the literal the next consumer sees —
/// and it has the shape this pass recognises as a producer, cells and all.
///
/// Set `BJOLANG_NO_FUSION` to skip the pass entirely, and `BJOLANG_FUSION_TRACE`
/// to have each fusion say where it happened — the two together bisect a
/// suspected miscompile in one build each.

// ---------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------

/// How many yields a producer may have. See above.
let private maxYieldSites = 4

/// How large a producer continuation may be, in nodes, when the consumer has
/// more than one jump to copy it into.
let private restBudget = 400

let private unitT = TypeConstants.unitType
let private boolT = TypeConstants.boolType

let private at r (t: HMType) (node: TExprNode) : TypedExpr = { Type = t; Range = r; Node = node }

let private baseIs (prefix: string) (name: string) = Gensym.baseName name = prefix

/// How many times `name` is referenced. The same count `TraitInline` keeps,
/// kept private there and repeated here rather than exported for one caller.
let rec private occurrences (name: string) (expr: TypedExpr) : int =
    let here =
        match expr.Node with
        | TIdent(n, _) when n = name -> 1
        | TSet(n, _) when n = name -> 1
        | TRecordUpdate(n, _) when n = name -> 1
        | TRecordSet(n, _) when n = name -> 1
        | _ -> 0

    TypeVisitor.children expr |> List.sumBy (occurrences name) |> (+) here

let private mentions (name: string) (expr: TypedExpr) = occurrences name expr > 0

/// Replaces every reference to `name` with `replacement`. The caller freshens
/// the expression first, so no binder in it can capture a free variable of the
/// replacement.
let rec private substitute (name: string) (replacement: TypedExpr) (expr: TypedExpr) : TypedExpr =
    match expr.Node with
    | TIdent(n, _) when n = name -> replacement
    | _ -> TypeVisitor.mapChildren (substitute name replacement) expr

let private isCallTo (name: string) (e: TypedExpr) =
    match e.Node with
    | TApply({ Node = TIdent(n, _) }, _, _) when n = name -> true
    | _ -> false

let private size (expr: TypedExpr) : int = TypeVisitor.foldExpr (fun n _ -> n + 1) 0 expr

/// The `yield`s that belong to a seq body: not those of a nested `seq`, whose
/// elements are its own, and not anything inside a `bjo`, which is a spawned
/// scope. Every other sub-expression is looked into, including the lambdas of a
/// loop's members — that is where a `seql`'s yield lives.
let rec private ownYields (expr: TypedExpr) : TypedExpr list =
    match expr.Node with
    | TYield _
    | TYieldFrom _ -> [ expr ]
    | TSeq _
    | TBjo _ -> []
    | _ -> TypeVisitor.children expr |> List.collect ownYields

/// `(let ((_ (yield x))) rest)` — a `:do (yield x)` and what follows it.
let private isYieldStatement (e: TypedExpr) =
    match e.Node with
    | TLet("_", false, _, { Node = TYield _ }, _) -> true
    | _ -> false

/// Every node on the tail spine of `expr`: the node itself and, through a
/// binding form's body, a conditional's branches, a match's arms and a
/// `when`'s body, everything in tail position under it. The same edges
/// `LoopLowering` follows when it decides what is a jump.
let rec private tailSpine (expr: TypedExpr) : TypedExpr list =
    expr
    :: (match expr.Node with
        | TLet(_, _, _, _, body)
        | TLetTuple(_, _, body)
        | TLetMutable(_, _, body)
        | TLetRec(_, body) -> tailSpine body
        | TIf(_, t, f) -> tailSpine t @ tailSpine f
        | TMatch(_, clauses) -> clauses |> List.collect (fun c -> tailSpine c.Body)
        | TWhen(_, body, _) -> tailSpine body
        | _ -> [])

/// Recomputes the type of every binding form and conditional from its body,
/// bottom-up. Needed once the tails of a body have changed type — a jump that
/// became a store-and-continue, a producer exit that became the consumer's
/// finish — since every node on the spine above was typed as the old tail.
/// On a node that has not changed it is the identity: these are the types
/// inference gave them.
let rec private retype (expr: TypedExpr) : TypedExpr =
    let expr = TypeVisitor.mapChildren retype expr

    let fromBody =
        match expr.Node with
        | TLet(_, _, _, _, body)
        | TLetTuple(_, _, body)
        | TLetMutable(_, _, body)
        | TLetRec(_, body) -> Some body.Type
        | TIf(_, t, _) -> Some t.Type
        | TMatch(_, c :: _) -> Some c.Body.Type
        | TWhen _ -> Some unitT
        | _ -> None

    match fromBody with
    | Some t -> { expr with Type = t }
    | None -> expr

/// `(begin (set! c1 v1) ... (set! cn vn) tail)`. Every `vi` is an expression of
/// the consumer's scope, where the slots are immutable names, so none of them
/// reads a cell and the order of the stores is not observable.
let private stores r (cells: string list) (values: TypedExpr list) (tail: TypedExpr) : TypedExpr =
    List.foldBack
        (fun (cell, v) rest -> at r rest.Type (TLet("_", false, noParams, at r unitT (TSet(cell, v)), rest)))
        (List.zip cells values)
        tail

// ---------------------------------------------------------------------------
// Recognising the consumer
// ---------------------------------------------------------------------------

/// A single-level `(loop (:for e S) ...)` as `desugarLoop` and `LetRecify` leave
/// it, taken apart.
type private Consumer =
    { /// The `loopseq__N` the prologue bound the source to.
      SeqName: string
      /// The `looplevel__N` member: every jump inside the body calls it.
      Level: string
      /// Its `loopcur__N` parameter.
      Cursor: string
      /// Its `:with` slots, in parameter order.
      Withs: string list
      /// Its accumulator slots, which are also `loopexit`'s parameters.
      Accs: string list
      /// The slots' initial values, from the entry call: `:with` starts, then
      /// collector inits.
      Starts: TypedExpr list
      /// The `loopexit__N` finish member.
      ExitName: string
      /// The call to it at exhaustion, on the slot names. Reused verbatim
      /// where a `:with`'s `end` holds, since the reads bind those names.
      ExitCall: TypedExpr
      /// The `:with` `end` tests, as one `or`-chain — `None` when there are
      /// none. The cursor's `done?` is not among them: the producer ending is
      /// what it meant.
      WithTests: TypedExpr option
      /// Rebuilds the tuple-pattern `:with` destructuring that `bindWiths`
      /// wrapped the member body in, around a new inner expression.
      WrapWiths: TypedExpr -> TypedExpr
      /// `(let ((e (iterable-current ...))) K)` — or a tuple pattern's
      /// `let-tuple`. Its value is the cursor read; its body is the rest of
      /// the iteration.
      ElemBind: TypedExpr
      /// The `TLetRec` node itself, for its type and range.
      Group: TypedExpr }

let private recognize
    (seqName: string)
    (exitName: string)
    (exitFn: LocalFun)
    (lvl: string * bool * LocalFun * TypedExpr)
    (entry: TypedExpr)
    (group: TypedExpr)
    : Consumer option =

    let lvlName, isFun, _, value = lvl

    if not (isFun && baseIs "looplevel" lvlName) then
        None
    else
        match value.Node, entry.Node with
        // The member's parameters are its slots: the cursor, the `:with`s,
        // then the accumulators — which are exactly `loopexit`'s parameters,
        // so the accumulators are the last that many, and the `:with`s are
        // whatever sits between.
        | TLambda(cur :: slots, memberBody), TApply({ Node = TIdent(entryName, _) }, _ :: starts, []) when
            entryName = lvlName
            && baseIs "loopcur" cur
            && slots.Length = starts.Length
            && slots.Length >= exitFn.Params.Length
            ->
            let withs, accs = List.splitAt (slots.Length - exitFn.Params.Length) slots

            // A second cursor is a second `:for` at the same level, walked in
            // lockstep. Only one producer can drive a fused loop.
            if withs |> List.exists (baseIs "loopcur") then
                None
            else
                // `bindWiths` destructures tuple-pattern `:with` slots around the
                // whole member body, ahead of the exhaustion test.
                let rec peelWiths (e: TypedExpr) (wrap: TypedExpr -> TypedExpr) =
                    match e.Node with
                    | TLetTuple(names, ({ Node = TIdent(slot, _) } as v), inner) when List.contains slot withs ->
                        peelWiths inner (fun x -> wrap { e with Type = x.Type; Node = TLetTuple(names, v, x) })
                    | _ -> e, wrap

                let core, wrapWiths = peelWiths memberBody id

                match core.Node with
                | TIf(exhausted, exitCall, elemBind) when isCallTo exitName exitCall ->
                    // `exhausted` is `anyOf` over the level's tests in clause
                    // order: `(if t1 #t (if t2 #t t3))`. The one that mentions
                    // the cursor is `done?`; the rest are `:with` `end`s.
                    let rec tests (t: TypedExpr) =
                        match t.Node with
                        | TIf(t1, { Node = TBool true }, rest) -> t1 :: tests rest
                        | _ -> [ t ]

                    let all = tests exhausted
                    let cursorTests, withTests = all |> List.partition (fun t -> mentions cur t || mentions seqName t)

                    let orChain =
                        match withTests with
                        | [] -> None
                        | _ ->
                            let last = List.last withTests
                            let before = List.take (withTests.Length - 1) withTests

                            Some(
                                List.foldBack
                                    (fun (t: TypedExpr) acc -> at t.Range boolT (TIf(t, at t.Range boolT (TBool true), acc)))
                                    before
                                    last
                            )

                    match cursorTests, elemBind.Node with
                    | [ _ ], (TLet(_, false, _, _, _) | TLetTuple _) ->
                        Some
                            { SeqName = seqName
                              Level = lvlName
                              Cursor = cur
                              Withs = withs
                              Accs = accs
                              Starts = starts
                              ExitName = exitName
                              ExitCall = exitCall
                              WithTests = orChain
                              WrapWiths = wrapWiths
                              ElemBind = elemBind
                              Group = group }
                    | _ -> None
                | _ -> None
        | _ -> None

// ---------------------------------------------------------------------------
// Recognising the producer
// ---------------------------------------------------------------------------

/// A `seql` body — fresh from the parser, or the result of an earlier fusion,
/// which has the same shape with cells in front — taken apart.
type private Producer =
    { /// The group's members, all `looplevel`s.
      Members: string list
      /// The finish member the group calls when the sequence ends. Its
      /// binding is dropped by `Rebuild`; `fuse` replaces every call to it.
      ExitName: string
      /// The `TLetRec` node.
      Group: TypedExpr
      /// Rebuilds the prologue — `loopseq`, cells, spliced-argument `let`s —
      /// around a new group, without the finish binding.
      Rebuild: TypedExpr -> TypedExpr }

let private recognizeProducer (body: TypedExpr) : Producer option =
    let rec walk (e: TypedExpr) (exitFound: string option) (wrap: TypedExpr -> TypedExpr) =
        match e.Node with
        | TLetRec(bindings, entry) when exitFound.IsSome ->
            let names = bindings |> List.map (fun (n, _, _, _) -> n)

            let allMembers =
                bindings
                |> List.forall (fun (n, isFun, _, (v: TypedExpr)) ->
                    isFun
                    && baseIs "looplevel" n
                    && (match v.Node with
                        | TLambda _ -> true
                        | _ -> false))

            let enteredByCall =
                match entry.Node with
                | TApply({ Node = TIdent(n, _) }, _, []) -> List.contains n names
                | _ -> false

            if allMembers && enteredByCall then
                Some
                    { Members = names
                      ExitName = exitFound.Value
                      Group = e
                      Rebuild = wrap }
            else
                None

        // The finish member. A `seql` has no accumulators and no `=>`, so its
        // finish takes nothing and is the empty `(when #f ())` — which is
        // also what an earlier fusion's consumer left, when that consumer was
        // a `seql`. Anything else does work of its own at the end of the
        // sequence, and is not this shape.
        | TLet(n, true, fn, v, inner) when baseIs "loopexit" n && exitFound.IsNone ->
            let trivial =
                fn.Params.IsEmpty
                && (match v.Node with
                    | TLambda([], { Node = TWhen({ Node = TBool false }, _, false) }) -> true
                    | _ -> false)

            if trivial then walk inner (Some n) wrap else None

        | TLet(n, false, fn, v, inner) ->
            walk inner exitFound (fun g -> wrap { e with Type = g.Type; Node = TLet(n, false, fn, v, g) })

        | TLetMutable(n, v, inner) ->
            walk inner exitFound (fun g -> wrap { e with Type = g.Type; Node = TLetMutable(n, v, g) })

        | _ -> None

    walk body None id

// ---------------------------------------------------------------------------
// The rewrite
// ---------------------------------------------------------------------------

/// The fused loop, or `None` when one of the refusals above applies.
let private fuse (c: Consumer) (producerBody: TypedExpr) : TypedExpr option =
    let r = c.Group.Range
    let resultType = c.Group.Type

    // 1. The producer, its binders freshened — the consumer's body is about to
    //    be placed inside them and names the consumer's scope — and taken
    //    apart.
    let fresh = AlphaRename.freshenTyped [] producerBody |> fst

    match recognizeProducer fresh with
    | None -> None
    | Some p ->

    // Every yield must be a `:do` statement in tail position of a member: that
    // is the shape whose continuation is a tail call. A `yield-from`, or a
    // yield inside an `if` in a `:do`, is a yield this cannot place.
    let memberBodies =
        match p.Group.Node with
        | TLetRec(bindings, _) ->
            bindings
            |> List.choose (fun (_, _, _, (v: TypedExpr)) ->
                match v.Node with
                | TLambda(_, body) -> Some body
                | _ -> None)
        | _ -> []

    let yieldCount = ownYields fresh |> List.length
    let tailYieldSites = memberBodies |> List.collect tailSpine |> List.filter isYieldStatement

    // The rest of the consumer's iteration, and how to put the element back in
    // front of it — with the yielded expression where the cursor read was, so
    // it is evaluated exactly once, as the read was.
    let k1, rebindElem =
        match c.ElemBind.Node with
        | TLet(e, false, fn, _, body) ->
            body, (fun (x: TypedExpr) (rest: TypedExpr) -> at c.ElemBind.Range rest.Type (TLet(e, false, fn, x, rest)))
        | TLetTuple(names, _, body) ->
            body, (fun (x: TypedExpr) (rest: TypedExpr) -> at c.ElemBind.Range rest.Type (TLetTuple(names, x, rest)))
        | _ -> failwith "internal error: SeqFusion.recognize admitted an element binding it cannot rebuild"

    // Every jump in the consumer's body must be in tail position, or the
    // producer's continuation would be copied somewhere it is not a jump — a
    // real call, which keeps the member from being emitted inline.
    let jumpCount = occurrences c.Level k1
    let tailJumps = tailSpine k1 |> List.filter (isCallTo c.Level) |> List.length

    let restTooLarge =
        jumpCount > 1
        && (yieldCount > 1
            || tailYieldSites
               |> List.exists (fun site ->
                   match site.Node with
                   | TLet(_, _, _, _, rest) -> jumpCount * size rest > restBudget
                   | _ -> false))

    if yieldCount <> tailYieldSites.Length
       || yieldCount > maxYieldSites
       || jumpCount <> tailJumps
       || restTooLarge then
        None
    else

    // 2. The consumer's slots become cells; its finish, called on the
    //    accumulators' cells, is what the producer's exits become.
    let slots = c.Withs @ c.Accs
    let cells = slots |> List.map (fun s -> Gensym.fresh (Gensym.baseName s + "_cell"))
    let cellTypes = c.Starts |> List.map (fun s -> s.Type)
    let accCells = List.zip cells cellTypes |> List.skip c.Withs.Length

    let exitOnCells =
        match c.ExitCall.Node with
        | TApply(callee, _, _) ->
            { c.ExitCall with Node = TApply(callee, accCells |> List.map (fun (cell, t) -> at r t (TIdent(cell, []))), []) }
        | _ -> failwith "internal error: SeqFusion.recognize admitted an exit that is not a call"

    // 3. The producer's group with the consumer's result type: every call to
    //    a member is typed as returning it, every member is declared to, and
    //    every call to the producer's finish is the consumer's finish.
    let memberSet = Set.ofList p.Members

    let rec redirect (e: TypedExpr) : TypedExpr =
        match e.Node with
        | TApply({ Node = TIdent(n, _) }, _, _) when n = p.ExitName -> exitOnCells
        | TApply(({ Node = TIdent(n, _) } as callee), args, kwArgs) when Set.contains n memberSet ->
            { e with
                Type = resultType
                Node = TApply(callee, List.map redirect args, kwArgs |> List.map (fun (k, v) -> k, redirect v)) }
        | _ -> TypeVisitor.mapChildren redirect e

    let group' =
        match p.Group.Node with
        | TLetRec(bindings, entry) ->
            let bindings' =
                bindings
                |> List.map (fun (n, isFun, fn, (v: TypedExpr)) ->
                    let declared =
                        match v.Type with
                        | TFun(args, _, eff) -> TFun(args, resultType, eff)
                        | t -> t

                    n, isFun, fn, { redirect v with Type = declared })

            { p.Group with Node = TLetRec(bindings', redirect entry) } |> retype
        | _ -> failwith "internal error: SeqFusion.recognizeProducer admitted a group that is not a letrec"

    if mentions p.ExitName group' then
        None
    else

    // 4. The consumer's body for one element, given the yielded expression and
    //    the producer's continuation: read the cells into the slots' names,
    //    destructure tuple `:with`s, test the `:with` ends, bind the element,
    //    run the rest with every jump a store-and-continue. Freshened per
    //    site, since a producer with several yields gets a copy at each, and
    //    a copy shares no binder with its siblings.
    let perSite (x: TypedExpr) (rest: TypedExpr) : TypedExpr =
        let rec asStores (e: TypedExpr) : TypedExpr =
            match e.Node with
            | TApply({ Node = TIdent(n, _) }, _ :: slotArgs, []) when n = c.Level && slotArgs.Length = cells.Length ->
                stores r cells (slotArgs |> List.map asStores) rest
            | _ -> TypeVisitor.mapChildren asStores e

        let iteration = asStores k1 |> retype |> rebindElem x

        let tested =
            match c.WithTests with
            | None -> iteration
            | Some t -> at r iteration.Type (TIf(t, c.ExitCall, iteration))

        let body = c.WrapWiths tested

        let withReads =
            List.foldBack
                (fun (slot, cell, t) inner -> at r inner.Type (TLet(slot, false, noParams, at r t (TIdent(cell, [])), inner)))
                (List.zip3 slots cells cellTypes)
                body

        AlphaRename.freshenTyped [] withReads |> fst

    // 5. Yields replaced, innermost first, so that a continuation holding a
    //    later yield is spliced already rewritten.
    let rec replaceYields (e: TypedExpr) : TypedExpr =
        match e.Node with
        | TSeq _
        | TBjo _ -> e
        | _ ->
            let e = TypeVisitor.mapChildren replaceYields e

            match e.Node with
            | TLet("_", false, _, { Node = TYield x }, rest) -> perSite x rest
            | _ -> e

    let fusedGroup = replaceYields group'

    // Nothing of the cursor protocol may survive. After the Seq impl's methods
    // were spliced there is nothing left to survive; if they were not — a
    // landing pad — a reference here would be to a name that no longer exists,
    // and the loop is left alone rather than miscompiled.
    if mentions c.Cursor fusedGroup || mentions c.SeqName fusedGroup || mentions c.Level fusedGroup then
        None
    else

    // 6. Assemble: cells, then the producer's prologue around its rewritten
    //    group. The consumer's finish member stays where it is, outside.
    let producer' = p.Rebuild fusedGroup

    Some(
        List.foldBack
            (fun (cell, start) rest -> at r resultType (TLetMutable(cell, start, rest)))
            (List.zip cells c.Starts)
            producer'
    )

// ---------------------------------------------------------------------------
// Finding the shape
// ---------------------------------------------------------------------------

let private fusedCount = ref 0

let private trace =
    not (isNull (System.Environment.GetEnvironmentVariable "BJOLANG_FUSION_TRACE"))

/// `Some fused` when `expr` is the prologue of a loop whose source is a seq
/// literal and the loop fuses.
///
/// The shape, outermost first: the `loopseq` binding, the `loopcol` bindings,
/// the `loopexit` binding, and the one-member `letrec`. Only that chain is
/// descended; a user's own `let` is never mistaken for part of it, because
/// nothing a user writes has these base names.
///
/// The source may be a seq literal under a prefix of `let`s — what
/// `bindArguments` leaves when a spliced default's argument was not trivial,
/// as in `(->seq (list 1 2 3))`. Those bindings are hoisted around the fused
/// loop unchanged, so their values are evaluated exactly when the prologue
/// evaluated them.
let private tryFuse (expr: TypedExpr) : TypedExpr option =
    match expr.Node with
    | TLet(seqName, false, _, source, rest) when baseIs "loopseq" seqName ->
        let rec peel (src: TypedExpr) (wrap: TypedExpr -> TypedExpr) =
            match src.Node with
            | TSeq body -> Some(body, wrap)
            | TLet(n, false, fn, v, inner) when not (mentions n rest) ->
                peel inner (fun e -> wrap { src with Type = e.Type; Node = TLet(n, false, fn, v, e) })
            | _ -> None

        let rec descend (e: TypedExpr) (producerBody: TypedExpr) : TypedExpr option =
            match e.Node with
            | TLet(exitName, true, exitFn, exitValue, ({ Node = TLetRec([ lvl ], entry) } as group)) when
                baseIs "loopexit" exitName
                ->
                recognize seqName exitName exitFn lvl entry group
                |> Option.bind (fun c -> fuse c producerBody)
                |> Option.map (fun fused -> { e with Node = TLet(exitName, true, exitFn, exitValue, fused) })
            | TLet(n, false, fn, v, body) when baseIs "loopcol" n ->
                descend body producerBody
                |> Option.map (fun body' -> { e with Node = TLet(n, false, fn, v, body') })
            | _ -> None

        match peel source id with
        | Some(producerBody, wrap) ->
            descend rest producerBody
            |> Option.map (fun rest' ->
                fusedCount.Value <- fusedCount.Value + 1

                if trace then
                    eprintfn $"seq fusion: fused the loop at %s{Lexer.formatPos expr.Range}"

                wrap rest')
        | None -> None
    | _ -> None

/// Moves a seq literal bound once to where it is used.
///
/// `bindArguments` binds a spliced method's parameter to its argument with a
/// `let` unless the argument is trivial, and a seq literal is not trivial by
/// its test — so `(map f (filter p xs))` arrives as `(let ((s (seq ...)))
/// (seq (loop (:for e s) ...)))`, with the literal one binding away from the
/// prologue that wants it. A literal is a recipe: building it has no effect,
/// and its body reads its free variables when it is walked, which is the same
/// moment wherever the literal stands. So a single use may take it, even a use
/// inside a lambda or another `seq`. The body is freshened first so that no
/// binder between the two sites can capture a free variable of the literal.
///
/// The loop's own `loopseq` and `loopenter` bindings are left alone: they *are*
/// the use, and `tryFuse` reads the literal off them.
let rec private propagate (expr: TypedExpr) : TypedExpr =
    match expr.Node with
    | TLet(s, false, _, ({ Node = TSeq _ } as literal), body) when
        not (baseIs "loopseq" s)
        && not (baseIs "loopenter" s)
        && occurrences s body = 1
        ->
        let literal' = propagate literal
        let body', _ = AlphaRename.freshenTyped [] body
        propagate (substitute s literal' body')
    | _ -> TypeVisitor.mapChildren propagate expr

/// Bottom-up: a fused loop is a seq literal to the loop that walks it.
let rec private fuseExpr (expr: TypedExpr) : TypedExpr =
    let expr = TypeVisitor.mapChildren fuseExpr expr

    match tryFuse expr with
    | Some fused -> fused
    | None -> expr

// ---------------------------------------------------------------------------
// The pass
// ---------------------------------------------------------------------------

/// Fuses every loop over a seq literal in the program.
let run (decls: TDecl list) : TDecl list =
    if not (isNull (System.Environment.GetEnvironmentVariable "BJOLANG_NO_FUSION")) then
        decls
    else
        fusedCount.Value <- 0

        let result =
            decls
            |> List.map (TypeVisitor.mapDecl propagate)
            |> List.map (TypeVisitor.mapDecl fuseExpr)

        if fusedCount.Value > 0 then
            Diagnostics.progress $"seq fusion: %d{fusedCount.Value} loop(s) fused"

        result
