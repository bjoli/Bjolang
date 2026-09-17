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

module Bjolang.TypeEnv

open Bjolang.Lexer
open Bjolang.Ast
open Bjolang.TypedAST
open Bjolang.Unification

/// The name a visible spelling stands for, where the spelling is only that.
///
/// A def is deliberately left alone: it is *bound* under the name the import
/// gave it, and codegen resolves that to the origin's member through
/// `GlobalBindings` — which is what keeps a renamed import from being taken
/// over by a same-named binding elsewhere in the program. Everything else is
/// resolved away before any registry is consulted, because `Implementations`,
/// `InlineMethods`, `Unions`, `Records` and `Aliases` are all keyed on the name
/// the declaring module wrote.
let originalName (registry: TraitRegistry) (name: string) : string =
    match Map.tryFind name registry.ImportAliases with
    | Some alias when alias.Kind <> AliasDef && alias.Kind <> AliasMacro -> alias.OriginalName
    | _ -> name

/// What to add to a lookup that has just failed, when the name is a member of
/// an imported `#:opaque` type.
///
/// A hidden constructor or field is registered nowhere, so every use of one
/// fails on the ordinary path — as a constructor that does not exist, a
/// variable that is not bound, a field no record has. That is the correct
/// refusal and the wrong explanation, and this is the only thing that stands
/// between the two. Empty for a name that is genuinely unknown, so a caller can
/// append it unconditionally.
let hiddenMemberNote (registry: TraitRegistry) (name: string) : string =
    match Map.tryFind name registry.HiddenMembers with
    | Some typeKey ->
        $" '%s{name}' belongs to %s{Naming.showTypeName typeKey}, which is exported #:opaque: the type's name crosses the module boundary and its representation does not, so a value of it can be held and passed on but not taken apart here."
    | None -> ""

/// The same, for a type whose representation did not cross — reached when the
/// *type* is known and the member name is not the thing that failed.
let opaqueTypeNote (registry: TraitRegistry) (typeName: string) : string =
    if Set.contains typeName registry.OpaqueTypes then
        $" %s{Naming.showTypeName typeName} is exported #:opaque, so its fields did not cross the module boundary."
    else
        ""

