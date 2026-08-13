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

    /// <summary>Returns independently owned sample storage for editor documents.</summary>
    [Fact]
    public void CopyHeights_MutatedCopy_DoesNotChangeResource()
    {
        var terrain = new TerrainResource(2, 2, [0f, 0.25f, 0.5f, 1f]);

        var copy = terrain.CopyHeights();
        copy[0] = 1f;

        Assert.Equal(0f, terrain.GetHeight(0, 0));
    }
}
