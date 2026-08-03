using System.Numerics;
using Editor;
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
        var script = Assert.IsType<TextField>(FindByName<TextField>(inspector, "ScriptTypeField"));

        name.SetFocus(true);
        name.InvokeTextInput('2');
        node.Position = new Vector3(node.Position.X, 8f, node.Position.Z);
        positionX.SetFocus(true);
        positionX.InvokeKeyDown((int)InputKey.Backspace);
        positionX.InvokeTextInput('5');
        script.SetFocus(true);
        script.InvokeTextInput('S');

        Assert.Equal("Cube2", node.Name);
        Assert.Equal(5f, node.Position.X);
        Assert.Equal(8f, node.Position.Y);
        Assert.Equal("S", node.ScriptType);
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