/// Walk a typed expression body for the trait constraints its enclosing function
/// must carry. Returns a list of TraitConstraints (TraitName, TargetType as TVar).
/// Constraints arise from trait method calls on type variables, or from calling 
/// constrained functions with type variables.
let collectTraitConstraints (env: Env) (body: TypedExpr) : TraitConstraint list =
    let registry = env.Registry

    let step (acc: Set<string * string>) (expr: TypedExpr) =
        match expr.Node with
        // A numeric literal that settled at one of this function's own type
        // variables. It is emitted as `T_a.CreateChecked(1)`, which is a member
        // of `INumberBase` — so the function has to carry the constraint that
        // puts it there, and the operators around the literal need not have
        // asked for it: `(if (< x 3) 3 x)` is `IComparisonOperators` and
        // nothing else.
        | TInt _ ->
            match prune registry expr.Type with
            | TVar v -> Set.add ("Num", v) acc
            | _ -> acc

        // A trait call, whether or not the solver pinned it down. The node says
        // which trait it belongs to, so there is nothing to guess: looking the
        // method name up across every trait picked an arbitrary one whenever two
        // traits shared a method.
        //
        // An unresolved call needs a dictionary for the variable it dispatches
        // on. A *resolved* one usually needs nothing — but a conditional impl
        // was selected on the strength of its `(where ...)`, and that clause has
        // to be paid for by whoever instantiates this function: `(->str xs)` at
        // `(List %a)` resolves here and still owes a `(->str %a)`.
        | TTraitCall(tref, _, _) ->
            let acc =
                tref.Holes
                |> List.fold
                    (fun acc hole ->
                        leafConstraints registry tref.Trait hole
                        |> List.fold (fun acc c -> Set.add c acc) acc)
                    acc

            // A member-level `(where ...)`: the constraints the member's own
            // clause raised at this call — discharged here if the target is
            // concrete, owed by the enclosing function if it is not.
            tref.MemberConstraints
            |> List.fold
                (fun acc c ->
                    leafConstraints registry c.TraitName c.TargetType
                    |> List.fold (fun acc leaf -> Set.add leaf acc) acc)
                acc

        // Packing a value into a trait box requires constructing the same
        // dictionary evidence as calling a method on it. For type variables,
        // dictionary evidence must be provided via a parameter on the enclosing
        // function.
        | TDynPack(traitName, hole, _) ->
            leafConstraints registry traitName hole
            |> List.fold (fun acc c -> Set.add c acc) acc

        | TApply({ Node = TIdent(calleeName, tArgs) }, _, _) ->
            // `tArgs` is positionally aligned with the callee's scheme
            // variables, so it says what each of them was instantiated to
            // at this call site.
            match Map.tryFind calleeName env.Bindings with
            | Some binding ->
                let (Scheme(schemeVars, constraints, _)) = binding.Scheme

                if constraints.IsEmpty || schemeVars.Length <> tArgs.Length then
                    acc
                else
                    let varSubst = List.zip schemeVars tArgs |> Map.ofList

                    constraints
                    |> List.fold
                        (fun acc c ->
                            let instantiated =
                                match c.TargetType with
                                | TVar v -> Map.tryFind v varSubst |> Option.defaultValue c.TargetType
                                | t -> t

                            // A concrete instantiation usually resolves to a
                            // real impl at this call site and needs nothing
                            // from our caller — unless that impl is itself
                            // conditional, in which case what it demands is
                            // asked of us in turn.
                            leafConstraints registry c.TraitName instantiated
                            |> List.fold (fun acc leaf -> Set.add leaf acc) acc)
                        acc
            | None -> acc
        | _ -> acc

    TypeVisitor.foldExpr step Set.empty body
    |> Set.toList
    |> List.map (fun (traitName, varName) ->
        { TraitName = traitName; TargetType = TVar varName; Pins = [] })

// --- INFERENCE ENGINE ---

/// What a numeric literal's spelling says it is, with `int` where it says
/// nothing.
///
/// The total answer, for the two places that need one whatever the literal
/// looks like: a pattern, which is emitted as a C# constant and so has to have
/// its type settled where it is written, and literal *elaboration*, which reads
/// a shape rather than a type. Every other use goes through
/// `numericLiteralType` below and leaves a bare integer open.
let inferNumericType (value: string) : HMType =
    NumericLiteral.spelledType value |> Option.defaultValue TypeConstants.intType

/// The numeric literals whose type is still open, and where each was written.
///
/// A literal with no suffix does not say what it is, so it is inferred as a
/// metavariable and whatever it meets decides. That is the whole of numeric
/// literal polymorphism: without it the `1` in `(bitwise-and x 1)` pinned `%a`
/// to `int` before the body could say otherwise, and a generic numeric
/// function could not be written however well the constraints worked.
///
/// Nothing may *stay* open. `defaultNumericLiterals` settles the survivors at
/// `int` before the enclosing declaration generalizes, and is also where a
/// literal that met a type no number can have is refused.
///
/// Per compilation, and emptied by every defaulting pass — which leaves the
/// next one nothing to walk.
let internal openLiterals = ResizeArray<HMType * string * Range>()

/// Drops whatever is still open. For the compilation that *failed*: see
/// `clearWanteds`, which this is the other half of.
let clearNumericLiterals () : unit = openLiterals.Clear()

/// Refuses a literal the type it has ended up at cannot hold.
let internal checkLiteralFits (t: HMType) (text: string) (r: Range) : HMType =
    if not (NumericLiteral.fits t text) then
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{text}' does not fit in a '%s{DotNetInterop.showType t}'."

    t

