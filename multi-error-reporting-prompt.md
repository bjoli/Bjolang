
# Bjolang compiler: report every error in a phase, not just the first, and add `--check`

Today one `failwith` ends the build. `runFullFrontendPipeline` wraps the entire frontend in a
single `try/with` (`Pipeline.fs:1939-1941`), so a file with five broken `defun`s is fixed and
recompiled five times. Collect diagnostics instead. Then add a `--check` flag that runs the
frontend and stops.

## Ground rules

- `Diagnostics.isDiagnostic` (`Diagnostics.fs:27`) separates an intentional diagnostic from a
  compiler bug by *exact* exception type equality: `failwith` raises exactly
  `System.Exception`, every runtime fault raises a subclass. Keep that. Collect only
  exceptions satisfying it; let everything else propagate with its stack trace intact.
- Do not touch the ~500 `failwith`/`failwithf` call sites. The design exists to avoid that.
- Every file in `TestFiles/errors/` must still be rejected with a non-zero exit, and every
  `;; EXPECT-ERROR:` substring must still appear in the output. Run `./run_tests.py` after
  each phase; green before you start the next.
- Comment style is in `CLAUDE.md`. Read it. Rationale belongs in a file-top comment.

## The model: phase gates, not free-running recovery

Each phase collects every error it finds, then compilation **halts before the next phase**.
Parse errors never reach the type checker. This bounds cascades. Recovery within a phase is
cheap here because the reader has already established form boundaries — this is a Lisp, so
there is no token-stream resynchronisation problem to solve.

Within the type-check phase, a failed declaration poisons its own name so that dependent
declarations stay quiet (Phase 4).

## Phase 1 — a collector in `Diagnostics.fs`

Add, alongside the existing `let mutable verbose` (`Diagnostics.fs:113`):

- `type Severity = Error | Warning`
- `type Diagnostic = { Severity: Severity; Message: string; Where: Lexer.Range option; Phase: string }`
- a module-level `ResizeArray<Diagnostic>` with `reset ()`, `record d`, `hasErrors ()`, `count ()`
- `report ()` — sort by file, line, column; drop exact duplicates; print at most 25, then
  `... and N more errors.`
- `recover (phase: string) (where: Lexer.Range option) (fallback: 'a) (f: unit -> 'a) : 'a`

`recover` is the only new control flow in the compiler:

    try f ()
    with ex when isDiagnostic ex ->
        record (ofException phase where ex)
        fallback

The `when` filter is load-bearing: it runs before unwinding, so a non-diagnostic exception
keeps its stack trace — the same reason the filters at `InferExpr.fs:201` and
`CheckDecl.fs:209` are written that way.

`ofException` reuses `humanize` (`Diagnostics.fs:100`) and takes its location from `where`
only when the message carries none (`needsLocation`, `Diagnostics.fs:38`).

Route `warn` (`Diagnostics.fs:123`) through `record` with `Severity = Warning`, but keep the
literal `Warning: ` stderr prefix at report time — `TestFiles/warnings/` matches on it.
Leave `reportFailure` as-is for the compiler-bug path.

## Phase 2 — parse barrier (low risk, do this first)

`DeclParser.parseModule` (`DeclParser.fs:990`) is `List.collect parseDeclForms exprs` over
independent top-level forms. Wrap the per-form call in `recover "parse" (Some (getRange form)) []`.

That one line buys multi-error reporting at declaration granularity, with no change to `Decl`
and no error nodes. Verify on a file with three separately-broken `defun`s.

Gate: after `parseModule`, if `hasErrors ()`, report and return `None` from
`runFullFrontendPipeline`. Do not type check.

## Phase 3 — type-check barrier

`Inference.checkProgram` (`Inference.fs:187`) folds `Env * Sigs` across declarations via
`CheckDecl.checkDecl` (`CheckDecl.fs:206`). Wrap each declaration: on failure record the
diagnostic, keep the *previous* `env` and `sigs`, contribute no `TDecl`.

Do the same for the independent whole-module passes that can reject a program — `MustUse.run`,
`Exhaustiveness.run`, `ColourCheck.run` — each iterates declarations, so each takes a
per-declaration `recover`. Gate again before monomorphisation (`Pipeline.fs:1892`).

## Phase 4 — cascade suppression

A skipped declaration is absent from `Env`, so every caller now reports "unknown identifier".
Suppress that in two layers:

