/// Read, compile, load, print, loop.
///
/// Deliberately not an interpreter. An entry is compiled by the same
/// `runFullFrontendPipeline` a build runs, emitted by the same `Codegen`, and
/// published by the same `Exports.metadata` — so there is no second account of
/// what Bjolang means anywhere in here, and nothing that could drift from the
/// compiler. What this module contributes is a driver: it decides what source
/// an entry becomes, which earlier entries it links, and how the result is
/// shown.
///
/// One assembly per entry. Entry N is written to `Bjo_Repl_N.bjo` in a session
/// directory, compiled to `Bjo_Repl_N.dll`, and loaded; entry N+1 reaches
/// entry N's bindings by importing that `.dll`, which is the ordinary module
/// mechanism and needs nothing of its own. `Codegen` already emits a top-level
/// `def` as a `public static readonly` field and a `defun` as a `public static`
/// method on the module class, which is exactly what an importing entry links
/// against.
///
/// No line editing, no completion, no history. Run it under `rlwrap`.
module Bjolang.Repl

open System
open System.IO
open System.Runtime.Loader
open Bjolang.Lexer
open Bjolang.Parser

/// One entry that has been compiled and loaded.
type private Entry =
    { Index: int
      /// Every name a later entry might write that this one answers for — see
      /// `providedNames`. What a later entry looks itself up in.
      Provides: Set<string>
      /// Linked by every later entry regardless — see `isSticky`.
      Sticky: bool
      DllPath: string }

type private State =
    { Next: int
      /// Import forms typed at the prompt, as written, oldest first.
      ///
      /// Replayed into every subsequent entry rather than remembered as
      /// compiler state. `Session.replEntry` clears the macro table between
      /// entries, so a macro imported at entry 2 is re-registered at entry 3 by
      /// that entry's own import — which is the same path a build takes, and
      /// the reason `(import ...)` at the prompt needs no save/restore of its
      /// own.
      Imports: string list
      /// Signatures typed at the prompt that nothing has defined yet, as
      /// `name, the form as written`.
      ///
      /// A top-level `defun` must have one — `Inference` refuses every name but
      /// `main` without it — and at a prompt the signature and the definition
      /// arrive on separate lines, because a complete form is a complete entry.
      /// So a signature waits for the entry that defines its name, is replayed
      /// into that entry's source, and is then forgotten. Which is what the
      /// same two lines in a file mean.
      Pending: (string * string) list
      /// Newest first, so the first entry defining a name is the latest one to
      /// have done so.
      Entries: Entry list
      Directory: string }

// ---------------------------------------------------------------------------
// Reading
// ---------------------------------------------------------------------------

/// Is there an unclosed bracket?
///
/// Asked of the tokens rather than the characters, so a `(` inside a string or
/// a comment is not one. A text the lexer cannot finish — an unterminated
/// string — is also incomplete, which is what continues the line.
let private incomplete (text: string) : bool =
    try
        let tokens = Lexer.tokenize "<repl>" text

        let depth =
            tokens
            |> List.sumBy (fun t ->
                match t.Token with
                | LParen | LBracket | LBrace -> 1
                | RParen | RBracket | RBrace -> -1
                | _ -> 0)

        depth > 0
    with _ ->
        true

/// Reads one entry, continuing the line while brackets are open.
let private readEntry (prompt: string) (continuation: string) : string option =
    let rec go (acc: string) =
        Console.Out.Write(if acc = "" then prompt else continuation)
        Console.Out.Flush()

        match Console.In.ReadLine() with
        | null -> if acc.Trim() = "" then None else Some acc
        | line ->
            let text = if acc = "" then line else acc + "\n" + line
            if incomplete text then go text else Some text

    go ""

// ---------------------------------------------------------------------------
// What an entry is
// ---------------------------------------------------------------------------

