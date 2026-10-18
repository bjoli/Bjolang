using System;
using static BjolangRuntime;
using BjoMod._2024_ae7e6d86;
using BjoMod.std;
using BjoMod.std.mutable;
using static BjoMod._2024_ae7e6d86.day05_Module;
using static BjoMod.std.prelude_Module;
using static BjoMod.std.eq_Module;
using static BjoMod.std.maths_Module;
using static BjoMod.std.effect_Module;
using static BjoMod.std.syntax_match_Module;
using static BjoMod.std.mutable.set_Module;
using static BjoMod.std.hashset_Module;
using static BjoMod.std.mutable.vec_Module;
using static BjoMod._2024_ae7e6d86.common_Module;

namespace BjoMod._2024_ae7e6d86 {
    public static class day05_Module {
#line (9, 1) - (32, 5) 8 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
        public static int main(Collections.RrbList<BjoString.Utf8String> args) {
#line (10, 14) - (10, 32) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            Collections.RrbList<BjoString.Utf8String> lines = inputsublines(args);
#line (12, 14) - (12, 26) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            System.Collections.Generic.HashSet<BjoString.Utf8String> rules = Bjolang.Runtime.MutableSetModule.Empty<BjoString.Utf8String>();
#line (13, 17) - (13, 22) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            Collections.RrbList<BjoString.Utf8String> loopseq__3 = lines;
#line (13, 3) - (15, 50) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            Bjoml.Unit loopexit__6() {
#line (13, 3) - (15, 50) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                if (false) {
#line (13, 3) - (15, 50) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    _ = default(ValueTuple);
                }
                return default(Bjoml.Unit);
            }
#line (13, 3) - (15, 50) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            BjolangRuntime.VecCursor<BjoString.Utf8String> loopcur__4__113;
            var __init__148 = vecsubcursor(loopseq__3);
            loopcur__4__113 = __init__148;
            while (true) {
#line (13, 3) - (15, 50) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                if (vecsubcursorsubdone_QMARK(loopcur__4__113)) {
#line (13, 3) - (15, 50) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    _ = loopexit__6();
                    break;
                } else {
#line hidden
                    BjoString.Utf8String l = vecsubcursorsubcurrent(loopcur__4__113);
#line (14, 9) - (14, 27) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    if ((l == __Literals.Str.Value)) {
#line (14, 9) - (14, 27) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        _ = loopexit__6();
                        break;
                    } else {
#line (15, 22) - (15, 47) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        bool x__40 = Bjolang.Runtime.MutableSetModule.Add<BjoString.Utf8String>(rules, l);
#line hidden
                        _ = unit;
#line (13, 3) - (15, 50) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        var __next__150 = vecsubcursorsubnext(loopcur__4__113);
                        loopcur__4__113 = __next__150;
                        continue;
                    }
                }
            }
#line (9, 1) - (32, 5) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            bool before_QMARK(int a, int b) {
#line (16, 24) - (16, 94) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                return Bjolang.Runtime.MutableSetModule.Contains<BjoString.Utf8String>(rules, str(new BjoString.Utf8String[] { intsubgtstring(a), __Literals.Str__u7C_.Value, intsubgtstring(b) }));
            }
#line (9, 1) - (32, 5) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            int comparesubpages(int a__134, int b__135) {
#line (17, 30) - (17, 82) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                if (before_QMARK(a__134, b__135)) {
#line (17, 51) - (17, 53) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    return -1;
                } else {
#line (17, 30) - (17, 82) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    if (before_QMARK(b__135, a__134)) {
#line (17, 70) - (17, 71) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        return 1;
                    } else {
#line (17, 79) - (17, 80) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        return 0;
                    }
                }
            }
#line (19, 32) - (19, 37) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            Collections.RrbList<BjoString.Utf8String> loopseq__9 = lines;
#line (18, 16) - (20, 57) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            Collections.RrbList<Collections.RrbList<int>> loopexit__13(Collections.RrbBuilder<Collections.RrbList<int>> comp__8) {
#line hidden
                Collections.RrbList<Collections.RrbList<int>> comp__136 = vecbuildersubgtvec(comp__8);
#line (18, 16) - (20, 57) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                return comp__136;
            }
