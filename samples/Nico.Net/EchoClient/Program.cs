using System.Net;
using System.Runtime.InteropServices;
using Nico.Net;

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Helper.TimeBeginPeriod(1);
}

List<R2Udp> clients = new();
DateTimeOffset sendTime = DateTimeOffset.Now;

for (int i = 0; i < 1000; i++)
{
    var client = new R2Udp();
    clients.Add(client);
    var connection = client.Connect(new IPEndPoint(IPAddress.Loopback, 8888));
    connection.OnMessage = (conn, data, length) =>
    {
        Console.WriteLine($"rcv {(DateTimeOffset.Now - connection.SendTime).TotalMilliseconds}ms");
        // Console.WriteLine("client rcv: " + Encoding.UTF8.GetString(data.AsSpan().Slice(0, length)));
    };
}

// Coroutine coroutine = new();
//
// coroutine.Start(() =>
// {
//     while (true)
//     {
//         Console.WriteLine(111);
//         yield return null;
//     }
// });

foreach (var client in clients)
{
    client.Start();
}


DateTimeOffset start = DateTimeOffset.UtcNow;
// byte[] buff = "hello world, hello world, hello world, hello world\n"u8.ToArray();

byte[] buff = new byte[4048];

// Task.Run(() =>
// {
while (true)
{
    foreach (var r2Udpse in clients.Chunk(500))
    {
        Thread.Sleep(1);
        Console.ReadLine();
        foreach (var client in r2Udpse)
        {
            client.Connection!.Send(buff);
        }
    }
}
// });

Console.ReadLine();

var idx = 0;
foreach (var client in clients)
{
    var conn = client.Connection!;

    Console.WriteLine(
        $"{idx++:0000} {conn.DropFragmentCount}/{conn.FragmentCount} rtt {conn.RttMin}/{conn.RttMax}/{conn.RttMean}");
}

Console.WriteLine($"total {DateTimeOffset.UtcNow - start}");
