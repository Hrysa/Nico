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
        Dialog.AddChild(new DialogHeader(0f, 0f, Dialog.Width, $"{actionVerb} {itemKind}",
            $"Create inside {parentPath}", resolvedTheme));

        _nameField = new TextField(16f, 82f, Dialog.Width - 32f, 34f, resolvedTheme)
        {
            Name = "ItemName",
            Placeholder = $"{itemKind} name"
        };
        _errorLabel = new Label(16f, 120f, Dialog.Width - 32f, 28f, string.Empty)
        {
            Name = "ValidationError",
            ForegroundColor = resolvedTheme.AccentHover,
            FontSize = resolvedTheme.CaptionFontSize,
            PaddingLeft = 0f
        };
        var createButton = new Button(0f, Dialog.Height - 50f, 34f,
            actionVerb == "Save" ? "Save" : "Create", resolvedTheme, ButtonStyle.Primary)
            { Name = "Create" };
        createButton.Position = new System.Numerics.Vector3(
            Dialog.Width - createButton.Width - 16f, Dialog.Height - 50f, 0f);
        var cancelButton = new Button(0f, Dialog.Height - 50f, 34f, "Cancel", resolvedTheme)
            { Name = "Cancel" };
        cancelButton.Position = new System.Numerics.Vector3(
            createButton.Position.X - cancelButton.Width - 8f, Dialog.Height - 50f, 0f);

        createButton.Click += RequestCreate;
        cancelButton.Click += RequestCancel;
        DismissRequested += RequestCancel;
        Dialog.AddChild(_nameField);
        Dialog.AddChild(_errorLabel);
        Dialog.AddChild(cancelButton);
        Dialog.AddChild(createButton);
    }

    /// <summary>Displays a validation or filesystem error.</summary>
    /// <param name="message">Error text to display.</param>
    public void ShowError(string message)
    {
        _errorLabel.Text = message;
    }

    /// <summary>Validates and reports the entered leaf name.</summary>
    private void RequestCreate()
    {
        var name = _nameField.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Enter a name.");
            return;
        }
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar))
        {
            ShowError("Use a single valid file or folder name.");
            return;
        }
        CreateRequested?.Invoke(name);
    }

    /// <summary>Requests that the dialog close without creating an item.</summary>
    private void RequestCancel()
    {
        CancelRequested?.Invoke();
    }
}
