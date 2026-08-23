module Bjolang.Pipeline

open System
open System.IO
open Bjolang.Lexer
open Bjolang.Parser
open Bjolang.LetRecify

let unionLexerRanges (r1: Lexer.Range) (r2: Lexer.Range) : Lexer.Range =
    // The two ranges can come from different files once `include` is involved.
    // The opening one wins: it is where the form the caller is describing began.
    { Start = r1.Start; End = r2.End; File = r1.File }

let rec collectPositionalArgs (expr: SExpr) : Set<int> =
    match expr with
    | SAtom { Token = Lexer.Symbol sym } when sym.Length > 1 && sym.StartsWith("&") ->
        match System.Int32.TryParse(sym.Substring(1)) with
        | true, n when n > 0 -> Set.singleton n
        | _ -> Set.empty
    | SList(items, _) ->
        items |> List.map collectPositionalArgs |> Set.unionMany
    | _ -> Set.empty

let desugarMapLiteral (headRange: Lexer.Range) (entriesSList: SExpr) : SExpr =
    let listRange = getRange entriesSList
    let entries =
        match entriesSList with
        | SList(items, _) -> items
        | _ -> []

    if List.isEmpty entries then
        let mapEmptyToken = SAtom { Token = Lexer.Symbol "map-empty"; Range = headRange }
        SList([ mapEmptyToken ], listRange)
    else
        let parsePair entry =
            match entry with
            | SList([ k; v ], er) -> (k, v, er)
            | SList([ SAtom { Token = Lexer.Symbol "Tuple" }; k; v ], er) -> (k, v, er)
            | SList([ SAtom { Token = Lexer.Symbol "vec-literal" }; k; v ], er) -> (k, v, er)
            | bad ->
                let er = getRange bad
                failwithf
                    "Invalid map entry at %s. Expected (key value), [key value], or (key . value)"
                    (Lexer.formatPos er)

        let pairs = List.map parsePair entries
        let nilToken = SAtom { Token = Lexer.Symbol "Nil"; Range = listRange }
        let rec makeConsChain listPairs =
            match listPairs with
            | [] -> nilToken
            | (k, v, er) :: rest ->
                let tupleSExpr = SList([ SAtom { Token = Lexer.Symbol "Tuple"; Range = er }; k; v ], er)
                let restChain = makeConsChain rest
                let consToken = SAtom { Token = Lexer.Symbol "Cons"; Range = er }
                SList([ consToken; tupleSExpr; restChain ], er)

        let consChain = makeConsChain pairs
        let listMapToken = SAtom { Token = Lexer.Symbol "list->map"; Range = headRange }
        SList([ listMapToken; consChain ], listRange)

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

