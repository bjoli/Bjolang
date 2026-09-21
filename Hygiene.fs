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

module Bjolang.Hygiene

open Lexer
open Bjolang.Ast

/// Set by `Pipeline` once the macro table is populated.
///
/// A mutable hook rather than a threaded context, for the same reason `Gensym`
/// keeps one counter: `parseExpr` is a pure `SExpr -> Expr` called from several
/// hundred places and from `Pipeline.inlineImplDecl`, which parses
/// already-expanded metadata bodies and must keep working with no macros
/// registered at all.
let mutable expandHook: SExpr -> Expansion option = fun _ -> None

/// The same, for the table `def/pattern` fills.
///
/// Separate from `expandHook` because the tables are separate: a name may be a
/// function in expression position and a pattern macro in pattern position, the
/// way `Cons` is already both. Asked for a list headed by a symbol and for a
/// bare capitalized symbol, which are the two shapes a pattern macro is called
/// in.
let mutable patternExpandHook: SExpr -> Expansion option = fun _ -> None

/// The same, for the table `def/hash-extend` fills.
///
/// Asked for a list whose head is a `#name` symbol — the reader's rendering of
/// `#name(...)` — and before the special forms, since no special form is
/// spelled with a `#`. Expression position only: a pattern refuses one, and a
/// quoted list refuses one.
let mutable hashExpandHook: SExpr -> Expansion option = fun _ -> None

/// Whether a head symbol names a macro, without running the transformer.
///
/// `parseBody` needs to know before it decides how to read a form, because a
/// macro may expand to a `def` — and running the transformer twice to find out
/// would run user code twice.
let mutable isMacroName: string -> bool = fun _ -> false

/// Names the expander has introduced.
///
/// A macro-introduced identifier is renamed apart from the call site, so a
/// template's `let` arrives as `let__37` and head-symbol dispatch has to see
/// through the mark. Only for names the expander actually made: `x__1` is a
/// name a program may legitimately define, and stripping it would be wrong.
///
/// Never pruned. Every entry is unique by construction, so it can only ever
/// answer for the expansion that created it.
let mutable private introducedNames: Set<string> = Set.empty

let noteIntroduced (names: string seq) =
    introducedNames <- names |> Seq.fold (fun acc n -> Set.add n acc) introducedNames

/// The set, and a way to put it back.
///
/// "Never pruned" above is true within one compilation and is what makes the
/// set safe. Across compilations in one process it is only growth: every entry
/// answers for an expansion that has already been parsed, so `Session` drops
/// them at the boundary rather than carrying a REPL session's worth of marks
/// into every subsequent `headName`.
let snapshotIntroduced () : Set<string> = introducedNames

let restoreIntroduced (names: Set<string>) : unit = introducedNames <- names

/// The name to dispatch a head symbol on: the third renaming rule, "strip the
/// rename and dispatch as a special form".
///
/// Three places dispatch on a head: the special-form chain in `parseExpr`, the
/// operator table beside it, and a pattern's constructor in `parsePattern`.
///
/// Only the dispatch uses this. The identifier itself keeps its mark, so a
/// template's reference to a binding of its own module is still resolvable by
/// rule two.
let headName (sym: string) =
    if Set.contains sym introducedNames then Gensym.baseName sym else sym

/// The third renaming rule, at a head that is dispatched on rather than bound.
/// Unconditional, unlike in `parseExpr`: a declaration's head is either one of
/// the declaration forms or the name of a macro, and neither is a binding a
/// mark could be resolving. A macro that expands to a `defun` — or to a
/// `(: name type)` beside it — arrives with both renamed like anything else a
/// template constructs.
///
/// Used wherever a form's head selects a form rather than naming a value:
/// `tryParseDecl`, `parseDeclForms`, `flattenBegins`, a type definition's
/// `Record`/`Union` tag, and the clauses of a `def/trait` or `impl`.
let stripHeadMark (s: SExpr) : SExpr =
    match s with
    | SList(SAtom({ Token = Symbol sym } as head) :: rest, lr) when sym <> headName sym ->
        SList(SAtom { head with Token = Symbol(headName sym) } :: rest, lr)
    | _ -> s

