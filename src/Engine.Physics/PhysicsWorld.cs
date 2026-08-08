using System.Numerics;
using Engine.Core;
using Engine.Graphics;

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

/// <summary>Runs deterministic fixed-step linear 3D rigid-body simulation.</summary>
public sealed class PhysicsWorld
{
    private readonly List<PhysicsBody> _bodies = new();
    private double _accumulator;
    private double _fixedTimeStep = 1d / 60d;
    private int _maxSubsteps = 8;

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

    /// <summary>Discovers enabled collider components in one scene hierarchy.</summary>
    /// <param name="root">Synthetic scene root.</param>
    public void Attach(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _bodies.Clear();
        _accumulator = 0d;
        AttachNode(root);
    }

    /// <summary>Advances the accumulator and performs bounded fixed simulation steps.</summary>
    /// <param name="deltaTime">Scaled elapsed gameplay time in seconds.</param>
    public void Update(double deltaTime)
    {
        if (!double.IsFinite(deltaTime) || deltaTime < 0d)
            throw new ArgumentOutOfRangeException(nameof(deltaTime));
        if (deltaTime == 0d || _bodies.Count == 0)
            return;
        SynchronizeExternalTransforms();
        _accumulator = Math.Min(
            _accumulator + deltaTime, FixedTimeStep * MaxSubsteps);
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
                _bodies.Add(new PhysicsBody(node3D, rigidBody, collider));
        }
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
            AttachNode(children[index]);
    }

    /// <summary>Integrates bodies and resolves primitive overlaps once.</summary>
    /// <param name="deltaTime">Fixed step duration.</param>
    private void Step(float deltaTime)
    {
        for (var index = 0; index < _bodies.Count; index++)
            _bodies[index].PreviousPosition = _bodies[index].SimulationPosition;
        for (var index = 0; index < _bodies.Count; index++)
            Integrate(_bodies[index], deltaTime);
        for (var first = 0; first < _bodies.Count; first++)
        {
            for (var second = first + 1; second < _bodies.Count; second++)
                ResolvePair(_bodies[first], _bodies[second]);
        }
        for (var index = 0; index < _bodies.Count; index++)
            _bodies[index].SimulationPosition = _bodies[index].Node.GetWorldPosition();
    }

    /// <summary>Restores authoritative poses or recognizes transforms changed by game code.</summary>
    private void SynchronizeExternalTransforms()
    {
        for (var index = 0; index < _bodies.Count; index++)
        {
            var body = _bodies[index];
            var worldPosition = body.Node.GetWorldPosition();
            if (!EnableInterpolation)
            {
                body.PreviousPosition = worldPosition;
                body.SimulationPosition = worldPosition;
                continue;
            }
            if (body.HasPresentationPosition &&
                Vector3.DistanceSquared(worldPosition, body.PresentationPosition) <= 0.0000001f)
            {
                SetWorldPosition(body.Node, body.SimulationPosition);
                continue;
            }
            body.PreviousPosition = worldPosition;
            body.SimulationPosition = worldPosition;
            body.PresentationPosition = worldPosition;
            body.HasPresentationPosition = true;
        }
    }

    /// <summary>Publishes smooth render poses without changing authoritative simulation state.</summary>
    /// <param name="alpha">Remaining fixed-step fraction from zero through one.</param>
    private void PublishInterpolatedTransforms(float alpha)
    {
        alpha = Math.Clamp(alpha, 0f, 1f);
        for (var index = 0; index < _bodies.Count; index++)
        {
            var body = _bodies[index];
            var position = Vector3.Lerp(
                body.PreviousPosition, body.SimulationPosition, alpha);
            SetWorldPosition(body.Node, position);
            body.PresentationPosition = position;
            body.HasPresentationPosition = true;
        }
    }

    /// <summary>Integrates one enabled dynamic rigid body.</summary>
    /// <param name="body">Body to integrate.</param>
    /// <param name="deltaTime">Fixed step duration.</param>
    private void Integrate(PhysicsBody body, float deltaTime)
    {
        var rigidBody = body.RigidBody;
        if (rigidBody is not { Enabled: true, MotionType: RigidBodyMotionType.Dynamic })
            return;
        var velocity = rigidBody.LinearVelocity;
        if (rigidBody.UseGravity)
            velocity += Gravity * rigidBody.GravityScale * deltaTime;
        velocity *= MathF.Max(0f, 1f - rigidBody.LinearDamping * deltaTime);
        rigidBody.LinearVelocity = velocity;
        SetWorldPosition(body.Node, body.Node.GetWorldPosition() + velocity * deltaTime);
    }

    /// <summary>Detects and resolves one collider pair.</summary>
    /// <param name="a">First body.</param>
    /// <param name="b">Second body.</param>
    private void ResolvePair(PhysicsBody a, PhysicsBody b)
    {
        var inverseMassA = GetInverseMass(a.RigidBody);
        var inverseMassB = GetInverseMass(b.RigidBody);
        if (!a.Collider.Enabled || !b.Collider.Enabled)
            return;
        Vector3 normal;
        float penetration;
        if (a.Collider.Shape == ColliderShape.Plane)
        {
            if (!TryPlaneContact(a, b, out normal, out penetration))
                return;
        }
        else if (b.Collider.Shape == ColliderShape.Plane)
        {
            if (!TryPlaneContact(b, a, out normal, out penetration))
                return;
            normal = -normal;
        }
        else if (!TryBoundsContact(GetBounds(a), GetBounds(b), out normal, out penetration))
        {
            return;
        }
        var trigger = a.Collider.IsTrigger || b.Collider.IsTrigger;
        Contact?.Invoke(new PhysicsContact(a.Node, b.Node, normal, penetration, trigger));
        if (trigger)
            return;
        ApplyResponse(a, b, normal, penetration, inverseMassA, inverseMassB);
    }

    /// <summary>Tests an infinite plane against one finite primitive.</summary>
    /// <param name="plane">Plane body.</param>
    /// <param name="other">Finite body.</param>
    /// <param name="normal">Plane-to-body contact normal.</param>
    /// <param name="penetration">Overlap depth.</param>
    /// <returns>True when the finite bounds cross the plane.</returns>
    private static bool TryPlaneContact(
        PhysicsBody plane,
        PhysicsBody other,
        out Vector3 normal,
        out float penetration)
    {
        var matrix = plane.Node.GetModelMatrix();
        normal = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, matrix));
        var point = Vector3.Transform(plane.Collider.Center, matrix);
        var bounds = GetBounds(other);
        var radius = Vector3.Dot(Vector3.Abs(normal), bounds.Extents);
        var distance = Vector3.Dot(bounds.Center - point, normal);
        penetration = radius - distance;
        return penetration > 0f;
    }

    /// <summary>Tests two world axis-aligned bounds and selects minimum penetration.</summary>
    /// <param name="a">First bounds.</param>
    /// <param name="b">Second bounds.</param>
    /// <param name="normal">A-to-B contact normal.</param>
    /// <param name="penetration">Minimum overlap depth.</param>
    /// <returns>True when all axes overlap.</returns>
    private static bool TryBoundsContact(
        PhysicsBounds a,
        PhysicsBounds b,
        out Vector3 normal,
        out float penetration)
    {
        var delta = b.Center - a.Center;
        var overlap = a.Extents + b.Extents - Vector3.Abs(delta);
        if (overlap.X <= 0f || overlap.Y <= 0f || overlap.Z <= 0f)
        {
            normal = default;
            penetration = 0f;
            return false;
        }
        if (overlap.X <= overlap.Y && overlap.X <= overlap.Z)
        {
            normal = new Vector3(delta.X < 0f ? -1f : 1f, 0f, 0f);
            penetration = overlap.X;
        }
        else if (overlap.Y <= overlap.Z)
        {
            normal = new Vector3(0f, delta.Y < 0f ? -1f : 1f, 0f);
            penetration = overlap.Y;
        }
        else
        {
            normal = new Vector3(0f, 0f, delta.Z < 0f ? -1f : 1f);
            penetration = overlap.Z;
        }
        return true;
    }

    /// <summary>Applies positional correction, normal impulse, and Coulomb friction.</summary>
    /// <param name="a">First body.</param>
    /// <param name="b">Second body.</param>
    /// <param name="normal">A-to-B contact normal.</param>
    /// <param name="penetration">Overlap depth.</param>
    /// <param name="inverseMassA">First inverse mass.</param>
    /// <param name="inverseMassB">Second inverse mass.</param>
    private static void ApplyResponse(
        PhysicsBody a,
        PhysicsBody b,
        Vector3 normal,
        float penetration,
        float inverseMassA,
        float inverseMassB)
    {
        var inverseMassSum = inverseMassA + inverseMassB;
        if (inverseMassSum <= 0f)
            return;
        const float Slop = 0.0001f;
        var correction = normal * (MathF.Max(0f, penetration - Slop) / inverseMassSum);
        if (inverseMassA > 0f)
            SetWorldPosition(a.Node, a.Node.GetWorldPosition() - correction * inverseMassA);
        if (inverseMassB > 0f)
            SetWorldPosition(b.Node, b.Node.GetWorldPosition() + correction * inverseMassB);

        var velocityA = a.RigidBody?.LinearVelocity ?? Vector3.Zero;
        var velocityB = b.RigidBody?.LinearVelocity ?? Vector3.Zero;
        var relativeVelocity = velocityB - velocityA;
        var normalVelocity = Vector3.Dot(relativeVelocity, normal);
        if (normalVelocity > 0f)
            return;
        var restitution = MathF.Max(a.Collider.Restitution, b.Collider.Restitution);
        var impulseMagnitude = -(1f + restitution) * normalVelocity / inverseMassSum;
        var impulse = normal * impulseMagnitude;
        ApplyImpulse(a.RigidBody, -impulse * inverseMassA);
        ApplyImpulse(b.RigidBody, impulse * inverseMassB);

        relativeVelocity = (b.RigidBody?.LinearVelocity ?? Vector3.Zero) -
            (a.RigidBody?.LinearVelocity ?? Vector3.Zero);
        var tangent = relativeVelocity - normal * Vector3.Dot(relativeVelocity, normal);
        if (tangent.LengthSquared() <= float.Epsilon)
            return;
        tangent = Vector3.Normalize(tangent);
        var frictionImpulse = -Vector3.Dot(relativeVelocity, tangent) / inverseMassSum;
        var friction = MathF.Sqrt(a.Collider.Friction * b.Collider.Friction);
        frictionImpulse = Math.Clamp(frictionImpulse,
            -impulseMagnitude * friction, impulseMagnitude * friction);
        ApplyImpulse(a.RigidBody, -tangent * frictionImpulse * inverseMassA);
        ApplyImpulse(b.RigidBody, tangent * frictionImpulse * inverseMassB);
    }

    /// <summary>Adds a velocity delta to one dynamic body.</summary>
    /// <param name="rigidBody">Target rigid body.</param>
    /// <param name="velocityDelta">World velocity delta.</param>
    private static void ApplyImpulse(RigidBodyComponent? rigidBody, Vector3 velocityDelta)
    {
        if (rigidBody is { Enabled: true, MotionType: RigidBodyMotionType.Dynamic })
            rigidBody.LinearVelocity += velocityDelta;
    }

    /// <summary>Computes inverse mass for collision response.</summary>
    /// <param name="rigidBody">Optional rigid body.</param>
    /// <returns>Positive inverse mass only for enabled dynamic bodies.</returns>
    private static float GetInverseMass(RigidBodyComponent? rigidBody) =>
        rigidBody is { Enabled: true, MotionType: RigidBodyMotionType.Dynamic }
            ? 1f / rigidBody.Mass : 0f;

    /// <summary>Computes conservative world bounds for one finite primitive.</summary>
    /// <param name="body">Physics body.</param>
    /// <returns>World center and half extents.</returns>
    private static PhysicsBounds GetBounds(PhysicsBody body)
    {
        var matrix = body.Node.GetModelMatrix();
        var center = Vector3.Transform(body.Collider.Center, matrix);
        if (!Matrix4x4.Decompose(matrix, out var scale, out var orientation, out _))
            return new PhysicsBounds(center, Vector3.Zero);
        scale = Vector3.Abs(scale);
        Vector3 localExtents;
        switch (body.Collider.Shape)
        {
            case ColliderShape.Sphere:
                var radius = body.Collider.Radius * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
                return new PhysicsBounds(center, new Vector3(radius));
            case ColliderShape.Capsule:
                localExtents = new Vector3(
                    body.Collider.Radius * scale.X,
                    body.Collider.Height * 0.5f * scale.Y,
                    body.Collider.Radius * scale.Z);
                break;
            case ColliderShape.Cylinder:
                localExtents = new Vector3(
                    body.Collider.Radius * scale.X,
                    body.Collider.Height * 0.5f * scale.Y,
                    body.Collider.Radius * scale.Z);
                break;
            default:
                localExtents = body.Collider.Size * scale * 0.5f;
                break;
        }
        var rotation = Matrix4x4.CreateFromQuaternion(orientation);
        var extents = new Vector3(
            MathF.Abs(rotation.M11) * localExtents.X +
            MathF.Abs(rotation.M21) * localExtents.Y +
            MathF.Abs(rotation.M31) * localExtents.Z,
            MathF.Abs(rotation.M12) * localExtents.X +
            MathF.Abs(rotation.M22) * localExtents.Y +
            MathF.Abs(rotation.M32) * localExtents.Z,
            MathF.Abs(rotation.M13) * localExtents.X +
            MathF.Abs(rotation.M23) * localExtents.Y +
            MathF.Abs(rotation.M33) * localExtents.Z);
        return new PhysicsBounds(center, extents);
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

    /// <summary>Stores references participating in one simulation body.</summary>
    private sealed class PhysicsBody
    {
        /// <summary>Gets the transformed node.</summary>
        internal Node3D Node { get; }

        /// <summary>Gets the optional motion component.</summary>
        internal RigidBodyComponent? RigidBody { get; }

        /// <summary>Gets the required collision component.</summary>
        internal ColliderComponent Collider { get; }

        /// <summary>Gets or sets the preceding completed-step position.</summary>
        internal Vector3 PreviousPosition { get; set; }

        /// <summary>Gets or sets the latest authoritative simulation position.</summary>
        internal Vector3 SimulationPosition { get; set; }

        /// <summary>Gets or sets the pose most recently exposed for rendering.</summary>
        internal Vector3 PresentationPosition { get; set; }

        /// <summary>Gets or sets whether a presentation pose has been published.</summary>
        internal bool HasPresentationPosition { get; set; }

        /// <summary>Creates retained simulation state for one attached collider.</summary>
        /// <param name="node">Transformed scene node.</param>
        /// <param name="rigidBody">Optional motion component.</param>
        /// <param name="collider">Required collision component.</param>
        internal PhysicsBody(
            Node3D node,
            RigidBodyComponent? rigidBody,
            ColliderComponent collider)
        {
            Node = node;
            RigidBody = rigidBody;
            Collider = collider;
            var position = node.GetWorldPosition();
            PreviousPosition = position;
            SimulationPosition = position;
            PresentationPosition = position;
        }
    }

    /// <summary>Stores one conservative world axis-aligned bounding box.</summary>
    /// <param name="Center">World center.</param>
    /// <param name="Extents">Positive world half extents.</param>
    private readonly record struct PhysicsBounds(Vector3 Center, Vector3 Extents);
}
