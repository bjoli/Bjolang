// Hopac counterpart to bench/Bench/Program.cs and bench/go/main.go.
//
// Hopac is the reference point that matters most for BjoML: it is also CML on .NET,
// it is also built on a work-stealing scheduler, and it has had years of tuning. Go
// tells us what a mature runtime with language support achieves; Hopac tells us what
// the same *idea* costs on the same *platform*, which is the number BjoML should
// actually be judged against.
//
// Every benchmark is shaped identically to its C# and Go twins: same counts, same
// channel topology, same work per unit, same allocation reporting.
//
// A note on fairness: Hopac's `queue` is used for spawning rather than `start`.
// `start` runs the job inline on the current worker until it blocks, which is a
// direct call rather than a spawn; `queue` pushes it to the scheduler, which is what
// `Bjo.Spawn` and Go's `go` both do.

module HopacBench.Program

open System
open System.Diagnostics
open System.Threading
open Hopac
open Hopac.Infixes

// ---------------------------------------------------------------------------

let report (name: string) (n: int) (sw: Stopwatch) (alloc: int64) (extra: string) =
    let nsPerOp = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / float n
    printfn "%-17s %d in %d ms (%.0f ns/op, %.0f B/op), %s"
        name n sw.ElapsedMilliseconds nsPerOp (float alloc / float n) extra

let allocated () = GC.GetTotalAllocatedBytes true

// ---------------------------------------------------------------------------
// 1. Spawn storm
// ---------------------------------------------------------------------------

let mutable private stormCounter = 0L
let mutable private stormRemaining = 0

let spawnStorm () =
    let n = 1_000_000
    stormCounter <- 0L
    stormRemaining <- n
    let finished = IVar<unit>()

    // One job value queued n times. The C# and Go versions likewise hoist their
    // closure, since it captures nothing per iteration.
    let bump =
        job {
            Interlocked.Increment(&stormCounter) |> ignore
            if Interlocked.Decrement(&stormRemaining) = 0 then
                do! IVar.tryFill finished ()
        }

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    for _ in 1 .. n do
        queue bump

    run (IVar.read finished)
    sw.Stop()

    report "Spawn storm" n sw (allocated () - before)
        (sprintf "counter=%d" (Interlocked.Read(&stormCounter)))

/// Same storm, but the producer loop runs INSIDE a job.
///
/// This distinction turns out to dominate the result, and the original benchmark
/// suite was not fair about it. In Go, `main` is itself a goroutine, so `go func()`
/// pushes onto a local run queue and never touches a shared structure. In the .NET
/// versions the producer was an ordinary thread, so every single spawn crossed a
/// shared queue — for Hopac, a TTAS spinlock that 24 workers are simultaneously
/// spinning on to pull work out.
///
/// Spawning from inside the runtime is what Go's number actually measures, so this
/// is the row to compare against it.
let spawnStormInside () =
    let n = 1_000_000
    stormCounter <- 0L
    stormRemaining <- n
    let finished = IVar<unit>()

    let bump =
        job {
            Interlocked.Increment(&stormCounter) |> ignore
            if Interlocked.Decrement(&stormRemaining) = 0 then
                do! IVar.tryFill finished ()
        }

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    run (job {
            for _ in 1 .. n do
                do! Job.queue bump
            do! IVar.read finished
         })

    sw.Stop()

    report "Spawn storm inside" n sw (allocated () - before)
        (sprintf "counter=%d" (Interlocked.Read(&stormCounter)))

// ---------------------------------------------------------------------------
// 2. Spawn + one channel send each
// ---------------------------------------------------------------------------

let rec private takeSum (ch: Ch<int>) n (acc: int64) =
    job {
        if n = 0 then
            return acc
        else
            let! v = Ch.take ch
            return! takeSum ch (n - 1) (acc + int64 v)
    }

let spawnAndSend () =
    let n = 200_000
    let ch = Ch<int>()

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    for i in 0 .. n - 1 do
        queue (Ch.give ch i)

    let total = run (takeSum ch n 0L)
    sw.Stop()

    report "Spawn+send" n sw (allocated () - before) (sprintf "total=%d" total)

// ---------------------------------------------------------------------------
// 3. Ping-pong
// ---------------------------------------------------------------------------

