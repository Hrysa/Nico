using System.Numerics;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>
/// Builds the editor UI layout using <see cref="UIElement"/> types.
/// Coordinate system: (0,0) at top-left, Y increases downward. Y is the top edge.
/// </summary>
public static class EditorUI
{
    /// <summary>
    /// Builds the editor UI tree from the given window dimensions.
    /// </summary>
    /// <param name="width">The window width in pixels.</param>
    /// <param name="height">The window height in pixels.</param>
    /// <returns>The root <see cref="Panel"/> containing all editor UI elements.</returns>
    public static EditorView BuildView(float width, float height)
    {
        var theme = UITheme.Dark;
        const float titleBarHeight = TitleBar.DefaultHeight;
        const float bottomDockHeight = 30f;
        const float separatorWidth = 1f;
        const float hierarchyWidth = 252f;
        const float inspectorWidth = 300f;
        const float viewportToolbarHeight = 36f;
        var workspaceHeight = MathF.Max(0f, height - titleBarHeight - bottomDockHeight);

        var projectLabel = new Label("scene.node", 180f, titleBarHeight)
        {
            Name = "ProjectLabel",
            FontSize = theme.FontSize,
            ForegroundColor = theme.TextSecondary,
            PaddingLeft = 0f
        };
        var windowMenu = new ContextMenu(180f, theme) { Name = "WindowMenu" };
        var windowPanelItems = new Dictionary<string, ContextMenuItem>(StringComparer.Ordinal)
        {
            [EditorDockWorkspace.HierarchyId] =
                windowMenu.AddCheckItem("Hierarchy", isChecked: true, _ => { }),
            [EditorDockWorkspace.FileSystemId] =
                windowMenu.AddCheckItem("File System", isChecked: true, _ => { }),
            [EditorDockWorkspace.SceneId] =
                windowMenu.AddCheckItem("Scene", isChecked: true, _ => { }),
            [EditorDockWorkspace.GameId] =
                windowMenu.AddCheckItem("Game", isChecked: true, _ => { }),
            [EditorDockWorkspace.InspectorId] =
                windowMenu.AddCheckItem("Inspector", isChecked: true, _ => { }),
            [EditorDockWorkspace.ProfilerId] =
                windowMenu.AddCheckItem("Profiler", isChecked: true, _ => { })
        };
        var previewMenu = new ContextMenu(180f, theme) { Name = "ScenePreviewMenu" };
        var previewItems = new Dictionary<ScenePreviewCategory, ContextMenuItem>
        {
            [ScenePreviewCategory.Nodes] = previewMenu.AddCheckItem("Invisible Nodes", true, _ => { }),
            [ScenePreviewCategory.Cameras] = previewMenu.AddCheckItem("Cameras", true, _ => { }),
            [ScenePreviewCategory.Lights] = previewMenu.AddCheckItem("Lights", true, _ => { }),
            [ScenePreviewCategory.Colliders] = previewMenu.AddCheckItem("Colliders", true, _ => { })
        };
        var viewMenu = new ContextMenu(180f, theme) { Name = "ViewMenu" };
        viewMenu.AddSubmenu("Scene Previews", previewMenu);
        var titleMenuBar = new MenuBar(126f, titleBarHeight, theme)
        {
            Name = "TitleMenuBar",
            BackgroundColor = theme.Canvas
        };
        titleMenuBar.AddMenu("Window", windowMenu);
        titleMenuBar.AddMenu("View", viewMenu);
        var playButtonIcon = new Icon(IconKind.Play, 16f)
        {
            ForegroundColor = theme.Accent
        };
        var playButton = new Button(28f, theme, ButtonStyle.Primary)
        {
            Name = "Play",
            Content = playButtonIcon
        };
        var titleBar = new TitleBar(width, titleBarHeight, theme)
        {
            Width = 0f,
            FlexShrink = 0f
        }.Configure(bar =>
        {
            bar.LeftZone.WithChildren(titleMenuBar, projectLabel);
            bar.CenterZone.WithChildren(playButton);
        });

        var bottomTabs = UI.Row(
        [
            UI.Ref(new Button(90f, bottomDockHeight, "Hierarchy", theme)
                { Name = "HierarchyButton" }, out var hierarchyButton),
            UI.Ref(new Button(90f, bottomDockHeight, "Files", theme)
                { Name = "FileSystemButton" }, out var fileSystemButton),
            UI.Ref(new Button(90f, bottomDockHeight, "Scene", theme)
                { Name = "SceneButton" }, out var sceneButton),
            UI.Ref(new Button(90f, bottomDockHeight, "Game", theme)
                { Name = "GameButton" }, out var gameButton),
            UI.Ref(new Button(90f, bottomDockHeight, "Inspector", theme)
                { Name = "InspectorButton" }, out var inspectorButton),
            UI.Ref(new Button(90f, bottomDockHeight, "Profiler", theme)
                { Name = "ProfilerButton" }, out var profilerButton)
        ],
        backgroundColor: theme.Surface,
        justifyContent: FlexJustify.Center).Named("BottomDock");

        var profilerPauseLabel = new Label("Record")
        {
            FontSize = theme.CaptionFontSize,
            ForegroundColor = theme.TextPrimary,
            PaddingLeft = 0f,
            IsHitTestVisible = false
        };
        var profilerLayout = UI.Column(
        [
            UI.Row(
            [
                UI.Ref(new Button(80f, theme.ControlHeight, theme)
                {
                    Name = "ProfilerPause",
                    Content = profilerPauseLabel
                }, out var profilerPauseButton)
            ], backgroundColor: theme.SurfaceRaised).Configure(toolbar =>
            {
                toolbar.Padding = new Thickness(4f, 0f);
                toolbar.Height = theme.ControlHeight;
                toolbar.FlexShrink = 0f;
            }),
            UI.Ref(new ProfilerView(theme) { Name = "ProfilerView" }
                .Configure(view => view.SetPaused(true)), out var profiler).Grow()
        ], backgroundColor: theme.Surface);
        var hierarchyTree = new TreeView(hierarchyWidth, 0f, theme)
        {
            Name = "HierarchyTree",
            Width = 0f,
            Height = 0f
        };
        var fileTree = new TreeView(hierarchyWidth, 0f, theme)
        {
            Name = "ProjectFiles",
            Width = 0f,
            Height = 0f
        };
        var inspector = new SceneInspector(inspectorWidth, 0f, theme)
        {
            Name = "SceneInspector",
            Width = 0f,
            Height = 0f
        };

        var viewportWidth = MathF.Max(0f, width - hierarchyWidth - inspectorWidth - (separatorWidth * 2f));
        var sceneSlotHeight = MathF.Floor(workspaceHeight * 0.73f);
        var gameSlotHeight = MathF.Max(0f, workspaceHeight - sceneSlotHeight - separatorWidth);
        var sceneTools = new Surface(theme.SurfaceRaised, theme.Border,
            viewportWidth, viewportToolbarHeight)
        {
            Name = "SceneToolbar",
            Width = 0f,
            FlexShrink = 0f
        }.WithChildren(
            UI.Row(
            [
                new Button(28f, "Select", theme, ButtonStyle.Primary),
                new Button(28f, "Move", theme),
                new Button(28f, "Rotate", theme),
                new Button(28f, "Scale", theme),
                new FlexPanel().Grow(),
                new Label("Perspective", 96f, viewportToolbarHeight)
                {
                    ForegroundColor = theme.TextSecondary,
                    FontSize = theme.CaptionFontSize,
                    PaddingLeft = 0f
                }
            ],
            backgroundColor: theme.SurfaceRaised,
            alignItems: FlexAlignment.Center,
            gap: 4f).Named("SceneToolbarLayout").Configure(toolbar =>
                toolbar.Padding = new Thickness(8f, 0f)));

        var sceneSlot = UI.Column(
        [
            sceneTools,
            UI.Ref(new ViewportPanel(viewportWidth,
                MathF.Max(0f, sceneSlotHeight - viewportToolbarHeight), theme.Viewport)
            {
                Name = "SceneViewport",
                Width = 0f,
                Height = 0f
            }, out var sceneViewport).Grow()
        ], backgroundColor: theme.Viewport).Named("SceneSlot");

        var gameSlot = UI.Column(
        [
            UI.Ref(new ViewportPanel(viewportWidth, gameSlotHeight, theme.Viewport)
            {
                Name = "GameViewport",
                Width = 0f,
                Height = 0f
            }, out var gameViewport).Grow()
        ], backgroundColor: theme.Viewport).Named("GameSlot");

        var initialWorkspace = EditorDockWorkspace.CreateDefault();
        var initialDockHost = new DockHost(initialWorkspace, id => id switch
        {
            EditorDockWorkspace.HierarchyId => hierarchyTree,
            EditorDockWorkspace.FileSystemId => fileTree,
            EditorDockWorkspace.SceneId => sceneSlot,
            EditorDockWorkspace.GameId => gameSlot,
            EditorDockWorkspace.InspectorId => inspector,
            EditorDockWorkspace.ProfilerId => profilerLayout,
            _ => null
        }, theme)
        {
            Name = "InitialDockHost"
        };

        var background = UI.Overlay(
        [
            UI.Column(
            [
                titleBar,
                UI.Ref(new ContentControl
                {
                    Name = "WorkspaceHost",
                    Content = initialDockHost,
                    PaintBackground = false,
                    FlexGrow = 1f
                }, out var workspaceHost),
                UI.Row(
                [
                    new FlexPanel
                    {
                        Width = hierarchyWidth + separatorWidth,
                        FlexShrink = 0f
                    },
                    bottomTabs.Grow(),
                    new FlexPanel
                    {
                        Width = inspectorWidth + separatorWidth,
                        FlexShrink = 0f
                    }
                ], backgroundColor: theme.Canvas).Named("BottomShell").Configure(shell =>
                {
                    shell.Height = bottomDockHeight;
                    shell.FlexShrink = 0f;
                })
            ]).Named("MainLayout"),
            UI.Ref(new Canvas { Name = "Overlay" }, out var overlay)
        ], backgroundColor: theme.Canvas).Named("Background").Configure(root =>
            root.ForegroundColor = theme.TextPrimary);
        background.Measure(new Vector2(width, height));
        background.Arrange(Vector2.Zero, new Vector2(width, height));

        return new EditorView(background, sceneViewport, gameViewport, hierarchyTree, fileTree,
            projectLabel, playButton, playButtonIcon, inspector, titleBar, overlay,
            sceneSlot, gameSlot, sceneTools, initialDockHost, workspaceHost,
            profilerLayout, profiler, hierarchyButton, fileSystemButton, sceneButton, gameButton,
            inspectorButton, profilerButton,
            profilerPauseButton, profilerPauseLabel, titleMenuBar, windowMenu, windowPanelItems,
            viewMenu, previewMenu, previewItems);
    }

