namespace Engine.Assets;

/// <summary>Coalesces project filesystem changes into safe asset database refresh requests.</summary>
public sealed class AssetDatabaseWatcher : IDisposable
{
    private static readonly HashSet<string> _ignoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".nico", "bin", "obj"
    };
    private readonly string _projectRoot;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _timer;
    private readonly object _sync = new();
    private readonly TimeSpan _debounce;
    private bool _pending;
    private bool _disposed;

    /// <summary>Occurs once after a burst of relevant project changes settles.</summary>
    public event Action? RefreshRequested;

    /// <summary>Starts observing one project tree for source and sidecar changes.</summary>
    /// <param name="projectRoot">Project root to observe recursively.</param>
    /// <param name="debounce">Optional change-coalescing delay.</param>
    public AssetDatabaseWatcher(string projectRoot, TimeSpan? debounce = null)
        : this(projectRoot, debounce, startNativeWatcher: true)
    {
    }

    /// <summary>Creates a watcher with optional native observation for deterministic tests.</summary>
    /// <param name="projectRoot">Project root to observe.</param>
    /// <param name="debounce">Optional change-coalescing delay.</param>
    /// <param name="startNativeWatcher">Whether to start <see cref="FileSystemWatcher"/>.</param>
    internal AssetDatabaseWatcher(
        string projectRoot,
        TimeSpan? debounce,
        bool startNativeWatcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _projectRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(_projectRoot))
            throw new DirectoryNotFoundException($"Asset project root does not exist: {_projectRoot}");
        _debounce = debounce ?? TimeSpan.FromMilliseconds(150);
        if (_debounce < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce));
        _timer = new Timer(OnDebounceElapsed, null, Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        if (!startNativeWatcher)
            return;
        _watcher = new FileSystemWatcher(_projectRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }

    /// <summary>Stops native observation and pending debounce callbacks.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending = false;
        }
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChanged;
            _watcher.Changed -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
        }
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Schedules a refresh when a path belongs to editable project content.</summary>
    /// <param name="path">Absolute changed path.</param>
    internal void SchedulePath(string path)
    {
        if (!IsRelevant(path))
            return;
        Schedule();
    }

    /// <summary>Returns whether a changed path can affect the authoritative asset index.</summary>
    /// <param name="path">Absolute changed path.</param>
    /// <returns>False for generated, build, and version-control directories.</returns>
    internal bool IsRelevant(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_projectRoot, fullPath);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            return false;
        }
        return !relative.Split(Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => _ignoredDirectories.Contains(segment));
    }

    /// <summary>Restarts the debounce timer for one relevant change.</summary>
    private void Schedule()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _pending = true;
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Handles created, changed, and deleted filesystem entries.</summary>
    /// <param name="sender">Native watcher.</param>
    /// <param name="args">Changed path.</param>
    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        SchedulePath(args.FullPath);
    }

    /// <summary>Handles both sides of a filesystem rename.</summary>
    /// <param name="sender">Native watcher.</param>
    /// <param name="args">Old and new paths.</param>
    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        if (IsRelevant(args.OldFullPath) || IsRelevant(args.FullPath))
            Schedule();
    }

    /// <summary>Requests reconciliation after a native watcher buffer or I/O error.</summary>
    /// <param name="sender">Native watcher.</param>
    /// <param name="args">Watcher error details.</param>
    private void OnError(object sender, ErrorEventArgs args)
    {
        Schedule();
    }

    /// <summary>Publishes one coalesced refresh request on a thread-pool callback.</summary>
    /// <param name="state">Unused timer state.</param>
    private void OnDebounceElapsed(object? state)
    {
        lock (_sync)
        {
            if (_disposed || !_pending)
                return;
            _pending = false;
        }
        RefreshRequested?.Invoke();
    }
}
