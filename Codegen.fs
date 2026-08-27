module Bjolang.Codegen

open System
open System.Text
open Bjolang.TypedAST
open Bjolang.Parser

type UnionCaseInfo = {
    ParentTypeName: string
    IsDataCase: bool
}

/// The loop a `TRecur` may jump to.
type LoopScope = {
    Members: TLoopMember list
    /// Set when the group was merged into a single switch-dispatched local
    /// function because its members tail-call each other.
    Merged: bool
    /// The state discriminant of a merged group.
    StateVar: string
    /// `switch` statements entered since the group's own dispatch switch. A
    /// `goto case` binds to the *nearest* enclosing switch, so a jump from
    /// inside a nested one has to go through the discriminant instead.
    NestedSwitches: int
    /// When true, this is a flattened inlined loop. Non-recur terminals leave
    /// the `while` loop instead of returning.
    IsInlineLoop: bool
    /// A label emitted just after an inlined loop, for exits that `break`
    /// cannot express because a `switch` stands in the way.
    ExitLabel: string
    /// Set when something actually jumped to `ExitLabel`. A label C# can see no
    /// jump to is a warning, so it is only emitted once it has been used —
    /// which is known only after the body has been generated.
    ExitLabelUsed: bool ref
}

type CodegenContext = {
    Builder: StringBuilder
    IndentLevel: int
    UnionCases: Map<string, UnionCaseInfo>
    /// Visible name -> the module class holding it and the member it is
    /// spelled as there. The two names differ for an alias and for an import
    /// brought in under a modifier; an empty module means a name with no class
    /// of its own, emitted bare.
    GlobalBindings: Map<string, string * string>
    /// Where `generateExpr` may hoist statement-shaped operands to. `None` in
    /// the three contexts C# gives no statement position: optional-parameter
    /// defaults, `case ... when` guards, and switch-expression arms.
    Prelude: ResizeArray<string> option
    /// The innermost loop in scope.
    Loop: LoopScope option
    /// Type parameters the enclosing method or class already introduced.
    TypeParams: Set<string>
    /// True inside the iterator method a `seq` was emitted as. `yield` is a
    /// property of the *method* it appears in, not of the lexical form, so any
    /// construct that opens a new C# method — a lambda, a local function, a
    /// non-inlined loop member — clears this.
    InSeq: bool
    /// What the type checker ended the module with. Only the type tables are
    /// read from it: a record field or a union payload is written as an
    /// `FType`, so resolving one to the type C# names it by needs the same
    /// alias table inference resolved it against. Resolved against an empty
    /// registry a payload of alias type came out as a C# class named after the
    /// alias, which nothing declares.
    Registry: TraitRegistry
    /// The C# `where` clause a module function's CLR constraints amount to,
    /// by function name, for the functions that have any.
    ///
    /// Keyed by name, as `GlobalBindings` is, because that is the only handle a
    /// `TDefun` offers: which constraints a function carries lives on its
    /// `Scheme` in the environment, and a `CodegenContext` holds the registry
    /// but not the bindings. Rendered rather than resolved here so that the one
    /// place that knows how to spell a type is the one that spells it.
    ClrConstraints: Map<string, string>
    /// Does the enclosing C# method return `void`?
    ///
    /// Only a function whose *inferred* return type came from a statement-shaped
    /// form does — `(defun (f) (set! x 1))` with no signature. One that says
    /// `(-> ... void)` returns `Unit`, and a body ending in a void expression
    /// then has to produce a value the expression does not have. Which of the
    /// two it is cannot be read off the body, so it is carried down: like
    /// `InSeq`, it is a property of the method, and every construct opening a
    /// new one sets it afresh.
    ReturnsVoid: bool
}

let inline append (ctx: CodegenContext) (s: string) =
    ctx.Builder.Append(s) |> ignore

let inline appendLine (ctx: CodegenContext) (s: string) =
    ctx.Builder.AppendLine(s) |> ignore

let inline indent (ctx: CodegenContext) =
    ctx.Builder.Append(String(' ', ctx.IndentLevel * 4)) |> ignore

let withIndent (ctx: CodegenContext) (f: CodegenContext -> unit) =
    f { ctx with IndentLevel = ctx.IndentLevel + 1 }

/// `#line`, mapping the C# that follows back to the Bjolang that produced it.
///
/// Emitted per statement rather than per method: a directive holds until the
/// next one, so one per method would map the twentieth generated line of a body
/// to the twentieth line of the source function — usually past the end of it.
///
/// A range with no file is skipped rather than guessed at. Nothing in the
/// compiler builds one today, but a wrong `#line` is worse than none: it sends
/// a reader to a line that has nothing to do with the error.
let lineDirective (ctx: CodegenContext) (r: Lexer.Range) =
    if not (String.IsNullOrEmpty r.File) then
        let path = r.File.Replace("\\", "\\\\").Replace("\"", "\\\"")
        ctx.Builder.AppendLine($"#line {r.Start.Line} \"{path}\"") |> ignore

/// Ends the reach of the preceding `#line`.
///
/// A directive holds until the next one, so generated scaffolding that follows
/// a method — the entry point, a trait singleton — would otherwise be numbered
/// as a continuation of whatever source that method came from, and report lines
/// past the end of the file. Scaffolding has no source, and this says so.
let hiddenDirective (ctx: CodegenContext) =
    ctx.Builder.AppendLine("#line hidden") |> ignore

/// A user-facing code generation failure. A loud error at compile time beats
/// invalid generated C#, a silent wrong answer, or a stack overflow at run time.
let codegenError (where: Lexer.Range) (message: string) : 'a =
    failwithf $"Codegen Error at %s{Lexer.formatPos where}: %s{message}"

let private freshName (prefix: string) = Gensym.fresh prefix

let mapPrimitiveType (name: string) =
    match name with
    | "System.Int32" -> "int"
    | "System.Byte" -> "byte"
    | "System.Int16" -> "short"
    | "System.UInt16" -> "ushort"
    | "System.UInt32" -> "uint"
    | "System.Int64" -> "long"
    | "System.UInt64" -> "ulong"
    | "System.Double" -> "double"
    | "System.String" -> "string"
    | "System.Boolean" -> "bool"
    | "System.Void" -> "void"
    // The unit *value*. `System.Void` above is the interop void, which is not a
    // type C# can hold — see `TypeConstants.unitType`.
    | "Unit" -> "Bjoml.Unit"
    | "System.Object" -> "object"
    | "Vec" -> "Collections.RrbList"
    // Builders and cursors. Both live inside the runtime's static class, which
    // `using static` also imports the nested types of — but a declaration
    // spells the type out, so they are qualified here.
    | "ListBuilder" -> "SchemeList.SchemeListBuilder"
    | "VecCursor" -> "BjolangRuntime.VecCursor"
    | "SeqCursor" -> "BjolangRuntime.SeqCursor"
    // Fully qualified for the same reason `BjoChar` is: it lives in the
    // `Bjolang.Runtime` namespace rather than nested in the static class the
    // generated file has a `using static` for.
    | "StringCursor" -> "Bjolang.Runtime.StringCursor"
    | "StringBuilder" -> "System.Text.StringBuilder"
    | "VecBuilder" -> "Collections.RrbBuilder"
    | "List" -> "SchemeList.SchemeList"
    // A `seq` is a C# iterator, so its type is the one C# iterators produce.
    | "Seq" -> "System.Collections.Generic.IEnumerable"
    | "Option" -> "BjolangRuntime.Option"
    | "Result" -> "BjolangRuntime.Result"
    | "Param" -> "BjolangRuntime.Param"
    | "DynEnv" -> "BjolangRuntime.DynEnv"
    // The concurrency surface. A promise is what `bjo` hands back and what a
    // fiber's core already is; an event is an interface, because a `choose` of
    // two events has to be one.
    | "Promise" -> "Bjoml.Promise"
    | "Event" -> "Bjoml.IEvent"
    | "Chan" -> "Bjoml.Channel"
    // A cancellation token *is* a promise of a reason, so it needs no type of
    // its own here — the whole newtype lives in the Bjolang type system, where
    // it keeps `promise-join` and `detach` off a value neither of them means
    // anything for. Spelled with its argument already applied because the
    // Bjolang type takes none: a token is a promise of exactly one thing.
    | "CancelToken" -> "Bjoml.Promise<BjolangRuntime.CancelReason>"
    // Why a scope was cancelled. A builtin union for the same reason `Syntax`
    // is one: the runtime constructs values of it — `spawn-evt`'s nack, the
    // deadline watcher — and is compiled below anything a `def/type` could be
    // written in.
    | "CancelReason" -> "BjolangRuntime.CancelReason"
    | "AsyncSeq" -> "System.Collections.Generic.IAsyncEnumerable"
    | "Keyword" | "Bjolang.Keyword" -> "BjolangRuntime.Keyword"
    | "Symbol" | "Bjolang.Symbol" -> "BjolangRuntime.Symbol"
    // A macro's input and output. Qualified for the same reason `BjoChar` is:
    // it lives in `Bjolang.Runtime`, not in the static class the generated file
    // has a `using static` for. Its cases are seeded into `UnionCases` in
    // `generateProgram`, so patterns and construction take the ordinary union
    // paths.
    | "Syntax" -> "Bjolang.Runtime.Syntax"
    // Fully qualified: `BjoChar` lives in the `Bjolang.Runtime` namespace,
    // while `Keyword` and `Symbol` are nested in the global `BjolangRuntime`
    // static class that the generated file has a `using static` for.
    | "Char" | "Bjolang.Char" -> "Bjolang.Runtime.BjoChar"
    | _ -> name

// Promoted to `Bjolang.Naming`, which the passes that run before code
// generation also need: an inlined body has to be able to name the module a
// free variable came from.
let sanitizeIdent = Naming.sanitizeIdent

/// The C# parameter a keyword argument arrives in. Spelled by the declaration
/// and by every call site, so it has one definition.
let keywordParamName = Naming.keywordParamName

/// The C# parameter a rest argument arrives in, where the function also takes
/// keyword arguments and the array therefore has to be passed by name.
let restParamName = Naming.restParamName

/// Does this `::` name qualify a binding to the module class that defines it,
/// rather than name a method of a trait implementation?
///
/// The two shapes are spelled the same because they mean the same thing to
/// `sanitizeIdent` — reach into that class — but they disagree about what the
/// identifier's type arguments are for. A trait landing pad's belong to the
/// *class*, `Foldable_List<int>.Instance.fold`; a qualified binding's belong to
/// the *function*, and C# infers those from the arguments as it would for any
/// other call.
let private isModuleQualified (name: string) =
    match name.LastIndexOf "::" with
    | -1 -> false
    | i -> name.Substring(0, i).EndsWith "_Module"

/// The C# class a module's declarations are emitted into.
///
/// A module is named after its source file, so the name can hold characters no
/// C# identifier may hold — or start with a digit, as `006_lib.bjo` does. Every
/// site that spells this class has to agree on the answer: the class definition,
/// the `using static` for it, a qualified reference to one of its bindings, and
/// the generated entry point.
let moduleClassName = Naming.moduleClassName

/// The C# spelling of a Bjolang type parameter.
let typeParamName = Naming.typeParamName

/// The class holding a CLR-constraint trait's members, as generic methods.
///
/// Named after the trait and emitted into the module that declares it, exactly
/// as an impl class is, so an importing module reaches it the same way.
let clrHelperClassName (traitName: string) = Naming.sanitizeIdent traitName + "_Clr"

/// The canonical key a type parameter is tracked under, independent of whether
/// the source wrote it quoted.
let typeParamKey = Naming.typeParamKey



/// The C# spelling of a type constructor's name.
///
/// The single answer to "what is this type called in the generated code", so
/// that one type cannot be spelled two ways in two emitters.
///
/// A module declaring its own `Result` needs nothing special here any more: its
/// declaration is keyed by the module that wrote it, so it arrives as
/// `main__Result` and the runtime type it shadows arrives as `Result`. They are
/// two names because they are two types.
let private conBaseName (name: string) =
    let mapped = mapPrimitiveType name
    if mapped = name then sanitizeIdent name else mapped

let rec typeToString (hm: HMType) : string =
    match hm with
    | TCon ("Array", [elemType]) ->
        $"{typeToString elemType}[]"
    | TCon (name, args) ->
        let baseName = conBaseName name
        if args.IsEmpty then baseName
        else
            let argsStr = args |> List.map typeToString |> String.concat ", "
            $"%s{baseName}<%s{argsStr}>"
    | TVar name -> typeParamName name
    // A function *value*. `Action` only for the interop void — a Bjolang
    // `(-> %a void)` is `(-> %a Unit)` and comes out as `Func<T_a, Bjoml.Unit>`.
    //
    // That uniformity is the reason `Unit` exists. A generic `(-> %a %b)`
    // instantiated at `%b = Unit` has to be spelled *something*, and nothing at
    // this point knows whether `%b` will turn out to be unit — so a delegate
    // type that depends on the answer cannot be emitted at all.
    //
    // The effect is ignored because only `ESync` exists. It is the field that
    // will *not* stay ignorable: an `EAsync` arrow's C# counterpart returns
    // `Fiber<TRet>` rather than `TRet`, so it is `Func<..., Fiber<TRet>>` here,
    // and an effect *variable* has no single spelling at all — C# cannot be
    // generic over async-ness, which is why the design's §3.1 needs an
    // effect-monomorphisation pass rather than a wider delegate type.
    | TFun (args, ret, eff) ->
        let argsStr = args |> List.map typeToString |> String.concat ", "
        // A bjoroutine value hands back the state machine, so its delegate is
        // always a `Func` — `Fiber<Bjoml.Unit>` is a real type even where the
        // payload is nothing, which is the second reason the unit had to become
        // a value before any of this could work.
        let retStr =
            match eff with
            | ESync -> typeToString ret
            | _ ->
                let payload = if typeToString ret = "void" then "Bjoml.Unit" else typeToString ret
                $"Bjoml.Fiber<%s{payload}>"

        if eff = ESync && typeToString ret = "void" then
            if args.IsEmpty then "Action" else $"Action<%s{argsStr}>"
        else
            if args.IsEmpty then $"Func<%s{retStr}>" else $"Func<%s{argsStr}, %s{retStr}>"
    // `ValueTuple<>` is not a type. The zero-element tuple is the unit type, and
    // C# spells it `ValueTuple` — the non-generic struct — so it needs its own
    // case rather than falling out of the general one. Reachable without ever
    // writing `(Tuple)`: `()` parses as an empty tuple, and so does a body with
    // no forms in it.
    | TTuple [] -> "ValueTuple"
    | TTuple types ->
        let typesStr = types |> List.map typeToString |> String.concat ", "
        $"ValueTuple<%s{typesStr}>"
    | TMeta m ->
        match m.Value with
        | Some t -> typeToString t
        | None -> "object /* unresolved meta */"
    // Projected out of a type variable, an associated type is spelled as the
    // synthesized type parameter that stands for it.
    | TAssoc (_, assocName, TVar implVar) -> typeParamName (assocTypeVar implVar assocName)
    | TAssoc (traitName, assocName, TMeta { Value = Some inner }) ->
        typeToString (TAssoc(traitName, assocName, inner))
    | TAssoc (traitName, assocName, implType) ->
        "object /* unresolved assoc */"

/// A numeric literal, spelled for the type the checker gave it.
///
/// Two things happen here that did not used to. The digits are no longer
/// emitted verbatim — `21uy` is Bjolang's spelling of a byte and not C#'s, so
/// `(byte->string 21uy)` was a program that type-checked and would not compile
/// — and a literal that settled at a *type parameter* is built through the
/// implementor's own `CreateChecked`, which is the only way to write a number
/// at a type C# does not know yet.
///
/// `CreateChecked` is a member of `INumberBase`, so it is legal exactly when
/// the enclosing function carries the `Num` that `collectTraitConstraints`
/// reads off the literal. Checked rather than truncating: a literal too large
/// for the type it is used at is a mistake, and one `Inference` has already
/// refused — this is what the run-time answer would be if it had not.
let private numericLiteral (where: Lexer.Range) (t: HMType) (text: string) : string =
    match NumericLiteral.settled t with
    | TVar _ as v -> $"%s{typeToString v}.CreateChecked(%s{NumericLiteral.digits text})"
    | concrete ->
        match NumericLiteral.csharp concrete text with
        | Some spelled -> spelled
        | None -> codegenError where $"'%s{text}' is a number, and the type it reached emission at is not one."

/// Does this expression yield no C# value at all?
///
/// True for a foreign method reflected as `System.Void`, and for the
/// statement-shaped forms — `set!`, `yield` — that are given the same type. It
/// is *false* for `Unit`, which is an ordinary value: that is the whole
/// distinction, and every emission site below turns on it.
let private isVoidType (t: HMType) = typeToString t = "void"

/// What a method of this colour actually returns in C#.
///
/// A bjoroutine's declared return type is what it hands back to *its caller in
/// Bjolang*; the C# method hands back the state machine's task-like, and the
/// caller's `await` unwraps it. So the Bjolang type is the payload, not the
/// return type, and `(: fetch (-> string string))` compiles to
/// `async Bjoml.Fiber<string> fetch(string)`.
///
/// `Fiber<void>` is not a type. BjoML has a non-generic `Fiber` for exactly
/// that case, but a bjoroutine that yields nothing yields the *unit*, and
/// spelling it `Fiber<Bjoml.Unit>` keeps one shape for every colour: one
/// awaited-result rule, and one `Promise<T>` for `bjo` to hand back later.
let private returnTypeString (effect: Effect) (retType: HMType) : string =
    match effect with
    | ESync -> typeToString retType
    | EAsync ->
        let payload = if isVoidType retType then "Bjoml.Unit" else typeToString retType
        $"Bjoml.Fiber<%s{payload}>"
    | EEffVar _ ->
        // Unreachable: nothing constructs one. If it ever is reachable, this is
        // the wall §3.1 describes — C# cannot be generic over async-ness, so
        // there is no single string to return and the body has to be emitted
        // once per ground effect instead.
        failwith "Internal error: cannot emit a method generic over its effect (concurrency-design.md §3.1)"

/// The C# spelling of the unit value. One place, because it is written both
/// where a unit is returned and where one is discarded.
let private unitValue = "default(Bjoml.Unit)"

/// The C# element type of a single-argument container type such as the runtime
/// `SchemeList<T>` or `Vec<T>`.
///
/// Falls back to `object` when the type did not resolve to a one-argument
/// constructor, which keeps the emitted C# well-formed rather than propagating
/// an inference failure into codegen.
let private elementTypeString (t: HMType) =
    match t with
    | TCon (_, [ elemT ]) -> typeToString elemT
    | _ -> "object"

/// Every type variable mentioned by `t`, in source spelling.
///
/// Not built on `TypedAST.foldType`, which calls its leaf function on the `TVar`
/// itself: the `TAssoc` case here has to be recognized one level *above* that
/// `TVar`, because the name it yields is made from the projection.
let rec collectTypeVars (t: HMType) : string list =
    match t with
    | TVar name -> [ name ]
    | TFun (args, ret, _) -> (args |> List.collect collectTypeVars) @ collectTypeVars ret
    | TCon (_, args) -> args |> List.collect collectTypeVars
    | TTuple types -> types |> List.collect collectTypeVars
    | TMeta m ->
        match m.Value with
        | Some t' -> collectTypeVars t'
        | None -> []
    // The projection is itself a type parameter, not a mention of the
    // implementor: `Foldable %c`'s element type is `T_c_item`, and a local
    // function that uses it must not redeclare it.
    | TAssoc (_, assocName, TVar implVar) -> [ assocTypeVar implVar assocName ]
    | TAssoc (_, _, implType) -> collectTypeVars implType

/// What is to become of the value a block produces.
///
/// Every case but `Effect` describes a *terminal* position: once the value is
/// discharged, the block is over, and inside an inlined loop that means leaving
/// the loop. `Effect` is the other thing a block can be — one statement among
/// several, after which control simply continues — and the two must not be
/// confused. Spelling both as `Discard` made every intermediate `(println x)`
/// in a named `let` compile to `println(x); break;`.
type BlockTarget =
    | Return
    | Assign of string
    | DeclareAndAssign of string * string
    /// Terminal, but the value is thrown away.
    | Discard
    /// Not terminal: run it for its effect, then fall through to the statements
    /// that follow.
    | Effect

/// How a type constructor is spelled in published metadata.
///
/// Intentionally the inverse of only part of `Inference.typeNameMap`: an entry
/// added here changes what every `.dll` already on disk reads back as.
let private shortPrimitiveName (name: string) : string =
    match name with
    | _ when name = TypeConstants.Int32Name -> "int"
    | _ when name = TypeConstants.StringName -> "string"
    | _ when name = TypeConstants.BooleanName -> "bool"
    | _ when name = TypeConstants.VoidName -> "void"
    | _ -> name

let rec serializeHMType (t: HMType) : string =
    match t with
    | TCon (name, args) ->
        let baseName = shortPrimitiveName name
        if args.IsEmpty then baseName
        else $"(%s{baseName} " + String.concat " " (List.map serializeHMType args) + ")"
    | TVar name -> name
    // The arrow head carries the effect, so an `ESync` signature — every
    // signature that exists today — serializes exactly as it always did and
    // every `.dll` already on disk still reads back.
    | TFun (args, ret, eff) ->
        let head = arrowHead eff
        if args.IsEmpty then $"(%s{head} %s{serializeHMType ret})"
        else $"(%s{head} " + String.concat " " (List.map serializeHMType args) + $" %s{serializeHMType ret})"
    | TTuple types ->
        $"(Tuple " + String.concat " " (List.map serializeHMType types) + ")"
    | TMeta m ->
        match m.Value with
        | Some v -> serializeHMType v
        | None -> "object"
    // An unresolved associated type has to survive into the metadata as an
    // associated type. Flattening it to `object` used to make an imported
    // signature unusable: `fold`'s element type is `%item`, and `object` will
    // not unify with the `int` the caller actually has.
    | TAssoc (traitName, assocName, implType) ->
        $"(assoc %s{traitName} %s{assocName} %s{serializeHMType implType})"


