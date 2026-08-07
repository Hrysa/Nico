using System.Numerics;

namespace Engine.Graphics;

/// <summary>Describes one display work area in window-client logical coordinates.</summary>
/// <param name="Left">Logical left edge.</param>
/// <param name="Top">Logical top edge.</param>
/// <param name="Right">Logical right edge.</param>
/// <param name="Bottom">Logical bottom edge.</param>
/// <param name="DpiScale">Physical pixels per logical pixel.</param>
public readonly record struct DisplayWorkArea(
    float Left, float Top, float Right, float Bottom, float DpiScale = 1f);

/// <summary>Provides native monitor information without exposing a platform windowing library.</summary>
public interface IDisplayService
{
    /// <summary>Gets the monitor work area containing a client-logical anchor.</summary>
    /// <param name="clientAnchor">Anchor in window-client logical coordinates.</param>
    /// <returns>Work area expressed in the same coordinate space.</returns>
    DisplayWorkArea GetWorkArea(Vector2 clientAnchor);
}

/// <summary>Maps logical client positions through a shared physical screen coordinate space.</summary>
public interface IWindowCoordinateMapper
{
    /// <summary>Maps a logical client position to physical screen coordinates.</summary>
    /// <param name="clientPosition">Position relative to the client area's top-left corner.</param>
    /// <returns>Position in physical screen pixels.</returns>
    Vector2 ClientToScreen(Vector2 clientPosition);

    /// <summary>Maps physical screen coordinates to a logical client position.</summary>
    /// <param name="screenPosition">Position in physical screen pixels.</param>
    /// <returns>Position relative to the client area's top-left corner.</returns>
    Vector2 ScreenToClient(Vector2 screenPosition);
}

/// <summary>Exposes the framebuffer density used to rasterize one logical client unit.</summary>
public interface IUIRasterScaleService
{
    /// <summary>Gets physical framebuffer pixels per logical client unit.</summary>
    float RasterScale { get; }
}

/// <summary>Optionally presents captured UI movement before a native event batch finishes draining.</summary>
public interface IInteractiveFrameScheduler
{
    /// <summary>Requests and, when safe, immediately presents the latest interactive UI state.</summary>
    void PresentInteractiveFrame();
}

/// <summary>Identifies the native window system represented by an opaque handle.</summary>
public enum NativeWindowKind
{
    /// <summary>No supported native handle is available.</summary>
    Unknown,

    /// <summary>Microsoft Win32 HWND.</summary>
    Win32,

    /// <summary>Apple Cocoa NSWindow pointer.</summary>
    Cocoa,

    /// <summary>X11 Window identifier.</summary>
    X11,

    /// <summary>Wayland surface pointer.</summary>
    Wayland
}

/// <summary>Describes one opaque platform window handle without exposing Silk.NET.</summary>
/// <param name="Kind">Native window-system kind.</param>
/// <param name="Window">Native window or surface handle.</param>
/// <param name="Display">Optional native display or connection handle.</param>
public readonly record struct NativeWindowHandle(
    NativeWindowKind Kind,
    IntPtr Window,
    IntPtr Display = default);

/// <summary>Provides an optional native handle to platform integration services.</summary>
public interface INativeWindowHandleSource
{
    /// <summary>Gets the current native window handle.</summary>
    /// <returns>A supported native handle, or an unknown default value.</returns>
    NativeWindowHandle GetNativeWindowHandle();
}

/// <summary>Provides synchronous text clipboard access for UI commands.</summary>
public interface IClipboardService
{
    /// <summary>Gets clipboard text, or null when unavailable.</summary>
    /// <returns>Current clipboard text.</returns>
    string? GetText();

    /// <summary>Replaces clipboard text.</summary>
    /// <param name="text">Text to store.</param>
    void SetText(string text);
}
