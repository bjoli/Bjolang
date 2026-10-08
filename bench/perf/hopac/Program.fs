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

// One row of bench/hopac/Cmlbench.fs, chosen by argument, run three times as a
// warm-up and then REPS times: `PerfHopac ring|choose REPS`. The twin of
// bench/perf/bjolang/rows.bjo, for bench/perf/drive.py. The ring has no cancel
// race, which is Hopac at its fastest; the Bjolang ring always races its
// scope's token.
module PerfHopac.Program

open System
open System.Threading
open Hopac
open Hopac.Infixes

type Latch(n: int) =
    let mutable remaining = n
    member val Finished = IVar<unit>()
    member this.Arrive() =
        job {
            if Interlocked.Decrement(&remaining) = 0 then
                do! IVar.tryFill this.Finished ()
        }

let settle () =
    GC.Collect()
    GC.WaitForPendingFinalizers()
    GC.Collect()

let rec ringNode (inCh: Ch<int>) (outCh: Ch<int>) isLast trips =
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

let ring workers trips =
    let chs = Array.init workers (fun _ -> Ch<int>())
    let latch = Latch(workers)
    run (job {
            for i in 0 .. workers - 1 do
                do! Job.queue (ringNode chs.[i] chs.[(i + 1) % workers] (i = workers - 1) trips >>= latch.Arrive)
         })
    settle ()
    run (job {
            do! Job.queue (Ch.give chs.[0] 0)
            do! IVar.read latch.Finished
         })

let rec sender (ch: Ch<int>) i n =
    job {
        if i < n then
            do! Ch.give ch i
            return! sender ch (i + 1) n
    }

let rec receiver (choice: Alt<int>) n =
    job {
        if n > 0 then
            let! _ = choice
            return! receiver choice (n - 1)
    }

let skewed rounds =
    let chs = Array.init 8 (fun _ -> Ch<int>())
    let senderDone = IVar<unit>()
    run (Job.queue (sender chs.[0] 0 rounds >>= fun () -> IVar.tryFill senderDone ()))
    let choice = Alt.choose (Array.map Ch.take chs)
    settle ()
    let receiverDone = IVar<unit>()
    run (job {
            do! Job.queue (receiver choice rounds >>= fun () -> IVar.tryFill receiverDone ())
            do! IVar.read receiverDone
            do! IVar.read senderDone
         })

let runRow row n size =
    for _ in 1 .. n do
        match row with
        | "ring" -> ring 1000 size
        | _ -> skewed (1000 * size)

[<EntryPoint>]
let main argv =
    let row = argv.[0]
    let reps = int argv.[1]
    runRow row 3 1000
    runRow row reps 1000
    0
