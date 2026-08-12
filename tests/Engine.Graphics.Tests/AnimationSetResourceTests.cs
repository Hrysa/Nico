using Engine.Core;
using Engine.Graphics;
using System.Numerics;
using System.Text;
using Xunit;

namespace Engine.Graphics.Tests;

public sealed class AnimationSetResourceTests
{
    /// <summary>Verifies readable project sources decode into explicit aliased references.</summary>
    [Fact]
    public void Load_JsonSource_DecodesEntries()
    {
        var asset = AssetId.New();
        var json = $$"""
            {
              "version": 1,
              "entries": [
                {
                  "alias": "Run",
                  "source": {
                    "asset": "{{asset}}",
                    "subAsset": "animation/0"
                  }
                }
              ]
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var resource = AnimationSetResource.Load(stream);

        var entry = Assert.Single(resource.Entries);
        Assert.Equal("Run", entry.Alias);
        Assert.Equal(new AssetReference(asset, "animation/0"), entry.Source);
        Assert.Null(entry.Clip);
    }

    /// <summary>Round-trips stable aliases and explicit source clip references.</summary>
    [Fact]
    public void SaveLoad_ValidSet_PreservesEntries()
    {
        var source = new AssetReference(AssetId.New(), "animation/Run");
        var expected = new AnimationSetResource(
        [
            new AnimationSetEntry("Run", source, "Sprint", true, "Root", 1.5f, false),
            new AnimationSetEntry("Idle", source)
        ]);
        using var stream = new MemoryStream();

        expected.Save(stream);
        stream.Position = 0;
        var actual = AnimationSetResource.Load(stream);

        Assert.Equal(expected.Entries, actual.Entries);
    }

    /// <summary>Round-trips the readable source encoding without losing authored options.</summary>
    [Fact]
    public void SaveJsonLoad_ValidSet_PreservesEntries()
    {
        var source = new AssetReference(AssetId.New(), "animation/Run");
        var expected = new AnimationSetResource(
        [
            new AnimationSetEntry("Run", source, null, true, "Hips", 0.75f, false)
        ]);
        using var stream = new MemoryStream();

        expected.SaveJson(stream);
        stream.Position = 0;
        var actual = AnimationSetResource.Load(stream);

        Assert.Equal(expected.Entries, actual.Entries);
    }

    /// <summary>Propagates authored speed and loop defaults into bound controller state.</summary>
    [Fact]
    public void BindTo_PlaybackDefaults_ConfigureRegisteredState()
    {
        var reference = new AssetReference(AssetId.New(), "animation/Run");
        var skeleton = new SkeletonResource(
        [
            new SkeletonJoint("Root", -1, JointTransform.Identity, Matrix4x4.Identity)
        ]);
        var source = CreateSource(skeleton, "ImportedRun", Vector3.Zero);
        var set = new AnimationSetResource(
        [
            new AnimationSetEntry("Run", reference, Speed: 1.5f, Loop: false)
        ]);
        var clips = set.BindTo(skeleton, _ => source);
        var skin = new SkinnedMeshResource(
            new StaticMeshResource([], [], []), [], skeleton, clips);
        using var controller = new AnimationController(skin);

        var state = controller.GetOrCreate("Run");

        Assert.Equal(1.5f, state.Speed);
        Assert.False(state.Loop);
    }

    /// <summary>Loads version-one binary sets as entries with in-place processing disabled.</summary>
    [Fact]
    public void Load_VersionOneBinary_PreservesCompatibility()
    {
        var asset = AssetId.New();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("NASET001"));
            writer.Write(1u);
            writer.Write(1u);
            writer.Write("Idle");
            writer.Write(asset.Value.ToByteArray());
            writer.Write("animation/0");
            writer.Write(false);
        }
        stream.Position = 0;

        var resource = AnimationSetResource.Load(stream);

        var entry = Assert.Single(resource.Entries);
        Assert.False(entry.InPlace);
        Assert.Null(entry.RootMotionJoint);
    }

    /// <summary>Removes X/Z travel while retaining authored vertical root motion.</summary>
    [Fact]
    public void BindTo_InPlaceEntry_StripsHorizontalRootTravel()
    {
        var reference = new AssetReference(AssetId.New(), "animation/Run");
        var skeleton = new SkeletonResource(
        [
            new SkeletonJoint("Armature", -1, JointTransform.Identity, Matrix4x4.Identity),
            new SkeletonJoint("mixamorig:Hips", 0, JointTransform.Identity, Matrix4x4.Identity)
        ]);
        var hipsTranslation = new Vector3AnimationTrack(
            [0f, 0.5f, 1f],
            [new Vector3(2f, 3f, 4f), new Vector3(6f, 5f, 8f), new Vector3(10f, 7f, 12f)],
            AnimationInterpolation.Linear);
        var source = new SkeletalAnimationResource(skeleton,
        [
            new AnimationClipResource("ImportedRun", 1f,
            [
                null,
                new JointAnimationTrack(hipsTranslation, null, null)
            ])
        ]);
        var set = new AnimationSetResource(
        [
            new AnimationSetEntry("Run", reference, null, true, "mixamorig:Hips")
        ]);

        var clip = Assert.Single(set.BindTo(skeleton, candidate =>
            candidate == reference ? source : null));

        Assert.Equal(
        [
            new Vector3(2f, 3f, 4f),
            new Vector3(2f, 5f, 4f),
            new Vector3(2f, 7f, 4f)
        ], clip.Tracks[1]!.Translation!.Values);
        Assert.Equal("Run", clip.Name);
    }

    /// <summary>Rejects an explicit in-place joint that cannot supply root translation.</summary>
    [Fact]
    public void BindTo_InPlaceMissingJoint_ThrowsDiagnostic()
    {
        var reference = new AssetReference(AssetId.New(), "animation/Run");
        var skeleton = new SkeletonResource(
        [
            new SkeletonJoint("Root", -1, JointTransform.Identity, Matrix4x4.Identity)
        ]);
        var source = CreateSource(skeleton, "ImportedRun", Vector3.UnitX);
        var set = new AnimationSetResource(
        [
            new AnimationSetEntry("Run", reference, null, true, "MissingHips")
        ]);

        var exception = Assert.Throws<InvalidDataException>(() =>
            set.BindTo(skeleton, _ => source));

        Assert.Contains("MissingHips", exception.Message, StringComparison.Ordinal);
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
