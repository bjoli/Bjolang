# Documentation: where things stand, and what comes next

## Done (committed)

- `8700b45` Raw strings `"""..."""` in the compiler's lexer and in bjodat's
  reader (`lib/text/bjodat-core.bjo`), bjomode.el, and textmate-bjolang
  (`6bb44bc`, pushed). Rules in `Docs/Syntax.org`, "Raw strings".
- `41e8ae3` `(:doc ...)`. **Not pushed yet.** Specification:
  `Docs/Documentation.org`. Implementation:
  - `Docs.fs`: `parseEntry` (reads a form), `kindsOf` (what each name of the
    module is, from its parsed decls), `checkEntry`/`check` (every rule, plus
    the undocumented-export warnings), `publish` (the `BjolangDocs` text),
    `published` (mutable, read by Codegen).
  - `Pipeline.fs`: `expandIncludes` splices `(:doc #:file ...)` so the
    `.bjodoc` is a source; `load` takes the doc forms out of every source
    module after stamping and keeps the main file's in `mainDocs`;
    `loadModuleGraph` returns them; `runFullFrontendPipeline` resets
    `Docs.published` after loading, then calls `Docs.check` and `Docs.publish`
    after `Exhaustiveness.run`, before the type-check gate.
  - `Codegen.fs`: writes `[assembly: AssemblyMetadata("BjolangDocs", ...)]`.
  - `Repl.fs`: `shapeOf` treats a doc form as declaring nothing; a doc-only
    entry is refused with an explanation.
  - Tests: `TestFiles/264_docs.bjo` (+ `.bjodoc`, reads its own published docs
    back), `errors/doc_*.bjo` (26), `warnings/doc_undocumented.bjo`,
    `warnings/doc_quiet_without_module_doc.bjo`, `repl/docs.in`.

## Decisions already made

- Clauses: summary, arg, rest, key, returns, raises, see, example, reference,
  field, case, tparam, form, literal. No `law` (postponed until something can
  check laws), no `deprecated` (until call sites can warn).
- Arg order enforced; returns required unless void; rest required; keywords
  optional; record fields required unless `#:opaque`; union cases optional but
  checked; kind inferred, `#:reader` for reader extensions (the user prefers
  "reader extension" over "hash macro").
- Warnings only in modules with `(:doc #:module ...)`.
- Prose for the long explanation lives in `(reference """...""")`; a manual
  page may add context-specific text around a reference entry, not instead of it.

## Next steps

1. ~~Push `41e8ae3`~~ — pushed.
2. ~~Reader~~ — done: `(janitor docs)` + `(janitor modules)`, runtime helper
   `BjoAssemblyMetadata.Read`, test `TestFiles/265_janitor_docs.bjo`.
   Original notes: **A reader for published docs, in Bjolang.** Load a module's assembly,
   read the `BjolangDocs` attribute, parse it with `(text bjodat-core)` into
   records (one per doc: subject, kind, signature, definition, clauses). It is
   what both consumers below need. Open questions: where it lives (a std module
   such as `(bjolang docs)`?), and how a module name like `(std random)` is
   resolved to its dll (the compiler's roots, or a runtime helper). Reading the
   attribute works from Bjolang: `264_docs.bjo` does it with
   `System.Reflection.Assembly.GetCustomAttributes` and
   `(:is System.Reflection.AssemblyMetadataAttribute m)`.
3. ~~samizdat commands~~ — done in samizdat `0deb343` (not pushed, no
   version bump): standard commands `@defmodule`, `@defdoc`, `@defdocs`,
   `@docref` in `(samizdat apidocs)`; `@anchor` turned out unnecessary (entries
   are level-4 headings). glasnost uses fetched samizdat@0.5.0, so the manual
   gets them after a samizdat release + glasnost update.
   Original notes: **The samizdat command for the manual** (BjoManual, part III):
   `@defmodule["std/random"]` renders a module's docs, `@defun["random-int"]{...}`
   one entry plus extra text, `@fn["random-int"]` an inline link that refuses
   unknown names. Needs ids on non-headings: add `@anchor` to samizdat first
   (it is on samizdat's "Not yet" list). Undecided: whether this lives beside
   the manual (glasnost's `make-site` takes `#:commands`, so a small
   `BjoManual/_tools/serve.bjo` could serve with them) or as a reusable
   `(samizdat bjolang)` package. Ask the user.
4. **REPL `:doc name`**, printing a name's published doc.
5. **Builtins from `Prelude.fs`** cannot be documented: they have no
   definition in a `.bjo` for a doc to be checked against. Proposal to discuss:
   allow docs for builtin names in `lib/std/prelude.bjo` (or a
   `prelude.bjodoc`), with the kind and arity taken from the builtin's scheme.
6. **Document the stdlib.** Pilot with `(std random)` (its export list has
   section comments that map to `@section`s in a `.bjodoc`), then move the
   reference parts of `Docs/std/*.org` into `.bjodoc` files. Adding
   `(:doc #:module ...)` turns on the warnings, which then list what is left.
7. **samizdat's copy of textmate-bjolang** should move to `6bb44bc` so raw
   strings are highlighted in the manual.
8. **Later: a compiler (or `bjo`) command that exports a module's docs to a
   file**, so that tools can read plain bjodat instead of the `.dll`. Decided:
   the docs stay in the assembly only for now; the export is a later problem.
   Until then the reader in step 2 reads the attribute, and should do it
   without loading the assembly into the process if other modules' dlls are
   read (System.Reflection.Metadata), since plain reflection loads it.

## Found along the way, not fixed

- Interop bug: `(import/extern (f (: System.Reflection.Assembly.GetCustomAttributes
  (-> Assembly bool (Array object)))))` fails at the use site with "these types
  do not match: object / object". Leaving the type off works.
- The REPL treats any lexer failure as an incomplete entry and keeps reading,
  so a raw-string error (bad margin) waits for more input instead of reporting.
- bjodat's writer writes every string escaped, never as `"""`; deliberate, see
  `Docs/std/bjodat.org`.

## Things worth knowing when picking this up

- An `SList`'s range ends at the end of the token *after* its closing bracket
  (`Pipeline.read` takes it from what follows). `Docs.formText` finds a form's
  real end by counting brackets in the file's tokens.
- Tokens: `...` lexes as `Spread`, not a symbol; `%a` as `QuotedSymbol "a"`;
  `:doc` and `#:doc` are the same `Keyword "doc"`; `(: x t)` starts with a
  `Colon` token, so it never clashes with `(:doc ...)`.
- `TypeDef.TypeArgs` hold `a`, a signature's `FType` holds `TName "'a"`, a
  trait's implementor var is `c` without `%`.
- Error tests: `TestFiles/errors/*.bjo` with `;; EXPECT-ERROR: substring`.
  Warnings: `TestFiles/warnings/*.bjo` with `EXPECT-WARNING` and
  `EXPECT-NO-WARNING`. REPL: `TestFiles/repl/x.in` + `x.expected`, recorded
  the way `run_tests.py` cleans output (prompts and blank lines stripped,
  run with `--repl`, not `--batch`).
- After changing the compiler: `./bjo/bjo rebjo`, `./build_std.sh`, then
  `./run_tests.py`; `python3 run_bjo_tests.py` for bjo itself. After changing
  the runtime, rebuild bjoweb and other packages: compiled modules bind to the
  runtime's awaiter types.
- CLAUDE.md: comments say what the code does and why, succinctly, without
  referring to conversations; prompts and plan files are not committed.
