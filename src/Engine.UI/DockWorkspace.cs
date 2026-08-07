using System.Text.Json;
using System.Text.Json.Serialization;

namespace Engine.UI;

/// <summary>Controls the axis along which two dock nodes share space.</summary>
public enum DockSplitOrientation
{
    /// <summary>Places nodes left and right.</summary>
    Horizontal,

    /// <summary>Places nodes top and bottom.</summary>
    Vertical
}

/// <summary>Identifies how a dragged panel is inserted relative to a dock target.</summary>
public enum DockDropZone
{
    /// <summary>Merges the panel into the target tab well.</summary>
    Center,

    /// <summary>Creates a panel to the target's left.</summary>
    Left,

    /// <summary>Creates a panel to the target's right.</summary>
    Right,

    /// <summary>Creates a panel above the target.</summary>
    Top,

    /// <summary>Creates a panel below the target.</summary>
    Bottom
}

/// <summary>Base model for one node in a persisted docking tree.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DockTabGroup), "tabs")]
[JsonDerivedType(typeof(DockSplit), "split")]
public abstract class DockNode
{
}

/// <summary>Identifies one dockable panel independently from its runtime UI content.</summary>
/// <param name="Id">Stable unique panel identifier.</param>
/// <param name="Title">Displayed tab title.</param>
public sealed record DockTab(string Id, string Title);

/// <summary>Stores an ordered tab well and its selected panel.</summary>
public sealed class DockTabGroup : DockNode
{
    /// <summary>Gets or sets the ordered tabs.</summary>
    public List<DockTab> Tabs { get; set; } = [];

    /// <summary>Gets or sets the selected tab identifier.</summary>
    public string? SelectedId { get; set; }

    /// <summary>Creates an empty tab group for serialization.</summary>
    public DockTabGroup()
    {
    }

    /// <summary>Creates a tab group with an initial ordered tab set.</summary>
    /// <param name="tabs">Initial tabs.</param>
    /// <param name="selectedId">Optional selected identifier.</param>
    public DockTabGroup(IEnumerable<DockTab> tabs, string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        Tabs.AddRange(tabs);
        SelectedId = selectedId;
        NormalizeSelection();
    }

