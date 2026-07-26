using System.Numerics;

namespace Engine.Core;

/// <summary>
/// Base class for 2D scene nodes. Provides 2D position and a Z-order for sorting.
/// Extends Node with screen-space layout support.
/// </summary>
public class Node2D : Node
{
    /// <summary>Gets or sets the 2D position in screen space (pixels).</summary>
    public Vector2 Position2D { get; set; }

    /// <summary>Gets or sets the Z-order for draw sorting (higher = drawn later = on top).</summary>
    public int ZOrder { get; set; }
}
