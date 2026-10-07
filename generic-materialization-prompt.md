# Task: compile a generic type's conditional `Eq` and `Ord` into its .NET members

`Map`, `Set`, `OrderedMap` and `OrderedSet` are .NET collections: they never
call `=` or `compare`, only the key type's `Equals`, `GetHashCode` and
`CompareTo`. For a type a module declares, the compiler writes the type's `Eq`
and `Ord` implementations into those members ("materialization", in
`Codegen.fs`), so the collections agree with `=`.

A *conditional* implementation — one with a `(where ...)`, which is what every
implementation for a generic type has, derived or hand-written — is skipped,
because nothing inside `Pair<T>.Equals` can build the `Eq<T>` dictionary the
`where` asks for. Measured on the current compiler:

| Case | `=` / `compare` | Collections |
|---|---|---|
| hand-written `Eq` on `(Edge %a)`, where (1 2) = (2 1) | equal | a set of both has 2 elements; a map misses the lookup |
| `type/derive (Eq Ord)` on `(Pair %a)`, as an `OrderedMap` key | works | crashes: "At least one object must implement IComparable" |
| hand-written `Ord` on `(Job %a)`, as an `OrderedMap` key | works | the same crash |
| `type/derive (Eq)` on `(Pair %a)` in a `Set` | equal | right, by luck: C#'s field-wise `Equals` agrees |
| a union whose `Eq` makes two *different cases* equal | equal | never equal: out of scope, see "Decided" 7 |

This task materializes conditional implementations, so the first three rows
come right, and writes down the union rule.

About me: English is not my first language, and I am not trained in compiler
design. When you make a design choice, explain what it rules out, with a
concrete example.

## Read first

- `CLAUDE.md`: comment style ("this does this, because later that"), no
  references to conversations in comments, and prompts such as this file are
  never committed.
- `Codegen.fs`, section "Materialization" (around lines 4545–4720):
  `materializableImpl` (the `target.Constraints.IsEmpty` test is what skips a
  conditional implementation), `MaterializeTarget`, `materializedMembers`,
  `materializedInterfaces`, `materializedBody`.
- `Codegen.fs`, the `TType`/`TTypeRec` branch of `generateDecl` (around
  4926–5085): where a record, a record struct and a union are emitted with the
  materialized members, and the mutable-record `hashed` rule that writes a
  throwing `GetHashCode`.
- `Codegen.fs`, the implementation-class emission (around 5370–5420): a
  conditional impl's class takes its dictionaries as constructor arguments in
  `dictFields` order, and has a static `Make`.
- `CheckDecl.fs`, around 2080–2125: why `Eq`/`Ord` for a type from elsewhere
  is refused, and the `lib/std` carve-out ("their two answers cannot part
  ways"), which is the fact this task's stand-ins rely on.
- `lib/std/eq.bjo`: the two traits, the blanket `Eq` (`clr-equals`), and the
  `Ord` implementations (strings compare *ordinally*).
- `BjolangRuntime/OrderedMap/OrderedMap/DefaultOrder.cs`: the ordered
  collections' default, ordinal for strings and `Comparer<T>.Default` else.
- `Docs/prelude.org`, "Equality", "The blanket", "Writing one by hand" and
  "`type/derive`". "Writing one by hand" says a conditional implementation
  keeps C#'s synthesized members; that sentence changes.
- `TestFiles/codegen/materialization.bjo` and `TestFiles/codegen/mutable_fields.bjo`:
  they pin today's behaviour (`public record materialization__Box<T_a>(T_a item);`
  and the throwing hash on `mutable_fields__Box<T_a>`), and change on purpose.
- `TestFiles/128_derive.bjo`, `139_ord.bjo`, `195_ord_materialization.bjo`.
- `lib/std/mutable/deque.bjo`: `(Deque %a)` is a declared generic record with
  mutable fields and a conditional `Eq` whose `eq-hash` raises `unhashable`.
  `MutableVec`, `MutableSet` and `MutableMap` are .NET types and are not
  affected.
- The reproductions in `/tmp/todo/` (`j_edge.bjo`, `k_ordgeneric.bjo`,
  `l_derived.bjo`, `i_eqkeys.bjo`), if they are still there. Recreate them
  from the table above if not.

## Ground rules

