/// What a compiled module publishes about itself, and the only reader and
/// writer for it.
///
/// One `[AssemblyMetadata]` string, read back by `Pipeline` when somebody
/// imports the resulting `.dll`. It carries everything an importer needs that
/// the emitted C# cannot express: the types of exported bindings, the traits
/// and impls those bindings dispatch through, the foreign imports they resolve
/// overloads against, the bodies that may be inlined at a call site, and the
/// names that are macros rather than functions.
///
/// Types are kept as Bjolang type syntax rather than a structure of their own.
/// They are re-resolved against the *importing* module's registry anyway, so a
/// second representation would be a second parser to keep in step with the
/// first.
module Bjolang.ModuleMetadata

open System.Text

/// Bumped whenever a field's meaning changes such that an older assembly would
/// be misread. Adding an optional field does not require it: a reader of the
/// same version writes and reads the same field list.
/// 2: a type name is a *key* — the module that declared it and the name it was
/// declared under — rather than the bare name source wrote. An assembly built
/// before this reads as though its types were somebody else's.
/// 3: a function with both keyword and rest parameters declares the rest array
/// under `Naming.restParamName` rather than its own name, because such a call
/// has to pass it by name. An assembly built before this declares it under the
/// name its source wrote, and a call that leaves a keyword out cannot be
/// written against it at all.
/// 4: `TypeDecls` holds the types the module *exported*, rather than every type
/// it declared. An assembly built before this offers its private types as
/// though they were public, and knows nothing of the `(Opaque ...)` shape an
/// `#:opaque` export is published as.
/// 5: a `def/trait` may carry a `(#:clr-constraint ...)` clause. Strictly an
/// added field, so a reader of this version is unaffected — but an assembly
/// built *after* this and read by a compiler built before it would fail in the
/// parser, naming the clauses a trait body may hold, rather than saying the
/// assembly is from another version.
let currentVersion = 5

/// An exported binding: enough to bind its name and give it a type.
type ExportedDef = {
    Name: string
    /// Bjolang type syntax, as `Exports.exportedDef` wrote it.
    TypeText: string
    /// `(where ...)` as written, or `""` for an unconstrained binding.
    ConstraintsText: string
    /// The module and original name this export ultimately came from, when the
    /// exporting module was only a facade for it. The importer then emits a
    /// qualified reference to the origin instead of binding a local extern.
    Origin: (string * string) option
}

/// A method body an importing module may splice at a call site.
type InlineTemplateEntry = {
    TraitName: string
    MethodName: string
    Ctor: string
    OriginModule: string
    /// Parameter names, body and qualification map stay three distinct fields.
    /// Bundling the parameters and body into a lambda would be worse than
    /// redundant: `infer`'s `EFun` case binds each parameter to a fresh
    /// metavariable in a scope of its own, discarding exactly the concrete
    /// argument types the inliner supplies.
    Params: string list
    /// A serialized Bjolang expression.
    Body: string
    Qualification: (string * string) list
}

/// One macro an assembly publishes: the Bjolang name, and the module that
/// defines it.
type MacroEntry = { Name: string; ModuleName: string }

/// Declaration groups are separate fields because an importer has to read them
/// in this order: a trait's signature may name a type an `import/class` alias
/// introduced, and an impl's inline template may call an `import/extern` one.
type Metadata = {
    Version: int
    /// Assemblies to link. Transitive, and deliberately not imported: this is
    /// where the code of anything re-exported through this module lives.
    Deps: string list
    TypeDecls: string list
    ExternDecls: string list
    TraitDecls: string list
    ImplDecls: string list
    Defs: ExportedDef list
    InlineTemplates: InlineTemplateEntry list
    Macros: MacroEntry list
}

let empty = {
    Version = currentVersion
    Deps = []
    TypeDecls = []
    ExternDecls = []
    TraitDecls = []
    ImplDecls = []
    Defs = []
    InlineTemplates = []
    Macros = []
}

/// Nothing worth writing: an executable, or a library that exports nothing.
let isEmpty (m: Metadata) =
    m.Deps.IsEmpty
    && m.TypeDecls.IsEmpty
    && m.ExternDecls.IsEmpty
    && m.TraitDecls.IsEmpty
    && m.ImplDecls.IsEmpty
    && m.Defs.IsEmpty
    && m.InlineTemplates.IsEmpty
    && m.Macros.IsEmpty

// ---------------------------------------------------------------------------
// The format
// ---------------------------------------------------------------------------
//
// Every value is a length-prefixed string: `<chars>:<text>`. Lists write their
// count as one of those and then their items.
//
// Length prefixes rather than delimiters because the payload is arbitrary
// source text — an inline template body carries quotes, parentheses and
// newlines by construction, and every delimiter worth choosing appears in one.
// Nothing here escapes anything, so nothing here can disagree with an escaper
// about what it did.

