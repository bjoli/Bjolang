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
    /// A case with payload types, and whether it was marked `#:literal`.
    ///
    /// The marker names the case a literal is injected into when several cases
    /// of the union could carry one. Selection for a quoted literal goes by the
    /// payload's *head* constructor, so `(ProcSub (List ProcItem))` and
    /// `(ProcArgs (List string))` are indistinguishable to it; this is how the
    /// program says which was meant.
    | DataCase of string * FType list * bool * Range

type RecordField =
    { Name: string
      Type: FType
      /// `#:mutable` on the field: it may be written in place by
      /// `record-set!`, and only by the module that declared the record.
      ///
      /// A property of the *field* rather than of the type, because it decides
      /// two unrelated things about it: which half of the emitted C# record it
      /// lands in — a positional parameter is init-only, so a mutable field
      /// has to be declared in the body — and whether constructing the record
      /// is a syntactic value. The second is why it has to survive into a
      /// module's published metadata: an importer that did not know would
      /// generalize a construction over a cell that exists once.
      Mutable: bool
      Range: Range }

type TypeDefKind =
    | Alias of FType
    | Union of UnionCase list
    /// A record, and whether it is a *value* type (struct).
    | Record of RecordField list * bool
    /// A head with no body: what an `#:opaque` type is published as, and the
    /// only shape a module ever reads back for one.
    ///
    /// It carries the names of the members that did *not* cross — the union's
    /// cases, or the record's fields — and carries them for diagnostics alone.
    /// Nothing is registered under them, so a use resolves to nothing whether
    /// they are listed or not; listing them is what lets the failure say which
    /// type the name belongs to instead of claiming there is no such name. No
    /// secret is spent on it: the emitted C# members are public either way.
    | Opaque of string list

type TypeDef =
    { Name: string
      TypeArgs: string list
      Kind: TypeDefKind
      /// `#:opaque` on the declaration: the type name crosses the module
      /// boundary and its representation does not.
      ///
      /// Separate from `Kind = Opaque` because the two are the same fact on
      /// opposite sides of the boundary. Inside the declaring module the body
      /// is fully visible and `Kind` is the `Union` or `Record` as written —
      /// this flag is what tells `Exports` to publish a head instead. `Kind =
      /// Opaque` is what an importer reads back, and implies the flag.
      IsOpaque: bool
      Range: Range }


type Pattern =
    | PWildcard of Range
    | PIdent of string * Range
    | PInt of string * Range
    | PString of string * Range
    /// A Unicode scalar value. See `Lexer.CharLit`.
    | PChar of int * Range
    | PBool of bool * Range
    | PKeyword of string * Range
    | PQuotedSymbol of string * Range
    | PList of Pattern list * Pattern option * Range // (items, optional tail, range)
    | PVec of Pattern list * Pattern option * Range // (items, optional tail, range)
    | PTuple of Pattern list * Range
    | PConstruct of string * Pattern list * Range
    /// `(:is System.IO.IOException e)` — matches when the value is of that .NET type, binding it there at the narrowed type.
    /// The binder is optional. Used in `Err` arms.
    | PTypeTest of string * string option * Range
    /// Alternatives, none of which may bind: what `case` builds from a clause's
    /// datum list, and what makes one `switch` section carry several labels.
    | POr of Pattern list * Range

and Expr =
    | EInt of string * Range
    | EString of string * Range
    /// A Unicode scalar value. See `Lexer.CharLit`.
    | EChar of int * Range
    /// `#t` and `#f`, which are literals rather than names.
    ///
    /// They used to be the prelude bindings `true` and `false`, which put a
    /// boolean in the environment where a local could reach it: binding `true`
    /// redefined the literal, `and`, `or`, `not` and a loop's termination test,
    /// all of which are written in terms of it.
    | EBool of bool * Range
    /// A name the *compiler* wrote, which resolves where it was written rather
    /// than where it lands.
    ///
    /// A desugaring calls things by name — a loop calls `iterable-next`, string
    /// interpolation calls `->str`, a record's synthesised equality calls `=` —
    /// and those names went into the user's scope to be looked up like any
    /// other. Nothing a program binds can reach one of these.
    ///
    /// Only the compiler and the macro expander construct it. It is a leaf in
    /// every structural pass: not renamed, and not a free variable, because it
    /// does not refer to anything the surrounding code can bind.
    | EResolved of string * Range
    | EQuotedSymbol of string * Range
    | EKeyword of string * Range
    | EIdent of string * Range
    | ETuple of Expr list * Range
    | EApp of Expr * Expr list * Range
    | ECast of FType * Expr * Range
    // ELet (name, isFun, args, typeAnn, value, restOfScope, range)
    //
    // `args` is empty unless `isFun`, in which case the binding is a local
    // `defun` and takes the same argument grammar a top-level one does.
    // `typeAnn` is whatever the source wrote after a colon: the value's own
    // type for a `def`, and the *return* type for a `defun`.
    | ELet of string * bool * DefunArg list * FType option * Expr * Expr * Range
    /// A binding that is deliberately *not* generalized (used for associated-type projections).
    | ELetMono of string * Expr * Expr * Range
    // ELetRec (bindings, restOfScope, range)
    // binding tuple: (name, isFun, args, typeAnn, value), read as for `ELet`
    | ELetRec of (string * bool * DefunArg list * FType option * Expr) list * Expr * Range
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
    /// `(record-set! r (field value) ...)` — a write in place, to one or more
    /// `#:mutable` fields of the record `r` names.
    ///
    /// The target is a name rather than an expression for the same reason
    /// `ERecordUpdate`'s is: it is the shape that has been needed, and widening
    /// it later is a local change. Void, as every other write in the language
    /// is.
    | ERecordSet of string * (string * Expr) list * Range
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
    /// A positional parameter, with the type `(: name type)` gave it if it was
    /// written with one.
    | MandatoryArg of string * FType option
    | KeywordArg of string * Expr              // (#:keyword defaultValue)
    | RestArg of string                        // #:rest name

/// The positional parameters of an argument list, in order.
///
/// Keyword and rest parameters are a calling convention rather than a name the
/// body binds positionally, so callers that want "the parameters" in the plain
/// sense want exactly these. Several of them also compare the count back
/// against the whole list to find out whether anything was left out.
let mandatoryNames (args: DefunArg list) : string list =
    args
    |> List.choose (function
        | MandatoryArg(n, _) -> Some n
        | _ -> None)

/// Every parameter name, in the order a call's arguments are laid out:
/// mandatory, then keyword, then rest.
let allArgNames (args: DefunArg list) : string list =
    let pick f = args |> List.choose f

    pick (function MandatoryArg(n, _) -> Some n | _ -> None)
    @ pick (function KeywordArg(n, _) -> Some n | _ -> None)
    @ pick (function RestArg n -> Some n | _ -> None)

/// Where an import resolves from. A quoted string is relative to the importing
/// file; a list of symbols anchors to the installation.
type ImportPath =
    | RelativePath of string
    | ModulePath of string list

/// What an import does to the names it brings in.
///
/// Modifiers compose by nesting, and are read inside-out:
/// `(prefix (except (std strings) trim) "s/")` drops `trim` and then prefixes
/// what is left.
type ImportModifier =
    /// Defs and macros only. Types, constructors, traits and impls always
    /// arrive, because an imported signature is source text that has to
    /// resolve the types it mentions.
    | Only of string list
    | Except of string list
    /// Everything but impls: defs, macros, types, constructors, traits and
    /// trait methods.
    | Prefix of string
    | Postfix of string
    /// Defs and macros.
    | PrefixDefs of string
    | PostfixDefs of string
    /// Types, constructors and traits, including their methods.
    | PrefixTypes of string
    | PostfixTypes of string
    /// Defs and macros. Renaming a type or an individual trait method is an
    /// error: see `prefix-types`.
    | Rename of (string * string) list

type ImportSpec = { Path: ImportPath; Modifiers: ImportModifier list }

/// What kind of thing a visible name is a second spelling of.
///
/// Everything resolves through one table, but the consumers of it consult
/// different registries once they get there — and only a def keeps its visible
/// spelling, because only a def is *bound* under it.
type AliasKind =
    | AliasDef
    | AliasMacro
    | AliasType
    | AliasConstructor
    | AliasTrait

/// Where a visible name really comes from.
///
/// `OriginModule` is `""` for "wherever this declaration is" — the enclosing
/// module for an import, the compiling module for an alias, and no module at
/// all for a compiler builtin. Whoever resolves it fills that in, because only
/// they know which of the three it is.
type ImportAlias =
    { OriginModule: string
      OriginalName: string
      Kind: AliasKind }

/// An import with nothing done to it, which is what most of them are.
let plainImport (path: ImportPath) = { Path = path; Modifiers = [] }

/// One clause of `(import/extern ...)`: a .NET member bound as an ordinary
/// Bjolang function.
///
/// The member may be static or an instance one, and which it is comes from
/// reflection rather than from the clause: an instance member's receiver is
/// simply the alias's first argument. So `(: System.IO.StreamReader.ReadLineAsync
/// (-> StreamReader string) #:async)` is called as `(read-line r)`, and every
/// keyword below applies to it exactly as it does to a static import.
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
      /// `#:blocking`: calling this parks the thread it runs on.
      ///
      /// Nothing is emitted differently. It is a claim the blocking lint reads:
      /// a bjoroutine that reaches one of these does not suspend when it waits,
      /// it holds a pool thread for the duration, and the scheduler has one
      /// fewer. Declared rather than guessed because only the importer knows —
      /// a .NET method's signature says nothing about whether it waits.
      IsBlocking: bool
      /// `#:get`: the target is a property or field, and the alias reads it.
      IsGet: bool
      /// `#:set`: the target is a property or field, and the alias writes it.
      IsSet: bool
      Range: Range }

/// One clause of `(import/class ...)`: a .NET class, its name, and its
/// constructor.
type ClassImportSpec =
    { Alias: string
      /// The alias's own type parameters, written applied — `(Set %a)`.
      ///
      /// A .NET generic type is a type *constructor*, so the alias for one has
      /// to be applied before it is a type, and the names are needed to say in
      /// which order. Empty for an ordinary class, which is the common case and
      /// is written bare.
      TypeParams: string list
      ClrClass: string
      ConstructorType: FType option
      Exceptions: string list
      Range: Range }

type Decl =
    | DSignature of string * FType * (string * string) list * Range
    | DImport of ImportSpec list * Range
    /// `(:alias new-name existing-name)`. A second spelling of a binding or
    /// macro already in scope, sharing its scheme, its keyword and rest
    /// metadata, and its mutability.
    | DAlias of string * string * Range
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
    /// `(defbjouble (name args...) (#:sync body) (#:bjo body))`.
    ///
    /// One name, one signature, two hand-written bodies — the `#:sync` one and
    /// the `#:bjo` one, in that order however they were written. The *only*
    /// place two bodies are written by hand, and the reason is specific: the
    /// two halves call different .NET methods, and no inference derives that.
    ///
    /// Not desugared into two `DDefun`s by the parser, because the second one
    /// would need a signature and the signature is a separate form the parser
    /// has not seen yet. `checkDecl` does the split, where `sigs` is in scope.
    | DDefDouble of string * DefunArg list * Expr * Expr * Range
    | DType of TypeDef list * Range
    | DTypeRec of TypeDef list * Range
    // DTrait (Name, ImplementorVar, HoleArity, AssociatedTypes, Signatures, Defaults, ClrConstraint, Range)
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
    //
    // `ClrConstraint` is the .NET interface the trait stands for, if it was
    // written with `(#:clr-constraint (Iface %a))`: the interface name, the
    // arguments it was applied to, and each method's `#:clr-member` binding.
    // Unresolved here — neither the interface nor its members are looked up
    // until inference, where the diagnostic can say where they were written.
    //
    // The member bindings ride here rather than in `Signatures` so that the
    // signature list keeps its shape, and with it every site that reads a
    // trait's method names.
    | DTrait of string * string * int * string list * (string * FType) list * Decl list * (string * FType list * (string * string) list) option * Range
    /// A binding an imported module publishes: the name it is visible under
    /// here, where it actually lives, its type and its constraints.
    ///
    /// The visible name differs from the origin's when the import carried a
    /// modifier; the origin's *module* differs from the declaring one when the
    /// module publishing it was only a facade for it. Everything keyed
    /// internally — the `Module::name` qualified binding, the reference codegen
    /// emits — uses the origin; the visible name is only a spelling.
    /// DExtern (VisibleName, Origin, Type, Constraints, Range)
    | DExtern of string * ImportAlias * FType * (string * string) list * Range

    /// A spelling an import modifier produced for something that is not a
    /// binding: a type, a constructor, a trait or one of its methods.
    ///
    /// A def carries its own second name on its `DExtern`, because it is bound
    /// under it. These are not bound under anything — the declaration that
    /// introduces them keeps the name every registry is keyed on — so the
    /// spelling has to travel as a declaration of its own and is resolved away
    /// before any of those registries is consulted.
    /// DImportAlias (VisibleName, OriginalName, Kind, Range)
    | DImportAlias of string * string * AliasKind * Range
    /// `(import/extern (alias (: Clr.Target type #:exceptions (E ...))) ...)`
    | DImportExtern of ExternImportSpec list * Range
    /// `(import/class (Alias (: Clr.Class type #:exceptions (E ...))) ...)`
    | DImportClass of ClassImportSpec list * Range

    // One inline-trait method body, read back out of a compiled module's
    // metadata. It is the *untyped* expression: re-inferring it at the splice is
    // what gives it a type its trait signature cannot express.
    // DInlineImpl (TraitName, MethodName, Ctor, OriginModule, Params, Body, Qualification, Range)
    | DInlineImpl of string * string * string * string * string list * Expr * (string * string) list * Range

    /// Records that a name defined in this module is a macro.
    ///
    /// `def/macro` also produces a `DSignature` and a `DDefun` — a transformer
    /// is an ordinary function, and this says only that the compiler should run
    /// it at parse time rather than let anyone call it. It carries no body for
    /// the same reason: the body is the `DDefun`'s.
    ///
    /// It does not survive type checking. What survives is the macro list in
    /// the assembly's metadata, which is what an importing compilation reads.
    | DMacro of string * Range

    /// Records that a name is to be given no suspending copy: `(: name #:sync (-> ...))`.
    ///
    /// `#:sync` prevents the generation of an async counterpart for a function.
    /// This is used when an async copy would be forced to park (e.g., when 
    /// synchronous callbacks are stored in data structures).
    ///
    /// Like `DMacro`, it is carried as its own declaration since it provides
    /// metadata about a name. It does not survive type checking.
    | DSyncOnly of string * Range

    // DImpl (TraitName, TargetType, AssociatedTypeBindings, Constraints, Methods, Range)
    //
    // The constraints are the impl's `(where (Trait %v) ...)`, spelled exactly
    // as a signature's are: this impl holds only where those do.
    | DImpl of string * FType * (string * FType) list * (string * string) list * Decl list * Range

    // A declaration-only implementation: it records that the target type
    // implements the trait, and what its associated types are, without carrying
    // any method bodies. This is what a compiled module's metadata exports —
    // the methods themselves already live in that assembly.
    // DImplExtern (TraitName, TargetType, AssociatedTypeBindings, Constraints, Range)
    | DImplExtern of string * FType * (string * FType) list * (string * string) list * Range

/// Gets the source code location (Range) of a declaration.
///
/// This provides a fallback location for error reporting, ensuring that any
/// error during declaration processing can point to the declaration itself.
let declRange (decl: Decl) : Range =
    match decl with
    | DDef(_, _, r) | DDefun(_, _, _, _, r) | DDefDouble(_, _, _, _, r) | DDefTuple(_, _, r) | DDefMutable(_, _, r)
    | DSignature(_, _, _, r) | DType(_, r) | DTypeRec(_, r) | DTrait(_, _, _, _, _, _, _, r) | DImpl(_, _, _, _, _, r)
    | DImplExtern(_, _, _, _, r) | DInlineImpl(_, _, _, _, _, _, _, r)
    | DModule(_, _, r) | DImport(_, r) | DAlias(_, _, r) | DExport(_, r) | DReExport(_, r)
    | DExtern(_, _, _, _, r) | DImportAlias(_, _, _, r)
    | DImportExtern(_, r) | DImportClass(_, r) | DMacro(_, r) | DSyncOnly(_, r) -> r

// ---------------------------------------------------------------------------
// Macro expansion
// ---------------------------------------------------------------------------

/// What a macro expansion hands back.
///
/// `Resolve` runs on the *parsed* result rather than on the form, because the
/// first of the three renaming rules is "if the name is bound in the current
/// scope, use that binding" — and which names an expansion binds is not known
/// until it has been parsed. `AlphaRename.freeNames` answers it exactly, so
/// resolution is a post-pass and the parser needs no scope of its own.
///
/// The `Set<string>` is what to count as already bound. It is empty in
/// expression and body position, where the binders an expansion introduced are
/// inside the expression `freeNames` walks. In declaration position there is no
/// enclosing expression to be inside, so the group's own binders are passed in:
/// see `boundNames`.
type Expansion =
    { Form: SExpr
      Resolve: Set<string> -> Expr -> Expr }

