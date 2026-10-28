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

/// The constraints of .NET generics, checked where Bjolang uses them.
///
/// A .NET generic method or type can require of its type arguments: a value
/// type (`struct`), a reference type (`class`), a constructor of no
/// parameters (`new()`), or an interface or base class, as
/// `where T : INumber<T>`. Bjolang's types do not say these things, so a
/// call or a type that breaks one used to be found by the C# compiler, about
/// generated code. This pass finds it first, at the place it was written,
/// once the types are known.
///
/// A use at a type variable of a generic Bjolang function is not checked
/// here: that type is not known until the function is called.
module Bjolang.ClrConstraints

open System
open System.Reflection
open System.Collections.Concurrent
open Bjolang.Lexer
open Bjolang.TypedAST

/// A .NET type as a reader of a constraint writes it: `System.Numerics.INumber<T>`.
let rec private show (t: Type) : string =
    if t.IsGenericParameter then
        t.Name
    elif t.IsGenericType then
        let def = t.GetGenericTypeDefinition()
        let name = def.FullName |> Option.ofObj |> Option.defaultValue def.Name
        let bare = match name.IndexOf '`' with -1 -> name | i -> name.Substring(0, i)
        bare + "<" + (t.GetGenericArguments() |> Array.map show |> String.concat ", ") + ">"
    else
        t.FullName |> Option.ofObj |> Option.defaultValue t.Name

/// `c` with the generic parameters it names replaced by their arguments:
/// a method's by `methodArgs`, a type's by `typeArgs`. None when it names a
/// parameter there is no argument for, or when the result breaks a
/// constraint of its own, as `INumber<string>` does.
let rec private substitute (methodArgs: Type[]) (typeArgs: Type[]) (c: Type) : Type option =
    if c.IsGenericParameter then
        let args = if isNull c.DeclaringMethod then typeArgs else methodArgs
        if c.GenericParameterPosition < args.Length then Some args[c.GenericParameterPosition] else None
    elif c.IsArray then
        substitute methodArgs typeArgs (c.GetElementType()) |> Option.map (fun e -> e.MakeArrayType())
    elif c.IsGenericType && c.ContainsGenericParameters then
        let args = c.GetGenericArguments() |> Array.map (substitute methodArgs typeArgs)

        if args |> Array.forall Option.isSome then
            try
                Some(c.GetGenericTypeDefinition().MakeGenericType(args |> Array.map Option.get))
            with :? ArgumentException ->
                None
        else
            None
    else
        Some c

/// What the generic parameter `p` requires and `arg` is not, as a phrase:
/// "a value type (struct)", or the interface. None when `arg` fits.
///
/// A constraint that names a parameter with no argument here, such as one of
/// the declaring type's for a method of a generic type, is passed over.
let private broken (methodArgs: Type[]) (typeArgs: Type[]) (p: Type) (arg: Type) : string option =
    let attrs = p.GenericParameterAttributes

    let has (flag: GenericParameterAttributes) = attrs &&& flag = flag

    if has GenericParameterAttributes.NotNullableValueTypeConstraint
       && (not arg.IsValueType || not (isNull (Nullable.GetUnderlyingType arg))) then
        Some "a value type (struct)"
    elif has GenericParameterAttributes.ReferenceTypeConstraint && arg.IsValueType then
        Some "a reference type (class)"
    elif has GenericParameterAttributes.DefaultConstructorConstraint
         && not arg.IsValueType
         && isNull (arg.GetConstructor Type.EmptyTypes) then
        Some "a type with a public constructor of no parameters (new())"
    else
        p.GetGenericParameterConstraints()
        |> Array.tryPick (fun c ->
            let fits =
                match substitute methodArgs typeArgs c with
                | Some concrete -> concrete.IsAssignableFrom arg
                // The constraint names its own parameter with this argument,
                // and that breaks one of its constraints: `INumber<string>`.
                | None when c.ContainsGenericParameters && not (Array.isEmpty methodArgs && Array.isEmpty typeArgs) ->
                    false
                | None -> true

            if fits then None else Some(show c))

/// The first parameter of `parameters` that its argument breaks, with what it
/// requires.
let private firstBroken (methodArgs: Type[]) (typeArgs: Type[]) (parameters: Type[]) (args: Type[]) =
    Array.zip parameters args
    |> Array.indexed
    |> Array.tryPick (fun (i, (p, a)) -> broken methodArgs typeArgs p a |> Option.map (fun what -> i, p, what))

// ---------------------------------------------------------------------------
// Methods
// ---------------------------------------------------------------------------

