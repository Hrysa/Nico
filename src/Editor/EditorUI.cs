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
        var titleBarHeight = OperatingSystem.IsWindows() ? 36f : 48f;
        const float bottomDockHeight = 30f;
        const float separatorWidth = 1f;
        const float hierarchyWidth = 252f;
        const float inspectorWidth = 300f;
        const float viewportToolbarHeight = 36f;
        var panelHeaderHeight = theme.PanelHeaderHeight;

        var workspaceHeight = MathF.Max(0f, height - titleBarHeight - bottomDockHeight);
        var hierarchyHeight = MathF.Floor(workspaceHeight * 0.58f);
        var filesystemHeight = MathF.Max(0f, workspaceHeight - hierarchyHeight - separatorWidth);

        var background = new Grid(theme.Canvas)
        {
            Name = "Background",
            ForegroundColor = theme.TextPrimary
        };
        background.Rows.Add(GridLength.Pixels(titleBarHeight));
        background.Rows.Add(GridLength.Star());
        background.Rows.Add(GridLength.Pixels(bottomDockHeight));
        background.Columns.Add(GridLength.Star());

        var titleBar = new TitleBar(width, titleBarHeight, theme);
        titleBar.Width = 0f;
        titleBar.Margin = new Thickness(0f, 0f, 0f, 1f);
        var projectLabel = new Label("scene.node", 180f, titleBarHeight)
        {
            Name = "ProjectLabel",
            FontSize = theme.FontSize,
            ForegroundColor = theme.TextSecondary,
            PaddingLeft = 0f
        };
        var playButtonLabel = new Label("Play")
        {
            FontSize = theme.FontSize,
            ForegroundColor = theme.Accent,
            PaddingLeft = 0f,
            IsHitTestVisible = false
        };
        var playButton = new Button(28f, theme, ButtonStyle.Primary)
        {
            Name = "Play",
            Content = playButtonLabel
        };

        titleBar.LeftZone.AddChild(projectLabel);
        titleBar.CenterZone.AddChild(playButton);

        var bottomDock = new Surface(theme.Surface, theme.Border,
            MathF.Max(0f, width - hierarchyWidth - inspectorWidth - separatorWidth * 2f),
            bottomDockHeight) { Name = "BottomDock" };
        bottomDock.Width = 0f;
        var bottomTabs = new Grid(theme.Surface);
        bottomTabs.Rows.Add(GridLength.Star());
        bottomTabs.Columns.Add(GridLength.Pixels(90f));
        bottomTabs.Columns.Add(GridLength.Pixels(90f));
        bottomTabs.Columns.Add(GridLength.Pixels(90f));
        bottomTabs.Columns.Add(GridLength.Pixels(90f));
        bottomTabs.Columns.Add(GridLength.Pixels(90f));
        bottomTabs.Columns.Add(GridLength.Pixels(90f));
        bottomTabs.Columns.Add(GridLength.Star());
        var hierarchyButton = new Button(90f, bottomDockHeight, "Hierarchy", theme)
            { Name = "HierarchyButton" };
        var fileSystemButton = new Button(90f, bottomDockHeight, "Files", theme)
            { Name = "FileSystemButton" };
        var sceneButton = new Button(90f, bottomDockHeight, "Scene", theme)
            { Name = "SceneButton" };
        var gameButton = new Button(90f, bottomDockHeight, "Game", theme)
            { Name = "GameButton" };
        var inspectorButton = new Button(90f, bottomDockHeight, "Inspector", theme)
            { Name = "InspectorButton" };
        var profilerButton = new Button(90f, bottomDockHeight, "Profiler", theme)
        {
            Name = "ProfilerButton"
        };
        bottomTabs.Add(hierarchyButton, 0, 0);
        bottomTabs.Add(fileSystemButton, 0, 1);
        bottomTabs.Add(sceneButton, 0, 2);
        bottomTabs.Add(gameButton, 0, 3);
        bottomTabs.Add(inspectorButton, 0, 4);
        bottomTabs.Add(profilerButton, 0, 5);
        bottomDock.AddChild(bottomTabs);

        var profilerPanel = new ToolPanel(width, 0f, "Profiler", theme)
        {
            Name = "Profiler",
            IsVisible = false
        };
        profilerPanel.Width = 0f;
        profilerPanel.Height = 0f;
        var profilerPauseLabel = new Label("Pause")
        {
            FontSize = theme.CaptionFontSize,
            ForegroundColor = theme.TextPrimary,
            PaddingLeft = 0f,
            IsHitTestVisible = false
        };
        var profilerPauseButton = new Button(80f, theme.ControlHeight, theme)
        {
            Name = "ProfilerPause",
            Content = profilerPauseLabel
        };
        var profiler = new ProfilerView(theme) { Name = "ProfilerView" };
        var profilerLayout = new Grid(theme.Surface);
        profilerLayout.Rows.Add(GridLength.Pixels(theme.ControlHeight));
        profilerLayout.Rows.Add(GridLength.Star());
        profilerLayout.Columns.Add(GridLength.Star());
        var profilerToolbar = new Grid(theme.SurfaceRaised);
        profilerToolbar.Rows.Add(GridLength.Star());
        profilerToolbar.Columns.Add(GridLength.Pixels(88f));
        profilerToolbar.Columns.Add(GridLength.Star());
        profilerToolbar.Add(profilerPauseButton, 0, 0);
        profilerLayout.Add(profilerToolbar, 0, 0);
        profilerLayout.Add(profiler, 1, 0);
        profilerPanel.Content.AddChild(profilerLayout);

        var hierarchyPanel = new ToolPanel(hierarchyWidth, hierarchyHeight,
            "Hierarchy", theme) { Name = "Hierarchy" };
        hierarchyPanel.Content.Name = "Hierarchy";
        hierarchyPanel.Height = 0f;
        hierarchyPanel.Header.Name = "HierarchyHeader";
        var hierarchyTree = new TreeView(hierarchyWidth,
            MathF.Max(0f, hierarchyHeight - panelHeaderHeight), theme)
        {
            Name = "HierarchyTree"
        };
        hierarchyTree.Width = 0f;
        hierarchyTree.Height = 0f;
        hierarchyPanel.Content.AddChild(hierarchyTree);

        var filesystemPanel = new ToolPanel(hierarchyWidth, filesystemHeight,
            "File System", theme) { Name = "FileSystem" };
        filesystemPanel.Content.Name = "FileSystem";
        filesystemPanel.Height = 0f;
        filesystemPanel.Header.Name = "FileSystemHeader";
        var fileTree = new TreeView(hierarchyWidth,
            MathF.Max(0f, filesystemHeight - panelHeaderHeight), theme) { Name = "ProjectFiles" };
        fileTree.Width = 0f;
        fileTree.Height = 0f;
        filesystemPanel.Content.AddChild(fileTree);

        var inspectorPanel = new ToolPanel(inspectorWidth,
            workspaceHeight, "Inspector", theme) { Name = "Inspector" };
        inspectorPanel.Content.Name = "Inspector";
        inspectorPanel.Height = 0f;
        inspectorPanel.Header.Name = "InspectorHeader";
        var inspector = new SceneInspector(inspectorWidth,
            MathF.Max(0f, workspaceHeight - panelHeaderHeight), theme)
        {
            Name = "SceneInspector"
        };
        inspector.Width = 0f;
        inspector.Height = 0f;
        inspectorPanel.Content.AddChild(inspector);

        var viewportWidth = MathF.Max(0f, width - hierarchyWidth - inspectorWidth - (separatorWidth * 2f));
        var sceneSlotHeight = MathF.Floor(workspaceHeight * 0.73f);
        var gameSlotHeight = MathF.Max(0f, workspaceHeight - sceneSlotHeight - separatorWidth);
        var sceneTools = new Surface(theme.SurfaceRaised, theme.Border,
            viewportWidth, viewportToolbarHeight) { Name = "SceneToolbar" };
        sceneTools.Width = 0f;
        var selectTool = new Button(28f, "Select", theme, ButtonStyle.Primary);
        var moveTool = new Button(28f, "Move", theme);
        var rotateTool = new Button(28f, "Rotate", theme);
        var scaleTool = new Button(28f, "Scale", theme);
        var toolbarButtonSpace = new Vector2(float.PositiveInfinity, viewportToolbarHeight);
        selectTool.Measure(toolbarButtonSpace);
        moveTool.Measure(toolbarButtonSpace);
        rotateTool.Measure(toolbarButtonSpace);
        scaleTool.Measure(toolbarButtonSpace);
        var toolbarLayout = new Grid(theme.SurfaceRaised) { Name = "SceneToolbarLayout" };
        toolbarLayout.Rows.Add(GridLength.Star());
        toolbarLayout.Columns.Add(GridLength.Pixels(8f));
        toolbarLayout.Columns.Add(GridLength.Pixels(selectTool.DesiredSize.X));
        toolbarLayout.Columns.Add(GridLength.Pixels(4f));
        toolbarLayout.Columns.Add(GridLength.Pixels(moveTool.DesiredSize.X));
        toolbarLayout.Columns.Add(GridLength.Pixels(4f));
        toolbarLayout.Columns.Add(GridLength.Pixels(rotateTool.DesiredSize.X));
        toolbarLayout.Columns.Add(GridLength.Pixels(4f));
        toolbarLayout.Columns.Add(GridLength.Pixels(scaleTool.DesiredSize.X));
        toolbarLayout.Columns.Add(GridLength.Star());
        toolbarLayout.Columns.Add(GridLength.Pixels(96f));
        toolbarLayout.Columns.Add(GridLength.Pixels(8f));
        toolbarLayout.Add(selectTool, 0, 1);
        toolbarLayout.Add(moveTool, 0, 3);
        toolbarLayout.Add(rotateTool, 0, 5);
        toolbarLayout.Add(scaleTool, 0, 7);
        toolbarLayout.Add(new Label("Perspective", 96f, viewportToolbarHeight)
        {
            ForegroundColor = theme.TextSecondary,
            FontSize = theme.CaptionFontSize,
            PaddingLeft = 0f
        }, 0, 9);
        sceneTools.AddChild(toolbarLayout);
        var sceneViewport = new ViewportPanel(viewportWidth,
            MathF.Max(0f, sceneSlotHeight - viewportToolbarHeight), theme.Viewport)
            { Name = "SceneViewport" };
        sceneViewport.Width = 0f;
        sceneViewport.Height = 0f;
        var gameViewport = new ViewportPanel(viewportWidth,
            gameSlotHeight, theme.Viewport)
            { Name = "GameViewport" };
        gameViewport.Width = 0f;
        gameViewport.Height = 0f;

        var sceneSlot = new Grid(theme.Viewport) { Name = "SceneSlot" };
        sceneSlot.Columns.Add(GridLength.Star());
        sceneSlot.Rows.Add(GridLength.Pixels(viewportToolbarHeight));
        sceneSlot.Rows.Add(GridLength.Star());
        sceneSlot.Add(sceneTools, 0, 0);
        sceneSlot.Add(sceneViewport, 1, 0);

        var gameSlot = new Grid(theme.Viewport) { Name = "GameSlot" };
        gameSlot.Columns.Add(GridLength.Star());
        gameSlot.Rows.Add(GridLength.Star());
        gameSlot.Add(gameViewport, 0, 0);

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

        var bottomShell = new Grid(theme.Canvas) { Name = "BottomShell" };
        bottomShell.Rows.Add(GridLength.Star());
        bottomShell.Columns.Add(GridLength.Pixels(hierarchyWidth));
        bottomShell.Columns.Add(GridLength.Pixels(separatorWidth));
        bottomShell.Columns.Add(GridLength.Star());
        bottomShell.Columns.Add(GridLength.Pixels(separatorWidth));
        bottomShell.Columns.Add(GridLength.Pixels(inspectorWidth));
        bottomShell.Add(bottomDock, 0, 2);

        var overlay = new Canvas { Name = "Overlay" };

        background.Add(titleBar, 0, 0);
        background.Add(initialDockHost, 1, 0);
        background.Add(bottomShell, 2, 0);
        background.Add(overlay, 0, 0, rowSpan: 3);
        background.Measure(new Vector2(width, height));
        background.Arrange(Vector2.Zero, new Vector2(width, height));

        return new EditorView(background, sceneViewport, gameViewport, hierarchyTree, fileTree,
            projectLabel, playButton, playButtonLabel, inspector, titleBar, overlay,
            sceneSlot, gameSlot, sceneTools, initialDockHost,
            hierarchyPanel, filesystemPanel, inspectorPanel, profilerPanel, profilerLayout,
            profiler, hierarchyButton, fileSystemButton, sceneButton, gameButton,
            inspectorButton, profilerButton,
            profilerPauseButton, profilerPauseLabel);
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
/// <param name="PlayButtonLabel">Text content composed inside the play button.</param>
/// <param name="Inspector">Selection-bound scene property Inspector.</param>
/// <param name="TitleBar">Custom native-window title bar.</param>
/// <param name="Overlay">Canvas hosting floating editor UI.</param>
/// <param name="ViewportDock">Main viewport docking grid.</param>
/// <param name="SceneSlot">Detachable Scene tool content.</param>
/// <param name="GameSlot">Detachable Game tool content.</param>
/// <param name="SceneToolbar">Scene tool header used to detach its window.</param>
/// <param name="SceneSlot">Retained Scene viewport content.</param>
/// <param name="GameSlot">Retained Game viewport content.</param>
/// <param name="SceneToolbar">Scene viewport toolbar.</param>
/// <param name="InitialDockHost">Default host replaced by the restored live dock session.</param>
/// <param name="HierarchyPanel">Detachable Hierarchy tool.</param>
/// <param name="FileSystemPanel">Detachable File System tool.</param>
/// <param name="InspectorPanel">Detachable Inspector tool.</param>
/// <param name="ProfilerPanel">Dockable Profiler tool panel.</param>
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
public sealed record EditorView(
    Panel Root,
    ViewportPanel SceneViewport,
    ViewportPanel GameViewport,
    TreeView HierarchyTree,
    TreeView FileSystemTree,
    Label ProjectLabel,
    Button PlayButton,
    Label PlayButtonLabel,
    SceneInspector Inspector,
    TitleBar TitleBar,
    Canvas Overlay,
    Grid SceneSlot,
    Grid GameSlot,
    Surface SceneToolbar,
    DockHost InitialDockHost,
    ToolPanel HierarchyPanel,
    ToolPanel FileSystemPanel,
    ToolPanel InspectorPanel,
    ToolPanel ProfilerPanel,
    Grid ProfilerContent,
    ProfilerView Profiler,
    Button HierarchyButton,
    Button FileSystemButton,
    Button SceneButton,
    Button GameButton,
    Button InspectorButton,
    Button ProfilerButton,
    Button ProfilerPauseButton,
    Label ProfilerPauseLabel);
