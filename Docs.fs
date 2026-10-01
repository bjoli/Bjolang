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

/// `(:doc ...)`: reading the forms, and checking them against the definitions
/// they document. Docs/Documentation.org is the specification.
///
/// The forms never reach the parser. `Pipeline` takes them out of the module's
/// top-level forms once includes are spliced, and they are checked after type
/// checking, because what a name *is* — a function, a record, a trait method —
/// is read off the parsed declarations, and whether a name in `(see ...)` or a
/// type in `(raises ...)` exists is read off the environment.
module Bjolang.Docs

open System
open Bjolang.Lexer
open Bjolang.Ast
open Bjolang.TypedAST

// ---------------------------------------------------------------------------
// The form
// ---------------------------------------------------------------------------

/// What a doc documents.
type Subject =
    | Named of string
    /// `(:doc name #:reader ...)`: the reader extension `#name(...)`, whose
    /// name is in a namespace of its own.
    | Reader of string
    | ModuleDoc

type Clause =
    | Summary of string
    | Arg of string * string
    | Rest of string * string
    | Key of string * string
    | Returns of string
    | Raises of string * string
    | See of string list
    | Example of string
    | Reference of string
    | Field of string * string
    | Case of string * string
    /// The type parameter's name without its `%`, as a `TypeDef` holds it.
    | TParam of string * string
    | Form of SExpr
    | Literal of string list

type Entry =
    { Subject: Subject
      Clauses: (Clause * Range) list
      /// The form as written, which is what is published.
      Form: SExpr
      Range: Range }

let private clauseName (c: Clause) =
    match c with
    | Summary _ -> "summary"
    | Arg _ -> "arg"
    | Rest _ -> "rest"
    | Key _ -> "key"
    | Returns _ -> "returns"
    | Raises _ -> "raises"
    | See _ -> "see"
    | Example _ -> "example"
    | Reference _ -> "reference"
    | Field _ -> "field"
    | Case _ -> "case"
    | TParam _ -> "tparam"
    | Form _ -> "form"
    | Literal _ -> "literal"

/// The texts a clause holds, for the rule that none is empty.
let private clauseTexts (c: Clause) : string list =
    match c with
    | Summary t
    | Returns t
    | Example t
    | Reference t -> [ t ]
    | Arg(_, t)
    | Rest(_, t)
    | Key(_, t)
    | Raises(_, t)
    | Field(_, t)
    | Case(_, t)
    | TParam(_, t) -> [ t ]
    | See _
    | Form _
    | Literal _ -> []

let isDocForm (s: SExpr) =
    match s with
    | SList(SAtom { Token = Keyword "doc" } :: _, _) -> true
    | _ -> false

/// `(:doc #:file "path")`: the path as written, and where the form is.
let fileForm (s: SExpr) : (string * Range) option =
    match s with
    | SList([ SAtom { Token = Keyword "doc" }; SAtom { Token = Keyword "file" }; SAtom { Token = StringLit path } ], r) ->
        Some(path, r)
    | _ -> None

let private syntaxError (r: Range) (what: string) : 'a =
    failwithf $"Syntax error in (:doc ...) at %s{formatPos r}: %s{what}"

let private parseClause (c: SExpr) : Clause * Range =
    let r = getRange c

    let text (s: SExpr) (what: string) =
        match s with
        | SAtom { Token = StringLit t } -> t
        | other -> syntaxError (getRange other) $"%s{what} is a string."

    let name (s: SExpr) (what: string) =
        match s with
        | SAtom { Token = Symbol n } -> n
        | other -> syntaxError (getRange other) $"%s{what} is a name."

    match c with
    | SList(SAtom { Token = Symbol head } :: parts, _) ->
        let clause =
            match head, parts with
            | "summary", [ t ] -> Summary(text t "A summary")
            | "returns", [ t ] -> Returns(text t "What (returns ...) says")
            | "example", [ t ] -> Example(text t "An example")
            | "reference", [ t ] -> Reference(text t "A reference")
            | ("summary" | "returns" | "example" | "reference"), _ ->
                syntaxError r $"(%s{head} text) takes one string."
            | "arg", [ n; t ] -> Arg(name n "What (arg ...) documents", text t "An argument's documentation")
            | "rest", [ n; t ] -> Rest(name n "What (rest ...) documents", text t "A rest parameter's documentation")
            | "field", [ n; t ] -> Field(name n "What (field ...) documents", text t "A field's documentation")
            | "case", [ n; t ] -> Case(name n "What (case ...) documents", text t "A case's documentation")
            | ("arg" | "rest" | "field" | "case"), _ -> syntaxError r $"(%s{head} name text) takes a name and a string."
            | "key", [ n; t ] ->
                let k =
                    match n with
                    | SAtom { Token = Symbol k }
                    | SAtom { Token = Keyword k } -> k
                    | other -> syntaxError (getRange other) "What (key ...) documents is a keyword's name, as rng or #:rng."

                Key(k, text t "A keyword parameter's documentation")
            | "key", _ -> syntaxError r "(key name text) takes the keyword's name and a string."
            | "raises", [ n; t ] -> Raises(name n "What (raises ...) names", text t "When it raises")
            | "raises", _ -> syntaxError r "(raises Type text) takes an exception type and a string saying when."
            | "tparam", [ v; t ] ->
                match v with
                | SAtom { Token = QuotedSymbol a } -> TParam(a, text t "A type parameter's documentation")
                | other -> syntaxError (getRange other) "What (tparam ...) documents is a type parameter, written %a."
            | "tparam", _ -> syntaxError r "(tparam %a text) takes a type parameter and a string."
            | "see", [] -> syntaxError r "(see name ...) names at least one thing."
            | "see", names -> See(names |> List.map (fun n -> name n "What (see ...) names"))
            | "literal", [] -> syntaxError r "(literal name ...) names at least one symbol."
            | "literal", names -> Literal(names |> List.map (fun n -> name n "What (literal ...) names"))
            | "form", [ (SList _ as shape) ] -> Form shape
            | "form", _ -> syntaxError r "(form (name ...)) takes one form, written as a list."
            | other, _ ->
                syntaxError
                    r
                    $"(%s{other} ...) is not a doc clause. A doc takes summary, arg, rest, key, returns, raises, see, example, reference, field, case, tparam, form and literal."

        clause, r
    | other -> syntaxError (getRange other) "a doc's clauses are lists, such as (summary \"...\")."

