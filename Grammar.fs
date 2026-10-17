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

/// `def/grammar`: the reader of the form, and the types a grammar declares.
///
/// A grammar says how a quoted literal of its types is written. Each production
/// is also a type: an `:or` is a union, an `:each` or a sequence is a record,
/// and a single type is an alias. `deriveTypes` makes those declarations, and
/// `DeclParser` puts them in a `DTypeRec` before the `DGrammar`. Matching a
/// literal against the grammar is in `InferExpr`. See
/// `Docs/Grammar-design.org`.
module Bjolang.Grammar

open Lexer
open Bjolang.Ast

let private fail (r: Range) (message: string) : 'a =
    failwithf $"Grammar Error at %s{Lexer.formatPos r}: %s{message}"

/// The lower-case names that are types in a grammar. A lower-case symbol at the
/// head of a list is a keyword, except these.
let terminalNames =
    Set.ofList
        [ "string"; "symbol"; "int"; "long"; "double"; "float"; "char"; "bool"; "byte"; "sbyte"; "short"
          "ushort"; "uint"; "ulong"; "decimal" ]

/// A grammar writes the type of a symbol as `symbol`, as it writes `string`.
let private typeName (name: string) = if name = "symbol" then "Symbol" else name

let rec private renameSymbolType (t: FType) : FType =
    match t with
    | TName(n, r) -> TName(typeName n, r)
    | TApp(n, args, r) -> TApp(typeName n, List.map renameSymbolType args, r)
    | TArrow(ps, kws, rest, ret, c, r) ->
        TArrow(
            List.map renameSymbolType ps,
            kws |> List.map (fun (k, t) -> k, renameSymbolType t),
            Option.map renameSymbolType rest,
            renameSymbolType ret,
            c,
            r
        )

let private parseType (s: SExpr) = renameSymbolType (TypeSyntax.parseType s)

let private isUpper (name: string) = name.Length > 0 && System.Char.IsUpper name[0]

/// What the reader needs to know about the grammar it reads.
type private Scope =
    { Params: Set<string>
      Productions: Set<string>
      HasParams: bool }

