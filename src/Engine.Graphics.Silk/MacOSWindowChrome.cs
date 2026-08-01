using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;

namespace Engine.Graphics;

/// <summary>Applies native macOS presentation to a borderless custom-chrome window.</summary>
internal static partial class MacOSWindowChrome
{
    private const double CornerRadius = 10d;
    private const nuint TitledStyleMask = 1u << 0;
    private const nuint ClosableStyleMask = 1u << 1;
    private const nuint MiniaturizableStyleMask = 1u << 2;
    private const nuint ResizableStyleMask = 1u << 3;
    private const nuint FullSizeContentViewStyleMask = 1u << 15;
    private const nuint FullScreenPrimaryCollectionBehavior = 1u << 7;

    /// <summary>Rounds and shadows the native Cocoa window while retaining custom-drawn chrome.</summary>
    /// <param name="window">Initialized Silk window whose Cocoa handle will be styled.</param>
    internal static void Apply(Silk.NET.Windowing.IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS())
            return;

        var cocoaWindow = ((INativeWindowSource)window).Native?.Cocoa;
        if (cocoaWindow is null || cocoaWindow.Value == 0)
            return;

        var nsWindow = cocoaWindow.Value;
        var styleMask = (nuint)Send(nsWindow, "styleMask");
        SendUnsigned(nsWindow, "setStyleMask:", styleMask
            | TitledStyleMask
            | ClosableStyleMask
            | MiniaturizableStyleMask
            | ResizableStyleMask
            | FullSizeContentViewStyleMask);
        SendUnsigned(nsWindow, "setTitleVisibility:", 1u);
        SendBool(nsWindow, "setTitlebarAppearsTransparent:", true);
        HideStandardWindowButton(nsWindow, 0u);
        HideStandardWindowButton(nsWindow, 1u);
        HideStandardWindowButton(nsWindow, 2u);
        var collectionBehavior = (nuint)Send(nsWindow, "collectionBehavior");
        SendUnsigned(nsWindow, "setCollectionBehavior:",
            collectionBehavior | FullScreenPrimaryCollectionBehavior);
        SendBool(nsWindow, "setOpaque:", false);
        SendBool(nsWindow, "setHasShadow:", true);

        var nsColor = GetClass("NSColor");
        var clearColor = Send(nsColor, "clearColor");
        SendObject(nsWindow, "setBackgroundColor:", clearColor);

        SetRounded(window, true);
    }

    /// <summary>Toggles AppKit fullscreen for a properly configured native macOS window.</summary>
    /// <param name="window">Initialized Silk window to toggle.</param>
    /// <returns>True when the request was sent to AppKit.</returns>
    internal static bool TryToggleFullScreen(Silk.NET.Windowing.IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS())
            return false;

        var cocoaWindow = ((INativeWindowSource)window).Native?.Cocoa;
        if (cocoaWindow is null || cocoaWindow.Value == 0)
            return false;

        SendObject(cocoaWindow.Value, "toggleFullScreen:", 0);
        return true;
    }

    /// <summary>Starts AppKit's native window-drag tracking for the current mouse event.</summary>
    /// <param name="window">Initialized Silk window to drag.</param>
    /// <returns>True when AppKit received a current event and started the drag.</returns>
    internal static bool TryBeginWindowDrag(Silk.NET.Windowing.IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS())
            return false;

        var cocoaWindow = ((INativeWindowSource)window).Native?.Cocoa;
        if (cocoaWindow is null || cocoaWindow.Value == 0)
            return false;

        var applicationClass = GetClass("NSApplication");
        var application = Send(applicationClass, "sharedApplication");
        var currentEvent = Send(application, "currentEvent");
        if (currentEvent == 0)
            return false;

        SendObject(cocoaWindow.Value, "performWindowDragWithEvent:", currentEvent);
        return true;
    }

    /// <summary>Hides one native traffic-light button because the editor draws its own control.</summary>
    /// <param name="nsWindow">Native NSWindow pointer.</param>
    /// <param name="buttonType">NSWindowButton value.</param>
    private static void HideStandardWindowButton(nint nsWindow, nuint buttonType)
    {
        var button = SendUnsignedResult(nsWindow, "standardWindowButton:", buttonType);
        if (button != 0)
            SendBool(button, "setHidden:", true);
    }

    /// <summary>Changes native corner clipping for floating or maximized presentation.</summary>
    /// <param name="window">Initialized Silk window whose Cocoa content layer will be updated.</param>
    /// <param name="rounded">Whether the floating-window radius should be applied.</param>
    internal static void SetRounded(Silk.NET.Windowing.IWindow window, bool rounded)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS())
            return;

        var cocoaWindow = ((INativeWindowSource)window).Native?.Cocoa;
        if (cocoaWindow is null || cocoaWindow.Value == 0)
            return;

        var contentView = Send(cocoaWindow.Value, "contentView");
        if (contentView == 0)
            return;

        SendBool(contentView, "setWantsLayer:", true);
        var layer = Send(contentView, "layer");
        if (layer == 0)
            return;

        SendDouble(layer, "setCornerRadius:", rounded ? CornerRadius : 0d);
        SendBool(layer, "setMasksToBounds:", true);
    }

    /// <summary>Looks up one Objective-C class.</summary>
    /// <param name="name">Runtime class name.</param>
    /// <returns>The class pointer.</returns>
    private static nint GetClass(string name)
    {
        return objc_getClass(name);
    }

    /// <summary>Sends an Objective-C message without arguments.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Selector name.</param>
    /// <returns>The returned object pointer.</returns>
    private static nint Send(nint receiver, string selector)
    {
        return objc_msgSend(receiver, sel_registerName(selector));
    }

    /// <summary>Sends an Objective-C message with an object argument.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Selector name.</param>
    /// <param name="value">Object pointer argument.</param>
    private static void SendObject(nint receiver, string selector, nint value)
    {
        objc_msgSendObject(receiver, sel_registerName(selector), value);
    }

    /// <summary>Sends an Objective-C message with a Boolean argument.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Selector name.</param>
    /// <param name="value">Boolean argument.</param>
    private static void SendBool(nint receiver, string selector, bool value)
    {
        objc_msgSendBool(receiver, sel_registerName(selector), value);
    }

    /// <summary>Sends an Objective-C message with a floating-point argument.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Selector name.</param>
    /// <param name="value">Floating-point argument.</param>
    private static void SendDouble(nint receiver, string selector, double value)
    {
        objc_msgSendDouble(receiver, sel_registerName(selector), value);
    }

    /// <summary>Sends an Objective-C message with an unsigned integer argument.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Selector name.</param>
    /// <param name="value">Unsigned integer argument.</param>
    private static void SendUnsigned(nint receiver, string selector, nuint value)
    {
        objc_msgSendUnsigned(receiver, sel_registerName(selector), value);
    }

    /// <summary>Sends an Objective-C message with an unsigned argument and returns an object.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Selector name.</param>
    /// <param name="value">Unsigned integer argument.</param>
    /// <returns>The returned object pointer.</returns>
    private static nint SendUnsignedResult(nint receiver, string selector, nuint value)
    {
        return objc_msgSendUnsignedResult(receiver, sel_registerName(selector), value);
    }

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSend(nint receiver, nint selector);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSendObject(nint receiver, nint selector, nint value);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSendDouble(nint receiver, nint selector, double value);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSendUnsigned(nint receiver, nint selector, nuint value);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSendUnsignedResult(nint receiver, nint selector, nuint value);
}
