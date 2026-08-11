type CompilerOptions =
    { InputFile: string option
      IsLibrary: bool
      Debug: bool }

let defaultOptions = { InputFile = None; IsLibrary = false; Debug = false }

let printUsage () =
    printfn "Bjolang Compiler"
    printfn "Usage: bjoc [options] <source.bjo>"
    printfn ""
    printfn "Options:"
    printfn "  --lib       Compile the source as a library (.dll) instead of an executable"
    printfn "  -d, --debug Build unoptimized, with debug symbols, and dump the typed AST to"
    printfn "              ast_dump.txt and the generated C# to out.cs"
    printfn "  --help      Show this help message"
    printfn ""
    printfn "Without -d the output is optimized; a debug build runs several times slower."

let rec parseArgs (args: string list) (opts: CompilerOptions) =
    match args with
    | [] -> opts
    | "--help" :: _ ->
        printUsage ()
        exit 0
    | "--lib" :: rest -> parseArgs rest { opts with IsLibrary = true }
    | "-d" :: rest
    | "--debug" :: rest -> parseArgs rest { opts with Debug = true }
    | arg :: rest when not (arg.StartsWith("-")) ->
        // If it doesn't start with '-', assume it's the input file
        match opts.InputFile with
        | None -> parseArgs rest { opts with InputFile = Some arg }
        | Some _ ->
            printfn "Error: Multiple input files specified."
            exit 1
    | unknown :: _ ->
        printfn $"Error: Unknown argument '%s{unknown}'"
        printUsage ()
        exit 1



open Bjolang
open System.IO