    /// <summary>
    /// Builds only the root UI panel for callers that do not need named editor elements.
    /// </summary>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <returns>The root UI panel.</returns>
    public static Panel BuildUI(float width, float height) => BuildView(width, height).Root;

    /// <summary>
    /// Creates the MVP push constants with an orthographic projection for 2D editor rendering.
    /// </summary>
    /// <param name="width">The window width in pixels.</param>
    /// <param name="height">The window height in pixels.</param>
    /// <returns>A <see cref="PushConstants"/> struct with identity model/view and orthographic projection.</returns>
    public static PushConstants CreatePushConstants(float width, float height)
    {
        var model = Matrix4x4.Identity;
        var view = Matrix4x4.Identity;

        var projection = Matrix4x4.CreateOrthographicOffCenter(
            0, width,  // left, right
            0, height, // Top-Left origin
            -1, 1);

        return new PushConstants
        {
            Model = model,
            View = view,
            Projection = projection
        };
    }
}

/// <summary>
/// Contains one editor UI tree and its named viewport elements.
/// </summary>
/// <param name="Root">Root editor panel.</param>
/// <param name="SceneViewport">Scene viewport panel.</param>
/// <param name="GameViewport">Game viewport panel.</param>
/// <param name="HierarchyTree">Scene hierarchy tree.</param>
/// <param name="FileSystemTree">Project-root filesystem tree.</param>
/// <param name="ProjectLabel">Title-bar label displaying the active node asset.</param>
/// <param name="PlayButton">Editor control that starts and stops play mode.</param>
/// <param name="PlayButtonIcon">State icon composed inside the play button.</param>
/// <param name="Inspector">Selection-bound scene property Inspector.</param>
/// <param name="TitleBar">Custom native-window title bar.</param>
/// <param name="Overlay">Canvas hosting floating editor UI.</param>
/// <param name="SceneSlot">Retained Scene viewport content.</param>
/// <param name="GameSlot">Retained Game viewport content.</param>
/// <param name="SceneToolbar">Scene viewport toolbar.</param>
/// <param name="InitialDockHost">Default host replaced by the restored live dock session.</param>
/// <param name="WorkspaceHost">Stable content slot receiving the active main dock host.</param>
/// <param name="ProfilerContent">Profiler toolbar and history layout.</param>
/// <param name="Profiler">Live CPU and allocation history view.</param>
/// <param name="HierarchyButton">Command that activates or restores Hierarchy.</param>
/// <param name="FileSystemButton">Command that activates or restores Files.</param>
/// <param name="SceneButton">Command that activates or restores Scene.</param>
/// <param name="GameButton">Command that activates or restores Game.</param>
/// <param name="InspectorButton">Command that activates or restores Inspector.</param>
/// <param name="ProfilerButton">Bottom-dock button that toggles the Profiler.</param>
/// <param name="ProfilerPauseButton">Profiler toolbar pause/record toggle.</param>
/// <param name="ProfilerPauseLabel">Text showing the current pause/record action.</param>
/// <param name="TitleMenuBar">Title-bar application menu strip.</param>
/// <param name="WindowMenu">Window menu containing panel visibility actions.</param>
/// <param name="WindowPanelItems">Check rows keyed by stable dock-panel identifier.</param>
/// <param name="ViewMenu">View menu containing Scene diagnostic controls.</param>
/// <param name="ScenePreviewMenu">Submenu containing preview category controls.</param>
/// <param name="ScenePreviewItems">Check rows keyed by preview category.</param>
public sealed record EditorView(
    Panel Root,
    ViewportPanel SceneViewport,
    ViewportPanel GameViewport,
    TreeView HierarchyTree,
    TreeView FileSystemTree,
    Label ProjectLabel,
    Button PlayButton,
    Icon PlayButtonIcon,
    SceneInspector Inspector,
    TitleBar TitleBar,
    Canvas Overlay,
    FlexPanel SceneSlot,
    FlexPanel GameSlot,
    Surface SceneToolbar,
    DockHost InitialDockHost,
    ContentControl WorkspaceHost,
    FlexPanel ProfilerContent,
    ProfilerView Profiler,
    Button HierarchyButton,
    Button FileSystemButton,
    Button SceneButton,
    Button GameButton,
    Button InspectorButton,
    Button ProfilerButton,
    Button ProfilerPauseButton,
    Label ProfilerPauseLabel,
    MenuBar TitleMenuBar,
    ContextMenu WindowMenu,
    IReadOnlyDictionary<string, ContextMenuItem> WindowPanelItems,
    ContextMenu ViewMenu,
    ContextMenu ScenePreviewMenu,
    IReadOnlyDictionary<ScenePreviewCategory, ContextMenuItem> ScenePreviewItems);
