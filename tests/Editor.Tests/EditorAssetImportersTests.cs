using Editor;
using Xunit;

namespace Editor.Tests;

public class EditorAssetImportersTests
{
    /// <summary>Verifies the initial editor index includes scripts and node scenes only.</summary>
    [Theory]
    [InlineData("Scripts/Move.cs", "csharp-script")]
    [InlineData("Scenes/Main.node", "scene")]
    [InlineData("Scenes/Main.scene.node", "scene")]
    [InlineData("README.md", null)]
    [InlineData("Game.csproj", null)]
    public void Select_KnownExtension_ReturnsExpectedImporter(string path, string? importer)
    {
        Assert.Equal(importer, EditorAssetImporters.Select(path));
    }
}
