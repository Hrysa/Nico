using System.Numerics;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

/// <summary>Exercises the renderer-independent measure and arrange system.</summary>
public sealed class UILayoutTests
{
    /// <summary>Verifies fixed items are allocated before flexible growth.</summary>
    [Fact]
    public void FlexRow_FixedAndGrowingItems_AllocateExpectedBounds()
    {
        var left = new Panel(Color.Red, 100f);
        var center = new Panel(Color.Green) { FlexGrow = 1f };
        var right = new Panel(Color.Blue, 50f);
        var row = UI.Row(Color.Black, left, center, right);

        row.Measure(new Vector2(400f, 200f));
        row.Arrange(Vector2.Zero, new Vector2(400f, 200f));

        Assert.Equal(100f, left.Width);
        Assert.Equal(250f, center.Width);
        Assert.Equal(50f, right.Width);
        Assert.Equal(100f, center.Left);
        Assert.Equal(350f, right.Left);
    }

    /// <summary>Verifies justify-content positions fixed children inside a flex row.</summary>
    [Fact]
    public void FlexRow_JustifyEnd_PacksChildAtTrailingEdge()
    {
        var child = new Panel(Color.Red, 80f, 20f)
        {
            AlignSelf = FlexAlignment.Center
        };
        var row = UI.Row(Color.Black, child);
        row.JustifyContent = FlexJustify.End;

        row.Measure(new Vector2(300f, 100f));
        row.Arrange(Vector2.Zero, new Vector2(300f, 100f));

        Assert.Equal(220f, child.Left);
        Assert.Equal(40f, child.Top);
        Assert.Equal(80f, child.Width);
        Assert.Equal(20f, child.Height);
    }

    /// <summary>Verifies both left-dock panels consume proportional growth after a root resize.</summary>
    [Fact]
    public void EditorLeftDock_Resize_GrowsHierarchyAndFileSystem()
    {
        var view = EditorUI.BuildView(1280f, 720f);
        var hierarchy = view.HierarchyTree;
        var fileSystem = view.FileSystemTree;
        var hierarchyHeight = hierarchy.Height;
        var fileSystemHeight = fileSystem.Height;

        view.Root.Measure(new Vector2(1280f, 920f));
        view.Root.Arrange(Vector2.Zero, new Vector2(1280f, 920f));

        Assert.True(hierarchy.Height > hierarchyHeight);
        Assert.True(fileSystem.Height > fileSystemHeight);
    }

    /// <summary>Verifies content-sized title menus end before the active document title begins.</summary>
    [Fact]
    public void EditorTitleBar_MenuHeaders_DoNotOverlapProjectLabel()
    {
        var view = EditorUI.BuildView(1280f, 720f);

        Assert.Equal(
            view.TitleMenuBar.Headers.Sum(header => header.DesiredSize.X),
            view.TitleMenuBar.Width);
        Assert.Equal(5f, view.ProjectLabel.Left - view.TitleMenuBar.Right);
        Assert.True(view.TitleMenuBar.Headers[0].Width > view.TitleMenuBar.Headers[1].Width);
    }

    /// <summary>Verifies canvas coordinates are owned and updated by the overlay container.</summary>
    [Fact]
    public void Canvas_SetPosition_MovesFloatingChild()
    {
        var canvas = new Canvas { Width = 400f, Height = 300f };
        var menu = new ContextMenu(160f);
        menu.AddItem("Open", () => { });
        canvas.Add(menu, new Vector2(20f, 30f));

        canvas.BuildDrawList();
        Assert.Equal(20f, menu.Left);
        Assert.Equal(30f, menu.Top);

        canvas.SetPosition(menu, new Vector2(80f, 90f));
        canvas.BuildDrawList();
        Assert.Equal(80f, menu.Left);
        Assert.Equal(90f, menu.Top);
    }

