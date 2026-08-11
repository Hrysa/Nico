using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using Engine.Core;
using Engine.Graphics;
using BepuMesh = BepuPhysics.Collidables.Mesh;

namespace Engine.Physics;

/// <summary>Describes one primitive contact produced by a physics step.</summary>
/// <param name="A">First colliding node.</param>
/// <param name="B">Second colliding node.</param>
/// <param name="Normal">World normal pointing from A toward B.</param>
/// <param name="Penetration">Overlap depth.</param>
/// <param name="IsTrigger">Whether either collider suppresses physical response.</param>
public readonly record struct PhysicsContact(
    Node3D A,
    Node3D B,
    Vector3 Normal,
    float Penetration,
    bool IsTrigger);

/// <summary>Describes the closest collision ray hit.</summary>
/// <param name="Node">Owning scene node.</param>
/// <param name="Collider">Authored collider hit, including a compound child.</param>
/// <param name="Position">World-space hit position.</param>
/// <param name="Normal">World-space surface normal.</param>
/// <param name="Distance">Distance from the ray origin.</param>
public readonly record struct PhysicsRayHit(Node3D Node, ColliderComponent Collider,
    Vector3 Position, Vector3 Normal, float Distance);

/// <summary>Adapts engine scene components to a BepuPhysics fixed-step simulation.</summary>
public sealed class PhysicsWorld : IDisposable
{
    private const float PlaneThickness = 0.02f;
    private const int TerrainChunkQuads = 64;
    private readonly BufferPool _bufferPool = new();
    private readonly List<PhysicsBody?> _bodyHandles = [];
    private readonly List<PhysicsBody?> _staticHandles = [];
    private readonly ContactBridge _contactBridge;
    private readonly Func<AssetReference, StaticMeshResource?>? _meshResolver;
    private readonly Func<AssetReference, TerrainResource?>? _terrainResolver;
    private readonly List<PhysicsBody> _bodies = [];
    private readonly List<string> _validationIssues = [];
    private Simulation _simulation;
    private double _accumulator;
    private double _fixedTimeStep = 1d / 60d;
    private int _maxSubsteps = 8;
    private int _colliderCount;
    private bool _disposed;

    /// <summary>Creates an empty Bepu-backed physics world.</summary>
    /// <param name="meshResolver">Optional imported mesh resolver used by static mesh colliders.</param>
    /// <param name="terrainResolver">Optional explicit terrain-data resolver.</param>
    public PhysicsWorld(Func<AssetReference, StaticMeshResource?>? meshResolver = null,
        Func<AssetReference, TerrainResource?>? terrainResolver = null)
    {
        _meshResolver = meshResolver;
        _terrainResolver = terrainResolver;
        _contactBridge = new ContactBridge(this);
        _simulation = CreateSimulation();
    }

