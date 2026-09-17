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

module Bjolang.ForeignTyping

open Bjolang.Lexer
open Bjolang.Ast
open Bjolang.TypedAST
open Bjolang.Unification
open Bjolang.TypeEnv
open Bjolang.Annotations
open Bjolang.Traits

/// The type of a foreign call once its declared exceptions are accounted for.
///
/// Without `#:exceptions` the call has the type the .NET member has, and
/// anything it throws propagates. With it, the call cannot fail *in the ways
/// that were listed* — those become `Err` — and everything else still
/// propagates. That asymmetry is the whole design: an exception nobody named is
/// a bug, not a value.
///
/// `void` is replaced by the unit tuple on the way in. C# has no `Result<E,
/// void>` and never will, and `()` is exactly what Bjolang means by a call
/// performed for its effect.
let wrapForeignExceptions (exceptions: string list) (retType: HMType) : HMType =
    if List.isEmpty exceptions then
        retType
    else
        let okType =
            if retType = TypeConstants.voidType then TTuple [] else retType

        TCon("Result", [ TCon("System.Exception", []); okType ])

/// Checks that everything named by `#:exceptions` is in fact an exception type.
///
/// The list drives a C# `catch ... when (ex is E1 || ...)` filter, and a name
/// that is not an exception type produces C# that does not compile — reported
/// against generated code rather than against the import that caused it.
let internal checkExceptionTypes (where: string) (exceptions: string list) : unit =
    for name in exceptions do
        let t = DotNetInterop.resolveType $" at %s{where}" name

        if not (typeof<System.Exception>.IsAssignableFrom t) then
            failwithf
                $"Type Error at %s{where}: '%s{name}' is named in #:exceptions but does not derive from System.Exception."

/// What an `import/class` spec declares, validated: the alias, its parameters,
/// and the .NET type it stands for.
///
/// Split out because the *signature* pre-pass needs the alias too. Signatures
/// are read before any declaration is checked, so `(: open-input-file (-> string
/// (Result Exception TextInputPort)))` was resolved with no idea that
/// `Exception` had been imported, and took it for a constructor of that name.
/// The definition's own body was checked against the annotation resolved again
/// in place, where the import had landed — so only a reference *above* the
/// definition saw the other reading, and the two met as `System.Exception`
/// against `Exception`: one type failing to match itself.
///
/// `CtorType` is left `None`. A constructor signature is written in terms of
/// the alias it is declaring, so it can only be resolved once the alias is a
/// type — which is the second pass in `DImportClass`, and is nothing a
/// signature elsewhere can name.
let internal classInfoOfSpec (spec: ClassImportSpec) : ClrClassInfo =
    let where = Lexer.formatPos spec.Range
    // A generic type is spelled with its arity — `Set.Set`1` — but written in
    // source without one, because the alias says how many arguments it takes.
    // Both spellings are tried so that a clause reads the way C# does.
    /// The generic definition of this name at *some* arity.
    ///
    /// Asked when the bare name does not resolve, so that a generic type
    /// written without its parameters is reported as the type constructor it is
    /// rather than as a name that does not exist. Eight is past every arity in
    /// the BCL that anyone imports.
    let genericAtAnyArity () =
        [ 1..8 ]
        |> List.tryPick (fun n -> DotNetInterop.tryResolveType $"%s{spec.ClrClass}`%d{n}")

    let clrType =
        if spec.TypeParams.IsEmpty then
            match DotNetInterop.tryResolveType spec.ClrClass with
            | Some t -> t
            | None ->
                match genericAtAnyArity () with
                | Some t -> t
                | None -> DotNetInterop.resolveType $" at %s{where}" spec.ClrClass
        else
            let arityName = $"%s{spec.ClrClass}`%d{spec.TypeParams.Length}"

            match DotNetInterop.tryResolveType arityName with
            | Some t -> t
            | None ->
                match genericAtAnyArity () with
                | Some t -> t
                | None -> DotNetInterop.resolveType $" at %s{where}" spec.ClrClass

    // The two halves of the same claim: a generic type has to be imported
    // applied, and an ordinary one cannot be.
    if clrType.IsGenericTypeDefinition && spec.TypeParams.IsEmpty then
        let arity = clrType.GetGenericArguments().Length
        let written = List.init arity (fun i -> "%" + string (char (int 'a' + i))) |> String.concat " "

        failwithf
            $"Type Error at %s{where}: '%s{spec.ClrClass}' is a generic type taking %d{arity} argument(s), so it is a type constructor rather than a type. Import it applied to its parameters: ((%s{spec.Alias} %s{written}) (: %s{spec.ClrClass}))."

    if not clrType.IsGenericTypeDefinition && not spec.TypeParams.IsEmpty then
        failwithf
            $"Type Error at %s{where}: '%s{spec.ClrClass}' is not a generic type, so '%s{spec.Alias}' takes no type parameters. Write the alias bare."

    if clrType.IsGenericTypeDefinition
       && clrType.GetGenericArguments().Length <> spec.TypeParams.Length then
        let arity = clrType.GetGenericArguments().Length

        failwithf
            $"Type Error at %s{where}: '%s{spec.ClrClass}' takes %d{arity} type argument(s), but '%s{spec.Alias}' was declared with %d{spec.TypeParams.Length}."

    if clrType.IsGenericTypeDefinition && not spec.Exceptions.IsEmpty then
        failwithf
            $"Type Error at %s{where}: #:exceptions describes the constructor, and a generic class is imported as a type only. Reach its constructor through a static factory imported with import/extern."

    checkExceptionTypes where spec.Exceptions

    { Alias = spec.Alias
      TypeParams = spec.TypeParams
      // Without the arity mark, which is what a Bjolang type constructor
      // carries in the number of arguments it is applied to — and what the code
      // generator emits before its angle brackets.
      ClrName = DotNetInterop.clrTypeName clrType
      CtorType = None
      CtorExceptions = spec.Exceptions }

