using Engine.Graphics;

namespace Engine.UI;

/// <summary>Tracks one held keyboard key and emits device-neutral repeat transitions.</summary>
public sealed class UIKeyRepeatController
{
    private InputKey? _heldKey;
    private InputModifiers _heldModifiers;
    private double _elapsed;
    private bool _started;
    private bool _nativeRepeatObserved;

    /// <summary>Gets or sets the delay before a held key starts repeating.</summary>
    public double Delay { get; set; } = 0.4d;

    /// <summary>Gets or sets the interval between synthesized repeat transitions.</summary>
    public double Interval { get; set; } = 0.05d;

    /// <summary>Gets whether recurring updates are required to synthesize a repeat.</summary>
    public bool IsRepeatPending => _heldKey is not null && !_nativeRepeatObserved;

    /// <summary>Checks whether one key is the currently tracked held key.</summary>
    /// <param name="key">Logical key to inspect.</param>
    /// <returns>True when the key is currently held.</returns>
    internal bool IsHeld(InputKey key) => _heldKey == key;

    /// <summary>Observes one native keyboard transition and updates held-key state.</summary>
    /// <param name="keyEvent">Native device-neutral keyboard transition.</param>
    public void Observe(KeyInputEvent keyEvent)
    {
        if (keyEvent.Key == InputKey.Unknown)
            return;
        if (IsModifierKey(keyEvent.Key))
        {
            if (_heldKey is not null)
                _heldModifiers = keyEvent.Modifiers;
            return;
        }
        if (keyEvent.IsPressed)
        {
            if (keyEvent.IsRepeat && _heldKey == keyEvent.Key)
            {
                _nativeRepeatObserved = true;
            }
            else if (!keyEvent.IsRepeat)
            {
                _heldKey = keyEvent.Key;
                _heldModifiers = keyEvent.Modifiers;
                _elapsed = 0d;
                _started = false;
                _nativeRepeatObserved = false;
            }
        }
        else if (_heldKey == keyEvent.Key)
        {
            Clear();
        }
    }

    /// <summary>Advances unscaled host time and emits bounded repeat transitions.</summary>
    /// <param name="deltaTime">Elapsed host time in seconds.</param>
    /// <param name="emit">Synchronous receiver for synthesized repeat transitions.</param>
    public void Advance(double deltaTime, Action<KeyInputEvent> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        if (!IsRepeatPending || deltaTime <= 0d || !double.IsFinite(deltaTime))
            return;
        _elapsed += deltaTime;
        var emitted = 0;
        while (_heldKey is { } key && emitted < 8)
        {
            var threshold = _started ? Math.Max(0.01d, Interval) : Math.Max(0d, Delay);
            if (_elapsed < threshold)
                break;
            _elapsed -= threshold;
            _started = true;
            emit(new KeyInputEvent(key, true, IsRepeat: true, _heldModifiers));
            emitted++;
        }
    }

    /// <summary>Clears held-key and timing state.</summary>
    public void Clear()
    {
        _heldKey = null;
        _heldModifiers = InputModifiers.None;
        _elapsed = 0d;
        _started = false;
        _nativeRepeatObserved = false;
    }

    /// <summary>Checks whether a key modifies another key rather than repeating independently.</summary>
    /// <param name="key">Logical engine key.</param>
    /// <returns>True for Control, Super, and Shift keys.</returns>
    private static bool IsModifierKey(InputKey key) => key is
        InputKey.LeftControl or InputKey.RightControl or
        InputKey.LeftSuper or InputKey.RightSuper or
        InputKey.LeftShift or InputKey.RightShift;
}
