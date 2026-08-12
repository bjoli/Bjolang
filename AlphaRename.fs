module Bjolang.AlphaRename

open Bjolang.Parser
open Bjolang.TypedAST

/// Capture-avoiding renaming, over both the untyped and the typed AST.
///
/// This is deliberately *not* a CS0136 patch. It is the substrate three
/// different things need:
///
///   * `freshen`, over untyped `Expr`, is the inliner's splice hygiene. A
///     template body is renamed apart from its call site *before* the arguments
///     are bound into it, because binding first destroys meaning that no later
///     pass can recover: `TLet(a, argA, TLet(b, argB, body))` captures a caller
///     variable named `a` that `argB` mentions, and a faithful renamer will then
///     faithfully preserve the wrong binding.
///   * `renameExpr`, over `TypedExpr`, is `LoopLowering`'s per-iteration
///     parameter copying, promoted out of that pass.
///   * `uniquifyDecl` is the global cleanup pass that keeps C# from seeing two
///     locals of one name in nested scopes.
///
/// Everything is parameterized over *which* binders to freshen rather than
/// hardcoded to "all of them", so that hygienic macro expansion can reuse it.

// ---------------------------------------------------------------------------
// What may never be renamed
// ---------------------------------------------------------------------------

/// A name whose spelling is part of an interface someone else relies on.
///
///   * `::` marks a name the compiler synthesized to reach into another class —
///     `Foldable_List.Instance::fold`, `core_Module::helper`. It never names a
///     binder, and rewriting one would point it somewhere else entirely.
///   * A keyword parameter's name is the calling convention: `Codegen` emits it
///     as a C# named argument at every call site.
let isRenamable (name: string) : bool =
    not (name.Contains "::") && name <> "_"

// ---------------------------------------------------------------------------
// Untyped: `freshen`
// ---------------------------------------------------------------------------

let private withoutKeys (names: string seq) (subst: Map<string, string>) =
    names |> Seq.fold (fun acc n -> Map.remove n acc) subst

/// Every name a pattern binds.
let rec patternBinders (pat: Pattern) : string list =
    match pat with
    | PWildcard _
    | PInt _
    | PString _
    | PChar _
    | PKeyword _
    | PQuotedSymbol _ -> []
    | PIdent(n, _) -> [ n ]
    | PTypeTest(_, binder, _) -> Option.toList binder
    | PList(items, tailOpt, _)
    | PVec(items, tailOpt, _) ->
        (items |> List.collect patternBinders)
        @ (tailOpt |> Option.map patternBinders |> Option.defaultValue [])
    | PTuple(items, _) -> items |> List.collect patternBinders
    | PConstruct(_, args, _) -> args |> List.collect patternBinders

let rec private renamePattern (subst: Map<string, string>) (pat: Pattern) : Pattern =
    match pat with
    | PIdent(n, r) -> PIdent((Map.tryFind n subst |> Option.defaultValue n), r)
    | PList(items, tailOpt, r) ->
        PList(List.map (renamePattern subst) items, Option.map (renamePattern subst) tailOpt, r)
    | PVec(items, tailOpt, r) ->
        PVec(List.map (renamePattern subst) items, Option.map (renamePattern subst) tailOpt, r)
    | PTuple(items, r) ->
        PTuple(List.map (renamePattern subst) items, r)
    | PConstruct(n, args, r) -> PConstruct(n, List.map (renamePattern subst) args, r)
    | PTypeTest(t, binder, r) -> PTypeTest(t, binder |> Option.map (fun n -> Map.tryFind n subst |> Option.defaultValue n), r)
    | leaf -> leaf

