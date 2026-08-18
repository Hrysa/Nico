using System.Numerics;
using Engine.Core;
using Engine.Graphics;

namespace Editor;

/// <summary>Accelerates repeated terrain pointer rays with cached heightfield chunk bounds.</summary>
internal sealed class TerrainSurfaceRaycaster
{
    private const int ChunkQuads = 8;
    private TerrainResource? _terrain;
    private TerrainColliderComponent? _collider;
    private Vector2 _horizontalSize;
    private float _heightScale;
    private int _topologyVersion = -1;
    private TerrainStaticMeshData? _mesh;
    private Chunk[] _chunks = [];

    /// <summary>Gets the triangle tests performed by the latest query.</summary>
    internal int LastTriangleTestCount { get; private set; }

    /// <summary>Finds the nearest terrain hit after rejecting non-intersecting chunks.</summary>
    /// <param name="terrain">Current terrain samples.</param>
    /// <param name="collider">Terrain dimensions.</param>
    /// <param name="origin">Terrain-local ray origin.</param>
    /// <param name="direction">Terrain-local ray direction.</param>
    /// <param name="position">Closest terrain-local hit position.</param>
    /// <returns>True when the ray intersects the finite terrain surface.</returns>
    internal bool TryIntersect(
        TerrainResource terrain,
        TerrainColliderComponent collider,
        Vector3 origin,
        Vector3 direction,
        out Vector3 position)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(collider);
        EnsureCache(terrain, collider);
        LastTriangleTestCount = 0;
        var closest = float.PositiveInfinity;
        for (var chunkIndex = 0; chunkIndex < _chunks.Length; chunkIndex++)
        {
            var chunk = _chunks[chunkIndex];
            if (!TryIntersectBounds(origin, direction, chunk.Minimum, chunk.Maximum,
                    out var entry) || entry > closest)
                continue;
            var mesh = _mesh!.Mesh;
            var triangleIndices = chunk.TriangleIndices;
            for (var index = 0; index < triangleIndices.Length; index++)
            {
                var triangle = triangleIndices[index] * 3;
                var a = mesh.Vertices[mesh.Indices[triangle]].Position;
                var b = mesh.Vertices[mesh.Indices[triangle + 1]].Position;
                var c = mesh.Vertices[mesh.Indices[triangle + 2]].Position;
                LastTriangleTestCount++;
                if (TryIntersectTriangle(origin, direction, a, b, c, out var distance) &&
                    distance < closest)
                    closest = distance;
            }
        }
        if (!float.IsFinite(closest))
        {
            position = default;
            return false;
        }
        position = origin + direction * closest;
        return IsFinite(position);
    }

    /// <summary>Refreshes only cached chunks whose quads touch an edited sample region.</summary>
    /// <param name="terrain">Current terrain samples.</param>
    /// <param name="collider">Terrain dimensions.</param>
    /// <param name="region">Inclusive changed sample rectangle.</param>
    internal void Invalidate(
        TerrainResource terrain,
        TerrainColliderComponent collider,
        TerrainEditRegion region)
    {
        EnsureCache(terrain, collider);
        TerrainMeshBuilder.UpdateStaticMeshVertices(
            terrain, collider.HorizontalSize, collider.HeightScale, default, true,
            _mesh!.Samples, _mesh.Mesh.Indices, _mesh.Mesh.Vertices);
        var minimumQuadX = Math.Max(0, region.MinimumX - 1);
        var minimumQuadZ = Math.Max(0, region.MinimumZ - 1);
        var maximumQuadX = Math.Min(terrain.Width - 2, region.MaximumX);
        var maximumQuadZ = Math.Min(terrain.Depth - 2, region.MaximumZ);
        var countX = (terrain.Width - 2 + ChunkQuads) / ChunkQuads;
        var firstChunkX = minimumQuadX / ChunkQuads;
        var firstChunkZ = minimumQuadZ / ChunkQuads;
        var lastChunkX = maximumQuadX / ChunkQuads;
        var lastChunkZ = maximumQuadZ / ChunkQuads;
        for (var chunkZ = firstChunkZ; chunkZ <= lastChunkZ; chunkZ++)
        {
            for (var chunkX = firstChunkX; chunkX <= lastChunkX; chunkX++)
            {
                var index = chunkZ * countX + chunkX;
                var chunk = _chunks[index];
                _chunks[index] = CreateChunk(_mesh.Mesh, chunk.TriangleIndices);
            }
        }
    }

    /// <summary>Drops cached bounds after a scene or selection transition.</summary>
    internal void Clear()
    {
        _terrain = null;
        _collider = null;
        _mesh = null;
        _topologyVersion = -1;
        _chunks = [];
        LastTriangleTestCount = 0;
    }

    /// <summary>Creates or replaces chunks when terrain identity or dimensions change.</summary>
    /// <param name="terrain">Current terrain samples.</param>
    /// <param name="collider">Terrain dimensions.</param>
    private void EnsureCache(TerrainResource terrain, TerrainColliderComponent collider)
    {
        if (ReferenceEquals(_terrain, terrain) && ReferenceEquals(_collider, collider) &&
            _horizontalSize == collider.HorizontalSize && _heightScale == collider.HeightScale &&
            _topologyVersion == terrain.TopologyVersion)
            return;
        _terrain = terrain;
        _collider = collider;
        _horizontalSize = collider.HorizontalSize;
        _heightScale = collider.HeightScale;
        _topologyVersion = terrain.TopologyVersion;
        _mesh = TerrainMeshBuilder.BuildTerrainStaticMesh(
            terrain, collider.HorizontalSize, collider.HeightScale, default, true);
        var countX = (terrain.Width - 2 + ChunkQuads) / ChunkQuads;
        var countZ = (terrain.Depth - 2 + ChunkQuads) / ChunkQuads;
        _chunks = new Chunk[checked(countX * countZ)];
        var triangleLists = new List<int>[_chunks.Length];
        for (var index = 0; index < triangleLists.Length; index++)
            triangleLists[index] = [];
        var mesh = _mesh.Mesh;
        for (var index = 0; index < mesh.Indices.Length; index += 3)
        {
            var first = mesh.Vertices[mesh.Indices[index]].TexCoord;
            var second = mesh.Vertices[mesh.Indices[index + 1]].TexCoord;
            var third = mesh.Vertices[mesh.Indices[index + 2]].TexCoord;
            var center = (first + second + third) / 3f;
            var baseX = Math.Min(terrain.Width - 2,
                Math.Max(0, (int)(center.X * (terrain.Width - 1))));
            var baseZ = Math.Min(terrain.Depth - 2,
                Math.Max(0, (int)(center.Y * (terrain.Depth - 1))));
            var chunkIndex = baseZ / ChunkQuads * countX + baseX / ChunkQuads;
            triangleLists[chunkIndex].Add(index / 3);
        }
        for (var index = 0; index < _chunks.Length; index++)
            _chunks[index] = CreateChunk(mesh, triangleLists[index].ToArray());
    }

    /// <summary>Computes one chunk's exact adaptive triangle bounds.</summary>
    /// <param name="mesh">Current adaptive terrain mesh.</param>
    /// <param name="triangleIndices">Triangle ordinals assigned to the chunk.</param>
    /// <returns>Cached chunk description.</returns>
    private static Chunk CreateChunk(
        StaticMeshResource mesh,
        int[] triangleIndices)
    {
        if (triangleIndices.Length == 0)
            return new Chunk(Vector3.Zero, Vector3.Zero, triangleIndices);
        var firstTriangle = triangleIndices[0] * 3;
        var first = mesh.Vertices[mesh.Indices[firstTriangle]].Position;
        var minimum = first;
        var maximum = first;
        for (var index = 0; index < triangleIndices.Length; index++)
        {
            var triangle = triangleIndices[index] * 3;
            for (var corner = 0; corner < 3; corner++)
            {
                var point = mesh.Vertices[mesh.Indices[triangle + corner]].Position;
                minimum = Vector3.Min(minimum, point);
                maximum = Vector3.Max(maximum, point);
            }
        }
        const float epsilon = 0.0001f;
        minimum -= new Vector3(epsilon);
        maximum += new Vector3(epsilon);
        return new Chunk(minimum, maximum, triangleIndices);
    }

    /// <summary>Intersects a ray with an axis-aligned box using a slab test.</summary>
    /// <param name="origin">Ray origin.</param>
    /// <param name="direction">Ray direction.</param>
    /// <param name="minimum">Box minimum.</param>
    /// <param name="maximum">Box maximum.</param>
    /// <param name="entry">Nonnegative box entry distance.</param>
    /// <returns>True when the forward ray intersects the box.</returns>
    private static bool TryIntersectBounds(
        Vector3 origin,
        Vector3 direction,
        Vector3 minimum,
        Vector3 maximum,
        out float entry)
    {
        var near = 0f;
        var far = float.PositiveInfinity;
        if (!ClipSlab(origin.X, direction.X, minimum.X, maximum.X, ref near, ref far) ||
            !ClipSlab(origin.Y, direction.Y, minimum.Y, maximum.Y, ref near, ref far) ||
            !ClipSlab(origin.Z, direction.Z, minimum.Z, maximum.Z, ref near, ref far))
        {
            entry = 0f;
            return false;
        }
        entry = near;
        return far >= 0f;
    }

    /// <summary>Clips a ray interval against one axis-aligned slab.</summary>
    /// <param name="origin">Ray origin on the axis.</param>
    /// <param name="direction">Ray direction on the axis.</param>
    /// <param name="minimum">Slab minimum.</param>
    /// <param name="maximum">Slab maximum.</param>
    /// <param name="near">Current interval start.</param>
    /// <param name="far">Current interval end.</param>
    /// <returns>True when the interval remains nonempty.</returns>
    private static bool ClipSlab(
        float origin,
        float direction,
        float minimum,
        float maximum,
        ref float near,
        ref float far)
    {
        if (MathF.Abs(direction) <= 0.000001f)
            return origin >= minimum && origin <= maximum;
        var inverse = 1f / direction;
        var first = (minimum - origin) * inverse;
        var second = (maximum - origin) * inverse;
        if (first > second)
            (first, second) = (second, first);
        near = MathF.Max(near, first);
        far = MathF.Min(far, second);
        return near <= far;
    }

    /// <summary>Intersects a two-sided triangle using the Möller-Trumbore test.</summary>
    /// <param name="origin">Ray origin.</param>
    /// <param name="direction">Ray direction.</param>
    /// <param name="a">First triangle vertex.</param>
    /// <param name="b">Second triangle vertex.</param>
    /// <param name="c">Third triangle vertex.</param>
    /// <param name="distance">Positive ray parameter when hit.</param>
    /// <returns>True when the finite triangle is hit in front of the ray.</returns>
    private static bool TryIntersectTriangle(
        Vector3 origin,
        Vector3 direction,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float distance)
    {
        var edge1 = b - a;
        var edge2 = c - a;
        var p = Vector3.Cross(direction, edge2);
        var determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) <= 0.000001f)
        {
            distance = 0f;
            return false;
        }
        var inverse = 1f / determinant;
        var translated = origin - a;
        var u = Vector3.Dot(translated, p) * inverse;
        if (u < 0f || u > 1f)
        {
            distance = 0f;
            return false;
        }
        var q = Vector3.Cross(translated, edge1);
        var v = Vector3.Dot(direction, q) * inverse;
        if (v < 0f || u + v > 1f)
        {
            distance = 0f;
            return false;
        }
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance >= 0f && float.IsFinite(distance);
    }

    /// <summary>Checks whether every vector component is finite.</summary>
    /// <param name="value">Vector to validate.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Stores one terrain chunk's quad range and exact local bounds.</summary>
    private readonly record struct Chunk(
        Vector3 Minimum,
        Vector3 Maximum,
        int[] TriangleIndices);
}
