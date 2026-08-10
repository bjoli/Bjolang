# Source-level import reproduction (known bug, not fixed)

`importer.bjo` imports `helper.bjo` by relative path. No `helper.dll` exists, so
`Pipeline.resolveImportPath` falls back to the `.bjo` source.

Compile it with:

    dotnet run TestFiles/srcimport/importer.bjo --debug

The frontend succeeds and reports three declarations, having type-checked
`core`, `helper` and `importer`. But `Codegen.generateProgram` emits only
`List.last decls`, while `moduleUsings` emits a `using static` for every import.
The generated C# therefore contains

    using static helper_Module;
    ...
    helper_Module.twice(21)

with no `helper_Module` class anywhere, and the C# compiler rejects it:

    error CS0246: The type or namespace name 'helper_Module' could not be found

This is masked in ordinary use because `build_std.sh` pre-compiles the standard
library to DLLs, and `resolveImportPath` prefers a `.dll` when one is present.
Any genuine source-level import reproduces it.

These files are deliberately not named `[0-9][0-9]_*.bjo`, so `run_tests.sh`
and `snapshot_cs.sh` do not pick them up.
