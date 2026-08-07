using Engine.Graphics;

namespace Engine.UI;

/// <summary>Selects retained UI diagnostics drawn by <see cref="UIDebugOverlay"/>.</summary>
[Flags]
public enum UIDebugOverlayOptions
{
    /// <summary>Draws no diagnostics.</summary>
    None = 0,

    /// <summary>Draws every visible element's arranged bounds.</summary>
    Bounds = 1,

    /// <summary>Draws effective rectangles established by clipping elements.</summary>
    Clips = 2,

    /// <summary>Highlights the current routed pointer target.</summary>
    HitTarget = 4,

    /// <summary>Highlights the focused element.</summary>
    Focus = 8,

    /// <summary>Highlights the element holding pointer capture.</summary>
    Capture = 16,

    /// <summary>Draws all available diagnostics.</summary>
    All = Bounds | Clips | HitTarget | Focus | Capture
}

/// <summary>Draws non-interactive retained-tree bounds and input-state diagnostics.</summary>
public sealed class UIDebugOverlay : UIElement, IDisposable
{
    private static readonly Color BoundsColor = Color.FromSrgb(90, 110, 140);
    private static readonly Color ClipColor = Color.FromSrgb(255, 145, 45);
    private static readonly Color HitColor = Color.Yellow;
    private static readonly Color FocusColor = Color.Cyan;
    private static readonly Color CaptureColor = Color.Magenta;
    private readonly UIElement _targetRoot;
    private readonly UIEventRouter _router;
    private UIDebugOverlayOptions _options = UIDebugOverlayOptions.All;
    private bool _disposed;

    /// <summary>Creates a debug overlay for one routed UI tree.</summary>
    /// <param name="targetRoot">Root whose retained geometry is inspected.</param>
    /// <param name="router">Router supplying current input state.</param>
    public UIDebugOverlay(UIElement targetRoot, UIEventRouter router)
    {
        ArgumentNullException.ThrowIfNull(targetRoot);
        ArgumentNullException.ThrowIfNull(router);
        _targetRoot = targetRoot;
        _router = router;
        IsOverlay = true;
        IsHitTestVisible = false;
        _router.DiagnosticStateChanged += OnDiagnosticStateChanged;
    }

    /// <summary>Gets or sets the diagnostics included in the overlay.</summary>
    public UIDebugOverlayOptions Options
    {
        get => _options;
        set
        {
            if (_options == value)
                return;
            _options = value;
            InvalidateVisual();
        }
    }

    /// <summary>Stops observing router diagnostic changes.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _router.DiagnosticStateChanged -= OnDiagnosticStateChanged;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        PaintElement(drawList, _targetRoot, null);
        if ((_options & UIDebugOverlayOptions.HitTarget) != 0)
            PaintHighlight(drawList, _router.HoveredElement, HitColor, 2f);
        if ((_options & UIDebugOverlayOptions.Focus) != 0)
            PaintHighlight(drawList, _router.FocusedElement, FocusColor, 2f);
        if ((_options & UIDebugOverlayOptions.Capture) != 0)
            PaintHighlight(drawList, _router.CapturedElement, CaptureColor, 3f);
    }

    /// <summary>Draws diagnostics for one visible subtree without iterator allocation.</summary>
    /// <param name="drawList">Draw list receiving diagnostic strokes.</param>
    /// <param name="element">Current retained element.</param>
    /// <param name="inheritedClip">Effective ancestor clip.</param>
    private void PaintElement(
        UIDrawList drawList,
        UIElement element,
        UIClipRect? inheritedClip)
    {
        if (!element.IsVisible || ReferenceEquals(element, this))
            return;
        var bounds = new UIClipRect(element.Left, element.Top, element.Right, element.Bottom);
        var effectiveClip = element.ClipToBounds
            ? inheritedClip is { } parentClip
                ? UIClipRect.Intersect(parentClip, bounds)
                : bounds
            : inheritedClip;
        if ((_options & UIDebugOverlayOptions.Bounds) != 0)
            AddOutline(drawList, bounds, BoundsColor, 1f);
        if ((_options & UIDebugOverlayOptions.Clips) != 0 && element.ClipToBounds &&
            effectiveClip is { IsEmpty: false } clip)
            AddOutline(drawList, clip, ClipColor, 1f);
        var children = element.Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child)
                PaintElement(drawList, child, effectiveClip);
        }
    }

    /// <summary>Draws an emphasized element outline when the element is available.</summary>
    /// <param name="drawList">Draw list receiving diagnostic strokes.</param>
    /// <param name="element">Element to highlight.</param>
    /// <param name="color">Highlight color.</param>
    /// <param name="thickness">Stroke thickness.</param>
    private static void PaintHighlight(
        UIDrawList drawList,
        UIElement? element,
        Color color,
        float thickness)
    {
        if (element is null || !element.IsVisible)
            return;
        AddOutline(drawList,
            new UIClipRect(element.Left, element.Top, element.Right, element.Bottom),
            color, thickness);
    }

    /// <summary>Adds four semantic line commands around a rectangle.</summary>
    /// <param name="drawList">Draw list receiving lines.</param>
    /// <param name="rectangle">Outlined rectangle.</param>
    /// <param name="color">Stroke color.</param>
    /// <param name="thickness">Stroke thickness.</param>
    private static void AddOutline(
        UIDrawList drawList,
        UIClipRect rectangle,
        Color color,
        float thickness)
    {
        if (rectangle.IsEmpty)
            return;
        drawList.AddLine(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Top,
            thickness, color);
        drawList.AddLine(rectangle.Right, rectangle.Top, rectangle.Right, rectangle.Bottom,
            thickness, color);
        drawList.AddLine(rectangle.Right, rectangle.Bottom, rectangle.Left, rectangle.Bottom,
            thickness, color);
        drawList.AddLine(rectangle.Left, rectangle.Bottom, rectangle.Left, rectangle.Top,
            thickness, color);
    }

    /// <summary>Invalidates cached diagnostic commands after routed state changes.</summary>
    private void OnDiagnosticStateChanged() => InvalidateVisual();
}
