using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SocketAsyncClient
{
    public sealed class SocketClient : IDisposable
    {
        private int bufferSize = 60000;
        private const int MessageHeaderSize = 4;

        private Socket clientSocket;
        private bool connected = false;
        private IPEndPoint hostEndPoint;
        private AutoResetEvent autoConnectEvent;
        private AutoResetEvent autoSendEvent;
        private SocketAsyncEventArgs sendEventArgs;
        private SocketAsyncEventArgs receiveEventArgs;
        private BlockingCollection<byte[]> sendingQueue;
        private BlockingCollection<byte[]> receivedMessageQueue;
        private Thread sendMessageWorker;
        private Thread processReceivedMessageWorker;

        public SocketClient(IPEndPoint hostEndPoint)
        {
            this.hostEndPoint = hostEndPoint;
            this.autoConnectEvent = new AutoResetEvent(false);
            this.autoSendEvent = new AutoResetEvent(false);
            this.sendingQueue = new BlockingCollection<byte[]>();
            this.receivedMessageQueue = new BlockingCollection<byte[]>();
            this.clientSocket = new Socket(this.hostEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            this.sendMessageWorker = new Thread(new ThreadStart(SendQueueMessage));
            this.processReceivedMessageWorker = new Thread(new ThreadStart(ProcessReceivedMessage));

            this.sendEventArgs = new SocketAsyncEventArgs();
            this.sendEventArgs.UserToken = this.clientSocket;
            this.sendEventArgs.RemoteEndPoint = this.hostEndPoint;
            this.sendEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnSend);

            this.receiveEventArgs = new SocketAsyncEventArgs();
            this.receiveEventArgs.UserToken = new AsyncUserToken(clientSocket);
            this.receiveEventArgs.RemoteEndPoint = this.hostEndPoint;
            this.receiveEventArgs.SetBuffer(new Byte[bufferSize], 0, bufferSize);
            this.receiveEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnReceive);
        }

        public void Connect()
        {
            SocketAsyncEventArgs connectArgs = new SocketAsyncEventArgs();
            connectArgs.UserToken = this.clientSocket;
            connectArgs.RemoteEndPoint = this.hostEndPoint;
            connectArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnConnect);

            clientSocket.ConnectAsync(connectArgs);
            autoConnectEvent.WaitOne();

            SocketError errorCode = connectArgs.SocketError;
            if (errorCode != SocketError.Success)
            {
                throw new SocketException((Int32)errorCode);
            }
            sendMessageWorker.Start();
            processReceivedMessageWorker.Start();

            if (!clientSocket.ReceiveAsync(receiveEventArgs))
            {
                ProcessReceive(receiveEventArgs);
            }
        }

        public void Disconnect()
        {
            clientSocket.Disconnect(false);
        }
        public void Send(byte[] message)
        {
            sendingQueue.Add(message);
        }

        private void OnConnect(object sender, SocketAsyncEventArgs e)
        {
            autoConnectEvent.Set();
            connected = (e.SocketError == SocketError.Success);
        }
        private void OnSend(object sender, SocketAsyncEventArgs e)
        {
            autoSendEvent.Set();
        }
        private void SendQueueMessage()
        {
            while (true)
            {
                var message = sendingQueue.Take();
                if (message != null)
                {
                    sendEventArgs.SetBuffer(message, 0, message.Length);
                    clientSocket.SendAsync(sendEventArgs);
                    autoSendEvent.WaitOne();
                }
            }
        }

        private void OnReceive(object sender, SocketAsyncEventArgs e)
        {
            ProcessReceive(e);
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
                ProcessError(e);
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
                    ProcessMessage(messageData);

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
        private void ProcessMessage(byte[] messageData)
        {
            receivedMessageQueue.Add(messageData);
        }
        private void ProcessReceivedMessage()
        {
            while (true)
            {
                var message = receivedMessageQueue.Take();
                if (message != null)
                {
                    var current = Interlocked.Increment(ref Program._receivedMessageCount);
                    if (current == 1)
                    {
                        Program._watch = Stopwatch.StartNew();
                    }
                    if (current % 1000 == 0)
                    {
                        Console.WriteLine("received reply message, length:{0}, count:{1}, timeSpent:{2}", message.Length, current, Program._watch.ElapsedMilliseconds);
                    }
                }
            }
        }

        private void ProcessError(SocketAsyncEventArgs e)
        {
            //Socket s = e.UserToken as Socket;
            //if (s.Connected)
            //{
            //    // close the socket associated with the client
            //    try
            //    {
            //        s.Shutdown(SocketShutdown.Both);
            //    }
            //    catch (Exception)
            //    {
            //        // throws if client process has already closed
            //    }
            //    finally
            //    {
            //        if (s.Connected)
            //        {
            //            s.Close();
            //        }
            //    }
            //}

            // Throw the SocketException
            throw new SocketException((Int32)e.SocketError);
        }

        #region IDisposable Members

        public void Dispose()
        {
            autoConnectEvent.Close();
            if (this.clientSocket.Connected)
            {
                this.clientSocket.Close();
            }
        }

        #endregion
    }
}
