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

// A sharded key-value service under load: the Hopac twin of
// bench/service/go/main.go, whose header has the specification. Same counts,
// same channels, same work, same checksum.
//
//     dotnet bin/Release/net10.0/ServiceHopac.dll [REPS [CLIENTS [REQUESTS]]]

module ServiceHopac.Program

open System
open System.Diagnostics
open Hopac
open Hopac.Infixes

[<Literal>]
let Shards = 16

[<Literal>]
let SlotsPerShard = 1024

[<Literal>]
let KeySpace = 16384

[<Literal>]
let DecodeWork = 100

[<Literal>]
let ServeWork = 20

[<Literal>]
let DeadlineMs = 1000

[<Literal>]
let OpGet = 0

[<Literal>]
let OpPut = 1

type Req = { Op: int; Key: int; Value: int; Reply: Ch<int> }

type ClientOut = { Checksum: int64; Samples: ResizeArray<int64>; Timeouts: int }

let nsSince (t0: int64) =
    (Stopwatch.GetTimestamp() - t0) * 1_000_000_000L / Stopwatch.Frequency

let cpuMs () =
    int64 (Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds)

let crunch n seed =
    let mutable x = seed
    for i in 0 .. n - 1 do
        x <- (x * 31 + i) % 1000003
    x

/// Serves its slots until told to stop, and fills in the work it did.
let shard (id: int) (requests: Ch<Req>) (quit: Ch<int>) (result: IVar<int64>) : Job<unit> =
    let slots = Array.init SlotsPerShard (fun j -> (j * Shards + id) * 7)
    let rec serve (work: int64) : Job<unit> =
        (Ch.take requests ^=> fun r ->
            let w = int64 (crunch ServeWork r.Key)
            let slot = r.Key / Shards
            let old = slots.[slot]
            if r.Op = OpPut then slots.[slot] <- r.Value
            Ch.give r.Reply old >>= fun () -> serve (work + w))
        <|> (Ch.take quit ^=> fun _ -> IVar.fill result work)
        :> Job<unit>
    serve 0L

/// One request and its answer, or None at the deadline.
let request (chans: Ch<Req>[]) op key value : Job<int option> =
    let reply = Ch<int>()
    Ch.give chans.[key % Shards] { Op = op; Key = key; Value = value; Reply = reply } >>= fun () ->
        (Ch.take reply ^-> Some) <|> (timeOutMillis DeadlineMs ^->. None)

let subGet chans key (results: Ch<int>) : Job<unit> =
    request chans OpGet key 0 >>= fun r ->
        Ch.give results (match r with Some v -> v | None -> -1)

let rec private spawnGets chans key results j : Job<unit> =
    if j = 4 then Job.unit ()
    else Job.queue (subGet chans ((key + j) % KeySpace) results) >>= fun () ->
         spawnGets chans key results (j + 1)

let rec private gather (results: Ch<int>) j sum lost : Job<struct (int * int)> =
    if j = 4 then Job.result (struct (sum, lost))
    else Ch.take results >>= fun v ->
         if v < 0 then gather results (j + 1) sum (lost + 1)
         else gather results (j + 1) (sum + v) lost

/// Four keys from four queued jobs: the sum of the answers, and how many
/// missed their deadline.
let multiGet chans key results : Job<struct (int * int)> =
    spawnGets chans key results 0 >>= fun () -> gather results 0 0 0

let single chans op key : Job<struct (int * int)> =
    request chans op key (key * 7) >>- function
        | Some v -> struct (v, 0)
        | None -> struct (0, 1)

let client c requests chans : Job<ClientOut> =
    let results = Ch<int>()
    let samples = ResizeArray<int64>(requests / 8 + 1)
    let rec go i (s: int64) (checksum: int64) timeouts : Job<ClientOut> =
        if i = requests then
            Job.result { Checksum = checksum; Samples = samples; Timeouts = timeouts }
        else
            let sampled = i % 8 = 0
            let t0 = if sampled then Stopwatch.GetTimestamp() else 0L
            let s2 = (s * 1103515245L + 12345L) % 2147483648L
            let key = int (s2 % int64 KeySpace)
            let d = crunch DecodeWork key
            let outcome =
                if i % 10 = 9 then multiGet chans key results
                else single chans (if i % 5 = 0 then OpPut else OpGet) key
            outcome >>= fun (struct (answer, lost)) ->
                if sampled then samples.Add(nsSince t0)
                go (i + 1) s2 (checksum + int64 (answer + d % 3)) (timeouts + lost)
    go 0 (int64 (c + 1)) 0L 0

let percentile (sorted: ResizeArray<int64>) per over = sorted.[sorted.Count * per / over]

let rep n clients requests =
    let chans = Array.init Shards (fun _ -> Ch<Req>())
    let quits = Array.init Shards (fun _ -> Ch<int>())
    let works = Array.init Shards (fun _ -> IVar<int64>())
    for i in 0 .. Shards - 1 do
        queue (shard i chans.[i] quits.[i] works.[i])
    GC.Collect()
    GC.WaitForPendingFinalizers()
    GC.Collect()

    let a0 = GC.GetTotalAllocatedBytes true
    let t0 = Stopwatch.GetTimestamp()
    let c0 = cpuMs ()
    let outs = Array.init clients (fun _ -> IVar<ClientOut>())
    for c in 0 .. clients - 1 do
        queue (client c requests chans >>= IVar.fill outs.[c])
    let gathered = run (job {
        let all = ResizeArray<ClientOut>(clients)
        for iv in outs do
            let! o = IVar.read iv
            all.Add o
        return all })
    let wall = nsSince t0 / 1_000_000L
    let cpu = cpuMs () - c0
    let alloc = GC.GetTotalAllocatedBytes true - a0

    run (job { for q in quits do do! Ch.give q 0 })
    let work = run (job {
        let mutable total = 0L
        for iv in works do
            let! w = IVar.read iv
            total <- total + w
        return total })

    let samples = ResizeArray<int64>()
    let mutable checksum = work
    let mutable timeouts = 0
    for o in gathered do
        checksum <- checksum + o.Checksum
        timeouts <- timeouts + o.Timeouts
        samples.AddRange o.Samples
    samples.Sort()

    if n > 0 then
        printfn "rep %d wall_ms %d cpu_ms %d p50_ns %d p99_ns %d p999_ns %d timeouts %d checksum %d alloc_b %d"
            n wall cpu (percentile samples 50 100) (percentile samples 99 100)
            (percentile samples 999 1000) timeouts checksum alloc

[<EntryPoint>]
let main argv =
    let arg i fallback = if argv.Length > i then int argv.[i] else fallback
    let reps = arg 0 5
    let clients = arg 1 1000
    let requests = arg 2 1000
    printfn "hopac ProcessorCount=%d clients=%d requests=%d" Environment.ProcessorCount clients requests
    for n in 0 .. reps do
        rep n clients requests
    0
