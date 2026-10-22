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

module Bjolang.InferExpr

open Bjolang.Lexer
open Bjolang.Ast
open Bjolang.TypedAST
open Bjolang.Unification
open Bjolang.TypeEnv
open Bjolang.Annotations
open Bjolang.Traits
open Bjolang.ForeignTyping

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
    | EArray _ -> Some [ "Array" ]
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

/// `a`, `a or b`, `a, b or c`.
let private orList (names: string list) : string =
    match List.rev names with
    | [] -> ""
    | [ one ] -> one
    | last :: earlier -> String.concat ", " (List.rev earlier) + " or " + last

/// The places already warned about. A body can be inferred more than once,
/// and a use warns only the first time.
let private deprecationsWarned = System.Collections.Generic.HashSet<string>()

/// Warns when `name` is deprecated and still means the binding that was.
let private warnIfDeprecated (env: Env) (name: string) (r: Range) =
    match Map.tryFind name env.DeprecatedNames with
    | Some instead ->
        let at = Lexer.formatPos r

        if deprecationsWarned.Add(name + "@" + at) then
            Diagnostics.warn $"`%s{name}` at %s{at} is deprecated, %s{instead}."
    | None -> ()

// ---------------------------------------------------------------------------
// Grammars: the questions that need no inference
// ---------------------------------------------------------------------------

/// Runs a grammar's check on the atom of a literal, while the literal is
/// compiled: the module key, the function's name in that module, and the atom
/// as the runtime holds it. `Error` is the check's `Err` text, or why the check
/// could not run.
///
/// A hook because the compiled module is loaded by `Pipeline`, which is
/// compiled after this file. `Pipeline` sets it when it loads an assembly.
let mutable runGrammarCheck: string -> string -> obj -> Result<unit, string> =
    fun moduleKey name _ -> Error $"the module %s{moduleKey} that declares %s{name} is not loaded, so the check can not run"

/// The atom of a literal as the runtime holds it, for `runGrammarCheck`.
let private grammarAtomValue (atomType: HMType) (expr: Expr) : obj option =
    let invariant = System.Globalization.CultureInfo.InvariantCulture

    match expr, atomType with
    | EString(s, _), _ -> Some(box (BjoString.Utf8String.FromUtf16 s))
    | EQuotedSymbol(s, _), _ -> Some(box (BjolangRuntime.Symbol.Intern s))
    | EBool(b, _), _ -> Some(box b)
    | EInt(n, _), TCon(name, []) when name = TypeConstants.Int32Name -> Some(box (System.Int32.Parse(n, invariant)))
    | EInt(n, _), TCon(name, []) when name = TypeConstants.Int64Name -> Some(box (System.Int64.Parse(n, invariant)))
    | EInt(n, _), TCon(name, []) when name = TypeConstants.DoubleName -> Some(box (System.Double.Parse(n, invariant)))
    | _ -> None

/// Whether an element of a quoted literal is written data. Anything else is a
/// value spliced in with `,`: the reader puts the expression itself in the
/// literal, so its kind of node is the only thing that tells the two apart.
let private isWrittenLiteral (expr: Expr) =
    match expr with
    | EList _
    | EVec _
    | EArray _
    | EString _
    | EQuotedSymbol _
    | EInt _
    | EChar _
    | EBool _
    | EKeyword _ -> true
    | _ -> false

/// The production a type is, with its type arguments, if it is one.
let private grammarOf (registry: TraitRegistry) (t: HMType) : (string * HMType list * GrammarProduction) option =
    match prune registry t with
    | TCon(key, args) -> Map.tryFind key registry.Grammars |> Option.map (fun p -> key, args, p)
    | _ -> None

/// A production's item types with the expected type's arguments put in for
/// the grammar's parameters.
let private grammarInstance (p: GrammarProduction) (args: HMType list) : HMType -> HMType =
    if p.TypeParams.Length = args.Length && not args.IsEmpty then
        substTypeVars (List.zip p.TypeParams args |> Map.ofList)
    else
        id

/// Whether an item keeps a value. The rules are `Grammar.itemValueType`'s.
let rec private grammarKeeps (item: GrammarItem) =
    match item with
    | GrType _
    | GrSpliceOnly _
    | GrOpt _
    | GrEach _ -> true
    | GrKeyword _ -> false
    | GrList(_, elements)
    | GrVec elements -> elements |> List.exists (fun e -> e.Rep <> GOne || grammarKeeps e.Item)
    | GrOr _ -> true

/// Whether an element can match nothing, so that what follows it can come
/// first.
let private grammarCanBeEmpty (e: GrammarElement) =
    e.Rep = GStar
    || (match e.Item with
        | GrOpt _ -> true
        | GrEach fields ->
            fields
            |> List.forall (fun (_, i) ->
                match i with
                | GrOpt _ -> true
                | _ -> false)
        | _ -> false)

/// What a literal can start with, as far as a grammar decides by it.
type private GrammarStart =
    /// `'kw`: the symbol alone.
    | StartKeyword of string
    /// `(kw ...)`: a list with the keyword at its head.
    | StartKeywordList of string
    | StartList
    | StartVec
    | StartArray
    | StartAtom of string
    | StartSplice of string
    /// A type the grammar does not decide by: a parameter, or a type outside
    /// any grammar that a literal is elaborated into by its own rules.
    | StartAny

/// The starts of an item. Used to check a grammar where it is declared: two
/// alternatives with a start in common can not be told apart.
let rec private grammarStarts
    (registry: TraitRegistry)
    (inst: HMType -> HMType)
    (visited: Set<string>)
    (item: GrammarItem)
    : Set<GrammarStart> =
    match item with
    | GrKeyword kw -> Set.singleton (StartKeyword kw)
    | GrList(Some kw, _) -> Set.singleton (StartKeywordList kw)
    | GrList(None, _) -> Set.singleton StartList
    | GrVec _ -> Set.singleton StartVec
    | GrOpt inner -> grammarStarts registry inst visited inner
    | GrSpliceOnly t -> Set.singleton (StartSplice(DotNetInterop.showType (inst t)))
    | GrOr alternatives -> alternatives |> List.map (snd >> grammarStarts registry inst visited) |> Set.unionMany
    | GrEach fields -> fields |> List.map (snd >> grammarStarts registry inst visited) |> Set.unionMany
    | GrType t ->
        let t = inst t

        match grammarOf registry t with
        | Some(key, _, _) when visited.Contains key -> Set.empty
        | Some(key, args, p) -> productionStarts registry (Set.add key visited) args p
        | None ->
            match prune registry t with
            | TVar _
            | TMeta _ -> Set.singleton StartAny
            | TCon(name, _) when Map.containsKey name registry.Unions -> Set.singleton StartAny
            | TCon("List", _) -> Set.singleton StartList
            | TCon("Vec", _) -> Set.singleton StartVec
            | TCon("Array", _) -> Set.singleton StartArray
            | TCon(name, []) -> Set.singleton (StartAtom name)
            | other -> Set.singleton (StartSplice(DotNetInterop.showType other))

and private productionStarts registry visited args (p: GrammarProduction) : Set<GrammarStart> =
    let inst = grammarInstance p args

    match p.Shape with
    | GsUnion alternatives -> alternatives |> List.map (snd >> grammarStarts registry inst visited) |> Set.unionMany
    | GsAlias item
    | GsNested item -> grammarStarts registry inst visited item
    | GsSequence elements -> sequenceStarts registry inst visited elements
    | GsChecked(atom, _, _) -> grammarStarts registry inst visited (GrType atom)

and private sequenceStarts registry inst visited (elements: GrammarElement list) : Set<GrammarStart> =
    match elements with
    | [] -> Set.empty
    | e :: rest ->
        let own = grammarStarts registry inst visited e.Item

        if grammarCanBeEmpty e then
            Set.union own (sequenceStarts registry inst visited rest)
        else
            own

/// Whether `expr` can start `item`, decided without inferring anything.
///
/// A spliced value is not decided here: its type is what says where it goes,
/// so it fits every item that can hold a value.
let rec private grammarFits
    (registry: TraitRegistry)
    (inst: HMType -> HMType)
    (visited: Set<string>)
    (item: GrammarItem)
    (expr: Expr)
    : bool =
    let starts = grammarStarts registry inst visited item

    let fitsStart (s: GrammarStart) =
        match s, expr with
        | StartAny, _ -> true
        | StartKeyword kw, EQuotedSymbol(sym, _) -> sym = kw
        | StartKeywordList kw, EList(EQuotedSymbol(sym, _) :: _, _) -> sym = kw
        | StartList, EList _ -> true
        | StartVec, EVec _ -> true
        | StartArray, EArray _ -> true
        | StartAtom name, _ ->
            match literalPayloadHeads expr with
            | Some heads -> List.contains name heads
            | None ->
                match expr with
                | EChar _ -> name = TypeConstants.CharName
                | EBool _ -> name = TypeConstants.BooleanName
                | EKeyword _ -> name = TypeConstants.KeywordName
                | _ -> false
        | StartSplice _, _ -> not (isWrittenLiteral expr)
        | _ -> false

    if not (isWrittenLiteral expr) then
        // Any item that keeps a value can take one spliced in: a type, also
        // a production whose alternatives all start with keywords, since a
        // value of the production itself is one.
        let rec takesSplice (item: GrammarItem) =
            match item with
            | GrType _
            | GrSpliceOnly _ -> true
            | GrOpt inner -> takesSplice inner
            | GrOr alternatives -> alternatives |> List.exists (snd >> takesSplice)
            | GrEach fields -> fields |> List.exists (snd >> takesSplice)
            | GrKeyword _
            | GrList _
            | GrVec _ -> false

        takesSplice item
    else
        starts |> Set.exists fitsStart

/// The start a literal has as a keyword: the head of a list, or a symbol
/// alone.
let private literalKeywordStart (expr: Expr) : GrammarStart option =
    match expr with
    | EList(EQuotedSymbol(kw, _) :: _, _) -> Some(StartKeywordList kw)
    | EQuotedSymbol(kw, _) -> Some(StartKeyword kw)
    | _ -> None

/// Whether an item starts with the keyword the literal starts with. A keyword
/// has priority over any other item at the same place: `'(pipe ...)` is a
/// pipe even where a list of symbols could also be read.
let private grammarClaims registry inst (item: GrammarItem) (expr: Expr) =
    match literalKeywordStart expr with
    | Some start -> grammarStarts registry inst Set.empty item |> Set.contains start
    | None -> false

/// A type's name with the indefinite article it is read with: "an int",
/// "a string". A name that starts with a, e, i or o takes "an". One that
/// starts with u does not, as the u of `uint` and `unit` is said "you".
let private withArticle (name: string) : string =
    if name.Length > 0 && "aeioAEIO".Contains name[0] then $"an %s{name}" else $"a %s{name}"

/// How a diagnostic names what an item expects.
let rec private describeGrammarItem (registry: TraitRegistry) (inst: HMType -> HMType) (item: GrammarItem) : string =
    match item with
    | GrKeyword kw -> $"`%s{kw}`"
    | GrList(Some kw, _) -> $"(%s{kw} ...)"
    | GrList(None, _) -> "a list"
    | GrVec _ -> "a vec"
    | GrOpt inner -> describeGrammarItem registry inst inner
    | GrSpliceOnly t -> $"%s{withArticle (DotNetInterop.showType (inst t))} spliced in with ,"
    | GrOr alternatives -> alternatives |> List.map (snd >> describeGrammarItem registry inst) |> orList
    | GrEach fields -> fields |> List.map (snd >> describeGrammarItem registry inst) |> orList
    | GrType t ->
        match grammarOf registry (inst t) with
        | Some(_, _, p) -> p.Name
        | None ->
            match prune registry (inst t) with
            | TCon(name, []) when name = TypeConstants.SymbolName -> "a symbol"
            | TCon(name, []) when name = TypeConstants.StringName -> "a string"
            | other -> withArticle (DotNetInterop.showType other)

/// How a diagnostic names what a literal holds at the place it went wrong.
let private describeFound (expr: Expr) : string =
    match expr with
    | EList(EQuotedSymbol(kw, _) :: _, _) -> $"(%s{kw} ...)"
    | EList([], _) -> "an empty list"
    | EList _ -> "a list"
    | EVec _ -> "a vec"
    | EArray _ -> "an array"
    | EQuotedSymbol(s, _) -> $"the symbol `%s{s}`"
    | EString(s, _) -> $"the string \"%s{s}\""
    | EInt(n, _) -> $"the number %s{n}"
    | EChar _ -> "a character"
    | EBool _ -> "a boolean"
    | EKeyword(k, _) -> $"the keyword #:%s{k}"
    | ESplice _ -> "a splice ,@"
    | _ -> "a value spliced in with ,"

let private grammarError (r: Range) (context: string) (message: string) : 'a =
    failwithf $"Type Error at %s{Lexer.formatPos r}: %s{context}: %s{message}"

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
        | EVec _
        | EArray _ -> true
        | _ -> false)

/// Whether any element of a literal is a `,@`.
///
/// The question both literal paths ask first, because the answer decides
/// between the node they have always built and the appended one below. A
/// literal with no splice in it must reach the old path untouched: that is what
/// keeps the emitted C# for every program written before this byte for byte
/// what it was.
let private hasSplice (elements: Expr list) : bool =
    elements
    |> List.exists (function
        | ESplice _ -> true
        | _ -> false)

/// A literal's elements grouped into the pieces it is appended from: maximal
/// runs of ordinary elements, and one piece per splice.
///
/// `Choice1Of2` is a run, which becomes exactly one literal node and so goes
/// through the ordinary element path, injection and all. `Choice2Of2` is a
/// splice's expression and its `,@`'s own range, which is what a diagnostic
/// about it has to point at.
let private splicePieces (elements: Expr list) : Choice<Expr list, Expr * Range> list =
    let rec go acc pending remaining =
        let flush () =
            if List.isEmpty pending then acc else Choice1Of2(List.rev pending) :: acc

        match remaining with
        | [] -> List.rev (flush ())
        | ESplice(inner, sr) :: rest -> go (Choice2Of2(inner, sr) :: flush ()) [] rest
        | item :: rest -> go acc (item :: pending) rest

    go [] [] elements

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
            $"Type Error at %s{Lexer.formatPos r}: nothing here says what this %s{kind} literal holds, and its elements do not agree on a type by themselves:\n  %s{shown[0]}\n  %s{shown[1]}\nA literal with a nested list among its elements is read by a grammar, and which grammar reads it comes from the type expected where it is written. A generic parameter expects nothing in particular, so there is none: annotate the value and pass that, as in (def (: procs (List ProcList)) (list '(...)))."

// ---------------------------------------------------------------------------
// `def` propagation
// ---------------------------------------------------------------------------

/// The cases of the union `t` is, as name and payload count, or `None` when `t`
/// is not a union at all.
///
/// A declared union wins over a builtin of the same name, as it does in
/// `Exhaustiveness`: a module that defines its own `Result` propagates its own
/// cases.
let private unionCasesOf (registry: TraitRegistry) (t: HMType) : (string * int) list option =
    match t with
    | TCon(name, args) ->
        match Map.tryFind name registry.Unions with
        | Some(_, cases) -> Some(cases |> List.map (fun (caseName, payloads) -> caseName, payloads.Length))
        | None ->
            match name, args with
            | "Option", [ _ ] -> Some [ "None", 0; "Some", 1 ]
            | "Result", [ _; _ ] -> Some [ "Err", 1; "Ok", 1 ]
            | "List", [ _ ] -> Some [ "Nil", 0; "Cons", 2 ]
            | _ -> None
    | _ -> None

/// Whether a pattern matches every value of its type.
let rec private alwaysMatches (pat: TypedPattern) : bool =
    match pat.Node with
    | TPWildcard
    | TPIdent _ -> true
    | TPAs(inner, _) -> alwaysMatches inner
    | TPTuple items -> List.forall alwaysMatches items
    | TPRecord(_, fields) -> fields |> List.forall (snd >> alwaysMatches)
    | TPAnd alts -> List.forall alwaysMatches alts
    | _ -> false

/// The constructors of `t` that `binder` leaves out, as name and payload count.
///
/// `None` where the leftovers are not a set of constructors: a literal, a
/// sequence pattern, a view, or a constructor whose fields are themselves
/// refutable. `:propagate` rebuilds one constructor per leftover case, so it
/// has nothing to build from a `None` and refuses it.
let private uncoveredCases
    (registry: TraitRegistry)
    (t: HMType)
    (binder: TypedPattern)
    : (string * int) list option =
    let rec covered (pat: TypedPattern) =
        match pat.Node with
        | TPAs(inner, _) -> covered inner
        | TPConstruct(name, args) when List.forall alwaysMatches args -> Some name
        | _ -> None

    if alwaysMatches binder then
        Some []
    else
        match covered binder, unionCasesOf registry t with
        | Some name, Some cases -> Some(cases |> List.filter (fun (c, _) -> c <> name))
        // A constructor pattern on a type with no cases to list is a record or
        // a CLR class: there is one way to build one, so the pattern matches
        // every value of it.
        | Some _, None -> Some []
        | None, _ -> None

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
      // A *cell*, not `ESync`, because a body-local function has no signature
      // and so nothing to declare a colour in — which by this language's own
      // rule means the colour is inferred. `EffectGraph` binds it once it has
      // seen the body: a local function that reaches a yield point becomes an
      // `async` C# local function, and one that does not stays ordinary.
      //
      // The cell is shared by every reference, which is what makes this work
      // with no repainting anywhere. `generalizeLocal` quantifies `MetaVar`s
      // and an `EffectCell` is not one, and `instantiate` freshens `EPoly` and
      // passes every other effect through — so each use of the name, including
      // a recursive one, reads back the same cell the binding decided.
      FunType = TFun(argTypes, retType, freshEffect ())
      Meta =
        { MandatoryCount = mandatory.Length
          KeywordParams = keywords |> List.map (fun (n, t, _) -> n, t)
          RestParam = rest |> Option.map snd } }

