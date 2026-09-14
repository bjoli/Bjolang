# Scopes as owners, and tail-resumptive effects: restore the top-level scope, add owned resources, add handlers

## Context

Bjolang's concurrency runtime (formerly BjoML, now merged into the tree)
has cancellation scopes: `(with-cancel (cancel) ...)`, `(with-deadline ms
...)` and `(with-shield ...)` all expand through the `with-scope` macro in
`prelude.bjo` to `scope-open!` / `scope-push!` / `scope-close!` on the C#
`Scope` class. A scope owns the fibers started inside it: `spawn` enlists
in the ambient scope (an `Interlocked` count, no list), and `scope-close!`
drains the children, fires the token, and reports failures together.

The root scope around `main` was removed earlier for speed. At that time a
live token cost every parked `sync` a `CancellableEvent`, a closure and a
promise branch. After the merge the token is a link on the parked op, so
a scope's per-park cost is one pointer write. That changes the trade, and
this work revisits it.

Bjolang also has a dynamic environment: `DynEnv` in `BjolangRuntime.cs`,
one immutable snapshot per fiber context, which `parameterize` compiles to
(`parameter-push!` / `dyn-restore!` around a `try/finally` in `Parser.fs`)
and which `Bjo.Spawn` snapshots for every child. The `Scope` a spawn
enlists in is a field on that same snapshot.

This work also adds *tail-resumptive effect handlers* on top of that
environment. An effect operation is a parameter holding a function;
`with-handler` is `parameterize`; performing an operation reads the
parameter and calls the function on the performer's own stack. No
continuation is captured, so a handler can return once (resume) or throw
(abort) and nothing else — Scheme's `raise-continuable` with a declared
type per operation. It is in this document rather than one of its own
because of the last phase: file opening becomes an effect so tests can
install a fake filesystem, and a fake filesystem is only worth having if
fake ports are owned by scopes exactly the way real ports are. That needs
Phases 2 to 4 first.

This work does six things, in order:

0. Make the dynamic environment reach code that runs off-fiber, and add
   `defeffect`, `with-handler` and the first prelude effects (Phase 0).
1. Restore an implicit scope around `main`, gated by measurement.
2. Generalise `Scope` so it owns *resources* as well as fibers.
3. Make file ports, the REPL, stdin/stdout/stderr and the test runner use it.
4. Make file opening an effect, and give the test runner a fake
   filesystem whose ports are scope-owned like real ones (Phase 4a).
5. Run tests under scopes and under the fake filesystem.

Phase 0 comes first because it is cheap, touches no scope code, and its
one runtime change (`blocking` losing the environment) is a bug today
that becomes dangerous once file opening can be faked.

## Non-negotiable constraints

- **Scopes that own nothing pay nothing.** The release list on `Scope` is
  allocated lazily on the first `own!`. No new field is touched on the
  spawn or sync hot paths. Re-run the ring and spawn-burst benchmarks
  after each phase; append rows to `bench/BASELINE.md`.
- **One scope object.** Fibers and resources are owned by the same
  `Scope`. Do not introduce a second scope type or a second dynamic slot.
- **Ordering at close is fixed:** body finishes or fails → on failure,
  fire the token → wait for every owned fiber to land → fire the token
  (this is what stops daemons) → run releases newest-first, inside a
  shield → aggregate: body failure first, then child failures, then
  release failures as suppressed.
- **Releases are plain thunks in this work, and plain thunks have no
  deadline.** `(-> void)`, no suspension, no outcome parameter, run on
  the closer's own stack. A synchronous thunk cannot be abandoned — there
  is no way to interrupt a thread in the middle of `Dispose` short of
  running every release on a pool thread and walking away from it, which
  leaks a thread per stuck release and costs a thread hop per handle.
  So a release that blocks, blocks the close. That is the same trade
  `with-cancel` already makes for a child that never lands: a hang says
  where it is.
- **The deadline arrives with `own-bjo!`, and it is per release, not
  per closing pass.** Reserve the names `own-bjo!` (a suspending release,
  run under the shield with the shield's default deadline applied to
  *each* thunk on its own, so a scope with 100k owned handles is allowed
  to close them all) and an outcome-aware variant in the design doc.
  Releases already run inside the shield in this work, so adding
  `own-bjo!` later is additive: the mechanism is in place, only the
  deadline and the `await` are new.