/// One `(:doc ...)` form, read but not checked.
let parseEntry (s: SExpr) : Entry =
    match s with
    | SList(SAtom { Token = Keyword "doc" } :: rest, r) ->
        let subject, clauses =
            match rest with
            | SAtom { Token = Keyword "module" } :: clauses -> ModuleDoc, clauses
            | SAtom { Token = Symbol n } :: SAtom { Token = Keyword "reader" } :: clauses -> Reader n, clauses
            | SAtom { Token = Symbol n } :: clauses -> Named n, clauses
            | SAtom { Token = Keyword "file" } :: _ ->
                syntaxError r "(:doc #:file \"path\") takes one string, the path of a .bjodoc file."
            | [] ->
                syntaxError
                    r
                    "it documents nothing. Write (:doc name (summary \"...\") ...), or (:doc #:module ...) for the module."
            | other :: _ ->
                syntaxError (getRange other) "what follows :doc is the name it documents, #:module, or #:file."

        { Subject = subject
          Clauses = clauses |> List.map parseClause
          Form = s
          Range = r }
    | _ -> invalidArg (nameof s) "not a (:doc ...) form"

// ---------------------------------------------------------------------------
// What a name is
// ---------------------------------------------------------------------------

type private Kind =
    /// A `defun`, `defbjo` or `defbjouble`: the parameters have names.
    | KFunction of positional: string list * keywords: string list * rest: string option * ret: FType option * typeVars: string list
    /// A trait method, an `import/extern` binding, a `def` of a function type:
    /// the doc names the parameters. `None` is what cannot be known, which is
    /// everything about an `import/extern` written without a type.
    | KUnnamed of count: int option * keywords: string list option * hasRest: bool option * ret: FType option * typeVars: string list
    | KValue
    | KRecord of fields: string list * opaque: bool * typeArgs: string list
    | KUnion of cases: string list * opaque: bool * typeArgs: string list
    | KAlias of typeArgs: string list
    | KTrait of implementor: string
    | KMacro
    | KReader
    | KModule
    /// Something no doc here may document, and why.
    | KRefused of string

let private kindName (k: Kind) =
    match k with
    | KFunction _
    | KUnnamed _ -> "a function"
    | KValue -> "a value"
    | KRecord _ -> "a record"
    | KUnion _ -> "a union"
    | KAlias _ -> "a type alias"
    | KTrait _ -> "a trait"
    | KMacro -> "a macro"
    | KReader -> "a reader extension"
    | KModule -> "the module"
    | KRefused _ -> "nothing"

let private allowedClauses (k: Kind) : string list =
    let common = [ "summary"; "see"; "example"; "reference" ]

    match k with
    | KFunction _
    | KUnnamed _ -> common @ [ "arg"; "rest"; "key"; "returns"; "raises"; "tparam" ]
    | KValue
    | KModule -> common
    | KRecord _ -> common @ [ "field"; "tparam" ]
    | KUnion _ -> common @ [ "case"; "tparam" ]
    | KAlias _
    | KTrait _ -> common @ [ "tparam" ]
    | KMacro
    | KReader -> common @ [ "form"; "arg"; "rest"; "literal" ]
    | KRefused _ -> []

/// The type variables of a signature, without their `'`.
let rec private typeVarsOf (t: FType) : string list =
    match t with
    | TName(n, _) when n.StartsWith "'" -> [ n.Substring 1 ]
    | TName _ -> []
    | TApp(head, args, _) ->
        (if head.StartsWith "'" then [ head.Substring 1 ] else []) @ List.collect typeVarsOf args
    | TArrow(mandatory, keywords, rest, ret, _, _) ->
        List.collect typeVarsOf mandatory
        @ List.collect (snd >> typeVarsOf) keywords
        @ (rest |> Option.map typeVarsOf |> Option.defaultValue [])
        @ typeVarsOf ret
    |> List.distinct

let private isVoid (t: FType) =
    match t with
    | TName(("void" | "Unit" | "unit"), _) -> true
    | _ -> false

