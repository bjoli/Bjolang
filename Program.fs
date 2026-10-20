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

/// The command line, and nothing else.
///
/// What a compilation *is* lives in `Build`, so that building an imported
/// module is a call rather than a process.
module Bjolang.Program

open Bjolang
open System.IO

type CompilerOptions =
    { /// The files named on the command line, in the order they appeared.
      ///
      /// A list rather than an `option`, because a batch takes many. Outside
      /// of `--batch`, more than one is still an error.
      InputFiles: string list
      IsLibrary: bool
      Debug: bool
      Repl: bool
      /// Compile all input files in this process instead of one each.
      Batch: bool
      /// Skip the input files that already have a current `.dll`. Implies
      /// library building: the question `Pipeline.ensureLibrary` answers is whether a
      /// library needs to be rebuilt.
      IfStale: bool
      /// File to read additional input files from, one per line.
      FilesFrom: string option
      /// File to write the batch's report to. `-` means stdout.
      Report: string option
      /// `--emit-cs`: where the generated C# goes.
      EmitCs: string option
      /// `--dump-ast`: where the typed AST goes.
      DumpAst: string option
      /// `--emit-cs-beside`: each input's C# beside it.
      EmitCsBeside: bool
      /// Run the frontend over each input and stop, generating nothing.
      Check: bool

      /// The roots file `--roots` named, as it was written.
      ///
      /// Kept as the path rather than as the roots it loaded, because the build
      /// record has to name the file: what a module resolves to depends on it,
      /// and a driver comparing timestamps has to know that.
      Roots: string option

      /// The `--frameworks` file: which package declared which .NET shared
      /// framework, one TAB-separated pair per line. Kept as a path for the
      /// same reason the roots file is.
      FrameworksFile: string option

      /// `--framework`, repeatable: what a file no line of that file covers may
      /// name, which is how a single-file build declares anything at all.
      Frameworks: string list

      /// `--nuget`: the directory holding the restored packages' lists. See
      /// `NuGetRefs`.
      NuGet: string option

      /// `--runtime name=value`, repeatable: a program's .NET runtime
      /// settings, checked by `Build.parseRuntimeSetting`. The last value
      /// given for a name is the one used.
      Runtime: (string * string) list

      /// `--aot`: a program is also published as a Native AOT executable.
      Aot: bool

      /// `--aot-cpu <set>`: the instruction sets of the native executable.
      AotCpu: string option

      /// `--build-graph`: build the libraries under the inputs, files or
      /// directories, and everything they import, in parallel. See `BuildGraph`.
      BuildGraph: bool

      /// `--jobs N`: how many workers a graph build runs at most.
      Jobs: int

      /// `--dry-run`: plan a graph build and say what it would do.
      DryRun: bool
      /// `--deps-only`: a graph build leaves out the inputs themselves.
      DepsOnly: bool
      /// `--quiet`: a graph build says nothing about what was current.
      Quiet: bool

      /// `--worker`: build libraries named on stdin for a graph build. A graph
      /// build starts these processes itself; they are not meant to be run by
      /// hand.
      Worker: bool }

let defaultOptions =
    { InputFiles = []
      IsLibrary = false
      Debug = false
      Repl = false
      Batch = false
      IfStale = false
      FilesFrom = None
      Report = None
      EmitCs = None
      DumpAst = None
      EmitCsBeside = false
      Check = false
      Roots = None
      FrameworksFile = None
      Frameworks = []
      NuGet = None
      Runtime = []
      Aot = false
      AotCpu = None
      BuildGraph = false
      Jobs = System.Environment.ProcessorCount
      DryRun = false
      DepsOnly = false
      Quiet = false
      Worker = false }

