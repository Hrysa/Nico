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

    /// <summary>Reuses one state and does not restart it when repeatedly played.</summary>
    [Fact]
    public void AnimationController_PlayCurrent_ReusesStateWithoutRestarting()
    {
        using var controller = new AnimationController(CreateBlendResource());
        var first = controller.Play("Forward");
        controller.Update(0.4d);

        var second = controller.Play("Forward");

        Assert.Same(first, second);
        Assert.Equal(0.4f, second.Time, 5);
        Assert.True(second.IsCurrent);
    }

    /// <summary>Blends sampled local transforms instead of final skin matrices.</summary>
    [Fact]
    public void AnimationController_CrossFade_BlendsLocalPose()
    {
        using var controller = new AnimationController(CreateBlendResource());
        controller.Play("Forward");
        var backward = controller.PlayFromStart("Backward", 1f);

        controller.Update(0.5d);

        Assert.Equal(0.5f, backward.Weight, 5);
        Assert.Equal(0f, controller.Pose.LocalTransforms[0].Translation.X, 5);
    }

    /// <summary>Starts an interrupted fade from current weights without snapping.</summary>
    [Fact]
    public void AnimationController_InterruptedFade_PreservesCurrentWeights()
    {
        using var controller = new AnimationController(CreateBlendResource());
        var forward = controller.Play("Forward");
        var backward = controller.Play("Backward", 1f);
        controller.Update(0.25d);
        var forwardBefore = forward.Weight;
        var backwardBefore = backward.Weight;

        controller.Play("Forward", 1f);
        controller.Update(0d);

        Assert.Equal(forwardBefore, forward.Weight, 5);
        Assert.Equal(backwardBefore, backward.Weight, 5);
    }

    /// <summary>Raises completion exactly once for reverse non-looping playback.</summary>
    [Fact]
    public void AnimationController_ReverseNonLooping_RaisesEndedOnce()
    {
        using var controller = new AnimationController(CreateBlendResource());
        var state = controller.GetOrCreate("Forward");
        state.Speed = -1f;
        state.Loop = false;
        var endings = 0;
        state.Ended += _ => endings++;
        controller.PlayFromStart("Forward");

        controller.Update(2d);
        controller.Update(2d);

        Assert.Equal(0f, state.Time);
        Assert.False(state.IsPlaying);
        Assert.Equal(1, endings);
    }

    /// <summary>Raises completion exactly once for forward non-looping playback.</summary>
    [Fact]
    public void AnimationController_ForwardNonLooping_RaisesEndedOnce()
    {
        using var controller = new AnimationController(CreateBlendResource());
        var state = controller.GetOrCreate("Forward");
        state.Loop = false;
        var endings = 0;
        state.Ended += _ => endings++;
        controller.PlayFromStart("Forward");

        controller.Update(2d);
        controller.Update(2d);

        Assert.Equal(1f, state.Time);
        Assert.False(state.IsPlaying);
        Assert.Equal(1, endings);
    }

    /// <summary>Defers completion callbacks until the runtime's explicit dispatch phase.</summary>
    [Fact]
    public void AnimationController_Advance_DefersEndedUntilDispatch()
    {
        using var controller = new AnimationController(CreateBlendResource());
        var state = controller.GetOrCreate("Forward");
        state.Loop = false;
        var endings = 0;
        state.Ended += _ => endings++;
        controller.PlayFromStart("Forward");

        controller.Advance(2d);
        Assert.Equal(0, endings);

        controller.DispatchEvents();
        Assert.Equal(1, endings);
    }

    /// <summary>Ensures warmed two-state cross-fading remains allocation-free.</summary>
    [Fact]
    public void AnimationController_CrossFadeUpdate_DoesNotAllocate()
    {
        using var controller = new AnimationController(CreateBlendResource());
        controller.Play("Forward");
        controller.Play("Backward", 10f);
        controller.Update(0.016d);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 100; index++)
            controller.Update(0.016d);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Uses the configured default fade for the convenience Play overload.</summary>
    [Fact]
    public void AnimationController_Play_UsesConfiguredDefaultFade()
    {
        using var controller = new AnimationController(CreateBlendResource())
            { DefaultFadeDuration = 1f };
        controller.PlayFromStart("Forward");

        var backward = controller.Play("Backward");
        controller.Update(0.25d);

        Assert.Equal(0.25f, backward.Weight, 5);
    }

    /// <summary>Fades a stopped state smoothly back to bind pose.</summary>
    [Fact]
    public void AnimationController_StopWithFade_BlendsTowardBindPose()
    {
        using var controller = new AnimationController(CreateBlendResource());
        controller.PlayFromStart("Forward");

        controller.Stop(1f);
        controller.Update(0.5d);

        Assert.Equal(0.5f, controller.Pose.LocalTransforms[0].Translation.X, 5);
        Assert.Null(controller.Current);
        Assert.True(controller.RequiresUpdate);
    }

    /// <summary>Offers a non-throwing path for optional gameplay clips.</summary>
    [Fact]
    public void AnimationController_TryPlay_UnknownClip_ReturnsFalse()
    {
        using var controller = new AnimationController(CreateBlendResource());

        Assert.False(controller.TryPlay("Missing", out var state));
        Assert.Null(state);
        Assert.Null(controller.Current);
    }

    /// <summary>Invalidates retained state mutation when its runtime scene is destroyed.</summary>
    [Fact]
    public void AnimationController_Dispose_InvalidatesRetainedState()
    {
        var controller = new AnimationController(CreateBlendResource());
        var state = controller.GetOrCreate("Forward");

        controller.Dispose();

        Assert.False(controller.IsValid);
        Assert.Throws<ObjectDisposedException>(() => state.Time = 0.5f);
        Assert.Throws<ObjectDisposedException>(() => state.Speed = 2f);
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

    /// <summary>Creates one-joint clips translating in opposite directions.</summary>
    /// <returns>Reusable blend-test resource.</returns>
    private static SkinnedMeshResource CreateBlendResource()
    {
        var skeleton = new SkeletonResource(
        [
            new SkeletonJoint("Root", -1, JointTransform.Identity, Matrix4x4.Identity)
        ]);
        var forward = new AnimationClipResource("Forward", 1f,
        [
            new JointAnimationTrack(
                new Vector3AnimationTrack([0f, 1f],
                    [Vector3.UnitX, Vector3.UnitX], AnimationInterpolation.Linear),
                null, null)
        ]);
        var backward = new AnimationClipResource("Backward", 1f,
        [
            new JointAnimationTrack(
                new Vector3AnimationTrack([0f, 1f],
                    [-Vector3.UnitX, -Vector3.UnitX], AnimationInterpolation.Linear),
                null, null)
        ]);
        return new SkinnedMeshResource(
            new StaticMeshResource([], [], []), [], skeleton, [forward, backward]);
    }
}
