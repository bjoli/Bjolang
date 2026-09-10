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
using System.Threading.Tasks;
using Bjoml;

public static class MyServer
{
    // The original method is rewritten to initialize and start the state machine
    public static Task Server(Channel<object> @in, Channel<object> @out)
    {
        ServerStateMachine stateMachine = new ServerStateMachine();
        stateMachine._builder = AsyncTaskMethodBuilder.Create();
        var task = stateMachine._builder.Task;
        stateMachine.@in = @in;
        stateMachine.@out = @out;
        stateMachine._state = -1;
        stateMachine._builder.Start(ref stateMachine);
        return task;
    }

    // The compiler-generated state machine struct
    [CompilerGenerated]
    private struct ServerStateMachine : IAsyncStateMachine
    {
        public int _state;
        public AsyncTaskMethodBuilder _builder;
        public Channel<object> @in;
        public Channel<object> @out;
        
        // Local variables hoisted to struct fields
        private object _msg; 
        
        // Awaiter fields. These are the awaiters a direct `await ch.Receive()` /
        // `await ch.Send(v)` resolves to: the channel's own, which park an
        // operation without going through `Cml.Sync` at all. This is the path the
        // compiled language takes.
        private ChannelReceiveAwaiter<object> _u1;
        private ChannelSendAwaiter<object> _u2;

        public void MoveNext()
        {
            int num = _state;
            try
            {
                ChannelReceiveAwaiter<object> getAwaiter;
                ChannelSendAwaiter<object> putAwaiter;
                
                while (true) // The original while(true) loop
                {
                    switch (num)
                    {
                        case 0: // Resuming from Receive
                            getAwaiter = _u1;
                            _u1 = default;
                            num = (_state = -1);
                            goto Label_Receive_Completed;

                        case 1: // Resuming from Send (ping)
                        case 2: // Resuming from Send (sup)
                        case 3: // Resuming from Send (wat)
                            putAwaiter = _u2;
                            _u2 = default;
                            num = (_state = -1);
                            goto Label_Send_Completed;
                    }

                    // --- INITIAL EXECUTION / LOOP RESTART ---
                    getAwaiter = @in.Receive().GetAwaiter();
                    if (!getAwaiter.IsCompleted)
                    {
                        num = (_state = 0);
                        _u1 = getAwaiter;
                        ServerStateMachine stateMachine = this;
                        _builder.AwaitUnsafeOnCompleted(ref getAwaiter, ref stateMachine);
                        return; // Yield thread
                    }

                Label_Receive_Completed:
                    _msg = getAwaiter.GetResult();
                    Console.WriteLine($"server-received: {_msg}");

                    // --- BRANCHING LOGIC ---
                    if (_msg is "ping!")
                    {
                        putAwaiter = @out.Send("pong!").GetAwaiter();
                        if (!putAwaiter.IsCompleted)
                        {
                            num = (_state = 1);
                            _u2 = putAwaiter;
                            ServerStateMachine stateMachine = this;
                            _builder.AwaitUnsafeOnCompleted(ref putAwaiter, ref stateMachine);
                            return; // Yield thread
                        }
                    }
                    else if (_msg is "sup")
                    {
                        putAwaiter = @out.Send("not-much-u").GetAwaiter();
                        if (!putAwaiter.IsCompleted)
                        {
                            num = (_state = 2);
                            _u2 = putAwaiter;
                            ServerStateMachine stateMachine = this;
                            _builder.AwaitUnsafeOnCompleted(ref putAwaiter, ref stateMachine);
                            return; // Yield thread
                        }
                    }
                    else
                    {
                        putAwaiter = @out.Send($"wat {_msg}").GetAwaiter();
                        if (!putAwaiter.IsCompleted)
                        {
                            num = (_state = 3);
                            _u2 = putAwaiter;
                            ServerStateMachine stateMachine = this;
                            _builder.AwaitUnsafeOnCompleted(ref putAwaiter, ref stateMachine);
                            return; // Yield thread
                        }
                    }

                Label_Send_Completed:
                    putAwaiter.GetResult(); // Throw if faulted
                    
                    // The switch ends, the while(true) loop restarts, pulling the next message.
                }
            }
            catch (Exception exception)
            {
                _state = -2;
                _builder.SetException(exception);
            }
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
            _builder.SetStateMachine(stateMachine);
        }
    }
}