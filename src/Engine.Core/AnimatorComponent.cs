namespace Engine.Core;

/// <summary>Configures playback for animation data embedded in a skinned mesh asset.</summary>
public sealed class AnimatorComponent : Component
{
    private string? _clip;
    private bool _playAutomatically = true;
    private bool _loop = true;
    private float _speed = 1f;

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
}
