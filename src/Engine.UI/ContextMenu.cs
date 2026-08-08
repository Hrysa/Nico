using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>A floating menu containing keyboard-navigable actions and nested menus.</summary>
public sealed class ContextMenu : Popup
{
    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.Menu, Name, null, IsEnabled, true, false, null);

    private const float ItemHeight = 26f;
    private const float SeparatorHeight = 9f;
    private readonly UITheme _theme;
    private readonly List<ContextMenuItem> _items = [];
    private readonly List<ContextMenu> _submenus = [];
    private readonly List<UIElement> _rows = [];
    private readonly Panel _rowViewport;
    private string _typeAhead = string.Empty;
    private double _typeAheadElapsed;
    private ContextMenuItem? _pendingSubmenu;
    private Action? _pendingSubmenuAction;
    private double _submenuElapsed;
    private double _pendingSubmenuDelay;
    private ContextMenuItem? _openSubmenuOwner;
    private Vector2 _lastPointerPosition;
    private Vector2 _corridorOrigin;
    private float _contentHeight = 4f;
    private float _scrollOffset;
    private float _maxVisibleHeight = float.PositiveInfinity;

    /// <summary>Gets or sets pointer-hover delay before a submenu opens.</summary>
    public double SubmenuOpenDelay { get; set; } = 0.25d;

    /// <summary>Gets or sets additional delay while the pointer travels through an open submenu corridor.</summary>
    public double SubmenuCorridorDelay { get; set; } = 0.4d;

    /// <summary>Gets or sets the maximum visible menu height before rows scroll.</summary>
    public float MaxVisibleHeight
    {
        get => _maxVisibleHeight;
        set
        {
            if (value <= 0f || float.IsNaN(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _maxVisibleHeight = value;
            RecalculateHeight();
        }
    }

    /// <summary>Gets the current vertical row scroll offset.</summary>
    public float ScrollOffset => _scrollOffset;

    /// <summary>Gets or sets whether owned submenus flip and clamp against the top-level UI root.</summary>
    public bool ConstrainSubmenusToHost { get; set; } = true;

    /// <summary>Gets the menu's action rows in display order.</summary>
    public IReadOnlyList<ContextMenuItem> Items => _items;

    /// <summary>Creates an empty context menu.</summary>
    /// <param name="width">Menu width.</param>
    /// <param name="theme">Theme supplying menu colors and typography.</param>
    public ContextMenu(float width, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).SurfaceRaised, (theme ?? UITheme.Dark).BorderStrong, width)
    {
        _theme = theme ?? UITheme.Dark;
        IsOverlay = true;
        _rowViewport = new Panel()
        {
            ClipToBounds = true
        };
        AddChild(_rowViewport);
        Closed += CloseSubmenus;
        RoutedTextInput += OnRoutedTextInput;
        Pointer += OnMenuPointer;
    }

    /// <summary>Adds an action to the menu.</summary>
    /// <param name="label">Action label.</param>
    /// <param name="action">Action invoked when clicked or activated.</param>
    public void AddItem(string label, Action action)
    {
        AddItem(label, action, isEnabled: true);
    }

    /// <summary>Adds an optionally disabled action to the menu.</summary>
    /// <param name="label">Action label.</param>
    /// <param name="action">Action invoked when activated.</param>
    /// <param name="isEnabled">Whether pointer and keyboard activation are allowed.</param>
    public void AddItem(string label, Action action, bool isEnabled)
    {
        ArgumentNullException.ThrowIfNull(action);
        var item = CreateItem(label);
        item.IsEnabled = isEnabled;
        item.Click += action;
        item.Click += CloseRootMenu;
        AddItemCore(item);
    }

    /// <summary>Adds an action with a right-aligned accelerator hint.</summary>
    /// <param name="label">Action label.</param>
    /// <param name="gesture">Gesture displayed as a hint.</param>
    /// <param name="action">Action invoked when activated.</param>
    /// <param name="isEnabled">Whether activation is allowed.</param>
    public void AddItem(string label, UIKeyGesture gesture, Action action, bool isEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(action);
        var item = CreateItem(label);
        item.SetAcceleratorText(gesture.ToDisplayString());
        item.IsEnabled = isEnabled;
        item.Click += action;
        item.Click += CloseRootMenu;
        AddItemCore(item);
    }

    /// <summary>Adds an action with an arbitrary retained icon element.</summary>
    /// <param name="label">Action label.</param>
    /// <param name="icon">Non-interactive icon element.</param>
    /// <param name="action">Action invoked when activated.</param>
    /// <param name="isEnabled">Whether activation is allowed.</param>
    public void AddItem(string label, UIElement icon, Action action, bool isEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentNullException.ThrowIfNull(action);
        var item = CreateItem(label);
        item.SetIcon(icon);
        item.IsEnabled = isEnabled;
        item.Click += action;
        item.Click += CloseRootMenu;
        AddItemCore(item);
    }

    /// <summary>Adds a checkable action whose state toggles before its callback runs.</summary>
    /// <param name="label">Action label with optional mnemonic markup.</param>
    /// <param name="isChecked">Initial checked state.</param>
    /// <param name="changed">Callback receiving the new checked state.</param>
    /// <returns>The created stateful menu row.</returns>
    public ContextMenuItem AddCheckItem(string label, bool isChecked, Action<bool> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var item = CreateItem(label);
        item.ConfigureCheck(isChecked, groupName: null);
        item.Click += () =>
        {
            item.SetChecked(!item.IsChecked);
            changed(item.IsChecked);
        };
        item.Click += CloseRootMenu;
        AddItemCore(item);
        return item;
    }

    /// <summary>Adds an exclusive radio action within a named menu-local group.</summary>
    /// <param name="label">Action label with optional mnemonic markup.</param>
    /// <param name="groupName">Non-empty local exclusivity group.</param>
    /// <param name="isChecked">Initial selected state.</param>
    /// <param name="selected">Callback invoked after this row becomes selected.</param>
    /// <returns>The created stateful menu row.</returns>
    public ContextMenuItem AddRadioItem(string label, string groupName, bool isChecked, Action selected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentNullException.ThrowIfNull(selected);
        var item = CreateItem(label);
        item.ConfigureCheck(isChecked, groupName);
        if (isChecked)
            ClearRadioGroup(groupName, except: null);
        item.Click += () =>
        {
            ClearRadioGroup(groupName, item);
            item.SetChecked(true);
            selected();
        };
        item.Click += CloseRootMenu;
        AddItemCore(item);
        return item;
    }

    /// <summary>Adds a non-interactive visual separator.</summary>
    public void AddSeparator()
    {
        var separator = new ContextMenuSeparator(_theme.BorderStrong, Width - 12f, SeparatorHeight);
        _rows.Add(separator);
        _rowViewport.AddChild(separator);
        RecalculateHeight();
    }

    /// <summary>Adds a nested child menu with pointer and keyboard ownership.</summary>
    /// <param name="label">Item label.</param>
    /// <param name="submenu">Child menu displayed beside this item.</param>
    public void AddSubmenu(string label, ContextMenu submenu)
    {
        ArgumentNullException.ThrowIfNull(submenu);
        var item = CreateItem($"{label}  ›");
        item.Submenu = submenu;
        submenu.Owner = item;
        submenu.Close();
        item.SubmenuRequested += () => QueueSubmenu(item, () => OpenSubmenu(item));
        item.MouseLeave += () => CancelPendingSubmenu(item);
        _submenus.Add(submenu);
        AddChild(submenu);
        AddItemCore(item);
    }

    /// <summary>Adds an item that delegates child-menu display to an external overlay owner.</summary>
    /// <param name="label">Item label.</param>
    /// <param name="showSubmenu">Action that displays the child menu.</param>
    public void AddSubmenu(string label, Action<ContextMenuItem> showSubmenu)
    {
        ArgumentNullException.ThrowIfNull(showSubmenu);
        var item = CreateItem($"{label}  ›");
        item.SubmenuRequested += () => QueueSubmenu(item, () => showSubmenu(item));
        item.MouseLeave += () => CancelPendingSubmenu(item);
        AddItemCore(item);
    }

    /// <summary>Focuses the first enabled row through a routed key event.</summary>
    /// <param name="keyEvent">Key event providing host-local focus access.</param>
    internal void FocusFirst(UIKeyEventArgs keyEvent)
    {
        for (var index = 0; index < _items.Count; index++)
        {
            if (!_items[index].IsEnabled)
                continue;
            keyEvent.Focus(_items[index]);
            EnsureItemVisible(_items[index]);
            return;
        }
    }

    /// <summary>Adds and wires one menu row.</summary>
    /// <param name="item">Configured row.</param>
    private void AddItemCore(ContextMenuItem item)
    {
        item.Key += OnItemKey;
        _items.Add(item);
        _rows.Add(item);
        _rowViewport.AddChild(item);
        RecalculateHeight();
    }

    /// <summary>Creates a context-menu row.</summary>
    /// <param name="label">Displayed row label.</param>
    /// <returns>The unparented row.</returns>
    private ContextMenuItem CreateItem(string label)
    {
        var parsed = ParseMnemonic(label);
        return new ContextMenuItem(Width - 4f, ItemHeight, parsed.Label, _theme)
        {
            Mnemonic = parsed.Mnemonic
        };
    }

    /// <summary>Handles arrow and activation keys for a focused menu row.</summary>
    /// <param name="sender">Focused row.</param>
    /// <param name="keyEvent">Routed keyboard input.</param>
    private void OnItemKey(UIElement sender, UIKeyEventArgs keyEvent)
    {
        if (keyEvent.Kind != UIKeyEventKind.KeyDown || keyEvent.RoutePhase != UIRoutePhase.Target ||
            sender is not ContextMenuItem item)
            return;
        var index = _items.IndexOf(item);
        if ((keyEvent.Modifiers & InputModifiers.Alt) != 0 && TryGetLetter(keyEvent.Key, out var mnemonic) &&
            FindMnemonic(mnemonic) is { } mnemonicItem)
        {
            ActivateItem(mnemonicItem, keyEvent);
        }
        else if (keyEvent.Key == InputKey.Down)
            FocusNextEnabled(index, 1, keyEvent);
        else if (keyEvent.Key == InputKey.Up)
            FocusNextEnabled(index, -1, keyEvent);
        else if (keyEvent.Key == InputKey.Home)
            FocusBoundary(first: true, keyEvent);
        else if (keyEvent.Key == InputKey.End)
            FocusBoundary(first: false, keyEvent);
        else if (keyEvent.Key == InputKey.PageDown)
            FocusPage(index, 1, keyEvent);
        else if (keyEvent.Key == InputKey.PageUp)
            FocusPage(index, -1, keyEvent);
        else if (keyEvent.Key is InputKey.Enter or InputKey.Space)
            ActivateItem(item, keyEvent);
        else if (keyEvent.Key == InputKey.Right && item.Submenu is { } submenu)
        {
            OpenSubmenu(item);
            submenu.FocusFirst(keyEvent);
        }
        else if (keyEvent.Key == InputKey.Left && Owner is ContextMenuItem ownerItem)
        {
            Close();
            keyEvent.Focus(ownerItem);
        }
        else
            return;
        keyEvent.Handled = true;
    }

    /// <summary>Uses committed text to focus the next row whose label starts with the typed prefix.</summary>
    /// <param name="sender">Current route receiver.</param>
    /// <param name="textEvent">Committed text input.</param>
    private void OnRoutedTextInput(UIElement sender, UITextInputEventArgs textEvent)
    {
        if (textEvent.Text.Length == 0 || _items.Count == 0)
            return;
        var incoming = textEvent.Text;
        var repeatedSingle = _typeAhead.Length == 1 && incoming.Length == 1 &&
            char.ToUpperInvariant(_typeAhead[0]) == char.ToUpperInvariant(incoming[0]);
        _typeAhead = repeatedSingle ? incoming : _typeAhead + incoming;
        _typeAheadElapsed = 0d;
        var currentIndex = textEvent.Source is ContextMenuItem current ? _items.IndexOf(current) : -1;
        for (var offset = 1; offset <= _items.Count; offset++)
        {
            var item = _items[(currentIndex + offset) % _items.Count];
            if (!item.IsEnabled || !item.LabelText.StartsWith(
                    _typeAhead, StringComparison.CurrentCultureIgnoreCase))
                continue;
            textEvent.Focus(item);
            EnsureItemVisible(item);
            textEvent.Handled = true;
            return;
        }
    }

    /// <summary>Clears an idle type-ahead prefix.</summary>
    /// <param name="deltaTime">Elapsed host time.</param>
    /// <returns>False because clearing the search prefix has no visual effect.</returns>
    protected override bool UpdateElement(double deltaTime)
    {
        if (deltaTime <= 0d)
            return false;
        if (_typeAhead.Length > 0)
        {
            _typeAheadElapsed += deltaTime;
            if (_typeAheadElapsed >= 0.75d)
            {
                _typeAhead = string.Empty;
                _typeAheadElapsed = 0d;
            }
        }
        if (_pendingSubmenuAction is null)
            return false;
        _submenuElapsed += deltaTime;
        if (_submenuElapsed < Math.Max(0d, _pendingSubmenuDelay))
            return false;
        var action = _pendingSubmenuAction;
        _pendingSubmenu = null;
        _pendingSubmenuAction = null;
        _submenuElapsed = 0d;
        action();
        return true;
    }

    /// <inheritdoc/>
    protected override bool IsTimeUpdateActive =>
        _typeAhead.Length > 0 || _pendingSubmenuAction is not null;

    /// <summary>Queues one pointer-triggered submenu request.</summary>
    /// <param name="item">Hovered owner row.</param>
    /// <param name="open">Action opening its owned or externally hosted submenu.</param>
    private void QueueSubmenu(ContextMenuItem item, Action open)
    {
        _pendingSubmenu = item;
        _pendingSubmenuAction = open;
        _submenuElapsed = 0d;
        _pendingSubmenuDelay = SubmenuOpenDelay;
    }

    /// <summary>Cancels a delayed request when its owner is left before the delay elapses.</summary>
    /// <param name="item">Row that lost pointer hover.</param>
    private void CancelPendingSubmenu(ContextMenuItem item)
    {
        if (!ReferenceEquals(_pendingSubmenu, item))
            return;
        _pendingSubmenu = null;
        _pendingSubmenuAction = null;
        _submenuElapsed = 0d;
        _pendingSubmenuDelay = 0d;
    }

    /// <summary>Cancels delayed submenu work when this menu closes.</summary>
    private void CancelPendingSubmenu()
    {
        _pendingSubmenu = null;
        _pendingSubmenuAction = null;
        _submenuElapsed = 0d;
        _pendingSubmenuDelay = 0d;
    }

    /// <summary>Activates an action row or opens and focuses its submenu.</summary>
    /// <param name="item">Row to activate.</param>
    /// <param name="keyEvent">Routed key event providing focus access.</param>
    private void ActivateItem(ContextMenuItem item, UIKeyEventArgs keyEvent)
    {
        if (item.Submenu is { } child)
        {
            OpenSubmenu(item);
            child.FocusFirst(keyEvent);
        }
        else
            item.InvokeClick();
    }

    /// <summary>Finds the first row assigned to a mnemonic letter.</summary>
    /// <param name="mnemonic">Uppercase mnemonic letter.</param>
    /// <returns>Matching row, or null.</returns>
    private ContextMenuItem? FindMnemonic(char mnemonic)
    {
        for (var index = 0; index < _items.Count; index++)
        {
            if (_items[index].IsEnabled && _items[index].Mnemonic == mnemonic)
                return _items[index];
        }
        return null;
    }

    /// <summary>Converts a letter key to an uppercase mnemonic character without allocation.</summary>
    /// <param name="key">Engine key.</param>
    /// <param name="letter">Converted uppercase letter.</param>
    /// <returns>True when the key is A through Z.</returns>
    private static bool TryGetLetter(InputKey key, out char letter)
    {
        if (key >= InputKey.A && key <= InputKey.Z)
        {
            letter = (char)('A' + (int)key - (int)InputKey.A);
            return true;
        }
        letter = default;
        return false;
    }

    /// <summary>Extracts the first ampersand mnemonic and removes its marker from display text.</summary>
    /// <param name="label">Label containing an optional ampersand marker.</param>
    /// <returns>Display label and optional uppercase mnemonic.</returns>
    private static (string Label, char? Mnemonic) ParseMnemonic(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        for (var index = 0; index < label.Length - 1; index++)
        {
            if (label[index] != '&' || label[index + 1] == '&')
                continue;
            return (label.Remove(index, 1), char.ToUpperInvariant(label[index + 1]));
        }
        return (label.Replace("&&", "&", StringComparison.Ordinal), null);
    }

    /// <summary>Moves focus to the next enabled row with wraparound.</summary>
    /// <param name="currentIndex">Current item index.</param>
    /// <param name="direction">Positive for forward, negative for backward.</param>
    /// <param name="keyEvent">Key event providing focus access.</param>
    private void FocusNextEnabled(int currentIndex, int direction, UIKeyEventArgs keyEvent)
    {
        for (var offset = 1; offset <= _items.Count; offset++)
        {
            var index = (currentIndex + direction * offset) % _items.Count;
            if (index < 0)
                index += _items.Count;
            if (!_items[index].IsEnabled)
                continue;
            keyEvent.Focus(_items[index]);
            EnsureItemVisible(_items[index]);
            return;
        }
    }

    /// <summary>Focuses the first or last enabled menu action.</summary>
    /// <param name="first">True for the first row; false for the last.</param>
    /// <param name="keyEvent">Key event providing focus access.</param>
    private void FocusBoundary(bool first, UIKeyEventArgs keyEvent)
    {
        var index = first ? 0 : _items.Count - 1;
        var direction = first ? 1 : -1;
        while (index >= 0 && index < _items.Count)
        {
            if (_items[index].IsEnabled)
            {
                keyEvent.Focus(_items[index]);
                EnsureItemVisible(_items[index]);
                return;
            }
            index += direction;
        }
    }

    /// <summary>Moves focus by approximately one visible page while skipping disabled rows.</summary>
    /// <param name="currentIndex">Current item index.</param>
    /// <param name="direction">Positive for the next page; negative for the previous page.</param>
    /// <param name="keyEvent">Key event providing focus access.</param>
    private void FocusPage(int currentIndex, int direction, UIKeyEventArgs keyEvent)
    {
        var pageSize = Math.Max(1, (int)MathF.Floor(Height / ItemHeight));
        var target = Math.Clamp(currentIndex + direction * pageSize, 0, _items.Count - 1);
        for (var index = target; index >= 0 && index < _items.Count; index += direction)
        {
            if (!_items[index].IsEnabled)
                continue;
            keyEvent.Focus(_items[index]);
            EnsureItemVisible(_items[index]);
            return;
        }
        FocusBoundary(direction < 0, keyEvent);
    }

    /// <summary>Recomputes menu height from interactive rows and separators.</summary>
    private void RecalculateHeight()
    {
        var height = 4f;
        for (var index = 0; index < _rows.Count; index++)
            height += _rows[index] is ContextMenuSeparator ? SeparatorHeight : ItemHeight;
        _contentHeight = height;
        Height = MathF.Min(height, MaxVisibleHeight);
        _scrollOffset = Math.Clamp(_scrollOffset, 0f, GetMaximumScrollOffset());
        RefreshLayout();
    }

    /// <summary>Clears selected radio rows in one menu-local group.</summary>
    /// <param name="groupName">Group to clear.</param>
    /// <param name="except">Row that should retain its state, or null.</param>
    private void ClearRadioGroup(string groupName, ContextMenuItem? except)
    {
        for (var index = 0; index < _items.Count; index++)
        {
            var candidate = _items[index];
            if (!ReferenceEquals(candidate, except) && candidate.RadioGroup == groupName)
                candidate.SetChecked(false);
        }
    }

    /// <summary>Opens one nested menu and closes its siblings.</summary>
    /// <param name="ownerItem">Row owning the requested child menu.</param>
    private void OpenSubmenu(ContextMenuItem ownerItem)
    {
        for (var index = 0; index < _submenus.Count; index++)
        {
            var submenu = _submenus[index];
            if (ReferenceEquals(submenu, ownerItem.Submenu))
                submenu.Open();
            else
                submenu.Close();
        }
        _openSubmenuOwner = ownerItem;
        _corridorOrigin = _lastPointerPosition;
        InvalidateMeasure();
    }

    /// <summary>Closes this menu and every ancestor menu after an action.</summary>
    private void CloseRootMenu()
    {
        ContextMenu? menu = this;
        while (menu is not null)
        {
            menu.Close();
            menu = FindAncestorMenu(menu.Owner);
        }
    }

    /// <summary>Closes all nested menus.</summary>
    private void CloseSubmenus()
    {
        CancelPendingSubmenu();
        _openSubmenuOwner = null;
        for (var index = 0; index < _submenus.Count; index++)
            _submenus[index].Close();
    }

    /// <summary>Refreshes row bounds after menu contents change.</summary>
    private void RefreshLayout()
    {
        var size = new Vector2(Width, Height);
        Measure(size);
        Arrange(Vector2.Zero, size);
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        _rowViewport.Measure(contentSize);
        _rowViewport.Arrange(Vector2.Zero, contentSize);
        var y = 2f - _scrollOffset;
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            var rowHeight = row is ContextMenuSeparator ? SeparatorHeight : ItemHeight;
            var horizontalInset = row is ContextMenuSeparator ? 6f : 2f;
            row.Measure(new Vector2(MathF.Max(0f, contentSize.X - horizontalInset * 2f), rowHeight));
            row.Arrange(new Vector2(horizontalInset, y),
                new Vector2(MathF.Max(0f, contentSize.X - horizontalInset * 2f), rowHeight));
            if (row is ContextMenuItem { Submenu: { } submenu })
            {
                submenu.Measure(new Vector2(submenu.Width, submenu.Height));
                var submenuPosition = GetSubmenuPosition(submenu, y - 2f, contentSize.X);
                submenu.Arrange(submenuPosition,
                    new Vector2(submenu.Width, submenu.Height));
            }
            y += rowHeight;
        }
    }

    /// <summary>Chooses right/left submenu placement and clamps its vertical host position.</summary>
    /// <param name="submenu">Owned submenu.</param>
    /// <param name="preferredY">Parent-relative preferred top.</param>
    /// <param name="contentWidth">Parent content width.</param>
    /// <returns>Parent-relative constrained position.</returns>
    private Vector2 GetSubmenuPosition(ContextMenu submenu, float preferredY, float contentWidth)
    {
        var preferredX = contentWidth - 2f;
        submenu.ActualPlacement = PopupPlacement.Right;
        if (!ConstrainSubmenusToHost || GetTopLevelRoot() is not { } root)
            return new Vector2(preferredX, preferredY);

        var hostRight = root.Right;
        var hostBottom = root.Bottom;
        var absoluteLeft = Left + preferredX;
        if (absoluteLeft + submenu.Width > hostRight)
        {
            preferredX = -submenu.Width + 2f;
            submenu.ActualPlacement = PopupPlacement.Left;
        }
        var absoluteTop = Top + preferredY;
        if (absoluteTop + submenu.Height > hostBottom)
            preferredY -= absoluteTop + submenu.Height - hostBottom;
        if (Top + preferredY < root.Top)
            preferredY += root.Top - (Top + preferredY);
        return new Vector2(preferredX, preferredY);
    }

    /// <summary>Gets the top-level UI root when this menu is attached beneath another element.</summary>
    /// <returns>Host root, or null when this menu itself is the root.</returns>
    private UIElement? GetTopLevelRoot()
    {
        UIElement current = this;
        while (current.Parent is UIElement parent)
            current = parent;
        return ReferenceEquals(current, this) ? null : current;
    }

    /// <summary>Tracks pointer motion and consumes wheel input for constrained menu rows.</summary>
    /// <param name="sender">Current route receiver.</param>
    /// <param name="pointerEvent">Routed pointer input.</param>
    private void OnMenuPointer(UIElement sender, UIPointerEventArgs pointerEvent)
    {
        if (pointerEvent.Kind == UIPointerEventKind.Move)
        {
            if (_openSubmenuOwner is { IsHovered: true })
                _corridorOrigin = pointerEvent.Position;
            _lastPointerPosition = pointerEvent.Position;
            if (_pendingSubmenu is not null && !ReferenceEquals(_pendingSubmenu, _openSubmenuOwner) &&
                IsInsideOpenSubmenuCorridor(pointerEvent.Position))
                _pendingSubmenuDelay = Math.Max(_pendingSubmenuDelay, SubmenuCorridorDelay);
            if (_openSubmenuOwner?.Submenu is { } open && IsDescendantOrSelf(pointerEvent.Source, open))
                CancelPendingSubmenu();
            return;
        }
        if (pointerEvent.Kind != UIPointerEventKind.Wheel || GetMaximumScrollOffset() <= 0f)
            return;
        ScrollBy(-pointerEvent.WheelDelta.Y * ItemHeight);
        pointerEvent.Handled = true;
    }

    /// <summary>Scrolls constrained menu rows by a logical-pixel delta.</summary>
    /// <param name="delta">Positive values move toward later rows.</param>
    public void ScrollBy(float delta)
    {
        var resolved = Math.Clamp(_scrollOffset + delta, 0f, GetMaximumScrollOffset());
        if (resolved == _scrollOffset)
            return;
        _scrollOffset = resolved;
        InvalidateArrange();
    }

    /// <summary>Ensures one row remains visible after keyboard or type-ahead focus movement.</summary>
    /// <param name="item">Focused row, or null.</param>
    private void EnsureItemVisible(ContextMenuItem? item)
    {
        if (item is null || GetMaximumScrollOffset() <= 0f)
            return;
        var top = 2f;
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            var rowHeight = row is ContextMenuSeparator ? SeparatorHeight : ItemHeight;
            if (ReferenceEquals(row, item))
            {
                if (top < _scrollOffset)
                    _scrollOffset = top;
                else if (top + rowHeight > _scrollOffset + Height)
                    _scrollOffset = top + rowHeight - Height;
                _scrollOffset = Math.Clamp(_scrollOffset, 0f, GetMaximumScrollOffset());
                InvalidateArrange();
                return;
            }
            top += rowHeight;
        }
    }

    /// <summary>Gets the largest valid constrained row offset.</summary>
    /// <returns>Maximum vertical scroll offset.</returns>
    private float GetMaximumScrollOffset() => MathF.Max(0f, _contentHeight - Height);

    /// <summary>Checks whether a point lies in the triangular path toward the currently open submenu.</summary>
    /// <param name="point">Host-relative pointer point.</param>
    /// <returns>True when switching should receive corridor grace time.</returns>
    private bool IsInsideOpenSubmenuCorridor(Vector2 point)
    {
        if (_openSubmenuOwner?.Submenu is not { IsOpen: true } submenu)
            return false;
        var a = _corridorOrigin;
        var b = new Vector2(submenu.Left, submenu.Top);
        var c = new Vector2(submenu.Left, submenu.Bottom);
        if (IsPointInTriangle(point, a, b, c))
            return true;
        var minimumY = MathF.Min(_openSubmenuOwner.Top, submenu.Top);
        var maximumY = MathF.Max(_openSubmenuOwner.Bottom, submenu.Bottom);
        return point.X >= MathF.Min(a.X, submenu.Left) && point.X <= MathF.Max(a.X, submenu.Left) &&
            point.Y >= minimumY && point.Y <= maximumY;
    }

    /// <summary>Tests a point against a triangle using signed edge areas.</summary>
    /// <param name="point">Point to test.</param>
    /// <param name="a">First vertex.</param>
    /// <param name="b">Second vertex.</param>
    /// <param name="c">Third vertex.</param>
    /// <returns>True when the point lies inside or on an edge.</returns>
    private static bool IsPointInTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        var d1 = Cross(point, a, b);
        var d2 = Cross(point, b, c);
        var d3 = Cross(point, c, a);
        var hasNegative = d1 < 0f || d2 < 0f || d3 < 0f;
        var hasPositive = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(hasNegative && hasPositive);
    }

    /// <summary>Calculates a signed two-dimensional edge area.</summary>
    /// <param name="point">Test point.</param>
    /// <param name="a">Edge start.</param>
    /// <param name="b">Edge end.</param>
    /// <returns>Signed area.</returns>
    private static float Cross(Vector2 point, Vector2 a, Vector2 b) =>
        (point.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (point.Y - b.Y);

    /// <summary>Finds the nearest context-menu ancestor of an element.</summary>
    /// <param name="element">Starting element.</param>
    /// <returns>Ancestor menu, or null.</returns>
    private static ContextMenu? FindAncestorMenu(UIElement? element)
    {
        var current = element?.Parent;
        while (current is not null)
        {
            if (current is ContextMenu menu)
                return menu;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>Checks visual ancestry without allocating a route.</summary>
    /// <param name="element">Potential descendant.</param>
    /// <param name="ancestor">Potential ancestor.</param>
    /// <returns>True when the ancestor is reachable.</returns>
    private static bool IsDescendantOrSelf(UIElement element, UIElement ancestor)
    {
        Engine.Core.Node? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
                return true;
            current = current.Parent;
        }
        return false;
    }
}

/// <summary>Draws a non-interactive dividing line between menu groups.</summary>
public sealed class ContextMenuSeparator : UIElement
{
    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.Separator, Name, null, IsEnabled, true, false, null);

    /// <summary>Creates a fixed-size menu separator.</summary>
    /// <param name="color">Line color.</param>
    /// <param name="width">Separator width.</param>
    /// <param name="height">Separator row height.</param>
    public ContextMenuSeparator(Color color, float width, float height) : base(width, height)
    {
        ForegroundColor = color;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var y = Top + Height * 0.5f;
        drawList.AddLine(Left, y, Right, y, 1f, ForegroundColor);
    }
}

