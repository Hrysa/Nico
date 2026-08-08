using Engine.Graphics;

namespace Engine.Scripting;

/// <summary>Provides frame-stable keyboard state to all scripts in one active scene.</summary>
public sealed class SceneInput : IDisposable
{
    private readonly bool[] _down = new bool[GetKeyCapacity()];
    private readonly bool[] _pressed = new bool[GetKeyCapacity()];
    private readonly bool[] _released = new bool[GetKeyCapacity()];
    private readonly IInputSource? _source;
    private readonly IInputSourceV2? _sourceV2;
    private bool _disposed;

    /// <summary>Creates keyboard state backed by an optional runtime input source.</summary>
    /// <param name="source">Window or headless input provider.</param>
    internal SceneInput(IInputSource? source)
    {
        _source = source;
        _sourceV2 = source as IInputSourceV2;
        if (_sourceV2 is not null)
        {
            _sourceV2.KeyChanged += HandleKeyChanged;
        }
        else if (_source is not null)
        {
            _source.KeyDown += HandleKeyDown;
            _source.KeyUp += HandleKeyUp;
        }
    }

    /// <summary>Gets whether a key is currently held.</summary>
    /// <param name="key">Key to query.</param>
    /// <returns>True from its press transition through its release transition.</returns>
    public bool IsKeyDown(InputKey key)
    {
        var index = (int)key;
        return (uint)index < (uint)_down.Length && _down[index];
    }

    /// <summary>Gets whether a key became pressed since the previous script update.</summary>
    /// <param name="key">Key to query.</param>
    /// <returns>True for one script update after the initial press.</returns>
    public bool WasKeyPressed(InputKey key)
    {
        var index = (int)key;
        return (uint)index < (uint)_pressed.Length && _pressed[index];
    }

    /// <summary>Gets whether a key became released since the previous script update.</summary>
    /// <param name="key">Key to query.</param>
    /// <returns>True for one script update after release.</returns>
    public bool WasKeyReleased(InputKey key)
    {
        var index = (int)key;
        return (uint)index < (uint)_released.Length && _released[index];
    }

    /// <summary>Clears one-update transitions after every script has observed them.</summary>
    internal void EndUpdate()
    {
        Array.Clear(_pressed);
        Array.Clear(_released);
    }

    /// <summary>Unsubscribes from the runtime input source.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        if (_sourceV2 is not null)
        {
            _sourceV2.KeyChanged -= HandleKeyChanged;
        }
        else if (_source is not null)
        {
            _source.KeyDown -= HandleKeyDown;
            _source.KeyUp -= HandleKeyUp;
        }
        _disposed = true;
    }

    /// <summary>Records a version-two keyboard transition.</summary>
    /// <param name="keyEvent">Device-neutral key event.</param>
    private void HandleKeyChanged(KeyInputEvent keyEvent)
    {
        if (keyEvent.IsRepeat)
            return;
        SetKey(keyEvent.Key, keyEvent.IsPressed);
    }

    /// <summary>Records a legacy key press.</summary>
    /// <param name="key">Pressed key.</param>
    private void HandleKeyDown(InputKey key) => SetKey(key, true);

    /// <summary>Records a legacy key release.</summary>
    /// <param name="key">Released key.</param>
    private void HandleKeyUp(InputKey key) => SetKey(key, false);

    /// <summary>Updates held and transitional state for one key.</summary>
    /// <param name="key">Changed key.</param>
    /// <param name="isPressed">New held state.</param>
    private void SetKey(InputKey key, bool isPressed)
    {
        var index = (int)key;
        if ((uint)index >= (uint)_down.Length || _down[index] == isPressed)
            return;
        _down[index] = isPressed;
        if (isPressed)
            _pressed[index] = true;
        else
            _released[index] = true;
    }

    /// <summary>Computes storage capacity from the largest declared input key.</summary>
    /// <returns>Number of key-state entries required.</returns>
    private static int GetKeyCapacity()
    {
        var maximum = 0;
        var keys = Enum.GetValues<InputKey>();
        for (var index = 0; index < keys.Length; index++)
            maximum = Math.Max(maximum, (int)keys[index]);
        return maximum + 1;
    }
}
