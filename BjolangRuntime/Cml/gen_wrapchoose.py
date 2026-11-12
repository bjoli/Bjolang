# Writes WrapChoose.cs: the 2-, 3- and 4-branch WrapChoose events and their
# sink, from one template.
#
#     python3 gen_wrapchoose.py > WrapChoose.cs
HEADER = '''/* This Source Code Form is subject to the terms of the Mozilla Public
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
'''

def gen(n):
    ts = [f"T{i}" for i in range(n)]
    tp = ", ".join(ts + ["R"])
    params = ", ".join(f"IEvent<T{i}> e{i}, Func<T{i}, R> f{i}" for i in range(n))
    args = ", ".join(f"e{i}, f{i}" for i in range(n))
    fieldargs = ", ".join(f"_e{i}, _f{i}" for i in range(n))
    out = []
    # Sink
    out.append(f'''
/// <summary>
/// The sink of a {n}-branch <c>WrapChoose</c>, and its publish. See the top of
/// this file.
/// </summary>
[NonUserCode]
internal sealed class WrapChooseSink{n}<{tp}> : IDeferredWrap
{{
    private readonly SyncState _state;
''')
    for i in range(n): out.append(f"    private readonly Action<T{i}> _c{i};\n")
    for i in range(n): out.append(f"    private Func<T{i}, R>? _f{i};\n")
    out.append("    private Action<R>? _onSync;\n    private int _index;\n")
    for i in range(n): out.append(f"    private T{i} _v{i} = default!;\n")
    out.append(f'''
    private WrapChooseSink{n}(SyncState state)
    {{
        _state = state;
''')
    for i in range(n): out.append(f"        _c{i} = Commit{i};\n")
    out.append("    }\n")
    # Publish
    out.append(f'''
    /// <summary>
    /// Publish the branches against <paramref name="state"/>, through its kept
    /// sink when this is the root publish.
    /// </summary>
    public static void Publish(SyncState state, int eventId, Action<R> onSync, {params})
    {{
        WrapChooseSink{n}<{tp}> sink;
        if (eventId != SyncState.RootEventId) sink = new(state);
        else if (state.KeptSink is WrapChooseSink{n}<{tp}> kept) sink = kept;
        else state.KeptSink = sink = new(state);

''')
    out.append("        " + " ".join(f"sink._f{i} = f{i};" for i in range(n)) + " sink._onSync = onSync;\n")
    out.append(f'''
        int start = WrapChooseOrder.Start({n});
        for (int k = 0; k < {n}; k++)
        {{
            if (state.IsSynchronized) return;
            int i = start + k;
            if (i >= {n}) i -= {n};
            switch (i)
            {{
''')
    for i in range(n):
        label = f"case {i}" if i < n - 1 else "default"
        out.append(f"                {label}: e{i}.Publish(state, state.NextEventId(), sink._c{i}); break;\n")
    out.append("            }\n        }\n    }\n")
    # Disarm
    out.append(f'''
    private Action<R> Disarm()
    {{
        var k = _onSync!;
        {" ".join(f"_f{i} = null;" for i in range(n))} _onSync = null;
        return k;
    }}
''')
    for i in range(n):
        out.append(f'''
    private void Commit{i}(T{i} v)
    {{
        if (_state.WrapHost is {{ }} host)
        {{
            // Written before the handover, which is a full fence.
            _index = {i}; _v{i} = v;
            if (host.TryDefer(this)) return;
            _v{i} = default!;
        }}
        var f = _f{i}!;
        Disarm()(f(v));
    }}
''')
    out.append("\n    public void Run()\n    {\n        switch (_index)\n        {\n")
    for i in range(n):
        label = f"case {i}" if i < n - 1 else "default"
        out.append(f"            {label}: {{ var v = _v{i}; _v{i} = default!; var f = _f{i}!; Disarm()(f(v)); break; }}\n")
    out.append("        }\n    }\n}\n")
    # Plain event
    out.append(f'''
/// <summary>A {n}-branch <c>WrapChoose</c> that can be kept and synced again.</summary>
[NonUserCode]
public sealed class WrapChoose{n}<{tp}> : IEvent<R>
{{
''')
    for i in range(n): out.append(f"    private readonly IEvent<T{i}> _e{i};\n    private readonly Func<T{i}, R> _f{i};\n")
    out.append(f'''
    public WrapChoose{n}({params})
    {{
        {" ".join(f"_e{i} = e{i}; _f{i} = f{i};" for i in range(n))}
    }}

    public void Publish(SyncState state, int eventId, Action<R> onSync) =>
        WrapChooseSink{n}<{tp}>.Publish(state, eventId, onSync, {fieldargs});
}}
''')
    # Once event
    out.append(f'''
/// <summary>
/// A {n}-branch <c>WrapChoose</c> for one sync at once, taken from the thread's
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
public sealed class WrapChooseOnce{n}<{tp}> : IEvent<R>
{{
    [ThreadStatic] private static WrapChooseOnce{n}<{tp}>? t_free;

''')
    for i in range(n): out.append(f"    private IEvent<T{i}>? _e{i};\n    private Func<T{i}, R>? _f{i};\n")
    out.append(f'''
    public static WrapChooseOnce{n}<{tp}> Rent({params})
    {{
        var ev = FiberTrace.Enabled ? null : t_free;
        if (ev is null) ev = new();
        else t_free = null;
        {" ".join(f"ev._e{i} = e{i}; ev._f{i} = f{i};" for i in range(n))}
        return ev;
    }}

    public void Publish(SyncState state, int eventId, Action<R> onSync)
    {{
''')
    for i in range(n): out.append(f"        var e{i} = _e{i}!; var f{i} = _f{i}!;\n")
    out.append("        " + " ".join(f"_e{i} = null; _f{i} = null;" for i in range(n)) + "\n")
    out.append(f'''        t_free = this;
        WrapChooseSink{n}<{tp}>.Publish(state, eventId, onSync, {args});
    }}
}}
''')
    return "".join(out)

print(HEADER + "".join(gen(n) for n in (2, 3, 4)), end="")
