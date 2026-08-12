using Editor;
using Engine.Assets;
using Xunit;

namespace Editor.Tests;

public class EditorAssetImportersTests
{
    /// <summary>Verifies the editor selects importers for every supported source format.</summary>
    [Theory]
    [InlineData("Scripts/Move.cs", "csharp-script")]
    [InlineData("Scenes/Main.node", "scene")]
    [InlineData("Scenes/Main.scene.node", "scene")]
    [InlineData("Models/Robot.GLB", "gltf-model")]
    [InlineData("Textures/Grid.PNG", "image-texture")]
    [InlineData("Textures/Grid.jpg", "image-texture")]
    [InlineData("Materials/Ground.nmat", "standard-material")]
    [InlineData("README.md", null)]
    [InlineData("Game.csproj", null)]
    public void Select_KnownExtension_ReturnsExpectedImporter(string path, string? importer)
    {
        Assert.Equal(importer, EditorAssetImporters.Select(path));
    }

    /// <summary>Recognizes generated collision and terrain project source extensions.</summary>
    [Theory]
    [InlineData("map.ncollision", "collision-mesh")]
    [InlineData("height.nterrain", "terrain")]
    [InlineData("character.nanimset", "animation-set")]
    public void Select_CollisionSources_ReturnsTypedImporter(string path, string importer)
    {
        Assert.Equal(importer, EditorAssetImporters.Select(path));
    }

    /// <summary>Ensures every selected importer ID is present in the Editor registry.</summary>
    [Fact]
    public void RegisterAll_EverySelectedImporter_Resolves()
    {
        string[] paths =
        [
            "Scripts/Move.cs", "Scenes/Main.node", "Models/Robot.glb",
            "Map.ncollision", "Map.nterrain", "Character.nanimset",
            "Materials/Ground.nmat", "Textures/Grid.png"
        ];
        var registry = new AssetImporterRegistry();

        EditorAssetImporters.RegisterAll(registry);

        for (var index = 0; index < paths.Length; index++)
        {
            var id = Assert.IsType<string>(EditorAssetImporters.Select(paths[index]));
            Assert.Equal(id, registry.Resolve(id).Id);
        }
    }
}
