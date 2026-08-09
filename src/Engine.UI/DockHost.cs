using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Materializes a retained dock workspace using caller-owned panel content.</summary>
public sealed class DockHost : UIElement
{
    private readonly DockWorkspace _workspace;
    private readonly Func<string, UIElement?> _resolveContent;
    private readonly Func<DockNode> _resolveRoot;
    private readonly Func<string, bool> _canFloat;
    private readonly UITheme _theme;
    private readonly List<DockTabGroupPresenter> _groupPresenters = [];

    /// <summary>Gets the workspace represented by this host.</summary>
    public DockWorkspace Workspace => _workspace;

    /// <summary>Occurs after this host commits a dock workspace mutation.</summary>
    public event Action? WorkspaceChanged;

    /// <summary>Occurs when a tab is released beyond this host for cross-host docking or floating.</summary>
    public event Action<DockHost, string, Vector2, bool>? TabFloatRequested;

    /// <summary>Occurs after a live splitter resize completes and expensive dependents may resize.</summary>
    public event Action? SplitResizeCompleted;

    /// <summary>Creates a retained dock host.</summary>
    /// <param name="workspace">Dock model to display.</param>
    /// <param name="resolveContent">Maps stable tab identifiers to retained content.</param>
    /// <param name="theme">Theme supplying tab and splitter visuals.</param>
    /// <param name="resolveRoot">Optionally selects a floating subtree from the workspace.</param>
    /// <param name="canFloat">Optionally authorizes drag-outside floating by panel identifier.</param>
    public DockHost(
        DockWorkspace workspace,
        Func<string, UIElement?> resolveContent,
        UITheme? theme = null,
        Func<DockNode>? resolveRoot = null,
        Func<string, bool>? canFloat = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(resolveContent);
        _workspace = workspace;
        _resolveContent = resolveContent;
        _theme = theme ?? UITheme.Dark;
        Margin = new Thickness(4f);
        _resolveRoot = resolveRoot ?? (() => _workspace.Root);
        _canFloat = canFloat ?? (_ => true);
        Refresh();
    }

    /// <summary>Rebuilds presenters after structural dock-model mutation.</summary>
    public void Refresh()
    {
        _workspace.Normalize();
        _groupPresenters.Clear();
        ClearChildren();
        AddChild(CreatePresenter(_resolveRoot()));
        InvalidateMeasure();
    }

    /// <summary>Finds the deepest tab well containing a host-space pointer position.</summary>
    /// <param name="position">Pointer position in this host's coordinate space.</param>
    /// <param name="group">Matched dock group.</param>
    /// <param name="bounds">Matched logical host bounds.</param>
    /// <returns>True when a presented tab well contains the position.</returns>
    public bool TryGetDropTarget(
        Vector2 position,
        out DockTabGroup? group,
        out Engine.Graphics.UIClipRect bounds)
    {
        for (var index = _groupPresenters.Count - 1; index >= 0; index--)
        {
            var presenter = _groupPresenters[index];
            var candidate = new Engine.Graphics.UIClipRect(
                presenter.Left - Left,
                presenter.Top - Top,
                presenter.Right - Left,
                presenter.Bottom - Top);
            if (!candidate.Contains(position.X, position.Y))
                continue;
            group = presenter.Group;
            bounds = candidate;
            return true;
        }
        group = null;
        bounds = default;
        return false;
    }

    /// <summary>Updates the cross-window dock preview under a host-space pointer position.</summary>
    /// <param name="position">Pointer position in this host's coordinate space.</param>
    /// <returns>The active drop placement, or null when the pointer misses every target glyph.</returns>
    public DockDropPlacement? UpdateExternalDockPreview(Vector2 position)
    {
        DockTabGroupPresenter? matched = null;
        for (var index = _groupPresenters.Count - 1; index >= 0; index--)
        {
            var presenter = _groupPresenters[index];
            var bounds = new Engine.Graphics.UIClipRect(
                presenter.Left - Left,
                presenter.Top - Top,
                presenter.Right - Left,
                presenter.Bottom - Top);
            if (bounds.Contains(position.X, position.Y))
            {
                matched = presenter;
                break;
            }
        }
        for (var index = 0; index < _groupPresenters.Count; index++)
        {
            var presenter = _groupPresenters[index];
            if (!ReferenceEquals(presenter, matched))
                presenter.ClearExternalPreview();
        }
        return matched?.UpdateExternalPreview(position + new Vector2(Left, Top));
    }

