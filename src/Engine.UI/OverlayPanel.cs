using Engine.Graphics;

namespace Engine.UI;

/// <summary>Layers every child into the same content rectangle.</summary>
public sealed class OverlayPanel : Panel
{
    /// <summary>Creates an overlay panel.</summary>
    /// <param name="backgroundColor">Optional painted background; null creates a layout-only panel.</param>
    public OverlayPanel(Color? backgroundColor = null)
        : base(backgroundColor)
    {
    }
}
