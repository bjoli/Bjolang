module Bjolang.TypedAST

open Bjolang.Lexer
open Bjolang.Parser


// --- MUTABLE HM TYPES (For Inference) ---
[<CustomEquality; CustomComparison>]
type MetaVar = 
    { Id: int; mutable Value: HMType option }
    
    override this.Equals(obj) =
        match obj with
        | :? MetaVar as other -> this.Id = other.Id
        | _ -> false
        
    override this.GetHashCode() = this.Id
    
    interface System.IComparable with
        member this.CompareTo(obj) =
            match obj with
            | :? MetaVar as other -> compare this.Id other.Id
            | _ -> invalidArg "obj" "not a MetaVar"

and HMType =
    | TCon of string * HMType list
    /// Parameters, return type, and whether calling it is a yield point.
    | TFun of HMType list * HMType * Effect
    | TTuple of HMType list
    | TVar of string
    | TMeta of MetaVar
    // TraitName * AssociatedTypeName * Implementor
    | TAssoc of string * string * HMType

/// Whether calling a function can suspend the fiber it runs on.
///
/// Today this is `ESync` everywhere: `defbjo` does not exist yet, so nothing
/// constructs anything else, and `unifyEffect` rejects the cases that cannot
/// arise. The field is here early on purpose. An arrow type is the one thing
/// published into module metadata, and a signature written without an effect
/// slot is a signature that has to be re-read once effects arrive — with every
/// already-compiled `.dll` in the world spelling the old shape. Adding the slot
/// while `ESync` is the only inhabitant costs a wildcard in sixty pattern
/// matches and nothing else.
///
/// The reason it is a property of the *arrow* and not of the function is
/// `map`. `(-> (-> %a %b) (List %a) (List %b))` says nothing about whether the
/// callback suspends, so `map` is emitted as an ordinary C# method whose
/// `Func<A, B>` cannot contain an `await` — which is the higher-order
/// restriction in the design's §3.1. Effect polymorphism lifts that by letting
/// the callback's effect be a variable the arrow quantifies over, and the
/// variable has to live where the callback's type lives.
///
/// See `concurrency-design.md` §3.1 for what `EAsync` and `EEffVar` will mean
/// and what else has to land before either can be constructed.
and Effect =
    /// An ordinary function. Calling it is not a yield point.
    | ESync
    /// A bjoroutine: calling it may suspend. Compiles to a C# async state
    /// machine returning `Fiber<T>` rather than `T`.
    | EAsync
    /// An effect variable, for a function generic over the effect of a callback
    /// it is given. Never constructed yet: solving one needs a mutable binding
    /// cell and a union-find the way `MetaVar` has, and *using* one needs the
    /// effect-monomorphisation pass described in §3.1, because C# cannot be
    /// generic over async-ness and the body must be emitted once per ground
    /// effect.
    | EEffVar of int

/// `(-> ...)` as written in every signature that exists today.
let tfun (args: HMType list) (ret: HMType) : HMType = TFun(args, ret, ESync)

/// How an arrow of this effect is spelled, in diagnostics and in published
/// module metadata alike.
///
/// `ESync` is `->`, which is what makes this step invisible on disk: every
/// signature written today serializes byte-for-byte as it did before, and every
/// already-compiled `.dll` still reads back. An arrow with no effect marker is
/// an `ESync` arrow, so the format is forward compatible in the direction that
/// matters — old metadata into a new compiler.
///
/// The other spellings are reserved rather than in use. When `defbjo` lands,
/// `resolveTypeAnnotation` needs a `TApp("-bjo->", ...)` case to read one back;
/// until then nothing constructs a non-`ESync` arrow, so nothing writes one.
/// The type-level effect a written colour stands for.
let colourEffect (c: Parser.Colour) : Effect =
    match c with
    | Parser.Ordinary -> ESync
    | Parser.Suspending -> EAsync

/// Repaint the outermost arrow of `t`.
///
/// A signature is written `(-> ...)` whatever the definition's colour, so the
/// declared type arrives `ESync` and has to be repainted before it can be
/// unified with the function actually being defined. Only the outermost arrow:
/// a parameter that is itself a function keeps whatever colour *it* was written
/// with, which today is always `ESync`.
///
/// A non-arrow is returned unchanged rather than rejected. A `defbjo` whose
/// signature is not an arrow is already an error, and it is a better one when
/// it comes from the arity check further down than from here.
let recolour (eff: Effect) (t: HMType) : HMType =
    match t with
    | TFun(args, ret, _) -> TFun(args, ret, eff)
    | other -> other

let arrowHead (eff: Effect) : string =
    match eff with
    | ESync -> "->"
    | EAsync -> "-bjo->"
    | EEffVar n -> $"-e%d{n}->"



/// The type variable standing for an associated type projected out of a
/// *generic* implementor, e.g. `%item` of `Foldable %c`.
///
/// C# has nothing to project with, so a function generic in `'c` that dispatches
/// through a `Foldable` dictionary carries the element type as a second type
/// parameter of its own and lets the dictionary argument infer it:
/// `int count<T_c, T_c_item>(Foldable<T_c, T_c_item> dict, T_c c)`.
/// `Lowering` injects the parameter and `Codegen` spells the projection with it,
/// so both have to agree on the name.
let assocTypeVar (implVar: string) (assocName: string) = $"%s{implVar}_%s{assocName}"

