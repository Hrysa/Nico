using System.Numerics;
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

    /// <summary>Gets the source skeleton-space to rendered-space transform.</summary>
    public Matrix4x4 SkeletonTransform { get; }

    /// <summary>Creates a validated standalone skeletal-animation resource.</summary>
    /// <param name="skeleton">Source skeleton.</param>
    /// <param name="animations">Clips aligned to the source skeleton.</param>
    public SkeletalAnimationResource(
        SkeletonResource skeleton,
        AnimationClipResource[] animations)
        : this(skeleton, animations, Matrix4x4.Identity)
    {
    }

    /// <summary>Creates a standalone animation with its authored skeleton basis.</summary>
    /// <param name="skeleton">Source skeleton.</param>
    /// <param name="animations">Clips aligned to the source skeleton.</param>
    /// <param name="skeletonTransform">Skeleton-space to rendered-space transform.</param>
    public SkeletalAnimationResource(SkeletonResource skeleton,
        AnimationClipResource[] animations, Matrix4x4 skeletonTransform)
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
        SkeletonTransform = skeletonTransform;
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
            new StaticMeshResource([], [], []), [], Skeleton, _animations,
            SkeletonTransform);
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
            container.Skeleton, container.Animations.ToArray(),
            container.MeshNodeTransform);
    }

    /// <summary>Remaps source animation tracks to a compatible target skeleton by joint name.</summary>
    /// <param name="target">Target skinned-mesh skeleton.</param>
    /// <returns>Clips whose tracks are aligned to the target skeleton.</returns>
    public AnimationClipResource[] BindTo(SkeletonResource target) =>
        BindTo(target, AnimationRetargetMode.Auto);

    /// <summary>Binds clips by exact hierarchy or through a detected humanoid avatar.</summary>
    /// <param name="target">Target skinned-mesh skeleton.</param>
    /// <param name="mode">Requested skeleton binding strategy.</param>
    /// <returns>Clips whose tracks are aligned to the target skeleton.</returns>
    public AnimationClipResource[] BindTo(
        SkeletonResource target, AnimationRetargetMode mode)
        => BindTo(target, mode, Matrix4x4.Identity);

    /// <summary>Binds clips using the destination skeleton's rendered-space basis.</summary>
    /// <param name="target">Target skinned-mesh skeleton.</param>
    /// <param name="mode">Requested skeleton binding strategy.</param>
    /// <param name="targetSkeletonTransform">Target skeleton-space to rendered-space transform.</param>
    /// <returns>Clips whose tracks are aligned to the target skeleton.</returns>
    public AnimationClipResource[] BindTo(SkeletonResource target,
        AnimationRetargetMode mode, Matrix4x4 targetSkeletonTransform)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (mode == AnimationRetargetMode.Humanoid)
        {
            if (HasCompatibleHierarchy(target) &&
                NearlyEqual(SkeletonTransform, targetSkeletonTransform))
            {
                return BindExact(target);
            }
            return HumanoidAnimationRetargeter.Retarget(
                this, target, targetSkeletonTransform);
        }
        if (mode == AnimationRetargetMode.Exact)
            return BindExact(target);
        if (HasCompatibleHierarchy(target) &&
            NearlyEqual(SkeletonTransform, targetSkeletonTransform))
        {
            return BindExact(target);
        }
        if ((!HasEquivalentBindPose(target) ||
             !NearlyEqual(SkeletonTransform, targetSkeletonTransform)) &&
            HumanoidRig.TryDetect(Skeleton, out _) &&
            HumanoidRig.TryDetect(target, out _))
        {
            return HumanoidAnimationRetargeter.Retarget(
                this, target, targetSkeletonTransform);
        }
        try
        {
            return BindExact(target);
        }
        catch (InvalidOperationException exactException)
        {
            try
            {
                return HumanoidAnimationRetargeter.Retarget(
                    this, target, targetSkeletonTransform);
            }
            catch (InvalidOperationException humanoidException)
            {
                throw new InvalidOperationException(
                    $"Exact animation binding failed: {exactException.Message} " +
                    $"Humanoid retargeting failed: {humanoidException.Message}",
                    humanoidException);
            }
        }
    }

    /// <summary>Checks whether name binding also preserves the source reference pose.</summary>
    /// <param name="target">Potential exact-binding destination.</param>
    /// <returns>True when every target joint has the same named parent and local bind pose.</returns>
    private bool HasEquivalentBindPose(SkeletonResource target)
    {
        if (!HasCompatibleHierarchy(target))
            return false;
        for (var index = 0; index < target.JointCount; index++)
        {
            var targetJoint = target.Joints[index];
            var sourceIndex = Skeleton.FindJoint(targetJoint.Name);
            var sourceJoint = Skeleton.Joints[sourceIndex];
            if (!NearlyEqual(sourceJoint.BindTransform, targetJoint.BindTransform))
                return false;
        }
        return true;
    }

    /// <summary>Checks whether direct name binding covers the same complete hierarchy.</summary>
    /// <param name="target">Potential exact-binding destination.</param>
    /// <returns>True when names and named parent relationships match.</returns>
    private bool HasCompatibleHierarchy(SkeletonResource target)
    {
        if (target.JointCount != Skeleton.JointCount)
            return false;
        for (var index = 0; index < target.JointCount; index++)
        {
            var targetJoint = target.Joints[index];
            var sourceIndex = Skeleton.FindJoint(targetJoint.Name);
            if (sourceIndex < 0)
                return false;
            var sourceJoint = Skeleton.Joints[sourceIndex];
            var sourceParent = sourceJoint.ParentIndex < 0
                ? null : Skeleton.Joints[sourceJoint.ParentIndex].Name;
            var targetParent = targetJoint.ParentIndex < 0
                ? null : target.Joints[targetJoint.ParentIndex].Name;
            if (!string.Equals(sourceParent, targetParent, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>Compares two local reference transforms within import precision.</summary>
    /// <param name="left">First transform.</param>
    /// <param name="right">Second transform.</param>
    /// <returns>True when translation, orientation, and scale are equivalent.</returns>
    private static bool NearlyEqual(JointTransform left, JointTransform right) =>
        Vector3.DistanceSquared(left.Translation, right.Translation) <= 0.00000001f &&
        Vector3.DistanceSquared(left.Scale, right.Scale) <= 0.00000001f &&
        MathF.Abs(Quaternion.Dot(left.Rotation, right.Rotation)) >= 0.999999f;

    /// <summary>Compares two skeleton-to-rendered transforms within import precision.</summary>
    /// <param name="left">First transform.</param>
    /// <param name="right">Second transform.</param>
    /// <returns>True when every matrix component is equivalent.</returns>
    private static bool NearlyEqual(Matrix4x4 left, Matrix4x4 right) =>
        MathF.Abs(left.M11 - right.M11) <= 0.000001f &&
        MathF.Abs(left.M12 - right.M12) <= 0.000001f &&
        MathF.Abs(left.M13 - right.M13) <= 0.000001f &&
        MathF.Abs(left.M14 - right.M14) <= 0.000001f &&
        MathF.Abs(left.M21 - right.M21) <= 0.000001f &&
        MathF.Abs(left.M22 - right.M22) <= 0.000001f &&
        MathF.Abs(left.M23 - right.M23) <= 0.000001f &&
        MathF.Abs(left.M24 - right.M24) <= 0.000001f &&
        MathF.Abs(left.M31 - right.M31) <= 0.000001f &&
        MathF.Abs(left.M32 - right.M32) <= 0.000001f &&
        MathF.Abs(left.M33 - right.M33) <= 0.000001f &&
        MathF.Abs(left.M34 - right.M34) <= 0.000001f &&
        MathF.Abs(left.M41 - right.M41) <= 0.000001f &&
        MathF.Abs(left.M42 - right.M42) <= 0.000001f &&
        MathF.Abs(left.M43 - right.M43) <= 0.000001f &&
        MathF.Abs(left.M44 - right.M44) <= 0.000001f;

    /// <summary>Remaps source tracks using exact joint names and parent relationships.</summary>
    /// <param name="target">Target skinned-mesh skeleton.</param>
    /// <returns>Exactly bound clips.</returns>
    private AnimationClipResource[] BindExact(SkeletonResource target)
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