/// Renames names in `expr`, given how to rename a binder and what the free
/// names start out substituted by.
///
/// `renameBinder` returning a name unchanged is what makes this usable for a
/// substitution that must *not* freshen: `bind` then drops the name from the
/// substitution instead of adding to it, so a binder shadows an outer name
/// exactly as it does at runtime.
let private renameWith
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

    let rec go (subst: Map<string, string>) (e: Expr) : Expr =
        let sub = go subst
        let reference n = Map.tryFind n subst |> Option.defaultValue n

        match e with
        | EInt _
        | EString _
        | EChar _
        | EQuotedSymbol _
        | EKeyword _ -> e
        | EIdent(n, r) -> EIdent(reference n, r)
        | ETuple(items, r) -> ETuple(List.map sub items, r)
        | EApp(target, args, r) -> EApp(sub target, List.map sub args, r)
        | ECast(t, v, r) -> ECast(t, sub v, r)

        | ELet(n, isFun, args, ann, value, body, r) ->
            // A function-shaped `let` is never self-recursive: `LetRecify` emits
            // one only for a singleton component with no self-edge.
            let args', valueSubst = if isFun then bind args subst else args, subst
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
                    let args', valueSubst = if isFun then bind args groupSubst else args, groupSubst
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

        | ERecordUpdate(n, fields, r) ->
            ERecordUpdate(reference n, fields |> List.map (fun (k, v) -> k, sub v), r)
        | EGetField(target, f, r) -> EGetField(sub target, f, r)
        | EList(items, r) -> EList(List.map sub items, r)
        | EVec(items, r) -> EVec(List.map sub items, r)

        | EMatch(target, clauses, r) ->
            EMatch(
                sub target,
                clauses
                |> List.map (fun (pat, guard, body) ->
                    let _, inner = bind (patternBinders pat) subst
                    renamePattern inner pat, Option.map (go inner) guard, go inner body),
                r
            )

        | ETryFinally(body, cleanup, r) -> ETryFinally(sub body, sub cleanup, r)
        | ETryCatch(body, exceptions, r) -> ETryCatch(sub body, exceptions, r)
        | ESeq(body, r) -> ESeq(sub body, r)
        | EBjo(body, r) -> EBjo(sub body, r)
        | ETaskEvent(body, r) -> ETaskEvent(sub body, r)
        | EYield(v, r) -> EYield(sub v, r)
        | EYieldFrom(s, r) -> EYieldFrom(sub s, r)

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

    renameWith (fun n -> if isRenamable n then Gensym.fresh n else n) rootSubst expr, rootSubst

/// Rewrites the *free* occurrences of the names in `subst`, leaving binders as
/// they are.
///
/// A name the expression binds itself keeps its meaning: the substitution is
/// dropped for the extent of that binder, so this cannot reach inside a scope
/// where the name means something else.
let renameFree (subst: Map<string, string>) (expr: Expr) : Expr =
    if Map.isEmpty subst then expr else renameWith id subst expr

