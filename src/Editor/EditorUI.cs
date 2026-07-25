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
    private static Panel _background = null!;
    private static Panel _menuBar = null!;
    private static Panel _statusBar = null!;
    private static Panel _hierarchyPanel = null!;
    private static Panel _inspectorPanel = null!;
    private static Panel _viewport = null!;

    /// <summary>
    /// Builds the editor UI tree from the given window dimensions.
    /// </summary>
    /// <param name="width">The window width in pixels.</param>
    /// <param name="height">The window height in pixels.</param>
    /// <returns>The root <see cref="Panel"/> containing all editor UI elements.</returns>
    public static Panel BuildUI(float width, float height)
    {
        const float menuBarHeight = 30;
        const float statusBarHeight = 24;
        const float separatorWidth = 2;
        const float headerHeight = 22;
        const float hierarchyWidth = 220;
        const float inspectorWidth = 260;

        var viewportTop = menuBarHeight;
        var viewportBottom = height - statusBarHeight;
        var viewportHeight = viewportBottom - viewportTop;

        _background = new Panel(0, 0, width, height, Color.EditorBackground) { Name = "Background" };

        _menuBar = new Panel(0, 0, width, menuBarHeight, Color.Red) { Name = "MenuBar" };
        _statusBar = new Panel(0, height - statusBarHeight, width, statusBarHeight, Color.Green) { Name = "StatusBar" };

        _hierarchyPanel = new Panel(0, viewportTop, hierarchyWidth, viewportHeight, Color.Blue) { Name = "Hierarchy" };
        _inspectorPanel = new Panel(width - inspectorWidth, viewportTop, inspectorWidth, viewportHeight, Color.Black) { Name = "Inspector" };

        var separatorLeft = new Panel(hierarchyWidth, viewportTop, separatorWidth, viewportHeight, Color.White) { Name = "SeparatorLeft" };
        var separatorRight = new Panel(width - inspectorWidth - separatorWidth, viewportTop, separatorWidth, viewportHeight, Color.White) { Name = "SeparatorRight" };

        var viewportWidth = width - hierarchyWidth - inspectorWidth - (separatorWidth * 2);
        _viewport = new Panel(hierarchyWidth + separatorWidth, viewportTop, viewportWidth, viewportHeight, Color.EditorViewport) { Name = "Viewport" };

        // Assemble tree
        _background.AddChild(_menuBar);
        _background.AddChild(_statusBar);
        _background.AddChild(_hierarchyPanel);
        _background.AddChild(_inspectorPanel);
        _background.AddChild(separatorLeft);
        _background.AddChild(separatorRight);
        _background.AddChild(_viewport);

        return _background;
    }

    /// <summary>
    /// Collects all vertices from the UI tree for rendering.
    /// </summary>
    /// <param name="width">The window width in pixels.</param>
    /// <param name="height">The window height in pixels.</param>
    /// <returns>An array of <see cref="Vertex"/> for the entire editor UI.</returns>
    public static Vertex[] CreateVertices(float width, float height)
    {
        var root = BuildUI(width, height);
        return root.CollectVertices().ToArray();
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
