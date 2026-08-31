/// Where a yield point is allowed to be.
///
/// A call to a bjoroutine compiles to an `await`, and an `await` needs an async
/// method around it. So the rule is one sentence: **a yield point may appear
/// only in the body of the bjoroutine it was written in, and not inside
/// anything that becomes a C# member of its own.**
///
/// # Why this is a pass and not a rule in `Inference`
///
/// Because "becomes a C# member of its own" is not decided until
/// `LoopLowering` has run.
///
/// A body-local function and a named `let` loop are the same shape coming out
/// of the parser — an `ELetRec` binding with parameters — and they compile to
/// completely different things. A loop whose recursive edge is in tail position
/// is promoted to a `while`/`switch` in the *enclosing* method, where an
/// `await` is perfectly fine; one that is not stays a C# local function, where
/// it is not, because a local function inside an async method is not itself
/// async.
///
/// Checking this in `Inference` therefore means guessing. Guessing permissively
/// puts the error in generated C# — a method the user never wrote, complaining
/// about `Fiber<T>`, a type the language does not have. Guessing conservatively
/// rejects the loop, which is the one workaround the design's §3.1 offers for
/// the higher-order restriction ("write the loop instead"), and would make
/// bjoroutines close to useless.
///
/// After `LoopLowering` there is nothing to guess: a promoted loop is
/// `TLoop(members, None)` and a local function is `TLet(_, true, ...)`, and
/// they are different nodes.
///
/// # Why running late is safe
///
/// Nothing between type checking and here can *introduce* a yield point.
/// `TraitInline` splices impl bodies, but a trait method cannot be a bjoroutine
/// — the parser refuses the definer and `resolveTemplate` refuses the arrow —
/// so no spliced body contains one. This is the opposite of the argument for
/// running must-use checking *before* `TraitInline` (§8.3): that check would
/// see the same body once per call site, whereas this one cannot see a body it
/// did not already see.
///
/// # Why the message names one construct
///
/// There are six ways to be somewhere a yield point cannot live, and from
/// inside the program they look identical: a call that would be fine one line
/// out is rejected here, and nothing on the page says which enclosing thing
/// objected. So the traversal carries *which* construct sealed it rather than a
/// boolean, and the message names that one and prescribes the fix for that one.
///
/// The pass has always known this exactly — the seal is set at the construct
/// that causes it — and used to throw the knowledge away, emitting a paragraph
/// listing all six for the reader to narrow down by hand. Listing every reason
/// is a worse message than any single one of them.
module Bjolang.ColourCheck

open Bjolang.Lexer
open Bjolang.TypedAST

/// Does this type describe something whose call is a yield point?
///
/// Shared with `Codegen`, which emits an `await` for exactly these. Two
/// spellings of one question are how a program gets accepted here and then
/// rejected by Roslyn.
let private suspends (t: HMType) = callSuspends t

/// What fixed the colour of an ordinary lambda.
///
/// A lambda declares nothing, so the reason it may not suspend is never written
/// inside it — it is the position it was written *in*, one level up. The pin is
/// therefore carried down from the call rather than read off the lambda, which
/// knows only that it is ordinary and not why it had to be.
/// A lambda handed to a .NET `Func` parameter is pinned for a third reason —
/// a delegate's colour is part of its type, and there is no suspending `Func`
/// to ask for — but there is no case for it here, because that message cannot
/// currently be reached from a test. Every `Func`-taking method in the runtime
/// is generic, and the one non-generic BCL candidate, `Task.Run`, does not
/// resolve through `import/extern` at all. `Unpinned` says something true about
/// such a lambda; a branch no fixture can trigger would not.
type private Pin =
    /// Written where nothing constrains it: bound to a name, stored in a
    /// record, returned, or handed to a foreign call. There is nothing to name
    /// but the lambda itself.
    | Unpinned
    /// An argument to a Bjolang function whose parameter is declared `->`.
    | ByParameter of callee: string * declared: HMType

