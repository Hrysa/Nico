using System.Numerics;
using Editor;
using Engine.Core;
using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

public class ScenePlayCloneTests
{
    /// <summary>Verifies runtime mutations do not change authored scene objects.</summary>
    [Fact]
    public void Create_RuntimeMutation_PreservesAuthoredScene()
    {
        var root = new Node3D { Name = "Scene" };
        var cube = new MeshInstance3D
        {
            Name = "Cube",
            Position = new Vector3(2f, 0f, 0f),
            ScriptId = AssetId.New()
        };
        var material = new AssetReference(AssetId.New(), "material/0");
        cube.Materials.Add(material);
        var authoredScript = Assert.IsType<ScriptComponent>(Assert.Single(cube.Components));
        authoredScript.SetPropertyOverride(42, SerializedPropertyValue.From("authored"));
        cube.AddComponent(new ScriptComponent(AssetId.New()) { Enabled = false });
        var authoredCollider = new ColliderComponent
        {
            Shape = ColliderShape.Sphere,
            Radius = 0.75f
        };
        var authoredBody = new RigidBodyComponent
        {
            Mass = 2f,
            LinearVelocity = Vector3.UnitX
        };
        cube.AddComponent(authoredCollider);
        cube.AddComponent(authoredBody);
        var animationReference = new AssetReference(AssetId.New(), "animation/0");
        var authoredAnimator = new AnimatorComponent
        {
            AnimationSource = animationReference,
            Clip = "Walk",
            Loop = false,
            Speed = 0.75f
        };
        cube.AddComponent(authoredAnimator);
        var camera = new PerspectiveCamera { Name = "Camera" };
        root.AddChild(cube);
        root.AddChild(camera);

        var playScene = ScenePlayClone.Create(root, camera);
        playScene.MeshInstances[0].Position = new Vector3(20f, 0f, 0f);

        Assert.NotSame(cube, playScene.MeshInstances[0]);
        Assert.Equal(cube.Mesh, playScene.MeshInstances[0].Mesh);
        Assert.Equal(new Vector3(2f, 0f, 0f), cube.Position);
        Assert.Equal(cube.ScriptId, playScene.MeshInstances[0].ScriptId);
        Assert.Equal(5, playScene.MeshInstances[0].Components.Count);
        var clonedScript = Assert.IsType<ScriptComponent>(
            playScene.MeshInstances[0].Components[0]);
        Assert.NotSame(authoredScript, clonedScript);
        Assert.True(clonedScript.TryGetPropertyOverride(42, out var clonedValue));
        Assert.True(clonedValue.TryGetString(out var text));
        Assert.Equal("authored", text);
        Assert.False(playScene.MeshInstances[0].Components[1].Enabled);
        var clonedCollider = Assert.IsType<ColliderComponent>(
            playScene.MeshInstances[0].Components[2]);
        Assert.NotSame(authoredCollider, clonedCollider);
        Assert.Equal(ColliderShape.Sphere, clonedCollider.Shape);
        Assert.Equal(0.75f, clonedCollider.Radius);
        var clonedBody = Assert.IsType<RigidBodyComponent>(
            playScene.MeshInstances[0].Components[3]);
        Assert.NotSame(authoredBody, clonedBody);
        Assert.Equal(2f, clonedBody.Mass);
        Assert.Equal(Vector3.UnitX, clonedBody.LinearVelocity);
        var clonedAnimator = Assert.IsType<AnimatorComponent>(
            playScene.MeshInstances[0].Components[4]);
        Assert.NotSame(authoredAnimator, clonedAnimator);
        Assert.Equal(animationReference, clonedAnimator.AnimationSource);
        Assert.Equal("Walk", clonedAnimator.Clip);
        Assert.False(clonedAnimator.Loop);
        Assert.Equal(0.75f, clonedAnimator.Speed);
        Assert.Equal(material, Assert.Single(playScene.MeshInstances[0].Materials));
        Assert.NotSame(camera, playScene.GameCamera);
    }

    /// <summary>Verifies play mode rejects an active camera outside the authored scene.</summary>
    [Fact]
    public void Create_CameraOutsideScene_ThrowsInvalidOperationException()
    {
        var root = new Node3D { Name = "Scene" };

        Assert.Throws<InvalidOperationException>(() =>
            ScenePlayClone.Create(root, new PerspectiveCamera()));
    }

    /// <summary>Preserves asset mesh references so play mode can recreate GPU resources.</summary>
    [Fact]
    public void Create_AssetMesh_PreservesResourceReferences()
    {
        var root = new Node3D { Name = "Scene" };
        var reference = new AssetReference(AssetId.New(), "mesh/0");
        var material = new AssetReference(reference.Asset, "material/0");
        var imported = new MeshInstance3D
        {
            Mesh = reference,
            Name = "Character"
        };
        imported.Materials.Add(material);
        var camera = new PerspectiveCamera { Name = "Camera" };
        root.AddChild(imported);
        root.AddChild(camera);

        var playScene = ScenePlayClone.Create(root, camera);

        var clone = Assert.IsType<MeshInstance3D>(playScene.MeshInstances[0]);
        Assert.NotSame(imported, clone);
        Assert.Equal(reference, clone.Mesh);
        Assert.Equal(material, Assert.Single(clone.Materials));
        Assert.Equal("Character", clone.Name);
    }
}