/// The alias an imported class contributes to a registry: the name, and the
/// .NET type it expands to.
///
/// A generic import becomes a type *alias with parameters*, which the
/// annotation resolver already knows how to expand: `(Set int)` substitutes
/// into `Set.Set<int>` the same way a hand-written `(type (: (Pair %a) ...))`
/// does. The bare alias of an ordinary class is the arity-zero case of it.
let internal classAliasTarget (info: ClrClassInfo) : HMType =
    TCon(info.ClrName, info.TypeParams |> List.map (fun p -> TVar("'" + p)))

/// The same signature, with every `-?->` parameter fixed at the suspending
/// colour — what the suspending half of a pair is checked and emitted against.
///
/// Only a parameter that is *itself* `-?->` is repainted. That is the whole of
/// what the parser lets through: an arrow nested inside a container is refused,
/// so there is no deeper case to reach.
///
/// Two callers, and they are the two ways a pair comes about.
/// `expandPolymorphicDefuns` generates the second body from the first, and
/// `defbjouble` has it written by hand — but both need the *same* repainting,
/// or the hand-written one is emitted taking a `Func<A,B>` while its body
/// awaits it.
let suspendingSignature (ftype: FType) : FType =
    let repaint (t: FType) =
        match t with
        | TApp("-?->", args, r) -> TApp("-bjo->", args, r)
        | other -> other

    match ftype with
    | TArrow(mandatory, keywords, restOpt, ret, colour, r) ->
        TArrow(
            mandatory |> List.map repaint,
            keywords |> List.map (fun (n, t) -> n, repaint t),
            restOpt |> Option.map repaint,
            ret,
            colour,
            r
        )
    | other -> other

/// The .NET type a receiver expression has, or a diagnostic saying why not.
let internal receiverClrType (where: string) (form: string) (targetType: HMType) : System.Type =
    // `(.ToString 5)` — a receiver is a place a type has to be concrete now.
    settleLiterals [ targetType ]

    if DotNetInterop.isUnresolved targetType then
        failwithf
            $"Type Error at %s{where}: the type of the receiver of '%s{form}' is not known here. A .NET member is resolved at compile time, so the receiver's type has to be pinned down first — annotate it, or bind it with a signature."

    match DotNetInterop.tryClrTypeOf targetType with
    | Some t -> t
    | None ->
        let shown = DotNetInterop.showType targetType

        failwithf
            $"Type Error at %s{where}: '%s{form}' needs a .NET receiver, but its target has the Bjolang type %s{shown}, which is not a .NET class."

/// Unifies a foreign member's parameter types into the argument types.
///
/// This is what makes reflection *drive* inference rather than merely check it:
/// an argument whose type was still open is pinned to the parameter type of the
/// overload that was selected.
///
/// Only sound where every argument is expected to match a parameter *exactly*,
/// which is true of the one caller left: a declared `import/extern` signature,
/// checked against the overload reflection chose for it. A call site goes
/// through `reconcileForeignArgs` instead, an argument there being allowed to
/// fit by conversion.
let internal unifyForeignArgs (registry: TraitRegistry) (argTypes: HMType list) (paramTypes: HMType list) =
    List.iter2 (unify registry) argTypes paramTypes

