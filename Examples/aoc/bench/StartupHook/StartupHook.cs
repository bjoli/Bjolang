// A .NET startup hook for bench.py. The runtime calls Initialize after it has
// started and before Main. The hook starts a clock there, and writes the
// elapsed time to stderr when the process exits. Thus the time leaves out
// runtime startup, but it includes the JIT compilation of the program.
//
// Use: DOTNET_STARTUP_HOOKS=/path/to/StartupHook.dll dotnet dayNN.exe input.txt
using System;
using System.Diagnostics;

internal class StartupHook
{
    public static void Initialize()
    {
        var clock = Stopwatch.StartNew();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Console.Error.WriteLine($"BENCH {clock.Elapsed.TotalSeconds:F4}");
    }
}