/// A trait signature that mentions the implementor applied.
///
/// The hole is written as the implementor variable in applied position —
/// `('m 'a)` — which is the one thing `parseType` accepts only for a quoted
/// head, and therefore the one thing that reads back as a hole rather than as a
/// constructor named `m`.
let rec serializeTplType (implementorVar: string) (t: TplType) : string =
    let go = serializeTplType implementorVar

    match t with
    | TplCon(name, args) ->
        let baseName = shortPrimitiveName name

        if args.IsEmpty then baseName
        else $"(%s{baseName} " + String.concat " " (List.map go args) + ")"
    | TplVar name -> name
    | TplFun(args, ret, eff) ->
        let head = arrowHead eff
        if args.IsEmpty then $"(%s{head} %s{go ret})"
        else $"(%s{head} " + String.concat " " (List.map go args) + $" %s{go ret})"
    | TplTuple types -> "(Tuple " + String.concat " " (List.map go types) + ")"
    | TplHole args ->
        "('" + implementorVar.TrimStart('\'') + " " + String.concat " " (List.map go args) + ")"

let rec serializeFType (ft: Parser.FType) : string =
    match ft with
    | Parser.TName(n, _) -> n
    | Parser.TApp(n, args, _) -> $"({n} " + String.concat " " (List.map serializeFType args) + ")"
    | Parser.TArrow(mandatory, keywords, restOpt, ret, colour, _) ->
        let mandatoryStrs = mandatory |> List.map serializeFType
        let keywordStrs = keywords |> List.map (fun (n, t) -> $"(#:{n} {serializeFType t})")
        let restStrs = match restOpt with Some t -> [$"#:rest {serializeFType t}"] | None -> []
        let allParts = mandatoryStrs @ keywordStrs @ restStrs @ [serializeFType ret]
        // The head carries the colour, and it is the only place it can: an
        // importing module has no definition to read it off.
        "(" + arrowHead (colourEffect colour) + " " + String.concat " " allParts + ")"

// ---------------------------------------------------------------------------
// Untyped expressions, for inline templates
// ---------------------------------------------------------------------------

/// What has to survive a round trip through the reader.
///
/// The metadata string is escaped again on its way into a C# attribute, so
/// backslashes have to be doubled *here* as well as there; escaping only quotes
/// used to turn `\"` into `\\"`, which closes the C# literal.
let private escapeSexpr (s: string) =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t")

let rec serializePattern (p: Parser.Pattern) : string =
    match p with
    | Parser.PWildcard _ -> "_"
    | Parser.PIdent(n, _) -> n
    | Parser.PInt(v, _) -> v
    | Parser.PString(v, _) -> "\"" + escapeSexpr v + "\""
    // Always the hex spelling: it round-trips through the lexer for every
    // codepoint, including ones with no name and ones that are not printable.
    | Parser.PChar(c, _) -> $"#\\x%X{c}"
    | Parser.PBool(b, _) -> if b then "#t" else "#f"
    | Parser.PKeyword(k, _) -> "#:" + k
    | Parser.PQuotedSymbol(s, _) -> "'" + s
    // Always parenthesized, even with no arguments. A bare name reads back as a
    // constructor only when it happens to start with a capital, and that is not
    // something to rely on.
    | Parser.PConstruct(n, args, _) ->
        "(" + String.concat " " (n :: List.map serializePattern args) + ")"
    | Parser.PList(items, tailOpt, _) -> serializeSeqPattern "List" items tailOpt
    | Parser.PVec(items, tailOpt, _) -> serializeSeqPattern "Vec" items tailOpt
    | Parser.PTuple(items, _) -> "(" + String.concat " " ("Tuple" :: List.map serializePattern items) + ")"
    | Parser.PTypeTest(t, binder, _) ->
        "(:is " + String.concat " " (t :: Option.toList binder) + ")"

and private serializeSeqPattern (head: string) items tailOpt =
    let itemStrs = items |> List.map serializePattern
    let tailStrs =
        match tailOpt with
        | Some t -> [ serializePattern t; "..." ]
        | None -> []
    "(" + String.concat " " (head :: (itemStrs @ tailStrs)) + ")"

/// Writes an untyped expression as source the reader accepts again.
///
/// The *untyped* expression is what an inline template stores: `HMType` is full
/// of mutable metavariable cells that mean nothing outside the compilation that
/// made them, and re-inferring the body at the call site is exactly what gives
/// the method a type its trait signature could not express.
/// A local function's parameters, refused if they are anything a `(fun ...)`
/// cannot say.
///
/// A keyword parameter is a calling convention and a spliced body carries no
/// call to hold it. Refusing here rather than emitting something lossy is what
/// keeps the round trip honest: `isSerializableTemplate` catches this, the
/// template is simply not published, and the landing pad — always emitted —
/// answers instead.
let private serializableParams (args: Parser.DefunArg list) : string list =
    let names = Parser.mandatoryNames args

    if names.Length <> args.Length then
        failwith "an inline template body may not contain a local function with keyword or rest parameters"

    names

/// Writes an untyped expression as source the reader accepts again.
///
/// The *untyped* expression is what an inline template stores: `HMType` is full
/// of mutable metavariable cells that mean nothing outside the compilation that
/// made them, and re-inferring the body at the call site is exactly what gives
/// the method a type its trait signature could not express.
let rec serializeExpr (e: Parser.Expr) : string =
    let list (parts: string list) = "(" + String.concat " " parts + ")"

    match e with
    | Parser.EInt(v, _) -> v
    | Parser.EString(v, _) -> "\"" + escapeSexpr v + "\""
    | Parser.EChar(c, _) -> $"#\\x%X{c}"
    | Parser.EBool(b, _) -> if b then "#t" else "#f"
    | Parser.EQuotedSymbol(s, _) -> "'" + s
    | Parser.EKeyword(k, _) -> "#:" + k
    | Parser.EIdent(n, _) -> n
    | Parser.ETuple(items, _) -> list ("Tuple" :: List.map serializeExpr items)
    | Parser.EApp(target, args, _) -> list (serializeExpr target :: List.map serializeExpr args)
    | Parser.ECast(t, v, _) -> list [ "cast"; serializeFType t; serializeExpr v ]

    // Round-trips as its own form: re-importing it as a plain `let` would put
    // the generalization back, which is the whole thing it exists to prevent.
    | Parser.ELetMono(n, value, body, _) ->
        list [ "let/mono"; n; serializeExpr value; serializeExpr body ]

    | Parser.ELet(n, isFun, args, ann, value, body, _) ->
        let valueStr =
            if isFun then list [ "fun"; list (serializableParams args); serializeExpr value ]
            else serializeExpr value

        let annotated =
            match ann with
            | Some t -> list [ "cast"; serializeFType t; valueStr ]
            | None -> valueStr

        // One binding per `let`, which is now load-bearing rather than merely
        // tidy: a `let` binds simultaneously, so a group of them would be read
        // back with a different scope than the nest it was written from. A
        // single binding means the same thing under either reading.
        list [ "let"; list [ list [ n; annotated ] ]; serializeExpr body ]

    | Parser.ELetRec(bindings, body, _) ->
        // A body block: consecutive `def`/`defun` forms are collected back into
        // one mutually-recursive group by the reader.
        let defs =
            bindings
            |> List.map (fun (n, isFun, args, _, value) ->
                if isFun then list [ "defun"; list (n :: serializableParams args); serializeExpr value ]
                else list [ "def"; n; serializeExpr value ])

        list ([ "let"; "()" ] @ defs @ [ serializeExpr body ])

    | Parser.ELetMutable(n, _, value, body, _) ->
        list [ "let"; "()"; list [ "def/mutable"; n; serializeExpr value ]; serializeExpr body ]

    | Parser.ESet(n, v, _) -> list [ "set!"; n; serializeExpr v ]
    | Parser.EIf(c, t, f, _) -> list [ "if"; serializeExpr c; serializeExpr t; serializeExpr f ]
    | Parser.EWhen(c, b, negated, _) ->
        list [ (if negated then "unless" else "when"); serializeExpr c; serializeExpr b ]
    | Parser.EFun(args, body, colour, _) ->
        let head = match colour with Parser.Suspending -> "bjoroutine" | Parser.Ordinary -> "fun"
        list [ head; list args; serializeExpr body ]
    | Parser.ERecordUpdate(n, fields, _) ->
        list ("record-set" :: n :: (fields |> List.map (fun (k, v) -> list [ k; serializeExpr v ])))
    | Parser.ERecordSet(n, fields, _) ->
        list ("record-set!" :: n :: (fields |> List.map (fun (k, v) -> list [ k; serializeExpr v ])))
    | Parser.EGetField(target, f, _) -> list [ "record-ref"; serializeExpr target; f ]
    | Parser.EVec(items, _) -> "[" + String.concat " " (List.map serializeExpr items) + "]"

    | Parser.EMatch(target, clauses, _) ->
        let clauseStrs =
            clauses
            |> List.map (fun (pat, guard, body) ->
                match guard with
                | Some g -> list [ serializePattern pat; "#:when"; serializeExpr g; serializeExpr body ]
                | None -> list [ serializePattern pat; serializeExpr body ])

        list ("match" :: serializeExpr target :: clauseStrs)

    | Parser.ESeq(body, _) -> list [ "seq"; serializeExpr body ]
    | Parser.EBjo(body, _) -> list [ "bjo"; serializeExpr body ]
    | Parser.ETaskEvent(body, _) -> list [ "task->event"; serializeExpr body ]
    | Parser.EYield(v, _) -> list [ "yield"; serializeExpr v ]
    | Parser.EYieldFrom(s, _) -> list [ "yield-from"; serializeExpr s ]

    // No reader form produces these, so none can appear in a template body.
    | Parser.ELetTuple _ -> failwith "an inline template body may not destructure a tuple binding"
    | Parser.EList _ -> failwith "an inline template body may not contain a bare list literal"
    | Parser.ETryFinally _ -> failwith "an inline template body may not contain try/finally"
    | Parser.ETryCatch _ -> failwith "an inline template body may not contain try/catch"

/// Can this body be written out and read back at all? A template that cannot be
/// serialized is simply not exported; its landing pad still is.
let isSerializableTemplate (e: Parser.Expr) : bool =
    try
        serializeExpr e |> ignore
        true
    with _ ->
        false

let getUnionTypeString (hm: HMType) (parentName: string) : string =
    let rec findCon t =
        match t with
        | TCon(n, args) when n = parentName -> Some (n, args)
        | TFun(_, ret, _) -> findCon ret
        | TMeta m ->
            match m.Value with
            | Some t' -> findCon t'
            | None -> None
        | _ -> None
    match findCon hm with
    | Some (n, args) ->
        let baseName = conBaseName n
        if args.IsEmpty then baseName
        else
            let argsStr = args |> List.map typeToString |> String.concat ", "
            $"%s{baseName}<%s{argsStr}>"
    | None ->
        sanitizeIdent parentName

/// Backslashes are doubled *first*, for the same reason `escapeAttribute` does
/// it first: escaping only the quotes turns a `\` the source already had into
/// the start of an escape sequence C# does not recognize, and `"a\c"` emits
/// source that does not parse.
let escapeStringLiteral (s: string) =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")

/// A pattern that cannot fail.
///
/// A tuple counts when its parts do: a tuple has exactly one shape, so there is
/// nothing for the pattern to fail against. That is not a nicety — C# proves the
/// same thing, and rejects the fallback arm of a switch expression whose arms
/// already cover the type (CS8510). Anything else may fail: a list pattern can
/// meet `Nil`, a vector pattern a shorter vector, a constructor pattern another
/// case of its union.
let rec private isIrrefutablePattern (p: TypedPattern) =
    match p.Node with
    | TPWildcard
    | TPIdent _ -> true
    | TPTuple items -> items |> List.forall isIrrefutablePattern
    | _ -> false

/// A clause that matches unconditionally; anything after it is dead code.
let private isIrrefutable (c: TMatchClause) =
    c.Guard.IsNone && isIrrefutablePattern c.Pattern

/// Whether an irrefutable clause can be *moved into* a `default:` section.
///
/// A `default:` carries no pattern, so it can only stand in for a clause with
/// nothing to bind or a single name to alias. An irrefutable tuple has parts to
/// destructure, so it stays an ordinary `case` and the `default:` after it keeps
/// its unreachable throw — which a switch *statement*, unlike a switch
/// expression, is content to carry and which the definite-assignment analysis
/// still wants.
let private fitsDefaultSection (c: TMatchClause) =
    isIrrefutable c
    && (match c.Pattern.Node with
        | TPWildcard
        | TPIdent _ -> true
        | _ -> false)

/// Bjolang matches are first-match-wins. C# rejects arms it can prove are
/// unreachable (CS8510), so drop everything following the first irrefutable clause.
let private liveClauses (clauses: TMatchClause list) =
    let rec take acc remaining =
        match remaining with
        | [] -> List.rev acc
        | c :: rest -> if isIrrefutable c then List.rev (c :: acc) else take (c :: acc) rest
    take [] clauses

// ---------------------------------------------------------------------------
// Keyword defaults
// ---------------------------------------------------------------------------

/// The C# constant a keyword parameter's default can be written as, when it can
/// be written as one at all.
///
/// A keyword default is in general an arbitrary expression, evaluated in the
/// callee on every call that omits the argument — see `generateArgumentPrologue`
/// for the machinery that costs. When the default is a literal, none of that is
/// needed: C# can carry the value in the signature itself, and the parameter
/// becomes an ordinary optional one that a keyword-free call does no work for.
///
/// Both halves of the answer matter, which is why the *type* is consulted and
/// not only the node:
///
///   - A C# optional parameter's default must be a constant expression. `1.5`
///     and `"n="` are; `new Bjolang.Runtime.BjoChar(32)` and
///     `BjolangRuntime.Keyword.Intern("k")` are how `char` and a keyword are
///     emitted, and are not. Nor is `@true` — the runtime spells the boolean
///     literals as static fields — so `true` and `false` are emitted here
///     directly rather than through `generateExpr`.
///   - A type *parameter* admits no constant default but `default`, so a
///     generic keyword slot is never eligible however literal its default
///     looks.
///
/// One thing does change, and is the price of the whole optimization: a C#
/// optional parameter's default is baked into the *call site* by the C#
/// compiler, so a library that changes a default value only reaches callers
/// that are recompiled. Bjolang rebuilds a dependent whenever its dependency's
/// `.dll` is newer — `Pipeline.ensureLibrary` — so a changed default cannot
/// outlive the build that changed it. Nothing else is observable: the value is
/// a constant, so evaluating it at the call site and evaluating it in the
/// callee cannot be told apart.
let private csharpConstantDefault (kwType: HMType) (kwDefault: TypedExpr) : string option =
    match kwDefault.Node, typeToString kwType with
    // Spelled for its type rather than emitted verbatim — see `numericLiteral`
    // above, which is the same table. A parenthesised cast is still a constant
    // expression, so `((byte)21)` is a legal default. The types are listed
    // rather than defaulted to eligible because `mapPrimitiveType` is free to
    // grow a case that a bare numeral is not a constant of.
    | TInt text, ("int" | "byte" | "short" | "ushort" | "uint" | "long" | "ulong" | "double") ->
        NumericLiteral.csharp kwType text
    | TString value, "string" -> Some $"\"%s{escapeStringLiteral value}\""
    | TBool b, "bool" -> Some(if b then "true" else "false")
    | _ -> None

/// Which of a keyword-taking function's two entry points is being emitted.
///
/// A default that is not a constant has to be evaluated somewhere, and the
/// general entry evaluates it in a branch: the argument arrives as an `Option`,
/// and the callee asks whether it was there. A call that passes no keywords at
/// all pays for a question it already knows the answer to.
///
/// So such a function is emitted twice, as two C# overloads of one name. The
/// second takes no keyword parameters and binds every default outright. C# then
/// picks between them with no help from anyone: an overload all of whose
/// parameters have arguments beats one that has to substitute a default, so a
/// keyword-free call selects the keyword-free entry — including across an
/// assembly boundary, where the caller cannot see the defaults at all and does
/// not need to. Call sites are emitted exactly as they were.
type private KeywordEntry =
    /// Keyword parameters in the signature; a default is evaluated only for an
    /// argument this call left out.
    | KeywordParameters
    /// No keyword parameters at all; every default is bound unconditionally.
    | KeywordDefaultsOnly

/// Whether a keyword-free entry point is worth emitting for these parameters.
///
/// Only a default that is *not* a constant costs anything to leave out: a
/// constant one is already carried in the signature, so the general entry is
/// the fast one and a second copy of the body would buy nothing.
let private needsKeywordFreeEntry (kwArgs: (string * HMType * TypedExpr) list) : bool =
    kwArgs |> List.exists (fun (_, kwType, kwDefault) -> (csharpConstantDefault kwType kwDefault).IsNone)

// ---------------------------------------------------------------------------
// Call shape
// ---------------------------------------------------------------------------

/// The callee's flat parameter types, seen through whatever metavariables
/// inference left in the way.
let rec private funArgTypes (t: HMType) : HMType list option =
    match t with
    | TFun (argTypes, _, _) -> Some argTypes
    | TMeta { Value = Some inner } -> funArgTypes inner
    | _ -> None

/// Whether the callee's last parameter is the array a `#:rest` resolves to.
///
/// Read off the flat type, because that is all a call site has: `FunMeta`
/// records which parameters are really keyword and rest ones, and it does not
/// reach code generation. A function whose last *mandatory* parameter happens
/// to be an array is therefore indistinguishable from one with a rest
/// parameter — a pre-existing limit of reading the shape off the type, and
/// harmless while such a function has no keyword parameters, which is the only
/// case where the two are treated differently.
let private calleeHasRest (target: TypedExpr) : bool =
    match funArgTypes target.Type with
    | Some argTypes when not argTypes.IsEmpty ->
        match List.last argTypes with
        | TCon ("Array", _) -> true
        | _ -> false
    | _ -> false

/// Whether the callee *declares* keyword parameters — which is a different
/// question from whether this call supplies any, and the one that decides how
/// the call has to be written.
///
/// The flat type is mandatory ++ keyword ++ rest?, and a call's positional
/// arguments are mandatory ++ the rest array, so whatever the type has over the
/// arguments is the keyword slots.
let private calleeDeclaresKeywords (target: TypedExpr) (args: TypedExpr list) : bool =
    match funArgTypes target.Type with
    | Some argTypes -> argTypes.Length > args.Length
    | None -> false

// ---------------------------------------------------------------------------
// Statement shape
// ---------------------------------------------------------------------------

/// True when *this node* has no C# expression form and `generateExpr` therefore
/// has to hoist it into a preceding statement.
///
/// A node whose operands merely *contain* something statement-shaped is not
/// itself statement-shaped: those operands are hoisted individually, which keeps
/// the node an expression.
let rec isStatementShaped (expr: TypedExpr) : bool =
    match expr.Node with
    | TLet _
    | TLetRec _
    | TLetTuple _
    | TLetMutable _
    | TSet _
    // A write to a record's field. Void like `set!`, and with more than one
    // field it is several statements rather than one expression anyway.
    | TRecordSet _
    // A `#:set` import. C# does have an assignment *expression*, but its value
    // is the value assigned and Bjolang says the form is void — so it is
    // emitted where `set!` is, for the reason `set!` is.
    | TDotPropertySet _
    | TForeignStaticSet _
    | TThrow _
    | TTryFinally _
    | TVecMake _
    | TLoop _
    | TRecur _
    // A C# iterator is a *method*: the body has to be emitted as one, and this
    // node's value is a call to it.
    | TSeq _
    // `bjo` binds each operand to a local of its own before spawning, so that
    // the operands are evaluated here rather than in the child. Those bindings
    // need a statement position, which is what this asks for.
    | TBjo _
    // `task->event` does the same, and needs it more: its lambda is called at
    // every sync, and a `guard` may sync the same event twice. An argument
    // emitted inside the lambda would be evaluated again each time.
    | TTaskEvent _
    | TYield _
    | TYieldFrom _ -> true

    // A conditional stays `c ? t : f` as long as it yields a value and neither
    // arm needs statements. Hoisting out of an arm would evaluate it
    // unconditionally, so an arm that needs a statement forces the whole node
    // into an `if`. The condition is evaluated unconditionally, so whatever it
    // hoists can safely go ahead of the conditional.
    | TIf (_, t, f) -> isVoidType expr.Type || containsHoist t || containsHoist f

    // One-armed and void: there is no C# expression with that shape.
    | TWhen _ -> true

    // A `switch` expression cannot yield void, cannot contain the `continue` or
    // `goto` a jump compiles to, and gives its arms and guards no statement
    // position of their own.
    | TMatch (_, clauses) ->
        isVoidType expr.Type
        || liveClauses clauses
           |> List.exists (fun c ->
               containsHoist c.Body
               || (c.Guard |> Option.map containsHoist |> Option.defaultValue false))

    | _ -> false

/// True when evaluating `expr` will hoist statements into the enclosing
/// statement position — which moves that work earlier than the expression it
/// came from.
///
/// A lambda body is a block of its own, so nothing inside one can need a
/// statement position out here.
and containsHoist (expr: TypedExpr) : bool =
    isStatementShaped expr
    || match expr.Node with
       // Both open a block of their own, so nothing inside can need a statement
       // position out here.
       | TLambda _
       | TSeq _ -> false
       // So does a guarded call: it is emitted as an immediately invoked
       // lambda, and everything it needs a statement for goes inside that.
       | TTryCatch _ -> false
       | TNewObject (_, _, Some meta) when not meta.Exceptions.IsEmpty -> false
       | TForeignStaticCall (_, _, _, Some meta) when not meta.Exceptions.IsEmpty -> false
       | TDotMethodCall (_, _, _, Some meta) when not meta.Exceptions.IsEmpty -> false
       | _ -> TypeVisitor.children expr |> List.exists containsHoist

/// How the ambient cancellation token reaches a .NET call.
///
/// §7.2's second rule: nearly every async BCL method takes a
/// `CancellationToken` as a trailing parameter, and the emitter fills it in
/// rather than the caller. Written once here because the plain and the guarded
/// emission paths both need it and must agree.
let private ambientTokenArgument = "BjolangRuntime.AmbientCancellation()"

/// The `<...>` a generic foreign call is written with, or nothing.
///
/// Always written out when there is one, never left to C#'s own inference. The
/// call was *typed* against this instantiation during inference, and generated
/// code naming a method resolves it a second time — so spelling the arguments
/// is what keeps the second answer equal to the first, which is the same reason
/// a widened argument is emitted with its cast. It is also the only thing that
/// can make a call C# could not infer at all compile: a nullary `Empty<T>()`
/// takes its argument from the context, and the context is Bjolang's.
let private foreignTypeArguments (meta: DotNetMethodMetadata option) =
    match meta with
    | Some m when not m.TypeArguments.IsEmpty ->
        "<" + (m.TypeArguments |> List.map typeToString |> String.concat ", ") + ">"
    | _ -> ""