let printUsage () =
    printfn "Bjolang Compiler"
    printfn "Usage: bjoc [options] <source.bjo>"
    printfn "       bjoc --batch [options] <source.bjo>..."
    printfn ""
    printfn "Options:"
    printfn "  --repl      Read, evaluate and print Bjolang forms until end of input."
    printfn "              At a terminal, with line editing, completion and history."
    printfn "  --lib       Compile the source as a library (.dll) instead of an executable"
    printfn "  -d, --debug Build unoptimized, for a debugger: the program steps through"
    printfn "              its .bjo lines. Writes nothing else."
    printfn "  --aot       Also publish a program as a Native AOT executable: <name> beside"
    printfn "              the source (<name>.native.exe on Windows). It starts in a few"
    printfn "              milliseconds and needs no .NET installed. The build takes"
    printfn "              about 10 s more, and needs clang or gcc. A library ignores it."
    printfn "  --aot-cpu <set>"
    printfn "              The CPU that --aot builds for: native (this machine), or a"
    printfn "              level such as x86-64-v3 (AVX2). The default is a baseline"
    printfn "              CPU, so vectors have 128 bits. The executable fails to start"
    printfn "              on a CPU without the instructions."
    printfn "  --emit-cs <file>"
    printfn "              Write the generated C# to <file>, in a debug build or not."
    printfn "  --emit-cs-beside"
    printfn "              Write each input's C# beside it, as <name>.out.cs. What a"
    printfn "              batch takes instead of --emit-cs."
    printfn "  --dump-ast <file>"
    printfn "              Write the typed AST that code generation is given to <file>."
    printfn "  --check     Check for errors without generating code. Reports every error"
    printfn "              the frontend finds and writes no assembly."
    printfn "  --roots <file>"
    printfn "              Where the packages are. One directory per package name:"
    printfn "                (roots (root (bjorsec) \"/proj/.bjo/pkg/bjorsec@1.2.3/src\")"
    printfn "                       (root (myapp)   \"src\"))"
    printfn "              A relative directory is relative to the roots file. The standard"
    printfn "              library is always a set of roots and may not be named here."
    printfn "              Whoever writes this file must leave it alone when its contents"
    printfn "              have not changed: it is an input of every build made against it,"
    printfn "              so rewriting it makes everything stale."
    printfn "  --frameworks <file>"
    printfn "              Which package declared which .NET shared framework. One line per"
    printfn "              pair: the package's directory, a tab, the framework's name."
    printfn "              A module may only *name* types from a framework its own package"
    printfn "              declares; it may use values of any type that reaches it. Written"
    printfn "              by `bjo`, and left alone when unchanged for the reason above."
    printfn "  --runtime <name>=<value>"
    printfn "              A .NET runtime setting of the program, written into its"
    printfn "              runtimeconfig. Repeatable. The settings are %s." (Build.runtimeSettings |> List.map fst |> String.concat ", ")
    printfn "              gc-heap-count is a whole number from 1, and call-counting-delay"
    printfn "              milliseconds from 0; the others are true or false."
    printfn "              With gc-server=true, gc-dynamic-adaptation is false unless given."
    printfn "              call-counting-delay is 0 unless given (.NET's default is 100)."
    printfn "  --framework <name>"
    printfn "              A shared framework for every file no --frameworks line covers,"
    printfn "              which is all of them in a single-file build. Repeatable."
    printfn "  --nuget <dir>"
    printfn "              Restored NuGet packages: <dir>/compile.txt lists the assemblies"
    printfn "              the C# compile references, <dir>/runtime.txt the ones the type"
    printfn "              checker loads and the program resolves at run time. One path"
    printfn "              per line. Written by `bjo`, which restores the packages the"
    printfn "              manifest names."
    printfn "  --help      Show this help message"
    printfn ""
    printfn "Batch options:"
    printfn "  --batch     Compile every input in this one process. A cold compiler costs"
    printfn "              about half a second before it does anything; a batch pays that"
    printfn "              once rather than per file."
    printfn "  --files-from <file>"
    printfn "              Read further inputs from <file>, one path per line. Blank lines"
    printfn "              and lines starting with # are ignored."
    printfn "  --report <file>"
    printfn "              Write one JSON object per input to <file>, or to stdout for '-'."
    printfn "              Fields: file, status, artifact, output."
    printfn "  --if-stale  Build only the inputs whose .dll is out of date, as an import"
    printfn "              would. Implies --lib."
    printfn ""
    printfn "Under --batch the exit code says only whether every input compiled; which one"
    printfn "did not, and what it said, is in the report."
    printfn ""
    printfn "Graph builds:"
    printfn "  --build-graph <path>..."
    printfn "              Build the libraries under each path — a .bjo, or every .bjo"
    printfn "              below a directory — and everything they import, in parallel."
    printfn "              The order comes from the imports; a module is built when all it"
    printfn "              imports is built, and only if it is out of date or something"
    printfn "              below it was rebuilt. Directories starting with . are skipped."
    printfn "  -j, --jobs <n>"
    printfn "              At most n compilers at once. Defaults to the number of cores."
    printfn "  --dry-run   Say what a graph build would build, and what each module"
    printfn "              imports, and build nothing."
    printfn "  --deps-only Build what the given files import, and not the files: what a"
    printfn "              program's entry needs before it is compiled as a program."
    printfn "  --quiet     Say nothing about modules that were current, and give the"
    printfn "              summary only when something failed."
    printfn ""
    printfn "Without -d the output is optimized; a debug build runs several times slower."

