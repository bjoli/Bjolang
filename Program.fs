/// The command line, and nothing else.
///
/// What a compilation *is* lives in `Build`, so that building an imported
/// module is a call rather than a process.
module Bjolang.Program

open Bjolang
open System.IO

type CompilerOptions =
    { InputFile: string option
      IsLibrary: bool
      Debug: bool
      Repl: bool }

let defaultOptions =
    { InputFile = None; IsLibrary = false; Debug = false; Repl = false }

let printUsage () =
    printfn "Bjolang Compiler"
    printfn "Usage: bjoc [options] <source.bjo>"
    printfn ""
    printfn "Options:"
    printfn "  --repl      Read, evaluate and print Bjolang forms until end of input."
    printfn "              No line editing — run it under rlwrap."
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
    | "--repl" :: rest -> parseArgs rest { opts with Repl = true }
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

let private run (argv: string array) =
    Build.installDependencyBackend ()

    let options = parseArgs (Array.toList argv) defaultOptions

    if options.Repl then
        Repl.run ()
    else

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

    Build.compile { IsLibrary = options.IsLibrary; Debug = options.Debug } inputFilePath

[<EntryPoint>]
let main argv =
    try
        run argv
    finally
        Timing.report ()
