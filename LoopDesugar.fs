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

module Bjolang.LoopDesugar

open Lexer
open Bjolang.Ast
open Bjolang.Hygiene

/// One clause of a `(loop ...)`, still unparsed.
///
/// The clause list is flat and there is no body position: every clause carries
/// its own condition, and iteration order is clause order.
///
/// Which is also why `:for`, `:with` and `:let` bind **sequentially** and stay
/// that way now that `let`'s bindings are simultaneous. A clause list is an
/// explicitly ordered thing, and an inner level's sequence is normally written
/// in terms of the outer level's variable — `(:for row rows) (:for cell row)` —
/// so a simultaneous reading would forbid the ordinary nested loop rather than
/// merely change what it means.
type private LoopClause =
    | LFor of SExpr * SExpr * Range
    /// `(:with pat start [update [end]])`. A loop variable that carries its own
    /// state instead of drawing it from a cursor — the general case of a
    /// sequence whose state *is* the value.
    ///
    /// Structurally it is a `:for` in every respect that matters: it belongs to
    /// a level, it advances in lockstep with that level's cursors, and its `end`
    /// is one of the level's termination tests. The only difference is where the
    /// value comes from.
    ///
    /// `update` absent is a loop-invariant binding, and contributes no override
    /// to the jump rather than a self-assignment. `end` absent is a `:with` that
    /// never ends the level on its own, and contributes no test rather than a
    /// folded constant.
    | LWith of SExpr * SExpr * SExpr option * SExpr option * Range
    | LLet of SExpr * SExpr * Range
    /// `(:when-let pat expr)` and `(:break-let pat expr)`. A `:let` whose
    /// pattern is allowed to fail, and which says where the failure goes: the
    /// next iteration of this level, or the finish block.
    ///
    /// The two edges are separate clauses rather than one with a modifier
    /// because neither is the default. A `chan-recv` that answers `None`
    /// answers it forever, so a skip where a break was meant is a loop that
    /// spins rather than one that stops.
    ///
    /// `true` is the breaking one.
    | LRefutableLet of SExpr * SExpr * bool * Range
    | LDo of SExpr list * Range
    | LWhen of SExpr * Range
    | LSubloop of Range
    /// `(:acc name (collector args...) #:when cond)`. The `#:when` is a clause
    /// modifier the loop form intercepts, never something the collector sees: it
    /// mentions loop variables, and construction arguments are hoisted out of
    /// the loop entirely.
    | LAcc of string * SExpr * SExpr option * Range
    /// Ends the whole loop when the condition holds, before the rest of the
    /// iteration runs. Routes through the finish block like every other exit.
    | LBreak of SExpr * Range
    /// Abandons the current subloop and resumes the enclosing level's next
    /// iteration — an early return from a subloop, not an iteration skip. The
    /// same edge inner exhaustion takes.
    | LEndSubloop of SExpr * Range
    /// Ends the loop *after* the current iteration completes.
    ///
    /// Not a mechanism of its own: it is a `:break` on a hidden accumulator
    /// holding the previous iteration's verdict, and the two sit at the position
    /// `:final` occupied with the break first. An accumulator slot read at the
    /// top of iteration N holds what was written at the end of N-1, which is
    /// exactly "finish this one, then stop". Reversed, the break would read the
    /// value written this iteration and `:final` would collapse into `:break`.
    | LFinal of SExpr * Range

/// An accumulator's slot, after its collector form has been split.
type private AccSlot =
    { /// Prologue binding holding the collector value.
      Collector: string
      /// The slot's name. A user accumulator keeps the name it was declared
      /// with — it is in scope through the loop, and each `:acc` rebinds it, so
      /// a later clause reads the value as of its own position.
      Name: string
      CollectorExpr: Expr
      StepForm: SExpr
      Modifier: SExpr option
      /// `:final`'s accumulator, which is not the author's and must not appear
      /// in the finish block's result.
      Hidden: bool
      /// The level whose body steps it. Every accumulator is a slot on *every*
      /// member — they are hoisted — but only one level runs its step.
      Level: int
      Range: Range }

/// A `(loop ...)` clause's own range, for diagnostics.
let private loopClauseRange (c: LoopClause) : Range =
    match c with
    | LFor(_, _, r)
    | LWith(_, _, _, _, r)
    | LLet(_, _, r)
    | LRefutableLet(_, _, _, r)
    | LDo(_, r)
    | LWhen(_, r)
    | LSubloop r
    | LAcc(_, _, _, r)
    | LBreak(_, r)
    | LEndSubloop(_, r)
    | LFinal(_, r) -> r

// ---------------------------------------------------------------------------
// n-ary arithmetic and comparison
// ---------------------------------------------------------------------------

/// Whether `(loop ...)` is the loop facility rather than a call to something
/// named `loop`.
///
/// `(let loop ((i 0)) ... (loop (+ i 1)))` is how every named `let` in the
/// language recurses, so the head symbol cannot be claimed outright. A clause is
/// a keyword-headed list and an argument expression is not, which tells the two
/// apart without reserving the name.
let rec internal isLoopForm (args: SExpr list) : bool =
    match args with
    | SList(SAtom { Token = Keyword _ } :: _, _) :: _ -> true
    // A named loop puts its name first, so the clause is one further along.
    // `(loop f (g 1))` — a call to a named `let` taking a function and an
    // argument — still is not one, because `(g 1)` is not keyword-headed.
    | SAtom { Token = Symbol _ } :: SList(SAtom { Token = Keyword _ } :: _, _) :: _ -> true
    | _ -> false

