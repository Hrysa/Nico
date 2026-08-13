using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies built-in symbols sourced from Visual Studio Code Codicons.</summary>
public enum IconKind
{
    /// <summary>Draws no symbol.</summary>
    None,

    /// <summary>Draws a renderer-owned texture.</summary>
    Texture,

    /// <summary>Draws a check mark.</summary>
    Check,

    /// <summary>Draws a right-pointing chevron.</summary>
    ChevronRight,

    /// <summary>Draws a downward-pointing chevron.</summary>
    ChevronDown,

    /// <summary>Draws a close cross.</summary>
    Close,

    /// <summary>Draws a plus sign.</summary>
    Plus,

    /// <summary>Draws a minus sign.</summary>
    Minus,

    /// <summary>Draws a magnifying-glass search symbol.</summary>
    Search,

    /// <summary>Draws a right-pointing play symbol.</summary>
    Play,

    /// <summary>Draws a square debug-stop symbol.</summary>
    Stop,

    /// <summary>Draws a settings gear.</summary>
    Settings
}

/// <summary>Displays a texture-backed image or a bundled Visual Studio Code Codicon glyph.</summary>
public sealed class Icon : UIElement
{
    private const string AddGlyph = "\uEA60";
    private const string SearchGlyph = "\uEA6D";
    private const string CloseGlyph = "\uEA76";
    private const string CheckGlyph = "\uEAB2";
    private const string ChevronDownGlyph = "\uEAB4";
    private const string ChevronRightGlyph = "\uEAB6";
    private const string DebugStopGlyph = "\uEAD7";
    private const string RemoveGlyph = "\uEB3B";
    private const string PlayGlyph = "\uEB2C";
    private const string SettingsGlyph = "\uEB51";
    private IconKind _kind;
    private TextureHandle _texture;

    /// <summary>Creates a built-in symbolic icon.</summary>
    /// <param name="kind">Symbol to draw.</param>
    /// <param name="size">Square logical size.</param>
    public Icon(IconKind kind, float size = 16f)
        : base(size, size)
    {
        if (kind == IconKind.Texture)
            throw new ArgumentException("Texture icons require a texture handle.", nameof(kind));
        _kind = kind;
        ConfigureInteraction();
    }

    /// <summary>Creates a texture-backed icon.</summary>
    /// <param name="texture">Renderer-owned icon texture.</param>
    /// <param name="size">Square logical size.</param>
    public Icon(TextureHandle texture, float size = 16f)
        : base(size, size)
    {
        if (!texture.IsValid)
            throw new ArgumentException("A valid texture handle is required.", nameof(texture));
        _kind = IconKind.Texture;
        _texture = texture;
        ConfigureInteraction();
    }

    /// <summary>Gets or sets the displayed icon kind.</summary>
    public IconKind Kind
    {
        get => _kind;
        set
        {
            if (value == IconKind.Texture && !_texture.IsValid)
                throw new InvalidOperationException("Assign Texture before selecting the texture icon kind.");
            if (_kind == value)
                return;
            _kind = value;
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets the texture used by the texture icon kind.</summary>
    public TextureHandle Texture
    {
        get => _texture;
        set
        {
            if (!value.IsValid)
                throw new ArgumentException("A valid texture handle is required.", nameof(value));
            if (_texture == value)
                return;
            _texture = value;
            if (_kind == IconKind.Texture)
                InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        if (_kind == IconKind.Texture)
        {
            drawList.AddImage(_texture, ContentLeft, ContentTop,
                ContentLeft + ContentWidth, ContentTop + ContentHeight);
            return;
        }
        PaintCodicon(drawList);
    }

    /// <summary>Configures icons as clipped non-interactive visual content.</summary>
    private void ConfigureInteraction()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>Emits the official Codicon glyph for the selected built-in symbol.</summary>
    /// <param name="drawList">Draw list receiving the glyph.</param>
    private void PaintCodicon(UIDrawList drawList)
    {
        var glyph = _kind switch
        {
            IconKind.Check => CheckGlyph,
            IconKind.ChevronRight => ChevronRightGlyph,
            IconKind.ChevronDown => ChevronDownGlyph,
            IconKind.Close => CloseGlyph,
            IconKind.Plus => AddGlyph,
            IconKind.Minus => RemoveGlyph,
            IconKind.Search => SearchGlyph,
            IconKind.Play => PlayGlyph,
            IconKind.Stop => DebugStopGlyph,
            IconKind.Settings => SettingsGlyph,
            _ => null
        };
        if (glyph is null)
            return;
        var size = MathF.Min(ContentWidth, ContentHeight);
        drawList.AddText(
            glyph,
            ContentLeft + (ContentWidth - size) * 0.5f,
            ContentTop + (ContentHeight - size) * 0.5f,
            size,
            ForegroundColor,
            BackgroundColor,
            fontFamily: UIFontFamily.Codicon);
    }
}
