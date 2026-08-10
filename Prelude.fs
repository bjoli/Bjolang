module Bjolang.Prelude

open Bjolang.TypedAST.TypeConstants
open Bjolang.TypedAST

// Helper for function types
let makeFunType args ret = tfun args ret

let makeVecType a = TCon("Vec", [a])
let makeVecBuilderType a = TCon("VecBuilder", [a])
let makeListBuilderType a = TCon("ListBuilder", [a])
let makeMapBuilderType k v = TCon("MapBuilder", [k; v])
let makeVecCursorType a = TCon("VecCursor", [a])
let makeMapCursorType k v = TCon("MapCursor", [k; v])
let makeListType a = TCon("List", [a])
let makeSeqType a = TCon("Seq", [a])
let makeOptionType a = TCon("Option", [a])
let makeResultType e a = TCon("Result", [e; a])
let makeMapType k v = TCon("Map", [k; v])
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
/// A newtype over `(Promise Unit)` and nothing else — the runtime
/// representation *is* a `Bjoml.Promise<Unit>`, so cancelling is
/// `TrySetResult`, asking is `IsCompleted`, waiting is `Join`, and linking a
/// child to a parent is `Forward`. No new runtime machinery, which is the
/// point of §6.1.
///
/// Nominal rather than an alias, though, because the two are not
/// interchangeable *to a program*: `promise-join` on a token would hand back a
/// `(Result Exception Unit)` and invite a `match` on a failure that cannot
/// happen, and `detach` on one would arm an unhandled-exception report for a
/// promise nothing ever fails. A distinct `TCon` costs nothing at runtime and
/// keeps both out of reach.
let cancelTokenType = TCon("CancelToken", [])

/// A saved dynamic environment. Produced by `parameter-push!` and consumed by
/// `dyn-restore!`, both of which only ever appear in a `parameterize` desugar.
let dynEnvType = TCon("DynEnv", [])

/// The port types, as `std/prelude` publishes them. Named here because the
/// three standard ports are builtin bindings and have to be typed before
/// `prelude.bjo` exists to say it.
let textInputPortType = TCon("System.IO.TextReader", [])
let textOutputPortType = TCon("System.IO.TextWriter", [])


