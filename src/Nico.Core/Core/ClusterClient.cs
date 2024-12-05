using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nico.Core;

public class ClusterClient(ILogger<ClusterClient> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("client started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("client stopped");
        return Task.CompletedTask;
    }
}
