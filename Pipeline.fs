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

    /// Reads the body of a parenthesized form. A dot anywhere in the body makes
    /// it a tuple regardless of how the form was introduced; otherwise the
    /// caller decides what, if anything, to put at the head.
    ///
    /// `startRange` opens the form and `rangeFrom` opens the range the result
    /// spans — they differ for a quoted list, where the quote comes first.
    let readForm (startRange: Lexer.Range) (rangeFrom: Lexer.Range) (undotted: SExpr list -> SExpr list) rest =
        let innerNodes, afterList = read rest
        let endRange = if List.isEmpty afterList then startRange else (List.head afterList).Range
        let listRange = unionLexerRanges rangeFrom endRange

        let finalNodes =
            if List.exists isDot innerNodes then
                let tupleToken = { Token = Lexer.Symbol "Tuple"; Range = startRange }
                SAtom tupleToken :: List.filter (not << isDot) innerNodes
            else
                undotted innerNodes

        SList(finalNodes, listRange), afterList

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
            let withHead innerNodes =
                let headToken = { Token = Lexer.Symbol "quoted-list"; Range = qr }
                SAtom headToken :: innerNodes

            let node, afterList = readForm r qr withHead rest
            loop (node :: acc) afterList

        // Function shorthand: #(+ &1 &2 5) → (fun (&1 &2) (+ &1 &2 5))
        | { Token = Hash; Range = hr } :: { Token = LParen; Range = r } :: rest ->
            let bodySList, afterList = readForm r hr id rest
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
            let entriesSList, afterList = readForm r hr id rest
            let mapSExpr = desugarMapLiteral hr entriesSList
            loop (mapSExpr :: acc) afterList

        | { Token = LParen; Range = r } :: rest ->
            let node, afterList = readForm r r id rest
            loop (node :: acc) afterList

        // Comprehension: {collector expr clause...} → (comprehension collector expr clause...)
        //
        // Read as an ordinary list under a reserved head, exactly as a vec
        // literal is; the parser is where it becomes a loop.
        | { Token = LBrace; Range = r } :: rest ->
            let innerNodes, afterList = read rest
            let endRange = if List.isEmpty afterList then r else (List.head afterList).Range
            let listRange = unionLexerRanges r endRange
            let headToken = { Token = Lexer.Symbol "comprehension"; Range = r }
            loop (SList(SAtom headToken :: innerNodes, listRange) :: acc) afterList

        // Vec literal: [items...] → (vec-literal items...)
        | { Token = LBracket; Range = r } :: rest ->
            let innerNodes, afterList = read rest
            let endRange = if List.isEmpty afterList then r else (List.head afterList).Range
            let listRange = unionLexerRanges r endRange
            let headToken = { Token = Lexer.Symbol "vec-literal"; Range = r }
            let finalNodes = SAtom headToken :: innerNodes
            loop (SList(finalNodes, listRange) :: acc) afterList

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
let rec private expandIncludes (activeFiles: string list) (filePath: string) (forms: SExpr list) : SExpr list =
    let includedFrom (r: Lexer.Range) = Lexer.formatPos r

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
            expandIncludes (target :: activeFiles) target innerForms

        | SList(SAtom { Token = Lexer.Symbol "include" } :: _, r) ->
            failwithf
                "Include Error: malformed include at %s. Expected (include \"path\")"
                (includedFrom r)

        | other -> [ other ])

/// Reads the `BjolangInlineImpls` metadata back into declarations.
///
/// Each entry keeps the parameter names, the untyped body and the qualification
/// map as three separate fields, exactly as they were written.
let private parseInlineImpls (source: string) (metadata: string) : Decl list =
    let forms, _ = Lexer.tokenize source metadata |> read

    forms
    |> List.choose (fun form ->
        match form with
        | SList([ SAtom { Token = Lexer.Symbol "inline-impl" }
                  SAtom { Token = Lexer.StringLit traitName }
                  SAtom { Token = Lexer.StringLit methodName }
                  SAtom { Token = Lexer.StringLit ctor }
                  SAtom { Token = Lexer.StringLit originModule }
                  SList(paramNodes, _)
                  body
                  SList(qualNodes, _) ],
                r) ->
            let parameters =
                paramNodes
                |> List.map (function
                    | SAtom { Token = Lexer.Symbol p } -> p
                    | bad -> failwithf $"Malformed inline template parameter in metadata at %s{Lexer.formatPos (getRange bad)}")

            let qualification =
                qualNodes
                |> List.map (function
                    | SList([ SAtom { Token = Lexer.StringLit name }; SAtom { Token = Lexer.StringLit emitted } ], _) ->
                        name, emitted
                    | bad ->
                        failwithf $"Malformed inline template qualification in metadata at %s{Lexer.formatPos (getRange bad)}")

            Some(
                DInlineImpl(
                    traitName,
                    methodName,
                    ctor,
                    originModule,
                    parameters,
                    Parser.parseExpr body,
                    qualification,
                    r
                )
            )
        | _ -> None)

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

