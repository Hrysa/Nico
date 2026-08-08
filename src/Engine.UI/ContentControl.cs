using Engine.Graphics;

namespace Engine.UI;

/// <summary>A box that owns and arranges exactly one visual content child.</summary>
public class ContentControl : Control
{
    private UIElement? _content;

    /// <summary>Gets or sets the single visual child displayed inside the content box.</summary>
    public UIElement? Content
    {
        get => _content;
        set
        {
            if (ReferenceEquals(_content, value))
                return;
            if (_content is not null)
                RemoveChild(_content);
            _content = value;
            if (_content is not null)
                AddChild(_content);
        }
    }

    /// <summary>Creates an empty single-content box.</summary>
    /// <param name="width">Outer box width.</param>
    /// <param name="height">Outer box height.</param>
    public ContentControl(float width = 0f, float height = 0f)
        : base(width, height)
    {
    }

    /// <summary>Measures the content and includes the control's padding.</summary>
    /// <param name="availableSize">Space offered for the complete control.</param>
    /// <returns>Desired border-box size.</returns>
    protected override System.Numerics.Vector2 MeasureOverride(System.Numerics.Vector2 availableSize)
    {
        if (TemplateRoot is not null)
            return base.MeasureOverride(availableSize);
        if (_content is null)
            return new System.Numerics.Vector2(Padding.Horizontal, Padding.Vertical);
        var contentAvailable = new System.Numerics.Vector2(
            MathF.Max(0f, availableSize.X - Padding.Horizontal),
            MathF.Max(0f, availableSize.Y - Padding.Vertical));
        _content.Measure(contentAvailable);
        return new System.Numerics.Vector2(
            _content.DesiredSize.X + Padding.Horizontal,
            _content.DesiredSize.Y + Padding.Vertical);
    }

    /// <summary>Arranges the content child within the box's padding.</summary>
    protected override void ArrangeOverride(System.Numerics.Vector2 contentSize)
    {
        if (TemplateRoot is not null)
        {
            base.ArrangeOverride(contentSize);
            return;
        }
        if (_content is null)
            return;
        _content.Measure(contentSize);
        _content.Arrange(new System.Numerics.Vector2(Padding.Left, Padding.Top), contentSize);
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        base.Paint(drawList);
    }
}
