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

/// One lock for each library module, held while it is built, so that two
/// compilers do not build the same module at the same time.
///
/// Two `bjo` commands at once can find the same module stale: the standard
/// library after a new compiler, or a module two projects share. Each would
/// build it, and each would write its `.dll` while the other reads it.
/// `AtomicFile` makes the writes safe; this makes the second build wait for
/// the first, and then not build at all.
///
/// The lock is a file opened with `FileShare.None`. On Linux and macOS .NET
/// takes an advisory `flock` on it, which excludes every other open with
/// `FileShare.None`, in this process too. The system releases the lock when
/// the process ends, so a compiler that is killed leaves no stale lock.
///
/// The files are in a directory of their own in the temporary directory, one
/// for each source path, and they are never deleted. A lock file deleted when
/// it is released could be opened by a waiter just before the delete, and
/// locked after it: then that waiter and the next one would hold locks on two
/// different files, and both would build.
///
/// Locks are taken in import order: a module's imports are built before the
/// module, and a build holds the lock of one module and waits only for those
/// of its imports. So two compilers can not wait for each other.
module Bjolang.ModuleLock

open System
open System.IO
open System.Text
open System.Threading

let private directory = Path.Combine(Path.GetTempPath(), "bjolang-locks")

let private fileFor (source: string) : string =
    let full = Path.GetFullPath source
    let hash = Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes full) |> Convert.ToHexString
    Path.Combine(directory, hash.Substring(0, 32) + ".lock")

let private tryTake (path: string) : FileStream option =
    try
        Some(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    with :? IOException ->
        None

/// Runs `build` while this process holds the lock of `source`, and answers its
/// exit code.
///
/// When another compiler held the lock first, this waits for it, and then asks
/// `stillNeeded`: that compiler was most likely building the same module, and
/// when it is current now, this one answers 0 and builds nothing. A lock taken
/// at once asks nothing, so a build with no other compiler is the build it was.
let withLock (source: string) (stillNeeded: unit -> bool) (build: unit -> int) : int =
    Directory.CreateDirectory directory |> ignore
    let path = fileFor source

    let held, waited =
        match tryTake path with
        | Some stream -> stream, false
        | None ->
            printfn $"Waiting for another build of %s{Path.GetFileName source}"
            let mutable stream = None

            while stream.IsNone do
                Thread.Sleep 50
                stream <- tryTake path

            stream.Value, true

    use _ = held
    if waited && not (stillNeeded ()) then 0 else build ()
