using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SocketAsyncServer
{
    public sealed class SocketListener
    {
        private const int MessageHeaderSize = 4;
        private int _receivedMessageCount = 0;  //for testing
        private Stopwatch _watch;  //for testing

        private BlockingCollection<MessageData> sendingQueue;
        private Thread sendMessageWorker;

        private static Mutex _mutex = new Mutex();
        private Socket _listenSocket;
        private int _bufferSize;
        private int _connectedSocketCount;
        private int _maxConnectionCount;
        private SocketAsyncEventArgsPool _socketAsyncEventArgsPool;
        private Semaphore _acceptedClientsSemaphore;

        public SocketListener(int maxConnectionCount, int bufferSize)
        {
            _maxConnectionCount = maxConnectionCount;
            _bufferSize = bufferSize;
            _socketAsyncEventArgsPool = new SocketAsyncEventArgsPool(maxConnectionCount);
            _acceptedClientsSemaphore = new Semaphore(maxConnectionCount, maxConnectionCount);

            sendingQueue = new BlockingCollection<MessageData>();
            sendMessageWorker = new Thread(new ThreadStart(SendQueueMessage));

            for (int i = 0; i < maxConnectionCount; i++)
            {
                SocketAsyncEventArgs socketAsyncEventArgs = new SocketAsyncEventArgs();
                socketAsyncEventArgs.Completed += OnIOCompleted;
                socketAsyncEventArgs.SetBuffer(new Byte[bufferSize], 0, bufferSize);
                _socketAsyncEventArgsPool.Push(socketAsyncEventArgs);
            }
        }

        public void Start(IPEndPoint localEndPoint)
        {
            _listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.ReceiveBufferSize = _bufferSize;
            _listenSocket.SendBufferSize = _bufferSize;
            _listenSocket.Bind(localEndPoint);
            _listenSocket.Listen(_maxConnectionCount);
            sendMessageWorker.Start();
            StartAccept(null);
            _mutex.WaitOne();
        }
        public void Stop()
        {
            try
            {
                _listenSocket.Close();
            }
            catch { }
            _mutex.ReleaseMutex();
        }

        private void OnIOCompleted(object sender, SocketAsyncEventArgs e)
        {
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    ProcessSend(e);
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not a receive or send");
            }
        }

        private void SendQueueMessage()
        {
            while (true)
            {
                var messageData = sendingQueue.Take();
                if (messageData != null)
                {
                    var message = BuildMessage(messageData.Message);
                    messageData.Token.SendEventArgs.SetBuffer(message, 0, message.Length);
                    messageData.Token.SendEventArgs.UserToken = messageData.Token;
                    messageData.Token.Socket.SendAsync(messageData.Token.SendEventArgs);
                    messageData.Token.AutoSendEvent.WaitOne();
                }
            }
        }
        static byte[] BuildMessage(byte[] data)
        {
            var header = BitConverter.GetBytes(data.Length);
            var message = new byte[header.Length + data.Length];
            header.CopyTo(message, 0);
            data.CopyTo(message, header.Length);
            return message;
        }

        private void StartAccept(SocketAsyncEventArgs acceptEventArg)
        {
            if (acceptEventArg == null)
            {
                acceptEventArg = new SocketAsyncEventArgs();
                acceptEventArg.Completed += (sender, e) => ProcessAccept(e);
            }
            else
            {
                acceptEventArg.AcceptSocket = null;
            }

            _acceptedClientsSemaphore.WaitOne();
            if (!_listenSocket.AcceptAsync(acceptEventArg))
            {
                ProcessAccept(acceptEventArg);
            }
        }
        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            try
            {
                SocketAsyncEventArgs readEventArgs = _socketAsyncEventArgsPool.Pop();
                SocketAsyncEventArgs sendEventArgs = _socketAsyncEventArgsPool.Pop();
                if (readEventArgs != null && sendEventArgs != null)
                {
                    readEventArgs.UserToken = new AsyncUserToken(e.AcceptSocket, sendEventArgs, new AutoResetEvent(false));
                    Interlocked.Increment(ref _connectedSocketCount);
                    Console.WriteLine("Client connection accepted. There are {0} clients connected to the server", _connectedSocketCount);
                    if (!e.AcceptSocket.ReceiveAsync(readEventArgs))
                    {
                        ProcessReceive(readEventArgs);
                    }
                }
                else
                {
                    Console.WriteLine("There are no more available sockets to allocate.");
                }
            }
            catch (SocketException ex)
            {
                AsyncUserToken token = e.UserToken as AsyncUserToken;
                Console.WriteLine("Error when processing data received from {0}:\r\n{1}", token.Socket.RemoteEndPoint, ex.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            // Accept the next connection request.
            StartAccept(e);
        }
        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
            {
                AsyncUserToken token = e.UserToken as AsyncUserToken;

                //处理接收到的数据
                ProcessReceivedData(token.DataStartOffset, token.NextReceiveOffset - token.DataStartOffset + e.BytesTransferred, 0, token, e);

                //更新下一个要接收数据的起始位置
                token.NextReceiveOffset += e.BytesTransferred;

                //如果达到缓冲区的结尾，则将NextReceiveOffset复位到缓冲区起始位置，并迁移可能需要迁移的未处理的数据
                if (token.NextReceiveOffset == e.Buffer.Length)
                {
                    //将NextReceiveOffset复位到缓冲区起始位置
                    token.NextReceiveOffset = 0;

                    //如果还有未处理的数据，则把这些数据迁移到数据缓冲区的起始位置
                    if (token.DataStartOffset < e.Buffer.Length)
                    {
                        var notYesProcessDataSize = e.Buffer.Length - token.DataStartOffset;
                        Buffer.BlockCopy(e.Buffer, token.DataStartOffset, e.Buffer, 0, notYesProcessDataSize);

                        //数据迁移到缓冲区起始位置后，需要再次更新NextReceiveOffset
                        token.NextReceiveOffset = notYesProcessDataSize;
                    }

                    token.DataStartOffset = 0;
                }

                //更新接收数据的缓冲区下次接收数据的起始位置和最大可接收数据的长度
                e.SetBuffer(token.NextReceiveOffset, e.Buffer.Length - token.NextReceiveOffset);

                //接收后续的数据
                if (!token.Socket.ReceiveAsync(e))
                {
                    ProcessReceive(e);
                }
            }
            else
            {
                CloseClientSocket(e);
            }
        }
        private void ProcessReceivedData(int dataStartOffset, int totalReceivedDataSize, int alreadyProcessedDataSize, AsyncUserToken token, SocketAsyncEventArgs e)
        {
            if (alreadyProcessedDataSize >= totalReceivedDataSize)
            {
                return;
            }

            if (token.MessageSize == null)
            {
                //如果之前接收到到数据加上当前接收到的数据大于消息头的大小，则可以解析消息头
                if (totalReceivedDataSize > MessageHeaderSize)
                {
                    //解析消息长度
                    var headerData = new byte[MessageHeaderSize];
                    Buffer.BlockCopy(e.Buffer, dataStartOffset, headerData, 0, MessageHeaderSize);
                    var messageSize = BitConverter.ToInt32(headerData, 0);

                    token.MessageSize = messageSize;
                    token.DataStartOffset = dataStartOffset + MessageHeaderSize;

                    //递归处理
                    ProcessReceivedData(token.DataStartOffset, totalReceivedDataSize, alreadyProcessedDataSize + MessageHeaderSize, token, e);
                }
                //如果之前接收到到数据加上当前接收到的数据仍然没有大于消息头的大小，则需要继续接收后续的字节
                else
                {
                    //这里不需要做什么事情
                }
            }
            else
            {
                var messageSize = token.MessageSize.Value;
                //判断当前累计接收到的字节数减去已经处理的字节数是否大于消息的长度，如果大于，则说明可以解析消息了
                if (totalReceivedDataSize - alreadyProcessedDataSize >= messageSize)
                {
                    var messageData = new byte[messageSize];
                    Buffer.BlockCopy(e.Buffer, dataStartOffset, messageData, 0, messageSize);
                    ProcessMessage(messageData, token, e);

                    //消息处理完后，需要清理token，以便接收下一个消息
                    token.DataStartOffset = dataStartOffset + messageSize;
                    token.MessageSize = null;

                    //递归处理
                    ProcessReceivedData(token.DataStartOffset, totalReceivedDataSize, alreadyProcessedDataSize + messageSize, token, e);
                }
                //说明剩下的字节数还不够转化为消息，则需要继续接收后续的字节
                else
                {
                    //这里不需要做什么事情
                }
            }
        }
        private void ProcessMessage(byte[] messageData, AsyncUserToken token, SocketAsyncEventArgs e)
        {
            var current = Interlocked.Increment(ref _receivedMessageCount);
            if (current == 1)
            {
                _watch = Stopwatch.StartNew();
            }
            if (current % 1000 == 0)
            {
                Console.WriteLine("received message, length:{0}, count:{1}, timeSpent:{2}", messageData.Length, current, _watch.ElapsedMilliseconds);
            }
            sendingQueue.Add(new MessageData { Message = messageData, Token = token });
        }
        private void ProcessSend(SocketAsyncEventArgs e)
        {
            (e.UserToken as AsyncUserToken).AutoSendEvent.Set();
        }
        private void CloseClientSocket(SocketAsyncEventArgs e)
        {
            var token = e.UserToken as AsyncUserToken;
            token.Dispose();
            _acceptedClientsSemaphore.Release();
            Interlocked.Decrement(ref _connectedSocketCount);
            Console.WriteLine("A client has been disconnected from the server. There are {0} clients connected to the server", _connectedSocketCount);
            _socketAsyncEventArgsPool.Push(e);
        }
    }
}