let private putStr (sb: StringBuilder) (s: string) =
    sb.Append(s.Length).Append(':').Append(s) |> ignore

let private putList (sb: StringBuilder) (put: StringBuilder -> 'a -> unit) (items: 'a list) =
    putStr sb (string items.Length)
    for item in items do
        put sb item

let private putPair (sb: StringBuilder) (a: string, b: string) =
    putStr sb a
    putStr sb b

let private putOpt (sb: StringBuilder) (put: StringBuilder -> 'a -> unit) (x: 'a option) =
    match x with
    | Some v ->
        putStr sb "1"
        put sb v
    | None -> putStr sb "0"

type private Cursor = { Text: string; mutable Pos: int }

let private getStr (c: Cursor) : string =
    let colon = c.Text.IndexOf(':', c.Pos)

    if colon < 0 then
        failwith "Malformed module metadata: expected a length-prefixed value."

    let len =
        match System.Int32.TryParse(c.Text.Substring(c.Pos, colon - c.Pos)) with
        | true, n when n >= 0 && colon + 1 + n <= c.Text.Length -> n
        | _ -> failwith "Malformed module metadata: bad length prefix."

    let s = c.Text.Substring(colon + 1, len)
    c.Pos <- colon + 1 + len
    s

let private getList (get: Cursor -> 'a) (c: Cursor) : 'a list =
    let n = int (getStr c)
    [ for _ in 1..n -> get c ]

let private getPair (c: Cursor) : string * string =
    let a = getStr c
    let b = getStr c
    a, b

let private getOpt (get: Cursor -> 'a) (c: Cursor) : 'a option =
    if getStr c = "1" then Some(get c) else None

let private putDef (sb: StringBuilder) (d: ExportedDef) =
    putStr sb d.Name
    putStr sb d.TypeText
    putStr sb d.ConstraintsText
    putOpt sb putPair d.Origin

let private getDef (c: Cursor) : ExportedDef =
    let name = getStr c
    let typeText = getStr c
    let constraintsText = getStr c
    let origin = getOpt getPair c

    { Name = name
      TypeText = typeText
      ConstraintsText = constraintsText
      Origin = origin }

let private putTemplate (sb: StringBuilder) (t: InlineTemplateEntry) =
    putStr sb t.TraitName
    putStr sb t.MethodName
    putStr sb t.Ctor
    putStr sb t.OriginModule
    putList sb putStr t.Params
    putStr sb t.Body
    putList sb putPair t.Qualification

let private getTemplate (c: Cursor) : InlineTemplateEntry =
    let traitName = getStr c
    let methodName = getStr c
    let ctor = getStr c
    let originModule = getStr c
    let params' = getList getStr c
    let body = getStr c
    let qualification = getList getPair c

    { TraitName = traitName
      MethodName = methodName
      Ctor = ctor
      OriginModule = originModule
      Params = params'
      Body = body
      Qualification = qualification }

let private putMacro (sb: StringBuilder) (m: MacroEntry) =
    putStr sb m.Name
    putStr sb m.ModuleName

let private getMacro (c: Cursor) : MacroEntry =
    let name = getStr c
    let moduleName = getStr c
    { Name = name; ModuleName = moduleName }

let serialize (m: Metadata) : string =
    let sb = StringBuilder()
    putStr sb (string m.Version)
    putList sb putStr m.Deps
    putList sb putStr m.TypeDecls
    putList sb putStr m.ExternDecls
    putList sb putStr m.TraitDecls
    putList sb putStr m.ImplDecls
    putList sb putDef m.Defs
    putList sb putTemplate m.InlineTemplates
    putList sb putMacro m.Macros
    sb.ToString()

/// `assemblyPath` names the dependency in the error, because the fix is to
/// rebuild *it* and the compilation that fails is somebody else's.
let deserialize (assemblyPath: string) (text: string) : Metadata =
    let c = { Text = text; Pos = 0 }

    let version =
        match System.Int32.TryParse(getStr c) with
        | true, v -> v
        | _ ->
            failwithf
                $"'%s{assemblyPath}' has unreadable Bjolang metadata. Rebuild it with this compiler version."

    if version <> currentVersion then
        failwithf
            $"'%s{assemblyPath}' was built by a different version of the Bjolang compiler (metadata version %d{version}, this compiler reads %d{currentVersion}). Rebuild it with this compiler version."

    let deps = getList getStr c
    let typeDecls = getList getStr c
    let externDecls = getList getStr c
    let traitDecls = getList getStr c
    let implDecls = getList getStr c
    let defs = getList getDef c
    let templates = getList getTemplate c
    let macros = getList getMacro c

    { Version = version
      Deps = deps
      TypeDecls = typeDecls
      ExternDecls = externDecls
      TraitDecls = traitDecls
      ImplDecls = implDecls
      Defs = defs
      InlineTemplates = templates
      Macros = macros }
