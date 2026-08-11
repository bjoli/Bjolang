module Bjolang.Parser

open Lexer

// --- S-Expression Types ---
type SExpr =
    | SAtom of LexedToken
    | SList of SExpr list * Range

let getRange =
    function
    | SAtom t -> t.Range
    | SList(_, r) -> r

// --- AST Types ---
// Every node carries a Range to enable #line emission.

/// Whether a definition or lambda was *written* as one that may suspend.
///
/// This is the syntactic half of what `TypedAST.Effect` is the type-level half
/// of, and the two alphabets differ on purpose: `Effect` has a third case for a
/// variable, and no source form spells one. Keeping them apart means the parser
/// never has to name something a program cannot write.
///
/// The colour is on the *definition*, not on the signature. `(: fetch (-> string
/// string))` is what you write for a bjoroutine too — an arrow says what a
/// function takes and returns, and `defbjo` says whether calling it is a yield
/// point. The two are only joined up in `checkDecl`, which recolours the
/// declared type before unifying with it.
type Colour =
    /// `defun` / `fun`. Cannot suspend.
    | Ordinary
    /// `defbjo` / `bjoroutine`. May suspend; calling it is a yield point.
    | Suspending

type FType =
    | TName of string * Range
    | TApp of string * FType list * Range
    // (-> MandatoryTypes... (#:key KeyType)... #:rest RestElemType ReturnType)
    //
    // The colour is `Ordinary` for everything a program writes by hand: a
    // signature says what a function takes and returns, and `defbjo` says
    // whether calling it suspends. `Suspending` reaches here only from module
    // metadata, where the arrow is spelled `-bjo->` because the importing side
    // has no definition to read the colour off.
    | TArrow of FType list * (string * FType) list * FType option * FType * Colour * Range

type UnionCase =
    | SimpleCase of string * Range
    | DataCase of string * FType list * Range

type RecordField =
    { Name: string
      Type: FType
      Range: Range }

type TypeDefKind =
    | Alias of FType
    | Union of UnionCase list
    /// A record, and whether it is a *value* type (struct).
    | Record of RecordField list * bool

type TypeDef =
    { Name: string
      TypeArgs: string list
      Kind: TypeDefKind
      Range: Range }


type Pattern =
    | PWildcard of Range
    | PIdent of string * Range
    | PInt of string * Range
    | PString of string * Range
    /// A Unicode scalar value. See `Lexer.CharLit`.
    | PChar of int * Range
    | PKeyword of string * Range
    | PQuotedSymbol of string * Range
    | PList of Pattern list * Pattern option * Range // (items, optional tail, range)
    | PVec of Pattern list * Pattern option * Range // (items, optional tail, range)
    | PTuple of Pattern list * Range
    | PConstruct of string * Pattern list * Range
    /// `(:is System.IO.IOException e)` — matches when the value is of that .NET type, binding it there at the narrowed type.
    /// The binder is optional. Used in `Err` arms.
    | PTypeTest of string * string option * Range

and Expr =
    | EInt of string * Range
    | EString of string * Range
    /// A Unicode scalar value. See `Lexer.CharLit`.
    | EChar of int * Range
    | EQuotedSymbol of string * Range
    | EKeyword of string * Range
    | EIdent of string * Range
    | ETuple of Expr list * Range
    | EApp of Expr * Expr list * Range
    | ECast of FType * Expr * Range
    // ELet (name, isFun, args, typeAnn, value, restOfScope, range)
    | ELet of string * bool * string list * FType option * Expr * Expr * Range
    /// A binding that is deliberately *not* generalized (used for associated-type projections).
    | ELetMono of string * Expr * Expr * Range
    // ELetRec (bindings, restOfScope, range)
    // binding tuple: (name, isFun, args, typeAnn, value)
    | ELetRec of (string * bool * string list * FType option * Expr) list * Expr * Range
    | ELetTuple of string list * Expr * Expr * Range
    | ELetMutable of string * FType option * Expr * Expr * Range
    | ESet of string * Expr * Range
    | EIf of Expr * Expr * Expr * Range
    /// `(when cond body...)`, and with the flag set, `(unless cond body...)`:
    /// a conditional with only one arm, evaluated for effect.
    | EWhen of Expr * Expr * bool * Range
    /// `(fun (a b) ...)`, and with `Suspending`, `(bjoroutine (a b) ...)`.
    | EFun of string list * Expr * Colour * Range
    /// Record and struct construction, treated as application.
    /// Handled by type inference rather than a dedicated AST node.
    | ERecordUpdate of string * (string * Expr) list * Range
    | EGetField of Expr * string * Range
    | EList of Expr list * Range
    | EVec of Expr list * Range
    | EMatch of Expr * (Pattern * Expr option * Expr) list * Range
    | ETryFinally of Expr * Expr * Range
    /// `(try body... #:catch (E1 E2 ...))`: run the body, and catch specific .NET exception types.
    | ETryCatch of Expr * string list * Range
    /// `(seq body...)`: a lazy sequence evaluated one `yield` at a time.
    | ESeq of Expr * Range
    /// `(bjo (f x y))`: spawn, and hand back a `(Promise %a)`. Operands are evaluated in the parent.
    | EBjo of Expr * Range
    /// `(task->event (fetch url))` — the *event* of making an async .NET call.
    /// The task is started when the event is synced and cancelled if its branch loses.
    | ETaskEvent of Expr * Range
    /// `(yield v)`: hand `v` to the enclosing `seq`'s consumer.
    | EYield of Expr * Range
    /// `(yield-from s)`: hand over every element of `s` in turn.
    | EYieldFrom of Expr * Range

and DefunArg =
    | MandatoryArg of string
    | KeywordArg of string * Expr              // (#:keyword defaultValue)
    | RestArg of string                        // #:rest name

type ImportSpec =
    | RelativePath of string
    | ModulePath of string list

/// One clause of `(import/extern ...)`: a static .NET method bound as an
/// ordinary Bjolang function.
///
/// `ExplicitType` is optional. Given, it is enforced — the resolved overload
/// has to unify with it, which is how a call site says *which* `WriteLine` it
/// means. Omitted, the overload is chosen from the argument types at each call.
type ExternImportSpec =
    { Alias: string
      /// The fully qualified target, e.g. `System.Console.WriteLine`.
      ClrTarget: string
      ExplicitType: FType option
      /// Exception types named by `#:exceptions`. Non-empty makes the call
      /// return a `(Result System.Exception ...)`; anything not listed here
      /// keeps propagating.
      Exceptions: string list
      /// `#:async`: the target returns a task, calling it is a yield point, and
      /// the Bjolang type of the call is the task's *result*. §7.2.
      IsAsync: bool
      /// `#:uncancellable`: do not thread the ambient token. Required when the
      /// method has no `CancellationToken` overload to thread it into.
      Uncancellable: bool
      /// `#:cancellable`: thread the ambient token into a non-`#:async` import,
      /// whose `CancellationToken` parameter is not optional. §7.6.
      Cancellable: bool
      Range: Range }

/// One clause of `(import/class ...)`: a .NET class, its name, and its
/// constructor.
type ClassImportSpec =
    { Alias: string
      ClrClass: string
      ConstructorType: FType option
      Exceptions: string list
      Range: Range }

type Decl =
    | DSignature of string * FType * (string * string) list * Range
    | DImport of ImportSpec list * Range
    | DExport of string list * Range
    // Re-exports bindings this module imported from elsewhere. Unlike `export`,
    // the names are not required to have a signature in this module — they
    // already have one where they were defined.
    | DReExport of string list * Range
    | DModule of string * Decl list * Range
    | DDef of string * Expr * Range
    | DDefTuple of string list * Expr * Range
    | DDefMutable of string * Expr * Range
    /// `(defun (name args...) body)`, and with `Suspending`, `defbjo`.
    | DDefun of string * DefunArg list * Expr * Colour * Range
    | DType of TypeDef list * Range
    | DTypeRec of TypeDef list * Range
    // DTrait (Name, ImplementorVar, HoleArity, AssociatedTypes, Signatures, Defaults, Range)
    //
    // `HoleArity` is how many arguments the implementor was written applied to.
    // `(def/trait (Show %c) ...)` gives 0 and means an interface trait;
    // `(def/trait (Monad (%m %a)) ...)` gives 1 and means an inline-only one.
    //
    // `Defaults` are `DDefun`s written in the trait itself, standing in for the
    // method of that name in any impl that does not write one. They are kept
    // untyped and *unchecked* here: a default is checked once per impl, against
    // that impl's instantiation of the signature, which is what lets one body
    // mean something different at each implementor.
    | DTrait of string * string * int * string list * (string * FType) list * Decl list * Range
    | DExtern of string * FType * (string * string) list * Range
    /// `(import/extern (alias (: Clr.Target type #:exceptions (E ...))) ...)`
    | DImportExtern of ExternImportSpec list * Range
    /// `(import/class (Alias (: Clr.Class type #:exceptions (E ...))) ...)`
    | DImportClass of ClassImportSpec list * Range

    // One inline-trait method body, read back out of a compiled module's
    // metadata. It is the *untyped* expression: re-inferring it at the splice is
    // what gives it a type its trait signature cannot express.
    // DInlineImpl (TraitName, MethodName, Ctor, OriginModule, Params, Body, Qualification, Range)
    | DInlineImpl of string * string * string * string * string list * Expr * (string * string) list * Range

    // DImpl (TraitName, TargetType, AssociatedTypeBindings, Methods, Range)
    | DImpl of string * FType * (string * FType) list * Decl list * Range

    // A declaration-only implementation: it records that the target type
    // implements the trait, and what its associated types are, without carrying
    // any method bodies. This is what a compiled module's metadata exports —
    // the methods themselves already live in that assembly.
    // DImplExtern (TraitName, TargetType, AssociatedTypeBindings, Range)
    | DImplExtern of string * FType * (string * FType) list * Range

// --- Parser ---

let rec parsePattern (s: SExpr) : Pattern =
    let r = getRange s

    match s with
    | SAtom { Token = Symbol "_" } -> PWildcard r
    | SAtom { Token = Symbol sym } ->
        if System.Char.IsUpper(sym.[0]) then PConstruct(sym, [], r)
        else PIdent(sym, r)
    | SAtom { Token = NumberLit n } -> PInt(n, r)
    | SAtom { Token = StringLit str } -> PString(str, r)
    | SAtom { Token = Keyword kw } -> PKeyword(kw, r)
    | SAtom { Token = CharLit c } -> PChar(c, r)
    | SAtom { Token = QuotedSymbol sym } -> PQuotedSymbol(sym, r)

    // `(:is Some.Clr.Type)` and `(:is Some.Clr.Type binder)`.
    | SList([ SAtom { Token = Keyword "is" }; SAtom { Token = Symbol typeName } ], _) ->
        PTypeTest(typeName, None, r)
    | SList([ SAtom { Token = Keyword "is" }
              SAtom { Token = Symbol typeName }
              SAtom { Token = Symbol binder } ],
            _) ->
        PTypeTest(typeName, Some binder, r)
    | SList(SAtom { Token = Keyword "is" } :: _, _) ->
        failwithf
            $"Invalid :is pattern at %s{Lexer.formatPos r}. Expected (:is Fully.Qualified.Type) or (:is Fully.Qualified.Type binding-name)."

    // Special handling for List/Vec patterns and the spread operator
    | SList(SAtom { Token = Symbol "List" } :: args, _) ->
        let elements, tail = parseSpreadArgs r args
        PList(elements, tail, r)

    // `(Vec a b c ...)` and the bracket literal form `[a b c ...]`, which the
    // reader rewrites to `(vec-literal a b c ...)`.
    | SList(SAtom { Token = Symbol("Vec" | "vec-literal") } :: args, _) ->
        let elements, tail = parseSpreadArgs r args
        PVec(elements, tail, r)

    // `(Tuple a b ...)` and dotted pairs `(a . b ...)` which the reader rewrites to `(Tuple a b ...)`
    | SList(SAtom { Token = Symbol "Tuple" } :: args, _) ->
        PTuple(List.map parsePattern args, r)

    | SList(SAtom { Token = Symbol name } :: args, _) -> PConstruct(name, List.map parsePattern args, r)

    | SList([], _) -> PList([], None, r) // Empty list pattern

    | _ -> failwithf $"Invalid pattern at %s{Lexer.formatPos r}"

