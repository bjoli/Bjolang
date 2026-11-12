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

// Generated from one template for 2, 3 and 4 branches by gen_wrapchoose.py
// in this directory. Edit the template there, and run
// `python3 gen_wrapchoose.py > WrapChoose.cs`.

namespace Bjoml;

using System;

// A `choose` of `wrap`s as one event: what the compiler emits for
// `(sync (choose (wrap e0 f0) (wrap e1 f1) ...))` with two to four branches,
// which is also what `select` expands to.
//
// - Same meaning as `ChooseEvent` over `WrapEvent`s:
//   - The branches are published in order (rotated when
//     `Cml.RandomizeChoice` is set), each with its own event id, and
//     publishing stops once the state is synchronized.
//   - A branch's function runs as `WrapSink` runs it: on the syncing side
//     when the sync has a `WrapHost`, else on the thread that committed.
// - Two events carry the branches to the sink:
//   - `WrapChooseN`: an ordinary event, which can be kept and synced again.
//   - `WrapChooseOnceN`: what the compiler emits. It is taken from a free
//     slot of the thread, and its publish puts it back before it publishes a
//     branch. So it must be synced at once, and once. A warm sync of it
//     allocates nothing.
// - The sink (`WrapChooseSinkN`) and its delegates are kept on the pooled
//   `SyncState` (`SyncState.KeptSink`) and used again at its next root
//   publish of the same branch types. A publish below the root makes a sink
//   of its own.
// - Lifecycle: the sink holds the functions and the continuation from the
//   publish until the winner delivers. It clears them before it calls the
//   continuation, so a kept sink does not keep a closure alive.

[NonUserCode]
internal static class WrapChooseOrder
{
    /// <summary>The branch to publish first: 0, or a random one under <c>Cml.RandomizeChoice</c>.</summary>
    public static int Start(int n) => Cml.RandomizeChoice ? (int)(ChoiceRng.Next() % (uint)n) : 0;
}

/// <summary>
/// The sink of a 2-branch <c>WrapChoose</c>, and its publish. See the top of
/// this file.
/// </summary>
[NonUserCode]
internal sealed class WrapChooseSink2<T0, T1, R> : IDeferredWrap
{
    private readonly SyncState _state;
    private readonly Action<T0> _c0;
    private readonly Action<T1> _c1;
    private Func<T0, R>? _f0;
    private Func<T1, R>? _f1;
    private Action<R>? _onSync;
    private int _index;
    private T0 _v0 = default!;
    private T1 _v1 = default!;

    private WrapChooseSink2(SyncState state)
    {
        _state = state;
        _c0 = Commit0;
        _c1 = Commit1;
    }

    /// <summary>
    /// Publish the branches against <paramref name="state"/>, through its kept
    /// sink when this is the root publish.
    /// </summary>
    public static void Publish(SyncState state, int eventId, Action<R> onSync, IEvent<T0> e0, Func<T0, R> f0, IEvent<T1> e1, Func<T1, R> f1)
    {
        WrapChooseSink2<T0, T1, R> sink;
        if (eventId != SyncState.RootEventId) sink = new(state);
        else if (state.KeptSink is WrapChooseSink2<T0, T1, R> kept) sink = kept;
        else state.KeptSink = sink = new(state);

        sink._f0 = f0; sink._f1 = f1; sink._onSync = onSync;

        int start = WrapChooseOrder.Start(2);
        for (int k = 0; k < 2; k++)
        {
            if (state.IsSynchronized) return;
            int i = start + k;
            if (i >= 2) i -= 2;
            switch (i)
            {
                case 0: e0.Publish(state, state.NextEventId(), sink._c0); break;
                default: e1.Publish(state, state.NextEventId(), sink._c1); break;
            }
        }
    }

    private Action<R> Disarm()
    {
        var k = _onSync!;
        _f0 = null; _f1 = null; _onSync = null;
        return k;
    }

    private void Commit0(T0 v)
    {
        if (_state.WrapHost is { } host)
        {
            // Written before the handover, which is a full fence.
            _index = 0; _v0 = v;
            if (host.TryDefer(this)) return;
            _v0 = default!;
        }
        var f = _f0!;
        Disarm()(f(v));
    }

    private void Commit1(T1 v)
    {
        if (_state.WrapHost is { } host)
        {
            // Written before the handover, which is a full fence.
            _index = 1; _v1 = v;
            if (host.TryDefer(this)) return;
            _v1 = default!;
        }
        var f = _f1!;
        Disarm()(f(v));
    }

