using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Engine.Graphics;
using Engine.UI;

namespace Engine.UI.Benchmarks;

/// <summary>Runs repeatable retained-UI CPU and allocation baseline scenarios.</summary>
internal static class Program
{
    private const int DefaultSampleCount = 31;
    private const int WarmupCount = 7;
    private const double RegressionFraction = 0.15d;
    private const double TimerNoiseMilliseconds = 0.005d;

    /// <summary>Runs every benchmark and optionally writes or verifies a named-machine baseline.</summary>
    /// <param name="args">Command-line options.</param>
    /// <returns>Zero on success, one on regression, or two for invalid arguments.</returns>
    private static int Main(string[] args)
    {
        if (!TryParseArguments(args, out var sampleCount, out var writePath, out var verifyPath))
        {
            PrintUsage();
            return 2;
        }

        var results = RunBenchmarks(sampleCount);
        var baseline = new UIBenchmarkBaseline(
            Environment.MachineName,
            GetProcessorName(),
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            DateTimeOffset.UtcNow,
            results);
        PrintResults(baseline);

        if (writePath is not null)
            WriteBaseline(writePath, baseline);
        if (verifyPath is not null && !VerifyBaseline(verifyPath, baseline))
            return 1;
        return HasAbsoluteBudgetFailure(results) ? 1 : 0;
    }

    /// <summary>Parses supported sample-count, write-baseline, and verify-baseline switches.</summary>
    /// <param name="args">Raw command-line arguments.</param>
    /// <param name="sampleCount">Resolved odd sample count.</param>
    /// <param name="writePath">Optional output baseline path.</param>
    /// <param name="verifyPath">Optional comparison baseline path.</param>
    /// <returns>True when every option is valid.</returns>
    private static bool TryParseArguments(
        string[] args,
        out int sampleCount,
        out string? writePath,
        out string? verifyPath)
    {
        sampleCount = DefaultSampleCount;
        writePath = null;
        verifyPath = null;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (index + 1 >= args.Length)
                return false;
            var value = args[++index];
            if (option == "--samples")
            {
                if (!int.TryParse(value, out sampleCount) || sampleCount < 5 || sampleCount > 501)
                    return false;
                if ((sampleCount & 1) == 0)
                    sampleCount++;
            }
            else if (option == "--write-baseline")
            {
                writePath = value;
            }
            else if (option == "--verify-baseline")
            {
                verifyPath = value;
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Prints command-line usage.</summary>
    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: dotnet run --project tools/Engine.UI.Benchmarks -c Release -- " +
            "[--samples N] [--write-baseline path] [--verify-baseline path]");
    }

    /// <summary>Runs all named Phase 0 scenarios in a stable order.</summary>
    /// <param name="sampleCount">Measured sample count per scenario.</param>
    /// <returns>Ordered benchmark results.</returns>
    private static UIBenchmarkResult[] RunBenchmarks(int sampleCount)
    {
        return
        [
            BenchmarkUnchangedUpdate(sampleCount),
            BenchmarkUnchangedPointerMove(sampleCount),
            BenchmarkChangedPointerMove(sampleCount),
            BenchmarkCachedComposition(sampleCount),
            BenchmarkDirtySubtree(sampleCount),
            BenchmarkVirtualizedScroll(sampleCount),
            BenchmarkAnimationControllers(sampleCount)
        ];
    }

    /// <summary>Measures 100 independently blended two-state poses over 80-joint skeletons.</summary>
    /// <param name="sampleCount">Measured sample count.</param>
    /// <returns>Timing and allocation statistics.</returns>
    private static UIBenchmarkResult BenchmarkAnimationControllers(int sampleCount)
    {
        const int characterCount = 100;
        const int jointCount = 80;
        var joints = new SkeletonJoint[jointCount];
        var firstTracks = new JointAnimationTrack?[jointCount];
        var secondTracks = new JointAnimationTrack?[jointCount];
        for (var index = 0; index < jointCount; index++)
        {
            joints[index] = new SkeletonJoint($"Joint{index}", index - 1,
                JointTransform.Identity, Matrix4x4.Identity);
            firstTracks[index] = CreateBenchmarkTrack(Vector3.Zero, Vector3.UnitX);
            secondTracks[index] = CreateBenchmarkTrack(Vector3.Zero, Vector3.UnitY);
        }
        var resource = new SkinnedMeshResource(
            new StaticMeshResource([], [], []), [], new SkeletonResource(joints),
            [new AnimationClipResource("First", 1f, firstTracks),
             new AnimationClipResource("Second", 1f, secondTracks)]);
        var controllers = new AnimationController[characterCount];
        for (var index = 0; index < controllers.Length; index++)
        {
            controllers[index] = new AnimationController(resource);
            controllers[index].Play("First", 0f);
            controllers[index].Play("Second", 1f);
        }
        return Measure(
            "animation-100x80x2",
            "Advance 100 characters with 80 joints and two active blended states each.",
            5.00d,
            0,
            10,
            sampleCount,
            () =>
            {
                for (var index = 0; index < controllers.Length; index++)
                    controllers[index].Advance(1d / 600d);
            });
    }

