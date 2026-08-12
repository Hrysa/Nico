using System.Numerics;
using Editor;
using Engine.Assets;
using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class SceneInspectorTests
{
    /// <summary>Displays provider-independent properties without scene transform controls.</summary>
    [Fact]
    public void Bind_InspectorDocument_ShowsReadOnlyProperties()
    {
        var inspector = new SceneInspector(320f, 500f);

        inspector.Bind(new InspectorDocument("Asset", "Ground.nmat",
            new PropertyInspectorContent([
            new InspectorProperty("Importer", "standard-material"),
            new InspectorProperty("Size", "48 B")
        ])));

        Assert.Null(inspector.InspectedNode);
        var importer = Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "InspectorProperty0"));
        Assert.Equal("standard-material", importer.Text);
        Assert.True(importer.IsReadOnly);
        Assert.Null(FindByName<TextField>(inspector, "NameField"));
    }
    /// <summary>Edits directional-light color and intensity through authored Inspector fields.</summary>
    [Fact]
    public void DirectionalLight_Fields_UpdateAuthoredSettings()
    {
        var light = new DirectionalLight3D();
        var inspector = new SceneInspector(320f, 620f);
        inspector.Bind(light);
        var red = Assert.IsType<TextField>(FindByName<TextField>(inspector, "LightColorX"));
        var intensity = Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "LightIntensity"));
        var enabled = Assert.IsType<ToggleButton>(
            FindByName<ToggleButton>(inspector, "LightEnabled"));

        red.SetFocus(true);
        red.InvokeKeyDown((int)InputKey.Backspace);
        red.InvokeTextInput('0');
        red.InvokeTextInput('.');
        red.InvokeTextInput('5');
        intensity.SetFocus(true);
        intensity.InvokeKeyDown((int)InputKey.Backspace);
        intensity.InvokeTextInput('2');
        intensity.InvokeTextInput('.');
        intensity.InvokeTextInput('2');
        intensity.InvokeTextInput('5');
        Assert.True(inspector.EditForm.CommitAll());
        enabled.InvokeClick();

        Assert.Equal(0.5f, light.Color.X);
        Assert.Equal(2.25f, light.Intensity);
        Assert.False(light.IsEnabled);
    }

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
        Assert.True(inspector.EditForm.CommitAll());
        Assert.Equal("Cube2", node.Name);
        Assert.Equal(5f, node.Position.X);
        Assert.Equal(8f, node.Position.Y);
        Assert.True(script.IsReadOnly);
        Assert.Null(node.ScriptId);
        Assert.True(changeCount >= 2);
        Assert.Equal(1, nameChangeCount);
    }

    /// <summary>Verifies non-focused fields follow runtime transform notifications immediately.</summary>
    [Fact]
    public void RefreshValues_RuntimeTransform_UpdatesDisplayedPosition()
    {
        var node = new Node3D { Position = Vector3.Zero };
        var inspector = new SceneInspector(300f, 500f);
        inspector.Bind(node);
        var positionX = Assert.IsType<TextField>(FindByName<TextField>(inspector, "PositionX"));
        node.Position = new Vector3(2.5f, 0f, 0f);

        Assert.Equal("2.5", positionX.Text);
    }

    /// <summary>Verifies an inactive dock ancestor suppresses Inspector binding refresh work.</summary>
    [Fact]
    public void RefreshValues_InactiveDockPanel_SkipsBindings()
    {
        var node = new Node3D { ScriptId = AssetId.New() };
        var resolveCount = 0;
        var inspector = new SceneInspector(300f, 500f)
        {
            ResolveScriptName = _ =>
            {
                resolveCount++;
                return "PlayerController";
            }
        };
        var tabContent = new Panel(Color.Black);
        tabContent.AddChild(inspector);
        inspector.Bind(node);
        resolveCount = 0;

        tabContent.IsVisible = false;
        var changed = inspector.RefreshValues();

        Assert.False(changed);
        Assert.Equal(0, resolveCount);
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

    /// <summary>Accepts typed material references without creating a scene override.</summary>
    [Fact]
    public void Material_TypedAssignments_UpdateMeshInstance()
    {
        var mesh = new MeshInstance3D();
        var inspector = new SceneInspector(320f, 560f);
        inspector.Bind(mesh);
        var material = new AssetReference(AssetId.New(), "material/Body");

        Assert.True(inspector.AssignMaterial(material));
        Assert.Equal(material, Assert.Single(mesh.Materials));
    }

    /// <summary>Verifies Inspector Apply and Revert buttons control pending numeric edits.</summary>
    [Fact]
    public void Inspector_EditActions_ApplyAndRevertPendingNumericValues()
    {
        var node = new Node3D { Position = new Vector3(1f, 0f, 0f) };
        var inspector = new SceneInspector(320f, 560f);
        inspector.Bind(node);
        var positionX = Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "PositionX"));
        var apply = Assert.IsType<Button>(FindByName<Button>(inspector, "InspectorApply"));
        var revert = Assert.IsType<Button>(FindByName<Button>(inspector, "InspectorRevert"));
        Assert.False(apply.IsEnabled);
        Assert.False(revert.IsEnabled);

        positionX.SetFocus(true);
        positionX.InvokeKeyDown((int)InputKey.Backspace);
        positionX.InvokeTextInput('2');
        Assert.Equal(1f, node.Position.X);
        Assert.True(apply.IsEnabled);
        Assert.True(revert.IsEnabled);

        revert.InvokeClick();
        Assert.Equal("1", positionX.Text);
        Assert.Equal(1f, node.Position.X);

        positionX.SetFocus(true);
        positionX.InvokeKeyDown((int)InputKey.Backspace);
        positionX.InvokeTextInput('3');
        apply.InvokeClick();

        Assert.Equal(3f, node.Position.X);
        Assert.False(apply.IsEnabled);
        Assert.False(revert.IsEnabled);
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
            var attached = ScriptFileDrop.TryAttach(
                new FileSystemNode(path, isDirectory: false), inspector, database);

            Assert.True(attached);
            Assert.Equal(script.Id, node.ScriptId);
            Assert.Equal(script.ProjectPath, Assert.IsType<TextField>(
                FindByName<TextField>(inspector, "ScriptAssetField0")).Text);
            Assert.Equal(string.Empty, Assert.IsType<TextField>(
                FindByName<TextField>(inspector, "ScriptAssetField")).Text);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Displays every authored script plus a distinct slot for adding another one.</summary>
    [Fact]
    public void MultipleScripts_DisplayIndependentRowsAndAddSlot()
    {
        var first = new ScriptComponent(AssetId.New());
        var second = new ScriptComponent(AssetId.New());
        var node = new Node3D();
        node.AddComponent(first);
        node.AddComponent(second);
        var inspector = new SceneInspector(320f, 620f)
        {
            ResolveScriptName = id => id == first.ScriptId ? "First.cs" : "Second.cs"
        };

        inspector.Bind(node);

        Assert.Equal("First.cs", Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "ScriptAssetField0")).Text);
        Assert.Equal("Second.cs", Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "ScriptAssetField1")).Text);
        Assert.Equal("Drop another script here", Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "ScriptAssetField")).Placeholder);
        Assert.NotNull(FindByName<Button>(inspector, "ScriptRemove0"));
        Assert.NotNull(FindByName<Button>(inspector, "ScriptRemove1"));
    }

    /// <summary>Removes only the script represented by the clicked component row.</summary>
    [Fact]
    public void ScriptRemoveButton_RemovesExactComponentAndKeepsAddSlot()
    {
        var first = new ScriptComponent(AssetId.New());
        var second = new ScriptComponent(AssetId.New());
        var node = new Node3D();
        node.AddComponent(first);
        node.AddComponent(second);
        var inspector = new SceneInspector(320f, 620f);
        var changes = 0;
        inspector.NodeChanged += _ => changes++;
        inspector.Bind(node);

        Assert.IsType<Button>(FindByName<Button>(
            inspector, "ScriptRemove0")).InvokeClick();

        Assert.DoesNotContain(first, node.Components);
        Assert.Contains(second, node.Components);
        Assert.Equal(1, changes);
        Assert.NotNull(FindByName<TextField>(inspector, "ScriptAssetField0"));
        Assert.NotNull(FindByName<TextField>(inspector, "ScriptAssetField"));
    }

    /// <summary>Attaches repeated drops as ordered independent script components.</summary>
    [Fact]
    public void AttachScript_MultipleCalls_AppendsComponents()
    {
        var first = AssetId.New();
        var second = AssetId.New();
        var node = new Node3D();
        var inspector = new SceneInspector(320f, 620f);
        inspector.Bind(node);

        Assert.True(inspector.AttachScript(first));
        Assert.True(inspector.AttachScript(second));

        Assert.Equal(2, node.Components.Count);
        Assert.Equal(first, Assert.IsType<ScriptComponent>(node.Components[0]).ScriptId);
        Assert.Equal(second, Assert.IsType<ScriptComponent>(node.Components[1]).ScriptId);
        Assert.NotNull(FindByName<TextField>(inspector, "ScriptAssetField"));
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
            var attached = ScriptFileDrop.TryAttach(
                new FileSystemNode(path, isDirectory: false), inspector, database);

            Assert.False(attached);
            Assert.Null(node.ScriptId);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Uses generated Editor metadata for event-driven script editing and refresh.</summary>
    [Fact]
    public void ObservedScriptProperty_UsesGeneratedBindingWithoutPolling()
    {
        var node = new Node3D { Name = "Player" };
        var component = new ScriptComponent(AssetId.New());
        node.AddComponent(component);
        var script = new TestObservedScript { Speed = 2.5d };
        var inspector = new SceneInspector(320f, 620f)
        {
            ResolveScriptName = _ => "PlayerController.cs",
            ResolveScriptInstance = candidate =>
                ReferenceEquals(candidate, component) ? script : null
        };
        var tabContent = new Panel(Color.Black, 320f, 620f);
        tabContent.AddChild(inspector);
        var nodeChanges = 0;
        inspector.NodeChanged += _ => nodeChanges++;
        inspector.Bind(node);
        var field = Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "ScriptProperty0_Speed"));

        Assert.Equal("2.5", field.Text);
        Assert.Null(FindByName<TextField>(inspector, "ScriptProperty0_RuntimeCounter"));
        tabContent.IsVisible = false;
        script.Speed = 8d;
        Assert.Equal("2.5", field.Text);
        tabContent.IsVisible = true;
        inspector.Measure(new Vector2(320f, 620f));
        inspector.Arrange(Vector2.Zero, new Vector2(320f, 620f));
        Assert.Equal("8", field.Text);
        component.SetPropertyOverride(TestObservedScript.SpeedId,
            SerializedPropertyValue.From(9d));
        Assert.Equal(9d, script.Speed);
        Assert.Equal("9", field.Text);

        field.SetFocus(true);
        field.InvokeKeyDown((int)InputKey.Backspace);
        field.InvokeTextInput('4');
        Assert.True(inspector.EditForm.CommitAll());

        Assert.Equal(4d, script.Speed);
        Assert.True(component.TryGetPropertyOverride(TestObservedScript.SpeedId, out var stored));
        Assert.True(stored.TryGetNumber(out var storedSpeed));
        Assert.Equal(4d, storedSpeed);
        Assert.Equal(1, nodeChanges);
    }

    /// <summary>Exposes concrete collider-only fields and applies validated authored dimensions.</summary>
    [Fact]
    public void Bind_BoxCollider_ShowsRelevantEditablePhysicsFields()
    {
        var node = new Node3D();
        var collider = new BoxColliderComponent();
        node.AddComponent(collider);
        var inspector = new SceneInspector(320f, 900f);
        inspector.Bind(node, collider);
        var sizeX = Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "Collider0SizeX"));

        sizeX.SetFocus(true);
        sizeX.InvokeKeyDown((int)InputKey.Backspace);
        sizeX.InvokeTextInput('2');
        Assert.True(inspector.EditForm.CommitAll());

        Assert.Same(collider, inspector.FocusedComponent);
        Assert.Equal(2f, collider.Size.X);
        Assert.NotNull(FindByName<TextField>(inspector, "Collider0Friction"));
        Assert.NotNull(FindByName<TextField>(inspector, "Collider0Layer"));
        Assert.Null(FindByName<TextField>(inspector, "Collider0Radius"));
    }

    /// <summary>Exposes an explicit editable mesh reference instead of inferred render geometry.</summary>
    [Fact]
    public void Bind_MeshCollider_ShowsExplicitReferenceField()
    {
        var node = new MeshInstance3D();
        var collider = new MeshColliderComponent();
        node.AddComponent(collider);
        var inspector = new SceneInspector(320f, 900f);

        inspector.Bind(node, collider);

        var field = Assert.IsType<TextField>(
            FindByName<TextField>(inspector, "Collider0Mesh"));
        Assert.Equal(string.Empty, field.Text);
        Assert.False(field.IsReadOnly);
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

    /// <summary>Provides generator-equivalent metadata for Inspector integration coverage.</summary>
    private sealed class TestObservedScript : SceneScript
    {
        private static readonly ObservedPropertyDescriptor[] Descriptors =
        [
            new(SpeedId, nameof(Speed), ObservedValueKind.Number, ObserveScope.Editor),
            new(RuntimeCounterId, "RuntimeCounter", ObservedValueKind.SignedInteger,
                ObserveScope.Runtime)
        ];
        private double _speed;

        /// <summary>Stable generated property identifier.</summary>
        internal const int SpeedId = 31873;

        /// <summary>Stable runtime-only property identifier.</summary>
        internal const int RuntimeCounterId = 31874;

        /// <summary>Gets generated Editor-facing metadata.</summary>
        public override IReadOnlyList<ObservedPropertyDescriptor> ObservedProperties => Descriptors;

        /// <summary>Gets or sets the observed test speed.</summary>
        internal double Speed
        {
            get => _speed;
            set
            {
                if (_speed == value)
                    return;
                _speed = value;
                NotifyObservedPropertyChanged(SpeedId, ObserveScope.Editor);
            }
        }

        /// <inheritdoc/>
        public override bool TryGetObservedValue(int propertyId, out ObservedValue value)
        {
            if (propertyId == SpeedId)
            {
                value = ObservedValue.From(Speed);
                return true;
            }
            return base.TryGetObservedValue(propertyId, out value);
        }

        /// <inheritdoc/>
        public override bool TrySetObservedValue(int propertyId, ObservedValue value)
        {
            if (propertyId != SpeedId || !value.TryGetNumber(out var speed))
                return false;
            Speed = speed;
            return true;
        }
    }
}
