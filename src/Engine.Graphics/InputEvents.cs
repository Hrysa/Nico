using System.Numerics;

namespace Engine.Graphics;

/// <summary>Identifies modifier keys active for one input event.</summary>
[Flags]
public enum InputModifiers
{
    /// <summary>No modifiers.</summary>
    None = 0,
    /// <summary>Shift modifier.</summary>
    Shift = 1 << 0,
    /// <summary>Control modifier.</summary>
    Control = 1 << 1,
    /// <summary>Alt or Option modifier.</summary>
    Alt = 1 << 2,
    /// <summary>Windows, Command, or Super modifier.</summary>
    Super = 1 << 3
}

/// <summary>Identifies the logical kind of a pointing device.</summary>
public enum PointerDeviceKind
{
    /// <summary>Mouse pointer.</summary>
    Mouse,
    /// <summary>Touch contact.</summary>
    Touch,
    /// <summary>Pen or stylus pointer.</summary>
    Pen
}

/// <summary>Identifies a renderer-independent pointer button.</summary>
public enum InputPointerButton
{
    /// <summary>Unknown button.</summary>
    Unknown,
    /// <summary>Primary button.</summary>
    Primary,
    /// <summary>Secondary button.</summary>
    Secondary,
    /// <summary>Middle button.</summary>
    Middle,
    /// <summary>First auxiliary button.</summary>
    Auxiliary1,
    /// <summary>Second auxiliary button.</summary>
    Auxiliary2
}

/// <summary>Identifies pointer buttons held during an input event.</summary>
[Flags]
public enum PointerButtons
{
    /// <summary>No buttons.</summary>
    None = 0,
    /// <summary>Primary button.</summary>
    Primary = 1 << 0,
    /// <summary>Secondary button.</summary>
    Secondary = 1 << 1,
    /// <summary>Middle button.</summary>
    Middle = 1 << 2,
    /// <summary>First auxiliary button.</summary>
    Auxiliary1 = 1 << 3,
    /// <summary>Second auxiliary button.</summary>
    Auxiliary2 = 1 << 4
}

/// <summary>Describes device-neutral pointer movement.</summary>
/// <param name="PointerId">Stable pointer identity within the source.</param>
/// <param name="Position">Logical host position.</param>
/// <param name="Delta">Movement since the preceding event.</param>
/// <param name="DeviceKind">Pointing device kind.</param>
/// <param name="Modifiers">Active keyboard modifiers.</param>
/// <param name="PressedButtons">Buttons held during the move.</param>
public readonly record struct PointerMoveEvent(
    int PointerId,
    Vector2 Position,
    Vector2 Delta,
    PointerDeviceKind DeviceKind,
    InputModifiers Modifiers,
    PointerButtons PressedButtons);

/// <summary>Describes a device-neutral pointer button transition.</summary>
/// <param name="PointerId">Stable pointer identity within the source.</param>
/// <param name="Position">Logical host position.</param>
/// <param name="Button">Button that changed.</param>
/// <param name="IsPressed">Whether the button became pressed.</param>
/// <param name="ClickCount">Click count associated with the transition.</param>
/// <param name="DeviceKind">Pointing device kind.</param>
/// <param name="Modifiers">Active keyboard modifiers.</param>
/// <param name="PressedButtons">Buttons held after the transition.</param>
public readonly record struct PointerButtonEvent(
    int PointerId,
    Vector2 Position,
    InputPointerButton Button,
    bool IsPressed,
    int ClickCount,
    PointerDeviceKind DeviceKind,
    InputModifiers Modifiers,
    PointerButtons PressedButtons);

/// <summary>Describes device-neutral pointer-wheel movement.</summary>
/// <param name="PointerId">Stable pointer identity within the source.</param>
/// <param name="Position">Logical host position.</param>
/// <param name="Delta">Horizontal and vertical wheel delta.</param>
/// <param name="Modifiers">Active keyboard modifiers.</param>
public readonly record struct PointerWheelEvent(
    int PointerId,
    Vector2 Position,
    Vector2 Delta,
    InputModifiers Modifiers);