- **A closing scope refuses `own!` and ignores `spawn`, and that
  asymmetry is deliberate.** `spawn` into a closing scope starts nothing
  and says nothing (`bjo` hands back a stillborn promise); that is the
  existing rule and it stays. `own!` into a closing scope *throws*. The
  two differ because the consequences differ: a fiber that was never
  started leaks nothing, but a resource that was opened and then not
  registered is a leak with nobody to release it, and the only safe
  answer is to refuse before the caller lets go of it. Document both
  sentences next to each other.
- **No new arities on existing functions.** The owner is always the
  ambient scope; overriding it is a form (`in-scope`), never a keyword
  argument. Bjolang has no rest-keywords, so a `#:scope` kwarg could not
  be forwarded through wrappers.
- **Semantics visible from Bjolang do not change except where this
  document says so.** Every change of meaning gets a test and a doc line.
- **One change per commit.** If a step needs a semantic change not
  listed here, stop and ask with an example program whose meaning would
  differ.
- **Effects are parameters, and there is no new dynamic slot.** An
  operation is a `Param` holding a function, and `with-handler` expands
  to the same push/restore pair `parameterize` uses. `DynEnv` gets no new
  field. `Scope` stays a field and never becomes an effect: it is read on
  every spawn and every sync, and a champ lookup there would give back
  the 1.4x the merge earned.
- **The root environment stays empty.** An operation's default handler
  is its parameter's *initial value*, never an entry in `Root.Vals`. A
  program that installs no handler pays nothing, and restoring the
  top-level scope (Phase 1) must not change that.
- **An operation's colour is its declared arrow, and there are two.**
  A `(-> ...)` operation accepts only ordinary handlers and can be
  performed anywhere. A `(-bjo-> ...)` operation accepts either colour
  (an ordinary `fun` is lifted, as everywhere else) and can only be
  performed from a bjoroutine. There is no colour-polymorphic operation
  in this work — no `-?->`, no `defbjouble` wrapper. If one seems
  needed, stop and ask with the example; the answer is usually a sync
  operation that returns a value whose *methods* are already
  colour-aware, as ports are.
- **Handlers run on the performer's stack.** A handler that throws
  unwinds the performer, not the installer; inside a spawned fiber that
  is a child failure like any other. This is what makes a file opened in
  a child fiber register on the child's ambient scope, and it is the
  reason the design is tail-resumptive rather than capturing. Do not add
  any form that suspends the performer to run the handler elsewhere.
- **Every `DynEnv` constructor carries both `Scope` and `Vals`.**
  Entering a scope must not drop handlers, and installing a handler must
  not leave a scope. `WithScope`, `WithVal` and `Detached` already keep
  this; keep it true, and keep effect commits and scope commits apart so
  it can be checked one change at a time.
- **No compiler change for effects in this work.** `defeffect` and
  `with-handler` are prelude macros; performing an operation calls an
  ordinary wrapper function the macro emits. If a step in Phase 0 turns
  out to need `Inference.fs` or `Parser.fs`, stop and say which and why.

## Phase 0 — Tail-resumptive effects

### 0.1 The environment reaches off-fiber code

In `Concurrency.cs`, `blocking` hands its thunk to `Task.Run` and
`spawn/thread` to a `LongRunning` task. `FiberContext` is thread-static
and is not flowed, so the thunk runs against `Dyn.Root`: today a
`parameterize` is invisible inside `(blocking ...)`, and after Phase 4a a
faked `open-input-file` inside one would open the real file from inside
a test. Capture `Dyn.Current` before handing the thunk over and
`FiberContext.Push` it inside the work, restoring on the way out. No
other callback path changes: nack actions and promise waiters never run
user code, which is the existing rule.

Tests: `(parameterize ((p 1)) (sync (blocking (fn () (p)))))` is `1`;
the same through `spawn/thread`; and after Phase 0.3, the same with a
handler.

### 0.2 `Unhandled`

A Bjolang error carrying the operation's written name, declared in the
prelude the way `Cancelled` is, so `try` can match on it. Its message
names the operation and says that no handler was installed. It is raised
by a default handler, never by the runtime.

