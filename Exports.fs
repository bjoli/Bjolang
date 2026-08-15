/// The metadata a compiled module publishes about itself.
///
/// Three `[AssemblyMetadata]` strings, read back by `Pipeline` when somebody
/// imports the resulting `.dll`. Between them they carry everything an importer
/// needs that the emitted C# cannot express: the *types* of exported bindings,
/// the traits and impls those bindings dispatch through, the foreign imports
/// they resolve overloads against, the bodies that may be inlined at a call
/// site, and the names that are macros rather than functions.
///
/// Only a library publishes any of it. An executable has no importer.
module Bjolang.Exports

open Bjolang

/// `BjolangExports`, `BjolangInlineImpls`, `BjolangMacros`, in that order.
let metadata
    (env: TypedAST.Env)
    (typedAst: TypedAST.TDecl list)
    (declaredMacros: string list)
    (inputFilePath: string)
    (isLibrary: bool)
    : string * string * string =
    let exports =
        typedAst
        |> TypedAST.collectDecls (function
            | TypedAST.TExport(names, _)
            | TypedAST.TReExport(names, _) -> names
            | _ -> [])

    let typesToExport =
        typedAst
        |> TypedAST.collectDecls (function
            | TypedAST.TType(defs, _) -> defs |> List.map (fun d -> d, false)
            | TypedAST.TTypeRec(defs, _) -> defs |> List.map (fun d -> d, true)
            | _ -> [])
    
    // A trait travels with its methods. Whichever module publishes a
    // trait method has to publish the trait itself and every
    // implementation of it, or the importer sees a plain function whose
    // associated types cannot be resolved and whose calls cannot be
    // dispatched to an impl class.
    let traitMethodNames (info: TypedAST.TraitInfo) =
        (info.Signatures |> Map.toList |> List.map fst)
        @ (info.Templates |> Map.toList |> List.map fst)

    let exportedTraits =
        env.Registry.Traits
        |> Map.filter (fun _ info ->
            traitMethodNames info |> List.exists (fun m -> List.contains m exports))

    let exportedTraitMethods =
        exportedTraits
        |> Map.toList
        |> List.collect (snd >> traitMethodNames)
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
            let modTag = Naming.moduleNameOfPath inputFilePath

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
                   let params' = Parser.mandatoryNames args
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
                            let paramNames = Parser.mandatoryNames args

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

            // The `(where ...)` travels with the impl, and it is not
            // decoration: the importing module is where the dictionary
            // for `(List int)` gets built, and it cannot build one
            // without knowing that a `(->str int)` goes inside. The
            // order is the constructor's, so it is preserved as read.
            let serializeImpl
                (traitName: string)
                (typeKey: string)
                (targetType: TypedAST.HMType)
                (assocMap: Map<string, TypedAST.HMType>)
                =
                let assocStrs =
                    assocMap
                    |> Map.toList
                    |> List.map (fun (n, t) -> $"(type %s{quoted n} %s{Codegen.serializeHMType t})")
                    |> String.concat " "

                let whereStr =
                    match Map.tryFind (traitName, typeKey) env.Registry.ImplTargets with
                    | Some target when not target.Constraints.IsEmpty ->
                        let parts =
                            target.Constraints
                            |> List.map (fun c ->
                                $"(%s{c.TraitName} %s{Codegen.serializeHMType c.TargetType})")
                            |> String.concat " "

                        " (where " + parts + ")"
                    | _ -> ""

                $"(def/impl/extern (%s{traitName} %s{Codegen.serializeHMType targetType}) %s{assocStrs}%s{whereStr})"

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

                // Whether the member is static or an instance one is
                // deliberately *not* written here: the reader on the far
                // side asks the same metadata and gets the same answer,
                // and a flag would be a second copy of a fact that
                // already has one.
                let accessorStr =
                    match info.Kind with
                    | TypedAST.ExternGet -> " #:get"
                    | TypedAST.ExternSet -> " #:set"
                    | TypedAST.ExternMethod -> ""

                $"(import/extern (%s{info.Alias} (: %s{info.ClrType}.%s{info.MemberName}%s{typeStr}%s{exceptionStr}%s{asyncStr}%s{uncancellableStr}%s{cancellableStr}%s{accessorStr})))"

            /// `None` for a name with no binding to read a type off.
            ///
            /// The result is joined with newlines, so answering with an
            /// empty string would publish a blank line and export
            /// nothing — silently, which is the failure this shape
            /// exists to make impossible.
            let serializeExport name : string option =
                Map.tryFind name env.Bindings
                |> Option.map (fun b ->
                    let (TypedAST.Scheme(_, constraints, t)) = b.Scheme
                    let typeStr = serializeSignature name t

                    if constraints.IsEmpty then
                        $"(: %s{name} %s{typeStr})"
                    else
                        let constraintStrs =
                            constraints |> List.map (fun c ->
                                let targetStr = Codegen.serializeHMType c.TargetType
                                $"(%s{c.TraitName} %s{targetStr})")

                        let whereClause = "(where " + String.concat " " constraintStrs + ")"
                        $"(: %s{name} %s{typeStr} %s{whereClause})")
                
            let serializeFType = Codegen.serializeFType

            let serializeTypeDef (td: Parser.TypeDef, isRec: bool) : string =
                let quotedArgs = td.TypeArgs |> List.map (fun a -> if a.StartsWith("'") then a else "'" + a)
                let typeArgsStr = if td.TypeArgs.IsEmpty then "" else " " + String.concat " " quotedArgs
                let headStr = if td.TypeArgs.IsEmpty then td.Name else $"({td.Name}{typeArgsStr})"
                let head = if isRec then "type-rec" else "type"
                match td.Kind with
                | Parser.Alias(ft) -> $"({head} (: {headStr} {serializeFType ft}))"
                | Parser.Union(cases) ->
                    // `#:literal` travels with the case. It decides
                    // which constructor a quoted literal elaborates
                    // into, so a union that is unambiguous where it was
                    // declared has to stay unambiguous where it is
                    // imported.
                    let serializeCase c =
                        match c with
                        | Parser.SimpleCase(n, _) -> n
                        | Parser.DataCase(n, args, isLiteral, _) ->
                            let parts =
                                List.map serializeFType args
                                @ (if isLiteral then [ "#:literal" ] else [])

                            $"({n} " + String.concat " " parts + ")"
                    $"({head} (: {headStr} (Union\n  " + String.concat "\n  " (List.map serializeCase cases) + ")))"
                // A record's *fields* are the part worth publishing.
                // Without them an importer knows the name and nothing
                // else: `record-ref` on the type fails with "unknown
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
                |> List.choose serializeExport
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
                |> List.map (fun ((traitName, typeKey), (targetType, assocMap)) ->
                    serializeImpl traitName typeKey targetType assocMap)
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

    // The macros this assembly publishes, and the class holding them.
    //
    // Kept out of `BjolangExports` deliberately: those entries are
    // signatures, and a macro is not a binding an importer may call. An
    // importer needs one thing from this — that the name is a macro and
    // where the transformer lives — and reads it before it parses a
    // line of its own source.
    //
    // Every macro is published. There is no `(export ...)` for them,
    // because a macro that cannot be used from anywhere is the one thing
    // a macro cannot be: it is unusable in its own module by
    // construction.
    let macroMetadata =
        if isLibrary && not declaredMacros.IsEmpty then
            // The Bjolang module name, not the C# class. The reader
            // needs both — the class to reflect on, and the module to
            // spell `Module_Module::helper` for a template that names
            // one of this module's own bindings — and `Naming` already
            // derives the second from the first.
            let moduleName = Naming.moduleNameOfPath inputFilePath

            declaredMacros
            |> List.map (fun name -> $"(macro \"{name}\" \"{moduleName}\")")
            |> String.concat "\n"
        else ""

    if isLibrary && not declaredMacros.IsEmpty then
        printfn "Publishing %d macro(s): %s" declaredMacros.Length (String.concat ", " declaredMacros)

    exportMetadata, inlineMetadata, macroMetadata
