using System;
using System.Runtime.CompilerServices;
using Randbjom;

namespace Bjolang.Runtime;

/// <summary>
/// A source of random numbers: either a generator of its own, or the ambient
/// one, which is the calling thread's.
/// </summary>
///
/// <remarks>
/// <para>
/// <c>ChaChaRng</c> is mutable and not thread-safe, and a fiber may resume on a
/// different thread than the one it suspended on. In plain english: A fiber
/// that suspends after storing the random source can read from the random source
/// of another thread. Don't. Just don't. This also applies to
/// (parameterize ((current-random ...)) spawn bjoroutines here)
/// </para>
/// <para>
/// <see cref="Ambient"/> The ambient generator. Which one it means is decided by
/// <see cref="Get"/>, at the draw, so the value that crosses the suspension is
/// stateless and the generator that answers is always the running thread's.
/// This is what <c>current-random</c> holds, and why the default draw needs no
/// lock.
/// </para>
/// <para>
/// A source made by one of the constructors below does name a generator, and
/// two fibers sharing one race. <c>random-split</c> is the way to hand a fiber
/// its own.
/// </para>
/// </remarks>
public sealed class RandomSource {
    /// Null means the calling thread's generator.
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChaChaRng Get() => _own ?? ThreadRng;

    internal static RandomSource Own(ChaChaRng rng) => new(rng);

    public override string ToString() => "#<random-source>";
}

/// <summary>
/// What <c>(std random)</c> binds. Every draw resolves its source first, so a
/// call on the ambient source reads the thread-static field and one on an
/// explicit source does not.
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
    public static RandomSource Split(RandomSource r) =>
        RandomSource.Own(r.Get().Split());

    public static int Range(RandomSource r, int lo, int hi) => r.Get().Range(lo, hi);
    public static double Double(RandomSource r) => r.Get().NextF64();
    public static uint U32(RandomSource r) => r.Get().NextU32();
    public static ulong U64(RandomSource r) => r.Get().NextU64();
    public static bool Bool(RandomSource r) => r.Get().NextBool();
    public static bool Chance(RandomSource r, double p) => r.Get().Chance(p);
    public static void Fill(RandomSource r, byte[] bytes) => r.Get().FillBytes(bytes);
    public static void Shuffle<T>(RandomSource r, T[] array) => r.Get().Shuffle(array);

    /// The generator's own alphabet, `[A-Za-z0-9-_]`. A string over an
    /// alphabet of the caller's own is built in `(std random)` instead:
    /// Bjolang's `char` is `BjoChar` and does not cross as `char[]`.
    public static string Str(RandomSource r, int length) => r.Get().RandomString(length);
}
