using System.Text;
using Engine.Core;

namespace Engine.Assets;

/// <summary>Stores one validated asset record and its filesystem validation stamp.</summary>
/// <param name="Id">Persistent asset identity.</param>
/// <param name="ProjectPath">Normalized project-relative source path.</param>
/// <param name="Importer">Stable importer identifier.</param>
/// <param name="SourceLength">Source file length.</param>
/// <param name="SourceWriteTicks">Source UTC write timestamp ticks.</param>
/// <param name="MetadataLength">Sidecar file length.</param>
/// <param name="MetadataWriteTicks">Sidecar UTC write timestamp ticks.</param>
internal sealed record AssetIndexEntry(
    AssetId Id,
    string ProjectPath,
    string Importer,
    long SourceLength,
    long SourceWriteTicks,
    long MetadataLength,
    long MetadataWriteTicks);

/// <summary>Reads and atomically writes the disposable binary asset startup index.</summary>
internal static class AssetIndexCache
{
    private const uint Magic = 0x5844494E;
    private const int Version = 1;
    private const int MaximumEntryCount = 10_000_000;
    private const int MaximumStringBytes = 1_048_576;

    /// <summary>Returns the generated index path for one project.</summary>
    /// <param name="projectRoot">Normalized project root.</param>
    /// <returns>The absolute generated binary index path.</returns>
    internal static string GetPath(string projectRoot)
    {
        return Path.Combine(Path.GetFullPath(projectRoot), ".nico", "cache", "asset-index.bin");
    }

    /// <summary>Loads a valid binary index or returns an empty cache after corruption/incompatibility.</summary>
    /// <param name="projectRoot">Normalized project root.</param>
    /// <returns>Validated cached entries.</returns>
    internal static IReadOnlyList<AssetIndexEntry> Load(string projectRoot)
    {
        var path = GetPath(projectRoot);
        if (!File.Exists(path))
            return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
                return [];
            var count = reader.ReadInt32();
            if (count < 0 || count > MaximumEntryCount)
                return [];
            var entries = new AssetIndexEntry[count];
            var paths = new HashSet<string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var ids = new HashSet<AssetId>();
            for (var index = 0; index < count; index++)
            {
                var id = new AssetId(new Guid(reader.ReadBytes(16)));
                var projectPath = ReadString(reader);
                var importer = ReadString(reader);
                if (!paths.Add(projectPath) || !ids.Add(id))
                    return [];
                entries[index] = new AssetIndexEntry(id, projectPath, importer,
                    reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
            }
            if (stream.Position != stream.Length)
                return [];
            return entries;
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException
            or ArgumentException or FormatException)
        {
            return [];
        }
    }

    /// <summary>Atomically publishes a deterministic binary startup index.</summary>
    /// <param name="projectRoot">Normalized project root.</param>
    /// <param name="entries">Validated entries to store.</param>
    internal static void Save(string projectRoot, IEnumerable<AssetIndexEntry> entries)
    {
        var path = GetPath(projectRoot);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The asset index path has no parent directory.");
        Directory.CreateDirectory(directory);
        var ordered = entries.OrderBy(entry => entry.ProjectPath, StringComparer.Ordinal).ToArray();
        var temporaryPath = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(ordered.Length);
                foreach (var entry in ordered)
                {
                    writer.Write(entry.Id.Value.ToByteArray());
                    WriteString(writer, entry.ProjectPath);
                    WriteString(writer, entry.Importer);
                    writer.Write(entry.SourceLength);
                    writer.Write(entry.SourceWriteTicks);
                    writer.Write(entry.MetadataLength);
                    writer.Write(entry.MetadataWriteTicks);
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>Reads one bounded UTF-8 string.</summary>
    /// <param name="reader">Binary cache reader.</param>
    /// <returns>The decoded string.</returns>
    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > MaximumStringBytes)
            throw new InvalidDataException("Asset index string length is invalid.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Writes one bounded UTF-8 string.</summary>
    /// <param name="writer">Binary cache writer.</param>
    /// <param name="value">String to encode.</param>
    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaximumStringBytes)
            throw new InvalidDataException("Asset index string is too long.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