/// Reads one clause. Nothing is desugared here.
and private parseLoopClause (s: SExpr) : LoopClause =
    match s with
    | SList(SAtom { Token = Keyword "for" } :: rest, r) ->
        match rest with
        | [ pat; sequence ] -> LFor(pat, sequence, r)
        | _ -> failwithf $"Invalid (:for ...) at %s{Lexer.formatPos r}. Expected: (:for pattern sequence)"

    // Two expressions is a loop-invariant binding, three the usual recurrence,
    // four one that ends the level on its own. Nothing is optional in the
    // middle: an omitted `update` with a given `end` would have to be spelled,
    // and there is no spelling worth inventing for it — write `var` and mean it.
    | SList(SAtom { Token = Keyword "with" } :: rest, r) ->
        match rest with
        | [ pat; start ] -> LWith(pat, start, None, None, r)
        | [ pat; start; update ] -> LWith(pat, start, Some update, None, r)
        | [ pat; start; update; endCond ] -> LWith(pat, start, Some update, Some endCond, r)
        | _ ->
            failwithf
                $"Invalid (:with ...) at %s{Lexer.formatPos r}. Expected: (:with pattern start [update [end]])"

    | SList(SAtom { Token = Keyword "let" } :: rest, r) ->
        match rest with
        | [ pat; value ] -> LLet(pat, value, r)
        | _ -> failwithf $"Invalid (:let ...) at %s{Lexer.formatPos r}. Expected: (:let pattern expr)"

    | SList(SAtom { Token = Keyword "when-let" } :: rest, r) ->
        match rest with
        | [ pat; value ] -> LRefutableLet(pat, value, false, r)
        | _ -> failwithf $"Invalid (:when-let ...) at %s{Lexer.formatPos r}. Expected: (:when-let pattern expr)"

    | SList(SAtom { Token = Keyword "break-let" } :: rest, r) ->
        match rest with
        | [ pat; value ] -> LRefutableLet(pat, value, true, r)
        | _ -> failwithf $"Invalid (:break-let ...) at %s{Lexer.formatPos r}. Expected: (:break-let pattern expr)"

    | SList(SAtom { Token = Keyword "do" } :: rest, r) ->
        if rest.IsEmpty then
            failwithf $"Invalid (:do ...) at %s{Lexer.formatPos r}. Expected: (:do expr ...)"

        LDo(rest, r)

    | SList(SAtom { Token = Keyword "when" } :: rest, r) ->
        match rest with
        | [ cond ] -> LWhen(cond, r)
        | _ -> failwithf $"Invalid (:when ...) at %s{Lexer.formatPos r}. Expected: (:when cond)"

    | SList([ SAtom { Token = Keyword "subloop" } ], r) -> LSubloop r

    | SList(SAtom { Token = Keyword "end-subloop-if" } :: rest, r) ->
        match rest with
        | [ cond ] -> LEndSubloop(cond, r)
        | _ ->
            failwithf $"Invalid (:end-subloop-if ...) at %s{Lexer.formatPos r}. Expected: (:end-subloop-if cond)"

    | SList(SAtom { Token = Keyword "acc" } :: SAtom { Token = Symbol name } :: collector :: rest, r) ->
        let modifier =
            match rest with
            | [] -> None
            | [ SAtom { Token = Keyword "when" }; cond ] -> Some cond
            | _ ->
                failwithf
                    $"Invalid (:acc ...) at %s{Lexer.formatPos r}. Expected: (:acc name (collector ...) [#:when cond])"

        LAcc(name, collector, modifier, r)

    | SList(SAtom { Token = Keyword "acc" } :: _, r) ->
        failwithf $"Invalid (:acc ...) at %s{Lexer.formatPos r}. Expected: (:acc name (collector ...) [#:when cond])"

    // Both take a condition rather than being guarded by a preceding `:when`:
    // clauses do not compose, so there is no bare `(:break)` to be reached
    // conditionally.
    | SList(SAtom { Token = Keyword "break" } :: rest, r) ->
        match rest with
        | [ cond ] -> LBreak(cond, r)
        | _ -> failwithf $"Invalid (:break ...) at %s{Lexer.formatPos r}. Expected: (:break cond)"

    | SList(SAtom { Token = Keyword "final" } :: rest, r) ->
        match rest with
        | [ cond ] -> LFinal(cond, r)
        | _ -> failwithf $"Invalid (:final ...) at %s{Lexer.formatPos r}. Expected: (:final cond)"

    | SList(SAtom { Token = Keyword k } :: _, r) ->
        failwithf $"Unknown (loop ...) clause ':%s{k}' at %s{Lexer.formatPos r}"

    | _ ->
        let r = getRange s
        failwithf $"Expected a (loop ...) clause at %s{Lexer.formatPos r}, which is a keyword-headed list like (:for x xs)"

/// Binds a `:for` or `:let` pattern.
///
/// The pattern is there to destructure and must always match: it is not a
/// filter, and a pattern that could fail would need somewhere to send the
/// failure. So only the shapes that cannot fail are accepted.
and private bindLoopPattern (pat: SExpr) (value: Expr) (body: Expr) (r: Range) : Expr =
    // Expanded first, so that a pattern macro rewriting to `(Tuple a b)` is one
    // of the shapes below rather than something this refuses. The head is
    // unmarked for the reason `parsePattern` unmarks one: `Tuple` in a template
    // is dispatched on, not bound.
    match patternExpandHook pat with
    | Some expansion -> bindLoopPattern (stripHeadMark expansion.Form) value body r
    | None ->

    match pat with
    | SAtom { Token = Symbol name } -> ELet(name, false, [], None, value, body, r)

    | SList(SAtom { Token = Symbol "Tuple" } :: parts, pr) when
        not parts.IsEmpty
        && parts
           |> List.forall (function
               | SAtom { Token = Symbol _ }
               | SAtom { Token = Comma } -> true
               | _ -> false)
        ->
        let names =
            parts
            |> List.choose (function
                | SAtom { Token = Symbol n } -> Some n
                | _ -> None)

        ELetTuple(names, value, body, pr)

    | _ ->
        let pr = getRange pat
        failwithf
            $"Invalid pattern at %s{Lexer.formatPos pr}: a (loop ...) pattern only destructures and must always match, so it has to be a name or (Tuple a b ...)."

/// Splits a collector form into the collector *value* and the per-iteration
/// step expression.
///
/// The convention: the last positional argument is the step expression, and
/// everything before it — plus every keyword argument — constructs the
/// collector. `(listing b)` is `listing` stepping `b`; `(folding #f test)` is
/// `(folding #f)` stepping `test`.
and private splitCollector (fns: ParseFns) (s: SExpr) : Expr * SExpr =
    match s with
    | SList(SAtom { Token = Symbol head } :: args, r) ->
        let rec split positional keywords rest =
            match rest with
            | [] -> List.rev positional, List.rev keywords
            | (SAtom { Token = Keyword _ } as k) :: value :: tl -> split positional ((k, value) :: keywords) tl
            | [ SAtom { Token = Keyword k } ] ->
                failwithf $"Keyword argument '#:%s{k}' at %s{Lexer.formatPos r} has no value"
            | SAtom { Token = Comma } :: tl -> split positional keywords tl
            | x :: tl -> split (x :: positional) keywords tl

        let positional, keywords = split [] [] args

        match List.rev positional with
        | [] ->
            failwithf
                $"Collector at %s{Lexer.formatPos r} has no step expression. The last positional argument is what is accumulated each iteration, as in (listing x)."
        | stepForm :: revConstruction ->
            let construction = List.rev revConstruction

            let collector =
                if construction.IsEmpty && keywords.IsEmpty then
                    EIdent(head, r)
                else
                    let kwArgs =
                        keywords |> List.collect (fun (k, v) -> [ fns.Expr k; fns.Expr v ])

                    EApp(EIdent(head, r), (construction |> List.map fns.Expr) @ kwArgs, r)

            collector, stepForm

    | _ ->
        let r = getRange s
        failwithf
            $"Expected a collector at %s{Lexer.formatPos r}, as in (listing x) or (folding seed expr)"

