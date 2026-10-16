// What a debugger sees of an assembly's locals, from its PDB, one fact a
// line, for `run_tests.py` to compare:
//
//   shown <name>        a local a debugger shows, under the name it shows
//   hidden <name>       one it hides
//   checksum <bool>     whether the assembly's PdbChecksum is of this PDB
//   id <bool>           whether the assembly's CodeView entry names its ID
open System.IO
open System.Collections.Immutable
open System.Reflection.Metadata
open System.Reflection.PortableExecutable

let assembly = fsi.CommandLineArgs[1]
let bytes = File.ReadAllBytes(Path.ChangeExtension(assembly, ".pdb"))
let provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create<byte>(bytes))
let reader = provider.GetMetadataReader()

for h in reader.LocalVariables do
    let v = reader.GetLocalVariable h
    let hidden = v.Attributes.HasFlag LocalVariableAttributes.DebuggerHidden
    printfn "%s %s" (if hidden then "hidden" else "shown") (reader.GetString v.Name)

let pe = new PEReader(File.OpenRead assembly)

// The fields of the types Roslyn makes to hold locals, closures and state
// machines, which a debugger shows as locals too: `field <name>` as stored.
let metadata = pe.GetMetadataReader()

for th in metadata.TypeDefinitions do
    let t = metadata.GetTypeDefinition th
    let name = metadata.GetString t.Name

    if name.StartsWith "<" && name <> "<Module>" && name <> "<PrivateImplementationDetails>" then
        for f in t.GetFields() do
            printfn "field %s" (metadata.GetString(metadata.GetFieldDefinition(f).Name))

for e in pe.ReadDebugDirectory() do
    match e.Type with
    | DebugDirectoryEntryType.PdbChecksum ->
        let data = pe.ReadPdbChecksumDebugDirectoryData e
        let zeroed = Array.copy bytes
        Array.fill zeroed reader.DebugMetadataHeader.IdStartOffset 20 0uy
        let hash = System.Security.Cryptography.SHA256.HashData zeroed
        printfn "checksum %b" (List.ofSeq data.Checksum = List.ofArray hash)
    | DebugDirectoryEntryType.CodeView ->
        let guid = pe.ReadCodeViewDebugDirectoryData(e).Guid
        let id = reader.DebugMetadataHeader.Id
        printfn "id %b" (guid = System.Guid(Seq.toArray (Seq.take 16 id)))
    | _ -> ()
