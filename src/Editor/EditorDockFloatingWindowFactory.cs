using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Creates native Editor dock windows and coordinates panel-specific resource transfers.</summary>
public sealed class EditorDockFloatingWindowFactory : IDockFloatingWindowFactory
{
    private readonly SilkWindowGroup _windowGroup;
    private readonly Dictionary<string, PanelLifecycle> _lifecycles = new(StringComparer.Ordinal);

    /// <summary>Creates a floating-window factory sharing the Editor Vulkan device.</summary>
    /// <param name="windowGroup">Shared-device native window group.</param>
    public EditorDockFloatingWindowFactory(SilkWindowGroup windowGroup)
    {
        ArgumentNullException.ThrowIfNull(windowGroup);
        _windowGroup = windowGroup;
    }

    /// <summary>Registers resource-transfer callbacks for one stable dock panel.</summary>
    /// <param name="panelId">Stable dock panel identifier.</param>
    /// <param name="opened">Runs after the native floating window and UI host exist.</param>
    /// <param name="closing">Runs before the floating UI host and native window are disposed.</param>
    public void RegisterLifecycle(
        string panelId,
        Action<DetachedToolWindow>? opened = null,
        Action<DetachedToolWindow>? closing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(panelId);
        if (!_lifecycles.TryAdd(panelId, new PanelLifecycle(opened, closing)))
            throw new InvalidOperationException(
                $"Floating lifecycle for dock panel '{panelId}' is already registered.");
    }

    /// <inheritdoc/>
    public IDockFloatingWindow Create(FloatingDockRoot model, DockHost content)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(content);
        var lifecycles = new List<PanelLifecycle>();
        CollectLifecycles(model.Root, lifecycles);
        var window = new DetachedToolWindow(
            _windowGroup,
            GetTitle(model.Root),
            (int)MathF.Ceiling(model.Width),
            (int)MathF.Ceiling(model.Height),
            content);
        window.Window.SetClientPosition(new System.Numerics.Vector2(model.Left, model.Top));
        for (var index = 0; index < lifecycles.Count; index++)
            lifecycles[index].Opened?.Invoke(window);
        return new FloatingWindow(model, window, lifecycles);
    }

    /// <summary>Collects registered lifecycle hooks for every panel in a subtree.</summary>
    /// <param name="node">Dock subtree.</param>
    /// <param name="destination">Ordered hook destination.</param>
    private void CollectLifecycles(DockNode node, List<PanelLifecycle> destination)
    {
        if (node is DockTabGroup group)
        {
            for (var index = 0; index < group.Tabs.Count; index++)
            {
                if (_lifecycles.TryGetValue(group.Tabs[index].Id, out var lifecycle))
                    destination.Add(lifecycle);
            }
            return;
        }
        var split = (DockSplit)node;
        CollectLifecycles(split.First, destination);
        CollectLifecycles(split.Second, destination);
    }

    /// <summary>Chooses a concise native title from the first tab in a subtree.</summary>
    /// <param name="node">Dock subtree.</param>
    /// <returns>First tab title, or a generic Editor title.</returns>
    private static string GetTitle(DockNode node)
    {
        if (node is DockTabGroup group)
            return group.Tabs.Count == 0 ? "Editor" : group.Tabs[0].Title;
        var split = (DockSplit)node;
        var first = GetTitle(split.First);
        return string.Equals(first, "Editor", StringComparison.Ordinal)
            ? GetTitle(split.Second)
            : first;
    }

    /// <summary>Stores optional resource-transfer callbacks for one panel.</summary>
    /// <param name="Opened">Post-open callback.</param>
    /// <param name="Closing">Pre-close callback.</param>
    internal sealed record PanelLifecycle(
        Action<DetachedToolWindow>? Opened,
        Action<DetachedToolWindow>? Closing);

    /// <summary>Adapts an Editor native tool window to the reusable docking contract.</summary>
    private sealed class FloatingWindow : IDockFloatingWindow, IDockFloatingGeometry,
        IDockFloatingWindowCoordinates, IDockFloatingDragHost
    {
        private readonly FloatingDockRoot _model;
        private readonly DetachedToolWindow _window;
        private readonly List<PanelLifecycle> _lifecycles;
        private bool _disposed;

        /// <inheritdoc/>
        public bool IsOpen => !_disposed && _window.IsOpen;

        /// <inheritdoc/>
        public IWindowCoordinateMapper CoordinateMapper => _window.Window;

        /// <inheritdoc/>
        public UIEventRouter InputRouter => _window.UIHost.InputRouter;

        /// <inheritdoc/>
        public void RefreshDockPreview() => _window.UIHost.Refresh();

        /// <summary>Creates an owned floating-window adapter.</summary>
        /// <param name="model">Persisted floating model.</param>
        /// <param name="window">Native Editor tool window.</param>
        /// <param name="lifecycles">Panel hooks invoked around disposal.</param>
        internal FloatingWindow(
            FloatingDockRoot model,
            DetachedToolWindow window,
            List<PanelLifecycle> lifecycles)
        {
            _model = model;
            _window = window;
            _lifecycles = lifecycles;
        }

        /// <inheritdoc/>
        public void SynchronizeGeometry()
        {
            if (_disposed)
                return;
            var position = _window.Window.ClientPosition;
            var size = _window.Window.ClientSize;
            _model.Left = position.X;
            _model.Top = position.Y;
            _model.Width = MathF.Max(160f, size.X);
            _model.Height = MathF.Max(120f, size.Y);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
                return;
            SynchronizeGeometry();
            _disposed = true;
            for (var index = _lifecycles.Count - 1; index >= 0; index--)
                _lifecycles[index].Closing?.Invoke(_window);
            _window.Dispose();
        }
    }
}
