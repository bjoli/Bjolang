/// Compiling one source file, end to end.
///
/// Lifted out of `Program` so that a compilation is a function rather than a
/// process. `compileDependencyOutOfProcess` used to be the only way to build an
/// imported module, because the compiler's module-level state was shared and
/// unstacked; `Session` makes that state explicit, so the same work can now
/// happen here, and `Program` is left holding the command line and nothing
/// else.
module Bjolang.Build

open Bjolang
open System.IO

/// What a compilation is told, once the command line has been read.
type Options = { IsLibrary: bool; Debug: bool }


/// Runs `fileName args` to completion, with `env` added to its environment,
/// and hands back what it said.
///
/// Output is captured rather than inherited by every caller here: each either
/// passes the child's diagnostics on with context of its own, or is a
/// sub-compilation whose errors name another file entirely.
let private runProcess (fileName: string) (args: string) (env: (string * string) list) : int * string * string =
    let psi = System.Diagnostics.ProcessStartInfo(fileName, args)
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true

    for (key, value) in env do
        psi.Environment[key] <- value

    let p = System.Diagnostics.Process.Start psi
    // Drained before waiting. A child that fills a pipe buffer blocks on the
    // write, and a parent waiting on exit never empties it — which is a
    // deadlock, not a slow build.
    let stdout = p.StandardOutput.ReadToEnd()
    let stderr = p.StandardError.ReadToEnd()
    p.WaitForExit()
    p.ExitCode, stdout, stderr


