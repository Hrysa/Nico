using System.Numerics;
using Engine.Core;
using Engine.Graphics;

namespace Editor;

/// <summary>Builds a camera icon, forward ray, and projection frustum.</summary>
internal sealed class CameraPreviewProvider : IScenePreviewProvider
{
    /// <inheritdoc/>
    public ScenePreviewCategory Category => ScenePreviewCategory.Cameras;

    /// <inheritdoc/>
    public bool Supports(object value) => value is PerspectiveCamera;

    /// <inheritdoc/>
    public void Build(Node3D node, object value, ScenePreviewPickingId pickingId,
        bool selected, bool hovered, ScenePreviewList destination)
    {
        var camera = (PerspectiveCamera)value;
        var color = selected ? new Vector4(1f, 0.78f, 0.18f, 1f) :
            hovered ? new Vector4(0.7f, 0.9f, 1f, 1f) : new Vector4(0.35f, 0.7f, 1f, 0.85f);
        var transform = camera.GetModelMatrix();
        var origin = Vector3.Transform(Vector3.Zero, transform);
        var previewFar = MathF.Min(camera.Far, MathF.Max(camera.Near * 8f, 2f));
        var near = MathF.Max(camera.Near, 0.02f);
        destination.AddIcon(new ScenePreviewIcon(origin, 18f,
            ScenePreviewIconKind.Camera, color, ScenePreviewDepthMode.AlwaysVisible, pickingId));
        destination.AddFrustum(new ScenePreviewFrustum(transform, camera.Fov, camera.Aspect,
            near, previewFar, color, ScenePreviewDepthMode.DepthTested, pickingId));
        AddFrustum(destination, transform, camera.Fov, camera.Aspect, near, previewFar,
            color, pickingId);
        var forwardEnd = Vector3.Transform(new Vector3(0f, 0f, -previewFar * 1.2f), transform);
        destination.AddLine(new(origin, forwardEnd, color,
            ScenePreviewDepthMode.AlwaysVisible, pickingId));
        PreviewWire.AddBox(destination, transform * Matrix4x4.CreateScale(0.3f, 0.2f, 0.25f),
            color, ScenePreviewDepthMode.AlwaysVisible, pickingId);
    }

    /// <summary>Adds the eight-corner wire frustum represented by camera-local distances.</summary>
    /// <param name="destination">Primitive destination.</param>
    /// <param name="transform">Camera world transform.</param>
    /// <param name="fov">Vertical field of view.</param>
    /// <param name="aspect">Viewport aspect ratio.</param>
    /// <param name="near">Near preview distance.</param>
    /// <param name="far">Far preview distance.</param>
    /// <param name="color">Line color.</param>
    /// <param name="pickingId">Camera identity.</param>
    private static void AddFrustum(ScenePreviewList destination, Matrix4x4 transform,
        float fov, float aspect, float near, float far, Vector4 color,
        ScenePreviewPickingId pickingId)
    {
        var tan = MathF.Tan(fov * 0.5f);
        var nearY = near * tan;
        var nearX = nearY * aspect;
        var farY = far * tan;
        var farX = farY * aspect;
        Span<Vector3> corners = stackalloc Vector3[8]
        {
            new(-nearX, -nearY, -near), new(nearX, -nearY, -near),
            new(nearX, nearY, -near), new(-nearX, nearY, -near),
            new(-farX, -farY, -far), new(farX, -farY, -far),
            new(farX, farY, -far), new(-farX, farY, -far)
        };
        for (var index = 0; index < corners.Length; index++)
            corners[index] = Vector3.Transform(corners[index], transform);
        PreviewWire.AddLoop(destination, corners[..4], color, pickingId);
        PreviewWire.AddLoop(destination, corners[4..], color, pickingId);
        for (var index = 0; index < 4; index++)
            destination.AddLine(new(corners[index], corners[index + 4], color,
                ScenePreviewDepthMode.DepthTested, pickingId));
    }
}

/// <summary>Builds exact authored primitive collider wire geometry and invalid-asset warnings.</summary>
internal sealed class ColliderPreviewProvider : IScenePreviewProvider
{
    private readonly Func<AssetReference, StaticMeshResource?>? _meshResolver;
    private readonly Func<AssetReference, TerrainResource?>? _terrainResolver;
    private readonly Dictionary<AssetReference, StaticMeshResource?> _meshCache = [];
    private readonly Dictionary<AssetReference, TerrainResource?> _terrainCache = [];

