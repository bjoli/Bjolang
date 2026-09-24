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

module Bjolang.TypeSyntax

open Lexer
open Bjolang.Ast
open Bjolang.Hygiene

/// `(dyn ->str)`, `(dyn Foldable #:item int)` — a trait object type.
///
/// `dyn` is a keyword only in type positions. A standalone trait name in a
/// signature still represents an unboxed unknown `TCon`.
///
/// Each associated type is pinned by name as `TName "#:item"` before its type.
///
/// `parseInner` is passed in because dynamic types are parsed in both standalone
/// type positions and function arrow parameter lists.
let parseDynType (parseInner: SExpr -> FType) (traitExpr: SExpr) (assocItems: SExpr list) (r: Range) : FType =
    let traitName =
        match stripTypeMark traitExpr with
        | SAtom { Token = Symbol name } -> name
        | _ ->
            failwithf
                $"Syntax error at %s{Lexer.formatPos r}: (dyn ...) names a trait, as in (dyn ->str) or (dyn Foldable #:item int)."

    let rec assocs items =
        match items with
        | [] -> []
        | SAtom { Token = Keyword assocName } :: typeExpr :: rest ->
            TName("#:" + assocName, r) :: parseInner typeExpr :: assocs rest
        | _ ->
            failwithf
                $"Syntax error at %s{Lexer.formatPos r}: every associated type of '%s{traitName}' is pinned by name in a dyn type, as in (dyn %s{traitName} #:item int)."

    TApp("dyn", TName(traitName, r) :: assocs assocItems, r)

/// `(out T)` anywhere but directly as a parameter of an arrow.
///
/// The marker says that a .NET method writes this parameter rather than reads
/// it, so it only means something as one of the method's own parameters. Only
/// an `import/extern` signature can use it; `Annotations.resolveTypeAnnotation`
/// refuses the arrows of every other signature.
let private rejectOutMarker (r: Range) : 'a =
    failwithf
        $"Syntax error at %s{Lexer.formatPos r}: (out T) marks an out parameter of a .NET method, so it may only stand directly as a parameter of an import/extern signature — not inside another type, not as a result, and not as a keyword or rest parameter."

