using System.Numerics;
using Engine.Graphics;

namespace Editor;

/// <summary>
/// Owns fly-camera mode, held-key state, pointer look, and movement.
/// </summary>
public sealed class FlyCameraController
{
    private const float MoveSpeed = 5f;
    private const float FastMultiplier = 3f;
    private const float LookSensitivity = 0.003f;

    private readonly PerspectiveCamera _camera;
    private readonly Action<bool> _setMouseCaptured;
    private readonly Action _cancelInteraction;
    private readonly HashSet<InputKey> _pressedKeys = [];
    private bool _hasPointerPosition;
    private Vector2 _pointerPosition;

    /// <summary>Gets whether fly-camera mode is active.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Creates a fly-camera controller.
    /// </summary>
    /// <param name="camera">Camera to manipulate.</param>
    /// <param name="setMouseCaptured">Cursor-capture callback.</param>
    /// <param name="cancelInteraction">Callback that cancels conflicting editor interaction.</param>
    public FlyCameraController(
        PerspectiveCamera camera,
        Action<bool> setMouseCaptured,
        Action cancelInteraction)
    {
        _camera = camera;
        _setMouseCaptured = setMouseCaptured;
        _cancelInteraction = cancelInteraction;
    }

    /// <summary>Handles pointer movement while fly mode is active.</summary>
    /// <param name="position">Pointer position.</param>
    /// <returns>True when fly mode consumed the input.</returns>
    public bool MovePointer(Vector2 position)
    {
        if (!IsActive)
            return false;

        if (_hasPointerPosition)
        {
            var delta = position - _pointerPosition;
            _camera.Rotate(delta.X * LookSensitivity, -delta.Y * LookSensitivity);
        }

        _pointerPosition = position;
        _hasPointerPosition = true;
        return true;
    }

    /// <summary>Handles a key press and mode toggling.</summary>
    /// <param name="key">Pressed key.</param>
    /// <returns>True when the controller consumed the key.</returns>
    public bool KeyDown(InputKey key)
    {
        if (!_pressedKeys.Add(key))
            return IsActive || key == InputKey.F;

        if (key == InputKey.F)
        {
            SetActive(!IsActive);
            _pressedKeys.Clear();
            _pressedKeys.Add(InputKey.F);
            return true;
        }

        if (IsActive && key == InputKey.Escape)
        {
            SetActive(false);
            return true;
        }

        return IsActive;
    }

    /// <summary>Handles a key release.</summary>
    /// <param name="key">Released key.</param>
    /// <returns>True when fly mode consumed the key.</returns>
    public bool KeyUp(InputKey key)
    {
        _pressedKeys.Remove(key);
        return IsActive || key == InputKey.F;
    }

    /// <summary>Releases all held keys and exits fly mode when its viewport loses input ownership.</summary>
    public void ReleaseFocus()
    {
        _pressedKeys.Clear();
        SetActive(false);
    }

    /// <summary>Applies held movement keys for one update.</summary>
    /// <param name="deltaSeconds">Elapsed frame time in seconds.</param>
    public void Update(double deltaSeconds)
    {
        if (!IsActive)
            return;

        var distance = MoveSpeed * (float)deltaSeconds;
        if (IsPressed(InputKey.LeftShift, InputKey.RightShift))
            distance *= FastMultiplier;
        if (_pressedKeys.Contains(InputKey.W))
            _camera.MoveForward(distance);
        if (_pressedKeys.Contains(InputKey.S))
            _camera.MoveForward(-distance);
        if (_pressedKeys.Contains(InputKey.D))
            _camera.MoveRight(distance);
        if (_pressedKeys.Contains(InputKey.A))
            _camera.MoveRight(-distance);
        if (_pressedKeys.Contains(InputKey.Space))
            _camera.MoveUp(distance);
        if (IsPressed(InputKey.LeftControl, InputKey.RightControl))
            _camera.MoveUp(-distance);
    }

    /// <summary>Changes fly mode and its associated capture state.</summary>
    /// <param name="active">New active state.</param>
    private void SetActive(bool active)
    {
        if (IsActive == active)
            return;

        IsActive = active;
        _hasPointerPosition = false;
        _cancelInteraction();
        _setMouseCaptured(active);
    }

    /// <summary>Checks whether either of two equivalent keys is pressed.</summary>
    /// <param name="first">First key.</param>
    /// <param name="second">Second key.</param>
    /// <returns>True when either key is pressed.</returns>
    private bool IsPressed(InputKey first, InputKey second)
    {
        return _pressedKeys.Contains(first) || _pressedKeys.Contains(second);
    }
}
