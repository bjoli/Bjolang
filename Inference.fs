module Bjolang.Inference

open Bjolang.Lexer
open Bjolang.Parser
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
            tref.Holes
            |> List.fold
                (fun acc hole ->
                    leafConstraints registry tref.Trait hole
                    |> List.fold (fun acc c -> Set.add c acc) acc)
                acc

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
        { TraitName = traitName; TargetType = TVar varName })

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
let private openLiterals = ResizeArray<HMType * string * Range>()

/// Drops whatever is still open. For the compilation that *failed*: see
/// `clearWanteds`, which this is the other half of.
let clearNumericLiterals () : unit = openLiterals.Clear()

/// Refuses a literal the type it has ended up at cannot hold.
let private checkLiteralFits (t: HMType) (text: string) (r: Range) : HMType =
    if not (NumericLiteral.fits t text) then
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{text}' does not fit in a '%s{DotNetInterop.showType t}'."

    t

/// The type of a numeric literal as an expression.
///
/// A suffix fixes it outright. A bare integer becomes a fresh metavariable —
/// see `openLiterals`.
let private numericLiteralType (value: string) (r: Range) : HMType =
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
let rec private metaIdsOf (t: HMType) : int list =
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
let private settleLiterals (types: HMType list) : unit =
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

let private withSeqElement (elemType: HMType) (env: Env) : Env =
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
let private unshadow (name: string) (env: Env) : Env =
    // Dispatch comes back with the binding. A trait method that something has
    // bound over is no longer dispatched on, which is the point of the rule —
    // but the compiler wrote *this* mention, and it meant the method.
    let env =
        if Map.containsKey name env.Registry.TraitMethods then
            { env with TraitMethodNames = Set.add name env.TraitMethodNames }
        else
            env

    match Map.tryFind name env.Resolved with
    | Some binding when Map.tryFind name env.Bindings <> Some binding ->
        { env with Bindings = Map.add name binding env.Bindings }
    | _ -> env

let private withoutSeqElement (env: Env) : Env =
    { env with Bindings = Map.remove seqElementSlot env.Bindings }

let private currentSeqElement (env: Env) (formName: string) (r: Range) : HMType =
    match Map.tryFind seqElementSlot env.Bindings with
    | Some binding ->
        let (Scheme(_, _, t)) = binding.Scheme
        t
    | None ->
        failwithf
            $"Type Error: '%s{formName}' only means something inside a (seq ...) body, and there is none here, at %s{Lexer.formatPos r}"

let rec checkPattern (env: Env) (expectedType: HMType) (pat: Pattern) : TypedPattern * Map<string, HMType> =
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

let private typeNameMap =
    Map.ofList [
        "int", TypeConstants.intType
        "byte", TypeConstants.byteType
        "short", TypeConstants.shortType
        "ushort", TypeConstants.ushortType
        "uint", TypeConstants.uintType
        "long", TypeConstants.longType
        "ulong", TypeConstants.ulongType
        "double", TypeConstants.doubleType
        "string", TypeConstants.stringType
        "bool", TypeConstants.boolType
        // `void` in a Bjolang signature is the *unit* type, not C#'s `void`.
        // `System.Void` is spelled here too because programs already write it,
        // and there is nothing else it could reasonably mean: the interop void
        // is not a type a program can hold, pass or return.
        "void", TypeConstants.unitType
        "Unit", TypeConstants.unitType
        "System.Void", TypeConstants.unitType
        // Lowercase, like the other primitives. `Char` is the canonical name
        // the type carries internally, but a signature spells it `char`.
        "char", TypeConstants.charType
    ]

let rec resolveTypeAnnotation (registry: TraitRegistry) (ptype: FType) : HMType =
    match ptype with
    | TName(name, _) ->
        if name.StartsWith("'") then
            TVar name
        else

        let name = originalName registry name

        match Map.tryFind name registry.Aliases with
            | Some (args, t) when args.Length = 0 -> t
            | Some (args, _) -> failwithf $"Type alias {name} expects {args.Length} arguments, but got 0"
            | None ->
                match Map.tryFind name typeNameMap with
                | Some t -> t
                | None -> TCon(name, [])
    | TApp("->", args, _) ->
        let resolvedArgs = args |> List.map (resolveTypeAnnotation registry)
        tfun (List.take (resolvedArgs.Length - 1) resolvedArgs) (List.last resolvedArgs)
    // `(-bjo-> ...)` reaching here rather than as a `TArrow` — the nested
    // positions of an arrow are read by `parseArrowTypeInner`, which builds a
    // `TApp` for every applied form. A bjoroutine-typed *parameter* is not
    // reachable from source yet, but the metadata serializer writes what the
    // type says, so it can be read.
    | TApp("-bjo->", args, _) ->
        let resolvedArgs = args |> List.map (resolveTypeAnnotation registry)
        TFun(List.take (resolvedArgs.Length - 1) resolvedArgs, List.last resolvedArgs, EAsync)
    | TArrow(mandatory, keywords, restOpt, ret, colour, _) ->
        let mandatoryTypes = mandatory |> List.map (resolveTypeAnnotation registry)
        let keywordTypes = keywords |> List.map (fun (_, t) -> resolveTypeAnnotation registry t)
        let restArrayType =
            match restOpt with
            | Some rt -> [TCon("Array", [resolveTypeAnnotation registry rt])]
            | None -> []
        let retType = resolveTypeAnnotation registry ret
        let allArgTypes = mandatoryTypes @ keywordTypes @ restArrayType
        TFun(allArgTypes, retType, colourEffect colour)
    // (assoc Trait item 'col) — an associated type projected out of an
    // implementor. Written by the export-metadata serializer rather than by
    // hand: inside a `def/trait` an associated type is named directly.
    // `(Tuple a b)` is the tuple type, not a one-off constructor named "Tuple".
    // It is also what `serializeHMType` writes for a `TTuple`, so without this
    // no exported signature mentioning a tuple could be read back.
    | TApp("Tuple", args, _) -> TTuple(args |> List.map (resolveTypeAnnotation registry))
    | TApp("assoc", [ TName(traitName, _); TName(assocName, _); implType ], _) ->
        TAssoc(traitName, assocName, resolveTypeAnnotation registry implType)
    // `(%f int)` — a type variable applied to arguments. `HMType` has no case
    // for this and deliberately never will: giving the unifier one makes it
    // higher-order. Only an inline trait's own constructor variable may be
    // written applied, and `resolveTemplate` reads those, not this function.
    //
    // Falling through to the general case turned it into a type constructor
    // literally named `'f`, which then failed much later with a confusing
    // complaint about a missing implementation for a type nobody wrote.
    // An arrow spelled with an effect this compiler does not know.
    //
    // Only reachable from module metadata written by a *newer* compiler —
    // `serializeHMType` writes `(-bjo-> ...)` for an `EAsync` arrow, and there
    // is no source syntax for one yet. Without this the name falls through to
    // the general case and becomes a type constructor literally called
    // `-bjo->`, which then fails somewhere else entirely, complaining about a
    // missing implementation for a type nobody wrote.
    | TApp(name, _, r) when name.EndsWith "->" && name <> "->" ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: the arrow %s{name} carries an effect this compiler does not understand. The module was compiled by a newer Bjolang; rebuild it, or upgrade."

    | TApp(name, _, r) when name.StartsWith "'" ->
        failwithf
            $"Kind Error at %s{Lexer.formatPos r}: the type variable %%%s{name.TrimStart('\'')} is applied to arguments here. Bjolang has no higher-kinded type variables: only the constructor variable of an inline trait may be written applied, and only inside that trait's own signatures. A function cannot be generic over a type constructor."

    | TApp(name, args, _) ->
        let name = originalName registry name
        let resolvedArgs = args |> List.map (resolveTypeAnnotation registry)
        match Map.tryFind name registry.Aliases with
        | Some (typeParams, t) ->
            if typeParams.Length <> resolvedArgs.Length then
                failwithf $"Type alias {name} expects {typeParams.Length} arguments, but got {resolvedArgs.Length}"
            let normalizeParam (p: string) = if p.StartsWith("'") then p else "'" + p
            let subst = List.zip (typeParams |> List.map normalizeParam) resolvedArgs |> Map.ofList
            substTypeVars subst t
        | None -> TCon(name, resolvedArgs)


// ---------------------------------------------------------------------------
// Inline-trait signature templates
// ---------------------------------------------------------------------------

let rec private hmToTpl (t: HMType) : TplType =
    match t with
    | TCon(n, args) -> TplCon(n, List.map hmToTpl args)
    | TVar n -> TplVar n
    | TFun(args, ret, eff) -> TplFun(List.map hmToTpl args, hmToTpl ret, eff)
    | TTuple ts -> TplTuple(List.map hmToTpl ts)
    | other ->
        failwithf $"Type error: %s{DotNetInterop.showType other} may not appear in an inline trait's signature"

/// Reads a trait signature that mentions the implementor *applied*.
///
/// The result is a `TplType`, never an `HMType`: `m` occurs at two different
/// argument lists in `bind`, and giving the unifier a case for that would make
/// it higher-order. Instantiation at an impl (see `instantiateTemplate`)
/// eliminates the hole and hands inference an ordinary first-order type.
let rec resolveTemplate (registry: TraitRegistry) (holeVar: string) (ftype: FType) : TplType =
    let go = resolveTemplate registry holeVar
    let holeName = "'" + holeVar

    match ftype with
    | TName(name, r) when name = holeName ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: the constructor variable %%%s{holeVar} must be written applied, as (%%%s{holeVar} ...)."
    | TName _ -> hmToTpl (resolveTypeAnnotation registry ftype)
    | TApp("->", args, _) ->
        let resolved = args |> List.map go
        TplFun(List.take (resolved.Length - 1) resolved, List.last resolved, ESync)
    | TArrow(mandatory, keywords, restOpt, ret, colour, r) ->
        if colour <> Ordinary then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: an inline trait's methods cannot be bjoroutines — a trait signature has no way to say that calling a method suspends."
        if not keywords.IsEmpty || restOpt.IsSome then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: an inline trait's methods may not take keyword or rest parameters."
        TplFun(mandatory |> List.map go, go ret, ESync)
    | TApp("Tuple", args, _) -> TplTuple(args |> List.map go)
    | TApp(name, args, _) when name = holeName -> TplHole(args |> List.map go)
    | TApp(name, args, _) when name.StartsWith "'" ->
        failwithf
            $"Type Error: only the trait's own constructor variable may be applied in a signature; %s{name} is an ordinary type variable."
    | TApp(name, args, _) ->
        match Map.tryFind name registry.Aliases with
        | Some _ ->
            // An alias may expand into anything, including something that hides
            // the hole. Resolving it as an ordinary type is only sound when no
            // argument mentions the hole.
            hmToTpl (resolveTypeAnnotation registry ftype)
        | None -> TplCon(name, args |> List.map go)

/// Instantiates a template for one call site: every ordinary type variable
/// becomes a fresh meta, and every *occurrence* of the hole becomes a meta of
/// its own together with the arguments it was applied to.
///
/// The hole metas are shared with the surrounding expression, which is the whole
/// point: the call node is fully typed immediately, with the constructor still
/// unknown, and whatever later pins one of them — an argument, an enclosing
/// `bind`, a declared return type — pins the constructor.
let instantiateTemplateFresh (tpl: TplType) : HMType * (HMType * HMType list) list =
    let varMap = System.Collections.Generic.Dictionary<string, HMType>()
    let holes = ResizeArray<HMType * HMType list>()

    let rec go t =
        match t with
        | TplVar n ->
            match varMap.TryGetValue n with
            | true, m -> m
            | _ ->
                let m = freshMeta ()
                varMap[n] <- m
                m
        | TplCon(n, args) -> TCon(n, List.map go args)
        | TplFun(args, ret, eff) -> TFun(List.map go args, go ret, eff)
        | TplTuple ts -> TTuple(List.map go ts)
        | TplHole args ->
            let argTypes = List.map go args
            let m = freshMeta ()
            holes.Add(m, argTypes)
            m

    let t = go tpl
    t, List.ofSeq holes

// ---------------------------------------------------------------------------
// Deferred trait resolution
// ---------------------------------------------------------------------------

/// A trait obligation raised by a call and discharged later.
///
/// `bind` resolves from its first argument, but `pure : 'a -> m 'a` mentions the
/// constructor only in its *result*, so any rule that reads argument zero cannot
/// see it at all. Rather than make `infer` bidirectional, the obligation is
/// simply recorded and revisited once the surrounding expression has had its say.
type Wanted =
    { Trait: string
      Method: string
      Kind: TraitKind
      /// One entry per occurrence of the hole: the meta standing for the
      /// constructor application, and the arguments it was applied to.
      HoleArgs: (HMType * HMType list) list
      /// The AST node that reads the answer back.
      Ref: TraitRef
      Range: Range }

let private wantedQueue = ResizeArray<Wanted>()

/// The holes an unresolved obligation of `kind` is still watching.
///
/// For `InlineTrait`: a local helper written without a signature —
/// `(defun (bump fa) (fmap fa inc))` — is let-polymorphic, so its parameter's
/// metavariable used to be quantified the moment the binding was finished, long
/// before any call site said what it was. Resolution then found a rigid type
/// variable and reported that an inline-only trait cannot be used generically,
/// which was true of the type it had just been given and false of the program
/// that was written. Holding these back makes such a binding monomorphic, which
/// is the only thing it can honestly be: one use site, one constructor,
/// resolved and inlined.
///
/// For `InterfaceTrait`: held back for local bindings only. At the top level
/// such an obligation is exactly the generic-receiver case the dictionary path
/// handles, and holding it there would turn every constrained generic function
/// monomorphic — but a top-level binding drains the queue before it generalizes
/// anyway, so the two never meet.
let private heldWanteds (kind: TraitKind) () : Set<int> =
    wantedQueue
    |> Seq.filter (fun w -> w.Kind = kind && w.Ref.Resolved.IsNone)
    |> Seq.collect (fun w -> w.HoleArgs |> Seq.collect (fun (m, _) -> metaIdsOf m))
    |> Set.ofSeq

/// The metavariables an unsettled numeric literal is still watching.
///
/// Generalizing one would quantify a variable `defaultNumericLiterals` is about
/// to answer, and the answer would arrive at a type parameter nothing
/// instantiates: `(let ((n 5)) ...)` generalized to `forall t. t`, and codegen
/// then emitted the digits `5` for a local declared at that parameter. Held
/// back, such a binding is monomorphic — the only thing it can be, a literal
/// having exactly one type in the code that comes out.
let private heldLiterals () : Set<int> =
    openLiterals |> Seq.collect (fun (t, _, _) -> metaIdsOf t) |> Set.ofSeq

do Unification.heldMetaIds <- fun () -> Set.union (heldWanteds InlineTrait ()) (heldLiterals ())
do Unification.heldLocalMetaIds <- heldWanteds InterfaceTrait

let private pushWanted (w: Wanted) = wantedQueue.Add w

/// Detaches everything raised so far. Callers solve what they take.
let takeWanteds () : Wanted list =
    let ws = List.ofSeq wantedQueue
    wantedQueue.Clear()
    ws

/// Drops whatever is still queued.
///
/// A successful `checkProgram` leaves the queue empty — `solvePending` is what
/// empties it — so this is for the compilation that *failed*. An obligation
/// raised before the exception is still in the queue, and the next compilation
/// in the same process would try to solve it against an environment it was
/// never about. That is a diagnostic pointing at another file entirely, which
/// is the worst shape a state leak can take.
let clearWanteds () : unit = wantedQueue.Clear()

/// Instantiates an impl's target pattern, giving fresh metas to the impl's own
/// prefix variables.
///
/// Returns the prefix to unify the hole against, and — separately — the metas
/// standing for the class's *type parameters*. The two are not the same list:
/// `impl Show for (List int)` has a one-argument prefix and no type parameters
/// at all, and naming `Show_List<int>` for it is a type error in C#.
let private instantiateImplPrefix (target: ImplTarget) : HMType list * HMType list =
    let vars = target.FixedPrefix |> List.collect typeVarsOf |> List.distinct
    let subst = vars |> List.map (fun v -> v, freshMeta ()) |> Map.ofList
    let prefix = target.FixedPrefix |> List.map (substTypeVars subst)
    prefix, vars |> List.map (fun v -> subst[v])

let private tryResolveWanted (env: Env) (w: Wanted) : bool =
    if w.Ref.Resolved.IsSome then
        true
    else

    let registry = env.Registry

    // A trait that stands for a .NET interface is never resolved to an
    // implementation, and the lookup below is not merely pointless for it but
    // wrong: `ImplTargets` is keyed by trait *name*, so a local `Num` would
    // otherwise pick up the impls of an imported trait that happens to share
    // the name, and dispatch to a class that knows nothing about it.
    //
    // Left unresolved on purpose. `TraitInline` passes an unresolved call
    // through untouched and `Lowering` turns it into the member call, which is
    // the same emission at a concrete implementor as at a generic one — so
    // there is nothing here for resolution to decide.
    if (match Map.tryFind w.Trait registry.Traits with
        | Some info -> info.ClrConstraint.IsSome
        | None -> false) then
        false
    else

    let ctorOpt =
        w.HoleArgs
        |> List.tryPick (fun (m, _) ->
            match prune registry m with
            | TCon(ctor, _) -> Some ctor
            | TTuple args -> Some(tupleCtor args.Length)
            | _ -> None)

    match ctorOpt with
    // A hole that is still a metavariable, or a rigid `TVar`, never reaches a
    // lookup at all: it falls through to the dictionary path. That is what makes
    // the blanket fallback below safe — see the comment on it.
    | None -> false
    | Some ctor ->
        match Map.tryFind (w.Trait, ctor) registry.ImplTargets with
        | None ->
            // Level two: the blanket, if the trait has one.
            //
            // Nothing is unified here, and nothing should be. A blanket imposes
            // no structure on the implementor — that is what makes it a blanket
            // — so there is no prefix to match and the hole is left exactly as
            // it was found.
            //
            // The soundness argument is that this branch is only ever reached
            // with `ctor` in hand, which means the implementor is *ground*. A
            // type variable became a dictionary parameter further up and gets
            // filled in at the concrete instantiation site, where this same
            // choice is made again with the same answer. So a blanket can
            // legitimately differ in behaviour from a specific impl — dropping
            // a value and detaching a promise are not the same act — without
            // generic code ever baking in the wrong one.
            match Map.tryFind w.Trait registry.BlanketImpls with
            | Some _ ->
                let hole =
                    match w.HoleArgs with
                    | (m, _) :: _ -> prune registry m
                    | [] ->
                        failwithf
                            $"Internal error: trait obligation for '%s{w.Trait}' has no implementor at %s{Lexer.formatPos w.Range}"

                w.Ref.Resolved <- Some(BlanketCtor, [ hole ])
                true
            | None ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos w.Range}: no implementation of trait '%s{w.Trait}' for '%s{Naming.showTypeName ctor}', required by '%s{w.Method}'."
        | Some target ->
            let prefix, classTypeArgs = instantiateImplPrefix target

            for (m, occArgs) in w.HoleArgs do
                unify registry m (implTargetType ctor (prefix @ occArgs))

            w.Ref.Resolved <- Some(ctor, classTypeArgs |> List.map (prune registry))
            true

/// Runs the wanted queue to a fixpoint, then reports what is left.
///
/// An unsolved `InterfaceTrait` obligation is not an error: it is exactly the
/// generic-receiver case the dictionary path already handles, and leaving it
/// alone is what keeps the current semantics intact.
let solveWanteds (env: Env) (wanteds: Wanted list) : unit =
    let mutable pending = wanteds |> List.filter (fun w -> w.Ref.Resolved.IsNone)
    let mutable progress = true

    while progress && not pending.IsEmpty do
        progress <- false

        pending <-
            pending
            |> List.filter (fun w ->
                if tryResolveWanted env w then
                    progress <- true
                    false
                else
                    true)

    for w in pending do
        match w.Kind with
        | InterfaceTrait -> ()
        | InlineTrait ->
            let holes = w.HoleArgs |> List.map (fst >> prune env.Registry)

            if holes |> List.exists (function TVar _ -> true | _ -> false) then
                failwithf
                    $"Type Error at %s{Lexer.formatPos w.Range}: '%s{w.Method}' cannot be used at a generic type; '%s{w.Trait}' is an inline-only trait, so there is no dictionary to pass. Give the call a concrete type, or make the caller monomorphic."
            else
                failwithf
                    $"Type Error at %s{Lexer.formatPos w.Range}: cannot determine which '%s{w.Trait}' instance '%s{w.Method}' uses here; add a type annotation. Nothing in this expression says what the constructor is — a `(do ...)` block with no `:bind` never mentions one."

/// Settles every numeric literal that nothing pinned down, and holds the rest
/// to being numbers.
///
/// The check has to be here rather than left to unification: a bare literal is
/// a metavariable, so `(: greeting string) (def greeting 5)` unified rather
/// than failing, and the mismatch surfaced as C# that would not compile.
let private defaultNumericLiterals (env: Env) : unit =
    for (t, text, r) in openLiterals do
        match prune env.Registry t with
        // Nothing said what it is, so it is an `int` — which is what a literal
        // written without a suffix has always been.
        | TMeta m -> m.Value <- Some TypeConstants.intType
        // A type variable. The literal is emitted through the implementor's own
        // `CreateChecked`, and the `Num` that `collectTraitConstraints` reads
        // off it is what makes that legal.
        | TVar _ -> ()
        | numeric when NumericLiteral.isNumeric numeric -> checkLiteralFits numeric text r |> ignore
        | other ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: these types do not match. '%s{text}' is a number, and it is being used where a '%s{DotNetInterop.showType other}' is wanted."

    openLiterals.Clear()

/// Solves everything raised since the last call. Used at every point that is
/// about to generalize, since a scheme must not be built over a constructor
/// that resolution would still have pinned down.
///
/// Literals first. A trait obligation dispatches on its implementor, and one
/// that is still an open literal — `(= 1 2)` — resolves to nothing at all, so
/// the queue has to be drained after the numbers have said what they are.
let solvePending (env: Env) : unit =
    defaultNumericLiterals env
    solveWanteds env (takeWanteds ())

/// Reads an impl's target as a pattern.
///
/// The trait's constructor variable abstracts over the *trailing* `HoleArity`
/// arguments; everything before them is fixed by this impl.
let implTargetOf
    (traitName: string)
    (info: TraitInfo)
    (targetType: HMType)
    (constraints: TraitConstraint list)
    (r: Range)
    : ImplTarget =
    match targetType with
    | TCon(ctor, args) ->
        if args.Length < info.HoleArity then
            failwithf
                $"Kind Error at %s{Lexer.formatPos r}: trait '%s{traitName}' abstracts over the last %d{info.HoleArity} argument(s) of its implementor, but '%s{ctor}' is applied to only %d{args.Length}. A constructor whose abstracted argument is not last — `Either e` in the first position — needs a newtype that flips them."

        { Ctor = ctor
          FixedPrefix = args |> List.take (args.Length - info.HoleArity)
          HoleArity = info.HoleArity
          Constraints = constraints }

    // A blanket: `(def/impl (Discard %a) ...)`, which applies wherever the exact
    // head has no impl of its own. The implementor is the class's one type
    // parameter, so the prefix is the variable itself.
    //
    // Only for a first-order trait. A blanket over a trait that abstracts a
    // *constructor* would have to be written `(%f %a)` — a type variable
    // applied — which `HMType` has no case for and deliberately never will.
    | TVar _ ->
        if info.HoleArity > 0 then
            failwithf
                $"Kind Error at %s{Lexer.formatPos r}: trait '%s{traitName}' abstracts over the last %d{info.HoleArity} argument(s) of its implementor, so it cannot have a blanket implementation. A blanket names a bare type variable, and there is nothing to apply it to."

        // A blanket may not be conditional, and this is not a limitation of the
        // implementation. Its target *is* the implementor, so `(where (C %a))`
        // over it would ask a type to satisfy a constraint in order to satisfy
        // the constraint — evidence with no bottom. A conditional impl is
        // written at a constructor, where the demand lands on a smaller type.
        if not constraints.IsEmpty then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: a blanket implementation of '%s{traitName}' cannot have a where clause. Its target is the implementor itself, so the constraint would be discharged at the very type it is being proved for."

        { Ctor = BlanketCtor
          FixedPrefix = [ targetType ]
          HoleArity = 0
          Constraints = [] }

    // A tuple, under its synthetic arity key. Nothing abstracts over a tuple's
    // trailing arguments — there is no constructor to apply — so the whole of
    // it is the fixed prefix and only a first-order trait can be implemented
    // for one.
    | TTuple args ->
        if info.HoleArity > 0 then
            failwithf
                $"Kind Error at %s{Lexer.formatPos r}: trait '%s{traitName}' abstracts over the last %d{info.HoleArity} argument(s) of its implementor, and a tuple has no constructor for it to abstract over."

        { Ctor = tupleCtor args.Length
          FixedPrefix = args
          HoleArity = 0
          Constraints = constraints }

    | _ -> failwithf $"Trait implementations require concrete target types at %s{Lexer.formatPos r}"

/// Instantiates a trait method at a call site and records the obligation.
let private traitCallType (env: Env) (traitName: string) (methodName: string) (r: Range) : HMType * TraitRef =
    let info = Map.find traitName env.Registry.Traits

    let methodType, holeArgs =
        match info.Kind with
        | InlineTrait ->
            match Map.tryFind methodName info.Templates with
            | Some tpl -> instantiateTemplateFresh tpl
            | None -> failwithf $"Internal error: '%s{methodName}' is not a method of inline trait '%s{traitName}'"
        | InterfaceTrait ->
            // Instantiated from the trait's own signature rather than from
            // whatever `methodName` happens to be bound to. Inside an `impl`
            // the method is also bound monomorphically, for recursion, and that
            // binding quantifies nothing — so reading the implementor out of a
            // scheme's type arguments found no hole at all for a self-call.
            let sigType =
                match Map.tryFind methodName info.Signatures with
                | Some t -> t
                | None -> failwithf $"Internal error: '%s{methodName}' is not a method of trait '%s{traitName}'"

            let implVar = "'" + info.ImplementorVar

            // An associated type is a projection out of the implementor, so it
            // is pinned by the same meta rather than being free on its own.
            let assocSubst =
                info.AssociatedTypes
                |> List.map (fun a -> "'" + a, TAssoc(traitName, a, TVar implVar))
                |> Map.ofList

            let withAssoc = substTypeVars assocSubst sigType

            let vars =
                implVar :: (freeTVars env.Registry withAssoc |> List.distinct |> List.filter ((<>) implVar))

            let subst = vars |> List.map (fun v -> v, freshMeta ()) |> Map.ofList

            // An implementor of arity zero is the hole, applied to nothing.
            substTypeVars subst withAssoc, [ subst[implVar], [] ]

    let tref =
        { Trait = traitName
          Method = methodName
          Holes = holeArgs |> List.map fst
          Resolved = None }

    pushWanted
        { Trait = traitName
          Method = methodName
          Kind = info.Kind
          HoleArgs = holeArgs
          Ref = tref
          Range = r }

    methodType, tref

