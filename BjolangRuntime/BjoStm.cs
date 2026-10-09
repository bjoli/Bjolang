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

/* The design of this file, and the algorithms of `Mcas` and `Tx`, are taken
 * from kcas (https://github.com/ocaml-multicore/kcas). No code is copied, but
 * the structure follows kcas closely, so its licence applies to those parts:
 *
 * Copyright (c) 2016, KC Sivaramakrishnan <kc@kcsrk.info>
 * Copyright (c) 2017, Nicolas ASSOUAD <nicolas.assouad@ens.fr>
 * Copyright (c) 2018, Sadiq Jaffer
 * Copyright (c) 2023, Vesa Karvonen <vesa.a.j.k@gmail.com>
 *
 * Permission to use, copy, modify, and/or distribute this software for any
 * purpose with or without fee is hereby granted, provided that the above
 * copyright notice and this permission notice appear in all copies.
 *
 * THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
 * WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
 * ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
 * WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
 * ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
 * OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
 */

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using Bjoml;

namespace Bjolang.Runtime;

// The software transactional memory of (std stm).
//
// A transaction is a function of a log, `Tx`. The log records the state of
// each Loc that the function reads, and the value that it writes. No Loc
// changes until the commit, which is one multi-word compare-and-swap (MCAS)
// over the Locs in the log.
//
// FROM KCAS
//
// - The MCAS is the GKMZ algorithm: Guerraoui, Kogan, Marathe and Zablotskiy,
//   "Efficient Multi-word Compare and Swap", DISC 2020. A commit installs a
//   new State in each Loc it writes, in Loc id order, and then sets the status
//   of its Casn with one compare-and-swap. A thread that finds an undetermined
//   State helps that commit to finish. The order of the Locs makes sure that
//   two commits can not help each other in a cycle.
// - A Loc that was only read is not written by the commit (a CMP). The commit
//   checks that the Loc still holds the State that the transaction read. Thus
//   readers do not write shared memory and do not slow each other down. A CMP
//   fails if any other commit has installed a State in the Loc, even one that
//   is not finished. Thus a transaction that reads and writes is
//   obstruction-free, not lock-free. kcas has a lock-free mode for this case;
//   this file does not.
// - When a CMP comes before a CAS in Loc order, the CMP was checked before all
//   writes were installed. The commit then checks all CMPs again before it
//   sets the status (`verify` in kcas).
// - The log is validated when the count of accesses gets to a power of two.
//   Thus a transaction that reads an inconsistent snapshot and loops over the
//   same Locs stops. The log is also validated before an exception from the
//   transaction is raised, because the snapshot can be the cause.
//
// NOT FROM KCAS
//
// - A version is compared by identity, not a value by value. Each successful
//   write makes a new version. Thus the MCAS can compare any T with no
//   equality, and values that are equal but come from different writes are
//   different versions.
// - The waiters of a Loc are in a list on the Loc, not in its State.
// - Waiting is a CML event. See `AtomicallyEvent`.
//
// THE STATE OF A LOC
//
// A State has a value, the version it replaces (`Prev`), and the Casn of the
// commit that installed it. The status of the Casn gives the version that the
// Loc holds:
//
//   After          the State itself, with the value `Value`
//   Before         `Prev`, with the value `Prev.Value`
//   undetermined   the reader helps the commit to finish, then as above
//
// A version is always a State whose status is After. When a commit is
// finished, it clears the field that its States do not use any more. Thus a
// Loc does not keep a chain of old versions alive.

/// <summary>The status of one commit.</summary>
internal sealed class Casn
{
    internal static readonly object Before = new();
    internal static readonly object After = new();

    /// A finished commit. A new Loc and a commit with one Loc use it.
    internal static readonly Casn Done = new(After);

    /// The log of the commit, sorted by Loc id (an `Entry[]`), until the
    /// commit is finished. Then `Before` or `After`.
    internal object Status;

    internal Casn(object status) => Status = status;
}

internal sealed class State<T>
{
    internal T Value;
    internal State<T>? Prev;
    internal readonly Casn Casn;

    internal State(T value, State<T>? prev, Casn casn)
    {
        Value = value;
        Prev = prev;
        Casn = casn;
    }

    /// The version that this State stands for. If the commit that installed
    /// it is not finished, this helps it to finish first.
    internal State<T> Version()
    {
        object status = Volatile.Read(ref Casn.Status);
        if (ReferenceEquals(status, Casn.After)) return this;
        if (ReferenceEquals(status, Casn.Before)) return Prev!;
        return Mcas.Determine(Casn) ? this : Prev!;
    }
}

