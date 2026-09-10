// The "varied" suite: shapes the main suite never exercises, hunting dark spots.
// Twin of bench/Bench/Varied.cs and bench/go/varied.go — same counts, same
// channel topology, same work per unit.
//
// Run with: dotnet run -c Release -- varied --reps 5

module HopacBench.Varied

open System
open System.Diagnostics
open System.Threading
open Hopac
open Hopac.Infixes

let private report (name: string) (n: int) (sw: Stopwatch) (alloc: int64) (extra: string) =
    let nsPerOp = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / float n
    printfn "%-17s %d in %d ms (%.0f ns/op, %.0f B/op), %s"
        name n sw.ElapsedMilliseconds nsPerOp (float alloc / float n) extra

let private allocated () = GC.GetTotalAllocatedBytes true

// ---------------------------------------------------------------------------
// Shared recursive loops
// ---------------------------------------------------------------------------

let rec private sendN (ch: Ch<int>) n =
    job {
        if n > 0 then
            do! Ch.give ch 1
            return! sendN ch (n - 1)
    }

let rec private takeN (ch: Ch<int>) n =
    job {
        if n > 0 then
            let! _ = Ch.take ch
            return! takeN ch (n - 1)
    }

let rec private takeSum (ch: Ch<int>) n (acc: int64) =
    job {
        if n = 0 then
            return acc
        else
            let! v = Ch.take ch
            return! takeSum ch (n - 1) (acc + int64 v)
    }

let rec private chooseReceiver (choice: Alt<int>) n =
    job {
        if n > 0 then
            let! _ = choice
            return! chooseReceiver choice (n - 1)
    }

// ---------------------------------------------------------------------------
// 1. Fan-in: 100 long-lived producers, one channel, one consumer.
// ---------------------------------------------------------------------------

let fanIn () =
    let producers = 100
    let perProducer = 10_000
    let n = producers * perProducer
    let ch = Ch<int>()

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    for _ in 1 .. producers do
        queue (sendN ch perProducer)

    let total = run (takeSum ch n 0L)
    sw.Stop()

    report "Fan-in" n sw (allocated () - before) (sprintf "total=%d" total)

// ---------------------------------------------------------------------------
// 2. Many-to-many: 100 producers, 100 consumers, one channel.
// ---------------------------------------------------------------------------

let mutable private m2mRemaining = 0

let manyToMany () =
    let side = 100
    let per = 10_000
    let n = side * per
    let ch = Ch<int>()
    let finished = IVar<unit>()
    m2mRemaining <- 2 * side

    let worker (body: Job<unit>) =
        job {
            do! body
            if Interlocked.Decrement(&m2mRemaining) = 0 then
                do! IVar.tryFill finished ()
        }

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    for _ in 1 .. side do
        queue (worker (sendN ch per))
        queue (worker (takeN ch per))

    run (IVar.read finished)
    sw.Stop()

    report "Many-to-many" n sw (allocated () - before) "msgs"

// ---------------------------------------------------------------------------
// 3./4. Wide and skewed choose over 8 channels.
// ---------------------------------------------------------------------------

let rec private wideSender (chs: Ch<int>[]) i n skewed =
    job {
        if i < n then
            let ch = if skewed then chs.[0] else chs.[i &&& 7]
            do! Ch.give ch i
            return! wideSender chs (i + 1) n skewed
    }

let private wideChooseCore name skewed =
    let k = 8
    let rounds = 1_000_000
    let chs = Array.init k (fun _ -> Ch<int>())
    let finished = IVar<unit>()

    queue (wideSender chs 0 rounds skewed >>= fun () -> IVar.tryFill finished ())

    // Hoisted, like the C# Cml.Choose(evs).
    let choice = Alt.choose (Array.map Ch.take chs)

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    run (chooseReceiver choice rounds)
    run (IVar.read finished)
    sw.Stop()

    report name rounds sw (allocated () - before) "ops"

let wideChoose () = wideChooseCore "Wide choose (8)" false
let skewedChoose () = wideChooseCore "Skewed choose(8)" true

// ---------------------------------------------------------------------------
// 5. Choose send: alt over two gives, two receivers draining.
// ---------------------------------------------------------------------------

let mutable private csCount = 0L
let mutable private csRemaining = 0

let rec private drain (ch: Ch<int>) (finished: IVar<unit>) =
    job {
        let! v = Ch.take ch
        if v = -1 then
            if Interlocked.Decrement(&csRemaining) = 0 then
                do! IVar.tryFill finished ()
        else
            Interlocked.Increment(&csCount) |> ignore
            return! drain ch finished
    }

let rec private chooseSendLoop (choice: Alt<unit>) n =
    job {
        if n > 0 then
            do! choice
            return! chooseSendLoop choice (n - 1)
    }

