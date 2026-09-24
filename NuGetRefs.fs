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

/// The NuGet package assemblies a build uses, from `--nuget <dir>`.
///
/// `bjo` restores the packages with the .NET SDK and leaves three lists in that
/// directory:
///
/// - `compile.txt`: what the C# compile references. These can be reference
///   assemblies, which cannot be loaded, so the type checker does not use them.
/// - `runtime.txt`: the implementation assemblies. The type checker loads these,
///   and the built program's assembly resolver maps each one's assembly name to
///   its path.
/// - `native.txt`: runtime-specific assets for every runtime identifier the
///   packages have files for, as `package TAB asset-type TAB rid TAB path`. The
///   ones for this machine are picked here: native libraries go to the built
///   program's unmanaged resolver, and a managed `runtime` asset replaces the
///   package's portable assembly of the same name.
///
/// The files are build inputs. `bjo` rewrites them only when the restore result
/// changes, so their timestamps are what makes a module stale when the packages
/// change.
module Bjolang.NuGetRefs

open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Text.RegularExpressions

type private Resolved =
    { Dir: string
      Compile: string list
      /// (assembly name, path), one per runtime assembly.
      Runtime: (string * string) list
      /// (library key, path), one per native library for this machine.
      Native: (string * string) list }

let mutable private resolved: Resolved option = None

let private compileList (dir: string) = Path.Combine(dir, "compile.txt")
let private runtimeList (dir: string) = Path.Combine(dir, "runtime.txt")
let private nativeList (dir: string) = Path.Combine(dir, "native.txt")

/// The runtime identifiers whose assets this machine can use, best first:
/// the RID .NET reports, then the portable ones NuGet packages are published
/// for. A distribution's own build of .NET reports e.g. `fedora.43-x64`,
/// which no package ships files for.
let ridChain () : string list =
    let arch =
        match RuntimeInformation.ProcessArchitecture with
        | Architecture.X64 -> "x64"
        | Architecture.X86 -> "x86"
        | Architecture.Arm64 -> "arm64"
        | Architecture.Arm -> "arm"
        | other -> other.ToString().ToLowerInvariant()

    let os =
        if OperatingSystem.IsWindows() then "win"
        elif OperatingSystem.IsMacOS() then "osx"
        elif OperatingSystem.IsLinux() then "linux"
        elif OperatingSystem.IsFreeBSD() then "freebsd"
        else "unix"

    let exact = RuntimeInformation.RuntimeIdentifier
    let musl = exact.Contains "musl" || exact.StartsWith "alpine"

    [ exact
      if musl then
          $"linux-musl-%s{arch}"
          "linux-musl"
      $"%s{os}-%s{arch}"
      os
      if os <> "win" then "unix"
      "any" ]
    |> List.distinct

/// What a native library is looked up by: its file name without `lib` and
/// without `.dll`, `.dylib` or `.so[.N...]`. A `DllImport` may name the
/// library any of those ways, so both sides are reduced to this. The built
/// program's resolver does the same in C# — see `Build.fs`.
let nativeKey (name: string) : string =
    let file = Regex.Replace(Path.GetFileName name, @"(\.dll|\.dylib|\.so(\.[0-9]+)*)$", "")
    if file.StartsWith("lib", StringComparison.Ordinal) then file.Substring 3 else file

type private Asset =
    { Package: string
      Kind: string
      Rid: string
      Path: string }

/// The runtime-specific assets for this machine. For each package and kind of
/// asset, the RID nearest the front of `ridChain` that the package has files
/// for — as NuGet picks them for a RID-specific build. An asset with no RID
/// applies everywhere.
let private selectAssets (assets: Asset list) : Asset list =
    let chain = ridChain ()

    assets
    |> List.groupBy (fun a -> a.Package, a.Kind)
    |> List.collect (fun (_, group) ->
        let everywhere = group |> List.filter (fun a -> a.Rid = "")

        let specific =
            chain
            |> List.tryPick (fun rid ->
                match group |> List.filter (fun a -> a.Rid = rid) with
                | [] -> None
                | found -> Some found)
            |> Option.defaultValue []

        everywhere @ specific)

let private readAssets (file: string) : Asset list =
    if not (File.Exists file) then
        []
    else
        File.ReadAllLines file
        |> Array.filter (fun line -> line.Trim() <> "")
        |> Array.toList
        |> List.map (fun line ->
            match line.TrimEnd('\r', '\n').Split('\t') with
            | [| package; kind; rid; path |] ->
                { Package = package
                  Kind = kind
                  Rid = rid
                  Path = Path.GetFullPath path }
            | _ -> failwithf $"--nuget: '%s{file}' has a line that is not package, asset type, RID and path: %s{line}")