    /// <summary>Creates one two-key translation track used by the animation benchmark.</summary>
    /// <param name="start">Translation at time zero.</param>
    /// <param name="end">Translation at time one.</param>
    /// <returns>A joint track with linear translation keys.</returns>
    private static JointAnimationTrack CreateBenchmarkTrack(Vector3 start, Vector3 end) =>
        new(new Vector3AnimationTrack([0f, 1f], [start, end],
            AnimationInterpolation.Linear), null, null);

    /// <summary>Measures an unchanged continuous-style update over 2,000 retained elements.</summary>
    /// <param name="sampleCount">Measured sample count.</param>
    /// <returns>Timing and allocation statistics.</returns>
    private static UIBenchmarkResult BenchmarkUnchangedUpdate(int sampleCount)
    {
        var root = CreateFlatCanvas(2_000, out _, out _);
        root.BuildDrawList();
        return Measure(
            "unchanged-update-2000",
            "Advance unscaled/scaled host time through 2,000 unchanged retained elements.",
            0.05d,
            0,
            100,
            sampleCount,
            () => root.AdvanceTime(1d / 120d, 1d / 120d));
    }

    /// <summary>Measures pointer routing when the retained hover target remains unchanged.</summary>
    /// <param name="sampleCount">Measured sample count.</param>
    /// <returns>Timing and allocation statistics.</returns>
    private static UIBenchmarkResult BenchmarkUnchangedPointerMove(int sampleCount)
    {
        var root = CreateFlatCanvas(2_000, out var last, out _);
        root.BuildDrawList();
        var router = new UIEventRouter(root, EmptyCallback);
        var position = new Vector2(last.Left + 1f, last.Top + 1f);
        router.MovePointer(position);
        return Measure(
            "pointer-unchanged-2000",
            "Route pointer movement while the hover target remains unchanged in a 2,000-element tree.",
            0.05d,
            0,
            100,
            sampleCount,
            () => router.MovePointer(position));
    }

    /// <summary>Measures pointer routing while alternating between adjacent retained targets.</summary>
    /// <param name="sampleCount">Measured sample count.</param>
    /// <returns>Timing and allocation statistics.</returns>
    private static UIBenchmarkResult BenchmarkChangedPointerMove(int sampleCount)
    {
        var root = CreateFlatCanvas(2_000, out var last, out var previous);
        root.BuildDrawList();
        var router = new UIEventRouter(root, EmptyCallback);
        var firstPosition = new Vector2(last.Left + 1f, last.Top + 1f);
        var secondPosition = new Vector2(previous.Left + 1f, previous.Top + 1f);
        var useFirst = false;
        return Measure(
            "pointer-changed-2000",
            "Route pointer movement while alternating hover between two targets.",
            0.20d,
            0,
            100,
            sampleCount,
            () =>
            {
                useFirst = !useFirst;
                router.MovePointer(useFirst ? firstPosition : secondPosition);
            });
    }

    /// <summary>Measures retained composition reuse for 10,000 cached draw commands.</summary>
    /// <param name="sampleCount">Measured sample count.</param>
    /// <returns>Timing and allocation statistics.</returns>
    private static UIBenchmarkResult BenchmarkCachedComposition(int sampleCount)
    {
        var root = CreateFlatCanvas(10_000, out _, out _);
        var drawList = root.BuildDrawList();
        if (drawList.Commands.Count != 10_000)
            throw new InvalidOperationException("Cached-composition scene did not create 10,000 commands.");
        return Measure(
            "cached-composition-10000",
            "Return the unchanged retained snapshot containing 10,000 draw commands.",
            0.20d,
            0,
            1_000,
            sampleCount,
            () => root.BuildDrawList());
    }

