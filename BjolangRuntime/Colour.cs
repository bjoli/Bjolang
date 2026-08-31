using Bjoml;

namespace Bjolang.Runtime;

/// <summary>
/// A function that does not suspend, seen as one that may.
/// </summary>
///
/// <remarks>
/// <para>
/// This is subeffecting, and it is the only direction that exists: a procedure
/// that never suspends is usable wherever one that may is expected, and the
/// reverse is not, because there is no un-awaiting. The compiler refuses the
/// reverse in <c>unifyEffect</c> and emits a call to this for the safe one.
/// </para>
/// <para>
/// It exists because colour is a *type* on this runtime rather than an
/// annotation. <c>(-&gt; A B)</c> is a <c>Func&lt;A,B&gt;</c> and
/// <c>(-bjo-&gt; A B)</c> is a <c>Func&lt;A,Fiber&lt;B&gt;&gt;</c>, so a slot
/// whose consumer will await cannot hold the first, and something has to bridge
/// the representation. The alternative was to generate a second body for every
/// procedure that might ever be passed somewhere suspending; this is one
/// delegate instead, and costs the same at run time — an <c>async</c> method
/// returning <c>Fiber&lt;T&gt;</c> allocates a <c>FiberCore</c> whether or not
/// it awaits, so the copy was never the cheap option.
/// </para>
/// <para>
/// <b>Lifting does not make a call polite.</b> The lifted function still runs to
/// completion on the fiber that calls it, so lifting one that *parks* — a
/// synchronous read, say — parks that fiber. Nothing here can detect it, which
/// is why the blocking lint treats a lift as a call graph edge: it is the one
/// place a fiber can reach parking code through a value.
/// </para>
/// <para>
/// Arity stops at eight, which is past every arrow anyone writes and matches
/// the bound the class importer already uses when it looks for a generic type.
/// A ninth would be a compiler error naming this file, not silent breakage.
/// </para>
/// <para>
/// The <c>Action</c> overloads are the void-returning half. Bjolang's
/// <c>void</c> is a <c>Func</c> away from being a payload: a suspending arrow is
/// always a <c>Func</c>, because <c>Fiber&lt;Unit&gt;</c> is a real type where
/// <c>void</c> is not, so these return the unit rather than nothing.
/// </para>
/// </remarks>
public static class Colour {
    public static Func<Fiber<TR>> Lift<TR>(Func<TR> f) =>
        async () => f();

    public static Func<T1, Fiber<TR>> Lift<T1, TR>(Func<T1, TR> f) =>
        async a1 => f(a1);

    public static Func<T1, T2, Fiber<TR>> Lift<T1, T2, TR>(Func<T1, T2, TR> f) =>
        async (a1, a2) => f(a1, a2);

    public static Func<T1, T2, T3, Fiber<TR>> Lift<T1, T2, T3, TR>(Func<T1, T2, T3, TR> f) =>
        async (a1, a2, a3) => f(a1, a2, a3);

    public static Func<T1, T2, T3, T4, Fiber<TR>> Lift<T1, T2, T3, T4, TR>(Func<T1, T2, T3, T4, TR> f) =>
        async (a1, a2, a3, a4) => f(a1, a2, a3, a4);

    public static Func<T1, T2, T3, T4, T5, Fiber<TR>> Lift<T1, T2, T3, T4, T5, TR>(Func<T1, T2, T3, T4, T5, TR> f) =>
        async (a1, a2, a3, a4, a5) => f(a1, a2, a3, a4, a5);

    public static Func<T1, T2, T3, T4, T5, T6, Fiber<TR>> Lift<T1, T2, T3, T4, T5, T6, TR>(Func<T1, T2, T3, T4, T5, T6, TR> f) =>
        async (a1, a2, a3, a4, a5, a6) => f(a1, a2, a3, a4, a5, a6);

    public static Func<T1, T2, T3, T4, T5, T6, T7, Fiber<TR>> Lift<T1, T2, T3, T4, T5, T6, T7, TR>(Func<T1, T2, T3, T4, T5, T6, T7, TR> f) =>
        async (a1, a2, a3, a4, a5, a6, a7) => f(a1, a2, a3, a4, a5, a6, a7);

    public static Func<T1, T2, T3, T4, T5, T6, T7, T8, Fiber<TR>> Lift<T1, T2, T3, T4, T5, T6, T7, T8, TR>(Func<T1, T2, T3, T4, T5, T6, T7, T8, TR> f) =>
        async (a1, a2, a3, a4, a5, a6, a7, a8) => f(a1, a2, a3, a4, a5, a6, a7, a8);

    // --- The void-returning half ---------------------------------------------

    public static Func<Fiber<Unit>> Lift(Action f) =>
        async () => { f(); return Unit.Value; };

    public static Func<T1, Fiber<Unit>> Lift<T1>(Action<T1> f) =>
        async a1 => { f(a1); return Unit.Value; };

    public static Func<T1, T2, Fiber<Unit>> Lift<T1, T2>(Action<T1, T2> f) =>
        async (a1, a2) => { f(a1, a2); return Unit.Value; };

    public static Func<T1, T2, T3, Fiber<Unit>> Lift<T1, T2, T3>(Action<T1, T2, T3> f) =>
        async (a1, a2, a3) => { f(a1, a2, a3); return Unit.Value; };

    public static Func<T1, T2, T3, T4, Fiber<Unit>> Lift<T1, T2, T3, T4>(Action<T1, T2, T3, T4> f) =>
        async (a1, a2, a3, a4) => { f(a1, a2, a3, a4); return Unit.Value; };

    public static Func<T1, T2, T3, T4, T5, Fiber<Unit>> Lift<T1, T2, T3, T4, T5>(Action<T1, T2, T3, T4, T5> f) =>
        async (a1, a2, a3, a4, a5) => { f(a1, a2, a3, a4, a5); return Unit.Value; };

    public static Func<T1, T2, T3, T4, T5, T6, Fiber<Unit>> Lift<T1, T2, T3, T4, T5, T6>(Action<T1, T2, T3, T4, T5, T6> f) =>
        async (a1, a2, a3, a4, a5, a6) => { f(a1, a2, a3, a4, a5, a6); return Unit.Value; };

    public static Func<T1, T2, T3, T4, T5, T6, T7, Fiber<Unit>> Lift<T1, T2, T3, T4, T5, T6, T7>(Action<T1, T2, T3, T4, T5, T6, T7> f) =>
        async (a1, a2, a3, a4, a5, a6, a7) => { f(a1, a2, a3, a4, a5, a6, a7); return Unit.Value; };

    public static Func<T1, T2, T3, T4, T5, T6, T7, T8, Fiber<Unit>> Lift<T1, T2, T3, T4, T5, T6, T7, T8>(Action<T1, T2, T3, T4, T5, T6, T7, T8> f) =>
        async (a1, a2, a3, a4, a5, a6, a7, a8) => { f(a1, a2, a3, a4, a5, a6, a7, a8); return Unit.Value; };
}
