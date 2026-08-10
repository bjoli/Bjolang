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
module Bjolang.ColourCheck

open Bjolang.Lexer
open Bjolang.TypedAST

/// Does this type describe something whose call is a yield point?
let private suspends (t: HMType) =
    match t with
    | TFun(_, _, EAsync) -> true
    | _ -> false

/// `allowed` is whether the expression is being emitted into an `async` C#
/// member. It is set once, by a bjoroutine's own body, and cleared by every
/// construct that opens a member of its own.
let rec private checkExpr (allowed: bool) (expr: TypedExpr) : unit =
    let descend = checkExpr allowed
    let sealed_ = checkExpr false

    match expr.Node with
    // A lambda is a delegate, and its own colour decides: a `(bjoroutine ...)`
    // is an async lambda and may suspend, a `(fun ...)` may not — whatever it
    // is written inside.
    | TLambda(_, body) -> checkExpr (suspends expr.Type) body

    // A `seq` body is emitted as a C# iterator, and an iterator cannot be
    // async: `yield return` and `await` are mutually exclusive in one member.
    | TSeq body -> sealed_ body

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
            checkExpr true { body with Node = TApply(target, [], []) }
        | _ -> checkExpr true body

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
            members |> List.iter (fun m -> checkExpr (allowed && inlined) m.Body)
            descend body

    // A function-shaped binding is a C# local function; a value binding is just
    // a local, and its initializer runs where it is written.
    | TLet(_, isFun, _, value, body) ->
        (if isFun then sealed_ value else descend value)
        descend body

    | TLetRec(bindings, body) ->
        for (_, isFun, _, value) in bindings do
            if isFun then sealed_ value else descend value

        descend body

    // An `#:async` import. The same rule and the same reason as a call to a
    // bjoroutine — it compiles to an `await` — but it arrives as a different
    // node, because a foreign call is not an application of anything the
    // language has a type for. The fact is read off the call's own metadata
    // rather than by looking the import back up: by the time this runs, the
    // registry that knew is three passes behind.
    | TForeignStaticCall(clrType, methodName, args, Some meta) when meta.Await && not allowed ->
        failwithf
            $"Type Error at %s{formatPos expr.Range}: calling '%s{clrType}.%s{methodName}' is a yield point — it is imported #:async, so it compiles to an await — and a yield point is not allowed here.\n  A yield point may only appear in the body of the bjoroutine it is written in. An ordinary (fun ...), a body-local (defun ...), a (seq ...) and a loop that is not tail-recursive each become a C# member of their own, and a member that is not async cannot suspend.\n  If what you want is to start this and carry on, that is (bjo (%s{methodName} ...)), which is colourless and may be written anywhere."

    | TApply(target, args, kwArgs) ->
        if suspends target.Type && not allowed then
            let what =
                match target.Node with
                | TIdent(name, _) -> $"'%s{name}'"
                | _ -> "this"

            // "calling X is a yield point" rather than "X is a bjoroutine",
            // because `sync` is one too and nobody wrote it with `defbjo`. What
            // is true of both is the property that matters here.
            failwithf
                $"Type Error at %s{formatPos expr.Range}: calling %s{what} is a yield point, and a yield point is not allowed here.\n  A yield point may only appear in the body of the bjoroutine it is written in. An ordinary (fun ...), a body-local (defun ...), a (seq ...) and a loop that is not tail-recursive each become a C# member of their own, and a member that is not async cannot suspend.\n  If this is inside a lambda passed to a higher-order function like map, that is the restriction in concurrency-design.md §3.1: the arrow (-> %%a %%b) does not say its argument may suspend, so the function is emitted once, for the ordinary case. Write a loop instead."

        descend target
        args |> List.iter descend
        kwArgs |> List.iter (snd >> descend)

    | _ -> TypeVisitor.children expr |> List.iter descend

let rec private checkDecl (decl: TDecl) : unit =
    match decl with
    // The one place `allowed` is ever true.
    | TDefun(_, _, _, kwArgs, _, _, effect, body, _) ->
        // A keyword parameter's default is emitted in the method's prologue, so
        // it is inside the same member as the body and inherits its colour.
        kwArgs |> List.iter (fun (_, _, d) -> checkExpr (effect = EAsync) d)
        checkExpr (effect = EAsync) body

    // A module-level value is a static field initialised by the class's static
    // constructor, which cannot be async and has no fiber to suspend.
    | TDef(_, value, _, _)
    | TDefTuple(_, value, _, _)
    | TDefMutable(_, value, _, _) -> checkExpr false value

    | TModule(_, decls, _) -> decls |> List.iter checkDecl
    | TImpl(_, _, _, _, _, methods, _) -> methods |> List.iter checkDecl
    | _ -> ()

let run (decls: TDecl list) : unit = decls |> List.iter checkDecl
