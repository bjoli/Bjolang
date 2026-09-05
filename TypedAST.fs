module Bjolang.TypedAST

open Bjolang.Lexer
open Bjolang.Parser


// --- MUTABLE HM TYPES (For Inference) ---
/// A metavariable with its `let`-level — Rémy's rank technique.
///
/// `Level` is how deeply nested in `let`-bindings the cell was created. A
/// binding generalized at level L gets to quantify exactly those cells whose
/// level is strictly greater than L: they cannot be reached from the environment, since everything in
/// the environment was created at level L or lower. Unification keeps this true
/// by lowering the level of everything that is bound into a cell with a lower level.
///
/// `Level` is not part of equality or ordering: it is mutable and says
/// nothing about which cell it is.
[<CustomEquality; CustomComparison>]
type MetaVar = 
    { Id: int; mutable Value: HMType option; mutable Level: int }
    
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
/// It is a property of the *arrow* rather than of the function, and the reason
/// is `map`. `(-> (-> %a %b) (List %a) (List %b))` says nothing about whether
/// the callback suspends, so `map` is emitted as an ordinary C# method whose
/// `Func<A, B>` cannot contain an `await`. Effect polymorphism lifts that by
/// letting the callback's effect be a variable the arrow quantifies over, and
/// the variable has to live where the callback's type lives.
and Effect =
    /// An ordinary function. Calling it is not a yield point.
    | ESync
    /// A bjoroutine: calling it may suspend. Compiles to a C# async state
    /// machine returning `Fiber<T>` rather than `T`.
    | EAsync
    /// `-?->`: the signature's one anonymous effect variable.
    ///
    /// One per signature, shared by every occurrence in it, and freshened into
    /// an `EMeta` at each use exactly the way `instantiate` freshens a `TVar`
    /// into a `TMeta`. One rather than one-per-arrow on purpose: it gives a
    /// signature exactly two instantiations however many function parameters it
    /// has, so there is no 2^k of emitted copies and no mixed instantiation to
    /// have a rule about.
    ///
    /// What it costs is expressiveness nobody has wanted yet:
    /// `(-?-> (-?-> A B) (-?-> B C) A C)` makes all three agree, and "the first
    /// may suspend, the second may not" needs named variables — which is what
    /// `EEffVar` is reserved for.
    | EPoly
    /// A named effect variable, `-e0->`. Reserved, still never constructed:
    /// it is what `EPoly` becomes when one variable per signature stops being
    /// enough.
    | EEffVar of int
    /// Inference-internal, and never serialized: what an `EPoly` becomes when a
    /// signature is instantiated, and what gets solved.
    | EMeta of EffectCell

/// A solved-or-not effect, with the same shape `MetaVar` has and for the same
/// reason: unification needs somewhere to write the answer.
///
/// Equality is by id rather than structural, because the field is mutable —
/// and because `Effect` is compared with `=` in a dozen places that mean "is
/// this the same variable", not "do these two hold equal contents".
and [<CustomEquality; NoComparison>] EffectCell =
    { EId: int
      mutable EValue: Effect option }

    override this.Equals(o) =
        match o with
        | :? EffectCell as other -> this.EId = other.EId
        | _ -> false

    override this.GetHashCode() = this.EId

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

/// Follow a solved effect cell to whatever it stands for.
///
/// Path-compressing, like `prune`: a chain of cells is collapsed as it is
/// walked, so the same question asked twice is answered from one hop the second
/// time.
let rec pruneEffect (eff: Effect) : Effect =
    match eff with
    | EMeta cell ->
        match cell.EValue with
        | Some inner ->
            let resolved = pruneEffect inner
            cell.EValue <- Some resolved
            resolved
        | None -> eff
    | _ -> eff

/// The colour an arrow is actually emitted at.
///
/// The real defaulting — an unbound cell taking the colour of the member it is
/// written in — is `EffectGraph.ground`, which has the context this does not:
/// what runs here is the same rule with nothing left to read, so it can only
/// answer `ESync`.
///
/// Which is the right answer for what reaches it. Anything that pass grounded is
/// already solved by the time this is asked, so what is left is a cell in a type
/// no expression carries — a signature printed for a diagnostic, a delegate
/// spelled for a member nobody calls. Ordinary is what those are.
///
/// Familiar shape regardless: it is the same move as defaulting an
/// unconstrained numeric type variable.
let groundEffect (eff: Effect) : Effect =
    match pruneEffect eff with
    | EMeta _
    | EPoly -> ESync
    | other -> other

/// Does calling a value of this type compile to an `await`?
///
/// The effect is pruned rather than matched literally, because by the time
/// anyone asks, a colour can be sitting in a *solved cell* rather than written
/// on the arrow: an `-?->` instantiated at a call site, or a two-copy name
/// whose reference was grounded to the colour of the member it appears in.
/// Reading such an arrow as ordinary because it is not spelled `EAsync` emits a
/// call to a `Fiber<T>`-returning delegate with no `await` on it, which Roslyn
/// then rejects in a file nobody wrote.
let callSuspends (t: HMType) : bool =
    match t with
    | TFun(_, _, eff) -> pruneEffect eff = EAsync
    | _ -> false