### 0.3 `defeffect`

In `prelude.bjo`, next to the parameter section:

```scheme
(defeffect Log
  (log  (-> string void))              ; sync: performable anywhere
  (audit (-bjo-> string void))         ; suspending: bjoroutines only
  #:default ((log (fn (s) (display s (current-error-port)))))
  #:cold (audit))
```

Per operation it emits three definitions, and nothing per effect:

- `%op`: `(make-parameter default)` typed `(Param <arrow>)`. `default`
  is the `#:default` clause if there is one, otherwise a function of the
  declared arity that raises `Unhandled` with the operation's name. Use
  `make-cold-parameter` for operations listed under `#:cold`.
- `op`: the wrapper the program calls. For a `(-> ...)` arrow a `defun`
  that reads `%op` and applies it; for a `(-bjo-> ...)` arrow a `defbjo`
  that reads `%op` and awaits it. The wrapper carries the declared
  signature, so a `(-bjo-> ...)` operation performed inside a `defun` is
  the ordinary "cannot suspend here" error, and the copy machinery
  (`expandReachingDefuns`) sees a named function, not a parameter.
- Nothing else. The effect name is documentation in this work; no record
  of handlers, no per-effect install form (see Deferred).

Build the `%op` symbol with `derive-sym`, which the prelude already
uses. If `def/macro` cannot expand to more than one top-level
definition, make `defeffect` a parser special form shaped like
`parameterize` and say so in the report — that is the one exception to
the "no compiler change" constraint, and it is a parsing convenience,
not a typing change.

Every hot operation spends one of the 31 hot parameter ids. `#:cold`
exists so that a program with many rarely-performed effects does not
push its ports off the root slots; say this in the doc comment.

### 0.4 `with-handler`

```scheme
(with-handler ((log collect) (audit send-it)) body ...)
```

expands to `(parameterize ((%log collect) (%audit send-it)) body ...)`:
simultaneous bindings, one `try/finally` per binding, environment
restored however the body ends. No new push/restore machinery.

