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

    /// <summary>Builds one indexed static mesh from one terrain height grid.</summary>
    /// <param name="terrain">Height samples to render.</param>
    /// <param name="horizontalSize">Local X and Z surface dimensions.</param>
    /// <param name="heightScale">Local height represented by a normalized sample of one.</param>
    /// <param name="center">Optional node-local surface offset.</param>
    /// <returns>Static terrain geometry ready for indexed material shading.</returns>
    public static StaticMeshResource BuildStaticMesh(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        Vector3 center = default)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ValidateDimensions(horizontalSize, heightScale, center);
        var vertices = new ModelVertex[terrain.Width * terrain.Depth];
        var normals = new Vector3[vertices.Length];
        for (var z = 0; z < terrain.Depth; z++)
        {
            for (var x = 0; x < terrain.Width; x++)
            {
                var positionIndex = z * terrain.Width + x;
                var position = CreatePosition(terrain, horizontalSize, heightScale, center, x, z);
                var color = CreateTerrainColor(terrain.GetHeight(x, z));
                var uv = CreateTerrainUv(terrain, x, z);
                vertices[positionIndex] = new ModelVertex(
                    position, Vector3.UnitY, uv, new Vector4(1f, 0f, 0f, 1f),
                    new Vector4(color, 1f));
            }
        }
        var indices = new uint[(terrain.Width - 1) * (terrain.Depth - 1) * 6];
        var index = 0;
        for (var z = 0; z < terrain.Depth - 1; z++)
        {
            for (var x = 0; x < terrain.Width - 1; x++)
            {
                var topLeft = z * terrain.Width + x;
                var topRight = topLeft + 1;
                var bottomRight = topRight + terrain.Width;
                var bottomLeft = topLeft + terrain.Width;

                var topLeftPosition = vertices[topLeft].Position;
                var topRightPosition = vertices[topRight].Position;
                var bottomRightPosition = vertices[bottomRight].Position;
                var bottomLeftPosition = vertices[bottomLeft].Position;
                var triangle1Normal = Vector3.Cross(
                    bottomLeftPosition - topLeftPosition,
                    bottomRightPosition - topLeftPosition);
                var triangle2Normal = Vector3.Cross(
                    bottomRightPosition - topLeftPosition,
                    topRightPosition - topLeftPosition);

                AddNormal(ref normals[topLeft], triangle1Normal);
                AddNormal(ref normals[topLeft], triangle2Normal);
                AddNormal(ref normals[bottomLeft], triangle1Normal);
                AddNormal(ref normals[bottomRight], triangle1Normal);
                AddNormal(ref normals[bottomRight], triangle2Normal);

                indices[index++] = (uint)topLeft;
                indices[index++] = (uint)bottomLeft;
                indices[index++] = (uint)bottomRight;
                indices[index++] = (uint)topLeft;
                indices[index++] = (uint)bottomRight;
                indices[index++] = (uint)topRight;
            }
        }
        for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
        {
            var normal = normals[vertexIndex];
            vertices[vertexIndex] = new ModelVertex(
                vertices[vertexIndex].Position,
                normal.LengthSquared() > 0f ? Vector3.Normalize(normal) : Vector3.UnitY,
                vertices[vertexIndex].TexCoord,
                vertices[vertexIndex].Tangent,
                vertices[vertexIndex].Color);
        }
        return new StaticMeshResource(
            vertices, indices, [new Submesh(0, (uint)indices.Length, 0)]);
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

    /// <summary>Creates one positioned and height-tinted dynamic terrain vertex.</summary>
    /// <param name="terrain">Source grid.</param>
    /// <param name="horizontalSize">Local X and Z dimensions.</param>
    /// <param name="heightScale">Local height scale.</param>
    /// <param name="center">Node-local surface offset.</param>
    /// <param name="x">Sample column.</param>
    /// <param name="z">Sample row.</param>
    /// <returns>Colored dynamic surface vertex.</returns>
    private static Vertex CreateVertex(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        Vector3 center,
        int x,
        int z)
    {
        var position = CreatePosition(terrain, horizontalSize, heightScale, center, x, z);
        var color = CreateTerrainColor(terrain.GetHeight(x, z));
        return new Vertex(position, color);
    }

    /// <summary>Gets terrain-space UV at one sample index.</summary>
    /// <param name="terrain">Source grid.</param>
    /// <param name="x">Sample column.</param>
    /// <param name="z">Sample row.</param>
    /// <returns>Terrain UV coordinate.</returns>
    private static Vector2 CreateTerrainUv(TerrainResource terrain, int x, int z) => new(
        x / (float)(terrain.Width - 1),
        z / (float)(terrain.Depth - 1));

    /// <summary>Gets terrain-space position at one sample index.</summary>
    /// <param name="terrain">Source grid.</param>
    /// <param name="horizontalSize">Local X and Z surface dimensions.</param>
    /// <param name="heightScale">Local height represented by a normalized sample of one.</param>
    /// <param name="center">Node-local surface offset.</param>
    /// <param name="x">Sample column.</param>
    /// <param name="z">Sample row.</param>
    /// <returns>Object-space sample position.</returns>
    private static Vector3 CreatePosition(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        Vector3 center,
        int x,
        int z)
    {
        var u = x / (float)(terrain.Width - 1);
        var v = z / (float)(terrain.Depth - 1);
        return center + new Vector3(
            (u - 0.5f) * horizontalSize.X,
            terrain.GetHeight(x, z) * heightScale,
            (v - 0.5f) * horizontalSize.Y);
    }

    /// <summary>Computes one vertex color from a normalized terrain sample.</summary>
    /// <param name="height">Normalized height sample.</param>
    /// <returns>RGB terrain tint.</returns>
    private static Vector3 CreateTerrainColor(float height) =>
        height <= 0.5f
            ? Vector3.Lerp(LowColor, MidColor, Math.Clamp(height * 2f, 0f, 1f))
            : Vector3.Lerp(MidColor, HighColor, Math.Clamp(height * 2f - 1f, 0f, 1f));

    /// <summary>Adds a normal contribution during accumulation.</summary>
    /// <param name="target">Target normal accumulator.</param>
    /// <param name="contribution">Triangle normal contribution.</param>
    private static void AddNormal(ref Vector3 target, Vector3 contribution)
    {
        target += contribution;
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
