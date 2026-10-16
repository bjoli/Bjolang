(* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 *
 * As a special exception to the Mozilla Public License, version 2.0, if you
 * compile your application source code and portions of this software are
 * embedded into the generated object code or executable form as a normal
 * consequence of the compilation process (such as inline functions,
 * templates, generics, or macros), you may redistribute such embedded portions
 * in such object code or executable form without complying with the source code
 * availability requirements or notice obligations of Section 3 of the MPL 2.0.
 *)

/// The locals of a built assembly's PDB, as a debugger should show them: the
/// program's under the names it wrote, and the compiler's hidden.
///
/// Roslyn names a local after its C#, which is the Bjolang name sanitized and,
/// where hygiene or alpha renaming made it unique, suffixed: `is-big?` is
/// `is_subbig_QMARK`, a renamed `x` is `x__23`, and beside them sit the
/// compiler's own temporaries, `loopcur__75` and `__init__65`. C# cannot spell
/// a Bjolang name, so the generated source cannot fix this; the PDB, which is
/// where a debugger reads local names, can.
///
/// The PDB is patched rather than rebuilt. Each `LocalVariable` row has a name
/// in the `#Strings` heap and an attributes field: a temporary gets
/// `DebuggerHidden`, in place, and a renamed local a new string appended to
/// the heap, the streams after it moved along. Rebuilding the whole PDB would
/// mean re-encoding every blob that points into another heap, an import scope
/// for one, which a patch never touches. The assembly's `PdbChecksum`, which
/// a debugger may check the PDB against, is computed again over the result.
///
/// Every build, not only `-d`: a program being debugged steps into its
/// imports, which are built without it. Nothing at run time reads a local's
/// name, and a stack trace's lines come from the sequence points, which the
/// patch leaves as they were and checks. A patch that fails or does not read
/// back as it should leaves Roslyn's files as they are. `BJOLANG_RAW_PDB=1`
/// leaves them alone always.
///
/// A local that is a field — captured by a closure, or held by an iterator's
/// state machine, which in a debug build is every local of a `seql` — is
/// named in the assembly instead, and renamed there by the same rule (see
/// "The assembly's fields" below).
///
/// A parameter keeps its C# name: parameters are named in the assembly's own
/// metadata, and a keyword argument arrives by that name. So does a captured
/// parameter's field, which is the same string.
module Bjolang.PdbNames

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable

/// What a debugger is to make of a local.
type private Seen =
    /// The program's own, under this name.
    | Shown of string
    /// The compiler's.
    | Hidden

/// The name `fresh` derived a name from, every suffix taken off:
/// `loopcur__75__11` is `loopcur`.
let rec private unsuffixed (name: string) =
    let b = Gensym.baseName name
    if b = name then name else unsuffixed b

/// A local's C# name, as the program wrote it, or `None` for one Roslyn made
/// for itself, `CS$<>8__locals0`, which the debugger already knows what to do
/// with.
///
/// The Bjolang spelling is read back from the sanitizing, with or without its
/// suffix, since a name may have been sanitized before it was renamed or
/// after. A local is the program's if source this compilation read wrote that
/// name, which an inlined body's are too: `elem` from `list-map`'s loop is a
/// name somebody wrote, and is what that slot holds.
let private seen (originals: Dictionary<string, string>) (csName: string) : Seen option =
    if csName.Contains '<' || csName.Contains '$' then
        None
    else
        let bjolang =
            match originals.TryGetValue csName with
            | true, original -> unsuffixed original
            | _ ->
                let stripped = unsuffixed csName

                match originals.TryGetValue stripped with
                | true, original -> original
                | _ -> stripped

        if Gensym.isWritten bjolang then Some(Shown bjolang) else Some Hidden

let private readU16 (b: byte[]) (at: int) = int b[at] ||| (int b[at + 1] <<< 8)

let private readU32 (b: byte[]) (at: int) =
    int b[at] ||| (int b[at + 1] <<< 8) ||| (int b[at + 2] <<< 16) ||| (int b[at + 3] <<< 24)

