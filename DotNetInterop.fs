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

/// The Bjolang type a .NET type corresponds to.
let rec mapClrType (t: Type) : HMType =
    if t.IsArray then
        TCon("Array", [ mapClrType (t.GetElementType()) ])
    elif t.IsByRef || t.IsPointer then
        // `out`/`ref` parameters have no Bjolang spelling. Mapping them to the
        // referent would silently drop the indirection, so overloads that use
        // them simply do not match.
        TCon("<byref>", [ mapClrType (t.GetElementType()) ])
    elif t.IsGenericType
         && t.GetGenericTypeDefinition().FullName = "System.Collections.Generic.IAsyncEnumerable`1" then
        // The one constructed generic that maps to a Bjolang type rather than
        // to the mangled `IAsyncEnumerable`1[[System.String, …]]`.
        //
        // Worth the special case because it is the only way to *get* one: an
        // async stream comes back from a .NET call and there is no other way to
        // make one, so without this `async-seq->chan` would have nothing it
        // could ever be applied to. `IEnumerable<T>` deserves the same
        // treatment for `(Seq %a)` and does not have it yet — see the note on
        // `file-read-lines/seq` in `Prelude`.
        TCon("AsyncSeq", [ mapClrType (t.GetGenericArguments()[0]) ])
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
        | name -> TCon(name, [])

/// The .NET type a Bjolang type corresponds to, when it has one.
///
/// `None` covers two very different situations that the callers keep apart: a
/// type that has no .NET counterpart at all (`(List int)`), and one that is
/// simply not known yet (an unresolved metavariable).
let rec tryClrTypeOf (t: HMType) : Type option =
    match pruneLocal t with
    | TCon("Array", [ elem ]) -> tryClrTypeOf elem |> Option.map (fun e -> e.MakeArrayType())
    | TCon(name, []) -> tryResolveType name
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
            | other -> Naming.showTypeName other
        | TCon(name, args) ->
            "(" + Naming.showTypeName name + " " + String.Join(" ", args |> List.map go) + ")"
        | TFun(args, ret, eff) -> "(" + arrowHead eff + " " + String.Join(" ", (args @ [ ret ]) |> List.map go) + ")"
        | TTuple items -> "(Tuple " + String.Join(" ", items |> List.map go) + ")"
        | TVar n -> "%" + n.TrimStart('\'')
        | TMeta m -> nameOf m.Id
        | TAssoc(tn, an, impl) -> $"(assoc %s{tn} %s{an} %s{go impl})"

    ts |> List.map go

let showType (t: HMType) : string =
    showTypesTogether [ t ] |> List.head

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

let private instanceFlags = BindingFlags.Public ||| BindingFlags.Instance
let private staticFlags = BindingFlags.Public ||| BindingFlags.Static

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
                $"Type Error at %s{where}: '%s{name}' on a task blocks the thread it is called on, and inside a bjoroutine that is a thread-pool thread — which deadlocks or starves the pool depending on the host.\n  There is no sync-over-async in Bjolang, deliberately. Import the method with #:async instead: the call then *is* the result, suspension is invisible at the call site, and the pool thread goes back to the pool while the task is in flight (§7.2).\n  For genuinely synchronous work that has to block something, `(blocking (fun () ...))` moves it to a thread the pool can grow to replace."
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
