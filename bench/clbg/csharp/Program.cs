// Single-threaded C# versions of the Benchmarks Game programs that
// bench/clbg/bjolang ports. Each follows the algorithm of the Bjolang
// program. The first argument names the benchmark.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;

static class Program {
    // With --warm as the last argument, the benchmark runs twice: first with
    // its output thrown away, then again, and only the second run is timed.
    // The time goes to standard error as "warm-seconds <s>". Standard input is
    // read once, before either run, and each run reads it from memory.
    static int Main(string[] args) {
        bool warm = args[^1] == "--warm";
        var rest = args[1..(warm ? args.Length - 1 : args.Length)];
        var stdout = Console.OpenStandardOutput();
        if (!warm) return Run(args[0], rest, stdout);

        string input = args[0] is "knucleotide" or "revcomp" ? Console.In.ReadToEnd() : "";
        var realOut = Console.Out;
        Console.SetIn(new StringReader(input));
        Console.SetOut(TextWriter.Null);
        Run(args[0], rest, Stream.Null);
        Console.SetIn(new StringReader(input));
        Console.SetOut(realOut);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int code = Run(args[0], rest, stdout);
        Console.Error.WriteLine($"warm-seconds {watch.Elapsed.TotalSeconds.ToString(CultureInfo.InvariantCulture)}");
        return code;
    }

    static int Run(string name, string[] rest, Stream stdout) {
        switch (name) {
            case "nbody": NBody.Run(rest); break;
            case "spectralnorm": SpectralNorm.Run(rest); break;
            case "fannkuchredux": Fannkuch.Run(rest); break;
            case "binarytrees": BinaryTrees.Run(rest); break;
            case "mandelbrot": Mandelbrot.Run(rest, stdout); break;
            case "fasta": Fasta.Run(rest, stdout); break;
            case "knucleotide": KNucleotide.Run(); break;
            case "revcomp": RevComp.Run(stdout); break;
            case "pidigits": PiDigits.Run(rest); break;
            default: Console.Error.WriteLine($"unknown benchmark {name}"); return 2;
        }
        stdout.Flush();
        return 0;
    }

    public static string F(double x, int digits) =>
        x.ToString("F" + digits, CultureInfo.InvariantCulture);
}

static class NBody {
    sealed class Body {
        public double X, Y, Z, Vx, Vy, Vz;
        public readonly double Mass;
        public Body(double x, double y, double z, double vx, double vy, double vz, double mass) {
            X = x; Y = y; Z = z;
            Vx = vx * DaysPerYear; Vy = vy * DaysPerYear; Vz = vz * DaysPerYear;
            Mass = mass * SolarMass;
        }
    }

    const double Pi = 3.141592653589793;
    const double SolarMass = 4 * Pi * Pi;
    const double DaysPerYear = 365.24;

    static Body[] MakeSystem() => [
        new(0, 0, 0, 0, 0, 0, 1),
        new(4.84143144246472090e+00, -1.16032004402742839e+00, -1.03622044471123109e-01,
            1.66007664274403694e-03, 7.69901118419740425e-03, -6.90460016972063023e-05,
            9.54791938424326609e-04),
        new(8.34336671824457987e+00, 4.12479856412430479e+00, -4.03523417114321381e-01,
            -2.76742510726862411e-03, 4.99852801234917238e-03, 2.30417297573763929e-05,
            2.85885980666130812e-04),
        new(1.28943695621391310e+01, -1.51111514016986312e+01, -2.23307578892655734e-01,
            2.96460137564761618e-03, 2.37847173959480950e-03, -2.96589568540237556e-05,
            4.36624404335156298e-05),
        new(1.53796971148509165e+01, -2.59193146099879641e+01, 1.79258772950371181e-01,
            2.68067772490389322e-03, 1.62824170038242295e-03, -9.51592254519715870e-05,
            5.15138902046611451e-05),
    ];