internal enum InstallResult { Installed, Failed, Determined }

/// <summary>One Loc in the log of a transaction.</summary>
internal abstract class Entry
{
    internal readonly LocBase Loc;
    internal bool Written;

    protected Entry(LocBase loc) => Loc = loc;

    /// The Loc still holds the State that the transaction read.
    internal abstract bool ReadIsCurrent();

    /// Make the State that the commit installs. Called before the Casn is
    /// visible to other threads.
    internal abstract void Freeze(Casn casn);

    internal abstract InstallResult Install(Casn casn);

    /// Clear what the States do not use any more, and wake the waiters if the
    /// commit succeeded. Called once, by the thread that finished the commit.
    internal abstract void Release(bool after);

    /// Commit a log that has only this Loc, with one compare-and-swap.
    internal abstract bool CommitAlone();

    internal abstract object? SaveValue();
    internal abstract void Restore(object? value, bool written);
}

internal sealed class Entry<T> : Entry
{
    private readonly Loc<T> _loc;
    private readonly State<T> _read;
    private readonly State<T> _version;
    private State<T>? _desired;
    internal T Value;

    internal Entry(Loc<T> loc, State<T> read, State<T> version) : base(loc)
    {
        _loc = loc;
        _read = read;
        _version = version;
        Value = version.Value;
    }

    internal override bool ReadIsCurrent() => ReferenceEquals(Volatile.Read(ref _loc.Current), _read);

    internal override void Freeze(Casn casn) => _desired = new State<T>(Value, _version, casn);

    internal override InstallResult Install(Casn casn)
    {
        var desired = _desired!;
        while (true)
        {
            var current = Volatile.Read(ref _loc.Current);
            if (ReferenceEquals(current, desired)) return InstallResult.Installed;

            // A helper that is late must not install a State after the commit
            // is finished.
            if (Volatile.Read(ref casn.Status) is not Entry[]) return InstallResult.Determined;

            if (!ReferenceEquals(current.Version(), _version)) return InstallResult.Failed;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _loc.Current, desired, current), current))
                return InstallResult.Installed;
        }
    }

    internal override void Release(bool after)
    {
        var desired = _desired!;
        if (after)
        {
            desired.Prev = null;
            _loc.Wake();
        }
        else
        {
            desired.Value = default!;
        }
    }

    internal override bool CommitAlone()
    {
        var desired = new State<T>(Value, null, Casn.Done);
        while (true)
        {
            var current = Volatile.Read(ref _loc.Current);
            if (!ReferenceEquals(current.Version(), _version)) return false;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _loc.Current, desired, current), current))
            {
                _loc.Wake();
                return true;
            }
        }
    }

    internal override object? SaveValue() => Value;

    internal override void Restore(object? value, bool written)
    {
        Value = (T)value!;
        Written = written;
    }
}

/// <summary>The MCAS of a commit, which other threads can also run to help it.</summary>
internal static class Mcas
{
    /// Finish the commit, and tell if it succeeded.
    internal static bool Determine(Casn casn)
    {
        object status = Volatile.Read(ref casn.Status);
        if (status is not Entry[] ops) return ReferenceEquals(status, Casn.After);

        bool sawCmp = false;
        bool verify = false;
        foreach (var op in ops)
        {
            if (!op.Written)
            {
                if (!op.ReadIsCurrent()) return Finish(casn, ops, false);
                sawCmp = true;
                continue;
            }

            switch (op.Install(casn))
            {
                case InstallResult.Installed:
                    verify |= sawCmp;
                    break;
                case InstallResult.Failed:
                    return Finish(casn, ops, false);
                default:
                    return ReferenceEquals(Volatile.Read(ref casn.Status), Casn.After);
            }
        }

        if (verify)
        {
            foreach (var op in ops)
                if (!op.Written && !op.ReadIsCurrent()) return Finish(casn, ops, false);
        }

        return Finish(casn, ops, true);
    }

    private static bool Finish(Casn casn, Entry[] ops, bool success)
    {
        object final = success ? Casn.After : Casn.Before;
        if (ReferenceEquals(Interlocked.CompareExchange(ref casn.Status, final, ops), ops))
        {
            foreach (var op in ops)
                if (op.Written) op.Release(success);
            return success;
        }

        return ReferenceEquals(Volatile.Read(ref casn.Status), Casn.After);
    }
}