module TypeConstants =
    [<Literal>]
    let Int32Name = "System.Int32"
    [<Literal>]
    let StringName = "System.String"
    [<Literal>]
    let BooleanName = "System.Boolean"
    [<Literal>]
    let VoidName = "System.Void"
    [<Literal>]
    let ObjectName = "System.Object"

    [<Literal>]
    let ByteName = "System.Byte"
    [<Literal>]
    let Int16Name = "System.Int16"
    [<Literal>]
    let UInt16Name = "System.UInt16"
    [<Literal>]
    let UInt32Name = "System.UInt32"
    [<Literal>]
    let Int64Name = "System.Int64"
    [<Literal>]
    let UInt64Name = "System.UInt64"
    [<Literal>]
    let DoubleName = "System.Double"
    [<Literal>]
    let KeywordName = "Keyword"
    [<Literal>]
    let SymbolName = "Symbol"
    /// A Unicode scalar value, backed by `Bjolang.Runtime.BjoChar`.
    ///
    /// Deliberately not `System.Char`: a C# char is a 16-bit UTF-16 code unit,
    /// which cannot hold an astral codepoint on its own.
    [<Literal>]
    let CharName = "Char"

    /// Bjolang's unit type — what `void` in a signature actually means.
    ///
    /// Backed by `Bjoml.Unit`, a one-field struct, and deliberately **not** by
    /// `System.Void`, which is not a type in C#'s type system. The difference
    /// only starts to matter once a *generic* `(-> %a %b)` is instantiated at
    /// `%b = unit`: `Action<T>` exists but `Func<T, void>` does not, and nothing
    /// at emission time can choose between the two from a type variable. With a
    /// real struct there is one answer, `Func<T, Bjoml.Unit>`.
    ///
    /// It is Bjoml's rather than a struct of the runtime's own so that it is
    /// already the right type when the concurrency surface lands: `Bjo.Spawn`
    /// over a body with no useful result hands back a `Promise<Bjoml.Unit>`, and
    /// a unit of our own would need converting at exactly that boundary.
    [<Literal>]
    let UnitName = "Unit"

    let intType = TCon(Int32Name, [])
    let stringType = TCon(StringName, [])
    let boolType = TCon(BooleanName, [])
    /// The **interop** void: an expression that yields no C# value at all.
    ///
    /// Reachable only from `mapClrType`, from the statement-shaped forms
    /// (`set!`, `yield`), and from a function whose inferred return type came
    /// from one of those. No Bjolang signature can name it — `void` written in
    /// source is `unitType` — so it never appears in a published type. `unify`
    /// lets it meet `unitType`, which is what makes a `(-> ... void)` function
    /// able to end in a `.Dispose` call.
    let voidType = TCon(VoidName, [])

    let unitType = TCon(UnitName, [])
    let objType = TCon(ObjectName, [])
    let keywordType = TCon(KeywordName, [])
    let symbolType = TCon(SymbolName, [])
    let charType = TCon(CharName, [])
    
    let byteType = TCon(ByteName, [])
    let shortType = TCon(Int16Name, [])
    let ushortType = TCon(UInt16Name, [])
    let uintType = TCon(UInt32Name, [])
    let longType = TCon(Int64Name, [])
    let ulongType = TCon(UInt64Name, [])
    let doubleType = TCon(DoubleName, [])

// ---------------------------------------------------------------------------
// Foreign .NET interop
// ---------------------------------------------------------------------------

/// A .NET class named by `import/class`.
///
/// `Alias` is what Bjolang code writes; `ClrName` is the fully qualified name
/// the code generator emits and the reflection engine resolves. The two are
/// kept apart deliberately: the alias is also registered as a type alias, so a
/// signature may say `StreamWriter` while the emitted C# says
/// `System.IO.StreamWriter`.
type ClrClassInfo =
    { Alias: string
      ClrName: string
      /// The declared constructor signature, if one was written. It is checked
      /// against the overload reflection picks rather than used instead of it.
      CtorType: HMType option
      /// Exception types the *constructor* is declared to raise. Empty means
      /// the constructor is not wrapped and anything it throws propagates.
      ///
      /// Constructor-only is deliberate: `import/class` declares one signature,
      /// the constructor's, so one exception list is all it can honestly
      /// describe. An instance method is never wrapped — nothing has said what
      /// it may raise, and inventing an answer would swallow exceptions the
      /// author never listed.
      CtorExceptions: string list }

/// What an `import/extern` alias names on the far side.
///
/// The alias is applied either way — a property is read by calling its alias
/// and written by calling its setter's — so this decides what the application
/// *emits*, not how it is written.
type ClrExternKind =
    /// A method, chosen from the overload set by the argument types.
    | ExternMethod
    /// `#:get`: a property or field read. There is nothing to overload, so the
    /// arity is fixed and the type is known without any arguments.
    | ExternGet
    /// `#:set`: a property or field write. Always yields void; the value is the
    /// alias's last argument.
    | ExternSet

/// A .NET member bound as a first-class Bjolang function by `import/extern`.
type ClrExternInfo =
    { Alias: string
      /// The fully qualified name of the declaring type, e.g. `System.Console`.
      ClrType: string
      /// The member's own name: a method, or a property or field under
      /// `#:get`/`#:set`.
      MemberName: string
      Kind: ClrExternKind
      /// The member is an instance one, so the alias takes the receiver as its
      /// first argument.
      ///
      /// Reflected at the import rather than written there: a name either
      /// denotes a static member of that type or an instance one, and asking
      /// the metadata is both shorter to write and impossible to get wrong.
      /// Static wins where a type has both, which is also how C# reads
      /// `Type.Member`.
      IsInstance: bool
      /// The declared signature, if one was written, receiver included: an
      /// instance member's alias is a function of its receiver, so
      /// `StreamReader.ReadLine` is written `(-> StreamReader string)`.
      ///
      /// Required in order to use a *method* import as a value: a .NET method
      /// group is not a value, so a first-class use has to be eta-expanded into
      /// a lambda, and that needs the parameter types before there are any
      /// arguments to infer them from. An accessor needs no such thing — a
      /// property has no overloads, so its type is known from the member alone.
      DeclaredType: HMType option
      Exceptions: string list
      /// `#:async`: the .NET method returns a task, so calling it is a yield
      /// point and the Bjolang type of the call is the task's result. `Task`
      /// itself is never a Bjolang type — see concurrency-design.md §7.2.
      IsAsync: bool
      /// `#:uncancellable`: do not thread the ambient cancellation token into
      /// the call. Written at the import because that is where the fact is
      /// decided; discovered at a `choose` instead, it is discovered as leaked
      /// work.
      Uncancellable: bool
      /// `#:cancellable`: thread the ambient token into a call that is not
      /// `#:async`. A `CancellationToken` parameter is not always optional —
      /// `File.ReadLinesAsync` returns a stream rather than a task and takes
      /// one in every overload — so without this such a method is uncallable.
      Cancellable: bool }