/// The generic definitions named `name` on `declaringType` with `arity` type
/// parameters.
let private genericMethods (declaringType: string) (name: string) (arity: int) : MethodInfo list =
    let flags = BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.Instance

    let candidates (t: Type) =
        t.GetMethods flags
        |> Array.filter (fun m -> m.Name = name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length = arity)
        |> Array.toList

    match DotNetInterop.tryResolveType declaringType with
    | Some t -> candidates t
    | None -> []

/// A generic method's call at these type arguments, checked. None when it fits
/// or when it cannot be told: a type argument that is not known, or that has
/// no .NET type yet, as a record this module declares.
let private checkCall
    (registry: TraitRegistry)
    (declaringType: string)
    (name: string)
    (typeArgs: HMType list)
    : string option =
    let pruned = typeArgs |> List.map (Unification.prune registry)
    let clr = pruned |> List.map DotNetInterop.tryClrTypeOf

    if typeArgs.IsEmpty || not (List.forall Option.isSome clr) then
        None
    else
        let args = clr |> List.map Option.get |> Array.ofList

        // Several definitions of one arity are overloads; the call is wrong
        // only when every one of them refuses the arguments.
        let verdicts =
            genericMethods declaringType name args.Length
            |> List.map (fun m -> m, firstBroken args [||] (m.GetGenericArguments()) args)

        if verdicts.IsEmpty || verdicts |> List.exists (fun (_, v) -> v.IsNone) then
            None
        else
            let _, verdict = List.head verdicts
            let i, p, what = verdict.Value

            Some
                $"'%s{declaringType}.%s{name}' needs its type parameter %s{p.Name} to be %s{what}, and here %s{p.Name} is %s{DotNetInterop.showType pruned[i]}."

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/// For a generic .NET type by its Bjolang name and arity: its definition, when
/// some parameter of it is constrained. Most are not, and are passed over.
let private constrainedDefinitions = ConcurrentDictionary<string * int, Type option>()

let private constrainedDefinition (name: string) (arity: int) : Type option =
    constrainedDefinitions.GetOrAdd(
        (name, arity),
        fun _ ->
            if not (name.Contains '.') || Naming.isModuleKey name then
                None
            else
                DotNetInterop.tryResolveType $"%s{name}`%d{arity}"
                |> Option.filter (fun def ->
                    def.IsGenericTypeDefinition
                    && def.GetGenericArguments()
                       |> Array.exists (fun p ->
                           p.GenericParameterAttributes &&& GenericParameterAttributes.SpecialConstraintMask
                           <> GenericParameterAttributes.None
                           || not (Array.isEmpty (p.GetGenericParameterConstraints()))))
    )

/// The first .NET generic type in `t` whose arguments break its constraints.
let rec private checkType (t: HMType) : string option =
    match t with
    | TCon(name, args) when not args.IsEmpty ->
        let own =
            match constrainedDefinition name args.Length with
            | None -> None
            | Some def ->
                let clr = args |> List.map DotNetInterop.tryClrTypeOf

                if not (List.forall Option.isSome clr) then
                    None
                else
                    let actual = clr |> List.map Option.get |> Array.ofList

                    firstBroken [||] actual (def.GetGenericArguments()) actual
                    |> Option.map (fun (i, p, what) ->
                        $"%s{DotNetInterop.showType t} is %s{show def}, which needs %s{p.Name} to be %s{what}, and here %s{p.Name} is %s{DotNetInterop.showType args[i]}.")

        match own with
        | Some _ -> own
        | None -> List.tryPick checkType args
    | TCon(_, args)
    | TTuple args -> List.tryPick checkType args
    | TFun(ps, r, _) -> List.tryPick checkType (r :: ps)
    | _ -> None

// ---------------------------------------------------------------------------
// Constraints of generic Bjolang functions
// ---------------------------------------------------------------------------
//
// A generic Bjolang function is a generic C# method, and C# refuses one that
// uses its type parameter where a .NET generic constrains it, unless the
// method says so too: `PrimitiveArray<T_a>` needs `where T_a : struct,
// IEquatable<T_a>`. Bjolang's types do not say it, so it is found here, from
// what the function uses: the .NET generic types in its signature and its
// body, the generic .NET methods it calls, and the generic functions of this
// module it calls, whose own constraints it then has. The last is a
// fixpoint over the calls.
//
// A function of another module is not looked into. Its constraints are on
// its compiled method, and a call from a generic function here that needs
// them is still found by the C# compiler.

/// What a type parameter of a generic Bjolang function is required to be.
type Bound =
    | ValueType
    | ReferenceType
    | Constructor
    /// An interface or a base class, in the function's own type variables.
    | Implements of HMType

