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

/// Every `match` has to cover the values it can meet, and no clause may be
/// dead.
///
/// Maranget's usefulness algorithm ("Warnings for pattern matching", JFP 17(3))
/// over the typed patterns. `useful` is his `U`: a clause is useful when some
/// value reaches it past every clause above. `missing` is his `I`: it hands
/// back a value that reaches past all of them, which is what the error prints.
///
/// Runs after inference, for the resolved constructor names and the registry's
/// union case lists, and before `Lowering`, which rewrites matches into shapes
/// nobody wrote.
///
/// # Views and guards
///
/// A `(:view step p)` and a `#:when` guard both decide a clause by running
/// code, so they cover nothing here: nothing works out what a step returns, and
/// a match whose clauses all carry one has to end in a binder.
///
/// A later change could do better: a run of consecutive clauses sharing one
/// argument-free top-level view, exhaustive over the view's result type, could
/// be emitted as one call with an inner switch — "total views". That would make
/// `(:view parse-int (Some k))` beside `(:view parse-int None)` exhaustive, and
/// one call rather than two. Not implemented.
///
/// # What is given up
///
/// A sequence pattern whose rest is itself refutable — `[a ... [b c]]` — needs
/// length classes this does not model. `Undecidable` leaves such a match
/// unchecked rather than reporting it wrongly.
module Bjolang.Exhaustiveness

open Bjolang.Lexer
open Bjolang.TypedAST

/// Raised when a match holds a shape the algorithm does not model. The match is
/// then skipped: no error, no counterexample.
exception private Undecidable

/// A head constructor of a pattern column.
type private Ctor =
    /// A union case, `Some`/`None`, `Ok`/`Err`, or `Cons`/`Nil`.
    | CCase of string
    | CBool of bool
    | CTuple of int
    /// A literal of a type with no listable set of values, as written.
    | CConst of string
    /// A `Vec` or `Array` of exactly this many elements.
    | CLen of int
    /// A `Vec` or `Array` of at least this many elements. Its last field is the
    /// remainder, so its arity is one more than the number.
    | CLenGe of int

/// Whether a sequence column is a `Vec` or an `Array`, which is only ever asked
/// in order to spell a counterexample.
type private SeqKind =
    | VecSeq
    | ArraySeq

/// What the values of a column type can be taken apart into.
type private Signature =
    /// Every constructor of the type, with the types of its fields.
    | Closed of (Ctor * HMType list) list
    /// No listable set of values: `int`, `string`, a CLR type, an `#:opaque`
    /// import, a type variable. Only a binder covers one.
    | Open
    /// A sequence of these elements. Its classes are read off the column,
    /// because a rest pattern is what makes them finite.
    | Sequence of SeqKind * HMType

/// A pattern's head, as the algorithm reads it.
type private Head =
    /// A binder or `_`.
    | HWild
    | HCtor of Ctor * TypedPattern list
    | HOr of TypedPattern list
    /// A sequence pattern: its fixed leading elements and its rest, if any.
    | HSeq of TypedPattern list * TypedPattern option
    /// `(:is ...)` and `(:view ...)`. Refutable by something nothing here can
    /// enumerate, so it covers nothing.
    | HNever

let private nowhere =
    { Start = { Line = 0; Column = 0 }
      End = { Line = 0; Column = 0 }
      File = "" }

let private wildcard (t: HMType) : TypedPattern =
    { Type = t; Range = nowhere; Node = TPWildcard }

/// The column type with its metavariables followed.
///
/// `prune` raises on an associated type with no implementation. That says the
/// column cannot be described, not that this match is wrong, so it is skipped.
let private settle (registry: TraitRegistry) (t: HMType) : HMType =
    try
        Unification.prune registry t
    with _ ->
        raise Undecidable

/// A declared payload with the union's type arguments put in.
let private substitute (typeParams: string list) (typeArgs: HMType list) (t: HMType) : HMType =
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

    go t

