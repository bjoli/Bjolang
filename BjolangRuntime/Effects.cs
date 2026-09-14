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

namespace Bjolang.Runtime {

    /// <summary>
    /// An effect operation was performed with no handler installed and no
    /// default to fall back on.
    ///
    /// Raised by the operation's default handler, which `defeffect` writes in
    /// Bjolang; nothing in the runtime throws this. The type lives here because
    /// `#:catch` names .NET types, and a program that wants to answer for a
    /// missing handler has to be able to name one.
    /// </summary>
    public sealed class Unhandled : System.Exception {

        /// <summary>The operation's name, as it was written in `defeffect`.</summary>
        public readonly string Operation;

        public Unhandled(string operation)
            : base($"no handler for the effect operation '{operation}'") =>
            Operation = operation;
    }
}
