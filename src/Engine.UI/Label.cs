using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Displays renderer-independent TrueType text.
/// </summary>
public class Label : UIElement
{
    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.Text,
        string.IsNullOrWhiteSpace(Name) ? Text : Name,
        Text,
        IsEnabled,
        true,
        false,
        null);

    /// <summary>Gets or sets the displayed text.</summary>
    private string _text = string.Empty;
    private float _fontSize = UITheme.Dark.FontSize;
    private float _paddingLeft = 4f;
    private bool _paintBackground;

    /// <summary>Gets or sets the displayed text.</summary>
    public string Text
    {
        get => _text;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_text == value)
                return;
            _text = value;
            InvalidateMeasure();
        }
    }

    /// <summary>Gets or sets the font height in logical pixels.</summary>
    public float FontSize
    {
        get => _fontSize;
        set { if (_fontSize != value) { _fontSize = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets the left text inset.</summary>
    public float PaddingLeft
    {
        get => _paddingLeft;
        set { if (_paddingLeft != value) { _paddingLeft = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets whether the label paints its background.</summary>
    public bool PaintBackground
    {
        get => _paintBackground;
        set { if (_paintBackground != value) { _paintBackground = value; InvalidateVisual(); } }
    }

    /// <summary>
    /// Creates a text label.
    /// </summary>
    /// <param name="width">Label width.</param>
    /// <param name="height">Label height.</param>
    /// <param name="text">Displayed text.</param>
    private Label(float width, float height, string text)
        : base(width, height)
    {
        Text = text;
    }

    /// <summary>Creates a label for container-owned layout.</summary>
    /// <param name="text">Displayed text.</param>
    /// <param name="width">Optional explicit width.</param>
    /// <param name="height">Optional explicit height.</param>
    public Label(string text, float width = 0f, float height = 0f)
        : this(width, height, text)
    {
    }

    /// <summary>Measures label text using hosted system-font or approximate startup metrics.</summary>
    /// <returns>The estimated horizontal glyph advance in logical pixels.</returns>
    public float MeasureTextWidth()
    {
        return TextLayout.MeasureWidth(
            Text.AsSpan(), FontSize, FlowDirection.ToTextFlowDirection());
    }

    /// <summary>Measures text using approximate startup metrics before a host is available.</summary>
    /// <param name="text">Text whose horizontal advance is measured.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <returns>The estimated horizontal glyph advance in logical pixels.</returns>
    public static float MeasureTextWidth(string text, float fontSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FallbackTextLayoutService.Instance.MeasureWidth(text.AsSpan(), fontSize);
    }

    /// <summary>Measures text content and its leading inset.</summary>
    /// <param name="availableSize">Space offered by the parent.</param>
    /// <returns>Desired label size.</returns>
    protected override System.Numerics.Vector2 MeasureOverride(System.Numerics.Vector2 availableSize)
    {
        return new System.Numerics.Vector2(PaddingLeft + MeasureTextWidth(), FontSize);
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        if (PaintBackground)
            base.Paint(drawList);

        var textHeight = FontSize;
        drawList.AddText(Text, Left + PaddingLeft, Top + MathF.Max(0f, (Height - textHeight) / 2f),
            FontSize, ForegroundColor, BackgroundColor, FlowDirection.ToTextFlowDirection());
    }
}
