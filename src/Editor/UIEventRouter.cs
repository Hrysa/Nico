using System.Numerics;
using Engine.UI;

namespace Editor;

/// <summary>
/// Owns UI hit testing, hover, press, focus, and input-event dispatch.
/// </summary>
public sealed class UIEventRouter
{
    private UIElement _root;
    private readonly Action _invalidate;
    private UIElement? _pressedElement;

    /// <summary>Gets the element currently under the pointer.</summary>
    public UIElement? HoveredElement { get; private set; }

    /// <summary>Gets the element that currently owns keyboard focus.</summary>
    public UIElement? FocusedElement { get; private set; }

    /// <summary>
    /// Creates a router for one UI tree.
    /// </summary>
    /// <param name="root">Root UI element.</param>
    /// <param name="invalidate">Callback used when visual state changes.</param>
    public UIEventRouter(UIElement root, Action invalidate)
    {
        _root = root;
        _invalidate = invalidate;
    }

    /// <summary>Replaces the routed UI tree and clears transient state.</summary>
    /// <param name="root">New root UI element.</param>
    public void SetRoot(UIElement root)
    {
        _pressedElement?.SetPressed(false);
        HoveredElement?.SetHover(false);
        FocusedElement?.SetFocus(false);
        _pressedElement = null;
        HoveredElement = null;
        FocusedElement = null;
        _root = root;
    }

    /// <summary>Updates the element under the pointer.</summary>
    /// <param name="position">Pointer position in window pixels.</param>
    public void MovePointer(Vector2 position)
    {
        var hit = HitTest(_root, position);
        if (ReferenceEquals(hit, HoveredElement))
            return;

        HoveredElement?.SetHover(false);
        HoveredElement = hit;
        HoveredElement?.SetHover(true);
        _invalidate();
    }

    /// <summary>Focuses and presses the hovered element.</summary>
    public void Press()
    {
        _pressedElement?.SetPressed(false);
        _pressedElement = HoveredElement;
        SetFocus(HoveredElement);
        _pressedElement?.SetPressed(true);
        _invalidate();
    }

    /// <summary>Releases the hovered element and optionally invokes its click.</summary>
    /// <param name="invokeClick">Whether to invoke the click event.</param>
    public void Release(bool invokeClick)
    {
        if (_pressedElement is null)
            return;

        var pressedElement = _pressedElement;
        _pressedElement = null;
        pressedElement.SetPressed(false);
        if (invokeClick && ReferenceEquals(pressedElement, HoveredElement))
            pressedElement.InvokeClick();
        _invalidate();
    }

    /// <summary>Dispatches a double click to the hovered element.</summary>
    public void DoubleClick()
    {
        HoveredElement?.InvokeDoubleClick();
        _invalidate();
    }

    /// <summary>Dispatches mouse-wheel input to the hovered element.</summary>
    /// <param name="offset">Wheel offset.</param>
    public void Scroll(float offset)
    {
        HoveredElement?.InvokeScroll(offset);
        _invalidate();
    }

    /// <summary>Dispatches a key press to the focused element.</summary>
    /// <param name="keyCode">Engine key code.</param>
    public void KeyDown(int keyCode)
    {
        FocusedElement?.InvokeKeyDown(keyCode);
        _invalidate();
    }

    /// <summary>Dispatches a key release to the focused element.</summary>
    /// <param name="keyCode">Engine key code.</param>
    public void KeyUp(int keyCode)
    {
        FocusedElement?.InvokeKeyUp(keyCode);
        _invalidate();
    }

    /// <summary>Dispatches a text-input character to the focused element.</summary>
    /// <param name="character">Produced text character.</param>
    public void TextInput(char character)
    {
        FocusedElement?.InvokeTextInput(character);
        _invalidate();
    }

    /// <summary>Changes keyboard focus.</summary>
    /// <param name="element">Element to focus, or null to clear focus.</param>
    private void SetFocus(UIElement? element)
    {
        if (ReferenceEquals(element, FocusedElement))
            return;

        FocusedElement?.SetFocus(false);
        FocusedElement = element;
        FocusedElement?.SetFocus(true);
    }

    /// <summary>Finds the topmost visible element containing a point.</summary>
    /// <param name="element">Subtree root.</param>
    /// <param name="position">Point in window pixels.</param>
    /// <returns>The topmost hit element, or null.</returns>
    private static UIElement? HitTest(UIElement element, Vector2 position)
    {
        if (!element.IsVisible || !element.ContainsPoint(position))
            return null;

        for (var index = element.Children.Count - 1; index >= 0; index--)
        {
            if (element.Children[index] is UIElement child && HitTest(child, position) is { } childHit)
                return childHit;
        }

        return element.IsHitTestVisible ? element : null;
    }
}
