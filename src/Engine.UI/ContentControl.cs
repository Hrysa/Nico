using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>A box that owns and arranges exactly one visual content child.</summary>
public class ContentControl : Box
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
            ArrangeContent();
        }
    }

    /// <summary>Creates an empty single-content box.</summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Outer box width.</param>
    /// <param name="height">Outer box height.</param>
    public ContentControl(float x, float y, float width, float height)
        : base(x, y, width, height)
    {
    }

    /// <summary>Arranges the content child within the box's padding.</summary>
    protected virtual void ArrangeContent()
    {
        if (_content is null)
            return;
        _content.Position = new Vector3(Padding.Left, Padding.Top, _content.Position.Z);
        _content.Width = MathF.Max(0f, Width - Padding.Horizontal);
        _content.Height = MathF.Max(0f, Height - Padding.Vertical);
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        ArrangeContent();
        base.Paint(drawList);
    }
}