    static void OffsetMomentum(Body[] bodies) {
        double px = 0, py = 0, pz = 0;
        foreach (var b in bodies) { px += b.Vx * b.Mass; py += b.Vy * b.Mass; pz += b.Vz * b.Mass; }
        var sun = bodies[0];
        sun.Vx = -px / SolarMass; sun.Vy = -py / SolarMass; sun.Vz = -pz / SolarMass;
    }

    static double Energy(Body[] bodies) {
        double e = 0;
        for (int i = 0; i < bodies.Length; i++) {
            var b = bodies[i];
            e += 0.5 * (b.Mass * (b.Vx * b.Vx + (b.Vy * b.Vy + b.Vz * b.Vz)));
            for (int j = i + 1; j < bodies.Length; j++) {
                var b2 = bodies[j];
                double dx = b.X - b2.X, dy = b.Y - b2.Y, dz = b.Z - b2.Z;
                e -= (b.Mass * b2.Mass) / Math.Sqrt(dx * dx + (dy * dy + dz * dz));
            }
        }
        return e;
    }

    static void Advance(Body[] bodies, double dt) {
        for (int i = 0; i < bodies.Length; i++) {
            var b = bodies[i];
            for (int j = i + 1; j < bodies.Length; j++) {
                var b2 = bodies[j];
                double dx = b.X - b2.X, dy = b.Y - b2.Y, dz = b.Z - b2.Z;
                double d2 = dx * dx + (dy * dy + dz * dz);
                double mag = dt / (d2 * Math.Sqrt(d2));
                double bm = b.Mass * mag, b2m = b2.Mass * mag;
                b.Vx -= dx * b2m; b.Vy -= dy * b2m; b.Vz -= dz * b2m;
                b2.Vx += dx * bm; b2.Vy += dy * bm; b2.Vz += dz * bm;
            }
        }
        foreach (var b in bodies) { b.X += dt * b.Vx; b.Y += dt * b.Vy; b.Z += dt * b.Vz; }
    }

    public static void Run(string[] args) {
        int n = int.Parse(args[0]);
        var bodies = MakeSystem();
        OffsetMomentum(bodies);
        Console.WriteLine(Program.F(Energy(bodies), 9));
        for (int i = 0; i < n; i++) Advance(bodies, 0.01);
        Console.WriteLine(Program.F(Energy(bodies), 9));
    }
}

static class SpectralNorm {
    static double A(int i, int j) {
        int ij = i + j;
        return 1.0 / (ij * (ij + 1) / 2 + (i + 1));
    }

    static void MulAv(double[] v, double[] outv) {
        for (int i = 0; i < v.Length; i++) {
            double s = 0;
            for (int j = 0; j < v.Length; j++) s += A(i, j) * v[j];
            outv[i] = s;
        }
    }

    static void MulAtv(double[] v, double[] outv) {
        for (int i = 0; i < v.Length; i++) {
            double s = 0;
            for (int j = 0; j < v.Length; j++) s += A(j, i) * v[j];
            outv[i] = s;
        }
    }

    static void MulAtAv(double[] v, double[] outv, double[] tmp) { MulAv(v, tmp); MulAtv(tmp, outv); }

    public static void Run(string[] args) {
        int n = int.Parse(args[0]);
        var u = new double[n]; var v = new double[n]; var tmp = new double[n];
        Array.Fill(u, 1.0);
        for (int i = 0; i < 10; i++) { MulAtAv(u, v, tmp); MulAtAv(v, u, tmp); }
        double vBv = 0, vv = 0;
        for (int i = 0; i < n; i++) { vBv += u[i] * v[i]; vv += v[i] * v[i]; }
        Console.WriteLine(Program.F(Math.Sqrt(vBv / vv), 9));
    }
}

static class Fannkuch {
    static int CountFlips(int[] perm1, int[] perm) {
        Array.Copy(perm1, perm, perm1.Length);
        int flips = 0;
        for (int k = perm[0]; k != 0; k = perm[0]) {
            for (int i = 0, j = k; i < j; i++, j--) { int t = perm[i]; perm[i] = perm[j]; perm[j] = t; }
            flips++;
        }
        return flips;
    }

