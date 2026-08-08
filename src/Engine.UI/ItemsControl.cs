using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Selects how a collection control retains item selection.</summary>
public enum UISelectionMode
{
    /// <summary>Retains at most one selected item.</summary>
    Single,

    /// <summary>Toggles independent items without requiring modifiers.</summary>
    Multiple,

    /// <summary>Supports replace, modifier-toggle, and anchored range selection.</summary>
    Extended
}

/// <summary>Describes one selection operation independent of an input device.</summary>
public enum UISelectionIntent
{
    /// <summary>Replaces the complete selection.</summary>
    Replace,

    /// <summary>Toggles one index.</summary>
    Toggle,

    /// <summary>Replaces selection with the anchor-to-index range.</summary>
    Range,

    /// <summary>Adds the anchor-to-index range to existing selection.</summary>
    AddRange
}

/// <summary>Owns sorted stable indices for single, multiple, and range selection.</summary>
public sealed class UISelectionModel
{
    private readonly List<int> _indices = [];

    /// <summary>Gets the selected indices in ascending order.</summary>
    public IReadOnlyList<int> SelectedIndices => _indices;

    /// <summary>Gets the most recently targeted selected index, or -1.</summary>
    public int PrimaryIndex { get; private set; } = -1;

    /// <summary>Gets the current range anchor, or -1.</summary>
    public int AnchorIndex { get; private set; } = -1;

    /// <summary>Gets the number of selected indices.</summary>
    public int Count => _indices.Count;

    /// <summary>Tests whether one logical index is selected.</summary>
    /// <param name="index">Logical item index.</param>
    /// <returns>True when selected.</returns>
    public bool IsSelected(int index) => FindIndex(index) >= 0;

    /// <summary>Applies one bounded selection operation.</summary>
    /// <param name="index">Logical index, or -1 to clear.</param>
    /// <param name="itemCount">Current logical item count.</param>
    /// <param name="mode">Collection selection mode.</param>
    /// <param name="intent">Requested replace, toggle, or range operation.</param>
    /// <returns>True when selected state changed.</returns>
    public bool Select(int index, int itemCount, UISelectionMode mode, UISelectionIntent intent)
    {
        if (index < -1 || index >= itemCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (index < 0)
            return Clear();
        if (mode == UISelectionMode.Single)
            intent = UISelectionIntent.Replace;

        var changed = intent switch
        {
            UISelectionIntent.Toggle => Toggle(index),
            UISelectionIntent.Range => ReplaceRange(ResolveAnchor(index), index),
            UISelectionIntent.AddRange => AddRange(ResolveAnchor(index), index),
            _ => Replace(index)
        };
        PrimaryIndex = intent == UISelectionIntent.Toggle && !IsSelected(index)
            ? _indices.Count == 0 ? -1 : _indices[^1]
            : index;
        if (intent is UISelectionIntent.Replace or UISelectionIntent.Toggle || AnchorIndex < 0)
            AnchorIndex = index;
        return changed;
    }

    /// <summary>Clears selection and range ownership.</summary>
    /// <returns>True when selection was nonempty.</returns>
    public bool Clear()
    {
        if (_indices.Count == 0 && PrimaryIndex < 0 && AnchorIndex < 0)
            return false;
        _indices.Clear();
        PrimaryIndex = -1;
        AnchorIndex = -1;
        return true;
    }

    /// <summary>Removes indices no longer valid after collection shrinkage.</summary>
    /// <param name="itemCount">Current logical item count.</param>
    /// <returns>True when selection changed.</returns>
    public bool Trim(int itemCount)
    {
        itemCount = Math.Max(0, itemCount);
        var changed = false;
        for (var index = _indices.Count - 1; index >= 0; index--)
        {
            if (_indices[index] < itemCount)
                continue;
            _indices.RemoveAt(index);
            changed = true;
        }
        if (PrimaryIndex >= itemCount)
        {
            PrimaryIndex = _indices.Count == 0 ? -1 : _indices[^1];
            changed = true;
        }
        if (AnchorIndex >= itemCount)
            AnchorIndex = PrimaryIndex;
        return changed;
    }

    /// <summary>Replaces selection with one index.</summary>
    /// <param name="index">Index to retain.</param>
    /// <returns>True when state changed.</returns>
    private bool Replace(int index)
    {
        if (_indices.Count == 1 && _indices[0] == index)
            return false;
        _indices.Clear();
        _indices.Add(index);
        return true;
    }

    /// <summary>Toggles one sorted index.</summary>
    /// <param name="index">Index to toggle.</param>
    /// <returns>True because toggle always changes state.</returns>
    private bool Toggle(int index)
    {
        var position = FindIndex(index);
        if (position >= 0)
            _indices.RemoveAt(position);
        else
            _indices.Insert(~position, index);
        return true;
    }

    /// <summary>Replaces selection with an inclusive index range.</summary>
    /// <param name="first">First range endpoint.</param>
    /// <param name="second">Second range endpoint.</param>
    /// <returns>True when state changed.</returns>
    private bool ReplaceRange(int first, int second)
    {
        var minimum = Math.Min(first, second);
        var maximum = Math.Max(first, second);
        if (_indices.Count == maximum - minimum + 1
            && _indices.Count > 0 && _indices[0] == minimum && _indices[^1] == maximum)
            return false;
        _indices.Clear();
        for (var index = minimum; index <= maximum; index++)
            _indices.Add(index);
        return true;
    }

    /// <summary>Adds an inclusive range while preserving sorted uniqueness.</summary>
    /// <param name="first">First range endpoint.</param>
    /// <param name="second">Second range endpoint.</param>
    /// <returns>True when any index was added.</returns>
    private bool AddRange(int first, int second)
    {
        var changed = false;
        var minimum = Math.Min(first, second);
        var maximum = Math.Max(first, second);
        for (var index = minimum; index <= maximum; index++)
        {
            var position = FindIndex(index);
            if (position >= 0)
                continue;
            _indices.Insert(~position, index);
            changed = true;
        }
        return changed;
    }

    /// <summary>Resolves a range anchor for an unanchored model.</summary>
    /// <param name="fallback">Fallback index.</param>
    /// <returns>Existing anchor or fallback.</returns>
    private int ResolveAnchor(int fallback) => AnchorIndex >= 0 ? AnchorIndex : fallback;

    /// <summary>Finds one index using allocation-free binary search.</summary>
    /// <param name="index">Index to locate.</param>
    /// <returns>Position or bitwise-complement insertion position.</returns>
    private int FindIndex(int index)
    {
        var low = 0;
        var high = _indices.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var candidate = _indices[middle];
            if (candidate == index)
                return middle;
            if (candidate < index)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return ~low;
    }
}

/// <summary>Generates retained containers for a typed logical item collection.</summary>
/// <typeparam name="TItem">Application item type.</typeparam>
public class ItemsControl<TItem> : Control
{
    private readonly List<TItem> _items = [];
    private readonly List<UIElement> _containers = [];
    private IUIDataTemplate? _itemTemplate;

