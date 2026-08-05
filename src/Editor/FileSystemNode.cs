using Engine.Core;

namespace Editor;

/// <summary>Represents one project filesystem entry in the editor tree.</summary>
public sealed class FileSystemNode : Node
{
    /// <summary>Gets the absolute filesystem path represented by this node.</summary>
    public string FullPath { get; }

    /// <summary>Gets whether this entry is a directory.</summary>
    public bool IsDirectory { get; }

    /// <inheritdoc/>
    public override bool CanHaveChildren => IsDirectory || Children.Count > 0;

    /// <summary>Creates a project filesystem tree node.</summary>
    /// <param name="fullPath">Absolute entry path.</param>
    /// <param name="isDirectory">Whether the entry is a directory.</param>
    public FileSystemNode(string fullPath, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        FullPath = Path.GetFullPath(fullPath);
        IsDirectory = isDirectory;
        Name = Path.GetFileName(FullPath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(Name))
            Name = FullPath;
    }
}
