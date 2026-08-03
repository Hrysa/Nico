using Engine.UI;

namespace Editor;

/// <summary>
/// Project-scoped modal dialog for searching and opening scene files.
/// </summary>
public sealed class ScenePickerDialog : Modal
{
    private readonly string _projectRoot;
    private readonly IReadOnlyList<string> _scenePaths;
    private readonly TextField _searchField;
    private readonly ListView _sceneList;
    private readonly Button _openButton;
    private List<string> _visiblePaths = [];
    private string? _selectedPath;

    /// <summary>Occurs when the user confirms a scene path.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>Occurs when the user cancels the dialog.</summary>
    public event Action? CancelRequested;

    /// <summary>
    /// Creates a project-scoped scene picker.
    /// </summary>
    /// <param name="width">Editor window width.</param>
    /// <param name="height">Editor window height.</param>
    /// <param name="projectRoot">Absolute game-project root.</param>
    /// <param name="scenePaths">Absolute paths available for selection.</param>
    /// <param name="theme">Theme supplying dialog visuals.</param>
    public ScenePickerDialog(
        float width,
        float height,
        string projectRoot,
        IReadOnlyList<string> scenePaths,
        UITheme? theme = null)
        : base(width, height, MathF.Min(640f, width - 48f), MathF.Min(520f, height - 48f), theme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(scenePaths);
        var resolvedTheme = theme ?? UITheme.Dark;
        _projectRoot = projectRoot;
        _scenePaths = scenePaths;

        var content = new Canvas();
        Dialog.AddChild(content);
        content.Add(new DialogHeader("Open Scene",
            "Choose a scene from this game project", resolvedTheme), System.Numerics.Vector2.Zero);
        _searchField = new TextField(Dialog.Width - 32f, 34f, resolvedTheme)
        {
            Name = "SceneSearch",
            Placeholder = "Search scenes"
        };
        _sceneList = new ListView(Dialog.Width - 32f,
            MathF.Max(80f, Dialog.Height - 190f), resolvedTheme) { Name = "SceneList" };
        _openButton = new Button(34f, "Open", resolvedTheme, ButtonStyle.Primary) { Name = "Open" };
        var cancelButton = new Button(34f, "Cancel", resolvedTheme) { Name = "Cancel" };
        var openPosition = new System.Numerics.Vector2(
            Dialog.Width - _openButton.Width - 16f, Dialog.Height - 50f);
        var cancelPosition = new System.Numerics.Vector2(
            openPosition.X - cancelButton.Width - 8f, Dialog.Height - 50f);

        _searchField.TextChanged += ApplyFilter;
        _sceneList.SelectionChanged += SelectVisiblePath;
        _sceneList.ItemActivated += (_, _) => ConfirmSelection();
        cancelButton.Click += RequestCancel;
        _openButton.Click += ConfirmSelection;
        DismissRequested += RequestCancel;

        content.Add(_searchField, new System.Numerics.Vector2(16f, 78f));
        content.Add(_sceneList, new System.Numerics.Vector2(16f, 124f));
        content.Add(cancelButton, cancelPosition);
        content.Add(_openButton, openPosition);
        ApplyFilter(string.Empty);
    }

    /// <summary>Filters available scene paths by project-relative name.</summary>
    /// <param name="query">Case-insensitive search query.</param>
    private void ApplyFilter(string query)
    {
        _visiblePaths = _scenePaths
            .Where(path => Path.GetRelativePath(_projectRoot, path)
                .Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _selectedPath = null;
        _sceneList.SetItems(_visiblePaths.Select(path => Path.GetRelativePath(_projectRoot, path)));
    }

    /// <summary>Maps a visible row selection back to its absolute scene path.</summary>
    /// <param name="index">Visible row index.</param>
    /// <param name="label">Selected relative label.</param>
    private void SelectVisiblePath(int index, string? label)
    {
        _selectedPath = index >= 0 && index < _visiblePaths.Count ? _visiblePaths[index] : null;
    }

    /// <summary>Confirms the currently selected scene when available.</summary>
    private void ConfirmSelection()
    {
        if (_selectedPath is not null)
            OpenRequested?.Invoke(_selectedPath);
    }

    /// <summary>Requests that the picker close without opening a scene.</summary>
    private void RequestCancel()
    {
        CancelRequested?.Invoke();
    }
}
