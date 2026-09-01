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

/// The C# parameter a keyword argument arrives in.
///
/// Keyword arguments are passed as C# *named* arguments, so this name is the
/// calling convention: the declaration and every call site — including one in
/// another assembly, compiling against a `.dll` — have to spell it the same
/// way. That is also why `AlphaRename` is forbidden from renaming a keyword
/// parameter.
///
/// The prefix goes on *before* sanitizing rather than after. A keyword named
/// after a C# keyword — `#:base`, `#:default` — sanitizes to `@base`, and
/// prefixing that gives `__kw_@base`, which is not an identifier at all and
/// emitted C# that does not parse. `__kw_base` needs no escape and never did:
/// the prefix is what takes it out of the reserved set.
let keywordParamName (kwName: string) = sanitizeIdent ("__kw_" + kwName)

/// The C# parameter a rest argument arrives in, for a function that also takes
/// keyword arguments.
///
/// Such a call has to be able to leave a keyword out, and the rest parameter
/// sits *after* the keyword ones — C# puts `params` last and gives no choice
/// about it. Leaving a keyword out therefore moves the array out of the
/// position it would be passed in, so the array has to be named too, and a name
/// the call site can spell is one it does not have to look up. A single fixed
/// name is enough: there is only ever one rest parameter.
///
/// A function with no keyword parameters keeps its own name for it. Nothing can
/// be left out of such a call, so the array is passed positionally and there is
/// nothing to disambiguate.
let restParamName = "__rest"

/// The suspending half of a `defbjouble`, under the name it is emitted with.
///
/// It has no Bjolang name — nothing writes it, and no diagnostic mentions it —
/// but it *is* an ordinary top-level definition once the split has happened, so
/// it needs a key to be bound under and published as. Derived rather than
/// published, so that an importing module can name the twin of anything it was
/// told is a double without the origin having to spell both.
///
/// A stack trace naming `readsubline__bjo` is acceptable; a compiler error
/// naming it is not, which is what the `defbjouble` diagnostics are careful
/// about.
let suspendingCopy (name: string) = name + "__bjo"

/// Whether this name is one of those, which is how a pass tells a generated
/// definition from a written one.
let isSuspendingCopy (name: string) = name.EndsWith "__bjo"

/// The root namespace that all module namespaces fall under.
///
/// Every child ends with a hash, ensuring no child can be named `Bjoml`, `Set`,
/// or `Collections` — names that would otherwise shadow the runtime's own
/// namespaces for the code inside.
[<Literal>]
let moduleNamespaceRoot = "BjoMod"

/// Whether the name is a module key — or a type key owned by a module.
///
/// This distinguishes them from names that just happen to contain dots, like
/// `System.IO.TextReader`. The root prefix makes this decidable.
let isModuleKey (name: string) =
    name.StartsWith(moduleNamespaceRoot + ".", StringComparison.Ordinal)

/// The namespace portion of a key: everything except the last segment.
let namespaceOfKey (key: string) =
    match key.LastIndexOf '.' with
    | i when i > 0 -> key.Substring(0, i)
    | _ -> ""

/// The name a key is emitted under: the last segment.
///
/// A declaration is already in its namespace and only writes the final segment; a nested
/// union branch never has a qualified name at all. Names that are not keys are left untouched.
let emittedTypeName (name: string) =
    if isModuleKey name then name.Substring(name.LastIndexOf '.' + 1) else name

/// The module a source or assembly path is known by.
///
/// A module is named after its file, with the two characters that separate a
/// C# identifier folded away. Everything that has to name another file's module
/// — the import graph, the metadata a `.dll` publishes, the macro table — has
/// to arrive at the same answer, so there is one spelling of the rule.
///
/// Also accepts a module key: the flat name is the key's last segment.
let moduleNameOfPath (path: string) : string =
    if isModuleKey path then
        emittedTypeName path
    else
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

