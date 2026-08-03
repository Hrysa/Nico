using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Positions floating children at explicit coordinates within an overlay surface.</summary>
public sealed class Canvas : Panel
{
    private readonly Dictionary<UIElement, Vector2> _positions = new();

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
        foreach (var child in Children.OfType<UIElement>())
            child.Measure(availableSize);
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        foreach (var child in Children.OfType<UIElement>())
        {
            child.Measure(contentSize);
            var position = _positions.GetValueOrDefault(child);
            var size = child.DesiredSize;
            if (child.HorizontalAlignment == HorizontalAlignment.Stretch)
                size.X = MathF.Max(0f, contentSize.X - position.X);
            if (child.VerticalAlignment == VerticalAlignment.Stretch)
                size.Y = MathF.Max(0f, contentSize.Y - position.Y);
            child.Arrange(position, size);
        }
    }
}
