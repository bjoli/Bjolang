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
                failwithf "Invalid map entry at line %d, col %d. Expected (key value), [key value], or (key . value)" er.Start.Line er.Start.Column

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

    let rec loop acc remaining =
        match remaining with
        | [] -> List.rev acc, []
        | { Token = RParen } :: rest -> List.rev acc, rest
        | { Token = RBracket } :: rest -> List.rev acc, rest

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
                    | bad -> failwithf $"Malformed inline template parameter in metadata at line %d{(getRange bad).Start.Line}")

            let qualification =
                qualNodes
                |> List.map (function
                    | SList([ SAtom { Token = Lexer.StringLit name }; SAtom { Token = Lexer.StringLit emitted } ], _) ->
                        name, emitted
                    | bad ->
                        failwithf $"Malformed inline template qualification in metadata at line %d{(getRange bad).Start.Line}")

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
let private withImplicitPrelude (absPath: string) (decls: Decl list) : Decl list =
    let alreadyImported =
        decls
        |> List.exists (function
            | DImport(specs, _) -> specs |> List.contains preludeSpec
            | _ -> false)

    if alreadyImported || isStandardLibrary absPath then
        decls
    else
        let r =
            { Start = { Line = 1; Column = 1 }
              End = { Line = 1; Column = 1 }
              File = absPath }

        DImport([ preludeSpec ], r) :: decls

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
                | DSignature(_, _, _, r) | DType(_, r) | DTypeRec(_, r) | DTrait(_, _, _, _, _, _, r) | DImpl(_, _, _, _, r)
                | DImplExtern(_, _, _, r) | DInlineImpl(_, _, _, _, _, _, _, r)
                | DModule(_, _, r) | DImport(_, r) | DExport(_, r) | DReExport(_, r) | DExtern(_, _, _, r)
                | DImportExtern(_, r) | DImportClass(_, r) -> r
            unionLexerRanges (getRange first) (getRange last)
    
    [ DModule(moduleName, decls, r) ]

let loadModuleGraph (mainFilePath: string) : Decl list * string list =
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
                        // No module dependencies: a DLL's transitive deps are
                        // link-only and never enter the module graph. Inline
                        // templates come last: registering one is meaningless
                        // until the trait and impl it belongs to exist.
                        parsedDecls @ inlineImplDecls, []
                    | None ->
                        inlineImplDecls, []
                else
                    let sourceCode = File.ReadAllText(absPath)
                    let tokens, _ = Lexer.tokenize absPath sourceCode |> read
                    // Includes are spliced before anything looks at the forms, so
                    // an included file's own imports are picked up as this
                    // module's dependencies below.
                    let tokens = expandIncludes [ absPath ] absPath tokens
                    // The implicit `(import (std prelude))` goes on *after*
                    // includes are spliced: an included file's forms land in
                    // this module, so this module's one import covers them.
                    let parsedDecls = Parser.parseModule tokens |> withImplicitPrelude absPath

                    let deps = 
                        parsedDecls 
                        |> List.choose (function DImport(specs, _) -> Some specs | _ -> None)
                        |> List.concat
                        |> List.choose (resolveImportPath absPath)
                    parsedDecls, deps

            for dep in deps do
                load dep

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
        Some (env, uniquifiedAst, dllDeps)
    with ex ->
        printfn $"Compilation Panicked: %s{ex.Message}"
        printfn $"Stack Trace: %s{ex.StackTrace}"
        None

