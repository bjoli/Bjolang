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

// Messages between a program and the host that runs it, such as the Android
// app that `bjo publish --android` makes. Neither references the other: both
// reference the runtime.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bjolang.Runtime;

/// <summary>
/// Strings between a program and its host, by kind, such as "share-text".
///
/// - Host to program: the host calls <see cref="Post"/>, and the program
///   takes each message with <see cref="NextAsync"/>. A message waits in a
///   queue until it is taken, so a message posted before the program starts
///   is not lost. A kind keeps at most <see cref="MaxQueued"/> messages: a
///   further one drops the oldest.
/// - Program to host: the host offers a service with <see cref="SetHandler"/>,
///   and the program calls it with <see cref="Request"/>.
/// - Thread safety: every member may be called on any thread.
/// </summary>
[NonUserCode]
public static class Host {
    /// The most messages that wait for one kind.
    public const int MaxQueued = 64;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Queue<string>> Queued = new();
    private static readonly Dictionary<string, List<TaskCompletionSource<string>>> Waiting = new();
    private static readonly Dictionary<string, Func<string, bool>> Handlers = new();

    /// <summary>
    /// Gives `text` to the program as a message of `kind`: to the first
    /// waiter of <see cref="NextAsync"/>, or to the queue.
    /// </summary>
    public static void Post(string kind, string text) {
        TaskCompletionSource<string>? waiter = null;
        lock (Gate) {
            if (Waiting.TryGetValue(kind, out var list)) {
                while (list.Count > 0 && waiter is null) {
                    var w = list[0];
                    list.RemoveAt(0);
                    if (!w.Task.IsCompleted) waiter = w;
                }
            }
            if (waiter is null) {
                if (!Queued.TryGetValue(kind, out var q)) Queued[kind] = q = new Queue<string>();
                if (q.Count == MaxQueued) q.Dequeue();
                q.Enqueue(text);
                return;
            }
        }
        // Outside the lock: the waiter's continuation may run here.
        if (!waiter.TrySetResult(text)) Post(kind, text);
    }

    /// <summary>
    /// The next message of `kind`: a queued one at once, else the next that
    /// is posted.
    /// - Cancelling `cancel` cancels the Task, and the waiter takes no
    ///   message.
    /// </summary>
    public static Task<string> NextAsync(string kind, CancellationToken cancel) {
        TaskCompletionSource<string> waiter;
        lock (Gate) {
            if (Queued.TryGetValue(kind, out var q) && q.Count > 0)
                return Task.FromResult(q.Dequeue());
            waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Waiting.TryGetValue(kind, out var list)) Waiting[kind] = list = new();
            list.Add(waiter);
        }
        if (cancel.CanBeCanceled) {
            var registration = cancel.Register([NonUserCode] () => {
                lock (Gate) {
                    if (Waiting.TryGetValue(kind, out var list)) list.Remove(waiter);
                }
                waiter.TrySetCanceled(cancel);
            });
            _ = waiter.Task.ContinueWith([NonUserCode] (_) => registration.Dispose(), TaskScheduler.Default);
        }
        return waiter.Task;
    }

    /// <summary>
    /// Offers a service of the host for requests of `kind`. The handler
    /// returns whether it did the work. Null removes the service.
    /// </summary>
    public static void SetHandler(string kind, Func<string, bool>? handler) {
        lock (Gate) {
            if (handler is null) Handlers.Remove(kind);
            else Handlers[kind] = handler;
        }
    }

    /// <summary>
    /// Asks the host's service for `kind` to act on `text`. Returns what the
    /// handler returns, and false when the host offers no such service.
    /// The handler runs on the calling thread.
    /// </summary>
    public static bool Request(string kind, string text) {
        Func<string, bool>? handler;
        lock (Gate) Handlers.TryGetValue(kind, out handler);
        return handler is not null && handler(text);
    }
}
