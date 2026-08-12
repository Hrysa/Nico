using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>Exercises conservative humanoid detection and reference-pose retargeting.</summary>
public sealed class HumanoidRetargetingTests
{
    /// <summary>Does not guess a humanoid mapping from arbitrary generic joint names.</summary>
    [Fact]
    public void TryDetect_UnknownConvention_IsRejected()
    {
        var skeleton = new SkeletonResource(
        [
            new SkeletonJoint("CharacterRoot", -1, JointTransform.Identity,
                Matrix4x4.Identity),
            new SkeletonJoint("BodyCenter", 0, JointTransform.Identity,
                Matrix4x4.Identity)
        ]);

        Assert.False(HumanoidRig.TryDetect(skeleton, out var rig));
        Assert.Null(rig);
    }

    /// <summary>Maps an Unreal reference pose to the destination Mixamo reference pose.</summary>
    [Fact]
    public void Retarget_ReferencePose_PreservesTargetBindPose()
    {
        var source = CreateSkeleton(mixamo: false);
        var target = CreateSkeleton(mixamo: true);
        var tracks = new JointAnimationTrack?[source.JointCount];
        for (var index = 0; index < tracks.Length; index++)
        {
            var bind = source.Joints[index].BindTransform;
            tracks[index] = new JointAnimationTrack(
                new Vector3AnimationTrack([0f], [bind.Translation],
                    AnimationInterpolation.Step),
                new QuaternionAnimationTrack([0f], [bind.Rotation],
                    AnimationInterpolation.Step), null);
        }
        var animation = new SkeletalAnimationResource(source,
        [
            new AnimationClipResource("Reference", 0f, tracks)
        ]);

        var clip = Assert.Single(animation.BindTo(
            target, AnimationRetargetMode.Humanoid));
        var pose = new SkeletonPose(target);
        pose.Evaluate(target, clip, 0f);

        for (var index = 0; index < target.JointCount; index++)
        {
            var expected = target.Joints[index].BindTransform;
            AssertVector(expected.Translation, pose.LocalTransforms[index].Translation);
            AssertRotation(expected.Rotation, pose.LocalTransforms[index].Rotation);
        }
    }

