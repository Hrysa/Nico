using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies the visual emphasis of a themed button.</summary>
public enum ButtonStyle
{
    /// <summary>Low-emphasis button placed on a surrounding surface.</summary>
    Subtle,

    /// <summary>Filled button for a primary action.</summary>
    Primary,

    /// <summary>Text-first header action with interaction-only background fills.</summary>
    Header
}

/// <summary>A clickable content box with hover and press visual states.</summary>
public class Button : ContentControl
{
    private UIInteractionColors _interactionColors;
    private bool _paintNormalBackground = true;

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.Button,
        string.IsNullOrWhiteSpace(Name) ? (Content as Label)?.Text : Name,
        null,
        IsEnabled,
        true,
        false,
        null,
        Actions: UISemanticAction.Invoke);

    /// <inheritdoc/>
    public override bool PerformSemanticAction(UISemanticAction action, double? value = null)
    {
        if (action != UISemanticAction.Invoke || !IsEnabled)
            return false;
        InvokeClick();
        return true;
    }

    /// <summary>Gets or sets whether the idle state paints the button box.</summary>
    protected bool PaintNormalBackground
    {
        get => _paintNormalBackground;
        set => _paintNormalBackground = value;
    }

    /// <summary>Gets or sets the complete common interaction palette.</summary>
    public UIInteractionColors InteractionColors
    {
        get => _interactionColors;
        set
        {
            if (_interactionColors == value)
                return;
            _interactionColors = value;
            BackgroundColor = value.Normal;
            InvalidateVisual();
        }
    }

    /// <summary>Gets whether this control contributes persistent selected visual state.</summary>
    protected virtual bool IsVisualStateSelected => false;

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
        var theme = UITheme.Dark;
        IsTabStop = true;
        ClipToBounds = true;
        Padding = new Thickness(theme.ControlHorizontalPadding, 0f);
        CornerRadius = theme.ControlCornerRadius;
        _interactionColors = new UIInteractionColors(
            normalColor,
            Color.Lerp(normalColor, Color.White, 0.15f),
            Color.Lerp(normalColor, Color.Black, 0.2f),
            normalColor,
            Color.Lerp(normalColor, Color.White, 0.15f),
            normalColor);
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
        IsTabStop = true;
        ClipToBounds = true;
        Padding = new Thickness(theme.ControlHorizontalPadding, 0f);
        var visualStyle = theme.GetButtonStyle(style);
        CornerRadius = visualStyle.CornerRadius;
        _interactionColors = visualStyle.InteractionColors;
        _paintNormalBackground = visualStyle.PaintNormalBackground;
        BackgroundColor = visualStyle.InteractionColors.Normal;
        ForegroundColor = visualStyle.ForegroundColor;
    }

    /// <summary>Creates a non-interactive label child for text convenience constructors.</summary>
    /// <param name="text">Label text.</param>
    /// <param name="fontSize">Label font size.</param>
    /// <returns>The configured label child.</returns>
    private Label CreateLabel(string text, float fontSize)
    {
        return new Label(text)
        {
            TextStyle = new UITextStyle(fontSize, ForegroundColor),
            Padding = Thickness.Zero,
            IsHitTestVisible = false
        };
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var interactive = VisualStateMode == BoxVisualStateMode.Interactive;
        var state = GetInteractionState(interactive && IsVisualStateSelected);
        var hasTransientState = interactive &&
            (state & (UIInteractionState.Hovered | UIInteractionState.Pressed |
                UIInteractionState.Selected)) != 0;
        if (!_paintNormalBackground && !hasTransientState)
            return;
        PaintBox(drawList, interactive
            ? _interactionColors.Resolve(state)
            : _interactionColors.Normal);
    }
}
