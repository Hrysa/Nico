using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.Physics;
using Xunit;

namespace Engine.Physics.Tests;

public sealed class PhysicsWorldTests
{
    /// <summary>Verifies gravity settles a dynamic box on a static plane.</summary>
    [Fact]
    public void Update_DynamicBoxAbovePlane_SettlesOnSurface()
    {
        var root = new Node3D();
        var plane = new Node3D();
        plane.AddComponent(new ColliderComponent { Shape = ColliderShape.Plane });
        var box = new Node3D { Position = new Vector3(0f, 3f, 0f) };
        box.AddComponent(new ColliderComponent { Shape = ColliderShape.Box });
        var rigidBody = new RigidBodyComponent { LinearDamping = 0f };
        box.AddComponent(rigidBody);
        root.AddChild(plane);
        root.AddChild(box);
        var world = new PhysicsWorld();
        world.Attach(root);

        for (var step = 0; step < 180; step++)
            world.Update(1d / 60d);

        Assert.InRange(box.Position.Y, 0.499f, 0.501f);
        Assert.InRange(MathF.Abs(rigidBody.LinearVelocity.Y), 0f, 0.001f);
    }

    /// <summary>Verifies an imported static triangle mesh supports dynamic bodies.</summary>
    [Fact]
    public void Update_DynamicBoxAboveMesh_SettlesOnSurface()
    {
        var meshReference = new AssetReference(AssetId.New(), "mesh/collision/0");
        var mesh = new StaticMeshResource(
        [
            new ModelVertex(new Vector3(-5f, 0f, -5f), Vector3.UnitY, Vector2.Zero,
                Vector4.UnitX),
            new ModelVertex(new Vector3(5f, 0f, -5f), Vector3.UnitY, Vector2.Zero,
                Vector4.UnitX),
            new ModelVertex(new Vector3(5f, 0f, 5f), Vector3.UnitY, Vector2.Zero,
                Vector4.UnitX),
            new ModelVertex(new Vector3(-5f, 0f, 5f), Vector3.UnitY, Vector2.Zero,
                Vector4.UnitX)
        ], [0, 2, 1, 0, 3, 2], [new Submesh(0, 6, 0)]);
        var root = new Node3D();
        var terrain = new Node3D();
        terrain.AddComponent(new ColliderComponent
        {
            Shape = ColliderShape.Mesh,
            Mesh = meshReference
        });
        var box = new Node3D { Position = new Vector3(0f, 3f, 0f) };
        box.AddComponent(new ColliderComponent { Shape = ColliderShape.Box });
        var rigidBody = new RigidBodyComponent { LinearDamping = 0f };
        box.AddComponent(rigidBody);
        root.AddChild(terrain);
        root.AddChild(box);
        using var world = new PhysicsWorld(reference =>
            reference == meshReference ? mesh : null);
        world.Attach(root);

        for (var step = 0; step < 180; step++)
            world.Update(1d / 60d);

        Assert.InRange(box.Position.Y, 0.499f, 0.501f);
        Assert.InRange(MathF.Abs(rigidBody.LinearVelocity.Y), 0f, 0.001f);
    }

    /// <summary>Verifies triggers report overlap without moving either body.</summary>
    [Fact]
    public void Update_TriggerOverlap_ReportsWithoutResponse()
    {
        var root = new Node3D();
        var trigger = new Node3D();
        trigger.AddComponent(new ColliderComponent { IsTrigger = true });
        var dynamic = new Node3D();
        dynamic.AddComponent(new ColliderComponent());
        dynamic.AddComponent(new RigidBodyComponent { UseGravity = false });
        root.AddChild(trigger);
        root.AddChild(dynamic);
        var world = new PhysicsWorld { Gravity = Vector3.Zero };
        var contactCount = 0;
        world.Contact += contact =>
        {
            Assert.True(contact.IsTrigger);
            contactCount++;
        };
        world.Attach(root);

        world.Update(1d / 60d);

        Assert.Equal(1, contactCount);
        Assert.Equal(Vector3.Zero, dynamic.Position);
    }

    /// <summary>Verifies trigger contacts do not require a dynamic rigid body.</summary>
    [Fact]
    public void Update_StaticTriggerAndKinematicBody_ReportsContact()
    {
        var root = new Node3D();
        var trigger = new Node3D();
        trigger.AddComponent(new ColliderComponent { IsTrigger = true });
        var kinematic = new Node3D();
        kinematic.AddComponent(new ColliderComponent());
        kinematic.AddComponent(new RigidBodyComponent
            { MotionType = RigidBodyMotionType.Kinematic });
        root.AddChild(trigger);
        root.AddChild(kinematic);
        var world = new PhysicsWorld();
        var contactCount = 0;
        world.Contact += _ => contactCount++;
        world.Attach(root);

        world.Update(1d / 60d);

        Assert.Equal(1, contactCount);
    }

    /// <summary>Verifies fixed stepping produces the same result for different frame chunks.</summary>
    [Fact]
    public void Update_DifferentFrameChunks_ProducesSameFixedStepResult()
    {
        var (firstWorld, firstBody, firstNode) = CreateFallingBody();
        var (secondWorld, secondBody, secondNode) = CreateFallingBody();

        for (var frame = 0; frame < 60; frame++)
            firstWorld.Update(1d / 60d);
        for (var frame = 0; frame < 30; frame++)
            secondWorld.Update(1d / 30d);

        Assert.Equal(firstNode.Position, secondNode.Position);
        Assert.Equal(firstBody.LinearVelocity, secondBody.LinearVelocity);
    }

    /// <summary>Verifies the simulation update path remains allocation free after warmup.</summary>
    [Fact]
    public void Update_AfterWarmup_DoesNotAllocate()
    {
        var (world, _, _) = CreateFallingBody();
        world.EnableInterpolation = true;
        world.Update(1d / 60d);
        world.Update(1d / 60d);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var frame = 0; frame < 1_000; frame++)
            world.Update(1d / 60d);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Verifies rendered clients receive an in-between fixed-step pose.</summary>
    [Fact]
    public void Update_InterpolationEnabled_PublishesFractionalPose()
    {
        var (world, body, node) = CreateFallingBody();
        world.Gravity = Vector3.Zero;
        world.FixedTimeStep = 1d;
        world.EnableInterpolation = true;
        body.LinearVelocity = Vector3.UnitX;

        world.Update(1.5d);

        Assert.Equal(0.5f, node.Position.X, 5);
        Assert.Equal(Vector3.UnitX, body.LinearVelocity);
    }

    /// <summary>Creates an attached world containing one unconstrained dynamic body.</summary>
    /// <returns>The world, body component, and owning node.</returns>
    private static (PhysicsWorld World, RigidBodyComponent Body, Node3D Node)
        CreateFallingBody()
    {
        var root = new Node3D();
        var node = new Node3D();
        node.AddComponent(new ColliderComponent());
        var body = new RigidBodyComponent { LinearDamping = 0f };
        node.AddComponent(body);
        root.AddChild(node);
        var world = new PhysicsWorld();
        world.Attach(root);
        return (world, body, node);
    }
}