/// The top-level names a declaration introduces.
///
/// Only what an importing entry can name. `Parser.boundNames` is the neighbour
/// of this and answers a different question — it includes a `defun`'s
/// parameters, because it exists to stop a macro's template capturing one.
let private definedNames (decl: Parser.Decl) : string list =
    match decl with
    | Parser.DDef(n, _, _)
    | Parser.DDefMutable(n, _, _)
    | Parser.DDefun(n, _, _, _, _) -> [ n ]
    | Parser.DDefTuple(names, _, _) -> names
    | Parser.DType(defs, _)
    | Parser.DTypeRec(defs, _) -> defs |> List.map (fun d -> d.Name)
    | Parser.DTrait(n, _, _, _, _, _, _) -> [ n ]
    | _ -> []

/// Of those, the ones an `(export ...)` demands a signature for.
///
/// A type publishes its declaration and a trait its methods, so neither has a
/// signature to be missing. A binding does.
let private bindingNames (decl: Parser.Decl) : string list =
    match decl with
    | Parser.DType _
    | Parser.DTypeRec _
    | Parser.DTrait _ -> []
    | other -> definedNames other

/// Every name a later entry might write that this one answers for.
///
/// Wider than `definedNames`, and it has to be: a union's cases and a trait's
/// methods are what source actually writes — `(Circle 2.0)`, `(->str x)` — and
/// nothing mentions the type or the trait by name at all. They cannot go in the
/// `(export ...)` list, which is why this is a second function rather than one:
/// `(export Circle)` is refused, since a case has no signature and travels with
/// the type that declares it.
let private providedNames (decl: Parser.Decl) : string list =
    match decl with
    | Parser.DType(defs, _)
    | Parser.DTypeRec(defs, _) ->
        defs
        |> List.collect (fun td ->
            td.Name
            :: (match td.Kind with
                | Parser.Union cases ->
                    cases
                    |> List.map (function
                        | Parser.SimpleCase(n, _) -> n
                        | Parser.DataCase(n, _, _, _) -> n)
                // A record is constructed by its own name, and an opaque type
                // and an alias offer no constructor at all.
                | _ -> []))
    | Parser.DTrait(name, _, _, _, signatures, _, _) -> name :: List.map fst signatures
    | other -> definedNames other

/// Does this entry have to be linked by every entry after it?
///
/// An impl is not a name. Nothing a later entry writes mentions it, so nothing
/// would pull it in — and a trait method call would then dispatch as though the
/// impl had never been written. So an entry that declares one is linked
/// unconditionally, which is what makes `(def/impl ...)` at the prompt affect
/// the entries after it.
///
/// The entries *before* it are a different matter and cannot be helped:
/// `Lowering` bakes a dictionary choice into IL, so an impl written at entry 9
/// is not in the code entry 4 already emitted.
let private isSticky (decl: Parser.Decl) : bool =
    match decl with
    | Parser.DImpl _
    | Parser.DImplExtern _
    | Parser.DInlineImpl _
    | Parser.DTrait _ -> true
    | _ -> false

/// What the user typed, as the compiler reads it.
///
/// `Parser.tryParseDecl` is asked rather than the head symbol matched here, so
/// that what counts as a declaration is decided in one place. Run *before* the
/// entry's own session is reset, which is what makes a macro imported by an
/// earlier entry visible: the table still holds what the previous entry's
/// import registered.
type private Shape =
    /// Every form is a declaration: what it defines, and what it only declared
    /// the type of.
    | Definitions of
        defined: string list *
        provided: string list *
        bindings: string list *
        signed: (string * SExpr) list *
        sticky: bool
    /// One form, and it is an expression. Its value is what the prompt shows.
    | Expression
    | Malformed of reason: string

