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
      /// Where `-d` puts the generated C# code.
      EmitCs: string option
      /// Run the frontend over each input and stop, generating nothing.
      Check: bool

      /// The roots file `--roots` named, as it was written.
      ///
      /// Kept as the path rather than as the roots it loaded, because the build
      /// record has to name the file: what a module resolves to depends on it,
      /// and a driver comparing timestamps has to know that.
      Roots: string option }

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
      Check = false
      Roots = None }

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

/// Reads the file `--roots` named: one directory per package name.
///
/// Read with the compiler's own lexer and reader rather than with a parser of
/// its own, so that a roots file is Bjolang and quoting, comments and escapes
/// mean there what they mean everywhere else. `Paths` cannot do this itself —
/// it is compiled before the lexer, and has to be, because `Naming` asks it
/// what a file is called.
///
/// Every failure here names the line and column, because the file is written by
/// a tool and read by a person only when something is wrong with it.
let private loadRoots (rootsPath: string) : Paths.PackageRoot list =
    let shape = "(roots (root (package name) \"directory\") ...)"
    let full = Path.GetFullPath rootsPath

    if not (File.Exists full) then
        failwithf $"--roots: no such file '%s{rootsPath}'."

    let baseDir = Path.GetDirectoryName full
    let forms, _ = Lexer.tokenize full (File.ReadAllText full) |> Pipeline.read

    /// With the column, unlike the compiler's diagnostics for source files: a
    /// roots file is written by a tool, which is free to put every package on
    /// one line, and then the line alone points at all of them.
    let at (r: Lexer.Range) = $"%s{Lexer.formatPos r}:%d{r.Start.Column}"
    let where (form: Ast.SExpr) = at (Ast.getRange form)

    let entries =
        match forms with
        | [ Ast.SList(Ast.SAtom { Token = Lexer.Symbol "roots" } :: entries, _) ] -> entries
        | form :: _ -> failwithf $"Invalid roots file at %s{where form}: expected one %s{shape} form."
        | [] -> failwithf $"Invalid roots file '%s{full}': it is empty, and a build needs at least the %s{shape} head."

    let roots =
        entries
        |> List.map (fun entry ->
            match entry with
            | Ast.SList([ Ast.SAtom { Token = Lexer.Symbol "root" }
                          Ast.SList(nameForms, nameRange)
                          Ast.SAtom { Token = Lexer.StringLit dir } ],
                        _) ->
                let name =
                    nameForms
                    |> List.map (function
                        | Ast.SAtom { Token = Lexer.Symbol s } -> s
                        | bad ->
                            failwithf
                                $"Invalid roots file at %s{where bad}: a package name is a list of plain symbols, as a module path is.")

                if name.IsEmpty then
                    failwithf $"Invalid roots file at %s{at nameRange}: a package name has at least one segment."

                // Relative to the roots file, not to the working directory: the
                // file is written next to the project it describes, and a
                // driver that produced it has no idea where it will be read
                // from.
                let directory =
                    if Path.IsPathRooted dir then
                        Path.GetFullPath dir
                    else
                        Path.GetFullPath(Path.Combine(baseDir, dir))

                if not (Directory.Exists directory) then
                    failwithf
                        $"Invalid roots file at %s{where entry}: package %s{Paths.showPackageName name} points at '%s{directory}', which is not a directory."

                { Paths.Name = name
                  Paths.Directory = directory.TrimEnd Path.DirectorySeparatorChar
                  Paths.IsStandardLibrary = false },
                entry
            | bad -> failwithf $"Invalid roots file at %s{where bad}: a root is written (root (package name) \"directory\").")

    // The standard library is a set of roots already, and a second directory
    // for one of its names would decide `(std prelude)` by list order. Refused
    // rather than overridden: a program that means to replace the standard
    // library is asking for `BJOLANG_LIB`, which replaces all of it at once.
    for (root, entry) in roots do
        if Paths.packageRoots () |> List.exists (fun s -> s.IsStandardLibrary && s.Name = root.Name) then
            failwithf
                $"Invalid roots file at %s{where entry}: %s{Paths.showPackageName root.Name} is part of the standard library and cannot be given a directory here."

    // A name with two directories is refused rather than resolved by order,
    // because the loser is not silent: it is a package whose modules exist and
    // are never reached.
    roots
    |> List.groupBy (fun (root, _) -> root.Name)
    |> List.iter (fun (name, group) ->
        if group.Length > 1 then
            let places = group |> List.map (fun (_, entry) -> where entry) |> String.concat " and "

            failwithf
                $"Invalid roots file: %s{Paths.showPackageName name} is given a directory twice, at %s{places}. One package name, one directory.")

    roots |> List.map fst

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
    | "--emit-cs" :: path :: rest -> parseArgs rest { opts with EmitCs = Some path }
    | "--roots" :: path :: rest -> parseArgs rest { opts with Roots = Some path }
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

    // Before anything is resolved, and before the REPL, a batch or a check:
    // every derived module name and every import goes through the registry, so
    // installing it late would give the files read before it a different
    // identity from the ones read after.
    match options.Roots with
    | Some path ->
        try
            Paths.setConfiguredRoots path (loadRoots path)
        with ex ->
            printfn $"Error: %s{ex.Message}"
            exit 1
    | None -> ()

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

    // Both of these ask for a build product from a mode that produces none.
    // Refused rather than ignored: silently dropping one of the two would leave
    // whoever passed them waiting for a file that is never written.
    if options.Check && options.EmitCs.IsSome then
        printfn "Error: --check generates no C#, so there is nothing for --emit-cs to write."
        exit 1

    if options.Check && options.IfStale then
        printfn "Error: --check builds nothing, so --if-stale has no .dll to judge as stale."
        exit 1

    let buildOptions: Build.Options =
        { IsLibrary = options.IsLibrary
          Debug = options.Debug
          EmitCs = options.EmitCs
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
