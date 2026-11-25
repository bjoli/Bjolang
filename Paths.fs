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

/// Resolves paths to installed compiler resources (the standard library and
/// runtime support assemblies) as well as package roots and module paths.
///
/// Installation paths are resolved relative to the compiler binary (or env
/// vars) rather than just the working directory, so invoking the compiler from
/// an arbitrary directory still finds the prebuilt `lib/std` instead of
/// falling back to recompiling `std/prelude.bjo` into user programs.
module Bjolang.Paths

open System
open System.IO

let private ancestorsOf (start: string) : string list =
    if String.IsNullOrWhiteSpace start then
        []
    else
        let rec walk (d: DirectoryInfo) =
            if isNull d then [] else d.FullName :: walk d.Parent

        walk (DirectoryInfo(Path.GetFullPath start))

/// Candidate `lib` directories in priority order: `BJOLANG_LIB`,
/// `BJOLANG_HOME/lib`, ancestors of the compiler binary, and ancestors of the
/// current working directory (so `dotnet run` from the repo root works).
let private libCandidates () : string seq =
    seq {
        match Environment.GetEnvironmentVariable "BJOLANG_LIB" with
        | null | "" -> ()
        | p -> yield Path.GetFullPath p

        match Environment.GetEnvironmentVariable "BJOLANG_HOME" with
        | null | "" -> ()
        | p -> yield Path.GetFullPath(Path.Combine(p, "lib"))

        for dir in ancestorsOf AppContext.BaseDirectory do
            yield Path.Combine(dir, "lib")

        for dir in ancestorsOf Environment.CurrentDirectory do
            yield Path.Combine(dir, "lib")
    }

/// Directory containing the standard library. Must contain a `std`
/// subdirectory so an unrelated `lib` folder doesn't shadow the real one.
let libDir: string =
    libCandidates ()
    |> Seq.tryFind (fun lib -> Directory.Exists(Path.Combine(lib, "std")))
    |> Option.defaultWith (fun () -> Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "lib")))
    |> Path.GetFullPath

/// Installation root (the parent directory of `libDir`).
let root: string = Path.GetFullPath(Path.Combine(libDir, ".."))

/// Path to the compiler assembly used for staleness checks and out-of-process
/// dependency builds (empty when published as a native single-file host).
///
/// Defaults to the executing assembly, but an embedding host (like an LSP
/// server) can override this to point at the installed CLI compiler so it
/// doesn't treat every prebuilt module as stale.
let compilerAssembly =
    Compilation.Per<string>((fun () -> Reflection.Assembly.GetExecutingAssembly().Location), true)

/// In-memory source overrides keyed by full file path. Used by the LSP server
/// to type-check unsaved editor buffers under their real paths so module names
/// and imports resolve normally. Empty for CLI runs.
let sourceOverrides = Compilation.Per<Map<string, string>>((fun () -> Map.empty), true)

/// Reads a source file, preferring an in-memory override if one is registered.
let readSource (path: string) : string =
    match Map.tryFind (Path.GetFullPath path) sourceOverrides.Value with
    | Some text -> text
    | None -> File.ReadAllText path

// ---------------------------------------------------------------------------
// Packages
// ---------------------------------------------------------------------------

/// Maps a package name prefix (e.g. `(bjorsec)`) to its root directory.
/// Module paths under that prefix resolve to files inside `Directory`, allowing
/// dependencies to live anywhere on disk while keeping stable module names.
type PackageRoot =
    { /// Module path prefix, e.g. `[ "std" ]` or `[ "bjolang"; "http" ]`.
      Name: string list
      /// Absolute path without a trailing separator.
      Directory: string
      /// True for installed standard library packages (no implicit prelude,
      /// and allowed internal compiler features).
      IsStandardLibrary: bool }

/// Formats a package name as S-expression syntax for diagnostics.
let showPackageName (name: string list) : string =
    "(" + String.concat " " name + ")"

/// Ensures `dir` ends with a single directory separator so prefix checks don't
/// falsely match sibling directories (e.g. `/p/src` vs `/p/src2/m.bjo`).
let private asPrefix (dir: string) : string =
    dir.TrimEnd Path.DirectorySeparatorChar + string Path.DirectorySeparatorChar