/// A symbol whose mark has been stripped, for the positions that name something
/// dispatched on rather than bound — a trait in an `impl`, say, which has to
/// match what the `def/trait` declared.
let (|StrippedSymbol|_|) (s: SExpr) =
    match s with
    | SAtom { Token = Symbol sym } -> Some(headName sym)
    | _ -> None

/// The same, at the name of a method inside a `def/trait` or `impl` body.
///
/// `(defun (= a b) ...)` in a template has `=` renamed like everything else it
/// constructs, and the completeness check compares it against the trait's
/// signature. The *parameters* are left alone: those are binders, and hygiene
/// is exactly what should apply to them.
let stripMethodName (s: SExpr) : SExpr =
    match s with
    | SList((SAtom { Token = Symbol("defun" | "defbjo") } as definer)
            :: SList(SAtom({ Token = Symbol name } as nameAtom) :: args, hr)
            :: rest,
            lr) when name <> headName name ->
        SList(
            definer
            :: SList(SAtom { nameAtom with Token = Symbol(headName name) } :: args, hr)
            :: rest,
            lr
        )
    | _ -> s

// --- Parser ---

/// The third renaming rule, applied to a type name.
///
/// A type is never a *binding*, so — exactly as for a pattern's constructor —
/// neither of the first two rules can reach one: nothing an expansion binds is
/// a type, and `AlphaRename.freeNames` walks expressions, which an `FType` is
/// no part of. A mark left here is therefore never resolved anywhere else.
///
/// Without this a template that writes `(: ,name (-> int))` — which is how a
/// macro declares the type of a function it defines — reaches inference as
/// `(->__37 int__38)`: an unknown constructor applied to an unknown type. A
/// type *variable* needs nothing, being read from a `QuotedSymbol`, which
/// hygiene does not rename.
///
/// Both readers of a type call it, because there are two: `parseType` and the
/// `parseArrowTypeInner` inside `parseArrowType`.
let stripTypeMark (s: SExpr) : SExpr =
    match s with
    | SAtom({ Token = Symbol sym } as atom) when headName sym <> sym ->
        SAtom { atom with Token = Symbol(headName sym) }
    | SList(SAtom({ Token = Symbol sym } as head) :: args, lr) when headName sym <> sym ->
        SList(SAtom { head with Token = Symbol(headName sym) } :: args, lr)
    | _ -> s

/// What stands between an escape's application and the block it would leave.
type private EscapeBarrier =
    | BarrierLambda
    | BarrierSeq
    | BarrierSpawn
    | BarrierFinally

