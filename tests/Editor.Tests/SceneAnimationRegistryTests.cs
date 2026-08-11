using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;
using Xunit;

namespace Editor.Tests;

public sealed class SceneAnimationRegistryTests
{
    /// <summary>Allows a character root script to find its animated mesh child.</summary>
    [Fact]
    public void Get_ParentNode_ReturnsFirstDescendantController()
    {
        var root = new Node3D { Name = "Character" };
        var mesh = new MeshInstance3D { Name = "Body" };
        root.AddChild(mesh);
        using var registry = new SceneAnimationRegistry();
        var controller = CreateController();
        registry.Register(mesh, controller);

        Assert.Same(controller, registry.Get(root));
        Assert.Same(controller, registry.GetRequired(mesh));
    }

    /// <summary>Invalidates registered controllers when the active scene is destroyed.</summary>
    [Fact]
    public void Dispose_RegisteredController_InvalidatesState()
    {
        var node = new Node3D();
        var registry = new SceneAnimationRegistry();
        var controller = CreateController();
        registry.Register(node, controller);

        registry.Dispose();

        Assert.False(controller.IsValid);
        Assert.Throws<ObjectDisposedException>(() => controller.Play("Idle"));
    }

    /// <summary>Creates one empty-skeleton controller suitable for registry identity tests.</summary>
    /// <returns>A controller containing one Idle state.</returns>
    private static AnimationController CreateController()
    {
        return new AnimationController(new SkinnedMeshResource(
            new StaticMeshResource([], [], []), [], new SkeletonResource([]),
            [new AnimationClipResource("Idle", 1f, [])]));
    }
}
