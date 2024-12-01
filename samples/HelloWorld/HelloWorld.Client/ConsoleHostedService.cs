using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HelloWorld.Client;

public class ConsoleHostedService : IHostedService
{
    private Task _task = default!;
    private bool _running;
    private readonly ILogger<ConsoleHostedService> _logger;

    public ConsoleHostedService(ILogger<ConsoleHostedService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _running = true;
        _task = Task.Run(Execute, CancellationToken.None);

        _logger.LogInformation("Console Started");

        return Task.CompletedTask;
    }

    private void Execute()
    {
        while (_running)
        {
            Console.ReadLine();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _running = false;
        await _task;
        _logger.LogInformation("Console Stopped");
    }
}
