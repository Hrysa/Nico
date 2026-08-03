using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// A Panel subclass that represents a viewport area in the editor.
/// Tracks its viewport ID and provides resize notification so the
/// rendering system can recreate the underlying FBO.
/// </summary>
public class ViewportPanel : Panel
{
    private float _previousWidth;
    private float _previousHeight;

    /// <summary>
    /// Gets or sets the viewport ID assigned by the window's FBO manager.
    /// Zero means not yet registered.
    /// </summary>
    public uint ViewportId { get; set; }

    /// <summary>
    /// Gets or sets the camera used for rendering this viewport's content.
    /// </summary>
    public ICamera? Camera { get; set; }

    /// <summary>
    /// Gets whether the viewport panel has changed size since the
    /// last call to <see cref="CheckAndReportResize"/>.
    /// </summary>
    public bool HasResized { get; private set; }

    /// <summary>
    /// Creates a new ViewportPanel at the specified position and size.
    /// </summary>
    /// <param name="width">The panel width.</param>
    /// <param name="height">The panel height.</param>
    /// <param name="backgroundColor">The viewport background color.</param>
    public ViewportPanel(float width, float height, Color backgroundColor)
        : base(backgroundColor, width, height)
    {
        _previousWidth = width;
        _previousHeight = height;
    }

    /// <summary>
    /// Checks if the panel dimensions have changed since last check.
    /// Returns the new dimensions and resets the dirty flag.
    /// </summary>
    /// <param name="newWidth">The current width.</param>
    /// <param name="newHeight">The current height.</param>
    /// <returns>True if the panel was resized.</returns>
    public bool CheckAndReportResize(out float newWidth, out float newHeight)
    {
        newWidth = Width;
        newHeight = Height;
        HasResized = Width != _previousWidth || Height != _previousHeight;
        _previousWidth = Width;
        _previousHeight = Height;
        return HasResized;
    }
}
