namespace Engine.Assets;

/// <summary>Provides read-only access to one normalized virtual file namespace.</summary>
public interface IVirtualFileSystem
{
    /// <summary>Returns whether a virtual file exists.</summary>
    /// <param name="path">Slash-normalized path relative to this filesystem.</param>
    /// <returns>True when the path identifies a readable file.</returns>
    bool Exists(string path);

    /// <summary>Opens one virtual file for reading.</summary>
    /// <param name="path">Slash-normalized path relative to this filesystem.</param>
    /// <returns>A readable stream owned by the caller.</returns>
    Stream OpenRead(string path);

    /// <summary>Enumerates immediate files and directories beneath a virtual directory.</summary>
    /// <param name="directory">Slash-normalized directory relative to this filesystem.</param>
    /// <returns>Immediate child paths relative to this filesystem.</returns>
    IEnumerable<string> Enumerate(string directory);
}

/// <summary>Normalizes virtual paths without permitting rooted or parent traversal.</summary>
public static class VirtualPath
{
    /// <summary>Normalizes one relative virtual path.</summary>
    /// <param name="path">Virtual path using either platform separator.</param>
    /// <param name="allowEmpty">Whether an empty root path is accepted.</param>
    /// <returns>A slash-normalized relative virtual path.</returns>
    public static string Normalize(string path, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            if (allowEmpty)
                return string.Empty;
            throw new ArgumentException("A virtual file path cannot be empty.", nameof(path));
        }
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
            throw new ArgumentException("A virtual path must be relative and cannot traverse parents.",
                nameof(path));
        return string.Join('/', segments);
    }

    /// <summary>Combines normalized virtual path segments.</summary>
    /// <param name="left">Leading virtual path or empty root.</param>
    /// <param name="right">Trailing virtual path.</param>
    /// <returns>The combined normalized path.</returns>
    public static string Combine(string left, string right)
    {
        var normalizedLeft = Normalize(left, allowEmpty: true);
        var normalizedRight = Normalize(right);
        return normalizedLeft.Length == 0
            ? normalizedRight : $"{normalizedLeft}/{normalizedRight}";
    }
}