/// The arguments of a call the compiler wrote, with the user's own mentions of
/// its head moved out of the way.
///
/// `unshadow` gives the head its module-level meaning back, and the arguments
/// are inferred in that same environment, so a local of the same spelling
/// mentioned in them would mean the module-level one too: a parameter `str`
/// inside `#"${str}"`, or `bind` inside a `do`. Every mention the compiler wrote
/// is an `EResolved`, which `renameFree` leaves alone, so what it renames is what
/// the user wrote. The local goes in under a spelling no source can write, and
/// the answer puts the name back in the typed tree.
let private shieldLocal (name: string) (env: Env) (args: Expr list) : Env * Expr list * (TypedExpr -> TypedExpr) =
    let local = Map.tryFind name env.Bindings
    let escape = Map.tryFind name env.Escapes
    let shadowed = escape.IsSome || (local.IsSome && local <> Map.tryFind name env.Resolved)

    let mentioned () =
        args |> List.exists (fun a -> AlphaRename.freeNames Set.empty a |> Seq.contains name)

    if not shadowed || not (mentioned ()) then
        env, args, id
    else
        let hidden = $"%s{name} (local)"
        let carry (m: Map<string, 'v>) = match Map.tryFind name m with Some v -> Map.add hidden v m | None -> m

        { env with
            Bindings = carry env.Bindings
            FunMetas = carry env.FunMetas
            Escapes = carry env.Escapes },
        args |> List.map (AlphaRename.renameFree (Map.ofList [ name, hidden ])),
        AlphaRename.applyQualification (Map.ofList [ hidden, name ])

/// A value of an ordinary function type in a place that expects a bjoroutine,
/// wrapped in a bjoroutine that calls it. Call this after `expected` and the
/// value's type were unified.
///
/// `unifyEffect` accepts the pair: a function that never suspends can be used
/// where one that can suspend is expected. But in C# the two are different
/// delegate types, `Func<A,B>` and `Func<A,Fiber<B>>`. For a positional
/// argument, `Codegen` wraps the value in `Colour.Lift`, because the call
/// gives it the parameter type. A record field, a keyword argument or a
/// return value has no such type in `Codegen`, so these are wrapped here,
/// where the expected type is known.
///
/// The value is evaluated once, where it is written. A name or a lambda is
/// called directly from the wrapper. Any other expression is bound to a
/// local first, because the wrapper runs it again at each call.
///
/// The lambda keeps its own colour: a `fun` that suspends is still refused by
/// `ColourCheck`, as it is in any other place.
let bridgeToSuspending (env: Env) (expected: HMType) (typed: TypedExpr) : TypedExpr =
    match prune env.Registry expected, prune env.Registry typed.Type with
    | TFun(paramTypes, ret, _) as wanted, supplied when TypeVisitor.liftsToSuspending wanted supplied ->
        let r = typed.Range
        let names = paramTypes |> List.map (fun _ -> Gensym.fresh "__lift_arg")

        let args: TypedExpr list =
            List.map2 (fun name t -> { Type = t; Range = r; Node = TIdent(name, []) }) names paramTypes

        let callee, bind =
            match typed.Node with
            | TIdent _
            | TLambda _ -> typed, id
            | _ ->
                let local = Gensym.fresh "__lifted"

                { typed with Node = TIdent(local, []) },
                fun (body: TypedExpr) ->
                    { Type = wanted
                      Range = r
                      Node = TLet(local, false, noParams, typed, body) }

        bind
            { Type = wanted
              Range = r
              Node =
                TLambda(
                    names,
                    { Type = ret
                      Range = r
                      Node = TApply(callee, args, []) }
                ) }
    | _ -> typed

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
    // The site an equation deferred in here reports, if it fails later.
    let outer = Unification.inferenceSite
    Unification.inferenceSite <- Some(exprRange expr)

    try
        try
            inferNode env (resolveAliasedHead env expr)
        with ex when Diagnostics.needsLocation ex ->
            raise (Diagnostics.withLocation (exprRange expr) ex)
    finally
        Unification.inferenceSite <- outer

/// Rewrites a head identifier that is a prefixed spelling of a constructor, a
/// record type or a trait method back to the name it stands for.
///
/// Once, here, rather than at each of the guards in `inferNode`: that function
/// dispatches on `EIdent` and on `EApp(EIdent ...)` in a dozen places, and a
/// spelling is not meant to be visible to any of them.
///
/// A *binding* of that name is not a spelling of anything, and wins. Without
/// this the rewrite ran before scope was consulted, so a lowercase union case
/// beat every binder: `(let ((counting 7)) (int->string counting))` reported
/// `int` against `(prelude/Counting ?a)` — the reference had already become the
/// constructor's key, and the local was never asked about. That made `mapping`,
/// `counting`, `folding` and their four siblings unusable as variable names
/// anywhere in a program that imports the prelude, which is every program.
///
/// The test is `Bindings` rather than a kind, because that is what a spelling
/// resolves *past*. A constructor's key is what holds the binding, so a bare
/// name is in `Bindings` only when something else put it there.
and private resolveAliasedHead (env: Env) (expr: Expr) : Expr =
    let resolve (name: string) =
        // A binder inside this declaration wins; a name that still means what it
        // meant at module level does not. The comparison is against `Resolved`
        // rather than a mere `containsKey` because a declaration's own name can
        // be bound under its bare spelling too, and that binding *is* what the
        // spelling stands for rather than something in its way.
        if Map.tryFind name env.Bindings <> Map.tryFind name env.Resolved then
            name
        // A name several unions have a case of is resolved by type, in
        // `inferCheckedNode` or as an open case.
        elif not (caseCandidates env name).IsEmpty then
            name
        else
            originalName env.Registry name

    // A name two imports offer as different things, used where it reaches the
    // module level. A local of that name is a name of its own.
    //
    // By reference, not by value: a parameter `f` of the imported `f`'s type is
    // a binding equal to the import's, and it is still a local.
    let checkAmbiguous (name: string) (r: Range) =
        let isLocal =
            match Map.tryFind name env.Bindings, Map.tryFind name env.Resolved with
            | Some here, Some atModule -> not (obj.ReferenceEquals(here, atModule))
            | Some _, None -> true
            | None, _ -> false

        if not isLocal then
            Diagnostics.refuseAmbiguous Diagnostics.ValueName r name

    match expr with
    | EIdent(name, r) ->
        checkAmbiguous name r
        let original = resolve name
        if original = name then expr else EIdent(original, r)
    | EApp(EIdent(name, ir), args, r) ->
        checkAmbiguous name ir
        let original = resolve name
        if original = name then expr else EApp(EIdent(original, ir), args, r)
    | _ -> expr

and private inferNode (env: Env) (expr: Expr) : HMType * TypedExpr =
    match expr with
    // `ColourTwins.resolveByColour` replaces every one in a declaration before
    // it is checked. One that gets here is in code checked some other way.
    // A field or a call, where the head was not a record type: so it is the
    // call. `inferRecordConstruct` takes the field.
    | EApp(EResolved(marker, _), [ _; _; call ], _) when marker = fieldOrCallMarker -> infer env call
    | EApp(EResolved(marker, _), [ EString(message, _) ], _) when marker = callErrorMarker -> failwith message

    | EApp(EResolved(marker, _), _, r) when marker = byColourMarker ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: (by-colour ...) chooses a body by the colour of the definition it is in, and this one is not in a definition, or is in a body spliced from another module. Write it in a defun, a defbjo or a defbjouble."
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

    | EApp(EIdent(name, _), args, r) when Map.containsKey name env.Escapes ->
        inferEscapeCall env name args r

    | EIdent(name, r) when Holes.isHoleName env name -> inferHole env name r

    | EIdent(name, r) when Map.containsKey name env.Escapes ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: `%s{name}` is an escape, not a value: it can only be applied inside its own `with-return`."

    | EWithReturn(name, body, r) ->
        // Every rule about where `name` may be applied is a question about
        // shape, so all of them are decided here, over the body as written,
        // before a single form in it is typed.
        Hygiene.checkEscapeUses name body

        let resultType = freshMeta ()
        let label = Gensym.fresh "__ret"

        let bodyEnv =
            { env with
                Escapes = Map.add name { Label = label; Result = resultType } env.Escapes }

        let bodyType, typedBody = infer bodyEnv body
        unify env.Registry bodyType resultType

        resultType,
        { Type = resultType
          Range = r
          Node = TWithReturn(label, typedBody) }

    | EDefMatch(binder, scrutinee, failure, sequel, r) ->
        inferDefMatch infer env binder scrutinee failure sequel r


    // `std/eq`'s own equality primitives, refused everywhere else. See
    // `Naming.eqPrivateBindings` for why they are shut away at all.
    //
    // This refusal is also the recursion diagnostic for materialization: an
    // `Eq` impl written as `(clr-equals a b)` would emit an `Equals` that
    // calls the impl that calls `Equals`, forever. Refusing the *name* is
    // deliberately blunter than scanning impl bodies — a shallow body check
    // misses the same call one helper function away, and there is no use of
    // these primitives outside std/eq that is not that loop waiting to happen.
    | EIdent(name, r) when
        Set.contains name Naming.eqPrivateBindings
        // The module whose code the reference is, by its simple name.
        && Naming.moduleNameOfPath (codeModule env r) <> Naming.eqModuleName
        ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{name}' is private to std/eq. It is .NET's equality, and a type's own `Eq` implementation is what .NET equality is made *of* — writing one in terms of the other is a loop. Use structural-equals and structural-hash for the field-by-field comparison, compare the fields yourself, or derive."

    // A trait method written where a value is wanted, rewritten into the lambda
    // it stands for: `even?` becomes `(fun (x) (even? x))`.
    //
    // C# has no method group to hand over — an interface trait's method is a
    // slot on an interface and an inline trait's is not emitted at all — so a
    // bare reference used to reach `Codegen` as a name with nothing behind it.
    // The call inside the lambda is in call position like any other, so it
    // dispatches normally and the obligation it raises is discharged at
    // whatever type the lambda is used at.
    //
    // This is the rewrite the parser makes for operators, made here because
    // only the registry knows which names are methods. `TraitMethodNames`
    // leaves out a name that something has bound over, so a local of the same
    // name still wins; and the head of an application never arrives here,
    // because `EApp` matches its own callee first.
    | EIdent(name, r) when
        Set.contains name env.TraitMethodNames
        && (match traitMethodArity env name with
            | Some n -> n > 0
            | None -> false)
        ->
        let arity = Option.defaultValue 0 (traitMethodArity env name)
        let ps = List.init arity (fun _ -> Gensym.fresh "eta")

        infer env (EFun(ps, EApp(EIdent(name, r), ps |> List.map (fun p -> EIdent(p, r)), r), Ordinary, r))

    // An inline trait's methods are never bound as values: there is no single
    // scheme they could be bound under, which is the whole reason the trait is
    // inline-only.
    | EIdent(name, r) when
        Map.containsKey name env.Registry.TraitMethods
        && not (Map.containsKey name env.Bindings)
        ->
        let traitName = env.Registry.TraitMethods[name]

        failwithf
            $"Type Error at %s{Lexer.formatPos r}: '%s{Naming.showTraitName name}' is a method of the inline-only trait '%s{Naming.showTraitName traitName}' and has no value form. Apply it directly, or wrap it in a lambda at a known type."

    | EIdent(name, r) when
        not (Map.containsKey name env.Bindings)
        && not (name.EndsWith ".")
        && name.Contains "."
        && Map.containsKey (name.Substring(0, name.LastIndexOf ".")) env.Registry.ClrClasses
        ->
        inferStaticMember env name r


    | EIdent(name, r) when Map.containsKey name env.Registry.ClrExterns && not (Map.containsKey name env.Bindings) ->
        inferExternValue env name r


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

    | EIdent(name, r) when isDotName env name ->
        inferDotValue env name r

    | EIdent(name, r) when not (caseCandidates env name).IsEmpty ->
        inferOpenCase env name (caseCandidates env name) r

    | EIdent(name, r) ->
        inferIdent env name r


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
    | EResolved(name, r) ->
        let t, te = infer (unshadow name env) (EIdent(name, r))
        t, requalifyResolved name env te

    | EFun(args, body, colour, r) -> inferLambda None env args body colour r

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
    | EApp(EResolved(name, mr), args, r) ->
        let shielded, args, restore = shieldLocal name env args
        let t, te = infer (unshadow name shielded) (EApp(EIdent(name, mr), args, r))
        t, requalifyResolved name env (restore te)

    // A case that cases of several unions have the name of. The number of
    // payloads may leave one; otherwise the case is chosen once the type of the
    // value made says which union it is.
    | EApp(EIdent(name, ir), args, r) when not (caseCandidates env name).IsEmpty ->
        match candidatesTaking env args.Length (caseCandidates env name) with
        | [ only ] ->
            chooseCase ir name only
            infer env (EApp(EIdent(only, ir), args, r))
        | candidates ->
            let headType, head = inferOpenCase env name candidates ir
            let argTypes, typedArgs = args |> List.map (infer env) |> List.unzip
            let result = freshMeta ()
            unify env.Registry headType (tfun argTypes result)

            result,
            { Type = result
              Range = r
              Node = TApply(head, typedArgs, []) }

    | EApp(EIdent(methodName, _), args, r) when Set.contains methodName env.TraitMethodNames ->
        inferTraitMethodCall env methodName args r


    | EApp(EIdent(recordTypeName, hr), args, r) when Map.containsKey recordTypeName env.Registry.Records ->
        if not (representationVisible env.Registry (codeModule env hr) recordTypeName) then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: '%s{Naming.showTypeName recordTypeName}' cannot be constructed here. %s{opaqueTypeNote env.Registry recordTypeName}"

        inferRecordConstruct env recordTypeName args r


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

            // A record's fields are read with `.name`. Only said when the
            // field exists: otherwise the hint would name a field that is not
            // there, and the .NET error below is the accurate one.
            match prune env.Registry targetType with
            | TCon(recordTypeName, _) when
                Map.containsKey recordTypeName env.Registry.Records
                && (Map.tryFind propName env.Registry.RecordFields
                    |> Option.defaultValue []
                    |> List.contains recordTypeName)
                ->
                failwithf
                    $"Type Error at %s{where}: '%s{name}' reads a .NET property, but its target has the record type %s{DotNetInterop.showType targetType}, where '%s{propName}' is a field. Write (.%s{propName} ...) to read it."
            | _ -> ()

            let clrTarget = receiverClrType where name targetType

            let propType, typedTarget =
                onStringOrClrString env.Registry targetType typedTarget (fun target ->
                    DotNetInterop.resolveMemberRead where (Option.defaultValue clrTarget target) propName false)

            let read =
                marshalResult
                    { Type = propType
                      Range = r
                      Node = TDotPropertyGet(typedTarget, propName, propType) }

            read.Type, read
        | _ ->
            failwithf
                $"Type Error at %s{where}: '%s{name}' reads a property, so it takes exactly one argument — the object to read it from — but was given %d{args.Length}."

    // `(.name target)` — a record or struct field, or an instance method call.
    | EApp(EIdent(name, _), args, r) when name.StartsWith "." && name.Length > 1 ->
        inferDotMethod env name args r


    | EApp(EIdent(name, _), args, r) when
        name.EndsWith "."
        && name.Length > 1
        && Map.containsKey (name.Substring(0, name.Length - 1)) env.Registry.ClrClasses
        ->
        inferClassConstruct env name args r


    | EApp(EIdent(name, _), args, r) when
        Map.containsKey name env.Registry.ClrExterns && not (Map.containsKey name env.Bindings)
        ->
        inferExternCall env name args r


    | EApp(EIdent("apply", _), args, r) when not (Map.containsKey "apply" env.Bindings) ->
        inferApply env args r


    | EApp(target, args, r) ->
        inferGeneralApp env target args r


    // Deliberately not generalized — see `ELetMono`. The value is inferred
    // first, exactly as `let` does, so the binding keeps its concrete head; only
    // the quantification is dropped.
    | ELetMono(name, value, body, r) ->
        inferLetMono infer env name value body r

    | ELet(name, isFun, args, typeAnn, value, body, r) ->
        inferLet infer env name isFun args typeAnn value body r


    | ELetRec(bindings, body, r) ->
        inferLetRec infer env bindings body r


    | ELetMutable(name, typeAnn, value, body, r) ->
        inferLetMutable infer env name typeAnn value body r


    | ESet(name, value, r) ->
        let binding = lookup env name

        if not binding.IsMutable then
            match Map.tryFind name env.Registry.ImportAliases with
            | Some alias when alias.IsMutable ->
                failwithf
                    $"Type Error: Cannot mutate '%s{name}' at %s{Lexer.formatPos r}. It is a def/mutable of the module '%s{Naming.moduleNameOfPath alias.OriginModule}', and only that module may write it. Give that module a function that sets it, and export the function."
            | _ ->
                // A binding that is not a `def/mutable` cannot change, so
                // `(set! name value)` writes what it holds, through
                // `Derefable!`: `(set! hits 2)` on a Box. Only a type with an
                // impl, or a type parameter, which a `(where (Derefable! %a))`
                // can stand for, goes to the trait. Anything else keeps the
                // plain refusal, since a variable that was meant to be a
                // `def/mutable` is the likelier mistake: that includes a type
                // not known yet, such as an unsettled number literal's.
                let targetType, _, _ = instantiate env.Registry binding.Scheme

                let writable =
                    match prune env.Registry targetType with
                    | TCon(ctor, _) ->
                        hasImplAtHead env.Registry "Derefable!" ctor
                        || Map.containsKey "Derefable!" env.Registry.BlanketImpls
                    | TVar _ -> true
                    | _ -> false

                if not writable then
                    failwithf
                        $"Type Error: Cannot mutate immutable variable '%s{name}' at %s{Lexer.formatPos r}. set! with a name and a value writes a def/mutable, or what a Box holds. A Box whose type is not known here needs an annotation."

                infer env (EApp(EResolved("deref-set!", r), [ EIdent(name, r); value ], r))
        else

        let valType, typedVal = infer env value
        let targetType, _, _ = instantiate env.Registry binding.Scheme
        unify env.Registry valType targetType

        TypeConstants.unitType,
        { Type = TypeConstants.unitType
          Range = r
          Node = TSet(name, typedVal) }

    | EIf(cond, trueBranch, falseBranch, r) ->
        inferIf infer env cond trueBranch falseBranch r

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
        inferLetTuple infer env names value body r

    | EList(exprs, r) -> inferCollection env "List" "list" TListMake exprs r

    | EVec(exprs, r) -> inferCollection env "Vec" "vec" TVecMake exprs r

    | EArray(exprs, r) -> inferCollection env "Array" "array" TArrayMake exprs r

    // Only a literal's own elements are splice positions, and both paths that
    // build one take the splices out before any element is inferred. So an
    // `ESplice` arriving here is a `,@` the parser accepted in a place that
    // turned out not to be a run of elements after all.
    | ESplice(_, r) ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: ,@ puts the elements of a collection into the literal around it, and there is no literal around this one."

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

    // A hoisted form has the type of what it hoists. Whether it may be hoisted
    // is asked later, by `HoistCheck`, of the checked body.
    | EHoist(body, r) ->
        let bodyType, tBody = infer env body

        bodyType,
        { Type = bodyType
          Range = r
          Node = THoist tBody }

    | ESeq(body, r) ->
        inferSeq env body r


    | EBjo(call, kind, r) ->
        inferBjo env call kind r


    | ETaskEvent(call, r) ->
        inferTaskEvent env call r


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
        inferMatch infer env target clauses r


    | EGetField(targetExpr, field, r) ->
        let targetType, typedTarget = infer env targetExpr
        inferFieldRead env targetType typedTarget field r

    | ERecordUpdate(targetName, fields, r) ->
        inferRecordUpdate env targetName fields r


    | ERecordSet(targetName, fields, r) ->
        inferRecordSet env targetName fields r


    | ECast(targetTypeAnnotation, expr, r) ->
        let targetType = resolveTypeAnnotation env.Registry targetTypeAnnotation
        let exprType, typedExpr = infer env expr

        // A newtype is its constructor's to make and a pattern's to take
        // apart. A cast would be a third way, past both and past `#:opaque`,
        // and at run time it would change nothing anyway.
        for t in [ targetType; exprType ] do
            match prune env.Registry t with
            | TCon(name, _) when Map.containsKey name env.Registry.Newtypes ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: a cast cannot make or take apart the newtype %s{Naming.showTypeName name}. Write (%s{Naming.bareTypeName env.CurrentModule name} x) to make one, and take one apart with a pattern."
            | _ -> ()

        targetType,
        { Type = targetType
          Range = r
          Node = TCast(typedExpr, targetType) }

    | EDynPack(writtenTrait, valueExpr, r) ->
        inferDynPack env writtenTrait valueExpr r


// `(ret e)` — an application of an escape some enclosing `with-return` put
// in scope. Taken before the general application case, and before the
// general `EIdent` below it, because an escape is not a value and there is
// nothing in `Bindings` for either of them to find.
//
// Where it may *stand* was settled syntactically by `checkEscapeUses` when
// the block was entered, so nothing here has to ask.
/// The two conversions a collection is spliced through: into a `Seq` and back.
///
/// There is no `seq->array`, and an array's length is fixed when it is
/// allocated, so there is nothing to convert back to. `Parser.collectItems`
/// refuses `,@` inside `#[...]` for that reason, which is why this is an
/// internal failure and not a diagnostic: reaching it means the parser let one
/// through.
and private spliceConversions (ctor: string) : string * string =
    match ctor with
    | "List" -> "list->seq", "seq->list"
    | "Vec" -> "vec->seq", "seq->vec"
    | other -> failwithf "internal: %s literals have no splice lowering" other

/// A spliced literal, assembled from the pieces `splicePieces` grouped it into.
///
/// Every piece is already typed as a whole `(ctor elemTy)` — a run of ordinary
/// elements as the literal node it has always been, a splice as whatever it
/// evaluates to — so all of them are joined the same way:
///
///   '(a ,@xs b)  ⇒  (seq->list (seq-append (list->seq (list a))
///                                          (seq-append (list->seq xs)
///                                                      (list->seq (list b)))))
///
/// No new typed node, no `Codegen` case and no runtime addition: `seq-append`,
/// `list->seq` and `seq->list` already have exactly these types. Nor is there
/// an evaluation-order question to answer, because there is nothing here but
/// call operands: C# evaluates those left to right and once each, so the
/// elements and the spliced expressions run in written order however the
/// appends are nested. `seq-append` is lazy, and that never shows — everything
/// handed to it is a collection that has already been built.
and private assembleSplicedLiteral
    (ctor: string)
    (elemTy: HMType)
    (pieces: TypedExpr list)
    (r: Range)
    : HMType * TypedExpr =
    let collTy = TCon(ctor, [ elemTy ])

    match pieces with
    // `'(,@xs)` is `xs`. Lists and vecs are immutable, so handing the value
    // straight back is indistinguishable from copying it, and the round trip
    // would only cost a walk.
    | [ single ] -> collTy, { single with Range = r }
    | _ ->
        let toSeq, fromSeq = spliceConversions ctor
        let seqTy = TCon("Seq", [ elemTy ])

        let call1 (fn: string) (argTy: HMType) (retTy: HMType) (arg: TypedExpr) : TypedExpr =
            { Type = retTy
              Range = arg.Range
              Node =
                TApply(
                    { Type = tfun [ argTy ] retTy
                      Range = arg.Range
                      Node = TIdent(fn, []) },
                    [ arg ],
                    []
                ) }

        // Right-nested, so that each append has a one-piece left operand and
        // the whole thing is linear in the number of elements rather than
        // quadratic in the number of pieces.
        let appended =
            pieces
            |> List.map (call1 toSeq collTy seqTy)
            |> List.reduceBack (fun left right ->
                { Type = seqTy
                  Range = r
                  Node =
                    TApply(
                        { Type = tfun [ seqTy; seqTy ] seqTy
                          Range = r
                          Node = TIdent("seq-append", []) },
                        [ left; right ],
                        []
                    ) })

        collTy, { call1 fromSeq seqTy collTy appended with Range = r }

/// The expression of a `,@`, checked against the literal it is written in.
///
/// A splice contributes a whole collection where its siblings contribute one
/// element each, so what it splices has to be the enclosing literal's own type
/// — `(List T)` in a list, `(Vec T)` in a vec. The mismatch is reported at the
/// `,@` rather than at the literal, because the literal is not what is wrong.
and private checkSplice
    (env: Env)
    (literalName: string)
    (collTy: HMType)
    (inner: Expr)
    (sr: Range)
    : TypedExpr =
    let innerType, typedInner = inferChecked collTy env inner

    try
        unify env.Registry innerType collTy
    with ex when Diagnostics.isDiagnostic ex ->
        let shown =
            DotNetInterop.showTypesTogether [ prune env.Registry collTy; prune env.Registry innerType ]

        failwithf
            $"Type Error at %s{Lexer.formatPos sr}: ,@ puts the elements of a collection into the %s{literalName} literal around it, so it has to be handed that same kind of collection:\n  expected %s{shown[0]}\n  got      %s{shown[1]}\nWrite ,x instead to place a single element."

    typedInner

/// A collection literal: `(list ...)`, `[...]` or `#[...]`.
///
/// One element type, joined across every element so that the diagnostic names
/// the literal rather than the pair of elements that disagreed. `ctor` names
/// the type, `literalName` is how that diagnostic spells the form, and
/// `mkNode` builds the node — which is all the three literals differ by.
///
/// This is the path a literal takes when nothing pushed a type into it. A
/// splice fixes the element type here exactly as an ordinary element does:
/// its own type is `(ctor elem)`, and unifying that against the literal's is
/// the same one question asked of a whole collection instead of one value.
and private inferCollection
    (env: Env)
    (ctor: string)
    (literalName: string)
    (mkNode: TypedExpr list -> TExprNode)
    (exprs: Expr list)
    (r: Range)
    : HMType * TypedExpr =
    let elementType = freshMeta ()
    let collectionType = TCon(ctor, [ elementType ])

    let run (elements: Expr list) : TypedExpr =
        let typedExprs =
            elements
            |> List.map (fun e ->
                let t, te = infer env e
                joinLiteralElement env r literalName exprs elementType t
                te)

        { Type = collectionType
          Range = r
          Node = mkNode typedExprs }

    if not (hasSplice exprs) then
        collectionType, run exprs
    else
        splicePieces exprs
        |> List.map (function
            | Choice1Of2 elements -> run elements
            | Choice2Of2(inner, sr) -> checkSplice env literalName collectionType inner sr)
        |> fun pieces -> assembleSplicedLiteral ctor elementType pieces r

and private inferEscapeCall (env: Env) (name: string) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let info = env.Escapes[name]

    let typedValue =
        match args with
        | [] ->
            // `(ret)` says the block produces nothing, which only a
            // void-typed block can agree with.
            unify env.Registry info.Result TypeConstants.unitType
            None
        | [ value ] ->
            let valueType, typedValue = infer env value
            unify env.Registry valueType info.Result
            Some typedValue
        | _ ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: `%s{name}` leaves its block with one value or with none, and here it was given %d{List.length args}."

    // Never yields a value, so it is given a fresh metavariable: it stands
    // wherever a value of any type was wanted and constrains nothing there.
    let resultType = freshMeta ()

    resultType,
    { Type = resultType
      Range = r
      Node = TReturn(info.Label, typedValue) }

and private inferDefMatch
    (tail: Env -> Expr -> HMType * TypedExpr)
    (env: Env)
    (binder: Pattern)
    (scrutinee: Expr)
    (failure: DefFailure)
    (sequel: Expr)
    (r: Range)
    : HMType * TypedExpr =
    // One scrutinee, matched by the binder and by every arm, so it is inferred
    // once and every pattern is checked against the type it has here.
    let scrutineeType, typedScrutinee = infer env scrutinee
    let typedBinder, boundVars = checkPattern inferChecked env scrutineeType binder

    let withVars vars inner =
        Map.fold
            (fun acc n t ->
                addBinding
                    n
                    { Scheme = Scheme([], [], t)
                      IsMutable = false }
                    acc)
            inner
            vars

    // The sequel is the rest of the body, so the form's own type is the
    // sequel's — and so is the failure part's. Whichever runs produces the
    // whole form's value, exactly as an `if`'s two branches do. A body that
    // means to leave an enclosing block says so with a `(ret ...)`, which is an
    // ordinary tail-position form here.
    let sequelType, typedSequel = tail (withVars boundVars env) sequel

    // `:propagate` rebuilds every case its pattern leaves out, each written as
    // the arm that rebuilds it and checked as any other arm is. The case name
    // rides along for the report: a propagation that does not fit the body is a
    // mistake about this form rather than about the arm's type.
    let failureArms =
        match failure with
        | FailNone -> []
        | FailLeave arms -> arms |> List.map (fun (pat, body) -> None, pat, body)
        | FailLeaveWith value
        | FailDefault value -> [ None, PWildcard(exprRange value), value ]
        | FailPropagate ->
            propagatedArms env scrutineeType typedBinder r
            |> List.map (fun (caseName, (pat, body)) -> Some caseName, pat, body)

    // The failure part is checked in `env` and not under what the binder binds:
    // it runs because the binder did not match, so nothing the binder would
    // have bound exists. What an arm's own pattern binds is in scope in that
    // arm and nowhere else.
    let typedArms =
        failureArms
        |> List.map (fun (propagated, armPattern, armBody) ->
            let typedPattern, armVars = checkPattern inferChecked env scrutineeType armPattern
            let armType, typedBody = tail (withVars armVars env) armBody

            try
                unify env.Registry armType sequelType
            with ex when Diagnostics.isDiagnostic ex ->
                let shown =
                    DotNetInterop.showTypesTogether [ prune env.Registry armType; prune env.Registry sequelType ]

                match propagated, failure, sequel with
                | Some caseName, _, _ ->
                    failwithf
                        $"Type Error at %s{Lexer.formatPos r}: `:propagate` would return %s{Naming.showTypeName caseName} here, but this body has type %s{shown[1]}."
                | None, FailDefault _, EIdent(name, _) ->
                    failwithf
                        $"Type Error at %s{Lexer.formatPos typedBody.Range}: `:default` gives %s{name} its value, so it has %s{name}'s type:\n  the default: %s{shown[0]}\n  %s{name}: %s{shown[1]}"
                | None, _, _ ->
                    // The `void` sequel is the mistake this form invites, and
                    // it is worth naming: it is what a body written for its
                    // effects leaves behind, and the failure that "returned a
                    // value" from it was relying on an escape this form does
                    // not perform.
                    let hint =
                        if shown[1] = "void" then
                            "\nThe rest of the body produces nothing, so the failure may not produce anything either. If it was meant to leave the enclosing block, say so: `(ret ...)` naming an enclosing `with-return`, or `(panic! ...)`."
                        else
                            "\nA failure produces the form's value, as an `if`'s else does — it does not leave the enclosing block by itself. To leave one, write it: `(ret ...)` naming an enclosing `with-return`, or `(panic! ...)`."

                    failwithf
                        $"Type Error at %s{Lexer.formatPos typedBody.Range}: what a `def` produces when its pattern does not match is the value of the whole form, and here it disagrees with the rest of the body:\n  the failure:          %s{shown[0]}\n  the rest of the body: %s{shown[1]}%s{hint}"

            ({ Pattern = typedPattern; Body = typedBody }: TDefMatchArm))

    sequelType,
    { Type = sequelType
      Range = r
      Node = TDefMatch(typedBinder, typedScrutinee, typedSequel, typedArms) }

/// The arms `:propagate` stands for: every case the pattern leaves out, matched
/// and rebuilt, one arm each.
///
/// Each case is rebuilt rather than the scrutinee handed back, which is what
/// lets the type arguments change: `None` is constructed fresh at the body's
/// type, and an `Err`'s payload is carried across into a new one. Whether the
/// body admits that is decided by unifying the arm with the sequel, as for any
/// other arm — so a type parameter the leftover case mentions is forced to be
/// the same in scrutinee and body, and one it does not mention is free.
and private propagatedArms
    (env: Env)
    (scrutineeType: HMType)
    (binder: TypedPattern)
    (r: Range)
    : (string * (Pattern * Expr)) list =
    let settled =
        try
            prune env.Registry scrutineeType
        with _ ->
            scrutineeType

    let shown = DotNetInterop.showType settled

    let rebuild (caseName: string, arity: int) =
        if not (Map.containsKey caseName env.Bindings) then
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: `:propagate` rebuilds the cases this pattern leaves out, and %s{Naming.showTypeName caseName} of %s{shown} is not in scope here. Import it, or use :leave-with or :leave."

        let carried = List.init arity (fun _ -> Gensym.fresh "carried")

        let rebuilt =
            match carried with
            | [] -> EIdent(caseName, r)
            | _ -> EApp(EIdent(caseName, r), carried |> List.map (fun n -> EIdent(n, r)), r)

        (caseName, (PConstruct(caseName, carried |> List.map (fun n -> PIdent(n, r)), r), rebuilt))

    match uncoveredCases env.Registry settled binder with
    | Some [] ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: this pattern always matches, so the failure part of this def can never run. `:propagate` has no case to rebuild — drop it."
    | Some cases -> cases |> List.map rebuild
    | None ->
        failwithf
            $"Type Error at %s{Lexer.formatPos r}: `:propagate` rebuilds whole cases of the scrutinee's type, and what this pattern leaves of %s{shown} is not a set of cases. Use :leave-with or :leave."

// `Class.Member` — a static field or property. This is how an enum value
// such as `FileMode.Open` is written, and it is why `import/class` is
// useful for a type that has no constructor at all.
and private inferStaticMember (env: Env) (name: string) (r: Range) : HMType * TypedExpr =
    let split = name.LastIndexOf "."
    let alias = name.Substring(0, split)
    let memberName = name.Substring(split + 1)
    let info = env.Registry.ClrClasses[alias]
    let where = Lexer.formatPos r
    let clrType = DotNetInterop.resolveType $" at %s{where}" info.ClrName
    let memberType = DotNetInterop.resolveMemberRead where clrType memberName true

    let read =
        marshalResult
            { Type = memberType
              Range = r
              Node = TForeignStaticGet(info.ClrName, memberName, memberType) }

    read.Type, read

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
and private inferExternValue (env: Env) (name: string) (r: Range) : HMType * TypedExpr =
    let info = env.Registry.ClrExterns[name]
    let where, clrType, receiverType = externTarget info r

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

        let read =
            marshalExternResult info
                { Type = memberType
                  Range = r
                  Node = TForeignStaticGet(info.ClrType, info.MemberName, memberType) }

        read.Type, read

    | ExternGet ->
        let memberType = DotNetInterop.resolveMemberRead where clrType info.MemberName false
        let recv = Gensym.fresh "__foreign"

        let body: TypedExpr =
            marshalExternResult info
                { Type = memberType
                  Range = r
                  Node = TDotPropertyGet(identOf recv receiverType, info.MemberName, memberType) }

        let funType = tfun [ receiverType ] body.Type

        funType,
        { Type = funType
          Range = r
          Node = TLambda([ recv ], body) }

    | ExternSet ->
        let clrMemberType = DotNetInterop.resolveMemberWrite where clrType info.MemberName (not info.IsInstance)
        let value = Gensym.fresh "__foreign"

        // A .NET string member is set from a Bjolang string.
        let memberType, valueExpr =
            match bjolangViewOf clrMemberType with
            | Some seen -> seen, toClrString (identOf value seen) clrMemberType
            | None -> clrMemberType, identOf value clrMemberType

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
            let paramTypes, retType, typeArgs, outs = instantiateGenericExtern env.Registry where info

            let methodParams =
                if info.IsInstance then List.tail paramTypes else paramTypes

            let argNames = paramTypes |> List.map (fun _ -> Gensym.fresh "__foreign")
            let argExprs: TypedExpr list = List.map2 identOf argNames paramTypes

            let meta = Some(genericExternMeta info typeArgs outs methodParams retType)

            let node =
                if info.IsInstance then
                    foreignCallNode info.ClrType info.MemberName (Some(List.head argExprs)) (convertStringIns outs (List.tail argExprs)) meta
                else
                    foreignCallNode info.ClrType info.MemberName None (convertStringIns outs argExprs) meta

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
            declaredReceiver |> Option.iter (fun t -> unify env.Registry t (seenType receiverType))

            let retType = wrapForeignExceptions info.Exceptions (externResultType info resolved.ReturnType)
            let argNames = resolved.ParameterTypes |> List.map (fun _ -> Gensym.fresh "__foreign")

            // The lambda takes Bjolang strings where the method takes .NET
            // ones, and converts them in the call.
            let passed (n: string) (t: HMType) : TypedExpr =
                match bjolangViewOf t with
                | Some seen -> toClrString (identOf n seen) t
                | None -> identOf n t

            let argExprs = List.map2 passed argNames resolved.ParameterTypes
            let seenParams = resolved.ParameterTypes |> List.map seenType

            let paramNames, paramTypes, node =
                if info.IsInstance then
                    let recv = Gensym.fresh "__foreign"

                    recv :: argNames,
                    seenType receiverType :: seenParams,
                    TDotMethodCall(
                        passed recv receiverType,
                        info.MemberName,
                        argExprs,
                        Some(metadataOf resolved info.Exceptions)
                    )
                else
                    argNames,
                    seenParams,
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

// A hole is whatever the code around it makes it. See `Holes`.
and private inferHole (env: Env) (name: string) (r: Range) : HMType * TypedExpr =
    let t = freshMeta ()

    Holes.pending.Add
        { Written = name
          Type = t
          Range = r
          Env = env }

    t,
    { Type = t
      Range = r
      Node = TIdent(name, []) }

// A reference to a case that several unions have, whose union is not known
// here. See `TypeEnv.openCase`.
and private inferOpenCase (env: Env) (name: string) (candidates: string list) (r: Range) : HMType * TypedExpr =
    let t = freshMeta ()
    let placeholder = openCase env name candidates t r false

    t,
    { Type = t
      Range = r
      Node = TIdent(placeholder, []) }

// The case `name` means where `expected` is the type wanted, when several
// unions have a case of that name and `expected` is one of them.
and private caseChosenBy (env: Env) (expected: HMType) (name: string) : string option =
    match caseCandidates env name with
    | [] -> None
    | candidates -> unionHeadOf env.Registry expected |> Option.bind (candidateOf candidates)

and private inferIdent (env: Env) (name: string) (r: Range) : HMType * TypedExpr =
    // A case is referred to by name whether it is built bare or applied, and
    // an application infers its head here, so this covers both.
    match Map.tryFind name env.Registry.OpaqueCases with
    | Some typeKey when not (representationVisible env.Registry (codeModule env r) typeKey) ->
        // A newtype's one constructor is the type itself.
        let what =
            if name = typeKey then $"the newtype %s{Naming.showTypeName typeKey}"
            else $"'%s{Naming.showTypeName name}' is a case of %s{Naming.showTypeName typeKey}, which"

        failwithf
            $"Type Error at %s{Lexer.formatPos r}: %s{what} cannot be built here. %s{opaqueTypeNote env.Registry typeKey}"
    | _ -> ()

    let binding = lookup env name
    warnIfDeprecated env name r
    let t, tArgs, constraints = instantiate env.Registry binding.Scheme

    for c in constraints do
        Traits.queueClrObligation env c.TraitName c.TargetType r $"to call '%s{name}'"
        Traits.queueCallObligation env c.TraitName c.TargetType r $"to call '%s{name}'"

    Traits.queuePins name constraints r

    // A name with two emitted copies is colour-polymorphic, and its
    // *reference* has to say so, not just its call.
    //
    // The binding is the ordinary copy's, so its arrow is `ESync`, and
    // handing that to a `-?->` parameter bound the parameter's cell to
    // `ESync` before anything had decided anything: `(port->list read-line
    // p)` from a bjoroutine chose the ordinary reader and parked on every
    // line, silently, while `(port->list (bjoroutine (q) (read-line q)) p)`
    // suspended. Same call, and the difference was that one of them
    // mentioned a colour — which is the thing this design exists to avoid.
    //
    // So the reference gets a cell of its own instead. Meeting a parameter
    // declared `->` binds it to `ESync` exactly as before; meeting a `-?->`
    // chains the two and leaves both open, and `EffectGraph` grounds the
    // chain to the colour of the member the reference is written in.
    let t =
        match t with
        | TFun(args, ret, ESync) when Map.containsKey name env.Registry.DoubleDefs ->
            TFun(args, ret, freshEffect ())
        | other -> other

    t,
    { Type = t
      Range = r
      Node = TIdent(name, tArgs) }

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
and private inferTraitMethodCall (env: Env) (methodKey: string) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let traitName, methodName = (traitMethodOf env.Registry methodKey).Value
    warnIfDeprecated env methodKey r

    // Every argument is positional, keywords included. A trait method's
    // shape is fixed by its trait and no trait declares a keyword
    // parameter, so `#:foo` here can only be the keyword *value* — which
    // `(= k #:foo)` is, now that `Keyword` has an `Eq` implementation. A
    // call written as though it took keyword arguments fails on arity
    // instead, which is what it is.
    let typedArgs = args |> List.map (infer env)

    let methodType, tref = traitCallType env traitName methodName r
    let retType = freshMeta ()

    // The effect is the method's own, copied rather than chosen — the same
    // move the ordinary application makes with `demandedEffect`. Building
    // this arrow with `tfun` spelled it `->` unconditionally, so a trait
    // that declared `-bjo->` met its own call site and was told an ordinary
    // function cannot be used where a bjoroutine is expected.
    unify
        env.Registry
        methodType
        (TFun(typedArgs |> List.map fst, retType, demandedEffect env methodType))

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
and private inferRecordConstruct (env: Env) (recordTypeName: string) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let writtenFields =
        args
        |> List.map (fun arg ->
            match arg with
            | EApp(EIdent(fieldName, _), [ value ], _) -> fieldName, value
            // A label a macro's template wrote arrives resolved, as every name
            // a template does not bind does (see `Macro`). It is a spelling all
            // the same.
            | EApp(EResolved(fieldName, _), [ value ], _) -> fieldName, value
            // A label that is also a macro or a special form: the field is
            // the reading for a record. See `Parser.processConstructorArgs`.
            | EApp(EResolved(marker, _), [ EQuotedSymbol(fieldName, _); value; _ ], _) when marker = fieldOrCallMarker ->
                fieldName, value
            | bad ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos (exprRange bad)}: '%s{recordTypeName}' is a record type, so each argument is one of its fields, written (field-name value).")

    let instantiatedRecordType, expectedFields, expectedFieldsInstantiated =
        instantiateRecord env.Registry recordTypeName

    // A field label is written like a call, so a renaming pass, which cannot
    // tell one from a call, renames a label together with a local of the same
    // name: `(Frame (name name))` in a body freshened for inlining gives
    // `(name__7 name__7)`, and a second renaming `name__7__9`. A label is a
    // spelling and never a variable, so a label that names no field means the
    // field its base name names.
    //
    // A body spliced from another module also has its free names pointed at
    // their qualified references (`AlphaRename.reachHidden`), and a label that
    // spells a binding of that module goes with them: `(Dep (version v))`
    // in a module that binds a `version` gives `Module::version`. The
    // qualifier comes off too.
    //
    // And a published body writes its types and constructors as their keys
    // (`AlphaRename.keyTypeSpellings`), which reaches a label that spells a
    // constructor as well. The key's module comes off too.
    let rec original (name: string) =
        let unkeyed =
            match (if name.Contains "." then Naming.typeKeyParts name else None) with
            | Some(_, bare) -> bare
            | None -> name

        let stripped = Gensym.baseName (Naming.writtenName unkeyed)
        if stripped = name then name else original stripped

    let writtenFields =
        writtenFields
        |> List.map (fun (name, value) ->
            if Map.containsKey name expectedFieldsInstantiated then
                name, value
            else
                let base' = original name

                if Map.containsKey base' expectedFieldsInstantiated then
                    base', value
                else
                    name, value)

    let fieldList = expectedFields |> List.map fst |> String.concat ", "

    let provided =
        (Map.empty, writtenFields)
        ||> List.fold (fun acc (name, expr) ->
            if Map.containsKey name acc then
                failwithf
                    $"Type Error at %s{Lexer.formatPos r}: field '%s{name}' of '%s{recordTypeName}' is given twice."

            let exprType, typedExpr = infer env expr

            match Map.tryFind name expectedFieldsInstantiated with
            | Some expectedType ->
                try
                    unifyExpecting
                        env.Registry
                        expectedType
                        exprType
                        $"the field '%s{name}' of '%s{Naming.showTypeName recordTypeName}'"
                with ex when Diagnostics.needsLocation ex ->
                    raise (Diagnostics.withLocation (exprRange expr) ex)

                Map.add name (bridgeToSuspending env expectedType typedExpr) acc
            | None ->
                failwithf
                    $"Type Error at %s{Lexer.formatPos (exprRange expr)}: '%s{recordTypeName}' has no field '%s{name}'. Its fields are: %s{fieldList}.")

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

// A field read from a target that has already been inferred. Shared by
// `(record-ref target x)` and `(.x target)`, so that the two are the same
// typing, the same errors and the same node.
and private inferFieldRead
    (env: Env)
    (targetType: HMType)
    (typedTarget: TypedExpr)
    (field: string)
    (r: Range)
    : HMType * TypedExpr =
    let recordTypeName = recordTypeOfField env.Registry (codeModule env r) "read" targetType field r

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

// Whether `(.x target)` reads the field `x`, rather than calling a .NET
// method, going by the target's type as inference has it now.
//
// A record or struct always reads a field, even one it does not have, so a
// missing field is reported the way `record-ref` reports it. The C# record
// behind it has .NET methods, but `DotNetInterop` cannot resolve a Bjolang
// record type, so `(.ToString r)` has never reached them.
//
// A type that is not known yet uses `record-ref`'s fallback when some record
// declares a field `x`. When none does it stays a .NET call, and gets the
// error an unknown receiver always gets.
and private dotReadsField (env: Env) (field: string) (targetType: HMType) : bool =
    match prune env.Registry targetType with
    | TCon(name, _) -> Map.containsKey name env.Registry.Records
    | TMeta _ as t -> not (isOpenLiteral t) && Map.containsKey field env.Registry.RecordFields
    | _ -> false

// `.x` or `.-x` written as a value, and not bound by anything.
and private isDotName (env: Env) (name: string) : bool =
    name.StartsWith "." && name.Length > 1 && not (Map.containsKey name env.Bindings)

// A bare `.x` whose `x` is a record field, rewritten to the lambda
// `(fun (r) (record-ref r x))`. Anything else comes back unchanged.
//
// Called before an argument is inferred as well as from `inferDotValue`, so
// that `(vec-map .url v)` is a lambda argument to `inferGeneralApp`: that is
// what makes it wait for `v` to say which record `url` is read from, as a
// written `fun` does.
and private fieldAccessorLambda (env: Env) (expr: Expr) : Expr =
    match expr with
    | EIdent(name, r) when
        isDotName env name
        && not (name.StartsWith ".-")
        && Map.containsKey (name.Substring 1) env.Registry.RecordFields
        ->
        let p = Gensym.fresh "record"
        EFun([ p ], EGetField(EIdent(p, r), name.Substring 1, r), Ordinary, r)
    | _ -> expr

// A bare `.x` or `.-x` in value position.
//
// A .NET member cannot be one: it is resolved against the receiver's type,
// and there is no receiver until it is called.
and private inferDotValue (env: Env) (name: string) (r: Range) : HMType * TypedExpr =
    let where = Lexer.formatPos r

    match fieldAccessorLambda env (EIdent(name, r)) with
    | EFun _ as lambda -> infer env lambda
    | _ when name.StartsWith ".-" && name.Length > 2 ->
        let propName = name.Substring 2

        if Map.containsKey propName env.Registry.RecordFields then
            failwithf
                $"Type Error at %s{where}: '%s{name}' reads a .NET property, and a property read is not a value. '%s{propName}' is a record field, so write .%s{propName} instead."
        else
            failwithf
                $"Type Error at %s{where}: '%s{name}' reads a .NET property, and a property read is not a value. Write (fun (x) (%s{name} x)) instead."
    | _ ->
        failwithf
            $"Type Error at %s{where}: '%s{name}' names a .NET method, and a method group is not a value. Write (fun (x) (%s{name} x)) instead."

// `(.name target)` — a record or struct field, or else an instance method
// call.
and private inferDotMethod (env: Env) (name: string) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let methodName = name.Substring 1
    let where = Lexer.formatPos r

    match args with
    | [] ->
        failwithf
            $"Type Error at %s{where}: '%s{name}' calls an instance method, so its first argument is the object to call it on, but it was given none."
    | target :: rest ->
        let targetType, typedTarget = infer env target

        if dotReadsField env methodName targetType then
            if not rest.IsEmpty then
                failwithf
                    $"Type Error at %s{where}: '%s{name}' reads a field, so it takes exactly one argument — the record or struct to read it from — but was given %d{args.Length}. To call a function stored in the field, write ((%s{name} target) args...)."

            inferFieldRead env targetType typedTarget methodName r
        else

        let clrTarget = receiverClrType where name targetType

        let typedArgs = rest |> List.map (infer env)
        let argTypes = typedArgs |> List.map fst

        settleLiterals argTypes

        let resolved, typedTarget =
            onStringOrClrString env.Registry targetType typedTarget (fun target ->
                DotNetInterop.resolveMethod where false (Option.defaultValue clrTarget target) methodName argTypes)

        let coercedArgs =
            reconcileForeignArgs env.Registry (typedArgs |> List.map snd) resolved.ParameterTypes

        // Never exception-wrapped: `import/class` declares one signature —
        // the constructor's — so there is nowhere to say what a method may
        // raise, and wrapping it anyway would swallow exceptions nobody
        // listed.
        let retType = seenType resolved.ReturnType

        retType,
        { Type = retType
          Range = r
          Node = TDotMethodCall(typedTarget, methodName, coercedArgs, Some(metadataOf resolved [])) }

// `(ClassName. args...)` — construction.
and private inferClassConstruct (env: Env) (name: string) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let alias = name.Substring(0, name.Length - 1)
    let info = env.Registry.ClrClasses[alias]
    let where = Lexer.formatPos r

    // A generic class is known to .NET by its arity, `Dictionary`2`, and an
    // import of one names it as a type only: the construction would have
    // nowhere to take its type arguments from.
    match DotNetInterop.tryResolveType info.ClrName with
    | None ->
        match [ 1..8 ] |> List.tryFind (fun n -> (DotNetInterop.tryResolveType $"%s{info.ClrName}`%d{n}").IsSome) with
        | Some arity ->
            failwithf
                $"Type Error at %s{where}: '%s{alias}' is %s{info.ClrName}, a generic class taking %d{arity} type argument(s), and (%s{alias}.) cannot construct one: an imported generic class is a type only. Make one through a static generic method, imported with a signature that says its type, as (set-empty (: Set.SetModule.Empty (-> (Set %%a)))) does."
        | None -> ()
    | Some _ -> ()

    let clrType = DotNetInterop.resolveType $" at %s{where}" info.ClrName

    let typedArgs = args |> List.map (infer env)
    let argTypes = typedArgs |> List.map fst

    match info.CtorType |> Option.map (prune env.Registry) with
    | Some(TFun(declared, _, _)) -> pinLiteralsToParameters env.Registry declared argTypes
    | _ -> ()

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
    | Some declared -> unify env.Registry declared (tfun (resolved.ParameterTypes |> List.map seenType) resolved.ReturnType)
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
and private inferExternCall (env: Env) (name: string) (args: Expr list) (r: Range) : HMType * TypedExpr =
    let info = env.Registry.ClrExterns[name]
    let where, clrType, receiverType = externTarget info r

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
        checkDeclaredExtern env.Registry info receiverType true [] memberType

        let read =
            marshalExternResult info
                { Type = memberType
                  Range = r
                  Node = TDotPropertyGet(receiver, info.MemberName, memberType) }

        read.Type, read

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

        checkDeclaredExtern env.Registry info receiverType receiver.IsSome [ memberType ] TypeConstants.voidType

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
        let paramTypes, retType, typeArgs, outs = instantiateGenericExtern env.Registry where info

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
            Some(genericExternMeta info typeArgs outs methodParams retType)

        resultType,
        { Type = resultType
          Range = r
          Node = foreignCallNode info.ClrType info.MemberName receiver (convertStringIns outs methodArgs) meta }

    | ExternMethod ->
        let allTypedArgs = args |> List.map (infer env)

        let receiver, typedArgs =
            if info.IsInstance then
                let recv, rest = takeReceiver allTypedArgs
                Some recv, rest
            else
                None, allTypedArgs

        let argTypes = typedArgs |> List.map fst

        match info.DeclaredType |> Option.map (prune env.Registry) with
        | Some(TFun(declared, _, _)) ->
            let methodParams = if info.IsInstance && not declared.IsEmpty then List.tail declared else declared
            pinLiteralsToParameters env.Registry methodParams argTypes
        | _ -> ()

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

        checkDeclaredExtern env.Registry info receiverType receiver.IsSome visibleParams callResultType

        // A .NET string answer is converted by `Codegen`, which reads
        // `ReturnType` below; see `bjolangViewOf`.
        let retType = wrapForeignExceptions info.Exceptions (externResultType info callResultType)

        let meta =
            Some
                { metadataOf resolved info.Exceptions with
                    ParameterTypes = visibleParams
                    ReturnType = callResultType
                    Await = info.IsAsync
                    AmbientToken = threadsToken
                    Blocking = info.IsBlocking }

        retType,
        { Type = retType
          Range = r
          Node = foreignCallNode resolved.DeclaringType info.MemberName receiver coercedArgs meta }

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
and private inferApply (env: Env) (args: Expr list) (r: Range) : HMType * TypedExpr =
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
    // A spliced literal is not one: its length is not known here, so there is
    // no fixed set of elements to lay out into the rest array. It takes the
    // ordinary path below and is converted like any other collection.
    let literalItems =
        match collExpr with
        | EList(items, _) when not (hasSplice items) -> Some items
        | EVec(items, _) when not (hasSplice items) -> Some items
        | EArray(items, _) -> Some items
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

and private inferGeneralApp (env: Env) (target: Expr) (args: Expr list) (r: Range) : HMType * TypedExpr =
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

    /// The type the callee declares for keyword parameter `kwName`, for the
    /// same use as `expectedParam`. In the flat parameter list the keyword
    /// parameters follow the mandatory ones, in declaration order.
    let expectedKeyword (kwName: string) : HMType option =
        match funMeta with
        | None -> None
        | Some meta ->
            match meta.KeywordParams |> List.tryFindIndex (fun (k, _) -> k = kwName) with
            | None -> None
            | Some k ->
                match prune env.Registry targetType with
                | TFun(paramTys, _, _) when meta.MandatoryCount + k < paramTys.Length ->
                    match prune env.Registry paramTys[meta.MandatoryCount + k] with
                    | TMeta _ -> None
                    | paramTy -> Some paramTy
                | _ -> None

    // Separate keyword args from positional args
    // Keyword args appear as EKeyword("name") followed by a value expr when matching a declared keyword parameter
    let rec splitArgs positional keywords remaining =
        match remaining with
        | [] -> List.rev positional, List.rev keywords
        | EKeyword(kwName, _) :: value :: rest when isDeclaredKw kwName ->
            splitArgs positional ((kwName, value) :: keywords) rest
        | EKeyword(kwName, kr) :: [] when isDeclaredKw kwName ->
            failwithf $"Keyword argument '#:%s{kwName}' is missing a value at %s{Lexer.formatPos kr}"
        | arg :: rest -> splitArgs (arg :: positional) keywords rest

    let positionalExprs, keywordExprs = splitArgs [] [] args

    // A bare `.field` is a lambda, and is inferred with the lambdas below.
    let positionalExprs = positionalExprs |> List.map (fieldAccessorLambda env)
    let keywordExprs = keywordExprs |> List.map (fun (k, v) -> k, fieldAccessorLambda env v)

    // Positional arguments are inferred in two passes, lambdas last. A
    // lambda's parameter types come from what the callee declares for its
    // position, and `expectedParam` can only report that once the other
    // arguments have pinned it: in `(vec-for-each (fun (r) ...) kept)` it is
    // `kept` that says what `r` is, and the body cannot be inferred before
    // then.
    //
    // Results are written back into their source position, because codegen
    // emits the arguments in the order they are listed and they may have
    // side effects.
    let slots: (HMType * TypedExpr) option array = Array.create (List.length positionalExprs) None

    /// Pin an argument to the parameter it fills, ahead of the flat
    /// unification below, so that a later lambda argument can read its own
    /// parameter types off the callee's arrow.
    ///
    /// The parameter goes first, because `unifyEffect` reads its first
    /// argument as the expectation: the other way round an ordinary
    /// function passed to a `-bjo->` parameter is reported as its opposite
    /// and refused.
    ///
    /// A parameter still waiting on an implementor is skipped, the same
    /// exception `unify` makes for `TFun`: in `(fold + 0 v)` the folding
    /// function mentions `Foldable`'s associated type, which nothing knows
    /// until `v` has been inferred.
    ///
    /// `what` names the argument in a mismatch, which is reported at the
    /// argument rather than at the whole call.
    let pin (expected: HMType option) (argType: HMType) (what: string) (where: Range) =
        match expected with
        | Some paramTy when not (awaitsImplementor env.Registry paramTy || awaitsImplementor env.Registry argType) ->
            try
                unifyExpecting env.Registry paramTy argType what
            with ex when Diagnostics.needsLocation ex ->
                raise (Diagnostics.withLocation where ex)
        | _ -> ()

    let callee =
        match target with
        | EIdent(name, _) -> $"'%s{name}'"
        | _ -> "the function"

    /// An argument that is not a lambda, inferred against the type its
    /// parameter declares.
    let inferArg (arg: Expr) (expected: HMType option) =
        match arg, expected with
        // At a parameter whose type is a production of a grammar, the
        // argument is read as an element of a literal of it is: a literal
        // by the grammar, the results of an `if` or a `match` each so, and
        // any other value by its type. `(padded/left 5 name)` with `name` a
        // string is then a block, as `[name]` is a vec of one.
        | _, Some paramTy when (grammarOf env.Registry paramTy).IsSome ->
            let typed = matchGrammar env paramTy arg
            typed.Type, typed
        // A literal is checked against the type its position expects
        // rather than inferred on its own, so a union the parameter
        // names can elaborate it: `(f 'x)` is a `T` because the
        // signature says so, the same way `(f '(pipe …))` is a `Form`.
        | (EList _ | EVec _ | EArray _ | EString _ | EQuotedSymbol _ | EInt _), Some paramTy ->
            inferChecked paramTy env arg
        // So is a form that ends in one, `(f (if c '(a) '(b)))`. Not
        // at a parameter still waiting on an implementor, which
        // `pin` leaves alone too: a lambda in a branch would
        // be pinned to it.
        | (EIf _ | ELet _ | ELetMono _ | ELetRec _ | ELetMutable _ | ELetTuple _ | EMatch _ | EDefMatch _),
          Some paramTy when not (awaitsImplementor env.Registry paramTy) ->
            inferChecked paramTy env arg
        // A function with keyword parameters, passed where a function of
        // its mandatory ones is expected. See `keywordFreeReference`.
        | EIdent(name, r), Some paramTy ->
            match keywordFreeReference env paramTy name r with
            | Some typed -> typed
            | None -> infer env arg
        | _ -> infer env arg

    /// A lambda argument, inferred once the other arguments have pinned
    /// what its parameter says about it.
    let inferLambdaArg (arg: Expr) (expected: HMType option) =
        match expected with
        | Some paramTy -> inferChecked paramTy env arg
        | None -> infer env arg

    positionalExprs
    |> List.iteri (fun i arg ->
        match arg with
        | EFun _ -> ()
        | _ ->
            let argType, typedArg = inferArg arg (expectedParam i)
            pin (expectedParam i) argType $"the %s{ordinal (i + 1)} argument to %s{callee}" (Ast.exprRange arg)
            slots[i] <- Some(argType, typedArg))

    // Keyword arguments get the same treatment, so `(f #:mode 'fast)`
    // elaborates the way `(f 'fast)` does. They are kept in the order they
    // were written, which is the order codegen evaluates them in.
    let keywordSlots: (HMType * TypedExpr) option array = Array.create (List.length keywordExprs) None

    keywordExprs
    |> List.iteri (fun i (kwName, value) ->
        match value with
        | EFun _ -> ()
        | _ ->
            let argType, typedArg = inferArg value (expectedKeyword kwName)
            pin (expectedKeyword kwName) argType $"the #:%s{kwName} argument to %s{callee}" (Ast.exprRange value)
            keywordSlots[i] <- Some(argType, typedArg))

    positionalExprs
    |> List.iteri (fun i arg ->
        match arg with
        | EFun _ ->
            let lambdaType, typedLambda = inferLambdaArg arg (expectedParam i)
            pin (expectedParam i) lambdaType $"the %s{ordinal (i + 1)} argument to %s{callee}" (Ast.exprRange arg)
            slots[i] <- Some(lambdaType, typedLambda)
        | _ -> ())

    keywordExprs
    |> List.iteri (fun i (kwName, value) ->
        match value with
        | EFun _ -> keywordSlots[i] <- Some(inferLambdaArg value (expectedKeyword kwName))
        | _ -> ())

    let keywordArgs =
        List.zip (keywordExprs |> List.map fst) (keywordSlots |> Array.toList |> List.map Option.get)

    let positionalArgs = slots |> Array.toList |> List.map Option.get
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

        // A keyword argument has no parameter type in `Codegen` to be lifted
        // against, so an ordinary function given to a `-bjo->` keyword is
        // wrapped here. The callee's arrow has the keywords after the
        // mandatory parameters, in declaration order.
        let declaredParams =
            match prune env.Registry targetType with
            | TFun(ps, _, _) -> ps
            | _ -> []

        let typedKwArgs =
            keywordArgs
            |> List.map (fun (n, (_, te)) ->
                let declared =
                    meta.KeywordParams
                    |> List.tryFindIndex (fun (kwName, _) -> kwName = n)
                    |> Option.bind (fun k -> List.tryItem (meta.MandatoryCount + k) declaredParams)

                match declared with
                | Some wanted -> n, bridgeToSuspending env wanted te
                | None -> n, te)

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

        // Said in terms of the call rather than as two arrows printed one above
        // the other, which is what the unification below would report.
        match prune env.Registry targetType with
        | TFun(paramTys, _, _) when paramTys.Length <> positionalArgs.Length ->
            let count n = if n = 1 then "1 argument" else $"%d{n} arguments"

            failwithf
                $"Type Error at %s{Lexer.formatPos r}: %s{callee} takes %s{count paramTys.Length}, and is given %s{count positionalArgs.Length} here."
        | _ -> ()

        unify
            env.Registry
            targetType
            (TFun(positionalArgs |> List.map fst, retType, demandedEffect env targetType))

        retType,
        { Type = retType
          Range = r
          Node = TApply(typedTarget, positionalArgs |> List.map snd, []) }

// The forms below take `tail`, which is how their tail positions are inferred:
// `infer`, or `inferChecked expected` when the context expects a type. The
// expectation is what lets a literal in a branch or a body be elaborated.
and private inferIf (tail: Env -> Expr -> HMType * TypedExpr) (env: Env) (cond: Expr) (trueBranch: Expr) (falseBranch: Expr) (r: Range) : HMType * TypedExpr =
    let condType, tCond = infer env cond
    unify env.Registry condType TypeConstants.boolType
    let trueType, tTrue = tail env trueBranch
    let falseType, tFalse = tail env falseBranch

    // Located here because a tail position is checked through `inferChecked`,
    // which attaches no location of its own: without this, branches that
    // disagree are reported at whatever encloses the `if`.
    try
        // Not "then" and "else": `cond` and a loop's `:finish` are ifs too, and
        // neither says either word.
        unifyLabelled env.Registry "two branches here give values of different types." ("one", trueType) ("other", falseType)
    with ex when Diagnostics.needsLocation ex ->
        raise (Diagnostics.withLocation r ex)

    trueType,
    { Type = trueType
      Range = r
      Node = TIf(tCond, tTrue, tFalse) }

// Deliberately not generalized — see `ELetMono`. The value is inferred first,
// exactly as `let` does, so the binding keeps its concrete head; only the
// quantification is dropped.
and private inferLetMono (tail: Env -> Expr -> HMType * TypedExpr) (env: Env) (name: string) (value: Expr) (body: Expr) (r: Range) : HMType * TypedExpr =
    let valType, typedVal = infer env value

    let localEnv =
        addBinding
            name
            { Scheme = Scheme([], [], valType)
              IsMutable = false }
            env

    let bodyType, typedBody = tail localEnv body

    bodyType,
    { Type = bodyType
      Range = r
      Node = TLet(name, false, noParams, typedVal, typedBody) }

and private inferLetTuple (tail: Env -> Expr -> HMType * TypedExpr) (env: Env) (names: string list) (value: Expr) (body: Expr) (r: Range) : HMType * TypedExpr =
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

    let bodyType, typedBody = tail localEnv body

    bodyType,
    { Type = bodyType
      Range = r
      Node = TLetTuple(names, typedVal, typedBody) }

and private inferLet (tail: Env -> Expr -> HMType * TypedExpr) (env: Env) (name: string) (isFun: bool) (args: DefunArg list) (typeAnn: FType option) (value: Expr) (body: Expr) (r: Range) : HMType * TypedExpr =
    let inferBinding () =
        // For a function-shaped binding `typeAnn` is the *return* type, and is
        // already accounted for by the shape; for a value binding it is the
        // binding's own type.
        let shape = if isFun then Some(localFunShape env args typeAnn) else None

        let valType, typedVal, localFun =
            match shape with
            | Some s ->
                let lambda, lf = inferLocalFunBody env name s args r value
                s.FunType, lambda, lf
            | None ->
                // When the binding has a type annotation, resolve it first and
                // pass it down as the expected type.  For list / vec literals
                // this enables per-element constructor injection before any
                // element-level unification can fail.
                // The annotation is the expected type and the value is what is
                // supplied, so an ordinary function is accepted for a `-bjo->`
                // annotation and is wrapped to have that type.
                let t, typed =
                    match typeAnn with
                    | Some tAnn ->
                        let expectedType = resolveTypeAnnotation env.Registry tAnn
                        let t, typed = inferChecked expectedType env value
                        unify env.Registry expectedType t
                        expectedType, bridgeToSuspending env expectedType typed
                    | None -> infer env value

                t, typed, noParams

        shape, valType, typedVal, localFun

    // One level in just for the binding that is actually generalized. A
    // value is bound monomorphically, and its cells belong in the enclosing
    // level — if we raised it here the next sibling binding would quantify them.
    let shape, valType, typedVal, localFun =
        if isFun then atLevel inferBinding else inferBinding ()

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

    let bodyType, typedBody = tail localEnv body

    bodyType,
    { Type = bodyType
      Range = r
      Node = TLet(name, isFun, localFun, typedVal, typedBody) }

and private inferLetRec (tail: Env -> Expr -> HMType * TypedExpr) (env: Env) (bindings: (string * bool * DefunArg list * FType option * Expr) list) (body: Expr) (r: Range) : HMType * TypedExpr =
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
                    let lambda, lf = inferLocalFunBody recEnv name s args r expr
                    s.FunType, lambda, lf
                | None ->
                    let t, typed = infer recEnv expr

                    // A value binding's annotation is its own type. A
                    // function's is its return type, and the shape has
                    // already unified it with the body. The annotation is the
                    // expected type, as in `inferLet`.
                    match typeAnn with
                    | Some tAnn ->
                        let expectedType = resolveTypeAnnotation env.Registry tAnn
                        unify env.Registry expectedType t
                        expectedType, bridgeToSuspending env expectedType typed, noParams
                    | None -> t, typed, noParams

            unify env.Registry valType expectedType
            name, isFun, localFun, typedVal)

    // No own level for the group, unlike `ELet`. Each member
    // is bound monomorphically in `recEnv` while the bodies are checked, so
    // the cells belong to the enclosing level and `generalizeLocal` below
    // quantifies nothing beyond what the annotations already named. A
    // recursive local function is monomorphic; see `057_local_generalization`.
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

    let bodyType, typedBody = tail finalEnv body

    bodyType,
    { Type = bodyType
      Range = r
      Node = TLetRec(typedBindings, typedBody) }

