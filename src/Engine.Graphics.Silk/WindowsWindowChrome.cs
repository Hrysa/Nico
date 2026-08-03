using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;

namespace Engine.Graphics;

/// <summary>Provides borderless native Windows chrome with standard resizing and dragging.</summary>
internal sealed partial class WindowsWindowChrome : IDisposable
{
    private const int WindowProcedureIndex = -4;
    private const uint NonClientCalculateSize = 0x0083;
    private const uint NonClientHitTest = 0x0084;
    private const uint NonClientLeftButtonDown = 0x00A1;
    private const int HitClient = 1;
    private const int HitCaption = 2;
    private const int HitLeft = 10;
    private const int HitRight = 11;
    private const int HitTop = 12;
    private const int HitTopLeft = 13;
    private const int HitTopRight = 14;
    private const int HitBottom = 15;
    private const int HitBottomLeft = 16;
    private const int HitBottomRight = 17;
    private const int SizeFrameMetric = 32;
    private const int PaddedBorderMetric = 92;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint FrameChanged = 0x0020;
    private const uint NearestMonitor = 0x00000002;

    private readonly IntPtr _windowHandle;
    private readonly WindowProcedure _windowProcedure;
    private readonly nint _previousWindowProcedure;
    private bool _disposed;

    /// <summary>Installs borderless non-client handling for one native window.</summary>
    /// <param name="windowHandle">Native Win32 window handle.</param>
    private WindowsWindowChrome(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _windowProcedure = HandleWindowMessage;
        _previousWindowProcedure = SetWindowLongPtr(
            _windowHandle,
            WindowProcedureIndex,
            Marshal.GetFunctionPointerForDelegate(_windowProcedure));
        SetWindowPos(_windowHandle, IntPtr.Zero, 0, 0, 0, 0,
            NoSize | NoMove | NoZOrder | NoActivate | FrameChanged);
    }

    /// <summary>Installs custom chrome on an initialized Silk window.</summary>
    /// <param name="window">Initialized Silk window.</param>
    /// <returns>The installed chrome handler, or <see langword="null"/> on other platforms.</returns>
    internal static WindowsWindowChrome? Apply(Silk.NET.Windowing.IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows())
            return null;

        var win32 = ((INativeWindowSource)window).Native?.Win32;
        if (win32 is null || win32.Value.Item1 == IntPtr.Zero)
            return null;

