using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace Engine.Graphics.Tests;

public sealed class AnimationResourceTests
{
    /// <summary>Ensures managed packing matches the Vulkan vertex declaration.</summary>
    [Fact]
    public void SkinnedForwardModelVertex_Stride_MatchesManagedLayout()
    {
        Assert.Equal((int)SkinnedForwardModelVertex.Stride,
            Marshal.SizeOf<SkinnedForwardModelVertex>());
    }

    /// <summary>Interpolates a joint curve and builds its mesh-space skin matrix.</summary>
    [Fact]
    public void AnimationPlayer_Update_EvaluatesSkinPalette()
    {
        var resource = CreateResource();
        var player = new AnimationPlayer(resource);
        Assert.True(player.Play("Move"));

        player.Update(0.5d);

        Assert.Equal(0.5f, player.Time);
        Assert.Equal(0.5f, player.Pose.SkinMatrices[1].M41, 5);
    }

    /// <summary>Starts negative-speed playback at the clip end and advances backward.</summary>
    [Fact]
    public void AnimationPlayer_NegativeSpeed_StartsAtClipEnd()
    {
        var player = new AnimationPlayer(CreateResource()) { Speed = -1f };

        player.Play("Move");
        player.Update(0.25d);

        Assert.Equal(0.75f, player.Time);
        Assert.Equal(0.75f, player.Pose.SkinMatrices[1].M41, 5);
    }

    /// <summary>Round-trips geometry, joints, influences, and clips through the artifact format.</summary>
    [Fact]
    public void SaveLoad_ValidSkinnedMesh_RoundTripsAnimationData()
    {
        var expected = CreateResource();
        using var stream = new MemoryStream();

        expected.Save(stream);
        stream.Position = 0;
        var actual = SkinnedMeshResource.Load(stream);

        Assert.Equal(2, actual.Skeleton.JointCount);
        Assert.Equal("Child", actual.Skeleton.Joints[1].Name);
        Assert.Equal(new Vector4(0.75f, 0.25f, 0f, 0f), actual.Influences[0].Weights);
        Assert.Equal(expected.MeshNodeTransform, actual.MeshNodeTransform);
        var clip = Assert.Single(actual.Animations);
        Assert.Equal("Move", clip.Name);
        Assert.Equal(2f, clip.Tracks[1]!.Translation!.Values[1].X);
    }

    /// <summary>Applies the source mesh-node transform after skinning and before the instance.</summary>
    [Fact]
    public void ComposeModelTransform_PreservesGltfTransformOrder()
    {
        var resource = CreateResource();
        var instance = Matrix4x4.CreateTranslation(3f, 4f, 5f);

        var actual = resource.ComposeModelTransform(instance);

        Assert.Equal(resource.MeshNodeTransform * instance, actual);
    }

    /// <summary>Ensures warmed animation playback remains allocation-free per frame.</summary>
    [Fact]
    public void AnimationPlayer_Update_DoesNotAllocate()
    {
        var player = new AnimationPlayer(CreateResource());
        player.Play();
        player.Update(0.016d);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 100; index++)
            player.Update(0.016d);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Creates a two-joint animated triangle resource.</summary>
    /// <returns>Reusable test resource.</returns>
    private static SkinnedMeshResource CreateResource()
    {
        var mesh = new StaticMeshResource(
        [
            new(Vector3.Zero, Vector3.UnitY, Vector2.Zero, Vector4.UnitX),
            new(Vector3.UnitX, Vector3.UnitY, Vector2.Zero, Vector4.UnitX),
            new(Vector3.UnitY, Vector3.UnitY, Vector2.Zero, Vector4.UnitX)
        ], [0u, 1u, 2u], [new Submesh(0, 3, 0)]);
        var skeleton = new SkeletonResource(
        [
            new SkeletonJoint("Root", -1, JointTransform.Identity, Matrix4x4.Identity),
            new SkeletonJoint("Child", 0,
                new JointTransform(Vector3.UnitX, Quaternion.Identity, Vector3.One),
                Matrix4x4.CreateTranslation(-Vector3.UnitX))
        ]);
        var clip = new AnimationClipResource("Move", 1f,
        [
            null,
            new JointAnimationTrack(
                new Vector3AnimationTrack([0f, 1f],
                    [Vector3.UnitX, Vector3.UnitX * 2f], AnimationInterpolation.Linear),
                null,
                null)
        ]);
        var meshNodeTransform = Matrix4x4.CreateScale(0.01f) *
            Matrix4x4.CreateRotationX(MathF.PI / 2f);
        return new SkinnedMeshResource(mesh,
        [
            new SkinInfluence(0, 1, 0, 0, new Vector4(0.75f, 0.25f, 0f, 0f)),
            new SkinInfluence(1, 0, 0, 0, Vector4.UnitX),
            new SkinInfluence(1, 0, 0, 0, Vector4.UnitX)
        ], skeleton, [clip], meshNodeTransform);
    }
}
