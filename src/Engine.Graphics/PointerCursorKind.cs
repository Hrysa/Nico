namespace Engine.Graphics;

/// <summary>Represents logical pointer cursor styles requested by UI controls.</summary>
public enum PointerCursorKind
{
    /// <summary>Uses the default platform pointer cursor.</summary>
    Default,

    /// <summary>Uses a horizontal resize cursor (left-right).</summary>
    HorizontalResize,

    /// <summary>Uses a vertical resize cursor (up-down).</summary>
    VerticalResize
}
