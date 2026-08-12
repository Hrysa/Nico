using Editor;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

/// <summary>Exercises animation-set document loading and clip authoring.</summary>
public sealed class AnimationSetEditorTests
{
    /// <summary>Loads readable source data into the retained list and detail form.</summary>
    [Fact]
    public void Open_ValidSource_SelectsFirstEntry()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "Locomotion.nanimset");
        var reference = new AssetReference(AssetId.New(), "animation/0");
        AnimationSetAuthoring.Save(path,
            [new AnimationSetEntry("Run", reference, null, true, "Hips")]);
        var editor = new AnimationSetEditor
        {
            ResolveRootJoints = _ => new AnimationRootJointOptions(
                ["Root", "Hips"], "Hips")
        };

        editor.Open(path);

        Assert.Equal(path, editor.Path);
        Assert.Equal("Run", Assert.Single(editor.Entries).Alias);
        Assert.Equal("Run", FindByName<TextField>(editor, "AnimationAlias")!.Text);
        Assert.True(FindByName<CheckBox>(editor, "AnimationInPlace")!.IsChecked);
        Assert.True(FindByName<CheckBox>(editor, "AnimationLoop")!.IsChecked);
        Assert.Equal(1d, FindByName<NumericField>(editor, "AnimationSpeed")!.Value);
        Assert.Equal("Hips",
            FindByName<ComboBox>(editor, "AnimationRootMotionJoint")!.SelectedItem);
    }

    /// <summary>Adds an imported animation with a unique alias and saves readable JSON.</summary>
    [Fact]
    public void AddAndSave_ImportedAnimation_PersistsReadableEntry()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "Actions.nanimset");
        AnimationSetAuthoring.Save(path, []);
        var reference = new AssetReference(AssetId.New(), "animation/0");
        var source = new ImportedSubAssetNode(
            System.IO.Path.Combine(directory.Path, "Run.glb"), reference,
            "nico/skeletal-animation", "Run [Animation]");
        var editor = new AnimationSetEditor
        {
            ResolveRootJoints = _ => new AnimationRootJointOptions(
                ["Armature", "mixamorig:Hips"], "mixamorig:Hips")
        };
        editor.Open(path);
        var saved = false;
        editor.Saved += _ => saved = true;

        Assert.True(editor.Add(source));
        editor.Save();

        Assert.True(saved);
        Assert.StartsWith("{", File.ReadAllText(path).TrimStart(), StringComparison.Ordinal);
        using var stream = File.OpenRead(path);
        var entry = Assert.Single(AnimationSetResource.Load(stream).Entries);
        Assert.Equal("Run", entry.Alias);
        Assert.Equal(reference, entry.Source);
        Assert.True(entry.InPlace);
        Assert.Equal("mixamorig:Hips", entry.RootMotionJoint);
    }

    /// <summary>Rejects non-animation imported artifacts from the animation set.</summary>
    [Fact]
    public void Add_NonAnimationArtifact_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "Actions.nanimset");
        AnimationSetAuthoring.Save(path, []);
        var source = new ImportedSubAssetNode(
            System.IO.Path.Combine(directory.Path, "Model.glb"),
            new AssetReference(AssetId.New(), "mesh/0"),
            "nico/skinned-mesh", "Model [Skinned Mesh]");
        var editor = new AnimationSetEditor();
        editor.Open(path);

        Assert.False(editor.Add(source));
        Assert.Empty(editor.Entries);
    }

    /// <summary>Add toolbar is visible and physical GLB resolution can add its animations.</summary>
    [Fact]
    public void AddButton_PhysicalGlb_UsesConfiguredAnimationResolver()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "Actions.nanimset");
        var glbPath = System.IO.Path.Combine(directory.Path, "Actions.glb");
        AnimationSetAuthoring.Save(path, []);
        var file = new FileSystemNode(glbPath, false);
        var imported = new ImportedSubAssetNode(glbPath,
            new AssetReference(AssetId.New(), "animation/0"),
            "nico/skeletal-animation", "Jump [Animation]");
        var editor = new AnimationSetEditor
        {
            ResolveFileAnimations = _ => [imported]
        };
        editor.Open(path);
        var requested = false;
        editor.AddRequested += () =>
        {
            requested = true;
            editor.Add(file);
        };

        FindByName<Button>(editor, "AnimationSetAdd")!.InvokeClick();

        Assert.True(requested);
        Assert.Equal("Jump", Assert.Single(editor.Entries).Alias);
    }

    /// <summary>Form controls remain bounded by the dock viewport during layout.</summary>
    [Fact]
    public void Layout_LongSourceReference_RemainsInsideEditorWidth()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "Actions.nanimset");
        AnimationSetAuthoring.Save(path,
        [
            new AnimationSetEntry("Run",
                new AssetReference(AssetId.New(), new string('x', 300)))
        ]);
        var editor = new AnimationSetEditor { Width = 700f, Height = 400f };
        editor.Open(path);

        editor.BuildDrawList();

        var source = FindByName<TextField>(editor, "AnimationSource")!;
        Assert.True(float.IsFinite(source.Width));
        Assert.InRange(source.Right, editor.Left, editor.Right);
    }

    /// <summary>Finds a named descendant in one retained UI subtree.</summary>
    /// <typeparam name="TElement">Requested element type.</typeparam>
    /// <param name="root">Subtree root.</param>
    /// <param name="name">Stable element name.</param>
    /// <returns>Matching element, or null.</returns>
    private static TElement? FindByName<TElement>(UIElement root, string name)
        where TElement : UIElement
    {
        if (root is TElement match && string.Equals(root.Name, name, StringComparison.Ordinal))
            return match;
        var children = root.Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child &&
                FindByName<TElement>(child, name) is { } descendant)
                return descendant;
        }
        return null;
    }

    /// <summary>Owns one disposable test directory.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        /// <summary>Gets the absolute directory path.</summary>
        public string Path { get; } = Directory.CreateTempSubdirectory(
            "nico-animation-editor-").FullName;

        /// <summary>Deletes the test directory.</summary>
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
