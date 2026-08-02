namespace Engine.UI;

/// <summary>Describes independent inset values around a rectangular UI box.</summary>
/// <param name="Left">Left inset.</param>
/// <param name="Top">Top inset.</param>
/// <param name="Right">Right inset.</param>
/// <param name="Bottom">Bottom inset.</param>
public readonly record struct Thickness(float Left, float Top, float Right, float Bottom)
{
    /// <summary>Gets a thickness with no inset on any side.</summary>
    public static Thickness Zero { get; } = new(0f);

    /// <summary>Gets the combined horizontal inset.</summary>
    public float Horizontal => Left + Right;

    /// <summary>Gets the combined vertical inset.</summary>
    public float Vertical => Top + Bottom;

    /// <summary>Creates equal inset values on every side.</summary>
    /// <param name="uniform">Inset applied to every side.</param>
    public Thickness(float uniform) : this(uniform, uniform, uniform, uniform)
    {
    }

    /// <summary>Creates symmetric horizontal and vertical inset values.</summary>
    /// <param name="horizontal">Left and right inset.</param>
    /// <param name="vertical">Top and bottom inset.</param>
    public Thickness(float horizontal, float vertical)
        : this(horizontal, vertical, horizontal, vertical)
    {
    }

    /// <summary>Converts a scalar into a uniform thickness.</summary>
    /// <param name="uniform">Inset applied to every side.</param>
    public static implicit operator Thickness(float uniform) => new(uniform);
}
