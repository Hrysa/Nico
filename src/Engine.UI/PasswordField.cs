namespace Engine.UI;

/// <summary>Identifies when a password field may reveal its stored text.</summary>
public enum PasswordRevealMode
{
    /// <summary>Always mask the password.</summary>
    Never,
    /// <summary>Reveal the password while the field has focus.</summary>
    WhileFocused,
    /// <summary>Always reveal the password.</summary>
    Always
}

/// <summary>A single-line text field whose displayed characters follow a password reveal policy.</summary>
public sealed class PasswordField : TextField
{
    /// <summary>Creates a masked single-line password field.</summary>
    /// <param name="width">Field width.</param>
    /// <param name="height">Field height.</param>
    /// <param name="theme">Theme supplying colors and typography.</param>
    public PasswordField(float width, float height, UITheme? theme = null)
        : base(width, height, theme)
    {
        IsPassword = true;
        PasswordRevealMode = PasswordRevealMode.Never;
    }
}