/// Set by `Pipeline` once the macro table is populated.
///
/// A mutable hook rather than a threaded context, for the same reason `Gensym`
/// keeps one counter: `parseExpr` is a pure `SExpr -> Expr` called from several
/// hundred places and from `Pipeline.inlineImplDecl`, which parses
/// already-expanded metadata bodies and must keep working with no macros
/// registered at all.
let mutable expandHook: SExpr -> Expansion option = fun _ -> None

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

let rec parsePattern (s: SExpr) : Pattern =
    let r = getRange s

    // The third renaming rule, applied to a pattern's head.
    //
    // A constructor is never a binder, so the first two rules cannot reach one:
    // nothing in the expansion binds it, and a macro module publishes bindings
    // rather than constructors. Stripping is therefore the whole answer, and it
    // has to happen here — `AlphaRename.freeNames` reports the names a pattern
    // *binds* and never the constructor it matches, so a template's `(Cons a
    // Nil)` would otherwise reach inference as `Cons__37`.
    //
    // A bare lowercase symbol is left alone: that is a binder, and its mark is
    // what makes it uncapturable.
    let s =
        match s with
        | SList(SAtom({ Token = Symbol sym } as head) :: args, lr) when headName sym <> sym ->
            SList(SAtom { head with Token = Symbol(headName sym) } :: args, lr)
        | SAtom({ Token = Symbol sym } as atom) when
            sym.Length > 0 && System.Char.IsUpper sym[0] && headName sym <> sym
            ->
            SAtom { atom with Token = Symbol(headName sym) }
        | _ -> s

    match s with
    | SAtom { Token = Symbol "_" } -> PWildcard r
    // Before the binder case below, which would otherwise read `#t` as a name
    // and match everything. That is what it did: a boolean pattern bound a
    // variable called `#t` and reached the code generator, which spelled it
    // into C# as written and produced a preprocessor directive.
    | SAtom { Token = BoolLit true } -> PBool(true, r)
    | SAtom { Token = BoolLit false } -> PBool(false, r)
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

    // `(or p q ...)` — several patterns in one position, which a `switch`
    // statement gives a label each. Before the constructor case below, which
    // would otherwise read `or` as one.
    | SList(SAtom { Token = Symbol "or" } :: args, _) ->
        match args with
        | [] ->
            failwithf
                $"Invalid or pattern at %s{Lexer.formatPos r}. (or ...) needs alternatives to choose between."
        | [ single ] -> parsePattern single
        | _ -> POr(List.map parsePattern args, r)

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

let parseArrowType (colour: Colour) (items: SExpr list) (r: Range) : FType =
    if items.IsEmpty then failwithf $"Arrow type must have at least a return type at %s{Lexer.formatPos r}"
    let returnTypeExpr = List.last items
    let argItems = List.take (items.Length - 1) items

    let rec parseArrowTypeInner (s: SExpr) : FType =
        let r = getRange s
        match stripTypeMark s with
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

    // The return type is not a parameter either, and reaches
    // `parseArrowTypeInner` — which cannot tell the two apart — so it is
    // checked here, where the split is known.
    let parseReturnType () =
        match stripTypeMark returnTypeExpr with
        | SList(SAtom { Token = Symbol "-?->" } :: _, _) ->
            failwithf
                $"Syntax error at %s{Lexer.formatPos (getRange returnTypeExpr)}: -?-> says that a *parameter* may be given a function of either colour, and this is the return type. A function that hands one back has to have decided which it is building, and saying otherwise needs an effect variable with a name of its own, which does not exist yet."
        | _ -> parseArrowTypeInner returnTypeExpr

    let rec collectArgs mandatory keywords argItems =
        match argItems with
        | [] -> TArrow(List.rev mandatory, List.rev keywords, None, parseReturnType (), colour, r)
        | [SAtom { Token = Keyword "rest" }] ->
            failwithf $"Expected rest element type after #:rest at %s{Lexer.formatPos r}"
        | SAtom { Token = Keyword "rest" } :: restTypeExpr :: [] ->
            TArrow(List.rev mandatory, List.rev keywords, Some (parseArrowTypeInner restTypeExpr), parseReturnType (), colour, r)
        | SList(SAtom { Token = Keyword name } :: [ typeExpr ], _) :: rest ->
            collectArgs mandatory ((name, parseArrowTypeInner typeExpr) :: keywords) rest
        | item :: rest when keywords.IsEmpty ->
            collectArgs (parseArrowTypeInner item :: mandatory) keywords rest
        | _ -> failwithf $"Mandatory types must come before keyword/rest types in arrow type at %s{Lexer.formatPos r}"

    collectArgs [] [] argItems

let rec parseType (s: SExpr) : FType =
    let r = getRange s

    match stripTypeMark s with
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
    // `-?->` says a *parameter* may be either colour, so an arrow that is not a
    // parameter has nothing to say with it. This case is every position that is
    // not one: the type of a definition, a record field, a `let` annotation, an
    // element type. A parameter arrow never reaches here — it is read by
    // `parseArrowTypeInner`, which builds a `TApp` for every applied form.
    | SList(SAtom { Token = Symbol "-?->" } :: _, _) ->
        failwithf
            $"Syntax error at %s{Lexer.formatPos r}: -?-> says that a *parameter* may be given a function of either colour, and this arrow is not a parameter.\n  As the type of a definition it would say nothing: a defun is already colour-polymorphic, and a copy of it is made for each colour actually used. If what you need is two different *bodies* rather than two copies of one — because the two halves call different .NET methods — that is defbjouble.\n  As a record field, a let annotation or a return type it would need an effect variable with a name of its own, which does not exist yet."
    | SList(SAtom { Token = Symbol name } :: typeArgs, _) -> TApp(name, List.map parseType typeArgs, r)
    // `(%m %a)` — a type variable applied to arguments. See `parseArrowTypeInner`.
    | SList(SAtom { Token = QuotedSymbol sym } :: typeArgs, _) ->
        TApp("'" + sym, List.map parseType typeArgs, r)
    | _ -> failwithf $"Invalid type syntax at %s{Lexer.formatPos r}"

let parseUnionCase (s: SExpr) : UnionCase =
    let r = getRange s

    // `#:literal` marks the *case*, and is not one of its types, so it is taken
    // off here rather than by giving `parseType` a keyword case. A keyword is
    // not a type anywhere else, and admitting one there would make
    // `(-> #:literal int)` parse too.
    let takeMarkers (name: string) (items: SExpr list) =
        let markers, types =
            items
            |> List.partition (function
                | SAtom { Token = Keyword _ } -> true
                | _ -> false)

        for marker in markers do
            match marker with
            | SAtom { Token = Keyword "literal" } -> ()
            // Named separately from the unknown markers because it is a thing
            // someone may reasonably expect to work: a record field can be
            // mutable and a case payload cannot. A payload is positional and
            // unnamed, so there would be nothing for a write to name.
            | SAtom { Token = Keyword "mutable" } ->
                failwithf
                    $"#:mutable on the union case %s{name} at %s{Lexer.formatPos r}: a case payload is positional, so there is no field name for a write to use. Give the case a Record that has the mutable field instead."
            | SAtom { Token = Keyword bad } ->
                failwithf
                    $"Unknown marker #:%s{bad} on the union case %s{name} at %s{Lexer.formatPos r}. The only one is #:literal, which names this case as the one a quoted literal is injected into."
            | _ -> ()

        if not markers.IsEmpty && types.IsEmpty then
            failwithf
                $"#:literal on %s{name} at %s{Lexer.formatPos r} marks a case that carries nothing. It says which case a literal is injected into, so it belongs on one with a payload."

        types, not markers.IsEmpty

    match s with
    | SAtom { Token = Symbol name } -> SimpleCase(name, r)
    | SList([ SAtom { Token = Symbol name } ], _) -> SimpleCase(name, r)
    | SList(SAtom { Token = Symbol name } :: tTypes, _) ->
        let types, isLiteral = takeMarkers name tTypes
        DataCase(name, List.map parseType types, isLiteral, r)
    | _ ->
        printfn $"%A{s}"
        failwithf $"Invalid union case at %s{Lexer.formatPos r}"

let parseRecordField (s: SExpr) : RecordField =
    let r = getRange s

    // Markers come after the type, and are taken off before it is parsed for
    // the reason a union case's are: a keyword is not a type anywhere else, and
    // letting `parseType` see one would make `(-> #:mutable int)` parse too.
    let takeMarkers (name: string) (items: SExpr list) =
        let markers, rest =
            items
            |> List.partition (function
                | SAtom { Token = Keyword _ } -> true
                | _ -> false)

        for marker in markers do
            match marker with
            | SAtom { Token = Keyword "mutable" } -> ()
            | SAtom { Token = Keyword bad } ->
                failwithf
                    $"Unknown marker #:%s{bad} on the field '%s{name}' at %s{Lexer.formatPos r}. The only one is #:mutable, which lets the field be written in place by record-set!."
            | _ -> ()

        rest, not markers.IsEmpty

    match s with
    | SList(SAtom { Token = Colon } :: SAtom { Token = Symbol name } :: rest, _) ->
        match takeMarkers name rest with
        | [ tType ], isMutable ->
            { Name = name
              Type = parseType tType
              Mutable = isMutable
              Range = r }
        | _ -> failwithf $"Invalid record field at %s{Lexer.formatPos r}: a field is written (: name type), optionally followed by #:mutable."
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

    // `#:opaque` marks the *declaration* rather than any part of the shape, so
    // it is taken off here — before the shape is looked at — for the reason a
    // record field's `#:mutable` is: a keyword is not a type anywhere else, and
    // every arm below matches an exactly-three-element list.
    let s, isOpaque =
        match s with
        | SList((SAtom { Token = Colon } as colon) :: rest, sr) ->
            let markers, items =
                rest
                |> List.partition (function
                    | SAtom { Token = Keyword _ } -> true
                    | _ -> false)

            for marker in markers do
                match marker with
                | SAtom { Token = Keyword "opaque" } -> ()
                | SAtom { Token = Keyword bad } ->
                    failwithf
                        $"Unknown marker #:%s{bad} on the type definition at %s{Lexer.formatPos r}. The only one is #:opaque, which exports the type's name without its representation."
                | _ -> ()

            SList(colon :: items, sr), not markers.IsEmpty
        | _ -> s, false

    // The third renaming rule, at the shape tag. `Record`, `Struct` and `Union`
    // are dispatched on exactly as a special form's head is, and a template that
    // writes one — which is what a `derive` macro does — arrives with it
    // renamed. Nothing else in a type definition needs it: a type *name* is
    // read through `originalName` and a type inside a field is read by
    // `parseType`, which strips its own.
    let s =
        match s with
        | SList([ (SAtom { Token = Colon } as colon); head; shape ], sr) ->
            SList([ colon; head; stripHeadMark shape ], sr)
        | _ -> s

    match s with
    | SList([ SAtom { Token = Colon }
              head
              SList(SAtom { Token = Symbol(("Record" | "Struct") as kind) } :: fields, _) ],
            _) ->
        let name, typeArgs = parseTypeDefHead head
        let parsedFields = List.map parseRecordField fields

        // A `Struct` is a C# `record struct` — a value type, copied on every
        // assignment and every parameter pass, and one held inside a `List` or
        // a `Map` cannot be addressed at all. A write to a mutable field of one
        // would land on a copy and be lost, silently and unpreventably, so the
        // combination is refused rather than supported badly.
        if kind = "Struct" then
            match parsedFields |> List.tryFind (fun f -> f.Mutable) with
            | Some f ->
                failwithf
                    $"Invalid field '%s{f.Name}' at %s{Lexer.formatPos f.Range}: a Struct may not have a mutable field. A struct is a value type, so it is copied on assignment and a write would land on the copy. Declare '%s{name}' as a Record instead."
            | None -> ()

        { Name = name
          TypeArgs = typeArgs
          Kind = Record(parsedFields, kind = "Struct")
          IsOpaque = isOpaque
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
          IsOpaque = isOpaque
          Range = r }
    // A head with no body. Not a shape source writes: it is what `Exports`
    // publishes an `#:opaque` type as, read back here by the ordinary parser
    // because metadata *is* Bjolang source text.
    | SList([ SAtom { Token = Colon }
              head
              SList(SAtom { Token = Symbol "Opaque" } :: members, _) ],
            _) ->
        let name, typeArgs = parseTypeDefHead head

        let memberNames =
            members
            |> List.map (function
                | SAtom { Token = Symbol m } -> m
                | bad ->
                    failwithf
                        $"Invalid hidden member name in an Opaque type at %s{Lexer.formatPos (getRange bad)}")

        { Name = name
          TypeArgs = typeArgs
          Kind = Opaque memberNames
          IsOpaque = true
          Range = r }
    // Explicit Alias: (: head (Alias aliasType))
    | SList([ SAtom { Token = Colon }
              head
              SList([ SAtom { Token = Symbol "Alias" }; aliasType ], _) ],
            _) ->
        let name, typeArgs = parseTypeDefHead head

        // An alias has no representation to keep back. `resolveTypeAnnotation`
        // expands it wherever it is written, so an opaque one would be a
        // different type inside the module from the one outside.
        if isOpaque then
            failwithf
                $"#:opaque on the alias '%s{name}' at %s{Lexer.formatPos r}: an alias is expanded wherever it is named, so there is no representation to hold back. Declare '%s{name}' as a Record or a Union with one field to make it a type of its own."

        { Name = name
          TypeArgs = typeArgs
          Kind = Alias(parseType aliasType)
          IsOpaque = false
          Range = r }
    // Implicit Alias: (: head aliasType)
    | SList([ SAtom { Token = Colon }; head; aliasType ], _) ->
        let name, typeArgs = parseTypeDefHead head

        if isOpaque then
            failwithf
                $"#:opaque on the alias '%s{name}' at %s{Lexer.formatPos r}: an alias is expanded wherever it is named, so there is no representation to hold back. Declare '%s{name}' as a Record or a Union with one field to make it a type of its own."

        { Name = name
          TypeArgs = typeArgs
          Kind = Alias(parseType aliasType)
          IsOpaque = false
          Range = r }
    | _ -> failwithf $"Invalid type definition at %s{Lexer.formatPos r}"

// ---------------------------------------------------------------------------
// type/derive
// ---------------------------------------------------------------------------
//
// `(type/derive (Eq) (: Point (Record (: x int) (: y int))))` is the type
// declaration and the implementations that follow from its shape.
//
// Syntactic, and it has to be: where a declaration is read there is no registry
// to ask what a type's fields are, only the form declaring them. So this reads
// the same `TypeDef` the `type` form does and writes the implementation out of
// it, in declaration order.
//
// The traits are a list — `(Eq)` today, `(Eq Ord)` later — so that the shape
// does not have to change when there is a second one.

/// Every generated node carries the range of the *field* or *case* it came
/// from, so an unimplementable comparison is reported against the thing in the
/// source that asked for it rather than against the whole declaration.
let private dTrue r = EBool(true, r)
let private dFalse r = EBool(false, r)
let private dAnd r a b = EIf(a, b, dFalse r, r)
let private dEq r a b = EApp(EResolved("=", r), [ a; b ], r)
let private dHash r x = EApp(EResolved("eq-hash", r), [ x ], r)
let private dCombine r a b = EApp(EResolved("hash-combine", r), [ a; b ], r)
let private dInt r (n: int) = EInt(string n, r)

let private dAllOf (r: Range) (items: (Range * Expr) list) : Expr =
    match items with
    | [] -> dTrue r
    | _ ->
        let rec go =
            function
            | [] -> dTrue r
            | [ (_, last) ] -> last
            | (ir, item) :: rest -> dAnd ir item (go rest)

        go items

/// The seed a fold of `hash-combine` starts from, and the tag a union case
/// contributes. Two cases carrying the same payload have to hash differently,
/// which is what the index buys.
let private dHashSeed r = dInt r 17

let private deriveEqForRecord (r: Range) (fields: RecordField list) : Decl list =
    let get (who: string) (f: RecordField) = EGetField(EIdent(who, f.Range), f.Name, f.Range)

    let equals =
        DDefun(
            "=",
            [ MandatoryArg("a", None); MandatoryArg("b", None) ],
            dAllOf r (fields |> List.map (fun f -> f.Range, dEq f.Range (get "a" f) (get "b" f))),
            Ordinary,
            r
        )

    let hash =
        let body =
            fields
            |> List.fold (fun acc f -> dCombine f.Range acc (dHash f.Range (get "v" f))) (dHashSeed r)

        DDefun("eq-hash", [ MandatoryArg("v", None) ], body, Ordinary, r)

    [ equals; hash ]

