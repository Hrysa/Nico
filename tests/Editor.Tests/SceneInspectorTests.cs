using System.Numerics;
using Editor;
using Engine.Assets;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class SceneInspectorTests
{
    /// <summary>Verifies an empty Inspector has no obsolete property-filter input.</summary>
    [Fact]
    public void EmptyInspector_DoesNotContainFilterField()
    {
        var view = EditorUI.BuildView(1280f, 720f);

        Assert.Null(FindByName<TextField>(view.Root, "PropertyFilter"));
        Assert.Same(view.Inspector, FindByName<SceneInspector>(view.Root, "SceneInspector"));
    }

    /// <summary>Verifies text and transform fields update their bound authored node.</summary>
    [Fact]
    public void BoundInspector_FieldInput_UpdatesNode()
    {
        var node = new Node3D
        {
            Name = "Cube",
            Position = new Vector3(1f, 2f, 3f)
        };
        var inspector = new SceneInspector(300f, 500f);
        var changeCount = 0;
        var nameChangeCount = 0;
        inspector.NodeChanged += _ => changeCount++;
        inspector.NodeNameChanged += _ => nameChangeCount++;
        inspector.Bind(node);
        var name = Assert.IsType<TextField>(FindByName<TextField>(inspector, "NameField"));
        var positionX = Assert.IsType<TextField>(FindByName<TextField>(inspector, "PositionX"));
        var script = Assert.IsType<TextField>(FindByName<TextField>(inspector, "ScriptAssetField"));

        name.SetFocus(true);
        name.InvokeTextInput('2');
        node.Position = new Vector3(node.Position.X, 8f, node.Position.Z);
        positionX.SetFocus(true);
        positionX.InvokeKeyDown((int)InputKey.Backspace);
        positionX.InvokeTextInput('5');
        Assert.Equal("Cube2", node.Name);
        Assert.Equal(5f, node.Position.X);
        Assert.Equal(8f, node.Position.Y);
        Assert.True(script.IsReadOnly);
        Assert.Null(node.ScriptId);
        Assert.True(changeCount >= 2);
        Assert.Equal(1, nameChangeCount);
    }

    /// <summary>Verifies non-focused fields follow runtime transform changes.</summary>
    [Fact]
    public void RefreshValues_RuntimeTransform_UpdatesDisplayedPosition()
    {
        var node = new Node3D { Position = Vector3.Zero };
        var inspector = new SceneInspector(300f, 500f);
        inspector.Bind(node);
        var positionX = Assert.IsType<TextField>(FindByName<TextField>(inspector, "PositionX"));
        node.Position = new Vector3(2.5f, 0f, 0f);

        var changed = inspector.RefreshValues();

        Assert.True(changed);
        Assert.Equal("2.5", positionX.Text);
    }

    /// <summary>Verifies dropping a scene-script source fills the Inspector attachment field.</summary>
    [Fact]
    public void ScriptFileDrop_OnScriptAssetField_AttachesPersistentAsset()
    {
        var directory = Directory.CreateTempSubdirectory("nico-script-drop-");
        try
        {
            var path = Path.Combine(directory.FullName, "RotateObject.cs");
            File.WriteAllText(path, """
                using Engine.Scripting;
                namespace ExampleGame.Gameplay;
                public sealed class RotateObject : SceneScript { }
                """);
            var node = new Node3D { Name = "Cube" };
            var inspector = new SceneInspector(300f, 500f);
            var database = new AssetDatabase(directory.FullName, EditorAssetImporters.Select);
            var script = Assert.IsType<AssetMetadataRecord>(database.FindByPath(path));
            inspector.ResolveScriptName = id => database.Find(id)?.ProjectPath;
            inspector.Bind(node);
            var field = Assert.IsType<TextField>(
                FindByName<TextField>(inspector, "ScriptAssetField"));

            var attached = ScriptFileDrop.TryAttach(
                new FileSystemNode(path, isDirectory: false), field, inspector, database);

            Assert.True(attached);
            Assert.Equal(script.Id, node.ScriptId);
            Assert.Equal(script.ProjectPath, field.Text);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Verifies ordinary C# files cannot be attached as scene scripts.</summary>
    [Fact]
    public void ScriptFileDrop_OnNonCSharpAsset_IsRejected()
    {
        var directory = Directory.CreateTempSubdirectory("nico-script-drop-");
        try
        {
            var path = Path.Combine(directory.FullName, "Texture.png");
            File.WriteAllText(path, "not an image");
            var database = new AssetDatabase(directory.FullName, EditorAssetImporters.Select);
            var node = new Node3D { Name = "Cube" };
            var inspector = new SceneInspector(300f, 500f);
            inspector.Bind(node);
            var field = Assert.IsType<TextField>(
                FindByName<TextField>(inspector, "ScriptAssetField"));

            var attached = ScriptFileDrop.TryAttach(
                new FileSystemNode(path, isDirectory: false), field, inspector, database);

            Assert.False(attached);
            Assert.Null(node.ScriptId);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Finds a named UI element recursively.</summary>
    /// <typeparam name="TElement">Required UI element type.</typeparam>
    /// <param name="root">Subtree root.</param>
    /// <param name="name">Element name.</param>
    /// <returns>The matching element, or null.</returns>
    private static TElement? FindByName<TElement>(UIElement root, string name)
        where TElement : UIElement
    {
        if (root is TElement match && root.Name == name)
            return match;
        foreach (var child in root.Children.OfType<UIElement>())
        {
            if (FindByName<TElement>(child, name) is { } descendant)
                return descendant;
        }
        return null;
    }
}