let private writeU16 (b: byte[]) (at: int) (v: int) =
    b[at] <- byte v
    b[at + 1] <- byte (v >>> 8)

let private writeU32 (b: byte[]) (at: int) (v: int) =
    b[at] <- byte v
    b[at + 1] <- byte (v >>> 8)
    b[at + 2] <- byte (v >>> 16)
    b[at + 3] <- byte (v >>> 24)

/// The stream headers of a metadata blob, as (position of the header's offset
/// field, stream offset, stream size, name).
let private streamHeaders (b: byte[]) =
    // Signature, versions, reserved, then the version string's length and the
    // string, padded to four; then flags and the stream count.
    let versionLength = readU32 b 12
    let mutable at = 16 + versionLength + 2
    let count = readU16 b at
    at <- at + 2

    [ for _ in 1..count do
          let field = at
          let offset = readU32 b at
          let size = readU32 b (at + 4)
          let mutable e = at + 8

          while b[e] <> 0uy do
              e <- e + 1

          let name = Text.Encoding.ASCII.GetString(b, at + 8, e - at - 8)
          // The name, its terminator, and padding to four.
          at <- at + 8 + ((e - (at + 8) + 4) &&& ~~~3)
          yield field, offset, size, name ]

/// The patched PDB, or `None` if nothing in it changes or the patch cannot be
/// made: renaming would take the string heap past what two-byte indices
/// reach, which would change the size of every row that names a string.
let private patch (pdb: byte[]) : byte[] option =
    use provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create<byte>(pdb))
    let reader = provider.GetMetadataReader()
    let originals = Naming.originals ()

    let heapStart = reader.GetHeapMetadataOffset HeapIndex.String
    let heapSize = reader.GetHeapSize HeapIndex.String
    let tableStart = reader.GetTableMetadataOffset TableIndex.LocalVariable
    let rowSize = reader.GetTableRowSize TableIndex.LocalVariable
    let nameIndexSize = rowSize - 4

    let edited = Array.copy pdb
    use appended = new MemoryStream()
    let added = Dictionary<string, int>()
    let mutable changed = false

    for handle in reader.LocalVariables do
        let local = reader.GetLocalVariable handle
        let name = reader.GetString local.Name
        let row = tableStart + (MetadataTokens.GetRowNumber handle - 1) * rowSize

        match seen originals name with
        | None -> ()
        | Some Hidden ->
            if not (local.Attributes.HasFlag LocalVariableAttributes.DebuggerHidden) then
                writeU16 edited row (int local.Attributes ||| int LocalVariableAttributes.DebuggerHidden)
                changed <- true
        | Some(Shown shown) when shown <> name ->
            let index =
                match added.TryGetValue shown with
                | true, i -> i
                | _ ->
                    let i = heapSize + int appended.Length
                    let bytes = Text.Encoding.UTF8.GetBytes shown
                    appended.Write(bytes, 0, bytes.Length)
                    appended.WriteByte 0uy
                    added[shown] <- i
                    i

            if nameIndexSize = 2 then writeU16 edited (row + 4) index else writeU32 edited (row + 4) index
            changed <- true
        | Some(Shown _) -> ()

    // The heap is padded to four, as every stream is.
    while appended.Length % 4L <> 0L do
        appended.WriteByte 0uy

    let delta = int appended.Length

    if not changed then
        None
    elif nameIndexSize = 2 && heapSize + delta > 0xFFFF then
        None
    else
        let heapEnd = heapStart + heapSize
        let result = Array.zeroCreate<byte> (edited.Length + delta)
        Array.blit edited 0 result 0 heapEnd
        Array.blit (appended.ToArray()) 0 result heapEnd delta
        Array.blit edited heapEnd result (heapEnd + delta) (edited.Length - heapEnd)

        // The string heap is longer, and every stream after it has moved.
        for field, offset, size, name in streamHeaders result do
            if name = "#Strings" then writeU32 result (field + 4) (size + delta)
            elif offset >= heapEnd then writeU32 result field (offset + delta)

        Some result

