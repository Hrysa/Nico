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
        bottomDock.AddChild(new Label("Output    Debugger    Audio    Animation",
            bottomDock.Width - 28f, bottomDockHeight)
        {
            Name = "BottomDockTabs",
            FontSize = theme.CaptionFontSize,
            ForegroundColor = theme.TextSecondary,
            PaddingLeft = 0f
        });

        var hierarchyPanel = new ToolPanel(hierarchyWidth, hierarchyHeight,
            "Hierarchy", theme) { Name = "Hierarchy" };
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
        filesystemPanel.Height = 0f;
        filesystemPanel.Header.Name = "FileSystemHeader";
        var fileTree = new TreeView(hierarchyWidth,
            MathF.Max(0f, filesystemHeight - panelHeaderHeight), theme) { Name = "ProjectFiles" };
        fileTree.Width = 0f;
        fileTree.Height = 0f;
        filesystemPanel.Content.AddChild(fileTree);

        var inspectorPanel = new ToolPanel(inspectorWidth,
            workspaceHeight, "Inspector", theme) { Name = "Inspector" };
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

        var separatorLeft = new Separator(separatorWidth, workspaceHeight, theme)
            { Name = "SeparatorLeft" };
        separatorLeft.Height = 0f;
        var separatorRight = new Separator(separatorWidth, workspaceHeight, theme) { Name = "SeparatorRight" };
        separatorRight.Height = 0f;

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
        var gameHeader = new SectionHeader(viewportWidth,
            "Game", theme) { Name = "GameHeader" };
        gameHeader.Width = 0f;
        var gameViewport = new ViewportPanel(viewportWidth,
            MathF.Max(0f, gameSlotHeight - panelHeaderHeight), theme.Viewport)
            { Name = "GameViewport" };
        gameViewport.Width = 0f;
        gameViewport.Height = 0f;

        var leftDock = new Grid(theme.Canvas) { Name = "LeftDock" };
        leftDock.Columns.Add(GridLength.Star());
        leftDock.Rows.Add(GridLength.Star(0.58f));
        leftDock.Rows.Add(GridLength.Pixels(separatorWidth));
        leftDock.Rows.Add(GridLength.Star(0.42f));
        leftDock.Add(hierarchyPanel, 0, 0);
        leftDock.Add(new Separator(hierarchyWidth, separatorWidth, theme), 1, 0);
        leftDock.Add(filesystemPanel, 2, 0);

        var sceneSlot = new Grid(theme.Viewport) { Name = "SceneSlot" };
        sceneSlot.Columns.Add(GridLength.Star());
        sceneSlot.Rows.Add(GridLength.Pixels(viewportToolbarHeight));
        sceneSlot.Rows.Add(GridLength.Star());
        sceneSlot.Add(sceneTools, 0, 0);
        sceneSlot.Add(sceneViewport, 1, 0);

        var gameSlot = new Grid(theme.Viewport) { Name = "GameSlot" };
        gameSlot.Columns.Add(GridLength.Star());
        gameSlot.Rows.Add(GridLength.Pixels(panelHeaderHeight));
        gameSlot.Rows.Add(GridLength.Star());
        gameSlot.Add(gameHeader, 0, 0);
        gameSlot.Add(gameViewport, 1, 0);

        var viewportDock = new Grid(theme.Viewport) { Name = "ViewportDock" };
        viewportDock.Columns.Add(GridLength.Star());
        viewportDock.Rows.Add(GridLength.Star(0.73f));
        viewportDock.Rows.Add(GridLength.Pixels(separatorWidth));
        viewportDock.Rows.Add(GridLength.Star(0.27f));
        viewportDock.Add(sceneSlot, 0, 0);
        viewportDock.Add(new Separator(viewportWidth, separatorWidth, theme), 1, 0);
        viewportDock.Add(gameSlot, 2, 0);

        var workspace = new Grid(theme.Canvas) { Name = "Workspace" };
        workspace.Rows.Add(GridLength.Star());
        workspace.Columns.Add(GridLength.Pixels(hierarchyWidth));
        workspace.Columns.Add(GridLength.Pixels(separatorWidth));
        workspace.Columns.Add(GridLength.Star());
        workspace.Columns.Add(GridLength.Pixels(separatorWidth));
        workspace.Columns.Add(GridLength.Pixels(inspectorWidth));
        workspace.Add(leftDock, 0, 0);
        workspace.Add(separatorLeft, 0, 1);
        workspace.Add(viewportDock, 0, 2);
        workspace.Add(separatorRight, 0, 3);
        workspace.Add(inspectorPanel, 0, 4);

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
        background.Add(workspace, 1, 0);
        background.Add(bottomShell, 2, 0);
        background.Add(overlay, 0, 0, rowSpan: 3);
        background.Measure(new Vector2(width, height));
        background.Arrange(Vector2.Zero, new Vector2(width, height));

        return new EditorView(background, sceneViewport, gameViewport, hierarchyTree, fileTree,
            projectLabel, playButton, playButtonLabel, inspector, titleBar, overlay,
            viewportDock, sceneSlot, gameSlot, sceneTools, gameHeader,
            workspace, leftDock, hierarchyPanel, filesystemPanel, inspectorPanel);
    }

    /// <summary>
    /// Builds only the root UI panel for callers that do not need named editor elements.
    /// </summary>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <returns>The root UI panel.</returns>
    public static Panel BuildUI(float width, float height) => BuildView(width, height).Root;

    /// <summary>
    /// Creates textured quad vertices for a specific viewport panel.
    /// </summary>
    /// <param name="viewportPanel">The viewport panel to create vertices for.</param>
    /// <returns>An array of VertexT for the viewport's display quad.</returns>
    public static VertexT[] CreateViewportQuadVertices(ViewportPanel viewportPanel)
    {
        var left = viewportPanel.Left;
        var top = viewportPanel.Top;
        var right = viewportPanel.Right;
        var bottom = viewportPanel.Bottom;

        return new VertexT[]
        {
            new(new Vector3(left, top, 0), new Vector2(0, 0)),
            new(new Vector3(left, bottom, 0), new Vector2(0, 1)),
            new(new Vector3(right, bottom, 0), new Vector2(1, 1)),

            new(new Vector3(right, bottom, 0), new Vector2(1, 1)),
            new(new Vector3(right, top, 0), new Vector2(1, 0)),
            new(new Vector3(left, top, 0), new Vector2(0, 0)),
        };
    }

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
/// <param name="GameHeader">Game tool header used to detach its window.</param>
/// <param name="Workspace">Main editor workspace grid.</param>
/// <param name="LeftDock">Hierarchy and File System docking grid.</param>
/// <param name="HierarchyPanel">Detachable Hierarchy tool.</param>
/// <param name="FileSystemPanel">Detachable File System tool.</param>
/// <param name="InspectorPanel">Detachable Inspector tool.</param>
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
    Grid ViewportDock,
    Grid SceneSlot,
    Grid GameSlot,
    Surface SceneToolbar,
    SectionHeader GameHeader,
    Grid Workspace,
    Grid LeftDock,
    ToolPanel HierarchyPanel,
    ToolPanel FileSystemPanel,
    ToolPanel InspectorPanel);
