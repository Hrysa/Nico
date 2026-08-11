using System.Numerics;
using Editor;
using Engine.Core;
using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

public class ScenePreviewRegistryTests
{
    /// <summary>Verifies invisible nodes, cameras, and colliders share one preview traversal.</summary>
    [Fact]
    public void Build_DefaultProviders_ProducesCommonDiagnosticGeometry()
    {
        var root = new Node3D { Name = "Root" };
        var empty = new Node3D { Name = "Empty" };
        empty.AddComponent(new BoxColliderComponent());
        var camera = new PerspectiveCamera { Name = "Camera" };
        root.AddChild(empty);
        root.AddChild(camera);
        var registry = ScenePreviewRegistry.CreateDefault();
        var previews = new ScenePreviewList();

        registry.Build(root, empty, previews);

        Assert.NotEmpty(previews.Lines);
        Assert.Contains(previews.Lines, line => ReferenceEquals(line.PickingId.Node, empty));
        Assert.Contains(previews.Lines, line => line.PickingId.Component is BoxColliderComponent);
        Assert.Contains(previews.Lines, line => ReferenceEquals(line.PickingId.Node, camera));
        Assert.NotEmpty(previews.Icons);
        Assert.Single(previews.Frustums);
    }

    /// <summary>Builds a selectable light icon and illumination-direction marker.</summary>
    [Fact]
    public void Build_DirectionalLight_ProducesLightPreview()
    {
        var root = new Node3D();
        var light = new DirectionalLight3D();
        root.AddChild(light);
        var registry = ScenePreviewRegistry.CreateDefault();
        var previews = new ScenePreviewList();

        registry.Build(root, light, previews);

        Assert.Contains(previews.Icons, icon => icon.Kind == ScenePreviewIconKind.Light &&
            ReferenceEquals(icon.PickingId.Node, light));
        Assert.Contains(previews.Lines, line => ReferenceEquals(line.PickingId.Node, light));
    }

    /// <summary>Verifies preview picking identities survive hierarchy insertion and rebuilds.</summary>
    [Fact]
    public void Build_HierarchyChanges_PreservesExistingPickingIdentity()
    {
        var root = new Node3D();
        var camera = new PerspectiveCamera();
        root.AddChild(camera);
        var registry = ScenePreviewRegistry.CreateDefault();
        var previews = new ScenePreviewList();
        registry.Build(root, null, previews);
        var firstId = Assert.Single(previews.Lines
            .Where(line => ReferenceEquals(line.PickingId.Node, camera))
            .Select(line => line.PickingId.Value)
            .Distinct());

        root.AddChild(new Node3D());
        registry.Build(root, null, previews);
        var secondId = Assert.Single(previews.Lines
            .Where(line => ReferenceEquals(line.PickingId.Node, camera))
            .Select(line => line.PickingId.Value)
            .Distinct());

        Assert.Equal(firstId, secondId);
    }

    /// <summary>Verifies category visibility suppresses matching providers only.</summary>
    [Fact]
    public void Build_DisabledColliderCategory_HidesColliderGeometry()
    {
        var root = new Node3D();
        var node = new MeshInstance3D();
        node.AddComponent(new SphereColliderComponent());
        root.AddChild(node);
        var registry = ScenePreviewRegistry.CreateDefault();
        registry.SetCategoryVisible(ScenePreviewCategory.Colliders, false);
        var previews = new ScenePreviewList();

        registry.Build(root, node, previews);

        Assert.DoesNotContain(previews.Lines,
            line => line.PickingId.Component is ColliderComponent);
    }

    /// <summary>Publishes a semantic cached wire-mesh primitive for an explicit collision asset.</summary>
    [Fact]
    public void Build_ExplicitMeshCollider_PublishesWireMeshPrimitive()
    {
        var reference = new AssetReference(AssetId.New(), "mesh/collision");
        var mesh = new StaticMeshResource(
        [
            new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector2.Zero, Vector4.UnitX),
            new ModelVertex(Vector3.UnitX, Vector3.UnitY, Vector2.Zero, Vector4.UnitX),
            new ModelVertex(Vector3.UnitZ, Vector3.UnitY, Vector2.Zero, Vector4.UnitX)
        ], [0, 1, 2], [new Submesh(0, 3, -1)]);
        var root = new Node3D();
        var node = new Node3D();
        node.AddComponent(new MeshColliderComponent { Mesh = reference });
        root.AddChild(node);
        var registry = ScenePreviewRegistry.CreateDefault(candidate =>
            candidate == reference ? mesh : null);
        var previews = new ScenePreviewList();

        registry.Build(root, node, previews);

        var primitive = Assert.Single(previews.WireMeshes);
        Assert.Same(mesh, primitive.Mesh);
        Assert.IsType<MeshColliderComponent>(primitive.PickingId.Component);
    }

    /// <summary>Verifies retained preview traversal allocates nothing after provider caches warm up.</summary>
    [Fact]
    public void Build_AfterWarmup_DoesNotAllocate()
    {
        var root = new Node3D();
        var camera = new PerspectiveCamera();
        var node = new Node3D();
        node.AddComponent(new CapsuleColliderComponent());
        root.AddChild(camera);
        root.AddChild(node);
        var registry = ScenePreviewRegistry.CreateDefault();
        var previews = new ScenePreviewList();
        registry.Build(root, node, previews);
        registry.Build(root, node, previews);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var frame = 0; frame < 100; frame++)
            registry.Build(root, node, previews);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Verifies overlay tessellation reuses its retained vertex destination after warmup.</summary>
    [Fact]
    public void OverlayBuild_AfterWarmup_DoesNotAllocate()
    {
        var previews = new ScenePreviewList();
        var node = new Node3D();
        previews.AddLine(new ScenePreviewLine(
            new Vector3(-0.5f, 0f, 0f),
            new Vector3(0.5f, 0f, 0f),
            Vector4.One,
            ScenePreviewDepthMode.AlwaysVisible,
            new ScenePreviewPickingId(1, node)));
        var viewport = new GizmoViewport(0f, 0f, 640f, 480f);
        var gizmo = Array.Empty<Vertex>();
        var destination = Array.Empty<Vertex>();
        ScenePreviewOverlayBuilder.Build(previews, Matrix4x4.Identity,
            Matrix4x4.Identity, viewport, gizmo, ref destination);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var frame = 0; frame < 100; frame++)
            ScenePreviewOverlayBuilder.Build(previews, Matrix4x4.Identity,
                Matrix4x4.Identity, viewport, gizmo, ref destination);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