/// The type of a numeric literal as an expression.
///
/// A suffix fixes it outright. A bare integer becomes a fresh metavariable —
/// see `openLiterals`.
let internal numericLiteralType (value: string) (r: Range) : HMType =
    match NumericLiteral.spelledType value with
    | Some t -> checkLiteralFits t value r
    | None ->
        let m = freshMeta ()
        openLiterals.Add(m, value, r)
        m

/// Follows a metavariable's bindings without a registry.
///
/// `prune` wants one, and the callers below have none to hand. The answer is
/// the same: nothing here resolves an associated type.
let rec private followMeta (t: HMType) : HMType =
    match t with
    | TMeta { Value = Some inner } -> followMeta inner
    | _ -> t

/// The metavariables a type is still waiting on.
///
/// Deliberately registry-free: this is consulted from `generalize`, which has no
/// business being handed a queue, let alone an environment.
let rec internal metaIdsOf (t: HMType) : int list =
    t
    |> foldType (function
        | TMeta m ->
            match m.Value with
            | Some inner -> metaIdsOf inner
            | None -> [ m.Id ]
        | _ -> [])

/// Settles the numeric literals among `types` at `int`, now.
///
/// For the .NET boundary, which needs a concrete type where the rest of
/// inference can wait. Reflection picks an overload by argument type and scores
/// one that is still open worst against every candidate, so leaving a literal
/// open turned `(round 5)` from `Round(double)` into an ambiguity with
/// `Round(decimal)`. A literal handed to .NET is an `int` and always was; C#'s
/// own widening is what takes it the rest of the way.
let internal settleLiterals (types: HMType list) : unit =
    let waiting = types |> List.collect metaIdsOf |> Set.ofList

    if not (Set.isEmpty waiting) then
        for (t, _, _) in openLiterals do
            match followMeta t with
            | TMeta m when Set.contains m.Id waiting -> m.Value <- Some TypeConstants.intType
            | _ -> ()

/// The environment slot a `seq` records its element type in, so that the
/// `yield`s in its body have something to unify against.
///
/// A `yield` belongs to the nearest enclosing `seq`, which is precisely ordinary
/// lexical scoping — so it is expressed as an ordinary binding rather than as a
/// side channel, and a nested `seq` shadows the outer one for free. The name
/// contains a space, which no token can, so nothing in source can collide with
/// it or read it.
let private seqElementSlot = " seq-element"

let internal withSeqElement (elemType: HMType) (env: Env) : Env =
    { env with
        Bindings =
            Map.add
                seqElementSlot
                { Scheme = Scheme([], [], elemType); IsMutable = false }
                env.Bindings }

/// Leaves the enclosing `seq`, if any. A lambda body is compiled as a function
/// of its own and cannot be resumed, so it cannot yield into the sequence it
/// happens to be written inside.
/// Module level's binding for `name`, put back over whatever is in scope.
///
/// What an `EResolved` needs: the compiler wrote that name, so it has to mean
/// what it meant where it was written. Only the one name is restored, and only
/// for the expression it heads — everything around it keeps the scope it has,
/// which is why this is not simply `{ env with Bindings = env.Resolved }`: the
/// arguments of a synthesised call are ordinary user code.
///
/// A name absent from `Resolved` was not a module-level *binding* at all. That
/// is the usual case for a union constructor such as `folding`, which reaches
/// its meaning through the registry, where nothing in scope can interfere.
let internal unshadow (name: string) (env: Env) : Env =
    // Dispatch comes back with the binding. A trait method that something has
    // bound over is no longer dispatched on, which is the point of the rule —
    // but the compiler wrote *this* mention, and it meant the method.
    let env =
        if Map.containsKey name env.Registry.TraitMethods then
            { env with TraitMethodNames = Set.add name env.TraitMethodNames }
        else
            env

    // The shape comes back with the binding. `addBinding` dropped it when the
    // local went in, and a `#:rest` call without it is a call with the wrong
    // number of arguments.
    let env =
        match Map.tryFind name env.ResolvedFunMetas with
        | Some meta when Map.tryFind name env.FunMetas <> Some meta ->
            { env with FunMetas = Map.add name meta env.FunMetas }
        | _ -> env

    match Map.tryFind name env.Resolved with
    | Some binding when Map.tryFind name env.Bindings <> Some binding ->
        { env with Bindings = Map.add name binding env.Bindings }

    // Nothing at module level had this name, and yet something in scope does.
    // The name is a *spelling* then — a union case's, filed against the key that
    // holds the binding — so taking the local out of the way is what restores
    // the module-level meaning: `resolveAliasedHead` compares what is bound
    // against module level, and with the local gone the two agree again.
    | _ when
        Map.tryFind name env.Bindings <> Map.tryFind name env.Resolved
        && originalName env.Registry name <> name
        ->
        { env with
            Bindings =
                match Map.tryFind name env.Resolved with
                | Some binding -> Map.add name binding env.Bindings
                | None -> Map.remove name env.Bindings }

    | _ -> env