/// Reads a `--files-from` list.
///
/// Empty lines and `#` lines are skipped, so that a generated list can
/// be commented and a trailing newline does not become a file named
/// nothing.
let private readFileList (path: string) : string list =
    File.ReadAllLines path
    |> Array.map (fun line -> line.Trim())
    |> Array.filter (fun line -> line <> "" && not (line.StartsWith "#"))
    |> Array.toList

let rec parseArgs (args: string list) (opts: CompilerOptions) =
    match args with
    | [] -> opts
    | "--help" :: _ ->
        printUsage ()
        exit 0
    | "--repl" :: rest -> parseArgs rest { opts with Repl = true }
    | "--lib" :: rest -> parseArgs rest { opts with IsLibrary = true }
    | "--batch" :: rest -> parseArgs rest { opts with Batch = true }
    | "--check" :: rest -> parseArgs rest { opts with Check = true }
    | "--if-stale" :: rest -> parseArgs rest { opts with IfStale = true; IsLibrary = true }
    | "--files-from" :: path :: rest -> parseArgs rest { opts with FilesFrom = Some path }
    | "--report" :: path :: rest -> parseArgs rest { opts with Report = Some path }
    // A source file where the dump's file should be is the dump's file left
    // out, and taken as it stands it would be written over.
    | ("--emit-cs" | "--dump-ast" as flag) :: path :: _ when
        [ ".bjo"; ".bjodoc"; ".protobjo" ] |> List.contains (Path.GetExtension(path: string))
        ->
        let example = if flag = "--emit-cs" then "out.cs" else "ast.txt"
        printfn $"Error: %s{flag} would write over %s{path}. It takes the file to write to, as in %s{flag} %s{example}, before the source."
        exit 1
    | "--emit-cs" :: path :: rest when not (path.StartsWith "-") -> parseArgs rest { opts with EmitCs = Some path }
    | "--dump-ast" :: path :: rest when not (path.StartsWith "-") -> parseArgs rest { opts with DumpAst = Some path }
    // The file is not optional: a dump into a name nobody chose is a file
    // left in whatever directory the compiler happened to run in.
    | ("--emit-cs" | "--dump-ast" as flag) :: _ ->
        let example = if flag = "--emit-cs" then "out.cs" else "ast.txt"
        printfn $"Error: %s{flag} needs the file to write to, as in %s{flag} %s{example}."
        exit 1
    | "--emit-cs-beside" :: rest -> parseArgs rest { opts with EmitCsBeside = true }
    | "--roots" :: path :: rest -> parseArgs rest { opts with Roots = Some path }
    | "--frameworks" :: path :: rest -> parseArgs rest { opts with FrameworksFile = Some path }
    | "--nuget" :: dir :: rest -> parseArgs rest { opts with NuGet = Some dir }
    | "--runtime" :: setting :: rest ->
        (match Build.parseRuntimeSetting setting with
         | Ok(name, value) ->
             let others = opts.Runtime |> List.filter (fun (n, _) -> n <> name)
             parseArgs rest { opts with Runtime = (name, value) :: others |> List.sortBy fst }
         | Error message ->
             printfn $"Error: %s{message}"
             exit 1)
    // Repeatable, like an input file and for the same reason: a build declares
    // as many frameworks as it declares, and the last one is not the only one.
    | "--framework" :: name :: rest ->
        parseArgs rest { opts with Frameworks = opts.Frameworks @ [ name ] }
    | "-d" :: rest
    | "--debug" :: rest -> parseArgs rest { opts with Debug = true }
    | "--aot" :: rest -> parseArgs rest { opts with Aot = true }
    | "--aot-cpu" :: cpu :: rest when not (System.String.IsNullOrWhiteSpace cpu) && not (cpu.StartsWith "-") ->
        parseArgs rest { opts with AotCpu = Some cpu }
    | "--aot-cpu" :: _ ->
        printfn "Error: --aot-cpu takes the instruction sets to build for: native, or a level such as x86-64-v3."
        exit 1
    | "--build-graph" :: rest -> parseArgs rest { opts with BuildGraph = true }
    | "--dry-run" :: rest -> parseArgs rest { opts with DryRun = true }
    | "--deps-only" :: rest -> parseArgs rest { opts with DepsOnly = true }
    | "--quiet" :: rest -> parseArgs rest { opts with Quiet = true }
    | "--worker" :: rest -> parseArgs rest { opts with Worker = true }
    | ("-j" | "--jobs") :: n :: rest ->
        match System.Int32.TryParse n with
        | true, jobs when jobs > 0 -> parseArgs rest { opts with Jobs = jobs }
        | _ ->
            printfn $"Error: --jobs needs a positive number, not '%s{n}'."
            exit 1
    | arg :: rest when not (arg.StartsWith("-")) ->
        // Everything that doesn't start with '-' is an input file. The order is preserved, so
        // a batch compiles in the order the command line named the files.
        parseArgs rest { opts with InputFiles = opts.InputFiles @ [ arg ] }
    | unknown :: _ ->
        printfn $"Error: Unknown argument '%s{unknown}'"
        printUsage ()
        exit 1

