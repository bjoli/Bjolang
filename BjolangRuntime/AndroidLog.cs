/* This Source Code Form is subject to the terms of the Mozilla Public
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
 */

// Standard output and standard error on Android, where an app's file
// descriptors 1 and 2 go nowhere: a stream that writes each line to logcat.

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Bjolang.Runtime;

/// <summary>
/// A write-only stream that writes UTF-8 text to Android's log, one log
/// entry per line.
///
/// - Each line is an entry with the tag <see cref="Tag"/>, without its
///   newline. A line longer than <see cref="MaxEntry"/> bytes is split into
///   several entries, at a character boundary.
/// - The text after the last newline is kept until a newline comes.
///   <see cref="Flush"/> does not write it, because a port flushes at
///   points that are not line ends. <see cref="Dispose(bool)"/> writes it.
/// - Thread safety: the caller serialises writes. The standard ports do.
/// - Only on Android: the log is `liblog.so`.
/// </summary>
[NonUserCode]
public sealed class AndroidLogStream : Stream {
    /// The tag of the program's output in logcat: `adb logcat -s bjo`.
    public const string Tag = "bjo";

    /// The longest entry, in bytes. Logcat cuts an entry at about 4 KB.
    private const int MaxEntry = 4000;

    /// The priorities of `android/log.h`.
    public const int Info = 4;
    public const int Error = 6;

    private static readonly byte[] TagBytes = "bjo\0"u8.ToArray();

    private readonly int _priority;
    private byte[] _line = new byte[256];
    private int _len;

    /// <param name="priority"><see cref="Info"/> or <see cref="Error"/>.</param>
    public AndroidLogStream(int priority) => _priority = priority;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(new ReadOnlySpan<byte>(buffer, offset, count));

    public override void Write(ReadOnlySpan<byte> bytes) {
        while (!bytes.IsEmpty) {
            int nl = bytes.IndexOf((byte)'\n');
            if (nl < 0) {
                Append(bytes);
                return;
            }
            Append(bytes[..nl]);
            WriteLine();
            bytes = bytes[(nl + 1)..];
        }
    }

    /// Keeps the text after the last newline. See the class.
    public override void Flush() { }

    protected override void Dispose(bool disposing) {
        if (_len > 0) WriteLine();
        base.Dispose(disposing);
    }

    private void Append(ReadOnlySpan<byte> bytes) {
        if (_line.Length - _len < bytes.Length)
            Array.Resize(ref _line, Math.Max(_line.Length * 2, _len + bytes.Length));
        bytes.CopyTo(_line.AsSpan(_len));
        _len += bytes.Length;
    }

    /// Writes the kept line as one or more entries, and empties it.
    private void WriteLine() {
        var rest = _line.AsSpan(0, _len);
        do {
            int n = rest.Length;
            if (n > MaxEntry) {
                n = MaxEntry;
                // Not inside a UTF-8 sequence: back off continuation bytes.
                while (n > 0 && (rest[n] & 0xC0) == 0x80) n--;
                if (n == 0) n = MaxEntry;
            }
            WriteEntry(rest[..n]);
            rest = rest[n..];
        } while (!rest.IsEmpty);
        _len = 0;
    }

    private unsafe void WriteEntry(ReadOnlySpan<byte> text) {
        Span<byte> entry = text.Length < 1024 ? stackalloc byte[text.Length + 1] : new byte[text.Length + 1];
        text.CopyTo(entry);
        entry[text.Length] = 0;
        fixed (byte* tag = TagBytes)
        fixed (byte* p = entry) {
            _ = AndroidLogWrite(_priority, tag, p);
        }
    }

    [DllImport("log", EntryPoint = "__android_log_write")]
    private static extern unsafe int AndroidLogWrite(int priority, byte* tag, byte* text);
}