    /// <summary>Gets logical items in display order.</summary>
    public IReadOnlyList<TItem> Items => _items;

    /// <summary>Gets generated retained containers in display order.</summary>
    public IReadOnlyList<UIElement> Containers => _containers;

    /// <summary>Gets the vertical presenter owning generated containers.</summary>
    protected StackPanel ItemsPresenter { get; }

    /// <summary>Gets or sets the typed item presentation factory.</summary>
    public IUIDataTemplate? ItemTemplate
    {
        get => _itemTemplate;
        set
        {
            if (value is not null && !value.DataType.IsAssignableFrom(typeof(TItem)))
                throw new ArgumentException(
                    $"Template for {value.DataType.Name} cannot present {typeof(TItem).Name}.",
                    nameof(value));
            if (ReferenceEquals(_itemTemplate, value))
                return;
            _itemTemplate = value;
            RebuildContainers();
        }
    }

    /// <summary>Creates an empty typed item control.</summary>
    /// <param name="spacing">Vertical spacing between generated containers.</param>
    public ItemsControl(float spacing = 0f)
    {
        ItemsPresenter = new StackPanel(0f, 0f)
        {
            Spacing = spacing
        };
        AddVisualChild(ItemsPresenter);
    }

    /// <summary>Replaces logical items and regenerates retained containers.</summary>
    /// <param name="items">New logical items.</param>
    public virtual void SetItems(IEnumerable<TItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.Clear();
        _items.AddRange(items);
        RebuildContainers();
    }

    /// <summary>Creates retained content for one logical item.</summary>
    /// <param name="item">Logical item.</param>
    /// <returns>New unparented content.</returns>
    protected virtual UIElement CreateItemContent(TItem item)
    {
        if (_itemTemplate is not null)
            return _itemTemplate.Build(item!);
        return new Label(item is null ? string.Empty : item.ToString() ?? string.Empty);
    }