/// A function named by a type alone.
let private unnamedOf (t: FType) : Kind option =
    match t with
    | TArrow(mandatory, keywords, rest, ret, _, _) ->
        Some(KUnnamed(Some mandatory.Length, Some(List.map fst keywords), Some rest.IsSome, Some ret, typeVarsOf t))
    | _ -> None

/// What each name this module defines is, read off its declarations.
let private kindsOf (decls: Decl list) : Map<string, Kind> * Set<string> =
    let signatures =
        decls
        |> List.choose (function
            | DSignature(n, t, _, _) -> Some(n, t)
            | _ -> None)
        |> Map.ofList

    let typeVarsOfSig n =
        Map.tryFind n signatures |> Option.map typeVarsOf |> Option.defaultValue []

    let retOfSig n =
        match Map.tryFind n signatures with
        | Some(TArrow(_, _, _, ret, _, _)) -> Some ret
        | _ -> None

    let functionOf n (args: DefunArg list) =
        KFunction(
            mandatoryNames args,
            args |> List.choose (function KeywordArg(k, _) -> Some k | _ -> None),
            args |> List.tryPick (function RestArg r -> Some r | _ -> None),
            retOfSig n,
            typeVarsOfSig n
        )

    let valueOf n =
        match Map.tryFind n signatures |> Option.bind unnamedOf with
        | Some k -> k
        | None -> KValue

    let macros =
        decls
        |> List.choose (function
            | DMacro(n, _)
            | DPatternMacro(n, _) -> Some n
            | _ -> None)
        |> Set.ofList

    let readers =
        decls |> List.choose (function DHashMacro(n, _) -> Some n | _ -> None) |> Set.ofList

    // In order of precedence, lowest first: `Map.ofList` keeps the last, and a
    // macro's transformer is a `defun` of the macro's name.
    let found =
        decls
        |> List.collect (function
            | DDef(n, _, _)
            | DDefMutable(n, _, _) -> [ n, valueOf n ]
            | DDefTuple(ns, _, _) -> ns |> List.map (fun n -> n, valueOf n)
            | DDefPattern(p, _, _) -> patternBinders p |> List.map (fun n -> n, valueOf n)
            | DDefun(n, args, _, _, _)
            | DDefDouble(n, args, _, _, _) when not (n.StartsWith "#") -> [ n, functionOf n args ]
            | DImportExtern(specs, _) ->
                specs
                |> List.map (fun spec ->
                    let kind =
                        match spec.ExplicitType with
                        | Some t -> unnamedOf t |> Option.defaultValue KValue
                        | None when spec.IsGet -> KValue
                        | None -> KUnnamed(None, None, None, None, [])

                    spec.Alias, kind)
            | DImportClass(specs, _) ->
                specs
                |> List.map (fun spec -> spec.Alias, KAlias(spec.TypeParams |> List.map (fun p -> p.TrimStart('\'', '%'))))
            | DType(tds, _)
            | DTypeRec(tds, _) ->
                tds
                |> List.collect (fun td ->
                    let own =
                        match td.Kind with
                        | Record(fields, _) ->
                            KRecord(fields |> List.map (fun f -> f.Name), td.IsOpaque, td.TypeArgs)
                        | Union cases ->
                            let names =
                                cases
                                |> List.map (function
                                    | SimpleCase(c, _)
                                    | DataCase(c, _, _, _) -> c)

                            KUnion(names, td.IsOpaque, td.TypeArgs)
                        | Alias _ -> KAlias td.TypeArgs

                    let cases =
                        match td.Kind with
                        | Union cases ->
                            cases
                            |> List.map (function
                                | SimpleCase(c, _)
                                | DataCase(c, _, _, _) ->
                                    c,
                                    KRefused
                                        $"%s{c} is a case of the union %s{td.Name}, and is documented in that union's doc: (case %s{c} \"...\").")
                        | _ -> []

                    cases @ [ td.Name, own ])
            | DTrait(n, implementor, _, _, sigs, _, _, _) ->
                (n, KTrait implementor)
                :: (sigs |> List.map (fun (m, t, _) -> m, unnamedOf t |> Option.defaultValue KValue))
            | DAlias(newName, oldName, _) ->
                [ newName,
                  KRefused $"%s{newName} is a second name for %s{oldName}, made with (:alias ...). Document %s{oldName}." ]
            | DReExport(names, _) ->
                names
                |> List.map (fun n ->
                    n, KRefused $"%s{n} is re-exported from the module that defines it, and its doc is there.")
            | _ -> [])

    let withMacros = found @ (macros |> Set.toList |> List.map (fun n -> n, KMacro))
    Map.ofList withMacros, readers

// ---------------------------------------------------------------------------
// Checking
// ---------------------------------------------------------------------------

let private reportAt (r: Range) (message: string) =
    Diagnostics.record
        { Severity = Diagnostics.Severity.Error
          Message = $"Documentation error at %s{formatPos r}: %s{message}"
          Where = None
          Phase = "doc" }

let private candidateKeys (env: Env) (moduleKey: string) (name: string) =
    [ name; TypeEnv.originalName env.Registry name; Naming.typeKey moduleKey name ] |> List.distinct

let private typeKnown (env: Env) (moduleKey: string) (name: string) =
    let reg = env.Registry

    candidateKeys env moduleKey name
    |> List.exists (fun k ->
        Map.containsKey k reg.Records
        || Map.containsKey k reg.Unions
        || Map.containsKey k reg.Aliases
        || Map.containsKey k reg.ClrClasses
        || Map.containsKey k reg.Traits
        || Set.contains k reg.LocalTypes)

