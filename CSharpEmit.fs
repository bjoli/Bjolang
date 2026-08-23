/// Turning generated C# into an assembly, in this process.
///
/// The alternative is `csc`, and with `-shared` that is not slow: the
/// VBCSCompiler server keeps Roslyn warm and its reference set parsed, and one
/// small build costs about 80ms through it. What a process cannot do is hand
/// back an assembly without writing a file, and it cannot share anything with
/// the compilation that asked for it. Both matter exactly where a process
/// boundary is the thing being removed — a REPL entry, and a multi-module build
/// that no longer spawns a compiler per module.
///
/// The reference set is where the sharing is. A `MetadataReference` is a parsed
/// view of an assembly's metadata; there are about 167 of them in the .NET
/// reference pack, and building them takes long enough that doing it per module
/// is most of what an in-process build would otherwise save. They are held for
/// the life of the process.
module Bjolang.CSharpEmit

open System
open System.Collections.Concurrent
open System.IO
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.Emit

// ---------------------------------------------------------------------------
// The reference set
// ---------------------------------------------------------------------------

/// The .NET installation this compiler is running on.
let private dotnetRoot =
    let coreLib = typeof<obj>.Assembly.Location
    Path.GetFullPath(Path.Combine(Path.GetDirectoryName coreLib, "..", "..", ".."))

/// The reference assemblies, not the implementation ones.
///
/// Same choice `Program.tryFastCompile` makes, and for the same reason: an
/// assembly compiled against the shared framework records a dependency on
/// `System.Private.CoreLib`, which nothing consuming it through the ordinary
/// reference assemblies can resolve. The two build paths have to agree on which
/// assemblies they mean, or a `.dll` built by one cannot be linked by the
/// other.
let referenceAssemblyDir: string =
    let runtimeDir = Path.GetDirectoryName(typeof<obj>.Assembly.Location)

    let refPack =
        Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref", Path.GetFileName runtimeDir, "ref")

    if Directory.Exists refPack then
        match Directory.GetDirectories refPack |> Array.tryHead with
        | Some tfmDir -> tfmDir
        | None -> runtimeDir
    else
        runtimeDir

/// Is this one of the .NET installation's own assemblies?
///
/// Decides how it is read. Nothing here ever rewrites a file under the
/// installation, so those are memory-mapped and paged in as needed; a Bjolang
/// module's `.dll` may well be rebuilt later in the same process, and a mapping
/// held over a file that is then truncated is not a stale reference but a
/// corrupt one.
let private isInstalled (path: string) =
    path.StartsWith(dotnetRoot, StringComparison.Ordinal)

/// Parsed metadata, by path and the timestamp it was read at.
///
/// The timestamp is in the key rather than checked against it, so that
/// rebuilding a dependency and referencing it again in the same process gets
/// the new one. The old entry stays — an assembly already handed to a
/// `CSharpCompilation` cannot be taken back — which is a bounded leak: one
/// entry per rebuild of a module, in a process that builds each module once.
let private references = ConcurrentDictionary<string * int64, MetadataReference>()

let private referenceTo (path: string) : MetadataReference option =
    if not (File.Exists path) then
        None
    else
        let key = path, File.GetLastWriteTimeUtc(path).Ticks

        Some(
            references.GetOrAdd(
                key,
                fun _ ->
                    if isInstalled path then
                        MetadataReference.CreateFromFile path :> MetadataReference
                    else
                        MetadataReference.CreateFromImage(File.ReadAllBytes path) :> MetadataReference
            )
        )

/// Every reference assembly in the .NET pack, parsed once for the process.
let private frameworkReferences =
    lazy
        (Timing.phase "roslyn: framework references" (fun () ->
            Directory.GetFiles(referenceAssemblyDir, "*.dll")
            |> Array.choose referenceTo
            |> List.ofArray))

/// Whether every emit in this process goes through here.
///
/// One flag for the whole process, and decided before the first emit rather
/// than per emit. That is not tidiness. This Roslyn and the SDK's `csc` are two
/// different builds of the same compiler, so they do not produce the same
/// bytes — and a rule like "in-process once this process is warm" makes the
/// choice depend on whether a background thread finished first. Measured: a
/// four-module build came out with two modules from each, differently every
/// run. "The same source gives the same assembly" then fails for a reason
/// nothing in the source explains.
let mutable private preferred = false

/// Whether anyone asked for a pre-warm.
let mutable private prewarmStarted = false

