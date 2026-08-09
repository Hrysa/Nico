using System.Text;

namespace Engine.Graphics;

/// <summary>Contains animation clips authored against a named source skeleton.</summary>
public sealed class SkeletalAnimationResource
{
    private const string Magic = "NANIM001";
    private readonly AnimationClipResource[] _animations;

    /// <summary>Gets the source skeleton used by the imported clips.</summary>
    public SkeletonResource Skeleton { get; }

    /// <summary>Gets clips aligned to <see cref="Skeleton"/>.</summary>
    public IReadOnlyList<AnimationClipResource> Animations => _animations;

    /// <summary>Creates a validated standalone skeletal-animation resource.</summary>
    /// <param name="skeleton">Source skeleton.</param>
    /// <param name="animations">Clips aligned to the source skeleton.</param>
    public SkeletalAnimationResource(
        SkeletonResource skeleton,
        AnimationClipResource[] animations)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(animations);
        for (var index = 0; index < animations.Length; index++)
        {
            if (animations[index].Tracks.Count != skeleton.JointCount)
            {
                throw new ArgumentException(
                    "Animation tracks must align to source skeleton joints.",
                    nameof(animations));
            }
        }
        Skeleton = skeleton;
        _animations = animations.ToArray();
    }

    /// <summary>Writes one versioned standalone skeletal-animation artifact.</summary>
    /// <param name="stream">Writable artifact stream.</param>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("NANIM001"u8);
            writer.Write(1u);
            writer.Flush();
        }
        var container = new SkinnedMeshResource(
            new StaticMeshResource([], [], []), [], Skeleton, _animations);
        container.Save(stream);
    }

    /// <summary>Reads one versioned standalone skeletal-animation artifact.</summary>
    /// <param name="stream">Readable artifact stream.</param>
    /// <returns>The decoded resource.</returns>
    public static SkeletalAnimationResource Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != Magic)
            throw new InvalidDataException("Skeletal animation artifact has an invalid signature.");
        if (reader.ReadUInt32() != 1u)
            throw new InvalidDataException("Skeletal animation artifact version is unsupported.");
        var container = SkinnedMeshResource.Load(stream);
        return new SkeletalAnimationResource(
            container.Skeleton, container.Animations.ToArray());
    }

    /// <summary>Remaps source animation tracks to a compatible target skeleton by joint name.</summary>
    /// <param name="target">Target skinned-mesh skeleton.</param>
    /// <returns>Clips whose tracks are aligned to the target skeleton.</returns>
    public AnimationClipResource[] BindTo(SkeletonResource target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var sourceIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < Skeleton.JointCount; index++)
        {
            if (!sourceIndices.TryAdd(Skeleton.Joints[index].Name, index))
            {
                throw new InvalidOperationException(
                    $"Source skeleton contains duplicate joint '{Skeleton.Joints[index].Name}'.");
            }
        }
        var targetToSource = new int[target.JointCount];
        for (var targetIndex = 0; targetIndex < target.JointCount; targetIndex++)
        {
            var targetJoint = target.Joints[targetIndex];
            if (!sourceIndices.TryGetValue(targetJoint.Name, out var sourceIndex))
            {
                throw new InvalidOperationException(
                    $"Animation skeleton is missing target joint '{targetJoint.Name}'.");
            }
            var sourceJoint = Skeleton.Joints[sourceIndex];
            var sourceParentName = sourceJoint.ParentIndex < 0
                ? null : Skeleton.Joints[sourceJoint.ParentIndex].Name;
            var targetParentName = targetJoint.ParentIndex < 0
                ? null : target.Joints[targetJoint.ParentIndex].Name;
            if (!string.Equals(sourceParentName, targetParentName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Animation joint '{targetJoint.Name}' has an incompatible parent.");
            }
            targetToSource[targetIndex] = sourceIndex;
        }
        var result = new AnimationClipResource[_animations.Length];
        for (var animationIndex = 0; animationIndex < _animations.Length; animationIndex++)
        {
            var source = _animations[animationIndex];
            var tracks = new JointAnimationTrack?[target.JointCount];
            for (var targetIndex = 0; targetIndex < tracks.Length; targetIndex++)
                tracks[targetIndex] = source.Tracks[targetToSource[targetIndex]];
            result[animationIndex] = new AnimationClipResource(
                source.Name, source.Duration, tracks);
        }
        return result;
    }
}
