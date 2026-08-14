namespace Engine.Graphics;

/// <summary>Provides a finite-range cone light emitted along local negative Z.</summary>
public sealed class SpotLight3D : Light3D
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

    /// <summary>Gets or sets the outer cone angle in degrees.</summary>
    public float OuterAngle
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value <= InnerAngle || value > 89f)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = 35f;

    /// <summary>Gets or sets the fully illuminated inner cone angle in degrees.</summary>
    public float InnerAngle
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value < 0f || value >= OuterAngle)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = 25f;
}
