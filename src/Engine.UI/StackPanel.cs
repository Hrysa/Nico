using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Arranges UI children sequentially along the vertical axis.
/// </summary>
public class StackPanel : Panel
{
    /// <summary>Gets or sets the space between children.</summary>
    public float Spacing { get; set; }

    /// <summary>Gets or sets the top content inset.</summary>
    public float PaddingTop { get; set; }

    /// <summary>
    /// Creates a vertical stack panel.
    /// </summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Panel width.</param>
    /// <param name="height">Panel height.</param>
    /// <param name="backgroundColor">Panel background color.</param>
    public StackPanel(float x, float y, float width, float height, Color backgroundColor)
        : base(x, y, width, height, backgroundColor)
    {
    }

    /// <summary>Adds and lays out one child.</summary>
    /// <param name="child">Child element.</param>
    public void AddItem(UIElement child)
    {
        AddChild(child);
        Relayout();
    }

    /// <summary>Recomputes the vertical position and width of every UI child.</summary>
    public void Relayout()
    {
        var y = PaddingTop;
        foreach (var child in Children.OfType<UIElement>())
        {
            child.Position = new Vector3(child.Position.X, y, child.Position.Z);
            child.Width = MathF.Max(0f, Width - child.Position.X);
            y += child.Height + Spacing;
        }
    }
}