    /// <summary>Measures layout and paint after one representative dirty-subtree invalidation.</summary>
    /// <param name="sampleCount">Measured sample count.</param>
    /// <returns>Timing and allocation statistics.</returns>
    private static UIBenchmarkResult BenchmarkDirtySubtree(int sampleCount)
    {
        var root = CreateFlatCanvas(2_000, out var leaf, out _);
        root.BuildDrawList();
        var white = false;
        return Measure(
            "dirty-layout-paint-2000",
            "Invalidate one leaf measurement and rebuild a 2,000-element retained snapshot.",
            1.00d,
            128,
            10,
            sampleCount,
            () =>
            {
                white = !white;
                leaf.BackgroundColor = white ? Color.White : Color.Gray;
                leaf.InvalidateMeasure();
                root.BuildDrawList();
            });
    }

    /// <summary>Measures bounded virtualized scrolling over 100 visible and 100,000 logical rows.</summary>
    /// <param name="sampleCount">Measured sample count.</param>
    /// <returns>Timing and allocation statistics.</returns>
    private static UIBenchmarkResult BenchmarkVirtualizedScroll(int sampleCount)
    {
        var items = new string[100_000];
        for (var index = 0; index < items.Length; index++)
            items[index] = $"Item {index}";
        var list = new ListView(500f, UITheme.Dark.ItemRowHeight * 100f);
        list.SetItems(items);
        list.BuildDrawList();
        var down = true;
        return Measure(
            "virtualized-scroll-100x100000",
            "Scroll and rebuild 100 visible rows backed by 100,000 logical items.",
            1.00d,
            12 * 1024,
            10,
            sampleCount,
            () =>
            {
                list.InvokeScroll(down ? -1f : 1f);
                down = !down;
                list.BuildDrawList();
            });
    }

    /// <summary>Creates an explicitly positioned flat retained benchmark tree.</summary>
    /// <param name="count">Number of paintable child elements.</param>
    /// <param name="last">Last child in paint and hit-test order.</param>
    /// <param name="previous">Penultimate child in paint and hit-test order.</param>
    /// <returns>Measured and arranged canvas root.</returns>
    private static Canvas CreateFlatCanvas(
        int count,
        out UIElement last,
        out UIElement previous)
    {
        var columns = 100;
        var rows = (count + columns - 1) / columns;
        var root = new Canvas
        {
            Width = columns * 12f,
            Height = rows * 12f
        };
        last = null!;
        previous = null!;
        for (var index = 0; index < count; index++)
        {
            var child = new Panel(Color.Gray, 10f, 10f);
            root.Add(child, new Vector2((index % columns) * 12f, (index / columns) * 12f));
            previous = last;
            last = child;
        }
        root.BuildDrawList();
        return root;
    }

