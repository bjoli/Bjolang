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

module Bjolang.Parser

open Lexer
open Bjolang.Ast
open Bjolang.Hygiene
open Bjolang.TypeSyntax

/// One `->` step, threaded over the value that reached it.
///
/// A bare symbol `f` becomes `(f prev)`. A list containing `&` puts `prev` at
/// every `&`; a list without one takes it as its first argument. A `#(...)`
/// inside the step is left alone, because its `&` is the shorthand lambda's
/// own placeholder.
///
/// `(:view step p)` reads its step through this too, so that the threading
/// notation means one thing.
let threadStep (prev: SExpr) (step: SExpr) : SExpr =
    match step with
    | SAtom { Token = Symbol _ } as sym -> SList([ sym; prev ], getRange sym)
    | SList(items, stepR) ->
        let rec replaceListItems (items: SExpr list) : SExpr list * bool =
            match items with
            | [] -> [], false
            | SAtom { Token = Hash } as h :: SList(subItems, subR) :: tail ->
                let rest, foundInRest = replaceListItems tail
                h :: SList(subItems, subR) :: rest, foundInRest
            | head :: tail ->
                let newHead, foundHead = replaceAmpersand head
                let newTail, foundTail = replaceListItems tail
                newHead :: newTail, foundHead || foundTail

        and replaceAmpersand (expr: SExpr) : SExpr * bool =
            match expr with
            | SAtom { Token = Symbol "&" } -> prev, true
            | SList(subItems, subR) ->
                let newItems, found = replaceListItems subItems
                SList(newItems, subR), found
            | _ -> expr, false

        let newItems, hasAmp = replaceListItems items

        if hasAmp then
            SList(newItems, stepR)
        else
            match items with
            | head :: tail -> SList(head :: prev :: tail, stepR)
            | [] -> failwithf $"Invalid empty list in -> macro at %s{Lexer.formatPos stepR}"
    | _ -> failwithf $"Invalid step in -> macro at %s{Lexer.formatPos (getRange step)}"

