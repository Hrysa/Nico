using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Provides device-independent pointer and keyboard input.
/// </summary>
public interface IInputSource
{
    /// <summary>Occurs when the pointer moves.</summary>
    event Action<Vector2>? MouseMove;

    /// <summary>Occurs when a mouse button is pressed.</summary>
    event Action<int>? MouseDown;

    /// <summary>Occurs when a mouse button is released.</summary>
    event Action<int>? MouseUp;

    /// <summary>Occurs when a mouse button is double-clicked.</summary>
    event Action<int>? MouseDoubleClick;

    /// <summary>Occurs when the mouse wheel moves.</summary>
    event Action<float>? MouseScroll;

    /// <summary>Occurs when a key is pressed.</summary>
    event Action<InputKey>? KeyDown;

    /// <summary>Occurs when a key is released.</summary>
    event Action<InputKey>? KeyUp;

    /// <summary>Occurs when keyboard input produces a text character.</summary>
    event Action<char>? TextInput;

    /// <summary>Captures or releases the mouse cursor.</summary>
    /// <param name="captured">True to capture the cursor; otherwise false.</param>
    void SetMouseCaptured(bool captured);
}
