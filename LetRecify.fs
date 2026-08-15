module Bjolang.LetRecify

open Bjolang.Parser

/// Combine two free-var maps; if a var appears in both, Unguarded (false) wins.
let mergeVarUseMaps (a: Map<string, bool>) (b: Map<string, bool>) =
    let folder (acc: Map<string, bool>) (key: string) (useType: bool) =
        match Map.tryFind key acc with
        | Some false -> acc // Unguarded already exists; keep it.
        | _ -> Map.add key useType acc // Overwrites True with False, or inserts new.

    Map.fold folder a b

/// Walk an untyped Expr, returning free variables with their use classification.
/// `isGuarded`: true when inside at least one lambda body.
/// `bound`: variables bound in the current scope (to exclude from free vars).
let rec exprFreeVars (isGuarded: bool) (bound: Set<string>) (expr: Expr) : Map<string, bool> =
    let classify = isGuarded

    match expr with
    | EInt _
    | EString _
    | EChar _
    | EQuotedSymbol _
    | EKeyword _ -> Map.empty
    | EIdent(name, _) ->
        if Set.contains name bound then
            Map.empty
        else
            Map.add name classify Map.empty
    | ETuple(exprs, _)
    | EList(exprs, _)
    | EVec(exprs, _) ->
        exprs
        |> List.map (exprFreeVars isGuarded bound)
        |> List.fold mergeVarUseMaps Map.empty
    | EApp(target, args, _) ->
        let tfv = exprFreeVars isGuarded bound target

        let afvs =
            args
            |> List.map (exprFreeVars isGuarded bound)
            |> List.fold mergeVarUseMaps Map.empty

        mergeVarUseMaps tfv afvs
    | ECast(_, expr, _) -> exprFreeVars isGuarded bound expr
    | ELet(name, isFun, args, typeAnn, value, body, _) ->
        let boundInValue = if isFun then Set.union bound (Set.ofList args) else bound
        let vfv = exprFreeVars (isGuarded || isFun) boundInValue value
        let bfv = exprFreeVars isGuarded (Set.add name bound) body
        mergeVarUseMaps vfv bfv

    | ELetMono(name, value, body, _) ->
        let vfv = exprFreeVars isGuarded bound value
        let bfv = exprFreeVars isGuarded (Set.add name bound) body
        mergeVarUseMaps vfv bfv

    | ELetRec(bindings, body, _) ->
        let boundNames = bindings |> List.map (fun (n, _, _, _, _) -> n) |> Set.ofList
        let allBound = Set.union bound boundNames

        let bfvs =
            bindings
            |> List.map (fun (_, isFun, args, _, expr) ->
                let boundInExpr =
                    if isFun then
                        Set.union allBound (Set.ofList args)
                    else
                        allBound

                exprFreeVars (isGuarded || isFun) boundInExpr expr)
            |> List.fold mergeVarUseMaps Map.empty

        let bodyFv = exprFreeVars isGuarded allBound body
        mergeVarUseMaps bfvs bodyFv
    | ELetTuple(names, value, body, _) ->
        let vfv = exprFreeVars isGuarded bound value
        let bfv = exprFreeVars isGuarded (Set.union bound (Set.ofList names)) body
        mergeVarUseMaps vfv bfv
    | EIf(cond, trueB, falseB, _) ->
        let cfv = exprFreeVars isGuarded bound cond
        let tfv = exprFreeVars isGuarded bound trueB
        let ffv = exprFreeVars isGuarded bound falseB
        mergeVarUseMaps (mergeVarUseMaps cfv tfv) ffv
    | EWhen(cond, body, _, _) ->
        let cfv = exprFreeVars isGuarded bound cond
        let bfv = exprFreeVars isGuarded bound body
        mergeVarUseMaps cfv bfv
    | EFun(args, body, _, _) -> exprFreeVars true (Set.union bound (Set.ofList args)) body
    | ERecordUpdate(_, fields, _) ->
        fields
        |> List.map (snd >> exprFreeVars isGuarded bound)
        |> List.fold mergeVarUseMaps Map.empty
    | EGetField(target, _, _) -> exprFreeVars isGuarded bound target
    | EMatch(target, clauses, _) ->
        let tfv = exprFreeVars isGuarded bound target

        let cfvs =
            clauses
            |> List.map (fun (pat, guard, body) ->
                let patBounds = patternBinders pat |> Set.ofList
                let innerBound = Set.union bound patBounds

                let gfv =
                    match guard with
                    | Some g -> exprFreeVars isGuarded innerBound g
                    | None -> Map.empty

                let bfv = exprFreeVars isGuarded innerBound body
                mergeVarUseMaps gfv bfv)
            |> List.fold mergeVarUseMaps Map.empty

        mergeVarUseMaps tfv cfvs
    | ELetMutable(name, typeAnn, value, body, _) ->
        let vfv = exprFreeVars isGuarded bound value
        let bfv = exprFreeVars isGuarded (Set.add name bound) body
        mergeVarUseMaps vfv bfv
    | ESet(name, value, _) ->
        let vfv = exprFreeVars isGuarded bound value

        let tfv =
            if Set.contains name bound then
                Map.empty
            else
                Map.add name classify Map.empty

        mergeVarUseMaps tfv vfv
    | ETryFinally(body, cleanup, _) ->
        mergeVarUseMaps (exprFreeVars isGuarded bound body) (exprFreeVars isGuarded bound cleanup)
    | ETryCatch(body, _, _) -> exprFreeVars isGuarded bound body
    // A `seq` body is deferred exactly as a lambda body is: nothing in it runs
    // until the sequence is consumed, so a reference from inside one is guarded
    // and can point at a binding defined later in the same group.
    | ESeq(body, _) -> exprFreeVars true bound body
    // The operands are evaluated where the form is written; only the call is
    // deferred. Guarded all the same, because the call is.
    | EBjo(body, _)
    | ETaskEvent(body, _) -> exprFreeVars true bound body
    | EYield(value, _)
    | EYieldFrom(value, _) -> exprFreeVars isGuarded bound value

