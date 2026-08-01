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
        const float titleBarHeight = 30f;
        const float toolbarHeight = 48f;
        const float bottomDockHeight = 30f;
        const float separatorWidth = 1f;
        const float hierarchyWidth = 252f;
        const float inspectorWidth = 300f;
        const float panelHeaderHeight = 36f;
        const float viewportToolbarHeight = 36f;
        const float gameHeaderHeight = 30f;

        var workspaceTop = titleBarHeight + toolbarHeight;
        var workspaceHeight = MathF.Max(0f, height - workspaceTop - bottomDockHeight);
        var hierarchyHeight = MathF.Floor(workspaceHeight * 0.58f);
        var filesystemHeight = MathF.Max(0f, workspaceHeight - hierarchyHeight - separatorWidth);

        var background = new Panel(0, 0, width, height, theme.Canvas)
        {
            Name = "Background",
            ForegroundColor = theme.TextPrimary
        };

        var titleBar = new TitleBar(width, titleBarHeight, "scene.json — Game Engine", theme);
        var toolbar = new Surface(0, titleBarHeight, width, toolbarHeight, theme.Canvas, theme.Border)
        {
            Name = "Toolbar"
        };
        var fileButton = new Button(8f, 8f, 54f, 32f, "File", theme) { Name = "FileMenu" };
        var projectLabel = new Label(72f, 0f, 180f, toolbarHeight, "scene.json")
        {
            Name = "ProjectLabel",
            FontSize = theme.FontSize,
            ForegroundColor = theme.TextSecondary,
            PaddingLeft = 0f
        };
        var modeX = MathF.Max(270f, width / 2f - 176f);
        var sceneMode = new Button(modeX, 8f, 76f, 32f, "3D", theme, ButtonStyle.Primary)
            { Name = "SceneMode" };
        var scriptMode = new Button(modeX + 80f, 8f, 82f, 32f, "Script", theme)
            { Name = "ScriptMode" };
        var gameMode = new Button(modeX + 166f, 8f, 78f, 32f, "Game", theme)
            { Name = "GameMode" };
        var playButton = new Button(width - 68f, 8f, 58f, 32f, "Play", theme, ButtonStyle.Primary)
            { Name = "Play" };

        toolbar.AddChild(fileButton);
        toolbar.AddChild(projectLabel);
        toolbar.AddChild(sceneMode);
        toolbar.AddChild(scriptMode);
        toolbar.AddChild(gameMode);
        toolbar.AddChild(playButton);

        var bottomDock = new Surface(hierarchyWidth + separatorWidth, height - bottomDockHeight,
            MathF.Max(0f, width - hierarchyWidth - inspectorWidth - separatorWidth * 2f),
            bottomDockHeight, theme.Surface, theme.Border) { Name = "BottomDock" };
        bottomDock.AddChild(new Label(14f, 0f, bottomDock.Width - 28f, bottomDockHeight,
            "Output    Debugger    Audio    Animation")
        {
            Name = "BottomDockTabs",
            FontSize = theme.CaptionFontSize,
            ForegroundColor = theme.TextSecondary,
            PaddingLeft = 0f
        });

        var hierarchyPanel = new Surface(0, workspaceTop, hierarchyWidth, hierarchyHeight,
            theme.Surface, theme.Border) { Name = "Hierarchy", PaintBackground = false };
        var hierarchyHeader = new SectionHeader(0, 0, hierarchyWidth, panelHeaderHeight, "Scene", theme)
            { Name = "HierarchyHeader" };
        var hierarchyTree = new TreeView(0, panelHeaderHeight, hierarchyWidth,
            MathF.Max(0f, hierarchyHeight - panelHeaderHeight), theme)
        {
            Name = "HierarchyTree"
        };
        hierarchyPanel.AddChild(hierarchyHeader);
        hierarchyPanel.AddChild(hierarchyTree);

        var filesystemY = workspaceTop + hierarchyHeight + separatorWidth;
        var filesystemPanel = new Surface(0, filesystemY, hierarchyWidth, filesystemHeight,
            theme.Surface, theme.Border) { Name = "FileSystem", PaintBackground = false };
        filesystemPanel.AddChild(new SectionHeader(0f, 0f, hierarchyWidth, panelHeaderHeight,
            "FileSystem", theme) { Name = "FileSystemHeader" });
        filesystemPanel.AddChild(new Label(10f, panelHeaderHeight, hierarchyWidth - 20f, 30f, "res://")
        {
            Name = "ProjectRootPath",
            ForegroundColor = theme.TextSecondary,
            BackgroundColor = theme.SurfaceRaised,
            PaintBackground = true,
            PaddingLeft = 8f,
            FontSize = theme.FontSize
        });
        var fileList = new ListView(0f, panelHeaderHeight + 34f, hierarchyWidth,
            MathF.Max(0f, filesystemHeight - panelHeaderHeight - 34f), theme) { Name = "ProjectFiles" };
        fileList.SetItems(["scene.json", "assets", "scripts"]);
        filesystemPanel.AddChild(fileList);

        var inspectorPanel = new Surface(width - inspectorWidth, workspaceTop, inspectorWidth, workspaceHeight,
            theme.Surface, theme.Border) { Name = "Inspector", PaintBackground = false };
        inspectorPanel.AddChild(new SectionHeader(0, 0, inspectorWidth, panelHeaderHeight, "Inspector", theme)
            { Name = "InspectorHeader" });
        inspectorPanel.AddChild(new TextField(10f, panelHeaderHeight + 8f, inspectorWidth - 20f, 32f, theme)
        {
            Name = "PropertyFilter",
            Placeholder = "Filter Properties"
        });
        inspectorPanel.AddChild(new Label(12f, panelHeaderHeight + 52f, inspectorWidth - 24f, 28f,
            "Select an object to inspect")
        {
            Name = "InspectorEmptyState",
            ForegroundColor = theme.TextMuted,
            FontSize = theme.FontSize,
            PaddingLeft = 0f
        });

        var separatorLeft = new Separator(hierarchyWidth, workspaceTop, separatorWidth, workspaceHeight, theme)
            { Name = "SeparatorLeft" };
        var separatorRight = new Separator(width - inspectorWidth - separatorWidth, workspaceTop,
            separatorWidth, workspaceHeight, theme) { Name = "SeparatorRight" };

        var viewportWidth = MathF.Max(0f, width - hierarchyWidth - inspectorWidth - (separatorWidth * 2f));
        var viewportX = hierarchyWidth + separatorWidth;
        var sceneSlotHeight = MathF.Floor(workspaceHeight * 0.73f);
        var gameSlotHeight = MathF.Max(0f, workspaceHeight - sceneSlotHeight - separatorWidth);
        var sceneTools = new Surface(viewportX, workspaceTop, viewportWidth, viewportToolbarHeight,
            theme.SurfaceRaised, theme.Border) { Name = "SceneToolbar" };
        sceneTools.AddChild(new Button(8f, 4f, 62f, 28f, "Select", theme, ButtonStyle.Primary));
        sceneTools.AddChild(new Button(74f, 4f, 54f, 28f, "Move", theme));
        sceneTools.AddChild(new Button(132f, 4f, 62f, 28f, "Rotate", theme));
        sceneTools.AddChild(new Button(198f, 4f, 58f, 28f, "Scale", theme));
        sceneTools.AddChild(new Label(viewportWidth - 104f, 0f, 96f, viewportToolbarHeight,
            "Perspective")
        {
            ForegroundColor = theme.TextSecondary,
            FontSize = theme.CaptionFontSize,
            PaddingLeft = 0f
        });
        var sceneViewport = new ViewportPanel(viewportX, workspaceTop + viewportToolbarHeight,
            viewportWidth, MathF.Max(0f, sceneSlotHeight - viewportToolbarHeight), theme.Viewport)
            { Name = "SceneViewport" };
        var gameHeaderY = workspaceTop + sceneSlotHeight + separatorWidth;
        var gameHeader = new SectionHeader(viewportX, gameHeaderY, viewportWidth,
            gameHeaderHeight, "Game", theme) { Name = "GameHeader" };
        var gameViewport = new ViewportPanel(viewportX, gameHeaderY + gameHeaderHeight,
            viewportWidth, MathF.Max(0f, gameSlotHeight - gameHeaderHeight), theme.Viewport)
            { Name = "GameViewport" };

        // Assemble tree
        background.AddChild(titleBar);
        background.AddChild(toolbar);
        background.AddChild(bottomDock);
        background.AddChild(hierarchyPanel);
        background.AddChild(filesystemPanel);
        background.AddChild(inspectorPanel);
        background.AddChild(separatorLeft);
        background.AddChild(separatorRight);
        background.AddChild(sceneTools);
        background.AddChild(sceneViewport);
        background.AddChild(gameHeader);
        background.AddChild(gameViewport);

        return new EditorView(background, sceneViewport, gameViewport, hierarchyTree, fileButton, titleBar);
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
/// <param name="FileButton">Button opening scene file actions.</param>
/// <param name="TitleBar">Custom native-window title bar.</param>
public sealed record EditorView(
    Panel Root,
    ViewportPanel SceneViewport,
    ViewportPanel GameViewport,
    TreeView HierarchyTree,
    Button FileButton,
    TitleBar TitleBar);