/// <summary>
/// A transactional location: <c>(Loc T)</c> in Bjolang. Make one with
/// <see cref="StmModule.MakeLoc{T}"/>.
/// </summary>
public abstract class LocBase
{
    private static long s_nextId;

    /// The order in which a commit installs its States.
    internal readonly long Id = Interlocked.Increment(ref s_nextId);

    private List<StmBlock>? _waiters;
    private int _purgeAt = MinPurge;
    private const int MinPurge = 8;

    private protected LocBase() { }

    /// For the tests.
    internal int WaiterCount
    {
        get { lock (this) return _waiters?.Count ?? 0; }
    }

    /// A waiter whose sync was won by another branch stays in the list until
    /// the Loc is written. Thus the list is purged when it gets to twice its
    /// size after the last purge, and a Loc that is not written does not grow
    /// without limit.
    internal void AddWaiter(StmBlock block)
    {
        lock (this)
        {
            var list = _waiters ??= new List<StmBlock>();
            if (list.Count >= _purgeAt)
            {
                list.RemoveAll(static b => b.IsDead);
                _purgeAt = Math.Max(MinPurge, list.Count * 2);
            }
            list.Add(block);
        }
    }

    /// Called after a commit has changed the version of this Loc. A waiter
    /// that adds itself at the same time does not need this call: it validates
    /// its log after it is added, and sees the new State.
    internal void Wake()
    {
        if (Volatile.Read(ref _waiters) is null) return;

        List<StmBlock>? list;
        lock (this)
        {
            list = _waiters;
            _waiters = null;
            _purgeAt = MinPurge;
        }

        if (list is null) return;
        foreach (var block in list) block.Fire();
    }
}

public sealed class Loc<T> : LocBase
{
    internal State<T> Current;

    internal Loc(T value) => Current = new State<T>(value, null, Casn.Done);

    internal T Read() => Volatile.Read(ref Current).Version().Value;
}

/// <summary>
/// The signal that stops a transaction to retry or to start again. The flags
/// on the <see cref="Tx"/> tell which. Thus a transaction that catches the
/// signal and returns is still retried or started again.
/// </summary>
internal sealed class StmSignal : Exception
{
    internal StmSignal() : base("a transaction stopped, to run again later") { }
}

/// <summary>
/// The log of one run of a transaction. A transaction gets it as an argument
/// and gives it to each operation, as kcas's <c>~xt</c>. A log is used for one
/// run only, and operations on it fail after the run.
/// </summary>
public sealed class Tx
{
    // With more Locs than this, a dictionary finds an entry.
    private const int IndexAt = 8;

    private Entry[] _entries = new Entry[4];
    private int _count;
    private Dictionary<LocBase, Entry>? _index;

    // The log is validated when this gets to a power of two. The start value
    // is the one kcas uses.
    private int _accesses = 4;

    // The writes that an or-else rolls back if its first branch retries.
    private int _orElseDepth;
    private List<(Entry Entry, object? Value, bool Written)>? _undo;

    private bool _closed;
    internal bool RetryRequested;
    internal bool RestartRequested;

    internal Tx() { }

    public T Get<T>(Loc<T> loc) => Open(loc).Value;

    public void Set<T>(Loc<T> loc, T value)
    {
        var e = Open(loc);
        if (_orElseDepth > 0) (_undo ??= new()).Add((e, e.SaveValue(), e.Written));
        e.Value = value;
        e.Written = true;
    }

    /// Stores <c>f</c> of the value, and returns the value stored.
    public T Update<T>(Loc<T> loc, Func<T, T> f)
    {
        var value = f(Open(loc).Value);
        Set(loc, value);
        return value;
    }

    /// Stores the value, and returns the value before.
    public T Swap<T>(Loc<T> loc, T value)
    {
        var e = Open(loc);
        var before = e.Value;
        Set(loc, value);
        return before;
    }

    /// Stop the transaction, and run it again when a Loc that it read has
    /// changed.
    public T Retry<T>()
    {
        CheckOpen();
        RetryRequested = true;
        throw new StmSignal();
    }