/// Does this signature accept a callback of either colour?
///
/// Parameters only. `-?->` is refused everywhere else, so there is no deeper
/// occurrence to look for.
let declaresPolyParam (t: HMType) : bool =
    match t with
    | TFun(args, _, _) ->
        args
        |> List.exists (function
            | TFun(_, _, EPoly) -> true
            | _ -> false)
    | _ -> false

/// Was this call's `-?->` parameter instantiated at the suspending colour?
///
/// `EMeta` is the whole test, and it is exact: a declared parameter's arrow
/// gets a cell in two places, `instantiate`'s `EPoly` case and the template
/// instantiator's, so a cell in one *is* an `-?->` instantiated at this call. A
/// parameter declared `-bjo->` arrives as a plain `EAsync` and must not be
/// mistaken for one — it was never polymorphic and has no second copy to
/// choose.
///
/// One cell is shared by every `-?->` in a signature, so any one occurrence
/// answers for all of them and `exists` is not an approximation.
let wantsSuspendingCopy (t: HMType) : bool =
    match t with
    | TFun(paramTypes, _, _) ->
        paramTypes
        |> List.exists (function
            | TFun(_, _, (EMeta _ as eff)) -> pruneEffect eff = EAsync
            | _ -> false)
    | _ -> false

let private fixPoly (eff: Effect) (p: HMType) =
    match p with
    | TFun(args, ret, EPoly) -> TFun(args, ret, eff)
    | other -> other

/// The suspending twin of a method that takes a callback of either colour: the
/// callback fixed suspending, and the method's own arrow with it.
///
/// `Inference.suspendingSignature` is the same repainting one step earlier, on
/// the written `FType`. Both exist because a trait method has no `FType` left
/// by the time its twin is derived — the signature was resolved when the trait
/// was checked — and because there the outer arrow is the definer's to set.
let suspendingTwin (t: HMType) : HMType =
    match t with
    | TFun(args, ret, _) -> TFun(List.map (fixPoly EAsync) args, ret, EAsync)
    | other -> other

/// The half that takes the plain callback. Its own arrow is whatever the trait
/// declared: a `-bjo->` method may still be handed an ordinary function.
let ordinaryHalf (t: HMType) : HMType =
    match t with
    | TFun(args, ret, own) -> TFun(List.map (fixPoly ESync) args, ret, own)
    | other -> other

let arrowHead (eff: Effect) : string =
    match pruneEffect eff with
    | ESync -> "->"
    | EAsync -> "-bjo->"
    | EPoly -> "-?->"
    | EEffVar n -> $"-e%d{n}->"
    // An unsolved cell reaching a printer is a signature published before
    // defaulting ran. Spelled as what it will become rather than as a number
    // nobody can act on.
    | EMeta _ -> "-?->"



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

