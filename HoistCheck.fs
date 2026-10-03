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

/// Whether a `(please-hoist-i-promise-i-am-not-naughty expr)` form can be
/// hoisted.
///
/// A hoisted form is evaluated once, the first time it is reached, in a method
/// of its own, and every later evaluation answers that first value. The writer
/// promises the two things the compiler cannot check: that the form has no
/// effects worth repeating, and that it does not read dynamic context such as
/// a parameter or an effect handler. Everything else that would make keeping
/// the first value wrong is checked here, and every reason is reported at once,
/// in one error per form, so that a macro writer sees the whole list.
///
/// One reason is not reported here: a call that awaits. Which copy of a
/// `defbjouble` a call means is only decided after loop lowering, so that is
/// `ColourCheck`'s, under its `InHoist` site.
///
/// Runs beside `MustUse` and `Exhaustiveness`, on the program as written, so
/// that a monomorphised or inlined copy is not reported on again.
module Bjolang.HoistCheck

open Bjolang.Lexer
open Bjolang.TypedAST

/// The expressions a pattern holds: the steps of its views. They are evaluated
/// in the scope the pattern is matched in, not in the scope it binds.
let rec private patternExprs (pat: TypedPattern) : TypedExpr list =
    match pat.Node with
    | TPApp(step, inner) -> step :: patternExprs inner
    | _ ->
        let acc = ResizeArray<TypedExpr>()
        let rec collect p =
            TypeVisitor.mapPatternChildrenWith
                (fun e ->
                    acc.Add e
                    e)
                (fun child ->
                    acc.AddRange(patternExprs child)
                    child)
                p
            |> ignore
        collect pat
        List.ofSeq acc

/// Calls `visit scope expr` for every expression under `expr`, where `scope` is
/// the set of local names bound around that expression.
///
/// Every binder in the typed AST is handled here, because a binder missed is a
/// local this check would mistake for a global: `TLet`, `TLetRec`, `TLetTuple`,
/// `TLetMutable`, `TLambda`, the clauses of `TMatch` and `TDefMatch`, and a
/// loop member's slots. A keyword parameter's default is not a child of its
/// node, so it is walked from the `LocalFun` that holds it.
let rec private walk (visit: Set<string> -> TypedExpr -> unit) (scope: Set<string>) (expr: TypedExpr) : unit =
    visit scope expr
    let go = walk visit

    let funScope (fn: LocalFun) (s: Set<string>) =
        let s = List.fold (fun acc n -> Set.add n acc) s fn.Params
        let s = List.fold (fun acc (n, _, _) -> Set.add n acc) s fn.KeywordArgs
        match fn.RestArg with
        | Some(n, _) -> Set.add n s
        | None -> s

    let funValue (fn: LocalFun) (s: Set<string>) (value: TypedExpr) =
        let inner = funScope fn s
        for (_, _, d) in fn.KeywordArgs do
            go inner d
        go inner value

    match expr.Node with
    | TLet(name, isFun, fn, value, body) ->
        if isFun then funValue fn (Set.add name scope) value else go scope value
        go (Set.add name scope) body

    | TLetRec(bindings, body) ->
        let all = bindings |> List.fold (fun acc (n, _, _, _) -> Set.add n acc) scope
        for (_, isFun, fn, value) in bindings do
            if isFun then funValue fn all value else go all value
        go all body

    | TLetTuple(names, value, body) ->
        go scope value
        go (Set.union scope (Set.ofList names)) body

    | TLetMutable(name, value, body) ->
        go scope value
        go (Set.add name scope) body

    | TLambda(args, body) -> go (Set.union scope (Set.ofList args)) body

    | TMatch(target, clauses) ->
        go scope target
        for c in clauses do
            List.iter (go scope) (patternExprs c.Pattern)
            let inner = Set.union scope (Set.ofList (AlphaRename.patternNames c.Pattern))
            Option.iter (go inner) c.Guard
            go inner c.Body

    | TDefMatch(binder, scrutinee, sequel, arms) ->
        List.iter (go scope) (patternExprs binder)
        go scope scrutinee
        go (Set.union scope (Set.ofList (AlphaRename.patternNames binder))) sequel
        for a in arms do
            List.iter (go scope) (patternExprs a.Pattern)
            go (Set.union scope (Set.ofList (AlphaRename.patternNames a.Pattern))) a.Body

    | TLoop(members, bodyOpt) ->
        for m in members do
            let inner =
                m.Slots |> List.fold (fun acc (n, _) -> Set.add n acc) scope
                |> fun s -> List.fold (fun acc n -> Set.add n acc) s m.Locals
            go inner m.Body
        Option.iter (go scope) bodyOpt

    | _ -> TypeVisitor.children expr |> List.iter (go scope)

