using System.Numerics;

namespace Engine.Graphics;

/// <summary>Pairs adaptive terrain geometry with its stable active-sample vertex mapping.</summary>
/// <param name="Mesh">Indexed crack-free terrain mesh.</param>
/// <param name="Samples">Source sample for every mesh vertex.</param>
public sealed record TerrainStaticMeshData(
    StaticMeshResource Mesh,
    TerrainSamplePoint[] Samples);

/// <summary>Builds editable colored terrain surfaces from persistent height grids.</summary>
public static class TerrainMeshBuilder
{
    private static readonly Vector3 LowColor = new(0.08f, 0.24f, 0.06f);
    private static readonly Vector3 MidColor = new(0.16f, 0.46f, 0.11f);
    private static readonly Vector3 HighColor = new(0.48f, 0.36f, 0.16f);

    /// <summary>Builds row-major non-indexed triangles suitable for dynamic mesh updates.</summary>
    /// <param name="terrain">Height samples to render.</param>
    /// <param name="horizontalSize">Local X and Z surface dimensions.</param>
    /// <param name="heightScale">Local units represented by a height sample of one.</param>
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
    /// <param name="heightScale">Local units represented by a height sample of one.</param>
    /// <param name="center">Optional node-local surface offset.</param>
    /// <param name="tintByHeight">Whether vertex colors apply the procedural height tint.</param>
    /// <returns>Static terrain geometry ready for indexed material shading.</returns>
    public static StaticMeshResource BuildStaticMesh(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        Vector3 center = default,
        bool tintByHeight = true)
    {
        return BuildTerrainStaticMesh(
            terrain, horizontalSize, heightScale, center, tintByHeight).Mesh;
    }