/// What a column of this type is made of.
///
/// A declared union wins over a builtin of the same name: a module that
/// defines its own `Result` matches it with its own cases.
let private signatureOf (registry: TraitRegistry) (t: HMType) : Signature =
    match settle registry t with
    | TTuple items -> Closed [ CTuple items.Length, items ]
    | TCon(name, args) ->
        match Map.tryFind name registry.Unions with
        | Some(typeParams, cases) ->
            cases
            |> List.map (fun (caseName, payloads, _) ->
                CCase caseName, payloads |> List.map (substitute typeParams args))
            |> Closed
        | None ->
            match name, args with
            | TypeConstants.BooleanName, [] -> Closed [ CBool false, []; CBool true, [] ]
            | "Option", [ a ] -> Closed [ CCase "None", []; CCase "Some", [ a ] ]
            | "Result", [ e; a ] -> Closed [ CCase "Err", [ e ]; CCase "Ok", [ a ] ]
            | "List", [ a ] -> Closed [ CCase "Nil", []; CCase "Cons", [ a; TCon("List", [ a ]) ] ]
            | "Vec", [ a ] -> Sequence(VecSeq, a)
            | "Array", [ a ] -> Sequence(ArraySeq, a)
            | _ -> Open
    | _ -> Open

/// The types of a constructor's fields, in a column of this type.
let private fieldsOf (registry: TraitRegistry) (t: HMType) (c: Ctor) : HMType list =
    match c, signatureOf registry t with
    | CConst _, _
    | CBool _, _ -> []
    | CLen n, Sequence(_, elem) -> List.replicate n elem
    | CLenGe n, Sequence(_, elem) -> List.replicate n elem @ [ t ]
    | _, Closed cases ->
        match cases |> List.tryFind (fun (known, _) -> known = c) with
        | Some(_, fields) -> fields
        | None -> raise Undecidable
    | _ -> raise Undecidable

// ---------------------------------------------------------------------------
// Patterns as rows
// ---------------------------------------------------------------------------

/// A list pattern is a `Cons`/`Nil` chain, so it is written as one and the
/// union machinery covers it.
let rec private normalize (pat: TypedPattern) : TypedPattern =
    let rebuild node = { pat with Node = node }

    match pat.Node with
    | TPList(items, tailOpt) ->
        let rec chain items =
            match items with
            | [] ->
                match tailOpt with
                | Some t -> normalize t
                | None -> rebuild (TPConstruct("Nil", []))
            | head :: rest -> rebuild (TPConstruct("Cons", [ normalize head; chain rest ]))

        chain items
    | TPAs(inner, _) -> normalize inner
    | _ -> TypeVisitor.mapPatternChildrenWith id normalize pat

let rec private headOf (pat: TypedPattern) : Head =
    match pat.Node with
    | TPWildcard
    | TPIdent _ -> HWild
    | TPBool b -> HCtor(CBool b, [])
    | TPInt v -> HCtor(CConst v, [])
    | TPString v -> HCtor(CConst v, [])
    | TPChar c -> HCtor(CConst(string c), [])
    | TPKeyword k -> HCtor(CConst k, [])
    | TPSymbol s -> HCtor(CConst s, [])
    | TPTuple items -> HCtor(CTuple items.Length, items)
    | TPConstruct(name, args) -> HCtor(CCase name, args)
    | TPOr alts -> HOr alts
    | TPTypeTest _
    | TPApp _ -> HNever
    // An `and` matches the *intersection* of its conjuncts, and a matrix column
    // holds one head constructor. A conjunct that cannot fail rules nothing
    // out, so it is dropped; one that can and stands alone is the whole of what
    // the conjunction matches. Two that can is an intersection nothing here
    // spells, and it covers nothing rather than being over-reported.
    | TPAnd alts ->
        let refutable =
            alts
            |> List.filter (fun alt ->
                match headOf alt with
                | HWild -> false
                | _ -> true)

        match refutable with
        | [] -> HWild
        | [ only ] -> headOf only
        | _ -> HNever
    | TPVec(items, tailOpt)
    | TPArray(items, tailOpt) ->
        // A rest that is itself refutable constrains lengths this does not
        // model.
        match tailOpt with
        | Some t ->
            match headOf t with
            | HWild -> HSeq(items, tailOpt)
            | _ -> raise Undecidable
        | None -> HSeq(items, None)
    // Rewritten by `normalize`.
    | TPList _
    | TPAs _ -> raise Undecidable

/// One row of the matrix: the patterns still to be matched, left to right.
type private Row = TypedPattern list

