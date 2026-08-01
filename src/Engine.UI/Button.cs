using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies the visual emphasis of a themed button.</summary>
public enum ButtonStyle
{
    /// <summary>Low-emphasis button placed on a surrounding surface.</summary>
    Subtle,

    /// <summary>Filled button for a primary action.</summary>
    Primary
}

/// <summary>
/// A clickable button element with hover and press visual states.
/// </summary>
public class Button : UIElement
{
    private Color _normalColor;
    private Color _hoverColor;
    private Color _pressedColor;
    private bool _paintNormalBackground = true;

    /// <summary>Gets or sets the button text inset.</summary>
    public float PaddingLeft { get; set; } = 7f;

    /// <summary>Gets or sets the button's right content inset.</summary>
    public float PaddingRight { get; set; } = 7f;

    /// <summary>Gets or sets the button's top content inset.</summary>
    public float PaddingTop { get; set; } = 4f;

    /// <summary>Gets or sets the button's bottom content inset.</summary>
    public float PaddingBottom { get; set; } = 4f;

    /// <summary>Sets the same content inset on every side of the button.</summary>
    public float Padding
    {
        set
        {
            PaddingLeft = value;
            PaddingTop = value;
            PaddingRight = value;
            PaddingBottom = value;
        }
    }

    /// <summary>Gets or sets the button font size.</summary>
    public float FontSize { get; set; } = UITheme.Dark.FontSize;

    /// <summary>Gets or sets the radius of the button's rounded corners.</summary>
    public float CornerRadius { get; set; } = 5f;

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
        : this(x, y, width, height, label, UITheme.Dark, ButtonStyle.Subtle)
    {
    }

    /// <summary>
    /// Creates a button from shared theme tokens.
    /// </summary>
    /// <param name="x">The X position.</param>
    /// <param name="y">The Y position.</param>
    /// <param name="width">The button width.</param>
    /// <param name="height">The button height.</param>
    /// <param name="label">The button label.</param>
    /// <param name="theme">Theme supplying colors and typography.</param>
    /// <param name="style">Button emphasis.</param>
    public Button(
        float x,
        float y,
        float width,
        float height,
        string label,
        UITheme theme,
        ButtonStyle style = ButtonStyle.Subtle)
        : base(x, y, width, height)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Label = label;
        _normalColor = style == ButtonStyle.Primary ? theme.SurfacePressed : theme.SurfaceRaised;
        _hoverColor = theme.SurfaceHover;
        _pressedColor = style == ButtonStyle.Primary ? theme.BorderStrong : theme.SurfacePressed;
        _paintNormalBackground = style == ButtonStyle.Primary;
        BackgroundColor = _normalColor;
        ForegroundColor = style == ButtonStyle.Primary ? theme.Accent : theme.TextPrimary;
        FontSize = theme.FontSize;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var paintBackground = _paintNormalBackground || IsHovered || IsPressed;
        if (paintBackground)
            drawList.AddRoundedRectangle(Left, Top, Right, Bottom, CornerRadius, BackgroundColor);
        var textBackground = paintBackground ? BackgroundColor : GetParentBackgroundColor();
        var contentHeight = MathF.Max(0f, Height - PaddingTop - PaddingBottom);
        var textTop = Top + PaddingTop + MathF.Max(0f, (contentHeight - FontSize) / 2f);
        drawList.AddText(Label, Left + PaddingLeft, textTop,
            FontSize, ForegroundColor, textBackground);
    }

    /// <summary>Finds the nearest parent surface color behind this button.</summary>
    /// <returns>The parent background color, or the theme canvas when no UI parent exists.</returns>
    private Color GetParentBackgroundColor()
    {
        for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is UIElement element)
                return element.BackgroundColor;
        }
        return UITheme.Dark.Canvas;
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
