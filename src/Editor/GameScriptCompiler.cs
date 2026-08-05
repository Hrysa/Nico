using System.Diagnostics;
using System.Text.RegularExpressions;
using Engine.Assets;
using Engine.Scripting;

namespace Editor;

/// <summary>Tracks script inputs and performs incremental Play-time builds.</summary>
public sealed partial class GameScriptCompiler : IDisposable
{
    private readonly ScriptingWorkspace _workspace;
    private readonly FileSystemWatcher _watcher;
    private readonly object _stateLock = new();
    private readonly string _assemblyPath;
    private readonly AssetDatabase? _assetDatabase;
    private long _changeVersion = 1;
    private long _projectChangeVersion = 1;
    private long _successfulVersion;
    private long _restoredProjectVersion;
    private bool _disposed;

    /// <summary>Creates a compiler that observes relevant project inputs without scanning them on Play.</summary>
    /// <param name="workspace">Scripting workspace to compile.</param>
    public GameScriptCompiler(ScriptingWorkspace workspace, AssetDatabase? assetDatabase = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _assetDatabase = assetDatabase;
        var projectDirectory = Path.GetDirectoryName(workspace.ScriptProjectPath)
            ?? throw new ArgumentException("The script project has no parent directory.", nameof(workspace));
        var projectName = Path.GetFileNameWithoutExtension(workspace.ScriptProjectPath);
        var outputDirectory = Path.Combine(projectDirectory, "bin", "EditorPlay");
        _assemblyPath = Path.Combine(outputDirectory, $"{projectName}.dll");
        var projectRoot = Path.GetDirectoryName(projectDirectory) ?? projectDirectory;
        _watcher = new FileSystemWatcher(projectRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Builds changed scripts when necessary and loads a fresh runtime host.</summary>
    /// <param name="cancellationToken">Cancels the active SDK process.</param>
    /// <returns>A host loaded from the latest successful compilation.</returns>
    public GameScriptHost BuildAndLoad(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long version;
        long projectVersion;
        lock (_stateLock)
        {
            version = _changeVersion;
            projectVersion = _projectChangeVersion;
        }
        if (_successfulVersion != version || !File.Exists(_assemblyPath))
        {
            var restore = _restoredProjectVersion != projectVersion
                || !File.Exists(Path.Combine(
                    Path.GetDirectoryName(_workspace.ScriptProjectPath)!, "obj", "project.assets.json"));
            Build(restore, cancellationToken);
            lock (_stateLock)
            {
                _successfulVersion = version;
                if (restore)
                    _restoredProjectVersion = projectVersion;
            }
        }
        IReadOnlyList<ScriptAssetDescriptor>? scripts = null;
        if (_assetDatabase is not null)
        {
            _assetDatabase.Refresh();
            var analysis = CSharpScriptAnalyzer.Analyze(
                _assetDatabase, Path.GetDirectoryName(_assemblyPath)!);
            if (analysis.Diagnostics.Count > 0)
                throw new ScriptAnalysisException(analysis.Diagnostics);
            scripts = analysis.Scripts;
            CompiledScriptCatalog.Save(
                CompiledScriptCatalog.GetCatalogPath(_assemblyPath),
                scripts.Select(script => new CompiledScriptEntry(script.Asset, script.TypeName)));
        }
        return GameScriptHost.Load(_assemblyPath, scripts);
    }

    /// <summary>Stops observing script inputs.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _watcher.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Runs one incremental SDK build.</summary>
    /// <param name="restore">Whether dependency restore must be allowed.</param>
    /// <param name="cancellationToken">Cancellation token for the SDK process.</param>
    private void Build(bool restore, CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(_assemblyPath)!;
        Directory.CreateDirectory(outputDirectory);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_workspace.ScriptProjectPath)!
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(_workspace.ScriptProjectPath);
        if (!restore)
            startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("quiet");
        startInfo.ArgumentList.Add($"-p:OutputPath={outputDirectory}{Path.DirectorySeparatorChar}");
        startInfo.ArgumentList.Add("-p:AppendTargetFrameworkToOutputPath=false");
        startInfo.ArgumentList.Add("-p:AppendRuntimeIdentifierToOutputPath=false");
        startInfo.ArgumentList.Add("-p:UseSharedCompilation=true");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the .NET SDK to build game scripts.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode == 0)
            return;
        var output = standardOutput.Result + standardError.Result;
        throw new ScriptBuildException(output, ParseDiagnostics(output));
    }

