namespace Engine.Graphics;

/// <summary>Configures creation of an engine window.</summary>
public struct WindowOptions
{
    /// <summary>Gets or sets the native window title.</summary>
    public string Title { get; set; }

    /// <summary>Gets or sets the initial client width.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the initial client height.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets whether native decorations are replaced by editor UI.</summary>
    public bool CustomTitleBar { get; set; }
}
