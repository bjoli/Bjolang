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

/// .NET shared frameworks: `Microsoft.AspNetCore.App` and its like.
///
/// A framework is not a package and not an assembly. It is a directory of
/// assemblies the *host* loads, named in a manifest, and it has to be declared
/// before a package may name a type out of it — which is what most of this file
/// is about.
///
/// THREE SETS, AND KEEPING THEM APART IS THE POINT
///
/// 1. What a module may *name*: only what its own package declares. "Naming" is
///    writing a CLR type in source — `import/class`, `import/extern`, an
///    annotation, a `cast`, a `(:is T x)`. Without this check a package could
///    use `HttpContext` without declaring it, build only in graphs that happen
///    to contain an HTTP library, and break for everyone else.
///
/// 2. What a module is *compiled against*: its package's declared frameworks
///    plus the frameworks recorded by every module it imports. Calling an
///    imported function whose signature mentions a framework type can make the
///    emitted C# mention that type, so the reference has to be there.
///
/// 3. What a module *records*: the frameworks a type was actually resolved
///    from while compiling it, plus its imports' recorded sets. Actually
///    resolved, not declared: a router module inside an HTTP package that never
///    touches ASP.NET records nothing, so a command-line tool importing only
///    the router does not need the ASP.NET runtime installed.
///
/// WHY THE STATE IS MODULE-LEVEL
///
/// The frontend has no options record — `Pipeline.runFullFrontendPipeline`
/// takes a path and nothing else — and the two things that already have to
/// reach it, the roots file and the dependency backend, are module-level state
/// installed by `Program` and `Build`. This follows them rather than threading
/// a new parameter through every pass.
module Bjolang.Frameworks

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable

/// The framework every program already has. It is never declared: listing it is
/// an error, because saying it would suggest that not saying it left it out.
let coreFramework = "Microsoft.NETCore.App"

/// The .NET installation this compiler is running on, found the way
/// `CSharpEmit` and `Build` find it: three levels up from the core library.
let dotnetRoot =
    let coreLib = typeof<obj>.Assembly.Location
    Path.GetFullPath(Path.Combine(Path.GetDirectoryName coreLib, "..", "..", ".."))

/// The version directory the running runtime lives in — `10.0.0`,
/// `10.0.0-preview.7.25380.108`. The first choice for every other framework,
/// so that one .NET installation is used as one thing.
let private runtimeVersionDir =
    Path.GetFileName(Path.GetDirectoryName(typeof<obj>.Assembly.Location))

// ---------------------------------------------------------------------------
// What was declared
// ---------------------------------------------------------------------------

/// Package root directory (absolute, with a trailing separator) and what that
/// package declared. Longest match wins, which is what makes a dependency
/// fetched *under* the root project's directory belong to itself.
let mutable private declaredByRoot: (string * Set<string>) list = []

/// What `--framework` said, for a file no root covers: a single-file build.
let mutable private declaredGlobally: Set<string> = Set.empty

/// The `--frameworks` file, recorded as an input of every build made against
/// it — the same rule the roots file follows, and for the same reason: a
/// declaration removed from a manifest has to make the modules that used it
/// stale, and a file is what a timestamp comparison can see.
let mutable private declarationFile: string option = None

let private withSeparator (dir: string) =
    dir.TrimEnd Path.DirectorySeparatorChar + string Path.DirectorySeparatorChar

/// Installs the `--frameworks` file's contents: one (package directory,
/// framework) pair per line.
let setDeclared (file: string) (entries: (string * string) list) : unit =
    declarationFile <- Some(Path.GetFullPath file)

    declaredByRoot <-
        entries
        |> List.map (fun (dir, name) -> withSeparator (Path.GetFullPath dir), name)
        |> List.groupBy fst
        |> List.map (fun (dir, pairs) -> dir, pairs |> List.map snd |> Set.ofList)
        // Longest first, so that the first match is the innermost package.
        |> List.sortByDescending (fun (dir, _) -> dir.Length)

/// Installs what `--framework` named, which covers every file no line above
/// does.
let setGlobal (names: string list) : unit =
    declaredGlobally <- Set.ofList names

let declarationFilePath () : string option = declarationFile

/// The frameworks the package that owns this source file declared.
let declaredFor (sourceFile: string) : Set<string> =
    if String.IsNullOrEmpty sourceFile then
        declaredGlobally
    else

    let full =
        try
            Path.GetFullPath sourceFile
        with _ ->
            sourceFile

    match declaredByRoot |> List.tryFind (fun (dir, _) -> full.StartsWith(dir, StringComparison.Ordinal)) with
    | Some(_, names) -> names
    | None -> declaredGlobally