/// Splits the arguments of a sequence pattern into its fixed leading elements
/// plus an optional trailing rest pattern introduced by `...`.
/// For example `a b c ...` yields ([a; b], Some c), binding `c` to the rest.
and parseSpreadArgs (r: Range) (args: SExpr list) : Pattern list * Pattern option =
    let rec go acc items =
        match items with
        | [] -> (List.rev acc, None)
        // Matches `c ...` at the end of the sequence
        | [ tailItem; SAtom { Token = Spread } ] -> (List.rev acc, Some(parsePattern tailItem))
        // Fails if spread is used incorrectly (e.g., in the middle of the sequence)
        | SAtom { Token = Spread } :: _ -> failwithf $"Invalid use of spread operator at %s{Lexer.formatPos r}"
        | head :: tail -> go (parsePattern head :: acc) tail

    go [] args

let parseArrowType (colour: Colour) (items: SExpr list) (r: Range) : FType =
    if items.IsEmpty then failwithf $"Arrow type must have at least a return type at %s{Lexer.formatPos r}"
    let returnTypeExpr = List.last items
    let argItems = List.take (items.Length - 1) items

    let rec parseArrowTypeInner (s: SExpr) : FType =
        let r = getRange s
        match s with
        | SAtom { Token = QuotedSymbol sym } -> TName("'" + sym, r)
        | SAtom { Token = Symbol sym }
        | SAtom { Token = TypeVar sym } -> TName(sym, r)
        | SList(SAtom { Token = Symbol name } :: typeArgs, _) -> TApp(name, List.map parseArrowTypeInner typeArgs, r)
        // A type variable in *applied* position: `(%m %a)`. Only an inline
        // trait's constructor variable can be written this way, and the leading
        // quote is what tells `resolveTemplate` it is the hole rather than a
        // constructor named `m`.
        | SList(SAtom { Token = QuotedSymbol sym } :: typeArgs, _) ->
            TApp("'" + sym, List.map parseArrowTypeInner typeArgs, r)
        | _ -> failwithf $"Invalid type syntax in arrow type at %s{Lexer.formatPos r}"

    let rec collectArgs mandatory keywords argItems =
        match argItems with
        | [] -> TArrow(List.rev mandatory, List.rev keywords, None, parseArrowTypeInner returnTypeExpr, colour, r)
        | [SAtom { Token = Keyword "rest" }] ->
            failwithf $"Expected rest element type after #:rest at %s{Lexer.formatPos r}"
        | SAtom { Token = Keyword "rest" } :: restTypeExpr :: [] ->
            TArrow(List.rev mandatory, List.rev keywords, Some (parseArrowTypeInner restTypeExpr), parseArrowTypeInner returnTypeExpr, colour, r)
        | SList(SAtom { Token = Keyword name } :: [ typeExpr ], _) :: rest ->
            collectArgs mandatory ((name, parseArrowTypeInner typeExpr) :: keywords) rest
        | item :: rest when keywords.IsEmpty ->
            collectArgs (parseArrowTypeInner item :: mandatory) keywords rest
        | _ -> failwithf $"Mandatory types must come before keyword/rest types in arrow type at %s{Lexer.formatPos r}"

    collectArgs [] [] argItems

let rec parseType (s: SExpr) : FType =
    let r = getRange s

    match s with
    | SAtom { Token = QuotedSymbol sym } -> TName("'" + sym, r)  // %a in source → 'a internally
    | SAtom { Token = Symbol sym }
    | SAtom { Token = TypeVar sym } -> TName(sym, r)
    | SList(SAtom { Token = Symbol "->" } :: arrowArgs, _) -> parseArrowType Ordinary arrowArgs r
    // `(-bjo-> ...)`, an arrow whose calls are yield points. Written only by
    // the metadata serializer: a module that imports a bjoroutine has no
    // `defbjo` to read the colour off, so the published *type* has to carry it.
    // Nothing stops a program spelling one by hand, and `checkDecl` catches the
    // case where it disagrees with the definer.
    | SList(SAtom { Token = Symbol "-bjo->" } :: arrowArgs, _) -> parseArrowType Suspending arrowArgs r
    | SList(SAtom { Token = Symbol name } :: typeArgs, _) -> TApp(name, List.map parseType typeArgs, r)
    // `(%m %a)` — a type variable applied to arguments. See `parseArrowTypeInner`.
    | SList(SAtom { Token = QuotedSymbol sym } :: typeArgs, _) ->
        TApp("'" + sym, List.map parseType typeArgs, r)
    | _ -> failwithf $"Invalid type syntax at %s{Lexer.formatPos r}"

let parseUnionCase (s: SExpr) : UnionCase =
    let r = getRange s

    match s with
    | SAtom { Token = Symbol name } -> SimpleCase(name, r)
    | SList([ SAtom { Token = Symbol name } ], _) -> SimpleCase(name, r)
    | SList(SAtom { Token = Symbol name } :: tTypes, _) -> DataCase(name, List.map parseType tTypes, r)
    | _ ->
        printfn $"%A{s}"
        failwithf $"Invalid union case at %s{Lexer.formatPos r}"

let parseRecordField (s: SExpr) : RecordField =
    let r = getRange s

    match s with
    | SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) ->
        { Name = name
          Type = parseType tType
          Range = r }
    | _ -> failwithf $"Invalid record field at %s{Lexer.formatPos r}"

let parseTypeDefHead (head: SExpr) : string * string list =
    match head with
    | SAtom { Token = Symbol name } -> name, []
    | SList(SAtom { Token = Symbol name } :: args, _) ->
        let parseTypeArg = function
            | SAtom { Token = QuotedSymbol ta } -> ta
            | SAtom { Token = Symbol s } -> s // Just in case they are not quoted
            | _ -> failwithf $"Invalid type argument at %s{Lexer.formatPos (getRange head)}"
        name, List.map parseTypeArg args
    | _ -> failwithf $"Invalid type definition head at %s{Lexer.formatPos (getRange head)}"

let parseTypeDef (s: SExpr) : TypeDef =
    let r = getRange s

    match s with
    | SList([ SAtom { Token = Colon }
              head
              SList(SAtom { Token = Symbol(("Record" | "Struct") as kind) } :: fields, _) ],
            _) ->
        let name, typeArgs = parseTypeDefHead head
        { Name = name
          TypeArgs = typeArgs
          Kind = Record(List.map parseRecordField fields, kind = "Struct")
          Range = r }
    // `Union`, `Enum`, and `Sum` are accepted tags for sum types.
    | SList([ SAtom { Token = Colon }
              head
              SList(SAtom { Token = Symbol("Union" | "Enum" | "Sum") } :: cases, _) ],
            _) ->
        let name, typeArgs = parseTypeDefHead head
        { Name = name
          TypeArgs = typeArgs
          Kind = Union(List.map parseUnionCase cases)
          Range = r }
    // Explicit Alias: (: head (Alias aliasType))
    | SList([ SAtom { Token = Colon }
              head
              SList([ SAtom { Token = Symbol "Alias" }; aliasType ], _) ],
            _) ->
        let name, typeArgs = parseTypeDefHead head
        { Name = name
          TypeArgs = typeArgs
          Kind = Alias(parseType aliasType)
          Range = r }
    // Implicit Alias: (: head aliasType)
    | SList([ SAtom { Token = Colon }; head; aliasType ], _) ->
        let name, typeArgs = parseTypeDefHead head
        { Name = name
          TypeArgs = typeArgs
          Kind = Alias(parseType aliasType)
          Range = r }
    | _ -> failwithf $"Invalid type definition at %s{Lexer.formatPos r}"

let parseDefunArg (arg: SExpr) : (string * FType option) =
    match arg with
    | SAtom { Token = Symbol n } -> (n, None)
    | SList([ SAtom { Token = Colon }; SAtom { Token = Symbol n }; t ], _) -> (n, Some(parseType t))
    | _ -> failwith "Invalid defun argument"

let parseDefunArgs (args: SExpr list) : (string * FType option) list = args |> List.map parseDefunArg

let parseDefunRest (rest: SExpr list) : (FType option * SExpr list) =
    match rest with
    | SAtom { Token = Colon } :: t :: body -> (Some(parseType t), body)
    | body -> (None, body)


// Desugar a quoted list '(a ,x b) into (Cons 'a (Cons x (Cons 'b Nil))).
//
// Symbols are literal Symbol data — '(a b c) gives three symbol values, not
// three variable references. To splice a computed value, prefix it with `,`:
// '(a ,x b) evaluates x and conses it between the two symbols.
//
// `parseExprFn` is threaded in as a parameter because this function is defined
// before `parseExpr`; the call site passes `parseExpr` directly.
let desugarQuotedList (parseExprFn: SExpr -> Expr) (items: SExpr list) (r: Range) : Expr =
    // Collect quoted items into a flat list and produce EList, giving '(...)
    // the same inference path as (list ...) and []. Constructor injection
    // (e.g. wrapping a string in ProcBang when the expected type is a union)
    // therefore works on quasiquote literals for free.
    let rec quoteItem (s: SExpr) : Expr =
        let ir = getRange s
        match s with
        | SAtom { Token = NumberLit n } -> EInt(n, ir)
        | SAtom { Token = StringLit str } -> EString(str, ir)
        | SAtom { Token = CharLit c } -> EChar(c, ir)
        | SAtom { Token = Keyword kw } -> EKeyword(kw, ir)
        // A symbol in a quoted list is a literal Symbol value, not a variable
        // reference — write ,(expr) to splice the value of a variable.
        | SAtom { Token = Symbol sym } -> EQuotedSymbol(sym, ir)
        | SAtom { Token = QuotedSymbol sym } -> EQuotedSymbol(sym, ir)
        // Dotted pair in a quoted list: '(a . b) → (Tuple a b)
        | SList(SAtom { Token = Symbol "Tuple" } :: tupleItems, _) ->
            ETuple(List.map quoteItem tupleItems, ir)
        // Nested list: '('(a b) '(c d)) → EList [EList [a b]; EList [c d]]
        | SList(inner, lr) ->
            collectItems inner lr
        | _ -> failwithf $"Unsupported item in quoted list at %s{Lexer.formatPos ir}"

    and collectItems (items: SExpr list) (r: Range) : Expr =
        let rec go acc remaining =
            match remaining with
            | [] -> EList(List.rev acc, r)
            // ,expr — unquote: evaluate and splice the expression as an element.
            | SAtom { Token = Comma } :: inner :: rest ->
                go (parseExprFn inner :: acc) rest
            | SAtom { Token = Comma } :: [] ->
                failwithf $"Unexpected , at end of quoted list at %s{Lexer.formatPos r}"
            | item :: rest ->
                go (quoteItem item :: acc) rest
        go [] items

    collectItems items r