/// What `p` requires, with the generic parameters of its method and type
/// replaced by `args`, in Bjolang types.
let private boundsOf (p: Type) (args: Map<string, HMType>) : Bound list =
    let attrs = p.GenericParameterAttributes
    let has (flag: GenericParameterAttributes) = attrs &&& flag = flag

    let special =
        [ if has GenericParameterAttributes.NotNullableValueTypeConstraint then ValueType
          elif has GenericParameterAttributes.ReferenceTypeConstraint then ReferenceType
          if has GenericParameterAttributes.DefaultConstructorConstraint
             && not (has GenericParameterAttributes.NotNullableValueTypeConstraint) then
              Constructor ]

    let types =
        p.GetGenericParameterConstraints()
        |> Array.filter (fun c -> c <> typeof<ValueType>)
        |> Array.map (fun c -> Implements(substTypeVars args (DotNetInterop.mapClrType c)))
        |> Array.toList

    special @ types

/// The generic parameters of `p`'s method and type, by the name `mapClrType`
/// gives them, mapped to the arguments given for them.
let private argumentMap (parameters: Type[]) (args: HMType list) : Map<string, HMType> =
    Seq.zip parameters args |> Seq.map (fun (p, a) -> "'" + p.Name, a) |> Map.ofSeq

/// The bounds `t` puts on the type variables in `vars`, by variable: each
/// constrained .NET generic in it whose argument is one of them.
let rec private typeBounds (vars: Set<string>) (t: HMType) : (string * Bound) list =
    match t with
    | TCon(name, args) when not args.IsEmpty ->
        let own =
            match constrainedDefinition name args.Length with
            | None -> []
            | Some def ->
                let parameters = def.GetGenericArguments()
                let map = argumentMap parameters args

                Seq.zip parameters args
                |> Seq.toList
                |> List.collect (fun (p, a) ->
                    match a with
                    | TVar v when vars.Contains(Naming.typeParamKey v) ->
                        boundsOf p map |> List.map (fun b -> Naming.typeParamKey v, b)
                    | _ -> [])

        own @ List.collect (typeBounds vars) args
    | TCon(_, args)
    | TTuple args -> List.collect (typeBounds vars) args
    | TFun(ps, r, _) -> List.collect (typeBounds vars) (r :: ps)
    | _ -> []

/// The bounds a call of a generic .NET method at `typeArgs` puts on `vars`.
let private callBounds (vars: Set<string>) (declaringType: string) (name: string) (typeArgs: HMType list) =
    match genericMethods declaringType name typeArgs.Length with
    | m :: _ ->
        let parameters = m.GetGenericArguments()
        let map = argumentMap parameters typeArgs

        Seq.zip parameters typeArgs
        |> Seq.toList
        |> List.collect (fun (p, a) ->
            match a with
            | TVar v when vars.Contains(Naming.typeParamKey v) ->
                boundsOf p map |> List.map (fun b -> Naming.typeParamKey v, b)
            | _ -> [])
    | [] -> []

/// `t` with each type variable replaced by its entry in `m`, by key: a
/// variable may be written `'a` or `a`.
let rec private substituteKeys (m: Map<string, HMType>) (t: HMType) : HMType =
    match t with
    | TVar v ->
        match Map.tryFind (Naming.typeParamKey v) m with
        | Some x -> x
        | None -> t
    | TCon(n, args) -> TCon(n, List.map (substituteKeys m) args)
    | TTuple ts -> TTuple(List.map (substituteKeys m) ts)
    | TFun(ps, r, e) -> TFun(List.map (substituteKeys m) ps, substituteKeys m r, e)
    | other -> other

/// A generic function of the module: its type parameters, by key, and the
/// declaration.
type private Generic =
    { Params: string list
      Decl: TDecl }

let rec private generics (decls: TDecl list) : Map<string, Generic> =
    decls
    |> List.collect (fun d ->
        match d with
        | TModule(_, inner, _) -> generics inner |> Map.toList
        | TDefun(name, tyArgs, _, _, _, _, _, _, _) when not tyArgs.IsEmpty ->
            [ name, { Params = tyArgs |> List.map Naming.typeParamKey; Decl = d } ]
        | _ -> [])
    |> Map.ofList