/// Does evaluating this expression *in the member it is written in* reach an
/// `await`?
///
/// Only used to decide whether a guarded region — `#:exceptions`, or a `(try
/// ...)` — has to become an async lambda rather than a plain one. A `Func<R>`
/// body cannot contain an await, and a `Func<Fiber<R>>` body can.
///
/// The sub-member cases answer `false` without looking inside, and that is
/// exact rather than approximate: `ColourCheck` has already rejected an await
/// in any of them, so a program that reaches the emitter has none to find.
/// `bjo` is the one shape where the distinction is live — its operands are
/// evaluated here and its call is not.
let rec private containsAwait (expr: TypedExpr) : bool =
    match expr.Node with
    | TLambda _
    | TSeq _ -> false
    | TBjo body ->
        match body.Node with
        | TApply (target, args, kwArgs) ->
            containsAwait target
            || List.exists containsAwait args
            || kwArgs |> List.exists (snd >> containsAwait)
        | _ -> false
    | TForeignStaticCall (_, _, _, Some meta) when meta.Await -> true
    | TDotMethodCall (_, _, _, Some meta) when meta.Await -> true
    | TApply (target, _, _) when (match target.Type with
                                  | TFun (_, _, EAsync) -> true
                                  | _ -> false) -> true
    | _ -> TypeVisitor.children expr |> List.exists containsAwait

/// Translates a typed pattern into C# pattern syntax.
let rec generatePattern (ctx: CodegenContext) (pat: TypedPattern) : unit =
    match pat.Node with
    | TPWildcard -> append ctx "_"
    | TPIdent name -> append ctx $"var {sanitizeIdent name}"
    // A constant pattern, spelled for the scrutinee's type like any other
    // literal. `((byte)21)` is still a constant expression, which is all a
    // `case` label asks of it.
    | TPInt value -> append ctx (numericLiteral pat.Range pat.Type value)
    | TPString value -> append ctx $"\"%s{escapeStringLiteral value}\""
    // A property pattern rather than a constant: `BjoChar` is a record struct,
    // and C# has no literal syntax for one.
    | TPChar c -> append ctx $"Bjolang.Runtime.BjoChar {{ Value: %d{c} }}"
    | TPBool b -> append ctx (if b then "true" else "false")
    | TPKeyword k -> append ctx $"BjolangRuntime.Keyword {{ Name: \"{escapeStringLiteral k}\" }}"
    | TPSymbol s -> append ctx $"BjolangRuntime.Symbol {{ Name: \"{escapeStringLiteral s}\" }}"
    // `Option` is the runtime's `Option<T>` struct — a flag and a value rather
    // than a pair of subclasses — so its constructors match as property
    // patterns. The type is left off: the scrutinee already has it, and a type
    // pattern naming a struct's own type reads as a tautology.
    | TPConstruct ("None", _) -> append ctx "{ Tag: 0 }"
    | TPConstruct ("Some", [ inner ]) ->
        append ctx "{ Tag: 1, Value: "
        generatePattern ctx inner
        append ctx " }"

    // The built-in `Result` is a struct carrying a tag and both payloads, so its
    // constructors match as property patterns exactly as `Option`'s do. Guarded
    // on the case *not* being a declared one: a module with a `Result` union of
    // its own matches it as the ordinary union it is.
    | TPConstruct (("Ok" | "Err") as name, args) when not (Map.containsKey name ctx.UnionCases) ->
        let tag, field = if name = "Ok" then 1, "OkValue" else 0, "ErrValue"

        match args with
        | [ inner ] ->
            append ctx $"{{ Tag: {tag}, {field}: "
            generatePattern ctx inner
            append ctx " }"
        | _ -> append ctx $"{{ Tag: {tag} }}"

    // A .NET type test compiles to the C# type pattern it is: the name, and a
    // designation when the source asked for one.
    | TPTypeTest (clrName, binder) ->
        append ctx clrName

        match binder with
        | Some n -> append ctx $" %s{sanitizeIdent n}"
        | None -> ()

    | TPConstruct (name, args) ->
        // Cons/Nil are now builtins backed by SchemeList.Cons<T>/SchemeList.Nil<T>,
        // not union cases, so they need special-case pattern generation.
        let caseTypeStr =
            match name with
            | "Cons" ->
                let elemTypeStr = elementTypeString pat.Type
                $"SchemeList.Cons<%s{elemTypeStr}>"
            | "Nil" ->
                let elemTypeStr = elementTypeString pat.Type
                $"SchemeList.Nil<%s{elemTypeStr}>"
            | _ ->
                match Map.tryFind name ctx.UnionCases with
                | Some info -> $"{getUnionTypeString pat.Type info.ParentTypeName}.{sanitizeIdent name}"
                | None -> $"{typeToString pat.Type}.{sanitizeIdent name}"
        append ctx caseTypeStr
        // A positional record with an empty parameter list gets no Deconstruct
        // method, so nullary cases must be emitted as a bare type pattern.
        if not args.IsEmpty then
            append ctx "("
            for i, argPat in List.indexed args do
                if i > 0 then append ctx ", "
                generatePattern ctx argPat
            append ctx ")"
    | TPList (items, tailOpt) ->
        // Lists are backed by SchemeList.SchemeList<T>. Desugar into nested
        // type patterns against the runtime Cons<T>/Nil<T> classes.
        let elemTypeStr = elementTypeString pat.Type
        let listTypeStr = typeToString pat.Type
        let rec desugar elements =
            match elements with
            | [] ->
                match tailOpt with
                | Some t -> generatePattern ctx t
                | None -> append ctx $"SchemeList.Nil<%s{elemTypeStr}>"
            | head :: rest ->
                append ctx $"SchemeList.Cons<%s{elemTypeStr}>("
                generatePattern ctx head
                append ctx ", "
                desugar rest
                append ctx ")"
        desugar items
    | TPVec (items, tailOpt) ->
        // Vec is backed by Collections.RrbList<T>, which is countable, indexable
        // and sliceable, so C# list patterns apply directly. A rest pattern
        // becomes a slice pattern, whose value Slice() hands back as an RrbList<T>.
        append ctx "["
        for i, item in List.indexed items do
            if i > 0 then append ctx ", "
            generatePattern ctx item
        match tailOpt with
        | Some t ->
            if not items.IsEmpty then append ctx ", "
            append ctx ".. "
            generatePattern ctx t
        | None -> ()
        append ctx "]"
    | TPTuple items ->
        match items with
        | [] -> append ctx "default(ValueTuple)"
        | [ single ] ->
            append ctx $"ValueTuple<%s{typeToString single.Type}> {{ Item1: "
            generatePattern ctx single
            append ctx " }"
        | _ ->
            append ctx "("
            for i, item in List.indexed items do
                if i > 0 then append ctx ", "
                generatePattern ctx item
            append ctx ")"
    | TPAs _ ->
        failwithf $"'as' patterns have no C# equivalent (line %d{pat.Range.Start.Line})"
    | TPApp _ ->
        failwithf $"Applied patterns are not supported by the C# backend (line %d{pat.Range.Start.Line})"

// ---------------------------------------------------------------------------
// Operators
// ---------------------------------------------------------------------------

/// The binary operators that yield a value, as Bjolang name -> C# spelling.
///
/// The comparisons are not here: they yield `bool`, so `castPromoted` below has
/// nothing to undo for them.
let private infixOperators =
    Map [ "+", "+"
          "-", "-"
          "*", "*"
          "/", "/"
          "%", "%"
          "bitwise-and", "&"
          "bitwise-ior", "|"
          "bitwise-xor", "^"
          "shift-left", "<<"
          "shift-right", ">>"
          "shift-right-logical", ">>>" ]

/// Does C# widen this type to `int` before applying an operator to it?
///
/// A solved metavariable is followed rather than pruned: `prune` wants a trait
/// registry, which nothing at emission time has, and the answer is the same.
let rec private promotesToInt (t: HMType) =
    match t with
    | TCon((TypeConstants.ByteName | TypeConstants.Int16Name | TypeConstants.UInt16Name), []) -> true
    | TMeta { Value = Some inner } -> promotesToInt inner
    | _ -> false

/// Emits `body`, cast back to `t` where C# promoted the operands out of it.
///
/// `-(byte)5` and `(byte)5 & (byte)3` are both `int` in C#, but Bjolang has
/// typed the expression `byte`, and without the cast the generated code would
/// not compile. `unchecked` because narrowing a result back into a smaller type
/// has to wrap rather than throw.
let private castPromoted (ctx: CodegenContext) (t: HMType) (body: unit -> unit) =
    if promotesToInt t then
        append ctx $"unchecked((%s{typeToString t})("
        body ()
        append ctx "))"
    else
        body ()

// ---------------------------------------------------------------------------
// Expressions and statements
// ---------------------------------------------------------------------------

/// Emits a parameter list shared by module functions and trait-`impl` methods.
///
/// A keyword parameter is emitted one of two ways. A default C# can carry as a
/// constant becomes an ordinary optional parameter of the declared type, and a
/// call that omits it costs nothing at all; anything else arrives as an
/// `Option`, so that the callee can tell an omitted argument from one passed
/// explicitly at the default value and evaluate the default expression itself.
/// `csharpConstantDefault` decides which, and `generateArgumentPrologue` emits
/// the matching half of the body.
let private generateParameterList
    (ctx: CodegenContext)
    (ownerName: string)
    (args: (string * HMType) list)
    (kwArgs: (string * HMType * TypedExpr) list)
    (restArg: (string * HMType) option)
    (entry: KeywordEntry)
    : unit =

    let mutable paramIdx = 0

    for (argName, argType) in args do
        if paramIdx > 0 then append ctx ", "
        append ctx (typeToString argType)
        append ctx " "
        append ctx (sanitizeIdent argName)
        paramIdx <- paramIdx + 1

    // The keyword-free entry has none of these: that is what it is for.
    if entry = KeywordParameters then
        for (kwName, kwType, kwDefault) in kwArgs do
            if paramIdx > 0 then append ctx ", "
            // The parameter is named the same either way: the name is the
            // calling convention, and a caller in another assembly picks its
            // spelling out of the `.dll` without knowing which lowering was
            // used. A bare value under that name binds to both — to the
            // `Option` through the runtime's implicit conversion — so no call
            // site has to know either.
            match csharpConstantDefault kwType kwDefault with
            | Some constant ->
                append ctx $"%s{typeToString kwType} "
                append ctx (keywordParamName kwName)
                append ctx $" = %s{constant}"
            | None ->
                append ctx $"BjolangRuntime.Option<{typeToString kwType}> "
                append ctx (keywordParamName kwName)
                append ctx " = default"
            paramIdx <- paramIdx + 1

    match restArg with
    | Some (restName, restElemType) ->
        if paramIdx > 0 then append ctx ", "
        // Beside keyword parameters the array is passed by name, so it is
        // declared under the one the call site knows; `generateArgumentPrologue`
        // binds it back to the name the body wrote. On its own it keeps that
        // name outright, and is passed positionally as it always was.
        //
        // This asks what the *function* declares, not what this entry point
        // takes: the keyword-free entry has no keyword parameters but is
        // reached by the same call, written the same way, so it has to answer
        // to the same name for the array.
        let declaredName =
            if kwArgs.IsEmpty then sanitizeIdent restName else restParamName
        append ctx $"params %s{typeToString restElemType}[] %s{declaredName}"
    | None -> ()

let rec generateExpr (ctx: CodegenContext) (expr: TypedExpr) : unit =
    match ctx.Prelude with
    | Some prelude when isStatementShaped expr -> append ctx (hoistToTemp ctx prelude expr)
    | None when containsHoist expr ->
        codegenError
            expr.Range
            "this expression needs statements to evaluate, but it appears where C# has no statement position"
    | _ ->

    match expr.Node with
    | TInt i -> append ctx (numericLiteral expr.Range expr.Type i)
    | TString s -> append ctx $"\"%s{escapeStringLiteral s}\""
    | TChar c -> append ctx $"new Bjolang.Runtime.BjoChar(%d{c})"
    | TBool b -> append ctx (if b then "true" else "false")
    | TKeyword k -> append ctx $"BjolangRuntime.Keyword.Intern(\"{escapeStringLiteral k}\")"
    | TSymbol s -> append ctx $"BjolangRuntime.Symbol.Intern(\"{escapeStringLiteral s}\")"
    // A dictionary singleton: "Foldable_Vec::Instance" with the impl class's own
    // type arguments. `Lowering` produces these when it passes a dictionary to a
    // constrained function, and the class is generic whenever the implemented
    // type is (`Foldable_Vec<T_a>`), so the arguments cannot be dropped.
    | TIdent (name, tArgs) when name.Contains("::") && not tArgs.IsEmpty && not (isModuleQualified name) ->
        let parts = name.Split("::")
        let tyArgsStr = tArgs |> List.map typeToString |> String.concat ", "
        append ctx (sanitizeIdent parts[0])
        append ctx $"<%s{tyArgsStr}>"
        for part in parts[1..] do
            append ctx "."
            append ctx (sanitizeIdent part)
    | TIdent (name, _) ->
        // Cons/Nil are now builtins backed by SchemeList, not union cases.
        match name with
        | "Nil" ->
            let elemTypeStr = elementTypeString expr.Type
            append ctx $"Nil<%s{elemTypeStr}>()"
        // Like `Nil`, a nullary constructor rather than a bare name: written
        // plain it would be a method group.
        | "None" ->
            let elemTypeStr = elementTypeString expr.Type
            append ctx $"None<%s{elemTypeStr}>()"
        | "Cons" ->
            match expr.Type with
            | TFun (argTypes, _, _) ->
                // First-class function value: emit a lambda.
                let argsList = [for i in 0 .. argTypes.Length - 1 -> $"arg{i}"]
                let argsStr = String.concat ", " argsList
                append ctx $"({argsStr}) => Cons({argsStr})"
            | _ ->
                // Should not happen (Cons always has function type), but safe fallback
                append ctx "Cons"
        // A built-in `Result` constructor used as a value. The struct's
        // factories are static methods on a *closed* generic type, so there is
        // no method group to convert and the lambda has to name the type.
        | "Ok"
        | "Err" when not (Map.containsKey name ctx.UnionCases) ->
            match expr.Type with
            | TFun (_, retType, _) -> append ctx $"(arg0) => {typeToString retType}.{name}(arg0)"
            | _ -> append ctx $"{typeToString expr.Type}.{name}"
        | _ ->
        match Map.tryFind name ctx.UnionCases with
        | Some info ->
            let typeStr = getUnionTypeString expr.Type info.ParentTypeName
            if info.IsDataCase then
                match expr.Type with
                | TFun (argTypes, _, _) ->
                    // A genuine first-class function value. Roslyn caches
                    // no-capture lambdas, so this allocates once per program.
                    let argsList = [for i in 0 .. argTypes.Length - 1 -> $"arg{i}"]
                    let argsStr = String.concat ", " argsList
                    // Cast for the same reason as in `generateApply`: the case
                    // class is not the type Bjolang says this has, and a lambda
                    // is exactly where C# infers rather than being told.
                    append ctx $"({argsStr}) => ({typeStr})new {typeStr}.{sanitizeIdent name}({argsStr})"
                | _ ->
                    append ctx $"({typeStr})new {typeStr}.{sanitizeIdent name}()"
            else
                // A nullary case, which is a *value* rather than a call — so
                // `generateApply` never sees it and this is the only place its
                // type can be pinned.
                append ctx $"({typeStr})new {typeStr}.{sanitizeIdent name}()"
        | None ->
            let targetName = qualifiedName ctx name
            match expr.Type with
            | TFun _ ->
                // A delegate-typed cast of a value or method group; Roslyn caches
                // method-group conversions too.
                append ctx $"(({typeToString expr.Type})({targetName}))"
            | _ ->
                append ctx targetName

    | TApply (target, args, kwArgs) ->
        generateApply ctx expr target args kwArgs

    // --- Foreign .NET interop ---
    //
    // Every one of these names a member the type checker already resolved
    // against .NET metadata. Nothing here chooses an overload, and nothing is
    // left for the C# compiler to work out — including, for a generic method,
    // the type arguments: `foreignTypeArguments` writes them out.

    // `(.Method x ...)`, and an `import/extern` clause naming an instance
    // method — which is the same node, and the reason this path carries the
    // whole of the static one's metadata. An `#:async` import may name an
    // instance method, so the `await`, the `ConfigureAwait(false)` and the
    // ambient token all have to be available here too; the comments on
    // `TForeignStaticCall` below explain why each is not optional.
    | TDotMethodCall (target, methodName, args, meta) ->
        let exceptions =
            meta |> Option.map (fun m -> m.Exceptions) |> Option.defaultValue []

        let methodName = methodName + foreignTypeArguments meta

        let awaits = meta |> Option.map (fun m -> m.Await) |> Option.defaultValue false
        let ambient = meta |> Option.map (fun m -> m.AmbientToken) |> Option.defaultValue false

        let returnsVoid =
            match meta with
            | Some m -> isVoidType m.ReturnType
            | None -> false

        if exceptions.IsEmpty then
            if awaits then append ctx (if awaits && returnsVoid then "await " else "(await ")

            let emitters = prepareOperands ctx (target :: args)
            emitReceiver ctx target emitters.Head
            append ctx $".%s{methodName}("

            for i, emit in List.indexed emitters.Tail do
                if i > 0 then append ctx ", "
                emit ctx

            if ambient then
                if not args.IsEmpty then append ctx ", "
                append ctx ambientTokenArgument

            append ctx ")"

            if awaits then
                append ctx (if returnsVoid then ".ConfigureAwait(false)" else ".ConfigureAwait(false))")
        else
            // The receiver is bound with the arguments, and first: it is
            // evaluated before them and its evaluation is not part of what the
            // call may fail at.
            generateGuarded ctx expr returnsVoid exceptions (target :: args) awaits (fun c names ->
                let receiver = List.head names
                let rest = List.tail names
                let allArgs = if ambient then rest @ [ ambientTokenArgument ] else rest
                let argList = String.concat ", " allArgs
                let call = $"%s{receiver}.%s{methodName}(%s{argList})"
                append c (if awaits then $"(await %s{call}.ConfigureAwait(false))" else call))

    | TDotPropertyGet (target, propName, _) ->
        let emitters = prepareOperands ctx [ target ]
        emitReceiver ctx target emitters.Head
        append ctx $".%s{propName}"

    | TForeignStaticGet (clrType, memberName, _) ->
        append ctx $"%s{clrType}.%s{memberName}"

    // Through the trait's helper, with the implementor as its type argument.
    // The concrete case and the generic case differ only in what that argument
    // is — `<int>` against `<T_a>` — and the JIT inlines the helper in both.
    | TClrMemberCall (traitName, methodName, implType, args) ->
        append ctx $"%s{clrHelperClassName traitName}.%s{sanitizeIdent methodName}<%s{typeToString implType}>("

        for i, emit in List.indexed (prepareOperands ctx args) do
            if i > 0 then append ctx ", "
            emit ctx

        append ctx ")"

    | TNewObject (clrName, args, meta) ->
        let exceptions =
            meta |> Option.map (fun m -> m.Exceptions) |> Option.defaultValue []

        if exceptions.IsEmpty then
            append ctx $"new %s{clrName}("
            for i, emit in List.indexed (prepareOperands ctx args) do
                if i > 0 then append ctx ", "
                emit ctx
            append ctx ")"
        else
            // A constructor always produces a value, so the guarded form never
            // has a void inner call — and never an awaited one, since a
            // constructor is not a task.
            generateGuarded ctx expr false exceptions args false (fun c names ->
                let argList = String.concat ", " names
                append c $"new %s{clrName}(%s{argList})")

    | TForeignStaticCall (clrType, methodName, args, meta) ->
        let exceptions =
            meta |> Option.map (fun m -> m.Exceptions) |> Option.defaultValue []

        let methodName = methodName + foreignTypeArguments meta

        // An `#:async` import. Two things are added and neither is optional —
        // see §7.2.
        //
        // `ConfigureAwait(false)`, because a bare `TaskAwaiter` captures the
        // ambient `SynchronizationContext` and posts the continuation back to
        // it. In a WinForms or legacy ASP.NET host that serialises the whole
        // fiber pool onto one thread and can deadlock outright. Awaiting a
        // *fiber* needs none of this — BjoML's own awaiter never captures a
        // context — which is why the bjoroutine call site does not have it.
        //
        // And the ambient cancellation token, appended by the emitter because
        // the overload was resolved with it. A token the caller has to
        // remember is a token nobody passes.
        let awaits = meta |> Option.map (fun m -> m.Await) |> Option.defaultValue false
        let ambient = meta |> Option.map (fun m -> m.AmbientToken) |> Option.defaultValue false

        // A non-generic `Task` awaits to *nothing* — not to unit, to no value at
        // all — and C# has no parenthesized void expression, so `(await t);` is
        // not a statement. Everything else is parenthesized for the usual
        // reason: `await` binds looser than member access, so a bare one would
        // regroup whatever the value is then used in.
        //
        // Not a problem the bjoroutine call site has. A `defbjo` yielding
        // nothing yields the *unit*, which is a value — limitation 25, earning
        // its keep here.
        let awaitsVoid =
            awaits
            && (match meta with
                | Some m -> isVoidType m.ReturnType
                | None -> false)

        if exceptions.IsEmpty then
            if awaits then append ctx (if awaitsVoid then "await " else "(await ")

            append ctx $"%s{clrType}.%s{methodName}("
            for i, emit in List.indexed (prepareOperands ctx args) do
                if i > 0 then append ctx ", "
                emit ctx

            if ambient then
                if not args.IsEmpty then append ctx ", "
                append ctx ambientTokenArgument

            append ctx ")"

            if awaits then
                append ctx (if awaitsVoid then ".ConfigureAwait(false)" else ".ConfigureAwait(false))")
        else
            let innerIsVoid =
                match meta with
                | Some m -> isVoidType m.ReturnType
                | None -> false

            generateGuarded ctx expr innerIsVoid exceptions args awaits (fun c names ->
                let allArgs = if ambient then names @ [ ambientTokenArgument ] else names
                let argList = String.concat ", " allArgs
                let call = $"%s{clrType}.%s{methodName}(%s{argList})"
                append c (if awaits then $"(await %s{call}.ConfigureAwait(false))" else call))

    // `(try body #:catch (...))`. Like a guarded foreign call, but around an
    // arbitrary expression — which is what lets one guard cover a whole region
    // rather than a single call.
    | TTryCatch (body, exceptions) ->
        // A `(try ...)` around an async call is the natural way to say "and
        // this one may fail", so the guard has to be able to hold the await
        // that call compiles to.
        emitGuard ctx expr (containsAwait body) exceptions ignore (fun c okOf ->
            if isVoidType body.Type then
                generateBlock c Effect body
                indent c
                appendLine c (okOf "default(ValueTuple)")
            else
                let tmp = freshName "__ok"
                generateBindingValue c (DeclareAndAssign(typeToString body.Type, tmp)) body
                indent c
                appendLine c (okOf tmp))

    | TInterfaceCall (iType, mName, dict, args) ->
        let emitters = prepareOperands ctx (dict :: args)
        emitters.Head ctx
        append ctx "."
        append ctx (sanitizeIdent mName)
        append ctx "("
        for i, emit in List.indexed emitters.Tail do
            if i > 0 then append ctx ", "
            emit ctx
        append ctx ")"

    | TLambda (args, body) ->
        // A `(bjoroutine ...)` lambda is an async lambda. C# infers the
        // task-like from the delegate it is converted to, which `typeToString`
        // already spells `Func<..., Bjoml.Fiber<T>>`, so the keyword is the
        // whole difference.
        (match expr.Type with
         | TFun(_, _, EAsync) -> append ctx "async "
         | _ -> ())

        append ctx "("
        append ctx (args |> List.map sanitizeIdent |> String.concat ", ")
        append ctx ") => {\n"
        // A lambda is its own function scope: it has no access to the enclosing
        // loop's slots, a `continue` inside it would bind to nothing, and it
        // cannot be an iterator, so it cannot yield either.
        //
        // Its delegate type decides whether it owes a value: `Action` for the
        // interop void, `Func<..., Bjoml.Unit>` for a Bjolang `(-> ... void)`.
        let returnsVoid =
            match expr.Type with
            | TFun(_, ret, _) -> isVoidType ret
            | _ -> isVoidType body.Type

        let inner = { ctx with Prelude = None; Loop = None; InSeq = false; ReturnsVoid = returnsVoid }
        withIndent inner (fun c -> generateBlock c Return body)
        indent ctx; append ctx "}"

    | TIf (cond, t, f) ->
        // Reached only when nothing inside needs a statement position. Both arms
        // are cast to the conditional's own type: C#'s "best common type" rule
        // rejects arms typed at different subclasses of a union (CS0173).
        let resultType = typeToString expr.Type
        append ctx "("
        generateExpr ctx cond
        append ctx $" ? (%s{resultType})("
        generateExpr ctx t
        append ctx $") : (%s{resultType})("
        generateExpr ctx f
        append ctx "))"

    // `()` is not an expression in C#, and a one-element `(x)` is just `x`
    // rather than a tuple — so the unit value is written as the struct's own
    // default. `default(ValueTuple)` rather than a bare `default`, which needs a
    // target type and does not have one in every position this can appear.
    | TTupleMake [] -> append ctx "default(ValueTuple)"

    | TTupleMake args ->
        append ctx "("
        for i, emit in List.indexed (prepareOperands ctx args) do
            if i > 0 then append ctx ", "
            emit ctx
        append ctx ")"

    | TRecordMake fields ->
        append ctx $"new %s{typeToString expr.Type}("
        for i, emit in List.indexed (prepareOperands ctx (fields |> List.map snd)) do
            if i > 0 then append ctx ", "
            emit ctx
        append ctx ")"

    | TRecordUpdate (name, fields) ->
        // `with` binds loosely, so parenthesize: `(r with { .. }).field` must not
        // parse as `r with { .. field }`.
        let emitters = prepareOperands ctx (fields |> List.map snd)
        append ctx "("
        append ctx (qualifiedName ctx name)
        append ctx " with { "
        for i, ((k, _), emit) in List.indexed (List.zip fields emitters) do
            if i > 0 then append ctx ", "
            append ctx (sanitizeIdent k)
            append ctx " = "
            emit ctx
        append ctx " })"

    | TIsInst (target, t) ->
        append ctx "("
        generateExpr ctx target
        append ctx " is "
        append ctx (typeToString t)
        append ctx ")"

    | TIsInstCase (target, t, caseName) ->
        append ctx "("
        generateExpr ctx target
        append ctx $" is {typeToString t}.{sanitizeIdent caseName}"
        append ctx ")"

    | TGetField (target, field) ->
        generateExpr ctx target
        append ctx "."
        append ctx (sanitizeIdent field)

    | TCast (target, t) ->
        append ctx "(("
        append ctx (typeToString t)
        append ctx ")("
        generateExpr ctx target
        append ctx "))"

    | TCaseCast (target, t, caseName) ->
        append ctx "(("
        append ctx $"{typeToString t}.{sanitizeIdent caseName}"
        append ctx ")("
        generateExpr ctx target
        append ctx "))"

    | TListMake items ->
        // Desugar to nested SchemeList.Cons / SchemeList.Empty calls.
        let elemTypeStr = elementTypeString expr.Type
        let emitters = prepareOperands ctx items
        let rec emitCons remaining =
            match remaining with
            | [] -> append ctx $"SchemeList.SchemeList.Empty<%s{elemTypeStr}>()"
            | emit :: rest ->
                append ctx "SchemeList.SchemeList.Cons("
                emit ctx
                append ctx ", "
                emitCons rest
                append ctx ")"
        emitCons emitters

    | TArrayMake items ->
        let elementTypeStr =
            match expr.Type with
            | TCon ("Array", [ elemT ]) -> typeToString elemT
            | _ -> "object"
        append ctx $"new %s{elementTypeStr}[] {{ "
        for i, emit in List.indexed (prepareOperands ctx items) do
            if i > 0 then append ctx ", "
            emit ctx
        append ctx " }"

    | TMatch (matchTarget, clauses) ->
        // Reached only when every live arm and guard is expression-shaped.
        let live = liveClauses clauses
        generateExpr ctx matchTarget
        appendLine ctx " switch {"
        withIndent ctx (fun c ->
            // Arms have no statement position of their own.
            let armCtx = { c with Prelude = None }
            for clause in live do
                indent armCtx
                generatePattern armCtx clause.Pattern
                match clause.Guard with
                | Some guard ->
                    append armCtx " when "
                    generateExpr armCtx guard
                | None -> ()
                append armCtx " => "
                generateExpr armCtx clause.Body
                appendLine armCtx ","
            if not (live |> List.exists isIrrefutable) then
                indent armCtx
                appendLine armCtx $"_ => throw new Exception(\"Match failure at %s{Lexer.formatPos expr.Range}\")"
        )
        indent ctx; append ctx "}"

    | TTypeEq _ ->
        codegenError expr.Range "type equality tests are not supported by the C# backend"

    // Every trait call has been turned into either a spliced body or a direct
    // call by the time code is generated: `TraitInline` takes the resolved ones
    // and `Lowering` takes the rest. One reaching here means neither did.
    | TTraitCall (tref, _, _) ->
        codegenError
            expr.Range
            $"internal error: call to '{tref.Trait}.{tref.Method}' was never resolved to an implementation"

    | TThrow _
    | TVecMake _
    | TLet _
    | TLetRec _
    | TLetTuple _
    | TLetMutable _
    | TSet _
    | TRecordSet _
    | TWhen _
    | TTryFinally _
    | TLoop _
    | TRecur _
    | TSeq _
    | TBjo _
    | TTaskEvent _
    | TDotPropertySet _
    | TForeignStaticSet _
    | TYield _
    | TYieldFrom _ ->
        // Statement-shaped: `needsHoist` has already routed these away.
        codegenError expr.Range "internal error: statement-shaped node reached expression emission"