/// A number as it is written, and as C# has to read it back.
///
/// Two questions with one answer, which is why they live together: what type a
/// spelling fixes, and how the same digits are spelled in the generated C#. A
/// suffix nobody translated is a Bjolang program that type-checks and then
/// fails in Roslyn — `21uy` reached C# as `21uy`, which is not a number there.
module NumericLiteral =

    /// A type with its solved metavariables followed, which is as far as a
    /// registry-free caller can get.
    let rec settled (t: HMType) : HMType =
        match t with
        | TMeta { Value = Some inner } -> settled inner
        | _ -> t

    let private isHex (text: string) =
        let bare = text.TrimStart '-'
        bare.StartsWith "0x" || bare.StartsWith "0X" || bare.StartsWith "0b" || bare.StartsWith "0B"

    /// The suffixes Bjolang writes a numeric type with, longest first.
    ///
    /// Longest first because `us` ends with `s`: tried the other way round,
    /// every `ushort` literal reads as a `short` — which is what used to
    /// happen, `ushort` having been unwritable as a result.
    let private suffixes =
        [ "uy", TypeConstants.byteType
          "us", TypeConstants.ushortType
          "UL", TypeConstants.ulongType
          "Ul", TypeConstants.ulongType
          "uL", TypeConstants.ulongType
          "ul", TypeConstants.ulongType
          "u", TypeConstants.uintType
          "U", TypeConstants.uintType
          "s", TypeConstants.shortType
          "L", TypeConstants.longType
          "l", TypeConstants.longType
          "d", TypeConstants.doubleType
          "D", TypeConstants.doubleType ]

    /// A hexadecimal literal's digits are letters, so only the suffixes that
    /// are not also hex digits can be read off one: `0xD` is thirteen and not a
    /// double, and `0x1s` is not a thing anybody writes.
    let private applicable (text: string) =
        if isHex text then
            suffixes |> List.filter (fun (s, _) -> s |> Seq.forall (fun c -> c = 'u' || c = 'U' || c = 'l' || c = 'L'))
        else
            suffixes

    /// The type this spelling fixes, or `None` when it fixes none.
    ///
    /// Integer literals like `1` aren't strictly typed as `int` right away. We leave
    /// their type ambiguous (`None`) so that they can automatically adapt when passed
    /// to functions expecting `byte`, `long`, or `double`. See `Inference.numericLiteralType`.
    let spelledType (text: string) : HMType option =
        // A decimal point or an exponent is a real number however it ends,
        // and it is asked first so that `0.5s` is a malformed double rather
        // than a short with a fraction in it.
        if not (isHex text) && (text.Contains "." || text.Contains "e" || text.Contains "E") then
            Some TypeConstants.doubleType
        else
            applicable text
            |> List.tryFind (fun (s, _) -> text.EndsWith s)
            |> Option.map snd

    /// The literal without whatever suffix Bjolang wrote on it.
    let digits (text: string) : string =
        match applicable text |> List.tryFind (fun (s, _) -> text.EndsWith s) with
        | Some(s, _) -> text.Substring(0, text.Length - s.Length)
        | None -> text

    /// What a literal's digits are worth, or `None` when they do not spell an
    /// integer at all.
    let private value (text: string) : System.Numerics.BigInteger option =
        let negative = text.StartsWith "-"
        let bare = text.TrimStart '-'
        let invariant = System.Globalization.CultureInfo.InvariantCulture

        let parsed =
            if bare.StartsWith "0x" || bare.StartsWith "0X" then
                // A leading zero, because `BigInteger` reads a hexadecimal
                // literal as two's complement: `0xFF` alone is −1.
                match
                    System.Numerics.BigInteger.TryParse(
                        "0" + bare.Substring 2,
                        System.Globalization.NumberStyles.HexNumber,
                        invariant
                    )
                with
                | true, v -> Some v
                | _ -> None
            elif bare.StartsWith "0b" || bare.StartsWith "0B" then
                let bits = bare.Substring 2

                if bits.Length > 0 && bits |> Seq.forall (fun c -> c = '0' || c = '1') then
                    bits
                    |> Seq.fold
                        (fun acc c -> acc * System.Numerics.BigInteger 2 + System.Numerics.BigInteger(int c - int '0'))
                        System.Numerics.BigInteger.Zero
                    |> Some
                else
                    None
            else
                match
                    System.Numerics.BigInteger.TryParse(bare, System.Globalization.NumberStyles.None, invariant)
                with
                | true, v -> Some v
                | _ -> None

        parsed |> Option.map (fun v -> if negative then -v else v)

    let private bounds (t: HMType) =
        let range (lo: System.Numerics.BigInteger) (hi: System.Numerics.BigInteger) = Some(lo, hi)
        let big (n: int64) = System.Numerics.BigInteger n
        let bigu (n: uint64) = System.Numerics.BigInteger n

        match t with
        | TCon(TypeConstants.ByteName, []) -> range (big 0L) (big 255L)
        | TCon(TypeConstants.Int16Name, []) -> range (big -32768L) (big 32767L)
        | TCon(TypeConstants.UInt16Name, []) -> range (big 0L) (big 65535L)
        | TCon(TypeConstants.Int32Name, []) -> range (big (int64 System.Int32.MinValue)) (big (int64 System.Int32.MaxValue))
        | TCon(TypeConstants.UInt32Name, []) -> range (big 0L) (big (int64 System.UInt32.MaxValue))
        | TCon(TypeConstants.Int64Name, []) -> range (big System.Int64.MinValue) (big System.Int64.MaxValue)
        | TCon(TypeConstants.UInt64Name, []) -> range (big 0L) (bigu System.UInt64.MaxValue)
        // A `double` holds any literal anybody writes, give or take precision,
        // and a type variable is answered at run time by `CreateChecked`.
        | _ -> None

    /// Does this literal fit the type it ended up at?
    ///
    /// Asked here because C# asks it too, and answers `CS0221` in generated
    /// code. A literal's value is the one thing about a number that is known
    /// while the program is still Bjolang, so there is no reason for the
    /// question to be put in the other language.
    let fits (t: HMType) (text: string) : bool =
        match bounds (settled t) with
        | None -> true
        | Some(lo, hi) ->
            match value (digits text) with
            | Some v -> v >= lo && v <= hi
            | None -> true

    /// How C# spells these digits at this type, or `None` for a type no numeric
    /// literal can have.
    ///
    /// The casts are parenthesised because a cast binds looser than member
    /// access, and `byte` and the two shorts have no C# suffix to be spelled
    /// with at all.
    let csharp (t: HMType) (text: string) : string option =
        let digits = digits text

        let real =
            not (isHex digits)
            && (digits.Contains "." || digits.Contains "e" || digits.Contains "E")

        // A solved metavariable is followed rather than pruned: emission has no
        // trait registry to hand, and the answer is the same.
        match settled t with
        | TCon(TypeConstants.Int32Name, []) -> Some digits
        | TCon(TypeConstants.Int64Name, []) -> Some(digits + "L")
        | TCon(TypeConstants.UInt32Name, []) -> Some(digits + "u")
        | TCon(TypeConstants.UInt64Name, []) -> Some(digits + "UL")
        | TCon(TypeConstants.ByteName, []) -> Some $"((byte)%s{digits})"
        | TCon(TypeConstants.Int16Name, []) -> Some $"((short)%s{digits})"
        | TCon(TypeConstants.UInt16Name, []) -> Some $"((ushort)%s{digits})"
        | TCon(TypeConstants.DoubleName, []) ->
            if real then Some digits
            elif isHex digits then Some $"((double)%s{digits})"
            else Some(digits + "d")
        | _ -> None

    /// Is this a type a number can have?
    ///
    /// The question `csharp` answers by having a case for it, asked without a
    /// literal to hand — so the types a literal may settle at and the types one
    /// can be emitted at are one list rather than two that could drift.
    let isNumeric (t: HMType) : bool = (csharp t "0").IsSome

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
      /// The alias's type parameters, for a .NET *generic* type.
      ///
      /// Empty for an ordinary class. Non-empty, the alias is registered as a
      /// type constructor of this arity rather than as a type: `(Set %a)` means
      /// `Set.Set<T_a>`, and `Set` written bare is an arity error like any
      /// other.
      TypeParams: string list
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
      /// itself is never a Bjolang type.
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
      Cancellable: bool
      /// `#:blocking`: calling this parks the thread it runs on rather than
      /// suspending the fiber. Changes no emitted code — it is read by the
      /// blocking lint, which reports the ones a bjoroutine can reach.
      IsBlocking: bool
      /// The target is a *generic* method, and these are its type arguments —
      /// one per parameter C# writes between the angle brackets, each a Bjolang
      /// type written in terms of `DeclaredType`'s own variables.
      ///
      /// Solved once, at the import, by matching the method's shape against the
      /// declared signature; `None` for an ordinary member, which is resolved
      /// per call site from its argument types instead. A call then instantiates
      /// the signature and these together, so the metavariables the arguments
      /// unify with are the very ones that end up between the brackets.
      GenericTypeArgs: HMType list option }

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
      AmbientToken: bool
      /// The call parks the thread it runs on, as declared by `#:blocking` on
      /// the import it came through. Emits nothing; it is what the blocking
      /// lint walks the call graph looking for.
      ///
      /// False for a bare `(.Method x)`, which came through no import and so
      /// has nowhere the claim could have been written.
      Blocking: bool
      /// The type arguments a generic method is called at, in order. Empty for
      /// an ordinary one.
      ///
      /// Written out rather than left to C#'s own inference, for the reason the
      /// rest of interop is: the call was *typed* against this instantiation,
      /// and generated code that resolves it a second time has to arrive at the
      /// same answer. It also covers the calls C# could not infer at all — a
      /// nullary `Empty<T>()`, whose argument comes from the context.
      TypeArguments: HMType list }

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
    | TPBool of bool
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
    /// Alternatives, none of which binds. Emitted as several labels on one
    /// `switch` section, which is the shape Roslyn turns into a jump table.
    | TPOr of TypedPattern list

