using Engine.Graphics;

namespace Engine.UI;

/// <summary>A button with persistent checked state.</summary>
public class ToggleButton : Button
{
    private readonly UITheme _theme;
    private bool _isChecked;

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => base.GetSemanticInfo() with
    {
        Role = UISemanticRole.ToggleButton,
        Actions = UISemanticAction.Invoke | UISemanticAction.Toggle,
        IsChecked = IsChecked
    };

    /// <inheritdoc/>
    public override bool PerformSemanticAction(UISemanticAction action, double? value = null)
    {
        if (action == UISemanticAction.Toggle)
            action = UISemanticAction.Invoke;
        return base.PerformSemanticAction(action, value);
    }

    /// <summary>Gets or sets whether this button is checked.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            InvalidateVisual();
            CheckedChanged?.Invoke(_isChecked);
        }
    }

    /// <summary>Occurs when checked state changes.</summary>
    public event Action<bool>? CheckedChanged;

    /// <summary>Creates a themed toggle button.</summary>
    /// <param name="width">Button width.</param>
    /// <param name="height">Button height.</param>
    /// <param name="label">Button label.</param>
    /// <param name="theme">Theme supplying visual states.</param>
    /// <param name="style">Button emphasis and interaction treatment.</param>
    public ToggleButton(
        float width,
        float height,
        string label,
        UITheme? theme = null,
        ButtonStyle style = ButtonStyle.Subtle)
        : base(width, height, label, theme ?? UITheme.Dark, style)
    {
        _theme = theme ?? UITheme.Dark;
    }

    /// <inheritdoc/>
    protected override void OnClick()
    {
        ApplyClickState();
        base.OnClick();
    }

    /// <summary>Applies the state transition associated with one click.</summary>
    protected virtual void ApplyClickState()
    {
        IsChecked = !IsChecked;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        if (IsChecked && VisualStateMode == BoxVisualStateMode.Interactive)
            drawList.AddRoundedRectangle(Left, Top, Right, Bottom, CornerRadius, _theme.AccentPressed);
        base.Paint(drawList);
    }
}
