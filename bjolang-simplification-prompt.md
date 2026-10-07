# Bjolang compiler: reorganise Parser.fs and Inference.fs

You are working in the Bjolang repository, an F# compiler (~40k lines) for a Scheme-inspired, statically typed language that compiles to C#. The goal of this task is to make the compiler easier to read and change **without changing what it does**. Three files hold half the compiler: `Parser.fs` (~6300 lines), `Inference.fs` (~7900 lines) and `Codegen.fs` (~5500 lines). Only the first two are in scope.

## Ground rules

1. **No behaviour change.** Same diagnostics (text and positions), same generated C#, same public function names where anything outside the file uses them. This is a move-and-rename refactor, not a rewrite.
2. **Every step builds and passes the test suite.** Find how the tests are run (look for a `TestFiles/` directory, scripts, or README instructions) before you start. Run them after every phase, not just at the end.
3. **Extra regression check:** before touching anything, compile three or four representative `.bjo` programs (the prelude and a couple of modules that use `loop`, traits with associated types, and `.NET` interop) and keep the emitted C#. After each phase, re-emit and diff. The output must be identical, or differ only in gensym suffixes (`x__37` → `x__41`). Anything else is a bug you introduced.
4. **Doc comments move with the code they describe.** Many functions carry long `///` comments explaining *why*; never drop or shorten them.
5. **Small commits, one per phase (or smaller).** Each commit message says what moved where.
6. **F# compiles files in order and does not allow mutual recursion across files.** New files must be added to `Bjolang.fsproj` in dependency order. When a function must be moved out of a `let rec … and …` group, break the knot by passing the callback as a parameter — never by copying code.
7. Prefer `internal` over `public` when a formerly `private` function has to cross a file boundary.
8. If something cannot be moved cleanly, leave it where it is and say so in the final report. Do not force it.

## The knot-breaking technique (use this, it already exists in the code base)

`Parser.desugarSyntaxQuote` takes `parseExprFn: SExpr -> Expr` as a parameter instead of calling `parseExpr` directly. Generalise that pattern with a record:

```fsharp
type ParseFns =
    { Expr: SExpr -> Expr
      Pattern: SExpr -> Pattern
      Body: SExpr list -> Range -> Expr }
```

A desugarer in an earlier file takes `ParseFns`; `Parser.fs` builds the record once inside the `let rec parseExpr` group and passes it. The same idea applies in `Inference.fs` with `checkDecl` passed as a parameter. Do not introduce new `let mutable` forward-reference hooks; the existing ones (`expandHook`, `patternExpandHook`, `hashExpandHook`, `isMacroName`) stay as they are.

---

## Phase 1 — split Parser.fs (pure moves)

Target layout, in compile order. Names are the current definitions; move each with its doc comment and any private helpers only it uses.

**`Ast.fs`** (`module Bjolang.Ast`) — data only, plus pure functions over it:
`SExpr`, `getRange`, `Colour`, `SpawnKind`, `FType`, `UnionCase`, `RecordField`, `TypeDefKind`, `TypeDef`, `Pattern`, `Expr`, `DefunArg`, `mandatoryNames`, `allArgNames`, `ImportPath`, `ImportModifier`, `ImportSpec`, `AliasKind`, `ImportAlias`, `plainImport`, `ExternImportSpec`, `ClassImportSpec`, `MemberConstraint`, `Decl`, `declRange`, `Expansion`, `exprRange`, `exprChildren`, `freeNamesWith`, `freeNames`, `patternBinders`, `mapDeclExprs`, `boundNames`.

**`Hygiene.fs`** (`module Bjolang.Hygiene`) — the macro hooks and renaming:
`expandHook`, `patternExpandHook`, `hashExpandHook`, `isMacroName`, `introducedNames`, `noteIntroduced`, `snapshotIntroduced`, `restoreIntroduced`, `headName`, `stripHeadMark`, `(|StrippedSymbol|_|)`, `stripMethodName`, `stripTypeMark`, `renameWith`, `renamePattern`, `checkEscapeUses`, `EscapeBarrier`, `simultaneous`, `isRenamable`.

