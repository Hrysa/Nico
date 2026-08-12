using System.Numerics;
using System.Text;
using System.Text.Json;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>Maps one stable gameplay alias to a clip inside an imported animation artifact.</summary>
/// <param name="Alias">Unique script-facing clip key.</param>
/// <param name="Source">Skinned-mesh or skeletal-animation artifact reference.</param>
/// <param name="Clip">Exact imported clip name, or null for the first clip.</param>
/// <param name="InPlace">Whether horizontal root motion is removed during binding.</param>
/// <param name="RootMotionJoint">Explicit translated joint, or null for common-name detection.</param>
/// <param name="Speed">Default signed playback-rate multiplier.</param>
/// <param name="Loop">Whether playback wraps by default.</param>
public readonly record struct AnimationSetEntry(
    string Alias,
    AssetReference Source,
    string? Clip = null,
    bool InPlace = false,
    string? RootMotionJoint = null,
    float Speed = 1f,
    bool Loop = true);

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
            if (entry.RootMotionJoint is not null &&
                string.IsNullOrWhiteSpace(entry.RootMotionJoint))
                throw new ArgumentException("Root-motion joint names cannot be empty.",
                    nameof(entries));
            if (!entry.InPlace && entry.RootMotionJoint is not null)
                throw new ArgumentException(
                    "A root-motion joint requires in-place processing.", nameof(entries));
            if (!float.IsFinite(entry.Speed))
                throw new ArgumentException("Animation playback speed must be finite.",
                    nameof(entries));
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
        => BindTo(target, resolver, Matrix4x4.Identity);

    /// <summary>Resolves and aliases clips using the mesh transform for rendered-space in-place baking.</summary>
    /// <param name="target">Target skinned-mesh skeleton.</param>
    /// <param name="resolver">Resolver for explicit source animation artifacts.</param>
    /// <param name="meshNodeTransform">Transform applied after skin deformation.</param>
    /// <returns>Clips aligned to the target skeleton and named by stable aliases.</returns>
    public AnimationClipResource[] BindTo(SkeletonResource target,
        Func<AssetReference, SkeletalAnimationResource?> resolver,
        Matrix4x4 meshNodeTransform)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resolver);
        if (!Matrix4x4.Invert(meshNodeTransform, out _))
            throw new ArgumentException("Mesh-node transform must be invertible.",
                nameof(meshNodeTransform));
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
            result[index] = entry.InPlace
                ? CreateInPlaceClip(entry, selected, target, meshNodeTransform)
                : new AnimationClipResource(
                    entry.Alias, selected.Duration, selected.Tracks.ToArray(),
                    entry.Speed, entry.Loop);
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
        writer.Write(3u);
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
            writer.Write(entry.InPlace);
            writer.Write(entry.RootMotionJoint is not null);
            if (entry.RootMotionJoint is not null)
                writer.Write(entry.RootMotionJoint!);
            writer.Write(entry.Speed);
            writer.Write(entry.Loop);
        }
    }

    /// <summary>Writes the human-readable project-source representation.</summary>
    /// <param name="stream">Writable source stream.</param>
    public void SaveJson(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("version", 3);
        writer.WriteStartArray("entries");
        for (var index = 0; index < _entries.Length; index++)
        {
            var entry = _entries[index];
            writer.WriteStartObject();
            writer.WriteString("alias", entry.Alias);
            writer.WriteStartObject("source");
            writer.WriteString("asset", entry.Source.Asset.Value);
            writer.WriteString("subAsset", entry.Source.SubAsset);
            writer.WriteEndObject();
            if (entry.Clip is not null)
                writer.WriteString("clip", entry.Clip);
            if (entry.InPlace)
                writer.WriteBoolean("inPlace", true);
            if (entry.RootMotionJoint is not null)
                writer.WriteString("rootMotionJoint", entry.RootMotionJoint);
            if (entry.Speed != 1f)
                writer.WriteNumber("speed", entry.Speed);
            if (!entry.Loop)
                writer.WriteBoolean("loop", false);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>Reads one versioned Nico animation-set artifact.</summary>
    /// <param name="stream">Readable artifact stream.</param>
    /// <returns>The decoded immutable animation set.</returns>
    public static AnimationSetResource Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (IsJson(stream))
            return LoadJson(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != Magic)
            throw new InvalidDataException("Animation-set artifact has an invalid signature.");
        var version = reader.ReadUInt32();
        if (version is not 1u and not 2u and not 3u)
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
            var clip = reader.ReadBoolean() ? reader.ReadString() : null;
            var inPlace = version >= 2u && reader.ReadBoolean();
            var rootMotionJoint = version >= 2u && reader.ReadBoolean()
                ? reader.ReadString() : null;
            var speed = version >= 3u ? reader.ReadSingle() : 1f;
            var loop = version < 3u || reader.ReadBoolean();
            entries[index] = new AnimationSetEntry(
                alias, source, clip, inPlace, rootMotionJoint, speed, loop);
        }
        if (stream.CanSeek && stream.Position != stream.Length)
            throw new InvalidDataException("Animation-set artifact contains trailing data.");
        return new AnimationSetResource(entries);
    }

    /// <summary>Detects the human-readable project source representation.</summary>
    /// <param name="stream">Seekable source stream.</param>
    /// <returns>True when the first non-whitespace byte begins a JSON object.</returns>
    private static bool IsJson(Stream stream)
    {
        if (!stream.CanSeek)
            return false;
        var start = stream.Position;
        int value;
        do
        {
            value = stream.ReadByte();
        }
        while (value >= 0 && char.IsWhiteSpace((char)value));
        stream.Position = start;
        return value == '{';
    }

    /// <summary>Reads a versioned human-readable animation-set source.</summary>
    /// <param name="stream">Readable JSON source stream.</param>
    /// <returns>The decoded immutable animation set.</returns>
    private static AnimationSetResource LoadJson(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version) ||
            version.GetInt32() is not 1 and not 2 and not 3)
            throw new InvalidDataException("Animation-set JSON version is unsupported.");
        if (!root.TryGetProperty("entries", out var entriesElement) ||
            entriesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Animation-set JSON requires an entries array.");
        var entries = new AnimationSetEntry[entriesElement.GetArrayLength()];
        var index = 0;
        foreach (var element in entriesElement.EnumerateArray())
        {
            var alias = element.GetProperty("alias").GetString()
                ?? throw new InvalidDataException("Animation alias is missing.");
            var source = element.GetProperty("source");
            var assetText = source.GetProperty("asset").GetString();
            var subAsset = source.GetProperty("subAsset").GetString();
            if (!Guid.TryParse(assetText, out var asset) || string.IsNullOrWhiteSpace(subAsset))
                throw new InvalidDataException("Animation source reference is invalid.");
            var clip = element.TryGetProperty("clip", out var clipElement) &&
                clipElement.ValueKind != JsonValueKind.Null ? clipElement.GetString() : null;
            var inPlace = element.TryGetProperty("inPlace", out var inPlaceElement) &&
                inPlaceElement.GetBoolean();
            var rootMotionJoint = element.TryGetProperty("rootMotionJoint",
                    out var rootMotionJointElement) &&
                rootMotionJointElement.ValueKind != JsonValueKind.Null
                ? rootMotionJointElement.GetString() : null;
            var speed = element.TryGetProperty("speed", out var speedElement)
                ? speedElement.GetSingle() : 1f;
            var loop = !element.TryGetProperty("loop", out var loopElement) ||
                loopElement.GetBoolean();
            entries[index++] = new AnimationSetEntry(alias,
                new AssetReference(new AssetId(asset), subAsset), clip,
                inPlace, rootMotionJoint, speed, loop);
        }
        return new AnimationSetResource(entries);
    }

    /// <summary>Creates an aliased clip with rendered horizontal translation fixed at its first key.</summary>
    /// <param name="entry">Authored in-place settings.</param>
    /// <param name="source">Skeleton-bound source clip.</param>
    /// <param name="target">Target skeleton used to resolve the translated joint.</param>
    /// <param name="meshNodeTransform">Transform applied after skin deformation.</param>
    /// <returns>A clip retaining vertical and rotational motion without horizontal travel.</returns>
    private static AnimationClipResource CreateInPlaceClip(AnimationSetEntry entry,
        AnimationClipResource source, SkeletonResource target,
        Matrix4x4 meshNodeTransform)
    {
        var jointIndex = ResolveRootMotionJoint(entry.RootMotionJoint, source, target);
        var sourceTrack = source.Tracks[jointIndex]!;
        var translation = sourceTrack.Translation!;
        var values = translation.Values.ToArray();
        var pose = new SkeletonPose(target);
        pose.Evaluate(target, source, translation.Times[0]);
        var anchor = Vector3.Transform(
            pose.WorldTransforms[jointIndex].Translation, meshNodeTransform);
        if (!Matrix4x4.Invert(meshNodeTransform, out var inverseMeshTransform))
            throw new InvalidDataException("In-place mesh-node transform is not invertible.");
        var parentIndex = target.Joints[jointIndex].ParentIndex;
        for (var index = 0; index < values.Length; index++)
        {
            pose.Evaluate(target, source, translation.Times[index]);
            var currentRendered = Vector3.Transform(
                pose.WorldTransforms[jointIndex].Translation, meshNodeTransform);
            var desiredRendered = new Vector3(anchor.X, currentRendered.Y, anchor.Z);
            var desiredWorld = Vector3.Transform(desiredRendered, inverseMeshTransform);
            if (parentIndex < 0)
            {
                values[index] = desiredWorld;
                continue;
            }
            if (!Matrix4x4.Invert(pose.WorldTransforms[parentIndex], out var inverseParent))
            {
                throw new InvalidDataException(
                    $"In-place parent of joint '{target.Joints[jointIndex].Name}' " +
                    "has a non-invertible animated transform.");
            }
            values[index] = Vector3.Transform(desiredWorld, inverseParent);
        }
        var tracks = source.Tracks.ToArray();
        tracks[jointIndex] = new JointAnimationTrack(
            new Vector3AnimationTrack(translation.Times, values, translation.Interpolation),
            sourceTrack.Rotation, sourceTrack.Scale);
        return new AnimationClipResource(
            entry.Alias, source.Duration, tracks, entry.Speed, entry.Loop);
    }

    /// <summary>Resolves an explicit or conventional root-motion translation track.</summary>
    /// <param name="explicitName">Optional exact target-joint name.</param>
    /// <param name="clip">Bound clip whose translation tracks are inspected.</param>
    /// <param name="target">Target skeleton containing joint names.</param>
    /// <returns>The joint index whose horizontal translation should be removed.</returns>
    private static int ResolveRootMotionJoint(string? explicitName,
        AnimationClipResource clip, SkeletonResource target)
    {
        if (explicitName is not null)
        {
            var explicitIndex = target.FindJoint(explicitName);
            if (explicitIndex < 0)
                throw new InvalidDataException(
                    $"In-place root-motion joint '{explicitName}' is missing.");
            if (clip.Tracks[explicitIndex]?.Translation is null)
                throw new InvalidDataException(
                    $"In-place root-motion joint '{explicitName}' has no translation track.");
            return explicitIndex;
        }
        for (var index = 0; index < target.JointCount; index++)
        {
            var name = target.Joints[index].Name;
            if (clip.Tracks[index]?.Translation is not null &&
                (string.Equals(name, "Hips", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(":Hips", StringComparison.OrdinalIgnoreCase)))
                return index;
        }
        for (var index = 0; index < target.JointCount; index++)
        {
            if (target.Joints[index].ParentIndex < 0 &&
                clip.Tracks[index]?.Translation is not null)
                return index;
        }
        throw new InvalidDataException(
            "In-place animation has no conventional root translation track; " +
            "specify rootMotionJoint explicitly.");
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