and private inferLetMutable (tail: Env -> Expr -> HMType * TypedExpr) (env: Env) (name: string) (typeAnn: FType option) (value: Expr) (body: Expr) (r: Range) : HMType * TypedExpr =
    // An annotation is an expectation, as it is for `def`.
    let valType, typedVal =
        match typeAnn with
        | Some tAnn ->
            let expectedType = resolveTypeAnnotation env.Registry tAnn
            let t, typed = inferChecked expectedType env value
            unify env.Registry expectedType t
            expectedType, bridgeToSuspending env expectedType typed
        | None -> infer env value

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

    let bodyType, typedBody = tail localEnv body

    bodyType,
    { Type = bodyType
      Range = r
      Node = TLetMutable(name, typedVal, typedBody) }

and private inferSeq (env: Env) (body: Expr) (r: Range) : HMType * TypedExpr =
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
// Only `bjo` hands the promise back. The three `spawn` forms are `Unit`,
// because the scope is holding the child and nothing else needs a handle —
// which is what keeps them out of `MustUse`'s way without giving `bjo` an
// exemption from it.
and private inferBjo (env: Env) (call: Expr) (kind: SpawnKind) (r: Range) : HMType * TypedExpr =
    let resultType, tCall = infer env call

    let formType =
        match kind with
        | SpawnScoped -> TCon("Promise", [ resultType ])
        | SpawnUnit
        | SpawnDaemon
        | SpawnDetached -> TypeConstants.unitType

    formType,
    { Type = formType
      Range = r
      Node = TBjo(tCall, kind) }

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
and private inferTaskEvent (env: Env) (call: Expr) (r: Range) : HMType * TypedExpr =
    let where = Lexer.formatPos r

    let name, args =
        match call with
        | EApp(EIdent(n, _), a, _) -> n, a
        // The import, named by a macro's template.
        | EApp(EResolved(n, _), a, _) -> n, a
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

    let _, clrType, receiverType = externTarget info r
    let allTypedArgs = args |> List.map (infer env)

    // An instance member's receiver is the first argument here as it is
    // everywhere else. It is evaluated where the form stands, like the other
    // operands, and only the *call* is deferred to the sync.
    let receiver, typedArgs =
        if info.IsInstance then
            match allTypedArgs with
            | (_, recv) :: rest ->
                Some(reconcileForeignArgs env.Registry [ recv ] [ receiverType ] |> List.head), rest
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

    checkDeclaredExtern env.Registry info receiverType receiver.IsSome visibleParams awaited

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

