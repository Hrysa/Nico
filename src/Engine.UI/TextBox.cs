using Engine.Graphics;
using System.Globalization;
using System.Text;

namespace Engine.UI;

/// <summary>A themed multiline text editor with caret, selection, and clipboard editing.</summary>
public class TextBox : Surface
{
    private readonly UITheme _theme;
    private readonly bool _acceptsReturn;
    private string _text = string.Empty;
    private int _caretIndex;
    private int _textWindowStart;
    private int _firstVisibleLine;
    private int _selectionStart;
    private int _selectionLength;
    private int _selectionAnchor;
    private bool _isPointerSelecting;
    private readonly List<EditState> _undoStates = [];
    private readonly List<EditState> _redoStates = [];
    private int[] _textElementBoundaries = [];
    private EditKind _coalescingEdit;
    private int _preferredCaretColumn = -1;
    private int _historyCapacity = 128;
    private char _passwordCharacter = '\u2022';
    private string _maskedText = string.Empty;
    private PasswordRevealMode _passwordRevealMode = PasswordRevealMode.Never;
    private Func<string, string?>? _validator;
    private string? _validationMessage;
    private string? _validatorMessage;
    private string? _externalValidationMessage;
    private string? _asyncValidationMessage;
    private Func<string, CancellationToken, ValueTask<string?>>? _asyncValidator;
    private CancellationTokenSource? _validationCancellation;
    private int _validationGeneration;
    private bool _isValidationPending;
    private CancellationTokenSource? _validationDebounceCancellation;
    private TimeSpan _asyncValidationDelay = Timeout.InfiniteTimeSpan;
    private int _maxLength = int.MaxValue;
    private string _committedText = string.Empty;
    private string _compositionText = string.Empty;
    private int _compositionCaretIndex;
    private bool _isComposing;
    private int _compositionSelectionStart;
    private int _compositionSelectionLength;

    /// <summary>Gets or sets the editable text.</summary>
    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? string.Empty;
            _caretIndex = Math.Clamp(_caretIndex, 0, _text.Length);
            _selectionStart = Math.Clamp(_selectionStart, 0, _text.Length);
            _selectionLength = Math.Clamp(_selectionLength, 0, _text.Length - _selectionStart);
            _textWindowStart = Math.Clamp(_textWindowStart, 0, _caretIndex);
            _selectionAnchor = _caretIndex;
            _undoStates.Clear();
            _redoStates.Clear();
            _coalescingEdit = EditKind.None;
            RebuildTextElementBoundaries();
            RebuildMaskedText();
            _externalValidationMessage = null;
            CancelPendingValidation();
            _asyncValidationMessage = null;
            Validate();
            if (!IsFocused)
                _committedText = _text;
            EnsureCaretLineVisible();
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets placeholder text displayed when empty and unfocused.</summary>
    public string Placeholder
    {
        get;
        set
        {
            var resolved = value ?? string.Empty;
            if (field == resolved)
                return;
            field = resolved;
            InvalidateVisual();
        }
    } = string.Empty;

    /// <summary>Gets or sets whether editing is allowed.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Gets whether the editor accepts newline insertion.</summary>
    public bool AcceptsReturn => _acceptsReturn;

    /// <summary>Gets the current UTF-16 caret index.</summary>
    public int CaretIndex => _caretIndex;

    /// <summary>Gets the UTF-16 start index of the current selection.</summary>
    public int SelectionStart => _selectionStart;

    /// <summary>Gets the UTF-16 length of the current selection.</summary>
    public int SelectionLength => _selectionLength;

    /// <summary>Gets whether an edit can currently be undone.</summary>
    public bool CanUndo => _undoStates.Count > 0;

    /// <summary>Gets whether an undone edit can currently be reapplied.</summary>
    public bool CanRedo => _redoStates.Count > 0;

    /// <summary>Gets or sets the maximum number of retained undo and redo states.</summary>
    public int HistoryCapacity
    {
        get => _historyCapacity;
        set
        {
            _historyCapacity = Math.Max(0, value);
            TrimHistory(_undoStates);
            TrimHistory(_redoStates);
        }
    }