/// One macro an assembly publishes: the Bjolang name, and the module that
/// defines it.
type MacroEntry = { Name: string; ModuleName: string }

/// Reads the `BjolangMacros` metadata back.
///
/// Format is `(macro "name" "module")`, one per line. Deliberately not folded
/// into `BjolangExports`: those entries are signatures, and this is read
/// *before* the importing module is parsed — the expander has to know a name is
/// a macro at the moment the parser meets it in head position.
let private parseMacroEntries (source: string) (metadata: string) : MacroEntry list =
    let forms, _ = Lexer.tokenize source metadata |> read

    forms
    |> List.choose (function
        | SList([ SAtom { Token = Lexer.Symbol "macro" }
                  SAtom { Token = Lexer.StringLit name }
                  SAtom { Token = Lexer.StringLit moduleName } ],
                _) -> Some { Name = name; ModuleName = moduleName }
        | bad ->
            failwithf $"Malformed macro entry in metadata at %s{Lexer.formatPos (getRange bad)}")

/// Loads the transformers an imported assembly publishes and hands them to the
/// expander.
///
/// This is the Template Haskell step: the assembly is already loaded (its
/// metadata was just read off it), and a transformer is an ordinary
/// `public static` method on the module class, so all that is left is to find
/// it. `Exports` comes from the same assembly's declarations, because a
/// template may only name an exported binding of its own module — anything else
/// has nowhere for rule two to resolve to.
let private registerMacros
    (asm: System.Reflection.Assembly)
    (entries: MacroEntry list)
    (decls: Decl list)
    : unit =

    if not entries.IsEmpty then
        let exports =
            decls
            |> List.choose (function
                | DExtern(n, _, _, _) -> Some n
                | DDefun(n, _, _, _, _) -> Some n
                | DDef(n, _, _) -> Some n
                | DDefMutable(n, _, _) -> Some n
                | _ -> None)
            |> Set.ofList

        for entry in entries do
            let className = Naming.moduleClassName entry.ModuleName
            let clrType = asm.GetType className

            if isNull clrType then
                failwithf
                    $"'%s{entry.Name}' is declared a macro by %s{asm.GetName().Name}, but the class '%s{className}' holding it is not in that assembly."

            let method = clrType.GetMethod(Naming.sanitizeIdent entry.Name)

            if isNull method then
                failwithf
                    $"'%s{entry.Name}' is declared a macro by %s{asm.GetName().Name}, but '%s{className}' has no method '%s{Naming.sanitizeIdent entry.Name}'."

            Macro.register
                { Name = entry.Name
                  ModuleName = entry.ModuleName
                  Exports = exports
                  Method = method }

        Macro.install ()

let resolveImportPath (basePath: string) (importSpec: ImportSpec) : string option =
    match importSpec with
    | RelativePath p -> 
        let rawPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(basePath), p))
        let dllPath = if rawPath.EndsWith(".bjo") then rawPath.Substring(0, rawPath.Length - 4) + ".dll" else rawPath + ".dll"
        if System.IO.File.Exists(dllPath) then Some dllPath
        else Some rawPath
    | ModulePath p -> 
        // Anchored to the installation, never to the working directory: a
        // module import means the same file no matter where the compiler is
        // invoked from, so the compiled standard library is always the one
        // that gets linked instead of being rebuilt from source per caller.
        let libPath = Paths.libDir
        let relPath = Path.Combine(Array.ofList p)
        let dllPath = Path.GetFullPath(Path.Combine(libPath, relPath + ".dll"))
        let bjoPath = Path.GetFullPath(Path.Combine(libPath, relPath + ".bjo"))
        if System.IO.File.Exists(dllPath) then Some dllPath
        else Some bjoPath

type LoadedModule = {
    FilePath: string
    ModuleName: string
    Dependencies: string list
    ParsedDecls: Decl list
}

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

