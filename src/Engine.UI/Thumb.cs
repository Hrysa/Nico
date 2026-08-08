using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Provides reusable pointer-captured drag behavior for sliders and scroll bars.</summary>
public sealed class Thumb : UIElement
{
    private readonly UITheme _theme;
    private bool _isDragging;
    private Vector2 _lastPosition;
    private readonly bool _isTransparent;
    private readonly bool _enableHoverState;
    private readonly Color? _overrideColor;

    /// <summary>Gets whether this thumb currently owns an active drag.</summary>
    public bool IsDragging => _isDragging;

    /// <summary>Gets or sets the pointer cursor requested while this thumb is hovered.</summary>
    public PointerCursorKind CursorKind { get; set; } = PointerCursorKind.Default;

    /// <summary>Occurs when a captured drag begins.</summary>
    public event Action? DragStarted;

    /// <summary>Occurs for each logical-pixel drag movement.</summary>
    public event Action<Vector2>? DragDelta;

    /// <summary>Occurs when the captured drag ends.</summary>
    public event Action? DragCompleted;

    /// <summary>Creates a draggable thumb.</summary>
    /// <param name="theme">Theme supplying thumb colors.</param>
    /// <param name="isTransparent">Whether to skip background paint for this thumb.</param>
    /// <param name="enableHoverState">Whether to alter color for hover and pressed states.</param>
    /// <param name="overrideColor">Optional fixed color replacing the usual state-based or theme color.</param>
    public Thumb(
        UITheme? theme = null,
        bool isTransparent = false,
        bool enableHoverState = true,
        Color? overrideColor = null)
    {
        _theme = theme ?? UITheme.Dark;
        _isTransparent = isTransparent;
        _enableHoverState = enableHoverState;
        _overrideColor = overrideColor;
        Pointer += OnPointer;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        if (_isTransparent)
            return;
        var color = _overrideColor ?? (_enableHoverState switch
        {
            true when IsPressed => _theme.SurfacePressed,
            true when IsHovered => _theme.SurfaceHover,
            _ => _theme.BorderStrong
        });
        drawList.AddRoundedRectangle(Left, Top, Right, Bottom,
            MathF.Min(MathF.Min(Width, Height) / 2f, 4f), color);
    }

    /// <summary>Starts, advances, or completes one captured drag.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="pointerEvent">Routed pointer data.</param>
    private void OnPointer(UIElement sender, UIPointerEventArgs pointerEvent)
    {
        if (pointerEvent.RoutePhase != UIRoutePhase.Target)
            return;
        if (pointerEvent.Kind == UIPointerEventKind.Press &&
            pointerEvent.Button == InputPointerButton.Primary)
        {
            _isDragging = true;
            _lastPosition = pointerEvent.Position;
            SetPressed(true);
            pointerEvent.CapturePointer();
            pointerEvent.Handled = true;
            DragStarted?.Invoke();
        }
        else if (pointerEvent.Kind == UIPointerEventKind.Move && _isDragging)
        {
            var delta = pointerEvent.Position - _lastPosition;
            _lastPosition = pointerEvent.Position;
            pointerEvent.Handled = true;
            if (delta != Vector2.Zero)
                DragDelta?.Invoke(delta);
        }
        else if (pointerEvent.Kind == UIPointerEventKind.Release && _isDragging)
        {
            _isDragging = false;
            SetPressed(false);
            pointerEvent.ReleasePointerCapture();
            pointerEvent.Handled = true;
            DragCompleted?.Invoke();
        }
    }
}
