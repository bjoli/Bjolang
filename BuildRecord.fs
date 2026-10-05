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

/// Reading the build record a compile leaves beside its source, the
/// `.bjobuild` that `Build.writeBuildRecord` writes and describes.
///
/// The compiler reads it for one thing: early cutoff. A module whose import was
/// rebuilt is still current if what the import offers an importer — its
/// interface — is the same as when the module was built. The record says what
/// that was, and the import's own record says what it is now.
module Bjolang.BuildRecord

open System
open System.IO

type Record =
    { /// The artefact the build produced.
      Output: string
      /// Its last-write time when the record was written. A record whose
      /// artefact has been written since describes some other build.
      Written: int64 option
      /// This module's interface digest. A library that publishes macros has
      /// none: what an importer gets from it is code that runs during the
      /// importer's compile, which no digest of declarations covers.
      Interface: string option
      /// Every assembly the build linked.
      Deps: string list
      /// The interface digest of each linked module as this build saw it. A
      /// linked module missing here is judged by its timestamp.
      InterfaceOf: Map<string, string> }

/// The record beside a source or an artefact: they share a directory and a
/// stem.
let pathFor (sourceOrArtefact: string) : string =
    Path.ChangeExtension(Path.GetFullPath sourceOrArtefact, ".bjobuild")

let digest (text: string) : string =
    Text.Encoding.UTF8.GetBytes text
    |> Security.Cryptography.SHA256.HashData
    |> Convert.ToHexString

let private parse (lines: string seq) : Record =
    let empty =
        { Output = ""
          Written = None
          Interface = None
          Deps = []
          InterfaceOf = Map.empty }

    lines
    |> Seq.fold
        (fun r (line: string) ->
            match line.Split(' ', 2) with
            | [| "output"; v |] -> { r with Output = v }
            | [| "written"; v |] ->
                match Int64.TryParse v with
                | true, n -> { r with Written = Some n }
                | _ -> r
            | [| "interface"; v |] -> { r with Interface = Some v }
            | [| "dep"; v |] -> { r with Deps = v :: r.Deps }
            | [| "interface-of"; v |] ->
                match v.Split(' ', 2) with
                | [| hash; dll |] -> { r with InterfaceOf = Map.add dll hash r.InterfaceOf }
                | _ -> r
            | _ -> r)
        empty

/// The record of the build that produced `artefact`, if that is what is on
/// disk now: a record left from an earlier build, or one whose write failed
/// after the artefact was replaced, answers nothing.
let ofArtefact (artefact: string) : Record option =
    try
        let full = Path.GetFullPath artefact
        let path = pathFor full

        if not (File.Exists path && File.Exists full) then
            None
        else
            let r = parse (File.ReadLines path)

            if r.Output = full && r.Written = Some(File.GetLastWriteTimeUtc(full).Ticks) then
                Some { r with Deps = List.rev r.Deps }
            else
                None
    with _ ->
        None

/// The interface digest of a built library, if its record vouches for one.
let interfaceOf (dll: string) : string option =
    ofArtefact dll |> Option.bind (fun r -> r.Interface)
