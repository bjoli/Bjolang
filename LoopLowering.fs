module Bjolang.LoopLowering

open Bjolang.TypedAST

/// Rewrites tail recursion into explicit `TLoop`/`TRecur` nodes.
///
/// Every syntactic form that can recur — a module-level `defun`, a trait-`impl`
/// method, a named `let`, an inner `defun` — is lowered to the *same* shape, so
/// the code generator has exactly one path from a loop to emitted C# and none
/// from a loop to a real call. Whether a call becomes a jump is decided here,
/// once, rather than falling out of whichever emitter branch happens to be
/// reached.
///
/// Two things this pass is responsible for that the emitter cannot do:
///
/// * **Normalizing the argument vector.** A `TRecur` always carries one argument
///   per loop slot, with keyword arguments resolved to their positional slot and
///   omitted optionals filled in from their defaults. The emitter never sees a
///   partial argument list, so it cannot silently leave a slot holding the
///   previous iteration's value.
/// * **Per-iteration parameter copies.** A loop mutates its slots, but C#
///   closures capture by reference, so a lambda created in one iteration would
///   otherwise observe the next iteration's values. Each member's body is
///   alpha-renamed to read fresh per-iteration locals instead of the slots.

// ---------------------------------------------------------------------------
// Fresh names
// ---------------------------------------------------------------------------

let private fresh (prefix: string) = Gensym.fresh prefix

// ---------------------------------------------------------------------------
// Alpha renaming
// ---------------------------------------------------------------------------

// Promoted to `Bjolang.AlphaRename`, which fixes the pattern hole this pass had:
// `TMatch` never rewrote the pattern, so a free variable inside a `TPApp`'s
// embedded expression escaped renaming and a `TPAs` binder was never seen.
let private patternNames = AlphaRename.patternNames
let renameExpr = AlphaRename.renameExpr

// ---------------------------------------------------------------------------
// Loop targets
// ---------------------------------------------------------------------------

/// A loop that a call in tail position may jump to.
type private LoopTarget =
    { Index: int
      /// Every name the loop answers to. A trait-`impl` method has two: its
      /// source name and the devirtualized name `Lowering.fs` rewrote concrete
      /// self-calls to.
      Names: string list
      Mandatory: (string * HMType) list
      Keywords: (string * HMType * TypedExpr) list
      Rest: (string * HMType) option
      /// How many dictionary parameters the function takes ahead of its own.
      ///
      /// They are not loop slots — a dictionary is the same on every iteration,
      /// so reassigning it would be work with no effect — but `Lowering` does
      /// forward them on a recursive call, so a jump arrives carrying this many
      /// arguments that have nowhere to land. Dropped rather than stored.
      Dicts: int }

    member this.Name = List.head this.Names

/// The slot vector a `TRecur` targeting `t` must fill, in emission order. This
/// has to agree with the parameter order `Codegen` builds for a `TDefun`:
/// mandatory, then keyword, then rest.
let private slotsOf (t: LoopTarget) : (string * HMType) list =
    t.Mandatory
    @ (t.Keywords |> List.map (fun (n, ty, _) -> n, ty))
    @ (match t.Rest with
       | Some(n, elemType) -> [ n, TCon("Array", [ elemType ]) ]
       | None -> [])

