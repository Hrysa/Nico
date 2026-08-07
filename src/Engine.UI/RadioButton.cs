namespace Engine.UI;

/// <summary>A sibling-scoped mutually exclusive toggle button.</summary>
public sealed class RadioButton : ToggleButton
{
    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => base.GetSemanticInfo() with
    {
        Role = UISemanticRole.RadioButton
    };

    /// <summary>Gets or sets the sibling group name.</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>Creates a radio button.</summary>
    /// <param name="width">Control width.</param>
    /// <param name="height">Control height.</param>
    /// <param name="label">Radio label.</param>
    /// <param name="theme">Theme supplying visual states.</param>
    public RadioButton(float width, float height, string label, UITheme? theme = null)
        : base(width, height, label, theme)
    {
    }

    /// <inheritdoc/>
    protected override void ApplyClickState()
    {
        if (!IsChecked)
            SelectWithinGroup();
    }

    /// <summary>Selects this button and clears checked siblings in the same named group.</summary>
    private void SelectWithinGroup()
    {
        if (Parent is not { } parent)
        {
            IsChecked = true;
            return;
        }
        var children = parent.Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is RadioButton radio &&
                string.Equals(radio.GroupName, GroupName, StringComparison.Ordinal))
                radio.IsChecked = ReferenceEquals(radio, this);
        }
    }

}
