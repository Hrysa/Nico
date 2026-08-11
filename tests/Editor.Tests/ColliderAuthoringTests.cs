using System.Numerics;
using Editor;
using Engine.Core;
using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

public sealed class ColliderAuthoringTests
{
    /// <summary>Fits descendant mesh bounds once in the selected node's local coordinates.</summary>
    [Fact]
    public void Create_BoxFromDescendantBounds_StoresCombinedLocalDimensions()
    {
        var root = new Node3D { Position = new Vector3(10f, 0f, 0f) };
        var mesh = new MeshInstance3D
        {
            Position = new Vector3(2f, 3f, 4f),
            Scale = new Vector3(2f, 1f, 3f),
            LocalBounds = new MeshBounds(new Vector3(-1f), new Vector3(1f))
        };
        root.AddChild(mesh);

        var collider = Assert.IsType<BoxColliderComponent>(
            ColliderAuthoring.Create(ColliderAuthoringKind.Box, root));

        Assert.Equal(new Vector3(2f, 3f, 4f), collider.Center);
        Assert.Equal(new Vector3(4f, 2f, 6f), collider.Size);
        mesh.Scale = Vector3.One;
        Assert.Equal(new Vector3(4f, 2f, 6f), collider.Size);
    }

    /// <summary>Copies only the selected mesh's explicit reference into a mesh collider.</summary>
    [Fact]
    public void Create_MeshCollider_UsesExplicitOwnerMeshOnly()
    {
        var reference = new AssetReference(AssetId.New(), "mesh/collision/0");
        var mesh = new MeshInstance3D { Mesh = reference };
        var parent = new Node3D();
        parent.AddChild(mesh);

        var direct = Assert.IsType<MeshColliderComponent>(
            ColliderAuthoring.Create(ColliderAuthoringKind.Mesh, mesh));
        var parentCollider = Assert.IsType<MeshColliderComponent>(
            ColliderAuthoring.Create(ColliderAuthoringKind.Mesh, parent));

        Assert.Equal(reference, direct.Mesh);
        Assert.Null(parentCollider.Mesh);
    }

    /// <summary>Uses finite unit bounds when no render mesh has decoded local bounds.</summary>
    [Fact]
    public void Create_NoDecodedBounds_UsesUnitFallback()
    {
        var collider = Assert.IsType<SphereColliderComponent>(
            ColliderAuthoring.Create(ColliderAuthoringKind.Sphere, new Node3D()));

        Assert.Equal(Vector3.Zero, collider.Center);
        Assert.Equal(0.5f, collider.Radius);
    }
}