/// Kosaraju's algorithm: compute SCCs of a graph, returned in reverse topological order.
let computeSCCs (nodes: Set<string>) (edges: Map<string, Map<string, bool>>) : Set<string> list =
    let mutable visited = Set.empty
    let mutable order: string list = []

    let rec dfsForward (node: string) =
        // Prevent traversal from escaping into external/global symbols
        if Set.contains node nodes && not (Set.contains node visited) then
            visited <- Set.add node visited

            match Map.tryFind node edges with
            | Some neighbors ->
                for nbr in Map.keys neighbors do
                    dfsForward nbr
            | None -> ()

            order <- node :: order

    for node in nodes do
        dfsForward node

    let revEdges =
        nodes
        |> Seq.fold
            (fun acc node ->
                let mutable acc' = acc

                match Map.tryFind node edges with
                | Some neighbors ->
                    for nbr in Map.keys neighbors do
                        // Only map reverse dependencies within the local block
                        if Set.contains nbr nodes then
                            acc' <-
                                match Map.tryFind nbr acc' with
                                | Some existing -> Map.add nbr (Set.add node existing) acc'
                                | None -> Map.add nbr (Set.singleton node) acc'
                | None -> ()

                if not (Map.containsKey node acc') then
                    acc' <- Map.add node Set.empty acc'

                acc')
            Map.empty

    visited <- Set.empty
    let mutable sccs: Set<string> list = []

    let rec dfsReverse (node: string) (acc: Set<string>) =
        if Set.contains node nodes && not (Set.contains node visited) then
            visited <- Set.add node visited
            let mutable acc' = Set.add node acc

            match Map.tryFind node revEdges with
            | Some neighbors ->
                for nbr in neighbors do
                    acc' <- dfsReverse nbr acc'
            | None -> ()

            acc'
        else
            acc

    for node in order do
        if Set.contains node nodes && not (Set.contains node visited) then
            let scc = dfsReverse node Set.empty
            sccs <- scc :: sccs

    sccs


/// Recursively optimizes ELetRec blocks into minimal ELet/ELetRec chains
let rec letrecifyExpr (expr: Expr) : Expr =
    match expr with
    | EInt _
    | EString _
    | EChar _
    | EQuotedSymbol _
    | EKeyword _
    | EIdent _ -> expr

    | ETuple(exprs, r) -> ETuple(List.map letrecifyExpr exprs, r)
    | EList(exprs, r) -> EList(List.map letrecifyExpr exprs, r)
    | EVec(exprs, r) -> EVec(List.map letrecifyExpr exprs, r)
    | EApp(target, args, r) -> EApp(letrecifyExpr target, List.map letrecifyExpr args, r)
    | ECast(t, e, r) -> ECast(t, letrecifyExpr e, r)

    | ELet(name, isFun, args, typeAnn, value, body, r) -> ELet(name, isFun, args, typeAnn, letrecifyExpr value, letrecifyExpr body, r)

    | ELetMono(name, value, body, r) -> ELetMono(name, letrecifyExpr value, letrecifyExpr body, r)

    | ELetTuple(names, value, body, r) -> ELetTuple(names, letrecifyExpr value, letrecifyExpr body, r)

    | EIf(cond, t, f, r) -> EIf(letrecifyExpr cond, letrecifyExpr t, letrecifyExpr f, r)

    | EWhen(cond, body, negated, r) -> EWhen(letrecifyExpr cond, letrecifyExpr body, negated, r)

    | EFun(args, body, colour, r) -> EFun(args, letrecifyExpr body, colour, r)

    | ERecordUpdate(baseRec, fields, r) ->
        ERecordUpdate(baseRec, fields |> List.map (fun (k, v) -> k, letrecifyExpr v), r)

    | EGetField(target, field, r) -> EGetField(letrecifyExpr target, field, r)

    | EMatch(target, clauses, r) ->
        let optimizedClauses =
            clauses
            |> List.map (fun (p, g, b) -> (p, Option.map letrecifyExpr g, letrecifyExpr b))

        EMatch(letrecifyExpr target, optimizedClauses, r)

    | ELetMutable(name, typeAnn, value, body, r) -> ELetMutable(name, typeAnn, letrecifyExpr value, letrecifyExpr body, r)

    | ESet(name, value, r) -> ESet(name, letrecifyExpr value, r)

    | ETryFinally(body, cleanup, r) -> ETryFinally(letrecifyExpr body, letrecifyExpr cleanup, r)
    | ETryCatch(body, exceptions, r) -> ETryCatch(letrecifyExpr body, exceptions, r)

    | ESeq(body, r) -> ESeq(letrecifyExpr body, r)
    | EBjo(body, r) -> EBjo(letrecifyExpr body, r)
    | ETaskEvent(body, r) -> ETaskEvent(letrecifyExpr body, r)
    | EYield(value, r) -> EYield(letrecifyExpr value, r)
    | EYieldFrom(value, r) -> EYieldFrom(letrecifyExpr value, r)

    | ELetRec(bindings, body, r) ->
        let optBindings =
            bindings |> List.map (fun (n, isF, args, t, e) -> (n, isF, args, t, letrecifyExpr e))

        let optBody = letrecifyExpr body

        let nodes = optBindings |> List.map (fun (n, _, _, _, _) -> n) |> Set.ofList

        let edges =
            optBindings
            |> List.map (fun (n, isFun, args, _, e) ->
                let boundInExpr = if isFun then Set.ofList args else Set.empty
                let fvs = exprFreeVars isFun boundInExpr e
                let localDeps = fvs |> Map.filter (fun k _ -> Set.contains k nodes)
                (n, localDeps))
            |> Map.ofList

        let sccs = computeSCCs nodes edges

        // Sort components topologically while preserving original source order for independent nodes
        // to prevent reordering side effects.
        let sourceIndex =
            optBindings |> List.mapi (fun i (n, _, _, _, _) -> n, i) |> Map.ofList

        /// A component's position: that of its earliest member.
        let keyOf (scc: Set<string>) =
            scc |> Set.toSeq |> Seq.map (fun n -> Map.find n sourceIndex) |> Seq.min

        let ownerOf =
            sccs
            |> List.collect (fun scc -> scc |> Set.toList |> List.map (fun n -> n, keyOf scc))
            |> Map.ofList

        /// The components this one has to follow.
        let dependsOn (scc: Set<string>) =
            scc
            |> Set.toList
            |> List.collect (fun n ->
                match Map.tryFind n edges with
                | Some deps -> deps |> Map.toList |> List.map fst
                | None -> [])
            |> List.map (fun d -> Map.find d ownerOf)
            |> List.filter (fun o -> o <> keyOf scc)
            |> Set.ofList

        let orderedSccs =
            let rec go (remaining: Set<string> list) (emitted: Set<int>) acc =
                match remaining with
                | [] -> List.rev acc
                | _ ->
                    let ready = remaining |> List.filter (fun s -> Set.isSubset (dependsOn s) emitted)

                    // `ready` can only be empty if the condensation had a cycle,
                    // which by construction it cannot. Falling back to the
                    // earliest remaining component keeps this total rather than
                    // looping, and degrades to the old behaviour rather than to
                    // a hang.
                    let pick = (if ready.IsEmpty then remaining else ready) |> List.minBy keyOf

                    go
                        (remaining |> List.filter (fun s -> keyOf s <> keyOf pick))
                        (Set.add (keyOf pick) emitted)
                        (pick :: acc)

            go sccs Set.empty []

        let bindingMap =
            optBindings |> List.map (fun ((n, _, _, _, _) as b) -> n, b) |> Map.ofList

        List.foldBack
            (fun scc accBody ->
                // Source order, not `Set.toList`'s. A set of strings comes back
                // sorted ordinally, and every name here is a gensym `p__N` with
                // a decimal counter — so `p__11` sorts before `p__8` and the
                // group's members come out in an order that depends on how many
                // names the compilation happened to invent earlier.
                //
                // That is not cosmetic. `Inference`'s `ELetRec` checks members in
                // list order and relies on an earlier member's call site having
                // pinned a later member's argument types; a body checked against
                // bare metavariables cannot resolve an associated-type
                // projection. A loop's levels are emitted outermost-first, which
                // is exactly the order that works, and sorting by name threw it
                // away.
                let componentNodes =
                    optBindings
                    |> List.map (fun (n, _, _, _, _) -> n)
                    |> List.filter (fun n -> Set.contains n scc)

                let componentBindings = componentNodes |> List.map (fun n -> Map.find n bindingMap)

                if componentNodes.Length = 1 then
                    let n = componentNodes[0]
                    let (_, isF, args, t, e) = componentBindings[0]

                    let isSelfRecursive =
                        match Map.tryFind n edges with
                        | Some deps -> Map.containsKey n deps
                        | None -> false

                    if isSelfRecursive then
                        ELetRec(componentBindings, accBody, r)
                    else
                        ELet(n, isF, args, t, e, accBody, r)
                else
                    ELetRec(componentBindings, accBody, r)

            )
            orderedSccs
            optBody

/// Walks declarations, applying the LetRecify pass to all inner bodies.
let rec letrecifyDecl (decl: Decl) : Decl =
    match decl with
    | DDef(name, expr, r) -> DDef(name, letrecifyExpr expr, r)
    | DDefTuple(names, expr, r) -> DDefTuple(names, letrecifyExpr expr, r)
    | DDefMutable(name, expr, r) -> DDefMutable(name, letrecifyExpr expr, r)
    | DDefun(name, args, body, colour, r) ->
        let letrecifiedArgs =
            args |> List.map (function
                | KeywordArg(n, defaultExpr) -> KeywordArg(n, letrecifyExpr defaultExpr)
                | other -> other)
        DDefun(name, letrecifiedArgs, letrecifyExpr body, colour, r)
    | DModule(name, decls, r) -> DModule(name, letrecifyModule decls, r)
    | _ -> decl // Types, imports, exports, and signatures carry no executable body [cite: 29, 30, 31, 37, 38]

and letrecifyModule (decls: Decl list) : Decl list = List.map letrecifyDecl decls
