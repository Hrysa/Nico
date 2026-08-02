using System.Numerics;
using Editor;
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
        var cube = new MeshInstance3D(new CubeMesh())
        {
            Name = "Cube",
            Position = new Vector3(2f, 0f, 0f),
            ScriptType = "Example.Move, Example.Scripts"
        };
        var camera = new PerspectiveCamera { Name = "Camera" };
        root.AddChild(cube);
        root.AddChild(camera);

        var playScene = ScenePlayClone.Create(root, camera);
        playScene.MeshInstances[0].Position = new Vector3(20f, 0f, 0f);

        Assert.NotSame(cube, playScene.MeshInstances[0]);
        Assert.NotSame(cube.Mesh, playScene.MeshInstances[0].Mesh);
        Assert.Equal(new Vector3(2f, 0f, 0f), cube.Position);
        Assert.Equal(cube.ScriptType, playScene.MeshInstances[0].ScriptType);
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
}