    /// <summary>Creates a provider using explicit collision asset resolvers.</summary>
    /// <param name="meshResolver">Static triangle-mesh resolver.</param>
    /// <param name="terrainResolver">Terrain height-grid resolver.</param>
    internal ColliderPreviewProvider(Func<AssetReference, StaticMeshResource?>? meshResolver = null,
        Func<AssetReference, TerrainResource?>? terrainResolver = null)
    {
        _meshResolver = meshResolver;
        _terrainResolver = terrainResolver;
    }

    /// <inheritdoc/>
    public ScenePreviewCategory Category => ScenePreviewCategory.Colliders;

    /// <inheritdoc/>
    public bool Supports(object value) => value is ColliderComponent;

    /// <inheritdoc/>
    public void Build(Node3D node, object value, ScenePreviewPickingId pickingId,
        bool selected, bool hovered, ScenePreviewList destination)
    {
        var collider = (ColliderComponent)value;
        var resolvedMesh = collider is MeshColliderComponent { Mesh: { } meshReference }
            ? ResolveMesh(meshReference) : null;
        var resolvedTerrain = collider is TerrainColliderComponent { TerrainData: { } terrainReference }
            ? ResolveTerrain(terrainReference) : null;
        var valid = collider switch
        {
            MeshColliderComponent => resolvedMesh is not null,
            TerrainColliderComponent => resolvedTerrain is not null,
            _ => true
        };
        var color = !valid
            ? new Vector4(1f, 0.18f, 0.08f, 1f)
            : selected ? new Vector4(0.15f, 1f, 0.45f, 1f) :
            hovered ? new Vector4(0.35f, 1f, 0.62f, 1f) : new Vector4(0.1f, 0.65f, 0.3f, 0.65f);
        var transform = Matrix4x4.CreateTranslation(collider.Center) * node.GetModelMatrix();
        GetPrimitivePose(node, collider, out var primitiveTransform, out var scale);
        switch (collider)
        {
            case BoxColliderComponent box:
                var scaledBoxSize = box.Size * scale;
                destination.AddBounds(new ScenePreviewBounds(primitiveTransform,
                    new MeshBounds(scaledBoxSize * -.5f, scaledBoxSize * .5f), color,
                    ScenePreviewDepthMode.DepthTested, pickingId));
                PreviewWire.AddBox(destination,
                    Matrix4x4.CreateScale(scaledBoxSize) * primitiveTransform,
                    color, ScenePreviewDepthMode.DepthTested, pickingId);
                break;
            case SphereColliderComponent sphere:
                PreviewWire.AddSphere(destination, primitiveTransform,
                    sphere.Radius * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)),
                    color, pickingId);
                break;
            case CapsuleColliderComponent capsule:
                PreviewWire.AddCapsule(destination, primitiveTransform,
                    capsule.Radius * MathF.Max(scale.X, scale.Z), capsule.Height * scale.Y,
                    color, pickingId);
                break;
            case CylinderColliderComponent cylinder:
                PreviewWire.AddCylinder(destination, primitiveTransform,
                    cylinder.Radius * MathF.Max(scale.X, scale.Z), cylinder.Height * scale.Y,
                    color, pickingId);
                break;
            case PlaneColliderComponent plane:
                PreviewWire.AddPlane(destination, primitiveTransform,
                    new Vector2(plane.Size.X * scale.X, plane.Size.Y * scale.Z), color, pickingId);
                break;
            case TerrainColliderComponent terrain when resolvedTerrain is not null:
                PreviewWire.AddTerrain(destination, transform, terrain, resolvedTerrain,
                    color, pickingId);
                break;
            case MeshColliderComponent when resolvedMesh is not null:
                destination.AddWireMesh(new ScenePreviewWireMesh(resolvedMesh, transform,
                    color, ScenePreviewDepthMode.DepthTested, pickingId));
                PreviewWire.AddMesh(destination, transform, resolvedMesh, color, pickingId);
                break;
        }
        if (!valid)
        {
            destination.AddIcon(new ScenePreviewIcon(
                Vector3.Transform(Vector3.Zero, transform), 18f,
                ScenePreviewIconKind.Warning, color,
                ScenePreviewDepthMode.AlwaysVisible, pickingId));
            PreviewWire.AddWarning(destination, transform, color, pickingId);
        }
    }

    /// <summary>Computes the rotation-only world pose and absolute scale used by primitive physics shapes.</summary>
    /// <param name="node">Collider owner.</param><param name="collider">Authored collider.</param>
    /// <param name="transform">World center and orientation without scale.</param>
    /// <param name="scale">Absolute decomposed node scale.</param>
    private static void GetPrimitivePose(Node3D node, ColliderComponent collider,
        out Matrix4x4 transform, out Vector3 scale)
    {
        var model = node.GetModelMatrix();
        var center = Vector3.Transform(collider.Center, model);
        if (!Matrix4x4.Decompose(model, out scale, out var orientation, out _))
        {
            scale = Vector3.One;
            orientation = Quaternion.Identity;
        }
        scale = Vector3.Abs(scale);
        transform = Matrix4x4.CreateFromQuaternion(orientation) *
            Matrix4x4.CreateTranslation(center);
    }

    /// <summary>Resolves and caches one explicit collision mesh.</summary>
    /// <param name="reference">Persistent mesh reference.</param><returns>Resolved mesh or null.</returns>
    private StaticMeshResource? ResolveMesh(AssetReference reference)
    {
        if (_meshCache.TryGetValue(reference, out var cached))
            return cached;
        var resolved = _meshResolver?.Invoke(reference);
        _meshCache.Add(reference, resolved);
        return resolved;
    }

    /// <summary>Resolves and caches one explicit terrain grid.</summary>
    /// <param name="reference">Persistent terrain reference.</param><returns>Resolved terrain or null.</returns>
    private TerrainResource? ResolveTerrain(AssetReference reference)
    {
        if (_terrainCache.TryGetValue(reference, out var cached))
            return cached;
        var resolved = _terrainResolver?.Invoke(reference);
        _terrainCache.Add(reference, resolved);
        return resolved;
    }
}