let parseArrowType (colour: Colour) (items: SExpr list) (r: Range) : FType =
    if items.IsEmpty then failwithf $"Arrow type must have at least a return type at %s{Lexer.formatPos r}"
    let returnTypeExpr = List.last items
    let argItems = List.take (items.Length - 1) items

    let rec parseArrowTypeInner (s: SExpr) : FType =
        let r = getRange s
        match stripTypeMark s with
        | SList(SAtom { Token = Symbol "out" } :: _, _) -> rejectOutMarker r
        | SAtom { Token = QuotedSymbol sym } -> TName("'" + sym, r)
        | SAtom { Token = Symbol sym }
        | SAtom { Token = TypeVar sym } -> TName(sym, r)
        // A trait object type as a function parameter. Retains keyword arguments
        // so arrow type parsing does not misinterpret them as parameter names.
        | SList(SAtom { Token = Symbol "dyn" } :: traitExpr :: assocItems, _) ->
            parseDynType parseArrowTypeInner traitExpr assocItems r
        | SList(SAtom { Token = Symbol name } :: typeArgs, _) -> TApp(name, List.map parseArrowTypeInner typeArgs, r)
        // A type variable in *applied* position: `(%m %a)`. Only an inline
        // trait's constructor variable can be written this way, and the leading
        // quote is what tells `resolveTemplate` it is the hole rather than a
        // constructor named `m`.
        | SList(SAtom { Token = QuotedSymbol sym } :: typeArgs, _) ->
            TApp("'" + sym, List.map parseArrowTypeInner typeArgs, r)
        | _ -> failwithf $"Invalid type syntax in arrow type at %s{Lexer.formatPos r}"

    // The return type is not a parameter either, and reaches
    // `parseArrowTypeInner` — which cannot tell the two apart — so it is
    // checked here, where the split is known.
    let parseReturnType () =
        match stripTypeMark returnTypeExpr with
        | SList(SAtom { Token = Symbol "-?->" } :: _, _) ->
            failwithf
                $"Syntax error at %s{Lexer.formatPos (getRange returnTypeExpr)}: -?-> says that a *parameter* may be given a function of either colour, and this is the return type. A function that hands one back has to have decided which it is building, and saying otherwise needs an effect variable with a name of its own, which does not exist yet."
        | _ -> parseArrowTypeInner returnTypeExpr

    // A mandatory parameter is the one place `(out T)` is read, and its type
    // goes through `parseArrowTypeInner`, which refuses a nested marker.
    let parseParameter (s: SExpr) : FType =
        match stripTypeMark s with
        | SList([ SAtom { Token = Symbol "out" }; inner ], _) -> TApp("out", [ parseArrowTypeInner inner ], getRange s)
        | SList(SAtom { Token = Symbol "out" } :: _, _) ->
            failwithf $"Syntax error at %s{Lexer.formatPos (getRange s)}: (out T) takes exactly one type."
        | _ -> parseArrowTypeInner s

    let rec collectArgs mandatory keywords argItems =
        match argItems with
        | [] -> TArrow(List.rev mandatory, List.rev keywords, None, parseReturnType (), colour, r)
        | [SAtom { Token = Keyword "rest" }] ->
            failwithf $"Expected rest element type after #:rest at %s{Lexer.formatPos r}"
        | SAtom { Token = Keyword "rest" } :: restTypeExpr :: [] ->
            TArrow(List.rev mandatory, List.rev keywords, Some (parseArrowTypeInner restTypeExpr), parseReturnType (), colour, r)
        | SList(SAtom { Token = Keyword name } :: [ typeExpr ], _) :: rest ->
            collectArgs mandatory ((name, parseArrowTypeInner typeExpr) :: keywords) rest
        | item :: rest when keywords.IsEmpty ->
            collectArgs (parseParameter item :: mandatory) keywords rest
        | _ -> failwithf $"Mandatory types must come before keyword/rest types in arrow type at %s{Lexer.formatPos r}"

    collectArgs [] [] argItems

let rec parseType (s: SExpr) : FType =
    let r = getRange s

    match stripTypeMark s with
    | SAtom { Token = QuotedSymbol sym } -> TName("'" + sym, r)  // %a in source → 'a internally
    | SAtom { Token = Symbol sym }
    | SAtom { Token = TypeVar sym } -> TName(sym, r)
    | SList(SAtom { Token = Symbol "->" } :: arrowArgs, _) -> parseArrowType Ordinary arrowArgs r
    // `(-bjo-> ...)`, an arrow whose calls are yield points. Written only by
    // the metadata serializer: a module that imports a bjoroutine has no
    // `defbjo` to read the colour off, so the published *type* has to carry it.
    // Nothing stops a program spelling one by hand, and `checkDecl` catches the
    // case where it disagrees with the definer.
    | SList(SAtom { Token = Symbol "-bjo->" } :: arrowArgs, _) -> parseArrowType Suspending arrowArgs r
    // `-?->` says a *parameter* may be either colour, so an arrow that is not a
    // parameter has nothing to say with it. This case is every position that is
    // not one: the type of a definition, a record field, a `let` annotation, an
    // element type. A parameter arrow never reaches here — it is read by
    // `parseArrowTypeInner`, which builds a `TApp` for every applied form.
    | SList(SAtom { Token = Symbol "-?->" } :: _, _) ->
        failwithf
            $"Syntax error at %s{Lexer.formatPos r}: -?-> says that a *parameter* may be given a function of either colour, and this arrow is not a parameter.\n  As the type of a definition it would say nothing: a defun is already colour-polymorphic, and a copy of it is made for each colour actually used. If what you need is two different *bodies* rather than two copies of one — because the two halves call different .NET methods — that is defbjouble.\n  As a record field, a let annotation or a return type it would need an effect variable with a name of its own, which does not exist yet."
    | SList(SAtom { Token = Symbol "dyn" } :: traitExpr :: assocItems, _) ->
        parseDynType parseType traitExpr assocItems r
    | SList(SAtom { Token = Symbol "out" } :: _, _) -> rejectOutMarker r
    | SList(SAtom { Token = Symbol name } :: typeArgs, _) -> TApp(name, List.map parseType typeArgs, r)
    // `(%m %a)` — a type variable applied to arguments. See `parseArrowTypeInner`.
    | SList(SAtom { Token = QuotedSymbol sym } :: typeArgs, _) ->
        TApp("'" + sym, List.map parseType typeArgs, r)
    | _ -> failwithf $"Invalid type syntax at %s{Lexer.formatPos r}"

