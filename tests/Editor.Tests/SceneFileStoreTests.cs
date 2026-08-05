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
            var cube = new MeshInstance3D
            {
                Name = "Cube",
                ScriptId = AssetId.New(),
                MaterialOverride = new MaterialProperties
                {
                    BaseColor = new Vector4(0.2f, 0.3f, 0.4f, 1f),
                    Metallic = 0.6f,
                    Roughness = 0.7f
                }
            };
            var camera = new PerspectiveCamera(0.9f, near: 0.25f, far: 500f)
            {
                Name = "GameCamera",
                Position = new Vector3(4f, 5f, 6f)
            };
            var modelReference = new AssetReference(AssetId.New(), "mesh/Robot/0");
            var materialReference = new AssetReference(modelReference.Asset, "material/0");
            var importedModel = new MeshInstance3D
            {
                Mesh = modelReference,
                Name = "Robot",
                Position = new Vector3(7f, 8f, 9f)
            };
            importedModel.Materials.Add(materialReference);
            root.AddChild(group);
            group.AddChild(cube);
            root.AddChild(camera);
            root.AddChild(importedModel);

            SceneFileStore.Save(path, root, camera);
            var loaded = SceneFileStore.Load(path);

            var loadedGroup = Assert.IsType<Node3D>(loaded.Root.Children[0]);
            Assert.Equal("Group", loadedGroup.Name);
            Assert.Equal(group.Position, loadedGroup.Position);
            Assert.Equal(group.Rotation, loadedGroup.Rotation);
            Assert.Equal(group.Scale, loadedGroup.Scale);
            var loadedCube = Assert.IsType<MeshInstance3D>(loadedGroup.Children[0]);
            Assert.Equal(BuiltInAssets.CubeMesh, loadedCube.Mesh);
            Assert.Equal(cube.ScriptId, loadedCube.ScriptId);
            Assert.Equal(cube.MaterialOverride.BaseColor, loadedCube.MaterialOverride?.BaseColor);
            Assert.Equal(cube.MaterialOverride.Metallic, loadedCube.MaterialOverride?.Metallic);
            Assert.Equal(2, loaded.MeshInstances.Count);
            var loadedModel = Assert.IsType<MeshInstance3D>(loaded.Root.Children[2]);
            Assert.Equal(modelReference, loadedModel.Mesh);
            Assert.Equal(materialReference, Assert.Single(loadedModel.Materials));
            Assert.Equal(importedModel.Position, loadedModel.Position);
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