/// Every name `expr` references without binding, given `bound` already in scope.
///
/// Used to decide which of an inline template's references have to be qualified
/// to the module they came from: a name the body binds itself is not free, and a
/// formal parameter is bound by the splice.
let freeNames (bound: Set<string>) (expr: Expr) : Set<string> =
    let mutable acc = Set.empty

    let rec go (bound: Set<string>) (e: Expr) =
        let sub = go bound
        let reference n = if not (Set.contains n bound) then acc <- Set.add n acc

        match e with
        | EInt _
        | EString _
        | EChar _
        | EQuotedSymbol _
        | EKeyword _ -> ()
        | EIdent(n, _) -> reference n
        | ETuple(items, _)
        | EList(items, _)
        | EVec(items, _) -> List.iter sub items
        | EApp(target, args, _) ->
            sub target
            List.iter sub args
        | ECast(_, v, _) -> sub v

        | ELet(n, isFun, args, _, value, body, _) ->
            go (if isFun then Set.union bound (Set.ofList args) else bound) value
            go (Set.add n bound) body

        | ELetMono(n, value, body, _) ->
            go bound value
            go (Set.add n bound) body

        | ELetRec(bindings, body, _) ->
            let inner = bindings |> List.fold (fun acc (n, _, _, _, _) -> Set.add n acc) bound

            for (_, isFun, args, _, value) in bindings do
                go (if isFun then Set.union inner (Set.ofList args) else inner) value

            go inner body

        | ELetTuple(names, value, body, _) ->
            sub value
            go (Set.union bound (Set.ofList names)) body

        | ELetMutable(n, _, value, body, _) ->
            sub value
            go (Set.add n bound) body

        | ESet(n, value, _) ->
            reference n
            sub value

        | EIf(c, t, f, _) ->
            sub c
            sub t
            sub f
        | EWhen(c, b, _, _) ->
            sub c
            sub b
        | EFun(args, body, _, _) -> go (Set.union bound (Set.ofList args)) body
        | ERecordUpdate(n, fields, _) ->
            reference n
            fields |> List.iter (snd >> sub)
        | EGetField(target, _, _) -> sub target

        | EMatch(target, clauses, _) ->
            sub target

            for (pat, guard, body) in clauses do
                let inner = Set.union bound (Set.ofList (patternBinders pat))
                Option.iter (go inner) guard
                go inner body

        | ETryFinally(body, cleanup, _) ->
            sub body
            sub cleanup
        | ETryCatch(body, _, _) -> sub body
        | ESeq(body, _) -> sub body
        | EBjo(body, _)
        | ETaskEvent(body, _) -> sub body
        | EYield(v, _) -> sub v
        | EYieldFrom(s, _) -> sub s

    go bound expr
    acc

// ---------------------------------------------------------------------------
// Typed: the parameterized core
// ---------------------------------------------------------------------------

let rec private typedPatternBinders (pat: TypedPattern) : string list =
    match pat.Node with
    | TPWildcard
    | TPInt _
    | TPString _
    | TPChar _
    | TPKeyword _
    | TPSymbol _ -> []
    | TPIdent n -> [ n ]
    | TPTypeTest(_, binder) -> Option.toList binder
    | TPList(items, tailOpt)
    | TPVec(items, tailOpt) ->
        (items |> List.collect typedPatternBinders)
        @ (tailOpt |> Option.map typedPatternBinders |> Option.defaultValue [])
    | TPTuple items -> items |> List.collect typedPatternBinders
    | TPConstruct(_, args) -> args |> List.collect typedPatternBinders
    // `TPApp` holds an expression, not a binder; the pattern it wraps binds.
    | TPApp(_, inner) -> typedPatternBinders inner
    | TPAs(inner, n) -> n :: typedPatternBinders inner

/// Kept for callers that only need the names.
let patternNames = typedPatternBinders