/// <summary>Allocation-free helpers for common wire diagnostic shapes.</summary>
internal static class PreviewWire
{
    private const int CircleSegments = 24;

    /// <summary>Adds a transformed unit box whose local range is minus to plus one half.</summary>
    /// <param name="destination">Primitive destination.</param><param name="transform">Box transform.</param>
    /// <param name="color">Line color.</param><param name="depthMode">Depth behavior.</param>
    /// <param name="pickingId">Owner identity.</param>
    internal static void AddBox(ScenePreviewList destination, Matrix4x4 transform,
        Vector4 color, ScenePreviewDepthMode depthMode, ScenePreviewPickingId pickingId)
    {
        Span<Vector3> corners = stackalloc Vector3[8]
        {
            new(-.5f,-.5f,-.5f), new(.5f,-.5f,-.5f), new(.5f,.5f,-.5f), new(-.5f,.5f,-.5f),
            new(-.5f,-.5f,.5f), new(.5f,-.5f,.5f), new(.5f,.5f,.5f), new(-.5f,.5f,.5f)
        };
        for (var index = 0; index < corners.Length; index++)
            corners[index] = Vector3.Transform(corners[index], transform);
        AddLoop(destination, corners[..4], color, pickingId, depthMode);
        AddLoop(destination, corners[4..], color, pickingId, depthMode);
        for (var index = 0; index < 4; index++)
            destination.AddLine(new(corners[index], corners[index + 4], color, depthMode, pickingId));
    }

    /// <summary>Adds three great circles for a transformed sphere.</summary>
    /// <param name="destination">Primitive destination.</param><param name="transform">Sphere transform.</param>
    /// <param name="radius">Local radius.</param><param name="color">Line color.</param>
    /// <param name="pickingId">Owner identity.</param>
    internal static void AddSphere(ScenePreviewList destination, Matrix4x4 transform, float radius,
        Vector4 color, ScenePreviewPickingId pickingId)
    {
        AddCircle(destination, transform, radius, 0, 1, color, pickingId);
        AddCircle(destination, transform, radius, 0, 2, color, pickingId);
        AddCircle(destination, transform, radius, 1, 2, color, pickingId);
    }

    /// <summary>Adds a Y-axis cylinder preview.</summary>
    /// <param name="destination">Primitive destination.</param><param name="transform">World transform.</param>
    /// <param name="radius">Local radius.</param><param name="height">Full local height.</param>
    /// <param name="color">Line color.</param><param name="pickingId">Owner identity.</param>
    internal static void AddCylinder(ScenePreviewList destination, Matrix4x4 transform, float radius,
        float height, Vector4 color, ScenePreviewPickingId pickingId)
    {
        AddHorizontalCircle(destination, transform, radius, -height * .5f, color, pickingId);
        AddHorizontalCircle(destination, transform, radius, height * .5f, color, pickingId);
        for (var index = 0; index < 4; index++)
        {
            var angle = index * MathF.PI * .5f;
            var x = MathF.Cos(angle) * radius;
            var z = MathF.Sin(angle) * radius;
            AddTransformed(destination, transform, new(x, -height * .5f, z), new(x, height * .5f, z), color, pickingId);
        }
    }