/// `(seql ...)` — a loop that produces a lazy sequence instead of a value.
///
/// A plain rewrite over the clause list, and deliberately nothing more:
///
///     (seql clauses...)        →  (seq (loop clauses'...))
///     (seql clauses... => e)   →  (seq (loop clauses'...) (yield e))
///     (:yield e)               →  (:do (yield e))
///
/// Every level, cursor, `:break` and `:with` is the loop facility's, unchanged.
/// The loop group is emitted inline as a `while`/`switch` in the sequence's own
/// iterator method, and a `yield return` inside that switch is ordinary C# —
/// which is the only reason this can be a rewrite rather than a second
/// implementation. Levels included: a nested loop is one merged switch, not a
/// function, so `:subloop` needs nothing special here.
///
/// `:acc` is refused. A `seql` hands its elements out one at a time and has no
/// result to accumulate into, and the ban is also what keeps the rewrite honest:
/// an accumulator would have to be read *after* the loop, which is exactly where
/// the `=>` yield now lives.
///
/// That placement is the one thing here that is not free. The `=>` yield goes
/// *outside* the loop rather than into its finish block, because a finish block
/// is emitted as a C# local function and C# forbids `yield return` inside one.
/// Outside costs nothing: with no accumulators a `=>` expression cannot mention
/// anything the loop bound, and every exit leaves the loop and then reaches the
/// yield — which is what running it in the finish block would have meant.
and internal desugarSeqLoop (fns: ParseFns) (allForms: SExpr list) (r: Range) : Expr =
    let isArrow =
        function
        | SAtom { Token = Symbol "=>" } -> true
        | _ -> false

    let clauseForms, finishForm =
        match allForms |> List.tryFindIndex isArrow with
        | None -> allForms, None
        | Some i when i = allForms.Length - 2 -> allForms |> List.take i, Some(List.last allForms)
        | Some i ->
            let ar = getRange allForms[i]
            failwithf
                $"'=>' at %s{Lexer.formatPos ar} must be followed by exactly one expression, at the end of the seql."

    let rewriteClause (s: SExpr) : SExpr =
        match s with
        | SList(SAtom { Token = Keyword "yield" } :: rest, cr) ->
            match rest with
            | [ value ] ->
                SList(
                    [ SAtom { Token = Keyword "do"; Range = cr }
                      SList([ SAtom { Token = Symbol "yield"; Range = cr }; value ], cr) ],
                    cr
                )
            | _ -> failwithf $"Invalid (:yield ...) at %s{Lexer.formatPos cr}. Expected: (:yield expr)"

        | SList(SAtom { Token = Keyword "acc" } :: _, cr) ->
            failwithf
                $"(:acc ...) at %s{Lexer.formatPos cr} has no meaning in a (seql ...): a seql yields its elements one at a time rather than accumulating a result. Use (:yield expr), or write a (loop ...) if you wanted the fold."

        | other -> other

    if clauseForms.IsEmpty then
        failwithf $"Invalid seql at %s{Lexer.formatPos r}: it has no clauses"

    let loopExpr = desugarLoop fns (clauseForms |> List.map rewriteClause) r

    let body =
        match finishForm with
        | None -> loopExpr
        // The loop runs for effect and then the trailing yield does; `_` is how
        // every other statement position in this file is spelled.
        | Some e -> ELet("_", false, [], None, loopExpr, EYield(fns.Expr e, getRange e), r)

    ESeq(body, r)

/// Desugars `{collector expr clause...}`, which the reader hands over as
/// `(comprehension collector expr clause...)`.
///
/// The whole construct is one rewrite:
///
///     {listing (* a a) (:for a (range 0 100))}
///     => (loop (:for a (range 0 100)) (:acc G (listing (* a a))) => G)
///
/// Everything before the first clause is the accumulator form, *verbatim*. That
/// is what lets a collector take however many construction arguments it likes
/// without this function knowing any of their arities:
///
///     {folding 0 a (:for a xs)}  => (:acc G (folding 0 a))
///
/// The clauses are passed through untouched and in order, which is the point of
/// taking them parenthesized: everything `loop` already understands — `:when`,
/// `:break`, `:let`, `:with`, `:final`, `:subloop` — works here on the day this
/// is written, and keeps meaning exactly what it means in a loop. There is no
/// second dialect of clause to learn.
///
/// Between the accumulator form and the first clause there may be a *loose*
/// `:when`, which becomes the `#:when` on the generated `:acc`:
///
///     {listing a :when (even? a) (:for a xs)}
///     => (loop (:for a xs) (:acc G (listing a) #:when (even? a)) => G)
///
/// A parenthesized `(:when ...)` is the loop's own clause and skips the rest of
/// the iteration; the loose one only gates the step. They are different things,
/// so they are written in different places: the loose one belongs to the
/// accumulator and sits with it, and among the clauses — where it would read as
/// one more clause — it is an error.
///
/// The accumulator's name is invented, and that is what forbids a second one:
/// a comprehension is an expression that produces a value, and with the name
/// out of reach there is nothing for a `(:acc ...)` of the caller's own to be
/// combined with. Writing one is an error rather than a silent second result.
and internal desugarComprehension (fns: ParseFns) (allForms: SExpr list) (r: Range) : Expr =
    let at t = SAtom { Token = t; Range = r }
    let isLoose = function SAtom { Token = Keyword _ } -> true | _ -> false
    let opensClauses = function SList(SAtom { Token = Keyword _ } :: _, _) -> true | f -> isLoose f

    // Everything up to the first clause is the accumulator form, verbatim. A
    // clause is keyword-*headed*, which is what tells the `(:for ...)` ending
    // the form apart from the `(* a a)` inside it.
    let accParts, tail =
        match allForms |> List.tryFindIndex opensClauses with
        | Some i -> List.take i allForms, List.skip i allForms
        | None -> allForms, []

    if List.length accParts < 2 then
        failwithf
            $"Invalid comprehension at %s{Lexer.formatPos r}. Expected {{collector expr clause...}}, as in {{listing (* a a) (:for a (range 0 100))}}."

    // A loose `:when` gates the accumulation; a parenthesized `(:when ...)` is
    // the loop's own and skips the iteration. It belongs to the head, so that is
    // where it is written — between the accumulator form and the first clause.
    let clauses, accWhen =
        match tail with
        | SAtom { Token = Keyword "when"; Range = kr } :: rest ->
            match rest with
            | guard :: clauses when not (opensClauses guard) -> clauses, Some guard
            | _ -> failwithf $"':when' at %s{Lexer.formatPos kr} has no condition."
        | _ -> tail, None

    // Among the clauses, position no longer tells the two apart: a loose `:when`
    // there reads as one more clause and means something else. It is refused
    // rather than given a second spelling.
    for form in clauses do
        match form with
        | SAtom { Token = Keyword "when"; Range = kr } ->
            failwithf
                $"':when' at %s{Lexer.formatPos kr} must be written (:when ...) here. The loose ':when' gates the accumulation and goes before the first clause: {{listing a :when (even? a) (:for a xs)}}."
        | SAtom { Token = Keyword k; Range = kr } ->
            failwithf
                $"':%s{k}' at %s{Lexer.formatPos kr} must be written (:%s{k} ...). Only ':when' may be loose, and only before the first clause."
        | _ -> ()

    // One result, named by the collector — so there is nothing for a second
    // accumulator or a finish expression to do. Anything else that is not a
    // clause `parseLoopClause` rejects, with the message it already has.
    clauses
    |> List.iter (function
        | SList(SAtom { Token = Keyword "acc" } :: _, cr)
        | SAtom { Token = Symbol "=>"; Range = cr } ->
            failwithf
                $"A comprehension at %s{Lexer.formatPos cr} has one result, and its collector names it: (:acc ...) and => mean nothing here. Write a (loop ...) for more than one."
        | _ -> ())

    match accParts with
    // `seqing` yields rather than folds, so it is a `seql` with no accumulator.
    | [ SAtom { Token = Symbol "seqing" }; expr ] ->
        let guard = accWhen |> Option.toList |> List.map (fun c -> SList([ at (Keyword "when"); c ], r))
        desugarSeqLoop fns (clauses @ guard @ [ SList([ at (Keyword "yield"); expr ], getRange expr) ]) r

    | SAtom { Token = Symbol "seqing" } :: _ ->
        failwithf $"{{seqing ...}} at %s{Lexer.formatPos r} takes exactly one expression to yield."

    | _ ->
        let name = Gensym.fresh "comp"
        let guard = accWhen |> Option.toList |> List.collect (fun c -> [ at (Keyword "when"); c ])
        let acc = SList([ at (Keyword "acc"); at (Symbol name); SList(accParts, r) ] @ guard, r)
        desugarLoop fns (clauses @ [ acc; at (Symbol "=>"); at (Symbol name) ]) r