    /// <summary>Adds or moves a tab to the requested position.</summary>
    /// <param name="tab">Tab to insert.</param>
    /// <param name="index">Insertion index, or -1 to append.</param>
    public void Add(DockTab tab, int index = -1)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var existing = Find(tab.Id);
        if (existing >= 0)
            Tabs.RemoveAt(existing);
        var insertion = index < 0 ? Tabs.Count : Math.Clamp(index, 0, Tabs.Count);
        Tabs.Insert(insertion, tab);
        SelectedId = tab.Id;
    }

    /// <summary>Removes one tab by identifier.</summary>
    /// <param name="id">Stable tab identifier.</param>
    /// <returns>The removed tab, or null when absent.</returns>
    public DockTab? Remove(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var index = Find(id);
        if (index < 0)
            return null;
        var removed = Tabs[index];
        Tabs.RemoveAt(index);
        NormalizeSelection();
        return removed;
    }

    /// <summary>Repairs selection after deserialization or mutation.</summary>
    internal void NormalizeSelection()
    {
        if (Tabs.Count == 0)
        {
            SelectedId = null;
            return;
        }
        if (SelectedId is null || Find(SelectedId) < 0)
            SelectedId = Tabs[0].Id;
    }

    /// <summary>Finds a tab without allocating an enumerator or comparer.</summary>
    /// <param name="id">Identifier to find.</param>
    /// <returns>Tab index, or -1.</returns>
    private int Find(string id)
    {
        for (var index = 0; index < Tabs.Count; index++)
        {
            if (string.Equals(Tabs[index].Id, id, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }
}

/// <summary>Stores two dock nodes separated by a resizable split.</summary>
public sealed class DockSplit : DockNode
{
    private float _ratio = 0.5f;

    /// <summary>Gets or sets the split axis.</summary>
    public DockSplitOrientation Orientation { get; set; }

    /// <summary>Gets or sets the first child.</summary>
    public DockNode First { get; set; } = new DockTabGroup();

    /// <summary>Gets or sets the second child.</summary>
    public DockNode Second { get; set; } = new DockTabGroup();

    /// <summary>Gets or sets the first child's normalized share.</summary>
    public float Ratio
    {
        get => _ratio;
        set => _ratio = Math.Clamp(value, 0.1f, 0.9f);
    }

    /// <summary>Creates an empty split for serialization.</summary>
    public DockSplit()
    {
    }

    /// <summary>Creates a split from two child nodes.</summary>
    /// <param name="orientation">Split axis.</param>
    /// <param name="first">First child.</param>
    /// <param name="second">Second child.</param>
    /// <param name="ratio">First child share.</param>
    public DockSplit(
        DockSplitOrientation orientation,
        DockNode first,
        DockNode second,
        float ratio = 0.5f)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        Orientation = orientation;
        First = first;
        Second = second;
        Ratio = ratio;
    }
}

/// <summary>Stores one independently hosted dock tree and logical bounds.</summary>
public sealed class FloatingDockRoot
{
    /// <summary>Gets or sets the stable floating-host identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the floating tree.</summary>
    public DockNode Root { get; set; } = new DockTabGroup();

    /// <summary>Gets or sets the logical left coordinate.</summary>
    public float Left { get; set; }

    /// <summary>Gets or sets the logical top coordinate.</summary>
    public float Top { get; set; }

    /// <summary>Gets or sets the logical width.</summary>
    public float Width { get; set; } = 640f;

    /// <summary>Gets or sets the logical height.</summary>
    public float Height { get; set; } = 480f;
}

/// <summary>Owns a versioned main dock tree and zero or more floating roots.</summary>
public sealed class DockWorkspace
{
    /// <summary>Gets the current persistence schema version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Gets or sets the serialized schema version.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Gets or sets the main-window dock tree.</summary>
    public DockNode Root { get; set; } = new DockTabGroup();

    /// <summary>Gets or sets floating dock roots.</summary>
    public List<FloatingDockRoot> FloatingRoots { get; set; } = [];

    /// <summary>Moves one panel into a target tab well.</summary>
    /// <param name="tabId">Panel identifier to move.</param>
    /// <param name="targetGroup">Destination tab well.</param>
    /// <param name="index">Destination index, or -1 to append.</param>
    /// <returns>True when the panel and target were found.</returns>
    public bool MoveTab(string tabId, DockTabGroup targetGroup, int index = -1)
    {
        ArgumentNullException.ThrowIfNull(tabId);
        ArgumentNullException.ThrowIfNull(targetGroup);
        if (Contains(targetGroup, tabId))
        {
            var existing = targetGroup.Remove(tabId);
            if (existing is null)
                return false;
            targetGroup.Add(existing, index);
            return true;
        }
        var tab = RemoveTab(tabId);
        if (tab is null || !ContainsGroup(targetGroup))
            return false;
        targetGroup.Add(tab, index);
        return true;
    }

    /// <summary>Selects the tab containing one panel across main and floating roots.</summary>
    /// <param name="tabId">Stable panel identifier.</param>
    /// <returns>True when the panel was found.</returns>
    public bool SelectTab(string tabId)
    {
        ArgumentNullException.ThrowIfNull(tabId);
        if (SelectTab(Root, tabId))
            return true;
        for (var index = 0; index < FloatingRoots.Count; index++)
        {
            if (SelectTab(FloatingRoots[index].Root, tabId))
                return true;
        }
        return false;
    }

    /// <summary>Gets whether a panel is the selected tab in its containing group.</summary>
    /// <param name="tabId">Stable panel identifier.</param>
    /// <returns>True when the panel exists and is selected.</returns>
    public bool IsTabSelected(string tabId)
    {
        ArgumentNullException.ThrowIfNull(tabId);
        if (IsTabSelected(Root, tabId))
            return true;
        for (var index = 0; index < FloatingRoots.Count; index++)
        {
            if (IsTabSelected(FloatingRoots[index].Root, tabId))
                return true;
        }
        return false;
    }

    /// <summary>Opens or selects a panel beside a stable anchor panel.</summary>
    /// <param name="tabId">Stable panel identifier.</param>
    /// <param name="title">Displayed tab title.</param>
    /// <param name="anchorId">Preferred sibling panel identifier.</param>
    /// <returns>True when an existing or fallback destination group was available.</returns>
    public bool OpenTab(string tabId, string title, string? anchorId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (SelectTab(tabId))
            return true;
        var target = anchorId is null ? null : FindGroup(Root, anchorId);
        target ??= FindFirstGroup(Root);
        if (target is null)
            return false;
        target.Add(new DockTab(tabId, title));
        return true;
    }

    /// <summary>Docks one panel at the center or edge of a main-tree tab group.</summary>
    /// <param name="tabId">Panel identifier to dock.</param>
    /// <param name="targetGroup">Target main-tree tab well.</param>
    /// <param name="zone">Drop zone.</param>
    /// <param name="newPaneShare">Normalized share assigned to a new edge pane.</param>
    /// <param name="targetIndex">Center-drop tab insertion index, or -1 to append.</param>
    /// <returns>True when the dock operation was applied.</returns>
    public bool DockTab(
        string tabId,
        DockTabGroup targetGroup,
        DockDropZone zone,
        float newPaneShare = 0.3f,
        int targetIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(tabId);
        ArgumentNullException.ThrowIfNull(targetGroup);
        if (!ContainsGroup(targetGroup))
            return false;
        if (zone == DockDropZone.Center)
            return MoveTab(tabId, targetGroup, targetIndex);
        if (Contains(targetGroup, tabId) && targetGroup.Tabs.Count == 1)
            return false;
        var tab = RemoveTab(tabId);
        if (tab is null || !ContainsGroup(targetGroup))
            return false;
        var newGroup = new DockTabGroup([tab], tab.Id);
        var share = Math.Clamp(newPaneShare, 0.1f, 0.9f);
        var horizontal = zone is DockDropZone.Left or DockDropZone.Right;
        var newFirst = zone is DockDropZone.Left or DockDropZone.Top;
        var split = new DockSplit(
            horizontal ? DockSplitOrientation.Horizontal : DockSplitOrientation.Vertical,
            newFirst ? newGroup : targetGroup,
            newFirst ? targetGroup : newGroup,
            newFirst ? share : 1f - share);
        return ReplaceGroup(targetGroup, split);
    }

    /// <summary>Removes one panel and collapses empty split branches.</summary>
    /// <param name="tabId">Panel identifier.</param>
    /// <returns>The removed tab, or null.</returns>
    public DockTab? RemoveTab(string tabId)
    {
        ArgumentNullException.ThrowIfNull(tabId);
        var root = RemoveFromNode(Root, tabId, out var removed);
        Root = root ?? new DockTabGroup();
        if (removed is not null)
            return removed;
        for (var index = FloatingRoots.Count - 1; index >= 0; index--)
        {
            root = RemoveFromNode(FloatingRoots[index].Root, tabId, out removed);
            if (root is null)
                FloatingRoots.RemoveAt(index);
            else
                FloatingRoots[index].Root = root;
            if (removed is not null)
                return removed;
        }
        return null;
    }

    /// <summary>Moves one panel into a new floating root.</summary>
    /// <param name="tabId">Panel identifier.</param>
    /// <param name="left">Logical window left.</param>
    /// <param name="top">Logical window top.</param>
    /// <param name="width">Logical window width.</param>
    /// <param name="height">Logical window height.</param>
    /// <returns>Created floating root, or null when the panel was absent.</returns>
    public FloatingDockRoot? FloatTab(
        string tabId,
        float left,
        float top,
        float width,
        float height)
    {
        var tab = RemoveTab(tabId);
        if (tab is null)
            return null;
        var floating = new FloatingDockRoot
        {
            Root = new DockTabGroup([tab], tab.Id),
            Left = left,
            Top = top,
            Width = MathF.Max(160f, width),
            Height = MathF.Max(120f, height)
        };
        FloatingRoots.Add(floating);
        return floating;
    }

    /// <summary>Merges every tab from a floating root into a target tab well.</summary>
    /// <param name="floatingIndex">Floating-root index.</param>
    /// <param name="targetGroup">Destination tab well.</param>
    public void RedockFloating(int floatingIndex, DockTabGroup targetGroup)
    {
        ArgumentNullException.ThrowIfNull(targetGroup);
        if (floatingIndex < 0 || floatingIndex >= FloatingRoots.Count)
            throw new ArgumentOutOfRangeException(nameof(floatingIndex));
        if (!ContainsGroup(Root, targetGroup))
            throw new ArgumentException(
                "Target group does not belong to the main dock tree.", nameof(targetGroup));
        var floating = FloatingRoots[floatingIndex];
        AddTabs(floating.Root, targetGroup);
        FloatingRoots.RemoveAt(floatingIndex);
    }

    /// <summary>Serializes the workspace to versioned JSON.</summary>
    /// <returns>JSON workspace document.</returns>
    public string Save()
    {
        Normalize();
        return JsonSerializer.Serialize(this, SerializerOptions);
    }

    /// <summary>Loads and validates a versioned workspace document.</summary>
    /// <param name="json">Workspace JSON.</param>
    /// <returns>Validated workspace.</returns>
    public static DockWorkspace Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var workspace = JsonSerializer.Deserialize<DockWorkspace>(json, SerializerOptions)
            ?? throw new JsonException("Workspace document was empty.");
        if (workspace.Version != CurrentVersion)
            throw new NotSupportedException($"Dock workspace version {workspace.Version} is not supported.");
        workspace.Normalize();
        return workspace;
    }

    /// <summary>Repairs selection, ratios, bounds, and duplicate identifiers.</summary>
    public void Normalize()
    {
        Version = CurrentVersion;
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        NormalizeNode(Root, identifiers);
        for (var index = FloatingRoots.Count - 1; index >= 0; index--)
        {
            var floating = FloatingRoots[index];
            if (floating.Root is null)
            {
                FloatingRoots.RemoveAt(index);
                continue;
            }
            floating.Width = MathF.Max(160f, floating.Width);
            floating.Height = MathF.Max(120f, floating.Height);
            NormalizeNode(floating.Root, identifiers);
        }
    }

    /// <summary>Validates one dock subtree recursively.</summary>
    /// <param name="node">Subtree root.</param>
    /// <param name="identifiers">Identifiers already claimed in this workspace.</param>
    private static void NormalizeNode(DockNode node, HashSet<string> identifiers)
    {
        switch (node)
        {
            case DockTabGroup group:
                for (var index = group.Tabs.Count - 1; index >= 0; index--)
                {
                    var tab = group.Tabs[index];
                    if (string.IsNullOrWhiteSpace(tab.Id) || !identifiers.Add(tab.Id))
                        group.Tabs.RemoveAt(index);
                }
                group.NormalizeSelection();
                break;
            case DockSplit split:
                split.Ratio = split.Ratio;
                NormalizeNode(split.First, identifiers);
                NormalizeNode(split.Second, identifiers);
                break;
            default:
                throw new JsonException($"Unsupported dock node type {node.GetType().Name}.");
        }
    }

    /// <summary>Removes a tab recursively and returns the collapsed subtree.</summary>
    /// <param name="node">Subtree root.</param>
    /// <param name="tabId">Panel identifier.</param>
    /// <param name="removed">Removed tab.</param>
    /// <returns>Collapsed subtree, or null when empty.</returns>
    private static DockNode? RemoveFromNode(DockNode node, string tabId, out DockTab? removed)
    {
        if (node is DockTabGroup group)
        {
            removed = group.Remove(tabId);
            return group.Tabs.Count == 0 ? null : group;
        }
        var split = (DockSplit)node;
        var first = RemoveFromNode(split.First, tabId, out removed);
        if (removed is not null)
        {
            if (first is null)
                return split.Second;
            split.First = first;
            return split;
        }
        var second = RemoveFromNode(split.Second, tabId, out removed);
        if (removed is null)
            return split;
        if (second is null)
            return split.First;
        split.Second = second;
        return split;
    }

    /// <summary>Checks whether a specific group belongs to this workspace.</summary>
    /// <param name="group">Candidate group.</param>
    /// <returns>True when found in the main or floating trees.</returns>
    private bool ContainsGroup(DockTabGroup group)
    {
        if (ContainsGroup(Root, group))
            return true;
        for (var index = 0; index < FloatingRoots.Count; index++)
        {
            if (ContainsGroup(FloatingRoots[index].Root, group))
                return true;
        }
        return false;
    }

    /// <summary>Checks one subtree for a group by reference identity.</summary>
    /// <param name="node">Subtree root.</param>
    /// <param name="group">Candidate group.</param>
    /// <returns>True when found.</returns>
    private static bool ContainsGroup(DockNode node, DockTabGroup group)
    {
        if (ReferenceEquals(node, group))
            return true;
        return node is DockSplit split &&
            (ContainsGroup(split.First, group) || ContainsGroup(split.Second, group));
    }

    /// <summary>Replaces a group in the main or floating trees by reference identity.</summary>
    /// <param name="target">Group to replace.</param>
    /// <param name="replacement">Replacement subtree.</param>
    /// <returns>True when the group still belongs to this workspace and was replaced.</returns>
    private bool ReplaceGroup(DockTabGroup target, DockNode replacement)
    {
        Root = ReplaceGroup(Root, target, replacement, out var replaced);
        if (replaced)
            return true;
        for (var index = 0; index < FloatingRoots.Count; index++)
        {
            var floating = FloatingRoots[index];
            floating.Root = ReplaceGroup(floating.Root, target, replacement, out replaced);
            if (replaced)
                return true;
        }
        return false;
    }

    /// <summary>Replaces one group by reference identity inside a dock tree.</summary>
    /// <param name="node">Current subtree.</param>
    /// <param name="target">Group to replace.</param>
    /// <param name="replacement">Replacement subtree.</param>
    /// <param name="replaced">Whether replacement occurred.</param>
    /// <returns>Updated subtree root.</returns>
    private static DockNode ReplaceGroup(
        DockNode node,
        DockTabGroup target,
        DockNode replacement,
        out bool replaced)
    {
        if (ReferenceEquals(node, target))
        {
            replaced = true;
            return replacement;
        }
        if (node is not DockSplit split)
        {
            replaced = false;
            return node;
        }
        split.First = ReplaceGroup(split.First, target, replacement, out replaced);
        if (replaced)
            return split;
        split.Second = ReplaceGroup(split.Second, target, replacement, out replaced);
        return split;
    }

    /// <summary>Checks whether a group currently contains an identifier.</summary>
    /// <param name="group">Tab group.</param>
    /// <param name="tabId">Panel identifier.</param>
    /// <returns>True when present.</returns>
    private static bool Contains(DockTabGroup group, string tabId)
    {
        for (var index = 0; index < group.Tabs.Count; index++)
        {
            if (string.Equals(group.Tabs[index].Id, tabId, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Selects a panel within one dock subtree.</summary>
    /// <param name="node">Dock subtree.</param>
    /// <param name="tabId">Stable panel identifier.</param>
    /// <returns>True when found.</returns>
    private static bool SelectTab(DockNode node, string tabId)
    {
        if (node is DockTabGroup group)
        {
            if (!Contains(group, tabId))
                return false;
            group.SelectedId = tabId;
            return true;
        }
        var split = (DockSplit)node;
        return SelectTab(split.First, tabId) || SelectTab(split.Second, tabId);
    }

    /// <summary>Checks selected state within one dock subtree.</summary>
    /// <param name="node">Dock subtree.</param>
    /// <param name="tabId">Stable panel identifier.</param>
    /// <returns>True when found selected.</returns>
    private static bool IsTabSelected(DockNode node, string tabId)
    {
        if (node is DockTabGroup group)
            return string.Equals(group.SelectedId, tabId, StringComparison.Ordinal) &&
                Contains(group, tabId);
        var split = (DockSplit)node;
        return IsTabSelected(split.First, tabId) || IsTabSelected(split.Second, tabId);
    }

    /// <summary>Finds the group containing a stable panel identifier.</summary>
    /// <param name="node">Dock subtree.</param>
    /// <param name="tabId">Stable panel identifier.</param>
    /// <returns>Containing group, or null.</returns>
    private static DockTabGroup? FindGroup(DockNode node, string tabId)
    {
        if (node is DockTabGroup group)
            return Contains(group, tabId) ? group : null;
        var split = (DockSplit)node;
        return FindGroup(split.First, tabId) ?? FindGroup(split.Second, tabId);
    }

    /// <summary>Finds the first tab well in a dock subtree.</summary>
    /// <param name="node">Dock subtree.</param>
    /// <returns>First tab group, or null.</returns>
    private static DockTabGroup? FindFirstGroup(DockNode node)
    {
        if (node is DockTabGroup group)
            return group;
        var split = (DockSplit)node;
        return FindFirstGroup(split.First) ?? FindFirstGroup(split.Second);
    }

    /// <summary>Appends every tab in one subtree to a destination group.</summary>
    /// <param name="node">Source subtree.</param>
    /// <param name="target">Destination group.</param>
    private static void AddTabs(DockNode node, DockTabGroup target)
    {
        if (node is DockTabGroup group)
        {
            for (var index = 0; index < group.Tabs.Count; index++)
                target.Add(group.Tabs[index]);
            return;
        }
        var split = (DockSplit)node;
        AddTabs(split.First, target);
        AddTabs(split.Second, target);
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true
    };
}

/// <summary>Persists dock workspaces atomically and restores a caller-provided safe default.</summary>
public static class DockWorkspaceStore
{
    /// <summary>Writes a workspace using an adjacent temporary file and atomic replacement.</summary>
    /// <param name="path">Destination JSON path.</param>
    /// <param name="workspace">Workspace to persist.</param>
    public static void Save(string path, DockWorkspace workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(workspace);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Workspace path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + ".tmp";
        File.WriteAllText(temporaryPath, workspace.Save());
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    /// <summary>Loads a workspace or returns a safe default when storage is absent or invalid.</summary>
    /// <param name="path">Workspace JSON path.</param>
    /// <param name="createDefault">Creates a new safe workspace when restoration fails.</param>
    /// <param name="error">Restoration failure when fallback was required.</param>
    /// <returns>Restored or default workspace.</returns>
    public static DockWorkspace LoadOrDefault(
        string path,
        Func<DockWorkspace> createDefault,
        out Exception? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(createDefault);
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                error = null;
                return createDefault();
            }
            var workspace = DockWorkspace.Load(File.ReadAllText(fullPath));
            error = null;
            return workspace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        {
            error = exception;
            return createDefault();
        }
    }
}