/// The constructors written in the first column.
let rec private columnCtors (matrix: Row list) : Ctor list =
    matrix
    |> List.collect (fun row ->
        match row with
        | [] -> []
        | head :: _ ->
            match headOf head with
            | HCtor(c, _) -> [ c ]
            | HOr alts -> columnCtors (alts |> List.map (fun a -> [ a ]))
            | _ -> [])
    |> List.distinct

/// The lengths written in the first column of a sequence: the exact ones, and
/// the prefixes of the rest patterns.
let rec private columnLengths (matrix: Row list) : int list * int list =
    let mutable fixedLengths = []
    let mutable restPrefixes = []

    let rec visit (pat: TypedPattern) =
        match headOf pat with
        | HSeq(items, None) -> fixedLengths <- items.Length :: fixedLengths
        | HSeq(items, Some _) -> restPrefixes <- items.Length :: restPrefixes
        | HOr alts -> alts |> List.iter visit
        | _ -> ()

    for row in matrix do
        match row with
        | [] -> ()
        | head :: _ -> visit head

    List.distinct fixedLengths, List.distinct restPrefixes

/// The length classes a sequence column splits into, or `None` when there is no
/// rest pattern and the family stays infinite.
///
/// The classes run `[]`, `[_]`, … up to the longest fixed length written, and
/// then one class for everything at or above the shortest rest prefix.
let private lengthClasses (matrix: Row list) : Ctor list option =
    let fixedLengths, restPrefixes = columnLengths matrix

    match restPrefixes with
    | [] -> None
    | _ ->
        let shortestRest = List.min restPrefixes

        let bound =
            match fixedLengths with
            | [] -> shortestRest
            | _ -> max shortestRest (List.max fixedLengths + 1)

        Some([ for n in 0 .. bound - 1 -> CLen n ] @ [ CLenGe bound ])

/// The constructors to recurse on when the column is completely covered by
/// what is written in it, or `None` when it is not.
let private completeSignature (registry: TraitRegistry) (t: HMType) (matrix: Row list) : Ctor list option =
    match signatureOf registry t with
    | Open -> None
    | Sequence _ -> lengthClasses matrix
    | Closed cases ->
        let written = columnCtors matrix
        let all = cases |> List.map fst

        if all |> List.forall (fun c -> List.contains c written) then
            Some all
        else
            None

// ---------------------------------------------------------------------------
// Matrix operations
// ---------------------------------------------------------------------------

/// `S(c, row)`: what is left of a row once the value is known to be `c`.
///
/// `asQuery` is set for the clause being tested rather than for the clauses
/// above it. A `(:is ...)` or a `(:view ...)` covers nothing when it stands
/// above — but the clause carrying one is still reachable exactly where a
/// binder would be, so as a query it is read as one.
let rec private specializeRow (asQuery: bool) (c: Ctor) (fields: HMType list) (row: Row) : Row list =
    match row with
    | [] -> []
    | head :: rest ->
        let wilds () = fields |> List.map wildcard

        match headOf head with
        | HWild -> [ wilds () @ rest ]
        | HNever -> if asQuery then [ wilds () @ rest ] else []
        | HOr alts -> alts |> List.collect (fun alt -> specializeRow asQuery c fields (alt :: rest))
        | HCtor(written, args) -> if written = c then [ args @ rest ] else []
        | HSeq(items, tailOpt) ->
            let elemWilds n =
                if n <= 0 then [] else List.replicate n (wildcard (List.head fields))

            match c, tailOpt with
            | CLen n, None -> if items.Length = n then [ items @ rest ] else []
            | CLen n, Some _ ->
                if items.Length <= n then
                    [ items @ elemWilds (n - items.Length) @ rest ]
                else
                    []
            // Only a rest pattern reaches the open class, and it fills the
            // remainder slot with the binder it already is.
            | CLenGe n, Some _ ->
                if items.Length <= n then
                    [ items @ elemWilds (n - items.Length) @ [ List.last fields |> wildcard ] @ rest ]
                else
                    []
            | _ -> []

let private specialize (asQuery: bool) (c: Ctor) (fields: HMType list) (matrix: Row list) : Row list =
    matrix |> List.collect (specializeRow asQuery c fields)