#line (18, 16) - (20, 57) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            BjolangRuntime.VecCursor<BjoString.Utf8String> loopcur__10__117;
            Collections.RrbBuilder<Collections.RrbList<int>> comp__8__118;
            var __init__151 = vecsubcursor(loopseq__9);
            var __init__152 = vecbuildersubempty<Collections.RrbList<int>>();
            loopcur__10__117 = __init__151;
            comp__8__118 = __init__152;
            Collections.RrbList<Collections.RrbList<int>> updates;
            while (true) {
#line (18, 16) - (20, 57) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                if (vecsubcursorsubdone_QMARK(loopcur__10__117)) {
#line (18, 16) - (20, 57) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    updates = loopexit__13(comp__8__118);
                    break;
                } else {
#line hidden
                    BjoString.Utf8String l__137 = vecsubcursorsubcurrent(loopcur__10__117);
#line (20, 24) - (20, 56) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    if (stringsubcontains_QMARK(l__137, __Literals.Str__u2C_.Value)) {
#line (18, 24) - (18, 66) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        Collections.RrbList<int> x__52 = vecsubmap(((Func<BjoString.Utf8String, int>)((fa0) => stringsubgtint(fa0))), stringsubsplit(l__137, __Literals.Str__u2C_.Value));
#line hidden
                        _ = vecbuildersubadd_BANG(comp__8__118, x__52);
#line (18, 16) - (20, 57) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        Collections.RrbBuilder<Collections.RrbList<int>> comp__138 = comp__8__118;
#line (18, 16) - (20, 57) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        var __next__154 = vecsubcursorsubnext(loopcur__10__117);
                        var __next__155 = comp__138;
                        loopcur__10__117 = __next__154;
                        comp__8__118 = __next__155;
                        continue;
                    } else {
#line (20, 24) - (20, 56) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        var __next__156 = vecsubcursorsubnext(loopcur__10__117);
                        var __next__157 = comp__8__118;
                        loopcur__10__117 = __next__156;
                        comp__8__118 = __next__157;
                        continue;
                    }
                }
            }
#line (9, 1) - (32, 5) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            bool insuborder_QMARK(Collections.RrbList<int> u) {
#line (22, 19) - (22, 39) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                BjoMod.std.prelude__Range loopseq__14 = range(1, (u).Count);
#line (22, 5) - (24, 17) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                bool loopexit__18(bool ok) {
#line (23, 11) - (23, 84) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    bool ok__139 = ok;
#line (24, 14) - (24, 16) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    return ok__139;
                }
#line (22, 5) - (24, 17) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                int loopcur__15__122;
                bool ok__123;
                var __init__158 = loopseq__14.lo;
#line (23, 29) - (23, 31) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                bool s__77 = true;
#line hidden
                bool __hoist__160 = s__77;
                var __init__159 = __hoist__160;
                loopcur__15__122 = __init__158;
                ok__123 = __init__159;
                while (true) {
#line (22, 5) - (24, 17) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    if ((loopcur__15__122 >= loopseq__14.hi)) {
#line (22, 5) - (24, 17) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        return loopexit__18(ok__123);
                    } else {
#line (22, 11) - (22, 40) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        int i = loopcur__15__122;
#line (23, 32) - (23, 82) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        bool x__72;
                        if (ok__123) {
#line hidden
                            int __hoist__162 = (u)[(i)];
#line (23, 71) - (23, 78) 28 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                            int i__69 = (i - 1);
#line hidden
                            int __hoist__163 = (u)[(i__69)];
#line (23, 40) - (23, 81) 28 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                            if (before_QMARK(__hoist__162, __hoist__163)) {
#line (23, 40) - (23, 81) 32 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                                x__72 = false;
                            } else {
#line (23, 40) - (23, 81) 32 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                                x__72 = true;
                            }
                        } else {
#line (23, 32) - (23, 82) 28 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                            x__72 = false;
                        }
#line hidden
                        bool ok__140 = x__72;
#line (22, 5) - (24, 17) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        var __next__164 = (loopcur__15__122 + loopseq__14.by);
                        var __next__165 = ok__140;
                        loopcur__15__122 = __next__164;
                        ok__123 = __next__165;
                        continue;
                    }
                }
            }
#line (9, 1) - (32, 5) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            object /* unresolved assoc */ middle(Collections.RrbList<int> u__141) {
#line (25, 28) - (25, 44) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                int i__80 = ((u__141).Count / 2);
#line hidden
                return (u__141)[(i__80)];
            }
