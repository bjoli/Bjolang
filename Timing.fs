/// Where a compilation's wall-clock time went.
///
/// Off unless `BJOLANG_TIMING` is set, and cheap enough when it is that a phase
/// may be wrapped without a second thought — one `Stopwatch` timestamp per
/// call, and a dictionary update.
///
/// Accumulating rather than logging each entry: a phase that runs once per
/// module runs many times per build, and what a reader wants is the total for
/// the phase, with the count beside it so a slow phase and a frequent one can
/// be told apart. Process start is included as its own line, because on a build
/// this small the runtime's own startup is a real part of the answer and
/// leaving it out makes every measured phase look bigger than it is.
module Bjolang.Timing

open System
open System.Collections.Concurrent
open System.Diagnostics

let enabled =
    match Environment.GetEnvironmentVariable "BJOLANG_TIMING" with
    | null | "" | "0" -> false
    | _ -> true

/// Elapsed since this process started, which is what every measurement is
/// relative to. Read at module init, so it excludes only the runtime's own
/// bootstrap.
let private processStart = Stopwatch.StartNew()

/// Total milliseconds and call count, by phase name.
///
/// Concurrent because dependency compilation is meant to become parallel, and a
/// measurement that stops being valid the moment it would be useful is not
/// worth taking.
let private totals = ConcurrentDictionary<string, float * int>()

/// The order phases were first seen in, which is the order they are reported
/// in. Alphabetical would put "Type checking" before "Parsing"; the order the
/// compiler actually ran them is what a reader is holding in their head.
let private order = ConcurrentQueue<string>()

let private record (name: string) (ms: float) =
    let mutable added = false

    totals.AddOrUpdate(
        name,
        (fun _ ->
            added <- true
            ms, 1),
        fun _ (total, count) -> total + ms, count + 1
    )
    |> ignore

    if added then order.Enqueue name

/// Times `f` under `name`, and hands back what it returned.
///
/// The result is passed through rather than discarded so that this can be
/// wrapped around an existing expression without restructuring it, and the
/// timing is recorded even when `f` throws — a phase that fails after two
/// seconds is exactly the one worth knowing about.
let phase (name: string) (f: unit -> 'a) : 'a =
    if not enabled then
        f ()
    else
        let sw = Stopwatch.StartNew()

        try
            f ()
        finally
            record name sw.Elapsed.TotalMilliseconds

/// Records a duration measured elsewhere — for a phase whose start and end are
/// not one call, such as a background pre-warm.
let note (name: string) (ms: float) = if enabled then record name ms

/// Prints the breakdown. Called once, as the compiler exits.
///
/// To stderr, so that a timed build can still have its stdout piped somewhere
/// that expects only the compiler's ordinary output.
let report () =
    if enabled then
        let lines =
            order
            |> Seq.distinct
            |> Seq.choose (fun name ->
                match totals.TryGetValue name with
                | true, (ms, count) -> Some(name, ms, count)
                | _ -> None)
            |> List.ofSeq

        let width =
            lines |> List.fold (fun acc (name: string, _, _) -> max acc name.Length) 8

        eprintfn ""
        eprintfn "=== Timing ==="

        for (name, ms, count) in lines do
            let times = if count = 1 then "" else $"  (x%d{count})"
            eprintfn "%-*s %8.1f ms%s" width name ms times

        eprintfn "%-*s %8.1f ms" width "wall clock" processStart.Elapsed.TotalMilliseconds