let private shapeOf (forms: SExpr list) : Shape =
    let asDecl (form: SExpr) =
        try
            Parser.tryParseDecl form |> Option.map (fun d -> d, form)
        with _ ->
            // A declaration whose *body* is malformed still reads as one. The
            // real diagnostic comes from compiling it, where it has a position.
            Some(Parser.DExport([], getRange form), form)

    match forms with
    | [] -> Malformed "nothing to evaluate"
    | _ ->
        let parsed = forms |> List.map asDecl

        if parsed |> List.forall Option.isSome then
            let decls = parsed |> List.map Option.get

            let signed =
                decls
                |> List.choose (function
                    | Parser.DSignature(name, _, _, _), form -> Some(name, form)
                    | _ -> None)

            Definitions(
                decls |> List.collect (fst >> definedNames),
                decls |> List.collect (fst >> providedNames),
                decls |> List.collect (fst >> bindingNames),
                signed,
                decls |> List.exists (fst >> isSticky)
            )
        elif forms.Length = 1 then
            Expression
        else
            Malformed "an entry is either a group of definitions or one expression"

// ---------------------------------------------------------------------------
// Building an entry's source
// ---------------------------------------------------------------------------

/// The two names an expression entry is compiled under.
///
/// `def` rather than `defun`, because a top-level `defun` must carry a
/// signature and the whole point of the wrapper is that the REPL does not know
/// the type. A `def` is inferred, is emitted as a `public static readonly`
/// field, and runs its initializer in the module's static constructor — so
/// reading the field is what evaluates the entry.
///
/// Rendering is `->str`, the language's own. The prelude gives it a blanket
/// impl over every type, so there is no value the prompt cannot show and no
/// printer in the compiler to keep in step with the language.
///
/// Two bindings rather than one so that the *type* of the value is still
/// readable off `env` afterwards: `__bjo_show` is a `string` whatever it
/// wrapped, and whether to print at all depends on what `__bjo_value` was.
let private valueName = "__bjo_value"
let private showName = "__bjo_show"

/// Rewrites a relative path in an `(import "...")` to an absolute one.
///
/// At a prompt, "relative" means relative to where the user is standing. An
/// entry is written to a session directory in `/tmp` that they never see and
/// could not have meant, and `(import "helper.bjo")` resolving against *that*
/// is an import error naming a path nobody typed.
///
/// Only string paths. `(import (std prelude))` is a module path, anchored to
/// the installation, and is already independent of anyone's working directory.
let private absolutizeImports (text: string) (forms: SExpr list) : string =
    let rec paths (s: SExpr) =
        match s with
        | SAtom { Token = StringLit p } -> [ p ]
        | SList(items, _) -> items |> List.collect paths
        | _ -> []

    let imported =
        forms
        |> List.collect (function
            | SList(SAtom { Token = Symbol "import" } :: rest, _) -> rest |> List.collect paths
            | _ -> [])

    imported
    |> List.filter (fun p -> not (Path.IsPathRooted p))
    |> List.fold (fun (acc: string) p -> acc.Replace($"\"%s{p}\"", $"\"%s{Path.GetFullPath p}\"")) text

/// A binding given to an entry that would otherwise publish nothing.
///
/// `Exports.metadata` writes a module's traits and impls only when the module
/// has a surface to write them onto — an export or a type. An entry that is
/// nothing but `(def/impl ...)` has neither, so its impl never crossed and a
/// later entry dispatched as though it had not been written. One exported
/// binding is enough to open the metadata block; nothing reads it.
let private anchorName = "__bjo_anchor"

/// The lines of `text` a form was written on.
///
/// Whole lines, which is coarse and is enough: what it is used for is replaying
/// an `(import ...)` or a `(: f ...)` into a later entry, and those are written
/// on lines of their own.
let private textOf (text: string) (form: SExpr) : string =
    let r = getRange form

    text.Split('\n')
    |> Array.skip (r.Start.Line - 1)
    |> Array.truncate (r.End.Line - r.Start.Line + 1)
    |> String.concat "\n"

