using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;

namespace Engine.Graphics;

/// <summary>Keeps a Win32 window compositable but invisible until its first frame is ready.</summary>
internal sealed partial class WindowsWindowCloak
{
    private const uint CloakAttribute = 13;
    private readonly IntPtr _windowHandle;

    /// <summary>Creates a compositor cloak around a native window handle.</summary>
    /// <param name="windowHandle">Native Win32 window handle.</param>
    private WindowsWindowCloak(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
    }

    /// <summary>Creates and enables a cloak for an initialized Silk window.</summary>
    /// <param name="window">Initialized native window.</param>
    /// <returns>The active cloak, or null when unavailable.</returns>
    internal static WindowsWindowCloak? Apply(Silk.NET.Windowing.IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows())
            return null;
        var win32 = ((INativeWindowSource)window).Native?.Win32;
        if (win32 is null || win32.Value.Item1 == IntPtr.Zero)
            return null;
        var cloak = new WindowsWindowCloak(win32.Value.Item1);
        return cloak.SetCloaked(true) ? cloak : null;
    }

    /// <summary>Waits for pending desktop composition and atomically reveals the window.</summary>
    internal void Reveal()
    {
        DwmFlush();
        SetCloaked(false);
        DwmFlush();
    }

    /// <summary>Changes the DWM cloak attribute.</summary>
    /// <param name="cloaked">Whether the window should remain compositor-hidden.</param>
    /// <returns>True when DWM accepted the attribute.</returns>
    private bool SetCloaked(bool cloaked)
    {
        var value = cloaked ? 1 : 0;
        return DwmSetWindowAttribute(
            _windowHandle, CloakAttribute, ref value, (uint)sizeof(int)) >= 0;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        IntPtr windowHandle,
        uint attribute,
        ref int value,
        uint valueSize);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmFlush();
}
