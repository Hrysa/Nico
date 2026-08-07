namespace Engine.UI;

/// <summary>Identifies when pending text requests an update to its bound model.</summary>
public enum TextUpdateTrigger
{
    /// <summary>Update whenever the text changes.</summary>
    TextChanged,
    /// <summary>Update when editing is committed.</summary>
    Commit,
    /// <summary>Update when keyboard focus is lost.</summary>
    LostFocus,
    /// <summary>Update only when requested explicitly.</summary>
    Explicit
}
