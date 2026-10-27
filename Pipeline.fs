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

module Bjolang.Pipeline

open System
open System.IO
open Bjolang.Lexer
open Bjolang.Ast
open Bjolang.LetRecify

let unionLexerRanges (r1: Lexer.Range) (r2: Lexer.Range) : Lexer.Range =
    // The two ranges can come from different files once `include` is involved.
    // The opening one wins: it is where the form the caller is describing began.
    { r1 with End = r2.End }

/// `&` as a spelling of `&1` inside `#(...)`.
/// 
/// A nested `#(...)` needs no exception. The reader has already turned it into
/// a `fun` of its own, with its own `&` rewritten, before this runs — which is
/// also what keeps `&` inside a `#(...)` inside a `->` the lambda's rather
/// than the thread's.
let rec expandBareArg (expr: SExpr) : SExpr =
    match expr with
    | SAtom({ Token = Lexer.Symbol "&" } as atom) -> SAtom { atom with Token = Lexer.Symbol "&1" }
    | SList(items, r) -> SList(List.map expandBareArg items, r)
    | other -> other

let rec collectPositionalArgs (expr: SExpr) : Set<int> =
    match expr with
    | SAtom { Token = Lexer.Symbol sym } when sym.Length > 1 && sym.StartsWith("&") ->
        match System.Int32.TryParse(sym.Substring(1)) with
        | true, n when n > 0 -> Set.singleton n
        | _ -> Set.empty
    | SList(items, _) ->
        items |> List.map collectPositionalArgs |> Set.unionMany
    | _ -> Set.empty

/// Folds `#'` into `(syntax-quote form)`.
///
/// `#'` is a prefix on the *form* after it, and which shapes a form can take is
/// the reader's whole job — a list, a vector literal, a comprehension, an atom,
/// or another `#'`. Rather than enumerate those at the token level, the reader
/// emits `#'` as an ordinary atom and this runs once a level's forms are known,
/// where "the next form" is just the next element.
///
/// Right to left, so `#'#'x` nests the way it reads.
let rec private collapseSynQuote (nodes: SExpr list) : SExpr list =
    match nodes with
    | SAtom({ Token = SynQuote } as q) :: rest ->
        match collapseSynQuote rest with
        | form :: tail ->
            let head = SAtom { Token = Lexer.Symbol "syntax-quote"; Range = q.Range }
            SList([ head; form ], unionLexerRanges q.Range (getRange form)) :: tail
        | [] -> failwithf $"Unexpected #' at end of form at %s{Lexer.formatPos q.Range}"
    | node :: rest -> node :: collapseSynQuote rest
    | [] -> []

/// A bracket as the source writes it, for the reader's diagnostics.
let private bracketText (t: Lexer.Token) =
    match t with
    | LParen -> "("
    | RParen -> ")"
    | LBracket -> "["
    | RBracket -> "]"
    | LBrace -> "{"
    | RBrace -> "}"
    | _ -> "?"

/// The bracket that closes `opener`.
let private closerOf (opener: Lexer.Token) =
    match opener with
    | LBracket -> RBracket
    | LBrace -> RBrace
    | _ -> RParen

/// Reads the forms in `tokens` up to the bracket that closes `opener`, and
/// returns them with the tokens from that bracket on. With no opener it reads
/// to the end.
///
/// Every bracket has to be closed, by its own kind, and nothing may be closed
/// that was not opened. Each of those is an error rather than something to
/// make the best of, because what the reader would otherwise do is guess: the
/// end of the file closed every open form, any closing bracket closed any
/// opening one, and a stray closing bracket at the top ended the file, so
/// that the forms after it were silently not compiled at all.
let rec private readIn (opener: LexedToken option) (tokens: LexedToken list) : SExpr list * LexedToken list =
    let isDot = function SAtom { Token = Dot } -> true | _ -> false

    /// Reads the body of a bracketed form, up to its closing bracket, and puts
    /// whatever `undotted` says at the head.
    ///
    /// With `dotMakesTuple`, a dot anywhere in the body makes the form a tuple
    /// regardless of how it was introduced. Only `(...)` says so: inside `[...]`
    /// and `{...}` a dot is an ordinary element, because a vec literal and a
    /// comprehension are not spellings of a tuple.
    ///
    /// `bracket` opens the form and `rangeFrom` opens the range the result
    /// spans — they differ for a quoted list, where the quote comes first.
    let readForm
        (dotMakesTuple: bool)
        (bracket: LexedToken)
        (rangeFrom: Lexer.Range)
        (undotted: SExpr list -> SExpr list)
        rest
        =
        let startRange = bracket.Range

        // The tokens after the form begin with its closing bracket, which is
        // where its range ends.
        let endRange, afterList =
            match readIn (Some bracket) rest with
            | innerNodes, closer :: after -> closer.Range, (innerNodes, after)
            | innerNodes, [] -> startRange, (innerNodes, [])

        let innerNodes, afterList = afterList
        let listRange = unionLexerRanges rangeFrom endRange

        let finalNodes =
            if dotMakesTuple && List.exists isDot innerNodes then
                let tupleToken = { Token = Lexer.Symbol "Tuple"; Range = startRange }
                SAtom tupleToken :: List.filter (not << isDot) innerNodes
            else
                undotted innerNodes

        SList(finalNodes, listRange), afterList

    /// Prepends a reserved head — `vec-literal`, `array-literal`,
    /// `comprehension` — to a body.
    let headed (name: string) (r: Lexer.Range) (innerNodes: SExpr list) =
        SAtom { Token = Lexer.Symbol name; Range = r } :: innerNodes

    // Every level ends at one of these four, so collapsing here covers all of
    // them and nothing else has to know about `#'`.
    let rec loop acc remaining =
        match remaining with
        | [] ->
            match opener with
            | None -> collapseSynQuote (List.rev acc), []
            | Some o ->
                failwithf
                    $"Syntax error at %s{Lexer.formatPos o.Range}: the '%s{bracketText o.Token}' opened here is never closed. The file ends first."
        | ({ Token = (RParen | RBracket | RBrace) } as closer) :: rest ->
            match opener with
            // The closer is handed back with the rest, for `readForm` to end
            // the form's range at.
            | Some o when closerOf o.Token = closer.Token -> collapseSynQuote (List.rev acc), closer :: rest
            | Some o ->
                failwithf
                    $"Syntax error at %s{Lexer.formatPos o.Range}: the '%s{bracketText o.Token}' opened here is closed by the '%s{bracketText closer.Token}' at %s{Lexer.formatPos closer.Range}. A form opened with '%s{bracketText o.Token}' is closed with '%s{bracketText (closerOf o.Token)}'."
            | None ->
                failwithf
                    $"Syntax error at %s{Lexer.formatPos closer.Range}: this '%s{bracketText closer.Token}' closes nothing. Every bracket before it is already closed."

        // Quoted list: '(items...) → (quoted-list items...)
        | { Token = Quote; Range = qr } :: ({ Token = LParen } as bracket) :: rest ->
            let node, afterList = readForm true bracket qr (headed "quoted-list" qr) rest
            loop (node :: acc) afterList

        // Function shorthand: #(+ &1 &2 5) → (fun (&1 &2) (+ &1 &2 5))
        //
        // `&` is `&1`, for the one-argument case that has no number to tell
        // apart: #(+ & 5).
        | { Token = Hash; Range = hr } :: ({ Token = LParen } as bracket) :: rest ->
            let rawBody, afterList = readForm true bracket hr id rest
            let bodySList = expandBareArg rawBody
            let argIndices = collectPositionalArgs bodySList
            let maxArg = if Set.isEmpty argIndices then 0 else Set.maxElement argIndices
            let paramNames = [ for i in 1 .. maxArg -> $"&{i}" ]
            let funToken = SAtom { Token = Lexer.Symbol "fun"; Range = hr }
            let paramList = SList(paramNames |> List.map (fun p -> SAtom { Token = Lexer.Symbol p; Range = hr }), hr)
            let lambdaSExpr = SList([ funToken; paramList; bodySList ], getRange bodySList)
            loop (lambdaSExpr :: acc) afterList

        // Array literal: #[items...] → (array-literal items...)
        //
        // The range starts at the `#`, so a diagnostic underlines the literal
        // and not the bracket half of it.
        | { Token = Hash; Range = hr } :: ({ Token = LBracket } as bracket) :: rest ->
            let node, afterList = readForm false bracket hr (headed "array-literal" hr) rest
            loop (node :: acc) afterList

        // Hash macro call: #name(args...) or #name[args...] → (#name args...)
        //
        // The lexer only makes a `#name` symbol when a bracket follows it, so
        // the second token is always one of these two. A dot directly among
        // the arguments is handed to the macro as written rather than making
        // the form a tuple, which would lose the head; an argument that is
        // itself a `(k . v)` gets the tuple rule from its own parentheses.
        | { Token = Lexer.Symbol name; Range = hr } :: ({ Token = (LParen | LBracket) } as bracket) :: rest when
            name.StartsWith "#"
            ->
            let node, afterList = readForm false bracket hr (headed name hr) rest
            loop (node :: acc) afterList

        | ({ Token = LParen; Range = r } as bracket) :: rest ->
            let node, afterList = readForm true bracket r id rest
            loop (node :: acc) afterList

        // Comprehension: {collector expr clause...} → (comprehension collector expr clause...)
        //
        // Read as an ordinary list under a reserved head, exactly as a vec
        // literal is; the parser is where it becomes a loop.
        | ({ Token = LBrace; Range = r } as bracket) :: rest ->
            let node, afterList = readForm false bracket r (headed "comprehension" r) rest
            loop (node :: acc) afterList

        // Vec literal: [items...] → (vec-literal items...)
        | ({ Token = LBracket; Range = r } as bracket) :: rest ->
            let node, afterList = readForm false bracket r (headed "vec-literal" r) rest
            loop (node :: acc) afterList

        | token :: rest -> loop (SAtom token :: acc) rest

    loop [] tokens

/// Reads every form in `tokens`. The second half of the result is always empty:
/// a bracket left open or closed without being opened is an error, so the
/// reader never stops short of the end. It is kept for the callers that take
/// the pair apart.
let read (tokens: LexedToken list) : SExpr list * LexedToken list = readIn None tokens

/// Splices the top-level forms of other files in at the position of each
/// `(include "path")`.
///
/// Unlike `import`, an include produces no module of its own: the forms become
/// part of the including file, exactly as if they had been typed there. That is
/// what makes it usable for splitting one module across files — the included
/// definitions are in scope without needing to be exported, and there is no
/// second module for the code generator to reach.
///
/// Paths resolve relative to the directory of the file doing the including, so
/// a chain of includes follows the files rather than the process's working
/// directory.
/// Returns the expanded forms and every file they came from, `filePath`
/// included. The second half is what staleness is judged against: an included
/// file is part of the module's source but is not the file the `.dll` is named
/// after, so editing one has to force a rebuild just as editing the module
/// itself does.
let rec private expandIncludes
    (activeFiles: string list)
    (filePath: string)
    (forms: SExpr list)
    : SExpr list * Set<string> =
    let includedFrom (r: Lexer.Range) = Lexer.formatPos r
    let mutable visited = Set.singleton filePath

    let expanded =
        forms
        |> List.collect (fun form ->
            match form with
            | SList([ SAtom { Token = Lexer.Symbol "include" }; SAtom { Token = Lexer.StringLit rel } ], r) ->
                let target =
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(filePath: string), rel))

                if List.contains target activeFiles then
                    let chain =
                        (List.rev (target :: activeFiles))
                        |> List.map Path.GetFileName
                        |> String.concat " -> "

                    failwithf
                        "Include Error: '%s' includes itself at %s. Include chain: %s"
                        (Path.GetFileName target)
                        (includedFrom r)
                        chain

                if not (File.Exists target) then
                    failwithf
                        "Include Error: cannot find '%s' included at %s (looked for %s)"
                        rel
                        (includedFrom r)
                        target

                // An included file is the including module's code, so it is
                // under the rule an import is.
                if not TypeEnv.compilingReplEntry then
                    match Paths.importRefusal filePath target with
                    | Some why -> failwithf $"Include Error at %s{includedFrom r}: %s{why}"
                    | None -> ()

                let source = Paths.readSource target
                let innerForms, _ = Lexer.tokenize target source |> read
                let inner, innerVisited = expandIncludes (target :: activeFiles) target innerForms
                visited <- Set.union visited innerVisited
                inner

            | SList(SAtom { Token = Lexer.Symbol "include" } :: _, r) ->
                failwithf
                    "Include Error: malformed include at %s. Expected (include \"path\")"
                    (includedFrom r)

            // `(:doc #:file "x.bjodoc")`: a file of docs, spliced as an
            // include is, so that it is one of the module's sources and a
            // change to it rebuilds the module.
            | form when (Docs.fileForm form).IsSome ->
                let rel, r = (Docs.fileForm form).Value
                let target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(filePath: string), rel))

                if not (File.Exists target) then
                    failwithf
                        "Syntax error in (:doc #:file ...) at %s: cannot find '%s' (looked for %s)."
                        (includedFrom r)
                        rel
                        target

                let innerForms, _ = Lexer.tokenize target (Paths.readSource target) |> read

                for inner in innerForms do
                    if not (Docs.isDocForm inner) || (Docs.fileForm inner).IsSome then
                        failwithf
                            "Syntax error at %s: a .bjodoc file holds (:doc name ...) and (:doc #:module ...) forms, and nothing else."
                            (includedFrom (getRange inner))

                visited <- Set.add target visited
                innerForms

            | other -> [ other ])

    expanded, visited

/// The tokens of text an assembly's metadata holds, `source` being the
/// assembly: inline templates, constrained bodies and declarations.
///
/// Read once per process. A batch compiles hundreds of files that each import
/// the prelude, whose metadata has some eight hundred inline templates, and
/// lexing them again for every file was most of what an error test cost. The
/// tokens are kept and not what is parsed from them: parsing invents names
/// (`Gensym.fresh`), and a compilation's invented names must not depend on what
/// the process compiled before it. Lexing has one effect on the session, the
/// names it reserves, and a cached read reserves them again.
let private metadataTokens =
    System.Collections.Concurrent.ConcurrentDictionary<struct (string * string), Lexer.LexedToken list>()

let tokenizeMetadata (source: string) (text: string) : Lexer.LexedToken list =
    let mutable tokens = []

    if metadataTokens.TryGetValue(struct (source, text), &tokens) then
        for t in tokens do
            match t.Token with
            | Symbol s -> Gensym.reserve s
            | _ -> ()

        tokens
    else
        let tokens = Lexer.tokenize source text
        metadataTokens[struct (source, text)] <- tokens
        tokens

