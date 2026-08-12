using System.Numerics;

namespace Engine.Graphics;

/// <summary>Identifies the standardized bones shared by supported humanoid rigs.</summary>
public enum HumanoidBone
{
    Hips,
    Spine,
    Chest,
    UpperChest,
    Neck,
    Head,
    LeftShoulder,
    LeftUpperArm,
    LeftLowerArm,
    LeftHand,
    RightShoulder,
    RightUpperArm,
    RightLowerArm,
    RightHand,
    LeftUpperLeg,
    LeftLowerLeg,
    LeftFoot,
    LeftToes,
    RightUpperLeg,
    RightLowerLeg,
    RightFoot,
    RightToes,
    LeftThumbProximal,
    LeftThumbIntermediate,
    LeftThumbDistal,
    LeftIndexProximal,
    LeftIndexIntermediate,
    LeftIndexDistal,
    LeftMiddleProximal,
    LeftMiddleIntermediate,
    LeftMiddleDistal,
    LeftRingProximal,
    LeftRingIntermediate,
    LeftRingDistal,
    LeftLittleProximal,
    LeftLittleIntermediate,
    LeftLittleDistal,
    RightThumbProximal,
    RightThumbIntermediate,
    RightThumbDistal,
    RightIndexProximal,
    RightIndexIntermediate,
    RightIndexDistal,
    RightMiddleProximal,
    RightMiddleIntermediate,
    RightMiddleDistal,
    RightRingProximal,
    RightRingIntermediate,
    RightRingDistal,
    RightLittleProximal,
    RightLittleIntermediate,
    RightLittleDistal
}

/// <summary>Identifies a conservatively detected, widely used skeleton convention.</summary>
public enum HumanoidRigPreset
{
    Mixamo,
    Unreal
}

/// <summary>Controls how an animation source is bound to its destination skeleton.</summary>
public enum AnimationRetargetMode
{
    Auto,
    Exact,
    Humanoid
}

/// <summary>Stores one validated semantic humanoid mapping for an imported skeleton.</summary>
public sealed class HumanoidRig
{
    private const int BoneCount = (int)HumanoidBone.RightLittleDistal + 1;
    private readonly int[] _jointByBone;

    /// <summary>Gets the skeleton described by this mapping.</summary>
    public SkeletonResource Skeleton { get; }

    /// <summary>Gets the convention that supplied the mapping.</summary>
    public HumanoidRigPreset Preset { get; }

    /// <summary>Gets the optional source joint carrying model-space root motion.</summary>
    public int RootMotionJointIndex { get; }

    /// <summary>Creates one immutable humanoid mapping.</summary>
    /// <param name="skeleton">Mapped skeleton.</param>
    /// <param name="preset">Detected naming convention.</param>
    /// <param name="jointByBone">Joint index for every semantic bone, or -1.</param>
    /// <param name="rootMotionJointIndex">Optional root-motion joint index.</param>
    private HumanoidRig(SkeletonResource skeleton, HumanoidRigPreset preset,
        int[] jointByBone, int rootMotionJointIndex)
    {
        Skeleton = skeleton;
        Preset = preset;
        _jointByBone = jointByBone;
        RootMotionJointIndex = rootMotionJointIndex;
    }

    /// <summary>Returns the imported joint assigned to one semantic bone.</summary>
    /// <param name="bone">Semantic humanoid bone.</param>
    /// <returns>Joint index, or -1 when the optional bone is absent.</returns>
    public int GetJoint(HumanoidBone bone) => _jointByBone[(int)bone];

