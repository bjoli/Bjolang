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

/// Where the compiler looks for things that are installed rather than written
/// by the user: the standard library and the runtime support assemblies.
///
/// Every one of these paths used to be resolved against the process's working
/// directory, which meant the answer changed depending on where the user
/// happened to stand when invoking the compiler. That is how a second copy of
/// the standard library comes into existence: from a directory without a `lib`
/// next to it, `(import (std prelude))` finds no `std/prelude.dll`, falls back
/// to `std/prelude.bjo`, and compiles the whole standard library into the
/// program all over again. There is exactly one installation, so these are
/// resolved once, against the compiler itself.
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

/// Candidate `lib` directories, most authoritative first: an explicit override,
/// then the tree the compiler binary lives in (`<root>/bin/<config>/<tfm>`),
/// then the working directory, which keeps `dotnet run` from the project root
/// working as before.
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

/// The one directory that holds the standard library. A candidate only counts
/// if it actually contains `std`, so an empty `lib` somewhere up the tree does
/// not shadow the real one.
let libDir: string =
    libCandidates ()
    |> Seq.tryFind (fun lib -> Directory.Exists(Path.Combine(lib, "std")))
    |> Option.defaultWith (fun () -> Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "lib")))
    |> Path.GetFullPath

/// The installation root: the directory `lib` sits in.
let root: string = Path.GetFullPath(Path.Combine(libDir, ".."))

// ---------------------------------------------------------------------------
// Packages
// ---------------------------------------------------------------------------

/// A package: one name, one directory, and every module under it.
///
/// A package owns exactly the modules under its own name — `(bjorsec)` provides
/// `(bjorsec core)` and `(bjorsec combine parsers)` and nothing else — so a
/// module path says which package answers it before it says which file. That is
/// what lets a fetched dependency live anywhere: the name is stable, the
/// directory is not.
type PackageRoot =
    { /// As an import spells it: `[ "std" ]`, `[ "bjolang"; "http" ]`.
      Name: string list
      /// Absolute, without a trailing separator.
      Directory: string
      /// Whether this is part of the installed standard library, which is
      /// allowed things user code is not and gets no implicit prelude.
      IsStandardLibrary: bool }

/// A package name as source writes it, for diagnostics.
let showPackageName (name: string list) : string =
    "(" + String.concat " " name + ")"

/// A directory path with one trailing separator, which is what containment has
/// to compare against: `/p/src` is not a prefix of `/p/src2/m.bjo` once both
/// carry it, and is of `/p/src/m.bjo`.
let private asPrefix (dir: string) : string =
    dir.TrimEnd Path.DirectorySeparatorChar + string Path.DirectorySeparatorChar

/// The standard library's packages: one per top-level directory of `lib`.
///
/// Implicit, and present with or without a roots file — a program that names no
/// packages at all still imports `(std prelude)`. This is the one place that
/// knows the standard library is "a `lib` with a `std` in it"; everything else
/// asks whether a file is under a root marked as such.
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

/// The packages a roots file added, set once before anything is resolved.
let mutable private configuredRoots: PackageRoot list = []

/// The file they were read from, kept for the build record.
let mutable private rootsFilePath: string option = None

/// Installs the packages named by `--roots`.
///
/// Called by `Program` before the first compile and never again: every cache
/// keyed by a path — `Naming`'s derived names, `Pipeline`'s module and facts
/// caches — would answer from a stale registry if the roots could change under
/// them, and a compiler process serves one roots file.
let setConfiguredRoots (file: string) (roots: PackageRoot list) : unit =
    rootsFilePath <- Some(Path.GetFullPath file)
    configuredRoots <- roots

/// The roots file this process was given, which every build it makes has to
/// record: a dependency compiled on the way to something else is built against
/// the same packages and needs the same line in its own record.
let rootsFile () : string option = rootsFilePath

/// Every package this build knows, the standard library included.
let packageRoots () : PackageRoot list = standardRoots @ configuredRoots

/// What a module path names.
type ModuleLookup =
    /// The package that owns the name, and the `.bjo` it names — which may not
    /// exist. Whether it does is the caller's question, because the answer
    /// differs for a source module and a prebuilt assembly.
    | ModuleFile of PackageRoot * string
    /// No package is a proper prefix of the name. Carries the package that
    /// would have owned it, which is what the reader has to install or name.
    | NoPackage of string list
    /// The name *is* a package name. A package is a directory, not a module, so
    /// there is nothing to import under this spelling.
    | PackageItself of PackageRoot

/// The package that answers a module path, and the file it points at.
///
/// The longest package name that is a proper prefix wins, so a `(bjolang http)`
/// registered beside `(bjolang)` takes `(bjolang http client)` — which is how a
/// package can be split out of another without every importer being edited.
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
            // The package that would have owned it: everything but the module's
            // own name, which is what a reader has to register. A one-segment
            // path names a package that provides nothing, so it is its own.
            NoPackage(if parts.Length > 1 then parts |> List.take (parts.Length - 1) else parts)