/// Rewrites `(:until-cancelled)` and `(:until-cancelled token)` into clauses the
/// loop facility already has.
///
/// For the compute loop with no sync point to hang an `until-cancelled` event
/// on. The zero-arity form becomes two clauses:
///
///     (:with %tok (parameter-ref current-cancel))   ;; loop ENTRY, wherever the
///                                                   ;; clause was written
///     (:break (cancelled? %tok))                    ;; left exactly where it was
///
/// Two-expression `:with` is already the loop-invariant binding form, so this
/// needs no new machinery. The break stays put because clause order decides
/// *where in an iteration* the exit happens, and that is the author's to say.
///
/// **The hoist is the point.** `current-cancel` holds a field on `DynEnv` — it
/// is one of the three parameters that do — so `parameter-ref` on it is
/// `FiberContext.Current`, a cast and a null test rather than a champ descent.
/// A thread-static read every iteration is still more than a loop whose body is
/// arithmetic should pay for something that cannot change under it.
///
/// *Why it is safe:* only `parameterize` can rebind it, and that is a
/// `try/finally`, so it has restored before control reaches the loop head again;
/// `parameter-push!`/`dyn-restore!` are not surface API; and `FiberContext` is
/// reinstated around every suspension, so a fiber sees the same token before and
/// after a `sync`.
///
/// *Why loop entry rather than function entry:* a loop inside a `parameterize`
/// in the same function would otherwise read what was ambient *outside* it,
/// which is usually the root token — the one that never fires.
///
/// **The limitation this locks in:** the ambient token cannot change under a
/// running loop. True today, because a fiber's dynamic environment is written
/// only by lexically scoped push/restore on that same fiber. It forecloses a
/// supervisor reaching into a running child to swap its deadline; if that is
/// ever wanted, the way in is a level of indirection — a token whose replacement
/// is itself observable — not mutating another fiber's environment.
///
/// The explicit form does no lookup at all, and is the only way to watch a token
/// that is not the ambient one.
///
/// Either way the test is once per iteration: a single ten-second iteration
/// still takes ten seconds.
and private expandUntilCancelled (clauseForms: SExpr list) : SExpr list =
    let at r t = SAtom { Token = t; Range = r }

    // Every zero-arity clause's binding, in the order the clauses were written.
    let mutable entryBindings = []

    let rewrite (s: SExpr) =
        match s with
        | SList(SAtom { Token = Keyword "until-cancelled" } :: rest, r) ->
            let token =
                match rest with
                | [] ->
                    let name = Gensym.fresh "untilcancel"

                    entryBindings <-
                        entryBindings
                        @ [ SList(
                                [ at r (Keyword "with")
                                  at r (Symbol name)
                                  SList([ at r (Symbol "parameter-ref"); at r (Symbol "current-cancel") ], r) ],
                                r
                            ) ]

                    at r (Symbol name)
                | [ token ] -> token
                | _ ->
                    failwithf
                        $"Invalid (:until-cancelled ...) at %s{Lexer.formatPos r}. Expected: (:until-cancelled) for the ambient token, or (:until-cancelled token) for a named one."

            SList([ at r (Keyword "break"); SList([ at r (Symbol "cancelled?"); token ], r) ], r)

        | other -> other

    let rewritten = clauseForms |> List.map rewrite
    entryBindings @ rewritten

