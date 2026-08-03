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

    /// <summary>Gets or sets whether updates and renders wait for native or requested events.</summary>
    public bool IsEventDriven { get; set; }

    /// <summary>Gets or sets the maximum update and render rate, or zero for unlimited.</summary>
    public double TargetFrameRate { get; set; }
}
