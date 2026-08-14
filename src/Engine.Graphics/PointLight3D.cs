namespace Engine.Graphics;

/// <summary>Provides an omnidirectional finite-range light source.</summary>
public sealed class PointLight3D : Light3D
{
    /// <summary>Gets or sets the distance at which this light reaches zero contribution.</summary>
    public float Range
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = 10f;
}
