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

/// The unit table: dimensions, units and prefixes, and the unit-expression
/// parser that reads names against it.
///
/// - A dimension has a base unit, an atom of the solver keyed `module/name`.
/// - A unit is a factor to the bases and a term over base keys.
/// - A factor is a rational times an integer power of pi.
///
/// See Docs/Units.org.
module Bjolang.UnitTable

open System
open System.Numerics
open Bjolang.Units

/// rat × pi^pi.
type Factor = { Rat: Rational; Pi: int }

module Factor =
    let one = { Rat = Rational.one; Pi = 0 }
    let ofRational (q: Rational) = { Rat = q; Pi = 0 }
    let pi = { Rat = Rational.one; Pi = 1 }
    let isOne (f: Factor) = f = one
    let mul (a: Factor) (b: Factor) = { Rat = Rational.mul a.Rat b.Rat; Pi = a.Pi + b.Pi }
    let div (a: Factor) (b: Factor) = { Rat = Rational.div a.Rat b.Rat; Pi = a.Pi - b.Pi }
    let inv (f: Factor) = div one f

    /// The integer q-th root of n, if n is a q-th power.
    let private exactRoot (n: BigInteger) (q: int) : BigInteger option =
        if n.Sign < 0 then
            None
        elif n.IsZero || n.IsOne then
            Some n
        else
            // Newton from a double estimate, then a check either side.
            let estimate = BigInteger(Math.Round(Math.Exp(BigInteger.Log n / float q)))

            [ estimate - BigInteger.One; estimate; estimate + BigInteger.One ]
            |> List.tryFind (fun r -> r.Sign >= 0 && BigInteger.Pow(r, q) = n)

    let private powInt (q: Rational) (k: int) : Rational =
        if k >= 0 then
            Rational.make (BigInteger.Pow(q.Num, k)) (BigInteger.Pow(q.Den, k))
        else
            Rational.make (BigInteger.Pow(q.Den, -k)) (BigInteger.Pow(q.Num, -k))

    /// f^e, if it is exact: a root must be of a perfect power, and pi's
    /// exponent must stay an integer.
    let pow (f: Factor) (e: Rational) : Factor option =
        if e.Den > BigInteger 64 || BigInteger.Abs e.Num > BigInteger 64 then
            None
        else
            let p = int e.Num
            let q = int e.Den
            let piExp = f.Pi * p

            if piExp % q <> 0 then
                None
            else
                match exactRoot f.Rat.Num q, exactRoot f.Rat.Den q with
                | Some n, Some d -> Some { Rat = powInt (Rational.make n d) p; Pi = piExp / q }
                | _ -> None

    let toDouble (f: Factor) : float =
        let r = float f.Rat.Num / float f.Rat.Den

        let r =
            if Double.IsFinite r then r
            else Math.Exp(BigInteger.Log f.Rat.Num - BigInteger.Log f.Rat.Den)

        r * Math.Pow(Math.PI, float f.Pi)

    /// "1000", "1/3", "pi/180".
    let toString (f: Factor) : string =
        let rat = Rational.toString f.Rat

        match f.Pi with
        | 0 -> rat
        | k ->
            let piPart = if k = 1 then "pi" else $"pi^%d{k}"
            if f.Rat = Rational.one then piPart else $"%s{rat} %s{piPart}"

/// A value of a unit expression: a factor and a term over base keys.
type Quantity = { Factor: Factor; Term: UnitTerm }

module Quantity =
    let one = { Factor = Factor.one; Term = Units.one }
    let mul (a: Quantity) (b: Quantity) = { Factor = Factor.mul a.Factor b.Factor; Term = Units.mul a.Term b.Term }
    let div (a: Quantity) (b: Quantity) = { Factor = Factor.div a.Factor b.Factor; Term = Units.div a.Term b.Term }

type Dimension =
    { Name: string
      Module: string
      /// The base unit's name.
      Base: string
      /// The base unit's atom.
      Key: string
      /// The type name a quantity of this dimension has when nothing else says.
      Carrier: string option }

type UnitEntry =
    { Name: string
      Module: string
      Value: Quantity }

type PrefixEntry =
    { Name: string
      Module: string
      Factor: Factor }

type Table =
    { Dimensions: Map<string, Dimension list>
      /// Dimensions by their base's key.
      Bases: Map<string, Dimension>
      Units: Map<string, UnitEntry list>
      Prefixes: Map<string, PrefixEntry list>
      /// Each module's declarations as source, in the order written: what
      /// its metadata publishes.
      Sources: (string * string) list }

