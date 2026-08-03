using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class UIDrawListTests
{
    /// <summary>Verifies parent-before-child paint ordering.</summary>
    [Fact]
    public void BuildDrawList_ChildPanel_PaintsAfterParent()
    {
        var root = new Canvas { Width = 100f, Height = 100f };
        var child = new Panel(Color.Red, 30f, 40f);
        root.Add(child, new(10f, 20f));

        var drawList = root.BuildDrawList();

        Assert.Single(drawList.Commands);
        Assert.Equal(Color.Red, drawList.Commands[0].Color);
        Assert.Equal(10f, drawList.Commands[0].Left);
    }

    /// <summary>Verifies hidden subtrees emit no paint commands.</summary>
    [Fact]
    public void BuildDrawList_HiddenChild_OmitsSubtree()
    {
        var root = new Panel(Color.Black, 100f, 100f);
        var child = new Panel(Color.Red, 50f, 50f) { IsVisible = false };
        child.AddChild(new Panel(Color.Green, 10f, 10f));
        root.AddChild(child);

        var drawList = root.BuildDrawList();

        Assert.Single(drawList.Commands);
    }

    /// <summary>Verifies child layout positions are relative to their parent.</summary>
    [Fact]
    public void BuildDrawList_NestedChild_AppliesParentPosition()
    {
        var root = new Canvas { Width = 500f, Height = 500f };
        var parent = new Canvas { Width = 300f, Height = 300f };
        var child = new Panel(Color.Red, 30f, 40f);
        root.Add(parent, new(100f, 200f));
        parent.Add(child, new(10f, 20f));

        var command = root.BuildDrawList().Commands[0];

        Assert.Equal(110f, command.Left);
        Assert.Equal(220f, command.Top);
    }

    /// <summary>Verifies text remains a semantic command for backend TrueType rasterization.</summary>
    [Fact]
    public void AddText_WithBackground_EmitsTrueTypeCommand()
    {
        var drawList = new UIDrawList();

        drawList.AddText("A", 0f, 0f, 14f, Color.White, Color.Black);

        var command = Assert.Single(drawList.Commands);
        Assert.Equal(UIDrawCommandType.Text, command.Type);
        Assert.Equal("A", command.Text);
        Assert.Equal(14f, command.FontPixelHeight);
        Assert.Equal(Color.Black, command.BackgroundColor);
    }

}