- Build: `dotnet build -c Release` after a compiler change, then
  `./build_std.sh`, then `./run_tests.py` (about 40 s) and `bjoweb` (in
  `bjoweb/`: `../bjo/bjo build`, `../bjo/bjo run tests/html.bjo`,
  `../bjo/bjo run tests/demo.bjo`). Green before every commit.
- One edit call per file per turn. Parallel edits to one file have raced.
- To read emitted C#, compile a *copy* in `/tmp` with
  `dotnet bin/Release/net10.0/Bjolang.dll -d --emit-cs out.cs file.bjo`. Never
  run `-d` on a file under `lib/` in place: it once replaced
  `lib/std/datetime.dll` with an unoptimized build.
- Do not commit `Readme.org`, `Todo.org`, `lib/std/fmt.bjo`, `lib/std/rx.bjo`
  (my own uncommitted edits) or any `*.md` prompt. `Todo.org` may be edited;
  leave it uncommitted.
- Commit per phase: the subject is a sentence, the body says what changed and
  why and names the tests, and it ends with the ECA trailer used in the log.
  A `CHANGELOG.org` entry per phase. Push only when I ask.

## Decided

1. **Which implementations materialize.** Today's rule, plus conditional
   ones: the target is fully generic (every argument a distinct type
   variable, one per parameter, as now), and every constraint in its
   `(where ...)` is `Eq` or `Ord` on one of those variables. Anything else —
   a constraint of another trait, `(where (->str %a))`, or on a compound
   type, `(where (Eq (List %a)))` — keeps today's fallback, and the comment in
   `materializableImpl` says so.
2. **The dictionary a `where` asks for is a stand-in that answers through the
   element type's own .NET members.** `(Eq %a)` becomes one whose `=` is
   `EqualityComparer<T>.Default.Equals` and whose `eq-hash` is its
   `GetHashCode`. `(Ord %a)` becomes one whose `compare` is
   `string.CompareOrdinal` when `T` is `string` and `Comparer<T>.Default`
   otherwise, exactly as `DefaultOrder` chooses. This is right because every
   type's `Eq` and `Ord` *is* its .NET member: materialized for a declared
   type (a generic one too, after this task), native for the primitives, and
   delegated to those very members for the runtime and .NET types `lib/std`
   implements. It is wrong only where materialization is already wrong (the
   union rule, a specialized target), and then no worse than today.
3. **Built once per closed type, not per call.** The implementation's
   dictionary is a `private static readonly` field on the type — on the
   union's abstract base for a union, which its nested cases can read —
   initialized with `Make(stand-ins...)`, arguments in the implementation
   class's `dictFields` order. Take the order from the same list the class
   is emitted from, so the two cannot disagree. A static field of a generic
   type is one per instantiation, so `Pair<int>` and `Pair<string>` each get
   their own.
4. **The members keep today's shapes and places.** `Equals` and
   `GetHashCode` on a record, a record struct, and every case of a union;
   `CompareTo` and `System.IComparable<T>` in the base clause on a record, a
   record struct, and a union's base. Only the receiver changes, from
   `Impl.Instance` to the static field.
5. **The stand-ins** implement the trait interfaces (`Eq<T>`, `Ord<T>`) and
   are emitted by the compiler, internal to the module that needs them, only
   when it needs them, under names two assemblies cannot share. The
   `lib/std` modules share one namespace, so a plain `ClrEq<T>` at namespace
   level is not safe to assume; nest them in the module class, or make the
   name module-unique.