/// A union: different cases are unequal, and the same case compares its payload
/// positionally.
let private deriveEqForUnion (r: Range) (cases: UnionCase list) : Decl list =
    let parts =
        function
        | SimpleCase(n, cr) -> n, 0, cr
        | DataCase(n, types, _, cr) -> n, types.Length, cr

    let binders (side: string) (arity: int) (cr: Range) =
        List.init arity (fun i -> PIdent($"__d_%s{side}%d{i}", cr))

    let equals =
        let arms =
            cases
            |> List.map (fun c ->
                let name, arity, cr = parts c

                // The inner match is on the *other* value: same case, compare
                // the payloads; any other case, unequal.
                let sameCase =
                    dAllOf
                        cr
                        (List.init arity (fun i ->
                            cr, dEq cr (EIdent($"__d_l%d{i}", cr)) (EIdent($"__d_r%d{i}", cr))))

                let inner =
                    EMatch(
                        EIdent("b", cr),
                        [ PConstruct(name, binders "r" arity cr, cr), None, sameCase
                          PWildcard cr, None, dFalse cr ],
                        cr
                    )

                PConstruct(name, binders "l" arity cr, cr), None, inner)

        DDefun(
            "=",
            [ MandatoryArg("a", None); MandatoryArg("b", None) ],
            EMatch(EIdent("a", r), arms, r),
            Ordinary,
            r
        )

    let hash =
        let arms =
            cases
            |> List.mapi (fun index c ->
                let name, arity, cr = parts c

                let body =
                    List.init arity id
                    |> List.fold
                        (fun acc i -> dCombine cr acc (dHash cr (EIdent($"__d_l%d{i}", cr))))
                        (dInt cr index)

                PConstruct(name, binders "l" arity cr, cr), None, body)

        DDefun("eq-hash", [ MandatoryArg("v", None) ], EMatch(EIdent("v", r), arms, r), Ordinary, r)

    [ equals; hash ]

/// The traits `type/derive` knows how to write, and how.
let private deriveMethods (traitName: string) (td: TypeDef) : Decl list =
    match traitName, td.Kind with
    | "Eq", Record(fields, _) -> deriveEqForRecord td.Range fields
    | "Eq", Union cases -> deriveEqForUnion td.Range cases
    | "Eq", Alias _ ->
        failwithf
            $"Cannot derive at %s{Lexer.formatPos td.Range}: '%s{td.Name}' is a type alias, which is a second spelling of a type rather than a type of its own. Derive for the type it names."
    | _ ->
        failwithf
            $"Cannot derive '%s{traitName}' at %s{Lexer.formatPos td.Range}: the traits that can be derived are Eq."

/// The implementation `traitName` derives for `td`.
///
/// A type with parameters derives a *conditional* implementation — every
/// parameter has to satisfy the same trait, because the fields hold values of
/// those types and comparing one is what the body does.
let private deriveImpl (traitName: string) (td: TypeDef) : Decl =
    let r = td.Range

    let target =
        if td.TypeArgs.IsEmpty then
            TName(td.Name, r)
        else
            TApp(td.Name, td.TypeArgs |> List.map (fun a -> TName("'" + a, r)), r)

    let constraints = td.TypeArgs |> List.map (fun a -> traitName, "'" + a)

    DImpl(traitName, target, [], constraints, deriveMethods traitName td, r)

/// `(type/derive (Eq) typedef ...)`, as the declarations it stands for.
let private parseDerive (isRec: bool) (traits: SExpr list) (typeDefForms: SExpr list) (r: Range) : Decl list =
    let traitNames =
        traits
        |> List.map (function
            | SAtom { Token = Symbol name } -> name
            | bad ->
                failwithf
                    $"Syntax error in type/derive at %s{Lexer.formatPos (getRange bad)}: the first form is the list of traits to derive, as in (Eq).")

    if traitNames.IsEmpty then
        failwithf
            $"Syntax error in type/derive at %s{Lexer.formatPos r}: it derives nothing. Write the traits to derive, as in (Eq), or use `type`."

    if typeDefForms.IsEmpty then
        failwithf $"Syntax error in type/derive at %s{Lexer.formatPos r}: it declares no type."

    let typeDefs = List.map parseTypeDef typeDefForms

    let decl = if isRec then DTypeRec(typeDefs, r) else DType(typeDefs, r)

    decl :: [ for t in traitNames do for td in typeDefs -> deriveImpl t td ]

