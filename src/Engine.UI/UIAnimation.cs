using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Selects the normalized timing curve used by a retained animation.</summary>
public enum UIAnimationEasing
{
    /// <summary>Advances at constant speed.</summary>
    Linear,

    /// <summary>Accelerates from rest.</summary>
    EaseIn,

    /// <summary>Decelerates toward rest.</summary>
    EaseOut,

    /// <summary>Accelerates and then decelerates.</summary>
    EaseInOut
}

/// <summary>Base class for reusable, element-owned retained animations.</summary>
public abstract class UIAnimation
{
    private double _elapsed;
    private UIElement? _owner;

    /// <summary>Creates an animation with a finite non-negative duration.</summary>
    /// <param name="duration">Animation duration in seconds.</param>
    protected UIAnimation(double duration)
    {
        if (!double.IsFinite(duration) || duration < 0d)
            throw new ArgumentOutOfRangeException(nameof(duration));
        Duration = duration;
    }

    /// <summary>Gets the animation duration in seconds.</summary>
    public double Duration { get; }

    /// <summary>Gets or sets the timing curve.</summary>
    public UIAnimationEasing Easing { get; set; }

    /// <summary>Gets or sets the host clock advanced by this animation.</summary>
    public UIClockKind Clock { get; set; } = UIClockKind.Unscaled;

    /// <summary>Gets or sets whether reduced-motion mode must preserve this animation.</summary>
    public bool IsEssential { get; set; }

    /// <summary>Gets whether an element currently owns and advances this animation.</summary>
    public bool IsRunning => _owner is not null;

    /// <summary>Gets whether the most recent run reached its final value.</summary>
    public bool IsCompleted { get; private set; }

    /// <summary>Gets whether the most recent run was cancelled before completion.</summary>
    public bool IsCancelled { get; private set; }

    /// <summary>Occurs after the animation reaches its final value.</summary>
    public event Action<UIAnimation>? Completed;

    /// <summary>Occurs after the owning element cancels the animation.</summary>
    public event Action<UIAnimation>? Cancelled;

    /// <summary>Begins a fresh run and applies its initial or reduced-motion final value.</summary>
    /// <param name="owner">Element taking ownership.</param>
    /// <param name="reduceMotion">Whether non-essential motion should complete immediately.</param>
    /// <returns>True when the animation remains active after startup.</returns>
    internal bool Start(UIElement owner, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_owner is not null)
            throw new InvalidOperationException("An animation can be owned by only one element at a time.");
        _owner = owner;
        _elapsed = 0d;
        IsCompleted = false;
        IsCancelled = false;
        if (Duration == 0d || reduceMotion && !IsEssential)
        {
            Apply(1f);
            Finish();
            return false;
        }
        Apply(0f);
        return true;
    }

    /// <summary>Advances one active run using its selected clock delta.</summary>
    /// <param name="deltaTime">Selected clock delta in seconds.</param>
    /// <param name="reduceMotion">Whether non-essential motion should finish now.</param>
    /// <returns>True when the target value was applied.</returns>
    internal bool Advance(double deltaTime, bool reduceMotion)
    {
        if (_owner is null)
            return false;
        if (reduceMotion && !IsEssential)
        {
            Apply(1f);
            Finish();
            return true;
        }
        if (!double.IsFinite(deltaTime) || deltaTime <= 0d)
            return false;
        _elapsed = Math.Min(Duration, _elapsed + deltaTime);
        var progress = (float)(_elapsed / Duration);
        Apply(Ease(progress));
        if (_elapsed >= Duration)
            Finish();
        return true;
    }

    /// <summary>Cancels the current run and releases its owning element.</summary>
    internal void Cancel()
    {
        if (_owner is null)
            return;
        _owner = null;
        IsCancelled = true;
        IsCompleted = false;
        Cancelled?.Invoke(this);
    }

    /// <summary>Publishes completion after the owner removes this animation from its keyed collection.</summary>
    internal void PublishCompleted()
    {
        if (IsCompleted)
            Completed?.Invoke(this);
    }

    /// <summary>Applies one eased normalized value to the animation target.</summary>
    /// <param name="progress">Eased progress from zero through one.</param>
    protected abstract void Apply(float progress);

    /// <summary>Marks the current run complete and releases ownership.</summary>
    private void Finish()
    {
        _owner = null;
        IsCompleted = true;
        IsCancelled = false;
    }

    /// <summary>Transforms linear progress through the selected easing curve.</summary>
    /// <param name="progress">Linear progress from zero through one.</param>
    /// <returns>Eased progress from zero through one.</returns>
    private float Ease(float progress)
    {
        return Easing switch
        {
            UIAnimationEasing.EaseIn => progress * progress,
            UIAnimationEasing.EaseOut => 1f - (1f - progress) * (1f - progress),
            UIAnimationEasing.EaseInOut => progress < 0.5f
                ? 2f * progress * progress
                : 1f - MathF.Pow(-2f * progress + 2f, 2f) / 2f,
            _ => progress
        };
    }
}

