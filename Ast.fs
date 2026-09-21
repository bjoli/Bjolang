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

module Bjolang.Ast

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

/// What the enclosing cancellation scope does about a fiber that was started
/// inside it.
///
/// A scope waits for its children before it returns, so the first question about
/// any spawn is whether it is one of those children. These four answers are the
/// four surface forms, and the code generator emits a different runtime entry
/// point for each; nothing else about them differs.
type SpawnKind =
    /// `(bjo (f x))`. A child, and the caller gets the promise. It is a value
    /// like any other, so `MustUse` applies: join it, race it, or say `ignore`.
    ///
    /// The scope waits for it but does not report its failure, because the
    /// failure is on the promise the caller is holding and reading it is what
    /// `bjo` is for.
    | SpawnScoped
    /// `(spawn (f x))`. The same child, returning `Unit` because the scope is
    /// the only thing that needs the handle — and therefore the only thing that
    /// can report a failure, so this one's failure is the scope's.
    ///
    /// It exists so that the common case does not have to write
    /// `(ignore (bjo ...))`. Exempting `bjo` from `MustUse` instead was the
    /// alternative, and a second form is cheaper than a hole in a rule that has
    /// no other exceptions.
    | SpawnUnit
    /// `(spawn/daemon (f x))`. Cancelled by the scope, but not waited for: a
    /// heartbeat, a logger, a metrics pump.
    | SpawnDaemon
    /// `(spawn/detached (f x))`. Outside the scope entirely — nothing waits for
    /// it and nothing cancels it. The name is meant to look uncomfortable.
    | SpawnDetached

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
    | PArray of Pattern list * Pattern option * Range // (items, optional tail, range)
    | PTuple of Pattern list * Range
    | PConstruct of string * Pattern list * Range
    /// `(:is System.IO.IOException e)` — matches when the value is of that .NET type, binding it there at the narrowed type.
    /// The binder is optional. Used in `Err` arms.
    | PTypeTest of string * string option * Range
    /// `(:view step p)` — hand the value to `step` and match what comes back
    /// against `p`. The only pattern that runs code.
    ///
    /// The step is a function-valued expression of the scope the `match` sits
    /// in, not of the clause: nothing the pattern binds is visible to it. A
    /// step written with `&` is read as a lambda over the value, so that the
    /// emitter has a body to inline rather than a closure to call.
    | PView of Expr * Pattern * Range
    /// Alternatives, none of which may bind: what `case` builds from a clause's
    /// datum list, and what makes one `switch` section carry several labels.
    | POr of Pattern list * Range
    /// Every one of these has to match the same value. Each may bind, and no
    /// two may bind the same name.
    ///
    /// `(and name p)` is how a value is bound and taken apart at once, and a
    /// conjunction of `(:view ...)`s is how several views are tried against one
    /// value — the tests become one `&&` chain, so a later one is not run when
    /// an earlier one fails.
    | PAnd of Pattern list * Range

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
    /// `(dyn ->str 42)` — packs a value into a trait box, erasing its concrete
    /// type. Trait name is required and packing is always explicit: no implicit
    /// boxing occurs.
    ///
    /// Associated types are NOT specified here; they are inferred from the
    /// implementation selected by the value. If the receiving annotation pins
    /// them differently, a standard type error occurs.
    | EDynPack of string * Expr * Range
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
    /// `#[1 2 3]` — a .NET array, mutable and of a fixed length. The literal
    /// spelling of what `make-array` allocates.
    | EArray of Expr list * Range
    | EMatch of Expr * (Pattern * Expr option * Expr) list * Range
    | ETryFinally of Expr * Expr * Range
    /// `(try body... #:catch (E1 E2 ...))`: run the body, and catch specific .NET exception types.
    | ETryCatch of Expr * string list * Range
    /// `(seq body...)`: a lazy sequence evaluated one `yield` at a time.
    | ESeq of Expr * Range
    /// `(bjo (f x y))` and the three `spawn` forms: start a fiber. Operands are
    /// evaluated in the parent. `SpawnKind` says what the enclosing scope does
    /// about the fiber afterwards, and it is the only difference between them.
    | EBjo of Expr * SpawnKind * Range
    /// `(task->event (fetch url))` — the *event* of making an async .NET call.
    /// The task is started when the event is synced and cancelled if its branch loses.
    | ETaskEvent of Expr * Range
    /// `(yield v)`: hand `v` to the enclosing `seq`'s consumer.
    | EYield of Expr * Range
    /// `(yield-from s)`: hand over every element of `s` in turn.
    | EYieldFrom of Expr * Range
    /// `(with-return ret body ...)` — a named early-exit block.
    ///
    /// The name is required and there is no implicit `return`: `:return`
    /// already means `pure` in `(do ...)` notation, and a second meaning for
    /// that word would be a trap. `ret` is bound in the *ordinary value
    /// namespace*, so shadowing, unbound-name errors and nested blocks all
    /// follow the scope rules that already exist rather than new ones.
    | EWithReturn of string * Expr * Range
    /// One clause of a `(def pattern scrutinee ...)` or `(def* clause ...)`:
    /// the binding pattern, its scrutinee, what the form produces when the
    /// pattern does not match, and **the rest of the body it was written in**.
    ///
    /// The sequel is what makes this a binding form: what the binder binds
    /// scopes over what follows it exactly as a plain `def`'s name does, and
    /// `parseBody` is what hands it that sequel. On a match the sequel is the
    /// value of the form; on a failure the failure part is, and nothing jumps
    /// anywhere — a body that means to leave an enclosing block writes the
    /// `(ret ...)` that leaves it.
    ///
    /// A `def*` is parsed as one of these per clause, nested in source order,
    /// which is what gives it short-circuiting and sequential scope.
    ///
    /// What the binder binds is in scope in the sequel and nowhere else; what
    /// an arm binds is in scope in that arm's body and nowhere else.
    | EDefMatch of Pattern * Expr * DefFailure * Expr * Range

