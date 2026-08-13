using System.Numerics;

namespace Engine.Graphics;

/// <summary>Builds editable colored terrain surfaces from persistent height grids.</summary>
public static class TerrainMeshBuilder
{
    private static readonly Vector3 LowColor = new(0.08f, 0.24f, 0.06f);
    private static readonly Vector3 MidColor = new(0.16f, 0.46f, 0.11f);
    private static readonly Vector3 HighColor = new(0.48f, 0.36f, 0.16f);

    /// <summary>Builds row-major non-indexed triangles suitable for dynamic mesh updates.</summary>
    /// <param name="terrain">Height samples to render.</param>
    /// <param name="horizontalSize">Local X and Z surface dimensions.</param>
    /// <param name="heightScale">Local height represented by a normalized sample of one.</param>
    /// <param name="center">Optional node-local surface offset.</param>
    /// <returns>Colored terrain triangles with stable topology.</returns>
    public static Vertex[] BuildVertices(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        Vector3 center = default)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        var vertices = new Vertex[GetVertexCount(terrain)];
        FillVertices(terrain, horizontalSize, heightScale, vertices, center);
        return vertices;
    }

    /// <summary>Gets the stable non-indexed vertex count for one terrain grid.</summary>
    /// <param name="terrain">Height grid whose topology is counted.</param>
    /// <returns>Six vertices per terrain quad.</returns>
    public static int GetVertexCount(TerrainResource terrain)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        return checked((terrain.Width - 1) * (terrain.Depth - 1) * 6);
    }

    /// <summary>Rebuilds a terrain surface into caller-owned reusable vertex storage.</summary>
    /// <param name="terrain">Height samples to render.</param>
    /// <param name="horizontalSize">Local X and Z surface dimensions.</param>
    /// <param name="heightScale">Local height represented by a normalized sample of one.</param>
    /// <param name="vertices">Exact-sized stable topology destination.</param>
    /// <param name="center">Optional node-local surface offset.</param>
    public static void FillVertices(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        Vertex[] vertices,
        Vector3 center = default)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(vertices);
        ValidateDimensions(horizontalSize, heightScale, center);
        if (vertices.Length != GetVertexCount(terrain))
            throw new ArgumentException(
                "Terrain vertex storage does not match the grid topology.", nameof(vertices));
        var vertexIndex = 0;
        for (var z = 0; z < terrain.Depth - 1; z++)
        {
            for (var x = 0; x < terrain.Width - 1; x++)
            {
                var a = CreateVertex(terrain, horizontalSize, heightScale, center, x, z);
                var b = CreateVertex(terrain, horizontalSize, heightScale, center, x + 1, z);
                var c = CreateVertex(terrain, horizontalSize, heightScale, center, x + 1, z + 1);
                var d = CreateVertex(terrain, horizontalSize, heightScale, center, x, z + 1);
                vertices[vertexIndex++] = a;
                vertices[vertexIndex++] = d;
                vertices[vertexIndex++] = c;
                vertices[vertexIndex++] = a;
                vertices[vertexIndex++] = c;
                vertices[vertexIndex++] = b;
            }
        }
    }

    /// <summary>Computes exact local bounds for one terrain surface.</summary>
    /// <param name="terrain">Height samples to bound.</param>
    /// <param name="horizontalSize">Local X and Z surface dimensions.</param>
    /// <param name="heightScale">Local height represented by a normalized sample of one.</param>
    /// <param name="center">Optional node-local surface offset.</param>
    /// <returns>Object-space terrain bounds.</returns>
    public static MeshBounds GetBounds(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        Vector3 center = default)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ValidateDimensions(horizontalSize, heightScale, center);
        var minimumHeight = terrain.GetHeight(0, 0);
        var maximumHeight = minimumHeight;
        for (var z = 0; z < terrain.Depth; z++)
        {
            for (var x = 0; x < terrain.Width; x++)
            {
                var height = terrain.GetHeight(x, z);
                minimumHeight = MathF.Min(minimumHeight, height);
                maximumHeight = MathF.Max(maximumHeight, height);
            }
        }
        return new MeshBounds(
            center + new Vector3(-horizontalSize.X * 0.5f, minimumHeight * heightScale,
                -horizontalSize.Y * 0.5f),
            center + new Vector3(horizontalSize.X * 0.5f, maximumHeight * heightScale,
                horizontalSize.Y * 0.5f));
    }

    /// <summary>Creates one positioned and height-tinted terrain vertex.</summary>
    /// <param name="terrain">Source grid.</param>
    /// <param name="horizontalSize">Local X and Z dimensions.</param>
    /// <param name="heightScale">Local height scale.</param>
    /// <param name="center">Node-local surface offset.</param>
    /// <param name="x">Sample column.</param>
    /// <param name="z">Sample row.</param>
    /// <returns>Colored surface vertex.</returns>
    private static Vertex CreateVertex(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        Vector3 center,
        int x,
        int z)
    {
        var u = x / (float)(terrain.Width - 1);
        var v = z / (float)(terrain.Depth - 1);
        var height = terrain.GetHeight(x, z);
        var color = height <= 0.5f
            ? Vector3.Lerp(LowColor, MidColor, Math.Clamp(height * 2f, 0f, 1f))
            : Vector3.Lerp(MidColor, HighColor, Math.Clamp(height * 2f - 1f, 0f, 1f));
        return new Vertex(center + new Vector3(
            (u - 0.5f) * horizontalSize.X,
            height * heightScale,
            (v - 0.5f) * horizontalSize.Y), color);
    }

    /// <summary>Validates finite positive terrain dimensions.</summary>
    /// <param name="horizontalSize">Local X and Z dimensions.</param>
    /// <param name="heightScale">Local height scale.</param>
    /// <param name="center">Node-local surface offset.</param>
    private static void ValidateDimensions(
        Vector2 horizontalSize,
        float heightScale,
        Vector3 center)
    {
        if (!float.IsFinite(horizontalSize.X) || !float.IsFinite(horizontalSize.Y) ||
            horizontalSize.X <= 0f || horizontalSize.Y <= 0f)
            throw new ArgumentOutOfRangeException(nameof(horizontalSize));
        if (!float.IsFinite(heightScale) || heightScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(heightScale));
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) ||
            !float.IsFinite(center.Z))
            throw new ArgumentOutOfRangeException(nameof(center));
    }
}