/// Emits this process has completed, the pre-warm included.
let private emitCount = ref 0

/// A stand-in for the C# `Codegen` produces.
///
/// The shape is what matters, not the body. What the first emit in a process
/// pays for is JITting Roslyn *and* reading the metadata of every assembly the
/// binder has to look inside — and metadata is read lazily, per type, so a
/// warm-up that binds nothing warms nothing. Measured: a throwaway
/// `static class W { static int F() => 1; }` left the following real emit at
/// 400ms, which is what it cost with no warm-up at all.
///
/// So this opens the runtime assemblies the way generated code does — the same
/// `using static`, a list, a string, a console call — and the real emit after
/// it reuses the `AssemblyMetadata` behind every one of those references.
let private prewarmSource =
    """using System;
using static BjolangRuntime;
public static class BjolangPrewarm_Module {
    public static readonly SchemeList.SchemeList<string> Names =
        SchemeList.SchemeList.Cons("a", SchemeList.SchemeList.Empty<string>());
    public static string Describe(int n) {
        var text = n.ToString();
        Console.Out.Write(text);
        return string.Concat(text, "!");
    }
}"""

/// Compiles `prewarmSource` off the main thread.
///
/// Roslyn arrives as plain IL — the SDK's `csc` ships ReadyToRun, the NuGet
/// package does not — so the first `Emit` in a process is several hundred
/// milliseconds slower than every one after it. That is time the frontend is
/// going to spend parsing and type checking anyway, so it is spent in parallel
/// with it and joined by the first real emit.
let private prewarmed =
    lazy
        (System.Threading.Tasks.Task.Run(fun () ->
            let sw = Diagnostics.Stopwatch.StartNew()

            try
                let text = Text.SourceText.From(prewarmSource, Text.Encoding.UTF8)
                let tree = CSharpSyntaxTree.ParseText(text, CSharpParseOptions(LanguageVersion.Latest))

                let refs =
                    frameworkReferences.Value
                    @ (Paths.runtimeAssemblies |> List.choose referenceTo)

                let compilation =
                    CSharpCompilation.Create(
                        "BjolangPrewarm",
                        [ tree ],
                        refs,
                        CSharpCompilationOptions(
                            OutputKind.DynamicallyLinkedLibrary,
                            optimizationLevel = OptimizationLevel.Release,
                            nullableContextOptions = NullableContextOptions.Enable,
                            deterministic = true
                        )
                    )

                use stream = new MemoryStream()
                use pdb = new MemoryStream()

                compilation.Emit(
                    stream,
                    pdb,
                    options = EmitOptions(debugInformationFormat = DebugInformationFormat.PortablePdb)
                )
                |> ignore
                System.Threading.Interlocked.Increment emitCount |> ignore
            with _ ->
                // A warm-up that fails has cost the build nothing. The real
                // emit reports whatever is actually wrong.
                ()

            Timing.note "roslyn: pre-warm (background)" sw.Elapsed.TotalMilliseconds))

/// Commits this process to emitting here, and starts the pre-warm.
///
/// For a caller that knows it will emit many times and cares more about the
/// marginal cost than the first one. Measured on a single-module program: the
/// first emit costs ~190ms behind a pre-warm and ~400ms without one, while
/// `csc -shared` costs ~82ms however often it is called — but the *second*
/// emit in this process costs ~14ms, which nothing across a process boundary
/// can approach.
///
/// So: a REPL, where every entry after the first is what the whole thing is
/// judged on. A batch build is the other case and stays with `csc`, whose
/// server is a warm Roslyn one process away and needs no warm-up of ours.
let preferInProcess () : unit =
    preferred <- true
    prewarmStarted <- true
    prewarmed.Force() |> ignore

let inProcessPreferred () : bool = preferred

/// Emits completed, the pre-warm included. Reported, not dispatched on.
let emitsDone () : int = emitCount.Value

// ---------------------------------------------------------------------------
// Emitting
// ---------------------------------------------------------------------------

type Target =
    | Library
    | Executable

/// How the three callers differ.
///
/// `Optimize` is not a preference. Without it Roslyn marks the assembly
/// debuggable, which tells the JIT to leave it alone, and the same generated
/// C# then runs several times slower — so it is on everywhere except a `-d`
/// build, where stepping matters more.
///
/// `EmitPdb` is what carries the `#line` mapping from generated C# back to the
/// Bjolang it came from into a stack trace. A build writes one beside the
/// output. A REPL entry does not: the entry is gone when the next one is typed,
/// there is no file for a debugger to open, and the symbols would double what a
/// keystroke costs.
type Options =
    { AssemblyName: string
      Target: Target
      Optimize: bool
      EmitPdb: bool
      /// The Bjolang and runtime assemblies this compilation links. The
      /// framework's are added here.
      References: string list }