    /// <summary>Gets or sets whether text is rendered using the password character.</summary>
    public bool IsPassword
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets the character used to mask password text.</summary>
    public char PasswordCharacter
    {
        get => _passwordCharacter;
        set
        {
            if (_passwordCharacter == value || char.IsControl(value))
                return;
            _passwordCharacter = value;
            RebuildMaskedText();
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets when password text is revealed.</summary>
    public PasswordRevealMode PasswordRevealMode
    {
        get => _passwordRevealMode;
        set
        {
            if (_passwordRevealMode == value)
                return;
            _passwordRevealMode = value;
            InvalidateVisual();
        }
    }

    /// <summary>Gets the first logical line currently visible in a multiline editor.</summary>
    public int FirstVisibleLine => _firstVisibleLine;

    /// <summary>Gets the first UTF-16 index visible in a horizontally scrolling single-line editor.</summary>
    public int FirstVisibleTextIndex => _textWindowStart;

    /// <summary>Gets or sets whether Tab inserts indentation instead of moving focus.</summary>
    public bool AcceptsTab { get; set; }

    /// <summary>Gets or sets the text inserted or removed by Tab and Shift+Tab.</summary>
    public string IndentationText { get; set; } = "    ";

    /// <summary>Gets or sets the optional validator returning an error message or null.</summary>
    public Func<string, string?>? Validator
    {
        get => _validator;
        set
        {
            _validator = value;
            Validate();
        }
    }

    /// <summary>Gets the current validation error, or null when valid.</summary>
    public string? ValidationMessage => _validationMessage;

    /// <summary>Gets whether validation currently reports an error.</summary>
    public bool HasValidationError => _validationMessage is not null;

    /// <summary>Gets whether asynchronous validation is currently pending.</summary>
    public bool IsValidationPending => _isValidationPending;

    /// <summary>Gets whether the pending text differs from the last committed value.</summary>
    public bool IsDirty => _text != _committedText;

    /// <summary>Gets whether a platform input-method composition is active.</summary>
    public bool IsComposing => _isComposing;

    /// <summary>Gets transient input-method text that is not yet committed.</summary>
    public string CompositionText => _compositionText;

    /// <summary>Gets the UTF-16 caret index within transient composition text.</summary>
    public int CompositionCaretIndex => _compositionCaretIndex;

    /// <summary>Gets the UTF-16 start of the active composition candidate range.</summary>
    public int CompositionSelectionStart => _compositionSelectionStart;

    /// <summary>Gets the UTF-16 length of the active composition candidate range.</summary>
    public int CompositionSelectionLength => _compositionSelectionLength;

    /// <summary>Gets or sets when pending text requests a model update.</summary>
    public TextUpdateTrigger UpdateTrigger { get; set; } = TextUpdateTrigger.TextChanged;

    /// <summary>Occurs when the validation result changes.</summary>
    public event Action<string?>? ValidationChanged;

    /// <summary>Occurs when validation error or pending state changes.</summary>
    public event Action? ValidationStateChanged;

    /// <summary>Occurs after a valid pending edit becomes committed.</summary>
    public event Action<string>? EditCommitted;

    /// <summary>Occurs after a pending edit is restored to its committed value.</summary>
    public event Action? EditCanceled;

    /// <summary>Occurs when the configured update trigger requests a model update.</summary>
    public event Action<string>? ValueUpdateRequested;

    /// <summary>Gets or sets the maximum number of Unicode text elements accepted from user input.</summary>
    public int MaxLength
    {
        get => _maxLength;
        set => _maxLength = Math.Max(0, value);
    }

    /// <summary>Gets or sets an optional Unicode scalar filter for typing and paste input.</summary>
    public Func<System.Text.Rune, bool>? InputFilter { get; set; }

    /// <summary>Gets or sets optional cancellable asynchronous validation.</summary>
    public Func<string, CancellationToken, ValueTask<string?>>? AsyncValidator
    {
        get => _asyncValidator;
        set
        {
            CancelPendingValidation();
            _asyncValidator = value;
            _asyncValidationMessage = null;
            PublishValidationResult();
            ScheduleAsyncValidation();
        }
    }

    /// <summary>Gets or sets the automatic validation debounce delay; infinite disables it.</summary>
    public TimeSpan AsyncValidationDelay
    {
        get => _asyncValidationDelay;
        set
        {
            if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(value));
            _asyncValidationDelay = value;
            ScheduleAsyncValidation();
        }
    }

    /// <summary>Gets or sets the safe message used when an asynchronous validator throws.</summary>
    public string AsyncValidationFailureMessage { get; set; } = "Validation could not be completed.";

    /// <summary>Occurs when editable text changes.</summary>
    public event Action<string>? TextChanged;

    /// <summary>Creates a multiline text box.</summary>
    /// <param name="width">Editor width.</param>
    /// <param name="height">Editor height.</param>
    /// <param name="theme">Theme supplying colors and typography.</param>
    public TextBox(float width, float height, UITheme? theme = null)
        : this(width, height, true, theme)
    {
    }

    /// <summary>Creates a text editor with a configured newline policy.</summary>
    /// <param name="width">Editor width.</param>
    /// <param name="height">Editor height.</param>
    /// <param name="acceptsReturn">Whether Enter and pasted line breaks are preserved.</param>
    /// <param name="theme">Theme supplying colors and typography.</param>
    protected TextBox(float width, float height, bool acceptsReturn, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).Field, (theme ?? UITheme.Dark).BorderStrong, width, height)
    {
        _theme = theme ?? UITheme.Dark;
        _acceptsReturn = acceptsReturn;
        Padding = new Thickness(_theme.TextContentPadding);
        ClipToBounds = true;
        IsTabStop = true;
        ForegroundColor = _theme.TextPrimary;
        CommandBindings.Add(new UICommandBinding(
            UIEditingCommands.SelectAll, _ => SelectAll(), args => args.CanExecute = _text.Length > 0));
        CommandBindings.Add(new UICommandBinding(
            UIEditingCommands.DeleteBackward, _ => DeleteBackward(), args => args.CanExecute = CanDeleteBackward()));
        CommandBindings.Add(new UICommandBinding(
            UIEditingCommands.DeleteForward, _ => DeleteForward(), args => args.CanExecute = CanDeleteForward()));
        CommandBindings.Add(new UICommandBinding(
            UIEditingCommands.Copy, CopySelection,
            args => args.CanExecute = args.Clipboard is not null && _selectionLength > 0));
        CommandBindings.Add(new UICommandBinding(
            UIEditingCommands.Cut, CutSelection,
            args => args.CanExecute = args.Clipboard is not null && !IsReadOnly && _selectionLength > 0));
        CommandBindings.Add(new UICommandBinding(
            UIEditingCommands.Paste, PasteClipboard,
            args => args.CanExecute = !IsReadOnly && !string.IsNullOrEmpty(args.Clipboard?.GetText())));
        CommandBindings.Add(new UICommandBinding(
            UIEditingCommands.Undo, _ => Undo(), args => args.CanExecute = !IsReadOnly && CanUndo));
        CommandBindings.Add(new UICommandBinding(
            UIEditingCommands.Redo, _ => Redo(), args => args.CanExecute = !IsReadOnly && CanRedo));
        CommandBindings.Add(new UICommandBinding(
            UIEditingCommands.CommitEdit, _ => CommitEdit(),
            args => args.CanExecute = !HasValidationError && !IsValidationPending && IsDirty));
        CommandBindings.Add(new UICommandBinding(
            UIEditingCommands.CancelEdit, _ => CancelEdit(), args => args.CanExecute = IsDirty));
        KeyBindings.Add(new UIKeyBinding(
            new UIKeyGesture(InputKey.A, InputModifiers.Control), UIEditingCommands.SelectAll));
        KeyBindings.Add(new UIKeyBinding(
            new UIKeyGesture(InputKey.A, InputModifiers.Super), UIEditingCommands.SelectAll));
        KeyBindings.Add(new UIKeyBinding(
            new UIKeyGesture(InputKey.Backspace), UIEditingCommands.DeleteBackward,
            allowsRepeat: true));
        KeyBindings.Add(new UIKeyBinding(
            new UIKeyGesture(InputKey.Delete), UIEditingCommands.DeleteForward,
            allowsRepeat: true));
        AddClipboardGestures(InputModifiers.Control);
        AddClipboardGestures(InputModifiers.Super);
        AddHistoryGestures(InputModifiers.Control);
        AddHistoryGestures(InputModifiers.Super);
        if (!acceptsReturn)
            KeyBindings.Add(new UIKeyBinding(new UIKeyGesture(InputKey.Enter), UIEditingCommands.CommitEdit));
        KeyBindings.Add(new UIKeyBinding(new UIKeyGesture(InputKey.Escape), UIEditingCommands.CancelEdit));
        Key += OnRoutedKey;
        Pointer += OnRoutedPointer;
        RoutedTextInput += OnRoutedTextInput;
        TextComposition += OnRoutedTextComposition;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        BorderColor = HasValidationError
            ? _theme.Error
            : IsFocused ? _theme.Accent : _theme.BorderStrong;
        base.Paint(drawList);
    }

