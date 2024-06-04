using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Nico.Net;

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Helper.TimeBeginPeriod(1);
}

List<R2Udp> clients = new();

for (int i = 0; i < 100; i++)
{
    var client = new R2Udp();
    clients.Add(client);
    var connection = client.Connect(new IPEndPoint(IPAddress.Loopback, 8888));
    connection.OnMessage = (conn, data, length) =>
    {
        // Console.WriteLine("client rcv: " + Encoding.UTF8.GetString(data.AsSpan().Slice(0, length)));
    };
}

foreach (var client in clients)
{
    Helper.Loop(client.Receive);
    Helper.Loop(client.Send);
    Helper.Loop(client.Update);

    while (true)
    {
        // byte[] buff = Encoding.UTF8.GetBytes(Console.ReadLine());
        byte[] buff = new byte[3000];
        Thread.Sleep(20);
        foreach (var simpleUdp in clients)
        {
            simpleUdp.Connection!.Send(buff);
        }
    }
}

Console.ReadLine();
