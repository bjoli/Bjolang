// The contexts of `Compilation.fs`: that a context of its own shares no state
// of a compilation with another, and that checks of files at the same time
// give what each gives alone.
//
//   ./build_compiler.sh && ./build_std.sh
//   dotnet fsi --quiet tests/contexts/contexts.fsx
//
// It checks every file of TestFiles/errors one at a time and then on 8 and on
// 16 threads, which takes about a minute. Exit code 1 when a check fails.
//
// The console is one for the process, and the compiler prints a failure that
// it does not record. So what is written goes to the buffer of the check that
// writes it (`Routed`), in place of `Diagnostics.captured`, which sets the
// console and is for one compilation at a time.

#r "../../bin/compiler/Bjolang.dll"

open System
open System.Threading
open Bjolang

let root = IO.Path.GetFullPath(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
Environment.SetEnvironmentVariable("BJOLANG_HOME", root)

let realOut = Console.Out
let printedBy = new AsyncLocal<Text.StringBuilder>()

type Routed() =
    inherit IO.TextWriter()
    override _.Encoding = Text.Encoding.UTF8

    override _.Write(c: char) =
        match printedBy.Value with
        | null -> ()
        | b -> lock b (fun () -> b.Append c |> ignore)

    override _.Write(s: string) =
        match printedBy.Value with
        | null -> ()
        | b -> lock b (fun () -> b.Append s |> ignore)

Console.SetOut(new Routed())
Console.SetError(new Routed())

let say fmt =
    Printf.kprintf (fun (line: string) -> realOut.WriteLine line) fmt

let mutable failures = 0

let expect (name: string) (ok: bool) (detail: string) =
    if ok then
        say "ok: %s" name
    else
        failures <- failures + 1
        say "FAIL: %s\n  %s" name detail

// ---------------------------------------------------------------------------
// Values and settings
// ---------------------------------------------------------------------------

Diagnostics.reset ()
Diagnostics.warn "a warning of the root"
let inRoot = (Diagnostics.snapshot ()).Length
let inNew = Compilation.inNew (fun () -> (Diagnostics.snapshot ()).Length)
expect "a new context has no diagnostics of the one that made it" (inRoot = 1 && inNew = 0) $"{inRoot} {inNew}"
Compilation.inNew (fun () -> Diagnostics.warn "inside")
expect "what a new context collects stays in it" ((Diagnostics.snapshot ()).Length = 1) ""

Paths.sourceOverrides.Value <- Map.ofList [ ("/x.bjo", "text") ]
let inherited = Compilation.inNew (fun () -> Paths.sourceOverrides.Value.ContainsKey "/x.bjo")
Compilation.inNew (fun () -> Paths.sourceOverrides.Value <- Map.empty)
expect "a setting starts as the maker's" inherited ""
expect "a setting written in a new context is not written in the maker's" (Paths.sourceOverrides.Value.ContainsKey "/x.bjo") ""
Paths.sourceOverrides.Value <- Map.empty

let onThread = ref -1
let plain = Thread(fun () -> onThread.Value <- (Diagnostics.snapshot ()).Length)
plain.Start()
plain.Join()
expect "a thread with no context of its own has the root's" (onThread.Value = 1) (string onThread.Value)

let inTask = Compilation.inNew (fun () ->
    Diagnostics.warn "of the new context"
    Tasks.Task.Run(fun () -> (Diagnostics.snapshot ()).Length).Result)
expect "a task that a context starts has that context" (inTask = 1) (string inTask)
Diagnostics.reset ()

// ---------------------------------------------------------------------------
// Whole checks
// ---------------------------------------------------------------------------

let options: Build.Options =
    { Build.dependencyOptions with
        IsLibrary = false
        Check = true }

/// What a check of `path` gives, in a context of its own: each error and
/// warning, an exception that leaves the compiler, and a failure that the
/// compiler printed and did not record.
let check (path: string) : string list =
    Compilation.inNew (fun () ->
        Diagnostics.verbose.Value <- false
        Diagnostics.reset ()
        printedBy.Value <- Text.StringBuilder()

        let status, raised =
            try
                Build.compile options path, []
            with ex ->
                -1, [ "RAISED " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0] ]

        let found =
            Diagnostics.snapshot ()
            |> List.map (fun d -> (if d.Severity = Diagnostics.Error then "E " else "W ") + d.Message)

        let printed =
            if status <> 0 && not (found |> List.exists (fun m -> m.StartsWith "E")) then
                [ "PRINTED " + printedBy.Value.ToString().Trim() ]
            else
                []

        raised @ found @ printed)

let files =
    IO.Directory.GetFiles(IO.Path.Combine(root, "TestFiles", "errors"), "*.bjo") |> Array.sort

say "%d files" files.Length

let timed (f: unit -> 'a) =
    let sw = Diagnostics.Stopwatch.StartNew()
    let r = f ()
    r, sw.Elapsed.TotalSeconds

// One at a time. The first pass loads what the files import; the second and
// third are compared, so that a file whose answer is not stable is known.
files |> Array.iter (fun f -> check f |> ignore)
let alone, aloneTime = timed (fun () -> files |> Array.map check)
let again = files |> Array.map check
let unstable = Array.zip alone again |> Array.filter (fun (a, b) -> a <> b) |> Array.length
say "one at a time: %.1f s" aloneTime
expect "a check gives the same after other checks" (unstable = 0) $"{unstable} files differ"
// So that a library that is not built, which stops every check at its first
// import, is not taken for checks that agree. Some files are refused only
// after the check: by code generation, or by the C# compiler.
let withErrors = alone |> Array.filter (fun found -> found |> List.exists (fun m -> m.StartsWith "E ")) |> Array.length
let sameError = alone |> Array.countBy id |> Array.map snd |> Array.max
expect "most files have an error of their own" (withErrors * 10 >= files.Length * 9 && sameError * 4 < files.Length) $"{withErrors} of {files.Length} have an error; {sameError} have one same answer"

for workers in [ 8; 16; 16 ] do
    let results: string list[] = Array.create files.Length []
    let next = ref -1

    let (), time =
        timed (fun () ->
            let threads =
                [ for _ in 1..workers ->
                      Thread(fun () ->
                          let mutable go = true

                          while go do
                              let i = Interlocked.Increment(&next.contents)

                              if i >= files.Length then
                                  go <- false
                              else
                                  results[i] <- check files[i]) ]

            threads |> List.iter (fun t -> t.Start())
            threads |> List.iter (fun t -> t.Join()))

    let differing = [ for i in 0 .. files.Length - 1 do if results[i] <> alone[i] then yield i ]

    let detail =
        differing
        |> List.truncate 5
        |> List.map (fun i ->
            let first (found: string list) =
                found |> List.map (fun m -> m.Split('\n')[0]) |> String.concat " | "

            $"{IO.Path.GetFileName files[i]}\n    alone:    {first alone[i]}\n    together: {first results[i]}")
        |> String.concat "\n  "

    say "%d threads: %.1f s, %.1f times as fast" workers time (aloneTime / time)
    expect $"on {workers} threads, each check gives what it gives alone" differing.IsEmpty detail

say "%d failures" failures
exit (if failures = 0 then 0 else 1)