let rec private parseItem (scope: Scope) (s: SExpr) : GItem =
    let r = getRange s

    match s with
    | SAtom { Token = Symbol name } -> GType(TName(typeName name, r), r)
    | SAtom { Token = QuotedSymbol q } when scope.Params.Contains q -> GType(TName("'" + q, r), r)
    | SAtom { Token = QuotedSymbol q } -> GKeyword(q, r)
    | SAtom { Token = Keyword k } ->
        fail r $"`:%s{k}` alone is not an item. A combinator is the head of a list: (:or ...), (:each ...) or (:? item)."
    | SAtom _ -> fail r "this is not an item of a grammar. An item is a type, a production, 'keyword or a list."
    | SList([], _) -> GList(None, [], r)
    | SList(SAtom { Token = Keyword "or" } :: alternatives, _) ->
        if alternatives.IsEmpty then
            fail r "(:or) has no alternatives."

        GOr(alternatives |> List.map (parseLabelled scope "an alternative of :or" "(: Case item)"), r)
    | SList(SAtom { Token = Keyword "each" } :: fields, _) ->
        if fields.IsEmpty then
            fail r "(:each) has no items."

        GEach(fields |> List.map (parseLabelled scope "an item of :each" "(: field item)"), r)
    | SList([ SAtom { Token = Keyword "?" }; item ], _) -> GOpt(parseItem scope item, r)
    | SList(SAtom { Token = Keyword "?" } :: _, _) -> fail r "(:? item) takes one item."
    // A list without a keyword, said explicitly. Module metadata writes every
    // such list this way, because its first element can look like a type.
    | SList(SAtom { Token = Keyword "list" } :: elements, _) -> GList(None, parseElements scope elements, r)
    // A keyword, said explicitly. Module metadata writes every keyword this
    // way, because 'kw and the parameter %kw are the same token.
    | SList([ SAtom { Token = Keyword "keyword" }; SAtom { Token = Symbol kw } ], _) -> GKeyword(kw, r)
    | SList(SAtom { Token = Keyword "keyword-list" } :: SAtom { Token = Symbol kw } :: elements, _) ->
        GList(Some kw, parseElements scope elements, r)
    | SList(SAtom { Token = Keyword k } :: _, _) ->
        fail r $"`:%s{k}` is not a combinator. The combinators are :or, :each, :? and :list."
    | SList([ t; SAtom { Token = Keyword "check" }; SAtom { Token = Symbol f } ], _) -> GChecked(parseType t, f, None, r)
    | SList([ t
              SAtom { Token = Keyword "check" }
              SAtom { Token = Symbol f }
              SAtom { Token = Keyword "module" }
              SAtom { Token = Symbol m } ],
            _) -> GChecked(parseType t, f, Some m, r)
    | SList(_ :: SAtom { Token = Keyword "check" } :: _, _) ->
        fail r "a checked item is written (T #:check function), with the type of the atom and the name of the function."
    | SList(SAtom { Token = Symbol "vec-literal" } :: elements, _) -> GVec(parseElements scope elements, r)
    | SList(SAtom { Token = Colon } :: _, _) ->
        fail r "a label (: name item) is an element of a sequence, of :or or of :each, not an item by itself."
    // `'kw` at the head names the keyword whatever its spelling.
    | SList(SAtom { Token = QuotedSymbol q } :: rest, _) when not (scope.Params.Contains q) ->
        GList(Some q, parseElements scope rest, r)
    | SList((SAtom { Token = Symbol name } :: rest) as items, _) ->
        if isUpper name then
            // A production of a generic grammar, applied: `(Cond %test)`.
            // Another production at the head starts a list of items. An
            // upper-case name from outside the grammar is a type.
            if scope.Productions.Contains name then
                if scope.HasParams && not rest.IsEmpty && isTypeArguments rest then
                    GType(parseType s, r)
                else
                    GList(None, parseElements scope items, r)
            else
                GType(parseType s, r)
        elif terminalNames.Contains name || name = "->" then
            if name = "->" then
                GType(parseType s, r)
            else
                GList(None, parseElements scope items, r)
        else
            GList(Some name, parseElements scope rest, r)
    | SList(items, _) -> GList(None, parseElements scope items, r)

/// Whether the rest of a list, after a production's name, are type arguments
/// and not elements. Elements can be repeats, labels and combinators.
and private isTypeArguments (items: SExpr list) =
    items
    |> List.forall (function
        | SAtom { Token = Symbol _ }
        | SAtom { Token = QuotedSymbol _ } -> true
        | SList(SAtom { Token = Symbol n } :: _, _) -> isUpper n
        | _ -> false)

/// `(: Name item)` in `:or` and `:each`. `,T` is allowed as the item.
and private parseLabelled (scope: Scope) (what: string) (shape: string) (s: SExpr) : string * GItem * Range =
    let r = getRange s

    match s with
    | SList(SAtom { Token = Colon } :: SAtom { Token = Symbol name } :: parts, _) ->
        match parts with
        | [ SAtom { Token = Comma }; t ] -> name, GSpliceOnly(parseType t, getRange t), r
        | [ item ] -> name, parseItem scope item, r
        | [] -> fail r $"%s{name} has no item. Write %s{shape}."
        | _ -> fail r $"%s{name} has more than one item. Put them in a list, or in a production of their own."
    | _ -> fail r $"%s{what} is written %s{shape}."

