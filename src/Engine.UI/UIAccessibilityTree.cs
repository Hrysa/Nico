using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Represents one immutable element in an accessibility-tree snapshot.</summary>
public sealed class UIAccessibilityNode
{
    /// <summary>Gets the process-stable element identifier.</summary>
    public long Id { get; }

    /// <summary>Gets the parent node index, or -1 for the root.</summary>
    public int ParentIndex { get; }

    /// <summary>Gets the first child node index, or -1 when childless.</summary>
    public int FirstChildIndex { get; internal set; } = -1;

    /// <summary>Gets the next sibling node index, or -1 for the final sibling.</summary>
    public int NextSiblingIndex { get; internal set; } = -1;

    /// <summary>Gets the element bounds in physical screen coordinates.</summary>
    public UIClipRect ScreenBounds { get; }

    /// <summary>Gets the current semantic role, value, state, and actions.</summary>
    public UISemanticInfo SemanticInfo { get; }

    /// <summary>Gets the stable author-supplied automation identifier.</summary>
    public string? AutomationId { get; }

    /// <summary>Gets the stable identifier of the labeling element, or zero.</summary>
    public long LabeledById { get; }

    /// <summary>Gets whether this element owns keyboard focus.</summary>
    public bool IsFocused { get; }

    /// <summary>Gets the retained element used to dispatch semantic actions.</summary>
    internal UIElement Element { get; }

    /// <summary>Creates one captured accessibility node.</summary>
    /// <param name="element">Source retained element.</param>
    /// <param name="parentIndex">Parent snapshot index.</param>
    /// <param name="screenBounds">Physical screen bounds.</param>
    /// <param name="semanticInfo">Current semantic information.</param>
    internal UIAccessibilityNode(
        UIElement element,
        int parentIndex,
        UIClipRect screenBounds,
        UISemanticInfo semanticInfo)
    {
        Element = element;
        Id = element.AccessibilityId;
        ParentIndex = parentIndex;
        ScreenBounds = screenBounds;
        SemanticInfo = semanticInfo with
        {
            Name = string.IsNullOrWhiteSpace(semanticInfo.Name)
                ? element.LabeledBy?.GetSemanticInfo().Name
                : semanticInfo.Name,
            Description = semanticInfo.Description ?? element.AccessibilityDescription
        };
        AutomationId = element.AutomationId;
        LabeledById = element.LabeledBy?.AccessibilityId ?? 0;
        IsFocused = element.IsFocused;
    }
}

/// <summary>Provides an immutable, index-addressable accessibility hierarchy.</summary>
public sealed class UIAccessibilitySnapshot
{
    private readonly UIAccessibilityNode[] _nodes;
    private readonly Dictionary<long, int> _indices;

    /// <summary>Gets all nodes in visual preorder.</summary>
    public IReadOnlyList<UIAccessibilityNode> Nodes => _nodes;

    /// <summary>Gets the root node.</summary>
    public UIAccessibilityNode Root => _nodes[0];

    /// <summary>Creates a captured accessibility hierarchy.</summary>
    /// <param name="nodes">Nodes in visual preorder.</param>
    internal UIAccessibilitySnapshot(UIAccessibilityNode[] nodes)
    {
        _nodes = nodes;
        _indices = new Dictionary<long, int>(nodes.Length);
        for (var index = 0; index < nodes.Length; index++)
            _indices.Add(nodes[index].Id, index);
    }

    /// <summary>Gets a node by snapshot index.</summary>
    /// <param name="index">Zero-based snapshot index.</param>
    /// <returns>The requested node.</returns>
    public UIAccessibilityNode GetNode(int index) => _nodes[index];

    /// <summary>Finds a node by its process-stable identity.</summary>
    /// <param name="id">Stable element identifier.</param>
    /// <param name="node">Matched node when present.</param>
    /// <returns>True when the element remains in this snapshot.</returns>
    public bool TryGetNode(long id, out UIAccessibilityNode? node)
    {
        if (_indices.TryGetValue(id, out var index))
        {
            node = _nodes[index];
            return true;
        }
        node = null;
        return false;
    }
}

/// <summary>Captures the visible retained tree for tests and native accessibility adapters.</summary>
public static class UIAccessibilityTree
{
    /// <summary>Captures semantic state and hierarchy using logical coordinates.</summary>
    /// <param name="root">Root retained element.</param>
    /// <returns>An immutable accessibility snapshot.</returns>
    public static UIAccessibilitySnapshot Capture(UIElement root) => Capture(root, null);

    /// <summary>Captures semantic state and maps bounds into physical screen coordinates.</summary>
    /// <param name="root">Root retained element.</param>
    /// <param name="coordinateMapper">Optional client-to-screen coordinate mapper.</param>
    /// <returns>An immutable accessibility snapshot.</returns>
    public static UIAccessibilitySnapshot Capture(
        UIElement root,
        IWindowCoordinateMapper? coordinateMapper)
    {
        ArgumentNullException.ThrowIfNull(root);
        var nodes = new List<UIAccessibilityNode>();
        CaptureElement(root, -1, coordinateMapper, nodes);
        if (nodes.Count == 0)
            throw new InvalidOperationException("An accessibility root must be visible.");
        return new UIAccessibilitySnapshot(nodes.ToArray());
    }

    /// <summary>Appends one visible subtree in visual preorder.</summary>
    /// <param name="element">Current retained element.</param>
    /// <param name="parentIndex">Parent snapshot index.</param>
    /// <param name="coordinateMapper">Optional screen-coordinate mapper.</param>
    /// <param name="nodes">Snapshot nodes being constructed.</param>
    /// <returns>The appended node index, or -1 for a hidden subtree.</returns>
    private static int CaptureElement(
        UIElement element,
        int parentIndex,
        IWindowCoordinateMapper? coordinateMapper,
        List<UIAccessibilityNode> nodes)
    {
        if (!element.IsVisible)
            return -1;

        var topLeft = new Vector2(element.Left, element.Top);
        var bottomRight = new Vector2(element.Right, element.Bottom);
        if (coordinateMapper is not null)
        {
            topLeft = coordinateMapper.ClientToScreen(topLeft);
            bottomRight = coordinateMapper.ClientToScreen(bottomRight);
        }
        var index = nodes.Count;
        var semantic = element.GetSemanticInfo();
        nodes.Add(new UIAccessibilityNode(
            element,
            parentIndex,
            new UIClipRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y),
            semantic));

        var previousChildIndex = -1;
        var children = element.VisualChildren;
        for (var childIndex = 0; childIndex < children.Count; childIndex++)
        {
            if (children[childIndex] is not UIElement child)
                continue;
            var capturedChildIndex = CaptureElement(child, index, coordinateMapper, nodes);
            if (capturedChildIndex < 0)
                continue;
            if (nodes[index].FirstChildIndex < 0)
                nodes[index].FirstChildIndex = capturedChildIndex;
            if (previousChildIndex >= 0)
                nodes[previousChildIndex].NextSiblingIndex = capturedChildIndex;
            previousChildIndex = capturedChildIndex;
        }
        return index;
    }
}
