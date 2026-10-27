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

/// Where a span may be.
///
/// A `Span<T>`, a `ReadOnlySpan<T>` and every other .NET `ref struct` is a
/// view of memory that is valid only while the call that made it runs. C#
/// lets one be a local, a parameter or a result, and refuses it everywhere it
/// could outlive the call. Bjolang gives the same rules, and this pass states
/// them in Bjolang terms. Without it, C# reports each one about generated code.
///
/// A span is refused when it would be:
///
///   * part of another value: in a vec, a list, a tuple, a record, an `Option`
///     or a `Result`, or a type argument of a generic function. .NET stores a
///     value of a type argument in the heap.
///   * a parameter or result of a function *value*. A function value is a
///     delegate, and a delegate cannot take a span. A function called by name
///     can.
///   * captured: used inside a `(fun ...)`, a local function, a loop that
///     stays a local function, a `(seq ...)` or a hoisted form, when it was
///     bound outside it. Each of those is emitted as a member of its own.
///   * in scope across a yield point of a bjoroutine, or across a `yield` of a
///     `(seq ...)`. Their locals are kept in a heap object while they wait.
///   * a parameter or result of a bjoroutine, a value handed to `(bjo ...)`,
///     or a module-level definition, which is a static field.
///
/// The pass runs after `LoopLowering`, beside `ColourCheck`, for the reason
/// that one does: only then is it known which loops stay local functions.
module Bjolang.SpanCheck

open System.Collections.Concurrent
open Bjolang.Lexer
open Bjolang.TypedAST

let private refStructs = ConcurrentDictionary<string * int, bool>()

/// Is this type a .NET `ref struct`, as `System.Span<T>` is? Asked of the
/// metadata, so a ref struct of any library counts.
let private isRefStructCon (name: string) (arity: int) : bool =
    refStructs.GetOrAdd(
        (name, arity),
        fun _ ->
            // A Bjolang type's key is dotted too, and is not looked for in
            // the loaded assemblies, because a miss searches all of them.
            name.Contains '.'
            && not (Naming.isModuleKey name)
            && (let clrName = if arity = 0 then name else $"%s{name}`%d{arity}"

                match DotNetInterop.tryResolveType clrName with
                | Some t -> t.IsByRefLike
                | None -> false)
    )

/// `t` with its solved metavariables followed, at its top only. The questions
/// below follow them at each level, because `Unification.prune` rebuilds the
/// whole type, and this pass asks of every node.
let rec private settle (t: HMType) : HMType =
    match t with
    | TMeta m ->
        match m.Value with
        | Some solved -> settle solved
        | None -> t
    | _ -> t

let private isRefStruct (t: HMType) : bool =
    match settle t with
    | TCon(name, args) -> isRefStructCon name args.Length
    | _ -> false

let rec private containsRef (t: HMType) : bool =
    match settle t with
    | TCon(name, args) -> isRefStructCon name args.Length || List.exists containsRef args
    | TFun(ps, r, _) -> containsRef r || List.exists containsRef ps
    | TTuple ts -> List.exists containsRef ts
    | _ -> false

/// The first ref struct in `t`, for a message.
let rec private firstRef (t: HMType) : HMType option =
    match settle t with
    | s when isRefStruct s -> Some s
    | TCon(_, args)
    | TTuple args -> List.tryPick firstRef args
    | TFun(ps, r, _) -> List.tryPick firstRef (ps @ [ r ])
    | _ -> None

/// Does `t` hold a ref struct anywhere but at its top? A span is a value of
/// its own; a vec of spans or a function over spans is not.
let private holdsRef (t: HMType) : bool =
    match settle t with
    | TCon(_, args)
    | TTuple args -> List.exists containsRef args
    | TFun(ps, r, _) -> containsRef r || List.exists containsRef ps
    | _ -> false

let private show (t: HMType) = DotNetInterop.showType t

let private shownRef (t: HMType) =
    firstRef t |> Option.map show |> Option.defaultValue (show t)

