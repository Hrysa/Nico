using Engine.Core;
using Engine.Graphics;
using System.Numerics;
using Xunit;

namespace Engine.Graphics.Tests;

public sealed class AnimationSetResourceTests
{
    /// <summary>Round-trips stable aliases and explicit source clip references.</summary>
    [Fact]
    public void SaveLoad_ValidSet_PreservesEntries()
    {
        var source = new AssetReference(AssetId.New(), "animation/Run");
        var expected = new AnimationSetResource(
        [
            new AnimationSetEntry("Run", source, "Sprint"),
            new AnimationSetEntry("Idle", source)
        ]);
        using var stream = new MemoryStream();

        expected.Save(stream);
        stream.Position = 0;
        var actual = AnimationSetResource.Load(stream);

        Assert.Equal(expected.Entries, actual.Entries);
    }

    /// <summary>Rejects aliases whose dictionary identity would be ambiguous.</summary>
    [Fact]
    public void Constructor_DuplicateAlias_Throws()
    {
        var source = new AssetReference(AssetId.New(), "animation/Run");

        Assert.Throws<ArgumentException>(() => new AnimationSetResource(
        [
            new AnimationSetEntry("Move", source, "Walk"),
            new AnimationSetEntry("Move", source, "Run")
        ]));
    }

    /// <summary>Rejects trailing bytes so corrupted published assets do not load silently.</summary>
    [Fact]
    public void Load_TrailingPayload_Throws()
    {
        var source = new AssetReference(AssetId.New(), "animation/Idle");
        using var stream = new MemoryStream();
        new AnimationSetResource([new AnimationSetEntry("Idle", source)]).Save(stream);
        stream.WriteByte(1);
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => AnimationSetResource.Load(stream));
    }

    /// <summary>Binds multiple explicit sources and replaces imported names with stable aliases.</summary>
    [Fact]
    public void BindTo_MultipleSources_ProducesAliasedTargetClips()
    {
        var walkReference = new AssetReference(AssetId.New(), "animation/WalkSource");
        var jumpReference = new AssetReference(AssetId.New(), "animation/JumpSource");
        var skeleton = new SkeletonResource(
        [
            new SkeletonJoint("Root", -1, JointTransform.Identity, Matrix4x4.Identity)
        ]);
        var walk = CreateSource(skeleton, "ImportedWalk", Vector3.UnitX);
        var jump = CreateSource(skeleton, "ImportedJump", Vector3.UnitY);
        var set = new AnimationSetResource(
        [
            new AnimationSetEntry("Locomotion", walkReference, "ImportedWalk"),
            new AnimationSetEntry("Action", jumpReference)
        ]);

        var clips = set.BindTo(skeleton, reference =>
            reference == walkReference ? walk : reference == jumpReference ? jump : null);

        Assert.Equal(["Locomotion", "Action"], clips.Select(clip => clip.Name));
        Assert.Equal(Vector3.UnitX,
            clips[0].Tracks[0]!.Translation!.Values[0]);
        Assert.Equal(Vector3.UnitY,
            clips[1].Tracks[0]!.Translation!.Values[0]);
    }

    /// <summary>Creates one single-joint standalone source for alias binding tests.</summary>
    /// <param name="skeleton">Source skeleton.</param>
    /// <param name="name">Imported clip name.</param>
    /// <param name="translation">Constant root translation.</param>
    /// <returns>A source animation resource.</returns>
    private static SkeletalAnimationResource CreateSource(
        SkeletonResource skeleton, string name, Vector3 translation)
    {
        return new SkeletalAnimationResource(skeleton,
        [
            new AnimationClipResource(name, 1f,
            [
                new JointAnimationTrack(
                    new Vector3AnimationTrack([0f], [translation],
                        AnimationInterpolation.Step), null, null)
            ])
        ]);
    }
}
