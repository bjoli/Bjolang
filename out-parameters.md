# Task: explicit `out` parameters in `import/extern`, and move the runtime's `(bool, T)` tuples onto them

## Background

`import/extern` cannot call .NET methods that have `out`, `ref` or pointer
parameters. `DotNetInterop.fs` maps byref types to `<byref>`, and
`genericMethods`, `callableMethods` and the constructor filter drop every method
that has a byref parameter.

The runtime works around this by returning `(bool found, T value)` tuples. Bjolang
code imports them as `(Tuple bool %v)` (the `/pair` bindings) and wraps them into
`Option` by hand. On a miss, such a tuple carries `default(T)`, which is `null` for
reference types. So a value that is illegal in Bjolang reaches Bjolang code. This
task closes that hole.

## Design (follow exactly: no inference, no new flags)

An import signature may mark a parameter as `(out T)`, in the same position the
C# parameter has. `(out T)` is NOT an argument at the call site. The binding's type
is the declared arrow with every `(out T)` removed.

The declared result says how the outs come back. There are exactly two forms. The
compiler checks the declaration against the C# method and never picks a form itself.

### 1. Option form: for `bool TryXyz(..., out T)`

- The C# return type must be `bool`.
- The declared result is `(Option T)` for one out, or `(Option (Tuple T1 ... Tn))` for several.
- True gives `Some`, false gives `None`. Out values are read ONLY when the call returned true.

```scheme
(parse-int (: System.Int32.TryParse (-> string (out int) (Option int))))
(dict-try-get (: System.Collections.Generic.Dictionary.TryGetValue
                 (-> (Dict %k %v) %k (out %v) (Option %v))))
```

Generated C# shape: `int.TryParse(s, out var t0) ? Some(t0) : None<int>()`

### 2. Tuple form: for everything else that has outs

- The declared result is the C# return value followed by the outs, in C# parameter order.
- A `void` return contributes nothing.
- If exactly one element remains, the result is that element alone. Otherwise it is a `Tuple`.

```scheme
;; int Math.DivRem(int a, int b, out int result)
(div-rem (: System.Math.DivRem (-> int int (out int) (Tuple int int))))
;; void KeyValuePair<K,V>.Deconstruct(out K key, out V value)
;; (spell the struct type the way the codebase spells CLR generic types)
(kv-parts (: System.Collections.Generic.KeyValuePair.Deconstruct
             (-> (KeyValuePair %k %v) (out %k) (out %v) (Tuple %k %v))))
```

Generated C# shape: `(Math.DivRem(a, b, out var t0), t0)`

## Rules

Each broken rule is a compile error. The message must name the rule and show the
C# signature.

1. **Where `(out T)` may appear.** Only in a parameter position of an
   `import/extern` signature. It is special-cased the same way `-?->` is in
   `Annotations.fs`, and `parseType` rejects it everywhere else.
2. **What it matches.** `(out T)` matches only a parameter with
   `ParameterInfo.IsOut`. A `ref` or `in` parameter never matches.
3. **`ref` stays unsupported.** Methods with `ref` parameters remain unsupported,
   and their error must say that `ref` parameters need a C# shim (example:
   `Socket.ReceiveMessageFrom`).
4. **Every out must be declared.** Every C# out parameter must appear in the
   declaration, with matching count and positions.
5. **The Option form needs `bool`.** The Option form requires a C# `bool` return.
6. **Garbage-on-false outs force the Option form.** The Tuple form is an error when
   any out parameter carries `[MaybeNullWhen(false)]` or `[NotNullWhen(true)]`
   (from `System.Diagnostics.CodeAnalysis`, read through reflection). Those
   attributes say the out is garbage on false, so the Option form is required.
7. **Check, never rewrite.** The declared result must be exactly what the form
   produces: same types, same order, same count.
8. **No async outs.** Outs together with `#:async` or `#:cancellable` are an
   error. Check whether `#:blocking` needs anything special; if unsure, make it an
   error too and say so in the report.
9. **No constructors.** Constructors with out parameters remain unsupported.
10. **The marker decides the overload.** An explicit `(out T)` must select the
    out-overload even when a same-named overload without it exists. Both of these
    must resolve correctly from their declarations:
    - `Math.DivRem(int, int)`, which returns a ValueTuple on .NET 6+
    - `Math.DivRem(int, int, out int)`

## Codegen