/// Which earlier entries this one has to link.
///
/// Every symbol the entry mentions, resolved to the newest entry that defines
/// it. Over-approximate on purpose: a local named `x` will pull in an earlier
/// entry that happens to define an `x`, which costs one assembly reference and
/// changes nothing — a `let`-bound `x` shadows the import, as it would shadow
/// any other. Under-approximating is what would be wrong, and cannot happen,
/// since every reference to a binding is a symbol in the text.
///
/// The point of it is that per-entry cost does not grow with session length.
/// Importing all N previous entries would make entry N re-read N sets of
/// metadata; importing only what is named keeps a session flat.
///
/// A name this entry *defines* is never one of them, and that is what makes
/// redefinition work at all. `(defun (f x) (* x 100))` mentions `f`, so without
/// this it would import the earlier entry's `f` alongside defining its own —
/// and the import wins, so redefining a name silently produced the old one.
/// In a file the same text is a definition and, if it calls itself, a
/// recursion; here too.
///
/// Ascending, so that when two entries both define a name the later import
/// wins — the same rule `loadModuleGraph` gives any two plain imports, and what
/// makes an earlier entry's binding visible to a later one.
let private neededEntries (state: State) (defined: string list) (sources: string list) : Entry list =
    let symbolsIn (text: string) =
        try
            Lexer.tokenize "<repl>" text
            |> List.choose (fun t ->
                match t.Token with
                | Symbol name -> Some name
                | _ -> None)
        with _ ->
            []

    let mentioned =
        Set.difference (sources |> List.collect symbolsIn |> Set.ofList) (Set.ofList defined)

    let byName =
        mentioned
        |> Seq.choose (fun name -> state.Entries |> List.tryFind (fun e -> Set.contains name e.Provides))
        |> List.ofSeq

    (byName @ (state.Entries |> List.filter (fun e -> e.Sticky)))
    |> List.distinctBy (fun e -> e.Index)
    |> List.sortBy (fun e -> e.Index)

/// The `.bjo` an entry becomes.
///
/// Imports and exports may sit anywhere in a file, which is what lets them go
/// last: `loadModuleGraph` collects every `(import ...)` in a module before it
/// parses a line of it, which is also what lets a macro be used above the
/// import that brings it in.
/// `publishing` is false for the first of the two passes a definition entry
/// takes: without an `(export ...)` there is nothing demanding a signature, so
/// the entry checks, and what it inferred is what the second pass writes down.
let private entrySource
    (state: State)
    (linked: Entry list)
    (shape: Shape)
    (replayed: string list)
    (publishing: bool)
    (text: string)
    : string =
    let preamble =
        [ yield! state.Imports
          yield! replayed
          for e in linked -> $"(import \"%s{Path.GetFileName e.DllPath}\")" ]

    let trailer =
        match shape with
        | Definitions(defined, _, _, _, sticky) when publishing && (not defined.IsEmpty || sticky) ->
            // Everything an entry defines is exported, whether or not it says
            // so. At a prompt there is no distinction to draw: the module is
            // one line long and its only importer is the next line.
            let anchor = if defined.IsEmpty then [ anchorName ] else []
            let exported = String.concat " " (defined @ anchor)

            [ if not anchor.IsEmpty then
                  yield $"(: %s{anchorName} int)"
                  yield $"(def %s{anchorName} 0)"
              yield $"(export %s{exported})" ]
        | _ -> []

    // What the user typed comes first in both shapes, and everything the REPL
    // adds after it, so that a diagnostic's line number is the line they typed
    // on. An expression is one line, and is reported as line 1.
    let body =
        match shape with
        | Expression ->
            [ $"(def %s{valueName} %s{text})"
              $"(def %s{showName} (->str %s{valueName}))" ]
        | _ -> [ text ]

    String.concat "\n" (body @ preamble @ trailer) + "\n"

// ---------------------------------------------------------------------------
// Showing a value
// ---------------------------------------------------------------------------