/// Compiles `inputFilePath`. Answers a process exit code.
let compile (options: Options) (inputFilePath: string) : int =
    try
        // The runtime assemblies are made reflectable *before* anything is
        // type-checked.
        //
        // `Type.GetType` searches the core library and the compiler's own
        // assembly, neither of which knows about Bjoml — so without this,
        // `(import/class (Chan (: Bjoml.Channel)))` fails at the import with
        // "cannot find the .NET type", and the concurrency runtime is
        // linkable but not nameable. `registerAssemblyFile` is idempotent and
        // swallows nothing: a runtime assembly that is present but unloadable
        // is a real problem and says so.
        Timing.phase "load runtime assemblies" (fun () ->
            for assemblyPath in Paths.runtimeAssemblies do
                if File.Exists assemblyPath then
                    DotNetInterop.registerAssemblyFile assemblyPath)

        Diagnostics.progress $"Compiling %s{inputFilePath}"

        let result = Pipeline.runFullFrontendPipeline inputFilePath
        match result with
        | Some (env, typedAst, dllDeps, declaredMacros) ->
            // A source file with no `main` is a library whether or not `--lib`
            // was passed: an entry point would call a method that does not
            // exist, and a C# `Exe` without a `Main` does not link at all.
            let isLibrary = options.IsLibrary || not (Map.containsKey "main" env.Bindings)
            let extension = if isLibrary then ".dll" else ".exe"
            let outputFilePath = Path.ChangeExtension(inputFilePath, extension)

            Diagnostics.progress (sprintf "Compilation succeeded. %d declarations." typedAst.Length)
            
            // Only a library records what it links. An executable is the end
            // of the chain: nothing imports it, so nothing needs to find the
            // assemblies behind it.
            let metadata =
                { Exports.metadata env typedAst declaredMacros inputFilePath isLibrary with
                    Deps =
                        if isLibrary then
                            dllDeps |> List.map Path.GetFullPath
                        else
                            [] }

            let csCode =
                Timing.phase "codegen" (fun () -> Codegen.generateProgram env.Registry metadata dllDeps typedAst)
            
            if options.Debug then
                File.WriteAllText("ast_dump.txt", sprintf "%A" typedAst)
            
            /// Does the entry point have to drive a fiber to call `main`?
            ///
            /// `main` is allowed to be a bjoroutine, and that is the only way a
            /// program gets *into* fiber-land today: `bjo` and `sync` do not
            /// exist yet, so without it there would be no caller a yield point
            /// could legally appear under.
            ///
            /// The rest of `main`'s type is not a question. It is
            /// `(-> (List string) int)`, given to the module rather than read
            /// off it (`Pipeline.shapeEntryPoint`) and checked afterwards
            /// (`Pipeline.checkEntryPoint`).
            let mainIsBjoroutine =
                match Map.tryFind "main" env.Bindings with
                | Some b ->
                    let (TypedAST.Scheme(_, _, t)) = b.Scheme

                    match t with
                    | TypedAST.TFun(_, _, TypedAST.EAsync) -> true
                    | _ -> false
                | None -> false

            let mainModuleClass = Codegen.moduleClassName inputFilePath

            // Everything this program links against, where it really lives.
            // Nothing is ever copied next to the output: an assembly has one
            // home, and a program built from it points back at that home.
            let linkedAssemblies =
                (Paths.runtimeAssemblies @ dllDeps)
                |> List.filter File.Exists
                |> List.map Path.GetFullPath
                |> List.distinct

            // The directories the running program has to probe to find those
            // assemblies. The default load context only looks beside the
            // executable, so the entry point installs a resolver that looks
            // here instead.
            let probeDirs =
                linkedAssemblies
                |> List.map Path.GetDirectoryName
                |> List.distinct

            let resolverCode =
                if isLibrary || probeDirs.IsEmpty then ""
                else
                    let dirLiterals =
                        probeDirs
                        |> List.map (fun d -> "@\"" + d.Replace("\"", "\"\"") + "\"")
                        |> String.concat ", "

                    "    private static readonly string[] BjolangProbeDirs = new string[] { " + dirLiterals + " };\n" +
                    "    private static void InstallAssemblyResolver() {\n" +
                    "        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) => {\n" +
                    "            var libOverride = System.Environment.GetEnvironmentVariable(\"BJOLANG_LIB\");\n" +
                    "            if (!string.IsNullOrEmpty(libOverride)) {\n" +
                    "                var overridden = System.IO.Path.Combine(libOverride, \"std\", name.Name + \".dll\");\n" +
                    "                if (System.IO.File.Exists(overridden)) return context.LoadFromAssemblyPath(overridden);\n" +
                    "            }\n" +
                    "            foreach (var dir in BjolangProbeDirs) {\n" +
                    "                var candidate = System.IO.Path.Combine(dir, name.Name + \".dll\");\n" +
                    "                if (System.IO.File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);\n" +
                    "            }\n" +
                    "            return null;\n" +
                    "        };\n" +
                    "    }\n"

            // `Main` itself must not touch a single type from a linked
            // assembly: the JIT would then have to load that assembly before
            // the resolver is in place. All real work lives in `Run`, which is
            // not compiled until it is called.
            // A bjoroutine `main` returns a `Fiber<T>`, which is a description of
            // work rather than the work's result, so the entry point has to
            // drive it. `RunToCompletion` starts the body on *this* thread and
            // blocks until it lands — which is safe here and nowhere else: the
            // rule it documents is never to call it from a pool thread, and the
            // thread `Main` runs on is the one thread in the process that is
            // certainly not one.
            let callMain (argExpr: string) =
                if mainIsBjoroutine then
                    $"        _ = Bjoml.Bjo.RunToCompletion(() => %s{mainModuleClass}.main(%s{argExpr}));\n"
                else
                    $"        %s{mainModuleClass}.main(%s{argExpr});\n"

            // One call, always. `main` takes the arguments as a `(List string)`
            // whether or not it was written with a parameter, so there is no
            // shape of entry point to choose between — and no case in which the
            // arguments are dropped, or a placeholder is passed to a `main` that
            // cannot take one.
            let runBody =
                $"        SchemeList.SchemeList<string> bjoArgs = SchemeList.SchemeList.Empty<string>();\n" +
                $"        for (int i = args.Length - 1; i >= 0; i--) {{\n" +
                $"            bjoArgs = SchemeList.SchemeList.Cons(args[i], bjoArgs);\n" +
                $"        }}\n" +
                callMain "bjoArgs"

            let entryPointCode =
                if isLibrary then ""
                else
                    "\npublic static class BjolangEntryPoint {\n" +
                    resolverCode +
                    "    public static void Main(string[] args) {\n" +
                    (if resolverCode = "" then "" else "        InstallAssemblyResolver();\n") +
                    "        Run(args);\n" +
                    "    }\n" +
                    "    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]\n" +
                    "    private static void Run(string[] args) {\n" +
                    runBody +
                    "    }\n" +
                    "}\n"

            let fullCode = csCode + entryPointCode

            let tmpDir = Path.Combine(Path.GetTempPath(), "Bjolang_" + System.Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tmpDir) |> ignore
            
            let projType = if isLibrary then "Library" else "Exe"

            // `Private` false keeps MSBuild from copying the referenced
            // assemblies into the output directory. They are resolved from
            // where they were built, at runtime, by the entry point's resolver.
            //
            // Generated from `linkedAssemblies` rather than written out, so the
            // set of runtime assemblies is stated once, in `Paths`. It used to
            // be spelled here as well, which is how a newly added one — Bjoml —
            // could be on the resolver's probe path and still not be referenced
            // at compile time.
            let dllReferences =
                linkedAssemblies
                |> List.map (fun dllPath ->
                    let name = Path.GetFileNameWithoutExtension(dllPath)
                    $"    <Reference Include=\"{name}\">\n      <HintPath>{dllPath}</HintPath>\n      <Private>false</Private>\n    </Reference>")
                |> String.concat "\n"
                
            let csprojContent = $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>{projType}</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
{dllReferences}
  </ItemGroup>
