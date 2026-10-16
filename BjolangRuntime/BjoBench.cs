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
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Bjolang.Runtime;

/// What `(std bench)` needs of the runtime and cannot say in Bjolang: a place
/// to put a value the JIT may not throw away, the collector's counters, and
/// the facts about the machine a run's header reports. See
/// `Docs/Benchmark-design.org`.
public static class BjoBench {
    // One field per type, so a value is kept without boxing it, which would
    // be an allocation charged to the benchmark.
    static class Sink<T> {
        public static T? Value;
    }

    /// Keeps a benchmark's value. A store to a static field is a side effect
    /// the JIT must perform, so what computed the value is not dead code.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Consume<T>(T value) => Sink<T>.Value = value;

    /// A full, blocking, compacting collection, and the finalizers it made
    /// runnable, so that a sample does not pay for an earlier one's garbage.
    public static void Collect() {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    public static int Collections(int generation) => GC.CollectionCount(generation);

    /// Bytes this thread has allocated: exact, and blind to other threads.
    public static long AllocatedOnThread() => GC.GetAllocatedBytesForCurrentThread();

    /// Bytes the process has allocated, for a body that moves between
    /// threads, as a bjoroutine does.
    public static long AllocatedInProcess() => GC.GetTotalAllocatedBytes(precise: true);

    /// Sorts in place. The statistics sort the same samples many times over,
    /// in a bootstrap, and this is `Array.Sort` on doubles.
    public static void SortDoubles(double[] values) => Array.Sort(values);

    /// The indices of `values` in the order of their values, equal values
    /// keeping their order: what ranking and Holm's correction walk.
    public static int[] ArgSort(double[] values) {
        var order = new int[values.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        var keys = (double[])values.Clone();
        // `Array.Sort` of keys and items is not stable, so ties are broken by
        // the index, which makes it so.
        Array.Sort(order, (a, b) => {
            var c = keys[a].CompareTo(keys[b]);
            return c != 0 ? c : a.CompareTo(b);
        });
        return order;
    }

    // ------------------------------------------------------------------
    // The header
    // ------------------------------------------------------------------

    public static string CpuModel() {
        try {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo")) {
                foreach (var line in File.ReadLines("/proc/cpuinfo")) {
                    if (line.StartsWith("model name", StringComparison.Ordinal)) {
                        var colon = line.IndexOf(':');
                        if (colon >= 0) return line[(colon + 1)..].Trim();
                    }
                }
            }
            if (OperatingSystem.IsMacOS()) {
                var p = Process.Start(new ProcessStartInfo("sysctl", "-n machdep.cpu.brand_string") {
                    RedirectStandardOutput = true, UseShellExecute = false });
                if (p != null) {
                    var text = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    if (text.Length > 0) return text;
                }
            }
            var env = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (!string.IsNullOrEmpty(env)) return env;
        } catch (Exception) {
            // A machine that will not say is reported by its architecture.
        }
        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    public static int Cores() => Environment.ProcessorCount;

    public static long MemoryBytes() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    public static string OperatingSystemName() => RuntimeInformation.OSDescription;

    public static string Architecture() => RuntimeInformation.ProcessArchitecture.ToString();

    public static string DotnetVersion() => RuntimeInformation.FrameworkDescription;

    public static bool ServerGc() => GCSettings.IsServerGC;

    /// The CPU frequency governor on Linux, or "" where there is none to read.
    public static string Governor() {
        try {
            const string path = "/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor";
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        } catch (Exception) {
            return "";
        }
    }

    public static bool DebuggerAttached() => Debugger.IsAttached;

    /// Whether the program was compiled with the JIT optimizer turned off,
    /// which is what a Bjolang `-d` build does.
    public static bool DebugBuild() {
        var entry = Assembly.GetEntryAssembly();
        var attribute = entry?.GetCustomAttribute<DebuggableAttribute>();
        return attribute != null && attribute.IsJITOptimizerDisabled;
    }

    /// A key for the machine: a hash of what makes times differ from one
    /// machine to another, the CPU, the cores, the memory and the system.
    /// Two runs with the same key were taken on the same kind of machine.
    public static string MachineKey() {
        var facts = $"{CpuModel()}|{Cores()}|{MemoryBytes()}|{OperatingSystemName()}|{Architecture()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(facts));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    // ------------------------------------------------------------------
    // The terminal
    // ------------------------------------------------------------------

    // When a run was saved: UTC, to the second, in ISO 8601.
    public static string NowUtc() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static bool OutputIsTerminal() => !Console.IsOutputRedirected;

    public static bool InputIsTerminal() => !Console.IsInputRedirected;
}
