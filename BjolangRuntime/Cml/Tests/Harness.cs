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
using System.Threading;

namespace Bjoml.Tests;

/// <summary>
/// Minimal test harness.
///
/// Every test body runs on its own background thread with a hard timeout, because
/// the most important bugs in this runtime (the self-synchronisation livelock, a
/// dropped promise completion) present as a HANG rather than as an exception. A
/// test framework that simply blocks would tell us nothing; this one reports the
/// hang as a failure and moves on, leaving the wedged thread to die with the
/// process.
/// </summary>
public static class Harness
{
    private static int _passed;
    private static int _failed;

    public const int DefaultTimeoutMs = 10_000;

    public static void Run(string name, Action body, int timeoutMs = DefaultTimeoutMs)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = $"test:{name}",
        };

        thread.Start();

        if (!thread.Join(timeoutMs))
        {
            _failed++;
            Console.WriteLine($"  TIMEOUT  {name}");
            Console.WriteLine($"           still running after {timeoutMs} ms (livelock or lost wakeup)");
            return;
        }

        if (failure != null)
        {
            _failed++;
            Console.WriteLine($"  FAIL     {name}");
            Console.WriteLine($"           {failure.Message}");
            return;
        }

        _passed++;
        Console.WriteLine($"  ok       {name}");
    }

    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void AssertEqual<T>(T expected, T actual, string what)
    {
        if (!Equals(expected, actual))
            throw new AssertionException($"{what}: expected <{expected}>, got <{actual}>");
    }

    /// <summary>Wait for a signal, failing with a useful message instead of hanging.</summary>
    public static void Await(ManualResetEventSlim signal, string what, int timeoutMs = 5000)
    {
        if (!signal.Wait(timeoutMs))
            throw new AssertionException($"timed out after {timeoutMs} ms waiting for {what}");
    }

    /// <summary>Assert a signal does NOT arrive. Used for "this nack must not fire".</summary>
    public static void AssertNoSignal(ManualResetEventSlim signal, string what, int windowMs = 300)
    {
        if (signal.Wait(windowMs))
            throw new AssertionException($"{what} happened but should not have");
    }

    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
    }

    public static int Report()
    {
        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}

public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}
