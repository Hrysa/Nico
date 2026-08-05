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

/// <summary>Defines one reusable data column in a <see cref="TreeView"/>.</summary>
public sealed class TreeViewColumn
{
    /// <summary>Creates a tree-view column.</summary>
    /// <param name="header">Column header text.</param>
    /// <param name="width">Fixed width, or zero to consume remaining width.</param>
    /// <param name="value">Formats the cell value for a node.</param>
    /// <param name="alignment">Cell and header text alignment.</param>
    public TreeViewColumn(
        string header,
        float width,
        Func<Node, string> value,
        TreeViewColumnAlignment alignment = TreeViewColumnAlignment.Left)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(value);
        Header = header;
        Width = MathF.Max(0f, width);
        Value = value;
        Alignment = alignment;
    }

    /// <summary>Gets the column header text.</summary>
    public string Header { get; }

    /// <summary>Gets the fixed width, or zero when the column consumes remaining width.</summary>
    public float Width { get; }

    /// <summary>Gets the node-to-cell formatter.</summary>
    public Func<Node, string> Value { get; }

    /// <summary>Gets the cell and header text alignment.</summary>
    public TreeViewColumnAlignment Alignment { get; }
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
        var fixedWidth = columns.Sum(candidate => candidate.Width);
        var flexibleCount = Math.Max(1, columns.Count(candidate => candidate.Width <= 0f));
        return MathF.Max(0f, availableWidth - fixedWidth) / flexibleCount;
    }
}
