using System.Text;

namespace Engine.UI;

/// <summary>Displays the current validation messages from one edit form.</summary>
public sealed class UIValidationSummary : Label, IDisposable
{
    private readonly UIEditForm _form;
    private readonly List<string> _messages = [];
    private readonly StringBuilder _builder = new();
    private bool _disposed;

    /// <summary>Creates a validation summary bound to a form.</summary>
    /// <param name="form">Form supplying validation state.</param>
    /// <param name="width">Summary width.</param>
    /// <param name="height">Summary height.</param>
    /// <param name="theme">Theme supplying error styling.</param>
    public UIValidationSummary(
        UIEditForm form,
        float width,
        float height,
        UITheme? theme = null)
        : base(string.Empty, width, height)
    {
        ArgumentNullException.ThrowIfNull(form);
        var resolvedTheme = theme ?? UITheme.Dark;
        _form = form;
        TextStyle = resolvedTheme.GetTextStyle(UITextRole.Error);
        _form.StateChanged += Refresh;
        Refresh();
    }

    /// <summary>Detaches this summary from its form.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _form.StateChanged -= Refresh;
        _messages.Clear();
        _builder.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>Rebuilds displayed messages from current form validation state.</summary>
    private void Refresh()
    {
        _form.CopyValidationMessages(_messages);
        _builder.Clear();
        for (var index = 0; index < _messages.Count; index++)
        {
            if (index > 0)
                _builder.AppendLine();
            _builder.Append(_messages[index]);
        }
        Text = _builder.ToString();
        IsVisible = _messages.Count > 0;
    }
}
