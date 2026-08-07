using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Provides reusable pointer-captured drag behavior for sliders and scroll bars.</summary>
public sealed class Thumb : UIElement
{
    private readonly UITheme _theme;
    private bool _isDragging;
    private Vector2 _lastPosition;

    /// <summary>Gets whether this thumb currently owns an active drag.</summary>
    public bool IsDragging => _isDragging;

    /// <summary>Occurs when a captured drag begins.</summary>
    public event Action? DragStarted;

    /// <summary>Occurs for each logical-pixel drag movement.</summary>
    public event Action<Vector2>? DragDelta;

    /// <summary>Occurs when the captured drag ends.</summary>
    public event Action? DragCompleted;

    /// <summary>Creates a draggable thumb.</summary>
    /// <param name="theme">Theme supplying thumb colors.</param>
    public Thumb(UITheme? theme = null)
    {
        _theme = theme ?? UITheme.Dark;
        Pointer += OnPointer;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var color = IsPressed ? _theme.SurfacePressed : IsHovered ? _theme.SurfaceHover : _theme.BorderStrong;
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