/// An expression's own range.
let exprRange (e: Expr) : Range =
    match e with
    | EInt(_, r)
    | EString(_, r)
    | EChar(_, r)
    | EQuotedSymbol(_, r)
    | EKeyword(_, r)
    | EIdent(_, r)
    | ETuple(_, r)
    | EApp(_, _, r)
    | ECast(_, _, r)
    | ELet(_, _, _, _, _, _, r)
    | ELetMono(_, _, _, r)
    | ELetRec(_, _, r)
    | ELetTuple(_, _, _, r)
    | ELetMutable(_, _, _, _, r)
    | ESet(_, _, r)
    | EIf(_, _, _, r)
    | EWhen(_, _, _, r)
    | EFun(_, _, _, r)
    | ERecordUpdate(_, _, r)
    | EGetField(_, _, r)
    | EList(_, r)
    | EVec(_, r)
    | EMatch(_, _, r)
    | ETryFinally(_, _, r)
    | ETryCatch(_, _, r)
    | ESeq(_, r)
    | EBjo(_, r)
    | ETaskEvent(_, r)
    | EYield(_, r)
    | EYieldFrom(_, r) -> r

/// Every expression held directly inside `e`.
///
/// Exhaustive on purpose: this is used to refuse a loop name outside tail
/// position, and a case missed here would let one through to a much worse
/// diagnostic later.
let exprChildren (e: Expr) : Expr list =
    match e with
    | EInt _
    | EString _
    | EChar _
    | EQuotedSymbol _
    | EKeyword _
    | EIdent _ -> []
    | ECast(_, x, _)
    | EGetField(x, _, _)
    | ESeq(x, _)
    | EBjo(x, _)
    | ETaskEvent(x, _)
    | EYield(x, _)
    | EYieldFrom(x, _) -> [ x ]
    | ESet(_, x, _) -> [ x ]
    | ETuple(xs, _)
    | EList(xs, _)
    | EVec(xs, _) -> xs
    | EApp(f, args, _) -> f :: args
    | ELet(_, _, _, _, v, b, _) -> [ v; b ]
    | ELetMono(_, v, b, _) -> [ v; b ]
    | ELetTuple(_, v, b, _) -> [ v; b ]
    | ELetMutable(_, _, v, b, _) -> [ v; b ]
    | ELetRec(bindings, b, _) -> (bindings |> List.map (fun (_, _, _, _, v) -> v)) @ [ b ]
    | EIf(c, t, f, _) -> [ c; t; f ]
    | EWhen(c, b, _, _) -> [ c; b ]
    | EFun(_, b, _, _) -> [ b ]
    | ERecordUpdate(_, fields, _) -> fields |> List.map snd
    | ETryFinally(b, c, _) -> [ b; c ]
    | ETryCatch(b, _, _) -> [ b ]
    | EMatch(target, clauses, _) ->
        target
        :: (clauses |> List.collect (fun (_, guard, body) -> (Option.toList guard) @ [ body ]))

/// One clause of a `(loop ...)`, still unparsed.
///
/// The clause list is flat and there is no body position: every clause carries
/// its own condition, and iteration order is clause order.
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

/// The arithmetic and bitwise operators, which left-fold, and the comparisons,
/// which chain.
let private foldingOps =
    [ "+"; "-"; "*"; "/"; "%"; "bitwise-and"; "bitwise-ior"; "bitwise-xor" ]
let private chainingOps = [ "<"; ">"; "<="; ">="; "=" ]

/// The operators `Codegen` emits as C# syntax, and how many operands each takes.
///
/// Applied, an operator becomes infix and never needs a name. Written as a value
/// it has none to be — C# has no `+` to pass — so it becomes the lambda it
/// stands for, and the ordinary application inside that lambda is emitted infix
/// like any other. `negate`, `recip` and `bitwise-not` are the unary ones.
let private operatorArity =
    Map [ "+", 2
          "-", 2
          "*", 2
          "/", 2
          "%", 2
          "=", 2
          "<", 2
          ">", 2
          "<=", 2
          ">=", 2
          "bitwise-and", 2
          "bitwise-ior", 2
          "bitwise-xor", 2
          "shift-left", 2
          "shift-right", 2
          "shift-right-logical", 2
          "negate", 1
          "recip", 1
          "bitwise-not", 1 ]

/// Is re-evaluating this expression free and side-effect free?
///
/// Only used to decide whether a chained comparison's middle operand needs a
/// temporary. Anything not obviously atomic gets one, so being conservative
/// here costs a binding and never costs correctness.
let private isAtomicOperand (e: Expr) : bool =
    match e with
    | EInt _
    | EString _
    | EQuotedSymbol _
    | EKeyword _
    | EIdent _ -> true
    | _ -> false

/// `(op a b c ...)` as nested binary applications.
///
/// Arithmetic left-folds, so `(+ a b c)` is `(+ (+ a b) c)` — which is what
/// keeps every arithmetic operator binary by the time codegen sees it, and so
/// keeps the infix emission in `Codegen` and its freedom from allocation.
///
/// Comparisons chain instead of folding: `(< a b c)` means `a < b && b < c`,
/// not `(a < b) < c`. Each middle operand appears in two comparisons, so one
/// that is not atomic is bound to a temporary first — `(< 0 (next!) 10)` must
/// call `next!` once, not twice.
let private desugarNaryOp (op: string) (args: Expr list) (r: Range) : Expr =
    let binary a b = EApp(EIdent(op, r), [ a; b ], r)

    let arityError (wanted: string) =
        failwithf
            $"Syntax error at %s{Lexer.formatPos r}: '%s{op}' takes %s{wanted}, but was given %d{args.Length}."

    if List.contains op foldingOps then
        match args with
        // `(+)` is 0 and `(*)` is 1 — each operator's identity, as in Scheme.
        // They are `int`; a zero of another type is written as a literal.
        | [] ->
            match op with
            | "+" -> EInt("0", r)
            | "*" -> EInt("1", r)
            | _ -> arityError "at least one argument"
        | [ single ] ->
            match op with
            | "+"
            | "*"
            | "bitwise-and"
            | "bitwise-ior"
            | "bitwise-xor" -> single
            | "-" -> EApp(EIdent("negate", r), [ single ], r)
            | "/" -> EApp(EIdent("recip", r), [ single ], r)
            | _ -> arityError "at least two arguments"
        | first :: rest -> rest |> List.fold binary first
    else
        match args with
        | []
        | [ _ ] -> arityError "at least two arguments"
        | _ ->
            let lastIndex = List.length args - 1

            // Only the middle operands are read twice; the ends are not.
            let bindings = ResizeArray<string * Expr>()

            let operands =
                args
                |> List.mapi (fun i a ->
                    if i > 0 && i < lastIndex && not (isAtomicOperand a) then
                        let name = Gensym.fresh "cmp"
                        bindings.Add(name, a)
                        EIdent(name, r)
                    else
                        a)

            let comparisons = operands |> List.pairwise |> List.map (fun (l, rr) -> binary l rr)

            let rec buildAnd items =
                match items with
                | [] -> EIdent("true", r)
                | [ last ] -> last
                | current :: rest -> EIf(current, buildAnd rest, EIdent("false", r), r)

            List.foldBack
                (fun (name, value) acc -> ELet(name, false, [], None, value, acc, r))
                (List.ofSeq bindings)
                (buildAnd comparisons)