/// The paths in one list file. A path that has gone away means the NuGet cache
/// was cleared since the restore, which a new restore fixes.
let private readList (file: string) : string list =
    if not (File.Exists file) then
        failwithf $"--nuget: there is no '%s{file}'. Run 'bjo build' to restore the packages."

    File.ReadAllLines file
    |> Array.map (fun line -> line.Trim())
    |> Array.filter (fun line -> line <> "")
    |> Array.toList
    |> List.map (fun path ->
        if not (File.Exists path) then
            failwithf
                $"--nuget: '%s{file}' names '%s{path}', which is not there. The NuGet cache may have been cleared; run 'bjo build' to restore the packages again."

        Path.GetFullPath path)

/// Installs the lists in `dir`.
///
/// Two runtime assemblies with one assembly name would make the program's
/// resolver pick one by list order. MSBuild's conflict resolution is supposed
/// to have removed one of them, so this is refused rather than resolved.
let load (dir: string) : unit =
    let full = Path.GetFullPath dir
    let compile = readList (compileList full)
    let assets = readAssets (nativeList full) |> selectAssets

    for a in assets do
        if not (File.Exists a.Path) then
            failwithf
                $"--nuget: '%s{nativeList full}' names '%s{a.Path}', which is not there. The NuGet cache may have been cleared; run 'bjo build' to restore the packages again."

    // A package's managed assembly for this RID replaces its portable one.
    let specific =
        assets
        |> List.filter (fun a -> a.Kind = "runtime" && a.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        |> List.map (fun a -> AssemblyName.GetAssemblyName(a.Path).Name, a.Path)

    let runtime =
        readList (runtimeList full)
        |> List.map (fun path -> AssemblyName.GetAssemblyName(path).Name, path)
        |> List.filter (fun (name, _) -> not (specific |> List.exists (fun (n, _) -> n = name)))
        |> fun portable -> portable @ specific

    let native =
        assets
        |> List.filter (fun a -> a.Kind = "native")
        |> List.map (fun a -> nativeKey a.Path, a.Path)

    runtime
    |> List.groupBy fst
    |> List.iter (fun (name, group) ->
        if group.Length > 1 then
            let paths = group |> List.map snd |> String.concat "\n  "

            failwithf
                $"--nuget: the assembly %s{name} was resolved from more than one package file:\n  %s{paths}\nThis is a bug in the package resolution, not in the program.")

    resolved <-
        Some
            { Dir = full
              Compile = compile
              Runtime = runtime
              Native = native }

/// The `--nuget` directory, as given.
let directory () : string option = resolved |> Option.map (fun r -> r.Dir)

let compileReferences () : string list =
    match resolved with
    | Some r -> r.Compile
    | None -> []

let runtimeAssemblies () : string list =
    match resolved with
    | Some r -> r.Runtime |> List.map snd
    | None -> []

/// (assembly name, path) for every runtime assembly.
let runtimeByName () : (string * string) list =
    match resolved with
    | Some r -> r.Runtime
    | None -> []

/// (library key, path) for every native library this machine uses. The key is
/// `nativeKey` of the file.
let nativeLibraries () : (string * string) list =
    match resolved with
    | Some r -> r.Native
    | None -> []

let mutable private nativeResolverInstalled = false

/// Lets this process load the packages' native libraries: the REPL runs the
/// code it compiles here. A built program installs its own, in its entry point.
let installNativeResolver () : unit =
    if not nativeResolverInstalled && not (nativeLibraries ()).IsEmpty then
        nativeResolverInstalled <- true

        System.Runtime.Loader.AssemblyLoadContext.Default.add_ResolvingUnmanagedDll (fun _ name ->
            match nativeLibraries () |> List.tryFind (fun (key, _) -> key = nativeKey name) with
            | Some(_, path) -> NativeLibrary.Load path
            | None -> IntPtr.Zero)

/// The list files, which are inputs of every build that used them.
let listFiles () : string list =
    match resolved with
    | Some r ->
        [ compileList r.Dir; runtimeList r.Dir ]
        @ (if File.Exists(nativeList r.Dir) then [ nativeList r.Dir ] else [])
    | None -> []

/// Do the packages count as an input of this module?
///
/// Not for the standard library. It is built once and shared by every program,
/// so if a project's packages made it stale, building a project with packages
/// and then one without would rebuild it each time.
let appliesTo (sourceFile: string) : bool =
    match Paths.identityOf sourceFile with
    | Some identity -> not identity.Root.IsStandardLibrary
    | None -> true

/// Has the `--nuget` directory come or gone since this module was built?
///
/// The list files' timestamps cover a change in what was restored. They cannot
/// cover the packages being removed from the manifest: then there is no
/// directory to compare with, and the module has to be rebuilt without them.
let changedSince (sourceFile: string) : bool =
    let record = Path.ChangeExtension(sourceFile, ".bjobuild")

    if not (appliesTo sourceFile) || not (File.Exists record) then
        false
    else
        try
            let recorded =
                File.ReadAllLines record
                |> Array.tryPick (fun line ->
                    if line.StartsWith("nuget ", StringComparison.Ordinal) then
                        Some(line.Substring("nuget ".Length).Trim())
                    else
                        None)

            recorded <> directory ()
        with _ ->
            false