#line (26, 3) - (26, 72) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            subgtstr<int> __hoist__166 = subgtstr_System_Int32.Instance;
#line (26, 40) - (26, 47) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            Collections.RrbList<Collections.RrbList<int>> loopseq__20 = updates;
#line (26, 12) - (26, 71) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            int loopexit__25(int comp__19) {
#line (26, 12) - (26, 71) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                int comp__142 = comp__19;
#line (26, 12) - (26, 71) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                return comp__142;
            }
#line (26, 12) - (26, 71) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            BjolangRuntime.VecCursor<Collections.RrbList<int>> loopcur__21__127;
            int comp__19__128;
            var __init__168 = vecsubcursor(loopseq__20);
            var __init__169 = 0;
            loopcur__21__127 = __init__168;
            comp__19__128 = __init__169;
            int __hoist__167;
            while (true) {
#line (26, 12) - (26, 71) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                if (vecsubcursorsubdone_QMARK(loopcur__21__127)) {
#line (26, 12) - (26, 71) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    __hoist__167 = loopexit__25(comp__19__128);
                    break;
                } else {
#line hidden
                    Collections.RrbList<int> u__143 = vecsubcursorsubcurrent(loopcur__21__127);
#line (26, 49) - (26, 70) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    if (insuborder_QMARK(u__143)) {
#line (26, 21) - (26, 31) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        int x__89 = middle(u__143);
#line hidden
                        int comp__144 = (comp__19__128 + x__89);
#line (26, 12) - (26, 71) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        var __next__171 = vecsubcursorsubnext(loopcur__21__127);
                        var __next__172 = comp__144;
                        loopcur__21__127 = __next__171;
                        comp__19__128 = __next__172;
                        continue;
                    } else {
#line (26, 49) - (26, 70) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        var __next__173 = vecsubcursorsubnext(loopcur__21__127);
                        var __next__174 = comp__19__128;
                        loopcur__21__127 = __next__173;
                        comp__19__128 = __next__174;
                        continue;
                    }
                }
            }
#line (26, 3) - (26, 72) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            _ = println(__hoist__166, __hoist__167);
#line (27, 3) - (31, 58) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            subgtstr<int> __hoist__175 = subgtstr_System_Int32.Instance;
#line (28, 21) - (28, 28) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            Collections.RrbList<Collections.RrbList<int>> loopseq__27 = updates;
#line (27, 12) - (31, 57) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            int loopexit__31(int comp__26) {
#line (27, 12) - (31, 57) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                int comp__145 = comp__26;
#line (27, 12) - (31, 57) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                return comp__145;
            }
#line (27, 12) - (31, 57) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            BjolangRuntime.VecCursor<Collections.RrbList<int>> loopcur__28__132;
            int comp__26__133;
            var __init__177 = vecsubcursor(loopseq__27);
            var __init__178 = 0;
            loopcur__28__132 = __init__177;
            comp__26__133 = __init__178;
            int __hoist__176;
            while (true) {
#line (27, 12) - (31, 57) 16 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                if (vecsubcursorsubdone_QMARK(loopcur__28__132)) {
#line (27, 12) - (31, 57) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    __hoist__176 = loopexit__31(comp__26__133);
                    break;
                } else {
#line hidden
                    Collections.RrbList<int> u__146 = vecsubcursorsubcurrent(loopcur__28__132);
#line (29, 13) - (29, 40) 20 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                    if ((insuborder_QMARK(u__146) ? (bool)(false) : (bool)(true))) {
#line (30, 21) - (30, 40) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        System.Collections.Generic.List<int> m = vecsubgtmutablevec(u__146);
#line (31, 18) - (31, 55) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        Bjolang.Runtime.MutableVecModule.SortBy<int>(m, ((Func<int, int, int>)(comparesubpages)));
#line (27, 21) - (27, 67) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        int x__104 = Bjolang.Runtime.MutableVecModule.Get<int>(m, (Bjolang.Runtime.MutableVecModule.Count<int>(m) / 2));
#line hidden
                        int comp__147 = (comp__26__133 + x__104);
#line (27, 12) - (31, 57) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        var __next__180 = vecsubcursorsubnext(loopcur__28__132);
                        var __next__181 = comp__147;
                        loopcur__28__132 = __next__180;
                        comp__26__133 = __next__181;
                        continue;
                    } else {
#line (29, 13) - (29, 40) 24 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
                        var __next__182 = vecsubcursorsubnext(loopcur__28__132);
                        var __next__183 = comp__26__133;
                        loopcur__28__132 = __next__182;
                        comp__26__133 = __next__183;
                        continue;
                    }
                }
            }
