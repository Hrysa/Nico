using Microsoft.Extensions.Hosting;
using Nico.Discovery.Abstractions;

namespace Nico.Core;

public class NicoHostedService : IHostedService
{
    private readonly IDiscovery _discovery;

    public NicoHostedService(IDiscovery discovery)
    {
        _discovery = discovery;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