/// The elements of a sequence: items, labels and repeats.
and private parseElements (scope: Scope) (items: SExpr list) : GElem list =
    /// A repeat mark after an item, if there is one, and what follows it.
    let takeRep (rest: SExpr list) : GRep * SExpr list =
        match rest with
        | SAtom { Token = Spread } :: SAtom { Token = Symbol "+" } :: after -> GPlus, after
        | SAtom { Token = Spread } :: after -> GStar, after
        | _ -> GOne, rest

    let rec go (acc: GElem list) (rest: SExpr list) =
        match rest with
        | [] -> List.rev acc
        | (SAtom { Token = Spread } as mark) :: _ ->
            fail (getRange mark) "... repeats the item before it, and there is no item before it."
        | (SAtom { Token = Comma } as comma) :: t :: after ->
            let rep, after = takeRep after

            go
                ({ Label = None
                   Item = GSpliceOnly(parseType t, getRange t)
                   Rep = rep
                   Range = getRange comma }
                 :: acc)
                after
        | SAtom { Token = Comma } as comma :: [] -> fail (getRange comma) ", needs a type after it."
        | SList(SAtom { Token = Colon } :: SAtom { Token = Symbol label } :: parts, lr) :: after ->
            let item, rep =
                match parts with
                | [ SAtom { Token = Comma }; t ] -> GSpliceOnly(parseType t, getRange t), GOne
                | SAtom { Token = Comma } :: t :: marks ->
                    let rep, left = takeRep marks

                    if not left.IsEmpty then
                        fail lr $"the label %s{label} has more than one item."

                    GSpliceOnly(parseType t, getRange t), rep
                | item :: marks ->
                    let rep, left = takeRep marks

                    if not left.IsEmpty then
                        fail lr $"the label %s{label} has more than one item. Put them in a list."

                    parseItem scope item, rep
                | [] -> fail lr $"the label %s{label} has no item."

            go
                ({ Label = Some label
                   Item = item
                   Rep = rep
                   Range = lr }
                 :: acc)
                after
        | item :: after ->
            let rep, after = takeRep after

            go
                ({ Label = None
                   Item = parseItem scope item
                   Rep = rep
                   Range = getRange item }
                 :: acc)
                after

    go [] items

/// Reads `(def/grammar Head form ...)`. `rest` is what follows the head.
///
/// The forms are productions, `(Name elem ...)`, when the first one starts
/// with an upper-case name. Otherwise they are the one production of a grammar
/// with the head's name: `(def/grammar Disp (:or ...))`.
let parse (rulesOnly: bool) (head: SExpr) (forms: SExpr list) (r: Range) : Grammar =
    let name, typeArgs = TypeSyntax.parseTypeDefHead head

    if forms.IsEmpty then
        fail r $"the grammar %s{name} has no productions."

    let isProduction (s: SExpr) =
        match s with
        | SList(SAtom { Token = Symbol n } :: _ :: _, _) -> isUpper n
        | _ -> false

    let productionForms =
        if isProduction forms.Head then
            forms
            |> List.map (fun s ->
                match s with
                | SList(SAtom { Token = Symbol n } :: body, pr) when isUpper n -> n, body, pr
                | other ->
                    fail
                        (getRange other)
                        "a production is written (Name item ...), with an upper-case name. If one form of a grammar is a production, all of them are.")
        else
            [ name, forms, r ]

    let productionNames = productionForms |> List.map (fun (n, _, _) -> n)

    match productionNames |> List.countBy id |> List.tryFind (fun (_, c) -> c > 1) with
    | Some(dup, _) -> fail r $"the production %s{dup} is declared twice."
    | None -> ()

    if List.head productionNames <> name then
        fail
            r
            $"the first production of the grammar %s{name} is %s{List.head productionNames}. The first production is the grammar's own type, so it has the grammar's name."

    let scope =
        { Params = Set.ofList typeArgs
          Productions = Set.ofList productionNames
          HasParams = not typeArgs.IsEmpty }

    { Name = name
      TypeArgs = typeArgs
      Productions =
        productionForms
        |> List.map (fun (n, body, pr) ->
            if body.IsEmpty then
                fail pr $"the production %s{n} is empty."

            { Name = n
              Body = parseElements scope body
              Range = pr })
      RulesOnly = rulesOnly
      Range = r }

// ---------------------------------------------------------------------------
// The types a grammar declares
// ---------------------------------------------------------------------------

