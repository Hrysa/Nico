using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace Nico.Net;

public class R2UdpRunner : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() =>
        {
            while (true)
            {
                foreach (var r2Udp in R2Udp.Group)
                {
                    r2Udp.Receive();
                }

                Thread.Sleep(1);
            }
        });

        _ = Task.Run(() =>
        {
            while (true)
            {
                foreach (var r2Udp in R2Udp.Group)
                {
                    r2Udp.Send();
                }

                Thread.Sleep(1);
            }
        });

        _ = Task.Run(() =>
        {
            while (true)
            {
                foreach (var r2Udp in R2Udp.Group)
                {
                    r2Udp.Update();
                }

                Thread.Sleep(1);
            }
        });

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("stop r2udp runner");
        return Task.CompletedTask;
    }
}
