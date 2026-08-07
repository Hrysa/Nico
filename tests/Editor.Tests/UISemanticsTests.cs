using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class UISemanticsTests
{
    /// <summary>Verifies a full semantic snapshot preserves hierarchy, bounds, labels, and visibility.</summary>
    [Fact]
    public void AccessibilityTree_Capture_ProducesStableVisibleHierarchy()
    {
        var root = new Panel(Color.Black, 300f, 200f) { AutomationId = "root" };
        var label = new Label("Project name", 100f, 20f);
        var field = new TextField(120f, 30f)
        {
            AutomationId = "project-name",
            AccessibilityDescription = "Name used when saving the project",
            LabeledBy = label
        };
        var hidden = new Button(80f, 20f, "Hidden") { IsVisible = false };
        root.AddChild(label);
        root.AddChild(field);
        root.AddChild(hidden);
        root.BuildDrawList();

        var snapshot = UIAccessibilityTree.Capture(root);

        Assert.Equal(3, snapshot.Nodes.Count);
        Assert.Equal("root", snapshot.Root.AutomationId);
        Assert.Equal(1, snapshot.Root.FirstChildIndex);
        Assert.Equal(2, snapshot.GetNode(1).NextSiblingIndex);
        Assert.Equal("Project name", snapshot.GetNode(2).SemanticInfo.Name);
        Assert.Equal("Name used when saving the project",
            snapshot.GetNode(2).SemanticInfo.Description);
        Assert.Equal(snapshot.GetNode(1).Id, snapshot.GetNode(2).LabeledById);
        Assert.True(snapshot.TryGetNode(snapshot.GetNode(2).Id, out var resolved));
        Assert.Same(snapshot.GetNode(2), resolved);
    }

    /// <summary>Verifies button semantics expose a useful name and invokable action.</summary>
    [Fact]
    public void Button_InvokeSemanticAction_RaisesClick()
    {
        var button = new Button(100f, 30f, "Save");
        var clicks = 0;
        button.Click += () => clicks++;

        var semantic = button.GetSemanticInfo();
        var performed = button.PerformSemanticAction(UISemanticAction.Invoke);

        Assert.Equal(UISemanticRole.Button, semantic.Role);
        Assert.Equal("Save", semantic.Name);
        Assert.Equal(UISemanticAction.Invoke, semantic.Actions);
        Assert.True(performed);
        Assert.Equal(1, clicks);
    }

    /// <summary>Verifies boolean controls expose and change checked state through semantics.</summary>
    [Fact]
    public void CheckBox_ToggleSemanticAction_ChangesCheckedState()
    {
        var checkBox = new CheckBox(120f, 30f, "Lighting");

        Assert.True(checkBox.PerformSemanticAction(UISemanticAction.Toggle));
        var semantic = checkBox.GetSemanticInfo();

        Assert.Equal(UISemanticRole.CheckBox, semantic.Role);
        Assert.True(semantic.IsChecked);
        Assert.True(semantic.Actions.HasFlag(UISemanticAction.Toggle));
    }

    /// <summary>Verifies range semantics report bounds and clamp adapter value requests.</summary>
    [Fact]
    public void Slider_SemanticActions_ReportAndChangeRangeValue()
    {
        var slider = new Slider(UIOrientation.Horizontal, 100f, 20f)
        {
            Minimum = 10f,
            Maximum = 20f,
            Value = 12f,
            SmallChange = 2f
        };

        Assert.True(slider.PerformSemanticAction(UISemanticAction.Increment));
        Assert.True(slider.PerformSemanticAction(UISemanticAction.SetValue, 100d));
        var semantic = slider.GetSemanticInfo();

        Assert.Equal(UISemanticRole.Slider, semantic.Role);
        Assert.Equal(20d, semantic.NumericValue);
        Assert.Equal(10d, semantic.Minimum);
        Assert.Equal(20d, semantic.Maximum);
    }

    /// <summary>Verifies combo semantics expand and select choices without pointer input.</summary>
    [Fact]
    public void ComboBox_SemanticActions_ExpandAndSelect()
    {
        var comboBox = new ComboBox(120f, 30f);
        comboBox.SetItems(["Low", "Medium", "High"]);

        Assert.True(comboBox.PerformSemanticAction(UISemanticAction.ExpandCollapse));
        Assert.True(comboBox.PerformSemanticAction(UISemanticAction.SetValue, 2d));
        var semantic = comboBox.GetSemanticInfo();

        Assert.True(semantic.IsExpanded);
        Assert.Equal("High", semantic.Value);
        Assert.Equal(2d, semantic.NumericValue);
    }

    /// <summary>Verifies recycled list rows expose selection and activation independently.</summary>
    [Fact]
    public void ListViewItem_SemanticActions_SelectAndActivateLogicalItem()
    {
        var list = new ListView(200f, 80f);
        list.SetItems(["One", "Two"]);
        var activated = string.Empty;
        list.ItemActivated += (_, item) => activated = item;
        list.BuildDrawList();
        var second = Assert.IsType<ListViewItem>(list.Children[1]);

        Assert.True(second.PerformSemanticAction(UISemanticAction.Select));
        Assert.True(second.PerformSemanticAction(UISemanticAction.Invoke));
        var semantic = second.GetSemanticInfo();

        Assert.Equal(UISemanticRole.ListItem, semantic.Role);
        Assert.True(semantic.IsSelected);
        Assert.Equal("Two", activated);
    }

    /// <summary>Verifies disabled controls reject accessibility actions consistently with input.</summary>
    [Fact]
    public void DisabledControl_SemanticAction_IsRejected()
    {
        var button = new Button(100f, 30f, "Delete") { IsEnabled = false };

        Assert.False(button.PerformSemanticAction(UISemanticAction.Invoke));
    }
}
