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
open Bjolang.Ast

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
/// Only what an importing entry can name. `Ast.boundNames` is the neighbour
/// of this and answers a different question — it includes a `defun`'s
/// parameters, because it exists to stop a macro's template capturing one.
let private definedNames (decl: Ast.Decl) : string list =
    match decl with
    | Ast.DDef(n, _, _)
    | Ast.DDefMutable(n, _, _)
    | Ast.DDefun(n, _, _, _, _) -> [ n ]
    | Ast.DDefTuple(names, _, _) -> names
    | Ast.DDefPattern(pattern, _, _) -> Ast.patternBinders pattern
    | Ast.DType(defs, _)
    | Ast.DTypeRec(defs, _) -> defs |> List.map (fun d -> d.Name)
    | Ast.DTrait(n, _, _, _, _, _, _, _) -> [ n ]
    | _ -> []

/// Of those, the ones an `(export ...)` demands a signature for.
///
/// A type publishes its declaration and a trait its methods, so neither has a
/// signature to be missing. A binding does.
let private bindingNames (decl: Ast.Decl) : string list =
    match decl with
    | Ast.DType _
    | Ast.DTypeRec _
    | Ast.DTrait _ -> []
    | other -> definedNames other

/// Every name a later entry might write that this one answers for.
///
/// Wider than `definedNames`, and it has to be: a union's cases and a trait's
/// methods are what source actually writes — `(Circle 2.0)`, `(->str x)` — and
/// nothing mentions the type or the trait by name at all. They cannot go in the
/// `(export ...)` list, which is why this is a second function rather than one:
/// `(export Circle)` is refused, since a case has no signature and travels with
/// the type that declares it.
let private providedNames (decl: Ast.Decl) : string list =
    match decl with
    | Ast.DType(defs, _)
    | Ast.DTypeRec(defs, _) ->
        defs
        |> List.collect (fun td ->
            td.Name
            :: (match td.Kind with
                | Ast.Union cases ->
                    cases
                    |> List.map (function
                        | Ast.SimpleCase(n, _) -> n
                        | Ast.DataCase(n, _, _, _) -> n)
                // A record is constructed by its own name, and an opaque type
                // and an alias offer no constructor at all.
                | _ -> []))
    | Ast.DTrait(name, _, _, _, signatures, _, _, _) -> name :: List.map (fun (n, _, _) -> n) signatures
    | other -> definedNames other

/// Does this entry have to be linked by every entry after it?
///
/// An impl is not a name. Nothing a later entry writes mentions it, so nothing
/// would pull it in — and a trait method call would then dispatch as though the
/// impl had never been written. So an entry that declares one is linked
/// unconditionally, which is what makes `(impl ...)` at the prompt affect
/// the entries after it.
///
/// The entries *before* it are a different matter and cannot be helped:
/// `Lowering` bakes a dictionary choice into IL, so an impl written at entry 9
/// is not in the code entry 4 already emitted.
let private isSticky (decl: Ast.Decl) : bool =
    match decl with
    | Ast.DImpl _
    | Ast.DImplExtern _
    | Ast.DInlineImpl _
    | Ast.DTrait _ -> true
    | _ -> false