let private caseKnown (env: Env) (name: string) =
    env.Registry.Unions
    |> Map.exists (fun _ (_, cases) -> cases |> List.exists (fun (c, _, _) -> c = name))

let private clrTypeKnown (name: string) =
    name.Contains '.' && (DotNetInterop.tryResolveType name).IsSome

/// Whether `(raises name ...)` names an exception type: a .NET type deriving
/// from `System.Exception`, by its full name or by an `import/class` alias.
let private exceptionProblem (env: Env) (moduleKey: string) (name: string) : string option =
    let clrName =
        candidateKeys env moduleKey name
        |> List.tryPick (fun k -> Map.tryFind k env.Registry.ClrClasses)
        |> Option.map (fun info -> info.ClrName)
        |> Option.defaultValue name

    match DotNetInterop.tryResolveType clrName with
    | None ->
        Some
            $"(raises %s{name} ...) names no .NET type. An exception is named in full, as System.ArgumentException, or by an import/class alias."
    | Some t when typeof<Exception>.IsAssignableFrom t -> None
    | Some _ -> Some $"(raises %s{name} ...) names a type that does not derive from System.Exception."

/// The names a macro's forms use as variables: those followed by `...`, which
/// are documented with `rest`, and the others, documented with `arg`. A
/// symbol listed in `literal` is syntax, not a variable.
let private formVariables (head: string) (literals: Set<string>) (forms: SExpr list) : Set<string> * Set<string> =
    let args = Collections.Generic.HashSet<string>()
    let rests = Collections.Generic.HashSet<string>()

    let isVar (v: string) =
        v <> head && not (Set.contains v literals)

    let rec walk (items: SExpr list) =
        match items with
        | SAtom { Token = Symbol v } :: SAtom { Token = Spread } :: more ->
            if isVar v then rests.Add v |> ignore
            walk more
        | SAtom { Token = Symbol v } :: more ->
            if isVar v then args.Add v |> ignore
            walk more
        | SList(inner, _) :: more ->
            walk inner
            walk more
        | _ :: more -> walk more
        | [] -> ()

    for f in forms do
        match f with
        | SList(_ :: items, _) -> walk items
        | _ -> ()

    Set.ofSeq args, Set.ofSeq rests

let private subjectText (s: Subject) =
    match s with
    | Named n -> n
    | Reader n -> n + " #:reader"
    | ModuleDoc -> "#:module"

let private listed (names: string list) =
    names |> String.concat ", "