/// Builds the complete positional argument vector for a jump to `t`.
let private normalizeRecur
    (t: LoopTarget)
    (args: TypedExpr list)
    (kwArgs: (string * TypedExpr) list)
    (source: TypedExpr)
    : TExprNode =
    let mandatoryCount = t.Mandatory.Length

    // The dictionaries a recursive call forwards are already in scope and
    // unchanged; the loop has no slot for them. See `LoopTarget.Dicts`.
    let args = if args.Length > mandatoryCount then args |> List.skip t.Dicts else args

    if args.Length < mandatoryCount then
        failwithf
            $"Internal error: tail call to '%s{t.Name}' passes %d{args.Length} positional arguments but %d{mandatoryCount} are mandatory (line %d{source.Range.Start.Line})"

    let mandatoryValues = args |> List.truncate mandatoryCount
    let restValues = args |> List.skip mandatoryCount

    // An omitted optional must be re-supplied from its default: the slot still
    // holds the *previous* iteration's value, which is not what a fresh call
    // would have produced.
    let keywordValues =
        t.Keywords
        |> List.map (fun (kwName, _, defaultValue) ->
            match kwArgs |> List.tryFind (fun (n, _) -> n = kwName) with
            | Some(_, value) -> value
            | None -> defaultValue)

    let restValue =
        match t.Rest with
        | Some(_, elemType) ->
            [ ({ Type = TCon("Array", [ elemType ])
                 Range = source.Range
                 Node = TArrayMake restValues }: TypedExpr) ]
        | None ->
            if not restValues.IsEmpty then
                failwithf
                    $"Internal error: tail call to '%s{t.Name}' passes too many positional arguments (line %d{source.Range.Start.Line})"

            []

    TRecur(t.Index, mandatoryValues @ keywordValues @ restValue)

// ---------------------------------------------------------------------------
// Queries
// ---------------------------------------------------------------------------

/// Whether `expr` contains a jump belonging to the loop scope it was lowered in.
/// Lambda bodies and nested loop members carry jumps of their own scopes.
let rec containsRecur (expr: TypedExpr) : bool =
    match expr.Node with
    | TRecur _ -> true
    | TLambda _
    // Neither runs where it is written: a `seq` body runs as it is drained, and
    // a spawned call runs on the pool. A tail call inside one is not this
    // function's tail call.
    | TSeq _
    | TBjo _ -> false
    | TLoop(_, bodyOpt) -> bodyOpt |> Option.map containsRecur |> Option.defaultValue false
    | _ -> TypeVisitor.children expr |> List.exists containsRecur

/// The set of member indices jumped to from within `expr`, in the loop scope
/// `expr` belongs to.
let rec recurTargetsIn (expr: TypedExpr) : Set<int> =
    match expr.Node with
    | TRecur(index, args) ->
        args |> List.fold (fun acc a -> Set.union acc (recurTargetsIn a)) (Set.singleton index)
    | TLambda _
    | TSeq _
    | TBjo _ -> Set.empty
    | TLoop(_, bodyOpt) ->
        bodyOpt |> Option.map recurTargetsIn |> Option.defaultValue Set.empty
    | _ ->
        TypeVisitor.children expr
        |> List.fold (fun acc c -> Set.union acc (recurTargetsIn c)) Set.empty

/// Every name `expr` mentions as a reference. Shadowing is ignored, so this
/// over-approximates: the emitter uses it to decide which loop members are still
/// reachable as *calls*, and over-approximating only keeps a member alive that
/// nothing could have called.
let rec referencedNames (expr: TypedExpr) : Set<string> =
    let here =
        match expr.Node with
        | TIdent(n, _) -> Set.singleton n
        | TSet(n, _) -> Set.singleton n
        | TRecordUpdate(n, _)
        | TRecordSet(n, _) -> Set.singleton n
        | _ -> Set.empty

    TypeVisitor.children expr
    |> List.fold (fun acc c -> Set.union acc (referencedNames c)) here

// ---------------------------------------------------------------------------
// Expressions
// ---------------------------------------------------------------------------