    /// <summary>Marks a created, changed, or deleted build input dirty.</summary>
    /// <param name="sender">Watcher that produced the event.</param>
    /// <param name="args">Changed filesystem path.</param>
    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        MarkDirty(args.FullPath);
    }

    /// <summary>Marks both sides of a rename dirty.</summary>
    /// <param name="sender">Watcher that produced the event.</param>
    /// <param name="args">Rename details.</param>
    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        MarkDirty(args.OldFullPath);
        MarkDirty(args.FullPath);
    }

    /// <summary>Falls back to a dependency-aware rebuild after watcher overflow or failure.</summary>
    /// <param name="sender">Watcher that reported failure.</param>
    /// <param name="args">Watcher error details.</param>
    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        lock (_stateLock)
        {
            _changeVersion++;
            _projectChangeVersion++;
        }
    }

    /// <summary>Classifies and records one potentially relevant path.</summary>
    /// <param name="path">Changed path.</param>
    private void MarkDirty(string path)
    {
        if (IsBuildOutput(path))
            return;
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        var source = extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);
        var project = extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("NuGet.config", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase);
        if (!source && !project)
            return;
        lock (_stateLock)
        {
            _changeVersion++;
            if (project)
                _projectChangeVersion++;
        }
    }

    /// <summary>Checks whether a watcher event originated in generated build directories.</summary>
    /// <param name="path">Candidate path.</param>
    /// <returns>True for bin or obj descendants.</returns>
    private static bool IsBuildOutput(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || part.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parses compiler diagnostics from SDK output.</summary>
    /// <param name="output">Combined build output.</param>
    /// <returns>Structured diagnostics found in the output.</returns>
    private static IReadOnlyList<ScriptBuildDiagnostic> ParseDiagnostics(string output)
    {
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => DiagnosticPattern().Match(line))
            .Where(match => match.Success)
            .Select(match => new ScriptBuildDiagnostic(
                match.Groups["file"].Value,
                int.Parse(match.Groups["line"].Value),
                int.Parse(match.Groups["column"].Value),
                match.Groups["severity"].Value,
                match.Groups["code"].Value,
                match.Groups["message"].Value.Trim()))
            .Distinct()
            .ToArray();
    }

    /// <summary>Matches standard C# compiler diagnostic output.</summary>
    /// <returns>Generated diagnostic regular expression.</returns>
    [GeneratedRegex(@"^(?<file>.+)\((?<line>\d+),(?<column>\d+)\):\s+(?<severity>warning|error)\s+(?<code>[A-Z]+\d+):\s+(?<message>.*?)(?:\s+\[.+\])?$")]
    private static partial Regex DiagnosticPattern();
}

/// <summary>Reports a failed game-script build with structured compiler diagnostics.</summary>
public sealed class ScriptBuildException : Exception
{
    /// <summary>Gets parsed compiler diagnostics.</summary>
    public IReadOnlyList<ScriptBuildDiagnostic> Diagnostics { get; }

    /// <summary>Creates a failed-build exception.</summary>
    /// <param name="output">Complete SDK output.</param>
    /// <param name="diagnostics">Parsed compiler diagnostics.</param>
    public ScriptBuildException(string output, IReadOnlyList<ScriptBuildDiagnostic> diagnostics)
        : base($"Game script build failed.{Environment.NewLine}{output}")
    {
        Diagnostics = diagnostics;
    }
}

/// <summary>Identifies one source diagnostic emitted by the script compiler.</summary>
/// <param name="File">Source file.</param>
/// <param name="Line">One-based line.</param>
/// <param name="Column">One-based column.</param>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="Code">Compiler diagnostic code.</param>
/// <param name="Message">Diagnostic message.</param>
public sealed record ScriptBuildDiagnostic(
    string File,
    int Line,
    int Column,
    string Severity,
    string Code,
    string Message);
