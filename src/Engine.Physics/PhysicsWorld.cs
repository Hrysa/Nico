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

/// <summary>Adapts engine scene components to a BepuPhysics fixed-step simulation.</summary>
public sealed class PhysicsWorld : IDisposable
{
    private const float PlaneThickness = 0.02f;
    private const float PlaneExtent = 100_000f;
    private readonly BufferPool _bufferPool = new();
    private readonly List<PhysicsBody?> _bodyHandles = [];
    private readonly List<PhysicsBody?> _staticHandles = [];
    private readonly ContactBridge _contactBridge;
    private readonly Func<AssetReference, StaticMeshResource?>? _meshResolver;
    private readonly List<PhysicsBody> _bodies = [];
    private Simulation _simulation;
    private double _accumulator;
    private double _fixedTimeStep = 1d / 60d;
    private int _maxSubsteps = 8;
    private bool _disposed;

    /// <summary>Creates an empty Bepu-backed physics world.</summary>
    /// <param name="meshResolver">Optional imported mesh resolver used by static mesh colliders.</param>
    public PhysicsWorld(Func<AssetReference, StaticMeshResource?>? meshResolver = null)
    {
        _meshResolver = meshResolver;
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
    public int BodyCount => _bodies.Count;

    /// <summary>Occurs for each overlapping collider pair during a fixed step.</summary>
    public event Action<PhysicsContact>? Contact;

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
        _bodyHandles.Clear();
        _staticHandles.Clear();
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
        _bodyHandles.Clear();
        _staticHandles.Clear();
        _accumulator = 0d;
        _simulation = CreateSimulation();
    }

    /// <summary>Discovers physics bodies recursively without iterator allocation.</summary>
    /// <param name="node">Current hierarchy node.</param>
    private void AttachNode(Node node)
    {
        if (node is Node3D node3D)
        {
            ColliderComponent? collider = null;
            RigidBodyComponent? rigidBody = null;
            var components = node.Components;
            for (var index = 0; index < components.Count; index++)
            {
                if (!components[index].Enabled)
                    continue;
                if (components[index] is ColliderComponent foundCollider)
                    collider ??= foundCollider;
                else if (components[index] is RigidBodyComponent foundRigidBody)
                    rigidBody ??= foundRigidBody;
            }
            if (collider is not null)
            {
                if (collider.Shape == ColliderShape.Mesh && collider.Mesh is null)
                    AddDescendantMeshBodies(node3D, rigidBody, collider);
                else
                    AddBody(node3D, rigidBody, collider);
            }
        }
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
            AttachNode(children[index]);
    }

    /// <summary>Adds static triangle colliders for imported meshes below a model root.</summary>
    /// <param name="root">Model root owning the compound collider.</param>
    /// <param name="rigidBody">Optional root motion settings.</param>
    /// <param name="source">Authored compound collider settings.</param>
    private void AddDescendantMeshBodies(
        Node3D root,
        RigidBodyComponent? rigidBody,
        ColliderComponent source)
    {
        if (rigidBody is { MotionType: not RigidBodyMotionType.Static })
            throw new InvalidOperationException("Compound triangle mesh colliders must be static.");
        var added = 0;
        AddDescendantMeshBodies(root, source, ref added);
        if (added == 0)
        {
            throw new InvalidOperationException(
                $"Compound mesh collider '{root.Name}' has no descendant mesh instances.");
        }
    }