#line (27, 3) - (31, 58) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            _ = println(__hoist__175, __hoist__176);
#line (32, 3) - (32, 4) 12 "/home/linis/Programmering/Bjolang/Examples/aoc/2024/day05.bjo"
            return 0;
        }
#line hidden
    }
#line hidden
    internal static class __Literals {
        public static readonly System.Runtime.CompilerServices.StrongBox<BjoString.Utf8String> Str = new(BjoString.Utf8String.FromUtf8(""u8));
        public static readonly System.Runtime.CompilerServices.StrongBox<BjoString.Utf8String> Str__u7C_ = new(BjoString.Utf8String.FromUtf8("|"u8));
        public static readonly System.Runtime.CompilerServices.StrongBox<BjoString.Utf8String> Str__u2C_ = new(BjoString.Utf8String.FromUtf8(","u8));
    }
}

public static class BjolangEntryPoint {
    private static readonly string[] BjolangProbeDirs = new string[] { @"/home/linis/Programmering/Bjolang/BjolangRuntime/bin/Release/net10.0", @"/home/linis/Programmering/Bjolang/lib/std", @"/home/linis/Programmering/Bjolang/lib/std/mutable", @"/home/linis/Programmering/Bjolang/Examples/aoc/2024" };
    private static readonly string[] BjolangModuleNames = new string[] { @"BjoMod.std.prelude", @"BjoMod.std.eq", @"BjoMod.std.maths", @"BjoMod.std.effect", @"BjoMod.std.syntax_match", @"BjoMod.std.mutable.set", @"BjoMod.std.hashset", @"BjoMod.std.mutable.vec", @"BjoMod._2024_ae7e6d86.common" };
    private static readonly string[] BjolangModulePaths = new string[] { @"/home/linis/Programmering/Bjolang/lib/std/prelude.dll", @"/home/linis/Programmering/Bjolang/lib/std/eq.dll", @"/home/linis/Programmering/Bjolang/lib/std/maths.dll", @"/home/linis/Programmering/Bjolang/lib/std/effect.dll", @"/home/linis/Programmering/Bjolang/lib/std/syntax-match.dll", @"/home/linis/Programmering/Bjolang/lib/std/mutable/set.dll", @"/home/linis/Programmering/Bjolang/lib/std/hashset.dll", @"/home/linis/Programmering/Bjolang/lib/std/mutable/vec.dll", @"/home/linis/Programmering/Bjolang/Examples/aoc/2024/common.dll" };
    private static bool InstallPublishedResolver() {
        var self = typeof(BjolangEntryPoint).Assembly.Location;
        if (string.IsNullOrEmpty(self)) return false;
        var table = System.IO.Path.ChangeExtension(self, ".bjolinks");
        if (!System.IO.File.Exists(table)) return false;
        var baseDir = System.IO.Path.GetDirectoryName(self);
        var managed = new System.Collections.Generic.Dictionary<string, string>();
        var native = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var line in System.IO.File.ReadAllLines(table)) {
            var parts = line.Split('\t');
            if (parts.Length != 3) continue;
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, parts[2]));
            if (parts[0] == "link") managed[parts[1]] = full;
            else if (parts[0] == "native") native[parts[1]] = full;
        }
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
            name.Name != null && managed.TryGetValue(name.Name, out var found) ? context.LoadFromAssemblyPath(found) : null;
        System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, name) => {
            var key = System.Text.RegularExpressions.Regex.Replace(System.IO.Path.GetFileName(name), @"(\.dll|\.dylib|\.so(\.[0-9]+)*)$", "");
            if (key.StartsWith("lib", System.StringComparison.Ordinal)) key = key.Substring(3);
            return native.TryGetValue(key, out var found) ? System.Runtime.InteropServices.NativeLibrary.Load(found) : System.IntPtr.Zero;
        };
        return true;
    }
    private static void InstallAssemblyResolver() {
        if (InstallPublishedResolver()) return;
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
        Collections.RrbList<BjoString.Utf8String> bjoArgs = Collections.RrbList<BjoString.Utf8String>.Create(new System.ReadOnlySpan<BjoString.Utf8String>(BjolangRuntime.StringsFromClr(args)));
        var exitCode = BjolangRuntime.RunMainSync(() => BjoMod._2024_ae7e6d86.day05_Module.main(bjoArgs));
        return exitCode == 0 ? System.Environment.ExitCode : exitCode;
    }
}
