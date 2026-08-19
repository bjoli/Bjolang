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

/// The assemblies every compiled program links against, in link order.
///
/// `Bjoml` is the concurrency runtime — fibers, promises, channels and the CML
/// event combinators that bjoroutines are built on. It is listed here rather
/// than pulled in on demand because the resolver a compiled program installs
/// probes exactly these directories, and because the compiler reflects on it at
/// type-check time to resolve `import/class` and `import/extern` against it.
///
/// `Microsoft.Extensions.ObjectPool` is Bjoml's own dependency. It is not named
/// here: MSBuild copies it next to `Bjoml.dll`, which is already a probe
/// directory, so the resolver finds it there.
/// The three collection assemblies carry a `Bjo` prefix that their namespaces
/// do not: `Set.Set<T>` lives in `BjoSet.dll`. Assembly names are matched
/// without regard to case, so an assembly called `Set` and the `(std set)`
/// module's own `set.dll` are one name to the loader — and the standard library
/// module is named after what it publishes, not after where it happens to come
/// from.
let private runtimeAssemblyNames =
    [ "BjolangRuntime"
      "Collections"
      "SchemeList"
      "Map"
      "BjoSet"
      "BjoOrderedSet"
      "BjoOrderedMap"
      "Bjoml" ]

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