let rec private lowerExpr (targets: LoopTarget list) (inTail: bool) (expr: TypedExpr) : TypedExpr =
    /// A sub-expression that is *not* in tail position.
    let notTail e = lowerExpr targets false e
    /// A sub-expression that inherits the current tail position.
    let inherits e = lowerExpr targets inTail e
    /// A nested function scope. Its tail positions are its own, and it cannot
    /// jump into the enclosing loop.
    let newScope e = lowerExpr [] true e
    /// Shadowing a loop's name rebinds it: calls in the inner scope are not jumps.
    let shadow names =
        targets |> List.filter (fun t -> not (t.Names |> List.exists (fun n -> List.contains n names)))

    match expr.Node with
    | TApply(target, args, kwArgs) ->
        let loopTarget =
            if inTail then
                match target.Node with
                | TIdent(n, _) -> targets |> List.tryFind (fun t -> List.contains n t.Names)
                | _ -> None
            else
                None

        let loweredArgs = args |> List.map notTail
        let loweredKwArgs = kwArgs |> List.map (fun (n, e) -> n, notTail e)

        match loopTarget with
        | Some t ->
            { expr with
                Node = normalizeRecur t loweredArgs loweredKwArgs expr }
        | None ->
            { expr with
                Node = TApply(notTail target, loweredArgs, loweredKwArgs) }

    | TIf(c, t, f) ->
        { expr with
            Node = TIf(notTail c, inherits t, inherits f) }

    // A `when` in tail position leaves its body in tail position too: the value
    // is discarded either way, and a jump never produces one.
    | TWhen(c, body, negated) ->
        { expr with
            Node = TWhen(notTail c, inherits body, negated) }

    | TLetMutable(n, v, b) ->
        { expr with
            Node = TLetMutable(n, notTail v, lowerExpr (shadow [ n ]) inTail b) }

    | TLetTuple(names, v, b) ->
        { expr with
            Node = TLetTuple(names, notTail v, lowerExpr (shadow names) inTail b) }

    | TMatch(target, clauses) ->
        { expr with
            Node =
                TMatch(
                    notTail target,
                    clauses
                    |> List.map (fun c ->
                        let inner = shadow (patternNames c.Pattern)

                        { c with
                            Guard = Option.map (lowerExpr inner false) c.Guard
                            Body = lowerExpr inner inTail c.Body })
                ) }

    | TLambda(args, b) ->
        { expr with
            Node = TLambda(args, newScope b) }

    // A sequence's body becomes an iterator method of its own, so a call in its
    // tail position is a call there — not a jump into the loop this `seq` was
    // written inside, which by then has long since returned.
    | TSeq b ->
        { expr with
            Node = TSeq(newScope b) }

    // Likewise for a spawned call: it runs on the pool, so a call in its tail
    // position is not a jump into the loop it was written inside.
    | TBjo b ->
        { expr with
            Node = TBjo(newScope b) }

    | TLet(n, isFun, fn, v, b) ->
        let loweredValue = if isFun then newScope v else notTail v

        { expr with
            Node = TLet(n, isFun, fn, loweredValue, lowerExpr (shadow [ n ]) inTail b) }

    | TLetRec(bindings, b) -> lowerLetRec targets inTail expr bindings b

    // No child of any remaining node is in tail position.
    | _ -> TypeVisitor.mapChildren notTail expr