- **Unique names.** Out locals get gensym'd names (`Gensym.fs`). A C# `out var`
  stays visible for the rest of the enclosing statement, so two such calls in one
  statement must not clash.
- **Option form.** Read the out locals only in the true branch.
- **Void methods with outs.** A `void` method with outs cannot be a C# tuple
  element. Where an expression is needed, use temporaries or a local function,
  following the pattern Codegen already uses for statement-only constructs.
- **Some/None.** Reuse the existing Some/None emission (`BjolangRuntime.Some<T>` /
  `None<T>`).
- **Generic outs.** Generic out types such as `(out %v)` are solved from the
  declared signature, the same way generic methods are.
- **Both colours.** Verify that the generated C# compiles in both the sync and the
  async twins.

Starting points (verify them; they are pointers, not a spec):
- `DotNetInterop.fs`: `mapClrType` (~419), `genericMethods` (~733),
  `callableMethods` (~1036), the constructor filter (~1227)
- `Annotations.fs`: how `-?->` is special-cased
- `ForeignTyping.fs`: `resolveExternMethod` and the declared-signature check
- `Codegen.fs`: extern call emission (~1790–1930)

## Part 2: move the runtime and stdlib onto the new form

Convert every runtime method that returns a tuple whose first element is a bool
success flag into idiomatic C#: `bool TryXyz(..., out ...)`, with the outs marked
`[MaybeNullWhen(false)]`. Re-import each one with the Option form.

Delete the hand-written `/pair` wrappers, so that each exported name is the import
itself (or a thin alias) and keeps its current Option type. Update every caller
inside the stdlib, such as `(def (found k v) ...)`.

### Search the WHOLE repository

The list further down is incomplete; at least the Map, OrderedMap and Set C#
sources are missing from it. Search for at least:
- **C#:** return types `(bool`, `ValueTuple<bool` and `Tuple<bool`, plus every
  method named `Try*`.
- **.bjo and .protobjo:** `(Tuple bool`, bindings ending in `/pair`,
  `(def (found`, and `(if found (Some`.
- **Also:** Examples, lib/ and the tests.

### Known occurrences

- **BjoMutable.cs:** `MutableVecModule.TryGet`, `TryPopBack`, the dictionary
  module's `TryGet` (~153), and the PriorityQueue `TryPeek`/`TryPop`. The last two
  return `(bool, T, P)` and become `(Option (Tuple %a %p))`.
- **prelude.bjo:** `map-try-ref/pair` (`Map.MapModule.TryGetValue`),
  `map-try-find` (`Map.MapModule.TryFind`), `transientmap-try-ref/pair`.
- **orderedmap.bjo:** `orderedmap-try-ref/pair`, `-try-min`, `-try-max`,
  `-try-successor`, `-try-predecessor`, `-try-find`,
  `transientorderedmap-try-ref/pair`.
- **orderedset.bjo:** `-try-min`, `-try-max`, `-try-successor`,
  `-try-predecessor`, `-try-find`, `transientorderedset-try-min`/`-try-max`.
- **set.bjo:** `set-try-find`.

### Notes

- **Exported raw tuples.** Some raw tuple bindings are exported. For example,
  `map-try-find` is in the prelude's export list and returns `(Tuple bool %k %v)`.
  Change it to the Option form. It may then be identical to `map-find`. Do not
  delete either one; list such duplicates in the report.
- **Hot paths.** Map lookups are hot. The generated C# for `map-try-ref` must be at
  least as good as today: no allocation, no extra call layers, and inlining must
  still apply. If a benchmark exists, run it before and after.

## Tests

Compile-and-run tests for:
- `Int32.TryParse`, both hit and miss
- `Dictionary<string,string>.TryGetValue` missing on a reference type: the result
  is `None`, and no null reaches Bjolang code
- `Math.DivRem`, both overloads, each from its own declaration
- a void method with two outs
- every error listed in the Rules section
- stdlib: `map-try-ref`, `orderedmap-min`, and priority queue pop behave exactly as before

## Docs

Document `(out T)` next to the other import flags (`#:async`, `#:get`, `#:set`):
both forms, the rules, and the errors.

## Report back

- every exported binding whose type changed
- duplicates such as `map-try-find` / `map-find`
- runtime methods you found but did not convert, and why
- any rule above that turned out impossible or ambiguous

## Non-goals

- `ref`, `in`, pointer and Span/ref-struct parameters
- constructors with outs
- inferring the result form
- any flag such as `#:try`
