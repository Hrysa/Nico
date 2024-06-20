using System.Buffers;
using System.Net;
using System.Runtime.InteropServices;
using Nico.Net;

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Helper.TimeBeginPeriod(1);
}

var server = new R2Udp(new IPEndPoint(IPAddress.Any, 8888));

byte[] buff = new byte[40 * 8];
server.OnMessage = (channel, data, length) =>
{
    ArrayPool<byte>.Shared.Rent(100);
    channel.Send(data.AsSpan().Slice(0, length).ToArray());
};

server.Start();

Console.ReadLine();

foreach (var conn in server.Connections.Values)
{
    Console.WriteLine($"{conn.DropFragmentCount}/{conn.FragmentCount} rtt {conn.RttMin}/{conn.RttMax}/{conn.RttMean}");
}

Console.ReadLine();