/// The path below a package a module name points at, as the diagnostic spells
/// it: `core.bjo`, `combine/parsers.bjo`.
let moduleFileName (root: PackageRoot) (parts: string list) : string =
    (parts |> List.skip (min root.Name.Length parts.Length) |> String.concat "/") + ".bjo"

/// What a file is called, worked out from where it is.
type FileIdentity =
    { /// The package the file belongs to.
      Root: PackageRoot
      /// The whole module name: the package name, then the path below the
      /// package directory without the extension.
      ModuleName: string list }

/// The package a file belongs to, and the module name that gives it.
///
/// Derived from the path and never from how an import spelled it, so that one
/// file is one module however it was reached — `(import "core.bjo")` and
/// `(import (relocpkg core))` name the same assembly rather than compiling it
/// twice under two identities.
///
/// The *deepest* containing root wins, which is the same rule resolution uses
/// from the other end: a `(bjolang http)` whose directory sits inside
/// `(bjolang)`'s owns the files under it.
///
/// `None` for a file under no package at all — a script somewhere in the
/// filesystem — which keeps its hashed identity.
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

/// The package that would answer a file's own module name, when that is not the
/// package the file is in.
///
/// The one thing that can go wrong with deriving a name from a path: a file at
/// `<(bjolang) root>/http/client.bjo` is named `(bjolang http client)`, and if
/// a package `(bjolang http)` is also registered then that name resolves into
/// *its* directory instead. The file would compile under a name that does not
/// lead back to it, and the module it shadows would be linked by whoever
/// imported the name. Checked wherever a name is derived rather than once at
/// startup, because a root's directory may hold files nobody has looked at yet.
let shadowedBy (identity: FileIdentity) (file: string) : PackageRoot option =
    // Extensions are ignored: this is asked about the `.dll` beside a source as
    // well, and `<root>/core.dll` derives `(pkg core)`, which resolves to
    // `<root>/core.bjo` — the same module, not a shadowing.
    let withoutExtension (p: string) = Path.ChangeExtension(Path.GetFullPath p, null)

    match findModule identity.ModuleName with
    | ModuleFile(owner, resolved) when
        not (String.Equals(withoutExtension resolved, withoutExtension file, StringComparison.Ordinal))
        ->
        Some owner
    | _ -> None

/// The assemblies every compiled program links against, in link order.
///
/// The concurrency runtime — fibers, promises, channels and the CML event
/// combinators that bjoroutines are built on — is compiled into
/// `BjolangRuntime` and is not a separate assembly. It used to be `Bjoml.dll`
/// and is named here in neither form: the namespace is still `Bjoml`, but a
/// namespace is not an assembly, and naming one that no longer exists makes
/// every compiled program fail to resolve.
///
/// They are listed here rather than pulled in on demand because the resolver a
/// compiled program installs probes exactly these directories, and because the
/// compiler reflects on them at type-check time to resolve `import/class` and
/// `import/extern`.
///
/// The three collection assemblies carry a `Bjo` prefix that their namespaces
/// do not: `Set.Set<T>` lives in `BjoSet.dll`.
///
/// Körtidens assemblies heter efter sin fil, modulerna efter sin katalog
/// (`Naming.assemblyName`). `Set` och `(std set)` är därför två namn, inte ett
/// som förr — assemblynamn jämförs utan hänsyn till skiftläge.
let private runtimeAssemblyNames =
    [ "BjolangRuntime"
      "Collections"
      "SchemeList"
      "Map"
      "BjoSet"
      "BjoOrderedSet"
      "BjoOrderedMap" ]

/// Directory holding the runtime support assemblies every compiled program
/// links against.
let runtimeDir: string =
    let candidates =
        [ for dir in ancestorsOf AppContext.BaseDirectory do
              yield Path.Combine(dir, "BjolangRuntime", "bin", "Release", "net10.0")
          yield Path.Combine(root, "BjolangRuntime", "bin", "Release", "net10.0")
          yield Path.Combine(Environment.CurrentDirectory, "BjolangRuntime", "bin", "Release", "net10.0") ]

    candidates
    |> List.tryFind (fun dir -> File.Exists(Path.Combine(dir, "BjolangRuntime.dll")))
    |> Option.defaultValue (Path.Combine(root, "BjolangRuntime", "bin", "Release", "net10.0"))
    |> Path.GetFullPath

/// Absolute paths of the runtime support assemblies, in link order.
let runtimeAssemblies: string list =
    runtimeAssemblyNames |> List.map (fun name -> Path.Combine(runtimeDir, name + ".dll"))