/// The overload the type checker selected, carried to the code generator.
///
/// Resolution happens once, during inference, against real .NET metadata —
/// nothing downstream re-derives it and nothing is left for the C# compiler to
/// guess. `Exceptions` being non-empty is what makes the emitted call
/// `try`/`catch`-wrapped into a `Result`.
type DotNetMethodMetadata =
    { DeclaringType: string
      MethodName: string
      ParameterTypes: HMType list
      /// The method's own return type, *before* any `Result` wrapping.
      ///
      /// For an awaited call this is the *awaited* type — what the task
      /// produces, not the task. The emitter never needs to name a `Task<T>`
      /// and the language never has one.
      ReturnType: HMType
      IsStatic: bool
      Exceptions: string list
      /// Emit this call as `(await ....ConfigureAwait(false))`. Set by an
      /// `#:async` import, and the only thing that makes a foreign call a yield
      /// point — `ColourCheck` and the emitter both read it here rather than
      /// looking the import back up.
      Await: bool
      /// Pass the ambient cancellation token as the final argument. The
      /// overload was resolved *with* it, so `ParameterTypes` includes it and
      /// the user's arguments are the prefix.
      AmbientToken: bool }

type DotNetConstructorMetadata =
    { ClrType: string
      ParameterTypes: HMType list
      Exceptions: string list }

type TypedExpr =
    { Type: HMType
      Range: Range
      Node: TExprNode }

and TypedPattern =
    { Type: HMType
      Range: Range
      Node: TPatternNode }

and TPatternNode =
    | TPWildcard
    | TPInt of string
    | TPString of string
    | TPChar of int
    | TPKeyword of string
    | TPSymbol of string
    | TPIdent of string
    | TPList of TypedPattern list * TypedPattern option
    | TPVec of TypedPattern list * TypedPattern option
    | TPTuple of TypedPattern list
    | TPConstruct of string * TypedPattern list
    /// `(:is Clr.Type binder)`. The string is the fully qualified .NET type
    /// name, already resolved and checked against the scrutinee's type; it is
    /// emitted as a C# type pattern.
    | TPTypeTest of string * string option
    | TPApp of TypedExpr * TypedPattern
    | TPAs of TypedPattern * string

and TExprNode =
    | TInt of string
    | TString of string
    | TIdent of string * HMType list
    | TKeyword of string
    | TSymbol of string
    /// A Unicode scalar value, as a codepoint.
    | TChar of int
    //     name     isFun  parameters  value        rest of scope
    | TLet of string * bool * LocalFun * TypedExpr * TypedExpr
    | TLetRec of (string * bool * LocalFun * TypedExpr) list * TypedExpr
    | TLetTuple of string list * TypedExpr * TypedExpr
    | TLambda of string list * TypedExpr
    | TApply of TypedExpr * TypedExpr list * (string * TypedExpr) list
    | TTupleMake of TypedExpr list
    | TListMake of TypedExpr list
    | TVecMake of TypedExpr list
    | TRecordMake of (string * TypedExpr) list
    | TRecordUpdate of string * (string * TypedExpr) list
    | TLetMutable of string * TypedExpr * TypedExpr
    | TSet of string * TypedExpr
    | TIf of TypedExpr * TypedExpr * TypedExpr
    /// A one-armed conditional: `(when cond body)`, or `(unless cond body)`
    /// when the flag is set. Always of type void — the body runs for its
    /// effect and its value is discarded.
    | TWhen of TypedExpr * TypedExpr * bool
    | TTryFinally of TypedExpr * TypedExpr
    /// `(try body #:catch (E1 E2 ...))`. Of type `(Result System.Exception %a)`
    /// where the body is of type `%a` — or of `()` where the body is void,
    /// since C# has no `Result<E, void>`.
    ///
    /// The strings are fully qualified .NET exception type names, checked
    /// during inference and emitted as a C# exception filter. Anything not
    /// listed keeps propagating.
    | TTryCatch of TypedExpr * string list
    /// A lazy sequence, of type `Seq 'a`. Its body is a *function scope*: it
    /// runs when the sequence is enumerated, not where the form appears, so no
    /// tail call inside it belongs to the enclosing function's loop.
    | TSeq of TypedExpr
    /// `(bjo (f x y))`, of type `(Promise %a)` where the call is of type `%a`.
    ///
    /// The node holds the *whole* application, not a pre-split callee and
    /// argument list, because splitting it is a code generation concern: the
    /// operands are bound to locals in the enclosing block and the call is
    /// rebuilt over them inside the spawned lambda. Keeping it whole until then
    /// means every call shape — keyword arguments, a rest parameter, an
    /// operator, a trait method — goes through the one emitter that already
    /// knows about them.
    | TBjo of TypedExpr
    /// `(task->event (fetch url))` — the event of making an async .NET call.
    ///
    /// Deliberately *not* a `TForeignStaticCall`: the whole difference is that
    /// this one is not made here and not awaited. The fields are the receiver
    /// when the import names an instance method, the declaring type, the
    /// method, the arguments the caller wrote — which are evaluated where the
    /// form stands, as `bjo`'s are, and so is the receiver — the payload type
    /// the event carries, and whether the .NET method returns a non-generic
    /// `Task`, which has no result to hand back and so needs a lambda of a
    /// different shape.
    | TTaskEvent of TypedExpr option * string * string * TypedExpr list * HMType * bool
    /// Produce one element of the enclosing `TSeq`. Always void.
    | TYield of TypedExpr
    /// Produce every element of another sequence in turn. Always void.
    | TYieldFrom of TypedExpr
    | TMatch of TypedExpr * TMatchClause list
    | TInterfaceCall of HMType * string * TypedExpr * TypedExpr list
    /// A call to a trait method, with the trait recorded rather than guessed.
    ///
    /// Every downstream pass reads `TraitRef.Resolved` and none of them
    /// re-derives the trait from the method name. Looking the method name up
    /// across all traits silently picks an arbitrary one when two traits share a
    /// method — which stops being hypothetical the moment `Monad.pure` and an
    /// `Applicative.pure` coexist.
    | TTraitCall of TraitRef * TypedExpr list * (string * TypedExpr) list
    | TThrow of TypedExpr
    // Lowered
    | TIsInst of TypedExpr * HMType
    | TIsInstCase of TypedExpr * HMType * string
    | TCast of TypedExpr * HMType
    | TCaseCast of TypedExpr * HMType * string
    | TGetField of TypedExpr * string
    | TTypeEq of TypedExpr * TypedExpr
    /// A `params`-style array. Produced by `LoopLowering` when a `TRecur`
    /// argument vector has to re-pack a rest parameter.
    | TArrayMake of TypedExpr list
    /// A group of loops produced by `LoopLowering`. Every member is a single
    /// strongly-connected component's worth of tail recursion.
    ///
    /// `TLoop (members, None)` *is* an enclosing function's body: the loop is
    /// emitted directly into that function and `Slots` name its parameters.
    ///
    /// `TLoop (members, Some body)` binds the members as local functions that
    /// are in scope in `body`.
    | TLoop of TLoopMember list * TypedExpr option
    /// A jump back to the top of member `index` of the innermost enclosing
    /// `TLoop`, carrying a *complete* argument vector aligned with that
    /// member's `Slots`.
    | TRecur of int * TypedExpr list

    // --- Foreign .NET interop ---
    //
    // All five are resolved against .NET metadata during inference, so each one
    // already knows exactly which member it names. There is no late binding
    // anywhere below this point: the code generator spells out what the type
    // checker chose.

    /// `(.Method target args...)` — an instance method call.
    | TDotMethodCall of TypedExpr * string * TypedExpr list * DotNetMethodMetadata option
    /// `(.-Property target)` — an instance property or field read.
    | TDotPropertyGet of TypedExpr * string * HMType
    /// An instance property or field write, from a `#:set` import: the
    /// receiver, the member's name, and the value. Always void, exactly as
    /// `set!` is.
    | TDotPropertySet of TypedExpr * string * TypedExpr
    /// `(ClassName. args...)` — construction. The string is the *fully
    /// qualified* CLR name, not the alias, so the emitter needs no environment.
    | TNewObject of string * TypedExpr list * DotNetConstructorMetadata option
    /// A static method imported by `import/extern`, as declaring type and name.
    | TForeignStaticCall of string * string * TypedExpr list * DotNetMethodMetadata option
    /// A static field or property read, as `Class.Member` — how an enum value
    /// such as `FileMode.Open` is written.
    | TForeignStaticGet of string * string * HMType
    /// A static property or field write, from a `#:set` import: the declaring
    /// type, the member's name, and the value. Always void.
    | TForeignStaticSet of string * string * TypedExpr