    /// <summary>Measures repeated batches after warmup and computes exact ordered percentiles.</summary>
    /// <param name="name">Stable scenario identifier.</param>
    /// <param name="description">Human-readable measured operation.</param>
    /// <param name="targetMilliseconds">Absolute per-operation CPU target.</param>
    /// <param name="allocationBudgetBytes">Maximum median managed bytes allocated per operation.</param>
    /// <param name="operationsPerSample">Operations timed in each sample batch.</param>
    /// <param name="sampleCount">Measured batch count.</param>
    /// <param name="operation">Allocation-reused operation callback.</param>
    /// <returns>Per-operation p50/p95 timing and maximum allocation.</returns>
    private static UIBenchmarkResult Measure(
        string name,
        string description,
        double targetMilliseconds,
        long allocationBudgetBytes,
        int operationsPerSample,
        int sampleCount,
        Action operation)
    {
        for (var warmup = 0; warmup < WarmupCount; warmup++)
        {
            for (var operationIndex = 0; operationIndex < operationsPerSample; operationIndex++)
                operation();
        }

        var durations = new double[sampleCount];
        var allocations = new long[sampleCount];
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var timestamp = Stopwatch.GetTimestamp();
            for (var operationIndex = 0; operationIndex < operationsPerSample; operationIndex++)
                operation();
            var elapsed = Stopwatch.GetElapsedTime(timestamp);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            durations[sample] = elapsed.TotalMilliseconds / operationsPerSample;
            allocations[sample] = allocated / operationsPerSample;
        }
        Array.Sort(durations);
        Array.Sort(allocations);
        return new UIBenchmarkResult(
            name,
            description,
            targetMilliseconds,
            allocationBudgetBytes,
            durations[PercentileIndex(sampleCount, 0.50d)],
            durations[PercentileIndex(sampleCount, 0.95d)],
            allocations[PercentileIndex(sampleCount, 0.50d)],
            allocations[PercentileIndex(sampleCount, 0.95d)],
            sampleCount,
            operationsPerSample);
    }

    /// <summary>Converts a percentile into a nearest-rank zero-based array index.</summary>
    /// <param name="count">Sorted sample count.</param>
    /// <param name="percentile">Requested percentile from zero through one.</param>
    /// <returns>Clamped zero-based sample index.</returns>
    private static int PercentileIndex(int count, double percentile)
    {
        return Math.Clamp((int)Math.Ceiling(percentile * count) - 1, 0, count - 1);
    }

    /// <summary>Prints reference identity and an aligned result table.</summary>
    /// <param name="baseline">Current benchmark run.</param>
    private static void PrintResults(UIBenchmarkBaseline baseline)
    {
        Console.WriteLine($"Machine: {baseline.Machine}");
        Console.WriteLine($"Processor: {baseline.Processor}");
        Console.WriteLine($"OS: {baseline.OperatingSystem}");
        Console.WriteLine($"Runtime: {baseline.Runtime}");
        Console.WriteLine();
        Console.WriteLine("Scenario                              p50 ms    p95 ms  alloc p50/p95  target   status");
        for (var index = 0; index < baseline.Results.Length; index++)
        {
            var result = baseline.Results[index];
            var passed = result.P50Milliseconds <= result.TargetMilliseconds
                && result.P50AllocatedBytesPerOperation <= result.AllocationBudgetBytes;
            Console.WriteLine(
                $"{result.Name,-36} {result.P50Milliseconds,7:F4}  " +
                $"{result.P95Milliseconds,7:F4}  {result.P50AllocatedBytesPerOperation,5}/" +
                $"{result.P95AllocatedBytesPerOperation,-5}  " +
                $"{result.TargetMilliseconds,6:F2}   {(passed ? "PASS" : "FAIL")}");
        }
    }

    /// <summary>Gets a stable best-effort processor identity without platform-native dependencies.</summary>
    /// <returns>Processor description or architecture fallback.</returns>
    private static string GetProcessorName()
    {
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
            ?? RuntimeInformation.ProcessArchitecture.ToString();
    }

    /// <summary>Writes an indented machine-readable baseline, creating its parent directory when needed.</summary>
    /// <param name="path">Destination JSON path.</param>
    /// <param name="baseline">Baseline to serialize.</param>
    private static void WriteBaseline(string path, UIBenchmarkBaseline baseline)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(
            baseline,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Wrote baseline: {fullPath}");
    }

    /// <summary>Compares current medians with a stored same-machine baseline and allocation budgets.</summary>
    /// <param name="path">Stored baseline JSON path.</param>
    /// <param name="current">Current benchmark run.</param>
    /// <returns>True when no material median or allocation regression is present.</returns>
    private static bool VerifyBaseline(string path, UIBenchmarkBaseline current)
    {
        var baseline = JsonSerializer.Deserialize<UIBenchmarkBaseline>(File.ReadAllText(path))
            ?? throw new InvalidDataException("UI benchmark baseline was empty.");
        var passed = true;
        if (!string.Equals(baseline.Machine, current.Machine, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"Baseline machine '{baseline.Machine}' differs from '{current.Machine}'; " +
                "absolute budgets remain enforced and relative comparison is skipped.");
            return !HasAbsoluteBudgetFailure(current.Results);
        }

        for (var currentIndex = 0; currentIndex < current.Results.Length; currentIndex++)
        {
            var result = current.Results[currentIndex];
            var stored = FindResult(baseline.Results, result.Name);
            if (stored is null)
            {
                Console.Error.WriteLine($"Missing stored baseline scenario: {result.Name}");
                passed = false;
                continue;
            }
            var tolerance = Math.Max(
                stored.P50Milliseconds * RegressionFraction,
                TimerNoiseMilliseconds);
            if (result.P50Milliseconds > stored.P50Milliseconds + tolerance)
            {
                Console.Error.WriteLine(
                    $"REGRESSION {result.Name}: p50 {result.P50Milliseconds:F4} ms exceeds " +
                    $"stored {stored.P50Milliseconds:F4} ms + {tolerance:F4} ms tolerance.");
                passed = false;
            }
            if (result.P50AllocatedBytesPerOperation > stored.P50AllocatedBytesPerOperation)
            {
                Console.Error.WriteLine(
                    $"REGRESSION {result.Name}: median allocation " +
                    $"{result.P50AllocatedBytesPerOperation} B/op exceeds stored " +
                    $"{stored.P50AllocatedBytesPerOperation} B/op.");
                passed = false;
            }
        }
        return passed && !HasAbsoluteBudgetFailure(current.Results);
    }

    /// <summary>Finds a stored scenario without allocating an enumerable iterator.</summary>
    /// <param name="results">Stored ordered results.</param>
    /// <param name="name">Stable scenario identifier.</param>
    /// <returns>Matching result, or null when absent.</returns>
    private static UIBenchmarkResult? FindResult(UIBenchmarkResult[] results, string name)
    {
        for (var index = 0; index < results.Length; index++)
        {
            if (string.Equals(results[index].Name, name, StringComparison.Ordinal))
                return results[index];
        }
        return null;
    }

    /// <summary>Checks hard CPU and zero-allocation budgets for every current result.</summary>
    /// <param name="results">Current ordered results.</param>
    /// <returns>True when at least one hard budget failed.</returns>
    private static bool HasAbsoluteBudgetFailure(UIBenchmarkResult[] results)
    {
        for (var index = 0; index < results.Length; index++)
        {
            if (results[index].P50Milliseconds > results[index].TargetMilliseconds
                || results[index].P50AllocatedBytesPerOperation
                    > results[index].AllocationBudgetBytes)
                return true;
        }
        return false;
    }

    /// <summary>Provides an allocation-free invalidation callback for router benchmarks.</summary>
    private static void EmptyCallback()
    {
    }
}