</Project>"""
            File.WriteAllText(Path.Combine(tmpDir, "Project.csproj"), csprojContent)
            File.WriteAllText(Path.Combine(tmpDir, "Program.cs"), fullCode)
            if options.Debug then
                File.WriteAllText("out.cs", fullCode)
            
            let outDir = Path.GetFullPath(if System.String.IsNullOrWhiteSpace(Path.GetDirectoryName(outputFilePath)) then "." else Path.GetDirectoryName(outputFilePath))
            let assemblyName = Path.GetFileNameWithoutExtension(outputFilePath)
            
            Diagnostics.progress "Invoking C# Compiler..."

            /// The two files the host needs beside an executable.
            ///
            /// Written by whichever backend produced the assembly, so the C#
            /// build being a process, a library call or MSBuild is not
            /// something the resulting program can tell apart.
            let writeHostFiles (targetPath: string) =
                if not isLibrary then
                    let assemblyBaseName = Path.GetFileNameWithoutExtension(targetPath)
                    let runtimeConfigPath = Path.ChangeExtension(targetPath, ".runtimeconfig.json")
                    let runtimeConfigContent = "{\n  \"runtimeOptions\": {\n    \"tfm\": \"net10.0\",\n    \"framework\": {\n      \"name\": \"Microsoft.NETCore.App\",\n      \"version\": \"10.0.0\"\n    }\n  }\n}"
                    File.WriteAllText(runtimeConfigPath, runtimeConfigContent)

                    // The manifest names only the program itself. Listing a
                    // dependency here would make the host demand a copy of it
                    // beside the executable — an asset path in a deps.json is
                    // always resolved against the application directory, which
                    // is exactly what forced the standard library to be
                    // duplicated into every output directory. The entry point's
                    // resolver loads them from where they live instead.
                    let depsJson =
                        "{\n  \"runtimeTarget\": { \"name\": \".NETCoreApp,Version=v10.0\", \"signature\": \"\" },\n  \"compilationOptions\": {},\n  \"targets\": {\n    \".NETCoreApp,Version=v10.0\": {\n      \""
                        + assemblyBaseName
                        + "/1.0.0\": {\n        \"runtime\": { \""
                        + assemblyBaseName
                        + ".dll\": {} }\n      }\n    }\n  },\n  \"libraries\": {\n    \""
                        + assemblyBaseName
                        + "/1.0.0\": { \"type\": \"project\", \"serviceable\": false, \"sha512\": \"\" }\n  }\n}"
                    let depsJsonPath = Path.ChangeExtension(targetPath, ".deps.json")
                    File.WriteAllText(depsJsonPath, depsJson)

            /// Roslyn as a library, in this process.
            ///
            /// Only when something has committed the process to it — see
            /// `CSharpEmit.preferInProcess`. A batch build has not, because
            /// `csc -shared` beats a cold in-process Roslyn and the two do not
            /// produce the same bytes, so mixing them within one build would
            /// make a module's assembly depend on how it was reached.
            ///
            /// `None` falls through to `csc`, which falls through to MSBuild.
            let tryInProcessCompile () =
                if not (CSharpEmit.inProcessPreferred ()) then
                    None
                else
                    try
                        let targetPath = Path.GetFullPath(outputFilePath)

                        let emitOptions: CSharpEmit.Options =
                            { AssemblyName = Path.GetFileNameWithoutExtension(outputFilePath)
                              Target = if isLibrary then CSharpEmit.Library else CSharpEmit.Executable
                              Optimize = not options.Debug
                              EmitPdb = true
                              References = linkedAssemblies }

                        match CSharpEmit.emitToFile emitOptions fullCode targetPath with
                        | [] ->
                            writeHostFiles targetPath
                            Diagnostics.progress $"Successfully built %s{outputFilePath}"
                            try Directory.Delete(tmpDir, true) with | _ -> ()
                            Some 0
                        | errors ->
                            // Printed, not swallowed. These are diagnostics
                            // about generated C#, so they are a compiler bug
                            // rather than a program error either way — but a
                            // silent fall-through to `csc` produced the same
                            // errors a second later with no account of the
                            // first attempt.
                            printfn "C# compilation reported:"
                            for e in errors do printfn "%s" e
                            None
                    with ex ->
                        // Roslyn missing, or a reference pack that is not
                        // where it should be. Environmental, so it falls
                        // through rather than failing the build.
                        printfn $"In-process C# compilation unavailable (%s{ex.Message}); falling back."
                        None

            // This does a fast compilation using the C# dll instead of the dotnet exe.
            let tryFastCompile () =
                try
                    let loc = typeof<obj>.Assembly.Location
                    let dotnetRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(loc), "..", "..", ".."))
                    let sdkDir = Path.Combine(dotnetRoot, "sdk")
                    if not (Directory.Exists(sdkDir)) then None
                    else
                        let cscFiles = Directory.GetFiles(sdkDir, "csc.dll", SearchOption.AllDirectories)
                        match cscFiles |> Array.tryHead with
                        | None -> None
                        | Some cscDll ->
                            let target = if isLibrary then "library" else "exe"
                            let runtimeDir = Path.GetDirectoryName(loc)

                            // Reference assemblies, not the implementation ones.
                            //
                            // `typeof<obj>.Assembly.Location` sits in the shared
                            // framework, where the core library is
                            // `System.Private.CoreLib` — an implementation
                            // detail. An assembly compiled against it records a
                            // dependency on it by name, and anything that later
                            // consumes that assembly through the ordinary
                            // reference assemblies cannot resolve the types its
                            // signatures mention: a trait whose associated type
                            // is a tuple fails with "the type '(, )' is defined
                            // in an assembly that is not referenced". That is
                            // exactly the MSBuild path this function falls back
                            // to, so the two builds have to agree on which
                            // assemblies they mean.
                            let bclDir =
                                let refPack =
                                    Path.Combine(
                                        dotnetRoot,
                                        "packs",
                                        "Microsoft.NETCore.App.Ref",
                                        Path.GetFileName(runtimeDir),
                                        "ref"
                                    )

                                if Directory.Exists refPack then
                                    // One target-framework directory inside.
                                    match Directory.GetDirectories(refPack) |> Array.tryHead with
                                    | Some tfmDir -> tfmDir
                                    | None -> runtimeDir
                                else
                                    runtimeDir

                            let bclRefs =
                                Timing.phase "gather BCL references" (fun () ->
                                    Directory.GetFiles(bclDir, "*.dll")
                                    |> Array.map (fun p -> $"\"-r:{p}\"")
                                    |> String.concat " ")
                            
                            let userRefs =
                                linkedAssemblies
                                |> List.map (fun p -> $"\"-r:{p}\"")
                                |> String.concat " "
                                
                            let csFile = Path.Combine(tmpDir, "Program.cs")
                            let targetPath = Path.GetFullPath(outputFilePath)
                            // Optimization is not free to leave off: without
                            // `-optimize+` Roslyn marks the assembly as
                            // debuggable, which tells the JIT to leave it
                            // alone, and the same generated C# then runs
                            // several times slower. It is off only under `-d`,
                            // where stepping through the code matters more than
                            // how fast it runs.
                            // Symbols in both configurations. `#line` maps the
                            // generated C# back to the Bjolang it came from,
                            // and a stack trace can only follow that mapping if
                            // there is a pdb to carry it. Portable symbols cost
                            // a file beside the output and nothing at run time —
                            // `-optimize+` still applies.
                            let codeGenArgs =
                                if options.Debug then "-optimize- -debug:portable" else "-optimize+ -debug:portable"

                            // `-shared` hands the compilation to a VBCSCompiler
                            // server, started on first use and kept alive
                            // between invocations. What that saves is Roslyn's
                            // JIT and the parse of ~150 reference assemblies,
                            // which a fresh `csc` process pays every time and
                            // is most of what a small build costs. The server
                            // keys its cache on the reference set, so builds
                            // that link the same standard library share it.
                            //
                            // Deliberately not the default when
                            // `BJOLANG_NO_CSC_SERVER` is set: a stale server
                            // holding an old reference is a class of failure
                            // with no good diagnostic, and the way out of one
                            // has to not require editing the compiler.
                            let shared =
                                match System.Environment.GetEnvironmentVariable "BJOLANG_NO_CSC_SERVER" with
                                | null | "" | "0" -> "-shared"
                                | _ -> ""

                            // `-deterministic`, without which unchanged source
                            // does not produce an unchanged `.dll`: Roslyn
                            // stamps a fresh MVID and a timestamp into every
                            // assembly it emits unless told not to. Two
                            // consecutive builds of the same file differed, and
                            // had always differed — so the property a process
                            // per module was thought to guarantee was never
                            // actually there to lose. It is here now, which is
                            // what makes "the same module compiles to the same
                            // bytes however it was reached" a thing that can be
                            // tested rather than assumed.
                            // The generated C# lives in a temp directory named
                            // after a fresh GUID, and `-debug:portable` records
                            // the path of the source it came from. So
                            // `-deterministic` alone changes nothing: the
                            // filename it is being deterministic about is
                            // different every build. Mapped to a fixed
                            // spelling, which is also the one the in-process
                            // path uses.
                            // The replacement has to be non-empty. csc rejects
                            // `-pathmap:x=` as a bad argument, and a rejected
                            // argument here is not an error the user sees: it
                            // fails the whole `csc` attempt, which falls back
                            // to MSBuild, which builds successfully and
                            // non-deterministically.
                            let pathMap =
                                $"-pathmap:\"{tmpDir}{Path.DirectorySeparatorChar}={CSharpEmit.generatedSourceRoot}\""

                            let cscArgs = $"exec \"{cscDll}\" -noconfig -nullable:enable -deterministic {pathMap} {shared} {codeGenArgs} -target:{target} -out:\"{targetPath}\" \"{csFile}\" {userRefs} {bclRefs}"

                            let exitCode, stdout, stderr =
                                Timing.phase "csc" (fun () -> runProcess "dotnet" cscArgs [])

                            // csc ran and said no. Print what it said.
                            //
                            // Returning `None` here falls back to the MSBuild
                            // path, which is right — the two builds can differ,
                            // and one that fails here may still succeed there.
                            // But the diagnostics used to be dropped on the
                            // floor, and MSBuild's are not always the same
                            // ones: a program whose real fault was a type error
                            // was reported as "does not contain a static
                            // 'Main'", which is a description of a consequence
                            // three steps removed from the cause.
                            //
                            // Only reached when csc actually started. Failing
                            // to *find* csc returns earlier, so this never
                            // reports an environmental miss as a program error.
                            if exitCode <> 0 then
                                printfn "C# compilation reported:"
                                if not (System.String.IsNullOrWhiteSpace stdout) then printfn "%s" (stdout.TrimEnd())
                                if not (System.String.IsNullOrWhiteSpace stderr) then printfn "%s" (stderr.TrimEnd())

                            if exitCode = 0 then
                                // Both configurations emit symbols now, so the
                                // pdb is kept: it is what carries the `#line`
                                // mapping into a stack trace, and without it a
                                // trace names generated methods and no source
                                // at all. `-optimize+` still applies.
                                writeHostFiles targetPath
                                Diagnostics.progress $"Successfully built %s{outputFilePath}"
                                try Directory.Delete(tmpDir, true) with | _ -> ()
                                Some 0
                            else
                                None
                with _ -> None

            match tryInProcessCompile () |> Option.orElseWith tryFastCompile with
            | Some code -> code
            | None ->
                let projPath = Path.Combine(tmpDir, "Project.csproj")
                let configuration = if options.Debug then "Debug" else "Release"

                let exitCode, stdout, stderr =
                    runProcess
                        "dotnet"
                        $"build \"%s{projPath}\" -c %s{configuration} -o \"%s{outDir}\" /p:AssemblyName=%s{assemblyName}"
                        []

                // MSBuild's own report, which used to go straight to the
                // console. Passed on either way: on success it is the build
                // log, and on failure it is the only account of what went
                // wrong.
                if not (System.String.IsNullOrWhiteSpace stdout) then printfn "%s" (stdout.TrimEnd())
                if not (System.String.IsNullOrWhiteSpace stderr) then printfn "%s" (stderr.TrimEnd())

                if exitCode = 0 then
                    let generatedDll = Path.Combine(outDir, assemblyName + ".dll")
                    if System.IO.File.Exists(generatedDll) && Path.GetFullPath(generatedDll) <> Path.GetFullPath(outputFilePath) then
                        if System.IO.File.Exists(outputFilePath) then System.IO.File.Delete(outputFilePath)
                        System.IO.File.Move(generatedDll, outputFilePath)
                    
                    let genRuntimeConfig = Path.Combine(outDir, assemblyName + ".runtimeconfig.json")
                    let outRuntimeConfig = Path.ChangeExtension(outputFilePath, ".runtimeconfig.json")
                    if System.IO.File.Exists(genRuntimeConfig) && Path.GetFullPath(genRuntimeConfig) <> Path.GetFullPath(outRuntimeConfig) then
                        if System.IO.File.Exists(outRuntimeConfig) then System.IO.File.Delete(outRuntimeConfig)
                        System.IO.File.Move(genRuntimeConfig, outRuntimeConfig)

                    Diagnostics.progress $"Successfully built %s{outputFilePath}"
                    try Directory.Delete(tmpDir, true) with | _ -> ()
                    0
                else
                    printfn "C# Compilation failed."
                    // Leave tmpDir for debugging
                    printfn $"Temp directory: %s{tmpDir}"
                    1
        | None ->
            printfn "Compilation failed."
            1
    with ex ->
        Diagnostics.reportFailure ex
        printfn "Compilation failed."
        1

// ---------------------------------------------------------------------------
// Building an imported module
// ---------------------------------------------------------------------------

/// The modules whose builds are already in flight, above this one.
///
/// Two spellings of one thing, because there are two ways the recursion can
/// travel. In-process it is the list; out-of-process it has to be the
/// environment, since each compiler process has a module graph of its own and
/// the in-process check cannot see past it. Without either, `a.bjo` importing
/// `b.bjo` importing `a.bjo` recurses until something gives out.
///
/// The variable cannot be retired while the out-of-process path exists, even
/// though one process now normally holds the whole graph: a build that falls
/// back to it, or a compiler invoked by one that did, still needs the chain to
/// cross the boundary. It becomes dead the day that path is deleted.
let private buildChainVariable = "BJOLANG_BUILD_CHAIN"

let private inFlight = System.Collections.Generic.List<string>()

let private buildChain () =
    let inherited =
        match System.Environment.GetEnvironmentVariable buildChainVariable with
        | null | "" -> []
        | v -> v.Split(';') |> Array.toList |> List.filter (fun s -> s <> "")

    inherited @ List.ofSeq inFlight

/// Refuses a module that is imported from a module it is itself building.
let private checkNotCyclic (chain: string list) (bjoPath: string) =
    if List.contains bjoPath chain then
        failwithf
            "Import Error: '%s' is imported from a module it is itself building. Import chain: %s"
            (Path.GetFileName bjoPath)
            ((chain @ [ bjoPath ]) |> List.map Path.GetFileName |> String.concat " -> ")

/// Compiles an imported `.bjo` to a `.dll`, in a process of its own.
///
/// The original arrangement, kept as a fallback. A sub-compilation shares
/// `Gensym`'s counter and the macro table with the compilation that asked for
/// it — module-level state, none of it stacked — and a process boundary was the
/// cheapest correct isolation. `Session.isolated` is the same guarantee without
/// the process, and this is what to fall back to when a build that used to work
/// stops: if `BJOLANG_OUT_OF_PROCESS_DEPS=1` fixes it, the fault is state that
/// was not moved.
let private compileDependencyOutOfProcess (bjoPath: string) : string =
    let dllPath = Path.ChangeExtension(bjoPath, ".dll")
    let chain = buildChain ()
    checkNotCyclic chain bjoPath

    printfn $"Building imported module %s{Path.GetFileName bjoPath}"

    let self = System.Reflection.Assembly.GetEntryAssembly().Location

    let fileName, args =
        if System.String.IsNullOrEmpty self then
            // Published as a native host: the process itself is the compiler.
            System.Environment.ProcessPath, $"--lib \"{bjoPath}\""
        else
            "dotnet", $"exec \"{self}\" --lib \"{bjoPath}\""

    let exitCode, stdout, stderr =
        Timing.phase "dependency build (out of process)" (fun () ->
            runProcess fileName args [ buildChainVariable, String.concat ";" (chain @ [ bjoPath ]) ])

    if exitCode <> 0 || not (File.Exists dllPath) then
        // The dependency's own diagnostics, passed through. They name its file
        // and its lines, which is where the fault is.
        failwithf
            "Import Error: could not build '%s', which is imported here.\n%s%s"
            (Path.GetFileName bjoPath)
            (if System.String.IsNullOrWhiteSpace stdout then "" else stdout.TrimEnd() + "\n")
            (if System.String.IsNullOrWhiteSpace stderr then "" else stderr.TrimEnd())

    dllPath

/// Compiles an imported `.bjo` to a `.dll`, here.
///
/// `Session.isolated` is what makes this legal: the sub-compilation starts from
/// the state a fresh process would have — an empty macro table, a `Gensym`
/// counter at zero — and the outer compilation gets its own back afterwards.
/// The counter matters most. It is what keeps a module's `.dll` byte-identical
/// however much was built before it, which is what a timestamp-based staleness
/// cache is worth anything on top of.
///
/// The dependency's narration is suppressed rather than captured. Out of
/// process it was captured and thrown away on success, so this is the same
/// output; a failure still reports through `Diagnostics`, naming the
/// dependency's own file and lines.
///
/// The C# backend is not changed by any of this. A dependency emits the way
/// everything else in the process emits — see `CSharpEmit.preferInProcess` —
/// so that removing the process boundary does not quietly change which compiler
/// produced a module's `.dll`.
let private compileDependencyInProcess (bjoPath: string) : string =
    let dllPath = Path.ChangeExtension(bjoPath, ".dll")
    checkNotCyclic (buildChain ()) bjoPath

    printfn $"Building imported module %s{Path.GetFileName bjoPath}"

    inFlight.Add bjoPath
    let narrating = Diagnostics.verbose
    Diagnostics.verbose <- false

    let exitCode =
        try
            Timing.phase "dependency build (in process)" (fun () ->
                Session.isolated (fun () -> compile { IsLibrary = true; Debug = false } bjoPath))
        finally
            Diagnostics.verbose <- narrating
            inFlight.RemoveAt(inFlight.Count - 1)

    if exitCode <> 0 || not (File.Exists dllPath) then
        failwithf
            "Import Error: could not build '%s', which is imported here."
            (Path.GetFileName bjoPath)

    dllPath

/// Installs the backend `Pipeline` reaches for when an import names a `.bjo`.
///
/// In-process by default. `BJOLANG_OUT_OF_PROCESS_DEPS=1` puts the process
/// boundary back, which is the escape hatch for exactly one class of bug: state
/// that belongs to a compilation and was not moved into `Session`, which shows
/// up as a module that compiles alone and not after another one.
let installDependencyBackend () =
    Pipeline.compileLibrary <-
        match System.Environment.GetEnvironmentVariable "BJOLANG_OUT_OF_PROCESS_DEPS" with
        | null | "" | "0" -> compileDependencyInProcess
        | _ -> compileDependencyOutOfProcess
