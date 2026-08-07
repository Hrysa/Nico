using System.Runtime.InteropServices;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Exposes a retained semantic snapshot through native Windows accessibility.</summary>
internal sealed class WindowsAccessibilityAdapter : IDisposable
{
    private const int WindowProcedureIndex = -4;
    private const uint GetObjectMessage = 0x003D;
    private const int ClientObjectId = -4;
    private const int UiaRootObjectId = -25;

    private readonly IntPtr _windowHandle;
    private readonly UIElement _root;
    private readonly IWindowCoordinateMapper? _coordinateMapper;
    private readonly UIDispatcher _dispatcher;
    private readonly Action _refresh;
    private readonly WindowProcedure _windowProcedure;
    private readonly nint _previousWindowProcedure;
    private readonly object _providerLock = new();
    private readonly Dictionary<long, AccessibleProvider> _providers = [];
    private UIAccessibilitySnapshot _snapshot;
    private bool _disposed;

    /// <summary>Installs one native accessibility provider on a Win32 host window.</summary>
    /// <param name="windowHandle">Native HWND.</param>
    /// <param name="root">Hosted retained root.</param>
    /// <param name="coordinateMapper">Client-to-screen mapper.</param>
    /// <param name="dispatcher">Owning UI dispatcher.</param>
    /// <param name="refresh">Callback rebuilding host visuals after an action.</param>
    private WindowsAccessibilityAdapter(
        IntPtr windowHandle,
        UIElement root,
        IWindowCoordinateMapper? coordinateMapper,
        UIDispatcher dispatcher,
        Action refresh)
    {
        _windowHandle = windowHandle;
        _root = root;
        _coordinateMapper = coordinateMapper;
        _dispatcher = dispatcher;
        _refresh = refresh;
        _snapshot = UIAccessibilityTree.Capture(root, coordinateMapper);
        _windowProcedure = HandleWindowMessage;
        _previousWindowProcedure = SetWindowLongPtr(
            windowHandle,
            WindowProcedureIndex,
            Marshal.GetFunctionPointerForDelegate(_windowProcedure));
    }

