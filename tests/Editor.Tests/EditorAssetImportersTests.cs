using Editor;
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
    [InlineData("README.md", null)]
    [InlineData("Game.csproj", null)]
    public void Select_KnownExtension_ReturnsExpectedImporter(string path, string? importer)
    {
        Assert.Equal(importer, EditorAssetImporters.Select(path));
    }
}