/// What a generic function requires of its own type parameters, before the
/// functions it calls are counted: from its signature, the types in its body
/// and the generic .NET methods it calls. And the generic functions of the
/// module it calls, with their type arguments.
let private direct (registry: TraitRegistry) (all: Map<string, Generic>) (g: Generic) =
    let vars = Set.ofList g.Params
    let bounds = ResizeArray<string * Bound>()
    let calls = ResizeArray<string * HMType list>()

    match g.Decl with
    | TDefun(_, _, args, kwArgs, restArg, retType, _, body, _) ->
        for t in retType :: List.map snd args @ List.map (fun (_, t, _) -> t) kwArgs @ (restArg |> Option.map snd |> Option.toList) do
            bounds.AddRange(typeBounds vars (Unification.prune registry t))

        let rec walk (e: TypedExpr) =
            bounds.AddRange(typeBounds vars (Unification.prune registry e.Type))

            match e.Node with
            | TForeignStaticCall(clrType, name, _, Some meta) when not meta.TypeArguments.IsEmpty ->
                bounds.AddRange(callBounds vars clrType name (meta.TypeArguments |> List.map (Unification.prune registry)))
            | TDotMethodCall(_, name, _, Some meta) when not meta.TypeArguments.IsEmpty ->
                bounds.AddRange(callBounds vars meta.DeclaringType name (meta.TypeArguments |> List.map (Unification.prune registry)))
            | TIdent(name, tArgs) when not tArgs.IsEmpty && all.ContainsKey name ->
                calls.Add(name, tArgs |> List.map (Unification.prune registry))
            | _ -> ()

            TypeVisitor.children e |> List.iter walk

        walk body
    | _ -> ()

    List.ofSeq bounds, List.ofSeq calls

/// For each generic function of the module that has any: what its type
/// parameters are required to be, by parameter key.
let infer (registry: TraitRegistry) (decls: TDecl list) : Map<string, (string * Bound) list> =
    let all = generics decls
    let directs = all |> Map.map (fun _ g -> direct registry all g)

    // A call to `callee` at `tArgs` gives the caller each bound of the callee
    // whose parameter it passes one of its own type variables for.
    let inherited (caller: Generic) (known: Map<string, (string * Bound) list>) (callee: string) (tArgs: HMType list) =
        let calleeParams = all[callee].Params
        let vars = Set.ofList caller.Params

        if calleeParams.Length <> tArgs.Length then
            []
        else
            let byKey = List.zip calleeParams tArgs |> Map.ofList

            Map.tryFind callee known
            |> Option.defaultValue []
            |> List.choose (fun (p, b) ->
                match Map.tryFind p byKey with
                | Some(TVar v) when vars.Contains(Naming.typeParamKey v) ->
                    let b =
                        match b with
                        | Implements t -> Implements(substituteKeys byKey t)
                        | other -> other

                    Some(Naming.typeParamKey v, b)
                | _ -> None)

    let rec fixpoint (known: Map<string, (string * Bound) list>) =
        let next =
            all
            |> Map.map (fun name g ->
                let own, calls = directs[name]

                own @ (calls |> List.collect (fun (callee, tArgs) -> inherited g known callee tArgs))
                |> List.distinct)

        if next = known then known else fixpoint next

    fixpoint (directs |> Map.map (fun _ (own, _) -> List.distinct own))
    |> Map.filter (fun _ bounds -> not bounds.IsEmpty)

/// A bound as a reader of an error message writes it.
let showBound (b: Bound) : string =
    match b with
    | ValueType -> "a value type (struct)"
    | ReferenceType -> "a reference type (class)"
    | Constructor -> "a type with a public constructor of no parameters (new())"
    | Implements t -> DotNetInterop.showType t

/// Whether the .NET type `arg` meets `b`, with the function's type variables
/// in `b` replaced by `args`, by key. True when it cannot be told.
let private meets (args: Map<string, HMType>) (arg: Type) (b: Bound) : bool =
    match b with
    | ValueType -> arg.IsValueType && isNull (Nullable.GetUnderlyingType arg)
    | ReferenceType -> not arg.IsValueType
    | Constructor -> arg.IsValueType || not (isNull (arg.GetConstructor Type.EmptyTypes))
    | Implements t ->
        match DotNetInterop.tryClrTypeOf (substituteKeys args t) with
        | Some c -> c.IsAssignableFrom arg
        | None -> true