/// Four bytes of a SHA-256 hash, as hex.
let private shortHash (text: string) : string =
    use sha = Security.Cryptography.SHA256.Create()

    Text.Encoding.UTF8.GetBytes text
    |> sha.ComputeHash
    |> Array.take 4
    |> Array.map (fun b -> b.ToString "x2")
    |> String.concat ""

let private identSegment (s: string) =
    sanitizeIdent (s.Replace(".", "_").Replace("-", "_"))

/// The namespace where a module's class and types are emitted.
///
/// Derived from the directory, not the file. A module and its `.dll` live in the same
/// directory, so an importer gets the same namespace from the `.dll` path as the module
/// itself got from its `.bjo` path.
///
/// Two files in the same directory share a namespace but cannot collide because they
/// have different filenames, hence different class names.
///
/// A library module is named after its location under `lib` (e.g. `lib/std` becomes `std`),
/// regardless of where the installation happens to be. This makes `BJOLANG_LIB` possible:
/// two copies of the library resolve to the same name and are interchangeable. Everything
/// else gets a hash of its absolute directory, which is what distinguishes two `set.bjo` files.
///
/// Requires a file path. A bare module name has no directory and will yield the wrong answer.
let moduleNamespace (path: string) : string =
    let full = IO.Path.GetFullPath path

    let dir =
        match IO.Path.GetDirectoryName full with
        | null | "" -> IO.Path.GetPathRoot full
        | d -> d

    let lib = Paths.libDir.TrimEnd IO.Path.DirectorySeparatorChar

    let underLib =
        dir = lib
        || dir.StartsWith(lib + string IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)

    if underLib then
        let relative = dir.Substring(lib.Length).Trim IO.Path.DirectorySeparatorChar

        let segments =
            if relative = "" then
                [ "lib" ]
            else
                relative.Split IO.Path.DirectorySeparatorChar
                |> Array.map identSegment
                |> List.ofArray

        $"""%s{moduleNamespaceRoot}.%s{String.concat "." segments}"""
    else
        let leaf =
            match IO.Path.GetFileName dir with
            | null | "" -> "root"
            | l -> identSegment l

        $"%s{moduleNamespaceRoot}.%s{leaf}_%s{shortHash dir}"

/// The module's identity: the namespace and the flat name, as a single string.
///
/// This is the key that everything owned by the module is filed under — types,
/// constructors, bindings, metadata. This ensures two `set.bjo` files in different
/// directories are treated as two distinct modules throughout the entire compilation,
/// not just in the emitted C#.
///
/// Idempotent: passing a key in returns the same key.
let moduleKeyOfPath (path: string) : string =
    if isModuleKey path then
        path
    else
        $"%s{moduleNamespace path}.%s{moduleNameOfPath path}"

/// The assembly name is the module's identity.
///
/// The file is still named `set.dll`. This is the internal name used by the CLR for
/// lookups. Therefore, two `set.dll` files in different directories must spell this
/// name differently so both can be linked into the same program.
let assemblyName = moduleKeyOfPath

/// The fully qualified class name for the module. Accepts a path or a key.
let qualifiedModuleClassName (path: string) =
    let key = moduleKeyOfPath path
    $"%s{namespaceOfKey key}.%s{moduleClassName key}"

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
/// declaring module to key them by, and they are the names `typeKey` never
/// produces. A program *declaring* one of these names is a different thing: it
/// gets a type of its own, keyed like any other, which is what keeps the two
/// apart. `Prelude.emptyRegistry` seeds its `LocalTypes` from this.
let builtinTypeNames =
    Set.ofList
        [ "List"; "Vec"; "VecBuilder"; "ListBuilder"; "VecCursor"; "StringCursor"
          "StringBuilder"; "Seq"; "SeqCursor"; "Option"; "Result"; "Keyword"; "Symbol"; "Array"; "Param"; "DynEnv"
          "Promise"; "Event"; "Chan"; "CancelToken"; "CancelReason"; "AsyncSeq"; "Syntax" ]