    public void Run()
    {
        switch (_index)
        {
            case 0: { var v = _v0; _v0 = default!; var f = _f0!; Disarm()(f(v)); break; }
            default: { var v = _v1; _v1 = default!; var f = _f1!; Disarm()(f(v)); break; }
        }
    }
}

/// <summary>A 2-branch <c>WrapChoose</c> that can be kept and synced again.</summary>
[NonUserCode]
public sealed class WrapChoose2<T0, T1, R> : IEvent<R>
{
    private readonly IEvent<T0> _e0;
    private readonly Func<T0, R> _f0;
    private readonly IEvent<T1> _e1;
    private readonly Func<T1, R> _f1;

    public WrapChoose2(IEvent<T0> e0, Func<T0, R> f0, IEvent<T1> e1, Func<T1, R> f1)
    {
        _e0 = e0; _f0 = f0; _e1 = e1; _f1 = f1;
    }

    public void Publish(SyncState state, int eventId, Action<R> onSync) =>
        WrapChooseSink2<T0, T1, R>.Publish(state, eventId, onSync, _e0, _f0, _e1, _f1);
}

/// <summary>
/// A 2-branch <c>WrapChoose</c> for one sync at once, taken from the thread's
/// free slot by <see cref="Rent"/>.
///
/// - Publish reads the branches, clears them and puts this event back in the
///   slot before it publishes a branch. A sync that the publish resumes inline
///   on this thread can then take it again.
/// - A rented event that is never published is dropped, and the slot gets a
///   new one at the next rent.
/// - While <c>FiberTrace</c> is on, each rent is a new event, as a trace
///   keeps the event it noted.
/// </summary>
[NonUserCode]
public sealed class WrapChooseOnce2<T0, T1, R> : IEvent<R>
{
    [ThreadStatic] private static WrapChooseOnce2<T0, T1, R>? t_free;

    private IEvent<T0>? _e0;
    private Func<T0, R>? _f0;
    private IEvent<T1>? _e1;
    private Func<T1, R>? _f1;

    public static WrapChooseOnce2<T0, T1, R> Rent(IEvent<T0> e0, Func<T0, R> f0, IEvent<T1> e1, Func<T1, R> f1)
    {
        var ev = FiberTrace.Enabled ? null : t_free;
        if (ev is null) ev = new();
        else t_free = null;
        ev._e0 = e0; ev._f0 = f0; ev._e1 = e1; ev._f1 = f1;
        return ev;
    }

    public void Publish(SyncState state, int eventId, Action<R> onSync)
    {
        var e0 = _e0!; var f0 = _f0!;
        var e1 = _e1!; var f1 = _f1!;
        _e0 = null; _f0 = null; _e1 = null; _f1 = null;
        t_free = this;
        WrapChooseSink2<T0, T1, R>.Publish(state, eventId, onSync, e0, f0, e1, f1);
    }
}

/// <summary>
/// The sink of a 3-branch <c>WrapChoose</c>, and its publish. See the top of
/// this file.
/// </summary>
[NonUserCode]
internal sealed class WrapChooseSink3<T0, T1, T2, R> : IDeferredWrap
{
    private readonly SyncState _state;
    private readonly Action<T0> _c0;
    private readonly Action<T1> _c1;
    private readonly Action<T2> _c2;
    private Func<T0, R>? _f0;
    private Func<T1, R>? _f1;
    private Func<T2, R>? _f2;
    private Action<R>? _onSync;
    private int _index;
    private T0 _v0 = default!;
    private T1 _v1 = default!;
    private T2 _v2 = default!;

    private WrapChooseSink3(SyncState state)
    {
        _state = state;
        _c0 = Commit0;
        _c1 = Commit1;
        _c2 = Commit2;
    }

    /// <summary>
    /// Publish the branches against <paramref name="state"/>, through its kept
    /// sink when this is the root publish.
    /// </summary>
    public static void Publish(SyncState state, int eventId, Action<R> onSync, IEvent<T0> e0, Func<T0, R> f0, IEvent<T1> e1, Func<T1, R> f1, IEvent<T2> e2, Func<T2, R> f2)
    {
        WrapChooseSink3<T0, T1, T2, R> sink;
        if (eventId != SyncState.RootEventId) sink = new(state);
        else if (state.KeptSink is WrapChooseSink3<T0, T1, T2, R> kept) sink = kept;
        else state.KeptSink = sink = new(state);

        sink._f0 = f0; sink._f1 = f1; sink._f2 = f2; sink._onSync = onSync;

        int start = WrapChooseOrder.Start(3);
        for (int k = 0; k < 3; k++)
        {
            if (state.IsSynchronized) return;
            int i = start + k;
            if (i >= 3) i -= 3;
            switch (i)
            {
                case 0: e0.Publish(state, state.NextEventId(), sink._c0); break;
                case 1: e1.Publish(state, state.NextEventId(), sink._c1); break;
                default: e2.Publish(state, state.NextEventId(), sink._c2); break;
            }
        }
    }

