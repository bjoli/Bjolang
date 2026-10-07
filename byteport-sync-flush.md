# Byte output ports: closing does blocking I/O

## The problem

`BjoByteOutputPort.Dispose()` (`BjolangRuntime/BjoBytePort.cs`, around line 1238),
unless the port has been `shutdown!`, does:

1. `DrainSync()`: a blocking `_inner.Write` of whatever is still buffered.
2. `_inner.Flush()`: a blocking flush, always, even when nothing was written.

Closing is synchronous because a scope's releases are plain thunks and cannot
suspend. Kestrel's streams (the response body, and the raw stream
`IHttpUpgradeFeature.UpgradeAsync` hands over) refuse blocking I/O by default and
throw `InvalidOperationException: Synchronous operations are disallowed`. So
closing a byte port over a Kestrel stream throws, even with nothing to flush.

## How it showed up

In `(net websocket protocol)` (github.com/bjoli/websocket, `src/protocol.bjo`,
`run-connection`), the close threw and skipped `(inbox-close! finished)`, so
`websocket-close!` waited forever, the Kestrel request was never answered, and
`stop-server!` hung.

Workaround there: `run-connection`'s `#:finally` calls `(shutdown! out)` first,
which drains and flushes asynchronously and sets `_writeClosed`, so `Dispose`
skips both blocking calls. Every close step is wrapped in `try`, so `finished`
always closes. The workaround can go once this is fixed.

## Why it matters beyond websockets

- Streaming response bodies in bjoweb would be byte ports over Kestrel's
  response stream, and every handler would have to `shutdown!` before closing.
- A port released by its scope (never closed by the program) is disposed the
  same way. Over a Kestrel stream the release throws and is reported as a
  failure of the scope.
- It affects any stream that refuses blocking I/O, not only Kestrel's.

## The fix

1. **Do no I/O on close when nothing is pending.** Track whether bytes have
   reached `_inner` since its last flush (set in `DrainSync`/`DrainAsync` and
   the write paths, cleared in `Flush`/`FlushValueAsync`). `Dispose` drains and
   flushes only when there is something to drain or flush. `flush!` already
   flushes `_inner` asynchronously (`FlushValueAsync`), so flush-then-close does
   no blocking I/O. No API change.
2. **An async close.** Give `BjoByteOutputPort` a `DisposeAsync` (async drain
   and flush, then the same release as `Dispose`). Make
   `close-byte-output-port!` in `lib/std/ports.bjo` a `defbjouble`, like
   `flush!` and `shutdown!`: the blocking form in a plain function, the
   suspending form in a fiber. Needs a `BytePorts.CloseOutputAsync` and an
   async counterpart of `BjolangRuntime.CloseByteOutput` (`Scope.cs`, around
   line 1390) that releases through the owner handle the same way.
3. **What is left:** a port released by its scope with unflushed bytes over a
   stream that forbids blocking I/O. Releases cannot suspend, so those bytes
   cannot be written. Report it, as now, but always release the handle, which
   the existing `finally` in `Dispose` already does.

## Test

.NET has no stream that refuses blocking I/O, so the test needs a small
`Stream` subclass that throws on `Write`/`Flush` and works on
`WriteAsync`/`FlushAsync`, next to the runtime's C# tests
(`BjolangRuntime/Cml/Tests`). Cases:

- write, `flush!`, close: no exception
- write, async close: bytes arrive, no exception
- write, blocking close with bytes pending: reported, handle still released

A `TestFiles/` fixture can cover the Bjolang side if the test stream is
reachable from Bjolang.

## Build order

Runtime, then the compiler (`bjor` rebuilds it when `BjolangRuntime/*.cs` is
newer), then `./build_std.sh`, then `./run_tests.py`.

## Afterwards

- Drop the `shutdown!`-before-close workaround in the websocket package's
  `run-connection`, or keep only the `try` guards, and tag a new version.
- Update the note in `CHANGELOG.org` under `stream->ports`, which says this is
  not fixed.