[<EntryPoint>]
let main argv =
    // 1. Parse CLI arguments
    let options = parseArgs (Array.toList argv) defaultOptions

    // 2. Validate inputs
    let inputFilePath =
        match options.InputFile with
        | Some path -> path
        | None ->
            printfn "Error: No input file specified."
            printUsage ()
            exit 1

    if not (File.Exists(inputFilePath)) then
        printfn $"Error: Source file '%s{inputFilePath}' not found."
        exit 1

    try
        // The runtime assemblies are made reflectable *before* anything is
        // type-checked.
        //
        // `Type.GetType` searches the core library and the compiler's own
        // assembly, neither of which knows about Bjoml — so without this,
        // `(import/class (Chan (: Bjoml.Channel)))` fails at the import with
        // "cannot find the .NET type", and the concurrency runtime is
        // linkable but not nameable. `registerAssemblyFile` is idempotent and
        // swallows nothing: a runtime assembly that is present but unloadable
        // is a real problem and says so.
        for assemblyPath in Paths.runtimeAssemblies do
            if File.Exists assemblyPath then
                DotNetInterop.registerAssemblyFile assemblyPath

        printfn $"Compiling %s{inputFilePath}"

        // 4. (Placeholder) Run your pipeline!
        // let ast = Parser.parseFile inputFilePath
        // let env, _, typedAst = TypedAST.checkProgram resolutionCtx ast
        // let loweredDecls = ClosureConversion.convertProgram typedAst

        // 5. Pass the isLibrary flag to the Emitter
        // Emitter.compileAssembly outputFilePath options.IsLibrary loweredDecls
        // here we should add the complation to a library
        let result = Pipeline.runFullFrontendPipeline inputFilePath
        match result with
        | Some (env, typedAst, dllDeps) ->
            // A source file with no `main` is a library whether or not `--lib`
            // was passed: an entry point would call a method that does not
            // exist, and a C# `Exe` without a `Main` does not link at all.
            let isLibrary = options.IsLibrary || not (Map.containsKey "main" env.Bindings)
            let extension = if isLibrary then ".dll" else ".exe"
            let outputFilePath = Path.ChangeExtension(inputFilePath, extension)

            printfn "Compilation succeeded. %d declarations." typedAst.Length
            
            let rec extractExports (decls: TypedAST.TDecl list) =
                decls |> List.choose (function 
                    | TypedAST.TExport(names, _) -> Some names 
                    | TypedAST.TReExport(names, _) -> Some names
                    | TypedAST.TModule(_, innerDecls, _) -> Some (extractExports innerDecls |> List.concat)
                    | _ -> None)
            
            let exports = extractExports typedAst |> List.concat
            
            let rec extractTypes (decls: TypedAST.TDecl list) =
                decls |> List.choose (function
                    | TypedAST.TType(defs, _) -> Some (defs |> List.map (fun d -> d, false))
                    | TypedAST.TTypeRec(defs, _) -> Some (defs |> List.map (fun d -> d, true))
                    | TypedAST.TModule(_, innerDecls, _) -> Some (extractTypes innerDecls |> List.concat)
                    | _ -> None)
            
            let typesToExport = extractTypes typedAst |> List.concat
            
            // A trait travels with its methods. Whichever module publishes a
            // trait method has to publish the trait itself and every
            // implementation of it, or the importer sees a plain function whose
            // associated types cannot be resolved and whose calls cannot be
            // dispatched to an impl class.
            let exportedTraits =
                env.Registry.Traits
                |> Map.filter (fun _ info ->
                    let methodNames =
                        (info.Signatures |> Map.toList |> List.map fst)
                        @ (info.Templates |> Map.toList |> List.map fst)

                    methodNames |> List.exists (fun m -> List.contains m exports))

            let exportedTraitMethods =
                exportedTraits
                |> Map.toList
                |> List.collect (fun (_, info) ->
                    (info.Signatures |> Map.toList |> List.map fst)
                    @ (info.Templates |> Map.toList |> List.map fst))
                |> Set.ofList

            // Every inline template belonging to a trait this module publishes.
            //
            // A template that will not serialize is simply left out: whoever
            // imports it then calls the landing pad, which is always correct and
            // is emitted for every impl method regardless.
            let inlineTemplatesToExport =
                env.Registry.InlineMethods
                |> Map.toList
                |> List.filter (fun ((traitName, _, _), (tpl: TypedAST.InlineTemplate)) ->
                    Map.containsKey traitName exportedTraits
                    && Codegen.isSerializableTemplate tpl.Body)

            // A template's free variables have to be reachable from the
            // importing module, or re-inference at the splice fails and the call
            // falls back to one. Anything an exported template names is
            // therefore exported too — including a helper this module itself
            // imported from a third one, which is where the qualification points.
            let autoExports =
                inlineTemplatesToExport
                |> List.collect (fun (_, (tpl: TypedAST.InlineTemplate)) ->
                    tpl.Qualification |> Map.toList |> List.map fst)
                |> List.filter (fun n ->
                    not (List.contains n exports)
                    && not (Set.contains n exportedTraitMethods)
                    && Map.containsKey n env.Bindings)
                |> List.distinct

            if isLibrary && not autoExports.IsEmpty then
                printfn
                    "Auto-exporting %d name(s) reachable only through an exported inline template: %s"
                    autoExports.Length
                    (String.concat ", " autoExports)

            // The `import/extern` aliases this module has to publish.
            //
            // An alias is not a binding and has no signature, so neither
            // `serializeExport` nor `autoExports` can carry one: what an
            // importer needs is the *import*, so that it resolves the overload
            // set against the same .NET metadata this module did.
            //
            // Two ways in. An alias may be exported outright, which is the only
            // way to publish an overloaded foreign method under one name. Or an
            // exported inline template may name one — `(defun (abs x) (clr-abs
            // x))` as a trait default, say — in which case the alias travels
            // whether or not anybody meant to export it, for the same reason
            // `autoExports` exists: an unreachable free name makes re-inference
            // at the splice fail, and the call silently falls back to the
            // landing pad.
            // An alias a *body* names is published under a name that source code
            // will not collide with, and the body is rewritten to use it.
            //
            // Publishing it under its own name would be a capture bug rather
            // than a convenience. A spliced template is re-inferred in the
            // importing module's scope, so a module that happened to define
            // `clr-abs` would have its definition silently become the meaning of
            // the library's `abs` — a wrong answer, with nothing said about it.
            // The mangled name means the body still refers to what it referred
            // to where it was written, whatever the importer calls things.
            let publishedAliasPrefix = "clr_import__"

            let publishedAliasOf (alias: string) =
                if alias.StartsWith publishedAliasPrefix then
                    alias
                else
                    let modTag =
                        System.IO.Path.GetFileNameWithoutExtension(inputFilePath).Replace(".", "_").Replace("-", "_")

                    $"%s{publishedAliasPrefix}%s{modTag}__%s{alias}"

            /// The aliases a body names, and what each is published as.
            let bodyExternSubst (bound: Set<string>) (body: Parser.Expr) =
                AlphaRename.freeNames bound body
                |> Set.toList
                |> List.filter (fun n -> Map.containsKey n env.Registry.ClrExterns)
                |> List.map (fun n -> n, publishedAliasOf n)
                |> Map.ofList

            // A trait's defaults are published with it, and are spliced into an
            // importing module's own implementations, so they are bodies in
            // exactly the same sense as a template.
            let exportedTraitDefaults =
                exportedTraits
                |> Map.toList
                |> List.collect (fun (_, (info: TypedAST.TraitInfo)) -> info.Defaults |> Map.toList)

            let externsNamedByBodies =
                (inlineTemplatesToExport
                 |> List.collect (fun (_, (tpl: TypedAST.InlineTemplate)) ->
                     bodyExternSubst (Set.ofList tpl.Params) tpl.Body |> Map.toList))
                @ (exportedTraitDefaults
                   |> List.collect (fun (_, decl) ->
                       match decl with
                       | Parser.DDefun(_, args, body, _, _) ->
                           let params' =
                               args
                               |> List.choose (function
                                   | Parser.MandatoryArg n -> Some n
                                   | _ -> None)

                           bodyExternSubst (Set.ofList params') body |> Map.toList
                       | _ -> []))
                |> List.distinct

            let externsToExport =
                // Exported by name — published as itself, because that name is
                // the one the importing module is meant to write.
                (exports
                 |> List.choose (fun n -> Map.tryFind n env.Registry.ClrExterns))
                @ (externsNamedByBodies
                   |> List.choose (fun (alias, published) ->
                       env.Registry.ClrExterns
                       |> Map.tryFind alias
                       |> Option.map (fun i -> { i with Alias = published })))
                |> List.distinctBy (fun i -> i.Alias)

            let exportedExternAliases =
                exports
                |> List.filter (fun n -> Map.containsKey n env.Registry.ClrExterns)
                |> Set.ofList

            if isLibrary && not externsNamedByBodies.IsEmpty then
                printfn
                    "Publishing %d foreign import(s) named by an exported body: %s"
                    externsNamedByBodies.Length
                    (externsNamedByBodies |> List.map fst |> String.concat ", ")

            let exportMetadata =
                if isLibrary && (not exports.IsEmpty || not typesToExport.IsEmpty) then
                    let quoted (name: string) = if name.StartsWith("'") then name else "'" + name

                    let serializeTrait (traitName: string) (info: TypedAST.TraitInfo) =
                        let assocStrs =
                            info.AssociatedTypes |> List.map (fun a -> $"(type %s{quoted a})")

                        // The implementor is written applied for an inline
                        // trait, which is what tells the reader it is one. The
                        // names of the arguments carry no information — only how
                        // many there are — so they are generated.
                        let implementorStr =
                            if info.HoleArity = 0 then
                                quoted info.ImplementorVar
                            else
                                let holeArgs =
                                    [ for i in 0 .. info.HoleArity - 1 -> $"'h%d{i}" ] |> String.concat " "
                                $"(%s{quoted info.ImplementorVar} %s{holeArgs})"

                        let methodStrs =
                            match info.Kind with
                            | TypedAST.InlineTrait ->
                                info.Templates
                                |> Map.toList
                                |> List.map (fun (mName, tpl) ->
                                    $"(: %s{mName} %s{Codegen.serializeTplType info.ImplementorVar tpl})")
                            | TypedAST.InterfaceTrait ->
                                info.Signatures
                                |> Map.toList
                                |> List.map (fun (mName, mType) -> $"(: %s{mName} %s{Codegen.serializeHMType mType})")

                        // Default bodies travel with the trait, so that an
                        // importing module can write an implementation of it and
                        // inherit them — which is the only way it ever gets
                        // them. The impls compiled *here* already have theirs
                        // spliced in and published as ordinary methods.
                        //
                        // A body that will not serialize is left out rather than
                        // mangled. The consequence is only that an importer has
                        // to write that method itself, and it is told so at its
                        // own `def/impl`.
                        let defaultStrs =
                            info.Defaults
                            |> Map.toList
                            |> List.choose (fun (mName, decl) ->
                                match decl with
                                | Parser.DDefun(_, args, body, _, _) when Codegen.isSerializableTemplate body ->
                                    let paramNames =
                                        args
                                        |> List.choose (function
                                            | Parser.MandatoryArg n -> Some n
                                            | _ -> None)

                                    // Keyword and rest parameters are a calling
                                    // convention, and the reader on the far side
                                    // rebuilds a plain `defun` from this.
                                    if paramNames.Length <> args.Length then
                                        None
                                    else
                                        let paramsStr = String.concat " " paramNames

                                        let body =
                                            AlphaRename.renameFree
                                                (bodyExternSubst (Set.ofList paramNames) body)
                                                body

                                        Some
                                            $"(defun (%s{mName} %s{paramsStr}) %s{Codegen.serializeExpr body})"
                                | _ -> None)

                        let parts = assocStrs @ methodStrs @ defaultStrs |> String.concat " "
                        $"(def/trait (%s{traitName} %s{implementorStr}) %s{parts})"

                    let serializeImpl (traitName: string) (targetType: TypedAST.HMType) (assocMap: Map<string, TypedAST.HMType>) =
                        let assocStrs =
                            assocMap
                            |> Map.toList
                            |> List.map (fun (n, t) -> $"(type %s{quoted n} %s{Codegen.serializeHMType t})")
                            |> String.concat " "
                        $"(def/impl/extern (%s{traitName} %s{Codegen.serializeHMType targetType}) %s{assocStrs})"

                    // A function's flat type says how many arguments it takes,
                    // not which of them are keyword arguments — and a keyword
                    // name is part of the calling convention. Flattening it here
                    // meant an importer could not pass one at all: the shorter
                    // argument list it wrote would not unify.
                    let serializeSignature (name: string) (t: TypedAST.HMType) =
                        match Map.tryFind name env.FunMetas, t with
                        | Some meta, TypedAST.TFun(argTypes, ret, _) when
                            not meta.KeywordParams.IsEmpty || meta.RestParam.IsSome
                            ->
                            let mandatory =
                                argTypes |> List.truncate meta.MandatoryCount |> List.map Codegen.serializeHMType

                            let keywords =
                                meta.KeywordParams
                                |> List.map (fun (n, kt) -> $"(#:{n} {Codegen.serializeHMType kt})")

                            let rest =
                                match meta.RestParam with
                                | Some rt -> [ $"#:rest {Codegen.serializeHMType rt}" ]
                                | None -> []

                            "(-> "
                            + String.concat " " (mandatory @ keywords @ rest @ [ Codegen.serializeHMType ret ])
                            + ")"
                        | _ -> Codegen.serializeHMType t

                    // `(import/extern (alias (: System.Math.Abs)))`, the same
                    // form the source was written in — the reader on the far
                    // side is the ordinary one, and an alias read back is
                    // registered exactly as a hand-written import would be.
                    //
                    // The declared type is emitted only if there was one. Adding
                    // one here would be actively wrong: it is what *narrows* an
                    // import to a single overload, so inventing it would publish
                    // whichever member of the set this module happened to use.
                    let serializeExtern (info: TypedAST.ClrExternInfo) =
                        let typeStr =
                            match info.DeclaredType with
                            | Some t -> " " + Codegen.serializeHMType t
                            | None -> ""

                        let exceptionStr =
                            if info.Exceptions.IsEmpty then ""
                            else " #:exceptions (" + String.concat " " info.Exceptions + ")"

                        // Republished, not re-derived. An importing module must
                        // register this alias exactly as the hand-written clause
                        // did — an `#:async` import read back without its flag
                        // is a call that silently stops being a yield point,
                        // and the Bjolang type it then has is the mangled name
                        // of a `Task`.
                        let asyncStr = if info.IsAsync then " #:async" else ""
                        let uncancellableStr = if info.Uncancellable then " #:uncancellable" else ""
                        let cancellableStr = if info.Cancellable then " #:cancellable" else ""

                        $"(import/extern (%s{info.Alias} (: %s{info.ClrType}.%s{info.MethodName}%s{typeStr}%s{exceptionStr}%s{asyncStr}%s{uncancellableStr}%s{cancellableStr})))"

                    let serializeExport name =
                        match Map.tryFind name env.Bindings with
                        | Some b ->
                            let (TypedAST.Scheme(_, constraints, t)) = b.Scheme
                            let typeStr = serializeSignature name t
                            if constraints.IsEmpty then $"(: %s{name} %s{typeStr})"
                            else
                                let constraintStrs = 
                                    constraints |> List.map (fun c ->
                                        let targetStr = Codegen.serializeHMType c.TargetType
                                        $"(%s{c.TraitName} %s{targetStr})")
                                let whereClause = "(where " + String.concat " " constraintStrs + ")"
                                $"(: %s{name} %s{typeStr} %s{whereClause})"
                        | None -> ""
                        
                    let rec serializeFType (ft: Parser.FType) : string =
                        match ft with
                        | Parser.TName(n, _) -> n
                        | Parser.TApp(n, args, _) -> $"({n} " + String.concat " " (List.map serializeFType args) + ")"
                        | Parser.TArrow(mandatory, keywords, restOpt, ret, colour, _) ->
                            let mandatoryStrs = mandatory |> List.map serializeFType
                            let keywordStrs = keywords |> List.map (fun (n, t) -> $"(#:{n} {serializeFType t})")
                            let restStrs = match restOpt with Some t -> [$"#:rest {serializeFType t}"] | None -> []
                            let allParts = mandatoryStrs @ keywordStrs @ restStrs @ [serializeFType ret]
                            "(" + TypedAST.arrowHead (TypedAST.colourEffect colour) + " "
                            + String.concat " " allParts + ")"
                        
                    let serializeTypeDef (td: Parser.TypeDef, isRec: bool) : string =
                        let quotedArgs = td.TypeArgs |> List.map (fun a -> if a.StartsWith("'") then a else "'" + a)
                        let typeArgsStr = if td.TypeArgs.IsEmpty then "" else " " + String.concat " " quotedArgs
                        let headStr = if td.TypeArgs.IsEmpty then td.Name else $"({td.Name}{typeArgsStr})"
                        let head = if isRec then "type-rec" else "type"
                        match td.Kind with
                        | Parser.Alias(ft) -> $"({head} (: {headStr} {serializeFType ft}))"
                        | Parser.Union(cases) ->
                            let serializeCase c =
                                match c with
                                | Parser.SimpleCase(n, _) -> n
                                | Parser.DataCase(n, args, _) -> $"({n} " + String.concat " " (List.map serializeFType args) + ")"
                            $"({head} (: {headStr} (Union\n  " + String.concat "\n  " (List.map serializeCase cases) + ")))"
                        // A record's *fields* are the part worth publishing.
                        // Without them an importer knows the name and nothing
                        // else: `record-get` on the type fails with "unknown
                        // record field", and an inline template that reads one
                        // cannot be re-inferred at the splice, so it falls back
                        // to an interface call. Both were silent — the type name
                        // still resolved, because an unrecognized one passes
                        // through to C# verbatim.
                        | Parser.Record(fields, isStruct) ->
                            let serializeField (f: Parser.RecordField) =
                                $"(: {f.Name} {serializeFType f.Type})"

                            // `Struct` and `Record` are different types on the
                            // far side, so the spelling has to survive: an
                            // importer that read a value type back as a
                            // reference one would emit the wrong C#.
                            let tag = if isStruct then "Struct" else "Record"

                            $"({head} (: {headStr} ({tag}\n  "
                            + String.concat "\n  " (List.map serializeField fields)
                            + ")))"
                        
                    // A trait method is published by its `def/trait`, which
                    // gives it the associated types a bare signature cannot
                    // express. Emitting a signature for it too would shadow
                    // that binding with a weaker one on the importing side.
                    let sigsStr =
                        (exports @ autoExports)
                        |> List.filter (fun name ->
                            not (Set.contains name exportedTraitMethods)
                            // An alias is published as its import, just below. It
                            // has no binding to read a type off, so leaving it in
                            // here would emit a blank line and export nothing.
                            && not (Set.contains name exportedExternAliases))
                        |> List.distinct
                        |> List.map serializeExport
                        |> String.concat "\n"

                    let externsStr = externsToExport |> List.map serializeExtern |> String.concat "\n"
                    let typesStr = typesToExport |> List.map serializeTypeDef |> String.concat "\n"

                    let traitsStr =
                        exportedTraits
                        |> Map.toList
                        |> List.map (fun (traitName, info) -> serializeTrait traitName info)
                        |> String.concat "\n"

                    // Implementations follow the traits they belong to: reading
                    // one back needs the trait already registered.
                    let implsStr =
                        env.Registry.Implementations
                        |> Map.toList
                        |> List.filter (fun ((traitName, _), _) -> Map.containsKey traitName exportedTraits)
                        |> List.map (fun ((traitName, _), (targetType, assocMap)) ->
                            serializeImpl traitName targetType assocMap)
                        |> String.concat "\n"

                    // Foreign imports precede the traits: a trait's own signature
                    // may name a type an `import/class` alias introduced, and an
                    // impl's inline template may call an `import/extern` one.
                    [ typesStr; externsStr; traitsStr; implsStr; sigsStr ]
                    |> List.filter (fun s -> not (System.String.IsNullOrWhiteSpace s))
                    |> String.concat "\n"
                else ""

            // Parameter names, body and qualification map as three distinct
            // fields. Bundling the parameters and body into a lambda would be
            // worse than redundant: `infer`'s `EFun` case binds each parameter
            // to a fresh metavariable in a scope of its own, discarding exactly
            // the concrete argument types the inliner supplies.
            let inlineMetadata =
                if isLibrary && not inlineTemplatesToExport.IsEmpty then
                    inlineTemplatesToExport
                    |> List.map (fun ((traitName, methodName, ctor), tpl) ->
                        let paramsStr = String.concat " " tpl.Params

                        let qualStr =
                            tpl.Qualification
                            |> Map.toList
                            |> List.map (fun (name, emitted) -> $"(\"{name}\" \"{emitted}\")")
                            |> String.concat " "

                        // Foreign aliases the body names are rewritten to the
                        // names they were published under, so that the splice
                        // resolves them where they were written rather than
                        // wherever it lands.
                        let body =
                            AlphaRename.renameFree
                                (bodyExternSubst (Set.ofList tpl.Params) tpl.Body)
                                tpl.Body

                        $"(inline-impl \"{traitName}\" \"{methodName}\" \"{ctor}\" \"{tpl.OriginModule}\" "
                        + $"({paramsStr}) {Codegen.serializeExpr body} ({qualStr}))")
                    |> String.concat "\n"
                else ""

            let csCode =
                Codegen.generateProgram
                    exportMetadata
                    inlineMetadata
                    (if isLibrary then dllDeps else [])
                    dllDeps
                    typedAst
            
            if options.Debug then
                File.WriteAllText("ast_dump.txt", sprintf "%A" typedAst)
            
            let mainArgKind =
                match Map.tryFind "main" env.Bindings with
                | Some b ->
                    let (TypedAST.Scheme(_, _, t)) = b.Scheme
                    match t with
                    | TypedAST.TFun([TypedAST.TCon("List", [TypedAST.TCon("System.String", [])])], _, _) -> "list_string"
                    | TypedAST.TFun([], _, _) -> "no_args"
                    | TypedAST.TFun(_, _, _) -> "other"
                    | _ -> "no_args" // Not a function, treat as no-args
                | None -> "other"

            /// Is the entry point a bjoroutine?
            ///
            /// It is allowed to be one, and that is the only way a program gets
            /// *into* fiber-land today: `bjo` and `sync` do not exist yet, so
            /// without this there would be no caller a yield point could
            /// legally appear under.
            let mainIsBjoroutine =
                match Map.tryFind "main" env.Bindings with
                | Some b ->
                    let (TypedAST.Scheme(_, _, t)) = b.Scheme

                    match t with
                    | TypedAST.TFun(_, _, TypedAST.EAsync) -> true
                    | _ -> false
                | None -> false

            let mainModuleClass = Path.GetFileNameWithoutExtension(inputFilePath) |> Codegen.moduleClassName

            // Everything this program links against, where it really lives.
            // Nothing is ever copied next to the output: an assembly has one
            // home, and a program built from it points back at that home.
            let linkedAssemblies =
                (Paths.runtimeAssemblies @ dllDeps)
                |> List.filter File.Exists
                |> List.map Path.GetFullPath
                |> List.distinct

            // The directories the running program has to probe to find those
            // assemblies. The default load context only looks beside the
            // executable, so the entry point installs a resolver that looks
            // here instead.
            let probeDirs =
                linkedAssemblies
                |> List.map Path.GetDirectoryName
                |> List.distinct

            let resolverCode =
                if isLibrary || probeDirs.IsEmpty then ""
                else
                    let dirLiterals =
                        probeDirs
                        |> List.map (fun d -> "@\"" + d.Replace("\"", "\"\"") + "\"")
                        |> String.concat ", "

                    "    private static readonly string[] BjolangProbeDirs = new string[] { " + dirLiterals + " };\n" +
                    "    private static void InstallAssemblyResolver() {\n" +
                    "        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) => {\n" +
                    "            var libOverride = System.Environment.GetEnvironmentVariable(\"BJOLANG_LIB\");\n" +
                    "            if (!string.IsNullOrEmpty(libOverride)) {\n" +
                    "                var overridden = System.IO.Path.Combine(libOverride, \"std\", name.Name + \".dll\");\n" +
                    "                if (System.IO.File.Exists(overridden)) return context.LoadFromAssemblyPath(overridden);\n" +
                    "            }\n" +
                    "            foreach (var dir in BjolangProbeDirs) {\n" +
                    "                var candidate = System.IO.Path.Combine(dir, name.Name + \".dll\");\n" +
                    "                if (System.IO.File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);\n" +
                    "            }\n" +
                    "            return null;\n" +
                    "        };\n" +
                    "    }\n"

            // `Main` itself must not touch a single type from a linked
            // assembly: the JIT would then have to load that assembly before
            // the resolver is in place. All real work lives in `Run`, which is
            // not compiled until it is called.
            // A bjoroutine `main` returns a `Fiber<T>`, which is a description of
            // work rather than the work's result, so the entry point has to
            // drive it. `RunToCompletion` starts the body on *this* thread and
            // blocks until it lands — which is safe here and nowhere else: the
            // rule it documents is never to call it from a pool thread, and the
            // thread `Main` runs on is the one thread in the process that is
            // certainly not one.
            let callMain (argExpr: string) =
                if mainIsBjoroutine then
                    $"        _ = Bjoml.Bjo.RunToCompletion(() => %s{mainModuleClass}.main(%s{argExpr}));\n"
                else
                    $"        %s{mainModuleClass}.main(%s{argExpr});\n"

            let runBody =
                if mainArgKind = "list_string" then
                    $"        SchemeList.SchemeList<string> bjoArgs = SchemeList.SchemeList.Empty<string>();\n" +
                    $"        for (int i = args.Length - 1; i >= 0; i--) {{\n" +
                    $"            bjoArgs = SchemeList.SchemeList.Cons(args[i], bjoArgs);\n" +
                    $"        }}\n" +
                    callMain "bjoArgs"
                elif mainArgKind = "no_args" then
                    callMain ""
                else
                    callMain "0"

            let entryPointCode =
                if isLibrary then ""
                else
                    "\npublic static class BjolangEntryPoint {\n" +
                    resolverCode +
                    "    public static void Main(string[] args) {\n" +
                    (if resolverCode = "" then "" else "        InstallAssemblyResolver();\n") +
                    "        Run(args);\n" +
                    "    }\n" +
                    "    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]\n" +
                    "    private static void Run(string[] args) {\n" +
                    runBody +
                    "    }\n" +
                    "}\n"

            let fullCode = csCode + entryPointCode

            let tmpDir = Path.Combine(Path.GetTempPath(), "Bjolang_" + System.Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tmpDir) |> ignore
            
            let projType = if isLibrary then "Library" else "Exe"

            // `Private` false keeps MSBuild from copying the referenced
            // assemblies into the output directory. They are resolved from
            // where they were built, at runtime, by the entry point's resolver.
            //
            // Generated from `linkedAssemblies` rather than written out, so the
            // set of runtime assemblies is stated once, in `Paths`. It used to
            // be spelled here as well, which is how a newly added one — Bjoml —
            // could be on the resolver's probe path and still not be referenced
            // at compile time.
            let dllReferences =
                linkedAssemblies
                |> List.map (fun dllPath ->
                    let name = Path.GetFileNameWithoutExtension(dllPath)
                    $"    <Reference Include=\"{name}\">\n      <HintPath>{dllPath}</HintPath>\n      <Private>false</Private>\n    </Reference>")
                |> String.concat "\n"
                
            let csprojContent = $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>{projType}</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
{dllReferences}
  </ItemGroup>
</Project>"""
            File.WriteAllText(Path.Combine(tmpDir, "Project.csproj"), csprojContent)
            File.WriteAllText(Path.Combine(tmpDir, "Program.cs"), fullCode)
            if options.Debug then
                File.WriteAllText("out.cs", fullCode)
            
            let outDir = Path.GetFullPath(if System.String.IsNullOrWhiteSpace(Path.GetDirectoryName(outputFilePath)) then "." else Path.GetDirectoryName(outputFilePath))
            let assemblyName = Path.GetFileNameWithoutExtension(outputFilePath)
            
            printfn "Invoking C# Compiler..."
            
            // This does a fast compilation using the C# dll instead of the dotnet exe.
            let tryFastCompile () =
                try
                    let loc = typeof<obj>.Assembly.Location
                    let dotnetRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(loc), "..", "..", ".."))
                    let sdkDir = Path.Combine(dotnetRoot, "sdk")
                    if not (Directory.Exists(sdkDir)) then None
                    else
                        let cscFiles = Directory.GetFiles(sdkDir, "csc.dll", SearchOption.AllDirectories)
                        match cscFiles |> Array.tryHead with
                        | None -> None
                        | Some cscDll ->
                            let target = if isLibrary then "library" else "exe"
                            let runtimeDir = Path.GetDirectoryName(loc)

                            // Reference assemblies, not the implementation ones.
                            //
                            // `typeof<obj>.Assembly.Location` sits in the shared
                            // framework, where the core library is
                            // `System.Private.CoreLib` — an implementation
                            // detail. An assembly compiled against it records a
                            // dependency on it by name, and anything that later
                            // consumes that assembly through the ordinary
                            // reference assemblies cannot resolve the types its
                            // signatures mention: a trait whose associated type
                            // is a tuple fails with "the type '(, )' is defined
                            // in an assembly that is not referenced". That is
                            // exactly the MSBuild path this function falls back
                            // to, so the two builds have to agree on which
                            // assemblies they mean.
                            let bclDir =
                                let refPack =
                                    Path.Combine(
                                        dotnetRoot,
                                        "packs",
                                        "Microsoft.NETCore.App.Ref",
                                        Path.GetFileName(runtimeDir),
                                        "ref"
                                    )

                                if Directory.Exists refPack then
                                    // One target-framework directory inside.
                                    match Directory.GetDirectories(refPack) |> Array.tryHead with
                                    | Some tfmDir -> tfmDir
                                    | None -> runtimeDir
                                else
                                    runtimeDir

                            let bclRefs =
                                Directory.GetFiles(bclDir, "*.dll")
                                |> Array.map (fun p -> $"\"-r:{p}\"")
                                |> String.concat " "
                            
                            let userRefs =
                                linkedAssemblies
                                |> List.map (fun p -> $"\"-r:{p}\"")
                                |> String.concat " "
                                
                            let csFile = Path.Combine(tmpDir, "Program.cs")
                            let targetPath = Path.GetFullPath(outputFilePath)
                            // Optimization is not free to leave off: without
                            // `-optimize+` Roslyn marks the assembly as
                            // debuggable, which tells the JIT to leave it
                            // alone, and the same generated C# then runs
                            // several times slower. It is off only under `-d`,
                            // where stepping through the code matters more than
                            // how fast it runs.
                            let codeGenArgs =
                                if options.Debug then "-optimize- -debug:portable" else "-optimize+"

                            let cscArgs = $"exec \"{cscDll}\" -noconfig -nullable:enable {codeGenArgs} -target:{target} -out:\"{targetPath}\" \"{csFile}\" {userRefs} {bclRefs}"
                            let psi = System.Diagnostics.ProcessStartInfo("dotnet", cscArgs)
                            psi.UseShellExecute <- false
                            psi.RedirectStandardOutput <- true
                            psi.RedirectStandardError <- true
                            let p = System.Diagnostics.Process.Start(psi)
                            let stdout = p.StandardOutput.ReadToEnd()
                            let stderr = p.StandardError.ReadToEnd()
                            p.WaitForExit()

                            // csc ran and said no. Print what it said.
                            //
                            // Returning `None` here falls back to the MSBuild
                            // path, which is right — the two builds can differ,
                            // and one that fails here may still succeed there.
                            // But the diagnostics used to be dropped on the
                            // floor, and MSBuild's are not always the same
                            // ones: a program whose real fault was a type error
                            // was reported as "does not contain a static
                            // 'Main'", which is a description of a consequence
                            // three steps removed from the cause.
                            //
                            // Only reached when csc actually started. Failing
                            // to *find* csc returns earlier, so this never
                            // reports an environmental miss as a program error.
                            if p.ExitCode <> 0 then
                                printfn "C# compilation reported:"
                                if not (System.String.IsNullOrWhiteSpace stdout) then printfn "%s" (stdout.TrimEnd())
                                if not (System.String.IsNullOrWhiteSpace stderr) then printfn "%s" (stderr.TrimEnd())

                            if p.ExitCode = 0 then
                                let assemblyBaseName = Path.GetFileNameWithoutExtension(targetPath)

                                // An optimized build has no symbols, so a
                                // leftover one from an earlier `-d` build would
                                // describe code that no longer exists. Its
                                // absence is also how a caller can tell which
                                // way this binary was built.
                                if not options.Debug then
                                    let stalePdb = Path.ChangeExtension(targetPath, ".pdb")
                                    if File.Exists(stalePdb) then
                                        try File.Delete(stalePdb) with | _ -> ()
                                if not isLibrary then
                                    let runtimeConfigPath = Path.ChangeExtension(targetPath, ".runtimeconfig.json")
                                    let runtimeConfigContent = "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\",\n    \"framework\": {\n      \"name\": \"Microsoft.NETCore.App\",\n      \"version\": \"10.0.0\"\n    }\n  }\n}"
                                    File.WriteAllText(runtimeConfigPath, runtimeConfigContent)

                                    // The manifest names only the program
                                    // itself. Listing a dependency here would
                                    // make the host demand a copy of it beside
                                    // the executable — an asset path in a
                                    // deps.json is always resolved against the
                                    // application directory, which is exactly
                                    // what forced the standard library to be
                                    // duplicated into every output directory.
                                    // The entry point's resolver loads them
                                    // from where they live instead.
                                    let depsJson =
                                        "{\n  \"runtimeTarget\": { \"name\": \".NETCoreApp,Version=v10.0\", \"signature\": \"\" },\n  \"compilationOptions\": {},\n  \"targets\": {\n    \".NETCoreApp,Version=v10.0\": {\n      \""
                                        + assemblyBaseName
                                        + "/1.0.0\": {\n        \"runtime\": { \""
                                        + assemblyBaseName
                                        + ".dll\": {} }\n      }\n    }\n  },\n  \"libraries\": {\n    \""
                                        + assemblyBaseName
                                        + "/1.0.0\": { \"type\": \"project\", \"serviceable\": false, \"sha512\": \"\" }\n  }\n}"
                                    let depsJsonPath = Path.ChangeExtension(targetPath, ".deps.json")
                                    File.WriteAllText(depsJsonPath, depsJson)
                                printfn $"Successfully built %s{outputFilePath}"
                                try Directory.Delete(tmpDir, true) with | _ -> ()
                                Some 0
                            else
                                None
                with _ -> None

            match tryFastCompile () with
            | Some code -> code
            | None ->
                let projPath = Path.Combine(tmpDir, "Project.csproj")
                let configuration = if options.Debug then "Debug" else "Release"
                let psi = new System.Diagnostics.ProcessStartInfo(
                    FileName = "dotnet",
                    Arguments = $"build \"%s{projPath}\" -c %s{configuration} -o \"%s{outDir}\" /p:AssemblyName=%s{assemblyName}",
                    UseShellExecute = false
                )
                let p = System.Diagnostics.Process.Start(psi)
                p.WaitForExit()
                
                if p.ExitCode = 0 then
                    let generatedDll = Path.Combine(outDir, assemblyName + ".dll")
                    if System.IO.File.Exists(generatedDll) && Path.GetFullPath(generatedDll) <> Path.GetFullPath(outputFilePath) then
                        if System.IO.File.Exists(outputFilePath) then System.IO.File.Delete(outputFilePath)
                        System.IO.File.Move(generatedDll, outputFilePath)
                    
                    let genRuntimeConfig = Path.Combine(outDir, assemblyName + ".runtimeconfig.json")
                    let outRuntimeConfig = Path.ChangeExtension(outputFilePath, ".runtimeconfig.json")
                    if System.IO.File.Exists(genRuntimeConfig) && Path.GetFullPath(genRuntimeConfig) <> Path.GetFullPath(outRuntimeConfig) then
                        if System.IO.File.Exists(outRuntimeConfig) then System.IO.File.Delete(outRuntimeConfig)
                        System.IO.File.Move(genRuntimeConfig, outRuntimeConfig)

                    printfn $"Successfully built %s{outputFilePath}"
                    try Directory.Delete(tmpDir, true) with | _ -> ()
                    0
                else
                    printfn "C# Compilation failed."
                    // Leave tmpDir for debugging
                    printfn $"Temp directory: %s{tmpDir}"
                    1
        | None ->
            printfn "Compilation failed."
            1
    with ex ->
        Diagnostics.reportFailure ex
        printfn "Compilation failed."
        1