/// Does an expression entry have a value worth printing?
///
/// `void` is the interop one — a `set!`, or a call to something that ends in a
/// `.Dispose` — and `Unit` is what `(println ...)` and its neighbours return.
/// Both are "this was done, not computed", and echoing a rendering of one is
/// noise.
let private producesAValue (env: TypedAST.Env) =
    match Map.tryFind valueName env.Bindings with
    | None -> false
    | Some binding ->
        let (TypedAST.Scheme(_, _, t)) = binding.Scheme

        match Unification.prune env.Registry t with
        | TypedAST.TCon(TypedAST.TypeConstants.VoidName, [])
        | TypedAST.TCon(TypedAST.TypeConstants.UnitName, []) -> false
        | _ -> true

/// Reads a field of an entry's module class, which is what runs the entry.
///
/// The read *is* the evaluation: a top-level `def` is emitted as a
/// `public static readonly` field assigned in the class's static constructor,
/// which the CLR runs on first touch.
///
/// Into the default load context, not one of its own. Entry N+1's assembly
/// holds a hard reference to entry N's, so the two have to be one identity to
/// the loader; and the resolver `Pipeline` installs — which is what finds
/// `prelude` and the runtime assemblies — is the default context's. The cost is
/// that nothing is ever unloaded, so a session grows by one small assembly per
/// entry.
let private readBinding (dllPath: string) (moduleName: string) (memberName: string) : obj =
    let assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath dllPath
    let className = Naming.moduleClassName moduleName
    let clrType = assembly.GetType className

    if isNull clrType then
        failwithf $"The entry compiled, but '%s{className}' is not in the assembly it produced."

    match clrType.GetField(Naming.sanitizeIdent memberName) with
    | null -> failwithf $"The entry compiled, but '%s{className}' has no '%s{memberName}'."
    | field -> field.GetValue null

/// Runs an entry's initializers without reading anything out of it.
///
/// A definition entry still has effects — `(def x (begin (println "hi") 1))` —
/// and they live in the static constructor like any other initializer.
let private force (dllPath: string) (moduleName: string) : unit =
    let assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath dllPath

    match assembly.GetType(Naming.moduleClassName moduleName) with
    | null -> ()
    | clrType -> Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor clrType.TypeHandle

// ---------------------------------------------------------------------------
// One entry
// ---------------------------------------------------------------------------

/// Warns when a name is being defined over one an earlier entry defined.
///
/// Shadowing, not replacement, and the difference is visible: entry 3's
/// compiled code holds a hard reference to entry 1's `f` and goes on calling
/// it. Nothing can change that short of recompiling every entry that mentions
/// `f`, which is a rebuild of the session on every keystroke. So the REPL says
/// what it did rather than pretending.
let private warnAboutShadowing (state: State) (names: string list) =
    for name in names do
        match state.Entries |> List.tryFind (fun e -> Set.contains name e.Provides) with
        | Some earlier ->
            eprintfn
                $"  note: %s{name} shadows the one from entry %d{earlier.Index}. Anything already compiled against that one still calls it."
        | None -> ()