/// Where `(name ...)` may be applied inside a `with-return` body.
///
/// Every rule about `ret` is a question about *shape* — is this application in
/// tail position, does a lambda stand between it and its block — so all of them
/// are answered here, over the body as written, rather than during inference,
/// where a type would have to be invented for a form that never yields one.
///
/// Shadowing stops the walk: a binding of the same name means the escape is
/// simply not what `name` names any more, which is the rule every other binding
/// already follows.
let checkEscapeUses (name: string) (body: Expr) : unit =
    let refuse (r: Range) (message: string) =
        failwithf $"Syntax error at %s{Lexer.formatPos r}: %s{message}"

    let crossed (barrier: EscapeBarrier) (r: Range) =
        match barrier with
        | BarrierLambda ->
            refuse r $"`%s{name}` cannot leave the lambda here. Use a loop, or a function returning a Result."
        | BarrierSeq ->
            refuse r $"`%s{name}` cannot leave a `seq` block: generated iterators may not be jumped out of."
        | BarrierSpawn ->
            refuse
                r
                $"`%s{name}` cannot leave the fiber here: a spawned call runs on its own, and the block it would return from may already have finished."
        | BarrierFinally -> refuse r $"`%s{name}` cannot leave a cleanup body."

    // `barrier` is the innermost function boundary crossed so far, and `tail`
    // says whether an escape applied *here* would stand in statement or tail
    // position. Everything else is a proper subexpression, which phase one
    // refuses: it would have to interact with `hoistToTemp` and would need a
    // bottom type to have one at all.
    let rec go (barrier: EscapeBarrier option) (tail: bool) (e: Expr) =
        // A body's statement form, which `parseBody` writes as a `_` binding.
        let stmt = go barrier true
        let sub = go barrier false
        let shadowed names = List.contains name names

        match e with
        | EIdent(n, r) when n = name ->
            refuse
                r
                $"`%s{name}` is an escape, not a value: it can only be applied inside its own `with-return`."

        | EApp(EIdent(n, _), args, r) when n = name ->
            match barrier with
            | Some b -> crossed b r
            | None -> ()

            if not tail then
                refuse r $"`%s{name}` may only appear as a statement or in tail position."

            List.iter sub args

        | EFun(args, b, _, _) -> if not (shadowed args) then go (Some BarrierLambda) true b
        | ESeq(b, _) -> go (Some BarrierSeq) true b
        | EBjo(b, _, _)
        | ETaskEvent(b, _) -> go (Some BarrierSpawn) true b

        // Leaving a `try` body is legal and correct — C# runs a `finally` on a
        // `goto` and on a `return` alike, so resources unwind. Leaving the
        // cleanup itself is what C# forbids.
        | ETryFinally(b, cleanup, _) ->
            stmt b
            go (Some BarrierFinally) true cleanup

        | ETryCatch(b, _, _) -> stmt b

        | ELet(n, isFun, args, _, value, b, _) ->
            // `(def x (ret 1))` is an initialiser, not a statement. Only the
            // `_` binding `parseBody` writes for a bare form in a body is one.
            if isFun then
                (if not (shadowed (allArgNames args)) then go (Some BarrierLambda) true value)
            elif n = "_" then
                stmt value
            else
                sub value

            if n <> name then go barrier tail b

        | ELetRec(bindings, b, _) ->
            let names = bindings |> List.map (fun (n, _, _, _, _) -> n)

            // A loop is not a function boundary, however it is spelled here.
            //
            // `loop`, `for` and a named `let` all desugar to a letrec whose
            // body immediately calls one of its own members, and that is
            // exactly what `LoopLowering.flatLoopEntry` recognises in order to
            // emit a `while` rather than a local function. A `goto` out of a
            // `while` is what the loop's own `ExitLabel` already does, so an
            // escape crossing one is leaving a loop and not a method.
            //
            // Recognised by shape rather than by the names the desugaring
            // generates: `(let go (...) ...)` is a loop too, and its member
            // carries a name the user chose.
            let isLoopEntry =
                match b with
                | EApp(EIdent(entry, _), _, _) -> List.contains entry names
                | _ -> false

            if not (shadowed names) then
                for (_, isFun, args, _, value) in bindings do
                    if isFun && not isLoopEntry then
                        (if not (shadowed (allArgNames args)) then go (Some BarrierLambda) true value)
                    elif isFun then
                        (if not (shadowed (allArgNames args)) then go barrier true value)
                    else
                        sub value

                go barrier tail b

        | ELetMono(n, value, b, _) ->
            sub value
            if n <> name then go barrier tail b

        | ELetTuple(names, value, b, _) ->
            sub value
            if not (shadowed names) then go barrier tail b

        | ELetMutable(n, _, value, b, _) ->
            sub value
            if n <> name then go barrier tail b

        | EIf(c, t, f, _) ->
            sub c
            go barrier tail t
            go barrier tail f

        // A `when` body's value is discarded, so it is a statement and an
        // escape is at home in it.
        | EWhen(c, b, _, _) ->
            sub c
            stmt b

        | EMatch(target, clauses, _) ->
            sub target

            for (pat, guard, clauseBody) in clauses do
                for step in patternSteps pat do
                    sub step

                Option.iter sub guard

                if not (shadowed (patternBinders pat)) then
                    go barrier tail clauseBody

        // A nested block binding the same name shadows this one, exactly as any
        // other binding of it would.
        | EWithReturn(n, b, _) -> if n <> name then go barrier tail b

        | EDefMatch(binder, scrutinee, failure, sequel, _) ->
            for step in patternSteps binder do
                sub step

            sub scrutinee

            // The binder's names reach the sequel only, and an arm's reach that
            // arm's body only — so each is walked under what binds over it, and
            // a binding of the same name there shadows this one.
            if not (shadowed (patternBinders binder)) then
                go barrier tail sequel

            (match failure with
             | FailNone
             | FailPropagate -> ()
             | FailValue value -> go barrier tail value
             | FailArms arms ->
                 for (armPattern, armBody) in arms do
                     for step in patternSteps armPattern do
                         sub step

                     if not (shadowed (patternBinders armPattern)) then
                         go barrier tail armBody)

        | _ -> List.iter sub (exprChildren e)

    go None true body

