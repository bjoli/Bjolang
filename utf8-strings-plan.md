# Plan: Bjolang `string` becomes BjoString's UTF-8 `Utf8String`

Untracked working notes (do not commit). Library: BjoString,
`git@github.com:bjoli/bjostring.git`, local `/home/linis/Programmering/bjostring`
(AI-authored; commits there use `--author="ECA agent (AI) <git@eca.dev>"`).
Baseline before the switch: `./run_tests.py` all green (~30 s + std build).

## Decisions
- BjoString is a submodule at `BjolangRuntime/BjoString`, a ProjectReference of
  the runtime like RrbList/Map, and in `Paths.runtimeAssemblyNames`.
- Types: Bjolang `string` = `TCon("String")` ↔ `BjoString.Utf8String`
  (nullaryCorrespondence/clrToNullary/mapPrimitiveType, like `Char` ↔ BjoChar).
  CLR `System.String` keeps `TCon("System.String")` and is a separate type.
  `StringCursor` ↔ `BjoString.StringCursor` (runtime's StringCursor.cs goes),
  `StringBuilder` ↔ `BjoString.Utf8StringBuilder`.
- FFI marshaling, inserted in the typed tree at inference: a `System.String`
  (or `string[]`) parameter/receiver/property-set takes a Bjolang string with
  a conversion; a `System.String` (or `string[]`) result/property/field comes
  back as a Bjolang string. `null` from .NET becomes "". Nested CLR strings
  (`Seq`, `Func<string,…>`, `List<string>`) stay `System.String`; builtins
  `string->clr-string` / `clr-string->string` convert by hand. Overload
  scoring: Bjolang string → System.String scores like a widening.
- Literals are hoisted to `__Literals` as `Utf8String.FromUtf8("…"u8)`; a
  string pattern becomes a guard with `ContentEquals("…"u8)`; string keyword
  defaults take the non-constant path.
- `string-length`, `string-code-ref`, `stringbuilder-length`,
  `stringbuilder-code-ref`, `stringbuilder-add-code!` work in bytes.
- Regex: temporary bridge (UTF-16 .NET Regex, offsets mapped to bytes) until
  BjoRx (Phase 7 of the rx plan) replaces it.
- Ports stay TextReader/TextWriter; conversions at the boundary
  (`Utf8Text.Write`, `FromUtf16`).

## Phases
1. Submodule + build wiring.
2. Compiler: type names, codegen spellings, literals, patterns, defaults,
   entry-point args, marshaling + overload scoring.
3. Runtime: builtins on Utf8String, cursors, builder, display, ports, numbers,
   regex bridge, keywords/symbols/syntax, helpers StringFromClr/StringToClr.
4. Lib: prelude string section, eq.bjo, json/bjodat-core (UTF-16 units →
   scalars), simpletest, http, xml, datetime, bjo/ tool.
5. Tests: fix expectations, add UTF-8 semantics tests.
6. Docs (Docs/prelude.org strings, CHANGELOG), BjoManual later.

## Progress
- Phases 1–4 done enough that `./build_std.sh` builds all 35 modules.
- Marshaling as built: args via `ForeignTyping.reconcileForeignArgs`
  (`toClrString` → `BjolangRuntime.StringToClr`); method results by Codegen
  from `meta.ReturnType` (`clrStringConversion`, also in guarded calls);
  property/field reads via `marshalResult` wrapper nodes; extern values via
  `passed`/`seenType`. An import declaring `System.String` as result opts out
  (`declaresClrResult`). Out-methods and generic methods are NOT marshaled:
  declare `System.String` and convert with `string->clr-string`.
- Runtime: string builtins in `BjolangRuntime.cs` (block "String cursors" …
  `StringsToClr`), byte helpers `StringIndexOf/LastIndexOf/Substring/
  SubstringFrom`, `StringContains/StartsWith/EndsWith/Replace/Concat`;
  BjoNum on Utf8String; BjoRegex bridge (`Searched` maps UTF-16 → bytes);
  Syntax SInt/SStr/SPunct payloads are Utf8String; `Panic` takes Utf8String.
- New builtins: `string->clr-string`, `clr-string->string`,
  `stringbuilder-add-unit!` (UTF-16 unit, pairs surrogates; BjoString
  `AppendUtf16Unit`), used by json/bjodat-core readers.
- Out-methods marshal string in-params via `ExternOuts.StringIns`; dot forms
  on a Bjolang string fall back to System.String members
  (`ForeignTyping.onStringOrClrString`); `(:is string s)` works; string
  patterns in `is` contexts use `generateIsGuard` (`&&`, not `when`).
- DONE: `./run_tests.py` all green (254 runs, 406 error, 35 codegen, REPL),
  `run_bjo_tests.py` 213/213; new `TestFiles/273_utf8_strings.bjo`; docs in
  `Docs/prelude.org` and `CHANGELOG.org`. Not committed (authorship question
  pending). Untested: bjoweb/, Examples/, Playground/; BjoManual not updated.
- Committed as eccdde8 (UTF-8 switch) and 437b78c ((std rx) on BjoRx,
  submodule `BjolangRuntime/BjoRx`, HIR text from `BjoRx.Syntax.HirText`).
  Not pushed.
- Later: generic
  methods with string params are not marshaled; nested CLR strings only by
  hand.