let rec read (tokens: LexedToken list) : SExpr list * LexedToken list =
    let isDot = function SAtom { Token = Dot } -> true | _ -> false

    /// Reads the body of a bracketed form, up to its closing bracket, and puts
    /// whatever `undotted` says at the head.
    ///
    /// With `dotMakesTuple`, a dot anywhere in the body makes the form a tuple
    /// regardless of how it was introduced. Only `(...)` says so: inside `[...]`
    /// and `{...}` a dot is an ordinary element, because a vec literal and a
    /// comprehension are not spellings of a tuple.
    ///
    /// `startRange` opens the form and `rangeFrom` opens the range the result
    /// spans — they differ for a quoted list, where the quote comes first.
    let readForm
        (dotMakesTuple: bool)
        (startRange: Lexer.Range)
        (rangeFrom: Lexer.Range)
        (undotted: SExpr list -> SExpr list)
        rest
        =
        let innerNodes, afterList = read rest
        let endRange = if List.isEmpty afterList then startRange else (List.head afterList).Range
        let listRange = unionLexerRanges rangeFrom endRange

        let finalNodes =
            if dotMakesTuple && List.exists isDot innerNodes then
                let tupleToken = { Token = Lexer.Symbol "Tuple"; Range = startRange }
                SAtom tupleToken :: List.filter (not << isDot) innerNodes
            else
                undotted innerNodes

        SList(finalNodes, listRange), afterList

    /// Prepends a reserved head — `vec-literal`, `comprehension` — to a body.
    let headed (name: string) (r: Lexer.Range) (innerNodes: SExpr list) =
        SAtom { Token = Lexer.Symbol name; Range = r } :: innerNodes

    // Every level ends at one of these four, so collapsing here covers all of
    // them and nothing else has to know about `#'`.
    let rec loop acc remaining =
        match remaining with
        | [] -> collapseSynQuote (List.rev acc), []
        | { Token = RParen } :: rest -> collapseSynQuote (List.rev acc), rest
        | { Token = RBracket } :: rest -> collapseSynQuote (List.rev acc), rest
        | { Token = RBrace } :: rest -> collapseSynQuote (List.rev acc), rest

        // Quoted list: '(items...) → (quoted-list items...)
        | { Token = Quote; Range = qr } :: { Token = LParen; Range = r } :: rest ->
            let node, afterList = readForm true r qr (headed "quoted-list" qr) rest
            loop (node :: acc) afterList

        // Function shorthand: #(+ &1 &2 5) → (fun (&1 &2) (+ &1 &2 5))
        | { Token = Hash; Range = hr } :: { Token = LParen; Range = r } :: rest ->
            let bodySList, afterList = readForm true r hr id rest
            let argIndices = collectPositionalArgs bodySList
            let maxArg = if Set.isEmpty argIndices then 0 else Set.maxElement argIndices
            let paramNames = [ for i in 1 .. maxArg -> $"&{i}" ]
            let funToken = SAtom { Token = Lexer.Symbol "fun"; Range = hr }
            let paramList = SList(paramNames |> List.map (fun p -> SAtom { Token = Lexer.Symbol p; Range = hr }), hr)
            let lambdaSExpr = SList([ funToken; paramList; bodySList ], getRange bodySList)
            loop (lambdaSExpr :: acc) afterList

        // Map shorthand: #map((k1 v1) (k2 v2) ...) or #map[(k1 v1) (k2 v2) ...]
        | { Token = Lexer.Symbol "#map"; Range = hr } :: { Token = LParen; Range = r } :: rest
        | { Token = Lexer.Symbol "#map"; Range = hr } :: { Token = LBracket; Range = r } :: rest ->
            let entriesSList, afterList = readForm true r hr id rest
            let mapSExpr = desugarMapLiteral hr entriesSList
            loop (mapSExpr :: acc) afterList

        | { Token = LParen; Range = r } :: rest ->
            let node, afterList = readForm true r r id rest
            loop (node :: acc) afterList

        // Comprehension: {collector expr clause...} → (comprehension collector expr clause...)
        //
        // Read as an ordinary list under a reserved head, exactly as a vec
        // literal is; the parser is where it becomes a loop.
        | { Token = LBrace; Range = r } :: rest ->
            let node, afterList = readForm false r r (headed "comprehension" r) rest
            loop (node :: acc) afterList

        // Vec literal: [items...] → (vec-literal items...)
        | { Token = LBracket; Range = r } :: rest ->
            let node, afterList = readForm false r r (headed "vec-literal" r) rest
            loop (node :: acc) afterList

        | token :: rest -> loop (SAtom token :: acc) rest

    loop [] tokens

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

                let source = File.ReadAllText(target)
                let innerForms, _ = Lexer.tokenize target source |> read
                let inner, innerVisited = expandIncludes (target :: activeFiles) target innerForms
                visited <- Set.union visited innerVisited
                inner

            | SList(SAtom { Token = Lexer.Symbol "include" } :: _, r) ->
                failwithf
                    "Include Error: malformed include at %s. Expected (include \"path\")"
                    (includedFrom r)

            | other -> [ other ])

    expanded, visited

/// Reads one metadata entry back into a declaration.
///
/// Only the body needs parsing: everything else was a typed field, and the
/// parameter names, the untyped body and the qualification map stay three
/// separate things exactly as they were written.
let private inlineImplDecl (source: string) (entry: ModuleMetadata.InlineTemplateEntry) : Decl =
    let form =
        match Lexer.tokenize source entry.Body |> read |> fst with
        | [ form ] -> form
        | _ ->
            failwithf
                $"Malformed inline template body in metadata for '%s{entry.TraitName}.%s{entry.MethodName}'."

    DInlineImpl(
        entry.TraitName,
        entry.MethodName,
        entry.Ctor,
        entry.OriginModule,
        entry.Params,
        Parser.parseExpr form,
        entry.Qualification,
        getRange form
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
      Types: Set<string>
      Constructors: Set<string>
      Traits: Set<string>
      /// Method name -> the trait that declares it.
      TraitMethods: Map<string, string> }

