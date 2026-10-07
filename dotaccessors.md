You are working on Bjolang, a Scheme-inspired, statically typed language
(Hindley–Milner inference) that compiles to C#. The compiler is written in F#.
Implement dot accessors for records and structs, and then path macros
(get / try-get / set / update) on top of them.

Read the relevant code before changing anything, and follow the existing
style: long explanatory comments, and error messages that say what to write
instead. Relevant places:
- Lexer.fs: `.name` and `.-Name` already lex as single Symbol tokens,
  and `...` lexes as Spread.
- InferExpr.fs: inferDotMethod handles `(.Method target args...)`. There is
  also a handler for `(.-Property target)`.
- TypedAST.fs: RecordFields (field name -> owning types) and the fallback
  that record-ref / record-set use when the target's type is unknown.
- TypeSyntax.fs: record and struct definitions.
- prelude.bjo: existing def/macro and def/trait conventions.

Work in two phases. Stop after phase 1, show me the diff and the test
results, and wait for my review.

## Phase 1: `.name` field access

Do NOT change the lexer or reader.

When the checker sees `(.x target args...)`, infer the type of target first,
then:
1. Bjolang record or struct: read field `x`. This must behave EXACTLY like
   `(record-ref target x)`: same typing, same fallback, same errors, same
   C# output. It is monomorphic. Do not add row types, HasField traits or
   any other form of field polymorphism.
2. .NET type: keep today's behaviour unchanged. `.Method` is a method call
   and `.-Prop` is a property read.
3. Type not known yet: use the same field-name fallback as record-ref. If no
   record has a field `x`, report the same error the .NET path gives today,
   not "no record has field x".

Rules:
- On a record, look for a field first. If there is none, fall back to a
  .NET method on the generated C# record (ToString, Equals, ...), but only
  if that works today. Check this and tell me what you find.
- (default) `.-name` on a record is an error. The hint says to write `.name`.
- (default) `(.field r extra-args...)` on a record is an error for now.
  Do not call a function-valued field.
- A bare `.name` in value position, as in `(map .name fruits)`, becomes
  `(fun (x) (record-ref x name))`. A bare `.Trim` must still give the
  existing "a method group is not a value" error.
- A `.name` written inside a macro template must still be recognised after
  hygiene renaming. Check how the dispatch reads the symbol's name.

Every program that compiles today must compile to the same C#. Add tests for:
- `.name` on a record, on a struct, on a generic record, and on a type that
  is still unknown (with one owner and with two owners of the field)
- `.name` inside a macro template
- `(map .name xs)`
- `.Trim` and `.-Length` on strings (unchanged)
- `.ToString` on a record
- each of the errors above

## Phase 2: the Indexed trait and the path macros

1. Before writing anything, check whether `get`, `try-get`, `set` or
   `update` is already bound anywhere. If one is, stop and ask me.

2. Add a trait `Indexed` with associated types `key` and `val` and three
   methods:
   - `index-ref` (throws when the key is missing)
   - `index-try-ref` (returns an Option)
   - `index-set` (returns a copy, like record-set)
   Implement it for Map and Vector.
   (default) Do not implement it for the linked List.
   (default) index-set on a Map inserts a missing key. On a Vector, an index
   out of range throws.

3. Macros, written with def/macro in the prelude. Steps are handled by the
   syntax of each step, since the macros run before type checking:
   - A symbol starting with `.` is a field step. It expands to `(.x t)`,
     and the checker's dispatch then handles records and .NET.
   - Any other step is a key. It goes through the Indexed trait.

   Semantics:
   - `(get r .stuff :banana)` expands to
     `(index-ref (.stuff r) :banana)`.
   - `(try-get r step...)` returns an Option. It short-circuits at the first
     missing key. Field steps never fail.
     (default) It does NOT step into Option-typed fields: a field of type
     `(Option T)` gives `Option (Option T)`.
   - `(update r step... f)` is the core. `(set r step... v)` is update with
     `(fun (_) v)_))`. Both copy: use record-set for field steps and
     index-set for key steps.
   - (default) In set/update, a missing key before the last step throws.
     There is no try-set.
   - Evaluate the target, then each key left to right, then the value or
     function, and each of them exactly once. Bind them to temporaries.
     For example, `(set r .items (next-id!) .count 5)` must call
     next-id! only once.
   - (default) An empty path, as in `(get r)`, is an error.
   - set/update through a .NET member, as in `(set r .buf .-Length 0)`,
     must fail with a clear message: set copies records, and .-Length is
     a .NET property.
   - Generated code must keep each step's source position, so that errors
     point at the step and not at the whole form.
   - There is no set! in this task.

Add tests for:
- nested paths through records, structs, maps and vectors
- try-get with a present key, a missing key, and an Option field
- update
- evaluation order and single evaluation
- each of the errors above

Show me the C# that is generated for a two-level set.

When something in the existing code contradicts this prompt, or a decision
is not covered here, stop and ask. Do not guess.