/// Which construct between the yield point and the enclosing bjoroutine became
/// a C# member of its own — which is the whole of why an `await` cannot be
/// written here.
///
/// The innermost wins, which is what overwriting the flag already did when this
/// was a boolean: it is the first thing in the way, and fixing it is what
/// uncovers the next.
type private Site =
    /// A bjoroutine's own body — the one place a yield point belongs.
    | Allowed
    /// The body of an ordinary top-level definition, named.
    | InDefun of string
    /// A module-level initializer, which runs in the static constructor.
    | InModuleValue
    | InLambda of Pin
    /// A `(seq ...)` body, which is emitted as a C# iterator.
    | InSeq
    /// A function-shaped `let` binding, which is emitted as a C# local
    /// function.
    | InLocalFun of string
    /// A loop that stayed a local function instead of becoming a `while`, and
    /// the group names whose use kept it one. The second is what makes the
    /// message true of a mutually recursive pair, where a member escapes
    /// because of its partner rather than because of itself.
    | InEscapingLoop of name: string * escaped: string list

let private isAllowed (site: Site) =
    match site with
    | Allowed -> true
    | _ -> false

/// The same arrow, spelled `-?->`. Rebuilt through `showType` rather than
/// spliced together as text, so the suggestion cannot disagree with how the
/// type prints everywhere else.
let private asPolyArrow (t: HMType) : string =
    match t with
    | TFun(args, ret, _) -> DotNetInterop.showType (TFun(args, ret, EPoly))
    | _ -> DotNetInterop.showType t

/// The lines under a verdict: why this place cannot suspend, and what to do
/// about it.
///
/// Every answer here has to be one that works *today*. Advice that lands the
/// reader in a second error is worse than no advice, which is why `-?->` is
/// never offered as a fix without saying what it does not yet do: declaring it
/// is accepted, and then instantiating it at the suspending colour fails in
/// `unifyEffect` with "copies are not generated yet".
let private explain (site: Site) : string =
    let lines (parts: string list) =
        parts |> List.map (fun p -> "  " + p) |> String.concat "\n"

    match site with
    // Unreachable — `refuse` is only called when the site is sealed — but a
    // wildcard here would silently swallow a `Site` added later.
    | Allowed -> lines [ "A yield point is allowed here, and this message is a bug in the compiler." ]

    | InDefun name ->
        lines
            [ $"'%s{name}' is defined with (defun ...), which is emitted as an ordinary C# method, and an ordinary method cannot await."
              $"Define it with (defbjo ...), or move the suspending call out of it. Note that (defbjo ...) spreads: whoever calls '%s{name}' needs to be one too." ]

    | InModuleValue ->
        lines
            [ "This is not inside a function at all — it is a module-level initializer, emitted into the class's static constructor, which has no fiber under it to suspend."
              "Move the work into a (defbjo ...) and call it from somewhere a fiber is running." ]

    | InSeq ->
        lines
            [ "It is inside a (seq ...) body — which is also what a (seql ...) becomes — and a sequence is emitted as a C# iterator, where `yield return` and `await` are mutually exclusive in one member."
              "A stream of values produced by suspending work is a channel rather than a sequence: fill one with (bjo ...) and read it. For a port that is written already — (std ports) has port->chan, which is port->seq with a channel where the iterator was. If the sequence is short, build the whole list first and yield from that." ]

    | InLocalFun name ->
        lines
            [ $"It is inside '%s{name}', a body-local function, and the definition around it is not a bjoroutine. A local function may suspend — it is emitted async and its callers await it — but its callers are here, and an ordinary member cannot await."
              $"Make the enclosing definition a (defbjo ...). '%s{name}' needs no annotation of its own: a body-local function takes its colour from what its body reaches." ]

    | InEscapingLoop(name, escaped) ->
        // One reason left, now that a loop emitted as local functions may be
        // async: the group is *used as a value*, and the use fixed its type as
        // an ordinary function. Recursion no longer reaches here at all,
        // whether tail or not.
        //
        // The blamed name is not always the member the yield point sits in — a
        // mutually recursive group escapes through whichever of its names was
        // passed somewhere — so `escapingNames` answers that rather than the
        // message guessing.
        let blamed = if escaped.IsEmpty then [ name ] else escaped
        let subject = blamed |> List.map (fun n -> $"'%s{n}'") |> String.concat " and "

        let cause =
            if blamed = [ name ] then
                $"'%s{name}' is a loop that is also used as a value — passed somewhere that wants an ordinary function. That use is what fixes its type as one that does not suspend, and the loop is emitted to match."
            else
                $"'%s{name}' is one of a group of loops, and %s{subject} is used as a value — passed somewhere that wants an ordinary function. Members of a group share one colour, so that use fixes the whole group as ordinary."

        lines
            [ cause
              $"Call %s{subject} instead of passing it: a loop that is only called or jumped to takes the colour of the bjoroutine around it, and may then suspend. If it has to be a value, the parameter receiving it decides — an ordinary one means an ordinary function." ]

    | InLambda pin ->
        let where =
            match pin with
            | Unpinned ->
                "It is inside an ordinary (fun ...), which is emitted as a delegate of its own, and a delegate that is not async cannot await."
            | ByParameter(callee, declared) ->
                $"It is inside an ordinary (fun ...) passed to '%s{callee}', whose parameter is declared %s{DotNetInterop.showType declared} — an arrow that does not say it may suspend. So '%s{callee}' is emitted once, for the ordinary case, and the lambda handed to it has to match."

        let loop =
            "Write the loop instead: a (loop ...) lowers to a while in the enclosing bjoroutine, so a yield point inside one is still inside the bjoroutine."

        match pin with
        | ByParameter(_, declared) ->
            lines
                [ where
                  loop
                  $"Declaring the parameter %s{asPolyArrow declared} is the eventual answer, but the suspending copy is not generated yet." ]
        | Unpinned -> lines [ where; loop ]