/// Emits the receiver of a `.Member` access, parenthesized when C# needs it.
///
/// `new Foo().Bar` does not parse, and neither does a conditional or a cast in
/// receiver position. Only the shapes that are already primary expressions are
/// left bare; anything else gets parentheses, which are never wrong.
and private emitReceiver (ctx: CodegenContext) (target: TypedExpr) (emit: CodegenContext -> unit) : unit =
    match target.Node with
    | TIdent _
    | TString _
    | TApply _
    | TDotMethodCall _
    | TDotPropertyGet _
    | TForeignStaticCall _
    | TForeignStaticGet _
    | TGetField _ -> emit ctx
    | _ ->
        append ctx "("
        emit ctx
        append ctx ")"

/// Emits a foreign call whose declared exceptions turn it into a `Result`.
///
/// The shape is an immediately invoked `Func<>` so that the whole thing stays a
/// C# *expression* and can appear anywhere a call could.
///
/// Two details are load-bearing:
///
///   * The arguments are evaluated into locals *before* the `try`. An exception
///     raised while working out an argument is not one the call raised, and
///     catching it would blame the wrong thing.
///   * The `catch` carries an exception *filter* naming exactly the types that
///     were declared. Anything not listed keeps unwinding — a `#:exceptions`
///     clause says which failures are values, and everything else stays a bug.
and private emitGuard
    (ctx: CodegenContext)
    (expr: TypedExpr)
    (isAsync: bool)
    (exceptions: string list)
    (emitPrologue: CodegenContext -> unit)
    (emitTryBody: CodegenContext -> (string -> string) -> unit)
    : unit =

    let resultType = typeToString expr.Type
    let exVar = freshName "__ex"
    let filter = exceptions |> List.map (fun e -> $"%s{exVar} is %s{e}") |> String.concat " || "
    let okOf (value: string) = $"return %s{resultType}.Ok(%s{value});"

    // A guarded region containing an `await` needs a lambda that can hold one,
    // and a lambda that can hold one is a fiber of its own. `Func<Fiber<R>>`
    // rather than `Func<Task<R>>` for the usual reason: awaiting a fiber costs
    // no context capture, and this one is immediately awaited by the member
    // that wrote it.
    //
    // The alternative was to reject `#:exceptions` on an `#:async` import.
    // §7.2's own example writes both together — an async call that cannot fail
    // is not an interesting async call — so the cost of one extra state machine
    // per guarded call is the right side of that trade.
    if isAsync then
        append ctx $"(await new Func<Bjoml.Fiber<%s{resultType}>>(async () => {{\n"
    else
        append ctx $"new Func<%s{resultType}>(() => {{\n"

    // A lambda is its own function scope: no enclosing loop to jump to, and no
    // iterator to yield from.
    let inner = { ctx with Prelude = None; Loop = None; InSeq = false }

    withIndent inner (fun c ->
        // Whatever has to happen before the guarded region — evaluating a
        // call's arguments, which is not part of what the call may fail at.
        emitPrologue c

        indent c
        appendLine c "try {"
        withIndent c (fun c2 -> emitTryBody c2 okOf)

        indent c
        appendLine c $"}} catch (Exception %s{exVar}) when (%s{filter}) {{"

        withIndent c (fun c2 ->
            indent c2
            appendLine c2 $"return %s{resultType}.Err(%s{exVar});")

        indent c
        appendLine c "}")

    indent ctx
    append ctx (if isAsync then "})())" else "})()")

and private generateGuarded
    (ctx: CodegenContext)
    (expr: TypedExpr)
    (innerIsVoid: bool)
    (exceptions: string list)
    (args: TypedExpr list)
    (isAsync: bool)
    (emitCall: CodegenContext -> string list -> unit)
    : unit =

    let temps = args |> List.map (fun a -> freshName "__farg", a)

    let prologue (c: CodegenContext) =
        for tmp, arg in temps do
            generateBindingValue c (DeclareAndAssign(typeToString arg.Type, tmp)) arg

    emitGuard ctx expr isAsync exceptions prologue (fun c okOf ->
        indent c

        if innerIsVoid then
            // `Ok` of a void call carries the unit value, because
            // `Result<E, void>` is not a type C# has.
            emitCall c (List.map fst temps)
            appendLine c ";"
            indent c
            appendLine c (okOf "default(ValueTuple)")
        else
            let tmp = freshName "__ok"
            append c $"var %s{tmp} = "
            emitCall c (List.map fst temps)
            appendLine c ";"
            indent c
            appendLine c (okOf tmp))

