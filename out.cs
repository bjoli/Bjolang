using System;
using static BjolangRuntime;
using BjoMod.Playground_4c49ceaf;
using BjoMod.std;
using static BjoMod.Playground_4c49ceaf.rec_Module;
using static BjoMod.std.prelude_Module;
using static BjoMod.std.eq_Module;
using static BjoMod.std.maths_Module;
using static BjoMod.std.effect_Module;
using static BjoMod.std.syntax_match_Module;

namespace BjoMod.Playground_4c49ceaf {
    public static class rec_Module {
#line (2, 1) - (2, 2) 8 "/home/linis/Programmering/Bjolang/Playground/part.bjo"
        public static string helper() {
#line (2, 17) - (2, 35) 12 "/home/linis/Programmering/Bjolang/Playground/part.bjo"
            return "from the include";
        }
#line hidden
#line (4, 1) - (4, 2) 8 "/home/linis/Programmering/Bjolang/Playground/rec.bjo"
        public static int main(Collections.RrbList<string> args) {
#line (5, 3) - (6, 4) 12 "/home/linis/Programmering/Bjolang/Playground/rec.bjo"
            _ = println(subgtstr_System_String.Instance, rec_Module.helper());
#line (6, 3) - (7, 4) 12 "/home/linis/Programmering/Bjolang/Playground/rec.bjo"
            _ = println(subgtstr_System_String.Instance, str(new string[] { "args=", intsubgtstring(vecsublength(args)) }));
#line (7, 3) - (7, 4) 12 "/home/linis/Programmering/Bjolang/Playground/rec.bjo"
            return 7;
        }
#line hidden
    }
}

public static class BjolangEntryPoint {
    private static readonly string[] BjolangProbeDirs = new string[] { @"/home/linis/Programmering/Bjolang/BjolangRuntime/bin/Release/net10.0", @"/home/linis/Programmering/Bjolang/lib/std" };
    private static readonly string[] BjolangModuleNames = new string[] { @"BjoMod.std.prelude", @"BjoMod.std.eq", @"BjoMod.std.maths", @"BjoMod.std.effect", @"BjoMod.std.syntax_match" };
    private static readonly string[] BjolangModulePaths = new string[] { @"/home/linis/Programmering/Bjolang/lib/std/prelude.dll", @"/home/linis/Programmering/Bjolang/lib/std/eq.dll", @"/home/linis/Programmering/Bjolang/lib/std/maths.dll", @"/home/linis/Programmering/Bjolang/lib/std/effect.dll", @"/home/linis/Programmering/Bjolang/lib/std/syntax-match.dll" };
    private static void InstallAssemblyResolver() {
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) => {
            var libOverride = System.Environment.GetEnvironmentVariable("BJOLANG_LIB");
            if (!string.IsNullOrEmpty(libOverride) && name.Name != null && name.Name.StartsWith("BjoMod.")) {
                var relative = name.Name.Substring(7).Replace('.', System.IO.Path.DirectorySeparatorChar) + ".dll";
                var overridden = System.IO.Path.Combine(libOverride, relative);
                if (System.IO.File.Exists(overridden)) return context.LoadFromAssemblyPath(overridden);
            }
            for (int i = 0; i < BjolangModuleNames.Length; i++) {
                if (BjolangModuleNames[i] == name.Name && System.IO.File.Exists(BjolangModulePaths[i]))
                    return context.LoadFromAssemblyPath(BjolangModulePaths[i]);
            }
            foreach (var dir in BjolangProbeDirs) {
                var candidate = System.IO.Path.Combine(dir, name.Name + ".dll");
                if (System.IO.File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
            }
            return null;
        };
    }
    public static int Main(string[] args) {
        InstallAssemblyResolver();
        return Run(args);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Run(string[] args) {
        Collections.RrbList<string> bjoArgs = Collections.RrbList<string>.Create(new System.ReadOnlySpan<string>(args));
        return BjolangRuntime.RunMainSync(() => BjoMod.Playground_4c49ceaf.rec_Module.main(bjoArgs));
    }
}
