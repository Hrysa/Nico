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

    /// <summary>Binds a script-selected set through the active scene resolver.</summary>
    [Fact]
    public void Bind_AnimationSet_RegistersResolvedAliasesOnController()
    {
        var node = new MeshInstance3D { Name = "Body" };
        var reference = new AssetReference(AssetId.New(), "main");
        var animationSet = new AnimationSet(reference);
        var callbackCount = 0;
        using var registry = new SceneAnimationRegistry((boundNode, boundSet, controller) =>
        {
            Assert.Same(node, boundNode);
            Assert.Equal(animationSet, boundSet);
            controller.RegisterClips([new AnimationClipResource("Run", 1f, [])]);
            callbackCount++;
        });
        var controller = CreateController();
        registry.Register(node, controller);

        var bound = registry.Bind(node, animationSet);

        Assert.Same(controller, bound);
        Assert.True(controller.TryGet("Run", out _));
        Assert.Equal(1, callbackCount);
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