    /// <summary>Clears any cross-window dock target or insertion preview.</summary>
    /// <returns>True when a visible preview was cleared.</returns>
    public bool ClearExternalDockPreview()
    {
        var changed = false;
        for (var index = 0; index < _groupPresenters.Count; index++)
            changed |= _groupPresenters[index].ClearExternalPreview();
        return changed;
    }

    /// <summary>Closes one dock tab and collapses any newly empty split branch.</summary>
    /// <param name="tabId">Stable panel identifier.</param>
    /// <returns>True when the panel was present.</returns>
    public bool CloseTab(string tabId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        if (_workspace.RemoveTab(tabId) is null)
            return false;
        Refresh();
        NotifyWorkspaceChanged();
        return true;
    }

    /// <summary>Notifies the owning dock session after a presenter commits a mutation.</summary>
    internal void NotifyWorkspaceChanged() => WorkspaceChanged?.Invoke();

    /// <summary>Notifies presentation owners after the user finishes resizing a split.</summary>
    internal void NotifySplitResizeCompleted() => SplitResizeCompleted?.Invoke();

    /// <summary>Requests a floating root for a tab released beyond this host.</summary>
    /// <param name="tabId">Stable tab identifier.</param>
    /// <param name="position">Release position in host coordinates.</param>
    internal void RequestTabFloat(string tabId, Vector2 position) =>
        RequestTabFloatCore(tabId, position);

    /// <summary>Raises an authorized floating request.</summary>
    /// <param name="tabId">Stable tab identifier.</param>
    /// <param name="position">Release position.</param>
    private void RequestTabFloatCore(string tabId, Vector2 position)
    {
        TabFloatRequested?.Invoke(this, tabId, position, _canFloat(tabId));
    }

    /// <summary>Creates the presenter matching one model node.</summary>
    /// <param name="node">Dock node.</param>
    /// <returns>Retained presenter.</returns>
    private UIElement CreatePresenter(DockNode node)
    {
        return node switch
        {
            DockTabGroup group => CreateTabPresenter(group),
            DockSplit split => new DockSplitPresenter(
                split, CreatePresenter(split.First), CreatePresenter(split.Second),
                NotifySplitResizeCompleted, _theme),
            _ => throw new InvalidOperationException($"Unsupported dock node {node.GetType().Name}.")
        };
    }

    /// <summary>Creates a tab well and mounts each registered panel exactly once.</summary>
    /// <param name="group">Tab group model.</param>
    /// <returns>Tab presenter.</returns>
    private UIElement CreateTabPresenter(DockTabGroup group)
    {
        if (group.Tabs.Count == 0)
            return new Panel(_theme.Surface, 0f, 0f, _theme);
        var tabs = new TabControl(0f, 0f, _theme.ControlHeight, _theme);
        var mountedContents = new List<UIElement>(group.Tabs.Count);
        var selectedIndex = 0;
        for (var index = 0; index < group.Tabs.Count; index++)
        {
            var tab = group.Tabs[index];
            var content = _resolveContent(tab.Id) ?? CreateMissingContent(tab.Id);
            var contentScroller = new ScrollViewer(theme: _theme)
            {
                BackgroundColor = _theme.Surface,
                CornerRadius = _theme.PanelCornerRadius,
                CornerMode = BoxCornerMode.TopRight | BoxCornerMode.Bottom,
                Padding = new Thickness(3f, 5f, 3f, 5f),
                Content = content
            };
            mountedContents.Add(content);
            var tabId = tab.Id;
            tabs.AddTab(tab.Title, contentScroller);
            tabs.GetHeader(index).DragData = new UIDragData(
                new DockTabDragData(tab.Id), tab.Title);
            tabs.GetHeader(index).AllowedDragEffects = UIDragEffect.Move;
            tabs.GetHeader(index).Key += (_, keyEvent) => CloseTabFromKeyboard(tabId, keyEvent);
            if (string.Equals(tab.Id, group.SelectedId, StringComparison.Ordinal))
                selectedIndex = index;
        }
        tabs.Select(selectedIndex);
        SynchronizeContentVisibility(mountedContents, selectedIndex);
        tabs.SelectionChanged += (index, _) =>
        {
            group.SelectedId = group.Tabs[index].Id;
            SynchronizeContentVisibility(mountedContents, index);
        };
        var presenter = new DockTabGroupPresenter(this, group, tabs, _theme);
        _groupPresenters.Add(presenter);
        return presenter;
    }