and private inferMatch (tail: Env -> Expr -> HMType * TypedExpr) (env: Env) (target: Expr) (clauses: (Pattern * Expr option * Expr) list) (r: Range) : HMType * TypedExpr =
    let targetType, typedTarget = infer env target
    let returnType = freshMeta ()

    let typedClauses =
        clauses
        |> List.map (fun (pat, guard, body) ->
            let typedPat, boundVars = checkPattern inferChecked env targetType pat

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

            let bodyType, typedBody = tail boundEnv body

            unify env.Registry bodyType returnType

            { Pattern = typedPat
              Guard = typedGuard
              Body = typedBody }
            : TMatchClause)

    returnType,
    { Type = returnType
      Range = r
      Node = TMatch(typedTarget, typedClauses) }

and private inferRecordUpdate (env: Env) (targetName: string) (fields: (string * Expr) list) (r: Range) : HMType * TypedExpr =
    let targetBinding = lookup env targetName
    let targetType, _, _ = instantiate env.Registry targetBinding.Scheme
    
    let recordTypeName =
        if fields.IsEmpty then
            failwithf $"Type Error at %s{formatPos r}: a record-set has to update at least one field."

        recordTypeOfField env.Registry (codeModule env r) "updated" targetType (fst fields.Head) r


    let instantiatedRecordType, _, expectedFieldsInstantiated =
        instantiateRecord env.Registry recordTypeName

    unify env.Registry targetType instantiatedRecordType

    let typedFields =
        fields |> List.map (fun (name, expr) ->
            let exprType, typedExpr = infer env expr
            match Map.tryFind name expectedFieldsInstantiated with
            | Some expectedType ->
                try
                    unifyExpecting
                        env.Registry
                        expectedType
                        exprType
                        $"the field '%s{name}' of '%s{Naming.showTypeName recordTypeName}'"
                with ex when Diagnostics.needsLocation ex ->
                    raise (Diagnostics.withLocation (exprRange expr) ex)

                name, bridgeToSuspending env expectedType typedExpr
            | None -> failwithf $"Type Error: Field '%s{name}' does not belong to record '%s{recordTypeName}' at %s{Lexer.formatPos r}")

    targetType,
    { Type = targetType
      Range = r
      Node = TRecordUpdate(targetName, typedFields) }