let parseUnionCase (s: SExpr) : UnionCase =
    let r = getRange s

    // Markers are read in one pass over the case rather than partitioned out of
    // it, because `#:tag` takes the item after it: partitioning would leave the
    // tag's name among the payload types, where `parseType` would read it as a
    // type. A keyword is not a type anywhere else, and admitting one there
    // would make `(-> #:literal int)` parse too.
    let takeMarkers (name: string) (items: SExpr list) : SExpr list * CaseMarkers =
        let rec go (types: SExpr list) (markers: CaseMarkers) (rest: SExpr list) =
            match rest with
            | [] -> List.rev types, markers
            | SAtom { Token = Keyword "literal" } :: tl -> go types { markers with IsLiteral = true } tl
            | SAtom { Token = Keyword "rest" } :: tl -> go types { markers with IsRest = true } tl
            | SAtom { Token = Keyword "tag" } :: SAtom { Token = Symbol tag } :: tl ->
                match markers.Tag with
                | Some written ->
                    failwithf
                        $"#:tag on the union case %s{name} at %s{Lexer.formatPos r}: the case is already tagged #:tag %s{written}, and a case carries one name."
                | None -> go types { markers with Tag = Some tag } tl
            | SAtom { Token = Keyword "tag" } :: _ ->
                failwithf
                    $"#:tag on the union case %s{name} at %s{Lexer.formatPos r} takes the name of the tag, as in (CFrom TableRef #:tag from)."
            // Named separately from the unknown markers because it is a thing
            // someone may reasonably expect to work: a record field can be
            // mutable and a case payload cannot. A payload is positional and
            // unnamed, so there would be nothing for a write to name.
            | SAtom { Token = Keyword "mutable" } :: _ ->
                failwithf
                    $"#:mutable on the union case %s{name} at %s{Lexer.formatPos r}: a case payload is positional, so there is no field name for a write to use. Give the case a Record that has the mutable field instead."
            | SAtom { Token = Keyword bad } :: _ ->
                failwithf
                    $"Unknown marker #:%s{bad} on the union case %s{name} at %s{Lexer.formatPos r}. A case takes #:literal, #:tag name and #:rest."
            | item :: tl -> go (item :: types) markers tl

        let types, markers = go [] noCaseMarkers items

        if markers.IsRest && types.Length <> 1 then
            failwithf
                $"#:rest on the union case %s{name} at %s{Lexer.formatPos r}: the tag's arguments are its payload, so the case declares exactly one type — the type every argument is checked against."

        if markers.IsRest && markers.Tag.IsNone then
            failwithf
                $"#:rest on the union case %s{name} at %s{Lexer.formatPos r} says that a *tag's* arguments are its payload. An untagged case's payload already describes the whole literal, so (List T) is how a case of many is written."

        if markers.IsLiteral && types.IsEmpty then
            failwithf
                $"#:literal on %s{name} at %s{Lexer.formatPos r} marks a case that carries nothing. It says which case a literal is injected into, so it belongs on one with a payload."

        types, markers

    match s with
    | SAtom { Token = Symbol name } -> SimpleCase(name, r)
    | SList([ SAtom { Token = Symbol name } ], _) -> SimpleCase(name, r)
    | SList(SAtom { Token = Symbol name } :: tTypes, _) ->
        let types, markers = takeMarkers name tTypes
        DataCase(name, List.map parseType types, markers, r)
    | _ ->
        printfn $"%A{s}"
        failwithf $"Invalid union case at %s{Lexer.formatPos r}"

