using Engine.Core;

namespace Engine.UI;

/// <summary>Controls horizontal text placement inside a tree-view column.</summary>
public enum TreeViewColumnAlignment
{
    /// <summary>Places text against the column's leading edge.</summary>
    Left,

    /// <summary>Places text against the column's trailing edge.</summary>
    Right
}

/// <summary>Selects display ordering for a sortable tree-view column.</summary>
public enum TreeViewSortDirection
{
    /// <summary>Preserves authored hierarchy order.</summary>
    None,

    /// <summary>Orders lower values before higher values.</summary>
    Ascending,

    /// <summary>Orders higher values before lower values.</summary>
    Descending
}

/// <summary>Defines one reusable data column in a <see cref="TreeView"/>.</summary>
public sealed class TreeViewColumn
{
    /// <summary>Creates a tree-view column.</summary>
    /// <param name="header">Column header text.</param>
    /// <param name="width">Fixed width, or zero to consume remaining width.</param>
    /// <param name="value">Formats the cell value for a node.</param>
    /// <param name="alignment">Cell and header text alignment.</param>
    /// <param name="sortComparison">Optional allocation-free node comparison.</param>
    /// <param name="canResize">Whether pointer divider dragging may set an explicit width.</param>
    public TreeViewColumn(
        string header,
        float width,
        Func<Node, string> value,
        TreeViewColumnAlignment alignment = TreeViewColumnAlignment.Left,
        Comparison<Node>? sortComparison = null,
        bool canResize = true)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(value);
        Header = header;
        Width = MathF.Max(0f, width);
        Value = value;
        Alignment = alignment;
        SortComparison = sortComparison;
        CanResize = canResize;
    }

    /// <summary>Gets the column header text.</summary>
    public string Header { get; }

    /// <summary>Gets the fixed width, or zero when the column consumes remaining width.</summary>
    public float Width { get; private set; }

    /// <summary>Gets or sets the minimum explicit resized width.</summary>
    public float MinWidth { get; set; } = 24f;

    /// <summary>Gets or sets the maximum explicit resized width.</summary>
    public float MaxWidth { get; set; } = 4096f;

    /// <summary>Gets whether the column permits pointer resizing.</summary>
    public bool CanResize { get; }

    /// <summary>Gets the node-to-cell formatter.</summary>
    public Func<Node, string> Value { get; }

    /// <summary>Gets the cell and header text alignment.</summary>
    public TreeViewColumnAlignment Alignment { get; }

    /// <summary>Gets the optional node comparison used for hierarchical sorting.</summary>
    public Comparison<Node>? SortComparison { get; }

    /// <summary>Sets a clamped explicit width after a column-resize gesture.</summary>
    /// <param name="width">Requested logical width.</param>
    /// <returns>True when the resolved width changed.</returns>
    internal bool Resize(float width)
    {
        var minimum = MathF.Max(0f, MinWidth);
        var maximum = MathF.Max(minimum, MaxWidth);
        var resolved = Math.Clamp(width, minimum, maximum);
        if (Width == resolved)
            return false;
        Width = resolved;
        return true;
    }
}

/// <summary>Calculates fixed and flexible tree-view column widths.</summary>
internal static class TreeViewColumnLayout
{
    /// <summary>Resolves one column's final width.</summary>
    /// <param name="columns">Configured columns.</param>
    /// <param name="column">Column being resolved.</param>
    /// <param name="availableWidth">Total available row width.</param>
    /// <returns>Final nonnegative column width.</returns>
    internal static float ResolveWidth(
        IReadOnlyList<TreeViewColumn> columns,
        TreeViewColumn column,
        float availableWidth)
    {
        if (column.Width > 0f)
            return column.Width;
        var fixedWidth = 0f;
        var flexibleCount = 0;
        for (var index = 0; index < columns.Count; index++)
        {
            var candidate = columns[index];
            fixedWidth += candidate.Width;
            if (candidate.Width <= 0f)
                flexibleCount++;
        }
        flexibleCount = Math.Max(1, flexibleCount);
        return MathF.Max(0f, availableWidth - fixedWidth) / flexibleCount;
    }
}
