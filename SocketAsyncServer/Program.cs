using System;
using System.Net;

namespace SocketAsyncServer
{
    public static class Program
    {
        private const Int32 DEFAULT_PORT = 9900, DEFAULT_MAX_NUM_CONNECTIONS = 10, DEFAULT_BUFFER_SIZE = 30000;

        public static void Main(String[] args)
        {
            try
            {
                SocketListener listener = new SocketListener(DEFAULT_MAX_NUM_CONNECTIONS, DEFAULT_BUFFER_SIZE);
                listener.Start(new IPEndPoint(IPAddress.Parse("127.0.0.1"), DEFAULT_PORT));

                Console.WriteLine("Server listening on port {0}. Press any key to terminate the server process...", DEFAULT_PORT);
                Console.Read();

                listener.Stop();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }
    }
}
