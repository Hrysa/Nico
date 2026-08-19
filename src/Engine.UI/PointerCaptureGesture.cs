using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Owns the shared primary-pointer capture lifecycle for held and dragged controls.</summary>
public sealed class PointerCaptureGesture
{
    private readonly bool _handleMoves;
    private Vector2 _lastPosition;

    /// <summary>Gets whether the owner currently holds an active captured gesture.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Occurs when a primary press starts capture.</summary>
    public event Action? Started;

    /// <summary>Occurs when the captured pointer moves by a non-zero logical delta.</summary>
    public event Action<Vector2>? Delta;

    /// <summary>Occurs with the owner's local position on press, captured move, and release.</summary>
    public event Action<Vector2>? PositionChanged;

    /// <summary>Occurs when release or capture loss completes the gesture.</summary>
    public event Action? Completed;

    /// <summary>Attaches a captured primary-pointer gesture to an element.</summary>
    /// <param name="owner">Element receiving routed pointer input.</param>
    /// <param name="handleMoves">Whether captured moves are handled and reported.</param>
    public PointerCaptureGesture(UIElement owner, bool handleMoves)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _handleMoves = handleMoves;
        owner.Pointer += OnPointer;
        owner.PointerCaptureLost += OnPointerCaptureLost;
    }

    /// <summary>Starts, advances, or completes pointer capture.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="pointerEvent">Routed pointer data.</param>
    private void OnPointer(UIElement sender, UIPointerEventArgs pointerEvent)
    {
        if (pointerEvent.RoutePhase != UIRoutePhase.Target)
            return;
        if (pointerEvent.Kind == UIPointerEventKind.Press &&
            pointerEvent.Button == InputPointerButton.Primary)
        {
            IsActive = true;
            _lastPosition = pointerEvent.Position;
            pointerEvent.CapturePointer();
            pointerEvent.Handled = true;
            PositionChanged?.Invoke(pointerEvent.LocalPosition);
            Started?.Invoke();
            return;
        }
        if (pointerEvent.Kind == UIPointerEventKind.Move && IsActive && _handleMoves)
        {
            var delta = pointerEvent.Position - _lastPosition;
            _lastPosition = pointerEvent.Position;
            pointerEvent.Handled = true;
            PositionChanged?.Invoke(pointerEvent.LocalPosition);
            if (delta != Vector2.Zero)
                Delta?.Invoke(delta);
            return;
        }
        if (pointerEvent.Kind != UIPointerEventKind.Release || !IsActive)
            return;
        PositionChanged?.Invoke(pointerEvent.LocalPosition);
        IsActive = false;
        pointerEvent.ReleasePointerCapture();
        pointerEvent.Handled = true;
        Completed?.Invoke();
    }

    /// <summary>Completes an active gesture when capture is revoked externally.</summary>
    private void OnPointerCaptureLost()
    {
        if (!IsActive)
            return;
        IsActive = false;
        Completed?.Invoke();
    }
}