let rec private ponger (a: Ch<int>) (b: Ch<int>) n =
    job {
        if n > 0 then
            let! v = Ch.take a
            do! Ch.give b (v * 2)
            return! ponger a b (n - 1)
    }

let rec private pinger (a: Ch<int>) (b: Ch<int>) i n =
    job {
        if i < n then
            do! Ch.give a i
            let! _ = Ch.take b
            return! pinger a b (i + 1) n
    }

let pingPong () =
    let rounds = 1_000_000
    let a = Ch<int>()
    let b = Ch<int>()
    let finished = IVar<unit>()

    queue (ponger a b rounds >>= fun () -> IVar.tryFill finished ())

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    queue (pinger a b 0 rounds)
    run (IVar.read finished)
    sw.Stop()

    report "Ping-pong" rounds sw (allocated () - before) "round trips"

// ---------------------------------------------------------------------------
// 4. Ring
// ---------------------------------------------------------------------------

let mutable private ringRemaining = 0

let rec private ringNode (inCh: Ch<int>) (outCh: Ch<int>) isLast numTrips =
    job {
        let! msg = Ch.take inCh

        if msg = -1 then
            if not isLast then do! Ch.give outCh -1
            return ()
        elif isLast then
            let m = msg + 1
            if m >= numTrips then
                do! Ch.give outCh -1
                return! ringNode inCh outCh isLast numTrips
            else
                do! Ch.give outCh m
                return! ringNode inCh outCh isLast numTrips
        else
            do! Ch.give outCh msg
            return! ringNode inCh outCh isLast numTrips
    }

let private ringWorker inCh outCh isLast numTrips (finished: IVar<unit>) =
    job {
        do! ringNode inCh outCh isLast numTrips
        if Interlocked.Decrement(&ringRemaining) = 0 then
            do! IVar.tryFill finished ()
    }

let ring () =
    let numWorkers = 1000
    let numTrips = 1000
    let channels = Array.init numWorkers (fun _ -> Ch<int>())
    let finished = IVar<unit>()
    ringRemaining <- numWorkers

    for i in 0 .. numWorkers - 1 do
        let inCh = channels.[i]
        let outCh = channels.[(i + 1) % numWorkers]
        queue (ringWorker inCh outCh (i = numWorkers - 1) numTrips finished)

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    run (Ch.give channels.[0] 0)
    run (IVar.read finished)
    sw.Stop()

    report "Ring" (numWorkers * numTrips) sw (allocated () - before) "messages"

// ---------------------------------------------------------------------------
// 5. Select / Choose
// ---------------------------------------------------------------------------

let rec private selectSender (a: Ch<int>) (b: Ch<int>) i n =
    job {
        if i < n then
            if i % 2 = 0 then do! Ch.give a i else do! Ch.give b i
            return! selectSender a b (i + 1) n
    }

let rec private selectReceiver (choice: Alt<int>) n =
    job {
        if n > 0 then
            let! _ = choice
            return! selectReceiver choice (n - 1)
    }

let selectChoose () =
    let rounds = 1_000_000
    let a = Ch<int>()
    let b = Ch<int>()
    let finished = IVar<unit>()

    queue (selectSender a b 0 rounds >>= fun () -> IVar.tryFill finished ())

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    // Built once outside the loop, matching the C# which hoists Cml.Choose(a, b).
    let choice = Ch.take a <|> Ch.take b
    run (selectReceiver choice rounds)
    run (IVar.read finished)
    sw.Stop()

    report "Select/Choose" rounds sw (allocated () - before) "ops"

// ---------------------------------------------------------------------------
// 6. Fan-out
// ---------------------------------------------------------------------------

let mutable private sink = 0.0
let mutable private fanRemaining = 0

let private burn iterations =
    let mutable acc = 0.0
    for i in 1 .. iterations do
        acc <- acc + 1.0 / float i
    sink <- acc