and TExprNode =
    | TInt of string
    | TBool of bool
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
    /// `record-set!` — a write in place to one or more mutable fields of the
    /// record a name is bound to. Always void, exactly as `set!` is.
    | TRecordSet of string * (string * TypedExpr) list
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
    /// A dispatched trait method: the dictionary's type, the method, the
    /// method's type *at this call*, the dictionary, and the arguments.
    ///
    /// The callee's type rides on the node rather than being looked up again,
    /// for the reason `TForeignStaticCall` carries `Await`: by the time
    /// `ColourCheck` and `Codegen` ask whether this call is a yield point, the
    /// trait registry that knew is several passes behind, and the dictionary
    /// type recorded here names the emitted *interface*, which an import alias
    /// may have renamed.
    ///
    /// The whole type and not just the colour, because a `-?->` parameter is
    /// answered by a cell in one of the *arguments* of that arrow rather than
    /// by its head. It is the same thing `TApply` reads off its target, and
    /// this is the one call shape with no target to read it off.
    ///
    /// It is the trait's colour and not the implementation's on purpose. A
    /// dispatched call has to decide whether to await before it knows which
    /// implementation it reached, which is the whole reason a trait's methods
    /// carry one colour between them.
    | TInterfaceCall of HMType * string * HMType * TypedExpr * TypedExpr list
    /// A call to a trait method, with the trait recorded rather than guessed.
    ///
    /// Every downstream pass reads `TraitRef.Resolved` and none of them
    /// re-derives the trait from the method name. Looking the method name up
    /// across all traits silently picks an arbitrary one when two traits share a
    /// method — which stops being hypothetical the moment `Monad.pure` and an
    /// `Applicative.pure` coexist.
    | TTraitCall of TraitRef * TypedExpr list * (string * TypedExpr) list
    /// `(dyn ->str 42)` — a value packed into a trait box: trait name, hole
    /// representing the erased implementor type, and the packed expression.
    ///
    /// The hole is a metavariable shared with the packed expression so that
    /// whatever binds the expression's type binds the implementation
    /// dictionary. Replaced during `Lowering` with a `Make` call.
    | TDynPack of string * HMType * TypedExpr
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
    /// A method of a `#:clr-constraint` trait, at the implementor it was called
    /// on: trait, method, implementor, arguments.
    ///
    /// Emitted as a call to the trait's generated helper —
    /// `Num_Clr.abs<int>(x)` — rather than as `int.Abs(x)` directly, and the
    /// indirection is not free-floating: *some* interface members are
    /// implemented explicitly by *some* primitives, so `int.Abs(x)` compiles
    /// while `int.IsZero(x)` and `byte.Abs(x)` do not. Reached through a
    /// constrained type parameter every member is accessible, which makes one
    /// rule cover every member at every implementor. The JIT inlines the
    /// helper away.
    | TClrMemberCall of string * string * HMType * TypedExpr list
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
      /// The colour this member is emitted in, when the group is emitted as C#
      /// local functions rather than as a `while`.
      ///
      /// A loop member is the one function-shaped thing with no arrow of its
      /// own — its parameters are `Slots` and its result is `RetType`, and the
      /// arrow it came from was dissolved when `LoopLowering` recognised the
      /// recursion. So the colour is recorded here rather than read off a type,
      /// which is the only thing that made this case cost more than a binding's.
      ///
      /// One colour per *group*, not per member: members of a group jump to one
      /// another, so a suspending one makes suspenders of them all, and a merged
      /// loop is a single C# method besides.
      ///
      /// Unread when the group is inlined. A `while` runs in the enclosing
      /// member and takes its colour, which is the case this field does not
      /// describe.
      Effect: Effect
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
      /// The method's type at this call, as instantiation left it.
      ///
      /// What a `-?->` parameter was instantiated to lives here and nowhere
      /// else. Unification binds the cell in *this* type; the argument that
      /// bound it kept its own arrow, so a callee type rebuilt out of the
      /// arguments cannot tell a parameter the trait let choose from one it
      /// declared `-bjo->`.
      MethodType: HMType
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
///     (impl (Monad (List %a))      ...)   Ctor = "List",   FixedPrefix = []
///     (impl (Monad (Result %e %a)) ...)   Ctor = "Result", FixedPrefix = ['e]
type ImplTarget =
    { Ctor: string
      FixedPrefix: HMType list
      HoleArity: int
      /// What the impl demands of its own type variables — the `(where ...)` of
      /// a conditional impl. `(impl (->str (List %a)) (where (->str %a)))`
      /// records `->str` at `'a`.
      ///
      /// Every target here is a bare `TVar` naming one of `FixedPrefix`'s
      /// variables, which is what makes evidence construction terminate: the
      /// type a constraint is discharged at is always a proper subterm of the
      /// type the impl was selected for.
      Constraints: TraitConstraint list }