let parseRecordField (s: SExpr) : RecordField =
    let r = getRange s

    // Markers come after the type, and are taken off before it is parsed for
    // the reason a union case's are: a keyword is not a type anywhere else, and
    // letting `parseType` see one would make `(-> #:mutable int)` parse too.
    let takeMarkers (name: string) (items: SExpr list) =
        let markers, rest =
            items
            |> List.partition (function
                | SAtom { Token = Keyword _ } -> true
                | _ -> false)

        for marker in markers do
            match marker with
            | SAtom { Token = Keyword "mutable" } -> ()
            | SAtom { Token = Keyword bad } ->
                failwithf
                    $"Unknown marker #:%s{bad} on the field '%s{name}' at %s{Lexer.formatPos r}. The only one is #:mutable, which lets the field be written in place by record-set!."
            | _ -> ()

        rest, not markers.IsEmpty

    match s with
    | SList(SAtom { Token = Colon } :: SAtom { Token = Symbol name } :: rest, _) ->
        match takeMarkers name rest with
        | [ tType ], isMutable ->
            { Name = name
              Type = parseType tType
              Mutable = isMutable
              Range = r }
        | _ -> failwithf $"Invalid record field at %s{Lexer.formatPos r}: a field is written (: name type), optionally followed by #:mutable."
    | _ -> failwithf $"Invalid record field at %s{Lexer.formatPos r}"

/// The `(where ...)` of a trait member: `(TraitName %var #:assoc type ...)`
/// constraints, each associated type of the constrained trait pinned by
/// keyword — the same grammar a `dyn` type uses, and for the same reason: the
/// constraint has to say what the associated types are before any implementor
/// is known to answer for them. Pinning to a fresh variable (`#:cursor %k`)
/// is how a member says "don't care"; that variable becomes another
/// method-level generic.
let parseMemberWhere (traitName: string) (constraintExprs: SExpr list) (wr: Range) : MemberConstraint list =
    constraintExprs
    |> List.map (fun c ->
        match c with
        | SList(StrippedSymbol cTrait :: varExpr :: pinItems, cr) ->
            let varName =
                match varExpr with
                | SAtom { Token = QuotedSymbol v } -> "'" + v
                | SAtom { Token = Symbol v } -> v
                | _ ->
                    failwithf
                        $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos cr}: a member's where clause constrains a type variable, as in (where (Iterable %%s #:elem %%item))."

            let rec pins items =
                match items with
                | [] -> []
                | SAtom { Token = Keyword assocName } :: typeExpr :: rest ->
                    (assocName, parseType typeExpr) :: pins rest
                | _ ->
                    failwithf
                        $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos cr}: every associated type of '%s{cTrait}' is pinned by name in a member's where clause, as in (where (%s{cTrait} %%s #:elem %%item)) — the same way a dyn type pins them."

            { MCTrait = cTrait
              MCVar = varName
              MCPins = pins pinItems
              MCRange = cr }
        | _ ->
            failwithf
                $"Syntax error in def/trait '%s{traitName}' at %s{Lexer.formatPos wr}: a member's where clause holds (TraitName %%var #:assoc type ...) constraints.")

let parseTypeDefHead (head: SExpr) : string * string list =
    match head with
    | SAtom { Token = Symbol name } -> name, []
    | SList(SAtom { Token = Symbol name } :: args, _) ->
        let parseTypeArg = function
            | SAtom { Token = QuotedSymbol ta } -> ta
            | SAtom { Token = Symbol s } -> s // Just in case they are not quoted
            | _ -> failwithf $"Invalid type argument at %s{Lexer.formatPos (getRange head)}"
        name, List.map parseTypeArg args
    | _ -> failwithf $"Invalid type definition head at %s{Lexer.formatPos (getRange head)}"

