using System.Numerics;

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

/// <summary>Identifies how a grid track obtains its size.</summary>
public enum GridUnitType
{
    /// <summary>Uses an exact logical-pixel size.</summary>
    Pixel,
    /// <summary>Shares space remaining after fixed tracks are allocated.</summary>
    Star
}

/// <summary>Describes the size policy of one grid row or column.</summary>
/// <param name="Value">Pixel size or proportional star weight.</param>
/// <param name="UnitType">Sizing policy.</param>
public readonly record struct GridLength(float Value, GridUnitType UnitType)
{
    /// <summary>Creates an exact-size track.</summary>
    /// <param name="pixels">Track size in logical pixels.</param>
    /// <returns>The fixed grid length.</returns>
    public static GridLength Pixels(float pixels) => new(pixels, GridUnitType.Pixel);

    /// <summary>Creates a proportional track.</summary>
    /// <param name="weight">Share of the remaining space.</param>
    /// <returns>The proportional grid length.</returns>
    public static GridLength Star(float weight = 1f) => new(weight, GridUnitType.Star);
}