/// One verdict, one reason, one answer.
///
/// `lead` says what suspends rather than what it was defined with — "calling
/// 'sync' is a yield point", not "'sync' is a bjoroutine" — because `sync` is
/// one and nobody wrote it with `defbjo`. The property is what is true of all
/// of them.
///
/// A `lead` that carries an aside parenthesises it rather than setting it off
/// with dashes, because the verdict is appended here and an unclosed dash would
/// swallow it: "…compiles to an await, and a yield point is not allowed here"
/// reads as though the await were the thing not allowed.
let private refuse (site: Site) (range: Range) (lead: string) (hint: string list) : unit =
    let detail =
        explain site :: (hint |> List.map (fun h -> "  " + h)) |> String.concat "\n"

    failwithf $"Type Error at %s{formatPos range}: %s{lead}, and a yield point is not allowed here.\n%s{detail}"

/// `site` is where the expression is being emitted. It is set to `Allowed` by a
/// bjoroutine's own body, and replaced by every construct that opens a member
/// of its own with the reason that construct is one.
let rec private checkExpr (site: Site) (expr: TypedExpr) : unit =
    let descend = checkExpr site

    match expr.Node with
    // A lambda is a delegate, and its own colour decides: a `(bjoroutine ...)`
    // is an async lambda and may suspend, a `(fun ...)` may not — whatever it
    // is written inside. Nothing pinned this one that we can see from here;
    // the argument positions below know better and say so.
    | TLambda(_, body) -> checkLambda Unpinned expr body

    // A `seq` body is emitted as a C# iterator, and an iterator cannot be
    // async: `yield return` and `await` are mutually exclusive in one member.
    | TSeq body -> checkExpr InSeq body

    // `(bjo (f x y))` splits in two, and the halves have different colours.
    //
    // The operands are evaluated *here*, in the enclosing member, so they get
    // the ambient answer — that is what makes `(bjo (handle (next-job!)))`
    // honest about where `next-job!` runs.
    //
    // The call itself becomes the body of an async lambda, so it may suspend
    // whatever the caller is. Which is also why `bjo` is colourless: spawning
    // does not infect its caller, and a plain `main` can start workers without
    // becoming a bjoroutine.
    | TBjo body ->
        match body.Node with
        | TApply(target, args, kwArgs) ->
            descend target
            args |> List.iter descend
            kwArgs |> List.iter (snd >> descend)
            // The call is checked in the child's colour, which is always async.
            checkExpr Allowed { body with Node = TApply(target, [], []) }
        | _ -> checkExpr Allowed body

    // `TLoop(_, None)` *is* the enclosing member's body — a `while` over a
    // state switch, in the same method — so it inherits.
    //
    // `TLoop(_, Some body)` — a named `let`, or a `(loop ...)` — depends on
    // whether the emitter will inline it or give its members local functions of
    // their own. `isInlinedLoop` is the same predicate `Codegen` decides with,
    // called rather than copied: a second copy would be a program this accepts
    // and Roslyn rejects.
    //
    // Getting this right is what makes the design's §3.1 workaround real. "You
    // cannot pass a suspending lambda to `map`; write the loop instead" is only
    // an answer if the loop can actually suspend.
    | TLoop(members, bodyOpt) ->
        match bodyOpt with
        | None -> members |> List.iter (fun m -> descend m.Body)
        | Some body ->
            let inlined = LoopLowering.isInlinedLoop members body

            members
            let escaped = if inlined then [] else LoopLowering.escapingNames members body

            // A group that stays C# local functions may still suspend — it is
            // emitted `async` like any other local function, and `EffectGraph`
            // has already decided that for the group. What is left to refuse is
            // the group that could *not* be given the colour: one in an
            // ordinary member, whose callers cannot await it.
            // Three ways a member's body is checked in the enclosing site
            // rather than blamed on the loop, and only the third is new:
            // inlined, so it *is* the enclosing member; given the suspending
            // colour, so it may await; or written somewhere that could not have
            // awaited anyway, where the loop is not the reason and saying it
            // was would send the reader to fix the wrong thing.
            let memberSite (m: TLoopMember) =
                if inlined || groundEffect m.Effect = EAsync || not (isAllowed site) then
                    site
                else
                    InEscapingLoop(m.LoopName, escaped)

            members |> List.iter (fun m -> checkExpr (memberSite m) m.Body)

            descend body

    // A function-shaped binding is a C# local function; a value binding is just
    // a local, and its initializer runs where it is written.
    | TLet(name, isFun, _, value, body) ->
        (if isFun then descendBinding name value else descend value)
        descend body

    | TLetRec(bindings, body) ->
        for (name, isFun, _, value) in bindings do
            if isFun then descendBinding name value else descend value

        descend body

    // An `#:async` import. The same rule and the same reason as a call to a
    // bjoroutine — it compiles to an `await` — but it arrives as a different
    // node, because a foreign call is not an application of anything the
    // language has a type for. The fact is read off the call's own metadata
    // rather than by looking the import back up: by the time this runs, the
    // registry that knew is three passes behind.
    | TForeignStaticCall(clrType, methodName, _, Some meta) when meta.Await && not (isAllowed site) ->
        refuse
            site
            expr.Range
            $"calling '%s{clrType}.%s{methodName}' is a yield point (it is imported #:async, so it compiles to an await)"
            [ $"If what you want is to start this and carry on, that is (bjo (%s{methodName} ...)), which is colourless and may be written anywhere." ]

    // The same rule for an `#:async` import that names an *instance* method.
    // It arrives as the node `(.Method x ...)` also produces, and only the
    // metadata says which of the two it was — which is the point of reading the
    // fact there rather than from the registry.
    | TDotMethodCall(_, methodName, _, Some meta) when meta.Await && not (isAllowed site) ->
        refuse
            site
            expr.Range
            $"calling '%s{meta.DeclaringType}.%s{methodName}' is a yield point (it is imported #:async, so it compiles to an await)"
            [ $"If what you want is to start this and carry on, that is (bjo (%s{methodName} ...)), which is colourless and may be written anywhere." ]

    // A method dispatched through a dictionary. It arrives as a node of its own
    // rather than as a `TApply`, so there is no arrow here for the case below
    // to read — the colour is on the node, put there by `Lowering` from the
    // trait's declaration.
    //
    // The trait's colour and not the implementation's, which is the point of
    // the restriction: this call has to know whether it awaits before it knows
    // which implementation answers it.
    | TInterfaceCall(_, mName, eff, dict, args) ->
        if pruneEffect eff = EAsync && not (isAllowed site) then
            refuse
                site
                expr.Range
                $"calling '%s{mName}' is a yield point (the trait that declares it writes -bjo->, so every implementation of it may suspend)"
                []

        checkExpr site dict
        args |> List.iter (checkExpr site)

    | TApply(target, args, kwArgs) ->
        if suspends target.Type && not (isAllowed site) then
            let what =
                match target.Node with
                | TIdent(name, _) -> $"'%s{name}'"
                | _ -> "this"

            refuse site expr.Range $"calling %s{what} is a yield point" []

        descend target

        // Which parameter an argument lands on is visible here and nowhere
        // else, so the pin is decided here and carried down.
        let pinAt (i: int) =
            match target.Type, target.Node with
            | TFun(paramTypes, _, _), TIdent(callee, _) when i < List.length paramTypes ->
                match paramTypes[i] with
                // Only a parameter genuinely declared `->` is described as one.
                // An instantiated `-?->` arrives as a bound cell, and calling
                // that "declared ->" would be a lie about the source.
                | TFun(_, _, ESync) -> ByParameter(callee, paramTypes[i])
                | _ -> Unpinned
            | _ -> Unpinned

        args |> List.iteri (fun i a -> descendArg site (pinAt i) a)
        // A keyword argument's declared type is not positional in `TFun`, so
        // there is nothing here to name it by.
        kwArgs |> List.iter (snd >> descendArg site Unpinned)

    | _ -> TypeVisitor.children expr |> List.iter descend