let private evaluate (state: State) (text: string) : State =
    let forms =
        try
            Lexer.tokenize "<repl>" text |> Pipeline.read |> fst
        with ex ->
            printfn "%s" (Diagnostics.humanize ex.Message)
            []

    if forms.IsEmpty then
        state
    else

    let text = absolutizeImports text forms

    // Before the session is reset, so that a macro an earlier entry imported is
    // still in the table and a form using it reads as what it is.
    match shapeOf forms with
    | Malformed reason ->
        printfn $"%s{reason}"
        state
    | shape ->

    let index = state.Next
    let moduleName = $"Bjo_Repl_%d{index}"
    let sourcePath = Path.Combine(state.Directory, moduleName + ".bjo")
    let dllPath = Path.Combine(state.Directory, moduleName + ".dll")

    let defined, provided, bindings, signedHere, sticky =
        match shape with
        | Definitions(defined, provided, bindings, signed, sticky) ->
            defined, provided, bindings, signed |> List.map fst, sticky
        | _ -> [], [], [], [], false

    // The signatures waiting for a name this entry defines. Replayed into its
    // source and dropped afterwards; the rest go on waiting.
    let consumed, stillPending =
        state.Pending |> List.partition (fun (name, _) -> List.contains name defined)

    let replayed = List.map snd consumed

    // The replayed signatures count as text this entry mentions: `(: area
    // (-> Shape double))` is the only place the entry names the type it works
    // on, and without it nothing would link the entry that declared `Shape`.
    let linked = neededEntries state provided (text :: replayed)
    let alreadySigned = signedHere @ List.map fst consumed

    /// Everything a `(: ...)` still has to be produced for.
    ///
    /// `Inference` refuses to export a binding without one, deliberately: a
    /// module's published surface is what its author committed to, not what
    /// happened to be inferred. At a prompt there is no author, and a `def`
    /// nothing else can see is not worth defining — so the REPL writes the
    /// inferred type down and compiles the entry again against it. The entry
    /// then means what the same lines in a file would.
    let unsigned = bindings |> List.filter (fun n -> not (List.contains n alreadySigned))

    let check (publishing: bool) (extra: string list) =
        File.WriteAllText(sourcePath, entrySource state linked shape (replayed @ extra) publishing text)
        // Everything a compilation owns starts clean; the invented-name counter
        // does not. See `Session.replEntry`.
        Session.replEntry (fun () -> Pipeline.runFullFrontendPipeline sourcePath)

    let compiled =
        if unsigned.IsEmpty then
            check true []
        else
            match check false [] with
            | None -> None
            | Some(env, _, _, _) -> check true (unsigned |> List.choose (Exports.signatureForm env))

    match compiled with
    | None ->
        // The diagnostic has already been printed by the pipeline, naming the
        // entry's file and the line the user typed on.
        state
    | Some(env, typedAst, dllDeps, declaredMacros) ->
        let source = Build.generateSource env typedAst dllDeps declaredMacros sourcePath true

        let references =
            (Paths.runtimeAssemblies @ dllDeps)
            |> List.filter File.Exists
            |> List.map Path.GetFullPath
            |> List.distinct

        let emitOptions: CSharpEmit.Options =
            { AssemblyName = moduleName
              Target = CSharpEmit.Library
              // Optimized, like a build. An entry that is a benchmark should
              // not be several times slower for being typed at a prompt.
              Optimize = true
              // No symbols. The entry's source is a temporary file that will
              // not be there to open, and the pdb would be most of what a
              // keystroke costs.
              EmitPdb = false
              References = references }

        match CSharpEmit.emit emitOptions source with
        | CSharpEmit.Failed diagnostics ->
            printfn "The generated C# did not compile. This is a compiler bug:"
            for d in diagnostics do printfn "  %s" d
            state
        | CSharpEmit.Emitted(bytes, _) ->
            File.WriteAllBytes(dllPath, bytes)

            try
                match shape with
                | Expression ->
                    if producesAValue env then
                        match readBinding dllPath moduleName showName with
                        | :? string as rendered -> printfn "%s" rendered
                        | other -> printfn "%A" other
                    else
                        // Read for the effect, not the value: `(println "hi")`
                        // happens in the static constructor.
                        readBinding dllPath moduleName valueName |> ignore
                | Definitions _ -> force dllPath moduleName
                | Malformed _ -> ()
            with
            | :? TypeInitializationException as ex when not (isNull ex.InnerException) ->
                // The entry's own exception. The initializer frame the CLR
                // wraps it in describes how a `def` is emitted, which is
                // nothing the person at the prompt asked about.
                printfn $"%s{ex.InnerException.GetType().Name}: %s{ex.InnerException.Message}"
            | ex -> printfn $"%s{ex.GetType().Name}: %s{Diagnostics.humanize ex.Message}"

            warnAboutShadowing state defined

            if not defined.IsEmpty then
                printfn "%s" (String.concat " " defined)

            { state with
                Next = index + 1
                Imports =
                    state.Imports
                    @ (forms
                       |> List.filter (function
                           | SList(SAtom { Token = Symbol "import" } :: _, _) -> true
                           | _ -> false)
                       |> List.map (textOf text))
                Pending =
                    stillPending
                    @ (match shape with
                       | Definitions(_, _, _, signed, _) ->
                           signed
                           |> List.filter (fun (name, _) -> not (List.contains name defined))
                           |> List.map (fun (name, form) -> name, textOf text form)
                       | _ -> [])
                Entries =
                    { Index = index
                      Provides = Set.ofList provided
                      Sticky = sticky
                      DllPath = dllPath }
                    :: state.Entries }