/// What every message ends with.
let private why =
    "A span is a view of memory that is valid only while the call that made it runs. It can be a local, an argument or a result, and nothing that can outlive the call."

let private refuse (r: Range) (lead: string) (advice: string) : 'a =
    failwithf $"Type Error at %s{formatPos r}: %s{lead}\n  %s{why}\n  %s{advice}"

/// Does evaluating `e` in the member it is written in reach a `yield` of a
/// `(seq ...)`? As `TypeVisitor.reachesAwait`, a lambda, a sequence and a
/// hoisted form are members of their own.
let rec private reachesYield (e: TypedExpr) : bool =
    match e.Node with
    | TYield _
    | TYieldFrom _ -> true
    | TLambda _
    | TSeq _
    | THoist _ -> false
    | _ -> TypeVisitor.children e |> List.exists reachesYield

/// Where an expression is emitted.
type private Ctx =
    { /// Names bound inside the innermost member: the closure, or the function
      /// body. A span with one of these names is the member's own.
      Own: Set<string>
      /// The member, when it is a closure inside another one, for the message.
      Closure: string option
      /// The member is a bjoroutine, so its locals wait in a heap object across
      /// each yield point.
      Async: bool
      /// The member is a `(seq ...)`, whose locals wait across each `yield`.
      Iterator: bool }

let private bodyOf (owned: string list) (isAsync: bool) : Ctx =
    { Own = Set.ofList owned
      Closure = None
      Async = isAsync
      Iterator = false }

let private bind (names: string list) (ctx: Ctx) : Ctx =
    { ctx with Own = Set.union ctx.Own (Set.ofList names) }

/// The names a pattern binds, with their types, and the expressions its views
/// run.
let private patternParts (pat: TypedPattern) : (string * HMType) list * TypedExpr list =
    let binders = ResizeArray<string * HMType>()
    let views = ResizeArray<TypedExpr>()

    let rec go (p: TypedPattern) : TypedPattern =
        match p.Node with
        | TPIdent n -> binders.Add(n, p.Type)
        | TPAs(_, n) -> binders.Add(n, p.Type)
        | TPTypeTest(clr, Some n) -> binders.Add(n, TCon(clr, []))
        | _ -> ()

        TypeVisitor.mapPatternChildrenWith
            (fun e ->
                views.Add e
                e)
            go
            p

    go pat |> ignore
    List.ofSeq binders, List.ofSeq views

