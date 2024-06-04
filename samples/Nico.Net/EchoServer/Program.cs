using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Nico.Net;

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Helper.TimeBeginPeriod(1);
}

var server = new R2Udp(new IPEndPoint(IPAddress.Any, 8888));

server.OnMessage = (channel, data, length) =>
{
    // Console.WriteLine("server rcv: " + Encoding.UTF8.GetString(data.AsSpan().Slice(0, length)));
    channel.Send(data.AsSpan().Slice(0, length).ToArray());
};

Helper.Loop(server.Receive);
Helper.Loop(server.Send);
Helper.Loop(server.Update);

Console.ReadLine();