/// <summary>One clickable text row in a <see cref="ContextMenu"/>.</summary>
public sealed class ContextMenuItem : Button
{
    private readonly UITheme _theme;
    private readonly ContextMenuItemContent _presentation;

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => base.GetSemanticInfo() with
    {
        Role = UISemanticRole.MenuItem,
        Name = LabelText,
        Actions = UISemanticAction.Invoke
            | (Submenu is null ? UISemanticAction.None : UISemanticAction.ExpandCollapse),
        IsChecked = IsCheckable ? IsChecked : null,
        IsExpanded = Submenu is null ? null : Submenu.IsOpen
    };

    /// <inheritdoc/>
    public override bool PerformSemanticAction(UISemanticAction action, double? value = null)
    {
        if (action == UISemanticAction.ExpandCollapse && Submenu is not null && IsEnabled)
        {
            SubmenuRequested?.Invoke();
            return true;
        }
        return base.PerformSemanticAction(action, value);
    }

    /// <summary>Gets the displayed row label without mnemonic markup.</summary>
    public string LabelText { get; }

    /// <summary>Gets the uppercase keyboard mnemonic, if assigned.</summary>
    public char? Mnemonic { get; internal set; }

    /// <summary>Gets whether this row exposes check or radio state.</summary>
    public bool IsCheckable { get; private set; }

