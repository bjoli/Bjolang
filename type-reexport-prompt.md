# Let a module re-export a type

> **Implemented.** All three phases. `(re-export SomeType)` binds the spelling,
> a union's cases come with it, and `(text bjodat)` needs one import. What
> follows is the prompt as written, with two corrections marked where they
> belong and a note at the end of what was found doing it.

## Context

A module can publish a name it did not define. `re-export` does it for a
binding, and the facade pattern in `Docs/MODULES.org` is built on it: the
metadata carries the *origin*, the consumer emits a reference straight into the
defining class, and the assembly is linked because a compiled library's
dependencies are transitive.

It does not do it for a type. `(re-export SomeType)` answers:

```
Re-export Error: 'SomeType' is not in scope. A re-exported name must be
imported by this module.
```

which is true only in the sense the check means it: `DReExport` in
`Inference.fs` looks in `env.Bindings`, and a type is not there.

`export` does not do it either. `(export SomeType)` in a module that only
*imported* that type declares a **second** type of the same name, and the two
then refuse to unify:

```
Type error: these types do not match.
  a/Thing
  b/Thing
```

This is deliberate, and `Exports.fs` says so where the decision is made:

> A declaration is published under the key it was given, and a key names the
> module that declared it — so republishing a dependency's would offer an
> importer a second copy of something that is already reachable, under a name
> that says where the first one lives. What an importer needs from a type it
> never imported is that the signatures mentioning it resolve, and a key
> resolves to itself.

That reasoning is right, and it is why re-exporting a *binding* whose signature
mentions a foreign type already works. `bjodat-key : (-> Keyword Bjodat)`
crosses two module boundaries with no trouble at all, because the signature
carries the key `bjodat_core/Bjodat` and the key resolves to itself.

**The gap is narrower than "re-export a type", and naming it correctly is most
of the design.** What fails is a *source-level type annotation, written in the
importing module, naming the bare spelling*. The type is reachable; the
**spelling** is not bound. So what is wanted is not a way to copy a
declaration. It is a way to bind a name, in the importing module's type
namespace, to a key that belongs to somebody else — which is exactly what a
type alias is.

## What has already been tried, so it is not tried again

All of this was run. `scratch/reexp/` holds the working half as three small
modules: `a` declares, `b` is the facade, `c` imports only `b`.

| attempt | result |
|---|---|
| `(export Thing)` in the facade | declares a second type; `a/Thing` vs `b/Thing` |
| `(re-export Thing)` | "not in scope" — `DReExport` sees bindings only |
| `(type (: Thing Thing))` in the facade | "a and b each declared one" — read as a declaration, not an alias |
| `(prefix-types "a.bjo" "Core")` + `(type (: Thing CoreThing))` + `(export Thing)` | **works.** `c` names `Thing` in a signature and it resolves to `a/Thing` |
| `(:alias Named CoreNamed)` for a case | refused: "a constructor follows its type" |
| writing the key `a/Thing` in `c`'s source | parses, does not resolve: `a/Thing` vs `a/Thing`, printed identically and not unifying |

Two things follow, and both matter to the implementation.

**The feature already exists in one spelling.** A prefixed import plus a local
alias does precisely what is wanted. So the work is not to invent a mechanism —
it is to let `re-export` reach the one that is there, without making the author
write `prefix-types` and invent a throwaway prefix.

**The last row rules out the obvious implementation.** It would be natural to
serialize `(type (: Bjodat bjodat_core/Bjodat))` into the metadata's
`TypeDecls` and let the importer re-read it as source. That will not work: a
qualified key written in source text is parsed as an ordinary name and resolves
to a fresh type in the reading module, which is what the identical-looking
`a/Thing` vs `a/Thing` error is. Whatever carries the alias has to bypass
source-level name resolution.

> **Correction, written after the work was done.** This paragraph is wrong, and
> it is why the bjodat split was made in the first place. A key serialized into
> *metadata* resolves perfectly well on the far side — the fourth row of the
> table above does exactly that, and always has: what `b.dll` publishes is
> `(type (: BjoMod....b__Thing BjoMod....a__Thing))`, and `a`'s key is read back
> as `a`'s key. What fails is a key written in a *source file*, where `a/Thing`
> is lexed as one ordinary identifier and keyed to the reading module. Source
> and metadata are not the same reader, and the failure of one said nothing
> about the other.
>
> So the implementation does serialize a declaration — the *whole* declaration
> rather than an alias to it, and under the origin's key rather than this
> module's. It goes in a list of its own, `ReExportedTypes`, rather than in
> `TypeDecls`: everything in `TypeDecls` is re-keyed to the module reading it,
> and one of these must not be. See `ModuleMetadata.ReExportedType`.

## Where the code is

- `Parser.fs:5483` — `re-export` already parses a bare name list. **No change
  needed here.**
- `Parser.fs:120` — `| Alias of FType`, the `TypeKind` a `(type (: New Old))`
  becomes. This is the thing to produce.
