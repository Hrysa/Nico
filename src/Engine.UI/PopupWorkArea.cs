using System.Numerics;

namespace Engine.UI;

using Engine.Graphics;

/// <summary>Describes one monitor work area in host-logical coordinates and its DPI scale.</summary>
/// <param name="Left">Logical left edge.</param>
/// <param name="Top">Logical top edge.</param>
/// <param name="Right">Logical right edge.</param>
/// <param name="Bottom">Logical bottom edge.</param>
/// <param name="DpiScale">Physical pixels per logical pixel.</param>
public readonly record struct UIPopupWorkArea(
    float Left, float Top, float Right, float Bottom, float DpiScale = 1f)
{
    /// <summary>Gets the non-negative logical width.</summary>
    public float Width => MathF.Max(0f, Right - Left);

    /// <summary>Gets the non-negative logical height.</summary>
    public float Height => MathF.Max(0f, Bottom - Top);

    /// <summary>Converts a logical host point to physical monitor pixels.</summary>
    /// <param name="logicalPoint">Logical point.</param>
    /// <returns>Physical pixel point.</returns>
    public Vector2 LogicalToPhysical(Vector2 logicalPoint) => logicalPoint * MathF.Max(float.Epsilon, DpiScale);

    /// <summary>Converts a physical monitor point to logical host coordinates.</summary>
    /// <param name="physicalPoint">Physical pixel point.</param>
    /// <returns>Logical point.</returns>
    public Vector2 PhysicalToLogical(Vector2 physicalPoint) => physicalPoint / MathF.Max(float.Epsilon, DpiScale);
}

/// <summary>Resolves the monitor-specific logical work area containing a popup anchor.</summary>
public interface IPopupWorkAreaProvider
{
    /// <summary>Gets the work area appropriate for one host-logical anchor point.</summary>
    /// <param name="anchorPoint">Owner or pointer anchor in host coordinates.</param>
    /// <returns>Logical work area and DPI scale.</returns>
    UIPopupWorkArea GetWorkArea(Vector2 anchorPoint);
}

/// <summary>Adapts renderer-level display information to popup work-area placement.</summary>
public sealed class DisplayPopupWorkAreaProvider : IPopupWorkAreaProvider
{
    private readonly IDisplayService _displayService;

    /// <summary>Creates a popup provider over one host display service.</summary>
    /// <param name="displayService">Host-local display service.</param>
    public DisplayPopupWorkAreaProvider(IDisplayService displayService)
    {
        ArgumentNullException.ThrowIfNull(displayService);
        _displayService = displayService;
    }

    /// <inheritdoc/>
    public UIPopupWorkArea GetWorkArea(Vector2 anchorPoint)
    {
        var area = _displayService.GetWorkArea(anchorPoint);
        return new UIPopupWorkArea(area.Left, area.Top, area.Right, area.Bottom, area.DpiScale);
    }
}

/// <summary>Maps display work areas through a runtime viewport's logical UI scale.</summary>
internal sealed class ViewportDisplayPopupWorkAreaProvider : IPopupWorkAreaProvider
{
    private readonly IDisplayService _displayService;
    private readonly Func<UIViewportLayout> _getLayout;

    /// <summary>Creates a policy-aware display work-area adapter.</summary>
    /// <param name="displayService">Host-local display service.</param>
    /// <param name="getLayout">Gets the current resolved viewport layout.</param>
    internal ViewportDisplayPopupWorkAreaProvider(
        IDisplayService displayService,
        Func<UIViewportLayout> getLayout)
    {
        ArgumentNullException.ThrowIfNull(displayService);
        ArgumentNullException.ThrowIfNull(getLayout);
        _displayService = displayService;
        _getLayout = getLayout;
    }

    /// <inheritdoc/>
    public UIPopupWorkArea GetWorkArea(Vector2 anchorPoint)
    {
        var layout = _getLayout();
        var area = _displayService.GetWorkArea(anchorPoint * layout.Scale);
        return new UIPopupWorkArea(
            area.Left / layout.Scale,
            area.Top / layout.Scale,
            area.Right / layout.Scale,
            area.Bottom / layout.Scale,
            area.DpiScale * layout.Scale);
    }
}
