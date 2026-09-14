# Hoist constant literals out of method bodies

## Context

`Codegen.fs` emits a keyword and a symbol as a call, at the site each one
appears:

```fsharp
| TKeyword k -> append ctx $"BjolangRuntime.Keyword.Intern(\"{escapeStringLiteral k}\")"
| TSymbol s -> append ctx $"BjolangRuntime.Symbol.Intern(\"{escapeStringLiteral s}\")"
```

`Intern` is `ConcurrentDictionary<string, T>.GetOrAdd` over the name. So a
keyword written in a loop is a string hash and a dictionary probe *per
iteration*, for a value that was fully known when the file was compiled.
Nothing about it can change at run time: interning is idempotent, the table is
process-wide and never shrinks, and two calls with the same name answer the
same reference by construction.

This is not a niche cost. `(match k (:apple 1) (:banana 2))` pays it per arm
per call. A map keyed by keywords pays it per lookup. A record codec pays it
per field per record.

> **Correction, written after the work was done.** The `match` claim in that
> paragraph is wrong, and it was wrong when it was written. A keyword *pattern*
> is not emitted through `TKeyword`: `generatePattern` emits
> `case BjolangRuntime.Keyword { Name: "apple" }`, a string comparison, and it
> never calls `Intern`. So a keyword `match` paid nothing here and did not move
> — measured at 0.82 ns/op before and 1.07 after, see `bench/BASELINE.md`. The
> other two examples stand, and the `eq?`-written form of the same dispatch —
> `(cond ((eq? k :apple) ...))` — went from 14.7 ns/op to 0.18. That a pattern
> compares names where it could now compare references against the hoisted
> field is a real and separate opportunity, in pattern lowering, not here.

The measurement that prompted this: `lib/text/bjodat-codec.bjo` generates a
decoder that reads five keyword-keyed fields per record. Hoisting those five
keys by hand — the macro now emits a module-level `def` per key, which becomes
a static field — took decode from **66 ns to 53 ns per record**, about 20%, on
a 20 000-record corpus (`TestFiles/assets/jsonbench/formatbench.bjo`). That
was five literals. Every keyword and symbol in every program pays the same toll
and almost none of them have been hand-hoisted.

The macro-level fix is a workaround in the wrong place. The compiler knows the
literal is constant; it should say so.

## What is hoisted, and what is not

**Hoist:**

- `TKeyword` and `TSymbol`. The whole point.
- `TVecMake` **when every element is itself a constant literal**. It emits
  `Collections.RrbBuilder<T>.FromArray(new T[] { ... }, true)`, which allocates
  an array and a list on every evaluation, and a `Vec` is immutable, so one
  instance can serve every evaluation. This is a second, independent win; do it
  in its own phase and measure it separately.

**Do not hoist:**

- `TChar` — `new Bjolang.Runtime.BjoChar(n)` is a struct construction from a
  constant. There is nothing to save and a static field would be slower.
- `TString` — the CLR already interns literals in metadata.
- **Array literals (`#[...]`).** `Docs/Syntax.org` is explicit that an array
  literal is an expression and each evaluation allocates an array of its own,
  and `036_array.bjo` and `187_array_literals.bjo` test exactly that. Hoisting
  one would be a correctness bug, not an optimisation. A `Vec` is safe for the
  opposite reason: it cannot be written to.
- Anything whose elements are not literals. A vec whose elements mention a
  module-level `def`, a parameter or a call is not constant, and hoisting it
  would both change evaluation order and create an initialisation-order
  dependency. Restrict the vec case to literals-of-literals and nothing
  cleverer.

## Where the fields go, and the one trap

The emitted module class already has static fields. `Codegen.fs` around 4822
declares `public static readonly T name;` for each module-level `def`, and
around 4838 assigns them inside an **explicit static constructor**:

```csharp
static bjodat_Module() {
    /* def initialisers, in declaration order */
}
```

Hoisted literals must be emitted as **field initialisers**, not as statements
added to that constructor:

```csharp
private static readonly BjolangRuntime.Keyword __kw_name =
    BjolangRuntime.Keyword.Intern("name");
```

The reason is `beforefieldinit`. A class with only field initialisers gets the
`beforefieldinit` flag and the runtime may initialise it early and, crucially,
elides the initialisation check on every static access. A class with an
explicit static constructor does not: every access to any static member gets a
check. Most modules have no module-level `def` today and therefore no static
constructor, and **adding one to carry hoisted literals would make every static
access in that module slower** — which is the opposite of the point, and would
not show up in any behavioural test.

Field initialisers also solve the ordering question for free: C# runs them
before the static constructor body, so a `def` whose initialiser mentions a
hoisted literal still sees it initialised. Do not break that by moving them.

## Scope and deduplication

One field per distinct literal **per emitted class**, not per module. A literal
inside an `impl` class, a dictionary class or a trait class belongs to that
class; there is no module class in scope to hang it on.

Two cases need a decision rather than a default:

- **Generic classes.** A static field in `Foo<T>` exists once per closed
  instantiation, so N instantiations means N fields interning the same name. It
  is correct — interning is idempotent — but it is not free, and for a widely
  instantiated generic it is worse than the status quo on metadata size while
  no better on speed after the first call. Consider hoisting to a single
  non-generic holder class (one per assembly, e.g. `__Literals`) instead of to
  the enclosing class. That also simplifies deduplication to one table. Weigh
  it against the extra indirection and pick one; say which in the commit.