/// Desugars a syntax template `#'(if ,c ,t ,f)` into the `Syntax` value it
/// describes.
///
/// This is not `desugarQuotedList` with a different payload. `'(...)` builds an
/// `EList`, which is homogeneous — every element has to share a type — and a
/// template is heterogeneous by nature: a symbol, then whatever the unquotes
/// splice in. Everything here is a `Syntax`, so the result types as one thing.
///
/// The two spellings are kept apart rather than switched on context. Deciding
/// by "is this inside a `def/macro`" would mean a helper function in the same
/// module got list semantics for the same `'(...)` its caller got template
/// semantics for, which is not a property a reader can see locally.
///
/// A `Symbol` becomes `SSym`, an *identifier*, which hygiene renames. A
/// `QuotedSymbol` — `'foo` written inside the template — becomes `SDatum`,
/// which it never renames: that is a value, not a reference to a binding. This
/// is also why `''foo` is not needed, and it is just as well, since it does not
/// read.
let desugarSyntaxQuote (parseExprFn: SExpr -> Expr) (template: SExpr) (r: Range) : Expr =
    let call name args range = EApp(EResolved(name, range), args, range)

    let rec go (s: SExpr) : Expr =
        let ir = getRange s

        match s with
        | SAtom { Token = NumberLit n } -> call "SInt" [ EString(n, ir) ] ir
        | SAtom { Token = StringLit str } -> call "SStr" [ EString(str, ir) ] ir
        | SAtom { Token = CharLit c } -> call "SChar" [ EChar(c, ir) ] ir
        | SAtom { Token = Keyword k } -> call "SKey" [ EKeyword(k, ir) ] ir
        | SAtom { Token = QuotedSymbol sym } -> call "SDatum" [ EQuotedSymbol(sym, ir) ] ir
        // A boolean crosses into a template as the symbol it is spelled with.
        // `Syntax` has no boolean node, and `Macro.neverRenamed` already knows
        // these two names, so the round trip is what it always was — only the
        // token on either side of it changed.
        | SAtom { Token = BoolLit b } -> call "SSym" [ EQuotedSymbol((if b then "#t" else "#f"), ir) ] ir
        | SAtom { Token = Symbol sym } -> call "SSym" [ EQuotedSymbol(sym, ir) ] ir
        | SAtom { Token = Comma } ->
            failwithf $"Unexpected , at %s{Lexer.formatPos ir}: nothing to unquote."
        | SAtom { Token = CommaAt } ->
            failwithf $"Unexpected ,@ at %s{Lexer.formatPos ir}: nothing to splice."
        // Punctuation that survives reading, and that a template has to be able
        // to write: `(: name type)` is a signature, and a macro that expands to
        // a definition has to be able to declare its type beside it. `Syntax`
        // has carried these as `SPunct` all along — `Macro.ofSExpr` hands one to
        // a transformer whenever the *input* contains it — and this was the one
        // direction that could not spell them.
        //
        // `,` and `,@` are not here: inside a template those are the unquote
        // markers, handled above. A macro that needs to *write* one writes
        // `(SPunct ",")`.
        | SAtom { Token = Colon } -> call "SPunct" [ EString(":", ir) ] ir
        | SAtom { Token = Dot } -> call "SPunct" [ EString(".", ir) ] ir
        | SAtom { Token = Spread } -> call "SPunct" [ EString("...", ir) ] ir
        | SAtom _ -> failwithf $"Unsupported item in a syntax template at %s{Lexer.formatPos ir}"
        | SList(items, lr) -> call "SList" [ items0 items lr ] lr

    /// The children of one form, as a `(List Syntax)`.
    ///
    /// `,@` is why this is a fold rather than a map: a splice contributes a
    /// whole list where its siblings contribute one element each, so the result
    /// is built by appending runs rather than by consing uniformly.
    and items0 (items: SExpr list) (lr: Range) : Expr =
        let rec go' remaining (pending: Expr list) : Expr =
            /// Everything gathered since the last splice, as a list literal.
            let flush (tail: Expr) =
                if List.isEmpty pending then tail
                else call "syntax-splice" [ EList(List.rev pending, lr); tail ] lr

            match remaining with
            | [] ->
                if List.isEmpty pending then EList([], lr) else EList(List.rev pending, lr)

            | SAtom { Token = Comma } :: inner :: rest -> go' rest (parseExprFn inner :: pending)
            | [ SAtom { Token = Comma } ] ->
                failwithf $"Unexpected , at end of a syntax template at %s{Lexer.formatPos lr}"

            | SAtom { Token = CommaAt } :: inner :: rest ->
                flush (call "syntax-splice" [ parseExprFn inner; go' rest [] ] lr)
            | [ SAtom { Token = CommaAt } ] ->
                failwithf $"Unexpected ,@ at end of a syntax template at %s{Lexer.formatPos lr}"

            | item :: rest -> go' rest (go item :: pending)

        go' items []

    go template

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
        // Ahead of the symbol case: `'(#t #f)` is a list of booleans, the way
        // `'(1 2)` is a list of ints.
        | SAtom { Token = BoolLit true } -> EBool(true, ir)
        | SAtom { Token = BoolLit false } -> EBool(false, ir)
        // A symbol in a quoted list is a literal Symbol value, not a variable
        // reference — write ,(expr) to splice the value of a variable.
        | SAtom { Token = Symbol sym } -> EQuotedSymbol(sym, ir)
        | SAtom { Token = QuotedSymbol sym } -> EQuotedSymbol(sym, ir)
        // Dotted pair in a quoted list: '(a . b) → (Tuple a b)
        | SList(SAtom { Token = Symbol "Tuple" } :: tupleItems, _) ->
            ETuple(List.map quoteItem tupleItems, ir)
        // `[a b]`, which the reader has already rewritten to
        // `(vec-literal a b)`. Without this the head is quoted like any other
        // symbol and `'(ls ["-l"])` yields a list beginning with the symbol
        // `vec-literal` — a form no program wrote.
        | SList(SAtom { Token = Symbol "vec-literal" } :: vecItems, vr) ->
            EVec(List.map quoteItem vecItems, vr)
        // `{...}`, likewise rewritten by the reader. A comprehension is a loop,
        // not data, so there is nothing to quote it as. The reserved head wins
        // over the symbol of the same name, which is the price of catching it.
        | SList(SAtom { Token = Symbol "comprehension" } :: _, cr) ->
            failwithf
                $"A comprehension inside a quoted list at %s{Lexer.formatPos cr}. Quoting builds data, and a comprehension is a loop that produces a value: write ,{{...}} to splice what it produces."
        // Any other list is data as well: '('(a b) '(c d)) nests.
        //
        // The two remaining reader rewrites arrive here — `#(...)` as a `fun`
        // form, `#map(...)` as a `list->map` call — and neither can be told
        // apart from the same list written by hand, so both are quoted as the
        // lists they have become. Write `,#(...)` to splice the function.
        | SList(inner, lr) -> collectItems inner lr
        | SAtom { Token = CommaAt } ->
            failwithf
                $"Splicing with ,@ inside '(...) at %s{Lexer.formatPos ir}, which is not supported. A quoted list is built element by element from what is written in it, so a spliced list — whose length is only known when the program runs — has nowhere to go. Write ,x to place one element. ,@ does work inside a #' template, which builds a Syntax value rather than a list."
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
    | EBool(_, r)
    | EResolved(_, r)
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
    | ERecordSet(_, _, r)
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

/// Every name a pattern binds.
let rec patternBinders (pat: Pattern) : string list =
    match pat with
    | PWildcard _
    | PInt _
    | PString _
    | PChar _
    | PBool _
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
    | POr(alts, _) -> alts |> List.collect patternBinders

/// One walk over an untyped expression, calling `reference name range guarded`
/// at every name it mentions but does not bind.
///
/// `guarded` is true where the reference sits inside something deferred — a
/// lambda or `seq` body, a `bjo` or task-event operand, the value of a local
/// `defun`. Nothing in there runs until the enclosing form is applied or
/// consumed, so such a reference may legally point at a binding that is not
/// established yet. That is what tells a mutually recursive group apart from a
/// use-before-definition; callers that only want the names ignore it.
///
/// The range is the occurrence's own, which is what lets a caller that refuses
/// a name point at the one it means rather than at the enclosing form.
let freeNamesWith (reference: string -> Range -> bool -> unit) (guarded: bool) (bound: Set<string>) (expr: Expr) : unit =
    let rec go (guarded: bool) (bound: Set<string>) (e: Expr) =
        let sub = go guarded bound
        let refer n r = if not (Set.contains n bound) then reference n r guarded

        match e with
        | EInt _
        | EString _
        | EChar _
        | EBool _
        | EResolved _
        | EQuotedSymbol _
        | EKeyword _ -> ()
        | EIdent(n, r) -> refer n r
        | ETuple(items, _)
        | EList(items, _)
        | EVec(items, _) -> List.iter sub items
        | EApp(target, args, _) ->
            sub target
            List.iter sub args
        | ECast(_, v, _) -> sub v

        | ELet(n, isFun, args, _, value, body, _) ->
            go (guarded || isFun) (if isFun then Set.union bound (Set.ofList (allArgNames args)) else bound) value
            go guarded (Set.add n bound) body

        | ELetMono(n, value, body, _) ->
            sub value
            go guarded (Set.add n bound) body

        | ELetRec(bindings, body, _) ->
            let inner = bindings |> List.fold (fun acc (n, _, _, _, _) -> Set.add n acc) bound

            for (_, isFun, args, _, value) in bindings do
                go (guarded || isFun) (if isFun then Set.union inner (Set.ofList (allArgNames args)) else inner) value

            go guarded inner body

        | ELetTuple(names, value, body, _) ->
            sub value
            go guarded (Set.union bound (Set.ofList names)) body

        | ELetMutable(n, _, value, body, _) ->
            sub value
            go guarded (Set.add n bound) body

        | ESet(n, value, r) ->
            refer n r
            sub value

        | EIf(c, t, f, _) ->
            sub c
            sub t
            sub f
        | EWhen(c, b, _, _) ->
            sub c
            sub b
        | EFun(args, body, _, _) -> go true (Set.union bound (Set.ofList args)) body
        | ERecordUpdate(n, fields, r)
        | ERecordSet(n, fields, r) ->
            refer n r
            fields |> List.iter (snd >> sub)
        | EGetField(target, _, _) -> sub target

        | EMatch(target, clauses, _) ->
            sub target

            for (pat, guard, body) in clauses do
                let inner = Set.union bound (Set.ofList (patternBinders pat))
                Option.iter (go guarded inner) guard
                go guarded inner body

        | ETryFinally(body, cleanup, _) ->
            sub body
            sub cleanup
        | ETryCatch(body, _, _) -> sub body
        // A `seq` body is deferred exactly as a lambda body is: nothing in it
        // runs until the sequence is consumed.
        | ESeq(body, _) -> go true bound body
        // The operands are evaluated where the form is written; only the call
        // is deferred. Guarded all the same, because the call is.
        | EBjo(body, _)
        | ETaskEvent(body, _) -> go true bound body
        | EYield(v, _)
        | EYieldFrom(v, _) -> sub v

    go guarded bound expr

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
    | EBool _
    | EResolved _
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
    | ERecordUpdate(_, fields, _)
    | ERecordSet(_, fields, _) -> fields |> List.map snd
    | ETryFinally(b, c, _) -> [ b; c ]
    | ETryCatch(b, _, _) -> [ b ]
    | EMatch(target, clauses, _) ->
        target
        :: (clauses |> List.collect (fun (_, guard, body) -> (Option.toList guard) @ [ body ]))

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

let rec private renamePattern (subst: Map<string, string>) (pat: Pattern) : Pattern =
    match pat with
    | PIdent(n, r) -> PIdent((Map.tryFind n subst |> Option.defaultValue n), r)
    | PList(items, tailOpt, r) ->
        PList(List.map (renamePattern subst) items, Option.map (renamePattern subst) tailOpt, r)
    | PVec(items, tailOpt, r) ->
        PVec(List.map (renamePattern subst) items, Option.map (renamePattern subst) tailOpt, r)
    | PTuple(items, r) -> PTuple(List.map (renamePattern subst) items, r)
    | PConstruct(n, args, r) -> PConstruct(n, List.map (renamePattern subst) args, r)
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

/// Every name `expr` references without binding, given `bound` already in scope.
let freeNames (bound: Set<string>) (expr: Expr) : Set<string> =
    let mutable acc = Set.empty
    freeNamesWith (fun n _ _ -> acc <- Set.add n acc) false bound expr
    acc

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

/// One clause of a `(loop ...)`, still unparsed.
///
/// The clause list is flat and there is no body position: every clause carries
/// its own condition, and iteration order is clause order.
///
/// Which is also why `:for`, `:with` and `:let` bind **sequentially** and stay
/// that way now that `let`'s bindings are simultaneous. A clause list is an
/// explicitly ordered thing, and an inner level's sequence is normally written
/// in terms of the outer level's variable — `(:for row rows) (:for cell row)` —
/// so a simultaneous reading would forbid the ordinary nested loop rather than
/// merely change what it means.
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
///
/// `marked` says the head carried a macro's rename, which the operator table
/// stripped in order to recognise it. Every operator here is a trait method —
/// `=` is `Eq`'s, `<` is `Ord`'s, `+` is `Num`'s — so a template that wrote one
/// meant the method, and the reference resolves where it was written rather than
/// where the expansion lands. `EResolved` is that spelling; without it a module
/// that binds `=` for its own purposes silently redefines the arithmetic of
/// every macro it calls, `type/derive` included. The stripping is why this
/// cannot be left to `Macro.resolveIntroduced` like an ordinary call head: by
/// the time it runs, the mark is gone.
let private desugarNaryOp (marked: bool) (op: string) (args: Expr list) (r: Range) : Expr =
    let opRef = if marked then EResolved(op, r) else EIdent(op, r)
    let binary a b = EApp(opRef, [ a; b ], r)

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
            | "-" -> EApp(EResolved("negate", r), [ single ], r)
            | "/" -> EApp(EResolved("recip", r), [ single ], r)
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
                | [] -> EBool(true, r)
                | [ last ] -> last
                | current :: rest -> EIf(current, buildAnd rest, EBool(false, r), r)

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
        | SAtom { Token = Symbol sym } -> Some sym
        | _ -> None

    match s with
    | SAtom { Token = NumberLit n } -> EInt(n, r)
    | SAtom { Token = StringLit str } -> EString(str, r)
    | SAtom { Token = CharLit c } -> EChar(c, r)
    // Ahead of `Ident` below, which used to rewrite these two to the names
    // `true` and `false` and hand them to the environment to resolve.
    | SAtom { Token = BoolLit true } -> EBool(true, r)
    | SAtom { Token = BoolLit false } -> EBool(false, r)
    | SAtom { Token = ResolvedSymbol name } -> EResolved(name, r)
    | SAtom { Token = QuotedSymbol sym } -> EQuotedSymbol(sym, r)
    | SAtom { Token = Keyword sym } -> EKeyword(sym, r)

    // An operator used as a value, which is the only position this case sees:
    // the head of an application is built by the `SList` branch below and never
    // arrives here. So no analysis is needed to tell the two apart, and none of
    // this depends on types.
    | Ident sym when Map.containsKey (headName sym) operatorArity ->
        let op = headName sym
        let ps = List.init operatorArity[op] (fun _ -> Gensym.fresh "op")
        // The call inside the lambda is in call position like any other, so a
        // marked operator resolves where the template wrote it. See
        // `desugarNaryOp`.
        let opRef = if op <> sym then EResolved(op, r) else EIdent(op, r)
        EFun(ps, EApp(opRef, ps |> List.map (fun p -> EIdent(p, r)), r), Ordinary, r)

    | Ident sym -> EIdent(sym, r)

    | SList(head :: args, listRange) ->
        match head with
        | Ident sym ->
            // Dispatch sees through a macro's rename; the identifier does not.
            // `sym` is what an application is built from, so a template's call
            // to a binding of its own module keeps the mark that resolves it.
            match headName sym with
            | "cast" ->
                match args with
                | [ typeSExpr; valSExpr ] ->
                    ECast(parseType typeSExpr, parseExpr valSExpr, r)
                | _ -> failwithf $"Invalid cast syntax at %s{Lexer.formatPos r}. Expected: (cast <type> <expr>)"
            // `(begin ...)` where a *value* is wanted, which `parseBody` did
            // not consume because it is not in body position: a nested body,
            // opening a scope of its own.
            //
            // Reached only there. In body position `parseItems` takes the form
            // first and splices it, which is what the two readings are: spliced
            // among forms, a nested body among values. Scheme's, and the one
            // that makes `(+ 1 (begin (log!) 2))` mean anything at all — there
            // is otherwise no sequencing expression, `seq` being a lazy
            // generator rather than a block.
            //
            // `(begin)` here is `unit`, as it is in a body, since `parseBody`
            // of nothing is `unit`.
            | "begin" -> parseBody args listRange

            // `(let ((x a) (y b)) body)` — R7RS, so the bindings are
            // *simultaneous*: every init is evaluated in the enclosing scope,
            // and none of the group's names is in scope for any of them.
            // `(let ((a b) (b a)) ...)` swaps.
            //
            // The AST has no node for a group, so the group is built out of
            // nested single-binding nodes, which are sequential — and
            // `simultaneous` is what makes that faithful, by renaming apart the
            // binders a later init would otherwise see. Its docstring has the
            // reasoning; the two things worth knowing here are that a binder
            // nothing shadows is left with the name the author gave it, and
            // that the substitution goes to the body and never to an init.
            //
            // The sequential form is `let*`, a prelude macro over nested
            // single-binding `let`s. A single binding means the same thing
            // under both readings, which is what lets the macro be that simple.
            //
            // Left to right is kept, though R7RS leaves the order of the inits
            // unspecified: an init may have effects, the emitted C# has one
            // order regardless, and an order nobody can predict buys a
            // reordering nobody performs.
            | "let" ->
                match args with
                | SList(bindings, _) :: bodyExprs ->
                    let body = parseBody bodyExprs listRange

                    // Each binding as (the names it binds, its init, its own range).
                    // A destructuring binding contributes several names, and each
                    // of them shadows on its own.
                    let parsedBindings =
                        bindings
                        |> List.map (fun bind ->
                            match bind with
                            | SList([ Ident k; v ], _) -> [ k ], parseExpr v, getRange bind, false
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
                                tupleNames, parseExpr v, bindRange, true
                            // Named rather than left to the generic message: a
                            // boolean is a literal, so it is not a symbol and
                            // does not match the binder shapes above — which is
                            // the whole point, but says nothing on its own.
                            | SList([ SAtom { Token = BoolLit b }; _ ], bindRange) ->
                                let spelling = if b then "#t" else "#f"

                                failwithf
                                    $"Cannot bind %s{spelling} at %s{Lexer.formatPos bindRange}: it is a boolean literal, not a name."
                            | _ -> failwith "Invalid let binding")

                    // A repeated name is meaningful under `let*` — the second
                    // binding shadows the first — and means nothing at all here,
                    // since neither binding is in scope for the other's init.
                    // Refused rather than given an arbitrary winner.
                    //
                    // `_` is exempt: it is the binder a body uses for a value
                    // nothing reads, and a body of several statements is several
                    // of them.
                    parsedBindings
                    |> List.collect (fun (names, _, bindRange, _) -> names |> List.map (fun n -> n, bindRange))
                    |> List.filter (fun (n, _) -> n <> "_")
                    |> List.groupBy fst
                    |> List.iter (fun (n, occurrences) ->
                        if occurrences.Length > 1 then
                            let (_, secondRange) = occurrences[1]

                            failwithf
                                $"'%s{n}' is bound twice in the same let at %s{Lexer.formatPos secondRange}. A let binds simultaneously, so neither binding is in scope for the other's value and there is nothing for the second to shadow. Write (let* ...) if the second is meant to see the first, or give one of them another name.")

                    let renamedNames, bodySubst =
                        simultaneous (parsedBindings |> List.map (fun (names, init, _, _) -> names, init))

                    List.foldBack
                        (fun (names, (_, init, bindRange, isTuple)) acc ->
                            if isTuple then
                                ELetTuple(names, init, acc, bindRange)
                            else
                                ELet(List.head names, false, [], None, init, acc, bindRange))
                        (List.zip renamedNames parsedBindings)
                        (renameFree bodySubst body)
                | Ident name :: SList(bindings, _) :: bodyExprs ->
                    // Named let. Already simultaneous, and unchanged by any of
                    // the above: its inits are the arguments of an `EApp`, so
                    // they are evaluated in the enclosing scope by construction
                    // and the loop's parameters cannot be in scope for them.
                    // This form is where the language was right about `let` all
                    // along and the unnamed one was not.
                    let parsedBindings =
                        bindings
                        |> List.map (function
                            | SList([ Ident k; v ], _) -> (k, parseExpr v)
                            | _ -> failwith "Invalid named let binding")

                    let argNames = parsedBindings |> List.map fst
                    let argVals = parsedBindings |> List.map snd
                    let body = parseBody bodyExprs listRange
                    let funcBinding = (name, true, argNames |> List.map (fun n -> MandatoryArg(n, None)), None, body)
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
                        EResolved("spawn-evt/start", listRange),
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
                    | [] -> EBool(true, listRange)
                    | [last] -> parseExpr last
                    | current :: rest ->
                        EIf(parseExpr current, buildAnd rest, EBool(false, listRange), listRange)
                buildAnd args

            | "or" ->
                let rec buildOr items =
                    match items with
                    | [] -> EBool(false, listRange)
                    | [last] -> parseExpr last
                    | current :: rest ->
                        EIf(parseExpr current, EBool(true, listRange), buildOr rest, listRange)
                buildOr args

            | "not" ->
                match args with
                | [arg] -> EIf(parseExpr arg, EBool(false, listRange), EBool(true, listRange), listRange)
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

            // `(case key ((datum ...) body ...) ... (else body ...))`.
            //
            // Desugared to `match` over or-patterns, which is what puts several
            // labels on one `switch` section.
            //
            // Read here rather than expanded by a macro because the datum rules
            // have to be enforced where the source is. A bare name in a datum
            // list is a *binder* to `match`, so `(case c ((a) ...))` would
            // match everything and bind `a` — silently, and only in the clause
            // the author thought was about a symbol.
            | "case" ->
                match args with
                | keyExpr :: (_ :: _ as clauses) ->
                    let datumPattern (d: SExpr) : Pattern =
                        let dr = getRange d

                        match d with
                        | SAtom { Token = NumberLit n } -> PInt(n, dr)
                        | SAtom { Token = StringLit str } -> PString(str, dr)
                        | SAtom { Token = CharLit c } -> PChar(c, dr)
                        | SAtom { Token = BoolLit b } -> PBool(b, dr)
                        | SAtom { Token = Keyword k } -> PKeyword(k, dr)
                        | SAtom { Token = QuotedSymbol sym } -> PQuotedSymbol(sym, dr)
                        | SAtom { Token = Symbol sym } ->
                            failwithf
                                $"Invalid case datum at %s{Lexer.formatPos dr}: '%s{sym}' is a name, and the data of a clause are literals. Write '%s{sym} for the symbol of that name, or use match to bind."
                        | _ ->
                            failwithf
                                $"Invalid case datum at %s{Lexer.formatPos dr}: the data of a clause are literals — a number, string, character, boolean, keyword or quoted symbol."

                    // A repeat is refused here because C# refuses it too, and a
                    // duplicate `case` label reported against generated code is
                    // a compiler error about a file nobody wrote. The second
                    // one is unreachable either way.
                    let seen = System.Collections.Generic.Dictionary<string, Range>()

                    let noteDatum (p: Pattern) =
                        let key, shown, dr =
                            match p with
                            // Normalised, so that `01` and `1` are the one
                            // label they will be emitted as.
                            | PInt(v, dr) ->
                                match System.Int64.TryParse v with
                                | true, n -> $"int:%d{n}", v, dr
                                | _ -> "num:" + v, v, dr
                            | PString(v, dr) -> "str:" + v, $"\"%s{v}\"", dr
                            | PChar(c, dr) -> $"char:%d{c}", $"#\\x%X{c}", dr
                            | PBool(b, dr) -> $"bool:%b{b}", (if b then "#t" else "#f"), dr
                            | PKeyword(k, dr) -> "kw:" + k, "#:" + k, dr
                            | PQuotedSymbol(s, dr) -> "sym:" + s, "'" + s, dr
                            | _ -> "", "", r

                        match seen.TryGetValue key with
                        | true, first ->
                            failwithf
                                $"Duplicate case datum at %s{Lexer.formatPos dr}: %s{shown} is already covered by the clause at %s{Lexer.formatPos first}, so this one can never run."
                        | _ -> seen[key] <- dr

                    let lastIndex = List.length clauses - 1

                    let parsedClauses =
                        clauses
                        |> List.mapi (fun i clause ->
                            let cr = getRange clause

                            let body bodyExprs =
                                match bodyExprs with
                                | [] ->
                                    failwithf
                                        $"Invalid case clause at %s{Lexer.formatPos cr}: this clause has nothing to do."
                                | _ -> parseBody bodyExprs cr

                            match clause with
                            | SList(_ :: SAtom { Token = Symbol "=>" } :: _, _) ->
                                failwithf
                                    $"Invalid case clause at %s{Lexer.formatPos cr}: (=> proc) is not a Bjolang clause. Write the call out, naming the key: (case k ((1 2) (proc k)) ...)."

                            | SList(SAtom { Token = Symbol "else" } :: bodyExprs, _) ->
                                if i <> lastIndex then
                                    failwithf
                                        $"Invalid case at %s{Lexer.formatPos cr}: (else ...) answers whatever the clauses before it did not, so nothing may follow it."

                                (PWildcard cr, None, body bodyExprs)

                            | SList(SList(datums, _) :: bodyExprs, _) ->
                                if List.isEmpty datums then
                                    failwithf
                                        $"Invalid case clause at %s{Lexer.formatPos cr}: this clause lists no data, so nothing reaches it. Remove it, or write (else ...) if it was meant to catch the rest."

                                let pats = datums |> List.map datumPattern
                                List.iter noteDatum pats

                                let pat =
                                    match pats with
                                    | [ single ] -> single
                                    | many -> POr(many, cr)

                                (pat, None, body bodyExprs)

                            // One datum, without the list around it:
                            // `('ms body ...)`. A clause groups data, and a
                            // group of one needs no bracket to say so.
                            | SList((SAtom _ as datum) :: bodyExprs, _) ->
                                let pat = datumPattern datum
                                noteDatum pat
                                (pat, None, body bodyExprs)

                            | _ ->
                                failwithf
                                    $"Invalid case clause at %s{Lexer.formatPos cr}: a clause is (datum body ...) or ((datum ...) body ...) for several, or (else body ...) for the last one.")

                    // Bools cannot have an else clause. C# rejects it.
                    let hasElse =
                        match List.tryLast clauses with
                        | Some(SList(SAtom { Token = Symbol "else" } :: _, _)) -> true
                        | _ -> false

                    let exhausted = seen.ContainsKey "bool:true" && seen.ContainsKey "bool:false"

                    if hasElse && exhausted then
                        failwithf
                            $"Invalid case at %s{Lexer.formatPos r}: the clauses already list both #t and #f, which is the whole of bool, so this (else ...) can never run. Remove it."

                    if not hasElse && not exhausted then
                        failwithf
                            $"Invalid case at %s{Lexer.formatPos r}: this case has no (else ...), and its data are literals — so a key that is none of them reaches no arm at all, and the program fails at runtime naming neither the key nor this form. Add (else ...) for the rest. Only bool can be covered by listing values instead, since #t and #f are the whole of it."

                    EMatch(parseExpr keyExpr, parsedClauses, r)
                | [ _ ] ->
                    failwithf
                        $"Invalid case at %s{Lexer.formatPos r}: there are no clauses, so there is nothing for the key to be."
                | _ ->
                    failwithf
                        $"Invalid case at %s{Lexer.formatPos r}: case takes a key and then its clauses: (case key ((datum ...) body ...) ... (else body ...))."

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

            // A write in place, to `#:mutable` fields. Deliberately *not* given
            // a `struct-set!` synonym the way the pure forms are: a Struct
            // cannot have a mutable field, so the spelling is refused below
            // with the reason rather than left to fail as an unbound name.
            | "record-set!" ->
                match args with
                | Ident baseRec :: (_ :: _ as fields) ->
                    let parsedFields =
                        fields
                        |> List.map (function
                            | SList([ Ident k; v ], _) -> (k, parseExpr v)
                            | bad ->
                                failwithf
                                    $"Invalid %s{sym} field at %s{Lexer.formatPos (getRange bad)}: expected (field-name value)")

                    ERecordSet(baseRec, parsedFields, r)
                | [ Ident _ ] ->
                    failwithf
                        $"Invalid %s{sym} at %s{Lexer.formatPos r}: it has to write at least one field."
                | _ ->
                    failwithf
                        $"Invalid %s{sym} syntax at %s{Lexer.formatPos r}: expected (%s{sym} target (field value) ...), where the target is the name of a record. A computed target is not supported — bind it first."

            | "struct-set!" ->
                failwithf
                    $"Invalid struct-set! at %s{Lexer.formatPos r}: a Struct may not have a mutable field, so there is nothing for it to write. Use record-set! on a Record, or struct-set for a copy."

            | "record-ref" | "struct-ref" ->
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

                        // `headName` for the reason `parsePattern` uses it: an
                        // exception type is dispatched on, never bound, so the
                        // first two renaming rules cannot reach one and
                        // stripping is the whole answer. It has to happen here
                        // because these names leave as a `string list` that
                        // `AlphaRename` never walks — without it a template
                        // writing `#:catch (System.IO.IOException)` reaches
                        // inference as `System.IO.IOException__37`.
                        let parsed =
                            names
                            |> List.map (function
                                | SAtom { Token = Symbol n } -> headName n
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
            //
            // Which is also why the bindings stay **sequential** where `let`'s
            // and `parameterize`'s became simultaneous. Simultaneous would mean
            // every constructor runs before any disposal is registered, so a
            // second one that throws would leak the first resource — the exact
            // failure the per-binding `try/finally` above exists to prevent.
            // And `(with-open ((r (open-in p)) (w (wrap r))) ...)` is how one is
            // normally written: a later resource built from an earlier one needs
            // the earlier one's name in scope. Resource safety and the ordinary
            // usage agree here, and both outrank uniformity.
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
            // The bindings are *simultaneous*, as R7RS says and as `let`'s are:
            // every parameter and value expression is evaluated against the
            // environment the form was written in, and only then is any of them
            // installed. So a later value expression that reads an earlier
            // parameter of the same form reads what it was *outside*.
            //
            // The mechanism is not `let`'s. There is no binder to rename apart
            // here — a binding's left-hand side is an arbitrary expression, not
            // a name — and the capture is not lexical but dynamic: an earlier
            // parameter is already installed in the environment while a later
            // value expression runs. What fixes that is evaluating everything
            // into temporaries first, in one flat chain outside the pushes, and
            // pushing the temporaries.
            //
            // `parameterize*` — a prelude macro over nested single-binding
            // `parameterize`s — is the sequential form, for the rare case that
            // wants one binding to be visible while the next value is computed.
            //
            // The unwinding is unchanged, and still nests: each binding gets its
            // own `try/finally`, so a `finally` runs only for a push that
            // happened, and the environment comes back in the reverse of the
            // order it went out.
            //
            // Note again that the binder is an arbitrary *expression*: a
            // parameter is a value, so `(parameterize (((config-port c) w)) ...)`
            // is as legitimate as naming one directly — which is the other
            // reason it has to go into a temporary, since computing it twice
            // could push one parameter and restore another.
            | "parameterize" ->
                match args with
                | SList(bindings, _) :: bodyForms ->
                    if bodyForms.IsEmpty then
                        failwithf $"Invalid parameterize at %s{Lexer.formatPos r}: it has no body."

                    let body = parseBody bodyForms listRange

                    // (the parameter's temporary, the value's temporary, the two
                    // expressions they hold, the binding's own range)
                    let parsedBindings =
                        bindings
                        |> List.map (fun binding ->
                            match binding with
                            | SList([ param; value ], bindRange) ->
                                Gensym.fresh "dynparam", Gensym.fresh "dynvalue", parseExpr param, parseExpr value, bindRange
                            | bad ->
                                failwithf
                                    $"Invalid parameterize binding at %s{Lexer.formatPos (getRange bad)}: expected (parameter expression).")

                    let installed =
                        List.foldBack
                            (fun (paramTemp, valueTemp, _, _, bindRange) acc ->
                                let saved = Gensym.fresh "dynsaved"

                                let push =
                                    EApp(
                                        EResolved("parameter-push!", bindRange),
                                        [ EIdent(paramTemp, bindRange); EIdent(valueTemp, bindRange) ],
                                        bindRange
                                    )

                                let restore =
                                    EApp(EResolved("dyn-restore!", bindRange), [ EIdent(saved, bindRange) ], bindRange)

                                ELet(saved, false, [], None, push, ETryFinally(acc, restore, bindRange), bindRange))
                            parsedBindings
                            body

                    // Source order, left to right, and the parameter before its
                    // own value — the same order the expressions were written
                    // in, which is the order they used to run in too. R7RS
                    // leaves it unspecified; an effect in a value expression is
                    // not a reason to make it unpredictable.
                    List.foldBack
                        (fun (paramTemp, valueTemp, paramExpr, valueExpr, bindRange) acc ->
                            ELet(
                                paramTemp,
                                false,
                                [],
                                None,
                                paramExpr,
                                ELet(valueTemp, false, [], None, valueExpr, acc, bindRange),
                                bindRange
                            ))
                        parsedBindings
                        installed
                | _ ->
                    failwithf
                        $"Invalid parameterize at %s{Lexer.formatPos r}: expected (parameterize ((parameter expression) ...) body...)"

            | "Tuple" -> ETuple(processArgs args, listRange)

            // Quoted list literal: '(1 2 3) → Cons chain
            | "quoted-list" -> desugarQuotedList parseExpr args listRange

            // The head the reader puts on a `#'` form, always with exactly one
            // argument: `#'` is a prefix on the form after it.
            | "syntax-quote" ->
                match args with
                | [ template ] -> desugarSyntaxQuote parseExpr template listRange
                | _ ->
                    failwithf
                        $"Invalid syntax-quote at %s{Lexer.formatPos r}. Write #'form; the head is not meant to be written by hand."

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
            | op when
                (List.contains op foldingOps || List.contains op chainingOps)
                && not (args |> List.exists (function
                                             | SAtom { Token = Keyword _ } -> true
                                             | _ -> false))
                ->
                desugarNaryOp (op <> sym) op (processArgs args) listRange

            // A macro, tried last so that a special form always wins. Anything
            // reaching here is either a macro call or an ordinary application,
            // and the head is the only thing that decides which.
            | _ ->
                match expandHook s with
                | Some expansion -> expansion.Resolve Set.empty (parseExpr expansion.Form)
                | None -> EApp(EIdent(sym, getRange head), processArgs args, listRange)


        | _ ->
            // Fallback for tuples or unquoted lists
            EApp(parseExpr head, processArgs args, listRange)

    | SList([], listRange) -> ETuple([], listRange)

    // Explicit token catches for better debugging
    | SAtom { Token = Comma } -> failwithf $"Unexpected comma at %s{Lexer.formatPos r}"
    | SAtom { Token = Quote } -> failwithf $"Unexpected quote at %s{Lexer.formatPos r}"
    | _ -> failwithf $"Unexpected expression at %s{Lexer.formatPos r}"

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

/// Rewrites `(:until-cancelled)` and `(:until-cancelled token)` into clauses the
/// loop facility already has.
///
/// For the compute loop with no sync point to hang an `until-cancelled` event
/// on. The zero-arity form becomes two clauses:
///
///     (:with %tok (parameter-ref current-cancel))   ;; loop ENTRY, wherever the
///                                                   ;; clause was written
///     (:break (cancelled? %tok))                    ;; left exactly where it was
///
/// Two-expression `:with` is already the loop-invariant binding form, so this
/// needs no new machinery. The break stays put because clause order decides
/// *where in an iteration* the exit happens, and that is the author's to say.
///
/// **The hoist is the point.** `current-cancel` holds a field on `DynEnv` — it
/// is one of the three parameters that do — so `parameter-ref` on it is
/// `FiberContext.Current`, a cast and a null test rather than a champ descent.
/// A thread-static read every iteration is still more than a loop whose body is
/// arithmetic should pay for something that cannot change under it.
///
/// *Why it is safe:* only `parameterize` can rebind it, and that is a
/// `try/finally`, so it has restored before control reaches the loop head again;
/// `parameter-push!`/`dyn-restore!` are not surface API; and `FiberContext` is
/// reinstated around every suspension, so a fiber sees the same token before and
/// after a `sync`.
///
/// *Why loop entry rather than function entry:* a loop inside a `parameterize`
/// in the same function would otherwise read what was ambient *outside* it,
/// which is usually the root token — the one that never fires.
///
/// **The limitation this locks in:** the ambient token cannot change under a
/// running loop. True today, because a fiber's dynamic environment is written
/// only by lexically scoped push/restore on that same fiber. It forecloses a
/// supervisor reaching into a running child to swap its deadline; if that is
/// ever wanted, the way in is a level of indirection — a token whose replacement
/// is itself observable — not mutating another fiber's environment.
///
/// The explicit form does no lookup at all, and is the only way to watch a token
/// that is not the ambient one.
///
/// Either way the test is once per iteration: a single ten-second iteration
/// still takes ten seconds.
and private expandUntilCancelled (clauseForms: SExpr list) : SExpr list =
    let at r t = SAtom { Token = t; Range = r }

    // Every zero-arity clause's binding, in the order the clauses were written.
    let mutable entryBindings = []

    let rewrite (s: SExpr) =
        match s with
        | SList(SAtom { Token = Keyword "until-cancelled" } :: rest, r) ->
            let token =
                match rest with
                | [] ->
                    let name = Gensym.fresh "untilcancel"

                    entryBindings <-
                        entryBindings
                        @ [ SList(
                                [ at r (Keyword "with")
                                  at r (Symbol name)
                                  SList([ at r (Symbol "parameter-ref"); at r (Symbol "current-cancel") ], r) ],
                                r
                            ) ]

                    at r (Symbol name)
                | [ token ] -> token
                | _ ->
                    failwithf
                        $"Invalid (:until-cancelled ...) at %s{Lexer.formatPos r}. Expected: (:until-cancelled) for the ambient token, or (:until-cancelled token) for a named one."

            SList([ at r (Keyword "break"); SList([ at r (Symbol "cancelled?"); token ], r) ], r)

        | other -> other

    let rewritten = clauseForms |> List.map rewrite
    entryBindings @ rewritten

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

    let clauseForms = expandUntilCancelled clauseForms

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

    let call (name: string) (args: Expr list) (cr: Range) = EApp(EResolved(name, cr), args, cr)

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
                      CollectorExpr = call "folding" [ EBool(false, cr) ] cr
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
                (fun sn cn -> cn, call "iterable-next" [ EIdent(sn, cr); EIdent(cn, cr) ] cr)
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
                    Some(call "iterable-done?" [ EIdent(levels[i].SeqNames[fi], r); EIdent(levels[i].CurNames[fi], r) ] r)
                | Choice2Of2 wi ->
                    let (_, _, _, endCond, _) = levels[i].Withs[wi]
                    endCond |> Option.map parseExpr)

        let rec anyOf ts =
            match ts with
            | [ last ] -> last
            | t :: tl -> EIf(t, EBool(true, r), anyOf tl, r)
            | [] -> EBool(false, r)

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
    /// perfectly well bind a name of its own that happens to collide — which is
    /// why this is `freeNamesWith` rather than a search for the spelling. It
    /// used to be a walker of its own, and that walker had already drifted: its
    /// pattern case did not see a `(:is T e)` binder, so a finish block that
    /// rebound the name that way was refused for shadowing it.
    let rejectWithInFinish (e: Expr) : unit =
        let names = Set.ofList withVarNames

        freeNamesWith
            (fun n ir _ ->
                if Set.contains n names then
                    failwithf
                        $"'%s{n}' at %s{Lexer.formatPos ir} is a (:with ...) variable, and a loop variable is not in scope after the loop: the finish block is reached from every exit, and an inner level's variables do not exist at an exit taken from an outer one. Carry it out with an accumulator — (:acc last (folding 0 %s{n})) — and name that in the '=>' instead.")
            false
            Set.empty
            e

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
                | [] -> EWhen(EBool(false, r), ETuple([], r), false, r)
                | [ slot ] -> EIdent(slot.Name, slot.Range)
                | _ -> ETuple(declared |> List.map (fun slot -> EIdent(slot.Name, slot.Range)), r)

        List.foldBack
            (fun slot acc ->
                ELet(
                    slot.Name,
                    false,
                    [],
                    None,
                    call "collector-finish" [ EIdent(slot.Collector, slot.Range); EIdent(slot.Name, slot.Range) ] slot.Range,
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
            call "collector-step" [ EIdent(slot.Collector, cr); EIdent(slot.Name, cr); parseExpr slot.StepForm ] cr

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
            @ (List.map2 (fun cn t -> cn, call "iterable-start" [ EIdent(t, cr) ] cr) levels[i].CurNames temps)
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
                bindLoopPattern pat (call "iterable-current" [ EIdent(sn, cr); EIdent(cn, cr) ] cr) acc cr)
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

            (lvl.Member, true, slotNames lvl.Index |> List.map (fun n -> MandatoryArg(n, None)), None, body))

    // The finish member. It calls nothing, so `LetRecify` gives it a component
    // of its own and it is bound ahead of the loop group rather than becoming a
    // case in the same switch — which costs one call on the way out and saves a
    // copy of the block at every other exit.
    let members =
        members
        @ [ (exitName, true, accNames |> List.map (fun n -> MandatoryArg(n, None)), None, finishBlockBody) ]

    // In `slotNames 0`'s order: level 0's cursors, then its `:with` slots, then
    // the accumulators.
    let initialArgs =
        (levels[0].SeqNames
         |> List.map (fun sn -> call "iterable-start" [ EIdent(sn, r) ] r))
        @ (levels[0].Withs |> List.map (fun (_, start, _, _, _) -> parseExpr start))
        @ (accInfo |> List.map (fun slot -> call "collector-init" [ EIdent(slot.Collector, slot.Range) ] slot.Range))

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

/// The argument list of a `defun`, top-level or body-local.
and parseDefunArgs (args: SExpr list) : DefunArg list =
    match args with
    | [] -> []
    | SAtom { Token = Symbol n } :: rest -> MandatoryArg(n, None) :: parseDefunArgs rest
    // `(: name type)` — a parameter's type written at the parameter. A
    // top-level `defun` normally takes its types from the signature declared
    // beside it, and one written here has to agree with that.
    | SList([ SAtom { Token = Colon }; SAtom { Token = Symbol n }; t ], _) :: rest ->
        MandatoryArg(n, Some(parseType t)) :: parseDefunArgs rest
    | SAtom { Token = Comma } :: rest -> parseDefunArgs rest
    // A keyword parameter's default is evaluated **sequentially**, with the
    // parameters before it in scope — unchanged by `let` becoming simultaneous.
    // The list is a C# parameter list by the time this runs anywhere, and a
    // default that reads an earlier parameter is emitted as an expression in the
    // method body, where the earlier one is already a local. There is nowhere to
    // put a simultaneous reading even if one were wanted.
    | SList(SAtom { Token = Keyword name } :: [ defaultExpr ], _) :: rest ->
        KeywordArg(name, parseExpr defaultExpr) :: parseDefunArgs rest
    | SAtom { Token = Keyword "rest" } :: SAtom { Token = Symbol name } :: rest ->
        if not rest.IsEmpty then
            failwithf $"Rest argument must be the last argument at %s{Lexer.formatPos (getRange (List.head rest))}"
        [RestArg name]
    | SAtom { Token = Keyword name } :: defaultExpr :: rest ->
        KeywordArg(name, parseExpr defaultExpr) :: parseDefunArgs rest
    | bad :: _ -> failwithf $"Invalid defun argument at %s{Lexer.formatPos (getRange bad)}"

/// The optional `: type` between a `defun`'s arguments and its body.
and parseDefunReturn (rest: SExpr list) : FType option * SExpr list =
    match rest with
    | SAtom { Token = Colon } :: t :: body -> Some(parseType t), body
    | body -> None, body

/// A body: a sequence of forms, where a definition scopes over what follows it.
///
/// Five heads are consumed *here* rather than by `parseExpr`, and are reserved
/// in body position because of it: `def`, `defun`, `defbjo`, `def/mutable` and
/// `begin`. The parser has no scope at parse time, so it cannot know that one
/// of them was rebound — `(defun (begin xs) ...)` still defines a function, but
/// it can never be *called* as `(begin xs)` inside a body, and
/// `(let ((begin f)) (begin 1 2))` splices rather than calls.
///
/// The same is already true of the other four, and for the same reason.
and parseBody (exprs: SExpr list) (fallbackRange: Range) : Expr =
    // The third renaming rule, for the heads this function consumes.
    //
    // `def`, `defun`, `def/mutable`, `defbjo` and `begin` never reach
    // `parseExpr`'s special-form chain, because `parseBody` takes them first —
    // so a template that writes `(let () (def x 1) x)` needs its mark stripped
    // here or the `def` is read as a call to something named `def`.
    //
    // `begin` is in the list for that reason and no other: a splice a macro
    // wrote arrives as `begin__37`, and without this it would fall past every
    // case below into an ordinary application and fail with "Unbound variable:
    // begin__37" — naming nothing the programmer wrote. Splicing a
    // macro-written body is the whole point of the form, so leaving it out
    // would make the feature do nothing where it is most wanted.
    //
    // Only these five, and only the head. Every other identifier keeps its
    // mark: that is what rule two resolves a macro module's own helper by, and
    // what keeps a template's binder uncapturable.
    let unmarkedHead (items: SExpr list) =
        match items with
        | SList(SAtom({ Token = Symbol sym } as head) :: rest, r) :: tail when sym <> headName sym ->
            match headName sym with
            | ("def" | "defun" | "defbjo" | "def/mutable" | "begin") as stripped ->
                SList(SAtom { head with Token = Symbol stripped } :: rest, r) :: tail
            | _ -> items
        | _ -> items

    let rec collectDefs acc remaining =
        match unmarkedHead remaining with
        // A non-empty splice does not close the group. Without this case a
        // `begin` between two mutually recursive definitions would put them in
        // separate `ELetRec`s, and the first would fail with "Unbound
        // variable" naming the second — on a program that looks obviously fine.
        //
        // We don't need a special case to handle empty blocks here. The parser
        // naturally treats an empty block as a basic statement. Just like any
        // normal expression, hitting a statement signals the end of the current
        // block of definitions.
        | SList(SAtom { Token = Symbol "begin" } :: (_ :: _ as inner), _) :: rest ->
            collectDefs acc (inner @ rest)

        | SList(SAtom { Token = Symbol "def" } :: SAtom { Token = Symbol name } :: [ expr ], _) :: rest ->
            // isFun = false, args = []
            collectDefs ((name, false, [], None, parseExpr expr) :: acc) rest

        | SList(SAtom { Token = Symbol "def" } :: SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) :: [ expr ], _) :: rest ->
            collectDefs ((name, false, [], Some(parseType tType), parseExpr expr) :: acc) rest

        // A body-local `defbjo`. Still rejected, and no longer for the reason it
        // used to be: a body-local function may suspend now, emitted as an
        // async C# local function. What it may not do is *declare* that, and
        // that is the language's own rule rather than a limit of the emitter —
        // colour is declared where a signature is required, and a local
        // definition has none. Its colour is read off what its body reaches.
        //
        // So the form has nothing left to mean. Accepting it would give the
        // reader two spellings with one behaviour, and the wrong impression
        // that the other spelling does not suspend.
        | SList(SAtom { Token = Symbol "defbjo" } :: SList(SAtom { Token = Symbol name } :: _, _) :: _, r) :: _ ->
            failwithf
                $"Syntax Error at %s{Lexer.formatPos r}: a bjoroutine may only be defined at the top level, and '%s{name}' is inside a body. Write it (defun ...): a body-local function takes its colour from what its body reaches, so it suspends when it needs to and costs nothing when it does not."

        | SList(SAtom { Token = Symbol "defun" } :: SList(SAtom { Token = Symbol name } :: args, _) :: rest, r) :: rest' ->
            let parsedArgs = parseDefunArgs args
            let retType, bodyExprs = parseDefunReturn rest
            let fBody = parseBody bodyExprs r
            collectDefs ((name, true, parsedArgs, retType, fBody) :: acc) rest'

        | _ -> (List.rev acc, remaining)

    and parseItems remaining =
        match unmarkedHead remaining with
        // A body with nothing in it is `unit` — the value a Bjolang signature
        // spells `void`. Not `ETuple []`, which is an empty *tuple* and unifies
        // with nothing anyone can write; `(begin)` in expression position is
        // the form that made the difference reachable from source.
        | [] -> EResolved("unit", fallbackRange)

        // `(begin)` with nothing in it is `unit`, and *not* a splice of
        // nothing. The two differ in exactly one place, and it matters:
        //
        //   (defun (f)
        //     (compute!)
        //     (begin))
        //
        // Were the empty form to vanish, `(compute!)` would move from statement
        // position into tail position — the function's value would silently
        // become whatever it returns, and `MustUse` would stop reporting a
        // dropped `Result`. As `unit` it keeps `(compute!)` where it was
        // written and gives the body the value "nothing", which is what a form
        // that does nothing should mean.
        //
        // Declaration position reads an empty `begin` the other way, because
        // there is no value there for it to be.
        //
        // `unit` the builtin, not `ETuple []`: an empty tuple is a *tuple*, and
        // the unit type is what a Bjolang signature spells `void`. The two do
        // not unify, so a `(defun (f) (begin))` declared `(-> void)` would be a
        // type error naming a type nobody wrote.
        | [ SList([ SAtom { Token = Symbol "begin" } ], r) ] -> EResolved("unit", r)

        | SList([ SAtom { Token = Symbol "begin" } ], r) :: rest ->
            ELet("_", false, [], None, EResolved("unit", r), parseItems rest, fallbackRange)

        // A non-empty one splices into the body it stands in, which is what
        // lets a macro expand to several forms — a definition and the code
        // after it — where only one was written.
        | SList(SAtom { Token = Symbol "begin" } :: inner, _) :: rest -> parseItems (inner @ rest)

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

        // A macro in body position, expanded here rather than left to
        // `parseExpr`, because it may expand to a definition and `def` and
        // `defun` are consumed by this function — `parseExpr` never sees one.
        //
        // The resolution pass wraps the *whole* remaining body, and must: if
        // the expansion is a definition then everything after it is inside its
        // scope, and whether an introduced name is free is only answerable
        // there.
        | (SList(SAtom { Token = Symbol h } :: _, _) as form) :: rest when isMacroName h ->
            match expandHook form with
            // `Set.empty`: the spliced definitions are inside the expression
            // tree this parses, so `freeNames` already sees them as bound.
            | Some expansion -> expansion.Resolve Set.empty (parseItems (expansion.Form :: rest))
            | None -> failwithf $"'%s{h}' is a macro but did not expand at %s{Lexer.formatPos (getRange form)}"

        | [ expr ] -> parseExpr expr

        | expr :: rest -> ELet("_", false, [], None, parseExpr expr, parseItems rest, fallbackRange)

    parseItems exprs



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
      /// Bjolang type.
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
      Cancellable: bool
      /// `#:blocking` — calling the target parks the thread it runs on, so a
      /// bjoroutine that reaches it holds a pool thread rather than suspending.
      /// Read by the blocking lint and by nothing else.
      IsBlocking: bool
      /// `#:get` — the target names a property or field, and the alias reads
      /// it.
      IsGet: bool
      /// `#:set` — the target names a property or field, and the alias writes
      /// it.
      ///
      /// One accessor per clause, so a read/write property is two clauses under
      /// two names. A property has no overloads to disambiguate, so nothing is
      /// gained by naming both in one place, and two clauses is what lets a
      /// module import only the read.
      IsSet: bool }