/// The constructor key a **blanket** impl is filed under — one written at a bare
/// type variable, `(impl (Discard %a) ...)`, which applies at any type with
/// no impl of its own.
///
/// `*` is deliberately not a legal constructor name, so this cannot collide with
/// a real one in `ImplTargets`, `Implementations` or `InlineMethods` — all three
/// of which are keyed by constructor. It is spelled `Blanket` in the C# class
/// name; see `implClassName`.
[<Literal>]
let BlanketCtor = "*"

/// The constructor key a tuple of this arity is filed under.
///
/// `TTuple` has no head constructor, so an implementation for it has nothing to
/// be keyed by and `tryResolveWanted` could never reach one. A synthetic key
/// keeps tuples inside the machinery every other type already uses —
/// `ImplTargets`, `InlineMethods`, the impl class's C# name, the metadata — in
/// place of a second resolution path that every trait after `Eq` would have to
/// duplicate.
///
/// Arity is part of the key because a pair and a triple are different types
/// needing different implementations.
let tupleCtor (arity: int) = $"Tuple%d{arity}"

let isTupleCtor (ctor: string) =
    ctor.StartsWith "Tuple" && ctor.Length > 5 && ctor.Substring 5 |> Seq.forall System.Char.IsDigit

/// The key an implementation target is filed under, or `None` for a target no
/// implementation may be written at.
let implCtorKey (targetType: HMType) : string option =
    match targetType with
    | TCon(name, _) -> Some name
    | TVar _ -> Some BlanketCtor
    | TTuple args -> Some(tupleCtor args.Length)
    | _ -> None