    private Action<R> Disarm()
    {
        var k = _onSync!;
        _f0 = null; _f1 = null; _f2 = null; _onSync = null;
        return k;
    }

    private void Commit0(T0 v)
    {
        if (_state.WrapHost is { } host)
        {
            // Written before the handover, which is a full fence.
            _index = 0; _v0 = v;
            if (host.TryDefer(this)) return;
            _v0 = default!;
        }
        var f = _f0!;
        Disarm()(f(v));
    }

    private void Commit1(T1 v)
    {
        if (_state.WrapHost is { } host)
        {
            // Written before the handover, which is a full fence.
            _index = 1; _v1 = v;
            if (host.TryDefer(this)) return;
            _v1 = default!;
        }
        var f = _f1!;
        Disarm()(f(v));
    }

    private void Commit2(T2 v)
    {
        if (_state.WrapHost is { } host)
        {
            // Written before the handover, which is a full fence.
            _index = 2; _v2 = v;
            if (host.TryDefer(this)) return;
            _v2 = default!;
        }
        var f = _f2!;
        Disarm()(f(v));
    }

    public void Run()
    {
        switch (_index)
        {
            case 0: { var v = _v0; _v0 = default!; var f = _f0!; Disarm()(f(v)); break; }
            case 1: { var v = _v1; _v1 = default!; var f = _f1!; Disarm()(f(v)); break; }
            default: { var v = _v2; _v2 = default!; var f = _f2!; Disarm()(f(v)); break; }
        }
    }
}

/// <summary>A 3-branch <c>WrapChoose</c> that can be kept and synced again.</summary>
[NonUserCode]
public sealed class WrapChoose3<T0, T1, T2, R> : IEvent<R>
{
    private readonly IEvent<T0> _e0;
    private readonly Func<T0, R> _f0;
    private readonly IEvent<T1> _e1;
    private readonly Func<T1, R> _f1;
    private readonly IEvent<T2> _e2;
    private readonly Func<T2, R> _f2;

    public WrapChoose3(IEvent<T0> e0, Func<T0, R> f0, IEvent<T1> e1, Func<T1, R> f1, IEvent<T2> e2, Func<T2, R> f2)
    {
        _e0 = e0; _f0 = f0; _e1 = e1; _f1 = f1; _e2 = e2; _f2 = f2;
    }

    public void Publish(SyncState state, int eventId, Action<R> onSync) =>
        WrapChooseSink3<T0, T1, T2, R>.Publish(state, eventId, onSync, _e0, _f0, _e1, _f1, _e2, _f2);
}

/// <summary>
/// A 3-branch <c>WrapChoose</c> for one sync at once, taken from the thread's
/// free slot by <see cref="Rent"/>.
///
/// - Publish reads the branches, clears them and puts this event back in the
///   slot before it publishes a branch. A sync that the publish resumes inline
///   on this thread can then take it again.
/// - A rented event that is never published is dropped, and the slot gets a
///   new one at the next rent.
/// - While <c>FiberTrace</c> is on, each rent is a new event, as a trace
///   keeps the event it noted.
/// </summary>
[NonUserCode]
public sealed class WrapChooseOnce3<T0, T1, T2, R> : IEvent<R>
{
    [ThreadStatic] private static WrapChooseOnce3<T0, T1, T2, R>? t_free;

    private IEvent<T0>? _e0;
    private Func<T0, R>? _f0;
    private IEvent<T1>? _e1;
    private Func<T1, R>? _f1;
    private IEvent<T2>? _e2;
    private Func<T2, R>? _f2;