    /// <summary>Adds a Y-axis capsule preview with hemispherical caps.</summary>
    /// <param name="destination">Primitive destination.</param><param name="transform">World transform.</param>
    /// <param name="radius">Local radius.</param><param name="height">Full local height.</param>
    /// <param name="color">Line color.</param><param name="pickingId">Owner identity.</param>
    internal static void AddCapsule(ScenePreviewList destination, Matrix4x4 transform, float radius,
        float height, Vector4 color, ScenePreviewPickingId pickingId)
    {
        var halfStraight = MathF.Max(0f, height * .5f - radius);
        AddHorizontalCircle(destination, transform, radius, -halfStraight, color, pickingId);
        AddHorizontalCircle(destination, transform, radius, halfStraight, color, pickingId);
        for (var axis = 0; axis < 2; axis++)
        {
            var previous = Vector3.Zero;
            for (var index = 0; index <= CircleSegments; index++)
            {
                var angle = -MathF.PI * .5f + index * MathF.PI / CircleSegments;
                var lateral = MathF.Cos(angle) * radius;
                var y = MathF.Sin(angle) * radius + (angle < 0f ? -halfStraight : halfStraight);
                var point = axis == 0 ? new Vector3(lateral, y, 0f) : new Vector3(0f, y, lateral);
                if (index > 0)
                    AddTransformed(destination, transform, previous, point, color, pickingId);
                previous = point;
            }
        }
    }

    /// <summary>Adds a finite XZ plane rectangle.</summary>
    /// <param name="destination">Primitive destination.</param><param name="transform">World transform.</param>
    /// <param name="size">Full XZ dimensions.</param><param name="color">Line color.</param>
    /// <param name="pickingId">Owner identity.</param>
    internal static void AddPlane(ScenePreviewList destination, Matrix4x4 transform, Vector2 size,
        Vector4 color, ScenePreviewPickingId pickingId)
    {
        Span<Vector3> points = stackalloc Vector3[4]
        {
            new(-size.X*.5f,0f,-size.Y*.5f), new(size.X*.5f,0f,-size.Y*.5f),
            new(size.X*.5f,0f,size.Y*.5f), new(-size.X*.5f,0f,size.Y*.5f)
        };
        for (var index = 0; index < points.Length; index++)
            points[index] = Vector3.Transform(points[index], transform);
        AddLoop(destination, points, color, pickingId);
    }

    /// <summary>Adds a red three-axis cross for an invalid referenced preview.</summary>
    /// <param name="destination">Primitive destination.</param><param name="transform">Owner transform.</param>
    /// <param name="color">Warning color.</param><param name="pickingId">Owner identity.</param>
    internal static void AddWarning(ScenePreviewList destination, Matrix4x4 transform, Vector4 color,
        ScenePreviewPickingId pickingId)
    {
        AddTransformed(destination, transform, new(-.35f,-.35f,-.35f), new(.35f,.35f,.35f), color, pickingId, ScenePreviewDepthMode.AlwaysVisible);
        AddTransformed(destination, transform, new(-.35f,.35f,-.35f), new(.35f,-.35f,.35f), color, pickingId, ScenePreviewDepthMode.AlwaysVisible);
    }

    /// <summary>Adds every explicit triangle edge from a collision mesh.</summary>
    /// <param name="destination">Primitive destination.</param><param name="transform">World transform.</param>
    /// <param name="mesh">Explicit collision mesh.</param><param name="color">Line color.</param>
    /// <param name="pickingId">Owner identity.</param>
    internal static void AddMesh(ScenePreviewList destination, Matrix4x4 transform,
        StaticMeshResource mesh, Vector4 color, ScenePreviewPickingId pickingId)
    {
        var indices = mesh.Indices;
        var vertices = mesh.Vertices;
        for (var index = 0; index + 2 < indices.Length; index += 3)
        {
            var a = vertices[checked((int)indices[index])].Position;
            var b = vertices[checked((int)indices[index + 1])].Position;
            var c = vertices[checked((int)indices[index + 2])].Position;
            AddTransformed(destination, transform, a, b, color, pickingId);
            AddTransformed(destination, transform, b, c, color, pickingId);
            AddTransformed(destination, transform, c, a, color, pickingId);
        }
    }

