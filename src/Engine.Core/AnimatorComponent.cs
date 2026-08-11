namespace Engine.Core;

/// <summary>Configures playback for embedded or separately imported skeletal animation.</summary>
public sealed class AnimatorComponent : Component
{
    private AssetReference? _animationSource;
    private AssetReference? _animationSet;
    private string? _clip;
    private bool _playAutomatically = true;
    private bool _loop = true;
    private float _speed = 1f;
    private float _defaultFadeDuration = 0.2f;

    /// <summary>Gets or sets an optional standalone skeletal-animation artifact.</summary>
    public AssetReference? AnimationSource
    {
        get => _animationSource;
        set
        {
            if (_animationSource == value)
                return;
            _animationSource = value;
            Owner?.NotifyComponentChanged(NodeChangeKind.ComponentValues);
        }
    }

    /// <summary>Gets or sets an optional project-owned animation-set artifact.</summary>
    public AssetReference? AnimationSet
    {
        get => _animationSet;
        set
        {
            if (_animationSet == value)
                return;
            _animationSet = value;
            Owner?.NotifyComponentChanged(NodeChangeKind.ComponentValues);
        }
    }

    /// <summary>Gets or sets the preferred imported clip name, or null for the first clip.</summary>
    public string? Clip
    {
        get => _clip;
        set
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("An animation clip name cannot be empty.", nameof(value));
            if (_clip == value)
                return;
            _clip = value;
            Owner?.NotifyComponentChanged(NodeChangeKind.ComponentValues);
        }
    }

    /// <summary>Gets or sets the default imported clip name, or null for the first clip.</summary>
    public string? DefaultClip
    {
        get => Clip;
        set => Clip = value;
    }

    /// <summary>Gets or sets whether playback begins when the runtime scene is attached.</summary>
    public bool PlayAutomatically
    {
        get => _playAutomatically;
        set
        {
            if (_playAutomatically == value)
                return;
            _playAutomatically = value;
            Owner?.NotifyComponentChanged(NodeChangeKind.ComponentValues);
        }
    }

    /// <summary>Gets or sets whether playback wraps at the end of the clip.</summary>
    public bool Loop
    {
        get => _loop;
        set
        {
            if (_loop == value)
                return;
            _loop = value;
            Owner?.NotifyComponentChanged(NodeChangeKind.ComponentValues);
        }
    }

    /// <summary>Gets or sets the signed playback-rate multiplier.</summary>
    public float Speed
    {
        get => _speed;
        set
        {
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_speed == value)
                return;
            _speed = value;
            Owner?.NotifyComponentChanged(NodeChangeKind.ComponentValues);
        }
    }

    /// <summary>Gets or sets the default script-driven cross-fade duration in seconds.</summary>
    public float DefaultFadeDuration
    {
        get => _defaultFadeDuration;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_defaultFadeDuration == value)
                return;
            _defaultFadeDuration = value;
            Owner?.NotifyComponentChanged(NodeChangeKind.ComponentValues);
        }
    }
}
