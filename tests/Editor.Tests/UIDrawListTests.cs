using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class UIDrawListTests
{
    /// <summary>Verifies unchanged retained UI reuses its paint snapshot.</summary>
    [Fact]
    public void BuildDrawList_UnchangedTree_ReusesSnapshotUntilVisualChanges()
    {
        var root = new Panel(Color.Black, 200f, 40f);
        var label = new Label("Before", 100f, 40f);
        root.AddChild(label);

        var first = root.BuildDrawList();
        var unchanged = root.BuildDrawList();
        var firstGeneration = first.Generation;
        label.Text = "After";
        var changed = root.BuildDrawList();

        Assert.Same(first, unchanged);
        Assert.Same(first, changed);
        Assert.True(changed.Generation > firstGeneration);
        Assert.Contains(changed.Commands, command => command.Text == "After");
    }

    /// <summary>Verifies a visual change reuses unaffected siblings' cached paint commands.</summary>
    [Fact]
    public void BuildDrawList_ChangedChild_DoesNotRepaintSibling()
    {
        var root = new Panel(Color.Black, 200f, 40f);
        var changed = new CountingElement();
        var sibling = new CountingElement();
        root.AddChild(changed);
        root.AddChild(sibling);
        root.BuildDrawList();

        changed.InvalidateVisual();
        root.BuildDrawList();

        Assert.Equal(2, changed.PaintCount);
        Assert.Equal(1, sibling.PaintCount);
    }

    /// <summary>Verifies repeated retained-tree composition reuses command storage.</summary>
    [Fact]
    public void BuildDrawList_RepeatedVisualChanges_DoNotAllocateAfterWarmup()
    {
        var root = new Panel(Color.Black, 500f, 500f);
        for (var index = 0; index < 100; index++)
            root.AddChild(new Panel(Color.Gray, 10f, 10f));
        root.BuildDrawList();
        root.BackgroundColor = Color.White;
        root.BuildDrawList();
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 100; index++)
        {
            root.BackgroundColor = (index & 1) == 0 ? Color.Black : Color.White;
            root.BuildDrawList();
        }

        var allocationEnd = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(allocationStart, allocationEnd);
    }

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

    /// <summary>Verifies retained snapshots carry monotonic identities and cached trees reuse them.</summary>
    [Fact]
    public void BuildDrawList_Snapshots_HaveMonotonicGenerations()
    {
        var root = new Panel(Color.Black, 100f, 100f);
        var first = root.BuildDrawList();
        var firstGeneration = first.Generation;
        var unchanged = root.BuildDrawList();
        var unchangedGeneration = unchanged.Generation;
        root.BackgroundColor = Color.White;
        var changed = root.BuildDrawList();

        Assert.Same(first, unchanged);
        Assert.Equal(firstGeneration, unchangedGeneration);
        Assert.Same(first, changed);
        Assert.True(changed.Generation > firstGeneration);
    }

    /// <summary>Counts local paint executions for snapshot tests.</summary>
    private sealed class CountingElement : UIElement
    {
        /// <summary>Gets the number of paint executions.</summary>
        internal int PaintCount { get; private set; }

        /// <inheritdoc/>
        protected override void Paint(UIDrawList drawList)
        {
            PaintCount++;
            base.Paint(drawList);
        }
    }

}
