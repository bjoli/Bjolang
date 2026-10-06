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

// Twin of bench/bjolang/cmlbench.bjo and of `go run . -suite cmlbench`: same
// rows, same counts, same topology, same timed regions, and the same report
// (minimum of five reps, median beside it, bytes and gen-0 collections per op).
//
// Run with: dotnet run -c Release -- cmlbench
//
// Every Bjolang sync races the cancellation token of its scope. Hopac has no
// ambient token; a job that can be stopped races its operations against an
// IVar by hand, as a Go program selects against ctx.Done(). So the rows that
// have a token in Bjolang are run twice here, plain and with that race. The
// ring is run a third time with an IVar for each job, because one IVar shared
// by a thousand jobs is slow in Hopac for a reason of its own (see
// `ringPerNode`).
//
// Hopac has no scope either. The nested-scope row's twin is what a scope is
// written as: the jobs started inside the timed region, each racing a cancel
// IVar of the row's own, a countdown that fills an IVar when the last one is
// done, and the cancel IVar filled at the end.
//
// Spawns are `Job.queue` from inside a job: `start` would run the job inline,
// which is a call rather than a spawn, and queueing from outside the scheduler
// crosses a shared queue Go and Bjolang do not (see Program.fs).

module HopacBench.Cmlbench

open System
open System.Diagnostics
open System.Threading
open Hopac
open Hopac.Infixes

type private Rep = { Ns: float; Bytes: int64; Gcs: int }

let private settle () =
    GC.Collect()
    GC.WaitForPendingFinalizers()
    GC.Collect()

/// Settles the heap, then times `body`, counting what it allocated and how many
/// gen-0 collections ran.
let private measured (body: unit -> unit) (ops: int) =
    settle ()
    let gc0 = GC.CollectionCount 0
    let before = GC.GetTotalAllocatedBytes true
    let sw = Stopwatch.StartNew()
    body ()
    sw.Stop()
    { Ns = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / float ops
      Bytes = (GC.GetTotalAllocatedBytes true - before) / int64 ops
      Gcs = GC.CollectionCount 0 - gc0 }

let private report (name: string) (reps: Rep list) =
    let ns = reps |> List.map (fun r -> r.Ns) |> List.sort
    // Bytes are the median rather than the minimum: under the server GC the
    // allocation counter now and then reads lower after a rep than before it,
    // and the minimum would pick out that one negative reading.
    let bytes = reps |> List.map (fun r -> r.Bytes) |> List.sort
    let gcs = reps |> List.map (fun r -> r.Gcs) |> List.sort
    let mid = List.length reps / 2
    printfn "%-26s%8.0f%10d   (median %.0f, gc0 %d/rep)" name (List.head ns) bytes.[mid] ns.[mid] gcs.[mid]

/// A countdown that fills `finished` when the last of `n` arrivals is in.
type private Latch(n: int) =
    let mutable remaining = n
    member val Finished = IVar<unit>()
    member this.Arrive() =
        job {
            if Interlocked.Decrement(&remaining) = 0 then
                do! IVar.tryFill this.Finished ()
        }

// ---------------------------------------------------------------------------
// 1. Ring — 1000 nodes, 1000 trips, a million rendezvous.
// ---------------------------------------------------------------------------

let rec private ringNode (inCh: Ch<int>) (outCh: Ch<int>) isLast trips =
    job {
        let! msg = Ch.take inCh
        if msg = -1 then
            if not isLast then do! Ch.give outCh -1
        elif isLast then
            let m = msg + 1
            if m >= trips then do! Ch.give outCh -1 else do! Ch.give outCh m
            return! ringNode inCh outCh isLast trips
        else
            do! Ch.give outCh msg
            return! ringNode inCh outCh isLast trips
    }

/// The same node with every operation racing `cancel`, which is what each
/// sync in the Bjolang node does with the scope's token.
let rec private ringNodeC (cancel: IVar<unit>) (inCh: Ch<int>) (outCh: Ch<int>) isLast trips =
    let send v = (Ch.give outCh v ^->. true) <|> (IVar.read cancel ^->. false)
    job {
        let! got = (Ch.take inCh ^-> Some) <|> (IVar.read cancel ^->. None)
        match got with
        | None -> ()
        | Some msg ->
            if msg = -1 then
                if not isLast then
                    let! _ = send -1
                    ()
            elif isLast then
                let m = msg + 1
                let! sent = send (if m >= trips then -1 else m)
                if sent then return! ringNodeC cancel inCh outCh isLast trips
            else
                let! sent = send msg
                if sent then return! ringNodeC cancel inCh outCh isLast trips
    }

let private channels workers = Array.init workers (fun _ -> Ch<int>())

/// The nodes are started before the timed region, the kick-off is a job of its
/// own, and the region ends when every node has finished.
let private ringJoined workers trips (cancel: IVar<unit> option) =
    let chs = channels workers
    let latch = Latch(workers)
    run (job {
            for i in 0 .. workers - 1 do
                let node =
                    match cancel with
                    | None -> ringNode chs.[i] chs.[(i + 1) % workers] (i = workers - 1) trips
                    | Some c -> ringNodeC c chs.[i] chs.[(i + 1) % workers] (i = workers - 1) trips
                do! Job.queue (node >>= latch.Arrive)
         })
    measured (fun () ->
        run (job {
                do! Job.queue (Ch.give chs.[0] 0)
                do! IVar.read latch.Finished
             }))
        (workers * trips)