/// The spelling a resolved name is emitted under when a local has its bare one.
///
/// `unshadow` gives inference the module-level binding back, but the typed
/// node still carries the bare name, and C# resolves a bare name lexically:
/// emitted beside the local it would bind to it. The class-qualified spelling
/// is what `Codegen` emits past a local — the same one an inlined body uses
/// for its free names — so a shadowed resolved name is rewritten to it.
///
/// Imports only. A trait method dispatches rather than naming a class; a
/// builtin has no class to name; and which of the rest this module defines
/// itself is not known here, since `Prelude` is compiled after this file. So a
/// module that defines its own `str`, shadows it locally and interpolates
/// there still emits the bare name — which is what every case did before.
let private resolvedSpelling (name: string) (env: Env) : string option =
    let shadowed = Map.tryFind name env.Bindings <> Map.tryFind name env.Resolved

    if not shadowed || Map.containsKey name env.Registry.TraitMethods then
        None
    else
        match Map.tryFind name env.Registry.ImportAliases with
        | Some origin when origin.OriginModule <> "" ->
            Some(Naming.qualifiedBinding origin.OriginModule origin.OriginalName)
        | _ -> None

/// Rewrites the head of a typed resolved reference to its qualified spelling,
/// where `resolvedSpelling` says there is one. `env` is the scope *before*
/// `unshadow`, which is where the local is.
let internal requalifyResolved (name: string) (env: Env) (te: TypedExpr) : TypedExpr =
    match resolvedSpelling name env with
    | None -> te
    | Some q ->
        match te.Node with
        | TIdent(n, tArgs) when n = name -> { te with Node = TIdent(q, tArgs) }
        | TApply({ Node = TIdent(n, tArgs) } as callee, args, kws) when n = name ->
            { te with Node = TApply({ callee with Node = TIdent(q, tArgs) }, args, kws) }
        | _ -> te

let internal withoutSeqElement (env: Env) : Env =
    { env with Bindings = Map.remove seqElementSlot env.Bindings }

let internal currentSeqElement (env: Env) (formName: string) (r: Range) : HMType =
    match Map.tryFind seqElementSlot env.Bindings with
    | Some binding ->
        let (Scheme(_, _, t)) = binding.Scheme
        t
    | None ->
        failwithf
            $"Type Error: '%s{formName}' only means something inside a (seq ...) body, and there is none here, at %s{Lexer.formatPos r}"

