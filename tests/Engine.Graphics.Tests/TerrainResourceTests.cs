using Engine.Graphics;
using System.Numerics;
using Xunit;

namespace Engine.Graphics.Tests;

public sealed class TerrainResourceTests
{
    /// <summary>Round-trips the versioned terrain artifact and preserves bilinear samples.</summary>
    [Fact]
    public void SaveAndLoad_HeightGrid_PreservesDimensionsAndSamples()
    {
        var source = new TerrainResource(2, 2, [0f, 1f, 0.5f, 0.25f]);
        using var stream = new MemoryStream();

        source.Save(stream);
        stream.Position = 0;
        var loaded = TerrainResource.Load(stream);

        Assert.Equal(2, loaded.Width);
        Assert.Equal(2, loaded.Depth);
        Assert.Equal(1f, loaded.GetHeight(1, 0));
        Assert.Equal(0.4375f, loaded.Sample(0.5f, 0.5f), 5);
    }

    /// <summary>Rejects malformed artifact signatures instead of accepting arbitrary data.</summary>
    [Fact]
    public void Load_InvalidSignature_ThrowsInvalidDataException()
    {
        using var stream = new MemoryStream(new byte[32]);

        Assert.Throws<InvalidDataException>(() => TerrainResource.Load(stream));
    }

    /// <summary>Maps boundary sample edits to only the neighboring native terrain chunks.</summary>
    [Fact]
    public void GetDirtyChunkRegions_SampleOnBoundary_ReturnsTouchingChunks()
    {
        var terrain = new TerrainResource(130, 2, new float[260]);

        var all = terrain.GetChunkRegions(64);
        var dirty = terrain.GetDirtyChunkRegions(64, 0, 64, 1, 64);

        Assert.Equal(3, all.Length);
        Assert.Equal(2, dirty.Length);
        Assert.Equal(0, dirty[0].StartX);
        Assert.Equal(64, dirty[1].StartX);
    }

    /// <summary>Builds stable upward-wound triangles and exact scaled height bounds.</summary>
    [Fact]
    public void TerrainMeshBuilder_NonSquareGrid_BuildsSurfaceAndBounds()
    {
        var terrain = new TerrainResource(3, 2,
            [0f, 0.25f, 0.5f, 0.5f, 0.75f, 1f]);

        var vertices = TerrainMeshBuilder.BuildVertices(
            terrain, new Vector2(8f, 4f), 3f);
        var bounds = TerrainMeshBuilder.GetBounds(
            terrain, new Vector2(8f, 4f), 3f);

        Assert.Equal(12, vertices.Length);
        Assert.Equal(new Vector3(-4f, 0f, -2f), bounds.Minimum);
        Assert.Equal(new Vector3(4f, 3f, 2f), bounds.Maximum);
        Assert.Equal(new Vector3(-4f, 0f, -2f), vertices[0].Position);
        Assert.Equal(new Vector3(-4f, 1.5f, 2f), vertices[1].Position);
        var normal = Vector3.Cross(
            vertices[1].Position - vertices[0].Position,
            vertices[2].Position - vertices[0].Position);
        Assert.True(normal.Y > 0f);
    }

    /// <summary>Rebuilds a dirty indexed region to match a complete terrain rebuild.</summary>
    [Fact]
    public void UpdateStaticMeshVertices_DirtyRegion_MatchesFullRebuild()
    {
        var terrain = new TerrainResource(5, 5, new float[25]);
        var size = new Vector2(8f, 8f);
        var initial = TerrainMeshBuilder.BuildStaticMesh(terrain, size, 3f);
        var heights = terrain.CopyHeights();
        heights[2 * terrain.Width + 2] = 1f;
        terrain.UpdateHeights(heights);

        TerrainMeshBuilder.UpdateStaticMeshVertices(
            terrain, size, 3f, default, true, initial.Vertices,
            1, 1, 3, 3);
        var rebuilt = TerrainMeshBuilder.BuildStaticMesh(terrain, size, 3f);

        Assert.Equal(rebuilt.Vertices, initial.Vertices);
        Assert.Equal(rebuilt.Indices, initial.Indices);
    }

