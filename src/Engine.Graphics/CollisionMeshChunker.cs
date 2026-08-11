using System.Numerics;

namespace Engine.Graphics;

/// <summary>Identifies one spatial collision-mesh chunk and its generated triangle resource.</summary>
/// <param name="Coordinate">Integer source-space chunk coordinate.</param>
/// <param name="Mesh">Dedicated collision-only triangle mesh.</param>
public readonly record struct CollisionMeshChunk((int X, int Y, int Z) Coordinate,
    StaticMeshResource Mesh);

/// <summary>Generates bounded collision-only mesh chunks from explicit source triangles.</summary>
public static class CollisionMeshChunker
{
    /// <summary>Partitions triangles by centroid into fixed-size source-space cells.</summary>
    /// <param name="source">Source triangle mesh selected by editor/import tooling.</param>
    /// <param name="chunkSize">Positive cell size in source units.</param>
    /// <returns>Deterministically ordered collision chunks.</returns>
    public static CollisionMeshChunk[] Chunk(StaticMeshResource source, float chunkSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!float.IsFinite(chunkSize) || chunkSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        var groups = new Dictionary<(int X, int Y, int Z), List<int>>();
        for (var index = 0; index < source.Indices.Length; index += 3)
        {
            var a = source.Vertices[checked((int)source.Indices[index])].Position;
            var b = source.Vertices[checked((int)source.Indices[index + 1])].Position;
            var c = source.Vertices[checked((int)source.Indices[index + 2])].Position;
            var centroid = (a + b + c) / 3f;
            var coordinate = (
                (int)MathF.Floor(centroid.X / chunkSize),
                (int)MathF.Floor(centroid.Y / chunkSize),
                (int)MathF.Floor(centroid.Z / chunkSize));
            if (!groups.TryGetValue(coordinate, out var triangles))
            {
                triangles = [];
                groups.Add(coordinate, triangles);
            }
            triangles.Add(index);
        }
        var coordinates = groups.Keys.ToArray();
        Array.Sort(coordinates, CompareCoordinates);
        var result = new CollisionMeshChunk[coordinates.Length];
        for (var chunkIndex = 0; chunkIndex < coordinates.Length; chunkIndex++)
        {
            var coordinate = coordinates[chunkIndex];
            var triangles = groups[coordinate];
            var vertices = new ModelVertex[triangles.Count * 3];
            var indices = new uint[vertices.Length];
            for (var triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
            {
                var sourceIndex = triangles[triangleIndex];
                var destination = triangleIndex * 3;
                vertices[destination] = source.Vertices[checked((int)source.Indices[sourceIndex])];
                vertices[destination + 1] = source.Vertices[checked((int)source.Indices[sourceIndex + 1])];
                vertices[destination + 2] = source.Vertices[checked((int)source.Indices[sourceIndex + 2])];
                indices[destination] = checked((uint)destination);
                indices[destination + 1] = checked((uint)destination + 1u);
                indices[destination + 2] = checked((uint)destination + 2u);
            }
            result[chunkIndex] = new CollisionMeshChunk(coordinate,
                new StaticMeshResource(vertices, indices,
                    [new Submesh(0, checked((uint)indices.Length), -1)]));
        }
        return result;
    }

    /// <summary>Orders integer cell coordinates deterministically by X, Y, then Z.</summary>
    /// <param name="left">First coordinate.</param><param name="right">Second coordinate.</param>
    /// <returns>Standard comparison result.</returns>
    private static int CompareCoordinates((int X, int Y, int Z) left,
        (int X, int Y, int Z) right)
    {
        var comparison = left.X.CompareTo(right.X);
        if (comparison != 0)
            return comparison;
        comparison = left.Y.CompareTo(right.Y);
        return comparison != 0 ? comparison : left.Z.CompareTo(right.Z);
    }
}