/// Turns a `letrec` group into a loop group. `LetRecify` has already reduced the
/// binding group to strongly-connected components, so the members handed to us
/// are exactly one component; no graph work is repeated here.
and private lowerLetRec
    (targets: LoopTarget list)
    (inTail: bool)
    (expr: TypedExpr)
    (bindings: (string * bool * LocalFun * TypedExpr) list)
    (body: TypedExpr)
    : TypedExpr =

    /// A member this group could jump to instead of calling.
    ///
    /// A keyword or rest parameter disqualifies one. A jump is positional — a
    /// `TRecur`'s arguments line up with the slots by index — and a keyword
    /// argument is exactly the thing that is *not* positional: an omitted one
    /// has no slot to fill, and its default is written to be evaluated once in
    /// the prologue rather than at every iteration. Such a member stays a C#
    /// local function, where the calling convention is the parameter list's.
    let asFunction (_, _, (fn: LocalFun), (value: TypedExpr)) =
        match value.Node, value.Type with
        | TLambda(lambdaArgs, lambdaBody), TFun(argTypes, retType, _) when
            argTypes.Length = lambdaArgs.Length
            && fn.KeywordArgs.IsEmpty
            && fn.RestArg.IsNone
            ->
            Some(lambdaArgs, argTypes, retType, lambdaBody)
        | _ -> None

    // Local loop names are made unique: a loop that used to live inside an
    // expression got its own lambda scope, but now becomes a C# local function
    // in the *enclosing* block, where a sibling of the same name could collide.
    let renames =
        bindings |> List.map (fun (n, _, _, _) -> n, fresh n) |> Map.ofList

    let renamedBindings =
        bindings
        |> List.map (fun (n, isFun, fn, v) -> renames[n], isFun, fn, renameExpr renames v)

    let renamedBody = renameExpr renames body

    match renamedBindings |> List.map asFunction |> List.forall Option.isSome with
    | false ->
        // An explicit `letrec` over values rather than functions. There is nothing
        // to jump to, so keep the mutually-visible-declaration encoding.
        { expr with
            Node =
                TLetRec(
                    renamedBindings
                    |> List.map (fun (n, isFun, fn, v) -> n, isFun, fn, lowerExpr [] true v),
                    lowerExpr targets inTail renamedBody
                ) }
    | true ->
        let members = renamedBindings |> List.map (asFunction >> Option.get)
        let names = renamedBindings |> List.map (fun (n, _, _, _) -> n)

        // Slots are fresh so that the per-iteration locals can keep the source's
        // parameter names, which is what the member bodies already read.
        let slotNames =
            members
            |> List.map (fun (lambdaArgs, _, _, _) -> lambdaArgs |> List.map (fun a -> fresh ("_" + a)))

        let loopTargets =
            List.mapi2
                (fun i name slots ->
                    let _, argTypes, _, _ = members[i]

                    { Index = i
                      Names = [ name ]
                      Mandatory = List.zip slots argTypes
                      Keywords = []
                      Rest = None
                      // A `loop` form's members take no dictionaries: they are
                      // local, and a local takes its evidence from the function
                      // it is written inside.
                      Dicts = 0 })
                names
                slotNames

        let loweredMembers =
            List.mapi2
                (fun i name slots ->
                    let lambdaArgs, argTypes, retType, lambdaBody = members[i]

                    { LoopName = name
                      Slots = List.zip slots argTypes
                      Locals = lambdaArgs
                      RetType = retType
                      // Ordinary until `EffectGraph` says otherwise. It decides
                      // per group, having seen the bodies and knowing which C#
                      // member the group lands in — neither of which is
                      // available here.
                      Effect = ESync
                      Body = lowerExpr loopTargets true lambdaBody })
                names
                slotNames

        // The group's own body is *outside* the loops: entering a loop from here
        // is a call, not a jump, so it keeps the enclosing scope's targets.
        { expr with
            Node = TLoop(loweredMembers, Some(lowerExpr targets inTail renamedBody)) }

// ---------------------------------------------------------------------------
// Declarations
// ---------------------------------------------------------------------------

/// Trait-dictionary parameters are prepended by `Lowering.fs`. They are constant
/// across iterations and a self-call never passes them (a recursive occurrence is
/// bound monomorphically, so it carries no type arguments and picks up no
/// dictionaries), so they are not loop slots.
let private isDictionaryParam (name: string) = name.StartsWith "_dict_"

/// Lowers a function body: `TLoop (_, None)` when it recurs, unchanged otherwise.
let private lowerFunctionBody
    (names: string list)
    (args: (string * HMType) list)
    (kwArgs: (string * HMType * TypedExpr) list)
    (restArg: (string * HMType) option)
    (retType: HMType)
    (body: TypedExpr)
    : TypedExpr =

    let name = List.head names

    let target =
        { Index = 0
          Names = names
          Mandatory = args |> List.filter (fst >> isDictionaryParam >> not)
          Keywords = kwArgs
          Rest = restArg
          Dicts = args |> List.filter (fst >> isDictionaryParam) |> List.length }

    let lowered = lowerExpr [ target ] true body

    if not (containsRecur lowered) then
        lowered
    else
        let slots = slotsOf target
        let locals = slots |> List.map (fun (n, _) -> fresh ("_" + n))

        let toLocals = List.zip (slots |> List.map fst) locals |> Map.ofList

        { body with
            Node =
                TLoop(
                    // `TLoop(_, None)` *is* the function's body: a `while` in the
                    // method itself, never a local function. So its colour is
                    // the method's and this field is not read for it.
                    [ { LoopName = name
                        Slots = slots
                        Locals = locals
                        RetType = retType
                        Effect = ESync
                        Body = renameExpr toLocals lowered } ],
                    None
                ) }