/// The rigid type variables in `t`: the ones the function around the form
/// introduced, which the form's method cannot name.
let rec private typeVars (registry: TraitRegistry) (t: HMType) : string list =
    match Unification.prune registry t with
    | TVar n -> [ n ]
    | TCon(_, args)
    | TTuple args -> List.collect (typeVars registry) args
    | TFun(args, ret, _) -> List.collect (typeVars registry) args @ typeVars registry ret
    | TAssoc(_, _, impl) -> typeVars registry impl
    | TMeta _ -> []

let private show (t: HMType) = DotNetInterop.showType t

/// Why a value of this type cannot be shared between evaluations, if it
/// cannot: it is something a later evaluation could write to, and every
/// evaluation would be handed the same one.
let private mutableReason (registry: TraitRegistry) (t: HMType) : string option =
    match Unification.prune registry t with
    | TCon(name, _) ->
        let last = name.Substring(name.LastIndexOf '.' + 1)
        let fields = Traits.mutableFieldsOf registry name

        if name = "Array" then
            Some "an Array, which array-set! writes in place"
        elif name = "Box" then
            Some "a Box, which box-set! writes in place"
        elif last.StartsWith "Mutable" || last.StartsWith "Transient" then
            Some $"a %s{last}, which is written in place"
        elif last.EndsWith "Builder" then
            Some $"a %s{last}, which is filled in place"
        elif not fields.IsEmpty then
            let shown = fields |> List.map (fun f -> $"`%s{f}`") |> String.concat ", "
            Some $"a %s{Naming.writtenName name}, whose #:mutable field %s{shown} can be written in place"
        else
            None
    | _ -> None

/// Every reason the form at `hoist` cannot be hoisted.
///
/// `locals` is what is bound around the form, `ownDefs` the module-level
/// values of the module it is written in, and `bindings` the module-level
/// environment, which says whether an imported name is a `def/mutable`.
let private reasons
    (registry: TraitRegistry)
    (bindings: Map<string, Binding>)
    (locals: Set<string>)
    (ownDefs: Set<string>)
    (hoist: TypedExpr)
    (body: TypedExpr)
    : string list =

    let found = ResizeArray<string>()
    let seen = System.Collections.Generic.HashSet<string>()

    let add (key: string) (reason: string) =
        if seen.Add key then found.Add reason

    // Names as they were written: without a module qualifier, and without the
    // mark a binder gets when it is renamed apart.
    let written n = Gensym.baseName (Naming.writtenName n)

    // Names: locals from around the form, and module-level values.
    body
    |> walk
        (fun inner e ->
            let free n = not (Set.contains n inner)

            match e.Node with
            | TIdent(n, _) when free n && Set.contains n locals ->
                add
                    $"local:%s{n}"
                    $"it cannot refer to the local `%s{written n}`: the form is evaluated once, so it would keep the `%s{written n}` of the first evaluation for every later one."
            | TSet(n, _) when free n && Set.contains n locals ->
                add
                    $"set:%s{n}"
                    $"it cannot assign the local `%s{written n}`: the form runs in a method of its own, where that local does not exist."
            | TIdent(n, _) when free n && Set.contains n ownDefs ->
                let isMutable =
                    match Map.tryFind n bindings with
                    | Some b -> b.IsMutable
                    | None -> false

                if isMutable then
                    add
                        $"def:%s{n}"
                        $"it cannot read `%s{written n}`, a def/mutable: its value can change, and the form would keep the one it read first."
                else
                    add
                        $"def:%s{n}"
                        $"it cannot read `%s{written n}`, a def of this module: the form may run while the module is still being initialised, before `%s{written n}` has its value. Move the def to another module, or hoist the expression it is defined by instead."
            | TIdent(n, _) when free n && not (Set.contains n locals) ->
                match Map.tryFind n bindings with
                | Some b when b.IsMutable ->
                    add
                        $"def:%s{n}"
                        $"it cannot read `%s{written n}`, a def/mutable: its value can change, and the form would keep the one it read first."
                | _ -> ()
            | _ -> ())
        Set.empty

    // Jumps: a `yield` to a sequence around the form, a `ret` to a block
    // around it. Neither is in the form's method.
    let rec jumps (labels: Set<string>) (e: TypedExpr) =
        match e.Node with
        | TSeq _ -> ()
        | TYield _
        | TYieldFrom _ ->
            add
                "yield"
                "it cannot yield to the (seq ...) around it: the form runs in a method of its own, which is not the sequence's iterator."
        | TWithReturn(label, inner) -> jumps (Set.add label labels) inner
        | TReturn(label, _) when not (Set.contains label labels) ->
            add
                $"ret:%s{label}"
                "it cannot use the escape of a (with-return ...) around it: the form runs in a method of its own, and that block is not part of it."
        | _ -> TypeVisitor.children e |> List.iter (jumps labels)

    jumps Set.empty body

    // Types: a variable of the function around the form appears nowhere in
    // it, and what it produces is a value that can be shared.
    let vars =
        TypeVisitor.foldExpr (fun acc (e: TypedExpr) -> acc @ typeVars registry e.Type) [] body
        |> List.distinct

    if not vars.IsEmpty then
        let shown = vars |> List.map (fun v -> "%" + v.TrimStart('\'')) |> String.concat ", "
        add
            "tvars"
            $"it cannot use the type variable %s{shown} of the function around it: the form is evaluated once for every type the function is called at, so it cannot depend on which."

    let carriesNothing =
        match Unification.prune registry hoist.Type with
        | TCon(TypeConstants.UnitName, [])
        | TCon(TypeConstants.VoidName, [])
        | TTuple [] -> true
        | _ -> false

    if carriesNothing then
        add "void" "it cannot be void: hoisting keeps a value, and this form has none to keep."
    else
        match mutableReason registry hoist.Type with
        | Some what ->
            add
                "mutable"
                $"it cannot produce %s{what}: every evaluation would be handed the same one, so a write through one would be seen by all the others. Its type is %s{show hoist.Type}."
        | None -> ()

    List.ofSeq found