    /// <summary>Gets or sets world gravity in units per second squared.</summary>
    public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);

    /// <summary>Gets or sets whether nodes publish interpolated poses between fixed steps.</summary>
    /// <remarks>Enable this for rendered clients. Authoritative and headless simulations should
    /// retain the default false value so scene transforms expose the latest completed step.</remarks>
    public bool EnableInterpolation { get; set; }

    /// <summary>Gets or sets fixed simulation step duration in seconds.</summary>
    public double FixedTimeStep
    {
        get => _fixedTimeStep;
        set
        {
            if (!double.IsFinite(value) || value <= 0d)
                throw new ArgumentOutOfRangeException(nameof(value));
            _fixedTimeStep = value;
            _accumulator = Math.Min(_accumulator, value * MaxSubsteps);
        }
    }

    /// <summary>Gets or sets the maximum catch-up steps performed by one update.</summary>
    public int MaxSubsteps
    {
        get => _maxSubsteps;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            _maxSubsteps = value;
        }
    }

    /// <summary>Gets the number of attached colliders.</summary>
    public int BodyCount => _colliderCount;

    /// <summary>Gets validation issues that left authored colliders inactive during attachment.</summary>
    public IReadOnlyList<string> ValidationIssues => _validationIssues;

    /// <summary>Occurs for each overlapping collider pair during a fixed step.</summary>
    public event Action<PhysicsContact>? Contact;

    /// <summary>Finds the closest collider along a normalized world-space ray.</summary>
    /// <param name="origin">World-space ray origin.</param>
    /// <param name="direction">World-space ray direction; normalization is performed internally.</param>
    /// <param name="maximumDistance">Maximum accepted hit distance.</param>
    /// <param name="collisionMask">Layers eligible for the query.</param>
    /// <param name="hit">Closest hit when successful.</param>
    /// <returns>True when an eligible collider was hit.</returns>
    public bool TryRaycast(Vector3 origin, Vector3 direction, float maximumDistance,
        uint collisionMask, out PhysicsRayHit hit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lengthSquared = direction.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= float.Epsilon)
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (!float.IsFinite(maximumDistance) || maximumDistance <= 0f)
            throw new ArgumentOutOfRangeException(nameof(maximumDistance));
        direction /= MathF.Sqrt(lengthSquared);
        var handler = new RayHitHandler(this, origin, direction, collisionMask);
        _simulation.RayCast(in origin, in direction, maximumDistance, ref handler, 0);
        hit = handler.Hit;
        return handler.HasHit;
    }

    /// <summary>Samples the highest attached explicit terrain at one world XZ position.</summary>
    /// <param name="worldPosition">World position whose XZ coordinates are queried.</param>
    /// <param name="height">Highest matching world-space surface Y.</param>
    /// <returns>True when the point lies over an attached terrain collider.</returns>
    public bool TryGetTerrainHeight(Vector3 worldPosition, out float height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        height = float.NegativeInfinity;
        var found = false;
        for (var index = 0; index < _bodies.Count; index++)
        {
            var body = _bodies[index];
            if (body.Collider is not TerrainColliderComponent terrain ||
                terrain.TerrainData is not { } reference)
                continue;
            var resource = body.TerrainResource ?? _terrainResolver?.Invoke(reference);
            var colliderTransform = Matrix4x4.CreateTranslation(terrain.Center) *
                body.Node.GetModelMatrix();
            if (resource is null || !Matrix4x4.Invert(colliderTransform, out var inverse))
                continue;
            var local = Vector3.Transform(worldPosition, inverse);
            var u = local.X / terrain.HorizontalSize.X + .5f;
            var v = local.Z / terrain.HorizontalSize.Y + .5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f)
                continue;
            var localHeight = resource.Sample(u, v) * terrain.HeightScale;
            var surface = Vector3.Transform(new Vector3(local.X, localHeight, local.Z),
                colliderTransform);
            height = MathF.Max(height, surface.Y);
            found = true;
        }
        return found;
    }

    /// <summary>Replaces only native terrain chunks touched by edited height samples.</summary>
    /// <param name="node">Attached terrain owner.</param>
    /// <param name="resource">Updated terrain sample resource with unchanged dimensions.</param>
    /// <param name="dirtyRegions">Chunk regions returned by TerrainResource dirty mapping.</param>
    public void RebuildTerrain(Node3D node, TerrainResource resource,
        IReadOnlyList<TerrainChunkRegion> dirtyRegions)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(dirtyRegions);
        PhysicsBody? body = null;
        for (var index = 0; index < _bodies.Count; index++)
        {
            if (ReferenceEquals(_bodies[index].Node, node) &&
                _bodies[index].Collider is TerrainColliderComponent)
            {
                body = _bodies[index];
                break;
            }
        }
        if (body is null || body.Collider is not TerrainColliderComponent collider ||
            body.TerrainChunks is null)
            throw new InvalidOperationException("The node has no attached active terrain collider.");
        if (body.TerrainResource is not { } previousResource ||
            resource.Width != previousResource.Width || resource.Depth != previousResource.Depth)
            throw new ArgumentException(
                "Updated terrain dimensions must match the attached terrain resource.",
                nameof(resource));
        for (var dirtyIndex = 0; dirtyIndex < dirtyRegions.Count; dirtyIndex++)
        {
            if (body.FindTerrainChunk(dirtyRegions[dirtyIndex]) < 0)
                throw new ArgumentException(
                    "A dirty region does not match an attached terrain chunk.",
                    nameof(dirtyRegions));
            for (var precedingIndex = 0; precedingIndex < dirtyIndex; precedingIndex++)
            {
                if (dirtyRegions[precedingIndex] == dirtyRegions[dirtyIndex])
                    throw new ArgumentException("Dirty terrain regions must be unique.",
                        nameof(dirtyRegions));
            }
        }
        body.TerrainResource = resource;
        GetColliderPose(node, collider, out var pose, out var scale, out _);
        for (var dirtyIndex = 0; dirtyIndex < dirtyRegions.Count; dirtyIndex++)
        {
            var region = dirtyRegions[dirtyIndex];
            var nativeIndex = body.FindTerrainChunk(region);
            var previous = body.TerrainChunks[nativeIndex];
            _simulation.Statics.Remove(previous.Handle);
            if ((uint)previous.Handle.Value < (uint)_staticHandles.Count)
                _staticHandles[previous.Handle.Value] = null;
            _simulation.Shapes.RecursivelyRemoveAndDispose(previous.Shape, _bufferPool);
            body.RemoveStaticHandle(previous.Handle);
            body.TerrainChunks.RemoveAt(nativeIndex);
            AddTerrainChunk(body, pose, collider, scale, resource, region);
        }
    }

    /// <summary>Rebuilds the Bepu simulation from enabled colliders in one hierarchy.</summary>
    /// <param name="root">Synthetic scene root.</param>
    public void Attach(Node root)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(root);
        ResetSimulation();
        AttachNode(root);
    }

    /// <summary>Advances the accumulator and performs bounded Bepu simulation steps.</summary>
    /// <param name="deltaTime">Scaled elapsed gameplay time in seconds.</param>
    public void Update(double deltaTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(deltaTime) || deltaTime < 0d)
            throw new ArgumentOutOfRangeException(nameof(deltaTime));
        if (deltaTime == 0d || _bodies.Count == 0)
            return;
        SynchronizeExternalTransforms();
        _accumulator = Math.Min(_accumulator + deltaTime, FixedTimeStep * MaxSubsteps);
        var substeps = 0;
        while (_accumulator >= FixedTimeStep && substeps < MaxSubsteps)
        {
            Step((float)FixedTimeStep);
            _accumulator -= FixedTimeStep;
            substeps++;
        }
        if (EnableInterpolation)
            PublishInterpolatedTransforms((float)(_accumulator / FixedTimeStep));
    }

    /// <summary>Releases Bepu simulation and pooled buffers.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _simulation.Dispose();
        _bufferPool.Clear();
        _bodies.Clear();
        _validationIssues.Clear();
        _bodyHandles.Clear();
        _staticHandles.Clear();
        _colliderCount = 0;
    }

    /// <summary>Creates a Bepu simulation using engine-owned contact and integration callbacks.</summary>
    /// <returns>A new empty simulation.</returns>
    private Simulation CreateSimulation()
    {
        return Simulation.Create(
            _bufferPool,
            new NarrowPhaseCallbacks(_contactBridge),
            new PoseIntegratorCallbacks(),
            new SolveDescription(8, 1));
    }

    /// <summary>Discards all retained bodies and creates a fresh simulation.</summary>
    private void ResetSimulation()
    {
        _simulation.Dispose();
        _bufferPool.Clear();
        _bodies.Clear();
        _validationIssues.Clear();
        _bodyHandles.Clear();
        _staticHandles.Clear();
        _colliderCount = 0;
        _accumulator = 0d;
        _simulation = CreateSimulation();
    }

    /// <summary>Discovers physics bodies recursively without iterator allocation.</summary>
    /// <param name="node">Current hierarchy node.</param>
    private void AttachNode(Node node)
    {
        if (node is Node3D node3D)
        {
            RigidBodyComponent? rigidBody = null;
            List<ColliderComponent>? colliders = null;
            var components = node.Components;
            for (var index = 0; index < components.Count; index++)
            {
                if (!components[index].Enabled)
                    continue;
                if (components[index] is RigidBodyComponent foundRigidBody)
                    rigidBody ??= foundRigidBody;
            }
            for (var index = 0; index < components.Count; index++)
            {
                if (components[index] is ColliderComponent { Enabled: true } collider)
                {
                    colliders ??= [];
                    colliders.Add(collider);
                }
            }
            if (colliders is { Count: > 1 } && rigidBody is { MotionType: not RigidBodyMotionType.Static })
                AddMovableColliders(node3D, rigidBody, colliders);
            else if (colliders is not null)
            {
                for (var index = 0; index < colliders.Count; index++)
                    AddBody(node3D, rigidBody, colliders[index]);
            }
        }
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
            AttachNode(children[index]);
    }

    /// <summary>Builds movable solid compounds and separate follower sensors for mixed triggers.</summary>
    /// <param name="node">Shared scene transform.</param><param name="rigidBody">Motion settings.</param>
    /// <param name="colliders">Enabled colliders in authored order.</param>
    private void AddMovableColliders(Node3D node, RigidBodyComponent rigidBody,
        List<ColliderComponent> colliders)
    {
        var triggerCount = 0;
        for (var index = 0; index < colliders.Count; index++)
            triggerCount += colliders[index].IsTrigger ? 1 : 0;
        if (triggerCount == 0 || triggerCount == colliders.Count)
        {
            AddCompoundBody(node, rigidBody, colliders);
            return;
        }
        var solids = new List<ColliderComponent>(colliders.Count - triggerCount);
        for (var index = 0; index < colliders.Count; index++)
        {
            if (!colliders[index].IsTrigger)
                solids.Add(colliders[index]);
        }
        if (solids.Count == 1)
            AddBody(node, rigidBody, solids[0]);
        else
            AddCompoundBody(node, rigidBody, solids);
        for (var index = 0; index < colliders.Count; index++)
        {
            if (colliders[index].IsTrigger)
                AddBody(node, rigidBody, colliders[index], followsNode: true);
        }
    }

    /// <summary>Creates one scaled Bepu primitive and registers its engine mapping.</summary>
    /// <param name="node">Scene transform represented by the body.</param>
    /// <param name="rigidBody">Optional motion settings.</param>
    /// <param name="collider">Collision geometry and material.</param>
    private void AddBody(Node3D node, RigidBodyComponent? rigidBody, ColliderComponent collider,
        bool followsNode = false)
    {
        StaticMeshResource? resolvedMesh = null;
        TerrainResource? resolvedTerrain = null;
        if (collider is MeshColliderComponent meshCollider)
        {
            if (rigidBody is { MotionType: not RigidBodyMotionType.Static })
                throw new InvalidOperationException("Triangle mesh colliders must be static.");
            if (meshCollider.Mesh is not { } reference)
            {
                AddValidationIssue(node, "Mesh collider requires an explicit collision mesh.");
                return;
            }
            resolvedMesh = _meshResolver?.Invoke(reference);
            if (resolvedMesh is null)
            {
                AddValidationIssue(node, $"Collision mesh '{reference}' could not be resolved.");
                return;
            }
        }
        else if (collider is TerrainColliderComponent terrainCollider)
        {
            if (rigidBody is { MotionType: not RigidBodyMotionType.Static })
                throw new InvalidOperationException("Terrain colliders must be static.");
            if (terrainCollider.TerrainData is not { } reference)
            {
                AddValidationIssue(node, "Terrain collider requires explicit terrain data.");
                return;
            }
            resolvedTerrain = _terrainResolver?.Invoke(reference);
            if (resolvedTerrain is null)
            {
                AddValidationIssue(node, $"Terrain data '{reference}' could not be resolved.");
                return;
            }
        }
        GetColliderPose(node, collider, out var pose, out var scale, out var origin);
        var body = new PhysicsBody(node, rigidBody, collider, pose.Position - origin, followsNode);
        _bodies.Add(body);
        _colliderCount++;
        switch (collider)
        {
            case SphereColliderComponent sphere:
                var sphereRadius = sphere.Radius * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
                AddBepuBody(body, pose, new Sphere(sphereRadius));
                break;
            case CapsuleColliderComponent capsule:
                var capsuleRadius = capsule.Radius * MathF.Max(scale.X, scale.Z);
                var capsuleHeight = capsule.Height * scale.Y;
                AddBepuBody(body, pose,
                    new Capsule(capsuleRadius, MathF.Max(0.001f, capsuleHeight - capsuleRadius * 2f)));
                break;
            case CylinderColliderComponent cylinder:
                AddBepuBody(body, pose,
                    new Cylinder(cylinder.Radius * MathF.Max(scale.X, scale.Z), cylinder.Height * scale.Y));
                break;
            case PlaneColliderComponent plane:
                pose.Position += Vector3.Transform(new Vector3(0f, -PlaneThickness * 0.5f, 0f),
                    pose.Orientation);
                body.CenterOffset = pose.Position - origin;
                AddBepuBody(body, pose, new Box(
                    plane.Size.X * scale.X, PlaneThickness, plane.Size.Y * scale.Z));
                break;
            case MeshColliderComponent:
                AddMeshBody(body, pose, scale, resolvedMesh!);
                break;
            case BoxColliderComponent box:
                var size = box.Size * scale;
                AddBepuBody(body, pose, new Box(size.X, size.Y, size.Z));
                break;
            case TerrainColliderComponent terrain:
                AddTerrainBody(body, pose, terrain, scale, resolvedTerrain!);
                break;
            default:
                throw new NotSupportedException(
                    $"Collider type '{collider.GetType().Name}' is not supported.");
        }
    }

    /// <summary>Records one inactive-collider validation issue with scene context.</summary>
    /// <param name="node">Collider owner.</param><param name="message">Validation message.</param>
    private void AddValidationIssue(Node3D node, string message)
    {
        var name = string.IsNullOrWhiteSpace(node.Name) ? "Node3D" : node.Name;
        _validationIssues.Add($"{name}: {message}");
    }

    /// <summary>Creates one native compound for all movable primitive colliders on a node.</summary>
    /// <param name="node">Scene transform represented by the compound.</param>
    /// <param name="rigidBody">Required movable-body settings.</param>
    /// <param name="colliders">Two or more enabled colliders in authored order.</param>
    private void AddCompoundBody(Node3D node, RigidBodyComponent rigidBody,
        List<ColliderComponent> colliders)
    {
        var model = node.GetModelMatrix();
        if (!Matrix4x4.Decompose(model, out var scale, out var orientation, out var origin))
        {
            scale = Vector3.One;
            orientation = Quaternion.Identity;
            origin = node.GetWorldPosition();
        }
        scale = Vector3.Abs(scale);
        var builder = new CompoundBuilder(_bufferPool, _simulation.Shapes, colliders.Count);
        try
        {
            var weight = rigidBody.Mass / colliders.Count;
            for (var index = 0; index < colliders.Count; index++)
                AddCompoundChild(ref builder, colliders[index], scale, weight,
                    rigidBody.MotionType == RigidBodyMotionType.Kinematic);

            Buffer<CompoundChild> children;
            BodyInertia inertia;
            Vector3 localCenter;
            if (rigidBody.MotionType == RigidBodyMotionType.Kinematic)
            {
                builder.BuildKinematicCompound(out children, out localCenter);
                inertia = default;
            }
            else
            {
                builder.BuildDynamicCompound(out children, out inertia, out localCenter);
                inertia.InverseInertiaTensor = default;
            }
            var compound = new Compound(children);
            var shapeIndex = _simulation.Shapes.Add(compound);
            var worldCenter = origin + Vector3.Transform(localCenter, orientation);
            var pose = new RigidPose(worldCenter, orientation);
            var body = new PhysicsBody(node, rigidBody, colliders.ToArray(),
                localCenter, worldCenter - origin);
            _bodies.Add(body);
            _colliderCount += colliders.Count;
            var collidable = new CollidableDescription(shapeIndex, 0.1f);
            var activity = new BodyActivityDescription(0.01f);
            var description = rigidBody.MotionType == RigidBodyMotionType.Kinematic
                ? BodyDescription.CreateKinematic(pose,
                    new BodyVelocity(rigidBody.LinearVelocity), collidable, activity)
                : BodyDescription.CreateDynamic(pose,
                    new BodyVelocity(rigidBody.LinearVelocity), inertia, collidable, activity);
            var handle = _simulation.Bodies.Add(description);
            body.BodyHandle = handle;
            RegisterHandle(_bodyHandles, handle.Value, body);
        }
        finally
        {
            builder.Dispose();
        }
    }

    /// <summary>Adds one supported convex collider to a native compound builder.</summary>
    /// <param name="builder">Native compound builder.</param>
    /// <param name="collider">Authored collider.</param>
    /// <param name="scale">Absolute node world scale.</param>
    /// <param name="weight">Mass contribution.</param>
    /// <param name="kinematic">Whether inertia computation can be skipped.</param>
    private static void AddCompoundChild(ref CompoundBuilder builder, ColliderComponent collider,
        Vector3 scale, float weight, bool kinematic)
    {
        var localPosition = collider.Center * scale;
        switch (collider)
        {
            case SphereColliderComponent sphere:
                var sphereShape = new Sphere(sphere.Radius * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)));
                AddCompoundShape(ref builder, ref sphereShape, localPosition, weight, kinematic);
                break;
            case CapsuleColliderComponent capsule:
                var capsuleRadius = capsule.Radius * MathF.Max(scale.X, scale.Z);
                var capsuleShape = new Capsule(capsuleRadius,
                    MathF.Max(0.001f, capsule.Height * scale.Y - capsuleRadius * 2f));
                AddCompoundShape(ref builder, ref capsuleShape, localPosition, weight, kinematic);
                break;
            case CylinderColliderComponent cylinder:
                var cylinderShape = new Cylinder(cylinder.Radius * MathF.Max(scale.X, scale.Z),
                    cylinder.Height * scale.Y);
                AddCompoundShape(ref builder, ref cylinderShape, localPosition, weight, kinematic);
                break;
            case PlaneColliderComponent plane:
                var planeShape = new Box(plane.Size.X * scale.X, PlaneThickness,
                    plane.Size.Y * scale.Z);
                localPosition.Y -= PlaneThickness * .5f;
                AddCompoundShape(ref builder, ref planeShape, localPosition, weight, kinematic);
                break;
            case BoxColliderComponent box:
                var size = box.Size * scale;
                var boxShape = new Box(size.X, size.Y, size.Z);
                AddCompoundShape(ref builder, ref boxShape, localPosition, weight, kinematic);
                break;
            case MeshColliderComponent:
                throw new InvalidOperationException("Triangle mesh colliders must be static and cannot be compound children of a movable body.");
            case TerrainColliderComponent:
                throw new InvalidOperationException("Terrain colliders must be static and cannot be compound children of a movable body.");
            default:
                throw new NotSupportedException($"Collider type '{collider.GetType().Name}' is not supported in compounds.");
        }
    }

    /// <summary>Adds one unmanaged convex shape with an identity local orientation.</summary>
    /// <typeparam name="TShape">Bepu convex shape type.</typeparam>
    /// <param name="builder">Native compound builder.</param><param name="shape">Child shape.</param>
    /// <param name="localPosition">Child center relative to the node.</param>
    /// <param name="weight">Mass contribution.</param><param name="kinematic">Whether inertia is unnecessary.</param>
    private static void AddCompoundShape<TShape>(ref CompoundBuilder builder, ref TShape shape,
        Vector3 localPosition, float weight, bool kinematic)
        where TShape : unmanaged, IConvexShape
    {
        var localPose = new RigidPose(localPosition, Quaternion.Identity);
        if (kinematic)
            builder.AddForKinematic(in shape, in localPose, weight);
        else
            builder.Add(in shape, in localPose, weight);
    }

    /// <summary>Adds one imported triangle mesh as a static BEPU collidable.</summary>
    /// <param name="body">Engine body mapping receiving the static handle.</param>
    /// <param name="pose">World pose of the mesh origin.</param>
    /// <param name="scale">Absolute world scale applied by BEPU.</param>
    private void AddMeshBody(PhysicsBody body, RigidPose pose, Vector3 scale,
        StaticMeshResource resource)
    {
        var triangleCount = resource.Indices.Length / 3;
        _bufferPool.Take<Triangle>(triangleCount, out var triangles);
        for (var triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
        {
            var indexOffset = triangleIndex * 3;
            var a = resource.Vertices[checked((int)resource.Indices[indexOffset])].Position;
            var b = resource.Vertices[checked((int)resource.Indices[indexOffset + 2])].Position;
            var c = resource.Vertices[checked((int)resource.Indices[indexOffset + 1])].Position;
            triangles[triangleIndex] = new Triangle(a, b, c);
        }
        var mesh = new BepuMesh(triangles, scale, _bufferPool);
        var shapeIndex = _simulation.Shapes.Add(mesh);
        var handle = _simulation.Statics.Add(new StaticDescription(pose, shapeIndex));
        body.StaticHandle = handle;
        RegisterHandle(_staticHandles, handle.Value, body);
    }

    /// <summary>Adds an explicit height grid as a static triangle terrain shape.</summary>
    /// <param name="body">Engine body mapping receiving the static handle.</param>
    /// <param name="pose">World pose of the terrain center.</param>
    /// <param name="collider">Authored terrain dimensions and asset reference.</param>
    /// <param name="scale">Absolute node world scale.</param>
    private void AddTerrainBody(PhysicsBody body, RigidPose pose,
        TerrainColliderComponent collider, Vector3 scale, TerrainResource resource)
    {
        body.TerrainResource = resource;
        var regions = resource.GetChunkRegions(TerrainChunkQuads);
        for (var index = 0; index < regions.Length; index++)
            AddTerrainChunk(body, pose, collider, scale, resource, regions[index]);
    }

    /// <summary>Builds and registers one bounded native terrain mesh region.</summary>
    /// <param name="body">Engine terrain body.</param><param name="pose">Shared terrain pose.</param>
    /// <param name="collider">Authored terrain dimensions.</param><param name="scale">World scale.</param>
    /// <param name="resource">Current height samples.</param><param name="region">Quad region.</param>
    private void AddTerrainChunk(PhysicsBody body, RigidPose pose,
        TerrainColliderComponent collider, Vector3 scale, TerrainResource resource,
        TerrainChunkRegion region)
    {
        var startZ = region.StartZ;
        var endZ = startZ + region.QuadCountZ;
        var startX = region.StartX;
        var endX = startX + region.QuadCountX;
        var triangleCount = checked(region.QuadCountX * region.QuadCountZ * 2);
        _bufferPool.Take<Triangle>(triangleCount, out var triangles);
        var triangleIndex = 0;
        for (var z = startZ; z < endZ; z++)
        {
            for (var x = startX; x < endX; x++)
            {
                var a = GetTerrainVertex(resource, collider, x, z);
                var b = GetTerrainVertex(resource, collider, x + 1, z);
                var c = GetTerrainVertex(resource, collider, x + 1, z + 1);
                var d = GetTerrainVertex(resource, collider, x, z + 1);
                triangles[triangleIndex++] = new Triangle(a, b, c);
                triangles[triangleIndex++] = new Triangle(a, c, d);
            }
        }
        var mesh = new BepuMesh(triangles, scale, _bufferPool);
        var shapeIndex = _simulation.Shapes.Add(mesh);
        var handle = _simulation.Statics.Add(new StaticDescription(pose, shapeIndex));
        body.AddTerrainChunk(new TerrainNativeChunk(region, handle, shapeIndex));
        RegisterHandle(_staticHandles, handle.Value, body);
    }

    /// <summary>Computes one centered local terrain vertex from a normalized sample.</summary>
    /// <param name="resource">Height sample grid.</param><param name="collider">Terrain dimensions.</param>
    /// <param name="x">Sample column.</param><param name="z">Sample row.</param>
    /// <returns>Local terrain vertex.</returns>
    private static Vector3 GetTerrainVertex(TerrainResource resource,
        TerrainColliderComponent collider, int x, int z)
    {
        var u = x / (float)(resource.Width - 1);
        var v = z / (float)(resource.Depth - 1);
        return new Vector3(
            (u - .5f) * collider.HorizontalSize.X,
            resource.GetHeight(x, z) * collider.HeightScale,
            (v - .5f) * collider.HorizontalSize.Y);
    }

    /// <summary>Adds one convex shape as a dynamic, kinematic, or static Bepu collidable.</summary>
    /// <typeparam name="TShape">Unmanaged Bepu convex shape type.</typeparam>
    /// <param name="body">Engine mapping receiving the Bepu handle.</param>
    /// <param name="pose">Initial collider-center pose.</param>
    /// <param name="shape">Scaled collision shape.</param>
    private void AddBepuBody<TShape>(PhysicsBody body, RigidPose pose, TShape shape)
        where TShape : unmanaged, IConvexShape
    {
        var shapeIndex = _simulation.Shapes.Add(shape);
        var rigidBody = body.RigidBody;
        if (rigidBody is null || rigidBody.MotionType == RigidBodyMotionType.Static)
        {
            var staticDescription = new StaticDescription(pose, shapeIndex);
            var handle = _simulation.Statics.Add(staticDescription);
            body.StaticHandle = handle;
            RegisterHandle(_staticHandles, handle.Value, body);
            return;
        }

        var collidable = new CollidableDescription(shapeIndex, 0.1f);
        var activity = new BodyActivityDescription(0.01f);
        BodyDescription description;
        if (body.FollowsNode || rigidBody.MotionType == RigidBodyMotionType.Kinematic)
        {
            description = BodyDescription.CreateKinematic(
                pose, new BodyVelocity(rigidBody.LinearVelocity), collidable, activity);
        }
        else
        {
            var inertia = shape.ComputeInertia(rigidBody.Mass);
            inertia.InverseInertiaTensor = default;
            description = BodyDescription.CreateDynamic(
                pose, new BodyVelocity(rigidBody.LinearVelocity), inertia, collidable, activity);
        }
        var bodyHandle = _simulation.Bodies.Add(description);
        body.BodyHandle = bodyHandle;
        RegisterHandle(_bodyHandles, bodyHandle.Value, body);
    }

    /// <summary>Stores one handle-indexed mapping without dictionary lookup in callbacks.</summary>
    /// <param name="mappings">Handle mapping list to grow.</param>
    /// <param name="handle">Nonnegative Bepu handle value.</param>
    /// <param name="body">Mapped engine body.</param>
    private static void RegisterHandle(List<PhysicsBody?> mappings, int handle, PhysicsBody body)
    {
        while (mappings.Count <= handle)
            mappings.Add(null);
        mappings[handle] = body;
    }

    /// <summary>Restores authoritative poses or recognizes transforms changed by game code.</summary>
    private void SynchronizeExternalTransforms()
    {
        for (var index = 0; index < _bodies.Count; index++)
        {
            var body = _bodies[index];
            var worldPosition = body.Node.GetWorldPosition();
            if (body.BodyHandle is { } handle)
            {
                var reference = _simulation.Bodies[handle];
                if (body.FollowsNode)
                {
                    GetBodyPose(body, out var followerPose, out var followerOrigin);
                    body.CenterOffset = followerPose.Position - followerOrigin;
                    reference.Pose = followerPose;
                    reference.Velocity = default;
                    _simulation.Bodies.UpdateBounds(handle);
                    continue;
                }
                var externallyMoved = EnableInterpolation
                    ? !body.HasPresentationPosition ||
                      Vector3.DistanceSquared(worldPosition, body.PresentationPosition) > 0.0000001f
                    : Vector3.DistanceSquared(worldPosition, body.SimulationPosition) > 0.0000001f;
                if (body.RigidBody?.MotionType == RigidBodyMotionType.Kinematic || externallyMoved)
                {
                    GetBodyPose(body, out var pose, out var origin);
                    body.CenterOffset = pose.Position - origin;
                    reference.Pose = pose;
                    _simulation.Bodies.UpdateBounds(handle);
                    if (externallyMoved || body.RigidBody?.LinearVelocity.LengthSquared() > 0f)
                        reference.Awake = true;
                    body.PreviousPosition = worldPosition;
                    body.SimulationPosition = worldPosition;
                }
                else if (EnableInterpolation)
                {
                    SetWorldPosition(body.Node, body.SimulationPosition);
                }
                continue;
            }
            if (body.StaticHandles is not { Count: > 0 } staticHandles)
                continue;
            GetBodyPose(body, out var staticPose, out var staticOrigin);
            if (body.Collider is PlaneColliderComponent)
                staticPose.Position += Vector3.Transform(
                    new Vector3(0f, -PlaneThickness * 0.5f, 0f), staticPose.Orientation);
            body.CenterOffset = staticPose.Position - staticOrigin;
            for (var handleIndex = 0; handleIndex < staticHandles.Count; handleIndex++)
            {
                var staticHandle = staticHandles[handleIndex];
                _simulation.Statics[staticHandle].Pose = staticPose;
                _simulation.Statics.UpdateBounds(staticHandle);
            }
        }
    }

    /// <summary>Advances one fixed Bepu step and copies authoritative state back to components.</summary>
    /// <param name="deltaTime">Fixed step duration.</param>
    private void Step(float deltaTime)
    {
        for (var index = 0; index < _bodies.Count; index++)
        {
            var body = _bodies[index];
            body.PreviousPosition = body.SimulationPosition;
            if (body.FollowsNode || body.BodyHandle is not { } handle ||
                body.RigidBody is not { } rigidBody)
                continue;
            var reference = _simulation.Bodies[handle];
            var velocity = rigidBody.LinearVelocity;
            var authoredVelocityChanged =
                Vector3.DistanceSquared(velocity, reference.Velocity.Linear) > 0.0000001f;
            if (!reference.Awake && !authoredVelocityChanged)
            {
                rigidBody.LinearVelocity = Vector3.Zero;
                continue;
            }
            if (authoredVelocityChanged && velocity.LengthSquared() > 0f)
                reference.Awake = true;
            if (rigidBody.MotionType == RigidBodyMotionType.Dynamic && rigidBody.UseGravity)
                velocity += Gravity * rigidBody.GravityScale * deltaTime;
            if (rigidBody.MotionType == RigidBodyMotionType.Dynamic)
                velocity *= MathF.Max(0f, 1f - rigidBody.LinearDamping * deltaTime);
            reference.Velocity.Linear = velocity;
            reference.Velocity.Angular = Vector3.Zero;
        }

        _simulation.Timestep(deltaTime);

        for (var index = 0; index < _bodies.Count; index++)
        {
            var body = _bodies[index];
            if (body.FollowsNode || body.BodyHandle is not { } handle)
                continue;
            var reference = _simulation.Bodies[handle];
            body.SimulationPosition = reference.Pose.Position - body.CenterOffset;
            if (body.RigidBody is { } rigidBody)
                rigidBody.LinearVelocity = reference.Velocity.Linear;
            if (!EnableInterpolation)
                SetWorldPosition(body.Node, body.SimulationPosition);
        }
    }

    /// <summary>Publishes smooth render poses without changing authoritative Bepu state.</summary>
    /// <param name="alpha">Remaining fixed-step fraction from zero through one.</param>
    private void PublishInterpolatedTransforms(float alpha)
    {
        alpha = Math.Clamp(alpha, 0f, 1f);
        for (var index = 0; index < _bodies.Count; index++)
        {
            var body = _bodies[index];
            if (body.BodyHandle is null)
                continue;
            var position = Vector3.Lerp(body.PreviousPosition, body.SimulationPosition, alpha);
            SetWorldPosition(body.Node, position);
            body.PresentationPosition = position;
            body.HasPresentationPosition = true;
        }
    }

    /// <summary>Computes a collider-center pose and positive world scale.</summary>
    /// <param name="node">Transformed scene node.</param>
    /// <param name="collider">Collider supplying its local center.</param>
    /// <param name="pose">Resulting world pose.</param>
    /// <param name="scale">Absolute world scale.</param>
    /// <param name="origin">World-space node origin.</param>
    private static void GetColliderPose(
        Node3D node,
        ColliderComponent collider,
        out RigidPose pose,
        out Vector3 scale,
        out Vector3 origin)
    {
        var matrix = node.GetModelMatrix();
        origin = Vector3.Transform(Vector3.Zero, matrix);
        if (!Matrix4x4.Decompose(matrix, out scale, out var orientation, out _))
        {
            scale = Vector3.One;
            orientation = Quaternion.Identity;
        }
        scale = Vector3.Abs(scale);
        pose = new RigidPose(Vector3.Transform(collider.Center, matrix), orientation);
    }

    /// <summary>Computes the current native pose for a primitive or compound body.</summary>
    /// <param name="body">Retained engine body mapping.</param>
    /// <param name="pose">Resulting native pose.</param>
    /// <param name="origin">Current world node origin.</param>
    private static void GetBodyPose(PhysicsBody body, out RigidPose pose, out Vector3 origin)
    {
        if (!body.IsCompound)
        {
            GetColliderPose(body.Node, body.Collider, out pose, out _, out origin);
            return;
        }
        var matrix = body.Node.GetModelMatrix();
        origin = Vector3.Transform(Vector3.Zero, matrix);
        if (!Matrix4x4.Decompose(matrix, out _, out var orientation, out _))
            orientation = Quaternion.Identity;
        pose = new RigidPose(origin + Vector3.Transform(body.LocalCenter, orientation), orientation);
    }

    /// <summary>Assigns a world position while preserving parent-relative storage.</summary>
    /// <param name="node">Node to move.</param>
    /// <param name="worldPosition">Desired world position.</param>
    private static void SetWorldPosition(Node3D node, Vector3 worldPosition)
    {
        if (node.Parent is not Node3D parent)
        {
            node.Position = worldPosition;
            return;
        }
        if (Matrix4x4.Invert(parent.GetModelMatrix(), out var inverseParent))
            node.Position = Vector3.Transform(worldPosition, inverseParent);
    }

    /// <summary>Resolves an engine body from a Bepu collidable reference.</summary>
    /// <param name="reference">Bepu collidable reference.</param>
    /// <returns>The mapped body, or null for an unknown handle.</returns>
    private PhysicsBody? GetBody(CollidableReference reference)
    {
        var mappings = reference.Mobility == CollidableMobility.Static
            ? _staticHandles : _bodyHandles;
        var handle = reference.RawHandleValue;
        return (uint)handle < (uint)mappings.Count ? mappings[handle] : null;
    }

    /// <summary>Reports a generated manifold and supplies pair material properties.</summary>
    /// <typeparam name="TManifold">Bepu manifold type.</typeparam>
    /// <param name="pair">Collidable pair owning the manifold.</param>
    /// <param name="manifold">Generated contact manifold.</param>
    /// <param name="material">Material properties used for response.</param>
    /// <returns>True when Bepu should create a response constraint.</returns>
    private bool ConfigureContact<TManifold>(
        CollidablePair pair,
        ref TManifold manifold,
        out PairMaterialProperties material)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        var a = GetBody(pair.A);
        var b = GetBody(pair.B);
        if (a is null || b is null)
        {
            material = new PairMaterialProperties(0.5f, 2f, new SpringSettings(30f, 1f));
            return true;
        }
        material = new PairMaterialProperties(
            MathF.Sqrt(a.Collider.Friction * b.Collider.Friction),
            2f + MathF.Max(a.Collider.Restitution, b.Collider.Restitution) * 8f,
            new SpringSettings(30f, 1f));
        var trigger = !a.IsCompound && a.Collider.IsTrigger ||
            !b.IsCompound && b.Collider.IsTrigger;
        if (manifold.Count > 0)
        {
            manifold.GetContact(0, out _, out var normal, out var depth, out _);
            if (depth > 0f)
                Contact?.Invoke(new PhysicsContact(a.Node, b.Node, -normal, depth, trigger));
        }
        return !trigger;
    }

    /// <summary>Configures an exact compound child contact, including trigger notification.</summary>
    /// <param name="pair">Native collidable pair.</param><param name="childIndexA">First child.</param>
    /// <param name="childIndexB">Second child.</param><param name="manifold">Generated child manifold.</param>
    /// <returns>False for an authored trigger child so no response constraint is created.</returns>
    private bool ConfigureChildContact(CollidablePair pair, int childIndexA, int childIndexB,
        ref ConvexContactManifold manifold)
    {
        var a = GetBody(pair.A);
        var b = GetBody(pair.B);
        if (a is null || b is null)
            return true;
        var colliderA = a.GetCollider(childIndexA);
        var colliderB = b.GetCollider(childIndexB);
        var trigger = colliderA.IsTrigger || colliderB.IsTrigger;
        if (trigger && manifold.Count > 0)
        {
            manifold.GetContact(0, out _, out var normal, out var depth, out _);
            if (depth > 0f)
                Contact?.Invoke(new PhysicsContact(a.Node, b.Node, -normal, depth, true));
        }
        return !trigger;
    }

    /// <summary>Checks whether a broad-phase pair needs narrow-phase contact generation.</summary>
    /// <param name="a">First collidable.</param>
    /// <param name="b">Second collidable.</param>
    /// <returns>True for dynamic pairs and trigger-only static/kinematic pairs.</returns>
    private bool AllowContact(CollidableReference a, CollidableReference b)
    {
        var bodyA = GetBody(a);
        var bodyB = GetBody(b);
        if (bodyA is null || bodyB is null || !HasCompatibleLayers(bodyA, bodyB))
            return false;
        if (a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic)
            return true;
        return bodyA.HasTrigger || bodyB.HasTrigger;
    }

    /// <summary>Checks whether any collider pair between two native bodies can interact.</summary>
    /// <param name="a">First engine body.</param><param name="b">Second engine body.</param>
    /// <returns>True when at least one authored layer pair is mutually enabled.</returns>
    private static bool HasCompatibleLayers(PhysicsBody a, PhysicsBody b)
    {
        for (var aIndex = 0; aIndex < a.Colliders.Length; aIndex++)
        {
            var colliderA = a.Colliders[aIndex];
            for (var bIndex = 0; bIndex < b.Colliders.Length; bIndex++)
            {
                var colliderB = b.Colliders[bIndex];
                if ((colliderA.CollisionMask & colliderB.CollisionLayer) != 0u &&
                    (colliderB.CollisionMask & colliderA.CollisionLayer) != 0u)
                    return true;
            }
        }
        return false;
    }

    /// <summary>Checks exact compound child layers during child-level narrow phase.</summary>
    /// <param name="pair">Owning native collidable pair.</param>
    /// <param name="childIndexA">First compound child index.</param>
    /// <param name="childIndexB">Second compound child index.</param>
    /// <returns>True when the selected authored colliders mutually enable contact.</returns>
    private bool AllowChildContact(CollidablePair pair, int childIndexA, int childIndexB)
    {
        var bodyA = GetBody(pair.A);
        var bodyB = GetBody(pair.B);
        if (bodyA is null || bodyB is null)
            return false;
        var colliderA = bodyA.GetCollider(childIndexA);
        var colliderB = bodyB.GetCollider(childIndexB);
        return (colliderA.CollisionMask & colliderB.CollisionLayer) != 0u &&
            (colliderB.CollisionMask & colliderA.CollisionLayer) != 0u;
    }

    /// <summary>Bridges Bepu's copied callback structs back to their owning world.</summary>
    private sealed class ContactBridge
    {
        /// <summary>Gets the callback target.</summary>
        internal PhysicsWorld World { get; }

        /// <summary>Creates a bridge for one world.</summary>
        /// <param name="world">Owning world.</param>
        internal ContactBridge(PhysicsWorld world)
        {
            World = world;
        }
    }

    /// <summary>Collects the closest Bepu ray hit while applying engine collision layers.</summary>
    private struct RayHitHandler : IRayHitHandler
    {
        private readonly PhysicsWorld _world;
        private readonly Vector3 _origin;
        private readonly Vector3 _direction;
        private readonly uint _collisionMask;

        /// <summary>Gets whether a hit has been collected.</summary>
        internal bool HasHit { get; private set; }

        /// <summary>Gets the closest collected engine hit.</summary>
        internal PhysicsRayHit Hit { get; private set; }

        /// <summary>Creates a query callback.</summary>
        /// <param name="world">World used for handle mapping.</param>
        /// <param name="origin">Normalized ray origin.</param>
        /// <param name="direction">Normalized ray direction.</param>
        /// <param name="collisionMask">Eligible engine layers.</param>
        internal RayHitHandler(PhysicsWorld world, Vector3 origin, Vector3 direction,
            uint collisionMask)
        {
            _world = world;
            _origin = origin;
            _direction = direction;
            _collisionMask = collisionMask;
            HasHit = false;
            Hit = default;
        }

        /// <inheritdoc/>
        public bool AllowTest(CollidableReference collidable)
        {
            var body = _world.GetBody(collidable);
            if (body is null)
                return false;
            for (var index = 0; index < body.Colliders.Length; index++)
            {
                if ((_collisionMask & body.Colliders[index].CollisionLayer) != 0u)
                    return true;
            }
            return false;
        }

        /// <inheritdoc/>
        public bool AllowTest(CollidableReference collidable, int childIndex)
        {
            var body = _world.GetBody(collidable);
            return body is not null &&
                (_collisionMask & body.GetCollider(childIndex).CollisionLayer) != 0u;
        }

        /// <inheritdoc/>
        public void OnRayHit(in BepuPhysics.Trees.RayData ray, ref float maximumT,
            float t, in Vector3 normal, CollidableReference collidable, int childIndex)
        {
            var body = _world.GetBody(collidable);
            if (body is null)
                return;
            maximumT = t;
            HasHit = true;
            Hit = new PhysicsRayHit(body.Node, body.GetCollider(childIndex),
                _origin + _direction * t, normal, t);
        }
    }

    /// <summary>Configures Bepu contact filtering, materials, triggers, and notifications.</summary>
    private struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
    {
        private readonly ContactBridge _bridge;

        /// <summary>Creates callbacks targeting one retained bridge.</summary>
        /// <param name="bridge">Owning-world bridge.</param>
        internal NarrowPhaseCallbacks(ContactBridge bridge)
        {
            _bridge = bridge;
        }

        /// <inheritdoc/>
        public void Initialize(Simulation simulation)
        {
        }

        /// <inheritdoc/>
        public bool AllowContactGeneration(
            int workerIndex,
            CollidableReference a,
            CollidableReference b,
            ref float speculativeMargin) => _bridge.World.AllowContact(a, b);

        /// <inheritdoc/>
        public bool ConfigureContactManifold<TManifold>(
            int workerIndex,
            CollidablePair pair,
            ref TManifold manifold,
            out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold> =>
            _bridge.World.ConfigureContact(pair, ref manifold, out pairMaterial);

        /// <inheritdoc/>
        public bool AllowContactGeneration(
            int workerIndex,
            CollidablePair pair,
            int childIndexA,
            int childIndexB) => _bridge.World.AllowChildContact(pair, childIndexA, childIndexB);

        /// <inheritdoc/>
        public bool ConfigureContactManifold(
            int workerIndex,
            CollidablePair pair,
            int childIndexA,
            int childIndexB,
            ref ConvexContactManifold manifold) =>
            _bridge.World.ConfigureChildContact(
                pair, childIndexA, childIndexB, ref manifold);

        /// <inheritdoc/>
        public void Dispose()
        {
        }
    }

    /// <summary>Leaves velocity integration to the engine's per-component pre-step update.</summary>
    private struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
    {
        /// <inheritdoc/>
        public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

        /// <inheritdoc/>
        public bool AllowSubstepsForUnconstrainedBodies => false;

        /// <inheritdoc/>
        public bool IntegrateVelocityForKinematics => false;

        /// <inheritdoc/>
        public void Initialize(Simulation simulation)
        {
        }

        /// <inheritdoc/>
        public void PrepareForIntegration(float dt)
        {
        }

        /// <inheritdoc/>
        public void IntegrateVelocity(
            Vector<int> bodyIndices,
            Vector3Wide position,
            QuaternionWide orientation,
            BodyInertiaWide localInertia,
            Vector<int> integrationMask,
            int workerIndex,
            Vector<float> dt,
            ref BodyVelocityWide velocity)
        {
        }
    }

    /// <summary>Stores engine state and handles participating in one Bepu body.</summary>
    private sealed class PhysicsBody
    {
        /// <summary>Gets the transformed node.</summary>
        internal Node3D Node { get; }

        /// <summary>Gets the optional motion component.</summary>
        internal RigidBodyComponent? RigidBody { get; }

        /// <summary>Gets the required collision component.</summary>
        internal ColliderComponent Collider { get; }

        /// <summary>Gets all authored colliders represented by this native body.</summary>
        internal ColliderComponent[] Colliders { get; }

        /// <summary>Gets whether this mapping represents a native compound.</summary>
        internal bool IsCompound => Colliders.Length > 1;

        /// <summary>Gets whether any authored collider represented by the body is a trigger.</summary>
        internal bool HasTrigger
        {
            get
            {
                for (var index = 0; index < Colliders.Length; index++)
                {
                    if (Colliders[index].IsTrigger)
                        return true;
                }
                return false;
            }
        }

        /// <summary>Gets compound center of mass in scaled node-local coordinates.</summary>
        internal Vector3 LocalCenter { get; }

        /// <summary>Gets or sets the Bepu body handle.</summary>
        internal BodyHandle? BodyHandle { get; set; }

        /// <summary>Gets or sets the Bepu static handle.</summary>
        internal StaticHandle? StaticHandle
        {
            get => StaticHandles is { Count: > 0 } handles ? handles[0] : null;
            set
            {
                StaticHandles ??= [];
                StaticHandles.Clear();
                if (value is { } handle)
                    StaticHandles.Add(handle);
            }
        }

        /// <summary>Gets all native static handles, including terrain chunks.</summary>
        internal List<StaticHandle>? StaticHandles { get; private set; }

        /// <summary>Gets native chunks owned by an explicit terrain collider.</summary>
        internal List<TerrainNativeChunk>? TerrainChunks { get; private set; }

        /// <summary>Gets or sets current height samples used by queries and rebuilds.</summary>
        internal TerrainResource? TerrainResource { get; set; }

        /// <summary>Gets or sets the world offset from node origin to collider center.</summary>
        internal Vector3 CenterOffset { get; set; }

        /// <summary>Gets or sets the preceding completed-step position.</summary>
        internal Vector3 PreviousPosition { get; set; }

        /// <summary>Gets or sets the latest authoritative simulation position.</summary>
        internal Vector3 SimulationPosition { get; set; }

        /// <summary>Gets or sets the pose most recently exposed for rendering.</summary>
        internal Vector3 PresentationPosition { get; set; }

        /// <summary>Gets or sets whether a presentation pose has been published.</summary>
        internal bool HasPresentationPosition { get; set; }

        /// <summary>Gets whether this sensor follows the node instead of publishing simulation pose.</summary>
        internal bool FollowsNode { get; }

        /// <summary>Creates retained state for one Bepu collidable.</summary>
        /// <param name="node">Transformed scene node.</param>
        /// <param name="rigidBody">Optional motion component.</param>
        /// <param name="collider">Required collision component.</param>
        /// <param name="centerOffset">World offset from node origin to collider center.</param>
        internal PhysicsBody(
            Node3D node,
            RigidBodyComponent? rigidBody,
            ColliderComponent collider,
            Vector3 centerOffset,
            bool followsNode = false)
        {
            Node = node;
            RigidBody = rigidBody;
            Collider = collider;
            Colliders = [collider];
            FollowsNode = followsNode;
            CenterOffset = centerOffset;
            var position = node.GetWorldPosition();
            PreviousPosition = position;
            SimulationPosition = position;
            PresentationPosition = position;
        }

        /// <summary>Creates retained state for a native compound collidable.</summary>
        /// <param name="node">Transformed scene node.</param>
        /// <param name="rigidBody">Movable body component.</param>
        /// <param name="colliders">Compound children in authored order.</param>
        /// <param name="localCenter">Scaled local center of mass.</param>
        /// <param name="centerOffset">World offset from node origin to center of mass.</param>
        internal PhysicsBody(Node3D node, RigidBodyComponent rigidBody,
            ColliderComponent[] colliders, Vector3 localCenter, Vector3 centerOffset)
        {
            Node = node;
            RigidBody = rigidBody;
            Colliders = colliders;
            Collider = colliders[0];
            CenterOffset = centerOffset;
            LocalCenter = localCenter;
            var position = node.GetWorldPosition();
            PreviousPosition = position;
            SimulationPosition = position;
            PresentationPosition = position;
        }

        /// <summary>Gets a compound child collider, or the sole primitive collider.</summary>
        /// <param name="childIndex">Native compound child index.</param>
        /// <returns>Authored collider represented by the child.</returns>
        internal ColliderComponent GetCollider(int childIndex)
        {
            return (uint)childIndex < (uint)Colliders.Length ? Colliders[childIndex] : Collider;
        }

        /// <summary>Adds one native static handle without replacing preceding chunks.</summary>
        /// <param name="handle">New static handle.</param>
        internal void AddStaticHandle(StaticHandle handle)
        {
            StaticHandles ??= [];
            StaticHandles.Add(handle);
        }

        /// <summary>Adds one native terrain chunk and its static handle.</summary>
        /// <param name="chunk">Native terrain chunk.</param>
        internal void AddTerrainChunk(TerrainNativeChunk chunk)
        {
            TerrainChunks ??= [];
            TerrainChunks.Add(chunk);
            AddStaticHandle(chunk.Handle);
        }

        /// <summary>Finds a native terrain chunk by its exact authored quad region.</summary>
        /// <param name="region">Requested region.</param><returns>Chunk index or minus one.</returns>
        internal int FindTerrainChunk(TerrainChunkRegion region)
        {
            if (TerrainChunks is null)
                return -1;
            for (var index = 0; index < TerrainChunks.Count; index++)
            {
                if (TerrainChunks[index].Region == region)
                    return index;
            }
            return -1;
        }

        /// <summary>Removes one static handle retained by a replaced terrain chunk.</summary>
        /// <param name="handle">Handle to remove.</param>
        internal void RemoveStaticHandle(StaticHandle handle)
        {
            StaticHandles?.Remove(handle);
        }
    }

    /// <summary>Retains one replaceable native terrain chunk.</summary>
    /// <param name="Region">Authored quad region.</param><param name="Handle">Bepu static handle.</param>
    /// <param name="Shape">Bepu mesh shape index.</param>
    private readonly record struct TerrainNativeChunk(
        TerrainChunkRegion Region, StaticHandle Handle, TypedIndex Shape);
}