    /// <summary>Retargeting a pose onto the same rig reproduces its animated world pose.</summary>
    [Fact]
    public void Retarget_SameMixamoRig_PreservesAnimatedPose()
    {
        var skeleton = CreateSkeleton(mixamo: true);
        var tracks = new JointAnimationTrack?[skeleton.JointCount];
        var arm = skeleton.FindJoint("mixamorig:LeftArm");
        var animatedRotation = Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.8f) *
            skeleton.Joints[arm].BindTransform.Rotation);
        tracks[arm] = new JointAnimationTrack(null,
            new QuaternionAnimationTrack([0f], [animatedRotation],
                AnimationInterpolation.Step), null);
        var sourceClip = new AnimationClipResource("Wave", 0f, tracks);
        var source = new SkeletalAnimationResource(skeleton, [sourceClip]);

        var retargeted = Assert.Single(source.BindTo(
            skeleton, AnimationRetargetMode.Humanoid));
        var expectedPose = new SkeletonPose(skeleton);
        var actualPose = new SkeletonPose(skeleton);
        expectedPose.Evaluate(skeleton, sourceClip, 0f);
        actualPose.Evaluate(skeleton, retargeted, 0f);

        for (var index = 0; index < skeleton.JointCount; index++)
            AssertMatrix(expectedPose.WorldTransforms[index], actualPose.WorldTransforms[index]);
    }

    /// <summary>Creates one minimal but complete supported humanoid body hierarchy.</summary>
    /// <param name="mixamo">Whether to use Mixamo rather than Unreal names.</param>
    /// <returns>Recognizable skeleton with deliberately non-identical reference rotations.</returns>
    private static SkeletonResource CreateSkeleton(bool mixamo)
    {
        var prefix = mixamo ? "mixamorig:" : string.Empty;
        var hips = mixamo ? prefix + "Hips" : "B_Pelvis";
        var spine = mixamo ? prefix + "Spine" : "B_Spine";
        var head = mixamo ? prefix + "Head" : "B_Head";
        var leftArm = mixamo ? prefix + "LeftArm" : "B_L_UpperArm";
        var leftForearm = mixamo ? prefix + "LeftForeArm" : "B_L_Forearm";
        var leftHand = mixamo ? prefix + "LeftHand" : "B_L_Hand";
        var rightArm = mixamo ? prefix + "RightArm" : "B_R_UpperArm";
        var rightForearm = mixamo ? prefix + "RightForeArm" : "B_R_Forearm";
        var rightHand = mixamo ? prefix + "RightHand" : "B_R_Hand";
        var leftThigh = mixamo ? prefix + "LeftUpLeg" : "B_L_Thigh";
        var leftCalf = mixamo ? prefix + "LeftLeg" : "B_L_Calf";
        var leftFoot = mixamo ? prefix + "LeftFoot" : "B_L_Foot";
        var rightThigh = mixamo ? prefix + "RightUpLeg" : "B_R_Thigh";
        var rightCalf = mixamo ? prefix + "RightLeg" : "B_R_Calf";
        var rightFoot = mixamo ? prefix + "RightFoot" : "B_R_Foot";
        var angle = mixamo ? 0.23f : -0.17f;
        var rotation = Quaternion.CreateFromYawPitchRoll(angle, angle * 0.5f, 0f);
        var rootOffset = mixamo ? new Vector3(0f, 1.1f, 0f) : Vector3.UnitY;
        return new SkeletonResource(
        [
            new SkeletonJoint(hips, -1,
                new JointTransform(rootOffset, rotation, Vector3.One), Matrix4x4.Identity),
            new SkeletonJoint(spine, 0,
                new JointTransform(new Vector3(0f, 0.4f, 0f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(head, 1,
                new JointTransform(new Vector3(0f, 0.7f, 0f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(leftArm, 1,
                new JointTransform(new Vector3(-0.3f, 0.5f, 0f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(leftForearm, 3,
                new JointTransform(new Vector3(-0.4f, 0f, 0f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(leftHand, 4,
                new JointTransform(new Vector3(-0.3f, 0f, 0f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(rightArm, 1,
                new JointTransform(new Vector3(0.3f, 0.5f, 0f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(rightForearm, 6,
                new JointTransform(new Vector3(0.4f, 0f, 0f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(rightHand, 7,
                new JointTransform(new Vector3(0.3f, 0f, 0f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(leftThigh, 0,
                new JointTransform(new Vector3(-0.2f, -0.2f, 0f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(leftCalf, 9,
                new JointTransform(new Vector3(0f, -0.6f, 0f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(leftFoot, 10,
                new JointTransform(new Vector3(0f, -0.5f, 0.1f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(rightThigh, 0,
                new JointTransform(new Vector3(0.2f, -0.2f, 0f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(rightCalf, 12,
                new JointTransform(new Vector3(0f, -0.6f, 0f), rotation, Vector3.One),
                Matrix4x4.Identity),
            new SkeletonJoint(rightFoot, 13,
                new JointTransform(new Vector3(0f, -0.5f, 0.1f), rotation, Vector3.One),
                Matrix4x4.Identity)
        ]);
    }

    /// <summary>Asserts two vectors are nearly equal.</summary>
    /// <param name="expected">Expected vector.</param>
    /// <param name="actual">Actual vector.</param>
    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }

    /// <summary>Asserts two orientations are equivalent despite quaternion sign.</summary>
    /// <param name="expected">Expected orientation.</param>
    /// <param name="actual">Actual orientation.</param>
    private static void AssertRotation(Quaternion expected, Quaternion actual)
    {
        Assert.InRange(MathF.Abs(Quaternion.Dot(expected, actual)), 0.9999f, 1.0001f);
    }

    /// <summary>Asserts two affine matrices are nearly equal component by component.</summary>
    /// <param name="expected">Expected matrix.</param>
    /// <param name="actual">Actual matrix.</param>
    private static void AssertMatrix(Matrix4x4 expected, Matrix4x4 actual)
    {
        Assert.Equal(expected.M11, actual.M11, 4);
        Assert.Equal(expected.M12, actual.M12, 4);
        Assert.Equal(expected.M13, actual.M13, 4);
        Assert.Equal(expected.M21, actual.M21, 4);
        Assert.Equal(expected.M22, actual.M22, 4);
        Assert.Equal(expected.M23, actual.M23, 4);
        Assert.Equal(expected.M31, actual.M31, 4);
        Assert.Equal(expected.M32, actual.M32, 4);
        Assert.Equal(expected.M33, actual.M33, 4);
        Assert.Equal(expected.M41, actual.M41, 4);
        Assert.Equal(expected.M42, actual.M42, 4);
        Assert.Equal(expected.M43, actual.M43, 4);
    }
}