/// `moduleName` is the dependency's own, and is what a type name is read back
/// through: a declaration is keyed by the module that wrote it, and what an
/// importer spells — with or without a modifier — is the bare name inside that
/// key.
let private surfaceOf (moduleName: string) (decls: Decl list) (macros: ModuleMetadata.MacroEntry list) : ImportSurface =
    let bare = Naming.bareTypeName moduleName

    let typeDefs =
        decls
        |> List.collect (function
            | DType(tds, _)
            | DTypeRec(tds, _) -> tds
            | _ -> [])

    { Defs =
        (decls
         |> List.collect (function
             | DExtern(visible, _, _, _, _) -> [ visible ]
             | DImportExtern(specs, _) ->
                 specs
                 |> List.map (fun s -> s.Alias)
                 |> List.filter (fun a -> not (a.StartsWith publishedAliasPrefix))
             | _ -> []))
        @ (macros |> List.map (fun m -> m.Name))
        |> Set.ofList

      Types = typeDefs |> List.map (fun td -> bare td.Name) |> Set.ofList

      Constructors =
        typeDefs
        |> List.collect (fun td ->
            match td.Kind with
            | Union cases ->
                cases
                |> List.map (function
                    | SimpleCase(n, _) -> bare n
                    | DataCase(n, _, _, _) -> bare n)
            // A record is constructed by its own name.
            | Record _ -> [ bare td.Name ]
            // An opaque type offers no constructor, not even the record one
            // that shares its name — which is why the name is in `Types` and
            // absent here. Its hidden members are held for diagnostics and are
            // not part of any surface.
            | Opaque _
            | Alias _ -> [])
        |> Set.ofList

      Traits =
        decls
        |> List.choose (function
            | DTrait(n, _, _, _, _, _, _) -> Some n
            | _ -> None)
        |> Set.ofList

      TraitMethods =
        decls
        |> List.collect (function
            | DTrait(traitName, _, _, _, signatures, _, _) ->
                signatures |> List.map (fun (m, _) -> m, traitName)
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
                "Types, constructors and traits always arrive with their module — an imported signature is source text that has to resolve the types it mentions — so (only ...) and (except ...) filter defs and macros only."

        let refuse (what: string) : unit =
            failwithf $"Invalid (%s{form} ...) at %s{where}: '%s{name}' %s{what}. %s{advice}"

        if Set.contains name surface.Types then refuse "is a type"
        elif Set.contains name surface.Constructors then refuse "is a constructor"
        elif Set.contains name surface.Traits then refuse "is a trait"
        else
            match Map.tryFind name surface.TraitMethods with
            | Some owner ->
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
/// `only`, `except` and `rename` contribute nothing here: they are refused on
/// these names outright, and a type always arrives.
let private typeRenaming
    (moduleName: string)
    (surface: ImportSurface)
    (modifiers: ImportModifier list)
    : (string * string * AliasKind) list =

    let start =
        (surface.Types |> Set.toList |> List.map (fun n -> n, n, AliasType))
        @ (surface.Constructors |> Set.toList |> List.map (fun n -> n, n, AliasConstructor))
        @ (surface.Traits |> Set.toList |> List.map (fun n -> n, n, AliasTrait))
        // A method is prefixed with the trait it belongs to. Dispatch resolves
        // through the original, so a systematic prefix costs nothing — which is
        // the reason rule 4 refuses an individual rename and points here.
        @ (surface.TraitMethods |> Map.toList |> List.map (fun (m, _) -> m, m, AliasTrait))

    let step (visible: (string * string * AliasKind) list) (m: ImportModifier) =
        match m with
        | Prefix a
        | PrefixTypes a -> visible |> List.map (fun (o, v, k) -> o, a + v, k)
        | Postfix a
        | PostfixTypes a -> visible |> List.map (fun (o, v, k) -> o, v + a, k)
        | _ -> visible

    List.fold step start modifiers
    |> List.filter (fun (original, visible, _) -> original <> visible)
    // A record's name is both its type and its constructor, so it arrives
    // twice. Which kind wins does not matter — both resolve the same way.
    |> List.distinctBy (fun (original, visible, _) -> original, visible)
    // The spelling is invented from the bare name; what it resolves to is the
    // key. A trait is not keyed — it is still nominal by its bare name, and
    // `TraitOrigins` is what answers where one was declared.
    |> List.map (fun (original, visible, kind) ->
        match kind with
        | AliasType
        | AliasConstructor -> Naming.typeKey moduleName original, visible, kind
        | _ -> original, visible, kind)

/// A dependency's declarations as every edge that reaches it sees them.
///
/// The spellings are a list rather than one name, which is rule 10: a module
/// imported twice under different modifiers contributes both, and the
/// declaration is duplicated once per spelling. A name no edge kept is dropped.
let private applyDefRenaming (renaming: Map<string, string list>) (decls: Decl list) : Decl list =
    let visible (n: string) =
        match Map.tryFind n renaming with
        | Some names -> names
        | None -> []

    decls
    |> List.collect (fun d ->
        match d with
        | DExtern(name, origin, t, constraints, r) ->
            visible name |> List.map (fun v -> DExtern(v, origin, t, constraints, r))
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

/// Every Bjolang assembly this compilation knows a path for, by simple name.
let private assemblyPaths = System.Collections.Generic.Dictionary<string, string>()

let private noteAssemblyPath (path: string) =
    let name = Path.GetFileNameWithoutExtension path

    if not (assemblyPaths.ContainsKey name) then
        assemblyPaths[name] <- path

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
/// it. Resolution is by simple name, because that is all the CLR asks with, so
/// two modules of one name in different directories answer with whichever was
/// loaded first — the same collision that makes both of them `string_Module`.
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
/// has nowhere for rule two to resolve to.
///
/// `renaming` is the importing edge's: a macro is registered under the name
/// *this* import makes it visible as, and one the edge filtered out is not
/// registered at all. Everything else stays the original's — the transformer to
/// invoke, and the module its templates resolve against.
let private registerMacros
    (asm: System.Reflection.Assembly)
    (entries: ModuleMetadata.MacroEntry list)
    (decls: Decl list)
    (renaming: Map<string, string>)
    : unit =

    if not entries.IsEmpty then
        let exports =
            decls
            |> List.choose (function
                | DExtern(_, origin, _, _, _) -> Some origin.OriginalName
                | DDefun(n, _, _, _, _) -> Some n
                | DDef(n, _, _) -> Some n
                | DDefMutable(n, _, _) -> Some n
                | _ -> None)
            |> Set.ofList

        for entry in entries do
            match Map.tryFind entry.Name renaming with
            | None -> ()
            | Some visibleName ->
                let className = Naming.moduleClassName entry.ModuleName
                let clrType = asm.GetType className

                if isNull clrType then
                    failwithf
                        $"'%s{entry.Name}' is declared a macro by %s{asm.GetName().Name}, but the class '%s{className}' holding it is not in that assembly."

                let method = clrType.GetMethod(Naming.clrMemberName entry.Name)

                if isNull method then
                    failwithf
                        $"'%s{entry.Name}' is declared a macro by %s{asm.GetName().Name}, but '%s{className}' has no method '%s{Naming.clrMemberName entry.Name}'."

                Macro.register
                    { Name = visibleName
                      ModuleName = entry.ModuleName
                      Exports = exports
                      Method = method }

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
    /// The macros the assembly publishes, and the assembly holding them.
    /// Registration is per edge, so it does not happen where this is built.
    Macros: ModuleMetadata.MacroEntry list
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

/// Keyed on the timestamp as well as the path, so that rebuilding a dependency
/// and importing it again in the same process gets the new one.
type private CachedDll =
    { Decls: Decl list
      Macros: ModuleMetadata.MacroEntry list
      Assembly: System.Reflection.Assembly
      /// Everything reading it added to the link set — itself, and the
      /// transitive dependencies its metadata named.
      Linked: string list }

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
let private ensureLibrary (bjoPath: string) : string =
    let dllPath = Path.ChangeExtension(bjoPath, ".dll")

    let compilerBuilt =
        let loc = System.Reflection.Assembly.GetExecutingAssembly().Location
        if loc <> "" && File.Exists loc then File.GetLastWriteTimeUtc loc else DateTime.MinValue

    let upToDate =
        File.Exists dllPath
        && (let built = File.GetLastWriteTimeUtc dllPath
            let forms, _ = Lexer.tokenize bjoPath (File.ReadAllText bjoPath) |> read
            let _, sources = expandIncludes [ bjoPath ] bjoPath forms

            compilerBuilt <= built
            && sources |> Set.forall (fun src -> File.GetLastWriteTimeUtc src <= built))

    if upToDate then dllPath else compileLibrary bjoPath

/// The path an import resolves to, which is always a `.dll`.
///
/// Where a source file exists it is the truth, and the `.dll` beside it may be
/// behind it — so the `.bjo` is what this looks for first, and `ensureLibrary`
/// decides whether the built artefact is still current. A `.dll` with no source
/// is a prebuilt library and is taken as given.
///
/// A module path anchors to the installation, never to the working directory: a
/// module import means the same file no matter where the compiler is invoked
/// from, so the compiled standard library is the one that gets linked instead
/// of being rebuilt from source per caller.
let private resolveDependency (basePath: string) (spec: ImportSpec) : string option =
    let raw =
        match spec.Path with
        | RelativePath p -> Path.GetFullPath(Path.Combine(Path.GetDirectoryName basePath, p))
        | ModulePath parts ->
            Path.GetFullPath(Path.Combine(Paths.libDir, Path.Combine(Array.ofList parts) + ".bjo"))

    let bjoPath = if raw.EndsWith ".bjo" then raw else raw + ".bjo"
    let dllPath = Path.ChangeExtension(bjoPath, ".dll")

    if File.Exists bjoPath then Some(ensureLibrary bjoPath)
    elif File.Exists dllPath then Some dllPath
    // Neither is there. Answering with the path as written keeps an import that
    // names a `.dll` outright working, and leaves the rest to fail by name.
    else Some raw

/// `(std prelude)`, which every program gets whether it asks or not.
let private preludePath = ModulePath [ "std"; "prelude" ]

/// Is this file part of the standard library itself?
///
/// The implicit import is suppressed for the whole of `lib`, not just for
/// `prelude.bjo`. `prelude` imports `maths`, so giving `maths` an implicit
/// `prelude` would be a cycle; and the rest of the library says what it depends
/// on explicitly, which is what building it in dependency order relies on.
let private isStandardLibrary (absPath: string) =
    let lib = Paths.libDir.TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar
    absPath.StartsWith(lib, StringComparison.Ordinal)

/// The imports a file declares, read off its S-expressions.
///
/// Read before the file is parsed, and that is the point. A module's
/// dependencies used to be collected from the `DImport`s in its *parsed*
/// declarations, which is too late for macros: parsing a form whose head is a
/// macro needs that macro's module already compiled and loaded. Reading
/// S-expressions needs no macros, so the edges can be found first.
///
/// `Parser.parseDecl` is called rather than the shape being re-matched here, so
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
            match Parser.parseDecl form with
            | DImport(specs, r) -> specs |> List.map (fun s -> s, r)
            | _ -> []
        | _ -> [])

/// Adds the implicit `(import (std prelude))`.
///
/// The prelude is *linked*, not imported: `string-append`, `int->string`,
/// `print` and the `->str`, `Iterable`, `Collector` and `Num` traits are the
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
              File = absPath }

        let sym name = SAtom { Token = Lexer.Symbol name; Range = r }
        SList([ sym "import"; SList([ sym "std"; sym "prelude" ], r) ], r) :: forms

/// The one shape an entry point has: `(-> (List string) int)`.
///
/// `main` is called by the generated entry point rather than by anything in the
/// program, so what it takes is the runtime's to say and not inference's to
/// work out from a body. There is one type, and a file gets it whatever it
/// wrote:
///
/// - a `main` with no parameter is given one. The arguments exist whether or
///   not the program wants them, and the entry point then has a single call to
///   emit rather than a case analysis over what it may hand over.
/// - a `main` with no signature is given this one, so its parameter is
///   `(List string)` by declaration rather than by whatever its body happened
///   to constrain. `(println (list-head args))` alone leaves the element type
///   open, and a generic entry point is one nothing can call.
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

        if decls |> List.exists (function DSignature("main", _, _, _) -> true | _ -> false) then
            withParameter
        else
            let argsType = TApp("List", [ TName("string", r) ], r)
            DSignature("main", TArrow([ argsType ], [], None, TName("int", r), colour, r), [], r) :: withParameter

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
        | TypedAST.TFun([ TypedAST.TCon("List", [ TypedAST.TCon(TypedAST.TypeConstants.StringName, []) ]) ],
                        TypedAST.TCon(TypedAST.TypeConstants.Int32Name, []),
                        _) when constraints.IsEmpty -> ()
        | _ ->
            failwithf
                $"Type Error at %s{Lexer.formatPos r}: 'main' is a program's entry point and has one type, (-> (List string) int). This one is %s{DotNetInterop.showType actual}. A main written without a signature is given that one, and a main written without a parameter is given the arguments anyway — so the way to a different type is a signature, and there is nothing the entry point could pass it."
    | _ -> ()

let wrapInModule (moduleName: string) (filePath: string) (decls: Decl list) : Decl list =
    // Find the first and last range to represent the module range
    let r = 
        match decls with
        | [] -> { Start = { Line = 1; Column = 1 }; End = { Line = 1; Column = 1 }; File = filePath }
        | first :: _ ->
            let last = List.last decls
            let getRange d = 
                match d with
                | DDef(_, _, r) | DDefun(_, _, _, _, r) | DDefTuple(_, _, r) | DDefMutable(_, _, r)
                | DSignature(_, _, _, r) | DType(_, r) | DTypeRec(_, r) | DTrait(_, _, _, _, _, _, r) | DImpl(_, _, _, _, _, r)
                | DImplExtern(_, _, _, _, r) | DInlineImpl(_, _, _, _, _, _, _, r)
                | DModule(_, _, r) | DImport(_, r) | DAlias(_, _, r) | DExport(_, r) | DReExport(_, r)
                | DExtern(_, _, _, _, r) | DImportAlias(_, _, _, r)
                | DImportExtern(_, r) | DImportClass(_, r) | DMacro(_, r) -> r
            unionLexerRanges (getRange first) (getRange last)
    
    [ DModule(moduleName, decls, r) ]

let loadModuleGraph (mainFilePath: string) : Decl list * string list =
    // Unconditionally, not only when something publishes a macro: the expander
    // is also what reports a macro used in the module that defines it.
    Macro.install ()

    // Before the first `Assembly.LoadFile` below, since it is the assemblies
    // loaded there whose dependencies would otherwise not resolve.
    installAssemblyResolver ()

    let resolvedModules = System.Collections.Generic.Dictionary<string, LoadedModule>()
    let currentPath = System.Collections.Generic.HashSet<string>()
    let dllDeps = System.Collections.Generic.HashSet<string>()

    /// Every import edge that reaches a given module, as the renaming it
    /// produces and the position of the form that wrote it.
    ///
    /// Keyed by path, like the module cache, but a *list* — the cache holds
    /// what a module says about itself, and this holds what each importer made
    /// of it.
    let edges =
        System.Collections.Generic.Dictionary<
            string,
            ResizeArray<Map<string, string> * (string * string * AliasKind) list * Lexer.Range>
         >()

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

            let parsedDecls, deps, macros, assembly =
                match cached with
                | Some hit ->
                    // The link set is per compilation and the parse is not, so
                    // what was linked is replayed rather than remembered.
                    for path in hit.Linked do
                        dllDeps.Add path |> ignore
                        noteAssemblyPath path

                    hit.Decls, [], hit.Macros, Some hit.Assembly
                | None ->

                if absPath.EndsWith(".dll") then
                    dllDeps.Add(absPath) |> ignore
                    // Before the assembly is loaded: a transformer this one
                    // publishes may call into any of these, and the resolver is
                    // what makes that possible.
                    noteAssemblyPath absPath
                    let asm = System.Reflection.Assembly.LoadFile(absPath)
                    let attr = asm.GetCustomAttributes(typeof<System.Reflection.AssemblyMetadataAttribute>, false)

                    let metadataValue (key: string) : string option =
                        attr
                        |> Array.choose (fun a ->
                            let meta = a :?> System.Reflection.AssemblyMetadataAttribute
                            if meta.Key = key then Some meta.Value else None)
                        |> Array.tryHead

                    // An assembly with no Bjolang metadata at all exports
                    // nothing and is a perfectly good thing to link — a
                    // hand-written C# library is exactly that. One carrying
                    // the attributes an older compiler wrote is a different
                    // thing, and saying so beats reporting every name it was
                    // meant to export as unbound.
                    let meta =
                        match metadataValue "BjolangMetadata" with
                        | Some text -> ModuleMetadata.deserialize absPath text
                        | None ->
                            let legacyKeys =
                                [ "BjolangExports"; "BjolangDeps"; "BjolangInlineImpls"; "BjolangMacros" ]

                            if legacyKeys |> List.exists (metadataValue >> Option.isSome) then
                                failwithf
                                    $"'%s{absPath}' was built by an earlier version of the Bjolang compiler, whose metadata format this one does not read. Rebuild it with this compiler version."

                            ModuleMetadata.empty

                    // A transitive dependency is *linked*, not *imported*. Its
                    // assembly has to be referenced, because that is where the
                    // code of anything re-exported through this DLL actually
                    // lives — but its exports are deliberately not parsed into
                    // the module graph. Only what this DLL exports or
                    // re-exports becomes visible to whoever imports it.
                    for depPath in meta.Deps do
                        if depPath <> "" && File.Exists depPath then
                            dllDeps.Add(depPath) |> ignore
                            noteAssemblyPath depPath

                    // Inlineable method bodies, if this assembly published any.
                    // Without them everything that would have been inlined
                    // calls the landing pad instead.
                    let inlineImplDecls =
                        meta.InlineTemplates |> List.map (inlineImplDecl absPath)

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
                            Lexer.tokenize absPath declText |> read |> fst |> Parser.parseModule

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
                        let bare = Naming.bareTypeName (Naming.moduleNameOfPath absPath)

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
                                               | DataCase(n, _, _, _) -> spellingOf AliasConstructor r n)
                                       | _ -> []))
                            | _ -> [])

                    // An exported binding becomes an extern: a name with a type
                    // and no body, which is exactly what an importer can say
                    // about it. The signature is rebuilt as source because a
                    // type is stored as the syntax it was written in, and there
                    // is one parser for that.
                    let externDecls =
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
                            |> Parser.parseModule
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
                                              Kind = AliasDef }
                                        | None ->
                                            { OriginModule = ""
                                              OriginalName = name
                                              Kind = AliasDef }

                                    DExtern(name, origin, t, constraints, r)
                                | other -> other))

                    let parsedDecls = typeSpellingDecls @ declsFromText @ externDecls
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
                              Macros = meta.Macros
                              Assembly = asm
                              Linked =
                                absPath :: (meta.Deps |> List.filter (fun p -> p <> "" && File.Exists p)) }

                    decls, [], meta.Macros, Some asm
                else
                    // Reported rather than left to `File.ReadAllText`, whose
                    // `FileNotFoundException` is not a diagnostic and so prints
                    // a compiler stack trace at whoever mistyped a path.
                    if not (File.Exists absPath) then
                        failwithf
                            "Import Error: cannot find '%s'. Paths in an (import \"...\") resolve relative to the file doing the importing."
                            absPath

                    let sourceCode = File.ReadAllText(absPath)
                    let forms, _ = Lexer.tokenize absPath sourceCode |> read
                    // Includes are spliced before anything looks at the forms, so
                    // an included file's own imports are picked up as this
                    // module's dependencies below.
                    let forms, _ = expandIncludes [ absPath ] absPath forms
                    // The implicit `(import (std prelude))` goes on *after*
                    // includes are spliced: an included file's forms land in
                    // this module, so this module's one import covers them.
                    let forms = withImplicitPrelude absPath forms

                    // Which of this module's own names are macros. Read off the
                    // S-expressions, like the imports: a `def/macro` is
                    // recognizable without parsing, and using one here has to
                    // be reported as what it is rather than as an unbound name.
                    let localMacros =
                        forms
                        |> List.choose (function
                            | SList(SAtom { Token = Lexer.Symbol "def/macro" }
                                    :: SList(SAtom { Token = Lexer.Symbol name } :: _, _)
                                    :: _,
                                    _) -> Some name
                            | _ -> None)
                        |> Set.ofList

                    // Resolved *and built*: an imported `.bjo` becomes a `.dll`
                    // here, so every edge in the graph names a compiled unit and
                    // the topological sort below keys on the same paths.
                    let importEdges =
                        importsOf forms
                        |> List.choose (fun (spec, r) ->
                            resolveDependency absPath spec
                            |> Option.map (fun p -> Path.GetFullPath p, spec, r))

                    // Dependencies are loaded *before* this module is parsed.
                    //
                    // This is the whole reordering: a dependency's `.dll` is
                    // what says which of its names are macros, and the parser
                    // has to know that at the moment it meets one in head
                    // position. Parsing first and collecting `DImport`s
                    // afterwards cannot work, however the expander is written.
                    for (dep, spec, r) in importEdges do
                        load dep

                        // The modifiers belong to this edge, so the renaming is
                        // computed here rather than inside `load`, which is
                        // keyed by path and shared by every importer.
                        let m = resolvedModules[dep]
                        let surface = surfaceOf m.ModuleName m.ParsedDecls m.Macros
                        let renaming = defRenaming r (Path.GetFileName dep) surface spec.Modifiers

                        if not (edges.ContainsKey dep) then
                            edges[dep] <- ResizeArray()

                        edges[dep].Add(renaming, typeRenaming m.ModuleName surface spec.Modifiers, r)

                        // A macro has to be in the table under the name this
                        // import gives it before the form using it is read.
                        match m.Assembly with
                        | Some asm -> registerMacros asm m.Macros m.ParsedDecls renaming
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
                                _) -> Macro.alias newName oldName |> ignore
                        | _ -> ()

                    // Set immediately before parsing, and not earlier: loading a
                    // dependency parses *that* module, whose own macros are a
                    // different set.
                    Macro.setLocalMacros localMacros
                    let parsed = Parser.parseModule forms
                    Macro.setLocalMacros Set.empty

                    // Only the file being compiled has an entry point. A `main`
                    // in a module this one imports is one of its functions.
                    let parsed =
                        if absPath = Path.GetFullPath mainFilePath then
                            shapeEntryPoint parsed
                        else
                            parsed

                    parsed, (importEdges |> List.map (fun (dep, _, _) -> dep)), [], None

            // Dependencies were loaded above, before this module was parsed. A
            // `.dll` has none to load: its transitive deps are link-only and
            // never enter the module graph.

            let moduleName = Naming.moduleNameOfPath absPath
            resolvedModules.[absPath] <- {
                FilePath = absPath
                ModuleName = moduleName
                Dependencies = deps
                ParsedDecls = parsedDecls
                Macros = macros
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
            sorted.Add(m)

    visit (Path.GetFullPath(mainFilePath))

    // Every spelling an import made visible: the name, where it came from,
    // whether a modifier invented it, and the import that did. Rule 5 is
    // decided over this once every module's edges are known.
    let spellings = ResizeArray<string * string * string * bool * Lexer.Range>()

    /// A module's declarations as its importers see them.
    ///
    /// The main module has no importer and is left exactly as parsed.
    let viewOf (m: LoadedModule) : Decl list =
        match edges.TryGetValue m.FilePath with
        | true, edgeList ->
            let merged =
                edgeList
                |> Seq.collect (fun (renaming, _, r) ->
                    renaming |> Map.toSeq |> Seq.map (fun (original, visible) -> original, visible, r))
                |> Seq.distinctBy (fun (original, visible, _) -> original, visible)
                |> List.ofSeq

            // The spellings for what is not a binding. They are declarations of
            // their own rather than a rewriting, because the declaration that
            // introduces the name has to keep it.
            let typeSpellings =
                edgeList
                |> Seq.collect (fun (_, types, r) ->
                    types |> Seq.map (fun (original, visible, kind) -> original, visible, kind, r))
                |> Seq.distinctBy (fun (original, visible, _, _) -> original, visible)
                |> List.ofSeq

            for (original, visible, r) in merged do
                spellings.Add(visible, m.ModuleName, original, visible <> original, r)

            // Reported under the name source writes rather than the key, which
            // is what the reader of the collision message is looking at.
            for (original, visible, _, r) in typeSpellings do
                spellings.Add(visible, m.ModuleName, Naming.bareTypeName m.ModuleName original, true, r)

            let byOriginal =
                merged
                |> List.groupBy (fun (original, _, _) -> original)
                |> List.map (fun (original, g) -> original, g |> List.map (fun (_, visible, _) -> visible))
                |> Map.ofList

            let aliasDecls =
                typeSpellings
                |> List.map (fun (original, visible, kind, r) -> DImportAlias(visible, original, kind, r))

            aliasDecls @ applyDefRenaming byOriginal m.ParsedDecls
        | _ -> m.ParsedDecls

    let allDecls =
        sorted
        |> Seq.map (fun m -> wrapInModule m.ModuleName m.FilePath (viewOf m))
        |> List.concat

    // Rule 5. Only a spelling a modifier *invented* is checked: two plain
    // imports offering the same name is the older shadowing rule, where the
    // later import wins, and widening this to cover it would reject programs
    // that have always compiled.
    for (visible, group) in spellings |> Seq.groupBy (fun (v, _, _, _, _) -> v) do
        let origins =
            group
            |> Seq.map (fun (_, originModule, original, _, _) -> originModule, original)
            |> Seq.distinct
            |> List.ofSeq

        if origins.Length > 1 && group |> Seq.exists (fun (_, _, _, renamed, _) -> renamed) then
            let (_, _, _, _, r) = group |> Seq.find (fun (_, _, _, renamed, _) -> renamed)
            let describe (m: string, n: string) = $"'%s{n}' from %s{m}"
            let both = origins |> List.map describe |> String.concat " and "

            failwithf
                $"Import collision at %s{Lexer.formatPos r}: '%s{visible}' would name %s{both}. A modifier or (:alias ...) that produces a name another import already produces is an error, not a shadowing."

    allDecls, dllDeps |> Seq.toList

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
let private moduleOfName (decls: TypedAST.TDecl list) : Map<string, string * string> =
    decls
    |> TypedAST.collectDecls (function
        | TypedAST.TModule(modName, inner, _) ->
            inner
            |> List.choose (function
                | TypedAST.TDef(n, _, _, _) -> Some(n, (modName, n))
                | TypedAST.TDefMutable(n, _, _, _) -> Some(n, (modName, n))
                | TypedAST.TDefun(n, _, _, _, _, _, _, _, _) -> Some(n, (modName, n))
                | TypedAST.TExtern(visible, origin, _, _) ->
                    Some(visible, (origin.OriginModule, origin.OriginalName))
                | _ -> None)
        | _ -> [])
    |> Map.ofList

/// Works out what each local inline template's free variables should be emitted
/// as, now that the whole program has been checked.
///
/// This cannot be done before inference — `infer` fails hard on unbound names
/// and `Origin_Module::helper` is not one — and it cannot be skipped for local
/// impls either. Without it, a body that calls a module-level `helper`, inlined
/// into a caller that happens to have a local named `helper`, emits a bare
/// `helper` that binds to the local.
let private qualifyInlineTemplates (env: TypedAST.Env) (decls: TypedAST.TDecl list) : TypedAST.Env =
    let moduleOf = moduleOfName decls

    let qualified =
        env.Registry.InlineMethods
        |> Map.map (fun _ (tpl: TypedAST.InlineTemplate) ->
            // A template read back from a `.dll` was qualified where it was
            // written, by a compilation that could see its module's imports.
            if not (Map.isEmpty tpl.Qualification) then
                tpl
            else
                let free = AlphaRename.freeNames (Set.ofList tpl.Params) tpl.Body

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

let runFullFrontendPipeline (mainFilePath: string) =
    try
        Diagnostics.progress "=== Step 1: Parsing & Module Resolution ==="
        let parsedModuleDecls, dllDeps =
            Timing.phase "parse + module graph" (fun () -> loadModuleGraph mainFilePath)

        // The macros *this* compilation publishes. The main module is last, and
        // is the only one being compiled — everything before it arrived as a
        // `.dll` and already declared its own macros in its own metadata.
        //
        // Read here rather than after type checking, because `DMacro` does not
        // survive it: a macro is checked as the `defun` it also produced.
        let declaredMacros =
            match List.tryLast parsedModuleDecls with
            | Some(DModule(_, decls, _)) -> decls |> List.choose (function DMacro(n, _) -> Some n | _ -> None)
            | _ -> []

        Diagnostics.progress "=== Step 2: Normalization ==="
        // First of the source-to-source passes, and before `LetRecify` on
        // purpose: an applied lambda reduced into a `let` chain here is one
        // fewer closure for every pass after it to carry, and the bindings it
        // leaves behind are what `LetRecify` orders.
        let normalizedDecls =
            Timing.phase "normalize" (fun () -> Normalize.normalizeModule parsedModuleDecls)

        let letrecifiedDecls = Timing.phase "letrecify" (fun () -> letrecifyModule normalizedDecls)

        Diagnostics.progress "=== Step 3: Type Checking ==="
        let env, typedAst =
            Timing.phase "type check" (fun () -> Inference.checkProgram Prelude.prelude letrecifiedDecls)

        // Before anything reads `main`: the entry point is generated code's
        // caller, and a type it cannot call is a diagnostic here rather than a
        // C# error in a file nobody wrote.
        checkEntryPoint env parsedModuleDecls

        let env = qualifyInlineTemplates env typedAst

        // Before inlining, and deliberately: `spliceTemplate` is best-effort, so
        // a check after it would report on a spliced body but not on the same
        // body reached through a landing pad — the same program, different
        // errors depending on inliner luck. See the module docstring and §8.3.
        MustUse.run env.Registry typedAst

        Diagnostics.progress "=== Step 4: Trait Inlining ==="
        // Before dictionary lowering, so that the dictionary pass sees the
        // inlined result and handles any interface-trait dispatch inside it with
        // no changes; and before loop lowering, because a `TRecur` carries an
        // index into its enclosing loop and cannot be spliced elsewhere.
        let inlinedAst = Timing.phase "trait inline" (fun () -> TraitInline.run env typedAst)

        Diagnostics.progress "=== Step 5: Dictionary Lowering ==="
        let loweredAst = Timing.phase "dictionary lowering" (fun () -> Lowering.lowerProgram env inlinedAst)

        Diagnostics.progress "=== Step 6: Loop Lowering ==="
        let loopLoweredAst = Timing.phase "loop lowering" (fun () -> LoopLowering.lowerProgram loweredAst)

        // A `(loop ...)` is a loop by construction, but promotion is a silent
        // optimization and a desugaring bug would leave real calls behind —
        // correct, and unable to iterate deeply. Checked rather than assumed.
        LoopLowering.assertLoopsPromoted loopLoweredAst

        // After loop lowering, and for that reason: whether a yield point sits
        // inside a C# member of its own is only decided once a named `let` has
        // either become a `while` or stayed a local function. See the module
        // docstring.
        ColourCheck.run loopLoweredAst

        // Last, and a cleanup pass only: C# rejects a local that shadows an
        // enclosing one, and every pass above is free to produce that.
        let uniquifiedAst = Timing.phase "alpha rename" (fun () -> AlphaRename.uniquifyProgram loopLoweredAst)

        Diagnostics.progress "=== Frontend pipeline complete ==="
        Some (env, uniquifiedAst, dllDeps, declaredMacros)
    with ex ->
        Diagnostics.reportFailure ex
        None