// ---------------------------------------------------------------------------
// Scope
// ---------------------------------------------------------------------------
//
// Free variables, capture-avoiding renaming, and simultaneous binding, over the
// untyped `Expr`.
//
// They live here rather than in `AlphaRename`, which is where the same three
// things over the *typed* AST live, because the parser is itself a caller:
// `(let ...)` binds simultaneously and the AST has no node for that, so
// desugaring one means freshening a shadowed binder on the spot. `AlphaRename`
// is compiled after `TypedAST`, which is compiled after this file, so a call
// from here to there cannot exist. It re-exports everything below under its own
// names, so no caller has to know which side of that line it is on.
//
// One walker, not several: `freeNamesWith` above is the only free-variable
// traversal in the compiler over this AST, and `renameWith` below the only
// renamer. A binder missed in either is a capture nothing downstream can
// detect — which is the mistake `AlphaRename`'s docstring records having made
// once already, in the typed pattern cases.

/// A name whose spelling is part of an interface someone else relies on.
///
///   * `::` marks a name the compiler synthesized to reach into another class —
///     `Foldable_List.Instance::fold`, `core_Module::helper`. It never names a
///     binder, and rewriting one would point it somewhere else entirely.
///   * `_` is the binder a body uses for a value nothing reads. Nothing can
///     reference it, so nothing can capture it either.
let isRenamable (name: string) : bool =
    not (name.Contains "::") && name <> "_"

/// Renames a pattern's binders, and its view steps with `renameStep`.
///
/// Two renamers because a view sits in two scopes at once: what the pattern
/// binds is the clause's, and the step is evaluated in the scope the `match`
/// sits in — where the names the clause binds do not exist yet.
let rec private renamePattern
    (renameStep: Expr -> Expr)
    (subst: Map<string, string>)
    (pat: Pattern)
    : Pattern =
    let go = renamePattern renameStep subst

    match pat with
    | PIdent(n, r) -> PIdent((Map.tryFind n subst |> Option.defaultValue n), r)
    | PList(items, tailOpt, r) -> PList(List.map go items, Option.map go tailOpt, r)
    | PVec(items, tailOpt, r) -> PVec(List.map go items, Option.map go tailOpt, r)
    | PArray(items, tailOpt, r) -> PArray(List.map go items, Option.map go tailOpt, r)
    | PTuple(items, r) -> PTuple(List.map go items, r)
    | PConstruct(n, args, r) -> PConstruct(n, List.map go args, r)
    | POr(alts, r) -> POr(List.map go alts, r)
    | PAnd(alts, r) -> PAnd(List.map go alts, r)
    | PView(step, inner, r) -> PView(renameStep step, go inner, r)
    | PTypeTest(t, binder, r) ->
        PTypeTest(t, binder |> Option.map (fun n -> Map.tryFind n subst |> Option.defaultValue n), r)
    | leaf -> leaf