/// The builtins only `std/eq` may name, and the module that may name them.
///
/// All three are .NET's own equality, which is what `std/eq` writes the `Eq`
/// implementations for the primitive types in terms of. They are shut away
/// because materialization emits a record's `Equals` as a call to its `Eq`
/// impl: an impl written with one of these would call itself forever, and the
/// resulting stack overflow names neither the impl nor the type.
///
/// A spliced inline template is checked under the module its body came from, so
/// `std/eq`'s own implementations still inline into every caller.
let eqPrivateBindings = Set.ofList [ "clr-eq"; "clr-equals"; "clr-hash" ]

let eqModuleName = "eq"

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
/// A declaration that shadows a runtime type is keyed like any other: a module
/// declaring its own `Option` gets `main__Option`, distinct from the `Option`
/// nothing declared, and neither can be taken for the other.
///
/// Idempotent for a declaration that already carries *this* module's key,
/// which is what a `.dll`'s own type declarations read back look like: the
/// prefix being tested for is one this function just built, so nothing here
/// has to guess where a key divides.
/// The prefix carried by types in this module.
///
/// The namespace is included, so the key is the fully qualified C# name. This ensures
/// two modules with the same filename file their types separately.
let private typePrefix (moduleKey: string) =
    let flat = sanitizeIdent (moduleNameOfPath moduleKey) + "__"

    if isModuleKey moduleKey then
        namespaceOfKey moduleKey + "." + flat
    else
        flat

let typeKey (moduleKey: string) (typeName: string) : string =
    if moduleKey = "" then
        typeName
    else
        let prefix = typePrefix moduleKey
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
        let prefix = typePrefix moduleName
        if key.StartsWith prefix then key.Substring(prefix.Length) else key

/// The module and the name a key is made of, for a name that looks like one.
///
/// Display only, and the one place that guesses: `typeKey` and `bareTypeName`
/// are told which module they are dealing with and a diagnostic is not. A
/// module whose own name contains `__` is therefore reported a little wrong —
/// which costs a reader a moment and costs a program nothing, since no
/// resolution goes through here.
let typeKeyParts (name: string) : (string * string) option =
    // Strip the namespace first: the reader wants to see `thing/Thing`, not the hash.
    let bare = emittedTypeName name

    match bare.IndexOf "__" with
    | i when i > 0 && i + 2 < bare.Length -> Some(bare.Substring(0, i), bare.Substring(i + 2))
    | _ -> None

/// How a type or constructor name reads in a diagnostic.
///
/// A key is a module and a name spelled as one string, and a reader needs both
/// halves: two `Banana`s in one message are told apart only by where each was
/// declared. Written with a `/` so that it composes inside a larger type —
/// `(List banana_a/Banana)` — and because that is the shape `prefix-types`
/// gives a disambiguating spelling anyway.
let showTypeName (name: string) : string =
    match typeKeyParts name with
    | Some(moduleName, typeName) -> moduleName + "/" + typeName
    | None -> name

/// The same name, prefixed by the module's directory.
///
/// Two modules with the same filename will spell their types identically, and an
/// error stating that `thing/Thing` is not `thing/Thing` is unhelpful. This is used
/// specifically for that case, since the directory tag is just noise when names differ.
let showQualifiedTypeName (name: string) : string =
    if not (isModuleKey name) then
        showTypeName name
    else
        let ns = namespaceOfKey name
        let tag = ns.Substring(min ns.Length (moduleNamespaceRoot.Length + 1))

        if tag = "" then showTypeName name else tag + "/" + showTypeName name

/// The C# spelling of a Bjolang type parameter.
let typeParamName (name: string) = "T_" + name.TrimStart('\'')

/// The canonical key a type parameter is tracked under, independent of whether
/// the source wrote it quoted.
let typeParamKey (name: string) = name.TrimStart('\'')