/// `D(row)`: what is left of a row for the values whose constructor is written
/// nowhere in the column.
///
/// A sequence pattern is dropped outright, because this is only reached when
/// the column holds no rest pattern and every sequence pattern there fixes a
/// length.
let rec private defaultRow (row: Row) : Row list =
    match row with
    | [] -> []
    | head :: rest ->
        match headOf head with
        | HWild -> [ rest ]
        | HOr alts -> alts |> List.collect (fun alt -> defaultRow (alt :: rest))
        | HNever
        | HCtor _
        | HSeq _ -> []

let private defaultMatrix (matrix: Row list) : Row list = matrix |> List.collect defaultRow

// ---------------------------------------------------------------------------
// Usefulness
// ---------------------------------------------------------------------------

/// Is there a value that matches `query` and none of `matrix`'s rows?
///
/// `matrix` holds the clauses above, `query` the one being asked about. Both
/// are aligned with `types`, one entry per column.
let rec private useful (registry: TraitRegistry) (types: HMType list) (matrix: Row list) (query: Row) : bool =
    match types, query with
    | [], _
    | _, [] -> List.isEmpty matrix
    | columnType :: restTypes, head :: queryRest ->
        let tryClass c =
            let fields = fieldsOf registry columnType c

            specializeRow true c fields (head :: queryRest)
            |> List.exists (useful registry (fields @ restTypes) (specialize false c fields matrix))

        match headOf head with
        | HOr alts -> alts |> List.exists (fun alt -> useful registry types matrix (alt :: queryRest))
        | queryHead ->
            // The query's own head joins the column, because the length classes
            // of a sequence have to hold it too.
            match completeSignature registry columnType (matrix @ [ [ head ] ]) with
            | Some ctors -> ctors |> List.exists tryClass
            | None ->
                match queryHead with
                | HWild
                | HNever -> useful registry restTypes (defaultMatrix matrix) queryRest
                | HCtor(c, _) -> tryClass c
                | HSeq(items, None) -> tryClass (CLen items.Length)
                // A rest pattern in the query makes the classes finite, so the
                // signature above was `Some`.
                | HSeq _
                | HOr _ -> raise Undecidable

// ---------------------------------------------------------------------------
// Counterexamples
// ---------------------------------------------------------------------------

/// A value that reaches past every clause, in as much detail as the algorithm
/// pinned down.
type private Witness =
    | WAny
    | WCase of string * Witness list
    | WBool of bool
    | WTuple of Witness list
    /// Elements, and whether the last of them stands for a remainder.
    | WSeq of SeqKind * Witness list * bool

/// A constructor as source writes it. A declared one is keyed by the module
/// that declared it, and the key is not a pattern anybody can write.
let private writtenCase (name: string) =
    match Naming.typeKeyParts name with
    | Some(_, bare) -> bare
    | None -> name

let rec private showWitness (w: Witness) : string =
    match w with
    | WAny -> "_"
    | WBool b -> if b then "#t" else "#f"
    | WCase(name, []) -> writtenCase name
    | WCase(name, args) ->
        "(" + writtenCase name + " " + (args |> List.map showWitness |> String.concat " ") + ")"
    | WTuple args -> "(Tuple " + (args |> List.map showWitness |> String.concat " ") + ")"
    | WSeq(kind, items, openEnded) ->
        let opening = match kind with VecSeq -> "[" | ArraySeq -> "#["
        let shown = items |> List.map showWitness
        let body = String.concat " " (if openEnded then shown @ [ "..." ] else shown)
        opening + body + "]"

/// A constructor applied to the witnesses of its fields. `kind` is only read
/// for a sequence class.
let private applyCtor (kind: SeqKind) (c: Ctor) (args: Witness list) : Witness =
    match c with
    | CCase name -> WCase(name, args)
    | CBool b -> WBool b
    | CTuple _ -> WTuple args
    | CConst _ -> WAny
    | CLen _ -> WSeq(kind, args, false)
    | CLenGe _ -> WSeq(kind, args, true)

let private seqKindOf (registry: TraitRegistry) (t: HMType) : SeqKind =
    match signatureOf registry t with
    | Sequence(kind, _) -> kind
    | _ -> VecSeq

