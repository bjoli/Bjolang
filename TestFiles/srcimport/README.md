# Source-level import fixture

`importer.bjo` imports `helper.bjo` by relative path. There is no `helper.dll`
in the tree, so the compiler builds one and imports that.

This directory was a reproduction of a bug: `(import "helper.bjo")` used to
type-check the imported file and emit a `using static helper_Module;` for it,
while `Codegen.generateProgram` emitted only `List.last decls` — so the
generated C# named a class nothing had produced and `csc` rejected it with
`CS0246`. The workaround was to compile the other file with `--lib` by hand, or
to use `(include ...)` instead.

`(import "x.bjo")` now means what it always looked like it meant: compile `x` to
`x.dll` and import that. The sub-compilation runs in a process of its own,
because the compiler keeps module-level mutable state — `Gensym`'s counter, the
macro table — that a nested compilation would otherwise clobber.

`helper.bjo` also defines a name it does not export, which
`TestFiles/errors/import_not_exported.bjo` relies on.

`importer.bjo` carries no numeric prefix, so `run_tests.sh` does not pick it up;
`TestFiles/092_source_import.bjo` is the test that does.
