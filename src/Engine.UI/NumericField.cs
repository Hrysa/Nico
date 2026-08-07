using System.Globalization;
using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Edits a bounded numeric value through text, keyboard, and repeating step buttons.</summary>
public sealed class NumericField : UIElement
{
    private double _minimum = double.NegativeInfinity;
    private double _maximum = double.PositiveInfinity;
    private double _value;
    private string _formatString = "G";

    /// <summary>Gets the editable text field.</summary>
    public TextField TextField { get; }

    /// <summary>Gets the decrement repeat button.</summary>
    public RepeatButton DecrementButton { get; }

    /// <summary>Gets the increment repeat button.</summary>
    public RepeatButton IncrementButton { get; }

    /// <summary>Gets or sets the minimum value.</summary>
    public double Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            if (_maximum < _minimum)
                _maximum = _minimum;
            SetValue(_value);
        }
    }

    /// <summary>Gets or sets the maximum value.</summary>
    public double Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(Minimum, value);
            SetValue(_value);
        }
    }

    /// <summary>Gets or sets the current clamped value.</summary>
    public double Value
    {
        get => _value;
        set => SetValue(value);
    }

    /// <summary>Gets or sets the amount applied by each step action.</summary>
    public double Step { get; set; } = 1d;

    /// <summary>Gets or sets the invariant numeric format string.</summary>
    public string FormatString
    {
        get => _formatString;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _formatString = value;
            SynchronizeText();
        }
    }

    /// <summary>Occurs when the parsed or stepped value changes.</summary>
    public event Action<double>? ValueChanged;

    /// <summary>Creates a numeric field.</summary>
    /// <param name="width">Control width.</param>
    /// <param name="height">Control height.</param>
    /// <param name="theme">Theme supplying child visuals.</param>
    public NumericField(float width, float height, UITheme? theme = null) : base(width, height)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        var buttonWidth = MathF.Min(height, 22f);
        TextField = new TextField(MathF.Max(0f, width - buttonWidth * 2f), height, resolvedTheme)
        {
            UpdateTrigger = TextUpdateTrigger.Commit
        };
        DecrementButton = new RepeatButton(buttonWidth, height, "−", resolvedTheme);
        IncrementButton = new RepeatButton(buttonWidth, height, "+", resolvedTheme);
        TextField.Validator = ValidateText;
        TextField.ValueUpdateRequested += OnTextCommitted;
        TextField.Blur += OnTextEditBlur;
        TextField.Key += OnTextFieldKey;
        DecrementButton.Click += () => SetValue(Value - Step);
        IncrementButton.Click += () => SetValue(Value + Step);
        AddChild(TextField);
        AddChild(DecrementButton);
        AddChild(IncrementButton);
        SynchronizeText();
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        TextField.Measure(availableSize);
        DecrementButton.Measure(availableSize);
        IncrementButton.Measure(availableSize);
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var buttonWidth = MathF.Min(contentSize.Y, 22f);
        var textWidth = MathF.Max(0f, contentSize.X - buttonWidth * 2f);
        TextField.Arrange(Vector2.Zero, new Vector2(textWidth, contentSize.Y));
        DecrementButton.Arrange(new Vector2(textWidth, 0f), new Vector2(buttonWidth, contentSize.Y));
        IncrementButton.Arrange(new Vector2(textWidth + buttonWidth, 0f),
            new Vector2(buttonWidth, contentSize.Y));
    }

    /// <summary>Applies valid committed invariant text to the numeric value.</summary>
    /// <param name="text">Committed editor text.</param>
    private void OnTextCommitted(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            SetValue(value, synchronizeText: false);
    }

    /// <summary>Validates pending invariant numeric text.</summary>
    /// <param name="text">Pending editor text.</param>
    /// <returns>An error message, or null when parseable.</returns>
    private static string? ValidateText(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? null
            : "Enter a valid number.";

    /// <summary>Commits valid pending text on blur or restores the preceding committed value.</summary>
    private void OnTextEditBlur()
    {
        if (!TextField.CommitEdit())
            TextField.CancelEdit();
    }

    /// <summary>Applies Up and Down stepping before text-field compatibility handling.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="keyEvent">Routed key data.</param>
    private void OnTextFieldKey(UIElement sender, UIKeyEventArgs keyEvent)
    {
        if (keyEvent.RoutePhase != UIRoutePhase.Target || keyEvent.Kind != UIKeyEventKind.KeyDown)
            return;
        if (keyEvent.Key == InputKey.Up)
            SetValue(Value + Step);
        else if (keyEvent.Key == InputKey.Down)
            SetValue(Value - Step);
        else
            return;
        keyEvent.Handled = true;
    }

    /// <summary>Clamps, synchronizes, and reports a numeric value.</summary>
    /// <param name="value">Requested value.</param>
    /// <param name="synchronizeText">Whether to normalize the editor text.</param>
    private void SetValue(double value, bool synchronizeText = true)
    {
        var resolved = Math.Clamp(value, Minimum, Maximum);
        if (_value == resolved)
        {
            if (synchronizeText)
                SynchronizeText();
            return;
        }
        _value = resolved;
        if (synchronizeText)
            SynchronizeText();
        ValueChanged?.Invoke(_value);
    }

    /// <summary>Formats the current value into the text editor using invariant culture.</summary>
    private void SynchronizeText()
    {
        TextField.Text = Value.ToString(FormatString, CultureInfo.InvariantCulture);
        TextField.CommitEdit();
    }
}