    /// <summary>Detects a supported rig only when its distinctive and required bones agree.</summary>
    /// <param name="skeleton">Imported skeleton to inspect.</param>
    /// <param name="rig">Detected mapping when successful.</param>
    /// <returns>True when a high-confidence supported convention was detected.</returns>
    public static bool TryDetect(SkeletonResource skeleton, out HumanoidRig? rig)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        if (skeleton.FindJoint("mixamorig:Hips") >= 0)
            return TryCreate(skeleton, HumanoidRigPreset.Mixamo, MixamoNames, out rig);
        if (skeleton.FindJoint("B_Pelvis") >= 0 || skeleton.FindJoint("pelvis") >= 0)
            return TryCreate(skeleton, HumanoidRigPreset.Unreal, UnrealNames, out rig);
        rig = null;
        return false;
    }

    /// <summary>Creates and validates a mapping from one convention table.</summary>
    /// <param name="skeleton">Imported skeleton.</param>
    /// <param name="preset">Convention represented by the table.</param>
    /// <param name="names">Candidate names indexed by semantic bone.</param>
    /// <param name="rig">Created mapping when all required bones exist.</param>
    /// <returns>True when required mapping and hierarchy validation succeeds.</returns>
    private static bool TryCreate(SkeletonResource skeleton, HumanoidRigPreset preset,
        string[]?[] names, out HumanoidRig? rig)
    {
        var joints = new int[BoneCount];
        Array.Fill(joints, -1);
        for (var boneIndex = 0; boneIndex < names.Length; boneIndex++)
        {
            var candidates = names[boneIndex];
            if (candidates is null)
                continue;
            for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                var joint = FindJointIgnoreCase(skeleton, candidates[candidateIndex]);
                if (joint < 0)
                    continue;
                joints[boneIndex] = joint;
                break;
            }
        }
        if (!HasRequiredBones(joints) || !HasRequiredHierarchy(skeleton, joints))
        {
            rig = null;
            return false;
        }
        var rootMotion = FindFirstJoint(skeleton,
            preset == HumanoidRigPreset.Mixamo
                ? ["Armature", "Root", "root"]
                : ["Motion", "root", "Root"]);
        var hips = joints[(int)HumanoidBone.Hips];
        if (rootMotion == hips || rootMotion >= 0 && !IsAncestor(skeleton, rootMotion, hips))
            rootMotion = -1;
        rig = new HumanoidRig(skeleton, preset, joints, rootMotion);
        return true;
    }

    /// <summary>Checks the minimum body mapping needed for safe retargeting.</summary>
    /// <param name="joints">Mapped joint indices.</param>
    /// <returns>True when the torso and all four limbs are complete.</returns>
    private static bool HasRequiredBones(int[] joints)
    {
        ReadOnlySpan<HumanoidBone> required =
        [
            HumanoidBone.Hips, HumanoidBone.Spine, HumanoidBone.Head,
            HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand,
            HumanoidBone.RightUpperArm, HumanoidBone.RightLowerArm, HumanoidBone.RightHand,
            HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot,
            HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot
        ];
        for (var index = 0; index < required.Length; index++)
        {
            if (joints[(int)required[index]] < 0)
                return false;
        }
        return true;
    }

    /// <summary>Rejects mappings whose major limbs do not descend from the expected body bone.</summary>
    /// <param name="skeleton">Mapped skeleton.</param>
    /// <param name="joints">Mapped joint indices.</param>
    /// <returns>True when the required ancestry is humanoid-compatible.</returns>
    private static bool HasRequiredHierarchy(SkeletonResource skeleton, int[] joints)
    {
        var hips = joints[(int)HumanoidBone.Hips];
        return IsAncestor(skeleton, hips, joints[(int)HumanoidBone.Head]) &&
               IsAncestor(skeleton, hips, joints[(int)HumanoidBone.LeftHand]) &&
               IsAncestor(skeleton, hips, joints[(int)HumanoidBone.RightHand]) &&
               IsAncestor(skeleton, hips, joints[(int)HumanoidBone.LeftFoot]) &&
               IsAncestor(skeleton, hips, joints[(int)HumanoidBone.RightFoot]);
    }

    /// <summary>Checks whether one joint occurs above another in the imported hierarchy.</summary>
    /// <param name="skeleton">Skeleton containing both joints.</param>
    /// <param name="ancestor">Candidate ancestor.</param>
    /// <param name="descendant">Candidate descendant.</param>
    /// <returns>True when the candidate is an ancestor.</returns>
    private static bool IsAncestor(SkeletonResource skeleton, int ancestor, int descendant)
    {
        for (var current = descendant; current >= 0;
             current = skeleton.Joints[current].ParentIndex)
        {
            if (current == ancestor)
                return true;
        }
        return false;
    }

    /// <summary>Finds a joint using an ordinal case-insensitive comparison.</summary>
    /// <param name="skeleton">Skeleton to search.</param>
    /// <param name="name">Convention name.</param>
    /// <returns>Joint index, or -1.</returns>
    private static int FindJointIgnoreCase(SkeletonResource skeleton, string name)
    {
        for (var index = 0; index < skeleton.JointCount; index++)
        {
            if (string.Equals(skeleton.Joints[index].Name, name,
                    StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return -1;
    }

    /// <summary>Finds the first present joint from an ordered candidate list.</summary>
    /// <param name="skeleton">Skeleton to search.</param>
    /// <param name="names">Candidate names in priority order.</param>
    /// <returns>Joint index, or -1.</returns>
    private static int FindFirstJoint(SkeletonResource skeleton, string[] names)
    {
        for (var index = 0; index < names.Length; index++)
        {
            var joint = FindJointIgnoreCase(skeleton, names[index]);
            if (joint >= 0)
                return joint;
        }
        return -1;
    }

    private static readonly string[]?[] MixamoNames = CreateNameTable(
    [
        (HumanoidBone.Hips, ["mixamorig:Hips"]),
        (HumanoidBone.Spine, ["mixamorig:Spine"]),
        (HumanoidBone.Chest, ["mixamorig:Spine1"]),
        (HumanoidBone.UpperChest, ["mixamorig:Spine2"]),
        (HumanoidBone.Neck, ["mixamorig:Neck"]),
        (HumanoidBone.Head, ["mixamorig:Head"]),
        (HumanoidBone.LeftShoulder, ["mixamorig:LeftShoulder"]),
        (HumanoidBone.LeftUpperArm, ["mixamorig:LeftArm"]),
        (HumanoidBone.LeftLowerArm, ["mixamorig:LeftForeArm"]),
        (HumanoidBone.LeftHand, ["mixamorig:LeftHand"]),
        (HumanoidBone.RightShoulder, ["mixamorig:RightShoulder"]),
        (HumanoidBone.RightUpperArm, ["mixamorig:RightArm"]),
        (HumanoidBone.RightLowerArm, ["mixamorig:RightForeArm"]),
        (HumanoidBone.RightHand, ["mixamorig:RightHand"]),
        (HumanoidBone.LeftUpperLeg, ["mixamorig:LeftUpLeg"]),
        (HumanoidBone.LeftLowerLeg, ["mixamorig:LeftLeg"]),
        (HumanoidBone.LeftFoot, ["mixamorig:LeftFoot"]),
        (HumanoidBone.LeftToes, ["mixamorig:LeftToeBase"]),
        (HumanoidBone.RightUpperLeg, ["mixamorig:RightUpLeg"]),
        (HumanoidBone.RightLowerLeg, ["mixamorig:RightLeg"]),
        (HumanoidBone.RightFoot, ["mixamorig:RightFoot"]),
        (HumanoidBone.RightToes, ["mixamorig:RightToeBase"]),
        (HumanoidBone.LeftThumbProximal, ["mixamorig:LeftHandThumb1"]),
        (HumanoidBone.LeftThumbIntermediate, ["mixamorig:LeftHandThumb2"]),
        (HumanoidBone.LeftThumbDistal, ["mixamorig:LeftHandThumb3"]),
        (HumanoidBone.LeftIndexProximal, ["mixamorig:LeftHandIndex1"]),
        (HumanoidBone.LeftIndexIntermediate, ["mixamorig:LeftHandIndex2"]),
        (HumanoidBone.LeftIndexDistal, ["mixamorig:LeftHandIndex3"]),
        (HumanoidBone.LeftMiddleProximal, ["mixamorig:LeftHandMiddle1"]),
        (HumanoidBone.LeftMiddleIntermediate, ["mixamorig:LeftHandMiddle2"]),
        (HumanoidBone.LeftMiddleDistal, ["mixamorig:LeftHandMiddle3"]),
        (HumanoidBone.LeftRingProximal, ["mixamorig:LeftHandRing1"]),
        (HumanoidBone.LeftRingIntermediate, ["mixamorig:LeftHandRing2"]),
        (HumanoidBone.LeftRingDistal, ["mixamorig:LeftHandRing3"]),
        (HumanoidBone.LeftLittleProximal, ["mixamorig:LeftHandPinky1"]),
        (HumanoidBone.LeftLittleIntermediate, ["mixamorig:LeftHandPinky2"]),
        (HumanoidBone.LeftLittleDistal, ["mixamorig:LeftHandPinky3"]),
        (HumanoidBone.RightThumbProximal, ["mixamorig:RightHandThumb1"]),
        (HumanoidBone.RightThumbIntermediate, ["mixamorig:RightHandThumb2"]),
        (HumanoidBone.RightThumbDistal, ["mixamorig:RightHandThumb3"]),
        (HumanoidBone.RightIndexProximal, ["mixamorig:RightHandIndex1"]),
        (HumanoidBone.RightIndexIntermediate, ["mixamorig:RightHandIndex2"]),
        (HumanoidBone.RightIndexDistal, ["mixamorig:RightHandIndex3"]),
        (HumanoidBone.RightMiddleProximal, ["mixamorig:RightHandMiddle1"]),
        (HumanoidBone.RightMiddleIntermediate, ["mixamorig:RightHandMiddle2"]),
        (HumanoidBone.RightMiddleDistal, ["mixamorig:RightHandMiddle3"]),
        (HumanoidBone.RightRingProximal, ["mixamorig:RightHandRing1"]),
        (HumanoidBone.RightRingIntermediate, ["mixamorig:RightHandRing2"]),
        (HumanoidBone.RightRingDistal, ["mixamorig:RightHandRing3"]),
        (HumanoidBone.RightLittleProximal, ["mixamorig:RightHandPinky1"]),
        (HumanoidBone.RightLittleIntermediate, ["mixamorig:RightHandPinky2"]),
        (HumanoidBone.RightLittleDistal, ["mixamorig:RightHandPinky3"])
    ]);

    private static readonly string[]?[] UnrealNames = CreateNameTable(
    [
        (HumanoidBone.Hips, ["B_Pelvis", "pelvis"]),
        (HumanoidBone.Spine, ["B_Spine", "spine_01"]),
        (HumanoidBone.Chest, ["B_Spine1", "spine_02"]),
        (HumanoidBone.UpperChest, ["B_Spine2", "spine_03"]),
        (HumanoidBone.Neck, ["B_Neck", "neck_01"]),
        (HumanoidBone.Head, ["B_Head", "head"]),
        (HumanoidBone.LeftShoulder, ["B_L_Clavicle", "clavicle_l"]),
        (HumanoidBone.LeftUpperArm, ["B_L_UpperArm", "upperarm_l"]),
        (HumanoidBone.LeftLowerArm, ["B_L_Forearm", "lowerarm_l"]),
        (HumanoidBone.LeftHand, ["B_L_Hand", "hand_l"]),
        (HumanoidBone.RightShoulder, ["B_R_Clavicle", "clavicle_r"]),
        (HumanoidBone.RightUpperArm, ["B_R_UpperArm", "upperarm_r"]),
        (HumanoidBone.RightLowerArm, ["B_R_Forearm", "lowerarm_r"]),
        (HumanoidBone.RightHand, ["B_R_Hand", "hand_r"]),
        (HumanoidBone.LeftUpperLeg, ["B_L_Thigh", "thigh_l"]),
        (HumanoidBone.LeftLowerLeg, ["B_L_Calf", "calf_l"]),
        (HumanoidBone.LeftFoot, ["B_L_Foot", "foot_l"]),
        (HumanoidBone.LeftToes, ["B_L_Toe0", "ball_l"]),
        (HumanoidBone.RightUpperLeg, ["B_R_Thigh", "thigh_r"]),
        (HumanoidBone.RightLowerLeg, ["B_R_Calf", "calf_r"]),
        (HumanoidBone.RightFoot, ["B_R_Foot", "foot_r"]),
        (HumanoidBone.RightToes, ["B_R_Toe0", "ball_r"]),
        (HumanoidBone.LeftThumbProximal, ["B_L_Finger0", "thumb_01_l"]),
        (HumanoidBone.LeftThumbIntermediate, ["B_L_Finger01", "thumb_02_l"]),
        (HumanoidBone.LeftThumbDistal, ["B_L_Finger02", "thumb_03_l"]),
        (HumanoidBone.LeftIndexProximal, ["B_L_Finger1", "index_01_l"]),
        (HumanoidBone.LeftIndexIntermediate, ["B_L_Finger11", "index_02_l"]),
        (HumanoidBone.LeftIndexDistal, ["B_L_Finger12", "index_03_l"]),
        (HumanoidBone.LeftMiddleProximal, ["B_L_Finger2", "middle_01_l"]),
        (HumanoidBone.LeftMiddleIntermediate, ["B_L_Finger21", "middle_02_l"]),
        (HumanoidBone.LeftMiddleDistal, ["B_L_Finger22", "middle_03_l"]),
        (HumanoidBone.LeftRingProximal, ["B_L_Finger3", "ring_01_l"]),
        (HumanoidBone.LeftRingIntermediate, ["B_L_Finger31", "ring_02_l"]),
        (HumanoidBone.LeftRingDistal, ["B_L_Finger32", "ring_03_l"]),
        (HumanoidBone.LeftLittleProximal, ["B_L_Finger4", "pinky_01_l"]),
        (HumanoidBone.LeftLittleIntermediate, ["B_L_Finger41", "pinky_02_l"]),
        (HumanoidBone.LeftLittleDistal, ["B_L_Finger42", "pinky_03_l"]),
        (HumanoidBone.RightThumbProximal,
            ["B_R_Finger0", "thumb_01_r"]),
        (HumanoidBone.RightThumbIntermediate,
            ["B_R_Finger0.001", "thumb_02_r"]),
        (HumanoidBone.RightThumbDistal,
            ["B_R_Finger0.002", "thumb_03_r"]),
        (HumanoidBone.RightIndexProximal, ["B_R_Finger1", "index_01_r"]),
        (HumanoidBone.RightIndexIntermediate, ["B_R_Finger11", "index_02_r"]),
        (HumanoidBone.RightIndexDistal, ["B_R_Finger12", "index_03_r"]),
        (HumanoidBone.RightMiddleProximal, ["B_R_Finger2", "middle_01_r"]),
        (HumanoidBone.RightMiddleIntermediate, ["B_R_Finger21", "middle_02_r"]),
        (HumanoidBone.RightMiddleDistal, ["B_R_Finger22", "middle_03_r"]),
        (HumanoidBone.RightRingProximal, ["B_R_Finger3", "ring_01_r"]),
        (HumanoidBone.RightRingIntermediate, ["B_R_Finger31", "ring_02_r"]),
        (HumanoidBone.RightRingDistal, ["B_R_Finger32", "ring_03_r"]),
        (HumanoidBone.RightLittleProximal, ["B_R_Finger4", "pinky_01_r"]),
        (HumanoidBone.RightLittleIntermediate, ["B_R_Finger41", "pinky_02_r"]),
        (HumanoidBone.RightLittleDistal, ["B_R_Finger42", "pinky_03_r"])
    ]);

    /// <summary>Expands sparse semantic-name pairs into an enum-indexed lookup table.</summary>
    /// <param name="entries">Sparse convention entries.</param>
    /// <returns>Name candidates indexed by semantic bone.</returns>
    private static string[]?[] CreateNameTable((HumanoidBone Bone, string[] Names)[] entries)
    {
        var table = new string[]?[BoneCount];
        for (var index = 0; index < entries.Length; index++)
            table[(int)entries[index].Bone] = entries[index].Names;
        return table;
    }
}

/// <summary>Retargets humanoid animation through semantic bones and reference poses.</summary>
public static class HumanoidAnimationRetargeter
{
    /// <summary>Retargets every clip from one detected humanoid rig to another.</summary>
    /// <param name="source">Source animation and skeleton.</param>
    /// <param name="target">Destination skeleton.</param>
    /// <param name="targetSkeletonTransform">Destination skeleton-to-rendered transform.</param>
    /// <returns>Clips aligned to the destination skeleton.</returns>
    public static AnimationClipResource[] Retarget(
        SkeletalAnimationResource source, SkeletonResource target,
        Matrix4x4 targetSkeletonTransform)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!HumanoidRig.TryDetect(source.Skeleton, out var sourceRig) || sourceRig is null)
            throw new InvalidOperationException(
                "The animation source is not a recognized humanoid skeleton.");
        if (!HumanoidRig.TryDetect(target, out var targetRig) || targetRig is null)
            throw new InvalidOperationException(
                "The animation target is not a recognized humanoid skeleton.");
        var result = new AnimationClipResource[source.Animations.Count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = RetargetClip(source.Animations[index], sourceRig, targetRig,
                source.SkeletonTransform, targetSkeletonTransform);
        }
        return result;
    }

    /// <summary>Retargets one clip while preserving target proportions and optional bones.</summary>
    /// <param name="clip">Source clip.</param>
    /// <param name="sourceRig">Detected source rig.</param>
    /// <param name="targetRig">Detected destination rig.</param>
    /// <param name="sourceSkeletonTransform">Source skeleton-to-rendered transform.</param>
    /// <param name="targetSkeletonTransform">Destination skeleton-to-rendered transform.</param>
    /// <returns>Destination-skeleton clip.</returns>
    private static AnimationClipResource RetargetClip(AnimationClipResource clip,
        HumanoidRig sourceRig, HumanoidRig targetRig,
        Matrix4x4 sourceSkeletonTransform, Matrix4x4 targetSkeletonTransform)
    {
        var sourceSkeleton = sourceRig.Skeleton;
        var targetSkeleton = targetRig.Skeleton;
        var sourceBindWorld = BuildBindWorldTransforms(
            sourceSkeleton, sourceSkeletonTransform);
        var targetBindWorld = BuildBindWorldTransforms(
            targetSkeleton, targetSkeletonTransform);
        var sourceByTarget = BuildSourceByTarget(sourceRig, targetRig);
        var sourceLocals = new JointTransform[sourceSkeleton.JointCount];
        var sourceWorld = new Matrix4x4[sourceSkeleton.JointCount];
        var targetLocals = new JointTransform[targetSkeleton.JointCount];
        var targetWorld = new Matrix4x4[targetSkeleton.JointCount];
        var scale = CalculateHeightScale(sourceRig, targetRig,
            sourceBindWorld, targetBindWorld);
        var tracks = new JointAnimationTrack?[targetSkeleton.JointCount];
        var hipsTarget = targetRig.GetJoint(HumanoidBone.Hips);
        var sampleTimes = CollectRetargetTimes(clip, sourceRig, sourceByTarget);
        var sampledLocals = new JointTransform[sampleTimes.Length][];
        for (var sample = 0; sample < sampleTimes.Length; sample++)
        {
            EvaluateAt(sampleTimes[sample], clip, sourceRig, targetRig,
                sourceByTarget, sourceBindWorld, targetBindWorld, scale,
                sourceSkeletonTransform, targetSkeletonTransform,
                sourceLocals, sourceWorld, targetLocals, targetWorld);
            sampledLocals[sample] = new JointTransform[targetLocals.Length];
            targetLocals.CopyTo(sampledLocals[sample], 0);
        }
        for (var targetIndex = 0; targetIndex < targetSkeleton.JointCount; targetIndex++)
        {
            var sourceIndex = sourceByTarget[targetIndex];
            if (sourceIndex < 0)
                continue;
            var sourceTrack = clip.Tracks[sourceIndex];
            QuaternionAnimationTrack? rotation = null;
            if (sourceTrack?.Rotation is { } sourceRotation)
            {
                var values = new Quaternion[sourceRotation.Times.Length];
                for (var key = 0; key < values.Length; key++)
                {
                    var sample = Array.BinarySearch(sampleTimes, sourceRotation.Times[key]);
                    values[key] = sampledLocals[sample][targetIndex].Rotation;
                }
                rotation = new QuaternionAnimationTrack(
                    sourceRotation.Times, values, sourceRotation.Interpolation);
            }
            Vector3AnimationTrack? translation = null;
            if (targetIndex == hipsTarget)
            {
                var times = CollectRootTranslationTimes(clip, sourceRig);
                if (times.Length > 0)
                {
                    var values = new Vector3[times.Length];
                    for (var key = 0; key < values.Length; key++)
                    {
                        var sample = Array.BinarySearch(sampleTimes, times[key]);
                        values[key] = sampledLocals[sample][targetIndex].Translation;
                    }
                    translation = new Vector3AnimationTrack(
                        times, values, AnimationInterpolation.Linear);
                }
            }
            if (rotation is not null || translation is not null)
                tracks[targetIndex] = new JointAnimationTrack(translation, rotation, null);
        }
        return new AnimationClipResource(clip.Name, clip.Duration, tracks,
            clip.DefaultSpeed, clip.DefaultLoop);
    }

    /// <summary>Collects every unique time required by mapped rotation and root tracks.</summary>
    /// <param name="clip">Source clip.</param>
    /// <param name="rig">Source humanoid mapping.</param>
    /// <param name="sourceByTarget">Source joint mapped to each target joint.</param>
    /// <returns>Sorted unique pose-sampling times.</returns>
    private static float[] CollectRetargetTimes(AnimationClipResource clip,
        HumanoidRig rig, int[] sourceByTarget)
    {
        var times = new SortedSet<float>();
        for (var targetIndex = 0; targetIndex < sourceByTarget.Length; targetIndex++)
        {
            var sourceIndex = sourceByTarget[targetIndex];
            if (sourceIndex < 0 ||
                clip.Tracks[sourceIndex]?.Rotation is not { } rotation)
                continue;
            for (var key = 0; key < rotation.Times.Length; key++)
                times.Add(rotation.Times[key]);
        }
        AddTranslationTimes(clip, rig.GetJoint(HumanoidBone.Hips), times);
        AddTranslationTimes(clip, rig.RootMotionJointIndex, times);
        if (times.Count == 0)
            times.Add(0f);
        var result = new float[times.Count];
        times.CopyTo(result);
        return result;
    }

    /// <summary>Evaluates one retargeted local pose at a clip time.</summary>
    /// <param name="time">Clip-local time.</param>
    /// <param name="clip">Source clip.</param>
    /// <param name="sourceRig">Source humanoid mapping.</param>
    /// <param name="targetRig">Target humanoid mapping.</param>
    /// <param name="sourceByTarget">Source joint mapped to each target joint.</param>
    /// <param name="sourceBindWorld">Source reference-pose world transforms.</param>
    /// <param name="targetBindWorld">Target reference-pose world transforms.</param>
    /// <param name="translationScale">Source-to-target height ratio.</param>
    /// <param name="sourceSkeletonTransform">Source skeleton-to-rendered transform.</param>
    /// <param name="targetSkeletonTransform">Destination skeleton-to-rendered transform.</param>
    /// <param name="sourceLocals">Reusable source local-transform storage.</param>
    /// <param name="sourceWorld">Reusable source world-transform storage.</param>
    /// <param name="targetLocals">Destination local-transform storage.</param>
    /// <param name="targetWorld">Reusable destination world-transform storage.</param>
    private static void EvaluateAt(float time, AnimationClipResource clip,
        HumanoidRig sourceRig, HumanoidRig targetRig, int[] sourceByTarget,
        Matrix4x4[] sourceBindWorld, Matrix4x4[] targetBindWorld,
        float translationScale, Matrix4x4 sourceSkeletonTransform,
        Matrix4x4 targetSkeletonTransform, JointTransform[] sourceLocals,
        Matrix4x4[] sourceWorld, JointTransform[] targetLocals,
        Matrix4x4[] targetWorld)
    {
        var sourceSkeleton = sourceRig.Skeleton;
        var targetSkeleton = targetRig.Skeleton;
        SkeletonPose.SampleLocalTransforms(sourceSkeleton, clip, time, sourceLocals);
        BuildWorldTransforms(sourceSkeleton, sourceLocals, sourceWorld);
        var inverseTargetTransform = Invert(targetSkeletonTransform);
        var targetHips = targetRig.GetJoint(HumanoidBone.Hips);
        var sourceHips = sourceRig.GetJoint(HumanoidBone.Hips);
        for (var targetIndex = 0; targetIndex < targetSkeleton.JointCount; targetIndex++)
        {
            var targetJoint = targetSkeleton.Joints[targetIndex];
            var local = targetJoint.BindTransform;
            var sourceIndex = sourceByTarget[targetIndex];
            if (sourceIndex >= 0)
            {
                var sourceBindRotation = ExtractRotation(sourceBindWorld[sourceIndex]);
                var sourceAnimatedRotation = ExtractRotation(
                    sourceWorld[sourceIndex] * sourceSkeletonTransform);
                var targetBindRotation = ExtractRotation(targetBindWorld[targetIndex]);
                var desiredWorldMatrix =
                    Matrix4x4.CreateFromQuaternion(targetBindRotation) *
                    Matrix4x4.CreateFromQuaternion(
                        Quaternion.Inverse(sourceBindRotation)) *
                    Matrix4x4.CreateFromQuaternion(sourceAnimatedRotation);
                var desiredSkeletonWorld = desiredWorldMatrix * inverseTargetTransform;
                var parentSkeletonRotation = targetJoint.ParentIndex < 0
                    ? Quaternion.Identity
                    : ExtractRotation(targetWorld[targetJoint.ParentIndex]);
                var localRotation = ExtractRotation(desiredSkeletonWorld *
                    Matrix4x4.CreateFromQuaternion(
                        Quaternion.Inverse(parentSkeletonRotation)));
                var localTranslation = local.Translation;
                if (targetIndex == targetHips)
                {
                    var sourceRendered = Vector3.Transform(
                        sourceWorld[sourceHips].Translation, sourceSkeletonTransform);
                    var sourceDelta = sourceRendered -
                        sourceBindWorld[sourceHips].Translation;
                    var desiredWorldTranslation = targetBindWorld[targetHips].Translation +
                        sourceDelta * translationScale;
                    var desiredSkeletonTranslation = Vector3.Transform(
                        desiredWorldTranslation, inverseTargetTransform);
                    localTranslation = targetJoint.ParentIndex < 0
                        ? desiredSkeletonTranslation
                        : Vector3.Transform(desiredSkeletonTranslation,
                            Invert(targetWorld[targetJoint.ParentIndex]));
                }
                local = new JointTransform(localTranslation, localRotation, local.Scale);
            }
            targetLocals[targetIndex] = local;
            var matrix = local.ToMatrix();
            targetWorld[targetIndex] = targetJoint.ParentIndex < 0
                ? matrix : matrix * targetWorld[targetJoint.ParentIndex];
        }
    }

    /// <summary>Creates a target-indexed semantic source-joint lookup.</summary>
    /// <param name="source">Source humanoid mapping.</param>
    /// <param name="target">Target humanoid mapping.</param>
    /// <returns>Source joint for every target joint, or -1.</returns>
    private static int[] BuildSourceByTarget(HumanoidRig source, HumanoidRig target)
    {
        var result = new int[target.Skeleton.JointCount];
        Array.Fill(result, -1);
        for (var boneIndex = 0;
             boneIndex <= (int)HumanoidBone.RightLittleDistal; boneIndex++)
        {
            var bone = (HumanoidBone)boneIndex;
            var sourceJoint = source.GetJoint(bone);
            var targetJoint = target.GetJoint(bone);
            if (sourceJoint >= 0 && targetJoint >= 0)
                result[targetJoint] = sourceJoint;
        }
        return result;
    }

    /// <summary>Collects unique source root and hips translation-key times.</summary>
    /// <param name="clip">Source animation clip.</param>
    /// <param name="rig">Source humanoid rig.</param>
    /// <returns>Sorted unique key times.</returns>
    private static float[] CollectRootTranslationTimes(
        AnimationClipResource clip, HumanoidRig rig)
    {
        var times = new SortedSet<float>();
        AddTranslationTimes(clip, rig.GetJoint(HumanoidBone.Hips), times);
        AddTranslationTimes(clip, rig.RootMotionJointIndex, times);
        var result = new float[times.Count];
        times.CopyTo(result);
        return result;
    }

    /// <summary>Adds one joint's translation keys when present.</summary>
    /// <param name="clip">Source animation clip.</param>
    /// <param name="jointIndex">Source joint index.</param>
    /// <param name="times">Unique destination set.</param>
    private static void AddTranslationTimes(AnimationClipResource clip,
        int jointIndex, SortedSet<float> times)
    {
        if (jointIndex < 0 || clip.Tracks[jointIndex]?.Translation is not { } translation)
            return;
        for (var index = 0; index < translation.Times.Length; index++)
            times.Add(translation.Times[index]);
    }

    /// <summary>Calculates proportional root translation from hips-to-head reference height.</summary>
    /// <param name="source">Source mapping.</param>
    /// <param name="target">Target mapping.</param>
    /// <param name="sourceWorld">Source bind-world transforms.</param>
    /// <param name="targetWorld">Target bind-world transforms.</param>
    /// <returns>Finite target-to-source reference-height ratio.</returns>
    private static float CalculateHeightScale(HumanoidRig source, HumanoidRig target,
        Matrix4x4[] sourceWorld, Matrix4x4[] targetWorld)
    {
        var sourceHeight = Vector3.Distance(
            sourceWorld[source.GetJoint(HumanoidBone.Hips)].Translation,
            sourceWorld[source.GetJoint(HumanoidBone.Head)].Translation);
        var targetHeight = Vector3.Distance(
            targetWorld[target.GetJoint(HumanoidBone.Hips)].Translation,
            targetWorld[target.GetJoint(HumanoidBone.Head)].Translation);
        return sourceHeight > 0.00001f && float.IsFinite(sourceHeight) &&
               float.IsFinite(targetHeight) ? targetHeight / sourceHeight : 1f;
    }

    /// <summary>Builds world transforms from one skeleton's reference pose.</summary>
    /// <param name="skeleton">Skeleton to evaluate.</param>
    /// <param name="skeletonTransform">Skeleton-to-rendered transform.</param>
    /// <returns>Reference-pose world transforms.</returns>
    private static Matrix4x4[] BuildBindWorldTransforms(
        SkeletonResource skeleton, Matrix4x4 skeletonTransform)
    {
        var result = new Matrix4x4[skeleton.JointCount];
        for (var index = 0; index < result.Length; index++)
        {
            var joint = skeleton.Joints[index];
            var local = joint.BindTransform.ToMatrix();
            result[index] = joint.ParentIndex < 0
                ? local : local * result[joint.ParentIndex];
        }
        for (var index = 0; index < result.Length; index++)
            result[index] *= skeletonTransform;
        return result;
    }

    /// <summary>Builds world transforms from sampled local transforms.</summary>
    /// <param name="skeleton">Skeleton hierarchy.</param>
    /// <param name="locals">Sampled local transforms.</param>
    /// <param name="world">Destination world-transform storage.</param>
    private static void BuildWorldTransforms(SkeletonResource skeleton,
        JointTransform[] locals, Matrix4x4[] world)
    {
        for (var index = 0; index < world.Length; index++)
        {
            var parent = skeleton.Joints[index].ParentIndex;
            var local = locals[index].ToMatrix();
            world[index] = parent < 0 ? local : local * world[parent];
        }
    }

    /// <summary>Extracts a normalized orientation while ignoring translation and scale.</summary>
    /// <param name="matrix">Finite affine transform.</param>
    /// <returns>Normalized orientation.</returns>
    private static Quaternion ExtractRotation(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out _, out var rotation, out _))
            throw new InvalidOperationException("A humanoid pose transform cannot be decomposed.");
        return Quaternion.Normalize(rotation);
    }

    /// <summary>Inverts an affine transform or reports invalid animated hierarchy data.</summary>
    /// <param name="matrix">Transform to invert.</param>
    /// <returns>Inverse transform.</returns>
    private static Matrix4x4 Invert(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Invert(matrix, out var inverse))
            throw new InvalidOperationException("A humanoid parent transform is not invertible.");
        return inverse;
    }
}
