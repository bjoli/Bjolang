namespace Bjolang

module Lexer =
    open System

    // 1-based lines, 0-based columns (aligns with FSharp.Compiler.Text.Position)
    type Position = { Line: int; Column: int }

    /// `File` is the source the range came from. It matters because `include`
    /// splices one file's forms into another, so a line number on its own is
    /// ambiguous.
    type Range = { Start: Position; End: Position; File: string }

    let private fileLabel (file: string) =
        if String.IsNullOrEmpty file then "<unknown>" else IO.Path.GetFileName file

    /// A location for a diagnostic, as `file.bjo:12`.
    let formatPos (r: Range) = $"%s{fileLabel r.File}:%d{r.Start.Line}"

    /// The same, inside the tokenizer, where there is no `Range` to hand yet.
    let formatAt (file: string) (line: int) (col: int) = $"%s{fileLabel file}:%d{line}:%d{col}"

    type Token =
        | Hash
        | Quote
        | LParen
        | RParen
        | LBracket
        | RBracket
        | LBrace
        | RBrace
        | Comma
        /// `,@` — unquote-splicing, inside a `#'` template. A token of its own
        /// because `,@x` would otherwise lex as `Comma` followed by a symbol
        /// literally named `@x`.
        | CommaAt
        /// `#'` — syntax-quote. Distinct from `Quote` because the two build
        /// different things: `'(a b)` is a homogeneous list literal and
        /// `#'(a b)` is a `Syntax` tree. Deciding between them by whether the
        /// enclosing form is a `def/macro` would make the meaning of a helper
        /// function's quote depend on which file it sits in.
        | SynQuote
        | Colon
        | Dot
        | Spread
        | StringLit of string
        /// A Unicode scalar value, not a UTF-16 code unit.
        ///
        /// `BjoChar` is a 32-bit codepoint, so this cannot be a C# `char`: an
        /// astral character written literally in source arrives as a surrogate
        /// pair and has to be recombined into the one codepoint it stands for.
        | CharLit of int
        /// `#t` and `#f`.
        ///
        /// A token of its own rather than a symbol, because a binder position
        /// matches a symbol: spelled `Symbol "#t"` a boolean could be bound,
        /// and `(let ((#t 1)) ...)` was accepted.
        | BoolLit of bool
        /// A name the reader itself wrote, which the parser turns into an
        /// `EResolved` rather than an ordinary reference.
        ///
        /// String interpolation expands to tokens rather than to a syntax tree
        /// — that is what makes it a *reader* feature — so it has no node to
        /// emit and needs a token to say "this `->str` is mine".
        ///
        /// Nothing else produces one, and no spelling reads as one, so a
        /// program cannot write what would capture it.
        | ResolvedSymbol of string
        | NumberLit of string
        | Keyword of string
        | Symbol of string
        | TypeVar of string
        | QuotedSymbol of string

    type LexedToken = { Token: Token; Range: Range }

    /// What a backslash escape stands for, or `None` for one this syntax does
    /// not define.
    ///
    /// One table for both string syntaxes so they cannot drift apart — they
    /// had, and a plain `"\r"` was two characters while an interpolated one was
    /// a carriage return. `$` is the single difference and takes a flag rather
    /// than a table of its own: it is special only where a hole can open, and
    /// escaping it in a plain string would be escaping nothing.
    let private escapeOf (allowDollar: bool) (c: char) : char option =
        match c with
        | 'n' -> Some '\n'
        | 't' -> Some '\t'
        | 'r' -> Some '\r'
        | '"' -> Some '"'
        | '\\' -> Some '\\'
        | '$' when allowDollar -> Some '$'
        | _ -> None

    /// An unrecognized escape keeps both of its characters.
    ///
    /// So `"C:\path"` reads as itself rather than failing, and a backslash that
    /// meant nothing in particular survives to be looked at.
    let private appendEscape (sb: Text.StringBuilder) (allowDollar: bool) (escaped: char) =
        match escapeOf allowDollar escaped with
        | Some c -> sb.Append(c) |> ignore
        | None -> sb.Append('\\').Append(escaped) |> ignore

    /// Decode the escapes in a string literal's body, left to right.
    ///
    /// One pass, and that is the whole point. This was a chain of `Replace`
    /// calls, which is order-dependent: `\n` was substituted before `\\` was, so
    /// an escaped backslash could not protect the character after it and
    /// `"\\n"` came out as a backslash followed by a newline. A literal
    /// backslash before an `n`, a `t` or a quote was unwritable.
    let private unescapeString (raw: string) : string =
        let sb = Text.StringBuilder(raw.Length)
        let mutable i = 0

        while i < raw.Length do
            if raw[i] = '\\' && i + 1 < raw.Length then
                appendEscape sb false raw[i + 1]
                i <- i + 2
            else
                sb.Append(raw[i]) |> ignore
                i <- i + 1

        sb.ToString()

    /// One piece of a `#"..."` string: literal text, or the source of a
    /// `${ ... }` hole together with where it starts in the enclosing file.
    ///
    /// The hole is kept as *source* rather than tokenized on the spot because
    /// tokenizing it is a recursive call to the whole reader — which is what
    /// makes a hole an arbitrary expression, nested interpolations included.
    type private InterpSegment =
        | InterpText of string
        | InterpHole of string * int * int

    /// Moves a range produced by lexing a fragment on its own to where that
    /// fragment actually sits.
    ///
    /// A fragment is lexed starting at line 1, column 0, so its first line is
    /// offset by both, and every later line only by the line count: column 0 of
    /// the fragment's second line really is column 0 of the file's.
    let private shiftToken (baseLine: int) (baseCol: int) (t: LexedToken) : LexedToken =
        let shift (p: Position) =
            if p.Line = 1 then
                { Line = baseLine; Column = baseCol + p.Column }
            else
                { Line = baseLine + p.Line - 1; Column = p.Column }

        { t with
            Range =
                { t.Range with
                    Start = shift t.Range.Start
                    End = shift t.Range.End } }

    /// Whether a character may appear inside a symbol.
    ///
    /// At module level because an import prefix has to be checkable against the
    /// same answer the tokenizer would give: a prefix that does not lex as part
    /// of a symbol produces a name nothing can write.
    let isSymbolChar c =
        not (Char.IsWhiteSpace c)
        && not (List.contains c [ '('; ')'; '['; ']'; '{'; '}'; ','; ':'; '"'; ';'; '\'' ])

    let rec tokenize (file: string) (input: string) : LexedToken list =
        let length = input.Length

        let rec following charList pos =
            if List.isEmpty charList then
                true
            elif pos >= String.length input then
                false
            elif (List.head charList) = input[pos] then
                following (List.tail charList) (pos + 1)
            else
                false


        let rec readSymbol p =
            if p < length && isSymbolChar input[p] then
                readSymbol (p + 1)
            else
                p

        // Calculates the new line and column after consuming a chunk of text
        let advance (text: string) startLine startCol =
            let mutable l = startLine
            let mutable c = startCol

            for i = 0 to text.Length - 1 do
                if text[i] = '\n' then
                    l <- l + 1
                    c <- 0
                else
                    c <- c + 1

            l, c

        let rec loop pos line col tokens =
            if pos >= length then
                List.rev tokens
            else
                let c = input[pos]

                // Helper to emit a token and automatically calculate its range
                let emit t len =
                    let text = input.Substring(pos, len)
                    let endLine, endCol = advance text line col

                    let range =
                        { Start = { Line = line; Column = col }
                          End = { Line = endLine; Column = endCol }
                          File = file }

                    loop (pos + len) endLine endCol ({ Token = t; Range = range } :: tokens)




                match c with
                // Whitespace tracking
                | '\n' -> loop (pos + 1) (line + 1) 0 tokens
                | '\r' -> loop (pos + 1) line col tokens
                | _ when Char.IsWhiteSpace c -> loop (pos + 1) line (col + 1) tokens

                // Comments
                | ';' ->
                    let rec skipLine p =
                        if p >= length || input[p] = '\n' then
                            p
                        else
                            skipLine (p + 1)

                    let nextPos = skipLine pos
                    let len = nextPos - pos
                    let text = input.Substring(pos, len)
                    let endLine, endCol = advance text line col
                    loop nextPos endLine endCol tokens


                // Delimiters
                | '(' -> emit LParen 1
                | ')' -> emit RParen 1
                | '[' -> emit LBracket 1
                | ']' -> emit RBracket 1
                // Braces open a comprehension. They are delimiters rather than
                // symbol characters, which is a change: `{listing` would
                // otherwise lex as one symbol. Nothing in the language used a
                // brace before this.
                | '{' -> emit LBrace 1
                | '}' -> emit RBrace 1
                | ',' when pos + 1 < length && input[pos + 1] = '@' -> emit CommaAt 2
                | ',' -> emit Comma 1
                // There are two types of keywords right now...
                | ':' when pos + 1 < length && isSymbolChar input[pos + 1] ->
                    let nextPos = readSymbol (pos + 1)
                    let len = nextPos - pos
                    emit (Keyword(input.Substring(pos + 1, len - 1))) len
                | ':' -> emit Colon 1

                // Spread Operator
                | '.' when pos + 2 < length && input[pos + 1] = '.' && input[pos + 2] = '.' -> emit Spread 3

                // A dot *joined* to what follows it is part of a symbol, not a
                // separator: `.Write` and `.-Length` are the names of an
                // instance method and a property, and they have to survive as
                // single symbols. `Pipeline.read` turns any form containing a
                // bare `Dot` into a tuple, so lexing `(.Write w "x")` as
                // `Dot Symbol Symbol String` did not fail — it silently read
                // the call as `(Tuple Write w "x")`.
                //
                // A dotted pair still writes its dot with space around it, so
                // `(a . b)` is unaffected.
                | '.' when pos + 1 < length && isSymbolChar input[pos + 1] ->
                    let nextPos = readSymbol (pos + 1)
                    let len = nextPos - pos
                    emit (Symbol(input.Substring(pos, len))) len

                | '.' -> emit Dot 1

                // Strings
                | '"' ->
                    let rec readString p =
                        if p >= length then
                            failwithf $"Unterminated string at %s{formatAt file line col}"
                        elif input[p] = '"' then
                            p + 1
                        elif input[p] = '\\' && p + 1 < length then
                            readString (p + 2)
                        else
                            readString (p + 1)

                    let nextPos = readString (pos + 1)
                    let len = nextPos - pos
                    let rawStr = input.Substring(pos + 1, len - 2)

                    let unescaped = unescapeString rawStr

                    let text = input.Substring(pos, len)
                    let endLine, endCol = advance text line col

                    let range =
                        { Start = { Line = line; Column = col }
                          End = { Line = endLine; Column = endCol }
                          File = file }

                    loop
                        nextPos
                        endLine
                        endCol
                        ({ Token = StringLit unescaped
                           Range = range }
                         :: tokens)

                // Type variables use % prefix: %a, %b, etc.
                | '%' when pos + 1 < length && isSymbolChar input[pos + 1] ->
                    let nextPos = readSymbol (pos + 1)
                    let len = nextPos - pos
                    let varName = input.Substring(pos + 1, len - 1)
                    emit (QuotedSymbol varName) len

                // Quote: '(1 2 3) for list literals, 'symbol for quoted symbols
                | '\'' ->
                    if pos + 1 < length && input[pos + 1] = '(' then
                        // Standalone quote before a paren — the S-expression reader handles the rest
                        emit Quote 1
                    elif pos + 1 < length && isSymbolChar input[pos + 1] then
                        // Quoted symbol: 'foo
                        let nextPos = readSymbol (pos + 1)
                        let len = nextPos - pos
                        let varName = input.Substring(pos + 1, len - 1)
                        emit (QuotedSymbol varName) len
                    else
                        emit Quote 1
                // Numbers
                | _ when Char.IsDigit c || (c = '-' && pos + 1 < length && Char.IsDigit input[pos + 1]) ->
                    let rec readNumber p =
                        if
                            p < length
                            && (Char.IsLetterOrDigit input[p] || input[p] = '.' || input[p] = '-')
                        then
                            readNumber (p + 1)
                        else
                            p

                    let nextPos = readNumber pos
                    let len = nextPos - pos
                    emit (NumberLit(input.Substring(pos, len))) len

                // Hashtag prefixes (#:, #\, #(, #[, etc.)
                | '#' when pos + 1 < length ->
                    match input[pos + 1] with
                    | '(' -> emit Hash 1
                    | '[' -> emit Hash 1

                    // `#"a ${x} b"` — an interpolated string. The reader
                    // expands it; `readInterpolatedString` below says what it
                    // expands to and why.
                    | '"' ->
                        let expansion, nextPos, nextLine, nextCol =
                            readInterpolatedString file input pos line col

                        loop nextPos nextLine nextCol (List.rev expansion @ tokens)

                    | '\'' -> emit SynQuote 2
                    | ':' -> // Keywords (#:keyword)
                        let nextPos = readSymbol (pos + 2)
                        let len = nextPos - pos
                        emit (Keyword(input.Substring(pos + 2, len - 2))) len

                    | '\\' -> // Scheme character literals (#\c, #\space, #\x41)
                        let rec readCharLiteral p =
                            if p < length && isSymbolChar input[p] then
                                readCharLiteral (p + 1)
                            else
                                p

                        let nameEnd = readCharLiteral (pos + 2)

                        // A surrogate pair is *one* character spelled with two
                        // UTF-16 units, and it has to be recognised before the
                        // name rule below: both halves pass `isSymbolChar`, so
                        // an emoji would otherwise be read as a two-character
                        // name and rejected.
                        let isAstral =
                            pos + 3 < length
                            && Char.IsHighSurrogate input[pos + 2]
                            && Char.IsLowSurrogate input[pos + 3]

                        // A name is only a name if it is longer than one
                        // character. Otherwise the literal is whatever single
                        // character follows the backslash — including one that
                        // is not a symbol character at all, so `#\(`, `#\;` and
                        // `#\ ` all lex, as R7RS requires. Reading the name run
                        // first and falling back is what lets both spellings
                        // share one rule.
                        if isAstral then
                            emit (CharLit(Char.ConvertToUtf32(input[pos + 2], input[pos + 3]))) 4
                        elif nameEnd - (pos + 2) > 1 then
                            let name = input.Substring(pos + 2, nameEnd - (pos + 2))
                            let len = nameEnd - pos

                            let codepoint =
                                match name.ToLowerInvariant() with
                                | "space" -> 0x20
                                | "newline" | "linefeed" -> 0x0A
                                | "tab" -> 0x09
                                | "return" -> 0x0D
                                | "null" | "nul" -> 0x00
                                | "alarm" -> 0x07
                                | "backspace" -> 0x08
                                | "delete" | "rubout" -> 0x7F
                                | "escape" | "esc" -> 0x1B
                                | hex when hex.StartsWith "x" && hex.Length > 1 ->
                                    match System.Int32.TryParse(
                                              hex.Substring 1,
                                              Globalization.NumberStyles.HexNumber,
                                              Globalization.CultureInfo.InvariantCulture) with
                                    | true, value when value >= 0 && value <= 0x10FFFF -> value
                                    | _ ->
                                        failwithf
                                            $"Invalid character literal #\\%s{name} at %s{formatAt file line col}: not a Unicode scalar value."
                                | _ ->
                                    failwithf
                                        $"Unknown character name #\\%s{name} at %s{formatAt file line col}."

                            emit (CharLit codepoint) len
                        elif pos + 2 < length then
                            emit (CharLit(int input[pos + 2])) 3
                        else
                            failwithf $"Unterminated character literal at %s{formatAt file line col}."

                    // `#` introduces a reader form; it is not a name character.
                    // What can follow it here is one of the two booleans, or
                    // `#map`, which the reader consumes together with the
                    // bracket after it.
                    //
                    // Anything else used to be read as a symbol and became an
                    // ordinary identifier. Bjolang accepted it all the way
                    // through and C# refused it, because `#` there begins a
                    // preprocessor directive — so `(def #banana 5)` was a
                    // CS1040 about a generated file.
                    | _ ->
                        let nextPos = readSymbol pos
                        let len = nextPos - pos
                        let text = input.Substring(pos, len)

                        match text with
                        | "#t" -> emit (BoolLit true) len
                        | "#f" -> emit (BoolLit false) len
                        | "#map" -> emit (Symbol text) len
                        | _ ->
                            failwithf
                                $"Unknown reader syntax '%s{text}' at %s{formatAt file line col}. '#' begins a reader form — #t, #f, #\\c, #:keyword, #'template, #(...), #[...], #map(...) or #\"...\" — and is not part of a name."
                | '#' -> emit Hash 1

                // Symbols
                | _ ->
                    let nextPos = readSymbol pos
                    let len = nextPos - pos
                    emit (Symbol(input.Substring(pos, len))) len

        loop 0 1 0 []

    /// Reads `#"a ${x} b"` and returns the tokens it stands for, followed by
    /// the position just past its closing quote.
    ///
    /// The expansion is `(str "a " (->str x) " b")`. Doing it in the reader is
    /// what a reader macro is, and it is what makes the feature cost nothing
    /// anywhere else: there is no new token for the parser, no new node for
    /// inference, nothing for the emitter, and a `#"..."` inside a macro
    /// template is a template of the expansion without anyone arranging for it.
    ///
    /// `->str` around every hole is what lets the parts have different types. A
    /// `#:rest` parameter has one element type, so `str` can only be variadic
    /// over `string`, and each part has to be converted while it still has a
    /// type of its own. It is the trait, so a type with an impl of its own is
    /// formatted by that impl.
    ///
    /// A function of its own rather than an arm of the tokenizer's loop, which
    /// is only about size: a hole is delimited by brace *depth* rather than by
    /// a token, so this is a cursor and a `while` where everything around it is
    /// a fold over tokens.
    ///
    /// Mutually recursive with the reader, which is what makes a hole an
    /// arbitrary expression — nested interpolations included.
    and private readInterpolatedString
        (file: string)
        (input: string)
        (pos: int)
        (line: int)
        (col: int)
        : LexedToken list * int * int * int =

        let length = input.Length
        let segments = ResizeArray<InterpSegment>()
        let literal = Text.StringBuilder()

        let mutable p = pos + 2
        let mutable l = line
        let mutable c = col + 2
        let mutable closed = false

        let step (ch: char) =
            if ch = '\n' then
                l <- l + 1
                c <- 0
            else
                c <- c + 1

            p <- p + 1

        let flush () =
            if literal.Length > 0 then
                segments.Add(InterpText(literal.ToString()))
                literal.Clear() |> ignore

        while not closed do
            if p >= length then
                failwithf $"Unterminated interpolated string at %s{formatAt file line col}"

            match input[p] with
            | '"' ->
                step '"'
                closed <- true

            // `\$` is how a dollar that opens nothing is written. Everything
            // else escapes as it does in an ordinary string.
            | '\\' when p + 1 < length ->
                let escaped = input[p + 1]
                appendEscape literal true escaped
                step '\\'
                step escaped

            | '$' when p + 1 < length && input[p + 1] = '{' ->
                flush ()
                step '$'
                step '{'

                let holeStart = p
                let holeLine = l
                let holeCol = c
                let mutable depth = 1

                while depth > 0 do
                    if p >= length then
                        failwithf $"Unterminated ${{ ... }} in the interpolated string at %s{formatAt file line col}"

                    match input[p] with
                    // A brace inside a nested string literal is not a brace of
                    // the hole, so the string is skipped whole. Without this,
                    // `${(f "}")}` ends in the wrong place.
                    | '"' ->
                        step '"'
                        let mutable inString = true

                        while inString do
                            if p >= length then
                                failwithf
                                    $"Unterminated string inside ${{ ... }} at %s{formatAt file holeLine holeCol}"
                            elif input[p] = '\\' && p + 1 < length then
                                let a = input[p]
                                let b = input[p + 1]
                                step a
                                step b
                            elif input[p] = '"' then
                                step '"'
                                inString <- false
                            else
                                step input[p]

                    // And neither is one written as a character literal: `#\{`
                    // is a value, not a nesting.
                    | '#' when p + 2 < length && input[p + 1] = '\\' ->
                        step '#'
                        step '\\'
                        step input[p]

                    | '{' ->
                        depth <- depth + 1
                        step '{'
                    | '}' ->
                        depth <- depth - 1
                        step '}'
                    | ch -> step ch

                // `p` is one past the closing brace, which is therefore at
                // `p - 1`.
                segments.Add(InterpHole(input.Substring(holeStart, p - 1 - holeStart), holeLine, holeCol))

            | ch ->
                literal.Append ch |> ignore
                step ch

        flush ()

        let range =
            { Start = { Line = line; Column = col }
              End = { Line = l; Column = c }
              File = file }

        // The synthesized tokens all carry the whole form's range: `str` and
        // `->str` were written by nobody, and the nearest true thing to say
        // about where they are is "here". A hole's own tokens keep their real
        // positions, so a type error inside one is reported against the source
        // that caused it.
        let mk t = { Token = t; Range = range }

        let parts =
            segments
            |> Seq.map (fun segment ->
                match segment with
                | InterpText text -> [ mk (StringLit text) ]
                | InterpHole(source, hl, hc) ->
                    let inner = tokenize file source |> List.map (shiftToken hl hc)

                    if inner.IsEmpty then
                        failwithf
                            $"Empty ${{}} in the interpolated string at %s{formatAt file hl hc}: there is nothing to interpolate."

                    [ mk LParen; mk (ResolvedSymbol "->str") ] @ inner @ [ mk RParen ])
            |> List.ofSeq

        // One part is already a string, and no parts is the empty one. Neither
        // is a special case in the language — both are what `(str ...)` of them
        // would produce — so this is only about not emitting a call with
        // nothing to concatenate.
        let expansion =
            match parts with
            | [] -> [ mk (StringLit "") ]
            | [ single ] -> single
            | many -> [ mk LParen; mk (Symbol "str") ] @ List.concat many @ [ mk RParen ]

        expansion, p, l, c