/// A call of the generic function `name` at `tArgs`, checked against what it
/// requires. None when it meets it or when that cannot be told.
let private checkGenericCall
    (registry: TraitRegistry)
    (inferred: Map<string, (string * Bound) list>)
    (paramsOf: Map<string, string list>)
    (name: string)
    (tArgs: HMType list)
    : string option =
    match Map.tryFind name inferred, Map.tryFind name paramsOf with
    | Some bounds, Some ps when ps.Length = tArgs.Length ->
        let pruned = tArgs |> List.map (Unification.prune registry)
        let byKey = List.zip ps pruned |> Map.ofList

        bounds
        |> List.tryPick (fun (p, b) ->
            let arg = byKey[p]

            match DotNetInterop.tryClrTypeOf arg with
            | Some clr when not (meets byKey clr b) ->
                Some
                    $"'%s{name}' needs its type variable %%%s{p} to be %s{showBound b}, for the .NET generics it uses %%%s{p} with, and here %%%s{p} is %s{DotNetInterop.showType arg}."
            | _ -> None)
    | _ -> None

/// The first type parameter of a generic function that is required to be a
/// value type and a reference type at once, which no type is.
let private conflict (bounds: (string * Bound) list) : string option =
    bounds
    |> List.groupBy fst
    |> List.tryPick (fun (p, group) ->
        let bs = List.map snd group

        if List.contains ValueType bs && List.contains ReferenceType bs then Some p else None)

// ---------------------------------------------------------------------------
// The pass
// ---------------------------------------------------------------------------

let private refuse (r: Range) (message: string) : 'a =
    failwithf
        $"Type Error at %s{formatPos r}: %s{message}\n  .NET requires this of the generic, and Bjolang's types do not say it, so it is checked once the types are known."

/// What the generic functions of the module require, for the calls of them.
type private Known =
    { Inferred: Map<string, (string * Bound) list>
      Params: Map<string, string list> }

let rec private check (registry: TraitRegistry) (known: Known) (seen: ConcurrentDictionary<string, unit>) (expr: TypedExpr) : unit =
    let t = Unification.prune registry expr.Type

    // A type is checked once: the same one is on many nodes.
    let key = DotNetInterop.showType t

    if seen.TryAdd(key, ()) then
        checkType t |> Option.iter (refuse expr.Range)

    match expr.Node with
    | TForeignStaticCall(clrType, name, _, Some meta) when not meta.TypeArguments.IsEmpty ->
        checkCall registry clrType name meta.TypeArguments |> Option.iter (refuse expr.Range)
    | TDotMethodCall(_, name, _, Some meta) when not meta.TypeArguments.IsEmpty ->
        checkCall registry meta.DeclaringType name meta.TypeArguments |> Option.iter (refuse expr.Range)
    | TIdent(name, tArgs) when not tArgs.IsEmpty ->
        checkGenericCall registry known.Inferred known.Params name tArgs |> Option.iter (refuse expr.Range)
    | _ -> ()

    TypeVisitor.children expr |> List.iter (check registry known seen)

/// The types a declaration states rather than an expression has: a function's
/// parameters and result, and a module-level value's type.
let rec private declaredTypes (decl: TDecl) : (HMType * Range) list =
    match decl with
    | TDefun(_, _, args, kwArgs, restArg, retType, _, _, r) ->
        (retType :: List.map snd args
         @ List.map (fun (_, t, _) -> t) kwArgs
         @ (restArg |> Option.map snd |> Option.toList))
        |> List.map (fun t -> t, r)
    | TDef(_, _, t, r)
    | TDefMutable(_, _, t, r) -> [ t, r ]
    | _ -> []

let rec private runIn (registry: TraitRegistry) (known: Known) (decls: TDecl list) : unit =
    for d in decls do
        match d with
        | TModule(_, inner, _) -> runIn registry known inner
        | TImpl(_, _, _, _, _, _, methods, _) -> runIn registry known methods
        | _ ->
            Diagnostics.recover "constraint" (Some(tdeclRange d)) () (fun () ->
                match d with
                | TDefun(name, _, _, _, _, _, _, _, r) ->
                    Map.tryFind name known.Inferred
                    |> Option.bind conflict
                    |> Option.iter (fun p ->
                        refuse
                            r
                            $"'%s{name}' uses its type variable %%%s{p} with .NET generics that need it to be a value type (struct) and with ones that need it to be a reference type (class), and no type is both.")
                | _ -> ()

                for (t, r) in declaredTypes d do
                    checkType (Unification.prune registry t) |> Option.iter (refuse r)

                let seen = ConcurrentDictionary<string, unit>()
                TypeVisitor.mapDecl (fun e -> check registry known seen e; e) d |> ignore)

/// Reports the first broken constraint of each declaration.
let run (registry: TraitRegistry) (decls: TDecl list) : unit =
    let known =
        { Inferred = infer registry decls
          Params = generics decls |> Map.map (fun _ g -> g.Params) }

    runIn registry known decls
