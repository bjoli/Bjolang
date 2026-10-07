# Task: NuGet package references for `bjo build` (first slice)

## Goal

A Bjolang program can declare NuGet packages in its manifest and then use them
through `import/extern`: the type checker sees them, Roslyn links them, and the
built program loads them at runtime.

It is done when this program builds and runs. It needs no database server:

```scheme
(import/extern
  (csb-new      (: Npgsql.NpgsqlConnectionStringBuilder (-> string Builder) #:new))
  (csb-database (: Npgsql.NpgsqlConnectionStringBuilder.Database (-> Builder string) #:get)))

(defun (main args)
  (println (csb-database (csb-new "Host=localhost;Database=shop")))  ;; prints "shop"
  0)
```

(Use whatever constructor and type-alias syntax `import/extern` actually has.
The point is: a type from the Npgsql package, used from Bjolang.)

## Decisions already made (do not revisit)

1. **Versions mean what NuGet means.** Pass the declared version string through to
   `PackageReference` unchanged. So `8.0.3` means "8.0.3 or newer" (NuGet picks the
   lowest version that is available), `[8.0.3]` means exactly that version, and
   ranges and floating versions work as NuGet defines them. `bjo` never interprets
   versions itself.
2. **Use a NuGet lock file.** It lives at the project root as `packages.lock.json`,
   next to the manifest, and it is meant to be committed.
3. **Generated files live in `.bjo/nuget/`.** They are owned by `bjo`, are not
   committed, and are never edited by users.

## Scope

In scope:
- **A new manifest field.** Add a `packages` field in the ROOT manifest (the project
  `bjo build` runs in), next to the existing `frameworks` field and in the
  manifest's existing syntax. Each entry is a package id and a version string.
- **Restore and resolve.** Resolve the packages with the .NET SDK, and feed the
  results into the type checker, the C# compile, and the runtime assembly resolver.
- **Lock file and locked mode.** Support the lock file, plus a `--locked` flag on
  `bjo build` for CI.

Out of scope for now. Each of these must fail with a clear error, never be
silently ignored:
- **Packages declared by dependencies.** If any dependency's manifest has a
  `packages` field, fail with an error that names the dependency: packages in
  dependencies are not supported yet.
- **Native or runtime-specific assets.** If the resolved closure contains any,
  fail with an error that names the packages: native assets are not supported yet.
  Examples are Microsoft.Data.Sqlite and Microsoft.Data.SqlClient.

Not in scope and not needing an error:
- bundled C# projects
- `bjo publish`
- replacing `Assembly.LoadFrom` in the type checker

## Design

### Why MSBuild and not `project.assets.json`

Do not parse `project.assets.json` yourself. Resolving package assets correctly
means reimplementing parts of the SDK:
- conflicts with shared frameworks (for example, Microsoft.AspNetCore.App already
  contains Microsoft.Extensions.Logging.Abstractions, which Npgsql also brings as a
  package)
- `_._` placeholder entries
- the difference between compile assets and runtime assets
- runtime-specific assets later

Instead, let MSBuild run the SDK's own resolution, and have a small custom target
write out the results.

### The generated project

Write `.bjo/nuget/root/Root.csproj`. The `root/` subfolder leaves room for one
generated project per package later. The project contains:

- `TargetFramework` set to the TFM the compiler already uses (currently
  `net10.0`). Take it from the same place as the rest of `Build.fs` instead of
  hard-coding a second copy.
- `OutputType` Library, and `CopyLocalLockFileAssemblies` set to true, so that
  package runtime assets reach `ReferenceCopyLocalPaths`.
- `RestorePackagesWithLockFile` set to true, and `NuGetLockFilePath` set to the
  absolute path of `<project root>/packages.lock.json`.
- One `PackageReference` per manifest entry, with the version string verbatim.
- One `FrameworkReference` per entry in the manifest's `frameworks` field (for
  example `Microsoft.AspNetCore.App`), so that the SDK's conflict resolution
  knows what the framework already provides.
- No source files.
- A custom target, for example `BjoResolve`, that depends on `ResolveReferences`
  and writes three files into `.bjo/nuget/root/`:
  - **`compile.txt`:** full paths of the compile-time references that come from
    packages. These are the `ReferencePath` items that carry `NuGetPackageId`
    metadata. Framework reference assemblies must not be included.
  - **`runtime.txt`:** full paths of the package runtime `.dll` files. These are
    the `ReferenceCopyLocalPaths` items with extension `.dll` and no
    `DestinationSubDirectory`. That excludes `.pdb`, `.xml` and satellite
    resource assemblies.
  - **`native.txt`:** everything in `NativeCopyLocalItems` and
    `RuntimeTargetsCopyLocalItems`, each with its `NuGetPackageId`.

**Verify every item and metadata name above** against the installed SDK's
`Microsoft.PackageDependencyResolution.targets` and
`Microsoft.NET.ConflictResolution.targets` before relying on them. Adjust them if
they differ, and say so in the report. The names are my best knowledge, not a
spec.

### Invocation

Run `dotnet msbuild .bjo/nuget/root/Root.csproj -restore -t:BjoResolve -nologo`
with these global properties:

- **`ImportDirectoryBuildProps=false`, `ImportDirectoryBuildTargets=false` and
  `ImportDirectoryPackagesProps=false`.** A user's project is often inside a
  repository that has a `Directory.Build.props` or a `Directory.Packages.props`.
  Those files must not leak into the generated project: they could change the
  target framework, turn on central package management (which rejects
  `Version=` on `PackageReference`), or add analyzers.