/// One clause of `import/extern` or `import/class`.
///
/// Both forms are spelled the same way — a Bjolang name, then a colon form
/// naming the fully qualified .NET target, its type, and optionally the
/// exceptions the call is allowed to turn into an `Err` — so one reader does
/// for both.
let parseForeignImportClause
    (formName: string)
    (s: SExpr)
    : string * string list * string * ForeignImportOptions * Range =
    let r = getRange s

    let malformed () : 'a =
        failwithf
            $"Syntax error in %s{formName} at %s{Lexer.formatPos r}: expected (alias (: Fully.Qualified.Target type)), the type optionally followed by #:exceptions (ExceptionType ...), #:async, #:blocking, #:uncancellable, #:get or #:set."

    match s with
    // The alias may be written applied — `(Set %a)` — which is how a generic
    // .NET type is named: it is a type constructor, so it is not a type until
    // it has its arguments, and `import/class` is the only form that accepts
    // the applied spelling. `parseTypeDefHead` reads it, so the shape is the
    // one every other type declaration in the language uses.
    | SList([ head; SList(SAtom { Token = Colon } :: SAtom { Token = Symbol clrTarget } :: rest, _) ], _) ->
        let alias, typeParams =
            match head with
            | SAtom { Token = Symbol name } -> name, []
            | SList _ -> parseTypeDefHead head
            | _ -> malformed ()

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
            | SAtom { Token = Keyword "blocking" } :: tail -> readOptions { opts with IsBlocking = true } tail
            | SAtom { Token = Keyword "get" } :: tail -> readOptions { opts with IsGet = true } tail
            | SAtom { Token = Keyword "set" } :: tail -> readOptions { opts with IsSet = true } tail
            | _ -> malformed ()

        let options =
            readOptions
                { ExplicitType = explicitType
                  Exceptions = []
                  IsAsync = false
                  Uncancellable = false
                  Cancellable = false
                  IsBlocking = false
                  IsGet = false
                  IsSet = false }
                optionForms

        alias, typeParams, clrTarget, options, r
    | _ -> malformed ()

