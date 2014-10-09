using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SocketAsyncClient
{
    internal sealed class SocketClient : IDisposable
    {
        /// <summary>
        /// Constants for socket operations.
        /// </summary>
        private const Int32 ReceiveOperation = 1, SendOperation = 0;

        /// <summary>
        /// The socket used to send/receive messages.
        /// </summary>
        private Socket clientSocket;

        /// <summary>
        /// Flag for connected socket.
        /// </summary>
        private Boolean connected = false;

        /// <summary>
        /// Listener endpoint.
        /// </summary>
        private IPEndPoint hostEndPoint;

        /// <summary>
        /// Signals a connection.
        /// </summary>
        private AutoResetEvent autoConnectEvent = new AutoResetEvent(false);

        private AutoResetEvent autoSendEvent = new AutoResetEvent(false); 
        /// <summary>
        /// Signals the send/receive operation.
        /// </summary>
        private static AutoResetEvent[] autoSendReceiveEvents = new AutoResetEvent[]
        {
            new AutoResetEvent(false),
            new AutoResetEvent(false)
        };

        private SocketAsyncEventArgs sendEventArgs;

        private BlockingCollection<byte[]> sendingQueue = new BlockingCollection<byte[]>();
        private Thread sendMessageWorker;

        internal SocketClient(IPEndPoint hostEndPoint)
        {
            this.hostEndPoint = hostEndPoint;
            this.clientSocket = new Socket(this.hostEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            this.sendMessageWorker = new Thread(new ThreadStart(SendQueueMessage));
        }

        internal void Connect()
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
        }
        internal void Disconnect()
        {
            clientSocket.Disconnect(false);
        }
        internal void Send(byte[] message)
        {
            sendingQueue.Add(message);
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
        private void OnConnect(object sender, SocketAsyncEventArgs e)
        {
            // Signals the end of connection.
            autoConnectEvent.Set();

            // Set the flag for socket connected.
            this.connected = (e.SocketError == SocketError.Success);

            sendEventArgs = new SocketAsyncEventArgs();
            sendEventArgs.UserToken = this.clientSocket;
            sendEventArgs.RemoteEndPoint = this.hostEndPoint;
            sendEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnSend);
        }
        private void OnSend(object sender, SocketAsyncEventArgs e)
        {
            autoSendEvent.Set();
            //// Signals the end of send.
            //autoSendReceiveEvents[ReceiveOperation].Set();

            //if (e.SocketError == SocketError.Success)
            //{
            //    if (e.LastOperation == SocketAsyncOperation.Send)
            //    {
            //        // Prepare receiving.
            //        Socket s = e.UserToken as Socket;

            //        byte[] receiveBuffer = new byte[255];
            //        e.SetBuffer(receiveBuffer, 0, receiveBuffer.Length);
            //        e.Completed += new EventHandler<SocketAsyncEventArgs>(OnReceive);
            //        s.ReceiveAsync(e);
            //    }
            //}
            //else
            //{
            //    this.ProcessError(e);
            //}
        }
        private void OnReceive(object sender, SocketAsyncEventArgs e)
        {
            // Signals the end of receive.
            autoSendReceiveEvents[SendOperation].Set();
        }
        private void ProcessError(SocketAsyncEventArgs e)
        {
            Socket s = e.UserToken as Socket;
            if (s.Connected)
            {
                // close the socket associated with the client
                try
                {
                    s.Shutdown(SocketShutdown.Both);
                }
                catch (Exception)
                {
                    // throws if client process has already closed
                }
                finally
                {
                    if (s.Connected)
                    {
                        s.Close();
                    }
                }
            }

            // Throw the SocketException
            throw new SocketException((Int32)e.SocketError);
        }

        #region IDisposable Members

        /// <summary>
        /// Disposes the instance of SocketClient.
        /// </summary>
        public void Dispose()
        {
            autoConnectEvent.Close();
            autoSendReceiveEvents[SendOperation].Close();
            autoSendReceiveEvents[ReceiveOperation].Close();
            if (this.clientSocket.Connected)
            {
                this.clientSocket.Close();
            }
        }

        #endregion
    }
}