/// Checks one doc against what it documents, reporting every problem found.
let private checkEntry (env: Env) (moduleKey: string) (localNames: Set<string>) (name: string) (kind: Kind) (entry: Entry) =
    let doc = $"(:doc %s{subjectText entry.Subject})"
    let err (r: Range) (message: string) = reportAt r message
    let clauses = entry.Clauses
    let allowed = allowedClauses kind

    // Clauses this kind does not take.
    for (c, r) in clauses do
        let cn = clauseName c

        if not (List.contains cn allowed) then
            let what =
                match kind with
                | KModule -> "this is the module's doc"
                | _ -> $"%s{name} is %s{kindName kind}"

            err r $"%s{doc} has (%s{cn} ...), and %s{what}. Its doc takes %s{listed allowed}."

    // What every doc has.
    let summaries = clauses |> List.choose (function (Summary t, r) -> Some(t, r) | _ -> None)

    match summaries with
    | [] -> err entry.Range $"%s{doc} has no summary. Every doc has one: (summary \"...\"), a line saying what it is."
    | (t, r) :: more ->
        if t.Contains '\n' then
            err r $"%s{doc} has a summary of more than one line. A summary is the line lists and hovers show; put the rest in (reference ...)."

        for (_, r2) in more do
            err r2 $"%s{doc} has a second (summary ...)."

    for once in [ "returns"; "reference" ] do
        match clauses |> List.filter (fun (c, _) -> clauseName c = once) with
        | _ :: (_, r) :: _ -> err r $"%s{doc} has a second (%s{once} ...)."
        | _ -> ()

    for (c, r) in clauses do
        if clauseTexts c |> List.exists String.IsNullOrWhiteSpace then
            err r $"%s{doc} has an empty (%s{clauseName c} ...). Documentation says something."

    for (c, r) in clauses do
        match c with
        | See names ->
            for n in names do
                let known =
                    Map.containsKey n env.Bindings
                    || Set.contains n env.TraitMethodNames
                    || Set.contains n localNames
                    || Macro.isMacro n
                    || Macro.isPatternMacro n
                    || Macro.isHashMacro n
                    || typeKnown env moduleKey n
                    || caseKnown env n
                    || clrTypeKnown n

                if not known then
                    err r $"%s{doc} has (see ... %s{n} ...), and nothing named %s{n} is in scope here."
        | Raises(t, _) ->
            match exceptionProblem env moduleKey t with
            | Some problem -> err r $"%s{doc}: %s{problem}"
            | None -> ()
        | _ -> ()

    // Names documented twice, by clause.
    let named (pick: Clause -> string option) =
        clauses |> List.choose (fun (c, r) -> pick c |> Option.map (fun n -> n, r))

    let once (what: string) (items: (string * Range) list) =
        items
        |> List.groupBy fst
        |> List.iter (fun (n, group) ->
            match group with
            | _ :: (_, r) :: _ -> err r $"%s{doc} documents %s{what} %s{n} twice."
            | _ -> ())

    let args = named (function Arg(n, _) -> Some n | _ -> None)
    let rests = named (function Rest(n, _) -> Some n | _ -> None)
    let keys = named (function Key(n, _) -> Some n | _ -> None)
    let tparams = named (function TParam(n, _) -> Some n | _ -> None)
    let fields = named (function Field(n, _) -> Some n | _ -> None)
    let cases = named (function Case(n, _) -> Some n | _ -> None)
    once "the argument" (args @ rests)
    once "the keyword" keys
    once "the type parameter" tparams
    once "the field" fields
    once "the case" cases

    let returns = clauses |> List.tryPick (function (Returns _, r) -> Some r | _ -> None)

    let checkTypeParams (known: string list) =
        for (v, r) in tparams do
            if not (List.contains v known) then
                let has =
                    let spelled = known |> List.map (fun k -> "%" + k) |> listed
                    if known.IsEmpty then "It has none." else $"Its type parameters are %s{spelled}."

                err r $"%s{doc} documents the type parameter %%%s{v}, and %s{name} has no %%%s{v}. %s{has}"

    // A `rest` after every `arg`: the order a call writes them in.
    let restBeforeArg () =
        let indexed = clauses |> List.indexed
        let lastArg = indexed |> List.tryFindBack (fun (_, (c, _)) -> match c with Arg _ -> true | _ -> false)
        let firstRest = indexed |> List.tryFind (fun (_, (c, _)) -> match c with Rest _ -> true | _ -> false)

        match lastArg, firstRest with
        | Some(ai, _), Some(ri, (_, r)) when ri < ai ->
            err r $"%s{doc} documents its rest parameter before an argument. The rest parameter comes last, as it does in a call."
        | _ -> ()

    let checkReturns (ret: FType option) =
        match ret, returns with
        | Some t, None when not (isVoid t) ->
            err entry.Range $"%s{doc} does not say what %s{name} returns: (returns \"...\")."
        | Some t, Some r when isVoid t -> err r $"%s{doc} has (returns ...), and %s{name} returns nothing."
        | _ -> ()

    let checkKeys (known: string list option) =
        match known with
        | None -> ()
        | Some ks ->
            for (k, r) in keys do
                if not (List.contains k ks) then
                    let has =
                        let spelled = ks |> List.map (fun k -> "#:" + k) |> listed
                        if ks.IsEmpty then "It takes none." else $"Its keyword parameters are %s{spelled}."

                    err r $"%s{doc} documents (key %s{k} ...), and %s{name} takes no #:%s{k}. %s{has}"

    match kind with
    | KFunction(positional, keywords, rest, ret, typeVars) ->
        for (n, r) in args do
            if Some n = rest then
                err r $"%s{doc} documents (arg %s{n} ...), and %s{n} is the rest parameter: (rest %s{n} \"...\")."
            elif List.contains n keywords then
                err r $"%s{doc} documents (arg %s{n} ...), and %s{n} is a keyword parameter: (key %s{n} \"...\")."
            elif not (List.contains n positional) then
                let has =
                    if positional.IsEmpty then "It takes no positional arguments."
                    else $"Its arguments are %s{listed positional}."

                err r $"%s{doc} documents (arg %s{n} ...), and %s{name} has no argument %s{n}. %s{has}"

        for p in positional do
            if not (args |> List.exists (fun (n, _) -> n = p)) then
                err
                    entry.Range
                    $"%s{doc} does not document the argument %s{p}. Every argument without a default is documented: (arg %s{p} \"...\")."

        // The documented arguments in the definition's order.
        let order =
            args
            |> List.map fst
            |> List.distinct
            |> List.choose (fun n -> List.tryFindIndex ((=) n) positional |> Option.map (fun i -> n, i))

        order
        |> List.pairwise
        |> List.tryFind (fun ((_, i), (_, j)) -> j < i)
        |> Option.iter (fun ((first, _), (second, _)) ->
            let r = args |> List.find (fun (n, _) -> n = first) |> snd
            err r $"%s{doc} documents %s{first} before %s{second}, and %s{name} takes %s{second} first.")

        match rest, rests with
        | Some rn, [] ->
            err entry.Range $"%s{doc} does not document the rest parameter %s{rn}: (rest %s{rn} \"...\")."
        | Some rn, (n, r) :: _ when n <> rn ->
            err r $"%s{doc} documents (rest %s{n} ...), and %s{name}'s rest parameter is %s{rn}."
        | None, (_, r) :: _ -> err r $"%s{doc} has (rest ...), and %s{name} has no rest parameter."
        | _ -> ()

        restBeforeArg ()
        checkKeys (Some keywords)
        checkReturns ret
        checkTypeParams typeVars

    | KUnnamed(count, keywords, hasRest, ret, typeVars) ->
        match count with
        | Some c when c <> args.Length ->
            let count (n: int) = if n = 1 then "1 argument" else $"%d{n} arguments"

            err
                entry.Range
                $"%s{doc} names %s{count args.Length} with (arg ...), and %s{name} takes %s{count c}. Its definition gives its arguments no names, so its doc names each of them, in order."
        | _ -> ()

        match hasRest, rests with
        | Some true, [] -> err entry.Range $"%s{doc} does not document %s{name}'s rest parameter: (rest name \"...\")."
        | Some false, (_, r) :: _ -> err r $"%s{doc} has (rest ...), and %s{name} has no rest parameter."
        | _ -> ()

        restBeforeArg ()
        checkKeys keywords
        checkReturns ret
        checkTypeParams typeVars

    | KRecord(recordFields, opaque, typeArgs) ->
        if opaque then
            for (_, r) in fields do
                err r $"%s{doc} has (field ...), and %s{name} is #:opaque: its fields are not part of what it offers."
        else
            for (f, r) in fields do
                if not (List.contains f recordFields) then
                    err r $"%s{doc} documents the field %s{f}, and %s{name} has no field %s{f}. Its fields are %s{listed recordFields}."

            for f in recordFields do
                if not (fields |> List.exists (fun (n, _) -> n = f)) then
                    err entry.Range $"%s{doc} does not document the field %s{f}. Every field of a record is documented: (field %s{f} \"...\")."

        checkTypeParams typeArgs

    | KUnion(unionCases, opaque, typeArgs) ->
        if opaque then
            for (_, r) in cases do
                err r $"%s{doc} has (case ...), and %s{name} is #:opaque: its cases are not part of what it offers."
        else
            for (c, r) in cases do
                if not (List.contains c unionCases) then
                    err r $"%s{doc} documents the case %s{c}, and %s{name} has no case %s{c}. Its cases are %s{listed unionCases}."

        checkTypeParams typeArgs

    | KAlias typeArgs -> checkTypeParams typeArgs

    | KTrait implementor -> checkTypeParams [ implementor ]

    | KMacro
    | KReader ->
        let forms = clauses |> List.choose (function (Form f, r) -> Some(f, r) | _ -> None)
        let literals = clauses |> List.collect (function (Literal ns, _) -> ns | _ -> []) |> Set.ofList

        if forms.IsEmpty then
            err entry.Range $"%s{doc} has no (form ...). A macro's doc says how it is written: (form (%s{name} ...))."

        for (f, r) in forms do
            match f with
            | SList(SAtom { Token = Symbol head } :: _, _) when head = name -> ()
            | _ -> err r $"%s{doc} has a form that does not begin with %s{name}. A form is written (%s{name} ...)."

        let argVars, restVars = formVariables name literals (List.map fst forms)
        let allVars = Set.union argVars restVars

        for l in literals do
            let appears =
                forms
                |> List.exists (fun (f, _) ->
                    let a, b = formVariables name Set.empty [ f ]
                    Set.contains l a || Set.contains l b)

            if not appears then
                let r = clauses |> List.pick (function (Literal ns, r) when List.contains l ns -> Some r | _ -> None)
                err r $"%s{doc} lists %s{l} as a literal, and no form has %s{l} in it."

        for (n, r) in args do
            if not (Set.contains n allVars) then
                err r $"%s{doc} documents (arg %s{n} ...), and no form of %s{name} has a variable %s{n}."
            elif Set.contains n restVars && not (Set.contains n argVars) then
                err r $"%s{doc} documents (arg %s{n} ...), and %s{n} is followed by ... in its form: (rest %s{n} \"...\")."

        for (n, r) in rests do
            if not (Set.contains n allVars) then
                err r $"%s{doc} documents (rest %s{n} ...), and no form of %s{name} has a variable %s{n}."
            elif not (Set.contains n restVars) then
                err r $"%s{doc} documents (rest %s{n} ...), and %s{n} is not followed by ... in any form: (arg %s{n} \"...\")."

        let documented = (args @ rests) |> List.map fst |> Set.ofList

        for v in allVars do
            if not (Set.contains v documented) then
                let clause = if Set.contains v restVars && not (Set.contains v argVars) then "rest" else "arg"

                err
                    entry.Range
                    $"%s{doc} does not document %s{v}, a variable in its forms: (%s{clause} %s{v} \"...\"). A symbol that is syntax rather than a variable is listed in (literal %s{v})."

    | KValue
    | KModule
    | KRefused _ -> ()