Colour checking is free: `%log` is a `(Param (-> string void))`, so a
`bjoroutine` handler fails arrow unification; `%audit` is a
`(Param (-bjo-> string void))`, so an ordinary `fun` is lifted. If the
arrow-mismatch message can name the operation ("'log' is a sync
operation; a bjoroutine cannot handle it") without touching
`unifyEffect`, do that; otherwise the existing message stands for this
work.

Tests, each a separate case in `simpletest.bjo`:

- performing with no handler raises `Unhandled` naming the operation;
- a handler's return value is the value of the perform;
- the environment is restored after `with-handler`, including on a
  throw out of the body;
- a handler that throws unwinds the performer and is caught by a `try`
  around the `with-handler`;
- a fiber spawned inside `with-handler` sees the handler;
- a `bjoroutine` handler on a sync operation is a compile error (a
  negative compile test, if the suite has that shape; otherwise a
  documented example);
- a `-bjo->` operation performed from a `defun` is a compile error;
- a perform inside `(sync (blocking (fn () ...)))` sees the handler
  (Phase 0.1).

### 0.5 The first prelude effects

Add, each with a `#:default`, each in its own commit:

- `warn`: `(-> string void)`, default writes to the error port. The
  case a plain exception cannot express: continue by default, collect or
  escalate under a handler. Test: escalate by installing a handler that
  raises, and catch it.
- `log`: `(-> string void)`, default writes to the error port. Sync on
  purpose so plain `defun`s can log; a handler that wants a channel
  pushes to a mutable queue rather than `sync`ing.
- `now`: `(-> int)` (milliseconds, whatever `stopwatch.bjo` already
  uses), default the system clock. Rewrite `stopwatch.bjo` to perform
  it. Test with a fake clock.
- `getenv`: `(-> string (Option string))`, default the process
  environment.
**The test for what is an effect and what is a parameter.** If the thing
installed is a *value* the caller reads, it is a parameter. If it is a
*behaviour* the caller decides — with a default worth naming, a possible
abort, and a signature worth checking — it is an effect. By that test:

- errors stay `try`/`raise`, state stays `parameterize` and
  `def/mutable`, and the ports stay parameters; each would only gain a
  second spelling.
- **`random` stays a parameter.** It is already swappable through
  `random.bjo`'s parameter, it has no abort case, and an effect would
  add a wrapper function over a read that is already one. Do not touch
  `random.bjo` in this work.
- `now` is the borderline case and is included anyway, because
  `stopwatch.bjo` performs it from library code that should not have to
  know which parameter holds the clock.

### 0.6 The extent rule, documented

A scope waits for its children; a handler frame does not. So

```scheme
(with-cancel (cancel)
  (with-handler ((log (fn (s) (vec-push! buf s))))
    (spawn (fn () (work))))
  (report buf))
```

lets the child keep logging into `buf` after `with-handler` has
returned, and `report` sees a partial buffer. `parameterize` behaves the
same way today; a collecting handler is just where it shows. Write the
rule into the `with-handler` doc comment: **a handler that collects must
enclose the scope that owns the performers** — put the `with-handler`
outside the `with-cancel`. Add a test showing the enclosing order gives
a complete buffer. Do not make `with-handler` open a scope of its own in
this work (see Deferred).

## Phase 1 — Restore the top-level scope (measured)

1. Benchmark first: the ring inside `(with-cancel ...)` versus the ring
   with no scope open, on the merged runtime. Record both.
2. If the scoped ring is within ~5% of the unscoped ring, proceed. If it
   is not, **stop and report the numbers**; the rest of this phase
   depends on the answer.
3. Make `RunMainFiber` / `RunMainSync` run `main` inside an ordinary
   scope, opened with the same `scope-open!` path `with-cancel` uses.
   It is a normal scope with the one rule: sibling failure cancels the
   rest, `main` does not return until every owned fiber has landed, and
   a top-level failure is reported through the aggregated report. This
   is *not* the old root: no special-cased token, no separate code path.
4. Register the stdout/stderr flush as the **first** release on that
   scope (LIFO: it runs last, after every user resource has written its
   final lines). Confirm with a test that the failure report reaches
   stdout when `main` fails.
5. `spawn` and `own!` with no ambient scope raise a Bjolang error
   ("spawn outside a scope"). With Phase 1 done this only triggers at
   module load time and in callbacks from .NET; see Phase 5.
6. Document in the README: "when `main` returns, the work it started is
   over" — the same sentence as for `with-cancel`.

## Phase 2 — Runtime: owned resources

In `Scope.cs`:

- Add an `Owned` node type: doubly linked (prev/next), a release
  `Action`, and a state field flipped with a CAS
  (`Registered → Released`). The node *is* the handle returned to
  Bjolang.
- `Scope.Own(Action release)`: if the scope has begun closing, throw a
  Bjolang error ("own! on a closing scope") — this is the one place a
  closing scope raises; see the constraint on the asymmetry with
  `spawn`. Otherwise allocate the node, link
  it at the head under `_gate`, return it. The list head is `null`
  until the first `Own`.
- `Owned.Release()`: CAS the state; if this call won, unlink under the
  scope's `_gate` and run the release outside the lock. A second call,
  or a call racing the scope's closing pass, is a no-op. Run the release
  on the caller's stack so its exception reaches the caller.
- `Scope.Close`: after children have landed and the token has fired,
  walk the list from the head, CAS each node, run the ones this pass
  won, inside the existing shield mechanism, on the closer's stack. No
  deadline in this work (see the constraint); a release that throws is
  caught, recorded, and the walk continues with the next node. Collect
  exceptions into the existing failure aggregation as suppressed.
- Add a `propagate` flag to the `Scope` constructor, default true. With
  it false, a child's failure is reported to stderr as it lands (via
  `ReportUnlessCancelled`) rather than stored, and never fires the token.
  Only two callers pass false: the REPL session scope and the scope
  around a detached fiber (Phase 5). Same object, one flag — not a
  second scope type.
- `IsCancellation`: treat `ObjectDisposedException` exactly like
  `OperationCanceledException` — a cancellation if the token has fired,
  a failure otherwise. A daemon is not waited for, so it can be parked
  in `read-line` on a port when the release walk disposes that port;
  without this rule every such daemon prints an unhandled exception at
  scope close.