/// What the user typed, as the compiler reads it.
///
/// `DeclParser.tryParseDeclGroup` is asked rather than the head symbol matched
/// here, so that what counts as a declaration is decided in one place. The
/// *group* form, because a `defun` carrying its own parameter and return types
/// declares a signature as well as a function — and an entry that already has
/// one must not be given a second.
///
/// Run *before* the entry's own session is reset, which is what makes a macro
/// imported by an earlier entry visible: the table still holds what the
/// previous entry's import registered.
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
    let asDecls (form: SExpr) =
        try
            // A doc declares nothing: the pipeline takes it out and checks it
            // against what the same entry defines.
            if Docs.isDocForm form then
                Some []
            else
                DeclParser.tryParseDeclGroup form
                |> Option.map (List.map (fun d -> d, form))
        with _ ->
            // A declaration whose *body* is malformed still reads as one. The
            // real diagnostic comes from compiling it, where it has a position.
            Some [ Ast.DExport([], getRange form), form ]

    match forms with
    | [] -> Malformed "nothing to evaluate"
    // Each entry is a module of its own, so a doc of an earlier entry's
    // definition would be a doc of another module's.
    | _ when forms |> List.forall Docs.isDocForm ->
        Malformed
            "(:doc ...) documents what its own entry defines. At the prompt, write it in the same entry as the definition, on the same line or inside (begin ...)."
    | _ ->
        let parsed = forms |> List.map asDecls

        if parsed |> List.forall Option.isSome then
            let decls = parsed |> List.collect Option.get

            // Every signature, including one a `defun` wrote for itself. Which
            // of them is still *waiting* for a definition is decided where they
            // are recorded: one whose name this entry also defines is already
            // beside its function and has nothing to wait for.
            let signed =
                decls
                |> List.choose (function
                    | Ast.DSignature(name, _, _, _), form -> Some(name, form)
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
///
/// A modifier's path is its first argument. Its other strings are not paths:
/// in `(prefix (std random) "r/")` the prefix stays as written.
let private absolutizeImports (text: string) (forms: SExpr list) : string =
    let rec paths (s: SExpr) =
        match s with
        | SAtom { Token = StringLit p } -> [ p ]
        | SList(_ :: imported :: _, _) -> paths imported
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
/// nothing but `(impl ...)` has neither, so its impl never crossed and a
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
    // Namnrymden ur dll:ens katalog, klassen ur modulnamnet.
    let className = $"%s{Naming.moduleNamespace dllPath}.%s{Naming.moduleClassName moduleName}"
    let clrType = assembly.GetType className

    if isNull clrType then
        failwithf $"The entry compiled, but '%s{className}' is not in the assembly it produced."

    match clrType.GetField(Prelude.moduleClrMemberName memberName) with
    | null -> failwithf $"The entry compiled, but '%s{className}' has no '%s{memberName}'."
    | field -> field.GetValue null

/// Runs an entry's initializers without reading anything out of it.
///
/// A definition entry still has effects — `(def x (begin (println "hi") 1))` —
/// and they live in the static constructor like any other initializer.
let private force (dllPath: string) (moduleName: string) : unit =
    let assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath dllPath

    match assembly.GetType($"%s{Naming.moduleNamespace dllPath}.%s{Naming.moduleClassName moduleName}") with
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
    // The collector is process-global and a prompt reuses the process, so each
    // entry starts with an empty one. Without it the phase gates would see the
    // entry before last's errors and refuse to check this one.
    Diagnostics.reset ()

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
            | Some(env, _, _, _, _, _) -> check true (unsigned |> List.choose (Exports.signatureForm env))

    match compiled with
    | None ->
        // The diagnostic has already been printed by the pipeline, naming the
        // entry's file and the line the user typed on.
        state
    | Some(env, typedAst, dllDeps, declaredMacros, declaredPatternMacros, declaredHashMacros) ->
        let source =
            Build.generateSource env typedAst dllDeps declaredMacros declaredPatternMacros declaredHashMacros sourcePath true

        let references =
            (Paths.runtimeAssemblies @ dllDeps @ NuGetRefs.compileReferences ())
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
            | ex ->
               // Unwrap the exceptions so we can see what is wrong in the repl. 
               // TODO: when I have fixed the compiler crashing and burning with exceptions
               // this is not needed
                let rec unwrap (e: exn) =
                    match e with
                    | :? Reflection.TargetInvocationException when not (isNull e.InnerException) ->
                        unwrap e.InnerException
                    | :? TypeInitializationException when not (isNull e.InnerException) ->
                        unwrap e.InnerException
                    | _ -> e

                match unwrap ex with
                // Special treatments for panicexceptions. They should not kill the repl.
                | :? BjolangRuntime.PanicException as panic -> printfn $"panic: %s{panic.Message}"
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
// :show
// ---------------------------------------------------------------------------

/// A name visible at the prompt, and where its documentation would be.
type private Visible =
    { Name: string
      /// The name in the module that defines it. A rename or a prefix on the
      /// import makes it differ from `Name`, and a doc is published under this.
      Original: string
      /// `(std random)`, `entry 3`, or `builtin`.
      Module: string
      /// The assembly whose published docs answer for the name. Builtins are
      /// compiled into the runtime and have none.
      Dll: string option }

/// Every name the next entry could write, and where each is defined.
///
/// The REPL does not work out the scope itself. It compiles an entry holding
/// nothing but the session's imports, and reads the scope off what the
/// compiler built, so `only`, `except`, renames, prefixes and re-exports mean
/// here exactly what they mean in an entry. A re-exported name is found in the
/// module that defines it, which is where its doc is published.
///
/// Earlier entries come first and builtins last, so a name defined at the
/// prompt shadows an import of the same name, and an import shadows a builtin.
let private visibleNames (state: State) : Visible list =
    Diagnostics.reset ()

    // The next entry is read against the macro table as the last entry left
    // it. The probe links no entries, so it would leave out a macro one of
    // them defined.
    let macros = Macro.snapshot ()
    let sourcePath = Path.Combine(state.Directory, "Bjo_Repl_show.bjo")
    File.WriteAllText(sourcePath, String.concat "\n" (state.Imports @ [ $"(def %s{valueName} 0)" ]) + "\n")

    let compiled =
        try
            Session.replEntry (fun () ->
                Pipeline.runFullFrontendPipeline sourcePath
                |> Option.map (fun (env, _, dllDeps, _, _, _) -> env, dllDeps, Macro.snapshot ()))
        finally
            Macro.restore macros

    match compiled with
    | None -> []
    | Some(env, dllDeps, probeMacros) ->
        let dllOf =
            dllDeps |> List.map (fun dll -> Naming.moduleKeyOfPath dll, dll) |> Map.ofList

        let imported (origin: string) (original: string) (name: string) =
            Map.tryFind origin dllOf
            |> Option.map (fun dll ->
                { Name = name
                  Original = original
                  Module = Pipeline.dependencyEntry dll
                  Dll = Some dll })

        // A union's cases are documented in the union's doc, not under their
        // own names.
        let bindings =
            env.Registry.ImportAliases
            |> Map.toList
            |> List.choose (fun (name, alias) ->
                match alias.Kind with
                | AliasConstructor -> None
                | _ -> imported alias.OriginModule alias.OriginalName name)

        let macros =
            (probeMacros.Bindings |> List.choose (fun (name, b) -> imported b.ModuleName b.Name name))
            @ (probeMacros.HashBindings
               |> List.choose (fun (name, b) -> imported b.ModuleName ("#" + b.Name) ("#" + name)))

        let entries =
            state.Entries
            |> List.collect (fun e ->
                e.Provides
                |> Set.toList
                |> List.map (fun name ->
                    { Name = name
                      Original = name
                      Module = $"entry %d{e.Index}"
                      Dll = Some e.DllPath }))

        let builtins =
            Prelude.builtinNames
            |> Set.toList
            |> List.map (fun name ->
                { Name = name
                  Original = name
                  Module = "builtin"
                  Dll = None })

        entries @ bindings @ macros @ builtins
        |> List.filter (fun v -> not (v.Name.StartsWith "__") && not (v.Name.Contains "::"))
        |> List.distinctBy (fun v -> v.Name)

/// Published docs by assembly path. A library does not change under a running
/// session, and an entry's assembly is written once.
let private docCache = Collections.Generic.Dictionary<string, Map<string, SExpr>>()

/// A module's published docs, by the name each documents: `#name` for a
/// reader extension. The module's own doc is left out, since no name leads to
/// it. An assembly that cannot be read has none.
let private docsOf (dll: string) : Map<string, SExpr> =
    match docCache.TryGetValue dll with
    | true, docs -> docs
    | _ ->
        let rec docForms (s: SExpr) =
            if Docs.isDocForm s then
                [ s ]
            else
                match s with
                | SList(items, _) -> List.collect docForms items
                | SAtom _ -> []

        let docs =
            try
                match Bjolang.Runtime.BjoAssemblyMetadata.Read(dll, "BjolangDocs") with
                | text when String.IsNullOrWhiteSpace text -> Map.empty
                | text ->
                    Lexer.tokenize dll text
                    |> Pipeline.read
                    |> fst
                    |> List.collect docForms
                    |> List.choose (fun form ->
                        match form with
                        | SList(_ :: SAtom { Token = Symbol n } :: SAtom { Token = Keyword "reader" } :: _, _) ->
                            Some("#" + n, form)
                        | SList(_ :: SAtom { Token = Symbol n } :: _, _) -> Some(n, form)
                        | _ -> None)
                    |> Map.ofList
            with _ ->
                Map.empty

        docCache[dll] <- docs
        docs

/// A form as source writes it, for a doc's `form`, `see` and `literal`
/// clauses and for anything else that is not a string.
let rec private sourceText (s: SExpr) : string =
    match s with
    | SList(items, _) -> "(" + String.concat " " (List.map sourceText items) + ")"
    | SAtom t ->
        match t.Token with
        | Symbol n
        | ResolvedSymbol n
        | NumberLit n
        | TypeVar n -> n
        | Keyword k -> "#:" + k
        | QuotedSymbol a -> "%" + a
        | StringLit text -> "\"" + text + "\""
        | BoolLit b -> if b then "#t" else "#f"
        | Spread -> "..."
        | Colon -> ":"
        | Dot -> "."
        | _ -> "?"

/// The clauses of a published doc, by head, in the order written.
let private clausesOf (form: SExpr) : (string * SExpr list) list =
    match form with
    | SList(_ :: rest, _) ->
        rest
        |> List.choose (function
            | SList(SAtom { Token = Symbol head } :: parts, _) -> Some(head, parts)
            | _ -> None)
    | _ -> []

/// Samizdat markup with plain text inside, `@code{hi}`, as the plain text. A
/// terminal has nothing to render it with, and the braces are noise.
let private plainMarkup =
    Text.RegularExpressions.Regex(@"@[A-Za-z][A-Za-z0-9-]*\{([^{}]*)\}", Text.RegularExpressions.RegexOptions.Compiled)

let private clauseString (s: SExpr) =
    match s with
    | SAtom { Token = StringLit text } -> plainMarkup.Replace(text, "$1")
    | other -> sourceText other

let private summaryOf (form: SExpr) : string =
    clausesOf form
    |> List.tryPick (function
        | "summary", [ text ] -> Some(clauseString text)
        | _ -> None)
    |> Option.defaultValue ""

/// Indents every line of `text` but the first by `width` spaces.
let private hanging (width: int) (text: string) =
    text.Replace("\n", "\n" + String(' ', width))

/// Prints one doc: what the name is and where it is from, how it is called,
/// then its clauses in the order the author wrote them.
let private showDoc (v: Visible) (form: SExpr) =
    let clauses = clausesOf form

    let single head =
        clauses
        |> List.tryPick (fun (h, parts) ->
            match parts with
            | [ p ] when h = head -> Some(clauseString p)
            | _ -> None)

    // `r/random-bool: function random-bool from (std random)` for a name an
    // import renamed.
    let what =
        [ yield! Option.toList (single "kind")
          if v.Original <> v.Name then yield v.Original
          yield $"from %s{v.Module}" ]

    printfn $"""%s{v.Name}: %s{String.concat " " what}"""

    // How it is called: a defun's head as written, else a macro's forms.
    match single "definition" with
    | Some head -> printfn $"  %s{head}"
    | None ->
        for (h, parts) in clauses do
            if h = "form" then
                for p in parts do printfn $"  %s{sourceText p}"

    single "signature" |> Option.iter (fun t -> printfn $"  : %s{t}")

    single "summary" |> Option.iter (fun s -> printfn $"\n  %s{s}")

    // The parameters, fields, cases, result and exceptions, as rows under
    // one column.
    let rows =
        clauses
        |> List.choose (fun (h, parts) ->
            match h, parts with
            | ("arg" | "field" | "case"), [ n; text ] -> Some(sourceText n, clauseString text)
            | "rest", [ n; text ] -> Some("#:rest " + sourceText n, clauseString text)
            | "key", [ n; text ] -> Some("#:" + (sourceText n).TrimStart('#', ':'), clauseString text)
            | "tparam", [ n; text ] -> Some(sourceText n, clauseString text)
            | "returns", [ text ] -> Some("returns", clauseString text)
            | "raises", [ n; text ] -> Some("raises", sourceText n + ": " + clauseString text)
            | _ -> None)

    if not rows.IsEmpty then
        printfn ""
        let width = rows |> List.map (fst >> String.length) |> List.max |> min 24

        for (label, text) in rows do
            printfn $"  %s{label.PadRight width}  %s{hanging (width + 4) text}"

    for (h, parts) in clauses do
        match h, parts with
        | "example", [ text ] -> printfn $"\n  Example:\n    %s{hanging 4 (clauseString text)}"
        | "reference", [ text ] -> printfn $"\n  %s{hanging 2 (clauseString text)}"
        | "literal", names -> printfn $"""%s{"\n"}  Literals: %s{names |> List.map sourceText |> String.concat " "}"""
        | _ -> ()

    match clauses |> List.collect (fun (h, parts) -> if h = "see" then parts else []) with
    | [] -> ()
    | names -> printfn $"""%s{"\n"}  See also: %s{names |> List.map sourceText |> String.concat ", "}"""

/// `:show text`: the doc of the visible name `text`, and a line for each other
/// visible name containing it, in any case. Prints nothing when no visible
/// name does: a name from a module the session has not imported is not one the
/// next entry could write.
let private show (state: State) (query: string) =
    let needle = query.ToLowerInvariant()

    let matches =
        visibleNames state
        |> List.filter (fun v -> v.Name.ToLowerInvariant().Contains needle)
        |> List.sortBy (fun v -> v.Name)

    let docOf (v: Visible) =
        v.Dll |> Option.bind (fun dll -> Map.tryFind v.Original (docsOf dll))

    let exact, others = matches |> List.partition (fun v -> v.Name = query)

    for v in exact do
        match docOf v with
        | Some form -> showDoc v form
        | None -> printfn $"%s{v.Name}: from %s{v.Module}, with no doc"

    if not others.IsEmpty then
        if not exact.IsEmpty then printfn ""
        let width = others |> List.map (fun v -> v.Name.Length) |> List.max |> min 32
        let moduleWidth = others |> List.map (fun v -> v.Module.Length) |> List.max

        for v in others do
            let summary = docOf v |> Option.map summaryOf |> Option.defaultValue ""
            printfn "%s" ($"  %s{v.Name.PadRight width}  %s{v.Module.PadRight moduleWidth}  %s{summary}".TrimEnd())

// ---------------------------------------------------------------------------
// The loop
// ---------------------------------------------------------------------------

let private help () =
    printfn "  :help       this"
    printfn "  :quit       leave (so does Ctrl-D)"
    printfn "  :show name  the doc of the visible name, and the other visible"
    printfn "              names containing it. A name is visible when an entry"
    printfn "              typed now could use it: a builtin, one an import"
    printfn "              brings in, or one an earlier entry defined."
    printfn ""
    printfn "  Anything else is a Bjolang entry: a group of definitions, or one"
    printfn "  expression, whose value is printed with ->str."
    printfn ""
    printfn "  A top-level defun needs a signature. Write it on the definition,"
    printfn "  which is one entry:"
    printfn ""
    printfn "    (defun (double (: x int)) : int (* x 2))"
    printfn ""
    printfn "  Or on the line before, which the next entry picks up:"
    printfn ""
    printfn "    (: double (-> int int))"
    printfn "    (defun (double x) (* x 2))"
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

    for assemblyPath in NuGetRefs.runtimeAssemblies () do
        DotNetInterop.registerAssemblyFile assemblyPath

    NuGetRefs.installNativeResolver ()

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

    // Every entry runs with `currently-in-repl` bound, which is what makes
    // `panic!` raise instead of calling `Environment.Exit`.
    //
    // Pushed once and never restored. 
    BjolangRuntime.parametersubpush_BANG(BjolangRuntime.currentlysubinsubrepl, true) |> ignore

    // The session scope. Every entry runs inside it, so a port opened at the
    // prompt lives until the session ends rather than until the next line, and
    // a `spawn` has somewhere to enlist. It does not propagate failures — see
    // the runtime docstring for why a prompt must not be cancellable by one bad
    // spawn.
    let session = BjolangRuntime.OpenReplSession()

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
            | ":show" ->
                printfn "  :show name  the doc of a visible name"
                loop state
            | entry when entry.StartsWith ":show " ->
                show state (entry.Substring(":show ".Length).Trim())
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

    // Before the directory, so that anything the session still owns is released
    // while the session's own files are still where it left them.
    BjolangRuntime.CloseReplSession session |> ignore

    // The session directory goes; the assemblies in it are already loaded, and
    // a loaded assembly does not need its file back.
    if not keep then
        try Directory.Delete(directory, true) with _ -> ()

    0