let rec parseExpr (s: SExpr) : Expr =
    let r = getRange s

    let rec processArgs items =
        match items with
        | [] -> []
        | SAtom { Token = Comma } :: rest -> processArgs rest
        | item :: rest -> parseExpr item :: processArgs rest

    // Treat specific operator tokens as valid identifiers in expressions
    let (|Ident|_|) =
        function
        | SAtom { Token = Symbol "#t" } -> Some "true"
        | SAtom { Token = Symbol "#f" } -> Some "false"
        | SAtom { Token = Symbol sym } -> Some sym
        | _ -> None

    match s with
    | SAtom { Token = NumberLit n } -> EInt(n, r)
    | SAtom { Token = StringLit str } -> EString(str, r)
    | SAtom { Token = CharLit c } -> EChar(c, r)
    | SAtom { Token = QuotedSymbol sym } -> EQuotedSymbol(sym, r)
    | SAtom { Token = Keyword sym } -> EKeyword(sym, r)

    // An operator used as a value, which is the only position this case sees:
    // the head of an application is built by the `SList` branch below and never
    // arrives here. So no analysis is needed to tell the two apart, and none of
    // this depends on types.
    | Ident sym when Map.containsKey sym operatorArity ->
        let ps = List.init operatorArity[sym] (fun _ -> Gensym.fresh "op")
        EFun(ps, EApp(EIdent(sym, r), ps |> List.map (fun p -> EIdent(p, r)), r), Ordinary, r)

    | Ident sym -> EIdent(sym, r)

    | SList(head :: args, listRange) ->
        match head with
        | Ident sym ->
            match sym with
            | "cast" ->
                match args with
                | [ typeSExpr; valSExpr ] ->
                    ECast(parseType typeSExpr, parseExpr valSExpr, r)
                | _ -> failwithf $"Invalid cast syntax at %s{Lexer.formatPos r}. Expected: (cast <type> <expr>)"
            | "let" ->
                match args with
                | SList(bindings, _) :: bodyExprs ->
                    let body = parseBody bodyExprs listRange

                    List.foldBack
                        (fun bind acc ->
                            match bind with
                            | SList([ Ident k; v ], _) -> ELet(k, false, [], None, parseExpr v, acc, getRange bind)
                            | SList([ SList(names, _); v ], bindRange) when
                                not names.IsEmpty
                                && names
                                   |> List.forall (function
                                       | SAtom { Token = Symbol _ }
                                       | SAtom { Token = Comma } -> true
                                       | _ -> false) ->
                                let rawNames =
                                    names
                                    |> List.choose (function
                                        | SAtom { Token = Symbol n } -> Some n
                                        | _ -> None)
                                let tupleNames =
                                    match rawNames with
                                    | "Tuple" :: restNames -> restNames
                                    | _ -> rawNames
                                ELetTuple(tupleNames, parseExpr v, acc, bindRange)
                            | _ -> failwith "Invalid let binding")
                        bindings
                        body
                | Ident name :: SList(bindings, _) :: bodyExprs ->
                    // Named let
                    let parsedBindings =
                        bindings
                        |> List.map (function
                            | SList([ Ident k; v ], _) -> (k, parseExpr v)
                            | _ -> failwith "Invalid named let binding")

                    let argNames = parsedBindings |> List.map fst
                    let argVals = parsedBindings |> List.map snd
                    let body = parseBody bodyExprs listRange
                    let funcBinding = (name, true, argNames, None, body)
                    ELetRec([funcBinding], EApp(EIdent(name, r), argVals, r), r)
                | _ -> failwith "Invalid let syntax"

            // Internal: the readable form of `ELetMono`, so that an inline
            // template containing one survives export and re-import. Written by
            // desugarings and by `serializeExpr`, not intended to be hand-written.
            | "let/mono" ->
                match args with
                | [ Ident name; value; body ] -> ELetMono(name, parseExpr value, parseExpr body, listRange)
                | _ ->
                    failwithf $"Invalid let/mono syntax at %s{Lexer.formatPos r}. Expected: (let/mono name value body)"

            | "letrec" ->
                match args with
                | SList(bindings, _) :: bodyExprs ->
                    let parsedBindings =
                        bindings
                        |> List.map (function
                            // Standard explicit letrec assumes value bindings or manually desugared lambdas
                            | SList([ Ident k; v ], _) -> (k, false, [], None, parseExpr v)
                            | _ -> failwith "Invalid letrec binding")

                    ELetRec(parsedBindings, parseBody bodyExprs listRange, r)
                | _ -> failwith "Invalid letrec syntax"
            | "set!" ->
                match args with
                | [ Ident target; valExpr ] -> ESet(target, parseExpr valExpr, r)
                | _ -> failwithf $"Invalid set! syntax at %s{Lexer.formatPos r}. Expected: (set! name value)"
            | "->" ->
                match args with
                | init :: steps ->
                    let rec buildThread (prev: SExpr) (step: SExpr) : SExpr =
                        match step with
                        | SAtom { Token = Symbol _ } as sym ->
                            SList([sym; prev], getRange sym)
                        | SList(items, stepR) ->
                            let rec replaceListItems (items: SExpr list) : SExpr list * bool =
                                match items with
                                | [] -> [], false
                                | SAtom { Token = Hash } as h :: SList(subItems, subR) :: tail ->
                                    let rest, foundInRest = replaceListItems tail
                                    h :: SList(subItems, subR) :: rest, foundInRest
                                | head :: tail ->
                                    let newHead, foundHead = replaceAmpersand head
                                    let newTail, foundTail = replaceListItems tail
                                    newHead :: newTail, foundHead || foundTail

                            and replaceAmpersand (expr: SExpr) : SExpr * bool =
                                match expr with
                                | SAtom { Token = Symbol "&" } -> prev, true
                                | SList(subItems, subR) ->
                                    let newItems, found = replaceListItems subItems
                                    SList(newItems, subR), found
                                | _ -> expr, false

                            let newItems, hasAmp = replaceListItems items
                            if hasAmp then
                                SList(newItems, stepR)
                            else
                                match items with
                                | head :: tail -> SList(head :: prev :: tail, stepR)
                                | [] -> failwithf $"Invalid empty list in -> macro at %s{Lexer.formatPos stepR}"
                        | _ -> failwithf $"Invalid step in -> macro at %s{Lexer.formatPos (getRange step)}"

                    let threadExpr = steps |> List.fold buildThread init
                    parseExpr threadExpr
                | _ -> failwithf $"-> requires at least one argument at %s{Lexer.formatPos r}"
            | "if" ->
                match args with
                | [ cond; t; f ] -> EIf(parseExpr cond, parseExpr t, parseExpr f, r)
                | _ -> failwith "Invalid if syntax"

            // `when` and `unless` are one-armed: there is no second branch for
            // the body's type to agree with, so they are statements rather than
            // expressions. Desugaring them into `if` with an empty tuple as the
            // missing arm made every body that was not itself an empty tuple a
            // type error — which is to say every body anyone would write.
            | "when" ->
                match args with
                | cond :: bodyExprs when not bodyExprs.IsEmpty ->
                    EWhen(parseExpr cond, parseBody bodyExprs listRange, false, listRange)
                | _ -> failwithf $"Invalid when syntax at %s{Lexer.formatPos r}. Expected: (when cond body...)"

            | "unless" ->
                match args with
                | cond :: bodyExprs when not bodyExprs.IsEmpty ->
                    EWhen(parseExpr cond, parseBody bodyExprs listRange, true, listRange)
                | _ -> failwithf $"Invalid unless syntax at %s{Lexer.formatPos r}. Expected: (unless cond body...)"

            // A `seq` body is a block like any other, but it is *not* run where
            // it is written: the form evaluates to a sequence, and the body runs
            // a `yield` at a time as that sequence is consumed.
            | "seq" ->
                match args with
                | [] -> failwithf $"Invalid seq syntax at %s{Lexer.formatPos r}. Expected: (seq body...)"
                | bodyExprs -> ESeq(parseBody bodyExprs listRange, listRange)

            // `(bjo (f x y))`. The operand must be a call: `bjo` splits it into
            // operands evaluated here and a call made over there, and there is
            // nothing to split in anything else.
            | "bjo" ->
                match args with
                | [ SList(_ :: _, _) as call ] -> EBjo(parseExpr call, listRange)
                | _ ->
                    failwithf
                        $"Invalid bjo syntax at %s{Lexer.formatPos r}. Expected: (bjo (f args...)) — one call, whose operands are evaluated here and whose call happens in the new fiber. For a thunk you already have, use spawn-thunk."

            // `(spawn-evt (worker q))` — start this when the event is synced,
            // and cancel it if the branch loses.
            //
            // Desugared here rather than given a node of its own, because every
            // piece already exists: a nullary lambda holding a `bjo`, handed to
            // a prelude function that installs a fresh cancellation token
            // around calling it. `bjo` is colourless, so the lambda is an
            // ordinary one even when the call inside it suspends — which is
            // exactly the case §3.1 would otherwise forbid, and the reason this
            // cannot be a plain function over a thunk.
            //
            // The lambda runs at *sync* time, once per sync, so the call's
            // operands are evaluated there rather than here. That is the
            // opposite of `bjo` and the same as `task->event`: an event is a
            // description, and a description that had already run its arguments
            // would be a description of something that had already happened.
            | "spawn-evt" ->
                match args with
                | [ SList(_ :: _, _) as call ] ->
                    EApp(
                        EIdent("spawn-evt/start", listRange),
                        [ EFun([], EBjo(parseExpr call, listRange), Ordinary, listRange) ],
                        listRange
                    )
                | _ ->
                    failwithf
                        $"Invalid spawn-evt syntax at %s{Lexer.formatPos r}. Expected: (spawn-evt (f args...)) — one call, spawned when the event is synced and cancelled if its branch loses. To spawn eagerly and keep listening whatever happens, use (promise-join (bjo (f args...)))."

            // `(task->event (fetch url))`. A special form for the same reason
            // `bjo` is: the operand must *not* be evaluated where it is
            // written. An `#:async` call means "await this" everywhere else,
            // and here it has to mean "hand me the task, unstarted", so that
            // the event can start it at sync time with a token of its own.
            | "task->event" ->
                match args with
                | [ SList(_ :: _, _) as call ] -> ETaskEvent(parseExpr call, listRange)
                | _ ->
                    failwithf
                        $"Invalid task->event syntax at %s{Lexer.formatPos r}. Expected: (task->event (f args...)) — one call to a method imported #:async, whose arguments are evaluated here and whose call is made when the event is synced."

            // A monadic block. `(seq ...)` was already taken by lazy sequences,
            // so the form is spelled `do`.
            //
            //   (do (:bind x xs)
            //       (:let  y (+ x 1))
            //       (:then (side-effecting-action))
            //       (:return (* y 2)))
            //
            // The last form may be *any* `m a`, not necessarily `:return`:
            // otherwise a monadic loop could not be written at all, because the
            // recursive call has to *be* the result rather than be wrapped in a
            // `pure`. `(:return e)` is sugar for `(pure e)` in tail position.
            | "do" ->
                match args with
                | [] -> failwithf $"Invalid do syntax at %s{Lexer.formatPos r}. Expected: (do form...)"
                | forms -> desugarDo forms listRange

            // Guarded rather than claimed outright: `(loop (+ i 1))` is how a
            // named `let` recurses, and that must keep meaning a call. A clause
            // is a keyword-headed list, which an argument expression is not.
            | "loop" when isLoopForm args -> desugarLoop args listRange

            // Claimed outright rather than guarded like `loop`: `seql` collides
            // with nothing, and a guard would turn a malformed one into an
            // unbound-variable error instead of a loop diagnostic.
            | "seql" -> desugarSeqLoop args listRange

            // The head the reader puts on a `{...}` form. Claimed outright for
            // the same reason `seql` is; a program cannot write braces by
            // accident.
            | "comprehension" -> desugarComprehension args listRange

            | "yield" ->
                match args with
                | [ value ] -> EYield(parseExpr value, listRange)
                | _ -> failwithf $"Invalid yield syntax at %s{Lexer.formatPos r}. Expected: (yield value)"

            | "yield-from" ->
                match args with
                | [ source ] -> EYieldFrom(parseExpr source, listRange)
                | _ -> failwithf $"Invalid yield-from syntax at %s{Lexer.formatPos r}. Expected: (yield-from seq)"

            | "and" ->
                let rec buildAnd items =
                    match items with
                    | [] -> EIdent("true", listRange)
                    | [last] -> parseExpr last
                    | current :: rest ->
                        EIf(parseExpr current, buildAnd rest, EIdent("false", listRange), listRange)
                buildAnd args

            | "or" ->
                let rec buildOr items =
                    match items with
                    | [] -> EIdent("false", listRange)
                    | [last] -> parseExpr last
                    | current :: rest ->
                        EIf(parseExpr current, EIdent("true", listRange), buildOr rest, listRange)
                buildOr args

            | "not" ->
                match args with
                | [arg] -> EIf(parseExpr arg, EIdent("false", listRange), EIdent("true", listRange), listRange)
                | _ -> failwithf $"Invalid not syntax at %s{Lexer.formatPos r}"

            | "fun"
            | "bjoroutine" ->
                let colour = if sym = "bjoroutine" then Suspending else Ordinary

                match args with
                | SList(fargs, _) :: bodyExprs ->
                    let argNames =
                        fargs
                        |> List.choose (function
                            | Ident n -> Some n
                            | SAtom { Token = Comma } -> None
                            | _ -> failwith "Expected arg name")

                    EFun(argNames, parseBody bodyExprs listRange, colour, r)
                | _ -> failwithf $"Invalid %s{sym} syntax"

            | "match" ->
                match args with
                | targetExpr :: clauses ->
                    let target = parseExpr targetExpr

                    let parsedClauses =
                        clauses
                        |> List.map (fun clause ->
                            let rClause = getRange clause

                            match clause with
                            // Clause with a guard: (pattern #:when guard body...)
                            | SList(pattern :: SAtom { Token = Keyword "when" } :: guard :: bodyExprs, _) ->
                                (parsePattern pattern, Some(parseExpr guard), parseBody bodyExprs rClause)
                            // Standard clause: (pattern body...)
                            | SList(pattern :: bodyExprs, _) ->
                                (parsePattern pattern, None, parseBody bodyExprs rClause)
                            | _ -> failwithf $"Invalid match clause at %s{Lexer.formatPos rClause}")

                    EMatch(target, parsedClauses, r)
                | _ -> failwithf $"Invalid match syntax at %s{Lexer.formatPos r}"

            // Construction is spelled with the type name — `(Car (brand "x")
            // (year 3000))` — so there is no anonymous `record` form to infer a
            // type for. The old spelling is caught here rather than left to
            // fail as an unbound `record`, because the fix is not obvious from
            // "unknown identifier".
            | "record" | "struct" ->
                let shown =
                    args
                    |> List.map (function
                        | SList(Ident k :: _, _) -> $"(%s{k} ...)"
                        | _ -> "...")
                    |> String.concat " "

                failwithf
                    $"Invalid %s{sym} at %s{Lexer.formatPos r}: record and struct construction names its type, so write (TypeName %s{shown}) instead of (%s{sym} %s{shown})."

            // `struct*` forms are accepted synonyms for the `record*` forms.
            | "record-set" | "struct-set" ->
                match args with
                | Ident baseRec :: fields ->
                    let parsedFields =
                        fields
                        |> List.map (function
                            | SList([ Ident k; v ], _) -> (k, parseExpr v)
                            | bad ->
                                failwithf
                                    $"Invalid %s{sym} field at %s{Lexer.formatPos (getRange bad)}: expected (field-name value)")

                    ERecordUpdate(baseRec, parsedFields, r)
                | _ -> failwithf $"Invalid %s{sym} syntax at %s{Lexer.formatPos r}: expected (%s{sym} target (field value) ...)"

            | "record-get" | "struct-get" ->
                match args with
                | [ target; Ident field ] ->
                    EGetField(parseExpr target, field, r)
                | _ -> failwithf $"Invalid %s{sym} syntax at %s{Lexer.formatPos r}: expected (%s{sym} target field-name)"

            // `(try body... #:catch (E1 E2 ...) #:finally cleanup...)`
            //
            // Both clauses are optional and at least one is required, which is
            // what makes this one form rather than two. `#:catch` turns the
            // listed exception types — and only those — into an `Err`, giving
            // the whole form the type `(Result System.Exception %a)`.
            | "try" ->
                let rec split bodyAcc remaining =
                    match remaining with
                    | SAtom { Token = Keyword("catch" | "finally") } :: _ -> List.rev bodyAcc, remaining
                    | x :: rest -> split (x :: bodyAcc) rest
                    | [] -> List.rev bodyAcc, []

                let bodyForms, clauseForms = split [] args

                if bodyForms.IsEmpty then
                    failwithf $"Invalid try at %s{Lexer.formatPos r}: it has no body."

                // The clauses, read in whichever order they were written.
                let rec readClauses catchNames finallyForms remaining =
                    match remaining with
                    | [] -> catchNames, finallyForms
                    | SAtom { Token = Keyword "catch" } :: SList(names, _) :: rest ->
                        if Option.isSome catchNames then
                            failwithf $"Invalid try at %s{Lexer.formatPos r}: #:catch is given twice."

                        if names.IsEmpty then
                            failwithf
                                $"Invalid try at %s{Lexer.formatPos r}: #:catch names no exception types. Leave it off to let everything propagate."

                        let parsed =
                            names
                            |> List.map (function
                                | SAtom { Token = Symbol n } -> n
                                | bad ->
                                    failwithf
                                        $"Invalid try at %s{Lexer.formatPos (getRange bad)}: #:catch takes fully qualified .NET exception type names, as in System.IO.IOException.")

                        readClauses (Some parsed) finallyForms rest
                    | SAtom { Token = Keyword "catch" } :: _ ->
                        failwithf
                            $"Invalid try at %s{Lexer.formatPos r}: #:catch takes a parenthesized list of exception types."
                    | SAtom { Token = Keyword "finally" } :: rest ->
                        if not (List.isEmpty finallyForms) then
                            failwithf $"Invalid try at %s{Lexer.formatPos r}: #:finally is given twice."

                        // Everything up to the next clause keyword.
                        let cleanup, after = split [] rest

                        if cleanup.IsEmpty then
                            failwithf $"Invalid try at %s{Lexer.formatPos r}: #:finally has no body."

                        readClauses catchNames cleanup after
                    | bad :: _ ->
                        failwithf
                            $"Invalid try at %s{Lexer.formatPos (getRange bad)}: expected #:catch or #:finally."

                let catchNames, finallyForms = readClauses None [] clauseForms

                if Option.isNone catchNames && List.isEmpty finallyForms then
                    failwithf
                        $"Invalid try at %s{Lexer.formatPos r}: a try does nothing without #:catch or #:finally."

                let body = parseBody bodyForms listRange

                // `#:catch` is applied first, so that the cleanup runs whether
                // the body completed, was caught, or is still on its way out.
                let caught =
                    match catchNames with
                    | Some names -> ETryCatch(body, names, r)
                    | None -> body

                if List.isEmpty finallyForms then
                    caught
                else
                    ETryFinally(caught, parseBody finallyForms listRange, r)

            // `(with-open ((name ctor) ...) body...)` — bind each resource,
            // and dispose it on the way out however the body ends.
            //
            // Each binding gets its *own* try/finally rather than one around
            // all of them, so a later constructor that throws still leaves the
            // earlier resources disposed.
            | "with-open" ->
                match args with
                | SList(bindings, _) :: bodyForms ->
                    if bodyForms.IsEmpty then
                        failwithf $"Invalid with-open at %s{Lexer.formatPos r}: it has no body."

                    let body = parseBody bodyForms listRange

                    List.foldBack
                        (fun binding acc ->
                            match binding with
                            | SList([ Ident name; value ], bindRange) ->
                                let dispose =
                                    EApp(EIdent(".Dispose", bindRange), [ EIdent(name, bindRange) ], bindRange)

                                ELet(
                                    name,
                                    false,
                                    [],
                                    None,
                                    parseExpr value,
                                    ETryFinally(acc, dispose, bindRange),
                                    bindRange
                                )
                            | bad ->
                                failwithf
                                    $"Invalid with-open binding at %s{Lexer.formatPos (getRange bad)}: expected (name expression).")
                        bindings
                        body
                | _ ->
                    failwithf
                        $"Invalid with-open at %s{Lexer.formatPos r}: expected (with-open ((name expression) ...) body...)"

            // `(parameterize ((param value) ...) body...)` — install each
            // binding in the dynamic environment, and put it back however the
            // body ends.
            //
            // The shape is `with-open`'s, for the same reason: a saved value in
            // an ordinary `let`, and a `try/finally` restoring it. What is saved
            // is the *whole* environment rather than the one parameter's old
            // value, so one `finally` undoes a binding whichever slot it went
            // to — a port field or the champ.
            //
            // Also like `with-open`, and unlike R7RS: the bindings nest, so
            // they are sequential rather than simultaneous. A later value
            // expression sees the earlier bindings already installed, and a
            // later one that throws leaves the earlier ones restored. The `let*`
            // reading is the one that matches the rest of the language — a
            // keyword argument's default may already read an earlier parameter.
            //
            // Note the binder is an arbitrary *expression*, not a name: a
            // parameter is a value, so `(parameterize (((config-port c) w)) ...)`
            // is as legitimate as naming one directly.
            | "parameterize" ->
                match args with
                | SList(bindings, _) :: bodyForms ->
                    if bodyForms.IsEmpty then
                        failwithf $"Invalid parameterize at %s{Lexer.formatPos r}: it has no body."

                    let body = parseBody bodyForms listRange

                    List.foldBack
                        (fun binding acc ->
                            match binding with
                            | SList([ param; value ], bindRange) ->
                                let saved = Gensym.fresh "dynsaved"

                                let push =
                                    EApp(
                                        EIdent("parameter-push!", bindRange),
                                        [ parseExpr param; parseExpr value ],
                                        bindRange
                                    )

                                let restore =
                                    EApp(EIdent("dyn-restore!", bindRange), [ EIdent(saved, bindRange) ], bindRange)

                                ELet(
                                    saved,
                                    false,
                                    [],
                                    None,
                                    push,
                                    ETryFinally(acc, restore, bindRange),
                                    bindRange
                                )
                            | bad ->
                                failwithf
                                    $"Invalid parameterize binding at %s{Lexer.formatPos (getRange bad)}: expected (parameter expression).")
                        bindings
                        body
                | _ ->
                    failwithf
                        $"Invalid parameterize at %s{Lexer.formatPos r}: expected (parameterize ((parameter expression) ...) body...)"

            | "Tuple" -> ETuple(processArgs args, listRange)

            // Quoted list literal: '(1 2 3) → Cons chain
            | "quoted-list" -> desugarQuotedList parseExpr args listRange

            // Vec literal: [1 2 3] → EVec
            | "vec-literal" -> EVec(processArgs args, listRange)

            // List special form: (list 1 2 3) → EList, same as [1 2 3] for vecs.
            // `list` used as a bare value (not in call position) remains an
            // ordinary identifier that references the prelude rest-arg function.
            | "list" -> EList(processArgs args, listRange)

            // Arithmetic and comparison, at any arity. Handled here rather than
            // in `Inference` so that everything downstream — the type checker,
            // the inliner and the operator emission in `Codegen` — continues to
            // see only the binary form it already understands.
            //
            // Keyword arguments are meaningless on an operator, so a call
            // carrying one falls through to ordinary application and fails
            // there, where the message is about the real mistake.
            | _ when
                (List.contains sym foldingOps || List.contains sym chainingOps)
                && not (args |> List.exists (function
                                             | SAtom { Token = Keyword _ } -> true
                                             | _ -> false))
                ->
                desugarNaryOp sym (processArgs args) listRange

            // Standard function application
            | _ -> EApp(EIdent(sym, getRange head), processArgs args, listRange)


        | _ ->
            // Fallback for tuples or unquoted lists
            EApp(parseExpr head, processArgs args, listRange)

    | SList([], listRange) -> ETuple([], listRange)

    // Explicit token catches for better debugging
    | SAtom { Token = Comma } -> failwithf $"Unexpected comma at %s{Lexer.formatPos r}"
    | SAtom { Token = Quote } -> failwithf $"Unexpected quote at %s{Lexer.formatPos r}"
    | _ -> failwithf $"Unexpected expression at %s{Lexer.formatPos r}"