    /// Run <paramref name="first"/>. If it retries, undo its writes and run
    /// <paramref name="second"/>. The Locs that the first branch read stay in
    /// the log, so that a transaction where both branches retry waits for a
    /// change to any of them.
    public T OrElse<T>(Func<Tx, T> first, Func<Tx, T> second)
    {
        CheckOpen();
        int mark = _undo?.Count ?? 0;
        _orElseDepth++;
        T value;
        try
        {
            value = first(this);
        }
        catch (Exception) when (RetryRequested && !RestartRequested)
        {
            return Second(mark, second);
        }
        catch
        {
            _orElseDepth--;
            throw;
        }

        if (RetryRequested && !RestartRequested) return Second(mark, second);

        if (--_orElseDepth == 0) _undo?.Clear();
        return value;
    }

    private T Second<T>(int mark, Func<Tx, T> second)
    {
        var undo = _undo;
        if (undo is not null)
        {
            for (int i = undo.Count - 1; i >= mark; i--)
            {
                var (entry, value, written) = undo[i];
                entry.Restore(value, written);
            }
            undo.RemoveRange(mark, undo.Count - mark);
        }

        RetryRequested = false;
        if (--_orElseDepth == 0) undo?.Clear();
        return second(this);
    }

    private void CheckOpen()
    {
        if (_closed) throw new InvalidOperationException("this transaction has ended, and its log can not be used");
    }

    private Entry<T> Open<T>(Loc<T> loc)
    {
        CheckOpen();

        int c0 = _accesses, c1 = c0 + 1;
        _accesses = c1;
        if ((c0 & c1) == 0 && !Validate()) Restart();

        if (Find(loc) is { } found) return (Entry<T>)found;

        var read = Volatile.Read(ref loc.Current);
        var entry = new Entry<T>(loc, read, read.Version());
        Add(entry);
        return entry;
    }

    private Entry? Find(LocBase loc)
    {
        if (_index is not null) return _index.TryGetValue(loc, out var e) ? e : null;

        var entries = _entries;
        for (int i = 0; i < _count; i++)
            if (ReferenceEquals(entries[i].Loc, loc)) return entries[i];
        return null;
    }

    private void Add(Entry entry)
    {
        if (_count == _entries.Length) Array.Resize(ref _entries, _count * 2);
        _entries[_count++] = entry;

        if (_index is not null)
        {
            _index.Add(entry.Loc, entry);
        }
        else if (_count > IndexAt)
        {
            _index = new Dictionary<LocBase, Entry>(_count * 2);
            for (int i = 0; i < _count; i++) _index.Add(_entries[i].Loc, _entries[i]);
        }
    }

    private void Restart()
    {
        RestartRequested = true;
        throw new StmSignal();
    }

    internal void Close() => _closed = true;

    /// Each Loc in the log still holds the State that was read. If so, the
    /// reads are a snapshot of one moment: the moment of the first check.
    internal bool Validate()
    {
        var entries = _entries;
        for (int i = 0; i < _count; i++)
            if (!entries[i].ReadIsCurrent()) return false;
        return true;
    }

    internal void AddWaiters(StmBlock block)
    {
        var entries = _entries;
        for (int i = 0; i < _count; i++) entries[i].Loc.AddWaiter(block);
    }

    private static readonly Comparison<Entry> ById = static (a, b) => a.Loc.Id.CompareTo(b.Loc.Id);

    internal bool Commit()
    {
        int n = _count;
        if (n == 0) return true;

        bool writes = false;
        for (int i = 0; i < n; i++) writes |= _entries[i].Written;
        if (!writes) return Validate();
        if (n == 1) return _entries[0].CommitAlone();

        var ops = new Entry[n];
        Array.Copy(_entries, ops, n);
        Array.Sort(ops, ById);

        var casn = new Casn(ops);
        foreach (var op in ops)
            if (op.Written) op.Freeze(casn);
        return Mcas.Determine(casn);
    }
}

internal enum Ran { Value, Failed, Retry, Restart }

internal static class Stm
{
    /// Run the transaction once. On <see cref="Ran.Failed"/> the exception is
    /// in <paramref name="error"/>, and the snapshot was valid.
    internal static Ran RunBody<T>(Tx tx, Func<Tx, T> body, out T value, out ExceptionDispatchInfo? error)
    {
        error = null;
        try
        {
            value = body(tx);
        }
        catch (Exception e)
        {
            tx.Close();
            value = default!;
            if (tx.RestartRequested || !tx.Validate()) return Ran.Restart;
            if (tx.RetryRequested) return Ran.Retry;
            error = ExceptionDispatchInfo.Capture(e);
            return Ran.Failed;
        }

        tx.Close();
        if (tx.RestartRequested) return Ran.Restart;
        if (tx.RetryRequested) return tx.Validate() ? Ran.Retry : Ran.Restart;
        return Ran.Value;
    }

