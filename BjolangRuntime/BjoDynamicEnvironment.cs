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

using System;

namespace Bjolang.Runtime;

/// <summary>
///     A dynamic environment taken from the code that made it: the ports, the parameters, the
///     ambient cancellation token and the scope that a spawn enlists in. Opaque to Bjolang:
///     it can only be taken, kept and given to <see cref="DynamicEnvironments.Call{T}" />.
///
///     It is what a spawned child gets (<c>ForChild</c>): everything but the fiber's own park
///     cell, so it can go to another thread, and two threads can use it at once.
/// </summary>
[NonUserCode]
public sealed class DynamicEnvironment
{
    internal readonly object? Env;
    internal DynamicEnvironment(object? env) => Env = env;
}

/// <summary>
///     <c>(current-dynamic-environment)</c> and <c>(call-with-dynamic-environment env thunk)</c>:
///     how work handed to another thread runs as if where it came from. What <c>blocking</c>
///     and <c>spawn/thread</c> do for their thunks, for code that hands work off itself, as a
///     pool of threads written in Bjolang does.
/// </summary>
[NonUserCode]
public static class DynamicEnvironments
{
    /// <summary>The dynamic environment here, for another thread.</summary>
    public static DynamicEnvironment Current() => new(Bjoml.FiberContext.ForChild());

    /// <summary>
    ///     Runs <paramref name="thunk" /> in <paramref name="env" />, and then puts back the
    ///     environment this thread had, also when the thunk raises.
    /// </summary>
    public static T Call<T>(DynamicEnvironment env, Func<T> thunk)
    {
        using (Bjoml.FiberContext.Push(env.Env)) return thunk();
    }
}
