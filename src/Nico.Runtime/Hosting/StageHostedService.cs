using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nico.Runtime.Hosting;

public class StageHostedService(ILogger<StageHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("StageHostedService is starting.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("StageHostedService is stopping.");

        return Task.CompletedTask;
    }
}
