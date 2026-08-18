using Engine.Core;
using Engine.Graphics;
using System.Numerics;

namespace ExampleGame.Server;

/// <summary>One sampled point on an authored terrain collision surface.</summary>
/// <param name="Height">World-space surface height.</param>
/// <param name="Normal">World-space unit surface normal.</param>
internal readonly record struct TerrainGroundSample(float Height, Vector3 Normal);

/// <summary>Samples the same authored terrain resources and transforms used by engine physics.</summary>
internal sealed class ServerTerrainCollision
{
    private readonly List<TerrainSurface> _surfaces = [];

    /// <summary>Discovers enabled terrain colliders in an already decoded scene.</summary>
    /// <param name="root">Authoritative scene root.</param>
    /// <param name="resolveTerrain">Resolver for imported terrain artifacts.</param>
    internal ServerTerrainCollision(
        Node root,
        Func<AssetReference, TerrainResource> resolveTerrain)
    {
        Reload(root, resolveTerrain);
    }

    /// <summary>Gets the number of terrain colliders shared with the authoritative scene.</summary>
    internal int SurfaceCount => _surfaces.Count;

    /// <summary>Atomically replaces all sampled surfaces from current terrain resources.</summary>
    /// <param name="root">Authoritative scene root.</param>
    /// <param name="resolveTerrain">Resolver for freshly imported terrain artifacts.</param>
    internal void Reload(
        Node root,
        Func<AssetReference, TerrainResource> resolveTerrain)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(resolveTerrain);
        var replacements = new List<TerrainSurface>();
        AddNode(root, resolveTerrain, replacements);
        if (replacements.Count == 0)
            throw new InvalidDataException(
                "The authoritative scene has no enabled terrain collider with terrain data.");
        _surfaces.Clear();
        _surfaces.AddRange(replacements);
    }

    /// <summary>Samples the highest terrain surface beneath one world XZ coordinate.</summary>
    /// <param name="worldPosition">World position whose XZ coordinates are queried.</param>
    /// <param name="sample">Highest matching surface sample.</param>
    /// <returns>True when the coordinate lies over an authored terrain collider.</returns>
    internal bool TrySample(Vector3 worldPosition, out TerrainGroundSample sample)
    {
        var found = false;
        sample = default;
        for (var index = 0; index < _surfaces.Count; index++)
        {
            if (!_surfaces[index].TrySample(worldPosition, out var candidate) ||
                found && candidate.Height <= sample.Height)
            {
                continue;
            }
            found = true;
            sample = candidate;
        }
        return found;
    }

    /// <summary>Returns a grounded spawn near the center of the first terrain collider.</summary>
    /// <returns>World-space foot position on the authored terrain.</returns>
    internal Vector3 GetDefaultSpawnPosition()
    {
        var candidate = _surfaces[0].Center;
        return TrySample(candidate, out var sample)
            ? new Vector3(candidate.X, sample.Height, candidate.Z)
            : candidate;
    }

    /// <summary>Recursively discovers terrain components without iterator allocation.</summary>
    /// <param name="node">Current scene node.</param>
    /// <param name="resolveTerrain">Resolver for imported terrain artifacts.</param>
    /// <param name="surfaces">Destination surface collection.</param>
    private static void AddNode(
        Node node,
        Func<AssetReference, TerrainResource> resolveTerrain,
        List<TerrainSurface> surfaces)
    {
        if (node is Node3D node3D)
        {
            var components = node.Components;
            for (var index = 0; index < components.Count; index++)
            {
                if (components[index] is not TerrainColliderComponent
                    {
                        Enabled: true,
                        TerrainData: { } reference
                    } collider)
                {
                    continue;
                }
                surfaces.Add(new TerrainSurface(
                    resolveTerrain(reference), collider, node3D.GetModelMatrix()));
            }
        }
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
            AddNode(children[index], resolveTerrain, surfaces);
    }

    /// <summary>Stores one decoded grid and its authored world placement.</summary>
    private sealed class TerrainSurface
    {
        private readonly TerrainResource _terrain;
        private readonly Vector2 _horizontalSize;
        private readonly float _heightScale;
        private readonly Matrix4x4 _transform;
        private readonly Matrix4x4 _inverse;
        private readonly Matrix4x4 _normalTransform;

        /// <summary>Gets the world-space center used for default spawning.</summary>
        internal Vector3 Center { get; }

        /// <summary>Creates one terrain collision sampler.</summary>
        /// <param name="terrain">Decoded normalized height grid.</param>
        /// <param name="collider">Authored collider dimensions and center.</param>
        /// <param name="nodeTransform">Owning node world transform.</param>
        internal TerrainSurface(
            TerrainResource terrain,
            TerrainColliderComponent collider,
            Matrix4x4 nodeTransform)
        {
            _terrain = terrain;
            _horizontalSize = collider.HorizontalSize;
            _heightScale = collider.HeightScale;
            _transform = Matrix4x4.CreateTranslation(collider.Center) * nodeTransform;
            if (!Matrix4x4.Invert(_transform, out _inverse))
                throw new InvalidDataException("A terrain collider has a singular world transform.");
            _normalTransform = Matrix4x4.Transpose(_inverse);
            Center = Vector3.Transform(Vector3.Zero, _transform);
        }

        /// <summary>Samples this bounded terrain at one world XZ coordinate.</summary>
        /// <param name="worldPosition">World coordinate to query.</param>
        /// <param name="sample">World-space height and normal.</param>
        /// <returns>True when the point lies over this terrain.</returns>
        internal bool TrySample(Vector3 worldPosition, out TerrainGroundSample sample)
        {
            var local = Vector3.Transform(worldPosition, _inverse);
            var u = local.X / _horizontalSize.X + 0.5f;
            var v = local.Z / _horizontalSize.Y + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f)
            {
                sample = default;
                return false;
            }
            var localHeight = _terrain.Sample(u, v) * _heightScale;
            var worldSurface = Vector3.Transform(
                new Vector3(local.X, localHeight, local.Z), _transform);
            var normal = GetWorldNormal(u, v);
            sample = new TerrainGroundSample(worldSurface.Y, normal);
            return true;
        }

        /// <summary>Computes a finite-difference normal from the shared height samples.</summary>
        /// <param name="u">Normalized terrain X coordinate.</param>
        /// <param name="v">Normalized terrain Z coordinate.</param>
        /// <returns>World-space unit normal.</returns>
        private Vector3 GetWorldNormal(float u, float v)
        {
            var du = 1f / (_terrain.Width - 1);
            var dv = 1f / (_terrain.Depth - 1);
            var left = _terrain.Sample(u - du, v) * _heightScale;
            var right = _terrain.Sample(u + du, v) * _heightScale;
            var back = _terrain.Sample(u, v - dv) * _heightScale;
            var forward = _terrain.Sample(u, v + dv) * _heightScale;
            var tangentX = new Vector3(_horizontalSize.X * du * 2f, right - left, 0f);
            var tangentZ = new Vector3(0f, forward - back, _horizontalSize.Y * dv * 2f);
            var localNormal = Vector3.Normalize(Vector3.Cross(tangentZ, tangentX));
            var worldNormal = Vector3.TransformNormal(localNormal, _normalTransform);
            return Vector3.Normalize(worldNormal);
        }
    }
}