/// Desugars a `(do ...)` block into `bind` / `pure`.
///
/// Each generated `bind` carries the range of *its own* form. Giving them all
/// the range of the opening paren made every type error in a ten-step block
/// point at the same character.
and desugarDo (forms: SExpr list) (fallbackRange: Range) : Expr =
    let named (s: SExpr) =
        match s with
        | SList(SAtom { Token = Keyword k } :: rest, r) -> Some(k, rest, r)
        | _ -> None

    match forms with
    | [] -> failwithf $"Invalid do syntax at %s{Lexer.formatPos fallbackRange}: the block is empty"

    | [ last ] ->
        match named last with
        | Some("return", [ e ], r) -> EApp(EIdent("pure", r), [ parseExpr e ], r)
        | Some("return", _, r) -> failwithf $"Invalid (:return ...) at %s{Lexer.formatPos r}. Expected: (:return expr)"
        | Some(("bind" | "let" | "then") as k, _, r) ->
            failwithf
                $"A (do ...) block cannot end with (:%s{k} ...) at %s{Lexer.formatPos r}. Its last form is the block's value."
        // Any `m a` may be the last form, which is what lets a monadic loop put
        // its own recursive call in tail position.
        | _ -> parseExpr last

    | first :: rest ->
        let continuation () = desugarDo rest fallbackRange

        match named first with
        // `:bind` takes an identifier, deliberately. A pattern would force the
        // failure question — what `(:bind (Some x) e)` means when the match
        // fails — which wants `MonadFail` rather than a match that can throw.
        | Some("bind", [ SAtom { Token = Symbol name }; e ], r) ->
            EApp(EIdent("bind", r), [ parseExpr e; EFun([ name ], continuation (), Ordinary, r) ], r)
        | Some("bind", _, r) ->
            failwithf $"Invalid (:bind ...) at %s{Lexer.formatPos r}. Expected: (:bind name expr) — a plain identifier, not a pattern."

        | Some("let", [ SAtom { Token = Symbol name }; e ], r) ->
            ELet(name, false, [], None, parseExpr e, continuation (), r)
        | Some("let", _, r) -> failwithf $"Invalid (:let ...) at %s{Lexer.formatPos r}. Expected: (:let name expr)"

        // `>>`, and named for what it is. Calling it `:do` would invite reading
        // it as a variable-less `:bind`, which in a strict language it is not:
        // on `List`, `>>` multiplies out the elements it discards.
        | Some("then", [ e ], r) ->
            EApp(EIdent("bind", r), [ parseExpr e; EFun([ "_" ], continuation (), Ordinary, r) ], r)
        | Some("then", _, r) -> failwithf $"Invalid (:then ...) at %s{Lexer.formatPos r}. Expected: (:then expr)"

        | Some("return", _, r) ->
            failwithf $"(:return ...) at %s{Lexer.formatPos r} must be the last form of its (do ...) block."

        | Some(k, _, r) -> failwithf $"Unknown (do ...) form ':%s{k}' at %s{Lexer.formatPos r}"

        // A plain form in non-tail position is an ordinary statement, run for
        // its effect. It is *not* a variable-less bind.
        | None -> ELet("_", false, [], None, parseExpr first, continuation (), getRange first)