and TLoopMember =
    { LoopName: string
      /// Mutable parameter slots. A `TRecur` argument vector is positionally
      /// aligned with this list.
      Slots: (string * HMType) list
      /// Per-iteration copies of `Slots`, parallel by index. `Body` reads these
      /// rather than the slots so that a closure escaping one iteration cannot
      /// observe the next iteration's values.
      Locals: string list
      RetType: HMType
      Body: TypedExpr }

and TMatchClause =
    { Pattern: TypedPattern
      Guard: TypedExpr option
      Body: TypedExpr }

/// The parameters of a function-shaped local binding — a body-local `defun`, a
/// named `let`, a lowered loop member.
///
/// A local `defun` takes the same argument grammar a top-level one does, so it
/// needs the same three pieces `TDefun` carries. A binding that is not a
/// function has `noParams`.
and LocalFun =
    { /// Every parameter, in the order the value's `TFun` lists their types:
      /// mandatory first, then keyword, then rest.
      Params: string list
      /// Keyword parameters with their defaults, as `TDefun` records them.
      KeywordArgs: (string * HMType * TypedExpr) list
      /// The rest parameter and its *element* type.
      RestArg: (string * HMType) option }

/// Which trait a `TTraitCall` invokes, and — once the solver has said so —
/// which implementation.
///
/// `Holes` are the metavariables standing for the trait's constructor variable,
/// one per occurrence in the method's signature. For a trait whose implementor
/// takes no arguments there is exactly one and it *is* the implementor type.
/// Resolution reads their pruned heads; because they are shared with the
/// surrounding expression, anything that later pins one of them — an argument,
/// an enclosing call, a declared return type — pins the implementation.
and TraitRef =
    { Trait: string
      Method: string
      Holes: HMType list
      mutable Resolved: (string * HMType list) option }

/// How a trait is compiled.
///
/// Derived, not declared: a trait whose implementor is written applied to
/// arguments — `(def/trait (Monad (%m %a)) ...)` — is `InlineTrait`; anything
/// else is `InterfaceTrait`.
type TraitKind =
    /// A C# `interface`, an `Instance` singleton, `_dict_*` parameters for
    /// generic receivers. Unchanged, and nothing about it is removed.
    | InterfaceTrait
    /// No interface, no dictionary, no dynamic dispatch. There is no valid C#
    /// interface for `Monad<M>`, which is exactly why this kind exists.
    | InlineTrait

/// A trait signature that mentions the trait's constructor variable *applied*.
///
/// `HMType` deliberately has no case for this: `m` appears applied to two
/// different arguments in `bind`, and adding it would make the unifier
/// higher-order. `TplType` lives only in the registry and is eliminated by
/// instantiation before inference ever sees a trait method's type.
///
/// Invariant: a `TplType` never reaches `unify`, `generalize` or `Codegen`.
type TplType =
    | TplCon of string * TplType list
    /// The trait's constructor variable, applied to these arguments.
    | TplHole of TplType list
    | TplVar of string
    /// Carries an effect for the same reason `TFun` does: a trait method's
    /// signature is published, and an arrow inside one that lost its effect on
    /// the way through the registry would come back out as `ESync`.
    | TplFun of TplType list * TplType * Effect
    | TplTuple of TplType list

/// What a binding that is not a function carries in place of parameters.
let noParams: LocalFun = { Params = []; KeywordArgs = []; RestArg = None }

/// Only mandatory parameters — a named `let`, a loop member, or a local `defun`
/// that was written without keyword or rest arguments.
let onlyParams (names: string list) : LocalFun =
    { Params = names; KeywordArgs = []; RestArg = None }

/// One "this type implements that trait" demand: on a function's signature, on
/// an impl's `(where ...)`, or on a dictionary parameter derived from either.
type TraitConstraint =
    { TraitName: string
      TargetType: HMType }

/// The target of an `impl`, kept as a pattern rather than a bare head name.
///
/// The trait's constructor variable abstracts over the *trailing* `HoleArity`
/// arguments; everything before them is fixed by the impl and becomes an
/// impl-level type variable.
///
///     (def/impl (Monad (List %a))      ...)   Ctor = "List",   FixedPrefix = []
///     (def/impl (Monad (Result %e %a)) ...)   Ctor = "Result", FixedPrefix = ['e]
type ImplTarget =
    { Ctor: string
      FixedPrefix: HMType list
      HoleArity: int
      /// What the impl demands of its own type variables — the `(where ...)` of
      /// a conditional impl. `(def/impl (->str (List %a)) (where (->str %a)))`
      /// records `->str` at `'a`.
      ///
      /// Every target here is a bare `TVar` naming one of `FixedPrefix`'s
      /// variables, which is what makes evidence construction terminate: the
      /// type a constraint is discharged at is always a proper subterm of the
      /// type the impl was selected for.
      Constraints: TraitConstraint list }

