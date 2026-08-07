using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Registers stable editor panels and creates each retained instance lazily.</summary>
public sealed class DockPanelRegistry
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Registers one stable dock panel.</summary>
    /// <param name="id">Stable persistence identifier.</param>
    /// <param name="title">Displayed title.</param>
    /// <param name="createContent">Creates the retained panel once.</param>
    /// <param name="canFloat">Whether drag-outside may create a native floating root.</param>
    public void Register(
        string id,
        string title,
        Func<UIElement> createContent,
        bool canFloat = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(createContent);
        if (!_entries.TryAdd(id, new Entry(title, createContent, canFloat)))
            throw new InvalidOperationException($"Dock panel '{id}' is already registered.");
    }

    /// <summary>Gets whether a registered panel may become a floating root.</summary>
    /// <param name="id">Stable panel identifier.</param>
    /// <returns>Configured policy, or false for an unknown panel.</returns>
    public bool CanFloat(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _entries.TryGetValue(id, out var entry) && entry.CanFloat;
    }

    /// <summary>Gets the registered title for one panel.</summary>
    /// <param name="id">Stable identifier.</param>
    /// <returns>Registered title, or null.</returns>
    public string? GetTitle(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _entries.TryGetValue(id, out var entry) ? entry.Title : null;
    }

    /// <summary>Resolves the stable retained content instance for one panel.</summary>
    /// <param name="id">Stable identifier.</param>
    /// <returns>Registered content, or null.</returns>
    public UIElement? Resolve(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_entries.TryGetValue(id, out var entry))
            return null;
        return entry.Content ??= entry.CreateContent();
    }

    /// <summary>Stores one lazy retained panel registration.</summary>
    /// <param name="Title">Displayed title.</param>
    /// <param name="CreateContent">Content factory.</param>
    /// <param name="CanFloat">Floating-window policy.</param>
    private sealed record Entry(string Title, Func<UIElement> CreateContent, bool CanFloat)
    {
        /// <summary>Gets or sets the single retained instance.</summary>
        internal UIElement? Content { get; set; }
    }
}

/// <summary>Represents one native or virtual floating dock host.</summary>
public interface IDockFloatingWindow : IDisposable
{
    /// <summary>Gets whether the host remains open.</summary>
    bool IsOpen { get; }
}

/// <summary>Optionally synchronizes native floating-window geometry into its persisted model.</summary>
public interface IDockFloatingGeometry
{
    /// <summary>Copies current native position and size into the associated floating model.</summary>
    void SynchronizeGeometry();
}

/// <summary>Exposes native coordinate mapping for a floating dock presentation.</summary>
public interface IDockFloatingWindowCoordinates
{
    /// <summary>Gets the native window's logical-client to physical-screen mapper.</summary>
    IWindowCoordinateMapper CoordinateMapper { get; }
}

/// <summary>Exposes a floating host's routed drag state and preview refresh boundary.</summary>
public interface IDockFloatingDragHost
{
    /// <summary>Gets the floating window's independent input router.</summary>
    UIEventRouter InputRouter { get; }

    /// <summary>Submits invalidated dock-preview visuals and requests presentation.</summary>
    void RefreshDockPreview();
}

/// <summary>Describes a tab-well target resolved through a native screen coordinate.</summary>
/// <param name="Host">Presentation host containing the target.</param>
/// <param name="Group">Authoritative destination group.</param>
/// <param name="LocalPosition">Pointer position in host coordinates.</param>
/// <param name="Bounds">Target bounds in host coordinates.</param>
public readonly record struct DockScreenDropTarget(
    DockHost Host,
    DockTabGroup Group,
    Vector2 LocalPosition,
    UIClipRect Bounds);

/// <summary>Creates independently presented floating dock hosts.</summary>
public interface IDockFloatingWindowFactory
{
    /// <summary>Creates a host for one persisted floating root.</summary>
    /// <param name="model">Floating model and geometry.</param>
    /// <param name="content">Dock host to present.</param>
    /// <returns>Owned floating host.</returns>
    IDockFloatingWindow Create(FloatingDockRoot model, DockHost content);
}

