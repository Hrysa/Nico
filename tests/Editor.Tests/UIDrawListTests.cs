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
        var root = new Panel(0f, 0f, 100f, 100f, Color.Black);
        var child = new Panel(10f, 20f, 30f, 40f, Color.Red);
        root.AddChild(child);

        var drawList = root.BuildDrawList();

        Assert.Equal(2, drawList.Commands.Count);
        Assert.Equal(Color.Black, drawList.Commands[0].Color);
        Assert.Equal(Color.Red, drawList.Commands[1].Color);
        Assert.Equal(10f, drawList.Commands[1].Left);
    }

    /// <summary>Verifies hidden subtrees emit no paint commands.</summary>
    [Fact]
    public void BuildDrawList_HiddenChild_OmitsSubtree()
    {
        var root = new Panel(0f, 0f, 100f, 100f, Color.Black);
        var child = new Panel(0f, 0f, 50f, 50f, Color.Red) { IsVisible = false };
        child.AddChild(new Panel(0f, 0f, 10f, 10f, Color.Green));
        root.AddChild(child);

        var drawList = root.BuildDrawList();

        Assert.Single(drawList.Commands);
    }

    /// <summary>Verifies child layout positions are relative to their parent.</summary>
    [Fact]
    public void BuildDrawList_NestedChild_AppliesParentPosition()
    {
        var root = new Panel(100f, 200f, 300f, 300f, Color.Black);
        var child = new Panel(10f, 20f, 30f, 40f, Color.Red);
        root.AddChild(child);

        var command = root.BuildDrawList().Commands[1];

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