// ---------------------------------------------------------------------------
// (loop ...)
// ---------------------------------------------------------------------------

/// Whether `(loop ...)` is the loop facility rather than a call to something
/// named `loop`.
///
/// `(let loop ((i 0)) ... (loop (+ i 1)))` is how every named `let` in the
/// language recurses, so the head symbol cannot be claimed outright. A clause is
/// a keyword-headed list and an argument expression is not, which tells the two
/// apart without reserving the name.
and private isLoopForm (args: SExpr list) : bool =
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
and private splitCollector (s: SExpr) : Expr * SExpr =
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
                        keywords |> List.collect (fun (k, v) -> [ parseExpr k; parseExpr v ])

                    EApp(EIdent(head, r), (construction |> List.map parseExpr) @ kwArgs, r)

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
and private desugarSeqLoop (allForms: SExpr list) (r: Range) : Expr =
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

    let loopExpr = desugarLoop (clauseForms |> List.map rewriteClause) r

    let body =
        match finishForm with
        | None -> loopExpr
        // The loop runs for effect and then the trailing yield does; `_` is how
        // every other statement position in this file is spelled.
        | Some e -> ELet("_", false, [], None, loopExpr, EYield(parseExpr e, getRange e), r)

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
and private desugarComprehension (allForms: SExpr list) (r: Range) : Expr =
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
        desugarSeqLoop (clauses @ guard @ [ SList([ at (Keyword "yield"); expr ], getRange expr) ]) r

    | SAtom { Token = Symbol "seqing" } :: _ ->
        failwithf $"{{seqing ...}} at %s{Lexer.formatPos r} takes exactly one expression to yield."

    | _ ->
        let name = Gensym.fresh "comp"
        let guard = accWhen |> Option.toList |> List.collect (fun c -> [ at (Keyword "when"); c ])
        let acc = SList([ at (Keyword "acc"); at (Symbol name); SList(accParts, r) ] @ guard, r)
        desugarLoop (clauses @ [ acc; at (Symbol "=>"); at (Symbol name) ]) r

/// Desugars `(loop clause... [=> expr])`.
///
/// A loop is a left fold with early exit that always delivers a result: every
/// exit runs the same finish block. That is why the accumulators are hoisted —
/// they hold state across the whole loop and have to be visible at the end.
and desugarLoop (allForms: SExpr list) (r: Range) : Expr =
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

    let call (name: string) (args: Expr list) (cr: Range) = EApp(EIdent(name, cr), args, cr)

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
                let collectorExpr, stepForm = splitCollector collector

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
                      CollectorExpr = call "folding" [ EIdent("false", cr) ] cr
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
                (fun sn cn -> cn, call "next" [ EIdent(sn, cr); EIdent(cn, cr) ] cr)
                levels[i].SeqNames
                levels[i].CurNames

        // No update is a loop-invariant `:with`: contributing no override leaves
        // the slot holding what it held, and emits nothing at all rather than a
        // self-assignment.
        let withs =
            List.zip levels[i].WithNames levels[i].Withs
            |> List.choose (fun (slot, (_, _, update, _, _)) ->
                update |> Option.map (fun u -> slot, parseExpr u))

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
                    Some(call "done?" [ EIdent(levels[i].SeqNames[fi], r); EIdent(levels[i].CurNames[fi], r) ] r)
                | Choice2Of2 wi ->
                    let (_, _, _, endCond, _) = levels[i].Withs[wi]
                    endCond |> Option.map parseExpr)

        let rec anyOf ts =
            match ts with
            | [ last ] -> last
            | t :: tl -> EIf(t, EIdent("true", r), anyOf tl, r)
            | [] -> EIdent("false", r)

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
    /// perfectly well bind a name of its own that happens to collide.
    let rejectWithInFinish (e: Expr) : unit =
        let names = Set.ofList withVarNames

        let rec patternBinds (p: Pattern) : string list =
            match p with
            | PIdent(n, _) -> [ n ]
            | PList(items, tail, _)
            | PVec(items, tail, _) -> (items @ Option.toList tail) |> List.collect patternBinds
            | PTuple(items, _)
            | PConstruct(_, items, _) -> items |> List.collect patternBinds
            | _ -> []

        let rec go (bound: Set<string>) (x: Expr) =
            let sub = go bound

            match x with
            | EIdent(n, ir) when Set.contains n names && not (Set.contains n bound) ->
                failwithf
                    $"'%s{n}' at %s{Lexer.formatPos ir} is a (:with ...) variable, and a loop variable is not in scope after the loop: the finish block is reached from every exit, and an inner level's variables do not exist at an exit taken from an outer one. Carry it out with an accumulator — (:acc last (folding 0 %s{n})) — and name that in the '=>' instead."
            | EFun(args, body, _, _) -> go (Set.union bound (Set.ofList args)) body
            | ELet(n, _, args, _, value, body, _) ->
                go (Set.union bound (Set.ofList args)) value
                go (Set.add n bound) body
            | ELetMono(n, value, body, _) ->
                sub value
                go (Set.add n bound) body
            | ELetMutable(n, _, value, body, _) ->
                sub value
                go (Set.add n bound) body
            | ELetTuple(ns, value, body, _) ->
                sub value
                go (Set.union bound (Set.ofList ns)) body
            | ELetRec(bindings, body, _) ->
                let inner =
                    Set.union bound (bindings |> List.map (fun (n, _, _, _, _) -> n) |> Set.ofList)

                for (_, _, args, _, v) in bindings do
                    go (Set.union inner (Set.ofList args)) v

                go inner body
            | EMatch(target, clauses, _) ->
                sub target

                for (pat, guard, body) in clauses do
                    let inner = Set.union bound (patternBinds pat |> Set.ofList)
                    Option.iter (go inner) guard
                    go inner body
            | _ -> exprChildren x |> List.iter sub

        go Set.empty e

    let finishBlockBody =
        // `:final`'s accumulator is not the author's and has no business in the
        // result, so only the declared ones are delivered or even finished.
        let declared = accInfo |> List.filter (fun slot -> not slot.Hidden)

        let result =
            match finishForm with
            | Some e ->
                let parsed = parseExpr e
                rejectWithInFinish parsed
                parsed
            | None ->
                match declared with
                // Nothing to deliver: a loop with no accumulators and no `=>`
                // is pure effect, and types as `void` rather than as unit so
                // that it can be the body of a `void` function. `when` is the
                // language's only void-typed expression form, and with a
                // constant-false condition it is also the emptiest one.
                | [] -> EWhen(EIdent("false", r), ETuple([], r), false, r)
                | [ slot ] -> EIdent(slot.Name, slot.Range)
                | _ -> ETuple(declared |> List.map (fun slot -> EIdent(slot.Name, slot.Range)), r)

        List.foldBack
            (fun slot acc ->
                ELet(
                    slot.Name,
                    false,
                    [],
                    None,
                    call "finish" [ EIdent(slot.Collector, slot.Range); EIdent(slot.Name, slot.Range) ] slot.Range,
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
            call "step" [ EIdent(slot.Collector, cr); EIdent(slot.Name, cr); parseExpr slot.StepForm ] cr

        let value =
            match slot.Modifier with
            | None -> stepped
            | Some cond -> EIf(parseExpr cond, stepped, EIdent(slot.Name, cr), cr)

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
            @ (List.map2 (fun cn t -> cn, call "start" [ EIdent(t, cr) ] cr) levels[i].CurNames temps)
            @ (List.zip levels[i].WithNames levels[i].Withs
               |> List.map (fun (slot, (_, start, _, _, _)) -> slot, parseExpr start))
            |> Map.ofList

        List.foldBack
            (fun (t, (_, sequence, fr)) acc -> ELetMono(t, parseExpr sequence, acc, fr))
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
            bindLoopPattern pat (parseExpr value) (buildClauses level tl accsLeft) cr

        // In a named loop the *final* `:do` owns the continue edge: if it tail
        // calls the loop, that is the jump, and if it completes without one the
        // loop leaves through the finish block like any other exit.
        | [ LDo(exprs, cr) ] when userLoopName.IsSome && level = maxLevel ->
            let name = userLoopName.Value
            let statements = exprs |> List.take (exprs.Length - 1)
            let final = List.last exprs

            List.foldBack
                (fun e acc ->
                    let parsed = parseExpr e
                    rejectLoopName name parsed
                    ELet("_", false, [], None, parsed, acc, cr))
                statements
                (continueEdge level name (parseExpr final) cr)

        | LDo(exprs, cr) :: tl ->
            List.foldBack
                (fun e acc -> ELet("_", false, [], None, parseExpr e, acc, cr))
                exprs
                (buildClauses level tl accsLeft)

        // Skips the rest of *this* iteration of *this* level. Clauses above it
        // have already run, so an accumulator stepped before it keeps what it
        // was given.
        | LWhen(cond, cr) :: tl ->
            EIf(parseExpr cond, buildClauses level tl accsLeft, advanceLevel level cr, cr)

        // Abandons this level and resumes the enclosing one — an early return
        // from a subloop, not an iteration skip. At level 0 the two would
        // coincide, which is a coincidence rather than a definition.
        | LEndSubloop(cond, cr) :: tl ->
            if level = 0 then
                failwithf
                    $"(:end-subloop-if ...) at %s{Lexer.formatPos cr} is at the outermost level, where there is no enclosing loop to resume. Use (:when ...) to skip an iteration, or (:break ...) to leave the loop."

            EIf(parseExpr cond, exitLevel level cr, buildClauses level tl accsLeft, cr)

        // Leaves the whole loop from any level, through the finish block.
        // Accumulators stepped earlier in this iteration keep what they were
        // given.
        | LBreak(cond, cr) :: tl ->
            EIf(parseExpr cond, finishBlock cr, buildClauses level tl accsLeft, cr)

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
                bindLoopPattern pat (call "current" [ EIdent(sn, cr); EIdent(cn, cr) ] cr) acc cr)
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

            (lvl.Member, true, slotNames lvl.Index, None, body))

    // The finish member. It calls nothing, so `LetRecify` gives it a component
    // of its own and it is bound ahead of the loop group rather than becoming a
    // case in the same switch — which costs one call on the way out and saves a
    // copy of the block at every other exit.
    let members = members @ [ (exitName, true, accNames, None, finishBlockBody) ]

    // In `slotNames 0`'s order: level 0's cursors, then its `:with` slots, then
    // the accumulators.
    let initialArgs =
        (levels[0].SeqNames
         |> List.map (fun sn -> call "start" [ EIdent(sn, r) ] r))
        @ (levels[0].Withs |> List.map (fun (_, start, _, _, _) -> parseExpr start))
        @ (accInfo |> List.map (fun slot -> call "init" [ EIdent(slot.Collector, slot.Range) ] slot.Range))

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
        (fun (sn, (_, sequence, cr)) acc -> ELetMono(sn, parseExpr sequence, acc, cr))
        (List.zip levels[0].SeqNames levels[0].Fors)
        withCollectors