/// The declared set a module was last built under, read back from the
/// `framework` lines of its build record. `None` when it has no record.
let private recordedFor (sourceFile: string) : string list option =
    let record = Path.ChangeExtension(sourceFile, ".bjobuild")

    if not (File.Exists record) then
        None
    else
        try
            File.ReadAllLines record
            |> Array.choose (fun line ->
                if line.StartsWith("framework ", StringComparison.Ordinal) then
                    Some(line.Substring("framework ".Length).Trim())
                else
                    None)
            |> Array.toList
            |> List.sort
            |> Some
        with _ ->
            None

/// Has this module's package changed what it declares since the module was
/// built?
///
/// A timestamp cannot answer this. Removing `(frameworks ...)` from a manifest
/// *deletes* the declarations file, and a file that is not there is not newer
/// than anything — while the module it covered has to be rebuilt, so that it
/// fails with the naming error instead of quietly staying built against a
/// framework it may no longer name.
///
/// `false` where there is no record: then the timestamps decide, as they did
/// before any of this existed.
let declarationsChanged (sourceFile: string) : bool =
    match recordedFor sourceFile with
    | None -> false
    | Some recorded -> recorded <> (declaredFor sourceFile |> Set.toList |> List.sort)

/// Every framework any package in this build declared, sorted. What the build
/// record writes down, so that a changed declaration is visible in it.
let declaredHere () : string list =
    declaredByRoot
    |> List.collect (fun (_, names) -> Set.toList names)
    |> List.append (Set.toList declaredGlobally)
    |> List.distinct
    |> List.sort

// ---------------------------------------------------------------------------
// Where a framework is
// ---------------------------------------------------------------------------

/// Version directory names, compared as versions rather than as text: `10.0.10`
/// is above `10.0.9`, and a release is above the previews leading to it.
let private versionKey (name: string) : int list * string =
    let core, tag =
        match name.IndexOf '-' with
        | -1 -> name, ""
        | i -> name.Substring(0, i), name.Substring(i + 1)

    let numbers =
        core.Split('.')
        |> Array.toList
        |> List.map (fun part ->
            match Int32.TryParse part with
            | true, n -> n
            | _ -> 0)

    // A release sorts above every prerelease of the same numbers, which is what
    // "" > "preview..." has to mean and is the opposite of a text comparison.
    numbers, (if tag = "" then "~" else tag)

let private sameMajorMinor (a: string) (b: string) =
    let numbers (s: string) = fst (versionKey s) |> List.truncate 2
    numbers a = numbers b

/// The version directory to use under `root`: the running runtime's own if it
/// is there, otherwise the highest with the same major.minor.
let private pickVersion (root: string) : string option =
    if not (Directory.Exists root) then
        None
    else

    let present =
        Directory.GetDirectories root
        |> Array.map (fun d -> Path.GetFileName(d.TrimEnd Path.DirectorySeparatorChar))

    if present |> Array.contains runtimeVersionDir then
        Some runtimeVersionDir
    else
        present
        |> Array.filter (sameMajorMinor runtimeVersionDir)
        |> Array.sortBy versionKey
        |> Array.tryLast

/// The implementation assemblies, which is what compile-time reflection reads:
/// `<dotnet root>/shared/<framework>/<version>`.
let implementationDir (name: string) : string option =
    let root = Path.Combine(dotnetRoot, "shared", name)
    pickVersion root |> Option.map (fun v -> Path.Combine(root, v))

/// The reference assemblies, which is what the C# compile is given:
/// `<dotnet root>/packs/<framework>.Ref/<version>/ref/<tfm>`.
let referenceDir (name: string) : string option =
    let root = Path.Combine(dotnetRoot, "packs", name + ".Ref")

    pickVersion root
    |> Option.bind (fun v ->
        let refRoot = Path.Combine(root, v, "ref")

        if Directory.Exists refRoot then
            Directory.GetDirectories refRoot |> Array.sort |> Array.tryLast
        else
            None)

/// The shared frameworks this machine has, for a diagnostic that has to say
/// what was available instead.
let installed () : string list =
    let shared = Path.Combine(dotnetRoot, "shared")

    if Directory.Exists shared then
        Directory.GetDirectories shared
        |> Array.map (fun d -> Path.GetFileName(d.TrimEnd Path.DirectorySeparatorChar))
        |> Array.toList
        |> List.sort
    else
        []