let empty: Table =
    { Dimensions = Map.empty
      Bases = Map.empty
      Units = Map.empty
      Prefixes = Map.empty
      Sources = [] }

/// The atom of a base unit declared in a module.
let baseKey (moduleKey: string) (name: string) = moduleKey + "/" + name

/// The name in a key: what a unit is called in a message.
let nameOfKey (key: string) =
    match key.LastIndexOf '/' with
    | i when i >= 0 -> key.Substring(i + 1)
    | _ -> key

/// A term as text, by unit names: "kg m^2/s^2".
let showTerm (t: UnitTerm) : string =
    if Units.isOne (Units.prune t) then "1" else Units.toStringWith nameOfKey (fun c -> $"?%d{c.UId}") t

/// "length", "length/time^2", "1": a term's dimension, by dimension names.
let showDimension (table: Table) (t: UnitTerm) : string =
    let name key =
        match Map.tryFind key table.Bases with
        | Some d -> d.Name
        | None -> nameOfKey key

    if Units.isOne (Units.prune t) then "1" else Units.toStringWith name (fun c -> $"?%d{c.UId}") t

/// Several modules may declare one name; the current module's wins, and
/// otherwise there must be one.
let private pick (current: string) (what: string) (name: string) (moduleOf: 'a -> string) (entries: 'a list) : Result<'a, string> option =
    match entries with
    | [] -> None
    | [ e ] -> Some(Ok e)
    | many ->
        match many |> List.tryFind (fun e -> moduleOf e = current) with
        | Some own -> Some(Ok own)
        | None ->
            let modules = many |> List.map moduleOf |> List.distinct |> String.concat " and "
            Some(Error $"the %s{what} '%s{name}' is declared in %s{modules}, and it is not clear which is meant here")

let private lookupUnitExact (table: Table) (current: string) (name: string) =
    Map.tryFind name table.Units
    |> Option.bind (pick current "unit" name (fun (u: UnitEntry) -> u.Module))

let private lookupPrefix (table: Table) (current: string) (name: string) =
    Map.tryFind name table.Prefixes
    |> Option.bind (pick current "prefix" name (fun (p: PrefixEntry) -> p.Module))

/// Every name a module can write: units, and nothing prefixed.
let unitNames (table: Table) : string list = table.Units |> Map.toList |> List.map fst

let private editDistance (a: string) (b: string) : int =
    let d = Array2D.create (a.Length + 1) (b.Length + 1) 0

    for i in 0 .. a.Length do
        d[i, 0] <- i

    for j in 0 .. b.Length do
        d[0, j] <- j

    for i in 1 .. a.Length do
        for j in 1 .. b.Length do
            let cost = if a[i - 1] = b[j - 1] then 0 else 1
            d[i, j] <- min (min (d[i - 1, j] + 1) (d[i, j - 1] + 1)) (d[i - 1, j - 1] + cost)

    d[a.Length, b.Length]

let private unknownUnit (table: Table) (name: string) : string =
    let near =
        unitNames table
        |> List.map (fun n -> n, editDistance (n.ToLowerInvariant()) (name.ToLowerInvariant()))
        |> List.filter (fun (n, d) -> d <= max 1 (name.Length / 3))
        |> List.sortBy snd
        |> List.truncate 3
        |> List.map (fun (n, _) -> $"'%s{n}'")

    let hint =
        match near with
        | [] when Map.isEmpty table.Units -> ". No units are declared here: import a module that declares them, as (import (std units)), or declare them with def/dimension and def/unit"
        | [] -> ""
        | _ -> ". Did you mean " + String.concat " or " near + "?"

    $"unknown unit '%s{name}'%s{hint}"

