using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Provides reusable pointer-captured drag behavior for sliders and scroll bars.</summary>
public sealed class Thumb : UIElement
{
    private readonly PointerCaptureGesture _dragGesture;
    private readonly bool _isTransparent;
    private readonly bool _enableHoverState;
    private UIInteractionColors _interactionColors;

    /// <summary>Gets whether this thumb currently owns an active drag.</summary>
    public bool IsDragging => _dragGesture.IsActive;

    /// <summary>Gets or sets the common interaction palette used by this thumb.</summary>
    public UIInteractionColors InteractionColors
    {
        get => _interactionColors;
        set
        {
            if (_interactionColors == value)
                return;
            _interactionColors = value;
            InvalidateVisual();
        }
    }

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
        var resolvedTheme = theme ?? UITheme.Dark;
        _isTransparent = isTransparent;
        _enableHoverState = enableHoverState;
        _interactionColors = overrideColor is { } color
            ? new UIInteractionColors(color, color, color, color, color, color)
            : resolvedTheme.GetThumbInteractionColors();
        _dragGesture = new PointerCaptureGesture(this, handleMoves: true);
        _dragGesture.Started += OnDragStarted;
        _dragGesture.Delta += OnDragDelta;
        _dragGesture.Completed += OnDragCompleted;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        if (_isTransparent)
            return;
        var state = _enableHoverState ? GetInteractionState() : UIInteractionState.Normal;
        var color = _interactionColors.Resolve(state);
        drawList.AddRoundedRectangle(Left, Top, Right, Bottom,
            MathF.Min(MathF.Min(Width, Height) / 2f, 4f), color);
    }

    /// <summary>Forwards the shared gesture start to thumb consumers.</summary>
    private void OnDragStarted() => DragStarted?.Invoke();

    /// <summary>Forwards shared captured movement to thumb consumers.</summary>
    /// <param name="delta">Logical captured movement.</param>
    private void OnDragDelta(Vector2 delta) => DragDelta?.Invoke(delta);

    /// <summary>Forwards shared gesture completion to thumb consumers.</summary>
    private void OnDragCompleted() => DragCompleted?.Invoke();
}
