namespace ExampleGame.Server;

/// <summary>Stores command-line configuration for the authoritative physics server.</summary>
internal sealed record ServerOptions(
    string ScenePath,
    int TickRate,
    long? MaximumTicks,
    int SnapshotInterval,
    bool RunWithoutDelay)
{
    /// <summary>Parses supported server command-line arguments.</summary>
    /// <param name="args">Arguments supplied to the process.</param>
    /// <param name="options">Parsed options when successful.</param>
    /// <param name="error">Validation error when parsing fails.</param>
    /// <returns>True when every argument is valid.</returns>
    internal static bool TryParse(
        string[] args,
        out ServerOptions? options,
        out string? error)
    {
        var scenePath = FindDefaultScenePath();
        var tickRate = 60;
        long? maximumTicks = null;
        var snapshotInterval = 60;
        var runWithoutDelay = false;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--no-delay")
            {
                runWithoutDelay = true;
                continue;
            }
            if (!TryReadValue(args, ref index, argument, out var value, out error))
            {
                options = null;
                return false;
            }
            switch (argument)
            {
                case "--scene":
                    scenePath = Path.GetFullPath(value!);
                    break;
                case "--tick-rate" when int.TryParse(value, out var parsedRate) && parsedRate > 0:
                    tickRate = parsedRate;
                    break;
                case "--ticks" when long.TryParse(value, out var parsedTicks) && parsedTicks >= 0:
                    maximumTicks = parsedTicks;
                    break;
                case "--snapshot-interval" when int.TryParse(value, out var parsedInterval) &&
                    parsedInterval > 0:
                    snapshotInterval = parsedInterval;
                    break;
                default:
                    options = null;
                    error = $"Unknown or invalid argument: {argument} {value}";
                    return false;
            }
        }
        options = new ServerOptions(
            scenePath, tickRate, maximumTicks, snapshotInterval, runWithoutDelay);
        error = null;
        return true;
    }

    /// <summary>Reads the value following one named command-line option.</summary>
    /// <param name="args">Complete process arguments.</param>
    /// <param name="index">Current argument index, advanced to the value.</param>
    /// <param name="argument">Named option requiring a value.</param>
    /// <param name="value">Option value when present.</param>
    /// <param name="error">Validation error when the value is missing.</param>
    /// <returns>True when a following value exists.</returns>
    private static bool TryReadValue(
        string[] args,
        ref int index,
        string argument,
        out string? value,
        out string? error)
    {
        if (index + 1 < args.Length)
        {
            value = args[++index];
            error = null;
            return true;
        }
        value = null;
        error = $"Missing value for {argument}.";
        return false;
    }

    /// <summary>Finds the example scene from common repository and build-output locations.</summary>
    /// <returns>Absolute path to the first existing candidate, or the repository-relative candidate.</returns>
    private static string FindDefaultScenePath()
    {
        var workingDirectoryCandidate = Path.GetFullPath(
            Path.Combine("example_game", "scenes", "scene.node"));
        if (File.Exists(workingDirectoryCandidate))
            return workingDirectoryCandidate;
        var siblingCandidate = Path.GetFullPath(
            Path.Combine("..", "example_game", "scenes", "scene.node"));
        if (File.Exists(siblingCandidate))
            return siblingCandidate;
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "example_game", "scenes", "scene.node"));
    }
}
