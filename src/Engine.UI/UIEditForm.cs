namespace Engine.UI;

/// <summary>Aggregates text-editor validation and transactions within one routed command scope.</summary>
public sealed class UIEditForm : IDisposable
{
    private readonly UIElement _commandScope;
    private readonly List<TextBox> _editors = [];
    private readonly UICommandBinding _commitBinding;
    private readonly UICommandBinding _cancelBinding;
    private readonly List<Button> _commitButtons = [];
    private readonly List<Button> _cancelButtons = [];
    private bool _disposed;

    /// <summary>Gets whether every registered editor is valid.</summary>
    public bool IsValid
    {
        get
        {
            for (var index = 0; index < _editors.Count; index++)
            {
                if (_editors[index].HasValidationError)
                    return false;
            }
            return true;
        }
    }

    /// <summary>Gets whether any registered editor has pending text.</summary>
    public bool IsDirty
    {
        get
        {
            for (var index = 0; index < _editors.Count; index++)
            {
                if (_editors[index].IsDirty)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Gets whether any registered editor has pending asynchronous validation.</summary>
    public bool IsValidationPending
    {
        get
        {
            for (var index = 0; index < _editors.Count; index++)
            {
                if (_editors[index].IsValidationPending)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Gets the first invalid registered editor in deterministic registration order.</summary>
    public TextBox? FirstInvalidEditor
    {
        get
        {
            for (var index = 0; index < _editors.Count; index++)
            {
                if (_editors[index].HasValidationError)
                    return _editors[index];
            }
            return null;
        }
    }

    /// <summary>Occurs when editor text, validation, commit, or cancellation can change aggregate state.</summary>
    public event Action? StateChanged;

    /// <summary>Creates a form and installs commit/cancel bindings on its command scope.</summary>
    /// <param name="commandScope">Element receiving aggregate routed commands.</param>
    public UIEditForm(UIElement commandScope)
    {
        ArgumentNullException.ThrowIfNull(commandScope);
        _commandScope = commandScope;
        _commitBinding = new UICommandBinding(UIEditingCommands.CommitForm,
            _ => CommitAll(), args => args.CanExecute = IsDirty && IsValid && !IsValidationPending);
        _cancelBinding = new UICommandBinding(UIEditingCommands.CancelForm,
            _ => CancelAll(), args => args.CanExecute = IsDirty);
        _commandScope.CommandBindings.Add(_commitBinding);
        _commandScope.CommandBindings.Add(_cancelBinding);
    }

    /// <summary>Registers one editor with this form.</summary>
    /// <param name="editor">Editor to aggregate.</param>
    public void Register(TextBox editor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(editor);
        if (_editors.Contains(editor))
            return;
        _editors.Add(editor);
        editor.TextChanged += OnTextChanged;
        editor.ValidationChanged += OnValidationChanged;
        editor.ValidationStateChanged += OnValidationStateChanged;
        editor.EditCommitted += OnTextChanged;
        editor.EditCanceled += OnEditorCanceled;
        PublishStateChanged();
    }

    /// <summary>Removes every registered editor while retaining the command scope.</summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var index = 0; index < _editors.Count; index++)
            Unsubscribe(_editors[index]);
        _editors.Clear();
        PublishStateChanged();
    }

    /// <summary>Binds a button to aggregate form commit and validity state.</summary>
    /// <param name="button">Button that commits the form.</param>
    public void BindCommitButton(Button button)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(button);
        if (_commitButtons.Contains(button))
            return;
        _commitButtons.Add(button);
        button.Click += OnCommitButtonClicked;
        UpdateButtons();
    }

    /// <summary>Binds a button to aggregate form cancellation and dirty state.</summary>
    /// <param name="button">Button that cancels the form.</param>
    public void BindCancelButton(Button button)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(button);
        if (_cancelButtons.Contains(button))
            return;
        _cancelButtons.Add(button);
        button.Click += OnCancelButtonClicked;
        UpdateButtons();
    }

    /// <summary>Commits every editor after validating the complete form.</summary>
    /// <returns>True when the form is valid and all pending values commit.</returns>
    public bool CommitAll()
    {
        for (var index = 0; index < _editors.Count; index++)
            _editors[index].Validate();
        if (!IsValid || IsValidationPending)
        {
            PublishStateChanged();
            return false;
        }
        for (var index = 0; index < _editors.Count; index++)
            _editors[index].CommitEdit();
        PublishStateChanged();
        return true;
    }

    /// <summary>Runs asynchronous validation for every registered editor.</summary>
    /// <param name="cancellationToken">External cancellation request.</param>
    /// <returns>True when every editor is valid and no validation remains pending.</returns>
    public async ValueTask<bool> ValidateAllAsync(CancellationToken cancellationToken = default)
    {
        for (var index = 0; index < _editors.Count; index++)
        {
            if (!await _editors[index].ValidateAsync(cancellationToken))
            {
                PublishStateChanged();
                return false;
            }
        }
        PublishStateChanged();
        return IsValid && !IsValidationPending;
    }

    /// <summary>Restores every registered editor to its committed baseline.</summary>
    public void CancelAll()
    {
        for (var index = 0; index < _editors.Count; index++)
            _editors[index].CancelEdit();
        PublishStateChanged();
    }

    /// <summary>Validates the form and focuses its first invalid editor.</summary>
    /// <param name="router">Host router controlling focus.</param>
    /// <returns>True when an invalid editor received focus.</returns>
    public bool FocusFirstInvalid(UIEventRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        for (var index = 0; index < _editors.Count; index++)
            _editors[index].Validate();
        if (FirstInvalidEditor is not { } editor)
            return false;
        router.Focus(editor);
        return true;
    }

    /// <summary>Copies current validation messages into caller-owned storage.</summary>
    /// <param name="messages">List cleared and populated in registration order.</param>
    public void CopyValidationMessages(List<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        messages.Clear();
        for (var index = 0; index < _editors.Count; index++)
        {
            if (_editors[index].ValidationMessage is { } message)
                messages.Add(message);
        }
    }

    /// <summary>Removes routed bindings and editor event subscriptions.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _commandScope.CommandBindings.Remove(_commitBinding);
        _commandScope.CommandBindings.Remove(_cancelBinding);
        for (var index = 0; index < _editors.Count; index++)
            Unsubscribe(_editors[index]);
        _editors.Clear();
        for (var index = 0; index < _commitButtons.Count; index++)
            _commitButtons[index].Click -= OnCommitButtonClicked;
        for (var index = 0; index < _cancelButtons.Count; index++)
            _cancelButtons[index].Click -= OnCancelButtonClicked;
        _commitButtons.Clear();
        _cancelButtons.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>Forwards an editor event into aggregate state notification.</summary>
    /// <param name="text">Current editor text.</param>
    private void OnTextChanged(string text) => PublishStateChanged();

    /// <summary>Forwards validation changes into aggregate state notification.</summary>
    /// <param name="message">Current validation message.</param>
    private void OnValidationChanged(string? message) => PublishStateChanged();

    /// <summary>Forwards validation pending-state changes into aggregate state notification.</summary>
    private void OnValidationStateChanged() => PublishStateChanged();

    /// <summary>Forwards cancellation into aggregate state notification.</summary>
    private void OnEditorCanceled() => PublishStateChanged();

    /// <summary>Detaches aggregate event handlers from one editor.</summary>
    /// <param name="editor">Editor to detach.</param>
    private void Unsubscribe(TextBox editor)
    {
        editor.TextChanged -= OnTextChanged;
        editor.ValidationChanged -= OnValidationChanged;
        editor.ValidationStateChanged -= OnValidationStateChanged;
        editor.EditCommitted -= OnTextChanged;
        editor.EditCanceled -= OnEditorCanceled;
    }

    /// <summary>Commits the form from a bound button.</summary>
    private void OnCommitButtonClicked() => CommitAll();

    /// <summary>Cancels the form from a bound button.</summary>
    private void OnCancelButtonClicked() => CancelAll();

    /// <summary>Refreshes bound button eligibility and publishes aggregate state.</summary>
    private void PublishStateChanged()
    {
        UpdateButtons();
        StateChanged?.Invoke();
    }

    /// <summary>Synchronizes bound button enabled states with aggregate form state.</summary>
    private void UpdateButtons()
    {
        var canCommit = IsDirty && IsValid && !IsValidationPending;
        for (var index = 0; index < _commitButtons.Count; index++)
            _commitButtons[index].IsEnabled = canCommit;
        for (var index = 0; index < _cancelButtons.Count; index++)
            _cancelButtons[index].IsEnabled = IsDirty;
    }
}