    /// Run and commit the transaction on this thread. False if it retries.
    /// An exception from the transaction is raised here.
    internal static bool TryNow<T>(Func<Tx, T> body, out T value)
    {
        var spin = new SpinWait();
        while (true)
        {
            var tx = new Tx();
            switch (RunBody(tx, body, out value, out var error))
            {
                case Ran.Value:
                    if (tx.Commit()) return true;
                    break;
                case Ran.Failed:
                    error!.Throw();
                    break;
                case Ran.Retry:
                    return false;
            }
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }
}

/// <summary>What a committed transaction gives its sync: a value or an exception.</summary>
internal readonly struct StmOutcome<T>
{
    private readonly T _value;
    private readonly ExceptionDispatchInfo? _error;

    internal StmOutcome(T value, ExceptionDispatchInfo? error)
    {
        _value = value;
        _error = error;
    }

    internal T Unwrap()
    {
        _error?.Throw();
        return _value;
    }
}

internal abstract class StmAttempt
{
    /// Another branch of the sync has won.
    internal abstract bool IsLost { get; }

    /// Run the transaction again, on the pool.
    internal abstract void Wake();
}

/// <summary>
/// One waiting run of a transaction, in the waiter list of each Loc it read.
/// It wakes its attempt once: the first write to any of the Locs fires it,
/// and the other lists drop it later.
/// </summary>
internal sealed class StmBlock
{
    private readonly StmAttempt _attempt;
    private int _fired;

    internal StmBlock(StmAttempt attempt) => _attempt = attempt;

    internal bool IsDead => Volatile.Read(ref _fired) != 0 || _attempt.IsLost;

    internal void Fire()
    {
        if (Interlocked.Exchange(ref _fired, 1) == 0 && !_attempt.IsLost) _attempt.Wake();
    }

    /// Take the block back before it fires. False if a write fired it first.
    internal bool TryTake() => Interlocked.Exchange(ref _fired, 1) == 0;
}

/// <summary>
/// The transaction of one published <see cref="AtomicallyEvent{T}"/>, for one
/// sync.
///
/// The commit of the transaction and the commit of the sync must be one step.
/// Thus the attempt claims the <see cref="SyncState"/> (W to C) before it
/// commits the transaction, and sets it to S if the commit succeeds or back to
/// W if it fails. While the claim is held, only the MCAS runs: no user code and
/// no locks that a claim holder can wait for. This is the rule that the
/// channels keep, and other threads can spin on the claim.
///
/// The first run is in <c>Publish</c>, on the syncing thread. When the
/// transaction retries, the attempt waits for a write to a Loc that it read.
/// The next run is then on a pool thread, while the fiber is still suspended
/// in its sync. That run installs the context of the fiber first, so that the
/// transaction sees the parameters and handlers of the fiber. The transaction
/// can not suspend, so it does not need the fiber itself.
///
/// The value or exception goes through a deferred wrap
/// (<see cref="WrapSink{T, U}"/>). Thus an exception from a run on the pool is
/// raised in the syncing fiber.
/// </summary>
internal sealed class StmAttempt<T> : StmAttempt
{
    private readonly Func<Tx, T> _body;
    private readonly SyncState _state;
    private readonly int _eventId;
    private readonly Action<StmOutcome<T>> _deliver;
    private readonly object? _context;
    private readonly Action _resume;

    internal StmAttempt(Func<Tx, T> body, SyncState state, int eventId,
                        Action<StmOutcome<T>> deliver, object? context)
    {
        _body = body;
        _state = state;
        _eventId = eventId;
        _deliver = deliver;
        _context = context;
        _resume = Resume;
    }

    internal override bool IsLost => _state.IsSynchronized;

    internal override void Wake() => Scheduler.Enqueue(_resume);

    private void Resume()
    {
        var saved = FiberContext.Current;
        FiberContext.Current = _context;
        try { Run(); }
        finally { FiberContext.Current = saved; }
    }

    /// Exceptions from the transaction go to the sync. Only a fault in this
    /// file can get out, and it goes to the scheduler's report: the caller
    /// is a <c>Publish</c> or a pool thread, and neither can take it.
    internal void Run()
    {
        try { RunUntilParked(); }
        catch (Exception e) { Scheduler.ReportUnhandled(e); }
    }