/// Reconciles the arguments of a foreign call with the parameters of the
/// overload that was selected for it, one argument at a time.
///
/// `DotNetInterop.scoreArgument` accepts an argument that fits by an implicit
/// conversion — a numeric widening, a reference upcast, a box — and ranks it
/// below an exact match so that an exact one always wins. Unifying every
/// argument against its parameter afterwards threw that away: unification is
/// nominal equality, Bjolang having no subtyping, so an `int` argument reaching
/// a `double` parameter failed with a diagnostic naming a type the caller never
/// wrote. Widenings were selectable and then unusable, and `(sin 1)` was an
/// error while `(sin 1.0)` was not.
///
/// So the two cases are now told apart:
///
///   * An argument whose type is still **open** is unified, exactly as before.
///     That is the case the docstring above describes and the one that lets
///     reflection settle a type inference has not.
///
///   * An argument whose type is **known** is left alone, and converted where
///     it differs. Nothing is being weakened: the overload was chosen because
///     the argument fits by C#'s own conversion rules, and one that fits no
///     way at all was rejected as a candidate before ever getting here.
///
/// The conversion is *written into the tree* rather than left to the C# that
/// eventually reads it. It has to be. The code generator emits a foreign call
/// as its receiver, its name and its arguments, so C# resolves the overload a
/// second time from what it is given — and a call this pass typed by one
/// overload's return type must not be free to land on another's. `((double)(x))`
/// pins it to the method that was actually chosen.
let internal reconcileForeignArgs
    (registry: TraitRegistry)
    (typedArgs: TypedExpr list)
    (paramTypes: HMType list)
    : TypedExpr list =
    List.map2
        (fun (arg: TypedExpr) paramType ->
            let argType = prune registry arg.Type
            let paramType = prune registry paramType

            if not (List.isEmpty (freeVars registry argType)) then
                unify registry argType paramType
                arg
            elif argType = paramType then
                arg
            else
                { arg with
                    Type = paramType
                    Node = TCast(arg, paramType) })
        typedArgs
        paramTypes

let internal metadataOf (resolved: DotNetInterop.ResolvedCall) (exceptions: string list) : DotNetMethodMetadata =
    { DeclaringType = resolved.DeclaringType
      MethodName = resolved.Name
      ParameterTypes = resolved.ParameterTypes
      ReturnType = resolved.ReturnType
      // Overload resolution by argument type only ever selects a *constructed*
      // method, so there is nothing to write between angle brackets. A generic
      // import fills this in from its declared signature instead — see
      // `instantiateGenericExtern`.
      TypeArguments = []
      IsStatic = resolved.IsStatic
      Exceptions = exceptions
      // Ordinary calls, which are all of them but an `#:async` import's. The
      // async path builds on this and overrides both.
      Await = false
      AmbientToken = false
      // A direct `(.Method x)` reaches here, and there is no import clause it
      // could have carried a `#:blocking` claim on. An alias overrides this.
      Blocking = false }

/// One use of a generic extern alias: its parameter types, its return type and
/// its .NET type arguments, all instantiated at fresh metavariables.
///
/// *All* — that is the point of packing them into one type before instantiating.
/// The type arguments were solved at the import in terms of the signature's own
/// variables, so instantiating the two apart would hand the call one set of
/// metavariables for the arguments to settle and a different set to write
/// between the angle brackets. Packed, `%a` is one variable, the argument that
/// pins it pins the bracket too, and there is nothing left for C# to infer.
///
/// The receiver is included in the parameters, exactly as the signature writes
/// it: an instance member's alias is a function of its receiver.
let internal instantiateGenericExtern (registry: TraitRegistry) (where: string) (info: ClrExternInfo) =
    let declared =
        match info.DeclaredType with
        | Some t -> t
        | None ->
            failwithf
                $"Type Error at %s{where}: '%s{info.Alias}' names the generic method '%s{info.ClrType}.%s{info.MemberName}' and has no declared signature. A generic method's type arguments come from the signature, so it is the one kind of import that cannot do without one."

    let typeArgs = Option.defaultValue [] info.GenericTypeArgs
    let packed = TTuple(declared :: typeArgs)
    let vars = freeTVars registry packed |> List.distinct
    let instantiated, _, _ = instantiate registry (Scheme(vars, [], packed))

    match instantiated with
    | TTuple(TFun(paramTypes, retType, _) :: instantiatedArgs) -> paramTypes, retType, instantiatedArgs
    | _ ->
        failwithf
            $"Type Error at %s{where}: the declared signature of '%s{info.Alias}' is not a function type, so it cannot name a method."