// ---------------------------------------------------------------------------
// Will this loop group be emitted inline, or as local functions?
// ---------------------------------------------------------------------------
//
// A `TLoop (_, Some body)` — a named `let`, or a `(loop ...)` — is emitted
// *inline* into the enclosing method as a `while`/`switch` whenever it is
// entered by an immediate call and no member escapes as a value. Otherwise its
// members become C# local functions.
//
// The difference is not a detail. A local function may not `yield return`, so a
// nested loop inside a `seq` has to be inlined for its `yield` to reach the
// enclosing iterator; and a local function is not `async`, so a loop inside a
// bjoroutine has to be inlined for a yield point inside it to reach the
// enclosing state machine. Same fact, twice.
//
// These live here rather than in `Codegen` because `ColourCheck` has to decide
// exactly what `Codegen` will do. Two copies of this recognition would mean a
// program the checker accepts and the emitter cannot compile.

/// A single-member group entered by an immediate call — the named-`let` shape.
/// `Some (member, initialArgs)` when it will be emitted inline.
let flatLoopEntry (members: TLoopMember list) (body: TypedExpr) : (TLoopMember * TypedExpr list) option =
    match members, body.Node with
    | [ member_ ], TApply({ Node = TIdent(calleeName, _) }, initArgs, _) when
        calleeName = member_.LoopName && initArgs.Length = member_.Slots.Length
        ->
        // A member that still names itself is calling rather than jumping, so it
        // needs to be a real function.
        if not ((referencedNames member_.Body).Contains member_.LoopName) then
            Some(member_, initArgs)
        else
            None
    | _ -> None

/// The same recognition for a group of *several* members, which is what a
/// multi-level `(loop ...)` is. `Some (entryIndex, initialArgs)` when it will be
/// emitted inline.
let mergedLoopEntry (members: TLoopMember list) (body: TypedExpr) : (int * TypedExpr list) option =
    match body.Node with
    | TApply({ Node = TIdent(calleeName, _) }, initArgs, _) when members.Length > 1 ->
        match members |> List.tryFindIndex (fun m -> m.LoopName = calleeName) with
        | Some entryIdx when initArgs.Length = members[entryIdx].Slots.Length ->
            let names = members |> List.map (fun m -> m.LoopName) |> Set.ofList

            // A member that names another as a *value* rather than jumping to it
            // needs a real function to be a value of.
            let escapes =
                members
                |> List.exists (fun m -> referencedNames m.Body |> Set.intersect names |> Set.isEmpty |> not)

            if escapes then None else Some(entryIdx, initArgs)
        | _ -> None
    | _ -> None

/// Does this loop group run in the enclosing method rather than in local
/// functions of its own?
let isInlinedLoop (members: TLoopMember list) (body: TypedExpr) : bool =
    (flatLoopEntry members body).IsSome || (mergedLoopEntry members body).IsSome

/// Which of the group's names are mentioned somewhere that is not a jump.
///
/// `isInlinedLoop` answers *whether* a group stays a local function; this
/// answers *what made it*, which is what a diagnostic needs and what it used to
/// guess. The guess was "it still mentions its own name", which is true of a
/// single self-recursive member and false of a mutually recursive pair — where
/// each member escapes because of the *other*, and a message naming the wrong
/// one sends the reader looking for a use that is not there.
///
/// Empty when the group is inlined, and also when it was refused for a reason
/// that is not a name at all: a group entered by something other than an
/// immediate call to a member has no escaping name to report.
let escapingNames (members: TLoopMember list) (body: TypedExpr) : string list =
    let names = members |> List.map (fun m -> m.LoopName) |> Set.ofList

    members
    |> List.collect (fun m -> referencedNames m.Body |> Set.intersect names |> Set.toList)
    |> List.distinct

