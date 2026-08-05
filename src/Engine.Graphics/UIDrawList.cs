namespace Engine.Graphics;

/// <summary>Identifies when UI geometry is composited relative to viewport textures.</summary>
public enum UIDrawLayer
{
    /// <summary>Normal editor chrome rendered below viewport textures.</summary>
    Content,

    /// <summary>Floating UI rendered above viewport textures and gizmos.</summary>
    Overlay
}

/// <summary>Identifies the semantic content of a UI draw command.</summary>
public enum UIDrawCommandType
{
    /// <summary>A solid rectangle.</summary>
    Rectangle,

    /// <summary>A line of TrueType text.</summary>
    Text,

    /// <summary>A filled ellipse bounded by the command rectangle.</summary>
    Ellipse
}

/// <summary>
/// Describes one renderer-independent UI paint operation.
/// </summary>
/// <param name="Left">Left edge in pixels.</param>
/// <param name="Top">Top edge in pixels.</param>
/// <param name="Right">Right edge in pixels.</param>
/// <param name="Bottom">Bottom edge in pixels.</param>
/// <param name="Color">Rectangle or text color.</param>
/// <param name="Type">Command type.</param>
/// <param name="Text">Text content for text commands.</param>
/// <param name="FontPixelHeight">Requested font height in pixels.</param>
/// <param name="BackgroundColor">Known color behind anti-aliased text.</param>
/// <param name="Layer">Composition layer relative to viewport textures.</param>
/// <param name="CaretIndex">UTF-16 text index at which to draw a caret, or -1 for none.</param>
public readonly record struct UIDrawCommand(
    float Left,
    float Top,
    float Right,
    float Bottom,
    Color Color,
    UIDrawCommandType Type = UIDrawCommandType.Rectangle,
    string Text = "",
    float FontPixelHeight = 0f,
    Color BackgroundColor = default,
    UIDrawLayer Layer = UIDrawLayer.Content,
    int CaretIndex = -1);

/// <summary>
/// Collects semantic UI paint commands without exposing GPU vertex formats.
/// </summary>
public sealed class UIDrawList
{
    private static long _nextGeneration;
    private readonly List<UIDrawCommand> _commands = [];

    /// <summary>Gets the globally monotonic identity of the current paint snapshot.</summary>
    public ulong Generation { get; private set; } = NextGeneration();

    /// <summary>Gets the ordered paint commands.</summary>
    public IReadOnlyList<UIDrawCommand> Commands => _commands;

    /// <summary>Gets or sets the layer assigned to subsequently added commands.</summary>
    public UIDrawLayer CurrentLayer { get; set; }

    /// <summary>Clears commands while retaining capacity for the next paint snapshot.</summary>
    /// <param name="layer">Layer assigned to subsequently added commands.</param>
    public void Reset(UIDrawLayer layer = UIDrawLayer.Content)
    {
        _commands.Clear();
        CurrentLayer = layer;
        Generation = NextGeneration();
    }

    /// <summary>Appends previously generated semantic commands.</summary>
    /// <param name="commands">Cached commands to append in order.</param>
    public void AddRange(IEnumerable<UIDrawCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands.AddRange(commands);
    }

    /// <summary>Adds a solid rectangle.</summary>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="right">Right edge.</param>
    /// <param name="bottom">Bottom edge.</param>
    /// <param name="color">Rectangle color.</param>
    public void AddRectangle(float left, float top, float right, float bottom, Color color)
    {
        _commands.Add(new UIDrawCommand(left, top, right, bottom, color, Layer: CurrentLayer));
    }

    /// <summary>Adds a solid rectangle with uniformly rounded corners.</summary>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="right">Right edge.</param>
    /// <param name="bottom">Bottom edge.</param>
    /// <param name="radius">Corner radius.</param>
    /// <param name="color">Rectangle color.</param>
    public void AddRoundedRectangle(
        float left,
        float top,
        float right,
        float bottom,
        float radius,
        Color color)
    {
        var resolvedRadius = MathF.Max(0f, MathF.Min(radius,
            MathF.Min((right - left) / 2f, (bottom - top) / 2f)));
        if (resolvedRadius <= 0f)
        {
            AddRectangle(left, top, right, bottom, color);
            return;
        }

        AddRectangle(left + resolvedRadius, top, right - resolvedRadius, bottom, color);
        AddRectangle(left, top + resolvedRadius, right, bottom - resolvedRadius, color);
        var diameter = resolvedRadius * 2f;
        AddEllipse(left, top, left + diameter, top + diameter, color);
        AddEllipse(right - diameter, top, right, top + diameter, color);
        AddEllipse(left, bottom - diameter, left + diameter, bottom, color);
        AddEllipse(right - diameter, bottom - diameter, right, bottom, color);
    }

    /// <summary>Adds a filled ellipse bounded by a rectangle.</summary>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="right">Right edge.</param>
    /// <param name="bottom">Bottom edge.</param>
    /// <param name="color">Ellipse color.</param>
    public void AddEllipse(float left, float top, float right, float bottom, Color color)
    {
        _commands.Add(new UIDrawCommand(
            left, top, right, bottom, color, UIDrawCommandType.Ellipse, Layer: CurrentLayer));
    }

    /// <summary>Adds TrueType text over a black background.</summary>
    /// <param name="text">Text to draw.</param>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="color">Text color.</param>
    public void AddText(string text, float left, float top, float fontSize, Color color)
    {
        AddText(text, left, top, fontSize, color, Color.Black);
    }

    /// <summary>Adds anti-aliased TrueType text over a known background.</summary>
    /// <param name="text">Text to draw.</param>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="color">Text color.</param>
    /// <param name="backgroundColor">Color beneath the text.</param>
    public void AddText(
        string text,
        float left,
        float top,
        float fontSize,
        Color color,
        Color backgroundColor)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (fontSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(fontSize));

        _commands.Add(new UIDrawCommand(
            left,
            top,
            left,
            top,
            color,
            UIDrawCommandType.Text,
            text,
            fontSize,
            backgroundColor,
            CurrentLayer));
    }

    /// <summary>Adds editable TrueType text with a separately rendered caret.</summary>
    /// <param name="text">Text to draw.</param>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="color">Text and caret color.</param>
    /// <param name="backgroundColor">Color beneath the text.</param>
    /// <param name="caretIndex">UTF-16 text index at which to draw the caret.</param>
    public void AddTextWithCaret(
        string text,
        float left,
        float top,
        float fontSize,
        Color color,
        Color backgroundColor,
        int caretIndex)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (fontSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (caretIndex < 0 || caretIndex > text.Length)
            throw new ArgumentOutOfRangeException(nameof(caretIndex));

        _commands.Add(new UIDrawCommand(
            left,
            top,
            left,
            top,
            color,
            UIDrawCommandType.Text,
            text,
            fontSize,
            backgroundColor,
            CurrentLayer,
            caretIndex));
    }

    /// <summary>Returns the next globally monotonic paint generation.</summary>
    /// <returns>Paint generation identity.</returns>
    private static ulong NextGeneration()
    {
        return checked((ulong)Interlocked.Increment(ref _nextGeneration));
    }
}
