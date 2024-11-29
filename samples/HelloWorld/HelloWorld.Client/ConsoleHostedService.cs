using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Nico.Net;

namespace HelloWorld.Client;

public class ConsoleHostedService : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(Execute);

        await Task.CompletedTask;
    }

    void Execute()
    {
        var client = new R2Udp();
        var connection = client.Connect(new IPEndPoint(IPAddress.Loopback, 10001));
        connection.OnMessage = (conn, data, length) =>
        {
            Console.WriteLine("client rcv: " + Encoding.UTF8.GetString(data.AsSpan().Slice(0, length)));
        };

        while (true)
        {
            byte[] buff = Encoding.UTF8.GetBytes(Console.ReadLine());
            connection.Send(buff);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