/// Rejects the keyword combinations an accessor clause cannot mean.
///
/// Shared by `import/extern` and `import/class` so that the second form's
/// blanket refusal of `#:get` and `#:set` still says the same things about
/// them.
let private checkAccessorOptions (formName: string) (opts: ForeignImportOptions) (r: Range) : unit =
    let where = Lexer.formatPos r

    if opts.IsGet && opts.IsSet then
        failwithf
            $"Syntax error in %s{formName} at %s{where}: a clause is one accessor. Write the read and the write as two clauses under two names — that is also what lets a module import only the read."

    if opts.IsGet || opts.IsSet then
        if opts.IsAsync || opts.Cancellable || opts.Uncancellable then
            failwithf
                $"Syntax error in %s{formName} at %s{where}: #:async, #:cancellable and #:uncancellable describe how a *call* is made, and reading or writing a property is not a call. Nothing about a property can be awaited or cancelled."

        if opts.IsBlocking then
            failwithf
                $"Syntax error in %s{formName} at %s{where}: #:blocking says a *call* parks the thread it runs on, and reading or writing a property is not a call. A property that does real work behind an accessor is better imported as the method it is."

        if not opts.Exceptions.IsEmpty then
            failwithf
                $"Syntax error in %s{formName} at %s{where}: #:exceptions decorates a call, and a property access is not one. An accessor that can fail is guarded with (try ... #:catch (...)) at the place it is read or written."

/// A prefix or postfix has to lex as part of a symbol.
///
/// The name it builds is one the importing module writes by hand, so a prefix
/// containing anything the tokenizer would break on produces a binding nothing
/// can refer to. `/` is the expected separator and is a symbol character; `.`
/// is one too, but is refused anyway — a dot is how a .NET member is spelled,
/// and a name that looks like one but is not would be read as such well before
/// anybody suspected the import.
let private checkAffix (form: string) (affix: string) (r: Lexer.Range) : string =
    if affix = "" then
        failwithf $"Empty string in (%s{form} ...) at %s{Lexer.formatPos r}. A prefix or postfix has to be a non-empty string."

    match affix |> Seq.tryFind (fun c -> not (Lexer.isSymbolChar c) || c = '.') with
    | Some bad ->
        let described =
            if System.Char.IsWhiteSpace bad then
                "whitespace, which cannot appear inside a symbol"
            elif bad = '.' then
                "'.', which is how a .NET member is spelled"
            else
                $"'%c{bad}', which cannot appear inside a symbol"

        failwithf
            $"Invalid prefix \"%s{affix}\" in (%s{form} ...) at %s{Lexer.formatPos r}: it contains %s{described}. A prefix has to lex as part of the name it builds — '/' is the usual separator."
    | None -> affix

