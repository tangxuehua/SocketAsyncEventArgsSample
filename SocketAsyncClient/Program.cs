using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace SocketAsyncClient
{
    static class Program
    {
        static void Main(string[] args)
        {
            try
            {
                int iterations = 100000;
                int clientCount = 1;
                int messageSize = 1024;
                var data = new byte[messageSize];
                var message = BuildMessage(data);

                var action = new Action(() =>
                {
                    var client = new SocketClient(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9900));
                    client.Connect();
                    for (var i = 0; i < iterations; i++)
                    {
                        client.Send(message);
                    }
                });

                var actionList = new List<Action>();
                for (var index = 0; index < clientCount; index++)
                {
                    actionList.Add(action);
                }

                Parallel.Invoke(actionList.ToArray());

                Console.WriteLine("Press any key to terminate the client process...");
                Console.Read();
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
                Console.Read();
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
    }
}
