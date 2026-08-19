namespace Engine.UI;

/// <summary>Provides persistent selection state for button-derived item containers.</summary>
public abstract class SelectableButton : Button
{
    /// <summary>Gets or sets whether this item container is selected.</summary>
    public bool IsSelected
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            InvalidateVisual();
            OnIsSelectedChanged();
        }
    }

    /// <summary>Creates a fixed-size selectable button.</summary>
    /// <param name="width">Button width.</param>
    /// <param name="height">Button height.</param>
    /// <param name="theme">Theme supplying state colors.</param>
    protected SelectableButton(float width, float height, UITheme theme)
        : base(width, height, theme)
    {
    }

    /// <summary>Creates a fixed-size labeled selectable button.</summary>
    /// <param name="width">Button width.</param>
    /// <param name="height">Button height.</param>
    /// <param name="label">Button label.</param>
    /// <param name="theme">Theme supplying state colors.</param>
    /// <param name="style">Button emphasis.</param>
    protected SelectableButton(float width, float height, string label, UITheme theme,
        ButtonStyle style)
        : base(width, height, label, theme, style)
    {
    }

    /// <summary>Responds after the persistent selected state changes.</summary>
    protected virtual void OnIsSelectedChanged()
    {
    }

    /// <inheritdoc/>
    protected sealed override bool IsVisualStateSelected => IsSelected;
}
