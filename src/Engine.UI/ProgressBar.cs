using Engine.Graphics;

namespace Engine.UI;

/// <summary>Displays determinate progress or a host-time-driven indeterminate segment.</summary>
public sealed class ProgressBar : UIElement
{
    private readonly UITheme _theme;
    private float _minimum;
    private float _maximum = 1f;
    private float _value;
    private float _animationPhase;
    private bool _isIndeterminate;

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.ProgressBar,
        Name,
        IsIndeterminate ? null : Value.ToString(Culture.NumberFormat),
        IsEnabled,
        true,
        false,
        null,
        NumericValue: IsIndeterminate ? null : Value,
        Minimum: Minimum,
        Maximum: Maximum);

    /// <summary>Gets or sets the minimum progress value.</summary>
    public float Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            _maximum = MathF.Max(_maximum, value);
            _value = Math.Clamp(_value, _minimum, _maximum);
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets the maximum progress value.</summary>
    public float Maximum
    {
        get => _maximum;
        set
        {
            _maximum = MathF.Max(Minimum, value);
            _value = Math.Clamp(_value, _minimum, _maximum);
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets the current clamped progress value.</summary>
    public float Value
    {
        get => _value;
        set
        {
            var resolved = Math.Clamp(value, Minimum, Maximum);
            if (_value == resolved)
                return;
            _value = resolved;
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets whether an animated segment replaces determinate progress.</summary>
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set
        {
            if (_isIndeterminate == value)
                return;
            _isIndeterminate = value;
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets indeterminate cycles per second.</summary>
    public float AnimationSpeed { get; set; } = 0.8f;

    /// <summary>Creates a progress bar.</summary>
    /// <param name="width">Bar width.</param>
    /// <param name="height">Bar height.</param>
    /// <param name="theme">Theme supplying track and fill colors.</param>
    public ProgressBar(float width, float height, UITheme? theme = null) : base(width, height)
    {
        _theme = theme ?? UITheme.Dark;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        drawList.AddRoundedRectangle(Left, Top, Right, Bottom,
            MathF.Min(Height / 2f, 4f), _theme.SurfaceRaised);
        if (Width <= 0f || Height <= 0f)
            return;
        if (IsIndeterminate)
        {
            var segmentWidth = Width * 0.3f;
            var start = MotionPreference == UIMotionPreference.Reduced
                ? Left + (Width - segmentWidth) * 0.5f
                : Left + (Width + segmentWidth) * _animationPhase - segmentWidth;
            drawList.AddRoundedRectangle(start, Top, start + segmentWidth, Bottom,
                MathF.Min(Height / 2f, 4f), _theme.Accent);
            return;
        }
        var ratio = Maximum <= Minimum ? 0f : (Value - Minimum) / (Maximum - Minimum);
        if (ratio > 0f)
            drawList.AddRoundedRectangle(Left, Top, Left + Width * ratio, Bottom,
                MathF.Min(Height / 2f, 4f), _theme.Accent);
    }

    /// <inheritdoc/>
    protected override bool UpdateElement(double deltaTime)
    {
        if (!IsIndeterminate || MotionPreference == UIMotionPreference.Reduced || deltaTime <= 0d)
            return false;
        _animationPhase = (_animationPhase + (float)deltaTime * MathF.Max(0f, AnimationSpeed)) % 1f;
        InvalidateVisual();
        return true;
    }

    /// <inheritdoc/>
    protected override bool IsTimeUpdateActive =>
        IsIndeterminate && MotionPreference == UIMotionPreference.Full;
}
