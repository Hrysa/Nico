namespace Engine.Assets;

/// <summary>Routes named virtual mount paths through prioritized filesystem layers.</summary>
public sealed class MountedVirtualFileSystem : IVirtualFileSystem
{
    private readonly Dictionary<string, List<MountLayer>> _mounts = new(StringComparer.Ordinal);

    /// <summary>Adds a filesystem layer to a named virtual mount.</summary>
    /// <param name="name">Single-segment mount name, such as <c>game</c> or <c>engine</c>.</param>
    /// <param name="filesystem">Filesystem layer.</param>
    /// <param name="priority">Higher values override lower values.</param>
    public void Mount(string name, IVirtualFileSystem filesystem, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(filesystem);
        var normalizedName = VirtualPath.Normalize(name);
        if (normalizedName.Contains('/'))
            throw new ArgumentException("A mount name must contain one path segment.", nameof(name));
        if (!_mounts.TryGetValue(normalizedName, out var layers))
        {
            layers = [];
            _mounts.Add(normalizedName, layers);
        }
        layers.Add(new MountLayer(filesystem, priority));
        layers.Sort((left, right) => right.Priority.CompareTo(left.Priority));
    }

    /// <inheritdoc/>
    public bool Exists(string path)
    {
        var (layers, relativePath) = ResolveMount(path);
        return layers.Any(layer => layer.Filesystem.Exists(relativePath));
    }

    /// <inheritdoc/>
    public Stream OpenRead(string path)
    {
        var (layers, relativePath) = ResolveMount(path);
        foreach (var layer in layers)
        {
            if (layer.Filesystem.Exists(relativePath))
                return layer.Filesystem.OpenRead(relativePath);
        }
        throw new FileNotFoundException("Mounted virtual file was not found.", path);
    }

    /// <inheritdoc/>
    public IEnumerable<string> Enumerate(string directory)
    {
        var normalized = VirtualPath.Normalize(directory, allowEmpty: true);
        if (normalized.Length == 0)
            return _mounts.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var (layers, relativePath) = ResolveMount(normalized, allowMountRoot: true);
        var mountName = normalized.Split('/')[0];
        return layers.SelectMany(layer => layer.Filesystem.Enumerate(relativePath))
            .Distinct(StringComparer.Ordinal)
            .Select(path => VirtualPath.Combine(mountName, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Resolves a mounted path to prioritized filesystem layers and a relative path.</summary>
    /// <param name="path">Mounted virtual path.</param>
    /// <param name="allowMountRoot">Whether a path containing only the mount name is accepted.</param>
    /// <returns>Prioritized layers and mount-relative path.</returns>
    private (IReadOnlyList<MountLayer> Layers, string RelativePath) ResolveMount(
        string path,
        bool allowMountRoot = false)
    {
        var normalized = VirtualPath.Normalize(path);
        var separator = normalized.IndexOf('/');
        var mountName = separator < 0 ? normalized : normalized[..separator];
        var relativePath = separator < 0 ? string.Empty : normalized[(separator + 1)..];
        if (relativePath.Length == 0 && !allowMountRoot)
            throw new ArgumentException("A mounted file path must include a path after its mount.",
                nameof(path));
        if (!_mounts.TryGetValue(mountName, out var layers))
            throw new KeyNotFoundException($"Virtual filesystem mount '{mountName}' was not found.");
        return (layers, relativePath);
    }

    /// <summary>Associates one filesystem layer with its override priority.</summary>
    /// <param name="Filesystem">Mounted filesystem layer.</param>
    /// <param name="Priority">Override priority.</param>
    private sealed record MountLayer(IVirtualFileSystem Filesystem, int Priority);
}
