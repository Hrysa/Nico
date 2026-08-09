namespace Engine.Graphics;

/// <summary>Selects how completed window frames are queued for display.</summary>
public enum PresentationModePreference
{
    /// <summary>Uses guaranteed FIFO presentation with no tearing.</summary>
    Fifo,

    /// <summary>Prefers tear-free mailbox presentation so stale queued frames are replaced.</summary>
    LowLatency,

    /// <summary>Prefers unsynchronized immediate presentation, allowing visible tearing.</summary>
    Immediate
}

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

    /// <summary>Gets or sets the desired swapchain presentation latency policy.</summary>
    public PresentationModePreference PresentationMode { get; set; }

    /// <summary>
    /// Gets or sets the viewport render scale relative to physical framebuffer pixels.
    /// Zero selects the default scale of one.
    /// </summary>
    public float ViewportRenderScale { get; set; }

    /// <summary>
    /// Gets or sets requested viewport MSAA samples: 1, 2, 4, or 8.
    /// Zero selects the default of four.
    /// </summary>
    public int MsaaSamples { get; set; }
}