/// The constructor key a **blanket** impl is filed under — one written at a bare
/// type variable, `(def/impl (Discard %a) ...)`, which applies at any type with
/// no impl of its own.
///
/// `*` is deliberately not a legal constructor name, so this cannot collide with
/// a real one in `ImplTargets`, `Implementations` or `InlineMethods` — all three
/// of which are keyed by constructor. It is spelled `Blanket` in the C# class
/// name; see `implClassName`.
[<Literal>]
let BlanketCtor = "*"

type TDecl =
    | TImport of ImportSpec list * Range
    | TExport of string list * Range
    | TReExport of string list * Range
    | TModule of string * TDecl list * Range
    | TDef of string * TypedExpr * HMType * Range
    | TDefTuple of string list * TypedExpr * HMType * Range
    | TDefMutable of string * TypedExpr * HMType * Range
    | TDefun of string * string list * (string * HMType) list * (string * HMType * TypedExpr) list * (string * HMType) option * HMType * Effect * TypedExpr * Range
    //          name     tyArgs          mandatoryArgs           keywordArgs(name,type,default)      restArg(name,elemType)       retType  effect  body       range
    | TType of TypeDef list * Range
    | TTypeRec of TypeDef list * Range
    //         name     implementorVar  kind        holeArity  assocTypes    signatures
    | TTrait of string * string * TraitKind * int * string list * Map<string, HMType> * Range
    //        traitName  kind        holeArity  targetType  assocBindings           dictFields              methods
    | TImpl of string * TraitKind * int * HMType * (string * HMType) list * (string * HMType) list * TDecl list * Range
    | TExtern of string * FType * Range
    /// `(import/extern ...)` and `(import/class ...)`. Both are pure
    /// environment entries: every use site has already been resolved into a
    /// `TForeignStaticCall`, `TNewObject` or `TDotMethodCall`, so neither emits
    /// any C# of its own.
    | TImportExtern of ClrExternInfo list * Range
    | TImportClass of ClrClassInfo list * Range

type FunMeta = {
    MandatoryCount: int
    KeywordParams: (string * HMType) list   // keyword name, type
    RestParam: HMType option                // element type of rest array
}

type Scheme = Scheme of string list * TraitConstraint list * HMType

type Binding = { Scheme: Scheme; IsMutable: bool }

/// The body of one `impl` method, kept for splicing into call sites.
///
/// The body is the **untyped** `Expr`, never the typed one. `HMType` contains
/// mutable `TMeta` cells that are not meaningfully serializable, and
/// re-inferring at the call site is precisely what gives the method a type its
/// trait signature could not express.
type InlineTemplate =
    { Params: string list
      Body: Expr
      /// Free name -> the name to emit for it. Computed where the origin
      /// module's environment is available, applied after inference.
      Qualification: Map<string, string>
      OriginModule: string }

// Metadata resolution callbacks
type TraitInfo =
    { ImplementorVar: string
      AssociatedTypes: string list
      /// Ordinary first-order signatures. Populated for `InterfaceTrait` only,
      /// and left exactly as it was — that path must not regress.
      Signatures: Map<string, HMType>
      Kind: TraitKind
      /// How many arguments the implementor is written applied to. Zero for an
      /// `InterfaceTrait`.
      HoleArity: int
      /// Signature templates. Populated for `InlineTrait` only.
      Templates: Map<string, TplType>
      /// Default method bodies, by method name, as written in the `def/trait`.
      ///
      /// Untyped `DDefun`s, and deliberately never checked here. `def/impl`
      /// splices one in for a method the impl leaves out, and the ordinary
      /// definition-site check then runs it against *that* impl's instantiation
      /// of the signature. So a single default body may mean a different thing
      /// at each implementor — which is the whole point when the body resolves
      /// an overloaded foreign method from its argument types.
      Defaults: Map<string, Decl> }

/// A declared union payload with the union's type arguments put in.
///
/// Shared by the two case-selection members of `TraitRegistry`, which differ
/// only in how they compare a payload once it is concrete: by matching against
/// an inferred type, or by its head constructor against a literal's shape.
///
/// A length mismatch substitutes nothing rather than failing. The payload is
/// then still written in terms of the declaration's own variables, which both
/// callers treat as wildcards — over-reporting a candidate is recoverable,
/// while raising from a speculative lookup is not.
let private withTypeArgs (typeParams: string list) (typeArgs: HMType list) (payload: HMType) : HMType =
    let subst =
        if typeParams.Length = typeArgs.Length then
            List.zip typeParams typeArgs |> Map.ofList
        else
            Map.empty

    let rec go t =
        match t with
        | TVar n ->
            match Map.tryFind n subst with
            | Some concrete -> concrete
            | None -> t
        | TCon(n, args) -> TCon(n, List.map go args)
        | TFun(args, ret, eff) -> TFun(List.map go args, go ret, eff)
        | TTuple args -> TTuple(List.map go args)
        | _ -> t

    go payload

