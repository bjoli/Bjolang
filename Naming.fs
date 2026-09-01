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

/// Roten alla modulnamnrymder hänger under.
///
/// Varje barn slutar med en hash, så inget barn kan heta `Bjoml`, `Set` eller
/// `Collections` — namn som annars hade skuggat körtidens egna namnrymder för
/// koden inuti.
[<Literal>]
let moduleNamespaceRoot = "BjoMod"

/// Är namnet en modulnyckel — eller en typnyckel en modul äger?
///
/// Skiljer dem från namn som bara råkar innehålla punkter, som
/// `System.IO.TextReader`. Roten är vad som gör frågan avgörbar.
let isModuleKey (name: string) =
    name.StartsWith(moduleNamespaceRoot + ".", StringComparison.Ordinal)

/// Namnrymden i en nyckel: allt utom sista ledet.
let namespaceOfKey (key: string) =
    match key.LastIndexOf '.' with
    | i when i > 0 -> key.Substring(0, i)
    | _ -> ""

/// Namnet en nyckel emitteras under: sista ledet.
///
/// En deklaration står redan i sin namnrymd och skriver bara ledet; en nästlad
/// unionsgren har aldrig något kvalificerat namn alls. Namn som inte är
/// nycklar lämnas orörda.
let emittedTypeName (name: string) =
    if isModuleKey name then name.Substring(name.LastIndexOf '.' + 1) else name

/// The module a source or assembly path is known by.
///
/// A module is named after its file, with the two characters that separate a
/// C# identifier folded away. Everything that has to name another file's module
/// — the import graph, the metadata a `.dll` publishes, the macro table — has
/// to arrive at the same answer, so there is one spelling of the rule.
///
/// Tar en modulnyckel lika gärna: det platta namnet är nyckelns sista led.
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

/// Fyra byte av SHA-256, som hex.
let private shortHash (text: string) : string =
    use sha = Security.Cryptography.SHA256.Create()

    Text.Encoding.UTF8.GetBytes text
    |> sha.ComputeHash
    |> Array.take 4
    |> Array.map (fun b -> b.ToString "x2")
    |> String.concat ""

let private identSegment (s: string) =
    sanitizeIdent (s.Replace(".", "_").Replace("-", "_"))

/// Namnrymden en moduls klass och typer emitteras i.
///
/// Härledd ur *katalogen*, inte ur filen. En modul och dess `.dll` ligger i
/// samma katalog, så importören får samma svar ur `.dll`-sökvägen som modulen
/// själv fick ur sin `.bjo`.
///
/// Två filer i samma katalog delar namnrymd och kan inte krocka: de har olika
/// filnamn, alltså olika klassnamn.
///
/// En biblioteksmodul namnges efter sin plats *under `lib`* — `lib/std` blir
/// `std` — och inte efter var installationen råkar ligga. Det är vad som gör
/// `BJOLANG_LIB` möjligt: två kopior av biblioteket är samma namn, alltså
/// utbytbara. Allt annat får en hash av den absoluta katalogen, som är det
/// enda som skiljer två `set.bjo` åt.
///
/// Kräver en sökväg. Ett modulnamn har ingen katalog och ger fel svar.
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

/// Modulens identitet: namnrymden och det platta namnet, som en sträng.
///
/// Nyckeln allt modulägt filas under — typer, konstruktorer, bindningar,
/// metadata — så två `set.bjo` i olika kataloger är två moduler hela vägen
/// igenom och inte bara i den emitterade C#:en.
///
/// Idempotent: en nyckel in ger samma nyckel ut.
let moduleKeyOfPath (path: string) : string =
    if isModuleKey path then
        path
    else
        $"%s{moduleNamespace path}.%s{moduleNameOfPath path}"

/// Assemblynamnet är modulens identitet.
///
/// Filen heter fortfarande `set.dll`. Det här är namnet *inuti* den, och det
/// är vad CLR slår upp på — så två `set.dll` i olika kataloger måste stava
/// namnet olika för att båda ska kunna länkas in i samma program.
let assemblyName = moduleKeyOfPath

/// Modulens klass, fullt kvalificerad. Tar en sökväg eller en nyckel.
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
/// Prefixet den här modulens typer bär.
///
/// Namnrymden är med, så nyckeln är det fullt kvalificerade C#-namnet och två
/// moduler med samma filnamn filar sina typer var för sig.
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
    // Namnrymden av först: en läsare vill se `thing/Thing`, inte hashen.
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

/// Samma namn, med katalogen modulen ligger i framför.
///
/// Två moduler med samma filnamn stavas lika, och ett fel som säger att
/// `thing/Thing` inte är `thing/Thing` hjälper ingen. Bara för det fallet:
/// katalogtaggen är brus när namnet redan skiljer.
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