/// The kind of type a production is, read off its body.
type ProductionShape =
    /// `(:or ...)`: a union.
    | ShapeUnion of (string * GItem * Range) list
    /// One type: an alias of it.
    | ShapeAlias of GItem
    /// `(kw elem ...)` or `[elem ...]`: a record, read from a nested list.
    | ShapeNested of GItem
    /// `(:each ...)`: a record, read in place.
    | ShapeEach of (string * GItem * Range) list
    /// Several elements: a record, read in place.
    | ShapeSequence of GElem list
    /// `(T #:check f)`: a type the program declares itself, with the
    /// production's name.
    | ShapeChecked of FType * string * string option

let shapeOf (p: GProduction) : ProductionShape =
    match p.Body with
    | [ { Label = None; Rep = GOne; Item = GChecked(t, f, m, _) } ] -> ShapeChecked(t, f, m)
    | [ { Label = None; Rep = GOne; Item = GOr(alternatives, _) } ] -> ShapeUnion alternatives
    | [ { Label = None; Rep = GOne; Item = (GType _ | GSpliceOnly _) as item } ] -> ShapeAlias item
    | [ { Label = None; Rep = GOne; Item = (GList _ | GVec _) as item } ] -> ShapeNested item
    | [ { Label = None; Rep = GOne; Item = GEach(fields, _) } ] -> ShapeEach fields
    | body -> ShapeSequence body

/// `T` for a production of a grammar without parameters, `(T %a ...)` for one
/// with: a bare production name in a generic grammar takes all its
/// parameters.
let applyProductions (g: Grammar) (t: FType) : FType =
    let names = g.Productions |> List.map (fun p -> p.Name) |> Set.ofList

    let rec go (t: FType) =
        match t with
        | TName(n, r) when names.Contains n && not g.TypeArgs.IsEmpty ->
            TApp(n, g.TypeArgs |> List.map (fun a -> TName("'" + a, r)), r)
        | TName _ -> t
        | TApp(n, args, r) -> TApp(n, List.map go args, r)
        | TArrow(ps, kws, rest, ret, c, r) ->
            TArrow(List.map go ps, kws |> List.map (fun (k, t) -> k, go t), Option.map go rest, go ret, c, r)

    go t

let rec private itemRange (item: GItem) =
    match item with
    | GType(_, r)
    | GSpliceOnly(_, r)
    | GKeyword(_, r)
    | GList(_, _, r)
    | GVec(_, r)
    | GOpt(_, r)
    | GOr(_, r)
    | GEach(_, r)
    | GChecked(_, _, _, r) -> r

/// The type of the value an item keeps, or `None` for an item that keeps
/// nothing, as a keyword.
let rec itemValueType (g: Grammar) (item: GItem) : FType option =
    match item with
    | GType(t, _)
    | GSpliceOnly(t, _) -> Some(applyProductions g t)
    | GKeyword _ -> None
    | GOpt(inner, r) ->
        match itemValueType g inner with
        | Some t -> Some(TApp("Option", [ t ], r))
        | None -> Some(TName("bool", r))
    | GList(_, elements, r)
    | GVec(elements, r) ->
        match elements |> List.choose (elementValueType g) with
        | [] -> None
        | [ t ] -> Some t
        | many ->
            fail
                r
                $"this list keeps %d{many.Length} values, and a list item can keep one. Make it a production of its own: a record if its elements have labels."
    | GOr(_, r) -> fail r "an :or inside another item. Make it a production of its own and name the production here."
    | GEach(_, r) ->
        fail r "an :each inside another item. Make it a production of its own, or put it directly in a sequence."
    | GChecked(_, _, _, r) ->
        fail r "a checked item inside another item. Make it a production of its own, (Name (T #:check f)), and name the production here."