type TraitRegistry =
    { LocalTraits: Set<string>
      LocalTypes: Set<string>
      Traits: Map<string, TraitInfo>
      /// Method name -> owning trait. `infer` recognizes a trait method in
      /// application position through this, instead of searching every trait's
      /// signature map and taking whichever matched first.
      TraitMethods: Map<string, string>
      // Maps (TraitName * TargetTypeIdentifier) -> (GenericTargetType * Map<AssociatedTypeName, HMType>)
      // The GenericTargetType preserves TVars (e.g. TCon("List", [TVar "'a"]))
      // so ResolveAssociatedType can substitute them when given a concrete type.
      Implementations: Map<string * string, HMType * Map<string, HMType>>
      /// The same implementations, as target *patterns*.
      ImplTargets: Map<string * string, ImplTarget>
      /// Blanket impls, by trait: the one that applies when the exact head
      /// constructor has none of its own.
      ///
      /// At most one per trait, which is what keeps this a `Map` and not a list
      /// of patterns needing a specificity order. Two levels — exact head, then
      /// blanket — is all the overlap there is, so the "which is more specific"
      /// question that makes general specialization hard never arises: a
      /// candidate either matches the head exactly or it is the blanket.
      BlanketImpls: Map<string, ImplTarget>
      /// Which module each trait was *declared* in.
      ///
      /// `LocalTraits` cannot answer this. It only ever grows, and a trait read
      /// back from a `.dll`'s metadata arrives as an ordinary `def/trait` — so
      /// by the time a user module is checked, every imported trait is in there
      /// too. That is tolerable for the constructor-headed orphan rule, which
      /// is a backstop; it is not tolerable for blankets, where the whole point
      /// is that only the trait's own module may declare one.
      TraitOrigins: Map<string, string>
      /// Inlineable method bodies, keyed `TraitName * MethodName * Ctor`.
      ///
      /// The constructor is part of the key on purpose: `(TraitName, MethodName)`
      /// alone collides between `Monad for List` and `Monad for Option`, and the
      /// second registration would silently win.
      InlineMethods: Map<string * string * string, InlineTemplate>
      Aliases: Map<string, string list * HMType>
      Records: Map<string, string list * (string * HMType) list>
      /// Every record type declaring a given field name.
      ///
      /// A list rather than a single owner: construction names its type, but
      /// `record-ref` and `record-set` still have to fall back to the field
      /// name when the target's type has not been resolved yet. Keeping only
      /// the last owner made a shared field name silently resolve to whichever
      /// type happened to be declared last.
      RecordFields: Map<string, string list>
      /// Union type name -> (type parameters, cases as (caseName, payload
      /// types, isLiteral)).
      ///
      /// `registerTypeDefs` otherwise registers each case as an ordinary
      /// constructor binding and throws the union structure away. Literal
      /// elaboration needs it back: given an expected type and a payload, it
      /// has to ask which case of that union could carry the payload.
      ///
      /// `isLiteral` records a `#:literal` marker on the case, which
      /// designates it as the injection target when two cases of the same
      /// union carry the same payload type.
      Unions: Map<string, string list * (string * HMType list * bool) list>
      /// Classes brought in by `import/class`, keyed by alias.
      ///
      /// Each one is *also* registered in `Aliases`, so that a signature may
      /// name it. This map is what the expression side needs: which CLR type an
      /// alias stands for, and what its constructor is declared to throw.
      ClrClasses: Map<string, ClrClassInfo>
      /// Static methods brought in by `import/extern`, keyed by the Bjolang
      /// name they were bound to.
      ClrExterns: Map<string, ClrExternInfo>

      /// Head constructors whose values may not be discarded at all — §8.2's
      /// third level. `(ignore x)` on one of these is an error rather than a
      /// permission, because there is no defensible automatic behaviour for a
      /// discarded error and pretending otherwise is the bug.
      NoDiscard: Set<string> }

    member this.IsTraitDefinedLocally(name) = Set.contains name this.LocalTraits
    member this.IsTypeDefinedLocally(name) = Set.contains name this.LocalTypes

    /// Cases of `unionName` carrying exactly one payload field that could hold
    /// `payload`, with the union's type arguments already substituted in.
    ///
    /// This runs *speculatively*, while looking for a constructor to inject
    /// around a literal, so it must never bind a `MetaVar`: the answer is
    /// structural yes/no rather than a unification. `unify` would corrupt the
    /// inference state for the candidates it rejected on the way.
    ///
    /// An unbound metavariable on either side matches anything. That wildcard
    /// is what makes a nested list literal work at all — at expected
    /// `ProcItem` a nested `'(…)` arrives as `TCon("List",[?m])` with `?m`
    /// still unbound, and it has to match a declared payload of
    /// `(List ProcItem)`. The cost is over-reporting: a union carrying both
    /// `ProcSub of (List ProcItem)` and `ProcNums of (List Int)` calls every
    /// nested literal ambiguous, because `(List ?m)` matches both. That is a
    /// deliberate trade — an ambiguity error is honest, and `#:literal` or an
    /// explicit constructor resolves it.
    ///
    /// `#:literal` is applied here rather than by the caller: when more than
    /// one case matches and exactly one is marked, that one wins. The caller's
    /// rule is then simply zero / one / many.
    member this.CandidateCases (unionName: string) (typeArgs: HMType list) (payload: HMType) : (string * HMType list) list =
        // A local dereference, because `prune` lives in `Unification`, which is
        // compiled after this file.
        let rec deref t =
            match t with
            | TMeta { Value = Some inner } -> deref inner
            | _ -> t

        let rec matches pat conc =
            match deref pat, deref conc with
            // An unbound meta is a hole nothing has decided yet, and a leftover
            // rigid variable is a slot that takes anything. Neither is written
            // to here.
            | TMeta _, _
            | _, TMeta _
            | TVar _, _
            | _, TVar _ -> true
            | TCon(n1, a1), TCon(n2, a2) ->
                n1 = n2 && a1.Length = a2.Length && List.forall2 matches a1 a2
            | TFun(a1, r1, e1), TFun(a2, r2, e2) ->
                e1 = e2 && a1.Length = a2.Length && List.forall2 matches a1 a2 && matches r1 r2
            | TTuple a1, TTuple a2 ->
                a1.Length = a2.Length && List.forall2 matches a1 a2
            | p, c -> p = c

        match Map.tryFind unionName this.Unions with
        | None -> []
        | Some (typeParams, cases) ->
            let matching =
                cases
                |> List.choose (fun (caseName, payloadTypes, isLiteral) ->
                    match payloadTypes with
                    | [ single ] ->
                        let substituted = withTypeArgs typeParams typeArgs single

                        if matches substituted payload then
                            Some(caseName, [ substituted ], isLiteral)
                        else
                            None
                    // A nullary case carries nothing, and a multi-field case
                    // cannot be built from one payload. Neither is a candidate.
                    | _ -> None)

            match matching with
            | [ (name, payloads, _) ] -> [ (name, payloads) ]
            | many ->
                match many |> List.filter (fun (_, _, isLiteral) -> isLiteral) with
                | [ (name, payloads, _) ] -> [ (name, payloads) ]
                | _ -> many |> List.map (fun (name, payloads, _) -> (name, payloads))

    /// Cases of `unionName` carrying exactly one payload field whose *head
    /// constructor* is one of `heads`, with the union's type arguments already
    /// substituted in.
    ///
    /// The shape-directed sibling of `CandidateCases`, and the one a quoted
    /// literal is selected by. `CandidateCases` needs the payload's inferred
    /// type, and inferring is exactly what fails for the literals this feature
    /// exists for: a nested `(ls "-l")` allocates one element metavariable and
    /// unifies `Symbol` against `string` before any constructor is consulted.
    /// So the only thing left to choose by is the literal's syntax — `'(...)`
    /// wants a `List` payload, `[...]` a `Vec`, a string a `string` — and the
    /// payload's arguments are not looked at at all. Whatever they are, the
    /// chosen payload is then pushed back down into the literal, and the
    /// elements are checked against it there.
    ///
    /// Ignoring the arguments is what makes `#:literal` matter. Two cases with
    /// the same head — `(ProcSub (List ProcItem))` beside
    /// `(ProcArgs (List string))` — are one question no literal can answer, so
    /// when several match and exactly one is marked, that one wins. Otherwise
    /// all of them are returned and the caller reports the ambiguity.
    member this.CasesByPayloadShape
        (unionName: string)
        (typeArgs: HMType list)
        (heads: string list)
        : (string * HMType) list =
        // As in `CandidateCases`: `prune` lives in `Unification`, which is
        // compiled after this file, and nothing here may bind a `MetaVar`
        // anyway — the lookup is speculative.
        let rec deref t =
            match t with
            | TMeta { Value = Some inner } -> deref inner
            | _ -> t

        let headMatches payload =
            match deref payload with
            | TCon(name, _) -> List.contains name heads
            // A hole nothing has decided yet, and a slot that takes anything.
            // Both match, for the same reason they do in `CandidateCases`.
            | TMeta _
            | TVar _ -> true
            | _ -> false

        match Map.tryFind unionName this.Unions with
        | None -> []
        | Some (typeParams, cases) ->
            let matching =
                cases
                |> List.choose (fun (caseName, payloadTypes, isLiteral) ->
                    match payloadTypes with
                    | [ single ] ->
                        let substituted = withTypeArgs typeParams typeArgs single

                        if headMatches substituted then
                            Some(caseName, substituted, isLiteral)
                        else
                            None
                    | _ -> None)

            match matching with
            | [ (name, payload, _) ] -> [ (name, payload) ]
            | many ->
                match many |> List.filter (fun (_, _, isLiteral) -> isLiteral) with
                | [ (name, payload, _) ] -> [ (name, payload) ]
                | _ -> many |> List.map (fun (name, payload, _) -> (name, payload))

    member this.ResolveAssociatedType (traitName: string) (assocName: string) (implType: HMType) : HMType option =
        // Pattern-match a stored generic type against a concrete type to build
        // a substitution for type variables.
        let rec matchTypes pat conc subst =
            match pat, conc with
            | TVar name, _ -> Some (Map.add name conc subst)
            | TCon(n1, args1), TCon(n2, args2) when n1 = n2 && args1.Length = args2.Length ->
                List.fold2 (fun acc p c -> acc |> Option.bind (fun s -> matchTypes p c s)) (Some subst) args1 args2
            | _ when pat = conc -> Some subst
            | _ -> None

        let rec applySubstLocal subst t =
            match t with
            | TVar name -> match Map.tryFind name subst with Some conc -> conc | None -> t
            | TCon(n, args) -> TCon(n, args |> List.map (applySubstLocal subst))
            | TFun(args, ret, eff) -> TFun(args |> List.map (applySubstLocal subst), applySubstLocal subst ret, eff)
            | TTuple args -> TTuple(args |> List.map (applySubstLocal subst))
            | _ -> t

        let typeKey =
            match implType with
            | TCon(name, _) -> Some name
            | _ -> None

        match typeKey with
        | Some tk ->
            // Exact head first, then the blanket — the same two levels
            // resolution uses. A blanket's stored target is a bare `TVar`, and
            // `matchTypes` already treats one as a wildcard, so the
            // substitution it yields binds the implementor to the concrete type
            // and the association is read off exactly as for a specific impl.
            let entry =
                match Map.tryFind (traitName, tk) this.Implementations with
                | Some e -> Some e
                | None -> Map.tryFind (traitName, BlanketCtor) this.Implementations

            match entry with
            | Some (genericTarget, assocMap) ->
                match matchTypes genericTarget implType Map.empty with
                | Some subst ->
                    Map.tryFind assocName assocMap
                    |> Option.map (applySubstLocal subst)
                | None -> None
            | None -> None
        | None -> None