- **Cap the failure list.** `_failures` is unbounded today. Keep the
  first N (say 32) `ExceptionDispatchInfo`s and a total count; the
  aggregated report says "and K more". A million failing children must
  not hold a million exceptions.
- Tests: LIFO order; early `Release` unlinks (assert list length by an
  internal test hook); `Release` racing `Close` runs the thunk exactly
  once in either interleaving; a release that throws does not stop the
  others and appears in the report; `Own` on a closed scope throws;
  `spawn` on a closed scope is silent; a daemon parked on an owned port
  ends quietly when the scope closes; a `propagate: false` scope reports
  a child failure and keeps running; the failure cap reports the count.
- **Scale tests** (mark them slow, run in CI nightly): open 100k owned
  handles in one scope from 100 concurrent fibers, then close — measure
  time to open (the `_gate` contention) and time to close (the walk);
  the same 100k handles as one-per-child-scope, 100k child scopes, then
  close the parent — confirm the parent's list stays at length 0 and
  the close time is dominated by the drain, not a walk. Record both in
  `bench/BASELINE.md`; the second must be no slower than the first.

## Phase 3 — Prelude surface

Add to `prelude.bjo`, next to the existing scope section:

```scheme
(with-scope body ...)        ; a scope with no cancel thunk; the plain form
                             ;   to put around resources. Same expansion as
                             ;   with-cancel minus the binding.
(current-scope)              ; the ambient Scope as a value; raises if none
(in-scope s body ...)        ; push s as the ambient scope for the extent,
                             ;   restore on every exit — same shape as the
                             ;   internal push/dyn-restore! pair
(own! thunk)                 ; register on the ambient scope; returns Owned
(own-disposable x)           ; (own! (fn () (.Dispose x))), returns x
(release! owned)             ; run once and unlink; later calls no-op
(scope-cancel! s reason)     ; what the cancel thunk already does, on a value
```

- The existing internal `with-scope` macro (the one whose error says
  "write one of those") is renamed to `%scope-form` so the user-facing
  name is free. `with-scope`, `with-cancel` and `with-deadline` are three
  spellings of one expansion; document them as "`with-scope` plus one
  feature each".
- `call-with-input-file` / `call-with-output-file` become
  `(with-scope (proc (open-input-file path)))`.
- **`with-open` already exists** as a parser special form (`Parser.fs`,
  `"with-open"`) that disposes each binding via a direct `.Dispose` on
  the way out. Keep the form — it is the right spelling for "this one
  resource, this one block", and it works on any .NET `IDisposable` from
  interop — but its exit must not bypass an owner handle: on an owned
  port a direct `.Dispose` closes the stream and leaves the node in the
  scope's list. Change the expansion to call a runtime helper
  (`BjolangRuntime.CloseOwnedOrDispose(x)`): if `x` is a `BjoPort` /
  `BjoWriter` with a non-null `Owner`, `release!` it; otherwise
  `.Dispose`. Do not add a second binding form.
- `Owned` is an opaque Bjolang type declared the way `Scope` is: no
  `Eq`, `Ord` or `show`, no field access. The only operation on it is
  `release!`. `Scope` gets no new observable operations either.
- **Fibers are not scopes.** A file opened inside `(spawn ...)` or
  `(bjo ...)` is owned by the enclosing `with-cancel`, not by the fiber,
  and lives on after the fiber lands. This is intentional — a scope per
  fiber would be an allocation per spawn — and it is the whole reason
  for the "child scope per unit of work" advice below. State it in the
  prelude doc comment with the example.
- `in-scope` covers spawning into another scope; do **not** add a
  `spawn-in`. `spawn` and `own!` both read the same ambient slot.
- Both `own!` and `spawn` raise the same "outside a scope" error.
- `with-cancel` keeps its current `(cancel)` binding form; scope values
  come from `(current-scope)`, so no new binding syntax.
- Keep the `Scope` surface to exactly these operations. Counts, list
  length, token state and drain order stay internal.
- Doc comment in the prelude, in the same voice as the existing scope
  section: own things the GC cannot price (OS handles, permits,
  subscriptions, state to restore). Do not own memory. A handle returned
  out of its block is dangling and will fail at first use. For many
  short-lived resources under one long-lived scope, open a child scope
  per unit of work — the parent then holds a count, not a list — and
  cite the scale-test numbers from Phase 2 as the reason.

