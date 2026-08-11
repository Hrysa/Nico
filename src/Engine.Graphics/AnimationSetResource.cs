using System.Text;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>Maps one stable gameplay alias to a clip inside an imported animation artifact.</summary>
/// <param name="Alias">Unique script-facing clip key.</param>
/// <param name="Source">Skinned-mesh or skeletal-animation artifact reference.</param>
/// <param name="Clip">Exact imported clip name, or null for the first clip.</param>
public readonly record struct AnimationSetEntry(
    string Alias,
    AssetReference Source,
    string? Clip = null);

/// <summary>Stores project-owned stable aliases for animation clips from multiple sources.</summary>
public sealed class AnimationSetResource
{
    private const string Magic = "NASET001";
    private readonly AnimationSetEntry[] _entries;

    /// <summary>Gets animation entries in authored order.</summary>
    public IReadOnlyList<AnimationSetEntry> Entries => _entries;

    /// <summary>Creates a validated immutable animation set.</summary>
    /// <param name="entries">Aliased source clips.</param>
    public AnimationSetResource(AnimationSetEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.ToArray();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < _entries.Length; index++)
        {
            var entry = _entries[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Alias);
            if (entry.Source.Asset.Value == Guid.Empty ||
                string.IsNullOrWhiteSpace(entry.Source.SubAsset))
                throw new ArgumentException("Animation entries require explicit source artifacts.",
                    nameof(entries));
            if (entry.Clip is not null && string.IsNullOrWhiteSpace(entry.Clip))
                throw new ArgumentException("Animation clip names cannot be empty.", nameof(entries));
            if (!aliases.Add(entry.Alias))
                throw new ArgumentException(
                    $"Animation alias '{entry.Alias}' is duplicated.", nameof(entries));
        }
    }

    /// <summary>Resolves, skeleton-binds, and aliases every authored source clip.</summary>
    /// <param name="target">Target skinned-mesh skeleton.</param>
    /// <param name="resolver">Resolver for explicit source animation artifacts.</param>
    /// <returns>Clips aligned to the target skeleton and named by stable aliases.</returns>
    public AnimationClipResource[] BindTo(SkeletonResource target,
        Func<AssetReference, SkeletalAnimationResource?> resolver)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resolver);
        var result = new AnimationClipResource[_entries.Length];
        for (var index = 0; index < _entries.Length; index++)
        {
            var entry = _entries[index];
            var source = resolver(entry.Source) ?? throw new InvalidDataException(
                $"Animation source '{entry.Source}' could not be resolved.");
            var bound = source.BindTo(target);
            var selected = FindClip(bound, entry.Clip) ?? throw new InvalidDataException(
                $"Animation clip '{entry.Clip ?? "<first>"}' is missing from " +
                $"source '{entry.Source}'.");
            result[index] = new AnimationClipResource(
                entry.Alias, selected.Duration, selected.Tracks.ToArray());
        }
        return result;
    }

    /// <summary>Writes one versioned Nico animation-set artifact.</summary>
    /// <param name="stream">Writable artifact stream.</param>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write(1u);
        writer.Write(checked((uint)_entries.Length));
        for (var index = 0; index < _entries.Length; index++)
        {
            var entry = _entries[index];
            writer.Write(entry.Alias);
            writer.Write(entry.Source.Asset.Value.ToByteArray());
            writer.Write(entry.Source.SubAsset!);
            writer.Write(entry.Clip is not null);
            if (entry.Clip is not null)
                writer.Write(entry.Clip!);
        }
    }

    /// <summary>Reads one versioned Nico animation-set artifact.</summary>
    /// <param name="stream">Readable artifact stream.</param>
    /// <returns>The decoded immutable animation set.</returns>
    public static AnimationSetResource Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != Magic)
            throw new InvalidDataException("Animation-set artifact has an invalid signature.");
        if (reader.ReadUInt32() != 1u)
            throw new InvalidDataException("Animation-set artifact version is unsupported.");
        var count = checked((int)reader.ReadUInt32());
        var entries = new AnimationSetEntry[count];
        for (var index = 0; index < entries.Length; index++)
        {
            var alias = reader.ReadString();
            var idBytes = reader.ReadBytes(16);
            if (idBytes.Length != 16)
                throw new EndOfStreamException("Animation-set asset ID is incomplete.");
            var source = new AssetReference(new AssetId(new Guid(idBytes)), reader.ReadString());
            entries[index] = new AnimationSetEntry(alias, source,
                reader.ReadBoolean() ? reader.ReadString() : null);
        }
        if (stream.CanSeek && stream.Position != stream.Length)
            throw new InvalidDataException("Animation-set artifact contains trailing data.");
        return new AnimationSetResource(entries);
    }

    /// <summary>Finds an exact named source clip or the first when no name is supplied.</summary>
    /// <param name="clips">Skeleton-bound source clips.</param>
    /// <param name="name">Optional exact imported name.</param>
    /// <returns>The matching clip, or null.</returns>
    private static AnimationClipResource? FindClip(
        AnimationClipResource[] clips, string? name)
    {
        if (name is null)
            return clips.Length > 0 ? clips[0] : null;
        for (var index = 0; index < clips.Length; index++)
        {
            if (string.Equals(clips[index].Name, name, StringComparison.Ordinal))
                return clips[index];
        }
        return null;
    }
}
