using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Publishes retained semantic nodes as AppKit accessibility elements on macOS.</summary>
internal sealed class MacOSAccessibilityAdapter : IDisposable
{
    private static readonly object ClassLock = new();
    private static readonly ConcurrentDictionary<IntPtr, MacAccessibleTarget> Targets = new();
    private static readonly AccessibilityActionCallback PressCallback = PerformPress;
    private static readonly AccessibilityActionCallback IncrementCallback = PerformIncrement;
    private static readonly AccessibilityActionCallback DecrementCallback = PerformDecrement;
    private static IntPtr _accessibleClass;

    private readonly IntPtr _contentView;
    private readonly UIElement _root;
    private readonly UIDispatcher _dispatcher;
    private readonly Action _refresh;
    private readonly Dictionary<long, IntPtr> _elements = [];
    private UIAccessibilitySnapshot _snapshot;
    private bool _disposed;

    /// <summary>Creates and attaches an AppKit accessibility hierarchy.</summary>
    /// <param name="windowHandle">Native NSWindow pointer.</param>
    /// <param name="root">Hosted retained root.</param>
    /// <param name="dispatcher">Owning UI dispatcher.</param>
    /// <param name="refresh">Callback rebuilding host visuals after an action.</param>
    private MacOSAccessibilityAdapter(
        IntPtr windowHandle,
        UIElement root,
        UIDispatcher dispatcher,
        Action refresh)
    {
        _root = root;
        _dispatcher = dispatcher;
        _refresh = refresh;
        _contentView = SendIntPtr(windowHandle, Selector("contentView"));
        _snapshot = UIAccessibilityTree.Capture(root);
        EnsureAccessibleClass();
        Update();
    }

