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

    /// <summary>Gets or sets whether this element can receive pointer hit tests.</summary>
    public bool IsHitTestVisible { get; set; } = true;

    /// <summary>Gets or sets whether this subtree is composited above viewport textures.</summary>
    public bool IsOverlay { get; set; }

    /// <summary>Gets or sets whether the mouse is hovering over this element.</summary>
    public bool IsHovered { get; set; }

    /// <summary>Gets or sets whether this element is currently pressed.</summary>
    public bool IsPressed { get; set; }

    /// <summary>Gets or sets whether this element has keyboard focus.</summary>
    public bool IsFocused { get; set; }

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

    /// <summary>Occurs when this element is double-clicked.</summary>
    public event Action? DoubleClick;

    /// <summary>Occurs when the mouse wheel scrolls over this element. Provides scroll offset.</summary>
    public event Action<float>? Scroll;

    /// <summary>Occurs when this element gains keyboard focus.</summary>
    public event Action? Focus;

    /// <summary>Occurs when this element loses keyboard focus.</summary>
    public event Action? Blur;

    /// <summary>Occurs when a key is pressed while this element is focused. Provides key code.</summary>
    public event Action<int>? KeyDown;

    /// <summary>Occurs when a key is released while this element is focused. Provides key code.</summary>
    public event Action<int>? KeyUp;

    /// <summary>Occurs when text input produces a character while this element is focused.</summary>
    public event Action<char>? TextInput;

    /// <summary>Gets the absolute left edge after applying parent layout positions.</summary>
    public float Left => GetParentLeft() + Position.X;

    /// <summary>Gets the absolute top edge after applying parent layout positions.</summary>
    public float Top => GetParentTop() + Position.Y;

    /// <summary>Gets the absolute right edge position.</summary>
    public float Right => Left + Width;

    /// <summary>Gets the absolute bottom edge position.</summary>
    public float Bottom => Top + Height;

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

    /// <summary>Gets the absolute left edge contributed by the UI parent.</summary>
    /// <returns>The parent left edge, or zero for a root element.</returns>
    private float GetParentLeft()
    {
        return Parent is UIElement parent ? parent.Left : 0f;
    }

    /// <summary>Gets the absolute top edge contributed by the UI parent.</summary>
    /// <returns>The parent top edge, or zero for a root element.</returns>
    private float GetParentTop()
    {
        return Parent is UIElement parent ? parent.Top : 0f;
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

    /// <summary>
    /// Raises the <see cref="DoubleClick"/> event.
    /// </summary>
    public void InvokeDoubleClick()
    {
        OnDoubleClick();
    }

    /// <summary>
    /// Raises the <see cref="Scroll"/> event.
    /// </summary>
    /// <param name="offset">The scroll offset.</param>
    public void InvokeScroll(float offset)
    {
        OnScroll(offset);
    }

    /// <summary>
    /// Sets the focus state and raises <see cref="Focus"/> / <see cref="Blur"/> as appropriate.
    /// </summary>
    /// <param name="focused">True to give this element focus.</param>
    public void SetFocus(bool focused)
    {
        if (IsFocused == focused)
            return;

        IsFocused = focused;

        if (focused)
            OnFocus();
        else
            OnBlur();
    }

    /// <summary>
    /// Raises <see cref="KeyDown"/> event for this element.
    /// </summary>
    /// <param name="keyCode">The key code.</param>
    public void InvokeKeyDown(int keyCode)
    {
        OnKeyDown(keyCode);
    }

    /// <summary>
    /// Raises <see cref="KeyUp"/> event for this element.
    /// </summary>
    /// <param name="keyCode">The key code.</param>
    public void InvokeKeyUp(int keyCode)
    {
        OnKeyUp(keyCode);
    }

    /// <summary>Raises the <see cref="TextInput"/> event for this element.</summary>
    /// <param name="character">Produced text character.</param>
    public void InvokeTextInput(char character)
    {
        OnTextInput(character);
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

    /// <summary>Called when this element is double-clicked. Override for custom double-click behavior.</summary>
    protected virtual void OnDoubleClick()
    {
        DoubleClick?.Invoke();
    }

    /// <summary>Called when the mouse wheel scrolls over this element. Override for custom scroll behavior.</summary>
    /// <param name="offset">The scroll offset.</param>
    protected virtual void OnScroll(float offset)
    {
        Scroll?.Invoke(offset);
    }

    /// <summary>Called when this element gains keyboard focus. Override for custom focus behavior.</summary>
    protected virtual void OnFocus()
    {
        Focus?.Invoke();
    }

    /// <summary>Called when this element loses keyboard focus. Override for custom blur behavior.</summary>
    protected virtual void OnBlur()
    {
        Blur?.Invoke();
    }

    /// <summary>Called when a key is pressed while focused. Override for custom key-down behavior.</summary>
    /// <param name="keyCode">The key code.</param>
    protected virtual void OnKeyDown(int keyCode)
    {
        KeyDown?.Invoke(keyCode);
    }

    /// <summary>Called when a key is released while focused. Override for custom key-up behavior.</summary>
    /// <param name="keyCode">The key code.</param>
    protected virtual void OnKeyUp(int keyCode)
    {
        KeyUp?.Invoke(keyCode);
    }

    /// <summary>Called when text input produces a character while focused.</summary>
    /// <param name="character">Produced text character.</param>
    protected virtual void OnTextInput(char character)
    {
        TextInput?.Invoke(character);
    }

    /// <summary>
    /// Appends paint commands for this element.
    /// </summary>
    /// <param name="drawList">Draw list receiving paint commands.</param>
    protected virtual void Paint(UIDrawList drawList)
    {
        drawList.AddRectangle(Left, Top, Right, Bottom, BackgroundColor);
    }

    /// <summary>
    /// Builds paint commands for this element and all visible descendants.
    /// </summary>
    /// <returns>The ordered UI draw list.</returns>
    public UIDrawList BuildDrawList()
    {
        var drawList = new UIDrawList();
        PaintRecursive(drawList, inheritedOverlay: false);
        return drawList;
    }

    /// <summary>Recursively appends visible paint commands.</summary>
    /// <param name="drawList">Draw list receiving paint commands.</param>
    /// <param name="inheritedOverlay">Whether an ancestor establishes overlay composition.</param>
    private void PaintRecursive(UIDrawList drawList, bool inheritedOverlay)
    {
        if (!IsVisible)
            return;

        var overlay = inheritedOverlay || IsOverlay;
        drawList.CurrentLayer = overlay ? UIDrawLayer.Overlay : UIDrawLayer.Content;
        Paint(drawList);

        foreach (var child in Children)
        {
            if (child is UIElement ui)
                ui.PaintRecursive(drawList, overlay);
        }
    }
}