    /// <summary>Keeps caller-owned content visibility aligned with its scroll-wrapper tab state.</summary>
    /// <param name="contents">Mounted content in tab order.</param>
    /// <param name="selectedIndex">Currently selected content index.</param>
    private static void SynchronizeContentVisibility(List<UIElement> contents, int selectedIndex)
    {
        for (var index = 0; index < contents.Count; index++)
            contents[index].IsVisible = index == selectedIndex;
    }

    /// <summary>Handles the platform close-tab gesture from a focused header.</summary>
    /// <param name="tabId">Stable panel identifier.</param>
    /// <param name="keyEvent">Routed keyboard transition.</param>
    private void CloseTabFromKeyboard(string tabId, UIKeyEventArgs keyEvent)
    {
        if (keyEvent.RoutePhase != UIRoutePhase.Target ||
            keyEvent.Kind != UIKeyEventKind.KeyDown ||
            keyEvent.IsRepeat || keyEvent.Key != Engine.Graphics.InputKey.W ||
            (keyEvent.Modifiers != Engine.Graphics.InputModifiers.Control &&
             keyEvent.Modifiers != Engine.Graphics.InputModifiers.Super))
            return;
        if (CloseTab(tabId))
            keyEvent.Handled = true;
    }

    /// <summary>Creates a visible placeholder for an unavailable panel registration.</summary>
    /// <param name="id">Missing panel identifier.</param>
    /// <returns>Placeholder content.</returns>
    private UIElement CreateMissingContent(string id)
    {
        return new Label($"Missing dock panel: {id}")
        {
            ForegroundColor = _theme.TextMuted,
            PaddingLeft = _theme.ItemRowPadding,
            IsHitTestVisible = false
        };
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (Children.Count == 0 || Children[0] is not UIElement child)
            return Vector2.Zero;
        child.Measure(new Vector2(
            MathF.Max(0f, availableSize.X - Padding.Horizontal),
            MathF.Max(0f, availableSize.Y - Padding.Vertical)));
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        if (Children.Count > 0 && Children[0] is UIElement child)
            child.Arrange(new Vector2(Padding.Left, Padding.Top), contentSize);
    }
}

/// <summary>Identifies a dock tab carried by the routed drag-and-drop system.</summary>
/// <param name="TabId">Stable dock tab identifier.</param>
public sealed record DockTabDragData(string TabId);

/// <summary>Describes one negotiated dock destination.</summary>
/// <param name="Group">Authoritative destination tab group.</param>
/// <param name="Zone">Center or edge insertion zone.</param>
/// <param name="TargetIndex">Center-drop insertion index, or -1 to append.</param>
public readonly record struct DockDropPlacement(
    DockTabGroup Group,
    DockDropZone Zone,
    int TargetIndex = -1);

/// <summary>Hosts one tab group and commits routed tab drops through its dock workspace.</summary>
internal sealed class DockTabGroupPresenter : Box
{
    private readonly DockHost _host;
    private readonly DockTabGroup _group;
    private readonly TabControl _tabs;
    private readonly DockDropOverlay _overlay;
    private int _tabInsertionIndex = -1;

    /// <summary>Gets the presented model group.</summary>
    internal DockTabGroup Group => _group;

    /// <summary>Creates one interactive tab-group presenter.</summary>
    /// <param name="host">Owning dock host.</param>
    /// <param name="group">Presented model group.</param>
    /// <param name="tabs">Materialized tab control.</param>
    /// <param name="theme">Theme supplying overlay visuals.</param>
    internal DockTabGroupPresenter(DockHost host, DockTabGroup group, TabControl tabs, UITheme theme)
    {
        _host = host;
        _group = group;
        _tabs = tabs;
        _overlay = new DockDropOverlay(theme);
        PaintBackground = false;
        CornerRadius = theme.PanelCornerRadius;
        AllowDrop = true;
        Drag += HandleDrag;
        AddChild(_tabs);
        AddChild(_overlay);
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _tabs.Measure(availableSize);
        _overlay.Measure(availableSize);
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        _tabs.Arrange(Vector2.Zero, contentSize);
        _overlay.Arrange(Vector2.Zero, contentSize);
    }

