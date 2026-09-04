module Bjolang.DotNetInterop

/// Compile-time .NET reflection.
///
/// Every foreign call Bjolang emits is resolved *here*, while the program is
/// being type-checked, against the real metadata of the real assemblies. There
/// is no dynamic dispatch, no `dynamic`, and nothing left for the C# compiler
/// to work out: by the time code generation runs, the exact overload, its
/// parameter types and its return type are already known.
///
/// That is what makes `(.Write w 42)` and `(.Write w "42")` two different
/// calls rather than one ambiguous one, and it is what lets a wrong argument
/// type be a Bjolang type error with a Bjolang source position instead of a
/// C# error in generated code the author never wrote.

open System
open System.Collections.Concurrent
open System.Reflection
open Bjolang.TypedAST
open Bjolang.TypedAST.TypeConstants

// ---------------------------------------------------------------------------
// Assembly and type resolution
// ---------------------------------------------------------------------------

/// Resolved types, by fully qualified name.
///
/// Concurrent because it is process-wide and inference is not promised to stay
/// single-threaded; a miss is also cached, so a misspelled type name is looked
/// for once rather than once per mention.
let private typeCache = ConcurrentDictionary<string, Type option>()

/// Assemblies the compiler was told about explicitly, beyond whatever the
/// runtime has already loaded.
let private extraAssemblies = ResizeArray<Assembly>()

/// The framework assemblies worth force-loading before giving up on a name.
///
/// `Type.GetType` searches only the core library and the calling assembly, so
/// `System.IO.StreamWriter` is found — it lives in `System.Private.CoreLib` —
/// while `System.Console` is not, because it lives in an assembly of its own
/// that the compiler may not have touched yet.
let private wellKnownAssemblies =
    [ "System.Runtime"
      "System.Console"
      "System.Private.CoreLib"
      "System.Runtime.Extensions"
      "System.IO.FileSystem"
      "System.Collections"
      "System.Linq"
      "System.Text.RegularExpressions"
      "System.Text.Encoding.Extensions"
      "netstandard"
      "mscorlib" ]

/// Assembly names a type's own namespace suggests, longest prefix first.
///
/// `System.Text.Json.JsonDocument` is in `System.Text.Json`, and `System.Console`
/// is in `System.Console` — the convention holds often enough to be worth trying
/// before falling back to the fixed list.
let private namespaceCandidates (fullName: string) =
    let parts = fullName.Split('.')

    [ for i in parts.Length .. -1 .. 1 -> String.Join(".", parts[0 .. i - 1]) ]

let private tryLoad (name: string) : Assembly option =
    try
        Some(Assembly.Load(AssemblyName name))
    with _ ->
        None

/// Registers an assembly the compiler should also search, given its path.
///
/// Nothing in the language wires this up yet — every type the tests need is in
/// the framework — but the resolver consults it, so referencing a user DLL is a
/// matter of calling this rather than of changing the search.
let registerAssemblyFile (path: string) : unit =
    try
        let asm = Assembly.LoadFrom path

        if not (extraAssemblies.Contains asm) then
            extraAssemblies.Add asm
            typeCache.Clear()
    with ex ->
        failwithf $"Interop Error: could not load the assembly '%s{path}': %s{ex.Message}"

let private searchLoaded (fullName: string) : Type option =
    let loaded =
        Seq.append (AppDomain.CurrentDomain.GetAssemblies() :> seq<Assembly>) extraAssemblies

    loaded
    |> Seq.tryPick (fun asm ->
        try
            match asm.GetType(fullName, false, false) with
            | null -> None
            | t -> Some t
        with _ ->
            None)

let private resolveUncached (fullName: string) : Type option =
    match Type.GetType(fullName, false, false) with
    | null ->
        match searchLoaded fullName with
        | Some t -> Some t
        | None ->
            // Nothing loaded has it, so pull in the assemblies it is most
            // likely to live in and look once more.
            let candidates = namespaceCandidates fullName @ wellKnownAssemblies

            for candidate in candidates do
                tryLoad candidate |> ignore

            searchLoaded fullName
    | t -> Some t

/// The `System.Type` a fully qualified name denotes, or `None`.
let tryResolveType (fullName: string) : Type option =
    typeCache.GetOrAdd(fullName, resolveUncached)

/// The `System.Type` a fully qualified name denotes, or a diagnostic.
let resolveType (context: string) (fullName: string) : Type =
    match tryResolveType fullName with
    | Some t -> t
    | None ->
        failwithf
            $"Interop Error%s{context}: cannot find the .NET type '%s{fullName}'. Names must be fully qualified, as in System.IO.StreamWriter."

// ---------------------------------------------------------------------------
// CLR interface constraints
// ---------------------------------------------------------------------------
//
// A trait may declare that it *is* a .NET interface, and is then discharged by
// asking whether the implementor implements it rather than by finding a
// `impl`. See `Docs/Numerics.org`.

/// The interface a name and an arity denote, as a generic *definition*.
///
/// The arity is separate from the name because that is how a `def/trait`
/// carries it: `(System.Numerics.INumber %a)` is a name and one argument, and
/// reflection spells the same thing `System.Numerics.INumber``1`. Arity zero is
/// an ordinary non-generic interface such as `System.IDisposable`.
///
/// A constructed interface — `IComparable<int>` — is deliberately not what
/// comes back. The constraint is written over the trait's implementor variable,
/// so the only stable thing to compare against is the definition.
let tryResolveGenericInterface (fullName: string) (arity: int) : Type option =
    let spelled =
        if arity = 0 then
            fullName
        else
            $"%s{fullName}`%d{arity}"

    match tryResolveType spelled with
    | Some t when t.IsInterface -> Some t
    | _ -> None

/// The interface definition applied to the arguments a `def/trait` wrote.
///
/// `None` rather than an exception when the arguments do not fit, and that is
/// load-bearing rather than defensive: the generic-math interfaces constrain
/// their own parameters — `INumber<TSelf>` demands `TSelf : INumber<TSelf>` —
/// so `MakeGenericType` *throws* for `INumber<string>`. A type that cannot even
/// be substituted does not implement the interface, which is the same answer,
/// so the failure is the verdict.
let tryConstructInterface (definition: Type) (args: Type list) : Type option =
    if not definition.IsGenericTypeDefinition then
        if List.isEmpty args then Some definition else None
    elif definition.GetGenericArguments().Length <> args.Length then
        None
    else
        try
            Some(definition.MakeGenericType(Array.ofList args))
        with _ ->
            None