let parseTypeDef (s: SExpr) : TypeDef =
    let r = getRange s

    // `#:opaque` marks the *declaration* rather than any part of the shape, so
    // it is taken off here — before the shape is looked at — for the reason a
    // record field's `#:mutable` is: a keyword is not a type anywhere else, and
    // every arm below matches an exactly-three-element list.
    let s, isOpaque =
        match s with
        | SList((SAtom { Token = Colon } as colon) :: rest, sr) ->
            let markers, items =
                rest
                |> List.partition (function
                    | SAtom { Token = Keyword _ } -> true
                    | _ -> false)

            for marker in markers do
                match marker with
                | SAtom { Token = Keyword "opaque" } -> ()
                | SAtom { Token = Keyword bad } ->
                    failwithf
                        $"Unknown marker #:%s{bad} on the type definition at %s{Lexer.formatPos r}. The only one is #:opaque, which exports the type's name without its representation."
                | _ -> ()

            SList(colon :: items, sr), not markers.IsEmpty
        | _ -> s, false

    // The third renaming rule, at the shape tag. `Record`, `Struct` and `Union`
    // are dispatched on exactly as a special form's head is, and a template that
    // writes one — which is what a `derive` macro does — arrives with it
    // renamed. Nothing else in a type definition needs it: a type *name* is
    // read through `originalName` and a type inside a field is read by
    // `parseType`, which strips its own.
    let s =
        match s with
        | SList([ (SAtom { Token = Colon } as colon); head; shape ], sr) ->
            SList([ colon; head; stripHeadMark shape ], sr)
        | _ -> s

    match s with
    | SList([ SAtom { Token = Colon }
              head
              SList(SAtom { Token = Symbol(("Record" | "Struct") as kind) } :: fields, _) ],
            _) ->
        let name, typeArgs = parseTypeDefHead head
        let parsedFields = List.map parseRecordField fields

        // A `Struct` is a C# `record struct` — a value type, copied on every
        // assignment and every parameter pass, and one held inside a `List` or
        // a `Map` cannot be addressed at all. A write to a mutable field of one
        // would land on a copy and be lost, silently and unpreventably, so the
        // combination is refused rather than supported badly.
        if kind = "Struct" then
            match parsedFields |> List.tryFind (fun f -> f.Mutable) with
            | Some f ->
                failwithf
                    $"Invalid field '%s{f.Name}' at %s{Lexer.formatPos f.Range}: a Struct may not have a mutable field. A struct is a value type, so it is copied on assignment and a write would land on the copy. Declare '%s{name}' as a Record instead."
            | None -> ()

        { Name = name
          TypeArgs = typeArgs
          Kind = Record(parsedFields, kind = "Struct")
          IsOpaque = isOpaque
          Range = r }
    // `Union`, `Enum`, and `Sum` are accepted tags for sum types.
    | SList([ SAtom { Token = Colon }
              head
              SList(SAtom { Token = Symbol("Union" | "Enum" | "Sum") } :: cases, _) ],
            _) ->
        let name, typeArgs = parseTypeDefHead head
        { Name = name
          TypeArgs = typeArgs
          Kind = Union(List.map parseUnionCase cases)
          IsOpaque = isOpaque
          Range = r }
    // A head with no body. Not a shape source writes: it is what `Exports`
    // publishes an `#:opaque` type as, read back here by the ordinary parser
    // because metadata *is* Bjolang source text.
    | SList([ SAtom { Token = Colon }
              head
              SList(SAtom { Token = Symbol "Opaque" } :: members, _) ],
            _) ->
        let name, typeArgs = parseTypeDefHead head

        let memberNames =
            members
            |> List.map (function
                | SAtom { Token = Symbol m } -> m
                | bad ->
                    failwithf
                        $"Invalid hidden member name in an Opaque type at %s{Lexer.formatPos (getRange bad)}")

        { Name = name
          TypeArgs = typeArgs
          Kind = Opaque memberNames
          IsOpaque = true
          Range = r }
    // Explicit Alias: (: head (Alias aliasType))
    | SList([ SAtom { Token = Colon }
              head
              SList([ SAtom { Token = Symbol "Alias" }; aliasType ], _) ],
            _) ->
        let name, typeArgs = parseTypeDefHead head

        // An alias has no representation to keep back. `resolveTypeAnnotation`
        // expands it wherever it is written, so an opaque one would be a
        // different type inside the module from the one outside.
        if isOpaque then
            failwithf
                $"#:opaque on the alias '%s{name}' at %s{Lexer.formatPos r}: an alias is expanded wherever it is named, so there is no representation to hold back. Declare '%s{name}' as a Record or a Union with one field to make it a type of its own."

        { Name = name
          TypeArgs = typeArgs
          Kind = Alias(parseType aliasType)
          IsOpaque = false
          Range = r }
    // Implicit Alias: (: head aliasType)
    | SList([ SAtom { Token = Colon }; head; aliasType ], _) ->
        let name, typeArgs = parseTypeDefHead head

        if isOpaque then
            failwithf
                $"#:opaque on the alias '%s{name}' at %s{Lexer.formatPos r}: an alias is expanded wherever it is named, so there is no representation to hold back. Declare '%s{name}' as a Record or a Union with one field to make it a type of its own."

        { Name = name
          TypeArgs = typeArgs
          Kind = Alias(parseType aliasType)
          IsOpaque = false
          Range = r }
    | _ -> failwithf $"Invalid type definition at %s{Lexer.formatPos r}"