/// What an `#:async` import's call resolves to.
///
/// Three things happen here that do not happen for an ordinary foreign call,
/// and all three are §7.2's rules rather than conveniences.
///
///   * **The token is threaded.** Nearly every async BCL method takes a
///     `CancellationToken` as a trailing parameter, so the overload is chosen
///     *with* one appended and the emitter fills it from the ambient token.
///     The alternative — make every caller pass it — is the parameter-through-
///     every-signature problem `current-cancel` exists to avoid, and a token
///     nobody remembers to pass is a `choose` that leaks work.
///
///   * **A method with no token overload has to say so.** `#:uncancellable` is
///     required rather than inferred, because "this cannot be stopped" is a
///     fact about a call that its reader needs and its writer knows.
///
///   * **The task is unwrapped.** `Task` is never a Bjolang type; the binding's
///     type is the task's result. There is nothing in the language that could
///     hold a `Task<T>` usefully — no `await` to spell, since suspension is
///     invisible at the call site.
///
/// The returned parameter list is what the *caller* wrote, with any threaded
/// token dropped: it is what the arguments are reconciled against and what a
/// declared signature is checked against.
/// Resolves an extern method call, on whichever half of the type it lives in.
///
/// Every caller below works in the method's *own* parameters: an instance
/// member's receiver has already been taken off the front, because it is the
/// alias's first argument and not one of the method's.
let internal resolveExternMethod
    (where: string)
    (info: ClrExternInfo)
    (clrType: System.Type)
    (argTypes: HMType list)
    : DotNetInterop.ResolvedCall =
    settleLiterals argTypes
    DotNetInterop.resolveMethod where (not info.IsInstance) clrType info.MemberName argTypes

/// Resolve an extern call that threads the ambient cancellation token.
///
/// Shared by `#:async` and `#:cancellable`, which want the same overload
/// selection and differ only in what happens to the return type. The token is
/// appended to the argument types so that the token-taking overload is the one
/// selected; the caller's own arguments are the prefix.
let internal resolveTokenThreadedExtern
    (where: string)
    (info: ClrExternInfo)
    (clrType: System.Type)
    (argTypes: HMType list)
    : DotNetInterop.ResolvedCall * bool =

    let wantsToken = not info.Uncancellable

    if
        wantsToken
        && DotNetInterop.hasTokenOverload (not info.IsInstance) clrType info.MemberName (Some(argTypes.Length + 1))
    then
        resolveExternMethod where info clrType (argTypes @ [ DotNetInterop.cancellationTokenType ]), true
    elif wantsToken && info.IsAsync then
        failwithf
            $"Type Error at %s{where}: '%s{info.ClrType}.%s{info.MemberName}' has no overload taking these %d{argTypes.Length} argument(s) and a System.Threading.CancellationToken, so the ambient cancellation token cannot be passed to it.\n  An async call that cannot be cancelled outlives the scope that asked for it: a losing choose stops listening, and the work carries on. If that is genuinely the case here, write #:uncancellable in the import/extern clause so that the fact is visible where it is decided."
    elif wantsToken then
        failwithf
            $"Type Error at %s{where}: '%s{info.ClrType}.%s{info.MemberName}' is imported #:cancellable, but it has no overload taking these %d{argTypes.Length} argument(s) and a System.Threading.CancellationToken. Leave #:cancellable off — the call takes no token and there is nothing to thread."
    else
        resolveExternMethod where info clrType argTypes, false

let internal resolveAsyncExtern
    (where: string)
    (info: ClrExternInfo)
    (clrType: System.Type)
    (argTypes: HMType list)
    : DotNetInterop.ResolvedCall * HMType list * HMType * bool =

    let resolved, threadsToken = resolveTokenThreadedExtern where info clrType argTypes

    let awaited =
        match DotNetInterop.awaitedResultType resolved.RawReturnType with
        | Some t -> t
        | None ->
            failwithf
                $"Type Error at %s{where}: '%s{info.ClrType}.%s{info.MemberName}' is imported #:async, but the overload selected here returns %s{resolved.RawReturnType.Name}, which is not a Task, a ValueTask or either of their generic forms. Leave #:async off to call it directly."

    let visibleParams =
        if threadsToken then
            resolved.ParameterTypes |> List.truncate (resolved.ParameterTypes.Length - 1)
        else
            resolved.ParameterTypes

    resolved, visibleParams, awaited, threadsToken