    /// <summary>Gets whether this stateful row is selected.</summary>
    public bool IsChecked { get; private set; }

    /// <summary>Gets the radio-group name, or null for ordinary/check rows.</summary>
    public string? RadioGroup { get; private set; }

    /// <summary>Gets the right-aligned accelerator hint.</summary>
    public string AcceleratorText => _presentation.AcceleratorText;

    /// <summary>Gets the optional retained icon element.</summary>
    public UIElement? Icon => _presentation.Icon;

    /// <summary>Gets the directly owned child menu, if any.</summary>
    public ContextMenu? Submenu { get; internal set; }

    /// <summary>Occurs when hovering or keyboard activation should display a child menu.</summary>
    public event Action? SubmenuRequested;

    /// <summary>Creates a context-menu item.</summary>
    /// <param name="width">Item width.</param>
    /// <param name="height">Item height.</param>
    /// <param name="label">Displayed label.</param>
    /// <param name="theme">Theme supplying row colors and typography.</param>
    public ContextMenuItem(float width, float height, string label, UITheme? theme = null)
        : base(width, height, label, theme ?? UITheme.Dark)
    {
        _theme = theme ?? UITheme.Dark;
        LabelText = label;
        _presentation = new ContextMenuItemContent(label, _theme);
        Content = _presentation;
        var resolvedTheme = theme ?? UITheme.Dark;
        PaddingLeft = 10f;
        NormalColor = resolvedTheme.SurfaceRaised;
        HoverColor = resolvedTheme.SurfaceHover;
        PressedColor = resolvedTheme.SurfacePressed;
        PaintNormalBackground = true;
        CornerRadius = 0f;
    }

