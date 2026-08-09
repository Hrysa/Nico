using Engine.UI;
using Xunit;

namespace Editor.Tests;

/// <summary>Verifies docking model mutation and safe workspace persistence.</summary>
public sealed class DockWorkspaceTests
{
    /// <summary>Verifies transparent tab chrome remains distinct from surfaced content.</summary>
    [Fact]
    public void DockHost_TabChromeTransparent_ContentUsesSurface()
    {
        var theme = UITheme.HighContrast;
        var group = new DockTabGroup([new DockTab("scene", "Scene")], "scene");
        var host = new DockHost(
            new DockWorkspace { Root = group }, _ => new UIElement(), theme);
        var presenter = Assert.IsAssignableFrom<Box>(Assert.Single(host.VisualChildren));
        var tabs = Assert.IsType<TabControl>(presenter.VisualChildren[0]);
        var content = Assert.Single(tabs.VisualChildren.OfType<ScrollViewer>());
        var headerStrip = Assert.IsType<FlexPanel>(tabs.VisualChildren[0]);
        var header = Assert.IsType<ToggleButton>(headerStrip.VisualChildren[0]);

        Assert.False(host.PaintBackground);
        Assert.Equal(new Thickness(4f), host.Margin);
        Assert.Equal(Thickness.Zero, host.Padding);
        Assert.False(presenter.PaintBackground);
        Assert.False(tabs.PaintBackground);
        Assert.True(content.PaintBackground);
        Assert.Equal(theme.Surface, content.BackgroundColor);
        Assert.Equal(new Thickness(3f, 5f, 3f, 5f), content.Padding);
        Assert.Equal(theme.PanelCornerRadius, content.CornerRadius);
        Assert.Equal(BoxCornerMode.TopRight | BoxCornerMode.Bottom, content.CornerMode);
        Assert.Equal(theme.PanelCornerRadius, header.CornerRadius);
        Assert.Equal(BoxCornerMode.Top, header.CornerMode);
        Assert.Equal(theme.PanelCornerRadius, presenter.CornerRadius);
    }

    /// <summary>Verifies dock margin and splitter thickness participate in pane layout.</summary>
    [Fact]
    public void DockHost_SplitLayout_UsesFourPixelMarginAndSplitter()
    {
        var split = new DockSplit(
            DockSplitOrientation.Horizontal,
            new DockTabGroup([new DockTab("left", "Left")]),
            new DockTabGroup([new DockTab("right", "Right")]),
            0.5f);
        var host = new DockHost(
            new DockWorkspace { Root = split }, _ => new UIElement())
        {
            Width = 400f,
            Height = 200f
        };

        host.BuildDrawList();

        var splitPresenter = Assert.IsAssignableFrom<UIElement>(Assert.Single(host.VisualChildren));
        var first = Assert.IsAssignableFrom<UIElement>(splitPresenter.VisualChildren[0]);
        var second = Assert.IsAssignableFrom<UIElement>(splitPresenter.VisualChildren[1]);
        var splitter = Assert.IsAssignableFrom<UIElement>(splitPresenter.VisualChildren[2]);
        Assert.Equal(4f, splitPresenter.Left);
        Assert.Equal(4f, splitPresenter.Top);
        Assert.Equal(392f, splitPresenter.Width);
        Assert.Equal(192f, splitPresenter.Height);
        Assert.Equal(198f, first.Right);
        Assert.Equal(4f, splitter.Width);
        Assert.Equal(198f, splitter.Left);
        Assert.Equal(202f, second.Left);
        Assert.Equal(396f, second.Right);
    }

    /// <summary>Verifies dock content exceeding its pane receives a visible vertical scroll bar.</summary>
    [Fact]
    public void DockHost_OverflowingContent_ShowsVerticalScrollBar()
    {
        var group = new DockTabGroup([new DockTab("inspector", "Inspector")], "inspector");
        var content = new Panel(Engine.Graphics.Color.Red, 100f, 300f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var host = new DockHost(
            new DockWorkspace { Root = group }, _ => content)
        {
            Width = 200f,
            Height = 120f
        };

        host.BuildDrawList();

        var presenter = Assert.IsAssignableFrom<UIElement>(Assert.Single(host.VisualChildren));
        var tabs = Assert.IsType<TabControl>(presenter.VisualChildren[0]);
        var scroller = Assert.Single(tabs.VisualChildren.OfType<ScrollViewer>());
        Assert.True(scroller.VerticalScrollBar.IsVisible);
        Assert.Equal(300f, scroller.ExtentHeight);
    }

    /// <summary>Verifies the five overlay targets map pointer centers to their zones.</summary>
    [Fact]
    public void DockDropOverlay_TargetCenters_HitEveryZone()
    {
        var overlay = new DockDropOverlay();
        overlay.Show(new Engine.Graphics.UIClipRect(0f, 0f, 300f, 300f));
        var points = new[]
        {
            new System.Numerics.Vector2(150f, 150f),
            new System.Numerics.Vector2(98f, 150f),
            new System.Numerics.Vector2(202f, 150f),
            new System.Numerics.Vector2(150f, 98f),
            new System.Numerics.Vector2(150f, 202f)
        };
        var zones = new[]
        {
            DockDropZone.Center,
            DockDropZone.Left,
            DockDropZone.Right,
            DockDropZone.Top,
            DockDropZone.Bottom
        };

        for (var index = 0; index < zones.Length; index++)
            Assert.Equal(zones[index], overlay.UpdatePointer(points[index]));
    }

    /// <summary>Verifies active edge targets emit a bounded pane preview.</summary>
    [Fact]
    public void DockDropOverlay_ActiveLeft_PaintsPreviewAndTargets()
    {
        var overlay = new DockDropOverlay { Width = 300f, Height = 300f };
        overlay.Show(new Engine.Graphics.UIClipRect(0f, 0f, 300f, 300f));
        overlay.UpdatePointer(new System.Numerics.Vector2(98f, 150f));

        var commands = overlay.BuildDrawList().Commands;
        var preview = overlay.GetPreviewBounds(DockDropZone.Left);

        Assert.Equal(90f, preview.Right);
        Assert.True(commands.Count > 5);
        Assert.Equal(90f, commands[0].Right);
        Assert.Contains(commands, command => command.Color == Engine.Graphics.Color.White);
    }

    /// <summary>Verifies tab insertion mode paints a narrow white header marker.</summary>
    [Fact]
    public void DockDropOverlay_TabInsertion_PaintsMarkerOnly()
    {
        var overlay = new DockDropOverlay { Width = 300f, Height = 30f };
        overlay.ShowTabInsertion(new Engine.Graphics.UIClipRect(0f, 0f, 300f, 30f), 100f);

        var commands = overlay.BuildDrawList().Commands;

        Assert.True(overlay.IsTabInsertion);
        Assert.Single(commands);
        Assert.Equal(99f, commands[0].Left);
        Assert.Equal(101f, commands[0].Right);
        Assert.Equal(Engine.Graphics.Color.White, commands[0].Color);
    }

    /// <summary>Verifies an edge drop inserts a correctly oriented nested split.</summary>
    [Theory]
    [InlineData(DockDropZone.Left, DockSplitOrientation.Horizontal, true)]
    [InlineData(DockDropZone.Right, DockSplitOrientation.Horizontal, false)]
    [InlineData(DockDropZone.Top, DockSplitOrientation.Vertical, true)]
    [InlineData(DockDropZone.Bottom, DockSplitOrientation.Vertical, false)]
    public void DockTab_EdgeZone_InsertsSplit(
        DockDropZone zone,
        DockSplitOrientation orientation,
        bool newPaneFirst)
    {
        var source = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("game", "Game")
        ]);
        var target = new DockTabGroup([new DockTab("inspector", "Inspector")]);
        var workspace = new DockWorkspace
        {
            Root = new DockSplit(DockSplitOrientation.Horizontal, source, target)
        };