/// Fully qualifies a module-level binding.
and private qualifiedName (ctx: CodegenContext) (name: string) =
    match Map.tryFind name ctx.GlobalBindings with
    | Some("", member') -> sanitizeIdent member'
    | Some(modName, member') -> $"%s{moduleClassName modName}.%s{sanitizeIdent member'}"
    | None -> sanitizeIdent name

/// Evaluates a statement-shaped node into a temporary in the enclosing statement
/// position and yields the temporary's name.
and private hoistToTemp (ctx: CodegenContext) (prelude: ResizeArray<string>) (expr: TypedExpr) : string =
    let tmp = freshName "__hoist"
    let scratch = StringBuilder()
    let inner = { ctx with Builder = scratch; Prelude = None }

    if isVoidType expr.Type then
        // Whatever follows in the enclosing expression still has to run, so this
        // is an intermediate statement rather than a block's last word.
        generateBlock inner Effect expr
    else
        generateBindingValue inner (DeclareAndAssign(typeToString expr.Type, tmp)) expr

    // Anything the node hoisted in turn is already inside `scratch`, ahead of the
    // node's own statements, so appending as one unit preserves the order.
    prelude.Add(scratch.ToString())
    tmp

/// Emits the operands of a single construct, preserving left-to-right evaluation.
///
/// Hoisting a node out of the middle of an operand list moves its evaluation
/// earlier, so every operand up to and including the last hoisted one is pulled
/// into a temporary too. There is no purity information in the typed AST, so no
/// operand is exempted.
///
/// The list must be given in *source* order; the returned emitters may be used
/// in any order, which is what `TApply`'s keyword branch needs.
and private prepareOperands (ctx: CodegenContext) (operands: TypedExpr list) : (CodegenContext -> unit) list =
    let hoisted =
        match ctx.Prelude with
        | Some prelude when operands |> List.exists containsHoist ->
            let lastHoisted =
                operands
                |> List.mapi (fun i e -> i, containsHoist e)
                |> List.filter snd
                |> List.map fst
                |> List.max

            Some(prelude, lastHoisted)
        | _ -> None

    match hoisted with
    | None -> operands |> List.map (fun operand -> fun (c: CodegenContext) -> generateExpr c operand)
    | Some(prelude, lastHoisted) ->
        operands
        |> List.mapi (fun i operand ->
            if i <= lastHoisted then
                let tmp = hoistToTemp ctx prelude operand
                fun (c: CodegenContext) -> append c tmp
            else
                fun (c: CodegenContext) -> generateExpr c operand)

and private generateApply
    (ctx: CodegenContext)
    (expr: TypedExpr)
    (target: TypedExpr)
    (args: TypedExpr list)
    (kwArgs: (string * TypedExpr) list)
    : unit =

    match target.Node with
    | TIdent (name, _) when Map.containsKey name ctx.UnionCases ->
        let info = Map.find name ctx.UnionCases
        let typeStr = getUnionTypeString expr.Type info.ParentTypeName

        // Cast to the union, because `new Msg.Job(1)` has the *case* class as
        // its static type and Bjolang says the value is a `Msg`.
        //
        // Free at run time — it is a reference upcast — and almost always
        // redundant, because an assignment or an argument position supplies the
        // target type anyway. It stops being redundant exactly where C# has to
        // *infer* a type from this expression and there is nothing else to
        // infer from: the body of a lambda passed to a generic function.
        // `(list-map (fun (j) (Job j)) xs)` inferred `Func<int, Msg.Job>` and
        // then could not convert the resulting `SchemeList<Msg.Job>`, because
        // C#'s generics are invariant. `(wrap ev (fun (j) (Job j)))` is the
        // same shape, and it is the idiomatic way to write a `choose` branch.
        append ctx $"({typeStr})new {typeStr}.{sanitizeIdent name}("
        for i, emit in List.indexed (prepareOperands ctx args) do
            if i > 0 then append ctx ", "
            emit ctx
        append ctx ")"

    // Unary operators. `(- x)` and `(/ x)` desugar to `negate` and `recip` in
    // the parser, so by the time codegen runs an arithmetic operator is always
    // binary.
    | TIdent (("negate" | "recip" | "bitwise-not") as name, _) when args.Length = 1 && kwArgs.IsEmpty ->
        let operand = (prepareOperands ctx args).Head

        castPromoted ctx expr.Type (fun () ->
            match name with
            | "negate" -> append ctx "(-("
            | "recip" -> append ctx "(1 / ("
            | _ -> append ctx "(~("

            operand ctx
            append ctx "))")

    | TIdent (name, _) when Map.containsKey name infixOperators && args.Length = 2 && kwArgs.IsEmpty ->
        let emitters = prepareOperands ctx args

        castPromoted ctx expr.Type (fun () ->
            append ctx "("
            emitters[0] ctx
            append ctx $" %s{infixOperators[name]} "
            emitters[1] ctx
            append ctx ")")

    | TIdent (name, _) when List.contains name ["<"; ">"; "<="; ">="] && args.Length = 2 && kwArgs.IsEmpty ->
        let emitters = prepareOperands ctx args
        append ctx "("
        emitters[0] ctx
        append ctx $" {name} "
        emitters[1] ctx
        append ctx ")"

    // A built-in `Result` constructor, applied. The struct has static factories
    // rather than nested case classes, so the closed generic type is named and
    // the case is a method on it.
    | TIdent (("Ok" | "Err") as name, _) when
        not (Map.containsKey name ctx.UnionCases) && args.Length = 1 && kwArgs.IsEmpty
        ->
        append ctx $"%s{typeToString expr.Type}.%s{name}("
        (prepareOperands ctx args).Head ctx
        append ctx ")"

    // `clr-eq` is C# `==` and nothing else. `=` never reaches codegen as an
    // identifier — it is a trait method — so this arm is what makes the `Eq`
    // implementation for a primitive type compile to the operator rather than
    // to a comparer call.
    | TIdent ("clr-eq", _) when args.Length = 2 && kwArgs.IsEmpty ->
        let emitters = prepareOperands ctx args
        append ctx "("
        emitters[0] ctx
        append ctx " == "
        emitters[1] ctx
        append ctx ")"

    | _ ->
        // The callee is evaluated first, so it joins the operand list in source
        // order ahead of the arguments.
        let calleeIsIdent =
            match target.Node with
            | TIdent _ -> true
            | _ -> false

        let sourceOperands =
            (if calleeIsIdent then [] else [ target ]) @ args @ (kwArgs |> List.map snd)

        let emitters = prepareOperands ctx sourceOperands

        let calleeEmitters, argEmitters =
            if calleeIsIdent then [], emitters else [ emitters.Head ], emitters.Tail

        let positionalEmitters = argEmitters |> List.truncate args.Length
        let keywordEmitters = argEmitters |> List.skip args.Length

        // A call to a bjoroutine is the yield point, and this is where it
        // becomes one. The method returns `Fiber<T>`, the language says the
        // call has type `T`, and the `await` is the whole of the difference —
        // which is what "suspension is invisible at the call site" means in
        // §1: nothing else about the call is spelled differently.
        //
        // Parenthesised because `await` binds looser than member access, so a
        // bare one would regroup anything the call's value is then used in.
        //
        // No `ConfigureAwait`. BjoML's awaiter never captures a
        // SynchronizationContext and never flows ExecutionContext, so there is
        // nothing to configure — unlike a BCL `Task`, where §7.2 makes it an
        // emitter obligation.
        let isYieldPoint =
            match target.Type with
            | TFun(_, _, EAsync) -> true
            | _ -> false

        if isYieldPoint then append ctx "(await "

        match target.Node with
        | TIdent (name, tArgs) ->
            if name.Contains("::") && not tArgs.IsEmpty && not (isModuleQualified name) then
                // Trait instance method: "TraitName_Type.Instance::methodName"
                // Split at "::" to insert type args on the class portion.
                let parts = name.Split("::")
                let classPart = parts.[0]  // e.g. "Foldable_List.Instance"
                let methodPart = parts.[1] // e.g. "fold"
                let tyArgsStr = tArgs |> List.map typeToString |> String.concat ", "
                // Insert <T> before ".Instance"
                let classPortions = classPart.Split('.')
                if classPortions.Length >= 2 then
                    // className.Instance -> className<T>.Instance
                    append ctx (sanitizeIdent classPortions.[0])
                    append ctx $"<%s{tyArgsStr}>"
                    for i in 1 .. classPortions.Length - 1 do
                        append ctx "."
                        append ctx (sanitizeIdent classPortions.[i])
                else
                    append ctx (sanitizeIdent classPart)
                    append ctx $"<%s{tyArgsStr}>"
                append ctx "."
                append ctx (sanitizeIdent methodPart)
            else
                append ctx (qualifiedName ctx name)
                // A call with arguments normally lets C# infer its type
                // arguments from them. The exceptions are the ones whose type
                // argument appears only in the *return* type, which C# cannot
                // infer from anything: a nullary call, `make-array`, and
                // `raise`, which claims to return whatever the position it
                // stands in wants and in fact returns nothing at all.
                //
                // A lambda argument is a fourth: an anonymous function whose
                // parameters have no written types contributes nothing to
                // inference, so a call whose arguments are *all* lambdas is as
                // blind as one with no arguments at all. That is what
                // `(make-parameter (fun (s) ...))` is.
                let onlyLambdas =
                    not args.IsEmpty
                    && args |> List.forall (fun a -> match a.Node with TLambda _ -> true | _ -> false)

                if not tArgs.IsEmpty
                   && (args.IsEmpty || onlyLambdas || name = "make-array" || name = "makesubarray" || name = "raise")
                   && kwArgs.IsEmpty then
                    let tyArgsStr = tArgs |> List.map typeToString |> String.concat ", "
                    append ctx $"<%s{tyArgsStr}>"
        | TLambda _ ->
            // A lambda literal has no type of its own. C# infers one from an
            // argument or assignment context, but a callee position gives it
            // nothing to infer from and `(x) => { … }(a)` is rejected (CS0149),
            // so the delegate type has to be written out.
            append ctx $"(({typeToString target.Type})("
            calleeEmitters.Head ctx
            append ctx "))"
        | _ -> calleeEmitters.Head ctx

        append ctx "("
        if kwArgs.IsEmpty && not (calleeDeclaresKeywords target args) then
            // Nothing can be left out, so nothing has to be named.
            for i, emit in List.indexed positionalEmitters do
                if i > 0 then append ctx ", "
                emit ctx
        else
            // The callee's type is TFun([mandatory..., keyword..., rest?], ret)
            // and `args` is mandatory ++ the rest array — `Inference` gathers
            // rest arguments into one array rather than leaving them spread —
            // so the last positional emitter is the array, and the rest are the
            // mandatory arguments.
            let mandatoryEmitters, restEmitters =
                if calleeHasRest target && not positionalEmitters.IsEmpty then
                    positionalEmitters |> List.splitAt (positionalEmitters.Length - 1)
                else
                    positionalEmitters, []

            let mutable argIdx = 0

            for emit in mandatoryEmitters do
                if argIdx > 0 then append ctx ", "
                emit ctx
                argIdx <- argIdx + 1

            for (kwName, _), emit in List.zip kwArgs keywordEmitters do
                if argIdx > 0 then append ctx ", "
                // The parameter is declared under `keywordParamName`, so that is
                // what a named argument has to say. Writing the bare name
                // produced C# that named a parameter which does not exist —
                // latent only because nothing in the suite had ever passed a
                // keyword argument rather than relying on its default.
                //
                // One spelling serves both lowerings. A bare value binds to the
                // declared type directly, and to an `Option` of it through the
                // runtime's implicit conversion, so a call site never has to
                // know which the callee chose — which is what lets the choice
                // depend on the default *expression*, a thing no importer can
                // see in a `.dll`.
                append ctx $"%s{keywordParamName kwName}: "
                emit ctx
                argIdx <- argIdx + 1

            for emit in restEmitters do
                if argIdx > 0 then append ctx ", "
                // Named for the same reason the keywords are: an omitted
                // keyword leaves a hole ahead of the array, and a positional
                // argument cannot be placed across one. The callee declares it
                // under this name whenever it has keyword parameters at all —
                // which is exactly when this branch is reached with a rest
                // argument to pass.
                append ctx $"%s{restParamName}: "
                emit ctx
                argIdx <- argIdx + 1
        append ctx ")"
        if isYieldPoint then append ctx ")"

/// Emits the value of a *binding*: a `let`, a `set!`, a hoisted temporary.
///
/// Every `BlockTarget` but `Effect` describes a terminal position, and inside an
/// inlined loop a terminal target ends the loop. A binding is not terminal —
/// whatever follows it still has to run — but it has to name somewhere to put
/// the value, so it uses a terminal-looking target anyway. Hiding the loop is
/// what stops `exitInlineLoop` from emitting a `break` between the binding and
/// its use, which is either a use of an unassigned local or, worse, a loop that
/// silently runs one iteration.
///
/// This was independently the same bug at four sites, so it has a name now.
/// Nothing inside a binding's value can legitimately jump to the enclosing loop:
/// a `TRecur` only ever appears in tail position, and a nested loop brings its
/// own context.
and private generateBindingValue (ctx: CodegenContext) (target: BlockTarget) (value: TypedExpr) : unit =
    generateBlock { ctx with Loop = None } target value

/// Emits one statement, giving `generateExpr` somewhere to hoist statement-shaped
/// operands to. The statement is built into a scratch buffer first so that the
/// hoisted statements can be written ahead of it — including ahead of its indent.
and private emitStatement (ctx: CodegenContext) (build: CodegenContext -> unit) : unit =
    let prelude = ResizeArray<string>()
    let scratch = StringBuilder()

    build { ctx with Builder = scratch; Prelude = Some prelude }

    for stmt in prelude do
        ctx.Builder.Append(stmt) |> ignore

    ctx.Builder.Append(scratch) |> ignore

and generateBlock (ctx: CodegenContext) (target: BlockTarget) (expr: TypedExpr) : unit =
    lineDirective ctx expr.Range

    match expr.Node with
    | TRecur (index, args) -> generateRecur ctx target expr index args

    | TLoop (members, bodyOpt) ->
        match bodyOpt with
        | Some body ->
            // Check if this is a single-entry flat loop (like a named-let):
            // A single member whose loop name is immediately called with initial arguments in `body`,
            // and the loop name is not referenced as a first-class value or called non-tail-recursively.
            let flatLoopInfo = LoopLowering.flatLoopEntry members body

            // The same recognition for a group of *several* members, which is
            // what a multi-level loop is. `desugarLoop` emits
            // `ELetRec(members, EApp(EIdent(levels[0].Member), initialArgs))`,
            // and `LetRecify` has already split the finish member into an SCC of
            // its own, so the group reaching here is exactly the level members
            // entered by a call to one of them.
            //
            // Inlining it rather than emitting a local function is what lets a
            // `yield` inside a nested loop land in the enclosing iterator
            // method — a local function may not `yield return` — and it saves a
            // function and its closure over the captured locals besides.
            let mergedInlineInfo = LoopLowering.mergedLoopEntry members body

            match flatLoopInfo with
            | Some (member_, initArgs) ->
                // Flat loop path: emit slot variables, evaluate initial args, and run while(true) inline!
                for (slotName, slotType) in member_.Slots do
                    indent ctx; appendLine ctx $"{typeToString slotType} {sanitizeIdent slotName};"

                let temps = initArgs |> List.map (fun _ -> freshName "__init")
                for arg, tmp in List.zip initArgs temps do
                    emitStatement ctx (fun c ->
                        indent c
                        append c $"var {tmp} = "
                        generateExpr c arg
                        appendLine c ";")

                let slots = member_.Slots |> List.map (fst >> sanitizeIdent)
                for slot, tmp in List.zip slots temps do
                    indent ctx; appendLine ctx $"{slot} = {tmp};"

                let loopTarget =
                    match target with
                    | DeclareAndAssign (varType, varName) ->
                        indent ctx; appendLine ctx $"{varType} {varName};"
                        Assign varName
                    // The loop's own value is dropped, but its body still has to
                    // leave the `while (true)` when it stops jumping — which is
                    // exactly what `Discard` means inside an inlined loop.
                    | Effect -> Discard
                    | _ -> target

                let exitLabel = freshName "__exit"
                let exitLabelUsed = ref false

                let inner =
                    { ctx with
                        Loop =
                            Some
                                { Members = members
                                  Merged = false
                                  StateVar = ""
                                  NestedSwitches = 0
                                  IsInlineLoop = true
                                  ExitLabel = exitLabel
                                  ExitLabelUsed = exitLabelUsed } }

                // Whether the label is needed is only known once the body has
                // been generated, and it has to appear *after* the loop — so
                // the loop is built aside and appended once the answer is in.
                let scratch = StringBuilder()
                let buffered = { inner with Builder = scratch }

                indent buffered; appendLine buffered "while (true) {"
                withIndent buffered (fun c2 ->
                    emitIterationCopies c2 member_
                    generateBlock c2 loopTarget member_.Body)
                indent buffered; appendLine buffered "}"

                ctx.Builder.Append(scratch) |> ignore

                if exitLabelUsed.Value then
                    // A label needs a statement; an empty one will do.
                    indent ctx; appendLine ctx $"%s{exitLabel}: ;"

            | None ->

            match mergedInlineInfo with
            | Some(entryIdx, initArgs) ->
                // The merged group, emitted here instead of inside a local
                // function of its own. Same `while (true) switch (state)` shape
                // `generateMergedLoop` uses; the difference is only that the
                // slots are locals, the state is a local, and the members'
                // bodies feed the enclosing target rather than a `return`.
                let entry = members[entryIdx]
                let retStr = typeToString entry.RetType

                // Every member feeds the same target, so they still have to
                // agree on a type — the same constraint the function form had,
                // for a different reason.
                for m in members do
                    if typeToString m.RetType <> retStr then
                        codegenError
                            m.Body.Range
                            $"'%s{entry.LoopName}' and '%s{m.LoopName}' tail-call each other but return %s{retStr} and %s{typeToString m.RetType}; a merged loop has one return type, so split the group so that they do not tail-call each other"

                // `default!` is not cosmetic. Only the entry member's slots are
                // assigned before the loop; the rest are written by whatever
                // jump enters their member, and C# will not prove that through a
                // `switch` dispatch — `case 1:` is reachable from the initial
                // dispatch with its slots unwritten. As parameters this was free.
                for m in members do
                    for (slotName, slotType) in m.Slots do
                        indent ctx
                        appendLine ctx $"%s{typeToString slotType} %s{sanitizeIdent slotName} = default!;"

                // Through temps, so that an argument may read a slot an earlier
                // assignment would already have overwritten.
                let temps = initArgs |> List.map (fun _ -> freshName "__init")

                for arg, tmp in List.zip initArgs temps do
                    emitStatement ctx (fun c ->
                        indent c
                        append c $"var %s{tmp} = "
                        generateExpr c arg
                        appendLine c ";")

                for (slotName, _), tmp in List.zip entry.Slots temps do
                    indent ctx
                    appendLine ctx $"%s{sanitizeIdent slotName} = %s{tmp};"

                let stateVar = freshName "__state"
                indent ctx
                appendLine ctx $"int %s{stateVar} = %d{entryIdx};"

                let loopTarget =
                    match target with
                    | DeclareAndAssign(varType, varName) ->
                        indent ctx
                        appendLine ctx $"%s{varType} %s{varName};"
                        Assign varName
                    | Effect -> Discard
                    | _ -> target

                let exitLabel = freshName "__exit"
                let exitLabelUsed = ref false

                let inner =
                    { ctx with
                        Loop =
                            Some
                                { Members = members
                                  Merged = true
                                  StateVar = stateVar
                                  // Zero really is the count: the bodies sit
                                  // directly inside the group's dispatch switch,
                                  // so `goto case` is legal from them. Exits go
                                  // through the label instead, which
                                  // `exitInlineLoop` decides from `Merged`.
                                  NestedSwitches = 0
                                  IsInlineLoop = true
                                  ExitLabel = exitLabel
                                  ExitLabelUsed = exitLabelUsed } }

                // Buffered, because whether the label is needed is only known
                // once the bodies have been generated.
                let scratch = StringBuilder()
                let buffered = { inner with Builder = scratch }

                indent buffered
                appendLine buffered $"while (true) switch (%s{stateVar}) {{"

                withIndent buffered (fun c ->
                    for i, member_ in List.indexed members do
                        indent c
                        appendLine c $"case %d{i}: {{"

                        withIndent c (fun cb ->
                            emitIterationCopies cb member_
                            generateBlock cb loopTarget member_.Body)

                        indent c
                        appendLine c "}"

                    indent c
                    appendLine c "default: throw new Exception(\"Unreachable loop state\");")

                indent buffered
                appendLine buffered "}"

                ctx.Builder.Append(scratch) |> ignore

                if exitLabelUsed.Value then
                    indent ctx
                    appendLine ctx $"%s{exitLabel}: ;"

            | None ->
                // General letrec / mutually-recursive / escaping loop: emit as local functions
                generateLoopGroup ctx members body
                generateBlock ctx target body
        | None ->
            codegenError
                expr.Range
                "internal error: a function-body loop was emitted outside of a function body"

    | TLet (name, isFun, fn, value, body) ->
        // `LetRecify` only emits `ELet` for a singleton component with no
        // self-edge, so a function-shaped binding here is always a
        // *non-recursive* local function and needs no loop.
        let asLocalFunction =
            if isFun then
                match value.Node, value.Type with
                | TLambda (_, lambdaBody), TFun (argTypes, retType, _) ->
                    Some(argTypes, retType, lambdaBody)
                | _ -> None
            else
                None

        match asLocalFunction with
        | Some (argTypes, retType, lambdaBody) ->
            generateLocalFunction ctx name fn argTypes retType lambdaBody value.Type
        | None ->
            if isVoidType value.Type || name = "_" then
                // `(begin a b)` is `TLet ("_", …, a, b)`: `a` runs, then `b`. The
                // block is not over, so this is an `Effect`, not a `Discard`.
                generateBlock ctx Effect value
            else
                // The body below *is* terminal, and is generated with the loop
                // still in scope so that a tail call in it still becomes a jump.
                generateBindingValue ctx (DeclareAndAssign(typeToString value.Type, sanitizeIdent name)) value

        generateBlock ctx target body

    | TLetRec (bindings, body) ->
        // A group `LoopLowering` declined to turn into a loop.
        //
        // A function-shaped member becomes a C# local function. They are
        // mutually visible within the block whatever order they are emitted in,
        // which is what a recursive group needs, and it is the only encoding
        // that can carry a keyword or rest parameter — which is one of the
        // reasons the promotion was declined.
        //
        // Anything else is a plain local, declared ahead of the group so that a
        // member can name a sibling, and assigned in source order.
        let isLocalFunction (_, isFun, _, (value: TypedExpr)) =
            isFun
            && (match value.Node with
                | TLambda _ -> true
                | _ -> false)

        for (name, _, fn, value) in bindings |> List.filter isLocalFunction do
            match value.Node, value.Type with
            | TLambda(_, lambdaBody), TFun(argTypes, retType, _) ->
                generateLocalFunction ctx name fn argTypes retType lambdaBody value.Type
            | _ -> ()

        let values = bindings |> List.filter (isLocalFunction >> not)

        for (name, _, _, value) in values do
            indent ctx
            appendLine ctx $"{typeToString value.Type} {sanitizeIdent name} = default!;"
        for (name, _, _, value) in values do
            generateBlock { ctx with Loop = None } (Assign(sanitizeIdent name)) value
        generateBlock ctx target body

    | TLetMutable (name, value, body) ->
        generateBindingValue ctx (DeclareAndAssign(typeToString value.Type, sanitizeIdent name)) value
        generateBlock ctx target body

    // Qualified, not bare: `set!` through an alias has to write the original's
    // cell rather than name a field the alias's own class does not have.
    // `AlphaRename` has already made sure no local shares a top-level name, so
    // the lookup cannot capture one.
    | TSet (name, value) ->
        generateBindingValue ctx (Assign(qualifiedName ctx name)) value
        // `set!` itself yields void, so the enclosing target still has to be
        // discharged.
        dischargeVoid ctx target

    // `(record-set! r (f v) ...)`. Qualified for the reason `set!` is: a write
    // through an alias has to reach the original's binding.
    | TRecordSet (name, fields) ->
        let lhs = qualifiedName ctx name

        match fields with
        // One field cannot be reordered against anything, so it is written
        // where it is evaluated.
        | [ (field, value) ] ->
            generateBindingValue ctx (Assign $"%s{lhs}.%s{sanitizeIdent field}") value

        // Every value is evaluated, left to right, before any of them is
        // written. A value that reads the record therefore sees the state
        // before the form whichever field it reads, rather than a state half
        // way through it.
        | _ ->
            let temporaries =
                fields
                |> List.map (fun (field, value) ->
                    let tmp = freshName "__field"
                    generateBindingValue ctx (DeclareAndAssign(typeToString value.Type, tmp)) value
                    field, tmp)

            for (field, tmp) in temporaries do
                indent ctx
                appendLine ctx $"%s{lhs}.%s{sanitizeIdent field} = %s{tmp};"

        dischargeVoid ctx target

    // A `#:set` import, applied. The left-hand side is a member access rather
    // than a name, and everything else is `set!`.
    | TDotPropertySet (receiver, propName, value) ->
        // The receiver is evaluated before the value, as it is written. A
        // temporary only where the receiver needs statements of its own:
        // assigning to `w.Position` reads better than assigning to a member of
        // a name invented for `w`.
        let lhs =
            if containsHoist receiver then
                let tmp = freshName "__target"
                generateBindingValue ctx (DeclareAndAssign(typeToString receiver.Type, tmp)) receiver
                tmp
            else
                let scratch = StringBuilder()
                let inner = { ctx with Builder = scratch; Prelude = None }
                emitReceiver inner receiver (fun c -> generateExpr c receiver)
                scratch.ToString()

        generateBindingValue ctx (Assign $"%s{lhs}.%s{propName}") value
        dischargeVoid ctx target

    | TForeignStaticSet (clrType, memberName, value) ->
        generateBindingValue ctx (Assign $"%s{clrType}.%s{memberName}") value
        dischargeVoid ctx target

    // `(bjo (f x y))` — spawn.
    //
    // The split is the whole content of the form: every operand is bound to a
    // local *here*, in the enclosing block, and the call is rebuilt over those
    // locals inside the spawned lambda. So `(bjo (handle (next-job!)))` runs
    // `next-job!` on this fiber and only `handle` on the new one. If the
    // operands ran in the child, every spawn would race and evaluation order
    // would depend on the scheduler.
    //
    // The callee is left alone when it is a plain name: a static method is not
    // an expression and has nothing to evaluate. Anything else — a local
    // holding a lambda, a field — is bound like an argument.
    //
    // Rebuilding the call rather than emitting it by hand is what keeps every
    // call shape working: keyword arguments, a rest parameter, an operator, a
    // trait method, and the `await` a bjoroutine callee needs all come from the
    // ordinary emitter.
    | TBjo call ->
        let bindOperand (operand: TypedExpr) : TypedExpr =
            let tmp = freshName "__bjo"
            generateBindingValue ctx (DeclareAndAssign(typeToString operand.Type, tmp)) operand
            { operand with Node = TIdent(tmp, []) }

        let spawned =
            match call.Node with
            | TApply(callee, args, kwArgs) ->
                let boundCallee =
                    match callee.Node with
                    | TIdent _ -> callee
                    | _ -> bindOperand callee

                let boundArgs = args |> List.map bindOperand
                let boundKwArgs = kwArgs |> List.map (fun (n, e) -> n, bindOperand e)
                { call with Node = TApply(boundCallee, boundArgs, boundKwArgs) }
            // The parser only admits an application, so this is unreachable —
            // but a spawn of something with nothing to split is still a spawn.
            | _ -> call

        // `Fiber<void>` is not a type. A call that yields nothing yields the
        // unit, which is what the promise then carries.
        let payload =
            if isVoidType spawned.Type then "Bjoml.Unit" else typeToString spawned.Type

        // Explicit type argument: BjoML's own docs warn that C# cannot infer the
        // result of an async lambda, and this is exactly that call.
        emitTerminal ctx target expr.Type (fun c ->
            append c $"Bjoml.Bjo.Spawn<%s{payload}>(async () => "
            generateExpr c spawned
            append c ")")

    // `(task->event (fetch url))` — the event of making the call, not the call.
    //
    // The arguments are bound here, for the reason `bjo`'s are and one more:
    // the lambda runs at every sync, and `guard` may sync the same event twice,
    // so an argument emitted inside it would be evaluated again each time.
    // Bound out here it is evaluated once, where it is written.
    //
    // The token parameter is the branch's, not the ambient one — that is the
    // whole difference from an ordinary `#:async` call. `TaskEvent` links the
    // two, so the work stops when either the branch loses or the scope is
    // cancelled.
    | TTaskEvent (receiver, clrType, methodName, args, payload, awaitIsVoid) ->
        let bind (e: TypedExpr) =
            let tmp = freshName "__task"
            generateBindingValue ctx (DeclareAndAssign(typeToString e.Type, tmp)) e
            tmp

        // The receiver is bound first and for the same reason as the arguments:
        // it is evaluated where the form stands, and the lambda below runs once
        // per sync.
        let boundReceiver = receiver |> Option.map bind
        let boundArgs = args |> List.map bind

        let tok = freshName "__tok"
        let payloadStr = typeToString payload

        emitTerminal ctx target expr.Type (fun c ->
            let argList = String.concat ", " (boundArgs @ [ tok ])

            let callee =
                match boundReceiver with
                | Some recv -> recv
                | None -> clrType

            let call = $"%s{callee}.%s{methodName}(%s{argList})"

            append c $"BjolangRuntime.TaskEvent<%s{payloadStr}>("

            if awaitIsVoid then
                // A non-generic `Task` has no result to hand back, so the
                // lambda has to await it and produce the unit itself. Every
                // other case hands the task straight over without an extra
                // state machine.
                append c $"async %s{tok} => {{ await %s{call}.ConfigureAwait(false); return default(%s{payloadStr}); }}"
            else
                append c $"%s{tok} => %s{call}"

            append c ")")

    | TSeq body ->
        // A C# iterator has to be a method, and a lambda cannot be one, so the
        // body becomes a local function and this node's value is a call to it.
        // Nothing is enumerated until that sequence is consumed.
        let iterator = freshName "__seq"

        indent ctx
        appendLine ctx $"%s{typeToString expr.Type} %s{iterator}() {{"
        // An iterator method returns `IEnumerable<T>`, and its terminal form is
        // `yield break` rather than a return — so `ReturnsVoid` is false and
        // `InSeq` is what the emitter actually consults.
        withIndent { ctx with Prelude = None; Loop = None; InSeq = true; ReturnsVoid = false } (fun c ->
            generateBlock c Effect body
            // Also what makes this an iterator at all when the body happens to
            // contain no `yield` — without one C# would read it as an ordinary
            // method that never returns a value.
            indent c
            appendLine c "yield break;")
        indent ctx
        appendLine ctx "}"

        emitTerminal ctx target expr.Type (fun c -> append c $"%s{iterator}()")

    | TYield value ->
        requireSeqScope ctx expr "yield"

        emitStatement ctx (fun c ->
            indent c
            append c "yield return "
            generateExpr c value
            appendLine c ";")

        dischargeVoid ctx target

    | TYieldFrom source ->
        requireSeqScope ctx expr "yield-from"

        // `foreach` rather than a bare re-yield: the elements have to be pulled
        // out one at a time and handed on individually, so that the consumer
        // sees one flat sequence and each source is disposed when it is done.
        let element = freshName "__yielded"

        emitStatement ctx (fun c ->
            indent c
            append c $"foreach (var %s{element} in "
            generateExpr c source
            appendLine c ") {")

        withIndent ctx (fun c ->
            indent c
            appendLine c $"yield return %s{element};")

        indent ctx
        appendLine ctx "}"

        dischargeVoid ctx target

    | TLetTuple (names, value, body) ->
        let tmp = freshName "__tuple"
        generateBindingValue ctx (DeclareAndAssign(typeToString value.Type, tmp)) value
        for i, name in List.indexed names do
            indent ctx
            appendLine ctx $"var %s{sanitizeIdent name} = %s{tmp}.Item%d{i + 1};"
        generateBlock ctx target body

    | TTryFinally (body, cleanup) ->
        // The declaration has to live outside the `try` or the assignment would
        // not be visible to anything following it.
        let bodyTarget =
            match target with
            | DeclareAndAssign (varType, varName) ->
                indent ctx; appendLine ctx $"%s{varType} %s{varName};"
                Assign varName
            | other -> other

        indent ctx; appendLine ctx "try {"
        withIndent ctx (fun c -> generateBlock c bodyTarget body)
        indent ctx; appendLine ctx "} finally {"
        // Cleanup runs for its effect and control leaves the `finally` on its
        // own; it must not try to break or return out of one.
        withIndent ctx (fun c -> generateBlock c Effect cleanup)
        indent ctx; appendLine ctx "}"

    | TVecMake items ->
        let elementTypeStr = elementTypeString expr.Type

        let builder = freshName "__vec"
        indent ctx; appendLine ctx $"var %s{builder} = new Collections.RrbBuilder<%s{elementTypeStr}>();"
        for item in items do
            emitStatement ctx (fun c ->
                indent c
                append c $"%s{builder}.Add("
                generateExpr c item
                appendLine c ");")
        emitTerminal ctx target expr.Type (fun c -> append c $"%s{builder}.ToImmutable()")

    | TIf (cond, t, f) ->
        let armTarget =
            match target with
            | DeclareAndAssign (varType, varName) ->
                indent ctx; appendLine ctx $"{varType} {varName};"
                Assign varName
            | other -> other

        emitStatement ctx (fun c ->
            indent c
            append c "if ("
            generateExpr c cond
            appendLine c ") {")
        withIndent ctx (fun c -> generateBlock c armTarget t)
        indent ctx; appendLine ctx "} else {"
        withIndent ctx (fun c -> generateBlock c armTarget f)
        indent ctx; appendLine ctx "}"

    | TWhen (cond, body, negated) ->
        emitStatement ctx (fun c ->
            indent c
            append c (if negated then "if (!(" else "if (")
            generateExpr c cond
            appendLine c (if negated then ")) {" else ") {"))

        // The body runs for its effect: whatever it evaluates to is discarded,
        // and control then continues after the `if`.
        withIndent ctx (fun c -> generateBlock c Effect body)
        indent ctx; appendLine ctx "}"

        // `when` yields void, like `set!`, so the enclosing target still has to
        // be discharged.
        dischargeVoid ctx target

    | TThrow msgExpr ->
        // A `throw` never reaches the declaration's use, but C# still wants the
        // variable to exist for the statements that follow.
        match target with
        | DeclareAndAssign (varType, varName) ->
            indent ctx; appendLine ctx $"%s{varType} %s{varName} = default!;"
        | _ -> ()

        emitStatement ctx (fun c ->
            indent c
            append c "throw new Exception("
            generateExpr c msgExpr
            appendLine c ");")

    | TMatch (matchTarget, clauses) -> generateMatch ctx target expr matchTarget clauses

    // Any node with no statement shape of its own: emit it as a C# expression
    // and let `emitTerminal` discharge the target. The `emitStatement` wrapper
    // supplies the hoisting buffer that `generateExpr` may need.
    | _ -> emitStatement ctx (fun c -> emitTerminal c target expr.Type (fun c2 -> generateExpr c2 expr))

/// Rejects a `yield` that did not end up inside the iterator method its `seq`
/// was emitted as.
///
/// Inference scopes `yield` lexically, but C# scopes it per *method*, and the
/// two disagree wherever a form inside a `seq` needs a method of its own: a
/// lambda, or a loop whose name escapes and so cannot be inlined. Emitting a
/// `yield return` there would produce C# that does not compile, with the error
/// pointing at generated code the author never wrote.
and private requireSeqScope (ctx: CodegenContext) (expr: TypedExpr) (formName: string) : unit =
    if not ctx.InSeq then
        codegenError
            expr.Range
            $"'%s{formName}' is inside a function of its own — a lambda, or a loop that is used as a value — rather than directly in the body of its (seq ...); move it into the sequence's own body"

/// Leaves an inlined loop, if that is what reaching this point means.
///
/// `break` binds to the nearest enclosing breakable statement. A `match` is
/// emitted as a `switch`, so from inside one a `break` leaves the switch and
/// drops back into the loop it was supposed to end — which is not a compile
/// error but an infinite loop. A `goto` to a label after the loop means the
/// same thing from any depth, so that is what a nested exit uses.
and private exitInlineLoop (ctx: CodegenContext) : unit =
    match ctx.Loop with
    | Some ({ IsInlineLoop = true } as loop) ->
        indent ctx

        // A merged group's body sits directly inside the group's own dispatch
        // `switch`, which is itself a breakable statement — so `break` there
        // leaves the switch and drops back into the `while` it was meant to end.
        // `NestedSwitches` cannot express this: `generateRecur` reads the same
        // field to decide whether `goto case` is legal, and for a merged loop it
        // *is*, so the count really is zero.
        if loop.NestedSwitches = 0 && not loop.Merged then
            appendLine ctx "break;"
        else
            loop.ExitLabelUsed.Value <- true
            appendLine ctx $"goto %s{loop.ExitLabel};"
    | _ -> ()

/// Discharges `target` after a form that has already emitted all of its own
/// statements and produced no value.
and private dischargeVoid (ctx: CodegenContext) (target: BlockTarget) : unit =
    match target with
    // Not terminal: the statements that follow still have to run.
    | Effect -> ()
    | Return -> emitReturnOfNothing ctx
    // The form produced no value, but a binding target still has to be filled:
    // these forms are `Unit`-typed, so what fills it is the unit.
    | Assign name ->
        indent ctx
        appendLine ctx $"%s{name} = %s{unitValue};"
        exitInlineLoop ctx
    | DeclareAndAssign (varType, varName) ->
        indent ctx
        appendLine ctx $"%s{varType} %s{varName} = %s{unitValue};"
        exitInlineLoop ctx
    | Discard -> exitInlineLoop ctx

/// Returns from a method whose body has just produced no value.
///
/// Three answers, and which one applies is a property of the enclosing C#
/// method rather than of the form that got here — which is why `InSeq` and
/// `ReturnsVoid` are carried down the context. The third is the interesting
/// one: the form yielded nothing, but the method still owes its caller a
/// `Unit`, and a unit is precisely a value that can be produced from nothing.
and private emitReturnOfNothing (ctx: CodegenContext) : unit =
    indent ctx

    appendLine ctx (
        if ctx.InSeq then "yield break;"
        elif ctx.ReturnsVoid then "return;"
        else $"return %s{unitValue};")

/// Discharges `target` with an already-formed C# expression fragment.
///
/// A void-typed value cannot be assigned or returned in C#, so under every
/// target that would bind it the value is emitted as a bare statement instead.
/// `Return` additionally needs a following `return;`, since the target still has
/// to be discharged.
and private emitTerminal (ctx: CodegenContext) (target: BlockTarget) (valueType: HMType) (emit: CodegenContext -> unit) : unit =
    let isVoid = isVoidType valueType

    indent ctx
    match target with
    | Effect ->
        // Not terminal, so no `break` and no `return`: whatever follows in the
        // enclosing block still has to run.
        if isVoid then
            emit ctx; appendLine ctx ";"
        else
            append ctx "_ = "; emit ctx; appendLine ctx ";"
    | Return ->
        if isVoid then
            // The value/statement mismatch, and one of the two places it is
            // resolved — the other being `dischargeVoid`, for forms that emit
            // their own statements. The expression yields nothing, but unless
            // the method itself returns `void` it still owes its caller a unit.
            emit ctx; appendLine ctx ";"
            emitReturnOfNothing ctx
        else
            append ctx "return "; emit ctx; appendLine ctx ";"
    | Assign name ->
        if isVoid then
            emit ctx; appendLine ctx ";"
        else
            append ctx $"%s{name} = "; emit ctx; appendLine ctx ";"
        exitInlineLoop ctx
    | DeclareAndAssign (varType, varName) ->
        if isVoid then
            emit ctx; appendLine ctx ";"
        else
            append ctx $"%s{varType} %s{varName} = "; emit ctx; appendLine ctx ";"
        exitInlineLoop ctx
    | Discard ->
        // C# has no expression statement for an arbitrary value, so a discarded
        // one is assigned to `_`. A void value is already a statement.
        if isVoid then
            emit ctx; appendLine ctx ";"
        else
            append ctx "_ = "; emit ctx; appendLine ctx ";"
        exitInlineLoop ctx

and private generateMatch
    (ctx: CodegenContext)
    (target: BlockTarget)
    (expr: TypedExpr)
    (matchTarget: TypedExpr)
    (clauses: TMatchClause list)
    : unit =

    // Emitted as a switch *statement* so that arms may contain statements,
    // produce void, or jump into the enclosing loop.
    let live = liveClauses clauses

    // C# only treats a switch statement as exhaustive when it has a `default`
    // section, and `case _:` is not legal syntax, so a trailing irrefutable
    // clause is emitted as the default section instead of a case.
    let irrefutableTail, cases =
        match List.rev live with
        | last :: revRest when fitsDefaultSection last -> Some last, List.rev revRest
        | _ -> None, live

    // `default:` carries no pattern, so an irrefutable `TPIdent` clause needs the
    // scrutinee hoisted into a local that it can alias.
    let needsTemp =
        match irrefutableTail with
        | Some c ->
            match c.Pattern.Node with
            | TPIdent _ -> true
            | _ -> false
        | None -> false

    let scrutinee =
        if needsTemp then
            let tmp = freshName "__match"
            emitStatement ctx (fun c ->
                indent c
                append c $"var %s{tmp} = "
                generateExpr c matchTarget
                appendLine c ";")
            Some tmp
        else
            None

    // A `goto case` binds to the nearest enclosing switch, so a jump from inside
    // this one has to route through the loop's discriminant instead.
    let inner =
        { ctx with
            Loop = ctx.Loop |> Option.map (fun l -> { l with NestedSwitches = l.NestedSwitches + 1 }) }

    let generateGuard (c: CodegenContext) (guard: TypedExpr) =
        if containsHoist guard then
            codegenError
                guard.Range
                "this `match` guard needs statements to evaluate, but C# gives `case ... when` no statement position; move the test into the arm body"

        append c " when "
        generateExpr { c with Prelude = None } guard

    let emitSwitch (armTarget: BlockTarget) =
        // A `Return` target always terminates the section itself
        // (return / continue / goto / throw), so a break would be unreachable.
        let emitBreak cb =
            match armTarget with
            | Return -> ()
            | _ -> indent cb; appendLine cb "break;"

        emitStatement ctx (fun c ->
            indent c
            append c "switch ("
            (match scrutinee with
             | Some tmp -> append c tmp
             | None -> generateExpr c matchTarget)
            appendLine c ") {")

        withIndent inner (fun c ->
            for clause in cases do
                indent c
                append c "case "
                generatePattern c clause.Pattern
                clause.Guard |> Option.iter (generateGuard c)
                // Each section gets its own block so locals declared by different
                // arms cannot collide in the shared switch scope.
                appendLine c ": {"
                withIndent c (fun cb ->
                    generateBlock cb armTarget clause.Body
                    emitBreak cb)
                indent c; appendLine c "}"

            indent c
            match irrefutableTail with
            | Some clause ->
                appendLine c "default: {"
                withIndent c (fun cb ->
                    match clause.Pattern.Node, scrutinee with
                    | TPIdent name, Some tmp ->
                        indent cb; appendLine cb $"var %s{sanitizeIdent name} = %s{tmp};"
                    | _ -> ()
                    generateBlock cb armTarget clause.Body
                    emitBreak cb)
                indent c; appendLine c "}"
            | None ->
                appendLine c $"default: throw new Exception(\"Match failure at %s{Lexer.formatPos expr.Range}\");")

        indent ctx; appendLine ctx "}"

    match target with
    | DeclareAndAssign (varType, varName) ->
        indent ctx; appendLine ctx $"{varType} {varName};"
        emitSwitch (Assign varName)
    | _ -> emitSwitch target

// ---------------------------------------------------------------------------
// Loops
// ---------------------------------------------------------------------------

and private generateRecur
    (ctx: CodegenContext)
    (target: BlockTarget)
    (expr: TypedExpr)
    (index: int)
    (args: TypedExpr list)
    : unit =

    let loop =
        match ctx.Loop with
        | Some l -> l
        | None ->
            codegenError expr.Range "internal error: a loop jump was emitted with no loop in scope"

    // A jump discards the enclosing block's remaining work. Under any target
    // (Return, Discard, Assign, DeclareAndAssign), the slot variables are updated
    // and the loop continues to the next iteration.

    let member_ = loop.Members[index]
    let slots = member_.Slots |> List.map (fst >> sanitizeIdent)

    if slots.Length <> args.Length then
        codegenError
            expr.Range
            $"internal error: jump to '%s{member_.LoopName}' carries %d{args.Length} arguments for %d{slots.Length} slots"

    // The whole vector is evaluated before any slot is written: an argument may
    // read a slot that an earlier assignment would already have overwritten.
    let temps = args |> List.map (fun _ -> freshName "__next")

    for arg, tmp in List.zip args temps do
        emitStatement ctx (fun c ->
            indent c
            append c $"var %s{tmp} = "
            generateExpr c arg
            appendLine c ";")

    for slot, tmp in List.zip slots temps do
        indent ctx; appendLine ctx $"%s{slot} = %s{tmp};"

    if loop.Merged then
        // `goto case` is a direct jump to another switch section rather than a
        // re-dispatch through the discriminant, so prefer it where it is legal.
        if loop.NestedSwitches = 0 then
            indent ctx; appendLine ctx $"goto case %d{index};"
        else
            indent ctx; appendLine ctx $"%s{loop.StateVar} = %d{index};"
            indent ctx; appendLine ctx "continue;"
    else
        indent ctx; appendLine ctx "continue;"

/// Copies each slot into a fresh per-iteration local. Done unconditionally: the
/// JIT elides the copy when nothing captures it, whereas an escape analysis
/// would be a correctness liability to maintain.
and private emitIterationCopies (ctx: CodegenContext) (member_: TLoopMember) : unit =
    for (slot, _), local in List.zip member_.Slots member_.Locals do
        indent ctx
        appendLine ctx $"var %s{sanitizeIdent local} = %s{sanitizeIdent slot};"

/// Emits `TLoop (_, None)`: the loop *is* this function's body, so the
/// `while (true)` lives in the function's own block and its slots are the
/// function's own parameters.
and private generateFunctionBody (ctx: CodegenContext) (body: TypedExpr) : unit =
    match body.Node with
    | TLoop ([ member_ ], None) ->
        indent ctx; appendLine ctx "while (true) {"
        withIndent ctx (fun c ->
            let inner =
                { c with
                    Loop = Some { Members = [ member_ ]; Merged = false; StateVar = ""; NestedSwitches = 0; IsInlineLoop = false; ExitLabel = ""; ExitLabelUsed = ref false } }

            emitIterationCopies inner member_
            generateBlock inner Return member_.Body)
        indent ctx; appendLine ctx "}"
    | _ -> generateBlock ctx Return body

/// Emits a `letrec` group as C# local functions.
and private generateLoopGroup (ctx: CodegenContext) (members: TLoopMember list) (body: TypedExpr) : unit =
    let targetsOf (m: TLoopMember) = LoopLowering.recurTargetsIn m.Body

    let jumpedTo =
        members |> List.fold (fun acc m -> Set.union acc (targetsOf m)) Set.empty

    // A jump between members is not a call, so a member the group's body never
    // names is only *entered* by its siblings — it needs no callable form. The
    // fixpoint drops members reachable solely from other unreachable ones.
    let called =
        let allNames = members |> List.map (fun m -> m.LoopName)

        let rec fix (live: Set<string>) =
            let referenced =
                members
                |> List.filter (fun m -> Set.contains m.LoopName live)
                |> List.fold
                    (fun acc m -> Set.union acc (LoopLowering.referencedNames m.Body))
                    (LoopLowering.referencedNames body)

            let next = allNames |> List.filter referenced.Contains |> Set.ofList
            if next = live then live else fix next

        fix (Set.ofList allNames)

    let hasCrossMemberJump =
        members
        |> List.mapi (fun i m -> targetsOf m |> Set.exists (fun j -> j <> i))
        |> List.exists id

    if members.Length > 1 && hasCrossMemberJump then
        generateMergedLoop ctx members called
    else
        for i, member_ in List.indexed members do
            // Nothing enters this member: emitting it would be dead code, and a
            // C# local function that is never used is a warning.
            if Set.contains member_.LoopName called then
                generateSingleLoop ctx members member_ (jumpedTo.Contains i)

and private generateSingleLoop
    (ctx: CodegenContext)
    (members: TLoopMember list)
    (member_: TLoopMember)
    (loops: bool)
    : unit =

    // Nothing jumps here, so the slot/local split has no purpose: the parameters
    // can simply carry the source's names.
    let paramNames = if loops then member_.Slots |> List.map fst else member_.Locals

    // A local loop introduces no type parameters of its own; it inherits the
    // enclosing method's. That also makes polymorphic recursion unrepresentable
    // rather than something to detect and reject.
    indent ctx
    append ctx (typeToString member_.RetType)
    append ctx " "
    append ctx (sanitizeIdent member_.LoopName)
    append ctx "("
    for i, ((_, slotType), paramName) in List.indexed (List.zip member_.Slots paramNames) do
        if i > 0 then append ctx ", "
        append ctx (typeToString slotType)
        append ctx " "
        append ctx (sanitizeIdent paramName)
    appendLine ctx ") {"

    withIndent ctx (fun c ->
        // A local function is a method of its own: it can neither jump into the
        // enclosing loop nor yield into the enclosing sequence.
        let inner =
            { c with
                InSeq = false
                ReturnsVoid = isVoidType member_.RetType
                Loop = Some { Members = members; Merged = false; StateVar = ""; NestedSwitches = 0; IsInlineLoop = false; ExitLabel = ""; ExitLabelUsed = ref false } }

        if loops then
            indent inner; appendLine inner "while (true) {"
            withIndent inner (fun c2 ->
                emitIterationCopies c2 member_
                generateBlock c2 Return member_.Body)
            indent inner; appendLine inner "}"
        else
            generateBlock inner Return member_.Body)

    indent ctx; appendLine ctx "}"

/// Emits a mutually recursive group as one local function whose parameters are
/// the union of the members' slots plus a state discriminant.
///
/// Switch sections are the right jump target: C# forbids jumping *into* a
/// lexical block, so plain labels would force every member's body into one flat
/// scope and require alpha-renaming all their locals to avoid collisions.
and private generateMergedLoop (ctx: CodegenContext) (members: TLoopMember list) (called: Set<string>) : unit =
    let first = List.head members
    let retStr = typeToString first.RetType

    // Only return types can disagree. Members cannot differ in type parameters:
    // a local binding is never generalized, so a loop introduces none of its own
    // (`TestFiles/probe/generic_local_rec.bjo`), and every member of a group
    // inherits the same enclosing method's set.
    for m in members do
        if typeToString m.RetType <> retStr then
            codegenError
                m.Body.Range
                $"'%s{first.LoopName}' and '%s{m.LoopName}' tail-call each other but return %s{retStr} and %s{typeToString m.RetType}; a merged loop has one return type, so split the group so that they do not tail-call each other"

    let groupName = freshName "__group"
    let stateVar = freshName "__state"
    let allSlots = members |> List.collect (fun m -> m.Slots)

    indent ctx
    append ctx retStr
    append ctx $" %s{groupName}(int %s{stateVar}"
    for (slotName, slotType) in allSlots do
        append ctx ", "
        append ctx (typeToString slotType)
        append ctx " "
        append ctx (sanitizeIdent slotName)
    appendLine ctx ") {"

    withIndent ctx (fun c ->
        indent c; appendLine c $"while (true) switch (%s{stateVar}) {{"
        withIndent c (fun cs ->
            for i, member_ in List.indexed members do
                indent cs; appendLine cs $"case %d{i}: {{"
                withIndent cs (fun cb ->
                    let inner =
                        { cb with
                            InSeq = false
                            ReturnsVoid = isVoidType member_.RetType
                            Loop = Some { Members = members; Merged = true; StateVar = stateVar; NestedSwitches = 0; IsInlineLoop = false; ExitLabel = ""; ExitLabelUsed = ref false } }

                    emitIterationCopies inner member_
                    generateBlock inner Return member_.Body)
                indent cs; appendLine cs "}"

            indent cs
            appendLine cs "default: throw new Exception(\"Unreachable loop state\");")
        indent c; appendLine c "}")

    indent ctx; appendLine ctx "}"

    // Entry wrappers keep each member callable — and passable as a value — from
    // outside the group. A member its siblings only ever *jump* to is reached
    // through the discriminant instead, and needs none.
    for i, member_ in List.indexed members do
        if Set.contains member_.LoopName called then
            let owned = member_.Slots |> List.map fst |> Set.ofList

            indent ctx
            append ctx retStr
            append ctx $" %s{sanitizeIdent member_.LoopName}("
            for j, (slotName, slotType) in List.indexed member_.Slots do
                if j > 0 then append ctx ", "
                append ctx (typeToString slotType)
                append ctx " "
                append ctx (sanitizeIdent slotName)
            append ctx $") => %s{groupName}(%d{i}"
            for (slotName, _) in allSlots do
                append ctx ", "
                append ctx (if owned.Contains slotName then sanitizeIdent slotName else "default!")
            appendLine ctx ");"

/// Emits the prologue that turns keyword parameters back into ordinary locals.
///
/// A keyword parameter whose default is not a constant arrives as an `Option`,
/// so that one omitted is distinguishable from one passed explicitly at its
/// default value — the callee has to know which, because it is the callee that
/// evaluates the default expression. That expression may be statement-shaped,
/// may read the parameters declared before it, and may have effects, so it is
/// emitted as a block rather than an expression.
///
/// A constant default needs none of that. The value is in the signature, C# has
/// already put it in place, and the two cases the `Option` exists to tell apart
/// have the same answer — so the parameter arrives as the declared type and the
/// prologue is the binding alone. `csharpConstantDefault` draws the line, and
/// `generateParameterList` emits the matching half of the signature; the choice
/// is made per parameter, so a function with one default of each kind gets one
/// of each here.
///
/// The keyword-free entry point has no parameter to ask about, so every default
/// is simply evaluated, in declaration order — which is the order they may read
/// each other in, and the order the general entry evaluates them in too.
///
/// A rest parameter needs a line here only when there are keyword parameters
/// beside it: that is the case where it is declared under `restParamName` so
/// that the call site can name it, and the body still wrote its own name.
///
/// Shared by methods and local functions, which take keyword arguments the same
/// way because they are written the same way.
and private generateArgumentPrologue
    (ctx: CodegenContext)
    (kwArgs: (string * HMType * TypedExpr) list)
    (restArg: (string * HMType) option)
    (entry: KeywordEntry)
    : unit =

    for (kwName, kwType, kwDefault) in kwArgs do
        let cType = typeToString kwType
        let sName = sanitizeIdent kwName
        let pName = keywordParamName kwName
        match entry, csharpConstantDefault kwType kwDefault with
        | KeywordDefaultsOnly, Some constant ->
            indent ctx
            appendLine ctx $"{cType} {sName} = {constant};"
        | KeywordDefaultsOnly, None ->
            indent ctx
            appendLine ctx $"{cType} {sName};"
            generateBlock ctx (Assign(sName)) kwDefault
        | KeywordParameters, Some _ ->
            // Copied to the name the body wrote rather than the parameter being
            // given that name outright: the parameter's name is the calling
            // convention and cannot be chosen to suit the body.
            indent ctx
            appendLine ctx $"{cType} {sName} = {pName};"
        | KeywordParameters, None ->
            indent ctx
            appendLine ctx $"{cType} {sName};"
            indent ctx
            appendLine ctx $"if ({pName}.IsSome) {{"
            withIndent ctx (fun c -> indent c; appendLine c $"{sName} = {pName}.Value;")
            indent ctx
            appendLine ctx "} else {"
            withIndent ctx (fun c -> generateBlock c (Assign(sName)) kwDefault)
            indent ctx
            appendLine ctx "}"

    match restArg with
    | Some (restName, restElemType) when not kwArgs.IsEmpty ->
        indent ctx
        appendLine ctx $"{typeToString restElemType}[] {sanitizeIdent restName} = {restParamName};"
    | _ -> ()

/// Emits a non-recursive local function.
///
/// A C# local function takes optional parameters and a `params` array just as a
/// method does, so a local `defun` is emitted with the same calling convention
/// a top-level one gets and the same emitter builds the parameter list.
and private generateLocalFunction
    (ctx: CodegenContext)
    (name: string)
    (fn: LocalFun)
    (argTypes: HMType list)
    (retType: HMType)
    (lambdaBody: TypedExpr)
    (funType: HMType)
    : unit =

    // The flat argument types are laid out mandatory, keyword, rest — the order
    // `Params` is in — so the mandatory ones are whatever is left at the front.
    let restCount = if fn.RestArg.IsSome then 1 else 0
    let mandatoryCount = fn.Params.Length - fn.KeywordArgs.Length - restCount

    let mandatory =
        List.zip fn.Params argTypes |> List.truncate mandatoryCount

    // `collectTypeVars` knows nothing about what is already in scope, so a local
    // function over `Vec<'a>` inside a method generic in `'a` would emit
    // `void f<T_a>(...)`, shadowing rather than unifying with the enclosing one.
    let typeParams =
        collectTypeVars funType
        |> List.distinct
        |> List.filter (fun v -> not (Set.contains (typeParamKey v) ctx.TypeParams))

    let tyParamsStr =
        if typeParams.IsEmpty then ""
        else "<" + (typeParams |> List.map typeParamName |> String.concat ", ") + ">"

    indent ctx
    append ctx (typeToString retType)
    append ctx " "
    append ctx (sanitizeIdent name)
    append ctx tyParamsStr
    append ctx "("
    // A local function gets no keyword-free twin: C# has no overloading for
    // local functions, two in one scope are a duplicate-name error, and a
    // distinct name would be one the call site has no way to know it should
    // write. It keeps the general entry, which is what it always had.
    generateParameterList ctx name mandatory fn.KeywordArgs fn.RestArg KeywordParameters
    appendLine ctx ") {"
    // A local function is a new function scope: it cannot jump into the
    // enclosing loop, nor yield into the enclosing sequence.
    withIndent
        { ctx with Loop = None; InSeq = false; ReturnsVoid = isVoidType retType }
        (fun c ->
            generateArgumentPrologue c fn.KeywordArgs fn.RestArg KeywordParameters
            generateBlock c Return lambdaBody)
    indent ctx
    appendLine ctx "}"

// ---------------------------------------------------------------------------
// Declarations
// ---------------------------------------------------------------------------

/// Emits a whole method: signature, the keyword-parameter unwrap prologue, and
/// the body. Shared by module-level functions and trait-`impl` methods, which
/// differ only in `modifier` and `genericParams`.
///
/// `ctx` must already carry the type parameters that are in scope: a module
/// function's are its own, an `impl` method's belong to the enclosing class.
let private generateMethod
    (ctx: CodegenContext)
    (modifier: string)
    (genericParams: string)
    (constraintClause: string)
    (name: string)
    (args: (string * HMType) list)
    (kwArgs: (string * HMType * TypedExpr) list)
    (restArg: (string * HMType) option)
    (retType: HMType)
    (effect: Effect)
    (body: TypedExpr)
    (entry: KeywordEntry)
    : unit =

    indent ctx
    append ctx modifier
    // `async` goes between the accessibility modifiers and the return type, so
    // it is appended to whatever the caller passed rather than prefixed to it.
    if effect = EAsync then append ctx "async "
    append ctx (returnTypeString effect retType)
    append ctx " "
    append ctx (sanitizeIdent name)
    append ctx genericParams
    append ctx "("
    generateParameterList ctx name args kwArgs restArg entry
    append ctx ")"
    // A `where` clause sits between the parameter list and the body, and is
    // where a CLR constraint ends up: no parameter, no dictionary, just a bound
    // on the type parameter that the runtime specializes against.
    append ctx constraintClause
    append ctx " {\n"
    // A method is where `ReturnsVoid` is first established; every nested scope
    // that opens a C# method of its own overrides it.
    //
    // A bjoroutine never returns void, whatever its payload: an `async
    // Fiber<Bjoml.Unit>` method owes its builder a `SetResult(value)`, so the
    // body has to produce a unit rather than fall off the end. That is the same
    // path a `(-> ... void)` ordinary function already takes.
    let ctx = { ctx with ReturnsVoid = (effect = ESync && isVoidType retType) }

    withIndent ctx (fun c ->
        generateArgumentPrologue c kwArgs restArg entry
        generateFunctionBody c body)
    indent ctx
    appendLine ctx "}"
    hiddenDirective ctx

// ---------------------------------------------------------------------------
// Materialization
// ---------------------------------------------------------------------------
//
// A trait implementation for a type this module declares is emitted *into* that
// type as the .NET member .NET asks for: `Eq` as `Equals`/`GetHashCode`, and
// `Ord` as `IComparable<T>` when it arrives.
//
// C# synthesizes those members only when they are not written, and a record's
// `==` calls `Equals`. So after this `EqualityComparer<T>.Default` *is* the
// implementation the program wrote, and `Map`, `Set`, `HashSet<T>`, `Distinct()`
// and every other .NET consumer agree with `=` — with no dictionary threaded
// anywhere and no signature changed.
//
// The orphan rule is what makes the lookup possible at all: an implementation
// for a type declared here is in this module too, so both are in hand at the
// moment the type is emitted.

/// The implementation of `traitName` for `typeKey` that can be materialized, if
/// there is one.
///
/// A *conditional* implementation cannot be. Its dictionary is built out of
/// evidence for its `(where ...)`, and a C# type parameter carries none — there
/// is nothing inside `Box<T>.Equals` that could produce the `Eq<T>` its own
/// implementation needs. Such a type keeps C#'s synthesized equality, which is
/// field-wise and therefore agrees with a derived implementation; only a
/// hand-written one that ignores a field can differ, and only for a generic
/// type.
let private materializableImpl (registry: TraitRegistry) (traitName: string) (typeKey: string) : bool =
    match Map.tryFind (traitName, typeKey) registry.ImplTargets with
    | Some target -> target.Constraints.IsEmpty
    | None -> false

/// What a materialized member is being written into. The three differ in what
/// C# would have synthesized in its place, which is what the replacement has to
/// match.
type private MaterializeTarget =
    /// A plain record. The synthesized `Equals(R?)` is virtual, because a
    /// record may be derived from, so the replacement has to be too.
    | OpenRecord
    /// A record struct. No null to guard against, and nothing virtual.
    | ValueRecord
    /// One case class of a union. Sealed, so `virtual` is an error; the
    /// `sealed override Equals(Base?)` the compiler still synthesizes is what
    /// routes a comparison at the base type through to here.
    | SealedCase

/// The members `traitName`'s implementation for `implKey` becomes.
///
/// `selfType` is the C# type they are written into — the *case* class for a
/// union — while `implKey` is the type the implementation was written for,
/// which is the union itself in both cases.
let private materializedMembers
    (traitName: string)
    (implKey: string)
    (selfType: string)
    (target: MaterializeTarget)
    : string list =

    let instance = $"%s{implClassName (sanitizeIdent traitName) implKey}.Instance"

    match traitName with
    | "Eq" ->
        // A reference type's `Equals` is handed `null` by .NET, which the
        // implementation — an ordinary Bjolang function over two values — has
        // no case for.
        let notNull = if target = ValueRecord then "" else "other is not null && "
        let param = if target = ValueRecord then $"%s{selfType} other" else $"%s{selfType}? other"
        let modifier = if target = OpenRecord then "public virtual " else "public "
        let hashMember = sanitizeIdent "eq-hash"

        [ $"%s{modifier}bool Equals(%s{param}) => %s{notNull}%s{instance}.eq(this, other);"
          $"public override int GetHashCode() => %s{instance}.%s{hashMember}(this);" ]
    | _ -> []

/// The trait implementations materialized into a type, in the order their
/// members are emitted. One list so that `Ord` is an entry rather than a second
/// pass.
let private materializedTraits = [ "Eq" ]

/// The body a declared type carries because of the implementations written for
/// it, or `[]` if it carries none.
let private materializedBody
    (registry: TraitRegistry)
    (implKey: string)
    (selfType: string)
    (target: MaterializeTarget)
    : string list =
    materializedTraits
    |> List.filter (fun t -> materializableImpl registry t implKey)
    |> List.collect (fun t -> materializedMembers t implKey selfType target)

/// `;` for a type with nothing to carry, or the block that carries it.
let private appendTypeBody (ctx: CodegenContext) (members: string list) : unit =
    if members.IsEmpty then
        appendLine ctx ";"
    else
        appendLine ctx " {"
        withIndent ctx (fun c ->
            for m in members do
                indent c
                appendLine c m)
        indent ctx
        appendLine ctx "}"

let rec generateDecl (ctx: CodegenContext) (decl: TDecl) : unit =
    match decl with
    | TDefun (name, tyArgs, args, kwArgs, restArg, retType, effect, body, _) ->
        let ctx = { ctx with TypeParams = tyArgs |> List.map typeParamKey |> Set.ofList }

        let genericParams =
            if tyArgs.IsEmpty then ""
            else
                let tyArgsStr = tyArgs |> List.map typeParamName |> String.concat ", "
                $"<%s{tyArgsStr}>"

        // Both entries below carry it: the keyword-free twin is a second
        // signature over the same type parameters, and C# requires the clause
        // on every declaration that introduces them.
        let constraintClause =
            Map.tryFind name ctx.ClrConstraints |> Option.defaultValue ""

        generateMethod ctx "public static " genericParams constraintClause name args kwArgs restArg retType effect body KeywordParameters

        // The keyword-free entry, as a C# overload of the same name. Emitted
        // only where it can win anything — a function all of whose defaults are
        // constants already carries them in its signature — because it is a
        // second copy of the body and not merely a second signature.
        //
        // The body is emitted rather than called: a wrapper would have to hand
        // the resolved values to a third method, and that method would need the
        // whole of `generateMethod`'s async, iterator and generic handling
        // repeated around a return type it no longer shares.
        if needsKeywordFreeEntry kwArgs then
            generateMethod ctx "public static " genericParams constraintClause name args kwArgs restArg retType effect body KeywordDefaultsOnly

    | TType (defs, _) 
    | TTypeRec (defs, _) ->
        for td in defs do
            let tyArgsStr = 
                if td.TypeArgs.IsEmpty then "" 
                else "<" + (td.TypeArgs |> List.map typeParamName |> String.concat ", ") + ">"
            // A type with a type parameter is left alone: an implementation for
            // one is conditional, and there is nothing inside the emitted type
            // that could build the dictionary its `(where ...)` asks for.
            let materialized selfType target =
                if td.TypeArgs.IsEmpty then
                    materializedBody ctx.Registry td.Name selfType target
                else
                    []

            match td.Kind with
            | Record(fields, isStruct) ->
                let selfType = sanitizeIdent td.Name
                let fieldType (f: Parser.RecordField) =
                    typeToString (Inference.resolveTypeAnnotation ctx.Registry f.Type)

                let members = materialized selfType (if isStruct then ValueRecord else OpenRecord)

                // A record with a mutable field is still a record — `with`,
                // `ToString` and the synthesized `Equals` are all wanted, and
                // all three are what a record has that a class does not. What
                // it cannot have is a *positional* parameter list, because a
                // primary constructor parameter of a record is init-only.
                //
                // So the whole field list moves into the body and the
                // constructor is written out. Doing it for every field rather
                // than only the mutable ones keeps construction positional and
                // in declaration order, which is what `TRecordMake` already
                // emits: splitting the list would have reordered the operands,
                // and evaluation order is not something to spend on a shorter
                // declaration.
                //
                // A `Struct` never reaches here with a mutable field — the
                // parser refuses the combination — so this is always the
                // reference-record path.
                if fields |> List.exists (fun f -> f.Mutable) then
                    let declarations =
                        fields
                        |> List.map (fun f ->
                            // `init` rather than a positional parameter for the
                            // immutable ones, so that `record-set`'s `with`
                            // still has something to assign.
                            if f.Mutable then $"public %s{fieldType f} %s{sanitizeIdent f.Name};"
                            else $"public %s{fieldType f} %s{sanitizeIdent f.Name} {{ get; init; }}")

                    let parameters =
                        fields
                        |> List.map (fun f -> $"%s{fieldType f} %s{sanitizeIdent f.Name}")
                        |> String.concat ", "

                    let assignments =
                        fields
                        |> List.map (fun f -> $"this.%s{sanitizeIdent f.Name} = %s{sanitizeIdent f.Name};")
                        |> String.concat " "

                    let constructor =
                        $"public %s{selfType}(%s{parameters}) {{ %s{assignments} }}"

                    // The hash a mutable record does not have.
                    //
                    // Materialization already writes one where there is an
                    // unconditional `Eq` implementation to write it from, and
                    // for a derived implementation that one throws too. This is
                    // the other two cases: a record with no implementation at
                    // all, and — the one that matters — every *generic* record,
                    // which materialization skips because there is nothing
                    // inside the type that could build the dictionary a
                    // conditional implementation asks for.
                    //
                    // Without it C# synthesizes a hash over all instance
                    // fields, the mutable one included, and a `Map` or a `Set`
                    // uses it without a word: the entry is simply lost the next
                    // time the field is written. Throwing needs no dictionary,
                    // so it reaches where materialization cannot.
                    //
                    // `Equals` is deliberately left alone. C#'s synthesized one
                    // compares every instance field, which is exactly what `=`
                    // on a mutable record means, so the two already agree.
                    let hashed =
                        if members |> List.exists (fun m -> m.Contains "GetHashCode") then
                            []
                        else
                            let shown = escapeStringLiteral (Naming.showTypeName td.Name)

                            [ $"public override int GetHashCode() => throw new System.InvalidOperationException(\"%s{shown} has a mutable field, so it has no stable hash: it cannot be a Map or Set key. Compare it with = instead, or write an Eq implementation whose eq-hash reads only the immutable fields.\");" ]

                    indent ctx
                    append ctx $"public record %s{selfType}%s{tyArgsStr}"
                    appendTypeBody ctx (declarations @ [ constructor ] @ members @ hashed)
                else
                    indent ctx
                    let kind = if isStruct then "record struct" else "record"
                    append ctx $"public %s{kind} %s{selfType}%s{tyArgsStr}("
                    for i, f in List.indexed fields do
                        if i > 0 then append ctx ", "
                        append ctx (fieldType f)
                        append ctx " "
                        append ctx (sanitizeIdent f.Name)
                    append ctx ")"
                    appendTypeBody ctx members
            | Union cases ->
                indent ctx
                appendLine ctx $"public abstract record %s{sanitizeIdent td.Name}%s{tyArgsStr} {{"
                withIndent ctx (fun ctx ->
                    indent ctx
                    appendLine ctx $"private %s{sanitizeIdent td.Name}() {{}}"

                    // Into every case class, and not onto the abstract base: a
                    // derived record synthesizes its own `Equals`, which
                    // overrides the base's, so an override written only on the
                    // base would be silently dead. Declaring `Equals(Case?)` is
                    // what displaces it — the `sealed override Equals(Base?)`
                    // the compiler still synthesizes calls through to it.
                    for c in cases do
                        indent ctx
                        match c with
                        | SimpleCase (n, _) ->
                            append ctx $"public sealed record %s{sanitizeIdent n}() : %s{sanitizeIdent td.Name}%s{tyArgsStr}"
                            appendTypeBody ctx (materialized (sanitizeIdent n) SealedCase)
                        | DataCase (n, ftypes, _, _) ->
                            append ctx $"public sealed record %s{sanitizeIdent n}("
                            for i, ft in List.indexed ftypes do
                                if i > 0 then append ctx ", "
                                append ctx (typeToString (Inference.resolveTypeAnnotation ctx.Registry ft))
                                append ctx $" Item%d{i+1}"
                            append ctx $") : %s{sanitizeIdent td.Name}%s{tyArgsStr}"
                            appendTypeBody ctx (materialized (sanitizeIdent n) SealedCase)
                )
                indent ctx
                appendLine ctx "}"
            | Alias _ -> ()
            // An opaque declaration is never one of this module's own: it is
            // how an `#:opaque` type arrives from a dependency, whose assembly
            // already holds the emitted class. Emitting one here would be a
            // second type of the same name.
            | Opaque _ -> ()

    // An inline trait emits nothing at all. There is no valid C# interface for
    // `Monad<M>`: the parameter would have to be a type constructor.
    | TTrait (_, _, InlineTrait, _, _, _, _) -> ()

    // A trait that stands for a .NET interface emits no interface of its own —
    // the one it names already exists, and a second of the same shape would be
    // a different type that no .NET value implements.
    //
    // What it does emit is one generic method per declared member, constrained
    // by the interface. Every call goes through these, concrete or not, because
    // a constrained type parameter is the only place *every* member is
    // reachable: `int.Abs(x)` compiles, `int.IsZero(x)` and `byte.Abs(x)` do
    // not, those being explicit implementations. Written this way the question
    // never arises, and the JIT inlines the whole thing.
    | TTrait (name, targetVar, _, _, _, signatures, _) when
        (match Map.tryFind name ctx.Registry.Traits with
         | Some info -> info.ClrConstraint.IsSome
         | None -> false)
        ->
        let info = ctx.Registry.Traits[name]
        let clr = info.ClrConstraint.Value

        let implVar = "'" + targetVar
        let implParam = typeParamName implVar

        // The signatures are written over the implementor variable, so naming
        // the helper's type parameter after it makes them render as they stand.
        let applied =
            if clr.Args.IsEmpty then
                clr.InterfaceName
            else
                let argsStr = clr.Args |> List.map typeToString |> String.concat ", "
                $"%s{clr.InterfaceName}<%s{argsStr}>"

        indent ctx
        appendLine ctx $"public static class %s{clrHelperClassName name} {{"

        withIndent ctx (fun ctx ->
            for KeyValue(mName, mType) in signatures do
                match Map.tryFind mName clr.Members, mType with
                | Some binding, TFun(argTypes, retType, _) ->
                    // A method may be generic in its own right; anything beyond
                    // the implementor is a type parameter of the helper too.
                    let extraVars =
                        (argTypes @ [ retType ])
                        |> List.collect collectTypeVars
                        |> List.distinct
                        |> List.filter (fun v -> typeParamKey v <> typeParamKey implVar)

                    let tyParams =
                        (implParam :: (extraVars |> List.map typeParamName)) |> String.concat ", "

                    let ctx = { ctx with TypeParams = (implVar :: extraVars) |> List.map typeParamKey |> Set.ofList }

                    let parameters =
                        argTypes
                        |> List.mapi (fun i t -> $"%s{typeToString t} a%d{i}")
                        |> String.concat ", "

                    let arguments = argTypes |> List.mapi (fun i _ -> $"a%d{i}")

                    // A static member is named on the type parameter; an
                    // instance one takes its receiver from the first argument,
                    // which a method over the implementor has anyway.
                    let call =
                        if binding.IsStatic then
                            let allArgs = String.concat ", " arguments
                            $"%s{implParam}.%s{binding.MemberName}(%s{allArgs})"
                        else
                            match arguments with
                            | receiver :: rest ->
                                let restArgs = String.concat ", " rest
                                $"%s{receiver}.%s{binding.MemberName}(%s{restArgs})"
                            | [] ->
                                failwithf
                                    $"Type Error: '%s{mName}' is an instance member of '%s{clr.InterfaceName}', so it needs a receiver — it must take at least one argument."

                    indent ctx
                    appendLine ctx
                        $"public static %s{typeToString retType} %s{sanitizeIdent mName}<%s{tyParams}>(%s{parameters}) where %s{implParam} : %s{applied} => %s{call};"
                | _ -> ())

        indent ctx
        appendLine ctx "}"

    | TTrait (name, targetVar, _, _, assocTypes, signatures, _) ->
        // Helper to collect all TVar names from a type
        let rec collectTVars t =
            match t with
            | TVar v -> [v]
            | TCon(_, args) -> List.collect collectTVars args
            | TFun(args, ret, _) -> (List.collect collectTVars args) @ collectTVars ret
            | TTuple args -> List.collect collectTVars args
            | TAssoc(_, _, impl) -> collectTVars impl
            | _ -> []

        indent ctx
        // Class-level type params: the implementor var + associated types
        let classTyParamsList = targetVar :: assocTypes
        let tyParams = classTyParamsList |> List.map typeParamName |> String.concat ", "
        appendLine ctx $"public interface %s{sanitizeIdent name}<%s{tyParams}> {{"
        withIndent ctx (fun ctx ->
            // The raw trait signature uses unprimed names (e.g. "col"),
            // but the TVars in the resolved HMType are primed (e.g. "'col").
            let classTyVarNames = classTyParamsList |> List.map (fun v -> "'" + v)
            for kvp in signatures do
                let mName = kvp.Key
                let mType = kvp.Value
                // Method-level generics: TVars in this method that aren't class-level
                let methodVars =
                    collectTVars mType
                    |> List.distinct
                    |> List.filter (fun v -> not (List.contains v classTyVarNames))
                let methodTyParamsStr =
                    if methodVars.IsEmpty then ""
                    else "<" + (methodVars |> List.map typeParamName |> String.concat ", ") + ">"

                match mType with
                | TFun (args, ret, _) ->
                    indent ctx
                    append ctx (typeToString ret)
                    append ctx " "
                    append ctx (sanitizeIdent mName)
                    append ctx methodTyParamsStr
                    append ctx "("
                    for i, arg in List.indexed args do
                        if i > 0 then append ctx ", "
                        append ctx (typeToString arg)
                        append ctx $" arg%d{i}"
                    appendLine ctx ");"
                | _ -> () // Should be function
        )
        indent ctx
        appendLine ctx "}"

    | TImpl (traitName, kind, holeArity, targetType, assocMap, dictFields, methods, _) ->
        // A blanket impl's target is a bare type variable, which becomes the
        // class's one type parameter: `Discard_Blanket<T_a> : Discard<T_a>`.
        let targetTypeName = implCtorKey targetType |> Option.defaultValue "Unknown"

        let sanitizedTraitName = sanitizeIdent traitName
        let className = implClassName sanitizedTraitName targetTypeName

        // The class's type parameters are the impl's *fixed prefix*. For an
        // interface trait that is the whole target; for an inline trait the
        // trailing `holeArity` arguments belong to the trait's constructor
        // variable, so they are the method's business rather than the class's.
        let targetArgs =
            match targetType with
            | TCon(_, args) -> args
            // A tuple's elements are its arguments, as a constructor's are.
            | TTuple args -> args
            // The implementor *is* the argument for a blanket. Left as `[]`,
            // the class would take no type parameter and its `Instance` field
            // and interface clause would both name an undeclared `T_a`.
            | TVar _ -> [ targetType ]
            | _ -> []

        let prefixArgs = targetArgs |> List.truncate (max 0 (targetArgs.Length - holeArity))

        let typeParamVars =
            prefixArgs |> List.collect collectTypeVars |> List.distinct

        let tyParamsStr =
            if typeParamVars.IsEmpty then ""
            else "<" + (typeParamVars |> List.map typeParamName |> String.concat ", ") + ">"

        // The class's own type parameters are in scope in every method body.
        let ctx = { ctx with TypeParams = typeParamVars |> List.map typeParamKey |> Set.ofList }

        let baseClause =
            match kind with
            | InlineTrait -> ""
            | InterfaceTrait ->
                let targetTypeStr = typeToString targetType
                let assocArgsStr =
                    assocMap
                    |> List.map (fun (_, t) -> typeToString t)
                    |> String.concat ", "
                let traitArgsStr =
                    if String.IsNullOrEmpty(assocArgsStr) then targetTypeStr
                    else $"%s{targetTypeStr}, %s{assocArgsStr}"
                $" : %s{sanitizedTraitName}<%s{traitArgsStr}>"

        indent ctx
        appendLine ctx $"public sealed class %s{className}%s{tyParamsStr}%s{baseClause} {{"
        withIndent ctx (fun ctx ->
            // An inline trait has no interface to satisfy, so there is nothing
            // for a singleton to be an instance *of*: its landing pads are plain
            // static methods.
            //
            // A conditional impl has no singleton either, for a different
            // reason: it holds the dictionaries its `(where ...)` demands, and
            // those differ per instantiation — `ToStr_List<int>` needs the `int`
            // one — so there is no single value to share. It gets the fields, a
            // constructor and a factory instead, and every dispatch site builds
            // one. The allocation is a sealed object of exactly known type, so
            // the interface call through it stays devirtualizable.
            match kind, dictFields with
            | InterfaceTrait, [] ->
                indent ctx
                appendLine ctx $"public static readonly %s{className}%s{tyParamsStr} Instance = new();"
            | InterfaceTrait, _ ->
                let parameters =
                    dictFields
                    |> List.mapi (fun i (_, t) -> $"%s{typeToString t} d%d{i}")
                    |> String.concat ", "

                let arguments =
                    dictFields |> List.mapi (fun i _ -> $"d%d{i}") |> String.concat ", "

                for (name, t) in dictFields do
                    indent ctx
                    appendLine ctx $"private readonly %s{typeToString t} %s{sanitizeIdent name};"

                indent ctx
                appendLine ctx $"public %s{className}(%s{parameters}) {{"

                withIndent ctx (fun ctx ->
                    for i, (name, _) in List.indexed dictFields do
                        indent ctx
                        appendLine ctx $"this.%s{sanitizeIdent name} = d%d{i};")

                indent ctx
                appendLine ctx "}"

                // Named rather than left to `new`, so that `Lowering` can spell
                // the whole thing as one `Class::Make` identifier and let the
                // existing `::` path insert the class's type arguments.
                indent ctx
                appendLine ctx
                    $"public static %s{className}%s{tyParamsStr} Make(%s{parameters}) => new %s{className}%s{tyParamsStr}(%s{arguments});"

                // The dictionary for this very implementation, which a method
                // recursing at its own target type needs: `this`, under the
                // name `Lowering` refers to it by. A property rather than a
                // field, so it costs nothing to hold.
                let selfName = sanitizeIdent (Lowering.selfDictName traitName)
                let interfaceStr = baseClause.Substring(3)

                indent ctx
                appendLine ctx $"private %s{interfaceStr} %s{selfName} => this;"
            | InlineTrait, _ -> ()

            let modifier =
                match kind with
                | InterfaceTrait -> "public "
                | InlineTrait -> "public static "

            for m in methods do
                match m with
                | TDefun (n, tyArgs, args, kwArgs, restArg, retType, effect, body, _) ->
                    // Whatever is left over after the class's own parameters is
                    // a method-level generic and must be emitted as one. This is
                    // exactly the restriction inline traits lift: `bind`'s `'b`
                    // belongs to the method, not to the trait's target.
                    let classKeys = typeParamVars |> List.map typeParamKey |> Set.ofList
                    let methodOnlyTyArgs =
                        tyArgs |> List.filter (fun v -> not (Set.contains (typeParamKey v) classKeys))
                    let methodTyArgsStr =
                        if methodOnlyTyArgs.IsEmpty then ""
                        else "<" + (methodOnlyTyArgs |> List.map typeParamName |> String.concat ", ") + ">"
                    // Include method-level type params in scope
                    let methodCtx =
                        { ctx with TypeParams = Set.union ctx.TypeParams (methodOnlyTyArgs |> List.map typeParamKey |> Set.ofList) }
                    // No twin for a trait-`impl` method: a keyword parameter on
                    // one is rejected before it reaches here, and an overload
                    // would in any case have no interface declaration to match.
                    generateMethod methodCtx modifier methodTyArgsStr "" n args kwArgs restArg retType effect body KeywordParameters
                | _ -> ()
        )
        indent ctx
        appendLine ctx "}"

    | TModule (name, decls, _) ->
        let isOuterDecl = function
            | TType _ | TTypeRec _ | TTrait _ | TImpl _ -> true
            | _ -> false

        for d in decls |> List.filter isOuterDecl do
            generateDecl ctx d

        let innerDecls = decls |> List.filter (not << isOuterDecl)

        // A static field initializer cannot contain statements, so module values
        // become static fields assigned by a static constructor. That is the last
        // place an IIFE would otherwise still be required.
        //
        // The three shapes are collected into *one* list in declaration order
        // rather than swept up a kind at a time, because the static constructor
        // assigns them in this order and one initializer may read a binding
        // above it. Taken kind by kind, a `def` reading a `def/mutable` declared
        // before it would have seen the field's default instead of its value —
        // silently, since C# is perfectly happy to read a zeroed static.
        let valueDefs =
            innerDecls
            |> List.choose (function
                | TDef(n, v, t, _) -> Some(Choice1Of3(n, v, t))
                | TDefMutable(n, v, t, _) -> Some(Choice2Of3(n, v, t))
                | TDefTuple(names, v, t, _) -> Some(Choice3Of3(names, v, t))
                | _ -> None)

        let className = moduleClassName name

        indent ctx
        appendLine ctx $"public static class %s{className} {{"
        withIndent ctx (fun ctx ->
            // Emit factory methods for union cases
            for d in decls |> List.filter isOuterDecl do
                match d with
                | TType (defs, _) | TTypeRec (defs, _) ->
                    for td in defs do
                        let tyArgsStr = 
                            if td.TypeArgs.IsEmpty then "" 
                            else "<" + (td.TypeArgs |> List.map typeParamName |> String.concat ", ") + ">"
                        match td.Kind with
                        | Union cases ->
                            for c in cases do
                                match c with
                                | SimpleCase (n, _) ->
                                    indent ctx
                                    appendLine ctx $"public static %s{sanitizeIdent td.Name}%s{tyArgsStr} %s{sanitizeIdent n}%s{tyArgsStr}() => new %s{sanitizeIdent td.Name}%s{tyArgsStr}.%s{sanitizeIdent n}();"
                                | DataCase (n, ftypes, _, _) ->
                                    indent ctx
                                    append ctx $"public static %s{sanitizeIdent td.Name}%s{tyArgsStr} %s{sanitizeIdent n}%s{tyArgsStr}("
                                    for i, ft in List.indexed ftypes do
                                        if i > 0 then append ctx ", "
                                        append ctx (typeToString (Inference.resolveTypeAnnotation ctx.Registry ft))
                                        append ctx $" arg{i}"
                                    let argsListStr = String.concat ", " [for i in 0 .. ftypes.Length - 1 -> $"arg{i}"]
                                    appendLine ctx $") => new %s{sanitizeIdent td.Name}%s{tyArgsStr}.%s{sanitizeIdent n}(%s{argsListStr});"
                        | _ -> ()
                | _ -> ()

            // `readonly` for everything but a `def/mutable`, which `set!` has to
            // be able to write.
            let tupleElemTypes (tupleType: HMType) =
                match tupleType with
                | TTuple ts -> ts
                | _ ->
                    failwithf $"Expected a tuple in this binding but got %s{DotNetInterop.showType tupleType}"

            for d in valueDefs do
                match d with
                | Choice1Of3(defName, _, defType) ->
                    indent ctx
                    appendLine ctx $"public static readonly %s{typeToString defType} %s{sanitizeIdent defName};"
                | Choice2Of3(defName, _, defType) ->
                    indent ctx
                    appendLine ctx $"public static %s{typeToString defType} %s{sanitizeIdent defName};"
                | Choice3Of3(names, _, tupleType) ->
                    for name, elemType in List.zip names (tupleElemTypes tupleType) do
                        indent ctx
                        appendLine ctx $"public static readonly %s{typeToString elemType} %s{sanitizeIdent name};"

            for d in innerDecls do
                match d with
                | TDef _
                | TDefMutable _
                | TDefTuple _ -> ()
                | _ -> generateDecl ctx d

            if not valueDefs.IsEmpty then
                indent ctx
                appendLine ctx $"static %s{className}() {{"

                withIndent ctx (fun c ->
                    for d in valueDefs do
                        match d with
                        | Choice1Of3(defName, defValue, _)
                        | Choice2Of3(defName, defValue, _) ->
                            generateBlock c (Assign(sanitizeIdent defName)) defValue
                        | Choice3Of3(names, defValue, _) ->
                            let tmp = freshName "__tuple"
                            generateBindingValue c (DeclareAndAssign(typeToString defValue.Type, tmp)) defValue

                            for i, name in List.indexed names do
                                indent c
                                appendLine c $"%s{sanitizeIdent name} = %s{tmp}.Item%d{i + 1};")

                indent ctx
                appendLine ctx "}"
        )
        indent ctx
        appendLine ctx "}"

    | _ -> ()


/// `metadataDeps` is recorded in the assembly for downstream compilations to
/// link against; it is empty for an executable, which nothing links to.
/// `linkedDlls` is every assembly this compilation references, and each one
/// contributes a `using static` so that names re-exported through one DLL can
/// still be found in the class that actually defines them.
/// The cases of the builtin unions — `Syntax` and `CancelReason` — which are
/// defined in the runtime rather than declared by any `def/type`.
///
/// Seeded rather than collected, since there is no declaration anywhere to read
/// them off. With this in place the generic pattern and construction paths
/// handle them exactly as they handle a union the program declared —
/// `Bjolang.Runtime.Syntax.SList(var xs)` and `new Bjolang.Runtime.Syntax.SList(xs)`
/// — and no special case is needed.
let builtinUnionCases: Map<string, UnionCaseInfo> =
    let syntaxCases =
        [ "SSym"; "SDatum"; "SInt"; "SStr"; "SChar"; "SKey"; "SList"; "SPunct" ]
        |> List.map (fun name -> name, { ParentTypeName = "Syntax"; IsDataCase = true })

    // `Deadline` and `Scope-Ended` carry nothing, so they are *values* rather
    // than calls and take the nullary path — which is what `IsDataCase = false`
    // selects.
    let cancelReasonCases =
        [ "Requested", true; "Deadline", false; "Scope-Ended", false; "Failed", true ]
        |> List.map (fun (name, isData) ->
            name, { ParentTypeName = "CancelReason"; IsDataCase = isData })

    syntaxCases @ cancelReasonCases |> Map.ofList

/// The names that arrive already bound, from `using static BjolangRuntime`.
///
/// A module may declare a binding of its own called `list`, and an importer
/// then has two of that name in scope with nothing in the identifier to say
/// which. Read off `Prelude` rather than listed here: the builtins are what
/// that environment *is*, and a second copy would go stale the first time one
/// was added.
let private builtinBindings: Set<string> =
    Prelude.prelude.Bindings |> Map.toSeq |> Seq.map fst |> Set.ofSeq

/// The C# `where` clauses a function's CLR constraints amount to.
///
/// `""` when it has none, which is every function in a program that never
/// writes one — so this is also what keeps the emitted C# byte-identical for
/// everything that came before.
let private clrConstraintClauses (env: Env) : Map<string, string> =
    env.Bindings
    |> Map.toSeq
    |> Seq.choose (fun (name, binding) ->
        let (Scheme(_, constraints, _)) = binding.Scheme

        let bounds =
            constraints
            |> List.choose (fun c ->
                match Map.tryFind c.TraitName env.Registry.Traits with
                | Some info ->
                    info.ClrConstraint
                    |> Option.bind (fun clr ->
                        match c.TargetType with
                        | TVar _ as target ->
                            // The interface is written over the trait's own
                            // implementor variable; the constraint says which
                            // of *this* function's variables stands in for it.
                            let subst = Map.ofList [ "'" + info.ImplementorVar, target ]
                            let args = clr.Args |> List.map (substTypeVars subst)

                            let applied =
                                if args.IsEmpty then
                                    clr.InterfaceName
                                else
                                    let argsStr = args |> List.map typeToString |> String.concat ", "
                                    $"%s{clr.InterfaceName}<%s{argsStr}>"

                            Some(typeToString target, applied)
                        | _ -> None)
                | None -> None)

        // Grouped by type parameter, because C# takes one `where` per parameter
        // with its bounds comma-separated and rejects a second clause for the
        // same one. A variable with two constraints is ordinary — `(+ a b)`
        // beside `(< a b)` is `INumber` and `IComparisonOperators` — so this is
        // the common case rather than a corner.
        let clauses =
            bounds
            |> List.groupBy fst
            |> List.map (fun (param, group) ->
                let all = group |> List.map snd |> List.distinct |> String.concat ", "
                $" where %s{param} : %s{all}")

        if clauses.IsEmpty then None else Some(name, String.concat "" clauses))
    |> Map.ofSeq

let generateProgram (env: Env) (metadata: ModuleMetadata.Metadata) (linkedDlls: string list) (decls: TDecl list) : string =
    let registry = env.Registry

    let unionCases =
        decls
        |> collectDecls (function
            | TType (defs, _) | TTypeRec (defs, _) ->
                defs |> List.collect (fun td ->
                    match td.Kind with
                    | Union cases ->
                        cases |> List.map (fun c ->
                            match c with
                            | SimpleCase (name, _) -> name, { ParentTypeName = td.Name; IsDataCase = false }
                            | DataCase (name, _, _, _) -> name, { ParentTypeName = td.Name; IsDataCase = true }
                        )
                    | _ -> []
                )
            | _ -> []
        )
        // A declared case is keyed by the module that declared it, so it cannot
        // land on one of the builtin names here — a program's own `SList` is
        // `main__SList`. The two sets merge rather than compete.
        |> List.fold (fun acc (n, info) -> Map.add n info acc) builtinUnionCases

    // Where each top-level name is emitted from, and under what member name.
    //
    // A plain import is deliberately absent: it resolves through the
    // `using static` for its module, as it always has. What is here is what a
    // bare identifier cannot express — a name whose spelling differs from the
    // member it stands for, which is every alias and every import brought in
    // under a modifier.
    let definitions =
        decls
        |> collectDecls (function
            | TModule (modName, innerDecls, _) ->
                innerDecls |> List.collect (function
                    | TDef (n, _, _, _) -> [ (n, (modName, n)) ]
                    | TDefMutable (n, _, _, _) -> [ (n, (modName, n)) ]
                    | TDefTuple (names, _, _, _) -> names |> List.map (fun n -> (n, (modName, n)))
                    | TDefun (n, _, _, _, _, _, _, _, _) -> [ (n, (modName, n)) ]
                    // An import named after a builtin. Both spellings reach the
                    // call site through a `using static` — the builtin's from
                    // `BjolangRuntime`, this one's from the class that defines
                    // it — and C# resolves a name two static imports provide to
                    // neither, so `(list-length list)` on an imported `list`
                    // used to be a CS0411 in generated code. A bare identifier
                    // cannot say which is meant; the class name can.
                    //
                    // Unconditionally, rather than only where the name also
                    // moved: the branch below reads "differs from what a bare
                    // identifier would find", and for these a bare identifier
                    // finds the wrong thing even when nothing differs.
                    | TExtern (visible, origin, _, _) when Set.contains visible builtinBindings ->
                        [ (visible, (origin.OriginModule, origin.OriginalName)) ]
                    // An import whose spelling or whose home differs from what a
                    // bare identifier would find: a modifier renamed it, or the
                    // module publishing it was a facade and generated no code
                    // for it at all.
                    | TExtern (visible, origin, _, _) when
                        visible <> origin.OriginalName || origin.OriginModule <> modName
                        ->
                        [ (visible, (origin.OriginModule, origin.OriginalName)) ]
                    | _ -> []
                )
            | _ -> []
        )
        |> Map.ofList

    // Aliases last, and resolved against the definitions: an alias whose
    // origin module is unknown to inference is one of this program's own
    // bindings, or a builtin with no class to name.
    let globalBindings =
        decls
        |> collectDecls (function
            | TAlias (visible, Some resolution, _) ->
                let target =
                    if resolution.OriginModule <> "" then
                        resolution.OriginModule, resolution.OriginalName
                    else
                        match Map.tryFind resolution.OriginalName definitions with
                        | Some found -> found
                        | None -> "", resolution.OriginalName

                [ (visible, target) ]
            | _ -> []
        )
        |> List.fold (fun acc (n, target) -> Map.add n target acc) definitions

    let ctx =
        { Builder = StringBuilder()
          IndentLevel = 0
          UnionCases = unionCases
          GlobalBindings = globalBindings
          Prelude = None
          Loop = None
          TypeParams = Set.empty
          InSeq = false
          Registry = registry
          ClrConstraints = clrConstraintClauses env
          ReturnsVoid = false }

    appendLine ctx "using System;"
    appendLine ctx "using static BjolangRuntime;"
    
    // Emit 'using static' for all modules to allow unqualified access. A module
    // reached both directly and through another module's import would otherwise
    // be named twice, which C# warns about.
    let moduleUsings =
        [ for decl in decls do
            match decl with
            | TModule (name, innerDecls, _) ->
                yield moduleClassName name
                for inner in innerDecls do
                    match inner with
                    | TImport (specs, _) ->
                        for spec in specs do
                            match spec.Path with
                            | RelativePath p -> yield moduleClassName p
                            | ModulePath parts ->
                                yield moduleClassName (List.last parts)
                    | _ -> ()
            | _ -> ()

          // Every linked assembly, including ones reached only transitively.
          // A name re-exported through one DLL is compiled as an unqualified
          // reference, so the class that actually defines it has to be in
          // scope even though its module was never imported.
          for dllPath in linkedDlls do
              yield moduleClassName dllPath ]
        |> List.distinct

    for className in moduleUsings do
        appendLine ctx $"using static %s{className};"
        
    // Backslashes are doubled *first*. Escaping only the quotes turned a `\"`
    // already inside the metadata — which an inline template body carries as
    // soon as it mentions a string literal — into `\\"`, closing the C# literal
    // early and producing source that does not parse.
    //
    // A carriage return is escaped rather than dropped. Dropping one changed
    // the length of a string the metadata format counts characters of, so a
    // body containing `\r` made every value after it read at the wrong offset.
    let escapeAttribute (s: string) =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")

    // One attribute for all of it. The parts have different readers on the far
    // side — a macro entry is read before the importing module is parsed, a
    // signature long after — but they are written together because they are
    // one version of one thing, and a reader that found three of four would
    // have no way to say so.
    if not (ModuleMetadata.isEmpty metadata) then
        let escaped = escapeAttribute (ModuleMetadata.serialize metadata)
        appendLine ctx $"[assembly: System.Reflection.AssemblyMetadata(\"BjolangMetadata\", \"%s{escaped}\")]"

    
    appendLine ctx ""
    // Only generate code for the main module (the last one)
    if not decls.IsEmpty then
        let mainModule = List.last decls
        generateDecl ctx mainModule
    
    ctx.Builder.ToString()
