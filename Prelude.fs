module Bjolang.Prelude

open Bjolang.TypedAST.TypeConstants
open Bjolang.TypedAST

// Helper for function types
let makeFunType args ret = tfun args ret

let makeVecType a = TCon("Vec", [a])
let makeVecBuilderType a = TCon("VecBuilder", [a])
let makeListBuilderType a = TCon("ListBuilder", [a])
let makeVecCursorType a = TCon("VecCursor", [a])

/// A position in a string, and the reason there is no `string-ref`.
///
/// Opaque, and takes no type argument: it is an offset into the string's own
/// storage, which today counts UTF-16 code units and after a move to UTF-8
/// would count bytes. Nothing in the language can read that number — there is
/// no cursor-to-int conversion, and the only way to make one is from a string —
/// so the change is invisible to every Bjolang program. That is the whole
/// point of the type, and why it is nominal rather than an alias for `int`.
let stringCursorType = TCon("StringCursor", [])

/// The accumulator behind the `Stringing` collector. A `System.Text.StringBuilder`,
/// named here because `std/prelude` has to write the type in an impl's
/// `(type %acc ...)` clause.
let stringBuilderType = TCon("StringBuilder", [])
let makeListType a = TCon("List", [a])
let makeSeqType a = TCon("Seq", [a])

/// A live position in a walk of a `Seq`, and the `Iterable` cursor for one.
///
/// Opaque, and used linearly: it holds the enumerator, so stepping it consumes
/// the sequence. That is what distinguishes it from a `Cursor`, which is a
/// value and may be held and compared — and why `Seq` implements `Iterable`
/// and not `Cursor`.
let makeSeqCursorType a = TCon("SeqCursor", [a])
let makeOptionType a = TCon("Option", [a])
let makeResultType e a = TCon("Result", [e; a])
let makeArrayType a = TCon("Array", [a])

/// A dynamic parameter, holding an `a`. Opaque: the only things that may look
/// inside are `parameter-ref` and `parameterize`.
let makeParamType a = TCon("Param", [a])

/// A promise: the handle `(bjo ...)` hands back, and Bjolang's future.
///
/// Opaque, and backed by `Bjoml.Promise<T>` rather than by `Fiber<T>`. In the
/// runtime `FiberCore<T> : Promise<T>`, so the fiber's own core *is* the
/// promise: a spawn costs one object rather than a fiber plus a handle. The
/// fiber is a compiler artifact and never appears in a Bjolang type.
let makePromiseType a = TCon("Promise", [a])

/// A first-class, withdrawable description of a synchronisation that has not
/// happened yet.
///
/// The central discipline of the CML surface is that *building* an event is
/// pure and *syncing* it is the yield point. That split is what makes `choose`
/// possible at all — a composite event can be offered and then withdrawn — and
/// it is why `promise-join` is an ordinary function while `sync` is the one
/// suspending builtin.
let makeEventType a = TCon("Event", [a])

/// A synchronous, unbuffered channel. A send and a receive rendezvous, so
/// backpressure falls out rather than being arranged: a sender waits in the
/// rendezvous until a receiver takes the item.
let makeChanType a = TCon("Chan", [a])

/// A .NET `IAsyncEnumerable<T>`, which is the one thing in the interop surface
/// that is a stream rather than a value or a task.
///
/// Opaque, and there is nothing to do with one but hand it to
/// `async-seq->chan`: it is not `choose`-able, not withdrawable and not a CML
/// event, which is exactly what turning it into a channel fixes.
let makeAsyncSeqType a = TCon("AsyncSeq", [a])

/// A cancellation token: a persistent event that fires at most once.
///
/// A newtype over `(Promise CancelReason)` and nothing else — the runtime
/// representation *is* a `Bjoml.Promise<CancelReason>`, so cancelling is
/// `TrySetResult`, asking is `IsCompleted`, waiting is `Join`, and linking a
/// child to a parent is `Forward`. No new runtime machinery, which is the
/// point of §6.1.
///
/// Nominal rather than an alias, though, because the two are not
/// interchangeable *to a program*: `promise-join` on a token would hand back a
/// `(Result Exception CancelReason)` and invite a `match` on a failure that
/// cannot happen, and `detach` on one would arm an unhandled-exception report
/// for a promise nothing ever fails. A distinct `TCon` costs nothing at runtime
/// and keeps both out of reach.
let cancelTokenType = TCon("CancelToken", [])

/// Why a scope was cancelled — the payload every token carries.
///
/// Builtin rather than declared in `prelude.bjo` because `Concurrency.cs` has to
/// *construct* reasons (the nack in `spawn-evt`, the deadline watcher) and the
/// runtime is compiled below the generated code, so it cannot reference a type
/// the code generator emits.
///
/// Deliberately **not** `CancelToken %r`. `current-cancel` is one ambient
/// parameter for the whole program, so its payload type is fixed program-wide; a
/// parameter would either infect every signature that touches cancellation or
/// collapse to a single instantiation anyway.
let cancelReasonType = TCon("CancelReason", [])

/// A saved dynamic environment. Produced by `parameter-push!` and consumed by
/// `dyn-restore!`, both of which only ever appear in a `parameterize` desugar.
let dynEnvType = TCon("DynEnv", [])

/// The port types, as `std/prelude` publishes them. Named here because the
/// three standard ports are builtin bindings and have to be typed before
/// `prelude.bjo` exists to say it.
let textInputPortType = TCon("System.IO.TextReader", [])
let textOutputPortType = TCon("System.IO.TextWriter", [])

/// A piece of syntax: what a macro transformer takes and returns.
///
/// Backed by `Bjolang.Runtime.Syntax`, a C# union shaped like a generated one,
/// for the same reason `Option` and `Result` are built in rather than declared:
/// the compiler has to construct and read values of it — here, on both sides of
/// a reflection call into a loaded macro module — and there is no source file it
/// could hang a `def/type` on that would already exist when that happens.
let syntaxType = TCon("Syntax", [])


let emptyRegistry : TraitRegistry =
    { LocalTraits = Set.empty
      // The types with no declaring module, which is exactly what `typeKey`
      // leaves unkeyed. One list, in `Naming`, so the registry that holds them
      // and the rule that exempts them cannot drift apart.
      LocalTypes = Naming.builtinTypeNames
      Traits = Map.empty
      TraitMethods = Map.empty
      Implementations = Map.empty
      ImplTargets = Map.empty
      BlanketImpls = Map.empty
      TraitOrigins = Map.empty
      InlineMethods = Map.empty
      Aliases = Map.empty
      ImportAliases = Map.empty
      Records = Map.empty
      RecordFields = Map.empty
      MutableRecordFields = Map.empty
      Unions = Map.empty
      ClrClasses = Map.empty
      ClrExterns = Map.empty

      // Seeded rather than declared, because the type it is about is built in
      // and there is nowhere in source to hang the declaration. `Result` is the
      // whole of §8.2's third level: a discarded error is exactly the failure
      // the type exists to make visible.
      NoDiscard = Set.ofList [ "Result" ]

      OpaqueTypes = Set.empty
      HiddenMembers = Map.empty }

