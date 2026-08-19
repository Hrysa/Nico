namespace Engine.UI;

/// <summary>Provides common text, typography, and invalidation behavior for retained text elements.</summary>
public abstract class TextElement : Box
{
    /// <summary>Creates a retained text element.</summary>
    /// <param name="width">Optional explicit width.</param>
    /// <param name="height">Optional explicit height.</param>
    protected TextElement(float width = 0f, float height = 0f)
        : base(width, height)
    {
    }

    /// <summary>Gets or sets the displayed text.</summary>
    public string Text
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (field == value)
                return;
            field = value;
            OnTextChanged();
        }
    } = string.Empty;

    /// <summary>Gets or sets the font height in logical pixels.</summary>
    public float FontSize
    {
        get;
        set
        {
            if (value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            OnFontSizeChanged();
        }
    } = UITheme.Dark.FontSize;

    /// <summary>Gets or sets the reusable typography properties as one style value.</summary>
    public UITextStyle TextStyle
    {
        get => new(FontSize, ForegroundColor);
        set
        {
            FontSize = value.FontSize;
            ForegroundColor = value.ForegroundColor;
        }
    }

    /// <summary>Invalidates layout after displayed text changes.</summary>
    protected virtual void OnTextChanged() => InvalidateMeasure();

    /// <summary>Invalidates layout after font metrics change.</summary>
    protected virtual void OnFontSizeChanged() => InvalidateMeasure();
}