/// What a `def` clause produces when its pattern does not match.
and DefFailure =
    /// Nothing was written, so there is no failure path: the pattern has to
    /// match every value, and `Exhaustiveness` refuses it where it does not.
    | FailNone
    /// `(def pattern scrutinee :propagate)` — every case the pattern leaves out
    /// is rebuilt at the body's type. Which cases those are, and whether the
    /// body can hold them, `InferExpr` decides once the scrutinee has a type.
    | FailPropagate
    /// `(def pattern scrutinee value-expr)` — the third slot is always an
    /// expression. What the binder would have bound is not in scope in it, so
    /// carrying a payload out of the failure takes `:fail`.
    | FailValue of Expr
    /// `(def pattern scrutinee :fail (arm arm ...))`. The arms match the
    /// clause's own scrutinee, so between them they have to cover the
    /// scrutinee's type minus the clause pattern.
    | FailArms of (Pattern * Expr) list

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

/// A trait constraint written in a *member's* `(where ...)` clause inside a
/// `def/trait`: the trait, the constrained variable, and every associated
/// type of that trait pinned by keyword —
/// `(: into! (-> %c %s void) (where (Iterable %s #:elem %added #:cursor %k)))`.
///
/// Distinct from the `(string * string)` pairs a function's or an impl's
/// `where` carries, because only a member's constraint may pin associated
/// types. A method-level constraint becomes method-level type parameters and
/// a dictionary argument at each call site; an impl-level one would have to
/// become class parameters fixed at impl-declaration time, which is why the
/// impl-level restriction stays.
type MemberConstraint =
    { MCTrait: string
      MCVar: string
      MCPins: (string * FType) list
      MCRange: Range }

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
    /// `(def pattern scrutinee)` at the top level, and each clause of a
    /// top-level `def*`.
    ///
    /// The pattern has to match every value of the scrutinee's type, which
    /// `Exhaustiveness` says: a failure part produces the value of the body the
    /// form stands in, and the top level is a declaration list rather than a
    /// body. Every binder becomes a definition of its own, so the scrutinee is
    /// evaluated once and destructured once.
    | DDefPattern of Pattern * Expr * Range
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
    //
    // Each signature carries the member's own `(where ...)` constraints —
    // empty for the ordinary member — see `MemberConstraint`.
    | DTrait of string * string * int * string list * (string * FType * MemberConstraint list) list * Decl list * (string * FType list * (string * string) list) option * Range
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

    /// The same, for a name that heads a *pattern* rather than a form.
    ///
    /// A second declaration rather than a flag on `DMacro` because the two
    /// tables are separate: a name may be an ordinary function in expression
    /// position and a pattern macro in pattern position.
    | DPatternMacro of string * Range

    // `(def/hash-extend (name form inject compare) body...)`, registered as
    // `#name(...)`. The name is the bare one; the transformer `defun` beside it
    // is spelled `#name`, which no source identifier can be.
    | DHashMacro of string * Range

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
    // DImpl (TraitName, TargetType, AssocBindings, WhereClause, MethodWheres, Methods, Range)
    //
    // `MethodWheres` is the impl-side spelling of a member-level `(where ...)`:
    // an impl that writes a constrained member by hand repeats the clause as
    // `(: name (where ...))` beside its `defun`, and is refused if it does not.
    // The trait's own clause stays the authority for what is checked; the
    // repetition is for the reader and for the diagnostic.
    | DImpl of string * FType * (string * FType) list * (string * string) list * (string * MemberConstraint list) list * Decl list * Range

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
    | DDef(_, _, r) | DDefun(_, _, _, _, r) | DDefDouble(_, _, _, _, r) | DDefTuple(_, _, r) | DDefPattern(_, _, r)
    | DDefMutable(_, _, r)
    | DSignature(_, _, _, r) | DType(_, r) | DTypeRec(_, r) | DTrait(_, _, _, _, _, _, _, r) | DImpl(_, _, _, _, _, _, r)
    | DImplExtern(_, _, _, _, r) | DInlineImpl(_, _, _, _, _, _, _, r)
    | DModule(_, _, r) | DImport(_, r) | DAlias(_, _, r) | DExport(_, r) | DReExport(_, r)
    | DExtern(_, _, _, _, r) | DImportAlias(_, _, _, r)
    | DImportExtern(_, r) | DImportClass(_, r) | DMacro(_, r) | DPatternMacro(_, r) | DHashMacro(_, r)
    | DSyncOnly(_, r) -> r

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