/// The type of the value an element keeps: its item's, or a list of them for
/// a repeat.
and elementValueType (g: Grammar) (e: GElem) : FType option =
    match e.Item with
    | GEach(_, r) -> fail r "an :each in a list item. Make the list a production of its own."
    | _ -> ()

    match e.Rep, itemValueType g e.Item with
    | GOne, t -> t
    | _, Some t -> Some(TApp("List", [ t ], e.Range))
    | _, None -> fail e.Range "a repeat of an item that keeps nothing. A repeated keyword is not a value."

/// The fields of a record production. A labelled element is a field. An
/// `:each` in the sequence adds its fields. An element without a label must
/// keep nothing.
let recordFields (g: Grammar) (elements: GElem list) : RecordField list =
    let eachFields (fields: (string * GItem * Range) list) =
        fields
        |> List.map (fun (name, item, r) ->
            match itemValueType g item with
            | Some t ->
                { Name = name
                  Type = t
                  Mutable = false
                  Range = r }
            | None -> fail r $"the item %s{name} keeps nothing. Write it as (:? 'keyword) to keep whether it is there.")

    let fields =
        elements
        |> List.collect (fun e ->
            match e with
            | { Label = None; Rep = GOne; Item = GEach(fields, _) } -> eachFields fields
            | { Label = Some label } ->
                match elementValueType g e with
                | Some t ->
                    [ { Name = label
                        Type = t
                        Mutable = false
                        Range = e.Range } ]
                | None -> fail e.Range $"the label %s{label} is on an item that keeps nothing."
            | _ ->
                match elementValueType g e with
                | None -> []
                | Some _ ->
                    fail
                        e.Range
                        "this element keeps a value, and in a record production every value has a label. Write (: name item).")

    match fields |> List.countBy (fun f -> f.Name) |> List.tryFind (fun (_, c) -> c > 1) with
    | Some(dup, _) -> fail (List.head elements).Range $"the field %s{dup} is given twice."
    | None -> ()

    fields

/// The payload of a case of an `:or`: the values its item keeps, in order.
let casePayload (g: Grammar) (item: GItem) : FType list =
    match item with
    | GList(_, elements, _)
    | GVec(elements, _) -> elements |> List.choose (elementValueType g)
    | GOr(_, r) -> fail r "an :or as an alternative. Make it a production of its own and name the production here."
    | GEach(_, r) -> fail r "an :each as an alternative. Make it a production of its own and name the production here."
    | GChecked(_, _, _, r) ->
        fail r "a checked item as an alternative. Make it a production of its own and name the production here."
    | _ -> itemValueType g item |> Option.toList

/// The type declarations of a grammar: one for each production, except a
/// checked one. Its type is the one its check gives, which the program
/// declares itself.
let deriveTypes (g: Grammar) : TypeDef list =
    g.Productions
    |> List.filter (fun p ->
        match shapeOf p with
        | ShapeChecked _ -> false
        | _ -> true)
    |> List.map (fun p ->
        let kind =
            match shapeOf p with
            | ShapeChecked _ -> failwith "internal: a checked production has no type of its own"
            | ShapeUnion alternatives ->
                alternatives
                |> List.map (fun (case, item, r) ->
                    match casePayload g item with
                    | [] -> SimpleCase(case, r)
                    | payload -> DataCase(case, payload, r))
                |> Union
            | ShapeAlias item -> Alias(itemValueType g item |> Option.get)
            | ShapeNested(GList(_, elements, _))
            | ShapeNested(GVec(elements, _)) -> Record(recordFields g elements, false)
            | ShapeNested other -> fail (itemRange other) "internal: a nested production that is not a list"
            | ShapeEach fields -> Record(recordFields g [ { Label = None; Item = GEach(fields, p.Range); Rep = GOne; Range = p.Range } ], false)
            | ShapeSequence elements -> Record(recordFields g elements, false)

        match kind with
        | Record([], _) ->
            fail
                p.Range
                $"the production %s{p.Name} keeps no value, so it is no type. Put its keywords in the item that uses it."
        | _ -> ()

        { Name = p.Name
          TypeArgs = g.TypeArgs
          Kind = kind
          IsOpaque = false
          Range = p.Range })
