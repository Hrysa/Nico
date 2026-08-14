using Engine.Core;

namespace Engine.Assets;

/// <summary>Identifies one stable typed runtime resource slot.</summary>
/// <typeparam name="TResource">Runtime resource type.</typeparam>
/// <param name="Value">Process-local non-zero slot identity.</param>
public readonly record struct ResourceHandle<TResource>(ulong Value);

/// <summary>Identifies the current state of an asynchronous runtime resource slot.</summary>
public enum ResourceLoadState
{
    /// <summary>The fallback value is available while initial loading runs.</summary>
    Loading,

    /// <summary>A successfully loaded resource is available.</summary>
    Ready,

    /// <summary>Loading failed and the previous resource or fallback remains available.</summary>
    Failed
}

/// <summary>Loads one runtime resource type from a resolved artifact stream.</summary>
public interface IRuntimeResourceLoader
{
    /// <summary>Gets the stable artifact content type supported by this loader.</summary>
    string ContentType { get; }

    /// <summary>Gets the runtime resource type produced by this loader.</summary>
    Type ResourceType { get; }

    /// <summary>Loads one runtime resource from an artifact stream.</summary>
    /// <param name="stream">Readable artifact stream.</param>
    /// <param name="resolved">Resolved artifact metadata.</param>
    /// <param name="cancellationToken">Manager shutdown cancellation.</param>
    /// <returns>The loaded runtime resource.</returns>
    ValueTask<object> LoadAsync(
        Stream stream,
        ResolvedAsset resolved,
        CancellationToken cancellationToken);
}

/// <summary>Adapts a typed decoding delegate to the runtime resource loader contract.</summary>
/// <typeparam name="TResource">Decoded resource type.</typeparam>
public sealed class DelegateRuntimeResourceLoader<TResource> : IRuntimeResourceLoader
    where TResource : class
{
    private readonly Func<Stream, ResolvedAsset, CancellationToken, TResource> _load;

    /// <inheritdoc/>
    public string ContentType { get; }

    /// <inheritdoc/>
    public Type ResourceType => typeof(TResource);

    /// <summary>Creates a typed resource loader.</summary>
    /// <param name="contentType">Stable artifact content type.</param>
    /// <param name="load">Synchronous stream decoder.</param>
    public DelegateRuntimeResourceLoader(
        string contentType,
        Func<Stream, ResolvedAsset, CancellationToken, TResource> load)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(load);
        ContentType = contentType;
        _load = load;
    }

    /// <inheritdoc/>
    public ValueTask<object> LoadAsync(
        Stream stream,
        ResolvedAsset resolved,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object>(_load(stream, resolved, cancellationToken));
    }
}

/// <summary>Retires replaced runtime resources according to subsystem lifetime rules.</summary>
public interface IRuntimeResourceRetirement
{
    /// <summary>Schedules or immediately performs retirement of an owned resource.</summary>
    /// <param name="resource">Previously loaded runtime resource.</param>
    void Retire(object resource);
}

/// <summary>Immediately disposes ordinary CPU resources when they leave a runtime slot.</summary>
public sealed class ImmediateResourceRetirement : IRuntimeResourceRetirement
{
    /// <inheritdoc/>
    public void Retire(object resource)
    {
        if (resource is IDisposable disposable)
            disposable.Dispose();
    }
}

/// <summary>Owns stable typed handles backed by asynchronously replaceable runtime resources.</summary>
public sealed class RuntimeResourceManager : IDisposable
{
    private readonly IAssetResolver _resolver;
    private readonly IAssetStorage _storage;
    private readonly IRuntimeResourceRetirement _retirement;
    private readonly Dictionary<(AssetReference Reference, Type Type), Entry> _byReference = new();
    private readonly Dictionary<ulong, Entry> _byHandle = new();
    private readonly Dictionary<(string ContentType, Type Type), IRuntimeResourceLoader> _loaders = new();
    private readonly LinkedList<Entry> _unusedRecency = new();
    private readonly int _unusedCapacity;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sync = new();
    private ulong _nextHandle = 1;
    private bool _disposed;

    /// <summary>Occurs after a resource slot becomes ready, fails, or is reloaded.</summary>
    public event Action<ulong, ResourceLoadState>? Changed;

