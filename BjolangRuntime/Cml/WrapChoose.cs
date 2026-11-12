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
// - Allocation:
//   - Today's form makes a `WrapEvent` per branch, and a `WrapSink` and its
//     `Action` per published branch.
//   - This form makes the event only. The sink and its delegates are kept on
//     the pooled `SyncState` (`SyncState.KeptSink`) and used again at its
//     next root publish of the same form.
//   - A publish that is not at the root makes a sink of its own.
// - Lifecycle: the sink holds the functions and the continuation from the
//   publish until the winner delivers. It clears them before it calls the
//   continuation, so a kept sink does not keep a closure alive.

[NonUserCode]
internal static class WrapChooseOrder
{
    /// <summary>The branch to publish first: 0, or a random one under <c>Cml.RandomizeChoice</c>.</summary>
    public static int Start(int n) => Cml.RandomizeChoice ? (int)(ChoiceRng.Next() % (uint)n) : 0;
}

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

    public void Publish(SyncState state, int eventId, Action<R> onSync)
    {
        Sink sink;
        if (eventId != SyncState.RootEventId) sink = new Sink(state);
        else if (state.KeptSink is Sink kept) sink = kept;
        else state.KeptSink = sink = new Sink(state);

        sink.Arm(_f0, _f1, onSync);

        int start = WrapChooseOrder.Start(2);
        for (int k = 0; k < 2; k++)
        {
            if (state.IsSynchronized) return;
            int i = start + k;
            if (i >= 2) i -= 2;
            if (i == 0) _e0.Publish(state, state.NextEventId(), sink.C0);
            else _e1.Publish(state, state.NextEventId(), sink.C1);
        }
    }

    [NonUserCode]
    private sealed class Sink : IDeferredWrap
    {
        private readonly SyncState _state;
        public readonly Action<T0> C0;
        public readonly Action<T1> C1;
        private Func<T0, R>? _f0;
        private Func<T1, R>? _f1;
        private Action<R>? _onSync;
        private int _index;
        private T0 _v0 = default!;
        private T1 _v1 = default!;

        public Sink(SyncState state)
        {
            _state = state;
            C0 = Commit0;
            C1 = Commit1;
        }

        public void Arm(Func<T0, R> f0, Func<T1, R> f1, Action<R> onSync)
        {
            _f0 = f0; _f1 = f1; _onSync = onSync;
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
                _index = 1; _v1 = v;
                if (host.TryDefer(this)) return;
                _v1 = default!;
            }
            var f = _f1!;
            Disarm()(f(v));
        }

        public void Run()
        {
            if (_index == 0)
            {
                var v = _v0; _v0 = default!;
                var f = _f0!;
                Disarm()(f(v));
            }
            else
            {
                var v = _v1; _v1 = default!;
                var f = _f1!;
                Disarm()(f(v));
            }
        }
    }
}

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

    public void Publish(SyncState state, int eventId, Action<R> onSync)
    {
        Sink sink;
        if (eventId != SyncState.RootEventId) sink = new Sink(state);
        else if (state.KeptSink is Sink kept) sink = kept;
        else state.KeptSink = sink = new Sink(state);

        sink.Arm(_f0, _f1, _f2, onSync);

        int start = WrapChooseOrder.Start(3);
        for (int k = 0; k < 3; k++)
        {
            if (state.IsSynchronized) return;
            int i = start + k;
            if (i >= 3) i -= 3;
            switch (i)
            {
                case 0: _e0.Publish(state, state.NextEventId(), sink.C0); break;
                case 1: _e1.Publish(state, state.NextEventId(), sink.C1); break;
                default: _e2.Publish(state, state.NextEventId(), sink.C2); break;
            }
        }
    }

    [NonUserCode]
    private sealed class Sink : IDeferredWrap
    {
        private readonly SyncState _state;
        public readonly Action<T0> C0;
        public readonly Action<T1> C1;
        public readonly Action<T2> C2;
        private Func<T0, R>? _f0;
        private Func<T1, R>? _f1;
        private Func<T2, R>? _f2;
        private Action<R>? _onSync;
        private int _index;
        private T0 _v0 = default!;
        private T1 _v1 = default!;
        private T2 _v2 = default!;

        public Sink(SyncState state)
        {
            _state = state;
            C0 = Commit0;
            C1 = Commit1;
            C2 = Commit2;
        }

        public void Arm(Func<T0, R> f0, Func<T1, R> f1, Func<T2, R> f2, Action<R> onSync)
        {
            _f0 = f0; _f1 = f1; _f2 = f2; _onSync = onSync;
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
}

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

    public WrapChoose4(IEvent<T0> e0, Func<T0, R> f0, IEvent<T1> e1, Func<T1, R> f1,
                       IEvent<T2> e2, Func<T2, R> f2, IEvent<T3> e3, Func<T3, R> f3)
    {
        _e0 = e0; _f0 = f0; _e1 = e1; _f1 = f1; _e2 = e2; _f2 = f2; _e3 = e3; _f3 = f3;
    }

    public void Publish(SyncState state, int eventId, Action<R> onSync)
    {
        Sink sink;
        if (eventId != SyncState.RootEventId) sink = new Sink(state);
        else if (state.KeptSink is Sink kept) sink = kept;
        else state.KeptSink = sink = new Sink(state);

        sink.Arm(_f0, _f1, _f2, _f3, onSync);

        int start = WrapChooseOrder.Start(4);
        for (int k = 0; k < 4; k++)
        {
            if (state.IsSynchronized) return;
            int i = (start + k) & 3;
            switch (i)
            {
                case 0: _e0.Publish(state, state.NextEventId(), sink.C0); break;
                case 1: _e1.Publish(state, state.NextEventId(), sink.C1); break;
                case 2: _e2.Publish(state, state.NextEventId(), sink.C2); break;
                default: _e3.Publish(state, state.NextEventId(), sink.C3); break;
            }
        }
    }

    [NonUserCode]
    private sealed class Sink : IDeferredWrap
    {
        private readonly SyncState _state;
        public readonly Action<T0> C0;
        public readonly Action<T1> C1;
        public readonly Action<T2> C2;
        public readonly Action<T3> C3;
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

        public Sink(SyncState state)
        {
            _state = state;
            C0 = Commit0;
            C1 = Commit1;
            C2 = Commit2;
            C3 = Commit3;
        }

        public void Arm(Func<T0, R> f0, Func<T1, R> f1, Func<T2, R> f2, Func<T3, R> f3, Action<R> onSync)
        {
            _f0 = f0; _f1 = f1; _f2 = f2; _f3 = f3; _onSync = onSync;
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
}