/// What a debugger checks a PDB by in the assembly: the `PdbChecksum` entry's
/// hash, which is of the PDB with its 20-byte ID zeroed. `None` when the
/// assembly has no such entry, and nothing to update.
let private withChecksum (dll: byte[]) (pdb: byte[]) : byte[] option =
    use pe = new PEReader(ImmutableArray.Create<byte>(dll))

    let entry =
        pe.ReadDebugDirectory() |> Seq.tryFind (fun e -> e.Type = DebugDirectoryEntryType.PdbChecksum)

    match entry with
    | None -> None
    | Some e ->
        let data = pe.ReadPdbChecksumDebugDirectoryData e

        let idStart =
            use provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create<byte>(pdb))
            provider.GetMetadataReader().DebugMetadataHeader.IdStartOffset

        let zeroed = Array.copy pdb
        Array.fill zeroed idStart 20 0uy

        let hash =
            match data.AlgorithmName with
            | "SHA256" -> Security.Cryptography.SHA256.HashData zeroed
            | "SHA384" -> Security.Cryptography.SHA384.HashData zeroed
            | "SHA512" -> Security.Cryptography.SHA512.HashData zeroed
            | other -> failwithf "unknown PDB checksum algorithm %s" other

        if hash.Length <> data.Checksum.Length then
            failwith "the PDB checksum is not the length its algorithm makes"

        let result = Array.copy dll
        let at = e.DataPointer + Text.Encoding.UTF8.GetByteCount data.AlgorithmName + 1
        Array.blit hash 0 result at hash.Length
        Some result

/// Whether `patched` is `original` with only local names and attributes
/// changed: every table the same length, every method's sequence points the
/// same, every local readable.
let private agrees (original: byte[]) (patched: byte[]) : bool =
    use a = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create<byte>(original))
    use b = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create<byte>(patched))
    let ra = a.GetMetadataReader()
    let rb = b.GetMetadataReader()

    let tables =
        [ TableIndex.Document
          TableIndex.MethodDebugInformation
          TableIndex.LocalScope
          TableIndex.LocalVariable
          TableIndex.LocalConstant
          TableIndex.ImportScope
          TableIndex.StateMachineMethod
          TableIndex.CustomDebugInformation ]

    let points (r: MetadataReader) =
        [ for h in r.MethodDebugInformation do
              for p in r.GetMethodDebugInformation(h).GetSequencePoints() do
                  yield p.StartLine, p.StartColumn, p.EndLine, p.EndColumn, p.Offset, p.IsHidden ]

    tables |> List.forall (fun t -> ra.GetTableRowCount t = rb.GetTableRowCount t)
    && List.ofSeq ra.DebugMetadataHeader.Id = List.ofSeq rb.DebugMetadataHeader.Id
    && points ra = points rb
    && (rb.LocalVariables |> Seq.forall (fun h -> rb.GetString(rb.GetLocalVariable(h).Name) <> null))

// ---------------------------------------------------------------------------
// The assembly's fields
//
// A local that a closure captures, or that lives across a `yield` — every local
// of a `seql` in a debug build, where Roslyn hoists them all — is not a local
// at all but a field: of a `<>c__DisplayClass` under its C# name, or of an
// iterator's state machine as `<name>5__N`. A debugger reads those names from
// the assembly, so they are renamed there, by the same rule as the PDB's
// locals: the program's under its Bjolang spelling, `<is-big?>5__3`, and the
// compiler's under a name beginning `<>`, which is how a debugger recognises
// a field Roslyn made for itself and leaves out.
//
// The assembly cannot grow a string heap the way the PDB can: it is laid out in
// sections, with RVAs into them. So a name is only ever written over the old
// one, in place, and only when it is no longer, which the Bjolang spelling and
// the hidden names below both nearly always are. Nothing else changes: IL
// refers to a field by token, never by name.

/// The `<name>5__N` Roslyn calls a hoisted local, as (name, N).
let private hoisted (field: string) : (string * string) option =
    if field.Length > 0 && field[0] = '<' then
        match field.IndexOf ">5__" with
        | i when i > 1 && field.IndexOf('<', 1) < 0 && field.IndexOf('>') = i ->
            let n = field.Substring(i + 4)

            if n.Length > 0 && n |> Seq.forall Char.IsDigit then
                Some(field.Substring(1, i - 1), n)
            else
                None
        | _ -> None
    else
        None