/// Built-in standard library packages: one per top-level directory in `libDir`.
/// Always available even without a `--roots` file.
let private standardRoots: PackageRoot list =
    if Directory.Exists libDir then
        Directory.GetDirectories libDir
        |> Array.toList
        |> List.map (fun dir ->
            { Name = [ Path.GetFileName(dir.TrimEnd Path.DirectorySeparatorChar) ]
              Directory = Path.GetFullPath(dir.TrimEnd Path.DirectorySeparatorChar)
              IsStandardLibrary = true })
        |> List.sortBy (fun r -> r.Name)
    else
        []

/// Extra package roots loaded from `--roots`.
let private configuredRoots = Compilation.Per<PackageRoot list>((fun () -> []), true)

/// Path to the `--roots` file, saved for build records.
let private rootsFilePath = Compilation.Per<string option>((fun () -> None), true)

/// Registers package roots from `--roots`.
///
/// Must be called once before compilation starts; changing roots later would
/// invalidate path-keyed caches in `Naming` and `Pipeline`.
let setConfiguredRoots (file: string) (roots: PackageRoot list) : unit =
    rootsFilePath.Value <- Some(Path.GetFullPath file)
    configuredRoots.Value <- roots

/// Returns the active `--roots` file path, if any, so build records for
/// compiled dependencies can record it too.
let rootsFile () : string option = rootsFilePath.Value

/// All registered package roots (standard library plus configured roots).
let packageRoots () : PackageRoot list = standardRoots @ configuredRoots.Value

/// Result of resolving a module path against registered package roots.
type ModuleLookup =
    /// Matched a package root and computed the `.bjo` path (which may or may
    /// not exist on disk; callers check depending on source vs. prebuilt DLL).
    | ModuleFile of PackageRoot * string
    /// No registered package is a proper prefix of the module path. Carries
    /// the expected package name for error messages.
    | NoPackage of string list
    /// The path exactly matches a package name (a directory, not a module).
    | PackageItself of PackageRoot

/// Resolves a module path to its owning package and `.bjo` file path.
///
/// Uses longest-prefix matching so a sub-package like `(bjolang http)` takes
/// precedence over `(bjolang)` for `(bjolang http client)`.
let findModule (parts: string list) : ModuleLookup =
    let isPrefix (name: string list) =
        name.Length <= parts.Length
        && List.forall2 (fun (a: string) b -> String.Equals(a, b, StringComparison.Ordinal)) name (List.truncate name.Length parts)

    let roots = packageRoots ()

    let best =
        roots
        |> List.filter (fun r -> r.Name.Length < parts.Length && isPrefix r.Name)
        |> List.sortByDescending (fun r -> r.Name.Length)
        |> List.tryHead

    match best with
    | Some r ->
        let rest = parts |> List.skip r.Name.Length
        ModuleFile(r, Path.GetFullPath(Path.Combine(r.Directory, Path.Combine(Array.ofList rest) + ".bjo")))
    | None ->
        match roots |> List.tryFind (fun r -> r.Name = parts) with
        | Some r -> PackageItself r
        | None ->
            // Guess the missing package name by dropping the final module segment
            // (or keep the whole path if it's only one segment).
            NoPackage(if parts.Length > 1 then parts |> List.take (parts.Length - 1) else parts)

/// Relative `.bjo` path of a module within its package (e.g. `combine/parsers.bjo`).
let moduleFileName (root: PackageRoot) (parts: string list) : string =
    (parts |> List.skip (min root.Name.Length parts.Length) |> String.concat "/") + ".bjo"

/// Module identity derived from a file's location on disk.
type FileIdentity =
    { /// The package the file belongs to.
      Root: PackageRoot
      /// Full module name: package name followed by relative path segments
      /// (without the file extension).
      ModuleName: string list }

