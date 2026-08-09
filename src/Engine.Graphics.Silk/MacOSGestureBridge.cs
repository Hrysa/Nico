using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;

namespace Engine.Graphics;

/// <summary>Bridges native Cocoa magnification events that GLFW does not expose.</summary>
internal static unsafe partial class MacOSGestureBridge
{
    private static readonly Dictionary<nint, Action<double>> Callbacks = [];
    private static readonly Dictionary<nint, nint> GestureClasses = [];

    /// <summary>Attaches magnification delivery to one initialized Cocoa content view.</summary>
    /// <param name="window">Initialized Silk window.</param>
    /// <param name="callback">Managed incremental-magnification receiver.</param>
    /// <returns>The attached content-view pointer, or zero when unavailable.</returns>
    internal static nint Attach(Silk.NET.Windowing.IWindow window, Action<double> callback)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(callback);
        if (!OperatingSystem.IsMacOS())
            return 0;
        var cocoaWindow = ((INativeWindowSource)window).Native?.Cocoa;
        if (cocoaWindow is null || cocoaWindow.Value == 0)
            return 0;
        var contentView = Send(cocoaWindow.Value, "contentView");
        if (contentView == 0)
            return 0;
        var originalClass = object_getClass(contentView);
        if (originalClass == 0)
            return 0;
        if (!GestureClasses.TryGetValue(originalClass, out var gestureClass))
        {
            var className = $"NicoGestureView_{(nuint)originalClass:X}";
            gestureClass = objc_getClass(className);
            if (gestureClass == 0)
            {
                gestureClass = objc_allocateClassPair(originalClass, className, 0);
                if (gestureClass == 0)
                    return 0;
                var implementation = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)
                    &HandleMagnify;
                if (!class_addMethod(gestureClass, sel_registerName("magnifyWithEvent:"),
                        implementation, "v@:@"))
                {
                    objc_disposeClassPair(gestureClass);
                    return 0;
                }
                objc_registerClassPair(gestureClass);
            }
            GestureClasses[originalClass] = gestureClass;
        }
        object_setClass(contentView, gestureClass);
        Callbacks[contentView] = callback;
        return contentView;
    }

    /// <summary>Stops delivering native magnification for one content view.</summary>
    /// <param name="contentView">Content-view pointer returned by <see cref="Attach"/>.</param>
    internal static void Detach(nint contentView)
    {
        if (contentView != 0)
            Callbacks.Remove(contentView);
    }

    /// <summary>Receives AppKit's <c>magnifyWithEvent:</c> callback.</summary>
    /// <param name="receiver">Cocoa content view.</param>
    /// <param name="selector">Objective-C selector.</param>
    /// <param name="nativeEvent">NSEvent carrying incremental magnification.</param>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HandleMagnify(nint receiver, nint selector, nint nativeEvent)
    {
        try
        {
            if (Callbacks.TryGetValue(receiver, out var callback))
                callback(SendDouble(nativeEvent, "magnification"));
        }
        catch
        {
            // Exceptions must never cross the Objective-C callback boundary.
        }
    }

    /// <summary>Sends an Objective-C message returning an object pointer.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Selector name.</param>
    /// <returns>The returned pointer.</returns>
    private static nint Send(nint receiver, string selector) =>
        objc_msgSend(receiver, sel_registerName(selector));

    /// <summary>Sends an Objective-C message returning a floating-point value.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Selector name.</param>
    /// <returns>The returned floating-point value.</returns>
    private static double SendDouble(nint receiver, string selector) =>
        objc_msgSendDouble(receiver, sel_registerName(selector));

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport("/usr/lib/libobjc.A.dylib")]
    private static partial void objc_registerClassPair(nint cls);

    [LibraryImport("/usr/lib/libobjc.A.dylib")]
    private static partial void objc_disposeClassPair(nint cls);

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool class_addMethod(
        nint cls, nint selector, nint implementation, string types);

    [LibraryImport("/usr/lib/libobjc.A.dylib")]
    private static partial nint object_getClass(nint value);

    [LibraryImport("/usr/lib/libobjc.A.dylib")]
    private static partial nint object_setClass(nint value, nint cls);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSend(nint receiver, nint selector);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial double objc_msgSendDouble(nint receiver, nint selector);
}
