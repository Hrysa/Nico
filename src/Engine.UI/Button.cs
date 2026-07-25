using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// A clickable button element with hover and press visual states.
/// </summary>
public class Button : UIElement
{
    private Color _normalColor;
    private Color _hoverColor;
    private Color _pressedColor;

    /// <summary>Gets or sets the button label text.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the mouse is hovering over this button.</summary>
    public bool IsHovered { get; set; }

    /// <summary>Gets or sets whether this button is currently pressed.</summary>
    public bool IsPressed { get; set; }

    /// <summary>Gets or sets the normal (idle) background color.</summary>
    public Color NormalColor
    {
        get => _normalColor;
        set
        {
            _normalColor = value;
            if (!IsHovered && !IsPressed)
                BackgroundColor = value;
        }
    }

    /// <summary>Gets or sets the hover background color.</summary>
    public Color HoverColor
    {
        get => _hoverColor;
        set => _hoverColor = value;
    }

    /// <summary>Gets or sets the pressed background color.</summary>
    public Color PressedColor
    {
        get => _pressedColor;
        set => _pressedColor = value;
    }

    /// <summary>Occurs when the button is clicked (released after press).</summary>
    public event Action? Click;

    /// <summary>
    /// Creates a new Button at the specified position and size.
    /// </summary>
    /// <param name="x">The X position (left edge).</param>
    /// <param name="y">The Y position (top edge).</param>
    /// <param name="width">The button width.</param>
    /// <param name="height">The button height.</param>
    /// <param name="label">The button label text.</param>
    /// <param name="normalColor">The normal background color.</param>
    public Button(float x, float y, float width, float height, string label, Color normalColor)
        : base(x, y, width, height)
    {
        Label = label;
        _normalColor = normalColor;
        _hoverColor = Color.Lerp(normalColor, Color.White, 0.15f);
        _pressedColor = Color.Lerp(normalColor, Color.Black, 0.2f);
        BackgroundColor = normalColor;
    }

    /// <summary>
    /// Creates a new Button with default gray color scheme.
    /// </summary>
    /// <param name="x">The X position (left edge).</param>
    /// <param name="y">The Y position (top edge).</param>
    /// <param name="width">The button width.</param>
    /// <param name="height">The button height.</param>
    /// <param name="label">The button label text.</param>
    public Button(float x, float y, float width, float height, string label)
        : this(x, y, width, height, label, Color.Gray)
    {
    }

    /// <summary>
    /// Sets the hover state and updates the background color accordingly.
    /// </summary>
    /// <param name="hovered">True if the mouse is hovering over this button.</param>
    public void SetHover(bool hovered)
    {
        IsHovered = hovered;
        UpdateColor();
    }

    /// <summary>
    /// Sets the pressed state and updates the background color accordingly.
    /// </summary>
    /// <param name="pressed">True if the button is being pressed.</param>
    public void SetPressed(bool pressed)
    {
        IsPressed = pressed;
        UpdateColor();
    }

    /// <summary>
    /// Invokes the Click event if the button is in a valid press state.
    /// </summary>
    public void InvokeClick()
    {
        if (IsPressed)
            Click?.Invoke();
    }

    /// <summary>
    /// Generates vertex data for this button as a colored quad.
    /// </summary>
    /// <returns>An array of 6 vertices forming two triangles.</returns>
    public override Vertex[] GetVertices()
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

    private void UpdateColor()
    {
        if (IsPressed)
            BackgroundColor = _pressedColor;
        else if (IsHovered)
            BackgroundColor = _hoverColor;
        else
            BackgroundColor = _normalColor;
    }
}