    /// <summary>Creates a runtime manager over asset resolution and stream storage.</summary>
    /// <param name="resolver">Persistent-reference artifact resolver.</param>
    /// <param name="storage">Resolved-location stream storage.</param>
    /// <param name="retirement">Optional subsystem-aware resource retirement.</param>
    public RuntimeResourceManager(
        IAssetResolver resolver,
        IAssetStorage storage,
        IRuntimeResourceRetirement? retirement = null,
        int unusedCapacity = 128)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(storage);
        if (unusedCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(unusedCapacity));
        _resolver = resolver;
        _storage = storage;
        _retirement = retirement ?? new ImmediateResourceRetirement();
        _unusedCapacity = unusedCapacity;
    }

    /// <summary>Registers one unique content-type and runtime-type loader.</summary>
    /// <param name="loader">Runtime loader implementation.</param>
    public void RegisterLoader(IRuntimeResourceLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentException.ThrowIfNullOrWhiteSpace(loader.ContentType);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_loaders.TryAdd((loader.ContentType, loader.ResourceType), loader))
            {
                throw new InvalidOperationException(
                    $"A loader for '{loader.ContentType}' and '{loader.ResourceType.Name}' is already registered.");
            }
        }
    }

    /// <summary>Acquires a stable handle and starts loading when this reference is not cached.</summary>
    /// <typeparam name="TResource">Required runtime resource type.</typeparam>
    /// <param name="reference">Persistent asset or sub-asset reference.</param>
    /// <param name="fallback">Non-null value available until loading succeeds.</param>
    /// <returns>A stable process-local typed handle.</returns>
    public ResourceHandle<TResource> Acquire<TResource>(
        AssetReference reference,
        TResource fallback) where TResource : class
    {
        ArgumentNullException.ThrowIfNull(fallback);
        Entry entry;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = (reference, typeof(TResource));
            if (_byReference.TryGetValue(key, out entry!))
            {
                if (entry.ReferenceCount == 0 && entry.UnusedNode is not null)
                {
                    _unusedRecency.Remove(entry.UnusedNode);
                    entry.UnusedNode = null;
                }
                entry.ReferenceCount++;
                return new ResourceHandle<TResource>(entry.Handle);
            }
            entry = new Entry(_nextHandle++, reference, typeof(TResource), fallback);
            _byReference.Add(key, entry);
            _byHandle.Add(entry.Handle, entry);
            entry.LoadVersion++;
            entry.ActiveLoad = LoadEntryAsync(entry, entry.LoadVersion);
        }
        return new ResourceHandle<TResource>(entry.Handle);
    }

    /// <summary>Gets the current fallback or loaded value for a handle.</summary>
    /// <typeparam name="TResource">Runtime resource type.</typeparam>
    /// <param name="handle">Stable typed resource handle.</param>
    /// <returns>The current resource value.</returns>
    public TResource Get<TResource>(ResourceHandle<TResource> handle) where TResource : class
    {
        lock (_sync)
        {
            var entry = Require(handle.Value, typeof(TResource));
            return (TResource)entry.Current;
        }
    }

    /// <summary>Gets the current asynchronous state of a resource handle.</summary>
    /// <typeparam name="TResource">Runtime resource type.</typeparam>
    /// <param name="handle">Stable typed resource handle.</param>
    /// <returns>The current load state.</returns>
    public ResourceLoadState GetState<TResource>(ResourceHandle<TResource> handle)
        where TResource : class
    {
        lock (_sync)
            return Require(handle.Value, typeof(TResource)).State;
    }

    /// <summary>Gets the last loading error for a failed resource handle.</summary>
    /// <typeparam name="TResource">Runtime resource type.</typeparam>
    /// <param name="handle">Stable typed resource handle.</param>
    /// <returns>The last error, or null after a successful load.</returns>
    public Exception? GetError<TResource>(ResourceHandle<TResource> handle)
        where TResource : class
    {
        lock (_sync)
            return Require(handle.Value, typeof(TResource)).Error;
    }

    /// <summary>Waits for the active initial load or reload of a resource handle.</summary>
    /// <typeparam name="TResource">Runtime resource type.</typeparam>
    /// <param name="handle">Stable typed resource handle.</param>
    /// <returns>A task completing after the active load publishes state.</returns>
    public Task WaitAsync<TResource>(ResourceHandle<TResource> handle) where TResource : class
    {
        lock (_sync)
            return Require(handle.Value, typeof(TResource)).ActiveLoad;
    }

    /// <summary>Reloads every cached runtime type for one persistent asset reference.</summary>
    /// <param name="reference">Persistent asset or sub-asset reference.</param>
    /// <returns>A task completing after all matching slots publish state.</returns>
    public Task ReloadAsync(AssetReference reference)
    {
        Task[] tasks;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entries = _byReference.Where(pair => pair.Key.Reference == reference)
                .Select(pair => pair.Value).ToArray();
            tasks = new Task[entries.Length];
            for (var index = 0; index < entries.Length; index++)
            {
                entries[index].State = ResourceLoadState.Loading;
                entries[index].LoadVersion++;
                entries[index].ActiveLoad = LoadEntryAsync(
                    entries[index], entries[index].LoadVersion);
                tasks[index] = entries[index].ActiveLoad;
            }
        }
        return Task.WhenAll(tasks);
    }

    /// <summary>Reloads every pinned runtime value and evicts every unused value for one asset.</summary>
    /// <param name="asset">Persistent asset whose published generation changed.</param>
    /// <returns>A task completing after all pinned values publish their new generation.</returns>
    public Task ReloadAsync(AssetId asset)
    {
        Task[] tasks;
        List<object>? retired = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entries = _byReference.Values
                .Where(entry => entry.Reference.Asset == asset).ToArray();
            var reloadCount = 0;
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (entry.ReferenceCount == 0)
                {
                    if (entry.UnusedNode is not null)
                        _unusedRecency.Remove(entry.UnusedNode);
                    entry.UnusedNode = null;
                    _byHandle.Remove(entry.Handle);
                    _byReference.Remove((entry.Reference, entry.ResourceType));
                    entry.Removed = true;
                    if (entry.OwnsCurrent)
                        (retired ??= []).Add(entry.Current);
                    continue;
                }
                reloadCount++;
            }
            tasks = new Task[reloadCount];
            var taskIndex = 0;
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (entry.Removed)
                    continue;
                entry.State = ResourceLoadState.Loading;
                entry.LoadVersion++;
                entry.ActiveLoad = LoadEntryAsync(entry, entry.LoadVersion);
                tasks[taskIndex++] = entry.ActiveLoad;
            }
        }
        if (retired is not null)
        {
            for (var index = 0; index < retired.Count; index++)
                _retirement.Retire(retired[index]);
        }
        return Task.WhenAll(tasks);
    }

    /// <summary>Evicts every unpinned decoded resource belonging to one persistent asset.</summary>
    /// <param name="asset">Persistent asset identity whose published generation changed.</param>
    public void Invalidate(AssetId asset)
    {
        List<object>? retired = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entries = _byReference.Values
                .Where(entry => entry.Reference.Asset == asset && entry.ReferenceCount == 0)
                .ToArray();
            foreach (var entry in entries)
            {
                if (entry.UnusedNode is not null)
                    _unusedRecency.Remove(entry.UnusedNode);
                entry.UnusedNode = null;
                _byHandle.Remove(entry.Handle);
                _byReference.Remove((entry.Reference, entry.ResourceType));
                entry.Removed = true;
                if (entry.OwnsCurrent)
                    (retired ??= []).Add(entry.Current);
            }
        }
        if (retired is not null)
        {
            foreach (var resource in retired)
                _retirement.Retire(resource);
        }
    }

    /// <summary>Releases one acquired reference and retires its owned resource at zero references.</summary>
    /// <typeparam name="TResource">Runtime resource type.</typeparam>
    /// <param name="handle">Stable typed resource handle.</param>
    public void Release<TResource>(ResourceHandle<TResource> handle) where TResource : class
    {
        List<object>? retired = null;
        lock (_sync)
        {
            var entry = Require(handle.Value, typeof(TResource));
            if (entry.ReferenceCount <= 0)
                throw new InvalidOperationException("A resource handle cannot be released twice.");
            entry.ReferenceCount--;
            if (entry.ReferenceCount > 0)
                return;
            entry.UnusedNode = _unusedRecency.AddFirst(entry);
            while (_unusedRecency.Count > _unusedCapacity)
            {
                var evicted = _unusedRecency.Last!.Value;
                _unusedRecency.RemoveLast();
                evicted.UnusedNode = null;
                _byHandle.Remove(evicted.Handle);
                _byReference.Remove((evicted.Reference, evicted.ResourceType));
                evicted.Removed = true;
                if (evicted.OwnsCurrent)
                    (retired ??= []).Add(evicted.Current);
            }
        }
        if (retired is not null)
        {
            foreach (var resource in retired)
                _retirement.Retire(resource);
        }
    }

    /// <summary>Cancels active loads and retires every manager-owned resource.</summary>
    public void Dispose()
    {
        object[] retired;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _shutdown.Cancel();
            retired = _byHandle.Values.Where(entry => entry.OwnsCurrent)
                .Select(entry => entry.Current).ToArray();
            foreach (var entry in _byHandle.Values)
                entry.Removed = true;
            _byHandle.Clear();
            _byReference.Clear();
            _unusedRecency.Clear();
        }
        foreach (var resource in retired)
            _retirement.Retire(resource);
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Loads and atomically publishes one resource slot while retaining failures safely.</summary>
    /// <param name="entry">Resource slot to load.</param>
    /// <param name="loadVersion">Slot generation owned by this load.</param>
    /// <returns>A task completing after publication.</returns>
    private async Task LoadEntryAsync(Entry entry, long loadVersion)
    {
        object? loaded = null;
        Exception? error = null;
        try
        {
            var resolved = _resolver.Resolve(entry.Reference);
            IRuntimeResourceLoader loader;
            lock (_sync)
            {
                if (!_loaders.TryGetValue((resolved.ContentType, entry.ResourceType), out loader!))
                {
                    throw new KeyNotFoundException(
                        $"No runtime loader produces '{entry.ResourceType.Name}' from '{resolved.ContentType}'.");
                }
            }
            using var stream = _storage.OpenRead(resolved.Location);
            loaded = await loader.LoadAsync(stream, resolved, _shutdown.Token).ConfigureAwait(false);
            if (loaded is null || !entry.ResourceType.IsInstanceOfType(loaded))
                throw new InvalidDataException("Runtime loader returned an incompatible resource type.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_disposed)
        {
            error = exception;
        }

        object? retired = null;
        ResourceLoadState state;
        var publish = true;
        lock (_sync)
        {
            if (entry.Removed || _disposed || entry.LoadVersion != loadVersion)
            {
                if (loaded is not null)
                    retired = loaded;
                publish = false;
            }
            else if (error is null)
            {
                if (entry.OwnsCurrent)
                    retired = entry.Current;
                entry.Current = loaded!;
                entry.OwnsCurrent = true;
                entry.Error = null;
                entry.State = ResourceLoadState.Ready;
            }
            else
            {
                entry.Error = error;
                entry.State = ResourceLoadState.Failed;
            }
            state = entry.State;
        }
        if (retired is not null)
            _retirement.Retire(retired);
        if (publish)
            Changed?.Invoke(entry.Handle, state);
    }

    /// <summary>Returns a required handle entry with matching runtime type.</summary>
    /// <param name="handle">Process-local slot identity.</param>
    /// <param name="resourceType">Expected runtime resource type.</param>
    /// <returns>The matching resource slot.</returns>
    private Entry Require(ulong handle, Type resourceType)
    {
        if (handle == 0 || !_byHandle.TryGetValue(handle, out var entry) ||
            entry.ResourceType != resourceType)
        {
            throw new KeyNotFoundException($"Runtime resource handle '{handle}' was not found.");
        }
        return entry;
    }

    /// <summary>Stores one stable runtime resource slot.</summary>
    private sealed class Entry
    {
        /// <summary>Gets the stable process-local handle.</summary>
        internal ulong Handle { get; }

        /// <summary>Gets the persistent source reference.</summary>
        internal AssetReference Reference { get; }

        /// <summary>Gets the required runtime resource type.</summary>
        internal Type ResourceType { get; }

        /// <summary>Gets or sets the current fallback or loaded value.</summary>
        internal object Current { get; set; }

        /// <summary>Gets or sets whether the manager owns the current value.</summary>
        internal bool OwnsCurrent { get; set; }

        /// <summary>Gets or sets the current load state.</summary>
        internal ResourceLoadState State { get; set; } = ResourceLoadState.Loading;

        /// <summary>Gets or sets the last load error.</summary>
        internal Exception? Error { get; set; }

        /// <summary>Gets or sets the active load task.</summary>
        internal Task ActiveLoad { get; set; } = Task.CompletedTask;

        /// <summary>Gets or sets the number of active acquisitions.</summary>
        internal int ReferenceCount { get; set; } = 1;

        /// <summary>Gets or sets whether the slot left manager lookup tables.</summary>
        internal bool Removed { get; set; }

        /// <summary>Gets or sets the latest asynchronous load generation.</summary>
        internal long LoadVersion { get; set; }

        /// <summary>Gets or sets this entry's zero-reference LRU node.</summary>
        internal LinkedListNode<Entry>? UnusedNode { get; set; }

        /// <summary>Creates one fallback-backed runtime resource slot.</summary>
        /// <param name="handle">Stable process-local handle.</param>
        /// <param name="reference">Persistent asset reference.</param>
        /// <param name="resourceType">Required runtime resource type.</param>
        /// <param name="fallback">Caller-owned fallback value.</param>
        internal Entry(ulong handle, AssetReference reference, Type resourceType, object fallback)
        {
            Handle = handle;
            Reference = reference;
            ResourceType = resourceType;
            Current = fallback;
        }
    }
}
