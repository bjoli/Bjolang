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

using System.Collections.Generic;
using System.Runtime.InteropServices;
using Bjoml;

namespace Bjolang.Runtime;

// The signal registrations of (std os).
//
// - A group holds the registrations that one `signal-subscribe` or one
//   `with-signal-cancel` makes. Disposing the group disposes all of them, and
//   the system's default action for each signal is back.
// - The handler that .NET calls sets `Cancel` when the caller asks, so the
//   default action does not occur while the registration lives, and then
//   calls the Bjolang thunk. (std os) asks for every signal whose default
//   action ends or stops the process. For SIGCHLD, SIGCONT and SIGWINCH the
//   default action is to do nothing, and .NET's own handling of them stays.
//   .NET calls it on a thread that is not a fiber, so the thunk follows the
//   rule of (std inbox): it posts, cancels or disposes, and nothing else.
// - A handler may dispose its own group. .NET copies the registrations before
//   it calls the handlers, and holds no lock while they run.
// - This is C# because a Bjolang function is a `Func`, and
//   `PosixSignalRegistration.Create` takes an `Action`.
[NonUserCode]
public sealed class SignalGroup : System.IDisposable
{
    private readonly object _gate = new();

    // Null after Dispose.
    private List<PosixSignalRegistration>? _registrations = new();

    /// <summary>
    /// Registers <paramref name="handler"/> for <paramref name="signal"/>: a
    /// <see cref="PosixSignal"/> value, negative for .NET's names and a raw
    /// signal number above zero. With <paramref name="cancelDefault"/>, the
    /// default action of the signal does not occur. Does nothing after
    /// <see cref="Dispose"/>, so a handler that disposes the group while the
    /// group is being made leaves no registration behind.
    ///
    /// Raises as <see cref="PosixSignalRegistration.Create"/> does when the
    /// system cannot handle the signal.
    /// </summary>
    public Unit Add(int signal, bool cancelDefault, System.Func<Unit> handler)
    {
        lock (_gate) {
            if (_registrations is null) return default;
            _registrations.Add(PosixSignalRegistration.Create(
                (PosixSignal)signal,
                [NonUserCode] (ctx) => {
                    if (cancelDefault) ctx.Cancel = true;
                    try { handler(); }
                    catch (System.Exception e) { Scheduler.ReportUnhandled(e); }
                }));
        }
        return default;
    }

    /// <summary>
    /// Disposes every registration. A second call does nothing. Safe from any
    /// thread, including from a handler of the group.
    /// </summary>
    public void Dispose()
    {
        List<PosixSignalRegistration>? registrations;
        lock (_gate) {
            registrations = _registrations;
            _registrations = null;
        }
        if (registrations is null) return;
        foreach (var r in registrations) r.Dispose();
    }

    /// <summary>
    /// Whether <paramref name="e"/> is how a body leaves after its scope's
    /// token has fired: a `Cancelled`, or the `OperationCanceledException` or
    /// `ObjectDisposedException` of a .NET call that the token stopped. The
    /// rule of the scope's own test, for a caller that knows the token has
    /// fired. `with-signal-cancel` asks this once the signal has cancelled its
    /// scope, and raises every other exception again.
    /// </summary>
    public static bool IsCancellation(System.Exception e) =>
        e is Cancelled or System.OperationCanceledException or System.ObjectDisposedException;
}