/// A key and its arguments, put back together as the type they came from.
let implTargetType (ctor: string) (args: HMType list) : HMType =
    if isTupleCtor ctor then TTuple args else TCon(ctor, args)

type TDecl =
    | TImport of ImportSpec list * Range
    /// A second spelling of an existing binding or macro. It emits no C# of its
    /// own: the alias resolves to the original, which is what codegen names.
    ///
    /// The resolution is `None` for an alias of a macro, which is a parse-time
    /// name with no binding behind it.
    /// TAlias (VisibleName, Resolution, Range)
    | TAlias of string * ImportAlias option * Range
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
    /// TExtern (VisibleName, Origin, Type, Range). The origin's module is
    /// filled in by then, so it is never `""`.
    | TExtern of string * ImportAlias * FType * Range
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

/// A trait that *is* a .NET interface.
///
/// Such a trait has no implementations and no dictionary: a constraint on it
/// becomes a C# `where` clause, and is discharged by asking the runtime whether
/// the implementor implements the interface. See `Docs/Numerics.org`.
type ClrConstraintInfo =
    { /// The interface, without the arity mark: `System.Numerics.INumber`.
      InterfaceName: string
      /// The arguments as the `def/trait` wrote them, over the trait's own
      /// variables. Kept rather than assumed to be `[implementor]`, because
      /// `IAdditionOperators` takes three and `IComparisonOperators`' last one
      /// is `bool` — not every argument is the implementor.
      Args: HMType list
      /// Trait method name -> the interface member it dispatches to.
      ///
      /// Whether the member is static is *reflected* at the declaration rather
      /// than written: it is a fact the metadata already holds, so asking the
      /// source to restate it only creates a way to be wrong.
      Members: Map<string, ClrMemberBinding> }

