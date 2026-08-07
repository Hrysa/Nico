using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Displays five dock targets and a live insertion preview over a tab well.</summary>
public sealed class DockDropOverlay : UIElement
{
    private const float TargetGap = 4f;
    private readonly UITheme _theme;
    private UIClipRect _targetBounds;
    private DockDropZone? _activeZone;
    private float? _tabInsertionX;

    /// <summary>Gets whether a dock target is currently displayed.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Gets the currently highlighted zone.</summary>
    public DockDropZone? ActiveZone => _activeZone;

    /// <summary>Gets whether the overlay currently displays a tab insertion marker.</summary>
    public bool IsTabInsertion => _tabInsertionX.HasValue;

    /// <summary>Creates a non-interactive overlay using theme dock colors.</summary>
    /// <param name="theme">Theme supplying target colors.</param>
    public DockDropOverlay(UITheme? theme = null)
    {
        _theme = theme ?? UITheme.Dark;
        IsOverlay = true;
        IsHitTestVisible = false;
        IsVisible = false;
    }

    /// <summary>Displays targets centered over one logical dock target.</summary>
    /// <param name="targetBounds">Absolute logical target bounds.</param>
    public void Show(UIClipRect targetBounds)
    {
        if (targetBounds.IsEmpty)
            throw new ArgumentException("Dock target bounds must have positive area.", nameof(targetBounds));
        _targetBounds = targetBounds;
        _activeZone = null;
        _tabInsertionX = null;
        IsActive = true;
        IsVisible = true;
        InvalidateVisual();
    }

    /// <summary>Displays a vertical tab insertion marker within a header strip.</summary>
    /// <param name="headerBounds">Absolute logical header-strip bounds.</param>
    /// <param name="insertionX">Absolute logical insertion coordinate.</param>
    public void ShowTabInsertion(UIClipRect headerBounds, float insertionX)
    {
        if (headerBounds.IsEmpty)
            throw new ArgumentException("Dock header bounds must have positive area.", nameof(headerBounds));
        _targetBounds = headerBounds;
        _activeZone = null;
        _tabInsertionX = Math.Clamp(insertionX, headerBounds.Left, headerBounds.Right);
        IsActive = true;
        IsVisible = true;
        InvalidateVisual();
    }

    /// <summary>Hides all targets and clears active state.</summary>
    public void Hide()
    {
        if (!IsActive)
            return;
        IsActive = false;
        IsVisible = false;
        _activeZone = null;
        _tabInsertionX = null;
        InvalidateVisual();
    }

    /// <summary>Updates the active zone from an absolute pointer position.</summary>
    /// <param name="position">Absolute logical pointer position.</param>
    /// <returns>Hit zone, or null outside all targets.</returns>
    public DockDropZone? UpdatePointer(Vector2 position)
    {
        var zone = HitTestZone(position);
        if (_activeZone != zone)
        {
            _activeZone = zone;
            InvalidateVisual();
        }
        return zone;
    }

    /// <summary>Finds the dock target containing an absolute pointer position.</summary>
    /// <param name="position">Absolute logical pointer position.</param>
    /// <returns>Hit zone, or null.</returns>
    public DockDropZone? HitTestZone(Vector2 position)
    {
        if (!IsActive)
            return null;
        for (var index = 0; index < 5; index++)
        {
            var zone = (DockDropZone)index;
            if (GetZoneBounds(zone).Contains(position.X, position.Y))
                return zone;
        }
        return null;
    }

    /// <summary>Gets the preview bounds for a zone.</summary>
    /// <param name="zone">Dock zone.</param>
    /// <returns>Absolute insertion preview rectangle.</returns>
    public UIClipRect GetPreviewBounds(DockDropZone zone)
    {
        var width = _targetBounds.Right - _targetBounds.Left;
        var height = _targetBounds.Bottom - _targetBounds.Top;
        return zone switch
        {
            DockDropZone.Left => _targetBounds with
            {
                Right = _targetBounds.Left + width * 0.3f
            },
            DockDropZone.Right => _targetBounds with
            {
                Left = _targetBounds.Right - width * 0.3f
            },
            DockDropZone.Top => _targetBounds with
            {
                Bottom = _targetBounds.Top + height * 0.3f
            },
            DockDropZone.Bottom => _targetBounds with
            {
                Top = _targetBounds.Bottom - height * 0.3f
            },
            _ => _targetBounds
        };
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        if (!IsActive)
            return;
        if (_tabInsertionX is { } insertionX)
        {
            drawList.AddRectangle(insertionX - 1f, _targetBounds.Top,
                insertionX + 1f, _targetBounds.Bottom, Color.White);
            return;
        }
        if (_activeZone is { } active)
        {
            var preview = GetPreviewBounds(active);
            drawList.AddRectangle(preview.Left, preview.Top, preview.Right, preview.Bottom,
                _theme.SurfacePressed);
        }
        for (var index = 0; index < 5; index++)
        {
            var zone = (DockDropZone)index;
            var bounds = GetZoneBounds(zone);
            var color = zone == _activeZone ? Color.White : _theme.Accent;
            drawList.AddRoundedRectangle(
                bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, 4f, color);
        }
    }

    /// <summary>Calculates one target rectangle around the target center.</summary>
    /// <param name="zone">Dock zone.</param>
    /// <returns>Absolute target rectangle.</returns>
    private UIClipRect GetZoneBounds(DockDropZone zone)
    {
        var targetWidth = _targetBounds.Right - _targetBounds.Left;
        var targetHeight = _targetBounds.Bottom - _targetBounds.Top;
        var size = MathF.Max(20f, MathF.Min(48f, MathF.Min(targetWidth, targetHeight) / 3f));
        var centerX = (_targetBounds.Left + _targetBounds.Right) * 0.5f;
        var centerY = (_targetBounds.Top + _targetBounds.Bottom) * 0.5f;
        var offset = size + TargetGap;
        var x = centerX - size * 0.5f;
        var y = centerY - size * 0.5f;
        switch (zone)
        {
            case DockDropZone.Left:
                x -= offset;
                break;
            case DockDropZone.Right:
                x += offset;
                break;
            case DockDropZone.Top:
                y -= offset;
                break;
            case DockDropZone.Bottom:
                y += offset;
                break;
        }
        return new UIClipRect(x, y, x + size, y + size);
    }
}
