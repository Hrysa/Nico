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

    /// <summary>Verifies revisiting a node reuses its retained Inspector controls.</summary>
    [Fact]
    public void Bind_RevisitedNode_ReusesConstructedControls()
    {
        var first = new Node3D { Name = "First" };
        var second = new Node3D { Name = "Second" };
        var inspector = new SceneInspector(300f, 500f);
        inspector.Bind(first);
        var originalNameField = Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "NameField"));

        inspector.Bind(second);
        inspector.Bind(first);

        Assert.Same(originalNameField,
            FindByName<TextField>(inspector, "NameField"));
        Assert.Equal("First", originalNameField.Text);
    }

    /// <summary>Creates and resets a scene-local material override through the Inspector.</summary>
    [Fact]
    public void Material_FirstPropertyEdit_CreatesCopyOnWriteOverride()
    {
        var cube = new MeshInstance3D { Name = "Cube" };
        var inspector = new SceneInspector(320f, 560f);
        inspector.Bind(cube);
        var red = Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "MaterialBaseColorR"));
        var slot = Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "MaterialSlot0"));

        red.SetFocus(true);
        red.InvokeKeyDown((int)InputKey.Backspace);
        red.InvokeKeyDown((int)InputKey.Backspace);
        red.InvokeKeyDown((int)InputKey.Backspace);
        red.InvokeTextInput('0');
        red.InvokeTextInput('.');
        red.InvokeTextInput('2');

        Assert.NotNull(cube.MaterialOverride);
        Assert.Equal(0.2f, cube.MaterialOverride.BaseColor.X);
        inspector.RefreshValues();
        Assert.Equal("Scene Override", slot.Text);

        Assert.IsType<Button>(FindByName<Button>(inspector, "MaterialReset")).InvokeClick();

        Assert.Null(cube.MaterialOverride);
        Assert.Equal("BuiltIn/Default", Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "MaterialSlot0")).Text);
    }

    /// <summary>Accepts typed material and texture references in the material panel.</summary>
    [Fact]
    public void Material_TypedAssignments_UpdateMeshInstance()
    {
        var mesh = new MeshInstance3D();
        var inspector = new SceneInspector(320f, 560f);
        inspector.Bind(mesh);
        var material = new AssetReference(AssetId.New(), "material/Body");
        var texture = new AssetReference(AssetId.New(), "texture/Albedo");

        Assert.True(inspector.AssignMaterial(material));
        Assert.Equal(material, Assert.Single(mesh.Materials));
        Assert.Null(mesh.MaterialOverride);

        Assert.True(inspector.AssignBaseColorTexture(texture));
        Assert.Equal(texture, mesh.MaterialOverride?.BaseColorTexture);
    }

    /// <summary>Resolves an imported material once when binding instead of during each refresh.</summary>
    [Fact]
    public void Material_RefreshValues_DoesNotRepeatedlyResolveImportedMaterial()
    {
        var mesh = new MeshInstance3D();
        var resolveCount = 0;
        var inspector = new SceneInspector(320f, 560f)
        {
            ResolveMaterial = _ =>
            {
                resolveCount++;
                return new MaterialProperties();
            }
        };

        inspector.Bind(mesh);
        inspector.RefreshValues();
        inspector.RefreshValues();

        Assert.Equal(1, resolveCount);
    }

    /// <summary>Creates a scene override when editing material values on an imported mesh.</summary>
    [Fact]
    public void ImportedMesh_MaterialPropertyEdit_CreatesEditableOverride()
    {
        var mesh = new MeshInstance3D
        {
            Mesh = new AssetReference(AssetId.New(), "mesh/Body")
        };
        mesh.Materials.Add(new AssetReference(mesh.Mesh.Asset, "material/Body"));
        var inspector = new SceneInspector(320f, 560f)
        {
            ResolveMaterial = _ => new MaterialProperties
            {
                BaseColor = new Vector4(0.8f, 0.7f, 0.6f, 1f)
            }
        };
        inspector.Bind(mesh);
        var red = Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "MaterialBaseColorR"));

        red.SetFocus(true);
        red.InvokeKeyDown((int)InputKey.Backspace);
        red.InvokeKeyDown((int)InputKey.Backspace);
        red.InvokeKeyDown((int)InputKey.Backspace);
        red.InvokeTextInput('0');
        red.InvokeTextInput('.');
        red.InvokeTextInput('2');

        Assert.NotNull(mesh.MaterialOverride);
        Assert.Equal(0.2f, mesh.MaterialOverride.BaseColor.X);
    }

    /// <summary>Routes pointer and text input to an imported mesh material field in the real layout.</summary>
    [Fact]
    public void ImportedMesh_MaterialPropertyEdit_IsReachableThroughEditorLayout()
    {
        var view = EditorUI.BuildView(1280f, 720f);
        var mesh = new MeshInstance3D
        {
            Mesh = new AssetReference(AssetId.New(), "mesh/Body")
        };
        mesh.Materials.Add(new AssetReference(mesh.Mesh.Asset, "material/Body"));
        view.Inspector.ResolveMaterial = _ => new MaterialProperties();
        view.Inspector.Bind(mesh);
        view.Root.BuildDrawList();
        var field = Assert.IsType<TextField>(
            FindByName<TextField>(view.Inspector, "MaterialRoughness"));
        var router = new UIEventRouter(view.Root, () => { });

        router.MovePointer(new Vector2(field.Left + 4f, field.Top + field.Height / 2f));
        router.Press();
        router.KeyDown((int)InputKey.Backspace);
        router.TextInput('0');

        Assert.Same(field, router.FocusedElement);
        Assert.NotNull(mesh.MaterialOverride);
    }

    /// <summary>Defers expensive renderer notification until an imported material edit commits.</summary>
    [Fact]
    public void ImportedMesh_MaterialTyping_NotifiesOnceWhenFieldLosesFocus()
    {
        var mesh = new MeshInstance3D
        {
            Mesh = new AssetReference(AssetId.New(), "mesh/Body")
        };
        var inspector = new SceneInspector(320f, 560f)
        {
            ResolveMaterial = _ => new MaterialProperties()
        };
        var changes = 0;
        inspector.NodeChanged += _ => changes++;
        inspector.Bind(mesh);
        var roughness = Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "MaterialRoughness"));

        roughness.SetFocus(true);
        roughness.InvokeKeyDown((int)InputKey.Backspace);
        roughness.InvokeKeyDown((int)InputKey.Backspace);
        roughness.InvokeKeyDown((int)InputKey.Backspace);
        roughness.InvokeTextInput('0');
        roughness.InvokeTextInput('.');
        roughness.InvokeTextInput('5');
        Assert.Equal(0, changes);

        roughness.SetFocus(false);

        Assert.Equal(1, changes);
        Assert.Equal(0.5f, mesh.MaterialOverride?.Roughness);
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