6. **Derived implementations materialize too**: one rule, and a derived
   `Ord` has to (C# synthesizes no `CompareTo`). A derived `Eq` gains nothing
   in meaning — C#'s field-wise `Equals` already agreed — and costs an
   interface call per field. Measure it (see "Measure"); if a `Set` or `Map`
   of derived generic keys is more than 20% slower, stop and ask before
   adding a marker that keeps derived `Eq` on C#'s members.
7. **A union's different cases stay unequal to the collections.** The case
   classes are C# records, and C# writes each one's `Equals(Base?)`: it
   answers false for another case before any Bjolang code runs, and declaring
   it by hand is error CS0111 (checked). Two values of the *same* case do
   reach the implementation. This is documented, not fixed: a value that has
   two spellings, such as `(Cents 100)` and `(Dollars 1)`, is normalized
   before it is a key. A test pins the rule so a later change notices.
8. **`(Deque %a)` changes, as intended.** Its `Equals` becomes its `=`
   (contents, in order) instead of C#'s field-wise one, which compared the
   `front` and `back` lists and so called two equal deques unequal when they
   were split differently. Its `GetHashCode` raises `unhashable` from the
   implementation instead of the mutable-field throw: a `Deque` could not be
   a key before and cannot be now. Say so in the changelog.

Stop and ask if any of these turns out not to work, and in particular if a
static field cannot go on a record struct or a union base, or if a codegen or
behavioural test shows something depending on the synthesized members.

## Phases

1. **Measure first.** Before changing the compiler, build a benchmark in
   `/tmp` (a `Stopwatch` loop and `GC.GetAllocatedBytesForCurrentThread`, as
   `/tmp/dtbench/readbench.bjo` does): `list->set` of 1 000 000
   `type/derive (Eq)` `(Pair int)` values, `map-set` and `map-try-ref` of
   100 000, and the same with a non-generic derived record for comparison.
   Keep the numbers for the report and the changelog.
2. **`Eq`.** `materializableImpl` accepts the conditional case of "Decided" 1;
   the stand-in for `Eq`; the static dictionary field; `Equals` and
   `GetHashCode` through it on records, record structs and union cases; the
   mutable-record `hashed` rule still skips its throw where a `GetHashCode`
   was written. Update the two codegen tests on purpose (the `Box<T_a>`
   expectations) with comments that say why, and add codegen expectations for
   a generic record, a generic record struct and a generic union. Measure
   again. Commit.
3. **`Ord`.** The stand-in for `Ord`, `CompareTo` and `IComparable<T>` for
   conditional implementations, through the same static-field mechanism.
   Codegen expectations for a generic record and a generic union base.
   Commit.
4. **Documentation.** `Docs/prelude.org` ("Writing one by hand" and
   "`type/derive`": a generic type's implementation is compiled in too, and
   the union rule with the `Cents`/`Dollars` example), the comments in
   `Codegen.fs` (`materializableImpl` says today that refusing the
   conditional case "is not a hole"), `CHANGELOG.org`, and in `Todo.org` the
   item "A hand-written `Eq` is still not what a `Map` keys on", rewritten to
   what is left: the union rule, a specialized target, and a `where` of
   another trait. Commit, without `Todo.org`.

## Tests

In a new `TestFiles/248_generic_materialization.bjo`, with `Set`, `Map`,
`OrderedMap` and `OrderedSet` each asked:

- `(Edge %a)`, undirected: a set of (1 2) and (2 1) has one element, and a
  map finds either through the other.
- The same through a generic record struct and a generic union's same-case
  values, such as `(Named %a)` whose equality ignores a label.
- Nesting: `(Edge (Pair int))` with derived `(Pair %a)`.
- `type/derive (Eq)` on `(Pair %a)`: unchanged answers, which is the point.
- `type/derive (Ord)` on `(Pair %a)` as an `OrderedMap` and an `OrderedSet`
  key, in order; a hand-written `Ord` on `(Job %a)` (by priority, highest
  first, label ignored) the same; `list-sort` agreeing with the ordered set.
- Strings inside a generic key ordered ordinally: `(Pair "B" ...)` before
  `(Pair "a" ...)`, which a culture's order would reverse.
- `(where (->str %a))` beside `(Eq %a)`: not materialized, still compiles,
  field-wise as before.
- A generic record with a mutable field and a conditional `Eq`: its `Equals`
  is the implementation's, and its hash raises what the implementation says.
- `(Deque %a)`: two equal deques built differently (`deque-push-front!` and
  `deque-push-back!`) are equal to .NET, and using one as a `Set` element
  raises. `(.Equals a b)` refuses a Bjolang type, so reach .NET's equality
  through a record with no `Eq` of its own holding each deque: its blanket
  `=` is `Equals`, field by field.
- The union rule, pinned: a union whose `=` makes two different cases equal
  still has both in a set, with a comment pointing at `Docs/prelude.org`.

Codegen expectations as in phases 2 and 3.

## Out of scope

- Passing the `Eq` dictionary to `Map` and `Set` as their comparer (the
  "evidence as comparer" design in `Todo.org`): it would fix the union rule
  and change every generic signature that builds a map.
- A union's cross-case equality, a specialized target such as
  `(impl (Eq (Pair %a %a)))`, and a `where` of another trait.

## Final report

For each phase: what was built, the commit, the tests added, anything decided
that this file did not decide with the example that decided it, the
measurements before and after, and what is left open.