    /// <summary>Returns independently owned sample storage for editor documents.</summary>
    [Fact]
    public void CopyHeights_MutatedCopy_DoesNotChangeResource()
    {
        var terrain = new TerrainResource(2, 2, [0f, 0.25f, 0.5f, 1f]);

        var copy = terrain.CopyHeights();
        copy[0] = 1f;

        Assert.Equal(0f, terrain.GetHeight(0, 0));
    }

    /// <summary>Refines one local quad with stitched neighbors and coarsens it non-destructively.</summary>
    [Fact]
    public void LocalRefinement_BuildStaticMesh_UsesCrackFreeAdaptiveTopology()
    {
        var terrain = new TerrainResource(3, 3, new float[9]);
        var baseMesh = TerrainMeshBuilder.BuildStaticMesh(
            terrain, new Vector2(2f, 2f), 1f);

        Assert.True(terrain.SetQuadRefined(0, 0, true));
        var refinedMesh = TerrainMeshBuilder.BuildStaticMesh(
            terrain, new Vector2(2f, 2f), 1f);

        Assert.Equal(1, terrain.RefinedQuadCount);
        Assert.Equal(16, terrain.GetActiveSamples().Length);
        Assert.True(refinedMesh.Vertices.Length > baseMesh.Vertices.Length);
        Assert.True(refinedMesh.Indices.Length > baseMesh.Indices.Length);
        AssertMeshHasNoInternalBoundaryCracks(refinedMesh);

        Assert.True(terrain.SetQuadRefined(0, 0, false));
        var coarsenedMesh = TerrainMeshBuilder.BuildStaticMesh(
            terrain, new Vector2(2f, 2f), 1f);
        Assert.Equal(0, terrain.RefinedQuadCount);
        Assert.Equal(baseMesh.Vertices.Length, coarsenedMesh.Vertices.Length);
        Assert.Equal(baseMesh.Indices.Length, coarsenedMesh.Indices.Length);
    }

    /// <summary>Persists local density and retained detail heights in the current terrain format.</summary>
    [Fact]
    public void SaveAndLoad_LocalRefinement_PreservesDetailSample()
    {
        var terrain = new TerrainResource(3, 3, new float[9]);
        terrain.SetQuadRefined(0, 0, true);
        var center = new TerrainSamplePoint(1, 1);
        terrain.SetSampleHeight(center, 2.5f);
        using var stream = new MemoryStream();

        terrain.Save(stream);
        stream.Position = 0;
        var loaded = TerrainResource.Load(stream);

        Assert.True(loaded.IsQuadRefined(0, 0));
        Assert.Equal(2.5f, loaded.GetSampleHeight(center));
        Assert.Equal(terrain.GetActiveSamples().Length, loaded.GetActiveSamples().Length);
    }

    /// <summary>Verifies every non-boundary adaptive mesh edge is shared by two triangles.</summary>
    /// <param name="mesh">Adaptive mesh to inspect.</param>
    private static void AssertMeshHasNoInternalBoundaryCracks(StaticMeshResource mesh)
    {
        var edges = new Dictionary<(uint Minimum, uint Maximum), int>();
        for (var index = 0; index < mesh.Indices.Length; index += 3)
        {
            AddEdge(edges, mesh.Indices[index], mesh.Indices[index + 1]);
            AddEdge(edges, mesh.Indices[index + 1], mesh.Indices[index + 2]);
            AddEdge(edges, mesh.Indices[index + 2], mesh.Indices[index]);
        }
        foreach (var pair in edges)
        {
            var first = mesh.Vertices[pair.Key.Minimum].TexCoord;
            var second = mesh.Vertices[pair.Key.Maximum].TexCoord;
            var boundary = first.X == 0f && second.X == 0f ||
                first.X == 1f && second.X == 1f ||
                first.Y == 0f && second.Y == 0f ||
                first.Y == 1f && second.Y == 1f;
            Assert.Equal(boundary ? 1 : 2, pair.Value);
        }
    }

    /// <summary>Adds one normalized undirected mesh edge occurrence.</summary>
    /// <param name="edges">Occurrence map.</param>
    /// <param name="first">First vertex index.</param>
    /// <param name="second">Second vertex index.</param>
    private static void AddEdge(
        Dictionary<(uint Minimum, uint Maximum), int> edges,
        uint first,
        uint second)
    {
        var key = first < second ? (first, second) : (second, first);
        edges.TryGetValue(key, out var count);
        edges[key] = count + 1;
    }
}