/// A value of this column type whose constructor is written nowhere in it.
///
/// A column that names no constructor at all rules nothing out, so it is
/// spelled `_` rather than by picking one arbitrarily.
let private missingCtor (registry: TraitRegistry) (t: HMType) (matrix: Row list) : Witness =
    match signatureOf registry t with
    | Open -> WAny
    | Sequence(kind, _) ->
        let fixedLengths, _ = columnLengths matrix

        match fixedLengths with
        | [] -> WAny
        | _ ->
            let shortest = Seq.initInfinite id |> Seq.find (fun n -> not (List.contains n fixedLengths))
            WSeq(kind, List.replicate shortest WAny, false)
    | Closed cases ->
        match columnCtors matrix with
        | [] -> WAny
        | written ->
            match cases |> List.tryFind (fun (c, _) -> not (List.contains c written)) with
            | Some(c, fields) -> applyCtor VecSeq c (fields |> List.map (fun _ -> WAny))
            | None -> WAny

/// Algorithm `I`: a value no row of `matrix` matches, or `None` when the rows
/// cover everything.
let rec private missing (registry: TraitRegistry) (types: HMType list) (matrix: Row list) : Witness list option =
    match types with
    | [] -> if List.isEmpty matrix then Some [] else None
    | columnType :: restTypes ->
        let regroup (c: Ctor) (fields: HMType list) (witnesses: Witness list) =
            let arity = fields.Length

            applyCtor (seqKindOf registry columnType) c (List.truncate arity witnesses)
            :: List.skip arity witnesses

        match completeSignature registry columnType matrix with
        | Some ctors ->
            ctors
            |> List.tryPick (fun c ->
                let fields = fieldsOf registry columnType c

                missing registry (fields @ restTypes) (specialize false c fields matrix)
                |> Option.map (regroup c fields))
        | None ->
            missing registry restTypes (defaultMatrix matrix)
            |> Option.map (fun rest -> missingCtor registry columnType matrix :: rest)

// ---------------------------------------------------------------------------
// The check
// ---------------------------------------------------------------------------

/// Does this pattern decide anything by running code or by a .NET type test?
let rec private runsCode (pat: TypedPattern) : bool =
    match pat.Node with
    | TPApp _
    | TPTypeTest _ -> true
    | _ ->
        let mutable found = false

        pat
        |> TypeVisitor.mapPatternChildrenWith
            id
            (fun child ->
                found <- found || runsCode child
                child)
        |> ignore

        found

/// The sentence that explains a counterexample nobody expected, for a match
/// that has one of the three clause shapes covering nothing.
let private coverageNote (clauses: TMatchClause list) =
    if clauses |> List.exists (fun c -> c.Guard.IsSome || runsCode c.Pattern) then
        "\n  A clause with a #:when guard, a (:view ...) or a (:is ...) covers nothing: whether it matches is only known when it runs."
    else
        ""

/// A `#:when` guard may fail, so a guarded clause covers nothing — but it is
/// still checked for being reachable.
let private checkMatch (registry: TraitRegistry) (range: Range) (scrutinee: HMType) (clauses: TMatchClause list) =
    try
        let mutable covered: Row list = []

        for clause in clauses do
            let row = [ normalize clause.Pattern ]

            if not (useful registry [ scrutinee ] covered row) then
                failwithf
                    $"Pattern Error at %s{formatPos clause.Pattern.Range}: this clause can never run — the clauses above it already match everything it does."

            if clause.Guard.IsNone then
                covered <- covered @ [ row ]

        match missing registry [ scrutinee ] covered with
        | Some [ witness ] ->
            failwithf
                $"Pattern Error at %s{formatPos range}: this match does not cover every value. %s{showWitness witness} reaches no clause.%s{coverageNote clauses}"
        | _ -> ()
    with Undecidable ->
        ()

let rec private checkExpr (registry: TraitRegistry) (expr: TypedExpr) : unit =
    match expr.Node with
    | TMatch(target, clauses) -> checkMatch registry expr.Range target.Type clauses
    | _ -> ()

    TypeVisitor.children expr |> List.iter (checkExpr registry)

let private checkDecl (registry: TraitRegistry) (decl: TDecl) : unit =
    decl
    |> TypeVisitor.mapDecl (fun e ->
        checkExpr registry e
        e)
    |> ignore

let run (registry: TraitRegistry) (decls: TDecl list) : unit =
    decls |> List.iter (checkDecl registry)
