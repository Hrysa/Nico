using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Arranges compact command controls in a horizontal strip.</summary>
public sealed class ToolBar : Panel
{
    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.ToolBar, Name, null, IsEnabled, true, false, null);

    /// <summary>Gets or sets spacing between toolbar items.</summary>
    public float ItemSpacing { get; set; } = 4f;

    /// <summary>Creates an empty toolbar.</summary>
    /// <param name="width">Toolbar width.</param>
    /// <param name="height">Toolbar height.</param>
    /// <param name="theme">Theme supplying background color.</param>
    public ToolBar(float width, float height, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).SurfaceRaised, width, height)
    {
        Padding = new Thickness(4f, 2f);
    }

    /// <summary>Adds one toolbar control.</summary>
    /// <param name="item">Control to append.</param>
    public void AddItem(UIElement item)
    {
        ArgumentNullException.ThrowIfNull(item);
        AddChild(item);
    }

    /// <summary>Adds a visual separator.</summary>
    /// <param name="theme">Theme supplying separator color.</param>
    public void AddSeparator(UITheme? theme = null) => AddChild(new ToolBarSeparator(8f, Height, theme));

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var width = Padding.Horizontal;
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is not UIElement child)
                continue;
            child.Measure(new Vector2(float.PositiveInfinity, ContentHeight));
            width += child.DesiredSize.X + (index > 0 ? ItemSpacing : 0f);
        }
        return new Vector2(width, availableSize.Y);
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var x = Padding.Left;
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is not UIElement child)
                continue;
            if (index > 0)
                x += ItemSpacing;
            child.Arrange(new Vector2(x, Padding.Top),
                new Vector2(child.DesiredSize.X, contentSize.Y));
            x += child.Width + child.Margin.Horizontal;
        }
    }
}

/// <summary>Draws one vertical divider inside a toolbar.</summary>
public sealed class ToolBarSeparator : UIElement
{
    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.Separator, Name, null, IsEnabled, true, false, null);

    private readonly UITheme _theme;

    /// <summary>Creates a toolbar separator.</summary>
    /// <param name="width">Reserved width.</param>
    /// <param name="height">Reserved height.</param>
    /// <param name="theme">Theme supplying line color.</param>
    public ToolBarSeparator(float width, float height, UITheme? theme = null) : base(width, height)
    {
        _theme = theme ?? UITheme.Dark;
        IsHitTestVisible = false;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var x = Left + Width / 2f;
        drawList.AddLine(x, Top + 4f, x, Bottom - 4f, 1f, _theme.BorderStrong);
    }
}
