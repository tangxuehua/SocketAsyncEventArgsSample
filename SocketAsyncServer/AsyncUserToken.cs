using System;
using System.Net.Sockets;

namespace SocketAsyncServer
{
    public sealed class AsyncUserToken : IDisposable
    {
        public Socket Socket { get; private set; }
        public int? MessageSize { get; set; }
        public int DataStartOffset { get; set; }
        public int NextReceiveOffset { get; set; }

        public AsyncUserToken(Socket socket)
        {
            this.Socket = socket;
        }

        #region IDisposable Members

        public void Dispose()
        {
            try
            {
                this.Socket.Shutdown(SocketShutdown.Send);
            }
            catch (Exception)
            { }

            try
            {
                this.Socket.Close();
            }
            catch (Exception)
            { }
        }

        #endregion
    }
}