let private run (argv: string array) =
    Build.installDependencyBackend ()

    let options = parseArgs (Array.toList argv) defaultOptions

    // Before anything is resolved, and before the REPL, a batch or a check:
    // see `ProjectInputs.install`. A framework that is not installed is said
    // once, here, rather than as a type error on the first name that could not
    // be found, which would name the type and not the reason.
    match ProjectInputs.install options.Roots options.FrameworksFile options.Frameworks options.NuGet with
    | Ok() -> ()
    | Error message ->
        printfn $"%s{message}"
        exit 1

    if options.Repl then
        Repl.run ()
    elif options.Worker then
        Build.runWorker ()
    else

    let inputFiles =
        match options.FilesFrom with
        | Some path when not (File.Exists path) ->
            printfn $"Error: File list '%s{path}' not found."
            exit 1
        | Some path -> options.InputFiles @ readFileList path
        | None -> options.InputFiles

    // Handled before the file checks below, because a graph build's inputs may
    // be directories. `BuildGraph.run` reports missing inputs itself.
    if options.BuildGraph then
        if inputFiles.IsEmpty then
            printfn "Error: --build-graph needs at least one file or directory."
            exit 1

        BuildGraph.run inputFiles options.Jobs options.DryRun options.DepsOnly options.Quiet
    else

    if inputFiles.IsEmpty then
        printfn "Error: No input file specified."
        printUsage ()
        exit 1

    match inputFiles |> List.filter (File.Exists >> not) with
    | [] -> ()
    | missing ->
        for path in missing do
            printfn $"Error: Source file '%s{path}' not found."

        exit 1

    // Both of these ask for a build product from a mode that produces none.
    // Refused rather than ignored: silently dropping one of the two would leave
    // whoever passed them waiting for a file that is never written.
    if options.Check && (options.EmitCs.IsSome || options.EmitCsBeside || options.DumpAst.IsSome) then
        printfn "Error: --check generates no code, so there is nothing for --emit-cs, --emit-cs-beside or --dump-ast to write."
        exit 1

    // One file for many inputs would hold whichever was compiled last.
    if options.Batch && (options.EmitCs.IsSome || options.DumpAst.IsSome) then
        printfn "Error: --emit-cs and --dump-ast name one file, and a batch compiles many. Use --emit-cs-beside for each input's C# beside it."
        exit 1

    if options.Check && options.IfStale then
        printfn "Error: --check builds nothing, so --if-stale has no .dll to judge as stale."
        exit 1

    let buildOptions: Build.Options =
        { IsLibrary = options.IsLibrary
          Runtime = options.Runtime
          Aot = options.Aot
          AotCpu = options.AotCpu
          Debug = options.Debug
          EmitCs = options.EmitCs
          DumpAst = options.DumpAst
          EmitCsBeside = options.EmitCsBeside
          Check = options.Check }

    if options.Batch then
        Build.runBatch buildOptions options.IfStale options.Report inputFiles
    else
        match inputFiles with
        | [ inputFilePath ] -> Build.compile buildOptions inputFilePath
        | _ ->
            printfn "Error: Multiple input files specified. Use --batch to compile them together."
            exit 1

[<EntryPoint>]
let main argv =
    try
        run argv
    finally
        Timing.report ()