/// Renames names in `expr`, given how to rename a binder and what the free
/// names start out substituted by.
///
/// `renameBinder` returning a name unchanged is what makes this usable for a
/// substitution that must *not* freshen: `bind` then drops the name from the
/// substitution instead of adding to it, so a binder shadows an outer name
/// exactly as it does at runtime.
///
/// A name in `resolved` becomes an `EResolved` where it is *called* rather than
/// an ordinary reference. Only this traversal knows which occurrences are free,
/// which is why the choice is made here and not in a pass of its own: a name the
/// expression binds for itself is not the one being resolved.
let private renameWith
    (resolved: Set<string>)
    (renameBinder: string -> string)
    (rootSubst: Map<string, string>)
    (expr: Expr)
    : Expr =

    /// Extends `subst` with a new name for each binder, returning the new names
    /// in the order given.
    let bind (names: string list) (subst: Map<string, string>) =
        let renamed = names |> List.map renameBinder

        let subst' =
            List.zip names renamed
            |> List.fold (fun acc (n, n') -> if n = n' then Map.remove n acc else Map.add n n' acc) subst

        renamed, subst'

    /// Binds a `defun` argument list, returning the rewritten list.
    ///
    /// A keyword parameter's name is left alone. It *is* the calling
    /// convention — `Codegen` emits it as a C# named argument at every call
    /// site — so renaming the parameter would rename only one end of it. It
    /// still shadows an outer name of the same spelling, which is what dropping
    /// it from the substitution does.
    let rec bindArgs (args: DefunArg list) (subst: Map<string, string>) =
        let renamable =
            args
            |> List.choose (function
                | MandatoryArg(n, _)
                | RestArg n -> Some n
                | KeywordArg _ -> None)

        let renamed, subst' = bind renamable subst

        let subst' =
            args
            |> List.fold
                (fun acc a ->
                    match a with
                    | KeywordArg(n, _) -> Map.remove n acc
                    | _ -> acc)
                subst'

        let newName = System.Collections.Generic.Queue renamed

        let args' =
            args
            |> List.map (function
                | MandatoryArg(_, t) -> MandatoryArg(newName.Dequeue(), t)
                | RestArg _ -> RestArg(newName.Dequeue())
                | KeywordArg(n, d) -> KeywordArg(n, go subst' d))

        args', subst'

    and go (subst: Map<string, string>) (e: Expr) : Expr =
        let sub = go subst
        let reference n = Map.tryFind n subst |> Option.defaultValue n

        match e with
        | EInt _
        | EString _
        | EChar _
        | EBool _
        // Deliberately not renamed: a substitution is what a shadow would do.
        | EResolved _
        | EQuotedSymbol _
        | EKeyword _ -> e
        | EIdent(n, r) -> EIdent(reference n, r)
        | ETuple(items, r) -> ETuple(List.map sub items, r)
        | EApp(EIdent(n, ir), args, r) when Set.contains n resolved && Map.containsKey n subst ->
            EApp(EResolved(subst[n], ir), List.map sub args, r)
        | EApp(target, args, r) -> EApp(sub target, List.map sub args, r)
        | ECast(t, v, r) -> ECast(t, sub v, r)
        // Trait name is a selector, not a binding, so it is not renamed.
        | EDynPack(traitName, v, r) -> EDynPack(traitName, sub v, r)

        | ELet(n, isFun, args, ann, value, body, r) ->
            // A function-shaped `let` is never self-recursive: `LetRecify` emits
            // one only for a singleton component with no self-edge.
            let args', valueSubst = if isFun then bindArgs args subst else args, subst
            let value' = go valueSubst value
            let names', bodySubst = bind [ n ] subst
            ELet(List.head names', isFun, args', ann, value', go bodySubst body, r)

        | ELetMono(n, value, body, r) ->
            let value' = go subst value
            let names', bodySubst = bind [ n ] subst
            ELetMono(List.head names', value', go bodySubst body, r)

        | ELetRec(bindings, body, r) ->
            // Every name in the group is bound before any value is renamed.
            let names = bindings |> List.map (fun (n, _, _, _, _) -> n)
            let names', groupSubst = bind names subst

            let bindings' =
                List.zip names' bindings
                |> List.map (fun (n', (_, isFun, args, ann, value)) ->
                    let args', valueSubst = if isFun then bindArgs args groupSubst else args, groupSubst
                    n', isFun, args', ann, go valueSubst value)

            ELetRec(bindings', go groupSubst body, r)

        | ELetTuple(names, value, body, r) ->
            let value' = sub value
            let names', bodySubst = bind names subst
            ELetTuple(names', value', go bodySubst body, r)

        | ELetMutable(n, ann, value, body, r) ->
            let value' = sub value
            let names', bodySubst = bind [ n ] subst
            ELetMutable(List.head names', ann, value', go bodySubst body, r)

        | ESet(n, value, r) -> ESet(reference n, sub value, r)
        | EIf(c, t, f, r) -> EIf(sub c, sub t, sub f, r)
        | EWhen(c, b, neg, r) -> EWhen(sub c, sub b, neg, r)

        | EFun(args, body, colour, r) ->
            let args', bodySubst = bind args subst
            EFun(args', go bodySubst body, colour, r)

        | ERecordUpdate(n, fields, r) -> ERecordUpdate(reference n, fields |> List.map (fun (k, v) -> k, sub v), r)
        | ERecordSet(n, fields, r) -> ERecordSet(reference n, fields |> List.map (fun (k, v) -> k, sub v), r)
        | EGetField(target, f, r) -> EGetField(sub target, f, r)
        | EList(items, r) -> EList(List.map sub items, r)
        | EVec(items, r) -> EVec(List.map sub items, r)
        | EArray(items, r) -> EArray(List.map sub items, r)

        | EMatch(target, clauses, r) ->
            EMatch(
                sub target,
                clauses
                |> List.map (fun (pat, guard, body) ->
                    let _, inner = bind (patternBinders pat) subst
                    // The step keeps the outer substitution: it is evaluated
                    // where the `match` is, not under what the clause binds.
                    renamePattern sub inner pat, Option.map (go inner) guard, go inner body),
                r
            )

        | ETryFinally(body, cleanup, r) -> ETryFinally(sub body, sub cleanup, r)
        | ETryCatch(body, exceptions, r) -> ETryCatch(sub body, exceptions, r)
        | ESeq(body, r) -> ESeq(sub body, r)
        | EBjo(body, kind, r) -> EBjo(sub body, kind, r)
        | ETaskEvent(body, r) -> ETaskEvent(sub body, r)
        | EYield(v, r) -> EYield(sub v, r)
        | EYieldFrom(s, r) -> EYieldFrom(sub s, r)

        | EWithReturn(name, body, r) ->
            let renamed, bodySubst = bind [ name ] subst
            EWithReturn(List.head renamed, go bodySubst body, r)

        | EDefMatch(binder, scrutinee, failure, sequel, r) ->
            // The scrutinee is evaluated in the scope the form began in, not
            // under what any pattern binds — same rule as a match clause's view
            // step.
            let scrutinee' = go subst scrutinee
            let _, sequelSubst = bind (patternBinders binder) subst

            let failure' =
                match failure with
                | FailNone -> FailNone
                | FailPropagate -> FailPropagate
                | FailValue value -> FailValue(go subst value)
                | FailArms arms ->
                    arms
                    |> List.map (fun (armPattern, armBody) ->
                        let _, armSubst = bind (patternBinders armPattern) subst
                        (renamePattern (go subst) armSubst armPattern, go armSubst armBody))
                    |> FailArms

            EDefMatch(
                renamePattern (go subst) sequelSubst binder,
                scrutinee',
                failure',
                go sequelSubst sequel,
                r
            )

    go rootSubst expr

/// Freshens every binder in `expr`, and every free occurrence of a name in
/// `roots`.
///
/// `roots` are the caller's chosen entry names — an inline template's formal
/// parameters — which are free in `expr` but still have to be renamed apart so
/// that the arguments can be substituted for names nothing else can mention.
/// The returned map covers exactly those.
let freshen (roots: string list) (expr: Expr) : Expr * Map<string, string> =
    let rootSubst =
        roots
        |> List.filter isRenamable
        |> List.map (fun r -> r, Gensym.fresh r)
        |> Map.ofList

    renameWith Set.empty (fun n -> if isRenamable n then Gensym.fresh n else n) rootSubst expr, rootSubst

/// Rewrites the *free* occurrences of the names in `subst`, leaving binders as
/// they are.
///
/// A name the expression binds itself keeps its meaning: the substitution is
/// dropped for the extent of that binder, so this cannot reach inside a scope
/// where the name means something else.
let renameFree (subst: Map<string, string>) (expr: Expr) : Expr =
    if Map.isEmpty subst then expr else renameWith Set.empty id subst expr

/// The same, except that a name in `resolved` becomes an `EResolved` wherever it
/// is called.
///
/// What a macro expansion needs for a trait method its template wrote: the
/// method has to dispatch as the macro's author meant it, which is not what a
/// binding of that name at the call site would do.
let renameFreeResolving (resolved: Set<string>) (subst: Map<string, string>) (expr: Expr) : Expr =
    if Map.isEmpty subst then expr else renameWith resolved id subst expr

/// A group of bindings that all take effect at once, expressed as bindings that
/// nest.
///
/// Nesting is the only scoping mechanism the AST has, so a simultaneous group
/// has to be built out of a sequential one that *means* the same thing. It does
/// as soon as no binder of the group is visible to a later init — and the only
/// way one can be is by having the name that a later init reads from further
/// out. So: rename exactly those binders apart, and rewrite the *body* to read
/// the new names. The inits are never rewritten; they mean what they meant in
/// the enclosing scope, which is the whole point.
///
///     (let ((x 1)) (let ((x 2) (y x)) (Tuple x y)))
///  => (let ((x 1)) (let ((x__7 2)) (let ((y x)) (Tuple x__7 y))))
///
/// Renaming only where it is needed, rather than everywhere: binder names
/// survive into the generated C# and into every message that mentions the
/// binding, so a gensym for a binder nothing shadows is a debugging cost with
/// nothing bought by it. Code that does not shadow-and-read — which is nearly
/// all code — comes out of this untouched.
///
/// A binding contributes a *list* of names because a destructuring binder binds
/// several at once, and each of them shadows on its own.
///
/// The caller keeps its own nesting and its own node types: what comes back is
/// the binder names to use, in source order, and the substitution to apply to
/// the body.
let simultaneous (bindings: (string list * Expr) list) : string list list * Map<string, string> =
    // `laterFree[i]` is everything the inits after `i` read. One element longer
    // than the group, so the last binding reads the empty set.
    let laterFree =
        List.foldBack
            (fun (_, init) (acc: Set<string> list) -> Set.union (freeNames Set.empty init) (List.head acc) :: acc)
            bindings
            [ Set.empty ]

    let renames =
        bindings
        |> List.mapi (fun i (names, _) ->
            names
            |> List.choose (fun n ->
                if isRenamable n && Set.contains n (List.item (i + 1) laterFree) then
                    // `baseName` first: freshening a name that is already a
                    // gensym would otherwise stack suffixes, `x__3__11`, and
                    // the C# local would carry both.
                    Some(n, Gensym.fresh (Gensym.baseName n))
                else
                    None))
        |> List.concat
        |> Map.ofList

    let renamed =
        bindings
        |> List.map (fun (names, _) -> names |> List.map (fun n -> Map.tryFind n renames |> Option.defaultValue n))

    renamed, renames