// ---------------------------------------------------------------------------
// Publishing
// ---------------------------------------------------------------------------

/// The text a range covers in its file, or `None` when the file cannot be read.
///
/// Docs are published as they were written, raw strings and comments
/// included, so that a reader sees what the author wrote and bjodat can read
/// it back. Columns are 0-based and an end is exclusive, as the lexer records
/// them.
let private linesOf (text: string) = text.Replace("\r\n", "\n").Split('\n')

/// The text between a range's two positions, in a text already split in lines.
let private slice (lines: string[]) (r: Range) : string option =
    if r.Start.Line < 1 || r.End.Line > lines.Length || r.End.Line < r.Start.Line then
        None
    elif r.Start.Line = r.End.Line then
        let line = lines[r.Start.Line - 1]
        let stop = min line.Length r.End.Column
        if r.Start.Column > stop then None else Some(line.Substring(r.Start.Column, stop - r.Start.Column))
    else
        let first = lines[r.Start.Line - 1]
        let lastLine = lines[r.End.Line - 1]

        if r.Start.Column > first.Length then
            None
        else
            let middle = lines[r.Start.Line .. r.End.Line - 2] |> List.ofArray
            let last = lastLine.Substring(0, min lastLine.Length r.End.Column)
            Some(String.concat "\n" ([ first.Substring r.Start.Column ] @ middle @ [ last ]))

