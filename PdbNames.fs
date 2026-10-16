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
/// A parameter keeps its C# name: parameters are named in the assembly's own
/// metadata, not in the PDB.
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

/// Renames and hides the locals of `assembly`'s PDB, beside it, if it has one.
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

                match patch pdb with
                | Some patched when agrees pdb patched ->
                    let dll = File.ReadAllBytes assembly
                    let checksummed = withChecksum dll patched
                    AtomicFile.writeBytes pdbPath patched
                    checksummed |> Option.iter (AtomicFile.writeBytes assembly)
                | Some _ -> Diagnostics.progress $"The PDB of %s{assembly} did not read back as patched; it keeps Roslyn's names."
                | None -> ()
            with ex ->
                Diagnostics.progress $"The PDB of %s{assembly} keeps Roslyn's names: %s{ex.Message}")
