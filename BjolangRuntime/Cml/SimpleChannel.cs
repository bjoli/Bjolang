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

using System;
using System.Runtime.CompilerServices;

namespace Bjoml;

/// <summary>
/// A high-performance, point-to-point unbuffered rendezvous channel.
///
/// Unlike <see cref="Channel{T}"/>, this is NOT a composable CML event (it cannot be
/// passed to <c>choose</c> or <c>withNack</c>). Instead, it provides raw, direct
/// fiber-to-fiber (and task-to-task) rendezvous with zero GC allocations and direct
/// inline continuation resumption.
/// </summary>
public sealed class SimpleChannel<T>
{
    private readonly object _lock = new();
    private SimpleNode<T>? _putsHead, _putsTail;
    private SimpleNode<T>? _getsHead, _getsTail;

    public SimpleAwaitable<T> PutMessage(T value)
    {
        Action? toResume = null;
        SimpleNode<T>? node = null;

        lock (_lock)
        {
            if (_getsHead is not null)
            {
                var getter = _getsHead;
                _getsHead = getter.Next;
                if (_getsHead is null) _getsTail = null;

                getter.Value = value;
                getter.IsCompleted = true;
                toResume = getter.Continuation;
            }
            else
            {
                node = SimpleNode<T>.Rent();
                node.Value = value;
                node.Channel = this;
                node.IsPut = true;
                if (_putsTail is null) _putsHead = _putsTail = node;
                else { _putsTail.Next = node; _putsTail = node; }
            }
        }

        if (toResume is not null) Scheduler.Dispatch(toResume);
        return node is null ? SimpleAwaitable<T>.Completed(default) : SimpleAwaitable<T>.Pending(node);
    }

    public SimpleAwaitable<T> GetMessage()
    {
        Action? toResume = null;
        SimpleNode<T>? node = null;
        T? syncVal = default;
        bool isSync = false;

        lock (_lock)
        {
            if (_putsHead is not null)
            {
                var putter = _putsHead;
                _putsHead = putter.Next;
                if (_putsHead is null) _putsTail = null;

                syncVal = putter.Value!;
                putter.IsCompleted = true;
                toResume = putter.Continuation;
                isSync = true;
            }
            else
            {
                node = SimpleNode<T>.Rent();
                node.Channel = this;
                node.IsPut = false;
                if (_getsTail is null) _getsHead = _getsTail = node;
                else { _getsTail.Next = node; _getsTail = node; }
            }
        }

        if (toResume is not null) Scheduler.Dispatch(toResume);
        return isSync ? SimpleAwaitable<T>.Completed(syncVal) : SimpleAwaitable<T>.Pending(node!);
    }

    public sealed class SimpleNode<TValue>
    {
        private const int MaxCached = 128;
        [ThreadStatic] private static SimpleNode<TValue>? _free;
        [ThreadStatic] private static int _freeCount;

        public SimpleNode<TValue>? Next;
        public SimpleChannel<TValue>? Channel;
        public TValue? Value;
        public Action? Continuation;
        public bool IsCompleted;
        public bool IsPut;

        public static SimpleNode<TValue> Rent()
        {
            var item = _free;
            if (item is null) return new SimpleNode<TValue>();
            _free = item.Next;
            _freeCount--;
            item.Next = null;
            item.IsCompleted = false;
            return item;
        }

        public void Return()
        {
            Next = null;
            Channel = null;
            Value = default;
            Continuation = null;
            IsCompleted = false;
            IsPut = false;

            if (_freeCount < MaxCached)
            {
                Next = _free;
                _free = this;
                _freeCount++;
            }
        }
    }

    public readonly struct SimpleAwaitable<TValue> : ICriticalNotifyCompletion
    {
        private readonly SimpleNode<TValue>? _node;
        private readonly TValue? _syncValue;
        private readonly bool _isCompleted;

        private SimpleAwaitable(TValue? syncValue, SimpleNode<TValue>? node, bool isCompleted)
        {
            _syncValue = syncValue;
            _node = node;
            _isCompleted = isCompleted;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SimpleAwaitable<TValue> Completed(TValue? value) => new(value, null, true);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SimpleAwaitable<TValue> Pending(SimpleNode<TValue> node) => new(default, node, false);

        public SimpleAwaitable<TValue> GetAwaiter() => this;
        public bool IsCompleted => _isCompleted;

        public TValue GetResult()
        {
            if (_isCompleted) return _syncValue!;
            var node = _node!;
            var val = node.Value!;
            node.Return();
            return val;
        }

        public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation)
        {
            if (_isCompleted)
            {
                Scheduler.Dispatch(continuation);
                return;
            }

            var node = _node!;
            var ch = node.Channel;
            if (ch is null)
            {
                Scheduler.Dispatch(continuation);
                return;
            }

            bool dispatchNow = false;
            lock (ch._lock)
            {
                if (node.IsCompleted)
                {
                    dispatchNow = true;
                }
                else
                {
                    node.Continuation = continuation;
                }
            }

            if (dispatchNow)
            {
                Scheduler.Dispatch(continuation);
            }
        }
    }
}
