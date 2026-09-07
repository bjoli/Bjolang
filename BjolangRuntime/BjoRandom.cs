using System;
using System.Runtime.CompilerServices;
using Randbjom;

namespace Bjolang.Runtime;

/// <summary>
/// A source of random numbers: either the ambient one, which is the calling
/// thread's, or a generator of the caller's own, which is locked.
/// </summary>
///
/// <remarks>
/// <para>
/// <c>ChaChaRng</c> is mutable and not thread-safe, and a fiber may resume on a
/// different thread than the one it suspended on. The two kinds of source
/// answer that differently, and neither leaves a race for the caller to avoid.
/// </para>
/// <para>
/// <see cref="Ambient"/> names no generator. Which one it means is decided at
/// the draw, so the value that crosses a suspension is stateless and the
/// generator that answers is always the running thread's — no two threads can
/// reach one generator, and nothing locks. This is what <c>current-random</c>
/// holds, so the default path costs a null test and a thread-static load.
/// </para>
/// <para>
/// A source from one of the constructors *does* name a generator, and two
/// fibers may hold it at once. Every operation on one takes its lock. What that
/// buys is safety and not reproducibility: the stream stays a single
/// deterministic sequence, but which fiber gets which value of it is the
/// scheduler's business. A run that has to replay wants a
/// <see cref="RandomModule.Split"/> per fiber.
/// </para>
/// <para>
/// The lock is taken once per *operation* rather than once per draw, so a
/// shuffle or a byte fill is a contiguous slice of the stream rather than one
/// interleaved with another fiber's. That is also why the generator is reached
/// only through <see cref="Borrow"/> and never handed out: a draw written
/// outside the lease is a draw outside the lock, and the guarantee above holds
/// only while every one of them is inside it.
/// </para>
/// </remarks>
public sealed class RandomSource {
    /// Null means the calling thread's generator, and is the whole of the
    /// distinction: null takes the lock-free path, non-null takes its own lock.
    private readonly ChaChaRng? _own;

    private RandomSource(ChaChaRng? own) { _own = own; }

    /// One instance, shared: it has no state to share.
    public static readonly RandomSource Ambient = new(null);

    /// Seeded from the OS CSPRNG on first use, one per thread.
    ///
    /// Not split from a global root generator. Splitting means locking
    /// and we want the draw to be lock-free.
    [ThreadStatic] private static ChaChaRng? _threadRng;

    private static ChaChaRng ThreadRng {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _threadRng ??= new ChaChaRng();
    }

    internal static RandomSource Own(ChaChaRng rng) => new(rng);

    /// The generator to draw from, held for the length of one operation.
    ///
    /// A `ref struct` rather than a callback, because a callback taking the
    /// operation's arguments captures them: `r.Draw(g => g.Range(lo, hi))`
    /// allocates a closure per draw, which costs more than the lock it was
    /// there to take. This allocates nothing and inlines away on the ambient
    /// path.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Lease Borrow() => new(this);

    internal ref struct Lease {
        /// The generator to use. The thread's, or the source's own.
        public readonly ChaChaRng Rng;

        /// What to release, and null when nothing was taken.
        private readonly ChaChaRng? _held;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Lease(RandomSource source) {
            var own = source._own;

            if (own is null) {
                Rng = ThreadRng;
                _held = null;
            } else {
                System.Threading.Monitor.Enter(own);
                Rng = own;
                _held = own;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() {
            if (_held is not null) System.Threading.Monitor.Exit(_held);
        }
    }

    public override string ToString() => "#<random-source>";
}

/// <summary>
/// What <c>(std random)</c> binds. Every entry goes through
/// <see cref="RandomSource.Draw"/>, which is what decides between the
/// thread's generator and a locked one.
/// </summary>
public static class RandomModule {
    public static RandomSource Ambient() => RandomSource.Ambient;

    /// 256 bits from the OS CSPRNG.
    public static RandomSource Fresh() => RandomSource.Own(new ChaChaRng());

    /// Reproducible. The 64 bits are stretched to the 256-bit key by
    /// SplitMix64, so only 2^64 of the generators are reachable.
    public static RandomSource FromSeed64(ulong seed) =>
        RandomSource.Own(new ChaChaRng(seed));

    /// Reproducible, at the generator's full key width. Four words rather than
    /// an array of eight, so the length is checked by the type checker and
    /// there is no malformed seed to throw on.
    ///
    /// Each word is split low half first, which is the order
    /// <c>ChaChaRng</c>'s own seed expansion uses.
    public static RandomSource FromSeed256(ulong a, ulong b, ulong c, ulong d) {
        var words = new[] { a, b, c, d };
        var key = new uint[8];
        for (int i = 0; i < 4; i++) {
            key[i * 2] = (uint)words[i];
            key[i * 2 + 1] = (uint)(words[i] >> 32);
        }
        return RandomSource.Own(new ChaChaRng(key));
    }

    /// Consumes the source's state to produce an independent generator. On the
    /// ambient source this draws from the calling thread's.
    ///
    /// A draw like any other, so splitting a shared source is safe from two
    /// fibers at once and hands each a stream nothing else will produce.
    public static RandomSource Split(RandomSource r) {
        using var g = r.Borrow();
        return RandomSource.Own(g.Rng.Split());
    }

    public static int Range(RandomSource r, int lo, int hi) {
        using var g = r.Borrow();
        return g.Rng.Range(lo, hi);
    }

    public static double Double(RandomSource r) {
        using var g = r.Borrow();
        return g.Rng.NextF64();
    }

    public static uint U32(RandomSource r) {
        using var g = r.Borrow();
        return g.Rng.NextU32();
    }

    public static ulong U64(RandomSource r) {
        using var g = r.Borrow();
        return g.Rng.NextU64();
    }

    public static bool Bool(RandomSource r) {
        using var g = r.Borrow();
        return g.Rng.NextBool();
    }

    public static bool Chance(RandomSource r, double p) {
        using var g = r.Borrow();
        return g.Rng.Chance(p);
    }

    // The three below are many draws each and hold the lease across all of
    // them: an interleaved shuffle is well defined, but it is not the
    // permutation the stream describes.
    public static void Fill(RandomSource r, byte[] bytes) {
        using var g = r.Borrow();
        g.Rng.FillBytes(bytes);
    }

    public static void Shuffle<T>(RandomSource r, T[] array) {
        using var g = r.Borrow();
        g.Rng.Shuffle(array);
    }

    /// The generator's own alphabet, `[A-Za-z0-9-_]`. A string over an
    /// alphabet of the caller's own is built in `(std random)` instead:
    /// Bjolang's `char` is `BjoChar` and does not cross as `char[]`.
    public static string Str(RandomSource r, int length) {
        using var g = r.Borrow();
        return g.Rng.RandomString(length);
    }
}