/// A source file's lines and its tokens, read once per file.
let private sources = Collections.Generic.Dictionary<string, string[] * LexedToken[]>()

let private sourceOf (file: string) : (string[] * LexedToken[]) option =
    match sources.TryGetValue file with
    | true, source -> Some source
    | _ when IO.File.Exists file ->
        try
            let text = IO.File.ReadAllText file
            let source = linesOf text, Array.ofList (Lexer.tokenize file text)
            sources[file] <- source
            Some source
        with _ ->
            None
    | _ -> None

/// The bracketed form that opens at `start`, from its opening bracket to its
/// closing one.
///
/// A form's range ends at the token after it, which is the reader's doing, so
/// the end is found here by counting brackets in the file's tokens. A string,
/// raw ones included, is one token, and a comment none, so neither upsets
/// the count.
let private formText (file: string) (start: Position) : string option =
    sourceOf file
    |> Option.bind (fun (lines, tokens) ->
        match tokens |> Array.tryFindIndex (fun t -> t.Range.Start = start) with
        | Some i ->
            let rec close (j: int) (depth: int) =
                if j >= tokens.Length then
                    None
                else
                    match tokens[j].Token with
                    | LParen
                    | LBracket
                    | LBrace -> close (j + 1) (depth + 1)
                    | RParen
                    | RBracket
                    | RBrace when depth = 1 -> Some tokens[j].Range.End
                    | RParen
                    | RBracket
                    | RBrace -> close (j + 1) (depth - 1)
                    | _ -> close (j + 1) depth

            close i 0
            |> Option.bind (fun stop -> slice lines { tokens[i].Range with Start = start; End = stop })
        | None -> None)

/// The text of a bracketed form, given its range.
let private sourceText (r: Range) = formText r.File r.Start

/// A string as bjodat writes one: escaped, on one line.
let private quoted (s: string) =
    "\""
    + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r")
    + "\""

/// A type as source writes it: `%a` for a variable, `(#:k T)` for a keyword.
let rec private typeText (t: FType) : string =
    let var (n: string) = if n.StartsWith "'" then "%" + n.Substring 1 else n

    match t with
    | TName(n, _) -> var n
    | TApp(n, args, _) -> "(" + String.concat " " (var n :: List.map typeText args) + ")"
    | TArrow(mandatory, keywords, rest, ret, colour, _) ->
        let head =
            match colour with
            | Suspending -> "-bjo->"
            | _ -> "->"

        let parts =
            List.map typeText mandatory
            @ (keywords |> List.map (fun (k, kt) -> $"(#:%s{k} %s{typeText kt})"))
            @ (rest |> Option.map (fun rt -> [ "#:rest " + typeText rt ]) |> Option.defaultValue [])
            @ [ typeText ret ]

        "(" + String.concat " " (head :: parts) + ")"

/// `(name params ...)` of a `defun` as written, defaults and all. `None` for
/// one a macro wrote, whose range is the call that produced it and so does
/// not begin `(defun (name`.
let private definitionHead (name: string) (r: Range) : string option =
    sourceOf r.File
    |> Option.bind (fun (_, tokens) ->
        match tokens |> Array.tryFindIndex (fun t -> t.Range.Start = r.Start) with
        | Some i when i + 3 < tokens.Length ->
            match tokens[i].Token, tokens[i + 1].Token, tokens[i + 2].Token, tokens[i + 3].Token with
            | LParen, Symbol("defun" | "defbjo" | "defbjouble"), LParen, Symbol n when n = name ->
                formText r.File tokens[i + 2].Range.Start
            | _ -> None
        | _ -> None)

