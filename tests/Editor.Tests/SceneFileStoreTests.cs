using System.Numerics;
using Editor;
using Engine.Core;
using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

public class SceneFileStoreTests
{
    /// <summary>Verifies scene hierarchy, transforms, meshes, and active camera survive a disk round trip.</summary>
    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesScene()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"scene-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "scene.node");
            var root = new Node3D { Name = "Scene" };
            var group = new Node3D
            {
                Name = "Group",
                Position = new Vector3(1f, 2f, 3f),
                Rotation = new Vector3(0.1f, 0.2f, 0.3f),
                Scale = new Vector3(2f, 3f, 4f)
            };
            var cube = new MeshInstance3D(new CubeMesh())
            {
                Name = "Cube",
                ScriptId = AssetId.New()
            };
            var camera = new PerspectiveCamera(0.9f, near: 0.25f, far: 500f)
            {
                Name = "GameCamera",
                Position = new Vector3(4f, 5f, 6f)
            };
            root.AddChild(group);
            group.AddChild(cube);
            root.AddChild(camera);

            SceneFileStore.Save(path, root, camera);
            var loaded = SceneFileStore.Load(path);

            var loadedGroup = Assert.IsType<Node3D>(loaded.Root.Children[0]);
            Assert.Equal("Group", loadedGroup.Name);
            Assert.Equal(group.Position, loadedGroup.Position);
            Assert.Equal(group.Rotation, loadedGroup.Rotation);
            Assert.Equal(group.Scale, loadedGroup.Scale);
            var loadedCube = Assert.IsType<MeshInstance3D>(loadedGroup.Children[0]);
            Assert.IsType<CubeMesh>(loadedCube.Mesh);
            Assert.Equal(cube.ScriptId, loadedCube.ScriptId);
            Assert.Single(loaded.MeshInstances);
            Assert.Equal("GameCamera", loaded.GameCamera.Name);
            Assert.Equal(camera.Position, loaded.GameCamera.Position);
            Assert.Equal(camera.Fov, loaded.GameCamera.Fov);
            Assert.Equal(camera.Near, loaded.GameCamera.Near);
            Assert.Equal(camera.Far, loaded.GameCamera.Far);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>Verifies save rejects an active camera outside the scene graph.</summary>
    [Fact]
    public void Save_GameCameraOutsideScene_ThrowsInvalidOperationException()
    {
        var root = new Node3D { Name = "Scene" };
        var camera = new PerspectiveCamera();
        var path = Path.Combine(Path.GetTempPath(), $"unused-scene-{Guid.NewGuid():N}.json");

        Assert.Throws<InvalidOperationException>(() => SceneFileStore.Save(path, root, camera));
        Assert.False(File.Exists(path));
    }
}
