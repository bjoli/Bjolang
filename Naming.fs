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

/// The types that belong to no module.
///
/// `List`, `Option`, `Syntax` and the rest are the runtime's, declared by no
/// `.bjo` and named the same wherever they are mentioned — so there is no
/// declaring module to key them by. `Prelude.emptyRegistry` seeds its
/// `LocalTypes` from this, and `typeKey` leaves these names alone: a program
/// that declares a type of one of these names is shadowing the runtime type on
/// purpose, and `Codegen.shadowedBuiltins` is what that costs.
let builtinTypeNames =
    Set.ofList
        [ "List"; "Vec"; "VecBuilder"; "ListBuilder"; "MapBuilder"; "VecCursor"; "MapCursor"; "StringCursor"
          "StringBuilder"; "Seq"; "Option"; "Result"; "Map"; "Keyword"; "Symbol"; "Array"; "Param"; "DynEnv"
          "Promise"; "Event"; "Chan"; "CancelToken"; "AsyncSeq"; "Syntax" ]

/// The name a declared type — or one of its constructors — is known by
/// everywhere except in source.
///
/// A Bjolang type is nominal, and its identity is the module that declared it
/// plus the name it was declared under. The pair is collapsed into one string
/// because everything keyed on a type is keyed on a *name*: the
/// implementations, the unions, the inline templates, the impl class C# gets
/// emitted as. Two modules may therefore each declare a `Banana` and mean two
/// types.
///
/// Source goes on writing the bare name. `registerTypeDefs` files a spelling
/// for it in the same table an import modifier's spellings go in, and
/// `Inference.originalName` resolves it before any registry is consulted.
///
/// Idempotent for a declaration that already carries *this* module's key,
/// which is what a `.dll`'s own type declarations read back look like: the
/// prefix being tested for is one this function just built, so nothing here
/// has to guess where a key divides.
let typeKey (moduleName: string) (typeName: string) : string =
    if moduleName = "" || Set.contains typeName builtinTypeNames then
        typeName
    else
        let prefix = sanitizeIdent (moduleNameOfPath moduleName) + "__"
        if typeName.StartsWith prefix then typeName else prefix + typeName

/// The name source wrote, given a key and the module that declared it.
///
/// The exact inverse of `typeKey` — a prefix this module built is removed
/// again, rather than a division guessed at — so that an importer can offer
/// the bare spelling of a type it read out of a `.dll`. A name that does not
/// carry the prefix was not keyed by this module and is its own bare name.
let bareTypeName (moduleName: string) (key: string) : string =
    if moduleName = "" then
        key
    else
        let prefix = sanitizeIdent (moduleNameOfPath moduleName) + "__"
        if key.StartsWith prefix then key.Substring(prefix.Length) else key

/// How a type or constructor name reads in a diagnostic.
///
/// A key is a module and a name spelled as one string, and a reader needs both
/// halves: two `Banana`s in one message are told apart only by where each was
/// declared. Written with a `/` so that it composes inside a larger type —
/// `(List banana_a/Banana)` — and because that is the shape `prefix-types`
/// gives a disambiguating spelling anyway.
///
/// Display only. It has to find where the key divides, and a module whose own
/// name contains `__` is shown a little wrong rather than resolved wrong.
let showTypeName (name: string) : string =
    match name.IndexOf "__" with
    | i when i > 0 && i + 2 < name.Length -> name.Substring(0, i) + "/" + name.Substring(i + 2)
    | _ -> name

/// The C# spelling of a Bjolang type parameter.
let typeParamName (name: string) = "T_" + name.TrimStart('\'')

/// The canonical key a type parameter is tracked under, independent of whether
/// the source wrote it quoted.
let typeParamKey (name: string) = name.TrimStart('\'')
