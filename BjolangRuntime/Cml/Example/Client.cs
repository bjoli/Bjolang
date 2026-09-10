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

public static class MyClient
{
    public static Task Client(Channel<object> @in, Channel<object> @out)
    {
        ClientStateMachine stateMachine = new ClientStateMachine();
        stateMachine._builder = AsyncTaskMethodBuilder.Create();
        var task = stateMachine._builder.Task;
        stateMachine.@in = @in;
        stateMachine.@out = @out;
        stateMachine._state = -1;
        stateMachine._builder.Start(ref stateMachine);
        return task;
    }

    [CompilerGenerated]
    private struct ClientStateMachine : IAsyncStateMachine
    {
        public int _state;
        public AsyncTaskMethodBuilder _builder;
        public Channel<object> @in;
        public Channel<object> @out;

        private object[] _messages;
        private int _index;

        // The awaiters a direct `await ch.Send(v)` / `await ch.Receive()` resolves
        // to: the channel's own, which park an operation without going through
        // `Cml.Sync` at all. This is the path the compiled language takes.
        private ChannelSendAwaiter<object> _u1;
        private ChannelReceiveAwaiter<object> _u2;

        public void MoveNext()
        {
            int num = _state;
            try
            {
                ChannelSendAwaiter<object> putAwaiter;
                ChannelReceiveAwaiter<object> getAwaiter;

                if (num == 0)
                {
                    putAwaiter = _u1;
                    _u1 = default;
                    num = (_state = -1);
                    goto Label_Send_Completed;
                }
                if (num == 1)
                {
                    getAwaiter = _u2;
                    _u2 = default;
                    num = (_state = -1);
                    goto Label_Receive_Completed;
                }

                // Initial setup
                _messages = new object[] { "ping!", "sup" };
                _index = 0;

            Label_Loop_Start:
                if (_index >= _messages.Length) goto Label_Loop_End;

                var msg = _messages[_index];
                putAwaiter = @out.Send(msg).GetAwaiter();
                if (!putAwaiter.IsCompleted)
                {
                    num = (_state = 0);
                    _u1 = putAwaiter;
                    ClientStateMachine stateMachine = this;
                    _builder.AwaitUnsafeOnCompleted(ref putAwaiter, ref stateMachine);
                    return; // Yield thread
                }

            Label_Send_Completed:
                putAwaiter.GetResult();

                getAwaiter = @in.Receive().GetAwaiter();
                if (!getAwaiter.IsCompleted)
                {
                    num = (_state = 1);
                    _u2 = getAwaiter;
                    ClientStateMachine stateMachine = this;
                    _builder.AwaitUnsafeOnCompleted(ref getAwaiter, ref stateMachine);
                    return; // Yield thread
                }

            Label_Receive_Completed:
                var response = getAwaiter.GetResult();
                Console.WriteLine($"client-received: {response}");

                _index++;
                goto Label_Loop_Start;
            
            Label_Loop_End:;
            }
            catch (Exception exception)
            {
                _state = -2;
                _builder.SetException(exception);
                return;
            }

            _state = -2;
            Console.WriteLine("Client calling SetResult");
            _builder.SetResult();
            Console.WriteLine("Client SetResult returned");
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
            _builder.SetStateMachine(stateMachine);
        }
    }
}
