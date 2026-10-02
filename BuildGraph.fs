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

/// Builds a set of libraries, and everything they import, in parallel.
///
/// The build order is read from the modules' own imports. A module is built as
/// soon as everything it imports has been built.
///
/// The work runs in separate `--worker` processes rather than threads. A
/// compilation keeps its state in module-level variables, which
/// `Session.isolated` saves and restores around one compilation at a time on
/// one thread, and `Diagnostics.captured` redirects the console for the whole
/// process. Two compilations on two threads in one process would overwrite each
/// other's state, so this process only plans and hands out work.
///
/// Running several workers at once is safe because of the order. A module is
/// sent to a worker only when everything it imports is built, so the worker's
/// own import walk finds every dependency current and never builds one itself.
/// That means no two workers write the same `.dll`, and no worker loads an
/// assembly that another worker is still writing.
module Bjolang.BuildGraph

open System
open System.IO
open System.Collections.Generic
open System.Collections.Concurrent

/// What happened to one module.
type private Outcome =
    | UpToDate
    | Built of output: string
    | Failed of output: string
    /// Not attempted, because the module named here, which it imports
    /// directly or further down, failed.
    | Skipped of because: string

/// The sources under a path: the file itself, or every `.bjo` below a
/// directory.
///
/// Directories whose names begin with a dot are skipped. Tools keep their own
/// files there (`.bjo/` holds fetched packages, `.test-logs/` a test run), and
/// those are not sources of the tree being built.
let private sourcesUnder (path: string) : string list =
    let rec walk (dir: string) : string seq =
        seq {
            yield! Directory.EnumerateFiles(dir, "*.bjo")

            for sub in Directory.EnumerateDirectories dir do
                if not ((Path.GetFileName sub).StartsWith ".") then
                    yield! walk sub
        }

    let full = Path.GetFullPath path

    if Directory.Exists full then
        walk full |> Seq.map Path.GetFullPath |> Seq.sort |> List.ofSeq
    elif File.Exists full then
        [ full ]
    else
        failwithf $"--build-graph: no such file or directory '%s{path}'."

/// A path as a person reads it: relative to where the build was started, when
/// it is under there.
let private shown (path: string) : string =
    let relative = Path.GetRelativePath(Environment.CurrentDirectory, path)
    if relative.StartsWith ".." then path else relative

/// The graph a set of sources and their imports make.
type private Graph =
    { /// Every module, in an order where each comes after all it imports.
      Order: string list
      /// What each module imports, of the modules in the graph.
      Imports: IDictionary<string, string list>
      /// What imports each module.
      ImportedBy: IDictionary<string, string list>
      /// The modules whose imports could not be read. Built anyway, so that
      /// the compiler says what is wrong with them, and counted stale.
      Unreadable: HashSet<string> }

/// One import cycle among `modules`, as a chain that starts and ends with the
/// same module.
let private findCycle (imports: IDictionary<string, string list>) (modules: Set<string>) : string list =
    let visiting = HashSet<string>()
    let visited = HashSet<string>()

    let rec visit (path: string list) (m: string) : string list option =
        if visiting.Contains m then
            let chain = List.rev (m :: path)
            Some(chain |> List.skipWhile (fun x -> x <> m))
        elif visited.Contains m then
            None
        else
            visiting.Add m |> ignore
            let next = imports[m] |> List.filter modules.Contains

            let found = next |> List.tryPick (visit (m :: path))
            visiting.Remove m |> ignore
            visited.Add m |> ignore
            found

    modules |> Seq.tryPick (visit []) |> Option.defaultValue (List.ofSeq modules)

/// Reads the graph: the given sources, and everything they import, however far
/// down.
///
/// A dependency outside the given paths is included and built if it is out of
/// date, the same way an import would build it. Otherwise a module could be
/// compiled against an out-of-date `.dll` of something it imports.
let private readGraph (sources: string list) : Result<Graph, string> =
    let imports = Dictionary<string, string list>()
    let unreadable = HashSet<string>()
    let queue = Queue<string>(sources)

    while queue.Count > 0 do
        let m = queue.Dequeue()

        if not (imports.ContainsKey m) then
            let found =
                try
                    Pipeline.importedSources m
                with _ ->
                    // The file cannot be read. It is still built, so that the
                    // compiler reports the error with its file and line.
                    unreadable.Add m |> ignore
                    []

            imports[m] <- found

            for d in found do
                if not (imports.ContainsKey d) then
                    queue.Enqueue d

    let importedBy = Dictionary<string, string list>()

    for m in imports.Keys do
        importedBy[m] <- []

    for KeyValue(m, ds) in imports do
        for d in ds do
            importedBy[d] <- m :: importedBy[d]

    // Kahn's algorithm, taking the ready modules in name order, so that the
    // order is the same every time for the same tree.
    let remaining = Dictionary<string, int>()

    for KeyValue(m, ds) in imports do
        remaining[m] <- ds.Length

    let ready = SortedSet<string>(imports.Keys |> Seq.filter (fun m -> remaining[m] = 0), StringComparer.Ordinal)
    let order = ResizeArray<string>()

    while ready.Count > 0 do
        let m = ready.Min
        ready.Remove m |> ignore
        order.Add m

        for user in importedBy[m] do
            remaining[user] <- remaining[user] - 1

            if remaining[user] = 0 then
                ready.Add user |> ignore

    if order.Count < imports.Count then
        let stuck = imports.Keys |> Seq.filter (fun m -> remaining[m] > 0) |> Set.ofSeq
        let cycle = findCycle imports stuck

        Error(
            "Import Error: these modules import each other, so none of them can be built first: "
            + (cycle |> List.map shown |> String.concat " -> ")
        )
    else
        Ok
            { Order = List.ofSeq order
              Imports = imports
              ImportedBy = importedBy
              Unreadable = unreadable }