    /// <summary>Configures this row as a check or named radio action.</summary>
    /// <param name="isChecked">Initial state.</param>
    /// <param name="groupName">Radio group, or null for an independent check action.</param>
    internal void ConfigureCheck(bool isChecked, string? groupName)
    {
        IsCheckable = true;
        RadioGroup = groupName;
        SetChecked(isChecked);
    }

    /// <summary>Updates state and its retained visual marker.</summary>
    /// <param name="isChecked">New state.</param>
    internal void SetChecked(bool isChecked)
    {
        if (!IsCheckable)
            return;
        IsChecked = isChecked;
        var marker = RadioGroup is null
            ? isChecked ? "✓ " : "  "
            : isChecked ? "● " : "  ";
        _presentation.SetPrimaryText(marker + LabelText);
        InvalidateVisual();
    }

    /// <summary>Updates the right-aligned accelerator hint.</summary>
    /// <param name="text">Hint text, or an empty string.</param>
    public void SetAcceleratorText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _presentation.SetAcceleratorText(text);
    }

    /// <summary>Sets or clears the retained icon displayed before the label.</summary>
    /// <param name="icon">Icon element, or null.</param>
    public void SetIcon(UIElement? icon) => _presentation.SetIcon(icon);

    /// <inheritdoc/>
    protected override void OnMouseEnter()
    {
        base.OnMouseEnter();
        SubmenuRequested?.Invoke();
    }
}