/// Every file spliced into `path` by an `(include ...)`, transitively.
///
/// Needed for staleness: an included file is part of the module's source but is
/// not the file the `.dll` is named after, so editing one has to force a
/// rebuild just as editing the module itself does.
let rec private includedFiles (seen: Set<string>) (path: string) : Set<string> =
    if Set.contains path seen || not (File.Exists path) then
        seen
    else
        let seen = Set.add path seen
        let forms, _ = Lexer.tokenize path (File.ReadAllText path) |> read

        forms
        |> List.fold
            (fun acc form ->
                match form with
                | SList([ SAtom { Token = Lexer.Symbol "include" }; SAtom { Token = Lexer.StringLit rel } ], _) ->
                    includedFiles acc (Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path: string), rel)))
                | _ -> acc)
            seen

/// The `.dll` for an imported `.bjo`, built if there is not a current one.
///
/// `(import "x.bjo")` means a compiled unit, always. Merging the source into
/// the importing assembly is what it used to mean, and it never worked: only
/// the last module was emitted, so the generated C# referenced a class nothing
/// produced. Compiling it separately is also what makes `include` a distinct
/// thing rather than a slower spelling of the same one.
///
/// Staleness is by timestamp against the whole source closure. It is checked at
/// all, which is new: `resolveImportPath` preferred any `.dll` that existed, so
/// editing a library and rebuilding a program against it quietly linked the old
/// one.
let private ensureLibrary (bjoPath: string) : string =
    let dllPath = Path.ChangeExtension(bjoPath, ".dll")

    let upToDate =
        File.Exists dllPath
        && (let built = File.GetLastWriteTimeUtc dllPath

            includedFiles Set.empty bjoPath
            |> Set.forall (fun src -> File.GetLastWriteTimeUtc src <= built))

    if upToDate then dllPath else compileLibrary bjoPath

/// The path an import resolves to, which is always a `.dll`.
///
/// `resolveImportPath` prefers an existing `.dll` and stops there, which is not
/// enough: with a `.bjo` beside it, the source is the truth and the `.dll` may
/// be behind it. A `.dll` with no source is a prebuilt library and is taken as
/// given.
let private resolveDependency (basePath: string) (spec: ImportSpec) : string option =
    resolveImportPath basePath spec
    |> Option.map (fun resolved ->
        let source =
            if resolved.EndsWith ".dll" then Path.ChangeExtension(resolved, ".bjo") else resolved

        if source.EndsWith ".bjo" && File.Exists source then ensureLibrary source else resolved)