**`TypeSyntax.fs`** (`module Bjolang.TypeSyntax`) — everything that parses a type or a type definition:
`parseType`, `parseArrowType`, `parseDynType`, `parseTypeDef`, `parseUnionCase`, `parseRecordField`, `deriveEqForUnion`, `parseDerive`, `dHashSeed` and its neighbours.

**`LoopDesugar.fs`** (`module Bjolang.LoopDesugar`) — the `loop` / `seql` / `comprehension` sub-language:
`LoopClause`, `AccSlot`, `loopClauseRange`, `parseLoopClause`, `bindLoopPattern`, `splitCollector`, `desugarSeqLoop`, `desugarComprehension`, `expandUntilCancelled`, `desugarLoop`, `isLoopForm`. These are currently inside the `let rec parseExpr` group; give each entry point a `ParseFns` parameter.

**`Parser.fs`** (stays `module Bjolang.Parser`) — expressions, patterns, bodies, operators, quasiquote:
`parseExpr`, `parsePattern`, `parseBody`, `parseDefunArgs`, `desugarOperator`, `operatorArity`, `foldingOps`, `chainingOps`, `threadStep`, `desugarSyntaxQuote`, `desugarQuotedList`, and the `ParseFns` record it hands to the desugarers.

**`DeclParser.fs`** (`module Bjolang.DeclParser`) — declarations and imports:
`tryParseDecl`, `tryParseDeclGroup`, `parseDeclForms`, `parseDefunDecl`, `parseImportForm`, `parseForeignImportClause`, `ForeignImportOptions`, `flattenBegins`.

After moving, fix every `open Bjolang.Parser` and every qualified `Parser.X` in: `AlphaRename.fs`, `Codegen.fs`, `Exports.fs`, `Inference.fs`, `LetRecify.fs`, `Macro.fs`, `Normalize.fs`, `Pipeline.fs`, `Repl.fs`, `Session.fs`, `TypedAST.fs`, `Diagnostics.fs` (if it uses the AST). `Pipeline.fs` sets the hooks and `Session.fs` snapshots `introducedNames`; both now point at `Hygiene`.

Check: the sum of the new files should be within a few lines of the old `Parser.fs`. If it grew by more than ~50 lines, you duplicated something.

## Phase 2 — split Inference.fs (pure moves)

The file already has two independent recursive groups: `infer … and …` (roughly lines 2330–4686) and `checkDecl … and …` (roughly 4864–7759). Nothing in the first calls the second, so the cut between them is free. Target layout, in compile order:

**`TypeEnv.fs`** — `originalName`, `hiddenMemberNote`, `collectTraitConstraints`, `followMeta`, `metaIdsOf`, `unshadow`, `withSeqElement`, the fresh-meta and generalisation helpers, and `checkPattern`.

**`Annotations.fs`** — `typeNameMap`, `resolveTypeAnnotation`, `hmToTpl`, `resolveTemplate`, `instantiateTemplateFresh`, `qualifyFTypeNames`.

**`Traits.fs`** — `tryResolveWanted`, `solveWanteds`, `solvePinEquations`, `implTargetOf`, `registerDynImpl`, `checkAssocBindings`, `traitCallType`, `instantiateRecord`, `recordTypeOfField`, `isSyntacticValue`, `usesColour`, `yieldPointsOfDecl`, `demandedEffect`.

**`ForeignTyping.fs`** — everything about `.NET` members that does **not** call `infer`: `checkExceptionTypes`, `classInfoOfSpec`, `receiverClrType`, `unifyForeignArgs`, `reconcileForeignArgs`, `metadataOf`, `instantiateGenericExtern`, `resolveTokenThreadedExtern`, `resolveAsyncExtern`.

**`InferExpr.fs`** — `joinLiteralElement`, `localFunShape`, `LocalFunShape`, and the whole `infer` group (`infer`, `resolveAliasedHead`, `inferNode`, `inferLocalFunBody`, `inferChecked`, `inferLambda`, `inferAndMaybeInject`).

**`CheckDecl.fs`** — `registerTypeDefs` and the whole `checkDecl` group (`checkDecl`, `checkDeclNode`, `checkDeclGroup`).

**`Inference.fs`** — becomes the thin entry point: `checkModuleValuesAreConcrete`, `warnAboutShadowedMethods`, `returnOnlyGenerics`, `checkAddendum`, `checkProgram`.