/// The one traversal every typed renaming goes through.
///
/// `freshenBinder` decides, per binder name, whether that binder is renamed and
/// to what. Returning `None` leaves it alone — which is what plain substitution
/// wants — and any `Some` both rewrites the binder and extends the substitution
/// over its scope.
let rec private renameCore
    (freshenBinder: Set<string> -> string -> string option)
    (scope: Set<string>)
    (subst: Map<string, string>)
    (expr: TypedExpr)
    : TypedExpr =

    /// Binds `names`, returning their emitted spellings, the extended scope and
    /// the extended substitution.
    let bind (names: string list) (scope: Set<string>) (subst: Map<string, string>) =
        let mutable sc = scope
        let mutable sb = subst

        let renamed =
            names
            |> List.map (fun n ->
                let n' =
                    if isRenamable n then
                        freshenBinder sc n |> Option.defaultValue n
                    else
                        n

                sb <- if n = n' then Map.remove n sb else Map.add n n' sb
                sc <- Set.add n' (Set.add n sc)
                n')

        renamed, sc, sb

    let sub e = renameCore freshenBinder scope subst e
    let reference n = Map.tryFind n subst |> Option.defaultValue n

    let node =
        match expr.Node with
        | TIdent(n, tArgs) -> TIdent(reference n, tArgs)
        | TSet(n, v) -> TSet(reference n, sub v)
        | TRecordUpdate(n, fields) -> TRecordUpdate(reference n, fields |> List.map (fun (k, v) -> k, sub v))

        | TLet(n, isFun, args, v, b) ->
            let args', vScope, vSubst = if isFun then bind args scope subst else args, scope, subst
            let v' = renameCore freshenBinder vScope vSubst v
            let names', bScope, bSubst = bind [ n ] scope subst
            TLet(List.head names', isFun, args', v', renameCore freshenBinder bScope bSubst b)

        | TLetRec(bindings, b) ->
            // Every name in the group is in scope in every value.
            let names = bindings |> List.map (fun (n, _, _, _) -> n)
            let names', gScope, gSubst = bind names scope subst

            TLetRec(
                List.zip names' bindings
                |> List.map (fun (n', (_, isFun, args, v)) ->
                    let args', vScope, vSubst = if isFun then bind args gScope gSubst else args, gScope, gSubst
                    n', isFun, args', renameCore freshenBinder vScope vSubst v),
                renameCore freshenBinder gScope gSubst b
            )

        | TLetTuple(names, v, b) ->
            let v' = sub v
            let names', bScope, bSubst = bind names scope subst
            TLetTuple(names', v', renameCore freshenBinder bScope bSubst b)

        | TLetMutable(n, v, b) ->
            let v' = sub v
            let names', bScope, bSubst = bind [ n ] scope subst
            TLetMutable(List.head names', v', renameCore freshenBinder bScope bSubst b)

        | TLambda(args, b) ->
            let args', bScope, bSubst = bind args scope subst
            TLambda(args', renameCore freshenBinder bScope bSubst b)

        | TMatch(target, clauses) ->
            TMatch(
                sub target,
                clauses
                |> List.map (fun c ->
                    let _, inner, innerSubst = bind (typedPatternBinders c.Pattern) scope subst

                    // The pattern hole `LoopLowering` left open: `TPApp` carries
                    // a `TypedExpr` whose free variables live in the *enclosing*
                    // scope, and `TPAs` binds a name. Neither used to be
                    // touched, so a free variable inside a view pattern escaped
                    // renaming entirely.
                    let rec goPat (p: TypedPattern) : TypedPattern =
                        let pnode =
                            match p.Node with
                            | TPIdent n -> TPIdent(Map.tryFind n innerSubst |> Option.defaultValue n)
                            | TPAs(inner', n) ->
                                TPAs(goPat inner', Map.tryFind n innerSubst |> Option.defaultValue n)
                            | TPApp(e, inner') ->
                                // Evaluated in the scope the `match` sits in,
                                // not under the names the pattern binds.
                                TPApp(sub e, goPat inner')
                            | TPList(items, tailOpt) -> TPList(List.map goPat items, Option.map goPat tailOpt)
                            | TPVec(items, tailOpt) -> TPVec(List.map goPat items, Option.map goPat tailOpt)
                            | TPTuple items -> TPTuple(List.map goPat items)
                            | TPConstruct(n, args) -> TPConstruct(n, List.map goPat args)
                            | leaf -> leaf

                        { p with Node = pnode }

                    { Pattern = goPat c.Pattern
                      Guard = Option.map (renameCore freshenBinder inner innerSubst) c.Guard
                      Body = renameCore freshenBinder inner innerSubst c.Body })
            )

        | TLoop(members, bodyOpt) ->
            // Member names are in scope throughout the group; a member's slots
            // and per-iteration locals are in scope in its own body only.
            let memberNames = members |> List.map (fun m -> m.LoopName)
            let memberNames', gScope, gSubst = bind memberNames scope subst

            /// Follows an existing renaming without inventing a new one. The
            /// substitution is left in place, because these names are not a new
            /// binding — they are the same one, spelled wherever it was already
            /// decided.
            let keep (names: string list) (scope: Set<string>) (subst: Map<string, string>) =
                let renamed = names |> List.map (fun n -> Map.tryFind n subst |> Option.defaultValue n)
                renamed, (renamed |> List.fold (fun acc n -> Set.add n acc) scope), subst

            // `TLoop (_, None)` *is* an enclosing function's body, and its slots
            // name that function's own parameters — they are already declared,
            // by the C# method signature, and renaming one here would leave the
            // signature spelling the old name.
            let bindSlots = if bodyOpt.IsSome then bind else keep

            TLoop(
                List.zip memberNames' members
                |> List.map (fun (name', m) ->
                    // `Slots` and `Locals` are parallel by index and a `TRecur`
                    // argument vector is positionally aligned with `Slots`, so
                    // both lists are rebuilt in place — never reordered, never
                    // filtered.
                    let slotNames', s1, sb1 = bindSlots (m.Slots |> List.map fst) gScope gSubst
                    let locals', s2, sb2 = bind m.Locals s1 sb1

                    { m with
                        LoopName = name'
                        Slots = List.zip slotNames' (m.Slots |> List.map snd)
                        Locals = locals'
                        Body = renameCore freshenBinder s2 sb2 m.Body }),
                Option.map (renameCore freshenBinder gScope gSubst) bodyOpt
            )

        | _ -> (TypeVisitor.mapChildren sub expr).Node

    { expr with Node = node }

/// Renames *free* occurrences of the keys of `subst`, respecting every binder.
let renameExpr (subst: Map<string, string>) (expr: TypedExpr) : TypedExpr =
    if Map.isEmpty subst then
        expr
    else
        renameCore (fun _ _ -> None) Set.empty subst expr

/// Freshens every binder in a typed expression, plus free occurrences of
/// `roots`. The typed counterpart of `freshen`, used to keep beta reduction
/// hygienic.
let freshenTyped (roots: string list) (expr: TypedExpr) : TypedExpr * Map<string, string> =
    let rootSubst =
        roots
        |> List.filter isRenamable
        |> List.map (fun r -> r, Gensym.fresh r)
        |> Map.ofList

    renameCore (fun _ n -> Some(Gensym.fresh (Gensym.baseName n))) Set.empty rootSubst expr, rootSubst

// ---------------------------------------------------------------------------
// The global CS0136 pass
// ---------------------------------------------------------------------------

/// Freshens a binder whose name something in this function has already taken.
///
/// C# rejects a local that shadows an enclosing local or parameter (CS0136), and
/// rejects two locals of one name in the same block (CS0128) — and a Bjolang
/// `let` chain compiles to exactly that. Renaming only on collision keeps the
/// generated code readable, and keeps this pass from being able to change
/// anything that was already correct.
///
/// What counts as a collision is the *whole function*, not the path from its
/// root to the binder. Two sibling `let`s are separate scopes in Bjolang and
/// neither can see the other, but a `let` body is emitted as statements in the
/// block around it — so to C# they are one declaration space, and
///
///     (println (let () (def tmp 1) tmp))
///     (let ((tmp 9)) (println tmp))
///
/// is two locals called `tmp` in one method. A macro makes this the common case
/// rather than a curiosity: a template binds names the caller cannot see, and
/// the caller binds names the template's author could not.
let private shadowing (used: System.Collections.Generic.HashSet<string>) (_scope: Set<string>) (name: string) : string option =
    if used.Add name then
        None
    else
        // Fresh, so it needs no checking against `used` itself.
        Some(Gensym.fresh (Gensym.baseName name))

/// Every top-level name in the program.
///
/// A local that shares one of these names is renamed too. `Codegen` qualifies a
/// module-level binding to the class that holds it, and it decides that from the
/// name alone — so a local called `helper` used to be emitted as
/// `Origin_Module.helper`, silently reading the module's value instead of its
/// own. Making the local's name unique is what makes that decision safe, and it
/// is what lets an inlined body reach a module-level helper past a caller's
/// local of the same name.
let rec private topLevelNames (decls: TDecl list) : Set<string> =
    decls
    |> List.collect (function
        | TModule(_, inner, _) -> Set.toList (topLevelNames inner)
        | TDef(n, _, _, _)
        | TDefMutable(n, _, _, _)
        | TExtern(n, _, _) -> [ n ]
        | TDefun(n, _, _, _, _, _, _, _, _) -> [ n ]
        | TDefTuple(names, _, _, _) -> names
        | _ -> [])
    |> Set.ofList

/// Runs after `LoopLowering` and immediately before code generation.
///
/// A cleanup pass, and only that: it is *not* the inliner's hygiene mechanism.
/// The inliner freshens at the splice, because by the time this runs the wrong
/// binding would already have been made and would merely be preserved
/// faithfully.
let rec uniquifyDeclWith (globals: Set<string>) (decl: TDecl) : TDecl =
    /// One emitted method, and one set of names it has already used. Everything
    /// that goes into the same C# member shares it — a keyword parameter's
    /// default is evaluated in the method that declares it, not somewhere else.
    let inFunction (scope: Set<string>) (subst: Map<string, string>) =
        let used = System.Collections.Generic.HashSet<string>(scope)
        fun (body: TypedExpr) -> renameCore (shadowing used) scope subst body

    match decl with
    | TModule(name, decls, r) -> TModule(name, decls |> List.map (uniquifyDeclWith globals), r)
    | TImpl(traitName, kind, holeArity, targetType, assoc, methods, r) ->
        TImpl(traitName, kind, holeArity, targetType, assoc, methods |> List.map (uniquifyDeclWith globals), r)

    | TDefun(name, tyArgs, args, kwArgs, restArg, retType, effect, body, r) ->
        // A positional parameter may be renamed; a keyword parameter may not,
        // because `Codegen` emits its name as a C# named argument at every call
        // site, which makes the spelling part of the calling convention.
        let renameParam (n: string) =
            if isRenamable n && not (n.StartsWith "_dict_") && Set.contains n globals then
                Some(n, Gensym.fresh (Gensym.baseName n))
            else
                None

        let positional = (args |> List.map fst) @ (restArg |> Option.map fst |> Option.toList)
        let paramSubst = positional |> List.choose renameParam |> Map.ofList
        let spell n = Map.tryFind n paramSubst |> Option.defaultValue n

        let args' = args |> List.map (fun (n, t) -> spell n, t)
        let restArg' = restArg |> Option.map (fun (n, t) -> spell n, t)

        let scope =
            Set.unionMany
                [ globals
                  args' |> List.map fst |> Set.ofList
                  kwArgs |> List.map (fun (n, _, _) -> n) |> Set.ofList
                  restArg' |> Option.map fst |> Option.toList |> Set.ofList ]

        // One set of used names for the whole method: a keyword parameter's
        // default is evaluated inside the member that declares it, so a local it
        // introduces shares a declaration space with the body's.
        let inMethod = inFunction scope paramSubst

        TDefun(
            name,
            tyArgs,
            args',
            kwArgs |> List.map (fun (n, t, e) -> n, t, inMethod e),
            restArg',
            retType,
            effect,
            inMethod body,
            r
        )

    | TDef(name, value, t, r) -> TDef(name, inFunction globals Map.empty value, t, r)
    | TDefTuple(names, value, t, r) -> TDefTuple(names, inFunction globals Map.empty value, t, r)
    | TDefMutable(name, value, t, r) -> TDefMutable(name, inFunction globals Map.empty value, t, r)
    | _ -> decl

let uniquifyDecl (decl: TDecl) : TDecl = uniquifyDeclWith (topLevelNames [ decl ]) decl

let uniquifyProgram (decls: TDecl list) : TDecl list =
    let globals = topLevelNames decls
    decls |> List.map (uniquifyDeclWith globals)