    /// <summary>Creates the retained container for one item and index.</summary>
    /// <param name="item">Logical item.</param>
    /// <param name="index">Logical index.</param>
    /// <returns>New unparented retained container.</returns>
    protected virtual UIElement CreateContainer(TItem item, int index) => CreateItemContent(item);

    /// <summary>Regenerates owned item containers and disposes bindings from discarded visuals.</summary>
    protected void RebuildContainers()
    {
        for (var index = 0; index < _containers.Count; index++)
        {
            _containers[index].CancelAnimationsRecursive();
            _containers[index].DisposeBindingsRecursive();
            RemoveLogicalChild(_containers[index]);
        }
        ItemsPresenter.ClearChildren();
        _containers.Clear();
        for (var index = 0; index < _items.Count; index++)
        {
            var container = CreateContainer(_items[index], index);
            if (container.Parent is not null)
                throw new InvalidOperationException("An item container factory returned a parented element.");
            container.DataContext = _items[index];
            _containers.Add(container);
            AddLogicalChild(container);
            ItemsPresenter.AddVisualChild(container);
        }
        InvalidateMeasure();
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        ItemsPresenter.Measure(availableSize);
        return ItemsPresenter.DesiredSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize) =>
        ItemsPresenter.Arrange(Vector2.Zero, contentSize);
}

/// <summary>Adds reusable selected-index ownership and type-ahead to a typed item control.</summary>
/// <typeparam name="TItem">Application item type.</typeparam>
public class Selector<TItem> : ItemsControl<TItem>
{
    private string _typeAhead = string.Empty;
    private double _typeAheadElapsed;

    /// <summary>Gets the shared sorted selection state.</summary>
    public UISelectionModel Selection { get; } = new();

    /// <summary>Gets or sets selection modifier behavior.</summary>
    public UISelectionMode SelectionMode { get; set; } = UISelectionMode.Single;

    /// <summary>Gets or sets text used for type-ahead matching.</summary>
    public Func<TItem, string>? ItemText { get; set; }

    /// <summary>Gets the primary selected index, or -1.</summary>
    public int SelectedIndex => Selection.PrimaryIndex;

    /// <summary>Gets the primary selected item, or the default value.</summary>
    public TItem? SelectedItem => SelectedIndex >= 0 ? Items[SelectedIndex] : default;

    /// <summary>Occurs after the selected index set changes.</summary>
    public event Action<UISelectionModel>? SelectionChanged;

    /// <summary>Creates an empty typed selector.</summary>
    /// <param name="spacing">Vertical spacing between generated containers.</param>
    public Selector(float spacing = 0f) : base(spacing)
    {
        RoutedTextInput += OnTextInput;
        Key += OnKey;
    }

    /// <inheritdoc/>
    public override void SetItems(IEnumerable<TItem> items)
    {
        Selection.Clear();
        base.SetItems(items);
        SelectionChanged?.Invoke(Selection);
    }