/// Checks a pattern against the type of the value it will meet, answering the
/// typed pattern and what it binds.
///
/// `inferStep` is the inferencer, passed in rather than called: a `(:view step
/// p)` holds an ordinary expression, and this is compiled well before `infer`
/// is. It is `inferChecked` at the one call site there is, so that a step
/// written as a lambda has its parameter at the scrutinee's type before its
/// body is inferred.
let rec checkPattern
    (inferStep: HMType -> Env -> Expr -> HMType * TypedExpr)
    (env: Env)
    (expectedType: HMType)
    (pat: Pattern)
    : TypedPattern * Map<string, HMType> =
    let checkPattern env expectedType pat = checkPattern inferStep env expectedType pat

    match pat with
    | PWildcard r ->
        { Type = expectedType
          Range = r
          Node = TPWildcard },
        Map.empty
    | PIdent(name, r) ->
        { Type = expectedType
          Range = r
          Node = TPIdent name },
        Map.add name expectedType Map.empty
    // A pattern is emitted as a C# constant, so its type has to be settled
    // where it is written: there is no obligation for it to carry, and nothing
    // in a `case` label a `CreateChecked` could reach a type parameter through.
    //
    // So a suffix-free literal takes the scrutinee's type when that is already
    // a number — which is what lets a `long` be matched on `5` — and `int`
    // otherwise, as it always did.
    | PInt(value, r) ->
        let inferredType =
            match NumericLiteral.spelledType value with
            | Some t -> t
            | None ->
                match prune env.Registry expectedType with
                | scrutinee when NumericLiteral.isNumeric scrutinee -> scrutinee
                | TVar _ ->
                    failwithf
                        $"Pattern Error at %s{Lexer.formatPos r}: '%s{value}' is matched against a generic type, and a number has no spelling at a type parameter. Compare it instead, or give the function a concrete type."
                | _ -> TypeConstants.intType
            |> fun t -> checkLiteralFits t value r

        unify env.Registry expectedType inferredType
        { Type = inferredType
          Range = r
          Node = TPInt value },
        Map.empty
    | PString(value, r) ->
        unify env.Registry expectedType TypeConstants.stringType

        { Type = TypeConstants.stringType
          Range = r
          Node = TPString value },
        Map.empty
    | PChar(value, r) ->
        unify env.Registry expectedType TypeConstants.charType

        { Type = TypeConstants.charType
          Range = r
          Node = TPChar value },
        Map.empty
    | PBool(value, r) ->
        unify env.Registry expectedType TypeConstants.boolType

        { Type = TypeConstants.boolType
          Range = r
          Node = TPBool value },
        Map.empty
    | PKeyword(value, r) ->
        unify env.Registry expectedType TypeConstants.keywordType

        { Type = TypeConstants.keywordType
          Range = r
          Node = TPKeyword value },
        Map.empty
    | PQuotedSymbol(value, r) ->
        unify env.Registry expectedType TypeConstants.symbolType

        { Type = TypeConstants.symbolType
          Range = r
          Node = TPSymbol value },
        Map.empty
    // Alternatives, all against the one scrutinee type — so `(case c ((#\a 1)
    // ...))` is a type error on the `1` rather than something the emitter
    // discovers.
    //
    // None may bind. A name bound down one alternative and not another has no
    // value on the arm that reads it, and C# says the same of a `case` label
    // pair whose designations differ.
    | POr(alts, r) ->
        let typedAlts =
            alts
            |> List.map (fun alt ->
                let typed, binders = checkPattern env expectedType alt

                if not (Map.isEmpty binders) then
                    let names = binders |> Map.toList |> List.map fst |> String.concat ", "

                    failwithf
                        $"Pattern Error at %s{Lexer.formatPos typed.Range}: this alternative binds %s{names}, and one of several alternatives cannot. Only one of them runs, so a name bound here has no value on the arms reached through the others."

                typed)

        { Type = expectedType
          Range = r
          Node = TPOr typedAlts },
        Map.empty

    // `(and p q ...)` — every conjunct against the one value, so every conjunct
    // is checked at the scrutinee's type and every conjunct's binders are the
    // clause's. The parser has already refused two conjuncts binding one name.
    //
    // The node's own type stays the scrutinee's even where a conjunct narrows:
    // a `(:is ...)` narrows the name it binds and nothing else, and the label
    // is emitted against the value as the `match` has it.
    | PAnd(alts, r) ->
        let mutable currentEnv = Map.empty

        let typedAlts =
            alts
            |> List.map (fun alt ->
                let typed, binders = checkPattern env expectedType alt
                currentEnv <- Map.fold (fun acc k v -> Map.add k v acc) currentEnv binders
                typed)

        { Type = expectedType
          Range = r
          Node = TPAnd typedAlts },
        currentEnv

    // `(:is Clr.Type binder)` — a .NET type test, which is how one exception is
    // told from another inside an `Err` arm.
    //
    // Both types are resolved against .NET metadata and the test has to be one
    // that could succeed. A tested type unrelated to the scrutinee is rejected
    // here rather than left to C#, where it would be an error in generated code
    // nobody wrote.
    | PTypeTest(typeName, binder, r) ->
        let where = Lexer.formatPos r
        let testedClr = DotNetInterop.resolveType $" at %s{where}" typeName
        let scrutinee = prune env.Registry expectedType

        if DotNetInterop.isUnresolved scrutinee then
            failwithf
                $"Pattern Error at %s{where}: the type of the value being matched is not known here, so ':is' has nothing to narrow. Annotate it first."

        match DotNetInterop.tryClrTypeOf scrutinee with
        | None ->
            let shown = DotNetInterop.showType scrutinee

            failwithf
                $"Pattern Error at %s{where}: ':is' tests a .NET type, but the value being matched has the Bjolang type %s{shown}. Match it with its own constructors instead."
        | Some scrutineeClr ->
            if not (scrutineeClr.IsAssignableFrom testedClr) then
                failwithf
                    $"Pattern Error at %s{where}: '%s{testedClr.FullName}' is not a '%s{scrutineeClr.FullName}', so this test can never succeed."

            // The binder sees the *narrowed* type: that is the whole point of
            // testing, and it is what lets the arm use members the scrutinee's
            // static type does not have.
            let testedType = DotNetInterop.mapClrType testedClr

            { Type = testedType
              Range = r
              Node = TPTypeTest(testedClr.FullName, binder) },
            (match binder with
             | Some n -> Map.add n testedType Map.empty
             | None -> Map.empty)

    // `(:view step p)` — the value is handed to `step` and `p` matches what
    // comes back.
    //
    // The step is inferred in `env`, which is the scope the `match` sits in:
    // the clause's own binders are established by this very pattern and cannot
    // be in it. Its binders are the inner pattern's, and the view contributes
    // none of its own.
    | PView(step, inner, r) ->
        let resultType = freshMeta ()

        // Inferred against the arrow it has to have, so that a lambda step —
        // which is what a `&` form is — reads its parameter at the scrutinee's
        // type. A trait method with an associated type, `(try-ref & key)`, has
        // nothing to resolve that type through otherwise.
        let stepType, typedStep =
            inferStep (TFun([ expectedType ], resultType, ESync)) env step

        // The call is emitted into a C# `case ... when`, which has no `await`,
        // so the arrow has to be an ordinary one. Refused here rather than left
        // to unification, which would report a mismatch of two arrows and say
        // nothing about why this one may not suspend.
        if callSuspends (prune env.Registry stepType) then
            failwithf
                $"Pattern Error at %s{Lexer.formatPos r}: a view runs inside a match guard, which cannot suspend, so its step cannot be a bjoroutine. Call it before the match and match on the result."

        unify env.Registry stepType (TFun([ expectedType ], resultType, ESync))

        let typedInner, binders = checkPattern env resultType inner

        { Type = expectedType
          Range = r
          Node = TPApp(typedStep, typedInner) },
        binders

    | PConstruct(name, args, r) ->
        // A prefixed constructor is a spelling: the typed pattern carries the
        // name the union declared, which is what codegen emits a case class for.
        let name = originalName env.Registry name

        let binding = 
            match Map.tryFind name env.Bindings with
            | Some b -> b
            | None ->
                failwithf
                    $"Pattern Error: Unknown constructor '%s{name}' at %s{Lexer.formatPos r}.%s{hiddenMemberNote env.Registry name}"

        let consType, _, _ = instantiate env.Registry binding.Scheme

        let argTypes, returnType =
            match prune env.Registry consType with
            | TFun(tArgs, ret, _) -> tArgs, prune env.Registry ret
            | _ -> [], prune env.Registry consType

        unify env.Registry expectedType returnType

        if args.Length <> argTypes.Length then
            failwithf $"Pattern Error: Constructor {name} expects {argTypes.Length} arguments but got {args.Length} at %s{Lexer.formatPos r}"

        let mutable currentEnv = Map.empty
        let typedArgs =
            List.zip argTypes args
            |> List.map (fun (expectedArgType, argPat) ->
                let tp, boundEnv = checkPattern env expectedArgType argPat
                currentEnv <- Map.fold (fun acc k v -> Map.add k v acc) currentEnv boundEnv
                tp)

        { Type = returnType
          Range = r
          Node = TPConstruct(name, typedArgs) },
        currentEnv
    | PList(items, tailOpt, r) ->
        let elemType = freshMeta ()
        let listType = TCon("List", [ elemType ])
        unify env.Registry expectedType listType
        let mutable currentEnv = Map.empty

        let typedItems =
            items
            |> List.map (fun p ->
                let tp, env = checkPattern env elemType p
                currentEnv <- Map.fold (fun acc k v -> Map.add k v acc) currentEnv env
                tp)

        let typedTail =
            tailOpt
            |> Option.map (fun p ->
                let tp, env = checkPattern env listType p
                currentEnv <- Map.fold (fun acc k v -> Map.add k v acc) currentEnv env
                tp)

        { Type = listType
          Range = r
          Node = TPList(typedItems, typedTail) },
        currentEnv
    | PVec(items, tailOpt, r) ->
        let elemType = freshMeta ()
        let vecType = TCon("Vec", [ elemType ])
        unify env.Registry expectedType vecType
        let mutable currentEnv = Map.empty

        let typedItems =
            items
            |> List.map (fun p ->
                let tp, env = checkPattern env elemType p
                currentEnv <- Map.fold (fun acc k v -> Map.add k v acc) currentEnv env
                tp)

        // A rest pattern captures the remaining elements, so it is itself a Vec.
        let typedTail =
            tailOpt
            |> Option.map (fun p ->
                let tp, env = checkPattern env vecType p
                currentEnv <- Map.fold (fun acc k v -> Map.add k v acc) currentEnv env
                tp)

        { Type = vecType
          Range = r
          Node = TPVec(typedItems, typedTail) },
        currentEnv
    | PArray(items, tailOpt, r) ->
        let elemType = freshMeta ()
        let arrayType = TCon("Array", [ elemType ])
        unify env.Registry expectedType arrayType
        let mutable currentEnv = Map.empty

        let typedItems =
            items
            |> List.map (fun p ->
                let tp, env = checkPattern env elemType p
                currentEnv <- Map.fold (fun acc k v -> Map.add k v acc) currentEnv env
                tp)

        // The remaining elements, copied out into an array of their own — a
        // slice of an array is an array, and a new one.
        let typedTail =
            tailOpt
            |> Option.map (fun p ->
                let tp, env = checkPattern env arrayType p
                currentEnv <- Map.fold (fun acc k v -> Map.add k v acc) currentEnv env
                tp)

        { Type = arrayType
          Range = r
          Node = TPArray(typedItems, typedTail) },
        currentEnv
    | PTuple(items, r) ->
        let elemTypes = items |> List.map (fun _ -> freshMeta ())
        let tupleType = TTuple elemTypes
        unify env.Registry expectedType tupleType
        let mutable currentEnv = Map.empty

        let typedItems =
            List.zip elemTypes items
            |> List.map (fun (elemType, p) ->
                let tp, boundEnv = checkPattern env elemType p
                currentEnv <- Map.fold (fun acc k v -> Map.add k v acc) currentEnv boundEnv
                tp)

        { Type = tupleType
          Range = r
          Node = TPTuple typedItems },
        currentEnv

