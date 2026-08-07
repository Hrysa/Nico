namespace Engine.Graphics;

/// <summary>Identifies when UI geometry is composited relative to viewport textures.</summary>
public enum UIDrawLayer
{
    /// <summary>Normal editor chrome rendered below viewport textures.</summary>
    Content,

    /// <summary>Floating UI rendered above viewport textures and gizmos.</summary>
    Overlay
}

/// <summary>Identifies a renderer-provided UI typeface.</summary>
public enum UIFontFamily
{
    /// <summary>Uses the platform UI font fallback chain.</summary>
    Default,

    /// <summary>Uses the bundled Visual Studio Code Codicons font.</summary>
    Codicon
}

/// <summary>Identifies the semantic content of a UI draw command.</summary>
public enum UIDrawCommandType
{
    /// <summary>A solid rectangle.</summary>
    Rectangle,

    /// <summary>A solid rectangle with uniformly rounded corners.</summary>
    RoundedRectangle,

    /// <summary>A line of TrueType text.</summary>
    Text,

    /// <summary>A filled ellipse bounded by the command rectangle.</summary>
    Ellipse,

    /// <summary>An inward-stroked ellipse bounded by the command rectangle.</summary>
    StrokedEllipse,

    /// <summary>A stroked line segment.</summary>
    Line,

    /// <summary>A sampled renderer-owned image.</summary>
    Image
}