/// Whether a type is one Roslyn made to hold locals: a closure's display class,
/// or an iterator's or async method's state machine, `<name>d__N`.
let private holdsLocals (typeName: string) =
    typeName.StartsWith "<>c__DisplayClass"
    || (typeName.StartsWith "<"
        && (let tail = typeName.Substring(typeName.LastIndexOf '>' + 1)
            tail = "d" || (tail.StartsWith "d__" && tail.Length > 3)))

/// Whether a member reference is to a field of a type that holds locals. A
/// generic method's closure or state machine is generic too, and its fields
/// are used through a reference to them on its instantiation, `<...>d<T>`,
/// which names the field again — the same string, renamed with it.
let private refersToLocal (r: MetadataReader) (m: MemberReference) : bool =
    m.GetKind() = MemberReferenceKind.Field
    && m.Parent.Kind = HandleKind.TypeSpecification
    && (let spec = r.GetTypeSpecification(TypeSpecificationHandle.op_Explicit m.Parent)
        let mutable blob = r.GetBlobReader spec.Signature

        blob.ReadSignatureTypeCode() = SignatureTypeCode.GenericTypeInstance
        && (let _class = blob.ReadSignatureTypeCode()
            let generic = blob.ReadTypeHandle()

            generic.Kind = HandleKind.TypeDefinition
            && holdsLocals (r.GetString(r.GetTypeDefinition(TypeDefinitionHandle.op_Explicit generic).Name))))

/// Every string-heap name the metadata refers to, with the table it is in:
/// every column of type String in ECMA-335's tables. A reference to a field
/// that holds a local is a `localref`, and is renamed with the field.
let private stringNames (r: MetadataReader) : (string * StringHandle) list =
    [ let m = r.GetModuleDefinition()
      yield "module", m.Name

      for h in r.TypeReferences do
          let t = r.GetTypeReference h
          yield "typeref", t.Name
          yield "typeref", t.Namespace

      for h in r.TypeDefinitions do
          let t = r.GetTypeDefinition h
          yield "typedef", t.Name
          yield "typedef", t.Namespace

          for g in t.GetGenericParameters() do
              yield "genericparam", r.GetGenericParameter(g).Name

      for h in r.FieldDefinitions do
          yield "field", r.GetFieldDefinition(h).Name

      for h in r.MethodDefinitions do
          let d = r.GetMethodDefinition h
          yield "method", d.Name

          for p in d.GetParameters() do
              yield "param", r.GetParameter(p).Name

          for g in d.GetGenericParameters() do
              yield "genericparam", r.GetGenericParameter(g).Name

          let import = d.GetImport()

          if not import.Name.IsNil then
              yield "implmap", import.Name

      for h in r.MemberReferences do
          let m = r.GetMemberReference h
          yield (if refersToLocal r m then "localref" else "memberref"), m.Name

      for h in r.EventDefinitions do
          yield "event", r.GetEventDefinition(h).Name

      for h in r.PropertyDefinitions do
          yield "property", r.GetPropertyDefinition(h).Name

      for row in 1 .. r.GetTableRowCount TableIndex.ModuleRef do
          yield "moduleref", r.GetModuleReference(MetadataTokens.ModuleReferenceHandle row).Name

      if r.IsAssembly then
          let a = r.GetAssemblyDefinition()
          yield "assembly", a.Name
          yield "assembly", a.Culture

      for h in r.AssemblyReferences do
          let a = r.GetAssemblyReference h
          yield "assemblyref", a.Name
          yield "assemblyref", a.Culture

      for h in r.AssemblyFiles do
          yield "file", r.GetAssemblyFile(h).Name

      for h in r.ExportedTypes do
          let t = r.GetExportedType h
          yield "exportedtype", t.Name
          yield "exportedtype", t.Namespace

      for h in r.ManifestResources do
          yield "resource", r.GetManifestResource(h).Name ]
    |> List.filter (fun (_, h) -> not h.IsNil)

