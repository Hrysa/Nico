using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// A single-line themed text-entry control with caret editing.
/// </summary>
public sealed class TextField : Surface
{
    private readonly UITheme _theme;
    private string _text = string.Empty;
    private string _placeholder = string.Empty;
    private int _caretIndex;
    private int _textWindowStart;

    /// <summary>Gets or sets the editable text.</summary>
    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? string.Empty;
            _caretIndex = Math.Clamp(_caretIndex, 0, _text.Length);
            _textWindowStart = Math.Clamp(_textWindowStart, 0, _caretIndex);
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets placeholder text displayed when empty and unfocused.</summary>
    public string Placeholder
    {
        get => _placeholder;
        set
        {
            var resolved = value ?? string.Empty;
            if (_placeholder == resolved)
                return;
            _placeholder = resolved;
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets whether editing is allowed.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Occurs when editable text changes.</summary>
    public event Action<string>? TextChanged;

    /// <summary>
    /// Creates a single-line text field.
    /// </summary>
    /// <param name="width">Field width.</param>
    /// <param name="height">Field height.</param>
    /// <param name="theme">Theme supplying colors and typography.</param>
    public TextField(float width, float height, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).Field, (theme ?? UITheme.Dark).BorderStrong, width, height)
    {
        _theme = theme ?? UITheme.Dark;
        ForegroundColor = _theme.TextPrimary;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        BorderColor = IsFocused ? _theme.Accent : _theme.BorderStrong;
        base.Paint(drawList);
        var displayText = _text;
        var displayCaretIndex = _caretIndex;
        var color = ForegroundColor;
        if (displayText.Length == 0 && !IsFocused)
        {
            displayText = Placeholder;
            color = _theme.TextMuted;
        }
        else if (IsFocused)
        {
            var visibleCharacterCount = Math.Max(1,
                (int)MathF.Floor(MathF.Max(0f, Width - 20f) / _theme.FontSize));
            if (_caretIndex < _textWindowStart)
                _textWindowStart = _caretIndex;
            else if (_caretIndex > _textWindowStart + visibleCharacterCount)
                _textWindowStart = _caretIndex - visibleCharacterCount;
            _textWindowStart = Math.Clamp(_textWindowStart, 0,
                Math.Max(0, _text.Length - visibleCharacterCount));
            var displayLength = Math.Min(visibleCharacterCount,
                _text.Length - _textWindowStart);
            displayText = _text.Substring(_textWindowStart, displayLength);
            displayCaretIndex -= _textWindowStart;
        }
        var textLeft = Left + 10f;
        var textTop = Top + MathF.Max(0f, (Height - _theme.FontSize) / 2f);
        if (IsFocused)
            drawList.AddTextWithCaret(displayText, textLeft, textTop, _theme.FontSize,
                color, BackgroundColor, displayCaretIndex);
        else
            drawList.AddText(displayText, textLeft, textTop,
                _theme.FontSize, color, BackgroundColor);
    }

    /// <inheritdoc/>
    protected override void OnFocus()
    {
        _caretIndex = _text.Length;
        _textWindowStart = 0;
        base.OnFocus();
    }

    /// <inheritdoc/>
    protected override void OnTextInput(char character)
    {
        if (!IsReadOnly && !char.IsControl(character))
        {
            _text = _text.Insert(_caretIndex, character.ToString());
            _caretIndex++;
            InvalidateVisual();
            TextChanged?.Invoke(_text);
        }
        base.OnTextInput(character);
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(int keyCode)
    {
        var key = (InputKey)keyCode;
        if (!IsReadOnly && key == InputKey.Backspace && _caretIndex > 0)
        {
            _text = _text.Remove(_caretIndex - 1, 1);
            _caretIndex--;
            TextChanged?.Invoke(_text);
        }
        else if (!IsReadOnly && key == InputKey.Delete && _caretIndex < _text.Length)
        {
            _text = _text.Remove(_caretIndex, 1);
            TextChanged?.Invoke(_text);
        }
        else if (key == InputKey.Left)
        {
            _caretIndex = Math.Max(0, _caretIndex - 1);
        }
        else if (key == InputKey.Right)
        {
            _caretIndex = Math.Min(_text.Length, _caretIndex + 1);
        }
        else if (key == InputKey.Home)
        {
            _caretIndex = 0;
        }
        else if (key == InputKey.End)
        {
            _caretIndex = _text.Length;
        }
        InvalidateVisual();
        base.OnKeyDown(keyCode);
    }
}