/// The cancellable ring with a cancel IVar for each node instead of one shared
/// by all. Stopping the ring then means filling a thousand IVars, but the row
/// separates what the race costs from what sharing one IVar costs: every
/// `IVar.read` of an unfilled IVar queues a taker on it under the IVar's lock,
/// and with one IVar for the whole ring every operation of every node takes
/// that same lock.
let private ringPerNode workers trips =
    let chs = channels workers
    let latch = Latch(workers)
    run (job {
            for i in 0 .. workers - 1 do
                let node = ringNodeC (IVar()) chs.[i] chs.[(i + 1) % workers] (i = workers - 1) trips
                do! Job.queue (node >>= latch.Arrive)
         })
    measured (fun () ->
        run (job {
                do! Job.queue (Ch.give chs.[0] 0)
                do! IVar.read latch.Finished
             }))
        (workers * trips)

/// The nested-scope row: started inside the timed region, under a cancel IVar
/// of its own, waited for, and cancelled at the end.
let private ringNestedScope workers trips =
    let chs = channels workers
    measured (fun () ->
        let cancel = IVar<unit>()
        let latch = Latch(workers + 1)
        run (job {
                for i in 0 .. workers - 1 do
                    let node = ringNodeC cancel chs.[i] chs.[(i + 1) % workers] (i = workers - 1) trips
                    do! Job.queue (node >>= latch.Arrive)
                do! Job.queue (((Ch.give chs.[0] 0) <|> IVar.read cancel) >>= latch.Arrive)
                do! IVar.read latch.Finished
                do! IVar.tryFill cancel ()
             }))
        (workers * trips)

// ---------------------------------------------------------------------------
// 2. Spawn burst — a million jobs that do nothing.
// ---------------------------------------------------------------------------

let private spawnBurst n =
    measured (fun () ->
        let latch = Latch(n)
        let nothing = latch.Arrive()
        run (job {
                for _ in 1 .. n do
                    do! Job.queue nothing
                do! IVar.read latch.Finished
             }))
        n

// ---------------------------------------------------------------------------
// 3. Skewed choose — 8 branches, only the first ever fires.
// ---------------------------------------------------------------------------

let rec private sender (ch: Ch<int>) i n =
    job {
        if i < n then
            do! Ch.give ch i
            return! sender ch (i + 1) n
    }

let rec private receiver (choice: Alt<int>) n =
    job {
        if n > 0 then
            let! _ = choice
            return! receiver choice (n - 1)
    }

/// The sender starts outside the timed region; the receiver is a job of its
/// own, started and waited for inside it. The choice is built once, as the
/// Bjolang receiver builds its `choose` once.
let private skewedChoose rounds (cancel: IVar<unit> option) =
    let chs = channels 8
    let senderDone = IVar<unit>()
    run (Job.queue (sender chs.[0] 0 rounds >>= fun () -> IVar.tryFill senderDone ()))

    let branches = Array.map Ch.take chs
    let choice =
        match cancel with
        | None -> Alt.choose branches
        | Some c -> Alt.choose (Array.append branches [| IVar.read c ^->. -1 |])

    measured (fun () ->
        let receiverDone = IVar<unit>()
        run (job {
                do! Job.queue (receiver choice rounds >>= fun () -> IVar.tryFill receiverDone ())
                do! IVar.read receiverDone
                do! IVar.read senderDone
             }))
        rounds

// ---------------------------------------------------------------------------

let run () =
    let reps = 5
    // A cancel IVar nothing fills: the equivalent of `main`'s scope.
    let root = IVar<unit>()

    printfn ".NET %O, ProcessorCount=%d, ServerGC=%b, Hopac %s"
        Environment.Version
        Environment.ProcessorCount
        System.Runtime.GCSettings.IsServerGC
        (typeof<Hopac.Job<int>>.Assembly.GetName().Version |> string)
    printfn ""
    printfn "%-26s%8s%10s   (min of %d)" "benchmark" "ns/op" "B/op" reps

    // Warm-up, so the first measured rep is not paying for tier-0 code.
    ringJoined 100 100 None |> ignore
    ringJoined 100 100 (Some root) |> ignore
    ringNestedScope 100 100 |> ignore
    ringPerNode 100 100 |> ignore
    spawnBurst 50_000 |> ignore
    skewedChoose 50_000 None |> ignore
    skewedChoose 50_000 (Some root) |> ignore

    let times f = List.init reps (fun _ -> f ())
    report "Ring" (times (fun () -> ringJoined 1000 1000 None))
    report "Ring (cancellable)" (times (fun () -> ringJoined 1000 1000 (Some root)))
    report "Ring (nested scope)" (times (fun () -> ringNestedScope 1000 1000))
    report "Ring (cancel IVar per job)" (times (fun () -> ringPerNode 1000 1000))
    report "Spawn burst" (times (fun () -> spawnBurst 1_000_000))
    report "Skewed choose(8)" (times (fun () -> skewedChoose 1_000_000 None))
    report "Skewed choose(8)+cancel" (times (fun () -> skewedChoose 1_000_000 (Some root)))