    /// <summary>Creates a native adapter for a supported host window.</summary>
    /// <param name="window">Host window and optional native-handle source.</param>
    /// <param name="root">Hosted retained root.</param>
    /// <param name="dispatcher">Owning UI dispatcher.</param>
    /// <param name="refresh">Callback rebuilding host visuals after an action.</param>
    /// <returns>An installed Windows adapter, or null on unsupported platforms.</returns>
    internal static WindowsAccessibilityAdapter? TryCreate(
        IWindow window,
        UIElement root,
        UIDispatcher dispatcher,
        Action refresh)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(refresh);
        if (!OperatingSystem.IsWindows() || window is not INativeWindowHandleSource source)
            return null;
        var native = source.GetNativeWindowHandle();
        return native.Kind == NativeWindowKind.Win32 && native.Window != IntPtr.Zero
            ? new WindowsAccessibilityAdapter(
                native.Window, root, window as IWindowCoordinateMapper, dispatcher, refresh)
            : null;
    }

    /// <summary>Refreshes the immutable tree when accessibility clients are listening.</summary>
    internal void Update()
    {
        if (!_disposed && UiaClientsAreListening())
            Volatile.Write(ref _snapshot, UIAccessibilityTree.Capture(_root, _coordinateMapper));
    }

    /// <summary>Gets the current snapshot without touching mutable UI state.</summary>
    /// <returns>The latest immutable accessibility tree.</returns>
    private UIAccessibilitySnapshot GetSnapshot() => Volatile.Read(ref _snapshot);

    /// <summary>Gets or creates a stable provider for one retained element identity.</summary>
    /// <param name="id">Stable retained element identity.</param>
    /// <returns>The provider serving that identity.</returns>
    private AccessibleProvider GetProvider(long id)
    {
        lock (_providerLock)
        {
            if (!_providers.TryGetValue(id, out var provider))
            {
                provider = new AccessibleProvider(this, id);
                _providers.Add(id, provider);
            }
            return provider;
        }
    }

    /// <summary>Queues one semantic action on the UI-owning thread.</summary>
    /// <param name="id">Target retained element identity.</param>
    /// <param name="action">Requested semantic action.</param>
    /// <param name="value">Optional numeric action value.</param>
    private void PostAction(long id, UISemanticAction action, double? value = null)
    {
        if (_disposed)
            return;
        _dispatcher.Post(() =>
        {
            if (_disposed || !GetSnapshot().TryGetNode(id, out var node) || node is null)
                return;
            if (node.Element.PerformSemanticAction(action, value))
                _refresh();
        });
    }

    /// <summary>Returns the root provider for native MSAA and UI Automation queries.</summary>
    /// <param name="windowHandle">Native window receiving the message.</param>
    /// <param name="message">Win32 message identifier.</param>
    /// <param name="wordParameter">Message word parameter.</param>
    /// <param name="longParameter">Requested object identifier.</param>
    /// <returns>The native accessibility result or the preceding procedure's result.</returns>
    private nint HandleWindowMessage(
        IntPtr windowHandle,
        uint message,
        nuint wordParameter,
        nint longParameter)
    {
        if (message == GetObjectMessage)
        {
            Volatile.Write(
                ref _snapshot,
                UIAccessibilityTree.Capture(_root, _coordinateMapper));
            var objectId = unchecked((int)longParameter);
            var root = GetProvider(GetSnapshot().Root.Id);
            if (objectId == ClientObjectId)
                return LresultFromObject(ref AccessibleInterfaceId, wordParameter, root);
            if (objectId == UiaRootObjectId)
                return ReturnUiaProvider(windowHandle, wordParameter, longParameter, root);
        }
        return CallWindowProc(
            _previousWindowProcedure, windowHandle, message, wordParameter, longParameter);
    }

    /// <summary>Wraps the MSAA root in the system UI Automation proxy.</summary>
    /// <param name="windowHandle">Native HWND.</param>
    /// <param name="wordParameter">Original WM_GETOBJECT word parameter.</param>
    /// <param name="longParameter">Original WM_GETOBJECT long parameter.</param>
    /// <param name="provider">Root MSAA provider.</param>
    /// <returns>The UI Automation provider result.</returns>
    private static nint ReturnUiaProvider(
        IntPtr windowHandle,
        nuint wordParameter,
        nint longParameter,
        AccessibleProvider provider)
    {
        if (!OperatingSystem.IsWindows())
            return 0;
        var accessible = Marshal.GetComInterfaceForObject<AccessibleProvider, IAccessibleNative>(provider);
        try
        {
            if (UiaProviderFromIAccessible(accessible, 0, 0, out var rawProvider) < 0 ||
                rawProvider == IntPtr.Zero)
                return 0;
            try
            {
                return UiaReturnRawElementProvider(
                    windowHandle, wordParameter, longParameter, rawProvider);
            }
            finally
            {
                Marshal.Release(rawProvider);
            }
        }
        finally
        {
            Marshal.Release(accessible);
        }
    }

    /// <summary>Restores the prior window procedure and releases native provider registrations.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        UiaReturnRawElementProvider(_windowHandle, 0, 0, IntPtr.Zero);
        SetWindowLongPtr(_windowHandle, WindowProcedureIndex, _previousWindowProcedure);
        lock (_providerLock)
            _providers.Clear();
    }

    /// <summary>Writes a pointer-sized native window attribute.</summary>
    /// <param name="windowHandle">Native HWND.</param>
    /// <param name="index">Window attribute index.</param>
    /// <param name="value">Replacement attribute value.</param>
    /// <returns>The previous attribute value.</returns>
    private static nint SetWindowLongPtr(IntPtr windowHandle, int index, nint value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : SetWindowLong32(windowHandle, index, value);

    /// <summary>Handles one native window message.</summary>
    /// <param name="windowHandle">Native window handle.</param>
    /// <param name="message">Message identifier.</param>
    /// <param name="wordParameter">Message word parameter.</param>
    /// <param name="longParameter">Message long parameter.</param>
    /// <returns>The native message result.</returns>
    private delegate nint WindowProcedure(
        IntPtr windowHandle,
        uint message,
        nuint wordParameter,
        nint longParameter);

    private static Guid AccessibleInterfaceId =
        new("618736E0-3C3D-11CF-810C-00AA00389B71");

    /// <summary>Replaces a 32-bit window attribute.</summary>
    /// <param name="windowHandle">Native window handle.</param>
    /// <param name="index">Window attribute index.</param>
    /// <param name="value">Replacement value.</param>
    /// <returns>The previous value.</returns>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern nint SetWindowLong32(IntPtr windowHandle, int index, nint value);

    /// <summary>Replaces a 64-bit window attribute.</summary>
    /// <param name="windowHandle">Native window handle.</param>
    /// <param name="index">Window attribute index.</param>
    /// <param name="value">Replacement value.</param>
    /// <returns>The previous value.</returns>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(IntPtr windowHandle, int index, nint value);

    /// <summary>Forwards a message to a native window procedure.</summary>
    /// <param name="previousWindowProcedure">Procedure to invoke.</param>
    /// <param name="windowHandle">Native window handle.</param>
    /// <param name="message">Message identifier.</param>
    /// <param name="wordParameter">Message word parameter.</param>
    /// <param name="longParameter">Message long parameter.</param>
    /// <returns>The procedure result.</returns>
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(
        nint previousWindowProcedure,
        IntPtr windowHandle,
        uint message,
        nuint wordParameter,
        nint longParameter);

    /// <summary>Packages a COM accessibility object into a window-message result.</summary>
    /// <param name="interfaceId">Requested COM interface.</param>
    /// <param name="wordParameter">Original message word parameter.</param>
    /// <param name="accessibleObject">Accessibility provider.</param>
    /// <returns>The packaged native result.</returns>
    [DllImport("oleacc.dll", EntryPoint = "LresultFromObject")]
    private static extern nint LresultFromObject(
        ref Guid interfaceId,
        nuint wordParameter,
        [MarshalAs(UnmanagedType.Interface)] object accessibleObject);

    /// <summary>Queries whether UI Automation clients are listening.</summary>
    /// <returns>True when at least one client is listening.</returns>
    [DllImport("uiautomationcore.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiaClientsAreListening();

    /// <summary>Creates a UI Automation proxy for an MSAA provider.</summary>
    /// <param name="accessible">Native IAccessible pointer.</param>
    /// <param name="childId">MSAA child identifier.</param>
    /// <param name="flags">Proxy flags.</param>
    /// <param name="provider">Created provider pointer.</param>
    /// <returns>An HRESULT.</returns>
    [DllImport("uiautomationcore.dll")]
    private static extern int UiaProviderFromIAccessible(
        IntPtr accessible,
        int childId,
        uint flags,
        out IntPtr provider);

    /// <summary>Returns a raw UI Automation provider for a window message.</summary>
    /// <param name="windowHandle">Native window handle.</param>
    /// <param name="wordParameter">Message word parameter.</param>
    /// <param name="longParameter">Message long parameter.</param>
    /// <param name="provider">Raw provider pointer.</param>
    /// <returns>The native message result.</returns>
    [DllImport("uiautomationcore.dll")]
    private static extern nint UiaReturnRawElementProvider(
        IntPtr windowHandle,
        nuint wordParameter,
        nint longParameter,
        IntPtr provider);

    /// <summary>Implements an MSAA node that the Windows UI Automation proxy can consume.</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class AccessibleProvider : IAccessibleNative
    {
        private const int SelfChildId = 0;
        private readonly WindowsAccessibilityAdapter _owner;
        private readonly long _id;

        /// <summary>Creates a provider for one stable retained identity.</summary>
        /// <param name="owner">Owning native adapter.</param>
        /// <param name="id">Stable retained identity.</param>
        internal AccessibleProvider(WindowsAccessibilityAdapter owner, long id)
        {
            _owner = owner;
            _id = id;
        }

        /// <inheritdoc/>
        public object? Parent => TryGetNode(out var node) && node!.ParentIndex >= 0
            ? _owner.GetProvider(_owner.GetSnapshot().GetNode(node.ParentIndex).Id)
            : null;

        /// <inheritdoc/>
        public int ChildCount
        {
            get
            {
                if (!TryGetNode(out var node))
                    return 0;
                var count = 0;
                for (var index = node!.FirstChildIndex; index >= 0;
                    index = _owner.GetSnapshot().GetNode(index).NextSiblingIndex)
                    count++;
                return count;
            }
        }

        /// <inheritdoc/>
        public object? GetChild(object childId)
        {
            if (!TryGetChildIndex(childId, out var index))
                return null;
            return _owner.GetProvider(_owner.GetSnapshot().GetNode(index).Id);
        }

        /// <inheritdoc/>
        public string? GetName(object childId) => GetNode(childId)?.SemanticInfo.Name;

        /// <inheritdoc/>
        public string? GetValue(object childId)
        {
            var info = GetNode(childId)?.SemanticInfo;
            if (info is null || info.Value.Role == UISemanticRole.PasswordField)
                return null;
            return info.Value.Value ?? info.Value.NumericValue?.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <inheritdoc/>
        public string? GetDescription(object childId) =>
            GetNode(childId)?.SemanticInfo.Description;

        /// <inheritdoc/>
        public object? GetRole(object childId) => GetNode(childId) is { } node
            ? ToNativeRole(node.SemanticInfo.Role)
            : null;

        /// <inheritdoc/>
        public object? GetState(object childId) => GetNode(childId) is { } node
            ? GetNativeState(node)
            : null;

        /// <inheritdoc/>
        public string? GetHelp(object childId) => GetDescription(childId);

        /// <inheritdoc/>
        public int GetHelpTopic(out string? helpFile, object childId)
        {
            helpFile = null;
            return -1;
        }

        /// <inheritdoc/>
        public string? GetKeyboardShortcut(object childId) => null;

        /// <inheritdoc/>
        public object? Focus => FindFocusedProvider();

        /// <inheritdoc/>
        public object? Selection => FindSelectedProvider();

        /// <inheritdoc/>
        public string? GetDefaultAction(object childId)
        {
            if (GetNode(childId) is not { } node)
                return null;
            var actions = node.SemanticInfo.Actions;
            if ((actions & UISemanticAction.Invoke) != 0) return "Press";
            if ((actions & UISemanticAction.Toggle) != 0) return "Toggle";
            if ((actions & UISemanticAction.Select) != 0) return "Select";
            if ((actions & UISemanticAction.ExpandCollapse) != 0)
                return node.SemanticInfo.IsExpanded == true ? "Collapse" : "Expand";
            return null;
        }

        /// <inheritdoc/>
        public void Select(int flagsSelect, object childId)
        {
            var node = GetNode(childId);
            if (node is not null &&
                (node.SemanticInfo.Actions & UISemanticAction.Select) != 0)
                _owner.PostAction(node.Id, UISemanticAction.Select);
        }

        /// <inheritdoc/>
        public void GetLocation(
            out int left,
            out int top,
            out int width,
            out int height,
            object childId)
        {
            var bounds = GetNode(childId)?.ScreenBounds ?? default;
            left = (int)MathF.Round(bounds.Left);
            top = (int)MathF.Round(bounds.Top);
            width = Math.Max(0, (int)MathF.Round(bounds.Right - bounds.Left));
            height = Math.Max(0, (int)MathF.Round(bounds.Bottom - bounds.Top));
        }

        /// <inheritdoc/>
        public object? Navigate(int direction, object start)
        {
            if (!TryGetNode(out var node))
                return null;
            var snapshot = _owner.GetSnapshot();
            var current = node!;
            var index = direction switch
            {
                1 => current.ParentIndex,
                5 => current.NextSiblingIndex,
                6 => FindPreviousSibling(snapshot, current),
                7 => current.FirstChildIndex,
                8 => FindLastChild(snapshot, current),
                _ => -1
            };
            return index >= 0 ? _owner.GetProvider(snapshot.GetNode(index).Id) : null;
        }

        /// <inheritdoc/>
        public object? HitTest(int x, int y)
        {
            var snapshot = _owner.GetSnapshot();
            for (var index = snapshot.Nodes.Count - 1; index >= 0; index--)
            {
                var candidate = snapshot.GetNode(index);
                if (candidate.ScreenBounds.Contains(x, y))
                    return _owner.GetProvider(candidate.Id);
            }
            return null;
        }

        /// <inheritdoc/>
        public void DoDefaultAction(object childId)
        {
            if (GetNode(childId) is not { } node)
                return;
            var actions = node.SemanticInfo.Actions;
            var action = (actions & UISemanticAction.Invoke) != 0
                ? UISemanticAction.Invoke
                : (actions & UISemanticAction.Toggle) != 0
                    ? UISemanticAction.Toggle
                    : (actions & UISemanticAction.Select) != 0
                        ? UISemanticAction.Select
                        : (actions & UISemanticAction.ExpandCollapse) != 0
                            ? UISemanticAction.ExpandCollapse
                            : UISemanticAction.None;
            if (action != UISemanticAction.None)
                _owner.PostAction(node.Id, action);
        }

        /// <inheritdoc/>
        public void SetName(object childId, string name)
        {
        }

        /// <inheritdoc/>
        public void SetValue(object childId, string value)
        {
            if (GetNode(childId) is { } node &&
                double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var numeric) &&
                (node.SemanticInfo.Actions & UISemanticAction.SetValue) != 0)
                _owner.PostAction(node.Id, UISemanticAction.SetValue, numeric);
        }

        /// <summary>Gets the provider's current node.</summary>
        /// <param name="node">Current node when it remains available.</param>
        /// <returns>True when this element remains in the snapshot.</returns>
        private bool TryGetNode(out UIAccessibilityNode? node) =>
            _owner.GetSnapshot().TryGetNode(_id, out node);

        /// <summary>Resolves self or a direct MSAA child identifier.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <returns>The resolved node, or null.</returns>
        private UIAccessibilityNode? GetNode(object childId)
        {
            if (Convert.ToInt32(childId, System.Globalization.CultureInfo.InvariantCulture) ==
                SelfChildId)
                return TryGetNode(out var self) ? self : null;
            return TryGetChildIndex(childId, out var index)
                ? _owner.GetSnapshot().GetNode(index)
                : null;
        }

        /// <summary>Maps a one-based direct-child identifier to a snapshot index.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <param name="index">Resolved snapshot index.</param>
        /// <returns>True when the child exists.</returns>
        private bool TryGetChildIndex(object childId, out int index)
        {
            index = -1;
            if (!TryGetNode(out var node))
                return false;
            var ordinal = Convert.ToInt32(
                childId, System.Globalization.CultureInfo.InvariantCulture);
            if (ordinal <= 0)
                return false;
            index = node!.FirstChildIndex;
            while (--ordinal > 0 && index >= 0)
                index = _owner.GetSnapshot().GetNode(index).NextSiblingIndex;
            return index >= 0;
        }

        /// <summary>Finds the first focused node in the current snapshot.</summary>
        /// <returns>The focused provider, or null.</returns>
        private object? FindFocusedProvider()
        {
            var snapshot = _owner.GetSnapshot();
            for (var index = 0; index < snapshot.Nodes.Count; index++)
            {
                var node = snapshot.GetNode(index);
                if (node.IsFocused)
                    return _owner.GetProvider(node.Id);
            }
            return null;
        }

        /// <summary>Finds the first selected node in the current snapshot.</summary>
        /// <returns>The selected provider, or null.</returns>
        private object? FindSelectedProvider()
        {
            var snapshot = _owner.GetSnapshot();
            for (var index = 0; index < snapshot.Nodes.Count; index++)
            {
                var node = snapshot.GetNode(index);
                if (node.SemanticInfo.IsSelected)
                    return _owner.GetProvider(node.Id);
            }
            return null;
        }

        /// <summary>Finds a node's preceding sibling.</summary>
        /// <param name="snapshot">Current snapshot.</param>
        /// <param name="node">Current node.</param>
        /// <returns>Preceding sibling index, or -1.</returns>
        private static int FindPreviousSibling(
            UIAccessibilitySnapshot snapshot,
            UIAccessibilityNode node)
        {
            if (node.ParentIndex < 0)
                return -1;
            var candidate = snapshot.GetNode(node.ParentIndex).FirstChildIndex;
            var previous = -1;
            while (candidate >= 0 && snapshot.GetNode(candidate).Id != node.Id)
            {
                previous = candidate;
                candidate = snapshot.GetNode(candidate).NextSiblingIndex;
            }
            return previous;
        }

        /// <summary>Finds a node's final direct child.</summary>
        /// <param name="snapshot">Current snapshot.</param>
        /// <param name="node">Current node.</param>
        /// <returns>Final child index, or -1.</returns>
        private static int FindLastChild(
            UIAccessibilitySnapshot snapshot,
            UIAccessibilityNode node)
        {
            var child = node.FirstChildIndex;
            if (child < 0)
                return -1;
            while (snapshot.GetNode(child).NextSiblingIndex >= 0)
                child = snapshot.GetNode(child).NextSiblingIndex;
            return child;
        }

        /// <summary>Maps renderer-independent roles to MSAA role constants.</summary>
        /// <param name="role">Renderer-independent semantic role.</param>
        /// <returns>Native MSAA role identifier.</returns>
        private static int ToNativeRole(UISemanticRole role) => role switch
        {
            UISemanticRole.Button or UISemanticRole.ToggleButton => 43,
            UISemanticRole.CheckBox or UISemanticRole.Switch => 44,
            UISemanticRole.RadioButton => 45,
            UISemanticRole.Slider => 51,
            UISemanticRole.ProgressBar => 48,
            UISemanticRole.ComboBox => 46,
            UISemanticRole.List => 33,
            UISemanticRole.ListItem => 34,
            UISemanticRole.Tree => 35,
            UISemanticRole.TreeItem => 36,
            UISemanticRole.TabList => 60,
            UISemanticRole.Menu => 11,
            UISemanticRole.MenuItem => 12,
            UISemanticRole.Image => 40,
            UISemanticRole.Dialog => 18,
            UISemanticRole.ToolBar => 22,
            UISemanticRole.Separator => 21,
            UISemanticRole.Text or UISemanticRole.TextField or UISemanticRole.PasswordField => 42,
            _ => 10
        };

        /// <summary>Maps renderer-independent state to MSAA state flags.</summary>
        /// <param name="node">Captured semantic node.</param>
        /// <returns>Combined native state flags.</returns>
        private static int GetNativeState(UIAccessibilityNode node)
        {
            var info = node.SemanticInfo;
            var state = info.IsEnabled ? 0 : 0x00000001;
            if (info.IsSelected) state |= 0x00000002;
            if (node.IsFocused) state |= 0x00000004;
            if (info.IsChecked == true) state |= 0x00000010;
            if (info.IsReadOnly) state |= 0x00000040;
            if (info.IsExpanded == true) state |= 0x00000200;
            if (info.IsExpanded == false) state |= 0x00000400;
            if (info.Role == UISemanticRole.PasswordField) state |= 0x20000000;
            return state;
        }
    }

    /// <summary>Defines the native Microsoft Active Accessibility provider contract.</summary>
    [ComVisible(true)]
    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IAccessibleNative
    {
        [DispId(-5000)] object? Parent { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
        [DispId(-5001)] int ChildCount { get; }
        /// <summary>Gets one accessible child.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <returns>The accessible child.</returns>
        [DispId(-5002)] [return: MarshalAs(UnmanagedType.IDispatch)] object? GetChild(
            [MarshalAs(UnmanagedType.Struct)] object childId);
        /// <summary>Gets an accessible name.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <returns>The accessible name.</returns>
        [DispId(-5003)] [return: MarshalAs(UnmanagedType.BStr)] string? GetName(
            [MarshalAs(UnmanagedType.Struct)] object childId);
        /// <summary>Gets an accessible value.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <returns>The accessible value.</returns>
        [DispId(-5004)] [return: MarshalAs(UnmanagedType.BStr)] string? GetValue(
            [MarshalAs(UnmanagedType.Struct)] object childId);
        /// <summary>Gets an accessible description.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <returns>The accessible description.</returns>
        [DispId(-5005)] [return: MarshalAs(UnmanagedType.BStr)] string? GetDescription(
            [MarshalAs(UnmanagedType.Struct)] object childId);
        /// <summary>Gets an accessible role.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <returns>The native role.</returns>
        [DispId(-5006)] [return: MarshalAs(UnmanagedType.Struct)] object? GetRole(
            [MarshalAs(UnmanagedType.Struct)] object childId);
        /// <summary>Gets accessible state flags.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <returns>The native state flags.</returns>
        [DispId(-5007)] [return: MarshalAs(UnmanagedType.Struct)] object? GetState(
            [MarshalAs(UnmanagedType.Struct)] object childId);
        /// <summary>Gets accessible help text.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <returns>The help text.</returns>
        [DispId(-5008)] [return: MarshalAs(UnmanagedType.BStr)] string? GetHelp(
            [MarshalAs(UnmanagedType.Struct)] object childId);
        /// <summary>Gets an accessible help topic.</summary>
        /// <param name="helpFile">Associated help file.</param>
        /// <param name="childId">MSAA child identifier.</param>
        /// <returns>The help topic identifier.</returns>
        [DispId(-5009)] int GetHelpTopic(
            [MarshalAs(UnmanagedType.BStr)] out string? helpFile,
            [MarshalAs(UnmanagedType.Struct)] object childId);
        /// <summary>Gets an accessible keyboard shortcut.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <returns>The shortcut text.</returns>
        [DispId(-5010)] [return: MarshalAs(UnmanagedType.BStr)] string? GetKeyboardShortcut(
            [MarshalAs(UnmanagedType.Struct)] object childId);
        [DispId(-5011)] object? Focus { [return: MarshalAs(UnmanagedType.Struct)] get; }
        [DispId(-5012)] object? Selection { [return: MarshalAs(UnmanagedType.Struct)] get; }
        /// <summary>Gets the default action name.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <returns>The action name.</returns>
        [DispId(-5013)] [return: MarshalAs(UnmanagedType.BStr)] string? GetDefaultAction(
            [MarshalAs(UnmanagedType.Struct)] object childId);
        /// <summary>Changes accessible selection or focus.</summary>
        /// <param name="flagsSelect">Native selection flags.</param>
        /// <param name="childId">MSAA child identifier.</param>
        [DispId(-5014)] void Select(int flagsSelect,
            [MarshalAs(UnmanagedType.Struct)] object childId);
        /// <summary>Gets accessible screen bounds.</summary>
        /// <param name="left">Left screen coordinate.</param>
        /// <param name="top">Top screen coordinate.</param>
        /// <param name="width">Screen width.</param>
        /// <param name="height">Screen height.</param>
        /// <param name="childId">MSAA child identifier.</param>
        [DispId(-5015)] void GetLocation(out int left, out int top, out int width, out int height,
            [MarshalAs(UnmanagedType.Struct)] object childId);
        /// <summary>Navigates the accessible hierarchy.</summary>
        /// <param name="direction">Native navigation direction.</param>
        /// <param name="start">Starting child identifier.</param>
        /// <returns>The navigation result.</returns>
        [DispId(-5016)] [return: MarshalAs(UnmanagedType.Struct)] object? Navigate(
            int direction, [MarshalAs(UnmanagedType.Struct)] object start);
        /// <summary>Hit-tests an accessible screen point.</summary>
        /// <param name="x">Screen X coordinate.</param>
        /// <param name="y">Screen Y coordinate.</param>
        /// <returns>The hit accessible object or child identifier.</returns>
        [DispId(-5017)] [return: MarshalAs(UnmanagedType.Struct)] object? HitTest(int x, int y);
        /// <summary>Invokes the default accessible action.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        [DispId(-5018)] void DoDefaultAction(
            [MarshalAs(UnmanagedType.Struct)] object childId);
        /// <summary>Sets an accessible name.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <param name="name">Replacement name.</param>
        [DispId(-5003)] void SetName(
            [MarshalAs(UnmanagedType.Struct)] object childId,
            [MarshalAs(UnmanagedType.BStr)] string name);
        /// <summary>Sets an accessible value.</summary>
        /// <param name="childId">MSAA child identifier.</param>
        /// <param name="value">Replacement value.</param>
        [DispId(-5004)] void SetValue(
            [MarshalAs(UnmanagedType.Struct)] object childId,
            [MarshalAs(UnmanagedType.BStr)] string value);
    }
}
