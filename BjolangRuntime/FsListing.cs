// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.
//
// As a special exception to the Mozilla Public License, version 2.0, if you
// compile your application source code and portions of this software are
// embedded into the generated object code or executable form as a normal
// consequence of the compilation process (such as inline functions,
// templates, generics, or macros), you may redistribute such embedded portions
// in such object code or executable form without complying with the source code
// availability requirements or notice obligations of Section 3 of the MPL 2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;

namespace Bjolang.Runtime;

/// One entry of a directory listing: the name as bytes, and the type the
/// listing reported.
///
/// Kind: 0 a file, 1 a directory, 2 a symbolic link (not followed), 3
/// anything else (a FIFO, a socket, a device).
[NonUserCode]
public readonly struct FsListed {
    public readonly byte[] Name;
    public readonly int Kind;

    internal FsListed(byte[] name, int kind) {
        Name = name;
        Kind = kind;
    }
}

/// Directory listings and path kinds for the prelude's `FS` effect.
///
/// - On Linux and Android, `List` calls `opendir` and `readdir64` of libc.
///   The directory and the names are bytes, so a name that is not UTF-8 is
///   listed as it is. The type comes from `d_type`: no `stat` is made, except
///   for an entry whose type the file system does not report.
/// - Elsewhere, `List` uses `FileSystemEnumerable`, with one `stat` for each
///   entry, and names are the UTF-8 bytes of what .NET gives.
[NonUserCode]
public static unsafe class FsListing {
    public const int File = 0;
    public const int Directory = 1;
    public const int Link = 2;
    public const int Other = 3;

    // The values of `d_type`.
    private const byte DtUnknown = 0;
    private const byte DtDir = 4;
    private const byte DtReg = 8;
    private const byte DtLnk = 10;

    // The offsets in `struct dirent64`, the same in glibc, musl and bionic:
    // d_ino (8 bytes), d_off (8), d_reclen (2), d_type (1), d_name.
    private const int DTypeOffset = 18;
    private const int DNameOffset = 19;

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr opendir(byte* name);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr readdir64(IntPtr dir);

    [DllImport("libc")]
    private static extern int closedir(IntPtr dir);

    private static readonly bool UseLibc = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();

    /// The entries of the directory `dir`, without `.` and `..`, in the
    /// order the OS gives them.
    ///
    /// Raises `DirectoryNotFoundException` when `dir` is missing or is not a
    /// directory, `UnauthorizedAccessException` when it cannot be read, and
    /// `IOException` for any other error.
    public static FsListed[] List(byte[] dir) =>
        UseLibc ? ListLibc(dir) : ListDotNet(dir);

    /// What a path names, following symbolic links: -1 when nothing is
    /// there, else `File` or `Directory`, plus 4 when the path itself is a
    /// symbolic link. One `lstat`, and one `stat` more for a link.
    ///
    /// A FIFO, a socket or a device is a `File`, as .NET reports it.
    public static int Classify(FileInfo info) {
        var attributes = info.Attributes;
        if ((int)attributes == -1) return -1;
        int kind = (attributes & FileAttributes.Directory) != 0 ? Directory : File;
        return (attributes & FileAttributes.ReparsePoint) != 0 ? kind + 4 : kind;
    }

    private static FsListed[] ListLibc(byte[] dir) {
        var terminated = new byte[dir.Length + 1];
        Array.Copy(dir, terminated, dir.Length);
        IntPtr handle;
        fixed (byte* p = terminated) handle = opendir(p);
        if (handle == IntPtr.Zero) throw ErrorFor(Marshal.GetLastPInvokeError(), dir);
        var found = new List<FsListed>();
        try {
            while (true) {
                Marshal.SetLastPInvokeError(0);
                IntPtr entry = readdir64(handle);
                if (entry == IntPtr.Zero) {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno != 0) throw ErrorFor(errno, dir);
                    break;
                }
                byte* bytes = (byte*)entry;
                byte* name = bytes + DNameOffset;
                int length = 0;
                while (name[length] != 0) length++;
                if (name[0] == (byte)'.' && (length == 1 || (length == 2 && name[1] == (byte)'.'))) continue;
                var copy = new ReadOnlySpan<byte>(name, length).ToArray();
                int kind = bytes[DTypeOffset] switch {
                    DtReg => File,
                    DtDir => Directory,
                    DtLnk => Link,
                    DtUnknown => KindOfUnreported(dir, copy),
                    _ => Other,
                };
                if (kind >= 0) found.Add(new FsListed(copy, kind));
            }
        } finally {
            closedir(handle);
        }
        return found.ToArray();
    }

    // The type of an entry whose file system does not report one, from an
    // `lstat` through .NET: -1 when it is gone. A name that is not UTF-8
    // cannot be given to .NET, and is `Other`.
    private static int KindOfUnreported(byte[] dir, byte[] name) {
        string path;
        try {
            var strict = new UTF8Encoding(false, true);
            path = strict.GetString(dir) + "/" + strict.GetString(name);
        } catch (DecoderFallbackException) {
            return Other;
        }
        var attributes = new FileInfo(path).Attributes;
        if ((int)attributes == -1) return -1;
        if ((attributes & FileAttributes.ReparsePoint) != 0) return Link;
        return (attributes & FileAttributes.Directory) != 0 ? Directory : File;
    }

    private static FsListed[] ListDotNet(byte[] dir) {
        var path = Encoding.UTF8.GetString(dir);
        var options = new EnumerationOptions {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        var entries = new FileSystemEnumerable<FsListed>(path, Listed, options);
        return new List<FsListed>(entries).ToArray();
    }

    // A named method rather than a lambda: a debugger reads the attribute
    // off a method or its own type, and a lambda's class has neither.
    private static FsListed Listed(ref FileSystemEntry e) {
        var attributes = e.Attributes;
        int kind = (attributes & FileAttributes.ReparsePoint) != 0 ? Link
                 : (attributes & FileAttributes.Directory) != 0 ? Directory
                 : File;
        return new FsListed(Encoding.UTF8.GetBytes(e.FileName.ToString()), kind);
    }

    private static Exception ErrorFor(int errno, byte[] dir) {
        var message = $"{Marshal.GetPInvokeErrorMessage(errno)}: {Encoding.UTF8.GetString(dir)}";
        return errno switch {
            2 or 20 => new DirectoryNotFoundException(message),   // ENOENT, ENOTDIR
            1 or 13 => new UnauthorizedAccessException(message),  // EPERM, EACCES
            _ => new IOException(message),
        };
    }
}