/// Both directories of a declared framework, or a diagnostic naming what was
/// looked for and what is there. `Microsoft.WindowsDesktop.App` on Linux is the
/// ordinary way to reach this, and so is a typo.
let checkAvailable (name: string) : unit =
    if name = coreFramework then
        failwithf
            $"Error: %s{coreFramework} is the framework every program already has, so it is not declared. Remove it from the manifest."

    let shown = installed () |> String.concat ", "
    let sharedPath = Path.Combine(dotnetRoot, "shared", name)
    let packPath = Path.Combine(dotnetRoot, "packs", name + ".Ref")

    match implementationDir name with
    | None ->
        failwithf
            $"Error: the shared framework '%s{name}' is not installed. Looked in %s{sharedPath}.\n  Installed here: %s{shown}."
    | Some _ ->
        match referenceDir name with
        | None ->
            failwithf
                $"Error: the shared framework '%s{name}' is installed, but its reference pack is not, so nothing can be compiled against it. Looked in %s{packPath}.\n  Installing the matching SDK, or the targeting pack, is what puts it there."
        | Some _ -> ()

// ---------------------------------------------------------------------------
// Finding a type inside a framework
// ---------------------------------------------------------------------------
//
// Namespace-based guessing does not work here. `Microsoft.AspNetCore.Http` is
// three assemblies — `HttpContext` is in `Microsoft.AspNetCore.Http.Abstractions`
// — so the only reliable way to find the file a type is in is to read the type
// names out of every assembly in the directory. That is done with
// `System.Reflection.Metadata`, which reads the tables without loading
// anything, and the result is cached for the life of the process.

/// Full type name -> the file that defines it, per framework.
let private indexes = Dictionary<string, Dictionary<string, string>>()

/// Assembly file -> the framework it came from. What the naming check consults:
/// asking whether the assembly is "already loaded" would answer differently
/// depending on what else this process compiled first.
let private frameworkOfFile =
    Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

/// Where a simple assembly name can be found, for the resolver below. A
/// framework assembly reflected over pulls its neighbours in, and the compiler's
/// own probing paths know nothing about them.
let private fileOfAssemblyName =
    Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

let mutable private resolverInstalled = false

let private installResolver () =
    if not resolverInstalled then
        resolverInstalled <- true

        AppDomain.CurrentDomain.add_AssemblyResolve (fun _ args ->
            let simple = AssemblyName(args.Name).Name

            match fileOfAssemblyName.TryGetValue simple with
            | true, path ->
                try
                    Assembly.LoadFrom path
                with _ ->
                    null
            | _ -> null)

/// Reads the public top-level type names out of one assembly file, without
/// loading it.
let private typesIn (file: string) : string seq =
    seq {
        use stream = File.OpenRead file
        use reader = new PEReader(stream)

        if reader.HasMetadata then
            let md = reader.GetMetadataReader()

            for handle in md.TypeDefinitions do
                let definition = md.GetTypeDefinition handle

                // Nested types are reached through their declaring type, and a
                // non-public one cannot be named from another assembly at all.
                if definition.GetDeclaringType().IsNil
                   && definition.Attributes &&& TypeAttributes.VisibilityMask = TypeAttributes.Public then
                    let ns = md.GetString definition.Namespace
                    let name = md.GetString definition.Name
                    yield (if ns = "" then name else ns + "." + name)
    }

let private indexOf (framework: string) : Dictionary<string, string> =
    match indexes.TryGetValue framework with
    | true, index -> index
    | _ ->
        let index = Dictionary<string, string>(StringComparer.Ordinal)

        match implementationDir framework with
        | None -> ()
        | Some dir ->
            // A full type name is not unique within a framework. In
            // `Microsoft.AspNetCore.App` four of them are defined twice, and
            // one is `Microsoft.Extensions.Logging.LoggingBuilderExtensions`:
            // `Microsoft.Extensions.Logging.dll` has `ClearProviders` on it and
            // `Microsoft.Extensions.Logging.Configuration.dll` has
            // `AddConfiguration`. Whichever of the two the index kept decided
            // which members existed — and it kept whichever the *directory
            // listing* happened to hand over first, which is not an order
            // anything promises.
            //
            // So: shortest assembly name first, ties broken alphabetically. In
            // every one of the four pairs the shorter name is the assembly the
            // type is really at home in and the longer one is an add-on beside
            // it. That is a heuristic, and the reason it is an acceptable one
            // is that the alternative is not "right" but "different on a
            // different machine".
            let files =
                Directory.GetFiles(dir, "*.dll")
                |> Array.sortBy (fun f ->
                    let name = Path.GetFileNameWithoutExtension f
                    name.Length, name)

            Timing.phase $"index the %s{framework} framework" (fun () ->
                for file in files do
                    let simple = Path.GetFileNameWithoutExtension file

                    if not (fileOfAssemblyName.ContainsKey simple) then
                        fileOfAssemblyName[simple] <- file

                    try
                        for typeName in typesIn file do
                            if not (index.ContainsKey typeName) then
                                index[typeName] <- file
                    with _ ->
                        // A native image or a file that is not managed at all.
                        ())

        indexes[framework] <- index
        index