/// Does `t` implement `iface`?
///
/// Both shapes of `iface` are accepted, because the two questions are asked at
/// different times. A generic *definition* (`IComparable``1`) asks whether the
/// type implements it at any arguments at all, which is what validating a
/// `def/trait` wants. A *constructed* interface (`IComparable<int>`) asks
/// whether it implements exactly that, which is what discharging a constraint
/// wants — `int : IComparable<string>` must not satisfy `(Ord int)`.
///
/// Plain equality would answer neither: `IComparable<int>` and `IComparable``1`
/// are different `System.Type`s, and a struct is not assignable to its own
/// interface without the walk.
let implementsInterface (t: Type) (iface: Type) : bool =
    if iface.IsGenericTypeDefinition then
        let definitionOf (candidate: Type) =
            if candidate.IsGenericType then
                candidate.GetGenericTypeDefinition()
            else
                candidate

        definitionOf t = iface
        || t.GetInterfaces() |> Array.exists (fun i -> definitionOf i = iface)
    else
        iface.IsAssignableFrom t

/// The nullary builtin types whose .NET name is not the name Bjolang uses.
///
/// `genericTypeCorrespondence` is the same thing for the constructed generics.
/// This is the rest: types that take no arguments and are still not called what
/// they are called here — a Bjolang `char` is a 32-bit codepoint and so cannot
/// be `System.Char`, which is a UTF-16 code unit.
///
/// `Codegen.mapPrimitiveType` has to agree with this. It is the same
/// correspondence in the direction of emission, and it cannot be shared: it is
/// compiled after this module.
///
/// Only the ones that differ are here. Everything else — `System.Int32`, and a
/// type a program declared — resolves under the name it already has.
let private nullaryCorrespondence =
    dict
        [ "Unit", "Bjoml.Unit"
          TypeConstants.CharName, "Bjolang.Runtime.BjoChar"
          "StringCursor", "Bjolang.Runtime.StringCursor"
          "Syntax", "Bjolang.Runtime.Syntax"
          "StringBuilder", "System.Text.StringBuilder"
          "Keyword", "BjolangRuntime.Keyword"
          "Symbol", "BjolangRuntime.Symbol"
          "CancelReason", "BjolangRuntime.CancelReason"
          "VecCursor", "BjolangRuntime.VecCursor"
          "SeqCursor", "BjolangRuntime.SeqCursor" ]

/// The same correspondence read backwards, for the one entry where it is
/// unambiguous.
///
/// `mapClrType` did not consult the table at all, which made it
/// one-directional: a Bjolang `char` knew it was a `BjoChar`, and a .NET method
/// *returning* one came back as a type named `Bjolang.Runtime.BjoChar` that
/// unified with nothing. No such method could be imported, which is why
/// `read-char` was a builtin — the workaround, not the design.
///
/// Only `char`, and deliberately not the whole table. The others have a .NET
/// name a program may also write: `(import/class (SB (: System.Text.StringBuilder ...)))`
/// is a real thing to do, and mapping the reflected type to `StringBuilder`
/// would make the imported alias and the reflected member disagree about what
/// they are. `char` has no such spelling — `System.Char` is a *different* type,
/// a UTF-16 code unit rather than a scalar — so there is nothing to collide
/// with.
let private clrToNullary =
    dict [ "Bjolang.Runtime.BjoChar", TypeConstants.CharName ]

/// A member of an interface, and whether it is reached through the type or
/// through a value: `T.Abs(x)` against `x.CompareTo(y)`.
type ClrMemberKind =
    | StaticMember
    | InstanceMember

/// Finds `memberName` on `iface` or on any interface it extends.
///
/// The walk is the whole of it. An interface does *not* inherit its bases'
/// members through `GetMethods`, so asking `INumber``1` for `Abs` finds
/// nothing — `Abs` is declared on `INumberBase``1`, which `INumber` merely
/// extends. The same is true of every transcendental function on
/// `IFloatingPointIeee754`, which are spread across `ITrigonometricFunctions`,
/// `IExponentialFunctions` and `IRootFunctions`.
///
/// Static wins a tie. The generic-math members are static abstract, and
/// `INumber` also carries the instance `CompareTo` it gets from `IComparable`,
/// so a name present as both is the static one being asked for.
let tryFindInterfaceMember (iface: Type) (memberName: string) : ClrMemberKind option =
    let staticFlags =
        BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.FlattenHierarchy

    let instanceFlags =
        BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.FlattenHierarchy

    let candidates = Array.append [| iface |] (iface.GetInterfaces())

    let has (flags: BindingFlags) =
        candidates
        |> Array.exists (fun t -> t.GetMethods flags |> Array.exists (fun m -> m.Name = memberName))

    if has staticFlags then Some StaticMember
    elif has instanceFlags then Some InstanceMember
    else None

/// Every member name an interface and its bases offer, for a diagnostic that
/// has to suggest something.
let interfaceMemberNames (iface: Type) : string list =
    let flags =
        BindingFlags.Public
        ||| BindingFlags.Static
        ||| BindingFlags.Instance
        ||| BindingFlags.FlattenHierarchy

    Array.append [| iface |] (iface.GetInterfaces())
    |> Array.collect (fun t -> t.GetMethods flags)
    |> Array.map (fun m -> m.Name)
    |> Array.filter (fun n -> not (n.StartsWith "op_") && not (n.StartsWith "get_"))
    |> Array.distinct
    |> Array.sort
    |> List.ofArray

// ---------------------------------------------------------------------------
// System.Type <-> HMType
// ---------------------------------------------------------------------------

/// Follows resolved metavariables to whatever they were bound to.
///
/// `Unification.prune` is the general version, but it is compiled after this
/// module and this only ever needs the one case.
let rec private pruneLocal (t: HMType) : HMType =
    match t with
    | TMeta { Value = Some inner } -> pruneLocal inner
    | other -> other

// ---------------------------------------------------------------------------
// Generic types
// ---------------------------------------------------------------------------

/// The .NET generic type definitions that Bjolang already has a name for.
///
/// Every *other* constructed generic maps to a type constructor spelled after
/// the .NET type itself — `Set.Set<int>` is `(Set.Set int)` — which is what
/// makes `import/class` able to name one. These are the ones where the two
/// names differ, because the type is the runtime's own and the language has
/// been spelling it since before interop could see inside a generic at all.
///
/// The inverse is `clrOfBjolangGeneric` below, and the two are written as one
/// list so that they cannot drift. `Codegen.mapPrimitiveType` has to agree with
/// them: it is the same correspondence, in the direction of emission.
let private genericTypeCorrespondence =
    [ "System.Collections.Generic.IEnumerable`1", "Seq"
      "System.Collections.Generic.IAsyncEnumerable`1", "AsyncSeq"
      "SchemeList.SchemeList`1", "List"
      "SchemeList.SchemeListBuilder`1", "ListBuilder"
      "Collections.RrbList`1", "Vec"
      "Collections.RrbBuilder`1", "VecBuilder"
      "BjolangRuntime+Option`1", "Option"
      "BjolangRuntime+Result`2", "Result"
      "Bjoml.Promise`1", "Promise"
      "Bjoml.IEvent`1", "Event"
      "Bjoml.Channel`1", "Chan" ]

