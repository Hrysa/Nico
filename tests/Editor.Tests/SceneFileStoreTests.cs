using System.Numerics;
using Editor;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class SceneFileStoreTests
{
    /// <summary>Round-trips authored directional-light settings and transforms.</summary>
    [Fact]
    public void SaveAndLoad_DirectionalLight_PreservesSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"light-scene-{Guid.NewGuid():N}.node");
        var root = new Node3D();
        var camera = new PerspectiveCamera();
        var light = new DirectionalLight3D
        {
            Name = "Sun",
            Rotation = new Vector3(0.1f, 0.2f, 0.3f),
            Color = new Vector3(1f, 0.75f, 0.5f),
            Intensity = 2.5f,
            AmbientIntensity = 0.15f,
            IsEnabled = false
        };
        root.AddChild(light);
        root.AddChild(camera);
        try
        {
            SceneFileStore.Save(path, root, camera);
            var loaded = SceneFileStore.Load(path);

            var actual = Assert.IsType<DirectionalLight3D>(loaded.Root.Children[0]);
            Assert.Equal(light.Name, actual.Name);
            Assert.Equal(light.Rotation, actual.Rotation);
            Assert.Equal(light.Color, actual.Color);
            Assert.Equal(light.Intensity, actual.Intensity);
            Assert.Equal(light.AmbientIntensity, actual.AmbientIntensity);
            Assert.False(actual.IsEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Verifies higher-level HUD roots survive scene persistence through their factory.</summary>
    [Fact]
    public void SaveAndLoad_HudRoot_PreservesCustomNodeAndComponents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hud-scene-{Guid.NewGuid():N}.node");
        var root = new Node3D { Name = "Scene" };
        var hud = new HudRoot { Name = "Player HUD" };
        var scriptId = AssetId.New();
        hud.AddComponent(new ScriptComponent(scriptId));
        var camera = new PerspectiveCamera { Name = "Camera" };
        root.AddChild(hud);
        root.AddChild(camera);
        try
        {
            SceneFileStore.Save(path, root, camera);

            Assert.Throws<InvalidDataException>(() => SceneFileStore.Load(path));
            var loaded = SceneFileStore.Load(path, HudSceneNodeFactory.Instance);

            var loadedHud = Assert.IsType<HudRoot>(loaded.Root.Children[0]);
            Assert.Equal("Player HUD", loadedHud.Name);
            Assert.Equal(scriptId,
                Assert.IsType<ScriptComponent>(Assert.Single(loadedHud.Components)).ScriptId);
            Assert.True(loadedHud.Content.IsOverlay);
            Assert.True(loadedHud.Content.ClipToBounds);
        }
        finally
        {
            File.Delete(path);
        }
    }

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
            var firstScript = Assert.IsType<ScriptComponent>(Assert.Single(cube.Components));
            firstScript.SetPropertyOverride(101, SerializedPropertyValue.From(2.5d));
            var secondScript = new ScriptComponent(AssetId.New()) { Enabled = false };
            secondScript.SetPropertyOverride(202,
                SerializedPropertyValue.From(new Vector3(1f, 2f, 3f)));
            cube.AddComponent(secondScript);
            cube.AddComponent(new CapsuleColliderComponent
            {
                Center = new Vector3(0f, 0.25f, 0f),
                Radius = 0.75f,
                Height = 2.5f,
                Friction = 0.25f,
                Restitution = 0.4f
            });
            cube.AddComponent(new RigidBodyComponent
            {
                Mass = 3f,
                LinearVelocity = new Vector3(1f, 2f, 3f),
                GravityScale = 0.5f,
                LinearDamping = 0.1f
            });
            var animationReference = new AssetReference(AssetId.New(), "animation/0");
            var animationSetReference = new AssetReference(AssetId.New(), "main");
            cube.AddComponent(new AnimatorComponent
            {
                AnimationSource = animationReference,
                AnimationSet = animationSetReference,
                DefaultClip = "Run",
                PlayAutomatically = false,
                Loop = false,
                Speed = 1.5f,
                DefaultFadeDuration = 0.35f
            });
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
            Assert.Equal(5, loadedCube.Components.Count);
            var loadedFirstScript = Assert.IsType<ScriptComponent>(loadedCube.Components[0]);
            Assert.True(loadedFirstScript.TryGetPropertyOverride(101, out var loadedNumber));
            Assert.True(loadedNumber.TryGetNumber(out var number));
            Assert.Equal(2.5d, number);
            var loadedSecondScript = Assert.IsType<ScriptComponent>(loadedCube.Components[1]);
            Assert.Equal(secondScript.ScriptId, loadedSecondScript.ScriptId);
            Assert.False(loadedSecondScript.Enabled);
            Assert.True(loadedSecondScript.TryGetPropertyOverride(202, out var loadedVector));
            Assert.True(loadedVector.TryGetVector3(out var vector));
            Assert.Equal(new Vector3(1f, 2f, 3f), vector);
            var loadedCollider = Assert.IsType<CapsuleColliderComponent>(loadedCube.Components[2]);
            Assert.Equal(new Vector3(0f, 0.25f, 0f), loadedCollider.Center);
            Assert.Equal(0.75f, loadedCollider.Radius);
            Assert.Equal(2.5f, loadedCollider.Height);
            Assert.Equal(0.25f, loadedCollider.Friction);
            Assert.Equal(0.4f, loadedCollider.Restitution);
            var loadedBody = Assert.IsType<RigidBodyComponent>(loadedCube.Components[3]);
            Assert.Equal(3f, loadedBody.Mass);
            Assert.Equal(new Vector3(1f, 2f, 3f), loadedBody.LinearVelocity);
            Assert.Equal(0.5f, loadedBody.GravityScale);
            Assert.Equal(0.1f, loadedBody.LinearDamping);
            var loadedAnimator = Assert.IsType<AnimatorComponent>(loadedCube.Components[4]);
            Assert.Equal(animationReference, loadedAnimator.AnimationSource);
            Assert.Equal(animationSetReference, loadedAnimator.AnimationSet);
            Assert.Equal("Run", loadedAnimator.DefaultClip);
            Assert.False(loadedAnimator.PlayAutomatically);
            Assert.False(loadedAnimator.Loop);
            Assert.Equal(1.5f, loadedAnimator.Speed);
            Assert.Equal(0.35f, loadedAnimator.DefaultFadeDuration);
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

    /// <summary>Loads format-three scenes through the legacy single-script compatibility field.</summary>
    [Fact]
    public void Load_FormatThreeScene_MigratesScriptIdToComponent()
    {
        var scriptId = AssetId.New();
        var path = Path.Combine(Path.GetTempPath(), $"legacy-scene-{Guid.NewGuid():N}.node");
        File.WriteAllText(path, $$"""
            {
              "formatVersion": 3,
              "gameCameraId": "camera",
              "nodes": [
                {
                  "id": "target",
                  "type": "node3D",
                  "name": "Target",
                  "position": { "x": 0, "y": 0, "z": 0 },
                  "rotation": { "x": 0, "y": 0, "z": 0 },
                  "scale": { "x": 1, "y": 1, "z": 1 },
                  "scriptId": "{{scriptId}}",
                  "camera": null,
                  "model": null,
                  "materialOverride": null,
                  "children": []
                },
                {
                  "id": "camera",
                  "type": "perspectiveCamera",
                  "name": "Camera",
                  "position": { "x": 0, "y": 0, "z": 0 },
                  "rotation": { "x": 0, "y": 0, "z": 0 },
                  "scale": { "x": 1, "y": 1, "z": 1 },
                  "scriptId": null,
                  "camera": { "fov": 0.8, "near": 0.1, "far": 100 },
                  "model": null,
                  "materialOverride": null,
                  "children": []
                }
              ]
            }
            """);
        try
        {
            var scene = SceneFileStore.Load(path);
            var target = Assert.IsType<Node3D>(scene.Root.Children[0]);
            var component = Assert.IsType<ScriptComponent>(Assert.Single(target.Components));

            Assert.Equal(scriptId, component.ScriptId);
            Assert.Equal(scriptId, target.ScriptId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Round-trips every new built-in primitive through the generic mesh scene record.</summary>
    [Fact]
    public void SaveAndLoad_BuiltInPrimitives_PreservesMeshReferences()
    {
        var path = Path.Combine(Path.GetTempPath(), $"primitive-scene-{Guid.NewGuid():N}.node");
        var root = new Node3D { Name = "Scene" };
        AssetReference[] references =
        [
            BuiltInAssets.PlaneMesh,
            BuiltInAssets.SphereMesh,
            BuiltInAssets.CapsuleMesh,
            BuiltInAssets.CylinderMesh
        ];
        for (var index = 0; index < references.Length; index++)
            root.AddChild(new MeshInstance3D { Name = $"Primitive{index}", Mesh = references[index] });
        var camera = new PerspectiveCamera { Name = "Camera" };
        root.AddChild(camera);
        try
        {
            SceneFileStore.Save(path, root, camera);
            var loaded = SceneFileStore.Load(path);

            Assert.Equal(references.Length, loaded.MeshInstances.Count);
            for (var index = 0; index < references.Length; index++)
                Assert.Equal(references[index], loaded.MeshInstances[index].Mesh);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Loads a format-eight shape-switched collider as its concrete replacement.</summary>
    [Fact]
    public void Load_FormatEightCollider_MigratesToConcreteComponent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"legacy-collider-{Guid.NewGuid():N}.node");
        File.WriteAllText(path, """
            {
              "formatVersion": 8,
              "gameCameraId": "camera",
              "nodes": [
                {
                  "id": "body",
                  "type": "node3D",
                  "name": "Body",
                  "position": { "x": 0, "y": 0, "z": 0 },
                  "rotation": { "x": 0, "y": 0, "z": 0 },
                  "scale": { "x": 1, "y": 1, "z": 1 },
                  "components": [
                    {
                      "type": "collider",
                      "enabled": true,
                      "collider": {
                        "shape": "capsule",
                        "center": { "x": 1, "y": 2, "z": 3 },
                        "size": { "x": 1, "y": 1, "z": 1 },
                        "radius": 0.75,
                        "height": 2.5,
                        "isTrigger": true,
                        "friction": 0.25,
                        "restitution": 0.4,
                        "collisionLayer": 8,
                        "collisionMask": 5
                      }
                    }
                  ],
                  "children": []
                },
                {
                  "id": "camera",
                  "type": "perspectiveCamera",
                  "name": "Camera",
                  "position": { "x": 0, "y": 0, "z": 5 },
                  "rotation": { "x": 0, "y": 0, "z": 0 },
                  "scale": { "x": 1, "y": 1, "z": 1 },
                  "camera": { "fov": 0.8, "near": 0.1, "far": 100 },
                  "components": [],
                  "children": []
                }
              ]
            }
            """);
        try
        {
            var loaded = SceneFileStore.Load(path);

            var body = Assert.IsType<Node3D>(loaded.Root.Children[0]);
            var collider = Assert.IsType<CapsuleColliderComponent>(Assert.Single(body.Components));
            Assert.Equal(new Vector3(1f, 2f, 3f), collider.Center);
            Assert.Equal(0.75f, collider.Radius);
            Assert.Equal(2.5f, collider.Height);
            Assert.True(collider.IsTrigger);
            Assert.Equal(0.25f, collider.Friction);
            Assert.Equal(0.4f, collider.Restitution);
            Assert.Equal(8u, collider.CollisionLayer);
            Assert.Equal(5u, collider.CollisionMask);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Loads format-nine animator fields into the state-driven defaults.</summary>
    [Fact]
    public void Load_FormatNineAnimator_MigratesClipAndFadeDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), $"legacy-animator-{Guid.NewGuid():N}.node");
        File.WriteAllText(path, """
            {
              "formatVersion": 9,
              "gameCameraId": "camera",
              "nodes": [
                {
                  "id": "animated",
                  "type": "node3D",
                  "name": "Animated",
                  "position": { "x": 0, "y": 0, "z": 0 },
                  "rotation": { "x": 0, "y": 0, "z": 0 },
                  "scale": { "x": 1, "y": 1, "z": 1 },
                  "components": [
                    {
                      "type": "animator",
                      "enabled": true,
                      "animator": {
                        "animationSource": null,
                        "clip": "LegacyRun",
                        "playAutomatically": true,
                        "loop": false,
                        "speed": 1.25
                      }
                    }
                  ],
                  "children": []
                },
                {
                  "id": "camera",
                  "type": "perspectiveCamera",
                  "name": "Camera",
                  "position": { "x": 0, "y": 0, "z": 5 },
                  "rotation": { "x": 0, "y": 0, "z": 0 },
                  "scale": { "x": 1, "y": 1, "z": 1 },
                  "camera": { "fov": 0.8, "near": 0.1, "far": 100 },
                  "components": [],
                  "children": []
                }
              ]
            }
            """);
        try
        {
            var loaded = SceneFileStore.Load(path);
            var animated = Assert.IsType<Node3D>(loaded.Root.Children[0]);
            var animator = Assert.IsType<AnimatorComponent>(Assert.Single(animated.Components));

            Assert.Equal("LegacyRun", animator.DefaultClip);
            Assert.Equal(0.2f, animator.DefaultFadeDuration);
            Assert.False(animator.Loop);
            Assert.Equal(1.25f, animator.Speed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Round-trips multiple concrete colliders and their explicit asset references.</summary>
    [Fact]
    public void SaveAndLoad_AllConcreteColliders_PreservesAuthoredOrderAndReferences()
    {
        var path = Path.Combine(Path.GetTempPath(), $"all-colliders-{Guid.NewGuid():N}.node");
        var meshReference = new AssetReference(AssetId.New(), "mesh/collision/0");
        var terrainReference = new AssetReference(AssetId.New(), "terrain/0");
        var root = new Node3D();
        var node = new Node3D();
        node.AddComponent(new BoxColliderComponent { Size = new Vector3(1f, 2f, 3f) });
        node.AddComponent(new SphereColliderComponent { Radius = 2f });
        node.AddComponent(new CapsuleColliderComponent { Radius = .4f, Height = 3f });
        node.AddComponent(new CylinderColliderComponent { Radius = .7f, Height = 4f });
        node.AddComponent(new PlaneColliderComponent { Size = new Vector2(20f, 30f) });
        node.AddComponent(new MeshColliderComponent { Mesh = meshReference });
        node.AddComponent(new TerrainColliderComponent
        {
            TerrainData = terrainReference,
            HorizontalSize = new Vector2(100f, 200f),
            HeightScale = 25f
        });
        var camera = new PerspectiveCamera();
        root.AddChild(node);
        root.AddChild(camera);
        try
        {
            SceneFileStore.Save(path, root, camera);
            var loaded = SceneFileStore.Load(path);
            var components = loaded.Root.Children[0].Components;

            Assert.Collection(components,
                component => Assert.Equal(new Vector3(1f, 2f, 3f),
                    Assert.IsType<BoxColliderComponent>(component).Size),
                component => Assert.Equal(2f,
                    Assert.IsType<SphereColliderComponent>(component).Radius),
                component => Assert.Equal(3f,
                    Assert.IsType<CapsuleColliderComponent>(component).Height),
                component => Assert.Equal(4f,
                    Assert.IsType<CylinderColliderComponent>(component).Height),
                component => Assert.Equal(new Vector2(20f, 30f),
                    Assert.IsType<PlaneColliderComponent>(component).Size),
                component => Assert.Equal(meshReference,
                    Assert.IsType<MeshColliderComponent>(component).Mesh),
                component =>
                {
                    var terrain = Assert.IsType<TerrainColliderComponent>(component);
                    Assert.Equal(terrainReference, terrain.TerrainData);
                    Assert.Equal(new Vector2(100f, 200f), terrain.HorizontalSize);
                    Assert.Equal(25f, terrain.HeightScale);
                });
        }
        finally
        {
            File.Delete(path);
        }
    }
}