/// The file defining `fullName` in one of `frameworks`, and which framework
/// that was. Both are needed: the file to load, the framework to record.
let tryLocate (frameworks: Set<string>) (fullName: string) : (string * string) option =
    installResolver ()

    frameworks
    |> Set.toList
    |> List.sort
    |> List.tryPick (fun framework ->
        match (indexOf framework).TryGetValue fullName with
        | true, file ->
            frameworkOfFile[file] <- framework
            Some(file, framework)
        | _ -> None)

/// Which framework an assembly belongs to, given where it was loaded from.
let frameworkOfAssemblyFile (location: string) : string option =
    if String.IsNullOrEmpty location then
        None
    else
        match frameworkOfFile.TryGetValue location with
        | true, framework -> Some framework
        | _ ->
            // Loaded before anything asked — the shared directory it sits in
            // names the framework, which is the one thing a path here is good
            // for.
            let dir = Path.GetDirectoryName location

            if isNull dir then
                None
            else
                let sharedRoot = withSeparator (Path.Combine(dotnetRoot, "shared"))
                let full = withSeparator (Path.GetFullPath dir)

                if full.StartsWith(sharedRoot, StringComparison.Ordinal) then
                    let rest = full.Substring(sharedRoot.Length).Split(Path.DirectorySeparatorChar)

                    if rest.Length > 0 && rest[0] <> coreFramework && rest[0] <> "" then
                        Some rest[0]
                    else
                        None
                else
                    None

/// Does an installed but undeclared framework hold assemblies whose names start
/// the way this type's namespace does?
///
/// `Microsoft.AspNetCore.Http.HttpContext` against `Microsoft.AspNetCore.*.dll`
/// in `Microsoft.AspNetCore.App`. Two segments, because one is `Microsoft` and
/// is true of half the platform.
let hintFor (declared: Set<string>) (fullName: string) : string option =
    let parts = fullName.Split '.'

    if parts.Length < 3 then
        None
    else

    let prefix = parts[0] + "." + parts[1]

    installed ()
    |> List.filter (fun framework -> framework <> coreFramework && not (Set.contains framework declared))
    |> List.tryPick (fun framework ->
        match implementationDir framework with
        | None -> None
        | Some dir ->
            let matches =
                Directory.GetFiles(dir, "*.dll")
                |> Array.exists (fun file -> Path.GetFileNameWithoutExtension(file).StartsWith(prefix, StringComparison.Ordinal))

            if matches then Some(framework, prefix) else None)
    |> Option.map (fun (framework, prefix) ->
        $"\n  hint: %s{prefix} types live in the shared framework %s{framework}. Declare it — (frameworks \"%s{framework}\") — in the manifest of the package that names one.")

// ---------------------------------------------------------------------------
// What this compilation used
// ---------------------------------------------------------------------------

/// The source file being compiled, which says which package's declarations
/// apply to a lookup that has no range of its own.
let mutable private currentFile: string = ""

/// Frameworks a type was actually resolved from, plus what the imports
/// recorded. This is what goes into the metadata and into the runtimeconfig.
let mutable private used: Set<string> = Set.empty

/// What the imports were built against, which this module is compiled against
/// too whether or not it names anything out of them.
let mutable private imported: Set<string> = Set.empty

/// Everything about the module being compiled, for `Session` to put back.
///
/// A dependency compiled on the way to something else is a whole compilation
/// of its own, with its own package and its own declarations — and it happens
/// *inside* the outer one, which has to find its own answers again afterwards.
type State =
    { File: string
      Used: Set<string>
      Imported: Set<string> }

let emptyState: State =
    { File = ""
      Used = Set.empty
      Imported = Set.empty }

let snapshot () : State =
    { File = currentFile
      Used = used
      Imported = imported }

let restore (state: State) : unit =
    currentFile <- state.File
    used <- state.Used
    imported <- state.Imported

let startModule (file: string) : unit =
    currentFile <- (if isNull file then "" else file)
    used <- Set.empty
    imported <- Set.empty

let currentSourceFile () : string = currentFile

/// The frameworks the module now being compiled may name.
let declaredForCurrent () : Set<string> = declaredFor currentFile

/// What this module may resolve types from: what its package declared, and
/// what its imports were built against.
let compileAgainst () : Set<string> = Set.union (declaredForCurrent ()) imported

/// A type was resolved out of a framework, so this module depends on it at run
/// time.
let noteUsed (framework: string) : unit = used <- Set.add framework used

/// An imported module says it uses these.
let noteImported (frameworks: string list) : unit =
    for framework in frameworks do
        imported <- Set.add framework imported
        used <- Set.add framework used

/// What this module records: sorted, so that metadata and build records do not
/// differ by the order two lookups happened in.
let usedHere () : string list = used |> Set.toList |> List.sort