    /// <summary>Adds explicit terrain grid edges using the same dimensions as physics.</summary>
    /// <param name="destination">Primitive destination.</param><param name="transform">World transform.</param>
    /// <param name="collider">Authored terrain dimensions.</param><param name="terrain">Height samples.</param>
    /// <param name="color">Line color.</param><param name="pickingId">Owner identity.</param>
    internal static void AddTerrain(ScenePreviewList destination, Matrix4x4 transform,
        TerrainColliderComponent collider, TerrainResource terrain, Vector4 color,
        ScenePreviewPickingId pickingId)
    {
        for (var z = 0; z < terrain.Depth; z++)
        {
            for (var x = 0; x < terrain.Width; x++)
            {
                var point = GetTerrainPoint(collider, terrain, x, z);
                if (x + 1 < terrain.Width)
                    AddTransformed(destination, transform, point,
                        GetTerrainPoint(collider, terrain, x + 1, z), color, pickingId);
                if (z + 1 < terrain.Depth)
                    AddTransformed(destination, transform, point,
                        GetTerrainPoint(collider, terrain, x, z + 1), color, pickingId);
            }
        }
    }

    /// <summary>Computes one centered authored terrain sample point.</summary>
    /// <param name="collider">Authored terrain dimensions.</param>
    /// <param name="terrain">Height sample resource.</param>
    /// <param name="x">Sample column.</param><param name="z">Sample row.</param>
    /// <returns>Centered local sample point.</returns>
    private static Vector3 GetTerrainPoint(TerrainColliderComponent collider,
        TerrainResource terrain, int x, int z)
    {
        var u = x / (float)(terrain.Width - 1);
        var v = z / (float)(terrain.Depth - 1);
        return new Vector3((u - .5f) * collider.HorizontalSize.X,
            terrain.GetHeight(x, z) * collider.HeightScale,
            (v - .5f) * collider.HorizontalSize.Y);
    }

    /// <summary>Adds a closed loop from transformed world points.</summary>
    /// <param name="destination">Primitive destination.</param><param name="points">World points.</param>
    /// <param name="color">Line color.</param><param name="pickingId">Owner identity.</param>
    /// <param name="depthMode">Depth behavior.</param>
    internal static void AddLoop(ScenePreviewList destination, ReadOnlySpan<Vector3> points,
        Vector4 color, ScenePreviewPickingId pickingId,
        ScenePreviewDepthMode depthMode = ScenePreviewDepthMode.DepthTested)
    {
        for (var index = 0; index < points.Length; index++)
            destination.AddLine(new(points[index], points[(index + 1) % points.Length], color, depthMode, pickingId));
    }

    /// <summary>Adds one local segment after applying a world transform.</summary>
    private static void AddTransformed(ScenePreviewList destination, Matrix4x4 transform,
        Vector3 start, Vector3 end, Vector4 color, ScenePreviewPickingId pickingId,
        ScenePreviewDepthMode depthMode = ScenePreviewDepthMode.DepthTested)
    {
        destination.AddLine(new(Vector3.Transform(start, transform), Vector3.Transform(end, transform),
            color, depthMode, pickingId));
    }

    /// <summary>Adds one circle in a selected local coordinate plane.</summary>
    private static void AddCircle(ScenePreviewList destination, Matrix4x4 transform, float radius,
        int firstAxis, int secondAxis, Vector4 color, ScenePreviewPickingId pickingId)
    {
        var previous = Vector3.Zero;
        for (var index = 0; index <= CircleSegments; index++)
        {
            var angle = index * MathF.Tau / CircleSegments;
            var point = Vector3.Zero;
            point[firstAxis] = MathF.Cos(angle) * radius;
            point[secondAxis] = MathF.Sin(angle) * radius;
            if (index > 0)
                AddTransformed(destination, transform, previous, point, color, pickingId);
            previous = point;
        }
    }

    /// <summary>Adds one local XZ circle at a selected height.</summary>
    private static void AddHorizontalCircle(ScenePreviewList destination, Matrix4x4 transform,
        float radius, float y, Vector4 color, ScenePreviewPickingId pickingId)
    {
        var previous = new Vector3(radius, y, 0f);
        for (var index = 1; index <= CircleSegments; index++)
        {
            var angle = index * MathF.Tau / CircleSegments;
            var point = new Vector3(MathF.Cos(angle) * radius, y, MathF.Sin(angle) * radius);
            AddTransformed(destination, transform, previous, point, color, pickingId);
            previous = point;
        }
    }
}