/// The docs, as one bjodat vec of the forms as written, each with what the
/// compiler knows added: `(kind ...)`, and for a function its `(signature ...)`
/// and the `(definition ...)` head with its parameters' names and defaults.
/// The added clauses are none that a doc may be written with, so a published
/// form can be read by the same rules as a written one.
let publish (decls: Decl list) (entries: Entry list) : string =
    if entries.IsEmpty then
        ""
    else
        let kinds, readers = kindsOf decls

        // A trait method's type is in its trait, and a foreign binding's in
        // its import, when it was written.
        let signatures =
            decls
            |> List.collect (function
                | DSignature(n, t, _, _) -> [ n, t ]
                | DTrait(_, _, _, _, sigs, _, _, _) -> sigs |> List.map (fun (m, t, _) -> m, t)
                | DImportExtern(specs, _) ->
                    specs |> List.choose (fun spec -> spec.ExplicitType |> Option.map (fun t -> spec.Alias, t))
                | _ -> [])
            |> Map.ofList

        let definitions =
            decls
            |> List.choose (function
                | DDefun(n, _, _, _, r)
                | DDefDouble(n, _, _, _, r) when not (n.StartsWith "#") -> Some(n, r)
                | _ -> None)
            |> Map.ofList

        let entryText (e: Entry) =
            let added =
                match e.Subject with
                | ModuleDoc -> [ "(kind module)" ]
                | Reader n when Set.contains n readers -> [ "(kind reader)" ]
                | Reader _ -> []
                | Named n ->
                    let kind =
                        match Map.tryFind n kinds with
                        | Some(KFunction _)
                        | Some(KUnnamed _) -> "function"
                        | Some KValue -> "value"
                        | Some(KRecord _) -> "record"
                        | Some(KUnion _) -> "union"
                        | Some(KAlias _) -> "alias"
                        | Some(KTrait _) -> "trait"
                        | Some KMacro -> "macro"
                        | _ -> "unknown"

                    let signature =
                        match Map.tryFind n signatures, kind with
                        | Some t, ("function" | "value") -> [ $"(signature %s{quoted (typeText t)})" ]
                        | _ -> []

                    let definition =
                        Map.tryFind n definitions
                        |> Option.bind (definitionHead n)
                        |> Option.map (fun head -> $"(definition %s{quoted head})")
                        |> Option.toList

                    [ $"(kind %s{kind})" ] @ signature @ definition

            let clauses =
                e.Clauses
                |> List.choose (fun (_, r) -> sourceText r)

            "(:doc " + subjectText e.Subject + "\n  " + String.concat "\n  " (added @ clauses) + ")"

        "[" + String.concat "\n" (entries |> List.map entryText) + "]"

/// What `Codegen` writes under the metadata key `BjolangDocs`: the published
/// docs of the module being compiled, or `""` for none. Set by `Pipeline` once
/// they are checked, and read once, when the module is emitted.
let mutable published = ""

/// Checks every doc of the module being compiled, and warns about the exports
/// of a documented module that have none.
let check (env: Env) (moduleKey: string) (decls: Decl list) (entries: Entry list) : unit =
    let kinds, readers = kindsOf decls
    let localNames = kinds |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let seen = Collections.Generic.Dictionary<Subject, Range>()

    for entry in entries do
        match seen.TryGetValue entry.Subject with
        | true, first ->
            reportAt
                entry.Range
                $"(:doc %s{subjectText entry.Subject}) is a second doc of the same thing. The first is at %s{formatPos first}."
        | _ ->
            seen[entry.Subject] <- entry.Range

            let name, kind =
                match entry.Subject with
                | ModuleDoc -> "the module", KModule
                | Reader n when Set.contains n readers -> n, KReader
                | Reader n -> n, KRefused $"this module defines no reader extension #%s{n}."
                | Named n ->
                    match Map.tryFind n kinds with
                    | Some k -> n, k
                    | None when Set.contains n readers ->
                        n, KRefused $"#%s{n} is a reader extension, documented as (:doc %s{n} #:reader ...)."
                    | None when Map.containsKey n env.Bindings || Macro.isMacro n || typeKnown env moduleKey n ->
                        n, KRefused $"%s{n} is defined in another module, and its doc is there."
                    | None -> n, KRefused $"nothing named %s{n} is defined in this module."

            match kind with
            | KRefused why -> reportAt entry.Range $"(:doc %s{subjectText entry.Subject}) cannot be written here: %s{why}"
            | _ -> checkEntry env moduleKey localNames name kind entry

    // A documented module documents what it offers.
    match entries |> List.tryFind (fun e -> e.Subject = ModuleDoc) with
    | None -> ()
    | Some moduleDoc ->
        let documented =
            entries
            |> List.choose (fun e ->
                match e.Subject with
                | Named n -> Some n
                | _ -> None)
            |> Set.ofList

        let documentedReaders =
            entries
            |> List.choose (fun e ->
                match e.Subject with
                | Reader n -> Some n
                | _ -> None)
            |> Set.ofList

        let why = $"This module is documented, by (:doc #:module ...) at %s{formatPos moduleDoc.Range}, so what it offers is."

        let traitMethods =
            decls
            |> List.choose (function
                | DTrait(n, _, _, _, sigs, _, _, _) -> Some(n, sigs |> List.map (fun (m, _, _) -> m))
                | _ -> None)
            |> Map.ofList

        // A trait method may be exported by name as well as with its trait, and
        // is one warning either way.
        let warned = Collections.Generic.HashSet<string>()

        for d in decls do
            match d with
            | DExport(names, r) ->
                for n in names do
                    if not (Set.contains n documented) && warned.Add n then
                        Diagnostics.warn $"%s{n} is exported at %s{formatPos r} and has no (:doc %s{n} ...). %s{why}"

                    for m in Map.tryFind n traitMethods |> Option.defaultValue [] do
                        if not (Set.contains m documented) && warned.Add m then
                            Diagnostics.warn
                                $"%s{m}, a method of the exported trait %s{n}, has no (:doc %s{m} ...), at %s{formatPos r}. %s{why}"
            | DMacro(n, r)
            | DPatternMacro(n, r) when not (Set.contains n documented) ->
                Diagnostics.warn $"The macro %s{n} at %s{formatPos r} has no (:doc %s{n} ...). Every macro is published. %s{why}"
            | DHashMacro(n, r) when not (Set.contains n documentedReaders) ->
                Diagnostics.warn
                    $"The reader extension #%s{n} at %s{formatPos r} has no (:doc %s{n} #:reader ...). Every reader extension is published. %s{why}"
            | _ -> ()