/// One import, which is a path wrapped in zero or more modifiers.
///
/// Modifiers nest rather than sit in a list, so that they compose in a stated
/// order: the innermost applies first. They are collected outermost-first here
/// and reversed, so `Modifiers` reads in application order.
let rec private parseImportForm (s: SExpr) : ImportSpec =
    let r = getRange s

    let rec go (s: SExpr) (acc: ImportModifier list) : ImportSpec =
        let names (nodes: SExpr list) =
            nodes
            |> List.map (function
                | SAtom { Token = Symbol n } -> n
                | bad -> failwithf $"Expected a name at %s{Lexer.formatPos (getRange bad)}")

        match s with
        | SAtom { Token = StringLit p } -> { Path = RelativePath p; Modifiers = acc }

        | SList(SAtom { Token = Symbol head } :: inner :: rest, mr) when
            List.contains
                head
                [ "only"; "except"; "prefix"; "postfix"; "prefix-defs"; "postfix-defs"; "prefix-types"
                  "postfix-types"; "rename" ]
            ->
            let modifier =
                match head, rest with
                | "only", args -> Only(names args)
                | "except", args -> Except(names args)
                | "rename", pairs ->
                    pairs
                    |> List.map (function
                        | SList([ SAtom { Token = Symbol from }; SAtom { Token = Symbol to' } ], _) -> from, to'
                        | bad ->
                            failwithf
                                $"Invalid (rename ...) clause at %s{Lexer.formatPos (getRange bad)}. Expected: (old-name new-name)")
                    |> Rename
                | affixForm, [ SAtom { Token = StringLit affix } ] ->
                    let a = checkAffix affixForm affix mr

                    match affixForm with
                    | "prefix" -> Prefix a
                    | "postfix" -> Postfix a
                    | "prefix-defs" -> PrefixDefs a
                    | "postfix-defs" -> PostfixDefs a
                    | "prefix-types" -> PrefixTypes a
                    | _ -> PostfixTypes a
                | affixForm, _ ->
                    failwithf
                        $"Invalid (%s{affixForm} ...) at %s{Lexer.formatPos mr}. Expected: (%s{affixForm} import \"affix\")"

            go inner (modifier :: acc)

        // A bare list of symbols is a module path, and so the innermost thing
        // an import can be. Checked after the modifier heads, which are lists
        // of the same shape.
        | SList(pathNodes, _) when not pathNodes.IsEmpty ->
            { Path = ModulePath(names pathNodes); Modifiers = acc }

        | _ -> failwithf $"Invalid import syntax at %s{Lexer.formatPos r}"

    go s []

/// The type every macro transformer has. Fixed, and not written by the user:
/// the expander constructs the arguments and consumes the result, so there is
/// nothing here for a program to choose.
let macroTransformerType (r: Range) : FType =
    let syntax = TName("Syntax", r)
    let inject = TArrow([ TName("Symbol", r) ], [], None, syntax, Ordinary, r)
    let compare = TArrow([ syntax; syntax ], [], None, TName("bool", r), Ordinary, r)
    TArrow([ syntax; inject; compare ], [], None, syntax, Ordinary, r)

/// Every name a group of declarations introduces.
///
/// This is what makes rule 1 apply to a top-level splice.
/// `Macro.resolveIntroduced` asks `AlphaRename.freeNames` which of a template's
/// introduced names came back *free*, and resolves those by rule 2 or rule 3.
/// Inside a body that is the right question, because a spliced `def` is a
/// binder the expression tree physically contains. At the top level there is no
/// enclosing expression, so every one of them looks free:
///
///   #'(begin (def counter 0) (defun (bump) (set! counter (+ counter 1))))
///
/// `counter__9` is free in `bump`, and would be rewritten either to a qualified
/// reference into the macro's module — which has no such binding — or, by rule
/// 3, to a bare `counter` that whatever the call site named `counter` then
/// captures. Handing the group's own binders to `Resolve` as already-bound
/// keeps it spelled `counter__9` on both sides, which is uncapturable.
///
/// Total, with no wildcard: a `Decl` case added later should stop the build
/// here rather than turn into an unbound variable inside somebody's macro.
let rec boundNames (decls: Decl list) : Set<string> =
    let ofDecl (d: Decl) : string list =
        match d with
        | DDef(name, _, _) -> [ name ]
        | DDefMutable(name, _, _) -> [ name ]
        // The parameters as well as the name. `mapDeclExprs` hands `Resolve`
        // the body on its own, so a parameter is not visible to `freeNames` as
        // the binder it is — and a template that writes `(defun (f x) x)` would
        // have the `x` in the body treated as free and stripped to `x` by rule
        // 3, while the parameter kept its fresh `x__12`. The body would then
        // reference something nothing binds.
        //
        // Names are memoised per invocation, so a template that both binds `x`
        // and refers to an outer `x` spells them the same and no rule could
        // tell them apart anyway. Rule 1 is the safer of the two readings.
        | DDefun(name, args, _, _, _) ->
            name
            :: (args
                |> List.map (function
                    | MandatoryArg(n, _) -> n
                    | KeywordArg(n, _) -> n
                    | RestArg n -> n))
        // The same, and for the same reason. Both bodies see one parameter
        // list, so it is bound once here rather than once per colour.
        | DDefDouble(name, args, _, _, _) ->
            name
            :: (args
                |> List.map (function
                    | MandatoryArg(n, _) -> n
                    | KeywordArg(n, _) -> n
                    | RestArg n -> n))
        | DDefTuple(names, _, _) -> names
        // Not a binder. It is renamed from the same memo as the `defun` it
        // belongs to, so leaving it out would take the pair apart: the body
        // would keep its fresh spelling and the signature would lose it.
        | DSignature(name, _, _, _) -> [ name ]
        | DType(defs, _)
        | DTypeRec(defs, _) ->
            defs
            |> List.collect (fun td ->
                // The type's own name, which is also its record or struct
                // constructor, plus every union case. Field names are
                // deliberately *not* here: a field is a string inside
                // `EGetField` and `ERecordUpdate` rather than an expression, so
                // nothing ever renames one and there is nothing to keep bound.
                td.Name
                :: (match td.Kind with
                    | Union cases ->
                        cases
                        |> List.map (function
                            | SimpleCase(n, _) -> n
                            | DataCase(n, _, _, _) -> n)
                    // An opaque type's members are not bound here either: the
                    // whole of what it publishes is a name.
                    | Alias _
                    | Record _
                    | Opaque _ -> []))
        | DTrait(name, _, _, _, signatures, defaults, _, _) ->
            (name :: (signatures |> List.map fst)) @ Set.toList (boundNames defaults)
        | DImpl(_, _, _, _, methods, _) -> Set.toList (boundNames methods)
        | DExtern(visible, _, _, _, _) -> [ visible ]
        | DAlias(visible, _, _) -> [ visible ]
        | DImportAlias(visible, _, _, _) -> [ visible ]
        | DImportExtern(specs, _) -> specs |> List.map (fun spec -> spec.Alias)
        | DImportClass(specs, _) -> specs |> List.map (fun spec -> spec.Alias)
        | DModule(_, inner, _) -> Set.toList (boundNames inner)
        // Nothing bound. An import brings names in without this declaration
        // naming them, and the other four introduce no binding at all.
        | DImport _
        | DExport _
        | DReExport _
        | DMacro _
        | DSyncOnly _
        | DImplExtern _
        | DInlineImpl _ -> []

    decls |> List.collect ofDecl |> Set.ofList

/// `f` applied to every expression a declaration carries.
///
/// Total and wildcard-free for `boundNames`' reason: a new `Decl` holding an
/// expression that this quietly skipped would leave a macro's introduced names
/// unresolved inside it, and the failure would name generated code.
let rec mapDeclExprs (f: Expr -> Expr) (d: Decl) : Decl =
    let mapArg (a: DefunArg) =
        match a with
        | MandatoryArg _
        | RestArg _ -> a
        // A keyword default is an ordinary expression that may mention an
        // introduced name, and it is nowhere near the body — easy to miss, and
        // invisible to every test whose macro does not write one.
        | KeywordArg(name, defaultExpr) -> KeywordArg(name, f defaultExpr)

    match d with
    | DDef(name, e, r) -> DDef(name, f e, r)
    | DDefMutable(name, e, r) -> DDefMutable(name, f e, r)
    | DDefTuple(names, e, r) -> DDefTuple(names, f e, r)
    | DDefun(name, args, body, colour, r) -> DDefun(name, List.map mapArg args, f body, colour, r)
    | DDefDouble(name, args, syncBody, bjoBody, r) ->
        DDefDouble(name, List.map mapArg args, f syncBody, f bjoBody, r)
    | DTrait(name, v, arity, assoc, signatures, defaults, clr, r) ->
        DTrait(name, v, arity, assoc, signatures, defaults |> List.map (mapDeclExprs f), clr, r)
    | DImpl(name, target, assoc, constraints, methods, r) ->
        DImpl(name, target, assoc, constraints, methods |> List.map (mapDeclExprs f), r)
    | DModule(name, inner, r) -> DModule(name, inner |> List.map (mapDeclExprs f), r)
    | DInlineImpl(traitName, method_, ctor, origin, ps, body, qual, r) ->
        DInlineImpl(traitName, method_, ctor, origin, ps, f body, qual, r)
    // No expression to map. A signature and a type carry `FType`s, which hold
    // no names a macro introduces, and the rest are declarations about names.
    | DSignature _
    | DImport _
    | DAlias _
    | DExport _
    | DReExport _
    | DType _
    | DTypeRec _
    | DExtern _
    | DImportAlias _
    | DImportExtern _
    | DImportClass _
    | DMacro _
    | DSyncOnly _
    | DImplExtern _ -> d

/// Literal `(begin ...)` forms flattened out of a list of declaration forms.
///
/// For the two places that read a body of declarations with their own loop
/// rather than through `parseDeclForms` — `def/trait` and `impl` — so that
/// a splice means the same thing inside one of those as it does at the top
/// level. Recursive, so nesting flattens; a `begin` a template wrote is
/// unmarked on the way past, as everywhere else a head is dispatched on.
let rec flattenBegins (forms: SExpr list) : SExpr list =
    forms
    |> List.collect (fun form ->
        match stripHeadMark form with
        | SList(SAtom { Token = Symbol "begin" } :: inner, _) -> flattenBegins inner
        | other -> [ other ])

/// What a declaration is, for an error message that has to say what a macro
/// produced where something else was wanted.
let declKindName (d: Decl) : string =
    match d with
    | DSignature _ -> "a signature"
    | DImport _ -> "an import"
    | DAlias _ -> "an alias"
    | DExport _ -> "an export"
    | DReExport _ -> "a re-export"
    | DModule _ -> "a module"
    | DDef _ -> "a definition"
    | DDefTuple _ -> "a tuple definition"
    | DDefMutable _ -> "a mutable definition"
    | DDefun _ -> "a function"
    | DDefDouble _ -> "a function with a body per colour"
    | DType _
    | DTypeRec _ -> "a type declaration"
    | DTrait _ -> "a trait"
    | DExtern _ -> "an imported binding"
    | DImportAlias _ -> "an imported spelling"
    | DImportExtern _ -> "a foreign import"
    | DImportClass _ -> "a class import"
    | DInlineImpl _ -> "an inline method body"
    | DMacro _ -> "a macro"
    | DSyncOnly _ -> "a #:sync marker"
    | DImpl _ -> "an implementation"
    | DImplExtern _ -> "an imported implementation"

/// A macro may not introduce an import.
///
/// `Pipeline.importsOf` and `expandIncludes` read the raw S-expressions of a
/// file *before* it is parsed, and they have to: parsing a form whose head is a
/// macro requires that macro's own module to be compiled and loaded already. So
/// an `(import ...)` a macro produces is one the module graph never saw — the
/// dependency is never compiled, never linked, and the user is told about an
/// unbound variable in code they did not write.
let rejectSplicedImports (decls: Decl list) (callSite: Range) : unit =
    for d in decls do
        match d with
        | DImport _ ->
            failwithf
                $"Macro Error at %s{Lexer.formatPos callSite}: a macro cannot introduce an import. The import graph is built from the source forms before any macro runs, because parsing a macro call requires that macro's module to be compiled already — so an import a macro produces is never linked. Write the import in the file that uses the macro."
        | _ -> ()

/// One declaration form, or `None` when nothing here matched it.
///
/// `None` says only that: what it *means* is the caller's to decide.
/// `parseDeclForms` reads it as "then it must be a macro call", and `parseDecl`
/// as an error. Keeping the two apart is what lets a macro in declaration
/// position produce several declarations — a function returning one `Decl` has
/// nowhere to put them.
/// A `defun` or `defbjo`, and the signature it writes for itself.
///
/// A top-level function needs a `(: name type)`: `Inference` refuses every name
/// but `main` without one, and `Exports` refuses to publish one. Writing the
/// types at the parameters and after the argument list says the same thing in
/// one form rather than two —
///
///   (: double (-> int int))        (defun (double (: x int)) : int
///   (defun (double x) (* x 2))       (* x 2))
///
/// — so that is what it becomes. The signature is *synthesized*, not a second
/// mechanism: the arity check, the export rule and the metadata a `.dll`
/// publishes all read a `DSignature` and cannot tell which spelling produced
/// it. `def/macro` writes its transformer's signature the same way.
///
/// The colour is `Ordinary` whichever definer this is, because that is what a
/// hand-written arrow says. `defbjo` is what declares a function may suspend,
/// and `recolour` repaints the declared type before anything unifies with it.
///
/// All or nothing. A return type beside an unannotated parameter is refused
/// rather than half-read: it used to be accepted and silently discarded, which
/// reads as a claim that was checked.
let private parseDefunDecl
    (definer: string)
    (name: string)
    (args: SExpr list)
    (rest: SExpr list)
    (r: Lexer.Range)
    : Decl list =

    let colour = if definer = "defbjo" then Suspending else Ordinary
    let parsedArgs = parseDefunArgs args
    let retAnn, bodyExprs = parseDefunReturn rest
    let defun = DDefun(name, parsedArgs, parseBody bodyExprs r, colour, r)

    match retAnn with
    | None -> [ defun ]
    | Some ret ->
        let where = Lexer.formatPos r

        let refuse (what: string) =
            failwithf
                $"Invalid signature on '%s{name}' at %s{where}: %s{what} Write a (: %s{name} ...) beside the definition instead."

        // A keyword or a rest parameter has nowhere to write its type — the
        // argument grammar gives one a default expression and the other a bare
        // name — so a function taking either cannot describe itself here, and
        // says so rather than producing a signature with a parameter missing.
        for a in parsedArgs do
            match a with
            | MandatoryArg _ -> ()
            | KeywordArg(kw, _) ->
                refuse $"the keyword parameter '#:%s{kw}' has nowhere to write its type."
            | RestArg restName -> refuse $"the rest parameter '%s{restName}' has nowhere to write its type."

        let mandatory =
            parsedArgs
            |> List.choose (function
                | MandatoryArg(argName, ann) -> Some(argName, ann)
                | _ -> None)

        match mandatory |> List.tryFind (snd >> Option.isNone) with
        | Some(bare, _) ->
            failwithf
                $"Invalid signature on '%s{name}' at %s{where}: the return type is written here but the parameter '%s{bare}' has no type. Give every parameter one — (: %s{bare} <type>) — or drop the return type and write a (: %s{name} ...) beside the definition."
        | None ->
            // A trait constraint has nowhere to go in this form either, so a
            // constrained generic still writes its signature separately. The
            // empty list is what an unconstrained one has.
            let argTypes = mandatory |> List.map (snd >> Option.get)
            [ DSignature(name, TArrow(argTypes, [], None, ret, Ordinary, r), [], r); defun ]

let rec tryParseDecl (s: SExpr) : Decl option =
    let r = getRange s
    let s = stripHeadMark s

    match s with
    | SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) ->
        Some(DSignature(name, parseType tType, [], r))
    // (: name type (where (TraitName %var) ...)) — signature with trait constraints
    | SList(SAtom { Token = Colon } :: SAtom { Token = Symbol name } :: tType :: SList(SAtom { Token = Symbol "where" } :: constraintExprs, _) :: _, _) ->
        let constraints =
            constraintExprs |> List.choose (function
                | SList([ SAtom { Token = Symbol traitName }; SAtom { Token = QuotedSymbol varName } ], _) ->
                    Some (traitName, "'" + varName)
                | SList([ SAtom { Token = Symbol traitName }; SAtom { Token = Symbol varName } ], _) ->
                    Some (traitName, varName)
                | _ -> None)
        Some(DSignature(name, parseType tType, constraints, r))

    | SList(SAtom { Token = Symbol "import" } :: imports, _) ->
        Some(DImport(List.map parseImportForm imports, r))

    // (:alias new-name existing-name)
    //
    // `:alias` is one `Keyword` token rather than a colon and a symbol, so this
    // is not the `(: name type)` shape above and does not compete with it.
    | SList(SAtom { Token = Keyword "alias" } :: rest, _) ->
        match rest with
        | [ SAtom { Token = Symbol newName }; SAtom { Token = Symbol oldName } ] ->
            Some(DAlias(newName, oldName, r))
        | _ ->
            failwithf
                $"Invalid (:alias ...) at %s{Lexer.formatPos r}. Expected: (:alias new-name existing-name)"

    // (import/extern (write-line (: System.Console.WriteLine (-> string void))) ...)
    | SList(SAtom { Token = Symbol "import/extern" } :: clauses, _) ->
        if clauses.IsEmpty then
            failwithf $"Syntax error in import/extern at %s{Lexer.formatPos r}: it imports nothing."

        let specs =
            clauses
            |> List.map (fun c ->
                let alias, typeParams, target, opts, cr = parseForeignImportClause "import/extern" c
                checkAccessorOptions "import/extern" opts cr

                // An extern alias is a *binding*, and a binding takes no type
                // parameters: what makes one polymorphic is the signature, and
                // that is where a generic method's arguments are solved from.
                if not typeParams.IsEmpty then
                    failwithf
                        $"Syntax error in import/extern at %s{Lexer.formatPos cr}: '%s{alias}' is written applied to type parameters, and an extern alias is a function rather than a type. A generic method's type arguments come from its declared signature — write them there, as in (-> (Set %%a) %%a (Set %%a))."

                { Alias = alias
                  ClrTarget = target
                  ExplicitType = opts.ExplicitType
                  Exceptions = opts.Exceptions
                  IsAsync = opts.IsAsync
                  Uncancellable = opts.Uncancellable
                  Cancellable = opts.Cancellable
                  IsBlocking = opts.IsBlocking
                  IsGet = opts.IsGet
                  IsSet = opts.IsSet
                  Range = cr })

        Some(DImportExtern(specs, r))

    // (import/class (StreamWriter (: System.IO.StreamWriter (-> string StreamWriter))) ...)
    | SList(SAtom { Token = Symbol "import/class" } :: clauses, _) ->
        if clauses.IsEmpty then
            failwithf $"Syntax error in import/class at %s{Lexer.formatPos r}: it imports nothing."

        let specs =
            clauses
            |> List.map (fun c ->
                let alias, typeParams, target, opts, cr = parseForeignImportClause "import/class" c
                checkAccessorOptions "import/class" opts cr

                // A constructor is never a task. Silently ignoring the flag
                // would mean an import that reads as async and is not.
                if opts.IsAsync || opts.Uncancellable || opts.Cancellable then
                    failwithf
                        $"Syntax error in import/class at %s{Lexer.formatPos cr}: #:async, #:cancellable and #:uncancellable describe how a call is made, and a constructor is not made that way. They belong on an import/extern clause."

                // Constructing is not the part that waits. Whatever a
                // constructor opens, the reads and writes afterwards are where
                // a thread is parked, and those are import/extern clauses.
                if opts.IsBlocking then
                    failwithf
                        $"Syntax error in import/class at %s{Lexer.formatPos cr}: #:blocking marks a call that parks the thread it runs on, and this form declares a type and its constructor. Put it on the import/extern clause for the method that does the waiting."

                // A class is not an accessor. `import/class` declares a type and
                // its constructor, and a property of that type is imported with
                // an `import/extern` clause naming it.
                if opts.IsGet || opts.IsSet then
                    failwithf
                        $"Syntax error in import/class at %s{Lexer.formatPos cr}: #:get and #:set name an accessor for a property or field, and this form declares a type and its constructor. Write the accessor as an import/extern clause."

                { Alias = alias
                  TypeParams = typeParams
                  ClrClass = target
                  ConstructorType = opts.ExplicitType
                  Exceptions = opts.Exceptions
                  Range = cr })

        Some(DImportClass(specs, r))

    | SList(SAtom { Token = Symbol "export" } :: exports, _) ->
        // Parse items like poop-on-you
        let exportNames =
            exports
            |> List.map (function
                | SAtom { Token = Symbol e } -> e
                | _ -> failwithf $"Invalid export item at %s{Lexer.formatPos r}")

        Some(DExport(exportNames, r))

    | SList(SAtom { Token = Symbol "re-export" } :: reExports, _) ->
        let reExportNames =
            reExports
            |> List.map (function
                | SAtom { Token = Symbol e } -> e
                | _ -> failwithf $"Invalid re-export item at %s{Lexer.formatPos r}")

        Some(DReExport(reExportNames, r))

    // `parseDeclForms` rather than `parseDecl`: a nested module is a
    // declaration list like any other, so a `begin` or a macro splices inside
    // one exactly as it does at the top level.
    | SList(SAtom { Token = Symbol "module" } :: SAtom { Token = Symbol name } :: body, _) ->
        Some(DModule(name, List.collect parseDeclForms body, r))

    | SList(SAtom { Token = Symbol "def" } :: SAtom { Token = Symbol name } :: [ expr ], _) ->
        Some(DDef(name, parseExpr expr, r))

    | SList(SAtom { Token = Symbol "def" } :: SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) :: [ expr ], _) ->
        Some(DDef(name, parseExpr expr, r))

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

        Some(DDefTuple(tupleNames, parseExpr expr, r))

    | SList(SAtom { Token = Symbol "def/mutable" } :: SAtom { Token = Symbol name } :: [ expr ], _) ->
        Some(DDefMutable(name, parseExpr expr, r))

    | SList(SAtom { Token = Symbol "def/mutable" } :: SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) :: [ expr ], _) ->
        Some(DDefMutable(name, parseExpr expr, r))

    | SList(SAtom { Token = Symbol(("defun" | "defbjo") as definer) } :: SList(SAtom { Token = Symbol name } :: args, _) :: rest, _) ->
        // The function only. A definition that carries its own types also makes
        // a signature, and one `Decl` has nowhere to put it — see
        // `tryParseDeclGroup`, which is what every caller that compiles a
        // module goes through.
        parseDefunDecl definer name args rest r
        |> List.tryPick (function
            | DDefun _ as d -> Some d
            | _ -> None)
    | SList(SAtom { Token = Symbol "defbjouble" } :: SList(SAtom { Token = Symbol name } :: args, _) :: clauses, _) ->
        let where = Lexer.formatPos r

        // The two bodies, by keyword rather than by position. Order-independent
        // because there is no reason for it not to be, and because `#:sync`
        // first reads better in some pairs and `#:bjo` first in others.
        let clauseNamed (want: string) =
            clauses
            |> List.tryPick (function
                | SList(SAtom { Token = Keyword k } :: body, cr) when k = want -> Some(parseBody body cr)
                | _ -> None)

        for c in clauses do
            match c with
            | SList(SAtom { Token = Keyword("sync" | "bjo") } :: _, _) -> ()
            | _ ->
                failwithf
                    $"Syntax error in defbjouble '%s{name}' at %s{where}: every clause is (#:sync body...) or (#:bjo body...), and this is neither."

        match clauseNamed "sync", clauseNamed "bjo" with
        | Some syncBody, Some bjoBody -> Some(DDefDouble(name, parseDefunArgs args, syncBody, bjoBody, r))
        | None, _ ->
            failwithf
                $"Syntax error in defbjouble '%s{name}' at %s{where}: it has no (#:sync ...) body. A defbjouble is written when the two colours call *different* .NET methods, so both halves have to be here. If there is only one implementation, this is a defun."
        | _, None ->
            failwithf
                $"Syntax error in defbjouble '%s{name}' at %s{where}: it has no (#:bjo ...) body. A defbjouble is written when the two colours call *different* .NET methods, so both halves have to be here. If there is only one implementation, this is a defun."

    | SList(SAtom { Token = Symbol "type" } :: typeDefs, _) -> Some(DType(List.map parseTypeDef typeDefs, r))

    | SList(SAtom { Token = Symbol "type-rec" } :: typeDefs, _) -> Some(DTypeRec(List.map parseTypeDef typeDefs, r))

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
        let mutable clrConstraint = None
        let mutable clrMembers = []

        for item in flattenBegins body do
            match item with
            // Match: (#:clr-constraint (System.Numerics.INumber %a))
            //
            // A list headed by the keyword rather than a keyword followed by
            // one, so that reading it needs no lookahead and the `def/trait`
            // head — which is matched at exactly two elements — is untouched.
            // `(#:name type)` in an arrow type is the same shape.
            | SList (SAtom { Token = Keyword "clr-constraint" } :: rest, cr) ->
                if clrConstraint.IsSome then
                    failwithf
                        $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos cr}: a trait stands for at most one .NET interface."

                match rest with
                | [ SList (SAtom { Token = Symbol ifaceName } :: ifaceArgs, _) ] ->
                    clrConstraint <- Some(ifaceName, ifaceArgs |> List.map parseType)
                // A non-generic interface may be written bare, since there are
                // no arguments to parenthesize it around.
                | [ SAtom { Token = Symbol ifaceName } ] -> clrConstraint <- Some(ifaceName, [])
                | _ ->
                    failwithf
                        $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos cr}: #:clr-constraint takes one fully qualified .NET interface, applied to the arguments it is implemented at, as in (#:clr-constraint (System.Numerics.INumber %%%s{implementorVar}))."

            // Match: (type 'item)
            | SList (SAtom { Token = Symbol "type" } :: SAtom { Token = QuotedSymbol assocName } :: [], _) ->
                assocTypes <- assocName :: assocTypes

            // Match: (: methodName signatureExpr #:clr-member Abs)
            //
            // Which member of the interface this method is. Only meaningful on
            // a trait that stands for one, and checked against the interface at
            // inference; here it is only read.
            | SList (SAtom { Token = Colon } :: SAtom { Token = Symbol methodName } :: typeExpr
                     :: SAtom { Token = Keyword "clr-member" } :: SAtom { Token = Symbol memberName } :: [], _) ->
                signatures <- (methodName, parseType typeExpr) :: signatures
                clrMembers <- (methodName, memberName) :: clrMembers

            // Match: (: methodName signatureExpr)
            | SList (SAtom { Token = Colon } :: SAtom { Token = Symbol methodName } :: typeExpr :: [], _) ->
                signatures <- (methodName, parseType typeExpr) :: signatures

            // Match: (defun (methodName args...) body) — a default body, used by
            // any impl that does not write this method itself. The signature is
            // still declared separately: a default supplies the *body*, and the
            // type it is checked at comes from the impl, not from here.
            | SList (SAtom { Token = Symbol "defun" } :: _, _) as defunExpr ->
                defaults <- parseDecl defunExpr :: defaults

            // A suspending default body, for a method the trait declares
            // `-bjo->`. A default is spliced into each impl as source and
            // checked there, so its definer meets the trait's arrow by the same
            // route a hand-written method's does, and needs no check here.
            | SList (SAtom { Token = Symbol "defbjo" } :: _, _) as defbjoExpr ->
                defaults <- parseDecl defbjoExpr :: defaults

            // A macro, which is how a `derive`-style transformer writes a
            // default body. It goes through `parseDeclForms` so that it gets
            // resolution and the bound set on the same terms a top-level splice
            // does; what comes back has to be method bodies, since a trait has
            // nowhere to put anything else.
            | SList(SAtom { Token = Symbol h } :: _, mr) when isMacroName h ->
                for d in parseDeclForms item do
                    match d with
                    | DDefun _ -> defaults <- d :: defaults
                    | other ->
                        failwithf
                            $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos mr}: the macro '%s{h}' produced %s{declKindName other}, and a trait body holds (type ...), (: ...) and (defun ...) — a default method body is the only one of those a macro can write."

            | _ ->
                failwithf
                    $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos (getRange item)}: Expected (type ...), (: ...), (defun ...), (#:clr-constraint ...), a (begin ...) of those, or a macro producing default method bodies."

        let clrSpec =
            clrConstraint
            |> Option.map (fun (ifaceName, ifaceArgs) -> ifaceName, ifaceArgs, List.rev clrMembers)

        Some(DTrait(traitName, implementorVar, holeArity, List.rev assocTypes, List.rev signatures, List.rev defaults, clrSpec, r))

    // Parse: (impl (TraitName (Vec 'a)) (type 'item 'a) (defun (get v i) ...))
    //
    // The trait name is stripped of a rename, as every dispatched-on head is: a
    // template that writes `(impl (Eq ,name) ...)` — which is what a
    // `derive` macro does — constructs the trait name and so has it renamed,
    // and a trait is not a binding for the mark to be resolving.
    | SList (SAtom { Token = Symbol "impl" } ::
             SList (StrippedSymbol traitName :: targetTypeExpr :: [], _) ::
             body, r) ->

        let targetType = parseType targetTypeExpr

        let mutable assocBindings = []
        let mutable constraints = []
        let mutable methods = []

        for item in flattenBegins body do
            // The clause tags — `type`, `where`, `defun` — are dispatched on
            // exactly as a declaration's head is, and a constructed one arrives
            // renamed. The *method name* inside a `defun` is stripped with them:
            // it has to match what the `def/trait` declared, so it is a
            // selector rather than a binding.
            match stripMethodName (stripHeadMark item) with
            // Match: (type 'item targetType)
            | SList (SAtom { Token = Symbol "type" } :: SAtom { Token = QuotedSymbol assocName } :: boundTypeExpr :: [], _) ->
                assocBindings <- (assocName, parseType boundTypeExpr) :: assocBindings

            // Match: (where (TraitName %var) ...) — a conditional impl. The
            // clause is read exactly as a signature's, so the two spellings
            // cannot drift.
            | SList (SAtom { Token = Symbol "where" } :: constraintExprs, wr) ->
                for c in constraintExprs do
                    match c with
                    | SList ([ StrippedSymbol cTrait; SAtom { Token = QuotedSymbol varName } ], _) ->
                        constraints <- (cTrait, "'" + varName) :: constraints
                    | SList ([ StrippedSymbol cTrait; SAtom { Token = Symbol varName } ], _) ->
                        constraints <- (cTrait, varName) :: constraints
                    | _ ->
                        failwithf
                            $"Syntax error in impl for '%s{traitName}' at %s{Lexer.formatPos wr}: a where clause holds (TraitName %%var) constraints, and the variable must be one the impl's own target names."

            // Match: (defun ...)
            | SList (SAtom { Token = Symbol "defun" } :: _, _) as defunExpr ->
                methods <- parseDecl defunExpr :: methods

            // A method that suspends, which the trait's signature has to have
            // declared `-bjo->`. The definer is checked against that arrow in
            // `DImpl` rather than here: this is the only place an impl method
            // names a colour, and the trait it belongs to is not in scope until
            // inference.
            | SList (SAtom { Token = Symbol "defbjo" } :: _, _) as defbjoExpr ->
                methods <- parseDecl defbjoExpr :: methods

            // The `derive` case: one macro call standing for the methods of a
            // whole implementation. See the matching arm in `def/trait`.
            | SList(SAtom { Token = Symbol h } :: _, mr) when isMacroName h ->
                for d in parseDeclForms item do
                    match d with
                    | DDefun _ -> methods <- d :: methods
                    | other ->
                        failwithf
                            $"Syntax error in impl for '%s{traitName}' at %s{Lexer.formatPos mr}: the macro '%s{h}' produced %s{declKindName other}, and an implementation holds methods."

            | _ ->
                failwithf
                    $"Syntax error in impl for '%s{traitName}' at %s{Lexer.formatPos (getRange item)}: Expected (type ...), (where ...), (defun ...), a (begin ...) of those, or a macro producing methods."

        Some(DImpl(traitName, targetType, List.rev assocBindings, List.rev constraints, List.rev methods, r))

    // Parse: (impl/extern (Foldable (Vec 'a)) (type 'item 'a))
    //
    // The bodyless counterpart of `impl`, emitted into a library's export
    // metadata so that whoever imports it can resolve the trait's associated
    // types and dispatch to the impl class compiled into that assembly.
    | SList (SAtom { Token = Symbol "impl/extern" } ::
             SList (SAtom { Token = Symbol traitName } :: targetTypeExpr :: [], _) ::
             body, r) ->

        let assocBindings =
            body
            |> List.choose (function
                | SList (SAtom { Token = Symbol "type" } :: SAtom { Token = QuotedSymbol assocName } :: boundTypeExpr :: [], _) ->
                    Some(assocName, parseType boundTypeExpr)
                | SList (SAtom { Token = Symbol "where" } :: _, _) -> None
                | _ -> failwithf $"Syntax error in impl/extern for '%s{traitName}': Expected (type ...) or (where ...).")

        // A conditional impl's `(where ...)` has to cross the module boundary
        // with it: the importing side is where the dictionary for
        // `(->str (List int))` is built, and it cannot build one without knowing
        // that a `(->str int)` goes inside.
        let constraints =
            body
            |> List.collect (function
                | SList (SAtom { Token = Symbol "where" } :: constraintExprs, _) ->
                    constraintExprs
                    |> List.choose (function
                        | SList ([ SAtom { Token = Symbol cTrait }; SAtom { Token = QuotedSymbol varName } ], _) ->
                            Some(cTrait, "'" + varName)
                        | SList ([ SAtom { Token = Symbol cTrait }; SAtom { Token = Symbol varName } ], _) ->
                            Some(cTrait, varName)
                        | _ -> None)
                | _ -> [])

        Some(DImplExtern(traitName, parseType targetTypeExpr, assocBindings, constraints, r))

    // Not a declaration form. It may still be a macro call, which is
    // `parseDeclForms`' business — reached only once every form above has
    // failed to match, so a special form always wins over a macro of the same
    // name.
    | _ -> None

/// One declaration, where there has to be exactly one.
///
/// `Pipeline.importsOf` is the caller this exists for: it reads an `(import
/// ...)` form before anything is expanded, wants the single declaration back,
/// and can never be looking at a macro.
and parseDecl (s: SExpr) : Decl =
    match tryParseDecl s with
    | Some d -> d
    | None -> failwithf $"Unknown declaration at %s{Lexer.formatPos (getRange s)}"

/// Every declaration one form makes, or `None` when nothing matched it.
///
/// `tryParseDecl` answers with one because most callers want one. A `defun`
/// that carries its own parameter and return types makes two — the signature
/// and the function — and anything deciding what a form *declares* has to see
/// both. `parseDeclForms` is one such caller; the REPL, which asks what an
/// entry defines and whether it still needs a signature written for it, is the
/// other.
and tryParseDeclGroup (s: SExpr) : Decl list option =
    match stripHeadMark s with
    | SList(SAtom { Token = Symbol(("defun" | "defbjo") as definer) } :: SList(SAtom { Token = Symbol name } :: args, _) :: rest, _) ->
        Some(parseDefunDecl definer name args rest (getRange s))

    // `(: name #:sync ...)`, in the slot `#:opaque` uses on a type: a marker on
    // the declaration rather than any part of the shape.
    //
    // The rest is handed back to `tryParseDecl` with the marker taken out, so
    // every signature form there is — with a `(where ...)` and without — keeps
    // working here without being spelled a second time.
    | SList(SAtom { Token = Colon } as colon :: (SAtom { Token = Symbol name } as named) :: SAtom { Token = Keyword "sync" } :: rest, r) ->
        match tryParseDecl (SList(colon :: named :: rest, r)) with
        | Some signature -> Some [ signature; DSyncOnly(name, r) ]
        | None ->
            failwithf
                $"Invalid signature for '%s{name}' at %s{Lexer.formatPos r}. #:sync goes straight after the name, as in (: %s{name} #:sync (-> ...))."

    | _ -> tryParseDecl s |> Option.map List.singleton

/// The declarations one top-level form expands to.
///
/// Three ways there can be more than one. `def/macro` is not a new binding
/// form: it is a signature the compiler writes, an ordinary `defun`, and a note
/// that the name is a macro. `(begin ...)` splices its contents into the
/// enclosing declaration list. And a macro call in declaration position becomes
/// whatever it expanded to, which is what makes the first two worth having —
/// a transformer can now emit a signature beside its `defun`, or a `def` beside
/// the function that reads it.
///
/// `(begin)` with nothing in it splices to nothing, deliberately: a macro has
/// to be able to decide that this call produces no declarations at all. Body
/// position reads an empty `begin` differently, and says why there.
///
/// The signature `def/macro` writes is not the user's to choose. A transformer
/// is invoked by reflection against a signature the expander has to know
/// exactly, and every way of varying it — a type parameter, a trait constraint,
/// a keyword or rest argument, a `defbjo` colour — changes the emitted C# method
/// (`T_` parameters, leading `_dict_*`, `__kw_*`, a `Fiber<T>` return). Fixing
/// it here is what turns those into a syntax error rather than a
/// `TargetParameterCountException` from inside the compiler.
and parseDeclForms (s: SExpr) : Decl list =
    // Before the match, because a `begin` a template wrote arrives as
    // `begin__37` and would otherwise be read as a macro call to something
    // that does not exist.
    let s = stripHeadMark s

    match s with
    | SList(SAtom { Token = Symbol "def/macro" } :: SList(head, _) :: body, r) ->
        let name, argNames =
            match head with
            | SAtom { Token = Symbol name } :: rest ->
                let args =
                    rest
                    |> List.map (function
                        | SAtom { Token = Symbol a } -> a
                        | bad ->
                            failwithf
                                $"Invalid def/macro parameter at %s{Lexer.formatPos (getRange bad)}. A transformer takes exactly three plain parameters: the form, inject and compare.")

                name, args
            | _ ->
                failwithf
                    $"Invalid def/macro at %s{Lexer.formatPos r}. Expected (def/macro (name form inject compare) body...)"

        if argNames.Length <> 3 then
            failwithf
                $"Invalid def/macro '%s{name}' at %s{Lexer.formatPos r}: a transformer takes exactly three parameters — the form, inject and compare — and this one takes %d{argNames.Length}."

        if body.IsEmpty then
            failwithf $"Invalid def/macro '%s{name}' at %s{Lexer.formatPos r}: it has no body."

        [ DSignature(name, macroTransformerType r, [], r)
          DDefun(name, argNames |> List.map (fun n -> MandatoryArg(n, None)), parseBody body r, Ordinary, r)
          DMacro(name, r) ]

    | SList(SAtom { Token = Symbol "def/macro" } :: _, r) ->
        failwithf
            $"Invalid def/macro at %s{Lexer.formatPos r}. Expected (def/macro (name form inject compare) body...)"

    // Each step removes one wrapper, so the form count strictly decreases and
    // arbitrary nesting flattens.
    | SList(SAtom { Token = Symbol "begin" } :: inner, _) -> List.collect parseDeclForms inner

    | other ->
        match tryParseDeclGroup other with
        | Some decls -> decls
        | None ->
            // A macro in declaration position. Unlike the expression and body
            // positions, this one used to drop `Resolve` entirely — so a
            // transformer calling a helper from its own module emitted the
            // helper's fresh spelling and rules 2 and 3 never ran.
            //
            // Nested expansions resolve inside-out, which is what we want: an
            // inner macro's memo names are gensyms the outer macro's map has
            // never heard of, so the outer `Resolve` leaves them alone. The
            // bound set covers the whole flattened group, including anything an
            // inner expansion contributed to it.
            match expandHook other with
            | Some expansion ->
                let decls = parseDeclForms expansion.Form
                rejectSplicedImports decls (getRange other)
                decls |> List.map (mapDeclExprs (expansion.Resolve(boundNames decls)))
            | None -> failwithf $"Unknown declaration at %s{Lexer.formatPos (getRange other)}"

let parseModule (exprs: SExpr list) : Decl list = List.collect parseDeclForms exprs
