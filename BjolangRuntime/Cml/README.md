# The CML layer

*Note: This README was mostly written by an AI assistant.*

A high-performance, mostly lock-free implementation of Reppy's Concurrent ML
(CML) primitive abstractions in C#. It was a separate project called BjoML,
under LGPL-3.0; it is now part of Bjolang and under Bjolang's licence. The
namespace is still `Bjoml`.

**Important:** This is **not intended to be used directly from C# by human developers**. It is specifically designed as a compilation target for a custom language that compiles down to C# state machines (bjoroutines). It is mostly "vibe-coded", but based on some of my own code from earlier. Only very small chunks of my original code remain, and performance is about an order of magnitude better. If you try to use this, note that AsyncLocals will not work, at least not in future releases. 

## The Fiber API

A compiled bjoroutine returns `Fiber` / `Fiber<T>`, driven by a custom
`AsyncMethodBuilder`. `(sync ev)` compiles to `await ev`, and nothing else in the
language suspends:

```csharp
static async Fiber Echo(Channel<int> inCh, Channel<int> outCh)
{
    while (true)
    {
        int x = await inCh.Receive();   // (sync (channel-get in))
        await outCh.Send(x + 1);        // (sync (channel-put out ...))
    }
}
```

`Bjo.Spawn` returns a `Promise<T>`, **not** a `Fiber<T>`. A fiber is a compiler
artifact; a promise is first-class and composable with `choose`, which a `Task` can
never be:

```csharp
var r = await Cml.Choose(
    Cml.Wrap(p.Join(),        _ => "done"),
    Cml.Wrap(abort.Receive(), _ => "aborted"));
```

The builder carries the language's dynamic environment in `FiberContext.Current`, an
opaque `[ThreadStatic] object?` that BjoML only ever saves and restores — a pointer
swap. It is re-captured at every suspension and reinstated on resume, with the
borrowed thread's own context restored in a `finally`.

**ExecutionContext is never flowed anywhere in the runtime** — every enqueue is an
`UnsafeQueueUserWorkItem`, every await an unsafe await. So `AsyncLocal`, `Activity`
spans and similar C# ambient state do **not** survive a BjoML await. Put anything
ambient you need into your own context object.

See [design.md](design.md) for the full design and the list of known issues.

## Performance

The scheduler runs on the .NET thread pool via
`ThreadPool.UnsafeQueueUserWorkItem(IThreadPoolWorkItem, preferLocal: true)`, so work
keeps producer-side locality but is stealable.

On a Ryzen 9 5900X (12C/24T), 1 000 000 messages round a 1000-node ring:

| Path | Time |
|---|---|
| Fiber ring (`await IEvent`) | 331 ms |
| ValueTask ring (C# interop path) | 442 ms |

A 480-child fan-out spawned from inside a single fiber runs **12–13x faster than
serial**. Under the previous dedicated-worker scheduler it would have serialised onto
one core, because `Enqueue` always targeted the current worker's own queue and idle
workers could not steal.

Full numbers, methodology and a before/after comparison are in
[bench/BENCHMARKS.md](../../bench/BENCHMARKS.md).

## Projects

- [Example](Example) - a small client/server demonstrating the layer end to end.
- [StressTest](StressTest) - ring, fan-in/fan-out, fiber ring, fan-out scaling and
  the CML combinators (`Choose`, `Wrap`, `WithNack`).
- [Tests](Tests) - regression tests for each fixed correctness bug. Every test has
  been verified to fail when its fix is reverted. Run with
  `dotnet run -c Release --project BjolangRuntime/Cml/Tests`.
- [bench/](../../bench) - the benchmark suites, and `BASELINE.md`.

## License

MPL-2.0 with a linking exception, which is Bjolang's licence; see
[LICENSE](../../LICENSE). This was LGPL-3.0-or-later while it was a separate
project, and was relicensed on merging. Every file was written by the same
copyright holder as the rest of Bjolang, and none of it is vendored, so the
relicensing needed nobody else's agreement.

The LGPL was originally chosen because I learned of CML from Andy Wingo's
guile-fibers and matched its licence. This is not a derived work of it, except
for possibly the channels, which I had looked at long before writing anything
myself. Most of the algorithms come directly from John Reppy's papers.