    /// <summary>Creates a native adapter for a Cocoa host window.</summary>
    /// <param name="window">Host window and optional native-handle source.</param>
    /// <param name="root">Hosted retained root.</param>
    /// <param name="dispatcher">Owning UI dispatcher.</param>
    /// <param name="refresh">Callback rebuilding host visuals after an action.</param>
    /// <returns>An installed adapter, or null outside macOS.</returns>
    internal static MacOSAccessibilityAdapter? TryCreate(
        IWindow window,
        UIElement root,
        UIDispatcher dispatcher,
        Action refresh)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(refresh);
        if (!OperatingSystem.IsMacOS() || window is not INativeWindowHandleSource source)
            return null;
        var native = source.GetNativeWindowHandle();
        return native.Kind == NativeWindowKind.Cocoa && native.Window != IntPtr.Zero
            ? new MacOSAccessibilityAdapter(native.Window, root, dispatcher, refresh)
            : null;
    }

    /// <summary>Rebuilds AppKit properties and relationships from current retained state.</summary>
    internal void Update()
    {
        if (_disposed || _contentView == IntPtr.Zero)
            return;
        _snapshot = UIAccessibilityTree.Capture(_root);
        EnsureElements();
        ApplyRootProperties();
        for (var index = 1; index < _snapshot.Nodes.Count; index++)
            ApplyNodeProperties(index);
        ApplyChildren(_contentView, 0);
        for (var index = 1; index < _snapshot.Nodes.Count; index++)
            ApplyChildren(_elements[_snapshot.GetNode(index).Id], index);
        RemoveDetachedElements();
    }

    /// <summary>Allocates native elements for newly visible retained nodes.</summary>
    private void EnsureElements()
    {
        for (var index = 1; index < _snapshot.Nodes.Count; index++)
        {
            var node = _snapshot.GetNode(index);
            if (_elements.ContainsKey(node.Id))
                continue;
            var element = SendIntPtr(SendIntPtr(_accessibleClass, Selector("alloc")),
                Selector("init"));
            if (element == IntPtr.Zero)
                continue;
            _elements.Add(node.Id, element);
            Targets[element] = new MacAccessibleTarget(this, node.Id);
        }
    }

    /// <summary>Applies semantic properties to the host content view.</summary>
    private void ApplyRootProperties()
    {
        var rootNode = _snapshot.Root;
        SendBool(_contentView, Selector("setAccessibilityElement:"), true);
        SetString(_contentView, "setAccessibilityRole:", ToRole(rootNode.SemanticInfo.Role));
        SetString(_contentView, "setAccessibilityLabel:", rootNode.SemanticInfo.Name);
        SetString(_contentView, "setAccessibilityIdentifier:", rootNode.AutomationId);
        SetString(_contentView, "setAccessibilityHelp:", rootNode.SemanticInfo.Description);
        SendBool(_contentView, Selector("setAccessibilityEnabled:"),
            rootNode.SemanticInfo.IsEnabled);
    }

    /// <summary>Applies role, state, frame, and relationship properties to one native element.</summary>
    /// <param name="index">Snapshot node index.</param>
    private void ApplyNodeProperties(int index)
    {
        var node = _snapshot.GetNode(index);
        if (!_elements.TryGetValue(node.Id, out var element))
            return;
        var info = node.SemanticInfo;
        SetString(element, "setAccessibilityRole:", ToRole(info.Role));
        SetString(element, "setAccessibilityLabel:", info.Name);
        SetString(element, "setAccessibilityIdentifier:", node.AutomationId);
        SetString(element, "setAccessibilityHelp:", info.Description);
        SetString(element, "setAccessibilityValue:",
            info.Role == UISemanticRole.PasswordField ? null :
            info.Value ?? info.NumericValue?.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        SendBool(element, Selector("setAccessibilityEnabled:"), info.IsEnabled);
        SendBool(element, Selector("setAccessibilityFocused:"), node.IsFocused);
        SendBool(element, Selector("setAccessibilitySelected:"), info.IsSelected);
        if (info.IsExpanded is { } expanded)
            SendBool(element, Selector("setAccessibilityExpanded:"), expanded);

        var parent = node.ParentIndex <= 0
            ? _contentView
            : _elements.GetValueOrDefault(_snapshot.GetNode(node.ParentIndex).Id);
        SendIntPtrArgument(element, Selector("setAccessibilityParent:"), parent);
        var parentBounds = node.ParentIndex < 0
            ? _snapshot.Root.ScreenBounds
            : _snapshot.GetNode(node.ParentIndex).ScreenBounds;
        var bounds = node.ScreenBounds;
        SendRectangle(element, Selector("setAccessibilityFrameInParentSpace:"), new NativeRect(
            bounds.Left - parentBounds.Left,
            parentBounds.Bottom - bounds.Bottom,
            MathF.Max(0f, bounds.Right - bounds.Left),
            MathF.Max(0f, bounds.Bottom - bounds.Top)));
    }

    /// <summary>Assigns direct native children for one snapshot node.</summary>
    /// <param name="parent">Native parent object.</param>
    /// <param name="parentIndex">Snapshot parent index.</param>
    private void ApplyChildren(IntPtr parent, int parentIndex)
    {
        if (parent == IntPtr.Zero)
            return;
        var arrayClass = ObjcGetClass("NSMutableArray");
        var array = SendIntPtrWithUnsigned(
            arrayClass, Selector("arrayWithCapacity:"), (nuint)_snapshot.Nodes.Count);
        for (var child = _snapshot.GetNode(parentIndex).FirstChildIndex;
            child >= 0;
            child = _snapshot.GetNode(child).NextSiblingIndex)
        {
            if (_elements.TryGetValue(_snapshot.GetNode(child).Id, out var nativeChild))
                SendIntPtrArgument(array, Selector("addObject:"), nativeChild);
        }
        SendIntPtrArgument(parent, Selector("setAccessibilityChildren:"), array);
    }

    /// <summary>Releases native elements no longer present in the retained snapshot.</summary>
    private void RemoveDetachedElements()
    {
        List<long>? removed = null;
        foreach (var pair in _elements)
        {
            if (_snapshot.TryGetNode(pair.Key, out _))
                continue;
            removed ??= [];
            removed.Add(pair.Key);
        }
        if (removed is null)
            return;
        for (var index = 0; index < removed.Count; index++)
        {
            var id = removed[index];
            var element = _elements[id];
            Targets.TryRemove(element, out _);
            SendVoid(element, Selector("release"));
            _elements.Remove(id);
        }
    }

    /// <summary>Queues a native accessibility action on the UI-owning thread.</summary>
    /// <param name="id">Target retained identity.</param>
    /// <param name="requested">Explicit requested action, or none for the primary action.</param>
    /// <returns>True when the current node advertises a compatible action.</returns>
    private bool TryPostAction(long id, UISemanticAction requested)
    {
        if (_disposed || !_snapshot.TryGetNode(id, out var node) || node is null)
            return false;
        var action = requested == UISemanticAction.None
            ? GetPrimaryAction(node.SemanticInfo.Actions)
            : requested;
        if (action == UISemanticAction.None ||
            (node.SemanticInfo.Actions & action) == 0)
            return false;
        _dispatcher.Post(() =>
        {
            if (_disposed || !_snapshot.TryGetNode(id, out var current) || current is null)
                return;
            if (current.Element.PerformSemanticAction(action))
                _refresh();
        });
        return true;
    }

    /// <summary>Chooses the action invoked by a platform press request.</summary>
    /// <param name="actions">Supported semantic actions.</param>
    /// <returns>The primary supported action.</returns>
    private static UISemanticAction GetPrimaryAction(UISemanticAction actions)
    {
        if ((actions & UISemanticAction.Invoke) != 0) return UISemanticAction.Invoke;
        if ((actions & UISemanticAction.Toggle) != 0) return UISemanticAction.Toggle;
        if ((actions & UISemanticAction.Select) != 0) return UISemanticAction.Select;
        if ((actions & UISemanticAction.ExpandCollapse) != 0)
            return UISemanticAction.ExpandCollapse;
        return UISemanticAction.None;
    }

    /// <summary>Registers the managed accessibility subclass once per process.</summary>
    private static void EnsureAccessibleClass()
    {
        if (_accessibleClass != IntPtr.Zero)
            return;
        lock (ClassLock)
        {
            if (_accessibleClass != IntPtr.Zero)
                return;
            var existing = ObjcGetClass("NicoAccessibilityElement");
            if (existing != IntPtr.Zero)
            {
                _accessibleClass = existing;
                return;
            }
            var baseClass = ObjcGetClass("NSAccessibilityElement");
            var created = ObjcAllocateClassPair(baseClass, "NicoAccessibilityElement", 0);
            if (created == IntPtr.Zero)
                throw new InvalidOperationException("Unable to create the AppKit accessibility class.");
            AddActionMethod(created, "accessibilityPerformPress", PressCallback);
            AddActionMethod(created, "accessibilityPerformIncrement", IncrementCallback);
            AddActionMethod(created, "accessibilityPerformDecrement", DecrementCallback);
            ObjcRegisterClassPair(created);
            _accessibleClass = created;
        }
    }

    /// <summary>Adds one boolean accessibility action method to the dynamic AppKit subclass.</summary>
    /// <param name="nativeClass">Dynamic Objective-C class.</param>
    /// <param name="selectorName">Action selector name.</param>
    /// <param name="callback">Managed callback retained for process lifetime.</param>
    private static void AddActionMethod(
        IntPtr nativeClass,
        string selectorName,
        AccessibilityActionCallback callback)
    {
        if (!ClassAddMethod(nativeClass, Selector(selectorName),
            Marshal.GetFunctionPointerForDelegate(callback), "c@:"))
            throw new InvalidOperationException(
                $"Unable to register AppKit accessibility action '{selectorName}'.");
    }

    /// <summary>Dispatches the native primary-action selector.</summary>
    /// <param name="self">Native accessibility element.</param>
    /// <param name="selector">Objective-C selector.</param>
    /// <returns>One when the action was accepted.</returns>
    private static byte PerformPress(IntPtr self, IntPtr selector) =>
        Targets.TryGetValue(self, out var target) &&
        target.Owner.TryPostAction(target.Id, UISemanticAction.None) ? (byte)1 : (byte)0;

    /// <summary>Dispatches the native increment selector.</summary>
    /// <param name="self">Native accessibility element.</param>
    /// <param name="selector">Objective-C selector.</param>
    /// <returns>One when the action was accepted.</returns>
    private static byte PerformIncrement(IntPtr self, IntPtr selector) =>
        Targets.TryGetValue(self, out var target) &&
        target.Owner.TryPostAction(target.Id, UISemanticAction.Increment) ? (byte)1 : (byte)0;

    /// <summary>Dispatches the native decrement selector.</summary>
    /// <param name="self">Native accessibility element.</param>
    /// <param name="selector">Objective-C selector.</param>
    /// <returns>One when the action was accepted.</returns>
    private static byte PerformDecrement(IntPtr self, IntPtr selector) =>
        Targets.TryGetValue(self, out var target) &&
        target.Owner.TryPostAction(target.Id, UISemanticAction.Decrement) ? (byte)1 : (byte)0;

    /// <summary>Maps semantic roles to AppKit role strings.</summary>
    /// <param name="role">Renderer-independent semantic role.</param>
    /// <returns>AppKit accessibility role.</returns>
    private static string ToRole(UISemanticRole role) => role switch
    {
        UISemanticRole.Text => "AXStaticText",
        UISemanticRole.Button or UISemanticRole.ToggleButton => "AXButton",
        UISemanticRole.CheckBox => "AXCheckBox",
        UISemanticRole.RadioButton => "AXRadioButton",
        UISemanticRole.Switch => "AXCheckBox",
        UISemanticRole.Slider => "AXSlider",
        UISemanticRole.ProgressBar => "AXProgressIndicator",
        UISemanticRole.ComboBox => "AXComboBox",
        UISemanticRole.List => "AXList",
        UISemanticRole.ListItem => "AXRow",
        UISemanticRole.Tree => "AXOutline",
        UISemanticRole.TreeItem => "AXRow",
        UISemanticRole.TabList => "AXTabGroup",
        UISemanticRole.Menu => "AXMenu",
        UISemanticRole.MenuItem => "AXMenuItem",
        UISemanticRole.Image => "AXImage",
        UISemanticRole.Dialog => "AXGroup",
        UISemanticRole.ToolBar => "AXToolbar",
        UISemanticRole.Separator => "AXSplitter",
        UISemanticRole.TextField or UISemanticRole.PasswordField => "AXTextField",
        _ => "AXGroup"
    };

    /// <summary>Sets an optional NSString property on an Objective-C object.</summary>
    /// <param name="target">Native target object.</param>
    /// <param name="selectorName">Setter selector.</param>
    /// <param name="value">Managed text or null.</param>
    private static void SetString(IntPtr target, string selectorName, string? value)
    {
        var nativeString = value is null ? IntPtr.Zero : CreateString(value);
        SendIntPtrArgument(target, Selector(selectorName), nativeString);
    }

    /// <summary>Creates an autoreleased NSString.</summary>
    /// <param name="value">Managed UTF-16 text.</param>
    /// <returns>Native NSString pointer.</returns>
    private static IntPtr CreateString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return SendIntPtrArgumentWithResult(
                ObjcGetClass("NSString"), Selector("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>Gets an Objective-C selector.</summary>
    /// <param name="name">Selector name.</param>
    /// <returns>Native selector pointer.</returns>
    private static IntPtr Selector(string name) => SelRegisterName(name);

    /// <summary>Detaches the hierarchy and releases owned AppKit elements.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_contentView != IntPtr.Zero)
        {
            var empty = SendIntPtr(ObjcGetClass("NSArray"), Selector("array"));
            SendIntPtrArgument(_contentView, Selector("setAccessibilityChildren:"), empty);
        }
        foreach (var element in _elements.Values)
        {
            Targets.TryRemove(element, out _);
            SendVoid(element, Selector("release"));
        }
        _elements.Clear();
    }

    /// <summary>Handles an Objective-C accessibility action.</summary>
    /// <param name="self">Receiving native object.</param>
    /// <param name="selector">Invoked selector.</param>
    /// <returns>One when the action was accepted.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte AccessibilityActionCallback(IntPtr self, IntPtr selector);

    /// <summary>Associates one native object with a retained semantic identity.</summary>
    /// <param name="Owner">Owning adapter.</param>
    /// <param name="Id">Stable retained identity.</param>
    private readonly record struct MacAccessibleTarget(MacOSAccessibilityAdapter Owner, long Id);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(double X, double Y, double Width, double Height);

    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";

    /// <summary>Looks up an Objective-C class.</summary>
    /// <param name="name">Class name.</param>
    /// <returns>Native class pointer.</returns>
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern IntPtr ObjcGetClass(string name);

    /// <summary>Registers or looks up an Objective-C selector.</summary>
    /// <param name="name">Selector name.</param>
    /// <returns>Native selector pointer.</returns>
    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterName(string name);

    /// <summary>Allocates an Objective-C subclass.</summary>
    /// <param name="superclass">Native superclass.</param>
    /// <param name="name">Subclass name.</param>
    /// <param name="extraBytes">Additional instance bytes.</param>
    /// <returns>The allocated native class.</returns>
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_allocateClassPair")]
    private static extern IntPtr ObjcAllocateClassPair(
        IntPtr superclass, string name, nuint extraBytes);

    /// <summary>Registers an allocated Objective-C class.</summary>
    /// <param name="nativeClass">Class to register.</param>
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_registerClassPair")]
    private static extern void ObjcRegisterClassPair(IntPtr nativeClass);

    /// <summary>Adds one method to an Objective-C class.</summary>
    /// <param name="nativeClass">Class receiving the method.</param>
    /// <param name="selector">Method selector.</param>
    /// <param name="implementation">Implementation function pointer.</param>
    /// <param name="types">Objective-C type encoding.</param>
    /// <returns>True when the method was added.</returns>
    [DllImport(ObjectiveCLibrary, EntryPoint = "class_addMethod")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ClassAddMethod(
        IntPtr nativeClass, IntPtr selector, IntPtr implementation, string types);

    /// <summary>Sends a parameterless message returning an object pointer.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Message selector.</param>
    /// <returns>The returned native pointer.</returns>
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    /// <summary>Sends a pointer argument and returns an object pointer.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Message selector.</param>
    /// <param name="argument">Pointer argument.</param>
    /// <returns>The returned native pointer.</returns>
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrArgumentWithResult(
        IntPtr receiver, IntPtr selector, IntPtr argument);

    /// <summary>Sends an unsigned argument and returns an object pointer.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Message selector.</param>
    /// <param name="argument">Unsigned argument.</param>
    /// <returns>The returned native pointer.</returns>
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrWithUnsigned(
        IntPtr receiver, IntPtr selector, nuint argument);

    /// <summary>Sends a pointer argument without a return value.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Message selector.</param>
    /// <param name="argument">Pointer argument.</param>
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendIntPtrArgument(
        IntPtr receiver, IntPtr selector, IntPtr argument);

    /// <summary>Sends a Boolean argument without a return value.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Message selector.</param>
    /// <param name="argument">Boolean argument.</param>
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendBool(
        IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool argument);

    /// <summary>Sends a rectangle argument without a return value.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Message selector.</param>
    /// <param name="rectangle">Rectangle argument.</param>
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendRectangle(
        IntPtr receiver, IntPtr selector, NativeRect rectangle);

    /// <summary>Sends a parameterless message without a return value.</summary>
    /// <param name="receiver">Message receiver.</param>
    /// <param name="selector">Message selector.</param>
    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);
}
