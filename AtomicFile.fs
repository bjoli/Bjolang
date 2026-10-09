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

/// Writes a file so that a reader in another process sees all of the old file
/// or all of the new one, and never a part.
///
/// The standard library and the modules of a project are shared by every
/// compiler that runs. One that reads a `.dll` or a build record while another
/// writes it could read half of it. So the new contents go to a file beside
/// the old one, and a rename puts it in place. A rename in one directory is
/// atomic, and a reader that already has the old file open keeps the old file.
module Bjolang.AtomicFile

open System
open System.IO

/// A name beside `path` that no other writer uses. Hidden, so that a file left
/// by a process that was killed while it wrote is out of the way.
let private temporaryBeside (path: string) : string =
    let full = Path.GetFullPath path
    Path.Combine(Path.GetDirectoryName full, "." + Path.GetFileName full + "." + Guid.NewGuid().ToString("N") + ".tmp")

/// Writes `temporary` with `write`, then renames it to `path`.
let private replace (path: string) (write: string -> unit) : unit =
    let temporary = temporaryBeside path

    try
        write temporary
        File.Move(temporary, path, true)
    with _ ->
        (try File.Delete temporary with _ -> ())
        reraise ()

let writeBytes (path: string) (bytes: byte array) : unit =
    replace path (fun t -> File.WriteAllBytes(t, bytes))

let writeText (path: string) (text: string) : unit =
    replace path (fun t -> File.WriteAllText(t, text))

let writeLines (path: string) (lines: string seq) : unit =
    replace path (fun t -> File.WriteAllLines(t, lines))

/// Copies `source` to `path`. The copy has the time `source` was written,
/// as `File.Copy` gives it.
let copy (source: string) (path: string) : unit =
    replace path (fun t -> File.Copy(source, t, true))

/// Moves `source` to `path`, replacing what is there. A copy beside `path` and
/// a rename, not a rename of `source`: `source` can be in another file system,
/// such as a temporary directory, and `File.Move` then copies into `path`
/// itself.
let move (source: string) (path: string) : unit =
    copy source path
    File.Delete source
