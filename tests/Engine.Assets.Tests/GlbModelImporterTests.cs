using System.Numerics;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Engine.Core;
using Engine.Graphics;
using Xunit;

namespace Engine.Assets.Tests;

public sealed class GlbModelImporterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("nico-glb-").FullName;

    /// <summary>Guards the cache contract for the current standard-material artifact format.</summary>
    [Fact]
    public void Version_CurrentArtifactContract_IsEleven()
    {
        Assert.Equal(11, new GlbModelImporter().Version);
    }

    /// <summary>Imports the example character and Run clip and verifies in-place hips binding.</summary>
    [Fact]
    public void Import_ExampleRun_InPlaceBindingRemovesHorizontalHipsTravel()
    {
        var models = Path.GetFullPath("../../example_game/models",
            Path.GetDirectoryName(GetSourceFilePath())!);
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var characterStaging = Path.Combine(_directory, "character-staging");
        var runStaging = Path.Combine(_directory, "run-staging");
        var characterResult = new GlbModelImporter().Import(new AssetImportContext(
            Path.Combine(models, "Ch03_nonPBR.glb"),
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", characterStaging, CancellationToken.None));
        var runResult = new GlbModelImporter().Import(new AssetImportContext(
            Path.Combine(models, "Running.glb"),
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", runStaging, CancellationToken.None));
        var characterArtifact = Assert.Single(characterResult.Artifacts,
            item => item.Key == "mesh/Mesh/0" && item.ContentType == "nico/skinned-mesh");
        var runArtifact = Assert.Single(runResult.Artifacts,
            item => item.Key == "animation/0" &&
                item.ContentType == "nico/skeletal-animation");
        using var characterStream = File.OpenRead(Path.Combine(
            characterStaging, characterArtifact.RelativePath));
        using var runStream = File.OpenRead(Path.Combine(runStaging, runArtifact.RelativePath));
        var character = SkinnedMeshResource.Load(characterStream);
        var run = SkeletalAnimationResource.Load(runStream);
        var reference = new AssetReference(AssetId.New(), "animation/0");
        var set = new AnimationSetResource(
        [
            new AnimationSetEntry(
                "Run", reference, null, true, "mixamorig:Hips")
        ]);

        var clip = Assert.Single(set.BindTo(
            character.Skeleton, _ => run, character.MeshNodeTransform));

        var hips = character.Skeleton.FindJoint("mixamorig:Hips");
        Assert.True(hips >= 0);
        var values = clip.Tracks[hips]!.Translation!.Values;
        Assert.True(values.Length > 1);
        var pose = new SkeletonPose(character.Skeleton);
        pose.Evaluate(character.Skeleton, clip, 0f);
        var initialWorld = pose.WorldTransforms[hips].Translation;
        var initialRendered = Vector3.Transform(initialWorld, character.MeshNodeTransform);
        for (var sample = 1; sample <= 20; sample++)
        {
            pose.Evaluate(character.Skeleton, clip, clip.Duration * sample / 20f);
            var world = pose.WorldTransforms[hips].Translation;
            var rendered = Vector3.Transform(world, character.MeshNodeTransform);
            Assert.Equal(initialRendered.X, rendered.X, 4);
            Assert.Equal(initialRendered.Z, rendered.Z, 4);
        }
    }

    /// <summary>Auto binding retargets Mixamo rigs whose reference poses differ.</summary>
    [Fact]
    public void Import_ExampleRun_AutoUsesHumanoidForDifferentMixamoBindPoses()
    {
        var models = Path.GetFullPath("../../example_game/models",
            Path.GetDirectoryName(GetSourceFilePath())!);
        var character = ImportSkinnedMesh(
            Path.Combine(models, "Ch03_nonPBR.glb"), "mixamo-character");
        var run = ImportAnimation(Path.Combine(models, "Running.glb"), "mixamo-run");
        var humanoid = Assert.Single(run.BindTo(
            character.Skeleton, AnimationRetargetMode.Humanoid,
            character.MeshNodeTransform));
        var automatic = Assert.Single(run.BindTo(
            character.Skeleton, AnimationRetargetMode.Auto,
            character.MeshNodeTransform));
        Assert.True(HumanoidRig.TryDetect(character.Skeleton, out var rig));
        var automaticPose = new SkeletonPose(character.Skeleton);
        var humanoidPose = new SkeletonPose(character.Skeleton);

        for (var sample = 0; sample <= 4; sample++)
        {
            var time = automatic.Duration * sample / 4f;
            automaticPose.Evaluate(character.Skeleton, automatic, time);
            humanoidPose.Evaluate(character.Skeleton, humanoid, time);
            for (var boneIndex = 0;
                 boneIndex <= (int)HumanoidBone.RightLittleDistal; boneIndex++)
            {
                var joint = rig!.GetJoint((HumanoidBone)boneIndex);
                if (joint >= 0)
                    AssertMatrixNearlyEqual(
                        automaticPose.WorldTransforms[joint], humanoidPose.WorldTransforms[joint]);
            }
        }
    }

    /// <summary>Retargeted Mixamo previews stay upright and preserve horizontal locomotion.</summary>
    [Fact]
    public void Import_MixamoPreviews_PreserveUpAxisAndBindQuickly()
    {
        var models = Path.GetFullPath("../../example_game/models",
            Path.GetDirectoryName(GetSourceFilePath())!);
        var character = ImportSkinnedMesh(
            Path.Combine(models, "Ch03_nonPBR.glb"), "preview-character");
        var breathing = ImportAnimation(
            Path.Combine(models, "Breathing Idle.glb"), "preview-breathing");
        var running = ImportAnimation(
            Path.Combine(models, "Running.glb"), "preview-running");
        Assert.NotEqual(Matrix4x4.Identity, breathing.SkeletonTransform);
        Assert.NotEqual(Matrix4x4.Identity, running.SkeletonTransform);
        AssertMatrixNearlyEqual(
            character.MeshNodeTransform, breathing.SkeletonTransform);
        AssertMatrixNearlyEqual(
            character.MeshNodeTransform, running.SkeletonTransform);
        var stopwatch = Stopwatch.StartNew();
        var breathingClip = Assert.Single(breathing.BindTo(
            character.Skeleton, AnimationRetargetMode.Humanoid,
            character.MeshNodeTransform));
        var runningClip = Assert.Single(running.BindTo(
            character.Skeleton, AnimationRetargetMode.Humanoid,
            character.MeshNodeTransform));
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Two preview clips took {stopwatch.Elapsed.TotalMilliseconds:0} ms to bind.");

        var hips = character.Skeleton.FindJoint("mixamorig:Hips");
        var head = character.Skeleton.FindJoint("mixamorig:Head");
        var pose = new SkeletonPose(character.Skeleton);
        for (var sample = 0; sample <= 4; sample++)
        {
            pose.Evaluate(character.Skeleton, breathingClip,
                breathingClip.Duration * sample / 4f);
            var body = Vector3.Normalize(
                Vector3.Transform(pose.WorldTransforms[head].Translation,
                    character.MeshNodeTransform) -
                Vector3.Transform(pose.WorldTransforms[hips].Translation,
                    character.MeshNodeTransform));
            Assert.True(Vector3.Dot(body, Vector3.UnitY) > 0.6f,
                $"Breathing pose is tilted at sample {sample}: {body}.");
            var bounds = CalculateRenderedBounds(character, pose);
            var extent = bounds.Maximum - bounds.Minimum;
            Assert.True(extent.Y > extent.Z * 1.5f,
                $"Breathing mesh is lying down at sample {sample}: extent {extent}.");
        }

        pose.Evaluate(character.Skeleton, runningClip, 0f);
        var start = Vector3.Transform(pose.WorldTransforms[hips].Translation,
            character.MeshNodeTransform);
        pose.Evaluate(character.Skeleton, runningClip, runningClip.Duration);
        var travel = Vector3.Transform(pose.WorldTransforms[hips].Translation,
            character.MeshNodeTransform) - start;
        var horizontal = MathF.Sqrt(travel.X * travel.X + travel.Z * travel.Z);
        Assert.True(horizontal > MathF.Abs(travel.Y) * 2f,
            $"Running travel should remain horizontal, but was {travel}.");
        var runningBounds = CalculateRenderedBounds(character, pose);
        var runningExtent = runningBounds.Maximum - runningBounds.Minimum;
        Assert.True(runningExtent.Y > runningExtent.Z * 1.5f,
            $"Running mesh is lying down: extent {runningExtent}.");
    }

    /// <summary>Retargets the RPG Unreal-style block clip onto the example Mixamo character.</summary>
    [Fact]
    public void Import_RpgBlock_HumanoidRetargetsToMixamoCharacter()
    {
        var project = Path.GetFullPath("../../example_game",
            Path.GetDirectoryName(GetSourceFilePath())!);
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var characterStaging = Path.Combine(_directory, "humanoid-character-staging");
        var animationStaging = Path.Combine(_directory, "humanoid-animation-staging");
        var importer = new GlbModelImporter();
        var characterResult = importer.Import(new AssetImportContext(
            Path.Combine(project, "models", "Ch03_nonPBR.glb"),
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", characterStaging, CancellationToken.None));
        var animationResult = importer.Import(new AssetImportContext(
            Path.Combine(project, "animations", "RPG-Character@Unarmed-Block.glb"),
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", animationStaging, CancellationToken.None));
        var characterArtifact = Assert.Single(characterResult.Artifacts,
            item => item.Key == "mesh/Mesh/0" && item.ContentType == "nico/skinned-mesh");
        var animationArtifact = Assert.Single(animationResult.Artifacts,
            item => item.Key == "animation/0" &&
                item.ContentType == "nico/skeletal-animation");
        using var characterStream = File.OpenRead(Path.Combine(
            characterStaging, characterArtifact.RelativePath));
        using var animationStream = File.OpenRead(Path.Combine(
            animationStaging, animationArtifact.RelativePath));
        var character = SkinnedMeshResource.Load(characterStream);
        var animation = SkeletalAnimationResource.Load(animationStream);

        Assert.True(HumanoidRig.TryDetect(animation.Skeleton, out var sourceRig));
        Assert.Equal(HumanoidRigPreset.Unreal, sourceRig!.Preset);
        Assert.True(HumanoidRig.TryDetect(character.Skeleton, out var targetRig));
        Assert.Equal(HumanoidRigPreset.Mixamo, targetRig!.Preset);
        Assert.Throws<InvalidOperationException>(() =>
            animation.BindTo(character.Skeleton, AnimationRetargetMode.Exact));

        var clip = Assert.Single(animation.BindTo(
            character.Skeleton, AnimationRetargetMode.Humanoid,
            character.MeshNodeTransform));
        var pose = new SkeletonPose(character.Skeleton);
        pose.Evaluate(character.Skeleton, clip, clip.Duration * 0.5f);
        var leftArm = character.Skeleton.FindJoint("mixamorig:LeftArm");
        var leftFingerEnd = character.Skeleton.FindJoint("mixamorig:LeftHandIndex4");

        Assert.NotNull(clip.Tracks[leftArm]?.Rotation);
        Assert.Null(clip.Tracks[leftFingerEnd]);
        Assert.True(IsFinite(pose.WorldTransforms[leftArm]));
    }

    /// <summary>Loads the authored locomotion set and binds all migrated RPG clips.</summary>
    [Fact]
    public void LocomotionSet_RpgSources_BindToExampleMixamoCharacter()
    {
        var project = Path.GetFullPath("../../example_game",
            Path.GetDirectoryName(GetSourceFilePath())!);
        using var setStream = File.OpenRead(Path.Combine(
            project, "models", "Locomotion.nanimset"));
        var set = AnimationSetResource.Load(setStream);
        Assert.Equal(["Idle", "Run", "Block"],
            set.Entries.Select(entry => entry.Alias));
        Assert.All(set.Entries,
            entry => Assert.Equal(AnimationRetargetMode.Humanoid, entry.Retarget));

        var expectedSources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Idle"] = "RPG-Character@Unarmed-Idle.glb",
            ["Run"] = "RPG-Character@Unarmed-Run-Forward.glb",
            ["Block"] = "RPG-Character@Unarmed-Block.glb"
        };
        var resolved = new Dictionary<AssetReference, SkeletalAnimationResource>();
        for (var index = 0; index < set.Entries.Count; index++)
        {
            var entry = set.Entries[index];
            var sourcePath = Path.Combine(
                project, "animations", expectedSources[entry.Alias]);
            Assert.Equal(ReadAssetId(sourcePath + ".meta"), entry.Source.Asset);
            resolved.Add(entry.Source, ImportAnimation(sourcePath, $"set-{entry.Alias}"));
        }
        var characterStaging = Path.Combine(_directory, "set-character");
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var characterResult = new GlbModelImporter().Import(new AssetImportContext(
            Path.Combine(project, "models", "Ch03_nonPBR.glb"),
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", characterStaging, CancellationToken.None));
        var characterArtifact = Assert.Single(characterResult.Artifacts,
            item => item.Key == "mesh/Mesh/0" && item.ContentType == "nico/skinned-mesh");
        using var characterStream = File.OpenRead(Path.Combine(
            characterStaging, characterArtifact.RelativePath));
        var character = SkinnedMeshResource.Load(characterStream);

        var clips = set.BindTo(character.Skeleton,
            reference => resolved.GetValueOrDefault(reference), character.MeshNodeTransform);

        Assert.Equal(["Idle", "Run", "Block"], clips.Select(clip => clip.Name));
        var run = clips[1];
        var hips = character.Skeleton.FindJoint("mixamorig:Hips");
        Assert.NotNull(run.Tracks[hips]?.Translation);
        var pose = new SkeletonPose(character.Skeleton);
        pose.Evaluate(character.Skeleton, run, 0f);
        var anchor = Vector3.Transform(
            pose.WorldTransforms[hips].Translation, character.MeshNodeTransform);
        for (var sample = 1; sample <= 10; sample++)
        {
            pose.Evaluate(character.Skeleton, run, run.Duration * sample / 10f);
            var position = Vector3.Transform(
                pose.WorldTransforms[hips].Translation, character.MeshNodeTransform);
            Assert.Equal(anchor.X, position.X, 3);
            Assert.Equal(anchor.Z, position.Z, 3);
        }
    }

    /// <summary>Gets this test source path independently of the test host working directory.</summary>
    /// <param name="path">Compiler-provided source path.</param>
    /// <returns>Absolute test source path.</returns>
    private static string GetSourceFilePath([CallerFilePath] string path = "") => path;

    /// <summary>Checks that every matrix component is finite.</summary>
    /// <param name="value">Matrix to inspect.</param>
    /// <returns>True when the matrix contains no NaN or infinity.</returns>
    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);

    /// <summary>Reads an asset identifier from one project metadata sidecar.</summary>
    /// <param name="path">Metadata path.</param>
    /// <returns>Parsed asset identifier.</returns>
    private static AssetId ReadAssetId(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return new AssetId(document.RootElement.GetProperty("id").GetGuid());
    }

    /// <summary>Imports and loads the first standalone animation from one real GLB.</summary>
    /// <param name="sourcePath">Animation GLB path.</param>
    /// <param name="stagingName">Unique staging directory name.</param>
    /// <returns>Loaded standalone animation resource.</returns>
    private SkeletalAnimationResource ImportAnimation(
        string sourcePath, string stagingName)
    {
        var staging = Path.Combine(_directory, stagingName);
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var result = new GlbModelImporter().Import(new AssetImportContext(sourcePath,
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", staging, CancellationToken.None));
        var artifact = Assert.Single(result.Artifacts,
            item => item.Key == "animation/0" &&
                item.ContentType == "nico/skeletal-animation");
        using var stream = File.OpenRead(Path.Combine(staging, artifact.RelativePath));
        return SkeletalAnimationResource.Load(stream);
    }

    /// <summary>Imports and loads the first skinned mesh from one real GLB.</summary>
    /// <param name="sourcePath">Model GLB path.</param>
    /// <param name="stagingName">Unique staging directory name.</param>
    /// <returns>Loaded skinned mesh resource.</returns>
    private SkinnedMeshResource ImportSkinnedMesh(string sourcePath, string stagingName)
    {
        var staging = Path.Combine(_directory, stagingName);
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var result = new GlbModelImporter().Import(new AssetImportContext(sourcePath,
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", staging, CancellationToken.None));
        var artifact = Assert.Single(result.Artifacts,
            item => item.ContentType == "nico/skinned-mesh");
        using var stream = File.OpenRead(Path.Combine(staging, artifact.RelativePath));
        return SkinnedMeshResource.Load(stream);
    }

    /// <summary>Imports indexed geometry and generates missing normals.</summary>
    [Fact]
    public void Import_MinimalTriangle_WritesVersionedMeshArtifact()
    {
        var sourcePath = Path.Combine(_directory, "triangle.glb");
        WriteMinimalGlb(sourcePath);
        var staging = Path.Combine(_directory, "staging");
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var metadata = new AssetMetadata(1, AssetId.New(), "gltf-model", settings);
        var context = new AssetImportContext(sourcePath, metadata, "editor", staging,
            CancellationToken.None);

        var result = new GlbModelImporter().Import(context);

        Assert.Equal(4, result.Artifacts.Count);
        var artifact = Assert.Single(result.Artifacts, item =>
            item.Key.StartsWith("mesh/", StringComparison.Ordinal) &&
            item.ContentType == "nico/static-mesh");
        Assert.Equal("mesh/Triangle/0", artifact.Key);
        Assert.Equal("nico/static-mesh", artifact.ContentType);
        using var reader = new BinaryReader(File.OpenRead(Path.Combine(staging,
            artifact.RelativePath)));
        Assert.Equal("NMESH001", Encoding.ASCII.GetString(reader.ReadBytes(8)));
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        Assert.Equal(3u, reader.ReadUInt32());
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(Vector3.Zero, ReadVector3(reader));
        Assert.Equal(Vector3.UnitZ, ReadVector3(reader));
        reader.BaseStream.Position += sizeof(float) * 6;
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 1f), ReadVector4(reader));
        Assert.Equal(new Vector3(1f, 0f, 0f), ReadVector3(reader));
        var meshNode = Assert.Single(result.Objects!, item =>
            item.Kind == "node" && item.ArtifactKeys is { Count: > 0 });
        Assert.Equal(new Vector3(5f, 6f, 7f), meshNode.LocalTransform!.Value.Translation);
        Assert.Equal(artifact.Key, Assert.Single(meshNode.ArtifactKeys!));
        var materialArtifact = Assert.Single(result.Artifacts, item =>
            item.ContentType == "nico/standard-material");
        using var materialStream = File.OpenRead(Path.Combine(staging,
            materialArtifact.RelativePath));
        var material = StandardMaterialAssetCodec.Load(materialStream);
        Assert.Equal(new Vector4(0.25f, 0.5f, 0.75f, 1f), material.BaseColor);
        Assert.Equal(0.2f, material.Metallic);
        Assert.Equal(0.6f, material.Roughness);
        Assert.True(material.DoubleSided);
        Assert.Equal(new AssetReference(metadata.Id, "texture/0"),
            material.BaseColorTexture);
        var textureArtifact = Assert.Single(result.Artifacts, item =>
            item.ContentType == "nico/texture2d");
        Assert.Equal("texture/0", textureArtifact.Key);
        using var textureReader = new BinaryReader(File.OpenRead(Path.Combine(staging,
            textureArtifact.RelativePath)));
        Assert.Equal("NTEX0001", Encoding.ASCII.GetString(textureReader.ReadBytes(8)));
        Assert.Equal(1u, textureReader.ReadUInt32());
        Assert.Equal(1u, textureReader.ReadUInt32());
        Assert.Equal(1u, textureReader.ReadUInt32());
        Assert.Equal(1, textureReader.ReadByte());
        Assert.Equal(4, textureReader.ReadBytes(4).Length);
    }

    /// <summary>Recognizes a collision naming convention and excludes it from visual batches.</summary>
    [Fact]
    public void Import_UcxNode_MarksCollisionObjectAndOmitsModelBatch()
    {
        var sourcePath = Path.Combine(_directory, "collision.glb");
        WriteMinimalGlb(sourcePath, "UCX_Ground");
        var staging = Path.Combine(_directory, "collision-staging");
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var context = new AssetImportContext(sourcePath,
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", staging, CancellationToken.None);

        var result = new GlbModelImporter().Import(context);

        var collision = Assert.Single(result.Objects!, item => item.Kind == "collision");
        Assert.Equal("UCX_Ground", collision.Name);
        Assert.DoesNotContain(result.Artifacts,
            item => item.Key.StartsWith("model-batch/", StringComparison.Ordinal));
        Assert.Single(collision.ArtifactKeys!);
    }

    /// <summary>Imports joint weights, inverse binds, and animation channels.</summary>
    [Fact]
    public void Import_SkinnedTriangle_WritesPlayableSkinnedMesh()
    {
        var sourcePath = Path.Combine(_directory, "skinned.glb");
        WriteSkinnedGlb(sourcePath);
        var staging = Path.Combine(_directory, "skinned-staging");
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var context = new AssetImportContext(sourcePath,
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", staging, CancellationToken.None);

        var result = new GlbModelImporter().Import(context);

        var artifact = Assert.Single(result.Artifacts,
            item => item.ContentType == "nico/skinned-mesh");
        Assert.Equal("nico/skinned-mesh", artifact.ContentType);
        Assert.NotNull(result.Objects);
        var armatureNodes = result.Objects!.Where(item => item.Kind == "node").ToArray();
        Assert.Equal(4, armatureNodes.Length);
        Assert.Equal("node/0", Assert.Single(armatureNodes,
            item => item.Name == "Helper").ParentKey);
        Assert.Equal("Rig", Assert.Single(result.Objects,
            item => item.Kind == "skeleton").Name);
        Assert.Equal("Move", Assert.Single(result.Objects,
            item => item.Kind == "animation").Name);
        using var stream = File.OpenRead(Path.Combine(staging, artifact.RelativePath));
        var resource = SkinnedMeshResource.Load(stream);
        Assert.Equal(2, resource.Skeleton.JointCount);
        Assert.Equal(1u, resource.Influences[1].Joint0);
        var animation = Assert.Single(resource.Animations);
        Assert.Equal("Move", animation.Name);
        var player = new AnimationPlayer(resource);
        player.Play();
        player.Update(0.5d);
        Assert.Equal(0.5f, player.Pose.SkinMatrices[1].M41, 5);
    }

    /// <summary>Preserves the source mesh transform that cancels inverse-bind coordinates.</summary>
    [Fact]
    public void Import_TransformedArmature_ComposesToIdentityAtBindPose()
    {
        var sourcePath = Path.Combine(_directory, "transformed-armature.glb");
        WriteTransformedArmatureGlb(sourcePath);
        var staging = Path.Combine(_directory, "transformed-staging");
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var context = new AssetImportContext(sourcePath,
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", staging, CancellationToken.None);

        var result = new GlbModelImporter().Import(context);
        var artifact = Assert.Single(result.Artifacts);
        using var stream = File.OpenRead(Path.Combine(staging, artifact.RelativePath));
        var resource = SkinnedMeshResource.Load(stream);
        var pose = new SkeletonPose(resource.Skeleton);

        Assert.Equal(0.01f, resource.MeshNodeTransform.M11, 5);
        AssertMatrixNearlyIdentity(
            pose.SkinMatrices[0] * resource.MeshNodeTransform);
    }

    /// <summary>Imports a mesh-free skin and clip as standalone skeletal animation.</summary>
    [Fact]
    public void Import_AnimationOnlyGlb_WritesBindableAnimationArtifact()
    {
        var sourcePath = Path.Combine(_directory, "idle.glb");
        WriteAnimationOnlyGlb(sourcePath);
        var staging = Path.Combine(_directory, "animation-staging");
        var settings = JsonDocument.Parse("{}").RootElement.Clone();
        var context = new AssetImportContext(sourcePath,
            new AssetMetadata(1, AssetId.New(), "gltf-model", settings),
            "editor", staging, CancellationToken.None);

        var result = new GlbModelImporter().Import(context);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("animation/0", artifact.Key);
        Assert.Equal("nico/skeletal-animation", artifact.ContentType);
        var animationObject = Assert.Single(result.Objects!, item => item.Kind == "animation");
        Assert.Equal(artifact.Key, animationObject.ArtifactKey);
        using var stream = File.OpenRead(Path.Combine(staging, artifact.RelativePath));
        var resource = SkeletalAnimationResource.Load(stream);
        Assert.Equal("Idle", Assert.Single(resource.Animations).Name);
        var target = new SkeletonResource(
        [
            new SkeletonJoint("Hips", -1, JointTransform.Identity, Matrix4x4.Identity)
        ]);
        var bound = Assert.Single(resource.BindTo(target));
        Assert.Equal(1f, bound.Tracks[0]!.Translation!.Sample(1f).Y, 5);
    }

    /// <summary>Removes temporary test data.</summary>
    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Reads one vector from the mesh artifact.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>The decoded vector.</returns>
    private static Vector3 ReadVector3(BinaryReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    /// <summary>Reads one four-component vector from the mesh artifact.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>The decoded vector.</returns>
    private static Vector4 ReadVector4(BinaryReader reader)
    {
        return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle());
    }

    /// <summary>Writes a GLB containing one indexed triangle without normals.</summary>
    /// <param name="path">Destination path.</param>
    private static void WriteMinimalGlb(string path, string? nodeName = null)
    {
        using var binaryStream = new MemoryStream();
        using (var binary = new BinaryWriter(binaryStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f })
                binary.Write(value);
            foreach (var value in new[]
                     {
                         0.2f, 0.4f, 0.6f,
                         0.2f, 0.4f, 0.6f,
                         0.2f, 0.4f, 0.6f
                     })
                binary.Write(value);
            binary.Write((ushort)0);
            binary.Write((ushort)1);
            binary.Write((ushort)2);
            binary.Write((ushort)0);
            binary.Write(Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        }
        var json = """
            {"asset":{"version":"2.0"},"scene":0,
             "scenes":[{"nodes":[0]}],
             "nodes":[{"mesh":0,"translation":[5,6,7],"scale":[2,3,4]}],
             "buffers":[{"byteLength":148}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},
                            {"buffer":0,"byteOffset":36,"byteLength":36},
                            {"buffer":0,"byteOffset":72,"byteLength":6},
                            {"buffer":0,"byteOffset":80,"byteLength":68}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
                          {"bufferView":2,"componentType":5123,"count":3,"type":"SCALAR"},
                          {"bufferView":1,"componentType":5126,"count":3,"type":"VEC3"}],
             "materials":[{"name":"Blue","doubleSided":true,
               "pbrMetallicRoughness":{"baseColorFactor":[0.25,0.5,0.75,1],
               "metallicFactor":0.2,"roughnessFactor":0.6,
               "baseColorTexture":{"index":0}}}],
             "images":[{"bufferView":3,"mimeType":"image/png"}],
             "textures":[{"source":0}],
             "meshes":[{"name":"Triangle","primitives":[{"attributes":{"POSITION":0,"COLOR_0":2},
               "indices":1,"material":0}]}]}
            """;
        if (nodeName is not null)
            json = json.Replace("\"nodes\":[{\"mesh\":0",
                $"\"nodes\":[{{\"name\":\"{nodeName}\",\"mesh\":0", StringComparison.Ordinal);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        Array.Resize(ref jsonBytes, (jsonBytes.Length + 3) & ~3);
        for (var index = Encoding.UTF8.GetByteCount(json); index < jsonBytes.Length; index++)
            jsonBytes[index] = 0x20;
        var binaryBytes = binaryStream.ToArray();
        using var output = new BinaryWriter(File.Create(path));
        output.Write(0x46546C67u);
        output.Write(2u);
        output.Write(checked((uint)(12 + 8 + jsonBytes.Length + 8 + binaryBytes.Length)));
        output.Write(checked((uint)jsonBytes.Length));
        output.Write(0x4E4F534Au);
        output.Write(jsonBytes);
        output.Write(checked((uint)binaryBytes.Length));
        output.Write(0x004E4942u);
        output.Write(binaryBytes);
    }

    /// <summary>Writes a GLB containing a two-joint skinned triangle and one clip.</summary>
    /// <param name="path">Destination path.</param>
    private static void WriteSkinnedGlb(string path)
    {
        using var binaryStream = new MemoryStream();
        using (var binary = new BinaryWriter(binaryStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f })
                binary.Write(value);
            binary.Write(new byte[] { 0, 1, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 });
            foreach (var value in new[]
                     {
                         0.5f, 0.5f, 0f, 0f,
                         1f, 0f, 0f, 0f,
                         1f, 0f, 0f, 0f
                     })
                binary.Write(value);
            binary.Write((ushort)0);
            binary.Write((ushort)1);
            binary.Write((ushort)2);
            binary.Write((ushort)0);
            WriteMatrix(binary, Matrix4x4.Identity);
            WriteMatrix(binary, Matrix4x4.CreateTranslation(-Vector3.UnitX));
            binary.Write(0f);
            binary.Write(1f);
            foreach (var value in new[] { 0.5f, 0f, 0f, 1.5f, 0f, 0f })
                binary.Write(value);
        }
        var json = """
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":264}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},
                            {"buffer":0,"byteOffset":36,"byteLength":12},
                            {"buffer":0,"byteOffset":48,"byteLength":48},
                            {"buffer":0,"byteOffset":96,"byteLength":6},
                            {"buffer":0,"byteOffset":104,"byteLength":128},
                            {"buffer":0,"byteOffset":232,"byteLength":8},
                            {"buffer":0,"byteOffset":240,"byteLength":24}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
                          {"bufferView":1,"componentType":5121,"count":3,"type":"VEC4"},
                          {"bufferView":2,"componentType":5126,"count":3,"type":"VEC4"},
                          {"bufferView":3,"componentType":5123,"count":3,"type":"SCALAR"},
                          {"bufferView":4,"componentType":5126,"count":2,"type":"MAT4"},
                          {"bufferView":5,"componentType":5126,"count":2,"type":"SCALAR"},
                          {"bufferView":6,"componentType":5126,"count":2,"type":"VEC3"}],
             "nodes":[{"name":"Root","children":[1]},
                      {"name":"Helper","translation":[0.5,0,0],"children":[2]},
                      {"name":"Child","translation":[0.5,0,0]},
                      {"name":"Character","mesh":0,"skin":0}],
             "skins":[{"name":"Rig","joints":[0,2],"inverseBindMatrices":4,"skeleton":0}],
             "meshes":[{"name":"Character","primitives":[{"attributes":{"POSITION":0,
               "JOINTS_0":1,"WEIGHTS_0":2},"indices":3}]}],
             "animations":[{"name":"Move","samplers":[{"input":5,"output":6,
               "interpolation":"LINEAR"}],"channels":[{"sampler":0,
               "target":{"node":1,"path":"translation"}}]}]}
            """;
        WriteGlb(path, json, binaryStream.ToArray());
    }

    /// <summary>Writes a mesh-free GLB containing one skeleton and animation clip.</summary>
    /// <param name="path">Destination path.</param>
    private static void WriteAnimationOnlyGlb(string path)
    {
        using var binaryStream = new MemoryStream();
        using (var binary = new BinaryWriter(binaryStream, Encoding.UTF8, leaveOpen: true))
        {
            binary.Write(0f);
            binary.Write(1f);
            foreach (var value in new[] { 0f, 0f, 0f, 0f, 1f, 0f })
                binary.Write(value);
        }
        var json = """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],
             "buffers":[{"byteLength":32}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":8},
                            {"buffer":0,"byteOffset":8,"byteLength":24}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":2,"type":"SCALAR"},
                          {"bufferView":1,"componentType":5126,"count":2,"type":"VEC3"}],
             "nodes":[{"name":"Armature","children":[1]},{"name":"Hips"}],
             "skins":[{"name":"Rig","joints":[1],"skeleton":1}],
             "animations":[{"name":"Idle","samplers":[{"input":0,"output":1,
               "interpolation":"LINEAR"}],"channels":[{"sampler":0,
               "target":{"node":1,"path":"translation"}}]}]}
            """;
        WriteGlb(path, json, binaryStream.ToArray());
    }

    /// <summary>Writes a GLB whose armature supplies unit conversion and axis correction.</summary>
    /// <param name="path">Destination path.</param>
    private static void WriteTransformedArmatureGlb(string path)
    {
        var armature = Matrix4x4.CreateScale(0.01f) *
            Matrix4x4.CreateRotationX(MathF.PI / 2f);
        Assert.True(Matrix4x4.Invert(armature, out var inverseArmature));
        using var binaryStream = new MemoryStream();
        using (var binary = new BinaryWriter(binaryStream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f })
                binary.Write(value);
            binary.Write(new byte[12]);
            for (var index = 0; index < 3; index++)
            {
                binary.Write(1f);
                binary.Write(0f);
                binary.Write(0f);
                binary.Write(0f);
            }
            binary.Write((ushort)0);
            binary.Write((ushort)1);
            binary.Write((ushort)2);
            binary.Write((ushort)0);
            WriteMatrix(binary, inverseArmature);
        }
        var json = """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[2]}],
             "buffers":[{"byteLength":168}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},
                            {"buffer":0,"byteOffset":36,"byteLength":12},
                            {"buffer":0,"byteOffset":48,"byteLength":48},
                            {"buffer":0,"byteOffset":96,"byteLength":6},
                            {"buffer":0,"byteOffset":104,"byteLength":64}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
                          {"bufferView":1,"componentType":5121,"count":3,"type":"VEC4"},
                          {"bufferView":2,"componentType":5126,"count":3,"type":"VEC4"},
                          {"bufferView":3,"componentType":5123,"count":3,"type":"SCALAR"},
                          {"bufferView":4,"componentType":5126,"count":1,"type":"MAT4"}],
             "nodes":[{"name":"Root"},{"name":"Character","mesh":0,"skin":0},
                      {"name":"Armature","rotation":[0.70710678,0,0,0.70710678],
                       "scale":[0.01,0.01,0.01],"children":[0,1]}],
             "skins":[{"name":"Armature","joints":[0],"inverseBindMatrices":4,"skeleton":0}],
             "meshes":[{"name":"Character","primitives":[{"attributes":{"POSITION":0,
               "JOINTS_0":1,"WEIGHTS_0":2},"indices":3}]}]}
            """;
        WriteGlb(path, json, binaryStream.ToArray());
    }

    /// <summary>Asserts a matrix is identity within import precision.</summary>
    /// <param name="matrix">Matrix to verify.</param>
    private static void AssertMatrixNearlyIdentity(Matrix4x4 matrix)
    {
        var expected = Matrix4x4.Identity;
        var actualValues = new[]
        {
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44
        };
        var expectedValues = new[]
        {
            expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44
        };
        for (var index = 0; index < actualValues.Length; index++)
            Assert.Equal(expectedValues[index], actualValues[index], 4);
    }

    /// <summary>Asserts two matrices are equal within animation-import precision.</summary>
    /// <param name="expected">Expected matrix.</param>
    /// <param name="actual">Actual matrix.</param>
    private static void AssertMatrixNearlyEqual(Matrix4x4 expected, Matrix4x4 actual)
    {
        var expectedValues = new[]
        {
            expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44
        };
        var actualValues = new[]
        {
            actual.M11, actual.M12, actual.M13, actual.M14,
            actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34,
            actual.M41, actual.M42, actual.M43, actual.M44
        };
        for (var index = 0; index < actualValues.Length; index++)
            Assert.Equal(expectedValues[index], actualValues[index], 3);
    }

    /// <summary>CPU-skins a real imported mesh and calculates its rendered-space bounds.</summary>
    /// <param name="mesh">Imported skinned mesh.</param>
    /// <param name="pose">Evaluated pose for the same skeleton.</param>
    /// <returns>Minimum and maximum rendered-space positions.</returns>
    private static (Vector3 Minimum, Vector3 Maximum) CalculateRenderedBounds(
        SkinnedMeshResource mesh, SkeletonPose pose)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var palette = pose.SkinMatrices;
        for (var index = 0; index < mesh.Mesh.Vertices.Length; index++)
        {
            var source = mesh.Mesh.Vertices[index].Position;
            var influence = mesh.Influences[index];
            var skinned =
                Vector3.Transform(source, palette[(int)influence.Joint0]) *
                    influence.Weights.X +
                Vector3.Transform(source, palette[(int)influence.Joint1]) *
                    influence.Weights.Y +
                Vector3.Transform(source, palette[(int)influence.Joint2]) *
                    influence.Weights.Z +
                Vector3.Transform(source, palette[(int)influence.Joint3]) *
                    influence.Weights.W;
            var rendered = Vector3.Transform(skinned, mesh.MeshNodeTransform);
            minimum = Vector3.Min(minimum, rendered);
            maximum = Vector3.Max(maximum, rendered);
        }
        return (minimum, maximum);
    }

    /// <summary>Writes one row-vector matrix using glTF's equivalent column-major sequence.</summary>
    /// <param name="writer">Binary output.</param>
    /// <param name="matrix">Row-vector matrix.</param>
    private static void WriteMatrix(BinaryWriter writer, Matrix4x4 matrix)
    {
        writer.Write(matrix.M11); writer.Write(matrix.M12); writer.Write(matrix.M13); writer.Write(matrix.M14);
        writer.Write(matrix.M21); writer.Write(matrix.M22); writer.Write(matrix.M23); writer.Write(matrix.M24);
        writer.Write(matrix.M31); writer.Write(matrix.M32); writer.Write(matrix.M33); writer.Write(matrix.M34);
        writer.Write(matrix.M41); writer.Write(matrix.M42); writer.Write(matrix.M43); writer.Write(matrix.M44);
    }

    /// <summary>Writes padded JSON and binary chunks into a GLB container.</summary>
    /// <param name="path">Destination path.</param>
    /// <param name="json">glTF JSON.</param>
    /// <param name="binary">Binary buffer.</param>
    private static void WriteGlb(string path, string json, byte[] binary)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        Array.Resize(ref jsonBytes, (jsonBytes.Length + 3) & ~3);
        for (var index = Encoding.UTF8.GetByteCount(json); index < jsonBytes.Length; index++)
            jsonBytes[index] = 0x20;
        Array.Resize(ref binary, (binary.Length + 3) & ~3);
        using var output = new BinaryWriter(File.Create(path));
        output.Write(0x46546C67u);
        output.Write(2u);
        output.Write(checked((uint)(12 + 8 + jsonBytes.Length + 8 + binary.Length)));
        output.Write(checked((uint)jsonBytes.Length));
        output.Write(0x4E4F534Au);
        output.Write(jsonBytes);
        output.Write(checked((uint)binary.Length));
        output.Write(0x004E4942u);
        output.Write(binary);
    }
}