let prelude : Env =
    { Bindings = Map.ofList [
        // Literals / Constants
        "true", {Scheme = Scheme([], [], boolType); IsMutable = false  }
        "false", {Scheme = Scheme([], [], boolType); IsMutable = false }

        /// The unit value, for the one body that has to produce one without
        /// doing anything: `Discard`'s blanket implementation.
        ///
        /// Every other way of getting a `Unit` is by calling something that
        /// also has an effect, which is fine everywhere except in the function
        /// whose entire meaning is "no effect, no value". `(Tuple)` is not it —
        /// that is the empty *tuple*, a different type with a different C#
        /// spelling. See §8.2.
        "unit", {Scheme = Scheme([], [], unitType); IsMutable = false }

        // Math Operators (Polymorphic, deferring resolution to C#)
        "+", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] (TVar "a")); IsMutable = false }
        "-", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] (TVar "a")); IsMutable = false }
        "*", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] (TVar "a")); IsMutable = false }
        "/", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] (TVar "a")); IsMutable = false }
        "%", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] (TVar "a")); IsMutable = false }

        // Unary arithmetic, which `(- x)` and `(/ x)` desugar to.
        //
        // These exist because the obvious expansions do not typecheck: `(- x)`
        // as `(- 0 x)` unifies the literal's `int` with `x`, so negating a
        // double is a type error. A primitive keeps the operand's own type,
        // and codegen emits C#'s unary minus rather than a subtraction.
        "negate", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"] (TVar "a")); IsMutable = false }
        "recip",  {Scheme = Scheme(["a"], [], makeFunType [TVar "a"] (TVar "a")); IsMutable = false }

        // Bitwise operators, typed and emitted exactly as the arithmetic ones
        // are: C# resolves each from the operand type, so they work on every
        // integral type including the unsigned ones `Num` leaves out. That is
        // why they are primitives rather than a trait — bit twiddling is where
        // `uint` and `ulong` live.
        "bitwise-and", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] (TVar "a")); IsMutable = false }
        "bitwise-ior", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] (TVar "a")); IsMutable = false }
        "bitwise-xor", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] (TVar "a")); IsMutable = false }
        "bitwise-not", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"] (TVar "a")); IsMutable = false }

        // Shifts. The count is an `int` whatever is being shifted, and C# masks
        // it to the operand's width, so `(shift-left 1 32)` is 1 and not 0.
        //
        // `shift-right` is C#'s `>>`: arithmetic on a signed type, logical on an
        // unsigned one. `shift-right-logical` is `>>>`, which shifts zeroes in
        // whatever the operand's sign.
        "shift-left", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; intType] (TVar "a")); IsMutable = false }
        "shift-right", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; intType] (TVar "a")); IsMutable = false }
        "shift-right-logical", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; intType] (TVar "a")); IsMutable = false }

        // Comparison Operators. `=` is not here: it is a method of the `Eq`
        // trait declared in `std/prelude`, so that one equality serves the
        // primitives, the containers and a user's own types alike.
        "<", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }
        ">", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }
        "<=", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }
        ">=", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }

        // --- The primitives `Eq` is built out of -----------------------------
        //
        // Prelude-private: `preludePrivateBindings` below refuses them to any
        // module but `std/prelude`. They have to be, because materialization
        // (§3.1) makes a record's `Equals` call its `Eq` impl — so an impl
        // written in terms of .NET equality would call itself forever.

        // C# `==`, which is what `=` used to be. Correct and free on the
        // primitive types the impls below use it for; meaningless at a type
        // variable, where it does not even compile.
        "clr-eq", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }
        // `EqualityComparer<T>.Default.Equals`, which does compile at a type
        // variable — and which differs from `==` on `NaN`, deliberately. See
        // the `double` impl in `std/prelude`.
        "clr-equals", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }
        "clr-hash", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"] intType); IsMutable = false }

        /// What an `eq-hash` over several fields folds with. Public, unlike the
        /// three above: every derived and hand-written impl needs it.
        "hash-combine", {Scheme = Scheme([], [], makeFunType [intType; intType] intType); IsMutable = false }

        /// The `eq-hash` of a type that has none: it throws, naming the type.
        ///
        /// A record with a `#:mutable` field has no lawful hash — see
        /// `unhashable` in the runtime — so `derive` writes this instead of a
        /// fold. Public because a derived implementation lands in the module
        /// that wrote the `type/derive`, and honest as a thing to write by
        /// hand: it is how a type says it is not a key.
        "unhashable", {Scheme = Scheme([], [], makeFunType [stringType] intType); IsMutable = false }

        // Object identity — for mutable cells, and for nothing else. On a value
        // type it is silently structural, there being no identity to ask about.
        //
        // `equal?` is gone: it *is* `=` now, and leaving the binding would let
        // a value use find the unconstrained primitive while an application
        // found the trait.
        "eq?", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }


        // I/O
        "display", {Scheme = Scheme([], [], makeFunType [stringType] unitType); IsMutable = false }
        "displayln", {Scheme = Scheme([], [], makeFunType [stringType] unitType); IsMutable = false }

        "newline", {Scheme = Scheme([], [], makeFunType [] unitType); IsMutable = false }

        // --- The dynamic environment ---
        //
        // A parameter is a value, not a nullary function, and is read with
        // `parameter-ref`. R7RS makes a parameter object callable and reads it
        // by applying it, which is a convenience of dynamic typing: here one
        // uniform reader keeps `(Param %a)` an ordinary type that can be passed,
        // stored and returned.
        //
        // `(Param TextOutputPort)` is closed, so the three standard ports are
        // module-level values without tripping the open-type restriction — a
        // user's own `(def verbose? (make-parameter #f))` is closed too, and one
        // that is not gets the existing "give it a signature" error.
        "make-parameter", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"] (makeParamType (TVar "a"))); IsMutable = false }

        // The same parameter, declining the cheap read. A parameter is filed in
        // the environment under an id that decides how deep it sits, and only
        // the first 31 get a place of their own; this one gives that place up
        // for a parameter that is not read in a loop, so that the ones that are
        // keep theirs. Nothing else about it differs, which is why it shares
        // `Param` and every operation on one.
        "make-cold-parameter", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"] (makeParamType (TVar "a"))); IsMutable = false }
        "parameter-ref", {Scheme = Scheme(["a"], [], makeFunType [makeParamType (TVar "a")] (TVar "a")); IsMutable = false }

        // --- Concurrency -----------------------------------------------------
        //
        // `bjo` is not here: it is a special form, because it must *not*
        // evaluate its operand in the child. See `EBjo`.

        /// The yield point. Its arrow is suspending, which is the whole of what
        /// makes `(sync ev)` a yield point: nothing else about it is special,
        /// and the emitter wraps the call in an `await` because of the type
        /// rather than because of the name.
        "sync", {Scheme = Scheme(["a"], [], TFun([makeEventType (TVar "a")], TVar "a", EAsync)); IsMutable = false }
        // The same wait from an ordinary function, parking the calling thread
        // instead of suspending a fiber. An ESync arrow deliberately: this is
        // what a `defun` reaches for, and a bjoroutine wants `sync` above.
        "sync/blocking", {Scheme = Scheme(["a"], [], makeFunType [makeEventType (TVar "a")] (TVar "a")); IsMutable = false }

        /// Pure — it builds an event and does not suspend. Failure is a value:
        /// "the fiber died" is a fact a supervisor needs, and an exception
        /// thrown inside an event continuation lands in a channel's matching
        /// loop rather than on the joining fiber's stack.
        "promise-join", {Scheme = Scheme(["a"], [], makeFunType [makePromiseType (TVar "a")] (makeEventType (makeResultType (TCon("System.Exception", [])) (TVar "a")))); IsMutable = false }

        /// `bjo` for the higher-order case, since a special form is not a
        /// value. A `(-> %a)` cannot suspend, so this spawns work that runs to
        /// completion — concurrent, but never suspending.
        "spawn-thunk", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [] (TVar "a")] (makePromiseType (TVar "a"))); IsMutable = false }

        "promise-done?", {Scheme = Scheme(["a"], [], makeFunType [makePromiseType (TVar "a")] boolType); IsMutable = false }

        // The CML surface. Every one of these is an ordinary arrow, and that is
        // the discipline rather than an oversight: building an event never
        // suspends, so it may be done anywhere — only `sync` is a yield point.
        "make-chan", {Scheme = Scheme(["a"], [], makeFunType [] (makeChanType (TVar "a"))); IsMutable = false }
        "chan-send", {Scheme = Scheme(["a"], [], makeFunType [makeChanType (TVar "a"); TVar "a"] (makeEventType unitType)); IsMutable = false }
        "chan-recv", {Scheme = Scheme(["a"], [], makeFunType [makeChanType (TVar "a")] (makeEventType (TVar "a"))); IsMutable = false }

        // Variadic, so its flat type takes the rest array and a `FunMeta` below
        // says how to fill it.
        "choose", {Scheme = Scheme(["a"], [], makeFunType [makeArrayType (makeEventType (TVar "a"))] (makeEventType (TVar "a"))); IsMutable = false }

        "wrap", {Scheme = Scheme(["a"; "b"], [], makeFunType [makeEventType (TVar "a"); makeFunType [TVar "a"] (TVar "b")] (makeEventType (TVar "b"))); IsMutable = false }
        "guard", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [] (makeEventType (TVar "a"))] (makeEventType (TVar "a"))); IsMutable = false }
        "with-nack", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [makeEventType unitType] (makeEventType (TVar "a"))] (makeEventType (TVar "a"))); IsMutable = false }
        "always", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"] (makeEventType (TVar "a"))); IsMutable = false }
        "never", {Scheme = Scheme(["a"], [], makeFunType [] (makeEventType (TVar "a"))); IsMutable = false }

        // Timers. Relative and absolute are different events and not
        // interchangeable — see the runtime's docstrings.
        "timeout", {Scheme = Scheme([], [], makeFunType [intType] (makeEventType unitType)); IsMutable = false }
        "at-time", {Scheme = Scheme([], [], makeFunType [TCon("System.DateTime", [])] (makeEventType unitType)); IsMutable = false }

        // "I know this can fail, and I still do not want the result." Dropping
        // a promise instead loses the exception inside it silently. `(ignore p)`
        // is this, via `Discard`'s implementation for `Promise`.
        "detach", {Scheme = Scheme(["a"], [], makeFunType [makePromiseType (TVar "a")] unitType); IsMutable = false }

        /// The last resort for synchronous .NET, and the only honest way to
        /// call it: the work is moved to a thread the pool can grow to replace,
        /// and the fiber suspends on the *result* rather than on the work.
        /// Failure is a value; cancellation is impossible. See §7.5.
        "blocking", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [] (TVar "a")] (makeEventType (makeResultType (TCon("System.Exception", [])) (TVar "a")))); IsMutable = false }

        /// A .NET async stream, as a channel and a promise that it is over.
        ///
        /// Two values because `Bjoml.Channel` has no close, and because a close
        /// could not carry the *failure* of a stream anyway. The rendezvous
        /// makes the pair exact rather than racy: the pump cannot finish until
        /// its last send was taken, so when the promise fires the channel is
        /// genuinely empty. See §7.6.
        "async-seq->chan", {Scheme = Scheme(["a"], [], makeFunType [makeAsyncSeqType (TVar "a")] (TTuple [makeChanType (TVar "a"); makePromiseType unitType])); IsMutable = false }

        // The other half of `(spawn-evt (f x))`, which the parser desugars to a
        // call of this over a nullary lambda holding the spawn. Not surface
        // API: called by hand it spawns under a token nothing will ever fire
        // unless the event it is part of actually loses.
        //
        // The starter takes nothing because the token does not travel as an
        // argument — it is pushed onto the dynamic environment around the call,
        // so the `bjo` inside captures it the way it captures everything else.
        "spawn-evt/start", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [] (makePromiseType (TVar "a"))] (makeEventType (makeResultType (TCon("System.Exception", [])) (TVar "a")))); IsMutable = false }

        // --- Cancellation (§6.1) ---------------------------------------------
        //
        // All ordinary arrows, including `cancelled`: it *builds* the event of
        // having been cancelled, and only the `sync` that offers it waits. That
        // is what lets a token be raced against real work in a `choose` rather
        // than polled, and it is why cancellation needs no machinery of its own
        // — a token is a promise, and a promise is already an event.

        /// The capability and the fact, handed back separately: whoever holds
        /// the thunk can cancel, whoever holds the token can only *observe*
        /// cancellation. A single value carrying both would make "here is my
        /// token, watch it" also mean "here is my token, fire it".
        ///
        /// The thunk takes the reason to raise. Cancelling twice is still a
        /// no-op, so the *first* reason is the one the token carries.
        "make-cancel", {Scheme = Scheme([], [], makeFunType [] (TTuple [makeFunType [cancelReasonType] unitType; cancelTokenType])); IsMutable = false }

        /// Persistent, and deliberately: cancellation is a fact, so every
        /// listener must see it and a listener that arrives late must still see
        /// it. The cost is §9's limitation 10 — a cancelled scope is finished,
        /// and resuming needs a fresh token rather than a reset.
        "cancelled", {Scheme = Scheme([], [], makeFunType [cancelTokenType] (makeEventType cancelReasonType)); IsMutable = false }

        /// The poll, for the compute loop that has no `sync` to hang a `choose`
        /// on. Answering without suspending is the whole point, so this is the
        /// one place a token is read rather than raced.
        ///
        /// `?`, so `bool`: a loop's exit test wants one bit and paying for an
        /// `Option` to get it would be a tax on the hot path this exists for.
        "cancelled?", {Scheme = Scheme([], [], makeFunType [cancelTokenType] boolType); IsMutable = false }

        /// `ev`, or `None` if the ambient scope goes down first. One combinator
        /// over any event rather than a cancellable variant of each primitive.
        ///
        /// What keeps a worker loop out of §4.4's trap: a fired token is
        /// persistent, so a loop that races one directly wins on it every
        /// iteration and spins. `None` makes leaving the natural spelling.
        "until-cancelled", {Scheme = Scheme(["a"], [], makeFunType [makeEventType (TVar "a")] (makeEventType (makeOptionType (TVar "a")))); IsMutable = false }

        /// The same poll, answering *why*. `None` is "not cancelled", which is
        /// the one case `cancelled?` collapses — so a loop tests with
        /// `cancelled?` and, having left, asks this what cleanup it owes.
        "cancel-reason", {Scheme = Scheme([], [], makeFunType [cancelTokenType] (makeOptionType cancelReasonType)); IsMutable = false }

        // The reasons themselves. A closed set: a library cannot add a case,
        // and `(Requested "...")` is the escape hatch for everything the four
        // do not name.
        "Requested", {Scheme = Scheme([], [], makeFunType [stringType] cancelReasonType); IsMutable = false }
        "Deadline", {Scheme = Scheme([], [], cancelReasonType); IsMutable = false }
        "Scope-Ended", {Scheme = Scheme([], [], cancelReasonType); IsMutable = false }
        "Failed", {Scheme = Scheme([], [], makeFunType [TCon("System.Exception", [])] cancelReasonType); IsMutable = false }

        /// `Promise.Forward`: cancelling the parent cancels the child, and not
        /// the other way round. Safe as a bare callback in a way user code
        /// never is — it stores a pointer and returns, so the borrowed thread
        /// it runs on stays borrowed.
        "link-cancel", {Scheme = Scheme([], [], makeFunType [cancelTokenType; cancelTokenType] unitType); IsMutable = false }

        /// The ambient token. `bjo` inherits the dynamic environment, so a
        /// `parameterize` around a spawn hands the token to every descendant —
        /// Go's `context.Context` without threading a parameter through every
        /// signature. Snapshot-at-spawn is the right semantics here precisely
        /// because a token is an immutable handle: the child sees the same
        /// promise, not a copy of a value that has since moved on.
        "current-cancel", {Scheme = Scheme([], [], makeParamType cancelTokenType); IsMutable = false }

        "current-output-port", {Scheme = Scheme([], [], makeParamType textOutputPortType); IsMutable = false }
        "current-input-port", {Scheme = Scheme([], [], makeParamType textInputPortType); IsMutable = false }
        "current-error-port", {Scheme = Scheme([], [], makeParamType textOutputPortType); IsMutable = false }

        /// The timer `with-deadline` desugars to. Not surface API: it fires a
        /// token whose thunk the desugar has just made, and called by hand it
        /// would be a fiber nobody owns racing a scope nobody established.
        "deadline-watch!", {Scheme = Scheme([], [], makeFunType [makeFunType [cancelReasonType] unitType; intType] unitType); IsMutable = false }

        /// `(raise e)` — the counterpart of `try`, which turns the failures it
        /// names into values. This is how one gets back out, and it keeps the
        /// stack trace the exception already has rather than starting a new one.
        ///
        /// Generic in its return type because it never returns: typing it
        /// `void` would keep it out of the one position that needs it, a `match`
        /// arm whose siblings produce a value.
        "raise", {Scheme = Scheme(["a"], [], makeFunType [TCon("System.Exception", [])] (TVar "a")); IsMutable = false }

        // The two halves of `parameterize`, which the parser desugars to. Not
        // surface API: called by hand they pair a push with a restore that no
        // `finally` is guarding.
        "parameter-push!", {Scheme = Scheme(["a"], [], makeFunType [makeParamType (TVar "a"); TVar "a"] dynEnvType); IsMutable = false }
        "dyn-restore!", {Scheme = Scheme([], [], makeFunType [dynEnvType] unitType); IsMutable = false }

        // `File.ReadLines` gives back an `IEnumerable<string>`, and interop maps
        // a constructed generic type to a name Bjolang cannot equate with
        // `(Seq string)` — so this one stays here while the rest of the file
        // operations live in `std/prelude`.
        "file-read-lines/seq", {Scheme = Scheme([], [], makeFunType [stringType] (makeSeqType stringType)); IsMutable = false }

        // The same, for an arbitrary read procedure: what `std/ports`' `file->seq`
        // is built on. It opens the file *inside* the iterator, once per
        // enumeration, and disposes it — which is what a sequence that owns its
        // source has to do to be walkable more than once. A `seql` cannot: it
        // would close over a reader opened before the sequence existed, and
        // share that one spent reader with every enumeration.
        "file-read/seq", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [textInputPortType] (TVar "a"); stringType] (makeSeqType (TVar "a"))); IsMutable = false }

        // `Path.GetDirectoryName` answers null for a root and for a bare
        // filename. Bjolang has no null to test against, so that sentinel
        // becomes `None` at the boundary — which is why this one is not in
        // `std/prelude` with the other path operations.
        "path-directory", {Scheme = Scheme([], [], makeFunType [stringType] (makeOptionType stringType)); IsMutable = false }

        // Raw port primitives. `std/prelude` is the documented surface — with
        // the reader-parameterised drains in `std/ports` — and these are the
        // pieces of it that cannot be written in Bjolang: a read that fails at
        // end of input, and the two drains, which want the collection builders
        // directly.
        "reader-read-line!", {Scheme = Scheme([], [], makeFunType [TCon("System.IO.TextReader", [])] stringType); IsMutable = false }
        // Char IO is a builtin pair rather than a `.Read` and a `.Write` at the
        // call site because a Bjolang `char` is a Unicode scalar and .NET's is a
        // UTF-16 code unit: both directions have to handle a surrogate pair, and
        // neither is something a caller should be reassembling by hand.
        "reader-read-char!", {Scheme = Scheme([], [], makeFunType [TCon("System.IO.TextReader", [])] charType); IsMutable = false }
        "writer-write-char!", {Scheme = Scheme([], [], makeFunType [TCon("System.IO.TextWriter", []); charType] unitType); IsMutable = false }
        // What `get-output-string` is built on. A builtin because the failure it
        // has to report — a port that is not a string port — is a value rather
        // than an exception on the .NET side.
        "writer->string", {Scheme = Scheme([], [], makeFunType [TCon("System.IO.TextWriter", [])] stringType); IsMutable = false }
        "reader->list", {Scheme = Scheme([], [], makeFunType [TCon("System.IO.TextReader", [])] (makeListType stringType)); IsMutable = false }
        "reader->vec", {Scheme = Scheme([], [], makeFunType [TCon("System.IO.TextReader", [])] (makeVecType stringType)); IsMutable = false }

        // Strings. `string-append` and `string-length` are `std/prelude`'s,
        // being ordinary .NET calls; this one is here to sit with the other
        // emptiness predicates rather than alone in the library.
        "string-empty?", {Scheme = Scheme([], [], makeFunType [stringType] boolType); IsMutable = false }

        // Keyword & Symbol conversions / predicates
        "keyword->string", {Scheme = Scheme([], [], makeFunType [keywordType] stringType); IsMutable = false }
        "string->keyword", {Scheme = Scheme([], [], makeFunType [stringType] keywordType); IsMutable = false }
        "symbol->string", {Scheme = Scheme([], [], makeFunType [symbolType] stringType); IsMutable = false }
        "string->symbol", {Scheme = Scheme([], [], makeFunType [stringType] symbolType); IsMutable = false }
        // Characters. A `char` is a Unicode scalar value, so `char->int` is a
        // codepoint rather than a UTF-16 code unit.
        //
        // No `string-ref`, and no other index-based accessor: indexing a
        // UTF-16 string by codepoint is O(n), so an innocent-looking loop over
        // indices is quadratic. String traversal belongs to a cursor.
        "char->int", {Scheme = Scheme([], [], makeFunType [charType] intType); IsMutable = false }
        "int->char", {Scheme = Scheme([], [], makeFunType [intType] charType); IsMutable = false }
        "char->string", {Scheme = Scheme([], [], makeFunType [charType] stringType); IsMutable = false }

        // Classification and case, R6RS §11.11. Builtins rather than
        // `std/prelude` calls for the same reason `char->string` is: interop
        // cannot resolve a method on a `BjoChar`, which is not a type Bjolang
        // can name.
        //
        // The *comparisons* are deliberately absent. `BjoChar` has C#
        // comparison operators, and `<` is typed `(-> %a %a bool)` and emitted
        // as the operator, so `char<?` is an ordinary alias in the library and
        // chains n-arily with no help from here.
        "char-upcase", {Scheme = Scheme([], [], makeFunType [charType] charType); IsMutable = false }
        "char-downcase", {Scheme = Scheme([], [], makeFunType [charType] charType); IsMutable = false }
        "char-titlecase", {Scheme = Scheme([], [], makeFunType [charType] charType); IsMutable = false }
        "char-foldcase", {Scheme = Scheme([], [], makeFunType [charType] charType); IsMutable = false }
        "char-alphabetic?", {Scheme = Scheme([], [], makeFunType [charType] boolType); IsMutable = false }
        "char-numeric?", {Scheme = Scheme([], [], makeFunType [charType] boolType); IsMutable = false }
        "char-whitespace?", {Scheme = Scheme([], [], makeFunType [charType] boolType); IsMutable = false }
        "char-upper-case?", {Scheme = Scheme([], [], makeFunType [charType] boolType); IsMutable = false }
        "char-lower-case?", {Scheme = Scheme([], [], makeFunType [charType] boolType); IsMutable = false }
        "char-title-case?", {Scheme = Scheme([], [], makeFunType [charType] boolType); IsMutable = false }
        // `Option` rather than a sentinel: "not a digit" is a real answer, and
        // -1 would be one the type does not mention.
        "digit-value", {Scheme = Scheme([], [], makeFunType [charType] (makeOptionType intType)); IsMutable = false }
        "char-general-category", {Scheme = Scheme([], [], makeFunType [charType] symbolType); IsMutable = false }

        // --- String cursors ---
        //
        // A position in a string, and what replaces the `string-ref` that is
        // deliberately missing. Every operation takes the string as well as the
        // cursor, because the cursor is a bare offset and decoding needs the
        // text — which is also the shape `Iterable` wants, its methods all
        // taking the sequence and the cursor both.
        //
        // There is no cursor-to-int and no int-to-cursor, and that is the
        // design rather than an omission: a cursor is only ever made from a
        // string and only ever moved one character at a time, so every value
        // that exists sits on a character boundary, and the offset inside can
        // change meaning when the storage does. Cursor *comparison* comes from
        // the C# operators, exactly as for `char`.
        "string-cursor-start", {Scheme = Scheme([], [], makeFunType [stringType] stringCursorType); IsMutable = false }
        "string-cursor-end", {Scheme = Scheme([], [], makeFunType [stringType] stringCursorType); IsMutable = false }
        "string-cursor-end?", {Scheme = Scheme([], [], makeFunType [stringType; stringCursorType] boolType); IsMutable = false }
        "string-cursor-ref", {Scheme = Scheme([], [], makeFunType [stringType; stringCursorType] charType); IsMutable = false }
        "string-cursor-next", {Scheme = Scheme([], [], makeFunType [stringType; stringCursorType] stringCursorType); IsMutable = false }
        "string-cursor-prev", {Scheme = Scheme([], [], makeFunType [stringType; stringCursorType] stringCursorType); IsMutable = false }
        "substring/cursors", {Scheme = Scheme([], [], makeFunType [stringType; stringCursorType; stringCursorType] stringType); IsMutable = false }
        // The character count, as against `string-length`'s storage count. Two
        // names because they are two questions with two answers and two costs:
        // this one walks.
        "string-count", {Scheme = Scheme([], [], makeFunType [stringType] intType); IsMutable = false }

        // StringBuilder, the accumulator behind the `Stringing` collector. Same
        // shape as the other builders: `add!` mutates and answers `Unit`, and
        // the identity it hands back never changes (§8.1).
        "stringbuilder-empty", {Scheme = Scheme([], [], makeFunType [] stringBuilderType); IsMutable = false }
        "stringbuilder-add!", {Scheme = Scheme([], [], makeFunType [stringBuilderType; charType] unitType); IsMutable = false }
        "stringbuilder-add-string!", {Scheme = Scheme([], [], makeFunType [stringBuilderType; stringType] unitType); IsMutable = false }
        "stringbuilder-length", {Scheme = Scheme([], [], makeFunType [stringBuilderType] intType); IsMutable = false }
        "stringbuilder->string", {Scheme = Scheme([], [], makeFunType [stringBuilderType] stringType); IsMutable = false }

        "keyword?", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"] boolType); IsMutable = false }
        "symbol?", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"] boolType); IsMutable = false }

        // List constructors (builtins backed by SchemeList)
        //
        // `list` is variadic through its `FunMeta` below, so `(list 1 2 3)`
        // spreads like any other `#:rest` function. Its *type* is the unary
        // `(-> (Array %a) (List %a))` that `#:rest` always resolves to, which is
        // what it means as a value: `(def f list)` binds the array form.
        "list", {Scheme = Scheme(["a"], [], makeFunType [makeArrayType (TVar "a")] (makeListType (TVar "a"))); IsMutable = false }
        "Cons", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; makeListType (TVar "a")] (makeListType (TVar "a"))); IsMutable = false }
        "Nil", {Scheme = Scheme(["a"], [], makeListType (TVar "a")); IsMutable = false }
        "cons", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; makeListType (TVar "a")] (makeListType (TVar "a"))); IsMutable = false }

        // List operations
        "list-empty", {Scheme = Scheme(["a"], [], makeFunType [] (makeListType (TVar "a"))); IsMutable = false }
        "list-head", {Scheme = Scheme(["a"], [], makeFunType [makeListType (TVar "a")] (TVar "a")); IsMutable = false }
        "list-tail", {Scheme = Scheme(["a"], [], makeFunType [makeListType (TVar "a")] (makeListType (TVar "a"))); IsMutable = false }
        "list-empty?", {Scheme = Scheme(["a"], [], makeFunType [makeListType (TVar "a")] boolType); IsMutable = false }
        "list-length", {Scheme = Scheme(["a"], [], makeFunType [makeListType (TVar "a")] intType); IsMutable = false }
        "list-reverse", {Scheme = Scheme(["a"], [], makeFunType [makeListType (TVar "a")] (makeListType (TVar "a"))); IsMutable = false }

        "list-map", {Scheme = Scheme(["a"; "b"], [], makeFunType [makeFunType [TVar "a"] (TVar "b"); makeListType (TVar "a")] (makeListType (TVar "b"))); IsMutable = false }
        "list-filter", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [TVar "a"] boolType; makeListType (TVar "a")] (makeListType (TVar "a"))); IsMutable = false }
        // Folds take the function first, then the identity, then the
        // collection: the two parts that describe *how* to fold stay together
        // at the call site instead of being split by the data.
        "list-foldl", {Scheme = Scheme(["a"; "b"], [], makeFunType [makeFunType [TVar "b"; TVar "a"] (TVar "b"); TVar "b"; makeListType (TVar "a")] (TVar "b")); IsMutable = false }
        "list-foldr", {Scheme = Scheme(["a"; "b"], [], makeFunType [makeFunType [TVar "a"; TVar "b"] (TVar "b"); TVar "b"; makeListType (TVar "a")] (TVar "b")); IsMutable = false }
        "list-for-each", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [TVar "a"] unitType; makeListType (TVar "a")] unitType); IsMutable = false }
        "list-ref", {Scheme = Scheme(["a"], [], makeFunType [makeListType (TVar "a"); intType] (TVar "a")); IsMutable = false }
        // No `list-count`. It was the same O(n) walk as `list-length` under a
        // second name, inherited from `SchemeList.Count` being a C# alias for
        // `Length` — one operation should have one name, and `length` is the
        // one every collection here answers to.

        // Vec operations
        "vec-empty", {Scheme = Scheme(["a"], [], makeFunType [] (makeVecType (TVar "a"))); IsMutable = false }
        "vec-ref", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a"); intType] (TVar "a")); IsMutable = false }
        "vec-set", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a"); intType; TVar "a"] (makeVecType (TVar "a"))); IsMutable = false }
        "vec-add", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a"); TVar "a"] (makeVecType (TVar "a"))); IsMutable = false }
        "vec-insert", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a"); intType; TVar "a"] (makeVecType (TVar "a"))); IsMutable = false }
        "vec-remove-at", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a"); intType] (makeVecType (TVar "a"))); IsMutable = false }
        "vec-pop", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a")] (makeVecType (TVar "a"))); IsMutable = false }
        "vec-pop-first", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a")] (makeVecType (TVar "a"))); IsMutable = false }
        "vec-slice", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a"); intType; intType] (makeVecType (TVar "a"))); IsMutable = false }
        "vec-merge", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a"); makeVecType (TVar "a")] (makeVecType (TVar "a"))); IsMutable = false }
        "vec-merge/pure", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a"); makeVecType (TVar "a")] (makeVecType (TVar "a"))); IsMutable = false }
        "vec-split", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a"); intType] (TTuple [makeVecType (TVar "a"); makeVecType (TVar "a")])); IsMutable = false }
        "vec-map", {Scheme = Scheme(["a"; "b"], [], makeFunType [makeFunType [TVar "a"] (TVar "b"); makeVecType (TVar "a")] (makeVecType (TVar "b"))); IsMutable = false }
        "vec-filter", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [TVar "a"] boolType; makeVecType (TVar "a")] (makeVecType (TVar "a"))); IsMutable = false }
        "vec-fold", {Scheme = Scheme(["a"; "b"], [], makeFunType [makeFunType [TVar "b"; TVar "a"] (TVar "b"); TVar "b"; makeVecType (TVar "a")] (TVar "b")); IsMutable = false }
        "vec-reduce", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [TVar "a"; TVar "a"] (TVar "a"); makeVecType (TVar "a")] (TVar "a")); IsMutable = false }
        "vec-for-each", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [TVar "a"] unitType; makeVecType (TVar "a")] unitType); IsMutable = false }
        "vec-for-each/range", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [TVar "a"] unitType; makeVecType (TVar "a"); intType; intType] unitType); IsMutable = false }
        "vec-iter", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [TVar "a"] boolType; makeVecType (TVar "a")] boolType); IsMutable = false }
        // `vec-length` rather than `vec-count`, even though the RRB member is
        // `Count`: the name a Bjolang program writes is the language's, not the
        // one the backing type happens to use, and `list-length`, `string-length`
        // and `array-length` had already settled which word that is.
        "vec-length", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a")] intType); IsMutable = false }
        // Note the shape of the pair: `vec-empty` is the *constructor* and
        // `vec-empty?` the predicate, which reads badly but is the name the
        // `?` convention demands. The predicate is O(1), off the same stored
        // count `vec-length` reads.
        "vec-empty?", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a")] boolType); IsMutable = false }
        "vec-contains", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a"); TVar "a"] boolType); IsMutable = false }
        "vec-compact", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a")] (makeVecType (TVar "a"))); IsMutable = false }

        // Array
        // Array operations
        "make-array",   { Scheme = Scheme(["a"], [], makeFunType [intType] (makeArrayType (TVar "a"))); IsMutable = false }
        "array-ref",    { Scheme = Scheme(["a"], [], makeFunType [makeArrayType (TVar "a"); intType] (TVar "a")); IsMutable = false }
        "array-set!",   { Scheme = Scheme(["a"], [], makeFunType [makeArrayType (TVar "a"); intType; TVar "a"] unitType); IsMutable = false }
        "array-length", { Scheme = Scheme(["a"], [], makeFunType [makeArrayType (TVar "a")] intType); IsMutable = false }


        // Option
        "Some", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"] (makeOptionType (TVar "a"))); IsMutable = false }
        "None", {Scheme = Scheme(["a"], [], makeOptionType (TVar "a")); IsMutable = false }
        "some?", {Scheme = Scheme(["a"], [], makeFunType [makeOptionType (TVar "a")] boolType); IsMutable = false }
        "none?", {Scheme = Scheme(["a"], [], makeFunType [makeOptionType (TVar "a")] boolType); IsMutable = false }
        "option-ref", {Scheme = Scheme(["a"], [], makeFunType [makeOptionType (TVar "a")] (TVar "a")); IsMutable = false }
        "option-ref-or", {Scheme = Scheme(["a"], [], makeFunType [makeOptionType (TVar "a"); TVar "a"] (TVar "a")); IsMutable = false }

        // Result. Built in for the same reason Option is: a `#:exceptions`
        // interop call returns one on every invocation, so it cannot be
        // something each file has to declare for itself.
        //
        // Every one of these is shadowed by a `Result` a module declares of its
        // own — the type definition rebinds `Ok` and `Err`, and both inference
        // and code generation look at what the module declared before they look
        // here. Modules that predate this and carry their own Result keep
        // compiling to their own union, unchanged.
        "Ok", {Scheme = Scheme(["e"; "a"], [], makeFunType [TVar "a"] (makeResultType (TVar "e") (TVar "a"))); IsMutable = false }
        "Err", {Scheme = Scheme(["e"; "a"], [], makeFunType [TVar "e"] (makeResultType (TVar "e") (TVar "a"))); IsMutable = false }

        // Syntax. The cases of `Bjolang.Runtime.Syntax`, monomorphic because
        // the type takes no argument: a form holds forms.
        //
        // `SSym` is an identifier and `SDatum` is a quoted symbol written as
        // data. They carry the same payload and mean opposite things to
        // hygiene: the first is renamed when a macro constructs it, the second
        // never is, because it is a value rather than a reference to a binding.
        "SSym", {Scheme = Scheme([], [], makeFunType [symbolType] syntaxType); IsMutable = false }
        "SDatum", {Scheme = Scheme([], [], makeFunType [symbolType] syntaxType); IsMutable = false }
        "SInt", {Scheme = Scheme([], [], makeFunType [stringType] syntaxType); IsMutable = false }
        "SStr", {Scheme = Scheme([], [], makeFunType [stringType] syntaxType); IsMutable = false }
        "SChar", {Scheme = Scheme([], [], makeFunType [charType] syntaxType); IsMutable = false }
        "SKey", {Scheme = Scheme([], [], makeFunType [keywordType] syntaxType); IsMutable = false }
        "SList", {Scheme = Scheme([], [], makeFunType [makeListType syntaxType] syntaxType); IsMutable = false }
        "SPunct", {Scheme = Scheme([], [], makeFunType [stringType] syntaxType); IsMutable = false }

        // What `,@` desugars to. Deliberately not a general `list-append`:
        // `std/prelude` publishes one of those, and a builtin sharing its name
        // is ambiguous to C# wherever both are in scope.
        "syntax-splice", {Scheme = Scheme([], [], makeFunType [makeListType syntaxType; makeListType syntaxType] (makeListType syntaxType)); IsMutable = false }

        // How a transformer rejects its input. Types as a `Syntax` so it can
        // stand beside the arms that return one; it never returns.
        "syntax-error", {Scheme = Scheme([], [], makeFunType [syntaxType; stringType] syntaxType); IsMutable = false }

        // The value-level `compare`: base-name equality on identifiers, which a
        // transformer gets as its third parameter and everything else has to
        // ask for. A `syntax-match` pattern's `'name` compiles to a call here.
        "syntax-ident=?", {Scheme = Scheme([], [], makeFunType [syntaxType; syntaxType] boolType); IsMutable = false }

        "syntax->string", {Scheme = Scheme([], [], makeFunType [syntaxType] stringType); IsMutable = false }
        "syntax-file", {Scheme = Scheme([], [], makeFunType [syntaxType] stringType); IsMutable = false }
        "syntax-line", {Scheme = Scheme([], [], makeFunType [syntaxType] intType); IsMutable = false }

        // Seq operations. A Seq is lazy: nothing below that returns one does any
        // work until the result is consumed.
        "seq-empty", {Scheme = Scheme(["a"], [], makeFunType [] (makeSeqType (TVar "a"))); IsMutable = false }
        "seq-empty?", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a")] boolType); IsMutable = false }
        // No `seq-tail`. `IEnumerable` has no cheap tail: the rest of a
        // sequence can only be described as the source minus a prefix, so
        // walking with one re-enumerates from the start per element and re-runs
        // whatever the generator does. `(seq-drop s 1)` says the same thing
        // without inviting the walk, and a walk is what `loop` is for.
        "seq-head", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a")] (TVar "a")); IsMutable = false }
        "seq-map", {Scheme = Scheme(["a"; "b"], [], makeFunType [makeFunType [TVar "a"] (TVar "b"); makeSeqType (TVar "a")] (makeSeqType (TVar "b"))); IsMutable = false }
        "seq-filter", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [TVar "a"] boolType; makeSeqType (TVar "a")] (makeSeqType (TVar "a"))); IsMutable = false }
        // Folds take the function first, then the identity, then the
        // collection, as `list-foldl` and `vec-fold` do.
        "seq-fold", {Scheme = Scheme(["a"; "b"], [], makeFunType [makeFunType [TVar "b"; TVar "a"] (TVar "b"); TVar "b"; makeSeqType (TVar "a")] (TVar "b")); IsMutable = false }
        // The generator maps a state to the next element and the state after
        // it, or to None to stop.
        "seq-unfold", {Scheme = Scheme(["a"; "s"], [], makeFunType [makeFunType [TVar "s"] (makeOptionType (TTuple [TVar "a"; TVar "s"])); TVar "s"] (makeSeqType (TVar "a"))); IsMutable = false }
        "seq-take", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a"); intType] (makeSeqType (TVar "a"))); IsMutable = false }
        "seq-drop", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a"); intType] (makeSeqType (TVar "a"))); IsMutable = false }
        "seq-append", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a"); makeSeqType (TVar "a")] (makeSeqType (TVar "a"))); IsMutable = false }
        "seq-for-each", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [TVar "a"] unitType; makeSeqType (TVar "a")] unitType); IsMutable = false }
        // `seq-length` walks the sequence, and a walk of a `Seq` over an
        // effectful source is a *consumption*: asking a `port->seq` its length
        // reads the port to the end. The name matches the rest of the library
        // rather than warning about that, because the hazard belongs to the
        // source — the same walk over a `list->seq` is merely O(n).
        "seq-length", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a")] intType); IsMutable = false }
        "seq-range", {Scheme = Scheme([], [], makeFunType [intType; intType] (makeSeqType intType)); IsMutable = false }

        // Seq conversions
        "list->seq", {Scheme = Scheme(["a"], [], makeFunType [makeListType (TVar "a")] (makeSeqType (TVar "a"))); IsMutable = false }
        "seq->list", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a")] (makeListType (TVar "a"))); IsMutable = false }
        "vec->seq", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a")] (makeSeqType (TVar "a"))); IsMutable = false }
        "seq->vec", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a")] (makeVecType (TVar "a"))); IsMutable = false }

        // The builders' mutators all answer `Unit` rather than the builder —
        // §8.1, and the precondition it asks for holds: all three are classes
        // that mutate in place and whose identity can never change, so nothing
        // is lost by not handing the reference back.
        //
        // The reason to bother is §8.2. Under a universal must-use rule a
        // mutator that returns something makes every call site ceremony, and
        // there are far more of those than there are places that wanted to
        // thread a builder through a loop slot. The ones that did are written
        // with the builder named, which reads better anyway.

        // VecBuilder operations
        "vecbuilder-empty", {Scheme = Scheme(["a"], [], makeFunType [] (makeVecBuilderType (TVar "a"))); IsMutable = false }
        "vec->vecbuilder", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a")] (makeVecBuilderType (TVar "a"))); IsMutable = false }
        "vecbuilder-add!", {Scheme = Scheme(["a"], [], makeFunType [makeVecBuilderType (TVar "a"); TVar "a"] unitType); IsMutable = false }
        "vecbuilder-set!", {Scheme = Scheme(["a"], [], makeFunType [makeVecBuilderType (TVar "a"); intType; TVar "a"] unitType); IsMutable = false }
        "vecbuilder-ref", {Scheme = Scheme(["a"], [], makeFunType [makeVecBuilderType (TVar "a"); intType] (TVar "a")); IsMutable = false }
        "vecbuilder-length", {Scheme = Scheme(["a"], [], makeFunType [makeVecBuilderType (TVar "a")] intType); IsMutable = false }
        "vecbuilder->vec", {Scheme = Scheme(["a"], [], makeFunType [makeVecBuilderType (TVar "a")] (makeVecType (TVar "a"))); IsMutable = false }

        // SchemeListBuilder operations
        "listbuilder-empty", {Scheme = Scheme(["a"], [], makeFunType [] (makeListBuilderType (TVar "a"))); IsMutable = false }
        "list->builder", {Scheme = Scheme(["a"], [], makeFunType [makeListType (TVar "a")] (makeListBuilderType (TVar "a"))); IsMutable = false }
        "list->listbuilder", {Scheme = Scheme(["a"], [], makeFunType [makeListType (TVar "a")] (makeListBuilderType (TVar "a"))); IsMutable = false }
        "listbuilder-add!", {Scheme = Scheme(["a"], [], makeFunType [makeListBuilderType (TVar "a"); TVar "a"] unitType); IsMutable = false }
        "listbuilder-add-range!", {Scheme = Scheme(["a"], [], makeFunType [makeListBuilderType (TVar "a"); makeSeqType (TVar "a")] unitType); IsMutable = false }
        "listbuilder-length", {Scheme = Scheme(["a"], [], makeFunType [makeListBuilderType (TVar "a")] intType); IsMutable = false }
        "listbuilder->list", {Scheme = Scheme(["a"], [], makeFunType [makeListBuilderType (TVar "a")] (makeListType (TVar "a"))); IsMutable = false }

        // Cursors over the collections' native struct enumerators. `done?` is
        // what advances — the iteration protocol allows exactly that, and it is
        // what lets `next` be the identity and the traversal allocate nothing
        // after the cursor itself.
        "vec-cursor", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a")] (makeVecCursorType (TVar "a"))); IsMutable = false }
        "vec-cursor-done?", {Scheme = Scheme(["a"], [], makeFunType [makeVecCursorType (TVar "a")] boolType); IsMutable = false }

        // The same three for a `Seq`, holding the enumerator so that a walk
        // pulls each element once. A `Seq` has no tail to step to, so without
        // these the only cursor available is the sequence itself and every step
        // re-enumerates it from the start.
        //
        // Consuming, and the one cursor here that is: stepping it advances the
        // underlying enumerator, so two walks from one cursor share a position.
        // `(start s)` makes a fresh one per walk, which is what keeps a `Seq`
        // re-enumerable.
        "seq-cursor", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a")] (makeSeqCursorType (TVar "a"))); IsMutable = false }
        "seq-cursor-done?", {Scheme = Scheme(["a"], [], makeFunType [makeSeqCursorType (TVar "a")] boolType); IsMutable = false }
        "seq-cursor-current", {Scheme = Scheme(["a"], [], makeFunType [makeSeqCursorType (TVar "a")] (TVar "a")); IsMutable = false }
        "vec-cursor-current", {Scheme = Scheme(["a"], [], makeFunType [makeVecCursorType (TVar "a")] (TVar "a")); IsMutable = false }
      ]
      Registry = emptyRegistry
      FunMetas = Map.ofList [
          // The recorded element type is the declaration's own rigid variable.
          // That is fine: the call site unifies each rest slot against a *fresh*
          // meta and lets the flat unification against the instantiated function
          // type supply the real one, so `FunMeta` is consulted only for the
          // call's shape. See the comment in `infer`'s structured-call branch.
          ("list", { MandatoryCount = 0; KeywordParams = []; RestParam = Some (TVar "a") })
          // `(choose e1 e2 e3)`. Without this the call site would have to pass
          // the array itself: the flat type takes one `(Array (Event %a))` and
          // would refuse the shorter argument list.
          ("choose", { MandatoryCount = 0; KeywordParams = []; RestParam = Some (TCon("Event", [ TVar "a" ])) })
      ]
      CurrentModule = "" }