// ---------------------------------------------------------------------------
// The loop
// ---------------------------------------------------------------------------

let private help () =
    printfn "  :help    this"
    printfn "  :quit    leave (so does Ctrl-D)"
    printfn ""
    printfn "  Anything else is a Bjolang entry: a group of definitions, or one"
    printfn "  expression, whose value is printed with ->str."
    printfn ""
    printfn "  Redefining a name shadows it. Code compiled against the earlier"
    printfn "  one goes on calling the earlier one."

let run () : int =
    // Nothing narrates a REPL entry. Six step banners per keystroke is not what
    // a prompt is for.
    Diagnostics.verbose <- false

    // This process will emit once per entry, which is the case in-process
    // Roslyn is for: the first emit costs a few hundred milliseconds and every
    // one after it costs about fifteen. Started here so the warm-up overlaps
    // the standard library's first load.
    CSharpEmit.preferInProcess ()

    // Every entry imports the prelude, and reading the prelude's metadata back
    // into declarations was about a third of what an entry cost. A REPL entry's
    // assembly is thrown away when the next one is typed, so it has nothing to
    // lose by the caveat that keeps this off for builds — see
    // `Pipeline.cacheLoadedModules`.
    Pipeline.cacheLoadedModules <- true

    for assemblyPath in Paths.runtimeAssemblies do
        if File.Exists assemblyPath then
            DotNetInterop.registerAssemblyFile assemblyPath

    // `BJOLANG_REPL_DIR` keeps the entries where they can be read. What an
    // entry becomes is the whole of what this module decides, so being able to
    // look at one is the difference between debugging the REPL and guessing at
    // it.
    let directory =
        match Environment.GetEnvironmentVariable "BJOLANG_REPL_DIR" with
        | null | "" -> Path.Combine(Path.GetTempPath(), "Bjolang_repl_" + Guid.NewGuid().ToString("N"))
        | dir -> Path.GetFullPath dir

    let keep = Environment.GetEnvironmentVariable "BJOLANG_REPL_DIR" |> String.IsNullOrEmpty |> not
    Directory.CreateDirectory directory |> ignore

    printfn "Bjolang REPL. :help for commands, Ctrl-D to leave."

    let rec loop (state: State) =
        match readEntry "bjo> " "...> " with
        | None ->
            printfn ""
            state
        | Some text ->
            match text.Trim() with
            | "" -> loop state
            | ":quit" | ":q" -> state
            | ":help" | ":h" ->
                help ()
                loop state
            | entry ->
                let next = Timing.phase "repl entry" (fun () -> evaluate state entry)
                loop next

    loop
        { Next = 1
          Imports = []
          Pending = []
          Entries = []
          Directory = directory }
    |> ignore

    // The session directory goes; the assemblies in it are already loaded, and
    // a loaded assembly does not need its file back.
    if not keep then
        try Directory.Delete(directory, true) with _ -> ()

    0