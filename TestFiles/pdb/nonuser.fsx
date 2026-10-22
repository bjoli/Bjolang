// The runtime's code a debugger would take for the user's own, so that a step
// into a builtin lands in its C#: every method with a line to stop at, in an
// assembly with a PDB beside the compiler, that netcoredbg does not exclude.
// netcoredbg counts any assembly with a PDB as user code and then excludes,
// in `src/metadata/jmc.cpp`, a type marked `DebuggerNonUserCode` or
// `DebuggerStepThrough`, and a method marked either or `DebuggerHidden`. An
// enclosing type does not count, so a lambda, whose method sits in a class of
// the compiler's, has to be marked itself.
//
//   checked <assembly>          an assembly that was looked at
//   user <assembly> <method>    a method of one a debugger would stop in
open System.IO
open System.Collections.Immutable
open System.Reflection.Metadata
open System.Reflection.PortableExecutable

let dir = fsi.CommandLineArgs[1]

let typeExcluding = set [ "DebuggerNonUserCodeAttribute"; "DebuggerStepThroughAttribute" ]
let methodExcluding = typeExcluding.Add "DebuggerHiddenAttribute"

for path in Directory.GetFiles(dir, "*.dll") |> Array.sort do
    let pdb = Path.ChangeExtension(path, ".pdb")

    // The compiler is not part of a program.
    if Path.GetFileName path <> "Bjolang.dll" && File.Exists pdb then
        use pe = new PEReader(File.OpenRead path)
        let md = pe.GetMetadataReader()
        let symbols =
            MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create<byte>(File.ReadAllBytes pdb)).GetMetadataReader()
        let assembly = Path.GetFileNameWithoutExtension path
        printfn "checked %s" assembly

        let names (attributes: CustomAttributeHandleCollection) =
            set [ for h in attributes do
                      let ctor = md.GetCustomAttribute(h).Constructor

                      if ctor.Kind = HandleKind.MemberReference then
                          let parent = md.GetMemberReference(MemberReferenceHandle.op_Explicit ctor).Parent

                          if parent.Kind = HandleKind.TypeReference then
                              md.GetString(md.GetTypeReference(TypeReferenceHandle.op_Explicit parent).Name) ]

        for th in md.TypeDefinitions do
            let t = md.GetTypeDefinition th

            if Set.intersect (names (t.GetCustomAttributes())) typeExcluding |> Set.isEmpty then
                for mh in t.GetMethods() do
                    let m = md.GetMethodDefinition mh
                    let stops =
                        symbols.GetMethodDebugInformation(mh).GetSequencePoints() |> Seq.exists (fun s -> not s.IsHidden)

                    if stops && Set.intersect (names (m.GetCustomAttributes())) methodExcluding |> Set.isEmpty then
                        printfn "user %s %s.%s" assembly (md.GetString t.Name) (md.GetString m.Name)