    /// <summary>Recursively adds one static body for each descendant mesh instance.</summary>
    /// <param name="node">Current hierarchy node.</param>
    /// <param name="source">Authored compound collider settings.</param>
    /// <param name="added">Number of mesh bodies created.</param>
    private void AddDescendantMeshBodies(
        Node node,
        ColliderComponent source,
        ref int added)
    {
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            if (child is MeshInstance3D mesh &&
                (mesh.Mesh.SubAsset?.StartsWith("model-batch/", StringComparison.Ordinal) == true ||
                 !HasModelBatchDescendant(node)))
            {
                var collider = new ColliderComponent
                {
                    Shape = ColliderShape.Mesh,
                    Mesh = mesh.Mesh,
                    Center = source.Center,
                    Friction = source.Friction,
                    Restitution = source.Restitution,
                    IsTrigger = source.IsTrigger
                };
                AddBody(mesh, null, collider);
                added++;
            }
            AddDescendantMeshBodies(child, source, ref added);
        }
    }

    /// <summary>Returns whether a hierarchy contains an optimized model batch.</summary>
    /// <param name="node">Hierarchy root.</param>
    /// <returns>True when a batch mesh exists below the node.</returns>
    private static bool HasModelBatchDescendant(Node node)
    {
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is MeshInstance3D mesh &&
                mesh.Mesh.SubAsset?.StartsWith("model-batch/", StringComparison.Ordinal) == true)
            {
                return true;
            }
            if (HasModelBatchDescendant(children[index]))
                return true;
        }
        return false;
    }

    /// <summary>Creates one scaled Bepu primitive and registers its engine mapping.</summary>
    /// <param name="node">Scene transform represented by the body.</param>
    /// <param name="rigidBody">Optional motion settings.</param>
    /// <param name="collider">Collision geometry and material.</param>
    private void AddBody(Node3D node, RigidBodyComponent? rigidBody, ColliderComponent collider)
    {
        GetColliderPose(node, collider, out var pose, out var scale, out var origin);
        var body = new PhysicsBody(node, rigidBody, collider, pose.Position - origin);
        _bodies.Add(body);
        switch (collider.Shape)
        {
            case ColliderShape.Sphere:
                var sphereRadius = collider.Radius * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
                AddBepuBody(body, pose, new Sphere(sphereRadius));
                break;
            case ColliderShape.Capsule:
                var capsuleRadius = collider.Radius * MathF.Max(scale.X, scale.Z);
                var capsuleHeight = collider.Height * scale.Y;
                AddBepuBody(body, pose,
                    new Capsule(capsuleRadius, MathF.Max(0.001f, capsuleHeight - capsuleRadius * 2f)));
                break;
            case ColliderShape.Cylinder:
                AddBepuBody(body, pose,
                    new Cylinder(collider.Radius * MathF.Max(scale.X, scale.Z), collider.Height * scale.Y));
                break;
            case ColliderShape.Plane:
                pose.Position += Vector3.Transform(new Vector3(0f, -PlaneThickness * 0.5f, 0f),
                    pose.Orientation);
                body.CenterOffset = pose.Position - origin;
                AddBepuBody(body, pose, new Box(PlaneExtent, PlaneThickness, PlaneExtent));
                break;
            case ColliderShape.Mesh:
                AddMeshBody(body, pose, scale);
                break;
            default:
                var size = collider.Size * scale;
                AddBepuBody(body, pose, new Box(size.X, size.Y, size.Z));
                break;
        }
    }

    /// <summary>Adds one imported triangle mesh as a static BEPU collidable.</summary>
    /// <param name="body">Engine body mapping receiving the static handle.</param>
    /// <param name="pose">World pose of the mesh origin.</param>
    /// <param name="scale">Absolute world scale applied by BEPU.</param>
    private void AddMeshBody(PhysicsBody body, RigidPose pose, Vector3 scale)
    {
        if (body.RigidBody is { MotionType: not RigidBodyMotionType.Static })
            throw new InvalidOperationException("Triangle mesh colliders must be static.");
        var reference = body.Collider.Mesh
            ?? throw new InvalidOperationException("A mesh collider requires a mesh reference.");
        var resource = _meshResolver?.Invoke(reference)
            ?? throw new InvalidOperationException($"Collision mesh '{reference}' could not be resolved.");
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
        if (rigidBody.MotionType == RigidBodyMotionType.Kinematic)
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
                var externallyMoved = EnableInterpolation
                    ? !body.HasPresentationPosition ||
                      Vector3.DistanceSquared(worldPosition, body.PresentationPosition) > 0.0000001f
                    : Vector3.DistanceSquared(worldPosition, body.SimulationPosition) > 0.0000001f;
                if (body.RigidBody?.MotionType == RigidBodyMotionType.Kinematic || externallyMoved)
                {
                    GetColliderPose(body.Node, body.Collider, out var pose, out _, out var origin);
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
            if (body.StaticHandle is not { } staticHandle)
                continue;
            GetColliderPose(body.Node, body.Collider, out var staticPose, out _, out var staticOrigin);
            if (body.Collider.Shape == ColliderShape.Plane)
                staticPose.Position += Vector3.Transform(
                    new Vector3(0f, -PlaneThickness * 0.5f, 0f), staticPose.Orientation);
            body.CenterOffset = staticPose.Position - staticOrigin;
            _simulation.Statics[staticHandle].Pose = staticPose;
            _simulation.Statics.UpdateBounds(staticHandle);
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
            if (body.BodyHandle is not { } handle || body.RigidBody is not { } rigidBody)
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
            if (body.BodyHandle is not { } handle)
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
        var trigger = a.Collider.IsTrigger || b.Collider.IsTrigger;
        if (manifold.Count > 0)
        {
            manifold.GetContact(0, out _, out var normal, out var depth, out _);
            if (depth > 0f)
                Contact?.Invoke(new PhysicsContact(a.Node, b.Node, -normal, depth, trigger));
        }
        return !trigger;
    }

    /// <summary>Checks whether a broad-phase pair needs narrow-phase contact generation.</summary>
    /// <param name="a">First collidable.</param>
    /// <param name="b">Second collidable.</param>
    /// <returns>True for dynamic pairs and trigger-only static/kinematic pairs.</returns>
    private bool AllowContact(CollidableReference a, CollidableReference b)
    {
        if (a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic)
            return true;
        return GetBody(a)?.Collider.IsTrigger == true || GetBody(b)?.Collider.IsTrigger == true;
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
            int childIndexB) => true;

        /// <inheritdoc/>
        public bool ConfigureContactManifold(
            int workerIndex,
            CollidablePair pair,
            int childIndexA,
            int childIndexB,
            ref ConvexContactManifold manifold) => true;

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

        /// <summary>Gets or sets the Bepu body handle.</summary>
        internal BodyHandle? BodyHandle { get; set; }

        /// <summary>Gets or sets the Bepu static handle.</summary>
        internal StaticHandle? StaticHandle { get; set; }

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

        /// <summary>Creates retained state for one Bepu collidable.</summary>
        /// <param name="node">Transformed scene node.</param>
        /// <param name="rigidBody">Optional motion component.</param>
        /// <param name="collider">Required collision component.</param>
        /// <param name="centerOffset">World offset from node origin to collider center.</param>
        internal PhysicsBody(
            Node3D node,
            RigidBodyComponent? rigidBody,
            ColliderComponent collider,
            Vector3 centerOffset)
        {
            Node = node;
            RigidBody = rigidBody;
            Collider = collider;
            CenterOffset = centerOffset;
            var position = node.GetWorldPosition();
            PreviousPosition = position;
            SimulationPosition = position;
            PresentationPosition = position;
        }
    }
}
