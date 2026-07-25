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

    /// <inheritdoc/>
    protected override void OnMouseEnter()
    {
        BackgroundColor = IsPressed ? _pressedColor : _hoverColor;
        base.OnMouseEnter();
    }

    /// <inheritdoc/>
    protected override void OnMouseLeave()
    {
        BackgroundColor = _normalColor;
        base.OnMouseLeave();
    }

    /// <inheritdoc/>
    protected override void OnMouseDown()
    {
        BackgroundColor = _pressedColor;
        base.OnMouseDown();
    }

    /// <inheritdoc/>
    protected override void OnMouseUp()
    {
        BackgroundColor = IsHovered ? _hoverColor : _normalColor;
        base.OnMouseUp();
    }
}