/// Instantiates a record type with fresh type variables.
///
/// The record type and its field types have to be instantiated under the *same*
/// substitution, or a field's type variable would be unrelated to the one in the
/// record type it came from. Returns the instantiated record type, the declared
/// fields as written, and the field types under that substitution.
let private instantiateRecord
    (registry: TraitRegistry)
    (recordTypeName: string)
    : HMType * (string * HMType) list * Map<string, HMType> =

    let tArgs, expectedFields = Map.find recordTypeName registry.Records

    // The names are used exactly as they were registered, leading quote and
    // all. Trimming it here bound the scheme over `a` while the field types
    // resolved to `'a`, so the substitution matched nothing and a generic
    // record's fields came back still holding the declaration's own variables.
    let recordScheme = Scheme(tArgs, [], TCon(recordTypeName, tArgs |> List.map TVar))

    let instantiatedRecordType, freshVars, _ = instantiate registry recordScheme
    let fieldSubst = List.zip tArgs freshVars |> Map.ofList

    let expectedFieldsInstantiated =
        expectedFields |> List.map (fun (n, t) -> n, substTypeVars fieldSubst t) |> Map.ofList

    instantiatedRecordType, expectedFields, expectedFieldsInstantiated

/// The `#:mutable` fields of a record type, or `[]` for one that has none —
/// which includes every type that is not a record at all.
let mutableFieldsOf (registry: TraitRegistry) (recordTypeName: string) : string list =
    Map.tryFind recordTypeName registry.MutableRecordFields |> Option.defaultValue []

/// Whether `moduleName` is the module that declared the record keyed
/// `recordTypeName`, and so the only one that may write its fields.
///
/// Read off the key rather than recorded beside it: a type's key *is* its
/// declaring module and its name collapsed into one string, and `Naming.typeKey`
/// is idempotent for a name that already carries this module's prefix. So a key
/// this module built comes back unchanged and any other key grows a second
/// prefix, which is exactly the question being asked. Nothing has to guess where
/// the key divides, which `typeKeyParts` would have to.
let private declaredHere (moduleName: string) (recordTypeName: string) : bool =
    Naming.typeKey moduleName recordTypeName = recordTypeName

/// Which record type a `record-ref` or `record-set` is talking about.
///
/// The target's own type answers that whenever it is known — which is every
/// place the value was constructed, annotated, or already unified with a record
/// somewhere upstream. Only in a genuinely generic context, where the target is
/// still an unresolved meta variable, is there nothing to go on, and the field
/// name is consulted instead. That fallback is a guess, so it is only allowed to
/// stand when exactly one record type declares the name: silently picking one of
/// several is how a field name shared by two records used to make one of them
/// unreachable.
let private recordTypeOfField
    (registry: TraitRegistry)
    (targetType: HMType)
    (field: string)
    (r: Range)
    : string =

    match prune registry targetType with
    | TCon(name, _) when Map.containsKey name registry.Records -> name
    // Answered here rather than by the fallback below, which would go looking
    // for another owner of the field name and report that none has it. The type
    // is known; what is missing is its fields, and they are missing on purpose.
    | TCon(name, _) when Set.contains name registry.OpaqueTypes ->
        failwithf
            $"Type Error at %s{formatPos r}: '%s{field}' cannot be read here.%s{opaqueTypeNote registry name}"
    | _ ->
        match Map.tryFind field registry.RecordFields |> Option.defaultValue [] with
        | [ only ] -> only
        | [] ->
            failwithf
                $"Type Error at %s{formatPos r}: no record or struct type has a field named '%s{field}'.%s{hiddenMemberNote registry field}"
        | many ->
            let owners = String.concat ", " many

            failwithf
                $"Type Error at %s{formatPos r}: '%s{field}' is a field of %s{owners}, and the type of the value here is not known yet. Annotate it, or give the enclosing function a signature."


/// A syntactic value, in the sense the value restriction means it.
///
/// Only these may be generalized. Generalizing anything else is unsound the
/// moment the language has a mutable cell — and it has one already, in `Array`:
///
///     (def c (make-array 1))            ;; if this were ∀a. (Array a)
///     (array-set! c 0 42)               ;; a := int
///     (string-length (array-ref c 0))   ;; a := string, same array
///
/// Both lines check, and an int is read as a string. An application is the shape
/// that can allocate such a cell, so an application is not a value however
/// innocent it looks. The recursion matters as much as the cases: a tuple or a
/// record is a value only when everything in it is, so a box nested inside one
/// is refused along with it.
///
/// A record with a `#:mutable` field is the `make-array` case arriving by a
/// second route, and is refused for the same reason — constructing one
/// allocates a cell, however syntactic the construction looks:
///
///     (type (: (Box %a) (Record (: item %a #:mutable))))
///     (def b (Box (item Nil)))         ;; if this were ∀a. (Box a)
///     (box-set! b 1)                   ;; a := int
///     (string-length (record-ref b item))  ;; a := string, same box
///
/// The registry is needed rather than the node alone because the node carries
/// the field *values*, not the declaration that says which of them are cells.
let rec isSyntacticValue (registry: TraitRegistry) (expr: TypedExpr) =
    let recur = isSyntacticValue registry

    match expr.Node with
    | TInt _
    | TString _
    | TKeyword _
    | TSymbol _
    | TLambda(_, _)
    | TIdent(_, _) -> true
    | TTupleMake es -> List.forall recur es
    | TListMake es -> List.forall recur es
    | TVecMake es -> List.forall recur es
    | TRecordMake fields ->
        let hasMutableField =
            match prune registry expr.Type with
            | TCon(name, _) -> not (mutableFieldsOf registry name).IsEmpty
            | _ -> false

        not hasMutableField && fields |> List.forall (snd >> recur)
    | _ -> false

// ---------------------------------------------------------------------------
// Foreign .NET interop
// ---------------------------------------------------------------------------

/// The type of a foreign call once its declared exceptions are accounted for.
///
/// Without `#:exceptions` the call has the type the .NET member has, and
/// anything it throws propagates. With it, the call cannot fail *in the ways
/// that were listed* — those become `Err` — and everything else still
/// propagates. That asymmetry is the whole design: an exception nobody named is
/// a bug, not a value.
///
/// `void` is replaced by the unit tuple on the way in. C# has no `Result<E,
/// void>` and never will, and `()` is exactly what Bjolang means by a call
/// performed for its effect.
let wrapForeignExceptions (exceptions: string list) (retType: HMType) : HMType =
    if List.isEmpty exceptions then
        retType
    else
        let okType =
            if retType = TypeConstants.voidType then TTuple [] else retType

        TCon("Result", [ TCon("System.Exception", []); okType ])

/// Checks that everything named by `#:exceptions` is in fact an exception type.
///
/// The list drives a C# `catch ... when (ex is E1 || ...)` filter, and a name
/// that is not an exception type produces C# that does not compile — reported
/// against generated code rather than against the import that caused it.
let private checkExceptionTypes (where: string) (exceptions: string list) : unit =
    for name in exceptions do
        let t = DotNetInterop.resolveType $" at %s{where}" name

        if not (typeof<System.Exception>.IsAssignableFrom t) then
            failwithf
                $"Type Error at %s{where}: '%s{name}' is named in #:exceptions but does not derive from System.Exception."

/// The .NET type a receiver expression has, or a diagnostic saying why not.
let private receiverClrType (where: string) (form: string) (targetType: HMType) : System.Type =
    // `(.ToString 5)` — a receiver is a place a type has to be concrete now.
    settleLiterals [ targetType ]

    if DotNetInterop.isUnresolved targetType then
        failwithf
            $"Type Error at %s{where}: the type of the receiver of '%s{form}' is not known here. A .NET member is resolved at compile time, so the receiver's type has to be pinned down first — annotate it, or bind it with a signature."

    match DotNetInterop.tryClrTypeOf targetType with
    | Some t -> t
    | None ->
        let shown = DotNetInterop.showType targetType

        failwithf
            $"Type Error at %s{where}: '%s{form}' needs a .NET receiver, but its target has the Bjolang type %s{shown}, which is not a .NET class."

/// Unifies a foreign member's parameter types into the argument types.
///
/// This is what makes reflection *drive* inference rather than merely check it:
/// an argument whose type was still open is pinned to the parameter type of the
/// overload that was selected.
///
/// Only sound where every argument is expected to match a parameter *exactly*,
/// which is true of the one caller left: a declared `import/extern` signature,
/// checked against the overload reflection chose for it. A call site goes
/// through `reconcileForeignArgs` instead, an argument there being allowed to
/// fit by conversion.
let private unifyForeignArgs (registry: TraitRegistry) (argTypes: HMType list) (paramTypes: HMType list) =
    List.iter2 (unify registry) argTypes paramTypes

/// Reconciles the arguments of a foreign call with the parameters of the
/// overload that was selected for it, one argument at a time.
///
/// `DotNetInterop.scoreArgument` accepts an argument that fits by an implicit
/// conversion — a numeric widening, a reference upcast, a box — and ranks it
/// below an exact match so that an exact one always wins. Unifying every
/// argument against its parameter afterwards threw that away: unification is
/// nominal equality, Bjolang having no subtyping, so an `int` argument reaching
/// a `double` parameter failed with a diagnostic naming a type the caller never
/// wrote. Widenings were selectable and then unusable, and `(sin 1)` was an
/// error while `(sin 1.0)` was not.
///
/// So the two cases are now told apart:
///
///   * An argument whose type is still **open** is unified, exactly as before.
///     That is the case the docstring above describes and the one that lets
///     reflection settle a type inference has not.
///
///   * An argument whose type is **known** is left alone, and converted where
///     it differs. Nothing is being weakened: the overload was chosen because
///     the argument fits by C#'s own conversion rules, and one that fits no
///     way at all was rejected as a candidate before ever getting here.
///
/// The conversion is *written into the tree* rather than left to the C# that
/// eventually reads it. It has to be. The code generator emits a foreign call
/// as its receiver, its name and its arguments, so C# resolves the overload a
/// second time from what it is given — and a call this pass typed by one
/// overload's return type must not be free to land on another's. `((double)(x))`
/// pins it to the method that was actually chosen.
let private reconcileForeignArgs
    (registry: TraitRegistry)
    (typedArgs: TypedExpr list)
    (paramTypes: HMType list)
    : TypedExpr list =
    List.map2
        (fun (arg: TypedExpr) paramType ->
            let argType = prune registry arg.Type
            let paramType = prune registry paramType

            if not (List.isEmpty (freeVars registry argType)) then
                unify registry argType paramType
                arg
            elif argType = paramType then
                arg
            else
                { arg with
                    Type = paramType
                    Node = TCast(arg, paramType) })
        typedArgs
        paramTypes

let private metadataOf (resolved: DotNetInterop.ResolvedCall) (exceptions: string list) : DotNetMethodMetadata =
    { DeclaringType = resolved.DeclaringType
      MethodName = resolved.Name
      ParameterTypes = resolved.ParameterTypes
      ReturnType = resolved.ReturnType
      // Overload resolution by argument type only ever selects a *constructed*
      // method, so there is nothing to write between angle brackets. A generic
      // import fills this in from its declared signature instead — see
      // `instantiateGenericExtern`.
      TypeArguments = []
      IsStatic = resolved.IsStatic
      Exceptions = exceptions
      // Ordinary calls, which are all of them but an `#:async` import's. The
      // async path builds on this and overrides both.
      Await = false
      AmbientToken = false }

/// One use of a generic extern alias: its parameter types, its return type and
/// its .NET type arguments, all instantiated at fresh metavariables.
///
/// *All* — that is the point of packing them into one type before instantiating.
/// The type arguments were solved at the import in terms of the signature's own
/// variables, so instantiating the two apart would hand the call one set of
/// metavariables for the arguments to settle and a different set to write
/// between the angle brackets. Packed, `%a` is one variable, the argument that
/// pins it pins the bracket too, and there is nothing left for C# to infer.
///
/// The receiver is included in the parameters, exactly as the signature writes
/// it: an instance member's alias is a function of its receiver.
let private instantiateGenericExtern (registry: TraitRegistry) (where: string) (info: ClrExternInfo) =
    let declared =
        match info.DeclaredType with
        | Some t -> t
        | None ->
            failwithf
                $"Type Error at %s{where}: '%s{info.Alias}' names the generic method '%s{info.ClrType}.%s{info.MemberName}' and has no declared signature. A generic method's type arguments come from the signature, so it is the one kind of import that cannot do without one."

    let typeArgs = Option.defaultValue [] info.GenericTypeArgs
    let packed = TTuple(declared :: typeArgs)
    let vars = freeTVars registry packed |> List.distinct
    let instantiated, _, _ = instantiate registry (Scheme(vars, [], packed))

    match instantiated with
    | TTuple(TFun(paramTypes, retType, _) :: instantiatedArgs) -> paramTypes, retType, instantiatedArgs
    | _ ->
        failwithf
            $"Type Error at %s{where}: the declared signature of '%s{info.Alias}' is not a function type, so it cannot name a method."

/// What an `#:async` import's call resolves to.
///
/// Three things happen here that do not happen for an ordinary foreign call,
/// and all three are §7.2's rules rather than conveniences.
///
///   * **The token is threaded.** Nearly every async BCL method takes a
///     `CancellationToken` as a trailing parameter, so the overload is chosen
///     *with* one appended and the emitter fills it from the ambient token.
///     The alternative — make every caller pass it — is the parameter-through-
///     every-signature problem `current-cancel` exists to avoid, and a token
///     nobody remembers to pass is a `choose` that leaks work.
///
///   * **A method with no token overload has to say so.** `#:uncancellable` is
///     required rather than inferred, because "this cannot be stopped" is a
///     fact about a call that its reader needs and its writer knows.
///
///   * **The task is unwrapped.** `Task` is never a Bjolang type; the binding's
///     type is the task's result. There is nothing in the language that could
///     hold a `Task<T>` usefully — no `await` to spell, since suspension is
///     invisible at the call site.
///
/// The returned parameter list is what the *caller* wrote, with any threaded
/// token dropped: it is what the arguments are reconciled against and what a
/// declared signature is checked against.
/// Resolves an extern method call, on whichever half of the type it lives in.
///
/// Every caller below works in the method's *own* parameters: an instance
/// member's receiver has already been taken off the front, because it is the
/// alias's first argument and not one of the method's.
let private resolveExternMethod
    (where: string)
    (info: ClrExternInfo)
    (clrType: System.Type)
    (argTypes: HMType list)
    : DotNetInterop.ResolvedCall =
    settleLiterals argTypes
    DotNetInterop.resolveMethod where (not info.IsInstance) clrType info.MemberName argTypes

/// Resolve an extern call that threads the ambient cancellation token.
///
/// Shared by `#:async` and `#:cancellable`, which want the same overload
/// selection and differ only in what happens to the return type. The token is
/// appended to the argument types so that the token-taking overload is the one
/// selected; the caller's own arguments are the prefix.
let private resolveTokenThreadedExtern
    (where: string)
    (info: ClrExternInfo)
    (clrType: System.Type)
    (argTypes: HMType list)
    : DotNetInterop.ResolvedCall * bool =

    let wantsToken = not info.Uncancellable

    if
        wantsToken
        && DotNetInterop.hasTokenOverload (not info.IsInstance) clrType info.MemberName (Some(argTypes.Length + 1))
    then
        resolveExternMethod where info clrType (argTypes @ [ DotNetInterop.cancellationTokenType ]), true
    elif wantsToken && info.IsAsync then
        failwithf
            $"Type Error at %s{where}: '%s{info.ClrType}.%s{info.MemberName}' has no overload taking these %d{argTypes.Length} argument(s) and a System.Threading.CancellationToken, so the ambient cancellation token cannot be passed to it.\n  An async call that cannot be cancelled outlives the scope that asked for it: a losing choose stops listening, and the work carries on. If that is genuinely the case here, write #:uncancellable in the import/extern clause so that the fact is visible where it is decided."
    elif wantsToken then
        failwithf
            $"Type Error at %s{where}: '%s{info.ClrType}.%s{info.MemberName}' is imported #:cancellable, but it has no overload taking these %d{argTypes.Length} argument(s) and a System.Threading.CancellationToken. Leave #:cancellable off — the call takes no token and there is nothing to thread."
    else
        resolveExternMethod where info clrType argTypes, false

let private resolveAsyncExtern
    (where: string)
    (info: ClrExternInfo)
    (clrType: System.Type)
    (argTypes: HMType list)
    : DotNetInterop.ResolvedCall * HMType list * HMType * bool =

    let resolved, threadsToken = resolveTokenThreadedExtern where info clrType argTypes

    let awaited =
        match DotNetInterop.awaitedResultType resolved.RawReturnType with
        | Some t -> t
        | None ->
            failwithf
                $"Type Error at %s{where}: '%s{info.ClrType}.%s{info.MemberName}' is imported #:async, but the overload selected here returns %s{resolved.RawReturnType.Name}, which is not a Task, a ValueTask or either of their generic forms. Leave #:async off to call it directly."

    let visibleParams =
        if threadsToken then
            resolved.ParameterTypes |> List.truncate (resolved.ParameterTypes.Length - 1)
        else
            resolved.ParameterTypes

    resolved, visibleParams, awaited, threadsToken

/// The effect the arrow demanded at a call site must carry.
///
/// The demand takes the callee's own effect rather than always `ESync`.
/// Otherwise every call to a bjoroutine would fail in `unifyEffect`, including
/// the legal ones — inference would be answering a question it cannot see the
/// answer to. *Whether* the yield point is somewhere it can be resumed from is
/// `ColourCheck`'s, after loop lowering has decided which bodies become members
/// of their own.
///
/// A callee whose type is still a metavariable is `ESync`: nothing has said it
/// is a bjoroutine, and unification will pin it as an ordinary arrow. That is
/// the right default, because the only way to *become* a bjoroutine is to be
/// written as one.
let private demandedEffect (env: Env) (targetType: HMType) : Effect =
    match prune env.Registry targetType with
    | TFun(_, _, eff) -> eff
    | _ -> ESync

// ---------------------------------------------------------------------------
// Literal elaboration
//
// A literal written where a union is expected is elaborated into a constructor
// of that union: `'(pipe (ls "-l"))` at `(List ProcItem)` becomes
// `(list (ProcSym 'pipe) (ProcSub (list (ProcSym 'ls) (ProcStr "-l"))))`.
//
// Which constructor is chosen by the literal's *shape*, not by its type, and
// the difference is the whole point: a nested `(ls "-l")` has no type to be
// chosen by. Inferring it on its own allocates one element metavariable and
// unifies `Symbol` against `string`, which fails before any constructor is
// consulted. The shape is available before any of that happens.
// ---------------------------------------------------------------------------

/// The payload head constructors a literal of this shape could be injected
/// into, or `None` for an expression that has no literal shape.
///
/// `None` is the ordinary case and keeps the type-directed path: an unquoted
/// `,my-rot-13` is a `(-> string string)` and is selected by that type, having
/// no shape a reader could select it by.
let private literalPayloadHeads (expr: Expr) : string list option =
    match expr with
    | EList _ -> Some [ "List" ]
    | EVec _ -> Some [ "Vec" ]
    | EString _ -> Some [ TypeConstants.StringName ]
    | EQuotedSymbol _ -> Some [ TypeConstants.SymbolName ]
    // A numeric literal's own type rather than "int or double": `1` and `1.0`
    // are different shapes to a reader, and a union carrying both would
    // otherwise be ambiguous for every number written in it.
    | EInt(value, _) ->
        match inferNumericType value with
        | TCon(name, _) -> Some [ name ]
        | _ -> None
    | _ -> None

/// How a literal is named in a diagnostic about it.
let private literalShapeName (expr: Expr) : string =
    match expr with
    | EList _ -> "list"
    | EVec _ -> "vec"
    | EString _ -> "string"
    | EQuotedSymbol _ -> "symbol"
    | EInt _ -> "number"
    | _ -> "value"

/// `a`, `a or b`, `a, b or c`.
let private orList (names: string list) : string =
    match List.rev names with
    | [] -> ""
    | [ one ] -> one
    | last :: earlier -> String.concat ", " (List.rev earlier) + " or " + last

/// A union's cases as they were declared, for a diagnostic that has to say what
/// was on offer.
let private describeUnionCases (registry: TraitRegistry) (unionName: string) : string =
    match Map.tryFind unionName registry.Unions with
    | None -> ""
    | Some(_, cases) ->
        cases
        |> List.map (fun (caseName, payloads, _) ->
            let caseName = Naming.showTypeName caseName

            if payloads.IsEmpty then
                caseName
            else
                let types = payloads |> List.map DotNetInterop.showType |> String.concat " "
                $"(%s{caseName} %s{types})")
        |> String.concat ", "

/// Whether any element of a literal is itself a list or vec literal.
///
/// The question a failed element join has to answer: is this a list of things
/// that happen not to agree, or a tree that was never meant to have one element
/// type at all? Only the second has an answer beyond "these two types differ",
/// and a nested literal is what tells them apart.
let private hasNestedSequenceLiteral (elements: Expr list) : bool =
    elements
    |> List.exists (function
        | EList _
        | EVec _ -> true
        | _ -> false)

/// Join one element of a literal to the element type its siblings share.
///
/// Plain unification, except for the report when it fails. A literal reaches
/// here only when nothing pushed an expected type into it, and for a tree —
/// `'(pipe (ls "-l"))` — that is itself the error: its elements are a symbol
/// and a list because it describes a union, and no union was named. Saying that
/// `Symbol` does not unify with `(List Symbol)` names two types the program
/// never wrote, and no way out of it.
let private joinLiteralElement
    (env: Env)
    (r: Range)
    (kind: string)
    (elements: Expr list)
    (elementType: HMType)
    (elemTy: HMType)
    : unit =
    try
        unify env.Registry elemTy elementType
    with ex when Diagnostics.isDiagnostic ex && hasNestedSequenceLiteral elements ->
        let shown =
            DotNetInterop.showTypesTogether [ prune env.Registry elemTy; prune env.Registry elementType ]

        failwithf
            $"Type Error at %s{Lexer.formatPos r}: nothing here says what this %s{kind} literal holds, and its elements do not agree on a type by themselves:\n  %s{shown[0]}\n  %s{shown[1]}\nA literal with a nested list among its elements stands for a union, and which union it stands for comes from the type expected where it is written. A generic parameter expects nothing in particular, so there is none: annotate the value and pass that, as in (def (: procs (List ProcList)) (list '(...)))."

/// What a function-shaped local binding declares, before its body is looked at.
///
/// Built first so that a recursive call *inside* the body — one that passes a
/// keyword argument, or leaves an optional one out — has metadata to resolve
/// against. A top-level `defun` establishes its `FunMeta` before its body for
/// exactly the same reason.
type private LocalFunShape =
    { Mandatory: (string * HMType) list
      Keywords: (string * HMType * Expr) list
      /// Name and *element* type; the parameter itself is an array of it.
      Rest: (string * HMType) option
      RetType: HMType
      /// The flat arrow a call unifies against: mandatory, then keyword, then
      /// the rest array. The layout a top-level `defun` builds, because the
      /// same application rule reads it.
      FunType: HMType
      Meta: FunMeta }

/// Reads a local `defun`'s argument list as types.
///
/// A local function has no declared signature to take them from, so a
/// parameter's type is a metavariable unless `(: x type)` said otherwise, and
/// the return type is whatever the body turns out to have unless `: type` did.
let private localFunShape (env: Env) (args: DefunArg list) (retAnn: FType option) : LocalFunShape =
    let annotated (ann: FType option) =
        match ann with
        | Some t -> resolveTypeAnnotation env.Registry t
        | None -> freshMeta ()

    let mandatory =
        args |> List.choose (function MandatoryArg(n, t) -> Some(n, annotated t) | _ -> None)

    let keywords =
        args |> List.choose (function KeywordArg(n, d) -> Some(n, freshMeta (), d) | _ -> None)

    let rest = args |> List.tryPick (function RestArg n -> Some(n, freshMeta ()) | _ -> None)
    let retType = annotated retAnn

    let argTypes =
        (mandatory |> List.map snd)
        @ (keywords |> List.map (fun (_, t, _) -> t))
        @ (match rest with
           | Some(_, t) -> [ TCon("Array", [ t ]) ]
           | None -> [])

    { Mandatory = mandatory
      Keywords = keywords
      Rest = rest
      RetType = retType
      FunType = tfun argTypes retType
      Meta =
        { MandatoryCount = mandatory.Length
          KeywordParams = keywords |> List.map (fun (n, t, _) -> n, t)
          RestParam = rest |> Option.map snd } }

/// Infers a type, attaching a source location to any diagnostic that lacks one.
///
/// `unify` is where most type errors are raised and it is given two types and
/// nothing else — no range reaches it, and threading one to every call site
/// would mean inventing a location at the several that have no natural one.
/// Catching here instead costs nothing on the path where nothing throws, and
/// names the innermost expression whose inference failed, which is the smallest
/// piece of source that can be blamed.
///
/// The `when` is doing real work: a filter runs before the stack unwinds, so an
/// exception that is not a diagnostic is never caught here and keeps its trace.
let rec infer (env: Env) (expr: Expr) : HMType * TypedExpr =
    try
        inferNode env (resolveAliasedHead env.Registry expr)
    with ex when Diagnostics.needsLocation ex ->
        raise (Diagnostics.withLocation (exprRange expr) ex)

/// Rewrites a head identifier that is a prefixed spelling of a constructor, a
/// record type or a trait method back to the name it stands for.
///
/// Once, here, rather than at each of the guards in `inferNode`: that function
/// dispatches on `EIdent` and on `EApp(EIdent ...)` in a dozen places, and a
/// spelling is not meant to be visible to any of them.
and private resolveAliasedHead (registry: TraitRegistry) (expr: Expr) : Expr =
    match expr with
    | EIdent(name, r) ->
        let original = originalName registry name
        if original = name then expr else EIdent(original, r)
    | EApp(EIdent(name, ir), args, r) ->
        let original = originalName registry name
        if original = name then expr else EApp(EIdent(original, ir), args, r)
    | _ -> expr