and parseBody (exprs: SExpr list) (fallbackRange: Range) : Expr =
    let rec collectDefs acc remaining =
        match remaining with
        | SList(SAtom { Token = Symbol "def" } :: SAtom { Token = Symbol name } :: [ expr ], _) :: rest ->
            // isFun = false, args = []
            collectDefs ((name, false, [], None, parseExpr expr) :: acc) rest

        | SList(SAtom { Token = Symbol "def" } :: SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) :: [ expr ], _) :: rest ->
            collectDefs ((name, false, [], Some(parseType tType), parseExpr expr) :: acc) rest

        // A body-local `defbjo`. Rejected rather than parsed, because a local
        // function is emitted as a C# *local function* and a local function
        // inside an async method is not itself async — the body would compile
        // to something that cannot await, and the failure would land in
        // generated code rather than here. Lift it to the top level.
        | SList(SAtom { Token = Symbol "defbjo" } :: SList(SAtom { Token = Symbol name } :: _, _) :: _, r) :: _ ->
            failwithf
                $"Syntax Error at %s{Lexer.formatPos r}: a bjoroutine may only be defined at the top level, and '%s{name}' is inside a body. A local definition is compiled as a C# local function, which cannot suspend."

        | SList(SAtom { Token = Symbol "defun" } :: SList(SAtom { Token = Symbol name } :: args, _) :: rest, r) :: rest' ->
            let argNames = parseDefunArgs args |> List.map fst
            let _, bodyExprs = parseDefunRest rest
            let fBody = parseBody bodyExprs r
            // isFun = true, args = argNames
            collectDefs ((name, true, argNames, None, fBody) :: acc) rest'

        | _ -> (List.rev acc, remaining)

    and parseItems remaining =
        match remaining with
        | [] -> ETuple([], fallbackRange)

        | SList(SAtom { Token = Symbol "def/mutable" } :: SAtom { Token = Symbol name } :: [ expr ], r) :: rest ->
            ELetMutable(name, None, parseExpr expr, parseItems rest, fallbackRange)

        | SList(SAtom { Token = Symbol "def/mutable" } :: SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) :: [ expr ], r) :: rest ->
            ELetMutable(name, Some(parseType tType), parseExpr expr, parseItems rest, fallbackRange)

        // Local tuple destructuring: (def (a b) expr).
        // Recognized before the `def` case below to prevent infinite recursion.
        | SList(SAtom { Token = Symbol "def" } :: SList(names, _) :: [ expr ], r) :: rest when
            not names.IsEmpty
            && names
               |> List.forall (function
                   | SAtom { Token = Symbol _ }
                   | SAtom { Token = Comma } -> true
                   | _ -> false)
            ->
            let rawNames =
                names
                |> List.choose (function
                    | SAtom { Token = Symbol n } -> Some n
                    | _ -> None)
            let tupleNames =
                match rawNames with
                | "Tuple" :: restNames -> restNames
                | _ -> rawNames

            ELetTuple(tupleNames, parseExpr expr, parseItems rest, r)
        // `defbjo` is listed so that it reaches `collectDefs`, which rejects it
        // by name; left out, it would fall through to an ordinary application
        // and fail with "Unbound variable: defbjo".
        | (SList(SAtom { Token = Symbol "def" } :: _, _)) :: _
        | (SList(SAtom { Token = Symbol "defbjo" } :: _, _)) :: _
        | (SList(SAtom { Token = Symbol "defun" } :: _, _)) :: _ ->
            let defs, rest = collectDefs [] remaining

            // Nothing recognized means nothing consumed, and recursing on an
            // unchanged list does not terminate. Whatever shape this is, saying
            // so is the only safe answer.
            if defs.IsEmpty then
                let r = getRange (List.head remaining)

                failwithf
                    $"Invalid def form at %s{Lexer.formatPos r}. Expected (def name expr), (def (: name type) expr), (def (a b ...) expr) or (defun (name args...) body)."

            ELetRec(defs, parseItems rest, fallbackRange)

        | [ expr ] -> parseExpr expr

        | expr :: rest -> ELet("_", false, [], None, parseExpr expr, parseItems rest, fallbackRange)

    parseItems exprs

// New defun arg parser for top-level defuns with keyword/rest support
let rec parseNewDefunArgs (args: SExpr list) : DefunArg list =
    match args with
    | [] -> []
    | SAtom { Token = Symbol n } :: rest -> MandatoryArg n :: parseNewDefunArgs rest
    | SAtom { Token = Comma } :: rest -> parseNewDefunArgs rest
    | SList(SAtom { Token = Keyword name } :: [ defaultExpr ], _) :: rest ->
        KeywordArg(name, parseExpr defaultExpr) :: parseNewDefunArgs rest
    | SAtom { Token = Keyword "rest" } :: SAtom { Token = Symbol name } :: rest ->
        if not rest.IsEmpty then
            failwithf $"Rest argument must be the last argument at %s{Lexer.formatPos (getRange (List.head rest))}"
        [RestArg name]
    | SAtom { Token = Keyword name } :: defaultExpr :: rest ->
        KeywordArg(name, parseExpr defaultExpr) :: parseNewDefunArgs rest
    | bad :: _ -> failwithf $"Invalid defun argument at %s{Lexer.formatPos (getRange bad)}"

/// What a foreign import clause said, beyond its alias and target.
///
/// A record rather than a wider tuple: the clause has grown three optional
/// pieces and a positional read of five values at two call sites was already
/// hard to get right.
type ForeignImportOptions =
    { ExplicitType: FType option
      Exceptions: string list
      /// `#:async` — the target returns a task, and calling it is a yield
      /// point. The Bjolang type is the task's *result*; `Task` is never a
      /// Bjolang type. See concurrency-design.md §7.2.
      IsAsync: bool
      /// `#:uncancellable` — do not thread the ambient token into this call.
      /// Required where the method has no `CancellationToken` overload, so that
      /// "this cannot be cancelled" is written where it is decided rather than
      /// discovered when a `choose` leaks work.
      Uncancellable: bool
      /// `#:cancellable` — thread the ambient token into a call that is *not*
      /// `#:async`.
      ///
      /// Needed because a `CancellationToken` parameter is not always optional.
      /// `File.ReadLinesAsync` hands back an `IAsyncEnumerable` rather than a
      /// task, so it is not an `#:async` import, and every one of its overloads
      /// takes a token — which makes it uncallable without this.
      Cancellable: bool }

/// One clause of `import/extern` or `import/class`.
///
/// Both forms are spelled the same way — a Bjolang name, then a colon form
/// naming the fully qualified .NET target, its type, and optionally the
/// exceptions the call is allowed to turn into an `Err` — so one reader does
/// for both.
let parseForeignImportClause (formName: string) (s: SExpr) : string * string * ForeignImportOptions * Range =
    let r = getRange s

    let malformed () : 'a =
        failwithf
            $"Syntax error in %s{formName} at %s{Lexer.formatPos r}: expected (alias (: Fully.Qualified.Target type)), the type optionally followed by #:exceptions (ExceptionType ...), #:async and #:uncancellable."

    match s with
    | SList([ SAtom { Token = Symbol alias }
              SList(SAtom { Token = Colon } :: SAtom { Token = Symbol clrTarget } :: rest, _) ],
            _) ->
        // The signature is optional. Given, it is enforced against what
        // reflection finds; omitted, the arguments at each call site decide.
        let explicitType, optionForms =
            match rest with
            | SAtom { Token = Keyword _ } :: _ -> None, rest
            | [] -> None, []
            | t :: tail -> Some(parseType t), tail

        // Order-independent, because there is no reason for it not to be and
        // three positional keywords would be one more thing to remember.
        // `opts` is annotated because `ExternImportSpec` carries these same
        // four labels, so a copy-and-update on it does not say which record is
        // being updated.
        let rec readOptions (opts: ForeignImportOptions) forms =
            match forms with
            | [] -> opts
            | SAtom { Token = Keyword "exceptions" } :: SList(names, _) :: tail ->
                if names.IsEmpty then
                    failwithf
                        $"Syntax error in %s{formName} at %s{Lexer.formatPos r}: #:exceptions names no exception types. Leave it off entirely to let everything propagate."

                let exceptions =
                    names
                    |> List.map (function
                        | SAtom { Token = Symbol n } -> n
                        | bad ->
                            failwithf
                                $"Syntax error in %s{formName} at %s{Lexer.formatPos (getRange bad)}: #:exceptions takes fully qualified .NET exception type names, as in System.IO.IOException.")

                readOptions { opts with Exceptions = exceptions } tail
            | SAtom { Token = Keyword "async" } :: tail -> readOptions { opts with IsAsync = true } tail
            | SAtom { Token = Keyword "uncancellable" } :: tail -> readOptions { opts with Uncancellable = true } tail
            | SAtom { Token = Keyword "cancellable" } :: tail -> readOptions { opts with Cancellable = true } tail
            | _ -> malformed ()

        let options =
            readOptions
                { ExplicitType = explicitType
                  Exceptions = []
                  IsAsync = false
                  Uncancellable = false
                  Cancellable = false }
                optionForms

        alias, clrTarget, options, r
    | _ -> malformed ()