/// A name: exact, then a prefix and a unit (longest prefix first), then a
/// trailing integer power (`m2`).
let lookupUnit (table: Table) (current: string) (name: string) : Result<Quantity, string> =
    // Exact, then the longest prefix whose rest is a unit.
    let plain (n: string) : Result<Quantity, string> option =
        match lookupUnitExact table current n with
        | Some r -> Some(Result.map (fun (u: UnitEntry) -> u.Value) r)
        | None ->
            [ n.Length - 1 .. -1 .. 1 ]
            |> List.tryPick (fun i ->
                match lookupPrefix table current (n.Substring(0, i)), lookupUnitExact table current (n.Substring i) with
                | Some(Ok p), Some(Ok u) -> Some(Ok { u.Value with Factor = Factor.mul p.Factor u.Value.Factor })
                | Some(Error e), Some _
                | Some _, Some(Error e) -> Some(Error e)
                | _ -> None)

    let powered () =
        let stemLength = name.Length - (name.ToCharArray() |> Array.rev |> Array.takeWhile Char.IsDigit |> Array.length)

        if stemLength > 0 && stemLength < name.Length && name.Length - stemLength <= 2 then
            let k = Rational.ofInt (int (name.Substring stemLength))

            plain (name.Substring(0, stemLength))
            |> Option.map (
                Result.bind (fun q ->
                    match Factor.pow q.Factor k with
                    | Some f -> Ok { Factor = f; Term = Units.pow q.Term k }
                    | None -> Error $"'%s{name}' is too large a power")
            )
        else
            None

    match plain name with
    | Some r -> r
    | None ->
        match powered () with
        | Some r -> r
        | None -> Error(unknownUnit table name)

// ---------------------------------------------------------------------------
// The unit-expression parser
// ---------------------------------------------------------------------------

type private Token =
    | TNumber of Rational
    | TName of string
    | TOp of char

let private operators = set [ '*'; '/'; '^'; '|'; '('; ')' ]

/// "9.81", "1e-3", "1.5E+3", ".5": exact.
let parseDecimal (text: string) : Rational option =
    let text = text.Trim()
    let mantissa, exponent =
        match text.IndexOfAny([| 'e'; 'E' |]) with
        | -1 -> text, 0
        | i ->
            match Int32.TryParse(text.Substring(i + 1), Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture) with
            | true, e -> text.Substring(0, i), e
            | _ -> "", 0

    let negative = mantissa.StartsWith "-"
    let mantissa = mantissa.TrimStart('-', '+')
    let whole, frac =
        match mantissa.IndexOf '.' with
        | -1 -> mantissa, ""
        | i -> mantissa.Substring(0, i), mantissa.Substring(i + 1)

    let digitsOnly (s: string) = s |> Seq.forall Char.IsDigit

    if (whole = "" && frac = "") || not (digitsOnly whole) || not (digitsOnly frac) then
        None
    else
        let n = BigInteger.Parse((if whole = "" then "0" else whole) + frac)
        let n = if negative then -n else n
        let scale = exponent - frac.Length

        Some(
            if scale >= 0 then Rational.make (n * BigInteger.Pow(BigInteger 10, scale)) BigInteger.One
            else Rational.make n (BigInteger.Pow(BigInteger 10, -scale))
        )

let private tokenize (text: string) : Result<Token list, string> =
    let n = text.Length

    let rec go i acc =
        if i >= n then
            Ok(List.rev acc)
        else
            let c = text[i]

            if Char.IsWhiteSpace c then
                go (i + 1) acc
            elif Set.contains c operators then
                go (i + 1) (TOp c :: acc)
            elif Char.IsDigit c || (c = '.' && i + 1 < n && Char.IsDigit text[i + 1]) then
                let mutable j = i

                while j < n && (Char.IsDigit text[j] || text[j] = '.') do
                    j <- j + 1

                // An exponent only when a digit follows: `2e` is 2 and the unit e.
                if j < n && (text[j] = 'e' || text[j] = 'E') then
                    let k = if j + 1 < n && (text[j + 1] = '-' || text[j + 1] = '+') then j + 2 else j + 1

                    if k < n && Char.IsDigit text[k] then
                        j <- k

                        while j < n && Char.IsDigit text[j] do
                            j <- j + 1

                let spelled = text.Substring(i, j - i)

                match parseDecimal spelled with
                | Some q -> go j (TNumber q :: acc)
                | None -> Error $"'%s{spelled}' is not a number"
            else
                let mutable j = i

                while j < n && not (Char.IsWhiteSpace text[j]) && not (Set.contains text[j] operators) do
                    j <- j + 1

                go j (TName(text.Substring(i, j - i)) :: acc)

    go 0 []

/// How names in an expression are read.
type Reading =
    { Table: Table
      Module: string
      /// Whether `%u` is a unit variable here (types) or an error (literals,
      /// definitions).
      AllowVariables: bool }

