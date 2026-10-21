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

/// What a project gives a compilation besides its source: the roots file that
/// says where each package is, the frameworks file that says which package
/// declared which .NET shared framework, and the directory of its restored
/// NuGet packages. `bjo` writes all three under a project's `.bjo`, and the
/// command line names them with `--roots`, `--frameworks` and `--nuget`.
///
/// Read here rather than in `Program`, so that the language server installs
/// them the way the command line does.
module Bjolang.ProjectInputs

open System.IO

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
let loadRoots (rootsPath: string) : Paths.PackageRoot list =
    let shape = "(roots (root (package name) \"directory\") ...)"
    let full = Path.GetFullPath rootsPath

    if not (File.Exists full) then
        failwithf $"--roots: no such file '%s{rootsPath}'."

    let baseDir = Path.GetDirectoryName full
    let forms, _ = Lexer.tokenize full (File.ReadAllText full) |> Pipeline.read

    let where (form: Ast.SExpr) = Lexer.formatPos (Ast.getRange form)

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
                    failwithf $"Invalid roots file at %s{Lexer.formatPos nameRange}: a package name has at least one segment."

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

/// Reads the file `--frameworks` named: one line per declared framework per
/// package, as `<package directory>TAB<framework name>`.
///
/// Two fields and a tab rather than Bjolang, unlike the roots file: it is
/// written by a tool and read by a tool, a package directory is a path and a
/// framework name has no syntax of its own, and nothing here is ever edited by
/// hand.
let loadFrameworks (path: string) : (string * string) list =
    let full = Path.GetFullPath path

    if not (File.Exists full) then
        failwithf $"--frameworks: no such file '%s{path}'."

    File.ReadAllLines full
    |> Array.toList
    |> List.mapi (fun i line -> i + 1, line)
    |> List.filter (fun (_, line) -> line.Trim() <> "" && not (line.StartsWith "#"))
    |> List.map (fun (lineNumber, line) ->
        match line.Split('\t') with
        | [| dir; name |] when dir.Trim() <> "" && name.Trim() <> "" -> dir.Trim(), name.Trim()
        | _ ->
            failwithf
                $"Invalid frameworks file '%s{full}', line %d{lineNumber}: a line is a package directory, a tab, and a framework name.")

/// Installs what a project gives a compilation, in the order every lookup
/// needs: the packages first, then which of them declared which framework,
/// the frameworks the command line declared, and the NuGet packages. Then
/// every declared framework is checked to be installed, with a reference pack
/// beside it.
///
/// Before anything is resolved: every derived module name and every import
/// goes through the package registry, so installing it late would give the
/// files read before it a different identity from the ones read after.
///
/// The error is the message to print, as the command line prints it.
let install (roots: string option) (frameworks: string option) (globalFrameworks: string list) (nuget: string option)
    : Result<unit, string> =
    let attempt (f: unit -> unit) =
        try
            f ()
            Ok()
        with ex ->
            Error $"Error: %s{ex.Message}"

    let fromFile (path: string option) (f: string -> unit) =
        match path with
        | Some p -> attempt (fun () -> f p)
        | None -> Ok()

    fromFile roots (fun path -> Paths.setConfiguredRoots path (loadRoots path))
    |> Result.bind (fun () -> fromFile frameworks (fun path -> Frameworks.setDeclared path (loadFrameworks path)))
    |> Result.bind (fun () ->
        Frameworks.setGlobal globalFrameworks
        fromFile nuget NuGetRefs.load)
    |> Result.bind (fun () ->
        try
            for name in Frameworks.declaredHere () do
                Frameworks.checkAvailable name

            Ok()
        with ex ->
            Error ex.Message)
