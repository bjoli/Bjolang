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
    /// A record or struct, the only constructor of its type. The field names
    /// are carried for spelling a counterexample.
    | CRecord of string * string list
    /// A literal of a type with no listable set of values, as written.
    | CConst of string
    /// A `Vec` or `Array` of exactly this many elements.
    | CLen of int
    /// A `Vec` or `Array` of at least this many elements. Its last field is the
    /// remainder, so its arity is one more than the number.
    | CLenGe of int
    /// The integers from `Lo` to `Hi`, both included, of an integer or char
    /// column. A literal is a range of one value, and a comparison is the
    /// range its bounds leave. See `Ranged`.
    | CRange of IntRange

/// A range of an integer or char column.
///
/// `Shown` is set for a class of the column whose value is worth printing in
/// a counterexample: one value, or a gap between two ranges that were
/// written. A class that runs to the end of the type prints as `_`, as the
/// counterexample of an `int` column always did.
and private IntRange =
    { Lo: System.Numerics.BigInteger
      Hi: System.Numerics.BigInteger
      IsChar: bool
      Shown: bool }

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
    /// An integer type or `Char`, its values from the first to the second. Its
    /// classes are read off the column too: the ranges written in it split
    /// the type into intervals, and each interval is matched by a range or by
    /// none. This is what C# itself proves of the same patterns, so a clause
    /// it would call unreachable is reported here first.
    | Ranged of System.Numerics.BigInteger * System.Numerics.BigInteger * bool

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
    /// An `(and ...)` whose parts no value matches together, such as
    /// `(and 1 2)`. It covers nothing, and a clause made of one can never run.
    | HEmpty

let private nowhere =
    { Start = { Line = 0; Column = 0 }
      End = { Line = 0; Column = 0 }
      File = ""
      Module = "" }

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

/// The highest Unicode scalar value, the last value of a `Char`.
let private charMax = System.Numerics.BigInteger 0x10FFFF

