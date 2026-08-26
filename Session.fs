/// The compiler state that belongs to a compilation rather than to the process.
///
/// The compiler keeps several counters and tables at module level. That was
/// safe as long as one process compiled one module, which is what
/// `compileDependencyOutOfProcess` bought by spawning a compiler per import.
/// Compiling a dependency in-process — and running a REPL at all — means the
/// same process holds several compilations, so the boundary has to be
/// something other than a process.
///
/// This is that boundary. What is *in* it is state whose correct starting value
/// is "what a fresh process would have"; what is deliberately left out is state
/// that is about the process, and would be wrong to reset.
///
/// **Per compilation** — captured and restored here:
///
///   * `Gensym.counter`. The reason is determinism: a `.dll` is judged stale by
///     timestamp, so unchanged source that compiles to a different `tmp__37`
///     because something else was built first is a cache that starts missing.
///   * `Unification.nextMetaId`. Ids are compared only within one inference
///     run, and one of them can reach a diagnostic.
///   * `Macro.table`, `Macro.localMacros`, `Macro.expansions`. Which macros
///     exist is decided by *this* module's imports under *this* module's
///     modifiers. A leaked entry does not raise an error; it makes a form read
///     as a macro call because a different file imported something.
///   * `Parser.introducedNames`. Correct if leaked, but it only grows.
///   * `Inference.wantedQueue` and `Inference.openLiterals`. Both empty after a
///     compilation that succeeded, and neither after one that threw.
///
/// **Per process** — deliberately not touched:
///
///   * `Pipeline.assemblyPaths` and `resolverInstalled`. The CLR's assembly
///     resolution is process-wide, and so is the loaded set: an assembly cannot
///     be unloaded from the default context, so forgetting where it came from
///     would only break the next lookup.
///   * `Pipeline.compileLibrary`. A backend hook, set once by `Program`.
///   * `Pipeline.walking`, the staleness walk's memo. Empty except during a
///     walk, and emptied by the one that started it, so there is nothing here
///     for a compilation to inherit.
///   * `DotNetInterop.typeCache` and `extraAssemblies`. Reflection over what
///     the process has loaded, which is the same answer for every compilation
///     in it.
///   * `Parser.expandHook` and `Parser.isMacroName`. Function pointers into
///     `Macro`, installed idempotently; it is the tables behind them that are
///     scoped.
///   * `Unification.heldMetaIds` and `heldLocalMetaIds`, likewise.
///
/// `Program.fs` used to name a "`Codegen` shadowed-builtin set" among the
/// reasons a sub-compilation had to be a subprocess. There is no such thing:
/// `Codegen`'s per-compilation data — `GlobalBindings`, `UnionCases`, the
/// registry — is built inside `generateProgram` and threaded through `ctx`. The
/// comment was stale and is gone.
module Bjolang.Session

type Scope =
    { Gensym: int
      MetaCounter: int
      Macros: Macro.State
      Introduced: Set<string> }

/// What a compilation would find in a process that had done nothing else.
let fresh: Scope =
    { Gensym = 0
      MetaCounter = 0
      Macros = Macro.emptyState
      Introduced = Set.empty }

let capture () : Scope =
    { Gensym = Gensym.snapshot ()
      MetaCounter = Unification.snapshotMetaCounter ()
      Macros = Macro.snapshot ()
      Introduced = Parser.snapshotIntroduced () }

let restore (scope: Scope) : unit =
    Gensym.restore scope.Gensym
    Unification.restoreMetaCounter scope.MetaCounter
    Macro.restore scope.Macros
    Parser.restoreIntroduced scope.Introduced
    // Not part of the scope value: the queue is either empty or garbage, and
    // there is never a reason to put a previous compilation's obligations back.
    // The same goes for the numeric literals still waiting to be settled.
    Inference.clearWanteds ()
    Inference.clearNumericLiterals ()

/// Runs `f` as though it were the first thing this process compiled, and leaves
/// everything as it was found.
///
/// This is what makes an in-process dependency build produce the `.dll` the
/// subprocess produced. The outer compilation is mid-flight — it is *why* the
/// dependency is being built — so restoring is not tidiness: without it the
/// importing module carries on with the dependency's macro table.
///
/// `try/finally` rather than a straight sequence, because a dependency that
/// fails to compile raises, and the outer compilation's own diagnostic is
/// printed by a `with` above this.
let isolated (f: unit -> 'a) : 'a =
    let outer = capture ()
    restore fresh

    try
        f ()
    finally
        restore outer

/// Runs one REPL entry: everything a compilation owns is reset, except the
/// invented-name counter.
///
/// The counter is the one field a REPL wants the *opposite* of a batch build's
/// behaviour for. Each entry is emitted as its own assembly, and entry 4 and
/// entry 9 both starting from zero means both emit a member called `tmp__12`.
/// Since entry 9's assembly references entry 4's, the two are in scope
/// together, and which one a name binds to stops being decidable.
///
/// Determinism is not lost by this: a REPL entry produces an assembly nothing
/// keeps and no staleness check ever looks at.
let replEntry (f: unit -> 'a) : 'a =
    restore { fresh with Gensym = Gensym.snapshot () }
    f ()