        Assert.True(workspace.DockTab("game", target, zone, 0.25f));

        var outer = Assert.IsType<DockSplit>(workspace.Root);
        var inserted = Assert.IsType<DockSplit>(outer.Second);
        Assert.Equal(orientation, inserted.Orientation);
        Assert.Equal(newPaneFirst ? 0.25f : 0.75f, inserted.Ratio);
        var newPane = Assert.IsType<DockTabGroup>(newPaneFirst ? inserted.First : inserted.Second);
        Assert.Equal("game", newPane.SelectedId);
        Assert.Equal(["scene"], source.Tabs.Select(tab => tab.Id));
    }

    /// <summary>Verifies a sole tab cannot split its own target into duplicate panes.</summary>
    [Fact]
    public void DockTab_SoleTabOntoOwnEdge_IsRejected()
    {
        var group = new DockTabGroup([new DockTab("scene", "Scene")]);
        var workspace = new DockWorkspace { Root = group };

        Assert.False(workspace.DockTab("scene", group, DockDropZone.Left));
        Assert.Same(group, workspace.Root);
    }

    /// <summary>Verifies an edge drop can target a group owned by a floating root.</summary>
    [Fact]
    public void DockTab_FloatingTarget_InsertsIntoAuthoritativeWorkspace()
    {
        var main = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("game", "Game")
        ]);
        var floatingTarget = new DockTabGroup([new DockTab("inspector", "Inspector")]);
        var floating = new FloatingDockRoot
        {
            Id = "tools",
            Root = floatingTarget
        };
        var workspace = new DockWorkspace
        {
            Root = main,
            FloatingRoots = [floating]
        };

        Assert.True(workspace.DockTab("game", floatingTarget, DockDropZone.Left));

        Assert.Equal(["scene"], main.Tabs.Select(tab => tab.Id));
        var split = Assert.IsType<DockSplit>(floating.Root);
        Assert.Equal(DockSplitOrientation.Horizontal, split.Orientation);
        var inserted = Assert.IsType<DockTabGroup>(split.First);
        Assert.Equal(["game"], inserted.Tabs.Select(tab => tab.Id));
        Assert.Same(floatingTarget, split.Second);
    }

    /// <summary>Verifies moving the final floating tab into main removes its empty floating root.</summary>
    [Fact]
    public void MoveTab_FromFloatingToMain_RemovesEmptyFloatingRoot()
    {
        var main = new DockTabGroup([new DockTab("scene", "Scene")]);
        var floating = new FloatingDockRoot
        {
            Id = "profiler-window",
            Root = new DockTabGroup([new DockTab("profiler", "Profiler")])
        };
        var workspace = new DockWorkspace
        {
            Root = main,
            FloatingRoots = [floating]
        };

        Assert.True(workspace.MoveTab("profiler", main));

        Assert.Empty(workspace.FloatingRoots);
        Assert.Equal(["scene", "profiler"], main.Tabs.Select(tab => tab.Id));
    }

    /// <summary>Verifies stable-ID activation finds tabs in nested and floating groups.</summary>
    [Fact]
    public void SelectTab_NestedWorkspace_UpdatesSelectedState()
    {
        var tools = new DockTabGroup([
            new DockTab("game", "Game"),
            new DockTab("profiler", "Profiler")
        ], "game");
        var workspace = new DockWorkspace
        {
            Root = new DockSplit(
                DockSplitOrientation.Horizontal,
                new DockTabGroup([new DockTab("scene", "Scene")]),
                tools)
        };

        Assert.False(workspace.IsTabSelected("profiler"));
        Assert.True(workspace.SelectTab("profiler"));
        Assert.True(workspace.IsTabSelected("profiler"));
        Assert.False(workspace.IsTabSelected("game"));
        Assert.False(workspace.SelectTab("missing"));
    }

    /// <summary>Verifies a closed panel reopens beside its stable sibling anchor.</summary>
    [Fact]
    public void OpenTab_ClosedPanel_ReopensBesideAnchor()
    {
        var gameTools = new DockTabGroup([new DockTab("game", "Game")]);
        var workspace = new DockWorkspace
        {
            Root = new DockSplit(
                DockSplitOrientation.Horizontal,
                new DockTabGroup([new DockTab("scene", "Scene")]),
                gameTools)
        };

        Assert.True(workspace.OpenTab("profiler", "Profiler", "game"));

        Assert.Equal(["game", "profiler"], gameTools.Tabs.Select(tab => tab.Id));
        Assert.Equal("profiler", gameTools.SelectedId);
        Assert.True(workspace.OpenTab("profiler", "Profiler", "scene"));
        Assert.Equal(2, gameTools.Tabs.Count);
    }

    /// <summary>Verifies session panel activation rejects unknown IDs and refreshes registered panels.</summary>
    [Fact]
    public void DockSession_OpenPanel_UsesRegistryTitleAndAnchor()
    {
        var group = new DockTabGroup([new DockTab("game", "Game")]);
        var registry = new DockPanelRegistry();
        registry.Register("game", "Game",
            () => new Panel(Engine.Graphics.Color.Black, 10f, 10f));
        registry.Register("profiler", "CPU Profiler",
            () => new Panel(Engine.Graphics.Color.Gray, 10f, 10f));
        using var session = new DockSession(
            new DockWorkspace { Root = group }, registry, new FakeFloatingWindowFactory());

        Assert.True(session.OpenPanel("profiler", "game"));
        Assert.False(session.OpenPanel("missing", "game"));
        Assert.Equal("CPU Profiler", group.Tabs[1].Title);
        Assert.Equal("profiler", group.SelectedId);
    }

    /// <summary>Verifies panel registry factories run once and retain identity.</summary>
    [Fact]
    public void PanelRegistry_Resolve_CreatesContentOnce()
    {
        var created = 0;
        var registry = new DockPanelRegistry();
        registry.Register("scene", "Scene", () =>
        {
            created++;
            return new Panel(Engine.Graphics.Color.Black);
        });

        var first = registry.Resolve("scene");
        var second = registry.Resolve("scene");

        Assert.Same(first, second);
        Assert.Equal(1, created);
        Assert.Equal("Scene", registry.GetTitle("scene"));
    }

    /// <summary>Verifies a session detaches, floats, disposes, and redocks one retained panel.</summary>
    [Fact]
    public void DockSession_FloatAndRedock_PreservesRetainedContent()
    {
        var group = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("profiler", "Profiler")
        ]);
        var registry = new DockPanelRegistry();
        var scene = new Panel(Engine.Graphics.Color.Black);
        var profiler = new Panel(Engine.Graphics.Color.Gray);
        registry.Register("scene", "Scene", () => scene);
        registry.Register("profiler", "Profiler", () => profiler);
        var factory = new FakeFloatingWindowFactory();
        using var session = new DockSession(
            new DockWorkspace { Root = group }, registry, factory);

        Assert.True(session.FloatTab("profiler", 10f, 20f, 400f, 300f));
        Assert.Same(session.Workspace, factory.Windows[0].Content.Workspace);
        var floatingId = session.Workspace.FloatingRoots[0].Id;
        session.Redock(floatingId, group);

        Assert.Single(factory.Windows);
        Assert.True(factory.Windows[0].Disposed);
        Assert.Empty(session.Workspace.FloatingRoots);
        Assert.Same(profiler, registry.Resolve("profiler"));
        Assert.Same(session.MainHost, profiler.Parent?.Parent?.Parent?.Parent);
    }

    /// <summary>Verifies floating recurring work contributes to the shared window-loop demand.</summary>
    [Fact]
    public void DockSession_FloatingFrameDemand_AggregatesOpenWindows()
    {
        var group = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("profiler", "Profiler")
        ]);
        var registry = new DockPanelRegistry();
        registry.Register("scene", "Scene",
            () => new Panel(Engine.Graphics.Color.Black, 10f, 10f));
        registry.Register("profiler", "Profiler",
            () => new Panel(Engine.Graphics.Color.Gray, 10f, 10f));
        var factory = new FakeFloatingWindowFactory();
        using var session = new DockSession(
            new DockWorkspace { Root = group }, registry, factory);
        Assert.True(session.FloatTab("profiler", 10f, 20f, 400f, 300f));

        Assert.False(session.RequiresContinuousUpdates);
        factory.Windows[0].RequiresContinuousUpdates = true;
        Assert.True(session.RequiresContinuousUpdates);
        factory.Windows[0].Dispose();
        Assert.False(session.RequiresContinuousUpdates);
    }

    /// <summary>Verifies a floating-host mutation refresh closes an emptied window and reparents content.</summary>
    [Fact]
    public void DockSession_FloatingMutation_ReconcilesAllPresentations()
    {
        var main = new DockTabGroup([new DockTab("scene", "Scene")]);
        var registry = new DockPanelRegistry();
        var scene = new Panel(Engine.Graphics.Color.Black);
        var profiler = new Panel(Engine.Graphics.Color.Gray);
        registry.Register("scene", "Scene", () => scene);
        registry.Register("profiler", "Profiler", () => profiler);
        main.Add(new DockTab("profiler", "Profiler"));
        var factory = new FakeFloatingWindowFactory();
        using var session = new DockSession(
            new DockWorkspace { Root = main }, registry, factory);
        Assert.True(session.FloatTab("profiler", 10f, 20f, 400f, 300f));
        Assert.True(session.Workspace.MoveTab("profiler", main));
        session.Refresh();

        Assert.True(factory.Windows[0].Disposed);
        Assert.Empty(session.Workspace.FloatingRoots);
        Assert.Same(session.MainHost, profiler.Parent?.Parent?.Parent?.Parent);
    }

    /// <summary>Verifies a session transfers tabs between native-host model roots and refreshes both.</summary>
    [Fact]
    public void DockSession_DockTab_CrossHostMovesAndReconciles()
    {
        var main = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("game", "Game")
        ]);
        var floatingTarget = new DockTabGroup([new DockTab("inspector", "Inspector")]);
        var workspace = new DockWorkspace
        {
            Root = main,
            FloatingRoots =
            [
                new FloatingDockRoot
                {
                    Id = "tools",
                    Root = floatingTarget
                }
            ]
        };
        var registry = new DockPanelRegistry();
        var scene = new Panel(Engine.Graphics.Color.Black, 10f, 10f);
        var game = new Panel(Engine.Graphics.Color.Gray, 10f, 10f);
        var inspector = new Panel(Engine.Graphics.Color.White, 10f, 10f);
        registry.Register("scene", "Scene", () => scene);
        registry.Register("game", "Game", () => game);
        registry.Register("inspector", "Inspector", () => inspector);
        var factory = new FakeFloatingWindowFactory();
        using var session = new DockSession(workspace, registry, factory);

        Assert.True(session.DockTab("game", floatingTarget, DockDropZone.Center));

        Assert.Equal(["scene"], main.Tabs.Select(tab => tab.Id));
        Assert.Equal(["inspector", "game"], floatingTarget.Tabs.Select(tab => tab.Id));
        Assert.Single(factory.Windows);
        Assert.False(factory.Windows[0].Disposed);
        Assert.Same(session.MainHost, scene.Parent?.Parent?.Parent?.Parent);
        Assert.Same(factory.Windows[0].Content, game.Parent?.Parent?.Parent?.Parent);
    }

    /// <summary>Verifies physical screen coordinates resolve across a differently scaled floating host.</summary>
    [Fact]
    public void DockSession_DockTabAtScreenPosition_MapsFloatingHostCoordinates()
    {
        var main = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("game", "Game")
        ]);
        var floatingTarget = new DockTabGroup([new DockTab("inspector", "Inspector")]);
        var workspace = new DockWorkspace
        {
            Root = main,
            FloatingRoots =
            [
                new FloatingDockRoot
                {
                    Id = "tools",
                    Left = 500f,
                    Top = 100f,
                    Width = 300f,
                    Height = 200f,
                    Root = floatingTarget
                }
            ]
        };
        var registry = new DockPanelRegistry();
        registry.Register("scene", "Scene",
            () => new Panel(Engine.Graphics.Color.Black, 10f, 10f));
        registry.Register("game", "Game",
            () => new Panel(Engine.Graphics.Color.Gray, 10f, 10f));
        registry.Register("inspector", "Inspector",
            () => new Panel(Engine.Graphics.Color.White, 10f, 10f));
        var factory = new FakeFloatingWindowFactory();
        using var session = new DockSession(workspace, registry, factory);
        var floatingWindow = factory.Windows[0];
        floatingWindow.Content.Measure(new System.Numerics.Vector2(300f, 152f));
        floatingWindow.Content.Arrange(
            new System.Numerics.Vector2(0f, 48f),
            new System.Numerics.Vector2(300f, 152f));
        var screenPosition = floatingWindow.CoordinateMapper.ClientToScreen(
            new System.Numerics.Vector2(150f, 148f));

        Assert.True(session.TryGetDropTarget(screenPosition, out var target));
        Assert.Same(floatingWindow.Content, target.Host);
        Assert.Same(floatingTarget, target.Group);
        Assert.Equal(new System.Numerics.Vector2(146f, 96f), target.LocalPosition);
        Assert.True(session.DockTabAtScreenPosition(
            "game", screenPosition, DockDropZone.Center));
        Assert.Equal(["scene"], main.Tabs.Select(tab => tab.Id));
        Assert.Equal(["inspector", "game"], floatingTarget.Tabs.Select(tab => tab.Id));
    }

    /// <summary>Verifies drag release beyond one host automatically docks into another native host.</summary>
    [Fact]
    public void DockSession_HeaderDragAcrossWindows_DocksIntoFloatingHost()
    {
        var main = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("game", "Game")
        ]);
        var floatingTarget = new DockTabGroup([new DockTab("inspector", "Inspector")]);
        var workspace = new DockWorkspace
        {
            Root = main,
            FloatingRoots =
            [
                new FloatingDockRoot
                {
                    Id = "tools",
                    Left = 500f,
                    Top = 100f,
                    Width = 300f,
                    Height = 200f,
                    Root = floatingTarget
                }
            ]
        };
        var registry = new DockPanelRegistry();
        registry.Register("scene", "Scene",
            () => new Panel(Engine.Graphics.Color.Black, 10f, 10f));
        registry.Register("game", "Game",
            () => new Panel(Engine.Graphics.Color.Gray, 10f, 10f), canFloat: false);
        registry.Register("inspector", "Inspector",
            () => new Panel(Engine.Graphics.Color.White, 10f, 10f));
        var factory = new FakeFloatingWindowFactory();
        var mainCoordinates = new FakeWindowCoordinateMapper(
            System.Numerics.Vector2.Zero, 1f);
        using var session = new DockSession(
            workspace, registry, factory, mainCoordinates: mainCoordinates);
        session.MainHost.Width = 400f;
        session.MainHost.Height = 200f;
        session.MainHost.BuildDrawList();
        var router = new UIEventRouter(session.MainHost, () => { });
        session.AttachDragRouter(session.MainHost, router, mainCoordinates, () => { });

        router.MovePointer(new System.Numerics.Vector2(120f, 15f));
        router.Press();
        router.MovePointer(new System.Numerics.Vector2(650f, 200f));
        router.Release(invokeClick: true);

        Assert.Equal(["scene"], main.Tabs.Select(tab => tab.Id));
        Assert.Equal(["inspector", "game"], floatingTarget.Tabs.Select(tab => tab.Id));
        Assert.Single(factory.Windows);
        Assert.False(factory.Windows[0].Disposed);
        Assert.True(factory.Windows[0].PreviewRefreshCount > 0);
    }

    /// <summary>Verifies a destination-window edge glyph previews and commits a split drop.</summary>
    [Fact]
    public void DockSession_HeaderDragAcrossWindows_LeftTargetCreatesSplit()
    {
        var main = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("game", "Game")
        ]);
        var floatingTarget = new DockTabGroup([new DockTab("inspector", "Inspector")]);
        var floating = new FloatingDockRoot
        {
            Id = "tools",
            Left = 500f,
            Top = 100f,
            Width = 300f,
            Height = 200f,
            Root = floatingTarget
        };
        var workspace = new DockWorkspace { Root = main, FloatingRoots = [floating] };
        var registry = new DockPanelRegistry();
        registry.Register("scene", "Scene",
            () => new Panel(Engine.Graphics.Color.Black, 10f, 10f));
        registry.Register("game", "Game",
            () => new Panel(Engine.Graphics.Color.Gray, 10f, 10f));
        registry.Register("inspector", "Inspector",
            () => new Panel(Engine.Graphics.Color.White, 10f, 10f));
        var factory = new FakeFloatingWindowFactory();
        var mainCoordinates = new FakeWindowCoordinateMapper(
            System.Numerics.Vector2.Zero, 1f);
        using var session = new DockSession(
            workspace, registry, factory, mainCoordinates: mainCoordinates);
        session.MainHost.Width = 400f;
        session.MainHost.Height = 200f;
        session.MainHost.BuildDrawList();
        var router = new UIEventRouter(session.MainHost, () => { });
        session.AttachDragRouter(session.MainHost, router, mainCoordinates, () => { });

        router.MovePointer(new System.Numerics.Vector2(120f, 15f));
        router.Press();
        router.MovePointer(new System.Numerics.Vector2(647f, 250f));

        Assert.True(factory.Windows[0].PreviewRefreshCount > 0);
        router.Release(invokeClick: true);

        var split = Assert.IsType<DockSplit>(floating.Root);
        Assert.Equal(DockSplitOrientation.Horizontal, split.Orientation);
        Assert.Equal(["game"], Assert.IsType<DockTabGroup>(split.First).Tabs.Select(tab => tab.Id));
        Assert.Same(floatingTarget, split.Second);
        Assert.Equal(["scene"], main.Tabs.Select(tab => tab.Id));
    }

    /// <summary>Verifies releasing a tab beyond its host creates a session-owned floating window.</summary>
    [Fact]
    public void DockSession_HeaderDragOutside_FloatsTab()
    {
        var group = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("profiler", "Profiler")
        ]);
        var registry = new DockPanelRegistry();
        registry.Register("scene", "Scene",
            () => new Panel(Engine.Graphics.Color.Black, 10f, 10f));
        registry.Register("profiler", "Profiler",
            () => new Panel(Engine.Graphics.Color.Gray, 10f, 10f));
        var factory = new FakeFloatingWindowFactory();
        var mainCoordinates = new FakeWindowCoordinateMapper(
            new System.Numerics.Vector2(100f, 200f), 1f);
        using var session = new DockSession(
            new DockWorkspace { Root = group }, registry, factory,
            mainCoordinates: mainCoordinates);
        session.MainHost.Width = 400f;
        session.MainHost.Height = 200f;
        session.MainHost.BuildDrawList();
        var router = new UIEventRouter(session.MainHost, () => { });

        router.MovePointer(new System.Numerics.Vector2(120f, 15f));
        router.Press();
        router.MovePointer(new System.Numerics.Vector2(450f, 240f));
        router.Release(invokeClick: true);

        Assert.Equal(["scene"], group.Tabs.Select(tab => tab.Id));
        Assert.Single(session.Workspace.FloatingRoots);
        Assert.Single(factory.Windows);
        Assert.Equal(526f, session.Workspace.FloatingRoots[0].Left);
        Assert.Equal(424f, session.Workspace.FloatingRoots[0].Top);
    }

    /// <summary>Verifies floating policy rejects drag-outside without disabling ordinary tab dragging.</summary>
    [Fact]
    public void DockSession_HeaderDragOutside_DisallowedPanelStaysDocked()
    {
        var group = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("game", "Game")
        ]);
        var registry = new DockPanelRegistry();
        registry.Register("scene", "Scene",
            () => new Panel(Engine.Graphics.Color.Black, 10f, 10f), canFloat: false);
        registry.Register("game", "Game",
            () => new Panel(Engine.Graphics.Color.Gray, 10f, 10f));
        var factory = new FakeFloatingWindowFactory();
        using var session = new DockSession(
            new DockWorkspace { Root = group }, registry, factory);
        session.MainHost.Width = 400f;
        session.MainHost.Height = 200f;
        session.MainHost.BuildDrawList();
        var router = new UIEventRouter(session.MainHost, () => { });

        router.MovePointer(new System.Numerics.Vector2(20f, 15f));
        router.Press();
        router.MovePointer(new System.Numerics.Vector2(450f, 240f));
        router.Release(invokeClick: true);

        Assert.Equal(["scene", "game"], group.Tabs.Select(tab => tab.Id));
        Assert.Empty(session.Workspace.FloatingRoots);
        Assert.Empty(factory.Windows);
    }

    /// <summary>Verifies session reconciliation asks native hosts to persist current geometry.</summary>
    [Fact]
    public void DockSession_SynchronizeFloatingWindows_UpdatesGeometry()
    {
        var group = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("profiler", "Profiler")
        ]);
        var registry = new DockPanelRegistry();
        registry.Register("scene", "Scene",
            () => new Panel(Engine.Graphics.Color.Black, 10f, 10f));
        registry.Register("profiler", "Profiler",
            () => new Panel(Engine.Graphics.Color.Gray, 10f, 10f));
        var factory = new FakeFloatingWindowFactory();
        using var session = new DockSession(
            new DockWorkspace { Root = group }, registry, factory);
        Assert.True(session.FloatTab("profiler", 10f, 20f, 400f, 300f));

        factory.Windows[0].NextGeometry = (50f, 60f, 700f, 500f);
        session.SynchronizeFloatingWindows();

        var floating = session.Workspace.FloatingRoots[0];
        Assert.Equal(50f, floating.Left);
        Assert.Equal(60f, floating.Top);
        Assert.Equal(700f, floating.Width);
        Assert.Equal(500f, floating.Height);
        Assert.True(factory.Windows[0].GeometrySyncCount > 0);
    }

    /// <summary>Verifies persisted floating roots can wait until renderer initialization completes.</summary>
    [Fact]
    public void DockSession_DelayedFloatingInitialization_OpensOnSynchronize()
    {
        var main = new DockTabGroup([new DockTab("scene", "Scene")]);
        var workspace = new DockWorkspace
        {
            Root = main,
            FloatingRoots =
            [
                new FloatingDockRoot
                {
                    Id = "tools",
                    Root = new DockTabGroup([new DockTab("profiler", "Profiler")])
                }
            ]
        };
        var registry = new DockPanelRegistry();
        registry.Register("scene", "Scene",
            () => new Panel(Engine.Graphics.Color.Black, 10f, 10f));
        registry.Register("profiler", "Profiler",
            () => new Panel(Engine.Graphics.Color.Gray, 10f, 10f));
        var factory = new FakeFloatingWindowFactory();
        using var session = new DockSession(
            workspace, registry, factory, initializeFloatingWindows: false);

        Assert.Empty(factory.Windows);
        session.SynchronizeFloatingWindows();

        Assert.Single(factory.Windows);
        Assert.Same(workspace, factory.Windows[0].Content.Workspace);
    }

    /// <summary>Verifies workspace storage round-trips through an atomic file replacement.</summary>
    [Fact]
    public void WorkspaceStore_SaveAndLoad_RoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nico-dock-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "workspace.json");
        try
        {
            var workspace = new DockWorkspace
            {
                Root = new DockTabGroup([new DockTab("scene", "Scene")])
            };

            DockWorkspaceStore.Save(path, workspace);
            var restored = DockWorkspaceStore.LoadOrDefault(
                path, static () => new DockWorkspace(), out var error);

            Assert.Null(error);
            Assert.Equal("scene", Assert.IsType<DockTabGroup>(restored.Root).SelectedId);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Verifies corrupt persistence fails safely without overwriting evidence.</summary>
    [Fact]
    public void WorkspaceStore_CorruptJson_ReturnsDefaultAndError()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not json");

            var restored = DockWorkspaceStore.LoadOrDefault(path,
                static () => new DockWorkspace
                {
                    Root = new DockTabGroup([new DockTab("safe", "Safe")])
                }, out var error);

            Assert.NotNull(error);
            Assert.Equal("safe", Assert.IsType<DockTabGroup>(restored.Root).SelectedId);
            Assert.Equal("not json", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Verifies removing the only tab in a split branch collapses the split.</summary>
    [Fact]
    public void RemoveTab_EmptySplitBranch_CollapsesToSibling()
    {
        var remaining = new DockTabGroup([new DockTab("scene", "Scene")]);
        var workspace = new DockWorkspace
        {
            Root = new DockSplit(
                DockSplitOrientation.Horizontal,
                new DockTabGroup([new DockTab("hierarchy", "Hierarchy")]),
                remaining)
        };

        var removed = workspace.RemoveTab("hierarchy");

        Assert.Equal("hierarchy", removed?.Id);
        Assert.Same(remaining, workspace.Root);
    }

    /// <summary>Verifies a tab can float and redock without losing identity or geometry bounds.</summary>
    [Fact]
    public void FloatAndRedockTab_PreservesPanelIdentity()
    {
        var target = new DockTabGroup([new DockTab("scene", "Scene")]);
        var workspace = new DockWorkspace
        {
            Root = new DockSplit(
                DockSplitOrientation.Horizontal,
                target,
                new DockTabGroup([new DockTab("profiler", "Profiler")]))
        };

        var floating = workspace.FloatTab("profiler", 10f, 20f, 20f, 30f);
        workspace.RedockFloating(0, target);

        Assert.NotNull(floating);
        Assert.Equal(160f, floating.Width);
        Assert.Equal(120f, floating.Height);
        Assert.Empty(workspace.FloatingRoots);
        Assert.Equal(["scene", "profiler"], target.Tabs.Select(tab => tab.Id));
    }

    /// <summary>Verifies a dock host arranges split content according to the persisted ratio.</summary>
    [Fact]
    public void DockHost_Split_ArrangesRegisteredPanelContent()
    {
        var left = new Panel(Engine.Graphics.Color.Red);
        var right = new Panel(Engine.Graphics.Color.Blue);
        var workspace = new DockWorkspace
        {
            Root = new DockSplit(
                DockSplitOrientation.Horizontal,
                new DockTabGroup([new DockTab("left", "Left")]),
                new DockTabGroup([new DockTab("right", "Right")]),
                0.25f)
        };
        var host = new DockHost(workspace, id => id == "left" ? left : right)
        {
            Width = 400f,
            Height = 200f
        };

        host.BuildDrawList();

        Assert.Equal(91f, left.Width);
        Assert.Equal(285f, right.Width);
        Assert.Equal(left.Right + 10f, right.Left);
    }

    /// <summary>Verifies host-local pointer discovery resolves a nested tab well and its bounds.</summary>
    [Fact]
    public void DockHost_TryGetDropTarget_ResolvesNestedGroupAndBounds()
    {
        var left = new DockTabGroup([new DockTab("left", "Left")]);
        var upperRight = new DockTabGroup([new DockTab("scene", "Scene")]);
        var lowerRight = new DockTabGroup([new DockTab("game", "Game")]);
        var workspace = new DockWorkspace
        {
            Root = new DockSplit(
                DockSplitOrientation.Horizontal,
                left,
                new DockSplit(
                    DockSplitOrientation.Vertical,
                    upperRight,
                    lowerRight,
                    0.5f),
                0.25f)
        };
        var host = new DockHost(workspace,
            _ => new Panel(Engine.Graphics.Color.Black, 10f, 10f))
        {
            Width = 400f,
            Height = 200f,
            Position = new System.Numerics.Vector3(50f, 30f, 0f)
        };
        host.BuildDrawList();

        var matched = host.TryGetDropTarget(
            new System.Numerics.Vector2(300f, 150f), out var target, out var bounds);

        Assert.True(matched);
        Assert.Same(lowerRight, target);
        Assert.True(bounds.Contains(300f, 150f));
        Assert.Equal(101f, bounds.Left);
        Assert.Equal(98f, bounds.Top);
        Assert.Equal(392f, bounds.Right);
        Assert.Equal(192f, bounds.Bottom);
    }

    /// <summary>Verifies an external pointer over a tab strip negotiates an indexed center drop.</summary>
    [Fact]
    public void DockHost_UpdateExternalDockPreview_HeaderReturnsInsertionIndex()
    {
        var group = new DockTabGroup([
            new DockTab("inspector", "Inspector"),
            new DockTab("profiler", "Profiler")
        ]);
        var host = new DockHost(new DockWorkspace { Root = group },
            _ => new Panel(Engine.Graphics.Color.Black, 10f, 10f))
        {
            Width = 300f,
            Height = 200f
        };
        host.BuildDrawList();
        var presenter = Assert.IsAssignableFrom<UIElement>(Assert.Single(host.VisualChildren));
        var tabs = Assert.IsType<TabControl>(presenter.VisualChildren[0]);
        var headerStrip = Assert.IsType<FlexPanel>(tabs.VisualChildren[0]);
        var firstHeader = Assert.IsType<ToggleButton>(headerStrip.VisualChildren[0]);

        var placement = host.UpdateExternalDockPreview(
            new System.Numerics.Vector2(firstHeader.Right, firstHeader.Top + 10f));

        Assert.NotNull(placement);
        Assert.Same(group, placement.Value.Group);
        Assert.Equal(DockDropZone.Center, placement.Value.Zone);
        Assert.Equal(1, placement.Value.TargetIndex);
        Assert.True(host.ClearExternalDockPreview());
    }

    /// <summary>Verifies tab selection writes through to the workspace model.</summary>
    [Fact]
    public void DockHost_SelectTab_UpdatesSelectedIdentifier()
    {
        var group = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("game", "Game")
        ]);
        var scene = new Panel(Engine.Graphics.Color.Black, 10f, 10f);
        var game = new Panel(Engine.Graphics.Color.Black, 10f, 10f);
        var host = new DockHost(new DockWorkspace { Root = group },
            id => id == "scene" ? scene : game);
        var presenter = host.Children[0];
        var tabs = Assert.IsType<TabControl>(presenter.Children[0]);

        Assert.True(scene.IsEffectivelyVisible);
        Assert.False(game.IsEffectivelyVisible);

        tabs.Select(1);

        Assert.Equal("game", group.SelectedId);
        Assert.False(scene.IsEffectivelyVisible);
        Assert.True(game.IsEffectivelyVisible);
    }

    /// <summary>Verifies a header drag commits a center drop through routed pointer input.</summary>
    [Fact]
    public void DockHost_HeaderDrag_CenterDropMovesTab()
    {
        var source = new DockTabGroup([
            new DockTab("game", "Game"),
            new DockTab("scene", "Scene")
        ]);
        var target = new DockTabGroup([new DockTab("inspector", "Inspector")]);
        var workspace = new DockWorkspace
        {
            Root = new DockSplit(DockSplitOrientation.Horizontal, source, target, 0.5f)
        };
        var host = new DockHost(workspace,
            _ => new Panel(Engine.Graphics.Color.Black, 10f, 10f))
        {
            Width = 400f,
            Height = 200f
        };
        host.BuildDrawList();
        var router = new UIEventRouter(host, () => { });

        router.MovePointer(new System.Numerics.Vector2(20f, 15f));
        router.Press();
        router.MovePointer(new System.Numerics.Vector2(300f, 100f));
        router.Release(invokeClick: true);

        Assert.Equal(["scene"], source.Tabs.Select(tab => tab.Id));
        Assert.Equal(["inspector", "game"], target.Tabs.Select(tab => tab.Id));
    }

    /// <summary>Verifies dragging a header across its own strip reorders the model.</summary>
    [Fact]
    public void DockHost_HeaderDrag_WithinStripReordersTabs()
    {
        var group = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("game", "Game"),
            new DockTab("profiler", "Profiler")
        ]);
        var host = new DockHost(new DockWorkspace { Root = group },
            _ => new Panel(Engine.Graphics.Color.Black, 10f, 10f))
        {
            Width = 400f,
            Height = 200f
        };
        host.BuildDrawList();
        var router = new UIEventRouter(host, () => { });

        router.MovePointer(new System.Numerics.Vector2(20f, 15f));
        router.Press();
        router.MovePointer(new System.Numerics.Vector2(290f, 15f));
        router.Release(invokeClick: true);

        Assert.Equal(["game", "profiler", "scene"], group.Tabs.Select(tab => tab.Id));
        Assert.Equal("scene", group.SelectedId);
    }

    /// <summary>Verifies expensive split dependents are notified once after live dragging ends.</summary>
    [Fact]
    public void DockHost_SplitterDrag_NotifiesResizeOnlyOnRelease()
    {
        var split = new DockSplit(
            DockSplitOrientation.Horizontal,
            new DockTabGroup([new DockTab("scene", "Scene")]),
            new DockTabGroup([new DockTab("game", "Game")]),
            0.5f);
        var host = new DockHost(new DockWorkspace { Root = split },
            _ => new Panel(Engine.Graphics.Color.Black, 10f, 10f))
        {
            Width = 400f,
            Height = 200f
        };
        host.BuildDrawList();
        var router = new UIEventRouter(host, () => { host.BuildDrawList(); });
        var completed = 0;
        host.SplitResizeCompleted += () => completed++;
        var originalRatio = split.Ratio;

        router.MovePointer(new System.Numerics.Vector2(199f, 100f));
        router.Press();
        router.MovePointer(new System.Numerics.Vector2(239f, 100f));

        Assert.NotEqual(originalRatio, split.Ratio);
        Assert.Equal(0, completed);

        router.Release(invokeClick: true);

        Assert.Equal(1, completed);
    }

    /// <summary>Verifies a focused tab header closes through the platform keyboard gesture.</summary>
    [Theory]
    [InlineData(Engine.Graphics.InputModifiers.Control)]
    [InlineData(Engine.Graphics.InputModifiers.Super)]
    public void DockHost_FocusedHeader_CloseGestureRemovesTab(
        Engine.Graphics.InputModifiers modifiers)
    {
        var group = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("game", "Game")
        ]);
        var host = new DockHost(new DockWorkspace { Root = group },
            _ => new Panel(Engine.Graphics.Color.Black, 10f, 10f))
        {
            Width = 400f,
            Height = 200f
        };
        host.BuildDrawList();
        var router = new UIEventRouter(host, () => { });
        router.MovePointer(new System.Numerics.Vector2(20f, 15f));
        router.Press();
        router.Release(invokeClick: true);

        router.RouteKey(new Engine.Graphics.KeyInputEvent(
            Engine.Graphics.InputKey.W, true, false, modifiers));

        Assert.Equal(["game"], group.Tabs.Select(tab => tab.Id));
    }

    /// <summary>Verifies repeat and modified close gestures do not remove tabs.</summary>
    [Fact]
    public void DockHost_FocusedHeader_InvalidCloseGestureIsIgnored()
    {
        var group = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("game", "Game")
        ]);
        var host = new DockHost(new DockWorkspace { Root = group },
            _ => new Panel(Engine.Graphics.Color.Black, 10f, 10f))
        {
            Width = 400f,
            Height = 200f
        };
        host.BuildDrawList();
        var router = new UIEventRouter(host, () => { });
        router.MovePointer(new System.Numerics.Vector2(20f, 15f));
        router.Press();
        router.Release(invokeClick: true);

        router.RouteKey(new Engine.Graphics.KeyInputEvent(
            Engine.Graphics.InputKey.W, true, true, Engine.Graphics.InputModifiers.Control));
        router.RouteKey(new Engine.Graphics.KeyInputEvent(
            Engine.Graphics.InputKey.W, true, false,
            Engine.Graphics.InputModifiers.Control | Engine.Graphics.InputModifiers.Shift));

        Assert.Equal(["scene", "game"], group.Tabs.Select(tab => tab.Id));
    }

    /// <summary>Verifies dock headers do not mount a trailing close button.</summary>
    [Fact]
    public void DockHost_Header_HasNoCloseButton()
    {
        var group = new DockTabGroup([
            new DockTab("scene", "Scene"),
            new DockTab("game", "Game")
        ]);
        var host = new DockHost(new DockWorkspace { Root = group },
            _ => new Panel(Engine.Graphics.Color.Black, 10f, 10f))
        {
            Width = 400f,
            Height = 200f
        };
        host.BuildDrawList();
        var router = new UIEventRouter(host, () => { });

        router.MovePointer(new System.Numerics.Vector2(88f, 15f));
        router.Press();
        router.Release(invokeClick: true);

        Assert.Equal(["scene", "game"], group.Tabs.Select(tab => tab.Id));
    }

    /// <summary>Verifies tabs can move within a retained group without duplication.</summary>
    [Fact]
    public void TabGroup_AddExisting_MovesAndSelectsTab()
    {
        var first = new DockTab("scene", "Scene");
        var second = new DockTab("game", "Game");
        var group = new DockTabGroup([first, second]);

        group.Add(first, 1);

        Assert.Equal([second, first], group.Tabs);
        Assert.Equal("scene", group.SelectedId);
    }

    /// <summary>Verifies split trees and floating roots survive a versioned round trip.</summary>
    [Fact]
    public void SaveLoad_SplitAndFloatingRoots_RoundTrip()
    {
        var workspace = new DockWorkspace
        {
            Root = new DockSplit(
                DockSplitOrientation.Horizontal,
                new DockTabGroup([new DockTab("hierarchy", "Hierarchy")]),
                new DockTabGroup([new DockTab("scene", "Scene")]),
                0.25f),
            FloatingRoots =
            [
                new FloatingDockRoot
                {
                    Root = new DockTabGroup([new DockTab("profiler", "Profiler")]),
                    Left = 20f,
                    Top = 30f,
                    Width = 800f,
                    Height = 600f
                }
            ]
        };

        var restored = DockWorkspace.Load(workspace.Save());

        var split = Assert.IsType<DockSplit>(restored.Root);
        Assert.Equal(0.25f, split.Ratio);
        Assert.Equal("hierarchy", Assert.IsType<DockTabGroup>(split.First).SelectedId);
        Assert.Equal("profiler", Assert.IsType<DockTabGroup>(restored.FloatingRoots[0].Root).SelectedId);
    }

    /// <summary>Verifies restoration clamps unsafe geometry and removes duplicate panel IDs.</summary>
    [Fact]
    public void Load_InvalidState_NormalizesSafely()
    {
        var duplicate = new DockTab("scene", "Scene");
        var workspace = new DockWorkspace
        {
            Root = new DockSplit(
                DockSplitOrientation.Vertical,
                new DockTabGroup([duplicate], "missing"),
                new DockTabGroup([duplicate]),
                5f),
            FloatingRoots =
            [
                new FloatingDockRoot { Width = 1f, Height = 1f }
            ]
        };

        var restored = DockWorkspace.Load(workspace.Save());

        var split = Assert.IsType<DockSplit>(restored.Root);
        Assert.Equal(0.9f, split.Ratio);
        Assert.Equal("scene", Assert.IsType<DockTabGroup>(split.First).SelectedId);
        Assert.Empty(Assert.IsType<DockTabGroup>(split.Second).Tabs);
        Assert.Equal(160f, restored.FloatingRoots[0].Width);
        Assert.Equal(120f, restored.FloatingRoots[0].Height);
    }

    /// <summary>Verifies incompatible schema versions fail closed.</summary>
    [Fact]
    public void Load_UnknownVersion_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            DockWorkspace.Load("{\"Version\":999,\"Root\":{\"kind\":\"tabs\",\"Tabs\":[]}}"));
    }

    /// <summary>Creates inspectable fake floating hosts.</summary>
    private sealed class FakeFloatingWindowFactory : IDockFloatingWindowFactory
    {
        /// <summary>Gets created fake windows.</summary>
        internal List<FakeFloatingWindow> Windows { get; } = [];

        /// <inheritdoc/>
        public IDockFloatingWindow Create(FloatingDockRoot model, DockHost content)
        {
            var window = new FakeFloatingWindow(model, content);
            Windows.Add(window);
            return window;
        }
    }

    /// <summary>Tracks fake floating-window lifecycle.</summary>
    private sealed class FakeFloatingWindow : IDockFloatingWindow, IDockFloatingGeometry,
        IDockFloatingWindowCoordinates, IDockFloatingDragHost, IDockFloatingFrameDemand
    {
        private readonly FloatingDockRoot _model;
        /// <summary>Gets hosted dock content.</summary>
        internal DockHost Content { get; }

        /// <summary>Gets whether disposal occurred.</summary>
        internal bool Disposed { get; private set; }

        /// <summary>Gets the number of geometry synchronization requests.</summary>
        internal int GeometrySyncCount { get; private set; }

        /// <summary>Gets the number of dock-preview submissions.</summary>
        internal int PreviewRefreshCount { get; private set; }

        /// <inheritdoc/>
        public bool RequiresContinuousUpdates { get; internal set; }

        /// <summary>Gets or sets geometry copied into the model on synchronization.</summary>
        internal (float Left, float Top, float Width, float Height)? NextGeometry { get; set; }

        /// <inheritdoc/>
        public bool IsOpen => !Disposed;

        /// <inheritdoc/>
        public Engine.Graphics.IWindowCoordinateMapper CoordinateMapper { get; }

        /// <inheritdoc/>
        public UIEventRouter InputRouter { get; }

        /// <summary>Creates a fake host.</summary>
        /// <param name="model">Floating model.</param>
        /// <param name="content">Hosted content.</param>
        internal FakeFloatingWindow(FloatingDockRoot model, DockHost content)
        {
            _model = model;
            Content = content;
            CoordinateMapper = new FakeWindowCoordinateMapper(
                new System.Numerics.Vector2(model.Left, model.Top), 1.5f);
            Content.Width = model.Width;
            Content.Height = model.Height;
            Content.BuildDrawList();
            InputRouter = new UIEventRouter(Content, RefreshDockPreview);
        }

        /// <inheritdoc/>
        public void SynchronizeGeometry()
        {
            GeometrySyncCount++;
            if (NextGeometry is not { } geometry)
                return;
            _model.Left = geometry.Left;
            _model.Top = geometry.Top;
            _model.Width = geometry.Width;
            _model.Height = geometry.Height;
        }

        /// <inheritdoc/>
        public void RefreshDockPreview()
        {
            PreviewRefreshCount++;
            Content.BuildDrawList();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Disposed)
                return;
            Disposed = true;
            Content.ClearChildren();
        }
    }

    /// <summary>Maps logical test positions through a configurable physical-pixel scale.</summary>
    private sealed class FakeWindowCoordinateMapper : Engine.Graphics.IWindowCoordinateMapper
    {
        private readonly System.Numerics.Vector2 _screenOrigin;
        private readonly float _scale;

        /// <summary>Creates a deterministic fake coordinate mapper.</summary>
        /// <param name="screenOrigin">Physical client origin.</param>
        /// <param name="scale">Physical pixels per logical pixel.</param>
        internal FakeWindowCoordinateMapper(System.Numerics.Vector2 screenOrigin, float scale)
        {
            _screenOrigin = screenOrigin;
            _scale = scale;
        }

        /// <inheritdoc/>
        public System.Numerics.Vector2 ClientToScreen(System.Numerics.Vector2 clientPosition) =>
            _screenOrigin + clientPosition * _scale;

        /// <inheritdoc/>
        public System.Numerics.Vector2 ScreenToClient(System.Numerics.Vector2 screenPosition) =>
            (screenPosition - _screenOrigin) / _scale;
    }
}