/// <summary>Interpolates a scalar value without allocating during advancement.</summary>
public sealed class UIFloatAnimation : UIAnimation
{
    private readonly Action<float> _apply;

    /// <summary>Creates a scalar animation.</summary>
    /// <param name="from">Initial value.</param>
    /// <param name="to">Final value.</param>
    /// <param name="duration">Duration in seconds.</param>
    /// <param name="apply">Target setter invoked while advancing.</param>
    public UIFloatAnimation(float from, float to, double duration, Action<float> apply)
        : base(duration)
    {
        ArgumentNullException.ThrowIfNull(apply);
        From = from;
        To = to;
        _apply = apply;
    }

    /// <summary>Gets the initial scalar.</summary>
    public float From { get; }

    /// <summary>Gets the final scalar.</summary>
    public float To { get; }

    /// <inheritdoc/>
    protected override void Apply(float progress) => _apply(From + (To - From) * progress);
}

/// <summary>Interpolates a two-dimensional vector without allocating during advancement.</summary>
public sealed class UIVector2Animation : UIAnimation
{
    private readonly Action<Vector2> _apply;

    /// <summary>Creates a vector animation.</summary>
    /// <param name="from">Initial vector.</param>
    /// <param name="to">Final vector.</param>
    /// <param name="duration">Duration in seconds.</param>
    /// <param name="apply">Target setter invoked while advancing.</param>
    public UIVector2Animation(Vector2 from, Vector2 to, double duration, Action<Vector2> apply)
        : base(duration)
    {
        ArgumentNullException.ThrowIfNull(apply);
        From = from;
        To = to;
        _apply = apply;
    }

    /// <summary>Gets the initial vector.</summary>
    public Vector2 From { get; }

    /// <summary>Gets the final vector.</summary>
    public Vector2 To { get; }

    /// <inheritdoc/>
    protected override void Apply(float progress) => _apply(Vector2.Lerp(From, To, progress));
}

/// <summary>Interpolates a linear RGB color without allocating during advancement.</summary>
public sealed class UIColorAnimation : UIAnimation
{
    private readonly Action<Color> _apply;

    /// <summary>Creates a color animation.</summary>
    /// <param name="from">Initial color.</param>
    /// <param name="to">Final color.</param>
    /// <param name="duration">Duration in seconds.</param>
    /// <param name="apply">Target setter invoked while advancing.</param>
    public UIColorAnimation(Color from, Color to, double duration, Action<Color> apply)
        : base(duration)
    {
        ArgumentNullException.ThrowIfNull(apply);
        From = from;
        To = to;
        _apply = apply;
    }

    /// <summary>Gets the initial color.</summary>
    public Color From { get; }

    /// <summary>Gets the final color.</summary>
    public Color To { get; }

    /// <inheritdoc/>
    protected override void Apply(float progress) => _apply(Color.Lerp(From, To, progress));
}