let fanOut () =
    let numChildren = 480
    let iterations = 3_000_000

    let serialSw = Stopwatch.StartNew()
    burn iterations
    serialSw.Stop()
    let serialTotalMs = serialSw.Elapsed.TotalMilliseconds * float numChildren

    fanRemaining <- numChildren
    let finished = IVar<unit>()

    let child =
        job {
            burn iterations
            if Interlocked.Decrement(&fanRemaining) = 0 then
                do! IVar.tryFill finished ()
        }

    let sw = Stopwatch.StartNew()

    // Spawned from inside a job, mirroring FanOutParent in the C# version.
    run (job {
            for _ in 1 .. numChildren do
                do! Job.queue child
            do! IVar.read finished
         })

    sw.Stop()

    printfn "Fan-out:          %d children in %d ms (serial reference %.0f ms, speedup %.1fx)"
        numChildren sw.ElapsedMilliseconds serialTotalMs
        (serialTotalMs / sw.Elapsed.TotalMilliseconds)

// ---------------------------------------------------------------------------

let private warmup () =
    for _ in 1 .. 200 do
        let ch = Ch<int>()
        queue (Ch.give ch 1)
        run (Ch.take ch) |> ignore

/// One Select round, returning ns/op. Used by the repeated mode below.
let private selectOnce rounds =
    let a = Ch<int>()
    let b = Ch<int>()
    let finished = IVar<unit>()

    queue (selectSender a b 0 rounds >>= fun () -> IVar.tryFill finished ())

    let sw = Stopwatch.StartNew()
    let choice = Ch.take a <|> Ch.take b
    run (selectReceiver choice rounds)
    run (IVar.read finished)
    sw.Stop()

    sw.Elapsed.TotalMilliseconds * 1_000_000.0 / float rounds

/// Select measured in isolation with repetitions.
///
/// The full suite runs each benchmark exactly once, which on .NET means measuring a
/// partially tiered-JIT'd method. BjoML's Select moved from 211 ns/op single-shot to
/// 97 ns/op by the fifth repetition, so the single-shot cross-runtime comparison was
/// not measuring what it claimed to. This gives Hopac the same treatment.
let selectRepeated reps =
    selectOnce 200_000 |> ignore   // warm up

    let samples = Array.init reps (fun _ -> selectOnce 1_000_000)
    let sorted = Array.sort samples

    printfn "Select/Choose  per rep: %s"
        (samples |> Array.map (sprintf "%6.0f") |> String.concat " ")
    printfn "               median : %.0f ns/op" sorted.[reps / 2]

/// Run a benchmark back-to-back. Consecutive per benchmark rather than looping the
/// whole suite, so each one's own code path reaches tier-1-with-PGO before the next
/// starts. See the BjoML side for the reasoning; both suites must repeat the same way
/// or the comparison is not like-for-like.
let private repeat reps (body: unit -> unit) =
    for _ in 1 .. reps do body ()

let private mainSuite reps =
    printfn ".NET %O, ProcessorCount=%d, ServerGC=%b, Hopac %s, reps=%d"
        Environment.Version
        Environment.ProcessorCount
        System.Runtime.GCSettings.IsServerGC
        (typeof<Hopac.Job<int>>.Assembly.GetName().Version |> string)
        reps
    printfn ""

    warmup ()

    repeat reps spawnStorm
    repeat reps spawnStormInside
    repeat reps spawnAndSend
    repeat reps pingPong
    repeat reps ring
    repeat reps selectChoose
    repeat reps fanOut
    0

[<EntryPoint>]
let main argv =
    if argv |> Array.contains "varied" then
        // Same --reps convention as the main suite.
        let reps =
            match Array.tryFindIndex ((=) "--reps") argv with
            | Some i when i + 1 < argv.Length -> int argv.[i + 1]
            | _ -> 1
        Varied.run reps
        0
    elif argv |> Array.contains "select" then
        let reps = if argv.Length > 1 then int argv.[1] else 9
        printfn ".NET %O, ProcessorCount=%d, ServerGC=%b, reps=%d"
            Environment.Version Environment.ProcessorCount
            System.Runtime.GCSettings.IsServerGC reps
        printfn ""
        warmup ()
        selectRepeated reps
        0
    else
        // --reps N, matching bench/Bench. Default 1 preserves the historical
        // single-shot behaviour.
        let reps =
            match Array.tryFindIndex ((=) "--reps") argv with
            | Some i when i + 1 < argv.Length -> int argv.[i + 1]
            | _ -> 1
        mainSuite reps