let rec private check (registry: TraitRegistry) (ctx: Ctx) (exempt: bool) (expr: TypedExpr) : unit =
    let prune = Unification.prune registry
    let walk = check registry ctx false
    let walkIn c = check registry c false
    let t = expr.Type

    // A span inside another value, or a function value over spans.
    if not exempt && holdsRef t then
        let t = prune t

        match expr.Node, t with
        | TLambda _, _ ->
            refuse
                expr.Range
                $"this (fun ...) takes or answers a %s{shownRef t}, and a (fun ...) is a function value: a .NET delegate, which cannot take or answer a span."
                "Write it as a local (defun ...) and call it by name."
        | _, TFun _ ->
            refuse
                expr.Range
                $"this function takes or answers a %s{shownRef t}, and here it is used as a value: a .NET delegate, which cannot take or answer a span."
                "Call it by name instead of passing it."
        | _ ->
            refuse
                expr.Range
                $"this is a %s{show t}, which holds a %s{shownRef t}. A span cannot be part of another value: a vec, a list, a tuple, a record, an Option or a Result."
                "Copy the span with (.ToArray s) to keep its elements."

    // A span in scope of `body` across a yield point.
    let checkLive (name: string) (bindingType: HMType) (body: TypedExpr) (r: Range) =
        if containsRef bindingType then
            let bindingType = prune bindingType

            if ctx.Async && TypeVisitor.reachesAwait body then
                refuse
                    r
                    $"'%s{name}' is a %s{shownRef bindingType}, and it is in scope across a yield point of this bjoroutine. A bjoroutine keeps its locals in a heap object while it waits, and a span cannot be there."
                    "Do the span work in a (defun ...) that the bjoroutine calls, so the span ends before the yield point."
            elif ctx.Iterator && reachesYield body then
                refuse
                    r
                    $"'%s{name}' is a %s{shownRef bindingType}, and it is in scope across a (yield ...) of this (seq ...). A sequence keeps its locals in a heap object between elements, and a span cannot be there."
                    "Do the span work in a (defun ...) that the sequence calls, so the span ends before the yield."

    // Operands evaluated in order, before the call: a span among them must not
    // wait for a later one to suspend.
    let checkOperands (operands: TypedExpr list) =
        if ctx.Async then
            operands
            |> List.iteri (fun i op ->
                if containsRef op.Type && operands |> List.skip (i + 1) |> List.exists TypeVisitor.reachesAwait then
                    refuse
                        op.Range
                        $"this argument is a %s{shownRef (prune op.Type)}, and a later argument of the same call suspends. The span would be held across the yield point."
                        "Bind the later argument to a name before the call.")

    // A closure: a member of its own, which can run after the span is gone.
    let closure (label: string) (owned: string list) (isAsync: bool) (iterator: bool) (body: TypedExpr) =
        walkIn
            { Own = Set.ofList owned
              Closure = Some label
              Async = isAsync
              Iterator = iterator }
            body

    let lambda (label: string) (lam: TypedExpr) (ps: string list) (body: TypedExpr) =
        let isAsync = callSuspends lam.Type

        match prune lam.Type with
        | TFun(paramTypes, ret, _) when isAsync ->
            List.zip ps (List.truncate ps.Length paramTypes)
            |> List.tryFind (fun (_, pt) -> containsRef pt)
            |> Option.iter (fun (p, pt) ->
                refuse
                    lam.Range
                    $"%s{label} suspends, and its parameter '%s{p}' is a %s{shownRef pt}. A bjoroutine keeps its arguments in a heap object while it waits, and a span cannot be there."
                    "Take an array instead, or do the span work in a (defun ...).")

            if containsRef ret then
                refuse
                    lam.Range
                    $"%s{label} suspends and answers a %s{shownRef ret}. What a bjoroutine answers is kept in a heap object, and a span cannot be there."
                    "Answer an array instead: copy the span with (.ToArray s)."
        | _ -> ()

        closure label ps isAsync false body

    match expr.Node with
    | TIdent(name, tArgs) ->
        match tArgs |> List.tryFind containsRef with
        | Some arg ->
            refuse
                expr.Range
                $"'%s{name}' is generic, and here one of its type variables would be a %s{shownRef (prune arg)}. A span cannot be a type argument: .NET can keep a value of a type argument in the heap."
                "Use a function that takes the span type itself, or copy the span with (.ToArray s)."
        | None -> ()

        match ctx.Closure with
        | Some label when isRefStruct t && not (ctx.Own.Contains name) ->
            refuse
                expr.Range
                $"'%s{name}' is a %s{show (prune t)} bound outside %s{label}, and a span cannot be captured. That is emitted as a member of its own, which can run after the span is gone."
                "Pass the span to it as an argument, or copy it with (.ToArray s)."
        | _ -> ()

    | TApply(target, args, kwArgs) ->
        // A function called by name may take and answer spans; only its use as
        // a value cannot.
        check registry ctx (match target.Node with TIdent _ -> true | _ -> false) target
        checkOperands (target :: args @ List.map snd kwArgs)
        args |> List.iter walk
        kwArgs |> List.iter (snd >> walk)

    | TLambda(ps, body) -> lambda "this (fun ...)" expr ps body

    | TLet(name, isFun, _, value, body) ->
        match value.Node with
        | TLambda(ps, lamBody) when isFun ->
            lambda $"the local function '%s{name}'" value ps lamBody
        | _ -> walk value

        checkLive name value.Type body value.Range
        walkIn (bind [ name ] ctx) body

    | TLetMutable(name, value, body) ->
        walk value
        checkLive name value.Type body value.Range
        walkIn (bind [ name ] ctx) body

    | TLetRec(bindings, body) ->
        let inner = bind (bindings |> List.map (fun (n, _, _, _) -> n)) ctx

        for (name, isFun, _, value) in bindings do
            match value.Node with
            | TLambda(ps, lamBody) when isFun -> lambda $"the local function '%s{name}'" value ps lamBody
            | _ -> walkIn inner value

        walkIn inner body

    | TLetTuple(names, value, body) ->
        walk value
        walkIn (bind names ctx) body

    | TMatch(target, clauses) ->
        walk target

        for c in clauses do
            let binders, views = patternParts c.Pattern
            views |> List.iter walk
            let inner = bind (List.map fst binders) ctx

            for (n, bt) in binders do
                checkLive n bt c.Body c.Pattern.Range

            c.Guard |> Option.iter (walkIn inner)
            walkIn inner c.Body

    | TDefMatch(binder, scrutinee, sequel, arms) ->
        walk scrutinee
        let binders, views = patternParts binder
        views |> List.iter walk

        for (n, bt) in binders do
            checkLive n bt sequel binder.Range

        walkIn (bind (List.map fst binders) ctx) sequel

        for a in arms do
            let armBinders, armViews = patternParts a.Pattern
            armViews |> List.iter walk
            walkIn (bind (List.map fst armBinders) ctx) a.Body

    | TLoop(members, bodyOpt) ->
        let names = members |> List.map (fun m -> m.LoopName)
        let inlined =
            match bodyOpt with
            | None -> true
            | Some body -> LoopLowering.isInlinedLoop members body

        for m in members do
            let owned = names @ List.map fst m.Slots @ m.Locals
            let isAsync = if inlined then ctx.Async else groundEffect m.Effect = EAsync

            let suspendsWithin =
                if inlined then
                    ctx.Async && members |> List.exists (fun other -> TypeVisitor.reachesAwait other.Body)
                else
                    isAsync && TypeVisitor.reachesAwait m.Body

            match m.Slots |> List.tryFind (fun (_, st) -> containsRef (prune st)) with
            | Some(slot, st) when suspendsWithin ->
                refuse
                    m.Body.Range
                    $"the loop variable '%s{slot}' of '%s{m.LoopName}' is a %s{shownRef (prune st)}, and the loop suspends. A bjoroutine keeps its locals in a heap object while it waits, and a span cannot be there."
                    "Do the span work in a (defun ...) that the loop calls."
            | _ -> ()

            if inlined then
                walkIn (bind owned ctx) m.Body
            else
                closure $"the loop '%s{m.LoopName}' (it is not only tail-recursive, so it stays a local function)" owned isAsync false m.Body

        bodyOpt |> Option.iter (walkIn (bind names ctx))

    | TSeq body -> closure "this (seq ...)" [] false true body

    | THoist body -> closure "this hoisted form" [] false false body

    | TBjo(body, _) ->
        match body.Node with
        | TApply(target, args, kwArgs) ->
            for a in args @ List.map snd kwArgs do
                if containsRef (prune a.Type) then
                    refuse
                        a.Range
                        $"(bjo ...) hands its arguments to a new fiber, and this one is a %s{shownRef (prune a.Type)}. The fiber can run after the span is gone."
                        "Hand it an array: copy the span with (.ToArray s)."

            check registry ctx (match target.Node with TIdent _ -> true | _ -> false) target
            args |> List.iter walk
            kwArgs |> List.iter (snd >> walk)
        | _ -> closure "this (bjo ...)" [] true false body

    | TDotMethodCall(receiver, _, args, _) ->
        checkOperands (receiver :: args)
        TypeVisitor.children expr |> List.iter walk

    | TForeignStaticCall(_, _, args, _)
    | TNewObject(_, args, _) ->
        checkOperands args
        TypeVisitor.children expr |> List.iter walk

    | _ -> TypeVisitor.children expr |> List.iter walk

