namespace Engine.Graphics;

/// <summary>
/// Describes one solid UI rectangle in window coordinates.
/// </summary>
/// <param name="Left">Left edge in pixels.</param>
/// <param name="Top">Top edge in pixels.</param>
/// <param name="Right">Right edge in pixels.</param>
/// <param name="Bottom">Bottom edge in pixels.</param>
/// <param name="Color">Rectangle color.</param>
public readonly record struct UIDrawCommand(
    float Left,
    float Top,
    float Right,
    float Bottom,
    Color Color);

/// <summary>
/// Collects semantic UI paint commands without exposing GPU vertex formats.
/// </summary>
public sealed class UIDrawList
{
    private static readonly IReadOnlyDictionary<char, string[]> Glyphs = CreateGlyphs();
    private readonly List<UIDrawCommand> _commands = [];

    /// <summary>Gets the ordered paint commands.</summary>
    public IReadOnlyList<UIDrawCommand> Commands => _commands;

    /// <summary>Adds a solid rectangle.</summary>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="right">Right edge.</param>
    /// <param name="bottom">Bottom edge.</param>
    /// <param name="color">Rectangle color.</param>
    public void AddRectangle(float left, float top, float right, float bottom, Color color)
    {
        _commands.Add(new UIDrawCommand(left, top, right, bottom, color));
    }

    /// <summary>Adds text using the renderer-independent five-by-seven pixel font.</summary>
    /// <param name="text">Text to draw.</param>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="pixelSize">Size of one font pixel.</param>
    /// <param name="color">Text color.</param>
    public void AddText(string text, float left, float top, float pixelSize, Color color)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (pixelSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(pixelSize));

        var cursor = left;
        foreach (var character in text.ToUpperInvariant())
        {
            var glyph = Glyphs.TryGetValue(character, out var found) ? found : Glyphs['?'];
            for (var row = 0; row < glyph.Length; row++)
            {
                for (var column = 0; column < glyph[row].Length; column++)
                {
                    if (glyph[row][column] == '1')
                        AddRectangle(cursor + column * pixelSize, top + row * pixelSize,
                            cursor + (column + 1) * pixelSize, top + (row + 1) * pixelSize, color);
                }
            }
            cursor += 6f * pixelSize;
        }
    }

    /// <summary>Creates the compact built-in glyph table.</summary>
    /// <returns>Glyph rows indexed by supported character.</returns>
    private static IReadOnlyDictionary<char, string[]> CreateGlyphs()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789?-_ .";
        var encoded = new[]
        {
            "011101000110001111111000110001", "111101000111110100011000111110", "011111000010000100001000001111",
            "111101000110001100011000111110", "111111000011110100001000011111", "111111000011110100001000010000",
            "011111000010111100011000101111", "100011000111111100011000110001", "111110010000100001000010011111",
            "001110001000010000101001001100", "100011001011100100101000110001", "100001000010000100001000011111",
            "100011101110101101011000110001", "100011100110101100111000110001", "011101000110001100011000101110",
            "111101000110001111101000010000", "011101000110001101011001001101", "111101000110001111101001010001",
            "011111000001110000011000111110", "111110010000100001000010000100", "100011000110001100011000101110",
            "100011000110001100010101000100", "100011000110101101011010101010", "100010101000100010101000110001",
            "100011000101010001000010000100", "111110001000100010001000011111",
            "01110100011001110101110011000101110", "001000110000100001000010001110", "011101000100001001100100011111",
            "111100000100001011100000111110", "000100011001010100101111100010", "111111000011110000011000111110",
            "011101000010000111101000101110", "111110000100010001000100001000", "011101000101110100011000101110",
            "011101000110001011110000101110", "011101000100010001000000000100", "000000000000000011100010000100",
            "000000000000000000000000011111", "000000000000000000000000000100", "000000000000000000000000000000"
        };
        var glyphs = new Dictionary<char, string[]>();
        for (var index = 0; index < alphabet.Length; index++)
        {
            var bits = encoded[index].PadRight(35, '0');
            glyphs[alphabet[index]] = Enumerable.Range(0, 7)
                .Select(row => bits.Substring(row * 5, 5)).ToArray();
        }
        glyphs['>'] = ["10000", "01000", "00100", "00010", "00100", "01000", "10000"];
        return glyphs;
    }
}
