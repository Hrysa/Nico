using System.Net;
using System.Runtime.InteropServices;
using Nico.Net;

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Helper.TimeBeginPeriod(1);
}

List<R2Udp> clients = new();

for (int i = 0; i < 1000; i++)
{
    var client = new R2Udp();
    clients.Add(client);
    var connection = client.Connect(new IPEndPoint(IPAddress.Parse("192.168.0.250"), 8888));
    connection.OnMessage = (conn, data, length) =>
    {
        // Console.WriteLine("client rcv: " + Encoding.UTF8.GetString(data.AsSpan().Slice(0, length)));
    };
}


foreach (var client in clients)
{
    client.StartReceive();
}


DateTimeOffset start = DateTimeOffset.UtcNow;
byte[] buff = "hello world, hello world, hello world, hello world\n"u8.ToArray();
for (int i = 0; i < 33 * 10; i++)
{
    // Thread.Sleep(30);
    // Console.ReadLine();
    foreach (var r2Udpse in clients.Chunk(300))
    {
        // await Task.Delay(2);
        Thread.Sleep(1);
        foreach (var client in r2Udpse)
        {
            client.Connection!.Send(buff);
        }
    }
}

await Task.Delay(5000);
var idx = 0;
foreach (var client in clients)
{
    var conn = client.Connection!;

    Console.WriteLine(
        $"{idx++:0000} {conn.DropFragmentCount}/{conn.FragmentCount} rtt {conn.RttMin}/{conn.RttMax}/{conn.RttMean}");
}

Console.WriteLine($"total {DateTimeOffset.UtcNow - start}");

Console.ReadLine();
