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

namespace Bjolang.Runtime;

/// <summary>
/// A copy of at most a given number of bytes, for <c>file-copy</c>.
/// </summary>
///
/// <remarks>
/// <c>Stream.CopyTo</c> copies to the end of the source, and when the target
/// is the source under another name, opened to append, the end moves away as
/// fast as the copy writes: the file grows until the disk is full. Copying the
/// length the source had when it was opened ends.
/// </remarks>
public static class BjoStreams {
    private const int BufferSize = 81920;

    public static void CopyCount(Stream from, Stream to, long count) {
        var buffer = new byte[(int)Math.Min(BufferSize, Math.Max(count, 1))];
        while (count > 0) {
            int n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n == 0) break;
            to.Write(buffer, 0, n);
            count -= n;
        }
    }

    public static async Task CopyCountAsync(Stream from, Stream to, long count, CancellationToken cancel) {
        var buffer = new byte[(int)Math.Min(BufferSize, Math.Max(count, 1))];
        while (count > 0) {
            int n = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancel)
                              .ConfigureAwait(false);
            if (n == 0) break;
            await to.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
            count -= n;
        }
    }
}