/// `(std prelude)`, which every program gets whether it asks or not.
let private preludeSpec = ModulePath [ "std"; "prelude" ]

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
let importsOf (forms: SExpr list) : ImportSpec list =
    forms
    |> List.collect (fun form ->
        match form with
        | SList(SAtom { Token = Lexer.Symbol "import" } :: _, _) ->
            match Parser.parseDecl form with
            | DImport(specs, _) -> specs
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
let private withImplicitPrelude (absPath: string) (forms: SExpr list) : SExpr list =
    if List.contains preludeSpec (importsOf forms) || isStandardLibrary absPath then
        forms
    else
        let r =
            { Start = { Line = 1; Column = 1 }
              End = { Line = 1; Column = 1 }
              File = absPath }

        let sym name = SAtom { Token = Lexer.Symbol name; Range = r }
        SList([ sym "import"; SList([ sym "std"; sym "prelude" ], r) ], r) :: forms

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
                | DModule(_, _, r) | DImport(_, r) | DExport(_, r) | DReExport(_, r) | DExtern(_, _, _, r)
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

    let rec load (filePath: string) : unit =
        let absPath = Path.GetFullPath(filePath)
        if currentPath.Contains(absPath) then
            failwithf "Cyclic dependency detected: %s" absPath
        if not (resolvedModules.ContainsKey(absPath)) then
            currentPath.Add(absPath) |> ignore
            
            let parsedDecls, deps =
                if absPath.EndsWith(".dll") then
                    dllDeps.Add(absPath) |> ignore
                    // Before the assembly is loaded: a transformer this one
                    // publishes may call into any of these, and the resolver is
                    // what makes that possible.
                    noteAssemblyPath absPath
                    let asm = System.Reflection.Assembly.LoadFile(absPath)
                    let attr = asm.GetCustomAttributes(typeof<System.Reflection.AssemblyMetadataAttribute>, false)
                    
                    // Collect transitive DLL dependencies from BjolangDeps metadata
                    let transitiveDeps =
                        attr
                        |> Array.choose (fun a -> 
                            let meta = a :?> System.Reflection.AssemblyMetadataAttribute
                            if meta.Key = "BjolangDeps" then Some meta.Value else None)
                        |> Array.tryHead
                    // A transitive dependency is *linked*, not *imported*. Its
                    // assembly has to be referenced, because that is where the
                    // code of anything re-exported through this DLL actually
                    // lives — but its exports are deliberately not parsed into
                    // the module graph. Only what this DLL exports or
                    // re-exports becomes visible to whoever imports it.
                    match transitiveDeps with
                    | Some depsStr ->
                        for dep in depsStr.Split(';') do
                            let depPath = dep.Trim()
                            if depPath <> "" && System.IO.File.Exists(depPath) then
                                dllDeps.Add(depPath) |> ignore
                                noteAssemblyPath depPath
                    | None -> ()
                    
                    let exports =
                        attr
                        |> Array.choose (fun a -> 
                            let meta = a :?> System.Reflection.AssemblyMetadataAttribute
                            if meta.Key = "BjolangExports" then Some meta.Value else None)
                        |> Array.tryHead

                    // Inlineable method bodies, if this assembly published any.
                    // An older assembly simply has none, and everything that
                    // would have been inlined calls the landing pad instead.
                    let inlineImplDecls =
                        attr
                        |> Array.choose (fun a ->
                            let meta = a :?> System.Reflection.AssemblyMetadataAttribute
                            if meta.Key = "BjolangInlineImpls" then Some meta.Value else None)
                        |> Array.tryHead
                        |> Option.map (parseInlineImpls absPath)
                        |> Option.defaultValue []

                    // The macros this assembly publishes.
                    let macroEntries =
                        attr
                        |> Array.choose (fun a ->
                            let meta = a :?> System.Reflection.AssemblyMetadataAttribute
                            if meta.Key = "BjolangMacros" then Some meta.Value else None)
                        |> Array.tryHead
                        |> Option.map (parseMacroEntries absPath)
                        |> Option.defaultValue []

                    match exports with
                    | Some metaStr ->
                        let tokens, _ = Lexer.tokenize absPath metaStr |> read
                        
                        // Extract constraint info from S-expressions before parsing
                        // Format: (: name type (where (trait var) ...))
                        let extractConstraints (sexpr: SExpr) : (string * string) list =
                            match sexpr with
                            | SList(items, _) ->
                                items |> List.tryPick (function
                                    | SList(SAtom { Token = Lexer.Symbol "where" } :: constraintExprs, _) ->
                                        constraintExprs |> List.choose (function
                                            | SList([ SAtom { Token = Lexer.Symbol traitName }; SAtom { Token = Lexer.QuotedSymbol varName } ], _) ->
                                                Some (traitName, "'" + varName)
                                            | SList([ SAtom { Token = Lexer.Symbol traitName }; SAtom { Token = Lexer.Symbol varName } ], _) ->
                                                Some (traitName, varName)
                                            | _ -> None)
                                        |> Some
                                    | _ -> None)
                                |> Option.defaultValue []
                            | _ -> []
                        
                        // Build a map from name to constraints  
                        let constraintMap =
                            tokens |> List.choose (function
                                | SList(SAtom { Token = Lexer.Colon } :: SAtom { Token = Lexer.Symbol name } :: _, _) as sexpr ->
                                    let constraints = extractConstraints sexpr
                                    if constraints.IsEmpty then None
                                    else Some (name, constraints)
                                | _ -> None)
                            |> Map.ofList
                        
                        let parsedDecls = 
                            Parser.parseModule tokens
                            |> List.map (function
                                | DSignature(name, t, _, r) ->
                                    let constraints = Map.tryFind name constraintMap |> Option.defaultValue []
                                    DExtern(name, t, constraints, r)
                                | d -> d)
                        registerMacros asm macroEntries parsedDecls
                        // No module dependencies: a DLL's transitive deps are
                        // link-only and never enter the module graph. Inline
                        // templates come last: registering one is meaningless
                        // until the trait and impl it belongs to exist.
                        parsedDecls @ inlineImplDecls, []
                    | None ->
                        registerMacros asm macroEntries []
                        inlineImplDecls, []
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
                    let forms = expandIncludes [ absPath ] absPath forms
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
                    let deps = importsOf forms |> List.choose (resolveDependency absPath)

                    // Dependencies are loaded *before* this module is parsed.
                    //
                    // This is the whole reordering: a dependency's `.dll` is
                    // what says which of its names are macros, and the parser
                    // has to know that at the moment it meets one in head
                    // position. Parsing first and collecting `DImport`s
                    // afterwards cannot work, however the expander is written.
                    for dep in deps do
                        load dep

                    // Set immediately before parsing, and not earlier: loading a
                    // dependency parses *that* module, whose own macros are a
                    // different set.
                    Macro.setLocalMacros localMacros
                    let parsed = Parser.parseModule forms
                    Macro.setLocalMacros Set.empty
                    parsed, deps

            // Dependencies were loaded above, before this module was parsed. A
            // `.dll` has none to load: its transitive deps are link-only and
            // never enter the module graph.

            let moduleName = Path.GetFileNameWithoutExtension(absPath).Replace(".", "_").Replace("-", "_")
            resolvedModules.[absPath] <- {
                FilePath = absPath
                ModuleName = moduleName
                Dependencies = deps
                ParsedDecls = parsedDecls
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
    
    // Concatenate all module ASTs
    let allDecls = sorted |> Seq.map (fun m -> wrapInModule m.ModuleName m.FilePath m.ParsedDecls) |> List.concat
    allDecls, dllDeps |> Seq.toList

/// Which module each top-level name belongs to.
///
/// Built from the typed program rather than from the environment, because the
/// environment says only *that* a name is bound. A name reached through an
/// imported `.dll` arrives as a `TExtern` inside that dll's module, which is
/// exactly the answer wanted for a helper the origin module itself imported
/// from a third module.
let private moduleOfName (decls: TypedAST.TDecl list) : Map<string, string> =
    let rec collect (decls: TypedAST.TDecl list) =
        decls
        |> List.collect (function
            | TypedAST.TModule(modName, inner, _) ->
                inner
                |> List.choose (function
                    | TypedAST.TDef(n, _, _, _) -> Some(n, modName)
                    | TypedAST.TDefMutable(n, _, _, _) -> Some(n, modName)
                    | TypedAST.TDefun(n, _, _, _, _, _, _, _, _) -> Some(n, modName)
                    | TypedAST.TExtern(n, _, _) -> Some(n, modName)
                    | _ -> None)
            | _ -> [])

    collect decls |> Map.ofList

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
                        | Some m -> Some(n, Naming.qualifiedBinding m n)
                        | None -> None)
                    |> Map.ofSeq

                { tpl with Qualification = qualification })

    { env with
        Registry = { env.Registry with InlineMethods = qualified } }