// ---------------------------------------------------------------------------
// type/derive
// ---------------------------------------------------------------------------
//
// `(type/derive (Eq) (: Point (Record (: x int) (: y int))))` is the type
// declaration and the implementations that follow from its shape.
//
// Syntactic, and it has to be: where a declaration is read there is no registry
// to ask what a type's fields are, only the form declaring them. So this reads
// the same `TypeDef` the `type` form does and writes the implementation out of
// it, in declaration order.
//
// The traits are a list — `(Eq)` today, `(Eq Ord)` later — so that the shape
// does not have to change when there is a second one.

/// Every generated node carries the range of the *field* or *case* it came
/// from, so an unimplementable comparison is reported against the thing in the
/// source that asked for it rather than against the whole declaration.
let private dTrue r = EBool(true, r)
let private dFalse r = EBool(false, r)
let private dAnd r a b = EIf(a, b, dFalse r, r)
let private dEq r a b = EApp(EResolved("=", r), [ a; b ], r)
let private dHash r x = EApp(EResolved("eq-hash", r), [ x ], r)
let private dCombine r a b = EApp(EResolved("hash-combine", r), [ a; b ], r)
let private dInt r (n: int) = EInt(string n, r)

let private dAllOf (r: Range) (items: (Range * Expr) list) : Expr =
    match items with
    | [] -> dTrue r
    | _ ->
        let rec go =
            function
            | [] -> dTrue r
            | [ (_, last) ] -> last
            | (ir, item) :: rest -> dAnd ir item (go rest)

        go items

/// The seed a fold of `hash-combine` starts from, and the tag a union case
/// contributes. Two cases carrying the same payload have to hash differently,
/// which is what the index buys.
let private dHashSeed r = dInt r 17

let private deriveEqForRecord (r: Range) (fields: RecordField list) : Decl list =
    let get (who: string) (f: RecordField) = EGetField(EIdent(who, f.Range), f.Name, f.Range)

    let equals =
        DDefun(
            "=",
            [ MandatoryArg("a", None); MandatoryArg("b", None) ],
            dAllOf r (fields |> List.map (fun f -> f.Range, dEq f.Range (get "a" f) (get "b" f))),
            Ordinary,
            r
        )

    let hash =
        let body =
            fields
            |> List.fold (fun acc f -> dCombine f.Range acc (dHash f.Range (get "v" f))) (dHashSeed r)

        DDefun("eq-hash", [ MandatoryArg("v", None) ], body, Ordinary, r)

    [ equals; hash ]

