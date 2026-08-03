using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Arranges children in fixed and proportional rows and columns.</summary>
public sealed class Grid : Panel
{
    private readonly Dictionary<UIElement, (int Row, int Column, int RowSpan, int ColumnSpan)> _cells = new();

    /// <summary>Gets the column definitions.</summary>
    public IList<GridLength> Columns { get; } = new List<GridLength>();

    /// <summary>Gets the row definitions.</summary>
    public IList<GridLength> Rows { get; } = new List<GridLength>();

    /// <summary>Creates an empty grid.</summary>
    /// <param name="backgroundColor">Grid background color.</param>
    public Grid(Color backgroundColor) : base(backgroundColor)
    {
    }

    /// <summary>Adds a child at one grid cell.</summary>
    /// <param name="child">Element to add.</param>
    /// <param name="row">Zero-based row index.</param>
    /// <param name="column">Zero-based column index.</param>
    /// <param name="rowSpan">Number of rows occupied.</param>
    /// <param name="columnSpan">Number of columns occupied.</param>
    public void Add(UIElement child, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (rowSpan <= 0 || columnSpan <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowSpan), "Grid spans must be positive.");
        _cells[child] = (row, column, rowSpan, columnSpan);
        AddChild(child);
    }

    /// <summary>Removes a child and its grid placement.</summary>
    /// <param name="child">Element to remove.</param>
    /// <returns>True when the child was present.</returns>
    public bool Remove(UIElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _cells.Remove(child);
        return RemoveChild(child);
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var inner = new Vector2(
            MathF.Max(0f, availableSize.X - Padding.Horizontal),
            MathF.Max(0f, availableSize.Y - Padding.Vertical));
        foreach (var child in Children.OfType<UIElement>())
            child.Measure(inner);
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var columns = ResolveTracks(Columns, contentSize.X);
        var rows = ResolveTracks(Rows, contentSize.Y);
        foreach (var child in Children.OfType<UIElement>())
        {
            if (!_cells.TryGetValue(child, out var cell))
                throw new InvalidOperationException(
                    $"Grid child '{child.Name}' must be added through Grid.Add().");
            if (cell.Column < 0 || cell.Column >= columns.Length ||
                cell.Row < 0 || cell.Row >= rows.Length)
                continue;
            child.Arrange(new Vector2(
                Padding.Left + SumBefore(columns, cell.Column),
                Padding.Top + SumBefore(rows, cell.Row)),
                new Vector2(SumRange(columns, cell.Column, cell.ColumnSpan),
                    SumRange(rows, cell.Row, cell.RowSpan)));
        }
    }

    /// <summary>Resolves track policies against one available axis.</summary>
    /// <param name="definitions">Track definitions.</param>
    /// <param name="available">Available axis size.</param>
    /// <returns>Resolved track sizes.</returns>
    private static float[] ResolveTracks(IList<GridLength> definitions, float available)
    {
        if (definitions.Count == 0)
            return [MathF.Max(0f, available)];
        var fixedSize = definitions.Where(x => x.UnitType == GridUnitType.Pixel)
            .Sum(x => MathF.Max(0f, x.Value));
        var totalWeight = definitions.Where(x => x.UnitType == GridUnitType.Star)
            .Sum(x => MathF.Max(0f, x.Value));
        var remaining = MathF.Max(0f, available - fixedSize);
        return definitions.Select(x => x.UnitType == GridUnitType.Pixel
            ? MathF.Max(0f, x.Value)
            : totalWeight > 0f ? remaining * MathF.Max(0f, x.Value) / totalWeight : 0f).ToArray();
    }

    /// <summary>Sums all track sizes preceding an index.</summary>
    /// <param name="tracks">Resolved track sizes.</param>
    /// <param name="index">Track index.</param>
    /// <returns>Offset of the selected track.</returns>
    private static float SumBefore(float[] tracks, int index)
    {
        var result = 0f;
        for (var i = 0; i < index; i++)
            result += tracks[i];
        return result;
    }

    /// <summary>Sums a consecutive range of resolved tracks.</summary>
    /// <param name="tracks">Resolved track sizes.</param>
    /// <param name="start">First track index.</param>
    /// <param name="count">Number of tracks.</param>
    /// <returns>Combined track size.</returns>
    private static float SumRange(float[] tracks, int start, int count)
    {
        var result = 0f;
        var end = Math.Min(tracks.Length, start + count);
        for (var index = start; index < end; index++)
            result += tracks[index];
        return result;
    }
}