/// A lambda's body, checked in the colour the lambda itself is, with `pin`
/// describing what made it that if the caller could tell.
and private checkLambda (pin: Pin) (lam: TypedExpr) (body: TypedExpr) : unit =
    if suspends lam.Type then
        checkExpr Allowed body
    else
        checkExpr (InLambda pin) body

/// Descend into an argument, telling a lambda written there what pinned it.
/// Anything else is an ordinary subexpression evaluated in the caller.
and private descendArg (site: Site) (pin: Pin) (arg: TypedExpr) : unit =
    match arg.Node with
    | TLambda(_, body) -> checkLambda pin arg body
    | _ -> checkExpr site arg

/// Descend into a function-shaped binding — a body-local `(defun ...)`, which
/// arrives here as a name bound to a lambda.
///
/// The lambda *is* the local function, so its body is checked as one and the
/// message can say which. Letting the ordinary `TLambda` case have it instead
/// would be true but useless: it would report an anonymous `(fun ...)` for
/// something the reader gave a name to and can see on the page.
and private descendBinding (name: string) (value: TypedExpr) : unit =
    match value.Node with
    | TLambda(_, body) when not (suspends value.Type) -> checkExpr (InLocalFun name) body
    | _ -> checkExpr (InLocalFun name) value

/// A `TDefun`'s own effect is the only source of `Allowed`, and every
/// expression it holds — its body and its keyword defaults, which are emitted
/// in the method's prologue — is inside that one C# member.
///
/// Everything else is emitted somewhere that cannot be async: a module-level
/// value is a static field, initialised by the class's static constructor,
/// which has no fiber to suspend.
let private checkDecl (registry: TraitRegistry) (decl: TDecl) : unit =
    decl
    |> TypeVisitor.mapDeclWithContext (fun owner e ->
        let site =
            match owner with
            | TDefun(_, _, _, _, _, _, EAsync, _, _) -> Allowed
            | TDefun(name, _, _, _, _, _, _, _, _) -> InDefun name
            | _ -> InModuleValue

        match owner with
        // A generated copy has no source of its own — its ranges are those of
        // the definition it was copied from. So the position is right and the
        // framing is wrong: the reader is told a yield point is not allowed at
        // a line where, as written, there is no yield point. The call only
        // becomes one in the copy, and the copy is the compiler's doing.
        | TDefun(name, _, _, _, _, _, _, _, _) when Set.contains name registry.GeneratedCopies ->
            try
                checkExpr site e
            with ex ->
                // The generated name is stripped when this prints, so it reads
                // as the written one.
                failwithf
                    $"%s{ex.Message}\n  This is the suspending copy of '%s{name}', which exists because its signature declares a -?-> parameter. As written the definition is fine — it is the second colour that has nowhere to put the yield point.\n  Either that parameter does not need to take both colours, in which case declare it -> and there is only one copy; or the construct in the way has to go, since both copies are made from this one body."
        | _ -> checkExpr site e

        e)
    |> ignore

let run (registry: TraitRegistry) (decls: TDecl list) : unit =
    decls |> List.iter (checkDecl registry)