let emptyRegistry : TraitRegistry =
    { LocalTraits = Set.empty
      LocalTypes = Set.ofList ["List"; "Vec"; "VecBuilder"; "ListBuilder"; "MapBuilder"; "VecCursor"; "MapCursor"; "Seq"; "Option"; "Result"; "Map"; "Keyword"; "Symbol"; "Array"; "Param"; "DynEnv"; "Promise"; "Event"; "Chan"; "CancelToken"; "AsyncSeq"]
      Traits = Map.empty
      TraitMethods = Map.empty
      Implementations = Map.empty
      ImplTargets = Map.empty
      BlanketImpls = Map.empty
      TraitOrigins = Map.empty
      InlineMethods = Map.empty
      Aliases = Map.empty
      Records = Map.empty
      RecordFields = Map.empty
      Unions = Map.empty
      ClrClasses = Map.empty
      ClrExterns = Map.empty

      // Seeded rather than declared, because the type it is about is built in
      // and there is nowhere in source to hang the declaration. `Result` is the
      // whole of §8.2's third level: a discarded error is exactly the failure
      // the type exists to make visible.
      NoDiscard = Set.ofList [ "Result" ] }

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

        // Comparison Operators
        "=", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }
        "<", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }
        ">", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }
        "<=", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }
        ">=", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }

        // Polymorphic equality
        // eq? : 'a -> 'a -> bool (Pointer/Reference equality)
        "eq?", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }
        // equal? : 'a -> 'a -> bool (Structural/Generic equality)
        "equal?", {Scheme = Scheme(["a"], [], makeFunType [TVar "a"; TVar "a"] boolType); IsMutable = false }


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
        "make-cancel", {Scheme = Scheme([], [], makeFunType [] (TTuple [makeFunType [] unitType; cancelTokenType])); IsMutable = false }

        /// Persistent, and deliberately: cancellation is a fact, so every
        /// listener must see it and a listener that arrives late must still see
        /// it. The cost is §9's limitation 10 — a cancelled scope is finished,
        /// and resuming needs a fresh token rather than a reset.
        "cancelled", {Scheme = Scheme([], [], makeFunType [cancelTokenType] (makeEventType unitType)); IsMutable = false }

        /// The poll, for the compute loop that has no `sync` to hang a `choose`
        /// on. Answering without suspending is the whole point, so this is the
        /// one place a token is read rather than raced.
        "cancelled?", {Scheme = Scheme([], [], makeFunType [cancelTokenType] boolType); IsMutable = false }

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
        // filename. Bjolang has no null to test against, so that sentinel is
        // absorbed at the boundary — which is why this one is not in
        // `std/prelude` with the other path operations.
        "path-directory", {Scheme = Scheme([], [], makeFunType [stringType] stringType); IsMutable = false }

        // Raw port primitives. `std/prelude` is the documented surface — with
        // the reader-parameterised drains in `std/ports` — and these are the
        // pieces of it that cannot be written in Bjolang: a read that fails at
        // end of input, and the two drains, which want the collection builders
        // directly.
        "reader-read-line!", {Scheme = Scheme([], [], makeFunType [TCon("System.IO.TextReader", [])] stringType); IsMutable = false }
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
        "vec-get", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a"); intType] (TVar "a")); IsMutable = false }
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
        "option-get", {Scheme = Scheme(["a"], [], makeFunType [makeOptionType (TVar "a")] (TVar "a")); IsMutable = false }
        "option-get-or", {Scheme = Scheme(["a"], [], makeFunType [makeOptionType (TVar "a"); TVar "a"] (TVar "a")); IsMutable = false }

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

        // Seq operations. A Seq is lazy: nothing below that returns one does any
        // work until the result is consumed.
        "seq-empty", {Scheme = Scheme(["a"], [], makeFunType [] (makeSeqType (TVar "a"))); IsMutable = false }
        "seq-empty?", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a")] boolType); IsMutable = false }
        "seq-head", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a")] (TVar "a")); IsMutable = false }
        "seq-tail", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a")] (makeSeqType (TVar "a"))); IsMutable = false }
        "seq-map", {Scheme = Scheme(["a"; "b"], [], makeFunType [makeFunType [TVar "a"] (TVar "b"); makeSeqType (TVar "a")] (makeSeqType (TVar "b"))); IsMutable = false }
        "seq-filter", {Scheme = Scheme(["a"], [], makeFunType [makeFunType [TVar "a"] boolType; makeSeqType (TVar "a")] (makeSeqType (TVar "a"))); IsMutable = false }
        // Folds take the function first, then the identity, then the
        // collection, as `list-foldl` and `vec-fold` do.
        "seq-fold", {Scheme = Scheme(["a"; "b"], [], makeFunType [makeFunType [TVar "b"; TVar "a"] (TVar "b"); TVar "b"; makeSeqType (TVar "a")] (TVar "b")); IsMutable = false }
        // The generator maps a state to the next element and the state after
        // it, or to None to stop.
        "seq-unfold", {Scheme = Scheme(["a"; "s"], [], makeFunType [makeFunType [TVar "s"] (makeOptionType (TTuple [TVar "a"; TVar "s"])); TVar "s"] (makeSeqType (TVar "a"))); IsMutable = false }
        "seq-take", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a"); intType] (makeSeqType (TVar "a"))); IsMutable = false }
        "seq-skip", {Scheme = Scheme(["a"], [], makeFunType [makeSeqType (TVar "a"); intType] (makeSeqType (TVar "a"))); IsMutable = false }
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
        "vecbuilder-get", {Scheme = Scheme(["a"], [], makeFunType [makeVecBuilderType (TVar "a"); intType] (TVar "a")); IsMutable = false }
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

        "mapbuilder-empty", {Scheme = Scheme(["k"; "v"], [], makeFunType [] (makeMapBuilderType (TVar "k") (TVar "v"))); IsMutable = false }
        "mapbuilder-add!", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapBuilderType (TVar "k") (TVar "v"); TVar "k"; TVar "v"] unitType); IsMutable = false }
        "mapbuilder->map", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapBuilderType (TVar "k") (TVar "v")] (makeMapType (TVar "k") (TVar "v"))); IsMutable = false }

        // Cursors over the collections' native struct enumerators. `done?` is
        // what advances — the iteration protocol allows exactly that, and it is
        // what lets `next` be the identity and the traversal allocate nothing
        // after the cursor itself.
        "vec-cursor", {Scheme = Scheme(["a"], [], makeFunType [makeVecType (TVar "a")] (makeVecCursorType (TVar "a"))); IsMutable = false }
        "vec-cursor-done?", {Scheme = Scheme(["a"], [], makeFunType [makeVecCursorType (TVar "a")] boolType); IsMutable = false }
        "vec-cursor-current", {Scheme = Scheme(["a"], [], makeFunType [makeVecCursorType (TVar "a")] (TVar "a")); IsMutable = false }

        "map-cursor", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v")] (makeMapCursorType (TVar "k") (TVar "v"))); IsMutable = false }
        "map-cursor-done?", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapCursorType (TVar "k") (TVar "v")] boolType); IsMutable = false }
        "map-cursor-current", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapCursorType (TVar "k") (TVar "v")] (TTuple [TVar "k"; TVar "v"])); IsMutable = false }

        // Map (CHAMP) operations
        "map-empty", {Scheme = Scheme(["k"; "v"], [], makeFunType [] (makeMapType (TVar "k") (TVar "v"))); IsMutable = false }
        "map-ref", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v"); TVar "k"] (TVar "v")); IsMutable = false }
        "map-get", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v"); TVar "k"] (TVar "v")); IsMutable = false }
        "map-get-or", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v"); TVar "k"; TVar "v"] (TVar "v")); IsMutable = false }
        "map-try-get", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v"); TVar "k"] (makeOptionType (TVar "v"))); IsMutable = false }
        "map-set", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v"); TVar "k"; TVar "v"] (makeMapType (TVar "k") (TVar "v"))); IsMutable = false }
        "map-add", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v"); TVar "k"; TVar "v"] (makeMapType (TVar "k") (TVar "v"))); IsMutable = false }
        "map-remove", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v"); TVar "k"] (makeMapType (TVar "k") (TVar "v"))); IsMutable = false }
        "map-contains?", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v"); TVar "k"] boolType); IsMutable = false }
        "map-has-key?", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v"); TVar "k"] boolType); IsMutable = false }
        "map-length", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v")] intType); IsMutable = false }
        "map-empty?", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v")] boolType); IsMutable = false }
        "map-clear", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v")] (makeMapType (TVar "k") (TVar "v"))); IsMutable = false }
        "map-keys", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v")] (makeSeqType (TVar "k"))); IsMutable = false }
        "map-values", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v")] (makeSeqType (TVar "v"))); IsMutable = false }
        "map-merge", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v"); makeMapType (TVar "k") (TVar "v")] (makeMapType (TVar "k") (TVar "v"))); IsMutable = false }
        // Every callback below takes the *pair*, as one argument. A Map's
        // element is its `(Tuple %k %v)`: `Iterable`'s `%elem` and `Foldable`'s
        // `%item` say so, and so do `map->list`, `map->seq`,
        // `map-cursor-current` and the `#map(...)` literal. A trait signature
        // mentioning one element takes a one-argument callback, so a
        // two-argument function over a key and a value could not be passed
        // where one is expected.
        "map-merge-with", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeFunType [TTuple [TVar "k"; TVar "v"; TVar "v"]] (TVar "v"); makeMapType (TVar "k") (TVar "v"); makeMapType (TVar "k") (TVar "v")] (makeMapType (TVar "k") (TVar "v"))); IsMutable = false }
        "map-for-each", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeFunType [TTuple [TVar "k"; TVar "v"]] unitType; makeMapType (TVar "k") (TVar "v")] unitType); IsMutable = false }
        "map-iter", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeFunType [TTuple [TVar "k"; TVar "v"]] boolType; makeMapType (TVar "k") (TVar "v")] boolType); IsMutable = false }
        "map-fold", {Scheme = Scheme(["k"; "v"; "s"], [], makeFunType [makeFunType [TVar "s"; TTuple [TVar "k"; TVar "v"]] (TVar "s"); TVar "s"; makeMapType (TVar "k") (TVar "v")] (TVar "s")); IsMutable = false }
        "map-filter", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeFunType [TTuple [TVar "k"; TVar "v"]] boolType; makeMapType (TVar "k") (TVar "v")] (makeMapType (TVar "k") (TVar "v"))); IsMutable = false }
        "map-map", {Scheme = Scheme(["k"; "v"; "v2"], [], makeFunType [makeFunType [TTuple [TVar "k"; TVar "v"]] (TVar "v2"); makeMapType (TVar "k") (TVar "v")] (makeMapType (TVar "k") (TVar "v2"))); IsMutable = false }

        // The one place a pair will not do. `Functor`'s `(-> %a %b)` has to
        // replace the element type and give back the same shape, and the only
        // argument of `(Map %k %v)` free to move is `%v` — so a functorial map
        // over a Map sees the value, with the key riding along.
        "map-map-values", {Scheme = Scheme(["k"; "v"; "v2"], [], makeFunType [makeFunType [TVar "v"] (TVar "v2"); makeMapType (TVar "k") (TVar "v")] (makeMapType (TVar "k") (TVar "v2"))); IsMutable = false }

        // Map conversions
        "list->map", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeListType (TTuple [TVar "k"; TVar "v"])] (makeMapType (TVar "k") (TVar "v"))); IsMutable = false }
        "map->list", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v")] (makeListType (TTuple [TVar "k"; TVar "v"]))); IsMutable = false }
        "vec->map", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeVecType (TTuple [TVar "k"; TVar "v"])] (makeMapType (TVar "k") (TVar "v"))); IsMutable = false }
        "map->vec", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v")] (makeVecType (TTuple [TVar "k"; TVar "v"]))); IsMutable = false }
        "seq->map", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeSeqType (TTuple [TVar "k"; TVar "v"])] (makeMapType (TVar "k") (TVar "v"))); IsMutable = false }
        "map->seq", {Scheme = Scheme(["k"; "v"], [], makeFunType [makeMapType (TVar "k") (TVar "v")] (makeSeqType (TTuple [TVar "k"; TVar "v"]))); IsMutable = false }
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