/// The module-level values a list of declarations defines: what a hoisted form
/// in that module may not read, because the module's static constructor may
/// not have assigned it yet.
let private defNames (decls: TDecl list) : Set<string> =
    decls
    |> List.collect (function
        | TDef(n, _, _, _)
        | TDefMutable(n, _, _, _) -> [ n ]
        | TDefTuple(names, _, _, _) -> names
        | TDefPattern(_, _, binders, _) -> List.map fst binders
        | _ -> [])
    |> Set.ofList

let private report (range: Range) (lines: string list) =
    let listed = lines |> List.map (fun l -> "  - " + l) |> String.concat "\n"

    failwithf
        $"Type Error at %s{formatPos range}: this (please-hoist-i-promise-i-am-not-naughty ...) form cannot be hoisted:\n%s{listed}\n  A hoisted form is evaluated once, the first time it is reached, in a method of its own, and every later evaluation answers that first value. It may use literals, functions, and the defs of other modules."

/// Checks every hoisted form in `decls`, reporting each one that cannot be
/// hoisted as an error of its own.
let run (registry: TraitRegistry) (bindings: Map<string, Binding>) (decls: TDecl list) : unit =
    let checkExpr (ownDefs: Set<string>) (locals: Set<string>) (expr: TypedExpr) =
        expr
        |> walk
            (fun scope e ->
                match e.Node with
                | THoist body ->
                    Diagnostics.recover "hoist" (Some e.Range) () (fun () ->
                        match reasons registry bindings scope ownDefs e body with
                        | [] -> ()
                        | found -> report e.Range found)
                | _ -> ())
            locals

    let rec checkDecls (ownDefs: Set<string>) (decls: TDecl list) =
        for d in decls do
            match d with
            | TModule(_, inner, _) -> checkDecls (defNames inner) inner
            | TImpl(_, _, _, _, _, _, methods, _) -> checkDecls ownDefs methods
            | TDefun(_, _, args, kwArgs, rest, _, _, body, _) ->
                let locals =
                    args
                    |> List.fold (fun acc (n, _) -> Set.add n acc) Set.empty
                    |> fun s -> List.fold (fun acc (n, _, _) -> Set.add n acc) s kwArgs
                    |> fun s ->
                        match rest with
                        | Some(n, _) -> Set.add n s
                        | None -> s
                for (_, _, d) in kwArgs do
                    checkExpr ownDefs locals d
                checkExpr ownDefs locals body
            | _ -> TypeVisitor.mapDecl (fun e -> checkExpr ownDefs Set.empty e; e) d |> ignore

    checkDecls (defNames decls) decls
