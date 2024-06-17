using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Nico.Net;

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Helper.TimeBeginPeriod(1);
}

var server = new R2Udp(new IPEndPoint(IPAddress.Any, 8888));

byte[] buff = new byte[40 * 8];
server.OnMessage = (channel, data, length) =>
{
    Console.WriteLine(Encoding.UTF8.GetString(data.AsSpan().Slice(0, length)));
    channel.Send(buff);
    // channel.Send(data.AsSpan().Slice(0, length).ToArray());
};

// server.StartReceive();
server.Start();

Console.ReadLine();

foreach (var conn in server.Connections.Values)
{
    Console.WriteLine($"{conn.DropFragmentCount}/{conn.FragmentCount} rtt {conn.RttMin}/{conn.RttMax}/{conn.RttMean}");
}

Console.ReadLine();