/// Determines a file's owning package and canonical module name from its path.
///
/// Using the file path (rather than the import spelling) ensures a file always
/// gets the same identity whether imported by path or by package name. Uses
/// the deepest containing package root, matching `findModule`. Returns `None`
/// for standalone files outside any package root.
let identityOf (file: string) : FileIdentity option =
    let full = Path.GetFullPath file

    packageRoots ()
    |> List.filter (fun r -> full.StartsWith(asPrefix r.Directory, StringComparison.Ordinal))
    |> List.sortByDescending (fun r -> r.Directory.Length)
    |> List.tryHead
    |> Option.map (fun r ->
        let below = full.Substring((asPrefix r.Directory).Length)

        let segments =
            match Path.ChangeExtension(below, null) with
            | "" -> []
            | stripped -> stripped.Split Path.DirectorySeparatorChar |> List.ofArray

        { Root = r; ModuleName = r.Name @ segments })

/// Checks whether `importer` is allowed to import or include `target`.
/// Returns `Some reason` if forbidden, or `None` if allowed.
///
/// Directories starting with `_` are private to their parent directory: e.g.
/// files under `src/_lib/` can only be imported by files under `src/` (plus
/// the same project's `tests/` and `benches/`). For package modules, `_`
/// directories above the package root are ignored.
///
/// Any `_` segment above the common ancestor covers both files, and a `_`
/// segment directly inside the common ancestor is a sibling of the importer
/// (allowed). Any deeper `_` segment on `target`'s path is forbidden, and we
/// report the innermost one.
let importRefusal (importer: string) (target: string) : string option =
    let importer = Path.GetFullPath importer
    let target = Path.GetFullPath target
    let targetDir = Path.GetDirectoryName target

    let segments (dir: string) =
        dir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

    let from = segments (Path.GetDirectoryName importer)
    let into = segments targetDir

    let shared =
        Seq.zip from into |> Seq.takeWhile (fun (a, b) -> String.Equals(a, b, StringComparison.Ordinal)) |> Seq.length

    let rec up (dir: string) (levels: int) =
        if levels <= 0 then dir else up (Path.GetDirectoryName dir) (levels - 1)

    let below = into |> List.skip shared

    let ownTests () =
        match identityOf target with
        | Some id when not id.Root.IsStandardLibrary && Path.GetFileName id.Root.Directory = "src" ->
            let project = Path.GetDirectoryName id.Root.Directory

            [ "tests"; "benches" ]
            |> List.exists (fun d -> importer.StartsWith(asPrefix (Path.Combine(project, d)), StringComparison.Ordinal))
        | _ -> false

    // For package modules, ignore `_` directories above the package root
    // (e.g. a dependency living in `../deps/_vendor/`).
    let firstOwn =
        match identityOf target with
        | Some id -> (segments id.Root.Directory).Length
        | None -> 0

    match
        below
        |> List.indexed
        |> List.filter (fun (i, s) -> i > 0 && shared + i >= firstOwn && s.StartsWith "_")
        |> List.tryLast
    with
    | None -> None
    | Some _ when ownTests () -> None
    | Some(i, name) ->
        let common = up targetDir below.Length
        let kept = up targetDir (below.Length - 1 - i)
        let owner = Path.GetDirectoryName kept
        let shown (p: string) = Path.GetRelativePath(common, p).Replace(Path.DirectorySeparatorChar, '/')

        let tests =
            match identityOf target with
            | Some id when
                not id.Root.IsStandardLibrary
                && Path.GetFileName id.Root.Directory = "src"
                && importer.StartsWith(asPrefix (Path.GetDirectoryName id.Root.Directory), StringComparison.Ordinal)
                ->
                " A project's tests/ and benches/ may import it as well."
            | _ -> ""

        // Prefer showing the `.bjo` filename if it exists (imports often
        // resolve to the compiled `.dll`).
        let source = Path.ChangeExtension(target, ".bjo")
        let written = Path.GetFileName(if File.Exists source then source else target)

        // If `importer` is outside the target package's project, tailor the
        // message so it doesn't suggest moving the importer into the package.
        match identityOf target with
        | Some theirs when
            not (importer.StartsWith(asPrefix (Path.GetDirectoryName theirs.Root.Directory), StringComparison.Ordinal))
            ->
            let package = showPackageName theirs.Root.Name
            let inPackage (p: string) =
                Path.GetRelativePath(Path.GetDirectoryName theirs.Root.Directory, p).Replace(Path.DirectorySeparatorChar, '/')

            Some(
                $"'%s{written}' is internal to the package %s{package}: it is in %s{inPackage kept}/, which only the package's modules under %s{inPackage owner}/ may import. Import a module of %s{package} outside %s{name}/ that exports what is needed."
            )
        | _ ->
            Some(
                $"'%s{written}' is in %s{shown kept}/, so only modules under %s{shown owner}/ may import it, and %s{shown importer} is not one of them. Move %s{shown importer} under %s{shown owner}/, or import a module outside %s{name}/ that exports what it needs.%s{tests}"
            )