- `Parser.fs:350` — `AliasKind`, with `AliasType` and `AliasConstructor` among
  its cases, and `DImportAlias` at 523. **This is the existing mechanism that
  binds a local spelling to a foreign type or constructor**, and it is what
  `prefix-types` goes through. Reuse it rather than building a second path.
- `Inference.fs:5759` — `DReExport`. The check is
  `Map.containsKey name env.Bindings`; it needs to consult the type namespace
  as well, and on a type register an alias instead of demanding a binding.
- `Exports.fs:90–145` — `ownModuleDecls`, `ownTypeNames`, `typesToExport`. This
  is where "not exported" is told from "not mine", and it is deliberately
  restricted to declarations this module made. A re-exported type must **not**
  join `typesToExport`; it needs a list of its own.
- `Exports.fs:784` — where `typesToExport` is serialized into `TypeDecls`.
- `ModuleMetadata.fs:124` — `TypeDecls: string list`, and the `Metadata` record
  generally. Note the comment at `BlockingDefs`, which already describes a
  re-exported facade as "a signature to publish and no body": the shape for
  carrying an origin exists, in the binding namespace.

## Phases

### 1. `(re-export SomeType)` binds the spelling

Accept a type name in a `re-export` list. Its effect is the alias that already
works — a local spelling resolving to the origin's key — and nothing else. No
declaration is published, no second type exists, `typesToExport` is untouched.

This is the whole win. Land it, test it, and stop.

The metadata has to carry enough for the importer to bind the spelling to the
key. Since a serialized source declaration cannot (see above), this is most
likely a new list on `Metadata` — spelling and origin key — read on the far
side into the same table `DImportAlias`/`AliasType` writes into. Bump
`Version`; there is a version field and this changes what a reader must
understand.

### 2. Decide about constructors, out loud

`(re-export Bjodat)` binds the spelling `Bjodat`. Does it also bind `BjoInt`,
`BjoStr` and the rest?

There is a real argument each way. *For:* "exporting a union exports its cases"
is already the rule for a declared type, and a re-exported union that arrives
without its cases is a type you cannot take apart. *Against:* a constructor is
a binding, and the facade's author asked for one name, not eleven.

The machinery is not the obstacle — `prefix-types` re-keys a union's cases
along with its type, so the path exists and would be reused with an identity
renaming. What is needed is a decision, written down, and an error message as
good as the one that is there now:

```
Alias Error: 'CoreNamed' is a constructor, and (:alias ...) makes a second
spelling of a def or a macro. A constructor follows its type: import its
module with (prefix-types ...) to change what it is called.
```

If the answer is "no, cases do not come", say that at the point of failure and
point at `prefix-types`, exactly as this one does.

### 3. Correct the bjodat split

**This is the part that exists because of a wrong belief, and it should be
revisited once phase 1 lands.**

`lib/text/` currently holds two modules:

- `(text bjodat)` — `def/bjodat-type`, the traits, and the decoders it generates
- `(text bjodat-core)` — the `Bjodat` value, the reader, the writer

and **both must be imported together**. The note at the head of
`lib/text/bjodat.bjo` and the "The two modules" section of
`Docs/std/bjodat.org` both state that one import is impossible, and the commit
that made the split says the same thing. That claim is **wrong**: the
`prefix-types` plus alias route was not found at the time. The generated code
names exactly two things it cannot bring with it — `Bjodat` in
`(-> Name Bjodat)` and `Reader` in `(-> Reader ...)` — and phase 1 is precisely
what lets `(text bjodat)` hand those on.

What to do once phase 1 is in:

- `(text bjodat)` re-exports `Bjodat` and `Reader`. A program that declares a
  record with `def/bjodat-type` and calls `bjodat-parse-<Name>` then needs
  **one** import.
- The generated encoder already avoids naming any case: it calls `bjodat-key`
  and `bjodat-entries`, which are ordinary bindings, and those were written for
  this exact reason. Do not undo them.
- Until phase 2, a program writing a `->bjodat` instance **by hand** still
  needs `(import (text bjodat-core))` for `BjoInt` and friends. That is the
  rarer case and the docs should say so rather than making everyone pay for it.
- Then fix the three places that assert the impossibility: the module header
  comment, `Docs/std/bjodat.org` under "The two modules", and — since a commit
  message cannot be edited — a line in `bench/BASELINE.md` or the doc saying
  what was actually true.

Whether the two modules should stay two is a separate question and probably
yes: `bjodat-core` is the value type, the reader and the writer, and a program
that only needs to read data of unknown shape has no use for the macro. The
split is fine. What is wrong is that it is forced on every consumer.

## Non-negotiable constraints

- **One type, not two.** A value built through the facade's spelling and one
  built through the origin's must unify, silently and everywhere. The
  `a/Thing` vs `b/Thing` failure above is the thing being fixed and must not
  reappear in a new form.
- **No second declaration in the metadata.** `Exports.fs`'s comment explains
  why, and it is still right: a republished declaration offers an importer a
  second copy of something already reachable. A re-export publishes a
  *spelling*.
- **`#:opaque` survives the trip.** A type exported opaque and then re-exported
  must still be opaque two modules along. If that is hard, refuse it loudly
  rather than leaking the representation.