    /// <summary>Selects one logical index with an explicit device-neutral intent.</summary>
    /// <param name="index">Logical index, or -1 to clear.</param>
    /// <param name="intent">Replace, toggle, or anchored range intent.</param>
    public void Select(int index, UISelectionIntent intent = UISelectionIntent.Replace)
    {
        if (!Selection.Select(index, Items.Count, SelectionMode, intent))
            return;
        RefreshContainerSelection();
        SelectionChanged?.Invoke(Selection);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override UIElement CreateContainer(TItem item, int index)
    {
        var row = new SelectorItem(CreateItemContent(item), index);
        row.SelectionRequested += SelectFromInput;
        return row;
    }

    /// <summary>Maps routed modifiers into the current selection mode.</summary>
    /// <param name="index">Logical item index.</param>
    /// <param name="modifiers">Device-neutral held modifiers.</param>
    private void SelectFromInput(int index, InputModifiers modifiers)
    {
        var toggle = (modifiers & (InputModifiers.Control | InputModifiers.Super)) != 0;
        var range = (modifiers & InputModifiers.Shift) != 0;
        var intent = range
            ? toggle ? UISelectionIntent.AddRange : UISelectionIntent.Range
            : toggle || SelectionMode == UISelectionMode.Multiple
                ? UISelectionIntent.Toggle
                : UISelectionIntent.Replace;
        Select(index, intent);
    }

    /// <summary>Handles focused-row arrows and range modifiers.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="keyEvent">Routed key transition.</param>
    private void OnKey(UIElement sender, UIKeyEventArgs keyEvent)
    {
        if (keyEvent.Kind != UIKeyEventKind.KeyDown || keyEvent.RoutePhase != UIRoutePhase.Bubble
            || Items.Count == 0)
            return;
        var index = keyEvent.Key switch
        {
            InputKey.Up => Math.Max(0, SelectedIndex < 0 ? 0 : SelectedIndex - 1),
            InputKey.Down => Math.Min(Items.Count - 1, SelectedIndex + 1),
            InputKey.Home => 0,
            InputKey.End => Items.Count - 1,
            _ => -1
        };
        if (index < 0)
            return;
        SelectFromInput(index, keyEvent.Modifiers);
        keyEvent.Focus(Containers[index]);
        keyEvent.Handled = true;
    }

    /// <summary>Matches committed text against item display text using inherited culture.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="textEvent">Committed text input.</param>
    private void OnTextInput(UIElement sender, UITextInputEventArgs textEvent)
    {
        if (textEvent.Text.Length == 0 || Items.Count == 0)
            return;
        var repeated = _typeAhead.Length == 1 && textEvent.Text.Length == 1
            && Culture.CompareInfo.Compare(_typeAhead, textEvent.Text,
                System.Globalization.CompareOptions.IgnoreCase) == 0;
        _typeAhead = repeated ? textEvent.Text : _typeAhead + textEvent.Text;
        _typeAheadElapsed = 0d;
        var start = Math.Max(-1, SelectedIndex);
        for (var offset = 1; offset <= Items.Count; offset++)
        {
            var index = (start + offset) % Items.Count;
            var item = Items[index];
            var text = ItemText?.Invoke(item)
                ?? (item is null ? string.Empty : item.ToString() ?? string.Empty);
            if (!Culture.CompareInfo.IsPrefix(text, _typeAhead,
                    System.Globalization.CompareOptions.IgnoreCase
                    | System.Globalization.CompareOptions.IgnoreNonSpace))
                continue;
            Select(index);
            textEvent.Focus(Containers[index]);
            textEvent.Handled = true;
            return;
        }
    }

    /// <summary>Refreshes generated row selected state.</summary>
    private void RefreshContainerSelection()
    {
        for (var index = 0; index < Containers.Count; index++)
        {
            if (Containers[index] is SelectorItem row)
                row.IsChecked = Selection.IsSelected(index);
        }
    }

    /// <inheritdoc/>
    protected override bool UpdateElement(double deltaTime)
    {
        if (_typeAhead.Length == 0 || deltaTime <= 0d)
            return false;
        _typeAheadElapsed += deltaTime;
        if (_typeAheadElapsed < 0.75d)
            return false;
        _typeAhead = string.Empty;
        _typeAheadElapsed = 0d;
        return false;
    }

    /// <inheritdoc/>
    protected override bool IsTimeUpdateActive => _typeAhead.Length > 0;

    /// <summary>Generated selectable row retaining logical index and input modifiers.</summary>
    private sealed class SelectorItem : ToggleButton
    {
        private InputModifiers _selectionModifiers;

        /// <summary>Gets the logical item index represented by this row.</summary>
        public int ItemIndex { get; }

        /// <summary>Occurs when pointer or semantic invocation requests selection.</summary>
        public event Action<int, InputModifiers>? SelectionRequested;

        /// <summary>Creates a generated row around item content.</summary>
        /// <param name="content">Retained item presentation.</param>
        /// <param name="itemIndex">Logical item index.</param>
        public SelectorItem(UIElement content, int itemIndex)
            : base(0f, UITheme.Dark.ControlHeight, string.Empty)
        {
            ItemIndex = itemIndex;
            Content = content;
            Pointer += CaptureModifiers;
        }

        /// <summary>Selects this row for a routed release over its presentation subtree.</summary>
        /// <param name="sender">Current routed receiver.</param>
        /// <param name="pointerEvent">Routed pointer transition.</param>
        private void CaptureModifiers(UIElement sender, UIPointerEventArgs pointerEvent)
        {
            if (pointerEvent.Kind != UIPointerEventKind.Release)
                return;
            _selectionModifiers = pointerEvent.Modifiers;
            SelectionRequested?.Invoke(ItemIndex, _selectionModifiers);
            _selectionModifiers = InputModifiers.None;
            pointerEvent.Handled = true;
        }

        /// <inheritdoc/>
        protected override void ApplyClickState()
        {
            SelectionRequested?.Invoke(ItemIndex, _selectionModifiers);
            _selectionModifiers = InputModifiers.None;
        }
    }
}
