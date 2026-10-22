// Every type and lambda here is marked `[NonUserCode]`, so that a debugger
// with Just My Code on steps over this library rather than into it: a Bjolang
// program's user is debugging their program, not its runtime. netcoredbg
// counts any assembly with a PDB as user code, and reads the attribute only
// off a method or its own type, never an enclosing one, which is why lambdas
// carry it too. Iterator and async state machines get it from their type.
global using NonUserCode = System.Diagnostics.DebuggerNonUserCodeAttribute;