    public static WrapChooseOnce3<T0, T1, T2, R> Rent(IEvent<T0> e0, Func<T0, R> f0, IEvent<T1> e1, Func<T1, R> f1, IEvent<T2> e2, Func<T2, R> f2)
    {
        var ev = FiberTrace.Enabled ? null : t_free;
        if (ev is null) ev = new();
        else t_free = null;
        ev._e0 = e0; ev._f0 = f0; ev._e1 = e1; ev._f1 = f1; ev._e2 = e2; ev._f2 = f2;
        return ev;
    }

    public void Publish(SyncState state, int eventId, Action<R> onSync)
    {
        var e0 = _e0!; var f0 = _f0!;
        var e1 = _e1!; var f1 = _f1!;
        var e2 = _e2!; var f2 = _f2!;
        _e0 = null; _f0 = null; _e1 = null; _f1 = null; _e2 = null; _f2 = null;
        t_free = this;
        WrapChooseSink3<T0, T1, T2, R>.Publish(state, eventId, onSync, e0, f0, e1, f1, e2, f2);
    }
}

/// <summary>
/// The sink of a 4-branch <c>WrapChoose</c>, and its publish. See the top of
/// this file.
/// </summary>
[NonUserCode]
internal sealed class WrapChooseSink4<T0, T1, T2, T3, R> : IDeferredWrap
{
    private readonly SyncState _state;
    private readonly Action<T0> _c0;
    private readonly Action<T1> _c1;
    private readonly Action<T2> _c2;
    private readonly Action<T3> _c3;
    private Func<T0, R>? _f0;
    private Func<T1, R>? _f1;
    private Func<T2, R>? _f2;
    private Func<T3, R>? _f3;
    private Action<R>? _onSync;
    private int _index;
    private T0 _v0 = default!;
    private T1 _v1 = default!;
    private T2 _v2 = default!;
    private T3 _v3 = default!;

    private WrapChooseSink4(SyncState state)
    {
        _state = state;
        _c0 = Commit0;
        _c1 = Commit1;
        _c2 = Commit2;
        _c3 = Commit3;
    }

    /// <summary>
    /// Publish the branches against <paramref name="state"/>, through its kept
    /// sink when this is the root publish.
    /// </summary>
    public static void Publish(SyncState state, int eventId, Action<R> onSync, IEvent<T0> e0, Func<T0, R> f0, IEvent<T1> e1, Func<T1, R> f1, IEvent<T2> e2, Func<T2, R> f2, IEvent<T3> e3, Func<T3, R> f3)
    {
        WrapChooseSink4<T0, T1, T2, T3, R> sink;
        if (eventId != SyncState.RootEventId) sink = new(state);
        else if (state.KeptSink is WrapChooseSink4<T0, T1, T2, T3, R> kept) sink = kept;
        else state.KeptSink = sink = new(state);

        sink._f0 = f0; sink._f1 = f1; sink._f2 = f2; sink._f3 = f3; sink._onSync = onSync;

        int start = WrapChooseOrder.Start(4);
        for (int k = 0; k < 4; k++)
        {
            if (state.IsSynchronized) return;
            int i = start + k;
            if (i >= 4) i -= 4;
            switch (i)
            {
                case 0: e0.Publish(state, state.NextEventId(), sink._c0); break;
                case 1: e1.Publish(state, state.NextEventId(), sink._c1); break;
                case 2: e2.Publish(state, state.NextEventId(), sink._c2); break;
                default: e3.Publish(state, state.NextEventId(), sink._c3); break;
            }
        }
    }

    private Action<R> Disarm()
    {
        var k = _onSync!;
        _f0 = null; _f1 = null; _f2 = null; _f3 = null; _onSync = null;
        return k;
    }

    private void Commit0(T0 v)
    {
        if (_state.WrapHost is { } host)
        {
            // Written before the handover, which is a full fence.
            _index = 0; _v0 = v;
            if (host.TryDefer(this)) return;
            _v0 = default!;
        }
        var f = _f0!;
        Disarm()(f(v));
    }

    private void Commit1(T1 v)
    {
        if (_state.WrapHost is { } host)
        {
            // Written before the handover, which is a full fence.
            _index = 1; _v1 = v;
            if (host.TryDefer(this)) return;
            _v1 = default!;
        }
        var f = _f1!;
        Disarm()(f(v));
    }

    private void Commit2(T2 v)
    {
        if (_state.WrapHost is { } host)
        {
            // Written before the handover, which is a full fence.
            _index = 2; _v2 = v;
            if (host.TryDefer(this)) return;
            _v2 = default!;
        }
        var f = _f2!;
        Disarm()(f(v));
    }