/// <summary>Stores one named-machine retained-UI benchmark run.</summary>
/// <param name="Machine">Reference machine name.</param>
/// <param name="Processor">Reference processor description.</param>
/// <param name="OperatingSystem">Reference operating-system description.</param>
/// <param name="Runtime">Managed runtime description.</param>
/// <param name="CapturedAtUtc">UTC capture timestamp.</param>
/// <param name="Results">Ordered scenario results.</param>
internal sealed record UIBenchmarkBaseline(
    string Machine,
    string Processor,
    string OperatingSystem,
    string Runtime,
    DateTimeOffset CapturedAtUtc,
    UIBenchmarkResult[] Results);

/// <summary>Stores per-operation timing, allocation, and target data for one scenario.</summary>
/// <param name="Name">Stable scenario identifier.</param>
/// <param name="Description">Human-readable measured operation.</param>
/// <param name="TargetMilliseconds">Absolute p50 CPU target.</param>
/// <param name="AllocationBudgetBytes">Maximum median managed bytes allocated per operation.</param>
/// <param name="P50Milliseconds">Median per-operation duration.</param>
/// <param name="P95Milliseconds">95th-percentile per-operation duration.</param>
/// <param name="P50AllocatedBytesPerOperation">Median managed allocation per operation.</param>
/// <param name="P95AllocatedBytesPerOperation">95th-percentile managed allocation per operation.</param>
/// <param name="SampleCount">Measured batch count.</param>
/// <param name="OperationsPerSample">Operations in each timed batch.</param>
internal sealed record UIBenchmarkResult(
    string Name,
    string Description,
    double TargetMilliseconds,
    long AllocationBudgetBytes,
    double P50Milliseconds,
    double P95Milliseconds,
    long P50AllocatedBytesPerOperation,
    long P95AllocatedBytesPerOperation,
    int SampleCount,
    int OperationsPerSample);
