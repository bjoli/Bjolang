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

namespace Bjoml.Tests;

public static class Program
{
    public static int Main()
    {
        Scheduler.Start();

        Console.WriteLine("BjoML regression tests");

        CmlTests.RunAll();
        FiberTests.RunAll();
        SpawnBatchTests.RunAll();

        return Harness.Report();
    }
}