let private bjolangOfClrGeneric =
    genericTypeCorrespondence |> List.map (fun (clr, bjo) -> clr, bjo) |> dict

let private clrOfBjolangGeneric =
    genericTypeCorrespondence |> List.map (fun (clr, bjo) -> bjo, clr) |> dict

/// The name a .NET type is known by in a Bjolang type constructor.
///
/// The arity mark goes: a Bjolang constructor carries its arity in the number
/// of arguments it is applied to, so `Set.Set`1` is `Set.Set`. A nested type's
/// `+` becomes `.`, which is both what C# writes and what the code generator
/// emits.
let clrTypeName (t: Type) : string =
    // The definition rather than the construction. An *open* constructed
    // generic — `Set<T>` as a method's parameter mentions it — has no
    // `FullName` at all, so the namespace has to come from the definition it
    // was built from.
    let t =
        if t.IsGenericType && not t.IsGenericTypeDefinition then
            t.GetGenericTypeDefinition()
        else
            t

    let full =
        if isNull t.FullName then
            if String.IsNullOrEmpty t.Namespace then t.Name else t.Namespace + "." + t.Name
        else
            t.FullName

    let withoutArity =
        match full.IndexOf '`' with
        | -1 -> full
        | i -> full.Substring(0, i)

    withoutArity.Replace("+", ".")

/// Is this a `System.Func<...>`? Its last type argument is the return type.
let private isFuncType (def: Type) =
    not (isNull def.FullName) && def.FullName.StartsWith "System.Func`"

let private isActionType (def: Type) =
    not (isNull def.FullName) && def.FullName.StartsWith "System.Action`"

let private isValueTupleType (def: Type) =
    not (isNull def.FullName) && def.FullName.StartsWith "System.ValueTuple`"

/// The Bjolang type a .NET type corresponds to.
///
/// Structural, and that is the whole of what makes generic interop possible: a
/// method's parameters and return type are read *into* the language rather than
/// flattened to an opaque name, so `Func<T, bool>` arrives as an arrow, an
/// `IEnumerable<T>` as a `(Seq %a)`, and the method's own type parameters as
/// type variables that a declared signature can then solve.
let rec mapClrType (t: Type) : HMType =
    if t.IsGenericParameter then
        // A method's or a type's own parameter, as a Bjolang type variable. It
        // is quoted because that is how `resolveTypeAnnotation` spells one, and
        // named after the .NET parameter so that a diagnostic can point at it.
        TVar("'" + t.Name)
    elif t.IsArray then
        TCon("Array", [ mapClrType (t.GetElementType()) ])
    elif t.IsByRef || t.IsPointer then
        // `out`/`ref` parameters have no Bjolang spelling. Mapping them to the
        // referent would silently drop the indirection, so overloads that use
        // them simply do not match.
        TCon("<byref>", [ mapClrType (t.GetElementType()) ])
    elif t.IsGenericType then
        let def = t.GetGenericTypeDefinition()
        let args = t.GetGenericArguments() |> Array.toList |> List.map mapClrType

        // A delegate is an arrow, which is what lets a Bjolang lambda be passed
        // to `Filter` or `Fold` with nothing in between. `Action` returns the
        // *interop* void rather than unit — see `TypeConstants.voidType`.
        if isFuncType def then
            tfun (args |> List.take (args.Length - 1)) (List.last args)
        elif isActionType def then
            tfun args voidType
        elif isValueTupleType def then
            TTuple args
        else
            match bjolangOfClrGeneric.TryGetValue(def.FullName) with
            | true, name -> TCon(name, args)
            | _ -> TCon(clrTypeName t, args)
    else
        match t.FullName with
        | null -> TCon("<open>", [])
        | "System.Void" -> voidType
        | "System.Int32" -> intType
        | "System.Int64" -> longType
        | "System.Double" -> doubleType
        | "System.String" -> stringType
        | "System.Boolean" -> boolType
        | "System.Byte" -> byteType
        | "System.Int16" -> shortType
        | "System.UInt16" -> ushortType
        | "System.UInt32" -> uintType
        | "System.UInt64" -> ulongType
        | "System.Object" -> objType
        | "System.Action" -> tfun [] voidType
        // The unit value's runtime type. A Bjolang `void` in a signature is
        // this, so a .NET member typed in terms of it is already in the
        // language.
        | "Bjoml.Unit" -> unitType
        | name ->
            match clrToNullary.TryGetValue name with
            | true, bjolang -> TCon(bjolang, [])
            | _ -> TCon(name, [])

/// The .NET type a Bjolang type corresponds to, when it has one.
///
/// The inverse of `mapClrType`, and it has to be: overload scoring asks this
/// what an argument *is* before comparing it with a parameter, so a type the
/// two functions disagree about is one that maps in and then fails to match the
/// very parameter it came from.
///
/// `None` covers two very different situations that the callers keep apart: a
/// type that has no .NET counterpart at all (a record this module declared),
/// and one that is simply not known yet (an unresolved metavariable).
let rec tryClrTypeOf (t: HMType) : Type option =
    match pruneLocal t with
    | TCon("Array", [ elem ]) -> tryClrTypeOf elem |> Option.map (fun e -> e.MakeArrayType())
    | TCon(name, []) ->
        match nullaryCorrespondence.TryGetValue name with
        | true, clrName -> tryResolveType clrName
        | _ -> tryResolveType name
    | TCon(name, args) ->
        // A constructed generic: resolve the definition by arity and fill it in.
        // The definition is spelled with its arity mark, which the Bjolang name
        // has dropped.
        let clrName =
            match clrOfBjolangGeneric.TryGetValue name with
            | true, full -> full
            | _ -> $"%s{name}`%d{args.Length}"

        let resolvedArgs = args |> List.map tryClrTypeOf

        if resolvedArgs |> List.forall Option.isSome then
            tryResolveType clrName
            |> Option.bind (fun def ->
                if def.IsGenericTypeDefinition && def.GetGenericArguments().Length = args.Length then
                    try
                        Some(def.MakeGenericType(resolvedArgs |> List.map Option.get |> Array.ofList))
                    with _ ->
                        None
                else
                    None)
        else
            None
    | TTuple [] -> tryResolveType "System.ValueTuple"
    | TTuple items ->
        let resolved = items |> List.map tryClrTypeOf

        if resolved |> List.forall Option.isSome then
            tryResolveType $"System.ValueTuple`%d{items.Length}"
            |> Option.bind (fun def ->
                try
                    Some(def.MakeGenericType(resolved |> List.map Option.get |> Array.ofList))
                with _ ->
                    None)
        else
            None
    // A function value is a delegate. `Action` for the interop void, `Func`
    // otherwise — exactly the choice `Codegen.typeToString` makes, because the
    // delegate this answers has to be the one the argument was emitted as.
    | TFun(args, ret, _) ->
        let resolvedArgs = args |> List.map tryClrTypeOf
        let isVoid = pruneLocal ret = voidType

        if resolvedArgs |> List.forall Option.isSome then
            let argTypes = resolvedArgs |> List.map Option.get

            let name, allTypes =
                if isVoid then
                    (if args.IsEmpty then "System.Action" else $"System.Action`%d{args.Length}"), argTypes
                else
                    match tryClrTypeOf ret with
                    | Some retType -> $"System.Func`%d{args.Length + 1}", argTypes @ [ retType ]
                    | None -> "", []

            if name = "" then
                None
            else
                tryResolveType name
                |> Option.bind (fun def ->
                    if allTypes.IsEmpty then
                        Some def
                    else
                        try
                            Some(def.MakeGenericType(Array.ofList allTypes))
                        with _ ->
                            None)
        else
            None
    | _ -> None

