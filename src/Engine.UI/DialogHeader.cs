namespace Engine.UI;

/// <summary>
/// Standard title and optional subtitle region for modal dialogs.
/// </summary>
public sealed class DialogHeader : Panel
{
    private readonly Label _title;
    private readonly Label? _subtitle;
    private readonly Separator _separator;

    /// <summary>Creates a container-arranged dialog header.</summary>
    /// <param name="title">Primary title.</param>
    /// <param name="subtitle">Optional secondary description.</param>
    /// <param name="theme">Theme supplying colors and typography.</param>
    public DialogHeader(string title, string subtitle = "", UITheme? theme = null)
        : base((theme ?? UITheme.Dark).SurfaceRaised, 0f,
            string.IsNullOrWhiteSpace(subtitle) ? 48f : 66f)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        _title = new Label(title)
        {
            TextStyle = resolvedTheme.GetTextStyle(UITextRole.DialogTitle),
            Padding = Thickness.Zero
        };
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            _subtitle = new Label(subtitle)
            {
                TextStyle = resolvedTheme.GetTextStyle(UITextRole.SecondaryCaption),
                Padding = Thickness.Zero
            };
        }
        _separator = new Separator(0f, 1f, resolvedTheme);
        AddChild(_title);
        if (_subtitle is not null)
            AddChild(_subtitle);
        AddChild(_separator);
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(System.Numerics.Vector2 contentSize)
    {
        var width = MathF.Max(0f, contentSize.X - 32f);
        _title.Measure(new System.Numerics.Vector2(width, 28f));
        _title.Arrange(new System.Numerics.Vector2(16f, 8f), new System.Numerics.Vector2(width, 28f));
        if (_subtitle is not null)
        {
            _subtitle.Measure(new System.Numerics.Vector2(width, 22f));
            _subtitle.Arrange(new System.Numerics.Vector2(16f, 34f), new System.Numerics.Vector2(width, 22f));
        }
        _separator.Measure(new System.Numerics.Vector2(contentSize.X, 1f));
        _separator.Arrange(new System.Numerics.Vector2(0f, MathF.Max(0f, contentSize.Y - 1f)),
            new System.Numerics.Vector2(contentSize.X, 1f));
    }
}