and private inferNode (env: Env) (expr: Expr) : HMType * TypedExpr =
    match expr with
    | EInt(value, r) ->
        let inferredType = numericLiteralType value r

        inferredType,
        { Type = inferredType
          Range = r
          Node = TInt value }
    | EString(value, r) ->
        TypeConstants.stringType,
        { Type = TypeConstants.stringType
          Range = r
          Node = TString value }

    // `std/eq`'s own equality primitives, refused everywhere else. See
    // `Naming.eqPrivateBindings` for why they are shut away at all.
    | EIdent(name, r) when
        Set.contains name Naming.eqPrivateBindings
        && env.CurrentModule <> Naming.eqModuleName
        ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{name}' is private to std/eq. It is .NET's equality, and a type's own `Eq` implementation is what .NET equality is made *of* — writing one in terms of the other is a loop. Compare the fields instead, or derive."

    // An inline trait's methods are never bound as values: there is no single
    // scheme they could be bound under, which is the whole reason the trait is
    // inline-only.
    | EIdent(name, r) when
        Map.containsKey name env.Registry.TraitMethods
        && not (Map.containsKey name env.Bindings)
        ->
        let traitName = env.Registry.TraitMethods[name]

        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{name}' is a method of the inline-only trait '%s{traitName}' and has no value form. Apply it directly, or wrap it in a lambda at a known type."

    // `Class.Member` — a static field or property. This is how an enum value
    // such as `FileMode.Open` is written, and it is why `import/class` is
    // useful for a type that has no constructor at all.
    | EIdent(name, r) when
        not (Map.containsKey name env.Bindings)
        && not (name.EndsWith ".")
        && name.Contains "."
        && Map.containsKey (name.Substring(0, name.LastIndexOf ".")) env.Registry.ClrClasses
        ->
        let split = name.LastIndexOf "."
        let alias = name.Substring(0, split)
        let memberName = name.Substring(split + 1)
        let info = env.Registry.ClrClasses[alias]
        let where = Lexer.formatPos r
        let clrType = DotNetInterop.resolveType $" at %s{where}" info.ClrName
        let memberType = DotNetInterop.resolveMemberRead where clrType memberName true

        memberType,
        { Type = memberType
          Range = r
          Node = TForeignStaticGet(info.ClrName, memberName, memberType) }

    // An `import/extern` name used as a *value* rather than applied.
    //
    // A .NET method group is not a value, so the only thing this can mean is a
    // lambda that calls it — which needs the parameter types before there are
    // any arguments to infer them from. That is what the declared signature is
    // for, and why it is required here and optional everywhere else.
    //
    // An accessor is the exception, and needs no signature: a property has no
    // overload set, so its type is known from the member alone. A *static*
    // accessor read is not even a lambda — the alias names the value, exactly as
    // `FileMode.Open` does.
    //
    // An ordinary binding of the same name wins. The extern registry is one flat
    // namespace shared by every module in the compilation, so without this an
    // alias published by some imported library would silently capture calls to a
    // function defined right here.
    | EIdent(name, r) when Map.containsKey name env.Registry.ClrExterns && not (Map.containsKey name env.Bindings) ->
        let info = env.Registry.ClrExterns[name]
        let where = Lexer.formatPos r
        let clrType = DotNetInterop.resolveType $" at %s{where}" info.ClrType
        let receiverType = TCon(info.ClrType, [])

        // Named once: three of the four shapes below build a lambda over the
        // receiver, and all of them have to agree on what its type is.
        let identOf (n: string) (t: HMType) : TypedExpr =
            { Type = t
              Range = r
              Node = TIdent(n, []) }

        match info.Kind with
        // A static read *is* the value, re-read wherever the name stands —
        // `TForeignStaticGet` emits the member access itself, so a property like
        // `DateTime.Now` still means "now" at each mention.
        | ExternGet when not info.IsInstance ->
            let memberType = DotNetInterop.resolveMemberRead where clrType info.MemberName true

            memberType,
            { Type = memberType
              Range = r
              Node = TForeignStaticGet(info.ClrType, info.MemberName, memberType) }

        | ExternGet ->
            let memberType = DotNetInterop.resolveMemberRead where clrType info.MemberName false
            let recv = Gensym.fresh "__foreign"

            let body: TypedExpr =
                { Type = memberType
                  Range = r
                  Node = TDotPropertyGet(identOf recv receiverType, info.MemberName, memberType) }

            let funType = tfun [ receiverType ] memberType

            funType,
            { Type = funType
              Range = r
              Node = TLambda([ recv ], body) }

        | ExternSet ->
            let memberType = DotNetInterop.resolveMemberWrite where clrType info.MemberName (not info.IsInstance)
            let value = Gensym.fresh "__foreign"
            let valueExpr = identOf value memberType

            let paramNames, paramTypes, node =
                if info.IsInstance then
                    let recv = Gensym.fresh "__foreign"

                    [ recv; value ],
                    [ receiverType; memberType ],
                    TDotPropertySet(identOf recv receiverType, info.MemberName, valueExpr)
                else
                    [ value ], [ memberType ], TForeignStaticSet(info.ClrType, info.MemberName, valueExpr)

            let body: TypedExpr =
                { Type = TypeConstants.voidType
                  Range = r
                  Node = node }

            let funType = tfun paramTypes TypeConstants.voidType

            funType,
            { Type = funType
              Range = r
              Node = TLambda(paramNames, body) }

        | ExternMethod ->
            // An async import is not a value either, and for a second reason on
            // top of the method-group one: the eta-expansion would be an
            // ordinary lambda whose body is a yield point, which is §3.1's
            // higher-order restriction with a worse error message. Said here
            // rather than left to `ColourCheck`, which would name a lambda the
            // user never wrote.
            if info.IsAsync then
                failwithf
                    $"Type Error at %s{where}: '%s{name}' names the async .NET method '%s{info.ClrType}.%s{info.MemberName}', and calling it is a yield point, so it cannot be used as a value — the (fun ...) it would become may not suspend. Call it directly, or wrap the call in a bjoroutine of your own and pass that."

            match info.GenericTypeArgs with
            // A generic method as a value. The eta-expansion is built from the
            // declared signature rather than from reflection, which is where a
            // generic import's meaning lives anyway — and the lambda is at *one*
            // instantiation, whatever the context settles it to, because a C#
            // delegate cannot be generic.
            | Some _ ->
                let paramTypes, retType, typeArgs = instantiateGenericExtern env.Registry where info

                let methodParams =
                    if info.IsInstance then List.tail paramTypes else paramTypes

                let argNames = paramTypes |> List.map (fun _ -> Gensym.fresh "__foreign")
                let argExprs: TypedExpr list = List.map2 identOf argNames paramTypes

                let meta =
                    Some
                        { DeclaringType = info.ClrType
                          MethodName = info.MemberName
                          ParameterTypes = methodParams
                          ReturnType = retType
                          TypeArguments = typeArgs
                          IsStatic = not info.IsInstance
                          Exceptions = info.Exceptions
                          Await = false
                          AmbientToken = false }

                let node =
                    if info.IsInstance then
                        TDotMethodCall(List.head argExprs, info.MemberName, List.tail argExprs, meta)
                    else
                        TForeignStaticCall(info.ClrType, info.MemberName, argExprs, meta)

                let resultType = wrapForeignExceptions info.Exceptions retType

                let body: TypedExpr =
                    { Type = resultType
                      Range = r
                      Node = node }

                let funType = tfun paramTypes resultType

                funType,
                { Type = funType
                  Range = r
                  Node = TLambda(argNames, body) }

            | None ->

            match info.DeclaredType with
            | Some(TFun(declaredParams, _, _)) ->
                // The receiver of an instance member is the alias's first
                // parameter and none of the method's, so the declared type is
                // split before reflection sees it and rejoined afterwards.
                let declaredReceiver, methodParamTypes =
                    if info.IsInstance then
                        match declaredParams with
                        | recv :: rest -> Some recv, rest
                        | [] ->
                            failwithf
                                $"Type Error at %s{where}: '%s{name}' names the instance method '%s{info.ClrType}.%s{info.MemberName}', whose receiver is its first argument, but its declared type takes none."
                    else
                        None, declaredParams

                let resolved = resolveExternMethod where info clrType methodParamTypes
                unifyForeignArgs env.Registry methodParamTypes resolved.ParameterTypes
                declaredReceiver |> Option.iter (fun t -> unify env.Registry t receiverType)

                let retType = wrapForeignExceptions info.Exceptions resolved.ReturnType
                let argNames = resolved.ParameterTypes |> List.map (fun _ -> Gensym.fresh "__foreign")

                // Annotated because `TypedExpr` and `TypedPattern` have the same
                // three field names, and neither of these is in a position that
                // says which one is meant.
                let argExprs: TypedExpr list = List.map2 identOf argNames resolved.ParameterTypes

                let paramNames, paramTypes, node =
                    if info.IsInstance then
                        let recv = Gensym.fresh "__foreign"

                        recv :: argNames,
                        receiverType :: resolved.ParameterTypes,
                        TDotMethodCall(
                            identOf recv receiverType,
                            info.MemberName,
                            argExprs,
                            Some(metadataOf resolved info.Exceptions)
                        )
                    else
                        argNames,
                        resolved.ParameterTypes,
                        TForeignStaticCall(
                            resolved.DeclaringType,
                            info.MemberName,
                            argExprs,
                            Some(metadataOf resolved info.Exceptions)
                        )

                let body: TypedExpr =
                    { Type = retType
                      Range = r
                      Node = node }

                let funType = tfun paramTypes retType

                funType,
                { Type = funType
                  Range = r
                  Node = TLambda(paramNames, body) }
            | _ ->
                failwithf
                    $"Type Error at %s{where}: '%s{name}' names the .NET method '%s{info.ClrType}.%s{info.MemberName}', and a method group is not a value. To use it as one, give it a signature in its import/extern clause; otherwise call it directly."

    // `apply` is a form, not a function, so it has no value form either.
    //
    // There is no `HMType` to bind it to: the arity it checks, the parameters
    // it fills and the effect the call takes on are all read off whichever `f`
    // it is applied to, and a bare `apply` has no `f` to read them from. Said
    // here rather than left to `lookup`, which would report it as unbound and
    // send the reader looking for a missing import.
    | EIdent("apply", r) when not (Map.containsKey "apply" env.Bindings) ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: 'apply' is a form, not a value, so it has no value form. Write the call out — (apply f xs) — or wrap it in a lambda over a function you name there."

    | EIdent(name, r) ->
        let binding = lookup env name
        let t, tArgs, constraints = instantiate env.Registry binding.Scheme

        t,
        { Type = t
          Range = r
          Node = TIdent(name, tArgs) }

    // A name the compiler wrote, in value position.
    //
    // Handled by putting module level's binding for it back and then taking the
    // ordinary path, rather than by resolving it here. A name is not always a
    // binding: `folding` is a union case and reaches its meaning through the
    // registry, as record constructors and trait methods do, and those paths
    // are not worth reproducing.
    //
    // Nothing to put back means nothing at module level had that name, so there
    // is nothing a local could be shadowing and the registries decide.
    | EResolved(name, r) -> infer (unshadow name env) (EIdent(name, r))

    | EFun(args, body, colour, r) ->
        let argTypes = args |> List.map (fun _ -> freshMeta ())
        let eff = colourEffect colour

        let localEnv =
            List.zip args argTypes
            |> List.fold
                (fun acc (n, t) ->
                    addBinding
                        n
                        { Scheme = Scheme([], [], t)
                          IsMutable = false }
                        acc)
                (withoutSeqElement env)

        let bodyType, typedBody = infer localEnv body
        let funType = TFun(argTypes, bodyType, eff)

        funType,
        { Type = funType
          Range = r
          Node = TLambda(args, typedBody) }

    // A trait method in application position.
    //
    // The call is typed immediately — every position in the template gets a
    // fresh meta and the arguments and result are unified against them — while
    // *which* implementation runs is left blank for the solver. That is what
    // lets `pure`, whose constructor appears only in its result, be resolved at
    // all: the metas are shared with the surrounding expression, so an enclosing
    // `bind` or a declared return type pins them.
    // Ahead of every specialised application below, so that a call the compiler
    // wrote reaches whichever of them it should — trait method, record
    // constructor, union case or ordinary function — with its head meaning what
    // it meant at module level.
    | EApp(EResolved(name, mr), args, r) -> infer (unshadow name env) (EApp(EIdent(name, mr), args, r))

    // A trait method call, unless the name has been bound over.
    //
    // This used to dispatch on the name alone, before the environment was
    // consulted at all, so nothing a program wrote could intercept it: a local
    // called `next` or a parameter called `compare` was accepted, ignored, and
    // dead — and the program's own calls to it failed on arity, against the
    // programmer's line, naming a parameter they never wrote.
    //
    // `TraitMethodNames` is what distinguishes the method's own binding from a
    // binding over it, which `Bindings` cannot: both sit there under one name.
    // An inline trait's methods are not bound at all, hence the first half.
    | EApp(EIdent(methodName, _), args, r) when Set.contains methodName env.TraitMethodNames ->
        let traitName = env.Registry.TraitMethods[methodName]

        // Every argument is positional, keywords included. A trait method's
        // shape is fixed by its trait and no trait declares a keyword
        // parameter, so `#:foo` here can only be the keyword *value* — which
        // `(= k #:foo)` is, now that `Keyword` has an `Eq` implementation. A
        // call written as though it took keyword arguments fails on arity
        // instead, which is what it is.
        let typedArgs = args |> List.map (infer env)

        let methodType, tref = traitCallType env traitName methodName r
        let retType = freshMeta ()
        unify env.Registry methodType (tfun (typedArgs |> List.map fst) retType)

        retType,
        { Type = retType
          Range = r
          Node = TTraitCall(tref, typedArgs |> List.map snd, []) }

    // Record and struct construction: `(Car (brand "banana") (year 3000))`.
    //
    // It arrives as an ordinary application because nothing before this point
    // knows which names are record types — and so do the arguments, `(brand
    // "banana")` being indistinguishable from a call to `brand` until the head
    // is known. Both are reread here, where the registry can say so. The type
    // name is the constructor: no field set is ever searched for an owner, and
    // two records sharing a field name are no longer in each other's way.
    | EApp(EIdent(recordTypeName, _), args, r) when Map.containsKey recordTypeName env.Registry.Records ->
        let writtenFields =
            args
            |> List.map (fun arg ->
                match arg with
                | EApp(EIdent(fieldName, _), [ value ], _) -> fieldName, value
                | bad ->
                    failwithf
                        $"Type Error at %s{Lexer.formatPos (exprRange bad)}: '%s{recordTypeName}' is a record type, so each argument is one of its fields, written (field-name value).")

        let instantiatedRecordType, expectedFields, expectedFieldsInstantiated =
            instantiateRecord env.Registry recordTypeName

        let fieldList = expectedFields |> List.map fst |> String.concat ", "

        let provided =
            (Map.empty, writtenFields)
            ||> List.fold (fun acc (name, expr) ->
                if Map.containsKey name acc then
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: field '%s{name}' of '%s{recordTypeName}' is given twice."

                let exprType, typedExpr = infer env expr

                match Map.tryFind name expectedFieldsInstantiated with
                | Some expectedType -> unify env.Registry exprType expectedType
                | None ->
                    failwithf
                        $"Type Error at %s{Lexer.formatPos (exprRange expr)}: '%s{recordTypeName}' has no field '%s{name}'. Its fields are: %s{fieldList}."

                Map.add name typedExpr acc)

        // Declaration order, not the order the fields were written in: the
        // constructor a record compiles to takes them positionally, so writing
        // them out of order would otherwise silently swap two same-typed fields.
        let orderedFields =
            expectedFields
            |> List.map (fun (name, _) ->
                match Map.tryFind name provided with
                | Some typedExpr -> name, typedExpr
                | None ->
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: '%s{recordTypeName}' is missing field '%s{name}'. Every field has to be given.")

        instantiatedRecordType,
        { Type = instantiatedRecordType
          Range = r
          Node = TRecordMake orderedFields }

    // --- Foreign .NET interop ---
    //
    // All four forms resolve the member they name against real .NET metadata
    // right here, from the types of the arguments as inference has them. The
    // selected overload's parameter types are then unified *back into* the
    // arguments, so reflection does not merely check the call — it informs it.

    // `(.-Property target)` — an instance property or field read.
    | EApp(EIdent(name, _), args, r) when name.StartsWith ".-" && name.Length > 2 ->
        let propName = name.Substring 2
        let where = Lexer.formatPos r

        match args with
        | [ target ] ->
            let targetType, typedTarget = infer env target
            let clrTarget = receiverClrType where name targetType
            let propType = DotNetInterop.resolveMemberRead where clrTarget propName false

            propType,
            { Type = propType
              Range = r
              Node = TDotPropertyGet(typedTarget, propName, propType) }
        | _ ->
            failwithf
                $"Type Error at %s{where}: '%s{name}' reads a property, so it takes exactly one argument — the object to read it from — but was given %d{args.Length}."

    // `(.Method target args...)` — an instance method call.
    | EApp(EIdent(name, _), args, r) when name.StartsWith "." && name.Length > 1 ->
        let methodName = name.Substring 1
        let where = Lexer.formatPos r

        match args with
        | [] ->
            failwithf
                $"Type Error at %s{where}: '%s{name}' calls an instance method, so its first argument is the object to call it on, but it was given none."
        | target :: rest ->
            let targetType, typedTarget = infer env target
            let clrTarget = receiverClrType where name targetType

            let typedArgs = rest |> List.map (infer env)
            let argTypes = typedArgs |> List.map fst

            settleLiterals argTypes
            let resolved = DotNetInterop.resolveMethod where false clrTarget methodName argTypes

            let coercedArgs =
                reconcileForeignArgs env.Registry (typedArgs |> List.map snd) resolved.ParameterTypes

            // Never exception-wrapped: `import/class` declares one signature —
            // the constructor's — so there is nowhere to say what a method may
            // raise, and wrapping it anyway would swallow exceptions nobody
            // listed.
            let retType = resolved.ReturnType

            retType,
            { Type = retType
              Range = r
              Node = TDotMethodCall(typedTarget, methodName, coercedArgs, Some(metadataOf resolved [])) }

    // `(ClassName. args...)` — construction.
    | EApp(EIdent(name, _), args, r) when
        name.EndsWith "."
        && name.Length > 1
        && Map.containsKey (name.Substring(0, name.Length - 1)) env.Registry.ClrClasses
        ->
        let alias = name.Substring(0, name.Length - 1)
        let info = env.Registry.ClrClasses[alias]
        let where = Lexer.formatPos r
        let clrType = DotNetInterop.resolveType $" at %s{where}" info.ClrName

        let typedArgs = args |> List.map (infer env)
        let argTypes = typedArgs |> List.map fst

        settleLiterals argTypes
        let resolved = DotNetInterop.resolveConstructor where clrType argTypes

        let coercedArgs =
            reconcileForeignArgs env.Registry (typedArgs |> List.map snd) resolved.ParameterTypes

        // The declared signature is enforced against the overload reflection
        // chose, rather than used in place of it. Writing one down is how a
        // reader of the source learns what the constructor takes without
        // consulting the BCL; getting it wrong is an error rather than a
        // silently ignored comment.
        match info.CtorType with
        | Some declared -> unify env.Registry declared (tfun resolved.ParameterTypes resolved.ReturnType)
        | None -> ()

        let retType = wrapForeignExceptions info.CtorExceptions resolved.ReturnType

        let meta =
            { ClrType = resolved.DeclaringType
              ParameterTypes = resolved.ParameterTypes
              Exceptions = info.CtorExceptions }

        retType,
        { Type = retType
          Range = r
          Node = TNewObject(resolved.DeclaringType, coercedArgs, Some meta) }

    // A .NET member named by `import/extern`, applied. As above, a binding of
    // the same name shadows the alias rather than the other way round.
    //
    // An instance member's receiver is the first argument and is taken off the
    // front here, so that everything below — overload selection, the threaded
    // token, a declared signature — works in the member's own parameters. It
    // rejoins as the receiver of a `TDotMethodCall`, which is the same node
    // `(.Method x ...)` produces; the difference is that this one arrived
    // through a clause that could say `#:async`.
    | EApp(EIdent(name, _), args, r) when
        Map.containsKey name env.Registry.ClrExterns && not (Map.containsKey name env.Bindings)
        ->
        let info = env.Registry.ClrExterns[name]
        let where = Lexer.formatPos r
        let clrType = DotNetInterop.resolveType $" at %s{where}" info.ClrType
        let receiverType = TCon(info.ClrType, [])

        /// Splits the receiver off an instance member's argument list.
        ///
        /// The receiver is reconciled like an argument rather than unified with
        /// the declaring type, so that a subclass reaches a member declared on
        /// its base: the upcast is written into the tree exactly as a widening
        /// argument's is, which is also what keeps the C# that reads the
        /// generated call resolving it the same way.
        let takeReceiver (typedArgs: (HMType * TypedExpr) list) =
            match typedArgs with
            | (_, recv) :: rest ->
                let coerced =
                    reconcileForeignArgs env.Registry [ recv ] [ receiverType ] |> List.head

                coerced, rest
            | [] ->
                failwithf
                    $"Type Error at %s{where}: '%s{name}' names the instance member '%s{info.ClrType}.%s{info.MemberName}', so its first argument is the object to use it on, but it was given none."

        match info.Kind with
        // A property read. There is no overload set and no conversion to make:
        // the member has one type, and the only thing the call site supplies is
        // the receiver.
        | ExternGet ->
            let typedArgs = args |> List.map (infer env)

            if not info.IsInstance then
                failwithf
                    $"Type Error at %s{where}: '%s{name}' reads the static property '%s{info.ClrType}.%s{info.MemberName}', so it is a value rather than a call. Write it bare, as '%s{name}'."

            let receiver, rest = takeReceiver typedArgs

            if not rest.IsEmpty then
                failwithf
                    $"Type Error at %s{where}: '%s{name}' reads a property, so it takes exactly one argument — the object to read it from — but was given %d{args.Length}."

            let memberType = DotNetInterop.resolveMemberRead where clrType info.MemberName false

            match info.DeclaredType with
            | Some declared -> unify env.Registry declared (tfun [ receiverType ] memberType)
            | None -> ()

            memberType,
            { Type = memberType
              Range = r
              Node = TDotPropertyGet(receiver, info.MemberName, memberType) }

        // A property write. Void, like `set!`, and for the same reason: the
        // value assigned is not what the form is for, and handing it back would
        // make `(set-length! sb n)` usable as an expression that quietly has a
        // value.
        | ExternSet ->
            let typedArgs = args |> List.map (infer env)
            let memberType = DotNetInterop.resolveMemberWrite where clrType info.MemberName (not info.IsInstance)

            let receiver, rest =
                if info.IsInstance then
                    let recv, rest = takeReceiver typedArgs
                    Some recv, rest
                else
                    None, typedArgs

            let value =
                match rest with
                | [ v ] -> reconcileForeignArgs env.Registry [ snd v ] [ memberType ] |> List.head
                | _ ->
                    let wanted = if info.IsInstance then "the object to write it on and the value" else "the value"

                    failwithf
                        $"Type Error at %s{where}: '%s{name}' writes a property, so it takes %s{wanted}, but was given %d{args.Length} argument(s)."

            let declaredParams =
                match receiver with
                | Some _ -> [ receiverType; memberType ]
                | None -> [ memberType ]

            match info.DeclaredType with
            | Some declared -> unify env.Registry declared (tfun declaredParams TypeConstants.voidType)
            | None -> ()

            let node =
                match receiver with
                | Some recv -> TDotPropertySet(recv, info.MemberName, value)
                | None -> TForeignStaticSet(info.ClrType, info.MemberName, value)

            TypeConstants.voidType,
            { Type = TypeConstants.voidType
              Range = r
              Node = node }

        // A generic method, applied. There is no overload to choose from the
        // arguments here: the import already chose one, against the declared
        // signature, and solved its type arguments. So this is an ordinary
        // polymorphic call — instantiate, unify, done — and the only thing that
        // makes it foreign is where the body ends up.
        | ExternMethod when info.GenericTypeArgs.IsSome ->
            let paramTypes, retType, typeArgs = instantiateGenericExtern env.Registry where info

            if args.Length <> paramTypes.Length then
                let what =
                    if info.IsInstance then
                        $"the object to use it on and %d{paramTypes.Length - 1} argument(s)"
                    else
                        $"%d{paramTypes.Length} argument(s)"

                failwithf
                    $"Type Error at %s{where}: '%s{name}' takes %s{what}, but was given %d{args.Length}."

            let typedArgs = args |> List.map (infer env)

            // Unified rather than scored. A declared signature is not a
            // candidate to be ranked against others — it is what the alias
            // *means* — so an argument that does not fit is a type error naming
            // the two types, exactly as it would be for a Bjolang function.
            List.iter2 (fun (argType, _) paramType -> unify env.Registry argType paramType) typedArgs paramTypes

            let callArgs = typedArgs |> List.map snd

            let receiver, methodArgs, methodParams =
                if info.IsInstance then
                    Some(List.head callArgs), List.tail callArgs, List.tail paramTypes
                else
                    None, callArgs, paramTypes

            let resultType = wrapForeignExceptions info.Exceptions retType

            let meta =
                Some
                    { DeclaringType = info.ClrType
                      MethodName = info.MemberName
                      ParameterTypes = methodParams
                      ReturnType = retType
                      TypeArguments = typeArgs
                      IsStatic = not info.IsInstance
                      Exceptions = info.Exceptions
                      Await = false
                      AmbientToken = false }

            resultType,
            { Type = resultType
              Range = r
              Node =
                match receiver with
                | Some recv -> TDotMethodCall(recv, info.MemberName, methodArgs, meta)
                | None -> TForeignStaticCall(info.ClrType, info.MemberName, methodArgs, meta) }

        | ExternMethod ->
            let allTypedArgs = args |> List.map (infer env)

            let receiver, typedArgs =
                if info.IsInstance then
                    let recv, rest = takeReceiver allTypedArgs
                    Some recv, rest
                else
                    None, allTypedArgs

            let argTypes = typedArgs |> List.map fst

            // An `#:async` import resolves against one more parameter than the
            // call site wrote — the ambient token — and yields the task's result
            // rather than the task, so the two paths differ in what they hand
            // back and agree on everything after it.
            let resolved, visibleParams, callResultType, threadsToken =
                if info.IsAsync then
                    resolveAsyncExtern where info clrType argTypes
                elif info.Cancellable then
                    // Threaded, but not awaited: the method takes a token and
                    // hands back an ordinary value. `File.ReadLinesAsync` is the
                    // case — a stream rather than a task, and a token in every
                    // overload.
                    let resolved, threads = resolveTokenThreadedExtern where info clrType argTypes

                    let visible =
                        if threads then
                            resolved.ParameterTypes |> List.truncate (resolved.ParameterTypes.Length - 1)
                        else
                            resolved.ParameterTypes

                    resolved, visible, resolved.ReturnType, threads
                else
                    let resolved = resolveExternMethod where info clrType argTypes
                    resolved, resolved.ParameterTypes, resolved.ReturnType, false

            let coercedArgs =
                reconcileForeignArgs env.Registry (typedArgs |> List.map snd) visibleParams

            // Checked against what the *caller* sees: the threaded token is not
            // a parameter anyone writes, and a declared signature that had to
            // mention it would be describing the emitter's work rather than the
            // call's. The receiver is part of what the caller sees, so an
            // instance member's declared type carries it.
            let declaredParams =
                match receiver with
                | Some _ -> receiverType :: visibleParams
                | None -> visibleParams

            match info.DeclaredType with
            | Some declared -> unify env.Registry declared (tfun declaredParams callResultType)
            | None -> ()

            let retType = wrapForeignExceptions info.Exceptions callResultType

            let meta =
                Some
                    { metadataOf resolved info.Exceptions with
                        ParameterTypes = visibleParams
                        ReturnType = callResultType
                        Await = info.IsAsync
                        AmbientToken = threadsToken }

            retType,
            { Type = retType
              Range = r
              Node =
                match receiver with
                | Some recv -> TDotMethodCall(recv, info.MemberName, coercedArgs, meta)
                | None -> TForeignStaticCall(resolved.DeclaringType, info.MemberName, coercedArgs, meta) }

    // `(apply f pos1 ... posN coll)` — spread a collection into `f`'s `#:rest`
    // parameter.
    //
    // An intrinsic rather than a prelude binding because there is no `HMType`
    // it could be given: how many arguments it accepts, which parameters they
    // fill, and whether the resulting call suspends are all read off whatever
    // `f` turns out to be.
    //
    // `f` has to be a bare name, and that is a real restriction rather than a
    // simplification. Whether a parameter is a `#:rest` one is recorded in
    // `FunMeta`, which `infer` looks up *by name*; the flat type says
    // `(Array %a)` and an ordinary array parameter says exactly the same thing.
    // So a computed callee — a parameter, a lambda, an element of a vec — has
    // nothing that could distinguish the two, and spreading into a parameter
    // that is not variadic is precisely the run-time failure this form exists
    // to rule out. `addBinding` drops a shadowed name's `FunMeta` for the same
    // reason, so a local `f` cannot inherit a global one's shape either.
    | EApp(EIdent("apply", _), args, r) when not (Map.containsKey "apply" env.Bindings) ->
        let where = Lexer.formatPos r

        let calleeExpr, callArgs =
            match args with
            | f :: rest -> f, rest
            | [] ->
                failwithf
                    $"Type Error at %s{where}: 'apply' needs a function and a collection, as (apply f coll)."

        let calleeName =
            match calleeExpr with
            | EIdent(n, _) when Map.containsKey n env.Bindings -> n
            | _ ->
                failwithf
                    $"Type Error at %s{where}: 'apply' needs a named function as its first argument. Whether a parameter is a #:rest one belongs to a function's declaration rather than to its type, so a computed function carries nothing that says how to spread into it."

        let meta =
            match Map.tryFind calleeName env.FunMetas with
            | Some m when m.RestParam.IsSome -> m
            | _ ->
                failwithf
                    $"Type Error at %s{where}: '%s{calleeName}' has no #:rest parameter, so there is nothing for 'apply' to spread a collection into. A collection's length is not part of its type, so filling fixed parameters from one would need an arity check at run time. Call '%s{calleeName}' directly with positional arguments instead."

        // Keyword arguments are passed straight through. `FunMeta` is what says
        // which names are keywords, and it is the callee's own — the same list
        // an ordinary call site consults.
        let isDeclaredKw kwName =
            meta.KeywordParams |> List.exists (fun (k, _) -> k = kwName)

        let rec splitArgs positional keywords remaining =
            match remaining with
            | [] -> List.rev positional, List.rev keywords
            | EKeyword(kwName, _) :: value :: rest when isDeclaredKw kwName ->
                splitArgs positional ((kwName, value) :: keywords) rest
            | EKeyword(kwName, kr) :: [] when isDeclaredKw kwName ->
                failwithf $"Keyword argument '#:%s{kwName}' is missing a value at %s{Lexer.formatPos kr}"
            | arg :: rest -> splitArgs (arg :: positional) keywords rest

        let positionalExprs, keywordExprs = splitArgs [] [] callArgs

        // The *last* positional argument is the collection. A syntactic rule,
        // not an inferred one: which argument is spread has to be legible from
        // the call site alone, and a type-directed choice would change under a
        // signature the reader cannot see.
        let fixedExprs, collExpr =
            match List.rev positionalExprs with
            | last :: revFixed -> List.rev revFixed, last
            | [] ->
                failwithf
                    $"Type Error at %s{where}: 'apply' needs a collection as its last argument, after any arguments filling '%s{calleeName}'s fixed parameters."

        if fixedExprs.Length < meta.MandatoryCount then
            failwithf
                $"Type Error at %s{where}: '%s{calleeName}' has %d{meta.MandatoryCount} fixed parameter(s) and 'apply' was given %d{fixedExprs.Length}. Every fixed parameter has to be supplied positionally, before the collection."

        if fixedExprs.Length > meta.MandatoryCount then
            failwithf
                $"Type Error at %s{where}: '%s{calleeName}' has %d{meta.MandatoryCount} fixed parameter(s) and 'apply' was given %d{fixedExprs.Length}. Everything before the collection fills a fixed parameter, one for one, and the collection is what fills the #:rest parameter."

        let targetType, typedTarget = infer env calleeExpr
        let fixedTyped = fixedExprs |> List.map (infer env)

        // A literal collection is never built. The elements go straight into
        // the rest array, which is the very node a direct call `(f a b c)`
        // produces — so the two spellings compile to the same call.
        let literalItems =
            match collExpr with
            | EList(items, _) -> Some items
            | EVec(items, _) -> Some items
            | _ -> None

        let restElem, restNode =
            match literalItems with
            | Some items ->
                let elemSlot = freshMeta ()

                let typedItems =
                    items
                    |> List.map (fun item ->
                        let itemType, typedItem = infer env item
                        unify env.Registry itemType elemSlot
                        typedItem)

                elemSlot,
                ({ Type = TCon("Array", [ elemSlot ])
                   Range = r
                   Node = TArrayMake typedItems }: TypedExpr)
            | None ->
                let collType, typedColl = infer env collExpr

                // Named so the conversion is an ordinary typed call. Emitting it
                // as C# text in `Codegen` instead would hide it from
                // `containsAwait`, which walks the typed tree to decide whether
                // the enclosing lambda has to be async.
                let convert (fn: string) (elem: HMType) : TypedExpr =
                    let arrayType = TCon("Array", [ elem ])

                    { Type = arrayType
                      Range = r
                      Node =
                        TApply(
                            { Type = tfun [ typedColl.Type ] arrayType
                              Range = r
                              Node = TIdent(fn, []) },
                            [ typedColl ],
                            []
                        ) }

                match prune env.Registry collType with
                // Passed through untouched: no conversion and no allocation,
                // which is the case `apply` is actually worth having for.
                | TCon("Array", [ elem ]) -> elem, typedColl
                | TCon("Vec", [ elem ]) -> elem, convert "vec->array" elem
                | TCon("List", [ elem ]) -> elem, convert "list->array" elem
                | TMeta _ ->
                    failwithf
                        $"Type Error at %s{where}: 'apply' needs to know the last argument's type here to spread it, and nothing has fixed it yet. Annotate it as an Array, a Vec or a List."
                | other ->
                    failwithf
                        $"Type Error at %s{where}: 'apply' can spread an Array, a Vec or a List, and the last argument is %s{DotNetInterop.showType other}."

        // Each keyword slot gets a fresh metavariable rather than the type
        // `FunMeta` recorded, for the reason the ordinary call path gives: the
        // recorded type still carries the declaration's rigid `TVar`s, and the
        // flat unification below is what gives the slot its real type.
        let keywordTyped = keywordExprs |> List.map (fun (n, e) -> n, infer env e)

        let kwSlots =
            meta.KeywordParams
            |> List.map (fun (kwName, _) ->
                let slot = freshMeta ()

                match keywordTyped |> List.tryFind (fun (n, _) -> n = kwName) with
                | Some(_, (valType, _)) -> unify env.Registry valType slot
                | None -> ()

                slot)

        let retType = freshMeta ()
        let flatTypes = (fixedTyped |> List.map fst) @ kwSlots @ [ TCon("Array", [ restElem ]) ]

        // The effect is the callee's own, copied rather than chosen. A pure `f`
        // gives a pure node and a suspending one a suspending node, which is why
        // there is one `apply` and not an `apply` plus an `apply/bjo`:
        // `ColourCheck` and `Codegen` already read the effect off the callee's
        // arrow, so neither needs to know this form exists.
        unify env.Registry targetType (TFun(flatTypes, retType, demandedEffect env targetType))

        retType,
        { Type = retType
          Range = r
          Node =
            TApply(
                typedTarget,
                (fixedTyped |> List.map snd) @ [ restNode ],
                keywordTyped |> List.map (fun (n, (_, te)) -> n, te)
            ) }

    | EApp(target, args, r) ->
        let targetType, typedTarget = infer env target

        // Look up FunMeta if the target is a known identifier
        let funMeta =
            match target with
            | EIdent(name, _) -> Map.tryFind name env.FunMetas
            | _ -> None

        let isDeclaredKw kwName =
            match funMeta with
            | Some meta -> meta.KeywordParams |> List.exists (fun (k, _) -> k = kwName)
            | None -> false

        /// The type the callee declares for positional argument `i`, when there
        /// is one worth pushing into the argument.
        ///
        /// Only a list or vec literal asks: it is the one argument shape whose
        /// *elements* need the expected type before they can be inferred at
        /// all, which is what makes `(run/lines '(pipe (ls "-l")))` work
        /// without an annotated intermediate binding.
        ///
        /// Only the mandatory prefix, because past it the flat parameter list
        /// holds the keyword parameters in declaration order and then the rest
        /// array, and neither lines up with an argument's position. And only a
        /// parameter that is already something: an unbound metavariable is a
        /// polymorphic parameter, `(map run/lines ...)`, which expects nothing
        /// in particular and so has nothing to push.
        let expectedParam (i: int) : HMType option =
            let declared =
                match prune env.Registry targetType with
                | TFun(paramTys, _, _) when i < paramTys.Length ->
                    match funMeta with
                    | Some meta when i >= meta.MandatoryCount -> None
                    | _ -> Some(prune env.Registry paramTys[i])
                | _ -> None

            match declared with
            | Some(TMeta _)
            | None -> None
            | Some paramTy -> Some paramTy

        // Separate keyword args from positional args
        // Keyword args appear as EKeyword("name") followed by a value expr when matching a declared keyword parameter
        let rec splitArgs positional keywords remaining =
            match remaining with
            | [] -> List.rev positional, List.rev keywords
            | EKeyword(kwName, _) :: value :: rest when isDeclaredKw kwName ->
                let valType, typedVal = infer env value
                splitArgs positional ((kwName, (valType, typedVal)) :: keywords) rest
            | EKeyword(kwName, kr) :: [] when isDeclaredKw kwName ->
                failwithf $"Keyword argument '#:%s{kwName}' is missing a value at %s{Lexer.formatPos kr}"
            | arg :: rest ->
                let argType, typedArg =
                    match arg, expectedParam (List.length positional) with
                    | (EList _ | EVec _), Some paramTy -> inferChecked paramTy env arg
                    | _ -> infer env arg

                splitArgs ((argType, typedArg) :: positional) keywords rest

        let positionalArgs, keywordArgs = splitArgs [] [] args
        let retType = freshMeta ()

        match funMeta with
        | Some meta when not keywordArgs.IsEmpty || meta.RestParam.IsSome || not meta.KeywordParams.IsEmpty ->
            // Structured call: separate mandatory, keyword, and rest args
            let mandatoryArgs = positionalArgs |> List.take (min positionalArgs.Length meta.MandatoryCount)
            let restArgs = positionalArgs |> List.skip (min positionalArgs.Length meta.MandatoryCount)

            // Build the flat arg types for unification (mandatory + keyword in decl order + rest array)
            //
            // Each keyword and rest slot gets a *fresh* metavariable rather than
            // the type recorded in `FunMeta`. The recorded type came from the
            // declaration and still carries that declaration's rigid `TVar`s, so
            // unifying an argument against it directly is what used to make
            // `(: f (-> #:rest %a %a))` unusable: the first call tried to unify
            // `int` with `'a` itself instead of with a fresh instance of it.
            //
            // The flat unification against `targetType` below is what gives
            // these slots their real types. `targetType` came from `infer`, which
            // instantiates the scheme, so its parameters are already fresh per
            // call site. `FunMeta` is then consulted only for the call's *shape*
            // — how many mandatory parameters there are, which keywords exist,
            // and whether there is a rest parameter at all.
            let kwArgTypes =
                meta.KeywordParams |> List.map (fun (kwName, _) ->
                    let slot = freshMeta ()

                    match keywordArgs |> List.tryFind (fun (n, _) -> n = kwName) with
                    | Some (_, (valType, _)) -> unify env.Registry valType slot
                    | None -> ()  // keyword not provided, will use default

                    slot)

            // The rest arguments become *one* argument: an array. That is what
            // the flat type says — `#:rest` resolves to a single `(Array %a)`
            // parameter — and the typed tree has to agree with it.
            //
            // It used to hand `TApply` the rest arguments spread flat, N of
            // them against a type with one parameter, and rely on C# `params`
            // to put them back together at the call site. That works only when
            // the callee is emitted as a real `params` method. Alias the
            // function to a value — `(def f list)` — and the callee is a
            // `Func<int[], SchemeList<int>>` field, delegates have no `params`
            // semantics, and `f(1, 2, 3)` fails to compile in C# after passing
            // the type checker. Materializing the array here makes the two
            // spellings the same call. C# still accepts an explicit array for a
            // `params` parameter, so the direct case is unaffected.
            //
            // `LoopLowering` already builds the same node for a tail call into
            // a rest parameter.
            let restArgTypes, restTypedArgs =
                match meta.RestParam with
                | Some _ ->
                    let elemSlot = freshMeta ()

                    for (rt, _) in restArgs do
                        unify env.Registry rt elemSlot

                    let arrayType = TCon("Array", [elemSlot])

                    [ arrayType ],
                    [ ({ Type = arrayType
                         Range = r
                         Node = TArrayMake(restArgs |> List.map snd) }: TypedExpr) ]
                | None ->
                    if not restArgs.IsEmpty then
                        failwithf $"Too many arguments at %s{Lexer.formatPos r}"
                    [], []

            let allFlatTypes = (mandatoryArgs |> List.map fst) @ kwArgTypes @ restArgTypes
            unify env.Registry targetType (TFun(allFlatTypes, retType, demandedEffect env targetType))

            let typedKwArgs =
                keywordArgs |> List.map (fun (n, (_, te)) -> (n, te))

            // Positional args in TApply = mandatory + the rest array (keyword
            // args are separate)
            let positionalTypedArgs =
                (mandatoryArgs |> List.map snd) @ restTypedArgs

            retType,
            { Type = retType
              Range = r
              Node = TApply(typedTarget, positionalTypedArgs, typedKwArgs) }

        | _ ->
            // No FunMeta or no keyword args: simple positional call
            if not keywordArgs.IsEmpty then
                failwithf $"Keyword arguments used on a function without keyword parameter metadata at %s{Lexer.formatPos r}"

            unify
                env.Registry
                targetType
                (TFun(positionalArgs |> List.map fst, retType, demandedEffect env targetType))

            retType,
            { Type = retType
              Range = r
              Node = TApply(typedTarget, positionalArgs |> List.map snd, []) }

    // Deliberately not generalized — see `ELetMono`. The value is inferred
    // first, exactly as `let` does, so the binding keeps its concrete head; only
    // the quantification is dropped.
    | ELetMono(name, value, body, r) ->
        let valType, typedVal = infer env value

        let localEnv =
            addBinding
                name
                { Scheme = Scheme([], [], valType)
                  IsMutable = false }
                env

        let bodyType, typedBody = infer localEnv body

        bodyType,
        { Type = bodyType
          Range = r
          Node = TLet(name, false, noParams, typedVal, typedBody) }

    | ELet(name, isFun, args, typeAnn, value, body, r) ->
        // For a function-shaped binding `typeAnn` is the *return* type, and is
        // already accounted for by the shape; for a value binding it is the
        // binding's own type.
        let shape = if isFun then Some(localFunShape env args typeAnn) else None

        let valType, typedVal, localFun =
            match shape with
            | Some s ->
                let lambda, lf = inferLocalFunBody env s args r value
                s.FunType, lambda, lf
            | None ->
                // When the binding has a type annotation, resolve it first and
                // pass it down as the expected type.  For list / vec literals
                // this enables per-element constructor injection before any
                // element-level unification can fail.
                let t, typed =
                    match typeAnn with
                    | Some tAnn ->
                        let expectedType = resolveTypeAnnotation env.Registry tAnn
                        inferChecked expectedType env value
                    | None -> infer env value

                match typeAnn with
                | Some tAnn -> unify env.Registry t (resolveTypeAnnotation env.Registry tAnn)
                | None -> ()

                t, typed, noParams

        // Only a *function*-shaped local binding is generalized.
        //
        // The value restriction would admit more — a bare lambda is a syntactic
        // value — but C# is the limit here rather than soundness. A local
        // binding that is not a function is emitted as an ordinary local
        // variable, and neither a delegate nor a `SchemeList<T>` local can be
        // generic: there is nowhere for the type parameter to be declared.
        // Quantifying one emitted `Func<T_t__1, T_t__1> id = ...` naming a
        // parameter the enclosing method never declared.
        //
        // A local `defun` is not affected: it becomes a C# local function,
        // which may have type parameters of its own.
        let scheme =
            if isFun then generalizeLocal env valType
            else Scheme([], [], valType)

        let localEnv = addBinding name { Scheme = scheme; IsMutable = false } env

        // Keyword and rest metadata travels with the name, or a call that
        // passes a keyword argument — or omits an optional one — has nothing to
        // resolve against and is checked against the flat arrow instead.
        let localEnv =
            match shape with
            | Some s -> { localEnv with FunMetas = Map.add name s.Meta localEnv.FunMetas }
            | None -> localEnv

        let bodyType, typedBody = infer localEnv body

        bodyType,
        { Type = bodyType
          Range = r
          Node = TLet(name, isFun, localFun, typedVal, typedBody) }

    | ELetRec(bindings, body, r) ->
        let bindingMetas = bindings |> List.map (fun (n, _, _, _, _) -> n, freshMeta ())

        // Every member's shape is read before any body is checked. Two reasons,
        // and they are the same reason at two scales: a mutually recursive
        // group's earlier member has already said what this one's arguments
        // are, and a body checked against bare metavariables is fatal rather
        // than merely imprecise for an associated type — a projection needs a
        // concrete head and cannot be deferred into a unification. A recursive
        // call that passes a keyword argument needs the `FunMeta` for the same
        // reason it does at the top level.
        let shapes =
            bindings
            |> List.map (fun (_, isFun, args, typeAnn, _) ->
                if isFun then Some(localFunShape env args typeAnn) else None)

        List.iter2
            (fun shape (_, expected) ->
                match shape with
                | Some(s: LocalFunShape) -> unify env.Registry s.FunType expected
                | None -> ())
            shapes
            bindingMetas

        let withMetas (start: Env) =
            List.fold2
                (fun (acc: Env) shape (n, _) ->
                    match shape with
                    | Some(s: LocalFunShape) -> { acc with FunMetas = Map.add n s.Meta acc.FunMetas }
                    | None -> acc)
                start
                shapes
                bindingMetas

        let recEnv =
            bindingMetas
            |> List.fold
                (fun acc (n, t) ->
                    addBinding
                        n
                        { Scheme = Scheme([], [], t)
                          IsMutable = false }
                        acc)
                env
            |> withMetas

        let typedBindings =
            List.zip3 bindings shapes bindingMetas
            |> List.map (fun ((name, isFun, args, typeAnn, expr), shape, (_, expectedType)) ->
                let valType, typedVal, localFun =
                    match shape with
                    | Some s ->
                        let lambda, lf = inferLocalFunBody recEnv s args r expr
                        s.FunType, lambda, lf
                    | None ->
                        let t, typed = infer recEnv expr

                        // A value binding's annotation is its own type. A
                        // function's is its return type, and the shape has
                        // already unified it with the body.
                        match typeAnn with
                        | Some tAnn -> unify env.Registry t (resolveTypeAnnotation env.Registry tAnn)
                        | None -> ()

                        t, typed, noParams

                unify env.Registry valType expectedType
                name, isFun, localFun, typedVal)

        let finalEnv =
            List.zip bindings bindingMetas
            |> List.fold
                // Function-shaped members only, for the reason `ELet` gives:
                // anything else becomes a plain local and cannot carry a type
                // parameter.
                (fun acc ((_, isFun, _, _, _), (n, t)) ->
                    addBinding
                        n
                        { Scheme = (if isFun then generalizeLocal recEnv t else Scheme([], [], t))
                          IsMutable = false }
                        acc)
                env
            |> withMetas

        let bodyType, typedBody = infer finalEnv body

        bodyType,
        { Type = bodyType
          Range = r
          Node = TLetRec(typedBindings, typedBody) }

    | ELetMutable(name, typeAnn, value, body, r) ->
        let valType, typedVal = infer env value
        
        match typeAnn with
        | Some tAnn ->
            let expectedType = resolveTypeAnnotation env.Registry tAnn
            unify env.Registry valType expectedType
        | None -> ()

        // Deliberately not generalized. A mutable binding is a cell, and a
        // *polymorphic* cell is the value restriction's classic hole: each use
        // would instantiate a fresh variable, so a `set!` at one type and a read
        // at another would both check and disagree about what is in there. If it
        // can be assigned, its type has to be settled.
        let localEnv =
            addBinding
                name
                { Scheme = Scheme([], [], valType)
                  IsMutable = true }
                env

        let bodyType, typedBody = infer localEnv body

        bodyType,
        { Type = bodyType
          Range = r
          Node = TLetMutable(name, typedVal, typedBody) }

    | ESet(name, value, r) ->
        let valType, typedVal = infer env value
        let binding = lookup env name

        if not binding.IsMutable then
            failwithf $"Type Error: Cannot mutate immutable variable '%s{name}' at %s{Lexer.formatPos r}"

        let targetType, _, _ = instantiate env.Registry binding.Scheme
        unify env.Registry valType targetType

        TypeConstants.unitType,
        { Type = TypeConstants.unitType
          Range = r
          Node = TSet(name, typedVal) }

    | EIf(cond, trueBranch, falseBranch, r) ->
        let condType, tCond = infer env cond
        unify env.Registry condType TypeConstants.boolType
        let trueType, tTrue = infer env trueBranch
        let falseType, tFalse = infer env falseBranch
        unify env.Registry trueType falseType

        trueType,
        { Type = trueType
          Range = r
          Node = TIf(tCond, tTrue, tFalse) }

    | EWhen(cond, body, negated, r) ->
        let condType, tCond = infer env cond
        unify env.Registry condType TypeConstants.boolType

        // The body is evaluated for its effect and its value thrown away, so it
        // constrains nothing: there is no other arm for it to agree with, and
        // the form itself yields nothing.
        let _, tBody = infer env body

        TypeConstants.unitType,
        { Type = TypeConstants.unitType
          Range = r
          Node = TWhen(tCond, tBody, negated) }

    | EQuotedSymbol(sym, r) ->
        let t = TypeConstants.symbolType

        t,
        { Type = t
          Range = r
          Node = TSymbol sym }

    | EChar(c, r) ->
        let t = TypeConstants.charType

        t,
        { Type = t
          Range = r
          Node = TChar c }

    | EBool(b, r) ->
        let t = TypeConstants.boolType

        t,
        { Type = t
          Range = r
          Node = TBool b }

    | EKeyword(kw, r) ->
        let t = TypeConstants.keywordType

        t,
        { Type = t
          Range = r
          Node = TKeyword kw }

    | ETuple(exprs, r) ->
        let typedExprs = exprs |> List.map (infer env)
        let tupleType = TTuple(typedExprs |> List.map fst)

        tupleType,
        { Type = tupleType
          Range = r
          Node = TTupleMake(typedExprs |> List.map snd) }

    | ELetTuple(names, value, body, r) ->
        let valType, typedVal = infer env value
        let elementMetas = names |> List.map (fun _ -> freshMeta ())
        unify env.Registry valType (TTuple elementMetas)

        let localEnv =
            List.zip names elementMetas
            |> List.fold
                (fun acc (n, t) ->
                    addBinding
                        n
                        { Scheme = Scheme([], [], t)
                          IsMutable = false }
                        acc)
                env

        let bodyType, typedBody = infer localEnv body

        bodyType,
        { Type = bodyType
          Range = r
          Node = TLetTuple(names, typedVal, typedBody) }

    | EList(exprs, r) ->
        let elementType = freshMeta ()

        let typedExprs =
            exprs
            |> List.map (fun e ->
                let t, te = infer env e
                joinLiteralElement env r "list" exprs elementType t
                te)

        let listType = TCon("List", [ elementType ])

        listType,
        { Type = listType
          Range = r
          Node = TListMake typedExprs }

    | EVec(exprs, r) ->
        let elementType = freshMeta ()

        let typedExprs =
            exprs
            |> List.map (fun e ->
                let t, te = infer env e
                joinLiteralElement env r "vec" exprs elementType t
                te)

        let vecType = TCon("Vec", [ elementType ])

        vecType,
        { Type = vecType
          Range = r
          Node = TVecMake typedExprs }

    | ETryFinally(body, cleanup, r) ->
        let bodyType, tBody = infer env body
        let _, tCleanup = infer env cleanup

        bodyType,
        { Type = bodyType
          Range = r
          Node = TTryFinally(tBody, tCleanup) }

    // `(try body #:catch (E1 E2 ...))`. The listed failures become values; the
    // rest keep propagating, exactly as `#:exceptions` does at an import — this
    // is the same facility with the list written at the use rather than at the
    // declaration.
    | ETryCatch(body, exceptions, r) ->
        let where = Lexer.formatPos r
        checkExceptionTypes where exceptions

        let bodyType, tBody = infer env body
        let resultType = wrapForeignExceptions exceptions bodyType

        resultType,
        { Type = resultType
          Range = r
          Node = TTryCatch(tBody, exceptions) }

    | ESeq(body, r) ->
        let elemType = freshMeta ()

        // The body is run for its yields; whatever its last form evaluates to is
        // discarded, exactly as in `when`. A sequence's *value* is its elements,
        // so there is nothing for the body's own type to agree with.
        let _, tBody = infer (withSeqElement elemType env) body

        let seqType = TCon("Seq", [ elemType ])

        seqType,
        { Type = seqType
          Range = r
          Node = TSeq tBody }

    // `(bjo (f x y))`. The call is checked exactly as it would be if it were
    // written where it stands — same arguments, same arity, same overloads —
    // and only its *result* is repackaged as a promise. Nothing about spawning
    // changes what the call means, which is the point of the direct style.
    //
    // The body may be a call to a bjoroutine or to an ordinary function; both
    // are useful and both compile to the same thing. `ColourCheck` allows a
    // yield point in there whatever the enclosing colour, because the spawned
    // body becomes an async lambda of its own.
    | EBjo(call, r) ->
        let resultType, tCall = infer env call

        let promiseType = TCon("Promise", [ resultType ])

        promiseType,
        { Type = promiseType
          Range = r
          Node = TBjo tCall }

    // `(task->event (fetch url))`. The event of making an async .NET call.
    //
    // The operand is *not* inferred as an expression, which is the whole point
    // of the special form: everywhere else `(fetch url)` means "await this",
    // and here it has to mean "hand me the task, unstarted". So the call is
    // taken apart and the pieces are re-resolved — same overload rules, same
    // arguments, one difference in what comes out.
    //
    // §7.3, and the reason `Cancellable` rather than `FromTask` is the only
    // form the language can reach: a task handed over already running cannot be
    // withdrawn from a `choose`, so losing would drop the result and leave the
    // work going. Here the branch owns a token, and losing cancels it.
    | ETaskEvent(call, r) ->
        let where = Lexer.formatPos r

        let name, args =
            match call with
            | EApp(EIdent(n, _), a, _) -> n, a
            | _ ->
                failwithf
                    $"Type Error at %s{where}: task->event takes a call to a method imported #:async. To turn a bjoroutine into an event, spawn it and join the promise — (promise-join (bjo (f x))) — though note that losing a choose on a join stops you listening without stopping the work."

        let info =
            match Map.tryFind name env.Registry.ClrExterns with
            | Some i when not (Map.containsKey name env.Bindings) -> i
            | _ ->
                failwithf
                    $"Type Error at %s{where}: '%s{name}' is not a method imported by import/extern, so task->event has no task to make an event of. For a bjoroutine, use (promise-join (bjo (%s{name} ...))) instead."

        if info.Kind <> ExternMethod then
            failwithf
                $"Type Error at %s{where}: '%s{name}' reads or writes the property '%s{info.ClrType}.%s{info.MemberName}', which produces no task. task->event takes a call to a method imported #:async."

        if not info.IsAsync then
            failwithf
                $"Type Error at %s{where}: '%s{name}' names '%s{info.ClrType}.%s{info.MemberName}', which is imported without #:async, so calling it produces no task to wait for. An ordinary .NET call is made where it is written and there is nothing to race."

        // The branch's own token is what makes losing mean something. Without a
        // parameter to put it in there is no difference between this and
        // `FromTask`, which §7.3 keeps out of the language on purpose.
        if info.Uncancellable then
            failwithf
                $"Type Error at %s{where}: '%s{name}' is imported #:uncancellable, so a losing choose branch could not stop it — the work would carry on with nobody listening, which is exactly what task->event exists to prevent. Await it directly instead, or find an overload that takes a CancellationToken."

        let clrType = DotNetInterop.resolveType $" at %s{where}" info.ClrType
        let allTypedArgs = args |> List.map (infer env)

        // An instance member's receiver is the first argument here as it is
        // everywhere else. It is evaluated where the form stands, like the other
        // operands, and only the *call* is deferred to the sync.
        let receiver, typedArgs =
            if info.IsInstance then
                match allTypedArgs with
                | (_, recv) :: rest ->
                    Some(reconcileForeignArgs env.Registry [ recv ] [ TCon(info.ClrType, []) ] |> List.head), rest
                | [] ->
                    failwithf
                        $"Type Error at %s{where}: '%s{name}' names the instance method '%s{info.ClrType}.%s{info.MemberName}', so its first argument is the object to call it on, but it was given none."
            else
                None, allTypedArgs

        let argTypes = typedArgs |> List.map fst

        if not (DotNetInterop.hasTokenOverload (not info.IsInstance) clrType info.MemberName (Some(argTypes.Length + 1))) then
            failwithf
                $"Type Error at %s{where}: '%s{info.ClrType}.%s{info.MemberName}' has no overload taking these %d{argTypes.Length} argument(s) and a System.Threading.CancellationToken, so this branch would have no way to stop the work it started."

        let resolved =
            resolveExternMethod where info clrType (argTypes @ [ DotNetInterop.cancellationTokenType ])

        // §7.2's third rule, enforced where it bites. A `ValueTask` may be
        // consumed exactly once, so it cannot become an event: the conversion
        // would have to call `.AsTask()` first, which allocates the thing the
        // `ValueTask` existed to avoid.
        if DotNetInterop.isValueTask resolved.RawReturnType then
            failwithf
                $"Type Error at %s{where}: '%s{info.ClrType}.%s{info.MemberName}' returns a ValueTask, which may only be consumed once and therefore cannot become an event. Call it directly — awaiting a ValueTask is fine and is what it is for."

        let awaited =
            match DotNetInterop.awaitedResultType resolved.RawReturnType with
            | Some t -> t
            | None ->
                failwithf
                    $"Type Error at %s{where}: '%s{info.ClrType}.%s{info.MemberName}' returns %s{resolved.RawReturnType.Name}, which is not a task."

        let visibleParams = resolved.ParameterTypes |> List.truncate (resolved.ParameterTypes.Length - 1)
        let coercedArgs = reconcileForeignArgs env.Registry (typedArgs |> List.map snd) visibleParams

        let declaredParams =
            match receiver with
            | Some _ -> TCon(info.ClrType, []) :: visibleParams
            | None -> visibleParams

        match info.DeclaredType with
        | Some declared -> unify env.Registry declared (tfun declaredParams awaited)
        | None -> ()

        // A non-generic `Task` carries no result, and `Result<E, void>` is not
        // a type C# has — so the event carries the unit, exactly as a `void`
        // call's `#:exceptions` wrapper does.
        let awaitIsVoid = awaited = TypeConstants.voidType
        let payload = if awaitIsVoid then TypeConstants.unitType else awaited

        // Failure is a value here for the same reason it is at a join: this
        // runs at sync time, on the fiber's stack rather than on the one that
        // completed the task, and a raise there would land in the wrong place.
        // Cancellation arrives as one of those values — a losing branch's
        // `Err` is a `TaskCanceledException` nobody ever looks at.
        let eventType =
            TCon("Event", [ TCon("Result", [ TCon("System.Exception", []); payload ]) ])

        eventType,
        { Type = eventType
          Range = r
          Node = TTaskEvent(receiver, resolved.DeclaringType, info.MemberName, coercedArgs, payload, awaitIsVoid) }

    | EYield(value, r) ->
        let elemType = currentSeqElement env "yield" r
        let valueType, tValue = infer env value
        unify env.Registry valueType elemType

        TypeConstants.unitType,
        { Type = TypeConstants.unitType
          Range = r
          Node = TYield tValue }

    | EYieldFrom(source, r) ->
        let elemType = currentSeqElement env "yield-from" r
        let sourceType, tSource = infer env source
        unify env.Registry sourceType (TCon("Seq", [ elemType ]))

        TypeConstants.unitType,
        { Type = TypeConstants.unitType
          Range = r
          Node = TYieldFrom tSource }


    | EMatch(target, clauses, r) ->
        let targetType, typedTarget = infer env target
        let returnType = freshMeta ()

        let typedClauses =
            clauses
            |> List.map (fun (pat, guard, body) ->
                let typedPat, boundVars = checkPattern env targetType pat

                let boundEnv =
                    Map.fold
                        (fun acc n t ->
                            addBinding
                                n
                                { Scheme = Scheme([], [], t)
                                  IsMutable = false }
                                acc)
                        env
                        boundVars

                let typedGuard =
                    match guard with
                    | Some g ->
                        let gType, tg = infer boundEnv g
                        unify env.Registry gType TypeConstants.boolType
                        Some tg
                    | None -> None

                let bodyType, typedBody = infer boundEnv body

                unify env.Registry bodyType returnType

                { Pattern = typedPat
                  Guard = typedGuard
                  Body = typedBody }
                : TMatchClause)

        returnType,
        { Type = returnType
          Range = r
          Node = TMatch(typedTarget, typedClauses) }

    | EGetField(targetExpr, field, r) ->
        let targetType, typedTarget = infer env targetExpr
        let recordTypeName = recordTypeOfField env.Registry targetType field r

        let instantiatedRecordType, _, expectedFieldsInstantiated =
            instantiateRecord env.Registry recordTypeName

        unify env.Registry targetType instantiatedRecordType

        let fieldType =
            match Map.tryFind field expectedFieldsInstantiated with
            | Some t -> t
            | None -> failwithf $"Type Error: Field '%s{field}' does not belong to record '%s{recordTypeName}' at %s{Lexer.formatPos r}"

        fieldType,
        { Type = fieldType
          Range = r
          Node = TGetField(typedTarget, field) }

    | ERecordUpdate(targetName, fields, r) ->
        let targetBinding = lookup env targetName
        let targetType, _, _ = instantiate env.Registry targetBinding.Scheme
        
        let recordTypeName =
            if fields.IsEmpty then
                failwithf $"Type Error at %s{formatPos r}: a record-set has to update at least one field."

            recordTypeOfField env.Registry targetType (fst fields.Head) r


        let instantiatedRecordType, _, expectedFieldsInstantiated =
            instantiateRecord env.Registry recordTypeName

        unify env.Registry targetType instantiatedRecordType

        let typedFields =
            fields |> List.map (fun (name, expr) ->
                let exprType, typedExpr = infer env expr
                match Map.tryFind name expectedFieldsInstantiated with
                | Some expectedType -> unify env.Registry exprType expectedType
                | None -> failwithf $"Type Error: Field '%s{name}' does not belong to record '%s{recordTypeName}' at %s{Lexer.formatPos r}"
                name, typedExpr)

        targetType,
        { Type = targetType
          Range = r
          Node = TRecordUpdate(targetName, typedFields) }

    // `(record-set! r (field value) ...)` — the write in place.
    //
    // Shaped like `ERecordUpdate` above and checked like it, plus the two
    // questions a write has that a copy does not: is this field writable, and
    // is this module allowed to write it.
    | ERecordSet(targetName, fields, r) ->
        let targetBinding = lookup env targetName
        let targetType, _, _ = instantiate env.Registry targetBinding.Scheme

        // Non-empty by construction — the parser refuses a `record-set!` that
        // names no field — so the head is safe to resolve the type from.
        let recordTypeName = recordTypeOfField env.Registry targetType (fst fields.Head) r

        let instantiatedRecordType, _, expectedFieldsInstantiated =
            instantiateRecord env.Registry recordTypeName

        unify env.Registry targetType instantiatedRecordType

        // A field is writable only where it was declared. The check is on the
        // *record's* module rather than on the binding's: a value of a foreign
        // record type reaches here by every ordinary route — an argument, a
        // field of something local — and none of them may write it.
        if not (declaredHere env.CurrentModule recordTypeName) then
            let shown = Naming.showTypeName recordTypeName

            failwithf
                $"Type Error at %s{formatPos r}: '%s{shown}' was declared in another module, so this one may not write its fields. A module that means its state to be written from outside exports functions that write it."

        let mutableFields = mutableFieldsOf env.Registry recordTypeName

        let typedFields =
            fields |> List.map (fun (name, expr) ->
                let exprType, typedExpr = infer env expr

                match Map.tryFind name expectedFieldsInstantiated with
                | Some expectedType -> unify env.Registry exprType expectedType
                | None ->
                    failwithf
                        $"Type Error at %s{formatPos r}: field '%s{name}' does not belong to record '%s{Naming.showTypeName recordTypeName}'."

                if not (List.contains name mutableFields) then
                    let writable =
                        if mutableFields.IsEmpty then "It has no mutable fields."
                        else "Its mutable fields are: " + String.concat ", " mutableFields + "."

                    failwithf
                        $"Type Error at %s{formatPos r}: field '%s{name}' of '%s{Naming.showTypeName recordTypeName}' is not mutable, so it cannot be written in place. Declare it (: %s{name} <type> #:mutable), or use record-set for a copy. %s{writable}"

                name, typedExpr)

        // Void, as every other write in the language is. The value it might
        // have handed back — the record — is the same object either way, so
        // returning it would only invite `(def r2 (record-set! r ...))` to read
        // as though it were a copy.
        TypeConstants.unitType,
        { Type = TypeConstants.unitType
          Range = r
          Node = TRecordSet(targetName, typedFields) }

    | ECast(targetTypeAnnotation, expr, r) ->
        let targetType = resolveTypeAnnotation env.Registry targetTypeAnnotation
        let exprType, typedExpr = infer env expr
        targetType,
        { Type = targetType
          Range = r
          Node = TCast(typedExpr, targetType) }

/// Infer `expr` expecting it to have type `expected`, enabling constructor
/// injection for list and vec literals.  For any other expression shape the
/// call degrades to a plain `infer` so the annotation is unified afterwards
/// by the caller as usual.
/// Checks a local function's body and its keyword defaults against the shape
/// already read off its argument list.
///
/// The order is the one a top-level `defun` uses: the body first, in a scope
/// holding every parameter, and then each default in the *mandatory*-argument
/// scope extended by the keyword parameters before it. A default may therefore
/// name an earlier parameter and not a later one, which is the only order that
/// can be evaluated.
and private inferLocalFunBody
    (env: Env)
    (shape: LocalFunShape)
    (args: DefunArg list)
    (r: Range)
    (value: Expr)
    : TypedExpr * LocalFun =

    let bind name t acc =
        addBinding name { Scheme = Scheme([], [], t); IsMutable = false } acc

    let envWithMandatory = shape.Mandatory |> List.fold (fun acc (n, t) -> bind n t acc) env

    let bodyEnv =
        shape.Keywords |> List.fold (fun acc (n, t, _) -> bind n t acc) envWithMandatory

    let bodyEnv =
        match shape.Rest with
        | Some(n, t) -> bind n (TCon("Array", [ t ])) bodyEnv
        | None -> bodyEnv

    let bodyType, typedBody = infer bodyEnv value
    unify env.Registry bodyType shape.RetType

    let typedKeywords, _ =
        shape.Keywords
        |> List.fold
            (fun (acc, currentEnv) (n, t, defaultExpr) ->
                let defaultType, typedDefault = infer currentEnv defaultExpr
                unify env.Registry defaultType t
                acc @ [ n, t, typedDefault ], bind n t currentEnv)
            ([], envWithMandatory)

    let paramNames = allArgNames args

    { Type = shape.FunType
      Range = r
      Node = TLambda(paramNames, typedBody) },
    { Params = paramNames
      KeywordArgs = typedKeywords
      RestArg = shape.Rest }

and private inferChecked (expected: HMType) (env: Env) (expr: Expr) : HMType * TypedExpr =
    match expr, prune env.Registry expected with
    | EList(exprs, r), TCon("List", [ elemTy ]) ->
        let typedExprs = exprs |> List.map (inferAndMaybeInject elemTy env)
        TCon("List", [ elemTy ]),
        { Type = TCon("List", [ elemTy ]); Range = r; Node = TListMake typedExprs }
    | EVec(exprs, r), TCon("Vec", [ elemTy ]) ->
        let typedExprs = exprs |> List.map (inferAndMaybeInject elemTy env)
        TCon("Vec", [ elemTy ]),
        { Type = TCon("Vec", [ elemTy ]); Range = r; Node = TVecMake typedExprs }
    | _ ->
        infer env expr

/// Check one element of a literal against the type its position expects,
/// injecting a union constructor around it where the expectation is a union.
///
/// Two ways to pick that constructor, and which applies is decided by the
/// element:
///
/// *Shape* first. A literal — a nested list, a string, a symbol, a number —
/// selects by what it is written as, against the *head* of each case's payload.
/// This has to happen before the element is inferred, because for a nested
/// heterogeneous literal inferring is what fails. The chosen payload is then
/// pushed back down through `inferChecked`, which calls this function again for
/// each of that literal's own elements. Recursion terminates because the
/// expression strictly shrinks; mutually recursive unions need nothing extra,
/// since every step is a fresh lookup in `Registry.Unions`.
///
/// *Type* otherwise. An unquoted `,value` has no shape to be read — a
/// `(-> string string)` is a candidate for `ProcFn` because of its type and
/// nothing else — so it is inferred first and matched against the payloads
/// afterwards.
and private inferAndMaybeInject (expectedElem: HMType) (env: Env) (expr: Expr) : TypedExpr =
    let pe = prune env.Registry expectedElem

    /// The typed constructor application, around an already typed payload.
    let wrapInCtor (ctorName: string) (payloadTy: HMType) (payload: TypedExpr) : TypedExpr =
        let ctorType = tfun [ payloadTy ] pe
        let ctorExpr: TypedExpr = { Type = ctorType; Range = payload.Range; Node = TIdent(ctorName, []) }
        { Type = pe; Range = payload.Range; Node = TApply(ctorExpr, [ payload ], []) }

    match pe, literalPayloadHeads expr with
    | TCon(unionName, typeArgs), Some heads when Map.containsKey unionName env.Registry.Unions ->
        // The literal's own range, not the range of whatever list it is written
        // in: the constructor that cannot be chosen is this one's.
        let reportAt () = Lexer.formatPos (exprRange expr)
        let shape = literalShapeName expr
        // Shown by key, like every other type name in a diagnostic: which
        // module's union this is may be the whole of what the reader is
        // missing.
        let shownUnion = Naming.showTypeName unionName

        match env.Registry.CasesByPayloadShape unionName typeArgs heads with
        | [ (ctorName, payloadTy) ] ->
            let payloadType, typedPayload = inferChecked payloadTy env expr
            unify env.Registry payloadType payloadTy
            wrapInCtor ctorName payloadTy typedPayload
        | [] ->
            failwithf
                $"Type Error at %s{reportAt ()}: no case of the union %s{shownUnion} carries a %s{shape}, so this literal cannot be one. A literal written where a union is expected is elaborated into the case that holds it, and %s{shownUnion} has %s{describeUnionCases env.Registry unionName}."
        | many ->
            let names = many |> List.map (fst >> Naming.showTypeName) |> orList

            failwithf
                $"Type Error at %s{reportAt ()}: this %s{shape} literal could be injected into %s{names}, and nothing here says which. They are all cases of %s{shownUnion} that carry a %s{shape}, and only the payload's head constructor is compared against the literal — never its arguments — so the literal itself cannot tell them apart. Mark the case a literal means with #:literal where %s{shownUnion} is declared, or write the constructor around it here."
    | _ ->
        // Type-directed. The element is inferred first, and `CandidateCases`
        // then asked — speculatively, so it may not bind anything — whether one
        // constructor's payload matches what came back.
        //
        // `inferChecked` rather than `infer`, because the expectation is worth
        // pushing even when it names no union: at `(List ProcList)` the element
        // is a list of its own, and its elements are what the unions are at.
        let elemTy, te = inferChecked pe env expr
        let pg = prune env.Registry elemTy

        match pe with
        | TCon(unionName, typeArgs) when Map.containsKey unionName env.Registry.Unions ->
            match env.Registry.CandidateCases unionName typeArgs pg with
            | [ (ctorName, [ payloadTy ]) ] ->
                // Exactly one constructor's payload matches. Unify the element
                // against it — which resolves any metavariable left on either
                // side — and wrap.
                unify env.Registry elemTy payloadTy
                wrapInCtor ctorName payloadTy te
            | _ ->
                // Zero or ambiguous matches — unify directly and let the type
                // error (if any) be reported normally.
                unify env.Registry elemTy expectedElem
                te
        | _ ->
            unify env.Registry elemTy expectedElem
            te

// --- DECLARATION CHECKING ---

/// Every type name a declaration mentions, resolved to the name it stands for.
///
/// The declaration itself outlives inference: the code generator emits a field
/// or a payload by the name written in it, and the metadata publishes it as
/// source text. Both read those names literally, so a payload written as the
/// bare `Crate` becomes `kitchen__Crate` here — where the table that knows
/// which `Crate` is meant is still at hand. Type variables are left alone.
let rec private qualifyFTypeNames (registry: TraitRegistry) (ftype: FType) : FType =
    let qualify (n: string) = if n.StartsWith "'" then n else originalName registry n
    let go = qualifyFTypeNames registry

    match ftype with
    | TName(n, r) -> TName(qualify n, r)
    | TApp(n, args, r) -> TApp(qualify n, List.map go args, r)
    | TArrow(mandatory, keywords, restOpt, ret, colour, r) ->
        TArrow(
            List.map go mandatory,
            keywords |> List.map (fun (k, t) -> k, go t),
            Option.map go restOpt,
            go ret,
            colour,
            r
        )

/// Registers a `type` or `type-rec` group, and hands back the declarations as
/// everything downstream will see them: named by their keys.
///
/// A declared type's identity is the module that declared it plus its name
/// (`Naming.typeKey`), so two modules may each declare a `Banana` and mean two
/// types. Nothing else has to change to make that work — `Implementations`,
/// `Unions`, `InlineMethods` and the impl class C# is emitted as are all keyed
/// on a type's *name*, and the name is now unique.
///
/// The bare name is what source goes on writing, here and in anything that
/// imports this module plainly. It is filed as a spelling in the same table an
/// import modifier's spellings go in, which `originalName` resolves before any
/// registry is consulted.
let registerTypeDefs (isRec: bool) (typeDefs: TypeDef list) (env: Env) : Env * TypeDef list =
    let key (name: string) = Naming.typeKey env.CurrentModule name

    let withSpelling (kind: AliasKind) (name: string) (registry: TraitRegistry) =
        let keyed = key name

        if keyed = name then
            registry
        else
            { registry with
                ImportAliases =
                    Map.add
                        name
                        { OriginModule = env.CurrentModule
                          OriginalName = keyed
                          Kind = kind }
                        registry.ImportAliases }

    // The whole group's type names before any of its bodies: a `type-rec` names
    // its siblings, and a payload written with a bare name has to reach the
    // sibling's key rather than a same-named type declared elsewhere.
    let preRegistry =
        typeDefs
        |> List.fold
            (fun (reg: TraitRegistry) td ->
                withSpelling AliasType td.Name { reg with LocalTypes = Set.add (key td.Name) reg.LocalTypes })
            env.Registry

    let mutable finalRegistry = preRegistry
    let mutable finalBindings = env.Bindings
    let keyedDefs = ResizeArray<TypeDef>()

    for td in typeDefs do
        // A constructor follows its type, in expression and in pattern
        // position alike, so its bare name is a spelling in exactly the same
        // sense. Registered before the payloads are resolved, because a case
        // may carry a value of the union it belongs to.
        match td.Kind with
        | Union cases ->
            for case in cases do
                let caseName =
                    match case with
                    | SimpleCase(n, _) -> n
                    | DataCase(n, _, _, _) -> n

                finalRegistry <- withSpelling AliasConstructor caseName finalRegistry
        | _ -> ()

        let name = key td.Name

        let keyedKind =
            match td.Kind with
            | Alias ftype -> Alias(qualifyFTypeNames finalRegistry ftype)
            | Record(fields, isStruct) ->
                Record(fields |> List.map (fun f -> { f with Type = qualifyFTypeNames finalRegistry f.Type }), isStruct)
            | Union cases ->
                cases
                |> List.map (function
                    | SimpleCase(n, r) -> SimpleCase(key n, r)
                    | DataCase(n, types, marked, r) ->
                        DataCase(key n, types |> List.map (qualifyFTypeNames finalRegistry), marked, r))
                |> Union
            // Left as written. A hidden member is a name in a diagnostic and
            // nothing else, so keying it would only make the message harder to
            // read than the source the reader is looking at.
            | Opaque members -> Opaque members

        let td = { td with Name = name; Kind = keyedKind }
        keyedDefs.Add td

        let tArgs = td.TypeArgs |> List.map (fun a -> if a.StartsWith("'") then a else "'" + a)
        let hmArgs = tArgs |> List.map TVar
        let parentType = TCon(td.Name, hmArgs)

        match td.Kind with
        | Alias ftype ->
            let resolved = resolveTypeAnnotation finalRegistry ftype
            finalRegistry <- { finalRegistry with Aliases = Map.add td.Name (tArgs, resolved) finalRegistry.Aliases }
        | Record(fields, _) ->
            let resolvedFields = fields |> List.map (fun f -> f.Name, resolveTypeAnnotation finalRegistry f.Type)
            finalRegistry <- { finalRegistry with Records = Map.add td.Name (tArgs, resolvedFields) finalRegistry.Records }

            // Only for a record that has one, so that `Map.tryFind` answering
            // `None` means "nothing here is mutable" rather than "not a record".
            match fields |> List.filter (fun f -> f.Mutable) |> List.map (fun f -> f.Name) with
            | [] -> ()
            | mutableNames ->
                finalRegistry <-
                    { finalRegistry with
                        MutableRecordFields = Map.add td.Name mutableNames finalRegistry.MutableRecordFields }
            for (fName, _) in resolvedFields do
                let owners =
                    Map.tryFind fName finalRegistry.RecordFields |> Option.defaultValue []

                finalRegistry <-
                    { finalRegistry with
                        RecordFields = Map.add fName (owners @ [ td.Name ]) finalRegistry.RecordFields }
        | Union cases ->
            // Collected alongside the constructor bindings so that literal
            // elaboration can ask the inverse question the bindings cannot
            // answer: not "what is this constructor's type?" but "which case of
            // this union could carry this payload?".
            let mutable caseTable = []

            for case in cases do
                let caseName, resolvedArgs, isLiteral =
                    match case with
                    | SimpleCase(n, _) -> n, [], false
                    | DataCase(n, types, marked, _) ->
                        n, types |> List.map (resolveTypeAnnotation finalRegistry), marked
                let schemeArgs = tArgs
                let consScheme =
                    if resolvedArgs.IsEmpty then
                        Scheme(schemeArgs, [], parentType)
                    else
                        Scheme(schemeArgs, [], tfun resolvedArgs parentType)
                finalBindings <- Map.add caseName { Scheme = consScheme; IsMutable = false } finalBindings
                caseTable <- caseTable @ [ (caseName, resolvedArgs, isLiteral) ]

            finalRegistry <- { finalRegistry with Unions = Map.add td.Name (tArgs, caseTable) finalRegistry.Unions }

        // A head and nothing else, which is what an `#:opaque` export arrives
        // as. `LocalTypes` and the name's spelling were registered by the
        // pre-pass above, so the type is nameable, unifiable and a legal impl
        // target; deliberately absent are the `Records`, `Unions` and
        // constructor bindings that would let anything take it apart.
        //
        // Note what this costs the importer nothing to know: the member names
        // go into `HiddenMembers` so that a use of one reports the type it
        // belongs to rather than "no such constructor".
        | Opaque members ->
            finalRegistry <-
                { finalRegistry with
                    OpaqueTypes = Set.add td.Name finalRegistry.OpaqueTypes
                    HiddenMembers =
                        members
                        |> List.fold (fun acc m -> Map.add m td.Name acc) finalRegistry.HiddenMembers }


    { env with Registry = finalRegistry; Bindings = finalBindings }, List.ofSeq keyedDefs

let rec checkDecl (env: Env) (sigs: Map<string, HMType * FType option * (string * string) list>) (decl: Decl) : Env * Map<string, HMType * FType option * (string * string) list> * TDecl list =
    // What module level looks like from inside this declaration: the imports,
    // the prelude and whatever this module has defined so far. Nothing a body
    // binds gets in, because every binder inside one goes through `addBinding`,
    // which does not touch this.
    //
    // It is what an `EResolved` resolves against — a name the compiler wrote,
    // which has to mean what it meant where it was written.
    let env = { env with Resolved = env.Bindings }

    match decl with
    | DSignature(name, ftype, constraints, _) -> env, Map.add name (resolveTypeAnnotation env.Registry ftype, Some ftype, constraints) sigs, []

    | DDef(name, expr, r) ->
        // A declared signature is an expected type, so it is pushed into the
        // value rather than only checked against it afterwards — a list literal
        // needs it while its elements are being inferred, not after. The
        // annotated branch of `let` does the same.
        let declaredType = Map.tryFind name sigs |> Option.map (fun (t, _, _) -> t)

        let exprType, typedExpr =
            match declaredType with
            | Some sigType -> inferChecked sigType env expr
            | None -> infer env expr

        match declaredType with
        | Some sigType -> unify env.Registry exprType sigType
        | None -> ()

        // Trait obligations are discharged before generalization: a scheme must
        // not be built over a constructor that resolution would still have
        // pinned down.
        solvePending env

        // The same value restriction `let` applies. It was missing here, so a
        // module-level `(def c (make-array 1))` was generalized over the element
        // type of a cell that only ever exists once — and could then be written
        // at one type and read at another, with nothing but the code generator's
        // inability to declare a generic static field standing in the way.
        let newEnv =
            addBinding
                name
                { Scheme =
                    if isSyntacticValue env.Registry typedExpr then
                        generalize env exprType
                    else
                        Scheme([], [], exprType)
                  IsMutable = false }
                env

        // Keyword and rest metadata travels with a `def`'s signature too, for
        // the same reason it travels with `extern`'s: `FunMeta` is what carries
        // the *shape* of a call, and the flat function type alone cannot spread
        // arguments. Without this, aliasing a variadic function bound its
        // array form and nothing else — `(def f list)` typechecked, and then
        // `(f 1 2 3)` failed on arity because `f` had no metadata to spread by.
        //
        // Only `defun`, `extern` and imported signatures used to register it,
        // so `def` was the one declaration form where a `#:rest` signature was
        // accepted and then silently meant something narrower.
        let newEnv =
            match Map.tryFind name sigs with
            | Some(_, Some(TArrow(mandatory, keywords, restOpt, _, _, _)), _) when
                restOpt.IsSome || not keywords.IsEmpty
                ->
                let funMeta =
                    { MandatoryCount = mandatory.Length
                      KeywordParams =
                        keywords |> List.map (fun (n, ft) -> n, resolveTypeAnnotation env.Registry ft)
                      RestParam = restOpt |> Option.map (resolveTypeAnnotation env.Registry) }

                { newEnv with FunMetas = Map.add name funMeta newEnv.FunMetas }
            | _ -> newEnv

        newEnv, Map.remove name sigs, [ TDef(name, typedExpr, exprType, r) ]

    | DDefun(name, defunArgs, body, colour, r) ->
        // `defbjo` is the only thing that says a function may suspend. The
        // signature does not: `(: fetch (-> string string))` is what you write
        // for either colour, because an arrow says what a function takes and
        // returns and the definer says how it is called. So the declared type
        // arrives `ESync` and is repainted before anything is unified with it.
        let effect = colourEffect colour

        // Enforce mandatory signature for all top-level defuns except 'main'
        let sigOpt = Map.tryFind name sigs
        if name <> "main" && sigOpt.IsNone then
            failwithf $"Type Error: Function '%s{name}' requires a type signature (: %s{name} ...) at %s{Lexer.formatPos r}"

        // Extract structured keyword/rest info from the raw FType (if available)
        let mandatoryFTypes, keywordFTypes, restFTypeOpt, retFType =
            match sigOpt with
            | Some (_, Some (TArrow(m, kw, rest, ret, _, _)), _) -> m, kw, rest, Some ret
            | _ -> [], [], None, None

        // A plain `->` says nothing about the colour, so it agrees with either
        // definer and `recolour` supplies the answer — that is the normal case
        // and the one the design asks for.
        //
        // `-bjo->` does say something. It is what module metadata publishes,
        // and nothing stops a program writing one by hand, so it has to be
        // held to: a signature that claims a function suspends, over a `defun`
        // that cannot, is a contradiction rather than a decoration.
        match sigOpt with
        | Some(_, Some(TArrow(_, _, _, _, Suspending, sr)), _) when colour = Ordinary ->
            failwithf
                $"Type Error at %s{Lexer.formatPos sr}: the signature of '%s{name}' is written -bjo->, which says calling it is a yield point, but it is defined with defun. Define it with defbjo, or write the signature with ->."
        | _ -> ()

        let sigHMType = sigOpt |> Option.map (fun (t, _, _) -> recolour effect t)

        // Extract explicit trait constraints from the signature
        let explicitConstraints =
            match sigOpt with
            | Some (_, _, constraints) ->
                constraints |> List.map (fun (traitName, varName) ->
                    { TraitName = originalName env.Registry traitName; TargetType = TVar varName })
            | None -> []

        // Match defun args with the signature types
        let mandatoryArgNames = mandatoryNames defunArgs
        let keywordArgDefs =
            defunArgs |> List.choose (function KeywordArg(n, defaultExpr) -> Some(n, defaultExpr) | _ -> None)
        let restArgName =
            defunArgs |> List.tryPick (function RestArg n -> Some n | _ -> None)

        // Resolve mandatory arg types from signature
        let mandatoryTypes =
            if mandatoryFTypes.Length > 0 then
                if mandatoryArgNames.Length <> mandatoryFTypes.Length then
                    failwithf $"Type Error: Function '%s{name}' has %d{mandatoryArgNames.Length} mandatory args but signature specifies %d{mandatoryFTypes.Length} at %s{Lexer.formatPos r}"
                List.zip mandatoryArgNames (mandatoryFTypes |> List.map (resolveTypeAnnotation env.Registry))
            else
                // For main or functions without TArrow signature, use fresh metas
                mandatoryArgNames |> List.map (fun n -> n, freshMeta())

        // A type written at the parameter, `(: x int)`, has to agree with the
        // one the signature gives it. The body-local form has no signature and
        // takes its parameter types from exactly here, so the annotation means
        // the same thing in both places rather than being decoration in one.
        List.iter2
            (fun ann (_, t) ->
                match ann with
                | Some ft -> unify env.Registry t (resolveTypeAnnotation env.Registry ft)
                | None -> ())
            (defunArgs |> List.choose (function MandatoryArg(_, a) -> Some a | _ -> None))
            mandatoryTypes

        // Resolve keyword arg types from signature and type-check defaults
        let keywordTypes =
            keywordArgDefs |> List.map (fun (kwName, _defaultExpr) ->
                let kwType =
                    match keywordFTypes |> List.tryFind (fun (n, _) -> n = kwName) with
                    | Some (_, ft) -> resolveTypeAnnotation env.Registry ft
                    | None ->
                        if sigOpt.IsSome then
                            failwithf $"Type Error: Keyword argument '#:%s{kwName}' not found in signature for '%s{name}' at %s{Lexer.formatPos r}"
                        else freshMeta()
                kwName, kwType)

        // Resolve rest arg type from signature
        let restArgType =
            match restArgName, restFTypeOpt with
            | Some _, Some ft -> Some (resolveTypeAnnotation env.Registry ft)
            | Some _, None ->
                if sigOpt.IsSome then
                    failwithf $"Type Error: Function '%s{name}' has a rest arg but signature has no #:rest at %s{Lexer.formatPos r}"
                else Some (freshMeta())
            | None, _ -> None

        let expectedRetType =
            match retFType with
            | Some ft -> resolveTypeAnnotation env.Registry ft
            | None -> freshMeta()

        // Build the flat function type for unification
        let allArgTypes =
            (mandatoryTypes |> List.map snd) @
            (keywordTypes |> List.map snd) @
            (match restArgType with Some rt -> [TCon("Array", [rt])] | None -> [])
        let funType = TFun(allArgTypes, expectedRetType, effect)

        match sigHMType with
        | Some st -> unify env.Registry funType st
        | None -> ()

        // Keyword/rest metadata has to exist *before* the body is inferred, or a
        // recursive call that passes a keyword argument, or omits an optional
        // one, has no metadata to resolve against: the keyword-application rule
        // would reject it and the flat `funType` would refuse to unify with the
        // shorter argument list.
        let funMeta = {
            MandatoryCount = mandatoryTypes.Length
            KeywordParams = keywordTypes
            RestParam = restArgType
        }

        let recEnv =
            let bound =
                addBinding
                    name
                    { Scheme = Scheme([], [], funType)
                      IsMutable = false }
                    env

            let bound =
                // Implementing a method is not shadowing it. `addBinding` drops
                // the name from `TraitMethodNames`, which is right for a program
                // that binds over a method and wrong for the `def/impl` that
                // supplies one: the body of `(defun (= xs ys) ...)` for lists
                // compares the elements, and that call has to dispatch.
                if bound.ImplMethod = Some name then
                    { bound with TraitMethodNames = Set.add name bound.TraitMethodNames }
                else
                    bound

            { bound with FunMetas = Map.add name funMeta bound.FunMetas }

        // Bind mandatory args
        let envWithMandatory =
            mandatoryTypes
            |> List.fold
                (fun acc (n, t) ->
                    addBinding n { Scheme = Scheme([], [], t); IsMutable = false } acc)
                recEnv

        // Bind keyword args
        let bodyEnv =
            keywordTypes
            |> List.fold
                (fun acc (n, t) ->
                    addBinding n { Scheme = Scheme([], [], t); IsMutable = false } acc)
                envWithMandatory

        // Bind rest arg as Array type
        let bodyEnv =
            match restArgName, restArgType with
            | Some rn, Some rt ->
                addBinding rn { Scheme = Scheme([], [], TCon("Array", [rt])); IsMutable = false } bodyEnv
            | _ -> bodyEnv

        let bodyType, typedBody = infer bodyEnv body
        unify env.Registry bodyType expectedRetType

        // Type-check keyword default expressions
        let typedKeywordArgs, _ =
            List.zip keywordArgDefs keywordTypes
            |> List.fold (fun (typedArgs, currentEnv) ((kwName, defaultExpr), (_, kwType)) ->
                let defaultType, typedDefault = infer currentEnv defaultExpr
                unify env.Registry defaultType kwType
                let nextEnv = addBinding kwName { Scheme = Scheme([], [], kwType); IsMutable = false } currentEnv
                (typedArgs @ [kwName, kwType, typedDefault], nextEnv)
            ) ([], envWithMandatory)

        solvePending env

        let scheme = generalize env funType
        let (Scheme(vars, _, schemeType)) = scheme

        // Collect trait constraints from the body and merge with explicit ones
        let inferredConstraints = collectTraitConstraints env typedBody
        let allConstraints =
            let seen = System.Collections.Generic.HashSet<string * string>()
            [ for c in explicitConstraints @ inferredConstraints do
                let key = (c.TraitName, match c.TargetType with TVar v -> v | _ -> "")
                if seen.Add(key) then yield c ]
        // A constraint may only land on a type variable this signature
        // quantifies.
        //
        // A local binding is generalized over variables of its own, and a
        // constraint collected from the body can land on one of *those*:
        // `(defun (inner y) (+ y y))` inside a generic function acquires `Num`
        // at `inner`'s variable rather than at the enclosing function's.
        // Attached to this scheme anyway it became a C# `where` clause naming a
        // type parameter the method does not declare — `CS0699`, in generated
        // code — and a published signature constraining a variable it never
        // mentions. Nothing compared the two, because nobody wrote either down.
        //
        // Checked before the declaration rule below, which would otherwise ask
        // for a clause naming that same unmentionable variable.
        for c in allConstraints do
            match c.TargetType with
            | TVar v when not (List.contains v vars) ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: '%s{name}' would carry the constraint '%s{c.TraitName}' at a type variable its signature does not mention. A local binding in the body was generalized on its own and picked the constraint up there. Annotate that local's parameter with one of this signature's variables — (defun (helper (: y %%a)) ...) — so the constraint lands where it can be declared."
            | _ -> ()

        // A CLR constraint has to be written down.
        //
        // `Eq` or `->str` at a type variable is a *capability*: an open world,
        // anyone may implement one, and inferring it from the body says no more
        // than the body already said. `Num` is not that. Nothing declared in
        // Bjolang can ever satisfy it, so the constraint is an enumeration of
        // the types .NET ships — a statement about what the type *is*, and this
        // language writes types down. Every top-level `defun` needs a signature
        // for the same reason.
        //
        // Inferred silently it also made a published contract depend on a body:
        // adding a `/` widened a signature with no diff in it, and a partial
        // `(where (Ordered %a))` was completed to include `Num` without a word.
        let undeclared =
            inferredConstraints
            |> List.filter (fun c ->
                (match Map.tryFind c.TraitName env.Registry.Traits with
                 | Some info -> info.ClrConstraint.IsSome
                 | None -> false)
                && not (
                    explicitConstraints
                    |> List.exists (fun d -> d.TraitName = c.TraitName && d.TargetType = c.TargetType)
                ))

        if not undeclared.IsEmpty then
            // The whole clause, explicit ones included, so the message is
            // something to paste rather than something to merge by hand.
            let shown (c: TraitConstraint) =
                let written =
                    match prune env.Registry c.TargetType with
                    | TVar v -> "%" + v.TrimStart('\'')
                    | t -> DotNetInterop.showType t

                $"(%s{c.TraitName} %s{written})"

            let clause =
                (explicitConstraints @ undeclared) |> List.map shown |> String.concat " "

            let needs = undeclared |> List.map shown |> String.concat " and "

            failwithf
                $"Type Error at %s{Lexer.formatPos r}: '%s{name}' needs %s{needs}, which its signature does not declare. A constraint on a .NET interface says which types this *is* rather than what they can do, and nothing written in Bjolang can ever satisfy one — so it belongs in the signature. Write:\n  (: %s{name} ... (where %s{clause}))"

        let schemeWithConstraints = Scheme(vars, allConstraints, schemeType)

        let finalEnv =
            addBinding
                name
                { Scheme = schemeWithConstraints
                  IsMutable = false }
                env
        let finalEnv = { finalEnv with FunMetas = Map.add name funMeta finalEnv.FunMetas }

        let restArgInfo =
            match restArgName, restArgType with
            | Some rn, Some rt -> Some(rn, rt)
            | _ -> None

        let decl = TDefun(name, vars, mandatoryTypes, typedKeywordArgs, restArgInfo, expectedRetType, effect, typedBody, r)
        finalEnv, Map.remove name sigs, [ decl ]

    | DDefTuple(names, expr, r) ->
        let exprType, typedExpr = infer env expr
        let elementMetas = names |> List.map (fun _ -> freshMeta ())
        unify env.Registry exprType (TTuple elementMetas)
        solvePending env

        let newEnv =
            List.zip names elementMetas
            |> List.fold
                (fun acc (n, t) ->
                    addBinding
                        n
                        { Scheme = generalize env t
                          IsMutable = false }
                        acc)
                env

        newEnv, sigs, [ TDefTuple(names, typedExpr, exprType, r) ]

    | DDefMutable(name, expr, r) ->
        let exprType, typedExpr = infer env expr

        match Map.tryFind name sigs with
        | Some (sigType, _, _) -> unify env.Registry exprType sigType
        | None -> ()

        solvePending env

        // Not generalized, for the same reason `ELetMutable` is not: a cell that
        // can be assigned has to have a settled type. At module level it is also
        // what makes the binding emittable at all — a generalized one would want
        // a static field at an open type, which C# has nowhere to declare.
        let newEnv =
            addBinding
                name
                { Scheme = Scheme([], [], exprType)
                  IsMutable = true }
                env

        newEnv, Map.remove name sigs, [ TDefMutable(name, typedExpr, exprType, r) ]

    | DModule(moduleName, decls, r) ->
        // Rule 5 for aliases, decided over the module as written rather than as
        // checked: an alias may appear above the definition it would collide
        // with, and by the time the fold reached it the collision would look
        // like ordinary shadowing.
        let aliasesHere =
            decls |> List.choose (function DAlias(n, _, ar) -> Some(n, ar) | _ -> None)

        if not aliasesHere.IsEmpty then
            let definedHere =
                decls
                |> List.collect (function
                    | DDef(n, _, _)
                    | DDefMutable(n, _, _)
                    | DDefun(n, _, _, _, _)
                    | DMacro(n, _) -> [ n ]
                    | DDefTuple(ns, _, _) -> ns
                    | _ -> [])
                |> Set.ofList

            for (name, ar) in aliasesHere do
                if Set.contains name definedHere then
                    failwithf
                        $"Alias Error: '%s{name}' at %s{Lexer.formatPos ar} is also defined in this module. An alias is a top-level binding, so it may not take a name the module defines for itself."

            for (name, group) in aliasesHere |> List.groupBy fst do
                if group.Length > 1 then
                    let positions = group |> List.map (snd >> Lexer.formatPos) |> String.concat " and "

                    failwithf
                        $"Alias Error: '%s{name}' is aliased more than once, at %s{positions}. Two aliases producing one name is an error, not a shadowing."

        let finalEnv, finalSigs, typedDecls =
            checkDeclGroup { env with CurrentModule = moduleName } sigs decls

        // Every name this module defines is also reachable as
        // `Module_Module::name`.
        //
        // That spelling is what a macro expansion uses for a binding of the
        // macro's own module: the expansion lands somewhere else, where a local
        // of the same name would otherwise take it over. `Codegen` already
        // emits `::` as a class-qualified reference and `AlphaRename` already
        // refuses to touch one, so the only thing missing was somewhere to look
        // it up.
        //
        // Restricted to what the module actually defines rather than to
        // everything in scope at its end, so `Foo_Module::print` is not a name
        // just because `Foo` imported the prelude.
        //
        // The name to look the binding up under and the name to qualify it as
        // are two things: they differ for an import brought in under a
        // modifier, where the qualified spelling has to name the member the
        // origin's class actually defines.
        let definedHere =
            decls
            |> List.collect (function
                | DDef(n, _, _)
                | DDefMutable(n, _, _) -> [ n, Naming.qualifiedBinding moduleName n ]
                | DExtern(visible, origin, _, _, _) ->
                    // A facade's re-export lives in another module's class, so
                    // its qualified spelling names that one.
                    let m = if origin.OriginModule = "" then moduleName else origin.OriginModule
                    [ visible, Naming.qualifiedBinding m origin.OriginalName ]
                | DDefun(n, _, _, _, _) -> [ n, Naming.qualifiedBinding moduleName n ]
                | DDefTuple(ns, _, _) -> ns |> List.map (fun n -> n, Naming.qualifiedBinding moduleName n)
                | _ -> [])

        let qualified =
            definedHere
            |> List.fold
                (fun acc (visible, qualifiedSpelling) ->
                    match Map.tryFind visible finalEnv.Bindings with
                    | Some binding -> Map.add qualifiedSpelling binding acc
                    | None -> acc)
                finalEnv.Bindings

        { finalEnv with
            Bindings = qualified
            CurrentModule = env.CurrentModule },
        finalSigs,
        [ TModule(moduleName, typedDecls, r) ]

    | DImport(paths, r) -> env, sigs, [ TImport(paths, r) ]

    // `(:alias new old)`. A second spelling of a binding or a macro already in
    // scope, sharing the original's scheme, keyword and rest metadata, and
    // mutability — `set!` through one writes to the original's cell, because
    // codegen resolves the alias to it rather than emitting a copy.
    //
    // Types, traits and constructors are refused. A type has `type` aliases of
    // its own, and a constructor follows its type: neither is a binding, so
    // neither could share one.
    | DAlias(newName, oldName, r) ->
        let where = Lexer.formatPos r

        // Through the table first, so that aliasing a *prefixed* type or trait
        // is refused for what it is rather than reported as unbound.
        let target = originalName env.Registry oldName

        let isConstructor =
            env.Registry.Unions
            |> Map.exists (fun _ (_, cases) -> cases |> List.exists (fun (c, _, _) -> c = target))

        if Map.containsKey target env.Registry.Traits then
            failwithf
                $"Alias Error: '%s{oldName}' at %s{where} is a trait, and (:alias ...) makes a second spelling of a def or a macro. Import the module that declares it with (prefix-types ...) to change what its traits are called."

        if
            Set.contains target env.Registry.LocalTypes
            || Map.containsKey target env.Registry.Aliases
            || Map.containsKey target env.Registry.Records
        then
            failwithf
                $"Alias Error: '%s{oldName}' at %s{where} is a type, and (:alias ...) makes a second spelling of a def or a macro. Write (type (: %s{newName} %s{oldName})) for a type, or import its module with (prefix-types ...)."

        if isConstructor then
            failwithf
                $"Alias Error: '%s{oldName}' at %s{where} is a constructor, and (:alias ...) makes a second spelling of a def or a macro. A constructor follows its type: import its module with (prefix-types ...) to change what it is called."

        match Map.tryFind oldName env.Bindings with
        | Some binding ->
            // Resolved through the table, so a chain of facades flattens here
            // rather than at every use. An origin module of `""` means "look it
            // up where the whole program is known": a name defined in this
            // module, or a compiler builtin with no module class at all.
            let resolution =
                match Map.tryFind oldName env.Registry.ImportAliases with
                | Some a -> { a with Kind = AliasDef }
                | None -> { OriginModule = ""; OriginalName = oldName; Kind = AliasDef }

            // `addBinding` for the reason `DExtern` uses it — a second spelling
            // is a binder, and one that lands on a trait method's name shadows
            // it. The `FunMeta` goes on *after*, since `addBinding` drops the
            // one the new name had and this is the case that wants a new one.
            let newEnv =
                addBinding
                    newName
                    binding
                    { env with
                        Registry =
                            { env.Registry with
                                ImportAliases = Map.add newName resolution env.Registry.ImportAliases } }

            let newEnv =
                match Map.tryFind oldName env.FunMetas with
                | Some meta -> { newEnv with FunMetas = Map.add newName meta newEnv.FunMetas }
                | None -> newEnv

            newEnv, sigs, [ TAlias(newName, Some resolution, r) ]

        // A macro is not a binding. It was registered under the new name before
        // this module was parsed — it had to be, since the parser decides what a
        // head symbol means when it meets it — so there is nothing left to do.
        | None when Macro.isMacro oldName -> env, sigs, [ TAlias(newName, None, r) ]

        | None ->
            failwithf
                $"Alias Error: '%s{oldName}' is not in scope at %s{where}. (:alias ...) needs a binding or a macro to make a second spelling of."

    // A macro is checked as the `defun` it also produced. This carries no body
    // and contributes nothing to the program's runtime shape, so it stops here;
    // what an importing compilation reads is the assembly's macro list.
    | DMacro _ -> env, sigs, []
    | DExport(names, r) -> env, sigs, [ TExport(names, r) ]

    // `(import/class (Alias (: Clr.Class type #:exceptions (E ...))) ...)`
    //
    // Two passes, because a constructor signature is written in terms of the
    // alias it is declaring — `(-> string StreamWriter)` — so the alias has to
    // be a type before that signature can be resolved.
    | DImportClass(specs, r) ->
        let baseInfos =
            specs
            |> List.map (fun spec ->
                let where = Lexer.formatPos spec.Range
                // A generic type is spelled with its arity — `Set.Set`1` — but
                // written in source without one, because the alias says how many
                // arguments it takes. Both spellings are tried so that a clause
                // reads the way C# does.
                /// The generic definition of this name at *some* arity.
                ///
                /// Asked when the bare name does not resolve, so that a generic
                /// type written without its parameters is reported as the type
                /// constructor it is rather than as a name that does not exist.
                /// Eight is past every arity in the BCL that anyone imports.
                let genericAtAnyArity () =
                    [ 1..8 ]
                    |> List.tryPick (fun n -> DotNetInterop.tryResolveType $"%s{spec.ClrClass}`%d{n}")

                let clrType =
                    if spec.TypeParams.IsEmpty then
                        match DotNetInterop.tryResolveType spec.ClrClass with
                        | Some t -> t
                        | None ->
                            match genericAtAnyArity () with
                            | Some t -> t
                            | None -> DotNetInterop.resolveType $" at %s{where}" spec.ClrClass
                    else
                        let arityName = $"%s{spec.ClrClass}`%d{spec.TypeParams.Length}"

                        match DotNetInterop.tryResolveType arityName with
                        | Some t -> t
                        | None ->
                            match genericAtAnyArity () with
                            | Some t -> t
                            | None -> DotNetInterop.resolveType $" at %s{where}" spec.ClrClass

                // The two halves of the same claim: a generic type has to be
                // imported applied, and an ordinary one cannot be.
                if clrType.IsGenericTypeDefinition && spec.TypeParams.IsEmpty then
                    let arity = clrType.GetGenericArguments().Length
                    let written = List.init arity (fun i -> "%" + string (char (int 'a' + i))) |> String.concat " "

                    failwithf
                        $"Type Error at %s{where}: '%s{spec.ClrClass}' is a generic type taking %d{arity} argument(s), so it is a type constructor rather than a type. Import it applied to its parameters: ((%s{spec.Alias} %s{written}) (: %s{spec.ClrClass}))."

                if not clrType.IsGenericTypeDefinition && not spec.TypeParams.IsEmpty then
                    failwithf
                        $"Type Error at %s{where}: '%s{spec.ClrClass}' is not a generic type, so '%s{spec.Alias}' takes no type parameters. Write the alias bare."

                if clrType.IsGenericTypeDefinition
                   && clrType.GetGenericArguments().Length <> spec.TypeParams.Length then
                    let arity = clrType.GetGenericArguments().Length

                    failwithf
                        $"Type Error at %s{where}: '%s{spec.ClrClass}' takes %d{arity} type argument(s), but '%s{spec.Alias}' was declared with %d{spec.TypeParams.Length}."

                if clrType.IsGenericTypeDefinition && not spec.Exceptions.IsEmpty then
                    failwithf
                        $"Type Error at %s{where}: #:exceptions describes the constructor, and a generic class is imported as a type only. Reach its constructor through a static factory imported with import/extern."

                checkExceptionTypes where spec.Exceptions

                { Alias = spec.Alias
                  TypeParams = spec.TypeParams
                  // Without the arity mark, which is what a Bjolang type
                  // constructor carries in the number of arguments it is applied
                  // to — and what the code generator emits before its angle
                  // brackets.
                  ClrName = DotNetInterop.clrTypeName clrType
                  CtorType = None
                  CtorExceptions = spec.Exceptions })

        // The alias becomes a type alias as well as a class: that is what lets
        // an ordinary signature say `StreamWriter` and mean
        // `System.IO.StreamWriter`, with no second spelling to keep in sync.
        let registryWithAliases =
            baseInfos
            |> List.fold
                (fun (reg: TraitRegistry) info ->
                    // A generic import becomes a type *alias with parameters*,
                    // which the annotation resolver already knows how to expand:
                    // `(Set int)` substitutes into `Set.Set<int>` the same way a
                    // hand-written `(type (: (Pair %a) ...))` does. The bare
                    // alias of an ordinary class is the arity-zero case of the
                    // same thing.
                    let aliasTarget =
                        TCon(info.ClrName, info.TypeParams |> List.map (fun p -> TVar("'" + p)))

                    { reg with
                        ClrClasses = Map.add info.Alias info reg.ClrClasses
                        Aliases = Map.add info.Alias (info.TypeParams, aliasTarget) reg.Aliases
                        // Both spellings are this module's: the alias, and the
                        // .NET name it expands to. The second is what an impl's
                        // target resolves to, and without it the orphan rule
                        // would refuse a module the right to implement a trait
                        // for a type it went to the trouble of importing.
                        LocalTypes = reg.LocalTypes |> Set.add info.Alias |> Set.add info.ClrName })
                env.Registry

        let infos =
            List.map2
                (fun (info: ClrClassInfo) (spec: ClassImportSpec) ->
                    { info with
                        CtorType = spec.ConstructorType |> Option.map (resolveTypeAnnotation registryWithAliases) })
                baseInfos
                specs

        let finalRegistry =
            infos
            |> List.fold (fun (reg: TraitRegistry) info -> { reg with ClrClasses = Map.add info.Alias info reg.ClrClasses }) registryWithAliases

        { env with Registry = finalRegistry }, sigs, [ TImportClass(infos, r) ]

    // `(import/extern (alias (: Clr.Type.Member type #:exceptions (E ...))) ...)`
    | DImportExtern(specs, r) ->
        let infos =
            specs
            |> List.map (fun spec ->
                let where = Lexer.formatPos spec.Range
                let split = spec.ClrTarget.LastIndexOf "."

                let kind =
                    if spec.IsGet then ExternGet
                    elif spec.IsSet then ExternSet
                    else ExternMethod

                if split <= 0 || split = spec.ClrTarget.Length - 1 then
                    let what =
                        match kind with
                        | ExternMethod -> "a method"
                        | _ -> "a property or field"

                    failwithf
                        $"Syntax error at %s{where}: '%s{spec.ClrTarget}' does not name %s{what}. Write the declaring type and the member together, as in System.Console.WriteLine."

                let typeName = spec.ClrTarget.Substring(0, split)
                let memberName = spec.ClrTarget.Substring(split + 1)
                let clrType = DotNetInterop.resolveType $" at %s{where}" typeName

                // Whether the member is static or an instance one is read off
                // the metadata rather than written in the clause. There is
                // nothing an author could add — the name denotes one or the
                // other — and an instance member simply takes its receiver as
                // the alias's first argument.
                //
                // Checked at the import rather than at the first call: an
                // import that names nothing is wrong whether or not anybody got
                // around to using it.
                let existsStatic, existsInstance =
                    match kind with
                    | ExternMethod ->
                        DotNetInterop.hasStaticMethod clrType memberName,
                        DotNetInterop.hasInstanceMethod clrType memberName
                    | _ ->
                        DotNetInterop.hasMember true clrType memberName,
                        DotNetInterop.hasMember false clrType memberName

                // Static first, which is how C# reads `Type.Member` too. A type
                // with both under one name is vanishingly rare and the static
                // one is what the spelling says.
                let isInstance =
                    if existsStatic then false
                    elif existsInstance then true
                    else
                        match kind with
                        | ExternMethod ->
                            failwithf
                                $"Type Error at %s{where}: '%s{clrType.FullName}' has no public method named '%s{memberName}', static or instance."
                        | ExternGet ->
                            failwithf
                                $"Type Error at %s{where}: '%s{clrType.FullName}' has no public property or field named '%s{memberName}'."
                        | ExternSet ->
                            failwithf
                                $"Type Error at %s{where}: '%s{clrType.FullName}' has no public property or field named '%s{memberName}'."

                match kind with
                | ExternGet ->
                    // Readability and writability are settled here for the same
                    // reason existence is: the clause is where the claim was
                    // made. The resolved type is thrown away — each use
                    // re-resolves it, and there is only ever one answer.
                    DotNetInterop.resolveMemberRead where clrType memberName (not isInstance) |> ignore
                | ExternSet -> DotNetInterop.resolveMemberWrite where clrType memberName (not isInstance) |> ignore
                | ExternMethod ->
                    checkExceptionTypes where spec.Exceptions

                    // Both of these are arity-independent, so they can be
                    // answered here rather than at the first call — which is the
                    // point. `#:async` on a method that returns nothing
                    // awaitable is a mistake about the method, and the import is
                    // where the claim was made.
                    if spec.IsAsync && not (DotNetInterop.hasAwaitableOverload (not isInstance) clrType memberName) then
                        failwithf
                            $"Type Error at %s{where}: '%s{clrType.FullName}.%s{memberName}' is imported #:async, but no overload of it returns a Task or a ValueTask. An ordinary method is imported without #:async and called directly."

                    if spec.IsAsync
                       && not spec.Uncancellable
                       && not (DotNetInterop.hasTokenOverload (not isInstance) clrType memberName None) then
                        failwithf
                            $"Type Error at %s{where}: '%s{clrType.FullName}.%s{memberName}' has no overload taking a System.Threading.CancellationToken, so the ambient cancellation token cannot be threaded into it.\n  Write #:uncancellable here to say so. That is deliberately not the default: a call that cannot be cancelled keeps running after the scope that wanted it has given up, and the place to notice is the import rather than the choose that leaks."

                    // §7.5's lint. A synchronous method with an `…Async` sibling
                    // is almost always the wrong one to have imported: the
                    // sibling does not park a pool thread, and with `#:async`
                    // the call site reads identically. Said rather than
                    // enforced, because there are real reasons to want the
                    // synchronous one — a startup path with no fiber in sight,
                    // say — and being told the name is enough to make the choice
                    // deliberate.
                    let hasSibling =
                        if isInstance then
                            DotNetInterop.hasInstanceMethod clrType (memberName + "Async")
                        else
                            DotNetInterop.hasStaticMethod clrType (memberName + "Async")

                    if not spec.IsAsync && hasSibling then
                        Diagnostics.progress
                            $"Note at %s{where}: '%s{clrType.FullName}.%s{memberName}' has an async sibling, '%s{memberName}Async'. The synchronous one parks a thread; importing the sibling with #:async does not, and the call site reads the same either way (§7.5)."

                    if spec.Uncancellable && not (spec.IsAsync || spec.Cancellable) then
                        failwithf
                            $"Syntax error at %s{where}: #:uncancellable says not to thread the ambient cancellation token into this call, but without #:async or #:cancellable there is no token to thread and nothing to cancel."

                    if spec.Cancellable && spec.IsAsync then
                        failwithf
                            $"Syntax error at %s{where}: #:cancellable is what #:async already does — the ambient token is threaded into every #:async call that has an overload to take it. Write one or the other."

                    if spec.Cancellable && not (DotNetInterop.hasTokenOverload (not isInstance) clrType memberName None) then
                        failwithf
                            $"Type Error at %s{where}: '%s{clrType.FullName}.%s{memberName}' is imported #:cancellable, but no overload of it takes a System.Threading.CancellationToken. There is nothing to thread."

                let declaredType =
                    spec.ExplicitType |> Option.map (resolveTypeAnnotation env.Registry)

                // A member whose overloads are *all* generic definitions is
                // resolved here, once, against the signature — and a member with
                // even one ordinary overload keeps being resolved per call site
                // from its argument types, exactly as before. Which of the two a
                // name gets is a property of the .NET method group rather than
                // of anything written in the clause.
                let genericTypeArgs, declaredType =
                    if kind <> ExternMethod
                       || not (DotNetInterop.isGenericOnlyMethod (not isInstance) clrType memberName) then
                        None, declaredType
                    else
                        if spec.IsAsync || spec.Cancellable then
                            failwithf
                                $"Type Error at %s{where}: '%s{clrType.FullName}.%s{memberName}' is generic, and #:async and #:cancellable are not supported for a generic method yet. Its type arguments are solved from the declared signature, which has no room to say what a threaded token or an unwrapped task does to them."

                        match declaredType with
                        | Some(TFun(declaredParams, declaredReturn, _)) ->
                            // The receiver is the alias's first parameter and
                            // none of the method's, so it comes off before
                            // reflection sees the signature.
                            let methodParams =
                                if not isInstance then
                                    declaredParams
                                else
                                    match declaredParams with
                                    | _ :: rest -> rest
                                    | [] ->
                                        failwithf
                                            $"Type Error at %s{where}: '%s{spec.Alias}' names the instance method '%s{clrType.FullName}.%s{memberName}', whose receiver is its first argument, but its declared type takes none."

                            let resolved =
                                DotNetInterop.resolveGenericMethod
                                    where
                                    (not isInstance)
                                    clrType
                                    memberName
                                    methodParams
                                    declaredReturn

                            // A method that answers nothing keeps the *interop*
                            // void as its type, not the unit the signature spells
                            // it with. The two are the same thing to a reader and
                            // not to the emitter: a void call is a statement, and
                            // a unit is a value C# would have to produce.
                            let normalized =
                                if resolved.ReturnType = declaredReturn then
                                    declaredType
                                else
                                    Some(TFun(declaredParams, resolved.ReturnType, ESync))

                            Some resolved.TypeArguments, normalized
                        | Some _ ->
                            failwithf
                                $"Type Error at %s{where}: '%s{spec.Alias}' names a method, so its declared type has to be a function type."
                        | None ->
                            failwithf
                                $"Type Error at %s{where}: '%s{clrType.FullName}.%s{memberName}' is generic, so this import needs a declared signature — that is where its type arguments come from. Write one, as in (: %s{spec.ClrTarget} (-> (Set %%a) %%a (Set %%a)))."

                { Alias = spec.Alias
                  ClrType = clrType.FullName
                  MemberName = memberName
                  Kind = kind
                  IsInstance = isInstance
                  DeclaredType = declaredType
                  GenericTypeArgs = genericTypeArgs
                  Exceptions = spec.Exceptions
                  IsAsync = spec.IsAsync
                  Uncancellable = spec.Uncancellable
                  Cancellable = spec.Cancellable })

        let newRegistry =
            infos
            |> List.fold (fun (reg: TraitRegistry) info -> { reg with ClrExterns = Map.add info.Alias info reg.ClrExterns }) env.Registry

        { env with Registry = newRegistry }, sigs, [ TImportExtern(infos, r) ]

    | DReExport(names, r) ->
        // A re-exported name was defined elsewhere and already carries a
        // signature from there, so the local-signature rule `export` enforces
        // cannot apply. What can be checked is that the name is actually in
        // scope here — otherwise the module would advertise something it does
        // not have.
        for name in names do
            if not (Map.containsKey name env.Bindings) then
                                    failwithf
                                        "Re-export Error: '%s' is not in scope at %s. A re-exported name must be imported by this module."
                                        name
                                        (Lexer.formatPos r)

        env, sigs, [ TReExport(names, r) ]
    | DType(typeDefs, r) ->
        let newEnv, keyed = registerTypeDefs false typeDefs env
        newEnv, sigs, [ TType(keyed, r) ]
    | DExtern(name, declaredOrigin, ftype, constraintPairs, r) ->
        // An unfilled origin module means "the module this declaration is in",
        // which is only knowable here. A filled one is a facade's: the module
        // publishing the name generated no code for it.
        let origin =
            if declaredOrigin.OriginModule = "" then
                { declaredOrigin with OriginModule = env.CurrentModule }
            else
                declaredOrigin

        let t = resolveTypeAnnotation env.Registry ftype
        let scheme = generalize env t
        let (Scheme(vars, _, schemeType)) = scheme
        // Add constraints from DLL metadata
        let constraints = 
            constraintPairs |> List.map (fun (traitName, varName) ->
                { TraitName = originalName env.Registry traitName; TargetType = TVar varName })
        let schemeWithConstraints = Scheme(vars, constraints, schemeType)
        // The same hazard as a top-level definition over a method, arriving by
        // a different route and with nothing in this file to point at — so the
        // module it came from is the location, which is where the fix is.
        match Map.tryFind name env.Registry.TraitMethods with
        | Some traitName when Set.contains name env.TraitMethodNames ->
            Diagnostics.warn
                $"'%s{name}' is imported from '%s{origin.OriginModule}' and is a method of the trait '%s{traitName}', so the import binds over it. A call to '%s{name}' in this module reaches the imported binding rather than dispatching. Import that module with (except ... %s{name}) or (rename ... (%s{name} another-name)) if that is not what you meant."
        | _ -> ()

        // Through `addBinding`, because an import is a binder like any other.
        // Writing `Bindings` directly left the name in `TraitMethodNames`, so a
        // module that bound over `sign` and exported it published a binding the
        // importer resolved and then never called: every `(sign x)` over there
        // went on dispatching `Num`. That is the bug shadowing was supposed to
        // have fixed, surviving across a module boundary.
        let newEnv = addBinding name { Scheme = schemeWithConstraints; IsMutable = false } env

        // Every imported binding gets a table entry, whether or not a modifier
        // renamed it. The degenerate one carries no new spelling but does carry
        // the module the name came from, which is what an `(:alias ...)` of it
        // needs to resolve to a qualified reference — and what makes a facade
        // of a facade flatten, since the entry already holds the ultimate
        // origin rather than the module it was read from.
        let newEnv =
            { newEnv with
                Registry =
                    { newEnv.Registry with
                        ImportAliases = Map.add name origin newEnv.Registry.ImportAliases } }

        // Keyword and rest metadata travels with an imported signature too.
        // Without it a call that passes a keyword argument, or omits an optional
        // one, has nothing to resolve against, and the flat function type
        // refuses to unify with the shorter argument list the caller wrote.
        let newEnv =
            match ftype with
            | TArrow(mandatory, keywords, restOpt, _, _, _) ->
                let funMeta =
                    { MandatoryCount = mandatory.Length
                      KeywordParams =
                        keywords |> List.map (fun (n, ft) -> n, resolveTypeAnnotation env.Registry ft)
                      RestParam = restOpt |> Option.map (resolveTypeAnnotation env.Registry) }

                { newEnv with FunMetas = Map.add name funMeta newEnv.FunMetas }
            | _ -> newEnv

        newEnv, sigs, [ TExtern(name, origin, ftype, r) ]

    | DTrait(traitName, implementorVar, holeArity, assocTypes, signatures, defaults, clrSpec, r) ->
        // The kind is derived, not declared: an implementor written applied to
        // arguments cannot be an interface, because there is no C# interface
        // that abstracts over a type constructor.
        let kind = if holeArity > 0 then InlineTrait else InterfaceTrait

        // A trait that stands for a .NET interface. Everything about it is
        // checked here rather than at a use site: the interface is named in
        // this declaration, so this is the only place a diagnostic can point at
        // where the name was written. See `Docs/Numerics.org`.
        let clrConstraint =
            clrSpec
            |> Option.map (fun (ifaceName, argExprs, memberSpecs) ->
                let args = argExprs |> List.map (resolveTypeAnnotation env.Registry)

                if holeArity > 0 then
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' applies its implementor, so it is inline-only and cannot stand for a .NET interface. A C# interface cannot abstract over a type constructor, which is the same reason the trait is inline-only."

                if not assocTypes.IsEmpty then
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' stands for a .NET interface and cannot declare associated types. There is no implementation to bind one in — the interface is the implementation."

                if not defaults.IsEmpty then
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' stands for a .NET interface and cannot give default method bodies. There is no implementation for one to land in."

                // The constraint has to *say* something about the implementor,
                // or a `(where ...)` on it constrains nothing and the C# clause
                // would name a type parameter the method does not have.
                let implVar = "'" + implementorVar

                if not (args |> List.exists (fun a -> freeTVars env.Registry a |> List.contains implVar)) then
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' stands for '%s{ifaceName}' but never applies it to %%%s{implementorVar}, so a constraint on it would say nothing about the implementor. Write (#:clr-constraint (%s{ifaceName} %%%s{implementorVar}))."

                let iface =
                    match DotNetInterop.tryResolveGenericInterface ifaceName args.Length with
                    | Some t -> t
                    | None ->
                        let applied =
                            if args.IsEmpty then ifaceName else $"%s{ifaceName} at %d{args.Length} type argument(s)"

                        failwithf
                            $"Interop Error at %s{Lexer.formatPos r}: trait '%s{traitName}' stands for '%s{applied}', which is not a .NET interface this compiler can find. Names must be fully qualified, as in System.Numerics.INumber, and the number of arguments has to be the number the interface declares."

                // Every method must say which member it is. There is no
                // implementation to fall back on and no default body to inherit,
                // so a method without one names nothing at all.
                let memberMap = Map.ofList memberSpecs

                for (mName, _) in signatures do
                    if not (Map.containsKey mName memberMap) then
                        failwithf
                            $"Type Error at %s{Lexer.formatPos r}: '%s{mName}' is a method of '%s{traitName}', which stands for the .NET interface '%s{ifaceName}', so it has to say which member of it to call. Write (: %s{mName} ... #:clr-member SomeMember)."

                let declared = signatures |> List.map fst |> Set.ofList

                for (mName, _) in memberSpecs do
                    if not (Set.contains mName declared) then
                        failwithf
                            $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' binds a #:clr-member for '%s{mName}', which it does not declare. Add (: %s{mName} ...) to the trait, or remove the binding."

                // Resolved against the interface here, where the diagnostic can
                // point at the declaration. Whether the member is static is
                // read rather than written: the metadata already knows.
                let members =
                    memberSpecs
                    |> List.map (fun (mName, memberName) ->
                        match DotNetInterop.tryFindInterfaceMember iface memberName with
                        | Some kind ->
                            mName,
                            { MemberName = memberName
                              IsStatic = (kind = DotNetInterop.StaticMember) }
                        | None ->
                            let available =
                                DotNetInterop.interfaceMemberNames iface |> String.concat ", "

                            failwithf
                                $"Interop Error at %s{Lexer.formatPos r}: '%s{ifaceName}' has no member '%s{memberName}', named by '%s{mName}' in trait '%s{traitName}'. It offers: %s{available}.")
                    |> Map.ofList

                { InterfaceName = ifaceName
                  Args = args
                  Members = members })

        let hmSignatures =
            match kind with
            | InterfaceTrait ->
                signatures
                |> List.map (fun (name, fType) -> name, resolveTypeAnnotation env.Registry fType)
                |> Map.ofList
            | InlineTrait -> Map.empty

        let templates =
            match kind with
            | InterfaceTrait -> Map.empty
            | InlineTrait ->
                signatures
                |> List.map (fun (name, fType) -> name, resolveTemplate env.Registry implementorVar fType)
                |> Map.ofList

        if kind = InlineTrait && not assocTypes.IsEmpty then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' applies its implementor, so it is inline-only and cannot declare associated types. An inline trait's methods may be generic in their own right instead."

        // A default body is not checked here — there is nothing to check it
        // against. Its type comes from the impl it is spliced into, and until
        // there is one the implementor is an abstract variable that no `.NET`
        // overload, record field or numeric literal could be resolved at.
        //
        // What *is* checked is that it stands for a method this trait declares.
        // A defaulted name with no signature would otherwise sit in the trait
        // being silently ignored by every impl, since only declared methods are
        // ever looked up.
        let declaredMethods = signatures |> List.map fst |> Set.ofList

        let defaultBodies =
            defaults
            |> List.map (fun d ->
                match d with
                | DDefun(name, _, _, _, dr) ->
                    if not (Set.contains name declaredMethods) then
                        failwithf
                            $"Type Error at %s{Lexer.formatPos dr}: trait '%s{traitName}' gives a default body for '%s{name}', which it does not declare. Add (: %s{name} ...) to the trait, or remove the body."

                    name, d
                | _ ->
                    failwithf
                        $"Syntax error at %s{Lexer.formatPos r}: only 'defun' declarations may appear in trait '%s{traitName}'.")

        for (name, _) in defaultBodies |> List.countBy fst |> List.filter (fun (_, n) -> n > 1) do
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' gives more than one default body for '%s{name}'."

        let traitInfo =
            { ImplementorVar = implementorVar
              AssociatedTypes = assocTypes
              Signatures = hmSignatures
              Kind = kind
              HoleArity = holeArity
              Templates = templates
              Defaults = Map.ofList defaultBodies
              ClrConstraint = clrConstraint }

        let newEnv = addTrait traitName traitInfo env

        // Whatever the kind, the method names are recorded so that `infer` can
        // recognize them in application position without searching every trait.
        let methodNames = signatures |> List.map fst

        // A method name identifies its trait, and that is the *only* thing that
        // can: nothing at a call site says which trait `pure` came from. Two
        // traits claiming one name is therefore not ambiguity to be resolved
        // later but a program with no meaning, and it has to be rejected here
        // rather than silently dispatched to whichever was registered last.
        for m in methodNames do
            match Map.tryFind m newEnv.Registry.TraitMethods with
            | Some owner when owner <> traitName ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: trait '%s{traitName}' declares a method '%s{m}', but '%s{owner}' already does. A call site says nothing about which trait a method name belongs to, so the two are indistinguishable. Rename one of them."
            | _ -> ()

        let newEnv =
            { newEnv with
                Registry =
                    { newEnv.Registry with
                        TraitMethods =
                            methodNames
                            |> List.fold (fun acc m -> Map.add m traitName acc) newEnv.Registry.TraitMethods } }

        let assocSubst = 
            assocTypes 
            |> List.map (fun assocName -> 
                "'" + assocName, TAssoc(traitName, assocName, TVar ("'" + implementorVar)))
            |> Map.ofList

        // An inline trait's methods are deliberately *not* bound into
        // `env.Bindings`. There is no single scheme they could be bound under —
        // `m` appears applied to two different arguments in `bind` — and a
        // weaker stand-in would be worse than nothing.
        let mutable finalEnv = newEnv

        if kind = InterfaceTrait then
            for kvp in hmSignatures do
                let methodTypeWithAssoc = substTypeVars assocSubst kvp.Value
                // Collect ALL free type variables from the method signature.
                // The implementor var is always first; any additional vars (like 'acc)
                // are method-level generics that must also be quantified.
                let methodVars = freeTVars env.Registry methodTypeWithAssoc |> List.distinct
                let implVar = "'" + implementorVar
                let allVars = implVar :: (methodVars |> List.filter ((<>) implVar))
                let scheme = Scheme(allVars, [], methodTypeWithAssoc)
                finalEnv <- addBinding kvp.Key { Scheme = scheme; IsMutable = false } finalEnv

        // Last, because `addBinding` above cleared each name as it bound it.
        // Declaring a trait is the one thing that makes a name mean "dispatch on
        // the trait", and it is done here rather than beside each binding
        // because an *inline* trait's methods are never bound at all.
        //
        // A name may be a prelude function and a trait method both — `wrap` is,
        // being a CML combinator and the method of a `Wrapper` declared in a
        // module of its own. The declaration wins there, which is what
        // shadowing means.
        finalEnv <-
            { finalEnv with
                TraitMethodNames =
                    methodNames |> List.fold (fun acc m -> Set.add m acc) finalEnv.TraitMethodNames }

        finalEnv, sigs, [ TTrait(traitName, implementorVar, kind, holeArity, assocTypes, hmSignatures, r) ]
    | DTypeRec(typeDefs, r) ->
        let newEnv, keyed = registerTypeDefs true typeDefs env
        newEnv, sigs, [ TTypeRec(keyed, r) ]
    | DImpl(traitName, targetTypeExpr, assocBindings, whereClause, methods, r) ->
        // The trait may be written under a spelling a `prefix` produced; the
        // registries are keyed on the name the `def/trait` gave it.
        let traitName = originalName env.Registry traitName
        let whereClause = whereClause |> List.map (fun (t, v) -> originalName env.Registry t, v)
        let targetType = resolveTypeAnnotation env.Registry targetTypeExpr

        let typeKey =
            match implCtorKey targetType with
            | Some k -> k
            | None -> failwithf $"Trait implementations require concrete target types at %s{Lexer.formatPos r}"

        let isLocalTrait = env.Registry.IsTraitDefinedLocally(traitName)

        // A tuple belongs to no module, exactly as `List` and `Option` do, so
        // the "or the module defining the type" half of the orphan rule has
        // nothing to hold it to.
        let isLocalType =
            typeKey <> BlanketCtor
            && (isTupleCtor typeKey || env.Registry.IsTypeDefinedLocally(typeKey))

        // The orphan rule, and it is what keeps the blanket fallback from being
        // a source of action at a distance. Once impls can overlap, adding one
        // in a third module could change which impl a call in an unrelated
        // module selects. Restricting an impl to the module defining the trait
        // or the module defining the head constructor removes the possibility:
        // any module that can *mention* both already depends on both, so it
        // recompiles.
        //
        // A blanket has no head constructor at all, so only the trait's own
        // module may write one — which is exactly right. A blanket declared
        // elsewhere would change the meaning of every call to that trait in
        // every module that never heard of it.
        // A blanket is held to the stricter half of the rule on its own, and
        // against the module the trait was actually declared in rather than
        // against `LocalTraits` — which by this point also holds every imported
        // trait. There is no second escape hatch for it: a blanket has no head
        // constructor, so "or the module defining the type" has nothing to say.
        if typeKey = BlanketCtor then
            let declaredHere =
                Map.tryFind traitName env.Registry.TraitOrigins = Some env.CurrentModule

            if not declaredHere then
                failwithf
                    $"Orphan Rule Violation at %s{Lexer.formatPos r}: a blanket implementation of '%s{traitName}' may only be written in the module that defines the trait. A blanket applies at every type that has no implementation of its own, so declaring one here would change what '%s{traitName}' means for modules that do not import this one."
        elif not (isLocalTrait || isLocalType) then
            failwithf
                $"Orphan Rule Violation at %s{Lexer.formatPos r}: Cannot implement foreign trait '%s{traitName}' for foreign type '%s{typeKey}'."

        let hmAssocBindings =
            assocBindings
            |> List.map (fun (name, fType) -> name, resolveTypeAnnotation env.Registry fType)

        let hmAssocBindingsMap = Map.ofList hmAssocBindings

        let traitInfo =
            match Map.tryFind traitName env.Registry.Traits with
            | Some info -> info
            | None -> failwithf $"Unknown trait '%s{traitName}' at %s{Lexer.formatPos r}"

        // A trait that stands for a .NET interface has no implementations to
        // write: whether a type satisfies it is decided by the runtime, and a
        // `def/impl` would be a second answer to a question already answered.
        match traitInfo.ClrConstraint with
        | Some clr ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: '%s{traitName}' stands for the .NET interface '%s{clr.InterfaceName}', so it has no implementations to write — a type satisfies it by implementing the interface, which '%s{Naming.showTypeName typeKey}' either does or does not. Remove the def/impl."
        | None -> ()

        // The `(where ...)`, checked against what the impl can actually hold.
        //
        // Each constraint becomes a dictionary the impl class carries, so it has
        // to be phrased over a variable of the impl's own target: there is
        // nowhere else for the evidence to come from, and a variable named here
        // and nowhere in the target would be one the class has no parameter for.
        let targetVars = freeTVars env.Registry targetType |> Set.ofList

        let implConstraints =
            whereClause
            |> List.map (fun (cTrait, varName) ->
                if not (Set.contains varName targetVars) then
                    let written = "%" + varName.TrimStart('\'')

                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: the where clause of this implementation constrains '%s{written}', which the implemented type does not mention. An impl may only constrain its own type variables."

                match Map.tryFind cTrait env.Registry.Traits with
                | None -> failwithf $"Unknown trait '%s{cTrait}' in the where clause at %s{Lexer.formatPos r}"
                | Some cInfo ->
                    // An inline trait has no dictionary — that is what makes it
                    // inline-only — so there is nothing an impl could hold to
                    // discharge a constraint over one.
                    if cInfo.Kind = InlineTrait then
                        failwithf
                            $"Type Error at %s{Lexer.formatPos r}: '%s{cTrait}' is an inline-only trait, so it cannot appear in an implementation's where clause. There is no dictionary for the impl to carry."

                    // The dictionary's C# type names the trait's associated
                    // types too, and for a constraint over a type *variable*
                    // those are not known here — they would have to become
                    // further parameters of the impl class. Not built; say so
                    // rather than emitting a class that will not compile.
                    if not cInfo.AssociatedTypes.IsEmpty then
                        failwithf
                            $"Type Error at %s{Lexer.formatPos r}: '%s{cTrait}' has associated types, which an implementation's where clause cannot carry yet. Constrain a function instead, where the association becomes a type parameter."

                    { TraitName = cTrait; TargetType = TVar varName })

        // Defaults are spliced in *here*, before anything looks at the method
        // list, so that everything below — the definition-site check, the
        // completeness check, the landing pads, the inline templates — sees an
        // impl that wrote every method out by hand. A defaulted method is
        // therefore not a second kind of method with a dispatch path of its own;
        // it is the same method, and it costs exactly what writing it would.
        //
        // Re-checking one body per impl rather than checking it once against the
        // trait is what makes a default able to say something the trait's own
        // signature cannot: `(clr-abs x)` picks `Math.Abs(int)` in the `int`
        // impl and `Math.Abs(double)` in the `double` one, from argument types
        // that only exist once the implementor is known.
        let definedMethodNames =
            methods
            |> List.choose (function
                | DDefun(name, _, _, _, _) -> Some name
                | _ -> None)
            |> Set.ofList

        let inheritedMethods =
            traitInfo.Defaults
            |> Map.toList
            |> List.filter (fun (name, _) -> not (Set.contains name definedMethodNames))
            |> List.map snd

        let methods = methods @ inheritedMethods

        let implTarget = implTargetOf traitName traitInfo targetType implConstraints r
        let regEnv = addImplementation traitName typeKey targetType implTarget hmAssocBindingsMap env

        // FIX 1: Prepend the "'" to the substitution keys so they match TVar "'c"
        let mutable substitutions = Map.add ("'" + traitInfo.ImplementorVar) targetType Map.empty

        for (k, v) in hmAssocBindings do
            substitutions <- Map.add ("'" + k) v substitutions

        let rec applySubst t =
            match prune regEnv.Registry t with
            | TVar name ->
                match Map.tryFind name substitutions with
                | Some concrete -> concrete
                | None -> t
            | TCon(n, args) -> TCon(n, args |> List.map applySubst)
            | TFun(args, ret, eff) -> TFun(args |> List.map applySubst, applySubst ret, eff)
            | TTuple args -> TTuple(args |> List.map applySubst)
            | _ -> t

        let typedMethods =
            methods
            |> List.map (fun methodDecl ->
                match methodDecl with
                | DDefun(name, args, body, _, methodRange) ->
                    // The definition-site check. Checking each body against the
                    // trait's own signature, instantiated at *this* impl, is what
                    // keeps errors out of the instantiation sites: an inline
                    // method that does not match its trait is rejected here,
                    // once, rather than at every place it is later spliced.
                    let expectedSignature =
                        match traitInfo.Kind with
                        | InlineTrait ->
                            match Map.tryFind name traitInfo.Templates with
                            | Some tpl -> instantiateTemplate implTarget tpl
                            | None ->
                                failwithf
                                    $"Method '%s{name}' is not a member of trait '%s{traitName}' at %s{Lexer.formatPos methodRange}"
                        | InterfaceTrait ->
                            match Map.tryFind name traitInfo.Signatures with
                            | Some sigType -> applySubst sigType
                            | None ->
                                failwithf
                                    $"Method '%s{name}' is not a member of trait '%s{traitName}' at %s{Lexer.formatPos methodRange}"

                    // After substituting the implementor var and associated types,
                    // the signature may still contain TVars from two sources:
                    //   1. Class-level type params (from targetType, e.g. 'a in List %a)
                    //      → These must stay as rigid TVars so they match the class params.
                    //   2. Method-level generics (like 'acc in fold's signature)
                    //      → These must be instantiated to fresh metas.
                    //
                    // An inline trait's class-level parameters are only the
                    // impl's *fixed prefix*: the arguments the constructor
                    // variable abstracts over belong to the method, and `bind`'s
                    // own `'b` is a method-level generic that has to reach C# as
                    // a generic method parameter.
                    let classLevelVars =
                        match traitInfo.Kind with
                        | InlineTrait -> implTarget.FixedPrefix |> List.collect typeVarsOf |> Set.ofList
                        | InterfaceTrait -> freeTVars regEnv.Registry targetType |> Set.ofList
                    let remainingVars = freeTVars regEnv.Registry expectedSignature |> List.distinct
                    let freshSubst =
                        remainingVars
                        |> List.filter (fun v -> not (Set.contains v classLevelVars))
                        |> List.map (fun v -> v, freshMeta())
                        |> Map.ofList
                    let instantiatedSig = substTypeVars freshSubst expectedSignature

                    // Pass instantiatedSig through 'sigs'.
                    // This forces DDefun to unify the expected types into the arguments
                    // BEFORE inference and generalization.
                    let methodSigs = Map.add name (instantiatedSig, None, []) Map.empty

                    // Which method this is, so that the `defun`'s own recursion
                    // binding is not taken for a shadow of it.
                    let regEnv = { regEnv with ImplMethod = Some name }

                    let _, _, tDecls = checkDecl regEnv methodSigs methodDecl
                    let tDecl = List.head tDecls // The fully verified TDefun node

                    // What the body turned out to need of the impl's own type
                    // variables, against what the impl declared. A method is not
                    // a generic function: there are no dictionary parameters to
                    // inject, only the fields the `(where ...)` put on the
                    // class, so an undeclared need has nowhere to come from.
                    //
                    // Caught here rather than in `Lowering`, which would
                    // otherwise report a missing `_dict_` — a name the program
                    // never wrote and the author has no way to supply.
                    match tDecl with
                    | TDefun(_, _, _, _, _, _, _, typedBody, _) ->
                        for c in collectTraitConstraints regEnv typedBody do
                            let varName =
                                match prune regEnv.Registry c.TargetType with
                                | TVar v -> v
                                | other -> DotNetInterop.showType other

                            let declared =
                                implConstraints
                                |> List.exists (fun d -> d.TraitName = c.TraitName && d.TargetType = TVar varName)

                            if not declared then
                                // Spelled as the source spells a type variable,
                                // because the message asks for a line to be
                                // typed and `'a` is not how one is written.
                                let written = "%" + varName.TrimStart('\'')

                                failwithf
                                    $"Type Error at %s{Lexer.formatPos methodRange}: '%s{name}' uses '%s{c.TraitName}' at '%s{written}', which this implementation does not require. Add (where (%s{c.TraitName} %s{written})) to the implementation, and every use of it will have to supply one."
                    | _ -> ()

                    tDecl

                | _ -> failwithf $"Only 'defun' declarations are allowed inside 'def/impl' at %s{Lexer.formatPos r}")

        // Ensure all required methods from the trait are implemented
        let requiredMethods =
            match traitInfo.Kind with
            | InlineTrait -> traitInfo.Templates |> Map.toList |> List.map fst
            | InterfaceTrait -> traitInfo.Signatures |> Map.toList |> List.map fst

        for requiredMethod in requiredMethods do
            let isImplemented =
                methods
                |> List.exists (function
                    | DDefun(name, _, _, _, _) -> name = requiredMethod
                    | _ -> false)

            if not isImplemented then
                failwithf
                    "Implementation of trait '%s' is missing required method '%s' at %s"
                    traitName requiredMethod (Lexer.formatPos r)

        // Register every method as an inline template — interface traits
        // included. A statically resolvable call is inlined whatever the kind of
        // trait it belongs to; the difference is only that an interface trait
        // also keeps its dictionary path for the generic case.
        //
        // The body stored is the untyped one. Re-inferring it at the splice is
        // what lets it take a type the trait signature could not express, and a
        // typed AST is not serializable anyway: `HMType` is full of mutable
        // metavariable cells.
        let finalEnv =
            methods
            |> List.fold
                (fun acc methodDecl ->
                    match methodDecl with
                    | DDefun(name, defunArgs, body, _, _) ->
                        let paramNames = mandatoryNames defunArgs

                        // Keyword and rest parameters would have to survive the
                        // splice as a calling convention, which a spliced body
                        // has no call to carry. Such a method simply is not
                        // inlineable; the landing pad still is.
                        let inlineable =
                            defunArgs |> List.forall (function MandatoryArg _ -> true | _ -> false)

                        if inlineable then
                            addInlineTemplate
                                traitName
                                name
                                implTarget.Ctor
                                { Params = paramNames
                                  Body = body
                                  // Filled in after inference, where a
                                  // name-to-module map exists.
                                  Qualification = Map.empty
                                  OriginModule = acc.CurrentModule }
                                acc
                        else
                            acc
                    | _ -> acc)
                regEnv

        // The dictionaries the class holds, named exactly as a constrained
        // function's parameters are — `Lowering` puts them in scope for the
        // method bodies under the same names, so a body cannot tell whether the
        // dictionary it dispatches through arrived as an argument or was stored
        // by the constructor.
        //
        // A constrained trait has no associated types (checked above), so the
        // dictionary's type is the trait applied to the one variable.
        let dictFields =
            implConstraints
            |> List.map (fun c ->
                let varName =
                    match c.TargetType with
                    | TVar v -> v
                    | other -> failwithf $"Internal error: impl constraint over %s{DotNetInterop.showType other}"

                dictParamName c.TraitName varName, TCon(c.TraitName, [ c.TargetType ]))

        finalEnv,
        sigs,
        [ TImpl(traitName, traitInfo.Kind, traitInfo.HoleArity, targetType, hmAssocBindings, dictFields, typedMethods, r) ]

    | DInlineImpl(traitName, methodName, ctor, originModule, parameters, body, qualification, r) ->
        // An inline template read back from a compiled module's metadata. Like
        // `DImplExtern` there is nothing to check and nothing to emit: the
        // landing pad is already compiled into the assembly that declared it,
        // and this is only the body to splice instead of calling it.
        let env =
            addInlineTemplate
                traitName
                methodName
                ctor
                { Params = parameters
                  Body = body
                  Qualification = Map.ofList qualification
                  OriginModule = originModule }
                env

        env, sigs, []

    | DImplExtern(traitName, targetTypeExpr, assocBindings, whereClause, r) ->
        // A bodyless implementation, read back from a compiled module's
        // metadata. Only the registry needs to learn about it: the methods are
        // already compiled into the assembly that declared it, so there is
        // nothing to type-check and nothing to emit.
        let traitName = originalName env.Registry traitName
        let whereClause = whereClause |> List.map (fun (t, v) -> originalName env.Registry t, v)
        let targetType = resolveTypeAnnotation env.Registry targetTypeExpr

        let typeKey =
            match implCtorKey targetType with
            | Some k -> k
            | None -> failwithf $"Trait implementations require concrete target types at %s{Lexer.formatPos r}"

        let traitInfo =
            match Map.tryFind traitName env.Registry.Traits with
            | Some info -> info
            | None ->
                failwithf $"Unknown trait '%s{traitName}' in imported implementation at %s{Lexer.formatPos r}"

        let hmAssocBindings =
            assocBindings
            |> List.map (fun (name, fType) -> name, resolveTypeAnnotation env.Registry fType)
            |> Map.ofList

        // The published `(where ...)`. An importing module builds the dictionary
        // for `(List int)` itself, so it has to know a `(->str int)` goes inside
        // — the impl class in the other assembly has no `Instance` to reach for.
        let implConstraints =
            whereClause
            |> List.map (fun (cTrait, varName) -> { TraitName = cTrait; TargetType = TVar varName })

        let implTarget = implTargetOf traitName traitInfo targetType implConstraints r
        addImplementation traitName typeKey targetType implTarget hmAssocBindings env, sigs, []

    // A spelling an import modifier produced for a type, a constructor, a trait
    // or a trait method. It binds nothing and emits nothing: what it does is
    // let `originalName` resolve the spelling away before any registry keyed on
    // the declaring module's own name is consulted.
    | DImportAlias(visible, original, kind, _) ->
        { env with
            Registry =
                { env.Registry with
                    ImportAliases =
                        Map.add
                            visible
                            { OriginModule = env.CurrentModule
                              OriginalName = original
                              Kind = kind }
                            env.Registry.ImportAliases } },
        sigs,
        []

/// Type-checks a group of declarations that share a signature scope: a module
/// body, or a whole program.
///
/// Signatures are collected up front so that declarations may refer to each
/// other out of order, which is why this cannot simply be a fold over
/// `checkDecl`. Signatures inherited from an enclosing group stay visible, with
/// the group's own taking precedence.
and private checkDeclGroup
    (env: Env)
    (sigs: Map<string, HMType * FType option * (string * string) list>)
    (decls: Decl list)
    : Env * Map<string, HMType * FType option * (string * string) list> * TDecl list =

    /// The registry a signature is *read* with: this one, plus the group's own
    /// type declarations.
    ///
    /// Signatures are resolved up front, before anything in the group is
    /// checked, and a `type` declared beside them had not been registered yet.
    /// For a union that is invisible — an unknown name resolves to a
    /// constructor of that name, which is exactly what the declaration
    /// registers — but an alias resolves to nothing at all, so
    /// `(: run/lines (-> ProcList (List string)))` took a `ProcList` that no
    /// later `(List ProcItem)` could unify with.
    ///
    /// The declarations are registered again by the fold below, in order and
    /// into the environment the group really uses. This copy is thrown away
    /// after the signatures have been read off it.
    ///
    /// Imports need no such treatment: `DImport` adds nothing here, because an
    /// imported module is a group of its own that has already been checked by
    /// the time this one starts.
    ///
    /// The spellings are folded in too. A type is registered under its key, so
    /// a signature written above the `type` it names — or above the import
    /// alias a `.dll`'s reader produced — resolves the bare name through the
    /// same table everything else does, and has to see it.
    let sigRegistry =
        decls
        |> List.fold
            (fun acc d ->
                match d with
                | DType(typeDefs, _) -> fst (registerTypeDefs false typeDefs acc)
                | DTypeRec(typeDefs, _) -> fst (registerTypeDefs true typeDefs acc)
                | DImportAlias(visible, original, kind, _) ->
                    { acc with
                        Registry =
                            { acc.Registry with
                                ImportAliases =
                                    Map.add
                                        visible
                                        { OriginModule = acc.CurrentModule
                                          OriginalName = original
                                          Kind = kind }
                                        acc.Registry.ImportAliases } }
                | _ -> acc)
            env
        |> fun withTypes -> withTypes.Registry

    // One name, two signatures. Silently unanswerable before this: the map
    // below is built from a list, so whichever came last won and the other was
    // dropped without a word. It became reachable when a `defun` was allowed to
    // write its own — the two spellings say the same thing, and saying it twice
    // is a mistake rather than a refinement.
    //
    // Trait method signatures are deliberately not in this: two traits
    // declaring a method of one name is a different question, answered by its
    // own diagnostic.
    let duplicateSignature =
        decls
        |> List.choose (function
            | DSignature(name, _, _, r) -> Some(name, r)
            | _ -> None)
        |> List.groupBy fst
        |> List.tryFind (fun (_, group) -> group.Length > 1)

    match duplicateSignature with
    | Some(name, (_, r) :: _) ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{name}' has more than one type signature. A (: %s{name} ...) and a `defun` that writes its own types both declare one, so write whichever of the two you meant and not both."
    | _ -> ()

    let explicitSigs =
        decls
        |> List.collect (function
            | DSignature(name, ftype, constraints, _) -> 
                [name, (resolveTypeAnnotation sigRegistry ftype, Some ftype, constraints)]
            // An inline trait's signatures are not `HMType`s and never can be:
            // they mention the constructor variable applied. They are read as
            // templates by `DTrait` instead, and there is nothing to inject here.
            | DTrait(_, _, holeArity, _, signatures, _, _, _) when holeArity = 0 ->
                signatures
                |> List.map (fun (name, ftype) ->
                    name, (resolveTypeAnnotation sigRegistry ftype, Some ftype, []))
            | _ -> [])
        |> Map.ofList

    /// A trait method's signature comes from its `def/trait`, whichever kind of
    /// trait that is, so exporting one is never missing a signature.
    ///
    /// The trait's own name is here too, and has to be: a trait standing for a
    /// .NET interface declares no methods, so there is no method name to
    /// publish it by, and `(export Num)` is the only way it can cross a module
    /// boundary at all.
    let traitMethodNames =
        decls
        |> List.collect (function
            | DTrait(traitName, _, _, _, signatures, _, _, _) ->
                traitName :: (signatures |> List.map fst)
            | _ -> [])
        |> Set.ofList

    /// Aliases bound by `import/extern` in this group.
    ///
    /// One of these has no signature and cannot be given one: it names a .NET
    /// *overload set*, and which member of it a call means is decided from that
    /// call's argument types. So it is exported as the import itself rather than
    /// as a type — the importing module re-resolves the overloads against the
    /// same metadata, exactly as this one did.
    let externAliases =
        decls
        |> List.collect (function
            | DImportExtern(specs, _) -> specs |> List.map (fun s -> s.Alias)
            | _ -> [])
        |> Set.ofList

    /// Names that already carry a signature from somewhere else: a second
    /// spelling this group declares, and anything an import brought in under
    /// any spelling.
    ///
    /// Rule 9's facade is exactly this — `(export http-get)` where `http-get`
    /// is an alias. The local-signature rule cannot apply to one, for the same
    /// reason it does not apply to `re-export`: the name was given its type
    /// where it was defined, and writing a second one here would be a copy that
    /// could disagree.
    let aliasedNames =
        decls
        |> List.collect (function
            | DAlias(newName, _, _) -> [ newName ]
            | _ -> [])
        |> Set.ofList

    /// The type names this group declares, and the `import/class` aliases it
    /// binds.
    ///
    /// A type has to be named in an `(export ...)` to cross at all, and it has
    /// no signature to be missing: what it publishes is its declaration. So the
    /// rule below has to know a type name when it sees one, or exporting one
    /// would fail asking for a signature that cannot be written.
    let declaredTypeNames =
        decls
        |> List.collect (function
            | DType(typeDefs, _)
            | DTypeRec(typeDefs, _) -> typeDefs |> List.map (fun td -> td.Name)
            | DImportClass(specs, _) -> specs |> List.map (fun s -> s.Alias)
            | _ -> [])
        |> Set.ofList

    decls
    |> List.iter (function
        | DExport(names, exprRange) ->
            for name in names do
                if
                    not (
                        Map.containsKey name explicitSigs
                        || Set.contains name traitMethodNames
                        || Set.contains name externAliases
                        || Set.contains name aliasedNames
                        || Set.contains name declaredTypeNames
                        || Map.containsKey name env.Registry.ImportAliases
                    )
                then
                    failwithf "Export Error: Exported item '%s' is missing a mandatory type signature at %s" name (Lexer.formatPos exprRange)
        | _ -> ())

    let combinedSigs = Map.fold (fun acc k v -> Map.add k v acc) sigs explicitSigs

    // Bind every function with a signature before checking them, 
    // allowing out-of-order and mutually recursive calls within the group.
    let declaredFunctions =
        decls
        |> List.choose (function
            | DDefun(name, _, _, colour, _) -> Some(name, colourEffect colour)
            | _ -> None)
        |> Map.ofList

    let envWithForwardDecls =
        explicitSigs
        |> Map.fold
            (fun (acc: Env) name (hmType, ftypeOpt, constraintPairs) ->
                match Map.tryFind name declaredFunctions with
                | None -> acc
                | Some effect ->

                    let (Scheme(vars, _, schemeType)) = generalize acc (recolour effect hmType)

                    let constraints =
                        constraintPairs
                        |> List.map (fun (traitName, varName) ->
                            { TraitName = originalName acc.Registry traitName; TargetType = TVar varName })

                    let bound =
                        addBinding
                            name
                            { Scheme = Scheme(vars, constraints, schemeType)
                              IsMutable = false }
                            acc

                    // Keyword and rest parameters need their metadata just as
                    // early: a forward call that passes a keyword argument, or
                    // omits an optional one, has nothing to resolve against
                    // without it.
                    match ftypeOpt with
                    | Some(TArrow(mandatory, keywords, restOpt, _, _, _)) ->
                        let funMeta =
                            { MandatoryCount = mandatory.Length
                              KeywordParams =
                                keywords |> List.map (fun (n, ft) -> n, resolveTypeAnnotation acc.Registry ft)
                              RestParam = restOpt |> Option.map (resolveTypeAnnotation acc.Registry) }

                        { bound with FunMetas = Map.add name funMeta bound.FunMetas }
                    | _ -> bound)
            env

    let finalEnv, finalSigs, typedDecls =
        decls
        |> List.fold
            (fun (currEnv, currSigs, accDecls) d ->
                let nextEnv, nextSigs, tDecls =
                    match d with
                    // Which implementation a failure came from. A method body
                    // is short, and often nobody wrote it — `type/derive`
                    // writes one per field, and every node a macro builds
                    // carries the *call site's* range, so the line reported is
                    // the derive form rather than the field that asked for the
                    // comparison.
                    | DImpl(traitName, target, _, _, _, ir) ->
                        try
                            checkDecl currEnv currSigs d
                        with ex when Diagnostics.isDiagnostic ex ->
                            let targetName =
                                match target with
                                | TName(n, _) -> n
                                | TApp(n, _, _) -> n
                                | _ -> "this type"

                            failwithf
                                $"%s{ex.Message}\n  in the implementation of '%s{traitName}' for '%s{targetName}' at %s{Lexer.formatPos ir}"
                    | _ -> checkDecl currEnv currSigs d

                (nextEnv, nextSigs, tDecls @ accDecls))
            (envWithForwardDecls, combinedSigs, [])

    finalEnv, finalSigs, List.rev typedDecls

// --- PIPELINE COORDINATION ---
/// Runs Hindley-Milner inference over a parsed program.
///
/// The result still contains high-level `TMatch` nodes: pattern matching is
/// translated straight to C# patterns by the code generator, and trait dispatch
/// is resolved afterwards by `Bjolang.Lowering`.
///
/// `Pipeline.loadModuleGraph` runs every file through `wrapInModule`, so in
/// practice `program` is a list of `DModule`s and the real work happens one
/// level down. The declarations are still handed to `checkDeclGroup` directly
/// rather than assumed to be wrapped, so a bare list type-checks the same way.
/// A module-level value has to have a type C# can write down.
///
/// It compiles to a static field of the module's class, and a static field
/// cannot be generic: there is no class for the type parameter to belong to. So
/// a value whose type is still open once the whole program has been checked has
/// nowhere to live. `(def bleh '())` is the honest example — perfectly sound,
/// `Nil` is a value and generalizing it is fine, it simply cannot be emitted.
///
/// A `defun` is exempt. A generic *method* is ordinary C#, and that is what a
/// polymorphic top-level function becomes.
///
/// Run at the very end rather than at each declaration, because a type left open
/// by its own initializer may still be pinned down by a later one — a
/// `(def/mutable pending Nil)` written to further down the module is settled by
/// the time this looks.
let private checkModuleValuesAreConcrete (registry: TraitRegistry) (decls: TDecl list) : unit =
    let check (form: string) (name: string) (t: HMType) (r: Range) =
        // Both halves of "open": a variable generalization quantified, and a
        // metavariable nothing ever resolved. Either one leaves the code
        // generator with a type it cannot name.
        let isOpen =
            not (List.isEmpty (freeTVars registry t)) || not (List.isEmpty (freeVars registry t))

        if isOpen then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: the type of '%s{name}' is still open, and a module-level %s{form} compiles to a static field — which cannot be generic, because there is no class for the type parameter to belong to. Give it a signature that pins it down, like (: %s{name} (List int)), or make it a function, whose type parameters do have somewhere to live."

    let rec go (ds: TDecl list) =
        for d in ds do
            match d with
            | TModule(_, inner, _) -> go inner
            | TDef(name, _, t, r) -> check "def" name t r
            | TDefMutable(name, _, t, r) -> check "def/mutable" name t r
            | TDefTuple(names, _, t, r) -> check "def" (String.concat ", " names) t r
            | _ -> ()

    go decls

/// Reports a top-level definition that binds over a trait method.
///
/// Legal, and since a method name became an ordinary binding it is also silent:
/// the definition wins for the whole module and nothing says so. What it almost
/// always means is `def/impl` — somebody writing `(defun (= a b) ...)` is
/// implementing equality rather than defining a function called `=`. The case
/// that prompted this was `sign`, a `Num` method by `#:clr-member`: a program's
/// own `(defun (sign n) ...)` used to be silently dead, and its call sites
/// reported an arity error against the programmer's own line.
///
/// Top level only. A parameter or a `let` is a local decision in a scope its
/// reader can see the whole of — `lib/std/fmt.bjo` binds a `start` — and warning
/// about one would be noise. A module-level binding is visible from everywhere
/// in the file, which is what makes it worth a word.
///
/// A `def/impl`'s methods are not reached: they sit inside `TImpl`, and this
/// descends through `TModule` and nothing else.
let private warnAboutShadowedMethods (registry: TraitRegistry) (decls: TDecl list) : unit =
    let check (form: string) (name: string) (r: Range) =
        match Map.tryFind name registry.TraitMethods with
        | Some traitName ->
            Diagnostics.warn
                $"'%s{name}' is a method of the trait '%s{traitName}', and this %s{form} binds over it at %s{Lexer.formatPos r}. A call to '%s{name}' written in this module reaches the definition rather than dispatching, and so does one in any module that imports it. To implement the method for a type of your own, write (def/impl (%s{traitName} YourType) (defun (%s{name} ...) ...))."
        | None -> ()

    let rec go (ds: TDecl list) =
        for d in ds do
            match d with
            | TModule(_, inner, _) -> go inner
            | TDefun(name, _, _, _, _, _, _, _, r) -> check "definition" name r
            | TDef(name, _, _, r) -> check "definition" name r
            | TDefMutable(name, _, _, r) -> check "mutable definition" name r
            | _ -> ()

    go decls

let checkProgram (initialEnv: Env) (program: Decl list) : Env * TDecl list =
    let finalEnv, _, typedDecls = checkDeclGroup initialEnv Map.empty program
    // Anything raised outside a declaration that generalizes still has to be
    // answered for.
    solvePending finalEnv
    checkModuleValuesAreConcrete finalEnv.Registry typedDecls
    warnAboutShadowedMethods finalEnv.Registry typedDecls
    finalEnv, typedDecls