/// What a trait method of a CLR-constraint trait compiles to.
and ClrMemberBinding =
    { MemberName: string
      /// `T.Abs(x)` against `x.CompareTo(y)`. The generic-math members are
      /// static abstract; `IComparable`'s one is an ordinary instance method,
      /// and takes its receiver from the method's first argument.
      IsStatic: bool }

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
      /// Untyped `DDefun`s, and deliberately never checked here. `impl`
      /// splices one in for a method the impl leaves out, and the ordinary
      /// definition-site check then runs it against *that* impl's instantiation
      /// of the signature. So a single default body may mean a different thing
      /// at each implementor — which is the whole point when the body resolves
      /// an overloaded foreign method from its argument types.
      Defaults: Map<string, Decl>
      /// The .NET interface this trait stands for, if it stands for one.
      ///
      /// `Some` makes the trait a closed world: there is nothing to implement,
      /// so `impl` is refused, no interface is emitted, and a constraint on
      /// it costs no parameter.
      ClrConstraint: ClrConstraintInfo option

      /// Dynamic safety (boxability) verdict for `(dyn Trait ...)`.
      ///
      /// Derived directly from signatures and never serialized. Evaluated on
      /// trait creation and import.
      DynSafe: Result<unit, string> }

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
      /// Visible name -> what it is really a spelling of.
      ///
      /// Populated by import modifiers, by `(:alias ...)`, and by every plain
      /// imported binding — a plain import is the degenerate alias whose
      /// visible name and original agree, and recording it is what lets an
      /// `(:alias ...)` of an imported name find the module its origin lives
      /// in.
      ///
      /// On the registry rather than beside it on `Env`, because type
      /// resolution, constructor resolution and trait lookup are all handed a
      /// registry and nothing else. Not to be confused with `Aliases` above,
      /// which is the table of `type` aliases.
      ImportAliases: Map<string, ImportAlias>
      Records: Map<string, string list * (string * HMType) list>
      /// Every record type declaring a given field name.
      ///
      /// A list rather than a single owner: construction names its type, but
      /// `record-ref` and `record-set` still have to fall back to the field
      /// name when the target's type has not been resolved yet. Keeping only
      /// the last owner made a shared field name silently resolve to whichever
      /// type happened to be declared last.
      RecordFields: Map<string, string list>
      /// Record type -> the names of its `#:mutable` fields, in declaration
      /// order. Absent for a record that has none.
      ///
      /// Three things read it, and they are why it is keyed by type rather than
      /// folded into `Records`: `record-set!` asks whether the field it is
      /// writing is writable at all; the value restriction asks whether
      /// constructing this type allocates a cell; and code generation asks
      /// which fields have to leave the emitted record's positional parameter
      /// list, since a positional parameter is init-only. `Records` answers
      /// "what type is this field", which is a different question consulted in
      /// unification, and none of these three want to disturb it.
      MutableRecordFields: Map<string, string list>
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
      NoDiscard: Set<string>

      /// Type keys whose name arrived without their representation — an
      /// `#:opaque` export, read back from a dependency's metadata.
      ///
      /// Only ever holds *imported* types. Inside the module that declares one
      /// the body is fully visible, so nothing is entered here for it.
      ///
      /// Nothing is enforced against this set: an opaque type registers no
      /// constructor, no `Records` entry and no `Unions` entry, so every use of
      /// its representation already fails on the ordinary path. It is read to
      /// say *why* the lookup failed.
      OpaqueTypes: Set<string>

      /// Constructor or field name -> the opaque type key it belongs to.
      ///
      /// Diagnostics only, and it has to be a map from the member rather than
      /// from the type because that is the direction the failing lookups run:
      /// a pattern has a constructor name and no type, and `record-ref` has a
      /// field name and often no resolved target yet.
      HiddenMembers: Map<string, string>

      /// Names whose call parks the thread it runs on.
      ///
      /// Three sources, and the set exists because they have to be asked as
      /// one: the builtins `Prelude.blockingBuiltins` names, the aliases an
      /// `import/extern` marked `#:blocking`, and the *imported* definitions a
      /// dependency published as reaching one of those.
      ///
      /// The third is what makes the lint work at all. A module sees its
      /// dependencies as signatures, so without it `(read-line p)` inside a
      /// bjoroutine is a call to an opaque extern and the graph stops there —
      /// which is exactly the call worth reporting.
      BlockingNames: Set<string>

      /// Written name -> the name its suspending copy is emitted under, for
      /// every `defbjouble` in scope.
      ///
      /// Both halves are ordinary definitions by the time anything downstream
      /// sees them; this is the only record that they are two faces of one
      /// name, and it is what call-site selection reads. Imported entries are
      /// in it too — a `defbjouble` in the prelude has to be selectable from
      /// every module that imports it, which is the whole point of the port
      /// surface being written this way.
      DoubleDefs: Map<string, string>

      /// The emitted names of definitions the compiler *generated* rather than
      /// read: the suspending copy every `-?->` signature asks for.
      ///
      /// Only diagnostics need this, and they need it badly. A generated copy
      /// shares the source ranges of the definition it was copied from, so a
      /// failure in one is reported at a line where, as written, nothing is
      /// wrong — the call that cannot be coloured only becomes a yield point in
      /// the copy. Without knowing which definitions are copies there is no way
      /// to say so, and a `defbjouble`'s hand-written half must not be told it
      /// was generated.
      GeneratedCopies: Set<string>

      /// Of those, the ones nobody asked for by name: the copy given to a
      /// `defun` because its call graph reaches a `defbjouble`.
      ///
      /// Kept apart from the declared ones because the two fail differently. A
      /// `-?->` copy that cannot be coloured is an error — the author wrote the
      /// arrow and is owed an answer. An inferred copy is the compiler's own
      /// idea, so one that cannot be coloured is dropped, and the call keeps the
      /// ordinary copy and the parking warning it already had. Erroring there
      /// would be blaming the author for a decision they did not make.
      InferredCopies: Set<string>

      /// Definitions whose *signature* is written `-bjo->`, so `defbjo` was not
      /// a choice their author made.
      ///
      /// `(: broken Filter)` over a `Filter` that is a `-bjo->` alias must be
      /// defined with `defbjo` — a `defun` there is an error. Anything asking
      /// "did this need to suspend?" has to leave those alone, or it recommends
      /// a rewrite the next pass rejects.
      ColourDeclared: Set<string>

      /// Bindings with a type parameter that none of their parameters mention.
      ///
      /// These bindings represent functions that diverge (never return), as they
      /// have no way to produce a value for a type parameter they do not take 
      /// as an argument. 
      /// 
      /// Tracks these functions because C# cannot infer such parameters, requiring
      /// `Codegen` to explicitly emit type arguments. Additionally, `MustUse` uses 
      /// this to skip discard checks since divergent functions yield no value.
      ReturnOnlyGenerics: Set<string> }

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
      /// `Bindings` as they stood at module level, which is what an `EResolved`
      /// resolves against.
      ///
      /// Set once per top-level declaration, by `checkDecl`, and never by a
      /// binder inside one — so it holds the imports, the prelude and this
      /// module's own definitions, and nothing a body binds. That is exactly
      /// the scope a name the compiler wrote was written in.
      Resolved: Map<string, Binding>
      /// The trait method names whose binding is still the method's own.
      ///
      /// A method is bound like anything else, so a program that binds the same
      /// name overwrites it and there is nothing in `Bindings` to tell the two
      /// apart. `addBinding` drops the name from here, and only a `def/trait`
      /// puts one in — so this says "calling this name still means dispatching
      /// on the trait", which is what a call site has to know.
      ///
      /// An *inline* trait's methods are never bound at all and so are never
      /// listed. Their calls are recognised by the absence of a binding.
      TraitMethodNames: Set<string>
      /// The trait method this declaration is the implementation of, if it is
      /// one.
      ///
      /// A `defun` binds its own name so that its body can recurse, and inside
      /// an `impl` that binding would otherwise read as shadowing the very
      /// method being implemented. It is not: `(defun (= xs ys) ... (= (list-head
      /// xs) (list-head ys)))` compares the *elements*, and has to dispatch.
      ImplMethod: string option
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
        // A `dyn` type key contains spaces. For the trait's own auto-impl, the
        // class name suffix is `DynImpl`. An impl of a *different* trait for the
        // same `dyn` type includes the hidden trait name to prevent class name
        // collisions.
        elif Naming.isDynType targetTypeName then
            let hidden = (Naming.dynTraitOf targetTypeName).Value

            if hidden = traitName || Naming.sanitizeIdent hidden = traitName then
                "DynImpl"
            else
                $"Dyn_%s{Naming.sanitizeIdent hidden}_Impl"
        // A module type key is qualified. The class is already in the module's
        // namespace, so it is named after the last part — a `.NET` type like
        // `System.IO.TextReader` has no namespace to be in and is flattened.
        elif Naming.isModuleKey targetTypeName then Naming.emittedTypeName targetTypeName
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

