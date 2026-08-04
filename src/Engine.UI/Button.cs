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

/// <summary>A clickable content box with hover and press visual states.</summary>
public class Button : ContentControl
{
    private const float DefaultHorizontalPadding = 7f;
    private Color _normalColor;
    private Color _hoverColor;
    private Color _pressedColor;
    private bool _paintNormalBackground = true;

    /// <summary>Gets or sets whether the idle state paints the button box.</summary>
    protected bool PaintNormalBackground
    {
        get => _paintNormalBackground;
        set => _paintNormalBackground = value;
    }

    /// <summary>Gets or sets the left content inset.</summary>
    public float PaddingLeft
    {
        get => Padding.Left;
        set
        {
            Padding = Padding with { Left = value };
        }
    }

    /// <summary>Gets or sets the top content inset.</summary>
    public float PaddingTop
    {
        get => Padding.Top;
        set => Padding = Padding with { Top = value };
    }

    /// <summary>Gets or sets the right content inset.</summary>
    public float PaddingRight
    {
        get => Padding.Right;
        set
        {
            Padding = Padding with { Right = value };
        }
    }

    /// <summary>Gets or sets the bottom content inset.</summary>
    public float PaddingBottom
    {
        get => Padding.Bottom;
        set => Padding = Padding with { Bottom = value };
    }

    /// <summary>Gets or sets the normal background color.</summary>
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

    /// <summary>Creates a fixed-size button with a custom color scheme.</summary>
    /// <param name="width">Button width.</param>
    /// <param name="height">Button height.</param>
    /// <param name="label">Button label.</param>
    /// <param name="normalColor">Normal background color.</param>
    public Button(float width, float height, string label, Color normalColor)
        : this(width, height, normalColor)
    {
        Content = CreateLabel(label, UITheme.Dark.FontSize);
    }

    /// <summary>Creates a fixed-size content button with a custom color scheme.</summary>
    /// <param name="width">Button width.</param>
    /// <param name="height">Button height.</param>
    /// <param name="normalColor">Normal background color.</param>
    public Button(float width, float height, Color normalColor)
        : base(width, height)
    {
        Padding = new Thickness(DefaultHorizontalPadding, 0f);
        CornerRadius = 5f;
        _normalColor = normalColor;
        _hoverColor = Color.Lerp(normalColor, Color.White, 0.15f);
        _pressedColor = Color.Lerp(normalColor, Color.Black, 0.2f);
        BackgroundColor = normalColor;
    }

    /// <summary>Creates a fixed-size button with the default theme.</summary>
    /// <param name="width">Button width.</param>
    /// <param name="height">Button height.</param>
    /// <param name="label">Button label.</param>
    public Button(float width, float height, string label)
        : this(width, height, label, UITheme.Dark, ButtonStyle.Subtle)
    {
    }

    /// <summary>Creates a themed button whose width follows its label content.</summary>
    /// <param name="height">Button height.</param>
    /// <param name="label">Button label.</param>
    /// <param name="theme">Theme supplying colors and typography.</param>
    /// <param name="style">Button emphasis.</param>
    public Button(float height, string label, UITheme theme,
        ButtonStyle style = ButtonStyle.Subtle)
        : this(height, theme, style)
    {
        Content = CreateLabel(label, theme.FontSize);
    }

    /// <summary>Creates a fixed-size themed button.</summary>
    /// <param name="width">Button width.</param>
    /// <param name="height">Button height.</param>
    /// <param name="label">Button label.</param>
    /// <param name="theme">Theme supplying colors and typography.</param>
    /// <param name="style">Button emphasis.</param>
    public Button(float width, float height, string label, UITheme theme,
        ButtonStyle style = ButtonStyle.Subtle)
        : this(width, height, theme, style)
    {
        Content = CreateLabel(label, theme.FontSize);
    }

    /// <summary>Creates a content-sized themed button with no predefined content.</summary>
    /// <param name="height">Button height.</param>
    /// <param name="theme">Theme supplying state colors.</param>
    /// <param name="style">Button emphasis.</param>
    public Button(float height, UITheme theme, ButtonStyle style = ButtonStyle.Subtle)
        : this(0f, height, theme, style)
    {
    }

    /// <summary>Creates a fixed-size themed button with no predefined content.</summary>
    /// <param name="width">Button width.</param>
    /// <param name="height">Button height.</param>
    /// <param name="theme">Theme supplying state colors.</param>
    /// <param name="style">Button emphasis.</param>
    public Button(float width, float height, UITheme theme,
        ButtonStyle style = ButtonStyle.Subtle)
        : base(width, height)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Padding = new Thickness(DefaultHorizontalPadding, 0f);
        CornerRadius = 5f;
        _normalColor = style == ButtonStyle.Primary ? theme.SurfacePressed : theme.SurfaceRaised;
        _hoverColor = theme.SurfaceHover;
        _pressedColor = style == ButtonStyle.Primary ? theme.BorderStrong : theme.SurfacePressed;
        _paintNormalBackground = style == ButtonStyle.Primary;
        BackgroundColor = _normalColor;
        ForegroundColor = style == ButtonStyle.Primary ? theme.Accent : theme.TextPrimary;
    }

    /// <summary>Creates a non-interactive label child for text convenience constructors.</summary>
    /// <param name="text">Label text.</param>
    /// <param name="fontSize">Label font size.</param>
    /// <returns>The configured label child.</returns>
    private Label CreateLabel(string text, float fontSize)
    {
        return new Label(text)
        {
            FontSize = fontSize,
            ForegroundColor = ForegroundColor,
            PaddingLeft = 0f,
            IsHitTestVisible = false
        };
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var paintBackground = _paintNormalBackground || IsHovered || IsPressed;
        base.PaintBackground = paintBackground;
        base.Paint(drawList);
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
