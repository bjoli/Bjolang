module Bjolang.Naming

open System

/// How a Bjolang name is spelled in C#.
///
/// Deliberately **not** injective: `a-b` and `asubb` both come out as `asubb`.
/// Anything that invents a binder therefore has to distinguish it by something
/// other than its base name — which is what `Gensym`'s `__N` suffix is for.
let sanitizeIdent (s: string) =
    let s = s.Replace("::", ".").Replace("-", "sub").Replace("?", "_QMARK").Replace("!", "_BANG").Replace("+", "add").Replace("*", "mul").Replace("/", "div").Replace("<", "lt").Replace(">", "gt").Replace("=", "eq").Replace("'", "").Replace("&", "arg_")
    let s = if s.Length > 0 && Char.IsDigit(s[0]) then "_" + s else s
    match s with
    | "class" | "struct" | "public" | "private" | "protected" | "internal" | "static" | "readonly" | "var" | "ref" | "out" | "in" | "params" | "new" | "return" | "if" | "else" | "while" | "for" | "foreach" | "do" | "switch" | "case" | "default" | "break" | "continue" | "goto" | "try" | "catch" | "finally" | "throw" | "lock" | "typeof" | "sizeof" | "is" | "as" | "true" | "false" | "null" | "void" | "object" | "string" | "int" | "bool"
    // The rest of the built-in type names. A Bjolang `double` or `long` is a
    // perfectly ordinary identifier, and one named after a C# type keyword used
    // to be emitted bare — producing C# that does not parse.
    | "double" | "float" | "decimal" | "char" | "byte" | "sbyte" | "short" | "ushort" | "uint" | "long" | "ulong" | "nint" | "nuint"
    | "abstract" | "base" | "checked" | "const" | "delegate" | "enum" | "event" | "explicit" | "extern" | "fixed" | "implicit" | "interface" | "namespace" | "operator" | "override" | "sealed" | "stackalloc" | "this" | "unchecked" | "unsafe" | "using" | "virtual" | "volatile" -> "@" + s
    | _ -> s

/// The module a source or assembly path is known by.
///
/// A module is named after its file, with the two characters that separate a
/// C# identifier folded away. Everything that has to name another file's module
/// — the import graph, the metadata a `.dll` publishes, the macro table — has
/// to arrive at the same answer, so there is one spelling of the rule.
let moduleNameOfPath (path: string) : string =
    IO.Path.GetFileNameWithoutExtension(path).Replace(".", "_").Replace("-", "_")

/// The C# class a module's declarations are emitted into.
///
/// Takes a module name or the path it came from, indifferently. A module is
/// named after its source file, so the name can hold characters no C#
/// identifier may hold — or start with a digit, as `006_lib.bjo` does. Every
/// site that spells this class has to agree on the answer: the class definition,
/// the `using static` for it, a qualified reference to one of its bindings, and
/// the generated entry point.
let moduleClassName (moduleName: string) =
    sanitizeIdent (moduleNameOfPath moduleName) + "_Module"

/// The reference an inlined body uses for a free name that belongs to a module.
///
/// A spliced body may land next to a local of the same name, so a bare
/// identifier is not good enough: it would bind to the local. `Codegen` rewrites
/// `::` to `.` and recognizes the `_Module` prefix as a qualification rather
/// than a trait implementation's method.
let qualifiedBinding (moduleName: string) (name: string) =
    $"%s{moduleClassName moduleName}::%s{name}"

/// The C# spelling of a Bjolang type parameter.
let typeParamName (name: string) = "T_" + name.TrimStart('\'')

/// The canonical key a type parameter is tracked under, independent of whether
/// the source wrote it quoted.
let typeParamKey (name: string) = name.TrimStart('\'')