/// `declaresPolyParam` for an inline trait's template.
let templateDeclaresPolyParam (tpl: TplType) : bool =
    match tpl with
    | TplFun(args, _, _) ->
        args
        |> List.exists (function
            | TplFun(_, _, EPoly) -> true
            | _ -> false)
    | _ -> false

/// `suspendingTwin` for an inline trait's template.
let suspendingTwinTemplate (tpl: TplType) : TplType =
    let param (p: TplType) =
        match p with
        | TplFun(args, ret, EPoly) -> TplFun(args, ret, EAsync)
        | other -> other

    match tpl with
    | TplFun(args, ret, _) -> TplFun(List.map param args, ret, EAsync)
    | other -> other

/// Evaluates whether `(dyn Trait ...)` is valid for a trait, returning `Ok()`
/// or `Error` with a human-readable explanation of why it cannot be boxed.
///
/// Derived from method signatures: a box erases the concrete type, so every
/// boxable method must mention the implementor variable exactly once, as a
/// direct parameter. Methods like `Eq`'s `(-> %a %a bool)` could receive two
/// boxes erasing different types, and `(-> %a %a)` would need to return the
/// erased type.
///
/// Associated types are statically pinned by type annotations and may appear
/// anywhere.
let dynSafety
    (traitName: string)
    (implementorVar: string)
    (kind: TraitKind)
    (clr: ClrConstraintInfo option)
    (signatures: Map<string, HMType>)
    : Result<unit, string> =

    let implVar = TVar("'" + implementorVar)

    let mentions (t: HMType) =
        t |> foldType (fun leaf -> if leaf = implVar then [ () ] else []) |> List.length

    match kind, clr with
    | InlineTrait, _ ->
        Error
            $"'%s{traitName}' applies its implementor, so it has no interface and no dictionary — there is nothing for a box to hold or to dispatch through."
    | _, Some c ->
        Error
            $"'%s{traitName}' stands for the .NET interface '%s{c.InterfaceName}', so there is nothing to box: that interface is already the erased type. Write (cast %s{c.InterfaceName} x) to erase a .NET type."
    | InterfaceTrait, None ->

    let written = "%" + implementorVar

    let offending =
        signatures
        |> Map.toList
        // Derived color twins match the shape of the original method, so filter
        // them out to report errors against the user-written signature.
        |> List.filter (fun (name, _) -> not (Naming.isSuspendingCopy name))
        |> List.tryPick (fun (name, t) ->
            match t with
            | TFun(args, _, _) ->
                let total = mentions t
                let receivers = args |> List.filter ((=) implVar) |> List.length

                if total = 1 && receivers = 1 then None
                elif total = 0 then
                    Some
                        $"'%s{name}' never mentions %s{written}, so it has no receiver to dispatch on and a boxed value could not answer it"
                elif receivers = 0 then
                    Some
                        $"'%s{name}' mentions %s{written} only inside another type or in its result, and a box hands back no concrete type to put there"
                else
                    Some
                        $"'%s{name}' mentions %s{written} %d{total} times, and one box says nothing about another: two of them could hide two different types"
            | _ -> Some $"'%s{name}' is not a function, so it has no parameter for the receiver")

    match offending with
    | Some why ->
        Error
            $"'%s{traitName}' cannot be boxed: %s{why}. A boxable method mentions the implementor exactly once, as one of its own parameters — which is why (-> %s{written} %s{written} bool) and (-> %s{written} %s{written}) are not."
    | None -> Ok()

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