type Env =
    { Bindings: Map<string, Binding>
      Registry: TraitRegistry
      FunMetas: Map<string, FunMeta>
      /// The module whose declarations are currently being checked.
      ///
      /// An inline template records where its body came from, because its free
      /// variables have to be emitted as references into *that* module's class
      /// rather than resolved wherever the body ends up spliced.
      CurrentModule: string }


let addTrait (name: string) (info: TraitInfo) (env: Env) : Env =
    let newRegistry =
        { env.Registry with
            LocalTraits = Set.add name env.Registry.LocalTraits
            Traits = Map.add name info env.Registry.Traits
            TraitOrigins = Map.add name env.CurrentModule env.Registry.TraitOrigins }

    { env with Registry = newRegistry }

let addImplementation
    (traitName: string)
    (typeKey: string)
    (targetType: HMType)
    (implTarget: ImplTarget)
    (assocBindings: Map<string, HMType>)
    (env: Env)
    : Env =
    let newRegistry =
        { env.Registry with
            Implementations = Map.add (traitName, typeKey) (targetType, assocBindings) env.Registry.Implementations
            ImplTargets = Map.add (traitName, typeKey) implTarget env.Registry.ImplTargets
            // A blanket is registered under the sentinel key like any other
            // impl — so that serialization, associated-type resolution and the
            // inline templates all treat it uniformly — and *additionally* in
            // its own map, which is the one resolution consults after an exact
            // head lookup misses.
            BlanketImpls =
                if typeKey = BlanketCtor then
                    Map.add traitName implTarget env.Registry.BlanketImpls
                else
                    env.Registry.BlanketImpls }

    { env with Registry = newRegistry }

let addInlineTemplate
    (traitName: string)
    (methodName: string)
    (ctor: string)
    (template: InlineTemplate)
    (env: Env)
    : Env =
    { env with
        Registry =
            { env.Registry with
                InlineMethods = Map.add (traitName, methodName, ctor) template env.Registry.InlineMethods } }

