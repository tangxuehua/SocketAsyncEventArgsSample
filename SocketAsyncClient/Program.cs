using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketAsyncClient
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            try
            {
                SocketClient client = new SocketClient(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9900));
                client.Connect();
                int iterations = 25000;
                int threadCount = 40;

                var action = new Action(() =>
                {
                    for (var i = 0; i < iterations; i++)
                    {
                        client.Send("Hello World");
                    }
                });

                var actionList = new List<Action>();
                for (var index = 0; index < threadCount; index++)
                {
                    actionList.Add(action);
                }

                Parallel.Invoke(actionList.ToArray());

                Console.WriteLine("Press any key to terminate the client process...");
                Console.Read();
                client.Disconnect();
                client.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                Console.Read();
            }
        }
    }
}