    private void Commit3(T3 v)
    {
        if (_state.WrapHost is { } host)
        {
            // Written before the handover, which is a full fence.
            _index = 3; _v3 = v;
            if (host.TryDefer(this)) return;
            _v3 = default!;
        }
        var f = _f3!;
        Disarm()(f(v));
    }

    public void Run()
    {
        switch (_index)
        {
            case 0: { var v = _v0; _v0 = default!; var f = _f0!; Disarm()(f(v)); break; }
            case 1: { var v = _v1; _v1 = default!; var f = _f1!; Disarm()(f(v)); break; }
            case 2: { var v = _v2; _v2 = default!; var f = _f2!; Disarm()(f(v)); break; }
            default: { var v = _v3; _v3 = default!; var f = _f3!; Disarm()(f(v)); break; }
        }
    }
}

/// <summary>A 4-branch <c>WrapChoose</c> that can be kept and synced again.</summary>
[NonUserCode]
public sealed class WrapChoose4<T0, T1, T2, T3, R> : IEvent<R>
{
    private readonly IEvent<T0> _e0;
    private readonly Func<T0, R> _f0;
    private readonly IEvent<T1> _e1;
    private readonly Func<T1, R> _f1;
    private readonly IEvent<T2> _e2;
    private readonly Func<T2, R> _f2;
    private readonly IEvent<T3> _e3;
    private readonly Func<T3, R> _f3;

    public WrapChoose4(IEvent<T0> e0, Func<T0, R> f0, IEvent<T1> e1, Func<T1, R> f1, IEvent<T2> e2, Func<T2, R> f2, IEvent<T3> e3, Func<T3, R> f3)
    {
        _e0 = e0; _f0 = f0; _e1 = e1; _f1 = f1; _e2 = e2; _f2 = f2; _e3 = e3; _f3 = f3;
    }

    public void Publish(SyncState state, int eventId, Action<R> onSync) =>
        WrapChooseSink4<T0, T1, T2, T3, R>.Publish(state, eventId, onSync, _e0, _f0, _e1, _f1, _e2, _f2, _e3, _f3);
}

/// <summary>
/// A 4-branch <c>WrapChoose</c> for one sync at once, taken from the thread's
/// free slot by <see cref="Rent"/>.
///
/// - Publish reads the branches, clears them and puts this event back in the
///   slot before it publishes a branch. A sync that the publish resumes inline
///   on this thread can then take it again.
/// - A rented event that is never published is dropped, and the slot gets a
///   new one at the next rent.
/// - While <c>FiberTrace</c> is on, each rent is a new event, as a trace
///   keeps the event it noted.
/// </summary>
[NonUserCode]
public sealed class WrapChooseOnce4<T0, T1, T2, T3, R> : IEvent<R>
{
    [ThreadStatic] private static WrapChooseOnce4<T0, T1, T2, T3, R>? t_free;

    private IEvent<T0>? _e0;
    private Func<T0, R>? _f0;
    private IEvent<T1>? _e1;
    private Func<T1, R>? _f1;
    private IEvent<T2>? _e2;
    private Func<T2, R>? _f2;
    private IEvent<T3>? _e3;
    private Func<T3, R>? _f3;

    public static WrapChooseOnce4<T0, T1, T2, T3, R> Rent(IEvent<T0> e0, Func<T0, R> f0, IEvent<T1> e1, Func<T1, R> f1, IEvent<T2> e2, Func<T2, R> f2, IEvent<T3> e3, Func<T3, R> f3)
    {
        var ev = FiberTrace.Enabled ? null : t_free;
        if (ev is null) ev = new();
        else t_free = null;
        ev._e0 = e0; ev._f0 = f0; ev._e1 = e1; ev._f1 = f1; ev._e2 = e2; ev._f2 = f2; ev._e3 = e3; ev._f3 = f3;
        return ev;
    }

    public void Publish(SyncState state, int eventId, Action<R> onSync)
    {
        var e0 = _e0!; var f0 = _f0!;
        var e1 = _e1!; var f1 = _f1!;
        var e2 = _e2!; var f2 = _f2!;
        var e3 = _e3!; var f3 = _f3!;
        _e0 = null; _f0 = null; _e1 = null; _f1 = null; _e2 = null; _f2 = null; _e3 = null; _f3 = null;
        t_free = this;
        WrapChooseSink4<T0, T1, T2, T3, R>.Publish(state, eventId, onSync, e0, f0, e1, f1, e2, f2, e3, f3);
    }
}
