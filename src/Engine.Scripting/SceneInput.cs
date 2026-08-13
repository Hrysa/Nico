using System.Numerics;
using Engine.Graphics;

namespace Engine.Scripting;

/// <summary>Provides frame-stable keyboard and pointer state to all scripts in one active scene.</summary>
public sealed class SceneInput : IDisposable
{
    private readonly bool[] _down = new bool[GetKeyCapacity()];
    private readonly bool[] _pressed = new bool[GetKeyCapacity()];
    private readonly bool[] _released = new bool[GetKeyCapacity()];
    private readonly bool[] _pointerDown = new bool[GetPointerButtonCapacity()];
    private readonly IInputSource? _source;
    private readonly IInputSourceV2? _sourceV2;
    private Vector2 _pointerPosition;
    private Vector2 _pointerDelta;
    private bool _hasLegacyPointerPosition;
    private bool _pointerCaptured;
    private bool _discardNextPointerDelta;
    private bool _disposed;

    /// <summary>Creates keyboard and pointer state backed by an optional runtime input source.</summary>
    /// <param name="source">Window or headless input provider.</param>
    internal SceneInput(IInputSource? source)
    {
        _source = source;
        _sourceV2 = source as IInputSourceV2;
        if (_sourceV2 is not null)
        {
            _sourceV2.KeyChanged += HandleKeyChanged;
            _sourceV2.PointerMoved += HandlePointerMoved;
            _sourceV2.PointerButtonChanged += HandlePointerButtonChanged;
        }
        else if (_source is not null)
        {
            _source.KeyDown += HandleKeyDown;
            _source.KeyUp += HandleKeyUp;
            _source.MouseMove += HandleLegacyPointerMoved;
            _source.MouseDown += HandleLegacyPointerDown;
            _source.MouseUp += HandleLegacyPointerUp;
        }
    }

    /// <summary>Gets the pointer position in logical host coordinates.</summary>
    public Vector2 PointerPosition => _pointerPosition;

    /// <summary>Gets accumulated pointer movement since the previous script update.</summary>
    public Vector2 PointerDelta => _pointerDelta;

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

    /// <summary>Gets whether a pointer button is currently held.</summary>
    /// <param name="button">Pointer button to query.</param>
    /// <returns>True from its press transition through its release transition.</returns>
    public bool IsPointerButtonDown(InputPointerButton button)
    {
        var index = (int)button;
        return (uint)index < (uint)_pointerDown.Length && _pointerDown[index];
    }

    /// <summary>Captures or releases the runtime pointer for unbounded relative movement.</summary>
    /// <param name="captured">True to hide and capture the pointer; false to restore it.</param>
    public void SetPointerCaptured(bool captured)
    {
        if (_pointerCaptured == captured)
            return;
        _pointerCaptured = captured;
        _discardNextPointerDelta = true;
        _hasLegacyPointerPosition = false;
        _source?.SetMouseCaptured(captured);
    }

    /// <summary>Clears one-update transitions after every script has observed them.</summary>
    internal void EndUpdate()
    {
        Array.Clear(_pressed);
        Array.Clear(_released);
        _pointerDelta = Vector2.Zero;
    }

    /// <summary>Unsubscribes from the runtime input source.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        if (_sourceV2 is not null)
        {
            _sourceV2.KeyChanged -= HandleKeyChanged;
            _sourceV2.PointerMoved -= HandlePointerMoved;
            _sourceV2.PointerButtonChanged -= HandlePointerButtonChanged;
        }
        else if (_source is not null)
        {
            _source.KeyDown -= HandleKeyDown;
            _source.KeyUp -= HandleKeyUp;
            _source.MouseMove -= HandleLegacyPointerMoved;
            _source.MouseDown -= HandleLegacyPointerDown;
            _source.MouseUp -= HandleLegacyPointerUp;
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

    /// <summary>Accumulates one version-two pointer movement.</summary>
    /// <param name="pointerEvent">Device-neutral pointer event.</param>
    private void HandlePointerMoved(PointerMoveEvent pointerEvent)
    {
        _pointerPosition = pointerEvent.Position;
        if (_discardNextPointerDelta)
        {
            _discardNextPointerDelta = false;
            return;
        }
        _pointerDelta += pointerEvent.Delta;
    }

    /// <summary>Records one version-two pointer-button transition.</summary>
    /// <param name="pointerEvent">Device-neutral pointer-button event.</param>
    private void HandlePointerButtonChanged(PointerButtonEvent pointerEvent)
    {
        _pointerPosition = pointerEvent.Position;
        SetPointerButton(pointerEvent.Button, pointerEvent.IsPressed);
    }

    /// <summary>Accumulates pointer movement from a legacy absolute-position event.</summary>
    /// <param name="position">Logical host position.</param>
    private void HandleLegacyPointerMoved(Vector2 position)
    {
        if (_discardNextPointerDelta)
        {
            _pointerPosition = position;
            _hasLegacyPointerPosition = true;
            _discardNextPointerDelta = false;
            return;
        }
        if (_hasLegacyPointerPosition)
            _pointerDelta += position - _pointerPosition;
        _pointerPosition = position;
        _hasLegacyPointerPosition = true;
    }

    /// <summary>Records one legacy pointer press.</summary>
    /// <param name="button">Legacy zero-based button identifier.</param>
    private void HandleLegacyPointerDown(int button) =>
        SetPointerButton(ToPointerButton(button), true);

    /// <summary>Records one legacy pointer release.</summary>
    /// <param name="button">Legacy zero-based button identifier.</param>
    private void HandleLegacyPointerUp(int button) =>
        SetPointerButton(ToPointerButton(button), false);

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

    /// <summary>Updates held state for one pointer button.</summary>
    /// <param name="button">Changed pointer button.</param>
    /// <param name="isPressed">New held state.</param>
    private void SetPointerButton(InputPointerButton button, bool isPressed)
    {
        var index = (int)button;
        if ((uint)index < (uint)_pointerDown.Length)
            _pointerDown[index] = isPressed;
    }

    /// <summary>Maps a legacy zero-based mouse button to its device-neutral identity.</summary>
    /// <param name="button">Legacy zero-based button identifier.</param>
    /// <returns>The corresponding device-neutral pointer button.</returns>
    private static InputPointerButton ToPointerButton(int button) => button switch
    {
        0 => InputPointerButton.Primary,
        1 => InputPointerButton.Secondary,
        2 => InputPointerButton.Middle,
        3 => InputPointerButton.Auxiliary1,
        4 => InputPointerButton.Auxiliary2,
        _ => InputPointerButton.Unknown
    };

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

    /// <summary>Computes storage capacity from the largest declared pointer button.</summary>
    /// <returns>Number of pointer-button state entries required.</returns>
    private static int GetPointerButtonCapacity()
    {
        var maximum = 0;
        var buttons = Enum.GetValues<InputPointerButton>();
        for (var index = 0; index < buttons.Length; index++)
            maximum = Math.Max(maximum, (int)buttons[index]);
        return maximum + 1;
    }
}
