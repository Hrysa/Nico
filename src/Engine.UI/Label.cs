using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Displays renderer-independent TrueType text.
/// </summary>
public class Label : TextElement
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

    /// <summary>
    /// Creates a text label.
    /// </summary>
    /// <param name="width">Label width.</param>
    /// <param name="height">Label height.</param>
    /// <param name="text">Displayed text.</param>
    private Label(float width, float height, string text)
        : base(width, height)
    {
        Padding = new Thickness(UITheme.Dark.TextContentPadding, 0f, 0f, 0f);
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

    /// <summary>Measures text content and its common padding.</summary>
    /// <param name="availableSize">Space offered by the parent.</param>
    /// <returns>Desired label size.</returns>
    protected override System.Numerics.Vector2 MeasureOverride(System.Numerics.Vector2 availableSize)
    {
        return new System.Numerics.Vector2(
            Padding.Horizontal + MeasureTextWidth(),
            Padding.Vertical + FontSize);
    }

    /// <inheritdoc/>
    protected override void PaintContent(UIDrawList drawList)
    {
        var textHeight = FontSize;
        drawList.AddText(Text, ContentLeft,
            ContentTop + MathF.Max(0f, (ContentHeight - textHeight) / 2f),
            FontSize, ForegroundColor, BackgroundColor, FlowDirection.ToTextFlowDirection());
    }
}