- **Colour copies.** A `defbjouble`, or any function that gets a generated
  `__bjo` copy, emits two method bodies into the same class. The field must be
  emitted **once** and referenced by both. Deduplicate on the literal, not on
  the use site.

Field names must be derived deterministically from the literal text (sanitised)
plus a counter only where sanitising collides. The codegen tests diff emitted
output, so unstable names would make them flap.

## Non-negotiable constraints

- **No behavioural change whatsoever.** `eq?` on keywords is reference
  equality; a hoisted literal and a `(string->keyword "name")` built at run
  time must still be the same reference. This follows from `Intern` being
  idempotent, and `TestFiles/034_keywords_and_symbols.bjo` already asserts it —
  it must keep passing untouched.
- **No new static constructor.** A module that has none today must still have
  none after this. Assert it in a codegen test; nothing else will catch it.
- **`#line` mapping stays coherent.** Hoisted fields are new lines at class
  scope, emitted before the methods. Check that a stack trace from inside a
  method still maps to the right `.bjo` line, since `Build.fs` emits portable
  symbols in both configurations and people read those traces.
- **The REPL keeps working.** Each entry compiles its own assembly, so fields
  are per-assembly and a keyword hoisted in entry 3 is the same reference as
  the one hoisted in entry 7. The four REPL transcript tests must pass
  unchanged.
- **Assembly size is measured, not assumed.** One field plus one initialiser
  per distinct literal. Report the delta on `lib/std/prelude.dll`, which has
  more distinct symbols than anything else in the tree.

## Phases

1. **Keywords and symbols, enclosing class, field initialisers.** The whole
   win is here. Land it, measure it, and stop.
2. **Constant vec literals.** Independent of phase 1 and separately
   measurable. Only literals-of-literals.
3. **Remove the workaround.** `lib/text/bjodat-codec.bjo` hoists its record
   keys by hand: `jb-key-ref` names a per-field static, `jb-key-decls` emits
   `(: k Keyword)` / `(def k :name)` pairs, and `jb-expand` splices them ahead
   of the record type. Once the compiler hoists, that is dead weight — delete
   it, put the keyword literal back inline in `jb-chain` and `jb-enc-chain`,
   and confirm decode stays at ~53 ns/rec. If it regresses to 66, the compiler
   is not hoisting what the macro was, and phase 1 is not finished.

## Tests

- **New**: `TestFiles/codegen/literal_hoisting.bjo`, in the existing
  `;; EXPECT-CS: <regex>` style. Assert (a) a static field is emitted for a
  keyword, (b) the use site names the field instead of calling `Intern`, (c) a
  keyword used twice emits one field, (d) a keyword in a `defbjouble`'s two
  bodies emits one field, (e) a module with no `def` and one keyword literal
  emits **no** `static ClassName()`.
- **Existing**: no codegen test currently asserts on `Intern`, so none should
  need editing. If one does, that is a signal the change reached further than
  intended — read it before changing it.
- `034_keywords_and_symbols.bjo` is the identity test — both spellings interning
  to one value, `eq?`, pattern matching, `string->keyword` reaching the literal's
  value. `103_metadata_round_trip.bjo` carries a `Keyword` across a module
  boundary. `215_hash_macros.bjo` matters for a different reason: its keyword and
  symbol literals arrive at codegen through *macro expansion*, which is the path
  a hand-written literal does not test. All must pass unchanged.
- Full suite via `run_tests.py`: 196 groups, 222 error tests, 8 warning tests,
  22 codegen tests, 4 REPL transcripts, 3 staleness.

## Measurement

Before and after, on the same machine, best-of-20 with a `GC.Collect` before
each rep and at least five warm-up passes — the tiered JIT and the collector
between them are worth more than the effect being measured, and a harness
without both has already produced a "total faster than the parse it contains"
in this tree.

- `TestFiles/assets/jsonbench/formatbench.bjo` — the decode column, which is
  where the 66→53 came from.
- `TestFiles/assets/jsonbench/codecbench.bjo` — the same split for JSON.
- **A new one worth having**: a keyword-dispatching `match` in a tight loop.
  That is the case nobody has hand-hoisted, so it is where the honest
  improvement shows. `match-kw` in `034_keywords_and_symbols.bjo` is the shape;
  put it in a loop over a few million iterations.

Append the rows to `bench/BASELINE.md` alongside the existing entries.

## A note on what this is not

This does not make keyword comparison cheaper — that is already reference
equality (`Keyword.Equals` is `ReferenceEquals`, `GetHashCode` is
`RuntimeHelpers.GetHashCode`). It makes *obtaining* the keyword free at sites
where the name was a constant all along. The reader in `lib/text/bjodat.bjo`
is the opposite case and is deliberately out of scope: it reads a name from a
port, so the name is not known until run time and no hoisting can help. That
one is handled by `BjoNameCache` in `BjolangRuntime/BjoText.cs`, which caches
the buffer-to-string step, and which would still pay `Intern`'s probe even
after this work — because a `.NET` method may not return a `Keyword` to
Bjolang at all. `DotNetInterop.clrToNullary` maps reflected types back to
builtin ones for `char` and deliberately nothing else. Widening that table is a
separate decision with its own trade-off, and is **not** part of this work.