- **`prefix-types` plus a local alias keeps working.** It is the documented
  answer today, people will have written it, and the new path should be built
  on the same mechanism rather than beside it.
- **Linking does not change.** Dependencies are already transitive, which is
  what makes a re-exported binding's reference resolve. Nothing here needs a
  new assembly reference.
- **The errors stay good.** `Docs/MODULES.org`'s error messages are unusually
  helpful — the alias error names the mechanism *and* the workaround. Whatever
  is still refused after this should be refused that well.

## Tests

- **New**, promoted from `scratch/reexp/`: three modules where `a` declares a
  union, `b` re-exports the type, and `c` imports only `b` and names it in a
  signature. Today `c` passes on the signature and fails on `(Named "x")`;
  after phase 1 the signature case is the assertion, and after phase 2 the
  constructor case is too. `TestFiles/srcimport/` already holds multi-file
  fixtures — follow that shape.
- **New**, in `TestFiles/errors/`: `(re-export Nonsense)` where the name is
  neither a binding nor a type, asserting the message still says what to do.
- A round trip through a `.dll`, not only through source imports. The metadata
  is the part being changed and a same-compilation test would not exercise it.
  `TestFiles/071_blanket_across_dll` is the precedent.
- `Docs/MODULES.org` has a `re-export` section and a modifier table. Both
  describe a world where types do not cross. Update them in the same change.
- Full suite via **`python3 run_tests.py`**: 196 groups, 222 error tests, 8
  warning, 23 codegen, 4 REPL transcripts, 3 staleness.

## A note on what this is not

This does not make a type *portable* — there is still exactly one `a/Thing`,
declared in one place, and every spelling of it resolves to that key. It does
not change what is linked, because dependencies were always transitive. It does
not make a facade cheaper at run time, because a facade already compiles to
nothing: the reference goes straight to the origin's class.

It makes a *name* available. The reason that is worth doing is that generated
code cannot choose its imports. A macro's template is spliced into somebody
else's module, and every type it names has to resolve there — so a library that
generates code either publishes every type its output mentions, or it makes
every consumer import a module they never asked about. `(text bjodat)` is that
library today, and `(text json)` and `(text json-codec)` have been that pair for
longer.

---

## What it turned out to be

Written after the fact, against the plan above.

**Phase 2's decision: the cases come.** A union is re-exported whole. They are
not written in the `re-export` list and could not usefully be — exporting a
union exports its cases, and this is the same rule one module further along. It
falls out of the shape rather than needing machinery: the declaration travels,
and the importer derives each case's bare spelling from the declaring module's
name, which is in the entry beside it. Nothing is published twice.

**Where it landed.**

- `ModuleMetadata.ReExportedType` — spelling, key, declaring module, and the
  serialized declaration. `currentVersion` 9 → 10.
- `Inference.fs` `DReExport` — a name that is not a binding may be a type some
  other module declared. Three refusals: this module's own type, a trait, and
  an `import/class` alias, each pointing at the form that would work.
- `Exports.fs` — the classification and the serialization. `typesToExport` is
  untouched, so the leak check goes on reading it as "what this module
  declares".
- `Pipeline.fs` — each entry becomes a `DModule` of the *declaring* module,
  placed beside the facade's module and before it, plus `DImportAlias`
  spellings for the type and every case. Beside rather than inside, because
  `registerTypeDefs` keys a declaration to whichever module it is read in and
  would otherwise key an already-keyed name twice over.
- `ImportSurface.Types` and `.Constructors` became `Map<spelling, key>`, and
  `typeRenaming` now takes the key from the surface instead of rebuilding it
  with `Naming.typeKey moduleName`. That rebuilding was right for a type the
  module declared and wrong for one it re-exported; carrying the key is also
  one derivation fewer.

**Two things found on the way.**

- *A macro template naming a re-exported binding was broken, and had been.*
  `Macro.fs` rule 2 qualifies a template's free name to `Module_Module::name`
  using the macro's own module. For a name that module re-exported there is no
  such member — a facade generates none — and the expansion fails with
  "Unbound variable". Nothing had hit it because no macro's module re-exported
  a name its templates used; `(text bjodat)` became the first the moment it
  re-exported the one-pass driver. `MacroBinding.Exports` now carries, per
  name, the module that defines it.
- *An implementation does not cross a facade.* Out of scope here and left so
  deliberately — which trait would have to travel with it is a design question
  of its own, and `bjodat-core` writes no impls. Documented under `re-export`
  in `Docs/MODULES.org` and pinned by a comment in
  `TestFiles/inc/reexport_origin.bjo`.

**Tests.** `TestFiles/216_reexport_type.bjo` over `inc/reexport_origin.bjo`,
`inc/reexport_facade.bjo` and `inc/reexport_chain.bjo` — a generic union, a
record, an `#:opaque` type, and a facade in front of a facade;
`TestFiles/217_bjodat_one_import.bjo` for the thing this was for. Three
refusals in `TestFiles/errors/reexport_*.bjo`. Suite: 198 groups, 225 error
tests, everything else unchanged.