/// Desugars a syntax template `#'(if ,c ,t ,f)` into the `Syntax` value it
/// describes.
///
/// This is not `desugarQuotedList` with a different payload. `'(...)` builds an
/// `EList`, which is homogeneous — every element has to share a type — and a
/// template is heterogeneous by nature: a symbol, then whatever the unquotes
/// splice in. Everything here is a `Syntax`, so the result types as one thing.
///
/// The two spellings are kept apart rather than switched on context. Deciding
/// by "is this inside a `def/macro`" would mean a helper function in the same
/// module got list semantics for the same `'(...)` its caller got template
/// semantics for, which is not a property a reader can see locally.
///
/// A `Symbol` becomes `SSym`, an *identifier*, which hygiene renames. A
/// `QuotedSymbol` — `'foo` written inside the template — becomes `SDatum`,
/// which it never renames: that is a value, not a reference to a binding. This
/// is also why `''foo` is not needed, and it is just as well, since it does not
/// read.
let desugarSyntaxQuote (parseExprFn: SExpr -> Expr) (template: SExpr) (r: Range) : Expr =
    let call name args range = EApp(EResolved(name, range), args, range)

    let rec go (s: SExpr) : Expr =
        let ir = getRange s

        match s with
        | SAtom { Token = NumberLit n } -> call "SInt" [ EString(n, ir) ] ir
        | SAtom { Token = StringLit str } -> call "SStr" [ EString(str, ir) ] ir
        | SAtom { Token = CharLit c } -> call "SChar" [ EChar(c, ir) ] ir
        | SAtom { Token = Keyword k } -> call "SKey" [ EKeyword(k, ir) ] ir
        | SAtom { Token = QuotedSymbol sym } -> call "SDatum" [ EQuotedSymbol(sym, ir) ] ir
        // A boolean crosses into a template as the symbol it is spelled with.
        // `Syntax` has no boolean node, and `Macro.neverRenamed` already knows
        // these two names, so the round trip is what it always was — only the
        // token on either side of it changed.
        | SAtom { Token = BoolLit b } -> call "SSym" [ EQuotedSymbol((if b then "#t" else "#f"), ir) ] ir
        | SAtom { Token = Symbol sym } -> call "SSym" [ EQuotedSymbol(sym, ir) ] ir
        // The `str` and `->str` of a `#"..."` written inside the template. An
        // `SSym` marked `Resolved`, so hygiene leaves it and the expander
        // lowers it back to the reference the reader wrote.
        | SAtom { Token = ResolvedSymbol sym } -> call "syntax-resolved" [ EQuotedSymbol(sym, ir) ] ir
        | SAtom { Token = Comma } ->
            failwithf $"Unexpected , at %s{Lexer.formatPos ir}: nothing to unquote."
        | SAtom { Token = CommaAt } ->
            failwithf $"Unexpected ,@ at %s{Lexer.formatPos ir}: nothing to splice."
        // Punctuation that survives reading, and that a template has to be able
        // to write: `(: name type)` is a signature, and a macro that expands to
        // a definition has to be able to declare its type beside it. `Syntax`
        // has carried these as `SPunct` all along — `Macro.ofSExpr` hands one to
        // a transformer whenever the *input* contains it — and this was the one
        // direction that could not spell them.
        //
        // `,` and `,@` are not here: inside a template those are the unquote
        // markers, handled above. A macro that needs to *write* one writes
        // `(SPunct ",")`.
        | SAtom { Token = Colon } -> call "SPunct" [ EString(":", ir) ] ir
        | SAtom { Token = Dot } -> call "SPunct" [ EString(".", ir) ] ir
        | SAtom { Token = Spread } -> call "SPunct" [ EString("...", ir) ] ir
        | SAtom _ -> failwithf $"Unsupported item in a syntax template at %s{Lexer.formatPos ir}"
        | SList(items, lr) -> call "SList" [ items0 items lr ] lr

    /// The children of one form, as a `(List Syntax)`.
    ///
    /// `,@` is why this is a fold rather than a map: a splice contributes a
    /// whole list where its siblings contribute one element each, so the result
    /// is built by appending runs rather than by consing uniformly.
    and items0 (items: SExpr list) (lr: Range) : Expr =
        let rec go' remaining (pending: Expr list) : Expr =
            /// Everything gathered since the last splice, as a list literal.
            let flush (tail: Expr) =
                if List.isEmpty pending then tail
                else call "syntax-splice" [ EList(List.rev pending, lr); tail ] lr

            match remaining with
            | [] ->
                if List.isEmpty pending then EList([], lr) else EList(List.rev pending, lr)

            | SAtom { Token = Comma } :: inner :: rest -> go' rest (parseExprFn inner :: pending)
            | [ SAtom { Token = Comma } ] ->
                failwithf $"Unexpected , at end of a syntax template at %s{Lexer.formatPos lr}"

            | SAtom { Token = CommaAt } :: inner :: rest ->
                flush (call "syntax-splice" [ parseExprFn inner; go' rest [] ] lr)
            | [ SAtom { Token = CommaAt } ] ->
                failwithf $"Unexpected ,@ at end of a syntax template at %s{Lexer.formatPos lr}"

            | item :: rest -> go' rest (go item :: pending)

        go' items []

    go template

// Desugar a quoted list '(a ,x b) into (Cons 'a (Cons x (Cons 'b Nil))).
//
// Symbols are literal Symbol data — '(a b c) gives three symbol values, not
// three variable references. To splice a computed value, prefix it with `,`:
// '(a ,x b) evaluates x and conses it between the two symbols.
//
// `parseExprFn` is threaded in as a parameter because this function is defined
// before `parseExpr`; the call site passes `parseExpr` directly.
let desugarQuotedList (parseExprFn: SExpr -> Expr) (items: SExpr list) (r: Range) : Expr =
    // Collect quoted items into a flat list and produce EList, giving '(...)
    // the same inference path as (list ...) and []. Constructor injection
    // (e.g. wrapping a string in ProcBang when the expected type is a union)
    // therefore works on quasiquote literals for free.
    let rec quoteItem (s: SExpr) : Expr =
        let ir = getRange s
        match s with
        | SAtom { Token = NumberLit n } -> EInt(n, ir)
        | SAtom { Token = StringLit str } -> EString(str, ir)
        | SAtom { Token = CharLit c } -> EChar(c, ir)
        | SAtom { Token = Keyword kw } -> EKeyword(kw, ir)
        // Ahead of the symbol case: `'(#t #f)` is a list of booleans, the way
        // `'(1 2)` is a list of ints.
        | SAtom { Token = BoolLit true } -> EBool(true, ir)
        | SAtom { Token = BoolLit false } -> EBool(false, ir)
        // A symbol in a quoted list is a literal Symbol value, not a variable
        // reference — write ,(expr) to splice the value of a variable.
        | SAtom { Token = Symbol sym } -> EQuotedSymbol(sym, ir)
        | SAtom { Token = QuotedSymbol sym } -> EQuotedSymbol(sym, ir)
        // Dotted pair in a quoted list: '(a . b) → (Tuple a b)
        | SList(SAtom { Token = Symbol "Tuple" } :: tupleItems, _) ->
            ETuple(List.map quoteItem tupleItems, ir)
        // `[a b]`, which the reader has already rewritten to
        // `(vec-literal a b)`. Without this the head is quoted like any other
        // symbol and `'(ls ["-l"])` yields a list beginning with the symbol
        // `vec-literal` — a form no program wrote.
        | SList(SAtom { Token = Symbol "vec-literal" } :: vecItems, vr) ->
            EVec(List.map quoteItem vecItems, vr)
        // `#[a b]`, likewise rewritten by the reader, and quoted as the array
        // it was written as.
        | SList(SAtom { Token = Symbol "array-literal" } :: arrayItems, ar) ->
            EArray(List.map quoteItem arrayItems, ar)
        // `{...}`, likewise rewritten by the reader. A comprehension is a loop,
        // not data, so there is nothing to quote it as. The reserved head wins
        // over the symbol of the same name, which is the price of catching it.
        | SList(SAtom { Token = Symbol "comprehension" } :: _, cr) ->
            failwithf
                $"A comprehension inside a quoted list at %s{Lexer.formatPos cr}. Quoting builds data, and a comprehension is a loop that produces a value: write ,{{...}} to splice what it produces."
        // A hash macro call: a `#name` head is nothing a quoted list can hold.
        | SList(SAtom { Token = Symbol name } :: _, hr) when name.StartsWith "#" ->
            failwithf
                $"'%s{name}(...)' inside a quoted list at %s{Lexer.formatPos hr}. Quoting builds data, and a hash macro expands to an expression: write ,%s{name}(...) to splice what it produces."
        // Any other list is data as well: '('(a b) '(c d)) nests.
        //
        // The one remaining reader rewrite arrives here — `#(...)` as a `fun`
        // form — and cannot be told apart from the same list written by hand,
        // so it is quoted as the list it has become. Write `,#(...)` to splice
        // the function.
        | SList(inner, lr) -> collectItems inner lr
        | SAtom { Token = CommaAt } ->
            failwithf
                $"Splicing with ,@ inside '(...) at %s{Lexer.formatPos ir}, which is not supported. A quoted list is built element by element from what is written in it, so a spliced list — whose length is only known when the program runs — has nowhere to go. Write ,x to place one element. ,@ does work inside a #' template, which builds a Syntax value rather than a list."
        | _ -> failwithf $"Unsupported item in quoted list at %s{Lexer.formatPos ir}"

    and collectItems (items: SExpr list) (r: Range) : Expr =
        let rec go acc remaining =
            match remaining with
            | [] -> EList(List.rev acc, r)
            // ,expr — unquote: evaluate and splice the expression as an element.
            | SAtom { Token = Comma } :: inner :: rest ->
                go (parseExprFn inner :: acc) rest
            | SAtom { Token = Comma } :: [] ->
                failwithf $"Unexpected , at end of quoted list at %s{Lexer.formatPos r}"
            | item :: rest ->
                go (quoteItem item :: acc) rest
        go [] items

    collectItems items r

/// The arithmetic and bitwise operators, which left-fold, and the comparisons,
/// which chain.
let private foldingOps =
    [ "+"; "-"; "*"; "/"; "%"; "bitwise-and"; "bitwise-ior"; "bitwise-xor" ]
let private chainingOps = [ "<"; ">"; "<="; ">="; "=" ]

/// The operators `Codegen` emits as C# syntax, and how many operands each takes.
///
/// Applied, an operator becomes infix and never needs a name. Written as a value
/// it has none to be — C# has no `+` to pass — so it becomes the lambda it
/// stands for, and the ordinary application inside that lambda is emitted infix
/// like any other. `negate`, `recip` and `bitwise-not` are the unary ones.
let private operatorArity =
    Map [ "+", 2
          "-", 2
          "*", 2
          "/", 2
          "%", 2
          "=", 2
          "<", 2
          ">", 2
          "<=", 2
          ">=", 2
          "bitwise-and", 2
          "bitwise-ior", 2
          "bitwise-xor", 2
          "shift-left", 2
          "shift-right", 2
          "shift-right-logical", 2
          "negate", 1
          "recip", 1
          "bitwise-not", 1 ]

/// `(op args...)` at whatever arity it was written.
///
/// Up to two operands are read here. `(+)` is 0 and `(*)` is 1, each
/// operator's identity as in Scheme; `(+ x)` is `x`; `(- x)` is `negate` and
/// `(/ x)` is `recip`, because `-` and `/` are binary trait methods with no
/// one-operand meaning of their own and `Codegen` emits the unary names. Two
/// operands are the binary application everything downstream understands, and
/// what keeps the infix emission in `Codegen` free of allocation.
///
/// Three or more are the prelude's: the form is handed to `#fl`, which
/// left-folds the arithmetic and bitwise operators — `(+ a b c)` is
/// `(+ (+ a b) c)` — or to `#ch`, which chains the comparisons — `(< a b c)`
/// is `a < b && b < c`, not `(a < b) < c`, with a middle operand evaluated once
/// and a later one not at all once an earlier test has failed. The expansion
/// is read like any other, and its binary applications land back here.
///
/// `sym` is the head as written and `op` its base name; they differ when the
/// head carried a macro's rename, which the operator table stripped in order
/// to recognise it. Every operator here is a trait method — `=` is `Eq`'s, `<`
/// is `Ord`'s, `+` is `Num`'s — so a template that wrote one meant the method,
/// and the reference resolves where it was written rather than where the
/// expansion lands. `EResolved` is that spelling; without it a module that
/// binds `=` for its own purposes silently redefines the arithmetic of every
/// macro it calls, `type/derive` included. The stripping is why this cannot be
/// left to `Macro.resolveIntroduced` like an ordinary call head: by the time it
/// runs, the mark is gone. The operator goes across to `#fl`/`#ch` as written,
/// mark and all, so the same holds for what they hand back.
let private desugarOperator
    (parseExprFn: SExpr -> Expr)
    (head: SExpr)
    (sym: string)
    (op: string)
    (args: SExpr list)
    (r: Range)
    : Expr =
    let opRef = if op <> sym then EResolved(op, r) else EIdent(op, r)

    let items =
        args
        |> List.filter (function
            | SAtom { Token = Comma } -> false
            | _ -> true)

    let arityError (wanted: string) =
        failwithf
            $"Syntax error at %s{Lexer.formatPos r}: '%s{op}' takes %s{wanted}, but was given %d{items.Length}."

    let folding = List.contains op foldingOps

    match items with
    | [] ->
        match op with
        | "+" -> EInt("0", r)
        | "*" -> EInt("1", r)
        | _ -> arityError (if folding then "at least one argument" else "at least two arguments")
    | [ single ] ->
        match op with
        | "+"
        | "*"
        | "bitwise-and"
        | "bitwise-ior"
        | "bitwise-xor" -> parseExprFn single
        | "-" -> EApp(EResolved("negate", r), [ parseExprFn single ], r)
        | "/" -> EApp(EResolved("recip", r), [ parseExprFn single ], r)
        | _ -> arityError "at least two arguments"
    | [ a; b ] -> EApp(opRef, [ parseExprFn a; parseExprFn b ], r)
    | _ ->
        let macro = if folding then "#fl" else "#ch"
        let form = SList(SAtom { Token = Symbol macro; Range = r } :: head :: items, r)

        match hashExpandHook form with
        | Some expansion -> expansion.Resolve Set.empty (parseExprFn expansion.Form)
        | None ->
            failwithf
                $"'%s{op}' with %d{items.Length} operands at %s{Lexer.formatPos r} is spelled out by the prelude's %s{macro} hash macro, which is not in scope here. Import (std prelude), or nest the binary applications by hand."

/// The `def` shapes that bind a name outright: a name, a name with its type,
/// and a tuple destructuring. Everything else a `def` may bind is a pattern,
/// and so is any `def` carrying a failure part.
///
/// A capitalized head is a constructor and never a tuple of names, which is
/// what tells `(def (a b) pair)` from `(def (Some x) opt)`.
///
/// Read here rather than in `parseBody`, because the top level asks the same
/// question: `DeclParser` takes the plain shapes as definitions and a pattern
/// as one of its own.
let isPlainDefBinder (binder: SExpr) : bool =
    let symbol s =
        match s with
        | SAtom { Token = Symbol _ }
        | SAtom { Token = Comma } -> true
        | _ -> false

    match binder with
    | SAtom { Token = Symbol _ } -> true
    | SList([ SAtom { Token = Colon }; SAtom { Token = Symbol _ }; _ ], _) -> true
    | SList((SAtom { Token = Symbol head } :: _) as names, _) ->
        List.forall symbol names
        && (head = "Tuple" || not (System.Char.IsUpper head[0]))
    | _ -> false

let rec parseExpr (s: SExpr) : Expr =
    let r = getRange s

    let rec processArgs items =
        match items with
        | [] -> []
        | SAtom { Token = Comma } :: rest -> processArgs rest
        | item :: rest -> parseExpr item :: processArgs rest

    // Treat specific operator tokens as valid identifiers in expressions
    let (|Ident|_|) =
        function
        | SAtom { Token = Symbol sym } -> Some sym
        | _ -> None

    match s with
    | SAtom { Token = NumberLit n } -> EInt(n, r)
    | SAtom { Token = StringLit str } -> EString(str, r)
    | SAtom { Token = CharLit c } -> EChar(c, r)
    // Ahead of `Ident` below, which used to rewrite these two to the names
    // `true` and `false` and hand them to the environment to resolve.
    | SAtom { Token = BoolLit true } -> EBool(true, r)
    | SAtom { Token = BoolLit false } -> EBool(false, r)
    | SAtom { Token = ResolvedSymbol name } -> EResolved(name, r)
    | SAtom { Token = QuotedSymbol sym } -> EQuotedSymbol(sym, r)
    | SAtom { Token = Keyword sym } -> EKeyword(sym, r)

    // An operator used as a value, which is the only position this case sees:
    // the head of an application is built by the `SList` branch below and never
    // arrives here. So no analysis is needed to tell the two apart, and none of
    // this depends on types.
    | Ident sym when Map.containsKey (headName sym) operatorArity ->
        let op = headName sym
        let ps = List.init operatorArity[op] (fun _ -> Gensym.fresh "op")
        // The call inside the lambda is in call position like any other, so a
        // marked operator resolves where the template wrote it. See
        // `desugarOperator`.
        let opRef = if op <> sym then EResolved(op, r) else EIdent(op, r)
        EFun(ps, EApp(opRef, ps |> List.map (fun p -> EIdent(p, r)), r), Ordinary, r)

    | Ident sym -> EIdent(sym, r)

    | SList(head :: args, listRange) ->
        match head with
        // `#name(...)`. Ahead of the special forms, none of which is spelled
        // with a `#`. A miss is a syntax error here and not an unbound name
        // later: nothing else a `#name` symbol could be.
        | Ident sym when sym.StartsWith "#" ->
            match hashExpandHook s with
            | Some expansion -> expansion.Resolve Set.empty (parseExpr expansion.Form)
            | None ->
                failwithf
                    $"Unknown hash macro '%s{sym}' at %s{Lexer.formatPos listRange}. A #name(...) form calls a hash macro, which arrives with the import of the module that (def/hash-extend ...)s it — the prelude's are #fl, #fr, #ch and #map."

        | Ident sym ->
            // Dispatch sees through a macro's rename; the identifier does not.
            // `sym` is what an application is built from, so a template's call
            // to a binding of its own module keeps the mark that resolves it.
            match headName sym with
            | "cast" ->
                match args with
                | [ typeSExpr; valSExpr ] ->
                    ECast(parseType typeSExpr, parseExpr valSExpr, r)
                | _ -> failwithf $"Invalid cast syntax at %s{Lexer.formatPos r}. Expected: (cast <type> <expr>)"
            // `(dyn ->str 42)` — value packed into a trait box.
            //
            // Trait name is stripped of macro marks just as `impl` strips theirs.
            // Associated types are never written here (inferred from value).
            | "dyn" ->
                match args with
                | [ StrippedSymbol traitName; valExpr ] -> EDynPack(traitName, parseExpr valExpr, r)
                | _ ->
                    failwithf
                        $"Invalid dyn syntax at %s{Lexer.formatPos r}. Expected: (dyn <TraitName> <expr>)"
            // `(begin ...)` where a *value* is wanted, which `parseBody` did
            // not consume because it is not in body position: a nested body,
            // opening a scope of its own.
            //
            // Reached only there. In body position `parseItems` takes the form
            // first and splices it, which is what the two readings are: spliced
            // among forms, a nested body among values. Scheme's, and the one
            // that makes `(+ 1 (begin (log!) 2))` mean anything at all — there
            // is otherwise no sequencing expression, `seq` being a lazy
            // generator rather than a block.
            //
            // `(begin)` here is `unit`, as it is in a body, since `parseBody`
            // of nothing is `unit`.
            | "begin" -> parseBody args listRange

            // `(let ((x a) (y b)) body)` — R7RS, so the bindings are
            // *simultaneous*: every init is evaluated in the enclosing scope,
            // and none of the group's names is in scope for any of them.
            // `(let ((a b) (b a)) ...)` swaps.
            //
            // The AST has no node for a group, so the group is built out of
            // nested single-binding nodes, which are sequential — and
            // `simultaneous` is what makes that faithful, by renaming apart the
            // binders a later init would otherwise see. Its docstring has the
            // reasoning; the two things worth knowing here are that a binder
            // nothing shadows is left with the name the author gave it, and
            // that the substitution goes to the body and never to an init.
            //
            // The sequential form is `let*`, a prelude macro over nested
            // single-binding `let`s. A single binding means the same thing
            // under both readings, which is what lets the macro be that simple.
            //
            // Left to right is kept, though R7RS leaves the order of the inits
            // unspecified: an init may have effects, the emitted C# has one
            // order regardless, and an order nobody can predict buys a
            // reordering nobody performs.
            | "let" ->
                match args with
                | SList(bindings, _) :: bodyExprs ->
                    let body = parseBody bodyExprs listRange

                    // Each binding as (the names it binds, its init, its own range).
                    // A destructuring binding contributes several names, and each
                    // of them shadows on its own.
                    let parsedBindings =
                        bindings
                        |> List.map (fun bind ->
                            match bind with
                            | SList([ Ident k; v ], _) -> [ k ], parseExpr v, getRange bind, false
                            | SList([ SList(names, _); v ], bindRange) when
                                not names.IsEmpty
                                && names
                                   |> List.forall (function
                                       | SAtom { Token = Symbol _ }
                                       | SAtom { Token = Comma } -> true
                                       | _ -> false) ->
                                let rawNames =
                                    names
                                    |> List.choose (function
                                        | SAtom { Token = Symbol n } -> Some n
                                        | _ -> None)
                                let tupleNames =
                                    match rawNames with
                                    | "Tuple" :: restNames -> restNames
                                    | _ -> rawNames
                                tupleNames, parseExpr v, bindRange, true
                            // Named rather than left to the generic message: a
                            // boolean is a literal, so it is not a symbol and
                            // does not match the binder shapes above — which is
                            // the whole point, but says nothing on its own.
                            | SList([ SAtom { Token = BoolLit b }; _ ], bindRange) ->
                                let spelling = if b then "#t" else "#f"

                                failwithf
                                    $"Cannot bind %s{spelling} at %s{Lexer.formatPos bindRange}: it is a boolean literal, not a name."
                            | _ -> failwith "Invalid let binding")

                    // A repeated name is meaningful under `let*` — the second
                    // binding shadows the first — and means nothing at all here,
                    // since neither binding is in scope for the other's init.
                    // Refused rather than given an arbitrary winner.
                    //
                    // `_` is exempt: it is the binder a body uses for a value
                    // nothing reads, and a body of several statements is several
                    // of them.
                    parsedBindings
                    |> List.collect (fun (names, _, bindRange, _) -> names |> List.map (fun n -> n, bindRange))
                    |> List.filter (fun (n, _) -> n <> "_")
                    |> List.groupBy fst
                    |> List.iter (fun (n, occurrences) ->
                        if occurrences.Length > 1 then
                            let (_, secondRange) = occurrences[1]

                            failwithf
                                $"'%s{n}' is bound twice in the same let at %s{Lexer.formatPos secondRange}. A let binds simultaneously, so neither binding is in scope for the other's value and there is nothing for the second to shadow. Write (let* ...) if the second is meant to see the first, or give one of them another name.")

                    let renamedNames, bodySubst =
                        simultaneous (parsedBindings |> List.map (fun (names, init, _, _) -> names, init))

                    List.foldBack
                        (fun (names, (_, init, bindRange, isTuple)) acc ->
                            if isTuple then
                                ELetTuple(names, init, acc, bindRange)
                            else
                                ELet(List.head names, false, [], None, init, acc, bindRange))
                        (List.zip renamedNames parsedBindings)
                        (renameFree bodySubst body)
                | Ident name :: SList(bindings, _) :: bodyExprs ->
                    // Named let. Already simultaneous, and unchanged by any of
                    // the above: its inits are the arguments of an `EApp`, so
                    // they are evaluated in the enclosing scope by construction
                    // and the loop's parameters cannot be in scope for them.
                    // This form is where the language was right about `let` all
                    // along and the unnamed one was not.
                    let parsedBindings =
                        bindings
                        |> List.map (function
                            | SList([ Ident k; v ], _) -> (k, parseExpr v)
                            | _ -> failwith "Invalid named let binding")

                    let argNames = parsedBindings |> List.map fst
                    let argVals = parsedBindings |> List.map snd
                    let body = parseBody bodyExprs listRange
                    let funcBinding = (name, true, argNames |> List.map (fun n -> MandatoryArg(n, None)), None, body)
                    ELetRec([funcBinding], EApp(EIdent(name, r), argVals, r), r)
                | _ -> failwith "Invalid let syntax"

            // Internal: the readable form of `ELetMono`, so that an inline
            // template containing one survives export and re-import. Written by
            // desugarings and by `serializeExpr`, not intended to be hand-written.
            | "let/mono" ->
                match args with
                | [ Ident name; value; body ] -> ELetMono(name, parseExpr value, parseExpr body, listRange)
                | _ ->
                    failwithf $"Invalid let/mono syntax at %s{Lexer.formatPos r}. Expected: (let/mono name value body)"

            | "letrec" ->
                match args with
                | SList(bindings, _) :: bodyExprs ->
                    let parsedBindings =
                        bindings
                        |> List.map (function
                            // Standard explicit letrec assumes value bindings or manually desugared lambdas
                            | SList([ Ident k; v ], _) -> (k, false, [], None, parseExpr v)
                            | _ -> failwith "Invalid letrec binding")

                    ELetRec(parsedBindings, parseBody bodyExprs listRange, r)
                | _ -> failwith "Invalid letrec syntax"
            | "set!" ->
                match args with
                | [ Ident target; valExpr ] -> ESet(target, parseExpr valExpr, r)
                | _ -> failwithf $"Invalid set! syntax at %s{Lexer.formatPos r}. Expected: (set! name value)"
            | "->" ->
                match args with
                | init :: steps ->
                    let threadExpr = steps |> List.fold threadStep init
                    parseExpr threadExpr
                | _ -> failwithf $"-> requires at least one argument at %s{Lexer.formatPos r}"
            | "if" ->
                match args with
                | [ cond; t; f ] -> EIf(parseExpr cond, parseExpr t, parseExpr f, r)
                | _ -> failwith "Invalid if syntax"

            // `when` and `unless` are one-armed: there is no second branch for
            // the body's type to agree with, so they are statements rather than
            // expressions. Desugaring them into `if` with an empty tuple as the
            // missing arm made every body that was not itself an empty tuple a
            // type error — which is to say every body anyone would write.
            | "when" ->
                match args with
                | cond :: bodyExprs when not bodyExprs.IsEmpty ->
                    EWhen(parseExpr cond, parseBody bodyExprs listRange, false, listRange)
                | _ -> failwithf $"Invalid when syntax at %s{Lexer.formatPos r}. Expected: (when cond body...)"

            | "unless" ->
                match args with
                | cond :: bodyExprs when not bodyExprs.IsEmpty ->
                    EWhen(parseExpr cond, parseBody bodyExprs listRange, true, listRange)
                | _ -> failwithf $"Invalid unless syntax at %s{Lexer.formatPos r}. Expected: (unless cond body...)"

            // `(with-return ret body ...)`. The body is an ordinary body, so a
            // `def` or a `guard` in it scopes over what follows.
            //
            // `name` keeps whatever mark it arrived with. It is a *binder*, and
            // a template's binder is meant to be uncapturable — stripping it
            // here would let a macro's `ret` be caught by a user's, which is
            // the one thing the marks exist to prevent.
            | "with-return" ->
                match args with
                | SAtom { Token = Symbol name } :: bodyExprs when not bodyExprs.IsEmpty ->
                    EWithReturn(name, parseBody bodyExprs listRange, listRange)
                | [ SAtom { Token = Symbol name } ] ->
                    failwithf
                        $"Syntax error at %s{Lexer.formatPos r}: the with-return block named '%s{name}' has no body."
                | _ ->
                    failwithf
                        $"Syntax error at %s{Lexer.formatPos r}: with-return needs a name for its escape. Expected: (with-return name body...). The name is required — there is no implicit `return`."

            // A `def` or a `def*` that got this far is one `parseBody` did not
            // take, which means it is not in body position. It has nowhere to
            // put its sequel there, so saying so is the only answer: reading it
            // as a call would fail with "Unbound variable: def" and name
            // nothing the programmer did wrong.
            | ("def" | "def*") as head ->
                failwithf
                    $"Syntax error at %s{Lexer.formatPos r}: `%s{head}` must appear directly in a body, not inside another expression."

            // A `seq` body is a block like any other, but it is *not* run where
            // it is written: the form evaluates to a sequence, and the body runs
            // a `yield` at a time as that sequence is consumed.
            | "seq" ->
                match args with
                | [] -> failwithf $"Invalid seq syntax at %s{Lexer.formatPos r}. Expected: (seq body...)"
                | bodyExprs -> ESeq(parseBody bodyExprs listRange, listRange)

            // `(bjo (f x y))` and its three siblings. The operand must be a
            // call: a spawn splits it into operands evaluated here and a call
            // made over there, and there is nothing to split in anything else.
            //
            // All four start a fiber in the same way and differ only in what the
            // enclosing scope does about it afterwards, which is what
            // `SpawnKind` carries. Writing them as four forms rather than one
            // form with a keyword argument is deliberate: the choice is not a
            // detail of a spawn, it is what the spawn *is*, and a reader should
            // see it at the head of the form.
            | "bjo" | "spawn" | "spawn/daemon" | "spawn/detached" ->
                // `headName sym` again, not `sym`: a template writes these
                // renamed, and the rename is only stripped for the dispatch
                // above. Reading the raw spelling here made every spawn form a
                // macro produced fall to the `_` arm and detach.
                let kind =
                    match headName sym with
                    | "bjo" -> SpawnScoped
                    | "spawn" -> SpawnUnit
                    | "spawn/daemon" -> SpawnDaemon
                    | _ -> SpawnDetached

                match args with
                | [ SList(_ :: _, _) as call ] -> EBjo(parseExpr call, kind, listRange)
                | _ ->
                    failwithf
                        $"Invalid %s{sym} syntax at %s{Lexer.formatPos r}. Expected: (%s{sym} (f args...)) — one call, whose operands are evaluated here and whose call happens in the new fiber. For a thunk you already have, use spawn-thunk."

            // `(spawn-evt (worker q))` — start this when the event is synced,
            // and cancel it if the branch loses.
            //
            // Desugared here rather than given a node of its own, because every
            // piece already exists: a nullary lambda holding a `bjo`, handed to
            // a prelude function that installs a fresh cancellation token
            // around calling it. `bjo` is colourless, so the lambda is an
            // ordinary one even when the call inside it suspends — which is
            // exactly the case §3.1 would otherwise forbid, and the reason this
            // cannot be a plain function over a thunk.
            //
            // The lambda runs at *sync* time, once per sync, so the call's
            // operands are evaluated there rather than here. That is the
            // opposite of `bjo` and the same as `task->event`: an event is a
            // description, and a description that had already run its arguments
            // would be a description of something that had already happened.
            | "spawn-evt" ->
                match args with
                | [ SList(_ :: _, _) as call ] ->
                    EApp(
                        EResolved("spawn-evt/start", listRange),
                        [ EFun([], EBjo(parseExpr call, SpawnScoped, listRange), Ordinary, listRange) ],
                        listRange
                    )
                | _ ->
                    failwithf
                        $"Invalid spawn-evt syntax at %s{Lexer.formatPos r}. Expected: (spawn-evt (f args...)) — one call, spawned when the event is synced and cancelled if its branch loses. To spawn eagerly and keep listening whatever happens, use (promise-join (bjo (f args...)))."

            // `(task->event (fetch url))`. A special form for the same reason
            // `bjo` is: the operand must *not* be evaluated where it is
            // written. An `#:async` call means "await this" everywhere else,
            // and here it has to mean "hand me the task, unstarted", so that
            // the event can start it at sync time with a token of its own.
            | "task->event" ->
                match args with
                | [ SList(_ :: _, _) as call ] -> ETaskEvent(parseExpr call, listRange)
                | _ ->
                    failwithf
                        $"Invalid task->event syntax at %s{Lexer.formatPos r}. Expected: (task->event (f args...)) — one call to a method imported #:async, whose arguments are evaluated here and whose call is made when the event is synced."

            // Guarded rather than claimed outright: `(loop (+ i 1))` is how a
            // named `let` recurses, and that must keep meaning a call. A clause
            // is a keyword-headed list, which an argument expression is not.
            | "loop" when LoopDesugar.isLoopForm args -> LoopDesugar.desugarLoop (parseFns ()) args listRange

            // Claimed outright rather than guarded like `loop`: `seql` collides
            // with nothing, and a guard would turn a malformed one into an
            // unbound-variable error instead of a loop diagnostic.
            | "seql" -> LoopDesugar.desugarSeqLoop (parseFns ()) args listRange

            // The head the reader puts on a `{...}` form. Claimed outright for
            // the same reason `seql` is; a program cannot write braces by
            // accident.
            | "comprehension" -> LoopDesugar.desugarComprehension (parseFns ()) args listRange

            | "yield" ->
                match args with
                | [ value ] -> EYield(parseExpr value, listRange)
                | _ -> failwithf $"Invalid yield syntax at %s{Lexer.formatPos r}. Expected: (yield value)"

            | "yield-from" ->
                match args with
                | [ source ] -> EYieldFrom(parseExpr source, listRange)
                | _ -> failwithf $"Invalid yield-from syntax at %s{Lexer.formatPos r}. Expected: (yield-from seq)"

            | "and" ->
                let rec buildAnd items =
                    match items with
                    | [] -> EBool(true, listRange)
                    | [last] -> parseExpr last
                    | current :: rest ->
                        EIf(parseExpr current, buildAnd rest, EBool(false, listRange), listRange)
                buildAnd args

            | "or" ->
                let rec buildOr items =
                    match items with
                    | [] -> EBool(false, listRange)
                    | [last] -> parseExpr last
                    | current :: rest ->
                        EIf(parseExpr current, EBool(true, listRange), buildOr rest, listRange)
                buildOr args

            | "not" ->
                match args with
                | [arg] -> EIf(parseExpr arg, EBool(false, listRange), EBool(true, listRange), listRange)
                | _ -> failwithf $"Invalid not syntax at %s{Lexer.formatPos r}"

            | "fun"
            | "bjoroutine" ->
                // `headName sym`, for the reason the spawn forms above use it:
                // a `bjoroutine` a template wrote arrives renamed, and reading
                // the raw spelling made it an ordinary lambda.
                let colour = if headName sym = "bjoroutine" then Suspending else Ordinary

                match args with
                | SList(fargs, _) :: bodyExprs ->
                    let argNames =
                        fargs
                        |> List.choose (function
                            | Ident n -> Some n
                            | SAtom { Token = Comma } -> None
                            | _ -> failwith "Expected arg name")

                    EFun(argNames, parseBody bodyExprs listRange, colour, r)
                | _ -> failwithf $"Invalid %s{sym} syntax"

            | "match" ->
                match args with
                | targetExpr :: clauses ->
                    let target = parseExpr targetExpr

                    let parsedClauses =
                        clauses
                        |> List.map (fun clause ->
                            let rClause = getRange clause

                            match clause with
                            // Clause with a guard: (pattern #:when guard body...)
                            | SList(pattern :: SAtom { Token = Keyword "when" } :: guard :: bodyExprs, _) ->
                                (parsePattern pattern, Some(parseExpr guard), parseBody bodyExprs rClause)
                            // Standard clause: (pattern body...)
                            | SList(pattern :: bodyExprs, _) ->
                                (parsePattern pattern, None, parseBody bodyExprs rClause)
                            | _ -> failwithf $"Invalid match clause at %s{Lexer.formatPos rClause}")

                    EMatch(target, parsedClauses, r)
                | _ -> failwithf $"Invalid match syntax at %s{Lexer.formatPos r}"

            // `(case key ((datum ...) body ...) ... (else body ...))`.
            //
            // Desugared to `match` over or-patterns, which is what puts several
            // labels on one `switch` section.
            //
            // Read here rather than expanded by a macro because the datum rules
            // have to be enforced where the source is. A bare name in a datum
            // list is a *binder* to `match`, so `(case c ((a) ...))` would
            // match everything and bind `a` — silently, and only in the clause
            // the author thought was about a symbol.
            | "case" ->
                match args with
                | keyExpr :: (_ :: _ as clauses) ->
                    let datumPattern (d: SExpr) : Pattern =
                        let dr = getRange d

                        match d with
                        | SAtom { Token = NumberLit n } -> PInt(n, dr)
                        | SAtom { Token = StringLit str } -> PString(str, dr)
                        | SAtom { Token = CharLit c } -> PChar(c, dr)
                        | SAtom { Token = BoolLit b } -> PBool(b, dr)
                        | SAtom { Token = Keyword k } -> PKeyword(k, dr)
                        | SAtom { Token = QuotedSymbol sym } -> PQuotedSymbol(sym, dr)
                        | SAtom { Token = Symbol sym } ->
                            failwithf
                                $"Invalid case datum at %s{Lexer.formatPos dr}: '%s{sym}' is a name, and the data of a clause are literals. Write '%s{sym} for the symbol of that name, or use match to bind."
                        | _ ->
                            failwithf
                                $"Invalid case datum at %s{Lexer.formatPos dr}: the data of a clause are literals — a number, string, character, boolean, keyword or quoted symbol."

                    // A repeat is refused here because C# refuses it too, and a
                    // duplicate `case` label reported against generated code is
                    // a compiler error about a file nobody wrote. The second
                    // one is unreachable either way.
                    let seen = System.Collections.Generic.Dictionary<string, Range>()

                    let noteDatum (p: Pattern) =
                        let key, shown, dr =
                            match p with
                            // Normalised, so that `01` and `1` are the one
                            // label they will be emitted as.
                            | PInt(v, dr) ->
                                match System.Int64.TryParse v with
                                | true, n -> $"int:%d{n}", v, dr
                                | _ -> "num:" + v, v, dr
                            | PString(v, dr) -> "str:" + v, $"\"%s{v}\"", dr
                            | PChar(c, dr) -> $"char:%d{c}", $"#\\x%X{c}", dr
                            | PBool(b, dr) -> $"bool:%b{b}", (if b then "#t" else "#f"), dr
                            | PKeyword(k, dr) -> "kw:" + k, "#:" + k, dr
                            | PQuotedSymbol(s, dr) -> "sym:" + s, "'" + s, dr
                            | _ -> "", "", r

                        match seen.TryGetValue key with
                        | true, first ->
                            failwithf
                                $"Duplicate case datum at %s{Lexer.formatPos dr}: %s{shown} is already covered by the clause at %s{Lexer.formatPos first}, so this one can never run."
                        | _ -> seen[key] <- dr

                    let lastIndex = List.length clauses - 1

                    let parsedClauses =
                        clauses
                        |> List.mapi (fun i clause ->
                            let cr = getRange clause

                            let body bodyExprs =
                                match bodyExprs with
                                | [] ->
                                    failwithf
                                        $"Invalid case clause at %s{Lexer.formatPos cr}: this clause has nothing to do."
                                | _ -> parseBody bodyExprs cr

                            match clause with
                            | SList(_ :: SAtom { Token = Symbol "=>" } :: _, _) ->
                                failwithf
                                    $"Invalid case clause at %s{Lexer.formatPos cr}: (=> proc) is not a Bjolang clause. Write the call out, naming the key: (case k ((1 2) (proc k)) ...)."

                            | SList(SAtom { Token = Symbol "else" } :: bodyExprs, _) ->
                                if i <> lastIndex then
                                    failwithf
                                        $"Invalid case at %s{Lexer.formatPos cr}: (else ...) answers whatever the clauses before it did not, so nothing may follow it."

                                (PWildcard cr, None, body bodyExprs)

                            | SList(SList(datums, _) :: bodyExprs, _) ->
                                if List.isEmpty datums then
                                    failwithf
                                        $"Invalid case clause at %s{Lexer.formatPos cr}: this clause lists no data, so nothing reaches it. Remove it, or write (else ...) if it was meant to catch the rest."

                                let pats = datums |> List.map datumPattern
                                List.iter noteDatum pats

                                let pat =
                                    match pats with
                                    | [ single ] -> single
                                    | many -> POr(many, cr)

                                (pat, None, body bodyExprs)

                            // One datum, without the list around it:
                            // `('ms body ...)`. A clause groups data, and a
                            // group of one needs no bracket to say so.
                            | SList((SAtom _ as datum) :: bodyExprs, _) ->
                                let pat = datumPattern datum
                                noteDatum pat
                                (pat, None, body bodyExprs)

                            | _ ->
                                failwithf
                                    $"Invalid case clause at %s{Lexer.formatPos cr}: a clause is (datum body ...) or ((datum ...) body ...) for several, or (else body ...) for the last one.")

                    // Bools cannot have an else clause. C# rejects it.
                    let hasElse =
                        match List.tryLast clauses with
                        | Some(SList(SAtom { Token = Symbol "else" } :: _, _)) -> true
                        | _ -> false

                    let exhausted = seen.ContainsKey "bool:true" && seen.ContainsKey "bool:false"

                    if hasElse && exhausted then
                        failwithf
                            $"Invalid case at %s{Lexer.formatPos r}: the clauses already list both #t and #f, which is the whole of bool, so this (else ...) can never run. Remove it."

                    if not hasElse && not exhausted then
                        failwithf
                            $"Invalid case at %s{Lexer.formatPos r}: this case has no (else ...), and its data are literals — so a key that is none of them reaches no arm at all, and the program fails at runtime naming neither the key nor this form. Add (else ...) for the rest. Only bool can be covered by listing values instead, since #t and #f are the whole of it."

                    EMatch(parseExpr keyExpr, parsedClauses, r)
                | [ _ ] ->
                    failwithf
                        $"Invalid case at %s{Lexer.formatPos r}: there are no clauses, so there is nothing for the key to be."
                | _ ->
                    failwithf
                        $"Invalid case at %s{Lexer.formatPos r}: case takes a key and then its clauses: (case key ((datum ...) body ...) ... (else body ...))."

            // Construction is spelled with the type name — `(Car (brand "x")
            // (year 3000))` — so there is no anonymous `record` form to infer a
            // type for. The old spelling is caught here rather than left to
            // fail as an unbound `record`, because the fix is not obvious from
            // "unknown identifier".
            | "record" | "struct" ->
                let shown =
                    args
                    |> List.map (function
                        | SList(Ident k :: _, _) -> $"(%s{k} ...)"
                        | _ -> "...")
                    |> String.concat " "

                failwithf
                    $"Invalid %s{sym} at %s{Lexer.formatPos r}: record and struct construction names its type, so write (TypeName %s{shown}) instead of (%s{sym} %s{shown})."

            // `struct*` forms are accepted synonyms for the `record*` forms.
            | "record-set" | "struct-set" ->
                match args with
                | Ident baseRec :: fields ->
                    let parsedFields =
                        fields
                        |> List.map (function
                            | SList([ Ident k; v ], _) -> (k, parseExpr v)
                            | bad ->
                                failwithf
                                    $"Invalid %s{sym} field at %s{Lexer.formatPos (getRange bad)}: expected (field-name value)")

                    ERecordUpdate(baseRec, parsedFields, r)
                | _ -> failwithf $"Invalid %s{sym} syntax at %s{Lexer.formatPos r}: expected (%s{sym} target (field value) ...)"

            // A write in place, to `#:mutable` fields. Deliberately *not* given
            // a `struct-set!` synonym the way the pure forms are: a Struct
            // cannot have a mutable field, so the spelling is refused below
            // with the reason rather than left to fail as an unbound name.
            | "record-set!" ->
                match args with
                | Ident baseRec :: (_ :: _ as fields) ->
                    let parsedFields =
                        fields
                        |> List.map (function
                            | SList([ Ident k; v ], _) -> (k, parseExpr v)
                            | bad ->
                                failwithf
                                    $"Invalid %s{sym} field at %s{Lexer.formatPos (getRange bad)}: expected (field-name value)")

                    ERecordSet(baseRec, parsedFields, r)
                | [ Ident _ ] ->
                    failwithf
                        $"Invalid %s{sym} at %s{Lexer.formatPos r}: it has to write at least one field."
                | _ ->
                    failwithf
                        $"Invalid %s{sym} syntax at %s{Lexer.formatPos r}: expected (%s{sym} target (field value) ...), where the target is the name of a record. A computed target is not supported — bind it first."

            | "struct-set!" ->
                failwithf
                    $"Invalid struct-set! at %s{Lexer.formatPos r}: a Struct may not have a mutable field, so there is nothing for it to write. Use record-set! on a Record, or struct-set for a copy."

            | "record-ref" | "struct-ref" ->
                match args with
                | [ target; Ident field ] ->
                    EGetField(parseExpr target, field, r)
                | _ -> failwithf $"Invalid %s{sym} syntax at %s{Lexer.formatPos r}: expected (%s{sym} target field-name)"

            // `(try body... #:catch (E1 E2 ...) #:finally cleanup...)`
            //
            // Both clauses are optional and at least one is required, which is
            // what makes this one form rather than two. `#:catch` turns the
            // listed exception types — and only those — into an `Err`, giving
            // the whole form the type `(Result System.Exception %a)`.
            | "try" ->
                let rec split bodyAcc remaining =
                    match remaining with
                    | SAtom { Token = Keyword("catch" | "finally") } :: _ -> List.rev bodyAcc, remaining
                    | x :: rest -> split (x :: bodyAcc) rest
                    | [] -> List.rev bodyAcc, []

                let bodyForms, clauseForms = split [] args

                if bodyForms.IsEmpty then
                    failwithf $"Invalid try at %s{Lexer.formatPos r}: it has no body."

                // The clauses, read in whichever order they were written.
                let rec readClauses catchNames finallyForms remaining =
                    match remaining with
                    | [] -> catchNames, finallyForms
                    | SAtom { Token = Keyword "catch" } :: SList(names, _) :: rest ->
                        if Option.isSome catchNames then
                            failwithf $"Invalid try at %s{Lexer.formatPos r}: #:catch is given twice."

                        if names.IsEmpty then
                            failwithf
                                $"Invalid try at %s{Lexer.formatPos r}: #:catch names no exception types. Leave it off to let everything propagate."

                        // `headName` for the reason `parsePattern` uses it: an
                        // exception type is dispatched on, never bound, so the
                        // first two renaming rules cannot reach one and
                        // stripping is the whole answer. It has to happen here
                        // because these names leave as a `string list` that
                        // `AlphaRename` never walks — without it a template
                        // writing `#:catch (System.IO.IOException)` reaches
                        // inference as `System.IO.IOException__37`.
                        let parsed =
                            names
                            |> List.map (function
                                | SAtom { Token = Symbol n } -> headName n
                                | bad ->
                                    failwithf
                                        $"Invalid try at %s{Lexer.formatPos (getRange bad)}: #:catch takes fully qualified .NET exception type names, as in System.IO.IOException.")

                        // Cancellation may not be caught here.
                        //
                        // `Cancelled` is what a `sync` raises when the scope has
                        // been cancelled, and a handler that turns it back into
                        // an ordinary value is a fiber that has been told to
                        // stop and has decided not to. The shape is always the
                        // same — catch, log, loop — and it is the classic bug in
                        // every language that made cancellation an exception.
                        //
                        // There is no catch-all in this language, so nothing can
                        // swallow it by accident: `#:catch` with no named types
                        // is already refused above. This is for the case where
                        // someone names it on purpose, and the answer to what
                        // they were trying to do is `with-shield`.
                        for name in parsed do
                            if name = "Cancelled" || name.EndsWith(".Cancelled") then
                                failwithf
                                    $"Invalid try at %s{Lexer.formatPos r}: #:catch may not name Cancelled.\n  Cancelled is what a sync raises once the scope has been cancelled, and catching it here would let this fiber carry on after it has been told to stop.\n  To run cleanup after a cancellation, put it in (with-shield ...): inside a shield the ambient token is a fresh one, so a sync works again."

                        readClauses (Some parsed) finallyForms rest
                    | SAtom { Token = Keyword "catch" } :: _ ->
                        failwithf
                            $"Invalid try at %s{Lexer.formatPos r}: #:catch takes a parenthesized list of exception types."
                    | SAtom { Token = Keyword "finally" } :: rest ->
                        if not (List.isEmpty finallyForms) then
                            failwithf $"Invalid try at %s{Lexer.formatPos r}: #:finally is given twice."

                        // Everything up to the next clause keyword.
                        let cleanup, after = split [] rest

                        if cleanup.IsEmpty then
                            failwithf $"Invalid try at %s{Lexer.formatPos r}: #:finally has no body."

                        readClauses catchNames cleanup after
                    | bad :: _ ->
                        failwithf
                            $"Invalid try at %s{Lexer.formatPos (getRange bad)}: expected #:catch or #:finally."

                let catchNames, finallyForms = readClauses None [] clauseForms

                if Option.isNone catchNames && List.isEmpty finallyForms then
                    failwithf
                        $"Invalid try at %s{Lexer.formatPos r}: a try does nothing without #:catch or #:finally."

                let body = parseBody bodyForms listRange

                // `#:catch` is applied first, so that the cleanup runs whether
                // the body completed, was caught, or is still on its way out.
                let caught =
                    match catchNames with
                    | Some names -> ETryCatch(body, names, r)
                    | None -> body

                if List.isEmpty finallyForms then
                    caught
                else
                    ETryFinally(caught, parseBody finallyForms listRange, r)

            // `(with-open ((name ctor) ...) body...)` — bind each resource,
            // and dispose it on the way out however the body ends.
            //
            // Each binding gets its *own* try/finally rather than one around
            // all of them, so a later constructor that throws still leaves the
            // earlier resources disposed.
            //
            // Which is also why the bindings stay **sequential** where `let`'s
            // and `parameterize`'s became simultaneous. Simultaneous would mean
            // every constructor runs before any disposal is registered, so a
            // second one that throws would leak the first resource — the exact
            // failure the per-binding `try/finally` above exists to prevent.
            // And `(with-open ((r (open-in p)) (w (wrap r))) ...)` is how one is
            // normally written: a later resource built from an earlier one needs
            // the earlier one's name in scope. Resource safety and the ordinary
            // usage agree here, and both outrank uniformity.
            | "with-open" ->
                match args with
                | SList(bindings, _) :: bodyForms ->
                    if bodyForms.IsEmpty then
                        failwithf $"Invalid with-open at %s{Lexer.formatPos r}: it has no body."

                    let body = parseBody bodyForms listRange

                    List.foldBack
                        (fun binding acc ->
                            match binding with
                            | SList([ Ident name; value ], bindRange) ->
                                // Not a direct `.Dispose`: on a port its scope
                                // owns, that would close the stream and leave
                                // the registration on the scope's list. The
                                // helper releases through the owner handle when
                                // there is one and disposes when there is not,
                                // so `with-open` still works on any
                                // `IDisposable` from interop.
                                let dispose =
                                    EApp(
                                        EResolved("close-owned-or-dispose", bindRange),
                                        [ EIdent(name, bindRange) ],
                                        bindRange
                                    )

                                ELet(
                                    name,
                                    false,
                                    [],
                                    None,
                                    parseExpr value,
                                    ETryFinally(acc, dispose, bindRange),
                                    bindRange
                                )
                            | bad ->
                                failwithf
                                    $"Invalid with-open binding at %s{Lexer.formatPos (getRange bad)}: expected (name expression).")
                        bindings
                        body
                | _ ->
                    failwithf
                        $"Invalid with-open at %s{Lexer.formatPos r}: expected (with-open ((name expression) ...) body...)"

            // `(parameterize ((param value) ...) body...)` — install each
            // binding in the dynamic environment, and put it back however the
            // body ends.
            //
            // The shape is `with-open`'s, for the same reason: a saved value in
            // an ordinary `let`, and a `try/finally` restoring it. What is saved
            // is the *whole* environment rather than the one parameter's old
            // value, so one `finally` undoes a binding whichever slot it went
            // to — a port field or the champ.
            //
            // The bindings are *simultaneous*, as R7RS says and as `let`'s are:
            // every parameter and value expression is evaluated against the
            // environment the form was written in, and only then is any of them
            // installed. So a later value expression that reads an earlier
            // parameter of the same form reads what it was *outside*.
            //
            // The mechanism is not `let`'s. There is no binder to rename apart
            // here — a binding's left-hand side is an arbitrary expression, not
            // a name — and the capture is not lexical but dynamic: an earlier
            // parameter is already installed in the environment while a later
            // value expression runs. What fixes that is evaluating everything
            // into temporaries first, in one flat chain outside the pushes, and
            // pushing the temporaries.
            //
            // `parameterize*` — a prelude macro over nested single-binding
            // `parameterize`s — is the sequential form, for the rare case that
            // wants one binding to be visible while the next value is computed.
            //
            // The unwinding is unchanged, and still nests: each binding gets its
            // own `try/finally`, so a `finally` runs only for a push that
            // happened, and the environment comes back in the reverse of the
            // order it went out.
            //
            // Note again that the binder is an arbitrary *expression*: a
            // parameter is a value, so `(parameterize (((config-port c) w)) ...)`
            // is as legitimate as naming one directly — which is the other
            // reason it has to go into a temporary, since computing it twice
            // could push one parameter and restore another.
            | "parameterize" ->
                match args with
                | SList(bindings, _) :: bodyForms ->
                    if bodyForms.IsEmpty then
                        failwithf $"Invalid parameterize at %s{Lexer.formatPos r}: it has no body."

                    let body = parseBody bodyForms listRange

                    // (the parameter's temporary, the value's temporary, the two
                    // expressions they hold, the binding's own range)
                    let parsedBindings =
                        bindings
                        |> List.map (fun binding ->
                            match binding with
                            | SList([ param; value ], bindRange) ->
                                Gensym.fresh "dynparam", Gensym.fresh "dynvalue", parseExpr param, parseExpr value, bindRange
                            | bad ->
                                failwithf
                                    $"Invalid parameterize binding at %s{Lexer.formatPos (getRange bad)}: expected (parameter expression).")

                    let installed =
                        List.foldBack
                            (fun (paramTemp, valueTemp, _, _, bindRange) acc ->
                                let saved = Gensym.fresh "dynsaved"

                                let push =
                                    EApp(
                                        EResolved("parameter-push!", bindRange),
                                        [ EIdent(paramTemp, bindRange); EIdent(valueTemp, bindRange) ],
                                        bindRange
                                    )

                                let restore =
                                    EApp(EResolved("dyn-restore!", bindRange), [ EIdent(saved, bindRange) ], bindRange)

                                ELet(saved, false, [], None, push, ETryFinally(acc, restore, bindRange), bindRange))
                            parsedBindings
                            body

                    // Source order, left to right, and the parameter before its
                    // own value — the same order the expressions were written
                    // in, which is the order they used to run in too. R7RS
                    // leaves it unspecified; an effect in a value expression is
                    // not a reason to make it unpredictable.
                    List.foldBack
                        (fun (paramTemp, valueTemp, paramExpr, valueExpr, bindRange) acc ->
                            ELet(
                                paramTemp,
                                false,
                                [],
                                None,
                                paramExpr,
                                ELet(valueTemp, false, [], None, valueExpr, acc, bindRange),
                                bindRange
                            ))
                        parsedBindings
                        installed
                | _ ->
                    failwithf
                        $"Invalid parameterize at %s{Lexer.formatPos r}: expected (parameterize ((parameter expression) ...) body...)"

            | "Tuple" -> ETuple(processArgs args, listRange)

            // Quoted list literal: '(1 2 3) → Cons chain
            | "quoted-list" -> desugarQuotedList parseExpr args listRange

            // The head the reader puts on a `#'` form, always with exactly one
            // argument: `#'` is a prefix on the form after it.
            | "syntax-quote" ->
                match args with
                | [ template ] -> desugarSyntaxQuote parseExpr template listRange
                | _ ->
                    failwithf
                        $"Invalid syntax-quote at %s{Lexer.formatPos r}. Write #'form; the head is not meant to be written by hand."

            // Vec literal: [1 2 3] → EVec
            | "vec-literal" -> EVec(processArgs args, listRange)

            // Array literal: #[1 2 3] → EArray
            | "array-literal" -> EArray(processArgs args, listRange)

            // List special form: (list 1 2 3) → EList, same as [1 2 3] for vecs.
            // `list` used as a bare value (not in call position) remains an
            // ordinary identifier that references the prelude rest-arg function.
            | "list" -> EList(processArgs args, listRange)

            // Arithmetic and comparison, at any arity. Handled here rather than
            // in `Inference` so that everything downstream — the type checker,
            // the inliner and the operator emission in `Codegen` — continues to
            // see only the binary form it already understands.
            //
            // Keyword arguments are meaningless on an operator, so a call
            // carrying one falls through to ordinary application and fails
            // there, where the message is about the real mistake.
            | op when
                (List.contains op foldingOps || List.contains op chainingOps)
                && not (args |> List.exists (function
                                             | SAtom { Token = Keyword _ } -> true
                                             | _ -> false))
                ->
                desugarOperator parseExpr head sym op args listRange

            // A macro, tried last so that a special form always wins. Anything
            // reaching here is either a macro call or an ordinary application,
            // and the head is the only thing that decides which.
            | _ ->
                match expandHook s with
                | Some expansion -> expansion.Resolve Set.empty (parseExpr expansion.Form)
                | None -> EApp(EIdent(sym, getRange head), processArgs args, listRange)


        | _ ->
            // Fallback for tuples or unquoted lists
            EApp(parseExpr head, processArgs args, listRange)

    | SList([], listRange) -> ETuple([], listRange)

    // Explicit token catches for better debugging
    | SAtom { Token = Comma } -> failwithf $"Unexpected comma at %s{Lexer.formatPos r}"
    | SAtom { Token = Quote } -> failwithf $"Unexpected quote at %s{Lexer.formatPos r}"
    | _ -> failwithf $"Unexpected expression at %s{Lexer.formatPos r}"

// ---------------------------------------------------------------------------
// Patterns
// ---------------------------------------------------------------------------
//
// Read here, in `parseExpr`'s group, because a pattern holds an expression: a
// `(:view step p)` runs `step` on the value, and the step is read by the same
// reader everything else is.

and parsePattern (s: SExpr) : Pattern =
    let r = getRange s

    // The third renaming rule, applied to a pattern's head.
    //
    // A constructor is never a binder, so the first two rules cannot reach one:
    // nothing in the expansion binds it, and a macro module publishes bindings
    // rather than constructors. Stripping is therefore the whole answer, and it
    // has to happen here — `AlphaRename.freeNames` reports the names a pattern
    // *binds* and never the constructor it matches, so a template's `(Cons a
    // Nil)` would otherwise reach inference as `Cons__37`.
    //
    // A bare lowercase symbol is left alone: that is a binder, and its mark is
    // what makes it uncapturable.
    let s =
        match s with
        | SList(SAtom({ Token = Symbol sym } as head) :: args, lr) when headName sym <> sym ->
            SList(SAtom { head with Token = Symbol(headName sym) } :: args, lr)
        | SAtom({ Token = Symbol sym } as atom) when
            sym.Length > 0 && System.Char.IsUpper sym[0] && headName sym <> sym
            ->
            SAtom { atom with Token = Symbol(headName sym) }
        | _ -> s

    match s with
    | SAtom { Token = Symbol "_" } -> PWildcard r
    // A hash macro expands to an expression, and a pattern is not one.
    | SList(SAtom { Token = Symbol name } :: _, _) when name.StartsWith "#" ->
        failwithf
            $"'%s{name}(...)' in a pattern at %s{Lexer.formatPos r}. A hash macro expands in expression position only; a pattern that is a macro is a (def/pattern ...)."
    // Before the binder case below, which would otherwise read `#t` as a name
    // and match everything. That is what it did: a boolean pattern bound a
    // variable called `#t` and reached the code generator, which spelled it
    // into C# as written and produced a preprocessor directive.
    | SAtom { Token = BoolLit true } -> PBool(true, r)
    | SAtom { Token = BoolLit false } -> PBool(false, r)
    | SAtom { Token = Symbol sym } ->
        if System.Char.IsUpper(sym.[0]) then
            // A capitalized bare symbol is a nullary constructor, and a pattern
            // macro may be called as one.
            match expandPatternMacro s with
            | Some expanded -> expanded
            | None -> PConstruct(sym, [], r)
        else PIdent(sym, r)
    | SAtom { Token = NumberLit n } -> PInt(n, r)
    | SAtom { Token = StringLit str } -> PString(str, r)
    | SAtom { Token = Keyword kw } -> PKeyword(kw, r)
    | SAtom { Token = CharLit c } -> PChar(c, r)
    | SAtom { Token = QuotedSymbol sym } -> PQuotedSymbol(sym, r)

    // `(:is Some.Clr.Type)` and `(:is Some.Clr.Type binder)`.
    | SList([ SAtom { Token = Keyword "is" }; SAtom { Token = Symbol typeName } ], _) ->
        PTypeTest(typeName, None, r)
    | SList([ SAtom { Token = Keyword "is" }
              SAtom { Token = Symbol typeName }
              SAtom { Token = Symbol binder } ],
            _) ->
        PTypeTest(typeName, Some binder, r)
    | SList(SAtom { Token = Keyword "is" } :: _, _) ->
        failwithf
            $"Invalid :is pattern at %s{Lexer.formatPos r}. Expected (:is Fully.Qualified.Type) or (:is Fully.Qualified.Type binding-name)."

    // `(:view step p)` — run `step` on the value and match what it answers.
    | SList([ SAtom { Token = Keyword "view" }; step; inner ], _) ->
        PView(parseViewStep step r, parsePattern inner, r)
    | SList(SAtom { Token = Keyword "view" } :: _, _) ->
        failwithf
            $"Invalid :view pattern at %s{Lexer.formatPos r}. Expected (:view step pattern), where step is a name, a form with & where the value goes, or a (fun ...)."

    // Special handling for List/Vec patterns and the spread operator
    | SList(SAtom { Token = Symbol "List" } :: args, _) ->
        let elements, tail = parseSpreadArgs r args
        PList(elements, tail, r)

    // `(Vec a b c ...)` and the bracket literal form `[a b c ...]`, which the
    // reader rewrites to `(vec-literal a b c ...)`.
    | SList(SAtom { Token = Symbol("Vec" | "vec-literal") } :: args, _) ->
        let elements, tail = parseSpreadArgs r args
        PVec(elements, tail, r)

    // `(Array a b c ...)` and the literal form `#[a b c ...]`, which the reader
    // rewrites to `(array-literal a b c ...)`.
    | SList(SAtom { Token = Symbol("Array" | "array-literal") } :: args, _) ->
        let elements, tail = parseSpreadArgs r args
        PArray(elements, tail, r)

    // `(Tuple a b ...)` and dotted pairs `(a . b ...)` which the reader rewrites to `(Tuple a b ...)`
    | SList(SAtom { Token = Symbol "Tuple" } :: args, _) ->
        PTuple(List.map parsePattern args, r)

    // `(or p q ...)` — several patterns in one position, which a `switch`
    // statement gives a label each. Before the constructor case below, which
    // would otherwise read `or` as one.
    | SList(SAtom { Token = Symbol "or" } :: args, _) ->
        match args with
        | [] ->
            failwithf
                $"Invalid or pattern at %s{Lexer.formatPos r}. (or ...) needs alternatives to choose between."
        | [ single ] -> parsePattern single
        | _ ->
            let alts = List.map parsePattern args

            // For the reason an alternative may not bind: only one of them
            // runs. A view decides the pattern by running code, and which code
            // ran would depend on which alternative was tried.
            for alt in alts do
                if not (patternSteps alt).IsEmpty then
                    failwithf
                        $"Invalid or pattern at %s{Lexer.formatPos r}: an alternative cannot run a view. Give the (:view ...) a clause of its own."

            POr(alts, r)

    // `(and p q ...)` — every one of them against the same value. Before the
    // constructor case below, for the reason `or` is, and beside it because the
    // expression forms of both names live in `parseExpr`: a head is read in the
    // position it stands in, so neither takes the other's spelling away.
    //
    // Unlike `or`, an alternative may bind and may run a view: all of them
    // match, so a name bound here has a value on the arm however the value got
    // through.
    | SList(SAtom { Token = Symbol "and" } :: args, _) ->
        match args with
        | [] ->
            failwithf
                $"Invalid and pattern at %s{Lexer.formatPos r}. (and ...) needs patterns to match together."
        | [ single ] -> parsePattern single
        | _ ->
            let alts = List.map parsePattern args

            let duplicates =
                alts
                |> List.collect patternBinders
                |> List.countBy id
                |> List.filter (fun (_, n) -> n > 1)
                |> List.map fst

            if not duplicates.IsEmpty then
                let names = String.concat ", " duplicates

                failwithf
                    $"Invalid and pattern at %s{Lexer.formatPos r}: %s{names} is bound by more than one of these, and each of them matches. Bind it once."

            PAnd(alts, r)

    // A pattern macro, before the constructor fallback: the name is tried in
    // the pattern table first, so a macro shadows a constructor of the same
    // name in pattern position.
    | SList(SAtom { Token = Symbol name } :: args, _) ->
        match expandPatternMacro s with
        | Some expanded -> expanded
        | None -> PConstruct(name, List.map parsePattern args, r)

    | SList([], _) -> PList([], None, r) // Empty list pattern

    | _ -> failwithf $"Invalid pattern at %s{Lexer.formatPos r}"

/// A pattern macro's expansion, read back as a pattern.
///
/// `Resolve` reaches the expressions in the result — which is to say a view's
/// step, the only place a pattern holds one. Rules 2 and 3 apply to it exactly
/// as they do to an expansion in expression position.
and private expandPatternMacro (s: SExpr) : Pattern option =
    patternExpandHook s
    |> Option.map (fun expansion ->
        parsePattern expansion.Form |> mapPatternSteps (expansion.Resolve Set.empty))

/// The `step` of a `(:view step p)`, as the function the view applies.
///
/// A `&` form becomes a lambda over the value rather than a partial
/// application, because the emitter inlines a lambda's body into the guard and
/// so emits a direct call. A written `(fun ...)` is already that function and
/// is taken as it stands; threading it would make the value its first argument.
///
/// The head is read through `headName`, as every dispatched head is: a template
/// writing `(fun ...)` arrives with the mark still on it.
and private parseViewStep (step: SExpr) (r: Range) : Expr =
    match step with
    | SAtom { Token = Symbol _ } -> parseExpr step
    | SList(SAtom { Token = Symbol sym } :: _, _) when
        (match headName sym with
         | "fun"
         | "bjoroutine" -> true
         | _ -> false)
        ->
        parseExpr step
    | _ ->
        let hole = Gensym.fresh "view"
        let holeAtom = SAtom { Token = Symbol hole; Range = getRange step }
        EFun([ hole ], parseExpr (threadStep holeAtom step), Ordinary, r)

/// Splits the arguments of a sequence pattern into its fixed leading elements
/// plus an optional trailing rest pattern introduced by `...`.
/// For example `a b c ...` yields ([a; b], Some c), binding `c` to the rest.
and parseSpreadArgs (r: Range) (args: SExpr list) : Pattern list * Pattern option =
    let rec go acc items =
        match items with
        | [] -> (List.rev acc, None)
        // Matches `c ...` at the end of the sequence
        | [ tailItem; SAtom { Token = Spread } ] -> (List.rev acc, Some(parsePattern tailItem))
        // Fails if spread is used incorrectly (e.g., in the middle of the sequence)
        | SAtom { Token = Spread } :: _ -> failwithf $"Invalid use of spread operator at %s{Lexer.formatPos r}"
        | head :: tail -> go (parsePattern head :: acc) tail

    go [] args

// ---------------------------------------------------------------------------
// (loop ...)
// ---------------------------------------------------------------------------

/// The argument list of a `defun`, top-level or body-local.
and parseDefunArgs (args: SExpr list) : DefunArg list =
    match args with
    | [] -> []
    | SAtom { Token = Symbol n } :: rest -> MandatoryArg(n, None) :: parseDefunArgs rest
    // `(: name type)` — a parameter's type written at the parameter. A
    // top-level `defun` normally takes its types from the signature declared
    // beside it, and one written here has to agree with that.
    | SList([ SAtom { Token = Colon }; SAtom { Token = Symbol n }; t ], _) :: rest ->
        MandatoryArg(n, Some(parseType t)) :: parseDefunArgs rest
    | SAtom { Token = Comma } :: rest -> parseDefunArgs rest
    // A keyword parameter's default is evaluated **sequentially**, with the
    // parameters before it in scope — unchanged by `let` becoming simultaneous.
    // The list is a C# parameter list by the time this runs anywhere, and a
    // default that reads an earlier parameter is emitted as an expression in the
    // method body, where the earlier one is already a local. There is nowhere to
    // put a simultaneous reading even if one were wanted.
    | SList(SAtom { Token = Keyword name } :: [ defaultExpr ], _) :: rest ->
        KeywordArg(name, parseExpr defaultExpr) :: parseDefunArgs rest
    | SAtom { Token = Keyword "rest" } :: SAtom { Token = Symbol name } :: rest ->
        if not rest.IsEmpty then
            failwithf $"Rest argument must be the last argument at %s{Lexer.formatPos (getRange (List.head rest))}"
        [RestArg name]
    | SAtom { Token = Keyword name } :: defaultExpr :: rest ->
        KeywordArg(name, parseExpr defaultExpr) :: parseDefunArgs rest
    | bad :: _ -> failwithf $"Invalid defun argument at %s{Lexer.formatPos (getRange bad)}"

/// The optional `: type` between a `defun`'s arguments and its body.
and parseDefunReturn (rest: SExpr list) : FType option * SExpr list =
    match rest with
    | SAtom { Token = Colon } :: t :: body -> Some(parseType t), body
    | body -> None, body

/// A body: a sequence of forms, where a definition scopes over what follows it.
///
/// Six heads are consumed *here* rather than by `parseExpr`, and are reserved
/// in body position because of it: `def`, `def*`, `defun`, `defbjo`,
/// `def/mutable` and `begin`. The parser has no scope at parse time, so it
/// cannot know that one of them was rebound — `(defun (begin xs) ...)` still
/// defines a function, but it can never be *called* as `(begin xs)` inside a
/// body, and `(let ((begin f)) (begin 1 2))` splices rather than calls.
///
/// The same is already true of the other five, and for the same reason.
and parseBody (exprs: SExpr list) (fallbackRange: Range) : Expr =
    // The third renaming rule, for the heads this function consumes.
    //
    // `def`, `def*`, `defun`, `def/mutable`, `defbjo` and `begin` never reach
    // `parseExpr`'s special-form chain, because `parseBody` takes them first —
    // so a template that writes `(let () (def x 1) x)` needs its mark stripped
    // here or the `def` is read as a call to something named `def`.
    //
    // `begin` is in the list for that reason and no other: a splice a macro
    // wrote arrives as `begin__37`, and without this it would fall past every
    // case below into an ordinary application and fail with "Unbound variable:
    // begin__37" — naming nothing the programmer wrote. Splicing a
    // macro-written body is the whole point of the form, so leaving it out
    // would make the feature do nothing where it is most wanted.
    //
    // Only these six, and only the head. Every other identifier keeps its
    // mark: that is what rule two resolves a macro module's own helper by, and
    // what keeps a template's binder uncapturable.
    let unmarkedHead (items: SExpr list) =
        match items with
        | SList(SAtom({ Token = Symbol sym } as head) :: rest, r) :: tail when sym <> headName sym ->
            match headName sym with
            // `def*` is here for the same reason `def` is: it is consumed by
            // this function and never reaches `parseExpr`'s chain, so a template
            // that writes one arrives marked and would otherwise be read as a
            // call to something named `def*__37`.
            | ("def"
              | "defun"
              | "defbjo"
              | "def/mutable"
              | "def*"
              | "begin") as stripped ->
                SList(SAtom { head with Token = Symbol stripped } :: rest, r) :: tail
            | _ -> items
        | _ -> items

    // `(pattern body ...)`, which is what an arm is and what nothing else in
    // the form is.
    let parseFailArm (form: SExpr) =
        match form with
        | SList(armPattern :: bodyForms, ar) when not bodyForms.IsEmpty ->
            (parsePattern armPattern, parseBody bodyForms ar)
        | bad ->
            failwithf
                $"Syntax error at %s{Lexer.formatPos (getRange bad)}: a :fail arm is written (pattern body ...)."

    // A list of lists whose heads read as patterns: the arms a reader who meant
    // `:fail` wrote in the value slot. Checked so that the answer is the missing
    // keyword rather than a type error about a call to a list.
    let looksLikeArms (form: SExpr) =
        let patternish (s: SExpr) =
            match s with
            | SAtom { Token = Symbol sym } -> sym = "_" || System.Char.IsUpper sym[0]
            | SList(SAtom { Token = Symbol sym } :: _, _) -> System.Char.IsUpper sym[0]
            | _ -> false

        match form with
        | SList((_ :: _) as arms, _) ->
            arms
            |> List.forall (function
                | SList(head :: _ :: _, _) -> patternish head
                | _ -> false)
        | _ -> false

    // What follows a clause's scrutinee: nothing, one expression, or `:fail`
    // and its arms.
    let parseDefFailure (r: Range) (forms: SExpr list) : DefFailure =
        match forms with
        | [] -> FailPropagate
        | [ SAtom { Token = Keyword "fail" }; SList((_ :: _) as armForms, _) ] ->
            FailArms(armForms |> List.map parseFailArm)
        | SAtom { Token = Keyword "fail" } :: _ ->
            failwithf
                $"Syntax error at %s{Lexer.formatPos r}: `:fail` takes one list of arms: :fail ((pattern body ...) ...)."
        | [ value ] when looksLikeArms value ->
            failwithf
                $"Syntax error at %s{Lexer.formatPos (getRange value)}: the third slot of a `def` is the value the body takes when the pattern does not match, and this is a list of arms. Arms go after `:fail`: (def pattern scrutinee :fail ((pattern body ...) ...))."
        | [ value ] -> FailValue(parseExpr value)
        | _ ->
            failwithf
                $"Syntax error at %s{Lexer.formatPos r}: expected (def pattern scrutinee), (def pattern scrutinee value) or (def pattern scrutinee :fail (arm ...))."

    // The clauses of a `def*`, in source order.
    //
    // Every clause's binders share one scope — the sequel's — so a name bound
    // by two of them is refused rather than shadowed.
    let parseDefStarClauses (r: Range) (clauseForms: SExpr list) =
        if clauseForms.IsEmpty then
            failwithf
                $"Syntax error at %s{Lexer.formatPos r}: expected (def* (pattern scrutinee ...) ...). A def* clause is always parenthesised, including when there is only one."

        let clauses =
            clauseForms
            |> List.map (function
                | SList(binder :: scrutinee :: failure, cr) ->
                    (parsePattern binder, parseExpr scrutinee, parseDefFailure cr failure, cr)
                | bad ->
                    failwithf
                        $"Syntax error at %s{Lexer.formatPos (getRange bad)}: a def* clause is written (pattern scrutinee), (pattern scrutinee value) or (pattern scrutinee :fail (arm ...)).")

        clauses
        |> List.iteri (fun i (binder, _, _, cr) ->
            let mine = patternBinders binder |> Set.ofList

            clauses
            |> List.iteri (fun j (earlier, _, _, _) ->
                if j < i then
                    match patternBinders earlier |> List.filter mine.Contains with
                    | [] -> ()
                    | shared ->
                        let names = String.concat ", " shared

                        failwithf
                            $"Syntax error at %s{Lexer.formatPos cr}: %s{names} is bound by clause %d{j + 1} of this def* as well, and both are in scope in the rest of the body. Bind it once."))

        clauses

    let rec collectDefs acc remaining =
        match unmarkedHead remaining with
        // A non-empty splice does not close the group. Without this case a
        // `begin` between two mutually recursive definitions would put them in
        // separate `ELetRec`s, and the first would fail with "Unbound
        // variable" naming the second — on a program that looks obviously fine.
        //
        // We don't need a special case to handle empty blocks here. The parser
        // naturally treats an empty block as a basic statement. Just like any
        // normal expression, hitting a statement signals the end of the current
        // block of definitions.
        | SList(SAtom { Token = Symbol "begin" } :: (_ :: _ as inner), _) :: rest ->
            collectDefs acc (inner @ rest)

        | SList(SAtom { Token = Symbol "def" } :: SAtom { Token = Symbol name } :: [ expr ], _) :: rest ->
            // isFun = false, args = []
            collectDefs ((name, false, [], None, parseExpr expr) :: acc) rest

        | SList(SAtom { Token = Symbol "def" } :: SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) :: [ expr ], _) :: rest ->
            collectDefs ((name, false, [], Some(parseType tType), parseExpr expr) :: acc) rest

        // A body-local `defbjo`. Still rejected, and no longer for the reason it
        // used to be: a body-local function may suspend now, emitted as an
        // async C# local function. What it may not do is *declare* that, and
        // that is the language's own rule rather than a limit of the emitter —
        // colour is declared where a signature is required, and a local
        // definition has none. Its colour is read off what its body reaches.
        //
        // So the form has nothing left to mean. Accepting it would give the
        // reader two spellings with one behaviour, and the wrong impression
        // that the other spelling does not suspend.
        | SList(SAtom { Token = Symbol "defbjo" } :: SList(SAtom { Token = Symbol name } :: _, _) :: _, r) :: _ ->
            failwithf
                $"Syntax Error at %s{Lexer.formatPos r}: a bjoroutine may only be defined at the top level, and '%s{name}' is inside a body. Write it (defun ...): a body-local function takes its colour from what its body reaches, so it suspends when it needs to and costs nothing when it does not."

        | SList(SAtom { Token = Symbol "defun" } :: SList(SAtom { Token = Symbol name } :: args, _) :: rest, r) :: rest' ->
            let parsedArgs = parseDefunArgs args
            let retType, bodyExprs = parseDefunReturn rest
            let fBody = parseBody bodyExprs r
            collectDefs ((name, true, parsedArgs, retType, fBody) :: acc) rest'

        | _ -> (List.rev acc, remaining)

    and parseItems remaining =
        match unmarkedHead remaining with
        // A body with nothing in it is `unit` — the value a Bjolang signature
        // spells `void`. Not `ETuple []`, which is an empty *tuple* and unifies
        // with nothing anyone can write; `(begin)` in expression position is
        // the form that made the difference reachable from source.
        | [] -> EResolved("unit", fallbackRange)

        // `(begin)` with nothing in it is `unit`, and *not* a splice of
        // nothing. The two differ in exactly one place, and it matters:
        //
        //   (defun (f)
        //     (compute!)
        //     (begin))
        //
        // Were the empty form to vanish, `(compute!)` would move from statement
        // position into tail position — the function's value would silently
        // become whatever it returns, and `MustUse` would stop reporting a
        // dropped `Result`. As `unit` it keeps `(compute!)` where it was
        // written and gives the body the value "nothing", which is what a form
        // that does nothing should mean.
        //
        // Declaration position reads an empty `begin` the other way, because
        // there is no value there for it to be.
        //
        // `unit` the builtin, not `ETuple []`: an empty tuple is a *tuple*, and
        // the unit type is what a Bjolang signature spells `void`. The two do
        // not unify, so a `(defun (f) (begin))` declared `(-> void)` would be a
        // type error naming a type nobody wrote.
        | [ SList([ SAtom { Token = Symbol "begin" } ], r) ] -> EResolved("unit", r)

        | SList([ SAtom { Token = Symbol "begin" } ], r) :: rest ->
            ELet("_", false, [], None, EResolved("unit", r), parseItems rest, fallbackRange)

        // A non-empty one splices into the body it stands in, which is what
        // lets a macro expand to several forms — a definition and the code
        // after it — where only one was written.
        | SList(SAtom { Token = Symbol "begin" } :: inner, _) :: rest -> parseItems (inner @ rest)

        // `(def* (pattern scrutinee ...) ...)` — several clauses, run in source
        // order.
        //
        // Nested one inside the next, so that short-circuiting and sequential
        // scope come from the shape rather than from a rule: a clause's sequel
        // is the clause after it, and the last one's is the rest of the body.
        | SList(SAtom { Token = Symbol "def*" } :: clauseForms, r) :: rest ->
            let clauses = parseDefStarClauses r clauseForms

            List.foldBack
                (fun (binder, scrutinee, failure, cr) sequel -> EDefMatch(binder, scrutinee, failure, sequel, cr))
                clauses
                (parseItems rest)

        // `(def pattern scrutinee)`, `(def pattern scrutinee value)` and
        // `(def pattern scrutinee :fail (arm ...))` — a binding that may fail.
        //
        // Consumed here beside the plain `def` shapes because it *is* one of
        // them: what the pattern binds scopes over the rest of this body. That
        // is `def`'s scoping and not `let`'s — a `let` opens a nested scope
        // with a body of its own — which is why the form is handed
        // `parseItems rest` as its sequel rather than desugared in `parseExpr`,
        // which has no sequel to give it.
        //
        // The pattern is unparenthesised at the head, so the third slot is
        // always the failure value and never arms; arms are written after
        // `:fail`, where nothing is positional.
        //
        // Reached before the plain shapes below, and only for what they do not
        // cover: a pattern that is not a name, a typed name or a tuple of
        // names, or any `def` at all that carries a failure part.
        | SList(SAtom { Token = Symbol "def" } :: binder :: scrutinee :: failureForms, r) :: rest when
            not failureForms.IsEmpty || not (isPlainDefBinder binder)
            ->
            EDefMatch(parsePattern binder, parseExpr scrutinee, parseDefFailure r failureForms, parseItems rest, r)

        | SList(SAtom { Token = Symbol "def/mutable" } :: SAtom { Token = Symbol name } :: [ expr ], r) :: rest ->
            ELetMutable(name, None, parseExpr expr, parseItems rest, fallbackRange)

        | SList(SAtom { Token = Symbol "def/mutable" } :: SList([ SAtom { Token = Colon }; SAtom { Token = Symbol name }; tType ], _) :: [ expr ], r) :: rest ->
            ELetMutable(name, Some(parseType tType), parseExpr expr, parseItems rest, fallbackRange)

        // Local tuple destructuring: (def (a b) expr).
        // Recognized before the `def` case below to prevent infinite recursion.
        | SList(SAtom { Token = Symbol "def" } :: SList(names, _) :: [ expr ], r) :: rest when
            not names.IsEmpty
            && names
               |> List.forall (function
                   | SAtom { Token = Symbol _ }
                   | SAtom { Token = Comma } -> true
                   | _ -> false)
            ->
            let rawNames =
                names
                |> List.choose (function
                    | SAtom { Token = Symbol n } -> Some n
                    | _ -> None)
            let tupleNames =
                match rawNames with
                | "Tuple" :: restNames -> restNames
                | _ -> rawNames

            ELetTuple(tupleNames, parseExpr expr, parseItems rest, r)
        // `defbjo` is listed so that it reaches `collectDefs`, which rejects it
        // by name; left out, it would fall through to an ordinary application
        // and fail with "Unbound variable: defbjo".
        | (SList(SAtom { Token = Symbol "def" } :: _, _)) :: _
        | (SList(SAtom { Token = Symbol "defbjo" } :: _, _)) :: _
        | (SList(SAtom { Token = Symbol "defun" } :: _, _)) :: _ ->
            let defs, rest = collectDefs [] remaining

            // Nothing recognized means nothing consumed, and recursing on an
            // unchanged list does not terminate. Whatever shape this is, saying
            // so is the only safe answer.
            if defs.IsEmpty then
                let r = getRange (List.head remaining)

                failwithf
                    $"Invalid def form at %s{Lexer.formatPos r}. Expected (def name expr), (def (: name type) expr), (def (a b ...) expr), (def pattern scrutinee ...) or (defun (name args...) body)."

            ELetRec(defs, parseItems rest, fallbackRange)

        // A macro in body position, expanded here rather than left to
        // `parseExpr`, because it may expand to a definition and `def` and
        // `defun` are consumed by this function — `parseExpr` never sees one.
        //
        // The resolution pass wraps the *whole* remaining body, and must: if
        // the expansion is a definition then everything after it is inside its
        // scope, and whether an introduced name is free is only answerable
        // there.
        | (SList(SAtom { Token = Symbol h } :: _, _) as form) :: rest when isMacroName h ->
            match expandHook form with
            // `Set.empty`: the spliced definitions are inside the expression
            // tree this parses, so `freeNames` already sees them as bound.
            | Some expansion -> expansion.Resolve Set.empty (parseItems (expansion.Form :: rest))
            | None -> failwithf $"'%s{h}' is a macro but did not expand at %s{Lexer.formatPos (getRange form)}"

        | [ expr ] -> parseExpr expr

        | expr :: rest -> ELet("_", false, [], None, parseExpr expr, parseItems rest, fallbackRange)

    parseItems exprs

/// What `LoopDesugar` reads its clauses' expressions and patterns with.
///
/// A member of this group because that is the only place `parseExpr` and
/// `parsePattern` are in scope by name; the desugarers are compiled before the
/// parser and can only reach them through this record.
and private parseFns () : ParseFns =
    { Expr = parseExpr
      Pattern = parsePattern }



