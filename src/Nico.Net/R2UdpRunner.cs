using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace Nico.Net;

public class R2UdpRunner : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("stop r2udp runner");
        return Task.CompletedTask;
    }
}