/// Is the type still open — a metavariable nothing has pinned down?
let isUnresolved (t: HMType) : bool =
    match pruneLocal t with
    | TMeta _ -> true
    | _ -> false

/// Renders types as Bjolang spells them, for diagnostics.
///
/// Every type in one call shares a naming table, so a variable on both sides of
/// a mismatch is visibly the same variable. An unsolved one is `?a`, `?b`, ...
/// rather than `%a`: a declared type variable and one the compiler is still
/// looking for are different things, and a reader should not have to guess
/// which a name is.
let showTypesTogether (ts: HMType list) : string list =
    let names = System.Collections.Generic.Dictionary<int, string>()

    // Find all type names that are defined in multiple modules. We will prefix these specific types with their directory name in error messages so the user can tell them apart.
    let ambiguous =
        let keys = System.Collections.Generic.HashSet<string>()

        let rec collect t =
            match pruneLocal t with
            | TCon(name, args) ->
                keys.Add name |> ignore
                List.iter collect args
            | TFun(args, ret, _) -> List.iter collect (ret :: args)
            | TTuple items -> List.iter collect items
            | TAssoc(_, _, impl) -> collect impl
            | _ -> ()

        ts |> List.iter collect

        keys
        |> Seq.filter Naming.isModuleKey
        |> Seq.groupBy Naming.showTypeName
        |> Seq.filter (fun (_, group) -> Seq.length group > 1)
        |> Seq.collect snd
        |> Set.ofSeq

    let showName (name: string) =
        if Set.contains name ambiguous then
            Naming.showQualifiedTypeName name
        else
            Naming.showTypeName name

    let nameOf (id: int) =
        match names.TryGetValue id with
        | true, n -> n
        | _ ->
            let i = names.Count
            let n = "?" + string (char (int 'a' + i % 26)) + (if i < 26 then "" else string (i / 26))
            names[id] <- n
            n

    let rec go (t: HMType) : string =
        match pruneLocal t with
        | TCon("Array", [ e ]) -> $"(Array %s{go e})"
        // A trait object type is printed back as originally written, e.g.
        // `(dyn Foldable #:item int)`. Matched before the cases below because a
        // trait without associated types has an empty argument list and would
        // otherwise be formatted as its internal name.
        | TCon(name, args) when Naming.isDynType name ->
            let assocs =
                List.zip (Naming.dynAssocNamesOf name) args
                |> List.collect (fun (assocName, t) -> [ "#:" + assocName; go t ])

            "(" + String.Join(" ", "dyn" :: (Naming.dynTraitOf name).Value :: assocs) + ")"
        // Spelled as a signature spells it, which is not always the name the
        // constructor carries: `char` is `TCon("Char")` and `void` is
        // `TCon("Unit")`.
        | TCon(name, []) ->
            match name with
            | "System.Int32" -> "int"
            | "System.Int64" -> "long"
            | "System.Int16" -> "short"
            | "System.UInt16" -> "ushort"
            | "System.UInt32" -> "uint"
            | "System.UInt64" -> "ulong"
            | "System.Double" -> "double"
            | "System.String" -> "string"
            | "System.Boolean" -> "bool"
            | "System.Byte" -> "byte"
            | "System.Object" -> "object"
            | "System.Void"
            | "Unit" -> "void"
            | "Char" -> "char"
            // A declared type is keyed by the module that declared it, and the
            // reader is shown both halves: `Banana` alone is no answer when
            // the other side of the mismatch is somebody else's `Banana`.
            | other -> showName other
        | TCon(name, args) ->
            "(" + showName name + " " + String.Join(" ", args |> List.map go) + ")"
        | TFun(args, ret, eff) -> "(" + arrowHead eff + " " + String.Join(" ", (args @ [ ret ]) |> List.map go) + ")"
        | TTuple items -> "(Tuple " + String.Join(" ", items |> List.map go) + ")"
        | TVar n -> "%" + n.TrimStart('\'')
        | TMeta m -> nameOf m.Id
        | TAssoc(tn, an, impl) -> $"(assoc %s{tn} %s{an} %s{go impl})"

    ts |> List.map go

let showType (t: HMType) : string =
    showTypesTogether [ t ] |> List.head

/// Which half of a type's surface a member lookup searches. Named here because
/// both resolvers below ask the question, and they have to ask it the same way.
let private instanceFlags = BindingFlags.Public ||| BindingFlags.Instance
let private staticFlags = BindingFlags.Public ||| BindingFlags.Static

// ---------------------------------------------------------------------------
// Generic methods
// ---------------------------------------------------------------------------
//
// A generic method's type arguments have to come from somewhere, and the answer
// is the signature the import declares. `(import/extern (set-add (: Set.SetModule.Add
// (-> (Set %a) %a (Set %a)))))` says what the alias means in Bjolang; matching
// that against the method's own `(Set<T>, T) -> Set<T>` solves `T` to `%a`, once,
// at the import. Every call site is then an ordinary polymorphic call: the
// signature is instantiated with fresh metavariables, the arguments unify with
// it, and the *same* substitution says what to write between the angle brackets.
//
// So nothing here infers anything a Bjolang programmer did not write down, and
// the type arguments are known before code generation rather than left to C#'s
// own inference — which is the same discipline the non-generic path follows for
// overloads.