1. **Bind a placeholder.** When a declaration fails, bind its name in `Env` to a fresh
   unconstrained scheme (`forall a. a`). It unifies with anything, so dependents type-check
   silently. No new type constructor is needed — a fresh metavariable already behaves this
   way. Take the name from the `Decl`; it is known even when the body fails to check.
2. **Filter at report time.** Track poisoned names and drop any *later* diagnostic naming one.
   This covers types, traits and instances, which cannot be pre-bound as cleanly as values.

State this hazard in the code comment: an unconstrained placeholder can make a dependent
declaration type-check *successfully*, producing a `TDecl` built on a lie. That is safe only
because the phase gate stops the build. A poisoned `Env` must never reach codegen — assert it.

## Phase 5 — wire the sink

- `Pipeline.fs:1939-1941`: the outer `with ex -> reportFailure ex; None` records the exception
  when it is a diagnostic, then calls `report ()`, then returns `None`. Bug exceptions still
  go through `reportFailure`.
- `Build.compile` (`Build.fs:637`): call `report ()` and return 1 whenever `hasErrors ()`,
  before `generateSource` (`Build.fs:83`).
- `Build.runBatch`: `reset ()` per input file. The collector is process-global — like
  `Diagnostics.captured` (`Diagnostics.fs:139`) — and batch mode reuses the process.
- `Repl.fs`: `reset ()` per entry. One entry is one form, so behaviour should not change;
  confirm `TestFiles/repl/` goldens still pass.

## Phase 6 — the `--check` flag

Three edits in `Program.fs`:

- `Check: bool` in `CompilerOptions` (`Program.fs:24-45`)
- `| "--check" :: rest -> parseArgs rest { opts with Check = true }` (`Program.fs:110-116`)
- one `printfn` in `printUsage` (`Program.fs:57-94`): check for errors without generating code

Then in `Build.compile`: when `Check` is set, return immediately after the frontend and the
diagnostics report — before `generateSource` (`Build.fs:83`), and therefore before all three
C# backends at `Build.fs:595`. Verify nothing warms Roslyn on this path: inspect
`CSharpEmit.prewarmed` (`CSharpEmit.fs:162`), `preferInProcess` (`CSharpEmit.fs:217`) and
`Build.installDependencyBackend` (`Program.fs:132`). `--check` must spawn no `csc`, no
`dotnet build`, and load no Roslyn assembly.

Conflicts: `--check` with `--emit-cs` or `--if-stale` is contradictory — print why, exit 1.
`--check --batch` checks every file and exits 1 if any fails. On failure print `N errors.`
before exiting 1; on success add nothing beyond the existing progress output.

## Phase 7 — tests

- `judge_error_test` (`run_tests.py:546-565`) already loops over *every* `;; EXPECT-ERROR:`
  line and requires each substring in the output, so multi-error fixtures need no runner
  change. Add `TestFiles/errors/multi_parse_errors.bjo` and `multi_type_errors.bjo`, three
  expectations each.
- Add `;; EXPECT-NO-ERROR:` to `judge_error_test`, mirroring the existing `EXPECT-NO-WARNING`
  judge. Use it in a cascade fixture: one broken declaration plus one caller, asserting the
  caller's "unknown identifier" never appears.
- `--check`: one good file (exit 0, no assembly written) and one bad file (exit 1, messages
  identical to a normal build).

## Out of scope — do not do these

- **No `EError` node in `Ast.Expr` or `TypedAST`.** It would force exhaustive-match updates
  across 262 KB of `Codegen.fs` plus `Lowering`, `Monomorphise` and `SeqFusion`. One error per
  declaration is the target.
- **No rewrite of raise sites** into structured diagnostic constructors.
- **No column added to `formatPos`** (`Lexer.fs:31`), even though `Range` carries one.
- **Do not fix the reader.** `Pipeline.read` (`Pipeline.fs:109-176`) lets `]` close a form
  opened by `(`, and lets end-of-input close every open form (`Pipeline.fs:111`), so an
  unclosed paren raises no reader error at all and surfaces later as a confusing type error.
  Real bug, good next task, separate task. Note it in your final report; do not fix it here.

## Final report

- Error counts before and after, on three deliberately-broken files.
- `./run_tests.py` summary, naming any test whose output changed and why.
- Proof `--check` invokes no Roslyn: what you checked, what you observed.
- Anything in Phase 4 you could not suppress cleanly.