/// Checks whether `file`'s derived module name actually resolves to a different
/// package (shadowing). For example, `<(bjolang)>/http/client.bjo` derives
/// `(bjolang http client)`, which is shadowed if a separate `(bjolang http)`
/// package is registered.
let shadowedBy (identity: FileIdentity) (file: string) : PackageRoot option =
    // Compare without extensions so `<root>/core.dll` and `<root>/core.bjo`
    // are recognized as the same module.
    let withoutExtension (p: string) = Path.ChangeExtension(Path.GetFullPath p, null)

    match findModule identity.ModuleName with
    | ModuleFile(owner, resolved) when
        not (String.Equals(withoutExtension resolved, withoutExtension file, StringComparison.Ordinal))
        ->
        Some owner
    | _ -> None

/// Runtime support assemblies linked by every compiled program, in link order.
/// Also reflected on at type-check time for `import/class` and `import/extern`.
///
/// Note: the concurrency runtime (`Bjoml` namespace) is compiled directly into
/// `BjolangRuntime` — there is no separate `Bjoml.dll`.
let private runtimeAssemblyNames =
    [ "BjolangRuntime"
      "SchemeList"
      "BjoString"
      "BjoRx" ]

/// Whether to use precompiled (ReadyToRun) runtime/stdlib assemblies. Enabled
/// by default unless `BJOLANG_NO_R2R` is set to a non-`"0"` value. Toggling
/// this changes `runtimeDir`, which invalidates cached builds.
let readyToRun: bool =
    match Environment.GetEnvironmentVariable "BJOLANG_NO_R2R" with
    | null | "" | "0" -> true
    | _ -> false

/// Target framework of the .NET runtime hosting the compiler (e.g. `net11.0`).
let targetFramework: string = $"net{Environment.Version.Major}.0"

/// Framework version written into a compiled program's `.runtimeconfig.json`.
/// Uses `<major>.0.0` on stable runtimes, or the full prerelease version on
/// prerelease runtimes (since .NET won't roll `11.0.0` forward to `11.0.0-rc.*`).
let frameworkVersion: string =
    let installed = Path.GetFileName(Path.GetDirectoryName(typeof<obj>.Assembly.Location))
    if installed.Contains '-' then installed else $"{Environment.Version.Major}.0.0"

/// Directory containing the runtime support assemblies.
///
/// When `readyToRun` is enabled, prefers `bin/compiler` (which holds the
/// ReadyToRun-published copies) over `BjolangRuntime/bin/Release` (which
/// MSBuild overwrites on normal builds).
let runtimeDir: string =
    let precompiled =
        if readyToRun then [ Path.Combine(root, "bin", "compiler") ] else []

    let candidates =
        [ yield! precompiled
          for dir in ancestorsOf AppContext.BaseDirectory do
              yield Path.Combine(dir, "BjolangRuntime", "bin", "Release", targetFramework)
          yield Path.Combine(root, "BjolangRuntime", "bin", "Release", targetFramework)
          yield Path.Combine(Environment.CurrentDirectory, "BjolangRuntime", "bin", "Release", targetFramework) ]

    candidates
    |> List.tryFind (fun dir -> File.Exists(Path.Combine(dir, "BjolangRuntime.dll")))
    |> Option.defaultValue (Path.Combine(root, "BjolangRuntime", "bin", "Release", targetFramework))
    |> Path.GetFullPath

/// Absolute paths of the runtime support assemblies, in link order.
let runtimeAssemblies: string list =
    runtimeAssemblyNames |> List.map (fun name -> Path.Combine(runtimeDir, name + ".dll"))
