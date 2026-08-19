using Engine.UI;

namespace Editor;

/// <summary>Collects a name for a new filesystem folder or file.</summary>
public sealed class FileSystemCreateDialog : Modal
{
    private readonly TextField _nameField;
    private readonly Label _errorLabel;

    /// <summary>Occurs when the user confirms a non-empty leaf name.</summary>
    public event Action<string>? CreateRequested;

    /// <summary>Occurs when the user cancels the dialog.</summary>
    public event Action? CancelRequested;

    /// <summary>Creates a filesystem item naming dialog.</summary>
    /// <param name="width">Editor window width.</param>
    /// <param name="height">Editor window height.</param>
    /// <param name="itemKind">Human-readable item kind, such as Folder or File.</param>
    /// <param name="parentPath">Project-relative destination directory.</param>
    /// <param name="actionVerb">Dialog action verb, such as Add or Save.</param>
    /// <param name="theme">Theme supplying dialog visuals.</param>
    public FileSystemCreateDialog(
        float width,
        float height,
        string itemKind,
        string parentPath,
        string actionVerb = "Add",
        UITheme? theme = null)
        : base(width, height, MathF.Min(480f, width - 48f), 238f, theme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemKind);
        var resolvedTheme = theme ?? UITheme.Dark;
        var content = new Canvas();
        Dialog.AddChild(content);
        content.Add(new DialogHeader($"{actionVerb} {itemKind}",
            $"Create inside {parentPath}", resolvedTheme), System.Numerics.Vector2.Zero);

        _nameField = new TextField(Dialog.Width - 32f, 34f, resolvedTheme)
        {
            Name = "ItemName",
            Placeholder = $"{itemKind} name",
            Validator = ValidateName
        };
        _errorLabel = new Label(string.Empty, Dialog.Width - 32f, 28f)
        {
            Name = "ValidationError",
            TextStyle = resolvedTheme.GetTextStyle(UITextRole.AccentCaption),
            Padding = Thickness.Zero
        };
        _errorLabel.Text = _nameField.ValidationMessage ?? string.Empty;
        _nameField.ValidationChanged += message => _errorLabel.Text = message ?? string.Empty;
        var createButton = new Button(34f,
            actionVerb == "Save" ? "Save" : "Create", resolvedTheme, ButtonStyle.Primary)
            { Name = "Create" };
        var cancelButton = new Button(34f, "Cancel", resolvedTheme)
            { Name = "Cancel" };
        var createPosition = new System.Numerics.Vector2(
            Dialog.Width - createButton.Width - 16f, Dialog.Height - 50f);
        var cancelPosition = new System.Numerics.Vector2(
            createPosition.X - cancelButton.Width - 8f, Dialog.Height - 50f);

        createButton.Click += RequestCreate;
        cancelButton.Click += RequestCancel;
        DismissRequested += RequestCancel;
        content.Add(_nameField, new System.Numerics.Vector2(16f, 82f));
        content.Add(_errorLabel, new System.Numerics.Vector2(16f, 120f));
        content.Add(cancelButton, cancelPosition);
        content.Add(createButton, createPosition);
    }

    /// <summary>Displays a validation or filesystem error.</summary>
    /// <param name="message">Error text to display.</param>
    public void ShowError(string message)
    {
        _nameField.SetValidationError(message);
    }

    /// <summary>Validates and reports the entered leaf name.</summary>
    private void RequestCreate()
    {
        var name = _nameField.Text.Trim();
        _nameField.Validate();
        if (_nameField.HasValidationError)
            return;
        CreateRequested?.Invoke(name);
    }

    /// <summary>Validates a candidate filesystem leaf name.</summary>
    /// <param name="text">Pending field text.</param>
    /// <returns>An error message, or null when valid.</returns>
    private static string? ValidateName(string text)
    {
        var name = text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return "Enter a name.";
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar))
            return "Use a single valid file or folder name.";
        return null;
    }

    /// <summary>Requests that the dialog close without creating an item.</summary>
    private void RequestCancel()
    {
        CancelRequested?.Invoke();
    }
}