let rec parseDecl (s: SExpr) : Decl =
    let r = getRange s

    match s with
    | SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) ->
        DSignature(name, parseType tType, [], r)
    // (: name type (where (TraitName %var) ...)) — signature with trait constraints
    | SList(SAtom { Token = Colon } :: SAtom { Token = Symbol name } :: tType :: SList(SAtom { Token = Symbol "where" } :: constraintExprs, _) :: _, _) ->
        let constraints =
            constraintExprs |> List.choose (function
                | SList([ SAtom { Token = Symbol traitName }; SAtom { Token = QuotedSymbol varName } ], _) ->
                    Some (traitName, "'" + varName)
                | SList([ SAtom { Token = Symbol traitName }; SAtom { Token = Symbol varName } ], _) ->
                    Some (traitName, varName)
                | _ -> None)
        DSignature(name, parseType tType, constraints, r)

    | SList(SAtom { Token = Symbol "import" } :: imports, _) ->
        // Parse paths like (io readline) into ["io"; "readline"]
        let parseImportPath =
            function
            | SAtom { Token = StringLit s } -> RelativePath s
            | SList(pathNodes, _) ->
                pathNodes
                |> List.map (function
                    | SAtom { Token = Symbol p } -> p
                    | _ -> failwithf $"Invalid import path element at %s{Lexer.formatPos r}")
                |> ModulePath
            | _ -> failwithf $"Invalid import syntax at %s{Lexer.formatPos r}"

        DImport(List.map parseImportPath imports, r)

    // (import/extern (write-line (: System.Console.WriteLine (-> string void))) ...)
    | SList(SAtom { Token = Symbol "import/extern" } :: clauses, _) ->
        if clauses.IsEmpty then
            failwithf $"Syntax error in import/extern at %s{Lexer.formatPos r}: it imports nothing."

        let specs =
            clauses
            |> List.map (fun c ->
                let alias, target, opts, cr = parseForeignImportClause "import/extern" c

                { Alias = alias
                  ClrTarget = target
                  ExplicitType = opts.ExplicitType
                  Exceptions = opts.Exceptions
                  IsAsync = opts.IsAsync
                  Uncancellable = opts.Uncancellable
                  Cancellable = opts.Cancellable
                  Range = cr })

        DImportExtern(specs, r)

    // (import/class (StreamWriter (: System.IO.StreamWriter (-> string StreamWriter))) ...)
    | SList(SAtom { Token = Symbol "import/class" } :: clauses, _) ->
        if clauses.IsEmpty then
            failwithf $"Syntax error in import/class at %s{Lexer.formatPos r}: it imports nothing."

        let specs =
            clauses
            |> List.map (fun c ->
                let alias, target, opts, cr = parseForeignImportClause "import/class" c

                // A constructor is never a task. Silently ignoring the flag
                // would mean an import that reads as async and is not.
                if opts.IsAsync || opts.Uncancellable || opts.Cancellable then
                    failwithf
                        $"Syntax error in import/class at %s{Lexer.formatPos cr}: #:async, #:cancellable and #:uncancellable describe how a call is made, and a constructor is not made that way. They belong on an import/extern clause."

                { Alias = alias
                  ClrClass = target
                  ConstructorType = opts.ExplicitType
                  Exceptions = opts.Exceptions
                  Range = cr })

        DImportClass(specs, r)

    | SList(SAtom { Token = Symbol "export" } :: exports, _) ->
        // Parse items like poop-on-you
        let exportNames =
            exports
            |> List.map (function
                | SAtom { Token = Symbol e } -> e
                | _ -> failwithf $"Invalid export item at %s{Lexer.formatPos r}")

        DExport(exportNames, r)

    | SList(SAtom { Token = Symbol "re-export" } :: reExports, _) ->
        let reExportNames =
            reExports
            |> List.map (function
                | SAtom { Token = Symbol e } -> e
                | _ -> failwithf $"Invalid re-export item at %s{Lexer.formatPos r}")

        DReExport(reExportNames, r)

    | SList(SAtom { Token = Symbol "module" } :: SAtom { Token = Symbol name } :: body, _) ->
        DModule(name, List.map parseDecl body, r)

    | SList(SAtom { Token = Symbol "def" } :: SAtom { Token = Symbol name } :: [ expr ], _) ->
        DDef(name, parseExpr expr, r)

    | SList(SAtom { Token = Symbol "def" } :: SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) :: [ expr ], _) ->
        DDef(name, parseExpr expr, r)

    | SList(SAtom { Token = Symbol "def" } :: SList(names, _) :: [ expr ], _) ->
        let rawNames =
            names
            |> List.map (function
                | SAtom { Token = Symbol n } -> n
                | SAtom { Token = Comma } -> ""
                | _ -> failwith "Invalid tuple def")
            |> List.filter ((<>) "")
        let tupleNames =
            match rawNames with
            | "Tuple" :: restNames -> restNames
            | _ -> rawNames

        DDefTuple(tupleNames, parseExpr expr, r)

    | SList(SAtom { Token = Symbol "def/mutable" } :: SAtom { Token = Symbol name } :: [ expr ], _) ->
        DDefMutable(name, parseExpr expr, r)

    | SList(SAtom { Token = Symbol "def/mutable" } :: SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) :: [ expr ], _) ->
        DDefMutable(name, parseExpr expr, r)

    | SList(SAtom { Token = Symbol(("defun" | "defbjo") as definer) } :: SList(SAtom { Token = Symbol name } :: args, _) :: rest, _) ->
        let colour = if definer = "defbjo" then Suspending else Ordinary
        let parsedArgs = parseNewDefunArgs args
        // Skip optional inline return type annotation (backward compat, ignored — type comes from signature)
        let bodyExprs =
            match rest with
            | SAtom { Token = Colon } :: _ :: body -> body
            | body -> body
        DDefun(name, parsedArgs, parseBody bodyExprs r, colour, r)
    | SList(SAtom { Token = Symbol "type" } :: typeDefs, _) -> DType(List.map parseTypeDef typeDefs, r)

    | SList(SAtom { Token = Symbol "type-rec" } :: typeDefs, _) -> DTypeRec(List.map parseTypeDef typeDefs, r)

    | SList (SAtom { Token = Symbol "def/trait" } ::
             SList (SAtom { Token = Symbol traitName } :: [ implementorSpec ], _) ::
             body, r) ->

        // `(Show %c)` declares an implementor of arity 0 — an interface trait.
        // `(Monad (%m %a))` writes it applied, which is what no C# interface can
        // express and what makes the trait inline-only.
        let implementorVar, holeArity =
            match implementorSpec with
            | SAtom { Token = QuotedSymbol v } -> v, 0
            | SList (SAtom { Token = QuotedSymbol v } :: holeArgs, hr) ->
                if holeArgs.IsEmpty then
                    failwithf
                        $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos hr}: (%%%s{v}) applies the implementor to nothing. Write %%%s{v} instead."
                for a in holeArgs do
                    match a with
                    | SAtom { Token = QuotedSymbol _ } -> ()
                    | _ ->
                        failwithf
                            $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos hr}: the implementor may only be applied to type variables."
                v, holeArgs.Length
            | _ ->
                failwithf
                    $"Syntax error in def/trait '%s{traitName}': expected (%s{traitName} %%c) or (%s{traitName} (%%m %%a))."

        let mutable assocTypes = []
        let mutable signatures = []
        let mutable defaults = []

        for item in body do
            match item with
            // Match: (type 'item)
            | SList (SAtom { Token = Symbol "type" } :: SAtom { Token = QuotedSymbol assocName } :: [], _) ->
                assocTypes <- assocName :: assocTypes

            // Match: (: methodName signatureExpr)
            | SList (SAtom { Token = Colon } :: SAtom { Token = Symbol methodName } :: typeExpr :: [], _) ->
                signatures <- (methodName, parseType typeExpr) :: signatures

            // Match: (defun (methodName args...) body) — a default body, used by
            // any impl that does not write this method itself. The signature is
            // still declared separately: a default supplies the *body*, and the
            // type it is checked at comes from the impl, not from here.
            | SList (SAtom { Token = Symbol "defun" } :: _, _) as defunExpr ->
                defaults <- parseDecl defunExpr :: defaults

            // See the matching case in `def/impl`: the trait's signature has no
            // colour, so a suspending default body would be invisible to a
            // caller dispatching through it.
            | SList (SAtom { Token = Symbol "defbjo" } :: _, r) ->
                failwithf
                    $"Syntax Error at %s{Lexer.formatPos r}: a trait's default method body cannot be a bjoroutine yet — a trait signature has no way to say that calling a method suspends."

            | _ -> failwithf $"Syntax error in def/trait '%s{traitName}': Expected (type ...), (: ...) or (defun ...)."

        DTrait (traitName, implementorVar, holeArity, List.rev assocTypes, List.rev signatures, List.rev defaults, r)

    // Parse: (def/impl (TraitName (Vec 'a)) (type 'item 'a) (defun (get v i) ...))
    | SList (SAtom { Token = Symbol "def/impl" } ::
             SList (SAtom { Token = Symbol traitName } :: targetTypeExpr :: [], _) ::
             body, r) ->

        let targetType = parseType targetTypeExpr

        let mutable assocBindings = []
        let mutable methods = []

        for item in body do
            match item with
            // Match: (type 'item targetType)
            | SList (SAtom { Token = Symbol "type" } :: SAtom { Token = QuotedSymbol assocName } :: boundTypeExpr :: [], _) ->
                assocBindings <- (assocName, parseType boundTypeExpr) :: assocBindings

            // Match: (defun ...)
            | SList (SAtom { Token = Symbol "defun" } :: _, _) as defunExpr ->
                methods <- parseDecl defunExpr :: methods

            // A trait method that may suspend needs the trait's *signature* to
            // carry the colour, so that a call through a dictionary knows it is
            // a yield point before it knows which impl it reached. That is not
            // built, and silently accepting the definer would produce an impl
            // whose colour no caller can see.
            | SList (SAtom { Token = Symbol "defbjo" } :: _, r) ->
                failwithf
                    $"Syntax Error at %s{Lexer.formatPos r}: a trait implementation's method cannot be a bjoroutine yet — a trait signature has no way to say that calling a method suspends."

            | _ -> failwithf $"Syntax error in def/impl for '%s{traitName}': Expected (type ...) or (defun ...)."

        DImpl (traitName, targetType, List.rev assocBindings, List.rev methods, r)

    // Parse: (def/impl/extern (Foldable (Vec 'a)) (type 'item 'a))
    //
    // The bodyless counterpart of `def/impl`, emitted into a library's export
    // metadata so that whoever imports it can resolve the trait's associated
    // types and dispatch to the impl class compiled into that assembly.
    | SList (SAtom { Token = Symbol "def/impl/extern" } ::
             SList (SAtom { Token = Symbol traitName } :: targetTypeExpr :: [], _) ::
             body, r) ->

        let assocBindings =
            body
            |> List.map (function
                | SList (SAtom { Token = Symbol "type" } :: SAtom { Token = QuotedSymbol assocName } :: boundTypeExpr :: [], _) ->
                    assocName, parseType boundTypeExpr
                | _ -> failwithf $"Syntax error in def/impl/extern for '%s{traitName}': Expected (type ...).")

        DImplExtern (traitName, parseType targetTypeExpr, assocBindings, r)

    | _ -> failwithf $"Unknown declaration at %s{Lexer.formatPos r}"

let parseModule (exprs: SExpr list) : Decl list = List.map parseDecl exprs