/// The parser's entry points, handed to a desugarer that lives below it.
///
/// F# compiles files in order, so `LoopDesugar` cannot call `Parser.parseExpr`
/// by name. `Parser` builds this record inside the `parseExpr` group and passes
/// it down, which is the same knot-breaking `desugarSyntaxQuote` already does
/// with its `parseExprFn` parameter.
type ParseFns =
    { Expr: SExpr -> Expr
      Pattern: SExpr -> Pattern }

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
    | EDynPack(_, _, r)
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
    | EArray(_, r)
    | EMatch(_, _, r)
    | ETryFinally(_, _, r)
    | ETryCatch(_, _, r)
    | ESeq(_, r)
    | EBjo(_, _, r)
    | ETaskEvent(_, r)
    | EYield(_, r)
    | EYieldFrom(_, r)
    | EWithReturn(_, _, r)
    | EDefMatch(_, _, _, _, r) -> r

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
    | PVec(items, tailOpt, _)
    | PArray(items, tailOpt, _) ->
        (items |> List.collect patternBinders)
        @ (tailOpt |> Option.map patternBinders |> Option.defaultValue [])
    | PTuple(items, _) -> items |> List.collect patternBinders
    | PConstruct(_, args, _) -> args |> List.collect patternBinders
    | POr(alts, _) -> alts |> List.collect patternBinders
    | PAnd(alts, _) -> alts |> List.collect patternBinders
    // A view binds what its inner pattern binds. The step is an expression and
    // binds nothing.
    | PView(_, inner, _) -> patternBinders inner

/// Every view step a pattern holds, outermost first.
///
/// The one place a pattern holds an expression, and the reason every traversal
/// over a `match` clause has two scopes to keep apart: these are evaluated
/// where the `match` is written, and everything else in the pattern is about
/// what the clause binds.
let rec patternSteps (pat: Pattern) : Expr list =
    match pat with
    | PWildcard _
    | PInt _
    | PString _
    | PChar _
    | PBool _
    | PKeyword _
    | PQuotedSymbol _
    | PIdent _
    | PTypeTest _ -> []
    | PList(items, tailOpt, _)
    | PVec(items, tailOpt, _)
    | PArray(items, tailOpt, _) ->
        (items |> List.collect patternSteps)
        @ (tailOpt |> Option.map patternSteps |> Option.defaultValue [])
    | PTuple(items, _) -> items |> List.collect patternSteps
    | PConstruct(_, args, _) -> args |> List.collect patternSteps
    | POr(alts, _) -> alts |> List.collect patternSteps
    | PAnd(alts, _) -> alts |> List.collect patternSteps
    | PView(step, inner, _) -> step :: patternSteps inner

/// `f` applied to every view step in a pattern.
let rec mapPatternSteps (f: Expr -> Expr) (pat: Pattern) : Pattern =
    let go = mapPatternSteps f

    match pat with
    | PList(items, tailOpt, r) -> PList(List.map go items, Option.map go tailOpt, r)
    | PVec(items, tailOpt, r) -> PVec(List.map go items, Option.map go tailOpt, r)
    | PArray(items, tailOpt, r) -> PArray(List.map go items, Option.map go tailOpt, r)
    | PTuple(items, r) -> PTuple(List.map go items, r)
    | PConstruct(n, args, r) -> PConstruct(n, List.map go args, r)
    | POr(alts, r) -> POr(List.map go alts, r)
    | PAnd(alts, r) -> PAnd(List.map go alts, r)
    | PView(step, inner, r) -> PView(f step, go inner, r)
    | leaf -> leaf

