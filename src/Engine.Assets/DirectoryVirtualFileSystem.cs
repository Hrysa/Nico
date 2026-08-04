namespace Engine.Assets;

/// <summary>Exposes a physical directory through safe read-only virtual paths.</summary>
public sealed class DirectoryVirtualFileSystem : IVirtualFileSystem
{
    /// <summary>Gets the normalized absolute physical root.</summary>
    public string Root { get; }

    /// <summary>Creates a read-only mount over an existing physical directory.</summary>
    /// <param name="root">Physical directory root.</param>
    public DirectoryVirtualFileSystem(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root))
            throw new DirectoryNotFoundException($"Virtual filesystem root does not exist: {Root}");
    }

    /// <inheritdoc/>
    public bool Exists(string path)
    {
        return File.Exists(Resolve(path, allowEmpty: false));
    }

    /// <inheritdoc/>
    public Stream OpenRead(string path)
    {
        var fullPath = Resolve(path, allowEmpty: false);
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.SequentialScan);
    }

    /// <inheritdoc/>
    public IEnumerable<string> Enumerate(string directory)
    {
        var normalized = VirtualPath.Normalize(directory, allowEmpty: true);
        var fullPath = Resolve(normalized, allowEmpty: true);
        if (!Directory.Exists(fullPath))
            return [];
        return Directory.EnumerateFileSystemEntries(fullPath)
            .Where(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            .Select(path => VirtualPath.Combine(normalized, Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Resolves a virtual path and rejects traversal through physical links.</summary>
    /// <param name="path">Virtual path to resolve.</param>
    /// <param name="allowEmpty">Whether the mounted root is accepted.</param>
    /// <returns>The contained absolute physical path.</returns>
    private string Resolve(string path, bool allowEmpty)
    {
        var normalized = VirtualPath.Normalize(path, allowEmpty);
        var fullPath = Path.GetFullPath(Path.Combine(Root,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(Root, fullPath);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Virtual paths must remain beneath the mounted root.",
                nameof(path));
        }
        RejectReparseTraversal(relative);
        return fullPath;
    }

    /// <summary>Rejects existing reparse points beneath the physical mount root.</summary>
    /// <param name="relativePath">Platform-relative path beneath the root.</param>
    private void RejectReparseTraversal(string relativePath)
    {
        if (relativePath == ".")
            return;
        var current = Root;
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"Virtual path crosses a filesystem link: {current}");
            }
        }
    }
}
