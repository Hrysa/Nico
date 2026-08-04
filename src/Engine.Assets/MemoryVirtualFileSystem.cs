namespace Engine.Assets;

/// <summary>Stores immutable virtual files in memory for built-in and generated content.</summary>
public sealed class MemoryVirtualFileSystem : IVirtualFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    /// <summary>Adds or replaces one immutable in-memory virtual file.</summary>
    /// <param name="path">Virtual file path.</param>
    /// <param name="content">File bytes copied into the filesystem.</param>
    public void Set(string path, ReadOnlySpan<byte> content)
    {
        _files[VirtualPath.Normalize(path)] = content.ToArray();
    }

    /// <inheritdoc/>
    public bool Exists(string path)
    {
        return _files.ContainsKey(VirtualPath.Normalize(path));
    }

    /// <inheritdoc/>
    public Stream OpenRead(string path)
    {
        var normalized = VirtualPath.Normalize(path);
        return _files.TryGetValue(normalized, out var content)
            ? new MemoryStream(content, writable: false)
            : throw new FileNotFoundException("Virtual file was not found.", normalized);
    }

    /// <inheritdoc/>
    public IEnumerable<string> Enumerate(string directory)
    {
        var normalized = VirtualPath.Normalize(directory, allowEmpty: true);
        var prefix = normalized.Length == 0 ? string.Empty : normalized + "/";
        return _files.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(path => path[prefix.Length..])
            .Where(remainder => remainder.Length > 0)
            .Select(remainder => remainder.Split('/')[0])
            .Distinct(StringComparer.Ordinal)
            .Select(child => VirtualPath.Combine(normalized, child))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }
}
