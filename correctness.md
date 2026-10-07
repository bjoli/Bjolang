# Correctness audit of the Bjolang repository

This report covers the code in the repository itself: the compiler (`*.fs`), the runtime (`BjolangRuntime/*.cs`, `BjolangRuntime/Cml`), the libraries (`lib/std`, `lib/text`, `lib/janitor`) and the `bjo` tool. The submodules (BjoRx, BjoString, Map, OrderedMap, OrderedSet, RrbList, SchemeList, Set, bjolang_rand) are not covered.

The audit covers correctness only: wrong results, unsound programs accepted, valid programs rejected, invalid C# emitted, crashes, hangs, races, leaks and documentation that contradicts the code. Style and performance are not covered.

Each finding has a severity, a status, a location, a description and, where possible, a complete program that reproduces it.

- **Status.** "Confirmed" means the repro was compiled and run against the current `bin/compiler` and a copy of `lib`, and the observed output is quoted. "Suspected" means the finding comes from reading the code only.
- **Severity.**
  - High: a silent wrong result, an unsound acceptance, data loss, or a hang or crash on reasonable code.
  - Medium: a crash or internal compiler error on less usual code, a wrong diagnostic, a leak, or a doc mismatch.
  - Low: edge cases.
- **Repro programs.** They were run in throwaway sandboxes under `/tmp/correctness/<area>/`, which also hold the extra files some findings mention (C# stress harnesses, small servers). Those directories are not part of the repository and will not survive a reboot.

The repository itself was not modified, apart from adding this file.

## Fixed since the audit

| Finding | Commit | Regression test |
|---|---|---|
| Event id 0 not reserved: a bare `with-nack`, `spawn-evt` or `task->event` is never nacked on cancel (concurrency #3) | `d798490` | `TestFiles/293_root_event_nack.bjo` |
| `(std run)` pipeline hangs when a stage stops reading early; `from-file` leaks its file (stdlib) | `10172b5`, `e678306` | `TestFiles/294_run_early_exit.bjo` |
| LetRecify skips `impl` methods, trait defaults and `defbjouble` (front end) | `073104c` | `TestFiles/295_local_def_order_in_methods.bjo` |
| Top-level `(def (: x T) e)` drops its type (types) | `54a98da` | `TestFiles/296_annotated_top_level_def.bjo`, `TestFiles/errors/annotated_def_wrong_type.bjo` |
| `take` pulls n+1 elements; negative n answers everything (prelude #1, prelude "take with a negative count", lowering) | `311b3e0` | `TestFiles/297_take_pulls_n.bjo` |
| bjodat `int` fields wrap out-of-range integers (text) | `4d971f5` | `TestFiles/298_bjodat_int_range.bjo` |
| A body-local `def` that reads an outer name silently sees a later sibling (front end); body definitions now follow `letrec*` | `1092f64` | `TestFiles/299_letrec_star_order.bjo`, `TestFiles/errors/def_*later_def*.bjo` |
| (introduced by `073104c`) `list-sort` and similar no longer specialised | `7085b00` | `TestFiles/codegen/monomorphise_local_annotations.bjo` |
| Seq fusion drops or adds producer evaluations around a `:with`/`:while`/`:until` end test (lowering) | `1cd7bdb` | `TestFiles/300_fusion_end_tests.bjo` |
| (found while fixing the next one) A nested inlined loop whose result is the outer loop's result left only the inner `while`: the outer loop hung | `79d2aa6` | `TestFiles/301_nested_loop_exit.bjo` |
| A named-let/self tail call from inside a nested loop is a real call; a million iterations overflow the stack (codegen #1) | `256f8bd` | `TestFiles/302_nested_loop_tail_calls.bjo` |
| Calling the result of a call is never treated as a yield point (`callSuspends` does not prune) (lowering) | `c39d05a` | `TestFiles/303_suspending_call_result.bjo`, `TestFiles/errors/yield_point_call_result_in_defun.bjo` |
| Suspending copies (`__bjo` twins) do not loop on self tail calls (lowering) | `157d090` | `TestFiles/304_suspending_copy_loops.bjo` |
| `->` duplicates the threaded expression at every `&`; a nested `->`'s `&` is taken by the outer one (front end) | `18c8728`, `ff781b6` | `TestFiles/305_thread_once.bjo` |
| Number literal text passed to C# unvalidated (`3-1`); also `1.`/`1.e3` (codegen #16) and `#\xD800` (front end) | `c556c6f` | `TestFiles/errors/number_*.bjo`, `TestFiles/errors/char_surrogate.bjo` |
| A local function's keyword defaults are never visited: non-exhaustive match at run time, trait call ICE (types) | `43f9982` | `TestFiles/306_local_keyword_defaults.bjo`, `TestFiles/errors/local_keyword_default_not_exhaustive.bjo` |
| Imports in an `(include …)`d file ignored by staleness and `--build-graph` (driver) | `5b9196e` | staleness section of `run_tests.py` |
| REPL: an `(import ...)` sharing a line with other forms is replayed with them | `86384f5` | `TestFiles/repl/entry_text.in` |
| REPL: an expression entry followed by a `;` comment is a syntax error | `d8d6344` | `TestFiles/repl/entry_text.in` |
| REPL: `absolutizeImports` rewrites every equal string literal, not only the import path | `bca306b` | `TestFiles/repl/entry_text.in` |
| REPL: two pending signatures for one name make it impossible to define | `eba7d5f` | `TestFiles/repl/entry_text.in` |
| REPL: a module edited and rebuilt during a session is still used in its old form, or its names become unbound | `d5eb1d8` | staleness section of `run_tests.py` |

Accepted as is: "Seq fusion moves a yield point out of a `seql`" (lowering). A fused program produces valid C# and runs. A suspending call in a `seq` body is defined as illegal, so a program that relies on it compiling is not expected to work.

To run a repro:

```sh
mkdir -p /tmp/r && cp -a lib /tmp/r/lib
cd /tmp/r && BJOLANG_LIB=/tmp/r/lib dotnet <repo>/bin/compiler/Bjolang.dll prog.bjo && dotnet prog.exe
```

## Counts

| Area | Files | Findings | High |
|---|---|---|---|
| Front end | Lexer, Parser, DeclParser, Macro, Hygiene, Normalize, LetRecify, … | 20 | 4 |
| Type system | InferExpr, Unification, TypeEnv, Traits, CheckDecl, Exhaustiveness, … | 17 | 5 |
| Colours, effects, lowering | ColourCheck, ColourTwins, EffectGraph, LoopDesugar, LoopLowering, SeqFusion, Monomorphise, … | 16 | 6 |
| Codegen and .NET interop | Codegen, Naming, DotNetInterop, ForeignTyping, CSharpEmit | 16 | 2 |
| Driver, modules, REPL, docs | Pipeline, Build, ModuleMetadata, Exports, Repl, Docs, … | 16 | 4 |
| Runtime (non-concurrency) | BjoPort, BjoBytePort, BjoNet, BjoMutable, BjolangRuntime, BjoProcess, … | 7 | 1 |
| Concurrency runtime | Concurrency.cs, Scope.cs, Effects.cs, Cml/* | 9 | 3 |
| Prelude and core libraries | prelude, eq, maths, monad, clr-ord | 12 | 1 |
| Rest of `lib/std` | run, http, fmt, datetime, simpletest, stopwatch, ports, … | 11 | 1 |
| `lib/text`, `lib/janitor`, `bjo` | json, json-codec, xml, bjodat, bjo tool | 23 | 1 |

## The findings to fix first

These are the High findings, in rough order of how likely they are to hit ordinary code. Each one is described in full in its area's section below.

1. **Channel rendezvous lost (concurrency #1).** Two fibers doing `(choose (send a) (recv b))` and `(choose (send b) (recv a))` both park forever. A compiled 2-fiber program hung 3 of 3 times when re-checked for this report.
2. **`take` pulls n+1 elements (prelude #1, also noted under lowering).** On a `port->seq`, the line after the taken ones is consumed and lost. `(take 0 s)` forces one element, and `map` callbacks run one extra time. Re-checked: `rest: []` instead of `rest: [body]`.
3. **`->` duplicates the threaded expression at every `&` (front end).** `(-> (noisy 3) (+ & &))` runs `noisy` twice. Re-checked.
4. **Body-local `def`s in `impl` methods, trait defaults and `defbjouble` are not reordered by LetRecify (front end).** A `def` that reads a later sibling gets the default value: `1` instead of `42`. Re-checked.
5. **The type annotation on a top-level `(def (: x int) "hello")` is ignored (types).** The program compiles and prints `hello`. Re-checked.
6. **A self tail call recomputes omitted keyword defaults from the previous iteration's parameters (lowering).** `(defun (f n #:k n) … (f (- n 1)))` answers `1` instead of `0`. Re-checked.
7. **Calling a local binding named `list` (or `not`, `case`, a macro name, …) ignores the binding (front end and types).** The parser builds the special form instead. Re-checked: `(1 2)` instead of `(2 1)`.
8. **Named-let and self tail calls made from inside a nested loop are real calls (codegen #1).** A million iterations overflow the stack, although the docs promise constant stack. Re-checked.
9. **Suspending twins (`__bjo` copies) of tail-recursive functions do not loop (lowering).** They overflow the stack on deep input inside a bjoroutine.
10. **`with-return` escape inside a non-tail-recursive named `let` returns from the local function, not from the block (front end).**
11. **The `:final` loop clause runs the clauses before it for one extra element (lowering).**
12. **The `(lp)` jump in a named `loop` takes slot values from user bindings that shadow the slot names (lowering).**
13. **A local function that shadows a top-level constrained function is replaced by the top-level one (Monomorphise and Lowering).**
14. **`callSuspends` does not prune the callee type (lowering).** `((make 1) 1)` where the result is a `-bjo->` function is never awaited, and is accepted in sync code.
15. **A forward reference to a top-level `def` that shadows an imported name reads the uninitialised local field (driver).** The program prints `1`, which is neither the import's value nor the local one.
16. **Imports written inside an `(include …)`d file are invisible to staleness checks and `--build-graph` (driver).** Edits to such a dependency are silently not rebuilt.
17. **REPL problems (driver).**
    - A module rebuilt during a session is still run in its old form.
    - An `(import …)` sharing a line with other forms replays those forms into every later entry.
18. **`wrap`'s function runs on the committing thread (concurrency #2).** It sees that thread's `parameterize` and handlers, and if it throws, the exception goes to the partner fiber.
19. **Event id 0 is not reserved (concurrency #3).** A bare `(sync (spawn-evt …))` hangs its scope when cancelled, and a bare `(sync (task->event …))` never cancels the .NET call. Starting the id counter at 1 should fix it.
20. **A cancelled `read-line` / `read-all` on a text port throws away text already read (runtime).** The common pattern of a `with-deadline` read followed by a retry returns a corrupted line.
21. **`(std run)` pipelines hang when the downstream stage exits early (stdlib).** `yes | head -n 1` never returns.
22. **bjodat `int` fields silently wrap out-of-range integers (text).** `:n 4294967297` reads as `1`. This also affects `bjo`'s manifests.
23. **Unsound acceptances that only Roslyn catches (types).**
    - An impl with fixed type arguments is chosen through a dictionary without checking those arguments.
    - A local function's own type variables are not scoped.
    - A local function's keyword defaults are never visited by the exhaustiveness check, by trait resolution or by lowering.
24. **Nested .NET types are mistyped (codegen #2).** `(.-Keys d)` is typed as the dictionary itself.

## Findings reported by more than one area

The same problem showed up in two places in these cases. Both write-ups are kept, because they describe different symptoms.

- `take` pulling n+1 elements: prelude #1, and the last item in the lowering section.
- `seconds` / `minutes` overflowing `int`: prelude, and concurrency #9.
- Shadowing `list` and other special-form names in call position: front end ("User bindings named like special forms"), and types ("Calling a local binding named `list`").
- Duplicate binders in patterns and parameter lists: front end ("Duplicate binders are not rejected"), and types ("Non-linear patterns").
- Unsuffixed integer literals that overflow `int` are not range-checked when defaulted: front end, and types ("A defaulted numeric literal is not range-checked").
- Malformed number literals reaching C# verbatim: front end (`3-1`, `5abc`), and codegen #16 (`1.`, `1.e3`). bjodat has its own form of the same problem (text: "a number is not checked for a delimiter after it").

## A side observation about the runtime that programs load

The concurrency audit noticed that compiled programs probe `BjolangRuntime/bin/Release/net10.0/BjolangRuntime.dll` first, not the ReadyToRun copy in `bin/compiler`. That is the first entry of `BjolangProbeDirs`. The two were built from the same commit at the time of the audit, so this is not a bug in itself. But a program can pick up a different runtime build than the compiler was built with if only one of the two is rebuilt. It also makes the JIT-sensitive race in concurrency #1 deterministic.

---

## Front-end correctness findings (Lexer / Parser / DeclParser / Ast / Macro / Hygiene / AlphaRename / Gensym / Normalize / LetRecify / Simplify / Annotations)

All repros were run with
`cd /tmp/correctness/frontend && BJOLANG_LIB=$PWD/lib dotnet /home/linis/Programmering/Bjolang/bin/compiler/Bjolang.dll X.bjo && dotnet X.exe`.
Unless a finding says otherwise, nothing here is listed in Todo.org.

---

### `with-return` escape inside a non-tail-recursive named `let` returns from the local function, not from the block
- Severity: High
- Status: Confirmed (repro run)
- Location: Hygiene.fs:226-246 (`checkEscapeUses`, `ELetRec` / `isLoopEntry`)
- Description: `checkEscapeUses` decides that a letrec is a loop only from its shape (`ELetRec(bindings, EApp(EIdent entry, ...))`), and then walks the member bodies with no lambda barrier. A named `let` whose recursion is not in tail position has that same shape, but `LoopLowering` cannot turn it into a `while`. It stays a real local function, so the escape is emitted as a `return` from that local function. The escape is accepted and silently does the wrong thing: it returns from the innermost recursive call instead of leaving the `with-return`.
- Repro:
```
(import (std prelude))
(: f (-> int int))
(defun (f n)
  (with-return ret
    (let go ((i 0))
      (if (< i n)
          (+ 1 (go (+ i 1)))
          (ret 100)))))
(defun (main args)
  (println (->str (f 3)))
  0)
```
- Observed: `103` (the `ret` returned 100 from the innermost `go`, then 1+1+1 was added). Expected: `100`, or a compile-time refusal ("`ret` cannot leave the lambda here") like the one a `fun` gets.

### LetRecify is not applied to `impl` methods, trait default methods or `defbjouble` bodies, so a local `def` reads a later one before it is initialised
- Severity: High
- Status: Confirmed (repro run)
- Location: LetRecify.fs:291-305 (`letrecifyDecl`, `| _ -> decl`)
- Description: `parseBody` turns every run of local `def`s into one `ELetRec` group and relies on LetRecify to split it into SCCs in dependency order. `letrecifyDecl` only handles `DDef`/`DDefTuple`/`DDefPattern`/`DDefMutable`/`DDefun`/`DModule`. `DImpl` methods, `DTrait` defaults and `DDefDouble` fall into `| _ -> decl`, so their groups reach inference and codegen unsplit and in source order. A value that reads a later sibling then sees the default value of the variable. The same body in a top-level `defun` gives 42. (Normalize.fs, by contrast, does recurse into DImpl/DTrait/DDefDouble.)
- Repro:
```
(import (std prelude))
(def/trait (Speak %a)
  (: speak (-> %a int)))
(type (: Dog (Record (: n int))))
(impl (Speak Dog)
  (defun (speak d)
    (def a (+ b 1))
    (def b (.n d))
    a))
(defun (main args)
  (println (->str (speak (Dog (n 41)))))
  0)
```
- Observed: `1`. Expected: `42`, which is what the same body gives in a top-level `defun` (`lr2.bjo`).

### Number literal text is passed to C# unvalidated, so `3-1`, `1-2-3` and `0x1-0x2` compile as C# arithmetic
- Severity: High
- Status: Confirmed (repro run)
- Location: Lexer.fs:584-596 (number branch of `tokenize`: `readNumber` accepts letters, digits, `.` and `-`); TypedAST.fs:493-505 (`NumericLiteral.fits` returns `true` when `value` cannot parse the digits); `csharp` emits `digits` verbatim
- Description: A token that starts with a digit takes in every following letter, digit, `.` and `-`. The resulting `NumberLit` is never checked for being a well-formed number: `fits` answers `true` when the text does not parse, and `csharp` copies the text into C#. Malformed spellings that happen to be valid C# expressions compile silently, with C# operator precedence. Ones that are not valid C# (`5abc`, `1.2.3`, `0b102`) fail as C# errors in generated code instead of a Bjolang lexer error.
- Repro:
```
(import (std prelude))
(defun (main args)
  (println (->str (* 2 3-1)))
  (def x 1-2-3)
  (println (->str x))
  0)
```
- Observed: `5` and `-4`; `(println (->str 5abc))` gives "C# Compilation failed". Expected: a lexer/parser error such as "malformed number literal '3-1'".

### `->` duplicates the threaded expression at every `&`, so its side effects run more than once
- Severity: High
- Status: Confirmed (repro run)
- Location: Parser.fs:30-71 (`threadStep`, `replaceAmpersand` returns `prev` for every `&`)
- Description: `prev` is an `SExpr` that is copied into each `&` position, with no temporary. `(-> (f) (+ & &))` therefore evaluates `(f)` twice. MACROS.org's own `twice` example `#'(-> ,x (+ & &))` has the same problem whenever the argument has an effect. Normalize's guarantee ("never duplicate work or lose an effect") is no help, because the duplication happens at parse time.
- Repro:
```
(import (std prelude))
(: noisy (-> int int))
(defun (noisy x) (println "eval!") x)
(defun (main args)
  (println (->str (-> (noisy 3) (+ & &))))
  0)
```
- Observed: `eval!`, `eval!`, `6`. Expected: `eval!` once, then `6`.

### Gensym names can collide with user identifiers spelled `name__N`, which captures user variables
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Gensym.fs:27-29 (`fresh`); used by Hygiene.fs `simultaneous` (line ~690), Macro.fs `toSExpr`, Normalize, etc.
- Description: `fresh "x"` produces `x__N`, which is also a legal user identifier (Hygiene.fs itself says "`x__1` is a name a program may legitimately define"). When `let`'s simultaneous-binding rename picks a name the user already uses, the renamed binder shadows the user's binding of that name inside the body. Macro-introduced names (`tmp__N`) can be captured the same way.
- Repro:
```
(import (std prelude))
(defun (main args)
  (def x__1 100)
  (def x 7)
  (let ((x 1) (y x))
    (println (->str x__1)))
  0)
```
- Observed: `1`. Expected: `100`.

### A run of body-local `def`s is one letrec group, so a `def` that reads an outer name silently sees a later sibling of the same name
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Parser.fs:1686-1716 (`parseBody.groupDefs`); LetRecify.fs (reorders by dependency)
- Description: `groupDefs` only breaks a run when a value reads its *own* name, or when a name repeats. A value that reads a name defined *later* in the same run binds to that later definition instead of the enclosing one, and LetRecify then reorders the evaluation to match. A reader expects sequential `def` semantics (the earlier text refers to the outer `y`), and the ordering of effects in the group changes too. This is not documented in Docs/Syntax.org.
- Repro:
```
(import (std prelude))
(defun (main args)
  (def y 10)
  (println "sep")
  (def a y)
  (def y 20)
  (println (->str a))
  0)
```
- Observed: `20`. Expected: `10` (sequential reading), or an error about the forward or ambiguous reference.

### Nested `#(...)` placeholders leak into the outer shorthand lambda's arity
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Pipeline.fs:57-63 (`collectPositionalArgs`), used from Pipeline.fs:164-173 (`#(` reader case)
- Description: The inner `#(...)` has already been rewritten into `(fun (&1 &2) ...)` by the time the outer one is processed. `collectPositionalArgs` still recurses into it and collects the inner `&1`/`&2` (from both its parameter list and its body) as the outer lambda's parameters. The comment above `expandBareArg` says nested shorthands need no exception, but for arity they do.
- Repro:
```
(import (std prelude))
(defun (main args)
  (println (->str (list-map #(+ & (#(* &1 &2) 2 3)) '(1 2))))
  0)
```
- Observed: type error "The expected function takes 1 argument, the found one 2 arguments". Expected: `(7 8)`.

### Template references to non-exported functions (rule 3) are captured by call-site locals
- Severity: Medium (this follows from the documented rule 3, but breaks the hygiene MACROS.org promises)
- Status: Confirmed (repro run)
- Location: Macro.fs:383-387 (`resolveIntroduced`, rule-3 branch `Some(n, original)`)
- Description: A free template identifier that the macro module does not export (for example a prelude function used by a user's macro module) has its mark stripped back to the bare name. Any local of that name at the call site then captures it. Only trait methods get an `EResolved`. MACROS.org says hygiene means "a binder the template introduces cannot capture" and that call-site names cannot take over template references, but here an ordinary call-site `let` silently changes what the macro calls.
- Repro: `inc/m1.bjo`:
```
(import (std prelude))
(import (std syntax-match))
(def/macro (head-of form inject compare)
  (syntax-match form ((_ xs) #'(list-head ,xs))))
(def/macro (sa form inject compare)
  (syntax-match form ((_ a b) #'(string-append ,a ,b))))
```
  `t4.bjo`:
```
(import (std prelude))
(import "inc/m1.bjo")
(defun (main args)
  (def xs '(1 2 3))
  (let ((list-head (fun (l) 99)))
    (println (->str (head-of xs))))
  (let ((string-append (fun (a b) "captured")))
    (println (sa "a" "b")))
  0)
```
- Observed: `99` and `captured`. Expected: `1` and `ab`.

### The runaway-expansion guard rejects terminating recursive macros on more than 100 items
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Macro.fs:425-449 (`expandIn`, `expansions` keyed by `(macro, callSite)`), `maxDepth = 100`
- Description: Every node a transformer constructs inherits the call site's range, so each round of a macro that shrinks its input still counts against the same key. MACROS.org says "A recursion that takes something off each round never reaches it", but the documented `any-of` pattern fails at 101 arguments. For the same reason, any macro that writes more than 100 calls to another macro in one template (all at the call-site range) is also rejected, even with no recursion.
- Repro: `inc/m1.bjo` holds the MACROS.org `any-of` macro. Then:
```
(import (std prelude))
(import "inc/m1.bjo")
(defun (main args)
  (println (->str (any-of 0 0 0 ... 120 zeros ... 0 7)))
  0)
```
  (`t2.bjo` is generated with 120 `0`s followed by `7`.)
- Observed: "'any-of' has expanded 100 times at t2.bjo:4 ... does not terminate". Expected: `7`.

### Field names in a template's `record-set` / `record-set!` are renamed by hygiene, so the macro is rejected
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Macro.fs:286-307 (`toSExpr` renames every Template `SSym` not in `neverRenamed`); Parser.fs:997-1031 (`record-set`/`record-set!` take field names as plain strings, which `resolveIntroduced`/`freeNames` never see)
- Description: In `(record-set ,x (n ,v))` the field name `n` is a template symbol, so it becomes `n__K`. A field name is a string inside `ERecordUpdate`/`ERecordSet`, never an expression, so neither rule 1, 2 nor 3 strips the mark again, and inference sees the field `n__K`. (Record construction `(Dog (n ,v))` and `(.n ,x)` both work.)
- Repro: `inc/m2.bjo`:
```
(import (std prelude))
(import (std syntax-match))
(def/macro (set-n form inject compare)
  (syntax-match form ((_ x v) #'(record-set ,x (n ,v)))))
```
  `mf1.bjo`:
```
(import (std prelude))
(import "inc/m2.bjo")
(type (: Dog (Record (: n int))))
(defun (main args)
  (def d (Dog (n 41)))
  (println (->str (.n (set-n d 7))))
  0)
```
- Observed: "Type Error: Field 'n' does not belong to record '...Dog'". Expected: `7` (the same `record-set` written by hand works).

### Declaration-position macro: a `defun` parameter name blocks resolution of the same name in every other declaration of the splice
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Ast.fs:1100-1120 (`boundNames` adds `DDefun`/`DDefDouble` parameters to the group-wide bound set); DeclParser.fs:1068
- Description: For a top-level splice, `boundNames decls` is passed to `Resolve` as "already bound" for *all* declarations in the group. It includes every defun's parameters. The memo gives one fresh spelling per template name, so if one defun has a parameter `str` and another defun calls the prelude's `str`, the call keeps the fresh spelling `str__N`, which nothing binds.
- Repro: `inc/m3.bjo`:
```
(import (std prelude))
(import (std syntax-match))
(def/macro (def-pair form inject compare)
  (syntax-match form
    ((_ a b)
     #'(begin
         (: ,a (-> string int))
         (defun (,a str) (string-length str))
         (: ,b (-> string))
         (defun (,b) (str "x" "y"))))))
```
  `dp1.bjo`:
```
(import (std prelude))
(import "inc/m3.bjo")
(def-pair len xy)
(defun (main args)
  (println (->str (len "abc")))
  (println (xy))
  0)
```
- Observed: "Unbound variable: str at dp1.bjo:3". Expected: `3` and `xy`.

### User bindings named like special forms or imported macros (`list`, `not`, `get`, …) are silently ignored in call position
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Parser.fs:454-1003 (special-form dispatch on `headName sym` with no scope), Parser.fs:1351 (`"list" -> EList`), Parser.fs:1378 (macro fallback)
- Description: The parser dispatches on the head symbol before any scoping, so a local or parameter named `list`, `not`, `case`, `seq`, `try`, `match`, `yield`, or an imported macro's name, is accepted as a binding but never called. No warning is given. MACROS.org mentions that a macro cannot shadow a special form, but nothing says a *user binding* cannot. Parser.fs:1640 says this only about the six body heads.
- Repro:
```
(import (std prelude))
(defun (main args)
  (def list (fun (x) 42))
  (def not (fun (x) x))
  (println (->str (list 1)))
  (println (->str (not #t)))
  0)
```
- Observed: `(1)` and `False`. Expected: `42` and `True`, or an error that the name is reserved.

### Duplicate binders are not rejected: `fun`/`defun` parameters and match patterns
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Parser.fs:858-872 (`fun` argument list), Parser.fs:1598 (`parseDefunArgs`), Parser.fs:1388ff (`parsePattern`: only `and` checks for duplicates)
- Description: `(fun (x x) x)` is accepted and silently picks the last argument. A local `(defun (h y y) y)` reaches C# and fails with CS0229. A pattern `(Tuple a a)` reaches C# and fails with CS0128. `syntax-match` and `(and ...)` patterns already refuse duplicate binders, so ordinary patterns and parameter lists are an inconsistency.
- Repro:
```
(import (std prelude))
(defun (main args)
  (println (->str ((fun (x x) x) 1 2)))
  (println (->str (match (Tuple 1 2) ((Tuple a a) a))))
  0)
```
- Observed: the first line alone prints `2`. With the match line the build ends in "C# Compilation failed": `error CS0128: A local variable or function named 'a__2' is already defined`. Expected: a Bjolang diagnostic "x is bound twice".

### Unsuffixed integer literals that overflow `int` are not range-checked when defaulted
- Severity: Medium
- Status: Confirmed (repro run)
- Location: TypeEnv.fs:174-181 (`numericLiteralType`: only spelled literals go through `checkLiteralFits`) and TypeEnv.fs:~129 (`settleLiterals` sets `int` without checking that the literal fits)
- Description: An unsuffixed literal becomes an open metavariable. When it settles at `int` (through `settleLiterals`, or flowing into `->str`), nothing checks that the literal fits, so the error comes back from Roslyn about generated code.
- Repro:
```
(import (std prelude))
(defun (main args)
  (println (->str 9999999999))
  0)
```
- Observed: "C# Compilation failed": `error CS1503: Argument 1: cannot convert from 'long' to 'int'`. The same happens for `(def x 9999999999) (+ x 1)` and for `-2147483649`. Expected: "Type Error: '9999999999' does not fit in a 'int'".

### `#\xD800` (a surrogate) passes the lexer and crashes at run time
- Severity: Low
- Status: Confirmed (repro run)
- Location: Lexer.fs:676-685 (hex char literal accepts `0 <= value <= 0x10FFFF`, although its error message says "not a Unicode scalar value")
- Description: The range check does not exclude 0xD800–0xDFFF. `BjoChar`'s constructor rejects those at run time.
- Repro:
```
(import (std prelude))
(defun (main args)
  (println (->str (char->int #\xD800)))
  0)
```
- Observed: `Unhandled exception. System.ArgumentOutOfRangeException: Invalid Unicode scalar value: 0xD800` at run time. Expected: a lexer error "Invalid character literal #\xD800 ... not a Unicode scalar value".

### A `;` comment inside a `${ }` hole is not skipped, so a `}` in the comment ends the hole
- Severity: Low
- Status: Confirmed (repro run)
- Location: Lexer.fs:760-808 (`readInterpolatedString`, the depth scanner skips strings and `#\c` but not comments)
- Description: Holes may span lines and contain any expression, including comments, but the brace scanner counts braces inside a comment.
- Repro:
```
(import (std prelude))
(defun (main args)
  (def x 5)
  (println #"a${x ; comment with }
  }b")
  0)
```
- Observed: prints `a5` and then `  }b` (the comment's `}` closed the hole and the rest became literal text). Expected: `a5b`.

### A nested `->`'s `&` is taken by the outer `->`
- Severity: Low
- Status: Confirmed (repro run)
- Location: Parser.fs:30-71 (`threadStep.replaceAmpersand` recurses into every sub-list, including a nested `(-> ...)`)
- Description: The outer `->` is expanded first and replaces every `&` inside its step, including those that belong to a nested `->`. The inner thread then has no `&` and inserts its value as the first argument instead.
- Repro:
```
(import (std prelude))
(defun (main args)
  (println (->str (-> 1 (list (-> 2 (+ & 10))))))
  0)
```
- Observed: `(13)` (it reads as `(list (+ 2 1 10))`). Expected: `(1 12)`, with the inner `&` being 2 and the outer value threaded first.

### Beta-reduction changes which programs are accepted (escapes and yields inside an immediately applied lambda)
- Severity: Low
- Status: Confirmed (repro run)
- Location: Normalize.fs:66-140 (`betaReduce`); compare Normalize.fs:25-28 ("Exactly the same set of programs typechecks before and after")
- Description: `((fun (y) (ret y)) 5)` inside `with-return` is accepted, because Normalize inlines the lambda before `checkEscapeUses` (in inference) sees the `EFun` barrier. The same lambda bound to a name and then called is refused with "`ret` cannot leave the lambda here". Whether a program compiles therefore depends on the optimisation, which the module header says must not happen. A `yield` inside an immediately applied lambda in a `seq` presumably behaves the same way; I did not run that case.
- Repro:
```
(import (std prelude))
(: f (-> int int))
(defun (f x)
  (with-return ret
    ((fun (y) (ret y)) 5)
    x))
(defun (main args)
  (println (->str (f 1)))
  0)
```
- Observed: compiles and prints `5`, while `(def h (fun (y) (ret y))) (h 5)` is refused. Expected: both treated the same way.

### `case` duplicate-datum check misses equal values with different spellings (`16` and `0x10`)
- Severity: Low
- Status: Confirmed (repro run)
- Location: Parser.fs:883-893 (`noteDatum`: `Int64.TryParse` fails on hex and binary, so the key becomes the raw spelling)
- Repro:
```
(import (std prelude))
(defun (main args)
  (def k 16)
  (println (case k ((16) "a") ((0x10) "b") (else "c")))
  0)
```
- Observed: "C# Compilation failed": `error CS8510: The pattern is unreachable`. Expected: Bjolang's own "Duplicate case datum" error.

### Unary `+`, `*`, `bitwise-and/ior/xor` return their operand without requiring a number
- Severity: Low
- Status: Confirmed (repro run)
- Location: Parser.fs:355-362 (`desugarOperator`, the `[ single ]` case returns `parseExprFn single`)
- Description: `(+ x)` is rewritten to `x` before type checking, so `(+ "s")` type-checks as a string. `(+)` and `(+ a b)` both require `Num`.
- Repro:
```
(import (std prelude))
(defun (main args)
  (println (+ "s"))
  0)
```
- Observed: prints `s`. Expected: a type error (no `Num` for string).

---

### Areas reviewed that looked correct (no finding)
- Hygiene `renameWith` and `freeNamesWith` scoping for let, letrec, match (view steps in the outer scope), def-match failure arms, and keyword parameters.
- `let` simultaneous binding (swap), and beta-reduction's simultaneous-argument capture. `nb1`: type preservation holds.
- Operator shadowing by locals (`+`, `=`, `<`, including the 3-operand `#fl`/`#ch` forms and operators used as values).
- Interpolation holes containing strings with `}` and `\$` escapes. Raw strings (single and multi-line). Type alias parameter substitution with swapped variables.
- Simplify (case-of-known-case): from reading, it keeps evaluation order (`wrapInOrder`) and only drops pure bindings. No issue found.
- REPL: the macro expansion counter does not accumulate across entries (105 `if-let` entries worked).


---

## Bjolang type-system correctness findings (area: types/checking)

All repros were run in /tmp/correctness/types with
`BJOLANG_LIB=/tmp/correctness/types/lib dotnet /home/linis/Programmering/Bjolang/bin/compiler/Bjolang.dll prog.bjo && dotnet prog.exe`.
None of these is listed in Todo.org.

---

### A non-exhaustive `match` inside a local function's keyword default is never checked, so it fails at run time; trait calls there cause an internal compiler error
- Severity: High
- Status: Confirmed (repro run)
- Location: TypeVisitor.fs:80-81 (`mapChildren`, the `TLet`/`TLetRec` cases); TypedAST.fs:1077 (`LocalFun.KeywordArgs`)
- Description: `TLet(name, isFun, args, value, body)` and `TLetRec` keep a local function's keyword defaults in `LocalFun.KeywordArgs : (string * HMType * TypedExpr) list`. `mapChildren` only maps `value` and `body`, so `children`, `mapExpr`, `foldExpr` and `mapDecl` never reach those default expressions. Every pass built on the visitor skips them: `Exhaustiveness.checkExpr`, `collectTraitConstraints`, `TraitInline` and dictionary `Lowering`. A top-level `defun`'s defaults are reached, because `mapDecl` maps `TDefun` kwArgs by hand. A local function's defaults are not.
- Repro 1 (non-exhaustive match accepted):
```
(import (std prelude))
(: pick (-> (Option int) int))
(defun (pick o)
  (defun (inner a #:k (match o ((Some v) v)))
    (+ a k))
  (inner 1))
(defun (main args)
  (println (->str (pick (Some 2))))
  (println (->str (pick None)))
  0)
```
- Observed: compiles. Prints `3`, then `Unhandled exception. System.Exception: Match failure at t33.bjo:4`.   Expected: `Pattern Error ... None reaches no clause`, which is what the same `match` gives when written in the body.
- Repro 2 (trait call in a local keyword default):
```
(import (std prelude))
(def/trait (Describe %a)
  (: describe (-> %a string)))
(impl (Describe int)
  (defun (describe x) "int"))
(: via (-> %a string) (where (Describe %a)))
(defun (via x)
  (defun (inner a #:k (describe x))
    (string-append a k))
  (inner "v:"))
(defun (main args)
  (println (via 5))
  0)
```
- Observed: `Codegen Error at t34.bjo:8: internal error: call to 'Describe.describe' was never resolved to an implementation`.   Expected: prints `v:int`.

---

### A generic function accepts an implementation whose fixed type arguments do not match (`Describe (List int)` is used for `(List string)`)
- Severity: High (the checker accepts an unsound program; Roslyn rejects the generated C#)
- Status: Confirmed (repro run)
- Location: Unification.fs `implFor` (approx. lines 150-185) and `leafConstraints`; used by Lowering.fs:240-262 (`buildEvidence`)
- Description: `implFor` finds an impl by `(trait, head ctor)` only. It then builds a substitution from the impl's `FixedPrefix`, and `List.choose` keeps only the `TVar` positions. Concrete prefix arguments (`int` in `(List int)`) are never compared with the actual arguments. A repeated variable (`(Pair %a %a)`) is not checked for consistency either: `Map.ofList` keeps the last binding. A direct call is fine, because `Traits.tryResolveWanted` unifies the prefix. A constraint discharged through a dictionary (a call to a `(where ...)` function) goes through `implFor`/`buildEvidence`, which selects the wrong impl class. The program type-checks and the C# fails.
- Repro:
```
(import (std prelude))
(def/trait (Describe %a)
  (: describe (-> %a string)))
(impl (Describe (List int))
  (defun (describe x) (->str (+ 1 (list-ref x 0)))))
(: via (-> %a string) (where (Describe %a)))
(defun (via x) (describe x))
(defun (main args)
  (println (via (list "hello")))
  0)
```
- Observed: "Warning: could not specialise 'via' ...", then Roslyn `error CS0411: The type arguments for method 'via<T_a>(Describe<T_a>, T_a)' cannot be inferred`.   Expected: a Bjolang type error: no implementation of `Describe` for `(List string)`.

---

### A local function annotated with a type variable is not a scope for that variable, so a nested local function quantifies it (unsound acceptance)
- Severity: High (the checker accepts ill-typed code; currently caught only by Roslyn)
- Status: Confirmed (repro run)
- Location: Unification.fs:743 (`scopedTypeVars`), :796-808 (`generalizeLocal`); CheckDecl.fs:892-920 (the only place `scopedTypeVars` is set)
- Description: `scopedTypeVars` is extended only for a top-level `defun`'s signature. If a body-local function annotates a parameter `(: y %b)`, `%b` is rigid inside that function. A local function nested inside it that mentions `%b` (here by closing over `y`) is generalized by `generalizeLocal`. `generalizeWith` quantifies every explicit `TVar` that is not in `scoped`, so the inner function becomes `∀b. ... -> b`. Each call then instantiates `%b` afresh, and the outer body can use a `%b` value at any type. Below, `(+ 1 (g 0))` treats `y : %b` as an `int` and is accepted.
- Repro:
```
(import (std prelude))
(defun (main args)
  (defun (f (: y %b))
    (defun (g z) y)
    (+ 1 (g 0)))
  (println (->str (f "hello")))
  0)
```
- Observed: type-checks. Roslyn: `error CS0029: Cannot implicitly convert type 'T_b [t4.bjo(2)]' to 'T_b [t4.bjo(3)]'` and `CS0411` for `g<T_t__1, T_b>`.   Expected: a type error, because `%b` is not `int`. Fix: add a local function's own annotation variables to `scopedTypeVars` while its body is checked.

---

### A sibling local binding can quantify a metavariable that `generalizeLocal` held back for another local function
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Unification.fs:796-808 (`generalizeLocal`, `resultOnly`); InferExpr.fs:2475-2494 (`inferLet`)
- Description: `generalizeLocal` holds back variables that appear only in a function's result (and interface-trait holes). A held variable is not demoted, so it keeps its inner level. The next sibling local function is inferred one level in and instantiates the first function's type, which contains the held metavariable itself. If the sibling mentions it in a parameter, the sibling's `generalizeLocal` quantifies it: it is above `currentLevel` and not "result only" for the sibling. `m.Value <- TVar 't` then also rewrites the first function's monomorphic type to a rigid variable that belongs to the sibling. Codegen emits C# that does not compile. Held metavariables should be demoted to the enclosing level.
- Repro:
```
(import (std prelude))
(defun (main args)
  (defun (mk) Nil)
  (defun (g x) (cons x (mk)))
  (println (->str (list-length (g 1))))
  (println (->str (list-length (g "s"))))
  0)
```
- Observed: type-checks. Roslyn: `error CS0411: The type arguments for method 'mk<T_t__1>()' cannot be inferred from the usage`.   Expected: either it compiles and prints `1` and `1`, or the checker rejects the second use. It should not emit invalid C#.

---

### A top-level `(def (: name T) e)` / `(def/mutable (: name T) e)` silently drops the annotation
- Severity: High (silent wrong typing: a value of the wrong type flows on)
- Status: Confirmed (repro run)
- Location: DeclParser.fs:531-532 and :576-577 (`tType` is matched but never used)
- Description: Docs/Syntax.org:178/188/1261 document `(def (: one Form) ...)` at top level as the way to give a value its type, for example to drive literal elaboration. The declaration parser matches the annotated form and builds `DDef(name, parseExpr expr, r)` with no `DSignature`, so the type is ignored. The body-local form in Parser.fs:1788 does keep it. Any value is accepted under any annotation, and elaboration of union literals does not happen at top level.
- Repro:
```
(import (std prelude))
(def (: x int) "hello")
(def/mutable (: y int) "s")
(defun (main args)
  (println x)
  (println y)
  0)
```
- Observed: compiles and prints `hello` and `s`.   Expected: a type error, since a string is not an `int`.

---

### Calling a local binding named `list` still builds a list: shadowing `list` is silently ignored in call position
- Severity: High (silent wrong result) — note: the cause is in Parser.fs, outside this area, but the effect shows up as wrong typing.
- Status: Confirmed (repro run)
- Location: Parser.fs:1342 (`| "list" -> EList(...)`)
- Description: `(list ...)` becomes `EList` at parse time, whatever `list` is bound to. A parameter, `let` or `match` binder called `list` that holds a function is never called. The call site silently builds a list from the arguments instead. `Unification.addBinding`'s doc comment discusses binding `list` locally as a supported case.
- Repro:
```
(import (std prelude))
(: rev2 (-> int int (List int)))
(defun (rev2 a b) (cons b (cons a Nil)))
(defun (main args)
  (match (Some rev2)
    ((Some list) (println (->str (list 1 2))))
    (None (println "n")))
  (let ((list rev2))
    (println (->str (list 1 2))))
  (defun (f list) (list 1 2))
  (println (->str (f rev2)))
  0)
```
- Observed: `(1 2)` three times.   Expected: `(2 1)` three times.

---

### Duplicate or overlapping `impl`s for the same trait and head are not detected; the later one silently replaces the earlier
- Severity: Medium
- Status: Confirmed (repro run)
- Location: CheckDecl.fs:2148ff (`checkImpl`; there is no duplicate/overlap check); the registry's `ImplTargets` map is keyed `(trait, ctor)`
- Description: `ImplTargets` is keyed by `(trait, head ctor)`, so two impls with the same head overwrite each other. That covers an exact duplicate and also `(List int)` beside `(List string)`. Neither is reported at the `impl`.
  (a) Exact duplicate: the checker accepts it and C# fails with CS0101/CS0111.
  (b) `(Describe (List int))` plus `(Describe (List string))`: the first impl disappears. `(describe (list 1))` then fails with a confusing `(List int)` vs `(List string)` mismatch at the call site.
- Repro (a):
```
(import (std prelude))
(def/trait (Describe %a)
  (: describe (-> %a string)))
(impl (Describe int)
  (defun (describe x) "first"))
(impl (Describe int)
  (defun (describe x) "second"))
(defun (main args)
  (println (describe 1))
  0)
```
- Observed: "Compilation succeeded", then Roslyn `CS0101: ... already contains a definition for 'Describe_System_Int32'`.   Expected: an error at the second `impl` (duplicate implementation).
- Repro (b): the same, but with `(impl (Describe (List int)) ...)` and `(impl (Describe (List string)) ...)`, calling `(describe (list 1))` and `(describe (list "a"))`.
- Observed: `Type error: these types do not match. (List int) (List string)` at the call.   Expected: either both impls work, or an "overlapping implementation" error at the impl.

---

### Associated types cannot be resolved for a tuple implementor
- Severity: Medium (valid program rejected)
- Status: Confirmed (repro run)
- Location: TypedAST.fs:1891-1936 (`TraitRegistry.ResolveAssociatedType`: `typeKey` handles only `TCon`); Unification.fs `prune` (the `TAssoc` case, which calls it for `TTuple` and then fails)
- Description: `prune` tries to resolve `TAssoc` when the implementor is `TCon`, `TTuple` or `TFun`. `ResolveAssociatedType` computes `typeKey` only for `TCon`, and `matchTypes` has no `TTuple` case, so a tuple implementor always gets `None`. The result is "Missing implementation", even though `impl ... (Tuple %a %b)` is accepted and dispatch for tuples is otherwise supported through `tupleCtor`.
- Repro:
```
(import (std prelude))
(def/trait (First %c)
  (type %out)
  (: first-of (-> %c %out)))
(impl (First (Tuple %a %b))
  (type %out %a)
  (defun (first-of t) (match t ((Tuple a b) a))))
(impl (First (List %a))
  (type %out %a)
  (defun (first-of t) (list-head t)))
(defun (main args)
  (println (->str (+ 1 (first-of (list 41)))))
  (println (string-append "a" (first-of (Tuple "b" "x"))))
  0)
```
- Observed: `Missing implementation of First for (Tuple string string)`.   Expected: prints `42` and `ab`.

---

### A constructor pattern accepts any capitalised function, not only constructors
- Severity: Medium (checker accepts; C# error; the exhaustiveness check is skipped)
- Status: Confirmed (repro run)
- Location: TypeEnv.fs:704-745 (`checkPattern`, the `PConstruct` case)
- Description: the case looks the name up in `env.Bindings` and accepts any binding whose instantiated type returns the scrutinee type. It never checks that the name is a union case. A function `(defun (Wrap x) (Some x))` is therefore accepted as the pattern `(Wrap y)`. Codegen emits a non-existent case class. Exhaustiveness hits `fieldsOf` → `Undecidable`, so the whole match goes unchecked.
- Repro:
```
(import (std prelude))
(: Wrap (-> int (Option int)))
(defun (Wrap x) (Some x))
(: f (-> (Option int) int))
(defun (f o) (match o ((Wrap y) y)))
(defun (main args) (println (->str (f None))) 0)
```
- Observed: "Compilation succeeded", then Roslyn `CS0426: The type name 'Wrap' does not exist in the type 'BjolangRuntime.Option<int>'`.   Expected: `Pattern Error: 'Wrap' is not a constructor`.

---

### Non-linear patterns (the same name bound twice) are accepted
- Severity: Medium
- Status: Confirmed (repro run)
- Location: TypeEnv.fs:730-745 (`PConstruct`) and :820-837 (`PTuple`), and the other compound cases. Binders are merged with `Map.add`, which silently overwrites. Only `and` patterns are checked, in the parser.
- Repro:
```
(import (std prelude))
(: f (-> (Tuple int int) int))
(defun (f t) (match t ((Tuple x x) x)))
(defun (main args) (println (->str (f (Tuple 1 2)))) 0)
```
- Observed: "Compilation succeeded", then Roslyn `CS0128: A local variable ... named 'x__2' is already defined`.   Expected: `Pattern Error: 'x' is bound twice` (or an equality pattern, if that were the intent).

---

### `type/derive (Eq)` on a union with a single case is rejected by the exhaustiveness checker
- Severity: Medium (valid program rejected)
- Status: Confirmed (repro run)
- Location: TypeSyntax.fs `deriveEqForUnion` (approx. lines 470-500): the inner match always ends with `PWildcard → #f`; Exhaustiveness.fs `checkMatch` (unreachable clause)
- Description: the derived `=` matches `b` against the same case and then `_`. With one case, `_` can never run, and the checker reports an error at the `type/derive` line.
- Repro:
```
(import (std prelude))
(type/derive (Eq) (: Box (Union (: B int))))
(defun (main args)
  (println (->str (= (B 1) (B 1))))
  0)
```
- Observed: `Pattern Error at t22.bjo:2: this clause can never run`.   Expected: prints `True`.

---

### Top-level tuple/destructuring `def` skips the value restriction, so valid programs are rejected
- Severity: Medium (valid program rejected; top-level unsoundness is blocked only by the later "still open" check)
- Status: Confirmed (repro run)
- Location: CheckDecl.fs:324-347 (`DDefTuple`) and :349-378 (`DDefPattern`): `generalize env t` is called unconditionally. Compare `checkDef` at :529-543, which uses `isSyntacticValue`.
- Description: each binder of a tuple/pattern `def` is generalized whatever the right-hand side is. `make-array` is therefore quantified, which is exactly what `checkDef`'s comment says must not happen. `checkModuleValuesAreConcrete` then rejects the binding as "still open", even when later uses (`array-set! a 0 5`) would have fixed its type. The plain `(def a (make-array 1))` is accepted. If the "still open" check is ever relaxed (as the REPL may already do), this becomes a polymorphic-mutable-cell hole: for example `(def (Tuple get put) (let ((c (make-array 1))) (Tuple (fun () (array-ref c 0)) (fun (v) (array-set! c 0 v)))))` would give `get : ∀a. -> a` and `put : ∀b. b -> unit`, generalized independently.
- Repro:
```
(import (std prelude))
(def a (make-array 1))
(def (Tuple c d) (Tuple (make-array 1) 2))
(defun (main args)
  (array-set! a 0 5)
  (array-set! c 0 5)
  (println (->str (array-ref a 0)))
  0)
```
- Observed: `Type Error at t3.bjo:3: the type of 'c, d' is still open ... (: c, d (List int))` (the suggested fix is not valid syntax either).   Expected: compiles; `c : (Array int)`, as for `a`.

---

### A `def` with a keyword signature accepts keyword calls that C# cannot compile
- Severity: Medium
- Status: Confirmed (repro run)
- Location: CheckDecl.fs:567-583 (`checkDef`: `FunMeta` is copied for keyword/rest signatures); Codegen emits a named argument on a `Func<>` field
- Description: `checkDef` deliberately registers keyword `FunMeta` for `(def g f)` when the signature has keywords. The type checker then accepts `(g 1 #:k 2)` and `(g 1)`. `g` is emitted as a `Func<int,int,int>` field, which has neither the named parameter `__kw_k` nor a default.
- Repro:
```
(import (std prelude))
(: f (-> int (#:k int) int))
(defun (f a #:k 10) (+ a k))
(: g (-> int (#:k int) int))
(def g f)
(defun (main args)
  (println (->str (g 1 #:k 2)))
  (println (->str (g 1)))
  0)
```
- Observed: Roslyn `CS1746: The delegate 'Func<int, int, int>' does not have a parameter named '__kw_k'` and `CS7036 ... 'arg2'`.   Expected: prints `3` and `11`, or a Bjolang error saying a `def` cannot carry keyword parameters.

---

### A defaulted numeric literal is not range-checked
- Severity: Low/Medium
- Status: Confirmed (repro run)
- Location: Traits.fs `defaultNumericLiterals` (approx. lines 200-215): `| TMeta m -> m.Value <- Some TypeConstants.intType` without `checkLiteralFits`
- Description: a literal whose type is fixed to a numeric type by unification is checked with `checkLiteralFits`. One that nothing fixed is defaulted to `int` without that check, so `3000000000` becomes an `int` and C# rejects the `uint` literal.
- Repro:
```
(import (std prelude))
(defun (main args)
  (def big 3000000000)
  (println (->str big))
  0)
```
- Observed: Roslyn `CS0266: Cannot implicitly convert type 'uint' to 'int'`.   Expected: `'3000000000' does not fit in an 'int'` (or defaulting to a wider type).

---

### Literal patterns are compared by spelling, not by value: duplicate clauses are not flagged and reach C# as duplicate case labels
- Severity: Low
- Status: Confirmed (repro run)
- Location: Exhaustiveness.fs:205-210 (`headOf`: `TPInt v -> CConst v`, and the same for strings and chars)
- Description: `16` and `016`, or `1.0` and `1.00`, are distinct `CConst`s, so the second clause is not reported as "can never run". Codegen emits a `switch` with two identical labels.
- Repro:
```
(import (std prelude))
(: f (-> int int))
(defun (f n) (match n (16 1) (016 2) (_ 3)))
(: g (-> double int))
(defun (g n) (match n (1.0 1) (1.00 2) (_ 3)))
(defun (main args) (println (->str (f 16))) (println (->str (g 1.0))) 0)
```
- Observed: Roslyn `CS0152: The switch statement contains multiple cases with the label value '16'` (and `'1'`).   Expected: `Pattern Error: this clause can never run`. Normalize the literal's value before comparing.

---

### `record-set` (functional update) accepts the same field twice
- Severity: Low
- Status: Confirmed (repro run)
- Location: InferExpr.fs:2833-2869 (`inferRecordUpdate`: no duplicate check; record construction does have one)
- Repro:
```
(import (std prelude))
(type (: P (Record (: x int) (: y int))))
(defun (main args)
  (def p (P (x 1) (y 2)))
  (def q (record-set p (x 5) (x 7)))
  (println (->str (record-ref q x)))
  0)
```
- Observed: Roslyn `CS1912: Duplicate initialization of member 'x'`.   Expected: `field 'x' ... is given twice`, the error construction already gives.

---

### A self-referential type alias is accepted and silently means an undeclared nominal type
- Severity: Low
- Status: Confirmed (repro run)
- Location: Annotations.fs:81-100 (`resolveWith`). Suspected cause, not verified: the alias body is resolved before the alias is registered, and the unknown-name check does not fire there.
- Repro:
```
(import (std prelude))
(type (: Loop (List Loop)))
(: x Loop)
(def x (list 5))
(defun (main args) (println (->str x)) 0)
```
- Observed: `these types do not match. '5' is a number ... where a 't23/Loop' is wanted`. The alias declaration itself is accepted, and `Loop` inside it means a type that does not exist.   Expected: an error at the alias ("recursive alias; use a Union/Record").

---

### Notes (checked, not bugs / documented)
- The value restriction holds for `def` (`isSyntacticValue`), local values (never generalized) and `let/mutable`/`def/mutable`. I tried closures over arrays and mutable cells through local functions, and every attempt was rejected correctly.
- Exhaustiveness: `Undecidable` (a refutable rest pattern in a vec) silently skips the whole match. This is documented in the module header.
- Every top-level `defun` needs a signature, so top-level mutual-recursion generalization is not reachable.
- Side observation, outside this area: a local `(defun (get) ...)` does not shadow the `get` macro, and the call fails as a malformed `get` macro.


---

## Correctness findings: effects/colours, lowering and optimisation passes

All repros were compiled with
`cd /tmp/correctness/lowering && BJOLANG_LIB=/tmp/correctness/lowering/lib dotnet /home/linis/Programmering/Bjolang/bin/compiler/Bjolang.dll X.bjo && dotnet X.exe`.
The repro files are in /tmp/correctness/lowering/ under the names given.

---

### A self tail call recomputes omitted keyword defaults from the previous iteration's parameters
- Severity: High
- Status: Confirmed (repro run)
- Location: LoopLowering.fs:107-113 (`normalizeRecur`), together with `lowerFunctionBody` (~line 395, `renameExpr toLocals lowered`)
- Description: When a self tail call leaves out a keyword argument, `normalizeRecur` fills the slot with the default *expression*. That expression is then renamed along with the rest of the body to read the current iteration's locals. A default that refers to another parameter (allowed; see `TestFiles/115_kwarg_const_defaults.bjo` with `#:b (+ a n)` and `#:fallback x`) is therefore evaluated against the **old** values, not the arguments of the new call. A real call would compute it from the new arguments.
- Repro (`kw.bjo`):
```scheme
(import (std prelude))
(: f (-> int (#:k int) int))
(defun (f n #:k n)
  (println #"n=${n} k=${k}")
  (if (= n 0) k (f (- n 1))))
(: g (-> int (#:a int) (#:b int) int))
(defun (g n #:a 0 #:b (+ a 100))
  (println #"n=${n} a=${a} b=${b}")
  (if (= n 0) b (g (- n 1) #:a (+ a 1))))
(defun (main args)
  (println (->str (f 2)))
  (println (->str (g 2)))
  0)
```
- Observed: `n=2 k=2 / n=1 k=2 / n=0 k=1 / 1` and `b=100,100,101 → 101`.   Expected: `k=2,1,0 → 0` and `b=100,101,102 → 102`, which is what the same calls give when they are not tail calls.

### A named loop's `(lp)` takes slot values from whatever user binding shadows the slot name
- Severity: High
- Status: Confirmed (repro run)
- Location: LoopDesugar.fs `jump` (~line 1105: "filling every slot … with whatever is in scope under that name otherwise") and `continueEdge` (~line 1600, which keeps `ELet` bodies as jump sites)
- Description: A jump builds its argument vector from `EIdent(slotName)` for every slot it does not override. Slot names include the accumulators, the `:with` variables and the variables of enclosing levels. The rebinding check (lines ~1040-1090) only looks at loop *clauses*. In a named loop, the `(lp)` call sits inside user code in the final `:do`. A `let`/`match` there that rebinds one of those names, such as the common `(let ((x (+ x 1))) … (lp))`, silently writes the local value into the carried slot.
- Repro (`nl.bjo`):
```scheme
(import (std prelude))
(defun (main args)
  (println (->str (loop lp (:for x (list 1 2 3)) (:acc total (summing x))
                        (:do (let ((total 1000)) (lp))) => total)))
  (println (->str (loop lp (:for x (list 1 2 3)) (:with w 0 (+ w 1)) (:acc total (summing w))
                        (:do (let ((w 1000)) (lp))) => total)))
  (println (->str (loop lp (:for x (list 1 2 3)) (:subloop) (:for y (list 1 2)) (:acc total (listing (Tuple x y)))
                        (:do (let ((x 1000)) (lp))) => total)))
  0)
```
- Observed: `1000`, `2002`, `((1, 1) (1000, 2) (2, 1) (1000, 2) (3, 1) (1000, 2))`.   Expected: `6`, `3`, `((1, 1) (1, 2) (2, 1) (2, 2) (3, 1) (3, 2))`.

### `:final` lets the clauses before it run during the next iteration
- Severity: High
- Status: Confirmed (repro run)
- Location: LoopDesugar.fs `buildClauses`, `LFinal` case (~line 1540)
- Description: The docs say `(:final cond)` "Aborts the loop *after* the current iteration completes". The implementation tests the hidden flag at the `:final` clause's position in the **next** iteration. So in that next iteration the element is pulled, and every `:do`, `:acc`, `:let` and so on written before `:final` runs for it. A `:when` before `:final` that rejects the next elements also postpones the stop, which can let many more elements through.
- Repro (`fin.bjo`, `fin3.bjo`):
```scheme
(import (std prelude))
(defun (main args)
  (println (->str (loop (:for x (list 1 2 3 4)) (:do (println #"do ${x}")) (:acc s (listing x)) (:final (= x 2)) => s)))
  (println (->str (loop (:for x (list 1 2 3 4 5 6 7)) (:when (odd? x)) (:acc s (listing x)) (:final (= x 3)) => s)))
  (println (->str (loop (:for x (list 1 2 3)) (:subloop) (:for y (list 1 2 3)) (:acc s (listing (Tuple x y))) (:final (= y 2)) => s)))
  0)
```
- Observed: `do 1, do 2, do 3, (1 2 3)`; `(1 3 5)`; `((1, 1) (1, 2) (1, 3))`.   Expected: `do 1, do 2, (1 2)`; `(1 3)`; `((1, 1) (1, 2))`.

### Suspending copies (`__bjo` twins) do not loop on self tail calls, so deep recursion overflows the stack
- Severity: High
- Status: Confirmed (repro run)
- Location: ColourTwins.fs `expandPolymorphicDefuns` (~line 95) and `expandReachingDefuns` (~line 230); LoopLowering.fs `lowerFunctionBody` (names = `[name]`)
- Description: Both twin generators copy the body verbatim as `DDefun(Naming.suspendingCopy name, args, body, …)`. The recursive call inside the copy still names the *original*. `EffectGraph.selectDoubles` only redirects it to `f__bjo` after `LoopLowering` has run, so LoopLowering does not see a self call: the ordinary copy becomes a `while`, and the suspending copy recurses through an awaited `Fiber` per step. Monomorphise.fs does rename the recursive call (`AlphaRename.renameFree` "The recursive call…"); ColourTwins does not. Any tail-recursive defun with a `-?->` parameter, or any tail-recursive defun that reaches a `defbjouble` (for example a port reader), overflows the stack when a bjoroutine calls it on large input.
- Repro (`tw.bjo`):
```scheme
(import (std prelude))
(: count-up (-> (-?-> int int) int int int))
(defun (count-up f n acc)
  (if (= n 0) acc (count-up f (- n 1) (f acc))))
(: inc (-bjo-> int int))
(defbjo (inc x) (+ x 1))
(defbjo (main)
  (println (->str (count-up (fun (x) (+ x 1)) 1000000 0)))
  (println (->str (count-up inc 1000000 0)))
  0)
```
  The inferred-copy variant (`tw2.bjo`) also overflows:
```scheme
(import (std prelude))
(: helper (-bjo-> int int))
(defbjo (helper x) (+ x 1))
(: step (-> int int))
(defbjouble (step x) (#:sync (+ x 1)) (#:bjo (helper x)))
(: count-up (-> int int int))
(defun (count-up n acc)
  (if (= n 0) acc (count-up (- n 1) (step acc))))
(defbjo (main) (println (->str (count-up 1000000 0))) 0)
```
- Observed: the first line prints `1000000`, then `Stack overflow.` with `countsubup__bjo … MoveNext` repeated (tw2: overflow straight away).   Expected: `1000000` twice, since the ordinary copy of the same source is a loop.

### A local function that shadows a top-level constrained function is replaced by the top-level one (or gets its dictionaries)
- Severity: High
- Status: Confirmed (repro run)
- Location: Monomorphise.fs `demandOf` (~line 455: `Map.tryFind bare cands` / `Map.tryFind bare env.Bindings`) and `redirect`; Lowering.fs:521-575 (`TApply` → `Map.tryFind (unqualify calleeName) env.Bindings`) and `takesDictionaries` / `SelfCall` (Lowering.fs:348-359, 505)
- Description: Both passes decide what a call to `TIdent(name, tArgs)` means by looking `name` up in the **module-level** bindings. They ignore lexical shadowing. A body-local generic `defun` has type arguments too, so a call to it:
  (a) is redirected by Monomorphise to a specialised copy of the top-level function of the same name, which silently calls the wrong function;
  (b) crashes Lowering with an internal error when the scheme variable counts differ (`List.zip` at Lowering.fs:575);
  (c) when the enclosing constrained function has the same name as the local (`SelfCall`), gets the enclosing function's dictionaries prepended, and the C# does not compile.
- Repro (`mono2.bjo`), wrong result:
```scheme
(import (std prelude))
(: pick (-> %a %a %a) (where (Eq %a)))
(defun (pick a b) (if (= a b) b a))
(defun (main args)
  (defun (pick x y) (if #t y x))
  (println (->str (pick 1 2)))
  0)
```
  Observed: `1`.   Expected: `2` (the local `pick`).
  (`mono1.bjo`: the same but with local `(defun (pick x y) y)`, which has two type variables.) Observed: "The lists had different lengths … This is a bug in the compiler" at `Lowering.DictionaryLowering.lowerExpr` line 575.
  (`low1.bjo`):
```scheme
(import (std prelude))
(: show-twice (-> %a string) (where (->str %a)))
(defun (show-twice v)
  (defun (show-twice (: n int)) (* n 2))
  (string-append (->str v) (->str (show-twice 21))))
(defun (main args) (println (show-twice #t)) 0)
```
  Observed: `error CS1501: No overload for method 'showsubtwice__4' takes 2 arguments`.   Expected: `True42`.

### Calling the result of a call is never treated as a yield point (`callSuspends` does not prune the type)
- Severity: High (unsound acceptance in sync code; a valid bjoroutine is rejected by Roslyn)
- Status: Confirmed (repro run)
- Location: TypedAST.fs:193 (`callSuspends` matches `TFun` directly), used by ColourCheck.fs:80/386, Codegen.fs:3074 and TypeVisitor.reachesAwait
- Description: `callSuspends` checks `match t with TFun(_,_,eff)`. When the callee is itself an application, e.g. `((make 1) 1)` or `((list-head fs) 1)`, its `Type` is a solved `TMeta` and not a `TFun`, so the answer is "does not suspend". The consequences: (1) ColourCheck accepts a suspending call inside a `defun`, and the program then fails in Roslyn; (2) inside a `defbjo`, Codegen emits no `await`, and a correct program fails to compile in C#; (3) the "defbjo with nothing that suspends" warning fires wrongly. An identifier callee works (its type is the instantiated `TFun`).
- Repro (`col/c11.bjo`, valid program rejected):
```scheme
(import (std prelude))
(: susp (-bjo-> int int))
(defbjo (susp x) (+ x 1))
(: make (-> int (-bjo-> int int)))
(defun (make n) susp)
(defbjo (main)
  (println (->str ((make 1) 1)))
  0)
```
  Observed: warning "'main' is defined with defbjo, but nothing in its body suspends", then `error CS0029: Cannot implicitly convert type 'Bjoml.Fiber<int>' to 'int'`.   Expected: prints `2`.
  (`col/c4.bjo`/`col/c8.bjo`, unsound acceptance: the same call `((first-of (list susp)) 1)` inside `(defun (main args) …)`.) Observed: Roslyn CS0029.   Expected: ColourCheck's "calling … is a yield point, and a yield point is not allowed here".

### A suspending body-local function passed to a `->` parameter is accepted and then fails in Roslyn
- Severity: Medium
- Status: Confirmed (repro run)
- Location: EffectGraph.fs `localFun` (~line 660) and `setColour`; ColourCheck.fs `descendBinding`
- Description: `localFun` gives a local function `EAsync` when its body awaits and **overwrites** its arrow's cell (`setColour` writes "over whatever is there"). That happens even when a value use already unified the cell with a declared `->` parameter. Loop groups have exactly this guard (`pinnedOrdinary`, with the `InEscapingLoop` message); local functions have none. The result is a `Func<int, Fiber<int>>` passed where a `Func<int,int>` is wanted.
- Repro (`col/c3.bjo`):
```scheme
(import (std prelude))
(: susp (-bjo-> int int))
(defbjo (susp x) (+ x 1))
(: app2 (-> (-> int int) int int))
(defun (app2 f x) (f x))
(defbjo (main)
  (defun (helper y) (susp y))
  (println (->str (app2 helper 5)))
  0)
```
- Observed: `error CS1503: Argument 1: cannot convert from 'System.Func<int, Bjoml.Fiber<int>>' to 'System.Func<int, int>'`.   Expected: a Bjolang colour error naming `helper`, like the one for an escaping loop or for passing a `defbjo` (`col/c1.bjo` gives "a bjoroutine cannot be used where an ordinary function is expected").

### A self tail call with `#:rest` arguments wraps the rest array twice, so C# compilation fails
- Severity: Medium
- Status: Confirmed (repro run)
- Location: LoopLowering.fs:115-122 (`normalizeRecur`, `restValue`)
- Description: By the time LoopLowering runs, the call's rest arguments are already packed into one array expression. `normalizeRecur` treats every positional argument after the mandatory ones as a rest *element* and wraps them again in `TArrayMake`. The emitted code is `var __next = new int[] { new int[] { 7, 8, 9 } };`.
- Repro (`rest.bjo`):
```scheme
(import (std prelude))
(: walk2 (-> int #:rest int int))
(defun (walk2 n #:rest xs)
  (if (= n 0) (array-length xs) (walk2 (- n 1) 7 8 9)))
(defun (main args) (println (->str (walk2 1))) 0)
```
- Observed: `error CS0029: Cannot implicitly convert type 'int[]' to 'int'` (the constrained-generic variant gives `'T_a[]' to 'T_a'`).   Expected: `3`.

### Seq fusion drops or adds producer evaluations when the consumer level has a `:with` end test
- Severity: Medium
- Status: Confirmed (repro run)
- Location: SeqFusion.fs `fuse`/`perSite` (~lines 520-545: `TIf(withTests, ExitCall, iteration)` where `iteration = rebindElem x …`); header comment lines 50-57
- Description: In the fused code, the yielded expression `x` moves into the consumer's element binding, which comes **after** the `:with` end test. In the unfused code, the cursor's `done?` (MoveNext) evaluates it before that test. With `(:for e S) (:with k … end)`, the order the header says "agree[s]", the fused loop therefore skips the producer's last yield expression. That expression is the `map` callback, so `map f` stops calling `f` on that element. The opposite order adds an extra producer `:do`; that one is documented. The same applies to a `:while`/`:until` that does not mention the element, because `recognize` puts it in the "with tests" partition.
- Repro (`f4.bjo`, and case 12 of `f1.bjo` with `map`), run with and without `BJOLANG_NO_FUSION=1`:
```scheme
(import (std prelude))
(: tr (-> string int int))
(defun (tr tag x) (println #"${tag}${x}") x)
(defun (main args)
  (println (->str (loop (:for e (seql (:for x (list 1 2 3 4)) (:yield (tr "y" x))))
                        (:with k 0 (+ k 1) (>= k 2)) (:acc s (listing e)) => s)))
  (println (->str (loop (:for e (map (fun (x) (tr "m" x)) (list 1 2 3 4 5)))
                        (:with k 0 (+ k 1) (>= k 2)) (:acc s (listing e)) => s)))
  0)
```
- Observed fused: `y1 y2 (1 2)` / `m1 m2 (1 2)`.   Unfused (reference semantics): `y1 y2 y3 (1 2)` / `m1 m2 m3 (1 2)`. The header says these orders agree; they do not.

### Seq fusion moves a yield point out of a `seql`, so a forbidden program is accepted only when fusion fires
- Severity: Medium
- Status: Confirmed (repro run)
- Location: SeqFusion.fs (runs before ColourCheck); ColourCheck.fs header ("Nothing between type checking and here can *move* a yield point")
- Description: Calling a suspending function inside a `(seq …)`/`(seql …)` is documented as an error (Syntax.org "Where a yield point may go"). ColourCheck runs after SeqFusion, though, and fusion splices the producer's body into the consuming bjoroutine. Whether the program compiles therefore depends on whether the optimiser fused it: on `BJOLANG_NO_FUSION`, on the fusion budgets (`maxYieldSites`, `restBudget`), and on whether TraitInline spliced the producer.
- Repro (`col/c12.bjo`):
```scheme
(import (std prelude))
(: susp (-bjo-> int int))
(defbjo (susp x) (+ x 1))
(defbjo (main)
  (println (->str (loop (:for e (seql (:for x (list 1 2 3)) (:yield (susp x)))) (:acc s (summing e)) => s)))
  0)
```
- Observed: it compiles and prints `9`. With `BJOLANG_NO_FUSION=1`: "calling 'susp' is a yield point … It is inside a (seq ...) body".   Expected: the same verdict either way.

### `(:until-cancelled)` turns an invalid loop into an infinite loop
- Severity: Medium
- Status: Confirmed (repro run)
- Location: LoopDesugar.fs `expandUntilCancelled` (~line 687, `entryBindings @ rewritten`) together with `buildGroup`'s "A loop must begin with a (:for …)" check
- Description: The zero-argument form puts an invariant `(:with %tok …)` in front of all clauses. A loop whose first clause is not a driver should be rejected ("A loop must begin with a (:for ...)…"). With the generated `:with` it now passes that check. The user's `:let` then belongs to a level 0 whose only driver is the never-ending invariant `:with`, and the user's `:for` opens level 1. When level 1 runs out, level 0 goes on forever. `:until-cancelled` only fires on cancellation, so the program hangs.
- Repro (`uc.bjo`):
```scheme
(import (std prelude))
(defun (main args)
  (println (->str (loop (:let y 5) (:for x (list 1 2 3)) (:until-cancelled) (:acc s (summing (+ x y))) => s)))
  0)
```
- Observed: it compiles and runs forever (killed by `timeout 10`, exit 124). Without the `(:until-cancelled)` clause it is a compile error.   Expected: the same compile error, or 21.

### A `:subloop` form's collector arguments are hoisted out of the whole loop, so they cannot see the level's variables (or they bind to an outer variable)
- Severity: Medium
- Status: Confirmed (repro run)
- Location: LoopDesugar.fs `buildGroup` (~line 1700: "Collectors are hoisted out of the whole loop, a form's included: their construction arguments are loop-invariant by definition"), and `splitCollector`
- Description: For a `:subloop` form the docs say its "accumulators start over on each run" and that "the form sees the level's variables". However, the collector, including a `folding` seed, is evaluated once before the outer loop starts. A seed that names the level's variable (for example `(folding (list-head row) …)`) is reported as "Unbound variable". If an outer binding of that name exists, it is captured silently. Implicit levels' `:acc` seeds behave the same way (e.g. `(:acc m (folding x …))` reads an outer `x`). The `=>` block, by contrast, gets a targeted error for that situation.
- Repro (`fin2.bjo`):
```scheme
(import (std prelude))
(defun (main args)
  (let ((row (list 100)))
    (println (->str (loop (:for row (list (list 5 1 9) (list 7 3)))
                    (:subloop (:for y row) (:acc m (folding (list-head row) (+ m y))))
                    (:acc out (listing m)) => out))))
  0)
```
- Observed: `(115 110)` (seed 100 from the outer `row`). Without the outer `let`: "Unbound variable: row".   Expected: `(20 17)` (seed = the head of each row), or a clear compile error.

### `seql`'s `=>` silently resolves a loop variable to an outer binding
- Severity: Low
- Status: Confirmed (repro run)
- Location: LoopDesugar.fs `desugarSeqLoop` (~line 532: the `=>` yield is placed *outside* the loop; the comment says "with no accumulators a `=>` expression cannot mention anything the loop bound")
- Description: `loop` rejects a `:with` variable named in `=>` with a targeted error (`rejectWithInFinish`). `seql` moves `=>` outside the loop and skips that check. So `=> a` for a `:with a` (or `=> x` for a `:for x`) is either "Unbound variable" or, if an outer `a` exists, silently the outer value. The comment's claim is false for `:with`/`:for` names.
- Repro (`s1.bjo`):
```scheme
(import (std prelude))
(defun (main args)
  (let ((a 100))
    (println (->str (seq->list (seql (:with a 0 (+ a 1)) (:for x (list 1 2 3)) (:yield x) => a)))))
  0)
```
- Observed: `(1 2 3 100)`.   Expected: the same error `loop` gives ("'a' … is a (:with ...) variable, and a loop variable is not in scope after the loop").

### A named loop's tail call inside `match`/`when` is rejected
- Severity: Low
- Status: Confirmed (repro run)
- Location: LoopDesugar.fs `continueEdge` (~line 1595: only `EApp`, `EIf`, `ELet` and `ELetTuple` are followed)
- Description: The docs say "Tail calls to the loop name are required". A call in tail position of a `match` arm or a `when` body is a tail call, but `continueEdge` does not descend into those forms, and `rejectLoopName` reports "may only be tail called from the loop's last (:do ...)". (`cond` works because it expands to `if`.)
- Repro (`nl4.bjo`):
```scheme
(import (std prelude))
(defun (main args)
  (println (->str (loop lp (:for x (list 1 2 3)) (:acc total (summing x))
                        (:do (match x (2 (lp #:total 100)) (_ (lp)))) => total)))
  0)
```
- Observed: "'lp' at nl4.bjo:4 is a loop name, which may only be tail called…".   Expected: `103`, as with the equivalent `if`.

### The final value of a `(seq …)` body is dropped without the must-use check
- Severity: Low
- Status: Confirmed (repro run)
- Location: MustUse.fs `checkExpr` (no `TSeq` case; the tail of a seq body is never treated as a discard)
- Description: The emitter drops a `seq` body's last expression (an iterator has no return value), but MustUse only looks at `TLet("_")`, `TWhen` and `TTryFinally`. `(seq (yield 1) (+ 2 3))` drops `5` with no diagnostic. The same value in the middle of a body is an error ("this value has type int and is discarded").
- Repro (`mu.bjo`):
```scheme
(import (std prelude))
(defun (main args)
  (println (->str (seq->list (seq (yield 1) (+ 2 3)))))
  0)
```
- Observed: it compiles and prints `(1)`.   Expected: the must-use error, or a yield of the value.

### (Outside my area, noticed in passing) `take` pulls one element more than it returns
- Severity: Low
- Status: Confirmed (repro run, `f1.bjo` case 9)
- Location: lib/std/prelude.bjo `Iterable` default `take`: `(seql (:with i 0 (+ i 1)) (:for elem s) (:finish (= n i)) (:yield elem))`
- Description: `:for elem s` pulls element n+1 before `:finish (= n i)` stops the loop. `(take 3 (map f xs))` calls `f` four times, and over a port-backed seq it consumes an extra line. `(take 0 s)` also forces one element. This happens fused and unfused alike.
- Repro: `(fold + 0 (take 3 (map (fun (x) (tr "m" x)) (list 1 2 3 4 5))))`.
- Observed: `m1 m2 m3 m4`, then 6.   Expected: `m1 m2 m3`, then 6.

---
Checked and found correct (no finding): per-iteration fresh bindings for closures over `:for`/`:with`/`:let`/accumulators/subloop variables, in plain loops, named `let` and fused loops; `:with` simultaneous update; `:finish` with and without a value; `:abandon` and `:subloop`, `:finish-subloop` and `:abandon-subloop`; `:when-let`/`:finish-let`; fusion of `map`/`filter`/`take`/`drop`/`enumerate`/`flat-map`/`take-while`/`any?`/`find` chains (identical output fused and unfused apart from the `:with` case above); re-walking a seq; colour checks for a `defbjo` passed as `->`, a bjo function stored in a tuple or record, `id`-wrapped bjo functions, and `-?->` twins choosing the right copy. HoistCheck's binder handling looked complete on reading.


---

## Correctness findings — codegen & .NET interop (Codegen.fs, Naming.fs, DotNetInterop.fs, ForeignTyping.fs, CSharpEmit.fs)

All repros were compiled with
`cd /tmp/correctness/codegen && BJOLANG_LIB=$PWD/lib dotnet /home/linis/Programmering/Bjolang/bin/compiler/Bjolang.dll --debug --emit-cs X.cs X.bjo && dotnet X.exe`
(`run.sh X.bjo` in the sandbox does this). The repro files are in `/tmp/correctness/codegen/`.

---

### 1. A named-let/self tail call made from inside a nested loop is a real call, so the stack overflows
- Severity: High
- Status: Confirmed (repro run)
- Location: LoopLowering.fs:~385-420 (`lowerLetRec`: an inner group's member bodies are lowered against the inner group's own targets only) → Codegen.fs:3270-3461 (`generateBlock` `TLoop`: `flatLoopEntry`/`mergedLoopEntry` both refuse, so it falls back to "General letrec … emit as local functions")
- Description: Syntax.org:692 says that a call to a named let's name in tail position "compiles to a jump, running in constant stack space", and Syntax.org:440 says a top-level `defun` "is promoted regardless". Neither holds when the tail call sits in tail position *inside another named let / `loop`* that is itself in tail position. The inner loop is inlined as a `while (true)`. The call to the outer name inside it is lowered as an ordinary `TApply`, because the inner member's body is lowered with only its own `LoopTarget`. As a result the outer loop is no longer "flat" and is emitted as a recursive C# local function. The generated C# is `return outer__4((i + 1), a);` inside the inner `while`, which uses one stack frame per outer iteration. The same thing happens to a top-level `defun` that calls itself from inside an inner named let, and to a `loop` whose `=>` finish clause tail-calls the outer named let.
- Repro (`nl.bjo`, `nl2.bjo`, `nl3.bjo`):
```scheme
(import (std prelude))
(: f (-> int int))
(defun (f n)
  (let outer ((i 0) (acc 0))
    (if (< i n)
        (let inner ((j 0) (a acc))
          (if (< j 2) (inner (+ j 1) (+ a 1)) (outer (+ i 1) a)))
        acc)))
(: g (-> int int int))
(defun (g i acc)            ; top-level defun, "promoted regardless"
  (if (< i 1000000)
      (let inner ((j 0) (a acc))
        (if (< j 2) (inner (+ j 1) (+ a 1)) (g (+ i 1) a)))
      acc))
(defun (main args)
  (println (->str (f 1000000)))
  (println (->str (g 0 0)))
  0)
```
  The same happens with `(loop (:for k [1 2]) (:acc s (summing k)) => (outer (+ i 1) (+ acc s)))` in tail position of `outer`.
- Observed: `Stack overflow. Repeated 104283 times: at …nl_Module.<f>g__outer__4|1_0(Int32, Int32, …)`. `g` crashes the same way. With small n the results are correct (6).   Expected: 2000000 and 2000000, in constant stack.

### 2. Nested .NET types are mistyped or unspellable: a generic nested type collapses to its outer type, and a non-generic one is emitted as `Outer` + "add" + `Inner`
- Severity: High
- Status: Confirmed (repro run)
- Location: DotNetInterop.fs:572-594 (`clrTypeName`) and 613-669 (`mapClrType`), Codegen.fs `typeToString`/`conBaseName` → Naming.fs:79 (`sanitizeIdent` maps `+` → `add`)
- Description:
  - **Generic nested types** (`Dictionary<K,V>.KeyCollection`, `List<T>.Enumerator`, …): `clrTypeName` takes the generic *definition* `System.Collections.Generic.Dictionary`2+KeyCollection` and cuts at the **first** backtick (`full.IndexOf '`'`). That leaves `System.Collections.Generic.Dictionary`, so `+KeyCollection` is dropped. `mapClrType` then builds `TCon("System.Collections.Generic.Dictionary", [K; V])`. The member gets the outer type. This is unsound: `(.ContainsKey (.-Keys d) "a")` type-checks, and `(.MoveNext (.GetEnumerator v))` is rejected with "List`1[Int32] has no public instance method named 'MoveNext'".
  - **Non-generic nested types** (`StringBuilder.ChunkEnumerator`, `Environment.SpecialFolder`, …): `mapClrType`'s non-generic branch keeps `t.FullName` with its `+` (`TCon("System.Text.StringBuilder+ChunkEnumerator")`). When emitted, `sanitizeIdent` rewrites `+` to `add`, which gives `System.Text.StringBuilderaddChunkEnumerator`. That type does not exist.
  - Nested types also cannot be named in `import/class`. `System.Environment+SpecialFolder` is accepted at the import but fails at use ("cannot find the .NET type 'System.Environment.SpecialFolder'"), and `System.Environment.SpecialFolder` fails at the import.
- Repro (`v4.bjo`, `v3.bjo`, `v6.bjo`):
```scheme
(import (std prelude))
(import (std mutable map))
(import/class (StringBuilder (: System.Text.StringBuilder (-> string StringBuilder))))
(defun (main args)
  (def d (mutablemap))
  (mutablemap-set! d "a" 1)
  (def ks (.-Keys d))
  (println (->str (.ContainsKey ks "a")))     ; KeyCollection has no ContainsKey
  (def sb (StringBuilder. "abc"))
  (def loc (.GetChunks sb))
  (println (->str (.MoveNext loc)))
  0)
```
- Observed: Bjolang accepts the program. C# then fails with `CS0029: Cannot implicitly convert type 'Dictionary<Utf8String,int>.KeyCollection' to 'Dictionary<Utf8String,int>'` (the emitted line is `System.Collections.Generic.Dictionary<…> ks = d.Keys;`) and `CS0234: The type or namespace name 'StringBuilderaddChunkEnumerator' does not exist in the namespace 'System.Text'`. `(def (: x int) (.GetEnumerator v))` reports the type as `(System.Collections.Generic.List int)`.   Expected: `.Keys` typed as `KeyCollection` (and `ContainsKey` rejected in Bjolang), the enumerator typed as `List<int>.Enumerator`, and nested types spelled `Outer<…>.Inner` in C#.

### 3. `import/extern` with a declared signature rejects integer literals: overload resolution runs on the literal's default `int` before the signature is applied
- Severity: Medium
- Status: Confirmed (repro run)
- Location: ForeignTyping.fs:575-583 (`resolveExternMethod`: `settleLiterals argTypes` and then `DotNetInterop.resolveMethod` by argument type), followed by ForeignTyping.fs:440-462 (`checkDeclaredExtern` unifies the declared signature with the overload that was picked)
- Description: TestFiles/051 says that each clause "pins its own overload with a signature", and TypedAST's `spelledType` docs say literals "adapt when passed to functions expecting byte, long, or double". For a direct application of an extern alias, the literal arguments are settled to `int` first. Overload resolution then picks `Math.Max(int,int)`/`Math.Abs(int)`, and the declared `(-> long long long)` is unified against that and fails. Passing the same alias as a value (`(def f dabs) (f -3)`) works, so the two forms are inconsistent.
- Repro (`ov.bjo`):
```scheme
(import (std prelude))
(import/extern
  (lmax (: System.Math.Max (-> long long long)))
  (dabs (: System.Math.Abs (-> double double))))
(defun (main args)
  (println (->str (lmax 3 4)))
  (println (->str (dabs -3)))
  0)
```
- Observed: `Type error: these types do not match. (-> long long long) (-> int int int) … at ov.bjo:6`. `(dabs -3)` fails in the same way.   Expected: `4` and `3`. The declared parameter types should pin the literals, as they do for the eta-expanded value form.

### 4. Tuples of 8+ elements (and 1-tuples) emit invalid C#
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Codegen.fs:619-621 (`typeToString` `TTuple types` → `ValueTuple<%s>`), Codegen.fs:~2214 (`TTupleMake args` → `(a, b, …)`), plus the tuple pattern code in `generatePattern`
- Description: `typeToString` spells every tuple as `ValueTuple<T1,…,Tn>`. C# has no `ValueTuple` with 9+ type parameters. `ValueTuple<T1..T8>` exists, but its 8th argument is `TRest`, and a tuple literal `(1,…,8)` has type `ValueTuple<…,ValueTuple<int>>`, so it does not convert. Tuple patterns over 8 elements also lose their element types (`object + object`). In the other direction, `TTupleMake [x]` emits `(x)`, which C# reads as a parenthesised `x`. The pattern side handles the 1-tuple as `ValueTuple<T> { Item1: … }`; construction does not.
- Repro (`tup8.bjo`, `tup1.bjo`):
```scheme
(import (std prelude))
(: f (-> (Tuple int int int int int int int int) int))
(defun (f t) (match t ((Tuple a b c d e f g h) (+ a h))))
(defun (main args)
  (println (->str (f (Tuple 1 2 3 4 5 6 7 8))))
  (def u (Tuple 1 2 3 4 5 6 7 8 9))
  (println (match u ((Tuple a b c d e f g h i) (->str (+ a i)))))
  (def t (Tuple 5))
  (println (match t ((Tuple x) (->str x))))
  0)
```
- Observed: `CS0019: Operator '+' cannot be applied to operands of type 'object' and 'object'`, `CS0029: Cannot implicitly convert type '(int, …, int)' to 'System.ValueTuple<int, …, int>'`, `CS0308: The non-generic type 'ValueTuple' cannot be used with type arguments`, and for the 1-tuple `CS0029: Cannot implicitly convert type 'int' to 'System.ValueTuple<int>'` (emitted `ValueTuple<int> t = (5);`).   Expected: `9`, `10`, `5`. Fix: emit the C# tuple-type syntax `(T1, …, Tn)` (it nests TRest itself), and use `ValueTuple.Create(x)`/`new ValueTuple<T>(x)` for a 1-tuple.

### 5. Name mangling: ASCII symbol characters `$ ^ ~ | @ \` and infix `%` pass through `sanitizeIdent` unchanged → invalid C#
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Naming.fs:79-87 (`sanitizeIdent`) and Naming.fs:52-64 (`escapeUnidentifiable` returns the part unchanged when it is all ASCII)
- Description: Lexer.fs:320 (`isSymbolChar`) lets any non-whitespace character other than `()[]{},:";'` into a symbol, so `a$b`, `f|g`, `x^2`, `a~b`, `a@b`, `a%b`, `a\b` are valid Bjolang names. `sanitizeIdent` only rewrites `- ? ! + * / < > = ' & # ::`. `escapeUnidentifiable` escapes only non-ASCII characters ("The ASCII a name may hold has its own spellings by then"), and that is not true for these characters. The result is that these names reach C# verbatim. (`a.b` has the same problem, but a dot may be intended as member syntax.)
- Repro (`n2.bjo`, `n3.bjo`):
```scheme
(import (std prelude))
(: $f (-> int))
(defun ($f) 7)
(: f|g (-> int))
(defun (f|g) 8)
(defun (main args)
  (let ((a^b 5) (c~d 6) (e%f 7))
    (println (->str (+ a^b (+ c~d e%f)))))
  (println (->str (+ ($f) (f|g))))
  0)
```
- Observed: C# errors such as `CS1056: Unexpected character '$'`, `CS1519: Invalid token '$' in a member declaration`, `CS1002: ; expected`, `CS1525: Invalid expression term '%'`/`'|'`.   Expected: compiles and prints `18` and `15`. Fix: escape every non-identifier rune, ASCII included (for example with the `_uXX_` scheme).

### 6. Non-injective mangling is only checked for top-level functions and locals. Keyword parameters, record fields, union cases and type names collide silently.
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Naming.fs:79 (`sanitizeIdent`, documented as "Deliberately not injective"), Naming.fs:103 (`keywordParamName`), Codegen.fs:1815-1830 (`generateParameterList`), record/union/type declaration emission in `generateDecl`
- Description: `a-b`/`asubb` (also `x?`/`x_QMARK`, `x!`/`x_BANG`) get a proper diagnostic for top-level functions ("'a-b' and 'asubb' are both 'asubb' in the C# …") and AlphaRename for locals (TestFiles/136). Keyword parameters, record fields, union cases and declared type names get no such check, so the program is accepted and then C# fails.
- Repro (`k2.bjo`, `r1.bjo`, `r3.bjo`, `r6.bjo`):
```scheme
(import (std prelude))
(: kf (-> int (#:a-b int) (#:asubb int) int))
(defun (kf n #:a-b 1 #:asubb 2) (+ n (+ (* 10 a-b) (* 100 asubb))))
(type (: Car (Record (: a-b int) (: asubb int))))
(type (: T (Union (: Foo? int) (: Foo_QMARK int))))
(type (: Pt-A (Record (: x int))))
(type (: PtsubA (Record (: y int))))
(defun (main args) (println (->str (kf 0 #:asubb 3))) 0)
```
- Observed (each case run on its own): `CS0100: The parameter name '__kw_asubb' is a duplicate`, `CS0100: The parameter name 'asubb' is a duplicate`, `CS0102: The type 'r3__T' already contains a definition for 'r3__Foo_QMARK'`, `CS0101: The namespace '…' already contains a definition for 'r6__PtsubA'`.   Expected: either a Bjolang diagnostic like the top-level one, or distinct C# names.

### 7. C# string literals: U+2028, U+2029 and U+0085 are not escaped (CS1010 "Newline in constant")
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Codegen.fs:1109-1110 (`escapeStringLiteral`). The same gap exists in Codegen.fs:840 (`escapeSexpr`) and Codegen.fs:6536 (`escapeAttribute`, metadata attribute for a library whose inline template contains such a string)
- Description: `escapeStringLiteral` escapes only `\ " \n \r \t`. C# treats U+0085 (NEL), U+2028 (LINE SEPARATOR) and U+2029 (PARAGRAPH SEPARATOR) as new-line characters, and a regular string literal may not contain them. A Bjolang string holding one of them raw (pasted text, or text containing NEL) is emitted into `FromUtf8("…"u8)` as is. NUL, VT and FF are fine (checked).
- Repro (`s1.bjo`; it has to be written with printf because the characters are invisible):
```sh
printf '(import (std prelude))\n(defun (main args)\n  (println (->str (string-length "a\xe2\x80\xa8b")))\n  (println (->str (string-length "a\xc2\x85b")))\n  0)\n' > s1.bjo
```
- Observed: `Program.cs(55,99): error CS1010: Newline in constant`, `CS1026: ) expected`, …   Expected: `3` and `3`. Fix: emit `\u2028`, `\u2029`, `\u0085` (or `\uXXXX` for any non-printable character).

### 8. `(- x)` on `uint`/`ulong` emits invalid C#, although Numerics.org says `Num` (negate) holds for every numeric type
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Codegen.fs:1767-1790 (`promotesToInt`/`castPromoted` cover only byte/short/ushort) and the `negate` emission in `generateApply`
- Description: In C#, `-uintValue` is promoted to `long` (CS0266 when it is assigned back to `uint`), and unary minus on `ulong` is not defined at all (CS0023). Docs/Numerics.org:378 lists negate as part of `Num` for every numeric type, and overflow is documented to wrap. Unsigned negate should be emitted as `unchecked((uint)(0u - x))`, or as `unchecked(~x + 1)`.
- Repro (`un.bjo`):
```scheme
(import (std prelude))
(: nu (-> uint uint))
(defun (nu x) (- x))
(: nul (-> ulong ulong))
(defun (nul x) (- x))
(defun (main args)
  (println (->str (nu 5u)))
  (println (->str (nul 5UL)))
  0)
```
- Observed: `CS0266: Cannot implicitly convert type 'long' to 'uint'` and `CS0023: Operator '-' cannot be applied to operand of type 'ulong'`.   Expected: `4294967291` and `18446744073709551611`.

### 9. `int-min / -1` and `int-min % -1` throw `OverflowException`, although "Overflow wraps, nothing checks at run time". Constant-folded, the same expression wraps.
- Severity: Low
- Status: Confirmed (repro run)
- Location: Codegen.fs:1716-1728 (`infixOperators` emits `/` and `%` as the plain C# operators)
- Description: Docs/Numerics.org:108-120 says integer overflow wraps and "Nothing checks at run time". On .NET, `int.MinValue / -1` and `int.MinValue % -1` (likewise for `long`) throw `System.OverflowException` even in an unchecked context. Because constant expressions are wrapped in `unchecked(...)` and folded by Roslyn, the same expression gives `-2147483648` with literal operands and crashes with variable operands. `%` is the more surprising case, since the mathematically correct answer is 0.
- Repro (`o1.bjo`, `o2.bjo`):
```scheme
(import (std prelude))
(: dv (-> int int int))
(defun (dv a b) (/ a b))
(: rm (-> int int int))
(defun (rm a b) (% a b))
(defun (main args)
  (println (->str (/ -2147483648 -1)))   ; constant: prints -2147483648
  (println (->str (rm -2147483648 -1)))  ; crashes
  (println (->str (dv -2147483648 -1)))
  0)
```
- Observed: `-2147483648`, then `Unhandled exception. System.OverflowException: Arithmetic operation resulted in an overflow.`   Expected (as documented): `0` for the remainder and `-2147483648` for the quotient, the same with or without constants. Alternatively, document the exception.

### 10. Setting an indexer/property on a value-type module-level `def` emits invalid C#. Mutating methods on such a def silently act on a copy.
- Severity: Medium
- Status: Confirmed for the setter (repro run); Suspected (from reading) for the silent copy
- Location: Codegen.fs:6103-6116 (a module-level `def` is emitted as `public static readonly T name;`), plus the indexer/`#:set` emission at Codegen.fs:~2014-2034
- Description: A top-level `def` of a .NET struct type becomes a `static readonly` field. Writing an indexer or property through it is CS1650 in C#, whereas the same code on a local `def` compiles and mutates the local. By C# semantics, any mutating *method* called on a `static readonly` struct field (e.g. `MoveNext` on an enumerator struct, `Add` on `HashCode`) runs on a defensive copy, so the mutation is silently lost. I could not get a runnable example of that, because nested enumerator types are broken (finding 2) and struct default constructors are refused (finding 13).
- Repro (`v1.bjo`):
```scheme
(import (std prelude))
(import/class
  (BitVector32 (: System.Collections.Specialized.BitVector32 (-> int BitVector32))))
(def top (BitVector32. 0))
(defun (main args)
  (def loc (BitVector32. 0))
  (.set_Item loc 4 #t)        ; fine: loc[4] = true
  (.set_Item top 4 #t)        ; v1_Module.top[4] = true
  (println (->str (.-Data loc)))
  (println (->str (.-Data top)))
  0)
```
- Observed: `error CS1650: Fields of static readonly field 'v1_Module.top' cannot be assigned to (except in a static constructor or a variable initializer)`.   Expected: `4` and `4` (or a Bjolang diagnostic that a module-level struct cannot be mutated).

### 11. `(cast System.Object (fun …))` (a lambda literal passed to an `object` parameter) emits `(object)(x => …)`: CS8917
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Codegen.fs:2261-2269 (`TCast` emits `((T)(expr))` with no delegate type for a lambda operand). The same thing happens when DotNetInterop's `selectOverload` picks an `object` parameter for an unresolved lambda argument (scored `Some 100` at DotNetInterop.fs:1532).
- Description: C# cannot cast a lambda expression directly to `object`, because it has no natural delegate type unless all parameter types are explicit. The checker knows the lambda's type (`Func<int,int>`), so the emitter should write `(object)(Func<int,int>)(x => …)`, or bind the lambda to a typed temporary first. `(.Append sb (fun (x) (+ x 1)))` hits the same error through overload resolution.
- Repro (`lam.bjo`):
```scheme
(import (std prelude))
(import/extern (same? (: System.Object.ReferenceEquals (-> System.Object System.Object bool))))
(defun (main args)
  (println (->str (same? (cast System.Object (fun (x) (+ x 1))) (cast System.Object 1))))
  0)
```
- Observed: `error CS8917: The delegate type could not be inferred.` (emitted as `System.Object.ReferenceEquals(((object)((x) => {…`)   Expected: `False`.

### 12. Values of module-declared types, `Symbol` and `Keyword` cannot be passed to a .NET `object` parameter (no overload found)
- Severity: Low
- Status: Confirmed (repro run)
- Location: DotNetInterop.fs:1529-1557 (`scoreArgument`: `tryClrTypeOf arg` is `None` for a type that is not compiled yet or not resolvable, which is "no fit", even when the parameter is `System.Object`)
- Description: `(.Append sb 1.5)`, `(.Append sb (list 1 2))`, `(.Append sb (Some 3))` and tuples are boxed to `object` automatically (score 3). A record or union declared in the module, a `Symbol` or a `Keyword` is rejected instead, because reflection cannot resolve its CLR type at check time. Every Bjolang value is an `object`, so `param = typeof<obj>` should accept any argument whose CLR type is unknown.
- Repro (`ob.bjo`):
```scheme
(import (std prelude))
(import/class (StringBuilder (: System.Text.StringBuilder (-> StringBuilder))))
(type (: Car (Record (: year int))))
(defun (main args)
  (def sb (StringBuilder.))
  (ignore (.Append sb (Car (year 1999))))
  (ignore (.Append sb 'sym))
  (println (.ToString sb))
  0)
```
- Observed: `Type Error at ob.bjo:6: no overload of 'System.Text.StringBuilder.Append' accepts (ob/Car). The candidates are: … (System.Object) …`. The same error appears for `(Symbol)`.   Expected: `Append(object)` is chosen, as it is for lists, options and tuples.

### 13. Value types without an explicit constructor (`System.HashCode`, `SpinWait`, …) are reported as having "no public constructor"
- Severity: Low
- Status: Confirmed (repro run)
- Location: DotNetInterop.fs:1917 (`resolveConstructor`, which only looks at `GetConstructors()`; a struct's implicit parameterless constructor is not reflected)
- Repro:
```scheme
(import (std prelude))
(import/class (HashCode (: System.HashCode (-> HashCode))))
(defun (main args)
  (def h (HashCode.))
  (.Add h 5)
  (println (->str (.ToHashCode h)))
  0)
```
- Observed: `Type Error at …: 'System.HashCode' has no public constructor.`   Expected: `new System.HashCode()` (valid C# for every struct).

### 14. `ref struct` results (`ReadOnlySpan<T>`, `Span<T>`, …) are accepted as ordinary values and then break in lambdas, generics, async methods and static fields
- Severity: Low
- Status: Confirmed (repro run, lambda case); other cases Suspected (from reading)
- Location: DotNetInterop.fs:613 (`mapClrType` has no `IsByRefLike` check). Nothing in the compiler mentions `IsByRefLike`.
- Description: A .NET member returning a ref struct type-checks like any other value. Capturing it in a closure gives CS8175. By C# rules, putting it in a `List`, a tuple or a generic, holding it across an await in a `defbjo` (CS4012), or making it a top-level `def` (a static field, CS8345) all fail as well. The type checker should refuse ref-struct types outside direct, immediately consumed use.
- Repro (`sp.bjo`):
```scheme
(import (std prelude))
(import/extern
  (as-span (: System.MemoryExtensions.AsSpan (-> System.String (System.ReadOnlySpan System.Char)))))
(defun (main args)
  (def s (as-span (string->clr-string "hello")))
  (println (->str (.-Length s)))
  (def f (fun () (.-Length s)))
  (println (->str (f)))
  0)
```
- Observed: `error CS8175: Cannot use ref local 's' inside an anonymous method, lambda expression, or query expression`.   Expected: a Bjolang diagnostic (or `5`/`5`).

### 15. Contextual and reserved C# identifiers are not escaped: `await` inside a `defbjo`, and `__arglist`/`__makeref`/`__reftype`/`__refvalue`
- Severity: Low
- Status: Confirmed (repro run)
- Location: Naming.fs:27-35 (`escapeReserved`)
- Description: `await` is a keyword inside an `async` method, which is what every `defbjo` becomes, so a parameter or local called `await` is a syntax error there. `@await` would work. The undocumented keywords `__arglist`, `__makeref`, `__reftype` and `__refvalue` are reserved everywhere and are not in the list. (`async`, `global`, `dynamic`, `nameof`, `value` were checked and do compile.)
- Repro (`w1.bjo`):
```scheme
(import (std prelude))
(: nap (-> int int))
(defbjo (nap x) (sync (timeout 1)) x)
(: f (-> int int))
(defbjo (f await) (def async (nap await)) (+ async await))
(: g (-> int int))
(defun (g y) (def __arglist 6) (+ y __arglist))
(defbjo (main args) (println (->str (f 2))) (println (->str (g 0))) 0)
```
- Observed: `CS1525: Invalid expression term ')'` (emitted `int async = (await w1_Module.nap(await));`) and `CS1001: Identifier expected` for `__arglist`.   Expected: `4`, `6`.

### 16. Double literals `1.` and `1.e3` are accepted by Bjolang but emitted verbatim → invalid C#
- Severity: Low
- Status: Confirmed (repro run)
- Location: TypedAST.fs:521-542 (`NumericLiteral.csharp`: for `double` with `real = true` it returns `Some digits` unchanged)
- Description: `spelledType` classifies any text containing `.` as a double, but C# has no `1.` or `1.e3` literal (`1.` is member access on `1`).
- Repro:
```scheme
(import (std prelude))
(defun (main args) (println (->str 1.)) (println (->str 1.e3)) 0)
```
- Observed: `CS1001: Identifier expected` for `1.`, and `CS1061: 'int' does not contain a definition for 'e3'` for `1.e3`.   Expected: `1` and `1000`, or a lexer error. Fix: normalise to `1.0`/`1.0e3`, or append `d` (`1.d` is also invalid, so insert a `0`).

---

Notes / checked and found OK (no finding): argument evaluation order with hoisted statement-shaped operands (match/try/with-return/`set!` in arguments); short-circuit `and`/`or` with statement-shaped right operands; closures capturing named-let/`loop`/self-tail-call variables (per-iteration copies work); mutual tail recursion of local functions, including `goto case` through nested `switch`es; try/finally around tail calls and in `defbjo` with `with-return`; keyword-argument evaluation order and defaults; string/char/keyword patterns with quotes and backslashes; negative and `int64`-min literals and patterns, `-0.0`, hex/binary/suffixed literals; `(.ToString -5)`/receiver parenthesisation; interpolated strings containing `{`, `}`, `$`, quotes; `seq`/`yield` with closures and `with-return`; enums; generic method imports (`Enumerable.Select`, `Repeat`, `Array.Empty`); indexers on locals; nullable string returns (→ `""`, as documented).

Unrelated to codegen, noticed in passing: `(->str #t)` prints `True` (prelude falls back to `Object.ToString` for `bool`); top-level mutual recursion (`ev?`/`od?`) is not tail-call optimised and overflows at 10M. The docs do not promise this, so it is not filed. The CS errors in finding 6 are also not mapped back to Bjolang lines in a useful way.


---

## Correctness findings: driver, modules, build system, metadata, REPL, docs

Every repro was run in `/tmp/correctness/driver/` with
`BJOLANG_LIB=/tmp/correctness/driver/lib dotnet /home/linis/Programmering/Bjolang/bin/compiler/Bjolang.dll`, written below as `$C`.
The sandbox stdlib records' `output`/`interface-of` paths were rewritten to the sandbox copy, so early cutoff runs as it would in a real tree.
None of the issues below is listed in Todo.org.

---

### A local `def` placed after a use of an imported name of the same name: the use silently reads the uninitialised local field
- Severity: High
- Status: Confirmed (repro run)
- Location: import/local name resolution vs. emission. Inference binds the earlier use to the import, but the C# emitted for it is the bare `k`, which binds to the module's own static field (Pipeline.fs `loadModuleGraph` / Codegen.fs identifier emission; exact emission site not pinned down).
- Description: Without an import, a forward reference to a later top-level `def` is refused ("Unbound variable: b"). With an import that offers the same name, the checker accepts the forward reference against the import. The generated code reads the importer's own field instead. The static constructor has not initialised that field yet, so the value is `0`/`null`. The result matches neither the import nor the local definition.
  - This is also the root cause of the REPL "value is 0" symptom in the replay finding below.
- Repro:
```
;; klib.bjo
(: k int)
(def k 5)
(export k)
;; ki.bjo
(import "klib.bjo")
(: t int)
(def t (+ k 1))
(: k int)
(def k 100)
(defun (main args) (println (->str t)) 0)
```
  The same happens with strings: `(def t (string-length s))` before a local `(def s "a much longer string")`, with an imported `s = "lib"`, prints `0`.
- Observed: prints `1`, with no warning. Expected: `6` (the import is in scope at that point), or a compile error for the forward reference. It should never be `1`.

### Imports written inside an `(include ...)`d file are ignored by staleness checks and by `--build-graph`
- Severity: High
- Status: Confirmed (repro run)
- Location: Pipeline.fs:1043-1061 (`factsOf`), used by `upToDateAgainst` (1079), `ensureLibrary`, `importedSources` (1317) and so by BuildGraph.
- Description: `factsOf` calls `expandIncludes` only to get `Sources`. It computes `Imports = importsOf (withImplicitPrelude bjoPath forms)` from the *unexpanded* top-level forms. `loadModuleGraph` (~1911) resolves imports from the *expanded* forms, so an import written in an included file is a real dependency that the staleness walk never visits and never rebuilds. The record's `dep` lines don't help either:
  - the dependency's `.dll` is never rebuilt, so it stays older than the importer;
  - the transitive `Deps` entry for a file under no package is a plain path, which is never rebuilt.
  
  The edit is silently dropped. `--build-graph` does not see the edge either.
- Repro:
```
;; lib.bjo
(include "part.bjo")
(export g)
;; part.bjo
(import "dep.bjo")
(: g (-> int int))
(defun (g x) (dep-f x))
;; dep.bjo
(: dep-f (-> int int))
(defun (dep-f x) (+ x 1))
(export dep-f)
;; main.bjo
(import "lib.bjo")
(defun (main args) (println (->str (g 1))) 0)
```
  `$C main.bjo && dotnet main.exe` prints 2. Then `sed -i 's/(+ x 1)/(+ x 100)/' dep.bjo` and run `$C main.bjo && dotnet main.exe` again. `$C --build-graph --dry-run lib.bjo` reports "lib.dll imports …/prelude.bjo" only, with 0 to build.
- Observed: after the edit, `dep.bjo` is not rebuilt and the program still prints `2`. Expected: `dep.dll` (and `lib.dll`) rebuilt, and the program prints `101`.

### REPL: a module edited on disk and rebuilt during a session is still used in its old form, or its names become unbound
- Severity: High
- Status: Confirmed (repro run)
- Location: Pipeline.fs:1586 (`System.Reflection.Assembly.LoadFile(absPath)`, which .NET caches per path) and Repl.fs:476/493 (`AssemblyLoadContext.Default.LoadFromAssemblyPath`, where an assembly name, once loaded, cannot be replaced). Rebuilding happens through `ensureLibrary`, but nothing in the process can load the new bytes. The `dllCache` key includes the timestamp, but the `Assembly` underneath it does not change.
- Description: Imports are replayed into every entry, so after an edit `ensureLibrary` rebuilds the `.dll` ("Building imported module m.bjo" is printed), and then:
  1. If the module was already executed, later entries run the **old** code: the default context already holds that assembly name.
  2. If the export list changed, every name from the module becomes unbound in the session, the unchanged ones included. This happens even if nothing from it had run yet.
  
  A fresh session with the same rebuilt `m.dll` works correctly (`(val2 5)` gives `5`). Docs/Repl.org documents shadowing between *entries*, not this.
- Repro (m.bjo exports `val` = `(+ x 1)`):
```
(echo '(import "m.bjo")'; echo '(val 1)'; sleep 12;
 printf '(: val (-> int int))\n(defun (val x) (+ x 100))\n(export val)\n' > m.bjo;
 echo '(val 1)') | $C --repl
```
  Variant: add `(: val2 (-> int int)) (defun (val2 x) x) (export val val2)` in the edit, then `(val 1)` and `(val2 1)`.
- Observed: case 1 prints `2` again after the rebuild. Case 2 gives `Unbound variable: val` and `Unbound variable: val2`. Expected: `101`, and both names bound. At minimum, a message that the module cannot be reloaded in this session.

### REPL: an `(import ...)` that shares a line with other forms is replayed with those forms into every later entry (repeated side effects, wrong values)
- Severity: High
- Status: Confirmed (repro run)
- Location: Repl.fs:666-673 (`Imports = … |> List.map (textOf text)`), with `textOf` at Repl.fs:305-313, which returns *whole lines*. Interacts with `entrySource` at Repl.fs:384-428, which puts the replayed preamble *after* the entry's body.
- Description: The REPL remembers an import as the full source lines it was written on. Anything else on that line is replayed into every later entry:
  - a `(def ...)` there re-runs its initialiser (side effects repeat);
  - it defines a local that shadows the earlier entry's binding. The preamble comes after `(def __bjo_value ...)`, so the value reads the uninitialised field (see the first finding).
- Repro:
```
printf '(import (std maths)) (def x (begin (println "SIDE") 1))\n(+ 1 2)\n(def y 5)\n(+ y x)\n' | $C --repl
```
  Also: `(import "m.bjo") (def s "m.bjo")` then `(string-length s)` prints `0`.
- Observed: `SIDE` is printed by every entry, and `(+ y x)` prints `5`. Expected: `SIDE` once, and `6`. Also expected: `5` (or the path length), not `0`.

### Cyclic imports are not reported: either an internal compiler error, or (with stale `.dll`s present) silently accepted
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Pipeline.fs:1209-1238 (`ensureLibrary`: `if not (walking.Add bjoPath) then dllPath`), Build.fs `checkNotCyclic`, Pipeline.fs:1586 (`Assembly.LoadFile`).
- Description: The comment says "a cycle terminates here … an illegal one is `Build.checkNotCyclic`'s to report". But the memo hands back the not-yet-built `.dll` path to the *compile* of the inner module. `checkNotCyclic` is never reached, because the inner compile asks `ensureLibrary`, not `compileLibrary`, for the outer module. Three outcomes:
  - a self-import or a↔b cycle with no `.dll`s: `Could not load file or assembly '…/a.dll'` and "This is a bug in the compiler" plus a stack trace;
  - with an older `a.dll` on disk: the cycle compiles, first failing with a misleading `Unbound variable: fa at main.bjo:2`, then succeeding on the next build. The program then dies with `Stack overflow`.
  
  Docs/MODULES.org (~690) and Docs/Compiling.org (116) promise that a cycle is reported as a chain.
- Repro:
```
;; a.bjo
(import "b.bjo")
(: fa (-> int int))
(defun (fa x) x)
(export fa)
;; b.bjo
(import "a.bjo")
(: fb (-> int int))
(defun (fb x) x)
(export fb)
;; main.bjo
(import "a.bjo")
(defun (main args) (println (->str (fa 1))) 0)
```
  Also `self.bjo` containing `(import "self.bjo")`. For the silent variant, build main once with `a.bjo` lacking the import, then add the cycle (`fa` calls `fb`, `fb` calls `fa`) and build twice.
- Observed: ICE with a .NET stack trace. In the stale variant, a successful build and a stack overflow at run time. Expected: `Import Error: … imported from a module it is itself building. Import chain: a.bjo -> b.bjo -> a.bjo`.

### `--framework X` or a newer `--frameworks` file makes the shared standard library stale (rebuilt, then rebuilt again on the next plain build)
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Frameworks.fs:114-127 (`declaredFor` returns `declaredGlobally` for stdlib files), Frameworks.fs:161 (`declarationsChanged`), Pipeline.fs:1105-1109 and 1150-1155 (`declarationsWritten <= built`, `not (Frameworks.declarationsChanged bjoPath)`), Build.fs:173 (`framework` lines written into the stdlib's records).
- Description: NuGet inputs are explicitly exempted for the stdlib (`NuGetRefs.appliesTo`: "if a project's packages made it stale, building a project with packages and then one without would rebuild it each time"). Framework declarations are not:
  - every stdlib module compares the declarations file's timestamp, and compares its recorded `framework` lines against the global `--framework` set;
  - a project that declares ASP.NET, or any build with `--framework`, therefore rebuilds `prelude`, `eq`, `maths`, `effect` and `syntax-match`, and records the framework in their `.bjobuild`;
  - the next build of any other program rebuilds them all again.
  
  This writes into the shared installation, which fails if `lib` is read-only. It also invites races between projects building at the same time.
- Repro:
```
printf '(defun (main args) (println "x") 0)\n' > main.bjo
$C --framework Microsoft.AspNetCore.App main.bjo   # rebuilds 5 stdlib modules
$C main.bjo                                        # rebuilds all 5 again
mkdir pkg; printf '%s\tMicrosoft.AspNetCore.App\n' "$PWD/pkg" > fws.txt
$C --frameworks fws.txt main.bjo                   # rebuilds all 5 again (unrelated package)
```
- Observed: "Building imported module prelude.bjo … syntax-match.bjo" on each of the three runs. Expected: the stdlib stays current. It resolves nothing from the framework, so the declarations of a user package don't apply to it.

### Two files whose names differ only in `-` vs `_` (or `.`) get the same module key; the clash is missed when one is only linked transitively
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Naming.fs:218-222 (`moduleNameOfPath` folds `.` and `-` to `_`). Pipeline.fs `claimedKeys` (~2042) checks only modules in the import graph, not link-only `Deps`. Build.fs `moduleAssemblyPairs`/`dllReferences` key on assembly name.
- Description: `my-lib.bjo` and `my_lib.bjo` in one directory are both `BjoMod.coll_<hash>.my_lib`, with the same assembly name, namespace and class. If both are imported directly, the collision is reported. If one arrives only as a transitive link (`a.bjo` imports `my_lib.bjo`), the program is handed to C# with two references of one assembly name, and the build fails inside the generated code.
- Repro:
```
;; my-lib.bjo
(: f (-> int int)) (defun (f x) (+ x 1)) (export f)
;; my_lib.bjo
(: g (-> int int)) (defun (g x) (+ x 100)) (export g)
;; a.bjo
(import "my_lib.bjo") (: h (-> int int)) (defun (h x) (g x)) (export h)
;; main.bjo
(import "my-lib.bjo")
(import "a.bjo")
(defun (main args) (println (->str (f 1))) (println (->str (h 1))) 0)
```
- Observed: `main.bjo(3,47): error CS0103: The name 'f' does not exist in the current context`, then MSBuild "Build FAILED". Expected: the "are both the module …my_lib. Two files cannot share one module name" error, or distinct keys.

### A module file name containing an ASCII space (or other ASCII punctuation such as `,`) produces invalid C#; the MSBuild fallback then fails on an unquoted argument
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Naming.fs:53-66 (`escapeUnidentifiable` escapes only non-ASCII: `if part |> Seq.forall (fun c -> c < '\u0080') then part`), Naming.fs:232 (`moduleClassName`), Naming.fs:245 (`identSegment`). Build.fs MSBuild call `/p:AssemblyName=%s{assemblyName}` is unquoted.
- Description: `sanitizeIdent` maps a fixed set of ASCII operators, but passes space, `,`, `(`, `$`, `%` and others through untouched. A file `my lib.bjo` emits `using static BjoMod.….my lib_Module;` and `public static class my lib_Module`. The assembly name `BjoMod.….my lib` then splits the MSBuild command line ("MSB1008: Only one project can be specified"). The same applies to package subdirectories, via `identSegment`.
- Repro:
```
;; "my lib.bjo"
(: f (-> int int)) (defun (f x) (+ x 1)) (export f)
;; main.bjo
(import "my lib.bjo")
(defun (main args) (println (->str (f 1))) 0)
```
- Observed: a screen of CS1529/CS9229/CS1514 errors, then MSB1008 and `Import Error: could not build 'my lib.bjo'`. Expected: the module compiles (the name escaped like non-ASCII is), or a clear diagnostic about the file name.

### The same module reached through a symlinked directory gets a second identity; an importer then cannot see its types
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Naming.fs:258-298 (`moduleNamespace` hashes `Path.GetFullPath` of the directory, which does not resolve symlinks). Pipeline.fs `load` derives `Naming.moduleKeyOfPath absPath` from the path the `.dll` was reached by (`typeSpellingDecls`, ~1745) rather than from the key baked into its metadata.
- Description: `m.dll` built via `real/m.bjo` keys its types `BjoMod.real_<h1>.m__T`. An importer that reaches the same file as `link/m.bjo` computes key `BjoMod.link_<h2>.m`, so `bareTypeName` finds no prefix to strip and no `T` spelling is offered. `ensureLibrary` treats the existing `.dll` as current, so nothing re-keys it.
- Repro (`ln -s real link`):
```
;; real/m.bjo
(type (: T (Record (: v int))))
(: mk (-> int T)) (defun (mk x) (T (v x)))
(: get (-> T int)) (defun (get t) (record-ref t v))
(export T mk get)
;; real/a.bjo
(import "m.bjo") (: twice (-> T int)) (defun (twice t) (* 2 (get t))) (export twice)
;; main.bjo
(import "real/m.bjo")
(import "link/a.bjo")
(defun (main args) (println (->str (twice (mk 21)))) 0)
```
- Observed: `Type Error at a.bjo:2: there is no type named 'T'.` Expected: `42`. One file should be one module however its path is spelled; otherwise the key should be read from the `.dll`'s metadata, not re-derived from the path.

### Exporting a constrained function without exporting its trait is accepted, and the function can never be called from an importer
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Exports.fs:249-253 (`exportedTraits` only includes traits whose name or methods are exported). Exports.fs:994-1046 (the `withheld` leak check covers *types* named in published text, not traits named in `ConstraintsText`).
- Description: `show-it` is published with `(where (Sh 'a))` while `Sh` and its impls are withheld. Every call in an importer fails with a "no implementation" error, even though the impl exists in the dependency. The failure lands in the importer, which is exactly what the withheld-type check exists to prevent.
- Repro:
```
;; b.bjo
(def/trait (Sh %a)
  (: sh (-> %a string)))
(impl (Sh int)
  (defun (sh x) (string-append "int:" (->str x))))
(: show-it (-> %a string) (where (Sh %a)))
(defun (show-it x) (sh x))
(export show-it)
;; main.bjo
(import "b.bjo")
(defun (main args) (println (show-it 7)) 0)
```
- Observed: `Type Error at main.bjo:2: no implementation of trait 'Sh' for 'System.Int32', needed to call 'show-it'.` Expected: `int:7`. The trait should travel with the constraint, or `b.bjo` should get an Export Error naming `Sh`.

### `Docs` caches source text and tokens per path with no timestamp: a module rebuilt in the same process publishes corrupted docs, and the corruption persists
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Docs.fs:852-870 (`sources` dictionary keyed by file path only; `sourceOf`), used by `formText`/`definitionHead`/`publish` (Docs.fs:874-1026).
- Description: In a long-lived process (REPL, `--batch`), a second build of the same file slices the *new* ranges out of the *old* text and tokens. Clauses that don't line up are dropped, and the `(definition ...)` head comes out wrong. The bad doc is written into the `.dll`'s `BjolangDocs` attribute. The `.dll` is then current, so the bad docs stay until the source changes again.
- Repro: `m.bjo` = `(:doc f (summary "OLD summary text.") (arg x "An x.") (returns "One more."))` plus a defun `f` with export. Start `$C --repl`, enter `(import "m.bjo")`. Outside the REPL, rewrite `m.bjo` with two comment lines at the top and a new longer summary. Enter `(f 1)` (this triggers the rebuild), then quit. In a **fresh** `$C --repl`: `(import "m.bjo")` then `:show f`. `strings m.dll` shows `BjolangDocs … (:doc f (kind function) (signature "(-> int int)"))`.
- Observed: the fresh session shows `(f)` (no parameter) and no summary, arg or returns. Expected: `(f x)` and "NEW summary …".

### REPL: an expression entry followed by a `;` comment is a syntax error
- Severity: Medium
- Status: Confirmed (repro run)
- Location: Repl.fs:419-424 (`entrySource`: `$"(def %s{valueName} %s{text})"`).
- Description: The entry text is spliced inside a `(def …)` with the closing paren on the same line. A trailing line comment comments out that paren.
- Repro: `printf '(+ 1 2) ; hi\n(+ 3 4)\n' | $C --repl`
- Observed: `Syntax error at Bjo_Repl_1.bjo:1: the '(' opened here is never closed.` Expected: `3`. Put a newline before the closing paren.

### REPL: two pending signatures for one name make that name impossible to define for the rest of the session
- Severity: Low
- Status: Confirmed (repro run)
- Location: Repl.fs:540-546 and 677-685 (`Pending` only grows; a new `(: h …)` is appended beside the old one instead of replacing it, and a failed definition leaves both).
- Description: Every later definition of `h` gets every pending signature replayed into its source, plus its own if it writes one. So it always fails with "more than one type signature", and there is no way to clear the pending list.
- Repro: `(: h (-> int int))`, `(: h (-> int string))`, `(defun (h x) (->str x))`, then `(defun (h (: x int)) : string (->str x))`.
- Observed: `Type Error at Bjo_Repl_N.bjo:2: 'h' has more than one type signature` on every attempt, and `h` stays unbound. Expected: the newer signature replaces the pending one (as redefinition shadows), or the second signature is refused when typed.

### REPL: `absolutizeImports` rewrites every string literal equal to an import path, not only the import
- Severity: Low
- Status: Confirmed (repro run)
- Location: Repl.fs:282-284 (`acc.Replace($"\"%s{p}\"", $"\"%s{Path.GetFullPath p}\"")` over the whole entry text).
- Description: Plain text replacement over the entry. Any string literal in the same entry that happens to equal the import path is changed to an absolute path.
- Repro: `(import "m.bjo") (def s "m.bjo")`. The generated `Bjo_Repl_2.bjo` contains `(def s "/tmp/correctness/driver/repl1/m.bjo")`.
- Observed: `s` holds the absolute path. Expected: `"m.bjo"`.

### No lock around building an imported module; concurrent compilers may rebuild and overwrite the same `.dll`
- Severity: Low
- Status: Suspected (from reading); not reproduced in 32 parallel compiles
- Location: Pipeline.fs:1209 (`ensureLibrary`), Build.fs `compileDependencyInProcess`, Build.fs csc path `File.Copy(cscOutPath, targetPath, true)`, Build.fs `writeBuildRecord`.
- Description: There is no file lock or atomic rename. N compilers that find `lib.bjo` stale at once each rebuild it, and each overwrites `lib.dll`, `lib.pdb` and `lib.bjobuild` in place. A compiler can `Assembly.LoadFile` a `.dll` while another is mid-copy. A record's `written` ticks can describe another process's artefact, which disables early cutoff (harmless). Torn reads look possible with the MSBuild fallback path, which deletes then moves.
  - Builds are deterministic and `File.Copy` is quick, so 4 rounds of 8 concurrent compiles sharing a stale 150-function library gave no failure.
  - `BuildGraph` avoids the problem only within one graph build.
- Repro: 8 `mainN.bjo` all `(import "lib.bjo")`. Rewrite `lib.bjo`, then `for i in 1..8; do $C main$i.bjo & done; wait`, over 4 rounds.
- Observed: every build succeeded, and all 8 compilers rebuilt `lib.bjo` each round (redundant work). Expected: one build, the others wait on a lock.

### `sourceFacts` is keyed on the top-level file's timestamp only, so a nested include added in a long-lived process is missing from the build record
- Severity: Low
- Status: Suspected (from reading)
- Location: Pipeline.fs:1041-1061 (`sourceFacts` key `bjoPath, File.GetLastWriteTimeUtc(bjoPath).Ticks`), Build.fs:136-148 (`writeBuildRecord` → `Pipeline.sourceClosure`).
- Description: The comment argues an edited included file cannot give a wrong *staleness* answer, which is true: the included file itself is newer, so the module rebuilds. But if that edit adds a new `(include "deeper.bjo")`, the cached facts (top-level file unchanged) still hold the old closure. In the same process (REPL, `--batch`, a graph-build worker):
  - the rebuilt module's `.bjobuild` omits `source deeper.bjo`;
  - later staleness checks in that process don't watch `deeper.bjo`;
  - a driver relying on the record misses later edits to `deeper.bjo` until something else changes.
- Repro: not run. It needs a long-lived process that rebuilds the same module twice, with the include added between the builds.
- Observed/Expected: expected `source …/deeper.bjo` in the rewritten record; from the code, it will be missing.


---

## Runtime correctness audit — BjolangRuntime (ports, bytes, net, mutable, regex, random, chars, numbers, process, syntax)

Sandbox: `/tmp/correctness/runtime/`. Bjolang repros were compiled with
`BJOLANG_LIB=/tmp/correctness/runtime/lib dotnet /home/linis/Programmering/Bjolang/bin/compiler/Bjolang.dll X.bjo && dotnet X.exe`
(wrapped in `/tmp/correctness/runtime/run.sh`). C# repros are in `/tmp/correctness/runtime/cs/`.
That project references `bin/compiler/BjolangRuntime.dll` and siblings, with `AssemblyName=Tests` so the
runtime's `InternalsVisibleTo("Tests")` applies.

---

### A cancelled `read-line` / `read-all` on a text port throws away text it already read (partial line, or a whole line)
- Severity: High
- Status: Confirmed (repro run, both C# and Bjolang)
- Location: BjoPort.cs:349-386 (`ReadLineValueAsync`), BjoPort.cs:320-327 (`StepOverTerminator` + the `await FillAsync` after it), BjoPort.cs:414-433 (`ReadToEndAsync`)
- Description: The class says a fill that throws (including a cancelled one) "leaves the port exactly as it was", and the docs say the ambient token is what lets `with-deadline` stop a stalled read. That holds for one fill, but not for a line longer than what is buffered:
  - The multi-fill path copies the buffered text into a local `StringBuilder` and sets `pos = len` (`sb.Append(buf, pos, len - pos); pos = len;`) **before** `await FillAsync(cancel)`. If that await throws `OperationCanceledException`, the characters are only in the local `sb`, which is dropped. They are gone from the port.
  - A complete line whose `\r` is the last buffered character is worse. `line` is built, `StepOverTerminator` moves `pos` past the `\r`, then `await FillAsync(cancel)` runs to look for a `\n`. If that is cancelled the whole line is lost.
  - `ReadToEndAsync` empties the buffer (`pos = len = 0`) and gathers into a local `sb` across awaits. A cancellation drops everything gathered so far, on purpose ("a cancelled read must not leave it looking like content"). The content is lost with it.
  The obvious pattern of a timeout, then retrying the read (`with-deadline` around `read-line` on a socket, pipe or text-over-bytes port), then returns a corrupted line. Nothing reports that data was lost.
- Repro (Bjolang, text port over a byte pipe):
```
(import (std prelude))
(import (std ports))

(defbjo (main args)
  (match (byte-pipe)
    ((Tuple bin bout)
     (def tin (byte->text-input-port bin utf8))
     (def tout (byte->text-output-port bout utf8))
     (write-string tout "abc")          ; half a line
     (flush-port tout)
     (def first
       (try
         (with-deadline 200 (read-line tin))
         #:catch (System.Exception)))
     (match first
       ((Ok l) (println (str "first: " l)))
       ((Err e) (println "first read-line hit the deadline")))
     (write-string tout "def\n")
     (flush-port tout)
     (println (str "second: [" (read-line tin) "]  (expected [abcdef])"))))
  0)
```
  C# repro (`cs/`, a `TextReader` fed by a channel, cancellation through a `CancellationTokenSource(200)`). It covers all three paths: `"abc"` then `"def\n"`; `"line1\r"` then `"line2\n"`; and `ReadToEndAsync` on `"hello "` then `"world"`.
- Observed: Bjolang prints `first read-line hit the deadline` then `second: [def]`. C#: `read-line #2: [def]` (expected `abcdef`), `next read-line: [line2]` (expected `line1`, so a whole line was lost), and `read-all again: [world]` (expected `hello world`).
- Expected: A cancelled read consumes nothing, as `BjoByteInputPort` already guarantees one layer down. The partial text should stay in the port, for example by not advancing `pos` / not moving data into a local builder until the line is complete, or by keeping a pending-prefix field.

### `with-deadline` (the ambient cancel token) does not interrupt a read on standard input or on a FIFO/tty file port
- Severity: Medium
- Status: Confirmed (repro run)
- Location: BjolangRuntime.cs:1555 (`StdIn = BjoPort.Wrap(Console.In)`), BjoPort.cs:113-125 (`FillAsync` → `inner.ReadAsync(buf, cancel)`)
- Description: `BjoPort.Dispose`'s doc says a reader parked on the port "is woken by the ambient cancellation token instead — which is what makes `with-deadline` work on a stalled read". The prelude docs say stdin's reads "suspend inside a bjoroutine". But for stdin, `inner` is `Console.In`, a `SyncTextReader`. Its `ReadAsync(Memory, CancellationToken)` checks the token once and then does a blocking `Read` on the calling pool thread. For a file port on a FIFO (or another non-seekable file), `FileStream.ReadAsync` hands a blocking read to the thread pool, and the token is not observed once that read has started. So the fiber holds a thread and the deadline does nothing until data or EOF arrives. In both runs below the body finished normally (`Ok`) 3 s later. The deadline was 300 ms. (That `with-deadline` then returns the late value normally is Scope's business. I only note it.)
- Repro:
```
(import (std prelude))

(defbjo (main args)
  (def in (parameter-ref current-input-port))
  (def first
    (try
      (with-deadline 300
        (read-line/opt in))
      #:catch (System.Exception)))
  (match first
    ((Ok l) (println (str "with-deadline 300 returned normally: " (->str l))))
    ((Err e) (println "deadline raised")))
  0)
```
  `time (sleep 3 | dotnet dl3.exe)`. The same happens with `(open-input-file "ff")` on a `mkfifo ff` fed by `sleep 3 > ff` (`port/dl4.bjo`).
- Observed: `with-deadline 300 returned normally: None` after 3.0 s, for both stdin and the FIFO file port.
- Expected: `deadline raised` after about 300 ms. Otherwise the docs should say that stdin and non-seekable files cannot be bounded by a deadline.

### Text ports are not safe for two fibers: concurrent `read-line` on one port crashes or loses lines
- Severity: Medium
- Status: Confirmed (repro run, Bjolang and C#)
- Location: BjoPort.cs (whole class: `pos`/`len`/`buf`/`ended` are unsynchronised; `FillAsync` can run twice at once on `inner`)
- Description: `BjoPort` has no lock and no single in-flight fill, unlike `BjoByteInputPort`, which is designed for "two fibers, one port". Fibers run on pool threads, so two fibers draining the same file port (or the global stdin port) race on `pos`/`len` and issue concurrent `ReadAsync`s on the inner `StreamReader`. The result is out-of-range exceptions from inside the port, malformed lines and lost lines. Nothing documents text ports as single-fiber. The design notes only say `BjoWriter` is not thread-safe.
- Repro:
```
(import (std prelude))

(: drain (-bjo-> TextInputPort int))
(defbjo (drain p)
  (let go ((n 0))
    (match (read-line/opt p)
      ((Some l) (go (+ n 1)))
      (None n))))

(defbjo (main args)
  (match (open-input-file "/tmp/correctness/runtime/port/big.txt")   ; 300000 lines "line-N"
    ((Ok p)
     (with-cancel (cancel)
       (def a (bjo (drain p)))
       (def b (bjo (drain p)))
       (match (Tuple (sync (promise-join a)) (sync (promise-join b)))
         ((Tuple (Ok x) (Ok y)) (println (str "total " (->str (+ x y)))))
         ((Tuple (Err e) _) (println (str "fiber A failed: " (.-Message e))))
         ((Tuple _ (Err e)) (println (str "fiber B failed: " (.-Message e)))))))
    ((Err e) (println "open failed")))
  0)
```
- Observed: Every run, one fiber fails. Messages seen: `Index and count must refer to a location within the buffer. (Parameter 'bytes')` and `Specified argument was out of the range of valid values.` A C# variant with 4 tasks calling `ReadLineValueAsync` on a `BjoPort(StringReader)` of 200000 lines read 198448 lines, 1 of them malformed, and one task died with `ArgumentOutOfRangeException: length ('-1')`.
- Expected: Either serialise reads (as the byte port does) so every line is handed out once and intact, or document that one text port belongs to one fiber.

### `mutablevec-sort-by!` corrupts the vector (duplicates and lost elements) when the comparison raises
- Severity: Medium
- Status: Confirmed (repro run)
- Location: BjoMutable.cs:131-183 (`MutableVecModule.StableSort`)
- Description: The insertion-sort phase lifts `x = items[i]` into a local and shifts elements right before writing `x` back. The merge phases write into `items` whenever `dst` is the list's own span (every other pass). If the user's `compare` throws partway (the callback is arbitrary Bjolang code), the list is left holding duplicates, and the lifted element exists nowhere. `List<T>.Sort` with a throwing comparer leaves a permutation. This sort leaves a list that no longer contains the same elements. Catching the exception and going on with the vector is ordinary, and the data is then silently wrong.
- Repro:
```
(import (std prelude))
(import (std mutable vec))
(import/class (InvalidOp (: System.InvalidOperationException (-> string InvalidOp))))

(defun (main args)
  (def xs (list->mutablevec (list 5 4 3 2 1)))
  (def calls (make-box 0))
  (def r
    (try
      (mutablevec-sort-by! xs
        (fun (a b)
          (box-set! calls (+ (box-ref calls) 1))
          (when (= (box-ref calls) 3) (raise (cast Exception (InvalidOp. "comparator failed"))))
          (compare a b)))
      #:catch (System.InvalidOperationException)))
  (println (str "after failed sort: " (->str (mutablevec->list xs))))
  0)
```
- Observed: `after failed sort: (4 5 5 2 1)` (3 is gone and 5 is there twice). In C#, 40 elements with the comparer throwing on call 150 ended with 39 distinct values.
- Expected: Some permutation of `(5 4 3 2 1)`. For example, sort into the scratch buffer and copy back only on success, or write `x` back in a `finally`.

### `structural-equals` / `structural-hash` are wrong for strings (always unequal) and arrays (always equal)
- Severity: Medium
- Status: Confirmed (repro run)
- Location: BjolangRuntime.cs:209-237 (`structurallyOpaque`, `structuralsubequals`, `structuralsubhash`)
- Description: The doc says "A primitive, enum or string compares as .NET does". But `structurallyOpaque` tests `typeof(string)`, and a Bjolang `string` is `BjoString.Utf8String`, a struct. So two equal strings are compared field by field: their private byte arrays are compared by **reference**, and two equal strings built separately come out unequal. The hashes differ too. For arrays (`T[]`), and any type with no instance fields, the field loop is empty, so `structural-equals` answers `#t` for any two arrays of the same element type and `structural-hash` is a constant. When a string is a field inside a record it works, because fields go through `object.Equals`. The bug is at the top level, i.e. `structural-equals` called directly on a string or an array.
- Repro:
```
(import (std prelude))

(defun (main args)
  (def a "ab")
  (def b2 (str "a" "b"))
  (println (str "(= a b2): " (->str (= a b2))))
  (println (str "structural-equals: " (->str (structural-equals a b2))))
  (println (str "structural-hash equal?: " (->str (= (structural-hash a) (structural-hash b2)))))
  (def x (make-array 1)) (array-set! x 0 1)
  (def y (make-array 1)) (array-set! y 0 2)
  (println (str "structural-equals #[1] #[2]: " (->str (structural-equals x y))))
  0)
```
- Observed: `(= a b2): True`, `structural-equals: False`, `structural-hash equal?: False`, `structural-equals #[1] #[2]: True`.
- Expected: `True`, `True`, `True`, `False`. Treat `Utf8String` (and other types with value semantics of their own) as opaque, and compare arrays element by element or refuse them.

### One transient `accept` failure (e.g. EMFILE) kills a TCP listener for good, and `accept-evt` then commits `Err` immediately forever
- Severity: Medium
- Status: Confirmed (repro run)
- Location: BjoNet.cs:224-248 (`Pump`: `_error ??= failure`), BjoNet.cs:211 (`EnsureAccept` refuses once `_error` is set), BjoNet.cs:347-350 / 500-502 (`BeginLocked` / `TryAcceptNow` answer the sticky error)
- Description: An accept error is made sticky "as a byte port's is". But for a listening socket the usual errors are per-connection or resource-temporary: `EMFILE`/`ENFILE` when a busy server runs out of descriptors, `ECONNABORTED`, `ENOBUFS`. After one of them, `EnsureAccept` never starts another accept, and every `accept-evt` sync commits at once with the same `Err` through the `INowable` fast path. The server loop shown in `net.bjo`'s own docs (`((Some (Err e)) (log ...) (go))`) then spins at 100% CPU, logging the same error, and never accepts again even after descriptors are freed.
- Repro: `net/emfile.bjo` together with `net/client.py`. The server accepts and holds connections until it sees 3 accept errors, closes everything it holds, waits 1.5 s, then tries once more while the client still has connections queued in the backlog. Run with `( ulimit -n 250; dotnet emfile.exe | tee fifo2 ) & python3 client.py < fifo2`. The core of the server:
```
(match (sync (choose (wrap (accept-evt l) Some) (wrap (timeout 5000) (fun (u) None))))
  ((Some (Ok c)) (box-set! held (cons c (box-ref held))) (go (+ i 1) errs))
  ((Some (Err e)) (println (str "accept error: " (.-Message e))) (go i (+ errs 1)))
  (None (println "timeout") (go i 3)))
;; ... after errs >= 3: close every held connection, (sync (timeout 1500)), accept again
```
- Observed: `accept error: Too many open files in system` ×3, `accepted 173 then 3 errors; releasing held connections`, then `after recovery: accept STILL fails: Too many open files in system`.
- Expected: After the descriptors are released, the next accept succeeds. Errors from `accept(2)` that are transient or per-connection should be reported once (or retried) and should not become the listener's permanent state. Only failures of the listening socket itself (for example `EBADF` or `EINVAL`) should be sticky.

### `BjoPipe` (the in-memory pipe between Bjolang stages in `std/run`) silently drops a leading U+FEFF
- Severity: Low
- Status: Confirmed (repro run, C#)
- Location: BjoProcess.cs:43-57 (`BjoPipe()` → `new StreamReader(pipe.Reader.AsStream(), encoding)`)
- Description: The writer deliberately uses a UTF-8 encoding without a preamble, so that nothing is added. But the `StreamReader(Stream, Encoding)` constructor defaults to `detectEncodingFromByteOrderMarks: true`, and it also strips a preamble matching `encoding`. So if a stage's output starts with the character U+FEFF, the next stage never sees it. Text going through a pipeline stage should arrive unchanged.
- Repro:
```csharp
var p = BjoPipe.Create();
var t = Task.Run(() => p.Reader.ReadToEnd());
p.Writer.Write("\uFEFFhello");
p.Writer.Dispose();
var s = t.Result;
Console.WriteLine($"pipe read back {s.Length} chars, first U+{(int)s[0]:X4}");
```
- Observed: `pipe read back 5 chars, first U+0068`
- Expected: 6 chars, first U+FEFF. Use `new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false)`. Note that `StreamReader` also skips a preamble matching the encoding even with detection off. The encoding has no preamble, so it is safe here.

---

### Checked and found correct (no finding)

These were tested with repros (`cs/Program.cs` variants and the Bjolang files under `rx/`, `bp/`, `num/`, `cult/`):

- **BjoPort line splitting.** A fuzz of 20000 random inputs over `a b \r \n \r\n 😀 é`, buffer sizes 2–7, inner reads of 1–3 chars, sync and async, with `Eof()` calls mixed in. Every result matched `StringReader.ReadLine`, including `\r\n` split across fills and a final `\r`. Surrogate pairs split across fills come back as one `read-char`. `ReadToEnd`/`ReadToEndAsync` after a `Peek`/`ReadLine` keep the buffered text.
- **BjoWriter.** A fuzz of 5000 sequences of `Write`/`WriteValueAsync`/`WriteLine`/`WriteLineValueAsync`/`Write(char)`/flush, buffer sizes 1–5. Output always matched.
- **Byte ports.** Peek with a skip past one bufferful grows the buffer. `ReadInto` after a peek returns the right bytes. `TryReadNow` at end of input gives `Ok None`. `limited` stops exactly at the bound and leaves the underlying port right after it. Multi-byte UTF-8 fed one byte at a time through `byte->text-input-port` decodes correctly. A surrogate pair written through `byte->text-output-port` with a 3-byte port buffer encodes correctly (`F0-9F-98-80`). `shutdown!` refuses later writes. Double dispose is harmless. A CML stress over a byte pipe (20000 lines, `choose` of `read-bytes-evt 7` against `(timeout 0)`, 4875 lost rounds) delivered all 108890 bytes.
- **Numbers (`BjoNum`).** Invariant under `LC_ALL=sv_SE.UTF-8`, and the runtime also pins the invariant culture at startup (Scope.cs:1486). `-0.0 → "-0"`, `NaN`, `-Infinity`, `0.30000000000000004`, `int.MinValue`/`long.MinValue`, and `"2147483648"` → `OverflowException`. `quotient`/`remainder`/`modulo` with negative operands follow R7RS.
- **Random.** `random-int` is inclusive with no bias visible over 60000 draws. Negative ranges, `int.MinValue..int.MaxValue` and `max-1..max` all work. `random-chance?` at 0 and 1 is exact. All 120 permutations of 5 elements are uniform. Seeds are reproducible.
- **Regex.** Empty matches near multi-byte characters and emoji, `a*` replace (`-b--c-`, as Python gives), `:bos` with repeated searches (one match), `:eos` replace, whole match of `(or "a" "ab")` on `"ab"`, `"a"` not matching `"a\n"`, and match positions and replace/split on a `substring/slice` are all correct.
- **Characters.** `char-titlecase` handles Ǆ/ǆ/ǳ → ǅ/ǅ/ǲ. `char-upcase` works on astral characters (U+10428 → U+10400). `digit-value` works for Arabic-Indic and mathematical digits. `int->char -1` is rejected. (`char-foldcase` against Unicode simple folding: known, in Todo.org.)

### Not reported (documented behaviour)
- `panic!` loses buffered output of open file ports, because `Environment.Exit` runs no cleanup. I confirmed this (`port/pn.bjo` leaves a 0-byte file), but `panic!`'s doc says "No finally and no scope's cleanup runs".
- `read-bytes` sticky errors, refills that are never cancelled, and `close-listener!` leaving parked accepts uncommitted are all documented design choices.


---

## Concurrency / CML / Scope correctness findings

Sandbox: `/tmp/correctness/concurrency/`. Every Bjolang repro is compiled with
`/tmp/correctness/concurrency/bjoc.sh file.bjo` (that is just
`BJOLANG_LIB=/tmp/correctness/concurrency/lib dotnet /home/linis/Programmering/Bjolang/bin/compiler/Bjolang.dll file.bjo`)
and run with `dotnet file.exe`. The C# stress harness is
`/tmp/correctness/concurrency/cs/` (`stress.csproj` + `Program.cs`, references
`bin/compiler/BjolangRuntime.dll`), and you run it as `dotnet outd/stress.dll <mode> ...`.
`/tmp/correctness/concurrency/mirror/` is a C# copy of the generated code that calls
`BjolangRuntime.*` directly.

None of these is listed in Todo.org. Todo.org's concurrency entries are only about performance.

---

### 1. Lost rendezvous: a channel match skips a partner that is transiently Claimed, so two `choose`s that could pair both park forever
- Severity: High
- Status: Confirmed. Bjolang program: 20/20 runs hung. C# harness: 7/2000 rounds (`sym`). `pair` lost a round in 9 of 10 runs of 20000 rounds with 2-3 channels, and 0 of 5 with 1 channel, where a single channel lock serialises the two sides.
- Location: `Cml/Channel.cs:630-650` (`PublishSend`), `Cml/Channel.cs:783-803` (`PublishReceive`). The pattern is `state.TryClaim(); if (!curr.TrySync()) { state.ResetClaim(); if (!curr.IsSynchronized) { prev = curr; curr = next; } }`.
- Description: When `curr.TrySync()` fails because the partner's `SyncState` is in `C` (that thread is busy pairing in some *other* channel), the code resets its own claim, skips the partner and goes on to park. `C` is transient, so the partner usually ends up back in `W`. The partner's op is already parked in this channel, and it does not rescan this channel. In Reppy's protocol the right move is to retry or spin until the partner leaves `C`. Here the match is simply dropped. Interleaving with p = `choose(send a, recv b)` and q = `choose(send b, recv a)`:
  1. p parks `send a` in channel a. q parks `send b` in channel b.
  2. p publishes `recv b`: it claims p (C) and tries `q.TrySync()`. At the same moment q publishes `recv a`: it claims q (C) and tries `p.TrySync()`.
  3. Both TrySyncs fail, because each sees the other in C. Both reset and skip. Both park their second op.
  4. Result: a = {p send, q recv} and b = {q send, p recv}. Every op is W and every pair can match, but nobody will ever walk them again.

  Compiled Bjolang programs hit this **every time** on the first such rendezvous. The generated entry point probes `BjolangRuntime/bin/Release/net10.0/BjolangRuntime.dll`, which is the non-ReadyToRun build (same commit as `bin/compiler`'s R2R build, checked via metadata). There, the first call of `TrySync` while holding `C` is JIT-compiled, which widens the claim window to milliseconds. Once warm it is the rare race shown by the C# numbers.
- Repro (`lost/one2.bjo`):
```
(import (std prelude))
(: side (-> (Chan int) (Chan int) int int))
(defbjo (side mine theirs id)
  (println (str "side " (->str id) " syncing"))
  (def v (sync (choose (wrap (chan-send mine id) (fun (u) 0))
                       (wrap (chan-recv theirs) (fun (x) 1)))))
  (println (str "side " (->str id) " done " (->str v)))
  v)
(defbjo (main args)
  (def a (make-chan))
  (def b (make-chan))
  (def p (bjo (side a b 1)))
  (def q (bjo (side b a 2)))
  (println "main: joining")
  (def rp (sync (promise-join p)))
  (def rq (sync (promise-join q)))
  (println "all done")
  0)
```
  Run: `timeout 5 dotnet one2.exe`. Starting q 200 ms after p (`lost/seq.bjo`) works every time. In C#: `dotnet cs/outd/stress.dll sym 2000` and `dotnet cs/outd/stress.dll pair 20000 2`. Loading `mirror/outr` (the mirror built against the bin/Release dll) dumps all four ops parked in state W.
- Observed: both fibers print "syncing" and never "done". The process hangs (exit 124). Expected: the pair rendezvous on a or on b.

### 2. `wrap`'s function runs on whichever thread commits, with that thread's dynamic environment; if it throws, the exception goes to the partner fiber and the syncing fiber hangs
- Severity: High
- Status: Confirmed (deterministic by construction; each of the three repros was run and gave the result below)
- Location: `Cml/Event.cs:386` (`WrapEvent.Publish`: `value => onSync(_mapper(value))`). The language-level doc is in `Concurrency.cs` (`wrap`: "f runs *after* the commit, on the syncing fiber, so it is safe to do real work in it") and in `prelude-concurrency.bjodoc`.
- Description: The mapper runs inside the event continuation. That is the partner's `Scheduler.Dispatch` inside its `PublishSend`/`TryDirectSend`, a timer-queue thread for `timeout`, or a pool thread for a promise. It does not run on the syncing fiber's stack. As a result:
  - (a) It sees the wrong `parameterize` bindings, effect handlers, `current-cancel` and scope. In the timeout case it sees none of them, because the timer thread has no `FiberContext`.
  - (b) An exception it throws unwinds into the *committer*. With a channel, the sender's `sync` raises the receiver's exception and the sending fiber dies. The receiver's `onSync` is never called, so the receiver hangs forever and the scope never closes.

  design.md lists "throwing inside a Wrap mapper" as wrong, but the Bjolang surface hands arbitrary user lambdas to `wrap`, and its docs promise the opposite.
- Repro (a), `wrap/wrapctx.bjo`:
```
(import (std prelude))
(: who (Param string))
(def who (make-parameter "root"))
(: sender (-> (Chan int) void))
(defbjo (sender ch)
  (parameterize ((who "SENDER"))
    (sync (timeout 100))
    (sync (chan-send ch 1))))
(defbjo (main args)
  (def ch (make-chan))
  (def s (bjo (sender ch)))
  (def seen (parameterize ((who "RECEIVER"))
              (sync (wrap (chan-recv ch) (fun (v) (parameter-ref who))))))
  (println (string-append "wrap mapper saw who=" seen))
  0)
```
  This prints `wrap mapper saw who=SENDER`. `wrap/wraptimer.bjo` is a single fiber doing `(parameterize ((who "RECEIVER")) (sync (wrap (timeout 50) (fun (u) (parameter-ref who)))))` and prints `who=root`.
  Repro (b), `wrap/wrapthrow.bjo`:
```
(import (std prelude))
(import/class (Failure (: System.Exception (-> string Failure))))
(: boom (-> int int))
(defun (boom v) (raise (Failure. "boom in wrap mapper")))
(: sender (-> (Chan int) void))
(defbjo (sender ch)
  (sync (timeout 100))
  (println "sender: about to send")
  (sync (chan-send ch 1))
  (println "sender: send returned normally"))
(defbjo (main args)
  (def ch (make-chan))
  (def s (bjo (sender ch)))
  (println "receiver: syncing on wrap with throwing mapper")
  (def r (try (sync (wrap (chan-recv ch) boom)) #:catch (System.Exception)))
  (match r
    ((Ok v) (println "receiver: got value"))
    ((Err e) (println "receiver: caught the mapper's exception (expected)")))
  0)
```
- Observed: (a) prints `SENDER` / `root`. (b) prints "sender: about to send" and then hangs (exit 124 under `timeout 10`). The receiver's `try` never sees the exception. Expected: (a) `RECEIVER`. (b) the receiver catches "boom" and the sender's send returns normally.

### 3. Event id 0 is not reserved: the root id collides with the first minted id, so the cancellation token's branch lands inside a top-level `with-nack`'s interval and the nack never fires. A bare `(sync (spawn-evt ...))` then hangs its scope forever, and a bare `(sync (task->event ...))` never cancels the .NET call
- Severity: High
- Status: Confirmed (deterministic, 3/3)
- Location: `Cml/SyncState.cs:62,65,85` (`RootEventId = 0`, `_eventIdCounter = RootEventId`, `NextEventId() => Increment - 1` returns 0 first), combined with `Concurrency.cs:225` (`int tokenBranch = state.NextEventId();` in `SyncOp.Chosen`) and `Cml/Event.cs:432-458` (`WithNackEvent.Publish`).
- Description: design.md B10 says the root id "is now 0 and reserved", but the first `NextEventId()` also returns 0. Take a `sync` of an event that is not a direct channel op and mints no ids itself, such as `with-nack`, `task->event` (Guard → Wrap → WithNack), `spawn-evt` or `timeout`. It is published with the root id 0, and its nack interval is [0, 1). `Chosen` then reserves the token branch with `NextEventId()`, which is **0**. When the scope is cancelled, the token commits `MarkSynchronized(0)`. 0 is inside [0, 1), so the with-nack is judged the winner and its nack is skipped, even though the branch lost. Wrapping the same event in `(choose ev (never))` mints ids from 1 and works.

  Consequences:
  - The child of `(sync (spawn-evt (f)))` runs under a token only the nack fires, so it is never cancelled and the enclosing scope's close waits for it forever.
  - `(sync (task->event ...))`, the case `TaskEvent`'s comment walks through as steps 1-5, never cancels the .NET call's token.
  - A cancelled bare `(sync (timeout ms))` keeps its timer until it fires.
- Repro (`nackid/spawnevt.bjo`):
```
(import (std prelude))
(: forever (-bjo-> (Chan int) int))
(defbjo (forever silent) (sync (chan-recv silent)))
(: waiter (-bjo-> (Chan int) bool void))
(defbjo (waiter silent in-choose)
  (def ev (spawn-evt (forever silent)))
  (ignore (sync (if in-choose (choose ev (never)) ev))))
(: scoped (-bjo-> (Chan int) bool (Chan int) void))
(defbjo (scoped silent in-choose done)
  (with-cancel (cancel)
    (spawn (waiter silent in-choose))
    (sync (timeout 50))
    (ignore (cancel (Requested "stop"))))
  (sync (chan-send done 1)))
(: trial (-bjo-> bool string))
(defbjo (trial in-choose)
  (def silent (make-chan))
  (def done (make-chan))
  (spawn/detached (scoped silent in-choose done))
  (sync (choose (wrap (chan-recv done) (fun (x) "scope closed"))
                (wrap (timeout 2000) (fun (u) "scope HUNG: the spawn-evt child was never cancelled")))))
(defbjo (main args)
  (println (str "choose of spawn-evt and never: " (trial #t)))
  (println (str "bare spawn-evt:                " (trial #f)))
  0)
```
  `nackid/nackid.bjo` shows the same thing for plain `with-nack`: `(sync (with-nack ...)) cancelled: nack NEVER fired`, against `nack fired` for the choose form. `mirror/Program.cs` shows it for `TaskEvent`: `(sync (task->event ..)): the .NET call's token cancelled = False`, and `True` inside a choose.
- Observed: `bare spawn-evt: scope HUNG ...`. Expected: `scope closed`, as in the choose case. A fix is to start `_eventIdCounter` at `RootEventId + 1`.

### 4. A `guard` thunk or `with-nack` generator that throws mid-publish leaves the branches already published alive, so a later partner commits against a dead sync and the message is lost
- Severity: Medium
- Status: Confirmed (deterministic, 1/1 run, logic is deterministic)
- Location: `Cml/Event.cs:403` (`GuardEvent.Publish`: `_generator()`), `Cml/Event.cs:441` (`WithNackEvent.Publish`), and the publish loops in `ChooseEvent`/`PairChooseEvent`. No cleanup on exception.
- Description: In `(choose (chan-recv ch) (guard thunk))`, the receive is parked under the block's `SyncState` (W) before the guard runs. If the thunk raises, the exception goes up through `Cml.Sync`/`RentPublished` into the fiber, which catches it and moves on. Nothing ever moves the state to S, so the parked `GetOp` stays live. The next sender on `ch` `TrySync`s it and returns successfully. The value goes to an `EventAwaiter` nobody will read. The same happens with a throwing `with-nack` generator, where the nack node also stays registered.
- Repro (`guard/guardthrow.bjo`):
```
(import (std prelude))
(import/class (Failure (: System.Exception (-> string Failure))))
(: bad-event (-> (Event int)))
(defun (bad-event) (raise (Failure. "guard failed")))
(: sender (-> (Chan int) void))
(defbjo (sender ch)
  (sync (timeout 200))
  (sync (chan-send ch 42))
  (println "sender: (sync (chan-send ch 42)) returned, so 42 was delivered"))
(defbjo (main args)
  (def ch (make-chan))
  (spawn (sender ch))
  (def r (try (sync (choose (chan-recv ch) (guard bad-event))) #:catch (System.Exception)))
  (match r
    ((Ok v) (println "receiver: got a value from the failed sync?"))
    ((Err e) (println "receiver: the sync raised the guard's exception, as expected")))
  (def got (sync (choose (wrap (chan-recv ch) (fun (v) (str "receiver: got " (->str v))))
                         (wrap (timeout 1000) (fun (u) "receiver: TIMED OUT, the 42 is gone")))))
  (println got)
  0)
```
- Observed: the sender's send returns, and the receiver prints `TIMED OUT, the 42 is gone`. Expected: the failed sync withdraws its branches (for example by driving the state to S in a catch), and the second receive gets 42.

### 5. A `with-nack` nack can fire even though its branch won (TrySync sets S before `WinningEventId` is written)
- Severity: Medium (rare; it cancels the winner's resources: `task->event`'s CTS, a `spawn-evt` child, a user nack)
- Status: Confirmed with the window widened by legal API use, 10/10 runs. With the natural window it was 0/2,000,000 in a tight loop and 0/20,000 on one CPU.
- Location: `Cml/Event.cs:450-458` (`WithNackEvent.Publish`, post-publish check), together with `Cml/SyncState.cs:97` (`TrySync`: W→S without a winner) and `Cml/SyncState.cs:142` (`MarkSynchronized` writes `_winningEventId` later). The partner's gap is `Channel.cs` `PublishSend`/`PublishReceive`/`TryDirect*`: the TrySync happens under the lock, and `getState.MarkSynchronized` runs after the lock is released and after the partner's *own* `MarkSynchronized`, which fires the partner's nacks inline.
- Description: After publishing its subtree, `WithNackEvent` checks `if (IsSynchronized) { winner = WinningEventId; if (winner < i0 || i1 <= winner) fire }`. A partner that matched one of this subtree's ops has already moved the state to S with `TrySync`, but has not yet called `MarkSynchronized`, so `WinningEventId` is still -1. -1 < i0, so the nack fires for the *winning* branch. Interleaving:
  1. Thread A: the with-nack's receive parks.
  2. Thread B: `TrySync(A)` sets S, B releases the channel lock, and B runs its own `MarkSynchronized` (with its own nack list).
  3. Thread A: finishes publishing the rest of the subtree, sees S, reads -1, and fires its nack.
  4. Thread B: `A.MarkSynchronized(winner)`.
- Repro: `dotnet cs/outd/stress.dll nack2 10`. A syncs `with-nack(n => choose(ch, guard(sleep until B is in its commit; never)))`. B syncs `choose(with-nack(n2 => 300000 waiters on n2; never), ch.Send(42))`, which makes B's own nack firing take milliseconds. The narrow-window version is `dotnet cs/outd/stress.dll nack 2000000`.
- Observed: `iter i: received=42 nackFired=1` for 10 of 10 iterations. Expected: the nack never fires when the branch delivered 42. A fix: have `WithNackEvent` (and `RegisterNack`) wait or spin until `WinningEventId` is published once S is seen, or write the winner before the S transition.

### 6. `inbox-closed` can win while an item is still in the inbox, so the item is lost (deterministic when `inbox-closed` is offered before `inbox-recv`)
- Severity: Medium (silent lost message on reasonable use)
- Status: Confirmed (3/3, deterministic)
- Location: `Cml/Inbox.cs:389-420` (`PublishReceive`): it calls `Settle()` (and so `SignalClosedIfDrained`) **after** dequeuing the item and **before** `state.TryCommit`. The rollback is `GiveBack` (line 419).
- Description: `PublishReceive` takes the last item out and then calls `Settle()` before committing. `SignalClosedIfDrained` sees a closed, empty inbox and signals the closed `Gate`. That commits any parked `inbox-closed` waiter, including one belonging to *this same sync* if `inbox-closed` was published first. Our own `TryCommit` then fails and the item is put back with `GiveBack`. But the consumer has already been told "closed and drained", so it leaves its loop and the item is stranded. With two consumers the same window exists in the documented order (recv first): consumer 1 takes the last item, consumer 2 sees `inbox-closed`, then consumer 1 loses its choose to another branch and gives the item back (suspected, not run).
- Repro (`inbox/closedfirst.bjo`):
```
(import (std prelude))
(import (std inbox))
(: drain (-bjo-> (Inbox int) string))
(defbjo (drain ib)
  (let go ((acc ""))
    (match (sync (choose (wrap (inbox-closed ib) (fun (e) (Err e)))
                         (wrap (inbox-recv ib) (fun (x) (Ok x)))))
      ((Ok x) (go (str acc (->str x) " ")))
      ((Err e) acc))))
(defbjo (main args)
  (def ib (make-inbox 8 DropNewest))
  (ignore (inbox-post! ib 1))
  (ignore (inbox-post! ib 2))
  (ignore (inbox-post! ib 3))
  (inbox-close! ib)
  (println (str "received: " (drain ib) "(expected 1 2 3)"))
  0)
```
- Observed: `received: 1 2 (expected 1 2 3)`. Expected: `1 2 3`, since `inbox-closed` should only fire once nothing is left to receive. A fix: commit before `Settle()`, or don't signal closed while a dequeued item is uncommitted.

### 7. `inbox-closed` leaks one waiter per sync in the documented receive loop
- Severity: Medium (unbounded memory growth on a long-lived inbox)
- Status: Confirmed (100000/100000)
- Location: `Cml/Inbox.cs:971-990` (`Gate<T>.Publish`: `_waiters = new Node(...)`). Nodes are only removed in `Signal`.
- Description: The loop documented in `std/inbox.bjo` and `TestFiles/228_inbox.bjo` is `(sync (choose (wrap (inbox-recv ib) ...) (wrap (inbox-closed ib) ...)))`. Every iteration where the receive wins leaves a `Node` on the closed gate, holding the `SyncState`, the `onSync` closure and the awaiter. Nothing prunes nodes whose state is already synchronized: `Promise` has `IsAbandoned` and an amortised prune, and `Gate` has no equivalent. A server inbox that is never closed therefore grows by one node per message, forever.
- Repro: `dotnet cs/outd/stress.dll gate 100000`. It syncs `choose(Receive, Wrap(Closed))` 100000 times on an inbox fed by `Post`, then counts `Gate._waiters` by reflection.
- Observed: `100000 items received, 100000 dead waiters on (inbox-closed ib)`. Expected: a bounded number, for example by pruning nodes with `State.IsSynchronized` on publish, amortised as `Promise` does.

### 8. Losing `inbox-recv` branches accumulate on an idle inbox (the B7 leak, not fixed for inboxes)
- Severity: Medium (leak)
- Status: Confirmed (100000/100000)
- Location: `Cml/Inbox.cs:404` (`PublishReceive` → `ParkReceiverLocked`) and `TakeReceiverLocked` (line ~658). Dead `RecvNode`s are only dropped when a post or hand-over walks the list.
- Description: `(choose (inbox-recv ib) (chan-recv work))` in a loop where `work` always wins and `ib` rarely gets posts leaves one dead `RecvNode` per iteration, each holding its `SyncState`, closure and awaiter. `Channel<T>` bounds this with the `NotePark` sweep (design.md B7). The inbox has no such trigger.
- Repro: `dotnet cs/outd/stress.dll recvleak 100000`.
- Observed: `after 100000 lost receives, 100000 dead RecvNodes parked on the inbox`. Expected: bounded, as for channels.

### 9. `seconds`/`minutes` overflow 32-bit `int`, so timeouts fire early and deadlines fire at the wrong time
- Severity: Low (silent wrong result for long but realistic intervals of 25+ days)
- Status: Confirmed (deterministic)
- Location: `lib/std/prelude.bjo:3294,3297` (`(defun (seconds n) (* n 1000))`, `(defun (minutes n) (* n 60000))`), which feed `Cml.Timeout(int)` (`TimeoutNode.Arm`: `ms <= 0` commits at once) and `Scope(int deadlineMs)` (`<= 0` means no deadline; a wrapped positive value means a short deadline).
- Description: Multiplying silently wraps. `(minutes 36000)` (25 days) is -2134967296, so `(timeout (minutes 36000))` is ready immediately and beats a 1-second timeout. `(minutes 71583)` (≈50 days) wraps to **12704**, so `(with-deadline (minutes 71583) ...)` cancels the body after 12.7 s. Nothing checks for this. `Timeout`/`At` take `int`, and `AtEvent` clamps but `timeout` does not.
- Repro (`ovf/ovf.bjo`, `ovf/ovf2.bjo`):
```
(import (std prelude))
(defbjo (main args)
  (println (str "(minutes 36000) = " (->str (minutes 36000))))
  (println (sync (choose (wrap (timeout (minutes 36000)) (fun (u) "the 25-day timeout fired first"))
                         (wrap (timeout 1000) (fun (u) "the 1-second timeout fired first (expected)")))))
  (def r (try (with-deadline (minutes 71583) (sync (timeout 15000)) "body finished")
              #:catch (System.Exception)))
  (match r ((Ok v) (println v)) ((Err e) (println "the 50-day deadline cancelled the body")))
  0)
```
- Observed: `the 25-day timeout fired first`, and `(minutes 71583) = 12704` followed by `the 50-day deadline cancelled the body` after 12.77 s. Expected: the 1-second timeout wins and the body finishes. Either use checked arithmetic or saturate, or have `timeout`/`with-deadline` take a 64-bit value.

---

### Notes (checked and not reported as bugs)
- I could not break the `FiberWatch` direct and choose park protocols (arm/take/End generations, `CancelParked` under the channel lock) by reading the interleavings. Every stale-reference path I traced is refused by a generation.
- `TimeoutNode` gate/cancel, `Promise.Complete`/`RegisterAny`, the `SpawnBatch` watchdog, and the scope counter (`TryEnlist`/`Close`/`Landed`) look correct.
- Compiled programs load `BjolangRuntime/bin/Release/net10.0/BjolangRuntime.dll` (first entry of `BjolangProbeDirs`), not `bin/compiler`'s R2R copy. It is the same commit, but it is why finding 1 is deterministic in compiled programs and rare in the C# harness.


---

## Correctness audit: lib/std/prelude.bjo, eq.bjo, maths.bjo, monad.bjo, clr-ord.bjo (+ docs)

All repros were compiled and run in the sandbox with
`BJOLANG_LIB=/tmp/correctness/prelude/lib dotnet .../Bjolang.dll prog.bjo && dotnet prog.exe`.
Repro sources are in `/tmp/correctness/prelude/` (r_*.bjo, t*.bjo). `inc/chk.bjo` is a small helper macro:
`(chk "label" expr)` prints `label => (->str expr)`, or `ERR <ExceptionType>`.

---

### `take` pulls one element too many from its source (silently loses data from port-backed seqs; `take 0` still pulls one)
- Severity: High
- Status: Confirmed (repro run)
- Location: lib/std/prelude.bjo:643-647 (`Iterable` default method `take`)
- Description: `take` is
  ```
  (seql (:with i 0 (+ i 1))
        (:for elem s)
        (:finish (= n i))
        (:yield elem))
  ```
  The `:for` clause pulls the next element *before* `:finish` is checked, so after yielding n elements the walk pulls element n+1 and then stops. For `(take 0 s)` one element is pulled. Every default-`take` over a `Seq` therefore (a) calls a `map` callback one extra time, (b) blocks on an endless/blocking source for an element nobody asked for, and (c) for a seq over a port (`port->seq`, `in-port`), consumes and discards the next item from the port, so it is lost to the next reader. `seq-take` is explicitly documented (prelude-seqs.bjodoc `seq-take`) as "After the n-th element it stops without pulling another from s, so it is safe on an endless sequence", and `take`'s own doc ("A lazy sequence of the first n elements") gives no hint that it reads further. A fix is to test `(= n i)` before pulling (e.g. a `:while (< i n)`-style guard ahead of the `:for`, or `:finish` evaluated before the cursor advances).
- Repro (r_take2.bjo — data loss):
  ```
  (import (std prelude))
  (import (std ports))
  (defun (main args)
    (def p (open-input-string "header1\nheader2\nbody\n"))
    (def headers {listing l (:for l (take 2 (port->seq read-line p)))})
    (println (->str headers))
    (println (str "rest of port: [" (read-all p) "]"))
    0)
  ```
  Repro (r_take.bjo — extra callback calls):
  ```
  (import (std prelude))
  (: calls (Array int))
  (def calls (make-array 1))
  (: costly (-> int int))
  (defun (costly x)
    (array-set! calls 0 (+ (array-ref calls 0) 1))
    (* x 10))
  (defun (main args)
    (array-set! calls 0 0)
    (println (->str (seq->list (take 2 (map costly (list 1 2 3 4 5))))))
    (println (str "f called " (->str (array-ref calls 0)) " times for take 2"))
    (array-set! calls 0 0)
    (println (->str (seq->list (take 0 (map costly (list 1 2 3 4 5))))))
    (println (str "f called " (->str (array-ref calls 0)) " times for take 0"))
    (array-set! calls 0 0)
    (println (->str (seq->list (seq-take (seq-map costly (list->seq (list 1 2 3 4 5))) 2))))
    (println (str "f called " (->str (array-ref calls 0)) " times for seq-take 2"))
    0)
  ```
- Observed: `(header1 header2)` then `rest of port: []` (the `body` line is gone). `f called 3 times for take 2`, `f called 1 times for take 0`, `f called 2 times for seq-take 2`. A counting `seq` source showed `take 3` → 4 pulls, `take 0` → 1 pull.
  Expected: `rest of port: [body]`; f called 2 times for `take 2` and 0 times for `take 0`, as `seq-take` does.

### Chained comparisons `(< a b c)` / `#ch` evaluate the second operand before the first
- Severity: Medium
- Status: Confirmed (repro run)
- Location: lib/std/prelude.bjo:3033-3045 (`chain-forms`, used by `def/hash-extend ch`)
- Description: For a non-atom middle operand `x`, `chain-forms` emits `(let ((t ,x)) (if (,op ,prev t) ...))`. `prev` is the *first* operand, written inline in the `if`, so it is evaluated after the `let` has already evaluated the second operand. Because the parser turns every comparison with 3+ operands into `#ch`, `(< (f) (g) h)` runs `g` before `f`. With side-effecting operands (reads from a port, counters, `next!`), the comparison silently compares the values in swapped positions. Arithmetic `(+ a b c)` (via `#fl`) evaluates left to right, so this is inconsistent; the doc ("A middle operand that is not an atom is bound once, right before its first use") does not say operand order changes. Fix: also bind a non-atom first operand to a temporary before the middle one, or bind `prev` first.
- Repro (r_ch.bjo):
  ```
  (import (std prelude))
  (defun (main args)
    (def p (open-input-string "1\n2\n"))
    ;; Three readings that should be increasing: 1 < 2 < 100
    (println (->str (< (string->int (read-line p)) (string->int (read-line p)) 100)))
    0)
  ```
  (t17.bjo also logs the order: `(< (log "a" 1) (log "b" 2) (log "c" 3))` logs `bac`, `(+ ...)` logs `abc`.)
- Observed: `False` (the first `read-line` result went to the second operand). Evaluation order logged as `bac` / `bacd`.
  Expected: `True`; operands evaluated `abc`.

### `file-read/seq` / `file-read-lines/seq` leave the file open when a walk stops early (contradicts the docs)
- Severity: Medium
- Status: Confirmed (repro run)
- Location: lib/std/prelude.bjo:2133-2142 (`file-read/seq`), with the `Iterable (Seq %a)` impl at prelude.bjo:706-715; docs: prelude-files.bjodoc:187-191, Docs/prelude.org (Files section), and the comment above `file-read/seq`
- Description: The docs say the file "is closed when the walk ends: at the end of the file, or when the walker stops early". But every walk that goes through `Iterable` (`find`, `any?`, `all?`, `take`, a `loop` with `:finish`, a comprehension) walks the `Seq` with a `seq-cursor`, which is never disposed when the loop is left early (prelude-seqs.bjodoc `seq-cursor-done?` even says so: "A walk left before the end ... the enumerator is left to the garbage collector"). The `with-open` inside the `seq` body therefore never runs and one file descriptor leaks per early-stopped walk until a GC finalizes it. Only `seq->list`/`seq-take`-style builtins (which use `foreach`) close it. Docs/prelude.org's own example `(find #(...) (directory-walk "."))` style usage is exactly the leaking pattern when applied to `file-read-lines/seq`.
- Repro (r_fd.bjo):
  ```
  (import (std prelude))
  (: open-fds (-> int))
  (defun (open-fds) (vec-length (directory-files "/proc/self/fd")))
  (defun (main args)
    (def path "/tmp/correctness/prelude/r_fd.txt")
    (file-write-text path "1\n2\n3\n")
    (def before (open-fds))
    (loop (:for i (range 0 50))
          (:do (ignore (find (fun (l) (= l "1")) (file-read-lines/seq path)))))
    (println (str "fds leaked by 50 early-stopped `find` walks: " (->str (- (open-fds) before))))
    (def before2 (open-fds))
    (loop (:for i (range 0 50))
          (:do (ignore (loop (:for l (file-read-lines/seq path))
                             (:acc got (folding "" l))
                             (:finish #t)))))
    (println (str "fds leaked by 50 loops left by :finish: " (->str (- (open-fds) before2))))
    0)
  ```
- Observed: `fds leaked by 50 early-stopped find walks: 50`, `fds leaked by 50 loops left by :finish: 50` (same with `take 1`: 20 walks → 20 fds; `seq-take` → 0).
  Expected: 0 leaked descriptors, as documented.

### `nan?` doc says `(= x x)` is false for NaN, but `=` on doubles answers `#t`
- Severity: Medium (doc mismatch on a numeric edge case users rely on)
- Status: Confirmed (repro run)
- Location: lib/std/maths.bjodoc:251-253 (`nan?` reference) vs lib/std/eq.bjo:171-173 (`(impl (Eq double)` uses `clr-equals`) and prelude-core.bjodoc:804 / Docs/prelude.org ("double uses Equals rather than ==, so (= x x) is true for NaN")
- Description: maths.bjodoc tells users "NaN is unequal to everything, itself included, so `(= x x)` cannot test for it". The implementation deliberately uses `Equals`, so `(= nan nan)` is `#t` (and `(not (= x x))` — the classic NaN test the doc implicitly suggests is impossible — would also be wrong if someone relied on the opposite claim). The two doc files contradict each other; maths.bjodoc is the wrong one. (Relatedly, `<`/`>` are IEEE (`(< nan 1.0)` = `#f`) while `compare`/`less?` put NaN below everything (`(compare nan 1.0)` = -1); that is the documented "total order" and not reported separately.)
- Repro:
  ```
  (import (std prelude))
  (import (std maths))
  (defun (main args)
    (def nan (/ 0.0 0.0))
    (println (->str (= nan nan)))
    (println (->str (nan? nan)))
    0)
  ```
- Observed: `True` / `True`.   Expected (per maths.bjodoc): `(= nan nan)` false. Expected fix: correct the maths.bjodoc text.

### `take` with a negative count answers the whole input; `seq-take` answers nothing
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/std/prelude.bjo:643-647 (`take`)
- Description: `(:finish (= n i))` never fires for negative `n`, so `(take -1 s)` yields all of `s` (and never ends on an endless one). The doc says n is "Not negative", so it is a precondition, but the builtin `seq-take` documents "Zero or less gives the empty sequence" and `drop`/`seq-drop` both treat negatives as 0. Using `(>= i n)` would make `take` agree.
- Repro (part of r_take.bjo):
  ```
  (println (->str (seq->list (take -1 (list 1 2 3)))))
  (println (->str (seq->list (seq-take (list->seq (list 1 2 3)) -1))))
  ```
- Observed: `(1 2 3)` and `()`.   Expected: both `()` (or `take` refusing negative n).

### `length` of a `Range` whose span exceeds `int-max` is negative
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/std/prelude.bjo:2476-2488 (`impl (Collection Range)`, `length`)
- Description: `(+ (/ (- (- hi lo) 1) by) 1)`. The comment says the formula was chosen to avoid overflow for a step near int-max, but `(- hi lo)` itself overflows when `hi - lo > int-max`, e.g. `(range -2000000000 2000000000)` (which iterates fine) reports a negative length. Computing in `long` (or `uint` for the difference) and clamping/raising when the count does not fit would fix it.
- Repro (r_misc.bjo):
  ```
  (chk "length (range -2000000000 2000000000)" (length (range -2000000000 2000000000)))
  (chk "length (range int-min int-max)" (length (range int-min int-max)))
  ```
- Observed: `-294967296` and `-1`.   Expected: the true counts (4000000000 / 4294967295 do not fit an int, so an exception, or at least not a silently negative number).

### `range-by` / `Range` walk wraps past `int-max` instead of stopping (any step > 1 with `hi` near int-max)
- Severity: Low (known, in Todo.org — "so does range-by with a step near int-max"; note it is not only a step *near* int-max)
- Status: Confirmed (repro run)
- Location: lib/std/prelude.bjo:755-761 (`impl (Iterable Range)`, `iterable-next`)
- Description: `iterable-next` is `(+ c by)` and `iterable-done?` is `(>= c hi)`; once `c + by` overflows, `c` becomes negative and the walk continues forever. With `(range-by 0 int-max 2)` this already happens with step 2.
- Repro (t11.bjo):
  ```
  (chk "after hi" {listing x (:for x (take 3 (drop 1073741823 (range-by 0 2147483647 2))))})
  ```
- Observed: `(2147483646 -2147483648 -2147483646)`.   Expected: `(2147483646)` and the walk ends.

### `list-min` / `list-max` return the *last* of several equal extremes, unlike `least`/`greatest` and `list-sort`
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/std/eq.bjo:111-130 (`list-min`, `list-max`, via `fold-left`)
- Description: `fold-left` calls `(f elem acc)`, so `list-min` computes `(least elem acc)`; `least` breaks ties toward its *first* argument ("ties to left"), which here is the newer element. So with elements equal under `Ord` but distinguishable, `list-min` answers the last one while `(list-head (list-sort xs))` (stable sort) and `least` answer the first. Fix: `(fold-left (fun (x acc) (least acc x)) ...)`.
- Repro (r_misc.bjo):
  ```
  (type (: Item (Record (: key int) (: tag string))))
  (impl (Ord Item)
    (defun (compare a b) (compare (record-ref a key) (record-ref b key))))
  ...
  (def xs (list (Item (key 1) (tag "first")) (Item (key 1) (tag "second"))))
  (chk "list-min tie" (tag-of (list-min xs)))
  (chk "list-head of list-sort" (record-ref (list-head (list-sort xs)) tag))
  (chk "least tie" (record-ref (least (list-head xs) (list-head (list-tail xs))) tag))
  ```
- Observed: `second`, `first`, `first` (list-max likewise answers the last tie).   Expected: `first` for all three.

### `seconds` / `minutes` overflow silently, turning a long deadline into a short one or none
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/std/prelude.bjo:3293-3297 (`seconds`, `minutes`)
- Description: `(* n 1000)` / `(* n 60000)` on `int` wrap. `(minutes 71583)` (≈50 days) becomes 12704 ms, so `(with-deadline (minutes 71583) ...)` cancels after 12.7 s; `(seconds 3000000)` becomes negative, which `with-deadline` treats as "no deadline" (prelude-concurrency.bjodoc: "Zero or less means no deadline"). Nothing reports the overflow. Checked multiplication (or raising past int-max) would fix it.
- Repro (r_misc.bjo):
  ```
  (chk "(seconds 3000000)" (seconds 3000000))
  (chk "(minutes 71583)" (minutes 71583))
  ```
- Observed: `-1294967296` and `12704`.   Expected: an error, or a value that is not silently shorter than requested.

### `lcm` raises `OverflowException` for `int-min`, while its doc says it wraps and lists no exception
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/std/maths.bjo:117-121 (`lcm`) and maths.bjodoc `lcm`
- Description: `lcm` calls `gcd`, whose `abs` raises for the smallest signed value (documented on `gcd`), and itself applies `abs` to the product, which raises if the wrapped product is int-min. The `lcm` doc says "it wraps as * does" and has no `raises` entry.
- Repro (r_misc.bjo): `(chk "(lcm int-min 2)" (lcm int-min 2))`
- Observed: `ERR OverflowException`.   Expected: per doc, a wrapped answer — or the doc should list the exception as `gcd`'s does.

### `->str` of a tuple prints .NET internals for container elements (no `->str` impl for `Tuple`)
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/std/prelude.bjo:3300-3430 (container `->str` impls; none for `Tuple`), falls to the blanket at prelude.bjo:221-223
- Description: The comment above the Option/Result impls (prelude.bjo:3411-3414) describes exactly this problem ("that ToString formats the payload with its own ToString rather than its ->str") and fixes it for Option/Result, but tuples still go through `ValueTuple.ToString`, so e.g. the result of `vec-split` (a tuple of vecs) prints the RRB tree's debug dump. Tuples are what `vec-split`, `quotient/remainder`, `map->list`, `enumerate` answer, so they are printed often.
- Repro (r_misc.bjo): `(chk "(->str (vec-split [1 2 3] 1))" (vec-split [1 2 3] 1))`
- Observed:
  ```
  (RrbList<Int32> (Cnt: 1, Height: 0)
    [Tail]: [1]
    [Tree Root]: <null>
  , RrbList<Int32> (Cnt: 2, Height: 0) ...
  ```
  Expected: something like `([1] [2 3])` (elements through their own `->str`).

### Docs/prelude.org: further statements that do not match the code (not in Todo.org's list)
- Severity: Low
- Status: Confirmed by reading (and t3/t6 runs where noted)
- Location: Docs/prelude.org, "Traits" section and "Files" section
- Description:
  - "`Collection` : `(length c)`, `(empty? c)` and `(->seq c)` ... not for `(Array %a)`, which has no `->seq` to give." — `Collection` has no `->seq` (it is an `Iterable` method, prelude.bjo:612) and the prelude *does* implement `Collection` for `Array` (prelude.bjo:2426-2431).
  - "`Iterable` ... a `Vec`'s [cursor] is a bare `int`" — the Vec cursor is a `VecCursor` (prelude.bjo:725-731); only Array's is an int.
  - "A string's `%cursor` is a `StringCursor`" — it is a `StringWalk` (prelude.bjo:784-790), as prelude-strings.bjodoc correctly says.
  - "`file-read/seq` ... closes it when the walk ends — at the end, or when the walker stops early" — false for loop-based early exits (see the fd-leak finding above).
  - "`file-read-lines` ... `"\n"` and `"\r\n"` both end a line" (also the comment at prelude.bjo:2101) — a lone `"\r"` also ends a line (observed: `read-line` of `"a\rb"` → `"a"`); prelude-files.bjodoc states this correctly.
- Repro: n/a (documentation).
- Observed/Expected: as listed.

---

### Checked and found consistent (no finding)

- No stack overflow on 1,000,000-element lists: `list-append`, `list-foldr`, `->str`, `=`, `eq-hash`, `list-sort`, `list->vec`, `vec->list`, `string->list`, `list-min`, `bind` (List monad).
- `list-sort` is stable (checked with a key-only `Ord`).
- vec index edge cases (`vec-ref/-set/-insert/-remove-at/-slice/-split/-pop/-pop-first/-reduce` at -1, length, length+1) raise as documented; `try-ref` on a Vec gives `None`.
- `string-split` (empty separator, separators at the ends, `""`), `string-join`, `string-replace` (empty old → ArgumentException, non-overlapping), `string-pad-*` (negative width, astral pad), `string-trim` vs `char-whitespace?` (exhaustively over all scalar values: 0 mismatches), `string-upcase/downcase` vs `char-upcase/downcase` (exhaustive: 0 mismatches, including Deseret), `string-reverse` round trip with astral chars, string ordering = code-point order.
- `string->int` / `string->double` on whitespace, `+1`, `1e5`, `1,000`, hex, overflow, NaN/Infinity; `double->string` round-trips (0.1+0.2, inf, NaN, -0.0).
- maths: `modulo` sign rules incl. int-min cases, `quotient`/`remainder`, `gcd`, `expt` (wraps, n<0 raises), `integer-sqrt` (exhaustive to 100000, int-max, long-max), `exact-*` rounding/overflow, `sign`/`abs`/`clamp` exceptions as documented.
- Maps: `map-merge` precedence, `map-merge-with` argument order, duplicate keys in `#map`/`list->map`/`mapbuilder`/`seq->map` (last wins), `map-add` duplicate raises, `=`/`eq-hash` order-independent, comparer preserved through `map-map`/`map-filter`/`map-map-values`/`map-set`/`map-merge`/transients/builders for a union key with a custom `Eq`.
- The suspending (`#:bjo`) copies of `vec-reduce/fold/iter/filter`, `list-foldl/foldr`, `seq-fold`, `map-fold`, `any?/all?/find` agree with the synchronous copies.
- Macros: `cond` (`:def`, `:match`, guards), `if-let`, `when-let` (evaluates once), `some->`, `try->`, `let*`, `get`/`try-get`/`set`/`update`, `do` blocks, `type/derive` Eq/Ord (records, unions with nullary cases, empty record, parametrised, mutable → unhashable).
- `drop-while`/`take`/`drop`/`enumerate` re-walkable; `any?`/`find`/`all?` stop at the deciding element.
- `string-reverse`/`string-pad-*` operate on code points, so a decomposed "é" is split/mis-padded — this is the documented behaviour ("by character"), not reported. `char-ci=?` ς/σ mismatch is documented in prelude-chars.bjodoc and Todo.org.
- Union cases that a custom `Eq` equates are unequal in `vec-contains` and in 5+-tuples (blanket `Equals`) — known, in Todo.org ("Equality and hashing").


---

## Correctness findings: lib/std (excluding prelude/eq/maths/monad/clr-ord)

Sandbox: /tmp/correctness/stdlib (repro programs are in t1..t7). None of these are in Todo.org.

Covered with no bugs found (differential/randomised tests, all passed):
- orderedmap: 20 000 random set/remove ops against a reference array, with range/from/until/successor/predecessor/min/max/contains, transients, persistence of an old snapshot, and builder duplicate keys.
- orderedset: union, intersection, difference, symmetric-difference, remove-all and transient filter! over 300 random pairs. Also orderedmap merge, merge-with, remove-all, set-all and filter, and the hashset ops.
- datetime: month clamping, leap years, ISO weeks, DST gaps and overlaps (Stockholm, Apia 2011, Lord Howe's 30-minute shift), resolvers, moment arithmetic, ISO-8601 and HTTP-date round trips, 12-hour clock, fraction widths, Unix time, duration limits.
- rx: astral characters, group numbering, lazy and greedy quantifiers, empty-match split/replace.
- Read through: deque and the mutable vec/map/set/heap wrappers, including the stable sort. Read through: random's range and Lemire bounds.

---

### run: a pipeline whose downstream stage exits early hangs forever (`yes | head -n 1`)
- Severity: High
- Status: Confirmed (repro run)
- Location: lib/std/run.bjo:376-388 (`junction`, `pump`), with `BjoProc.PumpAsync` in BjolangRuntime/BjoProcess.cs:90-102; waited on in `waiting` (run.bjo:~519)
- Description: The pump between two stages copies the upstream stdout into the downstream stdin. When the downstream process exits early, the pump's write fails with a broken pipe. `PumpAsync` disposes only `to`. It never closes or drains `from`, the upstream process's stdout, so the upstream process blocks forever on a full pipe. `waiting` then joins all the `exits` and never returns. The same thing happens with any upstream that writes more than a pipe buffer (about 64 KiB) after the downstream stops reading. That makes ordinary shell idioms (`... | head`, `... | grep -m1`) hang.
- Repro:
```
(import (std prelude))
(import (std run))

(: show-r (-> (Result Exception string) string))
(defun (show-r r)
  (match r ((Ok s) (str "Ok " s)) ((Err e) (str "Err " (.-Message e)))))

(defun (main args)
  (println (str "head: " (show-r (run/string '(pipe (seq 1 200000) (head "-n" "1"))))))
  0)
```
- Observed: never returns (`timeout 20` kills it, exit 124). The same happens with `(yes)` upstream. With `(seq 1 5)` it returns `Ok 1`.   Expected: `Ok 1\n`, as the shell gives. At worst an `Err`, but not a hang.

### run: `from-file` never closes the input file (one fd leaked per run)
- Severity: Medium
- Status: Confirmed (repro run)
- Location: lib/std/run.bjo:399-402 (`wire-redirect` RFrom), 418-426 (`redirect-input`); `PumpAsync` disposes only its target
- Description: `(bjo (pump file to))` copies the opened file into the process's stdin. `PumpAsync` disposes `to` but never the `file` reader, and nothing else closes it. Every `from-file` run leaves the file open for the life of the scope (here, the whole program). A long-running program that runs commands in a loop runs out of descriptors, and on Windows the file stays locked.
- Repro:
```
(import (std prelude))
(import (std run))

(defun (main args)
  (let go ((i 0)) (when (< i 50) (ignore (run/status '(from-file "in.txt" (wc "-l")))) (go (+ i 1))))
  (println (match (run/string '(sh "-c" "ls -l /proc/$PPID/fd | grep -c in.txt")) ((Ok s) s) ((Err e) "err")))
  0)
```
- Observed: `50`: there are 50 open descriptors on in.txt after the 50 runs have completed (and after a GC, in a variant). Total fds went from 57 to 135.   Expected: `0`.

### run: `into-file`/`append-to-file` truncates the target before the command is known to start, and leaks the writer when it fails
- Severity: Medium
- Status: Confirmed (repro run)
- Location: lib/std/run.bjo:403-405 (`wire-redirect` RInto: `open-for-writing` before `redirect-output` wires the inner node); also `redirect-input`/`redirect-output` on their `:propagate`/None paths
- Description: The `StreamWriter` (which truncates) is created before the inner command is started. If the start fails (a missing program, a bad redirect nesting), `run` returns `Err` but the file has already been emptied. The open writer is never closed on that path. The doc says `run` returns `Err` when "a program cannot be started", and nothing suggests that the target is clobbered first.
- Repro:
```
(import (std prelude))
(import (std run))
(: st (-> (Result Exception int) string))
(defun (st r) (match r ((Ok n) (str "Ok " (->str n))) ((Err e) (str "Err " (.-Message e)))))
(defun (main args)
  ;; keep.txt contains "precious"
  (println (st (run/status '(into-file "keep.txt" (no-such-program-xyz)))))
  (println (str "[" (match (open-input-file "keep.txt") ((Ok p) (let ((s (read-all p))) (close-input-port p) s)) ((Err e) "ERR")) "]"))
  0)
```
- Observed: `Err An error occurred trying to start process 'no-such-program-xyz' ...` and then `[]`. The file is now empty.   Expected: `Err ...`, with keep.txt still `precious`. Open the file only after the inner form has started, or close it and leave the target alone on failure.

### http: a URL with a `#fragment` silently loses all `#:query` parameters
- Severity: Medium
- Status: Confirmed (repro run, local Python server)
- Location: lib/std/http.bjo:228-235 (`full-url`)
- Description: `full-url` appends `?`/`&` plus the encoded query to the end of the url string. When the url has a fragment (`http://h/p#sec`), the query ends up inside the fragment (`/p#sec?a=1`), and .NET never sends a fragment. The request is sent with no query and no error. The doc says "Query parameters are percent-encoded and appended to the url."
- Repro:
```
(import (std prelude))
(import (std http))
(defun (main args)
  (match (fetch (http-get "http://127.0.0.1:47391/p#sec" #:query [("a" . "1")]))
    ((Ok r) (println (response-text r)) (response-close r))
    ((Err e) (println "err")))
  0)
```
(The server echoes `self.path`; see t5/srv.py.)
- Observed: the server sees path `/p`.   Expected: `/p?a=1`. Insert the query before the fragment, for example by building it with `UriBuilder`.

### http: the default client keeps a process-wide cookie jar and replays cookies on later requests
- Severity: Medium
- Status: Confirmed (repro run)
- Location: lib/std/http.bjo:157-170 (`make-client`: `SocketsHttpHandler` is created with its default `UseCookies = true`), 176 (`current-http-client` shared default)
- Description: `SocketsHttpHandler` stores every `Set-Cookie` it receives in its own `CookieContainer` and attaches matching cookies to later requests. `current-http-client` is one client per process, so a cookie set by any response leaks into every later request to that host, from unrelated code and requests. The module never mentions this. Its doc says "nothing here reads cookies yet", and a `Cookie` header passed in `#:headers` is combined with the jar's. That is hidden shared mutable state, which is a correctness issue and also a security one (session cookies cross between callers). The fix is `UseCookies = false` in `make-client`.
- Repro: t5/h.bjo. Make a GET to `/dup`, whose response has `Set-Cookie: a=1...` and `Set-Cookie: b=2`, then a POST to `/c` with no cookie headers.
- Observed: the second request's headers, as echoed by the server, include `"Cookie": "a=1; b=2"`. A later, unrelated `GET /p?&a=1` also carries `Cookie: a=1; b=2`.   Expected: no `Cookie` header unless the caller wrote one.

### fmt: `numeric` with grouping corrupts numbers that .NET prints in exponent form
- Severity: Medium
- Status: Confirmed (repro run)
- Location: lib/std/fmt.bjo:417-436 (`formatted`), 397-413 (`grouped`)
- Description: With the default precision (-1), `formatted` uses `double.ToString(InvariantCulture)`, which switches to scientific notation for small and large magnitudes (`1E-05`, `1.2345678901234568E+17`). The text before `.` is then treated as the integer digits and grouped. A comma is inserted inside the exponent, or the mantissa is left ungrouped, and a custom `decimal-sep` replaces the mantissa's point. The doc promises "grouped digits".
- Repro:
```
(import (std prelude))
(import (std fmt))
(defun (main args)
  (println (render->string [(numeric 0.00001 #:comma 3)]))
  (println (render->string [(numeric/comma 123456789012345678.0)]))
  0)
```
- Observed: `1E,-05` and `1.2345678901234568E+17`.   Expected: `0.00001` and `123,456,789,012,345,680`, or at least no separator inside an exponent. The exponent should be detected (or a non-exponent format such as "R"/"F" plus trimming used) before grouping.

### simpletest fake FS: `fs-move` of a file onto itself with `MoveReplace` deletes it
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/std/simpletest.bjo:~466-488 (`fake-fs-move`)
- Description: When `from = to` and `MoveReplace` is given, `replacing` is #t. The code first does `map-remove` of `to`, which is the source, and then renames entries from `from`, which no longer exists. The file disappears. The real `File.Move(a, a, true)` is a no-op, and 214_fake_filesystem says a fake and the disk are meant to be indistinguishable.
- Repro:
```
(import (std prelude))
(import (std effect))
(import (std simpletest))
(: show (-> (Result Exception Unit) string))
(defun (show r) (match r ((Ok u) "Ok") ((Err e) (str "Err " (.-Message e)))))
(defun (main args)
  (println (str "real: " (show (fs-move "a.txt" "a.txt" MoveReplace)) " " (->str (file-exists? "a.txt"))))
  (with-fake-fs (("a.txt" "hi"))
    (println (str "fake: " (show (fs-move "a.txt" "a.txt" MoveReplace)) " " (->str (file-exists? "a.txt")))))
  0)
```
- Observed: `real: Ok True` and `fake: Ok False`.   Expected: `fake: Ok True`.

### simpletest fake FS: moving a directory into its own subdirectory succeeds
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/std/simpletest.bjo (`fake-fs-move` / `fake-renamed`)
- Description: `(fs-move "d" "d/sub" MoveNew)` passes every check, because the parent `d` is a directory, and every `d/...` key is renamed to `d/sub/...`. The real `Directory.Move` fails with an IOException ("Invalid argument"). Code that the fake says works fails on disk.
- Repro: as above, with `(with-fake-fs (("d/x.txt" "x")) (fs-move "d" "d/sub" MoveNew))` and the real `(directory-create "dd") (fs-move "dd" "dd/sub" MoveNew)`.
- Observed: fake gives `Ok`, and `d/sub/x.txt` exists. Real gives `Err Invalid argument`.   Expected: the fake gives `Err` too.

### datetime: `iso8601->duration` accepts a fraction on hours/minutes when it is zero
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/std/datetime.bjo:1576 (`read-time-parts`: `((and (> f 0L) (not (= unit 2))) None)`)
- Description: Only seconds may take a fraction ("Only seconds take a fraction"). The check rejects a non-zero fraction only, so `PT1.0H` and `PT2.000M` are accepted while `PT1.5H` is refused. The reader accepts text that the module's own rule says is invalid, and the outcome depends on the digits.
- Repro: `(->str (iso8601->duration "PT1.0H"))` and `(->str (iso8601->duration "PT1.5H"))`
- Observed: `(Some PT1H)` and `None`.   Expected: `None` for both. Track whether a separator was seen, not whether f > 0.

### stopwatch: `time-it` divides by zero for `#:times 0` and does not drop the body's value as documented
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/std/stopwatch.bjo:83-104 (`time-it`)
- Description: (1) `(/ (stopwatch-ms sw) ,n)` and `(/ bytes-used ,n)` raise `DivideByZeroException` when n is 0. (2) The comment says "Drops body's value", but the body is spliced into statement position without `ignore`. `(time-it (+ 1 2))` is therefore a compile error ("this value has type int and is discarded"). `,n` is also evaluated several times.
- Repro: `(time-it #:times 0 (ignore (+ 1 2)))` and `(time-it (+ 1 2))`
- Observed: an unhandled `System.DivideByZeroException` at runtime, and a type error at compile time.   Expected: a refusal or a 0-run message for n ≤ 0, and a body value that is dropped.

### ports: `chan-next` hides a failed reader as end of stream
- Severity: Low
- Status: Suspected (from reading)
- Location: lib/std/ports.bjo (`chan-next`: `(wrap (promise-join done) (fun (r) None))`)
- Description: `port->chan` runs the reader in `port-fill`. If the reader raises, for example on a decoding or I/O error part-way through, the promise fails. `chan-next` maps any `promise-join` result, `Err` included, to `None`, so the consumer sees a normal end of stream and silently works with truncated data. The `Err` should be passed on.


---

## Correctness audit: lib/text (json, json-codec, xml, bjodat), lib/janitor, bjo/

Sandbox: /tmp/correctness/text. Every "Confirmed" item below was run there: j1.bjo/j2.bjo (JSON), x1-x3.bjo (XML), b1.bjo (bjodat), bjo/t1.bjo (the bjo tool's pure functions, built from a copy of bjo/*.bjo).
None of these issues is in Todo.org. The closest entry is "No shared clone cache", which is a different problem.

Areas checked that came out clean (nothing to report):
- **JSON reader** against RFC 8259:
  - Rejected correctly: leading zeros, a bare "-", "1.", ".5", "+1", "1e", "1e+", trailing commas, "[,]", "{,}", trailing garbage, empty input, NBSP/FF as whitespace, a raw control character in a string, a lone surrogate (either half, raw or escaped), a bad pair, a short \u, \U.
  - Read correctly: "\/", all escapes, surrogate pairs, raw non-BMP, U+2028, DEL, duplicate keys (documented: last wins), a BOM, values wider than a long (become a double), long.MinValue, nesting 1000 deep both sync and inside a bjoroutine.
  - There is no JSON writer (documented), so writer escaping, NaN and inf output could not be tested.
- **XML reader**:
  - Entities, undefined entities, &#0;, &#xD800;, CDATA, `]]>` in text, comments, PIs, both quote types, attribute-value normalisation and character references, duplicate attributes, mismatched tags, undeclared prefixes, multiple roots, data after the root, empty input, DTD modes, xml-skip-element!, and text merged across an ignored comment are all handled correctly.
- **XML writer**:
  - Escaping of `< & > " '` is correct in both text and attributes. CR, LF and TAB in attributes are entitized, and `]]>` in text is escaped.
  - Comment and PI checks work. Invalid characters are refused with XmlWriteError.
  - The namespace prefix choice round-trips, including when hints clash.
- **json-codec**: the int range check, Vec order and Map handling are correct.
- **janitor/docs.bjo, janitor/modules.bjo**: read only, no defect found. The published-doc forms in lib/ contain nothing the bjodat reader refuses. The prefix and roots logic matches the documentation.
- **bjo**:
  - Lock-file round trip (write-lock then read-lock gives the same text), including a URL holding `"` and `\`.
  - Git and dotnet are run with argument vectors (ProcessStartInfo.ArgumentList and (std run)). No shell is involved, so there is no quoting problem.
  - Manifest parsing goes through def/bjodat-type. The bjodat findings below apply to it.

---

### bjodat: `int` fields silently wrap out-of-range integers
- Severity: High
- Status: Confirmed (repro run)
- Location: lib/text/bjodat.bjo:131-135 (`impl (bjodat-> int)`)
- Description:
  - The instance is `((BjoInt n) (Ok (cast System.Int32 n)))`. A long outside the int range is truncated, not refused.
  - (text json-codec)'s `json-> int` refuses exactly this case ("expected an integer that fits in an int").
  - Both bjodat decoders, tree and one-pass, use this instance. So any manifest or config `int` field can silently read a different number.
- Repro:
```
(import (text bjodat))
(import (text bjodat-core))
(def/bjodat-type (: M (Record (: name string) (: n int #:default 0))))
(defun (main args)
  (match (bjodat-parse-M "{:name \"a\" :n 4294967297}")
    ((Ok m) (println (bjodat->string (M->bjodat m))))
    ((Err e) (println e)))
  0)
```
- Observed: `{:name "a" :n 1}`.
- Expected: `Err "M.n: expected an integer that fits in an int"`, as json-codec gives.

### bjodat: a lone-surrogate `\u` escape crashes the reader with an unhandled exception
- Severity: Medium (crash on hostile or plausible input; every reader is documented to answer `(Result string _)`)
- Status: Confirmed (repro run)
- Location: lib/text/bjodat-core.bjo:368-380 (`read-unicode-escape!`: `(stringbuilder-add-unit! sb acc)`), then `read-escaped-text` → `stringbuilder->string`
- Description:
  - Any `\uD800`–`\uDFFF` that is not part of a pair is appended as a raw UTF-16 unit.
  - `stringbuilder->string` then throws `System.InvalidOperationException: ... not valid UTF-8`.
  - Nothing catches it. `parse-bjodat`, `bjodat-parse-<Name>` and `read-lock`/`read-package` (manifest and lock) all abort the process.
  - text/json.bjo checks surrogate pairing explicitly. bjodat does not.
- Repro:
```
(import (std prelude))
(import (text bjodat))
(def/bjodat-type (: M (Record (: name string))))
(defun (main args)
  (println (match (try (bjodat-parse-M "{:name \"\\uDC00\"}") #:catch (System.InvalidOperationException))
             ((Ok (Ok m)) "ok") ((Ok (Err e)) (str "Err " e)) ((Err e) (str "RAISED " (.-Message e)))))
  0)
```
  (`(parse-bjodat "\"\\uD83D\"")` without the try kills the program: "Unhandled exception. System.InvalidOperationException".)
- Observed: `RAISED stringbuilder->string: the bytes added with stringbuilder-add-code! are not valid UTF-8.`
- Expected: `(Err "line 1, column N: ...")`.

### bjodat: a number is not checked for a delimiter after it, so `1abc`, `1.2.3`, `0x1g` are silently split into two values
- Severity: Medium (accepts invalid input and gives a silently different value)
- Status: Confirmed (repro run)
- Location: lib/text/bjodat-core.bjo:636-650 (`finish-number`) and 570-585 (`read-radix`). Neither looks at what follows the last digit.
- Description:
  - After the digits, fraction and exponent, the reader returns. The next character starts a new value in `read-sequence`.
  - So an unquoted version `(version 1.2.3)` reads as `(version 1.2 .3)`.
  - Top-level `parse-bjodat "1abc"` is refused only by the trailing-text check.
  - The compiler's own lexer treats `1abc` as one token. bjodat claims to be "Bjolang's shapes read as data".
- Repro:
```
(import (text bjodat-core))
(defun (main args)
  (loop (:for s (list "(1abc)" "(1.2.3)" "(1-2)" "(0x1g)"))
        (:do (println (match (parse-bjodat s) ((Ok v) (bjodat->string v)) ((Err e) e)))))
  0)
```
- Observed: `(1 abc)`, `(1.2 .3)`, `(1 -2)`, `(1 g)`.
- Expected: a refusal, because a number must end at whitespace or a delimiter, as source tokenises it.

### bjodat: hex/binary literals of 64 bits wrap to negative numbers
- Severity: Medium (silent wrong result)
- Status: Confirmed (repro run)
- Location: lib/text/bjodat-core.bjo:582 (`read-radix` → `System.Convert.ToInt64(string, radix)`)
- Description:
  - `Convert.ToInt64(s, 16)` reads 16 hex digits (or 64 binary digits) as a two's-complement bit pattern. It does not overflow.
  - The `OverflowException` branch ("does not fit in a long") is only reached at 17+ digits.
  - The negation `(- 0L v)` also wraps.
- Repro:
```
(import (text bjodat-core))
(defun (main args)
  (loop (:for s (list "0xFFFFFFFFFFFFFFFF" "0x8000000000000000" "-0x8000000000000000" "0x10000000000000000"))
        (:do (println (match (parse-bjodat s) ((Ok v) (bjodat->string v)) ((Err e) e)))))
  0)
```
- Observed: `-1`, `-9223372036854775808`, `-9223372036854775808`, `line 1, column 20: this number does not fit in a long`.
- Expected: the first two refused as not fitting in a long, the same way `9223372036854775808` decimal is promoted or refused; `-0x8000000000000000` is the only valid one.

### bjodat: long.MinValue does not round-trip (written as an integer, read back as a double)
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/text/bjodat-core.bjo:636-650 (`finish-number`)
- Description:
  - The sign is stepped over before the digits, so `"9223372036854775808"` is parsed and overflows. The value then falls back to a double and is negated.
  - `bjodat->string (BjoInt -9223372036854775808L)` writes `-9223372036854775808`, which reads back as `BjoFloat -9.223372036854776E+18`.
  - An `int`/`long` field would then refuse it ("expected an integer"). (text json) handles this correctly because it parses the sign together with the digits.
- Repro: `(parse-bjodat "-9223372036854775808")`. Round trip: `(parse-bjodat (bjodat->string (BjoInt -9223372036854775808L)))`.
- Observed: `-9.223372036854776E+18` (BjoFloat).
- Expected: `BjoInt -9223372036854775808`.

### bjodat: a non-BMP character literal is written but cannot be read back
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/text/bjodat-core.bjo:714-735 (`read-char-literal`), 1248-1257 (`char-into!`)
- Description:
  - The reader takes one UTF-16 unit as `first`. For U+1F600 that is the high surrogate.
  - The low surrogate is then taken by `take-name!` as if it were a character name, so `named-char` fails.
  - The writer emits `#\😀` for `(BjoChar (integer->char 128512))`, and its own reader refuses that. A `char` field holding an emoji cannot round-trip.
- Repro: `(parse-bjodat (bjodat->string (BjoChar (integer->char 128512))))`
- Observed: `line 1, column 5: #\😀 is not a character`.
- Expected: `BjoChar U+1F600`.

### bjodat: `-0.0` loses its sign on reading
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/text/bjodat-core.bjo:642 (`(BjoFloat (if neg (- 0.0 d) d))`)
- Description:
  - `0.0 - 0.0` is `+0.0`, so `-0.0` reads as positive zero.
  - The writer writes `-0.0`, so a negative zero does not round-trip. (text json) keeps the sign: `(parse-json "-0.0")` gives 1/d = -Infinity.
  - The fix is `(- d)`, i.e. negation, not subtraction from zero.
- Repro: `(parse-bjodat (bjodat->string (BjoFloat (* -1.0 0.0))))`, then look at `(/ 1.0 d)`.
- Observed: written `-0.0`; read back `0`, 1/d = `Infinity`.
- Expected: 1/d = `-Infinity`.

### bjodat: an invalid binary digit is reported as "does not fit in a long"
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/text/bjodat-core.bjo:570-585 (`read-radix` uses `hex-digit` for radix 2 as well, and maps `FormatException` to the overflow message)
- Repro: `(parse-bjodat "0b102")`, `(parse-bjodat "0b1a")`
- Observed: `line 1, column 7: this number does not fit in a long`.
- Expected: an error saying `2` / `a` is not a binary digit (or the number ends at `0b10` and the next character is refused).

### bjodat: one-pass and tree decoders disagree on a duplicate key/clause whose later value has the wrong type
- Severity: Medium (doc mismatch: "A duplicate key: the first wins, in both decoders" and "The one-pass and the tree decoders give the same message for the same document, word for word")
- Status: Confirmed (repro run)
- Location: lib/text/bjodat.bjo:824-855 (`jb-clauses`), 911-936 (`jb-form-clauses`)
- Description:
  - The one-pass decoder decodes every occurrence through the field's instance (`bjodat-read-field` / `bjodat-read-clause`) and only then keeps the first.
  - The tree decoder only converts the first occurrence (`bjodat-ref` / `bjodat-clause-ref`).
  - A later duplicate of the wrong type is therefore an error through `bjodat-parse-<Name>` and fine through `bjodat-><Name>`.
- Repro:
```
(import (text bjodat))
(import (text bjodat-core))
(def/bjodat-type (: M (Record (: name string) (: n int #:default 0))))
(def/bjodat-type (: F (Record #:tag f (: name string) (: n int #:default 0))))
(defun (main args)
  (println (match (bjodat-parse-M "{:name \"a\" :name 5}") ((Ok m) "ok") ((Err e) e)))
  (println (match (parse-bjodat "{:name \"a\" :name 5}") ((Ok v) (match (bjodat->M v) ((Ok m) "ok") ((Err e) e))) ((Err e) e)))
  (println (match (bjodat-parse-F "(f (name \"a\") (name 5))") ((Ok m) "ok") ((Err e) e)))
  (println (match (parse-bjodat "(f (name \"a\") (name 5))") ((Ok v) (match (bjodat->F v) ((Ok m) "ok") ((Err e) e))) ((Err e) e)))
  0)
```
- Observed: one-pass `M.name: expected a string` / tree `ok`; one-pass `F.name: expected a string` / tree `ok`.
- Expected: the same answer from both. Either skip the duplicate with `skip-value!`/`skip-clause!` once the slot is filled, or decode the duplicate in the tree decoder as well.

### bjodat: the one-pass decoder ignores keys written `#:key` (the tree decoder accepts them)
- Severity: Medium (silent misread; doc: "`:key #:key` a keyword, either spelling")
- Status: Confirmed (repro run)
- Location: lib/text/bjodat-core.bjo:986-1002 (`take-key!`)
- Description:
  - Only a held `:` takes the fast path that puts the name in the buffer.
  - A `#:name` key goes through `read-key`, which returns a `BjoKey`, and the buffer is then cleared. The key is treated as unknown and skipped.
  - The tree reader reads `#:name` as the same `BjoKey` and `bjodat-ref` finds it.
  - A manifest written with `#:` keywords therefore works through one decoder and not the other. Where the field has a default, it silently gets the default.
- Repro: `(bjodat-parse-M "{#:name \"a\"}")` against `(bjodat->M (parse-bjodat "{#:name \"a\"}"))`, with M as above.
- Observed: one-pass `Err "M: the map has no :name entry"`; tree `Ok {:name "a" :n 0}`.
- Expected: both `Ok`.

### bjodat: the tagged-form decoders disagree on what a clause head may be, and on an empty clause
- Severity: Low (doc mismatch with "refuse the same document in the same words")
- Status: Confirmed (repro run)
- Location: lib/text/bjodat-core.bjo:1105-1121 (`next-field!` uses `take-name!`, which accepts any run of symbol characters, e.g. `#t`, `123`, `1.5`); lib/text/bjodat.bjo:337-347 (`bjodat-clause-shape` requires a `BjoSym` head)
- Description:
  - Case 1: `(f (name "a") (#t 1))` and `(f (name "a") (123 1))`. One-pass treats the clause as an unknown clause and succeeds. The tree decoder refuses it with "F: a clause begins with a field name".
  - Case 2: `(f (name "a") (n))`. One-pass says "line 1, column 17: ')' closes nothing here". The tree decoder says "F.n: a clause holds a field name and one value".
- Repro: `bjodat-parse-F` against `bjodat->F (parse-bjodat ...)` on the strings above (F as in the duplicate-key repro).
- Observed: `OK` / `ERR F: a clause begins with a field name`; `ERR ... ')' closes nothing here` / `ERR F.n: a clause holds a field name and one value`.
- Expected: identical verdicts and sentences.

### bjodat: `#\` followed by a line feed does not advance the line counter
- Severity: Low
- Status: Suspected (from reading)
- Location: lib/text/bjodat-core.bjo:714-722 (`read-char-literal`: `(step! rd)` after taking `first`, even when `first` is 10)
- Description:
  - `#\` followed by a raw newline is a valid char literal (first = 10), but the reader steps with `step!` rather than `newline!`.
  - Every later error position in the document is then one line too low and has a wrong column.

### XML writer: a carriage return in text is not preserved (round-trip failure)
- Severity: Medium (silent data change on write)
- Status: Confirmed (repro run)
- Location: lib/text/xml/write.bjo:423-432 (`writer-settings`)
- Description:
  - The writer is left on .NET's default `NewLineHandling.Replace`. In text content that rewrites `\r` and `\r\n` to `\n`, instead of entitizing them as `&#xD;`.
  - Because the XML reader normalises line ends, the CR is lost. Attributes are fine, because there Replace entitizes.
  - Text containing a CR, e.g. a Windows-style value read from an attribute or a `&#13;` from a parsed document, does not survive parse → write → parse. A text node containing `&#13;` is the only way to keep it, and the writer never emits one.
  - Setting `NewLineHandling.Entitize` fixes it.
- Repro:
```
(import (std prelude))
(import (text xml))
(import (text xml read))
(import (text xml write))
(defun (main args)
  (def n (xml-element (xml-name "e") Nil (list (xml-text (str "a" (list->string (list (integer->char 13))) "b")))))
  (match (node->xml-string n)
    ((Ok s) (match (parse-xml s) ((Ok d) (println (str (->str (document-root d)) " equal? " (->str (node=? (document-root d) n))))) ((Err e) (println (->str e)))))
    ((Err e) (println (->str e))))
  0)
```
- Observed: written `<e>a\nb</e>`; reads back `(e "a\nb")`, `equal? False`. The same happens for `"a\r\nb"`.
- Expected: `<e>a&#xD;b</e>`, read back equal.

### XML writer: leading whitespace of PI data is lost
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/text/xml/write.bjo:120-128 (`pi-problem`)
- Description:
  - `XmlWriter` writes `<?t   data?>`, and XML defines the whitespace after the target as a separator. Reading back gives `"data"`.
  - The writer neither refuses leading whitespace (the way it refuses `?>`) nor documents the change.
- Repro: `(node->xml-string (xml-element (xml-name "e") Nil (list (xml-pi "t" "  data"))))`, then `parse-xml`.
- Observed: written `<e><?t   data?></e>`; back `(e (*PI* t "data"))`, not equal.
- Expected: refusal, or documented normalisation.

### XML writer: an element in the xml or xmlns namespace is written with an illegal default-namespace declaration
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/text/xml/write.bjo:207-213 (`element-prefix`). lib/text/xml.bjo:96-100 (`xml-name` accepts these namespaces for any name). The comment at xml.bjo:93 says "Nothing is ever named in it".
- Description:
  - `element-prefix` falls through to `(Tuple "" (list (Tuple "" uri)))` for these namespaces. The result is output that the module's own reader (and any conforming parser) refuses.
  - No XmlWriteError is given.
  - The fix is to use the `xml` prefix for the xml namespace, and to refuse the xmlns one either in `xml-name` or in the writer.
- Repro: `(node->xml-string (xml-element (xml-name "foo" #:ns xml-namespace) Nil Nil))`, then `parse-xml` of the result.
- Observed: `<foo xmlns="http://www.w3.org/XML/1998/namespace" />`. parse-xml of it: `malformed at line 1, column 13: Prefix '' cannot be mapped to namespace name reserved for "xml" or "xmlns".` The same happens with `http://www.w3.org/2000/xmlns/`.
- Expected: `<xml:foo />`, or an XmlWriteError/ArgumentException.

### json-codec: an `Option` field with a non-None `#:default` does not round-trip `None`
- Severity: Medium (silent data change through `Name->json` → `json->Name`)
- Status: Confirmed (repro run)
- Location: lib/text/json-codec.bjo:183-192 (`json-field/default`: `((Some JsonNull) (Ok fallback))`) together with `jt-put` (writes None as null)
- Description:
  - The encoder writes `None` as `null`, because only `#:optional` fields are dropped.
  - The decoder turns `null` into the `#:default` expression. A record with `(s None)` therefore decodes as `(s (Some "dflt"))`.
  - This contradicts the module doc's "Null is None. An Option is null when it is None ... nothing else reads or writes null".
- Repro:
```
(import (std prelude))
(import (text json))
(import (text json-codec))
(def/json-type (: P (Record (: s (Option string) #:default (Some "dflt")))))
(defun (main args)
  (println (match (json->P (P->json (P (s None)))) ((Ok q) (->str (record-ref q s))) ((Err e) e)))
  0)
```
- Observed: `(Some dflt)`.
- Expected: `None`. Either a JSON null should reach the field's own instance when the field type is an Option, or the encoder should drop a field equal to None.

### JSON reader: out-of-range numbers become ±Infinity, and `-0` loses its sign
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/text/json.bjo:380-390 (`read-number`)
- Description:
  - `1e400` reads as `JsonFloat Infinity` and `-1e400` as `-Infinity`. These are values JSON cannot express, and the conversion is silent.
  - `-0` reads as `JsonInt 0`, while `-0.0` keeps its sign.
  - RFC 8259 allows limits, but silently answering a non-JSON value is surprising, and the doc says nothing about it.
- Repro: `(parse-json "1e400")`, `(parse-json "-0")`.
- Observed: `JsonFloat Infinity`, `JsonInt 0`.
- Expected: a refusal, or documented behaviour, for the overflow; `-0` as `JsonFloat -0.0` (or documented).

### JSON reader: an error message embeds a NUL character for a non-ASCII escape
- Severity: Low
- Status: Confirmed (repro run)
- Location: lib/text/json.bjo:253-268 (`read-escape` uses `(peek rd)`, which answers `#\nul` for anything ≥ 128)
- Repro: `(parse-json "\"\\é\"")`
- Observed: `line 1, column 3: \<U+0000> is not an escape`.
- Expected: `\é is not an escape`, using `found`.

### bjo: different git URLs share one clone directory
- Severity: Medium (a dependency can silently be built from another repository's tags)
- Status: Confirmed (repro run, on a copy of bjo/git.bjo)
- Location: bjo/git.bjo:94-102 (`url->directory-name`, `clone-directory`); used by `ensure-clone` at git.bjo:107-120
- Description:
  - Every non-alphanumeric character becomes `_`. The comment claims "two URLs that differ anywhere differ here", which is false.
  - `https://github.com/foo/bar-baz` and `https://github.com/foo-bar/baz` (and `b-c` / `b_c` / `b.c`) map to the same directory.
  - `ensure-clone` returns an existing directory without checking its remote. The second package reads the first one's tags and manifests.
  - `package-at-tag` refuses this only if the manifest names differ. A fork with the same package name is used silently.
- Repro (bjo/t1.bjo):
```
(import (std prelude))
(import "git.bjo")
(defun (main args)
  (println (clone-directory "/d" "https://example.com/a/b-c"))
  (println (clone-directory "/d" "https://example.com/a/b_c"))
  (println (clone-directory "/d" "https://example.com/a/b.c"))
  0)
```
- Observed: all three print `/d/git/https___example_com_a_b_c`.
- Expected: three distinct directories, e.g. by escaping `_`, or by adding a hash of the URL.

### bjo: packages `(a b)` and `(a.b)` share a materialised and published directory
- Severity: Low
- Status: Confirmed (repro run) for `package-directory`. Suspected (from reading) for publish.
- Location:
  - bjo/git.bjo:225-228 (`package-directory` joins segments with `.`)
  - bjo/publish.bjo:77-82 (`package-prefix`, same join)
- Description:
  - A manifest name is a list of bjodat symbols, and a symbol may contain `.` (`net8.0` is the documented example).
  - `(a b)@1.0.0` and `(a.b)@1.0.0` therefore both live in `.bjo/pkg/a.b@1.0.0`, and both publish to `packages/a.b`.
  - `materialise` deletes and replaces one with the other when the commits differ.
- Repro: `(package-directory "/d" (list 'a 'b) (version 1 0 0))` and `(package-directory "/d" (list 'a.b) (version 1 0 0))`.
- Observed: both `/d/pkg/a.b@1.0.0`.
- Expected: distinct directories.

### bjo: version components overflow silently; version-compare can overflow
- Severity: Low
- Status: Confirmed (repro run)
- Location:
  - bjo/manifest.bjo:104-122 (`parse-version`: `(+ (* acc 10) ...)` on an int, with no bound)
  - manifest.bjo:126-134 (`version-compare` subtracts ints)
- Description:
  - `"4294967297.0.0"` parses as `1.0.0`, and `"2147483648.0.0"` as `-2147483648.0.0`.
  - With such values `version-compare` overflows: 2147483647.0.0 against the wrapped value gives -1.
  - A tag like `v4294967297.0.0` is rejected by `tag->version`'s round-trip check, but a manifest's `(version "...")` and its constraints are not checked that way.
- Repro (bjo/t1.bjo): `(parse-version "4294967297.0.0")`, `(parse-version "2147483648.0.0")`, and `version-compare` of `2147483647.0.0` with the latter.
- Observed: `1.0.0`, `-2147483648.0.0`, `-1`.
- Expected: refusal of an oversized component, and compare via `<`/`>` rather than subtraction.

### bjo: lock comparison ignores the source, so a changed URL leaves a stale lock (or a misleading "tag moved" error)
- Severity: Low
- Status: Suspected (from reading)
- Location: bjo/lock.bjo:107-131 (`lock-changes` compares versions only); bjo/resolve.bjo:687-720 (`lock-entry`)
- Description:
  - If a manifest changes a dependency's git URL but keeps the version, `lock-changes` reports nothing, so `bjo.lock` is never rewritten. It keeps the old URL, and `--locked` passes.
  - If the new repository's tag points at a different commit, `lock-entry` reports "no longer points at the commit bjo.lock names ... a tag can be moved". The real cause is that the source changed.
  - The source should be part of the comparison: `source-key` of the old against the new.

### bjo: version constraints on path dependencies are never checked
- Severity: Low
- Status: Suspected (from reading)
- Location:
  - bjo/resolve.bjo:248-259 (`dependencies-of` records a `Need` for every dependency)
  - resolve.bjo:631-670 (`check-bounds`/`check-every-bound` only iterate `chosen`, which `choose` builds from git nodes only)
- Description:
  - Example: `(package (name (x)) (version (version-at-least "2.0")) (source (path (dir "../x"))))`, where `../x/manifest.bjodat` says `(version "1.0.0")`.
  - That builds without a word, because the constraint is recorded but never compared with the path package's manifest version.


---