/// A `def` failure part rebuilt with `onExpr` over its expressions and
/// `onPattern` over its arm patterns.
let mapDefFailure (onExpr: Expr -> Expr) (onPattern: Pattern -> Pattern) (failure: DefFailure) : DefFailure =
    match failure with
    | FailNone -> FailNone
    | FailPropagate -> FailPropagate
    | FailValue value -> FailValue(onExpr value)
    | FailArms arms -> FailArms(arms |> List.map (fun (pat, body) -> onPattern pat, onExpr body))

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
    | EDynPack(_, x, _)
    | EGetField(x, _, _)
    | ESeq(x, _)
    | EBjo(x, _, _)
    | ETaskEvent(x, _)
    | EYield(x, _)
    | EYieldFrom(x, _) -> [ x ]
    | ESet(_, x, _) -> [ x ]
    | ETuple(xs, _)
    | EList(xs, _)
    | EVec(xs, _)
    | EArray(xs, _) -> xs
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
        :: (clauses
            |> List.collect (fun (pat, guard, body) ->
                patternSteps pat @ (Option.toList guard) @ [ body ]))
    | EWithReturn(_, b, _) -> [ b ]
    | EDefMatch(binder, scrutinee, failure, sequel, _) ->
        patternSteps binder
        @ [ scrutinee; sequel ]
        @ (match failure with
           | FailNone
           | FailPropagate -> []
           | FailValue value -> [ value ]
           | FailArms arms -> arms |> List.collect (fun (pat, body) -> patternSteps pat @ [ body ]))

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
        | EVec(items, _)
        | EArray(items, _) -> List.iter sub items
        | EApp(target, args, _) ->
            sub target
            List.iter sub args
        | ECast(_, v, _)
        | EDynPack(_, v, _) -> sub v

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
                // A view's step is read in the enclosing scope: what the clause
                // binds is not in scope in the expression that decides whether
                // the clause matches at all.
                for step in patternSteps pat do
                    sub step

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
        | EBjo(body, _, _) -> go true bound body
        | ETaskEvent(body, _) -> go true bound body
        | EYield(v, _)
        | EYieldFrom(v, _) -> sub v

        // `ret` is an ordinary binding over the body, which is what makes
        // shadowing, unbound-name reporting and nested blocks follow the rules
        // that already exist.
        | EWithReturn(name, body, _) -> go guarded (Set.add name bound) body

        | EDefMatch(binder, scrutinee, failure, sequel, r) ->
            // The scrutinee and a pattern's view steps are read in the scope the
            // form began in, as they are in `EMatch` above.
            for step in patternSteps binder do
                go guarded bound step

            go guarded bound scrutinee

            // What the binder binds is in scope in the sequel and nowhere else.
            go guarded (Set.union bound (Set.ofList (patternBinders binder))) sequel

            // The failure part runs because the binder did not match, so
            // nothing the binder would have bound is in scope in it — only what
            // an arm's own pattern binds, and only in that arm.
            match failure with
            | FailNone
            | FailPropagate -> ()
            | FailValue value -> go guarded bound value
            | FailArms arms ->
                for (armPattern, armBody) in arms do
                    for step in patternSteps armPattern do
                        go guarded bound step

                    go guarded (Set.union bound (Set.ofList (patternBinders armPattern))) armBody

    go guarded bound expr

/// Every name `expr` references without binding, given `bound` already in scope.
let freeNames (bound: Set<string>) (expr: Expr) : Set<string> =
    let mutable acc = Set.empty
    freeNamesWith (fun n _ _ -> acc <- Set.add n acc) false bound expr
    acc

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
        | DDefPattern(pattern, _, _) -> patternBinders pattern
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
            (name :: (signatures |> List.map (fun (n, _, _) -> n))) @ Set.toList (boundNames defaults)
        | DImpl(_, _, _, _, _, methods, _) -> Set.toList (boundNames methods)
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
        | DPatternMacro _
        | DHashMacro _
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
    // The pattern holds expressions too: a view's step, read in the scope the
    // form stands in.
    | DDefPattern(pattern, e, r) -> DDefPattern(mapPatternSteps f pattern, f e, r)
    | DDefun(name, args, body, colour, r) -> DDefun(name, List.map mapArg args, f body, colour, r)
    | DDefDouble(name, args, syncBody, bjoBody, r) ->
        DDefDouble(name, List.map mapArg args, f syncBody, f bjoBody, r)
    | DTrait(name, v, arity, assoc, signatures, defaults, clr, r) ->
        DTrait(name, v, arity, assoc, signatures, defaults |> List.map (mapDeclExprs f), clr, r)
    | DImpl(name, target, assoc, constraints, methodWheres, methods, r) ->
        DImpl(name, target, assoc, constraints, methodWheres, methods |> List.map (mapDeclExprs f), r)
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
    | DPatternMacro _
    | DHashMacro _
    | DSyncOnly _
    | DImplExtern _ -> d