    public static void Run(string[] args) {
        int n = int.Parse(args[0]);
        var perm1 = new int[n]; var perm = new int[n]; var count = new int[n];
        for (int i = 0; i < n; i++) perm1[i] = i;
        int r = n, maxFlips = 0, checksum = 0, permCount = 0;
        while (true) {
            for (; r > 1; r--) count[r - 1] = r;
            int flips = CountFlips(perm1, perm);
            maxFlips = Math.Max(maxFlips, flips);
            checksum += permCount % 2 == 0 ? flips : -flips;
            r = 1;
            while (true) {
                if (r == n) {
                    Console.WriteLine($"{checksum}\nPfannkuchen({n}) = {maxFlips}");
                    return;
                }
                int perm0 = perm1[0];
                for (int i = 0; i < r; i++) perm1[i] = perm1[i + 1];
                perm1[r] = perm0;
                count[r]--;
                if (count[r] > 0) break;
                r++;
            }
            permCount++;
        }
    }
}

static class BinaryTrees {
    sealed class Node(Node? left, Node? right) {
        public readonly Node? Left = left, Right = right;
    }

    static Node BottomUp(int depth) =>
        depth > 0 ? new Node(BottomUp(depth - 1), BottomUp(depth - 1)) : new Node(null, null);

    static int Check(Node? t) => t == null ? 0 : 1 + Check(t.Left) + Check(t.Right);

    public static void Run(string[] args) {
        int n = int.Parse(args[0]);
        const int minDepth = 4;
        int maxDepth = Math.Max(minDepth + 2, n);
        int stretchDepth = maxDepth + 1;
        Console.WriteLine($"stretch tree of depth {stretchDepth}\t check: {Check(BottomUp(stretchDepth))}");
        var longLived = BottomUp(maxDepth);
        for (int depth = minDepth; depth <= maxDepth; depth += 2) {
            int iterations = 1 << (maxDepth - depth + minDepth);
            int sum = 0;
            for (int i = 0; i < iterations; i++) sum += Check(BottomUp(depth));
            Console.WriteLine($"{iterations}\t trees of depth {depth}\t check: {sum}");
        }
        Console.WriteLine($"long lived tree of depth {maxDepth}\t check: {Check(longLived)}");
    }
}

static class Mandelbrot {
    static bool Inside(double cr, double ci) {
        double zr = 0, zi = 0, tr = 0, ti = 0;
        for (int i = 0; i < 50 && tr + ti <= 4.0; i++) {
            zi = 2.0 * (zr * zi) + ci;
            zr = tr - ti + cr;
            tr = zr * zr;
            ti = zi * zi;
        }
        return tr + ti <= 4.0;
    }

    public static void Run(string[] args, Stream stdout) {
        int n = int.Parse(args[0]);
        int rowBytes = (n + 7) / 8;
        var bitmap = new byte[rowBytes * n];
        double size = n;
        for (int y = 0; y < n; y++) {
            double ci = 2.0 * y / size - 1.0;
            int bits = 0, at = y * rowBytes;
            for (int x = 0; x < n; x++) {
                double cr = 2.0 * x / size - 1.5;
                bits = (bits << 1) + (Inside(cr, ci) ? 1 : 0);
                if (x % 8 == 7) { bitmap[at++] = (byte)bits; bits = 0; }
            }
            if (n % 8 != 0) bitmap[at] = (byte)(bits << (8 - n % 8));
        }
        var header = Encoding.ASCII.GetBytes($"P4\n{n} {n}\n");
        stdout.Write(header);
        stdout.Write(bitmap);
    }
}

