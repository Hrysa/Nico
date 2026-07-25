using System.Numerics;
using Engine.Core;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Base class for all UI elements. Extends <see cref="Node"/> with layout, size, color, and interaction.
/// </summary>
public class UIElement : Node
{
    /// <summary>Gets or sets the element width in pixels.</summary>
    public float Width { get; set; }

    /// <summary>Gets or sets the element height in pixels.</summary>
    public float Height { get; set; }

    /// <summary>Gets or sets the background color.</summary>
    public Color BackgroundColor { get; set; } = Color.Black;

    /// <summary>Gets or sets the foreground (text/icon) color.</summary>
    public Color ForegroundColor { get; set; } = Color.White;

    /// <summary>Gets or sets whether this element is visible.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Gets or sets whether the mouse is hovering over this element.</summary>
    public bool IsHovered { get; set; }

    /// <summary>Gets or sets whether this element is currently pressed.</summary>
    public bool IsPressed { get; set; }

    /// <summary>Occurs when the mouse enters this element.</summary>
    public event Action? MouseEnter;

    /// <summary>Occurs when the mouse leaves this element.</summary>
    public event Action? MouseLeave;

    /// <summary>Occurs when a mouse button is pressed on this element.</summary>
    public event Action? MouseDown;

    /// <summary>Occurs when a mouse button is released on this element.</summary>
    public event Action? MouseUp;

    /// <summary>Occurs when this element is clicked (released after press).</summary>
    public event Action? Click;

    /// <summary>Gets the left edge position (Position.X).</summary>
    public float Left => Position.X;

    /// <summary>Gets the top edge position (Position.Y).</summary>
    public float Top => Position.Y;

    /// <summary>Gets the right edge position (Position.X + Width).</summary>
    public float Right => Position.X + Width;

    /// <summary>Gets the bottom edge position (Position.Y + Height).</summary>
    public float Bottom => Position.Y + Height;

    /// <summary>
    /// Creates a new UIElement with the specified position and size.
    /// </summary>
    /// <param name="x">The X position (left edge).</param>
    /// <param name="y">The Y position (top edge).</param>
    /// <param name="width">The element width.</param>
    /// <param name="height">The element height.</param>
    public UIElement(float x, float y, float width, float height)
    {
        Position = new Vector3(x, y, 0);
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Tests whether a point (in screen coordinates) is inside this element.
    /// </summary>
    /// <param name="point">The point to test.</param>
    /// <returns>True if the point is within this element's bounds.</returns>
    public bool ContainsPoint(Vector2 point)
    {
        return point.X >= Left && point.X <= Right
            && point.Y >= Top && point.Y <= Bottom;
    }

    /// <summary>
    /// Sets the hover state and raises <see cref="MouseEnter"/> / <see cref="MouseLeave"/> as appropriate.
    /// </summary>
    /// <param name="hovered">True if the mouse is hovering over this element.</param>
    public void SetHover(bool hovered)
    {
        if (IsHovered == hovered)
            return;

        IsHovered = hovered;

        if (hovered)
            OnMouseEnter();
        else
            OnMouseLeave();
    }

    /// <summary>
    /// Sets the pressed state and raises <see cref="MouseDown"/> / <see cref="MouseUp"/> as appropriate.
    /// </summary>
    /// <param name="pressed">True if the button is being pressed.</param>
    public void SetPressed(bool pressed)
    {
        if (IsPressed == pressed)
            return;

        IsPressed = pressed;

        if (pressed)
            OnMouseDown();
        else
            OnMouseUp();
    }

    /// <summary>
    /// Raises the <see cref="Click"/> event. Call after a press-release cycle on this element.
    /// </summary>
    public void InvokeClick()
    {
        OnClick();
    }

    /// <summary>Called when the mouse enters this element. Override for custom hover-on behavior.</summary>
    protected virtual void OnMouseEnter()
    {
        MouseEnter?.Invoke();
    }

    /// <summary>Called when the mouse leaves this element. Override for custom hover-off behavior.</summary>
    protected virtual void OnMouseLeave()
    {
        MouseLeave?.Invoke();
    }

    /// <summary>Called when a mouse button is pressed on this element. Override for custom press behavior.</summary>
    protected virtual void OnMouseDown()
    {
        MouseDown?.Invoke();
    }

    /// <summary>Called when a mouse button is released on this element. Override for custom release behavior.</summary>
    protected virtual void OnMouseUp()
    {
        MouseUp?.Invoke();
    }

    /// <summary>Called when this element is clicked. Override for custom click behavior.</summary>
    protected virtual void OnClick()
    {
        Click?.Invoke();
    }

    /// <summary>
    /// Generates vertex data for this element as a colored quad.
    /// </summary>
    /// <returns>An array of 6 vertices forming two triangles.</returns>
    public virtual Vertex[] GetVertices()
    {
        var color = BackgroundColor;
        return new Vertex[]
        {
            new(new Vector3(Left, Top, 0), color),
            new(new Vector3(Left, Bottom, 0), color),
            new(new Vector3(Right, Bottom, 0), color),

            new(new Vector3(Right, Bottom, 0), color),
            new(new Vector3(Right, Top, 0), color),
            new(new Vector3(Left, Top, 0), color),
        };
    }

    /// <summary>
    /// Collects vertices from this element and all visible children recursively.
    /// </summary>
    /// <returns>A list of all vertices for rendering.</returns>
    public List<Vertex> CollectVertices()
    {
        var result = new List<Vertex>();
        CollectVerticesRecursive(result);
        return result;
    }

    private void CollectVerticesRecursive(List<Vertex> result)
    {
        if (!IsVisible)
            return;

        result.AddRange(GetVertices());

        foreach (var child in Children)
        {
            if (child is UIElement ui)
                ui.CollectVerticesRecursive(result);
        }
    }
}