/// The C# class an implementation is emitted as.
///
/// The blanket sentinel is not an identifier, so it is spelled out:
/// `Discard_Blanket`, generic in the implementor, which is what makes
/// `Discard_Blanket<int>.Instance` the dictionary for a type that has no impl
/// of its own.
let implClassName (traitName: string) (targetTypeName: string) =
    let flattened =
        if targetTypeName = BlanketCtor then "Blanket"
        else targetTypeName.Replace(".", "_")

    $"%s{traitName}_%s{flattened}"

/// The name a dictionary is passed and stored under: a parameter of a
/// constrained generic function, a field of a conditional impl's class, and the
/// reference to either from a method body. One function so that the three
/// cannot drift apart.
let dictParamName (traitName: string) (typeVarName: string) =
    $"_dict_%s{traitName}_%s{typeVarName}"

/// The dictionary singleton of an implementation: `Foldable_Vec::Instance`.
///
/// `Codegen` inserts the class's type arguments *before* the `::`, so this names
/// a class and its static field rather than a member path — `Foldable_Vec<int>`
/// followed by `.Instance`.
let implSingletonName (traitName: string) (targetTypeName: string) =
    $"%s{implClassName traitName targetTypeName}::Instance"

/// The factory of a *conditional* implementation: `ToStr_List::Make`.
///
/// A conditional impl holds the dictionaries its `(where ...)` demands, so it
/// has no value that exists before they do and therefore no `Instance`. The
/// factory is a static method rather than a bare constructor so that the one
/// name carries the class's type arguments through the `::` spelling every
/// other landing pad already uses — `Lowering` never has to spell C#.
let implFactoryName (traitName: string) (targetTypeName: string) =
    $"%s{implClassName traitName targetTypeName}::Make"

/// The landing pad for an interface-trait method: the impl class's methods are
/// instance methods, so the call goes through the singleton. `Codegen` rewrites
/// `::` to `.`.
let implInstanceMethodName (traitName: string) (targetTypeName: string) (methodName: string) =
    $"%s{implClassName traitName targetTypeName}.Instance::%s{methodName}"

/// The landing pad for an inline-trait method. There is no interface and no
/// singleton to route through, so the method is `static` and the class names it
/// directly.
///
/// It is emitted unconditionally for every impl method rather than only where
/// something can be proven to need it. It costs one static method, and it is
/// what makes the recursion guard, the occurrence-check fallback, and use of a
/// trait method at a resolved type all work.
let implStaticMethodName (traitName: string) (targetTypeName: string) (methodName: string) =
    $"%s{implClassName traitName targetTypeName}::%s{methodName}"

/// The landing pad for a method of `kind`.
let landingPadName (kind: TraitKind) (traitName: string) (targetTypeName: string) (methodName: string) =
    match kind with
    | InterfaceTrait -> implInstanceMethodName traitName targetTypeName methodName
    | InlineTrait -> implStaticMethodName traitName targetTypeName methodName

/// `f` applied to every declaration in the program, nested ones included.
///
/// A module's declarations and an impl's methods are declarations like any
/// other, so anything that asks a question of "every `TDefun`" or "every
/// `TType`" has to descend into both. `f` sees the `TModule` and `TImpl` nodes
/// too, which is what lets a caller that needs the enclosing module's name
/// answer at that level instead.
let rec collectDecls (f: TDecl -> 'a list) (decls: TDecl list) : 'a list =
    decls
    |> List.collect (fun d ->
        f d
        @ match d with
          | TModule(_, inner, _) -> collectDecls f inner
          | TImpl(_, _, _, _, _, _, methods, _) -> collectDecls f methods
          | _ -> [])

/// Structural traversal of a type, with `leaf` deciding what a `TVar` or a
/// `TMeta` contributes.
///
/// Only the compound cases are shared. What a metavariable *means* differs by
/// caller — some prune it through the trait registry first, some follow
/// `Value` by hand, some ignore it — and that decision stays with `leaf`.
let rec foldType (leaf: HMType -> 'a list) (t: HMType) : 'a list =
    match t with
    | TVar _
    | TMeta _ -> leaf t
    | TCon(_, args) -> List.collect (foldType leaf) args
    | TFun(args, ret, _) -> (List.collect (foldType leaf) args) @ foldType leaf ret
    | TTuple args -> List.collect (foldType leaf) args
    | TAssoc(_, _, impl) -> foldType leaf impl

/// Every type variable a type mentions, in order of first appearance.
let typeVarsOf (t: HMType) : string list =
    let rec leaf t =
        match t with
        | TVar n -> [ n ]
        | TMeta { Value = Some inner } -> foldType leaf inner
        | _ -> []

    foldType leaf t |> List.distinct

let rec substTypeVars (subst: Map<string, HMType>) (t: HMType) : HMType =
    match t with
    | TVar n ->
        match Map.tryFind n subst with
        | Some t' -> t'
        | None -> t
    | TCon(n, args) -> TCon(n, List.map (substTypeVars subst) args)
    | TFun(args, ret, eff) -> TFun(List.map (substTypeVars subst) args, substTypeVars subst ret, eff)
    | TTuple args -> TTuple(List.map (substTypeVars subst) args)
    | TAssoc(tn, an, impl) -> TAssoc(tn, an, substTypeVars subst impl)
    | other -> other

/// Turns a trait signature template into an ordinary `HMType` at one impl.
///
/// `TplHole args` becomes `TCon(Ctor, FixedPrefix @ args)`, which is what makes
/// the whole thing first-order again: after this there is no constructor
/// variable left anywhere, and the result may be handed to `unify` and
/// `generalize` like any other type.
let instantiateTemplate (target: ImplTarget) (tpl: TplType) : HMType =
    let rec go t =
        match t with
        | TplCon(n, args) -> TCon(n, List.map go args)
        | TplVar n -> TVar n
        | TplFun(args, ret, eff) -> TFun(List.map go args, go ret, eff)
        | TplTuple args -> TTuple(List.map go args)
        | TplHole args -> TCon(target.Ctor, target.FixedPrefix @ List.map go args)

    go tpl

/// Every type variable a template mentions, in order of first appearance.
let templateVarsOf (tpl: TplType) : string list =
    let rec go t =
        match t with
        | TplVar n -> [ n ]
        | TplCon(_, args)
        | TplHole args
        | TplTuple args -> List.collect go args
        | TplFun(args, ret, _) -> (List.collect go args) @ go ret

    go tpl |> List.distinct

