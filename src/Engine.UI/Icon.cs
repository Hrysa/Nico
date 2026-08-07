using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies built-in resolution-independent icon geometry.</summary>
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
    Search
}

/// <summary>Displays a texture-backed or resolution-independent symbolic icon.</summary>
public sealed class Icon : UIElement
{
    private IconKind _kind;
    private TextureHandle _texture;
    private float _strokeThickness = 1.5f;

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

    /// <summary>Gets or sets symbolic stroke thickness in logical pixels.</summary>
    public float StrokeThickness
    {
        get => _strokeThickness;
        set
        {
            if (value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_strokeThickness == value)
                return;
            _strokeThickness = value;
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
        PaintSymbol(drawList);
    }

    /// <summary>Configures icons as clipped non-interactive visual content.</summary>
    private void ConfigureInteraction()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>Emits normalized line geometry for the selected built-in symbol.</summary>
    /// <param name="drawList">Draw list receiving strokes.</param>
    private void PaintSymbol(UIDrawList drawList)
    {
        switch (_kind)
        {
            case IconKind.Check:
                AddLine(drawList, 0.16f, 0.52f, 0.40f, 0.76f);
                AddLine(drawList, 0.40f, 0.76f, 0.84f, 0.25f);
                break;
            case IconKind.ChevronRight:
                AddLine(drawList, 0.34f, 0.20f, 0.68f, 0.50f);
                AddLine(drawList, 0.68f, 0.50f, 0.34f, 0.80f);
                break;
            case IconKind.ChevronDown:
                AddLine(drawList, 0.20f, 0.34f, 0.50f, 0.68f);
                AddLine(drawList, 0.50f, 0.68f, 0.80f, 0.34f);
                break;
            case IconKind.Close:
                AddLine(drawList, 0.22f, 0.22f, 0.78f, 0.78f);
                AddLine(drawList, 0.78f, 0.22f, 0.22f, 0.78f);
                break;
            case IconKind.Plus:
                AddLine(drawList, 0.20f, 0.50f, 0.80f, 0.50f);
                AddLine(drawList, 0.50f, 0.20f, 0.50f, 0.80f);
                break;
            case IconKind.Minus:
                AddLine(drawList, 0.20f, 0.50f, 0.80f, 0.50f);
                break;
            case IconKind.Search:
                PaintSearch(drawList);
                break;
        }
    }

    /// <summary>Draws an octagonal search lens and handle.</summary>
    /// <param name="drawList">Draw list receiving strokes.</param>
    private void PaintSearch(UIDrawList drawList)
    {
        AddLine(drawList, 0.25f, 0.18f, 0.55f, 0.18f);
        AddLine(drawList, 0.55f, 0.18f, 0.72f, 0.35f);
        AddLine(drawList, 0.72f, 0.35f, 0.72f, 0.55f);
        AddLine(drawList, 0.72f, 0.55f, 0.55f, 0.72f);
        AddLine(drawList, 0.55f, 0.72f, 0.35f, 0.72f);
        AddLine(drawList, 0.35f, 0.72f, 0.18f, 0.55f);
        AddLine(drawList, 0.18f, 0.55f, 0.18f, 0.35f);
        AddLine(drawList, 0.18f, 0.35f, 0.25f, 0.18f);
        AddLine(drawList, 0.62f, 0.64f, 0.84f, 0.86f);
    }

    /// <summary>Adds a line using normalized content-box coordinates.</summary>
    /// <param name="drawList">Draw list receiving the stroke.</param>
    /// <param name="startX">Normalized start X.</param>
    /// <param name="startY">Normalized start Y.</param>
    /// <param name="endX">Normalized end X.</param>
    /// <param name="endY">Normalized end Y.</param>
    private void AddLine(
        UIDrawList drawList,
        float startX,
        float startY,
        float endX,
        float endY)
    {
        drawList.AddLine(
            ContentLeft + ContentWidth * startX,
            ContentTop + ContentHeight * startY,
            ContentLeft + ContentWidth * endX,
            ContentTop + ContentHeight * endY,
            _strokeThickness,
            ForegroundColor);
    }
}
