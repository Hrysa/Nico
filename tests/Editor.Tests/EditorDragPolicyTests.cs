using Editor;
using Engine.Assets;
using Engine.Core;
using Xunit;

namespace Editor.Tests;

public sealed class EditorDragPolicyTests
{
    /// <summary>Rejects GLB metadata and category rows as draggable asset sources.</summary>
    [Fact]
    public void CanStartFileSystemDrag_ImportedMetadata_ReturnsFalse()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "Character.glb");
        var metadata = new ImportedAssetObjectNode(
            sourcePath, "node/0", "node", "Armature");
        var category = new Node { Name = "Nodes" };

        Assert.False(EditorDragPolicy.CanStartFileSystemDrag(metadata));
        Assert.False(EditorDragPolicy.CanStartFileSystemDrag(category));
    }

    /// <summary>Allows imported meshes but rejects material and texture scene instantiation.</summary>
    [Theory]
    [InlineData("nico/static-mesh", true)]
    [InlineData("nico/skinned-mesh", true)]
    [InlineData("nico/standard-material", false)]
    [InlineData("nico/texture2d", false)]
    public void CanInstantiateInHierarchy_ImportedArtifact_RequiresMesh(
        string contentType,
        bool expected)
    {
        var source = new ImportedSubAssetNode(
            Path.Combine(Path.GetTempPath(), "Character.glb"),
            new AssetReference(AssetId.New(), "test"), contentType, "Test");

        Assert.True(EditorDragPolicy.CanStartFileSystemDrag(source));
        Assert.Equal(expected, EditorDragPolicy.CanInstantiateInHierarchy(source));
    }

    /// <summary>Allows physical GLB files but rejects folders and unrelated files.</summary>
    [Theory]
    [InlineData("Character.glb", false, true)]
    [InlineData("Character.GLB", false, true)]
    [InlineData("Character.png", false, false)]
    [InlineData("Folder.glb", true, false)]
    public void CanInstantiateInHierarchy_FileSystemEntry_RequiresGlbFile(
        string name,
        bool isDirectory,
        bool expected)
    {
        var source = new FileSystemNode(Path.Combine(Path.GetTempPath(), name), isDirectory);

        Assert.True(EditorDragPolicy.CanStartFileSystemDrag(source));
        Assert.Equal(expected, EditorDragPolicy.CanInstantiateInHierarchy(source));
    }
}
