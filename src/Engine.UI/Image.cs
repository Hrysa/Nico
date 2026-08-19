using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Controls how an image maps its intrinsic size into arranged content bounds.</summary>
public enum ImageStretch
{
    /// <summary>Uses the intrinsic logical size without scaling.</summary>
    None,

    /// <summary>Fills both axes independently.</summary>
    Fill,

    /// <summary>Preserves aspect ratio while fitting completely inside the bounds.</summary>
    Uniform,

    /// <summary>Preserves aspect ratio while covering the bounds and clipping overflow.</summary>
    UniformToFill
}

/// <summary>Displays a renderer-owned texture inside its arranged bounds.</summary>
public sealed class Image : UIElement
{
    private TextureHandle _texture;
    private Vector2 _sourceSize;
    private ImageStretch _stretch = ImageStretch.Fill;

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.Image, Name, null, IsEnabled, true, false, null);

    /// <summary>Creates an image element.</summary>
    /// <param name="texture">Renderer-owned texture to display.</param>
    /// <param name="width">Optional explicit width.</param>
    /// <param name="height">Optional explicit height.</param>
    public Image(TextureHandle texture, float width = 0f, float height = 0f)
        : base(width, height)
    {
        if (!texture.IsValid)
            throw new ArgumentException("A valid texture handle is required.", nameof(texture));
        _texture = texture;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>Gets or sets the renderer-owned texture displayed by this element.</summary>
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
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets the texture's intrinsic size in logical pixels.</summary>
    public Vector2 SourceSize
    {
        get => _sourceSize;
        set
        {
            if (value.X < 0f || value.Y < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_sourceSize == value)
                return;
            _sourceSize = value;
            InvalidateMeasure();
        }
    }

    /// <summary>Gets or sets how the intrinsic image maps into its content bounds.</summary>
    public ImageStretch Stretch
    {
        get => _stretch;
        set
        {
            if (_stretch == value)
                return;
            _stretch = value;
            InvalidateMeasure();
        }
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (_sourceSize.X <= 0f || _sourceSize.Y <= 0f)
            return new Vector2(Padding.Horizontal, Padding.Vertical);
        var availableContent = new Vector2(
            MathF.Max(0f, availableSize.X - Padding.Horizontal),
            MathF.Max(0f, availableSize.Y - Padding.Vertical));
        var scale = MathF.Min(1f, MathF.Min(
            availableContent.X / _sourceSize.X,
            availableContent.Y / _sourceSize.Y));
        if (!float.IsFinite(scale))
            scale = 1f;
        return _sourceSize * MathF.Max(0f, scale) +
            new Vector2(Padding.Horizontal, Padding.Vertical);
    }

    /// <inheritdoc/>
    protected override void PaintContent(UIDrawList drawList)
    {
        var destination = ResolveDestination();
        drawList.AddImage(_texture, destination.Left, destination.Top,
            destination.Right, destination.Bottom);
    }

    /// <summary>Resolves centered destination geometry for the selected stretch mode.</summary>
    /// <returns>Logical destination rectangle.</returns>
    private UIClipRect ResolveDestination()
    {
        if (_stretch == ImageStretch.Fill || _sourceSize.X <= 0f || _sourceSize.Y <= 0f)
            return new UIClipRect(ContentLeft, ContentTop,
                ContentLeft + ContentWidth, ContentTop + ContentHeight);
        var scale = _stretch == ImageStretch.None
            ? 1f
            : _stretch == ImageStretch.Uniform
                ? MathF.Min(ContentWidth / _sourceSize.X, ContentHeight / _sourceSize.Y)
                : MathF.Max(ContentWidth / _sourceSize.X, ContentHeight / _sourceSize.Y);
        if (!float.IsFinite(scale))
            scale = 0f;
        var size = _sourceSize * MathF.Max(0f, scale);
        var left = ContentLeft + (ContentWidth - size.X) * 0.5f;
        var top = ContentTop + (ContentHeight - size.Y) * 0.5f;
        return new UIClipRect(left, top, left + size.X, top + size.Y);
    }
}
