using Engine.UI;

namespace Editor;

/// <summary>Requests confirmation before an irreversible editor action.</summary>
public sealed class ConfirmationDialog : Modal
{
    /// <summary>Occurs when the user confirms the action.</summary>
    public event Action? Confirmed;

    /// <summary>Occurs when the user cancels the action.</summary>
    public event Action? CancelRequested;

    /// <summary>Creates a confirmation dialog.</summary>
    /// <param name="width">Editor window width.</param>
    /// <param name="height">Editor window height.</param>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Action consequence shown to the user.</param>
    /// <param name="confirmLabel">Confirmation button label.</param>
    /// <param name="theme">Theme supplying dialog visuals.</param>
    public ConfirmationDialog(float width, float height, string title, string message,
        string confirmLabel, UITheme? theme = null)
        : base(width, height, MathF.Min(480f, width - 48f), 210f, theme)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        Dialog.AddChild(new DialogHeader(0f, 0f, Dialog.Width, title, message, resolvedTheme));
        var confirmButton = new Button(0f, Dialog.Height - 50f, 34f, confirmLabel,
            resolvedTheme, ButtonStyle.Primary) { Name = "Confirm" };
        confirmButton.Position = new System.Numerics.Vector3(
            Dialog.Width - confirmButton.Width - 16f, Dialog.Height - 50f, 0f);
        var cancelButton = new Button(0f, Dialog.Height - 50f, 34f, "Cancel", resolvedTheme)
            { Name = "Cancel" };
        cancelButton.Position = new System.Numerics.Vector3(
            confirmButton.Position.X - cancelButton.Width - 8f, Dialog.Height - 50f, 0f);
        confirmButton.Click += () => Confirmed?.Invoke();
        cancelButton.Click += () => CancelRequested?.Invoke();
        DismissRequested += () => CancelRequested?.Invoke();
        Dialog.AddChild(cancelButton);
        Dialog.AddChild(confirmButton);
    }
}