/// `f` run with the macros as they were at `at`, and then as they are again.
///
/// A body read back from metadata is read when it is first used, and reading
/// it consults the macro tables: what it holds was expanded where it was
/// written, but a pattern such as `(Nil)` is a call to a pattern macro if one
/// is called that. It is read with the tables as they were when its assembly
/// was imported, as it was when everything was read then, and not with the
/// macros of whatever was imported after it.
let private withMacrosOf (at: Macro.State) (f: unit -> 'a) : 'a =
    let now = Macro.snapshot ()
    Macro.restore { at with Expansions = now.Expansions }

    try
        f ()
    finally
        Macro.restore now

/// Reads one published constrained body back.
///
/// Not a declaration, unlike an inline template's: nothing is registered under
/// a trait and nothing is checked. `Monomorphise` reads the map directly, and
/// only if some call site asks for a copy.
let private constrainedBodyTemplate
    (macros: Macro.State)
    (source: string)
    (entry: ModuleMetadata.ConstrainedBodyEntry)
    : TypedAST.InlineTemplate =
    let tokens = tokenizeMetadata source entry.Body

    let parse () =
        match read tokens |> fst with
        | [ form ] -> LetRecify.letrecifyExpr (Parser.parseExpr (readOrigins entry.OriginModule form))
        | _ -> failwithf $"Malformed constrained body in metadata for '%s{entry.Name}'."

    let body = lazy (withMacrosOf macros parse)

    { Params = entry.Params
      Body = body
      Qualification = Map.ofList entry.Qualification
      OriginModule = entry.OriginModule
      Inline = entry.Inline
      Imported = true }

/// Reads one metadata entry back into a declaration.
///
/// Only the body needs parsing: everything else was a typed field, and the
/// parameter names, the untyped body and the qualification map stay three
/// separate things exactly as they were written.
///
/// The body is parsed when it is first spliced. The prelude publishes some
/// eight hundred templates and a module uses a few, and parsing them all for
/// every module that imports it was most of what compiling a small one cost.
/// It is lexed now, though: lexing reserves the names of `Gensym.fresh`'s shape
/// that the body has, which a name this compilation invents must not be, from
/// the start of it.
let private inlineImplDecl
    (macros: Macro.State)
    (source: string)
    (entry: ModuleMetadata.InlineTemplateEntry)
    : Decl =
    let tokens = tokenizeMetadata source entry.Body

    let range =
        match tokens with
        | [] -> failwithf $"Empty inline template body in metadata for '%s{entry.TraitName}.%s{entry.MethodName}'."
        | first :: _ -> { first.Range with End = (List.last tokens).Range.End }

    let parse () =
        match read tokens |> fst with
        // The reader joins consecutive `defun`s into one group. `SeqFusion`
        // recognizes a loop only in the split shape `LetRecify` gives it.
        | [ form ] -> LetRecify.letrecifyExpr (Parser.parseExpr (readOrigins entry.OriginModule form))
        | _ ->
            failwithf $"Malformed inline template body in metadata for '%s{entry.TraitName}.%s{entry.MethodName}'."

    let body = lazy (withMacrosOf macros parse)

    DInlineImpl(
        entry.TraitName,
        entry.MethodName,
        entry.ImplKey,
        entry.OriginModule,
        entry.Params,
        body,
        entry.Qualification,
        range
    )

// ---------------------------------------------------------------------------
// Applying an import's modifiers
// ---------------------------------------------------------------------------

/// The prefix a foreign alias named by an exported body is published under.
///
/// Neither filtered nor renamed. A spliced template refers to it by the name it
/// was published as, wherever the splice lands, so an edge's modifiers must not
/// reach it — the name is not one the importing module ever writes.
let private publishedAliasPrefix = "clr_import__"

/// What a dependency offers, by kind.
///
/// `only`, `except` and `rename` are refused on everything but the defs, so
/// what a name *is* has to be answerable before any of them is applied.
type private ImportSurface =
    { /// Exported bindings, foreign aliases and macros — the names a modifier
      /// may filter or rename.
      Defs: Set<string>
      /// The `def/hash-extend`s, by bare name. Their own set because `#fl`
      /// is its own namespace: `only`, `except` and `rename` are about the
      /// defs and do not see these, and a `prefix` reaches them as it reaches
      /// everything.
      HashMacros: Set<string>
      /// The spelling a type is written under here, and the key it resolves
      /// to.
      ///
      /// The key is carried rather than rebuilt from the module name, because
      /// a module publishes two kinds: the ones it declared, keyed by itself,
      /// and the ones it re-exported, keyed by whoever declared them. The
      /// second kind has no derivation from this module's name at all.
      Types: Map<string, string>
      /// The same, for the constructors, as pairs: two unions may each have a
      /// case of one name. A record is built by its own name, so its type name
      /// is in here too.
      Constructors: (string * string) list
      /// A constructor's key -> its type's key. A constructor follows its
      /// type, so `(only m Figure)` keeps `Figure`'s cases and no others.
      TypeOfCase: Map<string, string>
      /// The name source writes for each trait -> its key
      /// (`Naming.traitKey`).
      Traits: Map<string, string>
      /// The name source writes for each method -> its key, and the key of
      /// the trait that declares it.
      TraitMethods: Map<string, string * string> }

/// `moduleName` is the dependency's own, and is what a type name is read back
/// through: a declaration is keyed by the module that wrote it, and what an
/// importer spells — with or without a modifier — is the bare name inside that
/// key.
let private surfaceOf
    (moduleName: string)
    (decls: Decl list)
    (carried: Decl list)
    (macros: ModuleMetadata.MacroEntry list)
    (patternMacros: ModuleMetadata.MacroEntry list)
    (hashMacros: ModuleMetadata.MacroEntry list)
    : ImportSurface =
    let bare = Naming.bareTypeName moduleName

    let rec typeDefsIn (ds: Decl list) =
        ds
        |> List.collect (function
            | DType(tds, _)
            | DTypeRec(tds, _) -> tds
            | DModule(_, inner, _) -> typeDefsIn inner
            | _ -> [])

    let typeDefs =
        decls
        |> List.collect (function
            | DType(tds, _)
            | DTypeRec(tds, _) -> tds
            | _ -> [])

    // Over what the module carries too: a re-exported type's declaration is
    // there, and its cases are offered beside it.
    let typeOfCase =
        typeDefsIn (decls @ carried)
        |> List.collect (fun td ->
            match td.Kind with
            | Union cases ->
                cases
                |> List.map (function
                    | SimpleCase(n, _) -> n, td.Name
                    | DataCase(n, _, _) -> n, td.Name)
            | Record _
            | Newtype _ -> [ td.Name, td.Name ]
            | Alias _ -> [])
        |> Map.ofList

    { Defs =
        (decls
         |> List.collect (function
             // An implicit export, declared under its qualified reference: not
             // a name an import edge offers, or that a modifier may filter.
             | DExtern(visible, _, _, _, _) when Naming.isQualifiedBinding visible -> []
             | DExtern(visible, _, _, _, _) -> [ visible ]
             | DImportExtern(specs, _) ->
                 specs
                 |> List.map (fun s -> s.Alias)
                 |> List.filter (fun a -> not (a.StartsWith publishedAliasPrefix))
             | _ -> []))
        @ (macros |> List.map (fun m -> m.Name))
        @ (patternMacros |> List.map (fun m -> m.Name))
        |> Set.ofList

      HashMacros = hashMacros |> List.map (fun m -> m.Name) |> Set.ofList

      Types =
        (typeDefs |> List.map (fun td -> bare td.Name, td.Name))
        // What a re-export publishes is a spelling for somebody else's
        // declaration, and the spelling arrives as the alias it is. Nothing
        // has to be derived from it: the alias already holds both halves.
        @ (decls
           |> List.choose (function
               | DImportAlias(visible, keyed, AliasType, _) -> Some(visible, keyed)
               | _ -> None))
        |> Map.ofList

      Constructors =
        (typeDefs
         |> List.collect (fun td ->
             match td.Kind with
             | Union cases ->
                 cases
                 |> List.map (function
                     | SimpleCase(n, _) -> bare n, n
                     | DataCase(n, _, _) -> bare n, n)
             // A record and a newtype are constructed by their own name.
             | Record _
             | Newtype _ -> [ bare td.Name, td.Name ]
             | Alias _ -> []))
        @ (decls
           |> List.choose (function
               | DImportAlias(visible, keyed, AliasConstructor, _) -> Some(visible, keyed)
               | _ -> None))
        |> List.distinct

      TypeOfCase = typeOfCase

      // A trait declaration in metadata is named by its key, and its methods
      // by the names source wrote.
      Traits =
        decls
        |> List.choose (function
            | DTrait(key, _, _, _, _, _, _, _, _) -> Some(Naming.bareTraitName key, key)
            | _ -> None)
        |> Map.ofList

      TraitMethods =
        decls
        |> List.collect (function
            | DTrait(traitKey, _, _, _, signatures, _, _, _, _) ->
                signatures |> List.map (fun (m, _, _) -> m, (Naming.traitMethodKey traitKey m, traitKey))
            | _ -> [])
        |> Map.ofList }

/// What each of a dependency's defs and macros is called after one import
/// edge's modifiers. A name absent from the result was filtered out.
///
/// Keyed on the original name throughout, because that is what the declarations
/// and the macro entries are keyed on; the value is the spelling this edge
/// produces. Modifiers are applied in the order they nest, innermost first, so
/// `(prefix (rename (m) (a b)) "p/")` yields `p/b`.
let private defRenaming
    (r: Lexer.Range)
    (moduleLabel: string)
    (surface: ImportSurface)
    (modifiers: ImportModifier list)
    : Map<string, string> =

    let where = Lexer.formatPos r

    /// Refuses a name `only`, `except` or `rename` may not touch, and a name
    /// the dependency does not offer at all.
    let checkTouchable (form: string) (visible: Map<string, string>) (name: string) : unit =
        let advice =
            if form = "rename" then
                $"(rename ...) applies to defs and macros. Write (prefix-types ...) to rename a module's types, constructors and traits together, or a local (type (: new-name %s{name})) for one of them."
            else
                "A trait always arrives with its module, so (only ...) and (except ...) do not name one."

        let refuse (what: string) : unit =
            failwithf $"Invalid (%s{form} ...) at %s{where}: '%s{name}' %s{what}. %s{advice}"

        // A type may be named by `only` and `except`: they decide whether its
        // bare name arrives (see `keptTypes`). A constructor follows its type
        // and is not named on its own.
        let filtersTypes = form = "only" || form = "except"

        if Map.containsKey name surface.Types && filtersTypes then ()
        elif Map.containsKey name surface.Types then refuse "is a type"
        elif List.exists (fst >> (=) name) surface.Constructors && filtersTypes then
            let owner =
                surface.Constructors
                |> List.tryPick (fun (n, key) -> if n = name then Map.tryFind key surface.TypeOfCase else None)
                |> Option.bind (fun key ->
                    surface.Types |> Map.tryFindKey (fun _ k -> k = key))

            match owner with
            | Some typeName ->
                failwithf
                    $"Invalid (%s{form} ...) at %s{where}: '%s{name}' is a constructor of '%s{typeName}', and a constructor follows its type. Name '%s{typeName}' in (%s{form} ...) to have or leave out the type and all its constructors."
            | None -> refuse "is a constructor"
        elif List.exists (fst >> (=) name) surface.Constructors then refuse "is a constructor"
        elif Map.containsKey name surface.Traits then refuse "is a trait"
        else
            match Map.tryFind name surface.TraitMethods with
            | Some(_, ownerKey) ->
                let owner = Naming.showTraitName ownerKey

                failwithf
                    $"Invalid (%s{form} ...) at %s{where}: '%s{name}' is a method of the trait '%s{owner}'. A trait method is dispatched under the name it was declared with, so an individual one cannot be remapped — prefix the module that declares the trait instead, which renames its methods together and still dispatches."
            | None ->
                if not (visible |> Map.exists (fun _ v -> v = name)) then
                    failwithf
                        $"Invalid (%s{form} ...) at %s{where}: '%s{name}' is not exported by %s{moduleLabel}."

    let step (visible: Map<string, string>) (m: ImportModifier) : Map<string, string> =
        match m with
        | Only names ->
            for n in names do
                checkTouchable "only" visible n

            let keep = Set.ofList names
            visible |> Map.filter (fun _ v -> Set.contains v keep)

        | Except names ->
            for n in names do
                checkTouchable "except" visible n

            let drop = Set.ofList names
            visible |> Map.filter (fun _ v -> not (Set.contains v drop))

        | Prefix a
        | PrefixDefs a -> visible |> Map.map (fun _ v -> a + v)

        | Postfix a
        | PostfixDefs a -> visible |> Map.map (fun _ v -> v + a)

        | Rename pairs ->
            for (oldName, _) in pairs do
                checkTouchable "rename" visible oldName

            let byOld = Map.ofList pairs

            visible
            |> Map.map (fun _ v ->
                match Map.tryFind v byOld with
                | Some renamed -> renamed
                | None -> v)

        // Types are a spelling of their own, and are renamed where they are
        // resolved rather than by rewriting the declarations that carry them.
        | PrefixTypes _
        | PostfixTypes _ -> visible

    surface.Defs
    |> Set.toList
    |> List.map (fun n -> n, n)
    |> Map.ofList
    |> fun start -> List.fold step start modifiers

/// What each of a dependency's hash macros is called after one import edge's
/// modifiers, keyed and valued on the bare name: `fl` -> `s/fl` for
/// `(prefix (m) "s/")`, and then `#s/fl(...)` is the call.
///
/// Only `prefix` and `postfix` apply. The three that name things — `only`,
/// `except`, `rename` — name defs, and a hash macro is not one; so every hash
/// macro always arrives, under whatever spelling the affixes give it.
let private hashRenaming (surface: ImportSurface) (modifiers: ImportModifier list) : Map<string, string> =
    let step (visible: Map<string, string>) (m: ImportModifier) =
        match m with
        | Prefix a
        | PrefixDefs a -> visible |> Map.map (fun _ v -> a + v)
        | Postfix a
        | PostfixDefs a -> visible |> Map.map (fun _ v -> v + a)
        | _ -> visible

    surface.HashMacros
    |> Set.toList
    |> List.map (fun n -> n, n)
    |> Map.ofList
    |> fun start -> List.fold step start modifiers

/// The extra spellings one import edge gives a dependency's types,
/// constructors, traits and trait methods.
///
/// Additional spellings rather than replacements, which is the difference from
/// `defRenaming`. A type declaration keeps its own name because every registry
/// — implementations, inline templates, associated types, union cases — is
/// keyed on it, and an impl travels unconditionally (rule 2), so renaming the
/// key would leave the impls filed under a name nothing looks up. The prefixed
/// name is resolved back to the original before any of those is consulted.
///
/// What it is resolved back *to* is the key: a type's identity is the module
/// that declared it plus its name, so `(prefix-types "a.bjo" "A/")` beside
/// `(prefix-types "b.bjo" "B/")` gives two spellings of two different types
/// rather than two spellings of one.
///
/// The key comes from the surface rather than being rebuilt here. It used to be
/// `Naming.typeKey moduleName original`, which is right for a type the module
/// declared and wrong for one it re-exported — those carry the key of whoever
/// declared them, and there is nothing in this module's name to derive it from.
///
/// `only`, `except` and `rename` contribute nothing here: they are refused on
/// these names outright, and a type always arrives.
let private typeRenaming
    (surface: ImportSurface)
    (modifiers: ImportModifier list)
    : (string * string * AliasKind) list =

    // (key, the name source writes, the spelling this edge gives it, kind).
    // The middle one is only here to answer "did a modifier change anything",
    // which is what decides whether a spelling is produced at all.
    let start =
        (surface.Types |> Map.toList |> List.map (fun (n, key) -> key, n, n, AliasType))
        @ (surface.Constructors |> List.map (fun (n, key) -> key, n, n, AliasConstructor))
        @ (surface.Traits |> Map.toList |> List.map (fun (n, key) -> key, n, n, AliasTrait))
        // A method is prefixed with the trait it belongs to. Dispatch resolves
        // through the original, so a systematic prefix costs nothing — which is
        // the reason rule 4 refuses an individual rename and points here.
        @ (surface.TraitMethods |> Map.toList |> List.map (fun (m, (key, _)) -> key, m, m, AliasMethod))

    // The type's own name, for a type and for one of its constructors: what
    // `only` and `except` name.
    let typeNameOf (key: string) (kind: AliasKind) =
        let typeKey =
            match kind with
            | AliasConstructor -> Map.tryFind key surface.TypeOfCase
            | _ -> Some key

        typeKey
        |> Option.bind (fun tk -> surface.Types |> Map.tryFindKey (fun _ k -> k = tk))

    let named (names: string list) (key: string, _: string, _: string, kind: AliasKind) =
        match kind with
        | AliasType
        | AliasConstructor ->
            match typeNameOf key kind with
            | Some n -> Some(List.contains n names)
            | None -> None
        | _ -> None

    let step (visible: (string * string * string * AliasKind) list) (m: ImportModifier) =
        match m with
        | Prefix a
        | PrefixTypes a -> visible |> List.map (fun (key, o, v, k) -> key, o, a + v, k)
        | Postfix a
        | PostfixTypes a -> visible |> List.map (fun (key, o, v, k) -> key, o, v + a, k)
        | Only names -> visible |> List.filter (fun e -> named names e |> Option.defaultValue true)
        | Except names -> visible |> List.filter (fun e -> named names e |> Option.map not |> Option.defaultValue true)
        | _ -> visible

    List.fold step start modifiers
    |> List.filter (fun (_, original, visible, _) -> original <> visible)
    // A record's name is both its type and its constructor, so it arrives
    // twice, once in each namespace.
    |> List.distinctBy (fun (key, _, visible, kind) -> key, visible, kind)
    |> List.map (fun (key, _, visible, kind) -> key, visible, kind)

/// The types one import edge offers, as keys, after its `only` and `except`.
/// A spelling of a type, bare or prefixed, arrives only for these, and a
/// constructor only with its type.
let private offeredTypes (surface: ImportSurface) (modifiers: ImportModifier list) : Set<string> =
    let keyOf (n: string) = Map.tryFind n surface.Types

    modifiers
    |> List.fold
        (fun (kept: Set<string>) m ->
            match m with
            | Only names -> names |> List.choose keyOf |> Set.ofList |> Set.intersect kept
            | Except names -> Set.difference kept (names |> List.choose keyOf |> Set.ofList)
            | _ -> kept)
        (surface.Types |> Map.toSeq |> Seq.map snd |> Set.ofSeq)

/// Whether an import edge spells the module's types and traits its own way,
/// which replaces their bare names.
let private respells (modifiers: ImportModifier list) : bool =
    modifiers
    |> List.exists (function
        | Prefix _
        | Postfix _
        | PrefixTypes _
        | PostfixTypes _ -> true
        | _ -> false)

/// The types, as keys, whose bare names one import edge keeps.
///
/// A modifier that spells the types differently replaces the bare name, as a
/// prefix replaces a def's name. `(prefix-types m "T/")` gives `T/Crate`, and
/// `Crate` is then no name for it, unless another import of `m` keeps it. A
/// prefix that left the bare name in place did not keep the type out of the
/// way: the bare name still took part in "the later import wins", so a later
/// prefixed import took the bare name from an earlier plain one.
///
/// `only` and `except` decide which types there are to keep (`offeredTypes`).
/// A type a module does not name does not crowd its scope: an imported
/// signature names a type by key, and so does a published body, so neither
/// needs the bare name.
let private keptTypes (surface: ImportSurface) (modifiers: ImportModifier list) : Set<string> =
    if respells modifiers then Set.empty else offeredTypes surface modifiers

/// The keys whose bare spellings one import edge keeps: the kept types, and
/// the constructors of those types. A constructor whose type is not known
/// here goes with the edge's types as a whole.
///
/// And the traits and their methods, unless the edge spells them its own
/// way. A trait always arrives with its module, so `only` and `except` do not
/// leave one out.
let private keptSpellings (surface: ImportSurface) (modifiers: ImportModifier list) : Set<string> =
    let kept = keptTypes surface modifiers
    let anyKept = not kept.IsEmpty

    let traits =
        if respells modifiers then
            Set.empty
        else
            Seq.append
                (surface.Traits |> Map.toSeq |> Seq.map snd)
                (surface.TraitMethods |> Map.toSeq |> Seq.map (snd >> fst))
            |> Set.ofSeq

    let constructors =
        surface.Constructors
        |> Seq.map snd
        |> Seq.filter (fun key ->
            match Map.tryFind key surface.TypeOfCase with
            | Some owner -> Set.contains owner kept
            | None -> anyKept)
        |> Set.ofSeq

    Set.unionMany [ kept; constructors; traits ]

/// What one import edge offers its importer, as `(namespace, the name the
/// importer sees, what the name stands for, the module that offers it)`.
///
/// "What it stands for" is the thing, not the name: a def's origin module and
/// name, so that a facade and the module it re-exports offer one thing; a
/// foreign alias's .NET member; a type's or constructor's key. Two offers of a
/// name are an ambiguity only when these differ.
let private offeredNames
    (moduleName: string)
    (decls: Decl list)
    (surface: ImportSurface)
    (renaming: Map<string, string>)
    (modifiers: ImportModifier list)
    : (Diagnostics.NameSpace * string * string * string * bool) list =
    let provider = Naming.moduleNameOfPath moduleName

    let origins =
        decls
        |> List.collect (function
            | DExtern(visible, origin, _, _, _) ->
                let home = if origin.OriginModule = "" then moduleName else origin.OriginModule
                [ visible, home + "::" + origin.OriginalName ]
            | DImportExtern(specs, _) -> specs |> List.map (fun s -> s.Alias, "clr:" + s.ClrTarget)
            | _ -> [])
        |> Map.ofList

    let defs =
        renaming
        |> Map.toList
        |> List.map (fun (original, visible) ->
            let thing = Map.tryFind original origins |> Option.defaultValue (moduleName + "::" + original)
            Diagnostics.ValueName, visible, thing, provider, false)

    let kept = keptSpellings surface modifiers

    let bare =
        (surface.Types
         |> Map.toList
         |> List.filter (fun (_, key) -> Set.contains key kept)
         |> List.map (fun (n, key) -> Diagnostics.TypeName, n, key, provider, true))
        @ (surface.Constructors
           |> List.filter (fun (_, key) -> Set.contains key kept)
           |> List.map (fun (n, key) -> Diagnostics.ValueName, n, key, provider, true))
        // A trait and its methods, which arrive with the module.
        @ (surface.Traits
           |> Map.toList
           |> List.filter (fun (_, key) -> Set.contains key kept)
           |> List.map (fun (n, key) -> Diagnostics.TraitName, n, key, provider, true))
        @ (surface.TraitMethods
           |> Map.toList
           |> List.filter (fun (_, (key, _)) -> Set.contains key kept)
           |> List.map (fun (n, (key, _)) -> Diagnostics.ValueName, n, key, provider, true))

    let spelled =
        typeRenaming surface modifiers
        |> List.choose (fun (key, visible, kind) ->
            match kind with
            | AliasType -> Some(Diagnostics.TypeName, visible, key, provider, true)
            | AliasConstructor
            | AliasMethod -> Some(Diagnostics.ValueName, visible, key, provider, true)
            | AliasTrait -> Some(Diagnostics.TraitName, visible, key, provider, true)
            | AliasDef
            | AliasMacro -> None)

    defs @ bare @ spelled

/// The bare type and constructor names an `only` or an `except` left out of
/// every import of their module, with the module and, for a constructor, the
/// name of its type. A module some import spells its own way is left to
/// `hiddenSpellingNote`, which names that spelling.
let private withheldNames
    (edges: (string * ImportSurface * ImportModifier list) seq)
    : Map<string, string * string option> =
    edges
    |> Seq.groupBy (fun (moduleName, _, _) -> moduleName)
    |> Seq.collect (fun (moduleName, group) ->
        let group = List.ofSeq group
        let respelled = group |> List.exists (fun (_, s, ms) -> (keptTypes s ms).IsEmpty && not (offeredTypes s ms).IsEmpty)

        if respelled then
            Seq.empty
        else
            let (_, surface, _) = List.head group
            let kept = group |> List.map (fun (_, s, ms) -> keptSpellings s ms) |> Set.unionMany
            let provider = Naming.moduleNameOfPath moduleName
            let typeName (key: string) = surface.Types |> Map.tryFindKey (fun _ k -> k = key)

            let types =
                surface.Types
                |> Map.toSeq
                |> Seq.filter (fun (_, key) -> not (Set.contains key kept))
                |> Seq.map (fun (n, _) -> n, (provider, None))

            let constructors =
                surface.Constructors
                |> Seq.filter (fun (n, key) -> not (Set.contains key kept) && not (Map.containsKey n surface.Types))
                |> Seq.map (fun (n, key) ->
                    n, (provider, Map.tryFind key surface.TypeOfCase |> Option.bind typeName))

            Seq.append types constructors)
    |> Map.ofSeq

/// The names offered as two or more different things, who offers each, and
/// whether one of them is a type or a constructor.
let private ambiguousNames
    (offers: (Diagnostics.NameSpace * string * string * string * bool) seq)
    : Map<Diagnostics.NameSpace * string, string list * bool> =
    offers
    |> Seq.groupBy (fun (space, name, _, _, _) -> space, name)
    |> Seq.choose (fun (key, group) ->
        let things = group |> Seq.map (fun (_, _, thing, _, _) -> thing) |> Seq.distinct |> List.ofSeq

        // Cases of different unions are told apart by type where they are
        // used (`InferExpr.caseCandidates`), so they are no ambiguity here.
        let onlyCases = things |> List.forall (fun t -> (Naming.unionOfCaseKey t).IsSome)

        if things.Length > 1 && not onlyCases then
            let providers = group |> Seq.map (fun (_, _, _, provider, _) -> provider) |> Seq.distinct |> List.ofSeq
            Some(key, (providers, group |> Seq.exists (fun (_, _, _, _, withModule) -> withModule)))
        else
            None)
    |> Map.ofSeq

/// The names a module's own top-level declarations give, which win over an
/// import's (rule 1).
let private ownNames (decls: Decl list) : Set<string> =
    decls
    |> List.collect (function
        | DDef(n, _, _)
        | DDefMutable(n, _, _) -> [ n ]
        | DDefun(n, _, _, _, _) -> [ n ]
        | DDefDouble(n, _, _, _, _) -> [ n ]
        | DDefTuple(ns, _, _) -> ns
        | DDefPattern(pattern, _, _) -> patternBinders pattern
        | DType(tds, _)
        | DTypeRec(tds, _) ->
            tds
            |> List.collect (fun td ->
                td.Name
                :: (match td.Kind with
                    | Union cases ->
                        cases
                        |> List.map (function
                            | SimpleCase(n, _) -> n
                            | DataCase(n, _, _) -> n)
                    | _ -> []))
        | DTrait(n, _, _, _, signatures, _, _, _, _) -> n :: (signatures |> List.map (fun (m, _, _) -> m))
        | DAlias(visible, _, _) -> [ visible ]
        | DImportExtern(specs, _) -> specs |> List.map (fun s -> s.Alias)
        | DImportClass(specs, _) -> specs |> List.map (fun s -> s.Alias)
        | _ -> [])
    |> Set.ofList

/// A dependency's declarations as every edge that reaches it sees them.
///
/// The spellings are a list rather than one name, which is rule 10: a module
/// imported twice under different modifiers contributes both, and the
/// declaration is duplicated once per spelling.
///
/// A binding no edge keeps under the name it was published as is still bound,
/// under its qualified reference — `BjoMod.std.hashset_Module::seq->hashset` —
/// which source cannot write. A published body may name it: `list->hashset`
/// calls `seq->hashset`, and an importer that took only `list->hashset` still
/// has to be able to check a copy of it, or splice it. The body's
/// qualification map names exactly this spelling, and
/// `AlphaRename.reachHidden` points the body at it before the check.
let private applyDefRenaming (moduleName: string) (renaming: Map<string, string list>) (decls: Decl list) : Decl list =
    let visible (n: string) =
        match Map.tryFind n renaming with
        | Some names -> names
        | None -> []

    // A trait's methods are bound under their keys by the trait, whatever
    // an edge calls them, so a deprecation of one stays under its key.
    let methodKeys =
        decls
        |> List.collect (function
            | DTrait(key, _, _, _, signatures, _, _, _, _) ->
                signatures |> List.map (fun (m, _, _) -> Naming.traitMethodKey key m)
            | _ -> [])
        |> Set.ofList

    decls
    |> List.collect (fun d ->
        match d with
        | DDeprecated(name, _, _) when Set.contains name methodKeys -> [ d ]
        | DExtern(name, origin, t, constraints, r) ->
            let spellings = visible name

            let hidden =
                if List.contains name spellings then
                    []
                else
                    let home = if origin.OriginModule = "" then moduleName else origin.OriginModule
                    [ Naming.qualifiedBinding home origin.OriginalName ]

            spellings @ hidden |> List.map (fun v -> DExtern(v, origin, t, constraints, r))
        // Under each name an edge keeps. A binding no edge keeps is bound only
        // under its qualified reference, which source cannot write, so no use
        // of it can warn.
        | DDeprecated(name, instead, r) -> visible name |> List.map (fun v -> DDeprecated(v, instead, r))
        | DImportExtern(specs, r) ->
            let kept =
                specs
                |> List.collect (fun s ->
                    if s.Alias.StartsWith publishedAliasPrefix then
                        [ s ]
                    else
                        visible s.Alias |> List.map (fun v -> { s with Alias = v }))

            if kept.IsEmpty then [] else [ DImportExtern(kept, r) ]
        | other -> [ other ])

// ---------------------------------------------------------------------------
// Making a macro module's dependencies loadable
// ---------------------------------------------------------------------------

/// Every Bjolang assembly this compilation knows a path for, by assembly name.
let private assemblyPaths = System.Collections.Generic.Dictionary<string, string>()

/// Nyckeln är modulens assemblynamn, inte filnamnet: två `set.dll` i olika
/// kataloger är två poster.
let private noteAssemblyPath (path: string) =
    let name = Naming.assemblyName path

    if not (assemblyPaths.ContainsKey name) then
        assemblyPaths[name] <- path

/// The file a Bjolang assembly name was found at, if this process has read one
/// by that name. What a load context outside this module resolves a reference
/// between two modules with.
let assemblyPathOf (name: string) : string option =
    match assemblyPaths.TryGetValue name with
    | true, path -> Some path
    | _ -> None

/// The assembly a `.dll`'s metadata and macros are read from, by path, with the
/// timestamp of the file it was loaded from.
let private metadataAssemblies =
    System.Collections.Generic.Dictionary<string, int64 * System.Reflection.Assembly>()

/// Loads a `.dll` to read what it publishes, as the file is now.
///
/// `Assembly.LoadFile` answers every later call for the same path with the
/// assembly it loaded first, so a module rebuilt while the process runs — the
/// REPL rebuilds an imported module whose source was edited — would go on
/// being read as it was. A file whose timestamp has moved is therefore loaded
/// again from its bytes, into a context of its own, and that version is what
/// its exports, macros and inline bodies are read from. The first version of a
/// file still goes through `LoadFile`, which keeps its location for stack
/// traces out of a macro.
/// The loaded assembly of each module, by module key: where a grammar's check
/// is run from when a literal is compiled.
let private assembliesByModule =
    System.Collections.Concurrent.ConcurrentDictionary<string, System.Reflection.Assembly>()

/// Runs a grammar's check, a function of a compiled module, on an atom. See
/// `InferExpr.runGrammarCheck`.
let private runGrammarCheck (moduleKey: string) (name: string) (atom: obj) : Result<unit, string> =
    match assembliesByModule.TryGetValue moduleKey with
    | false, _ ->
        Error $"the module %s{moduleKey} that declares the check %s{name} is not loaded, so the check can not run"
    | true, asm ->
        let className = $"%s{Naming.namespaceOfKey moduleKey}.%s{Naming.moduleClassName moduleKey}"

        match asm.GetType className with
        | null -> Error $"the module %s{moduleKey} has no class %s{className}"
        | clrType ->
            match clrType.GetMethod(Prelude.moduleClrMemberName name) with
            | null ->
                let names = clrType.GetMethods() |> Array.map (fun m -> m.Name) |> String.concat ", "
                Error $"the module %s{moduleKey} has no method for the check %s{name} (%s{Prelude.moduleClrMemberName name}): %s{names}"
            | m ->
                let result =
                    try
                        Ok(m.Invoke(null, [| atom |]))
                    with :? System.Reflection.TargetInvocationException as ex when not (isNull ex.InnerException) ->
                        Error $"the check %s{name} failed: %s{ex.InnerException.Message}"

                match result with
                | Error e -> Error e
                | Ok null -> Error $"the check %s{name} gave no value"
                | Ok value ->
                    let t = value.GetType()
                    let isOk = t.GetField("IsOk").GetValue(value) :?> bool

                    if isOk then
                        Ok()
                    else
                        Error(string (t.GetField("ErrValue").GetValue(value)))

let private loadForMetadata (absPath: string) : System.Reflection.Assembly =
    InferExpr.runGrammarCheck <- runGrammarCheck
    let ticks = File.GetLastWriteTimeUtc(absPath).Ticks

    let asm =
        match metadataAssemblies.TryGetValue absPath with
        | true, (seen, asm) when seen = ticks -> asm
        | known, _ ->
            let asm =
                if known then
                    System.Reflection.Assembly.Load(File.ReadAllBytes absPath)
                else
                    System.Reflection.Assembly.LoadFile absPath

            metadataAssemblies[absPath] <- (ticks, asm)
            asm

    assembliesByModule[Naming.moduleKeyOfPath absPath] <- asm
    asm

/// A compiled module's assembly and the Bjolang metadata it carries.
let private metadataOf (absPath: string) : System.Reflection.Assembly * ModuleMetadata.Metadata =
    let asm = Timing.phase "import: load assembly" (fun () -> loadForMetadata absPath)
    let attr = asm.GetCustomAttributes(typeof<System.Reflection.AssemblyMetadataAttribute>, false)

    let metadataValue (key: string) : string option =
        attr
        |> Array.choose (fun a ->
            let meta = a :?> System.Reflection.AssemblyMetadataAttribute
            if meta.Key = key then Some meta.Value else None)
        |> Array.tryHead

    // An assembly with no Bjolang metadata at all exports nothing and is a
    // perfectly good thing to link — a hand-written C# library is exactly that.
    // One carrying the attributes an older compiler wrote is a different
    // thing, and saying so beats reporting every name it was meant to export
    // as unbound.
    let meta =
        match metadataValue "BjolangMetadata" with
        | Some text -> Timing.phase "import: read metadata" (fun () -> ModuleMetadata.deserialize absPath text)
        | None ->
            let legacyKeys = [ "BjolangExports"; "BjolangDeps"; "BjolangInlineImpls"; "BjolangMacros" ]

            if legacyKeys |> List.exists (metadataValue >> Option.isSome) then
                failwithf
                    $"'%s{absPath}' was built by an earlier version of the Bjolang compiler, whose metadata format this one does not read. Rebuild it with this compiler version."

            ModuleMetadata.empty

    asm, meta

let mutable private resolverInstalled = false

/// Lets a transformer call into the modules its own module was compiled against.
///
/// `Assembly.LoadFile` resolves the file it is handed and nothing else. A
/// transformer's own code is therefore reachable, and so is anything in the
/// runtime assemblies — `Program.main` loads those by name before any of this
/// runs — but the first call into another Bjolang module used to fail: the CLR
/// asks the default load context for `prelude`, which probes beside the
/// *compiler* and does not find it. One `(string-append a b)` in a transformer
/// was enough to produce "Could not load file or assembly 'prelude'" at the
/// macro's call site, which named neither the cause nor the macro.
///
/// A compiled program installs the same resolver for the same reason
/// (`Program.fs`): a Bjolang assembly does not sit next to whatever is running
/// it.
///
/// Uppslaget sker på assemblynamn, som är det enda CLR frågar med. En modul
/// bär sin katalog i sitt namn, så två `set.dll` svarar var för sig. Namn som
/// inte är en moduls — körtidens assemblies — söks i `Paths.runtimeDir`.
let private installAssemblyResolver () =
    if not resolverInstalled then
        resolverInstalled <- true

        System.AppDomain.CurrentDomain.add_AssemblyResolve (
            System.ResolveEventHandler(fun _ args ->
                let simple = System.Reflection.AssemblyName(args.Name).Name

                let path =
                    match assemblyPaths.TryGetValue simple with
                    | true, p -> Some p
                    | _ ->
                        let probe = Path.Combine(Paths.runtimeDir, simple + ".dll")
                        if File.Exists probe then Some probe else None

                match path with
                | Some p -> System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath p
                | None -> null)
        )

/// Loads the transformers an imported assembly publishes and hands them to the
/// expander.
///
/// This is the Template Haskell step: the assembly is already loaded (its
/// metadata was just read off it), and a transformer is an ordinary
/// `public static` method on the module class, so all that is left is to find
/// it. `Exports` comes from the same assembly's declarations, because a
/// template may only name an exported binding of its own module — anything else
/// has nowhere for rule two to resolve to. `TraitMethods` comes from the same
/// place, for the names rule three resolves to the trait rather than merely
/// stripping.
///
/// `renaming` is the importing edge's: a macro is registered under the name
/// *this* import makes it visible as, and one the edge filtered out is not
/// registered at all. Everything else stays the original's — the transformer to
/// invoke, and the module its templates resolve against.
/// What each loaded assembly's metadata says its module's macros see as trait
/// methods. Read from the metadata alone, so it holds across compilations.
let private macroTraitMethods =
    System.Collections.Generic.Dictionary<System.Reflection.Assembly, Set<string>>()

let private registerMacros
    (asm: System.Reflection.Assembly)
    (entries: ModuleMetadata.MacroEntry list)
    (patternEntries: ModuleMetadata.MacroEntry list)
    (hashEntries: ModuleMetadata.MacroEntry list)
    (decls: Decl list)
    (renaming: Map<string, string>)
    (hashRenaming: Map<string, string>)
    : unit =

    if not (entries.IsEmpty && patternEntries.IsEmpty && hashEntries.IsEmpty) then
        // The name, and the module whose class actually holds it. `""` is this
        // one — the spelling a `.dll`'s metadata uses for a name its own module
        // defined, and the only thing a `DDefun` here can mean.
        //
        // A `DExtern` carries the origin because a module publishes names it
        // did not define. Rule 2 qualifies a template's free name to a module
        // class, and for a re-export that class is the *origin's*: a facade
        // generates no member, so qualifying to the facade names nothing.
        let exports =
            decls
            |> List.choose (function
                | DExtern(_, origin, _, _, _) -> Some(origin.OriginalName, origin.OriginModule)
                | DDefun(n, _, _, _, _) -> Some(n, "")
                | DDef(n, _, _) -> Some(n, "")
                | DDefMutable(n, _, _) -> Some(n, "")
                | _ -> None)
            |> Map.ofList

        // The names rule three *resolves* rather than merely strips: the
        // methods of the traits this module declares, and of the ones it
        // imports, which its metadata lists since its own dependencies are not
        // in this graph.
        let traitMethods =
            decls
            |> List.collect (function
                | DTrait(_, _, _, _, signatures, _, _, _, _) -> signatures |> List.map (fun (n, _, _) -> n)
                | _ -> [])
            |> Set.ofList
            |> Set.union (
                match macroTraitMethods.TryGetValue asm with
                | true, seen -> seen
                | _ -> Set.empty
            )

        // `transformer` is the `defun` the transformer was compiled as: the
        // macro's own name, or `#name` for a hash macro.
        let bindingOf
            (what: string)
            (entry: ModuleMetadata.MacroEntry)
            (transformer: string)
            (visibleName: string)
            : Macro.MacroBinding =
            // Modulnyckeln bär sin namnrymd, så klassen går att stava ur
            // den ensam.
            let className =
                $"%s{Naming.namespaceOfKey entry.ModuleName}.%s{Naming.moduleClassName entry.ModuleName}"

            let clrType = asm.GetType className

            if isNull clrType then
                failwithf
                    $"'%s{entry.Name}' is declared a %s{what} by %s{asm.GetName().Name}, but the class '%s{className}' holding it is not in that assembly."

            let method = clrType.GetMethod(Prelude.moduleClrMemberName transformer)

            if isNull method then
                failwithf
                    $"'%s{entry.Name}' is declared a %s{what} by %s{asm.GetName().Name}, but '%s{className}' has no method '%s{Prelude.moduleClrMemberName transformer}'."

            { Name = visibleName
              ModuleName = entry.ModuleName
              Exports = exports
              TraitMethods = traitMethods
              Builtins = Prelude.builtinNames
              Method = method }

        for entry in entries do
            match Map.tryFind entry.Name renaming with
            | None -> ()
            | Some visibleName -> Macro.register (bindingOf "macro" entry entry.Name visibleName)

        for entry in patternEntries do
            match Map.tryFind entry.Name renaming with
            | None -> ()
            | Some visibleName -> Macro.registerPattern (bindingOf "pattern macro" entry entry.Name visibleName)

        for entry in hashEntries do
            match Map.tryFind entry.Name hashRenaming with
            | None -> ()
            | Some visibleName -> Macro.registerHash (bindingOf "hash macro" entry ("#" + entry.Name) visibleName)

        Macro.install ()

/// One module of the graph, parsed once and cached by path.
///
/// Deliberately modifier-independent. Modifiers belong to the *edge* that
/// reaches a module, and the same module may be reached by several edges with
/// different ones — so what is cached here is what the module says about
/// itself, and each edge derives its own view from it.
type LoadedModule = {
    FilePath: string
    ModuleName: string
    Dependencies: string list
    ParsedDecls: Decl list
    /// Declarations this module brought with it but did not make: the types it
    /// re-exported, each already wrapped in a `DModule` naming whoever
    /// declared it.
    ///
    /// Kept apart from `ParsedDecls` rather than picked back out of them,
    /// because these are placed *beside* this module and before it — a nested
    /// module would have `registerTypeDefs` key its types to this one, which
    /// is the second copy the whole thing exists to avoid.
    Carried: Decl list
    /// The macros the assembly publishes, and the assembly holding them.
    /// Registration is per edge, so it does not happen where this is built.
    Macros: ModuleMetadata.MacroEntry list
    PatternMacros: ModuleMetadata.MacroEntry list
    HashMacros: ModuleMetadata.MacroEntry list
    Assembly: System.Reflection.Assembly option
}

/// What reading one `.dll` produced, kept for the next compilation in this
/// process.
///
/// Off for a build, on for a REPL, and the difference is what the cache is
/// allowed to be wrong about. Reading a `.dll` parses its metadata back into
/// declarations, and that parse can invent names through `Gensym` — so decls
/// produced under one compilation's counter and reused under another's would
/// make a module's output depend on what was compiled before it. That is
/// exactly the determinism a build must not lose, and exactly what a REPL entry
/// has no use for: an entry's assembly is thrown away when the next one is
/// typed.
///
/// What it buys is the whole reason: a REPL entry imports the prelude, and
/// re-reading the prelude's metadata was about a third of what an entry cost.
let mutable cacheLoadedModules = false

/// What a `.dll`'s metadata says about its bindings and traits that is not a
/// declaration, collected for the whole compilation rather than checked.
type private DllFacts =
    { /// Exports whose call parks a thread, each with where it was defined.
      Blocking: (string * (string * string)) list
      /// Exports written with `defbjouble`, each with where it was defined.
      Doubles: (string * (string * string)) list
      /// The bodies of exported constrained generics.
      Bodies: (string * TypedAST.InlineTemplate) list
      /// This module's key, a trait it re-exported, and the module that
      /// declared the trait.
      TraitOrigins: (string * string * string) list }

/// Keyed on the timestamp as well as the path, so that rebuilding a dependency
/// and importing it again in the same process gets the new one.
type private CachedDll =
    { Decls: Decl list
      /// Replayed into each compilation that reads the cached entry, like
      /// `Linked`: the sets they go into belong to one compilation.
      Facts: DllFacts
      Carried: Decl list
      Macros: ModuleMetadata.MacroEntry list
      PatternMacros: ModuleMetadata.MacroEntry list
      HashMacros: ModuleMetadata.MacroEntry list
      Assembly: System.Reflection.Assembly
      /// Everything reading it added to the link set — itself, and the
      /// transitive dependencies its metadata named.
      Linked: string list
      /// The shared frameworks its metadata declared. Replayed with `Linked`
      /// and for the same reason: both are per compilation, and the parse the
      /// cache holds is not.
      Frameworks: string list }

let private dllCache = System.Collections.Generic.Dictionary<string * int64, CachedDll>()

// ---------------------------------------------------------------------------
// Building an imported source module
// ---------------------------------------------------------------------------

/// Compiles a `.bjo` to a `.dll` and returns its path.
///
/// A hook because `Pipeline.fs` is compiled before `Codegen.fs` and
/// `Program.fs`, and the backend is where this lives. Set once, by
/// `Program.main`.
let mutable compileLibrary: string -> string =
    fun path -> failwithf $"No backend is installed to compile '%s{path}'."

/// The modules the staleness walk in flight has already decided about.
///
/// Emptied by the walk that started it rather than kept, so that nothing is
/// remembered between two builds in one process: what a `.dll`'s timestamp says
/// is only true until something rebuilds it, and a REPL rebuilds things.
let private walking = System.Collections.Generic.HashSet<string>()

/// `(std prelude)`, which every program gets whether it asks or not.
let private preludePath = ModulePath [ "std"; "prelude" ]

/// Is this file part of the standard library itself?
///
/// The implicit import is suppressed for the whole of `lib`, not just for
/// `prelude.bjo`. `prelude` imports `maths`, so giving `maths` an implicit
/// `prelude` would be a cycle; and the rest of the library says what it depends
/// on explicitly, which is what building it in dependency order relies on.
/// Asked of the package the file belongs to rather than of its path, so that
/// "the standard library is a `lib` with a `std` in it" is written in `Paths`
/// and nowhere else.
let private isStandardLibrary (absPath: string) =
    match Paths.identityOf absPath with
    | Some identity -> identity.Root.IsStandardLibrary
    | None -> false

/// The imports a file declares, read off its S-expressions.
///
/// Read before the file is parsed, and that is the point. A module's
/// dependencies used to be collected from the `DImport`s in its *parsed*
/// declarations, which is too late for macros: parsing a form whose head is a
/// macro needs that macro's module already compiled and loaded. Reading
/// S-expressions needs no macros, so the edges can be found first.
///
/// `DeclParser.parseDecl` is called rather than the shape being re-matched here, so
/// that one place decides what an import path means. An import form contains no
/// expressions and so cannot itself contain a macro call.
///
/// Each spec comes with the range of the form it was written in, which is what
/// a modifier's errors point at.
let importsOf (forms: SExpr list) : (ImportSpec * Lexer.Range) list =
    forms
    |> List.collect (fun form ->
        match form with
        | SList(SAtom { Token = Lexer.Symbol "import" } :: _, _) ->
            match DeclParser.parseDecl form with
            | DImport(specs, r) -> specs |> List.map (fun s -> s, r)
            | _ -> []
        | _ -> [])

/// Adds the implicit `(import (std prelude))`.
///
/// The prelude is *linked*, not imported: `string-append`, `int->string`,
/// `print` and the `Written`, `Displayed`, `Iterable`, `Collector` and `Num` traits are the
/// language as far as a program is concerned, and which side of the
/// compiler/library line they happen to fall on is an implementation detail
/// that should not show up in every file's header.
///
/// Prepended rather than appended so that an explicit import of a user module
/// still shadows a prelude name — later bindings win, and the prelude is the
/// earliest thing there is. A file that already imports the prelude by hand is
/// left alone, so the import graph has one edge rather than two.
///
/// Done to the *forms* rather than to the declarations, because the import
/// graph is now built before parsing and the prelude is one of its edges.
///
/// The comparison is on the path alone, so that
/// `(import (except (std prelude) print))` counts as importing it. Otherwise
/// the implicit edge would be added alongside the explicit one and reinstate
/// exactly the name the modifier was written to remove.
let private withImplicitPrelude (absPath: string) (forms: SExpr list) : SExpr list =
    if
        importsOf forms |> List.exists (fun (s, _) -> s.Path = preludePath)
        || isStandardLibrary absPath
    then
        forms
    else
        let r =
            { Start = { Line = 1; Column = 1 }
              End = { Line = 1; Column = 1 }
              File = absPath
              Module = Naming.moduleKeyOfPath absPath }

        let sym name = SAtom { Token = Lexer.Symbol name; Range = r }
        SList([ sym "import"; SList([ sym "std"; sym "prelude" ], r) ], r) :: forms

/// Det föråldringskollen behöver veta om en `.bjo`, utan att tolka den.
type private SourceFacts =
    { /// Filen själv och allt den drar in med `include`, transitivt.
      Sources: Set<string>
      /// Importerna, med den underförstådda preluden inräknad.
      Imports: (ImportSpec * Lexer.Range) list
      /// The import forms as written, the implicit prelude's not among them:
      /// what `Imports` is read from, and what a build record keeps so that
      /// the facts can be had without reading the source again.
      ImportForms: SExpr list }

/// Vad varje `.bjo` sade om sig själv, och vid vilken tidsstämpel.
///
/// Kollen läser och lexar hela källfilen bara för att få fram två saker som
/// båda är rena funktioner av filens innehåll. Det gjordes om från grunden för
/// varje modul som nådde filen, i varje bygge: i en batch över testsviten lexas
/// `prelude.bjo` omkring tvåhundra gånger och svarar likadant varje gång. Mätt
/// var det en tredjedel av batchens tid.
///
/// Tidsstämpeln sitter i nyckeln, som i `dllCache`, så att en fil som skrivs om
/// mitt i en process läses om. Jämförelserna av tidsstämplar cachas *inte* —
/// det är bara läsningen som sparas, och en `.dll` som byggs om under tiden
/// märks som förut.
///
/// En inkluderad fil som ändras kan inte ge fel svar, trots att stängningen
/// beräknades tidigare. Det enda sättet att sakna en fil i stängningen är att
/// filen som inkluderar den har ändrats — och den filen ligger själv i
/// stängningen och stäms av mot `.dll`:ens tidsstämpel, så modulen döms som
/// föråldrad och byggs om, varvid stängningen räknas om.
/// Keyed by path and timestamp, and that stays right once packages exist: the
/// roots are installed before the first file is read and never change again, so
/// what a path resolved to and what it was called cannot go stale under a cache
/// entry. The same holds for `dllCache` above.
let private sourceFacts = System.Collections.Generic.Dictionary<string * int64, SourceFacts>()

let private factsOf (bjoPath: string) : SourceFacts =
    let key = bjoPath, File.GetLastWriteTimeUtc(bjoPath).Ticks
    // Text a host holds changes without the file's timestamp, so it is read
    // again each time rather than cached under one.
    let held = Paths.sourceOverrides.ContainsKey(Path.GetFullPath bjoPath)

    match sourceFacts.TryGetValue key with
    | true, hit when not held -> hit
    | _ ->
      Timing.phase "import: source facts" (fun () ->
        let forms, _ = Lexer.tokenize bjoPath (Paths.readSource bjoPath) |> read
        let expanded, sources = expandIncludes [ bjoPath ] bjoPath forms

        // The imports of the expanded forms, as `loadModuleGraph` reads them:
        // an import written in an included file is this module's dependency.
        let facts =
            { Sources = sources
              Imports = importsOf (withImplicitPrelude bjoPath expanded)
              ImportForms =
                expanded
                |> List.filter (function
                    | SList(SAtom { Token = Lexer.Symbol "import" } :: _, _) -> true
                    | _ -> false) }

        // Not under the file's timestamp when the text was the host's: the
        // file on disk says something else.
        if not held then sourceFacts[key] <- facts
        facts)

/// An import form on one line, as source writes it, for a build record. `None`
/// for a form holding something other than names, keywords, strings and
/// lists, which no import does today; such a module's record keeps no facts,
/// and its source is read instead.
let rec private importText (s: SExpr) : string option =
    match s with
    | SAtom { Token = Lexer.Symbol n } -> Some n
    | SAtom { Token = Lexer.Keyword k } -> Some("#:" + k)
    | SAtom { Token = Lexer.StringLit v } ->
        Some("\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"")
    | SList(items, _) ->
        let parts = items |> List.map importText

        if parts |> List.forall Option.isSome then
            Some("(" + (parts |> List.choose id |> String.concat " ") + ")")
        else
            None
    | _ -> None

/// The lines of a build record that carry `bjoPath`'s facts: a `facts` line,
/// a `fact-source` for each file of its closure, and a `fact-import` for each
/// import form. With them the staleness check knows a current module's
/// imports without tokenizing its source, which for the prelude is most of
/// what an import of it costs. Empty when an import form cannot be written on
/// a line.
let factLines (bjoPath: string) : string list =
    let facts = factsOf bjoPath
    let imports = facts.ImportForms |> List.map importText

    if imports |> List.forall Option.isSome then
        [ yield "facts"
          for source in facts.Sources do
              yield "fact-source " + Path.GetFullPath source
          for text in imports |> List.choose id do
              yield "fact-import " + text ]
    else
        []

/// `bjoPath`'s facts as its build record kept them, when the record can be
/// trusted for them: it is the record of the `.dll` on disk, which is `built`,
/// and no file of the closure is newer than it or held by a host. A file of
/// the closure can only change by being written, and a written one is newer,
/// so the closure and its import forms are what they were when it was built.
let private recordedFacts (bjoPath: string) (built: DateTime) (record: BuildRecord.Record option) : SourceFacts option =
    match record |> Option.bind (fun r -> r.Facts) with
    | Some(sources, importTexts) when
        not sources.IsEmpty
        && sources
           |> List.forall (fun s ->
               not (Paths.sourceOverrides.ContainsKey s) && File.Exists s && File.GetLastWriteTimeUtc s <= built)
        ->
        try
            let forms = importTexts |> List.collect (fun text -> Lexer.tokenize bjoPath text |> read |> fst)

            Some
                { Sources = Set.ofList sources
                  Imports = importsOf (withImplicitPrelude bjoPath forms)
                  ImportForms = forms }
        with _ ->
            None
    | _ -> None

/// Every file `bjoPath` is made of: itself and what it pulls in with `include`,
/// transitively.
///
/// The same closure the staleness check below compares against, exposed so that
/// a finished build can write down what it was built from (`Build`'s build
/// record). A driver outside the compiler has no other way to know — an
/// `include` is a path in a form, and finding it means reading the file the way
/// the compiler does. Answered from the cache above, so asking after a build
/// costs nothing for anything the build already read.
let sourceClosure (bjoPath: string) : string list =
    (factsOf bjoPath).Sources |> Set.toList

/// Whether `bjoPath`'s `.dll` is current, given what each of its imports
/// resolves to.
///
/// This is the rule `ensureLibrary` uses, separated from the walk so that it
/// can also be checked without building anything. `ensureLibrary` passes a
/// resolver that builds each import first; a graph build passes one that only
/// looks at what is on disk. Both use this function so that the two cannot
/// disagree about what out of date means.
let private upToDateAgainst (resolve: ImportSpec -> Result<string, string>) (bjoPath: string) : bool =
    let dllPath = Path.ChangeExtension(bjoPath, ".dll")

    let compilerBuilt =
        let loc = Paths.compilerAssembly
        if loc <> "" && File.Exists loc then File.GetLastWriteTimeUtc loc else DateTime.MinValue

    /// The runtime assemblies, for the reason the compiler is an input: a
    /// library is compiled against their API, and one rebuilt on its own may
    /// no longer have a member the library calls.
    let runtimeBuilt =
        Paths.runtimeAssemblies
        |> List.filter File.Exists
        |> List.map File.GetLastWriteTimeUtc
        |> List.fold max DateTime.MinValue

    /// What the packages of this build declare. An input like the compiler
    /// itself: a framework taken out of a manifest has to make the modules
    /// that named types from it stale, so that they fail with the naming
    /// error instead of quietly staying built.
    let declarationsWritten =
        match Frameworks.declarationFilePath () with
        | Some path when Frameworks.appliesTo bjoPath && File.Exists path -> File.GetLastWriteTimeUtc path
        | _ -> DateTime.MinValue

    // The restored NuGet packages, for the same reason.
    let packagesWritten =
        if NuGetRefs.appliesTo bjoPath then
            NuGetRefs.listFiles ()
            |> List.filter File.Exists
            |> List.map File.GetLastWriteTimeUtc
            |> List.fold max DateTime.MinValue
        else
            DateTime.MinValue

    File.Exists dllPath
    && (let built = File.GetLastWriteTimeUtc dllPath
        let record = BuildRecord.ofArtefact dllPath

        let facts =
            match recordedFacts bjoPath built record with
            | Some recorded -> recorded
            | None -> factsOf bjoPath

        /// A linked module that was rebuilt after this one is still no reason
        /// to rebuild this one if its interface is the one this build saw.
        let unchanged (dep: string) =
            File.Exists dep
            && (File.GetLastWriteTimeUtc dep <= built
                || (match record with
                    | Some r ->
                        match Map.tryFind (Path.GetFullPath dep) r.InterfaceOf with
                        | Some seen -> BuildRecord.interfaceOf dep = Some seen
                        | None -> false
                    | None -> false))

        // Everything the build linked, not only what it imports: with early
        // cutoff a module between the two can stay as it was while one below
        // it changes, and this module read that one's metadata too.
        let linkedModules =
            match record with
            | Some r ->
                let runtime = Paths.runtimeAssemblies |> List.map Path.GetFullPath |> Set.ofList
                r.Deps |> List.filter (runtime.Contains >> not)
            | None -> []

        // Built against the runtime's assemblies in another directory: the
        // precompiled ones in `bin/compiler` or MSBuild's, which
        // `BJOLANG_NO_R2R` chooses between. Both are older than the module,
        // so the timestamps above can not see the change. A directory counts
        // as the runtime's when it holds `BjolangRuntime.dll`, so that a
        // module of the same file name elsewhere is not mistaken for one.
        let runtimeMoved =
            match record with
            | Some r ->
                let names = Paths.runtimeAssemblies |> List.map Path.GetFileName |> Set.ofList
                let current = Paths.runtimeAssemblies |> List.map Path.GetFullPath |> Set.ofList

                r.Deps
                |> List.exists (fun dep ->
                    names.Contains(Path.GetFileName dep)
                    && not (current.Contains(Path.GetFullPath dep))
                    && File.Exists(Path.Combine(Path.GetDirectoryName dep, "BjolangRuntime.dll")))
            | None -> false

        compilerBuilt <= built
        && not runtimeMoved
        && runtimeBuilt <= built
        && declarationsWritten <= built
        && packagesWritten <= built
        && not (NuGetRefs.changedSince bjoPath)
        // A declaration *removed* deletes the file rather than
        // touching it, so the comparison above cannot see it. This one
        // reads what the module was built under and compares the sets.
        && not (Frameworks.declarationsChanged bjoPath)
        && facts.Sources |> Set.forall (fun src -> File.GetLastWriteTimeUtc src <= built)
        // The implicit prelude edge counts as much as a written one:
        // a module that never names the prelude is still compiled
        // against its metadata.
        && (facts.Imports
            |> List.forall (fun (spec, _) ->
                match resolve spec with
                // A dependency whose `.dll` does not exist yet makes this
                // module stale. This only happens with a resolver that does
                // not build: `ensureLibrary`'s builds the dependency first.
                | Ok dep -> unchanged dep
                // An import that resolves to nothing makes the module
                // stale rather than current, so the compile runs and
                // reports it against the form that wrote it. Answering
                // "up to date" here would leave a broken import
                // undetected for as long as the stale `.dll` survives.
                | Error _ -> false))
        && linkedModules |> List.forall unchanged)

/// The `.dll` for an imported `.bjo`, built if there is not a current one.
///
/// `(import "x.bjo")` means a compiled unit, always. Merging the source into
/// the importing assembly is what it used to mean, and it never worked: only
/// the last module was emitted, so the generated C# referenced a class nothing
/// produced. Compiling it separately is also what makes `include` a distinct
/// thing rather than a slower spelling of the same one.
///
/// Staleness is by timestamp against the whole source closure, which is what
/// `expandIncludes` reports alongside the forms — the include walk is the only
/// thing that knows what that closure is, so it is asked rather than repeated.
///
/// The compiler counts as part of that closure. What a `.dll` carries for an
/// importer is this compiler's metadata format, so one built by a different
/// build of the compiler is out of date however new its source is — and the
/// symptom otherwise is not a rebuild but an unbound variable, because
/// unreadable metadata describes a module that exports nothing.
///
/// So do the module's *imports*, and for the same reason: what crosses an
/// import edge is metadata, read once, when the importer was compiled. A
/// dependency that has changed since is a `.dll` describing a module that no
/// longer exists, and the symptom is not an error — the old artefact keeps
/// being linked, and an edit two modules down simply does not happen.
///
/// Each import is *resolved* rather than merely stat-ed, which is what makes
/// this transitive: resolving builds the dependency if it is itself behind, so
/// by the time its timestamp is compared it is current, and a change anywhere
/// in the graph reaches everything above it one edge at a time.
///
/// Except where it changed nothing an importer can see. A linked module newer
/// than this one whose interface digest is the one this module's build record
/// says it was built against leaves this module current — early cutoff, so that
/// an edit to a body rebuilds one module rather than everything above it. With
/// no record to go by, a newer import is a stale module, as it always was.
///
/// Public because a batch wants exactly this question asked about files that
/// nobody imports. `--if-stale` builds a set of libraries by asking
/// about each one, and gets the ordering and skipping for free: one that
/// is already current is not recompiled, and one that is a dependency of
/// another in the set is built when reached rather than when it appears in the list.
let rec ensureLibrary (bjoPath: string) : string =
    let dllPath = Path.ChangeExtension(bjoPath, ".dll")

    // Reached from itself, or reached twice: the answer is the one this walk
    // has already arrived at. A diamond is walked once rather than once per
    // path through it, and a cycle terminates here rather than recurring —
    // an illegal one is `Build.checkNotCyclic`'s to report, and it says so in
    // terms of the import chain rather than as a stack overflow.
    if not (walking.Add bjoPath) then
        dllPath
    else

    let root = walking.Count = 1

    try
        // `resolveDependency` builds each import that is out of date before
        // its timestamp is compared, so a change anywhere below this module
        // reaches it.
        if upToDateAgainst (resolveDependency bjoPath) bjoPath then
            dllPath
        else
            compileLibrary bjoPath
    finally
        // Only the walk that started it forgets, so that everything one build
        // decided stays decided for the whole of it — including for the
        // compilation this is about to start, which resolves the same imports
        // again from `loadModuleGraph`.
        if root then walking.Clear()

/// The path an import resolves to, which is always an assembly, or why it
/// resolves to nothing.
///
/// Where a source file exists it is the truth, and the `.dll` beside it may be
/// behind it — so the `.bjo` is what this looks for first, and `ensureLibrary`
/// decides whether the built artefact is still current. A `.dll` with no source
/// is a prebuilt library and is taken as given.
///
/// A module path anchors to a package, never to the working directory: a module
/// import means the same file no matter where the compiler is invoked from, so
/// the compiled standard library is the one that gets linked instead of being
/// rebuilt from source per caller.
///
/// An import that resolves to nothing used to answer with the path as written,
/// which meant a mistyped module failed much later under a name nobody wrote —
/// as an unbound variable, or as a missing assembly at run time. The failure is
/// here, where the spelling that caused it is still in hand.
and private resolveDependency (basePath: string) (spec: ImportSpec) : Result<string, string> =
    resolveImport true basePath spec

/// `resolveDependency`, with a choice of whether an imported `.bjo` is built
/// first.
///
/// With `build = false` it answers the `.dll` beside the `.bjo` as it is on
/// disk, whether or not it exists or is current. A graph build plans with this.
/// Both cases share this function so that a graph build resolves imports to
/// exactly the files the compiler would.
and private resolveImport (build: bool) (basePath: string) (spec: ImportSpec) : Result<string, string> =
    /// The artefact a candidate source path names, if any of the three shapes
    /// is on disk.
    let artefactOf (raw: string) : string option =
        let bjoPath = if raw.EndsWith ".bjo" then raw else raw + ".bjo"
        let dllPath = Path.ChangeExtension(bjoPath, ".dll")

        if File.Exists bjoPath then Some(if build then ensureLibrary bjoPath else dllPath)
        elif File.Exists dllPath then Some dllPath
        // `(import "x.dll")` names an artefact outright: the two probes above
        // looked for `x.dll.bjo` and `x.dll.dll` and found neither, and the
        // file as written is the answer. This is the only spelling that reaches
        // a prebuilt assembly with no source beside it.
        elif File.Exists raw then Some raw
        else None

    match spec.Path with
    | RelativePath p ->
        let raw = Path.GetFullPath(Path.Combine(Path.GetDirectoryName basePath, p))

        match artefactOf raw with
        | Some artefact -> Ok artefact
        | None -> Error $"no such file: %s{p} (looked for %s{raw})."
    | ModulePath parts ->
        let shown = Paths.showPackageName parts

        match Paths.findModule parts with
        | Paths.NoPackage owner ->
            Error
                $"no module %s{shown}: no package named %s{Paths.showPackageName owner} is known to this build."
        | Paths.PackageItself root ->
            let example = Paths.showPackageName (parts @ [ "name" ])

            Error
                $"no module %s{shown}: %s{shown} is a package, not a module in one. Its modules are the files under %s{root.Directory}, imported as %s{example}."
        | Paths.ModuleFile(root, file) ->
            match artefactOf file with
            | Some artefact -> Ok artefact
            | None ->
                Error
                    $"no module %s{shown}: package %s{Paths.showPackageName root.Name} at %s{root.Directory} has no %s{Paths.moduleFileName root parts}."

// ---------------------------------------------------------------------------
// Asking about the graph without building it
// ---------------------------------------------------------------------------

/// The `.bjo` files `bjoPath` imports, as absolute paths, whether built or not.
///
/// These are the edges of a graph build, including the implicit prelude.
/// Imports that resolve to a prebuilt `.dll` are left out because there is
/// nothing to build. Imports that resolve to nothing are left out too; the
/// compiler reports them when the module itself is compiled.
let importedSources (bjoPath: string) : string list =
    (factsOf bjoPath).Imports
    |> List.choose (fun (spec, _) ->
        match resolveImport false bjoPath spec with
        | Ok artefact ->
            let source = Path.ChangeExtension(artefact, ".bjo")
            if File.Exists source then Some(Path.GetFullPath source) else None
        | Error _ -> None)
    |> List.distinct

/// Whether `bjoPath`'s `.dll` is current, without building anything.
///
/// It compares against the `.dll`s of the imports as they are on disk now. It
/// cannot know that an import is about to be rebuilt, so a graph build also
/// counts a module stale when anything it imports will be rebuilt.
let isCurrent (bjoPath: string) : bool =
    upToDateAgainst (resolveImport false bjoPath) bjoPath

/// How a library records one of the assemblies it links.
///
/// A module name where the dependency has a source under a package, so that an
/// importer resolves it the way it resolves an import — against its own roots,
/// in its own project. A path only where there is no name to write: a prebuilt
/// assembly with no source beside it, or a file under no package at all.
///
/// The name is derived from the `.bjo`, not from the `.dll`: they share a
/// directory and a stem, so either answers, and the source is the thing the
/// package actually contains.
let dependencyEntry (dllPath: string) : string =
    let full = Path.GetFullPath dllPath
    let bjoPath = Path.ChangeExtension(full, ".bjo")

    if File.Exists bjoPath then
        match Paths.identityOf bjoPath with
        | Some identity -> Paths.showPackageName identity.ModuleName
        | None -> full
    else
        full

/// Whether a recorded dependency is a module name rather than a path.
///
/// A module name is written the way source writes one, and no absolute path
/// begins with a parenthesis.
let private isModuleEntry (entry: string) = entry.StartsWith "("

/// The segments of a recorded module name.
let private moduleEntryParts (entry: string) : string list =
    entry.Trim([| '('; ')' |]).Split(' ')
    |> Array.filter (fun s -> s <> "")
    |> List.ofArray

/// The one parameter an entry point has: the arguments, a `(Vec string)`.
///
/// `main` is called by the generated entry point rather than by anything in the
/// program, so what it takes is the runtime's to say and not inference's to
/// work out from a body:
///
/// - a `main` with no parameter is given one. The arguments exist whether or
///   not the program wants them, and the entry point then has a single call to
///   emit rather than a case analysis over what it may hand over.
/// - a `main` with no signature has that parameter declared `(Vec string)`,
///   rather than typed by whatever its body happened to constrain.
///   `(println (vec-ref args 0))` alone leaves the element type open, and a
///   generic entry point is one nothing can call. What it returns is
///   inferred; the entry point exits with it if it is an int, and with 0 if
///   it is not.
///
/// A signature written by hand is left as written and checked afterwards, by
/// `checkEntryPoint`, which can compare types rather than syntax.
///
/// Applied to the file being compiled and to nothing else: an imported module's
/// `main`, if it has one, is an ordinary function of that module's.
let private shapeEntryPoint (decls: Decl list) : Decl list =
    let entryPoint =
        decls
        |> List.tryPick (function
            | DDefun("main", args, _, colour, r) -> Some(args, colour, r)
            | _ -> None)

    match entryPoint with
    | None -> decls
    | Some(args, colour, r) ->
        // Invented rather than named, because the source that would refer to it
        // is the source that did not write it.
        let withParameter =
            if not (mandatoryNames args).IsEmpty then
                decls
            else
                let received = MandatoryArg(Gensym.fresh "args", None)

                decls
                |> List.map (function
                    | DDefun("main", args, body, colour, r) -> DDefun("main", received :: args, body, colour, r)
                    | d -> d)

        // The parameter is declared rather than the whole type, so that what
        // `main` returns is inferred. The entry point returns 0 for a `main`
        // that does not return an int.
        if decls |> List.exists (function DSignature("main", _, _, _) -> true | _ -> false) then
            withParameter
        else
            let argsType = TApp("Vec", [ TName("string", r) ], r)

            withParameter
            |> List.map (function
                | DDefun("main", MandatoryArg(n, None) :: rest, body, colour, r) ->
                    DDefun("main", MandatoryArg(n, Some argsType) :: rest, body, colour, r)
                | d -> d)

/// The type `main` ended up with, once the whole program has been checked.
///
/// `shapeEntryPoint` gives an unsignatured `main` the right signature, so the
/// only way to arrive here with a different type is to have written one — and
/// the mistake deserves a message naming the shape rather than a C# error
/// inside a generated entry point nobody wrote.
///
/// The effect is not compared: `main` may be a bjoroutine, and the entry point
/// drives the fiber.
let private checkEntryPoint (env: TypedAST.Env) (decls: Decl list) : unit =
    let definedHere =
        match List.tryLast decls with
        | Some(DModule(_, inner, _)) ->
            inner
            |> List.tryPick (function
                | DDefun("main", _, _, _, r)
                | DDef("main", _, r)
                | DDefMutable("main", _, r) -> Some r
                | _ -> None)
        | _ -> None

    match definedHere, Map.tryFind "main" env.Bindings with
    | Some r, Some binding ->
        let (TypedAST.Scheme(_, constraints, t)) = binding.Scheme
        let actual = Unification.prune env.Registry t

        match actual with
        | TypedAST.TFun([ TypedAST.TCon("Vec", [ TypedAST.TCon(TypedAST.TypeConstants.StringName, []) ]) ], _, _) when
            constraints.IsEmpty
            ->
            ()
        | _ ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: 'main' is a program's entry point and takes the arguments, a (Vec string). This one is %s{DotNetInterop.showType actual}. A main written without a parameter is given the arguments anyway, and one that does not return an int exits with 0 — so the way to a different type is a signature or a parameter type, and there is nothing the entry point could pass it."
    | _ -> ()

let wrapInModule (moduleName: string) (filePath: string) (decls: Decl list) : Decl list =
    // Find the first and last range to represent the module range
    let r = 
        match decls with
        | [] ->
            { Start = { Line = 1; Column = 1 }
              End = { Line = 1; Column = 1 }
              File = filePath
              Module = moduleName }
        | first :: _ ->
            let last = List.last decls
            unionLexerRanges (Ast.declRange first) (Ast.declRange last)
    
    [ DModule(moduleName, decls, r) ]

let loadModuleGraph
    (mainFilePath: string)
    : Decl list
      * string list
      * Set<string * (string * string)>
      * Set<string * (string * string)>
      * Map<string, TypedAST.InlineTemplate>
      * (string * string * string) list
      * Docs.Entry list =
    // Unconditionally, not only when something publishes a macro: the expander
    // is also what reports a macro used in the module that defines it.
    Macro.install ()

    // Before the first `Assembly.LoadFile` below, since it is the assemblies
    // loaded there whose dependencies would otherwise not resolve.
    installAssemblyResolver ()

    let resolvedModules = System.Collections.Generic.Dictionary<string, LoadedModule>()

    /// The `(:doc ...)` forms of the file being compiled, read but not checked.
    let mainDocs = ResizeArray<Docs.Entry>()

    /// Which file each module key belongs to, for the length of one build.
    let claimedKeys = System.Collections.Generic.Dictionary<string, string>()
    let currentPath = System.Collections.Generic.HashSet<string>()
    let dllDeps = System.Collections.Generic.HashSet<string>()

    /// Imported definitions whose call parks the thread it runs on.
    ///
    /// Collected here because this is the only place a dependency's metadata is
    /// read, and carried out whole rather than turned into declarations: it is a
    /// fact about a binding's *body*, and there is no declaration form that says
    /// one. Under the names the origin published — a module that renamed the
    /// import is resolved back through `ImportAliases` when the set is asked.
    /// Each with where it was defined, for the reason `doubleDefs` below is.
    let blockingDefs = System.Collections.Generic.HashSet<string * (string * string)>()

    /// Imported definitions written with `defbjouble`, under the names their
    /// origin published. The suspending copy is an ordinary imported binding
    /// like any other; what this adds is that the two are a pair.
    ///
    /// Each with the module that defined it and its name there, the origin
    /// `checkExtern` records for the binding: another import may take the name
    /// over with a plain definition, and then the name is no longer a pair.
    let doubleDefs = System.Collections.Generic.HashSet<string * (string * string)>()

    /// The bodies of imported constrained generics, under the names their
    /// origin published — which is what an importer's `DExtern` points back to
    /// through `ImportAliases`, whatever an import modifier renamed it to here.
    let constrainedBodies =
        System.Collections.Generic.Dictionary<string, TypedAST.InlineTemplate>()

    /// The traits a dependency re-exported: the module whose metadata
    /// published each, and the module that declared it.
    let reExportedTraits = System.Collections.Generic.HashSet<string * string * string>()

    let noteFacts (facts: DllFacts) =
        for entry in facts.Blocking do
            blockingDefs.Add entry |> ignore

        for entry in facts.Doubles do
            doubleDefs.Add entry |> ignore

        for (name, body) in facts.Bodies do
            constrainedBodies[name] <- body

        for entry in facts.TraitOrigins do
            reExportedTraits.Add entry |> ignore

    /// Every import edge that reaches a given module, as the renaming it
    /// produces, the type spellings it adds, the keys of the types and
    /// constructors whose bare names it keeps, and the position of the form
    /// that wrote it.
    ///
    /// Keyed by path, like the module cache, but a *list* — the cache holds
    /// what a module says about itself, and this holds what each importer made
    /// of it.
    let edges =
        System.Collections.Generic.Dictionary<
            string,
            ResizeArray<Map<string, string> * (string * string * AliasKind) list * Set<string> * Lexer.Range>
         >()

    /// The modules each `.dll` was built against, which it links but does not
    /// offer. See the sort below.
    let linkedOf = System.Collections.Generic.Dictionary<string, string list>()

    let rec load (filePath: string) : unit =
        let absPath = Path.GetFullPath(filePath)
        if currentPath.Contains(absPath) then
            failwithf "Cyclic dependency detected: %s" absPath
        if not (resolvedModules.ContainsKey(absPath)) then
            currentPath.Add(absPath) |> ignore
            
            let cacheKey = absPath, File.GetLastWriteTimeUtc(absPath).Ticks

            let cached =
                if cacheLoadedModules && absPath.EndsWith ".dll" then
                    match dllCache.TryGetValue cacheKey with
                    | true, hit -> Some hit
                    | _ -> None
                else
                    None

            let parsedDecls, carriedDecls, deps, macros, patternMacros, hashMacros, assembly =
                match cached with
                | Some hit ->
                    // The link set is per compilation and the parse is not, so
                    // what was linked is replayed rather than remembered.
                    for path in hit.Linked do
                        dllDeps.Add path |> ignore
                        noteAssemblyPath path

                    linkedOf[absPath] <- hit.Linked |> List.filter ((<>) absPath)

                    Frameworks.noteImported hit.Frameworks
                    noteFacts hit.Facts

                    InferExpr.runGrammarCheck <- runGrammarCheck
                    assembliesByModule[Naming.moduleKeyOfPath absPath] <- hit.Assembly
                    hit.Decls, hit.Carried, [], hit.Macros, hit.PatternMacros, hit.HashMacros, Some hit.Assembly
                | None ->

                if absPath.EndsWith(".dll") then
                    dllDeps.Add(absPath) |> ignore
                    // Before the assembly is loaded: a transformer this one
                    // publishes may call into any of these, and the resolver is
                    // what makes that possible.
                    noteAssemblyPath absPath
                    let asm, meta = metadataOf absPath

                    // A transitive dependency is *linked*, not *imported*. Its
                    // assembly has to be referenced, because that is where the
                    // code of anything re-exported through this DLL actually
                    // lives. However, we intentionally hide its internal
                    // dependencies from the current module's scope. This ensures
                    // that you can only see the things the DLL explicitly chose to export.
                    let linkedDeps =
                        meta.Deps
                        |> List.filter (fun entry -> entry <> "")
                        |> List.map (fun entry ->
                            // A module name is resolved exactly as an import of
                            // it written inside this library would be — same
                            // function, so a dependency that is behind is built
                            // here too. There is no second rule for what a
                            // module name means.
                            let depPath =
                                if isModuleEntry entry then
                                    let spec = plainImport (ModulePath(moduleEntryParts entry))

                                    match resolveDependency absPath spec with
                                    | Ok path -> Path.GetFullPath path
                                    | Error why ->
                                        failwithf
                                            $"'%s{absPath}' links the module %s{entry}, which this build cannot resolve: %s{why}"
                                else
                                    entry

                            // Skipping a missing one is what this used to do,
                            // and it produced a link that was quietly short a
                            // module: the names it re-exported were reported as
                            // unbound in the importer, naming neither the
                            // library that recorded the dependency nor the file
                            // that had gone.
                            if not (File.Exists depPath) then
                                failwithf
                                    $"'%s{absPath}' links '%s{depPath}', which is not there. Rebuild the library, or restore the assembly it was built against."

                            depPath)

                    for depPath in linkedDeps do
                        dllDeps.Add(depPath) |> ignore
                        noteAssemblyPath depPath

                    linkedOf[absPath] <- linkedDeps

                    // What this import was built against is what the importing
                    // module is compiled against too — calling a function whose
                    // signature mentions a framework type can make the emitted
                    // C# mention it — and is part of what the importer records,
                    // because a program linking this assembly has to load the
                    // frameworks it uses.
                    Frameworks.noteImported meta.Frameworks

                    if not meta.MacroTraitMethods.IsEmpty then
                        macroTraitMethods[asm] <- Set.ofList meta.MacroTraitMethods

                    // Where a name this DLL publishes was defined, as its
                    // extern's origin says: a facade's entry names the module
                    // it passes the name on from.
                    let originOf (name: string) =
                        meta.Defs
                        |> List.tryFind (fun d -> d.Name = name)
                        |> Option.bind (fun d -> d.Origin)
                        |> Option.defaultValue (Naming.moduleKeyOfPath absPath, name)

                    // The macros as they are here, where the bodies below used
                    // to be read: they are read when they are first used, and
                    // with these (see `withMacrosOf`).
                    let macrosHere =
                        if meta.InlineTemplates.IsEmpty && meta.ConstrainedBodies.IsEmpty then
                            Macro.emptyState
                        else
                            Macro.snapshot ()

                    // Transitive, unlike the exports above: a name re-exported
                    // through this DLL is bound here, and whether calling it
                    // parks a thread is a fact about the body it still reaches.
                    let facts =
                        { Blocking = meta.BlockingDefs |> List.map (fun name -> name, originOf name)
                          Doubles = meta.DoubleDefs |> List.map (fun name -> name, originOf name)
                          // Transitive for the same reason `BlockingDefs` is: a
                          // name re-exported through this DLL is bound here, and
                          // the body a copy would be made from is still the one
                          // it reaches.
                          Bodies =
                            Timing.phase "import: constrained bodies" (fun () ->
                                meta.ConstrainedBodies
                                |> List.map (fun entry -> entry.Name, constrainedBodyTemplate macrosHere absPath entry))
                          TraitOrigins =
                            meta.TraitOrigins
                            |> List.map (fun (t, origin) -> Naming.moduleKeyOfPath absPath, t, origin) }

                    noteFacts facts

                    // Inlineable method bodies, if this assembly published any.
                    // Without them everything that would have been inlined
                    // calls the landing pad instead.
                    let inlineImplDecls =
                        Timing.phase "import: inline templates" (fun () ->
                            meta.InlineTemplates |> List.map (inlineImplDecl macrosHere absPath))

                    // Foreign imports precede the traits: a trait's own
                    // signature may name a type an `import/class` alias
                    // introduced, and an impl's inline template may call an
                    // `import/extern` one. Impls follow the traits they belong
                    // to, because reading one back needs the trait registered.
                    let declText =
                        meta.TypeDecls @ meta.ExternDecls @ meta.TraitDecls @ meta.ImplDecls
                        |> String.concat "\n"

                    let declsFromText =
                        if System.String.IsNullOrWhiteSpace declText then
                            []
                        else
                            // A trait's default bodies are in here, and are
                            // code: they are spliced at every impl that leaves
                            // the method out.
                            Timing.phase "import: declarations" (fun () ->
                                tokenizeMetadata absPath declText
                                |> read
                                |> fst
                                |> List.map (readOrigins (Naming.moduleKeyOfPath absPath))
                                |> DeclParser.parseModule)

                    // The bare spellings of the types this assembly declares.
                    //
                    // A type name in metadata is a *key*: the module that
                    // declared it and the name it was declared under, which
                    // resolves to itself wherever it is read. That is what a
                    // signature naming a type from a link-only dependency
                    // needs, and it is why nothing here re-derives anything.
                    // What source writes is still the bare name, so it is
                    // offered back as a spelling — the same mechanism an import
                    // modifier's spellings use, and the reason a plain import
                    // goes on meaning what it meant.
                    let typeSpellingDecls =
                        let bare = Naming.bareTypeName (Naming.moduleKeyOfPath absPath)

                        let spellingOf (kind: AliasKind) (r: Lexer.Range) (keyed: string) =
                            let name = bare keyed
                            if name = keyed then [] else [ DImportAlias(name, keyed, kind, r) ]

                        declsFromText
                        |> List.collect (function
                            | DType(tds, r)
                            | DTypeRec(tds, r) ->
                                tds
                                |> List.collect (fun td ->
                                    spellingOf AliasType r td.Name
                                    @ (match td.Kind with
                                       | Union cases ->
                                           cases
                                           |> List.collect (function
                                               | SimpleCase(n, _)
                                               | DataCase(n, _, _) -> spellingOf AliasConstructor r n)
                                       | Record _
                                       | Newtype _ -> spellingOf AliasConstructor r td.Name
                                       | Alias _ -> []))
                            | _ -> [])

                    // The bare spellings of the traits this assembly declares or
                    // passes on, and of their methods, as for its types. A
                    // trait in metadata is named by its key, and a method's key
                    // is made from it.
                    let traitSpellingDecls =
                        declsFromText
                        |> List.collect (function
                            | DTrait(key, _, _, _, signatures, _, _, _, r) when Naming.bareTraitName key <> key ->
                                DImportAlias(Naming.bareTraitName key, key, AliasTrait, r)
                                :: (signatures
                                    |> List.map (fun (m, _, _) ->
                                        DImportAlias(m, Naming.traitMethodKey key m, AliasMethod, r)))
                            | _ -> [])

                    // The types this assembly re-exported: declarations another
                    // module made, arriving under that module's key.
                    //
                    // They are kept in a `DModule` of the module that declared
                    // them, and that module is placed beside this one rather
                    // than inside it. `registerTypeDefs` keys a declaration to
                    // whichever module it is being read in, idempotently for a
                    // key that module already made — so read as this one's they
                    // would be keyed twice over and become a second type, which
                    // is exactly what a re-export must not produce.
                    //
                    // The spellings are the point of the exercise. The type's
                    // is published, because a facade may call it something of
                    // its own; every case's is derived from the declaring
                    // module's name, because a case follows its type and two
                    // spellings of one fact would be a pair that can disagree.
                    // Exporting a union exports its cases, and this is the same
                    // rule one module further along: a union that arrived
                    // without them would be a type nothing could take apart.
                    let reExportedGroups =
                        meta.ReExportedTypes
                        |> List.map (fun (entry: ModuleMetadata.ReExportedType) ->
                            entry, (Lexer.tokenize absPath entry.Decl |> read |> fst |> DeclParser.parseModule))
                        |> List.filter (fun (_, decls) -> not decls.IsEmpty)

                    let reExportedSpellings =
                        reExportedGroups
                        |> List.collect (fun (entry, decls) ->
                            let bare = Naming.bareTypeName entry.OriginModule
                            let r = Ast.declRange (List.head decls)

                            // The cases, and the type itself for a record or
                            // a newtype, which is constructed by its name.
                            let constructors =
                                decls
                                |> List.collect (function
                                    | DType(tds, _)
                                    | DTypeRec(tds, _) ->
                                        tds
                                        |> List.collect (fun td ->
                                            match td.Kind with
                                            | Union cs ->
                                                cs
                                                |> List.map (function
                                                    | SimpleCase(n, _) -> bare n, n
                                                    | DataCase(n, _, _) -> bare n, n)
                                            | Record _
                                            | Newtype _ when td.Name = entry.Key -> [ entry.Name, entry.Key ]
                                            | Record _
                                            | Newtype _
                                            | Alias _ -> [])
                                    | _ -> [])

                            DImportAlias(entry.Name, entry.Key, AliasType, r)
                            :: (constructors
                                |> List.map (fun (visible, n) -> DImportAlias(visible, n, AliasConstructor, r))))

                    // Declarations of other modules' types this assembly's
                    // signatures name, read the way a re-exported one is and
                    // offered under no spelling at all: what they are for is
                    // a value of the type, not a name for it.
                    let carriedGroups =
                        meta.CarriedTypes
                        |> List.map (fun (entry: ModuleMetadata.ReExportedType) ->
                            entry, (Lexer.tokenize absPath entry.Decl |> read |> fst |> DeclParser.parseModule))
                        |> List.filter (fun (_, decls) -> not decls.IsEmpty)

                    let carriedDecls =
                        reExportedGroups @ carriedGroups
                        |> List.groupBy (fun (entry, _) -> entry.OriginModule)
                        |> List.map (fun (originModule, group) ->
                            let decls = group |> List.collect snd
                            DModule(originModule, decls, Ast.declRange (List.head decls)))

                    // An exported binding becomes an extern: a name with a type
                    // and no body, which is exactly what an importer can say
                    // about it. The signature is rebuilt as source because a
                    // type is stored as the syntax it was written in, and there
                    // is one parser for that.
                    let externDecls =
                      Timing.phase "import: signatures" (fun () ->
                        meta.Defs
                        |> List.collect (fun d ->
                            let text =
                                if d.ConstraintsText = "" then
                                    $"(: %s{d.Name} %s{d.TypeText})"
                                else
                                    $"(: %s{d.Name} %s{d.TypeText} %s{d.ConstraintsText})"

                            Lexer.tokenize absPath text
                            |> read
                            |> fst
                            |> DeclParser.parseModule
                            |> List.map (function
                                // The visible name is the one this module
                                // publishes, and that is the point of parsing
                                // once per path: the module as it describes
                                // itself, before any importer's modifiers.
                                //
                                // The origin is elsewhere only when this module
                                // was a facade for the name — it then generated
                                // no code for it, and an importer has to be
                                // pointed at the module that did.
                                | DSignature(name, t, constraints, r) ->
                                    let origin =
                                        match d.Origin with
                                        | Some(originModule, originalName) ->
                                            { OriginModule = originModule
                                              OriginalName = originalName
                                              Kind = AliasDef
                                              IsMethod = d.IsMethod
                                              IsMutable = d.IsMutable }
                                        | None ->
                                            { OriginModule = ""
                                              OriginalName = name
                                              Kind = AliasDef
                                              IsMethod = d.IsMethod
                                              IsMutable = d.IsMutable }

                                    // An implicit export is declared under its
                                    // qualified reference, which no import edge
                                    // offers and source cannot write: it is
                                    // there for the published bodies naming it,
                                    // and `AlphaRename.reachHidden` points them
                                    // at it, whatever the importer calls its
                                    // own things.
                                    let declared =
                                        if d.IsImplicit then
                                            let home =
                                                if origin.OriginModule = "" then
                                                    Naming.moduleKeyOfPath absPath
                                                else
                                                    origin.OriginModule

                                            Naming.qualifiedBinding home origin.OriginalName
                                        else
                                            name

                                    DExtern(declared, origin, t, constraints, r)
                                | other -> other)
                            // After the extern, as a `(:deprecated ...)` comes
                            // after the definition it names.
                            |> List.collect (fun decl ->
                                match decl with
                                | DExtern(declared, _, _, _, r) when d.Deprecated <> "" ->
                                    [ decl; DDeprecated(declared, d.Deprecated, r) ]
                                | _ -> [ decl ])))

                    let parsedDecls =
                        typeSpellingDecls @ traitSpellingDecls @ reExportedSpellings @ declsFromText @ externDecls
                    // Macros are registered per import *edge*, not here: which
                    // name a transformer answers to is the importer's to say.
                    //
                    // No module dependencies: a DLL's transitive deps are
                    // link-only and never enter the module graph. Inline
                    // templates come last: registering one is meaningless
                    // until the trait and impl it belongs to exist.
                    let decls = parsedDecls @ inlineImplDecls

                    if cacheLoadedModules then
                        dllCache[cacheKey] <-
                            { Decls = decls
                              Facts = facts
                              Carried = carriedDecls
                              Macros = meta.Macros
                              PatternMacros = meta.PatternMacros
                              HashMacros = meta.HashMacros
                              Assembly = asm
                              // The resolved paths rather than the entries: a
                              // replay adds assemblies, and resolving a module
                              // name again would be the same answer at more
                              // cost.
                              Linked = absPath :: linkedDeps
                              Frameworks = meta.Frameworks }

                    decls, carriedDecls, [], meta.Macros, meta.PatternMacros, meta.HashMacros, Some asm
                else
                    // Reported rather than left to `File.ReadAllText`, whose
                    // `FileNotFoundException` is not a diagnostic and so prints
                    // a compiler stack trace at whoever mistyped a path.
                    if not (File.Exists absPath) then
                        failwithf
                            "Import Error: cannot find '%s'. Paths in an (import \"...\") resolve relative to the file doing the importing."
                            absPath

                    let sourceCode = Paths.readSource absPath
                    let forms, _ = Lexer.tokenize absPath sourceCode |> read
                    // Includes are spliced before anything looks at the forms, so
                    // an included file's own imports are picked up as this
                    // module's dependencies below.
                    let forms, _ = expandIncludes [ absPath ] absPath forms
                    // The implicit `(import (std prelude))` goes on *after*
                    // includes are spliced: an included file's forms land in
                    // this module, so this module's one import covers them.
                    let forms = withImplicitPrelude absPath forms

                    // After the includes, so that an included file's code is
                    // this module's.
                    let forms = forms |> List.map (stampModule (Naming.moduleKeyOfPath absPath))

                    // The docs leave here, and no parser sees them. Only the
                    // file being compiled has its docs checked: everything it
                    // imports is a `.dll`, whose docs were checked when it was
                    // built.
                    let docForms, forms = forms |> List.partition Docs.isDocForm

                    if absPath = Path.GetFullPath mainFilePath then
                        for f in docForms do
                            Diagnostics.recover "parse" (Some(getRange f)) () (fun () ->
                                mainDocs.Add(Docs.parseEntry f))

                    // Which of this module's own names are macros. Read off the
                    // S-expressions, like the imports: a `def/macro` is
                    // recognizable without parsing, and using one here has to
                    // be reported as what it is rather than as an unbound name.
                    let declaredHere (definer: string) =
                        forms
                        |> List.choose (function
                            | SList(SAtom { Token = Lexer.Symbol d }
                                    :: SList(SAtom { Token = Lexer.Symbol name } :: _, _)
                                    :: _,
                                    _) when d = definer -> Some name
                            | _ -> None)
                        |> Set.ofList

                    let localMacros = declaredHere "def/macro"
                    let localPatternMacros = declaredHere "def/pattern"
                    let localHashMacros = declaredHere "def/hash-extend"

                    // Resolved *and built*: an imported `.bjo` becomes a `.dll`
                    // here, so every edge in the graph names a compiled unit and
                    // the topological sort below keys on the same paths.
                    //
                    // A module under a `_` directory is refused before it is
                    // built (`Paths.importRefusal`). A REPL entry may import
                    // anything: it is for looking around.
                    let importEdges =
                        importsOf forms
                        |> List.map (fun (spec, r) ->
                            if not TypeEnv.compilingReplEntry then
                                match resolveImport false absPath spec with
                                | Ok p ->
                                    match Paths.importRefusal absPath p with
                                    | Some why -> failwithf $"Import Error at %s{Lexer.formatPos r}: %s{why}"
                                    | None -> ()
                                | Error _ -> ()

                            match resolveDependency absPath spec with
                            | Ok p -> Path.GetFullPath p, spec, r
                            | Error why -> failwithf $"Import Error at %s{Lexer.formatPos r}: %s{why}")

                    // Dependencies are loaded *before* this module is parsed.
                    //
                    // This is the whole reordering: a dependency's `.dll` is
                    // what says which of its names are macros, and the parser
                    // has to know that at the moment it meets one in head
                    // position. Parsing first and collecting `DImport`s
                    // afterwards cannot work, however the expander is written.
                    // What each import offers this module under each name, as
                    // the thing the name stands for. For the ambiguity check.
                    let offers = ResizeArray<Diagnostics.NameSpace * string * string * string * bool>()
                    // Each edge's module, surface and modifiers, for the names
                    // an `only` or an `except` leaves out.
                    let edgeSurfaces = ResizeArray<string * ImportSurface * ImportModifier list>()

                    for (dep, spec, r) in importEdges do
                        load dep

                        // The modifiers belong to this edge, so the renaming is
                        // computed here rather than inside `load`, which is
                        // keyed by path and shared by every importer.
                        let m = resolvedModules[dep]
                        let surface =
                            surfaceOf m.ModuleName m.ParsedDecls m.Carried m.Macros m.PatternMacros m.HashMacros
                        let renaming = defRenaming r (Path.GetFileName dep) surface spec.Modifiers

                        if spec.Path <> preludePath then
                            for offer in offeredNames m.ModuleName m.ParsedDecls surface renaming spec.Modifiers do
                                offers.Add offer

                        edgeSurfaces.Add((m.ModuleName, surface, spec.Modifiers))

                        if not (edges.ContainsKey dep) then
                            edges[dep] <- ResizeArray()

                        edges[dep].Add(
                            renaming,
                            typeRenaming surface spec.Modifiers,
                            keptSpellings surface spec.Modifiers,
                            r
                        )

                        // A macro has to be in the table under the name this
                        // import gives it before the form using it is read.
                        match m.Assembly with
                        | Some asm ->
                            registerMacros
                                asm
                                m.Macros
                                m.PatternMacros
                                m.HashMacros
                                m.ParsedDecls
                                renaming
                                (hashRenaming surface spec.Modifiers)
                        | None -> ()

                    // `(:alias new old)` where `old` is a macro, for the same
                    // reason: the parser decides what a head symbol means at the
                    // moment it meets it, and by inference time every use has
                    // already been read as an ordinary call. An alias of an
                    // ordinary binding is not one of these and is left to the
                    // type checker.
                    for form in forms do
                        match form with
                        | SList([ SAtom { Token = Lexer.Keyword "alias" }
                                  SAtom { Token = Lexer.Symbol newName }
                                  SAtom { Token = Lexer.Symbol oldName } ],
                                _) ->
                            Macro.alias newName oldName |> ignore
                            Macro.aliasPattern newName oldName |> ignore
                            Macro.aliasHash newName oldName |> ignore
                        | _ -> ()

                    // Set immediately before parsing, and not earlier: loading a
                    // dependency parses *that* module, whose own macros are a
                    // different set.
                    Macro.setLocalMacros localMacros
                    Macro.setLocalPatternMacros localPatternMacros
                    Macro.setLocalHashMacros localHashMacros
                    // Saved and put back: parsing a module can load and parse
                    // another, and each answers `(current-module)` for itself.
                    let outerModuleKey = Parser.currentModuleKey
                    Parser.currentModuleKey <- Naming.moduleKeyOfPath absPath

                    // The names two imports offer as different things, before
                    // parsing, because a macro is expanded while the module is
                    // parsed. This module's own macros are its own.
                    let ownKey = Naming.moduleKeyOfPath absPath

                    // At the prompt a later entry's definition takes the place
                    // of an earlier one's, and the later import's spelling is
                    // filed last, so a name only earlier entries offer is no
                    // ambiguity. Two of them are linked at once when both are
                    // linked unconditionally, as an entry declaring a trait is.
                    let ambiguous =
                        let fromEntries (providers: string list) =
                            providers |> List.forall (fun p -> p.StartsWith Naming.replEntryModulePrefix)

                        ambiguousNames offers
                        |> Map.filter (fun _ (providers, _) ->
                            not (TypeEnv.compilingReplEntry && fromEntries providers))

                    let setAmbiguous (own: Set<string>) =
                        let kept =
                            ambiguous |> Map.filter (fun (_, name) _ -> not (Set.contains name own))

                        Diagnostics.ambiguousImports <-
                            if kept.IsEmpty then
                                Map.remove ownKey Diagnostics.ambiguousImports
                            else
                                Map.add ownKey kept Diagnostics.ambiguousImports

                    setAmbiguous (Set.unionMany [ localMacros; localPatternMacros; localHashMacros ])

                    Diagnostics.withheldNames <-
                        match withheldNames edgeSurfaces with
                        | w when w.IsEmpty -> Map.remove ownKey Diagnostics.withheldNames
                        | w -> Map.add ownKey w Diagnostics.withheldNames

                    let parsed =
                        try
                            DeclParser.parseModule forms
                        finally
                            Parser.currentModuleKey <- outerModuleKey

                    // And a name this module defines wins over an import (rule
                    // 1), so it is no ambiguity.
                    setAmbiguous (Set.unionMany [ localMacros; localPatternMacros; localHashMacros; ownNames parsed ])

                    Macro.setLocalMacros Set.empty
                    Macro.setLocalPatternMacros Set.empty
                    Macro.setLocalHashMacros Set.empty

                    // Only the file being compiled has an entry point. A `main`
                    // in a module this one imports is one of its functions.
                    let parsed =
                        if absPath = Path.GetFullPath mainFilePath then
                            shapeEntryPoint parsed
                        else
                            parsed

                    // Every type is written, where `Written` can be seen: in a
                    // module of the prelude's, and in the prelude itself.
                    let parsed =
                        let isPrelude =
                            isStandardLibrary absPath
                            && Path.GetFileName absPath = "prelude.bjo"
                            && Path.GetFileName(Path.GetDirectoryName absPath) = "std"

                        if isPrelude || importsOf forms |> List.exists (fun (s, _) -> s.Path = preludePath) then
                            TypeSyntax.withWrittenImpls parsed
                        else
                            parsed

                    parsed, [], (importEdges |> List.map (fun (dep, _, _) -> dep)), [], [], [], None

            // Dependencies were loaded above, before this module was parsed. A
            // `.dll` has none to load: its transitive deps are link-only and
            // never enter the module graph.

            let moduleName = Naming.moduleKeyOfPath absPath

            // Two files, one identity. It is what a shadowing package produces
            // when neither file is under the other's root, and what two
            // directories whose names differ only in `-` against `_` produce —
            // `identSegment` folds both to the same segment. Either way the
            // second module's declarations would be registered under the first
            // one's key, and the error is about names nobody wrote.
            match claimedKeys.TryGetValue moduleName with
            | true, other when other <> absPath ->
                failwithf
                    $"'%s{absPath}' and '%s{other}' are both the module %s{Naming.showTypeName moduleName}. Two files cannot share one module name: rename one, or take one of them out of the package that claims it."
            | _ -> claimedKeys[moduleName] <- absPath

            resolvedModules.[absPath] <- {
                FilePath = absPath
                ModuleName = moduleName
                Dependencies = deps
                ParsedDecls = parsedDecls
                Carried = carriedDecls
                Macros = macros
                PatternMacros = patternMacros
                HashMacros = hashMacros
                Assembly = assembly
            }
            currentPath.Remove(absPath) |> ignore

    load mainFilePath
    
    // Sort topologically
    let sorted = System.Collections.Generic.List<LoadedModule>()
    let visited = System.Collections.Generic.HashSet<string>()
    let rec visit (path: string) =
        if not (visited.Contains(path)) then
            visited.Add(path) |> ignore
            let m = resolvedModules.[path]
            for dep in m.Dependencies do
                visit dep
            // A `.dll` comes after the modules of this graph it was built
            // against, though it does not import them here: what it publishes
            // may name their traits, as every type's `Written` implementation
            // names the prelude's, whatever order the importer wrote.
            match linkedOf.TryGetValue path with
            | true, linked ->
                for dep in linked do
                    if resolvedModules.ContainsKey dep then visit dep
            | _ -> ()
            sorted.Add(m)

    visit (Path.GetFullPath(mainFilePath))

    // Every spelling an import made visible: its namespace, the name, where it
    // came from, whether a modifier invented it, and the import that did.
    // Rule 5 is decided over this once every module's edges are known.
    let spellings =
        ResizeArray<Diagnostics.NameSpace * string * string * string * bool * Lexer.Range>()

    /// A module's declarations as its importers see them.
    ///
    /// The main module has no importer and is left exactly as parsed.
    let viewOf (m: LoadedModule) : Decl list =
        match edges.TryGetValue m.FilePath with
        | true, edgeList ->
            let merged =
                edgeList
                |> Seq.collect (fun (renaming, _, _, r) ->
                    renaming |> Map.toSeq |> Seq.map (fun (original, visible) -> original, visible, r))
                |> Seq.distinctBy (fun (original, visible, _) -> original, visible)
                |> List.ofSeq

            // The spellings for what is not a binding. They are declarations of
            // their own rather than a rewriting, because the declaration that
            // introduces the name has to keep it.
            let typeSpellings =
                edgeList
                |> Seq.collect (fun (_, types, _, r) ->
                    types |> Seq.map (fun (original, visible, kind) -> original, visible, kind, r))
                |> Seq.distinctBy (fun (original, visible, kind, _) -> original, visible, kind)
                |> List.ofSeq

            for (original, visible, r) in merged do
                spellings.Add(Diagnostics.ValueName, visible, m.ModuleName, original, visible <> original, r)

            // Reported under the name source writes rather than the key, which
            // is what the reader of the collision message is looking at. A key
            // this module did not make is one it re-exported, and there is no
            // prefix of this module's to take off it — what a reader can act on
            // is then the whole thing, module and all.
            for (key, visible, kind, r) in typeSpellings do
                let readable =
                    match kind with
                    | AliasTrait
                    | AliasMethod -> Naming.showTraitName key
                    | _ ->
                        let bare = Naming.bareTypeName m.ModuleName key
                        if bare = key then Naming.showTypeName key else bare

                let nameSpace =
                    match kind with
                    | AliasType -> Diagnostics.TypeName
                    | AliasTrait -> Diagnostics.TraitName
                    | AliasDef
                    | AliasMacro
                    | AliasConstructor
                    | AliasMethod -> Diagnostics.ValueName

                spellings.Add(nameSpace, visible, m.ModuleName, readable, true, r)

            let byOriginal =
                merged
                |> List.groupBy (fun (original, _, _) -> original)
                |> List.map (fun (original, g) -> original, g |> List.map (fun (_, visible, _) -> visible))
                |> Map.ofList

            let aliasDecls =
                typeSpellings
                |> List.map (fun (original, visible, kind, r) -> DImportAlias(visible, original, kind, r))

            // The bare names of the module's types, which the `.dll` reader
            // offers as spellings of their keys. A bare name is kept when an
            // edge keeps it: no edge spells the types its own way, and no
            // `only` or `except` leaves the type out.
            let keptKeys = edgeList |> Seq.map (fun (_, _, kept, _) -> kept) |> Set.unionMany

            let keptDecls =
                m.ParsedDecls
                |> List.filter (function
                    | DImportAlias(_, key, (AliasType | AliasConstructor | AliasTrait | AliasMethod), _) ->
                        Set.contains key keptKeys
                    | _ -> true)

            aliasDecls @ applyDefRenaming m.ModuleName byOriginal keptDecls
        | _ -> m.ParsedDecls

    // The implementations of the modules this compilation links and does not
    // import. A type reaches a module through one that passes it on, and its
    // implementations live with it or with their traits (see
    // `CheckDecl.refuseOrphan`): a module holding a value of the type, and
    // importing neither, would take the trait's blanket at it. They are read
    // just before the module being compiled, once every trait and type they
    // could name is registered. Only the implementations come: the linked
    // modules' names stay out of scope.
    let linkedImpls =
        let loaded = resolvedModules.Keys |> Set.ofSeq

        dllDeps
        |> Seq.filter (fun dll -> not (Set.contains (Path.GetFullPath dll) loaded))
        |> List.ofSeq
        |> List.choose (fun dll ->
            let _, meta = metadataOf dll
            let moduleKey = Naming.moduleKeyOfPath dll

            let decls =
                if meta.ImplDecls.IsEmpty then
                    []
                else
                    Lexer.tokenize dll (String.concat "\n" meta.ImplDecls)
                    |> read
                    |> fst
                    |> List.map (readOrigins moduleKey)
                    |> DeclParser.parseModule
                    |> List.map DLinked

            match decls with
            | [] -> None
            | first :: _ -> Some(DModule(moduleKey, decls, declRange first)))

    let allDecls =
        let groups =
            sorted
            |> Seq.map (fun m ->
                // What a module carried stands beside it and ahead of it: those
                // declarations belong to the module that made them, so they are
                // registered as its, and the module re-exporting them is checked
                // after they exist. A module reached both directly and through a
                // facade therefore has its types registered twice, under one key
                // and with one meaning, which is the whole point of the key.
                m.Carried @ wrapInModule m.ModuleName m.FilePath (viewOf m))
            |> List.ofSeq

        // The module being compiled is the last one sorted.
        match List.rev groups with
        | compiled :: dependencies -> List.concat (List.rev dependencies) @ linkedImpls @ compiled
        | [] -> linkedImpls

    // Rule 5. Only a spelling a modifier *invented* is checked: two plain
    // imports offering the same name is the older shadowing rule, where the
    // later import wins, and widening this to cover it would reject programs
    // that have always compiled.
    for ((_, visible), group) in spellings |> Seq.groupBy (fun (ns, v, _, _, _, _) -> ns, v) do
        let origins =
            group
            |> Seq.map (fun (_, _, originModule, original, _, _) -> originModule, original)
            |> Seq.distinct
            |> List.ofSeq

        if origins.Length > 1 && group |> Seq.exists (fun (_, _, _, _, renamed, _) -> renamed) then
            let (_, _, _, _, _, r) = group |> Seq.find (fun (_, _, _, _, renamed, _) -> renamed)
            let describe (m: string, n: string) = $"'%s{n}' from %s{Naming.moduleNameOfPath m}"
            let both = origins |> List.map describe |> String.concat " and "

            failwithf
                $"Import collision at %s{Lexer.formatPos r}: '%s{visible}' would name %s{both}. A modifier or (:alias ...) that produces a name another import already produces is an error, not a shadowing."

    allDecls,
    dllDeps |> Seq.toList,
    Set.ofSeq blockingDefs,
    Set.ofSeq doubleDefs,
    constrainedBodies |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq,
    List.ofSeq reExportedTraits,
    List.ofSeq mainDocs

/// Which module each top-level name belongs to.
///
/// Built from the typed program rather than from the environment, because the
/// environment says only *that* a name is bound. A name reached through an
/// imported `.dll` arrives as a `TExtern` inside that dll's module, which is
/// exactly the answer wanted for a helper the origin module itself imported
/// from a third module.
///
/// The answer is the module *and* the name that module knows it by, which
/// differ for an import brought in under a modifier: the qualified reference
/// has to spell the original, since that is what the origin's class defines.
///
/// An `import/extern` alias takes its name over from an import, so it removes
/// the name: the alias has no module class of its own, and a copy of a body
/// that calls it has to leave the name as written. Qualified to the import, the
/// copy called the import's function.
let private moduleOfName (decls: TypedAST.TDecl list) : Map<string, string * string> =
    decls
    |> TypedAST.collectDecls (function
        | TypedAST.TModule(modName, inner, _) ->
            inner
            |> List.collect (function
                | TypedAST.TDef(n, _, _, _) -> [ n, Some(modName, n) ]
                | TypedAST.TDefMutable(n, _, _, _) -> [ n, Some(modName, n) ]
                | TypedAST.TDefun(n, _, _, _, _, _, _, _, _) -> [ n, Some(modName, n) ]
                | TypedAST.TExtern(visible, origin, _, _) ->
                    [ visible, Some(origin.OriginModule, origin.OriginalName) ]
                | TypedAST.TImportExtern(infos, _) -> infos |> List.map (fun info -> info.Alias, None)
                | _ -> [])
        | _ -> [])
    |> List.fold
        (fun acc (name, origin) ->
            match origin with
            | Some o -> Map.add name o acc
            | None -> Map.remove name acc)
        Map.empty

/// Works out what each local inline template's free variables should be emitted
/// as, now that the whole program has been checked.
///
/// This cannot be done before inference — `infer` fails hard on unbound names
/// and `Origin_Module::helper` is not one — and it cannot be skipped for local
/// impls either. Without it, a body that calls a module-level `helper`, inlined
/// into a caller that happens to have a local named `helper`, emits a bare
/// `helper` that binds to the local.
let private qualifyInlineTemplates (moduleOf: Map<string, string * string>) (env: TypedAST.Env) : TypedAST.Env =
    let qualified =
        env.Registry.InlineMethods
        |> Map.map (fun _ (tpl: TypedAST.InlineTemplate) ->
            // A template read back from a `.dll` was qualified where it was
            // written, by a compilation that could see its module's imports,
            // and its body is not read for this: it is read when it is used.
            if tpl.Imported || not (Map.isEmpty tpl.Qualification) then
                tpl
            else
                let free = AlphaRename.freeNames (Set.ofList tpl.Params) tpl.Body.Value

                let qualification =
                    free
                    |> Seq.choose (fun n ->
                        // Anything with no module class of its own — a data
                        // constructor, a `Prelude` binding, a trait method — is
                        // left exactly as written. There is nothing to qualify
                        // it to.
                        match Map.tryFind n moduleOf with
                        | Some(m, original) -> Some(n, Naming.qualifiedBinding m original)
                        | None -> None)
                    |> Map.ofSeq

                { tpl with Qualification = qualification })

    { env with
        Registry = { env.Registry with InlineMethods = qualified } }

/// What each name an import binds ends up bound to, before inference: the
/// origin of the last extern for it, in the order the modules are checked,
/// which is the entry `checkExtern` leaves in `ImportAliases`. The module
/// being compiled is last and imports nothing of its own, so it is left out.
let private importOrigins (decls: Decl list) : Map<string, string * string> =
    let dependencies = decls |> List.truncate (max 0 (List.length decls - 1))

    dependencies
    |> List.fold
        (fun acc d ->
            match d with
            | DModule(moduleName, inner, _) ->
                inner
                |> List.fold
                    (fun acc d ->
                        match d with
                        | DExtern(visible, origin, _, _, _) ->
                            let m = if origin.OriginModule = "" then moduleName else origin.OriginModule
                            Map.add visible (m, origin.OriginalName) acc
                        | _ -> acc)
                    acc
            | _ -> acc)
        Map.empty

/// The frontend, from the source file to the tree code generation emits.
///
/// `codegen` false is a check, which wants the diagnostics and not the tree:
/// the passes that only make the tree fit to emit, which cannot report
/// anything, are left out. Every pass that can report is still run, the
/// lowerings included, since the colour check reads the program as it will be
/// emitted.
let runFrontend (codegen: bool) (mainFilePath: string) =
    try
        Diagnostics.progress "=== Step 1: Parsing & Module Resolution ==="
        let parsedModuleDecls, dllDeps, importedBlocking, importedDoubles, importedBodies, reExportedTraits, docEntries =
            Timing.phase "parse + module graph" (fun () -> loadModuleGraph mainFilePath)

        // A dependency compiled while the graph was loaded has published its
        // own docs and emitted them; nothing of them belongs to this module.
        Docs.published <- ""

        // The macros *this* compilation publishes. The main module is last, and
        // is the only one being compiled — everything before it arrived as a
        // `.dll` and already declared its own macros in its own metadata.
        //
        // Read here rather than after type checking, because `DMacro` does not
        // survive it: a macro is checked as the `defun` it also produced.
        let declaredMacros, declaredPatternMacros, declaredHashMacros =
            match List.tryLast parsedModuleDecls with
            | Some(DModule(_, decls, _)) ->
                decls |> List.choose (function DMacro(n, _) -> Some n | _ -> None),
                decls |> List.choose (function DPatternMacro(n, _) -> Some n | _ -> None),
                decls |> List.choose (function DHashMacro(n, _) -> Some n | _ -> None)
            | _ -> [], [], []

        // The parse gate. A form that failed to parse contributed no
        // declarations, so what follows would type check a module with holes in
        // it and report an unknown name for every use of everything that was
        // dropped. Those reports would be about the parse error, and would not
        // say so.
        if Diagnostics.hasErrors () then
            Diagnostics.report ()
            None
        else

        // Before any pass rewrites the module: a binding is then the one the
        // program wrote.
        match List.tryLast parsedModuleDecls with
        | Some(DModule(_, decls, _)) -> Unused.warn (not TypeEnv.compilingReplEntry) decls
        | _ -> ()

        Diagnostics.progress "=== Step 2: Normalization ==="
        // First of the source-to-source passes, and before `LetRecify` on
        // purpose: an applied lambda reduced into a `let` chain here is one
        // fewer closure for every pass after it to carry, and the bindings it
        // leaves behind are what `LetRecify` orders.
        let normalizedDecls =
            Timing.phase "normalize" (fun () -> Normalize.normalizeModule parsedModuleDecls)

        let letrecifiedDecls = Timing.phase "letrecify" (fun () -> letrecifyModule normalizedDecls)

        Diagnostics.progress "=== Step 3: Type Checking ==="

        // A dependency's `defbjouble`s have to be in the registry *before*
        // inference, not after: generating a suspending copy for every `defun`
        // that reaches one is a pass over the declarations, and the port surface
        // it reaches lives entirely behind this module boundary. An empty seed
        // set here would mean no module but the prelude ever generated a copy.
        //
        // The twin's name is derived rather than published — one function
        // decides it on both sides, so the two cannot drift.
        //
        // A claim an import makes about a name — that it is a pair, or that it
        // parks — holds only while the name ends up bound to what the claim is
        // about: a later import's plain `read-line` takes the name over from
        // the prelude's, and a call to it is neither to be pointed at the
        // prelude's `read-line__bjo` nor reported as parking.
        let origins = importOrigins parsedModuleDecls

        let stillBound (name: string, origin: string * string) =
            match Map.tryFind name origins with
            | Some bound -> bound = origin
            | None -> true

        let importedDoublePairs =
            importedDoubles
            |> Seq.filter stillBound
            |> Seq.map (fun (name, _) -> name, Naming.suspendingCopy name)
            |> Map.ofSeq

        // Which of the dependencies' exports park a thread goes in here too,
        // joined to what the builtins say about themselves. Nothing in
        // inference reads it, but `checkModule` drops the names this module
        // takes over with definitions of its own, as it drops their
        // `DoubleDefs` entries: a claim about an import's body is not one about
        // this module's.
        let startEnv =
            { Prelude.prelude with
                Registry =
                    { Prelude.prelude.Registry with
                        DoubleDefs =
                            importedDoublePairs
                            |> Map.fold (fun acc k v -> Map.add k v acc) Prelude.prelude.Registry.DoubleDefs
                        BlockingNames =
                            importedBlocking
                            |> Set.filter stillBound
                            |> Set.map fst
                            |> Set.union Prelude.prelude.Registry.BlockingNames } }

        let env, typedAst =
            Timing.phase "type check" (fun () -> Inference.checkProgram startEnv letrecifiedDecls)

        // The dependencies' constrained bodies, added after inference because
        // nothing in inference reads a body's source, and `Monomorphise` below
        // is the only thing that does. The pairs and the blocking names came in
        // with `startEnv` and are not joined again: that would put back the
        // names this module took over.
        //
        // A trait a dependency re-exported was registered as that dependency's,
        // because its declaration was read from there; it is put back with the
        // module that declared it. Only an entry still naming the re-exporter
        // is touched: a trait of that name declared since is somebody else's.
        // Nothing in inference minds the interim answer, since the one question
        // it asks is whether a trait is the module being compiled's own, and
        // that module never reads its own metadata.
        let env =
            { env with
                Registry =
                    { env.Registry with
                        ConstrainedBodies =
                            importedBodies
                            |> Map.fold (fun acc k v -> Map.add k v acc) env.Registry.ConstrainedBodies
                        TraitOrigins =
                            reExportedTraits
                            |> List.fold
                                (fun acc (facade, traitName, origin) ->
                                    if Map.tryFind traitName acc = Some facade then
                                        Map.add traitName origin acc
                                    else
                                        acc)
                                env.Registry.TraitOrigins } }

        // Before anything reads `main`: the entry point is generated code's
        // caller, and a type it cannot call is a diagnostic here rather than a
        // C# error in a file nobody wrote.
        checkEntryPoint env parsedModuleDecls

        // Which module each top-level name belongs to. Read once: both a spliced
        // trait method and a specialised constrained body have to keep meaning
        // what they meant where they were written, and both ask this the same
        // question.
        let moduleOf = moduleOfName typedAst

        let env = qualifyInlineTemplates moduleOf env

        // Before inlining, and deliberately: `spliceTemplate` is best-effort, so
        // a check after it would report on a spliced body but not on the same
        // body reached through a landing pad — the same program, different
        // errors depending on inliner luck. See the module docstring and §8.3.
        MustUse.run env.Registry typedAst

        // Beside `MustUse` and for its reason: the program as written is what
        // the reader is owed a report about, and a monomorphised copy would be
        // reported on once per instantiation under a name nobody wrote.
        Exhaustiveness.run env.Registry typedAst

        // Beside them and for their reason: a hoisted form is reported on once,
        // as written, with every reason it cannot be hoisted.
        HoistCheck.run env.Registry env.Bindings typedAst

        // After type checking, which is when what a name is — and whether a
        // name in `(see ...)` exists at all — can be asked; before the gate,
        // so that a doc that does not match its definition stops the build.
        //
        // Published here rather than by `Exports`, which only a library runs:
        // a program's docs are its own and go into its assembly too.
        match List.tryLast parsedModuleDecls with
        | Some(DModule(moduleKey, mainDecls, _)) ->
            Docs.check env moduleKey mainDecls docEntries
            Docs.published <- Docs.publish mainDecls docEntries
        | _ -> Docs.published <- ""

        // The type-check gate. Everything from here down rewrites the checked
        // tree, and a declaration that failed to check contributed none of it:
        // `Monomorphise` reads bodies it would not find, and the placeholder
        // bindings a failure leaves behind make the declarations after it agree
        // with something nobody wrote. Neither is worth a second diagnostic,
        // and neither may reach codegen.
        if Diagnostics.hasErrors () then
            Diagnostics.report ()
            None
        else

        Diagnostics.progress "=== Step 3.5: Monomorphisation ==="
        // Before trait inlining, and for the sake of it: a copy checked at a
        // concrete type has its trait calls resolved, so the inliner splices
        // them where the generic original would have dispatched through a
        // dictionary. After `MustUse`, so that a copy is not reported on twice
        // under a name nobody wrote.
        //
        // It takes the untyped declarations as well as the checked ones,
        // because the copy is generated as source. See the module docstring.
        let env, typedAst =
            Timing.phase "monomorphise" (fun () -> Monomorphise.run env moduleOf letrecifiedDecls typedAst)

        Diagnostics.progress "=== Step 4: Trait Inlining ==="
        // Before dictionary lowering, so that the dictionary pass sees the
        // inlined result and handles any interface-trait dispatch inside it with
        // no changes; and before loop lowering, because a `TRecur` carries an
        // index into its enclosing loop and cannot be spliced elsewhere.
        //
        // The bodies of this module's functions that take a function are
        // published first, so that a call in this module can be inlined too,
        // and so that `Exports` publishes them to importers.
        let env = TraitInline.publishBodies env moduleOf letrecifiedDecls typedAst
        let inlinedAst = Timing.phase "trait inline" (fun () -> TraitInline.run env moduleOf typedAst)

        Diagnostics.progress "=== Step 5: Seq Fusion ==="
        // Runs after trait inlining because that is what puts a `seql` default
        // where the loop walking it can be fused with it, and before both
        // lowerings because a fused loop is still a letrec of tail calls,
        // which is what `LoopLowering` needs in order to emit it inline.
        let fusedAst = Timing.phase "seq fusion" (fun () -> SeqFusion.run inlinedAst)

        // Before dictionary lowering, which turns a match into type tests that
        // no longer name the case they test for.
        let simplifiedAst = Timing.phase "simplify" (fun () -> Simplify.run env fusedAst)

        Diagnostics.progress "=== Step 6: Dictionary Lowering ==="
        let loweredAst = Timing.phase "dictionary lowering" (fun () -> Lowering.lowerProgram env simplifiedAst)

        Diagnostics.progress "=== Step 7: Loop Lowering ==="
        let loopLoweredAst = Timing.phase "loop lowering" (fun () -> LoopLowering.lowerProgram loweredAst)

        // A `(loop ...)` is a loop by construction, but promotion is a silent
        // optimization and a desugaring bug would leave real calls behind —
        // correct, and unable to iterate deeply. Checked rather than assumed.
        LoopLowering.assertLoopsPromoted loopLoweredAst

        // Only now is it known which named `let`s stayed functions, and an
        // escape out of one of those cannot be emitted.
        LoopLowering.checkEscapesFromFunctions loopLoweredAst

        // After loop lowering, and for that reason: whether a yield point sits
        // inside a C# member of its own is only decided once a named `let` has
        // either become a `while` or stayed a local function. See the module
        // docstring.
        // Before `ColourCheck`, because it is what decides which copy of a
        // `defbjouble` each call means — and after loop lowering, because
        // whether a call sits somewhere it may await is the same question
        // `ColourCheck` asks and has the same answer.
        let loopLoweredAst = EffectGraph.selectDoubles env.Registry loopLoweredAst

        // Before anything reads the registry's doubles: an idle copy is neither
        // emitted nor published, so importers are not copied for it.
        let loopLoweredAst, idleCopies = EffectGraph.pruneIdleCopies env.Registry loopLoweredAst

        let env =
            if idleCopies.IsEmpty then
                env
            else
                { env with
                    Registry =
                        { env.Registry with
                            DoubleDefs = env.Registry.DoubleDefs |> Map.filter (fun _ copy -> not (Set.contains copy idleCopies))
                            GeneratedCopies = Set.difference env.Registry.GeneratedCopies idleCopies
                            InferredCopies = Set.difference env.Registry.InferredCopies idleCopies } }

        ColourCheck.run env.Registry loopLoweredAst

        // Beside `ColourCheck`: whether a span is captured or held across a
        // yield point is decided by the same lowering.
        Timing.phase "span check" (fun () -> SpanCheck.run env.Registry loopLoweredAst)

        // Beside `ColourCheck` and for the same reason: both are about what a
        // body does with the thread it is on, and both want the program as it
        // will be emitted. This one only warns — parking is legal, and the
        // point is that it is otherwise invisible.
        EffectGraph.lint env.Registry loopLoweredAst

        // After inlining, which can leave a lambda as a local's value, and
        // after the checks, so that their messages describe the program as
        // written. Before the renaming below, which removes any shadowing that
        // moving a local out of a binding's value makes.
        let loopLoweredAst =
            if codegen then
                Timing.phase "local function promotion" (fun () -> LocalFunPromotion.run loopLoweredAst)
            else
                loopLoweredAst

        // Last, and a cleanup pass only: C# rejects a local that shadows an
        // enclosing one, and every pass above is free to produce that. A check
        // emits no C#.
        let uniquifiedAst =
            if codegen then
                Timing.phase "alpha rename" (fun () -> AlphaRename.uniquifyProgram Prelude.builtinNames loopLoweredAst)
            else
                loopLoweredAst

        Diagnostics.progress "=== Frontend pipeline complete ==="
        // The gates above answer `None` for a program that had an error, so a
        // program arriving here with a placeholder binding in its environment is
        // one that got past them.
        Diagnostics.assertNoPoison ()
        // On the way out of every path, including this one: a compilation that
        // succeeded still has warnings to pass on, and they are collected now
        // rather than printed where they were raised.
        Diagnostics.report ()
        Some (env, uniquifiedAst, dllDeps, declaredMacros, declaredPatternMacros, declaredHashMacros)
    with ex ->
        // Whatever ended the compilation joins what the phase gates already
        // collected, so that one report answers for the whole of it. A bug is
        // not a diagnostic and keeps its trace.
        if Diagnostics.isDiagnostic ex then
            Diagnostics.record (Diagnostics.ofException "frontend" None ex)
        else
            Diagnostics.reportFailure ex

        Diagnostics.report ()
        None

/// The frontend for code generation, every pass run.
let runFullFrontendPipeline (mainFilePath: string) = runFrontend true mainFilePath