// `(record-set! r (field value) ...)` — the write in place.
//
// Shaped like `ERecordUpdate` above and checked like it, plus the two
// questions a write has that a copy does not: is this field writable, and
// is this module allowed to write it.
and private inferRecordSet (env: Env) (targetName: string) (fields: (string * Expr) list) (r: Range) : HMType * TypedExpr =
    let targetBinding = lookup env targetName
    let targetType, _, _ = instantiate env.Registry targetBinding.Scheme

    // Non-empty by construction — the parser refuses a `record-set!` that
    // names no field — so the head is safe to resolve the type from.
    let recordTypeName =
        recordTypeOfField env.Registry (codeModule env r) "written" targetType (fst fields.Head) r

    let instantiatedRecordType, _, expectedFieldsInstantiated =
        instantiateRecord env.Registry recordTypeName

    unify env.Registry targetType instantiatedRecordType

    // A field is writable only where it was declared. The check is on the
    // *record's* module rather than on the binding's: a value of a foreign
    // record type reaches here by every ordinary route — an argument, a
    // field of something local — and none of them may write it. The writing
    // code's module is the range's, so a body its module published writes its
    // own fields wherever it is spliced.
    if not (declaredHere (codeModule env r) recordTypeName) then
        let shown = Naming.showTypeName recordTypeName

        failwithf
            $"Type Error at %s{formatPos r}: '%s{shown}' was declared in another module, so this one may not write its fields. A module that means its state to be written from outside exports functions that write it."

    let mutableFields = mutableFieldsOf env.Registry recordTypeName

    let typedFields =
        fields |> List.map (fun (name, expr) ->
            let exprType, typedExpr = infer env expr

            let typedExpr =
                match Map.tryFind name expectedFieldsInstantiated with
                | Some expectedType ->
                    try
                        unifyExpecting
                            env.Registry
                            expectedType
                            exprType
                            $"the field '%s{name}' of '%s{Naming.showTypeName recordTypeName}'"
                    with ex when Diagnostics.needsLocation ex ->
                        raise (Diagnostics.withLocation (exprRange expr) ex)

                    bridgeToSuspending env expectedType typedExpr
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

// `(dyn ->str 42)` — packing a value into a trait box.
//
// The argument's type is represented by a hole metavariable, so whatever
// binds it binds the implementation. The obligation is queued just like a
// trait method call: the hole is prevented from being generalized
// prematurely, and resolving it pins the box. If it remains open,
// projections linger and the enclosing function receives the constraint as a
// `_dict_` parameter like any other trait usage.
and private inferDynPack (env: Env) (writtenTrait: string) (valueExpr: Expr) (r: Range) : HMType * TypedExpr =
    let traitName = originalTraitName env.Registry writtenTrait

    let info =
        match Map.tryFind traitName env.Registry.Traits with
        | Some i -> i
        | None ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: '%s{writtenTrait}' is not a trait in scope, so (dyn %s{writtenTrait} ...) packs nothing."

    match info.DynSafe with
    | Error why -> failwithf $"Type Error at %s{Lexer.formatPos r}: %s{why}"
    | Ok() -> ()

    let hole, typedValue = infer env valueExpr

    let tref =
        { Trait = traitName
          Method = writtenTrait
          Holes = [ hole ]
          MethodType = tfun [ hole ] TypeConstants.unitType
          MemberConstraints = []
          Resolved = None }

    pushWanted
        { Trait = traitName
          Method = writtenTrait
          Kind = info.Kind
          HoleArgs = [ hole, [] ]
          Ref = tref
          Range = r }

    // Associated types are never specified at packing time: they are
    // resolved from the implementation chosen for the hole. `prune`
    // resolves projections as soon as the hole becomes concrete, unifying
    // with explicit annotations.
    let dynType =
        TCon(
            Naming.dynTypeName traitName info.AssociatedTypes,
            info.AssociatedTypes |> List.map (fun a -> TAssoc(traitName, a, hole))
        )

    dynType,
    { Type = dynType
      Range = r
      Node = TDynPack(traitName, hole, typedValue) }

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
    (name: string)
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

    // The type variables the local function's annotations name are its body's,
    // as a top-level signature's are (see `scopedTypeVars`): one type for the
    // whole body. Not scoped, a function nested in this one that mentions
    // `%b` quantified it, and each call could then use a `%b` value at
    // another type.
    let outerScoped = Unification.scopedTypeVars

    Unification.scopedTypeVars <-
        Set.union outerScoped (Set.ofList (freeTVars env.Registry shape.FunType))

    let typedBody, typedKeywords =
        try
            // The return type is the body's expectation. With none written, a
            // body ending in a .NET void call returns the unit, as a lambda's
            // does (see `functionResult`); a written `: void` already is the
            // unit.
            let bodyType, typedBody = inferChecked shape.RetType bodyEnv value
            let bodyType =
                match prune env.Registry shape.RetType with
                | TMeta _ -> functionResult env bodyType None
                | _ -> bodyType
            unifyExpecting env.Registry shape.RetType bodyType $"what '%s{name}' returns"

            let typedKeywords, _ =
                shape.Keywords
                |> List.fold
                    (fun (acc, currentEnv) (n, t, defaultExpr) ->
                        let defaultType, typedDefault = infer currentEnv defaultExpr
                        unify env.Registry defaultType t
                        acc @ [ n, t, typedDefault ], bind n t currentEnv)
                    ([], envWithMandatory)

            typedBody, typedKeywords
        finally
            Unification.scopedTypeVars <- outerScoped

    let paramNames = allArgNames args

    { Type = shape.FunType
      Range = r
      Node = TLambda(paramNames, typedBody) },
    { Params = paramNames
      KeywordArgs = typedKeywords
      RestArg = shape.Rest }

/// A collection literal whose element type the context already named.
///
/// This is the branch that *injects*: each ordinary element goes through
/// `inferAndMaybeInject`, which wraps it in the union case that can hold it.
/// Splicing changes nothing about that. A run of ordinary elements stays one
/// literal node and is injected exactly as before; only the runs either side of
/// a `,@` are new, and the spliced value is not injected at all — it already
/// has the collection type, which is what `checkSplice` demands of it.
///
/// With no splice this builds precisely the node it built before there was such
/// a thing, and nothing downstream can tell the two compilers apart.
and private inferCheckedLiteral
    (env: Env)
    (ctor: string)
    (literalName: string)
    (mkNode: TypedExpr list -> TExprNode)
    (elemTy: HMType)
    (exprs: Expr list)
    (r: Range)
    : HMType * TypedExpr =
    let collectionType = TCon(ctor, [ elemTy ])

    let run (elements: Expr list) : TypedExpr =
        { Type = collectionType
          Range = r
          Node = mkNode (elements |> List.map (inferAndMaybeInject elemTy env)) }

    if not (hasSplice exprs) then
        collectionType, run exprs
    else
        splicePieces exprs
        |> List.map (function
            | Choice1Of2 elements -> run elements
            | Choice2Of2(inner, sr) -> checkSplice env literalName collectionType inner sr)
        |> fun pieces -> assembleSplicedLiteral ctor elemTy pieces r

and internal inferChecked (expected: HMType) (env: Env) (expr: Expr) : HMType * TypedExpr =
    try
        inferCheckedNode expected env expr
    with ex when Diagnostics.needsLocation ex ->
        raise (Diagnostics.withLocation (exprRange expr) ex)

and private inferCheckedNode (expected: HMType) (env: Env) (expr: Expr) : HMType * TypedExpr =
    // A bare `.field` is checked as the lambda it stands for.
    let expr = fieldAccessorLambda env expr

    match expr, prune env.Registry expected with
    // The call, as in `inferNode`, and checked against what is expected.
    | EApp(EResolved(marker, _), [ _; _; call ], _), _ when marker = fieldOrCallMarker ->
        inferChecked expected env call

    // A hole takes the expected type as its own.
    | EIdent(name, r), _ when Holes.isHoleName env name ->
        let t, typed = inferHole env name r
        unify env.Registry t expected
        t, typed

    // A case name several unions have, where a value of one of them is wanted.
    | EIdent(name, r), _ when (caseChosenBy env expected name).IsSome ->
        let key = (caseChosenBy env expected name).Value
        chooseCase r name key
        inferChecked expected env (EIdent(key, r))
    | EApp(EIdent(name, ir), args, r), _ when (caseChosenBy env expected name).IsSome ->
        let key = (caseChosenBy env expected name).Value
        chooseCase ir name key
        inferChecked expected env (EApp(EIdent(key, ir), args, r))
    | EList(exprs, r), TCon("List", [ elemTy ]) ->
        inferCheckedLiteral env "List" "list" TListMake elemTy exprs r
    | EVec(exprs, r), TCon("Vec", [ elemTy ]) ->
        inferCheckedLiteral env "Vec" "vec" TVecMake elemTy exprs r
    | EArray(exprs, r), TCon("Array", [ elemTy ]) ->
        inferCheckedLiteral env "Array" "array" TArrayMake elemTy exprs r

    // A literal written where a union is expected is elaborated into a case of
    // it, at the top level exactly as in an element position: `(f '(pipe …))`
    // and `(def (: x Form) '(pipe …))` are the same question as `(list '(pipe
    // …))` is. Only the nodes `literalPayloadHeads` can name a shape for are
    // routed here, because the type-directed path below asks this function the
    // same question again and would otherwise come straight back.
    // A literal where a production of a grammar is expected is read by the
    // grammar. This comes before the union below, because a production that
    // is an `:or` is also a union.
    | (EList _ | EVec _ | EArray _ | EString _ | EQuotedSymbol _ | EInt _ | EChar _ | EBool _ | EKeyword _),
      TCon(key, _) when Map.containsKey key env.Registry.Grammars ->
        let typed = matchGrammar env expected expr
        typed.Type, typed

    // A lambda whose parameters and result the expectation already names.
    | EFun(args, body, colour, r), TFun(paramTys, ret, _) when List.length paramTys = List.length args ->
        inferLambda (Some(paramTys, ret)) env args body colour r

    // A form whose value is one of its tail positions passes the expectation
    // on, so `(if c '(from a) '(from b))` and a `let` body ending in a literal
    // are elaborated where the whole form is expected to be a union.
    | (EIf _ | ELetMono _ | ELet _ | ELetRec _ | ELetMutable _ | ELetTuple _ | EMatch _ | EDefMatch _), _ ->
        (inferTailForm (inferChecked expected) env expr).Value

    // A function with keyword parameters, where a function of its mandatory
    // ones is expected. See `keywordFreeReference`.
    | EIdent(name, r), TFun _ ->
        match keywordFreeReference env expected name r with
        | Some typed -> typed
        | None -> infer env expr

    // Any other value where a production of a grammar is expected is read
    // by its type, as it is in an element of a literal: a function declared
    // to return a `Block` may return a string it was given.
    | _, (TCon(key, args) as production) when Map.containsKey key env.Registry.Grammars ->
        let ty, typed = infer env expr

        match grammarOf env.Registry production with
        | Some(k, a, p) ->
            let read = spliceIntoGrammar env k a p ty typed
            read.Type, read
        | None -> ty, typed

    | _ ->
        infer env expr

/// A form whose value is one of its tail positions, with `tail` checking each
/// tail position. `None` for any other form.
and private inferTailForm
    (tail: Env -> Expr -> HMType * TypedExpr)
    (env: Env)
    (expr: Expr)
    : (HMType * TypedExpr) option =
    match expr with
    | EIf(cond, t, f, r) -> Some(inferIf tail env cond t f r)
    | ELetMono(name, value, body, r) -> Some(inferLetMono tail env name value body r)
    | ELet(name, isFun, args, typeAnn, value, body, r) ->
        Some(inferLet tail env name isFun args typeAnn value body r)
    | ELetRec(bindings, body, r) -> Some(inferLetRec tail env bindings body r)
    | ELetMutable(name, typeAnn, value, body, r) -> Some(inferLetMutable tail env name typeAnn value body r)
    | ELetTuple(names, value, body, r) -> Some(inferLetTuple tail env names value body r)
    | EMatch(target, clauses, r) -> Some(inferMatch tail env target clauses r)
    | EDefMatch(binder, scrutinee, failure, sequel, r) ->
        Some(inferDefMatch tail env binder scrutinee failure sequel r)
    | _ -> None

/// The result type of a function whose body has type `bodyType`, given the
/// result the context expects, if it expects one.
///
/// A body that ends in a call to a .NET method returning void has
/// `System.Void` for a type, which C# has no value of, and a function with that
/// result is emitted as an `Action`. A Bjolang function returns the unit then,
/// so that it is the `Func<..., Unit>` a `(-> ... void)` parameter is. Only a
/// function that goes straight to a .NET parameter of delegate type `Action`
/// stays one.
and private functionResult (env: Env) (bodyType: HMType) (expected: HMType option) : HMType =
    match prune env.Registry bodyType, expected |> Option.map (prune env.Registry) with
    | TCon(TypeConstants.VoidName, []), Some(TCon(TypeConstants.VoidName, [])) -> bodyType
    | TCon(TypeConstants.VoidName, []), _ -> TypeConstants.unitType
    | _ -> bodyType

/// A bare name of a function with keyword parameters, where a function of only
/// its mandatory parameters is expected: `read-line` in
/// `(port->seq read-line p)`.
///
/// As a value, such a function has its keyword parameters as positional
/// ones after the mandatory ones, so it does not fit. Where the expected
/// function takes exactly the mandatory parameters, the name is the function
/// that calls it with only those, and the keywords take their defaults: the
/// lambda `(fun (p) (read-line p))`, built here.
///
/// The lambda is built typed, because its colour is not `fun`'s. It takes the
/// effect of the bare name: the callee's own for an ordinary function or a
/// `defbjo`, and an open cell for a function with two copies, which the
/// parameter it fills then decides. Thus `(port->chan read-line p)` reads
/// with the suspending copy of `read-line`, as a bare name would.
///
/// `None` when the rule does not apply, and the name is inferred as before.
and private keywordFreeReference (env: Env) (expected: HMType) (name: string) (r: Range) : (HMType * TypedExpr) option =
    match Map.tryFind name env.FunMetas, prune env.Registry expected with
    | Some meta, TFun(expectedParams, _, _) when
        not meta.KeywordParams.IsEmpty
        && expectedParams.Length = meta.MandatoryCount
        && not (Set.contains name env.TraitMethodNames)
        && Map.containsKey name env.Bindings
        ->
        let effect =
            match prune env.Registry (fst (inferIdent env name r)) with
            | TFun(_, _, e) -> e
            | _ -> ESync

        let names = expectedParams |> List.map (fun _ -> Gensym.fresh "kwfree")
        let paramTypes = expectedParams |> List.map (fun _ -> freshMeta ())

        let bodyEnv =
            List.zip names paramTypes
            |> List.fold
                (fun acc (n, t) ->
                    addBinding
                        n
                        { Scheme = Scheme([], [], t)
                          IsMutable = false }
                        acc)
                env

        let callType, call =
            infer bodyEnv (EApp(EIdent(name, r), names |> List.map (fun n -> EIdent(n, r)), r))

        let funType = TFun(paramTypes, callType, effect)

        Some(
            funType,
            { Type = funType
              Range = r
              Node = TLambda(names, call) }
        )
    | _ -> None

/// A lambda. `expected` is the parameter and result types the context
/// expects, when it expects any. Unifying the parameters before the body is
/// inferred is what lets the body read a record field off a parameter, since
/// `recordTypeOfField` needs the record type at the moment of the access. The
/// body is checked against the result, as a `defun`'s body is checked against
/// its declared one, so a literal it ends in is elaborated: `(fun (x) 'fast)`
/// is a `Mode` where a function answering one is wanted.
///
/// The effect is the lambda's own keyword and never comes from the context. A
/// `fun` is `ESync` and a `bjoroutine` is `EAsync`, `ColourCheck` reads that off
/// the node, and taking it from a `-bjo->` parameter instead would repaint the
/// lambda and make the colour diagnostics wrong.
and private inferLambda
    (expected: (HMType list * HMType) option)
    (env: Env)
    (args: string list)
    (body: Expr)
    (colour: Colour)
    (r: Range)
    : HMType * TypedExpr =
    let argTypes = args |> List.map (fun _ -> freshMeta ())

    match expected with
    | Some(paramTys, _) -> List.iter2 (fun argTy paramTy -> unify env.Registry argTy paramTy) argTypes paramTys
    | None -> ()

    let expectedResult = expected |> Option.map snd

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

    // An unsolved result expects nothing in particular, as an unsolved
    // parameter type does for an argument.
    let bodyType, typedBody =
        match expectedResult |> Option.map (prune env.Registry) with
        | None
        | Some(TMeta _) -> infer localEnv body
        | Some result -> inferChecked result localEnv body

    let funType = TFun(argTypes, functionResult env bodyType expectedResult, colourEffect colour)

    funType,
    { Type = funType
      Range = r
      Node = TLambda(args, typedBody) }

/// Check one element of a literal against the type its position expects.
///
/// Where a production of a grammar is expected, the grammar reads the element.
/// Anything else is checked as the value it is: a quoted literal is
/// elaborated by a grammar only, never by a union's cases.
and private inferAndMaybeInject (expectedElem: HMType) (env: Env) (expr: Expr) : TypedExpr =
    match grammarOf env.Registry expectedElem with
    | Some _ -> matchGrammar env expectedElem expr
    | None ->
        let elemTy, te = inferChecked expectedElem env expr

        try
            unifyExpecting env.Registry expectedElem elemTy "this element"
        with ex when Diagnostics.needsLocation ex ->
            raise (Diagnostics.withLocation (exprRange expr) ex)

        te

// ---------------------------------------------------------------------------
// Grammars: reading a literal by its grammar
// ---------------------------------------------------------------------------

/// A quoted literal, or a value spliced into one, where a production of a
/// grammar is expected. See `Docs/Grammar-design.org`.
///
/// A literal is read by the production: an `:or` picks its alternative by
/// the literal's keyword first and its shape second, a record reads its
/// fields from the list, and an alias reads the literal as its item. A
/// spliced value is put where its type fits.
///
/// The result is the typed constructor calls the literal stands for. Nothing
/// is rewritten into source and inferred again: every value is checked against
/// the type its place in the grammar gives it, as it is read.
and internal matchGrammar (env: Env) (expected: HMType) (expr: Expr) : TypedExpr =
    match grammarOf env.Registry expected with
    | None -> inferAndMaybeInject expected env expr
    | Some(key, args, p) ->
        if isWrittenLiteral expr then
            matchProduction env key args p expr
        else
            // Each tail position of an `if`, a `match` or a `let` is read as
            // the form itself would be: `(match o ((Some s) s) (None "-"))`
            // is a block where a block is expected, as `s` alone is.
            let readTail env' e =
                let typed = matchGrammar env' expected e
                typed.Type, typed

            match inferTailForm readTail env expr with
            | Some(_, typed) -> typed
            | None ->
                let ty, typed = inferChecked (TCon(key, args)) env expr
                spliceIntoGrammar env key args p ty typed

/// A spliced value where a production is expected. A value of the
/// production's own type is the production. Otherwise, for an `:or`, the
/// alternative whose type the value has: `,raw` is a `RawCond` because it is a
/// `Sql`. An alternative that is itself a production is looked into, as far as
/// it goes.
and private spliceIntoGrammar
    (env: Env)
    (key: string)
    (args: HMType list)
    (p: GrammarProduction)
    (ty: HMType)
    (typed: TypedExpr)
    : TypedExpr =
    let pe = TCon(key, args)

    match prune env.Registry ty with
    | TCon(k, _) when k = key ->
        unify env.Registry ty pe
        typed
    // A number whose type is still open, as a `(def n 3)` is. It goes where
    // the same number written out would go, and has the type that place
    // gives it: in `["n = " n]`, the `BInt` that `["n = " 3]` would be. A
    // grammar's alternatives do not overlap, so at most one takes a number.
    | found when isOpenLiteral found ->
        let inst = grammarInstance p args
        let written = EInt((if isOpenRealLiteral found then "0.0" else "0"), typed.Range)

        let takesNumber =
            match p.Shape with
            | GsUnion alternatives ->
                alternatives |> List.filter (fun (_, item) -> grammarFits env.Registry inst Set.empty item written)
            | _ -> []

        match takesNumber with
        | [ (case, GrType t) ] ->
            let payloadTy = inst t

            let value =
                match grammarOf env.Registry payloadTy with
                | Some(k2, a2, p2) -> spliceIntoGrammar env k2 a2 p2 ty typed
                | None ->
                    unifyExpecting env.Registry payloadTy ty "this value"
                    typed

            let built = applyConstructor env case [ value ] typed.Range
            unify env.Registry built.Type pe
            built
        // No alternative takes a number. Said here, at the value, because the
        // literal its type comes from may be far away.
        | _ ->
            let number = if isOpenRealLiteral found then "a number with a fraction" else "a number"

            let found =
                match typed.Node with
                | TIdent(n, _) -> $"'%s{n}', %s{number}"
                | _ -> number

            let expected =
                match p.Shape with
                | GsUnion alternatives -> describeGrammarItem env.Registry inst (GrOr alternatives)
                | _ -> p.Name

            grammarError typed.Range p.Name $"expected %s{expected}; found %s{found}."
    | found ->
        let inst = grammarInstance p args

        match p.Shape, found with
        | GsUnion alternatives, (TCon _ | TFun _ | TTuple _) ->
            let fits =
                alternatives
                |> List.filter (fun (_, item) ->
                    match item with
                    | GrType t
                    | GrSpliceOnly t -> spliceFitsType env.Registry Set.empty (inst t) found
                    | _ -> false)

            match fits with
            | [ (case, (GrType t | GrSpliceOnly t)) ] ->
                let payloadTy = inst t

                let value =
                    match grammarOf env.Registry payloadTy with
                    | Some(k2, a2, p2) -> spliceIntoGrammar env k2 a2 p2 ty typed
                    | None ->
                        unifyExpecting env.Registry payloadTy ty "this value"
                        typed

                let built = applyConstructor env case [ value ] typed.Range
                unify env.Registry built.Type pe
                built
            // A value of another type, which no alternative takes. Said as a
            // literal that fits nothing is: what the grammar takes, and
            // what it was given.
            | [] ->
                grammarError
                    typed.Range
                    p.Name
                    $"expected %s{describeGrammarItem env.Registry inst (GrOr alternatives)}; found %s{withArticle (DotNetInterop.showType found)}."
            | many ->
                let names = many |> List.map (fst >> Naming.showTypeName) |> orList

                grammarError
                    typed.Range
                    p.Name
                    $"%s{withArticle (DotNetInterop.showType found)} fits %s{names}, and nothing here says which."
        | _ ->
            unifyExpecting env.Registry pe ty "this value"
            typed

/// Whether a spliced value of type `found` fits where `t` is expected,
/// decided without binding anything: a type that is a production takes the
/// values its alternatives take.
and private spliceFitsType (registry: TraitRegistry) (visited: Set<string>) (t: HMType) (found: HMType) : bool =
    let rec matches pat conc =
        match prune registry pat, prune registry conc with
        | TMeta _, _
        | _, TMeta _
        | TVar _, _
        | _, TVar _ -> true
        | TCon(n1, a1), TCon(n2, a2) -> n1 = n2 && a1.Length = a2.Length && List.forall2 matches a1 a2
        | TFun(a1, r1, _), TFun(a2, r2, _) -> a1.Length = a2.Length && List.forall2 matches a1 a2 && matches r1 r2
        | TTuple a1, TTuple a2 -> a1.Length = a2.Length && List.forall2 matches a1 a2
        | a, b -> a = b

    match grammarOf registry t, prune registry found with
    | Some(key, _, _), TCon(k, _) when k = key -> true
    | Some(key, _, _), _ when visited.Contains key -> false
    | Some(key, args, p), _ ->
        let inst = grammarInstance p args

        match p.Shape with
        | GsUnion alternatives ->
            alternatives
            |> List.exists (fun (_, item) ->
                match item with
                | GrType t
                | GrSpliceOnly t -> spliceFitsType registry (Set.add key visited) (inst t) found
                | _ -> false)
        | _ -> false
    | None, _ -> matches t found

/// A constructor applied to its typed arguments, or alone when it takes none.
and private applyConstructor (env: Env) (name: string) (args: TypedExpr list) (r: Range) : TypedExpr =
    let fnTy, fnExpr = inferIdent env name r

    match args with
    | [] -> { fnExpr with Range = r }
    | _ ->
        let result = freshMeta ()
        unify env.Registry fnTy (tfun (args |> List.map (fun a -> a.Type)) result)

        { Type = result
          Range = r
          Node = TApply(fnExpr, args, []) }

/// `#t` or `#f`, for an optional keyword: whether it was there.
and private grammarBool (env: Env) (value: bool) (r: Range) : TypedExpr = snd (infer env (EBool(value, r)))

/// A written literal where the production `p` is expected.
and private matchProduction
    (env: Env)
    (key: string)
    (args: HMType list)
    (p: GrammarProduction)
    (expr: Expr)
    : TypedExpr =
    let pe = TCon(key, args)
    let inst = grammarInstance p args
    let r = exprRange expr

    match p.Shape with
    | GsUnion alternatives ->
        let case, item = chooseAlternative env inst p alternatives expr

        let values =
            match item with
            | GrList(_, elements)
            | GrVec elements ->
                let kept = matchListItem env inst p.Name item expr
                ignore elements
                kept |> List.map snd
            | GrKeyword _ ->
                ignore (matchItem env inst p.Name item expr)
                []
            | _ -> matchItem env inst p.Name item expr |> Option.toList

        let built = applyConstructor env case values r
        unify env.Registry built.Type pe
        built
    | GsAlias item ->
        match matchItem env inst p.Name item expr with
        | Some value -> value
        | None -> failwithf $"internal: the alias production %s{p.Name} keeps nothing"
    | GsNested item -> buildGrammarRecord env key args (matchListItem env inst p.Name item expr) r
    | GsSequence elements ->
        match expr with
        | EList(items, lr) -> buildGrammarRecord env key args (matchWholeSequence env inst p.Name elements items lr) r
        | _ -> grammarError r p.Name $"expected a list; found %s{describeFound expr}."
    | GsChecked(atom, check, home) ->
        // The atom is checked as its type first, so a wrong kind of literal
        // is reported as that.
        ignore (inferChecked atom env expr)

        if home = env.CurrentModule then
            grammarError
                r
                p.Name
                $"a literal of %s{p.Name} is checked by %s{check}, which this module declares. The compiler runs a check from a compiled module, so write the literal in a module that imports this one, or call %s{check} here."

        match grammarAtomValue (prune env.Registry atom) expr with
        | None ->
            grammarError
                r
                p.Name
                $"the compiler can not give %s{describeFound expr} to a check. A checked atom is a string, a symbol, a bool, an int, a long or a double."
        | Some value ->
            match runGrammarCheck home (Naming.writtenName check) value with
            | Error message -> grammarError r p.Name message
            | Ok() ->
                // The check passed. The program calls it again, and takes the
                // value out of its Ok, which can not be an Err now.
                let call = EApp(EIdent(check, r), [ expr ], r)
                let t, typed = infer env (EApp(EResolved("grammar-checked", r), [ call ], r))
                unify env.Registry t pe
                typed

/// Which alternative of an `:or` a written literal is. A keyword decides
/// first, and the literal's shape second.
and private chooseAlternative
    (env: Env)
    (inst: HMType -> HMType)
    (p: GrammarProduction)
    (alternatives: (string * GrammarItem) list)
    (expr: Expr)
    : string * GrammarItem =
    let r = exprRange expr

    let ambiguous (many: (string * GrammarItem) list) =
        let names = many |> List.map (fst >> Naming.showTypeName) |> orList
        grammarError r p.Name $"%s{describeFound expr} can be %s{names}, and nothing here says which."

    match alternatives |> List.filter (fun (_, item) -> grammarClaims env.Registry inst item expr) with
    | [ one ] -> one
    | _ :: _ :: _ as many -> ambiguous many
    | [] ->
        match alternatives |> List.filter (fun (_, item) -> grammarFits env.Registry inst Set.empty item expr) with
        | [ one ] -> one
        | [] ->
            let expected = alternatives |> List.map (snd >> describeGrammarItem env.Registry inst) |> List.distinct

            grammarError r p.Name $"expected %s{orList expected}; found %s{describeFound expr}."
        | many -> ambiguous many

/// One written literal or spliced value against one item. The value the item
/// keeps, or `None` for an item that keeps nothing.
and private matchItem
    (env: Env)
    (inst: HMType -> HMType)
    (context: string)
    (item: GrammarItem)
    (expr: Expr)
    : TypedExpr option =
    let r = exprRange expr

    let expectedHere () =
        grammarError r context $"expected %s{describeGrammarItem env.Registry inst item}; found %s{describeFound expr}."

    match item with
    | GrType t ->
        let t = inst t

        match grammarOf env.Registry t with
        | Some _ -> Some(matchGrammar env t expr)
        | None -> Some(inferAndMaybeInject t env expr)
    | GrSpliceOnly t ->
        if isWrittenLiteral expr then
            expectedHere ()

        Some(inferAndMaybeInject (inst t) env expr)
    | GrKeyword kw ->
        match expr with
        | EQuotedSymbol(sym, _) when sym = kw -> None
        | _ -> expectedHere ()
    | GrList _
    | GrVec _ ->
        match matchListItem env inst context item expr with
        | [] -> None
        | [ (_, value) ] -> Some value
        | _ -> failwithf "internal: a list item of a grammar keeps more than one value"
    | GrOpt inner ->
        match matchItem env inst context inner expr with
        | Some value -> Some(applyConstructor env "Some" [ value ] r)
        | None -> Some(grammarBool env true r)
    | GrOr _
    | GrEach _ -> failwithf "internal: an :or or :each item outside its own production"

/// A nested list or vec item: the values it keeps, by label.
and private matchListItem
    (env: Env)
    (inst: HMType -> HMType)
    (context: string)
    (item: GrammarItem)
    (expr: Expr)
    : (string option * TypedExpr) list =
    let r = exprRange expr

    let expectedHere () =
        grammarError r context $"expected %s{describeGrammarItem env.Registry inst item}; found %s{describeFound expr}."

    match item, expr with
    | GrList(Some kw, elements), EList(EQuotedSymbol(sym, _) :: rest, lr) when sym = kw ->
        matchWholeSequence env inst $"(%s{kw} ...)" elements rest lr
    | GrList(None, elements), EList(items, lr) -> matchWholeSequence env inst context elements items lr
    | GrVec elements, EVec(items, vr) -> matchWholeSequence env inst context elements items vr
    | _ -> expectedHere ()

/// A sequence that has to use up the whole list it is read from.
and private matchWholeSequence
    (env: Env)
    (inst: HMType -> HMType)
    (context: string)
    (elements: GrammarElement list)
    (items: Expr list)
    (r: Range)
    : (string option * TypedExpr) list =
    let kept, left, could = matchSequence env inst context elements items r

    match left with
    | [] -> kept
    | extra :: _ ->
        let expected =
            match could with
            | [] -> "the end of the list"
            | _ -> orList (List.distinct could @ [ "the end of the list" ])

        grammarError (exprRange extra) context $"expected %s{expected}; found %s{describeFound extra}."

/// The elements of a sequence, read from the front of `items`.
///
/// Gives back the values kept, by label and in order; the items not used; and
/// what else could have come where the reading stopped, for the message if
/// what is left is wrong. Each choice is made by the next item alone, which a
/// grammar is checked for where it is declared.
and private matchSequence
    (env: Env)
    (inst: HMType -> HMType)
    (context: string)
    (elements: GrammarElement list)
    (items: Expr list)
    (r: Range)
    : (string option * TypedExpr) list * Expr list * string list =
    let describe item = describeGrammarItem env.Registry inst item
    let fits item expr = grammarFits env.Registry inst Set.empty item expr

    let notSplice (expr: Expr) =
        match expr with
        | ESplice _ -> false
        | _ -> true

    /// The production an item names, when it is a sequence read in place.
    let inPlace (item: GrammarItem) =
        match item with
        | GrType t ->
            match grammarOf env.Registry (inst t) with
            | Some(key, args, ({ Shape = GsSequence elements } as p)) -> Some(key, args, p, elements)
            | _ -> None
        | _ -> None

    let present (item: GrammarItem) (expr: Expr) : TypedExpr =
        match item with
        | GrOpt inner ->
            match matchItem env inst context inner expr with
            | Some value -> applyConstructor env "Some" [ value ] (exprRange expr)
            | None -> grammarBool env true (exprRange expr)
        | _ ->
            match matchItem env inst context item expr with
            | Some value -> value
            | None -> failwithf "internal: a field of a grammar keeps nothing"

    let absent (inner: GrammarItem) : TypedExpr =
        if grammarKeeps inner then
            applyConstructor env "None" [] r
        else
            grammarBool env false r

    let rec go (elements: GrammarElement list) (items: Expr list) kept (could: string list) =
        match elements with
        | [] -> List.rev kept, items, could
        | e :: rest ->
            match e.Item, e.Rep with
            | GrEach fields, GOne ->
                let mutable given: Map<string, TypedExpr> = Map.empty
                let mutable left = items

                let pick (next: Expr) =
                    match fields |> List.filter (fun (_, i) -> grammarClaims env.Registry inst i next) with
                    | [] -> fields |> List.filter (fun (_, i) -> fits i next)
                    | claimed -> claimed

                let mutable reading = true

                while reading do
                    match left with
                    | next :: more when notSplice next ->
                        match pick next with
                        | [ (name, i) ] ->
                            if given.ContainsKey name then
                                grammarError (exprRange next) context $"%s{describe i} is given twice."

                            given <- Map.add name (present i next) given
                            left <- more
                        | [] -> reading <- false
                        | many ->
                            let names = many |> List.map (snd >> describe) |> orList
                            grammarError (exprRange next) context $"%s{describeFound next} can be %s{names}."
                    | _ -> reading <- false

                let values =
                    fields
                    |> List.map (fun (name, i) ->
                        match Map.tryFind name given, i with
                        | Some value, _ -> Some name, value
                        | None, GrOpt inner -> Some name, absent inner
                        | None, _ ->
                            let at =
                                match left with
                                | next :: _ -> exprRange next
                                | [] -> r

                            grammarError at context $"%s{describe i} is missing.")

                let open' =
                    fields |> List.filter (fun (name, _) -> not (given.ContainsKey name)) |> List.map (snd >> describe)

                go rest left (List.rev values @ kept) (if given.IsEmpty then could @ open' else open')
            | item, GOne when (inPlace item).IsSome ->
                let key, args, p, inner = (inPlace item).Value
                let innerInst = grammarInstance p args
                let innerKept, left, innerCould = matchSequence env innerInst p.Name inner items r
                let record = buildGrammarRecord env key args innerKept r
                let used = List.length items - List.length left
                go rest left ((e.Label, record) :: kept) (if used = 0 then could @ innerCould else innerCould)
            | GrOpt inner, GOne ->
                match items with
                | next :: more when notSplice next && fits inner next ->
                    go rest more ((e.Label, present e.Item next) :: kept) []
                | _ -> go rest items ((e.Label, absent inner) :: kept) (could @ [ describe inner ])
            | item, GOne ->
                let expected () = orList (List.distinct (could @ [ describe item ]))

                match items with
                | [] -> grammarError r context $"expected %s{expected ()}; found the end of the list."
                | (ESplice(_, sr)) :: _ ->
                    grammarError
                        sr
                        context
                        $",@ puts several values where one is expected: %s{describe item}. Use ,@ where the grammar repeats an item, with ... or ...+."
                | next :: more ->
                    if not (fits item next) then
                        grammarError (exprRange next) context $"expected %s{expected ()}; found %s{describeFound next}."

                    let kept =
                        match matchItem env inst context item next with
                        | Some value -> (e.Label, value) :: kept
                        | None -> kept

                    go rest more kept []
            | item, rep ->
                let elemTy = freshMeta ()

                let rec take (items: Expr list) acc =
                    match items with
                    | ESplice(inner, sr) :: more -> take more (Choice2Of2(inner, sr) :: acc)
                    | next :: more when fits item next ->
                        match matchItem env inst context item next with
                        | Some value ->
                            unify env.Registry value.Type elemTy
                            take more (Choice1Of2 value :: acc)
                        | None -> failwithf "internal: a repeat of an item that keeps nothing"
                    | _ -> List.rev acc, items

                let taken, left = take items []

                if rep = GPlus && taken.IsEmpty then
                    let at =
                        match left with
                        | next :: _ -> exprRange next
                        | [] -> r

                    grammarError at context $"expected at least one %s{describe item}."

                let listTy = TCon("List", [ elemTy ])

                let run (values: TypedExpr list) : TypedExpr =
                    { Type = listTy
                      Range = r
                      Node = TListMake values }

                // Runs of read values, and the splices between them, as
                // `inferCheckedLiteral` builds a literal with a splice in it.
                let rec pieces (taken: Choice<TypedExpr, Expr * Range> list) (run': TypedExpr list) acc =
                    match taken with
                    | [] -> List.rev (if run'.IsEmpty then acc else run (List.rev run') :: acc)
                    | Choice1Of2 value :: more -> pieces more (value :: run') acc
                    | Choice2Of2(inner, sr) :: more ->
                        let acc = if run'.IsEmpty then acc else run (List.rev run') :: acc
                        pieces more [] (spliceIntoRepeat env listTy inner sr :: acc)

                let value =
                    match pieces taken [] [] with
                    | [] -> run []
                    | [ one ] -> one
                    | several -> snd (assembleSplicedLiteral "List" elemTy several r)

                go rest left ((e.Label, value) :: kept) [ describe item ]

    go elements items [] []

/// A `,@xs` where a grammar repeats an item. The values of a list or of a vec
/// go in, and a vec is made a list first.
and private spliceIntoRepeat (env: Env) (listTy: HMType) (inner: Expr) (sr: Range) : TypedExpr =
    let innerType, typedInner = infer env inner

    match prune env.Registry innerType, prune env.Registry listTy with
    | TCon("Vec", [ elem ]), TCon("List", [ wanted ]) ->
        unifyExpecting env.Registry wanted elem "an element of this ,@"
        let fnTy, fnExpr = infer env (EResolved("vec->list", sr))
        unify env.Registry fnTy (tfun [ innerType ] listTy)

        { Type = listTy
          Range = sr
          Node = TApply(fnExpr, [ typedInner ], []) }
    | _ ->
        try
            unify env.Registry innerType listTy
        with ex when Diagnostics.isDiagnostic ex ->
            let shown = DotNetInterop.showTypesTogether [ prune env.Registry listTy; prune env.Registry innerType ]

            failwithf
                $"Type Error at %s{Lexer.formatPos sr}: ,@ puts the values of a list or a vec where the grammar repeats an item:\n  expected %s{shown[0]} or a vec of the same\n  got      %s{shown[1]}\nWrite ,x instead to place one value."

        typedInner

/// The record a production declares, from the values read for its fields.
and private buildGrammarRecord
    (env: Env)
    (key: string)
    (args: HMType list)
    (kept: (string option * TypedExpr) list)
    (r: Range)
    : TypedExpr =
    let pe = TCon(key, args)

    match Map.tryFind key env.Registry.Records with
    | None -> failwithf $"internal: the grammar record %s{key} is not registered"
    | Some(typeParams, fields) ->
        let subst =
            if typeParams.Length = args.Length then
                List.zip typeParams args |> Map.ofList
            else
                Map.empty

        let values =
            fields
            |> List.map (fun (name, fieldTy) ->
                match kept |> List.tryFind (fun (label, _) -> label = Some name) with
                | Some(_, value) ->
                    unify env.Registry (substTypeVars subst fieldTy) value.Type
                    name, value
                | None -> failwithf $"internal: no value was read for the field %s{name} of %s{key}")

        { Type = pe
          Range = r
          Node = TRecordMake values }

/// Checks a grammar where it is declared: at each choice, the next item
/// decides. Two alternatives of an `:or`, two items of an `:each`, or an
/// optional or repeated element and what can follow it, must not start with
/// the same thing. A keyword is never in conflict with a list or a symbol: it
/// has priority.
///
/// A grammar parameter starts with anything and is not checked here. The
/// grammar it is given is checked where that grammar is declared.
let checkGrammar (env: Env) (g: Grammar) : unit =
    let registry = env.Registry

    let show (s: GrammarStart) =
        match s with
        | StartKeyword kw -> $"the symbol %s{kw}"
        | StartKeywordList kw -> $"(%s{kw} ...)"
        | StartList -> "a list"
        | StartVec -> "a vec"
        | StartArray -> "an array"
        | StartAtom name -> withArticle (DotNetInterop.showType (TCon(name, [])))
        | StartSplice t -> $"a spliced %s{t}"
        | StartAny -> "anything"

    let conflict (a: Set<GrammarStart>) (b: Set<GrammarStart>) =
        Set.intersect a b |> Set.remove StartAny |> Set.toList |> List.tryHead

    for production in g.Productions do
        let key = Naming.typeKey env.CurrentModule production.Name
        let p = registry.Grammars[key]
        let inst = id
        let starts item = grammarStarts registry inst (Set.singleton key) item
        let fail (message: string) = failwithf $"Grammar Error at %s{Lexer.formatPos p.Range}: in %s{p.Name}, %s{message}"
        let describe item = describeGrammarItem registry inst item

        let checkPairs (what: string) (named: (string * GrammarItem) list) =
            let rec go (items: (string * GrammarItem) list) =
                match items with
                | [] -> ()
                | (a, ai) :: rest ->
                    for (b, bi) in rest do
                        match conflict (starts ai) (starts bi) with
                        | Some s ->
                            fail
                                $"the %s{what} %s{Naming.bareTypeName env.CurrentModule a} and %s{Naming.bareTypeName env.CurrentModule b} can both start with %s{show s}, so a literal can not say which it is. Make them start differently, for example with a keyword."
                        | None -> ()

                    go rest

            go named

        let rec checkItem (item: GrammarItem) =
            match item with
            | GrList(_, elements)
            | GrVec elements -> checkSequence elements
            | GrOpt inner -> checkItem inner
            | GrOr alternatives ->
                checkPairs "alternatives" alternatives
                alternatives |> List.iter (snd >> checkItem)
            | GrEach fields ->
                checkPairs "items" fields
                fields |> List.iter (snd >> checkItem)
            | GrType _
            | GrSpliceOnly _
            | GrKeyword _ -> ()

        and checkSequence (elements: GrammarElement list) =
            let rec go (elements: GrammarElement list) =
                match elements with
                | [] -> ()
                | e :: rest ->
                    checkItem e.Item

                    if e.Rep <> GOne || grammarCanBeEmpty e then
                        let after = sequenceStarts registry inst (Set.singleton key) rest

                        match conflict (starts e.Item) after with
                        | Some s ->
                            fail
                                $"%s{describe e.Item} can be left out or repeated, and what comes after it can also start with %s{show s}. A literal can not say where the one ends. Make them start differently, for example with a keyword."
                        | None -> ()

                    go rest

            go elements

        match p.Shape with
        | GsUnion alternatives ->
            for (case, item) in alternatives do
                match item with
                | GrType t when (match grammarOf registry t with
                                 | Some(k, _, _) -> k = key
                                 | None -> false) ->
                    fail $"the alternative %s{Naming.bareTypeName env.CurrentModule case} is %s{p.Name} itself, so reading it never ends."
                | _ -> ()

            // A spliced value goes to the alternative of its type, so two
            // alternatives may not take a spliced value of one type: `string`
            // and `,string`, say. A parameter, or a production, is checked
            // where its own alternatives are.
            let spliceType (item: GrammarItem) =
                match item with
                | GrType t
                | GrSpliceOnly t ->
                    match grammarOf registry t, prune registry t with
                    | Some _, _ -> None
                    | None, (TVar _ | TMeta _) -> None
                    | None, t -> Some t
                | _ -> None

            let rec pairs (items: (string * GrammarItem) list) =
                match items with
                | [] -> ()
                | (a, ai) :: rest ->
                    match spliceType ai with
                    | Some ta ->
                        for (b, bi) in rest do
                            match spliceType bi with
                            | Some tb when spliceFitsType registry Set.empty ta tb ->
                                fail
                                    $"the alternatives %s{Naming.bareTypeName env.CurrentModule a} and %s{Naming.bareTypeName env.CurrentModule b} can both take a spliced %s{DotNetInterop.showType ta}, so a value spliced in with , can not say which it is."
                            | _ -> ()
                    | None -> ()

                    pairs rest

            pairs alternatives
            checkItem (GrOr alternatives)
        | GsAlias item
        | GsNested item -> checkItem item
        | GsSequence elements -> checkSequence elements
        | GsChecked _ -> ()

// --- DECLARATION CHECKING ---