/// What a column of this type is made of.
///
/// A declared union wins over a builtin of the same name: a module that
/// defines its own `Result` matches it with its own cases.
let private signatureOf (registry: TraitRegistry) (t: HMType) : Signature =
    match settle registry t with
    | TTuple items -> Closed [ CTuple items.Length, items ]
    | TCon(name, args) when Map.containsKey name registry.Records ->
        let typeParams, fields = registry.Records[name]

        Closed
            [ CRecord(name, List.map fst fields), fields |> List.map (snd >> substitute typeParams args) ]
    // One constructor, the type's own name, around the value it wraps.
    | TCon(name, args) when Map.containsKey name registry.Newtypes ->
        let typeParams, underlying = registry.Newtypes[name]
        Closed [ CCase name, [ substitute typeParams args underlying ] ]
    | TCon(name, args) ->
        match Map.tryFind name registry.Unions with
        | Some(typeParams, cases) ->
            cases
            |> List.map (fun (caseName, payloads) ->
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
            | TypeConstants.CharName, [] -> Ranged(System.Numerics.BigInteger.Zero, charMax, true)
            | _ ->
                match NumericLiteral.integerRange (TCon(name, args)) with
                | Some(lo, hi) -> Ranged(lo, hi, false)
                | None -> Open
    | _ -> Open

/// The types of a constructor's fields, in a column of this type.
let private fieldsOf (registry: TraitRegistry) (t: HMType) (c: Ctor) : HMType list =
    match c, signatureOf registry t with
    | CConst _, _
    | CBool _, _
    | CRange _, _ -> []
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

/// The range of an integer or char column a literal or a comparison matches,
/// or `None` for a column of any other type.
let private rangeOf (pat: TypedPattern) : IntRange option =
    let domain =
        match NumericLiteral.settled pat.Type with
        | TCon(TypeConstants.CharName, []) -> Some(System.Numerics.BigInteger.Zero, charMax, true)
        | t -> NumericLiteral.integerRange t |> Option.map (fun (lo, hi) -> lo, hi, false)

    let valueOf (bound: TypedPattern) =
        match bound.Node with
        | TPChar c -> Some(System.Numerics.BigInteger c)
        | TPInt v -> NumericLiteral.integerValue v
        | _ -> None

    let one = System.Numerics.BigInteger.One

    match domain with
    | None -> None
    | Some(lo, hi, isChar) ->
        let range lo hi =
            Some { Lo = lo; Hi = hi; IsChar = isChar; Shown = false }

        match pat.Node with
        | TPInt _
        | TPChar _ ->
            match valueOf pat with
            | Some v -> range v v
            | None -> None
        | TPCompare bounds ->
            let narrowed =
                bounds
                |> List.fold
                    (fun acc (op, bound) ->
                        match acc, valueOf bound with
                        | Some(l, h), Some b ->
                            match op with
                            | ">=" -> Some(max l b, h)
                            | ">" -> Some(max l (b + one), h)
                            | "<=" -> Some(l, min h b)
                            | "<" -> Some(l, min h (b - one))
                            | _ -> None
                        | _ -> None)
                    (Some(lo, hi))

            narrowed |> Option.bind (fun (l, h) -> range l h)
        | _ -> None

/// The constant a number literal of a type with no modelled range stands for.
/// A real is keyed by its value, because `1.0` and `1.00` are the same label in
/// C# and a duplicate label is CS0152 there.
let private numberConst (pat: TypedPattern) (text: string) : string =
    let invariant = System.Globalization.CultureInfo.InvariantCulture
    let digits = (NumericLiteral.digits text).Replace("_", "")

    match NumericLiteral.settled pat.Type with
    | TCon(TypeConstants.DecimalName, []) ->
        match System.Decimal.TryParse(digits, System.Globalization.NumberStyles.Float, invariant) with
        // The division drops trailing zeros, which `decimal` keeps as scale.
        | true, v -> "m:" + (v / 1.0000000000000000000000000000m).ToString(invariant)
        | _ -> text
    | t when NumericLiteral.holdsFraction t ->
        match System.Double.TryParse(digits, System.Globalization.NumberStyles.Float, invariant) with
        | true, v -> "d:" + v.ToString("R", invariant)
        | _ -> text
    | _ -> text

/// Both patterns at once, as one pattern in the place of the first.
let private both (a: TypedPattern) (b: TypedPattern) : TypedPattern = { a with Node = TPAnd [ a; b ] }

/// The head of the values that two heads both match. Neither is `HWild` or
/// `HOr`; the caller removed those.
///
/// Two constructors that are the same are met field by field, and two that
/// differ match no value together. A record pattern holds every field of its
/// record (see `TypeEnv`), so two of one record line up field by field too.
let private meet (h1: Head) (h2: Head) : Head =
    match h1, h2 with
    | HEmpty, _
    | _, HEmpty -> HEmpty
    | HCtor(CRange a, _), HCtor(CRange b, _) ->
        let lo = max a.Lo b.Lo
        let hi = min a.Hi b.Hi

        if lo > hi then
            HEmpty
        else
            HCtor(CRange { a with Lo = lo; Hi = hi; Shown = false }, [])
    | HCtor(c1, a1), HCtor(c2, a2) ->
        if c1 = c2 && a1.Length = a2.Length then
            HCtor(c1, List.map2 both a1 a2)
        else
            HEmpty
    // Two sequences: a fixed length has to be one the other allows, and the
    // leading elements are met pairwise. The rest of a rest pattern is a
    // binder, so it adds nothing to the elements it stands for.
    | HSeq(i1, t1), HSeq(i2, t2) ->
        let shorter, longer, shorterOpen, longerTail =
            if i1.Length <= i2.Length then i1, i2, t1.IsSome, t2 else i2, i1, t2.IsSome, t1

        // A fixed length meets the other only at a length it allows. When the
        // shorter one is fixed, that is its own length, and the result is
        // fixed too.
        if shorterOpen then
            let n = shorter.Length
            HSeq(List.map2 both shorter (List.take n longer) @ List.skip n longer, longerTail)
        elif shorter.Length = longer.Length then
            HSeq(List.map2 both shorter longer, None)
        else
            HEmpty
    | _ -> HNever

let rec private headOf (pat: TypedPattern) : Head =
    match rangeOf pat with
    | Some r -> HCtor(CRange r, [])
    | None ->

    match pat.Node with
    | TPWildcard
    | TPIdent _ -> HWild
    | TPBool b -> HCtor(CBool b, [])
    | TPInt v -> HCtor(CConst(numberConst pat v), [])
    | TPString v -> HCtor(CConst v, [])
    | TPChar c -> HCtor(CConst(string c), [])
    | TPKeyword k -> HCtor(CConst k, [])
    | TPSymbol s -> HCtor(CConst s, [])
    | TPTuple items -> HCtor(CTuple items.Length, items)
    | TPConstruct(name, args) -> HCtor(CCase name, args)
    | TPRecord(name, fields) -> HCtor(CRecord(name, List.map fst fields), List.map snd fields)
    | TPOr alts -> HOr alts
    | TPTypeTest _
    | TPApp _
    // A comparison of a `double` or a `decimal`, whose ranges are not
    // modelled. It is emitted as a guard, so it covers nothing, as a view does.
    | TPCompare _ -> HNever
    // An `and` matches the *intersection* of its conjuncts. A conjunct that
    // cannot fail rules nothing out, so it is dropped; one that can and stands
    // alone is the whole of what the conjunction matches. Several are met into
    // one head, because C# works out the same intersection of the same
    // patterns and refuses a label that it proves empty or covered (CS8120).
    | TPAnd alts ->
        let rec flatten (p: TypedPattern) =
            match p.Node with
            | TPAnd inner -> inner |> List.collect flatten
            | _ -> [ p ]

        let refutable =
            alts
            |> List.collect flatten
            |> List.filter (fun alt ->
                match headOf alt with
                | HWild -> false
                | _ -> true)

        match refutable with
        | [] -> HWild
        | [ only ] -> headOf only
        | _ ->
            // An `or` among the parts is distributed over the others:
            // `(and (or a b) c)` is `(or (and a c) (and b c))`.
            match refutable |> List.tryFindIndex (fun p -> match p.Node with TPOr _ -> true | _ -> false) with
            | Some i ->
                let others = refutable |> List.removeAt i

                match refutable[i].Node with
                | TPOr ors -> HOr(ors |> List.map (fun o -> { pat with Node = TPAnd(o :: others) }))
                | _ -> HNever
            | None ->
                let heads = refutable |> List.map headOf

                let modelled =
                    heads
                    |> List.filter (function
                        | HNever -> false
                        | _ -> true)

                // An empty intersection is empty also with a view or a type
                // test beside it. Otherwise such a part decides by running
                // code, and the conjunction covers nothing.
                match modelled with
                | [] -> HNever
                | _ ->
                    match List.reduce meet modelled with
                    | HEmpty -> HEmpty
                    | _ when modelled.Length < heads.Length -> HNever
                    | met -> met
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
/// then one class for everything at or above the longest rest prefix.
///
/// The longest, so that every rest pattern reaches the open class: one with
/// more leading elements than the class's length cannot be specialised to it,
/// and was dropped as if it matched nothing there. Under `["a" _ ...]` the
/// open class began at one, so `[x y rest ...]`, whose two elements do not fit
/// in it, reached no class at all and was reported as a clause that can never
/// run. The lengths between the shortest prefix and the longest are classes of
/// their own, which a shorter rest pattern covers with wildcards.
let private lengthClasses (matrix: Row list) : Ctor list option =
    let fixedLengths, restPrefixes = columnLengths matrix

    match restPrefixes with
    | [] -> None
    | _ ->
        let longestRest = List.max restPrefixes

        let bound =
            match fixedLengths with
            | [] -> longestRest
            | _ -> max longestRest (List.max fixedLengths + 1)

        Some([ for n in 0 .. bound - 1 -> CLen n ] @ [ CLenGe bound ])

/// The intervals an integer or char column splits into, or `None` when no
/// range is written in it and a binder is all that covers it.
///
/// Every written range starts a class at its first value and another after its
/// last, so each class is inside a written range or outside it, never across
/// one. The classes run from the type's first value to its last.
let private rangeClasses
    (lo: System.Numerics.BigInteger)
    (hi: System.Numerics.BigInteger)
    (isChar: bool)
    (matrix: Row list)
    : Ctor list option =
    let written =
        columnCtors matrix
        |> List.choose (function
            | CRange r when r.Lo <= r.Hi -> Some r
            | _ -> None)

    if written.IsEmpty then
        None
    else
        let one = System.Numerics.BigInteger.One

        let starts =
            lo
            :: (written |> List.collect (fun r -> [ r.Lo; r.Hi + one ]))
            |> List.filter (fun p -> p >= lo && p <= hi)
            |> List.distinct
            |> List.sort

        let ends = (starts |> List.tail |> List.map (fun p -> p - one)) @ [ hi ]

        // The classes worth printing first, so that the counterexample is a gap
        // between written ranges rather than `_` where both would do.
        List.zip starts ends
        |> List.map (fun (l, h) ->
            { Lo = l
              Hi = h
              IsChar = isChar
              Shown = l = h || (l > lo && h < hi) })
        |> List.sortBy (fun r -> not r.Shown)
        |> List.map CRange
        |> Some

/// Does the written constructor match every value of the class `c`? Equality
/// for every constructor but a range, which matches each class inside it.
let private covers (written: Ctor) (c: Ctor) : bool =
    match written, c with
    // An empty range, such as `(:between 9 0)`, matches nothing.
    | CRange w, CRange r -> w.Lo <= w.Hi && w.Lo <= r.Lo && r.Hi <= w.Hi
    | _ -> written = c

/// The constructors to recurse on when the column is completely covered by
/// what is written in it, or `None` when it is not.
let private completeSignature (registry: TraitRegistry) (t: HMType) (matrix: Row list) : Ctor list option =
    match signatureOf registry t with
    | Open -> None
    | Sequence _ -> lengthClasses matrix
    | Ranged(lo, hi, isChar) -> rangeClasses lo hi isChar matrix
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
        | HEmpty -> []
        | HOr alts -> alts |> List.collect (fun alt -> specializeRow asQuery c fields (alt :: rest))
        | HCtor(written, args) -> if covers written c then [ args @ rest ] else []
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
        | HEmpty
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
        | HEmpty -> false
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
                | HOr _
                | HEmpty -> raise Undecidable

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
    | WRecord of string * (string * Witness) list
    /// Elements, and whether the last of them stands for a remainder.
    | WSeq of SeqKind * Witness list * bool
    /// A value of an integer or char column, as source writes it.
    | WValue of string

/// A constructor as source writes it. A declared one is keyed by the module
/// that declared it, and the key is not a pattern anybody can write.
let private writtenCase (name: string) =
    match Naming.typeKeyParts name with
    | Some(_, bare) -> bare
    | None -> name

let rec private showWitness (w: Witness) : string =
    match w with
    | WAny -> "_"
    | WValue v -> v
    | WBool b -> if b then "#t" else "#f"
    | WCase(name, []) -> writtenCase name
    | WCase(name, args) ->
        "(" + writtenCase name + " " + (args |> List.map showWitness |> String.concat " ") + ")"
    | WTuple args -> "(Tuple " + (args |> List.map showWitness |> String.concat " ") + ")"
    // Only the fields that pin something down; the rest may be left out.
    | WRecord(name, fields) ->
        match fields |> List.filter (fun (_, w) -> w <> WAny) with
        | [] -> writtenCase name
        | pinned ->
            let shown = pinned |> List.map (fun (f, w) -> "(" + f + " " + showWitness w + ")")
            "(" + writtenCase name + " " + String.concat " " shown + ")"
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
    | CRecord(name, fields) -> WRecord(name, List.zip fields args)
    | CConst _ -> WAny
    | CRange r when not r.Shown -> WAny
    | CRange r when r.IsChar -> WValue $"#\\x%X{int r.Lo}"
    | CRange r -> WValue(r.Lo.ToString())
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
    | Open
    // Reached only with no range in the column: see `rangeClasses`.
    | Ranged _ -> WAny
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
// The clauses a match lacks
// ---------------------------------------------------------------------------

/// A match that does not cover every value, for a tool that offers to add the
/// clauses it lacks: the match, and patterns that cover what its clauses do
/// not, as source writes them.
type MissingClauses = { Range: Range; Patterns: string list }

/// Called with each match the check finds incomplete.
let mutable observe: (MissingClauses -> unit) option = None

/// A missing value as a clause would match it. A value of an integer or a
/// char column, or a sequence of one length, is widened to all of them, so
/// that the clauses added are few and the last of them is reached.
let rec private widened (w: Witness) : Witness =
    match w with
    | WValue _ -> WAny
    | WCase(name, args) -> WCase(name, List.map widened args)
    | WTuple args -> WTuple(List.map widened args)
    | WRecord(name, fields) -> WRecord(name, fields |> List.map (fun (f, x) -> f, widened x))
    | WSeq(kind, items, false) -> WSeq(kind, List.map widened items @ [ WAny ], true)
    | WSeq(kind, items, true) -> WSeq(kind, List.map widened items, true)
    | other -> other

/// The pattern a widened witness is, as a row of the matrix.
let rec private patternOf (registry: TraitRegistry) (t: HMType) (w: Witness) : TypedPattern =
    let at node = { Type = t; Range = nowhere; Node = node }
    let each (types: HMType list) (ws: Witness list) = List.map2 (patternOf registry) types ws

    match w with
    | WAny
    | WValue _ -> wildcard t
    | WBool b -> at (TPBool b)
    | WCase(name, args) -> at (TPConstruct(name, each (fieldsOf registry t (CCase name)) args))
    | WTuple args -> at (TPTuple(each (fieldsOf registry t (CTuple args.Length)) args))
    | WRecord(name, fields) ->
        let types = fieldsOf registry t (CRecord(name, List.map fst fields))
        at (TPRecord(name, List.map2 (fun (f, x) ft -> f, patternOf registry ft x) fields types))
    | WSeq(kind, items, openEnded) ->
        let elem =
            match signatureOf registry t with
            | Sequence(_, e) -> e
            | _ -> raise Undecidable

        let shown, rest =
            if openEnded then List.take (items.Length - 1) items, Some(wildcard t) else items, None

        let shownPatterns = List.map (patternOf registry elem) shown

        match kind with
        | VecSeq -> at (TPVec(shownPatterns, rest))
        | ArraySeq -> at (TPArray(shownPatterns, rest))

/// The clauses to add to a match whose clauses are `covered`, found one
/// missing value at a time, each added as a row before the next is looked for.
/// Past `limit` of them a `_` takes the rest.
///
/// A missing value that is `_` for a type of several constructors, which is
/// what a match with no clauses yet lacks, or one whose clauses all have a
/// guard, is every constructor no clause covers, each with `_` for what it
/// takes: a clause each is what such a match is written with. A `_` is what
/// the error says, since nothing rules a constructor out there.
let private missingClauses (registry: TraitRegistry) (scrutinee: HMType) (covered: Row list) : string list =
    let limit = 20

    let eachCase (covered: Row list) =
        match signatureOf registry scrutinee with
        | Closed cases when cases.Length > 1 ->
            cases
            |> List.map (fun (c, fields) -> applyCtor VecSeq c (fields |> List.map (fun _ -> WAny)))
            |> List.filter (fun w -> useful registry [ scrutinee ] covered [ patternOf registry scrutinee w ])
        | _ -> []

    let rec go (covered: Row list) (found: string list) =
        if found.Length >= limit then
            List.rev ("_" :: found)
        else
            match missing registry [ scrutinee ] covered with
            | Some [ w ] ->
                let ws =
                    match widened w, eachCase covered with
                    | WAny, (_ :: _ as cases) -> cases
                    | w, _ -> [ w ]

                go
                    (covered @ (ws |> List.map (fun w -> [ patternOf registry scrutinee w ])))
                    (List.rev (ws |> List.map showWitness) @ found)
            | _ -> List.rev found

    go covered []

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
    let rec compares (pat: TypedPattern) =
        match pat.Node with
        | TPCompare _ -> (rangeOf pat).IsNone
        | _ ->
            let mutable found = false

            pat
            |> TypeVisitor.mapPatternChildrenWith
                id
                (fun child ->
                    found <- found || compares child
                    child)
            |> ignore

            found

    let runs =
        if clauses |> List.exists (fun c -> c.Guard.IsSome || runsCode c.Pattern) then
            "\n  A clause with a #:when guard, a (:view ...) or a (:is ...) covers nothing: whether it matches is only known when it runs."
        else
            ""

    let ranges =
        if clauses |> List.exists (fun c -> compares c.Pattern) then
            "\n  A comparison of a double or a decimal, such as (:at-least 0.0), covers nothing: their ranges are not added up, and a double can be NaN, which no comparison matches. End the match with a clause that covers the rest, such as _."
        else
            ""

    runs + ranges

/// The message for a pattern that no value matches, such as `(and 1 2)` or
/// `(:between 9 0)`.
let private matchesNoValue =
    "this pattern matches no value, so its clause can never run. An (and ...) whose parts cannot match the same value, or a comparison whose bounds leave no value between them, does this."

/// Every alternative of a clause's top-level `or` has to be reachable past the
/// clauses above and the alternatives before it.
///
/// Codegen gives each of these alternatives a `case` label of its own, and C#
/// refuses a label that an earlier one covers (CS8120, CS0152). An earlier
/// alternative counts as covering also under a guard, because the guard is the
/// same for each alternative of the clause.
let private checkAlternatives (registry: TraitRegistry) (scrutinee: HMType) (covered: Row list) (pat: TypedPattern) =
    let rec flatten (p: TypedPattern) =
        match p.Node with
        | TPOr alts -> alts |> List.collect flatten
        | _ -> [ p ]

    match pat.Node with
    | TPOr _ ->
        flatten pat
        |> List.fold
            (fun (above: Row list) alt ->
                let row = [ normalize alt ]

                if not (useful registry [ scrutinee ] [] row) then
                    failwithf $"Pattern Error at %s{formatPos alt.Range}: %s{matchesNoValue}"

                if not (useful registry [ scrutinee ] above row) then
                    failwithf
                        $"Pattern Error at %s{formatPos alt.Range}: this alternative can never run — the clauses above and the alternatives before it already match everything it does."

                above @ [ row ])
            covered
        |> ignore
    | _ -> ()

/// A `#:when` guard may fail, so a guarded clause covers nothing — but it is
/// still checked for being reachable.
let private checkMatch (registry: TraitRegistry) (range: Range) (scrutinee: HMType) (clauses: TMatchClause list) =
    try
        let mutable covered: Row list = []

        for clause in clauses do
            let row = [ normalize clause.Pattern ]

            if not (useful registry [ scrutinee ] [] row) then
                failwithf $"Pattern Error at %s{formatPos clause.Pattern.Range}: %s{matchesNoValue}"

            if not (useful registry [ scrutinee ] covered row) then
                failwithf
                    $"Pattern Error at %s{formatPos clause.Pattern.Range}: this clause can never run — the clauses above it already match everything it does."

            checkAlternatives registry scrutinee covered clause.Pattern

            if clause.Guard.IsNone then
                covered <- covered @ [ row ]

        match missing registry [ scrutinee ] covered with
        | Some [ witness ] ->
            observe
            |> Option.iter (fun f ->
                match (try Some(missingClauses registry scrutinee covered) with Undecidable -> None) with
                | Some patterns -> f { Range = range; Patterns = patterns }
                | None -> ())

            failwithf
                $"Pattern Error at %s{formatPos range}: this match does not cover every value. %s{showWitness witness} reaches no clause.%s{coverageNote clauses}"
        | _ -> ()
    with Undecidable ->
        ()

/// A `def` is a match over its scrutinee whose first arm's body is the rest of
/// the body, so it answers the same two questions a match does: is every arm
/// reachable, and is every value covered.
///
/// A clause carrying a failure part answers one more, which is the form's own.
/// **The pattern must be able to fail.** A pattern that always matches leaves
/// the failure part unreachable, and the failure part is the whole reason to
/// write one. It is almost always a typo where a constructor name was not in
/// scope and so parsed as a plain variable: `(def (Some cfg) opts 0)` written
/// against a module that never imported `Some` binds a variable called `Some`
/// to the whole option and takes the binding path every time. Refused rather
/// than warned, because unlike a match clause there is no reading of it that
/// does anything.
///
/// A clause with no failure part has no arms here — a `:propagate` one was
/// given an arm per leftover case during inference — and is held to covering
/// the type by itself.
let private checkDefMatch
    (registry: TraitRegistry)
    (range: Range)
    (scrutinee: HMType)
    (binder: TypedPattern)
    (arms: TDefMatchArm list)
    (parameter: bool)
    =
    try
        let binderRow = [ normalize binder ]

        if not arms.IsEmpty && (missing registry [ scrutinee ] [ binderRow ]).IsNone then
            let what =
                match binder.Node with
                | TPIdent name -> $"`%s{name}` is a plain variable and"
                | TPWildcard -> "a wildcard"
                | _ -> "this pattern"

            failwithf
                $"Pattern Error at %s{formatPos binder.Range}: %s{what} always matches, so the failure part of this def can never run. A binding that cannot fail takes none. If a constructor of that name was meant, it is not in scope here."

        let mutable covered: Row list = [ binderRow ]

        for arm in arms do
            let row = [ normalize arm.Pattern ]

            if not (useful registry [ scrutinee ] covered row) then
                failwithf
                    $"Pattern Error at %s{formatPos arm.Pattern.Range}: this arm can never run — the binding pattern and the arms above it already match everything it does."

            covered <- covered @ [ row ]

        match missing registry [ scrutinee ] covered with
        | Some [ witness ] when parameter ->
            failwithf
                $"Pattern Error at %s{formatPos binder.Range}: a parameter's pattern has to match every value, and this one does not: %s{showWitness witness} reaches nothing. Take the parameter as a name, and match on it in the body."
        | Some [ witness ] when arms.IsEmpty ->
            failwithf
                $"Pattern Error at %s{formatPos range}: this def's pattern does not match every value. %s{showWitness witness} reaches nothing, and a def with no failure part has nowhere to send it. Give it a failure part: :leave-with, :leave, :default or :propagate."
        | Some [ witness ] ->
            failwithf
                $"Pattern Error at %s{formatPos range}: this def does not cover every value. %s{showWitness witness} matches neither the binding pattern nor any arm, and a def has nowhere to send it."
        | _ -> ()
    with Undecidable ->
        ()

/// A top-level `def`'s pattern stands alone, so it has to match every value.
///
/// There is nowhere for a failure to go: what a `def` produces when its pattern
/// does not match is the value of the body it stands in, and a declaration list
/// is not a body. So this is the one binding pattern with no second path, and a
/// witness here is refused rather than sent anywhere.
let private checkDefPattern
    (registry: TraitRegistry)
    (range: Range)
    (scrutinee: HMType)
    (pattern: TypedPattern)
    =
    try
        match missing registry [ scrutinee ] [ [ normalize pattern ] ] with
        | Some [ witness ] ->
            // A pattern that decides by running code covers nothing here, so
            // the counterexample it produces is every value. Saying which is
            // what keeps the message from looking like a mistake.
            let note =
                if runsCode pattern then
                    "\n  A (:view ...) or a (:is ...) covers nothing: whether it matches is only known when it runs."
                else
                    ""

            failwithf
                $"Pattern Error at %s{formatPos range}: this def's pattern does not match every value, and at the top level there is nowhere else for one to go. %s{showWitness witness} reaches nothing. Give its one name a :default, or move the binding into a body, where it can also take :leave-with, :leave or :propagate.%s{note}"
        | _ -> ()
    with Undecidable ->
        ()

/// The registry as the code at `range` sees it: an `#:opaque` union or
/// newtype whose representation that code may not use has no constructors,
/// so a match on one is open and needs a fallback, as a match on a type from
/// outside the language is.
///
/// Code no module was assigned to, which only the REPL produces, sees all.
let private seenFrom (registry: TraitRegistry) (range: Range) : TraitRegistry =
    let hidden =
        registry.OpaqueTypes
        |> Map.filter (fun key _ -> range.Module <> "" && not (representationVisible registry range.Module key))

    if hidden.IsEmpty then
        registry
    else
        { registry with
            Unions = hidden |> Map.fold (fun acc key _ -> Map.remove key acc) registry.Unions
            Newtypes = hidden |> Map.fold (fun acc key _ -> Map.remove key acc) registry.Newtypes }

let rec private checkExpr (registry: TraitRegistry) (expr: TypedExpr) : unit =
    match expr.Node with
    | TMatch(target, clauses) -> checkMatch (seenFrom registry expr.Range) expr.Range target.Type clauses
    | TDefMatch(binder, scrutinee, _, arms) ->
        // A parameter written as a pattern is read as such a def. Its
        // message speaks of the parameter, which is what the program wrote.
        let parameter =
            match scrutinee.Node with
            | TIdent(n, _) -> Parser.isPatternParameter n
            | _ -> false

        checkDefMatch (seenFrom registry expr.Range) expr.Range scrutinee.Type binder arms parameter
    | _ -> ()

    TypeVisitor.children expr |> List.iter (checkExpr registry)

let private checkDecl (registry: TraitRegistry) (decl: TDecl) : unit =
    // A top-level destructuring is the one pattern that is not inside an
    // expression, so it takes a walk of its own — `mapDecl` reaches
    // expressions, and this pattern is beside one rather than in it.
    let rec declaredPatterns (d: TDecl) =
        match d with
        | TDefPattern(pattern, scrutinee, _, r) -> checkDefPattern (seenFrom registry r) r scrutinee.Type pattern
        | TModule(_, inner, _) -> inner |> List.iter declaredPatterns
        | TImpl(_, _, _, _, _, _, methods, _) -> methods |> List.iter declaredPatterns
        | _ -> ()

    declaredPatterns decl

    decl
    |> TypeVisitor.mapDecl (fun e ->
        checkExpr registry e
        e)
    |> ignore

/// Reports every declaration with a match that does not cover its type, not
/// just the first. The declarations are independent here, so a failure costs
/// the rest of that declaration and nothing beyond it.
///
/// A module's declarations come inside its `TModule`, so they are taken out
/// of it to be checked one by one.
let run (registry: TraitRegistry) (decls: TDecl list) : unit =
    let rec each (d: TDecl) =
        match d with
        | TModule(_, inner, _) -> inner |> List.iter each
        | _ -> Diagnostics.recover "exhaustiveness" (Some(tdeclRange d)) () (fun () -> checkDecl registry d)

    decls |> List.iter each