/// Which modules have to be built: the ones that are out of date, and the ones
/// that import something that is about to be rebuilt.
///
/// Modules are checked in build order, so everything a module imports has
/// already been decided when the module is checked. A module that imports
/// something about to be rebuilt is stale whatever its timestamps say, because
/// its `.dll` was compiled against metadata that is about to change.
/// `Pipeline.isCurrent` cannot know that, since it only looks at what is on
/// disk now.
let private staleModules (graph: Graph) : HashSet<string> =
    let stale = HashSet<string>()

    for m in graph.Order do
        let behind =
            graph.Unreadable.Contains m
            || graph.Imports[m] |> List.exists stale.Contains
            || (try not (Pipeline.isCurrent m) with _ -> true)

        if behind then
            stale.Add m |> ignore

    stale

/// For each module, the length of the longest chain of modules that import it,
/// directly or further up. Ready modules are handed out longest chain first,
/// so that the longest sequence of dependent builds starts as early as
/// possible.
let private chainAbove (graph: Graph) : IDictionary<string, int> =
    let height = Dictionary<string, int>()

    for m in List.rev graph.Order do
        height[m] <-
            match graph.ImportedBy[m] with
            | [] -> 0
            | users -> 1 + (users |> List.map (fun u -> height[u]) |> List.max)

    height

// ---------------------------------------------------------------------------
// Workers
// ---------------------------------------------------------------------------

/// What a worker answered about one module, or why it could not.
type private Answer = { Status: int; Output: string }

/// One `--worker` process, started when it is first given something to build
/// and started again if it dies.
type private Worker(forwarded: string list) =
    let mutable proc: Diagnostics.Process = null
    let stderr = Text.StringBuilder()

    let start () =
        let self = Reflection.Assembly.GetEntryAssembly().Location

        let psi =
            if String.IsNullOrEmpty self then
                // Published as a native host: the process itself is the compiler.
                Diagnostics.ProcessStartInfo(Environment.ProcessPath)
            else
                let psi = Diagnostics.ProcessStartInfo("dotnet")
                psi.ArgumentList.Add "exec"
                psi.ArgumentList.Add self
                psi

        psi.ArgumentList.Add "--worker"

        for a in forwarded do
            psi.ArgumentList.Add a

        psi.UseShellExecute <- false
        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true

        lock stderr (fun () -> stderr.Clear() |> ignore)
        let p = Diagnostics.Process.Start psi

        // Drained as it comes, so that a worker with a lot to say on stderr
        // never blocks on a full pipe while this waits for its answer.
        p.ErrorDataReceived.Add(fun e ->
            if not (isNull e.Data) then
                lock stderr (fun () -> stderr.AppendLine e.Data |> ignore))

        p.BeginErrorReadLine()
        proc <- p

    member _.Build(file: string) : Answer =
        if isNull proc || proc.HasExited then
            start ()

        let died () =
            proc.WaitForExit()
            let said = lock stderr (fun () -> stderr.ToString())
            proc <- null

            { Status = 1
              Output = $"The compiler building this module stopped without answering.\n%s{said}" }

        try
            proc.StandardInput.WriteLine file
            proc.StandardInput.Flush()

            match proc.StandardOutput.ReadLine() with
            | null -> died ()
            | line ->
                use json = Text.Json.JsonDocument.Parse line
                let root = json.RootElement

                { Status = root.GetProperty("status").GetInt32()
                  Output = root.GetProperty("output").GetString() }
        with :? IOException ->
            died ()

    member _.Stop() =
        if not (isNull proc) then
            try
                proc.StandardInput.Close()
                proc.WaitForExit()
            with _ ->
                ()

            proc <- null

// ---------------------------------------------------------------------------
// The build
// ---------------------------------------------------------------------------

