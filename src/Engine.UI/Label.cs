using Engine.Graphics;
using System.Text;

namespace Engine.UI;

/// <summary>
/// Displays renderer-independent TrueType text.
/// </summary>
public class Label : UIElement
{
    private const float InterVerticalMetricsUnits = 2478f;
    private static readonly ushort[] _uppercaseAdvances =
    [
        1413, 1340, 1496, 1478, 1231, 1209, 1528, 1522, 550, 1169, 1376, 1158, 1850,
        1543, 1566, 1308, 1566, 1318, 1314, 1322, 1524, 1413, 2018, 1397, 1390, 1288
    ];
    private static readonly ushort[] _lowercaseAdvances =
    [
        1150, 1254, 1170, 1254, 1194, 758, 1256, 1211, 496, 496, 1124, 496, 1794,
        1210, 1228, 1254, 1254, 771, 1081, 670, 1211, 1151, 1676, 1118, 1151, 1131
    ];
    private static readonly ushort[] _digitAdvances =
    [
        1292, 833, 1249, 1265, 1323, 1215, 1270, 1159, 1267, 1270
    ];

    /// <summary>Gets or sets the displayed text.</summary>
    public string Text { get; set; }

    /// <summary>Gets or sets the font height in logical pixels.</summary>
    public float FontSize { get; set; } = UITheme.Dark.FontSize;

    /// <summary>Gets or sets the left text inset.</summary>
    public float PaddingLeft { get; set; } = 4f;

    /// <summary>Gets or sets whether the label paints its background.</summary>
    public bool PaintBackground { get; set; }

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

    /// <summary>Measures the label text using the bundled Inter font metrics.</summary>
    /// <returns>The estimated horizontal glyph advance in logical pixels.</returns>
    public float MeasureTextWidth()
    {
        var advanceUnits = 0f;
        foreach (var rune in Text.EnumerateRunes())
            advanceUnits += GetInterAdvanceUnits(rune.Value);
        return advanceUnits * FontSize / InterVerticalMetricsUnits;
    }

    /// <summary>Returns the bundled Inter font's unscaled horizontal glyph advance.</summary>
    /// <param name="codepoint">Unicode codepoint to measure.</param>
    /// <returns>Horizontal advance in Inter font units.</returns>
    private static float GetInterAdvanceUnits(int codepoint)
    {
        if (codepoint is >= 'A' and <= 'Z')
            return _uppercaseAdvances[codepoint - 'A'];
        if (codepoint is >= 'a' and <= 'z')
            return _lowercaseAdvances[codepoint - 'a'];
        if (codepoint is >= '0' and <= '9')
            return _digitAdvances[codepoint - '0'];
        return codepoint == ' ' ? 576f : 1200f;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        if (PaintBackground)
            base.Paint(drawList);

        var textHeight = FontSize;
        drawList.AddText(Text, Left + PaddingLeft, Top + MathF.Max(0f, (Height - textHeight) / 2f),
            FontSize, ForegroundColor, BackgroundColor);
    }
}