    /// <summary>Updates the presenter overlay from an absolute logical pointer position.</summary>
    /// <param name="position">Absolute logical pointer position.</param>
    /// <returns>The active placement, or null while only inactive target glyphs are visible.</returns>
    internal DockDropPlacement? UpdateExternalPreview(Vector2 position)
    {
        var localY = position.Y - Top;
        if (localY >= 0f && localY <= _tabs.HeaderHeight)
        {
            _tabInsertionIndex = _tabs.GetInsertionIndex(position.X);
            _overlay.ShowTabInsertion(
                new Engine.Graphics.UIClipRect(Left, Top, Right, Top + _tabs.HeaderHeight),
                _tabs.GetInsertionX(_tabInsertionIndex));
            return new DockDropPlacement(_group, DockDropZone.Center, _tabInsertionIndex);
        }
        _tabInsertionIndex = -1;
        if (_overlay.IsTabInsertion)
            _overlay.Hide();
        if (!_overlay.IsActive)
            _overlay.Show(new Engine.Graphics.UIClipRect(Left, Top, Right, Bottom));
        return _overlay.UpdatePointer(position) is { } zone
            ? new DockDropPlacement(_group, zone)
            : null;
    }

    /// <summary>Clears an externally coordinated preview.</summary>
    /// <returns>True when visible preview state changed.</returns>
    internal bool ClearExternalPreview()
    {
        _tabInsertionIndex = -1;
        if (!_overlay.IsActive)
            return false;
        _overlay.Hide();
        return true;
    }

    /// <summary>Negotiates, previews, and commits one routed dock-tab drag.</summary>
    /// <param name="sender">Current routed sender.</param>
    /// <param name="dragEvent">Routed drag data.</param>
    private void HandleDrag(UIElement sender, UIDragEventArgs dragEvent)
    {
        if (!dragEvent.Data.TryGet<DockTabDragData>(out var tab) || tab is null)
            return;
        if (dragEvent.Kind == UIDragEventKind.Cancel &&
            dragEvent.RoutePhase == UIRoutePhase.Bubble &&
            (dragEvent.Position.X < _host.Left || dragEvent.Position.X > _host.Right ||
             dragEvent.Position.Y < _host.Top || dragEvent.Position.Y > _host.Bottom))
        {
            _host.RequestTabFloat(tab.TabId, dragEvent.Position);
            dragEvent.Handled = true;
            return;
        }
        if (dragEvent.RoutePhase != UIRoutePhase.Target)
            return;
        if (dragEvent.Kind is UIDragEventKind.Enter or UIDragEventKind.Over)
        {
            if (dragEvent.LocalPosition.Y >= 0f && dragEvent.LocalPosition.Y <= _tabs.HeaderHeight)
            {
                _tabInsertionIndex = _tabs.GetInsertionIndex(dragEvent.Position.X);
                _overlay.ShowTabInsertion(
                    new Engine.Graphics.UIClipRect(Left, Top, Right, Top + _tabs.HeaderHeight),
                    _tabs.GetInsertionX(_tabInsertionIndex));
                dragEvent.Effect = UIDragEffect.Move;
                dragEvent.Handled = true;
                return;
            }
            _tabInsertionIndex = -1;
            if (_overlay.IsTabInsertion)
                _overlay.Hide();
            if (!_overlay.IsActive)
                _overlay.Show(new Engine.Graphics.UIClipRect(Left, Top, Right, Bottom));
            var activeZone = _overlay.UpdatePointer(dragEvent.Position);
            dragEvent.Effect = activeZone is null ? UIDragEffect.None : UIDragEffect.Move;
            dragEvent.Handled = true;
            return;
        }
        if (dragEvent.Kind == UIDragEventKind.Leave || dragEvent.Kind == UIDragEventKind.Cancel)
        {
            _tabInsertionIndex = -1;
            _overlay.Hide();
            dragEvent.Handled = true;
            return;
        }
        if (dragEvent.Kind != UIDragEventKind.Drop)
            return;
        var insertionIndex = _tabInsertionIndex;
        var dropZone = insertionIndex >= 0
            ? DockDropZone.Center
            : _overlay.UpdatePointer(dragEvent.Position);
        _tabInsertionIndex = -1;
        _overlay.Hide();
        if (dropZone is { } zone && _host.Workspace.DockTab(
                tab.TabId, _group, zone, targetIndex: insertionIndex))
        {
            dragEvent.Effect = UIDragEffect.Move;
            _host.Refresh();
            _host.NotifyWorkspaceChanged();
        }
        dragEvent.Handled = true;
    }
}