/// Desugars `(loop clause... [=> expr])`.
///
/// A loop is a left fold with early exit that always delivers a result: every
/// exit runs the same finish block. That is why the accumulators are hoisted —
/// they hold state across the whole loop and have to be visible at the end.
and desugarLoop (fns: ParseFns) (allForms: SExpr list) (r: Range) : Expr =
    // An optional name comes first, before any clause.
    let userLoopName, forms =
        match allForms with
        | SAtom { Token = Symbol n } :: rest -> Some n, rest
        | _ -> None, allForms

    // `=> expr`, if present, is the last two forms.
    let clauseForms, finishForm =
        let isArrow =
            function
            | SAtom { Token = Symbol "=>" } -> true
            | _ -> false

        match forms |> List.tryFindIndex isArrow with
        | None -> forms, None
        | Some i when i = forms.Length - 2 -> forms |> List.take i, Some(List.last forms)
        | Some i ->
            let ar = getRange forms[i]
            failwithf $"'=>' at %s{Lexer.formatPos ar} must be followed by exactly one result expression, at the end of the loop."

    if clauseForms.IsEmpty then
        failwithf $"Invalid loop at %s{Lexer.formatPos r}: it has no clauses"

    let clauseForms = expandUntilCancelled clauseForms

    let clauses = clauseForms |> List.map parseLoopClause

    match clauses with
    | (LFor _ | LWith _) :: _ -> ()
    | c :: _ ->
        let cr = loopClauseRange c
        failwithf
            $"A loop must begin with a (:for ...) or (:with ...) at %s{Lexer.formatPos cr}: every other clause belongs to the level open at its position, and before the first one there is none."
    | [] -> ()

    // Level assignment, in one left-to-right pass. An *iterating* clause — a
    // `:for` or a `:with` — preceded by anything other than another iterating
    // clause opens a new level; every other clause belongs to the level that was
    // current at its own position, so an `:acc` above an inner `:for`, or a
    // `:let` between a `:subloop` and the `:for` it opens, stays in the
    // enclosing level.
    //
    // A `:with` counts here for the same reason it is tested here: it advances
    // with the level, so it is in lockstep with the level's cursors rather than
    // an interruption between two of them. `(:subloop)` is still the only way to
    // separate two iterating clauses.
    let levelOf =
        let mutable current = -1
        let mutable prevWasIter = false

        clauses
        |> List.map (fun c ->
            match c with
            | LFor _
            | LWith _ ->
                if not prevWasIter then current <- current + 1
                prevWasIter <- true
                current
            | _ ->
                prevWasIter <- false
                current)

    let maxLevel = List.max levelOf

    let call (name: string) (args: Expr list) (cr: Range) = EApp(EResolved(name, cr), args, cr)

    /// The names a `:for` or `:let` pattern binds.
    let patternNames (pat: SExpr) =
        match pat with
        | SAtom { Token = Symbol n } -> [ n ]
        | SList(SAtom { Token = Symbol "Tuple" } :: parts, _) ->
            parts
            |> List.choose (function
                | SAtom { Token = Symbol n } -> Some n
                | _ -> None)
        | _ -> []

    /// The names a `:when-let` or `:break-let` pattern binds.
    ///
    /// Read through the pattern parser rather than off the s-expression:
    /// these patterns destructure constructors, and `patternNames` above only
    /// knows the two shapes an irrefutable clause is allowed to use.
    let refutableNames (pat: SExpr) = fns.Pattern pat |> patternBinders

    /// The slot a `:with` carries its value in.
    ///
    /// A plain identifier names its own slot. That is not only an economy: a
    /// `:with`'s `end` is tested at the very top of the iteration, before
    /// anything has been bound, so the variable has to *be* a parameter of the
    /// member for `end` to name it. A tuple pattern has no single name to give,
    /// so it gets a gensym and is destructured from it.
    let withSlotName (pat: SExpr) =
        match pat with
        | SAtom { Token = Symbol n } -> n
        | _ -> Gensym.fresh "loopwith"

    // One member per level, plus the names each of them needs.
    let levels =
        [ for i in 0..maxLevel ->
            let mine = List.zip levelOf clauses |> List.filter (fst >> (=) i) |> List.map snd

            let fors =
                mine
                |> List.choose (function
                    | LFor(p, sq, cr) -> Some(p, sq, cr)
                    | _ -> None)

            let withs =
                mine
                |> List.choose (function
                    | LWith(p, st, up, en, cr) -> Some(p, st, up, en, cr)
                    | _ -> None)

            // The level's iterating clauses in source order, as indices into
            // `fors` and `withs`. Termination tests are built from this rather
            // than from the two lists in turn: `done?` may be effectful, so
            // which test runs before which is observable and has to be what the
            // author wrote.
            let iterOrder =
                let mutable fi = -1
                let mutable wi = -1

                mine
                |> List.choose (function
                    | LFor _ ->
                        fi <- fi + 1
                        Some(Choice1Of2 fi)
                    | LWith _ ->
                        wi <- wi + 1
                        Some(Choice2Of2 wi)
                    | _ -> None)

            // `:subloop` emits nothing. Its only role is to have not been an
            // iterating clause, which the pass above has already taken account
            // of.
            let others =
                mine
                |> List.filter (function
                    | LFor _
                    | LWith _
                    | LSubloop _ -> false
                    | _ -> true)

            // A `:with` contributes nothing here: its value travels as a slot of
            // every level from its own inward, so an inner level reads it as a
            // parameter rather than being handed a copy under another name.
            let bound =
                mine
                |> List.collect (function
                    | LFor(p, _, _) -> patternNames p
                    | LLet(p, _, _) -> patternNames p
                    | LRefutableLet(p, _, _, _) -> refutableNames p
                    | _ -> [])

            {| Index = i
               Fors = fors
               Withs = withs
               IterOrder = iterOrder
               Others = others
               Bound = bound
               SeqNames = fors |> List.map (fun _ -> Gensym.fresh "loopseq")
               CurNames = fors |> List.map (fun _ -> Gensym.fresh "loopcur")
               WithNames = withs |> List.map (fun (p, _, _, _, _) -> withSlotName p)
               Member = Gensym.fresh "looplevel" |} ]

    // One slot per accumulator, in declaration order across *every* level: an
    // accumulator is hoisted, lives on all members, and is visible in the finish
    // block. `:final` contributes one of its own: a `folding` seeded with false,
    // whose step is the test.
    let accInfo =
        List.zip levelOf clauses
        |> List.choose (fun (level, clause) ->
            match clause with
            | LAcc(name, collector, modifier, cr) ->
                let collectorExpr, stepForm = splitCollector fns collector

                Some
                    { Collector = Gensym.fresh "loopcol"
                      Name = name
                      CollectorExpr = collectorExpr
                      StepForm = stepForm
                      Modifier = modifier
                      Hidden = false
                      Level = level
                      Range = cr }

            | LFinal(cond, cr) ->
                Some
                    { Collector = Gensym.fresh "loopcol"
                      // A gensym, so a user accumulator that happens to be
                      // called `tmp` cannot be captured by it.
                      Name = Gensym.fresh "loopfinal"
                      CollectorExpr = call "folding" [ EBool(false, cr) ] cr
                      StepForm = cond
                      Modifier = None
                      Hidden = true
                      Level = level
                      Range = cr }

            | _ -> None)

    let accNames = accInfo |> List.map (fun slot -> slot.Name)

    /// Every name a `:with` clause binds, at any level.
    let withVarNames =
        levels
        |> List.collect (fun lvl -> lvl.Withs |> List.collect (fun (p, _, _, _, _) -> patternNames p))

    /// The `:with` variables a named loop may override — the plain-identifier
    /// ones, whose slot *is* the variable. A tuple pattern has no single name to
    /// put after `#:`, and offering one of its parts would override a part of a
    /// slot that is written whole.
    let overridableWithNames =
        levels
        |> List.collect (fun lvl -> lvl.Withs)
        |> List.choose (fun (p, _, _, _, _) ->
            match p with
            | SAtom { Token = Symbol n } -> Some n
            | _ -> None)

    /// The slot vector of level `i`, in emission order.
    ///
    /// Every enclosing level's sequences and cursors are carried, because an
    /// inner level has to be able to jump *back* to its parent with the parent's
    /// cursors advanced — and every enclosing level's bindings too, because an
    /// inner sequence or clause may name them and a member is a separate
    /// function with no lexical view of its caller. Accumulators are on every
    /// member: they are hoisted, and the finish block reads them wherever it is
    /// reached from.
    ///
    /// Level 0's sequences are absent by design: they are loop-invariant, so
    /// they sit in the prologue and are lexically in scope for the whole group.
    /// A `:with` is carried exactly like a cursor, and from its own level
    /// inward: an inner clause may name it, and the jump back out has to hand it
    /// over unchanged. Unlike an accumulator it is *not* on every member — it
    /// does not exist above the level that owns it, which is the same reason it
    /// is out of scope in the finish block.
    let slotNames (i: int) : string list =
        [ for j in 0..i do
              if j > 0 then yield! levels[j].SeqNames
              yield! levels[j].CurNames
              yield! levels[j].WithNames
          for j in 0 .. i - 1 do
              yield! levels[j].Bound
          yield! accNames ]

    /// A jump to level `target`, filling every slot: with `overrides` where one
    /// is given, and with whatever is in scope under that name otherwise.
    ///
    /// A `TRecur` carries one argument per slot, so a partial update has to be
    /// completed here rather than left to the emitter.
    let jump (target: int) (overrides: Map<string, Expr>) (cr: Range) =
        let args =
            slotNames target
            |> List.map (fun n ->
                match Map.tryFind n overrides with
                | Some e -> e
                | None -> EIdent(n, cr))

        EApp(EIdent(levels[target].Member, cr), args, cr)

    /// Level `i` one step on: its cursors advanced and its `:with` slots
    /// updated.
    ///
    /// Both go into the *same* override map, which `jump` turns into one
    /// complete argument vector. That is what makes a level's updates
    /// simultaneous: every one of them is computed from this iteration's values
    /// before any slot is written, so `(:with a 0 b) (:with b 1 (+ a b))` is
    /// fibonacci rather than a sequence of assignments. An author who wants the
    /// sequential reading names the new value with a `:let` first.
    let advanced (i: int) (cr: Range) =
        let cursors =
            List.map2
                (fun sn cn -> cn, call "iterable-next" [ EIdent(sn, cr); EIdent(cn, cr) ] cr)
                levels[i].SeqNames
                levels[i].CurNames

        // No update is a loop-invariant `:with`: contributing no override leaves
        // the slot holding what it held, and emits nothing at all rather than a
        // self-assignment.
        let withs =
            List.zip levels[i].WithNames levels[i].Withs
            |> List.choose (fun (slot, (_, _, update, _, _)) ->
                update |> Option.map (fun u -> slot, fns.Expr u))

        cursors @ withs |> Map.ofList

    /// The next iteration of level `i`: its own cursors advanced, everything
    /// else as it stands.
    let advanceLevelWith (i: int) (extra: Map<string, Expr>) (cr: Range) =
        let overrides = Map.fold (fun acc k v -> Map.add k v acc) (advanced i cr) extra
        jump i overrides cr

    let advanceLevel (i: int) (cr: Range) = advanceLevelWith i Map.empty cr

    // The level's termination tests, in clause order, short-circuiting: a
    // `:for`'s `done?` and a `:with`'s `end` interleaved exactly as written.
    // When one holds the level is over and no later test runs.
    //
    // The order is not a detail. `done?` may be effectful — an enumerator-backed
    // cursor advances in it — so a `:with` whose `end` holds must leave a later
    // `:for`'s cursor un-advanced, and that only follows if the tests are built
    // from the source order rather than from the two lists in turn.
    //
    // A `:with` with no `end` contributes nothing, so the common case emits no
    // branch instead of a folded constant. A level of nothing but such `:with`
    // clauses yields `false` and never ends on its own — the same as a `:for`
    // over an infinite sequence, and equally the author's business.
    let exhausted (i: int) =
        let tests =
            levels[i].IterOrder
            |> List.choose (function
                | Choice1Of2 fi ->
                    Some(call "iterable-done?" [ EIdent(levels[i].SeqNames[fi], r); EIdent(levels[i].CurNames[fi], r) ] r)
                | Choice2Of2 wi ->
                    let (_, _, _, endCond, _) = levels[i].Withs[wi]
                    endCond |> Option.map fns.Expr)

        let rec anyOf ts =
            match ts with
            | [ last ] -> last
            | t :: tl -> EIf(t, EBool(true, r), anyOf tl, r)
            | [] -> EBool(false, r)

        anyOf tests

    // Every exit runs this. Each accumulator is rebound to its *finished* value,
    // shadowing the slot, so `=> expr` sees the finished one by name.
    //
    // It is a member of the group rather than something spliced at each exit:
    // exhaustion, every `:break`, every `:final` and a named loop's declining
    // `:do` all reach it, and inlining it at each one would emit as many copies
    // of the `=>` expression as there are ways out.
    let exitName = Gensym.fresh "loopexit"

    /// Refuses a `:with` variable named in the finish block.
    ///
    /// The finish block is reached from *every* exit, including one taken from a
    /// level where an inner `:with` does not exist — so "sometimes in scope"
    /// would be the only honest alternative to "never". Accumulators are hoisted
    /// and so have no such problem, which is why they are the way to carry a
    /// value out.
    ///
    /// Scope-aware, because a finish block is an ordinary expression and may
    /// perfectly well bind a name of its own that happens to collide — which is
    /// why this is `freeNamesWith` rather than a search for the spelling. It
    /// used to be a walker of its own, and that walker had already drifted: its
    /// pattern case did not see a `(:is T e)` binder, so a finish block that
    /// rebound the name that way was refused for shadowing it.
    let rejectWithInFinish (e: Expr) : unit =
        let names = Set.ofList withVarNames

        freeNamesWith
            (fun n ir _ ->
                if Set.contains n names then
                    failwithf
                        $"'%s{n}' at %s{Lexer.formatPos ir} is a (:with ...) variable, and a loop variable is not in scope after the loop: the finish block is reached from every exit, and an inner level's variables do not exist at an exit taken from an outer one. Carry it out with an accumulator — (:acc last (folding 0 %s{n})) — and name that in the '=>' instead.")
            false
            Set.empty
            e

    let finishBlockBody =
        // `:final`'s accumulator is not the author's and has no business in the
        // result, so only the declared ones are delivered or even finished.
        let declared = accInfo |> List.filter (fun slot -> not slot.Hidden)

        let result =
            match finishForm with
            | Some e ->
                let parsed = fns.Expr e
                rejectWithInFinish parsed
                parsed
            | None ->
                match declared with
                // Nothing to deliver: a loop with no accumulators and no `=>`
                // is pure effect, and types as `void` rather than as unit so
                // that it can be the body of a `void` function. `when` is the
                // language's only void-typed expression form, and with a
                // constant-false condition it is also the emptiest one.
                | [] -> EWhen(EBool(false, r), ETuple([], r), false, r)
                | [ slot ] -> EIdent(slot.Name, slot.Range)
                | _ -> ETuple(declared |> List.map (fun slot -> EIdent(slot.Name, slot.Range)), r)

        List.foldBack
            (fun slot acc ->
                ELet(
                    slot.Name,
                    false,
                    [],
                    None,
                    call "collector-finish" [ EIdent(slot.Collector, slot.Range); EIdent(slot.Name, slot.Range) ] slot.Range,
                    acc,
                    slot.Range
                ))
            declared
            result

    /// Leaving the loop: hand the accumulators as they stand to the finish
    /// member. They are in scope under their own names at every exit, whether as
    /// a slot or as a rebinding an `:acc` clause made earlier this iteration.
    let finishBlock (cr: Range) =
        EApp(EIdent(exitName, cr), accNames |> List.map (fun n -> EIdent(n, cr)), cr)

    /// Steps one accumulator, then carries on.
    let stepAcc (slot: AccSlot) (rest: Expr) =
        let cr = slot.Range

        let stepped =
            call "collector-step" [ EIdent(slot.Collector, cr); EIdent(slot.Name, cr); fns.Expr slot.StepForm ] cr

        let value =
            match slot.Modifier with
            | None -> stepped
            | Some cond -> EIf(fns.Expr cond, stepped, EIdent(slot.Name, cr), cr)

        ELet(slot.Name, false, [], None, value, rest, cr)

    /// Entering level `i` from its parent: its sequences are evaluated *here*,
    /// because an inner sequence usually names an outer loop variable and so is
    /// not loop-invariant; its cursors are freshly started from them.
    ///
    /// The sequences are bound to temporaries first — the jump needs their
    /// values, and `start` needs them too, so evaluating the expression twice
    /// would be both wrong and slow.
    /// Its `:with` clauses take their `start` here too, for the same reason and
    /// on the same edge as a cursor's: `start` is per *entry to the level that
    /// owns the clause*, so a `:with` inside a subloop is reset on every entry
    /// to that subloop. This is the opposite of an accumulator, which is hoisted
    /// and persists across the outer iterations.
    let enterLevel (i: int) (cr: Range) =
        let temps = levels[i].Fors |> List.map (fun _ -> Gensym.fresh "loopenter")

        let overrides =
            (List.map2 (fun sn t -> sn, EIdent(t, cr)) levels[i].SeqNames temps)
            @ (List.map2 (fun cn t -> cn, call "iterable-start" [ EIdent(t, cr) ] cr) levels[i].CurNames temps)
            @ (List.zip levels[i].WithNames levels[i].Withs
               |> List.map (fun (slot, (_, start, _, _, _)) -> slot, fns.Expr start))
            |> Map.ofList

        List.foldBack
            (fun (t, (_, sequence, fr)) acc -> ELetMono(t, fns.Expr sequence, acc, fr))
            (List.zip temps levels[i].Fors)
            (jump i overrides cr)

    /// Leaving level `i`: level 0 is the end of the loop, and any other level
    /// hands back to its parent with the parent's cursors advanced — the same
    /// edge an `:end-subloop-if` takes.
    let exitLevel (i: int) (cr: Range) =
        if i = 0 then finishBlock cr else advanceLevel (i - 1) cr

    // The clauses of one level, in order. The last of them falls into the next
    // level if there is one, and otherwise into the next iteration of this one —
    // unless a named loop's final `:do` has taken that edge over.
    let rec buildClauses (level: int) (cs: LoopClause list) (accsLeft: AccSlot list) =
        let continueEdgeOf (cr: Range) =
            if level < maxLevel then enterLevel (level + 1) cr else advanceLevel level cr

        match cs with
        | [] -> continueEdgeOf r

        | LLet(pat, value, cr) :: tl ->
            bindLoopPattern pat (fns.Expr value) (buildClauses level tl accsLeft) cr

        // The `:let` whose pattern may fail. `bindLoopPattern` refuses one of
        // these because it has nowhere to send the failure; the second arm is
        // that somewhere.
        //
        // Clauses above it have already run, so an accumulator stepped before
        // this one keeps what it was given — the same as `:when` and `:break`.
        | LRefutableLet(pat, value, stops, cr) :: tl ->
            let missed = if stops then finishBlock cr else advanceLevel level cr

            EMatch(
                fns.Expr value,
                [ fns.Pattern pat, None, buildClauses level tl accsLeft
                  PWildcard cr, None, missed ],
                cr
            )

        // In a named loop the *final* `:do` owns the continue edge: if it tail
        // calls the loop, that is the jump, and if it completes without one the
        // loop leaves through the finish block like any other exit.
        | [ LDo(exprs, cr) ] when userLoopName.IsSome && level = maxLevel ->
            let name = userLoopName.Value
            let statements = exprs |> List.take (exprs.Length - 1)
            let final = List.last exprs

            List.foldBack
                (fun e acc ->
                    let parsed = fns.Expr e
                    rejectLoopName name parsed
                    ELet("_", false, [], None, parsed, acc, cr))
                statements
                (continueEdge level name (fns.Expr final) cr)

        | LDo(exprs, cr) :: tl ->
            List.foldBack
                (fun e acc -> ELet("_", false, [], None, fns.Expr e, acc, cr))
                exprs
                (buildClauses level tl accsLeft)

        // Skips the rest of *this* iteration of *this* level. Clauses above it
        // have already run, so an accumulator stepped before it keeps what it
        // was given.
        | LWhen(cond, cr) :: tl ->
            EIf(fns.Expr cond, buildClauses level tl accsLeft, advanceLevel level cr, cr)

        // Abandons this level and resumes the enclosing one — an early return
        // from a subloop, not an iteration skip. At level 0 the two would
        // coincide, which is a coincidence rather than a definition.
        | LEndSubloop(cond, cr) :: tl ->
            if level = 0 then
                failwithf
                    $"(:end-subloop-if ...) at %s{Lexer.formatPos cr} is at the outermost level, where there is no enclosing loop to resume. Use (:when ...) to skip an iteration, or (:break ...) to leave the loop."

            EIf(fns.Expr cond, exitLevel level cr, buildClauses level tl accsLeft, cr)

        // Leaves the whole loop from any level, through the finish block.
        // Accumulators stepped earlier in this iteration keep what they were
        // given.
        | LBreak(cond, cr) :: tl ->
            EIf(fns.Expr cond, finishBlock cr, buildClauses level tl accsLeft, cr)

        // `:break` on the hidden accumulator, then the accumulator's own step —
        // in that order. The slot still holds the previous iteration's verdict
        // when the break reads it, which is what makes this "after the current
        // iteration" rather than "before the rest of it".
        | LFinal _ :: tl ->
            match accsLeft with
            | slot :: restAcc ->
                EIf(
                    EIdent(slot.Name, slot.Range),
                    finishBlock slot.Range,
                    stepAcc slot (buildClauses level tl restAcc),
                    slot.Range
                )
            | [] -> failwith "internal error: :final without its accumulator"

        | LAcc _ :: tl ->
            match accsLeft with
            | slot :: restAcc -> stepAcc slot (buildClauses level tl restAcc)
            | [] -> failwith "internal error: accumulator clause without its info"

        | (LFor _ | LWith _ | LSubloop _) :: _ -> failwith "internal error: clause should have been rejected"

    /// Rewrites the tail positions of a named loop's final `:do`.
    ///
    /// Every tail position either *is* a call to the loop — which becomes the
    /// jump, keeping it a tail call so it can be one — or is not, in which case
    /// it runs for its effect and the loop leaves through the finish block.
    and continueEdge (level: int) (name: string) (e: Expr) (cr: Range) : Expr =
        match e with
        | EApp(EIdent(n, ir), args, ar) when n = name ->
            for a in args do
                rejectLoopName name a

            advanceLevelWith level (parseOverrides name args ir) ar

        | EIf(cond, t, f, ir) ->
            rejectLoopName name cond
            EIf(cond, continueEdge level name t cr, continueEdge level name f cr, ir)

        | ELet(n, isFun, args, ann, value, body, ir) ->
            rejectLoopName name value
            ELet(n, isFun, args, ann, value, continueEdge level name body cr, ir)

        | ELetTuple(names, value, body, ir) ->
            rejectLoopName name value
            ELetTuple(names, value, continueEdge level name body cr, ir)

        // Anything else completes, and then the loop is over.
        | other ->
            rejectLoopName name other
            ELet("_", false, [], None, other, finishBlock cr, cr)

    /// `(lp #:name expr ...)` — the slots the call overrides.
    and parseOverrides (name: string) (args: Expr list) (cr: Range) : Map<string, Expr> =
        let rec go acc rest =
            match rest with
            | [] -> acc
            | EKeyword(k, kr) :: value :: tl ->
                if not (List.contains k accNames || List.contains k overridableWithNames) then
                    let known =
                        (accNames |> List.filter (fun n -> not (n.StartsWith "loopfinal")))
                        @ overridableWithNames
                        |> String.concat ", "

                    let known = if known = "" then "(none)" else known

                    failwithf
                        $"'%s{name}' at %s{Lexer.formatPos kr} has no slot called '#:%s{k}'. A named loop can override the slots it carries — %s{known} — but not a (:for ...) variable, which is derived from its cursor rather than carried. A variable you want to jump ahead is a (:with ...), not a (:for ...)."

                go (Map.add k value acc) tl
            | EKeyword(k, kr) :: [] -> failwithf $"'#:%s{k}' at %s{Lexer.formatPos kr} has no value"
            | other :: _ ->
                let orr = exprRange other
                failwithf
                    $"'%s{name}' at %s{Lexer.formatPos orr} takes only keyword arguments: write ('%s{name}') to advance everything, or ('%s{name}' #:acc expr) to override one accumulator."

        go Map.empty args

    /// The loop name is a jump target, not a value: it has no lowering anywhere
    /// but tail position, so anything else is refused where it is written.
    and rejectLoopName (name: string) (e: Expr) : unit =
        let rec go (x: Expr) =
            match x with
            | EIdent(n, ir) when n = name ->
                failwithf
                    $"'%s{name}' at %s{Lexer.formatPos ir} is a loop name, which may only be tail called from the loop's last (:do ...). It is a jump, so it cannot be used as a value or called from anywhere else."
            | _ -> exprChildren x |> List.iter go

        go e

    let bindCurrents (i: int) (inner: Expr) =
        List.foldBack
            (fun ((pat, _, cr), (sn, cn)) acc ->
                bindLoopPattern pat (call "iterable-current" [ EIdent(sn, cr); EIdent(cn, cr) ] cr) acc cr)
            (List.zip levels[i].Fors (List.zip levels[i].SeqNames levels[i].CurNames))
            inner

    /// Destructures the tuple-pattern `:with` slots of every level up to `i`.
    ///
    /// A plain identifier needs nothing — it names its own slot, so it is
    /// already a parameter. Only a tuple pattern has a gensym slot to unpack,
    /// and it is unpacked at *every* level that carries it rather than once at
    /// the owning one, so an inner level reads the same slot it was handed
    /// instead of a copy passed down under the part names.
    ///
    /// This wraps the whole member body, ahead of the termination test, because
    /// a `:with`'s `end` is tested before anything else runs and names its own
    /// variable. It deliberately does not reach the `:for` elements: those come
    /// from `current`, which `bindCurrents` binds only once the test has passed.
    let bindWiths (i: int) (inner: Expr) =
        let tuplePatterned =
            [ for j in 0..i do
                  yield!
                      List.zip levels[j].WithNames levels[j].Withs
                      |> List.filter (fun (_, (p, _, _, _, _)) ->
                          match p with
                          | SAtom { Token = Symbol _ } -> false
                          | _ -> true) ]

        List.foldBack
            (fun (slot, (pat, _, _, _, wr)) acc -> bindLoopPattern pat (EIdent(slot, wr)) acc wr)
            tuplePatterned
            inner

    // One member per level. Every level transition is a tail call by
    // construction, and they are all in one group rather than nested: a jump
    // across levels has to reach the *same* switch, and a nested group would
    // bind it to the wrong one.
    let members =
        levels
        |> List.map (fun lvl ->
            let accsHere = accInfo |> List.filter (fun slot -> slot.Level = lvl.Index)

            let body =
                bindWiths
                    lvl.Index
                    (EIf(
                        exhausted lvl.Index,
                        exitLevel lvl.Index r,
                        bindCurrents lvl.Index (buildClauses lvl.Index lvl.Others accsHere),
                        r
                    ))

            (lvl.Member, true, slotNames lvl.Index |> List.map (fun n -> MandatoryArg(n, None)), None, body))

    // The finish member. It calls nothing, so `LetRecify` gives it a component
    // of its own and it is bound ahead of the loop group rather than becoming a
    // case in the same switch — which costs one call on the way out and saves a
    // copy of the block at every other exit.
    let members =
        members
        @ [ (exitName, true, accNames |> List.map (fun n -> MandatoryArg(n, None)), None, finishBlockBody) ]

    // In `slotNames 0`'s order: level 0's cursors, then its `:with` slots, then
    // the accumulators.
    let initialArgs =
        (levels[0].SeqNames
         |> List.map (fun sn -> call "iterable-start" [ EIdent(sn, r) ] r))
        @ (levels[0].Withs |> List.map (fun (_, start, _, _, _) -> fns.Expr start))
        @ (accInfo |> List.map (fun slot -> call "collector-init" [ EIdent(slot.Collector, slot.Range) ] slot.Range))

    let group =
        ELetRec(members, EApp(EIdent(levels[0].Member, r), initialArgs, r), r)

    // The prologue. Everything loop-invariant is evaluated once, outside: the
    // collectors, and level 0's sequences. An inner level's sequence usually
    // names an outer loop variable, so it is evaluated at the entering jump
    // instead — hoisting is per clause, not unconditional.
    //
    // `let/mono` rather than `let` because a collector is typically a bare
    // nullary constructor, which `let` would generalize — and then its element
    // type would never pin down.
    let withCollectors =
        List.foldBack
            (fun slot acc -> ELetMono(slot.Collector, slot.CollectorExpr, acc, slot.Range))
            accInfo
            group

    List.foldBack
        (fun (sn, (_, sequence, cr)) acc -> ELetMono(sn, fns.Expr sequence, acc, cr))
        (List.zip levels[0].SeqNames levels[0].Fors)
        withCollectors