let runFullFrontendPipeline (mainFilePath: string) =
    try
        printfn "=== Step 1: Parsing & Module Resolution ==="
        let parsedModuleDecls, dllDeps = loadModuleGraph mainFilePath

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

        let letrecifiedDecls = letrecifyModule parsedModuleDecls

        printfn "=== Step 2: Type Checking ==="
        let env, typedAst = Inference.checkProgram Prelude.prelude letrecifiedDecls

        let env = qualifyInlineTemplates env typedAst

        // Before inlining, and deliberately: `spliceTemplate` is best-effort, so
        // a check after it would report on a spliced body but not on the same
        // body reached through a landing pad — the same program, different
        // errors depending on inliner luck. See the module docstring and §8.3.
        MustUse.run env.Registry typedAst

        printfn "=== Step 3: Trait Inlining ==="
        // Before dictionary lowering, so that the dictionary pass sees the
        // inlined result and handles any interface-trait dispatch inside it with
        // no changes; and before loop lowering, because a `TRecur` carries an
        // index into its enclosing loop and cannot be spliced elsewhere.
        let inlinedAst = TraitInline.run env typedAst

        printfn "=== Step 4: Dictionary Lowering ==="
        let loweredAst = Lowering.lowerProgram env inlinedAst

        printfn "=== Step 5: Loop Lowering ==="
        let loopLoweredAst = LoopLowering.lowerProgram loweredAst

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
        let uniquifiedAst = AlphaRename.uniquifyProgram loopLoweredAst

        printfn "=== Frontend pipeline complete ==="
        Some (env, uniquifiedAst, dllDeps, declaredMacros)
    with ex ->
        Diagnostics.reportFailure ex
        None

