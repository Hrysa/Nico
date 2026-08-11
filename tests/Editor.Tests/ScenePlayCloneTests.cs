using System.Numerics;
using Editor;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;
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
        var authoredCollider = new SphereColliderComponent
        {
            Radius = 0.75f
        };
        var authoredBody = new RigidBodyComponent
        {
            Mass = 2f,
            LinearVelocity = Vector3.UnitX
        };
        cube.AddComponent(authoredCollider);
        cube.AddComponent(authoredBody);
        var camera = new PerspectiveCamera { Name = "Camera" };
        var light = new DirectionalLight3D
        {
            Color = new Vector3(0.8f, 0.7f, 0.6f),
            Intensity = 1.5f,
            AmbientIntensity = 0.12f,
            IsEnabled = false
        };
        root.AddChild(cube);
        root.AddChild(camera);
        root.AddChild(light);

        var playScene = ScenePlayClone.Create(root, camera);
        playScene.MeshInstances[0].Position = new Vector3(20f, 0f, 0f);

        Assert.NotSame(cube, playScene.MeshInstances[0]);
        Assert.Equal(cube.Mesh, playScene.MeshInstances[0].Mesh);
        Assert.Equal(new Vector3(2f, 0f, 0f), cube.Position);
        Assert.Equal(cube.ScriptId, playScene.MeshInstances[0].ScriptId);
        Assert.Equal(4, playScene.MeshInstances[0].Components.Count);
        var clonedScript = Assert.IsType<ScriptComponent>(
            playScene.MeshInstances[0].Components[0]);
        Assert.NotSame(authoredScript, clonedScript);
        Assert.True(clonedScript.TryGetPropertyOverride(42, out var clonedValue));
        Assert.True(clonedValue.TryGetString(out var text));
        Assert.Equal("authored", text);
        Assert.False(playScene.MeshInstances[0].Components[1].Enabled);
        var clonedCollider = Assert.IsType<SphereColliderComponent>(
            playScene.MeshInstances[0].Components[2]);
        Assert.NotSame(authoredCollider, clonedCollider);
        Assert.Equal(0.75f, clonedCollider.Radius);
        var clonedBody = Assert.IsType<RigidBodyComponent>(
            playScene.MeshInstances[0].Components[3]);
        Assert.NotSame(authoredBody, clonedBody);
        Assert.Equal(2f, clonedBody.Mass);
        Assert.Equal(Vector3.UnitX, clonedBody.LinearVelocity);
        Assert.Equal(material, Assert.Single(playScene.MeshInstances[0].Materials));
        Assert.NotSame(camera, playScene.GameCamera);
        var clonedLight = Assert.IsType<DirectionalLight3D>(playScene.Root.Children[2]);
        Assert.NotSame(light, clonedLight);
        Assert.Equal(light.Color, clonedLight.Color);
        Assert.Equal(light.Intensity, clonedLight.Intensity);
        Assert.Equal(light.AmbientIntensity, clonedLight.AmbientIntensity);
        Assert.False(clonedLight.IsEnabled);
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

    /// <summary>Verifies authored HUD identity and scripts are cloned without sharing runtime content.</summary>
    [Fact]
    public void Create_HudRoot_CreatesIndependentRuntimeRoot()
    {
        var root = new Node3D { Name = "Scene" };
        var hud = new HudRoot { Name = "HUD" };
        var scriptId = AssetId.New();
        hud.AddComponent(new ScriptComponent(scriptId));
        hud.Content = new Label("Edit-only preview");
        var camera = new PerspectiveCamera { Name = "Camera" };
        root.AddChild(hud);
        root.AddChild(camera);

        var playScene = ScenePlayClone.Create(root, camera);

        var clone = Assert.IsType<HudRoot>(playScene.Root.Children[0]);
        Assert.NotSame(hud, clone);
        Assert.NotSame(hud.Content, clone.Content);
        Assert.IsType<Canvas>(clone.Content);
        Assert.Equal(scriptId,
            Assert.IsType<ScriptComponent>(Assert.Single(clone.Components)).ScriptId);
    }
}