    private void RunUntilParked()
    {
        var spin = new SpinWait();
        while (!_state.IsSynchronized)
        {
            var tx = new Tx();
            switch (Stm.RunBody(tx, _body, out var value, out var error))
            {
                case Ran.Value:
                    if (!Claim()) return;
                    bool committed;
                    try { committed = tx.Commit(); }
                    catch { _state.ResetClaim(); throw; }

                    if (committed)
                    {
                        _state.MarkSynchronized(_eventId);
                        Scheduler.Dispatch(_deliver, new StmOutcome<T>(value, null));
                        return;
                    }
                    _state.ResetClaim();
                    break;

                case Ran.Failed:
                    if (_state.TryCommit(_eventId))
                        Scheduler.Dispatch(_deliver, new StmOutcome<T>(default!, error));
                    return;

                case Ran.Retry:
                    if (Park(tx)) return;
                    continue;
            }
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// W to C, and spin past a C that another thread holds for a moment.
    /// False if the sync is already won.
    private bool Claim()
    {
        var spin = new SpinWait();
        while (!_state.TryClaim())
        {
            if (_state.IsSynchronized) return false;
            spin.SpinOnce(sleep1Threshold: -1);
        }
        return true;
    }

    /// Wait for a write to a Loc in the log. False if the log is already not
    /// valid, so the transaction must run again now.
    ///
    /// The block is added to each Loc before the log is validated. A commit
    /// changes a Loc before it reads the waiters. Thus either the commit sees
    /// the block, or the validation sees the change.
    private bool Park(Tx tx)
    {
        var block = new StmBlock(this);
        tx.AddWaiters(block);
        Interlocked.MemoryBarrier();

        if (_state.IsSynchronized || tx.Validate()) return true;

        // A write that fired the block runs the transaction again. Only one
        // of the two may do that.
        return !block.TryTake();
    }
}

/// <summary>
/// <c>(atomically-evt f)</c>: the event that runs and commits the transaction
/// <c>f</c>. When <c>f</c> retries, the event is not available until a Loc that
/// <c>f</c> read changes. Thus <c>retry</c> composes with other events: a
/// <c>choose</c> with a timeout or a channel, and the cancellation of the
/// fiber, which is a branch of each sync.
///
/// The transaction runs each time the event is synced. If another branch wins,
/// the transaction has written nothing.
/// </summary>
public sealed class AtomicallyEvent<T> : IEvent<T>, INowable<T>
{
    private static readonly Func<StmOutcome<T>, T> s_unwrap = static o => o.Unwrap();

    private readonly Func<Tx, T> _body;

    public AtomicallyEvent(Func<Tx, T> body) => _body = body;

    public void Publish(SyncState sharedState, int eventId, Action<T> onSync)
    {
        var sink = new WrapSink<StmOutcome<T>, T>(sharedState, s_unwrap, onSync);
        new StmAttempt<T>(_body, sharedState, eventId, sink.Commit, FiberContext.Current).Run();
    }

    /// The fast path of a sync of this event alone: run and commit on the
    /// syncing fiber, with no <see cref="SyncState"/>. If the transaction
    /// retries, the sync publishes the event, which runs it again.
    bool INowable<T>.TryNow(out T value) => Stm.TryNow(_body, out value);
}

/// <summary>The functions of (std stm).</summary>
public static class StmModule
{
    public static Loc<T> MakeLoc<T>(T value) => new(value);

    /// The value of the Loc now, outside a transaction.
    public static T Ref<T>(Loc<T> loc) => loc.Read();

    public static T Get<T>(Tx tx, Loc<T> loc) => tx.Get(loc);

    public static void Set<T>(Tx tx, Loc<T> loc, T value) => tx.Set(loc, value);

    public static T Update<T>(Tx tx, Loc<T> loc, Func<T, T> f) => tx.Update(loc, f);

    public static T Swap<T>(Tx tx, Loc<T> loc, T value) => tx.Swap(loc, value);

    public static T Retry<T>(Tx tx) => tx.Retry<T>();

    public static T OrElse<T>(Tx tx, Func<Tx, T> first, Func<Tx, T> second) => tx.OrElse(first, second);

    public static IEvent<T> AtomicallyEvt<T>(Func<Tx, T> body) => new AtomicallyEvent<T>(body);

    /// Run and commit the transaction on this thread, with no wait. None if it
    /// retries.
    public static global::BjolangRuntime.Option<T> TryAtomically<T>(Func<Tx, T> body) =>
        Stm.TryNow(body, out var value)
            ? global::BjolangRuntime.Some(value)
            : global::BjolangRuntime.None<T>();
}
