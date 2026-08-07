using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class UIDrawListTests
{
    /// <summary>Verifies opacity multiplies through retained parent and child subtrees.</summary>
    [Fact]
    public void BuildDrawList_MultipliesInheritedOpacity()
    {
        var root = new Panel(Color.Black, 100f, 100f) { Opacity = 0.5f };
        root.AddChild(new Panel(Color.White, 50f, 50f) { Opacity = 0.4f });

        var drawList = root.BuildDrawList();

        Assert.Equal(0.5f, drawList.Commands[0].Opacity);
        Assert.Equal(0.2f, drawList.Commands[1].Opacity, 5);
    }

    /// <summary>Verifies opacity rejects values outside the compositing range.</summary>
    [Fact]
    public void Opacity_OutsideUnitRange_Throws()
    {
        var panel = new Panel(Color.Black, 10f, 10f);

        Assert.Throws<ArgumentOutOfRangeException>(() => panel.Opacity = -0.1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => panel.Opacity = 1.1f);
    }

    /// <summary>Verifies sampled images retain texture identity, bounds, layer, and clipping.</summary>
    [Fact]
    public void AddImage_StoresSemanticTextureCommand()
    {
        var drawList = new UIDrawList
        {
            CurrentLayer = UIDrawLayer.Overlay,
            CurrentClip = new UIClipRect(0f, 0f, 50f, 50f)
        };
        var texture = new TextureHandle(42);

        drawList.AddImage(texture, 1f, 2f, 31f, 42f);

        var command = Assert.Single(drawList.Commands);
        Assert.Equal(UIDrawCommandType.Image, command.Type);
        Assert.Equal(texture, command.Texture);
        Assert.Equal(UIDrawLayer.Overlay, command.Layer);
        Assert.Equal(new UIClipRect(0f, 0f, 50f, 50f), command.Clip);
    }

    /// <summary>Verifies image controls fill their padded content rectangle.</summary>
    [Fact]
    public void Image_PaintsTextureInsidePadding()
    {
        var image = new Engine.UI.Image(new TextureHandle(7), 100f, 60f)
        {
            Padding = new Thickness(5f, 6f, 7f, 8f)
        };

        var command = Assert.Single(image.BuildDrawList().Commands);

        Assert.Equal(5f, command.Left);
        Assert.Equal(6f, command.Top);
        Assert.Equal(93f, command.Right);
        Assert.Equal(52f, command.Bottom);
    }

    /// <summary>Verifies uniform image stretch letterboxes while preserving aspect ratio.</summary>
    [Fact]
    public void Image_UniformStretch_CentersAspectFit()
    {
        var image = new Engine.UI.Image(new TextureHandle(7), 100f, 100f)
        {
            SourceSize = new System.Numerics.Vector2(200f, 100f),
            Stretch = ImageStretch.Uniform
        };

        var command = Assert.Single(image.BuildDrawList().Commands);

        Assert.Equal(0f, command.Left);
        Assert.Equal(25f, command.Top);
        Assert.Equal(100f, command.Right);
        Assert.Equal(75f, command.Bottom);
    }

    /// <summary>Verifies uniform-to-fill image stretch centers overflow for clipping.</summary>
    [Fact]
    public void Image_UniformToFill_CentersAspectCrop()
    {
        var image = new Engine.UI.Image(new TextureHandle(7), 100f, 100f)
        {
            SourceSize = new System.Numerics.Vector2(200f, 100f),
            Stretch = ImageStretch.UniformToFill
        };

        var command = Assert.Single(image.BuildDrawList().Commands);

        Assert.Equal(-50f, command.Left);
        Assert.Equal(0f, command.Top);
        Assert.Equal(150f, command.Right);
        Assert.Equal(100f, command.Bottom);
        Assert.Equal(new UIClipRect(0f, 0f, 100f, 100f), command.Clip);
    }

    /// <summary>Verifies stroked lines retain semantic endpoints, thickness, and clipping.</summary>
    [Fact]
    public void AddLine_StoresSemanticStrokeCommand()
    {
        var drawList = new UIDrawList
        {
            CurrentClip = new UIClipRect(0f, 0f, 50f, 50f)
        };

        drawList.AddLine(1f, 2f, 30f, 40f, 3f, Color.Red);

        var command = Assert.Single(drawList.Commands);
        Assert.Equal(UIDrawCommandType.Line, command.Type);
        Assert.Equal(3f, command.StrokeWidth);
        Assert.Equal(new UIClipRect(0f, 0f, 50f, 50f), command.Clip);
    }

    /// <summary>Verifies clipping ancestors attach intersected clips to descendant commands.</summary>
    [Fact]
    public void BuildDrawList_ClippingAncestors_IntersectDescendantClip()
    {
        var root = new Canvas { Width = 100f, Height = 100f, ClipToBounds = true };
        var parent = new Canvas
        {
            Width = 60f,
            Height = 60f,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var child = new Panel(Color.Red, 50f, 50f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        root.Add(parent, new(50f, 20f));
        parent.Add(child, new(30f, 20f));

        var command = Assert.Single(root.BuildDrawList().Commands);

        Assert.Equal(new UIClipRect(50f, 20f, 100f, 80f), command.Clip);
        Assert.Equal(80f, command.Left);
        Assert.Equal(130f, command.Right);
    }

    /// <summary>Verifies changing only inherited clipping rebuilds composition without repainting children.</summary>
    [Fact]
    public void BuildDrawList_ClipChange_ReusesChildPaintCache()
    {
        var root = new Panel(Color.Black, 100f, 100f);
        var child = new CountingElement();
        root.AddChild(child);
        root.BuildDrawList();

        root.ClipToBounds = true;
        var clipped = root.BuildDrawList();

        Assert.Equal(1, child.PaintCount);
        Assert.All(clipped.Commands, command => Assert.Equal(
            new UIClipRect(0f, 0f, 100f, 100f), command.Clip));
    }

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

    /// <summary>Verifies unchanged retained composition returns its cached snapshot without allocation.</summary>
    [Fact]
    public void BuildDrawList_UnchangedCachedSnapshot_DoesNotAllocate()
    {
        var root = new Panel(Color.Black, 500f, 500f);
        for (var index = 0; index < 100; index++)
            root.AddChild(new Panel(Color.Gray, 10f, 10f));
        root.BuildDrawList();
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 1_000; index++)
            root.BuildDrawList();

        Assert.Equal(allocationStart, GC.GetAllocatedBytesForCurrentThread());
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
