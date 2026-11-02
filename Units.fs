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

/// Units of measure in types: unit terms and their unification.
///
/// - A term is a product of atoms to nonzero rational exponents. 1 is empty.
/// - Atoms are named units (module-qualified keys), unit variables (`%u`,
///   rigid) and unit metavariables (solved in place, as `MetaVar` is).
/// - Unification is over the rationals, an abelian group: an equation with
///   an unsolved metavariable always has a most general solution.
///
/// Knows nothing of factors, dimensions or unit names. See Docs/Units.org.
module Bjolang.Units

open System
open System.Numerics

/// A normalized fraction: Den > 0, gcd(Num, Den) = 1.
[<CustomEquality; CustomComparison>]
type Rational =
    { Num: BigInteger
      Den: BigInteger }

    override this.Equals(o) =
        match o with
        | :? Rational as r -> this.Num = r.Num && this.Den = r.Den
        | _ -> false

    override this.GetHashCode() = hash (this.Num, this.Den)

    interface IComparable with
        member this.CompareTo(o) =
            match o with
            | :? Rational as r -> compare (this.Num * r.Den) (r.Num * this.Den)
            | _ -> invalidArg "o" "not a Rational"

module Rational =
    let make (n: BigInteger) (d: BigInteger) : Rational =
        if d.IsZero then
            raise (DivideByZeroException "a rational with denominator 0")

        let g = BigInteger.GreatestCommonDivisor(n, d)
        let s = if d.Sign < 0 then BigInteger.MinusOne else BigInteger.One
        { Num = s * n / g; Den = s * d / g }

    let ofInt (n: int) : Rational = { Num = BigInteger n; Den = BigInteger.One }
    let zero = ofInt 0
    let one = ofInt 1
    let isZero (q: Rational) = q.Num.IsZero
    let isInteger (q: Rational) = q.Den.IsOne
    let add (a: Rational) (b: Rational) = make (a.Num * b.Den + b.Num * a.Den) (a.Den * b.Den)
    let mul (a: Rational) (b: Rational) = make (a.Num * b.Num) (a.Den * b.Den)
    let neg (q: Rational) = { q with Num = -q.Num }
    let div (a: Rational) (b: Rational) = make (a.Num * b.Den) (a.Den * b.Num)

    /// "2", "-1", "1/2", "-3/4".
    let toString (q: Rational) =
        if isInteger q then q.Num.ToString() else $"%O{q.Num}/%O{q.Den}"

    /// "2", "-1", "1/2", "-3/4", "1|2": an integer, or two integers around / or |.
    let tryParse (text: string) : Rational option =
        let parseInt (s: string) =
            match BigInteger.TryParse(s, Globalization.NumberStyles.AllowLeadingSign, Globalization.CultureInfo.InvariantCulture) with
            | true, v -> Some v
            | _ -> None

        match text.Split([| '/'; '|' |]) with
        | [| n |] -> parseInt n |> Option.map (fun v -> make v BigInteger.One)
        | [| n; d |] ->
            match parseInt n, parseInt d with
            | Some a, Some b when not b.IsZero -> Some(make a b)
            | _ -> None
        | _ -> None

/// An unsolved unit, solved in place. Equality and order by id.
[<CustomEquality; CustomComparison>]
type UnitCell =
    { UId: int
      mutable UValue: UnitTerm option
      mutable ULevel: int }

    override this.Equals(o) =
        match o with
        | :? UnitCell as c -> this.UId = c.UId
        | _ -> false

    override this.GetHashCode() = this.UId

    interface IComparable with
        member this.CompareTo(o) =
            match o with
            | :? UnitCell as c -> compare this.UId c.UId
            | _ -> invalidArg "o" "not a UnitCell"

and UnitAtom =
    /// A declared unit, by its module-qualified key.
    | UName of string
    /// A unit variable of a signature: rigid during unification.
    | UVar of string
    | UMeta of UnitCell

/// Atoms to nonzero exponents.
and UnitTerm = { Exps: Map<UnitAtom, Rational> }

/// Why two terms do not unify: both sides, pruned.
type UnitMismatch = { Left: UnitTerm; Right: UnitTerm }

let one: UnitTerm = { Exps = Map.empty }

let isOne (t: UnitTerm) = t.Exps.IsEmpty

let atomPow (a: UnitAtom) (q: Rational) : UnitTerm =
    if Rational.isZero q then one else { Exps = Map.ofList [ a, q ] }

let ofAtom (a: UnitAtom) : UnitTerm = atomPow a Rational.one