/// The name a debugger is to see a field under, if it is to change: `name` is
/// the field's, in a type that holds locals, and `hidden` numbers the hidden
/// names that are not a hoisted local's, which have no number of their own.
let private renamed (originals: Dictionary<string, string>) (hidden: int ref) (name: string) : string option =
    match hoisted name with
    | Some(local, n) ->
        match seen originals local with
        | Some(Shown shown) when shown <> local -> Some $"<%s{shown}>5__%s{n}"
        | Some Hidden -> Some $"<>h__%s{n}"
        | _ -> None
    | None ->
        match seen originals name with
        | Some(Shown shown) when shown <> name -> Some shown
        | Some Hidden ->
            hidden.Value <- hidden.Value + 1
            Some $"<>v__%d{hidden.Value}"
        | _ -> None

/// The assembly with the fields that hold locals renamed, or `None` if none
/// is. A name is renamed only where it fits, and only where no other name is
/// stored in its bytes: the string heap shares a suffix between two names
/// that end alike, and overwriting one would change the other.
let private renameFields (dll: byte[]) : byte[] option =
    use pe = new PEReader(ImmutableArray.Create<byte>(dll))

    if not pe.HasMetadata
       || pe.PEHeaders.CorHeader.Flags.HasFlag CorFlags.StrongNameSigned then
        None
    else
        let r = pe.GetMetadataReader()
        let originals = Naming.originals ()
        let heap = pe.PEHeaders.MetadataStartOffset + r.GetHeapMetadataOffset HeapIndex.String
        let heapSize = r.GetHeapSize HeapIndex.String

        // Where the string at a heap offset ends: its terminator.
        let terminator (offset: int) =
            let mutable e = heap + offset

            while e < heap + heapSize && dll[e] <> 0uy do
                e <- e + 1

            e

        let names = stringNames r
        let offsets = names |> List.map (fun (_, h) -> MetadataTokens.GetHeapOffset h) |> List.distinct

        // The offsets whose bytes no other name shares. Every name stored in a
        // string's bytes ends where it ends, so sharing is a terminator two
        // distinct offsets have.
        let alone =
            offsets
            |> List.groupBy terminator
            |> List.choose (fun (_, group) ->
                match group with
                | [ only ] -> Some only
                | _ -> None)
            |> HashSet

        // A string is renamed only if every name at its offset is a field
        // that holds a local: a type, a method or anything else of the same
        // spelling would be renamed with it.
        let candidates = HashSet<int>()
        let others = HashSet<int>()

        for h in r.TypeDefinitions do
            let t = r.GetTypeDefinition h
            let holds = holdsLocals (r.GetString t.Name)

            for f in t.GetFields() do
                let offset = MetadataTokens.GetHeapOffset(r.GetFieldDefinition(f).Name)
                if holds then candidates.Add offset |> ignore else others.Add offset |> ignore

        // A reference to one of those fields finds it by name at run time, so
        // it has to be the same string as the field's. If one is a copy, the
        // spelling is left alone wherever it is.
        let copied = HashSet<string>()

        for kind, h in names do
            let offset = MetadataTokens.GetHeapOffset h

            match kind with
            | "field" -> ()
            | "localref" -> if not (candidates.Contains offset) then copied.Add(r.GetString h) |> ignore
            | _ -> others.Add offset |> ignore

        let edited = Array.copy dll
        let hidden = ref 0
        let mutable changed = false

        for offset in Seq.sort candidates do
            let old = r.GetString(MetadataTokens.StringHandle offset)

            if alone.Contains offset && not (others.Contains offset) && not (copied.Contains old) then
                match renamed originals hidden old with
                | Some name ->
                    let oldBytes = Text.Encoding.UTF8.GetByteCount old
                    let bytes = Text.Encoding.UTF8.GetBytes name

                    if bytes.Length <= oldBytes then
                        Array.blit bytes 0 edited (heap + offset) bytes.Length
                        Array.fill edited (heap + offset + bytes.Length) (oldBytes - bytes.Length + 1) 0uy
                        changed <- true
                | None -> ()

        if changed then Some edited else None

