using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Positions floating children at explicit coordinates within an overlay surface.</summary>
public sealed class Canvas : Panel
{
    private readonly Dictionary<UIElement, Vector2> _positions = new();

    /// <summary>Gets or sets the host service used for monitor-aware popup constraints.</summary>
    public IPopupWorkAreaProvider? PopupWorkAreaProvider { get; set; }

    /// <summary>Creates a transparent canvas.</summary>
    public Canvas() : base(Color.Black)
    {
        PaintBackground = false;
        IsHitTestVisible = false;
        IsOverlay = true;
    }

    /// <summary>Adds a floating child at a canvas position.</summary>
    /// <param name="child">Element to add.</param>
    /// <param name="position">Canvas-relative top-left position.</param>
    public void Add(UIElement child, Vector2 position)
    {
        ArgumentNullException.ThrowIfNull(child);
        _positions[child] = position;
        InvalidateArrange();
        AddChild(child);
    }

    /// <summary>Updates the position of a floating child.</summary>
    /// <param name="child">Canvas child to move.</param>
    /// <param name="position">New canvas-relative top-left position.</param>
    public void SetPosition(UIElement child, Vector2 position)
    {
        if (!ReferenceEquals(child.Parent, this))
            throw new InvalidOperationException("The element is not a child of this canvas.");
        _positions[child] = position;
        InvalidateArrange();
    }

    /// <summary>Places a popup relative to its owner or a pointer and flips it away from constrained edges.</summary>
    /// <param name="popup">Popup already owned by this canvas.</param>
    /// <param name="pointerPosition">Host-relative pointer position used by pointer placement.</param>
    public void PlacePopup(Popup popup, Vector2 pointerPosition = default)
    {
        ArgumentNullException.ThrowIfNull(popup);
        if (!ReferenceEquals(popup.Parent, this))
            throw new InvalidOperationException("The popup is not a child of this canvas.");
        popup.Measure(new Vector2(Width, Height));
        var size = popup.DesiredSize;
        var owner = popup.Owner;
        var placement = popup.Placement;
        var anchor = placement == PopupPlacement.Pointer
            ? pointerPosition
            : owner is null
                ? Vector2.Zero
                : new Vector2(owner.Left, owner.Bottom);
        var workArea = PopupWorkAreaProvider?.GetWorkArea(anchor) ??
            new UIPopupWorkArea(0f, 0f, Width, Height);
        var position = placement == PopupPlacement.Pointer
            ? pointerPosition
            : owner is null
                ? Vector2.Zero
                : GetPlacementPosition(owner, size, placement);
        position += popup.PlacementOffset;

        if (popup.ConstrainToOverlayBounds && owner is not null)
        {
            if (placement == PopupPlacement.Below && position.Y + size.Y > workArea.Bottom)
                placement = PopupPlacement.Above;
            else if (placement == PopupPlacement.Above && position.Y < workArea.Top)
                placement = PopupPlacement.Below;
            else if (placement == PopupPlacement.Right && position.X + size.X > workArea.Right)
                placement = PopupPlacement.Left;
            else if (placement == PopupPlacement.Left && position.X < workArea.Left)
                placement = PopupPlacement.Right;
            if (placement != popup.Placement)
                position = GetPlacementPosition(owner, size, placement) + popup.PlacementOffset;
        }
        if (popup.ConstrainToOverlayBounds)
        {
            position.X = Math.Clamp(position.X, workArea.Left,
                MathF.Max(workArea.Left, workArea.Right - size.X));
            position.Y = Math.Clamp(position.Y, workArea.Top,
                MathF.Max(workArea.Top, workArea.Bottom - size.Y));
        }
        popup.ActualPlacement = placement;
        SetPosition(popup, position);
    }

    /// <summary>Calculates an owner-relative popup position for one placement direction.</summary>
    /// <param name="owner">Popup owner.</param>
    /// <param name="popupSize">Measured popup size.</param>
    /// <param name="placement">Requested direction.</param>
    /// <returns>Unconstrained canvas position.</returns>
    private static Vector2 GetPlacementPosition(UIElement owner, Vector2 popupSize, PopupPlacement placement)
    {
        return placement switch
        {
            PopupPlacement.Above => new Vector2(owner.Left, owner.Top - popupSize.Y),
            PopupPlacement.Right => new Vector2(owner.Right, owner.Top),
            PopupPlacement.Left => new Vector2(owner.Left - popupSize.X, owner.Top),
            _ => new Vector2(owner.Left, owner.Bottom)
        };
    }

    /// <summary>Removes a floating child and its canvas position.</summary>
    /// <param name="child">Element to remove.</param>
    /// <returns>True when the element was present.</returns>
    public bool Remove(UIElement child)
    {
        _positions.Remove(child);
        return RemoveChild(child);
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child)
                child.Measure(availableSize);
        }
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is not UIElement child)
                continue;
            child.Measure(contentSize);
            var position = _positions.GetValueOrDefault(child);
            var size = child.DesiredSize;
            if (child.HorizontalAlignment == HorizontalAlignment.Stretch)
                size.X = MathF.Max(0f, contentSize.X - position.X);
            if (child.VerticalAlignment == VerticalAlignment.Stretch)
                size.Y = MathF.Max(0f, contentSize.Y - position.Y);
            if (child is Popup { ConstrainToOverlayBounds: true })
            {
                size.X = MathF.Min(size.X, contentSize.X);
                size.Y = MathF.Min(size.Y, contentSize.Y);
                position.X = Math.Clamp(position.X, 0f, MathF.Max(0f, contentSize.X - size.X));
                position.Y = Math.Clamp(position.Y, 0f, MathF.Max(0f, contentSize.Y - size.Y));
            }
            child.Arrange(position, size);
        }
    }
}