/// <summary>Arranges menu label and accelerator hint without constructing padded display strings.</summary>
internal sealed class ContextMenuItemContent : UIElement
{
    private readonly Label _primary;
    private readonly Label _accelerator;
    private UIElement? _icon;

    /// <summary>Gets the current accelerator hint.</summary>
    public string AcceleratorText => _accelerator.Text;

    /// <summary>Gets the current icon element.</summary>
    public UIElement? Icon => _icon;

    /// <summary>Creates a retained two-column menu row presentation.</summary>
    /// <param name="text">Primary label.</param>
    /// <param name="theme">Theme supplying typography and muted hint color.</param>
    public ContextMenuItemContent(string text, UITheme theme)
    {
        IsHitTestVisible = false;
        _primary = new Label(text)
        {
            FontSize = theme.FontSize,
            ForegroundColor = theme.TextPrimary,
            PaddingLeft = 0f,
            IsHitTestVisible = false
        };
        _accelerator = new Label(string.Empty)
        {
            FontSize = theme.CaptionFontSize,
            ForegroundColor = theme.TextMuted,
            PaddingLeft = 0f,
            IsHitTestVisible = false
        };
        AddChild(_primary);
        AddChild(_accelerator);
    }

    /// <summary>Updates the primary row text.</summary>
    /// <param name="text">New primary text.</param>
    public void SetPrimaryText(string text) => _primary.Text = text;

