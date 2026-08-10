namespace Bjolang

module Lexer =
    open System

    // 1-based lines, 0-based columns (aligns with FSharp.Compiler.Text.Position)
    type Position = { Line: int; Column: int }

    /// `File` is the source the range came from. It matters because `include`
    /// splices one file's forms into another, so a line number on its own is
    /// ambiguous.
    type Range = { Start: Position; End: Position; File: string }

    /// A location for a diagnostic, as `file.bjo:12`.
    let formatPos (r: Range) =
        let name =
            if String.IsNullOrEmpty r.File then "<unknown>"
            else IO.Path.GetFileName r.File

        $"%s{name}:%d{r.Start.Line}"

    type Token =
        | Hash
        | Quote
        | LParen
        | RParen
        | LBracket
        | RBracket
        | Comma
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
        | NumberLit of string
        | Keyword of string
        | Symbol of string
        | TypeVar of string
        | QuotedSymbol of string

    type LexedToken = { Token: Token; Range: Range }

    let tokenize (file: string) (input: string) : LexedToken list =
        let length = input.Length

        let isSymbolChar c =
            not (Char.IsWhiteSpace c)
            && not (List.contains c [ '('; ')'; '['; ']'; ','; ':'; '"'; ';'; '\'' ])

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
                            failwithf $"Unterminated string at line %d{line}, col %d{col}"
                        elif input[p] = '"' then
                            p + 1
                        elif input[p] = '\\' && p + 1 < length then
                            readString (p + 2)
                        else
                            readString (p + 1)

                    let nextPos = readString (pos + 1)
                    let len = nextPos - pos
                    let rawStr = input.Substring(pos + 1, len - 2)

                    // Simple unescaping for the final AST value
                    let unescaped =
                        rawStr.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\")

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
                                            $"Invalid character literal #\\%s{name} at line %d{line}, col %d{col}: not a Unicode scalar value."
                                | _ ->
                                    failwithf
                                        $"Unknown character name #\\%s{name} at line %d{line}, col %d{col}."

                            emit (CharLit codepoint) len
                        elif pos + 2 < length then
                            emit (CharLit(int input[pos + 2])) 3
                        else
                            failwithf $"Unterminated character literal at line %d{line}, col %d{col}."

                    | _ -> // Fallback for booleans (#t, #f) or symbols starting with #
                        let nextPos = readSymbol pos
                        let len = nextPos - pos
                        emit (Symbol(input.Substring(pos, len))) len
                | '#' -> emit Hash 1

                // Symbols
                | _ ->
                    let nextPos = readSymbol pos
                    let len = nextPos - pos
                    emit (Symbol(input.Substring(pos, len))) len

        loop 0 1 0 []