static class Fasta {
    const int LineLength = 60;
    const string Alu =
        "GGCCGGGCGCGGTGGCTCACGCCTGTAATCCCAGCACTTTGGGAGGCCGAGGCGGGCGGATCACCTGAGGTCAGGAGTTCGAGA" +
        "CCAGCCTGGCCAACATGGTGAAACCCCGTCTCTACTAAAAATACAAAAATTAGCCGGGCGTGGTGGCGCGCGCCTGTAATCCCA" +
        "GCTACTCGGGAGGCTGAGGCAGGAGAATCGCTTGAACCCGGGAGGCGGAGGTTGCAGTGAGCCGAGATCGCGCCACTGCACTCC" +
        "AGCCTGGGCGACAGAGCGAGACTCCGTCTCAAAAA";

    const int Im = 139968, Ia = 3877, Ic = 29573;
    static int seed = 42;

    static double Random(double max) {
        seed = (seed * Ia + Ic) % Im;
        return max * seed / Im;
    }

    // Stdout is unbuffered as a Stream, so the writes go through a buffer, as
    // they do for the other languages.
    static void RepeatFasta(Stream outs, byte[] source, int n) {
        var line = new byte[LineLength + 1];
        int k = 0;
        for (int left = n; left > 0; left -= LineLength) {
            int count = Math.Min(left, LineLength);
            for (int i = 0; i < count; i++) {
                line[i] = source[k];
                k = k + 1 == source.Length ? 0 : k + 1;
            }
            line[count] = (byte)'\n';
            outs.Write(line, 0, count + 1);
        }
    }

    static void RandomFasta(Stream outs, byte[] codes, double[] probs, int n) {
        var line = new byte[LineLength + 1];
        int last = probs.Length - 1;
        for (int left = n; left > 0; left -= LineLength) {
            int count = Math.Min(left, LineLength);
            for (int i = 0; i < count; i++) {
                double r = Random(1.0);
                int j = 0;
                while (j < last && r >= probs[j]) j++;
                line[i] = codes[j];
            }
            line[count] = (byte)'\n';
            outs.Write(line, 0, count + 1);
        }
    }

    static double[] Cumulative(double[] ps) {
        var acc = new double[ps.Length];
        double sum = 0;
        for (int i = 0; i < ps.Length; i++) { sum += ps[i]; acc[i] = sum; }
        return acc;
    }

    public static void Run(string[] args, Stream stdout) {
        int n = int.Parse(args[0]);
        seed = 42;  // a warm run runs this twice, from the same seed
        var outs = new BufferedStream(stdout, 1 << 16);
        void Header(string s) => outs.Write(Encoding.ASCII.GetBytes(s));
        Header(">ONE Homo sapiens alu\n");
        RepeatFasta(outs, Encoding.ASCII.GetBytes(Alu), 2 * n);
        Header(">TWO IUB ambiguity codes\n");
        RandomFasta(outs, Encoding.ASCII.GetBytes("acgtBDHKMNRSVWY"),
            Cumulative([0.27, 0.12, 0.12, 0.27, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02, 0.02]),
            3 * n);
        Header(">THREE Homo sapiens frequency\n");
        RandomFasta(outs, Encoding.ASCII.GetBytes("acgt"),
            Cumulative([0.3029549426680, 0.1979883004921, 0.1975473066391, 0.3015094502008]),
            5 * n);
        outs.Flush();
    }
}

static class KNucleotide {
    static byte Code(char c) => c switch {
        'a' or 'A' => 0, 'c' or 'C' => 1, 'g' or 'G' => 2, _ => 3,
    };

