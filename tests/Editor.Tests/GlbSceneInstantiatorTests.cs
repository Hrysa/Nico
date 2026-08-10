using System.Numerics;
using Engine.Assets;
using Engine.Core;
using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

/// <summary>Verifies imported GLB nodes become a complete transformed scene hierarchy.</summary>
public sealed class GlbSceneInstantiatorTests
{
    /// <summary>Prefers optimized static batches over hundreds of node primitive resources.</summary>
    [Fact]
    public void Create_StaticModelBatches_UsesOnlyBatchRenderables()
    {
        var assetId = AssetId.New();
        var artifacts = new AssetArtifact[]
        {
            new("mesh/tree/0", "nico/static-mesh", "meshes/tree.nmesh"),
            new("model-batch/0", "nico/static-mesh", "meshes/batch-0.nmesh"),
            new("model-batch/1", "nico/static-mesh", "meshes/batch-1.nmesh")
        };
        var objects = new AssetImportObject[]
        {
            new("node/0", "Tree", "node", ArtifactKeys: ["mesh/tree/0"])
        };
        var outcome = new AssetImportOutcome(assetId, "fingerprint", "artifacts",
            false, true, artifacts, [], [], objects);

        var result = GlbSceneInstantiator.Create("Models/Forest.glb", assetId, outcome);

        Assert.Equal(2, result.Meshes.Count);
        Assert.All(result.Meshes, mesh =>
            Assert.StartsWith("model-batch/", mesh.Mesh.SubAsset));
    }

    /// <summary>Creates every primitive and preserves repeated-mesh node transforms.</summary>
    [Fact]
    public void Create_MultipleNodesAndPrimitives_PreservesHierarchyAndInstances()
    {
        var assetId = AssetId.New();
        var artifacts = new AssetArtifact[]
        {
            new("mesh/shared/0", "nico/static-mesh", "meshes/shared-0.nmesh"),
            new("mesh/shared/1", "nico/static-mesh", "meshes/shared-1.nmesh")
        };
        var objects = new AssetImportObject[]
        {
            new("node/0", "Parent", "node", LocalTransform:
                Matrix4x4.CreateTranslation(10f, 0f, 0f)),
            new("node/1", "First", "node", "node/0", LocalTransform:
                Matrix4x4.CreateTranslation(2f, 0f, 0f),
                ArtifactKeys: ["mesh/shared/0", "mesh/shared/1"]),
            new("node/2", "Second", "node", LocalTransform:
                Matrix4x4.CreateTranslation(-4f, 0f, 0f),
                ArtifactKeys: ["mesh/shared/0"])
        };
        var outcome = new AssetImportOutcome(assetId, "fingerprint", "artifacts",
            false, true, artifacts, [], [], objects);

        var result = GlbSceneInstantiator.Create("Models/Valley.glb", assetId, outcome);

        Assert.Equal("Valley", result.Root.Name);
        Assert.Equal(3, result.Meshes.Count);
        Assert.Equal(new Vector3(12f, 0f, 0f), result.Meshes[0].GetWorldPosition());
        Assert.Equal(new Vector3(12f, 0f, 0f), result.Meshes[1].GetWorldPosition());
        Assert.Equal(new Vector3(-4f, 0f, 0f), result.Meshes[2].GetWorldPosition());
        Assert.Equal("mesh/shared/0", result.Meshes[0].Mesh.SubAsset);
        Assert.Equal("mesh/shared/1", result.Meshes[1].Mesh.SubAsset);
        Assert.Equal("mesh/shared/0", result.Meshes[2].Mesh.SubAsset);
    }
}