- **`RestoreLockedMode=true`,** only when `bjo build --locked` is used.

Also set `DOTNET_NOLOGO=1` in the environment. Keep honoring the user's
`NuGet.config` (NuGet finds it by walking up from the generated project, which
reaches the project root) and `global.json`.

Capture stdout and stderr, and show them only on failure.

### Skipping work

If the root manifest has no packages, do nothing: no `dotnet` process, and no
requirement that `dotnet` is installed. Pure Bjolang builds must be exactly as
fast as today.

Otherwise, keep a stamp file in `.bjo/nuget/root/`. It holds a hash of:
- the package list with versions
- the frameworks list
- the TFM
- the contents of `packages.lock.json`

Skip the `dotnet` call when all of these hold:
- the hash is unchanged
- `compile.txt` and `runtime.txt` exist
- every path listed in them still exists (a user may have cleared the NuGet cache)

A warm build must not start a `dotnet` process.

### Using the results

- **Type checker.** Call `DotNetInterop.registerAssemblyFile` with every path in
  `runtime.txt`, before type checking starts. Use runtime assets here, not compile
  assets: `registerAssemblyFile` uses `Assembly.LoadFrom`, which refuses reference
  assemblies.
- **C# compile.** Add every path in `compile.txt` as a reference, in BOTH backends
  in `Build.fs`: the in-process Roslyn path, and the MSBuild fallback that writes
  its own csproj with `<Reference><HintPath>`.
- **Runtime.** Add every path in `runtime.txt` to the name-to-path map of the
  assembly resolver generated into the executable (`InstallAssemblyResolver` in
  `Build.fs`). Key it by the assembly name read from metadata
  (`AssemblyName.GetAssemblyName(path).Name`), not by the file name. If two paths
  give the same assembly name, that is a bug in the resolution step: fail loudly.
- **Unchanged.** Do not change `runtimeconfig.json` or `deps.json` handling beyond
  what `frameworks` already does.

## Errors

Each error message names the cause in plain words. Where it comes from NuGet,
include NuGet's code and message verbatim, plus a one-line hint.

| Situation | Behaviour |
|---|---|
| `dotnet` not found, and packages are declared | Say that NuGet packages need the .NET SDK's `dotnet` command. |
| NU1101 or NU1102 (package or version not found) | Show NuGet's message; hint to check the name and version and the NuGet sources. |
| NU1301 (source unreachable) | Hint: the first restore needs network access or a local feed. |
| NU1004 (lock file out of date) under `--locked` | Hint: run `bjo build` without `--locked` to update `packages.lock.json`. |
| Any other restore or MSBuild failure | Show the captured output. |
| `native.txt` is not empty | Name the packages; native assets are not supported yet. |
| A dependency declares packages | Name the dependency; not supported yet. |
| The same package id twice in the root manifest | Error. |

## Tests

Use a local folder feed for the core tests, so they run offline and give the
same result every time. Build tiny test packages with `dotnet pack` during test
setup, and point a `NuGet.config` in the test project at the folder.

1. **No packages.** No `dotnet` process is started. For example, the build succeeds
   with `dotnet` removed from `PATH`.
2. **A local test package.** Its type is usable from Bjolang, and the program runs,
   also when started from a different working directory.
3. **The lock file.** `packages.lock.json` is created at the project root, and a
   second, unchanged build starts no `dotnet` process.
4. **`--locked`.** It fails with NU1004 after a version in the manifest changes.
5. **An unknown package.** NU1101 is shown with the hint.
6. **Native assets.** A local test package with a `runtimes/<rid>/native/` file
   produces the native-assets error.
7. **A dependency with packages.** It produces the dependency error.
8. **`Directory.Packages.props` isolation.** A `Directory.Packages.props` in a
   parent directory, with `ManagePackageVersionsCentrally` set to true, does NOT
   break the build.
9. **Online, opt-in.** The Npgsql program from the Goal section.
10. **Online, opt-in: framework conflict.** With `frameworks` set to include
    Microsoft.AspNetCore.App together with Npgsql, the package copy of
    Microsoft.Extensions.Logging.Abstractions is NOT in `runtime.txt`, because the
    framework's copy wins. Without the framework, it IS in `runtime.txt`.

## Docs

Document these where the manifest fields are documented:
- the `packages` field
- what versions mean (NuGet's rules, with a link)
- the lock file, and that it should be committed
- `--locked`
- that `.bjo/` should not be committed (add it to the `.gitignore` if `bjo init`
  writes one)

Also list the current limitations:
- The built executable references DLLs in the NuGet cache, so it runs only on the
  machine that built it, until `bjo publish` exists.
- No native assets.
- Dependencies cannot declare packages yet.
- The type checker loads package assemblies into the compiler process.

## Keep the door open (do not implement)

- **Packages in dependencies.** The plan is one generated project per Bjolang
  package under `.bjo/nuget/<package>/`. `ProjectReference`s will mirror the
  Bjolang dependency graph, so NuGet itself resolves versions across packages.
  Nothing in this slice should assume a single generated project forever.
- **Bundled C# projects.** These become extra `ProjectReference`s in the same
  graph.
- **The type checker's loading.** The type checker should eventually use
  `MetadataLoadContext` instead of `Assembly.LoadFrom`. If a package assembly
  clashes with one the compiler process already loaded (same name, different
  version), do not work around it: report it.

## Report back

- the MSBuild item and metadata names you actually used, and the SDK version you
  checked them against
- cold and warm timings for `bjo build` on the Npgsql example
- anything in this prompt that was wrong, impossible or ambiguous, and what you
  did instead
