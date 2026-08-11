using Engine.Graphics;

namespace Editor;

/// <summary>Creates project-owned animation-set assets from explicit aliased clip references.</summary>
public static class AnimationSetAuthoring
{
    /// <summary>Writes one validated `.nanimset` source asset.</summary>
    /// <param name="path">Destination project path.</param>
    /// <param name="entries">Stable aliases and explicit source clips.</param>
    public static void Save(string path, AnimationSetEntry[] entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);
        var resource = new AnimationSetResource(entries);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Animation-set path has no parent directory.");
        Directory.CreateDirectory(directory);
        using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        resource.Save(stream);
    }
}