    /// <summary>Builds adaptive terrain geometry and its active-sample vertex mapping.</summary>
    /// <param name="terrain">Current terrain samples and local refinement flags.</param>
    /// <param name="horizontalSize">Local X and Z surface dimensions.</param>
    /// <param name="heightScale">Local units represented by one height sample.</param>
    /// <param name="center">Optional node-local surface offset.</param>
    /// <param name="tintByHeight">Whether vertex colors apply height tint.</param>
    /// <returns>Crack-free indexed geometry and source sample mapping.</returns>
    public static TerrainStaticMeshData BuildTerrainStaticMesh(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        Vector3 center = default,
        bool tintByHeight = true)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ValidateDimensions(horizontalSize, heightScale, center);
        var samples = terrain.GetActiveSamples().ToArray();
        var vertices = new ModelVertex[samples.Length];
        var lookup = new int[checked(terrain.FineWidth * terrain.FineDepth)];
        Array.Fill(lookup, -1);
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            lookup[sample.FineZ * terrain.FineWidth + sample.FineX] = index;
        }
        var indices = new List<uint>(
            checked((terrain.Width - 1) * (terrain.Depth - 1) * 6));
        for (var z = 0; z < terrain.Depth - 1; z++)
        {
            for (var x = 0; x < terrain.Width - 1; x++)
                AppendQuadIndices(terrain, lookup, indices, x, z);
        }
        var indexArray = indices.ToArray();
        if (terrain.RefinedQuadCount == 0)
        {
            UpdateStaticMeshVertices(terrain, horizontalSize, heightScale, center,
                tintByHeight, vertices, 0, 0, terrain.Width - 1, terrain.Depth - 1);
        }
        else
        {
            UpdateStaticMeshVertices(terrain, horizontalSize, heightScale, center,
                tintByHeight, samples, indexArray, vertices);
        }
        return new TerrainStaticMeshData(
            new StaticMeshResource(vertices, indexArray,
                [new Submesh(0, (uint)indexArray.Length, 0)]),
            samples);
    }

    /// <summary>Rebuilds every active adaptive vertex while retaining topology and GPU identity.</summary>
    /// <param name="terrain">Current terrain samples.</param>
    /// <param name="horizontalSize">Local X and Z dimensions.</param>
    /// <param name="heightScale">Local units represented by one height sample.</param>
    /// <param name="center">Node-local surface offset.</param>
    /// <param name="tintByHeight">Whether vertex colors apply height tint.</param>
    /// <param name="samples">Stable source sample for every vertex.</param>
    /// <param name="indices">Stable adaptive triangle indices.</param>
    /// <param name="vertices">Reusable complete vertex array.</param>
    public static void UpdateStaticMeshVertices(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        Vector3 center,
        bool tintByHeight,
        TerrainSamplePoint[] samples,
        uint[] indices,
        ModelVertex[] vertices)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(vertices);
        ValidateDimensions(horizontalSize, heightScale, center);
        if (samples.Length != vertices.Length)
            throw new ArgumentException("Terrain sample mapping does not match vertices.",
                nameof(samples));
        for (var index = 0; index < vertices.Length; index++)
        {
            var sample = samples[index];
            var height = terrain.GetSampleHeight(sample);
            var color = tintByHeight ? CreateTerrainColor(height) : Vector3.One;
            vertices[index] = new ModelVertex(
                CreatePosition(terrain, horizontalSize, heightScale, center, sample),
                Vector3.Zero,
                new Vector2(
                    sample.FineX / (float)(terrain.FineWidth - 1),
                    sample.FineZ / (float)(terrain.FineDepth - 1)),
                new Vector4(1f, 0f, 0f, 1f),
                new Vector4(color, 1f));
        }
        for (var index = 0; index < indices.Length; index += 3)
        {
            var first = checked((int)indices[index]);
            var second = checked((int)indices[index + 1]);
            var third = checked((int)indices[index + 2]);
            var normal = Vector3.Cross(
                vertices[second].Position - vertices[first].Position,
                vertices[third].Position - vertices[first].Position);
            vertices[first].Normal += normal;
            vertices[second].Normal += normal;
            vertices[third].Normal += normal;
        }
        for (var index = 0; index < vertices.Length; index++)
        {
            var normal = vertices[index].Normal;
            vertices[index].Normal = normal.LengthSquared() > float.Epsilon
                ? Vector3.Normalize(normal) : Vector3.UnitY;
        }
    }

    /// <summary>Rebuilds one inclusive rectangular range of indexed terrain vertices.</summary>
    /// <param name="terrain">Current terrain height samples.</param>
    /// <param name="horizontalSize">Local X and Z surface dimensions.</param>
    /// <param name="heightScale">Local units represented by a height sample of one.</param>
    /// <param name="center">Node-local surface offset.</param>
    /// <param name="tintByHeight">Whether vertex colors apply procedural height tint.</param>
    /// <param name="vertices">Reusable complete indexed vertex storage.</param>
    /// <param name="minimumX">First sample column to rebuild.</param>
    /// <param name="minimumZ">First sample row to rebuild.</param>
    /// <param name="maximumX">Last sample column to rebuild.</param>
    /// <param name="maximumZ">Last sample row to rebuild.</param>
    public static void UpdateStaticMeshVertices(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        Vector3 center,
        bool tintByHeight,
        ModelVertex[] vertices,
        int minimumX,
        int minimumZ,
        int maximumX,
        int maximumZ)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(vertices);
        ValidateDimensions(horizontalSize, heightScale, center);
        if (vertices.Length != checked(terrain.Width * terrain.Depth))
            throw new ArgumentException("Terrain vertex storage has the wrong size.",
                nameof(vertices));
        if ((uint)minimumX >= (uint)terrain.Width ||
            (uint)maximumX >= (uint)terrain.Width ||
            (uint)minimumZ >= (uint)terrain.Depth ||
            (uint)maximumZ >= (uint)terrain.Depth ||
            minimumX > maximumX || minimumZ > maximumZ)
            throw new ArgumentOutOfRangeException(nameof(minimumX));
        for (var z = minimumZ; z <= maximumZ; z++)
        {
            for (var x = minimumX; x <= maximumX; x++)
            {
                var position = CreatePosition(
                    terrain, horizontalSize, heightScale, center, x, z);
                var color = tintByHeight
                    ? CreateTerrainColor(terrain.GetHeight(x, z)) : Vector3.One;
                vertices[z * terrain.Width + x] = new ModelVertex(
                    position,
                    CreateNormal(terrain, horizontalSize, heightScale, x, z),
                    CreateTerrainUv(terrain, x, z),
                    new Vector4(1f, 0f, 0f, 1f),
                    new Vector4(color, 1f));
            }
        }
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
    /// <param name="heightScale">Local units represented by a height sample of one.</param>
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
    /// <param name="heightScale">Local units represented by a height sample of one.</param>
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
        var samples = terrain.GetActiveSamples();
        var minimumHeight = terrain.GetSampleHeight(samples[0]);
        var maximumHeight = minimumHeight;
        for (var index = 1; index < samples.Length; index++)
        {
            var height = terrain.GetSampleHeight(samples[index]);
            minimumHeight = MathF.Min(minimumHeight, height);
            maximumHeight = MathF.Max(maximumHeight, height);
        }
        return new MeshBounds(
            center + new Vector3(-horizontalSize.X * 0.5f, minimumHeight * heightScale,
                -horizontalSize.Y * 0.5f),
            center + new Vector3(horizontalSize.X * 0.5f, maximumHeight * heightScale,
                horizontalSize.Y * 0.5f));
    }

    /// <summary>Appends one base quad's refined or stitched adaptive triangles.</summary>
    /// <param name="terrain">Adaptive terrain topology.</param>
    /// <param name="lookup">Fine-lattice coordinate to vertex-index mapping.</param>
    /// <param name="indices">Destination triangle indices.</param>
    /// <param name="x">Base quad column.</param>
    /// <param name="z">Base quad row.</param>
    private static void AppendQuadIndices(
        TerrainResource terrain,
        int[] lookup,
        List<uint> indices,
        int x,
        int z)
    {
        var fineX = x * 2;
        var fineZ = z * 2;
        if (terrain.IsQuadRefined(x, z))
        {
            for (var localZ = 0; localZ < 2; localZ++)
            {
                for (var localX = 0; localX < 2; localX++)
                {
                    AppendRegularQuad(terrain, lookup, indices,
                        fineX + localX, fineZ + localZ);
                }
            }
            return;
        }
        var leftSplit = x > 0 && terrain.IsQuadRefined(x - 1, z);
        var rightSplit = x + 1 < terrain.Width - 1 && terrain.IsQuadRefined(x + 1, z);
        var topSplit = z > 0 && terrain.IsQuadRefined(x, z - 1);
        var bottomSplit = z + 1 < terrain.Depth - 1 && terrain.IsQuadRefined(x, z + 1);
        if (!leftSplit && !rightSplit && !topSplit && !bottomSplit)
        {
            AppendTriangle(terrain, lookup, indices,
                fineX, fineZ, fineX, fineZ + 2, fineX + 2, fineZ + 2);
            AppendTriangle(terrain, lookup, indices,
                fineX, fineZ, fineX + 2, fineZ + 2, fineX + 2, fineZ);
            return;
        }
        Span<TerrainSamplePoint> boundary = stackalloc TerrainSamplePoint[8];
        var count = 0;
        boundary[count++] = new TerrainSamplePoint(fineX, fineZ);
        if (leftSplit)
            boundary[count++] = new TerrainSamplePoint(fineX, fineZ + 1);
        boundary[count++] = new TerrainSamplePoint(fineX, fineZ + 2);
        if (bottomSplit)
            boundary[count++] = new TerrainSamplePoint(fineX + 1, fineZ + 2);
        boundary[count++] = new TerrainSamplePoint(fineX + 2, fineZ + 2);
        if (rightSplit)
            boundary[count++] = new TerrainSamplePoint(fineX + 2, fineZ + 1);
        boundary[count++] = new TerrainSamplePoint(fineX + 2, fineZ);
        if (topSplit)
            boundary[count++] = new TerrainSamplePoint(fineX + 1, fineZ);
        var center = new TerrainSamplePoint(fineX + 1, fineZ + 1);
        for (var index = 0; index < count; index++)
        {
            var first = boundary[index];
            var second = boundary[(index + 1) % count];
            AppendTriangle(terrain, lookup, indices,
                center.FineX, center.FineZ,
                first.FineX, first.FineZ,
                second.FineX, second.FineZ);
        }
    }

    /// <summary>Appends two upward-wound triangles for one fine-lattice quad.</summary>
    /// <param name="terrain">Terrain coordinate dimensions.</param>
    /// <param name="lookup">Coordinate to vertex-index mapping.</param>
    /// <param name="indices">Destination triangle indices.</param>
    /// <param name="fineX">Fine quad column.</param>
    /// <param name="fineZ">Fine quad row.</param>
    private static void AppendRegularQuad(
        TerrainResource terrain,
        int[] lookup,
        List<uint> indices,
        int fineX,
        int fineZ)
    {
        AppendTriangle(terrain, lookup, indices,
            fineX, fineZ, fineX, fineZ + 1, fineX + 1, fineZ + 1);
        AppendTriangle(terrain, lookup, indices,
            fineX, fineZ, fineX + 1, fineZ + 1, fineX + 1, fineZ);
    }

    /// <summary>Appends one triangle after resolving its three active sample indices.</summary>
    /// <param name="terrain">Terrain coordinate dimensions.</param>
    /// <param name="lookup">Coordinate to vertex-index mapping.</param>
    /// <param name="indices">Destination triangle indices.</param>
    /// <param name="firstX">First sample X.</param>
    /// <param name="firstZ">First sample Z.</param>
    /// <param name="secondX">Second sample X.</param>
    /// <param name="secondZ">Second sample Z.</param>
    /// <param name="thirdX">Third sample X.</param>
    /// <param name="thirdZ">Third sample Z.</param>
    private static void AppendTriangle(
        TerrainResource terrain,
        int[] lookup,
        List<uint> indices,
        int firstX,
        int firstZ,
        int secondX,
        int secondZ,
        int thirdX,
        int thirdZ)
    {
        indices.Add(GetVertexIndex(terrain, lookup, firstX, firstZ));
        indices.Add(GetVertexIndex(terrain, lookup, secondX, secondZ));
        indices.Add(GetVertexIndex(terrain, lookup, thirdX, thirdZ));
    }

    /// <summary>Resolves one required active fine-lattice sample.</summary>
    /// <param name="terrain">Terrain coordinate dimensions.</param>
    /// <param name="lookup">Coordinate to vertex-index mapping.</param>
    /// <param name="fineX">Sample X.</param>
    /// <param name="fineZ">Sample Z.</param>
    /// <returns>Resolved unsigned vertex index.</returns>
    private static uint GetVertexIndex(
        TerrainResource terrain,
        int[] lookup,
        int fineX,
        int fineZ)
    {
        var index = lookup[fineZ * terrain.FineWidth + fineX];
        if (index < 0)
            throw new InvalidOperationException("Adaptive terrain topology omitted a required sample.");
        return checked((uint)index);
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
    /// <param name="heightScale">Local units represented by a height sample of one.</param>
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

    /// <summary>Gets one adaptive sample's object-space position.</summary>
    /// <param name="terrain">Source terrain.</param>
    /// <param name="horizontalSize">Local X and Z dimensions.</param>
    /// <param name="heightScale">Local height scale.</param>
    /// <param name="center">Node-local surface offset.</param>
    /// <param name="sample">Fine-lattice sample coordinate.</param>
    /// <returns>Object-space sample position.</returns>
    private static Vector3 CreatePosition(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        Vector3 center,
        TerrainSamplePoint sample)
    {
        var u = sample.FineX / (float)(terrain.FineWidth - 1);
        var v = sample.FineZ / (float)(terrain.FineDepth - 1);
        return center + new Vector3(
            (u - 0.5f) * horizontalSize.X,
            terrain.GetSampleHeight(sample) * heightScale,
            (v - 0.5f) * horizontalSize.Y);
    }

    /// <summary>Computes one saturated vertex color from a terrain height sample.</summary>
    /// <param name="height">Terrain height sample.</param>
    /// <returns>RGB terrain tint.</returns>
    private static Vector3 CreateTerrainColor(float height) =>
        height <= 0.5f
            ? Vector3.Lerp(LowColor, MidColor, Math.Clamp(height * 2f, 0f, 1f))
            : Vector3.Lerp(MidColor, HighColor, Math.Clamp(height * 2f - 1f, 0f, 1f));

    /// <summary>Computes a stable sample normal from neighboring terrain positions.</summary>
    /// <param name="terrain">Current height samples.</param>
    /// <param name="horizontalSize">Local X and Z dimensions.</param>
    /// <param name="heightScale">Local units represented by one height sample.</param>
    /// <param name="x">Sample column.</param>
    /// <param name="z">Sample row.</param>
    /// <returns>Normalized local surface direction.</returns>
    private static Vector3 CreateNormal(
        TerrainResource terrain,
        Vector2 horizontalSize,
        float heightScale,
        int x,
        int z)
    {
        var left = CreatePosition(terrain, horizontalSize, heightScale, default,
            Math.Max(0, x - 1), z);
        var right = CreatePosition(terrain, horizontalSize, heightScale, default,
            Math.Min(terrain.Width - 1, x + 1), z);
        var back = CreatePosition(terrain, horizontalSize, heightScale, default,
            x, Math.Max(0, z - 1));
        var forward = CreatePosition(terrain, horizontalSize, heightScale, default,
            x, Math.Min(terrain.Depth - 1, z + 1));
        var normal = Vector3.Cross(forward - back, right - left);
        return normal.LengthSquared() > float.Epsilon
            ? Vector3.Normalize(normal) : Vector3.UnitY;
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