let mul (a: UnitTerm) (b: UnitTerm) : UnitTerm =
    let exps =
        b.Exps
        |> Map.fold
            (fun acc atom q ->
                let sum =
                    match Map.tryFind atom acc with
                    | Some p -> Rational.add p q
                    | None -> q

                if Rational.isZero sum then Map.remove atom acc else Map.add atom sum acc)
            a.Exps

    { Exps = exps }

let pow (t: UnitTerm) (q: Rational) : UnitTerm =
    if Rational.isZero q then one
    else { Exps = t.Exps |> Map.map (fun _ p -> Rational.mul p q) }

let inv (t: UnitTerm) : UnitTerm = pow t (Rational.ofInt -1)

let div (a: UnitTerm) (b: UnitTerm) : UnitTerm = mul a (inv b)

let mutable private nextUnitId = 0

/// A fresh unsolved unit at a let-level.
let freshCell (level: int) : UnitCell =
    let id = nextUnitId
    nextUnitId <- nextUnitId + 1
    { UId = id; UValue = None; ULevel = level }

/// Solved metavariables replaced by their solutions, recursively. Compresses
/// the cells it passes through.
let rec prune (t: UnitTerm) : UnitTerm =
    if t.Exps |> Map.exists (fun a _ -> match a with UMeta { UValue = Some _ } -> true | _ -> false) then
        t.Exps
        |> Map.fold
            (fun acc atom q ->
                match atom with
                | UMeta({ UValue = Some solution } as cell) ->
                    let solved = prune solution
                    cell.UValue <- Some solved
                    mul acc (pow solved q)
                | _ -> mul acc (atomPow atom q))
            one
    else
        t

/// The unsolved metavariables of a pruned term, with their exponents.
let metas (t: UnitTerm) : (UnitCell * Rational) list =
    t.Exps
    |> Map.toList
    |> List.choose (fun (a, q) ->
        match a with
        | UMeta({ UValue = None } as c) -> Some(c, q)
        | _ -> None)

/// The unit variables of a term.
let vars (t: UnitTerm) : string list =
    t.Exps |> Map.toList |> List.choose (fun (a, _) -> match a with UVar v -> Some v | _ -> None)

/// Unit variables replaced: what instantiating a signature does to its units.
let substVars (subst: Map<string, UnitTerm>) (t: UnitTerm) : UnitTerm =
    t.Exps
    |> Map.fold
        (fun acc atom q ->
            match atom with
            | UVar v when subst.ContainsKey v -> mul acc (pow subst[v] q)
            | _ -> mul acc (atomPow atom q))
        one

/// Solves a = b, binding at most one metavariable.
///
/// - a / b is reduced to one term. If it is 1 they unify.
/// - Otherwise the newest unsolved metavariable c, exponent e, is bound to
///   rest^(-1/e). Its solution's metavariables are lowered to c's level.
/// - With no metavariable left, the terms differ: atoms and unit variables
///   are rigid.
///
/// Nothing is written when it fails.
let unify (a: UnitTerm) (b: UnitTerm) : Result<unit, UnitMismatch> =
    let a = prune a
    let b = prune b
    let d = div a b

    if isOne d then
        Ok()
    else
        match metas d |> List.sortByDescending (fun (c, _) -> c.UId) with
        | (cell, e) :: _ ->
            let rest = { Exps = Map.remove (UMeta cell) d.Exps }
            let solution = pow rest (Rational.neg (Rational.div Rational.one e))

            for (c, _) in metas solution do
                c.ULevel <- min c.ULevel cell.ULevel

            cell.UValue <- Some solution
            Ok()
        | [] -> Error { Left = a; Right = b }

let private atomOrder (a: UnitAtom) =
    match a with
    | UName _ -> 0
    | UVar _ -> 1
    | UMeta _ -> 2

/// "kg m^2/s^2", "1/s", "m/(s kg)", "m^(1/2)", "1".
///
/// - Names print as `name` gives them; metavariables as `meta` gives them.
/// - Positive exponents first, then a / and the negative ones, in
///   parentheses when there is more than one.
let toStringWith (name: string -> string) (meta: UnitCell -> string) (t: UnitTerm) : string =
    let atomText a =
        match a with
        | UName n -> name n
        | UVar v -> v
        | UMeta c -> meta c

    let part (a, q: Rational) =
        let text = atomText a

        if q = Rational.one then text
        elif Rational.isInteger q then $"%s{text}^%s{Rational.toString q}"
        else $"%s{text}^(%s{Rational.toString q})"

    let entries =
        (prune t).Exps |> Map.toList |> List.sortBy (fun (a, _) -> atomOrder a)

    let pos = entries |> List.filter (fun (_, q) -> q.Num.Sign > 0)
    let neg = entries |> List.filter (fun (_, q) -> q.Num.Sign < 0) |> List.map (fun (a, q) -> a, Rational.neg q)
    let join es = es |> List.map part |> String.concat " "
    let numer = if pos.IsEmpty then "1" else join pos

    match neg with
    | [] -> numer
    | [ _ ] -> $"%s{numer}/%s{join neg}"
    | _ -> $"%s{numer}/(%s{join neg})"

