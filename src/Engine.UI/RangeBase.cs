using System.Numerics;

namespace Engine.UI;

/// <summary>Owns shared bounds, clamping, invalidation, and notification for numeric controls.</summary>
/// <typeparam name="T">Numeric value type.</typeparam>
public abstract class RangeBase<T> : UIElement where T : INumber<T>
{
    private T _minimum;
    private T _maximum;
    private T _value;

    /// <summary>Gets or sets the inclusive lower bound.</summary>
    public T Minimum
    {
        get => _minimum;
        set
        {
            if (_minimum == value)
                return;
            _minimum = value;
            if (_maximum < value)
                _maximum = value;
            SetValueCore(_value, notify: true);
            OnRangeChanged();
        }
    }

    /// <summary>Gets or sets the inclusive upper bound.</summary>
    public T Maximum
    {
        get => _maximum;
        set
        {
            var resolved = T.Max(Minimum, value);
            if (_maximum == resolved)
                return;
            _maximum = resolved;
            SetValueCore(_value, notify: true);
            OnRangeChanged();
        }
    }

    /// <summary>Gets or sets the current clamped value.</summary>
    public T Value
    {
        get => _value;
        set => SetValueCore(value, notify: true);
    }

    /// <summary>Occurs after the current value changes.</summary>
    public event Action<T>? ValueChanged;

    /// <summary>Creates a bounded numeric control.</summary>
    /// <param name="width">Optional control width.</param>
    /// <param name="height">Optional control height.</param>
    /// <param name="minimum">Initial inclusive lower bound.</param>
    /// <param name="maximum">Initial inclusive upper bound.</param>
    protected RangeBase(float width, float height, T minimum, T maximum)
        : base(width, height)
    {
        _minimum = minimum;
        _maximum = T.Max(minimum, maximum);
        _value = minimum;
    }

    /// <summary>Sets the clamped value with optional public notification.</summary>
    /// <param name="value">Requested value.</param>
    /// <param name="notify">Whether to raise <see cref="ValueChanged"/>.</param>
    /// <returns>True when the current value changed.</returns>
    protected bool SetValueCore(T value, bool notify)
    {
        var resolved = T.Clamp(value, Minimum, Maximum);
        if (_value == resolved)
            return false;
        var previous = _value;
        _value = resolved;
        OnValueChanged(previous, resolved);
        if (notify)
            ValueChanged?.Invoke(resolved);
        return true;
    }

    /// <summary>Invalidates presentation after either bound changes.</summary>
    protected virtual void OnRangeChanged()
    {
        InvalidateVisual();
    }

    /// <summary>Invalidates presentation after the current value changes.</summary>
    /// <param name="previousValue">Previous clamped value.</param>
    /// <param name="value">New clamped value.</param>
    protected virtual void OnValueChanged(T previousValue, T value)
    {
        InvalidateVisual();
    }
}

/// <summary>Adds normalized float mapping to the shared numeric range lifecycle.</summary>
public abstract class RangeBase : RangeBase<float>
{
    /// <summary>Gets the non-negative distance between the current bounds.</summary>
    protected float RangeLength => MathF.Max(0f, Maximum - Minimum);

    /// <summary>Gets the current value normalized to the inclusive zero-to-one range.</summary>
    protected float NormalizedValue => Maximum <= Minimum
        ? 0f
        : (Value - Minimum) / (Maximum - Minimum);

    /// <summary>Creates a bounded float control.</summary>
    /// <param name="width">Optional control width.</param>
    /// <param name="height">Optional control height.</param>
    /// <param name="minimum">Initial inclusive lower bound.</param>
    /// <param name="maximum">Initial inclusive upper bound.</param>
    protected RangeBase(float width, float height, float minimum, float maximum)
        : base(width, height, minimum, maximum)
    {
    }

    /// <summary>Maps a normalized position into the current range.</summary>
    /// <param name="ratio">Requested normalized position.</param>
    /// <returns>The corresponding clamped range value.</returns>
    protected float ValueFromRatio(float ratio) =>
        Minimum + Math.Clamp(ratio, 0f, 1f) * RangeLength;
}