/// Whether `renamed` is `original` with only field names changed, and those
/// only in types that hold locals: every other name the metadata has reads
/// back the same, and every table is the same length.
let private fieldsAgree (original: byte[]) (renamed: byte[]) : bool =
    use a = new PEReader(ImmutableArray.Create<byte>(original))
    use b = new PEReader(ImmutableArray.Create<byte>(renamed))
    let ra = a.GetMetadataReader()
    let rb = b.GetMetadataReader()

    let tables = Enum.GetValues<TableIndex>()

    let named (r: MetadataReader) =
        [ for kind, h in stringNames r do
              if kind <> "field" && kind <> "localref" then
                  yield kind, r.GetString h
          for h in r.TypeDefinitions do
              let t = r.GetTypeDefinition h

              if not (holdsLocals (r.GetString t.Name)) then
                  for f in t.GetFields() do
                      yield "field", r.GetString(r.GetFieldDefinition(f).Name) ]

    // Each reference to a field of a closure or state machine still names a
    // field of the type it refers to.
    let referencesResolve (r: MetadataReader) =
        r.MemberReferences
        |> Seq.map r.GetMemberReference
        |> Seq.filter (refersToLocal r)
        |> Seq.forall (fun m ->
            let spec = r.GetTypeSpecification(TypeSpecificationHandle.op_Explicit m.Parent)
            let mutable blob = r.GetBlobReader spec.Signature
            blob.ReadSignatureTypeCode() |> ignore
            blob.ReadSignatureTypeCode() |> ignore
            let owner = r.GetTypeDefinition(TypeDefinitionHandle.op_Explicit(blob.ReadTypeHandle()))
            let name = r.GetString m.Name

            owner.GetFields()
            |> Seq.exists (fun f -> r.GetString(r.GetFieldDefinition(f).Name) = name))

    tables |> Seq.forall (fun t -> ra.GetTableRowCount t = rb.GetTableRowCount t)
    && named ra = named rb
    && (rb.FieldDefinitions |> Seq.forall (fun h -> rb.GetString(rb.GetFieldDefinition(h).Name) <> ""))
    && referencesResolve rb

/// Renames and hides the locals of `assembly`'s PDB, beside it, if it has one,
/// and the fields of its closures and state machines that hold locals.
/// Never fails a build: what goes wrong leaves the files as Roslyn wrote them.
let rewrite (assembly: string) : unit =
    let pdbPath = Path.ChangeExtension(assembly, ".pdb")

    match Environment.GetEnvironmentVariable "BJOLANG_RAW_PDB" with
    | "1" -> ()
    | _ when not (File.Exists pdbPath && File.Exists assembly) -> ()
    | _ ->
        Timing.phase "pdb names" (fun () ->
            try
                let pdb = File.ReadAllBytes pdbPath
                let dll = File.ReadAllBytes assembly

                let fields =
                    try
                        match renameFields dll with
                        | Some renamed when fieldsAgree dll renamed -> Some renamed
                        | Some _ ->
                            Diagnostics.progress
                                $"The fields of %s{assembly} did not read back as renamed; they keep Roslyn's names."

                            None
                        | None -> None
                    with ex ->
                        Diagnostics.progress $"The fields of %s{assembly} keep Roslyn's names: %s{ex.Message}"
                        None

                let locals =
                    match patch pdb with
                    | Some patched when agrees pdb patched -> Some patched
                    | Some _ ->
                        Diagnostics.progress $"The PDB of %s{assembly} did not read back as patched; it keeps Roslyn's names."
                        None
                    | None -> None

                let dll' = defaultArg fields dll

                // The checksum is of the PDB, and goes in the assembly whose
                // fields are already renamed.
                let checksummed =
                    match locals with
                    | Some patched -> withChecksum dll' patched
                    | None -> None

                locals |> Option.iter (AtomicFile.writeBytes pdbPath)

                match checksummed, fields with
                | Some written, _
                | None, Some written -> AtomicFile.writeBytes assembly written
                | None, None -> ()
            with ex ->
                Diagnostics.progress $"The PDB of %s{assembly} keeps Roslyn's names: %s{ex.Message}")
