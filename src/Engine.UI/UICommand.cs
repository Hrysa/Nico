using Engine.Graphics;

namespace Engine.UI;

/// <summary>Describes a logical key and modifier combination.</summary>
/// <param name="Key">Logical key.</param>
/// <param name="Modifiers">Modifiers that must match exactly.</param>
public readonly record struct UIKeyGesture(InputKey Key, InputModifiers Modifiers = InputModifiers.None)
{
    /// <summary>Checks whether a keyboard transition activates this gesture.</summary>
    /// <param name="keyEvent">Keyboard transition to inspect.</param>
    /// <param name="allowRepeat">Whether automatic repeat presses may match.</param>
    /// <returns>True for an eligible key press with matching key and modifiers.</returns>
    public bool Matches(KeyInputEvent keyEvent, bool allowRepeat = false) =>
        keyEvent.IsPressed && (allowRepeat || !keyEvent.IsRepeat) &&
        keyEvent.Key == Key && keyEvent.Modifiers == Modifiers;

    /// <summary>Formats this gesture as compact platform-neutral accelerator text.</summary>
    /// <returns>Modifier names followed by the logical key.</returns>
    public string ToDisplayString()
    {
        var text = string.Empty;
        if ((Modifiers & InputModifiers.Control) != 0)
            text += "Ctrl+";
        if ((Modifiers & InputModifiers.Alt) != 0)
            text += "Alt+";
        if ((Modifiers & InputModifiers.Shift) != 0)
            text += "Shift+";
        if ((Modifiers & InputModifiers.Super) != 0)
            text += "Super+";
        return text + Key;
    }
}

/// <summary>Maps a keyboard gesture to a routed command.</summary>
public sealed class UIKeyBinding
{
    /// <summary>Gets the activating gesture.</summary>
    public UIKeyGesture Gesture { get; }

    /// <summary>Gets the command to route.</summary>
    public UICommand Command { get; }

    /// <summary>Gets the optional command parameter.</summary>
    public object? Parameter { get; }

    /// <summary>Gets whether automatic held-key repeat may execute this binding.</summary>
    public bool AllowsRepeat { get; }

    /// <summary>Creates a key binding.</summary>
    /// <param name="gesture">Activating gesture.</param>
    /// <param name="command">Command to route.</param>
    /// <param name="parameter">Optional command parameter.</param>
    /// <param name="allowsRepeat">Whether automatic repeat presses may execute the command.</param>
    public UIKeyBinding(
        UIKeyGesture gesture,
        UICommand command,
        object? parameter = null,
        bool allowsRepeat = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        Gesture = gesture;
        Command = command;
        Parameter = parameter;
        AllowsRepeat = allowsRepeat;
    }
}

/// <summary>Provides stable identities for built-in text-editing actions.</summary>
public static class UIEditingCommands
{
    /// <summary>Selects all editable text.</summary>
    public static UICommand SelectAll { get; } = new("SelectAll");

    /// <summary>Deletes the selection or preceding character.</summary>
    public static UICommand DeleteBackward { get; } = new("DeleteBackward");

    /// <summary>Deletes the selection or following character.</summary>
    public static UICommand DeleteForward { get; } = new("DeleteForward");

    /// <summary>Copies selected text to the host clipboard.</summary>
    public static UICommand Copy { get; } = new("Copy");

    /// <summary>Copies and removes selected editable text.</summary>
    public static UICommand Cut { get; } = new("Cut");

    /// <summary>Inserts host clipboard text at the current selection.</summary>
    public static UICommand Paste { get; } = new("Paste");

    /// <summary>Restores the text state preceding the latest edit.</summary>
    public static UICommand Undo { get; } = new("Undo");

    /// <summary>Reapplies the latest undone text edit.</summary>
    public static UICommand Redo { get; } = new("Redo");

    /// <summary>Commits the pending value of the focused editor.</summary>
    public static UICommand CommitEdit { get; } = new("CommitEdit");

    /// <summary>Restores the focused editor's last committed value.</summary>
    public static UICommand CancelEdit { get; } = new("CancelEdit");

    /// <summary>Commits all pending editors in the nearest form scope.</summary>
    public static UICommand CommitForm { get; } = new("CommitForm");

    /// <summary>Cancels all pending editors in the nearest form scope.</summary>
    public static UICommand CancelForm { get; } = new("CancelForm");
}

/// <summary>Identifies an application-independent action routed through the UI tree.</summary>
public sealed class UICommand
{
    /// <summary>Gets the diagnostic command name.</summary>
    public string Name { get; }

    /// <summary>Creates a routed command.</summary>
    /// <param name="name">Non-empty diagnostic name.</param>
    public UICommand(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>Provides data while querying or executing a routed command.</summary>
public sealed class UICommandEventArgs
{
    /// <summary>Gets the routed command.</summary>
    public UICommand Command { get; internal set; } = null!;

    /// <summary>Gets the optional command parameter.</summary>
    public object? Parameter { get; internal set; }

    /// <summary>Gets the original command target.</summary>
    public UIElement Source { get; internal set; } = null!;

    /// <summary>Gets the element whose binding is being evaluated.</summary>
    public UIElement CurrentTarget { get; internal set; } = null!;

    /// <summary>Gets the active host clipboard, when available.</summary>
    public IClipboardService? Clipboard { get; internal set; }

    /// <summary>Gets or sets whether the command may execute.</summary>
    public bool CanExecute { get; set; } = true;

    /// <summary>Gets or sets whether command routing should stop.</summary>
    public bool Handled { get; set; }
}

/// <summary>Associates a routed command with query and execution callbacks.</summary>
public sealed class UICommandBinding
{
    /// <summary>Gets the command matched by this binding.</summary>
    public UICommand Command { get; }

    /// <summary>Gets the optional enabled-state query.</summary>
    public Action<UICommandEventArgs>? CanExecute { get; }

    /// <summary>Gets the command execution callback.</summary>
    public Action<UICommandEventArgs> Execute { get; }

    /// <summary>Creates a command binding.</summary>
    /// <param name="command">Command to match.</param>
    /// <param name="execute">Execution callback.</param>
    /// <param name="canExecute">Optional enabled-state query.</param>
    public UICommandBinding(
        UICommand command,
        Action<UICommandEventArgs> execute,
        Action<UICommandEventArgs>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execute);
        Command = command;
        Execute = execute;
        CanExecute = canExecute;
    }
}