/// <summary>Coordinates a main dock host with zero or more floating presentation hosts.</summary>
public sealed class DockSession : IDisposable
{
    private readonly DockPanelRegistry _registry;
    private readonly IDockFloatingWindowFactory _floatingFactory;
    private readonly Dictionary<string, FloatingPresentation> _floatingWindows =
        new(StringComparer.Ordinal);
    private readonly UITheme _theme;
    private readonly IWindowCoordinateMapper? _mainCoordinates;
    private readonly List<DockDragRegistration> _dragRegistrations = [];
    private DockHost? _externalPreviewHost;
    private bool _disposed;

    /// <summary>Gets the mutable persisted workspace.</summary>
    public DockWorkspace Workspace { get; }

    /// <summary>Gets the main-window dock host.</summary>
    public DockHost MainHost { get; }

    /// <summary>Creates a dock presentation session.</summary>
    /// <param name="workspace">Workspace model.</param>
    /// <param name="registry">Stable panel registry.</param>
    /// <param name="floatingFactory">Floating presentation factory.</param>
    /// <param name="theme">UI theme.</param>
    /// <param name="initializeFloatingWindows">Whether persisted floating roots open immediately.</param>
    /// <param name="mainCoordinates">Optional coordinate mapper for cross-window drops.</param>
    public DockSession(
        DockWorkspace workspace,
        DockPanelRegistry registry,
        IDockFloatingWindowFactory floatingFactory,
        UITheme? theme = null,
        bool initializeFloatingWindows = true,
        IWindowCoordinateMapper? mainCoordinates = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(floatingFactory);
        Workspace = workspace;
        _registry = registry;
        _floatingFactory = floatingFactory;
        _theme = theme ?? UITheme.Dark;
        _mainCoordinates = mainCoordinates;
        MainHost = new DockHost(workspace, registry.Resolve, _theme, canFloat: registry.CanFloat);
        MainHost.WorkspaceChanged += Refresh;
        MainHost.TabFloatRequested += FloatRequestedTab;
        if (initializeFloatingWindows)
            SynchronizeFloatingWindows();
    }