Fix `open`s and qualified `Inference.X` uses in `Codegen.fs`, `Exports.fs`, `Monomorphise.fs`, `Naming.fs`, `Pipeline.fs`, `Session.fs`, `TraitInline.fs`, `TypedAST.fs`. Keep `Inference.checkProgram` and `Inference.checkAddendum` as the names callers use, re-exported from the entry file if necessary.

## Phase 3 — name the steps (code moves within a file; small factoring)

Do these one at a time, with a test run between each.

**3a. Turn large match arms into named functions.** Every arm over ~50 lines in `inferNode` and `checkDeclNode` becomes an `and private` function in the same recursive group, called from a one-line arm. At minimum:

- `inferNode`: `inferIdent`, `inferEscapeCall`, `inferRecordConstruct`, `inferTraitMethodCall`, `inferDotField`, `inferDotMethod`, `inferClassConstruct`, `inferExternCall`, `inferExternValue`, `inferApply`, `inferGeneralApp`, `inferLet`, `inferLetRec`, `inferLetMutable`, `inferMatch`, `inferRecordUpdate`, `inferRecordSet`, `inferDynPack`, `inferTaskEvent`, `inferBindElse`.
- `checkDeclNode`: `checkDef`, `checkDefun`, `checkDefDouble`, `checkModule`, `checkAlias`, `checkReExport`, `checkImportExtern`, `checkExtern`, `checkTrait`, `checkImpl`, `checkImplExtern`. Inside `checkImpl`, the ~300-line `typedMethods` binding becomes its own function `checkImplMethod`.

Same code, just moved; the `when` guards stay on the arms.

**3b. Collapse `EList` / `EVec` / `EArray`.** They are three copies of one function differing only in the type constructor name, the literal name passed to `joinLiteralElement`, and the `TListMake`/`TVecMake`/`TArrayMake` node. One helper `inferCollection ctor literalName mkNode`.

**3c. Share the extern prologue.** The six interop arms (extern-as-value, extern call, `.Method`, `.-field`, `ClassName.`, `task->event`) each re-derive `info`, `where`, `clrType`, `receiverType` and re-split the receiver off the argument list. Extract one function in `ForeignTyping.fs` that does the resolution given already-inferred argument types, and have the `inferExtern*` functions from 3a call it. Keep the per-arm differences (async, token-threaded, generic type args) as parameters or a small record, not as copies. Expect these ~800 lines to roughly halve. If a shared version would change which overload is picked in any case, stop and report instead of guessing.

**3d. Name the signature triple.** `Map<string, HMType * FType option * (string * string) list>` appears in at least five signatures. Introduce

```fsharp
type Signature = { Type: HMType; Written: FType option; Constraints: (string * string) list }
type Sigs = Map<string, Signature>
```

and replace the tuple pattern-matches with field access.

**3e. Move the source-to-source pre-passes out of `checkDeclGroup`.** `expandPolymorphicDefuns` and `expandReachingDefuns` rewrite `Decl list` before any checking happens. Move them to a new `ColourTwins.fs` (or beside `LetRecify.fs` / `Normalize.fs`), called from `checkDeclGroup` at the same point. If `expandReachingDefuns` turns out to need inference results, leave it and report.

## Out of scope — do not do these

- Do **not** move any special form (`with-open`, `parameterize`, `case`, `when`, `->` …) out of the parser into a macro. That is a design change with bootstrap consequences and needs a separate decision.
- Do **not** change how `loop` desugars (flat `letrec` group) or how `LoopLowering` / `Codegen` emit loops.
- Do **not** touch `Codegen.fs`, `TypedAST.fs` or the C# runtime except to fix `open`s.
- Do **not** rewrite error messages, even to fix grammar.
- Do **not** reformat files you are not otherwise changing.

## Final report

When done, give:

1. Line counts per file before and after, and the total (should be within ±100 of the original).
2. The list of new files in `.fsproj` order.
3. Everything you could not move or factor, with the reason.
4. The result of the emitted-C# diff for the sample programs.
5. Any place where you were unsure the behaviour was preserved, so it can be reviewed by hand.