/// A union: different cases are unequal, and the same case compares its payload
/// positionally.
let private deriveEqForUnion (r: Range) (cases: UnionCase list) : Decl list =
    let parts =
        function
        | SimpleCase(n, cr) -> n, 0, cr
        | DataCase(n, types, _, cr) -> n, types.Length, cr

    let binders (side: string) (arity: int) (cr: Range) =
        List.init arity (fun i -> PIdent($"__d_%s{side}%d{i}", cr))

    let equals =
        let arms =
            cases
            |> List.map (fun c ->
                let name, arity, cr = parts c

                // The inner match is on the *other* value: same case, compare
                // the payloads; any other case, unequal.
                let sameCase =
                    dAllOf
                        cr
                        (List.init arity (fun i ->
                            cr, dEq cr (EIdent($"__d_l%d{i}", cr)) (EIdent($"__d_r%d{i}", cr))))

                let inner =
                    EMatch(
                        EIdent("b", cr),
                        [ PConstruct(name, binders "r" arity cr, cr), None, sameCase
                          PWildcard cr, None, dFalse cr ],
                        cr
                    )

                PConstruct(name, binders "l" arity cr, cr), None, inner)

        DDefun(
            "=",
            [ MandatoryArg("a", None); MandatoryArg("b", None) ],
            EMatch(EIdent("a", r), arms, r),
            Ordinary,
            r
        )

    let hash =
        let arms =
            cases
            |> List.mapi (fun index c ->
                let name, arity, cr = parts c

                let body =
                    List.init arity id
                    |> List.fold
                        (fun acc i -> dCombine cr acc (dHash cr (EIdent($"__d_l%d{i}", cr))))
                        (dInt cr index)

                PConstruct(name, binders "l" arity cr, cr), None, body)

        DDefun("eq-hash", [ MandatoryArg("v", None) ], EMatch(EIdent("v", r), arms, r), Ordinary, r)

    [ equals; hash ]

/// The traits `type/derive` knows how to write, and how.
let private deriveMethods (traitName: string) (td: TypeDef) : Decl list =
    match traitName, td.Kind with
    | "Eq", Record(fields, _) -> deriveEqForRecord td.Range fields
    | "Eq", Union cases -> deriveEqForUnion td.Range cases
    | "Eq", Alias _ ->
        failwithf
            $"Cannot derive at %s{Lexer.formatPos td.Range}: '%s{td.Name}' is a type alias, which is a second spelling of a type rather than a type of its own. Derive for the type it names."
    | _ ->
        failwithf
            $"Cannot derive '%s{traitName}' at %s{Lexer.formatPos td.Range}: the traits that can be derived are Eq."

/// The implementation `traitName` derives for `td`.
///
/// A type with parameters derives a *conditional* implementation — every
/// parameter has to satisfy the same trait, because the fields hold values of
/// those types and comparing one is what the body does.
let private deriveImpl (traitName: string) (td: TypeDef) : Decl =
    let r = td.Range

    let target =
        if td.TypeArgs.IsEmpty then
            TName(td.Name, r)
        else
            TApp(td.Name, td.TypeArgs |> List.map (fun a -> TName("'" + a, r)), r)

    let constraints = td.TypeArgs |> List.map (fun a -> traitName, "'" + a)

    DImpl(traitName, target, [], constraints, [], deriveMethods traitName td, r)

/// `(type/derive (Eq) typedef ...)`, as the declarations it stands for.
let private parseDerive (isRec: bool) (traits: SExpr list) (typeDefForms: SExpr list) (r: Range) : Decl list =
    let traitNames =
        traits
        |> List.map (function
            | SAtom { Token = Symbol name } -> name
            | bad ->
                failwithf
                    $"Syntax error in type/derive at %s{Lexer.formatPos (getRange bad)}: the first form is the list of traits to derive, as in (Eq).")

    if traitNames.IsEmpty then
        failwithf
            $"Syntax error in type/derive at %s{Lexer.formatPos r}: it derives nothing. Write the traits to derive, as in (Eq), or use `type`."

    if typeDefForms.IsEmpty then
        failwithf $"Syntax error in type/derive at %s{Lexer.formatPos r}: it declares no type."

    let typeDefs = List.map parseTypeDef typeDefForms

    let decl = if isRec then DTypeRec(typeDefs, r) else DType(typeDefs, r)

    decl :: [ for t in traitNames do for td in typeDefs -> deriveImpl t td ]

/// The type every macro transformer has. Fixed, and not written by the user:
/// the expander constructs the arguments and consumes the result, so there is
/// nothing here for a program to choose.
let macroTransformerType (r: Range) : FType =
    let syntax = TName("Syntax", r)
    let inject = TArrow([ TName("Symbol", r) ], [], None, syntax, Ordinary, r)
    let compare = TArrow([ syntax; syntax ], [], None, TName("bool", r), Ordinary, r)
    TArrow([ syntax; inject; compare ], [], None, syntax, Ordinary, r)