    /// <summary>Attaches one presentation router to cross-window dock preview coordination.</summary>
    /// <param name="host">Dock host routed by the input router.</param>
    /// <param name="router">Independent per-window input router.</param>
    /// <param name="coordinates">Native coordinate mapper for the router's window.</param>
    /// <param name="refresh">Submits preview visual changes to that window.</param>
    public void AttachDragRouter(
        DockHost host,
        UIEventRouter router,
        IWindowCoordinateMapper coordinates,
        Action refresh)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(coordinates);
        ArgumentNullException.ThrowIfNull(refresh);
        for (var index = 0; index < _dragRegistrations.Count; index++)
        {
            if (ReferenceEquals(_dragRegistrations[index].Router, router))
                return;
        }
        Action handler = () => UpdateExternalDrag(host, router, coordinates);
        _dragRegistrations.Add(new DockDragRegistration(host, router, refresh, handler));
        router.DragStateChanged += handler;
    }

    /// <summary>Floats one tab and creates its independent host.</summary>
    /// <param name="tabId">Panel identifier.</param>
    /// <param name="left">Logical left coordinate.</param>
    /// <param name="top">Logical top coordinate.</param>
    /// <param name="width">Logical width.</param>
    /// <param name="height">Logical height.</param>
    /// <returns>True when the panel was found.</returns>
    public bool FloatTab(
        string tabId,
        float left,
        float top,
        float width,
        float height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var floating = Workspace.FloatTab(tabId, left, top, width, height);
        if (floating is null)
            return false;
        Refresh();
        return true;
    }

    /// <summary>Opens or activates one registered panel beside an optional stable anchor.</summary>
    /// <param name="panelId">Stable registered panel identifier.</param>
    /// <param name="anchorId">Preferred sibling panel identifier.</param>
    /// <returns>True when the panel was registered and opened.</returns>
    public bool OpenPanel(string panelId, string? anchorId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(panelId);
        var title = _registry.GetTitle(panelId);
        if (title is null || !Workspace.OpenTab(panelId, title, anchorId))
            return false;
        Refresh();
        return true;
    }

    /// <summary>Transfers a tab into a target group presented by any session host.</summary>
    /// <param name="tabId">Stable panel identifier.</param>
    /// <param name="target">Destination group in the main or a floating root.</param>
    /// <param name="zone">Center or edge insertion zone.</param>
    /// <param name="targetIndex">Center-drop insertion index, or -1 to append.</param>
    /// <returns>True when the authoritative workspace applied the transfer.</returns>
    public bool DockTab(
        string tabId,
        DockTabGroup target,
        DockDropZone zone,
        int targetIndex = -1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        ArgumentNullException.ThrowIfNull(target);
        if (!Workspace.DockTab(tabId, target, zone, targetIndex: targetIndex))
            return false;
        Refresh();
        return true;
    }

    /// <summary>Resolves the tab well under one physical screen position across every session window.</summary>
    /// <param name="screenPosition">Pointer position in shared physical screen pixels.</param>
    /// <param name="target">Resolved host, group, local position, and bounds.</param>
    /// <returns>True when a presented tab well contains the pointer.</returns>
    public bool TryGetDropTarget(Vector2 screenPosition, out DockScreenDropTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mainCoordinates is not null &&
            TryGetDropTarget(MainHost, _mainCoordinates, screenPosition, out target))
            return true;
        foreach (var presentation in _floatingWindows.Values)
        {
            if (!presentation.Window.IsOpen ||
                presentation.Window is not IDockFloatingWindowCoordinates coordinates)
                continue;
            if (TryGetDropTarget(
                    presentation.Host, coordinates.CoordinateMapper, screenPosition, out target))
                return true;
        }
        target = default;
        return false;
    }

    /// <summary>Gets the native coordinate mapper associated with one presentation host.</summary>
    /// <param name="host">Main or floating presentation host.</param>
    /// <param name="coordinates">Resolved coordinate mapper.</param>
    /// <returns>True when the presentation exposes native coordinates.</returns>
    private bool TryGetCoordinates(DockHost host, out IWindowCoordinateMapper? coordinates)
    {
        if (ReferenceEquals(host, MainHost))
        {
            coordinates = _mainCoordinates;
            return coordinates is not null;
        }
        foreach (var presentation in _floatingWindows.Values)
        {
            if (!ReferenceEquals(presentation.Host, host) ||
                presentation.Window is not IDockFloatingWindowCoordinates floatingCoordinates)
                continue;
            coordinates = floatingCoordinates.CoordinateMapper;
            return true;
        }
        coordinates = null;
        return false;
    }

    /// <summary>Updates another window's dock target overlay from active routed drag state.</summary>
    /// <param name="sourceHost">Host owning the active drag.</param>
    /// <param name="router">Source input router.</param>
    /// <param name="coordinates">Source native coordinate mapper.</param>
    private void UpdateExternalDrag(
        DockHost sourceHost,
        UIEventRouter router,
        IWindowCoordinateMapper coordinates)
    {
        if (!router.IsDragging || router.ActiveDragData is null ||
            !router.ActiveDragData.TryGet<DockTabDragData>(out var tab) || tab is null)
        {
            ClearExternalPreview();
            return;
        }
        var screenPosition = coordinates.ClientToScreen(router.PointerPosition);
        if (!TryGetDropTarget(screenPosition, out var target) ||
            ReferenceEquals(target.Host, sourceHost))
        {
            ClearExternalPreview();
            return;
        }
        if (_externalPreviewHost is not null &&
            !ReferenceEquals(_externalPreviewHost, target.Host))
        {
            _externalPreviewHost.ClearExternalDockPreview();
            RefreshDragHost(_externalPreviewHost);
        }
        _externalPreviewHost = target.Host;
        target.Host.UpdateExternalDockPreview(target.LocalPosition);
        RefreshDragHost(target.Host);
    }

    /// <summary>Clears the active cross-window preview and submits the visual change.</summary>
    private void ClearExternalPreview()
    {
        if (_externalPreviewHost is not { } host)
            return;
        _externalPreviewHost = null;
        if (host.ClearExternalDockPreview())
            RefreshDragHost(host);
    }

    /// <summary>Submits dock preview changes for one attached presentation host.</summary>
    /// <param name="host">Host whose preview changed.</param>
    private void RefreshDragHost(DockHost host)
    {
        for (var index = 0; index < _dragRegistrations.Count; index++)
        {
            var registration = _dragRegistrations[index];
            if (ReferenceEquals(registration.Host, host))
            {
                registration.Refresh();
                return;
            }
        }
    }

    /// <summary>Disconnects cross-window drag coordination for one presentation host.</summary>
    /// <param name="host">Presentation host being removed.</param>
    private void DetachDragRouter(DockHost host)
    {
        if (ReferenceEquals(_externalPreviewHost, host))
            _externalPreviewHost = null;
        for (var index = _dragRegistrations.Count - 1; index >= 0; index--)
        {
            var registration = _dragRegistrations[index];
            if (!ReferenceEquals(registration.Host, host))
                continue;
            registration.Router.DragStateChanged -= registration.Handler;
            _dragRegistrations.RemoveAt(index);
        }
    }

    /// <summary>Docks one tab at a tab well resolved from physical screen coordinates.</summary>
    /// <param name="tabId">Stable panel identifier.</param>
    /// <param name="screenPosition">Pointer position in shared physical screen pixels.</param>
    /// <param name="zone">Center or edge insertion zone.</param>
    /// <param name="targetIndex">Center-drop insertion index, or -1 to append.</param>
    /// <returns>True when a destination was found and the workspace accepted the transfer.</returns>
    public bool DockTabAtScreenPosition(
        string tabId,
        Vector2 screenPosition,
        DockDropZone zone,
        int targetIndex = -1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        return TryGetDropTarget(screenPosition, out var target) &&
            DockTab(tabId, target.Group, zone, targetIndex);
    }

    /// <summary>Redocks one floating root into a main-tree tab group.</summary>
    /// <param name="floatingId">Stable floating identifier.</param>
    /// <param name="target">Destination main-tree tab group.</param>
    public void Redock(string floatingId, DockTabGroup target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(floatingId);
        ArgumentNullException.ThrowIfNull(target);
        var index = FindFloating(floatingId);
        if (index < 0)
            throw new ArgumentException("Floating root was not found.", nameof(floatingId));
        if (_floatingWindows.Remove(floatingId, out var presentation))
        {
            DetachDragRouter(presentation.Host);
            presentation.Window.Dispose();
        }
        Workspace.RedockFloating(index, target);
        MainHost.Refresh();
        SynchronizeFloatingWindows();
    }

    /// <summary>Reconciles model roots with presentation hosts and removes closed windows.</summary>
    public void SynchronizeFloatingWindows()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var index = Workspace.FloatingRoots.Count - 1; index >= 0; index--)
        {
            var floating = Workspace.FloatingRoots[index];
            if (_floatingWindows.TryGetValue(floating.Id, out var existing))
            {
                if (existing.Window is IDockFloatingGeometry geometry)
                    geometry.SynchronizeGeometry();
                if (existing.Window.IsOpen)
                    continue;
                existing.Host.WorkspaceChanged -= Refresh;
                existing.Host.TabFloatRequested -= FloatRequestedTab;
                DetachDragRouter(existing.Host);
                existing.Window.Dispose();
                _floatingWindows.Remove(floating.Id);
                Workspace.FloatingRoots.RemoveAt(index);
                continue;
            }
            var host = new DockHost(
                Workspace, _registry.Resolve, _theme, () => floating.Root, _registry.CanFloat);
            host.WorkspaceChanged += Refresh;
            host.TabFloatRequested += FloatRequestedTab;
            var window = _floatingFactory.Create(floating, host);
            _floatingWindows.Add(floating.Id, new FloatingPresentation(host, window));
            if (window is IDockFloatingWindowCoordinates coordinates &&
                window is IDockFloatingDragHost dragHost)
            {
                AttachDragRouter(host, dragHost.InputRouter, coordinates.CoordinateMapper,
                    dragHost.RefreshDockPreview);
            }
        }
        RemoveOrphanWindows();
    }

    /// <summary>Disposes every floating presentation host.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (var index = _dragRegistrations.Count - 1; index >= 0; index--)
        {
            var registration = _dragRegistrations[index];
            registration.Router.DragStateChanged -= registration.Handler;
        }
        _dragRegistrations.Clear();
        MainHost.WorkspaceChanged -= Refresh;
        MainHost.TabFloatRequested -= FloatRequestedTab;
        foreach (var presentation in _floatingWindows.Values)
        {
            presentation.Host.WorkspaceChanged -= Refresh;
            presentation.Host.TabFloatRequested -= FloatRequestedTab;
            DetachDragRouter(presentation.Host);
            presentation.Window.Dispose();
        }
        _floatingWindows.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>Finds a floating model by stable identifier.</summary>
    /// <param name="id">Floating identifier.</param>
    /// <returns>Model index, or -1.</returns>
    private int FindFloating(string id)
    {
        for (var index = 0; index < Workspace.FloatingRoots.Count; index++)
        {
            if (string.Equals(Workspace.FloatingRoots[index].Id, id, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    /// <summary>Maps one screen position into a host and resolves its deepest tab well.</summary>
    /// <param name="host">Candidate presentation host.</param>
    /// <param name="coordinates">Candidate native coordinate mapper.</param>
    /// <param name="screenPosition">Position in physical screen pixels.</param>
    /// <param name="target">Resolved target data.</param>
    /// <returns>True when the mapped position lies over a tab well.</returns>
    private static bool TryGetDropTarget(
        DockHost host,
        IWindowCoordinateMapper coordinates,
        Vector2 screenPosition,
        out DockScreenDropTarget target)
    {
        var localPosition = coordinates.ScreenToClient(screenPosition);
        if (host.TryGetDropTarget(localPosition, out var group, out var bounds) && group is not null)
        {
            target = new DockScreenDropTarget(host, group, localPosition, bounds);
            return true;
        }
        target = default;
        return false;
    }

    /// <summary>Disposes presentation hosts whose models were removed externally.</summary>
    private void RemoveOrphanWindows()
    {
        if (_floatingWindows.Count == 0)
            return;
        var orphanIds = new List<string>();
        foreach (var pair in _floatingWindows)
        {
            if (FindFloating(pair.Key) < 0)
                orphanIds.Add(pair.Key);
        }
        for (var index = 0; index < orphanIds.Count; index++)
        {
            var id = orphanIds[index];
            var presentation = _floatingWindows[id];
            presentation.Host.WorkspaceChanged -= Refresh;
            presentation.Host.TabFloatRequested -= FloatRequestedTab;
            DetachDragRouter(presentation.Host);
            presentation.Window.Dispose();
            _floatingWindows.Remove(id);
        }
    }

    /// <summary>Refreshes every surviving host and reconciles created or removed floating roots.</summary>
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MainHost.Refresh();
        foreach (var presentation in _floatingWindows.Values)
        {
            if (presentation.Window.IsOpen)
                presentation.Host.Refresh();
        }
        SynchronizeFloatingWindows();
    }

    /// <summary>Floats a tab released beyond one of this session's presentation hosts.</summary>
    /// <param name="sourceHost">Host from which the tab was released.</param>
    /// <param name="tabId">Stable panel identifier.</param>
    /// <param name="position">Release position in host coordinates.</param>
    /// <param name="canFloat">Whether an unmatched release may create a floating root.</param>
    private void FloatRequestedTab(
        DockHost sourceHost,
        string tabId,
        Vector2 position,
        bool canFloat)
    {
        if (TryGetCoordinates(sourceHost, out var sourceCoordinates) &&
            sourceCoordinates is not null)
        {
            var screenPosition = sourceCoordinates.ClientToScreen(position);
            if (TryGetDropTarget(screenPosition, out var target) &&
                !ReferenceEquals(target.Host, sourceHost))
            {
                var placement = target.Host.UpdateExternalDockPreview(target.LocalPosition);
                var zone = placement?.Zone ?? DockDropZone.Center;
                var targetIndex = placement?.TargetIndex ?? -1;
                ClearExternalPreview();
                if (DockTab(tabId, target.Group, zone, targetIndex))
                    return;
            }
        }
        if (canFloat)
            FloatTab(tabId, position.X - 24f, position.Y - 16f, 640f, 480f);
    }

    /// <summary>Pairs one floating model presentation with its retained host.</summary>
    /// <param name="Host">Retained floating dock host.</param>
    /// <param name="Window">Owned presentation window.</param>
    private sealed record FloatingPresentation(DockHost Host, IDockFloatingWindow Window);

    /// <summary>Stores one routed drag subscription and its presentation callback.</summary>
    /// <param name="Host">Dock host routed by the input router.</param>
    /// <param name="Router">Independent per-window input router.</param>
    /// <param name="Refresh">Submits preview changes.</param>
    /// <param name="Handler">Stable event handler used for deterministic detachment.</param>
    private sealed record DockDragRegistration(
        DockHost Host,
        UIEventRouter Router,
        Action Refresh,
        Action Handler);
}