    /// <inheritdoc/>
    protected override void PaintContent(UIDrawList drawList)
    {
        if (!_acceptsReturn)
            PaintSingleLine(drawList);
        else
            PaintMultipleLines(drawList);
    }

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        IsPassword ? UISemanticRole.PasswordField : UISemanticRole.TextField,
        Name,
        IsPassword ? null : _text,
        IsEnabled,
        IsReadOnly,
        HasValidationError,
        ValidationMessage,
        IsValidationPending);

    /// <summary>Paints the single-line specialization.</summary>
    /// <param name="drawList">Draw-list receiving text commands.</param>
    private void PaintSingleLine(UIDrawList drawList)
    {
        var sourceText = GetDisplayText();
        var displayText = sourceText;
        var displayCaretIndex = _caretIndex;
        var color = ForegroundColor;
        if (displayText.Length == 0 && !IsFocused)
        {
            displayText = Placeholder;
            color = _theme.TextMuted;
        }
        var displayStart = ResolveSingleLineStart(displayText, IsFocused, ContentWidth);
        displayText = displayText[displayStart..];
        displayCaretIndex -= displayStart;
        var left = ContentLeft;
        var top = ContentTop + MathF.Max(0f, (ContentHeight - _theme.FontSize) / 2f);
        PaintSelection(drawList, displayText, displayStart, left, top, GetLineHeight());
        if (IsFocused)
            drawList.AddTextWithCaret(displayText, left, top, _theme.FontSize,
                color, BackgroundColor, displayCaretIndex, FlowDirection.ToTextFlowDirection());
        else
            drawList.AddText(displayText, left, top, _theme.FontSize, color, BackgroundColor,
                FlowDirection.ToTextFlowDirection());
        if (IsFocused)
            PaintComposition(drawList, displayText, displayCaretIndex, left, top);
    }

    /// <summary>Paints visible logical lines and the caret-bearing line.</summary>
    /// <param name="drawList">Draw-list receiving text commands.</param>
    private void PaintMultipleLines(UIDrawList drawList)
    {
        var left = ContentLeft;
        var top = ContentTop;
        var lineHeight = GetLineHeight();
        if (_text.Length == 0 && !IsFocused)
        {
            drawList.AddText(Placeholder, left, top, _theme.FontSize, _theme.TextMuted,
                BackgroundColor, FlowDirection.ToTextFlowDirection());
            return;
        }

        var lineIndex = 0;
        var start = 0;
        while (start <= _text.Length)
        {
            var end = GetLineEnd(start);
            if (lineIndex >= _firstVisibleLine)
            {
                var y = top + (lineIndex - _firstVisibleLine) * lineHeight;
                if (y + lineHeight > Top + Height)
                    break;
                var line = GetDisplayText().Substring(start, end - start);
                PaintSelection(drawList, line, start, left, y, lineHeight);
                if (IsFocused && _caretIndex >= start && _caretIndex <= end)
                {
                    drawList.AddTextWithCaret(line, left, y, _theme.FontSize,
                        ForegroundColor, BackgroundColor, _caretIndex - start,
                        FlowDirection.ToTextFlowDirection());
                    PaintComposition(drawList, line, _caretIndex - start, left, y);
                }
                else
                    drawList.AddText(line, left, y, _theme.FontSize, ForegroundColor,
                        BackgroundColor, FlowDirection.ToTextFlowDirection());
            }
            if (end == _text.Length)
                break;
            start = end + 1;
            lineIndex++;
        }
    }

    /// <summary>Paints transient input-method text at the stored-text caret.</summary>
    /// <param name="drawList">Draw-list receiving composition text.</param>
    /// <param name="line">Displayed stored-text line.</param>
    /// <param name="caretIndex">Caret index within the displayed line.</param>
    /// <param name="left">Displayed line left edge.</param>
    /// <param name="top">Displayed line top edge.</param>
    private void PaintComposition(
        UIDrawList drawList,
        string line,
        int caretIndex,
        float left,
        float top)
    {
        if (!IsComposing)
            return;
        var prefixLength = Math.Clamp(caretIndex, 0, line.Length);
        var prefixWidth = GetCaretPosition(line.AsSpan(), prefixLength);
        if (_compositionSelectionLength > 0)
        {
            var ranges = GetSelectionRanges(
                _compositionText.AsSpan(),
                _compositionSelectionStart,
                _compositionSelectionLength);
            foreach (var range in ranges)
            {
                drawList.AddRectangle(left + prefixWidth + range.Left, top,
                    left + prefixWidth + range.Left + range.Width,
                    top + GetLineHeight(), _theme.AccentPressed);
            }
        }
        drawList.AddTextWithCaret(_compositionText, left + prefixWidth, top, _theme.FontSize,
            _theme.AccentHover, BackgroundColor, _compositionCaretIndex,
            FlowDirection.ToTextFlowDirection());
    }

    /// <summary>Paints the intersection of the current selection with one displayed line.</summary>
    /// <param name="drawList">Draw-list receiving the highlight rectangle.</param>
    /// <param name="line">Displayed line text.</param>
    /// <param name="lineStart">UTF-16 start of the displayed line in the buffer.</param>
    /// <param name="left">Displayed line left edge.</param>
    /// <param name="top">Displayed line top edge.</param>
    /// <param name="lineHeight">Displayed line height.</param>
    private void PaintSelection(
        UIDrawList drawList,
        string line,
        int lineStart,
        float left,
        float top,
        float lineHeight)
    {
        if (_selectionLength == 0)
            return;
        var selectionEnd = _selectionStart + _selectionLength;
        var localStart = Math.Clamp(_selectionStart - lineStart, 0, line.Length);
        var localEnd = Math.Clamp(selectionEnd - lineStart, 0, line.Length);
        if (localEnd <= localStart)
            return;
        var ranges = GetSelectionRanges(line.AsSpan(), localStart, localEnd - localStart);
        foreach (var range in ranges)
        {
            drawList.AddRectangle(left + range.Left, top,
                left + range.Left + range.Width, top + lineHeight, _theme.AccentPressed);
        }
    }

    /// <summary>Resolves the first displayed text element while keeping an editing caret visible.</summary>
    /// <param name="displayText">Stored, masked, or placeholder text to display.</param>
    /// <param name="keepCaretVisible">Whether the window follows the editing caret.</param>
    /// <param name="availableWidth">Shared content-box width.</param>
    /// <returns>Inclusive UTF-16 display start.</returns>
    private int ResolveSingleLineStart(
        string displayText,
        bool keepCaretVisible,
        float availableWidth)
    {
        var start = keepCaretVisible ? Math.Min(_textWindowStart, displayText.Length) : 0;
        var caret = Math.Min(_caretIndex, displayText.Length);
        if (keepCaretVisible && caret < start)
            start = caret;
        while (keepCaretVisible && start < caret &&
               MeasureTextWidth(displayText.AsSpan(start, caret - start)) > availableWidth)
        {
            start = FindNextDisplayTextElement(displayText, start);
        }
        if (keepCaretVisible)
            _textWindowStart = start;
        return start;
    }

    /// <summary>Finds the next grapheme boundary in arbitrary rendered text.</summary>
    /// <param name="text">Stored, masked, or placeholder display text.</param>
    /// <param name="index">Current UTF-16 index.</param>
    /// <returns>Next UTF-16 grapheme boundary or the text length.</returns>
    private static int FindNextDisplayTextElement(string text, int index)
    {
        if (index >= text.Length)
            return text.Length;
        return index + StringInfo.GetNextTextElementLength(text.AsSpan(index));
    }

    /// <inheritdoc/>
    protected override void OnFocus()
    {
        BreakHistoryGroup();
        _committedText = _text;
        if (!_isPointerSelecting)
        {
            _caretIndex = _text.Length;
            _textWindowStart = 0;
            ClearSelection();
        }
        EnsureCaretLineVisible();
        base.OnFocus();
    }

    /// <inheritdoc/>
    protected override void OnBlur()
    {
        ClearComposition();
        if (UpdateTrigger == TextUpdateTrigger.LostFocus)
            CommitEdit();
        InvalidateVisual();
        base.OnBlur();
    }

    /// <inheritdoc/>
    protected override void OnTextInput(char character)
    {
        if (!IsReadOnly && !char.IsControl(character))
            InsertText(character.ToString(), EditKind.Typing);
        base.OnTextInput(character);
    }

    /// <summary>Handles complete committed Unicode strings before the compatibility character route.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="textEvent">Routed committed-text data.</param>
    private void OnRoutedTextInput(UIElement sender, UITextInputEventArgs textEvent)
    {
        if (textEvent.RoutePhase != UIRoutePhase.Target || IsReadOnly)
            return;
        var text = _acceptsReturn
            ? textEvent.Text.Replace("\r\n", "\n").Replace('\r', '\n')
            : textEvent.Text.Replace("\r", string.Empty).Replace("\n", string.Empty)
                .Replace("\t", string.Empty);
        InsertText(text, EditKind.Typing);
        textEvent.Handled = true;
    }

    /// <summary>Tracks transient IME text and commits it only on composition completion.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="compositionEvent">Routed composition transition.</param>
    private void OnRoutedTextComposition(
        UIElement sender,
        UITextCompositionEventArgs compositionEvent)
    {
        if (compositionEvent.RoutePhase != UIRoutePhase.Target)
            return;
        if (compositionEvent.Kind is TextCompositionKind.Started or TextCompositionKind.Updated)
        {
            _isComposing = true;
            _compositionText = compositionEvent.Text;
            _compositionCaretIndex = compositionEvent.CaretIndex;
            _compositionSelectionStart = compositionEvent.SelectionStart;
            _compositionSelectionLength = compositionEvent.SelectionLength;
            InvalidateVisual();
        }
        else if (compositionEvent.Kind == TextCompositionKind.Completed)
        {
            ClearComposition();
            if (!IsReadOnly)
                InsertText(compositionEvent.Text, EditKind.Typing);
        }
        else
        {
            ClearComposition();
        }
        compositionEvent.Handled = true;
    }

    /// <summary>Clears transient input-method state without changing committed text.</summary>
    private void ClearComposition()
    {
        if (!_isComposing && _compositionText.Length == 0 && _compositionCaretIndex == 0)
            return;
        _isComposing = false;
        _compositionText = string.Empty;
        _compositionCaretIndex = 0;
        _compositionSelectionStart = 0;
        _compositionSelectionLength = 0;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(int keyCode)
    {
        var key = (InputKey)keyCode;
        if (!IsReadOnly && key == InputKey.Enter && _acceptsReturn)
            InsertText("\n", EditKind.Typing);
        else if (!IsReadOnly && key == InputKey.Backspace && CanDeleteBackward())
            DeleteBackward();
        else if (!IsReadOnly && key == InputKey.Delete && CanDeleteForward())
            DeleteForward();
        InvalidateVisual();
        base.OnKeyDown(keyCode);
    }

    /// <summary>Handles modifier-aware caret movement and Shift selection.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="keyEvent">Routed keyboard transition.</param>
    private void OnRoutedKey(UIElement sender, UIKeyEventArgs keyEvent)
    {
        if (keyEvent.RoutePhase != UIRoutePhase.Target || keyEvent.Kind != UIKeyEventKind.KeyDown)
            return;
        var extend = (keyEvent.Modifiers & InputModifiers.Shift) != 0;
        var wordNavigation = (keyEvent.Modifiers & InputModifiers.Control) != 0;
        if (keyEvent.Key == InputKey.Left)
            MoveCaret(wordNavigation ? FindPreviousWordBoundary(_caretIndex) : FindPreviousTextElement(_caretIndex), extend);
        else if (keyEvent.Key == InputKey.Right)
            MoveCaret(wordNavigation ? FindNextWordBoundary(_caretIndex) : FindNextTextElement(_caretIndex), extend);
        else if (keyEvent.Key == InputKey.Up && _acceptsReturn)
            MoveCaretVertically(-1, extend);
        else if (keyEvent.Key == InputKey.Down && _acceptsReturn)
            MoveCaretVertically(1, extend);
        else if (keyEvent.Key == InputKey.PageUp && _acceptsReturn)
            MoveCaretByPage(-1, extend);
        else if (keyEvent.Key == InputKey.PageDown && _acceptsReturn)
            MoveCaretByPage(1, extend);
        else if (keyEvent.Key == InputKey.Tab && AcceptsTab && !IsReadOnly)
            ChangeIndentation(extend);
        else if (keyEvent.Key == InputKey.Home)
            MoveCaret(_acceptsReturn ? GetLineStart(_caretIndex) : 0, extend);
        else if (keyEvent.Key == InputKey.End)
            MoveCaret(_acceptsReturn ? GetLineEnd(GetLineStart(_caretIndex)) : _text.Length, extend);
        else
            return;
        keyEvent.Handled = true;
    }

    /// <summary>Places or extends selection from captured primary-pointer input.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="pointerEvent">Routed pointer data.</param>
    private void OnRoutedPointer(UIElement sender, UIPointerEventArgs pointerEvent)
    {
        if (pointerEvent.RoutePhase != UIRoutePhase.Target)
            return;
        if (pointerEvent.Kind == UIPointerEventKind.Press &&
            pointerEvent.Button == InputPointerButton.Primary)
        {
            _isPointerSelecting = true;
            var extend = (pointerEvent.Modifiers & InputModifiers.Shift) != 0;
            MoveCaret(HitTestCaret(pointerEvent.LocalPosition), extend);
            pointerEvent.CapturePointer();
        }
        else if (pointerEvent.Kind == UIPointerEventKind.DoubleClick &&
                 pointerEvent.Button == InputPointerButton.Primary)
        {
            _isPointerSelecting = false;
            pointerEvent.ReleasePointerCapture();
            var caret = HitTestCaret(pointerEvent.LocalPosition);
            if (pointerEvent.ClickCount >= 3)
                SelectLineAt(caret);
            else
                SelectWordAt(caret);
            pointerEvent.Handled = true;
        }
        else if (pointerEvent.Kind == UIPointerEventKind.Move && _isPointerSelecting)
        {
            AutoScrollSelection(pointerEvent.LocalPosition);
            MoveCaret(HitTestCaret(pointerEvent.LocalPosition), true);
            pointerEvent.Handled = true;
        }
        else if (pointerEvent.Kind == UIPointerEventKind.Release && _isPointerSelecting)
        {
            MoveCaret(HitTestCaret(pointerEvent.LocalPosition), true);
            _isPointerSelecting = false;
            pointerEvent.ReleasePointerCapture();
            pointerEvent.Handled = true;
        }
    }

    /// <summary>Maps a local pointer position to the nearest UTF-16 caret index.</summary>
    /// <param name="position">Pointer position relative to this editor.</param>
    /// <returns>Nearest caret index.</returns>
    private int HitTestCaret(System.Numerics.Vector2 position)
    {
        var lineStart = _acceptsReturn ? GetLineStartByVisibleOffset(position.Y) : _textWindowStart;
        var lineEnd = _acceptsReturn ? GetLineEnd(lineStart) : _text.Length;
        var targetX = Math.Clamp(position.X - Padding.Left, 0f, ContentWidth);
        var displayText = GetDisplayText();
        return lineStart + TextLayout.HitTestCaret(
            displayText.AsSpan(lineStart, lineEnd - lineStart), _theme.FontSize, targetX,
            FlowDirection.ToTextFlowDirection());
    }

    /// <summary>Measures text using the inherited paragraph direction.</summary>
    /// <param name="text">Text to measure.</param>
    /// <returns>Horizontal advance.</returns>
    private float MeasureTextWidth(ReadOnlySpan<char> text) =>
        TextLayout.MeasureWidth(text, _theme.FontSize, FlowDirection.ToTextFlowDirection());

    /// <summary>Maps a logical caret to a visual horizontal position.</summary>
    /// <param name="text">Text containing the caret.</param>
    /// <param name="caretIndex">UTF-16 caret index.</param>
    /// <returns>Visual horizontal position.</returns>
    private float GetCaretPosition(ReadOnlySpan<char> text, int caretIndex) =>
        TextLayout.GetCaretPosition(
            text, _theme.FontSize, caretIndex, FlowDirection.ToTextFlowDirection());

    /// <summary>Resolves a logical selection into bidi-aware visual ranges.</summary>
    /// <param name="text">Text containing the selection.</param>
    /// <param name="selectionStart">Logical UTF-16 selection start.</param>
    /// <param name="selectionLength">Logical UTF-16 selection length.</param>
    /// <returns>Visual ranges ordered from left to right.</returns>
    private TextSelectionRange[] GetSelectionRanges(
        ReadOnlySpan<char> text,
        int selectionStart,
        int selectionLength) =>
        TextLayout.GetSelectionRanges(
            text,
            _theme.FontSize,
            selectionStart,
            selectionLength,
            FlowDirection.ToTextFlowDirection());

    /// <summary>Finds the logical line start at a visible vertical offset.</summary>
    /// <param name="localY">Vertical position relative to the editor.</param>
    /// <returns>Logical line start index.</returns>
    private int GetLineStartByVisibleOffset(float localY)
    {
        var visibleLine = Math.Clamp((int)MathF.Floor(
            MathF.Max(0f, localY - Padding.Top) / GetLineHeight()),
            0, GetVisibleLineCount() - 1);
        var targetLine = _firstVisibleLine + visibleLine;
        var line = 0;
        var start = 0;
        while (line < targetLine)
        {
            var end = GetLineEnd(start);
            if (end == _text.Length)
                return start;
            start = end + 1;
            line++;
        }
        return start;
    }

    /// <summary>Scrolls a multiline selection when the captured pointer leaves the vertical content area.</summary>
    /// <param name="position">Pointer position relative to the editor.</param>
    private void AutoScrollSelection(System.Numerics.Vector2 position)
    {
        if (!_acceptsReturn)
        {
            if (position.X < Padding.Left && _textWindowStart > 0)
                _textWindowStart = FindPreviousTextElement(_textWindowStart);
            else if (position.X > Width - Padding.Right && _textWindowStart < _text.Length)
                _textWindowStart = FindNextTextElement(_textWindowStart);
            return;
        }
        if (position.Y < Padding.Top)
            _firstVisibleLine = Math.Max(0, _firstVisibleLine - 1);
        else if (position.Y > Height - Padding.Bottom)
            _firstVisibleLine = Math.Min(GetMaximumFirstVisibleLine(), _firstVisibleLine + 1);
    }

    /// <summary>Inserts indentation or removes it from each selected logical line.</summary>
    /// <param name="remove">True for Shift+Tab unindentation.</param>
    private void ChangeIndentation(bool remove)
    {
        var indentation = IndentationText ?? string.Empty;
        if (indentation.Length == 0)
            return;
        if (_selectionLength == 0 && !remove)
        {
            InsertText(indentation, EditKind.Other);
            return;
        }
        if (_selectionLength == 0)
        {
            var lineStart = GetLineStart(_caretIndex);
            var removeCount = GetIndentationRemovalLength(lineStart, indentation);
            if (removeCount == 0)
                return;
            RecordEdit(EditKind.Other);
            _text = _text.Remove(lineStart, removeCount);
            _caretIndex = Math.Max(lineStart, _caretIndex - removeCount);
            ClearSelection();
            NotifyTextChanged();
            return;
        }

        var rangeStart = GetLineStart(_selectionStart);
        var selectionEnd = _selectionStart + _selectionLength;
        var rangeEnd = selectionEnd > rangeStart && selectionEnd <= _text.Length &&
            _text[selectionEnd - 1] == '\n' ? selectionEnd - 1 : selectionEnd;
        var lineStarts = new List<int>();
        var start = rangeStart;
        while (start <= rangeEnd)
        {
            lineStarts.Add(start);
            var end = GetLineEnd(start);
            if (end >= _text.Length || end + 1 > rangeEnd)
                break;
            start = end + 1;
        }

        RecordEdit(EditKind.Other);
        var changed = 0;
        for (var lineIndex = lineStarts.Count - 1; lineIndex >= 0; lineIndex--)
        {
            var lineStart = lineStarts[lineIndex];
            if (!remove)
            {
                _text = _text.Insert(lineStart, indentation);
                changed += indentation.Length;
                continue;
            }
            var removeCount = GetIndentationRemovalLength(lineStart, indentation);
            if (removeCount > 0)
            {
                _text = _text.Remove(lineStart, removeCount);
                changed -= removeCount;
            }
        }
        _selectionAnchor = rangeStart;
        _selectionStart = rangeStart;
        _selectionLength = Math.Max(0, selectionEnd - rangeStart + changed);
        _caretIndex = _selectionStart + _selectionLength;
        EnsureCaretLineVisible();
        NotifyTextChanged();
    }

    /// <summary>Gets removable indentation at one logical line start.</summary>
    /// <param name="lineStart">Logical line start.</param>
    /// <param name="indentation">Configured indentation text.</param>
    /// <returns>Number of UTF-16 units to remove.</returns>
    private int GetIndentationRemovalLength(int lineStart, string indentation)
    {
        if (lineStart < _text.Length && _text[lineStart] == '\t')
            return 1;
        var available = Math.Min(indentation.Length, _text.Length - lineStart);
        if (available == indentation.Length &&
            string.CompareOrdinal(_text, lineStart, indentation, 0, indentation.Length) == 0)
            return indentation.Length;
        var spaces = 0;
        while (spaces < available && _text[lineStart + spaces] == ' ')
            spaces++;
        return spaces;
    }

    /// <summary>Gets the greatest valid first visible logical line.</summary>
    /// <returns>Maximum vertical line offset.</returns>
    private int GetMaximumFirstVisibleLine()
    {
        var lineCount = 1;
        for (var index = 0; index < _text.Length; index++)
        {
            if (_text[index] == '\n')
                lineCount++;
        }
        return Math.Max(0, lineCount - GetVisibleLineCount());
    }

    /// <summary>Finds the preceding Unicode text-element boundary.</summary>
    /// <param name="index">Current UTF-16 index.</param>
    /// <returns>Previous grapheme boundary or zero.</returns>
    private int FindPreviousTextElement(int index)
    {
        for (var boundaryIndex = _textElementBoundaries.Length - 1; boundaryIndex >= 0; boundaryIndex--)
        {
            if (_textElementBoundaries[boundaryIndex] < index)
                return _textElementBoundaries[boundaryIndex];
        }
        return 0;
    }

    /// <summary>Finds the following Unicode text-element boundary.</summary>
    /// <param name="index">Current UTF-16 index.</param>
    /// <returns>Next grapheme boundary or buffer length.</returns>
    private int FindNextTextElement(int index)
    {
        for (var boundaryIndex = 0; boundaryIndex < _textElementBoundaries.Length; boundaryIndex++)
        {
            if (_textElementBoundaries[boundaryIndex] > index)
                return _textElementBoundaries[boundaryIndex];
        }
        return _text.Length;
    }

    /// <summary>Finds the beginning of the preceding word run.</summary>
    /// <param name="index">Current UTF-16 index.</param>
    /// <returns>Previous word boundary.</returns>
    private int FindPreviousWordBoundary(int index)
    {
        var position = Math.Clamp(index, 0, _text.Length);
        while (position > 0)
        {
            var previous = FindPreviousTextElement(position);
            if (IsWordElementAt(previous))
                break;
            position = previous;
        }
        while (position > 0)
        {
            var previous = FindPreviousTextElement(position);
            if (!IsWordElementAt(previous))
                break;
            position = previous;
        }
        return position;
    }

    /// <summary>Finds the beginning of the following word run.</summary>
    /// <param name="index">Current UTF-16 index.</param>
    /// <returns>Next word boundary.</returns>
    private int FindNextWordBoundary(int index)
    {
        var position = Math.Clamp(index, 0, _text.Length);
        while (position < _text.Length && IsWordElementAt(position))
            position = FindNextTextElement(position);
        while (position < _text.Length && !IsWordElementAt(position))
            position = FindNextTextElement(position);
        return position;
    }

    /// <summary>Selects the word or separator run containing a caret index.</summary>
    /// <param name="index">Hit-tested UTF-16 caret index.</param>
    private void SelectWordAt(int index)
    {
        if (_text.Length == 0)
            return;
        var start = FindTextElementStart(Math.Min(index, _text.Length - 1));
        var word = IsWordElementAt(start);
        while (start > 0)
        {
            var previous = FindPreviousTextElement(start);
            if (IsWordElementAt(previous) != word)
                break;
            start = previous;
        }
        var end = FindNextTextElement(start);
        while (end < _text.Length && IsWordElementAt(end) == word)
            end = FindNextTextElement(end);
        _selectionAnchor = start;
        _selectionStart = start;
        _selectionLength = end - start;
        _caretIndex = end;
        BreakHistoryGroup();
        EnsureCaretLineVisible();
        InvalidateVisual();
    }

    /// <summary>Selects the complete logical line containing a caret index.</summary>
    /// <param name="index">Hit-tested UTF-16 caret index.</param>
    private void SelectLineAt(int index)
    {
        var start = GetLineStart(index);
        var end = GetLineEnd(start);
        if (end < _text.Length)
            end++;
        _selectionAnchor = start;
        _selectionStart = start;
        _selectionLength = end - start;
        _caretIndex = end;
        _preferredCaretColumn = -1;
        BreakHistoryGroup();
        EnsureCaretLineVisible();
        InvalidateVisual();
    }

    /// <summary>Finds the grapheme start containing one UTF-16 index.</summary>
    /// <param name="index">UTF-16 index within the stored text.</param>
    /// <returns>The containing text-element boundary.</returns>
    private int FindTextElementStart(int index)
    {
        for (var boundaryIndex = _textElementBoundaries.Length - 1;
            boundaryIndex >= 0;
            boundaryIndex--)
        {
            if (_textElementBoundaries[boundaryIndex] <= index)
                return _textElementBoundaries[boundaryIndex];
        }
        return 0;
    }

    /// <summary>Checks whether a grapheme begins with an identifier-like Unicode scalar.</summary>
    /// <param name="index">UTF-16 text-element boundary.</param>
    /// <returns>True for letters, digits, and underscore.</returns>
    private bool IsWordElementAt(int index)
    {
        if ((uint)index >= (uint)_text.Length)
            return false;
        Rune.DecodeFromUtf16(_text.AsSpan(index), out var rune, out _);
        return Rune.IsLetterOrDigit(rune) || rune.Value == '_';
    }

    /// <summary>Moves the caret and clears selection.</summary>
    /// <param name="index">New UTF-16 index.</param>
    /// <param name="extendSelection">Whether to extend the current selection to the new caret.</param>
    /// <param name="preservePreferredColumn">Whether to retain the preferred column for vertical movement.</param>
    private void MoveCaret(
        int index,
        bool extendSelection = false,
        bool preservePreferredColumn = false)
    {
        BreakHistoryGroup();
        if (!preservePreferredColumn)
            _preferredCaretColumn = -1;
        _caretIndex = Math.Clamp(index, 0, _text.Length);
        if (extendSelection)
        {
            _selectionStart = Math.Min(_selectionAnchor, _caretIndex);
            _selectionLength = Math.Abs(_caretIndex - _selectionAnchor);
        }
        else
        {
            _selectionAnchor = _caretIndex;
            ClearSelection();
        }
        EnsureCaretLineVisible();
        InvalidateVisual();
    }

    /// <summary>Moves the caret to the nearest column on an adjacent logical line.</summary>
    /// <param name="direction">Negative for the previous line; positive for the next line.</param>
    /// <param name="extendSelection">Whether to extend the current selection to the new caret.</param>
    private void MoveCaretVertically(int direction, bool extendSelection)
    {
        var currentStart = GetLineStart(_caretIndex);
        if (_preferredCaretColumn < 0)
            _preferredCaretColumn = GetTextElementColumn(currentStart, _caretIndex);
        if (direction < 0)
        {
            if (currentStart == 0)
                return;
            var targetStart = GetLineStart(currentStart - 1);
            MoveCaret(GetIndexAtTextElementColumn(targetStart, _preferredCaretColumn),
                extendSelection, true);
            return;
        }
        var currentEnd = GetLineEnd(currentStart);
        if (currentEnd == _text.Length)
            return;
        var nextStart = currentEnd + 1;
        MoveCaret(GetIndexAtTextElementColumn(nextStart, _preferredCaretColumn),
            extendSelection, true);
    }

    /// <summary>Moves the caret by one visible page while retaining its preferred column.</summary>
    /// <param name="direction">Negative for PageUp; positive for PageDown.</param>
    /// <param name="extendSelection">Whether to extend from the stable selection anchor.</param>
    private void MoveCaretByPage(int direction, bool extendSelection)
    {
        var count = GetVisibleLineCount();
        for (var line = 0; line < count; line++)
            MoveCaretVertically(direction, extendSelection);
    }

    /// <summary>Counts grapheme columns from a logical line start to an index.</summary>
    /// <param name="lineStart">Logical line start.</param>
    /// <param name="index">Caret index on that line.</param>
    /// <returns>Zero-based grapheme column.</returns>
    private int GetTextElementColumn(int lineStart, int index)
    {
        var column = 0;
        for (var boundaryIndex = 0; boundaryIndex < _textElementBoundaries.Length; boundaryIndex++)
        {
            var boundary = _textElementBoundaries[boundaryIndex];
            if (boundary < lineStart)
                continue;
            if (boundary >= index)
                break;
            column++;
        }
        return column;
    }

    /// <summary>Finds the nearest caret index at a grapheme column on a logical line.</summary>
    /// <param name="lineStart">Logical line start.</param>
    /// <param name="column">Preferred grapheme column.</param>
    /// <returns>Caret index clamped to the line end.</returns>
    private int GetIndexAtTextElementColumn(int lineStart, int column)
    {
        var lineEnd = GetLineEnd(lineStart);
        var currentColumn = 0;
        var index = lineStart;
        while (index < lineEnd && currentColumn < column)
        {
            index = FindNextTextElement(index);
            currentColumn++;
        }
        return Math.Min(index, lineEnd);
    }

    /// <summary>Returns the start of the logical line containing an index.</summary>
    /// <param name="index">UTF-16 buffer index.</param>
    /// <returns>The first index after the preceding newline.</returns>
    private int GetLineStart(int index)
    {
        var position = Math.Clamp(index, 0, _text.Length);
        while (position > 0 && _text[position - 1] != '\n')
            position--;
        return position;
    }

    /// <summary>Returns the end of the logical line starting at an index.</summary>
    /// <param name="start">Logical line start.</param>
    /// <returns>The newline index or buffer length.</returns>
    private int GetLineEnd(int start)
    {
        var position = Math.Clamp(start, 0, _text.Length);
        while (position < _text.Length && _text[position] != '\n')
            position++;
        return position;
    }

    /// <summary>Gets the zero-based logical line containing the caret.</summary>
    /// <returns>The caret line index.</returns>
    private int GetCaretLineIndex()
    {
        var line = 0;
        for (var index = 0; index < _caretIndex; index++)
        {
            if (_text[index] == '\n')
                line++;
        }
        return line;
    }

    /// <summary>Keeps the caret line within the visible vertical text area.</summary>
    private void EnsureCaretLineVisible()
    {
        if (!_acceptsReturn)
            return;
        var visibleLines = GetVisibleLineCount();
        var caretLine = GetCaretLineIndex();
        if (caretLine < _firstVisibleLine)
            _firstVisibleLine = caretLine;
        else if (caretLine >= _firstVisibleLine + visibleLines)
            _firstVisibleLine = caretLine - visibleLines + 1;
    }

    /// <summary>Gets the logical height occupied by one text line.</summary>
    /// <returns>Line height in logical pixels.</returns>
    private float GetLineHeight() => _theme.FontSize + 2f;

    /// <summary>Gets the number of complete logical lines visible in the text area.</summary>
    /// <returns>At least one visible line.</returns>
    private int GetVisibleLineCount() => Math.Max(1, (int)MathF.Floor(
        ContentHeight / GetLineHeight()));

    /// <summary>Selects the complete text buffer.</summary>
    private void SelectAll()
    {
        BreakHistoryGroup();
        _preferredCaretColumn = -1;
        _selectionStart = 0;
        _selectionLength = _text.Length;
        _selectionAnchor = 0;
        _caretIndex = _text.Length;
        EnsureCaretLineVisible();
        InvalidateVisual();
    }

    /// <summary>Adds copy, cut, and paste gestures for one platform modifier.</summary>
    /// <param name="modifier">Control or Super modifier.</param>
    private void AddClipboardGestures(InputModifiers modifier)
    {
        KeyBindings.Add(new UIKeyBinding(new UIKeyGesture(InputKey.C, modifier), UIEditingCommands.Copy));
        KeyBindings.Add(new UIKeyBinding(new UIKeyGesture(InputKey.X, modifier), UIEditingCommands.Cut));
        KeyBindings.Add(new UIKeyBinding(new UIKeyGesture(InputKey.V, modifier), UIEditingCommands.Paste));
    }

    /// <summary>Adds undo and redo gestures for one platform modifier.</summary>
    /// <param name="modifier">Control or Super modifier.</param>
    private void AddHistoryGestures(InputModifiers modifier)
    {
        KeyBindings.Add(new UIKeyBinding(new UIKeyGesture(InputKey.Z, modifier), UIEditingCommands.Undo));
        KeyBindings.Add(new UIKeyBinding(new UIKeyGesture(InputKey.Y, modifier), UIEditingCommands.Redo));
        KeyBindings.Add(new UIKeyBinding(
            new UIKeyGesture(InputKey.Z, modifier | InputModifiers.Shift), UIEditingCommands.Redo));
    }

    /// <summary>Copies the selected range to the command's host clipboard.</summary>
    /// <param name="args">Routed command data.</param>
    private void CopySelection(UICommandEventArgs args)
    {
        if (args.Clipboard is not null && _selectionLength > 0)
            args.Clipboard.SetText(_text.Substring(_selectionStart, _selectionLength));
    }

    /// <summary>Copies and removes the selected editable range.</summary>
    /// <param name="args">Routed command data.</param>
    private void CutSelection(UICommandEventArgs args)
    {
        if (IsReadOnly || args.Clipboard is null || _selectionLength <= 0)
            return;
        CopySelection(args);
        RecordEdit(EditKind.Other);
        DeleteSelection();
        NotifyTextChanged();
    }

    /// <summary>Replaces the current selection with clipboard text under this editor's newline policy.</summary>
    /// <param name="args">Routed command data.</param>
    private void PasteClipboard(UICommandEventArgs args)
    {
        if (IsReadOnly || args.Clipboard?.GetText() is not { Length: > 0 } clipboardText)
            return;
        var normalized = _acceptsReturn
            ? clipboardText.Replace("\r\n", "\n").Replace('\r', '\n')
            : clipboardText.Replace('\r', ' ').Replace('\n', ' ');
        InsertText(normalized, EditKind.Other);
    }

    /// <summary>Inserts text at the selection or caret.</summary>
    /// <param name="value">Text to insert.</param>
    /// <param name="editKind">History category used to coalesce compatible edits.</param>
    private void InsertText(string value, EditKind editKind)
    {
        value = ApplyInputPolicy(value);
        if (value.Length == 0)
            return;
        RecordEdit(_selectionLength > 0 ? EditKind.Other : editKind);
        DeleteSelection();
        _text = _text.Insert(_caretIndex, value);
        _caretIndex += value.Length;
        ClearSelection();
        EnsureCaretLineVisible();
        NotifyTextChanged();
    }

    /// <summary>Checks whether backward deletion can change the text.</summary>
    /// <returns>True when the editor is editable and has preceding content.</returns>
    private bool CanDeleteBackward() => !IsReadOnly && (_selectionLength > 0 || _caretIndex > 0);

    /// <summary>Checks whether forward deletion can change the text.</summary>
    /// <returns>True when the editor is editable and has following content.</returns>
    private bool CanDeleteForward() => !IsReadOnly && (_selectionLength > 0 || _caretIndex < _text.Length);

    /// <summary>Deletes the selection or character preceding the caret.</summary>
    private void DeleteBackward()
    {
        RecordEdit(_selectionLength > 0 ? EditKind.Other : EditKind.DeleteBackward);
        if (!DeleteSelection())
        {
            var previous = FindPreviousTextElement(_caretIndex);
            _text = _text.Remove(previous, _caretIndex - previous);
            _caretIndex = previous;
        }
        EnsureCaretLineVisible();
        NotifyTextChanged();
    }

    /// <summary>Deletes the selection or character following the caret.</summary>
    private void DeleteForward()
    {
        RecordEdit(_selectionLength > 0 ? EditKind.Other : EditKind.DeleteForward);
        if (!DeleteSelection())
        {
            var next = FindNextTextElement(_caretIndex);
            _text = _text.Remove(_caretIndex, next - _caretIndex);
        }
        EnsureCaretLineVisible();
        NotifyTextChanged();
    }

    /// <summary>Deletes the selected range and places the caret at its start.</summary>
    /// <returns>True when a selection was deleted.</returns>
    private bool DeleteSelection()
    {
        if (_selectionLength == 0)
            return false;
        _text = _text.Remove(_selectionStart, _selectionLength);
        _caretIndex = _selectionStart;
        ClearSelection();
        return true;
    }

    /// <summary>Clears the current text selection.</summary>
    private void ClearSelection()
    {
        _selectionStart = _caretIndex;
        _selectionLength = 0;
        _selectionAnchor = _caretIndex;
    }

    /// <summary>Stores the current state as the predecessor of one text mutation.</summary>
    private void RecordEdit(EditKind editKind)
    {
        if (_historyCapacity == 0)
        {
            _undoStates.Clear();
            _redoStates.Clear();
            _coalescingEdit = EditKind.None;
            return;
        }
        if (editKind != EditKind.Other && _coalescingEdit == editKind)
            return;
        _undoStates.Add(CaptureState());
        TrimHistory(_undoStates);
        _redoStates.Clear();
        _coalescingEdit = editKind;
    }

    /// <summary>Ends the current typing or deletion undo group.</summary>
    private void BreakHistoryGroup() => _coalescingEdit = EditKind.None;

    /// <summary>Restores the latest predecessor state.</summary>
    private void Undo()
    {
        if (_undoStates.Count == 0)
            return;
        BreakHistoryGroup();
        _redoStates.Add(CaptureState());
        TrimHistory(_redoStates);
        RestoreState(PopState(_undoStates));
    }

    /// <summary>Reapplies the latest state removed by undo.</summary>
    private void Redo()
    {
        if (_redoStates.Count == 0)
            return;
        BreakHistoryGroup();
        _undoStates.Add(CaptureState());
        TrimHistory(_undoStates);
        RestoreState(PopState(_redoStates));
    }

    /// <summary>Removes oldest states until a history list satisfies the configured capacity.</summary>
    /// <param name="states">Undo or redo history list.</param>
    private void TrimHistory(List<EditState> states)
    {
        var removeCount = states.Count - _historyCapacity;
        if (removeCount > 0)
            states.RemoveRange(0, removeCount);
    }

    /// <summary>Captures text and selection state.</summary>
    /// <returns>Immutable editor state.</returns>
    private EditState CaptureState() =>
        new(_text, _caretIndex, _selectionStart, _selectionLength, _selectionAnchor);

    /// <summary>Removes and returns the final state in a history list.</summary>
    /// <param name="states">History list.</param>
    /// <returns>Removed final state.</returns>
    private static EditState PopState(List<EditState> states)
    {
        var index = states.Count - 1;
        var state = states[index];
        states.RemoveAt(index);
        return state;
    }

    /// <summary>Restores an immutable editor state and reports the text change.</summary>
    /// <param name="state">State to restore.</param>
    private void RestoreState(EditState state)
    {
        _text = state.Text;
        _caretIndex = state.CaretIndex;
        _selectionStart = state.SelectionStart;
        _selectionLength = state.SelectionLength;
        _selectionAnchor = state.SelectionAnchor;
        EnsureCaretLineVisible();
        NotifyTextChanged();
    }

    /// <summary>Invalidates visuals and reports one completed text mutation.</summary>
    private void NotifyTextChanged()
    {
        _preferredCaretColumn = -1;
        RebuildTextElementBoundaries();
        RebuildMaskedText();
        _externalValidationMessage = null;
        CancelPendingValidation();
        _asyncValidationMessage = null;
        Validate();
        InvalidateVisual();
        TextChanged?.Invoke(_text);
        if (UpdateTrigger == TextUpdateTrigger.TextChanged)
            ValueUpdateRequested?.Invoke(_text);
        ScheduleAsyncValidation();
    }

    /// <summary>Rebuilds Unicode text-element boundaries after a buffer mutation.</summary>
    private void RebuildTextElementBoundaries()
    {
        _textElementBoundaries = _text.Length == 0
            ? []
            : StringInfo.ParseCombiningCharacters(_text);
    }

    /// <summary>Rebuilds the index-preserving password display buffer.</summary>
    private void RebuildMaskedText()
    {
        if (_text.Length == 0)
        {
            _maskedText = string.Empty;
            return;
        }
        var characters = new char[_text.Length];
        for (var index = 0; index < _text.Length; index++)
            characters[index] = _text[index] == '\n' ? '\n' : _passwordCharacter;
        _maskedText = new string(characters);
    }

    /// <summary>Filters and truncates incoming text according to user-input policy.</summary>
    /// <param name="value">Candidate typed, pasted, or indentation text.</param>
    /// <returns>Accepted text, possibly empty.</returns>
    private string ApplyInputPolicy(string value)
    {
        if (value.Length == 0)
            return string.Empty;
        var filtered = value;
        if (InputFilter is { } filter)
        {
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (var rune in value.EnumerateRunes())
            {
                if (filter(rune))
                    builder.Append(rune);
            }
            filtered = builder.ToString();
        }
        if (filtered.Length == 0 || _maxLength == int.MaxValue)
            return filtered;
        var selectedElements = _selectionLength == 0
            ? 0
            : StringInfo.ParseCombiningCharacters(
                _text.Substring(_selectionStart, _selectionLength)).Length;
        var available = Math.Max(0, _maxLength - _textElementBoundaries.Length + selectedElements);
        if (available == 0)
            return string.Empty;
        var boundaries = StringInfo.ParseCombiningCharacters(filtered);
        if (boundaries.Length <= available)
            return filtered;
        return filtered.Substring(0, boundaries[available]);
    }

    /// <summary>Resolves the stored or masked buffer under the active password reveal policy.</summary>
    /// <returns>Text safe to submit to rendering.</returns>
    private string GetDisplayText()
    {
        if (!IsPassword || _passwordRevealMode == Engine.UI.PasswordRevealMode.Always ||
            _passwordRevealMode == Engine.UI.PasswordRevealMode.WhileFocused && IsFocused)
            return _text;
        return _maskedText;
    }

    /// <summary>Reevaluates the configured validator and publishes a changed result.</summary>
    public void Validate()
    {
        var message = _validator?.Invoke(_text);
        if (string.IsNullOrEmpty(message))
            message = null;
        _validatorMessage = message;
        PublishValidationResult();
    }

    /// <summary>Sets a validation result supplied by an external form or asynchronous operation.</summary>
    /// <param name="message">Error message, or null to clear the external result.</param>
    public void SetValidationError(string? message)
    {
        if (string.IsNullOrEmpty(message))
            message = null;
        if (_externalValidationMessage == message)
            return;
        _externalValidationMessage = message;
        PublishValidationResult();
    }

    /// <summary>Publishes the effective external, asynchronous, or synchronous validator result.</summary>
    private void PublishValidationResult()
    {
        var message = _externalValidationMessage ?? _asyncValidationMessage ?? _validatorMessage;
        if (_validationMessage == message)
            return;
        _validationMessage = message;
        InvalidateVisual();
        ValidationChanged?.Invoke(message);
        ValidationStateChanged?.Invoke();
    }

    /// <summary>Commits pending text when current validation succeeds.</summary>
    /// <returns>True when the text is valid and is now committed.</returns>
    public bool CommitEdit()
    {
        Validate();
        if (HasValidationError || IsValidationPending)
            return false;
        if (!IsDirty)
            return true;
        _committedText = _text;
        _undoStates.Clear();
        _redoStates.Clear();
        BreakHistoryGroup();
        if (UpdateTrigger is TextUpdateTrigger.Commit or TextUpdateTrigger.LostFocus)
            ValueUpdateRequested?.Invoke(_text);
        EditCommitted?.Invoke(_text);
        return true;
    }

    /// <summary>Restores pending text to the value captured at focus or the latest commit.</summary>
    public void CancelEdit()
    {
        if (!IsDirty)
            return;
        _text = _committedText;
        _caretIndex = _text.Length;
        ClearSelection();
        _undoStates.Clear();
        _redoStates.Clear();
        BreakHistoryGroup();
        NotifyTextChanged();
        EditCanceled?.Invoke();
    }

    /// <summary>Validates and explicitly requests a model update without changing trigger policy.</summary>
    /// <returns>True when validation succeeds and the update was requested.</returns>
    public bool RequestValueUpdate()
    {
        Validate();
        if (HasValidationError || IsValidationPending)
            return false;
        ValueUpdateRequested?.Invoke(_text);
        return true;
    }

    /// <summary>Runs cancellable asynchronous validation for the current text generation.</summary>
    /// <param name="cancellationToken">External cancellation request.</param>
    /// <returns>True when current text completes without an error.</returns>
    public async ValueTask<bool> ValidateAsync(CancellationToken cancellationToken = default)
    {
        Validate();
        if (HasValidationError || _asyncValidator is not { } validator)
            return !HasValidationError;
        CancelPendingValidation();
        var generation = ++_validationGeneration;
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var dispatcher = Dispatcher;
        _validationCancellation = source;
        _isValidationPending = true;
        InvalidateVisual();
        ValidationStateChanged?.Invoke();
        string? message;
        try
        {
            message = await validator(_text, source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            var canceled = await CompleteValidationAsync(
                dispatcher, source, generation, null, true, cancellationToken).ConfigureAwait(false);
            source.Dispose();
            return canceled;
        }
        catch (Exception)
        {
            var failed = await CompleteValidationAsync(
                dispatcher, source, generation, AsyncValidationFailureMessage,
                false, cancellationToken).ConfigureAwait(false);
            source.Dispose();
            return failed;
        }
        var result = await CompleteValidationAsync(
            dispatcher, source, generation, message, false, cancellationToken).ConfigureAwait(false);
        source.Dispose();
        return result;
    }

    /// <summary>Marshals one asynchronous validation completion to the owning UI dispatcher.</summary>
    /// <param name="dispatcher">Dispatcher captured before validation began.</param>
    /// <param name="source">Validation cancellation source.</param>
    /// <param name="generation">Text generation being validated.</param>
    /// <param name="message">Completed validation message.</param>
    /// <param name="wasCanceled">Whether the validator observed cancellation.</param>
    /// <param name="externalCancellation">External cancellation token.</param>
    /// <returns>True when the current generation completed valid.</returns>
    private Task<bool> CompleteValidationAsync(
        UIDispatcher? dispatcher,
        CancellationTokenSource source,
        int generation,
        string? message,
        bool wasCanceled,
        CancellationToken externalCancellation)
    {
        if (dispatcher is null || dispatcher.CheckAccess())
            return Task.FromResult(ApplyValidationCompletion(
                source, generation, message, wasCanceled, externalCancellation));
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            dispatcher.Post(() =>
            {
                try
                {
                    completion.SetResult(ApplyValidationCompletion(
                        source, generation, message, wasCanceled, externalCancellation));
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            });
        }
        catch (ObjectDisposedException)
        {
            completion.SetResult(false);
        }
        return completion.Task;
    }

    /// <summary>Applies validation completion state on the owning UI thread.</summary>
    /// <param name="source">Validation cancellation source.</param>
    /// <param name="generation">Text generation being validated.</param>
    /// <param name="message">Completed validation message.</param>
    /// <param name="wasCanceled">Whether the validator observed cancellation.</param>
    /// <param name="externalCancellation">External cancellation token.</param>
    /// <returns>True when the current generation completed valid.</returns>
    private bool ApplyValidationCompletion(
        CancellationTokenSource source,
        int generation,
        string? message,
        bool wasCanceled,
        CancellationToken externalCancellation)
    {
        if (!ReferenceEquals(_validationCancellation, source) || generation != _validationGeneration)
            return false;
        _validationCancellation = null;
        _isValidationPending = false;
        if (wasCanceled || externalCancellation.IsCancellationRequested)
        {
            InvalidateVisual();
            ValidationStateChanged?.Invoke();
            return false;
        }
        _asyncValidationMessage = string.IsNullOrEmpty(message) ? null : message;
        InvalidateVisual();
        ValidationStateChanged?.Invoke();
        PublishValidationResult();
        return _asyncValidationMessage is null;
    }

    /// <summary>Cancels pending asynchronous validation and advances the text generation.</summary>
    private void CancelPendingValidation()
    {
        _validationGeneration++;
        _validationCancellation?.Cancel();
        _validationCancellation = null;
        if (!_isValidationPending)
            return;
        _isValidationPending = false;
        InvalidateVisual();
        ValidationStateChanged?.Invoke();
    }

    /// <summary>Schedules automatic validation after the configured host-aware debounce delay.</summary>
    private void ScheduleAsyncValidation()
    {
        _validationDebounceCancellation?.Cancel();
        _validationDebounceCancellation?.Dispose();
        _validationDebounceCancellation = null;
        if (_asyncValidator is null || _asyncValidationDelay == Timeout.InfiniteTimeSpan ||
            Dispatcher is not { } dispatcher)
            return;
        var source = new CancellationTokenSource();
        _validationDebounceCancellation = source;
        _ = RunDebouncedValidationAsync(dispatcher, source);
    }

    /// <summary>Waits for one debounce interval and starts validation on the owning UI thread.</summary>
    /// <param name="dispatcher">Owning host dispatcher.</param>
    /// <param name="source">Debounce cancellation source.</param>
    private async Task RunDebouncedValidationAsync(
        UIDispatcher dispatcher,
        CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(_asyncValidationDelay, source.Token).ConfigureAwait(false);
            dispatcher.Post(() =>
            {
                if (!ReferenceEquals(_validationDebounceCancellation, source) || source.IsCancellationRequested)
                    return;
                _validationDebounceCancellation = null;
                source.Dispose();
                _ = ValidateAsync();
            });
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Stores one reversible text editing state.</summary>
    /// <param name="Text">Text buffer.</param>
    /// <param name="CaretIndex">Caret index.</param>
    /// <param name="SelectionStart">Selection start.</param>
    /// <param name="SelectionLength">Selection length.</param>
    /// <param name="SelectionAnchor">Selection anchor.</param>
    private readonly record struct EditState(
        string Text,
        int CaretIndex,
        int SelectionStart,
        int SelectionLength,
        int SelectionAnchor);

    /// <summary>Identifies mutations eligible for adjacent undo coalescing.</summary>
    private enum EditKind
    {
        None,
        Typing,
        DeleteBackward,
        DeleteForward,
        Other
    }
}