/// Solves a generic method's type parameters by matching its own signature
/// against a declared one.
///
/// One-way: only the names in `typeParams` are solved for. The declared side is
/// fixed, so a type variable of the *signature* stands for itself and has to
/// meet the same variable on the other side.
let rec private solveTypeParams
    (typeParams: Set<string>)
    (solution: Map<string, HMType>)
    (fromMethod: HMType)
    (declared: HMType)
    : Map<string, HMType> option =

    match fromMethod, declared with
    | TVar v, _ when typeParams.Contains v ->
        match Map.tryFind v solution with
        // A type parameter used twice has to mean the same thing both times,
        // which is what makes `(-> (Set %a) %a (Set %a))` reject a signature
        // whose element type disagrees with its set's.
        | Some existing -> if existing = declared then Some solution else None
        | None -> Some(Map.add v declared solution)
    | TVar a, TVar b -> if a = b then Some solution else None
    | TCon(n, xs), TCon(m, ys) when n = m && xs.Length = ys.Length ->
        List.zip xs ys
        |> List.fold (fun acc (x, y) -> acc |> Option.bind (fun s -> solveTypeParams typeParams s x y)) (Some solution)
    | TFun(xs, r1, _), TFun(ys, r2, _) when xs.Length = ys.Length ->
        List.zip (r1 :: xs) (r2 :: ys)
        |> List.fold (fun acc (x, y) -> acc |> Option.bind (fun s -> solveTypeParams typeParams s x y)) (Some solution)
    | TTuple xs, TTuple ys when xs.Length = ys.Length ->
        List.zip xs ys
        |> List.fold (fun acc (x, y) -> acc |> Option.bind (fun s -> solveTypeParams typeParams s x y)) (Some solution)
    | a, b -> if a = b then Some solution else None

/// What a resolved generic method tells the rest of the compiler.
type ResolvedGenericMethod =
    { /// The method's type parameters, in the order C# writes them between the
      /// angle brackets, each solved to a Bjolang type in terms of the declared
      /// signature's own variables.
      TypeArguments: HMType list
      /// The parameter types as the *declaration* has them, receiver excluded.
      /// Not the method's own: they are the same types, and these are the ones
      /// a call site unifies against.
      ParameterTypes: HMType list
      /// The method's own return type, with the type arguments substituted in.
      ///
      /// The same as the declared one but for a method that answers nothing:
      /// `void` in a signature is the *unit*, and what a void call has is the
      /// interop void, which no value can be. Taking the method's answer here
      /// is what keeps `(setbuilder-add! b x)` a statement rather than a value
      /// of a type C# has no way to produce.
      ReturnType: HMType
      DeclaringType: string
      Name: string
      IsStatic: bool }

/// Every public method of this name that is a generic definition.
let private genericMethods (t: Type) (name: string) (flags: BindingFlags) =
    t.GetMethods flags
    |> Array.filter (fun m ->
        m.Name = name
        && m.IsGenericMethodDefinition
        && not (m.GetParameters() |> Array.exists (fun p -> p.ParameterType.IsByRef || p.ParameterType.IsPointer)))
    |> Array.toList

/// Does this member consist *only* of generic definitions?
///
/// The question decides which resolution a call takes, and it is asked of the
/// whole method group rather than per call: a name that has an ordinary
/// overload keeps resolving by argument types, exactly as before.
let isGenericOnlyMethod (isStatic: bool) (t: Type) (name: string) : bool =
    let flags = if isStatic then staticFlags else instanceFlags
    let all = t.GetMethods flags |> Array.filter (fun m -> m.Name = name)

    not (Array.isEmpty all) && all |> Array.forall (fun m -> m.IsGenericMethodDefinition)

/// Resolves a generic method against the signature its import declared.
///
/// `declaredParams`/`declaredReturn` are the *caller's* view with the receiver
/// already removed, so an instance member is resolved in its own parameters
/// exactly as a static one is.
let resolveGenericMethod
    (where: string)
    (isStatic: bool)
    (t: Type)
    (name: string)
    (declaredParams: HMType list)
    (declaredReturn: HMType)
    : ResolvedGenericMethod =

    let flags = if isStatic then staticFlags else instanceFlags
    let candidates = genericMethods t name flags

    if candidates.IsEmpty then
        failwithf $"Type Error at %s{where}: '%s{t.FullName}' has no generic method named '%s{name}'."

    /// How many of a method's parameters a call is obliged to pass.
    ///
    /// A trailing optional parameter may be left out — that is what C# does at
    /// its own call sites, and the emitted call leaves it out the same way — so
    /// a signature declaring fewer arguments than the method takes is right
    /// rather than wrong, as long as everything it omits has a default. It is
    /// how a comparer-taking factory is imported without a comparer.
    let requiredCount (m: MethodInfo) =
        let ps = m.GetParameters()

        let rec countFrom i =
            if i > 0 && ps[i - 1].IsOptional then countFrom (i - 1) else i

        countFrom ps.Length

    let byArity =
        candidates
        |> List.filter (fun m ->
            declaredParams.Length <= m.GetParameters().Length
            && declaredParams.Length >= requiredCount m)

    if byArity.IsEmpty then
        let arities =
            candidates
            |> List.map (fun m ->
                let total = m.GetParameters().Length
                let required = requiredCount m
                if required = total then string total else $"%d{required} to %d{total}")
            |> List.distinct
            |> List.sort

        let shownArities = String.Join(" or ", arities)

        failwithf
            $"Type Error at %s{where}: '%s{t.FullName}.%s{name}' takes %s{shownArities} argument(s), but the declared signature has %d{declaredParams.Length}."

    let attempt (m: MethodInfo) =
        let typeParams = m.GetGenericArguments() |> Array.map (fun p -> "'" + p.Name) |> Set.ofArray

        // Only as many as were declared: the rest are optional and the call
        // simply leaves them out.
        let methodParams =
            m.GetParameters()
            |> Array.map (fun p -> mapClrType p.ParameterType)
            |> Array.toList
            |> List.truncate declaredParams.Length

        let methodReturn = mapClrType m.ReturnType

        // A method that answers nothing is declared `void`, which in a Bjolang
        // signature is the unit — there is no way to write the interop void, and
        // no reason to want one. The allowance is *only* here, at the method's
        // own answer: inside a parameter it would equate `Action<T>` with
        // `(-> %a void)`, which are two different delegates, and the mismatch
        // would surface as C# that does not compile rather than as a type error.
        let declaredReturn =
            if methodReturn = voidType && declaredReturn = unitType then
                voidType
            else
                declaredReturn

        let solved =
            List.zip (methodReturn :: methodParams) (declaredReturn :: declaredParams)
            |> List.fold
                (fun acc (fromMethod, declared) ->
                    acc |> Option.bind (fun s -> solveTypeParams typeParams s fromMethod declared))
                (Some Map.empty)

        match solved with
        | None -> None
        | Some solution ->
            let ordered =
                m.GetGenericArguments()
                |> Array.map (fun p -> Map.tryFind ("'" + p.Name) solution)
                |> Array.toList

            // Every type parameter has to be pinned by the signature. One the
            // signature never mentions is one nothing could write between the
            // angle brackets, and inventing an answer is exactly the guessing
            // this design does not do.
            if ordered |> List.forall Option.isSome then
                Some(m, ordered |> List.map Option.get, substTypeVars solution methodReturn)
            else
                None

    let describeMethod (m: MethodInfo) =
        let ps =
            m.GetParameters()
            |> Array.map (fun p -> showType (mapClrType p.ParameterType))
            |> String.concat " "

        "  (-> " + ps + " " + showType (mapClrType m.ReturnType) + ")"

    match byArity |> List.choose attempt with
    | [ (_, typeArgs, returnType) ] ->
        { TypeArguments = typeArgs
          ParameterTypes = declaredParams
          ReturnType = returnType
          DeclaringType = t.FullName
          Name = name
          IsStatic = isStatic }
    | [] ->
        let declaredShown =
            showType (tfun declaredParams declaredReturn)

        let shapes = byArity |> List.map describeMethod |> String.concat "\n"

        failwithf
            $"Type Error at %s{where}: the declared signature %s{declaredShown} does not match '%s{t.FullName}.%s{name}'. Its shape is:\n%s{shapes}\nA generic method's type arguments come from the signature, so the two have to agree exactly — a type variable of the signature stands for one of the method's."
    | several ->
        let shapes =
            several |> List.map (fun (m, _, _) -> describeMethod m) |> String.concat "\n"

        failwithf
            $"Type Error at %s{where}: '%s{t.FullName}.%s{name}' is ambiguous — the declared signature fits more than one of its overloads:\n%s{shapes}"