/// Builds the modules under `paths`, and everything they import, `jobs` at a
/// time. Answers a process exit code: 0 when every module is current at the
/// end.
///
/// Prints one line per module: first `Up to date:` for the modules that need
/// nothing, then `Built library:`, `Failed to build` or `Skipped:` as each
/// finishes, followed by anything the compiler said. A summary goes to stderr.
/// Results are printed in the order they finish, so the order varies between
/// runs, but each module's text is printed in one piece.
///
/// With `dryRun`, it prints what would be built and what each module imports,
/// and builds nothing.
let run (paths: string list) (jobs: int) (dryRun: bool) : int =
    let sources =
        try
            paths |> List.collect sourcesUnder |> List.distinct |> Ok
        with ex ->
            Error ex.Message

    match sources |> Result.bind readGraph with
    | Error message ->
        printfn $"%s{message}"
        1
    | Ok graph ->

    let stale = staleModules graph
    let dllOf (m: string) = shown (Path.ChangeExtension(m, ".dll"))

    if dryRun then
        for m in graph.Order do
            let verdict = if stale.Contains m then "Would build:" else "Up to date:"
            printfn $"%s{verdict} %s{dllOf m}"

            match graph.Imports[m] with
            | [] -> ()
            | ds -> printfn "    imports %s" (ds |> List.map shown |> List.sort |> String.concat ", ")

        eprintfn "Graph: %d modules, %d to build." graph.Order.Length stale.Count
        0
    else

    for m in graph.Order do
        if not (stale.Contains m) then
            printfn $"Up to date: %s{dllOf m}"

    let outcomes = Dictionary<string, Outcome>()

    for m in graph.Order do
        if not (stale.Contains m) then
            outcomes[m] <- UpToDate

    if stale.Count > 0 then
        let height = chainAbove graph

        // How many of each module's imports are still to be built. A module
        // is ready when this reaches zero.
        let waiting = Dictionary<string, int>()

        for m in stale do
            waiting[m] <- graph.Imports[m] |> List.filter stale.Contains |> List.length

        let queue = new BlockingCollection<string>(ConcurrentQueue<string>())
        let answers = new BlockingCollection<string * Answer>()

        let enqueue (ready: string seq) =
            for m in ready |> Seq.sortByDescending (fun m -> height[m], m) do
                queue.Add m

        let forwarded = Build.forwardedArguments ()

        let workers =
            [ for _ in 1 .. max 1 (min jobs stale.Count) ->
                  let worker = Worker(forwarded)

                  let thread =
                      Threading.Thread(fun () ->
                          try
                              for m in queue.GetConsumingEnumerable() do
                                  answers.Add((m, worker.Build m))
                          finally
                              worker.Stop())

                  thread.IsBackground <- true
                  thread.Start()
                  thread ]

        enqueue (stale |> Seq.filter (fun m -> waiting[m] = 0))

        /// Everything above `failed` that is still to be built, which now never
        /// will be.
        let rec skipAbove (failed: string) (cause: string) =
            for user in graph.ImportedBy[failed] do
                if stale.Contains user && not (outcomes.ContainsKey user) then
                    outcomes[user] <- Skipped cause
                    printfn $"Skipped: %s{dllOf user} (it imports %s{shown cause}, which failed)"
                    skipAbove user cause

        while outcomes.Count < graph.Order.Length do
            let m, answer = answers.Take()

            if answer.Status = 0 && File.Exists(Path.ChangeExtension(m, ".dll")) then
                outcomes[m] <- Built answer.Output
                printfn $"Built library: %s{dllOf m}"

                if not (String.IsNullOrWhiteSpace answer.Output) then
                    printfn "%s" (answer.Output.TrimEnd())

                let ready = ResizeArray<string>()

                for user in graph.ImportedBy[m] do
                    if stale.Contains user then
                        waiting[user] <- waiting[user] - 1

                        if waiting[user] = 0 && not (outcomes.ContainsKey user) then
                            ready.Add user

                enqueue ready
            else
                outcomes[m] <- Failed answer.Output
                printfn $"Failed to build %s{shown m}:"

                if not (String.IsNullOrWhiteSpace answer.Output) then
                    printfn "%s" (answer.Output.TrimEnd())

                skipAbove m m

        queue.CompleteAdding()

        for t in workers do
            t.Join()

    let count f =
        outcomes.Values |> Seq.filter f |> Seq.length

    let built = count (function Built _ -> true | _ -> false)
    let failed = count (function Failed _ -> true | _ -> false)
    let skipped = count (function Skipped _ -> true | _ -> false)
    let current = count (function UpToDate -> true | _ -> false)

    eprintfn
        "Graph: %d modules, %d built, %d up to date, %d failed, %d skipped."
        graph.Order.Length built current failed skipped

    if failed = 0 && skipped = 0 then 0 else 1
