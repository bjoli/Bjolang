// Fetch the given urls, at most 8 at a time, and stop once 15 have landed.
//
//   dotnet fsi Examples/fetch15/first-15.fsx https://example.com https://...
//
// The Bjolang and Clojure versions beside this one build a feeder, a crew of
// workers and a channel between them, because that is what their concurrency
// primitives are. .NET ships the whole shape as one call: Parallel.ForEachAsync
// walks the source, keeps a fixed number of them in flight, and stops when its
// token fires.

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Threading
open System.Threading.Tasks

let [<Literal>] Wanted = 15
let [<Literal>] AtOnce = 8

let client = new HttpClient(Timeout = TimeSpan.FromSeconds 15.0)

/// Reads the whole body and answers whether it arrived.
///
/// The exception filter lets OperationCanceledException past: it is the stop
/// signal rather than a failed request, and swallowing it here would keep a
/// cancelled worker fetching.
let landed (url: string) (token: CancellationToken) =
    task {
        try
            let! response = client.GetAsync(url, token)
            let! _ = response.Content.ReadAsStringAsync token
            return true
        with e when not (e :? OperationCanceledException) ->
            return false
    }

/// The first `wanted` urls to answer, in the order they answered. Fewer if too
/// many fail: `ForEachAsync` returns when the urls run out.
let firstToLand (urls: string[]) wanted atOnce =
    task {
        let arrived = ConcurrentQueue<string>()
        use stop = new CancellationTokenSource()
        let options = ParallelOptions(MaxDegreeOfParallelism = atOnce, CancellationToken = stop.Token)

        // Cancelling from inside the body is how the loop is stopped early, so
        // the call it stops throws — which is the expected way out, not a
        // failure. `ForEachAsync` has already waited for the requests still in
        // flight by the time it does.
        try
            do!
                Parallel.ForEachAsync(
                    urls,
                    options,
                    fun url token ->
                        ValueTask(
                            task {
                                let! ok = landed url token

                                if ok then
                                    arrived.Enqueue url

                                    if arrived.Count >= wanted then
                                        stop.Cancel()
                            }
                        )
                )
        with :? OperationCanceledException ->
            ()

        // More than `wanted` can arrive: the ones already in flight when the
        // token fires still finish. The queue is in arrival order, so the first
        // `wanted` of it is the answer either way.
        return arrived |> Seq.truncate wanted |> List.ofSeq
    }

let urls = fsi.CommandLineArgs |> Array.skip 1

for url in (firstToLand urls Wanted AtOnce).GetAwaiter().GetResult() do
    printfn "%s" url