/// <summary>Describes a logical axis-aligned UI clip rectangle.</summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Bottom edge.</param>
public readonly record struct UIClipRect(float Left, float Top, float Right, float Bottom)
{
    /// <summary>Gets whether the rectangle has no positive area.</summary>
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    /// <summary>Checks whether a logical point is inside the rectangle.</summary>
    /// <param name="x">Horizontal coordinate.</param>
    /// <param name="y">Vertical coordinate.</param>
    /// <returns>True when the point lies inside the rectangle.</returns>
    public bool Contains(float x, float y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    /// <summary>Intersects two logical clip rectangles.</summary>
    /// <param name="left">First rectangle.</param>
    /// <param name="right">Second rectangle.</param>
    /// <returns>The overlapping rectangle, which may be empty.</returns>
    public static UIClipRect Intersect(UIClipRect left, UIClipRect right) => new(
        MathF.Max(left.Left, right.Left),
        MathF.Max(left.Top, right.Top),
        MathF.Min(left.Right, right.Right),
        MathF.Min(left.Bottom, right.Bottom));
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
/// <param name="Clip">Optional inherited logical clip rectangle.</param>
/// <param name="StrokeWidth">Line thickness in logical pixels.</param>
/// <param name="Texture">Renderer-owned texture for image commands.</param>
/// <param name="Opacity">Multiplicative opacity from zero through one.</param>
/// <param name="TextDirection">Paragraph direction for text commands.</param>
/// <param name="CornerRadius">Uniform rounded-rectangle corner radius.</param>
/// <param name="FontFamily">Renderer-provided typeface used for text commands.</param>
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
    int CaretIndex = -1,
    UIClipRect? Clip = null,
    float StrokeWidth = 1f,
    TextureHandle Texture = default,
    float Opacity = 1f,
    TextFlowDirection TextDirection = TextFlowDirection.LeftToRight,
    float CornerRadius = 0f,
    UIFontFamily FontFamily = UIFontFamily.Default);

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

    /// <summary>Gets or sets the clip assigned to subsequently added commands.</summary>
    public UIClipRect? CurrentClip { get; set; }

    /// <summary>Clears commands while retaining capacity for the next paint snapshot.</summary>
    /// <param name="layer">Layer assigned to subsequently added commands.</param>
    public void Reset(UIDrawLayer layer = UIDrawLayer.Content)
    {
        _commands.Clear();
        CurrentLayer = layer;
        CurrentClip = null;
        Generation = NextGeneration();
    }

    /// <summary>Appends previously generated semantic commands.</summary>
    /// <param name="commands">Cached commands to append in order.</param>
    public void AddRange(IReadOnlyList<UIDrawCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        for (var index = 0; index < commands.Count; index++)
            _commands.Add(commands[index]);
    }

    /// <summary>Appends cached commands while applying an inherited clip.</summary>
    /// <param name="commands">Cached commands to append.</param>
    /// <param name="clip">Inherited clip.</param>
    public void AddRange(IReadOnlyList<UIDrawCommand> commands, UIClipRect? clip)
    {
        AddRange(commands, clip, 1f);
    }

    /// <summary>Appends cached commands while applying inherited clip and opacity.</summary>
    /// <param name="commands">Cached commands to append.</param>
    /// <param name="clip">Inherited clip.</param>
    /// <param name="opacity">Inherited multiplicative opacity.</param>
    public void AddRange(IReadOnlyList<UIDrawCommand> commands, UIClipRect? clip, float opacity)
    {
        ArgumentNullException.ThrowIfNull(commands);
        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            var resolved = command.Clip is { } local && clip is { } inherited
                ? UIClipRect.Intersect(local, inherited)
                : command.Clip ?? clip;
            if (resolved is { IsEmpty: true })
                continue;
            _commands.Add(command with { Clip = resolved, Opacity = command.Opacity * opacity });
        }
    }

    /// <summary>Adds a solid rectangle.</summary>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="right">Right edge.</param>
    /// <param name="bottom">Bottom edge.</param>
    /// <param name="color">Rectangle color.</param>
    public void AddRectangle(float left, float top, float right, float bottom, Color color)
    {
        _commands.Add(new UIDrawCommand(left, top, right, bottom, color,
            Layer: CurrentLayer, Clip: CurrentClip));
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

        _commands.Add(new UIDrawCommand(
            left, top, right, bottom, color, UIDrawCommandType.RoundedRectangle,
            Layer: CurrentLayer, Clip: CurrentClip, CornerRadius: resolvedRadius));
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
            left, top, right, bottom, color, UIDrawCommandType.Ellipse,
            Layer: CurrentLayer, Clip: CurrentClip));
    }

    /// <summary>Adds an analytically stroked ellipse inside a bounding rectangle.</summary>
    /// <param name="left">Outer left edge.</param>
    /// <param name="top">Outer top edge.</param>
    /// <param name="right">Outer right edge.</param>
    /// <param name="bottom">Outer bottom edge.</param>
    /// <param name="thickness">Positive inward stroke thickness.</param>
    /// <param name="color">Stroke color.</param>
    public void AddEllipseStroke(
        float left,
        float top,
        float right,
        float bottom,
        float thickness,
        Color color)
    {
        if (thickness <= 0f)
            throw new ArgumentOutOfRangeException(nameof(thickness));
        _commands.Add(new UIDrawCommand(
            left, top, right, bottom, color, UIDrawCommandType.StrokedEllipse,
            Layer: CurrentLayer, Clip: CurrentClip, StrokeWidth: thickness));
    }

    /// <summary>Adds a stroked line segment.</summary>
    /// <param name="startX">Start X coordinate.</param>
    /// <param name="startY">Start Y coordinate.</param>
    /// <param name="endX">End X coordinate.</param>
    /// <param name="endY">End Y coordinate.</param>
    /// <param name="thickness">Positive line thickness.</param>
    /// <param name="color">Line color.</param>
    public void AddLine(
        float startX,
        float startY,
        float endX,
        float endY,
        float thickness,
        Color color)
    {
        if (thickness <= 0f)
            throw new ArgumentOutOfRangeException(nameof(thickness));
        _commands.Add(new UIDrawCommand(
            startX, startY, endX, endY, color, UIDrawCommandType.Line,
            Layer: CurrentLayer, Clip: CurrentClip, StrokeWidth: thickness));
    }

    /// <summary>Adds a sampled image covering a logical rectangle.</summary>
    /// <param name="texture">Valid renderer-owned texture.</param>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="right">Right edge.</param>
    /// <param name="bottom">Bottom edge.</param>
    public void AddImage(
        TextureHandle texture,
        float left,
        float top,
        float right,
        float bottom)
    {
        if (!texture.IsValid)
            throw new ArgumentException("A valid texture handle is required.", nameof(texture));
        _commands.Add(new UIDrawCommand(
            left, top, right, bottom, Color.White, UIDrawCommandType.Image,
            Layer: CurrentLayer, Clip: CurrentClip, Texture: texture));
    }

    /// <summary>Adds TrueType text over a black background.</summary>
    /// <param name="text">Text to draw.</param>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="color">Text color.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <param name="fontFamily">Renderer-provided typeface.</param>
    public void AddText(
        string text,
        float left,
        float top,
        float fontSize,
        Color color,
        TextFlowDirection direction = TextFlowDirection.LeftToRight,
        UIFontFamily fontFamily = UIFontFamily.Default)
    {
        AddText(text, left, top, fontSize, color, Color.Black, direction, fontFamily);
    }

    /// <summary>Adds anti-aliased TrueType text over a known background.</summary>
    /// <param name="text">Text to draw.</param>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="color">Text color.</param>
    /// <param name="backgroundColor">Color beneath the text.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <param name="fontFamily">Renderer-provided typeface.</param>
    public void AddText(
        string text,
        float left,
        float top,
        float fontSize,
        Color color,
        Color backgroundColor,
        TextFlowDirection direction = TextFlowDirection.LeftToRight,
        UIFontFamily fontFamily = UIFontFamily.Default)
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
            CurrentLayer,
            Clip: CurrentClip,
            TextDirection: direction,
            FontFamily: fontFamily));
    }

    /// <summary>Adds editable TrueType text with a separately rendered caret.</summary>
    /// <param name="text">Text to draw.</param>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="color">Text and caret color.</param>
    /// <param name="backgroundColor">Color beneath the text.</param>
    /// <param name="caretIndex">UTF-16 text index at which to draw the caret.</param>
    /// <param name="direction">Paragraph base direction.</param>
    public void AddTextWithCaret(
        string text,
        float left,
        float top,
        float fontSize,
        Color color,
        Color backgroundColor,
        int caretIndex,
        TextFlowDirection direction = TextFlowDirection.LeftToRight)
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
            caretIndex,
            CurrentClip,
            TextDirection: direction));
    }

    /// <summary>Returns the next globally monotonic paint generation.</summary>
    /// <returns>Paint generation identity.</returns>
    private static ulong NextGeneration()
    {
        return checked((ulong)Interlocked.Increment(ref _nextGeneration));
    }
}