// ---------------------------------------------------------------------------
// Overload resolution
// ---------------------------------------------------------------------------

/// The implicit numeric widenings C# performs, as source -> permitted targets.
///
/// Only widening conversions: an `int` argument may satisfy a `long` parameter,
/// but a `long` argument must not silently satisfy an `int` one.
let private widenings =
    dict [
        typeof<byte>, [ typeof<int16>; typeof<uint16>; typeof<int>; typeof<uint32>; typeof<int64>; typeof<uint64>; typeof<float32>; typeof<float> ]
        typeof<int16>, [ typeof<int>; typeof<int64>; typeof<float32>; typeof<float> ]
        typeof<uint16>, [ typeof<int>; typeof<uint32>; typeof<int64>; typeof<uint64>; typeof<float32>; typeof<float> ]
        typeof<int>, [ typeof<int64>; typeof<float32>; typeof<float> ]
        typeof<uint32>, [ typeof<int64>; typeof<uint64>; typeof<float32>; typeof<float> ]
        typeof<int64>, [ typeof<float32>; typeof<float> ]
        typeof<uint64>, [ typeof<float32>; typeof<float> ]
        typeof<char>, [ typeof<int>; typeof<uint32>; typeof<int64>; typeof<uint64>; typeof<float32>; typeof<float> ]
        typeof<float32>, [ typeof<float> ]
    ]

/// How well an argument fits a parameter. Lower is better; `None` is no fit.
///
/// The scores are ordered so that an exact match always beats a widening and a
/// widening always beats a reference upcast — which is what makes `(.Write w
/// 42)` pick `Write(int)` rather than `Write(long)` or `Write(object)`.
let private scoreArgument (param: Type) (arg: HMType) : int option =
    if isUnresolved arg then
        // Nothing to judge yet. Accepting it is what lets a parameter type flow
        // *into* an argument whose type inference has not settled — but it
        // scores worst, so any candidate that genuinely matches wins.
        Some 100
    else
        match tryClrTypeOf arg with
        | None -> None
        | Some argType ->
            if argType = param then Some 0
            elif
                widenings.ContainsKey argType
                && widenings[argType] |> List.contains param
            then
                Some 1
            elif param.IsAssignableFrom argType then
                // A reference upcast, including anything to `object`.
                Some 2
            elif param = typeof<obj> then
                // Boxing a value type.
                Some 3
            else
                None

/// The signature of a candidate, for diagnostics.
let private showParams (ps: ParameterInfo[]) =
    ps |> Array.map (fun p -> showType (mapClrType p.ParameterType)) |> String.concat " "

