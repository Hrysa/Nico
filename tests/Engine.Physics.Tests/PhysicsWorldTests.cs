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
        plane.AddComponent(new PlaneColliderComponent());
        var box = new Node3D { Position = new Vector3(0f, 3f, 0f) };
        box.AddComponent(new BoxColliderComponent());
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
        terrain.AddComponent(new MeshColliderComponent { Mesh = meshReference });
        var box = new Node3D { Position = new Vector3(0f, 3f, 0f) };
        box.AddComponent(new BoxColliderComponent());
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

    /// <summary>Verifies a mesh collider never infers collision from descendant render geometry.</summary>
    [Fact]
    public void Attach_ModelRootMeshColliderWithoutReference_IsInactiveAndDiagnosed()
    {
        var meshReference = new AssetReference(AssetId.New(), "model-batch/0");
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
        var model = new Node3D { Position = new Vector3(0f, -1f, 0f) };
        model.AddComponent(new MeshColliderComponent());
        model.AddChild(new MeshInstance3D { Mesh = meshReference });
        var box = new Node3D { Position = new Vector3(0f, 3f, 0f) };
        box.AddComponent(new BoxColliderComponent());
        box.AddComponent(new RigidBodyComponent { LinearDamping = 0f });
        root.AddChild(model);
        root.AddChild(box);
        using var world = new PhysicsWorld(reference =>
            reference == meshReference ? mesh : null);

        world.Attach(root);

        Assert.Equal(1, world.BodyCount);
        Assert.Single(world.ValidationIssues);
        Assert.Contains("explicit collision mesh", world.ValidationIssues[0],
            StringComparison.Ordinal);
    }

    /// <summary>Verifies triggers report overlap without moving either body.</summary>
    [Fact]
    public void Update_TriggerOverlap_ReportsWithoutResponse()
    {
        var root = new Node3D();
        var trigger = new Node3D();
        trigger.AddComponent(new BoxColliderComponent { IsTrigger = true });
        var dynamic = new Node3D();
        dynamic.AddComponent(new BoxColliderComponent());
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
        trigger.AddComponent(new BoxColliderComponent { IsTrigger = true });
        var kinematic = new Node3D();
        kinematic.AddComponent(new BoxColliderComponent());
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

    /// <summary>Verifies multiple movable colliders form one native body and retain authored count.</summary>
    [Fact]
    public void Update_DynamicNodeWithTwoColliders_SimulatesAsCompound()
    {
        var root = new Node3D();
        var ground = new Node3D();
        ground.AddComponent(new PlaneColliderComponent());
        var compound = new Node3D { Position = new Vector3(0f, 3f, 0f) };
        compound.AddComponent(new BoxColliderComponent
            { Center = new Vector3(-1f, 0f, 0f) });
        compound.AddComponent(new BoxColliderComponent
            { Center = new Vector3(1f, 0f, 0f) });
        var body = new RigidBodyComponent { LinearDamping = 0f };
        compound.AddComponent(body);
        root.AddChild(ground);
        root.AddChild(compound);
        using var world = new PhysicsWorld();

        world.Attach(root);
        for (var step = 0; step < 180; step++)
            world.Update(1d / 60d);

        Assert.Equal(3, world.BodyCount);
        Assert.InRange(compound.Position.Y, 0.499f, 0.501f);
        Assert.InRange(MathF.Abs(body.LinearVelocity.Y), 0f, 0.001f);
    }

    /// <summary>Verifies unsupported nonconvex children fail before creating a partial compound.</summary>
    [Fact]
    public void Attach_DynamicCompoundContainingMesh_ThrowsClearValidationError()
    {
        var root = new Node3D();
        var node = new Node3D();
        node.AddComponent(new BoxColliderComponent());
        node.AddComponent(new MeshColliderComponent
            { Mesh = new AssetReference(AssetId.New(), "mesh/collision") });
        node.AddComponent(new RigidBodyComponent());
        root.AddChild(node);
        using var world = new PhysicsWorld();

        var exception = Assert.Throws<InvalidOperationException>(() => world.Attach(root));

        Assert.Contains("must be static", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, world.BodyCount);
    }

    /// <summary>Verifies explicit terrain data participates in collision and height sampling.</summary>
    [Fact]
    public void TerrainCollider_ExplicitHeightGrid_SupportsBodyAndHeightQuery()
    {
        var terrainReference = new AssetReference(AssetId.New(), "terrain/height/0");
        var terrainResource = new TerrainResource(2, 2, [0f, 0f, 0f, 0f]);
        var root = new Node3D();
        var terrain = new Node3D();
        terrain.AddComponent(new TerrainColliderComponent
        {
            TerrainData = terrainReference,
            HorizontalSize = new Vector2(10f),
            HeightScale = 4f
        });
        var box = new Node3D { Position = new Vector3(0f, 3f, 0f) };
        box.AddComponent(new BoxColliderComponent());
        box.AddComponent(new RigidBodyComponent { LinearDamping = 0f });
        root.AddChild(terrain);
        root.AddChild(box);
        using var world = new PhysicsWorld(terrainResolver: reference =>
            reference == terrainReference ? terrainResource : null);

        world.Attach(root);
        for (var step = 0; step < 180; step++)
            world.Update(1d / 60d);

        Assert.True(world.TryGetTerrainHeight(Vector3.Zero, out var height));
        Assert.Equal(0f, height, 5);
        Assert.InRange(box.Position.Y, 0.499f, 0.501f);
        Assert.False(world.TryGetTerrainHeight(new Vector3(6f, 0f, 0f), out _));
    }

    /// <summary>Verifies raycasts return the closest authored collider allowed by the query mask.</summary>
    [Fact]
    public void TryRaycast_LayerMask_ReturnsEligibleAuthoredCollider()
    {
        var root = new Node3D();
        var ignored = new Node3D { Position = new Vector3(0f, 0f, 2f) };
        ignored.AddComponent(new BoxColliderComponent { CollisionLayer = 1u });
        var accepted = new Node3D { Position = Vector3.Zero };
        var acceptedCollider = new SphereColliderComponent { CollisionLayer = 4u };
        accepted.AddComponent(acceptedCollider);
        root.AddChild(ignored);
        root.AddChild(accepted);
        using var world = new PhysicsWorld();
        world.Attach(root);

        var found = world.TryRaycast(new Vector3(0f, 0f, 5f), -Vector3.UnitZ,
            10f, 4u, out var hit);

        Assert.True(found);
        Assert.Same(accepted, hit.Node);
        Assert.Same(acceptedCollider, hit.Collider);
        Assert.InRange(hit.Distance, 4.49f, 4.51f);
    }

    /// <summary>Verifies a trigger child in a mixed compound reports without using the primary child.</summary>
    [Fact]
    public void Update_MixedCompoundTrigger_UsesExactChildBehavior()
    {
        var root = new Node3D();
        var target = new Node3D();
        target.AddComponent(new BoxColliderComponent());
        var compound = new Node3D();
        compound.AddComponent(new BoxColliderComponent
            { Center = new Vector3(10f, 0f, 0f) });
        compound.AddComponent(new SphereColliderComponent
            { Center = Vector3.Zero, IsTrigger = true });
        compound.AddComponent(new RigidBodyComponent { UseGravity = false });
        root.AddChild(target);
        root.AddChild(compound);
        using var world = new PhysicsWorld { Gravity = Vector3.Zero };
        var triggerContacts = 0;
        world.Contact += contact => triggerContacts += contact.IsTrigger ? 1 : 0;
        world.Attach(root);

        world.Update(1d / 60d);

        Assert.True(triggerContacts > 0);
        Assert.Equal(Vector3.Zero, compound.Position);
    }

    /// <summary>Replaces only a dirty native terrain chunk and exposes its updated surface.</summary>
    [Fact]
    public void RebuildTerrain_DirtyEdgeChunk_UpdatesRaycastAndHeightQuery()
    {
        var reference = new AssetReference(AssetId.New(), "terrain/editable");
        var initial = new TerrainResource(66, 2, new float[132]);
        var node = new Node3D();
        node.AddComponent(new TerrainColliderComponent
        {
            TerrainData = reference,
            HorizontalSize = new Vector2(10f, 2f),
            HeightScale = 4f
        });
        var root = new Node3D();
        root.AddChild(node);
        using var world = new PhysicsWorld(terrainResolver: _ => initial);
        world.Attach(root);
        var heights = new float[132];
        heights[65] = 1f;
        heights[131] = 1f;
        var updated = new TerrainResource(66, 2, heights);
        var dirty = updated.GetDirtyChunkRegions(65, 0, 65, 1);

        world.RebuildTerrain(node, updated, dirty);

        Assert.Single(dirty);
        Assert.True(world.TryGetTerrainHeight(new Vector3(4.99f, 0f, 0f), out var height));
        Assert.InRange(height, 3.7f, 4f);
        Assert.True(world.TryRaycast(new Vector3(4.99f, 10f, 0f), -Vector3.UnitY,
            20f, uint.MaxValue, out var hit));
        Assert.Equal(height, hit.Position.Y, 3);
        Assert.Equal(1, world.BodyCount);
    }

    /// <summary>Rejects incompatible terrain edits before changing the active query resource.</summary>
    [Fact]
    public void RebuildTerrain_IncompatibleDimensions_LeavesAttachedTerrainUnchanged()
    {
        var reference = new AssetReference(AssetId.New(), "terrain/editable");
        var initial = new TerrainResource(2, 2, new float[4]);
        var node = new Node3D();
        node.AddComponent(new TerrainColliderComponent { TerrainData = reference });
        var root = new Node3D();
        root.AddChild(node);
        using var world = new PhysicsWorld(terrainResolver: _ => initial);
        world.Attach(root);
        var incompatible = new TerrainResource(3, 2,
            [1f, 1f, 1f, 1f, 1f, 1f]);

        Assert.Throws<ArgumentException>(() => world.RebuildTerrain(node, incompatible,
            incompatible.GetChunkRegions()));

        Assert.True(world.TryGetTerrainHeight(Vector3.Zero, out var height));
        Assert.Equal(0f, height);
    }

    /// <summary>Creates an attached world containing one unconstrained dynamic body.</summary>
    /// <returns>The world, body component, and owning node.</returns>
    private static (PhysicsWorld World, RigidBodyComponent Body, Node3D Node)
        CreateFallingBody()
    {
        var root = new Node3D();
        var node = new Node3D();
        node.AddComponent(new BoxColliderComponent());
        var body = new RigidBodyComponent { LinearDamping = 0f };
        node.AddComponent(body);
        root.AddChild(node);
        var world = new PhysicsWorld();
        world.Attach(root);
        return (world, body, node);
    }
}