/// <summary>Describes a device-neutral trackpad magnification gesture.</summary>
/// <param name="PointerId">Stable pointer identity within the source.</param>
/// <param name="Position">Logical host position.</param>
/// <param name="Delta">Incremental magnification; positive values zoom in.</param>
/// <param name="Modifiers">Active keyboard modifiers.</param>
public readonly record struct PointerMagnifyEvent(
    int PointerId,
    Vector2 Position,
    float Delta,
    InputModifiers Modifiers);

/// <summary>Optionally supplies native trackpad gestures not represented as pointer wheels.</summary>
public interface IPointerGestureSource
{
    /// <summary>Occurs when a trackpad pinch changes magnification.</summary>
    event Action<PointerMagnifyEvent>? PointerMagnified;
}

/// <summary>Describes one device-neutral keyboard transition.</summary>
/// <param name="Key">Logical engine key.</param>
/// <param name="IsPressed">Whether the key became pressed.</param>
/// <param name="IsRepeat">Whether this is an automatic repeat event.</param>
/// <param name="Modifiers">Active modifiers after the transition.</param>
public readonly record struct KeyInputEvent(
    InputKey Key,
    bool IsPressed,
    bool IsRepeat,
    InputModifiers Modifiers);

/// <summary>Provides versioned, device-neutral input while legacy consumers migrate.</summary>
public interface IInputSourceV2 : IInputSource
{
    /// <summary>Occurs when a pointer moves.</summary>
    event Action<PointerMoveEvent>? PointerMoved;
    /// <summary>Occurs when a pointer button changes state.</summary>
    event Action<PointerButtonEvent>? PointerButtonChanged;
    /// <summary>Occurs when a pointer wheel moves.</summary>
    event Action<PointerWheelEvent>? PointerWheelChanged;
    /// <summary>Occurs when a keyboard key changes state.</summary>
    event Action<KeyInputEvent>? KeyChanged;
    /// <summary>Occurs when committed text is entered.</summary>
    event Action<string>? TextEntered;
}

/// <summary>Identifies one input-method composition transition.</summary>
public enum TextCompositionKind
{
    /// <summary>A composition started.</summary>
    Started,
    /// <summary>The active composition changed.</summary>
    Updated,
    /// <summary>The composition committed.</summary>
    Completed,
    /// <summary>The composition was canceled.</summary>
    Canceled
}

/// <summary>Describes transient or committed text from a platform input method.</summary>
/// <param name="Kind">Composition transition.</param>
/// <param name="Text">Current pre-edit or committed text.</param>
/// <param name="CaretIndex">UTF-16 caret index within the composition text.</param>
/// <param name="SelectionStart">UTF-16 start of the active candidate/conversion range.</param>
/// <param name="SelectionLength">UTF-16 length of the active candidate/conversion range.</param>
public readonly record struct TextCompositionEvent(
    TextCompositionKind Kind,
    string Text,
    int CaretIndex,
    int SelectionStart = 0,
    int SelectionLength = 0);

/// <summary>Optionally supplies native IME composition independently of committed text input.</summary>
public interface ITextInputMethodSource
{
    /// <summary>Occurs when native composition state changes.</summary>
    event Action<TextCompositionEvent>? TextCompositionChanged;
}

/// <summary>Identifies device-neutral UI navigation actions.</summary>
public enum UINavigationAction
{
    /// <summary>Navigate upward.</summary>
    Up,
    /// <summary>Navigate downward.</summary>
    Down,
    /// <summary>Navigate left.</summary>
    Left,
    /// <summary>Navigate right.</summary>
    Right,
    /// <summary>Activate the current item.</summary>
    Submit,
    /// <summary>Cancel or navigate back.</summary>
    Cancel,
    /// <summary>Open the contextual menu.</summary>
    Menu
}

/// <summary>Describes one gamepad or controller navigation transition.</summary>
/// <param name="Action">Logical navigation action.</param>
/// <param name="IsPressed">Whether the action became pressed.</param>
/// <param name="IsRepeat">Whether this is an automatic held-input repeat.</param>
/// <param name="DeviceId">Stable source-device identifier.</param>
public readonly record struct NavigationInputEvent(
    UINavigationAction Action,
    bool IsPressed,
    bool IsRepeat = false,
    int DeviceId = 0);

/// <summary>Supplies device-neutral gamepad and controller UI navigation.</summary>
public interface INavigationInputSource
{
    /// <summary>Occurs when a logical navigation action changes state.</summary>
    event Action<NavigationInputEvent>? NavigationChanged;
}