    /// <summary>Updates the accelerator hint.</summary>
    /// <param name="text">New hint.</param>
    public void SetAcceleratorText(string text) => _accelerator.Text = text;

    /// <summary>Replaces the retained icon element.</summary>
    /// <param name="icon">New icon, or null.</param>
    public void SetIcon(UIElement? icon)
    {
        if (ReferenceEquals(_icon, icon))
            return;
        if (_icon is not null)
            RemoveChild(_icon);
        _icon = icon;
        if (_icon is not null)
        {
            _icon.IsHitTestVisible = false;
            AddChild(_icon);
        }
        InvalidateMeasure();
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _primary.Measure(availableSize);
        _accelerator.Measure(availableSize);
        _icon?.Measure(new Vector2(16f, 16f));
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var acceleratorWidth = MathF.Min(contentSize.X, _accelerator.DesiredSize.X);
        var iconWidth = _icon is null ? 0f : 22f;
        var gap = acceleratorWidth > 0f ? 12f : 0f;
        if (_icon is not null)
            _icon.Arrange(new Vector2(0f, MathF.Max(0f, (contentSize.Y - 16f) * 0.5f)),
                new Vector2(16f, 16f));
        _primary.Arrange(new Vector2(iconWidth, 0f),
            new Vector2(MathF.Max(0f, contentSize.X - iconWidth - acceleratorWidth - gap), contentSize.Y));
        _accelerator.Arrange(new Vector2(MathF.Max(0f, contentSize.X - acceleratorWidth), 0f),
            new Vector2(acceleratorWidth, contentSize.Y));
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
    }
}
