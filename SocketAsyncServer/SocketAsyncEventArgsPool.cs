using System;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace SocketAsyncServer
{
    public sealed class SocketAsyncEventArgsPool
    {
        ConcurrentQueue<SocketAsyncEventArgs> queue;

        public SocketAsyncEventArgsPool(Int32 capacity)
        {
            this.queue = new ConcurrentQueue<SocketAsyncEventArgs>();
        }

        public SocketAsyncEventArgs Pop()
        {
            SocketAsyncEventArgs args;
            if (this.queue.TryDequeue(out args))
            {
                return args;
            }
            return null;
        }
        public void Push(SocketAsyncEventArgs item)
        {
            if (item == null) 
            { 
                throw new ArgumentNullException("Items added to a SocketAsyncEventArgsPool cannot be null"); 
            }
            this.queue.Enqueue(item);
        }
    }
}