/// Picks the single best-fitting candidate, or explains why it cannot.
///
/// `describe` names the thing being resolved and `where` locates it, so the two
/// failure modes — nothing matches, and more than one thing matches equally
/// well — both come out as a Bjolang diagnostic pointing at Bjolang source.
let private selectOverload
    (describe: string)
    (where: string)
    (candidates: (ParameterInfo[] * 'M) list)
    (argTypes: HMType list)
    : ParameterInfo[] * 'M =

    let shownArgs =
        if argTypes.IsEmpty then "no arguments"
        else "(" + (argTypes |> List.map showType |> String.concat " ") + ")"

    let byArity =
        candidates
        |> List.filter (fun (ps, _) -> ps.Length = argTypes.Length)

    if byArity.IsEmpty then
        let arities =
            candidates
            |> List.map (fun (ps, _) -> string ps.Length)
            |> List.distinct
            |> List.sort

        if candidates.IsEmpty then
            failwithf $"Type Error at %s{where}: %s{describe} does not exist."
        else
            let shownArities = String.Join(" or ", arities)

            failwithf
                $"Type Error at %s{where}: %s{describe} takes %s{shownArities} argument(s), but was given %d{argTypes.Length}."

    let scored =
        byArity
        |> List.choose (fun (ps, m) ->
            let scores = List.map2 (fun (p: ParameterInfo) a -> scoreArgument p.ParameterType a) (List.ofArray ps) argTypes

            if scores |> List.forall Option.isSome then
                Some(scores |> List.sumBy Option.get, ps, m)
            else
                None)

    match scored with
    | [] ->
        let overloads =
            byArity
            |> List.map (fun (ps, _) -> "  (" + showParams ps + ")")
            |> String.concat "\n"

        failwithf
            $"Type Error at %s{where}: no overload of %s{describe} accepts %s{shownArgs}. The candidates are:\n%s{overloads}"
    | _ ->
        let best = scored |> List.map (fun (s, _, _) -> s) |> List.min
        let winners = scored |> List.filter (fun (s, _, _) -> s = best)

        match winners with
        | [ (_, ps, m) ] -> ps, m
        | _ ->
            let overloads =
                winners
                |> List.map (fun (_, ps, _) -> "  (" + showParams ps + ")")
                |> String.concat "\n"

            failwithf
                $"Type Error at %s{where}: %s{describe} is ambiguous for %s{shownArgs} — these overloads fit equally well:\n%s{overloads}\nAnnotate the arguments to say which one you mean.\n"

/// What a resolved call tells the type checker and the code generator.
type ResolvedCall =
    { /// Parameter types, in order, as Bjolang types. Inference unifies the
      /// arguments against these, which is also how a still-open argument type
      /// gets pinned down.
      ParameterTypes: HMType list
      ReturnType: HMType
      /// The return type as .NET has it, before `mapClrType` flattened it.
      ///
      /// Needed because a constructed generic maps to a `TCon` whose name is
      /// the mangled `Task`1[[System.String, ...]]` — enough to tell two of
      /// them apart and not enough to take one apart. `#:async` has to reach
      /// inside the task for its result type, so it reads this instead.
      RawReturnType: Type
      DeclaringType: string
      Name: string
      IsStatic: bool }

/// Methods are filtered down to the ones Bjolang can actually call.
///
/// A generic method definition is excluded on purpose: inferring its type
/// arguments is a whole inference problem of its own, and a non-goal here.
/// Leaving it in the candidate list would let it win an overload contest and
/// then fail in generated C#.
let private callableMethods (t: Type) (name: string) (flags: BindingFlags) =
    t.GetMethods flags
    |> Array.filter (fun m ->
        m.Name = name
        && not m.IsGenericMethodDefinition
        && not (m.GetParameters() |> Array.exists (fun p -> p.ParameterType.IsByRef || p.ParameterType.IsPointer)))
    |> Array.toList
    |> List.map (fun m -> m.GetParameters(), m)

let private describeMethod (t: Type) (name: string) = $"'%s{t.FullName}.%s{name}'"

/// Which half of a type's surface a member lookup should search.
///
/// `import/extern` asks this of the metadata rather than of the clause: a name
/// either denotes a static member of that type or an instance one, so there is
/// nothing for the author to say and nothing to get wrong.
let private memberFlags (isStatic: bool) =
    if isStatic then staticFlags else instanceFlags

/// Does the type have *any* public static method of this name?
///
/// Asked at the import rather than at the first call site, so that a misspelled
/// method is reported where it was written.
let hasStaticMethod (t: Type) (name: string) : bool =
    t.GetMethods staticFlags |> Array.exists (fun m -> m.Name = name)

/// Does the type have *any* public instance method of this name, its own or
/// inherited?
let hasInstanceMethod (t: Type) (name: string) : bool =
    t.GetMethods instanceFlags |> Array.exists (fun m -> m.Name = name)

/// Does the type have a public property or field of this name?
///
/// The two are one question because they are read and written identically in
/// C#, so `#:get` and `#:set` do not make the caller say which they meant.
let hasMember (isStatic: bool) (t: Type) (name: string) : bool =
    let flags = memberFlags isStatic
    not (isNull (t.GetProperty(name, flags))) || not (isNull (t.GetField(name, flags)))

/// Is this type a task of some kind?
let private isTaskType (t: Type) =
    let name =
        if t.IsGenericType then t.GetGenericTypeDefinition().FullName else t.FullName

    match name with
    | "System.Threading.Tasks.Task"
    | "System.Threading.Tasks.Task`1"
    | "System.Threading.Tasks.ValueTask"
    | "System.Threading.Tasks.ValueTask`1" -> true
    | _ -> false

/// §7.5: sync-over-async has to be unreachable, not merely discouraged.
///
/// `.Result`, `.Wait()` and `.GetAwaiter().GetResult()` block the calling
/// thread on a task. Inside a fiber that is a pool thread, and depending on the
/// host it either deadlocks outright — the continuation needs the very thread
/// that is waiting — or quietly exhausts the pool. Both failures happen under
/// load, in production, and not in the test that was written for it.
///
/// Reachable today rather than hypothetically: an async method imported
/// *without* `#:async` hands back a `Task`, and a task has all three.
///
/// `GetAwaiter` is refused rather than `GetResult`, because refusing the second
/// half of the idiom leaves the first half looking legal. There is no use for a
/// hand-held awaiter in a language with no `await` to write.
let private rejectSyncOverAsync (where: string) (t: Type) (name: string) : unit =
    if isTaskType t then
        match name with
        | "Wait"
        | "WaitAll"
        | "WaitAny"
        | "Result"
        | "GetAwaiter" ->
            failwithf
                $"Type Error at %s{where}: '%s{name}' on a task blocks the thread it is called on, and inside a bjoroutine that is a thread-pool thread — which deadlocks or starves the pool depending on the host.\n  Bjolang does not support calling asynchronous .NET methods synchronously (sync-over-async). Instead, you must import the method using the #:async flag. When you do this, Bjolang handles the async state machine under the hood. To the caller, it looks like a normal synchronous function, but behind the scenes, the thread is correctly freed back to the thread pool while waiting (§7.2).\n  For genuinely synchronous work that has to block something, `(blocking (fun () ...))` moves it to a thread the pool can grow to replace."
        | _ -> ()

/// Resolves `(.Name target args...)` when instance, or a static method named by
/// `import/extern` when not.
///
/// `DeclaringType` is the type the lookup started from rather than the one that
/// declares the method: an inherited method is still called through the target,
/// and the field is only ever used for diagnostics and for static calls.
let resolveMethod
    (where: string)
    (isStatic: bool)
    (t: Type)
    (name: string)
    (argTypes: HMType list)
    : ResolvedCall =
    rejectSyncOverAsync where t name
    let candidates = callableMethods t name (memberFlags isStatic)

    if candidates.IsEmpty then
        let kind = if isStatic then "static" else "instance"

        failwithf
            $"Type Error at %s{where}: '%s{t.FullName}' has no public %s{kind} method named '%s{name}'."

    let ps, m = selectOverload (describeMethod t name) where candidates argTypes

    { ParameterTypes = ps |> Array.map (fun p -> mapClrType p.ParameterType) |> Array.toList
      ReturnType = mapClrType m.ReturnType
      RawReturnType = m.ReturnType
      DeclaringType = t.FullName
      Name = name
      IsStatic = isStatic }

// ---------------------------------------------------------------------------
// Awaiting .NET
// ---------------------------------------------------------------------------

/// The Bjolang type of a `CancellationToken`, as `mapClrType` spells it.
///
/// Named once because two places have to agree on it: the resolver, which
/// appends it to the argument types so that the token-taking overload is the
/// one selected, and the emitter, which fills the argument in.
let cancellationTokenType : HMType = TCon("System.Threading.CancellationToken", [])

/// What `await`ing this .NET type produces, or `None` if it is not awaitable.
///
/// Deliberately by name rather than by "has a GetAwaiter": the four types below
/// are what the BCL's async surface actually returns, and accepting a custom
/// awaitable would mean accepting one whose awaiter BjoML's builder may not be
/// able to drive.
///
/// A non-generic `Task` produces nothing, which is `void` here — the same thing
/// a `void` method's call has, so an awaited call performed for its effect is
/// statement-shaped exactly like an ordinary one.
///
/// `ValueTask` is accepted for the direct call and only for that. It may be
/// consumed once, so it can never become an event; §7.2's third rule is
/// enforced where `task->event` is, not here.
let awaitedResultType (t: Type) : HMType option =
    if t.IsGenericType then
        match t.GetGenericTypeDefinition().FullName with
        | "System.Threading.Tasks.Task`1"
        | "System.Threading.Tasks.ValueTask`1" -> Some(mapClrType (t.GetGenericArguments()[0]))
        | _ -> None
    else
        match t.FullName with
        | "System.Threading.Tasks.Task"
        | "System.Threading.Tasks.ValueTask" -> Some voidType
        | _ -> None

/// Is this a `ValueTask` or a `ValueTask<T>`?
///
/// Asked only by `task->event`. A `ValueTask` may be consumed exactly once, so
/// it cannot back an event that might be synced twice — and converting it with
/// `.AsTask()` allocates the object the `ValueTask` existed to avoid. §7.2's
/// third rule; awaiting one directly is fine and is what it is for.
let isValueTask (t: Type) : bool =
    if t.IsGenericType then
        t.GetGenericTypeDefinition().FullName = "System.Threading.Tasks.ValueTask`1"
    else
        t.FullName = "System.Threading.Tasks.ValueTask"

/// Does this method return something awaitable, in *any* overload?
///
/// Asked at the import site, where there are no arguments yet to choose an
/// overload with. A method with a mix would be answered "yes" here and caught
/// at the call site by the same check applied to the overload that won.
let hasAwaitableOverload (isStatic: bool) (t: Type) (name: string) : bool =
    callableMethods t name (memberFlags isStatic)
    |> List.exists (fun (_, m) -> (awaitedResultType m.ReturnType).IsSome)

/// Does this method have an overload whose last parameter is a
/// `CancellationToken`, and which takes `arity` parameters in total?
///
/// `arity` is `None` at the import site, where the question is only whether
/// this method is cancellable at all, and `Some n` at a call site, where the
/// question is whether *this* call can have a token appended to it. It counts
/// the method's own parameters either way: an instance member's receiver is the
/// alias's first argument and not one of these.
let hasTokenOverload (isStatic: bool) (t: Type) (name: string) (arity: int option) : bool =
    callableMethods t name (memberFlags isStatic)
    |> List.exists (fun (ps, _) ->
        ps.Length > 0
        && ps[ps.Length - 1].ParameterType = typeof<System.Threading.CancellationToken>
        && (match arity with
            | Some n -> ps.Length = n
            | None -> true))

/// Resolves `(ClassName. args...)`.
let resolveConstructor (where: string) (targetType: Type) (argTypes: HMType list) : ResolvedCall =
    if targetType.IsAbstract then
        failwithf $"Type Error at %s{where}: '%s{targetType.FullName}' is abstract and cannot be constructed."

    let candidates =
        targetType.GetConstructors instanceFlags
        |> Array.filter (fun c -> not (c.GetParameters() |> Array.exists (fun p -> p.ParameterType.IsByRef || p.ParameterType.IsPointer)))
        |> Array.toList
        |> List.map (fun c -> c.GetParameters(), c)

    if candidates.IsEmpty then
        failwithf $"Type Error at %s{where}: '%s{targetType.FullName}' has no public constructor."

    let ps, _ =
        selectOverload $"the constructor of '%s{targetType.FullName}'" where candidates argTypes

    { ParameterTypes = ps |> Array.map (fun p -> mapClrType p.ParameterType) |> Array.toList
      ReturnType = mapClrType targetType
      RawReturnType = targetType
      DeclaringType = targetType.FullName
      Name = ".ctor"
      IsStatic = false }

/// Resolves a member read — `(.-Name x)`, `Class.Member`, or a `#:get` import —
/// as a property first, then a field, matching `resolveMemberWrite`.
///
/// Both are read the same way in C#, so which one it turned out to be does not
/// reach the code generator; only the type does. An enum member is a field, and
/// its field type is the enum: `FileMode.Open` is a `FileMode`, not an `int`.
let resolveMemberRead (where: string) (declaringType: Type) (name: string) (isStatic: bool) : HMType =
    // Only for an instance read: a task is a value someone holds, and `.Result`
    // on it is the sync-over-async that §7.5 has to make unreachable.
    if not isStatic then
        rejectSyncOverAsync where declaringType name

    let flags = memberFlags isStatic
    let kind = if isStatic then "static" else "instance"

    match declaringType.GetProperty(name, flags) with
    | null ->
        match declaringType.GetField(name, flags) with
        | null ->
            failwithf
                $"Type Error at %s{where}: '%s{declaringType.FullName}' has no public %s{kind} property or field named '%s{name}'."
        | field -> mapClrType field.FieldType
    | prop ->
        if not prop.CanRead then
            failwithf $"Type Error at %s{where}: '%s{declaringType.FullName}.%s{name}' is write-only."

        mapClrType prop.PropertyType

/// Resolves a `#:set` import — a writable property or field, static or not.
///
/// A read-only member is refused here rather than at the write, because the
/// import is where the claim that it can be written was made. `readonly` and
/// `const` are separate cases only in the message: knowing which one it is is
/// the difference between looking for a setter and looking for another way.
let resolveMemberWrite (where: string) (declaringType: Type) (name: string) (isStatic: bool) : HMType =
    let flags = memberFlags isStatic

    match declaringType.GetProperty(name, flags) with
    | null ->
        match declaringType.GetField(name, flags) with
        | null ->
            failwithf
                $"Type Error at %s{where}: '%s{declaringType.FullName}' has no public property or field named '%s{name}'."
        | field ->
            if field.IsLiteral then
                failwithf
                    $"Type Error at %s{where}: '%s{declaringType.FullName}.%s{name}' is a constant and cannot be assigned. Import it with #:get."

            if field.IsInitOnly then
                failwithf
                    $"Type Error at %s{where}: '%s{declaringType.FullName}.%s{name}' is a readonly field, so it can only be assigned by the type's own constructor. Import it with #:get."

            mapClrType field.FieldType
    | prop ->
        if not prop.CanWrite then
            failwithf
                $"Type Error at %s{where}: '%s{declaringType.FullName}.%s{name}' has no setter. Import it with #:get."

        // An `init` accessor is a setter as far as `CanWrite` is concerned, and
        // is one only inside an object initializer. Caught here because the
        // alternative is C# that does not compile, blamed on generated code.
        let initOnly =
            match prop.GetSetMethod() with
            | null -> false
            | setter ->
                setter.ReturnParameter.GetRequiredCustomModifiers()
                |> Array.exists (fun m -> m.FullName = "System.Runtime.CompilerServices.IsExternalInit")

        if initOnly then
            failwithf
                $"Type Error at %s{where}: '%s{declaringType.FullName}.%s{name}' is init-only, so it can only be assigned while the object is being constructed. Import it with #:get."

        mapClrType prop.PropertyType