let chooseSend () =
    let rounds = 1_000_000
    let a = Ch<int>()
    let b = Ch<int>()
    let finished = IVar<unit>()
    csCount <- 0L
    csRemaining <- 2

    queue (drain a finished)
    queue (drain b finished)

    let choice = Ch.give a 1 <|> Ch.give b 1

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    run (job {
            do! chooseSendLoop choice rounds
            do! Ch.give a (-1)
            do! Ch.give b (-1)
         })
    run (IVar.read finished)
    sw.Stop()

    report "Choose send (2)" rounds sw (allocated () - before)
        (sprintf "received=%d" (Interlocked.Read(&csCount)))

// ---------------------------------------------------------------------------
// 6. Choose + timeout: alt(take, timeOutMillis 1000), take always wins.
// ---------------------------------------------------------------------------

let rec private plainSender (ch: Ch<int>) i n =
    job {
        if i < n then
            do! Ch.give ch i
            return! plainSender ch (i + 1) n
    }

let chooseTimeout () =
    let rounds = 100_000
    let ch = Ch<int>()
    let finished = IVar<unit>()

    queue (plainSender ch 0 rounds >>= fun () -> IVar.tryFill finished ())

    let choice = Ch.take ch <|> (timeOutMillis 1000 ^->. (-1))

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    run (chooseReceiver choice rounds)
    run (IVar.read finished)
    sw.Stop()

    report "Choose+timeout" rounds sw (allocated () - before) "ops"

// ---------------------------------------------------------------------------
// 7. Pipeline: source -> 4 stages -> sink, 500k items.
// ---------------------------------------------------------------------------

let rec private pipeSource (out: Ch<int>) i n =
    job {
        if i < n then
            do! Ch.give out i
            return! pipeSource out (i + 1) n
        else
            do! Ch.give out (-1)
    }

let rec private pipeStage (inCh: Ch<int>) (out: Ch<int>) =
    job {
        let! v = Ch.take inCh
        if v = -1 then
            do! Ch.give out (-1)
        else
            do! Ch.give out (v + 1)
            return! pipeStage inCh out
    }

let rec private pipeSink (inCh: Ch<int>) (acc: int64) (result: IVar<int64>) =
    job {
        let! v = Ch.take inCh
        if v = -1 then
            do! IVar.tryFill result acc
        else
            return! pipeSink inCh (acc + int64 v) result
    }

let pipeline () =
    let stages = 4
    let items = 500_000
    let chs = Array.init (stages + 1) (fun _ -> Ch<int>())
    let result = IVar<int64>()

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    queue (pipeSource chs.[0] 0 items)
    for s in 0 .. stages - 1 do
        queue (pipeStage chs.[s] chs.[s + 1])
    queue (pipeSink chs.[stages] 0L result)

    let total = run (IVar.read result)
    sw.Stop()

    report "Pipeline (4)" items sw (allocated () - before) (sprintf "total=%d" total)

// ---------------------------------------------------------------------------
// 8. Parallel ping-pong: 12 independent pairs.
// ---------------------------------------------------------------------------

let mutable private ppRemaining = 0

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

let parallelPingPong () =
    let pairs = 12
    let rounds = 100_000
    let n = pairs * rounds
    let finished = IVar<unit>()
    ppRemaining <- 2 * pairs

    let track (body: Job<unit>) =
        job {
            do! body
            if Interlocked.Decrement(&ppRemaining) = 0 then
                do! IVar.tryFill finished ()
        }

    let before = allocated ()
    let sw = Stopwatch.StartNew()

    for _ in 1 .. pairs do
        let a = Ch<int>()
        let b = Ch<int>()
        queue (track (ponger a b rounds))
        queue (track (pinger a b 0 rounds))

    run (IVar.read finished)
    sw.Stop()

    report "Par ping-pong(12)" n sw (allocated () - before) "round trips"

// ---------------------------------------------------------------------------

let private warmup () =
    for _ in 1 .. 200 do
        let ch = Ch<int>()
        queue (Ch.give ch 1)
        run (Ch.take ch) |> ignore

let run (reps: int) =
    printfn ".NET %O, ProcessorCount=%d, ServerGC=%b, Hopac %s, reps=%d"
        Environment.Version
        Environment.ProcessorCount
        System.Runtime.GCSettings.IsServerGC
        (typeof<Hopac.Job<int>>.Assembly.GetName().Version |> string)
        reps
    printfn ""

    warmup ()

    let repeat body = for _ in 1 .. reps do body ()

    repeat fanIn
    repeat manyToMany
    repeat wideChoose
    repeat skewedChoose
    repeat chooseSend
    repeat chooseTimeout
    repeat pipeline
    repeat parallelPingPong
