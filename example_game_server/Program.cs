using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ExampleGame.Server;

/// <summary>Starts the example game's headless authoritative physics process.</summary>
internal static class Program
{
    /// <summary>Parses configuration and runs the fixed-tick server loop.</summary>
    /// <param name="args">Server command-line arguments.</param>
    /// <returns>Process exit code.</returns>
    private static int Main(string[] args)
    {
        if (!ServerOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 2;
        }
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(console => console.SingleLine = true);
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("ExampleGame.Server");
        try
        {
            return Run(options!, loggerFactory, logger);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Authoritative server stopped unexpectedly");
            return 1;
        }
    }

    /// <summary>Runs authoritative ticks until cancellation or the configured tick limit.</summary>
    /// <param name="options">Validated server configuration.</param>
    /// <param name="loggerFactory">Factory creating the simulation logger.</param>
    /// <param name="logger">Process lifecycle logger.</param>
    /// <returns>Zero after an orderly shutdown.</returns>
    private static int Run(
        ServerOptions options,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            using var server = new AuthoritativePhysicsServer(
                options.ScenePath,
                options.TickRate,
                options.Port,
                options.NetworkSnapshotRate,
                TimeSpan.FromSeconds(options.ClientTimeoutSeconds),
                loggerFactory.CreateLogger<AuthoritativePhysicsServer>());
            logger.LogInformation(
                "Server listening on UDP port {Port}; press Ctrl+C to stop", server.Port);
            RunTicks(server, options, cancellation.Token);
            if (server.Tick == 0 || server.Tick % options.SnapshotInterval != 0)
                server.LogSnapshot();
            logger.LogInformation("Server stopped after tick {Tick}", server.Tick);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>Schedules fixed simulation ticks with bounded wall-clock drift.</summary>
    /// <param name="server">Authoritative simulation to advance.</param>
    /// <param name="options">Tick scheduling options.</param>
    /// <param name="cancellationToken">Process shutdown signal.</param>
    private static void RunTicks(
        AuthoritativePhysicsServer server,
        ServerOptions options,
        CancellationToken cancellationToken)
    {
        var timestampStep = Stopwatch.Frequency / (double)options.TickRate;
        var nextTimestamp = (double)Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested &&
            (!options.MaximumTicks.HasValue || server.Tick < options.MaximumTicks.Value))
        {
            if (!options.RunWithoutDelay)
                WaitUntil(nextTimestamp, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                break;
            server.Step();
            if (server.Tick % options.SnapshotInterval == 0)
                server.LogSnapshot();
            nextTimestamp += timestampStep;
            var now = Stopwatch.GetTimestamp();
            if (now - nextTimestamp > timestampStep * 8d)
                nextTimestamp = now;
        }
    }

    /// <summary>Waits efficiently for one absolute stopwatch timestamp.</summary>
    /// <param name="targetTimestamp">Absolute target in stopwatch ticks.</param>
    /// <param name="cancellationToken">Shutdown signal checked during the wait.</param>
    private static void WaitUntil(double targetTimestamp, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var remaining = (targetTimestamp - Stopwatch.GetTimestamp()) / Stopwatch.Frequency;
            if (remaining <= 0d)
                return;
            if (remaining > 0.002d)
                Thread.Sleep(Math.Max(1, (int)(remaining * 1000d) - 1));
            else
                Thread.SpinWait(64);
        }
    }

    /// <summary>Prints the supported server command-line syntax.</summary>
    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: dotnet run --project example_game_server -- " +
            "[--scene <path>] [--tick-rate <hz>] [--ticks <count>] " +
            "[--snapshot-interval <ticks>] [--port <udp-port>] " +
            "[--network-snapshot-rate <hz>] [--client-timeout <seconds>] [--no-delay]");
    }
}