/// <summary>Arranges two dock presenters around a draggable splitter.</summary>
internal sealed class DockSplitPresenter : UIElement
{
    private const float SplitterThickness = 4f;
    private readonly DockSplit _model;
    private readonly UIElement _first;
    private readonly UIElement _second;
    private readonly Thumb _splitter;
    private Vector2 _arrangedSize;

    /// <summary>Creates a presenter for one split node.</summary>
    /// <param name="model">Split model.</param>
    /// <param name="first">First child presenter.</param>
    /// <param name="second">Second child presenter.</param>
    /// <param name="resizeCompleted">Callback invoked once when live resizing ends.</param>
    /// <param name="theme">Theme supplying splitter visuals.</param>
    internal DockSplitPresenter(
        DockSplit model,
        UIElement first,
        UIElement second,
        Action resizeCompleted,
        UITheme theme)
    {
        _model = model;
        _first = first;
        _second = second;
        _splitter = new Thumb(theme, isTransparent: true, enableHoverState: false);
        _splitter.CursorKind = _model.Orientation == DockSplitOrientation.Horizontal
            ? PointerCursorKind.HorizontalResize
            : PointerCursorKind.VerticalResize;
        _splitter.DragDelta += Resize;
        _splitter.DragCompleted += resizeCompleted;
        AddChild(_first);
        AddChild(_second);
        AddChild(_splitter);
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var availableLength = MathF.Max(0f,
            (_model.Orientation == DockSplitOrientation.Horizontal
                ? availableSize.X
                : availableSize.Y) - SplitterThickness);
        var firstLength = availableLength * _model.Ratio;
        if (_model.Orientation == DockSplitOrientation.Horizontal)
        {
            _first.Measure(new Vector2(firstLength, availableSize.Y));
            _second.Measure(new Vector2(availableLength - firstLength, availableSize.Y));
        }
        else
        {
            _first.Measure(new Vector2(availableSize.X, firstLength));
            _second.Measure(new Vector2(availableSize.X, availableLength - firstLength));
        }
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        _arrangedSize = contentSize;
        var availableLength = MathF.Max(0f,
            (_model.Orientation == DockSplitOrientation.Horizontal
                ? contentSize.X
                : contentSize.Y) - SplitterThickness);
        var firstLength = availableLength * _model.Ratio;
        if (_model.Orientation == DockSplitOrientation.Horizontal)
        {
            _first.Arrange(Vector2.Zero, new Vector2(firstLength, contentSize.Y));
            _splitter.Arrange(new Vector2(firstLength, 0f),
                new Vector2(SplitterThickness, contentSize.Y));
            _second.Arrange(new Vector2(firstLength + SplitterThickness, 0f),
                new Vector2(availableLength - firstLength, contentSize.Y));
        }
        else
        {
            _first.Arrange(Vector2.Zero, new Vector2(contentSize.X, firstLength));
            _splitter.Arrange(new Vector2(0f, firstLength),
                new Vector2(contentSize.X, SplitterThickness));
            _second.Arrange(new Vector2(0f, firstLength + SplitterThickness),
                new Vector2(contentSize.X, availableLength - firstLength));
        }
    }

    /// <summary>Updates the normalized split ratio from a pointer delta.</summary>
    /// <param name="delta">Logical drag delta.</param>
    private void Resize(Vector2 delta)
    {
        var availableLength = (_model.Orientation == DockSplitOrientation.Horizontal
            ? _arrangedSize.X
            : _arrangedSize.Y) - SplitterThickness;
        if (availableLength <= 0f)
            return;
        var movement = _model.Orientation == DockSplitOrientation.Horizontal ? delta.X : delta.Y;
        _model.Ratio += movement / availableLength;
        InvalidateMeasure();
    }
}
