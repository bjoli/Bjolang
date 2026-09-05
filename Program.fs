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
      /// Where `-d` puts the generated C# code.
      EmitCs: string option }

let defaultOptions =
    { InputFiles = []
      IsLibrary = false
      Debug = false
      Repl = false
      Batch = false
      IfStale = false
      FilesFrom = None
      Report = None
      EmitCs = None }

let printUsage () =
    printfn "Bjolang Compiler"
    printfn "Usage: bjoc [options] <source.bjo>"
    printfn "       bjoc --batch [options] <source.bjo>..."
    printfn ""
    printfn "Options:"
    printfn "  --repl      Read, evaluate and print Bjolang forms until end of input."
    printfn "              No line editing — run it under rlwrap."
    printfn "  --lib       Compile the source as a library (.dll) instead of an executable"
    printfn "  -d, --debug Build unoptimized, with debug symbols, and dump the typed AST to"
    printfn "              ast_dump.txt and the generated C# to out.cs"
    printfn "  --emit-cs <file>"
    printfn "              Under -d, write the generated C# here instead of out.cs. The AST"
    printfn "              dump goes beside it."
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
    | "--if-stale" :: rest -> parseArgs rest { opts with IfStale = true; IsLibrary = true }
    | "--files-from" :: path :: rest -> parseArgs rest { opts with FilesFrom = Some path }
    | "--report" :: path :: rest -> parseArgs rest { opts with Report = Some path }
    | "--emit-cs" :: path :: rest -> parseArgs rest { opts with EmitCs = Some path }
    | "-d" :: rest
    | "--debug" :: rest -> parseArgs rest { opts with Debug = true }
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

    if options.Repl then
        Repl.run ()
    else

    let inputFiles =
        match options.FilesFrom with
        | Some path when not (File.Exists path) ->
            printfn $"Error: File list '%s{path}' not found."
            exit 1
        | Some path -> options.InputFiles @ readFileList path
        | None -> options.InputFiles

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

    let buildOptions: Build.Options =
        { IsLibrary = options.IsLibrary
          Debug = options.Debug
          EmitCs = options.EmitCs }

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