/// Reads a unit expression: a factor, and a term over base keys and unit
/// variables.
///
/// - expr := jux (('*' | '/') jux)*, left to right.
/// - jux := power power*: juxtaposition, tighter than `/`.
/// - power := primary ('^' exponent)?
/// - primary := number ('|' number)? | name | '(' expr ')'
/// - exponent := int ('|' int)? | '(' int ('/' int)? ')', signed.
let parse (reading: Reading) (text: string) : Result<Quantity, string> =
    let fail msg = raise (ArgumentException msg)

    let mutable tokens =
        match tokenize text with
        | Ok ts -> ts
        | Error e -> fail e

    let peek () = List.tryHead tokens

    let next () =
        match tokens with
        | t :: rest ->
            tokens <- rest
            t
        | [] -> fail "the expression ends too early"

    let expect c =
        match next () with
        | TOp d when d = c -> ()
        | _ -> fail $"expected '%c{c}'"

    let signedInt () : BigInteger =
        match next () with
        | TNumber q when Rational.isInteger q -> q.Num
        | TName "-" -> fail "write a negative exponent as ^-2"
        | TName s when s.StartsWith "-" ->
            match BigInteger.TryParse(s.Substring 1) with
            | true, v -> -v
            | _ -> fail $"'%s{s}' is not an integer exponent"
        | _ -> fail "an exponent is an integer, or two around | or /"

    let exponent () : Rational =
        match peek () with
        | Some(TOp '(') ->
            next () |> ignore
            let p = signedInt ()

            let q =
                match peek () with
                | Some(TOp '/')
                | Some(TOp '|') ->
                    next () |> ignore
                    signedInt ()
                | _ -> BigInteger.One

            expect ')'
            if q.IsZero then fail "an exponent with denominator 0"
            Rational.make p q
        | _ ->
            let p = signedInt ()

            match peek () with
            | Some(TOp '|') ->
                next () |> ignore
                let q = signedInt ()
                if q.IsZero then fail "an exponent with denominator 0"
                Rational.make p q
            | _ -> Rational.make p BigInteger.One

    let name (n: string) : Quantity =
        if n = "pi" then
            { Factor = Factor.pi; Term = Units.one }
        elif n.StartsWith "%" && n.Length > 1 then
            if reading.AllowVariables then
                { Factor = Factor.one; Term = Units.ofAtom (UVar("'" + n.Substring 1)) }
            else
                fail $"'%s{n}' is a unit variable, which only a type may have"
        else
            match lookupUnit reading.Table reading.Module n with
            | Ok q -> q
            | Error e -> fail e

    let rec expr () : Quantity =
        let mutable acc = jux ()
        let mutable going = true

        while going do
            match peek () with
            | Some(TOp '*') ->
                next () |> ignore
                acc <- Quantity.mul acc (jux ())
            | Some(TOp '/') ->
                next () |> ignore
                acc <- Quantity.div acc (jux ())
            | _ -> going <- false

        acc

    and jux () : Quantity =
        let mutable acc = power ()
        let mutable going = true

        while going do
            match peek () with
            | Some(TNumber _)
            | Some(TName _)
            | Some(TOp '(') -> acc <- Quantity.mul acc (power ())
            | _ -> going <- false

        acc

    and power () : Quantity =
        let base' = primary ()

        match peek () with
        | Some(TOp '^') ->
            next () |> ignore
            let e = exponent ()

            match Factor.pow base'.Factor e with
            | Some f -> { Factor = f; Term = Units.pow base'.Term e }
            | None -> fail $"the factor %s{Factor.toString base'.Factor} to the power %s{Rational.toString e} is not exact"
        | _ -> base'

    and primary () : Quantity =
        match next () with
        | TNumber q ->
            match peek () with
            | Some(TOp '|') ->
                next () |> ignore

                match next () with
                | TNumber d when not (Rational.isZero d) -> { Factor = Factor.ofRational (Rational.div q d); Term = Units.one }
                | _ -> fail "| divides two numbers"
            | _ -> { Factor = Factor.ofRational q; Term = Units.one }
        | TName n -> name n
        | TOp '(' ->
            let inner = expr ()
            expect ')'
            inner
        | TOp c -> fail $"'%c{c}' where a unit or a number was expected"

    try
        if List.isEmpty tokens then
            Error "an empty unit expression"
        else
            let q = expr ()

            match tokens with
            | [] -> Ok q
            | TOp c :: _ -> Error $"'%c{c}' where nothing more was expected"
            | _ -> Error "more after the end of the expression"
    with :? ArgumentException as e ->
        Error e.Message

// ---------------------------------------------------------------------------
// Declarations
// ---------------------------------------------------------------------------

let private validName (n: string) =
    n <> ""
    && n <> "pi"
    && not (Char.IsDigit n[0])
    && not (n.StartsWith "%" && n.Length > 1)
    && n |> Seq.forall (fun c -> not (Char.IsWhiteSpace c) && not (Set.contains c operators) && c <> '<' && c <> '>')

let private checkNewUnitName (table: Table) (current: string) (n: string) : Result<unit, string> =
    if not (validName n) then
        Error $"'%s{n}' cannot be a unit's name: a name does not start with a digit and has none of * / ^ | ( ) < >"
    else
        match Map.tryFind n table.Units with
        | Some entries when entries |> List.exists (fun u -> u.Module = current) ->
            Error $"the unit '%s{n}' is already declared in this module"
        | _ -> Ok()

let private addUnit (table: Table) (entry: UnitEntry) : Table =
    let others = Map.tryFind entry.Name table.Units |> Option.defaultValue []
    { table with Units = Map.add entry.Name (others @ [ entry ]) table.Units }

/// `(def/dimension name #:base unit #:carrier type)`.
let declareDimension (table: Table) (current: string) (name: string) (baseName: string) (carrier: string option) (source: string) : Result<Table, string> =
    let existing = Map.tryFind name table.Dimensions |> Option.defaultValue []

    if existing |> List.exists (fun d -> d.Module = current) then
        Error $"the dimension '%s{name}' is already declared in this module"
    else
        checkNewUnitName table current baseName
        |> Result.map (fun () ->
            let key = baseKey current baseName

            let dim =
                { Name = name
                  Module = current
                  Base = baseName
                  Key = key
                  Carrier = carrier }

            let table =
                addUnit
                    table
                    { Name = baseName
                      Module = current
                      Value = { Factor = Factor.one; Term = Units.ofAtom (UName key) } }

            { table with
                Dimensions = Map.add name (existing @ [ dim ]) table.Dimensions
                Bases = Map.add key dim table.Bases
                Sources = table.Sources @ [ current, source ] })

/// `(def/unit name "definition")`.
let declareUnit (table: Table) (current: string) (name: string) (definition: string) (source: string) : Result<Table, string> =
    checkNewUnitName table current name
    |> Result.bind (fun () ->
        parse { Table = table; Module = current; AllowVariables = false } definition
        |> Result.mapError (fun e -> $"in the definition of '%s{name}': %s{e}"))
    |> Result.bind (fun value ->
        if Rational.isZero value.Factor.Rat then
            Error $"'%s{name}' is defined as zero"
        else
            let table = addUnit table { Name = name; Module = current; Value = value }
            Ok { table with Sources = table.Sources @ [ current, source ] })

/// `(def/prefix name number)`.
let declarePrefix (table: Table) (current: string) (name: string) (value: string) (source: string) : Result<Table, string> =
    let existing = Map.tryFind name table.Prefixes |> Option.defaultValue []

    if not (validName name) then
        Error $"'%s{name}' cannot be a prefix's name"
    elif existing |> List.exists (fun p -> p.Module = current) then
        Error $"the prefix '%s{name}' is already declared in this module"
    else
        match parse { Table = empty; Module = current; AllowVariables = false } value with
        | Ok q when Units.isOne q.Term && not (Rational.isZero q.Factor.Rat) ->
            let entry = { Name = name; Module = current; Factor = q.Factor }

            Ok
                { table with
                    Prefixes = Map.add name (existing @ [ entry ]) table.Prefixes
                    Sources = table.Sources @ [ current, source ] }
        | _ -> Error $"the prefix '%s{name}' is a number other than zero, as 1000 or 1|1000"

/// The declarations a module published, in order.
let sourcesOf (table: Table) (moduleKey: string) : string list =
    table.Sources |> List.filter (fun (m, _) -> m = moduleKey) |> List.map snd

/// The carrier a term's dimensions agree on, if they declare one.
let defaultCarrier (table: Table) (t: UnitTerm) : string option =
    let carriers =
        (Units.prune t).Exps
        |> Map.toList
        |> List.choose (fun (atom, _) ->
            match atom with
            | UName key -> Map.tryFind key table.Bases |> Option.map (fun d -> d.Carrier)
            | _ -> None)
        |> List.distinct

    match carriers with
    | [ Some c ] -> Some c
    | _ -> None