/// `aliasFor` supplies the extra name a trait-`impl` method answers to.
let rec private lowerDeclWith (aliasFor: string -> string list) (decl: TDecl) : TDecl =
    match decl with
    | TModule(name, decls, r) -> TModule(name, decls |> List.map (lowerDeclWith aliasFor), r)

    | TImpl(traitName, kind, holeArity, targetType, assoc, dicts, methods, r) ->
        // A concrete self-call inside an `impl` method was devirtualized by
        // `Lowering.fs`, so the method no longer calls itself under its own name.
        // An inline trait's landing pad is a *static* method rather than one on
        // a singleton, so both spellings have to be recognized.
        let implAlias (methodName: string) =
            match targetType with
            | TCon(targetTypeName, _) -> [ landingPadName kind traitName targetTypeName methodName ]
            | _ -> []

        TImpl(traitName, kind, holeArity, targetType, assoc, dicts, methods |> List.map (lowerDeclWith implAlias), r)

    | TDefun(name, tyArgs, args, kwArgs, restArg, retType, effect, body, r) ->
        let loweredKwArgs = kwArgs |> List.map (fun (n, t, e) -> n, t, lowerExpr [] false e)

        TDefun(
            name,
            tyArgs,
            args,
            loweredKwArgs,
            restArg,
            retType,
            effect,
            lowerFunctionBody (name :: aliasFor name) args loweredKwArgs restArg retType body,
            r
        )

    | TDef(name, value, t, r) -> TDef(name, lowerExpr [] false value, t, r)
    | TDefTuple(names, value, t, r) -> TDefTuple(names, lowerExpr [] false value, t, r)
    | TDefMutable(name, value, t, r) -> TDefMutable(name, lowerExpr [] false value, t, r)
    | _ -> decl

let lowerDecl (decl: TDecl) : TDecl = lowerDeclWith (fun _ -> []) decl

let lowerProgram (decls: TDecl list) : TDecl list = List.map lowerDecl decls

// ---------------------------------------------------------------------------
// The promotion assertion
// ---------------------------------------------------------------------------

/// The prefix `(loop ...)` gives the member it generates.
///
/// Checked by name rather than by a flag on the node because the marker has to
/// survive inference, which builds a `TLetRec` of its own from the untyped one.
let loopMemberPrefix = "looplevel"

/// Fails the compile if a generated loop did not become real jumps.
///
/// The `(loop ...)` desugaring emits a shape that is a loop *by construction* —
/// one self-recursive member whose every exit is a tail call. But promotion is a
/// silent optimization: when it declines, the result is still correct, just a
/// closure and a real call per iteration, which shows up as a stack overflow in
/// somebody's program rather than as a failure here. A desugaring bug should
/// break the test suite instead.
///
/// Reaching `TLoop` is *not* the property worth checking: `lowerLetRec` emits
/// one whenever the members are function-shaped, whether or not any call was in
/// tail position. What matters is that no reference to the loop's own name
/// survives in its body — every one should have become a `TRecur`, and any that
/// is left is a call.
let assertLoopsPromoted (decls: TDecl list) : unit =
    let rec mentions (name: string) (e: TypedExpr) =
        match e.Node with
        | TIdent(n, _) when n = name -> true
        | _ -> TypeVisitor.children e |> List.exists (mentions name)

    let rec checkExpr (e: TypedExpr) =
        match e.Node with
        | TLetRec(bindings, _) ->
            for (name, _, _, _) in bindings do
                if name.StartsWith loopMemberPrefix then
                    failwithf
                        $"Internal error at %s{Lexer.formatPos e.Range}: a (loop ...) was left as a recursive binding rather than becoming a loop. Correct, but it allocates a closure per level entry and cannot iterate deeply. This is a bug in the loop desugaring, not in this program."

            TypeVisitor.children e |> List.iter checkExpr

        | TLoop(members, _) ->
            for m in members do
                if m.LoopName.StartsWith loopMemberPrefix && mentions m.LoopName m.Body then
                    failwithf
                        $"Internal error at %s{Lexer.formatPos e.Range}: a (loop ...) still calls itself by name instead of jumping, so its recursive edge was not in tail position. This is a bug in the loop desugaring, not in this program."

            TypeVisitor.children e |> List.iter checkExpr

        | _ -> TypeVisitor.children e |> List.iter checkExpr

    decls
    |> List.iter (fun d ->
        TypeVisitor.mapDecl
            (fun e ->
                checkExpr e
                e)
            d
        |> ignore)
