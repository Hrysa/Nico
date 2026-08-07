namespace Engine.UI;

/// <summary>Controls horizontal placement inside an allocated layout slot.</summary>
public enum HorizontalAlignment
{
    /// <summary>Fills the available width.</summary>
    Stretch,
    /// <summary>Uses the desired width at the left edge.</summary>
    Left,
    /// <summary>Uses the desired width at the horizontal center.</summary>
    Center,
    /// <summary>Uses the desired width at the right edge.</summary>
    Right
}

/// <summary>Controls vertical placement inside an allocated layout slot.</summary>
public enum VerticalAlignment
{
    /// <summary>Fills the available height.</summary>
    Stretch,
    /// <summary>Uses the desired height at the top edge.</summary>
    Top,
    /// <summary>Uses the desired height at the vertical center.</summary>
    Center,
    /// <summary>Uses the desired height at the bottom edge.</summary>
    Bottom
}

/// <summary>Controls the main axis used by a flex container.</summary>
public enum FlexDirection
{
    /// <summary>Places children from left to right.</summary>
    Row,
    /// <summary>Places children from right to left.</summary>
    RowReverse,
    /// <summary>Places children from top to bottom.</summary>
    Column,
    /// <summary>Places children from bottom to top.</summary>
    ColumnReverse
}

/// <summary>Controls distribution of unused space along a flex axis.</summary>
public enum FlexJustify
{
    /// <summary>Packs items against the leading edge.</summary>
    Start,
    /// <summary>Packs items at the center.</summary>
    Center,
    /// <summary>Packs items against the trailing edge.</summary>
    End,
    /// <summary>Places equal space between adjacent items.</summary>
    SpaceBetween,
    /// <summary>Places equal space around every item.</summary>
    SpaceAround,
    /// <summary>Places equal space between items and container edges.</summary>
    SpaceEvenly
}

/// <summary>Controls placement along the cross axis of a flex line.</summary>
public enum FlexAlignment
{
    /// <summary>Uses the container's alignment when applied to a child.</summary>
    Auto,
    /// <summary>Places items against the cross-axis leading edge.</summary>
    Start,
    /// <summary>Places items at the cross-axis center.</summary>
    Center,
    /// <summary>Places items against the cross-axis trailing edge.</summary>
    End,
    /// <summary>Expands auto-sized items across the line's cross axis.</summary>
    Stretch
}

/// <summary>Controls whether flex items form additional lines.</summary>
public enum FlexWrap
{
    /// <summary>Keeps every item on one line.</summary>
    NoWrap,
    /// <summary>Moves overflowing items onto additional lines.</summary>
    Wrap
}