## Phase 4 — File ports

`open-input-file` / `open-output-file` (and any other file-backed port
constructors) become scope-owning:

1. Read the ambient scope first — raise before opening anything, so the
   error path leaks nothing.
2. Open the .NET stream (existing `#:exceptions` mapping stays).
3. Wrap in `BjoPort` / `BjoWriter`; add an `Owner` field to those
   classes; `own!` a release that disposes the stream; store the handle.
   If `own!` throws (scope closed in between), dispose and rethrow.
4. `close-input-port` / `close-output-port` become a call to the same
   runtime helper `with-open` uses: release the owner if there is one,
   otherwise (stdin/stdout/stderr, string ports) flush and do nothing.
   `Owner` stays a C# field; there is no `(owner p)` in Bjolang. Remove
   the direct `.Dispose` — it must not bypass the handle, or the node
   stays in a long-lived scope's list.
5. String ports (`StringReader`, `StringWriter`) do **not** register:
   they are memory only.
6. Mark the `#:async` reads `#:cancellable` so a cancelled scope
   interrupts a pending read. Confirm with a test: a fiber parked in
   `read-line` on a port inside `(with-deadline 50 ...)` ends with
   `Deadline`, not a hang. **The test must read from a pipe, not a
   regular file**: `FileStream` reads of a regular file complete whether
   or not the token fires (on Linux the token is only checked before the
   read starts), so a file-backed test would pass by finishing rather
   than by cancelling. Use an `AnonymousPipeServerStream` /
   `AnonymousPipeClientStream` pair with nothing written to it.
7. Use after close maps `ObjectDisposedException` to a Bjolang error at
   the use site.
8. Optional: `#if DEBUG` finalizer on `BjoPort`/`BjoWriter` that logs to
   stderr if `Owner` is still `Registered` at finalization. It is an
   invariant check, not a mechanism; skip if it complicates anything.

## Phase 4a — File opening as an effect, and the fake filesystem

Depends on Phase 0 and Phase 4. Existing programs must not change
meaning: `(open-input-file path)` still opens the file and still
registers it on the ambient scope.

1. Rename Phase 4's constructors to `%open-input-file` and
   `%open-output-file`. They keep everything Phase 4 gave them —
   read the scope first, open, wrap, `own!`, set `Owner` — and they are
   the defaults below. Calling one directly bypasses the handler; the
   `%` says so, and the doc comment says it again.
2. In `prelude.bjo`:

   ```scheme
   (defeffect FS
     (open-input-file  (-> string (Result Exception TextInputPort)))
     (open-output-file (-> string (Result Exception TextOutputPort)))
     #:default ((open-input-file %open-input-file)
                (open-output-file %open-output-file)))
   ```

   Both operations are sync. Opening is cheap; the reads and writes that
   park go through the port's own `read-line` / `write-string`, which
   are already `defbjouble`s, so colour polymorphism is inherited from
   the value and the effect never needs a suspending half.
3. **Adopt what a handler returns.** The wrapper `defeffect` emits for
   these two operations (and only these — mark them `#:resource` in the
   declaration) passes the `Ok` value through a runtime helper,
   `BjolangRuntime.AdoptPort(x)`: if `x` is a `BjoPort` / `BjoWriter`
   whose `Owner` is non-null, return it unchanged; otherwise wrap it in
   a `BjoPort` / `BjoWriter`, `own!` a release that disposes it, set
   `Owner`, return the wrapper. So a real port is owned once, by its
   constructor, before the caller can lose it; a port a fake handler
   built from `open-input-string` is owned by the scope it was performed
   in, at the perform. This is the point of the phase: a leak test over a
   fake filesystem means the same thing as over a real one. String ports
   made directly with `open-input-string` still do not register (Phase 4
   step 5); only ports that arrive *through the effect* do. Document the
   distinction.
4. `AdoptPort` reads the ambient scope, so performing `open-input-file`
   outside a scope raises the same "outside a scope" error as `own!`.