/// What came out, or what stopped it.
type Result =
    | Emitted of assembly: byte array * pdb: byte array option
    | Failed of diagnostics: string list

/// The directory the generated C# is reported as living in.
///
/// Invented, and the same whichever backend ran. What `Codegen` writes goes to
/// a temp directory named after a fresh GUID, and `-debug:portable` records the
/// path of the source an assembly was built from — so the real path would make
/// every build of unchanged source produce a different `.dll`. A `#line` maps
/// each statement back to the `.bjo` anyway, which is the path a reader
/// actually wants.
let generatedSourceRoot = "/bjolang/"

let private compilationOf (options: Options) (source: string) =
    let kind =
        match options.Target with
        | Library -> OutputKind.DynamicallyLinkedLibrary
        | Executable -> OutputKind.ConsoleApplication

    let level =
        if options.Optimize then
            OptimizationLevel.Release
        else
            OptimizationLevel.Debug

    // The encoding is not decoration. A pdb records the checksum of the source
    // it maps to, and Roslyn refuses to emit one for text whose encoding it was
    // not told — `error CS8055`, which names neither the pdb nor the caller.
    let text = Text.SourceText.From(source, Text.Encoding.UTF8)

    let tree =
        CSharpSyntaxTree.ParseText(
            text,
            CSharpParseOptions(LanguageVersion.Latest),
            path = generatedSourceRoot + "Program.cs"
        )

    let refs = frameworkReferences.Value @ (options.References |> List.choose referenceTo)

    CSharpCompilation.Create(
        options.AssemblyName,
        [ tree ],
        refs,
        // Deterministic, so that unchanged source and an unchanged reference
        // set give a byte-identical assembly. That is what a timestamp-based
        // staleness cache is worth anything on top of, and it is the property
        // a process per module used to provide by accident.
        CSharpCompilationOptions(
            kind,
            optimizationLevel = level,
            nullableContextOptions = NullableContextOptions.Enable,
            deterministic = true
        )
    )

/// The C# a reader would have been shown by `csc`.
///
/// Warnings are dropped. `csc` prints them and the compiler has never passed
/// them on, and a generated file is not somewhere a warning can be acted on.
let private errorsOf (diagnostics: Diagnostic seq) =
    diagnostics
    |> Seq.filter (fun d -> d.Severity = DiagnosticSeverity.Error)
    |> Seq.map string
    |> List.ofSeq

let emit (options: Options) (source: string) : Result =
    // Waited for, not raced. Both would otherwise JIT the same methods at once
    // and contend over it, which is slower than either alone: a first emit
    // running alongside its own pre-warm took 400ms, and the same emit behind a
    // finished one takes 190.
    if prewarmStarted then
        Timing.phase "roslyn: wait for pre-warm" (fun () -> prewarmed.Value.Wait())

    Timing.phase "roslyn: emit" (fun () ->
        let compilation = compilationOf options source

        use assemblyStream = new MemoryStream()
        use pdbStream = new MemoryStream()

        let emitOptions =
            if options.EmitPdb then
                EmitOptions(debugInformationFormat = DebugInformationFormat.PortablePdb)
            else
                EmitOptions()

        let result =
            if options.EmitPdb then
                compilation.Emit(assemblyStream, pdbStream, options = emitOptions)
            else
                compilation.Emit(assemblyStream, options = emitOptions)

        System.Threading.Interlocked.Increment emitCount |> ignore

        if result.Success then
            Emitted(
                assemblyStream.ToArray(),
                if options.EmitPdb then Some(pdbStream.ToArray()) else None
            )
        else
            Failed(errorsOf result.Diagnostics))

/// Emits to `outputPath`, writing the symbols beside it. Answers the
/// diagnostics that stopped it.
let emitToFile (options: Options) (source: string) (outputPath: string) : string list =
    match emit options source with
    | Failed diagnostics -> diagnostics
    | Emitted(assembly, pdb) ->
        File.WriteAllBytes(outputPath, assembly)

        match pdb with
        | Some bytes -> File.WriteAllBytes(Path.ChangeExtension(outputPath, ".pdb"), bytes)
        | None -> ()

        []