    /// <summary>Verifies popup placement remains fully inside the overlay canvas.</summary>
    [Fact]
    public void Canvas_PopupNearEdge_ClampsPlacementToBounds()
    {
        var canvas = new Canvas { Width = 200f, Height = 100f };
        var popup = new Popup(Color.Black, Color.White, 80f, 30f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        canvas.Add(popup, new Vector2(180f, 90f));

        canvas.BuildDrawList();

        Assert.Equal(120f, popup.Left);
        Assert.Equal(70f, popup.Top);
        Assert.Equal(200f, popup.Right);
        Assert.Equal(100f, popup.Bottom);
    }

    /// <summary>Verifies owner-relative placement flips above an obstructed lower edge.</summary>
    [Fact]
    public void Canvas_PlacePopupBelowNearBottom_FlipsAboveOwner()
    {
        var canvas = new Canvas { Width = 200f, Height = 100f };
        var owner = new Panel(Color.Red, 40f, 20f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var popup = new Popup(Color.Black, Color.White, 80f, 30f)
        {
            Owner = owner,
            Placement = PopupPlacement.Below,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        canvas.Add(owner, new Vector2(20f, 75f));
        canvas.Add(popup, Vector2.Zero);
        canvas.BuildDrawList();

        canvas.PlacePopup(popup);
        canvas.BuildDrawList();

        Assert.Equal(PopupPlacement.Above, popup.ActualPlacement);
        Assert.Equal(20f, popup.Left);
        Assert.Equal(45f, popup.Top);
    }

    /// <summary>Verifies popup placement uses monitor-specific logical work-area bounds.</summary>
    [Fact]
    public void Canvas_PlacePopup_UsesProvidedWorkArea()
    {
        var canvas = new Canvas
        {
            Width = 300f,
            Height = 200f,
            PopupWorkAreaProvider = new FixedWorkAreaProvider(
                new UIPopupWorkArea(100f, 20f, 200f, 100f, 1.5f))
        };
        var owner = new Panel(Color.Red, 30f, 20f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var popup = new Popup(Color.Black, Color.White, 80f, 30f)
        {
            Owner = owner,
            Placement = PopupPlacement.Below,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        canvas.Add(owner, new Vector2(160f, 70f));
        canvas.Add(popup, Vector2.Zero);
        canvas.BuildDrawList();

        canvas.PlacePopup(popup);
        canvas.BuildDrawList();

        Assert.Equal(PopupPlacement.Above, popup.ActualPlacement);
        Assert.Equal(120f, popup.Left);
        Assert.Equal(40f, popup.Top);
    }

    /// <summary>Constrains popup placement to the overlay intersection with a safe inset.</summary>
    [Fact]
    public void Canvas_PlacePopup_IntersectsWorkAreaAndAppliesMargin()
    {
        var canvas = new Canvas
        {
            Width = 200f,
            Height = 100f,
            PopupWorkAreaProvider = new FixedWorkAreaProvider(
                new UIPopupWorkArea(-20f, -20f, 400f, 300f))
        };
        var owner = new Panel(Color.Red, 20f, 20f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var popup = new Popup(Color.Black, Color.White, 80f, 30f)
        {
            Owner = owner,
            ConstraintMargin = 8f,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        canvas.Add(owner, new Vector2(180f, 80f));
        canvas.Add(popup, Vector2.Zero);
        canvas.BuildDrawList();

        canvas.PlacePopup(popup);
        canvas.BuildDrawList();

        Assert.Equal(PopupPlacement.Above, popup.ActualPlacement);
        Assert.True(popup.Left >= 8f);
        Assert.True(popup.Right <= 192f);
        Assert.True(popup.Top >= 8f);
        Assert.True(popup.Bottom <= 92f);
    }

    /// <summary>Verifies popup work-area DPI conversion is reversible.</summary>
    [Fact]
    public void PopupWorkArea_DpiConversion_RoundTrips()
    {
        var workArea = new UIPopupWorkArea(0f, 0f, 100f, 100f, 1.5f);
        var logical = new Vector2(20f, 30f);

        var physical = workArea.LogicalToPhysical(logical);

        Assert.Equal(new Vector2(30f, 45f), physical);
        Assert.Equal(logical, workArea.PhysicalToLogical(physical));
    }

    /// <summary>Verifies renderer-level display services adapt without leaking platform types into UI.</summary>
    [Fact]
    public void DisplayPopupWorkAreaProvider_AdaptsGraphicsService()
    {
        var provider = new DisplayPopupWorkAreaProvider(new FixedDisplayService());

        var area = provider.GetWorkArea(new Vector2(10f, 20f));

        Assert.Equal(new UIPopupWorkArea(5f, 6f, 105f, 106f, 1.25f), area);
    }

    /// <summary>Returns one deterministic popup work area for placement tests.</summary>
    private sealed class FixedWorkAreaProvider : IPopupWorkAreaProvider
    {
        private readonly UIPopupWorkArea _workArea;

        /// <summary>Creates a fixed provider.</summary>
        /// <param name="workArea">Area returned for every anchor.</param>
        public FixedWorkAreaProvider(UIPopupWorkArea workArea) => _workArea = workArea;

        /// <inheritdoc/>
        public UIPopupWorkArea GetWorkArea(Vector2 anchorPoint) => _workArea;
    }

    /// <summary>Returns deterministic renderer-level display data.</summary>
    private sealed class FixedDisplayService : IDisplayService
    {
        /// <inheritdoc/>
        public DisplayWorkArea GetWorkArea(Vector2 clientAnchor) =>
            new(5f, 6f, 105f, 106f, 1.25f);
    }

    /// <summary>Verifies root resize preserves the existing editor element instances.</summary>
    [Fact]
    public void EditorRoot_Resize_ReusesElementTree()
    {
        var view = EditorUI.BuildView(1280f, 720f);
        var hierarchy = view.HierarchyTree;
        var sceneViewport = view.SceneViewport;

        view.Root.Measure(new Vector2(1440f, 900f));
        view.Root.Arrange(Vector2.Zero, new Vector2(1440f, 900f));

        Assert.Same(hierarchy, view.HierarchyTree);
        Assert.Same(sceneViewport, view.SceneViewport);
        Assert.Equal(1440f, view.Root.Width);
        Assert.Equal(900f, view.Root.Height);
    }

    /// <summary>Verifies the Profiler is retained as the initially inactive Game-well tab.</summary>
    [Fact]
    public void EditorBottomDock_ProfilerStartsCollapsed()
    {
        var view = EditorUI.BuildView(1280f, 720f);

        Assert.False(view.ProfilerContent.IsVisible);
        Assert.Equal("HierarchyButton", view.HierarchyButton.Name);
        Assert.Equal("FileSystemButton", view.FileSystemButton.Name);
        Assert.Equal("SceneButton", view.SceneButton.Name);
        Assert.Equal("GameButton", view.GameButton.Name);
        Assert.Equal("InspectorButton", view.InspectorButton.Name);
        Assert.Equal("ProfilerButton", view.ProfilerButton.Name);
        Assert.True(view.Profiler.IsPaused);
        Assert.Equal("Record", view.ProfilerPauseLabel.Text);
        Assert.Contains(view.Profiler, Descendants(view.ProfilerContent));
        Assert.Contains(view.ProfilerPauseButton, Descendants(view.ProfilerContent));
    }

    /// <summary>Verifies unchanged draw-list builds reuse cached measurement and arrangement.</summary>
    [Fact]
    public void BuildDrawList_UnchangedLayout_DoesNotRerunMeasure()
    {
        var element = new MeasureProbe(100f, 50f);

        element.BuildDrawList();
        element.BuildDrawList();

        Assert.Equal(1, element.MeasureCount);
    }

    /// <summary>Verifies repeated invalid base layout traversal does not allocate after warmup.</summary>
    [Fact]
    public void BaseLayout_RepeatedMeasure_DoesNotAllocate()
    {
        var root = new UIElement();
        for (var index = 0; index < 20; index++)
            root.AddChild(new UIElement(10f, 10f));
        var available = new Vector2(200f, 100f);
        root.Measure(available);
        root.InvalidateMeasure();
        root.Measure(available);
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 100; index++)
        {
            root.InvalidateMeasure();
            root.Measure(available);
        }

        Assert.Equal(allocationStart, GC.GetAllocatedBytesForCurrentThread());
    }

    /// <summary>Verifies dock content can leave and return to the same flex instance.</summary>
    [Fact]
    public void Flex_RemoveAndAdd_ReparentsDockContentCleanly()
    {
        var content = new Panel(Color.Red);
        var flex = UI.Column(Color.Black, content.Grow());

        Assert.True(flex.RemoveChild(content));
        Assert.Null(content.Parent);
        Assert.Empty(flex.Children);

        flex.AddChild(content);
        flex.Measure(new Vector2(320f, 200f));
        flex.Arrange(Vector2.Zero, new Vector2(320f, 200f));
        Assert.Same(flex, content.Parent);
        Assert.Equal(320f, content.Width);
        Assert.Equal(200f, content.Height);
    }

    /// <summary>Verifies auto-sized flex containers derive dimensions from their content.</summary>
    [Fact]
    public void FlexRow_AutoSize_MatchesContentAndGap()
    {
        var row = UI.Row(null,
            new Panel(Color.Red, 30f, 10f),
            new Panel(Color.Blue, 50f, 20f));
        row.Gap = 6f;
        row.HorizontalAlignment = HorizontalAlignment.Left;
        row.VerticalAlignment = VerticalAlignment.Top;

        row.Measure(new Vector2(500f, 500f));
        row.Arrange(Vector2.Zero, row.DesiredSize);

        Assert.Equal(new Vector2(86f, 20f), row.DesiredSize);
        Assert.Equal(86f, row.Width);
        Assert.Equal(20f, row.Height);
    }

    /// <summary>Verifies wrapped flex lines move overflowing items onto the next line.</summary>
    [Fact]
    public void FlexRow_Wrap_CreatesAdditionalLine()
    {
        var first = new Panel(Color.Red, 60f, 10f);
        var second = new Panel(Color.Green, 60f, 10f);
        var third = new Panel(Color.Blue, 60f, 10f);
        var row = UI.Row(null, first, second, third);
        row.Wrap = FlexWrap.Wrap;
        row.Gap = 4f;

        row.Measure(new Vector2(130f, 100f));
        row.Arrange(Vector2.Zero, new Vector2(130f, 100f));

        Assert.Equal(0f, first.Top);
        Assert.Equal(0f, second.Top);
        Assert.Equal(14f, third.Top);
    }

    /// <summary>Verifies a requested width acts as the basis before flex growth.</summary>
    [Fact]
    public void FlexRow_ExplicitBasisAndGrow_ExpandsActualItemBox()
    {
        var growing = new Panel(Color.Red, 100f, 20f) { FlexGrow = 1f };
        var fixedItem = new Panel(Color.Blue, 50f, 20f);
        var row = UI.Row(null, growing, fixedItem);

        row.Measure(new Vector2(300f, 20f));
        row.Arrange(Vector2.Zero, new Vector2(300f, 20f));

        Assert.Equal(250f, growing.Width);
        Assert.Equal(250f, fixedItem.Left);
    }

    /// <summary>Verifies maximum size freezes one item and redistributes remaining growth.</summary>
    [Fact]
    public void FlexRow_MaxWidth_RedistributesGrowth()
    {
        var capped = new Panel(Color.Red, 50f, 20f) { FlexGrow = 1f, MaxWidth = 80f };
        var growing = new Panel(Color.Blue, 50f, 20f) { FlexGrow = 1f };
        var row = UI.Row(null, capped, growing);

        row.Measure(new Vector2(300f, 20f));
        row.Arrange(Vector2.Zero, new Vector2(300f, 20f));

        Assert.Equal(80f, capped.Width);
        Assert.Equal(220f, growing.Width);
    }

    /// <summary>Verifies space-between distributes free space between fixed items.</summary>
    [Fact]
    public void FlexRow_SpaceBetween_DistributesFreeSpace()
    {
        var first = new Panel(Color.Red, 20f, 10f);
        var second = new Panel(Color.Green, 20f, 10f);
        var third = new Panel(Color.Blue, 20f, 10f);
        var row = UI.Row(null, first, second, third);
        row.JustifyContent = FlexJustify.SpaceBetween;

        row.Measure(new Vector2(160f, 20f));
        row.Arrange(Vector2.Zero, new Vector2(160f, 20f));

        Assert.Equal(0f, first.Left);
        Assert.Equal(70f, second.Left);
        Assert.Equal(140f, third.Left);
    }

    /// <summary>Verifies a vertical flex container allocates remaining height to a growing child.</summary>
    [Fact]
    public void FlexColumn_Grow_AllocatesRemainingHeight()
    {
        var header = new Panel(Color.Red, 100f, 30f) { FlexShrink = 0f };
        var content = new Panel(Color.Blue) { FlexGrow = 1f };
        var column = UI.Column(null, header, content);

        column.Measure(new Vector2(100f, 200f));
        column.Arrange(Vector2.Zero, new Vector2(100f, 200f));

        Assert.Equal(30f, header.Height);
        Assert.Equal(30f, content.Top);
        Assert.Equal(170f, content.Height);
    }

    /// <summary>Verifies declarative overlay composition retains layer order and shared bounds.</summary>
    [Fact]
    public void DeclarativeOverlay_AttachesAndLayersChildren()
    {
        var back = new Panel(Color.Red);
        var front = new Panel(Color.Blue);
        var overlay = UI.Overlay(
        [
            UI.Ref(back, out var capturedBack),
            front.Named("FrontLayer")
        ]).Named("RootOverlay");

        overlay.Measure(new Vector2(120f, 80f));
        overlay.Arrange(Vector2.Zero, new Vector2(120f, 80f));

        Assert.Same(overlay, back.Parent);
        Assert.Same(overlay, front.Parent);
        Assert.Same(back, capturedBack);
        Assert.Equal("RootOverlay", overlay.Name);
        Assert.Equal("FrontLayer", front.Name);
        Assert.Same(back, overlay.Children[0]);
        Assert.Same(front, overlay.Children[1]);
        Assert.Equal(120f, back.Width);
        Assert.Equal(80f, front.Height);
    }

    /// <summary>Verifies repeated invalid flex layout is allocation-free after buffer warmup.</summary>
    [Fact]
    public void FlexRow_RepeatedLayout_DoesNotAllocate()
    {
        var row = UI.Row(null,
            new Panel(Color.Red, 20f, 10f),
            new Panel(Color.Green, 20f, 10f) { FlexGrow = 1f },
            new Panel(Color.Blue, 20f, 10f));
        var available = new Vector2(200f, 40f);
        row.Measure(available);
        row.Arrange(Vector2.Zero, available);
        row.InvalidateMeasure();
        row.Measure(available);
        row.Arrange(Vector2.Zero, available);
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 100; index++)
        {
            row.InvalidateMeasure();
            row.Measure(available);
            row.Arrange(Vector2.Zero, available);
        }

        Assert.Equal(allocationStart, GC.GetAllocatedBytesForCurrentThread());
    }

    /// <summary>Enumerates all UI descendants beneath one root.</summary>
    /// <param name="root">Subtree root.</param>
    /// <returns>Descendants in depth-first order.</returns>
    private static IEnumerable<UIElement> Descendants(UIElement root)
    {
        foreach (var child in root.Children.OfType<UIElement>())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    /// <summary>Counts measure override calls for cache verification.</summary>
    private sealed class MeasureProbe : UIElement
    {
        /// <summary>Gets the number of measurement executions.</summary>
        public int MeasureCount { get; private set; }

        /// <summary>Creates a fixed-size measurement probe.</summary>
        /// <param name="width">Probe width.</param>
        /// <param name="height">Probe height.</param>
        public MeasureProbe(float width, float height) : base(width, height)
        {
        }

        /// <inheritdoc/>
        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            MeasureCount++;
            return base.MeasureOverride(availableSize);
        }
    }
}