        return new WindowsWindowChrome(win32.Value.Item1);
    }

    /// <summary>Starts the native Windows move loop for the custom title bar.</summary>
    /// <returns><see langword="true"/> when the native move request was sent.</returns>
    internal bool TryBeginWindowDrag()
    {
        if (_disposed)
            return false;

        ReleaseCapture();
        SendMessage(_windowHandle, NonClientLeftButtonDown, HitCaption, 0);
        return true;
    }

    /// <summary>Handles non-client sizing and hit testing for the borderless window.</summary>
    /// <param name="windowHandle">Native window receiving the message.</param>
    /// <param name="message">Win32 message identifier.</param>
    /// <param name="wordParameter">Message word parameter.</param>
    /// <param name="longParameter">Message long parameter.</param>
    /// <returns>The Win32 message result.</returns>
    private nint HandleWindowMessage(
        IntPtr windowHandle,
        uint message,
        nuint wordParameter,
        nint longParameter)
    {
        if (message == NonClientCalculateSize)
        {
            if (wordParameter != 0 && IsZoomed(windowHandle))
                InsetMaximizedClient(longParameter);
            return 0;
        }

        if (message == NonClientHitTest)
            return HitTest(longParameter);

        return CallWindowProc(
            _previousWindowProcedure,
            windowHandle,
            message,
            wordParameter,
            longParameter);
    }

    /// <summary>Maps a screen coordinate to a native resize edge or corner.</summary>
    /// <param name="longParameter">Packed screen coordinate supplied by Windows.</param>
    /// <returns>A Win32 hit-test value.</returns>
    private nint HitTest(nint longParameter)
    {
        if (IsZoomed(_windowHandle))
            return HitClient;

        if (!GetWindowRect(_windowHandle, out var rectangle))
            return HitClient;

        var x = unchecked((short)(longParameter.ToInt64() & 0xFFFF));
        var y = unchecked((short)((longParameter.ToInt64() >> 16) & 0xFFFF));
        var dpi = GetDpiForWindow(_windowHandle);
        var border = GetSystemMetricsForDpi(SizeFrameMetric, dpi)
            + GetSystemMetricsForDpi(PaddedBorderMetric, dpi);
        var left = x < rectangle.Left + border;
        var right = x >= rectangle.Right - border;
        var top = y < rectangle.Top + border;
        var bottom = y >= rectangle.Bottom - border;

        if (top && left) return HitTopLeft;
        if (top && right) return HitTopRight;
        if (bottom && left) return HitBottomLeft;
        if (bottom && right) return HitBottomRight;
        if (left) return HitLeft;
        if (right) return HitRight;
        if (top) return HitTop;
        if (bottom) return HitBottom;
        return HitClient;
    }

    /// <summary>Insets a maximized client rectangle by Windows' invisible sizing border.</summary>
    /// <param name="calculateSizePointer">Pointer to the first rectangle in NCCALCSIZE_PARAMS.</param>
    private void InsetMaximizedClient(nint calculateSizePointer)
    {
        var rectangle = Marshal.PtrToStructure<NativeRectangle>(calculateSizePointer);
        var dpi = GetDpiForWindow(_windowHandle);
        var border = GetSystemMetricsForDpi(SizeFrameMetric, dpi)
            + GetSystemMetricsForDpi(PaddedBorderMetric, dpi);
        rectangle.Left += border;
        rectangle.Top += border;
        rectangle.Right -= border;
        rectangle.Bottom -= border;
        Marshal.StructureToPtr(rectangle, calculateSizePointer, false);
    }

    /// <summary>Moves the initial normal window fully inside its monitor work area.</summary>
    internal void EnsureVisible()
    {
        if (IsZoomed(_windowHandle) || !GetWindowRect(_windowHandle, out var rectangle))
            return;

        var monitor = MonitorFromWindow(_windowHandle, NearestMonitor);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
            return;

        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        var maximumX = Math.Max(monitorInfo.WorkArea.Left, monitorInfo.WorkArea.Right - width);
        var maximumY = Math.Max(monitorInfo.WorkArea.Top, monitorInfo.WorkArea.Bottom - height);
        var x = Math.Clamp(rectangle.Left, monitorInfo.WorkArea.Left, maximumX);
        var y = Math.Clamp(rectangle.Top, monitorInfo.WorkArea.Top, maximumY);
        if (x != rectangle.Left || y != rectangle.Top)
            SetWindowPos(_windowHandle, IntPtr.Zero, x, y, 0, 0,
                NoSize | NoZOrder | NoActivate);
    }

    /// <summary>Restores the window procedure installed by Silk.NET.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        SetWindowLongPtr(_windowHandle, WindowProcedureIndex, _previousWindowProcedure);
        _disposed = true;
    }

    /// <summary>Writes a pointer-sized native window attribute.</summary>
    /// <param name="windowHandle">Native Win32 window handle.</param>
    /// <param name="index">Attribute index.</param>
    /// <param name="value">New attribute value.</param>
    /// <returns>The previous attribute value.</returns>
    private static nint SetWindowLongPtr(IntPtr windowHandle, int index, nint value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : SetWindowLong32(windowHandle, index, value);
    }

    private delegate nint WindowProcedure(
        IntPtr windowHandle,
        uint message,
        nuint wordParameter,
        nint longParameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal NativeRectangle MonitorArea;
        internal NativeRectangle WorkArea;
        internal uint Flags;
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial nint SetWindowLong32(IntPtr windowHandle, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr64(IntPtr windowHandle, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static partial nint CallWindowProc(
        nint previousWindowProcedure,
        IntPtr windowHandle,
        uint message,
        nuint wordParameter,
        nint longParameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr windowHandle);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(IntPtr windowHandle);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(
        IntPtr windowHandle,
        uint message,
        nuint wordParameter,
        nint longParameter);
}