    static byte[] ReadSequence(TextReader input) {
        string? line;
        while ((line = input.ReadLine()) != null && !line.StartsWith(">THREE", StringComparison.Ordinal)) { }
        var sb = new StringBuilder();
        while ((line = input.ReadLine()) != null && !line.StartsWith('>')) sb.Append(line);
        var s = sb.ToString();
        var bytes = new byte[s.Length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = Code(s[i]);
        return bytes;
    }

    static Dictionary<long, int> CountKmers(byte[] sq, int k) {
        var counts = new Dictionary<long, int>();
        long mask = (1L << (2 * k)) - 1, key = 0;
        for (int i = 0; i < sq.Length; i++) {
            key = ((key << 2) | sq[i]) & mask;
            if (i >= k - 1) counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts;
    }

    static string KeyToString(long key, int k) {
        var sb = new StringBuilder();
        for (int i = k - 1; i >= 0; i--) sb.Append("ACGT"[(int)((key >> (2 * i)) & 3)]);
        return sb.ToString();
    }

    static long StringToKey(string s) {
        long key = 0;
        foreach (var c in s) key = (key << 2) | Code(c);
        return key;
    }

    static void WriteFrequencies(byte[] sq, int k) {
        var counts = CountKmers(sq, k);
        double total = sq.Length + 1 - k;
        var sorted = counts.Select(kv => (Count: kv.Value, Name: KeyToString(kv.Key, k)))
            .OrderByDescending(e => e.Count).ThenBy(e => e.Name, StringComparer.Ordinal);
        foreach (var (count, name) in sorted)
            Console.WriteLine($"{name} {Program.F(100.0 * count / total, 3)}");
        Console.WriteLine();
    }

    static void WriteCount(byte[] sq, string name) {
        var counts = CountKmers(sq, name.Length);
        Console.WriteLine($"{counts.GetValueOrDefault(StringToKey(name))}\t{name}");
    }

    public static void Run() {
        var sq = ReadSequence(Console.In);
        WriteFrequencies(sq, 1);
        WriteFrequencies(sq, 2);
        foreach (var name in new[] { "GGT", "GGTA", "GGTATT", "GGTATTTTAATT", "GGTATTTTAATTTATAGT" })
            WriteCount(sq, name);
    }
}

static class PiDigits {
    public static void Run(string[] args) {
        int n = int.Parse(args[0]);
        var outs = new StringBuilder();
        System.Numerics.BigInteger acc = 0, den = 1, num = 1;
        int i = 0, k = 0;
        while (i < n) {
            k++;
            System.Numerics.BigInteger k2 = 2 * k + 1;
            acc = (acc + num * 2) * k2;
            den *= k2;
            num *= k;
            if (num > acc) continue;
            var d = (num * 3 + acc) / den;
            if (d != (num * 4 + acc) / den) continue;
            outs.Append((char)('0' + (int)d));
            i++;
            if (i % 10 == 0) outs.Append($"\t:{i}\n");
            acc = (acc - den * d) * 10;
            num *= 10;
        }
        if (n % 10 != 0) {
            outs.Append(' ', 10 - n % 10);
            outs.Append($"\t:{n}\n");
        }
        Console.Write(outs.ToString());
    }
}

static class RevComp {
    const int LineLength = 60;

    static byte[] Complements() {
        var table = new byte[256];
        for (int i = 0; i < 256; i++) table[i] = (byte)i;
        const string from = "ACGTUMRWSYKVHDBN", to = "TGCAAKYWSRMBDHVN";
        for (int i = 0; i < from.Length; i++) {
            table[from[i]] = (byte)to[i];
            table[from[i] + 32] = (byte)to[i];
        }
        return table;
    }

    static void WriteReversed(Stream outs, byte[] table, StringBuilder sb) {
        var sq = Encoding.UTF8.GetBytes(sb.ToString());
        int n = sq.Length;
        if (n == 0) return;
        int lines = (n + LineLength - 1) / LineLength;
        var buf = new byte[n + lines];
        int i = 0, col = 0;
        for (int from = n - 1; from >= 0; from--) {
            if (col == LineLength) { buf[i++] = (byte)'\n'; col = 0; }
            buf[i++] = table[sq[from]];
            col++;
        }
        buf[i] = (byte)'\n';
        outs.Write(buf);
    }

    public static void Run(Stream stdout) {
        var input = Console.In;
        var table = Complements();
        var sb = new StringBuilder();
        string? line;
        while ((line = input.ReadLine()) != null) {
            if (line.StartsWith('>')) {
                WriteReversed(stdout, table, sb);
                stdout.Write(Encoding.UTF8.GetBytes(line + "\n"));
                sb.Clear();
            } else {
                sb.Append(line);
            }
        }
        WriteReversed(stdout, table, sb);
    }
}