let toString (t: UnitTerm) : string =
    toStringWith id (fun c -> $"?%d{c.UId}") t

// ---------------------------------------------------------------------------
// The test driver: --solve-units <file>
// ---------------------------------------------------------------------------

/// Reads a term written for the solver tests: factors separated by spaces,
/// each `name`, `name^2`, `name^-1` or `name^(1/2)`; `/` divides by the
/// factors after it; `1` is one. `%u` is a unit variable, `?k` a
/// metavariable, anything else a unit name.
let parseTestTerm (meta: string -> UnitCell) (text: string) : Result<UnitTerm, string> =
    let factor (token: string) : Result<UnitTerm, string> =
        let name, exponent =
            match token.IndexOf '^' with
            | -1 -> token, Some Rational.one
            | i ->
                let e = token.Substring(i + 1)
                let e = if e.StartsWith "(" && e.EndsWith ")" then e.Substring(1, e.Length - 2) else e
                token.Substring(0, i), Rational.tryParse e

        match exponent with
        | None -> Error $"'%s{token}' has an exponent that is not a rational"
        | Some _ when name = "" -> Error $"'%s{token}' has no name"
        | Some _ when name = "1" -> Ok one
        | Some q when name.StartsWith "?" -> Ok(atomPow (UMeta(meta name)) q)
        | Some q when name.StartsWith "%" -> Ok(atomPow (UVar name) q)
        | Some q -> Ok(atomPow (UName name) q)

    let tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

    let rec go tokens dividing acc =
        match tokens with
        | [] -> Ok acc
        | "/" :: rest -> go rest true acc
        | token :: rest ->
            match factor token with
            | Ok t -> go rest dividing (if dividing then div acc t else mul acc t)
            | Error e -> Error e

    if tokens.IsEmpty then Error "an empty term" else go tokens false one

/// Runs a solver script and prints a transcript.
///
/// - `a = b` unifies and prints `ok` or the two sides that differ.
/// - `show ?k` prints what ?k is now.
/// - Empty lines and lines starting with `;` are skipped.
/// - Metavariables are shared by the whole file. At the end each is printed
///   with its solution, in order of first mention.
let runSolverFile (path: string) : unit =
    let cells = Collections.Generic.Dictionary<string, UnitCell>()
    let order = Collections.Generic.List<string>()
    let names = Collections.Generic.Dictionary<int, string>()

    let meta name =
        match cells.TryGetValue name with
        | true, c -> c
        | _ ->
            let c = freshCell 0
            cells[name] <- c
            names[c.UId] <- name
            order.Add name
            c

    let show t =
        toStringWith id (fun c -> match names.TryGetValue c.UId with | true, n -> n | _ -> $"?%d{c.UId}") t

    for raw in IO.File.ReadAllLines path do
        let line = raw.Trim()

        if line = "" || line.StartsWith ";" then
            ()
        elif line.StartsWith "level " then
            match line.Substring(6).Split(' ', StringSplitOptions.RemoveEmptyEntries) with
            | [| name; n |] when fst (Int32.TryParse n) -> (meta name).ULevel <- int n
            | _ -> printfn $"%s{line}: parse error: expected level ?k n"
        elif line.StartsWith "show-level " then
            let name = line.Substring(11).Trim()
            printfn $"%s{name} is at level %d{(meta name).ULevel}"
        elif line.StartsWith "show " then
            let name = line.Substring(5).Trim()

            match (meta name).UValue with
            | Some t -> printfn $"%s{name} = %s{show t}"
            | None -> printfn $"%s{name} is unsolved"
        else
            match line.Split(" = ") with
            | [| l; r |] ->
                match parseTestTerm meta l, parseTestTerm meta r with
                | Ok a, Ok b ->
                    match unify a b with
                    | Ok() -> printfn $"%s{line}: ok"
                    | Error m -> printfn $"%s{line}: error: %s{show m.Left} and %s{show m.Right} differ"
                | Error e, _
                | _, Error e -> printfn $"%s{line}: parse error: %s{e}"
            | _ -> printfn $"%s{line}: parse error: expected a = b"

    for name in order do
        match cells[name].UValue with
        | Some t -> printfn $"%s{name} = %s{show t}"
        | None -> printfn $"%s{name} is unsolved"