5. In `simpletest.bjo`, a test helper:

   ```scheme
   (with-fake-fs (("notes.txt" "a b c")
                  ("empty.txt" ""))
     body ...)
   (fake-fs-written "out.txt")     ; contents written so far, or None
   ```

   `with-fake-fs` is `with-handler` over both operations: reads look the
   path up and answer `(Ok (open-input-string contents))` or the same
   `Err` the real constructor gives for a missing file; writes hand out a
   `StringWriter` stored by path, which `fake-fs-written` reads
   (`StringWriter.ToString` works after dispose, so this is valid after
   the port is closed). A path written earlier in the same
   `with-fake-fs` is readable afterwards. Keep the fake in
   `simpletest.bjo`, not the prelude; it is a test tool.
6. Tests:

   - reading a fake file, `with-open` closing it, and the scope's owned
     count returning to 0 at close (Phase 2's internal hook);
   - writing, closing, then `fake-fs-written`;
   - a missing path is `Err`, with the same exception type as the real
     constructor;
   - a fake port opened without `with-open` inside `(with-scope ...)`
     is released when the scope closes — the leak test;
   - a fiber spawned inside `with-fake-fs` (inside its own
     `with-scope`, per the extent rule) sees the fake;
   - `(sync (blocking (fn () (open-input-file "notes.txt"))))` sees the
     fake (Phase 0.1 again, now with teeth);
   - a fake port used after close raises the Phase 4 step 7 error.
7. **The extent rule applies to the fake.** Phase 6 wraps every test
   body in a deadline scope, and `with-fake-fs` sits inside that body,
   so a fiber spawned directly into the test's scope can outlive the
   fake. The helper's doc comment says: spawn inside a `with-scope` of
   your own inside `with-fake-fs`. Do not silently open a scope inside
   `with-fake-fs`.

## Phase 5 — The places the rule bites

- **REPL.** Open one session scope in `Repl.fs` when the session starts,
  with `propagate: false`; every evaluation runs inside it; close it on
  exit. Without a session scope, `(def p (open-input-file "x"))` at the
  prompt is closed before the next line. Without `propagate: false`, one
  `(spawn (fn () (raise ...)))` at the prompt fires the session token and
  every later `sync` at the prompt raises `Cancelled` — a poisoned
  session. So at the REPL a failed spawn is printed and the session
  continues, which is what a scopeless spawn does today. Test both. Do
  this in the same commit as Phase 4 step 1.
- **`spawn/detached`.** A detached fiber clears the ambient scope by
  design, so once `spawn` and `own!` raise outside a scope, every spawn
  or file open in a detached subtree would raise. That is a semantic
  change and it is not wanted. `ScopeSpawnDetached` therefore runs the
  body inside a fresh scope of its own — unlinked, no deadline,
  `propagate: false` — opened before the body and closed when the body
  ends. Nothing outside can cancel it, as before, and now the work
  *inside* it is structured. Test: a detached fiber that spawns and opens
  a file finishes with both landed and released. `Detached()` keeps
  `Vals`, so a detached fiber still inherits its parent's handlers,
  including a fake filesystem; its fake ports are now owned by its own
  scope. Add that to the test, and to the `spawn/detached` doc line.
- **stdin/stdout/stderr.** Never owned. `Owner` is null; `close-*-port`
  on them flushes and does nothing else. Add a test that a scope which
  wrote to stdout does not close it.
- **stdin cancellation.** .NET console reads are not cancellable. Add one
  dedicated reader thread that feeds a channel; `read-line` on the
  standard input port becomes `sync` on that channel, so it is
  cancellable like every other read. Test: `(with-deadline 50 (read-line
  stdin))` ends with `Deadline`.
- **Module-level definitions** that spawn or open files now raise at
  load. Grep the examples and the stdlib for these; move them into `main`
  or make them lazy. List every file changed for this reason.
- **Callbacks from .NET.** A handler running on a foreign thread has no
  ambient scope. Document the pattern: capture `(current-scope)` when
  registering, wrap the callback body in `(in-scope captured ...)`. Add
  one example to the interop docs.

## Phase 6 — Tests run under scopes

In `simpletest.bjo`, run each test body under `(with-deadline
default-ms ...)`. This gives every test a timeout, turns a leaked fiber
into a `Deadline` failure that names the test, cleans up owned resources,
and reports multiple failures together instead of only the first. Make
the default configurable per test. Run the whole suite and fix any test
that relied on a fiber outliving it.

Two additions for effects:

- After each test body, assert the test's scope owned count is 0 before
  close (the Phase 2 hook). A test that opened a port — real or fake —
  and did not close it fails by name rather than being cleaned up
  silently. Make it overridable per test for the tests that check
  release-at-close on purpose.
- Convert every existing test that reads a fixture file to
  `with-fake-fs`, and delete the fixture files this frees. List them in
  the report. Tests of the real constructors keep real files, and there
  should be exactly as many of those as Phase 4 has steps that touch
  the filesystem.

## Docs

Update the scope section of the README and the prelude comments:

- The one rule, now for both fibers and resources: when a scope returns,
  everything it owns is over — fibers landed, resources released, LIFO.
- What is *not* prevented: a fiber in a pure loop with no `sync` never
  observes cancellation; two fibers waiting on each other is still a
  deadlock, now a scope that never closes. Recommend `with-deadline` as
  the default form.
- Own what the GC cannot price; do not own memory.
- Long-lived scopes: prefer a child scope per unit of work over `own!` +
  `release!` into the parent.
- Dangling handles fail at first use; there is no static check.
- Fibers are not scopes: a resource opened in a spawned fiber belongs to
  the enclosing scope and outlives the fiber.
- `with-open` for one resource in one block; `own!` for a resource whose
  lifetime is the scope's. Both go through the owner handle.
- A closing scope refuses `own!` and ignores `spawn`, and why.
- Plain releases have no deadline; a release that blocks, blocks the
  close. `own-bjo!` (later) is where the deadline lives.

And an effects section, next to the parameter section it builds on:

- What an effect is here: a typed, named parameter holding a function;
  a handler returns once or throws; nothing is captured. Say plainly
  what that rules out — generators, backtracking, resuming twice — and
  that it rules them out for good on this backend, so nobody plans
  around them.
- Colour: the declared arrow decides; sync operations for anything a
  `defun` must be able to perform; the trick of returning a
  colour-aware value instead of making the operation suspend.
- `#:default` versus `Unhandled`: give a default when there is one
  sensible meaning (`log`, `now`, the real filesystem); leave it
  unhandled when running without an installer should fail loudly.
- The extent rule, with the `with-cancel` example from Phase 0.6.
- Ports that arrive through `open-input-file` are owned by the scope
  they were performed in, whatever backs them; ports made directly with
  `open-input-string` are not.
- `%open-input-file` bypasses the handler.
- `#:cold`, and why hot ids are a budget.
- The interaction with `blocking` and `spawn/thread`: the environment
  now follows the thunk (Phase 0.1).

## Deferred, and why

Not in this work; listed so nobody builds them by accident:

- **A per-effect handler record** (`(with-handler Log …)` installing
  every operation at once). Would close the hole where `with-fake-fs`
  handles one operation and the other still hits the disk. Deferred
  because `defeffect` emits nothing per effect yet; if this is wanted,
  the record type is emitted by `defeffect` from day one of that work,
  not derived from the operations afterwards.
- **`#:scoped` handlers** — `with-handler` opening a scope of its own so
  it waits like `with-cancel` and the extent rule enforces itself. Costs
  a scope per handler frame; only worth it if the rule in Phase 0.6
  bites someone.
- **Colour-polymorphic operations** — a `defbjouble` wrapper over two
  parameters, one per colour, with `with-handler` taking a
  `(sync-fn bjo-fn)` pair. Statically checkable, but no operation in
  this work needs it.
- **A database effect** (`query` / `exec` as `-bjo->` operations, the
  connection held by the handler, transactions as rebinding). Blocked
  on choosing a `Row` representation that Bjolang can construct — a
  fake must be able to build the rows — which is a design decision, not
  an implementation one.
- **`own-bjo!`**, as already noted above.

## Report

Finish with: the Phase 1 benchmark numbers and the decision they led to;
a table of ring / spawn burst before and after each phase; whether
`defeffect` stayed a macro or became a parser form; the list of files changed under
Phase 5; the fixture files removed under Phase 6; and anything you did
not do, with the reason.