/// The fields and cases of the types `defs` declares.
let private checkTypes (registry: TraitRegistry) (defs: Ast.TypeDef list) (r: Range) =
    for def in defs do
        let typeName = Naming.showTypeName def.Name

        let fields =
            match Map.tryFind def.Name registry.Records, Map.tryFind def.Name registry.Unions with
            | Some(_, fs), _ -> fs |> List.map (fun (f, t) -> $"the field '%s{f}' of %s{typeName}", t)
            | None, Some(_, cases) ->
                cases
                |> List.collect (fun (c, ts) -> ts |> List.map (fun t -> $"the case %s{Naming.showTypeName c}", t))
            | None, None -> []

        for (what, ft) in fields do
            let ft = Unification.prune registry ft

            if containsRef ft then
                refuse
                    r
                    $"%s{what} holds a %s{show ft}, and a record or a union cannot hold a span: its value is kept in the heap."
                    "Hold an array instead, and make the span where it is used."

let rec private checkDecl (registry: TraitRegistry) (decl: TDecl) : unit =
    let prune = Unification.prune registry

    let moduleValue (name: string) (t: HMType) (r: Range) =
        if containsRef (prune t) then
            refuse
                r
                $"'%s{name}' is a module-level definition of type %s{show (prune t)}. A module-level value is a static field, and a span cannot be one."
                "Make the span in the function that uses it."

    // A module-level initializer runs in the static constructor, an ordinary
    // member.
    let initializer (value: TypedExpr) = check registry (bodyOf [] false) false value

    match decl with
    | TModule(_, inner, _) -> inner |> List.iter (checkDecl registry)
    | TImpl(_, _, _, _, _, _, methods, _) -> methods |> List.iter (checkDecl registry)
    | TDef(name, value, t, r)
    | TDefMutable(name, value, t, r) ->
        moduleValue name t r
        initializer value
    | TDefTuple(names, value, t, r) ->
        moduleValue (String.concat " " names) t r
        initializer value
    | TDefPattern(_, value, binders, r) ->
        for (n, bt) in binders do
            moduleValue n bt r

        initializer value
    | TDefun(name, _, args, kwArgs, restArg, retType, effect, body, r) ->
        let isAsync = groundEffect effect = EAsync

        if isAsync then
            match args |> List.tryFind (fun (_, at) -> containsRef (prune at)) with
            | Some(p, pt) ->
                refuse
                    r
                    $"'%s{name}' is a bjoroutine, and its parameter '%s{p}' is a %s{shownRef (prune pt)}. A bjoroutine keeps its arguments in a heap object while it waits, and a span cannot be there."
                    "Take an array instead, or do the span work in a (defun ...)."
            | None -> ()

            if containsRef (prune retType) then
                refuse
                    r
                    $"'%s{name}' is a bjoroutine, and it answers a %s{shownRef (prune retType)}. What a bjoroutine answers is kept in a heap object, and a span cannot be there."
                    "Answer an array instead: copy the span with (.ToArray s)."

        let owned =
            List.map fst args
            @ (kwArgs |> List.map (fun (n, _, _) -> n))
            @ (restArg |> Option.map fst |> Option.toList)

        let ctx = bodyOf owned isAsync
        kwArgs |> List.iter (fun (_, _, d) -> check registry ctx false d)
        check registry ctx false body
    | TType(defs, r)
    | TTypeRec(defs, r) -> checkTypes registry defs r
    | _ -> ()

/// Reports the first misplaced span of each declaration.
let run (registry: TraitRegistry) (decls: TDecl list) : unit =
    decls
    |> List.iter (fun d -> Diagnostics.recover "span" (Some(tdeclRange d)) () (fun () -> checkDecl registry d))
